using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers all loop constructs to the <see cref="LoopStatement"/> primitive so the code
/// generator only needs to handle one loop form via <c>EmitLoop</c>.
///
/// <para><b>while</b> -> loop+if-break:</para>
/// <code>
/// while cond { body }
///  ??
/// loop { if !cond { break }; body }
/// </code>
///
/// <para><b>for v in iterable</b> -> loop+when:</para>
/// <code>
///  {
/// var _lf_iter_N = iterable.iter()
/// loop { when _lf_iter_N.try_emit() { is None -> break; else var v -> body } }
/// }
/// </code>
///
/// <para><b>for (a, b) in pairs</b> (tuple destructuring) -> same loop shape, else
/// body prepends positional member-access bindings:</para>
/// <code>
///  {
/// var _lf_iter_N = pairs.iter()
/// loop {
/// when _lf_iter_N.try_emit() {
/// is None -> break
/// else var _lf_elem_M -> { var a = _lf_elem_M.item0; var b = _lf_elem_M.item1; body }
///  }
///  }
/// }
/// </code>
///
/// <para><b>for x in iterable else { alt }</b> (for-else) -> exhaustion flag:</para>
/// <code>
///  {
/// var _lf_exhausted_N: Bool = false
/// var _lf_iter_N = iterable.iter()
/// loop {
/// when _lf_iter_N.try_emit() {
/// is None -> { _lf_exhausted_N = true; break }
/// else var x -> body
///  }
///  }
/// if _lf_exhausted_N { alt }
/// }
/// </code>
///
/// <para>Range-based loops (<c>for x in 0 to n</c>) are also covered: the
/// <c>RangeExpression</c> iterable is converted to <c>Range[T](...)</c> by
/// <see cref="Compiler.Lowering.Passes.ExpressionLoweringPass"/> (which runs after this pass).</para>
/// </summary>
internal sealed class ControlFlowLoweringPass(DesugaringContext ctx)
{
    /// <summary>
    /// Tracks the iter count while this compiler phase runs.
    /// </summary>
    private int _iterCount;

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = LowerStatement(stmt: r.Body);
                    if (!ReferenceEquals(objA: newBody, objB: r.Body))
                    {
                        program.Declarations[index: i] = r with { Body = newBody };
                    }

                    break;
                }

                case EntityDeclaration e:
                    LowerMemberList(members: e.Members);
                    break;

                case RecordDeclaration rec:
                    LowerMemberList(members: rec.Members);
                    break;

                case CrashableDeclaration cr:
                    LowerMemberList(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Lower member list as part of this compiler phase.
    /// </summary>
    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }

            Statement newBody = LowerStatement(stmt: m.Body);
            if (!ReferenceEquals(objA: newBody, objB: m.Body))
            {
                members[index: j] = m with { Body = newBody };
            }
        }
    }

    /// <summary>
    /// Lower statement as part of this compiler phase.
    /// </summary>
    private Statement LowerStatement(Statement stmt)
    {
        return stmt switch
        {
            EachStatement f => LowerEach(eachStmt: f),
            DestructuringStatement ds => LowerDestructuring(destruct: ds),
            BlockStatement b => LowerBlock(b: b),
            WhileStatement w => LowerWhile(whileStmt: w),
            LoopStatement loop => LowerLoop(loop: loop),
            IfStatement ifs => LowerIf(ifs: ifs),
            WhenStatement w => LowerWhen(w: w),
            UsingStatement u => LowerUsing(u: u),
            DangerStatement d => LowerDanger(d: d),
            _ => stmt
        };
    }

    private BlockStatement LowerBlock(BlockStatement b)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement n = LowerStatement(stmt: s);
            // Splice DestructuringStatement's lowered block into the parent scope
            // so its `var a = ...; var b = ...;` bindings are visible to later siblings.
            if (s is DestructuringStatement && n is BlockStatement ds)
            {
                stmts.AddRange(collection: ds.Statements);
            }
            else
            {
                stmts.Add(item: n);
            }

            if (!ReferenceEquals(objA: n, objB: s))
            {
                changed = true;
            }
        }

        return changed
            ? b with { Statements = stmts }
            : b;
    }

    private LoopStatement LowerLoop(LoopStatement loop)
    {
        Statement body = LowerStatement(stmt: loop.Body);
        if (ReferenceEquals(objA: body, objB: loop.Body))
        {
            return loop;
        }

        return loop with { Body = body };
    }

    private Statement LowerIf(IfStatement ifs)
    {
        // `if x is T p [and guard...] { then } [else]` binds `p` for the then-branch only.
        // Desugar to a `when` so the whole binding/scope/codegen path is reused from
        // when-clauses (the binding is then-branch-scoped for free). Non-binding `if`s
        // (plain bool, `isnot`, `is T` without a name) fall through unchanged.
        if (TryLowerIfPatternBinding(ifs: ifs, result: out Statement? whenStmt))
        {
            return whenStmt!;
        }

        Statement then = LowerStatement(stmt: ifs.ThenStatement);
        Statement? elseS = ifs.ElseStatement != null
            ? LowerStatement(stmt: ifs.ElseStatement)
            : null;
        bool tc = !ReferenceEquals(objA: then, objB: ifs.ThenStatement);
        bool ec = !ReferenceEquals(objA: elseS, objB: ifs.ElseStatement);
        return tc || ec
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private WhenStatement LowerWhen(WhenStatement w)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
        foreach (WhenClause c in w.Clauses)
        {
            Statement body = LowerStatement(stmt: c.Body);
            if (!ReferenceEquals(objA: body, objB: c.Body))
            {
                clauses.Add(item: c with { Body = body });
                changed = true;
            }
            else
            {
                clauses.Add(item: c);
            }
        }

        return changed
            ? w with { Clauses = clauses }
            : w;
    }

    private UsingStatement LowerUsing(UsingStatement u)
    {
        Statement body = LowerStatement(stmt: u.Body);
        Statement? fb = u.FallbackBody != null
            ? LowerStatement(stmt: u.FallbackBody)
            : null;
        return !ReferenceEquals(objA: body, objB: u.Body) ||
               !ReferenceEquals(objA: fb, objB: u.FallbackBody)
            ? u with { Body = body, FallbackBody = fb }
            : u;
    }

    private DangerStatement LowerDanger(DangerStatement d)
    {
        // DangerStatement.Body is BlockStatement; LowerStatement on BlockStatement
        // always returns a BlockStatement so the cast is safe.
        Statement lowered = LowerStatement(stmt: d.Body);
        return !ReferenceEquals(objA: lowered, objB: d.Body)
            ? d with { Body = (BlockStatement)lowered }
            : d;
    }

    /// <summary>
    /// Lowers <c>while cond { body }</c> to <c>loop { if !cond { break } body }</c>.
    /// The else branch (if present) is dropped -> while-else is not yet fully implemented.
    /// </summary>
    private LoopStatement LowerWhile(WhileStatement whileStmt)
    {
        SourceLocation loc = whileStmt.Location;
        Statement loweredBody = LowerStatement(stmt: whileStmt.Body);

        // Build: if !cond { break }
        Expression negCond = new UnaryExpression(Operator: UnaryOperator.Not,
            Operand: whileStmt.Condition,
            Location: loc);
        Statement guardBreak = new IfStatement(Condition: negCond,
            ThenStatement: new BlockStatement(Statements:
                [new BreakStatement(Location: loc)],
                Location: loc),
            ElseStatement: null,
            Location: loc);

        // Build: loop { if !cond { break }; body }
        Statement loopBody = loweredBody is BlockStatement block
            ? block with { Statements = [guardBreak, .. block.Statements] }
            : new BlockStatement(Statements: [guardBreak, loweredBody], Location: loc);

        return new LoopStatement(Body: loopBody, Location: loc);
    }

    /// <summary>
    /// Lowers a binding <c>if</c> — <c>if x is T p [and g1 and g2 ...] { then } [else { alt }]</c> —
    /// to a <c>when</c>:
    /// <code>
    /// when x {
    ///   is T p [if g1 and g2 ...] -> then
    ///   else -> alt          // empty block when there is no else
    /// }
    /// </code>
    /// This reuses the when-clause machinery so the pattern binding <c>p</c> is scoped to the
    /// then-branch only (SA declares it inside the clause scope; codegen materialises it via
    /// <c>EmitSwitchArmBinding</c>). Returns false — leaving the <c>if</c> unchanged — unless the
    /// condition's head is a non-negated <c>is</c>-pattern that introduces a binding. Guard operands
    /// on the <c>and</c>-spine (which may reference the binding) become the arm guard; anything under
    /// <c>or</c>/<c>not</c> never reaches here, so a binding never escapes into a negated branch.
    /// </summary>
    private bool TryLowerIfPatternBinding(IfStatement ifs, out Statement? result)
    {
        result = null;

        // Walk the left spine of `and` collecting guard operands, until the head is reached.
        // `x is T p and g1 and g2` parses left-assoc as `((x is T p and g1) and g2)`, so the
        // rightmost guard is seen first — reverse to restore source order.
        var guards = new List<Expression>();
        Expression head = ifs.Condition;
        while (head is BinaryExpression { Operator: BinaryOperator.And } andExpr)
        {
            guards.Add(item: andExpr.Right);
            head = andExpr.Left;
        }

        if (head is not IsPatternExpression { IsNegated: false } ipe)
        {
            return false;
        }

        // Only a name-introducing pattern qualifies: `is T p` or a destructuring `is T (a, b)`.
        // A bare `is T` (no binding) stays a plain boolean condition.
        bool introducesBinding = ipe.Pattern is TypePattern { VariableName: not null }
            or TypeDestructuringPattern;
        if (!introducesBinding)
        {
            return false;
        }

        SourceLocation loc = ifs.Location;

        Statement then = LowerStatement(stmt: ifs.ThenStatement);
        Statement? elseS = ifs.ElseStatement != null
            ? LowerStatement(stmt: ifs.ElseStatement)
            : null;
        // No else on the original `if` → the fall-through does nothing, but an empty block needs
        // an explicit `pass` (RF-S211).
        Statement elseBody = elseS ?? new BlockStatement(Statements:
            [new PassStatement(Location: loc)],
            Location: loc);

        // Guarded form `is T p and g1 and g2 ...`: put the guard test INSIDE the matched arm as a
        // nested `if`, so the arm's binding (`p`) is materialised before the guard reads it. A
        // when-clause `GuardPattern` would instead flatten to `(x is T) and guard`, evaluating the
        // guard before `p` is bound — broken for guards that reference the binding. Guard failure
        // runs the same else path as a type mismatch.
        Statement matchBody = then;
        if (guards.Count > 0)
        {
            guards.Reverse();
            Expression guard = guards[index: 0];
            for (int i = 1; i < guards.Count; i++)
            {
                guard = new BinaryExpression(Left: guard,
                    Operator: BinaryOperator.And,
                    Right: guards[index: i],
                    Location: loc);
            }

            matchBody = new IfStatement(Condition: guard,
                ThenStatement: then,
                ElseStatement: elseBody,
                Location: loc);
        }

        var matchClause = new WhenClause(Pattern: ipe.Pattern, Body: matchBody, Location: loc);
        var elseClause = new WhenClause(
            Pattern: new ElsePattern(VariableName: null, Location: loc),
            Body: elseBody,
            Location: loc);

        result = new WhenStatement(Expression: ipe.Expression,
            Clauses: [matchClause, elseClause],
            Location: loc);
        return true;
    }

    /// <summary>
    /// Lowers <c>var (a, b, c) = expr</c> to a block:
    /// <code>
    /// var _ld_tmp_N = expr
    /// var a = _ld_tmp_N.item0
    /// var b = _ld_tmp_N.item1
    /// var c = _ld_tmp_N.item2
    /// </code>
    /// Tuple-only: positional bindings map by index to <c>itemI</c>. Wildcards (<c>_</c>) are skipped.
    /// </summary>
    private BlockStatement LowerDestructuring(DestructuringStatement destruct)
    {
        SourceLocation loc = destruct.Location;
        int n = _iterCount++;
        string tmpName = $"_ld_tmp_{n}";

        var stmts = new List<Statement>(capacity: destruct.Pattern.Bindings.Count + 1)
        {
            new DeclarationStatement(Declaration: new VariableDeclaration(Name: tmpName,
                    Type: null,
                    Initializer: destruct.Initializer,
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc)
        };

        for (int i = 0; i < destruct.Pattern.Bindings.Count; i++)
        {
            DestructuringBinding binding = destruct.Pattern.Bindings[index: i];
            string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_ld_b{i}";
            if (bindName == "_")
            {
                continue;
            }

            stmts.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(
                    Name: bindName,
                    Type: null,
                    Initializer: new MemberExpression(
                        Object: new IdentifierExpression(Name: tmpName, Location: loc),
                        MemberName: $"item{i}",
                        Location: loc),
                    Visibility: VisibilityModifier.Secret,
                    Location: loc),
                Location: loc));
        }

        return new BlockStatement(Statements: stmts, Location: loc);
    }

    /// <summary>
    /// Lower for as part of this compiler phase.
    /// </summary>
    private BlockStatement LowerEach(EachStatement eachStmt)
    {
        SourceLocation loc = eachStmt.Location;
        int n = _iterCount++;
        string iterName = $"_lf_iter_{n}";

        // Preserve the surface iteration-source name (only when it is a plain variable) so the
        // Phase-5 reshaping-during-iteration check can recover it — this EachStatement is gone by
        // the time body analysis runs.
        string? iterationSourceName = eachStmt.Iterable is IdentifierExpression iterSource
            ? iterSource.Name
            : null;

        // -----------------------------------------------------------------------------
        var iterCallExpr = new CallExpression(
            Callee: new MemberExpression(Object: eachStmt.Iterable,
                MemberName: "iter",
                Location: loc),
            Arguments: [],
            Location: loc) { IsSynthesizedLowering = true };

        var tryNextReceiver = new IdentifierExpression(Name: iterName, Location: loc);
        var tryNextCallExpr = new CallExpression(
            Callee: new MemberExpression(Object: tryNextReceiver,
                MemberName: Declaration.RuntimeContract.TryEmit,
                Location: loc),
            Arguments: [],
            Location: loc) { IsSynthesizedLowering = true };

        tryNextCallExpr = AnnotateIterAndTryEmit(eachStmt: eachStmt,
            iterCallExpr: iterCallExpr,
            tryNextReceiver: tryNextReceiver,
            tryNextCallExpr: tryNextCallExpr);
        Expression tryNextCall = tryNextCallExpr;

        Statement iterVarStmt = new DeclarationStatement(
            Declaration: new VariableDeclaration(Name: iterName,
                Type: null,
                Initializer: iterCallExpr,
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        // -----------------------------------------------------------------------------
        Statement loweredBody = LowerStatement(stmt: eachStmt.Body);

        // -----------------------------------------------------------------------------
        (Statement elseBody, string? elseVarName) = BuildElseArm(eachStmt: eachStmt,
            n: n,
            loweredBody: loweredBody,
            loc: loc);

        // -----------------------------------------------------------------------------
        Statement? elseBranchLowered = eachStmt.ElseBranch != null
            ? LowerStatement(stmt: eachStmt.ElseBranch)
            : null;

        return elseBranchLowered != null
            ? BuildForElse(elseBranchLowered: elseBranchLowered,
                tryNextCall: tryNextCall,
                elseBody: elseBody,
                iterVarStmt: iterVarStmt,
                loc: loc,
                names: new ForElseNames(ElseVarName: elseVarName,
                    ExhaustedName: $"_lf_exhausted_{n}",
                    IterationSourceName: iterationSourceName))
            : BuildPlainFor(tryNextCall: tryNextCall,
                elseBody: elseBody,
                elseVarName: elseVarName,
                iterVarStmt: iterVarStmt,
                iterationSourceName: iterationSourceName,
                loc: loc);
    }

    /// <summary>
    /// When running after SA (stdlib/variant bodies), annotate ResolvedType, ResolvedRoutine,
    /// and LoweringKind on the iter and try_emit calls so CallOverloadResolutionPass doesn't
    /// need to re-classify them (which fails for instantiated bodies where the receiver variable
    /// has no SA-annotated type), and so reachability marks the CONCRETE emitter's try_emit.
    /// Skip ErrorTypeInfo: SA suppresses stdlib errors. Returns the (possibly re-annotated)
    /// try_emit call expression.
    /// </summary>
    private CallExpression AnnotateIterAndTryEmit(EachStatement eachStmt,
        CallExpression iterCallExpr, IdentifierExpression tryNextReceiver,
        CallExpression tryNextCallExpr)
    {
        if (eachStmt.Iterable.ResolvedType is { } iterType and not ErrorTypeInfo)
        {
            RoutineInfo? iterMemberRoutine =
                ctx.Registry.LookupMemberRoutine(type: iterType, memberRoutineName: "iter");
            if (iterMemberRoutine?.ReturnType is { } rawIteratorType)
            {
                // LookupMemberRoutine returns the generic-def `iter`, whose ReturnType still carries the
                // owner's params (e.g. `?EnumerateEmitter[T, S/Iter]`). Substitute the concrete
                // owner's type args so `try_emit` resolves on the CONCRETE emitter
                // (`EnumerateEmitter[Text, ListEmitter[Text]]`); otherwise reachability marks the
                // unresolved-projection emitter's try_emit and the concrete one never generates.
                TypeInfo iteratorType = SubstituteForConcreteOwner(type: rawIteratorType,
                    owner: iterType);
                iterCallExpr.ResolvedRoutine = iterMemberRoutine;
                iterCallExpr.ResolvedType = iteratorType;
                // Carry the concrete emitter type onto the receiver so reachability/codegen see it.
                tryNextReceiver.ResolvedType = iteratorType;
                RoutineInfo? tryNextMemberRoutine =
                    ctx.Registry.LookupMemberRoutine(type: iteratorType,
                        memberRoutineName: Declaration.RuntimeContract.TryEmit);
                if (tryNextMemberRoutine != null)
                {
                    tryNextCallExpr = tryNextCallExpr with
                    {
                        ResolvedRoutine = tryNextMemberRoutine,
                        LoweringKind = CallLoweringKind.DirectMemberRoutine,
                        ResolvedType = tryNextMemberRoutine.ReturnType ??
                                       tryNextCallExpr.ResolvedType
                    };
                }
            }
        }

        return tryNextCallExpr;
    }

    /// <summary>
    /// Builds the <c>else var</c> arm body of the emitted <c>when</c> and the arm's bound variable
    /// name. For tuple destructuring, prepends the positional <c>var a = elem.itemI</c> bindings to
    /// the loop body; otherwise the arm binds the loop variable directly (or discards on <c>_</c>).
    /// </summary>
    private static (Statement elseBody, string? elseVarName) BuildElseArm(EachStatement eachStmt,
        int n, Statement loweredBody, SourceLocation loc)
    {
        if (eachStmt.VariablePattern != null)
        {
            // Tuple destructuring: else var _lf_elem_M -> { var a = elem.item0; var b = elem.item1; ... body }
            string elemName = $"_lf_elem_{n}";

            // Prepend: var a = _lf_elem_M.item0, var b = _lf_elem_M.item1, ??
            var bindStmts =
                new List<Statement>(capacity: eachStmt.VariablePattern.Bindings.Count + 1);
            for (int i = 0; i < eachStmt.VariablePattern.Bindings.Count; i++)
            {
                DestructuringBinding binding = eachStmt.VariablePattern.Bindings[index: i];
                string bindName = binding.BindingName ?? binding.MemberVariableName ?? $"_lf_b{i}";
                if (bindName == "_")
                {
                    continue;
                }

                bindStmts.Add(item: new DeclarationStatement(Declaration: new VariableDeclaration(
                        Name: bindName,
                        Type: null,
                        Initializer: new MemberExpression(
                            Object: new IdentifierExpression(Name: elemName, Location: loc),
                            MemberName: $"item{i}",
                            Location: loc),
                        Visibility: VisibilityModifier.Secret,
                        Location: loc),
                    Location: loc));
            }

            if (loweredBody is BlockStatement bodyBlock)
            {
                bindStmts.AddRange(collection: bodyBlock.Statements);
                return (bodyBlock with { Statements = bindStmts }, elemName);
            }

            bindStmts.Add(item: loweredBody);
            return (new BlockStatement(Statements: bindStmts, Location: loc), elemName);
        }

        // Simple variable or discard
        string? elseVarName = eachStmt.Variable == "_"
            ? null
            : eachStmt.Variable;
        return (loweredBody, elseVarName);
    }

    /// <summary>
    /// Builds the for-else lowering: an exhausted flag set inside the <c>None</c> arm, and an
    /// <c>if _lf_exhausted_N { alt }</c> check after the loop.
    /// </summary>
    private readonly record struct ForElseNames(
        string? ElseVarName,
        string ExhaustedName,
        string? IterationSourceName);

    private static BlockStatement BuildForElse(Statement elseBranchLowered, Expression tryNextCall,
        Statement elseBody, Statement iterVarStmt, ForElseNames names,
        SourceLocation loc)
    {
        string? elseVarName = names.ElseVarName;
        string exhaustedName = names.ExhaustedName;
        string? iterationSourceName = names.IterationSourceName;
        // For-else: set exhausted flag, then break
        Statement noneBody = new BlockStatement(Statements:
            [
                new AssignmentStatement(
                    Target: new IdentifierExpression(Name: exhaustedName, Location: loc),
                    Value: new LiteralExpression(Value: true,
                        LiteralType: TokenType.True,
                        Location: loc),
                    Location: loc),
                new BreakStatement(Location: loc)
            ],
            Location: loc);

        var noneClause = new WhenClause(Pattern: new NonePattern(Location: loc),
            Body: noneBody,
            Location: loc);
        var elseClause = new WhenClause(
            Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
            Body: elseBody,
            Location: loc);

        var whenStmt = new WhenStatement(Expression: tryNextCall,
            Clauses: [noneClause, elseClause],
            Location: loc);
        var loopStmt =
            new LoopStatement(Body: new BlockStatement(Statements: [whenStmt], Location: loc),
                Location: loc)
            {
                IsIteratorEachLoop = true, IterationSourceName = iterationSourceName
            };

        // var _lf_exhausted_N: Bool = false
        Statement exhaustedVarStmt = new DeclarationStatement(Declaration: new VariableDeclaration(
                Name: exhaustedName,
                Type: new TypeExpression(Name: "Bool", GenericArguments: null, Location: loc),
                Initializer: new LiteralExpression(Value: false,
                    LiteralType: TokenType.False,
                    Location: loc),
                Visibility: VisibilityModifier.Secret,
                Location: loc),
            Location: loc);

        // if _lf_exhausted_N { alt }
        Statement exhaustionCheck = new IfStatement(
            Condition: new IdentifierExpression(Name: exhaustedName, Location: loc),
            ThenStatement: elseBranchLowered,
            ElseStatement: null,
            Location: loc);

        return new BlockStatement(Statements:
            [exhaustedVarStmt, iterVarStmt, loopStmt, exhaustionCheck],
            Location: loc);
    }

    /// <summary>
    /// Builds the plain for lowering (no else branch): the <c>None</c> arm breaks directly.
    /// </summary>
    private static BlockStatement BuildPlainFor(Expression tryNextCall, Statement elseBody,
        string? elseVarName, Statement iterVarStmt, string? iterationSourceName,
        SourceLocation loc)
    {
        // Plain for (no else branch)
        Statement noneBody = new BlockStatement(Statements:
            [new BreakStatement(Location: loc)],
            Location: loc);

        var noneClause = new WhenClause(Pattern: new NonePattern(Location: loc),
            Body: noneBody,
            Location: loc);
        var elseClause = new WhenClause(
            Pattern: new ElsePattern(VariableName: elseVarName, Location: loc),
            Body: elseBody,
            Location: loc);

        var whenStmt = new WhenStatement(Expression: tryNextCall,
            Clauses: [noneClause, elseClause],
            Location: loc);
        var loopStmt =
            new LoopStatement(Body: new BlockStatement(Statements: [whenStmt], Location: loc),
                Location: loc)
            {
                IsIteratorEachLoop = true, IterationSourceName = iterationSourceName
            };

        return new BlockStatement(Statements: [iterVarStmt, loopStmt], Location: loc);
    }

    /// <summary>
    /// Substitutes a type with a concrete generic owner's type arguments (e.g. the generic-def
    /// <c>iter</c> return <c>EnumerateEmitter[T, S/Iter]</c> for owner
    /// <c>EnumerateIterator[Text, List[Text]]</c> → <c>EnumerateEmitter[Text, ListEmitter[Text]]</c>).
    /// Resolves associated-type projections via <see cref="RecordTypeInfo.SubstituteType"/>.
    /// </summary>
    private static TypeInfo SubstituteForConcreteOwner(TypeInfo type, TypeInfo owner)
    {
        TypeInfo? def = owner switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            _ => null
        };
        if (def?.GenericParameters is { } defParams && owner.TypeArguments is { } args &&
            defParams.Count == args.Count && defParams.Count > 0)
        {
            var subs = new Dictionary<string, TypeInfo>();
            for (int i = 0; i < defParams.Count; i++)
            {
                subs[key: defParams[index: i]] = args[index: i];
            }

            return RecordTypeInfo.SubstituteType(type: type, substitution: subs);
        }

        return type;
    }

    /// <summary>
    /// Lowers control flow in all synthesized variant bodies in
    /// <see cref="DesugaringContext.VariantBodies"/>.
    /// Called from <see cref="DesugaringPipeline.RunGlobal"/> after variant bodies are generated,
    /// so that bodies copied from unlowered stdlib originals get their WhileStatement nodes
    /// converted to LoopStatement before OperatorLoweringPass and codegen see them.
    /// </summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            if (ctx.RestoredVariantKeys.Contains(item: key))
            {
                continue; // already lowered at snapshot capture
            }

            Statement body = ctx.VariantBodies[key: key];
            Statement lowered = LowerStatement(stmt: body);
            if (!ReferenceEquals(objA: lowered, objB: body))
            {
                ctx.VariantBodies[key: key] = lowered;
            }
        }
    }

    /// <summary>
    /// Lower EachStatement/DestructuringStatement etc. in monomorphized bodies that GMP
    /// produced from stdlib originals (which never went through Phase 6 desugaring).
    /// Notable consumer: <see cref="Compiler.Instantiation.Passes.ProtocolDefaultImplLoweringPass"/>,
    /// which clones stdlib protocol-default-impl bodies (e.g. <c>Iterable[Text].join</c>) into
    /// per-implementer routines; those clones contain raw `for` loops that codegen rejects.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        IDictionary<string, Instantiation.MonomorphizedBody> bodies)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            Instantiation.MonomorphizedBody mb = bodies[key: key];
            Statement lowered = LowerStatement(stmt: mb.Ast.Body);
            if (!ReferenceEquals(objA: lowered, objB: mb.Ast.Body))
            {
                bodies[key: key] = mb with { Ast = mb.Ast with { Body = lowered } };
            }
        }
    }
}
