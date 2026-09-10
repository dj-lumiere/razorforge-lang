using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Lowering.Passes;

/// <summary>
/// v0.2.0 Phase 9-2 (Mechanism C): inserts coroutine cancellation push/pop markers into the
/// bodies of may-suspend routines, so a coroutine abandoned while parked tears down exactly the
/// owned values it had constructed (the design's cancellation shadow stack).
///
/// <para>The markers are sentinel <see cref="CallExpression"/>s (<c>__rf_cf_push(local)</c> /
/// <c>__rf_cf_pop(local)</c>) — no new AST node and no visitor surface. This pass runs LAST (after
/// all analysis/lowering, just before codegen), so nothing else observes the markers; codegen
/// recognises them and emits the <c>rf_coro_cf_push</c>/<c>rf_coro_cf_pop</c> runtime calls.</para>
///
/// <para>Consistency-by-construction: the set of instrumented locals is DERIVED from the inline
/// <c>local.destroy()</c> calls <c>ScopeTeardownLoweringPass</c> already inserted. A push goes
/// right after a local's construction (so a value built *after* a suspend point is not on the stack
/// before it — partial init), and a pop right before each of that local's inline
/// <c>destroy</c> (so the node is removed exactly when the inline teardown runs — the value can
/// never be torn down twice). Empty may-suspend set ⇒ this pass is a no-op.</para>
///
/// <para>First cut scope: owned LOCALS of free routines. Member routines and synthesized/generic
/// bodies are a follow-up (until then a may-suspend member routine that owns resources would leak
/// on abandon — tracked).</para>
/// </summary>
public sealed class CancellationInstrumentationPass
{
    /// <summary>Sentinel callee name for a cancellation-node PUSH; codegen lowers it to rf_coro_cf_push.</summary>
    public const string PushMarker = "__rf_cf_push";

    /// <summary>Sentinel callee name for a cancellation-node POP; codegen lowers it to rf_coro_cf_pop.</summary>
    public const string PopMarker = "__rf_cf_pop";

    private readonly HashSet<string> _maySuspend;
    private readonly TypeRegistry _registry;

    private CancellationInstrumentationPass(HashSet<string> maySuspend, TypeRegistry registry)
    {
        _maySuspend = maySuspend;
        _registry = registry;
    }

    /// <summary>
    /// Instruments every may-suspend routine in place: free routines and concrete member routines
    /// from <paramref name="programs"/>, plus monomorphized generic bodies in
    /// <paramref name="instantiatedBodies"/> (e.g. <c>List[Box].pop</c>). No-op when
    /// <paramref name="maySuspendKeys"/> is empty (i.e. no coroutine reaches a suspend point).
    /// </summary>
    public static void Run(IEnumerable<(Program Program, string FilePath, string Module)> programs,
        IReadOnlyDictionary<string, Instantiation.MonomorphizedBody> instantiatedBodies,
        IReadOnlyCollection<string> maySuspendKeys, TypeRegistry registry)
    {
        if (maySuspendKeys.Count == 0)
        {
            return;
        }

        var pass = new CancellationInstrumentationPass(
            maySuspend: new HashSet<string>(collection: maySuspendKeys,
                comparer: StringComparer.Ordinal),
            registry: registry);

        foreach ((Program program, _, _) in programs)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                pass.MaybeInstrument(decl: decl);
            }
        }

        // Monomorphized generic bodies (List[Box].pop, etc.) are keyed by concrete RegistryKey and
        // are the SAME objects codegen emits, so mutating their bodies in place takes effect. They
        // inherited the inline destroy calls from the lowered generic-def, so InstrumentBody derives
        // the teardown set the same way.
        foreach ((string key, Instantiation.MonomorphizedBody mb) in instantiatedBodies)
        {
            if (pass._maySuspend.Contains(item: key) ||
                pass._maySuspend.Contains(item: mb.Info.RegistryKey))
            {
                pass.InstrumentBody(body: mb.Ast.Body);
            }
        }
    }

    /// <summary>
    /// Resolves a routine declaration to its <see cref="RoutineInfo"/> so it can be gated on the
    /// may-suspend set. A member routine declares with a dotted name (<c>Box.do_work</c>); resolve
    /// it on its owner type. A concrete owner (a user-declared entity/record) resolves directly;
    /// generic-definition owners (<c>List[T].x</c>) are skipped here — their instrumentation belongs
    /// on the monomorphized bodies, a separate follow-up.
    /// </summary>
    private RoutineInfo? ResolveDecl(RoutineDeclaration decl)
    {
        if (decl.MemberRoutineName is not { } memberRoutineName)
        {
            return _registry.LookupRoutineByName(name: decl.Name, isFailable: decl.IsFailable);
        }

        // Owner is the RENDERED receiver: a bracketed generic-def owner keys to null here and is skipped
        // (its instrumentation belongs on the monomorphized bodies), which the bare OwnerName would not
        // reproduce.
        TypeInfo? owner = _registry.LookupType(name: decl.RenderedReceiver!);
        return owner == null
            ? null
            : _registry.LookupMemberRoutine(type: owner,
                memberRoutineName: memberRoutineName,
                isFailable: decl.IsFailable);
    }

    private void MaybeInstrument(RoutineDeclaration decl)
    {
        RoutineInfo? info = ResolveDecl(decl: decl);
        if (info != null && _maySuspend.Contains(item: info.RegistryKey))
        {
            InstrumentBody(body: decl.Body);
        }
    }

    /// <summary>
    /// Instruments one routine body (whether a source decl or a monomorphized generic body). The
    /// set of instrumented locals is DERIVED from the inline <c>X.destroy()</c> calls
    /// <c>ScopeTeardownLoweringPass</c> already inserted — keeping abandon's set == inline's set.
    /// </summary>
    private void InstrumentBody(Statement body)
    {
        if (body is not BlockStatement block)
        {
            return;
        }

        var locals = new HashSet<string>(comparer: StringComparer.Ordinal);
        AstWalker.Walk(root: block,
            visit: n =>
            {
                if (n is CallExpression
                    {
                        Callee: MemberExpression
                        {
                            MemberName: "destroy", Object: IdentifierExpression destroyed
                        }
                    })
                {
                    locals.Add(item: destroyed.Name);
                }
            });
        if (locals.Count == 0)
        {
            return;
        }

        InstrumentBlock(block: block, locals: locals);
    }

    /// <summary>
    /// Rewrites <paramref name="block"/>'s statement list in place: a push marker after each
    /// instrumented local's construction, a pop marker before each of its inline <c>destroy</c>,
    /// recursing into nested blocks first.
    /// </summary>
    private void InstrumentBlock(BlockStatement block, HashSet<string> locals)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);

        foreach (Statement stmt in block.Statements)
        {
            RecurseInto(stmt: stmt, locals: locals);

            if (IsDestroyCall(stmt: stmt, local: out string? destroyed) &&
                locals.Contains(item: destroyed!))
            {
                rewritten.Add(item: Marker(fn: PopMarker, local: destroyed!, loc: stmt.Location));
                rewritten.Add(item: stmt);
            }
            else if (stmt is DeclarationStatement { Declaration: VariableDeclaration v } &&
                     locals.Contains(item: v.Name))
            {
                rewritten.Add(item: stmt);
                rewritten.Add(item: Marker(fn: PushMarker, local: v.Name, loc: stmt.Location));
            }
            else
            {
                rewritten.Add(item: stmt);
            }
        }

        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    /// <summary>Descends into a statement's nested blocks so they are instrumented too.</summary>
    private void RecurseInto(Statement stmt, HashSet<string> locals)
    {
        switch (stmt)
        {
            case BlockStatement b:
                InstrumentBlock(block: b, locals: locals);
                break;
            case IfStatement i:
                RecurseStmt(stmt: i.ThenStatement, locals: locals);
                if (i.ElseStatement != null)
                {
                    RecurseStmt(stmt: i.ElseStatement, locals: locals);
                }

                break;
            case WhileStatement w:
                RecurseStmt(stmt: w.Body, locals: locals);
                if (w.ElseBranch != null)
                {
                    RecurseStmt(stmt: w.ElseBranch, locals: locals);
                }

                break;
            case LoopStatement l:
                RecurseStmt(stmt: l.Body, locals: locals);
                break;
            case EachStatement f:
                RecurseStmt(stmt: f.Body, locals: locals);
                if (f.ElseBranch != null)
                {
                    RecurseStmt(stmt: f.ElseBranch, locals: locals);
                }

                break;
            case DangerStatement d:
                InstrumentBlock(block: d.Body, locals: locals);
                break;
            case UsingStatement u:
                RecurseStmt(stmt: u.Body, locals: locals);
                if (u.FallbackBody != null)
                {
                    RecurseStmt(stmt: u.FallbackBody, locals: locals);
                }

                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses)
                {
                    RecurseStmt(stmt: clause.Body, locals: locals);
                }

                break;
        }
    }

    private void RecurseStmt(Statement stmt, HashSet<string> locals)
    {
        if (stmt is BlockStatement b)
        {
            InstrumentBlock(block: b, locals: locals);
        }
        else
        {
            RecurseInto(stmt: stmt, locals: locals);
        }
    }

    private static bool IsDestroyCall(Statement stmt, out string? local)
    {
        if (stmt is ExpressionStatement
            {
                Expression: CallExpression
                {
                    Callee: MemberExpression
                    {
                        MemberName: "destroy", Object: IdentifierExpression id
                    }
                }
            })
        {
            local = id.Name;
            return true;
        }

        local = null;
        return false;
    }

    private static ExpressionStatement Marker(string fn, string local, SourceLocation loc)
    {
        return new ExpressionStatement(Expression: new CallExpression(
                Callee: new IdentifierExpression(Name: fn, Location: loc),
                Arguments: [new IdentifierExpression(Name: local, Location: loc)],
                Location: loc),
            Location: loc);
    }
}
