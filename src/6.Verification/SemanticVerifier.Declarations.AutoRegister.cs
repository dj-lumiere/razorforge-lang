using SyntaxTree;

using Compiler.Instantiation;
using Compiler.Declaration;

namespace Compiler.Verification;

public sealed partial class SemanticVerifier
{
    #region Phase 5: Auto-Register Builder-Generated Member Routines

    /// <summary>
    /// Build-graph signal, precomputed in <see cref="AnalyzeMultiple"/>: does a NON-stdlib file in the
    /// build graph <c>import BuilderQuery</c>? On the multi-file path this is required because
    /// <see cref="TypeRegistry.UserPrograms"/> is not yet populated when the Phase-3 global sweep runs
    /// <see cref="AutoRegisterWiredRoutines"/> — the old UserPrograms scan therefore always saw an empty
    /// list and the gated BuilderQuery entity-list routines never registered. <c>null</c> on the
    /// single-file path, where the UserPrograms scan below is authoritative.
    /// </summary>
    private bool? _builderQueryUserImportedOverride;

    private void AutoRegisterWiredRoutines()
    {
        // Detect whether any USER program imports BuilderQuery. When absent we skip
        // resolving the list-returning reflection types (FieldInfo/ProtocolInfo/RoutineInfo),
        // which would otherwise drag in the full BTreeListNode/Owned/Array/ArrayIterator
        // closure for every type via GMP, even when BuilderQuery is never called.
        // The stdlib itself imports BuilderQuery everywhere, so the signal must be a
        // NON-stdlib import (see the override computed in AnalyzeMultiple).
        bool builderServiceImported = _builderQueryUserImportedOverride ?? ScanUserProgramsForBuilderQuery();

        // The everywhere-derive registration inside the pass consults GetDeriveTemplate to decide which
        // derived operators (lt/le/gt/ge from cmp) to register per type. AutoRegisterWiredRoutines is
        // invoked from several phase orderings (single-file Phase-6 stub synthesis runs BEFORE the
        // Analyze-level RegisterStdlibDeriveTemplates; the multi-file global sweep has no equivalent), so
        // guarantee the template store is populated HERE, immediately before the pass. Idempotent (dedups
        // by arity+gate) and a no-op in warm mode (restored registry re-scans its restored StdlibPrograms).
        RegisterStdlibDeriveTemplates();

        new AutoWiredRegistrationPass(_registry, implicitConformances: _implicitProtocolConformances)
            .Run(builderServiceImported: builderServiceImported);
    }

    /// <summary>
    /// Returns true if any file in the multi-file build graph that lives OUTSIDE the stdlib root
    /// declares <c>import BuilderQuery</c>. The stdlib imports BuilderQuery pervasively, so a stdlib
    /// file's import must not count; only a genuine user/library file opting in does.
    /// </summary>
    private bool DetectUserBuilderQueryImport(System.Collections.Generic.List<(Program Program, string FilePath)> files)
    {
        string? stdlibRoot = _registry.StdlibPath;
        string? normalizedStdlib = stdlibRoot != null
            ? System.IO.Path.GetFullPath(path: stdlibRoot)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar)
            : null;

        foreach ((Program program, string filePath) in files)
        {
            if (normalizedStdlib != null)
            {
                string full = System.IO.Path.GetFullPath(path: filePath);
                if (full.StartsWith(value: normalizedStdlib + System.IO.Path.DirectorySeparatorChar,
                        comparisonType: System.StringComparison.OrdinalIgnoreCase))
                {
                    continue; // real stdlib file — its BuilderQuery import is not a user opt-in
                }
            }

            if (program.Declarations.Any(node => node is ImportDeclaration { ModulePath: "BuilderQuery" }))
                return true;
        }

        return false;
    }

    /// <summary>Fallback scan of the registered user programs (single-file path).</summary>
    private bool ScanUserProgramsForBuilderQuery()
    {
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            if (program.Declarations.Any(node => node is ImportDeclaration { ModulePath: "BuilderQuery" }))
                return true;
        }

        return false;
    }

    #endregion
}
