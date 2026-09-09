using Tomlyn;
using Tomlyn.Model;

namespace Builder;
/// <summary>
/// Loads and validates RazorForge project manifest files.
/// </summary>

public static class ManifestLoader
{
    /// <summary>
    /// Gets the canonical file name for a RazorForge project manifest.
    /// </summary>
    public const string ManifestFileName = "config.toml";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a config.toml file.
    /// Returns the full path to the manifest, or null if not found.
    /// </summary>
    public static string? FindManifest(string startDir)
    {
        string? dir = Path.GetFullPath(path: startDir);
        while (dir != null)
        {
            string candidate = Path.Combine(path1: dir, path2: ManifestFileName);
            if (File.Exists(path: candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(path: dir);
        }

        return null;
    }

    /// <summary>
    /// Parses a config.toml file and returns a <see cref="ProjectManifest"/>.
    /// Validates that required fields are present and resolves entry modules to files.
    /// When <paramref name="resolveExecutable"/> is false (an explicit entry file on the
    /// command line overrides it), <c>executable</c> is optional and left unresolved —
    /// the manifest still supplies mode, library deps, and debug fields.
    /// </summary>
    public static ProjectManifest Load(string tomlPath, bool resolveExecutable = true)
    {
        string fullPath = Path.GetFullPath(path: tomlPath);
        string manifestDir = Path.GetDirectoryName(path: fullPath)!;
        string content = File.ReadAllText(path: fullPath);

        TomlTable root = Toml.ToModel(text: content);

        var manifest = new ProjectManifest { ManifestDirectory = manifestDir };

        // [package]
        if (root.TryGetValue(key: "package", value: out object? packageObj) &&
            packageObj is TomlTable packageTable)
        {
            manifest.Package = ParsePackage(table: packageTable);
        }
        else
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: missing [package] section.");
        }

        if (string.IsNullOrWhiteSpace(value: manifest.Package.Name))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: package.name is required.");
        }

        // Build module index for resolving entry modules (skipped when the CLI entry
        // file overrides [target] executable — avoids a full project scan and lets
        // scratch builds work even while the manifest's executable module is in flux).
        Dictionary<string, string>? moduleIndex = resolveExecutable
            ? BuildModuleIndex(projectDir: manifestDir)
            : null;

        // [target] — the single build description: executable + external library deps.
        if (root.TryGetValue(key: "target", value: out object? targetObj) &&
            targetObj is TomlTable targetTable)
        {
            manifest.Target = ParseBuildTarget(table: targetTable, moduleIndex: moduleIndex,
                manifestDir: manifestDir);
        }
        else
        {
            throw new InvalidOperationException(
                message:
                $"{ManifestFileName}: missing [target] section. Declare what the package builds, e.g.\n" +
                "[target]\nexecutable = \"MainModule\"\nlibrary = [\"../shared-utils\"]\nmode = \"debug\"");
        }

        // [libraries.NAME] — richly-declared foreign C libraries (linkage kind + calling convention). This
        // is where static-vs-dynamic lives, keeping that packaging decision out of source. Optional.
        if (root.TryGetValue(key: "libraries", value: out object? librariesObj) &&
            librariesObj is TomlTable librariesTable)
        {
            foreach ((string libName, object? libValue) in librariesTable)
            {
                if (libValue is TomlTable libTable)
                {
                    manifest.Target.LibraryConfigs[key: libName] =
                        ParseLibrary(name: libName, table: libTable);
                }
            }
        }

        // [debug] — internal compiler diagnostics (formerly the RF_* / RAZORFORGE_JIT_TRACE env vars).
        // All optional; niche developer tooling.
        if (root.TryGetValue(key: "debug", value: out object? debugObj) && debugObj is TomlTable debugTable)
        {
            ParseDebugOptions(debugTable: debugTable, d: manifest.Debug);
        }

        // Resolve external library dependency directories relative to the manifest.
        ResolveLibraryDependencyDirectories(manifest: manifest, manifestDir: manifestDir);

        return manifest;
    }

    /// <summary>Reads the optional <c>[debug]</c> flags into <paramref name="d"/>.</summary>
    private static void ParseDebugOptions(TomlTable debugTable, DebugOptions d)
    {
        if (debugTable.TryGetValue(key: "dump-ast", value: out object? da)) d.DumpAst = da is true;
        if (debugTable.TryGetValue(key: "timing", value: out object? tm)) d.Timing = tm is true;
        if (debugTable.TryGetValue(key: "show-build-stages", value: out object? sbs)) d.ShowBuildStages = sbs is true;
        if (debugTable.TryGetValue(key: "prune-stats", value: out object? ps)) d.PruneStats = ps is true;
        if (debugTable.TryGetValue(key: "jit-trace", value: out object? jt)) d.JitTrace = jt is true;
        if (debugTable.TryGetValue(key: "dump-ir", value: out object? di)) d.DumpIr = di is true;
        if (debugTable.TryGetValue(key: "reachability-dump", value: out object? rd) &&
            !string.IsNullOrWhiteSpace(value: rd?.ToString()))
            d.ReachabilityDump = rd!.ToString();
        if (debugTable.TryGetValue(key: "maysuspend-dump", value: out object? md) &&
            !string.IsNullOrWhiteSpace(value: md?.ToString()))
            d.MaySuspendDump = md!.ToString();
    }

    /// <summary>Resolves each external library dependency directory relative to the manifest,
    /// mutating the target's <c>Libraries</c> list in place and validating existence.</summary>
    private static void ResolveLibraryDependencyDirectories(ProjectManifest manifest, string manifestDir)
    {
        for (int i = 0; i < manifest.Target.Libraries.Count; i++)
        {
            string rawEntry = manifest.Target.Libraries[index: i];
            string resolved = Path.GetFullPath(path: Path.Combine(path1: manifestDir,
                path2: rawEntry));
            if (!Directory.Exists(path: resolved))
            {
                throw new InvalidOperationException(
                    message:
                    $"{ManifestFileName}: library dependency '{rawEntry}' not found (resolved to '{resolved}'). " +
                    "Library entries are directories containing RazorForge modules.");
            }

            manifest.Target.Libraries[index: i] = resolved;
        }
    }

    /// <summary>
    /// Parses one <c>[libraries.NAME]</c> table into a <see cref="CLibrary"/>. Fields (all optional):
    /// <c>name</c> (the <c>-l</c> link name; defaults to the table key), <c>kind</c> (<c>"dynamic"</c>
    /// default / <c>"static"</c>), <c>calling-convention</c> (<c>"c"</c> default).
    /// </summary>
    private static CLibrary ParseLibrary(string name, TomlTable table)
    {
        var lib = new CLibrary { Name = name };

        if (table.TryGetValue(key: "name", value: out object? linkName) &&
            !string.IsNullOrWhiteSpace(value: linkName?.ToString()))
        {
            lib.Name = linkName!.ToString()!.Trim();
        }

        if (table.TryGetValue(key: "kind", value: out object? kindObj))
        {
            string kind = kindObj?.ToString()?.Trim().ToLowerInvariant() ?? "";
            lib.Kind = kind switch
            {
                "static" => CLinkKind.Static,
                "dynamic" or "" => CLinkKind.Dynamic,
                _ => throw new InvalidOperationException(
                    message: $"{ManifestFileName}: [libraries.{name}] kind must be \"static\" or \"dynamic\", got \"{kind}\".")
            };
        }

        if (table.TryGetValue(key: "calling-convention", value: out object? ccObj) &&
            !string.IsNullOrWhiteSpace(value: ccObj?.ToString()))
        {
            lib.CallingConvention = ccObj!.ToString()!.Trim().ToLowerInvariant();
        }

        return lib;
    }

    private static PackageInfo ParsePackage(TomlTable table)
    {
        var pkg = new PackageInfo();

        if (table.TryGetValue(key: "name", value: out object? name))
        {
            pkg.Name = name?.ToString() ?? "";
        }

        if (table.TryGetValue(key: "version", value: out object? version))
        {
            pkg.Version = version?.ToString();
        }

        if (table.TryGetValue(key: "license", value: out object? license))
        {
            pkg.License = license?.ToString();
        }

        if (table.TryGetValue(key: "description", value: out object? description))
        {
            pkg.Description = description?.ToString();
        }

        if (table.TryGetValue(key: "authors", value: out object? authorsObj) &&
            authorsObj is TomlArray authorsArray)
        {
            pkg.Authors = authorsArray.Select(selector: a => a?.ToString() ?? "")
                                      .ToList();
        }

        if (table.TryGetValue(key: "repository", value: out object? repository))
        {
            pkg.Repository = repository?.ToString();
        }

        if (table.TryGetValue(key: "razorforge-version", value: out object? rfVersion))
        {
            pkg.RazorForgeVersion = rfVersion?.ToString();
        }

        return pkg;
    }

    private static BuildTarget ParseBuildTarget(TomlTable table,
        Dictionary<string, string>? moduleIndex, string manifestDir)
    {
        var target = new BuildTarget();
        if (moduleIndex != null)
        {
            target.Executable = ReadRequiredString(table: table,
                key: "executable",
                context: "[target]");
        }
        else if (table.TryGetValue(key: "executable", value: out object? executable))
        {
            // Entry file given on the command line — keep the raw module name for
            // display only; it is neither required nor resolved.
            target.Executable = executable?.ToString() ?? "";
        }

        ParseTargetLibraries(table: table, target: target, manifestDir: manifestDir);

        if (table.TryGetValue(key: "mode", value: out object? mode) &&
            !string.IsNullOrWhiteSpace(value: mode?.ToString()))
        {
            target.Mode = mode!.ToString()!;
        }

        // Dev-loop daemon routing (formerly the RAZORFORGE_DAEMON env var). The JIT dev loop is now the
        // `mode = "debug-jit"` build mode; all other diagnostics live in [debug].
        if (table.TryGetValue(key: "use-daemon", value: out object? useDaemon))
            target.UseDaemon = useDaemon is true;

        // Reserved: incremental compilation (parsed now, consumed once the incremental pipeline lands).
        if (table.TryGetValue(key: "incremental", value: out object? incremental))
            target.Incremental = incremental is true;

        // Resolve the executable's module name to a file path
        if (moduleIndex == null)
        {
            return target;
        }

        ResolveExecutableFile(target: target, moduleIndex: moduleIndex, manifestDir: manifestDir);
        return target;
    }

    /// <summary>Parses the <c>library</c>, <c>c_libraries</c>, and <c>library_paths</c> entries into
    /// <paramref name="target"/>. Each accepts a single string or an array of strings.</summary>
    private static void ParseTargetLibraries(TomlTable table, BuildTarget target, string manifestDir)
    {
        // `library` = EXTERNAL dependency directories (requirements.txt-style), relative
        // to the manifest. Accept a single string or an array of strings.
        if (table.TryGetValue(key: "library", value: out object? libraryObj))
            ParseLibraryEntries(value: libraryObj, target: target);

        // `c_libraries` = external C libraries to link (the `-l` names, e.g. "SDL2"). Names only.
        if (table.TryGetValue(key: "c_libraries", value: out object? cLibsObj))
            ParseCLibraryEntries(value: cLibsObj, target: target);

        // `library_paths` = additional `-L` search directories for `c_libraries`, resolved relative
        // to the manifest directory (absolute entries pass through).
        if (table.TryGetValue(key: "library_paths", value: out object? libPathsObj))
            ParseLibraryPathEntries(value: libPathsObj, target: target, manifestDir: manifestDir);
    }

    /// <summary>Adds non-empty raw library entries to <paramref name="target"/>'s Libraries list.</summary>
    private static void ParseLibraryEntries(object? value, BuildTarget target)
    {
        foreach (string? rawEntry in AsStringEntries(value: value)
                     .Where(predicate: e => !string.IsNullOrWhiteSpace(value: e)))
        {
            target.Libraries.Add(item: rawEntry!);
        }
    }

    /// <summary>Adds trimmed, non-empty C-library names to <paramref name="target"/>'s CLibraries list.</summary>
    private static void ParseCLibraryEntries(object? value, BuildTarget target)
    {
        foreach (string? rawEntry in AsStringEntries(value: value)
                     .Where(predicate: e => !string.IsNullOrWhiteSpace(value: e)))
        {
            target.CLibraries.Add(item: rawEntry!.Trim());
        }
    }

    /// <summary>Resolves and adds library search-path entries to <paramref name="target"/>'s LibraryPaths list.</summary>
    private static void ParseLibraryPathEntries(object? value, BuildTarget target, string manifestDir)
    {
        foreach (string? rawEntry in AsStringEntries(value: value)
                     .Where(predicate: e => !string.IsNullOrWhiteSpace(value: e)))
        {
            target.LibraryPaths.Add(item: Path.GetFullPath(
                path: Path.Combine(path1: manifestDir, path2: rawEntry!.Trim())));
        }
    }

    /// <summary>Normalizes a TOML value that may be a single string or an array of strings into a
    /// sequence of raw string entries.</summary>
    private static IEnumerable<string?> AsStringEntries(object? value) => value switch
    {
        TomlArray array => array.Select(selector: item => item?.ToString()),
        _ => [value?.ToString()]
    };

    /// <summary>Resolves <c>target.Executable</c> (a source-file path or a module name) to a concrete
    /// file path, throwing when the file/module cannot be found.</summary>
    private static void ResolveExecutableFile(BuildTarget target,
        Dictionary<string, string> moduleIndex, string manifestDir)
    {
        // File-based executable (the standard): `executable = "foo.rf"` / a path to an rf/sf file runs
        // that single file directly (module inferred from its path — no `module` declaration needed).
        if (LooksLikeSourceFile(name: target.Executable))
        {
            string filePath = Path.IsPathRooted(path: target.Executable)
                ? target.Executable
                : Path.GetFullPath(path: Path.Combine(path1: manifestDir, path2: target.Executable));
            if (!File.Exists(path: filePath))
            {
                throw new InvalidOperationException(
                    message: $"{ManifestFileName}: executable file '{target.Executable}' not found at {filePath}.");
            }

            target.Executable = filePath;
            return;
        }

        if (!moduleIndex.TryGetValue(key: target.Executable, value: out string? resolvedFile))
        {
            string available = moduleIndex.Count > 0
                ? string.Join(separator: ", ",
                    values: moduleIndex.Keys.OrderBy(keySelector: k => k))
                : "(none found)";
            throw new InvalidOperationException(
                message:
                $"{ManifestFileName}: executable module '{target.Executable}' not found. Available modules: {available}");
        }

        target.Executable = resolvedFile;
    }

    /// <summary>True when the manifest <c>executable</c> value names a source FILE (.rf/.sf) rather
    /// than a module — file-based single-file execution is the standard entry form.</summary>
    private static bool LooksLikeSourceFile(string name) =>
        name.EndsWith(value: ".rf", comparisonType: StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase);

    private static string ReadRequiredString(TomlTable table, string key, string context)
    {
        if (!table.TryGetValue(key: key, value: out object? raw))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: {context} must define '{key}'.");
        }

        string? value = raw?.ToString();
        if (string.IsNullOrWhiteSpace(value: value))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: {context} field '{key}' cannot be empty.");
        }

        return value;
    }

    /// <summary>
    /// Scans all .rf and .sf files under <paramref name="projectDir"/> and builds a
    /// map of module name -> file path by reading module declarations.
    /// </summary>
    /// <remarks>
    /// A module may legally span several files (e.g. one <c>module Fun</c> across many files in a
    /// directory), so a shared module name is NOT an error. This index exists only to resolve a
    /// <c>[target] executable</c> module to its entry file, so when files share a module name the
    /// one declaring <c>routine start()</c> wins. Two entry points for the same module is the only
    /// genuine ambiguity and is reported.
    /// </remarks>
    private static Dictionary<string, string> BuildModuleIndex(string projectDir)
    {
        var index = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);
        var entryModules = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(path: projectDir))
        {
            return index;
        }

        string[] extensions =
        [
            "*.rf",
            "*.sf"
        ];
        foreach (string pattern in extensions)
        {
            foreach (string filePath in Directory.GetFiles(path: projectDir,
                         searchPattern: pattern,
                         searchOption: SearchOption.AllDirectories))
            {
                IndexSourceFile(filePath: filePath, index: index, entryModules: entryModules);
            }
        }

        return index;
    }

    /// <summary>Indexes one source file into <paramref name="index"/>: extracts its module name,
    /// records the mapping, and promotes/validates entry-point-bearing files (throwing on a genuine
    /// two-entry-point ambiguity for one module).</summary>
    private static void IndexSourceFile(string filePath, Dictionary<string, string> index,
        HashSet<string> entryModules)
    {
        // Skip debug AST dump files — they share the module name with the real source
        if (filePath.EndsWith(value: ".rf.desugared",
                comparisonType: StringComparison.OrdinalIgnoreCase))
            return;

        // File-granularity conditional compilation: skip a `.rf` file whose leading
        // `#@target(...)` directive doesn't match the build target (RazorForge-only).
        if (!Compiler.Targeting.TargetGate.ShouldCompile(filePath: filePath))
            return;

        string? moduleName = ExtractModuleName(filePath: filePath);
        if (moduleName == null)
        {
            return;
        }

        string fullPath = Path.GetFullPath(path: filePath);
        bool hasEntryPoint = FileDeclaresEntryPoint(filePath: filePath);

        if (!index.TryGetValue(key: moduleName, value: out string? existingPath))
        {
            index[key: moduleName] = fullPath;
            if (hasEntryPoint)
            {
                entryModules.Add(item: moduleName);
            }

            return;
        }

        // Module name already seen in another file. A library/module file (no entry point)
        // sharing the name is fine — keep whichever entry candidate we already have.
        if (!hasEntryPoint)
        {
            return;
        }

        if (entryModules.Contains(item: moduleName))
        {
            throw new InvalidOperationException(
                message: $"{ManifestFileName}: module '{moduleName}' declares " +
                         $"'routine start()' in both '{existingPath}' and '{fullPath}'.");
        }

        // Promote the entry-bearing file over a previously-indexed library file.
        index[key: moduleName] = fullPath;
        entryModules.Add(item: moduleName);
    }

    /// <summary>
    /// Returns true if the file declares the program entry point <c>routine start()</c>.
    /// Member routines (<c>routine Type.start()</c>) are excluded — only the bare, module-level
    /// <c>start</c> is an entry point.
    /// </summary>
    private static bool FileDeclaresEntryPoint(string filePath)
    {
        try
        {
            return File.ReadLines(path: filePath)
                .Any(predicate: line => line.Trim()
                    .StartsWith(value: "routine start(", comparisonType: StringComparison.Ordinal));
        }
        catch (IOException)
        {
            // Unreadable file contributes no entry point.
            return false;
        }
    }

    /// <summary>
    /// Reads the first "module X" declaration from a source file.
    /// </summary>
    private static string? ExtractModuleName(string filePath)
    {
        try
        {
            foreach (string line in File.ReadLines(path: filePath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith(value: "module "))
                {
                    string name = trimmed["module ".Length..]
                       .Trim();
                    int commentIdx = name.IndexOf(value: '#');
                    if (commentIdx >= 0)
                    {
                        name = name[..commentIdx]
                           .Trim();
                    }

                    return name;
                }

                // Skip comments, empty lines, and imports — stop at first real declaration
                if (!string.IsNullOrWhiteSpace(value: trimmed) &&
                    !trimmed.StartsWith('#') && !trimmed.StartsWith(value: "import "))
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                value: $"Warning: Could not read or parse '{filePath}' for module name extraction: {ex.Message}");
        }

        return null;
    }
}
