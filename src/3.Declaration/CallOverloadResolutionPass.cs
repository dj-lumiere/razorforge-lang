using Compiler.Desugaring.Passes;
using Compiler.Lowering;
using Compiler.Instantiation;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification;
using Compiler.Desugaring;

namespace Compiler.Declaration;

/// <summary>
/// Post-instantiation pass that resolves overloads and assigns <see cref="CallLoweringKind"/>
/// for any <see cref="CallExpression"/> still marked <c>Unknown</c> after semantic analysis.
///
/// <para>Runs after <see cref="GenericCallLoweringPass"/> so that all generic memberRoutine calls
/// have already been lowered to plain <see cref="CallExpression"/> nodes with concrete
/// receiver types. At that point every <c>ResolvedType</c> on expressions is module-qualified
/// and structural (<c>FullName</c>-level) overload matching is safe.</para>
///
/// <para>SA leaves a call <c>Unknown</c> when overload resolution fails during Phase 4 -> typically because the receiver type lacked its module prefix at analysis time (types
/// resolved from generic bodies may arrive unqualified). This pass re-attempts resolution
/// with fully-qualified types and classifies the surviving unknowns via
/// <see cref="CallClassifier"/>.</para>
/// </summary>
internal sealed class CallOverloadResolutionPass
{
    /// <summary>
    /// Stores the registry state used by this compiler phase.
    /// </summary>
    private readonly TypeRegistry _registry;

    private readonly Dictionary<string, Statement>? _variantBodies;

    private readonly Dictionary<string, Statement>? _synthesizedBodies;

    // Per-body local-variable types (name → declared/inferred type), populated as the walk visits declarations
    // in order. Recovers a monomorphized body's member-call receiver whose reference ResolvedType is null
    // (`n.eq(...)` where `var n = me.count()`). Cleared per top-level body via WalkBody.
    private readonly Dictionary<string, TypeInfo> _localVarTypes =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    internal CallOverloadResolutionPass(PostprocessingContext ctx)
    {
        _registry = ctx.Registry;
        _variantBodies = ctx.VariantBodies;
        _synthesizedBodies = ctx.SynthesizedBodies;
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            switch (decl)
            {
                case RoutineDeclaration r:
                    WalkBody(body: r.Body);
                    break;
                case EntityDeclaration e:
                    WalkMemberList(members: e.Members);
                    break;
                case RecordDeclaration rec:
                    WalkMemberList(members: rec.Members);
                    break;
                case CrashableDeclaration cr:
                    WalkMemberList(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null)
        {
            return;
        }

        foreach (Statement body in _variantBodies.Values)
        {
            WalkBody(body: body);
        }
    }

    /// <summary>
    /// Classifies call expressions in a flat sequence of statement bodies.
    /// Used for <c>InstantiatedGenericBodies</c> produced by GMP: <see cref="GenericAstRewriter"/>
    /// rewrites type parameters but does not re-classify <c>try_emit</c> and other wired calls,
    /// leaving their <c>LoweringKind = Unknown</c>. This pass fills in the missing kind.
    /// </summary>
    public void RunOnStatements(IEnumerable<Statement> statements)
    {
        foreach (Statement body in statements)
        {
            WalkBody(body: body);
        }
    }

    /// <summary>
    /// Like <see cref="RunOnStatements"/> but threads each body's owner type so the implicit `me` receiver is
    /// seeded before the walk — recovers member calls in a monomorph/variant clone whose `me` (and the locals
    /// cascading from it, e.g. `var n = me.count()`) arrive un-typed.
    /// </summary>
    public void RunOnBodiesWithOwners(
        IEnumerable<(Statement body, TypeInfo? owner, IReadOnlyList<ParameterInfo>? parameters)>
            bodies)
    {
        foreach ((Statement body, TypeInfo? owner,
                     IReadOnlyList<ParameterInfo>? parameters) in bodies)
        {
            WalkBody(body: body, owner: owner, parameters: parameters);
        }
    }

    /// <summary>
    /// Classifies all <see cref="CallExpression"/> nodes inside synthesized derived-operator bodies
    /// (ne, lt, le, gt, ge, notcontains). These bodies are built by
    /// <see cref="Compiler.Instantiation.DerivedOperatorPass"/> with <c>ResolvedRoutine</c> set but
    /// <c>LoweringKind = Unknown</c>; this pass fills in the missing kind before codegen.
    /// </summary>
    public void RunOnSynthesizedBodies()
    {
        if (_synthesizedBodies == null)
        {
            return;
        }

        foreach (Statement body in _synthesizedBodies.Values)
        {
            WalkBody(body: body);
        }
    }

    /// <summary>
    /// Walk member list as part of this compiler phase.
    /// </summary>
    private void WalkMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration r)
            {
                WalkBody(body: r.Body);
            }
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>True when <paramref name="type"/> still carries a generic parameter (directly or nested in a
    /// type argument) — i.e. it is not yet a fully-concrete monomorphized type.</summary>
    private static bool TypeContainsGenericParameter(TypeInfo type)
    {
        return type is GenericParameterTypeInfo or ProtocolSelfTypeInfo
                   or ComptimeConstGenericTypeInfo ||
               (type.TypeArguments?.Any(predicate: TypeContainsGenericParameter) ?? false);
    }

    /// <summary>Walks one top-level routine body, resetting the per-body local-variable type scope first.
    /// When <paramref name="owner"/> is known (a monomorphized member routine), seeds the implicit receiver
    /// `me` so a variant/monomorph clone that left `me` un-typed can still resolve `me.count()` etc.</summary>
    private void WalkBody(Statement? body, TypeInfo? owner = null,
        IReadOnlyList<ParameterInfo>? parameters = null)
    {
        if (body == null)
        {
            return;
        }

        _localVarTypes.Clear();
        if (owner is { } o and not ErrorTypeInfo)
        {
            _localVarTypes[key: "me"] = o;
        }

        // Seed the routine's PARAMETERS so a bare param reference used as a member-call receiver resolves.
        // A comptime-`expand` monomorph body (SplitArray.getitem's `index >= N` → `index.ge(N)`) leaves the
        // `index` reference un-typed (the clone doesn't re-annotate every ref), so without this the receiver
        // type is unknown and `.ge` reaches codegen unresolved. Concrete param types only (skip any that
        // still carry a generic parameter).
        if (parameters != null)
        {
            foreach (ParameterInfo p in parameters)
            {
                if (p.Type is { } pt and not ErrorTypeInfo &&
                    !TypeContainsGenericParameter(type: pt))
                {
                    _localVarTypes[key: p.Name] = pt;
                }
            }
        }

        WalkStatement(stmt: body);
    }

    private void WalkStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                foreach (Statement s in block.Statements)
                {
                    WalkStatement(stmt: s);
                }

                break;
            case IfStatement ifs:
                WalkExpression(expr: ifs.Condition);
                WalkStatement(stmt: ifs.ThenStatement);
                if (ifs.ElseStatement != null)
                {
                    WalkStatement(stmt: ifs.ElseStatement);
                }

                break;
            case WhileStatement w:
                WalkExpression(expr: w.Condition);
                WalkStatement(stmt: w.Body);
                break;
            case LoopStatement loop:
                WalkStatement(stmt: loop.Body);
                break;
            case EachStatement f:
                WalkExpression(expr: f.Iterable);
                WalkStatement(stmt: f.Body);
                break;
            case WhenStatement ws:
                WalkExpression(expr: ws.Expression);
                foreach (WhenClause c in ws.Clauses)
                {
                    WalkStatement(stmt: c.Body);
                }

                break;
            case ReturnStatement { Value: not null } ret:
                WalkExpression(expr: ret.Value);
                break;
            case AssignmentStatement assign:
                WalkExpression(expr: assign.Target);
                WalkExpression(expr: assign.Value);
                break;
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: not null } vd
            }:
                WalkExpression(expr: vd.Initializer);
                // Track the local's inferred type (from the walked initializer) so a later member call on a
                // reference to it can recover a receiver type the monomorph clone left un-annotated.
                if (vd.Initializer.ResolvedType is { } vt and not ErrorTypeInfo)
                {
                    _localVarTypes[key: vd.Name] = vt;
                }

                break;
            case ExpressionStatement es:
                WalkExpression(expr: es.Expression);
                break;
            case DiscardStatement ds:
                WalkExpression(expr: ds.Expression);
                break;
            case ThrowStatement ts:
                WalkExpression(expr: ts.Error);
                break;
            case VariantReturnStatement { Value: not null } vrs:
                WalkExpression(expr: vrs.Value);
                break;
            case BecomesStatement bs:
                WalkExpression(expr: bs.Value);
                break;
            case UsingStatement us:
                WalkStatement(stmt: us.Body);
                if (us.FallbackBody != null)
                {
                    WalkStatement(stmt: us.FallbackBody);
                }

                break;
            case DangerStatement danger:
                WalkStatement(stmt: danger.Body);
                break;
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Walk expression as part of this compiler phase.
    /// </summary>
    private void WalkExpression(Expression expr)
    {
        switch (expr)
        {
            case LiteralExpression or IdentifierExpression or TypeIdExpression:
                return;

            case CallExpression call:
                ClassifyCall(call: call);
                WalkExpression(expr: call.Callee);
                foreach (Expression arg in call.Arguments)
                {
                    WalkExpression(expr: arg);
                }

                break;

            case BinaryExpression bin:
                WalkExpression(expr: bin.Left);
                WalkExpression(expr: bin.Right);
                break;

            case UnaryExpression un:
                WalkExpression(expr: un.Operand);
                break;

            case MemberExpression mem:
                WalkExpression(expr: mem.Object);
                break;

            case OptionalMemberExpression omem:
                WalkExpression(expr: omem.Object);
                break;

            case NamedArgumentExpression named:
                WalkExpression(expr: named.Value);
                break;

            case IndexExpression idx:
                WalkExpression(expr: idx.Object);
                WalkExpression(expr: idx.Index);
                break;

            case TypeConversionExpression conv:
                WalkExpression(expr: conv.Expression);
                break;

            case StealExpression steal:
                WalkExpression(expr: steal.Operand);
                break;

            case GenericMemberRoutineCallExpression gmc:
                WalkExpression(expr: gmc.Object);
                foreach (Expression arg in gmc.Arguments)
                {
                    WalkExpression(expr: arg);
                }

                break;

            case GenericMemberExpression gmem:
                WalkExpression(expr: gmem.Object);
                break;

            case IsPatternExpression ip:
                WalkExpression(expr: ip.Expression);
                break;

            case FlagsTestExpression flags:
                WalkExpression(expr: flags.Subject);
                break;

            case ChainedComparisonExpression chain:
                foreach (Expression op in chain.Operands)
                {
                    WalkExpression(expr: op);
                }

                break;

            case CompoundAssignmentExpression comp:
                WalkExpression(expr: comp.Target);
                WalkExpression(expr: comp.Value);
                break;

            case RangeExpression range:
                WalkRangeExpression(range: range);
                break;

            case ConditionalExpression cond:
                WalkExpression(expr: cond.Condition);
                WalkExpression(expr: cond.TrueExpression);
                WalkExpression(expr: cond.FalseExpression);
                break;

            case TupleLiteralExpression tuple:
                foreach (Expression e in tuple.Elements)
                {
                    WalkExpression(expr: e);
                }

                break;

            case ListLiteralExpression list:
                foreach (Expression e in list.Elements)
                {
                    WalkExpression(expr: e);
                }

                break;

            case SetLiteralExpression set:
                foreach (Expression e in set.Elements)
                {
                    WalkExpression(expr: e);
                }

                break;

            case DictLiteralExpression dict:
                WalkDictExpression(dict: dict);
                break;

            case CreatorExpression creator:
                foreach ((_, Expression v) in creator.MemberVariables)
                {
                    WalkExpression(expr: v);
                }

                break;

            case InsertedTextExpression fstr:
                WalkInsertedTextExpression(fstr: fstr);
                break;
        }
    }

    private void WalkRangeExpression(RangeExpression range)
    {
        WalkExpression(expr: range.Start);
        WalkExpression(expr: range.End);
        if (range.Step != null)
        {
            WalkExpression(expr: range.Step);
        }
    }

    private void WalkDictExpression(DictLiteralExpression dict)
    {
        foreach ((Expression k, Expression v) in dict.Pairs)
        {
            WalkExpression(expr: k);
            WalkExpression(expr: v);
        }
    }

    private void WalkInsertedTextExpression(InsertedTextExpression fstr)
    {
        foreach (InsertedTextPart part in fstr.Parts)
        {
            if (part is ExpressionPart ep)
            {
                WalkExpression(expr: ep.Expression);
            }
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Performs the classify call step for this compiler phase.
    /// </summary>
    private void ClassifyCall(CallExpression call)
    {
        TryBackfillAlreadyResolvedType(call: call);

        bool staleProtocolReturn = IsStaleProtocolReturn(call: call);

        // Skip only when FULLY classified — both the lowering kind AND the target routine are known. A body
        // may arrive with LoweringKind set (by GenericAstRewriter) yet ResolvedRoutine still null (a cloned
        // derive's me.assign()); the demand collector relies on this pass as the sole member-call resolver
        // (codegen no longer resolves call targets at emission), so it must still resolve those.
        if (call.LoweringKind != CallLoweringKind.Unknown && call.ResolvedRoutine != null &&
            !staleProtocolReturn)
        {
            return;
        }

        // A stale abstract-protocol ResolvedType must fall through to the FULL member-call resolver,
        // not the fast path below (which keeps the stale ResolvedType). Drop the routine so
        // ClassifyMemberCall re-binds iter on the concrete receiver and refreshes ResolvedType.
        if (staleProtocolReturn)
        {
            call.ResolvedRoutine = null;
        }

        // Fast path: routine already resolved by DerivedOperatorPass or SA.
        // Wired routines like ComparisonSign.eq may not be findable via LookupMemberRoutineOverload
        // (they are handled by codegen directly, not registered as normal overloads).
        if (call.ResolvedRoutine != null)
        {
            call.LoweringKind = call.Callee is MemberExpression
                ? CallClassifier.ClassifyMemberRoutineCall(memberRoutine: call.ResolvedRoutine)
                : CallClassifier.ClassifyStandaloneRoutineCall(routine: call.ResolvedRoutine);
            return;
        }

        List<TypeInfo> argTypes =
            CollectCallArgTypes(call: call, allKnown: out bool allArgTypesKnown);

        switch (call.Callee)
        {
            case MemberExpression member:
                ClassifyMemberCall(call: call,
                    member: member,
                    argTypes: argTypes,
                    allArgTypesKnown: allArgTypesKnown);
                break;
            case IdentifierExpression { Name: var name }:
                ClassifyStandaloneCall(call: call,
                    name: name,
                    argTypes: argTypes,
                    allArgTypesKnown: allArgTypesKnown);
                break;
        }
    }

    /// <summary>
    /// Backfills a null/Error ResolvedType on an already-resolved call from the routine's concrete return type.
    /// Skips ProtocolTypeInfo returns — those are handled by the stale-protocol re-resolve path in
    /// <see cref="IsStaleProtocolReturn"/>, which forces a full re-resolution rather than a naive backfill
    /// (a naive backfill of iter() would recurse into RangeEmittable[RangeEmittable[…]]).
    /// </summary>
    private static void TryBackfillAlreadyResolvedType(CallExpression call)
    {
        if (call.ResolvedRoutine is { ReturnType: { } rrRet } &&
            rrRet is not ProtocolTypeInfo and not ErrorTypeInfo &&
            call.ResolvedType is null or ErrorTypeInfo)
        {
            call.ResolvedType = rrRet;
        }
    }

    /// <summary>
    /// Returns true when the call's ResolvedType is a stale abstract-protocol return left by
    /// GenericAstRewriter on a monomorphized body whose receiver is now fully concrete.
    /// Forces a full re-resolution of the call (e.g. iter()) on the concrete receiver so that
    /// the each-loop iterator var gets the concrete type instead of leaking a protocol to codegen.
    /// Only fires when the receiver has no generic parameters — a still-generic receiver would
    /// spawn unbounded RangeEmittable[RangeEmittable[…]] monomorphization.
    /// </summary>
    private static bool IsStaleProtocolReturn(CallExpression call)
    {
        return call.ResolvedType is ProtocolTypeInfo && call.Callee is MemberExpression
        {
            Object.ResolvedType: { } recvT and not ProtocolTypeInfo
            and not GenericParameterTypeInfo and not ErrorTypeInfo
        } && !TypeContainsGenericParameter(type: recvT);
    }

    /// <summary>
    /// Collects the resolved argument types from a call's argument list, setting
    /// <paramref name="allKnown"/> to false when any argument's type is missing.
    /// Some stdlib generic bodies are lowered before SA runs, so literals may lack ResolvedType.
    /// </summary>
    private static List<TypeInfo> CollectCallArgTypes(CallExpression call, out bool allKnown)
    {
        var argTypes = new List<TypeInfo>(capacity: call.Arguments.Count);
        allKnown = true;
        foreach (Expression arg in call.Arguments)
        {
            TypeInfo? t = arg is NamedArgumentExpression named
                ? named.Value.ResolvedType
                : arg.ResolvedType;
            if (t == null)
            {
                allKnown = false;
            }
            else
            {
                argTypes.Add(item: t);
            }
        }

        return argTypes;
    }

    /// <summary>
    /// Resolves and classifies a member-routine call, applying the const-generic receiver
    /// fallback and the failable-form retry.
    /// </summary>
    private void ClassifyMemberCall(CallExpression call, MemberExpression member,
        List<TypeInfo> argTypes, bool allArgTypesKnown)
    {
        TypeInfo? receiverType = RecoverReceiverType(member: member);
        if (receiverType == null)
        {
            return;
        }

        // Const-generic value types (e.g. ConstGenericValueTypeInfo for N=63 in Array[T,63]) are
        // not registered in _routinesByOwner. Resolve to the underlying numeric type so member-routine
        // lookup can find operators like sub!. Arguments may also lack ResolvedType (pre-SA stdlib
        // bodies), so a by-name fallback lookup is allowed — there is typically one overload.
        if (receiverType is ConstGenericValueTypeInfo constVal)
        {
            string underlyingName = constVal.ExplicitTypeName ?? "U64";
            TypeInfo? resolved = _registry.LookupType(name: underlyingName);
            if (resolved == null)
            {
                return;
            }

            receiverType = resolved;
        }

        RoutineInfo? memberRoutine = LookupMemberRoutineWithFallback(receiverType: receiverType,
            member: member,
            argTypes: argTypes,
            allArgTypesKnown: allArgTypesKnown);
        if (memberRoutine == null)
        {
            return;
        }

        call.ResolvedRoutine = memberRoutine;
        call.LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: memberRoutine);
        // Backfill a call whose ResolvedType is stale/unresolved (null, ErrorTypeInfo, or a lingering abstract
        // protocol) from the freshly-resolved concrete routine's return type. A monomorphized / variant-clone
        // body leaves member-call ResolvedTypes stale: `var n = me.count()` keeps an ErrorTypeInfo so `n` is
        // untyped and a later `n.eq(...)` can't recover its receiver; `var it = r.iter()` keeps the abstract
        // Emittable[S64] which leaks into codegen. Propagating the concrete return forward types the local
        // (count→U64 lets `n` resolve, iter→RangeEmittable[S64] fixes the each-loop var).
        if (call.ResolvedType is null or ErrorTypeInfo or ProtocolTypeInfo &&
            memberRoutine.ReturnType is { } concreteRet &&
            concreteRet is not ProtocolTypeInfo and not ErrorTypeInfo)
        {
            call.ResolvedType = concreteRet;
        }
    }

    /// <summary>
    /// Recovers the receiver type for a member-call expression using three fallback strategies in order:
    /// (1) the node's own ResolvedType if non-null and non-error;
    /// (2) a deferred-type walk for field-walk bodies whose member accesses are intentionally left un-typed;
    /// (3) the local-variable declaration type tracked during this walk (for un-annotated monomorph references);
    /// (4) a type-registry lookup when the receiver is a bare concrete type name (comptime-expand monomorphs).
    /// Returns null when none of the strategies can determine the type.
    /// </summary>
    private TypeInfo? RecoverReceiverType(MemberExpression member)
    {
        // Prefer the node's own type; fall back to a deferred chain walk for derive-template bodies
        // (GenericAstRewriter leaves member types deferred to avoid unbounded concrete instantiations).
        TypeInfo? receiverType = member.Object.ResolvedType is { } rt and not ErrorTypeInfo
            ? rt
            : ComputeDeferredType(expr: member.Object);

        // A monomorphized body's local-variable reference can arrive with a null/deferred ResolvedType
        // (the clone doesn't re-annotate every reference). Recover from the var's DECLARATION type,
        // tracked as this walk visits declarations in body order.
        if (receiverType is null or ErrorTypeInfo &&
            member.Object is IdentifierExpression idRecv &&
            _localVarTypes.TryGetValue(key: idRecv.Name, value: out TypeInfo? declaredT))
        {
            receiverType = declaredT;
        }

        // TYPEWISE TYPE RECEIVER: T.blank() → Point.blank() after T→Point. The receiver is a bare
        // identifier naming a concrete TYPE (not a local/param — checked above). A comptime-expand
        // monomorph body leaves it un-typed; type it as the type it names so the static/wired member
        // resolves. Checked AFTER locals/params so a same-named local still wins.
        if (receiverType is null or ErrorTypeInfo &&
            member.Object is IdentifierExpression typeRecv &&
            _registry.LookupType(name: typeRecv.Name) is { IsGenericDefinition: false } typeRecvTy)
        {
            receiverType = typeRecvTy;
        }

        return receiverType;
    }

    /// <summary>
    /// Looks up the member routine on <paramref name="receiverType"/>, first by full overload signature
    /// (when all arg types are known), then by name alone. If the non-failable form is not found and
    /// <paramref name="member"/> is not already marked failable, retries with <c>isFailable: true</c>
    /// (e.g. U64.sub! — underflow is undefined so only the failable form is registered).
    /// Unknown arg types no longer bail: a monomorphized body can leave a literal arg with ErrorTypeInfo
    /// while the receiver is concrete and the name is unambiguous; the by-name lookup returns null on
    /// genuine ambiguity, leaving the call unresolved exactly as the old bail did.
    /// </summary>
    private RoutineInfo? LookupMemberRoutineWithFallback(TypeInfo receiverType,
        MemberExpression member, List<TypeInfo> argTypes, bool allArgTypesKnown)
    {
        RoutineInfo? memberRoutine = allArgTypesKnown
            ? _registry.LookupMemberRoutineOverload(type: receiverType,
                memberRoutineName: member.MemberName,
                argTypes: argTypes)
            : null;
        memberRoutine ??= _registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: member.MemberName);

        // If the non-failable form isn't registered, try the failable form.
        // MemberName is bare; failability is structural — retry with isFailable: true.
        if (memberRoutine == null && !member.IsFailable)
        {
            memberRoutine = allArgTypesKnown
                ? _registry.LookupMemberRoutineOverload(type: receiverType,
                    memberRoutineName: member.MemberName,
                    argTypes: argTypes)
                : null;
            memberRoutine ??= _registry.LookupMemberRoutine(type: receiverType,
                memberRoutineName: member.MemberName,
                isFailable: true);
        }

        return memberRoutine;
    }

    /// <summary>
    /// Computes an expression's type when its own <see cref="Expression.ResolvedType"/> is null/deferred, by
    /// walking a member-access chain and reading each field's static type off its (recursively computed)
    /// owner. Only field accesses off a typed base (`me`, a typed local) are recovered — it never invents a
    /// type. Used to resolve member calls in derive-template field-walk bodies whose unrolled member accesses
    /// are intentionally left type-deferred (see <c>GenericAstRewriter</c>). Returns null when the chain does
    /// not bottom out in a known type. Does NOT mutate the AST — resolution-only.
    /// </summary>
    private static TypeInfo? ComputeDeferredType(Expression expr)
    {
        if (expr.ResolvedType is { } t and not ErrorTypeInfo)
        {
            return t;
        }

        if (expr is MemberExpression member)
        {
            TypeInfo? ownerType = ComputeDeferredType(expr: member.Object);
            return ownerType switch
            {
                RecordTypeInfo record => record
                                        .LookupMemberVariable(
                                             memberVariableName: member.MemberName)
                                       ?.Type,
                EntityTypeInfo entity => entity
                                        .LookupMemberVariable(
                                             memberVariableName: member.MemberName)
                                       ?.Type,
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves and classifies a standalone (free-routine) call by overload-by-arg-types,
    /// falling back to a unique by-name lookup.
    /// </summary>
    private void ClassifyStandaloneCall(CallExpression call, string name, List<TypeInfo> argTypes,
        bool allArgTypesKnown)
    {
        // Overload-by-arg-types when all arg types are known; otherwise fall back to a
        // unique by-name lookup. Stdlib bodies aren't fully type-annotated, so a free call
        // like `decimalfixed_neg(a: you)` inside a variant body can reach here with an
        // untyped argument — the by-name lookup still resolves it when the name is
        // unambiguous. (A genuinely ambiguous name with unknown arg types stays unresolved.)
        RoutineInfo? routine = allArgTypesKnown
            ? _registry.LookupRoutineOverload(baseName: name, argTypes: argTypes)
            : null;
        routine ??= _registry.LookupRoutine(fullName: name);
        if (routine == null)
        {
            // Not a free routine — a TYPE-CONSTRUCTION call `TypeName(args)` (a comptime-`expand` monomorph
            // body's `throw IndexOutOfBoundsError(index:, count:)`, unresolved because the body was never
            // SA'd). SA normally stamps ConstructedType + `create` here; do the same so codegen's constructor
            // path emits it instead of tripping the RF-S959 gate. Only fires on an unresolved call whose name
            // is a concrete type (idempotent; no effect on resolved calls or real free-routine names).
            if (call is { ConstructedType: null } && _registry.LookupType(name: name) is
                    { IsGenericDefinition: false } ctorType)
            {
                call.ConstructedType = ctorType;
                call.ResolvedType ??= ctorType;
                call.LoweringKind = CallLoweringKind.TypeConstructor;
                if (_registry.LookupCreator(type: ctorType) is { } ctorCreate)
                {
                    call.ResolvedRoutine = ctorCreate;
                }
            }

            return;
        }

        call.ResolvedRoutine = routine;
        call.LoweringKind = CallClassifier.ClassifyStandaloneRoutineCall(routine: routine);
    }
}
