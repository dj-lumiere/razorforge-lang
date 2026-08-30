using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Instantiation;
using Compiler.Tokenizer;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Global pass that folds compile-time-constant BuilderQuery per-type calls to literal
/// expressions, eliminating runtime function calls to synthesized stubs.
///
/// <para>Runs in three contexts:</para>
/// <list type="number">
///   <item>Per-program pass (<see cref="Run"/>) -> handles non-generic user and stdlib bodies.</item>
///   <item>VariantBodies sweep (<see cref="RunOnVariantBodies"/>) -> after <c>WiredRoutinePass</c>.</item>
///   <item>Instantiated generic bodies sweep (<see cref="RunOnInstantiatedGenericBodies"/>) -> after
///         <c>GenericMonomorphizationPass</c>.</item>
/// </list>
///
/// <para>Covered constant routines: <c>data_size</c>, <c>type_id</c>, <c>type_name</c>,
/// <c>module_name</c>, <c>full_type_name</c>, <c>member_variable_count</c>,
/// <c>is_generic</c>, <c>type_kind</c>.</para>
///
/// <para>For bodies with unbound generic type parameters (e.g. <c>T.data_size()</c> before
/// monomorphization), <see cref="GenericAstRewriter"/> folds these during its substitution
/// rewrite. This pass handles the concrete-type residue left in instantiated or non-generic bodies.</para>
/// </summary>
internal sealed class BuilderQueryInliningPass
{
    private readonly TypeRegistry _registry;

    private readonly DesugaringContext? _ctx;

    private readonly Dictionary<string, Statement>? _ownedVariantBodies;

    // Tracks the enclosing routine name while walking — used for source_routine() / source_module().
    private string _currentRoutineName = "<unknown>";
    private string _currentRoutineModule = "";

    /// <summary>Creates a pass instance backed by a full <see cref="DesugaringContext"/>.</summary>
    internal BuilderQueryInliningPass(DesugaringContext ctx)
    {
        _ctx = ctx;
        _registry = ctx.Registry;
    }

    /// <summary>
    /// Creates a pass instance from a <see cref="TypeRegistry"/> alone (no desugaring context).
    /// Supports <see cref="Run"/> and <see cref="RunOnVariantBodies"/> only.
    /// </summary>
    internal BuilderQueryInliningPass(TypeRegistry registry,
        Dictionary<string, Statement>? variantBodies = null)
    {
        _registry = registry;
        _ownedVariantBodies = variantBodies;
    }

    private static readonly HashSet<string> _foldableRoutines = new(StringComparer.Ordinal)
    {
        RuntimeContract.DataSize,
        "type_id",
        "type_name",
        "module_name",
        "full_type_name",
        "member_variable_count",
        "is_generic",
        "is_in_flight",
        "type_kind"
    };

    private static readonly HashSet<string> _sourceLocationRoutines = new(StringComparer.Ordinal)
    {
        "source_file", "source_line", "source_column",
        "source_routine", "source_module", "source_text",
        "caller_file", "caller_line", "caller_routine"
    };

    /// <summary>
    /// Returns true if <paramref name="routineName"/> is a BuilderQuery constant routine that
    /// can be folded to a literal. Used by <see cref="GenericAstRewriter"/> to gate its own fold.
    /// </summary>
    internal static bool IsFoldable(string routineName) =>
        _foldableRoutines.Contains(routineName);

    // Lazily looked up once per pass instance
    private TypeInfo? _u64Type;
    private TypeInfo? _s64Type;
    private TypeInfo? _textType;
    private TypeInfo? _boolType;
    private TypeInfo? _byteSizeType;

    // Set per-body in RunOnInstantiatedGenericBodies so that
    // ResolveReceiverType can resolve unbound generic params (e.g. T -> Core.Byte).
    private Dictionary<string, TypeInfo>? _currentTypeSubs;

    //  Public entry points

    /// <summary>
    /// Runs the pass on all routine declarations in <paramref name="program"/>.
    /// </summary>
    public void Run(Program program)
    {
        // Extract module name from program's ModuleDeclaration (if any)
        string programModule = "";
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            if (decl is ModuleDeclaration mod) { programModule = mod.Path; break; }
        }

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    _currentRoutineName = r.Name;
                    _currentRoutineModule = programModule;
                    Statement newBody = LowerStatement(r.Body);
                    if (!ReferenceEquals(newBody, r.Body))
                        program.Declarations[i] = r with { Body = newBody };
                    break;
                }

                case EntityDeclaration { GenericParameters: not { Count: > 0 } } e:
                    LowerMemberList(e.Members, programModule);
                    break;

                case RecordDeclaration { GenericParameters: not { Count: > 0 } } rec:
                    LowerMemberList(rec.Members, programModule);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(cr.Members, programModule);
                    break;
            }
        }
    }

    /// <summary>
    /// Folds BS constant calls in all synthesized bodies in
    /// <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated.
    /// </summary>
    public void RunOnVariantBodies()
    {
        Dictionary<string, Statement> bodies = _ctx?.VariantBodies ?? _ownedVariantBodies
            ?? throw new InvalidOperationException("No variant bodies available");
        foreach (string key in bodies.Keys.ToList())
        {
            Statement body = bodies[key];
            Statement lowered = LowerStatement(body);
            if (!ReferenceEquals(lowered, body))
                bodies[key] = lowered;
        }
    }

    /// <summary>
    /// Folds BS constant calls in all instantiated generic bodies in
    /// <see cref="DesugaringContext.InstantiatedGenericBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after
    /// <c>GenericMonomorphizationPass</c> populates the map.
    /// </summary>
    public void RunOnInstantiatedGenericBodies()
    {
        foreach (string key in _ctx!.InstantiatedGenericBodies.Keys.ToList())
        {
            MonomorphizedBody entry = _ctx.InstantiatedGenericBodies[key];
            if (entry.IsSynthesized) continue; // pure-synthesized: no AST to walk

            _currentTypeSubs = entry.TypeSubs;
            Statement lowered = LowerStatement(entry.Ast.Body);
            _currentTypeSubs = null;
            if (!ReferenceEquals(lowered, entry.Ast.Body))
                _ctx.InstantiatedGenericBodies[key] = entry with
                {
                    Ast = entry.Ast with { Body = lowered }
                };
        }
    }

    //  Member list helper

    private void LowerMemberList(List<SyntaxTree.Declaration> members, string module = "")
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            _currentRoutineName = m.Name;
            _currentRoutineModule = module;
            Statement newBody = LowerStatement(m.Body);
            if (!ReferenceEquals(newBody, m.Body))
                members[j] = m with { Body = newBody };
        }
    }

    //  Statement walker

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
            {
                bool changed = false;
                var list = new List<Statement>(b.Statements.Count);
                foreach (Statement s in b.Statements)
                {
                    Statement ns = LowerStatement(s);
                    list.Add(ns);
                    if (!ReferenceEquals(ns, s)) changed = true;
                }
                return changed ? b with { Statements = list } : stmt;
            }

            case IfStatement ifs:
            {
                Expression cond = LowerExpression(ifs.Condition);
                Statement then = LowerStatement(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatement(ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(cond, ifs.Condition)
                               || !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                return changed
                    ? ifs with { Condition = cond, ThenStatement = then, ElseStatement = elseS }
                    : stmt;
            }

            case WhileStatement w:
            {
                Expression cond = LowerExpression(w.Condition);
                Statement body = LowerStatement(w.Body);
                bool changed = !ReferenceEquals(cond, w.Condition)
                               || !ReferenceEquals(body, w.Body);
                return changed ? w with { Condition = cond, Body = body } : stmt;
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatement(loop.Body);
                return ReferenceEquals(body, loop.Body) ? stmt : loop with { Body = body };
            }

            case EachStatement f:
            {
                Expression iter = LowerExpression(f.Iterable);
                Statement body = LowerStatement(f.Body);
                bool changed = !ReferenceEquals(iter, f.Iterable)
                               || !ReferenceEquals(body, f.Body);
                return changed ? f with { Iterable = iter, Body = body } : stmt;
            }

            case WhenStatement ws:
            {
                Expression subject = LowerExpression(ws.Expression);
                bool changed = !ReferenceEquals(subject, ws.Expression);
                var clauses = new List<WhenClause>(ws.Clauses.Count);
                foreach (WhenClause c in ws.Clauses)
                {
                    Statement cb = LowerStatement(c.Body);
                    if (!ReferenceEquals(cb, c.Body)) changed = true;
                    clauses.Add(!ReferenceEquals(cb, c.Body) ? c with { Body = cb } : c);
                }
                return changed ? ws with { Expression = subject, Clauses = clauses } : stmt;
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression v = LowerExpression(ret.Value);
                return ReferenceEquals(v, ret.Value) ? stmt : ret with { Value = v };
            }

            case AssignmentStatement assign:
            {
                Expression val = LowerExpression(assign.Value);
                return ReferenceEquals(val, assign.Value)
                    ? stmt
                    : assign with { Value = val };
            }

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
            {
                Expression init = LowerExpression(vd.Initializer);
                if (ReferenceEquals(init, vd.Initializer)) return stmt;
                return ds with { Declaration = vd with { Initializer = init } };
            }

            case ExpressionStatement es:
            {
                Expression e = LowerExpression(es.Expression);
                return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
            }

            case DiscardStatement ds:
            {
                Expression e = LowerExpression(ds.Expression);
                return ReferenceEquals(e, ds.Expression) ? stmt : ds with { Expression = e };
            }

            case ThrowStatement ts:
            {
                Expression e = LowerExpression(ts.Error);
                return ReferenceEquals(e, ts.Error) ? stmt : ts with { Error = e };
            }

            case BecomesStatement bs:
            {
                Expression v = LowerExpression(bs.Value);
                return ReferenceEquals(v, bs.Value) ? stmt : bs with { Value = v };
            }

            case UsingStatement us:
            {
                Statement body = LowerStatement(us.Body);
                Statement? fb = us.FallbackBody != null ? LowerStatement(us.FallbackBody) : null;
                return ReferenceEquals(body, us.Body) && ReferenceEquals(fb, us.FallbackBody)
                    ? stmt
                    : us with { Body = body, FallbackBody = fb };
            }

            case DangerStatement danger:
            {
                Statement newBody = LowerStatement(danger.Body);
                if (!ReferenceEquals(newBody, danger.Body) && newBody is BlockStatement bs2)
                    return danger with { Body = bs2 };
                return stmt;
            }

            default:
                return stmt;
        }
    }

    //  Expression walker

    private Expression LowerExpression(Expression expr)
    {
        // Leaf nodes: nothing to fold or recurse into.
        if (expr is LiteralExpression or TypeIdExpression) return expr;

        // Source location constant-call folding (standalone calls, not memberRoutine calls)
        if (expr is CallExpression
            {
                Callee: IdentifierExpression { Name: var slName },
                Arguments: { Count: 0 }
            } slCall
            && _sourceLocationRoutines.Contains(slName))
        {
            EnsureTypes();
            Expression? slFolded = FoldSourceLocationCall(slName, slCall.Location);
            if (slFolded != null) return slFolded;
        }

        //  BuilderQuery constant-call folding
        if (expr is CallExpression
            {
                Callee: MemberExpression { MemberName: var routineName } bsCallee,
                Arguments: { Count: 0 }
            } bsCall
            && _foldableRoutines.Contains(routineName))
        {
            TypeInfo? receiverType = ResolveReceiverType(bsCallee.Object);
            if (receiverType != null)
            {
                Expression? folded = FoldBsCall(routineName, receiverType, bsCall.Location,
                    receiverIsInFlight: bsCallee.Object.IsInFlight);
                if (folded != null) return folded;
            }
        }

        //  Structural recursion
        switch (expr)
        {
            case BinaryExpression bin:
            {
                Expression l = LowerExpression(bin.Left);
                Expression r = LowerExpression(bin.Right);
                return ReferenceEquals(l, bin.Left) && ReferenceEquals(r, bin.Right)
                    ? expr : bin with { Left = l, Right = r };
            }

            case UnaryExpression un:
            {
                Expression o = LowerExpression(un.Operand);
                return ReferenceEquals(o, un.Operand) ? expr : un with { Operand = o };
            }

            case CallExpression call:
            {
                Expression callee = LowerExpression(call.Callee);
                List<Expression> args = LowerExpressionList(call.Arguments);
                bool changed = !ReferenceEquals(callee, call.Callee)
                               || !ReferenceEquals(args, call.Arguments);
                if (!changed) return expr;
                var rewritten = call with { Callee = callee, Arguments = args };
                // ResolvedRoutine/ResolvedType/etc. are mutable {get;set;} properties — `with` drops them.
                rewritten.ResolvedRoutine = call.ResolvedRoutine;
                rewritten.LoweringKind = call.LoweringKind;
                rewritten.ConstructedType = call.ConstructedType;
                rewritten.IsCollectionLiteral = call.IsCollectionLiteral;
                rewritten.TypeArguments = call.TypeArguments;
                rewritten.ResolvedType = call.ResolvedType;
                return rewritten;
            }

            case NamedArgumentExpression named:
            {
                Expression v = LowerExpression(named.Value);
                return ReferenceEquals(v, named.Value) ? expr : named with { Value = v };
            }

            case MemberExpression mem:
            {
                Expression o = LowerExpression(mem.Object);
                return ReferenceEquals(o, mem.Object) ? expr : mem with { Object = o };
            }

            case OptionalMemberExpression omem:
            {
                Expression o = LowerExpression(omem.Object);
                return ReferenceEquals(o, omem.Object) ? expr : omem with { Object = o };
            }

            case IndexExpression idx:
            {
                Expression o = LowerExpression(idx.Object);
                Expression i = LowerExpression(idx.Index);
                bool changed = !ReferenceEquals(o, idx.Object) || !ReferenceEquals(i, idx.Index);
                if (!changed)
                    return expr;

                var rewritten = idx with { Object = o, Index = i };
                rewritten.ResolvedType = idx.ResolvedType;
                rewritten.ResolvedSetItem = idx.ResolvedSetItem;
                return rewritten;
            }

            case TypeConversionExpression conv:
            {
                Expression e = LowerExpression(conv.Expression);
                return ReferenceEquals(e, conv.Expression) ? expr : conv with { Expression = e };
            }

            case StealExpression steal:
            {
                Expression o = LowerExpression(steal.Operand);
                return ReferenceEquals(o, steal.Operand) ? expr : steal with { Operand = o };
            }

            case GenericMemberRoutineCallExpression gmc:
            {
                Expression obj = LowerExpression(gmc.Object);
                List<Expression> args = LowerExpressionList(gmc.Arguments);
                bool changed = !ReferenceEquals(obj, gmc.Object)
                               || !ReferenceEquals(args, gmc.Arguments);
                return changed ? gmc with { Object = obj, Arguments = args } : expr;
            }

            case GenericMemberExpression gmem:
            {
                Expression o = LowerExpression(gmem.Object);
                return ReferenceEquals(o, gmem.Object) ? expr : gmem with { Object = o };
            }

            case IsPatternExpression ip:
            {
                Expression e = LowerExpression(ip.Expression);
                return ReferenceEquals(e, ip.Expression) ? expr : ip with { Expression = e };
            }

            case FlagsTestExpression flags:
            {
                Expression s = LowerExpression(flags.Subject);
                return ReferenceEquals(s, flags.Subject) ? expr : flags with { Subject = s };
            }

            case ChainedComparisonExpression chain:
            {
                List<Expression> operands = LowerExpressionList(chain.Operands);
                return ReferenceEquals(operands, chain.Operands)
                    ? expr : chain with { Operands = operands };
            }

            case CompoundAssignmentExpression comp:
            {
                Expression target = LowerExpression(comp.Target);
                Expression value = LowerExpression(comp.Value);
                bool changed = !ReferenceEquals(target, comp.Target)
                               || !ReferenceEquals(value, comp.Value);
                return changed ? comp with { Target = target, Value = value } : expr;
            }

            case RangeExpression range:
            {
                Expression start = LowerExpression(range.Start);
                Expression end = LowerExpression(range.End);
                Expression? step = range.Step != null ? LowerExpression(range.Step) : null;
                bool changed = !ReferenceEquals(start, range.Start)
                               || !ReferenceEquals(end, range.End)
                               || !ReferenceEquals(step, range.Step);
                return changed ? range with { Start = start, End = end, Step = step } : expr;
            }

            case ConditionalExpression cond:
            {
                Expression c = LowerExpression(cond.Condition);
                Expression t = LowerExpression(cond.TrueExpression);
                Expression f = LowerExpression(cond.FalseExpression);
                bool changed = !ReferenceEquals(c, cond.Condition)
                               || !ReferenceEquals(t, cond.TrueExpression)
                               || !ReferenceEquals(f, cond.FalseExpression);
                return changed
                    ? cond with { Condition = c, TrueExpression = t, FalseExpression = f }
                    : expr;
            }

            case TupleLiteralExpression tuple:
            {
                List<Expression> elems = LowerExpressionList(tuple.Elements);
                return ReferenceEquals(elems, tuple.Elements)
                    ? expr : tuple with { Elements = elems };
            }

            case ListLiteralExpression list:
            {
                List<Expression> elems = LowerExpressionList(list.Elements);
                return ReferenceEquals(elems, list.Elements)
                    ? expr : list with { Elements = elems };
            }

            case SetLiteralExpression set:
            {
                List<Expression> elems = LowerExpressionList(set.Elements);
                return ReferenceEquals(elems, set.Elements)
                    ? expr : set with { Elements = elems };
            }

            case DictLiteralExpression dict:
            {
                bool changed = false;
                var pairs = new List<(Expression Key, Expression Value)>(dict.Pairs.Count);
                foreach ((Expression k, Expression v) in dict.Pairs)
                {
                    Expression lk = LowerExpression(k);
                    Expression lv = LowerExpression(v);
                    pairs.Add((lk, lv));
                    if (!ReferenceEquals(lk, k) || !ReferenceEquals(lv, v)) changed = true;
                }
                return changed ? dict with { Pairs = pairs } : expr;
            }

            case CreatorExpression creator:
            {
                bool changed = false;
                var members = new List<(string Name, Expression Value)>(
                    creator.MemberVariables.Count);
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    Expression v = LowerExpression(value);
                    members.Add((name, v));
                    if (!ReferenceEquals(v, value)) changed = true;
                }
                return changed ? creator with { MemberVariables = members } : expr;
            }

            case InsertedTextExpression fstr:
            {
                bool changed = false;
                var parts = new List<InsertedTextPart>(fstr.Parts.Count);
                foreach (InsertedTextPart part in fstr.Parts)
                {
                    if (part is ExpressionPart ep)
                    {
                        Expression e = LowerExpression(ep.Expression);
                        if (!ReferenceEquals(e, ep.Expression))
                        {
                            parts.Add(ep with { Expression = e });
                            changed = true;
                            continue;
                        }
                    }
                    parts.Add(part);
                }
                return changed ? fstr with { Parts = parts } : expr;
            }

            case BackIndexExpression back:
            {
                Expression o = LowerExpression(back.Operand);
                return ReferenceEquals(o, back.Operand) ? expr : back with { Operand = o };
            }

            case BlockExpression block:
            {
                Expression v = LowerExpression(block.Value);
                return ReferenceEquals(v, block.Value) ? expr : block with { Value = v };
            }

            case WaitforExpression wf:
            {
                Expression o = LowerExpression(wf.Operand);
                Expression? timeout = wf.Timeout != null ? LowerExpression(wf.Timeout) : null;
                bool changed = !ReferenceEquals(o, wf.Operand)
                               || !ReferenceEquals(timeout, wf.Timeout);
                return changed ? wf with { Operand = o, Timeout = timeout } : expr;
            }

            case CarrierPayloadExpression cpe:
            {
                Expression c = LowerExpression(cpe.Carrier);
                return ReferenceEquals(c, cpe.Carrier) ? expr : cpe with { Carrier = c };
            }

            default:
                return expr;
        }
    }

    private List<Expression> LowerExpressionList(List<Expression> list)
    {
        bool changed = false;
        var result = new List<Expression>(list.Count);
        foreach (Expression e in list)
        {
            Expression le = LowerExpression(e);
            result.Add(le);
            if (!ReferenceEquals(le, e)) changed = true;
        }
        return changed ? result : list;
    }

    //  Type resolution

    /// <summary>
    /// Resolves the <see cref="TypeInfo"/> for the receiver of a potential BS call.
    /// Returns null when the receiver is an unbound generic type parameter (can't fold).
    /// </summary>
    private TypeInfo? ResolveReceiverType(Expression receiver)
    {
        // 1. Use the expression's ResolvedType if it's a concrete (non-generic-param) type.
        if (receiver.ResolvedType is { } rt and not GenericParameterTypeInfo)
            return rt;

        // 1b. ResolvedType is a generic param -> look it up in the current body's TypeSubs
        //     (e.g. T in List[T].getitem! body with TypeSubs {T -> Core.Byte, I -> Core.S64}).
        if (receiver.ResolvedType is GenericParameterTypeInfo gp)
        {
            if (_currentTypeSubs != null
                && _currentTypeSubs.TryGetValue(gp.Name, out TypeInfo? subFromSubs))
                return subFromSubs;

            // Still an UNBOUND parameter (no substitution available): DEFER — the fold re-runs on the
            // concrete owner post-monomorphization. Must NOT fall through to the global LookupType below,
            // which a same-named user `record T` would hijack: `T.data_size()` in `Sender[T].send` would
            // fold to the RECORD's size (4) instead of the real element's (8), a too-small channel/buffer
            // allocation → heap overflow ([[generic-parameter identity = SLOT]]).
            return null;
        }

        // 2. Look up an identifier as a type name (handles post-monomorphization identifiers
        //    like IdentifierExpression("Core.Byte") produced by GenericAstRewriter).
        if (receiver is IdentifierExpression { Name: var idName })
        {
            // If identifier name is a generic param name, resolve via TypeSubs first.
            if (_currentTypeSubs != null
                && _currentTypeSubs.TryGetValue(idName, out TypeInfo? subByName))
                return subByName;
            return _registry.LookupType(idName);
        }

        // 3. TypeExpression used as the receiver (e.g. in type-level memberRoutine calls).
        if (receiver is TypeExpression { Name: var teName })
        {
            if (_currentTypeSubs != null
                && _currentTypeSubs.TryGetValue(teName, out TypeInfo? teSub))
                return teSub;
            return _registry.LookupType(teName);
        }

        return null;
    }

    //  Source location folding

    private LiteralExpression? FoldSourceLocationCall(string routineName, SourceLocation callSite)
    {
        return routineName switch
        {
            "source_file" or "caller_file" when _textType != null =>
                MakeLiteralText(callSite.FileName, _textType, callSite),
            "source_line" or "caller_line" when _s64Type != null =>
                MakeLiteralS64(callSite.Line, _s64Type, callSite),
            "source_column" when _s64Type != null =>
                MakeLiteralS64(callSite.Column, _s64Type, callSite),
            "source_routine" or "caller_routine" when _textType != null =>
                MakeLiteralText(_currentRoutineName, _textType, callSite),
            "source_module" when _textType != null =>
                MakeLiteralText(_currentRoutineModule, _textType, callSite),
            "source_text" when _textType != null =>
                MakeLiteralText("<expr>", _textType, callSite),
            _ => null
        };
    }

    //  Constant computation

    /// <summary>
    /// Returns a folded <see cref="LiteralExpression"/> for the given BuilderQuery constant
    /// routine on <paramref name="type"/>, or null if the routine is not supported or required
    /// types are not yet registered.
    /// </summary>
    private Expression? FoldBsCall(string routineName, TypeInfo type, SourceLocation loc,
        bool receiverIsInFlight = false) // NOSONAR S3776
    {
        EnsureTypes();
        string inFlightPrefix = receiverIsInFlight && type is EntityTypeInfo ? "?" : "";
        switch (routineName)
        {
            case RuntimeContract.DataSize when _u64Type != null && _byteSizeType != null:
                return MakeByteSizeCreator(CalculateDataSizeForType(type), _u64Type, _byteSizeType, loc);

            case "type_id" when _u64Type != null:
                return MakeLiteralU64(TypeIdHelper.ComputeTypeId(type.FullName), _u64Type, loc);

            case "type_name" when _textType != null:
            {
                // Defer when receiver is still a generic definition (no type subs available):
                // GenericMonomorphizationPass will fold per concrete instance later, producing
                // the correct "List[S64]" form (full_type_name gives "Core.List[Core.S64]").
                // Folding here would bake the bare "List".
                if (type.IsGenericDefinition && _currentTypeSubs == null) return null;
                return MakeLiteralText(inFlightPrefix + GetShortTypeName(type), _textType, loc);
            }

            case "module_name" when _textType != null:
                return MakeLiteralText(type.Module ?? "", _textType, loc);

            case "full_type_name" when _textType != null:
                if (type.IsGenericDefinition && _currentTypeSubs == null) return null;
                return MakeLiteralText(InsertInFlightMark(GetFullTypeName(type), inFlightPrefix),
                    _textType, loc);

            case "member_variable_count" when _s64Type != null:
            {
                long count = type switch
                {
                    TupleTypeInfo t => t.MemberVariables.Count,
                    ChoiceTypeInfo ch => ch.Cases.Count,
                    FlagsTypeInfo f => f.Members.Count,
                    VariantTypeInfo v => v.Members.Count,
                    RecordTypeInfo r => r.MemberVariables.Count,
                    EntityTypeInfo e => e.MemberVariables.Count,
                    _ => 0L
                };
                return MakeLiteralS64(count, _s64Type, loc);
            }

            case "is_generic" when _boolType != null:
            {
                bool isGen = type.IsGenericDefinition;
                return new LiteralExpression(
                    Value: isGen,
                    LiteralType: isGen ? TokenType.True : TokenType.False,
                    Location: loc) { ResolvedType = _boolType };
            }

            case "is_in_flight" when _boolType != null:
            {
                bool inFlight = receiverIsInFlight && type is EntityTypeInfo;
                return new LiteralExpression(
                    Value: inFlight,
                    LiteralType: inFlight ? TokenType.True : TokenType.False,
                    Location: loc) { ResolvedType = _boolType };
            }

            case "type_kind":
            {
                // TypeKind lives in `module BuilderQuery` — qualify (bare lookup relied on the short-name scan).
                TypeInfo? tkType = _registry.LookupType(name: "BuilderQuery.TypeKind");
                if (tkType is not ChoiceTypeInfo tkChoice) return null;
                // Wrappers (Retained/Modifying/etc) report the inner type's kind.
                TypeInfo kindType = type is WrapperTypeInfo wt ? wt.InnerType : type;
                string caseName = kindType.Category switch
                {
                    TypeCategory.Record => "RECORD",
                    TypeCategory.Entity => "ENTITY",
                    TypeCategory.Crashable => "CRASHABLE",
                    TypeCategory.Choice => "CHOICE",
                    TypeCategory.Variant => "VARIANT",
                    TypeCategory.Flags => "FLAGS",
                    TypeCategory.Routine => "ROUTINE",
                    TypeCategory.Protocol => "PROTOCOL",
                    _ => throw new InvalidOperationException(
                        $"Unhandled TypeCategory '{kindType.Category}' in type_kind BuilderQuery mapping.")
                };
                ChoiceCaseInfo? found = tkChoice.Cases.FirstOrDefault(c => c.Name == caseName);
                if (found == null) return null;
                // Emit as S64 literal with ResolvedType = TypeKind (choice), mirrors WiredRoutinePass.
                return MakeLiteralS64(found.ComputedValue, tkType, loc);
            }

            default:
                return null;
        }
    }

    private void EnsureTypes()
    {
        _u64Type ??= _registry.LookupType(name: "U64");
        _s64Type ??= _registry.LookupType(name: "S64");
        _textType ??= _registry.LookupType(name: "Text");
        _boolType ??= _registry.LookupType(name: "Bool");
        _byteSizeType ??= _registry.LookupType(name: "ByteSize");
    }

    //  Literal factory helpers

    private static LiteralExpression MakeLiteralU64(ulong value, TypeInfo type, SourceLocation loc) =>
        new(Value: value, LiteralType: TokenType.U64Literal, Location: loc)
        { ResolvedType = type };

    private static CreatorExpression MakeByteSizeCreator(ulong value, TypeInfo u64Type,
        TypeInfo byteSizeType, SourceLocation loc) =>
        MakeByteSizeCreatorPublic(value, u64Type, byteSizeType, loc);

    internal static CreatorExpression MakeByteSizeCreatorPublic(ulong value, TypeInfo u64Type,
        TypeInfo byteSizeType, SourceLocation loc)
    {
        LiteralExpression u64Lit = MakeLiteralU64(value, u64Type, loc);
        return new CreatorExpression(
            TypeName: "ByteSize",
            TypeArguments: null,
            MemberVariables: [("value", u64Lit)],
            Location: loc)
        { ResolvedType = byteSizeType };
    }

    private static LiteralExpression MakeLiteralS64(long value, TypeInfo type, SourceLocation loc) =>
        new(Value: value, LiteralType: TokenType.S64Literal, Location: loc)
        { ResolvedType = type };

    private static LiteralExpression MakeLiteralText(string value, TypeInfo type, SourceLocation loc) =>
        new(Value: value, LiteralType: TokenType.TextLiteral, Location: loc)
        { ResolvedType = type };

    private string GetShortTypeName(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo gp)
            return _currentTypeSubs != null && _currentTypeSubs.TryGetValue(gp.Name, out TypeInfo? sub)
                ? GetShortTypeName(sub)
                : gp.Name;
        if (type.TypeArguments is not { Count: > 0 } args)
            return type.BareName;
        string argList = string.Join(separator: ", ", values: args.Select(selector: GetShortTypeName));
        return $"{type.BareName}[{argList}]";
    }

    private static string InsertInFlightMark(string fullName, string mark)
    {
        if (mark.Length == 0) return fullName;
        int dot = fullName.LastIndexOf('.');
        return dot < 0 ? mark + fullName : fullName[..(dot + 1)] + mark + fullName[(dot + 1)..];
    }

    private string GetFullTypeName(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo gp)
            return _currentTypeSubs != null && _currentTypeSubs.TryGetValue(gp.Name, out TypeInfo? sub)
                ? GetFullTypeName(sub)
                : gp.Name;
        string baseName = string.IsNullOrEmpty(type.Module) ? type.BareName : $"{type.Module}.{type.BareName}";
        if (type.TypeArguments is not { Count: > 0 } args)
            return baseName;
        string argList = string.Join(separator: ", ", values: args.Select(selector: GetFullTypeName));
        return $"{baseName}[{argList}]";
    }

    //  Data size computation (single source of truth — also used by GenericAstRewriter's fold)

    /// <summary>
    /// Returns the byte size of <paramref name="type"/> as seen by collection pointer arithmetic.
    /// </summary>
    internal static ulong CalculateDataSizeForType(TypeInfo type) => type switch
    {
        // Tuple is a RecordTypeInfo (item0/item1/... members) — delegate to SizeBytes like records.
        // `element_count * 8` was wrong for tuples holding a non-8-byte element (e.g. a Text=24).
        TupleTypeInfo t => (ulong)t.SizeBytes(pointerSize: 8),
        // Variant is a tagged union: tag + MAX arm payload (not sum). Delegate to SizeBytes.
        // Variant is a RecordTypeInfo subclass, so it MUST precede the Record arms below.
        VariantTypeInfo v => (ulong)v.SizeBytes(pointerSize: 8),
        RecordTypeInfo { HasDirectBackendType: true } r => LlvmBackendTypeSize(r.BackendType!),
        // Delegate to the SAME size function codegen uses (RecordTypeInfo.SizeBytes) so the List
        // element stride matches the actual struct layout. `member_count * 8` was wrong for any
        // record with a non-8-byte member (a nested value-record like Text=24, or i32/i128).
        RecordTypeInfo r => (ulong)r.SizeBytes(pointerSize: 8),
        EntityTypeInfo => 8,    // heap pointer (Crashable, an entity subclass, included)
        _ => 0
    };

    /// <summary>
    /// Returns the byte size of an LLVM scalar type string from an <c>@llvm("...")</c> annotation.
    /// </summary>
    private static ulong LlvmBackendTypeSize(string llvmType) => llvmType.Trim() switch
    {
        "void" => 0,
        "i1" or "i8" => 1,
        "i16" or "half" => 2,
        "i32" or "float" => 4,
        "i64" or "double" or "ptr" => 8,
        "i128" or "fp128" => 16,
        var s when s.StartsWith('[') => ParseLlvmArraySize(s),
        var s when s.Contains('{') => 0,
        _ => throw new InvalidOperationException(
            $"Unknown LLVM type '{llvmType}' in LlvmBackendTypeSize — cannot determine byte size.")
    };

    private static ulong ParseLlvmArraySize(string arrayType)
    {
        int xIdx = arrayType.IndexOf(" x ", StringComparison.Ordinal);
        if (xIdx < 0) return 0;
        string countPart = arrayType[1..xIdx].Trim();
        string elemPart = arrayType[(xIdx + 3)..].TrimEnd(']').Trim();
        if (!ulong.TryParse(countPart, out ulong count)) return 0;
        return count * LlvmBackendTypeSize(elemPart);
    }
}
