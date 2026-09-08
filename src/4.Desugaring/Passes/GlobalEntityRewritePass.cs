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
                _singletonType = Registry.LookupVariable(name: Builder.Program.ModuleGlobalsSingletonName)?.Type;
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
        if (SingletonType == null) return;

        foreach (SyntaxTree.Declaration decl in program.Declarations)
        {
            RewriteDeclaration(decl: decl);
        }

        // Drop the original `global` declarations (keep the synthesized singleton). Their storage now
        // lives as fields of __ModuleGlobals; leaving them would emit dead @global cells.
        program.Declarations.RemoveAll(match: node =>
            node is VariableDeclaration { IsGlobal: true } g
            && g.Name != Builder.Program.ModuleGlobalsSingletonName);
    }

    /// <summary>Rewrites global references inside synthesized error-handling variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        if (SingletonType == null) return;
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            ctx.VariantBodies[key] = RewriteStmt(stmt: ctx.VariantBodies[key]);
        }
    }

    private void RewriteDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration { Body: { } body } r:
                ReplaceBody(r, body);
                break;
            case EntityDeclaration e:
                RewriteMemberRoutines(e.Members);
                break;
            case RecordDeclaration rec:
                RewriteMemberRoutines(rec.Members);
                break;
            case CrashableDeclaration cr:
                RewriteMemberRoutines(cr.Members);
                break;
        }
    }

    // RoutineDeclaration.Body is init-only; rebuild the block in place on the mutable Statements list so
    // the routine node identity (which downstream passes hold) is preserved.
    private void ReplaceBody(RoutineDeclaration r, Statement body)
    {
        if (body is not BlockStatement block) return;
        var rewritten = block.Statements.Select(selector: RewriteStmt).ToList();
        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    private void RewriteMemberRoutines(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration { Body: { } body } mr) ReplaceBody(mr, body);
        }
    }

    // ---- Statement rewrite (returns a rebuilt node; expression slots + nested statements rewritten) --

    private Statement RewriteStmt(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement s:
                return s with { Expression = RW(s.Expression) };
            case DiscardStatement s:
                return s with { Expression = RW(s.Expression) };
            case AssignmentStatement s:
                return s with { Target = RW(s.Target), Value = RW(s.Value) };
            case ReturnStatement { Value: not null } s:
                return s with { Value = RW(s.Value) };
            case BecomesStatement s:
                return s with { Value = RW(s.Value) };
            case VariantReturnStatement { Value: not null } s:
                return s with { Value = RW(s.Value) };
            case ThrowStatement s:
                return s with { Error = RW(s.Error) };
            case DestructuringStatement s:
                return s with { Initializer = RW(s.Initializer) };
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } v } s:
                return s with { Declaration = v with { Initializer = RW(v.Initializer) } };
            case IfStatement s:
                return s with
                {
                    Condition = RW(s.Condition),
                    ThenStatement = RewriteStmt(s.ThenStatement),
                    ElseStatement = s.ElseStatement is null ? null : RewriteStmt(s.ElseStatement)
                };
            case WhileStatement s:
                return s with
                {
                    Condition = RW(s.Condition),
                    Body = RewriteStmt(s.Body),
                    ElseBranch = s.ElseBranch is null ? null : RewriteStmt(s.ElseBranch)
                };
            case LoopStatement s:
                return s with { Body = RewriteStmt(s.Body) };
            case ExpandStatement s:
                return s with { Body = RewriteStmt(s.Body) };
            case EachStatement s:
                return s with
                {
                    Iterable = RW(s.Iterable),
                    Body = RewriteStmt(s.Body),
                    ElseBranch = s.ElseBranch is null ? null : RewriteStmt(s.ElseBranch)
                };
            case WhenStatement s:
                return s with
                {
                    Expression = RW(s.Expression),
                    Clauses = s.Clauses
                        .Select(selector: c => c with
                        {
                            Pattern = RewritePattern(c.Pattern), Body = RewriteStmt(c.Body)
                        })
                        .ToList()
                };
            case DangerStatement s:
                return s with { Body = (BlockStatement)RewriteStmt(s.Body) };
            case UsingStatement s:
                return s with
                {
                    Resource = RW(s.Resource),
                    Body = RewriteStmt(s.Body),
                    FallbackBody = s.FallbackBody is null ? null : RewriteStmt(s.FallbackBody)
                };
            case BlockStatement s:
                return s with { Statements = s.Statements.Select(selector: RewriteStmt).ToList() };
            default:
                return stmt;
        }
    }

    private Pattern RewritePattern(Pattern p) => p switch
    {
        ExpressionPattern ep => ep with { Expression = RW(ep.Expression) },
        ComparisonPattern cp => cp with { Value = RW(cp.Value) },
        GuardPattern gp => gp with
        {
            InnerPattern = RewritePattern(gp.InnerPattern), Guard = RW(gp.Guard)
        },
        _ => p
    };

    // ---- Expression rewrite (RW = the recursive transformer) -------------------------------------

    private Expression RW(Expression e)
    {
        switch (e)
        {
            // The one real substitution: a stamped global reference -> `__globals__.<name>`.
            case IdentifierExpression id when id.IsModuleGlobal
                && id.Name != Builder.Program.ModuleGlobalsSingletonName:
            {
                var receiver = new IdentifierExpression(
                    Name: Builder.Program.ModuleGlobalsSingletonName, Location: id.Location)
                {
                    ResolvedType = SingletonType
                };
                return new MemberExpression(Object: receiver, MemberName: id.Name, Location: id.Location)
                {
                    ResolvedType = id.ResolvedType
                };
            }


            case BinaryExpression x:
                return x with { Left = RW(x.Left), Right = RW(x.Right) };
            case UnaryExpression x:
                return x with { Operand = RW(x.Operand) };
            case CallExpression x:
                return x with { Callee = RW(x.Callee), Arguments = x.Arguments.Select(selector: RW).ToList() };
            case MemberExpression x:
                return x with { Object = RW(x.Object) };
            case OptionalMemberExpression x:
                return x with { Object = RW(x.Object) };
            case NamedArgumentExpression x:
                return x with { Value = RW(x.Value) };
            case CreatorExpression x:
                return x with
                {
                    MemberVariables = x.MemberVariables
                        .Select(selector: mv => (mv.Name, RW(mv.Value))).ToList()
                };
            case IndexExpression x:
                return x with { Object = RW(x.Object), Index = RW(x.Index) };
            case ConditionalExpression x:
                return x with
                {
                    Condition = RW(x.Condition),
                    TrueExpression = RW(x.TrueExpression),
                    FalseExpression = RW(x.FalseExpression)
                };
            case RangeExpression x:
                return x with
                {
                    Start = RW(x.Start), End = RW(x.End),
                    Step = x.Step is null ? null : RW(x.Step)
                };
            case ListLiteralExpression x:
                return x with { Elements = x.Elements.Select(selector: RW).ToList() };
            case SetLiteralExpression x:
                return x with { Elements = x.Elements.Select(selector: RW).ToList() };
            case TupleLiteralExpression x:
                return x with { Elements = x.Elements.Select(selector: RW).ToList() };
            case DictLiteralExpression x:
                return x with
                {
                    Pairs = x.Pairs.Select(selector: pr => (Key: RW(pr.Key), Value: RW(pr.Value))).ToList()
                };
            case StealExpression x:
                return x with { Operand = RW(x.Operand) };
            case TypeConversionExpression x:
                return x with { Expression = RW(x.Expression) };
            case CompoundAssignmentExpression x:
                return x with { Target = RW(x.Target), Value = RW(x.Value) };
            case ChainedComparisonExpression x:
                return x with { Operands = x.Operands.Select(selector: RW).ToList() };
            case WithExpression x:
                return x with
                {
                    Base = RW(x.Base),
                    Updates = x.Updates
                        .Select(selector: u => (u.MemberVariablePath,
                            Index: u.Index is null ? null : RW(u.Index), Value: RW(u.Value)))
                        .ToList()
                };
            case IsPatternExpression x:
                return x with { Expression = RW(x.Expression), Pattern = RewritePattern(x.Pattern) };
            case FlagsTestExpression x:
                return x with { Subject = RW(x.Subject) };
            case WhenExpression x:
                return x with
                {
                    Expression = x.Expression is null ? null : RW(x.Expression),
                    Clauses = x.Clauses
                        .Select(selector: c => c with
                        {
                            Pattern = RewritePattern(c.Pattern), Body = RewriteStmt(c.Body)
                        })
                        .ToList()
                };
            case WaitforExpression x:
                return x with { Operand = RW(x.Operand), Timeout = x.Timeout is null ? null : RW(x.Timeout) };
            case DependentWaitforExpression x:
                return x with { Operand = RW(x.Operand), Timeout = x.Timeout is null ? null : RW(x.Timeout) };
            case BackIndexExpression x:
                return x with { Operand = RW(x.Operand) };
            case CarrierPayloadExpression x:
                return x with { Carrier = RW(x.Carrier) };
            case BlockExpression x:
                return x with { Value = RW(x.Value) };
            case LambdaExpression x:
                return x with { Body = RW(x.Body) };
            case GenericMemberRoutineCallExpression x:
                return x with { Object = RW(x.Object), Arguments = x.Arguments.Select(selector: RW).ToList() };
            case GenericMemberExpression x:
                return x with { Object = RW(x.Object) };
            case InsertedTextExpression x:
                return x with
                {
                    Parts = x.Parts
                        .Select(selector: p => p is ExpressionPart ep
                            ? ep with { Expression = RW(ep.Expression) }
                            : p)
                        .ToList()
                };
            default:
                return e;
        }
    }
}
