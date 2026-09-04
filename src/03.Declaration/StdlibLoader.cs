using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Compiler.Tokenizer;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace Compiler.Declaration;

/// <summary>
/// Loads the standard library based on module declarations.
/// Files declaring "module Core" are loaded eagerly (auto-imported).
/// Other modules are loaded on-demand when imported.
/// Supports both RazorForge (.rf) and Suflae (.sf) stdlib files.
/// </summary>
public sealed partial class StdlibLoader
{
    /// <summary>
    /// The stdlib scan roots — each a (directory, glob) pair scanned in order. RazorForge is always
    /// present (the RF-realm Core, and — for a Suflae compile — the bridged delegation backend reached via
    /// <c>RF::</c>). A Suflae compile ALSO scans <c>Standard/Suflae/*.sf</c> (the SF-realm Core surface).
    /// Realm is stamped per file from its extension at registration (see StdlibLoader.Registration).
    /// </summary>
    private readonly List<(string Dir, string Glob)> _scanRoots;

    /// <summary>The language being built.</summary>
    private readonly Language _language;

    /// <summary>Parsed Core module programs with their file paths and module.</summary>
    private readonly List<(Program Program, string FilePath, string Module)> _corePrograms = [];

    /// <summary>Cache of parsed non-Core programs by module.</summary>
    private readonly Dictionary<string, List<(Program Program, string FilePath, string Module)>>
        _modulePrograms = new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Set of already scanned directories to avoid re-scanning.</summary>
    private bool _stdlibScanned;

    /// <summary>True when this loader belongs to a WARM-restore registry whose <c>module Core</c> programs
    /// were already parsed, analyzed and lowered at snapshot capture (served from the registry's restored
    /// set). A warm compile still needs the fresh loader to <see cref="ScanStdlibFiles"/> so on-demand
    /// non-Core imports (e.g. <c>Collections.Deque</c>) resolve from <see cref="_modulePrograms"/> — but that
    /// scan also re-parses every Core file into <see cref="_corePrograms"/> as UNANALYZED/UNLOWERED ASTs.
    /// When this flag is set, <see cref="AllLoadedPrograms"/> excludes <see cref="_corePrograms"/> so those
    /// stale Core copies never re-enter <c>FreshlyLoadedStdlibPrograms</c> and get re-lowered (which throws
    /// on any never-analyzed node, e.g. a D128 ternary — the warm-restore crash this guards).</summary>
    public bool CoreResident { get; set; }

    /// <summary>Tracks modules that have been loaded on-demand.</summary>
    private readonly HashSet<string> _loadedModules =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the parsed Core module programs.</summary>
    public List<(Program Program, string FilePath, string Module)> ParsedPrograms =>
        _corePrograms;

    /// <summary>
    /// Scans the stdlib once and returns every NON-Core module name found (e.g. "Collections.Deque",
    /// "IO.Console"). Used by the daemon snapshot capture to import — and therefore pre-load into the
    /// resident snapshot — the entire stdlib, so warm compiles short-circuit on-demand imports instead of
    /// re-parsing the whole stdlib per run to resolve one import.
    /// </summary>
    public IReadOnlyList<string> ScanModuleNames()
    {
        ScanStdlibFiles();
        return _modulePrograms.Keys.ToList();
    }

    /// <summary>Gets all parsed programs (core + loaded modules) for codegen.</summary>
    public List<(Program Program, string FilePath, string Module)> AllLoadedPrograms
    {
        get
        {
            // Warm restore: Core is already lowered in the registry's restored set — exclude the fresh
            // (unanalyzed) Core re-parse so it is not reported as freshly-loaded and re-lowered.
            var all = CoreResident
                ? new List<(Program, string, string)>()
                : new List<(Program, string, string)>(collection: _corePrograms);
            foreach (string mod in _loadedModules)
            {
                if (_modulePrograms.TryGetValue(key: mod,
                        value: out
                        List<(Program Program, string FilePath, string Module)>? programs))
                {
                    all.AddRange(collection: programs);
                }
            }

            return all;
        }
    }

    /// <summary>
    /// Creates a new stdlib loader for a specific language.
    /// </summary>
    /// <param name="stdlibRoot">Path to the stdlib root directory (containing razorforge/ and suflae/ subdirectories).</param>
    /// <param name="language">The language being built.</param>
    public StdlibLoader(string stdlibRoot, Language language)
    {
        _language = language;
        // RazorForge is always scanned: it is the RF-realm Core, and — under a Suflae compile — the bridged
        // delegation backend an SF wrapper reaches via `RF::Core.X`. A Suflae compile ALSO scans
        // Standard/Suflae/*.sf, the SF-realm Core surface (types there declare `module Core` too; they are
        // stamped Realm="SF" at registration, so they key distinctly from the RF-realm `Core.*`). Each file's
        // realm is derived from its extension (`.sf`→SF, `.rf`→RF) — see StdlibLoader.Registration.RealmOf.
        _scanRoots =
        [
            (Path.Combine(path1: stdlibRoot, path2: "RazorForge"), "*.rf")
        ];
        if (language == Language.Suflae)
        {
            _scanRoots.Add(item: (Path.Combine(path1: stdlibRoot, path2: "Suflae"), "*.sf"));
        }
    }

    /// <summary>
    /// Loads the Core module types into the type registry.
    /// Scans all stdlib files and loads those declaring "module Core".
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    public void LoadCoreModule(TypeRegistry registry)
    {
        // Scan all stdlib files and categorize by module
        ScanStdlibFiles();

        // Suflae wrapper stdlib: append transparent inner-forwarders to each SF `entity X { inner: RF::Y }`
        // so the wrapper presents Y's COMPLETE surface. Must run before registration so the synthesized
        // forwarders flow through the ordinary register/analyze/monomorph/codegen path as authored source.
        SynthesizeSuflaeForwarders();

        // Three-pass registration ensures protocols exist before types reference them in 'obeys' clauses.
        // Pass 1a: Register all protocol type shells first (names + generic params, no memberRoutines yet)
        RegisterCoreProtocolShells(registry: registry);

        // Pass 1a.1: Fill in protocol memberRoutine signatures (all protocols are now registered for cross-refs)
        FillCoreProtocolMemberRoutines(registry: registry);

        // Pass 1a.2: Resolve parent protocol hierarchies (now that all protocols are registered)
        foreach ((Program program, string _, string _) in _corePrograms)
        {
            ResolveProtocolParents(registry: registry, program: program);
        }

        // Pass 1b: Register all type shells (record, entity, choice, variant)
        foreach ((Program program, string filePath, string ns) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            RegisterProgramTypes(registry: registry, program: program, moduleName: ns);
        }

        // Pass 1b.1: Load modules imported by Core files so their types are available
        // for member variable resolution (e.g., Set imports Collections.SortedSet).
        LoadCoreImportedModules(registry: registry);

        // Pass 1c: Re-resolve member variables now that all types are registered.
        // The initial registration may have empty member lists due to forward references
        // (e.g., Bytes needs List which needs U64, but files are processed alphabetically).
        // Each deferred pass RE-STAMPS `_registeringRealm` per program — the shell-registration loop left it
        // at the last program's realm, which mis-scopes an RF program's deferred lookups to a coexisting SF
        // wrapper's shell (see the LoadModule siblings + the BitList not-iterable bug).
        foreach ((Program program, string filePath, string _) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveProgramMemberVariables(registry: registry, program: program);
        }

        // Pass 1d: Re-resolve protocol conformances now that all types are registered.
        // Protocol arguments may reference types not yet registered during Pass 1b
        // (e.g., EnumerateIterator[T] obeys Iterable[Tuple[S64, T]] needs S64).
        foreach ((Program program, string filePath, string _) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveProgramProtocolConformances(registry: registry, program: program);
        }

        // Pass 1e: Re-resolve protocol memberRoutine return types that failed in pass 1a.1 due to
        // forward references (e.g., Crashable.crash_message() -> Text where Text was not yet
        // registered when protocols were first processed in pass 1a.1).
        foreach ((Program program, string filePath, string _) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveProtocolMemberRoutineReturnTypes(registry: registry, program: program);
            ResolveAssociatedTypeBindings(registry: registry, program: program);
        }

        // Pass 2: Register all routines (now all types are available for return type resolution)
        foreach ((Program program, string filePath, string ns) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            RegisterProgramRoutines(registry: registry, program: program, moduleName: ns);
        }

        // Pass 2.1: Refresh any routine signatures that were still partially unresolved during
        // initial registration and later collapsed to None via semantic finalization.
        foreach ((Program program, string filePath, string ns) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveRoutineSignatures(registry: registry, program: program, moduleName: ns);
        }

        // Pass 3: Register all presets (module-level constants accessible across files)
        foreach ((Program program, string filePath, string ns) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            RegisterProgramPresets(registry: registry, program: program, moduleName: ns);
        }

        // Clear the thread-static realm so it never leaks into a later (on-demand) load pass on this thread.
        _registeringRealm = null;
    }

    /// <summary>
    /// Pass 1a: registers every Core program's protocol type shells (names + generic params, no
    /// memberRoutines yet), realm-stamped per program.
    /// </summary>
    private void RegisterCoreProtocolShells(TypeRegistry registry)
    {
        foreach ((Program program, string filePath, string ns) in _corePrograms)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    RegisterProtocolTypeShell(registry: registry,
                        protocol: protocol,
                        moduleName: ns);
                }
            }
        }
    }

    /// <summary>
    /// Pass 1a.1: fills in protocol memberRoutine signatures across every Core program (all protocols
    /// are now registered for cross-refs).
    /// </summary>
    private void FillCoreProtocolMemberRoutines(TypeRegistry registry)
    {
        foreach ((Program program, string _, string _) in _corePrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    FillProtocolMemberRoutines(registry: registry, protocol: protocol);
                }
            }
        }
    }

    /// <summary>
    /// Pass 1b.1: loads the top-level modules imported by Core files so their types are available for
    /// member variable resolution (e.g., Set imports Collections.SortedSet). Core and already-loaded
    /// modules are excluded.
    /// </summary>
    private void LoadCoreImportedModules(TypeRegistry registry)
    {
        var importedModules = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach ((Program program, string _, string _) in _corePrograms)
        {
            foreach (ISyntaxTreeNode decl in program.Declarations)
            {
                if (decl is ImportDeclaration import)
                {
                    // Extract top-level module name (e.g., "Collections" from "Collections.SortedSet")
                    string moduleName = import.ModulePath.Replace(oldChar: '/', newChar: '.');
                    int dotIndex = moduleName.IndexOf(value: '.');
                    if (dotIndex > 0)
                    {
                        moduleName = moduleName[..dotIndex];
                    }

                    if (!moduleName.Equals(value: "Core",
                            comparisonType: StringComparison.OrdinalIgnoreCase) &&
                        !_loadedModules.Contains(item: moduleName))
                    {
                        importedModules.Add(item: moduleName);
                    }
                }
            }
        }

        foreach (string mod in importedModules)
        {
            LoadModule(registry: registry, moduleName: mod);
        }
    }

    /// <summary>
    /// Scans all stdlib files recursively and categorizes them by module.
    /// Files with "module Core" go to _corePrograms.
    /// Other modules are cached in _modulePrograms for on-demand loading.
    /// </summary>
    private void ScanStdlibFiles()
    {
        if (_stdlibScanned)
        {
            return;
        }

        _stdlibScanned = true;

        // Scan every root (RazorForge always; Suflae too under an SF compile). Recursively find files
        // matching each root's glob. Sort by ordinal path so the scan/registration order is identical on
        // every OS (Directory.GetFiles order is OS-dependent) — otherwise memberRoutine resolution becomes
        // order-dependent across platforms. RazorForge sorts before Suflae so the RF-realm Core registers
        // first (the SF-realm Core keys distinctly by realm, so order does not cause collision either way).
        IEnumerable<string> allFiles = _scanRoots
            .Where(predicate: r => Directory.Exists(path: r.Dir))
            .SelectMany(selector: r => Directory.GetFiles(path: r.Dir,
                searchPattern: r.Glob,
                searchOption: SearchOption.AllDirectories))
            .OrderBy(keySelector: p => p, comparer: StringComparer.Ordinal);

        foreach (string filePath in allFiles)
        {
            // File-granularity conditional compilation applies to the stdlib too: a platform-specific
            // stdlib file (e.g. the LP64/LLP64 C-type width files) carries a `#@target(...)` directive
            // and only the matching one is loaded.
            if (!Compiler.Targeting.TargetGate.ShouldCompile(filePath: filePath))
            {
                continue;
            }

            try
            {
                string code = File.ReadAllText(path: filePath);
                // Parse stdlib files by their own extension, not the user language: the stdlib is
                // RazorForge source (`.rf`, uses `danger`/`extern`), so a Suflae compile must still
                // parse it with the RazorForge grammar (else SF-G112 on the `danger` bang).
                Program ast = ParseFileByExtension(code: code, filePath: filePath);

                // Find module declaration, or derive from directory
                string? fileModule = GetDeclaredModule(program: ast);
                fileModule ??= DeriveModuleFromPath(filePath: filePath);

                // Categorize by module
                if (fileModule.Equals(value: "Core",
                        comparisonType: StringComparison.OrdinalIgnoreCase))
                {
                    _corePrograms.Add(item: (ast, filePath, fileModule));
                }
                else
                {
                    // Cache for on-demand loading
                    if (!_modulePrograms.TryGetValue(key: fileModule,
                            value: out
                            List<(Program Program, string FilePath, string Module)>? programs))
                    {
                        programs = [];
                        _modulePrograms[key: fileModule] = programs;
                    }

                    programs.Add(item: (ast, filePath, fileModule));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    value: $"Warning: Failed to parse stdlib file {filePath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Parses a file using the appropriate tokenizer/parser for the current language.
    /// Used for scanning stdlib files where extension matches language.
    /// </summary>
    /// <param name="code">The source code to parse.</param>
    /// <param name="filePath">The file path for error reporting.</param>
    /// <returns>The parsed program AST.</returns>
    private Program ParseFile(string code, string filePath)
    {
        var tokenizer = new Tokenizer.Tokenizer(source: code, fileName: filePath, language: _language);
        List<Token> tokens = tokenizer.Tokenize();
        var parser = new Parser.Parser(tokens: tokens, language: _language, fileName: filePath);
        return parser.Parse();
    }

    /// <summary>
    /// Parses a file using the tokenizer/parser determined by file extension.
    /// Used for cross-language imports where a Suflae file imports a RazorForge module.
    /// </summary>
    /// <param name="code">The source code to parse.</param>
    /// <param name="filePath">The file path (extension determines parser choice).</param>
    /// <returns>The parsed program AST.</returns>
    private static Program ParseFileByExtension(string code, string filePath)
    {
        bool isSuflaeFile = filePath.EndsWith(value: ".sf",
            comparisonType: StringComparison.OrdinalIgnoreCase);
        Language language = isSuflaeFile
            ? Language.Suflae
            : Language.RazorForge;
        var tokenizer = new Tokenizer.Tokenizer(source: code, fileName: filePath, language: language);
        List<Token> tokens = tokenizer.Tokenize();
        var parser = new Parser.Parser(tokens: tokens, language: language, fileName: filePath);
        return parser.Parse();
    }

    /// <summary>
    /// Gets the declared module from a program AST.
    /// </summary>
    private static string? GetDeclaredModule(Program program)
    {
        return (program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault())?.Path;
    }

    /// <summary>
    /// Derives a module from the file path relative to the standard library root.
    /// Example: standard/razorforge/Collections/List.rf -> Collections
    /// Example: standard/razorforge/Text/Encoding/UTF8.rf -> Text.Encoding
    /// Files directly in the language root default to Core.
    /// </summary>
    private string DeriveModuleFromPath(string filePath)
    {
        try
        {
            string? fileDir = Path.GetDirectoryName(path: filePath);
            if (fileDir == null)
            {
                return "Core";
            }

            string normalizedFileDir = Path.GetFullPath(path: fileDir);
            // The file may live under any scan root (RazorForge or, in an SF compile, Suflae). Find the
            // root that contains it and derive the module path relative to THAT root.
            string? normalizedStdlibPath = _scanRoots
                .Select(selector: r => Path.GetFullPath(path: r.Dir))
                .FirstOrDefault(predicate: root => normalizedFileDir.StartsWith(value: root,
                    comparisonType: StringComparison.OrdinalIgnoreCase));

            if (normalizedStdlibPath == null)
            {
                return "Core";
            }

            string relativePath = normalizedFileDir[normalizedStdlibPath.Length..]
               .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (string.IsNullOrEmpty(value: relativePath))
            {
                return "Core";
            }

            // Convert directory separators to module path separators
            return relativePath.Replace(oldChar: Path.DirectorySeparatorChar, newChar: '/')
                               .Replace(oldChar: Path.AltDirectorySeparatorChar, newChar: '/');
        }
        catch
        {
            return "Core";
        }
    }

    /// <summary>
    /// Loads a specific module on-demand.
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    /// <param name="moduleName">The module to load (e.g., "Collections").</param>
    /// <returns>True if the module was loaded successfully, false if not found.</returns>
    public bool LoadModule(TypeRegistry registry, string moduleName)
    {
        // Ensure stdlib is scanned
        ScanStdlibFiles();

        // Core is already loaded
        if (moduleName.Equals(value: "Core", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check if we have files for this module
        if (!_modulePrograms.TryGetValue(key: moduleName,
                value: out List<(Program Program, string FilePath, string Module)>? programs) ||
            programs.Count == 0)
        {
            return false;
        }

        _loadedModules.Add(item: moduleName);

        // Three-pass registration: protocols first, then other types, then routines
        // Register protocol shells across all files first, then fill in memberRoutines
        foreach ((Program program, string filePath, string ns) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    RegisterProtocolTypeShell(registry: registry,
                        protocol: protocol,
                        moduleName: ns);
                }
            }
        }

        foreach ((Program program, string _, string _) in programs)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ProtocolDeclaration protocol)
                {
                    FillProtocolMemberRoutines(registry: registry, protocol: protocol);
                }
            }
        }

        foreach ((Program program, string _, string _) in programs)
        {
            ResolveProtocolParents(registry: registry, program: program);
        }

        foreach ((Program program, string filePath, string ns) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            RegisterProgramTypes(registry: registry, program: program, moduleName: ns);
        }

        // Re-resolve member variables now that all type shells in this module are registered.
        // Initial registration may have empty member lists due to forward references
        // (e.g., Set needs SortedSet which may not be registered yet during alphabetical processing).
        // Each deferred pass must RE-STAMP `_registeringRealm` per program (the registration loop above
        // left it at the LAST program's realm — and with an RF `.rf` + SF `.sf` wrapper for the same type
        // both loaded, that trailing realm is SF, so an RF program's deferred lookups would hit the SF
        // shell and mis-apply its protocols/members to the wrong realm — the `BitList` not-iterable bug).
        foreach ((Program program, string filePath, string _) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveProgramMemberVariables(registry: registry, program: program);
        }

        foreach ((Program program, string filePath, string _) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveProgramProtocolConformances(registry: registry, program: program);
        }

        // Re-resolve protocol memberRoutine return types that failed due to forward references
        foreach ((Program program, string filePath, string _) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveProtocolMemberRoutineReturnTypes(registry: registry, program: program);
            ResolveAssociatedTypeBindings(registry: registry, program: program);
        }

        foreach ((Program program, string filePath, string ns) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            RegisterProgramRoutines(registry: registry, program: program, moduleName: ns);
        }

        foreach ((Program program, string filePath, string ns) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            ResolveRoutineSignatures(registry: registry, program: program, moduleName: ns);
        }

        // Register presets for the module
        foreach ((Program program, string filePath, string ns) in programs)
        {
            _registeringRealm = RealmOf(filePath: filePath);
            RegisterProgramPresets(registry: registry, program: program, moduleName: ns);
        }

        // Clear the thread-static realm so it never leaks into a later load pass on this thread.
        _registeringRealm = null;
        return true;
    }

    /// <summary>
    /// Loads a specific module on-demand.
    /// Parses the module file and registers its types and routines.
    /// Uses module-based imports: the import path determines the module.
    /// </summary>
    /// <param name="registry">The type registry to populate.</param>
    /// <param name="filePath">The resolved file path of the module.</param>
    /// <param name="moduleId">The module identifier (e.g., "Collections.List").</param>
    /// <returns>The effective module of the loaded module, or null on failure.</returns>
    public string? LoadModule(TypeRegistry registry, string filePath, string moduleId)
    {
        try
        {
            string code = File.ReadAllText(path: filePath);
            // Detect file type from extension and use appropriate parser
            Program ast = ParseFileByExtension(code: code, filePath: filePath);

            // Get module from file declaration, or derive from directory structure
            string? fileModule = GetDeclaredModule(program: ast);
            string effectiveModule = fileModule ?? DeriveModuleFromPath(filePath: filePath);

            // Track loaded program for codegen
            _loadedModules.Add(item: moduleId);
            if (!_modulePrograms.TryGetValue(key: moduleId,
                    value: out List<(Program Program, string FilePath, string Module)>? progs))
            {
                progs = [];
                _modulePrograms[key: moduleId] = progs;
            }

            progs.Add(item: (ast, filePath, effectiveModule));

            // Two-pass registration for single module
            RegisterProgramTypes(registry: registry, program: ast, moduleName: effectiveModule);
            RegisterProgramRoutines(registry: registry, program: ast, moduleName: effectiveModule);

            // Handle any imports within this module (recursive loading)
            foreach (ISyntaxTreeNode node in ast.Declarations)
            {
                if (node is ImportDeclaration import)
                {
                    // Recursively load imported modules
                    registry.LoadModule(importPath: import.ModulePath,
                        currentFile: filePath,
                        location: import.Location,
                        effectiveModule: out _);
                }
            }

            return effectiveModule;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to load stdlib module '{moduleId}' from '{filePath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Resolves parent protocol relationships for all protocols in a program.
    /// Must run after all protocols are registered (pass 1a) so parent lookups succeed.
    /// </summary>
    private static TypeInfo? ResolveSimpleType(TypeRegistry registry, TypeExpression? typeExpr,
        List<string>? genericParams = null, string? moduleName = null)
    {
        if (typeExpr == null)
        {
            return null;
        }

        // Comptime type-position splice `${m.type}` in a stdlib decl-position expand column template
        // (e.g. `Hijacked[${m.type}]` in `SplitList[T]`). Resolve to the synthetic per-field placeholder,
        // mirroring TypeResolver.ResolveTypeCore; the registry substitutes each concrete field's type at
        // instantiation (ExpandSoAColumns). Without this the stdlib registration path — which does NOT go
        // through TypeBodyResolver.ResolveExpandTemplates — never sees the splice, so a stdlib SoA type
        // (SplitList) gets no columns.
        if (typeExpr.SpliceHandle != null)
        {
            return new GenericParameterTypeInfo(
                name: TypeModel.Symbols.MemberExpandTemplateInfo.ColumnPlaceholderName);
        }

        // Comptime VALUE-position splice used as a const-generic argument, e.g. the carrier payload
        // size `Array[U8, ${max(T.data_size().byte_size(), 8)}]`. Resolve to a symbolic
        // ComptimeConstGenericTypeInfo; RoutineInfo/RecordTypeInfo.SubstituteType fold it at
        // monomorphization. Without this the stdlib registration path returns null and the whole field
        // (Result/Lookup `payload`) is silently dropped from the record's member list.
        if (typeExpr.ComptimeValue != null)
        {
            return new ComptimeConstGenericTypeInfo(comptimeExpr: typeExpr.ComptimeValue);
        }

        string typeName = typeExpr.Name;

        // `Me` (protocol-self / owner placeholder) used as a type or type argument — e.g. in
        // `Iterable[T].enumerate() -> ?EnumerateIterator[T, Me]`. Resolved to ProtocolSelf here;
        // re-homing / call-site substitution binds it to the concrete implementer. Without this,
        // `Me` falls through to the type lookup, returns null, and nulls the whole signature type.
        if (typeName == "Me")
        {
            return ProtocolSelfTypeInfo.Instance;
        }

        // Associated-type projection `Base/Slot` (e.g. `S/Iter`): the base is an in-scope generic
        // parameter or `Me`. Produce a deferred AssociatedProjectionTypeInfo that monomorphization
        // resolves through the base's binding once the base is concrete.
        if (typeName.Contains(value: '/'))
        {
            string[] segments = typeName.Split(separator: '/');
            TypeInfo? projBase = segments[0] == "Me"
                ? ProtocolSelfTypeInfo.Instance
                : genericParams != null && genericParams.Contains(value: segments[0])
                    ? new GenericParameterTypeInfo(name: segments[0])
                    : null;
            if (projBase != null)
            {
                TypeInfo current = projBase;
                for (int i = 1; i < segments.Length; i++)
                {
                    current = new AssociatedProjectionTypeInfo(baseType: current,
                        slotName: segments[i]);
                }
                return current;
            }
        }

        // Generic parameter name (T, K, V) -> placeholder for substitution
        if (genericParams != null && genericParams.Contains(value: typeName))
        {
            return new GenericParameterTypeInfo(name: typeName);
        }

        // Const generic literal (e.g., 16, 8u64) used as a type argument (e.g., Array[T, 16])
        TypeInfo? constGeneric = ResolveConstGenericLiteral(typeName: typeName);
        if (constGeneric != null)
        {
            return constGeneric;
        }

        // Routine type: Routine[(T, T), Bool] -> RoutineTypeInfo
        if (typeName == "Routine" && typeExpr.GenericArguments?.Count == 2)
        {
            return ResolveRoutineType(registry: registry, typeExpr: typeExpr,
                genericParams: genericParams, moduleName: moduleName);
        }

        // Parameterized type like List[Character], Dict[Text, S32]
        if (typeExpr.GenericArguments is { Count: > 0 })
        {
            TypeInfo? parameterized = ResolveParameterizedType(registry: registry, typeExpr: typeExpr,
                typeName: typeName, genericParams: genericParams, moduleName: moduleName,
                resolved: out bool handled);
            if (handled)
            {
                return parameterized;
            }
        }

        // Own-module FIRST, then the bare (auto-import/Core-prefix) lookup — see the generic-def branch
        // above for why the overlay's same-named types must not collapse to the RazorForge realm.
        return (moduleName != null
            ? registry.LookupType(name: $"{moduleName}.{typeName}")
            : null) ?? registry.LookupType(name: typeName);
    }

    /// <summary>
    /// Resolves a const-generic literal type argument: a bare integer (16), or a typed suffix
    /// ("16u64", "8s32", …). Returns null when <paramref name="typeName"/> is not a numeric literal.
    /// </summary>
    private static TypeInfo? ResolveConstGenericLiteral(string typeName)
    {
        if (long.TryParse(s: typeName, result: out long constValue))
        {
            return new ConstGenericValueTypeInfo(
                literalText: typeName, value: constValue, explicitTypeName: null);
        }

        // Check typed suffixes: "16u64", "8s32", etc.
        (string suffix, string suffixType)[] suffixes =
            [("u64", "U64"), ("s64", "S64"), ("u32", "U32"), ("s32", "S32"),
             ("u16", "U16"), ("s16", "S16"), ("u8", "U8"), ("s8", "S8")];
        foreach ((string suffix, string suffixType) in suffixes)
        {
            if (typeName.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s: typeName[..^suffix.Length], result: out long suffixVal))
            {
                return new ConstGenericValueTypeInfo(
                    literalText: typeName, value: suffixVal, explicitTypeName: suffixType);
            }
        }

        return null;
    }

    /// <summary>
    /// Resolves a <c>Routine[(T, T), Bool]</c> type expression into a RoutineTypeInfo. Parameter types
    /// live in the first arg's GenericArguments (parsed as Tuple). Returns null when a parameter type
    /// fails to resolve.
    /// </summary>
    private static TypeInfo? ResolveRoutineType(TypeRegistry registry, TypeExpression typeExpr,
        List<string>? genericParams, string? moduleName)
    {
        TypeExpression paramTupleExpr = typeExpr.GenericArguments![index: 0];
        TypeExpression returnTypeExpr = typeExpr.GenericArguments[index: 1];

        // Parameter types live in the first arg's GenericArguments (parsed as Tuple)
        var paramTypes = new List<TypeInfo>();
        if (paramTupleExpr is { Name: "Tuple", GenericArguments: not null })
        {
            foreach (TypeExpression paramTypeExpr in paramTupleExpr.GenericArguments)
            {
                TypeInfo? pt = ResolveSimpleType(registry: registry,
                    typeExpr: paramTypeExpr,
                    genericParams: genericParams,
                    moduleName: moduleName);
                if (pt == null)
                {
                    return null;
                }

                paramTypes.Add(item: pt);
            }
        }
        else
        {
            TypeInfo? pt = ResolveSimpleType(registry: registry,
                typeExpr: paramTupleExpr,
                genericParams: genericParams,
                moduleName: moduleName);
            if (pt == null)
            {
                return null;
            }

            paramTypes.Add(item: pt);
        }

        TypeInfo? returnType = ResolveSimpleType(registry: registry,
            typeExpr: returnTypeExpr,
            genericParams: genericParams,
            moduleName: moduleName);
        return registry.GetOrCreateRoutineType(parameterTypes: paramTypes,
            returnType: returnType,
            isFailable: false);
    }

    /// <summary>
    /// Resolves a parameterized type expression (<c>List[Character]</c>, <c>Dict[Text, S32]</c>,
    /// a wrapper token like <c>Hijacked[T]</c>, or a <c>Tuple[..]</c>). Sets <paramref name="resolved"/>
    /// to true and returns the resolved type (possibly null, e.g. when an argument fails to resolve)
    /// when this method took responsibility for the expression; sets it to false to signal the caller
    /// should fall through to the bare/own-module lookup.
    /// </summary>
    private static TypeInfo? ResolveParameterizedType(TypeRegistry registry, TypeExpression typeExpr,
        string typeName, List<string>? genericParams, string? moduleName, out bool resolved)
    {
        // Wrapper types (Hijacked, Viewing, Modifying, etc.) are not in _types — create directly
        if (typeExpr.GenericArguments!.Count == 1 &&
            typeName is RuntimeContract.Hijacked or RuntimeContract.Viewing or RuntimeContract.Modifying
                or RuntimeContract.Retained or RuntimeContract.Tracked or RuntimeContract.Guarded or RuntimeContract.Witnessed)
        {
            TypeInfo? wrapperInner = ResolveSimpleType(registry: registry,
                typeExpr: typeExpr.GenericArguments[index: 0],
                genericParams: genericParams,
                moduleName: moduleName);
            if (wrapperInner != null)
            {
                bool isReadOnly = typeName is RuntimeContract.Viewing;
                resolved = true;
                return registry.GetOrCreateWrapperType(wrapperName: typeName,
                    innerType: wrapperInner,
                    isReadOnly: isReadOnly);
            }
        }

        // Tuple types are not registered as generic definitions — handle specially
        if (typeName is "Tuple")
        {
            var elemTypes = new List<TypeInfo>();
            foreach (TypeExpression argExpr in typeExpr.GenericArguments)
            {
                TypeInfo? argType = ResolveSimpleType(registry: registry,
                    typeExpr: argExpr,
                    genericParams: genericParams,
                    moduleName: moduleName);
                if (argType == null)
                {
                    resolved = true;
                    return null;
                }

                elemTypes.Add(item: argType);
            }

            resolved = true;
            return new TupleTypeInfo(elementTypes: elemTypes);
        }

        // Own-module FIRST: a bare `List` in `module Suflae` (e.g. the overlay constructor's
        // `-> List[T]` return) must resolve to `Suflae.List`, not the auto-imported `Core.List`
        // (LookupType's Core-prefix fast path). A dotted/RF::-qualified `Core.List` misses the
        // `Suflae.Core.List` probe and correctly falls back to the RazorForge `Core.List`.
        TypeInfo? genericDef = (moduleName != null
            ? registry.LookupType(name: $"{moduleName}.{typeName}")
            : null) ?? registry.LookupType(name: typeName);
        if (genericDef is { IsGenericDefinition: true } &&
            genericDef.GenericParameters!.Count == typeExpr.GenericArguments.Count)
        {
            var typeArgs = new List<TypeInfo>();
            foreach (TypeExpression argExpr in typeExpr.GenericArguments)
            {
                TypeInfo? argType = ResolveSimpleType(registry: registry,
                    typeExpr: argExpr,
                    genericParams: genericParams,
                    moduleName: moduleName);
                if (argType == null)
                {
                    resolved = true;
                    return null;
                }

                typeArgs.Add(item: argType);
            }

            resolved = true;
            return registry.GetOrCreateResolution(genericDef: genericDef,
                typeArguments: typeArgs);
        }

        resolved = false;
        return null;
    }

    /// <summary>
    /// Gets the default stdlib path relative to the application.
    /// </summary>
    public static string GetDefaultStdlibPath()
    {
        // Allow override via environment variable
        string? envOverride = Environment.GetEnvironmentVariable("FORGE_STDLIB");
        if (!string.IsNullOrWhiteSpace(value: envOverride) && Directory.Exists(path: envOverride))
            return envOverride;

        // Try to find standard library relative to the executable
        string? exeDir = Path.GetDirectoryName(path: typeof(StdlibLoader).Assembly.Location);
        if (exeDir != null)
        {
            string stdlibPath = Path.Combine(path1: exeDir, path2: "Standard");
            if (Directory.Exists(path: stdlibPath))
            {
                return stdlibPath;
            }

            // Try parent directories (for development)
            string? current = exeDir;
            for (int i = 0; i < 5; i++)
            {
                current = Path.GetDirectoryName(path: current);
                if (current == null)
                {
                    break;
                }

                stdlibPath = Path.Combine(path1: current, path2: "Standard");
                if (Directory.Exists(path: stdlibPath))
                {
                    return stdlibPath;
                }
            }
        }

        // Fallback to current directory
        return Path.Combine(path1: Directory.GetCurrentDirectory(), path2: "Standard");
    }

    /// <summary>
    /// Extracts the LLVM type from an @llvm("type") annotation.
    /// Returns null if no @llvm annotation is present.
    /// </summary>
    private static string? ExtractLlvmAnnotation(List<string>? annotations)
    {
        if (annotations == null)
        {
            return null;
        }

        foreach (string ann in annotations)
        {
            if (ann.StartsWith(value: "llvm(") && ann.EndsWith(')'))
            {
                return ann[5..^1]
                   .Trim(trimChar: '"');
            }
        }

        return null;
    }
}
