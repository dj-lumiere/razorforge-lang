using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace RazorForge.Tests.Analyzer;

/// <summary>
/// Regression tests for importing a directory-as-module: several files in one directory that all
/// declare the same bare <c>module Name</c> (the module name differs from each file's path). These
/// exercise the real multi-file pipeline (<see cref="BuildDriver"/> discovery +
/// <see cref="SemanticVerifier.AnalyzeMultiple"/>) over temp files on disk, mirroring the
/// <c>check</c> verb. Two bugs are guarded:
/// <list type="bullet">
/// <item>Member import of a shared bare module (<c>import Name.A</c>) double-registered the file
/// (RF-S406/S400/S450) because module-load deduped on the import-path alias, not the resolved file.</item>
/// <item>Bare/selective whole-module import (<c>import Name</c> / <c>import Name.[A, B]</c>) failed
/// with RF-S105 because the build driver never discovered the sibling files declaring that module.</item>
/// </list>
/// </summary>
public sealed class MultiFileModuleImportTests
{
    // Two files in the Shapes/ directory, both declaring `module Shapes` — the directory-as-module
    // layout. A whole-module import must gather BOTH; a member import of one must not re-register it.
    private static readonly (string RelPath, string Source)[] SharedModuleFiles =
    [
        ("Shapes/Alpha.rf", """
                            module Shapes
                            record Alpha
                              secret value: S32

                            routine Alpha.doubled() -> S32
                              return me.value * 2
                            """),
        ("Shapes/Beta.rf", """
                           module Shapes
                           record Beta
                             secret value: S32

                           routine Beta.tripled() -> S32
                             return me.value * 3
                           """)
    ];

    /// <summary>
    /// Bare <c>import Shapes</c> gathers every file declaring <c>module Shapes</c>, so both Alpha
    /// (from Shapes/Alpha.rf) and Beta (from Shapes/Beta.rf) resolve.
    /// </summary>
    [Fact]
    public void BareImport_GathersAllFilesOfSharedModule_NoErrors()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module ImportProbe
                                                             import IO/Console
                                                             import Shapes

                                                             routine start()
                                                               var a = Alpha(value: 5)
                                                               var b = Beta(value: 5)
                                                               show(f"{a.doubled()} {b.tripled()}")
                                                               return
                                                             """,
            moduleFiles: SharedModuleFiles);

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    /// <summary>
    /// Selective <c>import Shapes.[Alpha, Beta]</c> (parsed with the dot-free ModulePath "Shapes")
    /// also gathers the whole directory-as-module.
    /// </summary>
    [Fact]
    public void SelectiveImport_GathersAllFilesOfSharedModule_NoErrors()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module ImportProbe
                                                             import IO/Console
                                                             import Shapes.[Alpha, Beta]

                                                             routine start()
                                                               var a = Alpha(value: 5)
                                                               var b = Beta(value: 5)
                                                               show(f"{a.doubled()} {b.tripled()}")
                                                               return
                                                             """,
            moduleFiles: SharedModuleFiles);

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    /// <summary>
    /// A single member import (<c>import Shapes.Alpha</c>) of a file whose declared module name
    /// ("Shapes") differs from its path must not re-register the file (was RF-S406).
    /// </summary>
    [Fact]
    public void SingleMemberImport_OfSharedBareModule_NoErrors()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module ImportProbe
                                                             import IO/Console
                                                             import Shapes.Alpha

                                                             routine start()
                                                               var a = Alpha(value: 5)
                                                               show(f"{a.doubled()}")
                                                               return
                                                             """,
            moduleFiles: SharedModuleFiles);

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    /// <summary>
    /// Two member imports of the same shared bare module (<c>import Shapes.Alpha</c> +
    /// <c>import Shapes.Beta</c>) must not collide or mis-resolve (was RF-S400/S406/S450).
    /// </summary>
    [Fact]
    public void TwoMemberImports_OfSharedBareModule_NoErrors()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module ImportProbe
                                                             import IO/Console
                                                             import Shapes.Alpha
                                                             import Shapes.Beta

                                                             routine start()
                                                               var a = Alpha(value: 5)
                                                               var b = Beta(value: 5)
                                                               show(f"{a.doubled()} {b.tripled()}")
                                                               return
                                                             """,
            moduleFiles: SharedModuleFiles);

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    /// <summary>
    /// A genuinely missing module still reports RF-S105, so the directory-as-module fallback does
    /// not mask real resolution failures.
    /// </summary>
    [Fact]
    public void MissingModule_StillReportsModuleNotFound()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module ImportProbe
                                                             import NoSuchModule

                                                             routine start()
                                                               return
                                                             """,
            moduleFiles: SharedModuleFiles);

        Assert.Contains(collection: errors,
            filter: e => e.Code == SemanticDiagnosticCode.ModuleNotFound);
    }

    /// <summary>
    /// A generic free routine in an imported module is callable cross-module with EXPLICIT type
    /// arguments (<c>gen_id[S32](7)</c>). Concrete free routines always resolved; generic ones used
    /// to fail because the explicit-generic call path missed the generic-overload index.
    /// </summary>
    [Fact]
    public void GenericFreeRoutine_ExplicitTypeArgs_ResolvesCrossModule_NoErrors()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module ImportProbe
                                                             import GenLib

                                                             routine start()
                                                               var a = gen_id[S32](7)
                                                               var b = con_id(5)
                                                               return
                                                             """,
            moduleFiles:
            [
                ("GenLib/Lib.rf", """
                                  module GenLib
                                  routine con_id(x: S32) -> S32
                                    return x

                                  routine gen_id[T](x: T) -> T
                                    return x
                                  """)
            ]);

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    // A module spread across three same-directory files; Main uses Aaa, and Aaa internally uses its
    // sibling Bbb — all `module SameMod`, with NO import linking them. Used for the entry-file case.
    private static readonly (string RelPath, string Source)[] SameModuleSiblingFiles =
    [
        ("SameMod/Aaa.rf", """
                           module SameMod
                           record Aaa
                             secret value: S32

                           routine Aaa.combined() -> S32
                             var b = Bbb(value: 10)
                             return me.value + b.tripled()
                           """),
        ("SameMod/Bbb.rf", """
                           module SameMod
                           record Bbb
                             secret value: S32

                           routine Bbb.tripled() -> S32
                             return me.value * 3
                           """)
    ];

    /// <summary>
    /// A file that is itself part of a multi-file module and is the compilation entry can use its
    /// same-module siblings' types with no import — the build driver auto-gathers them.
    /// </summary>
    [Fact]
    public void EntryModuleFile_SeesSiblingsWithoutImport_NoErrors()
    {
        List<SemanticError> errors = RunProject(entrySource: """
                                                             module SameMod
                                                             import IO/Console

                                                             routine start()
                                                               var a = Aaa(value: 5)
                                                               show(f"{a.combined()}")
                                                               return
                                                             """,
            moduleFiles: SameModuleSiblingFiles,
            entryRelPath: "SameMod/Main.rf");

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    /// <summary>
    /// Sibling auto-gather only pulls in files declaring the SAME module: a broken file in the same
    /// directory declaring a different module is not analyzed, so it cannot leak errors.
    /// </summary>
    [Fact]
    public void EntryModuleFile_DoesNotGatherDifferentModuleSibling_NoErrors()
    {
        var files = new List<(string RelPath, string Source)>(collection: SameModuleSiblingFiles)
        {
            // Declares a DIFFERENT module and would error if analyzed (unknown type call).
            ("SameMod/Other.rf", """
                                 module OtherMod
                                 record Zzz
                                   secret value: S32

                                 routine Zzz.broken() -> S32
                                   return NonExistentType.nope()
                                 """)
        };

        List<SemanticError> errors = RunProject(entrySource: """
                                                             module SameMod
                                                             import IO/Console

                                                             routine start()
                                                               var a = Aaa(value: 5)
                                                               show(f"{a.combined()}")
                                                               return
                                                             """,
            moduleFiles: files,
            entryRelPath: "SameMod/Main.rf");

        Assert.True(condition: errors.Count == 0, userMessage: RenderErrors(errors: errors));
    }

    /// <summary>
    /// Writes the entry file plus the module files to a fresh temp directory and runs the multi-file
    /// build pipeline (mirrors the <c>check</c> verb in Program.cs), returning all reported errors.
    /// </summary>
    /// <param name="entrySource">Source of the entry (compiled) file.</param>
    /// <param name="moduleFiles">Additional files written alongside, keyed by relative path.</param>
    /// <param name="entryRelPath">Relative path for the entry file (default at the project root).</param>
    private static List<SemanticError> RunProject(string entrySource,
        IReadOnlyList<(string RelPath, string Source)> moduleFiles,
        string entryRelPath = "ImportProbe.rf")
    {
        string root = Path.Combine(path1: Path.GetTempPath(),
            path2: "rf_mfimport_" + Guid.NewGuid()
                                        .ToString(format: "N"));
        Directory.CreateDirectory(path: root);
        try
        {
            foreach ((string relPath, string source) in moduleFiles)
            {
                string full = Path.Combine(path1: root, path2: relPath);
                Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
                File.WriteAllText(path: full, contents: source);
            }

            string entryFile = Path.Combine(path1: root, path2: entryRelPath);
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: entryFile)!);
            File.WriteAllText(path: entryFile, contents: entrySource);

            return AnalyzeProject(entryFile: entryFile);
        }
        finally
        {
            try
            {
                Directory.Delete(path: root, recursive: true);
            }
            catch
            {
                // Best-effort cleanup; a leaked temp dir must not fail the test.
            }
        }
    }

    /// <summary>
    /// Runs BuildDriver discovery then multi-file semantic analysis, exactly as the <c>check</c>
    /// verb does. Build-graph errors abort before SA (and are returned as-is); otherwise the SA
    /// errors are returned.
    /// </summary>
    private static List<SemanticError> AnalyzeProject(string entryFile)
    {
        string fullEntry = Path.GetFullPath(path: entryFile);
        string projectRoot = Path.GetDirectoryName(path: fullEntry)!;
        string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

        var driver = new BuildDriver(projectRoot: projectRoot,
            stdlibRoot: stdlibRoot,
            language: Language.RazorForge);
        BuildResult buildResult = driver.CompileFile(entryFile: fullEntry);

        // Build errors (e.g. RF-S105 unresolved import) abort before SA in the real pipeline.
        if (buildResult.Errors.Count > 0)
        {
            return buildResult.Errors;
        }

        // Drop stdlib units (TypeRegistry/StdlibLoader own those) and order the user files.
        string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
        var userUnits = buildResult.Units
                                   .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                                               .StartsWith(value: normalizedStdlib,
                                                                    comparisonType:
                                                                    StringComparison
                                                                       .OrdinalIgnoreCase))
                                   .ToList();

        var unitsByModule =
            new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (FileBuildUnit unit in userUnits)
        {
            string moduleName =
                unit.Module ?? Path.GetFileNameWithoutExtension(path: unit.FilePath);
            unitsByModule[key: moduleName] = unit;
        }

        var orderedFiles = new List<(Program Program, string FilePath)>();
        foreach (string moduleName in buildResult.InitializationOrder)
        {
            if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
            {
                orderedFiles.Add(item: (unit.Ast, unit.FilePath));
            }
        }

        // Files not covered by the init order (e.g. siblings sharing a module name, or the entry
        // file with no module dependency) are appended so every user file is analyzed.
        foreach (FileBuildUnit unit in userUnits)
        {
            if (!orderedFiles.Any(predicate: f => string.Equals(a: f.FilePath,
                    b: unit.FilePath,
                    comparisonType: StringComparison.OrdinalIgnoreCase)))
            {
                orderedFiles.Add(item: (unit.Ast, unit.FilePath));
            }
        }

        var analyzer = new SemanticVerifier(language: Language.RazorForge);
        analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
        AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);
        return result.Errors;
    }

    private static string RenderErrors(List<SemanticError> errors)
    {
        string rendered = string.Join(separator: "\n",
            values: errors.Select(selector: e => $"  [{e.Code}] {e.Message} at {e.Location}"));
        return $"Expected no errors but got {errors.Count}:\n{rendered}";
    }
}
