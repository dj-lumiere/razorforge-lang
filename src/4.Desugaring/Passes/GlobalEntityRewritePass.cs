using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

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
        switch (stmt)
        {
            case ExpressionStatement s:
                return s with { Expression = RW(e: s.Expression) };
            case DiscardStatement s:
                return s with { Expression = RW(e: s.Expression) };
            case AssignmentStatement s:
                return s with { Target = RW(e: s.Target), Value = RW(e: s.Value) };
            case ReturnStatement { Value: not null } s:
                return s with { Value = RW(e: s.Value) };
            case BecomesStatement s:
                return s with { Value = RW(e: s.Value) };
            case VariantReturnStatement { Value: not null } s:
                return s with { Value = RW(e: s.Value) };
            case ThrowStatement s:
                return s with { Error = RW(e: s.Error) };
            case DestructuringStatement s:
                return s with { Initializer = RW(e: s.Initializer) };
            case DeclarationStatement
            {
                Declaration: VariableDeclaration { Initializer: not null } v
            } s:
                return s with { Declaration = v with { Initializer = RW(e: v.Initializer) } };
            case IfStatement s:
                return s with
                {
                    Condition = RW(e: s.Condition),
                    ThenStatement = RewriteStmt(stmt: s.ThenStatement),
                    ElseStatement = s.ElseStatement is null
                        ? null
                        : RewriteStmt(stmt: s.ElseStatement)
                };
            case WhileStatement s:
                return s with
                {
                    Condition = RW(e: s.Condition),
                    Body = RewriteStmt(stmt: s.Body),
                    ElseBranch = s.ElseBranch is null
                        ? null
                        : RewriteStmt(stmt: s.ElseBranch)
                };
            case LoopStatement s:
                return s with { Body = RewriteStmt(stmt: s.Body) };
            case ExpandStatement s:
                return s with { Body = RewriteStmt(stmt: s.Body) };
            case EachStatement s:
                return s with
                {
                    Iterable = RW(e: s.Iterable),
                    Body = RewriteStmt(stmt: s.Body),
                    ElseBranch = s.ElseBranch is null
                        ? null
                        : RewriteStmt(stmt: s.ElseBranch)
                };
            case WhenStatement s:
                return s with
                {
                    Expression = RW(e: s.Expression),
                    Clauses = s.Clauses
                               .Select(selector: c => c with
                                {
                                    Pattern = RewritePattern(p: c.Pattern),
                                    Body = RewriteStmt(stmt: c.Body)
                                })
                               .ToList()
                };
            case DangerStatement s:
                return s with { Body = (BlockStatement)RewriteStmt(stmt: s.Body) };
            case UsingStatement s:
                return s with
                {
                    Resource = RW(e: s.Resource),
                    Body = RewriteStmt(stmt: s.Body),
                    FallbackBody = s.FallbackBody is null
                        ? null
                        : RewriteStmt(stmt: s.FallbackBody)
                };
            case BlockStatement s:
                return s with
                {
                    Statements = s.Statements
                                  .Select(selector: RewriteStmt)
                                  .ToList()
                };
            default:
                return stmt;
        }
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
        switch (e)
        {
            case BinaryExpression x:
                return x with { Left = RW(e: x.Left), Right = RW(e: x.Right) };
            case UnaryExpression x:
                return x with { Operand = RW(e: x.Operand) };
            case CallExpression x:
                return x with
                {
                    Callee = RW(e: x.Callee),
                    Arguments = x.Arguments
                                 .Select(selector: RW)
                                 .ToList()
                };
            case MemberExpression x:
                return x with { Object = RW(e: x.Object) };
            case OptionalMemberExpression x:
                return x with { Object = RW(e: x.Object) };
            case NamedArgumentExpression x:
                return x with { Value = RW(e: x.Value) };
            case CreatorExpression x:
                return x with
                {
                    MemberVariables = x.MemberVariables
                                       .Select(selector: mv => (mv.Name, RW(e: mv.Value)))
                                       .ToList()
                };
            case IndexExpression x:
                return x with { Object = RW(e: x.Object), Index = RW(e: x.Index) };
            case ConditionalExpression x:
                return x with
                {
                    Condition = RW(e: x.Condition),
                    TrueExpression = RW(e: x.TrueExpression),
                    FalseExpression = RW(e: x.FalseExpression)
                };
            case RangeExpression x:
                return x with
                {
                    Start = RW(e: x.Start),
                    End = RW(e: x.End),
                    Step = x.Step is null
                        ? null
                        : RW(e: x.Step)
                };
            case ListLiteralExpression x:
                return x with
                {
                    Elements = x.Elements
                                .Select(selector: RW)
                                .ToList()
                };
            case SetLiteralExpression x:
                return x with
                {
                    Elements = x.Elements
                                .Select(selector: RW)
                                .ToList()
                };
            case TupleLiteralExpression x:
                return x with
                {
                    Elements = x.Elements
                                .Select(selector: RW)
                                .ToList()
                };
            case DictLiteralExpression x:
                return x with
                {
                    Pairs = x.Pairs
                             .Select(selector: pr => (Key: RW(e: pr.Key), Value: RW(e: pr.Value)))
                             .ToList()
                };
            case StealExpression x:
                return x with { Operand = RW(e: x.Operand) };
            case TypeConversionExpression x:
                return x with { Expression = RW(e: x.Expression) };
            case CompoundAssignmentExpression x:
                return x with { Target = RW(e: x.Target), Value = RW(e: x.Value) };
            case ChainedComparisonExpression x:
                return x with
                {
                    Operands = x.Operands
                                .Select(selector: RW)
                                .ToList()
                };
            case WithExpression x:
                return x with
                {
                    Base = RW(e: x.Base),
                    Updates = x.Updates
                               .Select(selector: u => (u.MemberVariablePath, Index: u.Index is null
                                    ? null
                                    : RW(e: u.Index), Value: RW(e: u.Value)))
                               .ToList()
                };
            case IsPatternExpression x:
                return x with
                {
                    Expression = RW(e: x.Expression), Pattern = RewritePattern(p: x.Pattern)
                };
            case FlagsTestExpression x:
                return x with { Subject = RW(e: x.Subject) };
            case WhenExpression x:
                return x with
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
                };
            case WaitforExpression x:
                return x with
                {
                    Operand = RW(e: x.Operand),
                    Timeout = x.Timeout is null
                        ? null
                        : RW(e: x.Timeout)
                };
            case DependentWaitforExpression x:
                return x with
                {
                    Operand = RW(e: x.Operand),
                    Timeout = x.Timeout is null
                        ? null
                        : RW(e: x.Timeout)
                };
            case BackIndexExpression x:
                return x with { Operand = RW(e: x.Operand) };
            case CarrierPayloadExpression x:
                return x with { Carrier = RW(e: x.Carrier) };
            case BlockExpression x:
                return x with { Value = RW(e: x.Value) };
            case LambdaExpression x:
                return x with { Body = RW(e: x.Body) };
            case GenericMemberRoutineCallExpression x:
                return x with
                {
                    Object = RW(e: x.Object),
                    Arguments = x.Arguments
                                 .Select(selector: RW)
                                 .ToList()
                };
            case GenericMemberExpression x:
                return x with { Object = RW(e: x.Object) };
            case InsertedTextExpression x:
                return x with
                {
                    Parts = x.Parts
                             .Select(selector: p => p is ExpressionPart ep
                                  ? ep with { Expression = RW(e: ep.Expression) }
                                  : p)
                             .ToList()
                };
            default:
                return e;
        }
    }
}
