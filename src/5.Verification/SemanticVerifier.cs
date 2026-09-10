using System.Diagnostics;
using Compiler.Desugaring;
using Compiler.Lowering;
using Compiler.Lowering.Passes;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Desugaring.Passes;
using Compiler.Declaration;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;
using Compiler.Verification.Results;
using Compiler.Verification.Scopes;
using Compiler.Collection.Passes;
using Compiler.LlvmEmit;
using Compiler.Collection;
using Compiler.Tokenizer;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Semantic analyzer for RazorForge and Suflae programs.
/// Performs type checking, scope analysis, and inference for:
/// - memberRoutine modification (readonly/writable/reshaping)
/// - Reshaping modification tracking (buffer relocation detection)
/// - Error handling variant generation (try_/check_/lookup_)
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Fields

    /// <summary>The type registry for storing and looking up types.</summary>
    internal readonly TypeRegistry _registry;

    /// <summary>Errors collected during analysis (insertion order preserved; deduplicated).</summary>
    private readonly List<SemanticError> _errors = [];

    /// <summary>Warnings collected during analysis (insertion order preserved; deduplicated).</summary>
    private readonly List<SemanticWarning> _warnings = [];

    // The analyzer runs several passes and re-resolves expressions (post type/protocol
    // registration, monomorphization, etc.), so the SAME diagnostic can be produced more than
    // once for one source location. These sets dedup by value — SemanticError/SemanticWarning are
    // records whose equality is (Code, Message, Location) — so a re-reported diagnostic is dropped
    // while the List keeps first-seen order. All add-sites route through AddError/AddWarning.
    private readonly HashSet<SemanticError> _seenErrors = [];
    private readonly HashSet<SemanticWarning> _seenWarnings = [];

    /// <summary>Adds an error unless an identical one (same code/message/location) was already recorded.</summary>
    private void AddError(SemanticError error)
    {
        if (_seenErrors.Add(item: error))
        {
            _errors.Add(item: error);
        }
    }

    /// <summary>Adds a warning unless an identical one (same code/message/location) was already recorded.</summary>
    private void AddWarning(SemanticWarning warning)
    {
        if (_seenWarnings.Add(item: warning))
        {
            _warnings.Add(item: warning);
        }
    }

    /// <summary>
    /// Warnings visible to a user build: stdlib-internal warnings are excluded (surface them with
    /// the <c>validate-stdlib</c> verb instead). EVERY AnalysisResult must use this — passing the
    /// raw <c>_warnings</c> list leaks stdlib style warnings (e.g. RF-W258) into user output.
    /// </summary>
    private List<SemanticWarning> UserVisibleWarnings()
    {
        return _warnings.Where(predicate: w =>
                             !string.IsNullOrEmpty(value: w.Location.FileName) &&
                             !IsStdlibFile(filePath: w.Location.FileName))
                        .ToList();
    }

    /// <summary>
    /// Parsed literal values for types requiring native library parsing.
    /// Keyed by source location for code generator lookup.
    /// </summary>
    private readonly Dictionary<SourceLocation, ParsedLiteral> _parsedLiterals = new();

    /// <summary>Current function being analyzed (for return type checking).</summary>
    internal RoutineInfo? _currentRoutine;

    /// <summary>Current type being analyzed (for me reference resolution).</summary>
    internal TypeSymbol? _currentType;

    /// <summary>Danger block nesting depth (0 = not in danger block, >0 = inside danger block).</summary>
    private int _dangerBlockDepth;

    /// <summary>Gets whether we're currently inside a danger block.</summary>
    private bool InDangerBlock => _dangerBlockDepth > 0;

    /// <summary>True while analyzing a compiler-generated body (variant or synthesized derived operator).
    /// Suppresses the wired-routine direct-call check so SA can fully annotate ResolvedType on
    /// all nodes -> errors are already discarded by AnalyzeCompilerGeneratedBody's error-count guard.</summary>
    internal bool _isInCompilerGeneratedBody;

    /// <summary>True while analyzing a synthesized derived-operator body (DerivedOperatorPass output).
    /// Instructs AnalyzeExpression to skip re-analysis of nodes that already have ResolvedType set,
    /// preserving the pre-annotations applied by DerivedOperatorPass.</summary>
    internal bool _preservePresetTypes;

    /// <summary>Member variable names seen in the current type during body resolution (for duplicate detection).</summary>
    internal HashSet<string>? _currentTypeMemberVariableNames;

    /// <summary>The source file path of the program being analyzed (for import resolution).</summary>
    internal string _currentFilePath = string.Empty;

    /// <summary>The module name declared in the current file (from 'module' declaration).</summary>
    internal string? _currentModuleName;

    /// <summary>Modules imported by the current file. Used for type resolution of non-Core types.</summary>
    internal readonly HashSet<string> _importedModules =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Tracks imported symbol names for collision detection (#105).</summary>
    internal readonly HashSet<string> _importedSymbolNames = new(comparer: StringComparer.Ordinal);

    /// <summary>Foreign routines the current file imported into BARE scope via `import Module.C::name`
    /// (or `LLVM::name`). Keyed <c>"REALM::name"</c> (e.g. <c>"C::qsort"</c>). A bare call resolving to
    /// such a foreign routine skips the usual <c>C::</c>/<c>LLVM::</c> call-site qualifier requirement.</summary>
    internal readonly HashSet<string> _importedForeignAliases =
        new(comparer: StringComparer.Ordinal);

    /// <summary>Per-file import snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, HashSet<string>> _importSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file imported symbol snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, HashSet<string>> _symbolNameSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file module-name snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, string?> _moduleNameSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file <see cref="_importedForeignAliases"/> snapshots (realm-qualified bare imports),
    /// so a bare-aliased foreign call inside a re-analyzed compiler-generated body (e.g. a failable
    /// user routine's `try_` variant) keeps passing the realm gate.</summary>
    private readonly Dictionary<string, HashSet<string>> _foreignAliasSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Nesting depth for conditional expressions (for #145 deep nesting warning).</summary>
    private int _conditionalNestingDepth;

    /// <summary>
    /// When statements determined to be exhaustive (either via catch-all or full type coverage).
    /// Consulted by control-flow termination analysis so that an exhaustive `when` whose every
    /// arm terminates also terminates.
    /// </summary>
    private readonly HashSet<WhenStatement> _exhaustiveWhens = [];

    /// <summary>Tracks Lookup variables that must be dismantled before scope exit (#161).</summary>
    private readonly List<(string Name, SourceLocation Location)> _pendingLookupVars = [];

    /// <summary>Tracks variables invalidated by steal/ownership transfer (#11).</summary>
    private readonly HashSet<string> _deadrefVariables = [];

    /// <summary>Flags-context stack: while a flags type is on top, bare identifiers are resolved
    /// against the flag members of that type.</summary>
    private readonly Stack<TypeSymbol> _flagsContextStack = new();

    /// <summary>Tracks the current for-loop iteration variable names for reshaping check (#22).</summary>
    private readonly HashSet<string> _activeIterationSources = [];

    /// <summary>Routine declarations collected in Phase 3/4, pending resolution and registration in Phase 4.1.</summary>
    internal readonly List<PendingRoutine> _pendingRoutines = [];

    /// <summary>The resource expression currently being analyzed as a `using` target, if any. A
    /// multi-threaded access token (Consulting/Amending) is only legal in this exact position —
    /// any other use is rejected (RF-S629) so its lock is always `using`-scoped.</summary>
    private ISyntaxTreeNode? _usingResourceNode;

    /// <summary>Stack of MT access holds (`consult`/`amend`) live in the enclosing `using` scopes.
    /// Each hold records the syntactic handle path AND the controller-identity it resolves to (see
    /// <see cref="_sharedHandleIdentity"/>), so aliased handles (`s2 = s.share()`) conflict even
    /// though their names differ. Pushed on `using` entry, popped on exit, so a nested `using` sees
    /// the holds it overlaps — the basis of the readers-XOR-writer check (RF-S630).</summary>
    private readonly List<(string Handle, int Identity, bool IsWriter, SourceLocation Location)>
        _activeAccessHolds = [];

    /// <summary>Maps a Guarded/Witnessed handle path (`s`, `s.a`) to the identity of the controller
    /// (the atomic Arc cell) it refers to. A fresh `T.share[P]()` mints a new identity;
    /// `.share()`/`.watch()` clones and plain copies INHERIT the source handle's identity, so all
    /// handles to one controller share an identity. Lets the readers-XOR-writer check key on the
    /// shared DATA rather than the variable name. Paths never bound to a tracked handle are
    /// lazily assigned a unique identity on first use (degrades to per-path = the old behaviour).</summary>
    private readonly Dictionary<string, int> _sharedHandleIdentity =
        new(comparer: StringComparer.Ordinal);

    /// <summary>Monotonic source of fresh controller identities for <see cref="_sharedHandleIdentity"/>.</summary>
    private int _nextSharedHandleIdentity;

    /// <summary>Tracks (TypeName, ProtocolName) pairs added by implicit marker conformance, excluded from validation.</summary>
    internal readonly HashSet<(string TypeName, string ProtocolName)>
        _implicitProtocolConformances = [];

    /// <summary>
    /// AST bodies synthesized for derived operators (ne, lt, le, gt, ge, notcontains).
    /// Keyed by RoutineInfo.RegistryKey. Analyzed in Phase 4 via AnalyzeSynthesizedBodies().
    /// </summary>
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies =
        new();

    /// <summary>Handles resolution of type expressions (TypeResolution logic).</summary>
    internal TypeResolver _typeResolver;

    /// <summary>Handles resolution of type bodies (member variables, protocol conformances, etc.).</summary>
    internal TypeBodyResolver _typeBodyResolver;

    /// <summary>Handles resolution and registration of routine signatures.</summary>
    internal SignatureResolver _signatureResolver;

    /// <summary>Handles implicit marker protocol conformance application.</summary>
    internal ProtocolConformanceAnalyzer _conformanceAnalyzer;

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines (try_/check_/lookup_), produced
    /// by <see cref="ErrorHandlingVariantPass"/> during Phase 6 global desugaring.
    /// Merged into <c>SynthesizedBodies</c> when building the <see cref="AnalysisResult"/>.
    /// </summary>
    private Dictionary<string, Statement> _variantBodies = new();

    /// <summary>
    /// Concrete generic memberRoutine bodies produced by <see cref="GenericMonomorphizationPass"/>.
    /// Captured from <see cref="DesugaringContext.InstantiatedGenericBodies"/> in
    /// <see cref="RunPhase6GlobalDesugaring"/> and forwarded to <see cref="AnalysisResult"/>.
    /// </summary>
    private Dictionary<string, MonomorphizedBody> _instantiatedGenericBodies = new();

    /// <summary>
    /// Reachable routine keys produced by <c>RoutineReachabilityPass</c>.
    /// Captured from <see cref="InstantiationContext.LiveRoutineKeys"/> after Phase 7.
    /// </summary>
    private IReadOnlyCollection<string> _liveRoutineKeys = Array.Empty<string>();

    private IReadOnlyCollection<string> _liveOwnerTypeNames = Array.Empty<string>();

    /// <summary>
    /// May-suspend routine keys from <see cref="MaySuspendAnalysis"/>, captured from
    /// <see cref="InstantiationContext.MaySuspendRoutineKeys"/> after Phase 7. Drives 9-2
    /// cancellation instrumentation in codegen.
    /// </summary>
    private IReadOnlyCollection<string> _maySuspendRoutineKeys = Array.Empty<string>();

    #endregion

    #region Constructor

    /// <summary>
    /// Stores the target state used by this compiler phase.
    /// </summary>
    private readonly TargetConfig _target;

    /// <summary>
    /// Stores the build mode state used by this compiler phase.
    /// </summary>
    private readonly RfBuildMode _buildMode;

    /// <summary>
    /// True when this instance was constructed from a pre-analyzed stdlib snapshot.
    /// Causes Phase 3 to skip <c>PreRegisterStdlibVariants</c> (already registered in snapshot)
    /// and Phase 4 to skip <c>AnalyzeStdlibBodies</c> (already analyzed in snapshot).
    /// Only valid with <see cref="SaOnly"/> = true; the full pipeline re-runs stdlib lowering
    /// so it cannot safely reuse snapshot state.
    /// </summary>
    private readonly StdlibMemo _memo;

    /// <summary>
    /// Initializes a new instance of the <see cref="SemanticVerifier"/> class.
    /// </summary>
    /// <param name="language">The language being analyzed (RazorForge or Suflae).</param>
    /// <param name="stdlibPath">Optional path to the stdlib directory.</param>
    /// <param name="target">Target platform -> drives BuilderQuery platform constants. Defaults to host.</param>
    /// <param name="buildMode">Build mode -> drives BuilderQuery.build_mode. Defaults to Debug.</param>
    public SemanticVerifier(Language language, string? stdlibPath = null,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _registry = new TypeRegistry(language: language, stdlibPath: stdlibPath);
        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;
        _memo = StdlibMemo.Empty;
    }

    /// <summary>
    /// Constructs a <see cref="SemanticVerifier"/> pre-warmed from a stdlib snapshot.
    /// Stdlib loading, body analysis, and variant pre-registration are all skipped on the
    /// first <see cref="Analyze"/> call — use with <see cref="SaOnly"/> = true only.
    /// </summary>
    public SemanticVerifier(Language language, TypeRegistry.StdlibSnapshot snapshot,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _registry = new TypeRegistry(language: language, snapshot: snapshot);
        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;
        _memo = StdlibMemo.Empty with { IsWarm = true };
    }

    /// <summary>
    /// Captures a pre-analyzed stdlib snapshot for the given language.
    /// Runs a full SA initialization (including stdlib body analysis) on a minimal program,
    /// then returns the registry snapshot for fast-restore in subsequent test instances.
    /// </summary>
    public static TypeRegistry.StdlibSnapshot CaptureStdlibSnapshot(Language language)
    {
        var sa = new SemanticVerifier(language: language) { SaOnly = true };
        List<Token> tokens = new Compiler.Tokenizer.Tokenizer(source: "module __snapshot__",
            fileName: "__snapshot__",
            language: language).Tokenize();
        var parser = new Compiler.Parser.Parser(tokens: tokens,
            language: language,
            fileName: "__snapshot__");
        sa.Analyze(program: parser.Parse());
        return sa._registry.CaptureSnapshot();
    }

    /// <summary>
    /// When true, AnalyzeMultiple prints per-phase timings to stderr.
    /// Set from the manifest's <c>sa-timing</c> target field.
    /// </summary>
    public bool SaTiming { get; set; }

    /// <summary>
    /// When true, stops after Phase 5 (semantic analysis) and skips Phase 6 global
    /// desugaring, Phase 7 instantiation, Phase 8 postprocessing, and Phase 9 checks.
    /// Use for tests that only assert on SA errors or type annotations — saves ~10× time
    /// by avoiding monomorphization and lowering passes.
    /// </summary>
    public bool SaOnly { get; set; }

    /// <summary>
    /// When true, root EVERY concrete stdlib routine in reachability so monomorphization materializes the
    /// full stdlib generic closure — for emitting a precompiled stdlib base (see
    /// <see cref="Compiler.LlvmEmit.LlvmEmitter.GenerateBase"/>) that must define everything it
    /// references. Threaded into <see cref="InstantiationContext.SeedAllStdlibRoutines"/> and (as
    /// <see cref="Compiler.Desugaring.DesugaringContext.SynthesizeAllDerives"/>) the Phase-6 derive
    /// synthesis. Default false = normal builds (byte-identical).
    /// </summary>
    public bool SeedAllStdlibRoutines { get; set; }

    #endregion

    #region Public API

    /// <summary>
    /// Analyzes a complete program AST.
    /// </summary>
    /// <param name="program">The program to analyze.</param>
    /// <param name="filePath">Optional source file path for import resolution.</param>
    /// <returns>Analysis result containing errors, warnings, and the populated type registry.</returns>
    public AnalysisResult Analyze(Program program, string? filePath = null)
    {
        _importSnapshots.Clear();
        _symbolNameSnapshots.Clear();
        _moduleNameSnapshots.Clear();
        _foreignAliasSnapshots.Clear();
        _currentFilePath = filePath ?? program.Location.FileName;
        _currentModuleName = null;
        _importedModules.Clear();
        _importedSymbolNames.Clear();
        _importedForeignAliases.Clear();

        bool saTiming = SaTiming;
        var swPhase = Stopwatch.StartNew();

        void Mark(string label)
        {
            if (!saTiming)
            {
                return;
            }

            swPhase.Stop();
            Console.Error.WriteLine(value: $"{label}: {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        // Phase numbers now run in EXECUTION ORDER (1 = first to run), independent of the source folder
        // prefixes (01.Tokenizer…10.CodeGen) — the old scheme reused folder numbers, so the driver read
        // out of order (Phase 5 after 6/7). The pull/(B) pipeline in execution order:
        //   1 Declarations · 2 Resolution · 3 Stub synthesis · 4 Syntax prepass · 5 Semantic analysis ·
        //   6 Global desugaring · 7 Instantiation (reachability) · 8 Postprocessing lowering ·
        //   [demand collect+monomorphize] · 9 Post-desugar checks.
        // Stub synthesis (3) + syntax prepass (4) run BEFORE semantic analysis (5) so their stubs are in
        // scope. Under the demand flip, stdlib SA+desugaring is driven per-file by the collector, not eagerly.
        // Install the on-demand failable-variant synthesizer: any LookupMemberRoutine miss on a
        // try_/check_/lookup_ name now synthesizes that variant from the deferred base index (populated
        // by pre-registration) rather than relying on eager registration of every failable's variants.
        _registry.OnDemandVariantSynthesizer = TrySynthesizeVariantOnDemand;
        _registry.OnDemandVariantForBase = SynthesizeVariantForBase;
        RunPhase1Declarations(program: program);
        Mark(label: "Phase 1 Declarations");
        CaptureCurrentImportStateSnapshot(filePath: _currentFilePath);
        RunPhase2Resolution(program: program);
        Mark(label: "Phase 2 Resolution");
        RunPhase3StubSynthesis(program: program);
        Mark(label: "Phase 3 Stub synthesis");
        RunPhase4SyntaxPrepass(program: program);
        Mark(label: "Phase 4 Syntax prepass");
        RunPhase5SemanticAnalysis(program: program);
        Mark(label: "Phase 5 Semantic analysis");
        // Failability inference: recompute RoutineInfo.IsFailable from throw/absent + propagated
        // failable callees now that all bodies (incl. synthesized) are analyzed, BEFORE variant
        // generation and codegen key the failable-carrier ABI on it.
        InferFailableRoutines();
        Mark(label: "Failability inference");
        // Register user program before global desugaring so GenericMonomorphizationPass can
        // search user-program ASTs for generic routine bodies (like FindInStdlib does for stdlib).
        _registry.RegisterUserProgram(program: program,
            filePath: _currentFilePath,
            module: _currentModuleName ?? "");

        // Register auto-derive templates UNCONDITIONALLY (even under SaOnly): the warm-stdlib snapshot is
        // captured with SaOnly=true, before CollectStdlibBodiesForVariantGeneration would register them,
        // so a warm full-analyze needs the templates in the snapshot to clone per-type derive bodies
        // (e.g. CLong.destroy). Idempotent — CollectStdlibBodiesForVariantGeneration's re-scan no-ops.
        RegisterStdlibDeriveTemplates();

        if (!SaOnly)
        {
            CollectStdlibBodiesForVariantGeneration();
            Mark(label: "CollectStdlibBodies");
            // Skip in snapshot/warm mode: the derive-marker validation is a property of the STDLIB SOURCE,
            // already enforced in the cold capture run. The opt-in-derive marking (_optInDeriveMemberRoutines,
            // which excludes eq/cmp/assign/copy) is NOT part of the serialized snapshot, so re-running here
            // over the restored stdlib would false-positive on Array.assign/Dict.copy/etc.
            if (!_memo.IsWarm)
            {
                CheckOverridableDeriveMarkers();
            }

            RunPhase6GlobalDesugaring();
            Mark(label: "Phase 6 Global desugaring");
            RunPhase7Instantiation();
            Mark(label: "Phase 7 Instantiation");
            RunPhase8Postprocessing(program: program);
            Mark(label: "Phase 8 Postprocessing");
            // Stage ② of the pull architecture: after Phase 8 lowered subscript/operator calls to real
            // getitem/member CallExpressions, run the demand collector so it walks from the entry points and
            // monomorphizes exactly the referenced generic closure. The AnalyzeMultiple path does this after
            // its per-file Phase-9 loop; the single-program path (unit tests, `check`/`codegen` verbs) needs
            // the same call or InstantiatedGenericBodies stays empty of user generics (eager GMP is retired
            // for non-base builds, so the collector is the SOLE monomorphizer).
            RunShadowCollectorIfNeeded();
            RunPhase9PostDesugarChecks();
            Mark(label: "Phase 9 Post-desugar checks");
            FinalizeReturnTypes();
            Mark(label: "FinalizeReturnTypes");
        }

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized[key: key] = variantBody;
        }

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.ToList(),
            Warnings: UserVisibleWarnings(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized,
            InstantiatedGenericBodies: _instantiatedGenericBodies,
            LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames,
            MaySuspendRoutineKeys: _maySuspendRoutineKeys);
    }

    /// <summary>Phase 1: Collect all type shapes and routine stubs -> no names resolved.</summary>
    private void RunPhase1Declarations(Program program)
    {
        CollectDeclarations(program: program);
    }

    /// <summary>Phase 2: Resolve all bare names to qualified types.</summary>
    private void RunPhase2Resolution(Program program)
    {
        _typeBodyResolver.ResolveTypeBodies(program: program);
        _signatureResolver.ResolveAndRegisterPendingRoutines();
        _signatureResolver.ResolveExternalSignatures(program: program);
        // Reject self-containing value records BEFORE conformance analysis, which computes
        // LlvmType/SizeBytes and would otherwise stack-overflow on the cycle.
        if (!ValidateNoRecursiveValueRecords())
        {
            _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        }
    }

    /// <summary>
    /// Phase 3 (stub pre-pass): Generate synthesized wired routines and derived operators.
    /// Runs before Phase 5 body analysis so stubs are in scope.
    /// Structural routines (represent/hash/eq/diagnose) remain as IsSynthesized stubs.
    /// Derived operators (ne/lt/le/gt/ge/notcontains) have real AST bodies stored in _synthesizedBodies.
    /// </summary>
    private void RunPhase3StubSynthesis(Program program)
    {
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        // Phase 4.1 (signature resolution) and all member-routine registration are now complete; re-derive the
        // wired attribute the parser no longer sets from the surface `$` sigil.
        InferWiredMemberRoutines();
        ValidateProtocolImplementations();
        PreRegisterUserVariants(program: program);
        // Memo content: when the memo carries restored stdlib bodies, the stdlib failable variants were
        // already registered in the restored registry — re-registering is pure warm overhead. A cold
        // compile's memo has no restored bodies (WarmStdlibRoutineBodies == null) ⇒ it does the work.
        if (_memo.WarmStdlibRoutineBodies == null)
        {
            PreRegisterStdlibVariants();
        }
    }

    /// <summary>
    /// Phase 5: Type-annotate and verify all routine bodies.
    /// Runs after Phase 6/7 stub pre-passes because body analysis needs stubs in scope;
    /// runs before Phase 6 main pass because synthesis needs type-annotated AST.
    /// </summary>
    private void RunPhase5SemanticAnalysis(Program program)
    {
        AnalyzeBodies(program: program);
        AnalyzeSynthesizedBodies();
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors and warnings are suppressed from user-visible output -> use 'validate-stdlib' to surface them.
        // Snapshot mode: the RESTORED stdlib bodies were analyzed during snapshot capture; AnalyzeStdlibBodies
        // now iterates only the FRESHLY-loaded programs (cold: all; warm: on-demand imports), so it is a
        // no-op for the restored set and correctly analyzes modules the warm compile imported on-demand.
        // Base build (SeedAllStdlibRoutines) ALWAYS analyzes the whole stdlib eagerly — it must DEFINE every
        // routine for the precompiled base, which demand-from-`start` cannot (it only reaches what one entry
        // program uses). Only NORMAL builds take the demand path.
        if (SeedAllStdlibRoutines)
        {
            int errorsBeforeStdlib = _errors.Count;
            int warningsBeforeStdlib = _warnings.Count;
            AnalyzeStdlibBodies();
            if (_errors.Count > errorsBeforeStdlib)
            {
                _errors.RemoveRange(index: errorsBeforeStdlib,
                    count: _errors.Count - errorsBeforeStdlib);
            }

            if (_warnings.Count > warningsBeforeStdlib)
            {
                _warnings.RemoveRange(index: warningsBeforeStdlib,
                    count: _warnings.Count - warningsBeforeStdlib);
            }
        }

        EagerSynthesizeAllWrapperForwarders();
    }

    /// <summary>
    /// Phase 6 (global): Runs registry-wide synthesis once after all Phase 4 analysis.
    /// Generates error-handling variants, wired routine bodies, prunes unused generics,
    /// then applies Phase 3 passes to generated variant bodies and stdlib programs.
    /// Immediately followed by Phase 8 global: lowers variant bodies and stdlib with type-aware passes.
    /// </summary>
    private void RunPhase6GlobalDesugaring()
    {
        Stopwatch? swSub = SaTiming
            ? Stopwatch.StartNew()
            : null;

        void SubMark(string label)
        {
            if (swSub == null)
            {
                return;
            }

            swSub.Stop();
            Console.Error.WriteLine(value: $"    P6sub - {label}: {swSub.ElapsedMilliseconds} ms");
            swSub.Restart();
        }

        // BASE build only (SeedAllStdlibRoutines): const-generic Array[T,N] and other instances created
        // during stdlib body analysis carry the "defer until user code touches it" lazy flag, which
        // excludes them from AllConcreteGenericInstances (so GMP never monomorphizes them) and from the
        // routine sweeps below (so their derive stubs are never registered). A precompiled base must
        // DEFINE everything it references, so clear the flag on all of them here — before the derive-stub
        // registration + WiredRoutinePass body-building below and before Phase 8 monomorphization — so
        // their destroy/represent/hash derives are synthesized and emitted. Normal builds skip this
        // entirely (flag stays; entry-point liveness drives what gets materialized), so they are unchanged.
        if (SeedAllStdlibRoutines)
        {
            int materialized = _registry.MaterializeAllLazyStdlibTypes();
            if (SaTiming)
            {
                Console.Error.WriteLine(
                    value: $"    P6sub - MaterializeAllLazyStdlibTypes: {materialized} types");
            }
        }

        // Build the bodies of all on-demand-synthesized variants (Phase-5 demand + transitive) BEFORE the
        // desugaring pipeline, so they flow through the same variant-body lowering (PresetInlining,
        // control-flow, operator, etc.) the eager `emit` bodies get — otherwise a body's preset identifiers
        // (e.g. S64_MIN in try_floordiv) survive to codegen (RF-S958).
        DrainVariantBodyGenQueue();

        var ctx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode)
        {
            VariantBodies = _variantBodies,
            SynthesizeAllDerives = SeedAllStdlibRoutines,
            RestoredVariantKeys = _memo.RestoredVariantKeys
        };
        new DesugaringPipeline(ctx: ctx).RunGlobal();
        SubMark(label: $"{nameof(DesugaringPipeline)}.RunGlobal");
        // Capture variant bodies produced by ErrorHandlingVariantPass for codegen. On the warm-restore
        // path _variantBodies is pre-seeded with the captured stdlib variants, so ErrorHandlingVariantPass
        // only ADDS user variants here — the seeded restored ones survive for codegen.
        _variantBodies = ctx.VariantBodies;
        AnalyzeVariantBodies();
        SubMark(label: nameof(AnalyzeVariantBodies));

        // Phase 8 global: lower variant bodies and stdlib programs with type-aware passes.
        // Also pass synthesized operator bodies so CallOverloadResolutionPass can classify
        // the CallExpression nodes inside them (LoweringKind = Unknown otherwise).
        var synthesizedBodyStatements =
            _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.Body);
        // Phase 6.5: re-run wired-routine synthesis to catch tuple types (and any other
        // lazily-registered types) created during Phase 4 SA. The original Phase 6 sweep
        // could not see these because they did not yet exist in the registry.
        // Re-run AutoRegisterWiredRoutines first so user variants (registered in Phase 3
        // per-file via PreRegisterUserVariants, AFTER the Phase 3 global AutoRegister sweep)
        // get their represent/diagnose stubs registered before WiredRoutinePass synthesizes
        // bodies. MaybeRegisterWired is idempotent on existing memberRoutines.
        AutoRegisterWiredRoutines();
        SubMark(label: nameof(AutoRegisterWiredRoutines));

        var p7ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            synthesizedBodies: synthesizedBodyStatements,
            target: _target,
            buildMode: _buildMode,
            monomorphizedBodies: _instantiatedGenericBodies)
        {
            SynthesizeAllDerives = SeedAllStdlibRoutines
        };
        // WARM-GATE (②): variant bodies restored from a warm snapshot were fully lowered at capture
        // time, so re-running the ~15 RunGlobal lowering passes over them is an idempotent no-op — the
        // dominant warm cost of this phase. Temporarily remove the restored keys from the SHARED
        // _variantBodies dict (every RunGlobal pass iterates this one object) so each pass sweeps only
        // the USER-delta fresh keys, then restore them afterward (codegen reads the lowered bodies back
        // from _variantBodies). Safe because no pass looks up a variant body by a key it isn't currently
        // iterating (verified: no ContainsKey/TryGetValue and every indexer read is the loop's own key).
        // Cold path is untouched: _restoredVariantKeys is empty, so the block is skipped entirely.
        Dictionary<string, Statement>? stashedRestoredVariants = null;
        if (_memo.RestoredVariantKeys.Count > 0)
        {
            stashedRestoredVariants = new Dictionary<string, Statement>(
                capacity: _memo.RestoredVariantKeys.Count,
                comparer: StringComparer.Ordinal);
            foreach (string key in _memo.RestoredVariantKeys)
            {
                if (_variantBodies.TryGetValue(key: key, value: out Statement? restoredBody))
                {
                    stashedRestoredVariants[key: key] = restoredBody;
                    _variantBodies.Remove(key: key);
                }
            }
        }

        new PostprocessingPipeline(ctx: p7ctx).RunGlobal();
        if (stashedRestoredVariants != null)
        {
            foreach (KeyValuePair<string, Statement> kv in stashedRestoredVariants)
            {
                _variantBodies[key: kv.Key] = kv.Value;
            }
        }

        SubMark(label: $"{nameof(PostprocessingPipeline)}.RunGlobal");
    }

    /// <summary>
    /// Lowers Suflae <c>entity E</c> local bindings to <c>Roamed[E]</c> biased-RC wrappers
    /// over all user programs. No-op for RazorForge programs.
    /// </summary>
    private void LowerSuflaeEntityBindings()
    {
        var suflaeEntityPass = new SuflaeEntityLoweringPass(registry: _registry);
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            suflaeEntityPass.Run(program: program);
        }
    }

    /// <summary>
    /// Expands <c>is Crashable err</c> pattern clauses in user and stdlib programs so that the
    /// per-crashable <c>err.crash_message()</c> calls participate in liveness analysis before
    /// reachability runs.
    /// </summary>
    private void ExpandCrashableClauses(PostprocessingContext markerCtx,
        IReadOnlyList<(Program Program, string FilePath, string Module)> freshStdlib)
    {
        var crashablePass = new CrashableExpansionPass(ctx: markerCtx);
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            crashablePass.Run(program: program);
        }

        foreach ((Program program, _, _) in freshStdlib)
        {
            crashablePass.Run(program: program);
        }
    }

    /// <summary>
    /// Phase 7: close reachable generic bodies up front so codegen no longer owns the
    /// common-case monomorphization entry point.
    /// </summary>
    /// <summary>
    /// Builds the variant-body dictionary passed into instantiation: the pre-transformed variant
    /// bodies plus every synthesized wrapper-forwarder body and derived-operator body on a generic
    /// owner type (GMP must monomorphize both; the generic-def version must not be emitted).
    /// </summary>
    private Dictionary<string, Statement> BuildMergedVariantBodies()
    {
        var mergedVariantBodies = new Dictionary<string, Statement>(dictionary: _variantBodies);
        foreach ((string key, (RoutineInfo Routine, Statement Body) pair) in _synthesizedBodies)
        {
            // Include wrapper forwarders AND derived operators on generic owner types.
            // GMP must monomorphize both; Phase C must not emit the generic-def version.
            if (pair.Routine.WrapperForwarderInnerMemberRoutine != null ||
                pair.Routine.OwnerType?.IsGenericDefinition == true)
            {
                mergedVariantBodies[key: key] = pair.Body;
            }
        }

        return mergedVariantBodies;
    }

    /// <summary>Instantiation context retained from <see cref="RunPhase7Instantiation"/> so the pull
    /// collector's shadow can run post-Phase-9 (see <c>_shadowCtx</c> assignment / RunPhase8Postprocessing).</summary>
    private InstantiationContext? _shadowCtx;

    private void RunPhase7Instantiation()
    {
        // Include wrapper forwarder bodies in variantBodies so GMP can rewrite them with
        // concrete type substitutions. Without this, GMP creates empty-body sentinels for
        // concrete forwarder instances instead of properly monomorphized bodies.
        Dictionary<string, Statement> mergedVariantBodies = BuildMergedVariantBodies();

        // WARM GATE: the teardown/temp-teardown/marker passes below re-walk EVERY variant body each warm
        // run. Restored variant bodies (from the snapshot) were already teardown/marker-lowered at capture,
        // and those passes are idempotent on an already-lowered body (proven: warm output == cold), so the
        // re-walk is pure wasted work. Stash the restored keys OUT of mergedVariantBodies for the duration of
        // those passes (so they only touch the fresh user-delta bodies), then restore them BEFORE reachability
        // /GMP, which must still walk the FULL set for liveness. Cold path: _restoredVariantKeys is empty →
        // no-op. mergedVariantBodies IS ctx.VariantBodies (shared object), so removing/re-adding here is what
        // the passes and the fixpoint below observe.
        Dictionary<string, Statement>? stashedP8Variants = null;
        long _p8GateStash = 0;
        if (_memo.RestoredVariantKeys.Count > 0)
        {
            Stopwatch? _swGate = SaTiming
                ? Stopwatch.StartNew()
                : null;
            stashedP8Variants =
                new Dictionary<string, Statement>(comparer: StringComparer.Ordinal);
            foreach (string key in _memo.RestoredVariantKeys)
            {
                if (mergedVariantBodies.TryGetValue(key: key, value: out Statement? body))
                {
                    stashedP8Variants[key: key] = body;
                    mergedVariantBodies.Remove(key: key);
                }
            }

            _p8GateStash = _swGate?.ElapsedMilliseconds ?? 0;
        }

        var ctx = new InstantiationContext(registry: _registry,
            userPrograms: _registry.UserPrograms,
            routineBodies: _routineBodies,
            options: new InstantiationOptions
            {
                VariantBodies = mergedVariantBodies,
                InstantiatedGenericBodies = _instantiatedGenericBodies,
                Target = _target,
                BuildMode = _buildMode,
                StdlibTemplateBodies = _memo.WarmStdlibRoutineBodies,
                RestoredVariantKeys = _memo.RestoredVariantKeys
            }) { SaTiming = SaTiming, SeedAllStdlibRoutines = SeedAllStdlibRoutines };

        // Rewrite Accessing[T]/Controlling[T] params to inner T before reachability so
        // the resulting RegistryKeys / mangled names captured downstream match codegen.
        // Call-site refer/control coercion was already injected during SA argument binding.
        // Pass mergedVariantBodies (not _variantBodies) so the dict used by reachability/GMP
        // gets re-keyed to the post-mutation form.
        var markerCtx = new PostprocessingContext(registry: _registry,
            variantBodies: mergedVariantBodies,
            synthesizedBodies: _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.Body),
            target: _target,
            buildMode: _buildMode);

        // Suflae only: lower `entity E` bindings to a `Roamed[E]` biased-RC backing. MUST run BEFORE
        // scope-teardown below so teardown inserts `Roamed.destroy` (→ release → cycle-collector
        // chain) for entity locals instead of a bare-entity `destroy` (which double-frees an alias
        // and never reaches the RC/cc machinery). Also before reachability, so the roam/promote/lock/
        // cc hooks seed off the live `Roamed` wrapper type. No-op for RazorForge.
        LowerSuflaeEntityBindings();

        // Insert scope-exit `destroy()` calls BEFORE reachability (so the calls drive liveness —
        // no manual seeding needed) and BEFORE the marker pass (so Accessing[T]/Controlling[T]
        // params are still protocol-typed and excluded as access types, not yet stripped to the
        // inner entity). Generic bodies are processed here too, then monomorphized with the calls.
        // Warm-restore: the restored stdlib programs already carry teardown/temp-teardown/marker/
        // crashable lowering from the capture, so re-running those passes on them would double-apply and
        // diverge. Only the FRESHLY-loaded stdlib programs (cold: all; warm: on-demand imports) need it.
        // Reachability + GMP below still walk the FULL (restored + fresh) StdlibPrograms.
        List<(Program Program, string FilePath, string Module)> freshStdlib =
            _registry.FreshlyLoadedStdlibPrograms;

        var teardownPass = new ScopeTeardownLoweringPass(ctx: markerCtx);
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            teardownPass.Run(program: program);
        }

        foreach ((Program program, _, _) in freshStdlib)
        {
            teardownPass.Run(program: program);
        }

        teardownPass.RunOnVariantBodies();

        // Tear down owned RVALUE temporaries (heap-owning receiver/discarded producers) that the
        // binding-only ScopeTeardownLoweringPass cannot see. Runs AFTER teardown so it never
        // double-frees the temps' bindings, and BEFORE reachability so its destroy calls drive
        // liveness. Stdlib + variant bodies are already Phase-8 lowered here (when→if done); USER
        // programs are lowered later (Phase 8 per-file), so they get this pass in RunPhase8Postprocessing.
        var tempTeardownPass = new TemporaryTeardownPass(ctx: markerCtx);
        foreach ((Program program, _, _) in freshStdlib)
        {
            tempTeardownPass.Run(program: program);
        }

        tempTeardownPass.RunOnBodies(bodies: markerCtx.VariantBodies);

        // MarkerProtocolDesugarPass. The warm-daemon RF-S413 false positive is ALREADY fixed by routing
        // marker protocols through the ordinary generic-bound desugar in SignatureResolver: a param is now
        // a bound generic, never a bare marker protocol, so RewriteAllSignatures finds no marker param to
        // erase in place — the shared/cached RoutineInfo is no longer mutated, so the cache can't be
        // poisoned. The pass is kept because its expression cleanup + late-resolution/instantiated-body
        // re-keying is still load-bearing for non-marker lowering (e.g. Sender-of-T GMCE construction).
        // Fully removing it requires separately solving that GMCE-lowering gap (warm-restore-gmce-bug).
        // The marker-protocol erase pass is no longer needed: marker protocols now desugar to generic
        // bounds in SignatureResolver, so no bare marker-protocol param reaches this pass for erasure.
        // Re-keying via RewriteAllSignatures is also disabled. The GMCE-lowering gap it addressed
        // (Sender-of-T construction) is tracked separately.

        // Restore the stashed restored variant bodies BEFORE reachability/GMP — the fixpoint below must walk
        // the FULL (restored + fresh) set for liveness (its GMP re-monomorphization short-circuits on the
        // already-present InstantiatedGenericBodies keys, so this only re-adds them for the liveness WALK).
        if (stashedP8Variants != null)
        {
            foreach (KeyValuePair<string, Statement> kv in stashedP8Variants)
            {
                mergedVariantBodies[key: kv.Key] = kv.Value;
            }

            if (SaTiming)
            {
                Console.Error.WriteLine(
                    value:
                    $"  Phase 8 warm-gate - skipped teardown/marker on {stashedP8Variants.Count} restored variants (stash {_p8GateStash} ms)");
            }
        }

        // Expand `is Crashable err` clauses BEFORE reachability so that the new
        // per-crashable `err.crash_message()` calls participate in liveness analysis.
        // Without this, the fanout happens in Phase 8 and the crash_message memberRoutine on
        // each concrete crashable is never marked reachable -> linker errors.
        ExpandCrashableClauses(markerCtx: markerCtx, freshStdlib: freshStdlib);

        // Fold constant list-returning BuilderQuery reflection calls (routine_names/protocols/annotations/…)
        // into inline analyzed List[Text] literals BEFORE reachability, so RRP walks the literal (seeding its
        // from_literal builder) rather than a per-type reflection routine — no such routine is synthesized, so
        // none survives into codegen. Must run after the marker/crashable AST rewrites above (stable call
        // shape) and before ReachableGenericCollectionPass/RRP below.
        FoldListBuilderQueryReflection();

        // Full-stdlib-closure FIXPOINT (only when SeedAllStdlibRoutines is set, i.e. building the precompiled
        // stdlib base): each reachability+monomorphization round materializes new instances whose bodies then
        // reference STILL-MORE concrete instances a single pass never seeded. Re-run reachability (which
        // re-seeds ALL concrete routines, now including the freshly-materialized ones) + monomorphization
        // until InstantiatedGenericBodies stops growing → the full closure the base must define. Bounded (the
        // instance set is finite; the guard caps any runaway). Normal builds run exactly ONE round (the
        // while-condition is false) → byte-identical.
        RunReachabilityMonomorphizationFixpoint(ctx: ctx);

        _variantBodies = ctx.VariantBodies;
        _instantiatedGenericBodies = ctx.InstantiatedGenericBodies;
        _liveRoutineKeys = ctx.LiveRoutineKeys.ToArray();
        _liveOwnerTypeNames = ctx.LiveOwnerTypeNames.ToArray();
        // Retain the instantiation context so the pull collector's SHADOW can run AFTER per-program Phase 9
        // lowering (RunPhase8Postprocessing), where user bodies have their subscript/operator calls lowered
        // to real getitem/member CallExpressions — the entry-point walk needs those to follow start()'s chain.
        _shadowCtx = ctx;
        // Stage-2 (pull/(B)): bind the demand-resolution hook so the collector can analyze a reached
        // stdlib file on first touch. No-op until the Stage-5 flip (guarded by _eagerStdlibAnalyzed).
        ctx.AnalyzeRoutineOnDemand = AnalyzeStdlibProgramOnDemand;
        // Bind the sibling hook that SA-annotates a derive-template body the collector clones per concrete
        // owner (lt/le/gt/ge from cmp, represent/cmp/… on a plain type) — the raw template body needs types
        // resolved in the owner's context before the fresh-body lowering sweep folds its operators.
        ctx.AnalyzeMaterializedDeriveBody = AnalyzeMaterializedDeriveBodyOnDemand;

        // v0.2.0 may-suspend effect analysis over the call graph RoutineReachabilityPass populated
        // (in either the timed or pipeline path above). Runs here — after both branches — so it is
        // computed exactly once regardless of SaTiming. Empty for any program that never reaches a
        // suspend primitive (all current code), so codegen instruments nothing extra.
        ComputeMaySuspend(ctx: ctx);

        // Classify call expressions (set LoweringKind) in rewritten instantiated generic bodies.
        // GenericAstRewriter preserves source-AST structure but doesn't re-classify try_emit
        // and other wired calls — they stay Unknown and cause codegen exceptions if not fixed here.
        var classCtx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            target: _target,
            buildMode: _buildMode);
        // WARM GATE: restored instantiation bodies were already call-classified at capture; only re-classify
        // the FRESH (user-delta) ones. Cold path: _restoredInstantiationKeys empty → classifies all.
        new CallOverloadResolutionPass(ctx: classCtx).RunOnStatements(
            statements: _instantiatedGenericBodies
                       .Where(predicate: kv => !_memo.RestoredInstantiationKeys.Contains(item: kv.Key))
                       .Select(selector: kv => kv.Value.Ast.Body));
    }

    /// <summary>
    /// Runs the reachability + monomorphization passes (ReachableGenericCollection →
    /// RoutineReachability → GenericClosure), then canonicalizes. For a normal build this executes
    /// exactly ONE round; only a full-stdlib-closure base build (<c>SeedAllStdlibRoutines</c>)
    /// re-runs the round until <c>InstantiatedGenericBodies</c> stops growing (bounded by a guard).
    /// </summary>
    private void RunReachabilityMonomorphizationFixpoint(InstantiationContext ctx)
    {
        // PULL UNIFICATION: for NORMAL builds the demand collector (RunShadowCollectorIfNeeded ->
        // RoutineCollectionPass) is the SOLE monomorphizer + liveness authority — it OVERWRITES
        // _liveRoutineKeys/_liveOwnerTypeNames and builds InstantiatedGenericBodies. The push reachability
        // walk below merely DUPLICATED that work and, running first, POLLUTED the shared registry by
        // materializing instances the demand walk never reaches (e.g. Range[U64] pulled in via reflection
        // over-reach), which the demand collector then inherited — the warm/cold define-set divergence.
        // Skip the whole push fixpoint for normal builds; keep it ONLY for the base build, whose eager
        // GenericClosurePass must define the ENTIRE stdlib closure (demand-from-start cannot).
        if (!ctx.SeedAllStdlibRoutines)
        {
            return;
        }

        int prevCount;
        int guard = 0;
        do
        {
            prevCount = ctx.InstantiatedGenericBodies.Count;
            if (SaTiming)
            {
                var sw = Stopwatch.StartNew();

                void Step(string label)
                {
                    sw.Stop();
                    Console.Error.WriteLine(
                        value: $"  Phase 8 sub - {label}: {sw.ElapsedMilliseconds} ms");
                    sw.Restart();
                }

                new ReachableGenericCollectionPass(ctx: ctx).Run();
                Step(label: nameof(ReachableGenericCollectionPass));
                // Base-only path: GenericClosurePass builds the WHOLE stdlib closure eagerly. (The push
                // RoutineReachabilityPass walk is gone — the demand collector is the sole walk for normal
                // builds, and base emits everything via the closure + empty LiveRoutineKeys.)
                new GenericClosurePass(ctx: ctx).Run();
                Step(label: nameof(GenericClosurePass));
            }
            else
            {
                new ReachableGenericCollectionPass(ctx: ctx).Run();
                // Base-only path: eager full-stdlib closure. (No push RoutineReachabilityPass.)
                new GenericClosurePass(ctx: ctx).Run();
            }
        } while (ctx.SeedAllStdlibRoutines && ctx.InstantiatedGenericBodies.Count != prevCount &&
                 ++guard < 20);

        if (ctx.SeedAllStdlibRoutines)
        {
            new GenericClosurePass(ctx: ctx).RunIsolatedTail();
        }

        GenericCanonicalizationPass.Run();
        if (SaTiming && ctx.SeedAllStdlibRoutines)
        {
            Console.Error.WriteLine(value: $"  Phase 8 base-closure fixpoint rounds={guard + 1}");
        }
    }

    /// <summary>
    /// Runs the v0.2.0 may-suspend fixpoint over the call graph that
    /// <c>RoutineReachabilityPass</c> populated, storing the result for codegen's 9-2
    /// cancellation instrumentation. Optional <c>RF_MAYSUSPEND_DUMP</c> writes the set for probes.
    /// </summary>
    private void ComputeMaySuspend(InstantiationContext ctx)
    {
        IReadOnlyCollection<string> maySuspend =
            new MaySuspendAnalysis(callGraph: ctx.MaySuspendGraph).Compute()
               .ToArray();
        foreach (string key in maySuspend)
        {
            ctx.MaySuspendRoutineKeys.Add(item: key);
        }

        _maySuspendRoutineKeys = maySuspend;

        string? dumpPath = DiagnosticFlags.MaySuspendDump;
        if (!string.IsNullOrEmpty(value: dumpPath))
        {
            var lines = new List<string> { "=== MAY-SUSPEND ROUTINES ===" };
            lines.AddRange(collection: maySuspend.OrderBy(keySelector: s => s));
            File.WriteAllLines(path: dumpPath, contents: lines);
        }
    }

    /// <summary>
    /// Phase 4 (syntax prepass): Syntax-only lowering that requires no type information.
    /// Runs before Phase 5 annotates ResolvedType on expressions.
    /// </summary>
    private void RunPhase4SyntaxPrepass(Program program)
    {
        var ctx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode);
        new DesugaringPipeline(ctx: ctx).Run(program: program);
    }

    /// <summary>
    /// Phase 8 (per-file): Type-aware lowering on a verified, type-annotated program.
    /// Runs after Phase 5 has annotated ResolvedType on all expressions.
    /// </summary>
    private void RunPhase8Postprocessing(Program program)
    {
        var ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            target: _target,
            buildMode: _buildMode,
            monomorphizedBodies: _instantiatedGenericBodies);
        // Now that Phase 7 has produced the concrete instances, lower any carrier-return sites inside
        // them (a monomorphized try_/check_/lookup_ variant) to real record construction.
        new VariantReturnLoweringPass(ctx: ctx).RunOnMonomorphizedBodies();
        // Inline simple iterator `emit!` bodies into their for-loops before the rest of Phase 8
        // lowering, replacing the `try_emit` call with the spliced advance. By Phase 8 the concrete
        // `emit!` bodies are already monomorphized (Phase 7 ran), so the lookup succeeds; the
        // spliced body then flows through the normal Phase 8 lowering below. Composed/filtering
        // iterators fall back to the existing `try_emit` loop.
        new IteratorInlineLoweringPass(registry: _registry, monoBodies: _instantiatedGenericBodies)
           .Run(program: program);
        new PostprocessingPipeline(ctx: ctx).Run(program: program);

        // Owned rvalue-temporary teardown for user code, now that Phase 8 has lowered when→if so the
        // producing calls sit in real statements. ScopeTeardownLoweringPass already ran (pre-lowering,
        // step 4) and will not revisit this program, so the temps' bindings are freed exactly once by
        // the destroy calls this pass emits (codegen emit-on-demand resolves the concrete destroy).
        new TemporaryTeardownPass(ctx: ctx).Run(program: program);
    }


    /// <summary>
    /// Phase 9: validates that postprocessing produced a backend-safe normalized AST.
    /// </summary>
    private void RunPhase9PostDesugarChecks()
    {
        var reprPass = new BackendRepresentationPass(registry: _registry, target: _target);
        var validator = new BackendEntryValidator(registry: _registry);

        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            reprPass.Run(program: program);
            foreach (SemanticError error in validator.ValidateProgram(program: program))
            {
                AddError(error: error);
            }
        }

        // Warm-restore: the stdlib program ASTs are shared read-only across warm compiles and were
        // already lowered to backend representation at capture time — re-running reprPass on them each
        // warm run is pure redundant cost (and re-mutating a shared AST is unsafe).
        if (!_memo.IsWarm)
        {
            foreach ((Program stdlibProgram, _, _) in _registry.StdlibPrograms)
            {
                reprPass.Run(program: stdlibProgram);
            }
        }

        foreach ((string key, Statement body) in _variantBodies)
        {
            // Warm-restore: variants captured from the snapshot were already repr'd + validated.
            if (_memo.RestoredVariantKeys.Contains(item: key))
            {
                continue;
            }

            reprPass.Run(statement: body);
            foreach (SemanticError error in validator.ValidateStatement(statement: body))
            {
                AddError(error: error with { Message = $"[{key}] {error.Message}" });
            }
        }

        foreach ((string key, MonomorphizedBody mono) in _instantiatedGenericBodies)
        {
            // Warm-restore: instantiations captured from the snapshot were already repr'd + validated.
            if (_memo.RestoredInstantiationKeys.Contains(item: key))
            {
                continue;
            }

            if (!mono.IsSynthesized)
            {
                reprPass.Run(statement: mono.Ast.Body);
            }

            foreach (SemanticError error in BackendEntryValidator.ValidateMonomorphizedBody(
                         body: mono))
            {
                AddError(error: error with { Message = $"[mono:{key}] {error.Message}" });
            }
        }

    }

    /// <summary>
    /// Validates routine bodies in the standard library and returns the full error list.
    /// Used by the <c>validate-stdlib</c> CLI subcommand to surface stdlib errors that the
    /// normal build pipeline suppresses. The main build pipeline calls
    /// <see cref="AnalyzeStdlibBodies"/> (via M-0) but discards its errors so they don't
    /// block user builds.
    /// </summary>
    /// <returns>List of errors found in stdlib routine bodies.</returns>
    /// <summary>Runs the <see cref="Compiler.Declaration.RuntimeContractCheck"/> against the loaded
    /// stdlib registry: asserts every name the compiler hard-codes against the stdlib still resolves.
    /// Call AFTER <see cref="ValidateStdlibBodies"/> (which loads and analyzes the stdlib). Returns a
    /// description per broken contract; empty means all contracts hold.</summary>
    public List<string> CheckRuntimeContract()
    {
        return RuntimeContractCheck.Check(registry: _registry);
    }

    /// <summary>True while <see cref="ValidateStdlibBodies"/> runs its reduced-phase stdlib check, so
    /// the structural operator-protocol gate (which needs full-pipeline derived operators) is suppressed.</summary>
    private bool _isReducedStdlibValidation;

    /// <summary>Runs a reduced-phase semantic check over stdlib routine bodies; returns the errors found.</summary>
    public List<SemanticError> ValidateStdlibBodies()
    {
        int errorsBefore = _errors.Count;

        // The operator-protocol conformance gate (AnalyzeBinaryExpression) relies on STRUCTURAL
        // conformance, whose derived operator memberRoutines (e.g. ByteSize's wrapping-multiply) are only
        // fully materialized by the FULL pipeline — this reduced validation phase runs a trimmed
        // GenerateDerivedOperators, so structural conformance under-reports and the gate false-fires
        // on stdlib operators that the full-pipeline StdlibHarness already validates. Suppress the gate
        // here; it stays active for user code, which is where an operator mis-bind actually matters.
        _isReducedStdlibValidation = true;

        // Run global phases that stdlib body analysis depends on
        // (StdlibLoader registered types and routines, but these phases were not run)
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        InferWiredMemberRoutines();
        AnalyzeSynthesizedBodies();

        // Index failable stdlib routines for on-demand variant synthesis (no eager GenerateVariants),
        // then install the synthesizer hook so stdlib bodies that call try_X (e.g. try_get_by_rank)
        // resolve during body analysis. Uses AST-level detection — no full body analysis required.
        PreRegisterStdlibVariants();
        _registry.OnDemandVariantSynthesizer = TrySynthesizeVariantOnDemand;
        _registry.OnDemandVariantForBase = SynthesizeVariantForBase;

        AnalyzeStdlibBodies();

        // Collect stdlib-specific errors
        var stdlibErrors = new List<SemanticError>();
        for (int i = errorsBefore; i < _errors.Count; i++)
        {
            stdlibErrors.Add(item: _errors[index: i]);
        }

        return stdlibErrors;
    }

    /// <summary>
    /// Runs per-program body analysis on every stdlib program registered via StdlibLoader.
    /// Sets up the correct module/import context for each file, calls <see cref="AnalyzeBodies"/>
    /// (which type-annotates expressions and populates <c>_routineBodies</c>), then restores state.
    ///
    /// Assumes the caller has already run the Phase 4/6 prerequisites
    /// (<c>ApplyImplicitMarkerConformance</c>, <see cref="AutoRegisterWiredRoutines"/>,
    /// <see cref="GenerateDerivedOperators"/>). Errors are appended to <c>_errors</c> ->
    /// callers that need to partition stdlib errors must snapshot <c>_errors.Count</c> themselves.
    /// </summary>
    private void AnalyzeStdlibBodies()
    {
        // The eager sweep is in effect (whether or not there are fresh programs — a warm restore's stdlib
        // was analyzed at capture). Mark it so the Stage-2 demand hook stays a no-op until the Stage-5 flip.
        _eagerStdlibAnalyzed = true;
        // Analyze only FRESHLY-loaded stdlib programs: in a cold compile that is every stdlib program.
        // In a warm-restore compile the restored bodies were already analyzed at capture, so this is just
        // the modules imported on-demand (e.g. IO/Console) — which still need SA before lowering/codegen.
        List<(Program Program, string FilePath, string Module)> freshStdlibPrograms =
            _registry.FreshlyLoadedStdlibPrograms;
        if (freshStdlibPrograms.Count == 0)
        {
            return;
        }

        // Mark the registry so that any concrete generic instances created as side-effects
        // of stdlib body analysis are tagged IsStdlibLazy and excluded from GMP iteration.
        // Types the user program actually needs will be materialized when user SA references them.
        // The stdlib is always RazorForge source (SF ≡ RF grammar; SF's Core IS RF's Core). Analyze
        // its bodies in RazorForge mode so language-sensitive SA — unsuffixed-literal defaults, the
        // danger/extern gates, generic-call resolution — matches how the stdlib was authored. Under
        // Suflae mode the RF stdlib's generic calls fail to resolve and survive lowering (RF-S954).
        Language savedLanguage = _registry.Language;
        // Capture the user's TARGET language before toggling to RF for stdlib body analysis, so
        // RazorForge-only diagnostics (@readonly/@reshaping) can be gated OFF for a Suflae build even
        // while analyzing the borrowed RF stdlib in RF mode.
        _registry.CompilationLanguage = savedLanguage;
        _registry.Language = Language.RazorForge;
        _registry.BeginStdlibAnalysis();
        try
        {

            string previousFilePath = _currentFilePath;
            var previousImports = new HashSet<string>(collection: _importedModules,
                comparer: StringComparer.OrdinalIgnoreCase);
            var previousForeignAliases = new HashSet<string>(collection: _importedForeignAliases,
                comparer: StringComparer.Ordinal);
            string? previousModuleName = _currentModuleName;
            foreach ((Program program, string filePath, string module) in freshStdlibPrograms)
            {
                AnalyzeOneStdlibProgram(program: program, filePath: filePath, module: module);
            }

            _currentFilePath = previousFilePath;
            _currentModuleName = previousModuleName;
            _registry.ResolutionRealm = _registry.AmbientRealm;
            _importedModules.Clear();
            foreach (string ns in previousImports)
            {
                _importedModules.Add(item: ns);
            }

            _importedForeignAliases.Clear();
            foreach (string alias in previousForeignAliases)
            {
                _importedForeignAliases.Add(item: alias);
            }

        }
        finally
        {
            _registry.EndStdlibAnalysis();
            _registry.ResolutionRealm = _registry.AmbientRealm;
            _registry.Language = savedLanguage;
        }

        _eagerStdlibAnalyzed = true;
    }

    /// <summary>
    /// Sets up one stdlib file's resolution scope (realm + auto-Core + own-module + its <c>import</c>s)
    /// and analyzes every body in it. Extracted from <see cref="AnalyzeStdlibBodies"/>'s per-program loop
    /// so the SAME setup can run ON DEMAND (Stage-2 pull/(B)) — <see cref="AnalyzeStdlibProgramOnDemand"/>
    /// calls it the first time the collector reaches a routine declared in this file, instead of the eager
    /// sweep analyzing every stdlib file up front.
    /// </summary>
    private void AnalyzeOneStdlibProgram(Program program, string filePath, string module)
    {
        _currentFilePath = filePath;
        _currentModuleName = module;
        // Prefer this file's own realm when resolving its bare type names: an SF-realm (`.sf`) stdlib
        // file's bare `List` resolves to the SF-realm `Core.List` (bridged) it declares, while an RF file
        // keeps the ambient RF resolution. Zero effect on RF (resolution realm == ambient there).
        _registry.ResolutionRealm = filePath.EndsWith(value: ".sf",
            comparisonType: StringComparison.OrdinalIgnoreCase)
            ? "SF"
            : "RF";
        _importedModules.Clear();
        _importedSymbolNames.Clear();
        _importedForeignAliases.Clear();

        // Core module types are auto-imported
        _importedModules.Add(item: "Core");

        // Add the file's own module so sibling types resolve
        if (!string.IsNullOrEmpty(value: module))
        {
            _importedModules.Add(item: module);
        }

        // Process import declarations for this stdlib file
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not ImportDeclaration import)
            {
                continue;
            }

            string importModule = import.ModulePath;
            int dotIdx = importModule.IndexOf(value: '.');
            if (dotIdx > 0)
            {
                _importedModules.Add(item: importModule[..dotIdx]);
            }

            _importedModules.Add(item: importModule);
        }

        AnalyzeBodies(program: program);
    }

    /// <summary>
    /// Stage-2 (pull/(B)) DEMAND analysis: the map from a routine <c>RegistryKey</c> to the stdlib
    /// program (file) that declares it, plus the set of files already analyzed. Built lazily on first use.
    /// Program-granularity is the first cut — reaching any routine in a file analyzes the whole file (the
    /// proven per-file scope setup is what makes resolution correct); per-routine granularity is a later
    /// refinement. USER routines are excluded (they were analyzed eagerly in the driver's Phase 5).
    /// </summary>
    private Dictionary<string, (Program Program, string FilePath, string Module)>?
        _demandStdlibProgramForKey;

    private readonly HashSet<string> _demandAnalyzedFiles = new(comparer: StringComparer.Ordinal);

    /// <summary>Set once the EAGER <see cref="AnalyzeStdlibBodies"/> sweep has run — while true, the
    /// demand hook is a no-op (the eager pass already analyzed every stdlib file). The Stage-5 flip stops
    /// calling the eager sweep, leaving this false, so <see cref="AnalyzeStdlibProgramOnDemand"/> becomes
    /// load-bearing (the collector drives per-file analysis on reach).</summary>
    private bool _eagerStdlibAnalyzed;


    /// <summary>
    /// SA-annotates a derive-template body the collector cloned for a concrete owner (T→owner already
    /// substituted): resolves types/calls in the owner's context via <see cref="AnalyzeCompilerGeneratedBody"/>
    /// so the fresh-body lowering sweep can fold its operators (<c>me.type_name() + "("</c>) and resolve its
    /// delegated calls (<c>me.cmp(you)</c>). Annotates in place; returns the same body. Bound to
    /// <see cref="InstantiationContext.AnalyzeMaterializedDeriveBody"/>.
    /// </summary>
    private Statement AnalyzeMaterializedDeriveBodyOnDemand(RoutineInfo routine, Statement body)
    {
        AnalyzeCompilerGeneratedBody(routineInfo: routine, body: body);
        return body;
    }

    /// <summary>
    /// Ensures the stdlib file declaring <paramref name="routineKey"/> has been body-analyzed, running the
    /// per-file setup + analysis exactly as the eager sweep would — but only when the collector actually
    /// reaches a routine in it. Wraps the same RF-language / stdlib-analysis-mode toggles as
    /// <see cref="AnalyzeStdlibBodies"/>. No-op if the file is already analyzed or the key is not a stdlib
    /// routine. This is the hook <see cref="InstantiationContext.AnalyzeRoutineOnDemand"/> is bound to.
    /// </summary>
    internal bool AnalyzeStdlibProgramOnDemand(string routineKey)
    {
        // While the eager sweep is in effect, every stdlib file is already analyzed → no-op (avoids
        // double-analysis / duplicate registration). Becomes active after the Stage-5 flip.
        if (_eagerStdlibAnalyzed)
        {
            return false;
        }

        if (_demandStdlibProgramForKey == null)
        {
            _demandStdlibProgramForKey =
                new Dictionary<string, (Program, string, string)>(
                    comparer: StringComparer.Ordinal);
            foreach ((Program p, string fp, string mod) in _registry.StdlibPrograms)
            foreach (ISyntaxTreeNode d in p.Declarations)
            {
                if (d is RoutineDeclaration { ResolvedInfo: { } ri })
                {
                    _demandStdlibProgramForKey[key: ri.RegistryKey] = (p, fp, mod);
                }
            }
        }

        if (!_demandStdlibProgramForKey.TryGetValue(key: routineKey,
                value: out (Program Program, string FilePath, string Module) entry))
        {
            return false;
        }

        if (!_demandAnalyzedFiles.Add(item: entry.FilePath))
        {
            return false;
        }

        Language savedLanguage = _registry.Language;
        string previousFilePath = _currentFilePath;
        string? previousModuleName = _currentModuleName;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        _registry.CompilationLanguage = savedLanguage;
        _registry.Language = Language.RazorForge;
        _registry.BeginStdlibAnalysis();
        // Suppress the operator-protocol conformance gate for stdlib bodies: a demand-analyzed file's
        // cross-file operators (e.g. ByteSize's wrapping-multiply used in List.rf) resolve against derived
        // operators only fully materialized by the whole pipeline — per-file demand SA under-reports
        // structural conformance and the gate false-fires (RF-S065). The gate stays active for USER code,
        // where an operator mis-bind actually matters. Mirrors ValidateStdlibBodies' rationale.
        bool prevReduced = _isReducedStdlibValidation;
        _isReducedStdlibValidation = true;
        // Stdlib-body diagnostics are SUPPRESSED from a user build (surfaced only via `validate-stdlib`) —
        // the eager path does this by RemoveRange-ing everything AnalyzeStdlibBodies() reports (Phase 5).
        // The demand path analyzes each reached stdlib file HERE instead, so it must discard its diagnostics
        // the same way; otherwise a stdlib-internal diagnostic that the eager sweep silently drops (a bare
        // foreign call RF-S460, a teardown-injected `destroy` RF-S609, an iterator-protocol RF-S205) leaks
        // into the user build and fails it — the demand-only regression across the stdlib fixtures.
        int errorsBeforeStdlib = _errors.Count;
        int warningsBeforeStdlib = _warnings.Count;
        try
        {
            AnalyzeOneStdlibProgram(program: entry.Program,
                filePath: entry.FilePath,
                module: entry.Module);
            // FLIP: fully lower this reached file so codegen sees ready bodies — the same per-file passes the
            // eager sweep ran (DesugaringPipeline.Run = syntactic Phase-3; PostprocessingPipeline.Run =
            // type-aware Phase-8). Variant/wired bodies come via the on-demand variant synthesizer + the
            // collector's per-owner materialization, so only the per-file program passes run here.
            var dctx =
                new DesugaringContext(registry: _registry,
                    routineBodies: _routineBodies,
                    target: _target,
                    buildMode: _buildMode)
                {
                    VariantBodies = _variantBodies,
                    SynthesizeAllDerives = SeedAllStdlibRoutines,
                    RestoredVariantKeys = _memo.RestoredVariantKeys
                };
            new DesugaringPipeline(ctx: dctx).Run(program: entry.Program);
            var pctx = new PostprocessingContext(registry: _registry,
                variantBodies: _variantBodies,
                synthesizedBodies: _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
                    elementSelector: kvp => kvp.Value.Body),
                target: _target,
                buildMode: _buildMode,
                monomorphizedBodies: _instantiatedGenericBodies);
            new PostprocessingPipeline(ctx: pctx).Run(program: entry.Program);
        }
        finally
        {
            // Discard stdlib-body diagnostics (see snapshot above): mirror the eager sweep's RemoveRange so a
            // user build never fails on a stdlib-internal diagnostic. Trim back to the pre-analysis counts.
            if (_errors.Count > errorsBeforeStdlib)
            {
                _errors.RemoveRange(index: errorsBeforeStdlib,
                    count: _errors.Count - errorsBeforeStdlib);
            }

            if (_warnings.Count > warningsBeforeStdlib)
            {
                _warnings.RemoveRange(index: warningsBeforeStdlib,
                    count: _warnings.Count - warningsBeforeStdlib);
            }

            _registry.EndStdlibAnalysis();
            _registry.ResolutionRealm = _registry.AmbientRealm;
            _registry.Language = savedLanguage;
            _isReducedStdlibValidation = prevReduced;
            _currentFilePath = previousFilePath;
            _currentModuleName = previousModuleName;
            _importedModules.Clear();
            foreach (string ns in previousImports)
            {
                _importedModules.Add(item: ns);
            }
        }

        return true; // analyzed+desugared a NEW file → collector rebuilds its template index
    }

    /// <summary>
    /// Analyzes multiple program ASTs from a multi-file build.
    /// Phases are split so per-file phases run with correct import scoping,
    /// while global phases run once across the combined registry.
    /// </summary>
    /// <param name="files">The programs and their file paths, in topological (dependency) order.</param>
    /// <returns>Analysis result containing errors, warnings, and the populated type registry.</returns>
    public AnalysisResult AnalyzeMultiple(List<(Program Program, string FilePath)> files)
    {
        // On-demand failable-variant synthesis (see Analyze): install the hook here too — the
        // multi-file / stdlib build path does not go through Analyze.
        _registry.OnDemandVariantSynthesizer = TrySynthesizeVariantOnDemand;
        _registry.OnDemandVariantForBase = SynthesizeVariantForBase;
        _importSnapshots.Clear();
        _symbolNameSnapshots.Clear();
        _moduleNameSnapshots.Clear();
        _foreignAliasSnapshots.Clear();
        bool saTiming = SaTiming;
        var swPhase = Stopwatch.StartNew();

        void Mark(string label)
        {
            if (!saTiming)
            {
                return;
            }

            swPhase.Stop();
            Console.Error.WriteLine(value: $"{label}: {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        // Snapshot storage: file path -> imported modules after Phase 1
        var importSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var symbolNameSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var moduleNameSnapshots =
            new Dictionary<string, string?>(comparer: StringComparer.OrdinalIgnoreCase);

        // Every file in the build graph contributes its declarations via Phase 1 below.
        // Pre-mark their declared modules as provided so `import` statements between them
        // record the module name instead of re-loading the file through StdlibLoader
        // (which would register every routine a second time -> duplicate-definition errors).
        foreach ((Program program, string filePath) in files)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is ModuleDeclaration moduleDecl)
                {
                    _registry.MarkModuleProvided(modulePath: moduleDecl.Path, filePath: filePath);
                    break;
                }
            }
        }

        // Phase 1: Collect declarations from ALL files (populates registry with all types/routines)
        foreach ((Program program, string filePath) in files)
        {
            _currentFilePath = filePath;
            _currentModuleName = null;
            _importedModules.Clear();
            _importedSymbolNames.Clear();

            RunPhase1Declarations(program: program);

            importSnapshots[key: filePath] = new HashSet<string>(collection: _importedModules,
                comparer: StringComparer.OrdinalIgnoreCase);
            symbolNameSnapshots[key: filePath] =
                new HashSet<string>(collection: _importedSymbolNames,
                    comparer: StringComparer.Ordinal);
            moduleNameSnapshots[key: filePath] = _currentModuleName;
            CaptureCurrentImportStateSnapshot(filePath: filePath);
        }

        Mark(label: "Phase 1 -> Declarations");

        // Record per-module import lists for BuilderQuery's T.dependencies(). importSnapshots is keyed
        // by file; a module may span files, so accumulate each file's imports under its declared module.
        foreach ((string filePath, HashSet<string> imports) in importSnapshots)
        {
            string? mod = moduleNameSnapshots.GetValueOrDefault(key: filePath);
            if (!string.IsNullOrEmpty(value: mod))
            {
                _registry.AddModuleDependencies(module: mod, imports: imports);
            }
        }

        // Phase 1b: Re-resolve record/entity `obeys` conformances now that ALL files' types AND
        // every referenced (lazily-loaded) protocol are registered. Initial per-file declaration
        // resolution can drop a protocol whose definition wasn't loaded yet — e.g. a user module
        // record obeying a Core protocol (FloorDivisible) registered before that protocol's file
        // loaded — leaving ImplementedProtocols short. Re-resolving here (before signature
        // resolution runs the generic-constraint checks, RF-S150) fills them in.
        foreach ((Program program, string _) in files)
        {
            StdlibLoader.ResolveProgramProtocolConformances(registry: _registry, program: program);
        }

        Mark(label: "Phase 1b -> re-resolve conformances");

        // Phase 2: Resolve type bodies across ALL files (members can reference types from other files)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            _typeBodyResolver.ResolveTypeBodies(program: program);
            _signatureResolver.ResolveAndRegisterPendingRoutines(filterFilePath: filePath);
            _signatureResolver.ResolveExternalSignatures(program: program);
        }

        Mark(label: "Phase 2 -> Type/signature resolution");

        // Divergent cross-file duplicate constructors: same signature + different body in different
        // files -> last-wins registration silently shadows one (the F64(from:F128) recursion class).
        // Benign identical duplicates (equal BodyHash) were not recorded, so anything here is a real bug.
        foreach ((RoutineInfo first, RoutineInfo second) in _registry.DivergentDuplicateCreators)
        {
            SourceLocation? loc = second.Location ?? first.Location;
            if (loc == null)
            {
                continue;
            }

            ReportError(code: SemanticDiagnosticCode.DuplicateRoutineDefinition,
                message:
                $"Constructor '{second.OwnerType?.Name}({string.Join(separator: ", ", values: second.Parameters.Select(selector: p => p.Type.Name))})' " +
                $"is defined with DIFFERENT bodies in two files ('{first.Location?.FileName}' and " +
                $"'{second.Location?.FileName}'). Registration is last-wins, so one silently shadows the " +
                "other — remove the redundant definition (keep the real one; a same-signature forwarder " +
                "stub self-recurses). Identical duplicates are allowed.",
                location: loc);
        }

        // Reject self-containing value records (incl. cross-file mutual recursion) BEFORE conformance
        // analysis, which computes LlvmType/SizeBytes and would otherwise stack-overflow on the cycle.
        // Bail out entirely on a cycle — every downstream phase computes layout and would crash.
        if (ValidateNoRecursiveValueRecords())
        {
            return new AnalysisResult(Registry: _registry,
                Errors: _errors.ToList(),
                Warnings: UserVisibleWarnings(),
                ParsedLiterals: _parsedLiterals,
                SynthesizedBodies: new Dictionary<string, Statement>(),
                InstantiatedGenericBodies: _instantiatedGenericBodies,
                LiveRoutineKeys: _liveRoutineKeys,
                LiveOwnerTypeNames: _liveOwnerTypeNames,
                MaySuspendRoutineKeys: _maySuspendRoutineKeys);
        }

        // Phase 2 global: once, registry-only -> no per-file import scoping needed
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        Mark(label: "Phase 2 global -> implicit marker conformance");

        // Detect a NON-stdlib `import BuilderQuery` from the build graph directly: UserPrograms is
        // not populated until the Phase-5 sweep below, so the AutoRegisterWiredRoutines UserPrograms
        // scan would see an empty list here and never register the gated entity-list metadata
        // routines. The stdlib imports BuilderQuery everywhere, so only a file OUTSIDE StdlibPath
        // counts as a genuine user opt-in (this preserves the "skip the heavy List[FieldInfo] closure
        // unless the user actually asked for BuilderQuery" optimization).
        _builderQueryUserImportedOverride = DetectUserBuilderQueryImport(files: files);

        // Phase 3 (stub) global: synthesized routine stubs, derived operators, protocol validation.
        // (AutoRegisterWiredRoutines registers the derive templates itself before the everywhere-derive
        // sweep, so lt/le/gt/ge resolve here regardless of phase ordering.)
        AutoRegisterWiredRoutines();
        Mark(label: $"Phase 3 global -> {nameof(AutoRegisterWiredRoutines)}");
        GenerateDerivedOperators();
        Mark(label: $"Phase 3 global -> {nameof(GenerateDerivedOperators)}");
        InferWiredMemberRoutines();
        Mark(label: $"Phase 3 global -> {nameof(InferWiredMemberRoutines)}");
        ValidateProtocolImplementations();
        Mark(label: $"Phase 3 global -> {nameof(ValidateProtocolImplementations)}");

        // Phase 3 pre-file: pre-register error-handling variants before Phase 5 body analysis
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            PreRegisterUserVariants(program: program);
        }

        Mark(label: $"Phase 3 pre-file -> {nameof(PreRegisterUserVariants)}");

        // Phase 3 global (pre-pass): pre-register stdlib failable memberRoutine variants (try_emit, try_recover, etc.)
        // Must run before Phase 5 body analysis and before Phase 4 syntax prepass
        // (ControlFlowLoweringPass generates try_emit calls that Phase 5 must resolve).
        // Memo content: the memo's restored stdlib bodies imply the stdlib variants are already registered
        // in the restored registry (parity with the single-file Analyze gate) — re-registering them is pure
        // warm-compile overhead (~240 ms). A cold compile has no restored bodies ⇒ it does the work.
        if (_memo.WarmStdlibRoutineBodies == null)
        {
            PreRegisterStdlibVariants();
        }

        Mark(label: $"Phase 3 global -> {nameof(PreRegisterStdlibVariants)}");

        // Phase 4 per-file: syntax-only lowering (no type info needed; runs before Phase 5 annotates types)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            RunPhase4SyntaxPrepass(program: program);
        }

        Mark(label: "Phase 4 per-file -> syntax-only desugaring");

        // Phase 5: Analyze bodies per file (expressions need correct import scoping)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            AnalyzeBodies(program: program);
        }

        // Per-file resolution realm is a body-analysis convenience only; global passes (synthesis,
        // instantiation, GMP) re-resolve concrete instantiations and must run at the ambient realm so
        // an explicit `RF::` inside an SF-realm body (the wrapper's `inner: RF::Core.List[T]()`) keeps
        // its RF binding instead of being re-resolved under a leaked SF resolution realm.
        _registry.ResolutionRealm = _registry.AmbientRealm;
        Mark(label: $"Phase 5 per-file -> {nameof(AnalyzeBodies)} (user)");

        // Phase 5 global: synthesized body analysis, modification inference
        AnalyzeSynthesizedBodies();
        Mark(label: $"Phase 5 global -> {nameof(AnalyzeSynthesizedBodies)}");
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors are suppressed from user-visible output -> use 'validate-stdlib' to surface them.
        // STAGE-5 FLIP (pull/(B)): mirror the single-program Analyze() gate (see RunPhase5SemanticAnalysis).
        // Under the demand flip, the collector drives per-file stdlib SA + LOWERING on reach
        // (AnalyzeStdlibProgramOnDemand). Running the eager stdlib SA here sets _eagerStdlibAnalyzed=true, which
        // turns the on-demand hook into a no-op — but the global desugar/postprocess sweep SKIPS stdlib under
        // the flip, so stdlib bodies would end up SA'd-but-NEVER-LOWERED (a raw `me == 0u64` in U64.represent
        // reaching codegen). Skip the eager sweep here for a normal build; the BASE build (SeedAllStdlibRoutines)
        // still needs it (it must DEFINE + lower the whole stdlib, not a demand slice).
        if (SeedAllStdlibRoutines)
        {
            int errorsBeforeStdlib = _errors.Count;
            AnalyzeStdlibBodies();
            Mark(label: $"Phase 5 global -> {nameof(AnalyzeStdlibBodies)}");
            if (_errors.Count > errorsBeforeStdlib)
            {
                _errors.RemoveRange(index: errorsBeforeStdlib,
                    count: _errors.Count - errorsBeforeStdlib);
            }
        }

        EagerSynthesizeAllWrapperForwarders();
        Mark(label: $"Phase 5 global -> {nameof(EagerSynthesizeAllWrapperForwarders)}");

        // Failability inference: recompute RoutineInfo.IsFailable from throw/absent + propagated
        // failable callees now that all bodies (user + stdlib + synthesized) are analyzed, BEFORE
        // variant generation and codegen key the failable-carrier ABI on it.
        InferFailableRoutines();
        Mark(label: $"Phase 5 global -> {nameof(InferFailableRoutines)}");

        // If SA produced errors in user code, skip desugaring. Lowering passes over a broken
        // AST produce garbage types and can drive GenericMonomorphizationPass's fixed-point loop
        // with <error>-typed instances. The CLI driver aborts on any errors.
        if (_errors.Count > 0)
        {
            return new AnalysisResult(Registry: _registry,
                Errors: _errors.ToList(),
                Warnings: UserVisibleWarnings(),
                ParsedLiterals: _parsedLiterals,
                SynthesizedBodies: new Dictionary<string, Statement>(),
                InstantiatedGenericBodies: _instantiatedGenericBodies,
                LiveRoutineKeys: _liveRoutineKeys,
                LiveOwnerTypeNames: _liveOwnerTypeNames,
                MaySuspendRoutineKeys: _maySuspendRoutineKeys);
        }

        foreach ((Program program, string filePath) in files)
        {
            string moduleName = moduleNameSnapshots.GetValueOrDefault(key: filePath) ?? "";
            _registry.RegisterUserProgram(program: program,
                filePath: filePath,
                module: moduleName);
        }

        // Phase 5 global: compute type liveness — mark which concrete generic instances are
        // actually reachable from routine signatures.  Must run before Phase 6 global desugaring so that
        // WiredRoutinePass and GMP only operate on live types, preventing phantom instantiations
        // (e.g. BTreeListNode[None]) from reaching codegen.
        new TypeLivenessPass(registry: _registry).Run();
        Mark(label: $"Phase 5 global -> {nameof(TypeLivenessPass)}");

        if (!SaOnly)
        {
            RunMultipleFullPipeline(files: files,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots,
                mark: Mark);
        }

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized2 = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized2[key: key] = variantBody;
        }

        return new AnalysisResult(Registry: _registry,
            Errors: _errors.ToList(),
            Warnings: UserVisibleWarnings(),
            ParsedLiterals: _parsedLiterals,
            SynthesizedBodies: allSynthesized2,
            InstantiatedGenericBodies: _instantiatedGenericBodies,
            LiveRoutineKeys: _liveRoutineKeys,
            LiveOwnerTypeNames: _liveOwnerTypeNames,
            MaySuspendRoutineKeys: _maySuspendRoutineKeys);
    }

    /// <summary>
    /// Runs phases 6–9 of the multi-file pipeline (global desugaring, instantiation, per-file
    /// postprocessing, shadow demand-collection, and post-desugar checks). Called from
    /// <see cref="AnalyzeMultiple"/> only when <see cref="SaOnly"/> is false.
    /// </summary>
    private void RunMultipleFullPipeline(List<(Program Program, string FilePath)> files,
        Dictionary<string, HashSet<string>> importSnapshots,
        Dictionary<string, HashSet<string>> symbolNameSnapshots,
        Dictionary<string, string?> moduleNameSnapshots, Action<string> mark)
    {
        // Phase 6 global: error handling variants + global desugaring (runs once)
        CollectStdlibBodiesForVariantGeneration();
        mark(obj: $"Phase 6 global -> {nameof(CollectStdlibBodiesForVariantGeneration)}");
        RunPhase6GlobalDesugaring();
        mark(obj: $"Phase 6 global -> {nameof(RunPhase6GlobalDesugaring)}");
        RunPhase7Instantiation();
        mark(obj: "Phase 7 -> Instantiation (monomorphization)");

        // Phase 8 per-file: type-aware lowering on verified, type-annotated AST
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            RunPhase8Postprocessing(program: program);
        }

        mark(obj: "Phase 8 per-file -> type-aware postprocessing");

        // Stage ② of the pull architecture, in SHADOW: all programs are now fully lowered
        // (subscript/operator → real call expressions), so the demand collector can walk from
        // the entry points. Flag-gated — a no-op in normal builds; builds into an isolated copy.
        RunShadowCollectorIfNeeded();

        RunPhase9PostDesugarChecks();
        mark(obj: $"Phase 9 -> PostDesugarChecks");
        FinalizeReturnTypes();
        mark(obj: $"Phase 9 -> {nameof(FinalizeReturnTypes)}");
    }

    /// <summary>
    /// Runs the pull-architecture shadow demand-collector when the instantiation context
    /// (<see cref="_shadowCtx"/>) is available. Materializes per-owner synthesized bodies in
    /// <c>InstantiatedGenericBodies</c> from the full synthesized-body source set, then
    /// re-snapshots the live-routine keys so codegen's liveness gate admits the freshly-built bodies.
    /// No-op in normal builds.
    /// </summary>
    private void RunShadowCollectorIfNeeded()
    {
        if (_shadowCtx == null)
        {
            return;
        }

        var synthSources = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body,
            comparer: StringComparer.Ordinal);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            synthSources[key: key] = variantBody;
        }

        new RoutineCollectionPass(ctx: _shadowCtx).RunCollect(synthesizedBodies: synthSources);
        _liveRoutineKeys = _shadowCtx.LiveRoutineKeys.ToArray();
        _liveOwnerTypeNames = _shadowCtx.LiveOwnerTypeNames.ToArray();
    }

    /// <summary>
    /// Analyzes all synthesized AST bodies (derived operators registered in _synthesizedBodies).
    /// Provides semantic validation for bodies produced by GenerateDerivedOperators.
    /// </summary>
    private void AnalyzeSynthesizedBodies()
    {
        foreach ((string _, (RoutineInfo Routine, Statement Body) pair) in _synthesizedBodies)
        {
            AnalyzeCompilerGeneratedBody(routineInfo: pair.Routine,
                body: pair.Body,
                preservePresetTypes: true);
        }
    }

    /// <summary>
    /// Analyzes all error-handling variant bodies in the context of their registered RoutineInfo.
    /// These bodies are compiler-generated, but they still need full semantic annotation before
    /// the type-aware postprocessing pipeline rewrites operators and expressions.
    /// </summary>
    private void AnalyzeVariantBodies()
    {
        // Build the bodies of every on-demand-synthesized variant reached so far (+ transitive) before
        // analyzing them. Snapshot the keys: analysis of a variant body can resolve an inner variant call
        // that synthesizes+enqueues yet another base, so drain again afterwards until fixed.
        DrainVariantBodyGenQueue();
        foreach (string key in _variantBodies.Keys.ToList())
        {
            Statement body = _variantBodies[key: key];
            // Warm-restore: variants captured from the snapshot were already analyzed at capture time.
            if (_memo.RestoredVariantKeys.Contains(item: key))
            {
                continue;
            }

            RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: key) ?? _registry
               .GetAllRoutines()
               .FirstOrDefault(predicate: r => r.RegistryKey == key);
            if (routineInfo == null)
            {
                continue;
            }

            AnalyzeCompilerGeneratedBody(routineInfo: routineInfo, body: body);
        }
    }

    /// <summary>
    /// Analyzes a single compiler-generated AST body in the context of its RoutineInfo.
    /// Sets up scope and parameters identically to AnalyzeFunctionBody, but skips
    /// validation that doesn't apply to compiler-generated code.
    /// </summary>
    private void AnalyzeCompilerGeneratedBody(RoutineInfo routineInfo, Statement body,
        bool preservePresetTypes = false)
    {
        string previousFilePath = _currentFilePath;
        var previousImports = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        var previousSymbols = new HashSet<string>(collection: _importedSymbolNames,
            comparer: StringComparer.Ordinal);
        string? previousModuleName = _currentModuleName;

        bool importRestored = TryRestoreImportStateForRoutine(routineInfo: routineInfo);
        if (!importRestored)
        {
            // Single-file path: no snapshot for stdlib files -> set up a minimal import
            // state so SA can resolve Core type annotations (S128, U32, etc.) in variant bodies.
            _importedModules.Add(item: "Core");
            if (!string.IsNullOrEmpty(value: routineInfo.Module))
            {
                _importedModules.Add(item: routineInfo.Module);
                int dotIdx = routineInfo.Module.IndexOf(value: '.');
                if (dotIdx > 0)
                {
                    _importedModules.Add(item: routineInfo.Module[..dotIdx]);
                }
            }
        }

        // BuilderQuery per-type entity-list routines (member_variable_info / protocol_info / routine_info)
        // synthesize bodies that construct FieldInfo/ProtocolInfo/RoutineInfo/Visibility values — all in
        // the BuilderQuery module. Their owner is a user/stdlib type, so the import scope restored above
        // does NOT include BuilderQuery; without this those constructors resolve to ErrorType (silently,
        // since synthesized-body errors are suppressed below) and the body is dropped, surfacing as an
        // over-prune "declared and called but never defined" at codegen. Make BuilderQuery visible.
        if (BuilderInfoProvider.IsBuilderQueryRoutine(name: routineInfo.Name))
        {
            _importedModules.Add(item: "BuilderQuery");
        }

        // Analyze the compiler-generated body in its OWNER's module, not whatever module happens to be
        // current when the body is first made live. A synthesized failable variant of a Core routine
        // (e.g. `Decimal.try_logb`) references Core module-private `secret` types (`U512`); resolved
        // under a user module those fail the secret-visibility check and silently become ErrorType,
        // which surfaces later as codegen "Error type found in codegen" for the variant — an
        // order/liveness-dependent (CI-flaky) failure. Pin the module to the routine's owner so
        // same-module secret types resolve.
        string? ownerModule = routineInfo.OwnerType?.Module ?? routineInfo.Module;
        if (!string.IsNullOrEmpty(value: ownerModule))
        {
            _currentModuleName = ownerModule;
        }

        RoutineInfo? prevRoutine = _currentRoutine;
        TypeSymbol? prevType = _currentType;
        _currentRoutine = routineInfo;
        _currentType = routineInfo.OwnerType;

        _registry.EnterScope(kind: ScopeKind.Function, name: routineInfo.Name);

        foreach (ParameterInfo param in routineInfo.Parameters)
        {
            _registry.DeclareVariable(name: param.Name, type: param.Type);
        }

        // Suppress errors for synthesized bodies -> they are compiler-generated and correct by construction.
        // Any error indicates a compiler bug, not user code error, so we don't surface them.
        // _isInCompilerGeneratedBody bypasses the wired-routine direct-call guard so SA can fully
        // annotate ResolvedType on all nodes (needed by CallOverloadResolutionPass later).
        bool prevIsInCompilerGeneratedBody = _isInCompilerGeneratedBody;
        bool prevPreservePresetTypes = _preservePresetTypes;
        _isInCompilerGeneratedBody = true;
        _preservePresetTypes = preservePresetTypes;
        int errorsBefore = _errors.Count;
        AnalyzeStatement(statement: body);
        if (_errors.Count > errorsBefore)
        {
            _errors.RemoveRange(index: errorsBefore, count: _errors.Count - errorsBefore);
        }

        _isInCompilerGeneratedBody = prevIsInCompilerGeneratedBody;
        _preservePresetTypes = prevPreservePresetTypes;

        _registry.ExitScope();
        _currentRoutine = prevRoutine;
        _currentType = prevType;
        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(item: ns);
        }

        _importedSymbolNames.Clear();
        foreach (string symbol in previousSymbols)
        {
            _importedSymbolNames.Add(item: symbol);
        }
    }

    /// <summary>
    /// Phase 7: Sets ReturnType = None for every routine still carrying null after all analysis.
    /// Null is a transient "not yet inferred" state. Stdlib routines without a return type
    /// annotation never go through AnalyzeFunctionBody, so they keep null permanently unless
    /// this pass runs.
    /// </summary>
    private void FinalizeReturnTypes()
    {
        TypeSymbol? blank = _registry.LookupType(name: "None");
        if (blank == null)
        {
            return;
        }

        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            routine.ReturnType ??= blank;
        }
    }

    /// <summary>
    /// Restores per-file import state (_currentFilePath, _importedModules, _importedSymbolNames, _currentModuleName)
    /// from previously captured snapshots.
    /// </summary>
    private void RestoreImportState(string filePath,
        Dictionary<string, HashSet<string>> importSnapshots,
        Dictionary<string, HashSet<string>> symbolNameSnapshots,
        Dictionary<string, string?>? moduleNameSnapshots = null)
    {
        _currentFilePath = filePath;
        _importedModules.Clear();
        _importedSymbolNames.Clear();
        _importedForeignAliases.Clear();
        _currentModuleName = null;

        // Prefer this file's own realm when resolving its bare type names. A user `.sf` file's bare
        // `List` resolves to the SF-realm (bridged) `Core.List` — the approachable SF wrapper — while an
        // `.rf` file keeps ambient RF resolution. Mirrors the stdlib-body loop; zero effect on RF
        // (resolution realm == ambient there). Restored to AmbientRealm by the caller after the phase.
        _registry.ResolutionRealm = filePath.EndsWith(value: ".sf",
            comparisonType: StringComparison.OrdinalIgnoreCase)
            ? "SF"
            : _registry.AmbientRealm;

        if (importSnapshots.TryGetValue(key: filePath, value: out HashSet<string>? imports))
        {
            foreach (string module in imports)
            {
                _importedModules.Add(item: module);
            }
        }

        if (symbolNameSnapshots.TryGetValue(key: filePath, value: out HashSet<string>? symbols))
        {
            foreach (string symbol in symbols)
            {
                _importedSymbolNames.Add(item: symbol);
            }
        }

        if (moduleNameSnapshots != null &&
            moduleNameSnapshots.TryGetValue(key: filePath, value: out string? moduleName))
        {
            _currentModuleName = moduleName;
        }

        if (_foreignAliasSnapshots.TryGetValue(key: filePath, value: out HashSet<string>? aliases))
        {
            foreach (string alias in aliases)
            {
                _importedForeignAliases.Add(item: alias);
            }
        }
    }

    /// <summary>
    /// Performs the capture current import state snapshot step for this compiler phase.
    /// </summary>
    private void CaptureCurrentImportStateSnapshot(string filePath)
    {
        _importSnapshots[key: filePath] = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        _symbolNameSnapshots[key: filePath] = new HashSet<string>(collection: _importedSymbolNames,
            comparer: StringComparer.Ordinal);
        _moduleNameSnapshots[key: filePath] = _currentModuleName;
        _foreignAliasSnapshots[key: filePath] =
            new HashSet<string>(collection: _importedForeignAliases,
                comparer: StringComparer.Ordinal);
    }

    /// <summary>
    /// Attempts to restore import state for routine and reports whether it succeeded.
    /// </summary>
    private bool TryRestoreImportStateForRoutine(RoutineInfo routineInfo)
    {
        string? locationFile = routineInfo.Location?.FileName;
        if (string.IsNullOrWhiteSpace(value: locationFile))
        {
            return false;
        }

        string? matchedFilePath = ResolveSnapshotFilePath(locationFile: locationFile);
        if (matchedFilePath == null)
        {
            return false;
        }

        RestoreImportState(filePath: matchedFilePath,
            importSnapshots: _importSnapshots,
            symbolNameSnapshots: _symbolNameSnapshots,
            moduleNameSnapshots: _moduleNameSnapshots);
        return true;
    }

    /// <summary>
    /// Resolves the snapshot file path from semantic compiler state.
    /// </summary>
    private string? ResolveSnapshotFilePath(string locationFile)
    {
        if (_importSnapshots.ContainsKey(key: locationFile))
        {
            return locationFile;
        }

        string locationFileName = Path.GetFileName(path: locationFile);
        return _importSnapshots.Keys.FirstOrDefault(predicate: candidate =>
            string.Equals(a: Path.GetFileName(path: candidate),
                b: locationFileName,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gets the type registry after analysis.
    /// </summary>
    public TypeRegistry Registry => _registry;

    /// <summary>
    /// Gets all errors collected during analysis.
    /// </summary>
    public List<SemanticError> Errors => _errors;

    /// <summary>
    /// Gets all warnings collected during analysis.
    /// </summary>
    public List<SemanticWarning> Warnings => _warnings;

    #endregion

    #region Error Reporting

    /// <summary>
    /// Reports a semantic error with a diagnostic code.
    /// </summary>
    /// <param name="code">The diagnostic code for this error.</param>
    /// <param name="message">The error message.</param>
    /// <param name="location">The source location of the error.</param>
    internal void ReportError(SemanticDiagnosticCode code, string message, SourceLocation location)
    {
        AddError(error: new SemanticError(Code: code, Message: message, Location: location));
    }

    /// <summary>
    /// Reports a semantic warning with a diagnostic code.
    /// </summary>
    /// <param name="code">The diagnostic code for this warning.</param>
    /// <param name="message">The warning message.</param>
    /// <param name="location">The source location of the warning.</param>
    internal void ReportWarning(SemanticWarningCode code, string message, SourceLocation location)
    {
        if (SuppressedWarnings.Contains(item: code))
        {
            return;
        }

        AddWarning(warning: new SemanticWarning(Code: code, Message: message, Location: location));
    }

    private static readonly HashSet<SemanticWarningCode> SuppressedWarnings = new()
    {
        SemanticWarningCode.UnusedRoutineReturnValue,
        SemanticWarningCode.UnhandledCrashableCall
    };

    #endregion

    #region Type Resolution Delegation Stubs

    /// <summary>Resolves a type expression. Delegates to <see cref="TypeResolver"/>.</summary>
    public TypeSymbol ResolveType(TypeExpression? typeExpr)
    {
        return _typeResolver.ResolveType(typeExpr: typeExpr);
    }

    /// <summary>Looks up a type by name, searching imported modules. Delegates to <see cref="TypeResolver"/>.</summary>
    internal TypeSymbol? LookupTypeWithImports(string name)
    {
        return _typeResolver.LookupTypeWithImports(name: name);
    }

    /// <summary>Returns true if name is a generic type parameter in the current context. Delegates to <see cref="TypeResolver"/>.</summary>
    internal bool IsGenericParameter(string name)
    {
        return _typeResolver.IsGenericParameter(name: name);
    }

    /// <summary>True when name is a GENUINE parameter of a generic-definition scope (so it shadows a
    /// same-named global type). Delegates to <see cref="TypeResolver"/>.</summary>
    internal bool IsGenericDefinitionScopeParam(string name)
    {
        return _typeResolver.IsGenericDefinitionScopeParam(name: name);
    }

    /// <summary>The 0-based positional slot of the in-scope generic parameter. Delegates to <see cref="TypeResolver"/>.</summary>
    internal int GenericParameterSlot(string name)
    {
        return _typeResolver.GenericParameterSlot(name: name);
    }

    /// <summary>Resolves a type expression in a protocol context (handles 'Me'). Delegates to <see cref="TypeResolver"/>.</summary>
    internal TypeSymbol ResolveProtocolType(TypeExpression? typeExpr)
    {
        return _typeResolver.ResolveProtocolType(typeExpr: typeExpr);
    }

    /// <summary>Looks up a routine by name, searching Core and imported modules. Delegates to <see cref="TypeResolver"/>.</summary>
    internal RoutineInfo? LookupRoutineWithImports(string name)
    {
        return _typeResolver.LookupRoutineWithImports(name: name);
    }

    /// <summary>Validates that type arguments satisfy generic constraints. Delegates to <see cref="TypeResolver"/>.</summary>
    internal void ValidateGenericConstraints(TypeSymbol genericDef, List<TypeSymbol> typeArgs,
        SourceLocation location)
    {
        _typeResolver.ValidateGenericConstraints(genericDef: genericDef,
            typeArgs: typeArgs,
            location: location);
    }

    #endregion

    #region Helper memberRoutines

    /// <summary>
    /// Walks the current scope chain and returns the fully-qualified module name
    /// for the scope being analyzed, or null if analysis is not inside any module scope.
    /// </summary>
    internal string? GetCurrentModuleName()
    {
        Scope? current = _registry.CurrentScope;
        var namespaces = new List<string>();

        while (current != null)
        {
            if (current is { Kind: ScopeKind.Module, Name: not null })
            {
                namespaces.Insert(index: 0, item: current.Name);
            }

            current = current.Parent;
        }

        return namespaces.Count > 0
            ? string.Join(separator: ".", values: namespaces)
            : _currentModuleName;
    }

    #endregion

    #region Pending Routine

    /// <summary>
    /// A routine declaration collected in Phase 3/4, pending resolution and registration in Phase 4.1.
    /// </summary>
    internal sealed record PendingRoutine(
        RoutineDeclaration Declaration,
        TypeSymbol? OwnerType,
        RoutineKind Kind,
        string RoutineName,
        string? Module,
        string FilePath);

    #endregion
}
