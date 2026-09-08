using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// Lowers the cycle-collector hook intrinsics <c>&lt;entity&gt;.roam_trace_ref()</c> /
/// <c>.roam_free_ref()</c> into an explicit routine-VALUE reference. Runs post-monomorphization
/// (inside <see cref="GenericClosurePass"/>), where the receiver's concrete entity type is known —
/// the source call lives in the generic <c>RoamController[T]</c> body, so the receiver is a plain
/// generic parameter until GMP substitutes it.
///
/// <para>The call <c>data.as_entity().roam_trace_ref()</c> is replaced by an
/// <see cref="IdentifierExpression"/> whose <see cref="IdentifierExpression.ResolvedRoutine"/> is the
/// concrete <c>roam_trace_impl</c> / <c>roam_free_impl</c> on the concrete entity. Codegen then takes
/// the routine as a value through its existing pre-resolved-routine path
/// (<c>EmitRoutineValueClosure</c>) with NO <c>LookupMemberRoutine</c> of its own — the pass, not codegen,
/// picks the routine.</para>
///
/// <para>The <c>as_entity()</c> receiver is a pure reinterpret whose only purpose was to name the
/// entity type; it is dropped, matching the codegen behaviour it replaces. Liveness of the resolved
/// impl is still seeded by <c>RoutineReachabilityPass.EnqueueRoamHookIfNeeded</c>.</para>
/// </summary>
internal sealed class RoamHookRefLoweringPass
{
    private const string TraceRef = "roam_trace_ref";
    private const string FreeRef = "roam_free_ref";
    private const string TraceImpl = "roam_trace_impl";
    private const string FreeImpl = "roam_free_impl";

    private readonly TypeRegistry _registry;

    public RoamHookRefLoweringPass(TypeRegistry registry)
    {
        _registry = registry;
    }

    /// <summary>Rewrites hook references in every monomorphized generic body (Phase 7 path).</summary>
    public void RunOnInstantiatedGenericBodies(IDictionary<string, MonomorphizedBody> bodies)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            MonomorphizedBody mb = bodies[key];
            Statement nb = RewriteStmt(stmt: mb.Ast.Body);
            if (!ReferenceEquals(nb, mb.Ast.Body))
                bodies[key] = mb with { Ast = mb.Ast with { Body = nb } };
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Statement recursion: only descends into the container shapes that can hold an initializer
    // expression carrying the hook call. Leaf mutation happens in RewriteExpr.
    // ---------------------------------------------------------------------------------------------
    private Statement RewriteStmt(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return RewriteBlock(block: block);

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } init } vd } ds:
            {
                Expression n = RewriteExpr(expr: init);
                return ReferenceEquals(n, init)
                    ? ds
                    : ds with { Declaration = vd with { Initializer = n } };
            }

            case ExpressionStatement es:
            {
                Expression n = RewriteExpr(expr: es.Expression);
                return ReferenceEquals(n, es.Expression) ? es : es with { Expression = n };
            }

            case ReturnStatement { Value: { } rv } ret:
            {
                Expression n = RewriteExpr(expr: rv);
                return ReferenceEquals(n, rv) ? ret : ret with { Value = n };
            }

            case AssignmentStatement asg:
            {
                Expression nv = RewriteExpr(expr: asg.Value);
                return ReferenceEquals(nv, asg.Value) ? asg : asg with { Value = nv };
            }

            case IfStatement ifs:
                return RewriteIf(ifs: ifs);

            case LoopStatement loop:
            {
                Statement b = RewriteStmt(stmt: loop.Body);
                return ReferenceEquals(b, loop.Body) ? loop : loop with { Body = b };
            }

            case WhileStatement w:
            {
                Statement b = RewriteStmt(stmt: w.Body);
                return ReferenceEquals(b, w.Body) ? w : w with { Body = b };
            }

            case WhenStatement w:
                return RewriteWhen(when: w);

            case DangerStatement d:
            {
                Statement b = RewriteStmt(stmt: d.Body);
                return ReferenceEquals(b, d.Body) ? d : d with { Body = (BlockStatement)b };
            }

            default:
                return stmt;
        }
    }

    private Statement RewriteBlock(BlockStatement block)
    {
        bool changed = false;
        var stmts = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement s in block.Statements)
        {
            Statement n = RewriteStmt(stmt: s);
            stmts.Add(item: n);
            if (!ReferenceEquals(n, s)) changed = true;
        }
        return changed ? block with { Statements = stmts } : block;
    }

    private Statement RewriteIf(IfStatement ifs)
    {
        Statement then = RewriteStmt(stmt: ifs.ThenStatement);
        Statement? elseS = ifs.ElseStatement != null ? RewriteStmt(stmt: ifs.ElseStatement) : null;
        return !ReferenceEquals(then, ifs.ThenStatement) || !ReferenceEquals(elseS, ifs.ElseStatement)
            ? ifs with { ThenStatement = then, ElseStatement = elseS }
            : ifs;
    }

    private Statement RewriteWhen(WhenStatement when)
    {
        bool changed = false;
        var clauses = new List<WhenClause>(capacity: when.Clauses.Count);
        foreach (WhenClause c in when.Clauses)
        {
            Statement b = RewriteStmt(stmt: c.Body);
            clauses.Add(item: ReferenceEquals(b, c.Body) ? c : c with { Body = b });
            if (!ReferenceEquals(b, c.Body)) changed = true;
        }
        return changed ? when with { Clauses = clauses } : when;
    }

    // ---------------------------------------------------------------------------------------------
    // Expression rewrite: replace a matching hook CallExpression with a routine-value reference;
    // otherwise recurse into the sub-expressions that can hold a nested hook call (creator member
    // values are the real site — `trace_hook: data.as_entity().roam_trace_ref()`).
    // ---------------------------------------------------------------------------------------------
    private Expression RewriteExpr(Expression expr)
    {
        Expression? lowered = TryLowerHookCall(expr: expr);
        if (lowered != null) return lowered;

        switch (expr)
        {
            case CreatorExpression cr:
                return RewriteCreator(cr: cr);

            case CallExpression call:
                return call with
                {
                    Arguments = call.Arguments.Select(selector: RewriteExpr).ToList()
                };

            case NamedArgumentExpression na:
            {
                Expression n = RewriteExpr(expr: na.Value);
                return ReferenceEquals(n, na.Value) ? na : na with { Value = n };
            }

            default:
                return expr;
        }
    }

    private Expression RewriteCreator(CreatorExpression cr)
    {
        bool changed = false;
        var members = new List<(string Name, Expression Value)>(capacity: cr.MemberVariables.Count);
        foreach ((string name, Expression value) in cr.MemberVariables)
        {
            Expression n = RewriteExpr(expr: value);
            members.Add(item: (name, n));
            if (!ReferenceEquals(n, value)) changed = true;
        }
        return changed ? cr with { MemberVariables = members } : cr;
    }

    /// <summary>
    /// If <paramref name="expr"/> is <c>&lt;recv&gt;.roam_trace_ref()</c> / <c>.roam_free_ref()</c>
    /// on a concrete entity receiver, returns the routine-value reference that replaces it; else null.
    /// </summary>
    private Expression? TryLowerHookCall(Expression expr)
    {
        if (expr is not CallExpression { Arguments.Count: 0, Callee: MemberExpression member } call)
            return null;
        if (member.MemberName is not (TraceRef or FreeRef)) return null;

        TypeInfo? recv = member.Object.ResolvedType;
        if (recv is not EntityTypeInfo ent) return null;

        string implName = member.MemberName == TraceRef ? TraceImpl : FreeImpl;
        RoutineInfo? impl = _registry.LookupMemberRoutine(type: ent, memberRoutineName: implName);
        if (impl is not { Parameters.Count: 0 }) return null;

        // A routine-value reference: bare name + ResolvedRoutine (codegen's pre-resolved path).
        // ResolvedType is the matching RoutineTypeInfo so the closure ABI is materialized correctly.
        RoutineTypeInfo routineType = _registry.GetOrCreateRoutineType(
            parameterTypes: impl.Parameters.Select(selector: p => p.Type).ToList(),
            returnType: impl.ReturnType,
            isFailable: impl.IsFailable);
        return new IdentifierExpression(Name: impl.Name, Location: call.Location)
        {
            ResolvedRoutine = impl,
            ResolvedType = routineType
        };
    }
}
