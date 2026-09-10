using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Lowering.Passes;

/// <summary>
/// Routes every Suflae module-level <c>global</c> through the hidden per-program
/// <c>__ModuleGlobals</c> entity so the globals become thread-safe: a bare global reference
/// <c>g</c> (read, write, f-string interpolation, or member-call receiver) is rewritten to
/// <c>__globals__.g</c> — a field access on the single promoted <c>Roamed[__ModuleGlobals]</c>
/// singleton. Downstream, <see cref="RoamedLockBracketLoweringPass"/> wraps each such field-access
/// statement in the escaped access-lock brackets (serializing concurrent RMW), and codegen projects
/// the field through the roam controller exactly like any entity field.
///
/// <para>Only <see cref="IdentifierExpression.IsModuleGlobal"/>-stamped references are rewritten. SA
/// sets that flag from <c>VariableInfo.IsGlobal</c> AFTER checking local scopes, so a local that
/// shadows a global is never stamped — the rewrite is shadowing-exact. The singleton's own name is
/// left alone (it IS the storage). The now-superfluous original <c>global</c> declarations are removed
/// from the program so codegen emits no dead top-level <c>@global</c> cells for them.</para>
///
/// <para>Runs at the TOP of the postprocessing pipeline: before <c>FStringLoweringPass</c> (so f-string
/// interpolations see the member access), before <c>OperatorLoweringPass</c>, and before
/// <c>RoamedProjectionLoweringPass</c>/<c>RoamedLockBracketLoweringPass</c>. The Roamed lock/promote
/// routines it depends on were already seeded off the live <c>Roamed[__ModuleGlobals]</c> (the singleton
/// construction in <c>start()</c>) during reachability — same contract
/// <see cref="RoamedSpawnPromotionLoweringPass"/> relies on.</para>
/// </summary>
internal sealed class GlobalEntityRewritePass(PostprocessingContext ctx)
{
    private TypeRegistry Registry => ctx.Registry;

    // Resolved lazily on first use — the roamed singleton type is only known once SA has registered it.
    private TypeInfo? _singletonType;
    private bool _resolvedSingleton;

    private TypeInfo? SingletonType
    {
        get
        {
            if (!_resolvedSingleton)
            {
                _singletonType = Registry
                                .LookupVariable(name: Builder.Program.ModuleGlobalsSingletonName)
                               ?.Type;
                _resolvedSingleton = true;
            }

            return _singletonType;
        }
    }

    /// <summary>Rewrites every global reference across a whole program and drops the original
    /// <c>global</c> declarations.</summary>
    public void Run(Program program)
    {
        // No globals in this program → the singleton was never synthesized; nothing to do.
        if (SingletonType == null)
        {
            return;
        }

        foreach (SyntaxTree.Declaration decl in
                 program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            RewriteDeclaration(decl: decl);
        }

        // Drop the original `global` declarations (keep the synthesized singleton). Their storage now
        // lives as fields of __ModuleGlobals; leaving them would emit dead @global cells.
        program.Declarations.RemoveAll(match: node =>
            node is VariableDeclaration { IsGlobal: true } g &&
            g.Name != Builder.Program.ModuleGlobalsSingletonName);
    }

    /// <summary>Rewrites global references inside synthesized error-handling variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        if (SingletonType == null)
        {
            return;
        }

        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            ctx.VariantBodies[key: key] = RewriteStmt(stmt: ctx.VariantBodies[key: key]);
        }
    }

    private void RewriteDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration { Body: { } body }:
                ReplaceBody(body: body);
                break;
            case EntityDeclaration e:
                RewriteMemberRoutines(members: e.Members);
                break;
            case RecordDeclaration rec:
                RewriteMemberRoutines(members: rec.Members);
                break;
            case CrashableDeclaration cr:
                RewriteMemberRoutines(members: cr.Members);
                break;
        }
    }

    // RoutineDeclaration.Body is init-only; rebuild the block in place on the mutable Statements list so
    // the routine node identity (which downstream passes hold) is preserved.
    private void ReplaceBody(Statement body)
    {
        if (body is not BlockStatement block)
        {
            return;
        }

        var rewritten = block.Statements
                             .Select(selector: RewriteStmt)
                             .ToList();
        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    private void RewriteMemberRoutines(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration { Body: { } body })
            {
                ReplaceBody(body: body);
            }
        }
    }

    // ---- Statement rewrite (returns a rebuilt node; expression slots + nested statements rewritten) --

    private Statement RewriteStmt(Statement stmt)
    {
        return stmt switch
        {
            ExpressionStatement s => s with { Expression = RW(e: s.Expression) },
            DiscardStatement s => s with { Expression = RW(e: s.Expression) },
            AssignmentStatement s => s with { Target = RW(e: s.Target), Value = RW(e: s.Value) },
            ReturnStatement { Value: not null } s => s with { Value = RW(e: s.Value) },
            BecomesStatement s => s with { Value = RW(e: s.Value) },
            VariantReturnStatement { Value: not null } s => s with { Value = RW(e: s.Value) },
            ThrowStatement s => s with { Error = RW(e: s.Error) },
            DestructuringStatement s => s with { Initializer = RW(e: s.Initializer) },
            DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } v } s
                => s with { Declaration = v with { Initializer = RW(e: v.Initializer) } },
            IfStatement s => s with
            {
                Condition = RW(e: s.Condition),
                ThenStatement = RewriteStmt(stmt: s.ThenStatement),
                ElseStatement = s.ElseStatement is null
                    ? null
                    : RewriteStmt(stmt: s.ElseStatement)
            },
            WhileStatement s => s with
            {
                Condition = RW(e: s.Condition),
                Body = RewriteStmt(stmt: s.Body),
                ElseBranch = s.ElseBranch is null
                    ? null
                    : RewriteStmt(stmt: s.ElseBranch)
            },
            LoopStatement s => s with { Body = RewriteStmt(stmt: s.Body) },
            ExpandStatement s => s with { Body = RewriteStmt(stmt: s.Body) },
            EachStatement s => s with
            {
                Iterable = RW(e: s.Iterable),
                Body = RewriteStmt(stmt: s.Body),
                ElseBranch = s.ElseBranch is null
                    ? null
                    : RewriteStmt(stmt: s.ElseBranch)
            },
            WhenStatement s => s with
            {
                Expression = RW(e: s.Expression),
                Clauses = s.Clauses
                           .Select(selector: c => c with
                            {
                                Pattern = RewritePattern(p: c.Pattern),
                                Body = RewriteStmt(stmt: c.Body)
                            })
                           .ToList()
            },
            DangerStatement s => s with { Body = (BlockStatement)RewriteStmt(stmt: s.Body) },
            UsingStatement s => s with
            {
                Resource = RW(e: s.Resource),
                Body = RewriteStmt(stmt: s.Body),
                FallbackBody = s.FallbackBody is null
                    ? null
                    : RewriteStmt(stmt: s.FallbackBody)
            },
            BlockStatement s => s with
            {
                Statements = s.Statements
                              .Select(selector: RewriteStmt)
                              .ToList()
            },
            _ => stmt
        };
    }

    private Pattern RewritePattern(Pattern p)
    {
        return p switch
        {
            ExpressionPattern ep => ep with { Expression = RW(e: ep.Expression) },
            ComparisonPattern cp => cp with { Value = RW(e: cp.Value) },
            GuardPattern gp => gp with
            {
                InnerPattern = RewritePattern(p: gp.InnerPattern), Guard = RW(e: gp.Guard)
            },
            _ => p
        };
    }

    // ---- Expression rewrite (RW = the recursive transformer) -------------------------------------

    private Expression RW(Expression e)
    {
        // The one real substitution: a stamped global reference -> `__globals__.<name>`.
        if (e is IdentifierExpression id && id.IsModuleGlobal &&
            id.Name != Builder.Program.ModuleGlobalsSingletonName)
        {
            return RewriteGlobalIdentifier(id: id);
        }

        return RWStructural(e: e);
    }

    /// <summary>
    /// Rewrites a stamped <see cref="IdentifierExpression.IsModuleGlobal"/> reference into a
    /// <c>__globals__.&lt;name&gt;</c> member access on the promoted singleton.
    /// </summary>
    private MemberExpression RewriteGlobalIdentifier(IdentifierExpression id)
    {
        var receiver = new IdentifierExpression(
            Name: Builder.Program.ModuleGlobalsSingletonName,
            Location: id.Location) { ResolvedType = SingletonType };
        return new MemberExpression(Object: receiver, MemberName: id.Name, Location: id.Location)
        {
            ResolvedType = id.ResolvedType
        };
    }

    /// <summary>
    /// Structural traversal: recursively rewrites every expression sub-tree, propagating the global
    /// reference rewrite into every expression node without changing the expression's logical value.
    /// </summary>
    private Expression RWStructural(Expression e)
    {
        return e switch
        {
            BinaryExpression x => x with { Left = RW(e: x.Left), Right = RW(e: x.Right) },
            UnaryExpression x => x with { Operand = RW(e: x.Operand) },
            CallExpression x => x with
            {
                Callee = RW(e: x.Callee),
                Arguments = x.Arguments
                             .Select(selector: RW)
                             .ToList()
            },
            MemberExpression x => x with { Object = RW(e: x.Object) },
            OptionalMemberExpression x => x with { Object = RW(e: x.Object) },
            NamedArgumentExpression x => x with { Value = RW(e: x.Value) },
            CreatorExpression x => x with
            {
                MemberVariables = x.MemberVariables
                                   .Select(selector: mv => (mv.Name, RW(e: mv.Value)))
                                   .ToList()
            },
            IndexExpression x => x with { Object = RW(e: x.Object), Index = RW(e: x.Index) },
            ConditionalExpression x => x with
            {
                Condition = RW(e: x.Condition),
                TrueExpression = RW(e: x.TrueExpression),
                FalseExpression = RW(e: x.FalseExpression)
            },
            RangeExpression x => x with
            {
                Start = RW(e: x.Start),
                End = RW(e: x.End),
                Step = x.Step is null
                    ? null
                    : RW(e: x.Step)
            },
            ListLiteralExpression x => x with
            {
                Elements = x.Elements
                            .Select(selector: RW)
                            .ToList()
            },
            SetLiteralExpression x => x with
            {
                Elements = x.Elements
                            .Select(selector: RW)
                            .ToList()
            },
            TupleLiteralExpression x => x with
            {
                Elements = x.Elements
                            .Select(selector: RW)
                            .ToList()
            },
            DictLiteralExpression x => x with
            {
                Pairs = x.Pairs
                         .Select(selector: pr => (Key: RW(e: pr.Key), Value: RW(e: pr.Value)))
                         .ToList()
            },
            StealExpression x => x with { Operand = RW(e: x.Operand) },
            TypeConversionExpression x => x with { Expression = RW(e: x.Expression) },
            CompoundAssignmentExpression x => x with
            {
                Target = RW(e: x.Target), Value = RW(e: x.Value)
            },
            ChainedComparisonExpression x => x with
            {
                Operands = x.Operands
                            .Select(selector: RW)
                            .ToList()
            },
            WithExpression x => x with
            {
                Base = RW(e: x.Base),
                Updates = x.Updates
                           .Select(selector: u => (u.MemberVariablePath, Index: u.Index is null
                                ? null
                                : RW(e: u.Index), Value: RW(e: u.Value)))
                           .ToList()
            },
            IsPatternExpression x => x with
            {
                Expression = RW(e: x.Expression), Pattern = RewritePattern(p: x.Pattern)
            },
            FlagsTestExpression x => x with { Subject = RW(e: x.Subject) },
            WhenExpression x => x with
            {
                Expression = x.Expression is null
                    ? null
                    : RW(e: x.Expression),
                Clauses = x.Clauses
                           .Select(selector: c => c with
                            {
                                Pattern = RewritePattern(p: c.Pattern),
                                Body = RewriteStmt(stmt: c.Body)
                            })
                           .ToList()
            },
            WaitforExpression x => x with
            {
                Operand = RW(e: x.Operand),
                Timeout = x.Timeout is null
                    ? null
                    : RW(e: x.Timeout)
            },
            DependentWaitforExpression x => x with
            {
                Operand = RW(e: x.Operand),
                Timeout = x.Timeout is null
                    ? null
                    : RW(e: x.Timeout)
            },
            BackIndexExpression x => x with { Operand = RW(e: x.Operand) },
            CarrierPayloadExpression x => x with { Carrier = RW(e: x.Carrier) },
            BlockExpression x => x with { Value = RW(e: x.Value) },
            LambdaExpression x => x with { Body = RW(e: x.Body) },
            GenericMemberRoutineCallExpression x => x with
            {
                Object = RW(e: x.Object),
                Arguments = x.Arguments
                             .Select(selector: RW)
                             .ToList()
            },
            GenericMemberExpression x => x with { Object = RW(e: x.Object) },
            InsertedTextExpression x => x with
            {
                Parts = x.Parts
                         .Select(selector: p => p is ExpressionPart ep
                              ? ep with { Expression = RW(e: ep.Expression) }
                              : p)
                         .ToList()
            },
            _ => e
        };
    }
}
