using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using Compiler.Parser;
using Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Compiler.Declaration;

/// <summary>
/// Result of building a single source file.
/// </summary>
/// <param name="FilePath">The path to the source file.</param>
/// <param name="Module">The module declared in the file (module path).</param>
/// <param name="Ast">The parsed AST.</param>
/// <param name="Imports">Import declarations found in the file.</param>
/// <param name="ParseWarnings">Warnings from parsing.</param>
public sealed record FileBuildUnit(
    string FilePath,
    string? Module,
    Program Ast,
    List<ImportDeclaration> Imports,
    List<BuildWarning> ParseWarnings);

/// <summary>
/// Result of a complete multi-file build.
/// </summary>
/// <param name="Units">All successfully built file units.</param>
/// <param name="Errors">All errors encountered during building.</param>
/// <param name="Warnings">All warnings encountered during building.</param>
/// <param name="InitializationOrder">Modules in safe initialization order.</param>
public sealed record BuildResult(
    List<FileBuildUnit> Units,
    List<SemanticError> Errors,
    List<BuildWarning> Warnings,
    List<string> InitializationOrder);

/// <summary>
/// Coordinates multi-file building with circular import detection.
/// This is the entry point for building RazorForge/Suflae projects.
/// </summary>
public sealed class BuildDriver
{
    private readonly ModuleDependencyGraph _dependencyGraph = new();
    private readonly ModuleResolver _resolver;
    private readonly Language _language;
    private readonly string _stdlibRoot;

    private readonly List<SemanticError> _errors = [];
    private readonly List<BuildWarning> _warnings = [];
    private readonly Dictionary<string, FileBuildUnit> _compiledUnits = [];
    private readonly HashSet<string> _processingFiles = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="BuildDriver"/> class.
    /// </summary>
    /// <param name="projectRoot">The root directory of the project.</param>
    /// <param name="stdlibRoot">The root directory of the standard library.</param>
    /// <param name="language">The language being built.</param>
    /// <param name="libraryRoots">
    /// External library dependency directories from the manifest's <c>[target] library</c>
    /// list; their modules join the import search space between the project and the stdlib.
    /// </param>
    public BuildDriver(string projectRoot, string stdlibRoot, Language language,
        IReadOnlyList<string>? libraryRoots = null)
    {
        _projectRoot = projectRoot;
        _libraryRoots = libraryRoots ?? [];
        _resolver = new ModuleResolver(projectRoot: projectRoot, stdlibRoot: stdlibRoot,
            libraryRoots: _libraryRoots);
        _stdlibRoot = stdlibRoot;
        _language = language;
    }

    private readonly string _projectRoot;

    private readonly IReadOnlyList<string> _libraryRoots;

    /// <summary>
    /// The driver's module resolver, fully indexed after <see cref="CompileFiles"/>
    /// (stdlib pre-scan + library roots + every project file processed). Share this with
    /// <c>TypeRegistry.UseModuleResolver</c> so SA-phase import loading resolves the same
    /// module set the build graph did.
    /// </summary>
    public ModuleResolver Resolver => _resolver;

    /// <summary>
    /// Builds a single source file and all its dependencies.
    /// </summary>
    /// <param name="entryFile">The main source file to build.</param>
    /// <returns>The build result with all units and errors.</returns>
    public BuildResult CompileFile(string entryFile)
    {
        return CompileFiles(sourceFiles: [entryFile]);
    }

    /// <summary>
    /// Builds multiple source files and all their dependencies.
    /// </summary>
    /// <param name="sourceFiles">The source files to build.</param>
    /// <returns>The build result with all units and errors.</returns>
    public BuildResult CompileFiles(List<string> sourceFiles)
    {
        // Pre-register all stdlib files so imports resolve without filesystem probing.
        PreRegisterStdlib();
        // Pre-register external library dependencies ([target] library) the same way —
        // their modules declare names in `module` headers, not file-path conventions.
        PreRegisterLibraryRoots();

        // Process each entry file
        foreach (string sourceFile in sourceFiles)
        {
            if (!File.Exists(path: sourceFile))
            {
                _errors.Add(item: new SemanticError(
                    Code: SemanticDiagnosticCode.SourceFileNotFound,
                    Message: $"Source file not found: '{sourceFile}'",
                    Location: new SourceLocation(FileName: sourceFile,
                        Line: 0,
                        Column: 0,
                        Position: 0)));
                continue;
            }

            ProcessFile(filePath: sourceFile, fromFile: null, importLocation: null, importPathString: null);
        }

        // Collect errors from resolver and dependency graph
        _errors.AddRange(collection: _resolver.Errors);
        _errors.AddRange(collection: _dependencyGraph.Errors);

        // Get initialization order (if no cycles)
        List<string> initOrder = [];
        if (_dependencyGraph.Errors.Count == 0)
        {
            try
            {
                initOrder = _dependencyGraph.GetInitializationOrder();
            }
            catch (InvalidOperationException ex)
            {
                _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.CircularImport,
                    Message: ex.Message,
                    Location: new SourceLocation(FileName: "",
                        Line: 0,
                        Column: 0,
                        Position: 0)));
            }
        }

        return new BuildResult(Units: _compiledUnits.Values.ToList(),
            Errors: _errors,
            Warnings: _warnings,
            InitializationOrder: initOrder);
    }

    /// <summary>
    /// Processes a single file: parses it, extracts imports, and recursively processes dependencies.
    /// </summary>
    private void ProcessFile(string filePath, string? fromFile, SourceLocation? importLocation, string? importPathString)
    {
        // Normalize the path
        filePath = Path.GetFullPath(path: filePath);

        // Skip if already built
        if (_compiledUnits.ContainsKey(key: filePath))
        {
            HandleAlreadyBuiltFile(filePath: filePath,
                fromFile: fromFile,
                importLocation: importLocation,
                importPathString: importPathString);
            return;
        }

        // Detect re-entrant processing (shouldn't happen with proper cycle detection)
        if (_processingFiles.Contains(item: filePath))
        {
            return;
        }

        _processingFiles.Add(item: filePath);

        try
        {
            // Parse the file
            FileBuildUnit? unit = ParseFile(filePath: filePath);
            if (unit == null)
            {
                return;
            }

            // Register the module
            string modulePath = unit.Module ?? Path.GetFileNameWithoutExtension(path: filePath);
            _dependencyGraph.GetOrCreateModule(modulePath: modulePath, sourceFile: filePath);

            // Validate that `/`-form imports refer to an actual module of that path.
            // `import Collections/BitList` is an error if the file declares `module Collections`
            // (BitList is a type, not a submodule).
            if (importPathString != null && importLocation != null)
            {
                ValidateSlashFormImport(
                    importPathString: importPathString,
                    actualModule: modulePath,
                    importLocation: importLocation);
            }

            // Track dependencies from imports. Returns false when a circular dependency is
            // detected, in which case processing must stop.
            if (!TrackImportDependency(modulePath: modulePath,
                    fromFile: fromFile,
                    importLocation: importLocation,
                    importPathString: importPathString))
            {
                return;
            }

            // Store the unit
            _compiledUnits[key: filePath] = unit;
            _warnings.AddRange(collection: unit.ParseWarnings);

            // Register into the resolver index so later imports of this file resolve correctly.
            _resolver.RegisterFile(filePath: filePath, moduleName: modulePath, ast: unit.Ast);

            // Pull in the rest of this file's own module: sibling files in the same directory that
            // declare the same `module`. Files of one module see each other's types without an
            // import, so they must all be analyzed together — even when no `import` links them and
            // this file is the compilation entry (otherwise a sibling's type reads as unknown).
            if (unit.Module != null)
            {
                ProcessSameModuleSiblings(filePath: filePath, modulePath: unit.Module);
            }

            // Process imports recursively
            foreach (ImportDeclaration import in unit.Imports)
            {
                ProcessImport(import: import, filePath: filePath);
            }
        }
        finally
        {
            _processingFiles.Remove(item: filePath);
        }
    }

    /// <summary>
    /// Handles a file that was already built: re-validates the `/`-form import path against the
    /// file's actual module (the only remaining work when the unit already exists).
    /// </summary>
    private void HandleAlreadyBuiltFile(string filePath, string? fromFile,
        SourceLocation? importLocation, string? importPathString)
    {
        // Even if already built, validate the `/`-form import path against the file's actual module.
        if (fromFile != null && importPathString != null && importLocation != null
            && _compiledUnits.TryGetValue(key: filePath, value: out FileBuildUnit? existing))
        {
            ValidateSlashFormImport(
                importPathString: importPathString,
                actualModule: existing.Module ?? Path.GetFileNameWithoutExtension(path: filePath),
                importLocation: importLocation);
        }
    }

    /// <summary>
    /// Records the import dependency edge for the just-parsed file. Skips self-dependencies for
    /// member imports (`.` form) and Core auto-imports — they're no-ops. Returns false when the
    /// edge creates a circular dependency (the caller must then stop processing), true otherwise.
    /// </summary>
    private bool TrackImportDependency(string modulePath, string? fromFile,
        SourceLocation? importLocation, string? importPathString)
    {
        // Track dependencies from imports.
        // Skip self-dependencies for member imports (`.` form) and Core auto-imports — they're no-ops.
        if (fromFile != null && importLocation != null)
        {
            string fromModule = GetModuleForFile(filePath: fromFile);

            bool isMemberImport = importPathString != null && importPathString.Contains(value: '.');
            bool isSelfMemberImport = isMemberImport && fromModule == modulePath;
            bool isCoreAutoImport = modulePath == "Core";

            if (!isSelfMemberImport && !isCoreAutoImport)
            {
                bool success = _dependencyGraph.AddDependency(fromModule: fromModule,
                    toModule: modulePath,
                    importLocation: importLocation);

                if (!success)
                {
                    // Circular dependency detected - don't continue processing
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves and recursively processes a single import declaration, trying single-file resolution
    /// first, then the directory-as-module case, then the prefix/package case, and finally reporting
    /// the import as unresolved.
    /// </summary>
    private void ProcessImport(ImportDeclaration import, string filePath)
    {
        string? resolvedPath = _resolver.TryResolveImport(importPath: import.ModulePath);

        if (resolvedPath != null)
        {
            ProcessFile(filePath: resolvedPath,
                fromFile: filePath,
                importLocation: import.Location,
                importPathString: import.ModulePath);
            return;
        }

        // No single file matched. Try the directory-as-module case: a bare/slash import
        // (`import Fun2` / `import Fun2.[A, B]`) naming a directory whose files all declare
        // the same `module Fun2`. Gather and process every such file so the whole module
        // is available, not just one arbitrary anchor file.
        if (ProcessDirectoryModule(moduleName: import.ModulePath,
                fromFile: filePath,
                importLocation: import.Location))
        {
            return;
        }

        // Prefix/package import: `import A/B` pulls in every submodule declaring `module A/B/...`
        // (keyed by DECLARED module path, not directory — a file's path need not mirror its
        // module). Process each submodule's file into the graph so SA sees them all.
        IReadOnlyList<string> submodules =
            _resolver.EnumerateSubmodulePaths(prefix: import.ModulePath);
        if (submodules.Count > 0)
        {
            foreach (string submodule in submodules)
            {
                string? subPath = _resolver.TryResolveImport(importPath: submodule);
                if (subPath != null)
                    ProcessFile(filePath: subPath,
                        fromFile: filePath,
                        importLocation: import.Location,
                        importPathString: submodule);
            }
            return;
        }

        // Truly unresolved — report it (TryResolveImport, unlike ResolveImport, records
        // no error of its own).
        _errors.Add(item: new SemanticError(
            Code: SemanticDiagnosticCode.ModuleNotFound,
            Message: $"Cannot resolve import '{import.ModulePath}'. Module not found.",
            Location: import.Location));
    }

    /// <summary>
    /// Resolves and processes a directory-as-module import: several files in one directory that
    /// all declare the same <c>module</c> name (e.g. <c>Fun2/A.rf</c> and <c>Fun2/B.rf</c> both
    /// declaring <c>module Fun2</c>), gathered by a bare/selective import (<c>import Fun2</c> or
    /// <c>import Fun2.[A, B]</c>) that no single-file resolution can satisfy.
    /// Only files whose <c>module</c> declaration equals <paramref name="moduleName"/> are taken,
    /// so an unrelated file living in the directory is not pulled in.
    /// </summary>
    /// <returns>True if at least one matching file was found and processed.</returns>
    private bool ProcessDirectoryModule(string moduleName, string fromFile,
        SourceLocation importLocation)
    {
        IReadOnlyList<string> candidates =
            _resolver.EnumerateProjectModuleDirectory(moduleName: moduleName);
        if (candidates.Count == 0)
        {
            return false;
        }

        var matched = new List<string>();
        foreach (string candidate in candidates)
        {
            // Cheap `module` line scan (no full parse) so an unrelated or broken file living in the
            // directory is neither parsed nor pulled in.
            if (ReadDeclaredModule(filePath: candidate) == moduleName)
            {
                matched.Add(item: candidate);
            }
        }

        if (matched.Count == 0)
        {
            return false;
        }

        foreach (string file in matched)
        {
            ProcessFile(filePath: file,
                fromFile: fromFile,
                importLocation: importLocation,
                importPathString: moduleName);
        }

        return true;
    }

    /// <summary>
    /// Discovers and processes the other files of <paramref name="filePath"/>'s own module: sibling
    /// <c>.rf</c>/<c>.sf</c> files in the same directory that declare the same
    /// <paramref name="modulePath"/>. This makes a module's files mutually visible without an explicit
    /// import, including when one of them is the compilation entry and nothing imports the module.
    /// Only same-directory siblings are gathered, matching the one-module-per-directory convention
    /// (and the directory-as-module import resolution).
    /// </summary>
    private void ProcessSameModuleSiblings(string filePath, string modulePath)
    {
        string? directory = Path.GetDirectoryName(path: filePath);
        if (directory is null)
        {
            return;
        }

        var candidates = new List<string>();
        candidates.AddRange(collection: Directory.GetFiles(path: directory,
            searchPattern: "*.rf",
            searchOption: SearchOption.TopDirectoryOnly));
        candidates.AddRange(collection: Directory.GetFiles(path: directory,
            searchPattern: "*.sf",
            searchOption: SearchOption.TopDirectoryOnly));

        foreach (string candidate in candidates)
        {
            string fullCandidate = Path.GetFullPath(path: candidate);

            // Skip the current file and anything already built or in-flight (the latter guards
            // against re-entrancy when siblings reference each other).
            if (fullCandidate.Equals(value: filePath, comparisonType: StringComparison.OrdinalIgnoreCase)
                || _compiledUnits.ContainsKey(key: fullCandidate)
                || _processingFiles.Contains(item: fullCandidate))
            {
                continue;
            }

            // File-granularity conditional compilation: skip a `.rf` sibling whose leading
            // `#@target(...)` directive doesn't match the build target (RazorForge-only).
            if (!Compiler.Targeting.TargetGate.ShouldCompile(filePath: fullCandidate))
            {
                continue;
            }

            // Read just the `module` declaration (cheap line scan, no full parse) so a broken or
            // unrelated sibling is neither fully parsed nor pulled in — only same-module files are
            // handed to ProcessFile, which is the one place that reports their parse/SA errors.
            if (ReadDeclaredModule(filePath: fullCandidate) != modulePath)
            {
                continue;
            }

            // No import edge: same-module siblings are peers, not dependencies.
            ProcessFile(filePath: fullCandidate,
                fromFile: null,
                importLocation: null,
                importPathString: null);
        }
    }

    /// <summary>
    /// Parses a single source file.
    /// </summary>
    private FileBuildUnit? ParseFile(string filePath)
    {
        try
        {
            string code = File.ReadAllText(path: filePath);
            bool isSuflae = filePath.EndsWith(value: ".sf",
                comparisonType: StringComparison.OrdinalIgnoreCase);

            // Validate language consistency
            if (isSuflae && _language == Language.RazorForge)
            {
                _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.LanguageMismatch,
                    Message: $"Cannot import Suflae file '{filePath}' from RazorForge project.",
                    Location: new SourceLocation(FileName: filePath,
                        Line: 1,
                        Column: 1,
                        Position: 0)));
                return null;
            }

            // A Suflae project MAY import RazorForge modules — SF's Core IS RF's Core, and a Suflae
            // program consuming a RazorForge library (e.g. an engine module) is the whole point of the
            // realm split. The `.rf` file is parsed with the RazorForge grammar below, so its entities
            // stay RF-realm (bare, not auto-roamed) — an SF↔RF boundary reference, same as `RF::`.
            // (Importing a non-stdlib `.rf` from SF was blocked; lifted to wire the SF→RF-module handoff.)
            bool isStdlibFile = Path.GetFullPath(path: filePath).StartsWith(
                value: Path.GetFullPath(path: _stdlibRoot), comparisonType: StringComparison.OrdinalIgnoreCase);

            // Tokenize
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;
            var tokenizer = new Tokenizer.Tokenizer(source: code, fileName: filePath, language: language);
            List<Token> tokens = tokenizer.Tokenize();

            // Parse
            var parser = new Parser.Parser(tokens: tokens, language: language, fileName: filePath);
            Program ast = parser.Parse();
            List<BuildWarning> warnings = parser.GetWarnings();

            // Extract module and imports (deriving + inserting a synthetic module header when absent).
            string modulePath = ExtractModuleAndImports(ast: ast, filePath: filePath,
                imports: out List<ImportDeclaration> imports);

            // Suflae prelude: inject the always-available SF modules (no explicit `import` needed).
            if (isSuflae && !isStdlibFile)
            {
                InjectSuflaePrelude(ast: ast, filePath: filePath, imports: imports);
            }

            return new FileBuildUnit(FilePath: filePath,
                Module: modulePath,
                Ast: ast,
                Imports: imports,
                ParseWarnings: warnings);
        }
        catch (GrammarException ex)
        {
            // Preserve the REAL error position (not 1:1) so the caret renderer points at the
            // offending token, and embed only the grammar code + raw text — the SemanticError
            // envelope supplies its own `error[…]: file:line:col:` prefix.
            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.ParseError,
                Message: $"[{ex.Code.ToCodeString(language: ex.Language)}] {ex.RawMessage}",
                Location: new SourceLocation(FileName: ex.FileName,
                    Line: ex.Line > 0 ? ex.Line : 1,
                    Column: ex.Column > 0 ? ex.Column : 1,
                    Position: 0)));
            return null;
        }
        catch (Exception ex)
        {
            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.CompilationError,
                Message: $"Error processing '{filePath}': {ex.Message}",
                Location: new SourceLocation(FileName: filePath,
                    Line: 1,
                    Column: 1,
                    Position: 0)));
            return null;
        }
    }

    /// <summary>
    /// Extracts the declared module path and import declarations from a parsed AST. A file with no
    /// <c>module</c> header gets one DERIVED from its path relative to the project root (the config.toml
    /// directory): each path segment is PascalCased (spaces removed), '.'/'..' segments dropped, the
    /// extension stripped, joined with '/'. E.g. <c>../SomeFolder/SomeMoreFolder/file a.rf</c> ->
    /// <c>SomeFolder/SomeMoreFolder/FileA</c>. A synthetic ModuleDeclaration is inserted at the top of
    /// the AST so every downstream reader (type/routine registration, protocol conformance) sees the
    /// same module uniformly.
    /// </summary>
    private string ExtractModuleAndImports(Program ast, string filePath,
        out List<ImportDeclaration> imports)
    {
        string? modulePath = null;
        imports = new List<ImportDeclaration>();

        foreach (ISyntaxTreeNode decl in ast.Declarations)
        {
            if (decl is ModuleDeclaration ns)
            {
                modulePath = ns.Path;
            }
            else if (decl is ImportDeclaration import)
            {
                imports.Add(item: import);
            }
        }

        if (modulePath == null)
        {
            modulePath = DeriveModuleFromPath(filePath: filePath);
            ast.Declarations.Insert(index: 0, item: new ModuleDeclaration(
                Path: modulePath,
                Location: new SourceLocation(FileName: filePath, Line: 0, Column: 0, Position: 0)));
        }

        return modulePath;
    }

    /// <summary>
    /// Injects the Suflae prelude: modules an SF USER file gets for free (no explicit <c>import</c>).
    /// These are injected into BOTH the extracted imports (so their files load) and the AST right after
    /// the module declaration (so SA adds them to _importedModules AND the top-of-file import order
    /// holds), each only if not already present. Stdlib `.rf` files are excluded — they're RF source.
    /// Members:
    ///   - `Numerics` — SF's unsuffixed integer literals default to `Integer` (RF defaults to S64),
    ///     and Integer/Real/Complex live in `Numerics` (NOT Core), so a bare `6` fails to resolve
    ///     (RF-S002) without it. (Real/Complex riding along relaxes #1's "import-only" for now;
    ///     TODO: narrow to Integer.)
    ///   - `IO/Console`, `IO/File` — always-available I/O in SF, so `show(...)` / file access need
    ///     no ceremony import.
    /// (Historical: a `Suflae` overlay module was prelude-injected here so a bare `List` shadowed
    /// `Core.List` with a hand-written roam-boundary wrapper. Removed 2026-08-14 — the world-line
    /// model makes SF's bare `List` resolve to the REAL `Core.List` (full API), which an SF `entity`
    /// slot roams directly, so the wrapper is obsolete. See [[realm-scoped-core]] pivot.)
    /// </summary>
    private static void InjectSuflaePrelude(Program ast, string filePath,
        List<ImportDeclaration> imports)
    {
        // (module, specificSymbols|null). `Numerics` is brought in as the SPECIFIC symbol `Integer`
        // ONLY — Suflae's bare numeric vocabulary is Integer/Decimal (Decimal is in Core), so bare
        // `6` defaults to Integer and bare `Integer` resolves, WITHOUT opening the whole Numerics
        // module. The fixed-width / complex / quaternion zoo (and Real/Complex) stay behind an
        // explicit whole-module `import Numerics` — which the prelude skips injecting when present,
        // so a whole-module import is the distinguishable "unlock" signal for the number gate
        // (see TypeResolver.EnforceSuflaeNumberGate). I/O stays whole-module for `show(...)`.
        (string Module, string[]? Symbols)[] preludeModules =
            [("Numerics", ["Integer"]), ("IO/Console", null), ("IO/File", null)];
        int insertAt = 1; // Module declaration is guaranteed at index 0 by now.
        foreach ((string preludeModule, string[]? symbols) in preludeModules)
        {
            if (imports.Any(predicate: i => i.ModulePath == preludeModule)) continue;
            var preludeImport = new ImportDeclaration(ModulePath: preludeModule, Alias: null,
                SpecificImports: symbols?.ToList(),
                Location: new SourceLocation(FileName: filePath, Line: 1, Column: 1, Position: 0));
            imports.Add(item: preludeImport);
            ast.Declarations.Insert(index: insertAt++, item: preludeImport);
        }
    }

    /// <summary>
    /// Derives a module path for a file with no <c>module</c> header, from its location relative to
    /// the project root (the config.toml directory). Path segments are PascalCased (whitespace
    /// removed, each word's first letter capitalized), <c>.</c>/<c>..</c> segments are dropped, the
    /// file extension is stripped, and segments are joined with <c>/</c>. E.g.
    /// <c>../SomeFolder/SomeMoreFolder/file a.rf</c> -> <c>SomeFolder/SomeMoreFolder/FileA</c>.
    /// </summary>
    private string DeriveModuleFromPath(string filePath)
    {
        string rel = Path.GetRelativePath(relativeTo: _projectRoot, path: filePath);
        List<string> segments = rel
            .Split(separator: ['/', '\\'], options: StringSplitOptions.RemoveEmptyEntries)
            .Where(predicate: s => s != "." && s != "..")
            .ToList();

        if (segments.Count == 0)
        {
            return PascalCaseSegment(segment: Path.GetFileNameWithoutExtension(path: filePath));
        }

        // Strip the extension from the final segment (the file name).
        segments[^1] = Path.GetFileNameWithoutExtension(path: segments[^1]);
        return string.Join(separator: '/',
            values: segments.Select(selector: PascalCaseSegment));
    }

    /// <summary>
    /// PascalCases one path segment: splits on whitespace, capitalizes the first letter of each word
    /// (preserving the rest), and concatenates. <c>file a</c> -> <c>FileA</c>; an already-cased
    /// <c>SomeFolder</c> stays <c>SomeFolder</c>.
    /// </summary>
    private static string PascalCaseSegment(string segment)
    {
        string[] words = segment.Split(separator: (char[]?)null,
            options: StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return segment;
        }

        var sb = new System.Text.StringBuilder();
        foreach (string w in words)
        {
            sb.Append(value: char.ToUpperInvariant(c: w[0]));
            if (w.Length > 1)
            {
                sb.Append(value: w[1..]);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Validates a `/`-form import path against the actual module declared by the resolved file.
    /// `import Foo/Bar` requires the file to declare `module Foo/Bar`. If the file declares a
    /// different module (e.g. `module Foo` because `Bar` is a type in that module), emit an error —
    /// the caller should have used `import Foo.Bar` instead.
    /// Member-form imports (paths containing `.`) skip this check.
    /// </summary>
    private void ValidateSlashFormImport(string importPathString, string actualModule, SourceLocation importLocation)
    {
        // Member-form imports are not subject to module-path equality.
        if (importPathString.Contains(value: '.'))
        {
            return;
        }

        if (importPathString == actualModule)
        {
            return;
        }

        _errors.Add(item: new SemanticError(
            Code: SemanticDiagnosticCode.ModuleNotFound,
            Message: $"No module '{importPathString}'. The file resolved to module '{actualModule}'. " +
                     $"Use 'import {actualModule}.{importPathString[(importPathString.LastIndexOf(value: '/') + 1)..]}' if you meant to import a member.",
            Location: importLocation));
    }

    /// <summary>
    /// Gets the module path for a file.
    /// </summary>
    private string GetModuleForFile(string filePath)
    {
        filePath = Path.GetFullPath(path: filePath);

        if (_compiledUnits.TryGetValue(key: filePath, value: out FileBuildUnit? unit))
        {
            return unit.Module ?? Path.GetFileNameWithoutExtension(path: filePath);
        }

        return Path.GetFileNameWithoutExtension(path: filePath);
    }

    /// <summary>
    /// Gets the dependency graph for inspection.
    /// </summary>
    public ModuleDependencyGraph DependencyGraph => _dependencyGraph;

    /// <summary>
    /// Parses all stdlib files and registers them in the resolver index.
    /// This replaces the old text-scanning approach with correct AST-based extraction.
    /// </summary>
    private void PreRegisterStdlib()
    {
        RegisterStdlibDirectory(subdirectory: "RazorForge", extension: "*.rf");
        RegisterStdlibDirectory(subdirectory: "Suflae", extension: "*.sf");
    }

    private void RegisterStdlibDirectory(string subdirectory, string extension)
    {
        string dirPath = Path.Combine(path1: _stdlibRoot, path2: subdirectory);
        if (!Directory.Exists(path: dirPath))
        {
            return;
        }

        // Sort by ordinal path so registration order is identical on every OS. Directory.GetFiles
        // returns OS-dependent order (NTFS sorts, ext4/APFS do not); without this, stdlib routines
        // register in different orders per platform, making memberRoutine/overload resolution
        // order-dependent — the root of the Linux/macOS-only UnpackedFloat resolution failures.
        foreach (string filePath in Directory.GetFiles(path: dirPath,
                     searchPattern: extension,
                     searchOption: SearchOption.AllDirectories)
                 .OrderBy(keySelector: p => p, comparer: StringComparer.Ordinal))
        {
            // File-granularity conditional compilation: skip files gated out for this target.
            if (!Compiler.Targeting.TargetGate.ShouldCompile(filePath: filePath))
            {
                continue;
            }

            Program? ast = ParseAstOnly(filePath: filePath);
            if (ast is null)
            {
                continue;
            }

            string? moduleName = null;
            foreach (ISyntaxTreeNode node in ast.Declarations)
            {
                if (node is ModuleDeclaration md)
                {
                    moduleName = md.Path;
                    break;
                }
            }

            moduleName ??= DeriveModuleNameFromPath(filePath: filePath, languageSubdir: subdirectory);
            _resolver.RegisterFile(filePath: filePath, moduleName: moduleName, ast: ast);
        }
    }

    /// <summary>
    /// Parses all sources in external library dependency directories and registers their
    /// declared module names in the resolver index — mirroring the stdlib pre-scan.
    /// Library files without a <c>module</c> declaration are skipped (a dependency's
    /// public surface is its declared modules).
    /// </summary>
    private void PreRegisterLibraryRoots()
    {
        foreach (string libraryRoot in _libraryRoots)
        {
            if (!Directory.Exists(path: libraryRoot))
            {
                continue;
            }

            foreach (string pattern in (string[])["*.rf", "*.sf"])
            {
                foreach (string filePath in Directory.GetFiles(path: libraryRoot,
                             searchPattern: pattern,
                             searchOption: SearchOption.AllDirectories)
                         .OrderBy(keySelector: p => p, comparer: StringComparer.Ordinal))
                {
                    // File-granularity conditional compilation: skip files gated out for this target.
                    if (!Compiler.Targeting.TargetGate.ShouldCompile(filePath: filePath))
                    {
                        continue;
                    }

                    Program? ast = ParseAstOnly(filePath: filePath);
                    if (ast is null)
                    {
                        continue;
                    }

                    foreach (ISyntaxTreeNode node in ast.Declarations)
                    {
                        if (node is ModuleDeclaration md)
                        {
                            _resolver.RegisterFile(filePath: filePath, moduleName: md.Path,
                                ast: ast);
                            break;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Parses a file to an AST without language-consistency validation or error accumulation.
    /// Used only for index pre-registration; actual compilation errors surface via <see cref="ParseFile"/>.
    /// </summary>
    private static Program? ParseAstOnly(string filePath)
    {
        try
        {
            string code = File.ReadAllText(path: filePath);
            bool isSuflae = filePath.EndsWith(value: ".sf",
                comparisonType: StringComparison.OrdinalIgnoreCase);
            Language language = isSuflae ? Language.Suflae : Language.RazorForge;
            var tokenizer = new Tokenizer.Tokenizer(source: code, fileName: filePath, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            var parser = new Parser.Parser(tokens: tokens, language: language, fileName: filePath);
            return parser.Parse();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file's declared module name from its <c>module</c> line without a full parse.
    /// Used to test directory siblings cheaply before deciding to compile them, so an unrelated or
    /// intentionally-broken file in the same directory is never fully parsed just to be rejected.
    /// Mirrors the manifest's module-name extraction.
    /// </summary>
    private static string? ReadDeclaredModule(string filePath)
    {
        try
        {
            foreach (string line in File.ReadLines(path: filePath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith(value: "module ", comparisonType: StringComparison.Ordinal))
                {
                    continue;
                }

                string name = trimmed["module ".Length..].Trim();
                int commentIdx = name.IndexOf(value: '#');
                if (commentIdx >= 0)
                {
                    name = name[..commentIdx].Trim();
                }

                return name.Length == 0 ? null : name;
            }
        }
        catch (IOException)
        {
            // Unreadable file declares no usable module.
        }

        return null;
    }

    /// <summary>
    /// Derives a module name from a file path for stdlib files that lack an explicit module declaration.
    /// Uses the directory path relative to the language subdirectory, with '/' as hierarchy separator.
    /// Files directly in the language root use the filename as their module name.
    /// </summary>
    private string DeriveModuleNameFromPath(string filePath, string languageSubdir)
    {
        try
        {
            string languagePath = Path.GetFullPath(path: Path.Combine(path1: _stdlibRoot,
                path2: languageSubdir));
            string fileDir = Path.GetFullPath(path: Path.GetDirectoryName(path: filePath) ?? "");

            if (!fileDir.StartsWith(value: languagePath,
                    comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetFileNameWithoutExtension(path: filePath);
            }

            string relativeDir = fileDir[languagePath.Length..]
               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return string.IsNullOrEmpty(value: relativeDir)
                ? Path.GetFileNameWithoutExtension(path: filePath)
                : relativeDir.Replace(oldChar: Path.DirectorySeparatorChar, newChar: '/')
                             .Replace(oldChar: Path.AltDirectorySeparatorChar, newChar: '/');
        }
        catch
        {
            return Path.GetFileNameWithoutExtension(path: filePath);
        }
    }
}
