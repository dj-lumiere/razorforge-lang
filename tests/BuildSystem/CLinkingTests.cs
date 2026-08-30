using System;
using System.Collections.Generic;
using System.IO;
using Builder;

namespace RazorForge.Tests.BuildSystem;

/// <summary>
/// Tests for external C-library linking config: the manifest's <c>[target] c_libraries</c> /
/// <c>library_paths</c> parse into <see cref="BuildTarget"/>, and
/// <see cref="NativeToolchain.BuildUserLibraryArgs"/> turns them into the clang <c>-L</c>/<c>-l</c>
/// fragment.
/// </summary>
public sealed class CLinkingTests
{
    [Fact]
    public void Manifest_ParsesCLibrariesAndResolvesLibraryPaths()
    {
        string root = CreateTempProject(new()
        {
            ["config.toml"] = """
                [package]
                name = "test"
                version = "0.0.1"

                [target]
                executable = "App"
                mode = "debug"
                c_libraries = ["SDL2", "m"]
                library_paths = ["vendor/lib"]
                """,
            ["App.rf"] = "module App\n\nroutine start()\n  return\n",
        });
        try
        {
            ProjectManifest manifest = ManifestLoader.Load(
                tomlPath: Path.Combine(path1: root, path2: "config.toml"));

            Assert.Equal(expected: new[] { "SDL2", "m" }, actual: manifest.Target.CLibraries);

            // library_paths entries are resolved to absolute paths against the manifest directory.
            Assert.Single(collection: manifest.Target.LibraryPaths);
            Assert.Equal(
                expected: Path.GetFullPath(path: Path.Combine(path1: root, path2: "vendor", path3: "lib")),
                actual: manifest.Target.LibraryPaths[index: 0],
                comparer: StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTempProject(root: root);
        }
    }

    [Fact]
    public void Manifest_ParsesLibraryConfigs()
    {
        string root = CreateTempProject(new()
        {
            ["config.toml"] = """
                [package]
                name = "test"
                version = "0.0.1"

                [target]
                executable = "App"
                mode = "debug"

                [libraries.SDL2]
                kind = "dynamic"
                calling-convention = "c"

                [libraries.physx]
                kind = "static"
                name = "physx_static"
                """,
            ["App.rf"] = "module App\n\nroutine start()\n  return\n",
        });
        try
        {
            ProjectManifest manifest = ManifestLoader.Load(
                tomlPath: Path.Combine(path1: root, path2: "config.toml"));

            Assert.Equal(expected: 2, actual: manifest.Target.LibraryConfigs.Count);

            CLibrary sdl = manifest.Target.LibraryConfigs["SDL2"];
            Assert.Equal(expected: "SDL2", actual: sdl.Name);
            Assert.Equal(expected: CLinkKind.Dynamic, actual: sdl.Kind);
            Assert.Equal(expected: "c", actual: sdl.CallingConvention);

            // `kind = "static"` + a `name` override that differs from the table key.
            CLibrary physx = manifest.Target.LibraryConfigs["physx"];
            Assert.Equal(expected: "physx_static", actual: physx.Name);
            Assert.Equal(expected: CLinkKind.Static, actual: physx.Kind);
        }
        finally
        {
            DeleteTempProject(root: root);
        }
    }

    [Fact]
    public void Manifest_NoCLinkingKeys_YieldsEmptyLists()
    {
        string root = CreateTempProject(new()
        {
            ["config.toml"] = "[package]\nname = \"t\"\nversion = \"0.0.1\"\n\n[target]\nexecutable = \"App\"\n",
            ["App.rf"] = "module App\n\nroutine start()\n  return\n",
        });
        try
        {
            ProjectManifest manifest = ManifestLoader.Load(
                tomlPath: Path.Combine(path1: root, path2: "config.toml"));
            Assert.Empty(collection: manifest.Target.CLibraries);
            Assert.Empty(collection: manifest.Target.LibraryPaths);
        }
        finally
        {
            DeleteTempProject(root: root);
        }
    }

    [Fact]
    public void BuildUserLibraryArgs_EmitsSearchPathsThenLibraries()
    {
        string args = NativeToolchain.BuildUserLibraryArgs(
            cLibraries: ["SDL2", "m"],
            libraryPaths: ["/opt/libs", "C:/vendor"]);

        Assert.Contains(expectedSubstring: "-L\"/opt/libs\"", actualString: args);
        Assert.Contains(expectedSubstring: "-L\"C:/vendor\"", actualString: args);
        Assert.Contains(expectedSubstring: "-lSDL2", actualString: args);
        Assert.Contains(expectedSubstring: "-lm", actualString: args);
        // Search paths must precede the library names so `-l` resolution can find them.
        Assert.True(condition: args.IndexOf(value: "-L\"/opt/libs\"", comparisonType: StringComparison.Ordinal)
                               < args.IndexOf(value: "-lSDL2", comparisonType: StringComparison.Ordinal));
    }

    [Fact]
    public void BuildUserLibraryArgs_EmptyOrNull_ReturnsEmpty()
    {
        Assert.Equal(expected: "", actual: NativeToolchain.BuildUserLibraryArgs(cLibraries: null, libraryPaths: null));
        Assert.Equal(expected: "", actual: NativeToolchain.BuildUserLibraryArgs(cLibraries: [], libraryPaths: []));
    }

    [Fact]
    public void LinkAnnotation_ParsesAllForms()
    {
        // Legacy positional + bare-identifier library reference.
        Assert.Equal(expected: ("SDL2", (string?)null),
            actual: global::TypeModel.Symbols.LinkAnnotation.Parse(annotation: "link(\"SDL2\")"));
        Assert.Equal(expected: ("SDL2", (string?)null),
            actual: global::TypeModel.Symbols.LinkAnnotation.Parse(annotation: "link(SDL2)"));

        // Named library, with and without a symbol-name override.
        Assert.Equal(expected: ("SDL2", (string?)null),
            actual: global::TypeModel.Symbols.LinkAnnotation.Parse(annotation: "link(lib=\"SDL2\")"));
        Assert.Equal(expected: ("SDL2", (string?)"SDL_Init"),
            actual: global::TypeModel.Symbols.LinkAnnotation.Parse(annotation: "link(lib=\"SDL2\", symbol=\"SDL_Init\")"));
    }

    [Fact]
    public void LinkAnnotation_NonLinkAnnotation_IsNull()
    {
        Assert.Equal(expected: ((string?)null, (string?)null),
            actual: global::TypeModel.Symbols.LinkAnnotation.Parse(annotation: "readonly"));
        Assert.Equal(expected: ((string?)null, (string?)null),
            actual: global::TypeModel.Symbols.LinkAnnotation.Parse(annotation: "llvm(\"i32\")"));
    }

    private static string CreateTempProject(Dictionary<string, string> files)
    {
        string root = Path.Combine(path1: Path.GetTempPath(),
            path2: "rf_clink_" + Guid.NewGuid().ToString(format: "N"));
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
