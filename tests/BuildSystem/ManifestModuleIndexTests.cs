using Builder;

namespace RazorForge.Tests.BuildSystem;

/// <summary>
/// Regression tests for the manifest's executable-module resolution (<c>ManifestLoader.BuildModuleIndex</c>).
/// A module may legally span several files (e.g. one <c>module Lib</c> across many files), so a shared
/// module name must NOT abort manifest loading. The index only needs to resolve the <c>[target] executable</c>
/// to the file that declares the entry point <c>routine start()</c>.
/// </summary>
public sealed class ManifestModuleIndexTests
{
    private const string Manifest = """
        [package]
        name = "test"
        version = "0.0.1"

        [target]
        executable = "App"
        mode = "debug"
        """;

    /// <summary>
    /// Several library files sharing one bare <c>module Lib</c> (none with an entry point) must not
    /// fail manifest loading, and the executable still resolves to the file declaring <c>start()</c>.
    /// </summary>
    [Fact]
    public void DuplicateNonEntryModule_DoesNotFail_AndResolvesExecutable()
    {
        string root = CreateTempProject(new()
        {
            ["config.toml"] = Manifest,
            ["App.rf"] = "module App\n\nroutine start()\n  return\n",
            ["Lib/A.rf"] = "module Lib\n\nrecord A\n  x: S32\n",
            ["Lib/B.rf"] = "module Lib\n\nrecord B\n  y: S32\n",
        });
        try
        {
            ProjectManifest manifest = ManifestLoader.Load(
                tomlPath: Path.Combine(path1: root, path2: "config.toml"));

            Assert.Equal(
                expected: Path.GetFullPath(path: Path.Combine(path1: root, path2: "App.rf")),
                actual: manifest.Target.Executable,
                comparer: StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempProject(root: root);
        }
    }

    /// <summary>
    /// When several files share a module name, the one declaring <c>routine start()</c> is the
    /// executable — even if a library file of the same module is scanned first.
    /// </summary>
    [Fact]
    public void EntryFile_WinsOverLibraryFileOfSameModule()
    {
        string root = CreateTempProject(new()
        {
            ["config.toml"] = Manifest,
            // Both declare `module App`; only the entry file has start().
            ["App/lib.rf"] = "module App\n\nrecord Helper\n  x: S32\n",
            ["App/main.rf"] = "module App\n\nroutine start()\n  return\n",
        });
        try
        {
            ProjectManifest manifest = ManifestLoader.Load(
                tomlPath: Path.Combine(path1: root, path2: "config.toml"));

            Assert.Equal(
                expected: Path.GetFullPath(path: Path.Combine(path1: root, path2: "App", path3: "main.rf")),
                actual: manifest.Target.Executable,
                comparer: StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempProject(root: root);
        }
    }

    /// <summary>
    /// Two entry points for the same module IS a genuine ambiguity and is reported.
    /// </summary>
    [Fact]
    public void TwoEntryPointsForSameModule_Throws()
    {
        string root = CreateTempProject(new()
        {
            ["config.toml"] = Manifest,
            ["main1.rf"] = "module App\n\nroutine start()\n  return\n",
            ["main2.rf"] = "module App\n\nroutine start()\n  return\n",
        });
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(testCode: () =>
                ManifestLoader.Load(tomlPath: Path.Combine(path1: root, path2: "config.toml")));
            Assert.Contains(expectedSubstring: "routine start()", actualString: ex.Message,
                comparisonType: StringComparison.Ordinal);
        }
        finally
        {
            DeleteTempProject(root: root);
        }
    }

    private static string CreateTempProject(Dictionary<string, string> files)
    {
        string root = Path.Combine(path1: Path.GetTempPath(),
            path2: "rf_manifest_" + Guid.NewGuid().ToString(format: "N"));
        Directory.CreateDirectory(path: root);
        foreach ((string relPath, string content) in files)
        {
            string full = Path.Combine(path1: root, path2: relPath);
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
            File.WriteAllText(path: full, contents: content);
        }

        return root;
    }

    private static void DeleteTempProject(string root)
    {
        try
        {
            Directory.Delete(path: root, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
