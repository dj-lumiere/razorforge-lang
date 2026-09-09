using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers <see cref="UsingStatement"/> to explicit <c>enter</c> / <c>exit</c> call sequences,
/// injecting <c>exit()</c> before every control-flow escape from the using body.
/// After this pass codegen sees only plain declarations, calls, and standard control flow.
///
/// <para>Transformation of <c>using x = resource { body }</c>:</para>
/// <code>
/// {
///   var __uf_N = resource
///   var x = __uf_N.enter()      // if enter returns a value
///   // OR: __uf_N.enter(); var x = __uf_N   // if enter is void
///   // OR: var x = __uf_N                     // if no enter
///   [body, with exit() injected before every escape]
///   __uf_N.exit()               // normal-path exit (unreachable if body always terminates)
/// }
/// </code>
///
/// <para>Escape injection rules (loopDepth = loops enclosing the point inside the using body):</para>
/// <list type="bullet">
///   <item><see cref="ReturnStatement"/>, <see cref="AbsentStatement"/>, <see cref="ThrowStatement"/>,
///         <see cref="VariantReturnStatement"/> — always inject.</item>
///   <item><see cref="BreakStatement"/>, <see cref="ContinueStatement"/> at loopDepth 0 — inject
///         (break/continue escapes the using's enclosing loop).</item>
///   <item>break/continue at loopDepth &gt; 0 — do not inject (exits a loop inside the using).</item>
/// </list>
///
/// <para>Nested usings are lowered bottom-up: the body is recursively lowered before the outer
/// using is processed, so inner <c>exit()</c> calls appear before outer ones in every escape path.</para>
/// </summary>
internal sealed class UsingLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private int _tempCount;

    private string NextResTemp() => $"__uf_{_tempCount++}";

    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: r => VisitStatement(r.Body));

    public void RunOnVariantBodies()
        => BodyDispatch.RunOnVariantBodies(ctx.VariantBodies, lower: (_, body) => VisitStatement(body));

    /// <summary>
    /// The only node this pass rewrites: a <c>using</c> becomes its explicit enter/exit call sequence.
    /// All structural recursion (blocks, if/when/loop/danger bodies, nested usings) is supplied by
    /// <see cref="AstRewriter"/>.
    /// </summary>
    protected override Statement VisitUsing(UsingStatement s) => LowerUsing(u: s);

    private BlockStatement LowerUsing(UsingStatement u)
    {
        // Lower body first -> nested usings expand bottom-up.
        Statement loweredBody = VisitStatement(u.Body);

        if (u.FallbackBody != null)
            return LowerFallibleUsing(u, loweredBody);

        TypeInfo? resourceType = u.Resource.ResolvedType;
        SourceLocation loc = u.Location;

        string resTemp = NextResTemp();
        var resTempIdent = new IdentifierExpression(Name: resTemp, Location: loc)
        {
            ResolvedType = resourceType
        };

        RoutineInfo? enterMemberRoutine = resourceType != null
            ? ctx.Registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "enter")
            : null;
        RoutineInfo? exitMemberRoutine = resourceType != null
            ? ctx.Registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "exit")
            : null;

        var stmts = new List<Statement>();

        // var __uf_N = resource
        stmts.Add(MakeBinding(name: resTemp, value: u.Resource, type: resourceType, loc: loc));

        // Bind user's name via enter (or directly to the resource if no enter)
        EmitEnterBinding(stmts: stmts, u: u, enterMemberRoutine: enterMemberRoutine,
            resTempIdent: resTempIdent, resourceType: resourceType, loc: loc);

        // Build the exit() call expression (reused for injection and normal exit)
        ExpressionStatement? exitCallStmt =
            MakeExitCallStmt(exitMemberRoutine: exitMemberRoutine, resTempIdent: resTempIdent, loc: loc);

        Statement body = exitCallStmt != null
            ? InjectExitBeforeEscapes(loweredBody, exitCallStmt, loopDepth: 0)
            : loweredBody;

        stmts.Add(body);

        // Normal-path exit — unreachable (and skipped by EmitBlock) if body always terminates.
        if (exitCallStmt != null)
            stmts.Add(exitCallStmt);

        return new BlockStatement(Statements: stmts, Location: loc);
    }

    // Bind user's name via enter (or directly to the resource if no enter).
    private static void EmitEnterBinding(List<Statement> stmts, UsingStatement u,
        RoutineInfo? enterMemberRoutine, IdentifierExpression resTempIdent, TypeInfo? resourceType,
        SourceLocation loc)
    {
        if (enterMemberRoutine == null)
        {
            stmts.Add(MakeBinding(name: u.Name, value: resTempIdent, type: resourceType, loc: loc));
            return;
        }

        var enterCallee = new MemberExpression(
            Object: resTempIdent, MemberName: "enter", Location: loc);
        var enterCall = new CallExpression(
            Callee: enterCallee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = enterMemberRoutine,
            ResolvedType = enterMemberRoutine.ReturnType
        };

        bool returnsValue = enterMemberRoutine.ReturnType != null
            && enterMemberRoutine.ReturnType.Name != "None";

        if (returnsValue)
        {
            stmts.Add(MakeBinding(name: u.Name, value: enterCall,
                type: enterMemberRoutine.ReturnType, loc: loc));
        }
        else
        {
            stmts.Add(new ExpressionStatement(Expression: enterCall, Location: loc));
            stmts.Add(MakeBinding(name: u.Name, value: resTempIdent,
                type: resourceType, loc: loc));
        }
    }

    // Build the exit() call statement (reused for injection and normal exit), or null when the
    // resource type has no exit memberRoutine.
    private static ExpressionStatement? MakeExitCallStmt(RoutineInfo? exitMemberRoutine,
        IdentifierExpression resTempIdent, SourceLocation loc)
    {
        if (exitMemberRoutine == null)
            return null;

        var exitCallee = new MemberExpression(
            Object: resTempIdent, MemberName: "exit", Location: loc);
        var exitCall = new CallExpression(
            Callee: exitCallee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = exitMemberRoutine,
            ResolvedType = null
        };
        return new ExpressionStatement(Expression: exitCall, Location: loc);
    }

    /// <summary>
    /// Lowers a fallible <c>using resource as x { body } fallback { alt }</c> to:
    /// <code>
    /// {
    ///   var __uf_N = resource
    ///   if __uf_N.try_enter():        // Bool: did the non-blocking acquire succeed?
    ///     var x = __uf_N
    ///     [body, with exit() injected before every escape]
    ///     __uf_N.exit()               // normal-path release
    ///   else:
    ///     [fallback]                    // nothing acquired -> no exit
    /// }
    /// </code>
    /// The hold is released by <c>exit</c> on every exit from the success branch only; the
    /// fallback branch never acquired it, so it carries no teardown.
    /// </summary>
    private BlockStatement LowerFallibleUsing(UsingStatement u, Statement loweredBody)
    {
        Statement loweredFallback = VisitStatement(u.FallbackBody!);

        TypeInfo? resourceType = u.Resource.ResolvedType;
        SourceLocation loc = u.Location;

        string resTemp = NextResTemp();
        var resTempIdent = new IdentifierExpression(Name: resTemp, Location: loc)
        {
            ResolvedType = resourceType
        };

        RoutineInfo? tryEnterMemberRoutine = resourceType != null
            ? ctx.Registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "try_enter")
            : null;
        RoutineInfo? exitMemberRoutine = resourceType != null
            ? ctx.Registry.LookupMemberRoutine(type: resourceType, memberRoutineName: "exit")
            : null;

        var stmts = new List<Statement>();

        // var __uf_N = resource
        stmts.Add(MakeBinding(name: resTemp, value: u.Resource, type: resourceType, loc: loc));

        // Condition: __uf_N.try_enter()  (Bool; SA has already verified try_enter exists)
        var tryEnterCallee = new MemberExpression(
            Object: resTempIdent, MemberName: "try_enter", Location: loc);
        var tryEnterCall = new CallExpression(
            Callee: tryEnterCallee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = tryEnterMemberRoutine,
            ResolvedType = tryEnterMemberRoutine?.ReturnType
        };

        // Success branch: bind the token, run body with exit injected, then normal-path exit.
        var thenStmts = new List<Statement>
        {
            MakeBinding(name: u.Name, value: resTempIdent, type: resourceType, loc: loc)
        };

        ExpressionStatement? exitCallStmt =
            MakeExitCallStmt(exitMemberRoutine: exitMemberRoutine, resTempIdent: resTempIdent, loc: loc);

        Statement successBody = exitCallStmt != null
            ? InjectExitBeforeEscapes(loweredBody, exitCallStmt, loopDepth: 0)
            : loweredBody;
        thenStmts.Add(successBody);
        if (exitCallStmt != null)
            thenStmts.Add(exitCallStmt);

        var ifStmt = new IfStatement(
            Condition: tryEnterCall,
            ThenStatement: new BlockStatement(Statements: thenStmts, Location: loc),
            ElseStatement: loweredFallback,
            Location: loc);

        stmts.Add(ifStmt);
        return new BlockStatement(Statements: stmts, Location: loc);
    }

    private static Statement InjectExitBeforeEscapes(
        Statement stmt, ExpressionStatement exitStmt, int loopDepth)
    {
        switch (stmt)
        {
            case ReturnStatement:
            case AbsentStatement:
            case ThrowStatement:
            case VariantReturnStatement:
                return MakeBlock([exitStmt, stmt], stmt.Location);

            case BreakStatement:
            case ContinueStatement:
                return loopDepth == 0
                    ? MakeBlock([exitStmt, stmt], stmt.Location)
                    : stmt;

            case BlockStatement b:
                return InjectIntoBlock(b, exitStmt, loopDepth);

            case IfStatement ifs:
                return InjectIntoIf(ifs, exitStmt, loopDepth);

            case WhenStatement w:
                return InjectIntoWhen(w, exitStmt, loopDepth);

            case LoopStatement loop:
            {
                Statement body = InjectExitBeforeEscapes(loop.Body, exitStmt, loopDepth + 1);
                return !ReferenceEquals(body, loop.Body) ? loop with { Body = body } : loop;
            }

            case DangerStatement d:
            {
                Statement body = InjectExitBeforeEscapes(d.Body, exitStmt, loopDepth);
                return !ReferenceEquals(body, d.Body)
                    ? d with { Body = (BlockStatement)body }
                    : d;
            }

            default:
                return stmt;
        }
    }

    private static BlockStatement InjectIntoBlock(BlockStatement b, ExpressionStatement exitStmt, int loopDepth)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: b.Statements.Count);
        foreach (Statement s in b.Statements)
        {
            Statement n = InjectExitBeforeEscapes(s, exitStmt, loopDepth);
            stmts.Add(n);
            if (!ReferenceEquals(n, s)) changed = true;
        }
        return changed ? b with { Statements = stmts } : b;
    }

    private static IfStatement InjectIntoIf(IfStatement ifs, ExpressionStatement exitStmt, int loopDepth)
    {
        Statement then = InjectExitBeforeEscapes(ifs.ThenStatement, exitStmt, loopDepth);
        Statement? elseS = ifs.ElseStatement != null
            ? InjectExitBeforeEscapes(ifs.ElseStatement, exitStmt, loopDepth)
            : null;
        bool changed = !ReferenceEquals(then, ifs.ThenStatement)
                       || !ReferenceEquals(elseS, ifs.ElseStatement);
        return changed ? ifs with { ThenStatement = then, ElseStatement = elseS } : ifs;
    }

    private static WhenStatement InjectIntoWhen(WhenStatement w, ExpressionStatement exitStmt, int loopDepth)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
        foreach (WhenClause c in w.Clauses)
        {
            Statement body = InjectExitBeforeEscapes(c.Body, exitStmt, loopDepth);
            clauses.Add(!ReferenceEquals(body, c.Body) ? c with { Body = body } : c);
            if (!ReferenceEquals(body, c.Body)) changed = true;
        }
        return changed ? w with { Clauses = clauses } : w;
    }

    private static BlockStatement MakeBlock(IEnumerable<Statement> stmts, SourceLocation loc)
        => new(Statements: stmts.ToList(), Location: loc);

    private static DeclarationStatement MakeBinding(
        string name, Expression value, TypeInfo? type, SourceLocation loc)
    {
        var decl = new VariableDeclaration(
            Name: name,
            Type: type != null ? TypeInfoToExpr(type: type, loc: loc) : null,
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        return new DeclarationStatement(Declaration: decl, Location: loc);
    }

    private static TypeExpression TypeInfoToExpr(TypeInfo type, SourceLocation loc)
    {
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution ? type.BareName : type.Name
        };
        return new TypeExpression(
            Name: baseName,
            GenericArguments: [],
            Location: loc)
        {
            ResolvedType = type
        };
    }
}
