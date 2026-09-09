using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;
using Compiler.Verification.Scopes;

namespace Compiler.Declaration;

using TypeInfo = TypeInfo;

/// <summary>
/// Central registry for all type information in a RazorForge/Suflae program.
/// Provides unified lookup for types, routines, member variables, and scopes.
/// </summary>
public sealed partial class TypeRegistry
{
    /// <summary>
    /// Ambient registry reference for static helpers that need to route generic resolutions
    /// through <see cref="GetOrCreateResolution"/> (to pick up entity specializations) but
    /// have no direct registry access — notably the static Substitute* memberRoutines on
    /// <see cref="RoutineInfo"/> and <see cref="RecordTypeInfo"/>. Set by the constructor.
    /// [ThreadStatic] so parallel test runs each get their own Ambient without cross-contamination.
    /// </summary>
    [ThreadStatic]
    private static TypeRegistry? _ambient;
    /// <summary>Thread-local ambient registry; set by the constructor so test isolation works without injection.</summary>
    public static TypeRegistry? Ambient => _ambient;

    /// <summary>Sets the thread-local ambient registry to <paramref name="registry"/>. Called from the
    /// constructor so each new <see cref="TypeRegistry"/> registers itself as the current thread's ambient,
    /// enabling static helpers (e.g. <see cref="RoutineInfo"/> Substitute methods) to resolve generics
    /// without requiring a registry parameter at every call site.</summary>
    private static void RegisterAsAmbient(TypeRegistry registry) => _ambient = registry;

    /// <summary>The language being built. Settable so the stdlib (always RazorForge source) can be
    /// analyzed in RazorForge mode during a Suflae compile — see SemanticVerifier.AnalyzeStdlibBodies.</summary>
    public Language Language { get; set; }

    /// <summary>
    /// The AMBIENT realm of the compilation — the world-line a bare (unqualified) type name resolves to:
    /// <c>"RF"</c> for a RazorForge compile, <c>"SF"</c> for a Suflae compile. A compilation-global
    /// constant (like <see cref="Language"/>): types of this realm key BARE (realm-free
    /// <see cref="TypeInfo.FullName"/>) so the whole name-resolution hot path stays realm-blind; only the
    /// NON-ambient (bridged via <c>RF::</c>/<c>SF::</c>) realm gets a realm-marked
    /// <see cref="TypeInfo.RealmQualifiedName"/> registry key. Settable so stdlib bodies of each realm are
    /// analyzed under their own realm — see SemanticVerifier.AnalyzeStdlibBodies. Defaults to <c>"RF"</c>.
    /// </summary>
    public string AmbientRealm { get; set; } = "RF";

    /// <summary>
    /// The user's TARGET compile language — unlike <see cref="Language"/>, this is NOT toggled to
    /// RazorForge while stdlib bodies are analyzed (see SemanticVerifier.AnalyzeStdlibBodies), so it
    /// reliably answers "is this ultimately a Suflae build?" even mid-stdlib-analysis. Used to gate
    /// RazorForge-ONLY surface diagnostics (@readonly / @reshaping enforcement) OFF for a Suflae build:
    /// Suflae hides those concepts, and the borrowed RF stdlib's RF-internal checks are RF's own concern.
    /// </summary>
    public Language CompilationLanguage { get; set; } = Language.RazorForge;

    /// <summary>
    /// The realm whose types a bare (unqualified) name should PREFER during the CURRENT file's analysis —
    /// set per-file (a <c>.sf</c> file ⇒ <c>"SF"</c>, a <c>.rf</c> file ⇒ <c>"RF"</c>). When it differs
    /// from <see cref="AmbientRealm"/> (i.e. analyzing an SF file while RF stays the ambient/bare realm),
    /// <see cref="LookupType(string)"/> first tries the <c>{ResolutionRealm}::</c>-keyed type before the
    /// bare ambient one, so a bare <c>List</c> in a Suflae file resolves to the SF-realm <c>Core.List</c>
    /// (bridged, keyed <c>SF::Core.List</c>) yet still falls back to the RF-realm type for anything the SF
    /// surface has not (re)declared. Defaults to <c>"RF"</c> ⇒ equal to the ambient ⇒ zero effect on RF.
    /// </summary>
    public string ResolutionRealm { get; set; } = "RF";

    #region Type Storage

    /// <summary>All registered types by their full name.</summary>
    private readonly Dictionary<string, TypeInfo> _types = new();

    /// <summary>Generic type resolutions cache.</summary>
    private readonly Dictionary<string, TypeInfo> _resolutions = new();

    /// <summary>
    /// Queue of EntityTypeInfo/RecordTypeInfo instances registered after GMP tracking began.
    /// Populated by <see cref="GetOrCreateResolution"/> only while tracking is active.
    /// Drained by GMP's fixed-point loop to process types discovered during body rewriting.
    /// </summary>
    private Queue<TypeInfo>? _gmpDiscoveryQueue;

    /// <summary>
    /// Set to true while <c>AnalyzeStdlibBodies</c> runs.
    /// New resolutions created in this window are marked <see cref="TypeInfo.IsStdlibLazy"/>
    /// and excluded from GMP until user code references them.
    /// </summary>
    private bool _stdlibAnalysisActive;

    /// <summary>
    /// Set of type FullNames determined to be live by <c>TypeLivenessPass</c>.
    /// Null until the pass runs, in which case all types are treated as live (pass-through).
    /// </summary>
    private HashSet<string>? _liveConcreteTypes;

    /// <summary>
    /// Stores the live-type set computed by TypeLivenessPass.  Called once, before Phase 6 synthesis.
    /// </summary>
    public void SetLiveConcreteTypes(HashSet<string> liveTypes) => _liveConcreteTypes = liveTypes;

    /// <summary>Enables GMP discovery tracking. After this call, newly created EntityTypeInfo/
    /// RecordTypeInfo instances are pushed to the discovery queue.</summary>
    public void StartGmpDiscoveryTracking() => _gmpDiscoveryQueue = new Queue<TypeInfo>();

    /// <summary>Drains and returns all types discovered since the last drain (or since tracking
    /// started). Returns empty if tracking is not active.</summary>
    public List<TypeInfo> DrainGmpDiscoveryQueue()
    {
        if (_gmpDiscoveryQueue == null || _gmpDiscoveryQueue.Count == 0)
            return [];
        var result = _gmpDiscoveryQueue.ToList();
        _gmpDiscoveryQueue.Clear();
        return result;
    }

    /// <summary>Called at the start of <c>AnalyzeStdlibBodies</c>. New resolutions created
    /// inside this window are marked <see cref="TypeInfo.IsStdlibLazy"/> and excluded from
    /// GMP iteration until user code references them.</summary>
    public void BeginStdlibAnalysis() => _stdlibAnalysisActive = true;

    /// <summary>Called at the end of <c>AnalyzeStdlibBodies</c>. Resumes normal eager resolution.</summary>
    public void EndStdlibAnalysis() => _stdlibAnalysisActive = false;

    /// <summary>
    /// Clears <see cref="TypeInfo.IsStdlibLazy"/> on <paramref name="type"/> and enqueues it
    /// to the GMP discovery queue if applicable. No-op if already materialized.
    /// </summary>
    private void MaterializeIfLazy(TypeInfo type)
    {
        if (!type.IsStdlibLazy) return;
        type.IsStdlibLazy = false;
        if (_gmpDiscoveryQueue == null || type is not (EntityTypeInfo or RecordTypeInfo) || !IsFullyConcrete(type))
            return;
        string bareBaseName = type.BareName;
        bool isSelfNesting = type.TypeArguments != null &&
                             type.TypeArguments.Any(arg => arg.FullName.Contains(bareBaseName));
        if (!isSelfNesting) _gmpDiscoveryQueue.Enqueue(type);
    }

    /// <summary>
    /// Returns true if the type is considered live (i.e., reachable from user-program roots).
    /// Always true for non-generic types and generic definitions; for concrete generic instances,
    /// requires the type to have been included in the live set by TypeLivenessPass.
    /// </summary>
    private bool IsConcreteTypeLive(TypeInfo t) =>
        _liveConcreteTypes == null ||
        t.IsGenericDefinition ||
        t.TypeArguments == null ||
        t.TypeArguments.Count == 0 ||
        _liveConcreteTypes.Contains(t.FullName);

    /// <summary>
    /// Wrapper type resolutions cache for synthesized scoped/RC wrappers
    /// (Viewing, Modifying, Consulting, Amending, Hijacked, Retained, Guarded, Tracked, Witnessed).
    /// Kept separate from <see cref="_resolutions"/> to prevent key collisions when both
    /// <see cref="GetOrCreateWrapperType"/> and <see cref="GetOrCreateResolution"/> produce
    /// the same FullName-based key (e.g., "Hijacked[Core.Byte]").
    /// </summary>
    private readonly Dictionary<string, WrapperTypeInfo> _wrapperResolutions = new();

    /// <summary>
    /// Entity-type specializations of constrained generics, keyed by bare type name.
    /// When a generic has two layout variants — one for record types and one for entity types
    /// (e.g. <c>Maybe[T] needs T is EntityType</c>) — the entity layout is stored here.
    /// <see cref="GetOrCreateResolution"/> consults this table when a type argument is an
    /// <see cref="EntityTypeInfo"/> so it can pick the correct struct layout
    /// (e.g. <c>{ Hijacked[T] }</c> for <c>Maybe[Text]</c> instead of <c>{ Bool, T }</c>).
    /// </summary>
    private readonly Dictionary<string, TypeInfo> _entitySpecializations = new();

    /// <summary>Whether Core module has been loaded from stdlib.</summary>
    private bool _coreModuleLoaded;

    /// <summary>The stdlib loader instance.</summary>
    private StdlibLoader? _stdlibLoader;

    /// <summary>Path to the stdlib directory.</summary>
    private readonly string? _stdlibPath;

    /// <summary>Gets the stdlib directory path.</summary>
    public string? StdlibPath => _stdlibPath;

    /// <summary>Set of loaded module paths (e.g., "Collections.List", "ErrorHandling.Maybe").</summary>
    private readonly HashSet<string> _loadedModules =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Maps module IDs to their effective module names (for import tracking).</summary>
    private readonly Dictionary<string, string> _moduleNames =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Maps already-registered source files (by normalized full path) to their declared
    /// module name. Used to dedup module loading by RESOLVED FILE PATH rather than by
    /// import-path alias: a bare <c>module Fun2</c> shared across files means
    /// <c>import Fun2.A</c>, <c>import Fun2/A</c>, and <c>import Fun2</c> can all resolve to
    /// the same file, and each must be served from the existing registration instead of
    /// re-parsing/re-registering it (which produces duplicate-definition errors).
    /// </summary>
    private readonly Dictionary<string, string> _loadedFilePaths =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>The module resolver for finding module files.</summary>
    private ModuleResolver? _moduleResolver;

    /// <summary>
    /// Injects a pre-built module resolver so SA-phase import loading shares the
    /// <see cref="Compiler.Declaration.BuildDriver"/> index (stdlib pre-scan, project files,
    /// and manifest <c>[target] library</c> roots). Without this, <see cref="LoadModule"/>
    /// lazily builds a blind resolver that can only probe the filesystem by path-name
    /// convention — which misses modules whose file name differs from their declared name
    /// and knows nothing about external library directories.
    /// </summary>
    public void UseModuleResolver(ModuleResolver resolver) => _moduleResolver = resolver;

    /// <summary>
    /// Marks a module as already provided by the current compilation, so an
    /// <c>import</c> of it resolves without re-loading its file through
    /// <see cref="StdlibLoader"/>. Multi-file analysis calls this for every file in the
    /// build graph before Phase 1: those files' declarations are registered directly by
    /// the per-file declaration pass, and loading them again would produce
    /// duplicate-definition errors.
    /// </summary>
    /// <param name="modulePath">The declared module path, verbatim (e.g. "MathUtils", "Geo/Shapes").</param>
    /// <param name="filePath">
    /// The source file that declared the module, if known. Recorded so a later import that
    /// resolves to this same file (by any import-path alias) is served from the existing
    /// registration instead of re-loading the file. See <see cref="_loadedFilePaths"/>.
    /// </param>
    public void MarkModuleProvided(string modulePath, string? filePath = null)
    {
        string moduleId = modulePath.Replace(oldChar: '/', newChar: '.')
                                    .Replace(oldChar: '\\', newChar: '.');
        _loadedModules.Add(item: moduleId);
        _moduleNames.TryAdd(key: moduleId, value: modulePath);

        if (filePath != null)
        {
            _loadedFilePaths.TryAdd(key: Path.GetFullPath(path: filePath), value: modulePath);
        }
    }

    #endregion

    #region Routine Storage

    /// <summary>All registered routines by their full name.</summary>
    private readonly Dictionary<string, RoutineInfo> _routines = new();

    /// <summary>Routines indexed by module-qualified name for unambiguous lookup.</summary>
    private readonly Dictionary<string, RoutineInfo> _routinesByQualifiedName = new();

    /// <summary>Routines indexed by owner type FullName, then by memberRoutine name → overload list. The nested
    /// by-name level makes `owner.MemberRoutine` lookup O(1) (no list scan), and — because a bare generic-param
    /// owner (`routine T.m()`) is stored under the canonical key <see cref="GenericOwnerKey"/> — lets a
    /// receiver-agnostic default-impl member routine resolve by name off the SAME store, no separate index.</summary>
    private readonly Dictionary<string, Dictionary<string, List<RoutineInfo>>> _routinesByOwner = new();

    /// <summary>Canonical owner key for a bare generic-parameter owner (`routine T.m()` / `E.m()` / …): the
    /// param name is arbitrary for a default-impl member routine, so they all share one key so `x.m()` on a
    /// concrete receiver resolves them by name via <see cref="_routinesByOwner"/>.</summary>
    internal const string GenericOwnerKey = "$T";

    /// <summary>Canonical owner key for OWNER-LESS (free / independent) routines — folded into
    /// <see cref="_routinesByOwner"/> under this key (by base name → overloads) so the old separate
    /// `_routineOverloads` index is no longer needed. Excluded from member-routine enumeration.</summary>
    internal const string FreeOwnerKey = "$free";

    /// <summary>The flattened memberRoutine list for an owner (all overloads across all memberRoutine names), or empty.</summary>
    private static IEnumerable<RoutineInfo> OwnerMemberRoutines(Dictionary<string, List<RoutineInfo>>? byName)
        => byName?.Values.SelectMany(selector: l => l) ?? Enumerable.Empty<RoutineInfo>();

    /// <summary>Derive memberRoutine NAMES that are OPT-IN (capability-conferred): any `@overridable/@override
    /// T.m()` whose gate is a `needs P everywhere` / `needs T obeys P` capability constraint. Once a memberRoutine
    /// is opt-in, ALL its derive templates — including KIND-gated overrides (`needs T is VariantType`) —
    /// stay opt-in (must NOT become live universals). Keyed by name so a kind-override registered AFTER its
    /// capability-gated base is classified consistently. Populated during stdlib load.</summary>
    private readonly HashSet<string> _optInDeriveMemberRoutines = new(comparer: StringComparer.Ordinal);

    /// <summary>Marks a derive memberRoutine name opt-in (capability-conferred) — see <see cref="_optInDeriveMemberRoutines"/>.</summary>
    public void MarkOptInDeriveMemberRoutine(string memberRoutine) => _optInDeriveMemberRoutines.Add(item: memberRoutine);

    /// <summary>True if a derive memberRoutine name was marked opt-in via a capability gate.</summary>
    public bool IsOptInDeriveMemberRoutine(string memberRoutine) => _optInDeriveMemberRoutines.Contains(item: memberRoutine);

    /// <summary>
    /// Auto-derive templates: universal <c>@overridable routine T.MemberRoutine()</c> bodies, plus their
    /// kind-specialized <c>@override … needs T is VariantType/ChoiceType/FlagsType/…</c> variants. Keyed by
    /// memberRoutine name → all candidate (routine + body) pairs. The wired-routine synthesizer picks the
    /// most-specific kind-matching template per concrete type at SYNTHESIS time (one body per type,
    /// so several same-signature templates coexist here without any registry/call-resolution clash).
    /// </summary>
    private readonly Dictionary<string,
        List<(string OwnerParam, int Arity, List<GenericConstraintDeclaration> Gates,
            Statement Body)>> _deriveTemplates = new();

    /// <summary>Generic routine resolutions cache.</summary>
    private readonly Dictionary<string, RoutineInfo> _routineResolutions = new();

    /// <summary>
    /// Base names of generic-definition routines that have no concrete instantiations.
    /// Populated by <see cref="PruneUnusedGenericRoutines"/> and used to filter
    /// <see cref="GetAllRoutines"/> so that dead generic stubs never reach codegen or the AST printer.
    /// </summary>
    private readonly HashSet<string> _prunedGenericBases = [];

    /// <summary>
    /// Lazy cache for bare-name type lookups (e.g., "List" -> Collections.List).
    /// Populated on first miss in <see cref="LookupType(string)"/> to amortize the O(N) scan.
    /// </summary>
    private readonly Dictionary<string, TypeInfo> _typesByShortName = new();

    #endregion

    #region Preset Storage

    /// <summary>Module-level preset constants registered by StdlibLoader (accessible across files).</summary>
    private readonly Dictionary<string, VariableInfo> _presets = new();

    /// <summary>Presets indexed by module-qualified name for unambiguous lookup.</summary>
    private readonly Dictionary<string, VariableInfo> _presetsByQualifiedName = new();

    /// <summary>Suflae module-level <c>global</c> variables (mutable, session-lifetime), accessible
    /// across files within their module. Separate from presets so they are NOT inlined.</summary>
    private readonly Dictionary<string, VariableInfo> _globals = new();

    /// <summary>Globals indexed by module-qualified name for unambiguous lookup.</summary>
    private readonly Dictionary<string, VariableInfo> _globalsByQualifiedName = new();

    #endregion

    #region Scope Management

    /// <summary>The global scope.</summary>
    public Scope GlobalScope { get; }

    /// <summary>The current scope during analysis.</summary>
    private Scope _currentScope;

    #endregion

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeRegistry"/> class.
    /// </summary>
    /// <param name="language">The language being built.</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    public TypeRegistry(Language language, string? stdlibPath = null)
    {
        Language = language;
        RegisterAsAmbient(this);
        GlobalScope = new Scope(kind: ScopeKind.Global);
        _currentScope = GlobalScope;
        _stdlibPath = stdlibPath ?? StdlibLoader.GetDefaultStdlibPath();

        // Register well-known error handling types BEFORE loading the Core module.
        // This ensures that when Core stdlib routines (e.g. Maybe[T].unwrap) are registered
        // during LoadCoreModule, LookupType("Maybe") returns the initial Maybe definition
        // (FullName="Maybe", no module prefix), so memberRoutines are keyed under "Maybe" in
        // _routinesByOwner and are reachable via LookupMemberRoutine on Maybe[T] resolutions.
        // If LoadCoreModule ran first, it would register Core.Maybe (FullName="Core.Maybe")
        // before the initial Maybe, causing memberRoutine registration under "Core.Maybe" — a key
        // that LookupMemberRoutine never checks when resolving memberRoutines on Maybe[S64].
        RegisterErrorHandlingTypes();

        // Load Core module eagerly - Core types are fundamental to every program.
        // NOTE: user-code bare names still resolve to the RF-realm Core (world-line) — a `.sf` file's bare
        // `List` is the RF `Core.List` roamed, which WORKS end-to-end. Flipping the user resolution realm to
        // SF (so bare `List` → the SF wrapper) is DEFERRED: it triggers a pipeline-wide realm cascade
        // (construction create-binding, reachability create-seeding, codegen emission) not yet complete —
        // see [[sf-real-stdlib-direction]]. The SF wrapper is reachable explicitly via `SF::List`.
        LoadCoreModule();
    }

    #region Initialization

    /// <summary>
    /// Loads the Core module from stdlib files.
    /// Called on-demand when Core types are first used or when import Core is encountered.
    /// </summary>
    public void LoadCoreModule()
    {
        if (_coreModuleLoaded)
        {
            return;
        }

        _coreModuleLoaded = true;

        if (_stdlibPath != null && Directory.Exists(path: _stdlibPath))
        {
            _stdlibLoader ??= new StdlibLoader(stdlibRoot: _stdlibPath, language: Language);
            _stdlibLoader.LoadCoreModule(registry: this);
        }
        else
        {
            string searchPath = _stdlibPath ?? "not specified";
            throw new InvalidOperationException(
                message: $"Standard library not found at '{searchPath}'. " +
                         "Ensure standard/ directory exists and contains the Core module.");
        }
    }

    /// <summary>
    /// Checks if the Core module has been loaded.
    /// </summary>
    public bool IsCoreModuleLoaded => _coreModuleLoaded;

    /// <summary>
    /// Gets the parsed stdlib programs (for code generation).
    /// Returns the programs parsed by the stdlib loader, including routine bodies.
    /// </summary>
    public List<(Program Program, string FilePath, string Module)> StdlibPrograms =>
        _restoredStdlibPrograms == null
            ? _stdlibLoader?.AllLoadedPrograms ?? []
            // Warm-restore: the restored set is the modules loaded at CAPTURE time; a warm compile may
            // `import` further modules (e.g. IO/Console) that the fresh loader parses on-demand — those
            // must join the stdlib program list or their routine bodies never reach codegen.
            : [.. _restoredStdlibPrograms, .. _stdlibLoader?.AllLoadedPrograms ?? []];

    /// <summary>Lowered stdlib program ASTs restored from a warm-compile snapshot. When set, these
    /// (already fully desugared/lowered) programs are served as <see cref="StdlibPrograms"/> instead of
    /// re-parsing/re-lowering them — the fast-restore path for the compile daemon / warm compiles.</summary>
    private List<(Program Program, string FilePath, string Module)>? _restoredStdlibPrograms;

    /// <summary>Installs pre-lowered stdlib programs from a warm-compile snapshot.</summary>
    public void RestoreStdlibPrograms(List<(Program Program, string FilePath, string Module)> programs) =>
        _restoredStdlibPrograms = programs;

    /// <summary>Stdlib programs that still need Phase 6/7/8 lowering: the loader's freshly-parsed
    /// programs. In a cold compile that is EVERY stdlib program; in a warm compile the restored programs
    /// are already lowered (excluded here), so this is just the modules the warm compile imported
    /// on-demand (e.g. IO/Console). The desugaring/monomorph/lowering passes iterate THIS, not the full
    /// <see cref="StdlibPrograms"/> — re-lowering the restored (already-lowered) programs would diverge.</summary>
    public List<(Program Program, string FilePath, string Module)> FreshlyLoadedStdlibPrograms =>
        _stdlibLoader?.AllLoadedPrograms ?? [];

    /// <summary>True when the stdlib was restored from a warm-compile snapshot already fully
    /// desugared/lowered/synthesized. The global desugaring + postprocessing passes then SKIP their
    /// stdlib-program loops (re-lowering already-lowered ASTs would be wasted work / double-apply).
    /// User programs and synthesized/monomorphized user bodies are still processed normally.</summary>
    public bool SkipStdlibReprocessing { get; set; }

    private readonly List<(Program Program, string FilePath, string Module)> _userPrograms = [];

    /// <summary>
    /// Gets user (non-stdlib) programs that have been analyzed.
    /// Used by synthesis passes to search for generic routine bodies in user code.
    /// </summary>
    public List<(Program Program, string FilePath, string Module)> UserPrograms =>
        _userPrograms;

    /// <summary>Registers a user program so synthesis passes can search it for routine bodies.</summary>
    public void RegisterUserProgram(Program program, string filePath, string module) =>
        _userPrograms.Add(item: (program, filePath, module));

    /// <summary>
    /// Per-module import list (module path → the module paths it imports), populated once after the
    /// multi-file Phase-3 declaration sweep. Backs the BuilderQuery <c>T.dependencies()</c> reflection
    /// routine, which reports the modules a type's declaring module depends on.
    /// </summary>
    private readonly Dictionary<string, List<string>> _moduleDependencies =
        new(comparer: StringComparer.Ordinal);

    /// <summary>Returns the sorted module import list for <paramref name="module"/>, or an empty list.</summary>
    public IReadOnlyList<string> GetModuleDependencies(string? module) =>
        module != null && _moduleDependencies.TryGetValue(key: module, value: out List<string>? deps)
            ? deps
            : [];

    /// <summary>Records (accumulating + de-duplicating) the modules <paramref name="module"/> imports.</summary>
    public void AddModuleDependencies(string module, IEnumerable<string> imports)
    {
        if (!_moduleDependencies.TryGetValue(key: module, value: out List<string>? deps))
        {
            deps = [];
            _moduleDependencies[key: module] = deps;
        }

        foreach (string imp in imports)
        {
            if (imp.Length > 0 && imp != module && !deps.Contains(item: imp))
            {
                deps.Add(item: imp);
            }
        }

        deps.Sort(comparer: StringComparer.Ordinal);
    }

    /// <summary>
    /// Every MODULE registered under the namespace <paramref name="prefix"/> (strict descendants:
    /// `prefix/Sub`, `prefix/Sub/Deep`, …), for the prefix/package import `import A/B`. Empty when the
    /// resolver isn't injected or the prefix is a leaf module with no submodules.
    /// </summary>
    public IReadOnlyList<string> EnumerateSubmodules(string prefix)
        => _moduleResolver?.EnumerateSubmodulePaths(prefix: prefix) ?? [];

    /// <summary>
    /// Loads a module on-demand by its import path.
    /// Handles both stdlib modules and project modules.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections/List", "ErrorHandling/Maybe").</param>
    /// <param name="currentFile">The file containing the import statement (for relative import resolution).</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <param name="effectiveModule">The effective module name of the loaded module, or null on failure.</param>
    /// <returns>True if the module was loaded successfully or was already loaded, false on error.</returns>
    public bool LoadModule(string importPath, string currentFile, SourceLocation location,
        out string? effectiveModule)
    {
        // Normalize the import path to a module identifier (e.g., "Collections/List" -> "Collections.List")
        string moduleId = importPath.Replace(oldChar: '/', newChar: '.')
                                    .Replace(oldChar: '\\', newChar: '.');

        // Check if already loaded
        if (_loadedModules.Contains(item: moduleId))
        {
            _moduleNames.TryGetValue(key: moduleId, value: out effectiveModule);
            return true;
        }

        // Core module is special - always loaded at startup
        if (moduleId.Equals(value: "Core", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            moduleId.StartsWith(value: "Core.",
                comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            LoadCoreModule();
            _loadedModules.Add(item: moduleId);
            effectiveModule = "Core";
            _moduleNames[key: moduleId] = "Core";
            return true;
        }

        // Ensure stdlib path is available
        if (_stdlibPath == null || !Directory.Exists(path: _stdlibPath))
        {
            effectiveModule = null;
            return false;
        }

        _stdlibLoader ??= new StdlibLoader(stdlibRoot: _stdlibPath, language: Language);

        // Bare module name (e.g. `import Collections`) — load every file declaring `module <Name>`
        // via the multi-file overload. Filesystem fallback can't handle directory-as-module unless
        // the directory contains a same-named anchor file (e.g. BuilderQuery/BuilderQuery.rf).
        bool isBareModuleName = !importPath.Contains(value: '/') && !importPath.Contains(value: '.');
        if (isBareModuleName && _stdlibLoader.LoadModule(registry: this, moduleName: importPath))
        {
            _loadedModules.Add(item: moduleId);
            effectiveModule = importPath;
            _moduleNames[key: moduleId] = effectiveModule;
            return true;
        }

        // Initialize the module resolver if needed
        _moduleResolver ??= new ModuleResolver(
            projectRoot: Path.GetDirectoryName(path: currentFile) ??
                         Directory.GetCurrentDirectory(),
            stdlibRoot: _stdlibPath);

        // Resolve the import path to a file
        string? resolvedPath = _moduleResolver.ResolveImport(importPath: importPath,
            currentFile: currentFile,
            location: location);
        if (resolvedPath == null)
        {
            effectiveModule = null;
            return false;
        }

        return LoadResolvedModuleFile(resolvedPath: resolvedPath, moduleId: moduleId,
            effectiveModule: out effectiveModule);
    }

    /// <summary>
    /// Loads a module from an already-resolved file path: dedups by RESOLVED FILE PATH (serving an
    /// existing registration when the file was already loaded under a different import-path alias, to
    /// avoid duplicate-definition errors), otherwise marks it loaded (before parsing, to break import
    /// recursion) and loads it via <see cref="StdlibLoader"/>.
    /// </summary>
    private bool LoadResolvedModuleFile(string resolvedPath, string moduleId, out string? effectiveModule)
    {
        // Dedup by RESOLVED FILE PATH: the file may already be registered under a different
        // import-path alias (e.g. the build pipeline pre-marked `module Fun2` from Fun2/A.rf,
        // and we're now serving `import Fun2.A` which resolves back to that same file). Serve
        // it from the existing registration instead of re-parsing/re-registering it, which
        // would raise duplicate-definition errors.
        string fullResolvedPath = Path.GetFullPath(path: resolvedPath);
        if (_loadedFilePaths.TryGetValue(key: fullResolvedPath, value: out string? alreadyLoadedModule))
        {
            _loadedModules.Add(item: moduleId);
            effectiveModule = alreadyLoadedModule;
            _moduleNames[key: moduleId] = alreadyLoadedModule;
            return true;
        }

        // Mark as loaded before parsing to prevent infinite recursion
        _loadedModules.Add(item: moduleId);

        // Load the module using StdlibLoader
        effectiveModule =
            _stdlibLoader!.LoadModule(registry: this, filePath: resolvedPath, moduleId: moduleId);

        if (effectiveModule != null)
        {
            _moduleNames[key: moduleId] = effectiveModule;
            _loadedFilePaths.TryAdd(key: fullResolvedPath, value: effectiveModule);
        }

        return effectiveModule != null;
    }

    /// <summary>
    /// Checks if a module has been loaded.
    /// </summary>
    /// <param name="moduleId">The module identifier (e.g., "Collections.List").</param>
    /// <returns>True if the module is loaded, false otherwise.</returns>
    public bool IsModuleLoaded(string moduleId)
    {
        return _loadedModules.Contains(item: moduleId);
    }

    /// <summary>
    /// Gets all loaded module identifiers.
    /// </summary>
    /// <returns>An enumerable of all loaded module identifiers.</returns>
    public IEnumerable<string> GetLoadedModules()
    {
        return _loadedModules;
    }

    /// <summary>
    /// Registers all well-known error handling types (Maybe, Result, Lookup) as ordinary generic records.
    /// </summary>
    private void RegisterErrorHandlingTypes()
    {
        // Register Maybe, Result, Lookup as type shells (name + GenericParameters only).
        // MemberVariables reference Core types (Bool, U64, Address) that aren't available yet
        // because LoadCoreModule hasn't run.  ResolveProgramMemberVariables (pass 1c inside
        // LoadCoreModule) fills in the members from the stdlib source once all Core types exist.
        // We must register the shells HERE (before LoadCoreModule) so that when
        // LoadCoreModule's RegisterProgramRoutines processes Maybe[T].unwrap etc., it calls
        // LookupType("Maybe") and gets this shell (FullName="Maybe"), causing those memberRoutines to
        // be keyed under "Maybe" in _routinesByOwner rather than "Core.Maybe".
        RegisterType(
            type: new RecordTypeInfo(name: "Maybe")
            {
                GenericParameters = ["T"], Module = "Core", CarrierKind = CarrierKind.Maybe
            });
        RegisterType(
            type: new RecordTypeInfo(name: "Result")
            {
                GenericParameters = ["T"], Module = "Core", CarrierKind = CarrierKind.Result
            });
        RegisterType(
            type: new RecordTypeInfo(name: "Lookup")
            {
                GenericParameters = ["T"], Module = "Core", CarrierKind = CarrierKind.Lookup
            });
    }

    #endregion

    #region Type Registration and Lookup

    /// <summary>
    /// Registers a constrained generic specialization (e.g. <c>Maybe[T] needs T is EntityType</c>).
    /// Unlike <see cref="RegisterType"/>, multiple specializations with the same bare name are allowed.
    /// Registers the entity-type specialization of a constrained generic
    /// (e.g. <c>record Maybe[T] needs T is EntityType</c>).
    /// When <see cref="GetOrCreateResolution"/> is asked to resolve this generic with an
    /// <see cref="EntityTypeInfo"/> argument it will use this specialization instead of the
    /// primary (record-type) definition, ensuring the correct struct layout.
    /// </summary>
    /// <param name="type">The entity-specialization type definition.</param>
    public void RegisterEntitySpecialization(TypeInfo type)
    {
        _entitySpecializations[key: type.Name] = type;
    }

    /// <summary>
    /// The registry key for a type: the realm-free <see cref="TypeInfo.FullName"/> when the type belongs to
    /// the ambient realm (the common case — keeps the name-resolution hot path realm-blind), or the
    /// realm-marked <see cref="TypeInfo.RealmQualifiedName"/> when it is a BRIDGED (non-ambient) realm type,
    /// so a bridged <c>RF::Core.List</c> never collides with the ambient <c>Core.List</c> when both
    /// world-lines coexist in one compilation. When <see cref="AmbientRealm"/> is the default <c>"RF"</c> and
    /// every type is <c>Realm="RF"</c>, this is exactly <see cref="TypeInfo.FullName"/> (no behavior change).
    /// </summary>
    internal string RealmRegistryKey(TypeInfo type) =>
        type.Realm == AmbientRealm ? type.FullName : type.RealmQualifiedName;

    /// <summary>
    /// The routine-index key for <paramref name="routine"/>: its realm-free
    /// <see cref="RoutineInfo.RegistryKey"/> when its owner is ambient-realm (the common case — RF routines
    /// in an RF compile, SF routines in an SF compile keep bare keys), or that key prefixed with
    /// <c>{ownerRealm}::</c> when the owner is a BRIDGED realm, so a bridged <c>RF::Core.List.add_last</c>
    /// never collides with / shadows the ambient <c>Core.List.add_last</c> (the RF-S406 creator clash).
    /// Owner-less (free) routines are never realm-bridged, so they use the bare key.
    /// </summary>
    public static string RealmRoutineKey(RoutineInfo routine) => routine.RegistryKey;

    /// <summary>
    /// Registers a type in the registry.
    /// </summary>
    /// <param name="type">The type to register.</param>
    /// <exception cref="InvalidOperationException">Thrown if the type is already registered.</exception>
    public void RegisterType(TypeInfo type)
    {
        string key = RealmRegistryKey(type: type);

        if (_types.ContainsKey(key: key))
        {
            throw new InvalidOperationException(message: $"Type '{key}' is already registered.");
        }

        _types[key: key] = type;
    }

    /// <summary>
    /// Updates a type in the registry, replacing it with a new version.
    /// Used for updating immutable type info after additional resolution (e.g., protocol memberRoutines).
    /// </summary>
    /// <param name="oldType">The old type to replace.</param>
    /// <param name="newType">The new type to register.</param>
    public void UpdateType(TypeInfo oldType, TypeInfo newType)
    {
        string key = RealmRegistryKey(type: oldType);
        if (_types.ContainsKey(key: key))
        {
            _types[key: key] = newType;
            _typesByShortName.Remove(key: oldType.Name);
        }
    }

    /// <summary>
    /// Updates a record type's implemented protocols.
    /// </summary>
    /// <param name="recordName">The name of the record to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateRecordProtocols(string recordName, List<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: recordName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not RecordTypeInfo record)
        {
            return;
        }

        // Mutate the protocol list in place to preserve the concrete subclass (Choice/Flags — and
        // Variant while it was a RecordTypeInfo subclass). ImplementedProtocols is settable, so this
        // is visible to any holder of the existing instance.
        record.ImplementedProtocols = protocols;
        _typesByShortName.Remove(key: record.Name);
    }

    /// <summary>
    /// Updates a record type with its resolved member variables.
    /// </summary>
    /// <param name="recordName">The name of the record to update.</param>
    /// <param name="memberVariables">The resolved member variables.</param>
    public void UpdateRecordMemberVariables(string recordName,
        List<MemberVariableInfo> memberVariables)
    {
        if (!_types.TryGetValue(key: recordName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not RecordTypeInfo record)
        {
            return;
        }

        // Mutate in-place so that all existing references (pending routine OwnerType pointers,
        // cached resolutions in _resolutions, etc.) see the updated member variable list.
        // Creating a new object here would leave _resolutions entries (e.g. Maybe[Bool] created
        // from the pre-registered carrier shell) with stale GenericDefinition pointers that have
        // empty MemberVariables — causing "Member variable 'present' not found on record
        // 'Core.Maybe[Core.Bool]'" errors during codegen of synthesized represent bodies that
        // iterate via for-loop (NonePattern lowering emits subject.present access).
        // Mirrors the in-place strategy in UpdateEntityMemberVariables.
        record.MemberVariables = memberVariables;
        RefreshRecordResolutions(genericDef: record);
    }

    /// <summary>
    /// Updates an entity type with its resolved member variables.
    /// </summary>
    /// <param name="entityName">The name of the entity to update.</param>
    /// <param name="memberVariables">The resolved member variables.</param>
    public void UpdateEntityMemberVariables(string entityName,
        List<MemberVariableInfo> memberVariables)
    {
        if (!_types.TryGetValue(key: entityName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not EntityTypeInfo entity)
        {
            return;
        }

        // Mutate in-place so that all existing references (pending routine OwnerType pointers,
        // cached resolutions in _resolutions, etc.) see the updated member variable list.
        // Creating a new object here would leave _resolutions entries with stale GenericDefinition
        // pointers that have empty MemberVariables — causing S450 "no member" errors when the SA
        // later resolves fields on resolved generic instances (e.g., ListNode[T].value).
        entity.MemberVariables = memberVariables;
        // Propagate to any already-cached generic resolutions of this entity definition.
        RefreshEntityResolutions(genericDef: entity);
    }

    /// <summary>
    /// Updates an entity type's implemented protocols.
    /// </summary>
    /// <param name="entityName">The name of the entity to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateEntityProtocols(string entityName, List<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: entityName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not EntityTypeInfo entity)
        {
            return;
        }

        entity.ImplementedProtocols = protocols;
    }

    /// <summary>Updates a crashable type's member variables.</summary>
    public void UpdateCrashableMemberVariables(string typeName,
        List<MemberVariableInfo> memberVariables)
    {
        if (!_types.TryGetValue(key: typeName, value: out TypeInfo? type) ||
            type is not CrashableTypeInfo crashable)
            return;

        var updated = new CrashableTypeInfo(name: crashable.Name)
        {
            MemberVariables = memberVariables,
            ImplementedProtocols = crashable.ImplementedProtocols,
            Visibility = crashable.Visibility,
            Location = crashable.Location,
            Module = crashable.Module
        };
        _types[key: typeName] = updated;
        _typesByShortName.Remove(key: crashable.Name);
    }

    /// <summary>Updates a crashable type's implemented protocols.</summary>
    public void UpdateCrashableProtocols(string typeName, List<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: typeName, value: out TypeInfo? type) ||
            type is not CrashableTypeInfo crashable)
            return;

        var updated = new CrashableTypeInfo(name: crashable.Name)
        {
            MemberVariables = crashable.MemberVariables,
            ImplementedProtocols = protocols,
            Visibility = crashable.Visibility,
            Location = crashable.Location,
            Module = crashable.Module
        };
        _types[key: typeName] = updated;
        _typesByShortName.Remove(key: crashable.Name);
    }

    /// <summary>
    /// Updates a choice type's implemented protocols.
    /// </summary>
    /// <param name="choiceName">The name of the choice to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateChoiceProtocols(string choiceName, List<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: choiceName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not ChoiceTypeInfo choice)
        {
            return;
        }

        var updatedChoice = new ChoiceTypeInfo(name: choice.Name)
        {
            Cases = choice.Cases,
            ImplementedProtocols = protocols,
            UnderlyingType = choice.UnderlyingType,
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = choice.Module
        };

        _types[key: choiceName] = updatedChoice;
        _typesByShortName.Remove(key: choice.Name);
    }

    /// <summary>
    /// Updates a flags type's implemented protocols.
    /// </summary>
    /// <param name="flagsName">The name of the flags type to update.</param>
    /// <param name="protocols">The resolved protocol types.</param>
    public void UpdateFlagsProtocols(string flagsName, List<TypeInfo> protocols)
    {
        if (!_types.TryGetValue(key: flagsName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not FlagsTypeInfo flags)
        {
            return;
        }

        var updatedFlags = new FlagsTypeInfo(name: flags.Name)
        {
            Members = flags.Members,
            ImplementedProtocols = protocols,
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = flags.Module
        };

        _types[key: flagsName] = updatedFlags;
        _typesByShortName.Remove(key: flags.Name);
    }

    /// <summary>
    /// Updates a protocol type's parent protocols.
    /// </summary>
    /// <param name="protocolName">The name of the protocol to update.</param>
    /// <param name="parentProtocols">The resolved parent protocol types.</param>
    public void UpdateProtocolParents(string protocolName,
        List<ProtocolTypeInfo> parentProtocols)
    {
        if (!_types.TryGetValue(key: protocolName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not ProtocolTypeInfo protocol)
        {
            return;
        }

        var updatedProtocol = new ProtocolTypeInfo(name: protocol.Name)
        {
            MemberRoutines = protocol.MemberRoutines,
            ParentProtocols = parentProtocols,
            GenericParameters = protocol.GenericParameters,
            GenericConstraints = protocol.GenericConstraints,
            TypeArguments = protocol.TypeArguments,
            GenericDefinition = protocol.GenericDefinition,
            Visibility = protocol.Visibility,
            Location = protocol.Location,
            Module = protocol.Module
        };

        _types[key: protocolName] = updatedProtocol;
        _typesByShortName.Remove(key: protocol.Name);
    }

    /// <summary>
    /// Updates a choice type with its resolved cases.
    /// </summary>
    /// <param name="choiceName">The name of the choice to update.</param>
    /// <param name="cases">The resolved choice cases.</param>
    public void UpdateChoiceCases(string choiceName, List<ChoiceCaseInfo> cases)
    {
        if (!_types.TryGetValue(key: choiceName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not ChoiceTypeInfo choice)
        {
            return;
        }

        // Create updated choice with cases
        var updatedChoice = new ChoiceTypeInfo(name: choice.Name)
        {
            Cases = cases,
            UnderlyingType = choice.UnderlyingType,
            GenericParameters = choice.GenericParameters,
            GenericConstraints = choice.GenericConstraints,
            Visibility = choice.Visibility,
            Location = choice.Location,
            Module = choice.Module
        };

        _types[key: choiceName] = updatedChoice;
        _typesByShortName.Remove(key: choice.Name);
    }

    /// <summary>
    /// Updates the declared member set for an already-registered flags type.
    /// </summary>
    public void UpdateFlagsMembers(string flagsName, List<FlagsMemberInfo> members)
    {
        if (!_types.TryGetValue(key: flagsName, value: out TypeInfo? type))
        {
            return;
        }

        if (type is not FlagsTypeInfo flags)
        {
            return;
        }

        var updated = new FlagsTypeInfo(name: flags.Name)
        {
            Members = members,
            Visibility = flags.Visibility,
            Location = flags.Location,
            Module = flags.Module
        };

        _types[key: flagsName] = updated;
        _typesByShortName.Remove(key: flags.Name);
    }

    /// <summary>
    /// Looks up a choice case by name across all choice types.
    /// </summary>
    /// <param name="caseName">The name of the choice case to look up.</param>
    /// <returns>A tuple of the choice type and case info if found, null otherwise.</returns>
    public (ChoiceTypeInfo ChoiceType, ChoiceCaseInfo CaseInfo)? LookupChoiceCase(string caseName)
    {
        foreach (TypeInfo type in _types.Values)
        {
            if (type is ChoiceTypeInfo choiceType)
            {
                ChoiceCaseInfo? caseInfo =
                    choiceType.Cases.FirstOrDefault(predicate: c => c.Name == caseName);
                if (caseInfo != null)
                {
                    return (choiceType, caseInfo);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a type by name IN A SPECIFIC REALM — the realm-aware sibling of <see cref="LookupType(string)"/>.
    /// The ambient realm keys bare (delegates to the realm-blind lookup); a bridged realm is keyed by
    /// <see cref="TypeInfo.RealmQualifiedName"/> (<c>{realm}::{FullName}</c>), so this reaches the SF-realm
    /// <c>Core.List</c> even while the RazorForge-realm <c>Core.List</c> occupies the bare key (and vice
    /// versa). Returns null when no type of that realm is registered (caller decides whether to fall back).
    /// </summary>
    public TypeInfo? LookupType(string name, string realm)
    {
        if (realm == AmbientRealm)
        {
            return LookupTypeInAmbient(name: name);
        }
        // Bridged realm: registered under the realm-marked key. Try the name as given, and — for a bare
        // name whose ambient hit reveals the module-qualified FullName — the realm-prefixed FullName.
        if (_types.TryGetValue(key: $"{realm}::{name}", value: out TypeInfo? direct))
        {
            return direct;
        }
        return LookupTypeInAmbient(name: name) is { } ambient
               && _types.TryGetValue(key: $"{realm}::{ambient.FullName}", value: out TypeInfo? viaFull)
            ? viaFull
            : null;
    }

    /// <summary>
    /// Looks up a type by name, PREFERRING the current <see cref="ResolutionRealm"/> (per-file) over the
    /// bare ambient realm. When resolution realm == ambient (every RF compile, and RF files in an SF
    /// compile) this is exactly the plain ambient lookup — zero behavioral change. When analyzing an SF
    /// file (resolution realm SF, ambient RF) a bare name that has an SF-realm (bridged) type resolves to
    /// it first, else falls through to the RF-realm type.
    /// </summary>
    public TypeInfo? LookupType(string name)
    {
        TypeInfo? hit = LookupTypeInAmbient(name: name);
        if (ResolutionRealm != AmbientRealm && hit != null
            && _types.TryGetValue(key: $"{ResolutionRealm}::{hit.FullName}", value: out TypeInfo? preferred))
        {
            return preferred;
        }
        return hit;
    }

    /// <summary>
    /// Looks up a type by name in the AMBIENT realm only (the realm-blind workhorse). Callers that must
    /// honor the per-file <see cref="ResolutionRealm"/> use <see cref="LookupType(string)"/> instead.
    /// </summary>
    private TypeInfo? LookupTypeInAmbient(string name)
    {
        // Try exact match first
        if (_types.TryGetValue(key: name, value: out TypeInfo? type))
        {
            return type;
        }

        // Try resolution cache
        if (_resolutions.TryGetValue(key: name, value: out TypeInfo? resolution))
        {
            if (!_stdlibAnalysisActive) MaterializeIfLazy(resolution);
            return resolution;
        }

        // Try Core module prefix (Core types are auto-imported)
        if (!name.Contains(value: '.') && _types.TryGetValue(key: $"Core.{name}", value: out type))
        {
            return type;
        }

        // (REMOVED — task 18) The cross-module short-name scan used to resolve a bare `Name` to ANY
        // module's `*.Name` (an arbitrary first match). It was the over-permissive "leak": it let a bare
        // reference bind a same-named type/routine in an unrelated module, masked missing imports, and
        // made resolution order-dependent. Every load-bearing dependent has been migrated to a
        // module-qualified or import-aware lookup (own-module / imported-module / Core-prefix above, or
        // an explicit `{module}.{name}` at the call site). A bare name that resolves to nothing here is now
        // an honest "unknown type" instead of a silent cross-module capture.

        return null;
    }

    /// <summary>
    /// Re-resolves <paramref name="type"/> into the <paramref name="realm"/> world-line — the mechanism
    /// behind an explicit <c>RF::</c>/<c>SF::</c> qualifier. Name resolution is realm-blind and always yields
    /// the AMBIENT-realm type (keyed bare); this swaps it to the bridged realm's equivalent, which the
    /// registry keys by <see cref="TypeInfo.RealmQualifiedName"/> (<c>{realm}::{FullName}</c>). Returns null
    /// when no such bridged type is registered (caller keeps the ambient resolution — for a pure-ambient
    /// compile this path is never meaningfully hit, so behavior is unchanged).
    /// </summary>
    public TypeInfo? ReResolveInRealm(TypeInfo type, string realm)
    {
        if (type.Realm == realm)
        {
            return type;
        }

        // A generic RESOLUTION (e.g. List[T]): find the target-realm generic DEFINITION (keyed
        // `{realm}::{def.FullName}`) and re-resolve the same arguments against it.
        TypeInfo? genericDef = type switch
        {
            EntityTypeInfo { GenericDefinition: { } gd } => gd,
            RecordTypeInfo { GenericDefinition: { } gd } => gd,
            ProtocolTypeInfo { GenericDefinition: { } gd } => gd,
            _ => null
        };
        if (genericDef != null && type.TypeArguments is { Count: > 0 } args)
        {
            // The target realm's DEFINITION is keyed BARE when it is the ambient realm (RF types under an
            // RF-ambient compile), or `{realm}::`-marked when bridged. Using the ambient (bare) lookup for
            // the ambient case is what lets `RF::Core.List` reach the RF-realm list (keyed bare `Core.List`).
            TypeInfo? tDef;
            if (realm == AmbientRealm)
                tDef = LookupTypeInAmbient(name: genericDef.FullName);
            else
                tDef = _types.TryGetValue(key: $"{realm}::{genericDef.FullName}", value: out TypeInfo? d) ? d : null;
            return tDef != null ? GetOrCreateResolution(genericDef: tDef, typeArguments: [.. args]) : null;
        }

        // A non-generic type or a bare generic definition: bare key for the ambient realm, else realm-marked.
        if (realm == AmbientRealm)
            return LookupTypeInAmbient(name: type.FullName);
        return _types.TryGetValue(key: $"{realm}::{type.FullName}", value: out TypeInfo? t) ? t : null;
    }

    /// <summary>
    /// Gets or creates a resolved generic type.
    /// </summary>
    /// <param name="genericDef">The generic type definition.</param>
    /// <param name="typeArguments">The type arguments for resolution.</param>
    /// <returns>The resolved type (cached if already created).</returns>
    public TypeInfo GetOrCreateResolution(TypeInfo genericDef,
        List<TypeInfo> typeArguments)
    {
        // Don't create or store instances where SA failed to resolve a type argument.
        // Storing ErrorTypeInfo-keyed instances produces broken concrete types that crash codegen.
        if (typeArguments.Any(predicate: t => t is ErrorTypeInfo))
            return genericDef;

        // Primary key uses FullName for each type argument (e.g. "Hijacked[Core.Byte]").
        // A short-name alias (e.g. "Hijacked[Byte]") is stored as a backward-compatible fallback.
        // Both keys map to the same TypeInfo, so AllConcreteGenericInstances uses .Distinct()
        // to avoid double-processing. Wrapper types are stored in _wrapperResolutions (not here)
        // to prevent key collisions at the FullName level.
        string fullKey =
            $"{genericDef.Name}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.FullName))}]";
        // WrapperTypeInfo.Name is bare ("Owned") without inner type args, so using Name alone
        // collapses "Maybe[X]" and "Maybe[Y]" to the same shortKey "Maybe[Owned]".
        // GetShortName expands wrappers to "Wrapper[Inner.Name]" to keep shortKeys distinct.
        string shortKey =
            $"{genericDef.Name}[{string.Join(separator: ", ", values: typeArguments.Select(selector: GetShortName))}]";
        // Module-qualified full key: used when @llvm_ir type args are rewritten to fully-qualified
        // names by GenericAstRewriter (e.g. "Collections.BTreeSetNode[Core.S64]"). A BRIDGED (non-ambient)
        // realm def gets a leading `RF::`/`SF::` so a bridged `RF::Core.List[S64]` keys distinctly from the
        // ambient `Core.List[S64]`; the ambient realm adds no prefix (unchanged for a single-realm compile).
        string realmPrefix = genericDef.Realm != AmbientRealm ? $"{genericDef.Realm}::" : "";
        string? moduleFullKey = genericDef.FullName != genericDef.Name
            ? $"{realmPrefix}{genericDef.FullName}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.FullName))}]"
            : null;

        // The module-qualified key is DISTINCT across modules/realms (RazorForge `Core.List` vs the
        // Suflae-realm overlay `Suflae.List`), so check it FIRST — the bare fullKey/shortKey below are
        // module-blind aliases that a same-named other-module type can occupy first-wins.
        if (TryGetCachedResolution(fullKey: fullKey, shortKey: shortKey, moduleFullKey: moduleFullKey,
                genericDef: genericDef, typeArguments: typeArguments) is { } cachedHit)
        {
            return cachedHit;
        }

        // If an entity-type specialization exists for this generic and the first type argument
        // is an entity type, use that specialization instead of the primary (record-type) definition.
        // This ensures e.g. Maybe[Text] gets { Hijacked[T] } layout instead of { Bool, T }.
        TypeInfo bestDef = genericDef;
        if (typeArguments.Count > 0 && typeArguments[index: 0] is EntityTypeInfo &&
            _entitySpecializations.TryGetValue(key: genericDef.Name,
                value: out TypeInfo? entitySpec))
        {
            bestDef = entitySpec;
        }

        TypeInfo resolved = bestDef.CreateInstance(typeArguments: typeArguments);
        // Decl-position expand: materialize struct-of-arrays column members from the generic def's
        // templates (one per member of the concrete source type). Appends real member variables so the
        // SoA layout falls out of ordinary record layout.
        ExpandSoAColumns(genericDef: bestDef, resolved: resolved, typeArguments: typeArguments);
        // Store the DISTINCT module-qualified key authoritatively. The bare fullKey/shortKey are
        // module-blind aliases (backward-compatible `LookupType("List[Core.S32]")` / "List[S32]"):
        // write them only when free or already ours, so a same-named OTHER-module type keeps its own
        // bare alias instead of being clobbered (the cross-module collision this all guards against).
        if (moduleFullKey != null && moduleFullKey != fullKey)
            _resolutions[key: moduleFullKey] = resolved;
        if (!_resolutions.TryGetValue(key: fullKey, value: out TypeInfo? bareOccupant)
            || ResolutionGenericDefMatches(resolved: bareOccupant, genericDef: genericDef))
            _resolutions[key: fullKey] = resolved;
        if (fullKey != shortKey
            && (!_resolutions.TryGetValue(key: shortKey, value: out TypeInfo? shortOccupant)
                || ResolutionGenericDefMatches(resolved: shortOccupant, genericDef: genericDef)))
            _resolutions[key: shortKey] = resolved;

        if (_stdlibAnalysisActive)
        {
            // Defer: this type was created as a side-effect of stdlib body analysis.
            // It is excluded from GMP until user code actually references it, at which
            // point MaterializeIfLazy will enqueue it. This prevents 22K+ phantom bodies
            // from being monomorphized for types the user program never imports.
            resolved.IsStdlibLazy = true;
        }
        else
        {
            EnqueueDiscoveredResolution(resolved: resolved);
        }

        return resolved;
    }

    /// <summary>
    /// Notifies GMP's fixed-point loop about a newly created concrete entity/record resolution. Guards:
    /// (1) fully-concrete (no unresolved param args, avoiding LookupMemberRoutine recursion); (2) no
    /// self-nesting (a type arg whose FullName contains the outer bare base name, e.g.
    /// <c>Hijacked[Hijacked[Text]]</c>, would recurse unboundedly).
    /// </summary>
    private void EnqueueDiscoveredResolution(TypeInfo resolved)
    {
        if (_gmpDiscoveryQueue != null && resolved is EntityTypeInfo or RecordTypeInfo &&
            IsFullyConcrete(resolved))
        {
            string bareBaseName = resolved.BareName;
            bool isSelfNesting = resolved.TypeArguments != null &&
                                 resolved.TypeArguments.Any(arg => arg.FullName.Contains(bareBaseName));
            if (!isSelfNesting)
                _gmpDiscoveryQueue.Enqueue(resolved);
        }
    }

    /// <summary>
    /// Probes the three resolution-cache aliases (module-qualified, bare full, bare short) for an
    /// existing instance of the requested generic, materializing it if lazy. The module-qualified key is
    /// distinct across modules/realms so it is checked first; the bare aliases are module-blind and only
    /// accepted when the cached resolution's generic definition (and, for the short alias, its type args)
    /// match the request. Returns null when no matching cached resolution exists.
    /// </summary>
    private TypeInfo? TryGetCachedResolution(string fullKey, string shortKey, string? moduleFullKey,
        TypeInfo genericDef, List<TypeInfo> typeArguments)
    {
        if (moduleFullKey != null &&
            _resolutions.TryGetValue(key: moduleFullKey, value: out TypeInfo? existing))
        {
            if (!_stdlibAnalysisActive) MaterializeIfLazy(existing);
            return existing;
        }
        // Bare fullKey: accept only when the cached resolution's generic DEFINITION is the one requested
        // (no-op for single-realm types; rejects a Suflae `List[Core.S32]` for a Core.List request).
        if (_resolutions.TryGetValue(key: fullKey, value: out existing)
            && ResolutionGenericDefMatches(resolved: existing, genericDef: genericDef))
        {
            if (!_stdlibAnalysisActive) MaterializeIfLazy(existing);
            return existing;
        }
        // The shortKey is a bare-type-arg alias (e.g. "Modifying[Counter]") shared by callers that
        // look up by short arg name. It COLLIDES when two modules declare a same-named type
        // (Modifying[A/Counter] vs Modifying[B/Counter]): a first-wins short alias would return the
        // wrong module's inner type, contaminating wrapper forwarding / memberRoutine dispatch. Only accept a
        // short-alias hit whose type arguments AND generic definition match the request.
        if (fullKey != shortKey && _resolutions.TryGetValue(key: shortKey, value: out existing)
            && ResolutionTypeArgsMatch(resolved: existing, typeArguments: typeArguments)
            && ResolutionGenericDefMatches(resolved: existing, genericDef: genericDef))
        {
            if (!_stdlibAnalysisActive) MaterializeIfLazy(existing);
            return existing;
        }

        return null;
    }

    /// <summary>
    /// Materializes decl-position <c>expand</c> columns onto a freshly-created concrete instance: for
    /// each column template on the generic definition, appends one member variable per member of the
    /// concrete source type (<c>${m.name}</c> → the column name, <c>${m.type}</c> → the column element
    /// type). The struct-of-arrays layout of <c>SplitArray[T, N]</c>/<c>SplitList[T]</c> then falls out
    /// of ordinary record layout — no bespoke codegen.
    /// </summary>
    private static void ExpandSoAColumns(TypeInfo genericDef, TypeInfo resolved,
        List<TypeInfo> typeArguments)
    {
        (List<MemberExpandTemplateInfo> templates, List<string>? genericParams) = genericDef switch
        {
            RecordTypeInfo r => (r.ExpandTemplates, r.GenericParameters),
            EntityTypeInfo e => (e.ExpandTemplates, e.GenericParameters),
            _ => ([], null)
        };
        if (templates.Count == 0 || genericParams == null)
        {
            return;
        }

        // Base substitution: each generic parameter -> its concrete argument.
        var baseSubs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < genericParams.Count && i < typeArguments.Count; i++)
        {
            baseSubs[key: genericParams[index: i]] = typeArguments[index: i];
        }

        List<MemberVariableInfo> target = resolved switch
        {
            RecordTypeInfo r => r.MemberVariables,
            EntityTypeInfo e => e.MemberVariables,
            _ => null!
        };
        if (target == null)
        {
            return;
        }

        foreach (MemberExpandTemplateInfo template in templates)
        {
            // The concrete source type whose members become columns (the T in `allmemvarof(T)`).
            if (!baseSubs.TryGetValue(key: template.SourceParamName, value: out TypeInfo? sourceType))
            {
                continue;
            }
            List<MemberVariableInfo> sourceMembers = sourceType switch
            {
                RecordTypeInfo r => r.MemberVariables,
                EntityTypeInfo e => e.MemberVariables,
                _ => []
            };

            foreach (MemberVariableInfo field in sourceMembers)
            {
                // Per-field substitution: the `${m.type}` placeholder binds to this field's type.
                var subs = new Dictionary<string, TypeInfo>(dictionary: baseSubs,
                    comparer: StringComparer.Ordinal)
                {
                    [key: MemberExpandTemplateInfo.ColumnPlaceholderName] = field.Type
                };
                TypeInfo columnType = RecordTypeInfo.SubstituteType(type: template.ColumnTypeTemplate,
                    substitution: subs);
                target.Add(item: new MemberVariableInfo(
                    name: template.NamePrefix + field.Name, type: columnType)
                {
                    Visibility = template.Visibility,
                    Index = target.Count,
                    Owner = resolved
                });
            }
        }
    }

    /// <summary>
    /// Returns true when <paramref name="resolved"/>'s type arguments match <paramref name="typeArguments"/>
    /// by fully-qualified name. Guards the bare short-alias cache hit in <see cref="GetOrCreateResolution"/>
    /// so a same-short-name type from a DIFFERENT module (e.g. two modules' <c>Counter</c>) is not
    /// mistaken for the requested one.
    /// </summary>
    private static bool ResolutionTypeArgsMatch(TypeInfo resolved, List<TypeInfo> typeArguments)
    {
        List<TypeInfo>? actual = resolved.TypeArguments;
        if (actual == null || actual.Count != typeArguments.Count) return false;
        for (int i = 0; i < actual.Count; i++)
        {
            if (actual[index: i].FullName != typeArguments[index: i].FullName) return false;
        }
        return true;
    }

    /// <summary>
    /// True when a cached resolution's generic DEFINITION is the one being requested (matched by
    /// module-qualified FullName). Guards the bare, module-blind resolution-cache aliases against
    /// returning a same-NAMED type from a DIFFERENT module/realm — a request for `Core.List[S32]` must
    /// not be satisfied by a cached Suflae-realm `Suflae.List[S32]` sharing the bare key `List[Core.S32]`.
    /// For single-realm types (one type of a given name) the definition matches itself, so this is a
    /// no-op; it only bites on genuine cross-module name collisions. Falls back to bare-name equality
    /// when the cached instance records no definition (older/aliased instances).
    /// </summary>
    private static bool ResolutionGenericDefMatches(TypeInfo resolved, TypeInfo genericDef)
    {
        TypeInfo? resolvedDef = resolved switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };
        // Realm must ALSO match: `FullName`/`BareName` are realm-FREE, so a Suflae-realm `Core.List[S32]`
        // would otherwise satisfy a request for the RazorForge-realm `Core.List` def via the module-blind
        // bare alias — handing back the SF wrapper for an `RF::Core.List[T]()` construction, whose `create`
        // then self-recurses. Comparing realm keeps the two world-lines' resolutions distinct.
        return resolvedDef == null
            ? resolved.BareName == genericDef.BareName && resolved.Realm == genericDef.Realm
            : resolvedDef.FullName == genericDef.FullName && resolvedDef.Realm == genericDef.Realm;
    }

    /// <summary>
    /// Looks up an existing concrete resolution without creating a new one.
    /// Returns null if the type has not been resolved yet.
    /// Use this in passes that must not create new concrete type instances as a side effect.
    /// </summary>
    public TypeInfo? TryGetResolution(TypeInfo genericDef, List<TypeInfo> typeArguments)
    {
        string fullKey =
            $"{genericDef.Name}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.FullName))}]";
        // Module-qualified key first (distinct across modules/realms); mirrors GetOrCreateResolution
        // incl. the bridged-realm `RF::`/`SF::` prefix (ambient realm adds none).
        string realmPrefix = genericDef.Realm != AmbientRealm ? $"{genericDef.Realm}::" : "";
        string? moduleFullKey = genericDef.FullName != genericDef.Name
            ? $"{realmPrefix}{genericDef.FullName}[{string.Join(separator: ", ", values: typeArguments.Select(selector: t => t.FullName))}]"
            : null;
        if (moduleFullKey != null && _resolutions.TryGetValue(key: moduleFullKey, value: out TypeInfo? existing))
            return existing;
        if (_resolutions.TryGetValue(key: fullKey, value: out existing)
            && ResolutionGenericDefMatches(resolved: existing, genericDef: genericDef))
            return existing;
        string shortKey =
            $"{genericDef.Name}[{string.Join(separator: ", ", values: typeArguments.Select(selector: GetShortName))}]";
        // The shortKey is a bare-type-arg alias and COLLIDES when two modules declare a same-named type
        // (Hijacked[A/Point] vs Hijacked[B/Point]): a first-wins short alias would return the wrong module's
        // instance, mis-dispatching member routines (a value-record `peek` resolving to an entity `peek`).
        // Accept a short-alias hit ONLY when its type arguments AND generic definition match — mirroring
        // TryGetCachedResolution (this lookup-only twin was missing the args check).
        if (fullKey != shortKey && _resolutions.TryGetValue(key: shortKey, value: out existing)
            && ResolutionTypeArgsMatch(resolved: existing, typeArguments: typeArguments)
            && ResolutionGenericDefMatches(resolved: existing, genericDef: genericDef))
            return existing;
        // Wrapper types (Hijacked, Retained, etc.) are stored in _wrapperResolutions, not _resolutions.
        if (_wrapperResolutions.TryGetValue(key: fullKey, value: out WrapperTypeInfo? wrapper))
            return wrapper;
        if (fullKey != shortKey &&
            _wrapperResolutions.TryGetValue(key: shortKey, value: out wrapper)
            && ResolutionTypeArgsMatch(resolved: wrapper, typeArguments: typeArguments))
            return wrapper;
        return null;
    }

    /// <summary>
    /// Refreshes stale cached entity resolutions whose member variable list is incomplete.
    /// Called after pass 1c updates a generic entity definition with its full member list.
    /// </summary>
    public void RefreshEntityResolutions(EntityTypeInfo genericDef)
    {
        foreach (TypeInfo resolution in _resolutions.Values)
        {
            if (resolution is EntityTypeInfo entityRes &&
                entityRes.GenericDefinition == genericDef &&
                entityRes.MemberVariables.Count < genericDef.MemberVariables.Count &&
                entityRes.TypeArguments != null)
            {
                var fresh =
                    (EntityTypeInfo)genericDef.CreateInstance(
                        typeArguments: entityRes.TypeArguments);
                entityRes.MemberVariables = fresh.MemberVariables;
            }
        }
    }

    /// <summary>
    /// Refreshes stale cached record resolutions whose member variable list is incomplete.
    /// Called after <see cref="UpdateRecordMemberVariables"/> updates a generic record definition
    /// (e.g. Maybe[T], Result[T]) with its full member list. Mirrors
    /// <see cref="RefreshEntityResolutions"/>.
    /// </summary>
    public void RefreshRecordResolutions(RecordTypeInfo genericDef)
    {
        foreach (TypeInfo resolution in _resolutions.Values)
        {
            if (resolution is RecordTypeInfo recordRes &&
                recordRes.GenericDefinition == genericDef &&
                recordRes.MemberVariables.Count < genericDef.MemberVariables.Count &&
                recordRes.TypeArguments != null)
            {
                var fresh =
                    (RecordTypeInfo)genericDef.CreateInstance(
                        typeArguments: recordRes.TypeArguments);
                recordRes.MemberVariables = fresh.MemberVariables;
            }
        }
    }

    /// <summary>
    /// Refreshes stale cached protocol resolutions whose memberRoutine signatures are out of date.
    /// Called after <c>ResolveProtocolMemberRoutineReturnTypes</c> (Pass 1e) re-fills a generic protocol
    /// definition (e.g. MutableIndexable[V]) whose memberRoutines were initially registered with dropped
    /// forward-reference params — a concrete <c>index: U64</c> param silently dropped because U64
    /// wasn't registered yet. Instances created before that re-fill (during earlier stdlib
    /// registration, e.g. List's <c>obeys MutableIndexable[T]</c>) hold stale memberRoutine signatures and
    /// are cached, so user types obeying the protocol pick up the stale 1-param <c>setitem</c> and
    /// wrongly fail conformance (S703). Mirrors <see cref="RefreshEntityResolutions"/> /
    /// <see cref="RefreshRecordResolutions"/> by rebuilding memberRoutines in place so existing references
    /// (e.g. a collection's ImplementedProtocols) also see the fix.
    /// </summary>
    public void RefreshProtocolResolutions(ProtocolTypeInfo genericDef)
    {
        if (!genericDef.IsGenericDefinition)
        {
            return;
        }

        // Snapshot: CreateInstance below can register a NEW resolution (e.g. the protocol member's own
        // return/param instantiations), mutating _resolutions mid-enumeration. A generic literal protocol
        // with a variadic member (`ListLiteral[T].from_literal`) makes this reliably reachable.
        foreach (TypeInfo resolution in _resolutions.Values.ToList())
        {
            if (resolution is ProtocolTypeInfo protoRes &&
                protoRes.GenericDefinition == genericDef &&
                protoRes.TypeArguments != null &&
                IsProtocolResolutionStale(instance: protoRes, genericDef: genericDef))
            {
                var fresh =
                    (ProtocolTypeInfo)genericDef.CreateInstance(
                        typeArguments: protoRes.TypeArguments);
                protoRes.MemberRoutines = fresh.MemberRoutines;
            }
        }
    }

    /// <summary>
    /// A cached protocol instance is stale if any of its memberRoutines is missing or has a different
    /// parameter arity than the (just re-filled) generic definition's matching memberRoutine.
    /// </summary>
    private static bool IsProtocolResolutionStale(ProtocolTypeInfo instance,
        ProtocolTypeInfo genericDef)
    {
        foreach (ProtocolMemberRoutineInfo defMemberRoutine in genericDef.MemberRoutines)
        {
            ProtocolMemberRoutineInfo? instMemberRoutine = instance.MemberRoutines.FirstOrDefault(predicate: m =>
                m.Name == defMemberRoutine.Name && m.IsFailable == defMemberRoutine.IsFailable);
            if (instMemberRoutine == null ||
                instMemberRoutine.ParameterTypes.Count != defMemberRoutine.ParameterTypes.Count)
            {
                return true;
            }
        }

        return false;
    }

    /// Short name for a type argument used in the shortKey of GetOrCreateResolution / TryGetResolution.
    /// WrapperTypeInfo.Name is bare ("Owned") without inner args, so we expand it recursively to
    /// "InnerName" to prevent shortKey collisions across different inner types.
    private static string GetShortName(TypeInfo t) =>
        t is WrapperTypeInfo wt
            ? $"{wt.Name}[{GetShortName(wt.InnerType)}]"
            : t.Name;

    /// <summary>
    /// Gets or creates a function type with the given parameter and return types.
    /// FreeRoutine types are cached by their signature.
    /// </summary>
    /// <param name="parameterTypes">The parameter types.</param>
    /// <param name="returnType">The return type (null for None/void).</param>
    /// <param name="isFailable">Whether the function can throw/absent.</param>
    /// <returns>The cached or newly created function type.</returns>
    public RoutineTypeInfo GetOrCreateRoutineType(List<TypeInfo> parameterTypes,
        TypeInfo? returnType, bool isFailable = false)
    {
        // Build the signature key
        string paramList = string.Join(separator: ", ",
            values: parameterTypes.Select(selector: p => p.Name));
        string returnName = returnType?.Name ?? "None";
        string failableSuffix = isFailable
            ? "!"
            : "";
        string key = $"({paramList}) -> {returnName}{failableSuffix}";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) &&
            existing is RoutineTypeInfo routineType)
        {
            return routineType;
        }

        // Create and cache
        var newType =
            new RoutineTypeInfo(parameterTypes: parameterTypes, returnType: returnType)
            {
                IsFailable = isFailable
            };
        _resolutions[key: key] = newType;

        // A routine VALUE is Serializable — like a tuple registers its memberRoutines at creation (structural
        // types never reach the AutoWiredRegistrationPass named-type loop). Serializing executable code is
        // meaningless, but a routine has a first-class `represent` (its signature), so serialize boxes that
        // Text. Registering the per-type memberRoutine lets the universal serialize walk over a routine-typed
        // member (`entity Callback { f: Routine[...] }`) resolve; the BODY is the zero-field
        // BuildSerializeBody path, synthesized in WiredRoutinePass's routine-type loop.
        TypeInfo? routineSerialValue = LookupType(name: "SerialValue");
        if (routineSerialValue != null)
        {
            RegisterRoutine(routine: new RoutineInfo(name: RuntimeContract.Serialize)
            {
                Kind = RoutineKind.MemberRoutine,
                OwnerType = newType,
                Parameters = [],
                ReturnType = routineSerialValue,
                IsFailable = false,
                DeclaredMutation = MutationCategory.Readonly,
                MutationCategory = MutationCategory.Readonly,
                Visibility = VisibilityModifier.Open,
                IsSynthesized = true
            });
        }

        // A routine VALUE is a trivially-destructible LEAF (a fat pointer over non-owned code + env), but the
        // auto-derived record/entity `destroy` field-walk (`me.$nameof(m).destroy()`, DeriveText) assumes EVERY
        // member type has a `destroy` — "a no-op for a raw-pointer / scalar / borrow leaf". A routine-typed
        // field otherwise resolves that call to nothing, so codegen mangles + references `Routine[...].destroy`
        // with no definition (link-undefined). Register the no-op leaf destroy here (BODY = bare `return`,
        // synthesized in WiredRoutinePass's routine-type loop) so the invariant holds and the field-walk links.
        RegisterRoutine(routine: new RoutineInfo(name: "destroy")
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = newType,
            Parameters = [],
            ReturnType = LookupType(name: "None"),
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });

        return newType;
    }

    /// <summary>
    /// Gets or creates a tuple type with the given element types.
    /// Tuple types are cached by their element type signature.
    /// </summary>
    /// <param name="elementTypes">The types of each element in the tuple.</param>
    /// <returns>The cached or newly created tuple type.</returns>
    public TupleTypeInfo GetOrCreateTupleType(List<TypeInfo> elementTypes)
    {
        // Build the cache key
        string typeList = string.Join(separator: ", ",
            values: elementTypes.Select(selector: t => t.FullName));
        string key = $"Tuple[{typeList}]";

        // Check cache
        if (_resolutions.TryGetValue(key: key, value: out TypeInfo? existing) &&
            existing is TupleTypeInfo tupleType)
        {
            return tupleType;
        }

        // Create and cache
        var newType = new TupleTypeInfo(elementTypes: elementTypes);
        _resolutions[key: key] = newType;

        RegisterTupleDisplayRoutines(newType: newType);
        RegisterTupleEqualityRoutines(newType: newType, elementTypes: elementTypes);
        RegisterTupleHashRoutine(newType: newType, elementTypes: elementTypes);
        RegisterTupleSerializeRoutine(newType: newType, elementTypes: elementTypes);
        RegisterTupleStoreRoutine(newType: newType, elementTypes: elementTypes);
        RegisterTupleComparisonRoutines(newType: newType, elementTypes: elementTypes);

        return newType;
    }

    /// <summary>Registers a synthesized readonly zero-arg member routine <paramref name="name"/> on
    /// <paramref name="owner"/> returning <paramref name="returnType"/>, with optional parameters.</summary>
    private void RegisterSynthesizedMemberRoutine(string name, TypeInfo owner, TypeInfo returnType,
        List<ParameterInfo>? parameters = null)
    {
        RegisterRoutine(routine: new RoutineInfo(name: name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = owner,
            Parameters = parameters ?? [],
            ReturnType = returnType,
            IsFailable = false,
            DeclaredMutation = MutationCategory.Readonly,
            MutationCategory = MutationCategory.Readonly,
            Visibility = VisibilityModifier.Open,
            IsSynthesized = true
        });
    }

    /// <summary>Auto-registers <c>TupleType.represent()</c> / <c>diagnose()</c> when Text is available.</summary>
    private void RegisterTupleDisplayRoutines(TupleTypeInfo newType)
    {
        TypeInfo? textType = LookupType(name: "Text");
        if (textType != null)
        {
            RegisterSynthesizedMemberRoutine(name: RuntimeContract.Display.Represent, owner: newType, returnType: textType);
            RegisterSynthesizedMemberRoutine(name: RuntimeContract.Display.Diagnose, owner: newType, returnType: textType);
        }
    }

    /// <summary>
    /// Auto-registers <c>eq</c> and <c>ne</c> if every element type has <c>eq</c> (option a: structural
    /// derivation iff all components support it). Component types whose owners haven't opted into
    /// Equatable simply won't have eq registered, so the tuple won't either.
    /// </summary>
    private void RegisterTupleEqualityRoutines(TupleTypeInfo newType, List<TypeInfo> elementTypes)
    {
        TypeInfo? boolType = LookupType(name: "Bool");
        if (boolType != null &&
            elementTypes.All(predicate: et => LookupMemberRoutine(type: et, memberRoutineName: "eq") != null))
        {
            var youParam = new ParameterInfo(name: "you", type: newType);
            RegisterSynthesizedMemberRoutine(name: "eq", owner: newType, returnType: boolType, parameters: [youParam]);
            RegisterSynthesizedMemberRoutine(name: "ne", owner: newType, returnType: boolType, parameters: [youParam]);
        }
    }

    /// <summary>Auto-registers <c>hash</c> if ALL element types support hash.</summary>
    private void RegisterTupleHashRoutine(TupleTypeInfo newType, List<TypeInfo> elementTypes)
    {
        TypeInfo? u64Type = LookupType(name: "U64");
        if (u64Type != null &&
            elementTypes.All(predicate: et => LookupMemberRoutine(type: et, memberRoutineName: "hash") != null))
        {
            RegisterSynthesizedMemberRoutine(name: "hash", owner: newType, returnType: u64Type);
        }
    }

    /// <summary>
    /// Auto-registers <c>serialize</c> if EVERY element is serializable — or a routine (the derive
    /// template boxes a routine element's signature via <c>represent</c>). A tuple can hold otherwise-
    /// unserializable elements (unlike a record), so gate on the elements. Generic-parameter elements
    /// (<c>Tuple[U64, T]</c>) are excluded — the body clones per CONCRETE instantiation (RF-S959).
    /// </summary>
    private void RegisterTupleSerializeRoutine(TupleTypeInfo newType, List<TypeInfo> elementTypes)
    {
        TypeInfo? serialValueType = LookupType(name: "SerialValue");
        if (serialValueType != null &&
            elementTypes.All(predicate: et =>
                et is not GenericParameterTypeInfo &&
                (et is RoutineTypeInfo || LookupMemberRoutine(type: et, memberRoutineName: RuntimeContract.Serialize) != null)))
        {
            RegisterSynthesizedMemberRoutine(name: RuntimeContract.Serialize, owner: newType, returnType: serialValueType);
        }
    }

    /// <summary>
    /// Auto-registers <c>store</c> (the retaining field-walk copy) iff every element is itself Assignable
    /// — a value / managed leaf / RC wrapper, never a bare <c>entity</c> or access token. Without it,
    /// <c>var u = t</c> on a tuple holding a managed leaf (Text) bitwise-aliases and double-frees at the
    /// two scopes' teardown. Generic-parameter elements are not Assignable here, so store is registered
    /// per CONCRETE instantiation (like serialize).
    /// </summary>
    private void RegisterTupleStoreRoutine(TupleTypeInfo newType, List<TypeInfo> elementTypes)
    {
        if (elementTypes.All(predicate: et =>
                CanAutoDeriveAssignable(type: et)
                || DoesTypeObeyProtocol(type: et, protocolName: "Assignable")
                || LookupMemberRoutine(type: et, memberRoutineName: "assign") is not null))
        {
            RegisterSynthesizedMemberRoutine(name: "assign", owner: newType, returnType: newType);
        }
    }

    /// <summary>Auto-registers <c>cmp</c> plus the derived <c>lt/le/gt/ge</c> operators if ALL element
    /// types support cmp.</summary>
    private void RegisterTupleComparisonRoutines(TupleTypeInfo newType, List<TypeInfo> elementTypes)
    {
        TypeInfo? boolType = LookupType(name: "Bool");
        TypeInfo? comparisonSignType = LookupType(name: "ComparisonSign");
        if (boolType != null && comparisonSignType != null &&
            elementTypes.All(predicate: et => LookupMemberRoutine(type: et, memberRoutineName: "cmp") != null))
        {
            var youParam = new ParameterInfo(name: "you", type: newType);
            RegisterSynthesizedMemberRoutine(name: "cmp", owner: newType, returnType: comparisonSignType, parameters: [youParam]);

            // Derived: lt, le, gt, ge
            foreach (string opName in new[]
                     {
                         "lt",
                         "le",
                         "gt",
                         "ge"
                     })
            {
                RegisterSynthesizedMemberRoutine(name: opName, owner: newType, returnType: boolType, parameters: [youParam]);
            }
        }
    }

    /// <summary>
    /// Gets or creates a synthesized wrapper type (Modifying, Consulting, Amending, Viewing).
    /// These are builder-intrinsic types that don't need to be defined in the program.
    /// </summary>
    /// <param name="wrapperName">The name of the wrapper type (e.g., "Modifying").</param>
    /// <param name="innerType">The type being wrapped.</param>
    /// <param name="isReadOnly">Whether this is a read-only wrapper (Viewing, Consulting).</param>
    /// <returns>The cached or newly created wrapper type.</returns>
    public WrapperTypeInfo GetOrCreateWrapperType(string wrapperName, TypeInfo innerType,
        bool isReadOnly)
    {
        // Build the cache key using the inner type's FullName for uniqueness.
        // Stored in _wrapperResolutions (not _resolutions) to avoid collisions with
        // GetOrCreateResolution's FullName-based keys for record types like Hijacked[Core.Byte].
        string key = $"{wrapperName}[{innerType.FullName}]";

        // Check cache
        if (_wrapperResolutions.TryGetValue(key: key, value: out WrapperTypeInfo? wrapperType))
        {
            if (!_stdlibAnalysisActive && wrapperType.IsStdlibLazy)
                wrapperType.IsStdlibLazy = false;
            return wrapperType;
        }

        // Create and cache — all wrapper types live in Core
        var newType = new WrapperTypeInfo(wrapperName: wrapperName,
            innerType: innerType,
            isReadOnly: isReadOnly) { Module = "Core" };
        _wrapperResolutions[key: key] = newType;

        if (_stdlibAnalysisActive)
            newType.IsStdlibLazy = true;

        return newType;
    }

    /// <summary>
    /// Determines if a type is a value type (has copy semantics).
    /// Value types include: Record, Choice, Tuple, and Variant.
    /// </summary>
    /// <param name="type">The type to check.</param>
    /// <returns>True if the type is a value type, false otherwise.</returns>
    public static bool IsValueType(TypeInfo type)
    {
        return type.Category switch
        {
            TypeCategory.Record => true,
            TypeCategory.Choice => true,
            TypeCategory.Variant => true, // Variants are value types (stack-allocated)
            _ => false
        };
    }

    /// <summary>
    /// Gets all types of a specific category.
    /// </summary>
    /// <param name="category">The category of types to retrieve.</param>
    /// <returns>An enumerable of all types in the specified category.</returns>
    public IEnumerable<TypeInfo> GetTypesByCategory(TypeCategory category)
    {
        return _types.Values
                     .Concat(second: _resolutions.Values)
                     .Where(predicate: t => t.Category == category);
    }

    /// <summary>
    /// Clears the "created during stdlib analysis, defer until user code needs it" (<see
    /// cref="TypeInfo.IsStdlibLazy"/>) flag on EVERY concrete instance, so all of them flow through
    /// monomorphization + derive synthesis. Used when building a precompiled stdlib base (empty entry
    /// program), where nothing user-side references these instances yet but the base must still define them.
    /// Returns how many were materialized. Normal builds never call this.
    /// </summary>
    public int MaterializeAllLazyStdlibTypes()
    {
        int n = 0;
        foreach (TypeInfo t in _resolutions.Values.Distinct().ToList())
            if (t.IsStdlibLazy)
            {
                t.IsStdlibLazy = false;
                n++;
            }
        return n;
    }

    /// <summary>
    /// WARM-RESTORE: re-marks every concrete generic/wrapper instance as <see cref="TypeInfo.IsStdlibLazy"/>.
    /// The whole-stdlib snapshot capture UN-lazied instances the stdlib's own bodies reference (Maybe/Result/
    /// Hijacked[X] for many X) — far more than any single user program uses (measured warm 638 vs cold 378
    /// processed, only 122 live). Re-lazying on restore makes a warm compile re-discover from the USER
    /// program's reachability (via <see cref="MaterializeIfLazy"/>), like a cold compile does, instead of
    /// GMP re-processing the whole primed closure every run. Returns how many were re-lazied.
    /// </summary>
    public int RelazyStdlibConcreteInstances()
    {
        int n = 0;
        void Relazy(TypeInfo t)
        {
            if (!t.IsStdlibLazy && t.TypeArguments is { Count: > 0 } && IsFullyConcrete(t))
            {
                t.IsStdlibLazy = true;
                n++;
            }
        }
        foreach (TypeInfo t in _resolutions.Values.Distinct().ToList()) Relazy(t);
        foreach (TypeInfo t in _wrapperResolutions.Values.Distinct().ToList()) Relazy(t);
        return n;
    }

    /// <summary>
    /// All concrete (non-generic-definition) entity and record instances created during semantic analysis,
    /// filtered to live (reachable) types and excluding stdlib-lazy deferred instances. Examples: <c>List[S64]</c>,
    /// <c>Maybe[Text]</c>. Used by <c>GenericMonomorphizationPass</c> to enumerate which member-routine bodies
    /// need rewriting. Deduplicated by reference because the dual-index scheme stores the same <see cref="TypeInfo"/>
    /// under both the full and short keys.
    /// </summary>
    public IEnumerable<TypeInfo> AllConcreteGenericInstances =>
        _resolutions.Values
                    .Where(predicate: t =>
                         t is EntityTypeInfo or RecordTypeInfo && t is { IsGenericDefinition: false, TypeArguments: { Count: > 0 } args } && args.All(predicate: IsFullyConcrete) &&
                         IsConcreteTypeLive(t) && !t.IsStdlibLazy)
                    .Distinct(); // dual-index stores the same TypeInfo under two keys; deduplicate by reference.

    /// <summary>
    /// All concrete generic instances, bypassing the liveness filter.
    /// Used by GMP's fixed-point loop to discover types that were registered during
    /// monomorphization itself (e.g. ListEmitter[Byte] discovered while rewriting List[Byte].iter).
    /// Wrapper types are excluded to prevent runaway growth for self-wrapping families.
    /// </summary>
    public IEnumerable<TypeInfo> AllConcreteGenericInstancesUnfiltered =>
        _resolutions.Values
                    .Where(predicate: t =>
                         t is EntityTypeInfo or RecordTypeInfo && t is { IsGenericDefinition: false, TypeArguments: { Count: > 0 } args } && args.All(predicate: IsFullyConcrete) &&
                         !t.IsStdlibLazy)
                    .Distinct();

    /// <summary>
    /// Returns true when a type argument is fully concrete — no GenericParameterTypeInfo,
    /// ErrorTypeInfo, or None at any nesting depth.
    /// </summary>
    private static bool IsFullyConcrete(TypeInfo t)
    {
        // An unresolved associated-type projection (`S/Iter`) or protocol self (`Me`/ProtocolSelf)
        // is NOT concrete — both must be resolved to a concrete type during monomorphization before
        // they can be instantiated/codegen'd.
        if (t is GenericParameterTypeInfo or ErrorTypeInfo or AssociatedProjectionTypeInfo
            or ProtocolSelfTypeInfo)
            return false;
        if (t.IsNone) return false;
        // A generic definition has free type parameters (no TypeArguments, only GenericParameters).
        // Wrapper instances like Hijacked[BTreeDictNode[K,V]] must not be treated as fully concrete
        // since they still reference unresolved type params via the generic-def inner type.
        if (t.IsGenericDefinition) return false;
        if (t.TypeArguments is not { Count: > 0 } args) return true;
        return args.All(predicate: IsFullyConcrete);
    }

    /// <summary>
    /// Returns all concrete WrapperTypeInfo instances (e.g. Hijacked[RetainController])
    /// whose type argument is fully resolved (no generic parameters or error types).
    /// Used by eager wrapper-forwarder synthesis.
    /// </summary>
    public IEnumerable<WrapperTypeInfo> AllConcreteWrapperInstances =>
        _wrapperResolutions.Values
                           .Where(predicate: t =>
                                t.TypeArguments is { Count: > 0 } args && args.All(predicate: IsFullyConcrete) &&
                                IsConcreteTypeLive(t) && !t.IsStdlibLazy)
                           .Distinct();

    /// <summary>
    /// All concrete wrapper instances bypassing the liveness filter. Mirror of
    /// <see cref="AllConcreteGenericInstancesUnfiltered"/> for wrapper types — used by GMP to
    /// monomorphize memberRoutines on wrappers like <c>Hijacked[Text]</c> that were created
    /// during stdlib analysis but never reached the liveness walk (e.g. as a field type of an
    /// iterator entity referenced indirectly via represent/diagnose).
    /// </summary>
    public IEnumerable<WrapperTypeInfo> AllConcreteWrapperInstancesUnfiltered =>
        _wrapperResolutions.Values
                           .Where(predicate: t =>
                                t.TypeArguments is { Count: > 0 } args && args.All(predicate: IsFullyConcrete) &&
                                !t.IsStdlibLazy)
                           .Distinct();

    /// <summary>
    /// Gets all types that can have memberRoutines (records, entities, choices, flags).
    /// </summary>
    /// <returns>An enumerable of all types that can have memberRoutines.</returns>
    public IEnumerable<TypeInfo> GetTypesWithMemberRoutines()
    {
        IEnumerable<TypeInfo> namedTypes = _types.Values.Where(predicate: t =>
            t.Category is TypeCategory.Record or TypeCategory.Entity or TypeCategory.Choice
                or TypeCategory.Flags or TypeCategory.Crashable or TypeCategory.Variant);

        // Include tuple types from resolutions cache
        IEnumerable<TypeInfo> tupleTypes =
            _resolutions.Values.Where(predicate: t => t is TupleTypeInfo);

        // Materialize: callers (e.g. WiredRoutinePass wired-body synthesis) register new resolutions
        // WHILE iterating this, which would invalidate a lazy enumerator over the live `_types`/
        // `_resolutions` dictionaries ("Collection was modified"). A snapshot is the current type set.
        return namedTypes.Concat(second: tupleTypes).ToList();
    }

    /// <summary>Concrete routine types from the resolutions cache (structural — never in <c>_types</c>).
    /// WiredRoutinePass iterates these to synthesize their wired bodies (serialize) regardless of the
    /// GetAllRoutines liveness filter, mirroring the tuple path.</summary>
    public IEnumerable<TypeInfo> GetResolvedRoutineTypes()
        // Materialized: like GetTypesWithMemberRoutines, wired-body synthesis registers resolutions while
        // iterating this, which would invalidate a lazy enumerator over the live `_resolutions`.
        => _resolutions.Values.Where(predicate: t => t is RoutineTypeInfo { IsGenericDefinition: false })
            .ToList();

    /// <summary>
    /// Gets all registered types.
    /// </summary>
    /// <returns>An enumerable of all types.</returns>
    public IEnumerable<TypeInfo> GetAllTypes()
    {
        return _types.Values;
    }

    /// <summary>
    /// Returns all concrete (non-generic-definition) types that implement the given protocol.
    /// For generic implementing types, resolves the concrete type against the protocol's type arguments.
    /// This is the authoritative implementer list; codegen should read this instead of scanning all types.
    /// </summary>
    public List<TypeInfo> GetProtocolImplementors(ProtocolTypeInfo protocol)
    {
        ProtocolTypeInfo protocolDef = protocol.GenericDefinition ?? protocol;
        string protocolBaseName = protocolDef.Name;

        var result = new List<TypeInfo>();
        var seen = new HashSet<string>();

        IEnumerable<TypeInfo> candidates = GetTypesByCategory(category: TypeCategory.Entity)
            .Concat(second: GetTypesByCategory(category: TypeCategory.Record))
            .Concat(second: GetTypesByCategory(category: TypeCategory.Crashable));

        foreach (TypeInfo type in candidates)
        {
            if (type.IsGenericDefinition && protocol.TypeArguments == null) continue;
            if (!seen.Add(item: type.Name)) continue;

            List<TypeInfo>? implemented = GetImplementedProtocols(type);
            if (implemented == null) continue;

            TryAddImplementor(protocol: protocol, protocolBaseName: protocolBaseName,
                type: type, implemented: implemented, result: result);
        }

        return result;
    }

    // Returns the implemented-protocols list for an entity or record; null for other type kinds.
    private static List<TypeInfo>? GetImplementedProtocols(TypeInfo type) => type switch
    {
        EntityTypeInfo e => e.ImplementedProtocols,
        RecordTypeInfo r => r.ImplementedProtocols,
        _ => null
    };

    // Scans the implemented-protocol list of a candidate type and adds a matching implementor
    // (concrete, generic-bound, or bare) to result when the protocol base name matches.
    private void TryAddImplementor(ProtocolTypeInfo protocol, string protocolBaseName,
        TypeInfo type, List<TypeInfo> implemented, List<TypeInfo> result)
    {
        foreach (TypeInfo impl in implemented)
        {
            string implBaseName = (impl as ProtocolTypeInfo)?.GenericDefinition?.Name ?? impl.Name;
            if (implBaseName != protocolBaseName) continue;

            TypeInfo? toAdd = ResolveImplementorMatch(protocol: protocol, type: type, impl: impl);
            if (toAdd != null)
            {
                result.Add(item: toAdd);
                break;
            }
        }
    }

    // Resolves which concrete type to add for a protocol-base-name match: null when the match
    // fails type-argument or binding checks (caller should skip this impl and continue).
    private TypeInfo? ResolveImplementorMatch(ProtocolTypeInfo protocol, TypeInfo type, TypeInfo impl)
    {
        if (!type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 }
            && impl.TypeArguments is { Count: > 0 })
        {
            return ProtocolTypeArgsMatch(protocol: protocol, impl: impl) ? type : null;
        }

        if (type.IsGenericDefinition && protocol.TypeArguments is { Count: > 0 })
        {
            return TryBindGenericImplementor(protocol: protocol, type: type, impl: impl);
        }

        return type;
    }

    /// <summary>
    /// True when a concrete implementer's <c>obeys Proto[...]</c> type arguments match the queried
    /// protocol's type arguments positionally by fully-qualified name (equal arity required).
    /// </summary>
    private static bool ProtocolTypeArgsMatch(ProtocolTypeInfo protocol, TypeInfo impl)
    {
        if (protocol.TypeArguments!.Count != impl.TypeArguments!.Count)
        {
            return false;
        }

        for (int i = 0; i < protocol.TypeArguments.Count; i++)
        {
            if (protocol.TypeArguments[index: i].FullName !=
                impl.TypeArguments[index: i].FullName)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Attempts to synthesize a concrete obeyer instance of a GENERIC implementing <paramref name="type"/>
    /// by binding each of its own generic parameters from the queried protocol's concrete arguments (in
    /// the obeyer's <c>obeys Proto[...]</c> slots). Returns null when not every obeyer parameter is
    /// bindable — e.g. <c>entity BitArrayIterator[N] obeys Iterator[Bool]</c> leaves N unbound, so the
    /// obeyer surfaces only via real <c>BitArrayIterator[8]</c>-style instantiations elsewhere.
    /// </summary>
    private TypeInfo? TryBindGenericImplementor(ProtocolTypeInfo protocol, TypeInfo type, TypeInfo impl)
    {
        ProtocolTypeInfo protoDef2 = protocol.GenericDefinition ?? protocol;
        if (protoDef2.GenericParameters is not { Count: > 0 } ||
            type.GenericParameters is not { Count: > 0 } ||
            impl.TypeArguments is not { Count: > 0 })
        {
            return null;
        }

        // Walk the obeyer's `obeys Proto[...]` slots: wherever the obeyer wrote its
        // own generic parameter (e.g. `obeys Iterator[T]` with obeyer param `T`),
        // bind that obeyer-param to the protocol's concrete arg in the same slot.
        // Concrete entries in impl.TypeArguments (e.g. `Bool`) contribute no binding.
        var obeyerBindings = new Dictionary<string, TypeInfo>();
        int slots = Math.Min(val1: impl.TypeArguments.Count,
            val2: protocol.TypeArguments!.Count);
        for (int slot = 0; slot < slots; slot++)
        {
            if (impl.TypeArguments[index: slot] is GenericParameterTypeInfo gp &&
                type.GenericParameters.Contains(item: gp.Name))
            {
                obeyerBindings[key: gp.Name] = protocol.TypeArguments[index: slot];
            }
        }

        if (obeyerBindings.Count != type.GenericParameters.Count)
        {
            return null;
        }

        var typeArgs = type.GenericParameters
                           .Select(selector: p => obeyerBindings[key: p])
                           .ToList();

        return GetOrCreateResolution(genericDef: type, typeArguments: typeArgs);
    }

    #endregion

    // Routine registration and lookup memberRoutines are in TypeRegistry.MemberRoutineLookup.cs

    #region Scope Management

    /// <summary>
    /// Gets the current scope.
    /// </summary>
    public Scope CurrentScope => _currentScope;

    /// <summary>
    /// Enters a new child scope.
    /// </summary>
    /// <param name="kind">The kind of scope to enter.</param>
    /// <param name="name">Optional name for the scope.</param>
    /// <returns>The newly created child scope.</returns>
    public Scope EnterScope(ScopeKind kind, string? name = null)
    {
        _currentScope = _currentScope.CreateChildScope(kind: kind, name: name);
        return _currentScope;
    }

    /// <summary>
    /// Exits the current scope and returns to the parent.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if attempting to exit the global scope.</exception>
    public void ExitScope()
    {
        _currentScope = _currentScope.Parent ??
                        throw new InvalidOperationException(
                            message: "Cannot exit the global scope.");
    }

    /// <summary>
    /// Declares a variable in the current scope.
    /// </summary>
    /// <param name="name">The name of the variable.</param>
    /// <param name="type">The type of the variable.</param>
    /// <param name="isPreset">Whether this is a preset (build-time constant).</param>
    /// <param name="presetValue">The preset value.</param>
    /// <param name="isNullable">Whether the variable is nullable.</param>
    /// <param name="isGlobal">Whether this is a Suflae module-level <c>global</c> variable (mutable,
    /// session-lifetime). When true the declared variable carries <see cref="VariableInfo.IsGlobal"/>
    /// so identifier resolution can stamp references with <c>IsModuleGlobal</c>.</param>
    /// <param name="location">Source location for error reporting; stored on the variable for
    /// diagnostics that need to point back to the declaration site.</param>
    /// <returns>True if successful, false if already declared in this scope.</returns>
    public bool DeclareVariable(string name, TypeInfo type, bool isPreset = false,
        Expression? presetValue = null, bool isNullable = false, bool isGlobal = false,
        SourceLocation? location = null)
    {
        var variable = new VariableInfo(name: name, type: type)
        {
            IsModifiable = !isPreset, IsPreset = isPreset, PresetValue = presetValue,
            IsNullable = isNullable, IsGlobal = isGlobal, Location = location
        };

        return _currentScope.DeclareVariable(variable: variable);
    }

    /// <summary>
    /// Registers a module-level preset constant (from StdlibLoader).
    /// Presets registered here are accessible across files within the same module.
    /// </summary>
    /// <param name="name">The preset name.</param>
    /// <param name="type">The type of the preset.</param>
    /// <param name="module">The module this preset belongs to.</param>
    /// <param name="value">The value.</param>
    /// <param name="isSecret">Whether the preset is file-private (secret).</param>
    public void RegisterPreset(string name, TypeInfo type, string? module = null,
        Expression? value = null, bool isSecret = false)
    {
        var variable = new VariableInfo(name: name, type: type)
        {
            IsModifiable = false, IsPreset = true, IsSecret = isSecret, Module = module,
            PresetValue = value
        };

        _presets[key: name] = variable;

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = variable.QualifiedName;
        if (qualifiedName != name)
        {
            _presetsByQualifiedName.TryAdd(key: qualifiedName, value: variable);
        }
    }

    /// <summary>
    /// Looks up a variable by name in the current scope chain,
    /// falling back to module-level presets if not found in local scopes.
    /// </summary>
    /// <param name="name">The name of the variable to look up.</param>
    /// <returns>The variable info if found, null otherwise.</returns>
    public VariableInfo? LookupVariable(string name)
    {
        return _currentScope.LookupVariable(name: name) ?? _presets.GetValueOrDefault(key: name) ??
            _presetsByQualifiedName.GetValueOrDefault(key: name) ??
            _globals.GetValueOrDefault(key: name) ??
            _globalsByQualifiedName.GetValueOrDefault(key: name);
    }

    /// <summary>
    /// Registers a Suflae module-level <c>global</c> variable — mutable, session-lifetime, accessible
    /// across files within the same module. Mirrors <see cref="RegisterPreset"/> but the global is
    /// modifiable and NOT a preset (so it is not inlined; codegen gives it real <c>@global</c> storage).
    /// </summary>
    public void RegisterGlobal(string name, TypeInfo type, string? module = null, bool isSecret = false)
    {
        var variable = new VariableInfo(name: name, type: type)
        {
            IsModifiable = true, IsGlobal = true, IsSecret = isSecret, Module = module
        };

        _globals[key: name] = variable;

        string qualifiedName = variable.QualifiedName;
        if (qualifiedName != name)
        {
            _globalsByQualifiedName.TryAdd(key: qualifiedName, value: variable);
        }
    }

    /// <summary>
    /// Looks up a preset by its module-qualified name (e.g., "Core.S8_MIN").
    /// </summary>
    public VariableInfo? LookupPresetByQualifiedName(string qualifiedName)
    {
        return _presetsByQualifiedName.GetValueOrDefault(key: qualifiedName);
    }

    /// <summary>
    /// Narrows the type of a variable in the current scope.
    /// Used for type narrowing after pattern checks (e.g., after "unless x is None").
    /// </summary>
    /// <param name="name">The variable name to narrow.</param>
    /// <param name="narrowedType">The narrowed type.</param>
    public void NarrowVariable(string name, TypeInfo narrowedType)
    {
        _currentScope.NarrowVariable(name: name, narrowedType: narrowedType);
    }

    /// <summary>
    /// Gets the narrowed type for a variable in the current scope chain.
    /// </summary>
    /// <param name="name">The variable name to look up.</param>
    /// <returns>The narrowed type if found, null otherwise.</returns>
    public TypeInfo? GetNarrowedType(string name)
    {
        return _currentScope.GetNarrowedType(name: name);
    }

    /// <summary>Records a variant arm (by full type name) as excluded for a variable in the current
    /// scope; accumulates down an if/elseif chain until a single arm remains.</summary>
    public void ExcludeVariantArm(string name, string armFullName)
    {
        _currentScope.ExcludeArm(name: name, armFullName: armFullName);
    }

    /// <summary>Gets the variant arms excluded for a variable in the current scope chain.</summary>
    public IReadOnlyCollection<string> GetExcludedVariantArms(string name)
    {
        return _currentScope.GetExcludedArms(name: name);
    }

    /// <summary>Suflae flow typing: marks a nullable entity reference proven non-none in the current scope.</summary>
    public void MarkVariableNonNull(string name)
    {
        _currentScope.MarkNonNull(name: name);
    }

    /// <summary>Suflae flow typing: records a variable as known-nullable-again in the current scope
    /// (shadows an outer proven-non-none fact — e.g. after reassigning a possibly-none value).</summary>
    public void MarkVariableNullableAgain(string name)
    {
        _currentScope.MarkNullableAgain(name: name);
    }

    /// <summary>Suflae flow typing: true if the variable was proven non-none in the current scope chain.</summary>
    public bool IsVariableProvenNonNull(string name)
    {
        return _currentScope.IsProvenNonNull(name: name);
    }

    /// <summary>
    /// Gets all variables visible in the current scope as a dictionary.
    /// Used for lambda capture analysis to track which variables from enclosing scopes are captured.
    /// </summary>
    /// <returns>A dictionary of variable names to their info.</returns>
    public IReadOnlyDictionary<string, VariableInfo> GetAllVariablesInScope()
    {
        var variables = new Dictionary<string, VariableInfo>();

        foreach (VariableInfo variable in _currentScope.GetAllVisibleVariables())
        {
            variables.TryAdd(key: variable.Name, value: variable);
        }

        return variables;
    }

    /// <summary>
    /// Gets variables from local (function-level) scopes only, stopping at Global/Module/Type boundaries.
    /// Variables from these scopes are truly "captured" by lambdas and require 'given' declarations.
    /// </summary>
    public IReadOnlyDictionary<string, VariableInfo> GetLocalScopeVariables()
    {
        var variables = new Dictionary<string, VariableInfo>();

        Scope? current = _currentScope;
        while (current != null)
        {
            // Stop at non-local scope boundaries
            if (current.Kind is ScopeKind.Global or ScopeKind.Module or ScopeKind.Type)
            {
                break;
            }

            foreach (VariableInfo variable in current.GetLocalVariables())
            {
                variables.TryAdd(key: variable.Name, value: variable);
            }

            current = current.Parent;
        }

        return variables;
    }

    #endregion
}
