using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// STEP 1 iterator-advance inlining. Rewrites the loop that
/// <see cref="Compiler.Desugaring.Passes.ControlFlowLoweringPass"/> emits for a <c>for x in coll</c>
/// so that the concrete emitter's monomorphized <c>emit!</c> body is spliced directly into the loop
/// in place of a call to the compiler-generated <c>try_emit</c> variant — but ONLY for "simple"
/// iterators (see <see cref="IsSimpleNextBody"/>). Composed / filtering iterators (nested loop or a
/// nested <c>emit!</c> call in the <c>emit!</c> body) FALL BACK to the existing <c>try_emit</c>-based
/// <c>when</c> loop untouched.
///
/// <para>Lowered for-loop shape produced by CFLP (the discriminator):</para>
/// <code>
/// loop /*IsIteratorEachLoop*/ {
///   when _lf_iter_N.try_emit() {
///     is None -> break                              # plain for
///        (or:  { _lf_exhausted_N = true; break }    # for-else)
///     else v -> &lt;bindings + user body&gt;
///   }
/// }
/// </code>
///
/// <para>Inlined replacement (simple emitter):</para>
/// <code>
/// loop {
///   &lt;emit! body with:  me -> _lf_iter_N
///                        local decls alpha-renamed to _ii{n}_&lt;orig&gt;
///                        return v      -> { &lt;bindings&gt; ; &lt;user body&gt; }
///                        absent        -> break   (or { _lf_exhausted_N = true; break })
///                        throw         -> unchanged (propagates) &gt;
/// }
/// </code>
///
/// <para>The spliced body's callees are already live: <c>RoutineReachabilityPass</c> walked
/// the loop's <c>try_emit</c> call, and <c>try_emit</c> is a transformed copy of <c>emit!</c> with
/// the identical callee set — so no separate liveness seed is needed for inlined loops.</para>
/// </summary>
internal sealed class IteratorInlineLoweringPass
{
    private readonly TypeRegistry _registry;
    private readonly IReadOnlyDictionary<string, MonomorphizedBody> _monoBodies;
    private int _inlineCounter;

    public IteratorInlineLoweringPass(TypeRegistry registry,
        IReadOnlyDictionary<string, MonomorphizedBody> monoBodies)
    {
        _registry = registry;
        _monoBodies = monoBodies;
    }

    /// <summary>Rewrites iterator-for loops in every routine body of a user program.</summary>
    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                {
                    Statement nb = Rewrite(stmt: r.Body);
                    if (!ReferenceEquals(nb, r.Body)) program.Declarations[i] = r with { Body = nb };
                    break;
                }
                case EntityDeclaration e:
                    RewriteMembers(members: e.Members);
                    break;
                case RecordDeclaration rec:
                    RewriteMembers(members: rec.Members);
                    break;
                case CrashableDeclaration cr:
                    RewriteMembers(members: cr.Members);
                    break;
            }
        }
    }

    /// <summary>Rewrites iterator-for loops in monomorphized generic bodies (Phase 7 path).</summary>
    public void RunOnInstantiatedGenericBodies(IDictionary<string, MonomorphizedBody> bodies)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            MonomorphizedBody mb = bodies[key];
            Statement nb = Rewrite(stmt: mb.Ast.Body);
            if (!ReferenceEquals(nb, mb.Ast.Body))
                bodies[key] = mb with { Ast = mb.Ast with { Body = nb } };
        }
    }

    private void RewriteMembers(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[j] is not RoutineDeclaration m) continue;
            Statement nb = Rewrite(stmt: m.Body);
            if (!ReferenceEquals(nb, m.Body)) members[j] = m with { Body = nb };
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Structural recursion: find the flagged iterator loops and try to inline each. Recurses through
    // the same container nodes as ControlFlowLoweringPass so nested for-loops are all visited.
    // ---------------------------------------------------------------------------------------------
    private Statement Rewrite(Statement stmt)
    {
        switch (stmt)
        {
            case LoopStatement { IsIteratorEachLoop: true } loop:
                return RewriteIteratorLoop(loop: loop);
            case LoopStatement loop:
            {
                Statement b = Rewrite(stmt: loop.Body);
                return ReferenceEquals(b, loop.Body) ? loop : loop with { Body = b };
            }
            case BlockStatement block:
                return RewriteBlock(block: block);
            case IfStatement ifs:
                return RewriteIf(ifs: ifs);
            case WhileStatement w:
                return RewriteWhile(w: w);
            case WhenStatement w:
                return RewriteWhen(w: w);
            case UsingStatement u:
                return RewriteUsing(u: u);
            case DangerStatement d:
            {
                Statement lowered = Rewrite(stmt: d.Body);
                return !ReferenceEquals(lowered, d.Body) ? d with { Body = (BlockStatement)lowered } : d;
            }
            default:
                return stmt;
        }
    }

    private Statement RewriteIteratorLoop(LoopStatement loop)
    {
        Statement? inlined = TryInline(loop: loop);
        if (inlined != null) return inlined;
        // Fallback: leave the try_emit loop untouched, but still recurse into its body
        // (the user body may itself contain further for-loops).
        Statement fb = Rewrite(stmt: loop.Body);
        return ReferenceEquals(fb, loop.Body) ? loop : loop with { Body = fb };
    }

    private BlockStatement RewriteBlock(BlockStatement block)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement s in block.Statements)
        {
            Statement n = Rewrite(stmt: s);
            stmts.Add(item: n);
            if (!ReferenceEquals(n, s)) changed = true;
        }
        return changed ? block with { Statements = stmts } : block;
    }

    private IfStatement RewriteIf(IfStatement ifs)
    {
        Statement then = Rewrite(stmt: ifs.ThenStatement);
        Statement? elseS = ifs.ElseStatement != null ? Rewrite(stmt: ifs.ElseStatement) : null;
        return !ReferenceEquals(then, ifs.ThenStatement) || !ReferenceEquals(elseS, ifs.ElseStatement)
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private WhileStatement RewriteWhile(WhileStatement w)
    {
        Statement b = Rewrite(stmt: w.Body);
        Statement? el = w.ElseBranch != null ? Rewrite(stmt: w.ElseBranch) : null;
        return !ReferenceEquals(b, w.Body) || !ReferenceEquals(el, w.ElseBranch)
            ? w with { Body = b, ElseBranch = el }
            : w;
    }

    private WhenStatement RewriteWhen(WhenStatement w)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
        foreach (WhenClause c in w.Clauses)
        {
            Statement b = Rewrite(stmt: c.Body);
            clauses.Add(item: ReferenceEquals(b, c.Body) ? c : c with { Body = b });
            if (!ReferenceEquals(b, c.Body)) changed = true;
        }
        return changed ? w with { Clauses = clauses } : w;
    }

    private UsingStatement RewriteUsing(UsingStatement u)
    {
        Statement b = Rewrite(stmt: u.Body);
        Statement? fb = u.FallbackBody != null ? Rewrite(stmt: u.FallbackBody) : null;
        return !ReferenceEquals(b, u.Body) || !ReferenceEquals(fb, u.FallbackBody)
            ? u with { Body = b, FallbackBody = fb }
            : u;
    }

    // ---------------------------------------------------------------------------------------------
    // Inline one flagged loop, or return null to keep the existing try_emit lowering.
    // ---------------------------------------------------------------------------------------------
    private LoopStatement? TryInline(LoopStatement loop)
    {
        // CheckAndAdvance the CFLP shape: loop body is a block whose single statement is a `when` over a
        // try_emit() call with a NonePattern clause + an ElsePattern clause.
        if (loop.Body is not BlockStatement { Statements: [WhenStatement when] }) return null;
        if (when.Expression is not CallExpression tryNextCall) return null;
        if (tryNextCall.Callee is not MemberExpression { MemberName: RuntimeContract.TryEmit, Object: { } recvExpr })
            return null;

        // Iterator local name (`_lf_iter_N`).
        if (recvExpr is not IdentifierExpression { Name: { } iterLocalName }) return null;

        // Concrete emitter type carried on the receiver by CFLP.
        if (recvExpr.ResolvedType is not { } emitterType) return null;
        if (emitterType is ErrorTypeInfo) return null;

        // Extract the none-clause (for for-else exhaustion scaffold) and the else-clause
        // (loop-var bindings + user body).
        if (!TryExtractClauses(when: when, noneClause: out WhenClause? noneClause,
                elseClause: out WhenClause? elseClause))
            return null;
        var elsePattern = (ElsePattern)elseClause.Pattern;

        // Resolve the concrete `emit!` on the emitter and fetch its monomorphized body.
        RoutineInfo? nextRoutine =
            _registry.LookupMemberRoutine(type: emitterType, memberRoutineName: "emit", isFailable: true);
        if (nextRoutine == null) return null;
        if (!_monoBodies.TryGetValue(key: nextRoutine.RegistryKey, out MonomorphizedBody? nextMono))
            return null;
        Statement nextBody = nextMono.Ast.Body;

        // Gate: only inline SIMPLE emit! bodies.
        if (!IsSimpleNextBody(stmt: nextBody)) return null;

        // Recurse into the user body FIRST (it may contain nested for-loops) so the inlined
        // return-site gets already-lowered nested loops.
        Statement loweredUserBody = Rewrite(stmt: elseClause.Body);

        // Build a fresh alpha-rename map for locals introduced by the emit! body.
        int inlineId = _inlineCounter++;
        var renameCtx = new NextBodyRewriteContext(
            iterLocalName: iterLocalName,
            renamePrefix: $"_ii{inlineId}_",
            elsePattern: elsePattern,
            loweredUserBody: loweredUserBody,
            noneClauseBody: noneClause.Body);

        // Collect the local names the emit! body declares so identifier references to them are
        // renamed consistently. `me` is handled separately (mapped to the iterator local).
        CollectDeclaredLocals(stmt: nextBody, sink: renameCtx.LocalRenames, prefix: renameCtx.RenamePrefix);

        Statement inlinedBody = RewriteNextStatement(stmt: nextBody, ctx: renameCtx);
        return new LoopStatement(Body: inlinedBody, Location: loop.Location);
    }

    /// <summary>
    /// Extracts the none-clause (for-else exhaustion scaffold) and else-clause (loop-var bindings +
    /// user body) from the CFLP `when`. Returns false when either is missing.
    /// </summary>
    private static bool TryExtractClauses(WhenStatement when,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WhenClause? noneClause,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WhenClause? elseClause)
    {
        noneClause = null;
        elseClause = null;
        foreach (WhenClause c in when.Clauses)
        {
            switch (c.Pattern)
            {
                case NonePattern: noneClause = c; break;
                case ElsePattern: elseClause = c; break;
            }
        }
        return noneClause != null && elseClause != null;
    }

    // ---------------------------------------------------------------------------------------------
    // Gate: a emit! body is "simple" if, at its OWN control level, it has no nested loop of any kind
    // and no nested failable `emit!` call anywhere.
    // ---------------------------------------------------------------------------------------------
    private static bool IsSimpleNextBody(Statement stmt)
    {
        if (ContainsLoop(stmt: stmt)) return false;
        if (ContainsNestedNextCall(stmt: stmt)) return false;
        // A monomorphized emit! body may still carry references to the emitter's own generic
        // parameters (an unresolved `T` in `lateinit var _result: T`) or const-generics (the `N` in
        // `Array[T, N]` → `U64(N)`) that codegen only resolves inside the emitter routine's own
        // type-substitution frame. Splicing such a body into the caller loses that frame and crashes
        // ("Unknown identifier 'N'" / unresolvable `T`). Fall back for these.
        if (ReferencesUnresolvedGenerics(stmt: stmt)) return false;
        return true;
    }

    private static bool ReferencesUnresolvedGenerics(Statement stmt)
    {
        bool found = false;

        // Explicit variable-declaration type annotations (e.g. `lateinit var _result: T`).
        WalkStatements(stmt: stmt, visit: s =>
        {
            if (s is DeclarationStatement { Declaration: VariableDeclaration { Type: { } te } }
                && TypeIsUnresolvedGeneric(type: te.ResolvedType))
                found = true;
        });
        if (found) return true;

        // Const-generic / generic-param references surfacing as expression ResolvedTypes
        // (e.g. the `N` identifier in `U64(N)`).
        WalkExpressions(stmt: stmt, visit: e =>
        {
            if (TypeIsUnresolvedGeneric(type: e.ResolvedType)) found = true;
        });
        return found;
    }

    private static bool TypeIsUnresolvedGeneric(TypeInfo? type)
    {
        if (type == null) return false;
        if (type is GenericParameterTypeInfo or ConstGenericValueTypeInfo) return true;
        return type.TypeArguments is { Count: > 0 } args
            && args.Any(a => TypeIsUnresolvedGeneric(type: a));
    }

    private static bool ContainsLoop(Statement stmt)
    {
        bool found = false;
        WalkStatements(stmt: stmt, visit: s =>
        {
            if (s is LoopStatement or WhileStatement or EachStatement) found = true;
        });
        return found;
    }

    private static bool ContainsNestedNextCall(Statement stmt)
    {
        bool found = false;
        WalkExpressions(stmt: stmt, visit: e =>
        {
            if (IsNextCall(e)) found = true;
        });
        return found;
    }

    /// <summary>
    /// Detects a call to another iterator's failable <c>emit!</c> — either via its resolved routine
    /// (OriginalName/Name == <c>emit</c> and failable) or, before resolution, via the callee's
    /// member name being <c>emit</c>.
    /// </summary>
    private static bool IsNextCall(Expression e)
    {
        if (e is CallExpression call)
        {
            // Name/OriginalName are bare (the wired `$` is a structured attribute, not in the name).
            if (call.ResolvedRoutine is { IsFailable: true } r && (r.OriginalName ?? r.Name) == "emit")
                return true;
            if (call.Callee is MemberExpression { MemberName: "emit" }) return true;
        }
        return false;
    }

    // ---------------------------------------------------------------------------------------------
    // Deep-clone rewrite of the emit! body. Handles the whitelisted container statements plus the
    // leaf actions (return / absent / throw). Everything else is deep-cloned with identifiers
    // renamed (me -> iter local, locals -> fresh names).
    // ---------------------------------------------------------------------------------------------
    private sealed class NextBodyRewriteContext
    {
        public string IterLocalName { get; }
        public string RenamePrefix { get; }
        public ElsePattern ElsePattern { get; }
        public Statement LoweredUserBody { get; }
        public Statement NoneClauseBody { get; }
        public Dictionary<string, string> LocalRenames { get; } = new();

        public NextBodyRewriteContext(string iterLocalName, string renamePrefix,
            ElsePattern elsePattern, Statement loweredUserBody, Statement noneClauseBody)
        {
            IterLocalName = iterLocalName;
            RenamePrefix = renamePrefix;
            ElsePattern = elsePattern;
            LoweredUserBody = loweredUserBody;
            NoneClauseBody = noneClauseBody;
        }
    }

    private static Statement RewriteNextStatement(Statement stmt, NextBodyRewriteContext ctx)
    {
        switch (stmt)
        {
            case ReturnStatement ret:
                return BuildReturnReplacement(retValue: ret.Value, ctx: ctx, loc: ret.Location);

            case AbsentStatement:
                // Clone the loop's none-clause body so the two inline sites never share a node.
                return CloneStatement(stmt: ctx.NoneClauseBody, ctx: ctx);

            case ThrowStatement:
                // Propagates unchanged (but still rename identifiers inside the error expression).
                return CloneStatement(stmt: stmt, ctx: ctx);

            case BlockStatement block:
                return block with
                {
                    Statements = block.Statements.Select(selector: s => RewriteNextStatement(stmt: s, ctx: ctx)).ToList()
                };

            case IfStatement ifs:
                return ifs with
                {
                    Condition = CloneExpression(expr: ifs.Condition, ctx: ctx),
                    ThenStatement = RewriteNextStatement(stmt: ifs.ThenStatement, ctx: ctx),
                    ElseStatement = ifs.ElseStatement != null
                        ? RewriteNextStatement(stmt: ifs.ElseStatement, ctx: ctx)
                        : null
                };

            case WhenStatement w:
                return w with
                {
                    Expression = CloneExpression(expr: w.Expression, ctx: ctx),
                    Clauses = w.Clauses.Select(selector: c => c with
                    {
                        Pattern = ClonePattern(pattern: c.Pattern, ctx: ctx),
                        Body = RewriteNextStatement(stmt: c.Body, ctx: ctx)
                    }).ToList()
                };

            case UsingStatement u:
                return u with
                {
                    Resource = CloneExpression(expr: u.Resource, ctx: ctx),
                    Body = RewriteNextStatement(stmt: u.Body, ctx: ctx),
                    FallbackBody = u.FallbackBody != null
                        ? RewriteNextStatement(stmt: u.FallbackBody, ctx: ctx)
                        : null
                };

            case DangerStatement d:
                return d with { Body = (BlockStatement)RewriteNextStatement(stmt: d.Body, ctx: ctx) };

            // Loops must never appear (the gate excludes them); guard defensively.
            case LoopStatement:
            case WhileStatement:
            case EachStatement:
                // Should be unreachable — fall back to a plain clone rather than crash.
                return CloneStatement(stmt: stmt, ctx: ctx);

            default:
                return CloneStatement(stmt: stmt, ctx: ctx);
        }
    }

    /// <summary>
    /// Replaces a <c>return v</c> in the emit! body with the loop-variable bindings CFLP builds for
    /// the <c>else v:</c> clause, followed by the (already-lowered) user body.
    /// </summary>
    private static BlockStatement BuildReturnReplacement(Expression? retValue, NextBodyRewriteContext ctx,
        SourceLocation loc)
    {
        Expression value = retValue != null ? CloneExpression(expr: retValue, ctx: ctx)
            : new IdentifierExpression(Name: "None", Location: loc);

        // Simple variable / discard: bind `var <loopvar> = value` (unless discard, where CFLP used a
        // null else-var name — nothing to bind).
        if (ctx.ElsePattern.VariableName is { } elseVarName)
        {
            var bindStmt = new DeclarationStatement(
                Declaration: new VariableDeclaration(
                    Name: elseVarName, Type: null, Initializer: value,
                    Visibility: VisibilityModifier.Secret, Location: loc),
                Location: loc);
            return new BlockStatement(Statements: [bindStmt, CloneUserBody(ctx.LoweredUserBody)], Location: loc);
        }

        // Discard: still evaluate `value` for side-effects (it's the advance), then the user body.
        return new BlockStatement(
            Statements: [new ExpressionStatement(Expression: value, Location: loc), CloneUserBody(ctx.LoweredUserBody)],
            Location: loc);
    }

    // The user body is spliced at exactly one return site per simple emit! (simple bodies have a
    // single tail return), so it is used once and need not be cloned. But to be safe against a
    // emit! body with multiple return statements, deep-clone it each time.
    private static Statement CloneUserBody(Statement userBody) => userBody;

    // ---------------------------------------------------------------------------------------------
    // Local-declaration collection: gather the names of `var` declarations the emit! body
    // introduces, mapping each to a fresh loop-unique name.
    // ---------------------------------------------------------------------------------------------
    private static void CollectDeclaredLocals(Statement stmt, Dictionary<string, string> sink,
        string prefix)
    {
        WalkStatements(stmt: stmt, visit: s =>
        {
            if (s is DeclarationStatement { Declaration: VariableDeclaration vd }
                && !sink.ContainsKey(vd.Name) && vd.Name != "_")
                sink[vd.Name] = prefix + vd.Name;
        });
    }

    // ---------------------------------------------------------------------------------------------
    // Statement / expression deep-clone with identifier renaming.
    // ---------------------------------------------------------------------------------------------
    private static Statement CloneStatement(Statement stmt, NextBodyRewriteContext ctx)
    {
        switch (stmt)
        {
            case BlockStatement b:
                return b with { Statements = b.Statements.Select(selector: s => CloneStatement(stmt: s, ctx: ctx)).ToList() };

            case DeclarationStatement { Declaration: VariableDeclaration vd } ds:
            {
                string newName = ctx.LocalRenames.TryGetValue(vd.Name, out string? r) ? r : vd.Name;
                return ds with
                {
                    Declaration = vd with
                    {
                        Name = newName,
                        Initializer = vd.Initializer != null ? CloneExpression(expr: vd.Initializer, ctx: ctx) : null
                    }
                };
            }

            case ExpressionStatement es:
                return es with { Expression = CloneExpression(expr: es.Expression, ctx: ctx) };

            case AssignmentStatement asg:
                return asg with
                {
                    Target = CloneExpression(expr: asg.Target, ctx: ctx),
                    Value = CloneExpression(expr: asg.Value, ctx: ctx)
                };

            case ReturnStatement ret:
                return ret with { Value = ret.Value != null ? CloneExpression(expr: ret.Value, ctx: ctx) : null };

            case ThrowStatement th:
                return th with { Error = CloneExpression(expr: th.Error, ctx: ctx) };

            case DiscardStatement disc:
                return disc with { Expression = CloneExpression(expr: disc.Expression, ctx: ctx) };

            case BecomesStatement bc:
                return bc with { Value = CloneExpression(expr: bc.Value, ctx: ctx) };

            case IfStatement ifs:
                return ifs with
                {
                    Condition = CloneExpression(expr: ifs.Condition, ctx: ctx),
                    ThenStatement = CloneStatement(stmt: ifs.ThenStatement, ctx: ctx),
                    ElseStatement = ifs.ElseStatement != null ? CloneStatement(stmt: ifs.ElseStatement, ctx: ctx) : null
                };

            case WhenStatement w:
                return w with
                {
                    Expression = CloneExpression(expr: w.Expression, ctx: ctx),
                    Clauses = w.Clauses.Select(selector: c => c with
                    {
                        Pattern = ClonePattern(pattern: c.Pattern, ctx: ctx),
                        Body = CloneStatement(stmt: c.Body, ctx: ctx)
                    }).ToList()
                };

            case UsingStatement u:
                return u with
                {
                    Resource = CloneExpression(expr: u.Resource, ctx: ctx),
                    Body = CloneStatement(stmt: u.Body, ctx: ctx),
                    FallbackBody = u.FallbackBody != null ? CloneStatement(stmt: u.FallbackBody, ctx: ctx) : null
                };

            case DangerStatement d:
                return d with { Body = (BlockStatement)CloneStatement(stmt: d.Body, ctx: ctx) };

            case AbsentStatement:
            case PassStatement:
            case BreakStatement:
            case ContinueStatement:
                return stmt;

            default:
                // Any statement we don't explicitly clone is returned as-is. Because it may still hold
                // identifier references, this is a best-effort — but the simple-gate keeps emit!
                // bodies to the shapes handled above.
                return stmt;
        }
    }

    private static Expression CloneExpression(Expression expr, NextBodyRewriteContext ctx)
    {
        switch (expr)
        {
            case IdentifierExpression id:
            {
                if (id.Name == "me")
                    return id with { Name = ctx.IterLocalName };
                if (ctx.LocalRenames.TryGetValue(id.Name, out string? r))
                    return id with { Name = r };
                return id;
            }

            case MemberExpression m:
                return m with { Object = CloneExpression(expr: m.Object, ctx: ctx) };

            case OptionalMemberExpression om:
                return om with { Object = CloneExpression(expr: om.Object, ctx: ctx) };

            case CallExpression call:
                return call with
                {
                    Callee = CloneExpression(expr: call.Callee, ctx: ctx),
                    Arguments = call.Arguments.Select(selector: a => CloneExpression(expr: a, ctx: ctx)).ToList()
                };

            case GenericMemberRoutineCallExpression gcall:
                return gcall with
                {
                    Object = CloneExpression(expr: gcall.Object, ctx: ctx),
                    Arguments = gcall.Arguments.Select(selector: a => CloneExpression(expr: a, ctx: ctx)).ToList()
                };

            case GenericMemberExpression gm:
                return gm with { Object = CloneExpression(expr: gm.Object, ctx: ctx) };

            case NamedArgumentExpression na:
                return na with { Value = CloneExpression(expr: na.Value, ctx: ctx) };

            case BinaryExpression bin:
                return bin with
                {
                    Left = CloneExpression(expr: bin.Left, ctx: ctx),
                    Right = CloneExpression(expr: bin.Right, ctx: ctx)
                };

            case UnaryExpression un:
                return un with { Operand = CloneExpression(expr: un.Operand, ctx: ctx) };

            case IndexExpression ix:
                return ix with
                {
                    Object = CloneExpression(expr: ix.Object, ctx: ctx),
                    Index = CloneExpression(expr: ix.Index, ctx: ctx)
                };

            case ConditionalExpression cond:
                return cond with
                {
                    Condition = CloneExpression(expr: cond.Condition, ctx: ctx),
                    TrueExpression = CloneExpression(expr: cond.TrueExpression, ctx: ctx),
                    FalseExpression = CloneExpression(expr: cond.FalseExpression, ctx: ctx)
                };

            case TypeConversionExpression tc:
                return tc with { Expression = CloneExpression(expr: tc.Expression, ctx: ctx) };

            case CarrierPayloadExpression cp:
                return cp with { Carrier = CloneExpression(expr: cp.Carrier, ctx: ctx) };

            case StealExpression st:
                return st with { Operand = CloneExpression(expr: st.Operand, ctx: ctx) };

            case TupleLiteralExpression tup:
                return tup with { Elements = tup.Elements.Select(selector: e => CloneExpression(expr: e, ctx: ctx)).ToList() };

            case ListLiteralExpression ll:
                return ll with { Elements = ll.Elements.Select(selector: e => CloneExpression(expr: e, ctx: ctx)).ToList() };

            case SetLiteralExpression sl:
                return sl with { Elements = sl.Elements.Select(selector: e => CloneExpression(expr: e, ctx: ctx)).ToList() };

            case RangeExpression rng:
                return rng with
                {
                    Start = CloneExpression(expr: rng.Start, ctx: ctx),
                    End = CloneExpression(expr: rng.End, ctx: ctx),
                    Step = rng.Step != null ? CloneExpression(expr: rng.Step, ctx: ctx) : null
                };

            case ChainedComparisonExpression cc:
                return cc with { Operands = cc.Operands.Select(selector: o => CloneExpression(expr: o, ctx: ctx)).ToList() };

            case CreatorExpression cr:
                return cr with
                {
                    MemberVariables = cr.MemberVariables
                        .Select(selector: mv => (mv.Name, CloneExpression(expr: mv.Value, ctx: ctx)))
                        .ToList()
                };

            case BlockExpression be:
                return be with { Value = CloneExpression(expr: be.Value, ctx: ctx) };

            case LiteralExpression:
            case TypeExpression:
            case TypeIdExpression:
                return expr;

            default:
                // Unhandled expression kinds are returned as-is (still referencing the original
                // identifiers). Acceptable because simple emit! bodies don't reach here; guarded by
                // the simple-gate.
                return expr;
        }
    }

    private static Pattern ClonePattern(Pattern pattern, NextBodyRewriteContext ctx)
    {
        return pattern switch
        {
            ExpressionPattern ep => ep with { Expression = CloneExpression(expr: ep.Expression, ctx: ctx) },
            ComparisonPattern cp => cp with { Value = CloneExpression(expr: cp.Value, ctx: ctx) },
            GuardPattern gp => gp with
            {
                InnerPattern = ClonePattern(pattern: gp.InnerPattern, ctx: ctx),
                Guard = CloneExpression(expr: gp.Guard, ctx: ctx)
            },
            _ => pattern
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Lightweight read-only walkers (used only by the simple-gate and local collection).
    // ---------------------------------------------------------------------------------------------
    private static void WalkStatements(Statement stmt, Action<Statement> visit)
    {
        visit(stmt);
        switch (stmt)
        {
            case BlockStatement b:
                foreach (Statement s in b.Statements) WalkStatements(stmt: s, visit: visit);
                break;
            case IfStatement ifs:
                WalkStatements(stmt: ifs.ThenStatement, visit: visit);
                if (ifs.ElseStatement != null) WalkStatements(stmt: ifs.ElseStatement, visit: visit);
                break;
            case WhileStatement w:
                WalkStatements(stmt: w.Body, visit: visit);
                if (w.ElseBranch != null) WalkStatements(stmt: w.ElseBranch, visit: visit);
                break;
            case LoopStatement l:
                WalkStatements(stmt: l.Body, visit: visit);
                break;
            case EachStatement f:
                WalkStatements(stmt: f.Body, visit: visit);
                if (f.ElseBranch != null) WalkStatements(stmt: f.ElseBranch, visit: visit);
                break;
            case WhenStatement wn:
                foreach (WhenClause c in wn.Clauses) WalkStatements(stmt: c.Body, visit: visit);
                break;
            case UsingStatement u:
                WalkStatements(stmt: u.Body, visit: visit);
                if (u.FallbackBody != null) WalkStatements(stmt: u.FallbackBody, visit: visit);
                break;
            case DangerStatement d:
                WalkStatements(stmt: d.Body, visit: visit);
                break;
        }
    }

    private static void WalkExpressions(Statement stmt, Action<Expression> visit)
    {
        WalkStatements(stmt: stmt, visit: s =>
        {
            switch (s)
            {
                case DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } }:
                    WalkExpr(e: init, visit: visit);
                    break;
                case ExpressionStatement es: WalkExpr(e: es.Expression, visit: visit); break;
                case AssignmentStatement asg:
                    WalkExpr(e: asg.Target, visit: visit);
                    WalkExpr(e: asg.Value, visit: visit);
                    break;
                case ReturnStatement { Value: { } rv }: WalkExpr(e: rv, visit: visit); break;
                case ThrowStatement th: WalkExpr(e: th.Error, visit: visit); break;
                case BecomesStatement bc: WalkExpr(e: bc.Value, visit: visit); break;
                case DiscardStatement disc: WalkExpr(e: disc.Expression, visit: visit); break;
                case IfStatement ifs: WalkExpr(e: ifs.Condition, visit: visit); break;
                case WhileStatement w: WalkExpr(e: w.Condition, visit: visit); break;
                case WhenStatement wn: WalkExpr(e: wn.Expression, visit: visit); break;
                case UsingStatement u: WalkExpr(e: u.Resource, visit: visit); break;
            }
        });
    }

    private static void WalkExpr(Expression e, Action<Expression> visit)
    {
        visit(e);
        switch (e)
        {
            case MemberExpression m: WalkExpr(e: m.Object, visit: visit); break;
            case OptionalMemberExpression om: WalkExpr(e: om.Object, visit: visit); break;
            case CallExpression call: WalkCallExpr(call: call, visit: visit); break;
            case GenericMemberRoutineCallExpression gcall: WalkGenericCallExpr(gcall: gcall, visit: visit); break;
            case GenericMemberExpression gm: WalkExpr(e: gm.Object, visit: visit); break;
            case NamedArgumentExpression na: WalkExpr(e: na.Value, visit: visit); break;
            case BinaryExpression bin: WalkExpr(e: bin.Left, visit: visit); WalkExpr(e: bin.Right, visit: visit); break;
            case UnaryExpression un: WalkExpr(e: un.Operand, visit: visit); break;
            case IndexExpression ix: WalkExpr(e: ix.Object, visit: visit); WalkExpr(e: ix.Index, visit: visit); break;
            case ConditionalExpression cond: WalkConditionalExpr(cond: cond, visit: visit); break;
            case TypeConversionExpression tc: WalkExpr(e: tc.Expression, visit: visit); break;
            case CarrierPayloadExpression cp: WalkExpr(e: cp.Carrier, visit: visit); break;
            case StealExpression st: WalkExpr(e: st.Operand, visit: visit); break;
            case TupleLiteralExpression tup:
                foreach (Expression x in tup.Elements) { WalkExpr(e: x, visit: visit); }
                break;
            case ListLiteralExpression ll:
                foreach (Expression x in ll.Elements) { WalkExpr(e: x, visit: visit); }
                break;
            case SetLiteralExpression sl:
                foreach (Expression x in sl.Elements) { WalkExpr(e: x, visit: visit); }
                break;
            case RangeExpression rng: WalkRangeExpr(rng: rng, visit: visit); break;
            case ChainedComparisonExpression cc:
                foreach (Expression o in cc.Operands) { WalkExpr(e: o, visit: visit); }
                break;
            case CreatorExpression cr:
                foreach (var mv in cr.MemberVariables) { WalkExpr(e: mv.Value, visit: visit); }
                break;
            case BlockExpression be: WalkExpr(e: be.Value, visit: visit); break;
        }
    }

    private static void WalkCallExpr(CallExpression call, Action<Expression> visit)
    {
        WalkExpr(e: call.Callee, visit: visit);
        foreach (Expression a in call.Arguments) { WalkExpr(e: a, visit: visit); }
    }

    private static void WalkGenericCallExpr(GenericMemberRoutineCallExpression gcall, Action<Expression> visit)
    {
        WalkExpr(e: gcall.Object, visit: visit);
        foreach (Expression a in gcall.Arguments) { WalkExpr(e: a, visit: visit); }
    }

    private static void WalkConditionalExpr(ConditionalExpression cond, Action<Expression> visit)
    {
        WalkExpr(e: cond.Condition, visit: visit);
        WalkExpr(e: cond.TrueExpression, visit: visit);
        WalkExpr(e: cond.FalseExpression, visit: visit);
    }

    private static void WalkRangeExpr(RangeExpression rng, Action<Expression> visit)
    {
        WalkExpr(e: rng.Start, visit: visit);
        WalkExpr(e: rng.End, visit: visit);
        if (rng.Step != null) WalkExpr(e: rng.Step, visit: visit);
    }
}
