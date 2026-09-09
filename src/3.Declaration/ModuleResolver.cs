using Compiler.Diagnostics;
using Compiler.Verification.Results;
using SyntaxTree;

namespace Compiler.Declaration;

/// <summary>
/// Resolves import paths to source files using a pre-built index from parsed ASTs.
/// </summary>
public sealed class ModuleResolver
{
    private readonly string _projectRoot;
    private readonly string _stdlibRoot;
    private readonly IReadOnlyList<string> _libraryRoots;

    private readonly List<SemanticError> _errors = [];

    /// <summary>Gets all errors from module resolution.</summary>
    public List<SemanticError> Errors => _errors;

    /// <summary>
    /// Index mapping "ModuleName" or "ModuleName.SymbolName" to the source file that declares it.
    /// Keys use '/' for module hierarchy and '.' for module-symbol separation, matching the parser's
    /// ImportDeclaration.ModulePath format.
    /// </summary>
    private readonly Dictionary<string, string> _index =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <param name="projectRoot">Project root directory, used as a filesystem fallback for project files.</param>
    /// <param name="stdlibRoot">Standard library root, used as a filesystem fallback when AST pre-registration fails.</param>
    /// <param name="libraryRoots">External library dependency directories (manifest <c>[target] library</c>), searched between project and stdlib.</param>
    public ModuleResolver(string projectRoot, string stdlibRoot,
        IReadOnlyList<string>? libraryRoots = null)
    {
        _projectRoot = projectRoot;
        _stdlibRoot = stdlibRoot;
        _libraryRoots = libraryRoots ?? [];
    }

    /// <summary>
    /// Bulk-seeds import-index entries from a cached snapshot (see <see cref="IndexSnapshot"/>), skipping the
    /// per-file parse that <see cref="BuildDriver"/>'s stdlib pre-scan would otherwise do. The stdlib index is
    /// invariant across a daemon's warm requests (the stdlib source + host target don't change), so building it
    /// once and re-seeding it each request replaces ~0.8 s of re-tokenize/re-parse per compile. Uses
    /// <c>TryAdd</c> so an existing (project/library) entry is never clobbered.
    /// </summary>
    /// <param name="entries">The snapshot entries to seed, mapping module/symbol keys to file paths.</param>
    public void SeedIndex(IReadOnlyDictionary<string, string> entries)
    {
        foreach ((string key, string path) in entries)
            _index.TryAdd(key: key, value: path);
    }

    /// <summary>Returns an immutable copy of the current import index (module/symbol → declaring file), for
    /// daemon-side caching + reuse via <see cref="SeedIndex"/>.</summary>
    public IReadOnlyDictionary<string, string> IndexSnapshot() =>
        new Dictionary<string, string>(dictionary: _index, comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a parsed file into the import index so its module and exported type/routine names
    /// can be resolved by subsequent <c>import</c> statements. Called by <see cref="BuildDriver"/>
    /// after parsing each file (stdlib pre-scan + project files).
    /// </summary>
    /// <param name="filePath">Absolute path to the source file.</param>
    /// <param name="moduleName">The module name — either from a <c>ModuleDeclaration</c> or derived from the file path.</param>
    /// <param name="ast">The parsed program AST to extract exported symbol names from.</param>
    public void RegisterFile(string filePath, string moduleName, Program ast)
    {
        // Register the module itself for bare imports: `import Module`
        _index.TryAdd(key: moduleName, value: filePath);

        foreach (ISyntaxTreeNode node in ast.Declarations)
        {
            switch (node)
            {
                case RecordDeclaration or EntityDeclaration or ChoiceDeclaration
                    or FlagsDeclaration or VariantDeclaration or CrashableDeclaration
                    or ProtocolDeclaration or PresetDeclaration:
                    string typeName = node switch
                    {
                        RecordDeclaration r => r.Name,
                        EntityDeclaration e => e.Name,
                        ChoiceDeclaration c => c.Name,
                        FlagsDeclaration f => f.Name,
                        VariantDeclaration v => v.Name,
                        CrashableDeclaration cr => cr.Name,
                        ProtocolDeclaration p => p.Name,
                        _ => ((PresetDeclaration)node).Name
                    };
                    _index.TryAdd(key: $"{moduleName}.{typeName}", value: filePath);
                    break;
                case DefineDeclaration { OldName: var original, NewName: var alias }:
                    _index.TryAdd(key: $"{moduleName}.{original}", value: filePath);
                    _index.TryAdd(key: $"{moduleName}.{alias}", value: filePath);
                    break;
                case RoutineDeclaration { Name: var n } when !n.Contains(value: '.'):
                    // Module-level routines only; member routines have "Type.name" as their Name
                    _index.TryAdd(key: $"{moduleName}.{n}", value: filePath);
                    break;
            }
        }
    }

    /// <summary>
    /// Enumerates every MODULE registered under the namespace <paramref name="prefix"/> — i.e. every
    /// module whose declared path is a strict descendant (`prefix/Sub`, `prefix/Sub/Deep`, …). Powers
    /// the prefix/package import `import A/B`, which pulls in all of `A/B`'s submodules at once. Only
    /// module keys are returned: symbol keys carry a `.` (`Module.Symbol`) while a module path is pure
    /// slash-hierarchy, so `!Contains('.')` distinguishes them. Sorted for deterministic load order.
    /// </summary>
    public IReadOnlyList<string> EnumerateSubmodulePaths(string prefix)
    {
        string p = prefix.Replace(oldChar: '\\', newChar: '/');
        string needle = p + "/";
        var result = new List<string>();
        foreach (string key in _index.Keys)
        {
            if (key.Contains(value: '.')) continue; // symbol key, not a module
            if (key.StartsWith(value: needle, comparisonType: StringComparison.OrdinalIgnoreCase))
                result.Add(item: key);
        }
        result.Sort(comparer: StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Resolves an import path to a source file path.
    /// Checks the pre-built AST index first; falls back to filesystem probing for project files.
    /// </summary>
    /// <param name="importPath">
    /// The import path as parsed — '/' for module hierarchy, '.' for symbol access.
    /// Examples: "Collections.List", "IO/Console.show", "Collections", "IO/Console".
    /// </param>
    /// <param name="currentFile">The file containing the import statement (unused, kept for API stability).</param>
    /// <param name="location">Source location for error reporting.</param>
    public string? ResolveImport(string importPath, string currentFile, SourceLocation location)
    {
        string? resolved = TryResolveImport(importPath: importPath);

        if (resolved is null)
        {
            _errors.Add(item: new SemanticError(
                Code: SemanticDiagnosticCode.ModuleNotFound,
                Message: $"Cannot resolve import '{importPath}'. Module not found.",
                Location: location));
        }

        return resolved;
    }

    /// <summary>
    /// Resolves an import path to a single source file without recording a not-found error.
    /// Returns null when no single file matches — the caller decides whether to try a
    /// directory-as-module fallback (see <see cref="EnumerateProjectModuleDirectory"/>) before
    /// reporting the import as unresolved.
    /// </summary>
    public string? TryResolveImport(string importPath)
    {
        return _index.TryGetValue(key: importPath, value: out string? resolved)
            ? resolved
            : TryFilesystemFallback(importPath: importPath);
    }

    /// <summary>
    /// Lists candidate source files in a project (or library) directory whose relative path
    /// matches a bare/slash module name — e.g. <c>import Fun2</c> maps to <c>&lt;root&gt;/Fun2/</c>.
    /// This is the directory-as-module case: several files in one directory that all declare the
    /// same <c>module Fun2</c>, which no single-file resolution can gather. Stdlib is intentionally
    /// excluded — its multi-file modules are loaded by <c>StdlibLoader</c>, not the build driver.
    /// The caller must parse each candidate and keep only those whose <c>module</c> declaration
    /// equals <paramref name="moduleName"/>; an unrelated file that merely lives in the directory
    /// is not part of the module.
    /// </summary>
    /// <param name="moduleName">The dot-free import path (e.g. "Fun2", "Geo/Shapes").</param>
    public IReadOnlyList<string> EnumerateProjectModuleDirectory(string moduleName)
    {
        if (moduleName.Contains(value: '.'))
        {
            return [];
        }

        string relPath = moduleName.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);

        // Project root first, then external library roots (manifest [target] library). Stdlib is
        // deliberately omitted — StdlibLoader already gathers multi-file stdlib modules.
        var roots = new List<string>(capacity: 1 + _libraryRoots.Count) { _projectRoot };
        roots.AddRange(collection: _libraryRoots);

        foreach (string root in roots)
        {
            string dir = Path.Combine(path1: root, path2: relPath);
            if (!Directory.Exists(path: dir))
            {
                continue;
            }

            // Sort by ordinal path for OS-independent registration order (see BuildDriver/StdlibLoader).
            var files = new List<string>();
            files.AddRange(collection: Directory.GetFiles(path: dir,
                searchPattern: "*.rf",
                searchOption: SearchOption.TopDirectoryOnly));
            files.AddRange(collection: Directory.GetFiles(path: dir,
                searchPattern: "*.sf",
                searchOption: SearchOption.TopDirectoryOnly));
            // File-granularity conditional compilation: drop `.rf` files whose leading
            // `#@target(...)` directive doesn't match the build target (RazorForge-only).
            files.RemoveAll(match: f => !Compiler.Targeting.TargetGate.ShouldCompile(filePath: f));
            files.Sort(comparer: StringComparer.Ordinal);

            if (files.Count > 0)
            {
                return files;
            }
        }

        return [];
    }

    /// <summary>
    /// Filesystem fallback for imports not yet registered in the AST index.
    /// Covers both project files (discovered organically) and stdlib files whose AST parsing failed
    /// silently during pre-registration. Only converts '/' (module hierarchy separator) to OS path
    /// separators; the '.' module-symbol separator is stripped before constructing file paths.
    /// </summary>
    private string? TryFilesystemFallback(string importPath)
    {
        // Split on the last '.' to separate module path from symbol name.
        // "Collections.List"   -> module="Collections",  symbol="List"
        // "IO/Console.show"    -> module="IO/Console",   symbol="show"
        // "Collections"        -> module="Collections",  symbol=null
        int lastDot = importPath.LastIndexOf(value: '.');
        string modulePart = lastDot >= 0 ? importPath[..lastDot] : importPath;
        string? symbolPart = lastDot >= 0 ? importPath[(lastDot + 1)..] : null;

        // Only '/' is a hierarchy separator; convert to OS path separator.
        string relPath = modulePart.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);

        // Search roots in priority order: project, then external library dependencies
        // (manifest [target] library entries), then stdlib RazorForge, then stdlib Suflae.
        string[] roots =
        [
            _projectRoot,
            .. _libraryRoots,
            Path.Combine(path1: _stdlibRoot, path2: "RazorForge"),
            Path.Combine(path1: _stdlibRoot, path2: "Suflae"),
        ];

        foreach (string root in roots)
        {
            string? found = TryFilesystemRoot(root: root, relPath: relPath, symbolPart: symbolPart);
            if (found != null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Probes one search root for the import's module/symbol under every file-layout convention
    /// (module file, type-per-file symbol, index.rf, same-name-as-directory). Returns the first
    /// existing file, or null if none match under this root.
    /// </summary>
    private static string? TryFilesystemRoot(string root, string relPath, string? symbolPart)
    {
        // Try: root/module.rf  or  .sf
        string modRf = Path.Combine(path1: root, path2: relPath + ".rf");
        if (File.Exists(path: modRf)) return modRf;

        string modSf = Path.Combine(path1: root, path2: relPath + ".sf");
        if (File.Exists(path: modSf)) return modSf;

        // Try: root/module/symbol.rf  (type-per-file convention)
        if (symbolPart is not null)
        {
            string symRf = Path.Combine(path1: root, path2: relPath,
                path3: symbolPart + ".rf");
            if (File.Exists(path: symRf)) return symRf;

            string symSf = Path.Combine(path1: root, path2: relPath,
                path3: symbolPart + ".sf");
            if (File.Exists(path: symSf)) return symSf;
        }

        // Try: root/module/index.rf
        string idxRf = Path.Combine(path1: root, path2: relPath, path3: "index.rf");
        if (File.Exists(path: idxRf)) return idxRf;

        // Try: root/module/module.rf (same-name-as-directory convention, e.g., BuilderQuery/BuilderQuery.rf)
        string dirName = Path.GetFileName(path: relPath);
        if (!string.IsNullOrEmpty(value: dirName))
        {
            string sameNameRf = Path.Combine(path1: root, path2: relPath,
                path3: dirName + ".rf");
            if (File.Exists(path: sameNameRf)) return sameNameRf;

            string sameNameSf = Path.Combine(path1: root, path2: relPath,
                path3: dirName + ".sf");
            if (File.Exists(path: sameNameSf)) return sameNameSf;
        }

        return null;
    }
}
