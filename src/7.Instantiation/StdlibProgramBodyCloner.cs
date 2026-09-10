using SyntaxTree;

namespace Builder.Instantiation;

/// <summary>
/// Produces a build-local COPY of a restored stdlib <see cref="Program"/> in which every routine body is a
/// fresh deep clone, while every other node (declaration records, <see cref="RoutineDeclaration.ResolvedInfo"/>
/// links, member variables, type expressions) is shared unchanged.
///
/// <para>WHY: on a warm compile the daemon serves many builds from ONE captured stdlib snapshot, whose program
/// ASTs are shared BY REFERENCE across every build. A routine that was not reached at capture time is still
/// UN-lowered in the restored program; the first warm build that reaches it runs on-demand analysis +
/// desugaring + lowering, which reassigns declarations and annotates call nodes IN PLACE. Mutating the shared
/// program leaks that build's lowering into the next build's template — the warm/cold define-set divergence
/// (a spurious extra define, e.g. Range[U64]'s creator, appearing on the 2nd+ warm build). Cloning the reached
/// program's bodies before on-demand analysis gives each build its own mutable copy so the shared snapshot
/// stays pristine, WITHOUT re-cloning the whole snapshot up front — only reached files are copied.</para>
///
/// <para>Bodies are cloned via <see cref="GenericAstRewriter.RewriteStatement(Statement,
/// System.Collections.Generic.Dictionary{string,string})"/> with an empty substitution map (a pure structural
/// deep-copy). The <see cref="RoutineDeclaration.ResolvedInfo"/> is preserved (record <c>with</c> copies it),
/// so registry keys and on-demand re-registration stay consistent.</para>
/// </summary>
internal static class StdlibProgramBodyCloner
{
    private static readonly Dictionary<string, string> NoSubs =
        new(comparer: StringComparer.Ordinal);

    /// <summary>Returns a new <see cref="Program"/> with a fresh <see cref="Program.Declarations"/> list whose
    /// routine bodies (top-level and inside type declarations) are deep-cloned; all other structure is shared.</summary>
    public static Program CloneBodies(Program program)
    {
        var decls = new List<ISyntaxTreeNode>(capacity: program.Declarations.Count);
        foreach (ISyntaxTreeNode d in program.Declarations)
        {
            decls.Add(item: CloneDeclaration(node: d));
        }

        return program with { Declarations = decls };
    }

    private static ISyntaxTreeNode CloneDeclaration(ISyntaxTreeNode node)
    {
        return node switch
        {
            RoutineDeclaration r => CloneRoutine(r: r),
            EntityDeclaration e => e with { Members = CloneMembers(members: e.Members) },
            RecordDeclaration rec => rec with { Members = CloneMembers(members: rec.Members) },
            CrashableDeclaration c => c with { Members = CloneMembers(members: c.Members) },
            ChoiceDeclaration ch => ch with
            {
                MemberRoutines = ch.MemberRoutines.Select(selector: CloneRoutine).ToList()
            },
            _ => node
        };
    }

    private static List<SyntaxTree.Declaration> CloneMembers(List<SyntaxTree.Declaration> members)
    {
        var outMembers = new List<SyntaxTree.Declaration>(capacity: members.Count);
        foreach (SyntaxTree.Declaration m in members)
        {
            outMembers.Add(item: m is RoutineDeclaration r
                ? CloneRoutine(r: r)
                : m);
        }

        return outMembers;
    }

    private static RoutineDeclaration CloneRoutine(RoutineDeclaration r)
    {
        // A GENERIC-DEFINITION template's body must stay UNFOLDED until monomorphization binds the type params
        // (a buildtime construct like `nameof`/`orderof`/`expand` inside a `WhereIterable[T,S]` template must
        // fold with the concrete T/S, not prematurely) — so its body is cloned with a PURE STRUCTURAL deep-copy
        // (GenericAstRewriter.DeepCloneStatement, CloneOnly mode: no folding), whereas a concrete body is cloned
        // by the substituting rewrite (which is safe/idempotent when params are already bound). BOTH are cloned:
        // even a generic-def template is body-analyzed on demand per warm build (on-demand SA annotates
        // `CreatorExpression.ResolvedCreatorRoutine` / `ResolvedType` IN PLACE), so sharing it across builds leaks
        // one build's annotations into the next — the warm/cold define-set divergence (a spurious extra define,
        // e.g. `Range[U64]`'s field-init creator, on the 2nd+ warm build). Cloning gives each build its own copy.
        bool isGenericDef = r.GenericParameters is { Count: > 0 } ||
                            r.ResolvedInfo is { IsGenericDefinition: true } ||
                            r.ResolvedInfo?.OwnerType is { IsGenericDefinition: true };

        // `with` preserves ResolvedInfo + every other field; only the body is a fresh clone.
        return r with
        {
            Body = isGenericDef
                ? GenericAstRewriter.DeepCloneStatement(stmt: r.Body)
                : GenericAstRewriter.RewriteStatement(stmt: r.Body, subs: NoSubs)
        };
    }
}
