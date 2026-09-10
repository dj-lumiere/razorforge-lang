using Builder.Instantiation;
using SyntaxTree;

namespace Builder.Lowering;

/// <summary>
/// The single home for the four body-collection walks every Phase-8 lowering pass would otherwise
/// re-hand-roll: the per-program declaration sweep, the variant-body map, and the instantiated
/// generic-body map. Each walk owns ONLY the mechanical skeleton — iterate the collection, hand each
/// body to the pass's own <c>lower</c> callback, and write the result back via a
/// <c>with</c> expression ONLY when the callback returned a different reference. The callback supplies
/// the per-body logic (which core lowering method to call, plus any per-item preamble a stateful pass
/// needs — see <c>RecordCopyLoweringPass</c>'s copy-verb/borrow-param setup).
///
/// <para>Reference identity is the change signal, matching <see cref="AstRewriter"/>: a callback that
/// returns the same body reference means "unchanged" and the collection entry is left untouched.</para>
///
/// <para>Keyed maps are snapshotted with <c>Keys.ToList()</c> before iterating so a callback may mutate
/// the underlying dictionary value (the walks only ever reassign the SAME key).</para>
/// </summary>
internal static class BodyDispatch
{
    /// <summary>
    /// Standard per-program sweep: lower each top-level routine body and each Entity/Record/Crashable
    /// member routine body. <paramref name="lower"/> receives the routine declaration (so a pass can read
    /// its name/owner/parameters) and returns the new body.
    /// </summary>
    public static void RunOnProgram(Program program, Func<RoutineDeclaration, Statement> lower)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                {
                    Statement newBody = lower(arg: r);
                    if (!ReferenceEquals(objA: newBody, objB: r.Body))
                    {
                        program.Declarations[index: i] = r with { Body = newBody };
                    }

                    break;
                }

                case EntityDeclaration e:
                    RunOnMembers(members: e.Members, lower: lower);
                    break;

                case RecordDeclaration rec:
                    RunOnMembers(members: rec.Members, lower: lower);
                    break;

                case CrashableDeclaration cr:
                    RunOnMembers(members: cr.Members, lower: lower);
                    break;
            }
        }
    }

    /// <summary>
    /// Lowers each <see cref="RoutineDeclaration"/> in a type's member list. Same callback contract as
    /// <see cref="RunOnProgram"/> (member routines and top-level routines are lowered identically).
    /// </summary>
    public static void RunOnMembers(List<SyntaxTree.Declaration> members,
        Func<RoutineDeclaration, Statement> lower)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is not RoutineDeclaration m)
            {
                continue;
            }

            Statement newBody = lower(arg: m);
            if (!ReferenceEquals(objA: newBody, objB: m.Body))
            {
                members[index: j] = m with { Body = newBody };
            }
        }
    }

    /// <summary>
    /// Sweeps the variant-body map (<c>ctx.VariantBodies</c>). <paramref name="lower"/> receives the
    /// registry key and current body (some passes key their per-body preamble off the name) and returns
    /// the new body.
    /// </summary>
    public static void RunOnVariantBodies(Dictionary<string, Statement> bodies,
        Func<string, Statement, Statement> lower)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            Statement body = bodies[key: key];
            Statement lowered = lower(arg1: key, arg2: body);
            if (!ReferenceEquals(objA: lowered, objB: body))
            {
                bodies[key: key] = lowered;
            }
        }
    }

    /// <summary>
    /// Sweeps the instantiated generic-body map that GMP populates AFTER the per-program Phase-8 sweep.
    /// Purely-synthesized entries carry no AST and are skipped. <paramref name="lower"/> receives the
    /// registry key and the <see cref="MonomorphizedBody"/> (name, RoutineInfo, parameters all live on
    /// it) and returns the new body; the entry is rebuilt via
    /// <c>entry with { Ast = entry.Ast with { Body = ... } }</c>.
    /// </summary>
    public static void RunOnInstantiatedGenericBodies(Dictionary<string, MonomorphizedBody> bodies,
        Func<string, MonomorphizedBody, Statement> lower)
    {
        foreach (string key in bodies.Keys.ToList())
        {
            MonomorphizedBody entry = bodies[key: key];
            if (entry.IsSynthesized)
            {
                continue;
            }

            Statement lowered = lower(arg1: key, arg2: entry);
            if (!ReferenceEquals(objA: lowered, objB: entry.Ast.Body))
            {
                bodies[key: key] = entry with { Ast = entry.Ast with { Body = lowered } };
            }
        }
    }
}
