using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Postprocessing;
using Compiler.Postprocessing.Passes;
using Compiler.Resolution;
using Compiler.Synthesis;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;
using Verification.Results;
using Verification.Scopes;

namespace Verification;

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

    /// <summary>Call graph for modification inference.</summary>
    private readonly CallGraph _callGraph = new();
    private MarkerProtocolDesugarPass? _markerPass;

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
            _errors.Add(item: error);
    }

    /// <summary>Adds a warning unless an identical one (same code/message/location) was already recorded.</summary>
    private void AddWarning(SemanticWarning warning)
    {
        if (_seenWarnings.Add(item: warning))
            _warnings.Add(item: warning);
    }

    /// <summary>
    /// Warnings visible to a user build: stdlib-internal warnings are excluded (surface them with
    /// the <c>validate-stdlib</c> verb instead). EVERY AnalysisResult must use this — passing the
    /// raw <c>_warnings</c> list leaks stdlib style warnings (e.g. RF-W258) into user output.
    /// </summary>
    private List<SemanticWarning> UserVisibleWarnings() =>
        _warnings
            .Where(predicate: w => !string.IsNullOrEmpty(value: w.Location.FileName)
                                && !IsStdlibFile(filePath: w.Location.FileName))
            .ToList();

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

    /// <summary>Per-file import snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, HashSet<string>> _importSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file imported symbol snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, HashSet<string>> _symbolNameSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-file module-name snapshots used when re-analyzing compiler-generated bodies.</summary>
    private readonly Dictionary<string, string?> _moduleNameSnapshots =
        new(comparer: StringComparer.OrdinalIgnoreCase);

    /// <summary>Nesting depth for conditional expressions (for #145 deep nesting warning).</summary>
    private int _conditionalNestingDepth;

    /// <summary>Tracks the last variant variable declared, for immediate dismantling check (#58).</summary>
#pragma warning disable CS0414
    private (string Name, SourceLocation Location)? _lastDeclaredVariantVar;
#pragma warning restore CS0414

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
    private readonly Dictionary<string, int> _sharedHandleIdentity = new(StringComparer.Ordinal);

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
    private IReadOnlyDictionary<string, MonomorphizedBody> _instantiatedGenericBodies =
        new Dictionary<string, MonomorphizedBody>();

    /// <summary>
    /// Reachable routine keys produced by <see cref="RoutineReachabilityPass"/>.
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
    private readonly bool _snapshotMode;

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
        _snapshotMode = true;
    }

    /// <summary>
    /// Captures a pre-analyzed stdlib snapshot for the given language.
    /// Runs a full SA initialization (including stdlib body analysis) on a minimal program,
    /// then returns the registry snapshot for fast-restore in subsequent test instances.
    /// </summary>
    public static TypeRegistry.StdlibSnapshot CaptureStdlibSnapshot(Language language)
    {
        var sa = new SemanticVerifier(language: language) { SaOnly = true };
        var tokens = new Compiler.Tokenizer.Tokenizer(
            source: "module __snapshot__",
            fileName: "__snapshot__",
            language: language).Tokenize();
        var parser = new Compiler.Parser.Parser(
            tokens: tokens,
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
    /// When true, stops after Phase 4 (semantic verification) and skips Phase 6 global
    /// desugaring, Phase 7 instantiation, Phase 8 postprocessing, and Phase 9 checks.
    /// Use for tests that only assert on SA errors or type annotations — saves ~10× time
    /// by avoiding monomorphization and lowering passes.
    /// </summary>
    public bool SaOnly { get; set; }

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
        _currentFilePath = filePath ?? program.Location.FileName;
        _currentModuleName = null;
        _importedModules.Clear();
        _importedSymbolNames.Clear();

        bool saTiming = SaTiming;
        var swPhase = Stopwatch.StartNew();
        void Mark(string label)
        {
            if (!saTiming) return;
            swPhase.Stop();
            Console.Error.WriteLine(value: $"[SA] {label}: {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        // Phase numbers match the folder prefix (01.Tokenizer=1 … 10.CodeGen=10).
        // Execution order differs from folder order for phases 5–7: Phase 6 stub synthesis and
        // Phase 7 syntax-only prepass must run BEFORE Phase 5 body analysis so their stubs are
        // in scope. Phase 5 TypeLiveness only runs on the multi-file path (see AnalyzeMultiple).
        // NOTE: Phase 10 (CodeGen) uses "Stage 1–4" for its internal emit stages; MutationInference
        // uses "Step 1–3" for its internal propagation steps — neither conflicts with these phase numbers.
        RunPhase3Declaration(program: program);
        Mark(label: "Phase 3 Declaration");
        CaptureCurrentImportStateSnapshot(filePath: _currentFilePath);
        RunPhase4Resolution(program: program);
        Mark(label: "Phase 4 Resolution");
        RunPhase6StubSynthesis(program: program);
        Mark(label: "Phase 6 stub synthesis (pre-pass)");
        RunPhase7SyntaxPrepass(program: program);
        Mark(label: "Phase 7 syntax prepass");
        RunPhase5Verification(program: program);
        Mark(label: "Phase 5 Verification");
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

        if (!SaOnly)
        {
            CollectStdlibBodiesForVariantGeneration();
            Mark(label: "CollectStdlibBodies");
            RunPhase6GlobalDesugaring();
            Mark(label: "Phase 6 GlobalDesugaring");
            RunPhase8Instantiation();
            Mark(label: "Phase 8 Instantiation");
            RunPhase9Postprocessing(program: program);
            Mark(label: "Phase 9 Postprocessing");
            SurveyMarkerProtocolLeaks();
            RunPhase9PostDesugarChecks();
            Mark(label: "Phase 9 PostDesugarChecks");
            FinalizeReturnTypes();
            Mark(label: "FinalizeReturnTypes");
        }

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized[key] = variantBody;
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

    /// <summary>Phase 3: Collect all type shapes and routine stubs -> no names resolved.</summary>
    private void RunPhase3Declaration(Program program)
    {
        CollectDeclarations(program: program);
    }

    /// <summary>Phase 4: Resolve all bare names to qualified types.</summary>
    private void RunPhase4Resolution(Program program)
    {
        _typeBodyResolver.ResolveTypeBodies(program: program);
        _signatureResolver.ResolveAndRegisterPendingRoutines();
        _signatureResolver.ResolveExternalSignatures(program: program);
        // Reject self-containing value records BEFORE conformance analysis, which computes
        // LlvmType/SizeBytes and would otherwise stack-overflow on the cycle.
        if (!ValidateNoRecursiveValueRecords())
            _conformanceAnalyzer.ApplyImplicitMarkerConformance();
    }

    /// <summary>
    /// Phase 6 (stub pre-pass): Generate synthesized wired routines and derived operators.
    /// Runs before Phase 5 body analysis so stubs are in scope.
    /// Structural routines (represent/hash/eq/diagnose) remain as IsSynthesized stubs.
    /// Derived operators (ne/lt/le/gt/ge/notcontains) have real AST bodies stored in _synthesizedBodies.
    /// </summary>
    private void RunPhase6StubSynthesis(Program program)
    {
        AutoRegisterWiredRoutines();
        GenerateDerivedOperators();
        // Phase 4.1 (signature resolution) and all member-routine registration are now complete; re-derive the
        // wired attribute the parser no longer sets from the surface `$` sigil.
        InferWiredMemberRoutines();
        ValidateProtocolImplementations();
        PreRegisterUserVariants(program: program);
        // Snapshot mode: stdlib variants are already registered in the restored registry.
        if (!_snapshotMode) PreRegisterStdlibVariants();
    }

    /// <summary>
    /// Phase 5: Type-annotate and verify all routine bodies.
    /// Runs after Phase 6/7 stub pre-passes because body analysis needs stubs in scope;
    /// runs before Phase 6 main pass because synthesis needs type-annotated AST.
    /// </summary>
    private void RunPhase5Verification(Program program)
    {
        AnalyzeBodies(program: program);
        AnalyzeSynthesizedBodies();
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors and warnings are suppressed from user-visible output -> use 'validate-stdlib' to surface them.
        // Snapshot mode: the RESTORED stdlib bodies were analyzed during snapshot capture; AnalyzeStdlibBodies
        // now iterates only the FRESHLY-loaded programs (cold: all; warm: on-demand imports), so it is a
        // no-op for the restored set and correctly analyzes modules the warm compile imported on-demand.
        {
            int errorsBeforeStdlib = _errors.Count;
            int warningsBeforeStdlib = _warnings.Count;
            AnalyzeStdlibBodies();
            if (_errors.Count > errorsBeforeStdlib)
                _errors.RemoveRange(index: errorsBeforeStdlib,
                    count: _errors.Count - errorsBeforeStdlib);
            if (_warnings.Count > warningsBeforeStdlib)
                _warnings.RemoveRange(index: warningsBeforeStdlib,
                    count: _warnings.Count - warningsBeforeStdlib);
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
        var swSub = SaTiming ? Stopwatch.StartNew() : null;
        void SubMark(string label)
        {
            if (swSub == null) return;
            swSub.Stop();
            Console.Error.WriteLine(value: $"[SA]     P6sub - {label}: {swSub.ElapsedMilliseconds} ms");
            swSub.Restart();
        }

        var ctx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode) { VariantBodies = _variantBodies };
        new DesugaringPipeline(ctx: ctx).RunGlobal();
        SubMark(label: "DesugaringPipeline.RunGlobal");
        // Capture variant bodies produced by ErrorHandlingVariantPass for codegen. On the warm-restore
        // path _variantBodies is pre-seeded with the captured stdlib variants, so ErrorHandlingVariantPass
        // only ADDS user variants here — the seeded restored ones survive for codegen.
        _variantBodies = ctx.VariantBodies;
        AnalyzeVariantBodies();
        SubMark(label: "AnalyzeVariantBodies");

        // Phase 8 global: lower variant bodies and stdlib programs with type-aware passes.
        // Also pass synthesized operator bodies so CallOverloadResolutionPass can classify
        // the CallExpression nodes inside them (LoweringKind = Unknown otherwise).
        var synthesizedBodyStatements = _synthesizedBodies
            .ToDictionary(keySelector: kvp => kvp.Key, elementSelector: kvp => kvp.Value.Body);
        // Phase 6.5: re-run wired-routine synthesis to catch tuple types (and any other
        // lazily-registered types) created during Phase 4 SA. The original Phase 6 sweep
        // could not see these because they did not yet exist in the registry.
        // Re-run AutoRegisterWiredRoutines first so user variants (registered in Phase 3
        // per-file via PreRegisterUserVariants, AFTER the Phase 3 global AutoRegister sweep)
        // get their represent/diagnose stubs registered before WiredRoutinePass synthesizes
        // bodies. MaybeRegisterWired is idempotent on existing memberRoutines.
        AutoRegisterWiredRoutines();
        var lateCtx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode) { VariantBodies = _variantBodies };
        new WiredRoutinePass(ctx: lateCtx).RunGlobal();
        SubMark(label: "AutoRegisterWiredRoutines + WiredRoutinePass.RunGlobal");

        var p7ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            synthesizedBodies: synthesizedBodyStatements,
            target: _target,
            buildMode: _buildMode,
            monomorphizedBodies: _instantiatedGenericBodies as Dictionary<string, MonomorphizedBody>);
        new PostprocessingPipeline(ctx: p7ctx).RunGlobal();
        SubMark(label: "PostprocessingPipeline.RunGlobal");
    }

    /// <summary>
    /// Phase 7: close reachable generic bodies up front so codegen no longer owns the
    /// common-case monomorphization entry point.
    /// </summary>
    private void RunPhase8Instantiation()
    {
        // Include wrapper forwarder bodies in variantBodies so GMP can rewrite them with
        // concrete type substitutions. Without this, GMP creates empty-body sentinels for
        // concrete forwarder instances instead of properly monomorphized bodies.
        var mergedVariantBodies = new Dictionary<string, Statement>(_variantBodies);
        foreach (var (key, pair) in _synthesizedBodies)
        {
            // Include wrapper forwarders AND derived operators on generic owner types.
            // GMP must monomorphize both; Phase C must not emit the generic-def version.
            if (pair.Routine.WrapperForwarderInnerMemberRoutine != null ||
                pair.Routine.OwnerType?.IsGenericDefinition == true)
                mergedVariantBodies[key] = pair.Body;
        }

        var ctx = new InstantiationContext(registry: _registry,
            userPrograms: _registry.UserPrograms,
            routineBodies: _routineBodies,
            variantBodies: mergedVariantBodies,
            instantiatedGenericBodies: _instantiatedGenericBodies is Dictionary<string, MonomorphizedBody> dict
                ? dict
                : _instantiatedGenericBodies.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            target: _target,
            buildMode: _buildMode) { SaTiming = SaTiming };

        // Rewrite Accessing[T]/Controlling[T] params to inner T before reachability so
        // the resulting RegistryKeys / mangled names captured downstream match codegen.
        // Call-site refer/control coercion was already injected during SA argument binding.
        // Pass mergedVariantBodies (not _variantBodies) so the dict used by reachability/GMP
        // gets re-keyed to the post-mutation form.
        var markerCtx = new PostprocessingContext(registry: _registry,
            variantBodies: mergedVariantBodies,
            synthesizedBodies: _synthesizedBodies.ToDictionary(
                keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.Body),
            target: _target,
            buildMode: _buildMode);

        // Suflae only: lower `entity E` bindings to a `Roamed[E]` biased-RC backing. MUST run BEFORE
        // scope-teardown below so teardown inserts `Roamed.destroy` (→ release → cycle-collector
        // chain) for entity locals instead of a bare-entity `destroy` (which double-frees an alias
        // and never reaches the RC/cc machinery). Also before reachability, so the roam/promote/lock/
        // cc hooks seed off the live `Roamed` wrapper type. No-op for RazorForge.
        {
            var suflaeEntityPass =
                new SuflaeEntityLoweringPass(registry: _registry);
            foreach ((Program program, _, _) in _registry.UserPrograms)
                suflaeEntityPass.Run(program: program);
        }

        // Insert scope-exit `destroy()` calls BEFORE reachability (so the calls drive liveness —
        // no manual seeding needed) and BEFORE the marker pass (so Accessing[T]/Controlling[T]
        // params are still protocol-typed and excluded as access types, not yet stripped to the
        // inner entity). Generic bodies are processed here too, then monomorphized with the calls.
        // Warm-restore: the restored stdlib programs already carry teardown/temp-teardown/marker/
        // crashable lowering from the capture, so re-running those passes on them would double-apply and
        // diverge. Only the FRESHLY-loaded stdlib programs (cold: all; warm: on-demand imports) need it.
        // Reachability + GMP below still walk the FULL (restored + fresh) StdlibPrograms.
        var freshStdlib = _registry.FreshlyLoadedStdlibPrograms;

        var teardownPass = new ScopeTeardownLoweringPass(markerCtx);
        foreach ((Program program, _, _) in _registry.UserPrograms)
            teardownPass.Run(program: program);
        foreach ((Program program, _, _) in freshStdlib)
            teardownPass.Run(program: program);
        teardownPass.RunOnVariantBodies();

        // Tear down owned RVALUE temporaries (heap-owning receiver/discarded producers) that the
        // binding-only ScopeTeardownLoweringPass cannot see. Runs AFTER teardown so it never
        // double-frees the temps' bindings, and BEFORE reachability so its destroy calls drive
        // liveness. Stdlib + variant bodies are already Phase-8 lowered here (when→if done); USER
        // programs are lowered later (Phase 8 per-file), so they get this pass in RunPhase9Postprocessing.
        var tempTeardownPass = new TemporaryTeardownPass(markerCtx);
        foreach ((Program program, _, _) in freshStdlib)
            tempTeardownPass.Run(program: program);
        tempTeardownPass.RunOnBodies(markerCtx.VariantBodies);

        var markerPass = new MarkerProtocolDesugarPass(markerCtx);
        _markerPass = markerPass;
        markerPass.RewriteAllSignatures();
        foreach ((Program program, _, _) in _registry.UserPrograms)
        {
            MarkerProtocolDesugarPass.RewriteAstSignatures(program);
            markerPass.Run(program);
        }
        foreach ((Program program, _, _) in freshStdlib)
        {
            MarkerProtocolDesugarPass.RewriteAstSignatures(program);
            markerPass.Run(program);
        }
        markerPass.RunOnVariantBodies();
        markerPass.RunOnSynthesizedBodies();

        // Expand `is Crashable err` clauses BEFORE reachability so that the new
        // per-crashable `err.crash_message()` calls participate in liveness analysis.
        // Without this, the fanout happens in Phase 8 and the crash_message memberRoutine on
        // each concrete crashable is never marked reachable -> linker errors.
        {
            var crashablePass = new CrashableExpansionPass(markerCtx);
            foreach ((Program program, _, _) in _registry.UserPrograms)
                crashablePass.Run(program);
            foreach ((Program program, _, _) in freshStdlib)
                crashablePass.Run(program);
        }

        if (SaTiming)
        {
            var sw = Stopwatch.StartNew();
            void Step(string label)
            {
                sw.Stop();
                Console.Error.WriteLine(value: $"[SA]   Phase 7 sub - {label}: {sw.ElapsedMilliseconds} ms");
                sw.Restart();
            }
            new ReachableGenericCollectionPass(ctx: ctx).Run();
            Step(label: "ReachableGenericCollectionPass");
            new RoutineReachabilityPass(ctx: ctx).Run();
            Step(label: "RoutineReachabilityPass");
            new GenericClosurePass(ctx: ctx).Run();
            Step(label: "GenericClosurePass");
            GenericCanonicalizationPass.Run();
            Step(label: "GenericCanonicalizationPass");
        }
        else
        {
            new InstantiationPipeline(ctx: ctx).Run();
        }

        _variantBodies = ctx.VariantBodies;
        _instantiatedGenericBodies = ctx.InstantiatedGenericBodies;
        _liveRoutineKeys = ctx.LiveRoutineKeys.ToArray();
        _liveOwnerTypeNames = ctx.LiveOwnerTypeNames.ToArray();

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
        new CallOverloadResolutionPass(classCtx).RunOnStatements(
            _instantiatedGenericBodies.Values.Select(selector: b => b.Ast.Body));
    }

    /// <summary>
    /// Runs the v0.2.0 may-suspend fixpoint over the call graph that
    /// <see cref="RoutineReachabilityPass"/> populated, storing the result for codegen's 9-2
    /// cancellation instrumentation. Optional <c>RF_MAYSUSPEND_DUMP</c> writes the set for probes.
    /// </summary>
    private void ComputeMaySuspend(InstantiationContext ctx)
    {
        IReadOnlyCollection<string> maySuspend =
            new MaySuspendAnalysis(callGraph: ctx.MaySuspendGraph).Compute().ToArray();
        foreach (string key in maySuspend) ctx.MaySuspendRoutineKeys.Add(item: key);
        _maySuspendRoutineKeys = maySuspend;

        string? dumpPath = Environment.GetEnvironmentVariable(variable: "RF_MAYSUSPEND_DUMP");
        if (!string.IsNullOrEmpty(value: dumpPath))
        {
            var lines = new List<string> { "=== MAY-SUSPEND ROUTINES ===" };
            lines.AddRange(collection: maySuspend.OrderBy(keySelector: s => s));
            File.WriteAllLines(path: dumpPath, contents: lines);
        }
    }

    /// <summary>
    /// Phase 7 (syntax prepass): Syntax-only lowering that requires no type information.
    /// Runs before Phase 5 annotates ResolvedType on expressions.
    /// </summary>
    private void RunPhase7SyntaxPrepass(Program program)
    {
        var ctx = new DesugaringContext(registry: _registry,
            routineBodies: _routineBodies,
            target: _target,
            buildMode: _buildMode);
        new DesugaringPipeline(ctx: ctx).Run(program: program);
    }

    /// <summary>
    /// Phase 9 (per-file): Type-aware lowering on a verified, type-annotated program.
    /// Runs after Phase 5 has annotated ResolvedType on all expressions.
    /// </summary>
    private void RunPhase9Postprocessing(Program program)
    {
        var ctx = new PostprocessingContext(registry: _registry,
            variantBodies: _variantBodies,
            target: _target,
            buildMode: _buildMode,
            monomorphizedBodies: _instantiatedGenericBodies as Dictionary<string, MonomorphizedBody>);
        // Now that Phase 7 has produced the concrete instances, lower any carrier-return sites inside
        // them (a monomorphized try_/check_/lookup_ variant) to real record construction.
        new VariantReturnLoweringPass(ctx).RunOnMonomorphizedBodies();
        // Inline simple iterator `emit!` bodies into their for-loops before the rest of Phase 8
        // lowering, replacing the `try_emit` call with the spliced advance. By Phase 8 the concrete
        // `emit!` bodies are already monomorphized (Phase 7 ran), so the lookup succeeds; the
        // spliced body then flows through the normal Phase 8 lowering below. Composed/filtering
        // iterators fall back to the existing `try_emit` loop.
        new IteratorInlineLoweringPass(
                registry: _registry, monoBodies: _instantiatedGenericBodies)
            .Run(program: program);
        new PostprocessingPipeline(ctx: ctx).Run(program: program);

        // Owned rvalue-temporary teardown for user code, now that Phase 8 has lowered when→if so the
        // producing calls sit in real statements. ScopeTeardownLoweringPass already ran (pre-lowering,
        // step 4) and will not revisit this program, so the temps' bindings are freed exactly once by
        // the destroy calls this pass emits (codegen emit-on-demand resolves the concrete destroy).
        new TemporaryTeardownPass(ctx).Run(program: program);
    }

    /// <summary>
    /// Diagnostic survey: after Phase 7/8 monomorphization, walks every routine in the
    /// registry and reports any RoutineInfo whose Parameters still contain
    /// Accessing[T]/Controlling[T]. Such routines indicate a creation path that bypassed
    /// MarkerProtocolDesugarPass.RewriteAllSignatures — call-site mangling will then
    /// diverge from definition-site mangling and produce LINKERRs.
    /// </summary>
    private void SurveyMarkerProtocolLeaks()
    {
        // This survey's RescanLateResolutions / RewriteInstantiatedBodyInfos below are FUNCTIONAL
        // (they clean the registry + instantiated-body cache) and always run. Its leak REPORT, however,
        // is a developer early-warning aid that writes to stderr; a residual dormant leak
        // (Hijacked[Accessing[List[S64]]] comparison ops) would otherwise pollute every build's stderr
        // and fail the harness's clean-stderr assertion. Gate the prints behind an opt-in env var — the
        // over-prune tripwire in codegen is the real undefined-symbol safety net.
        bool report = Environment.GetEnvironmentVariable(variable: "RF_MARKER_SURVEY") == "1";

        static bool IsMarker(TypeInfo? t)
        {
            if (t is not ProtocolTypeInfo p) return false;
            string n = (p.GenericDefinition ?? p).Name;
            return n is RuntimeContract.Accessing or RuntimeContract.Controlling;
        }

        static bool ContainsMarker(TypeInfo? t, HashSet<TypeInfo> seen)
        {
            if (t == null) return false;
            if (!seen.Add(t)) return false;
            if (IsMarker(t)) return true;
            if (t.TypeArguments is { Count: > 0 } args)
                foreach (TypeInfo a in args)
                    if (ContainsMarker(a, seen)) return true;
            return false;
        }

        int leakCount = 0;
        void Check(IEnumerable<RoutineInfo> rs, string bucket)
        {
            foreach (RoutineInfo r in rs)
            {
                for (int i = 0; i < r.Parameters.Count; i++)
                {
                    if (ContainsMarker(r.Parameters[i].Type, new HashSet<TypeInfo>()))
                    {
                        leakCount++;
                        if (report)
                            Console.Error.WriteLine(
                                $"[MARKER-LEAK] bucket={bucket} routine={r.RegistryKey} " +
                                $"param[{i}]={r.Parameters[i].Name}:{r.Parameters[i].Type?.FullName} " +
                                $"isGenericDef={r.IsGenericDefinition} owner={r.OwnerType?.FullName}");
                        break;
                    }
                }
            }
        }

        _markerPass?.RescanLateResolutions();

        // GMP creates body.Info entries in Phase 7 whose params still wear Accessing/Controlling;
        // RescanLateResolutions cleans the registry but not the codegen-side instantiated-body cache.
        // Rewrite those param types and re-key the dict + live-set so definition emission and
        // call-site mangling agree.
        if (_markerPass != null
            && _instantiatedGenericBodies is Dictionary<string, MonomorphizedBody> bodyDict)
        {
            Dictionary<string, string> bodyKeyMap = _markerPass.RewriteInstantiatedBodyInfos(bodyDict);
            if (bodyKeyMap.Count > 0)
            {
                _liveRoutineKeys = _liveRoutineKeys
                    .Select(selector: k => bodyKeyMap.TryGetValue(k, value: out string? newK) ? newK : k)
                    .ToArray();
            }
        }

        Check(_registry.GetAllRoutines(), "routines");
        Check(_registry.GetAllRoutineResolutions(), "resolutions");
        if (report && leakCount > 0)
            Console.Error.WriteLine($"[MARKER-LEAK] total={leakCount}");
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

        foreach ((Program stdlibProgram, _, _) in _registry.StdlibPrograms)
        {
            reprPass.Run(program: stdlibProgram);
        }

        foreach ((string key, Statement body) in _variantBodies)
        {
            reprPass.Run(statement: body);
            foreach (SemanticError error in validator.ValidateStatement(statement: body))
            {
                AddError(error: error with
                {
                    Message = $"[{key}] {error.Message}"
                });
            }
        }

        foreach ((string key, MonomorphizedBody mono) in _instantiatedGenericBodies)
        {
            if (!mono.IsSynthesized)
            {
                reprPass.Run(statement: mono.Ast.Body);
            }

            foreach (SemanticError error in BackendEntryValidator.ValidateMonomorphizedBody(body: mono))
            {
                AddError(error: error with
                {
                    Message = $"[mono:{key}] {error.Message}"
                });
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
    /// <summary>Runs the <see cref="Compiler.Resolution.RuntimeContractCheck"/> against the loaded
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

        // Pre-register try_/check_/lookup_ stubs for all failable stdlib routines so that
        // stdlib bodies that call try_X (e.g. try_get_by_rank) resolve during body analysis.
        // Uses AST-level detection — no full body analysis or expression lowering required.
        PreRegisterStdlibVariants();

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
        // Analyze only FRESHLY-loaded stdlib programs: in a cold compile that is every stdlib program;
        // in a warm-restore compile the restored bodies were already analyzed at capture, so this is just
        // the modules imported on-demand (e.g. IO/Console) — which still need SA before lowering/codegen.
        var freshStdlibPrograms = _registry.FreshlyLoadedStdlibPrograms;
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
        string? previousModuleName = _currentModuleName;
        int stdlibIdx = 0;
        foreach ((Program program, string filePath, string module) in freshStdlibPrograms)
        {
            stdlibIdx++;
            _currentFilePath = filePath;
            _currentModuleName = module;
            // Prefer this file's own realm when resolving its bare type names: an SF-realm (`.sf`) stdlib
            // file's bare `List` resolves to the SF-realm `Core.List` (bridged) it declares, while an RF file
            // keeps the ambient RF resolution. Zero effect on RF (resolution realm == ambient there).
            _registry.ResolutionRealm =
                filePath.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase) ? "SF" : "RF";
            _importedModules.Clear();
            _importedSymbolNames.Clear();

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

        _currentFilePath = previousFilePath;
        _currentModuleName = previousModuleName;
        _registry.ResolutionRealm = _registry.AmbientRealm;
        _importedModules.Clear();
        foreach (string ns in previousImports)
        {
            _importedModules.Add(item: ns);
        }

        } // try
        finally
        {
            _registry.EndStdlibAnalysis();
            _registry.ResolutionRealm = _registry.AmbientRealm;
            _registry.Language = savedLanguage;
        }
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
        _importSnapshots.Clear();
        _symbolNameSnapshots.Clear();
        _moduleNameSnapshots.Clear();
        bool saTiming = SaTiming;
        var swPhase = Stopwatch.StartNew();
        void Mark(string label)
        {
            if (!saTiming) return;
            swPhase.Stop();
            Console.Error.WriteLine(value: $"[SA] {label}: {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        // Snapshot storage: file path -> imported modules after Phase 1
        var importSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var symbolNameSnapshots =
            new Dictionary<string, HashSet<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        var moduleNameSnapshots =
            new Dictionary<string, string?>(comparer: StringComparer.OrdinalIgnoreCase);

        // Every file in the build graph contributes its declarations via Phase 3 below.
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

        // Phase 3: Collect declarations from ALL files (populates registry with all types/routines)
        foreach ((Program program, string filePath) in files)
        {
            _currentFilePath = filePath;
            _currentModuleName = null;
            _importedModules.Clear();
            _importedSymbolNames.Clear();

            RunPhase3Declaration(program: program);

            importSnapshots[key: filePath] = new HashSet<string>(collection: _importedModules,
                comparer: StringComparer.OrdinalIgnoreCase);
            symbolNameSnapshots[key: filePath] =
                new HashSet<string>(collection: _importedSymbolNames,
                    comparer: StringComparer.Ordinal);
            moduleNameSnapshots[key: filePath] = _currentModuleName;
            CaptureCurrentImportStateSnapshot(filePath: filePath);
        }
        Mark(label: "Phase 3 -> Declarations");

        // Record per-module import lists for BuilderQuery's T.dependencies(). importSnapshots is keyed
        // by file; a module may span files, so accumulate each file's imports under its declared module.
        foreach ((string filePath, HashSet<string> imports) in importSnapshots)
        {
            string? mod = moduleNameSnapshots.GetValueOrDefault(key: filePath);
            if (!string.IsNullOrEmpty(value: mod))
                _registry.AddModuleDependencies(module: mod, imports: imports);
        }

        // Phase 1b: Re-resolve record/entity `obeys` conformances now that ALL files' types AND
        // every referenced (lazily-loaded) protocol are registered. Initial per-file declaration
        // resolution can drop a protocol whose definition wasn't loaded yet — e.g. a user module
        // record obeying a Core protocol (FloorDivisible) registered before that protocol's file
        // loaded — leaving ImplementedProtocols short. Re-resolving here (before signature
        // resolution runs the generic-constraint checks, RF-S150) fills them in.
        foreach ((Program program, string _) in files)
        {
            Compiler.Declaration.StdlibLoader.ResolveProgramProtocolConformances(registry: _registry, program: program);
        }
        Mark(label: "Phase 3b -> re-resolve conformances");

        // Phase 4: Resolve type bodies across ALL files (members can reference types from other files)
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
        Mark(label: "Phase 4 -> Type/signature resolution");

        // Divergent cross-file duplicate constructors: same signature + different body in different
        // files -> last-wins registration silently shadows one (the F64(from:F128) recursion class).
        // Benign identical duplicates (equal BodyHash) were not recorded, so anything here is a real bug.
        foreach ((RoutineInfo first, RoutineInfo second) in _registry.DivergentDuplicateCreators)
        {
            SourceLocation? loc = second.Location ?? first.Location;
            if (loc == null) continue;
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

        // Phase 4 global: once, registry-only -> no per-file import scoping needed
        _conformanceAnalyzer.ApplyImplicitMarkerConformance();
        Mark(label: "Phase 4 global -> implicit marker conformance");

        // Detect a NON-stdlib `import BuilderQuery` from the build graph directly: UserPrograms is
        // not populated until the Phase-5 sweep below, so the AutoRegisterWiredRoutines UserPrograms
        // scan would see an empty list here and never register the gated entity-list metadata
        // routines. The stdlib imports BuilderQuery everywhere, so only a file OUTSIDE StdlibPath
        // counts as a genuine user opt-in (this preserves the "skip the heavy List[FieldInfo] closure
        // unless the user actually asked for BuilderQuery" optimization).
        _builderQueryUserImportedOverride = DetectUserBuilderQueryImport(files: files);

        // Phase 3 global: synthesized routines, derived operators, protocol validation
        AutoRegisterWiredRoutines();
        Mark(label: "Phase 6 global -> AutoRegisterWiredRoutines");
        GenerateDerivedOperators();
        Mark(label: "Phase 6 global -> GenerateDerivedOperators");
        InferWiredMemberRoutines();
        Mark(label: "Phase 6 global -> InferWiredMemberRoutines");
        ValidateProtocolImplementations();
        Mark(label: "Phase 6 global -> ValidateProtocolImplementations");

        // Phase 6 pre-file: pre-register error handling variants before Phase 5 body analysis
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            PreRegisterUserVariants(program: program);
        }
        Mark(label: "Phase 6 pre-file -> PreRegisterUserVariants");

        // Phase 6 global (pre-pass): pre-register stdlib failable memberRoutine variants (try_emit, try_recover, etc.)
        // Must run before Phase 5 body analysis and before Phase 7 syntax prepass
        // (ControlFlowLoweringPass generates try_emit calls that Phase 5 must resolve).
        // Snapshot mode: stdlib variants are already registered in the restored registry (parity with the
        // single-file Analyze gate) — re-registering them is pure warm-compile overhead (~240 ms).
        if (!_snapshotMode) PreRegisterStdlibVariants();
        Mark(label: "Phase 6 global -> PreRegisterStdlibVariants");

        // Phase 7 per-file: syntax-only lowering (no type info needed; runs before Phase 5 annotates types)
        foreach ((Program program, string filePath) in files)
        {
            RestoreImportState(filePath: filePath,
                importSnapshots: importSnapshots,
                symbolNameSnapshots: symbolNameSnapshots,
                moduleNameSnapshots: moduleNameSnapshots);

            RunPhase7SyntaxPrepass(program: program);
        }
        Mark(label: "Phase 7 per-file -> syntax-only desugaring");

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
        Mark(label: "Phase 5 per-file -> AnalyzeBodies (user)");

        // Phase 5 global: synthesized body analysis, modification inference
        AnalyzeSynthesizedBodies();
        Mark(label: "Phase 5 global -> AnalyzeSynthesizedBodies");
        // M-0: Annotate stdlib expression types so desugaring passes can lower stdlib bodies
        // uniformly (OperatorLoweringPass, ExpressionLoweringPass, etc.).
        // Stdlib errors are suppressed from user-visible output -> use 'validate-stdlib' to surface them.
        int errorsBeforeStdlib = _errors.Count;
        AnalyzeStdlibBodies();
        Mark(label: "Phase 5 global -> AnalyzeStdlibBodies");
        if (_errors.Count > errorsBeforeStdlib)
            _errors.RemoveRange(index: errorsBeforeStdlib,
                count: _errors.Count - errorsBeforeStdlib);
        EagerSynthesizeAllWrapperForwarders();
        Mark(label: "Phase 5 global -> EagerSynthesizeAllWrapperForwarders");

        // Failability inference: recompute RoutineInfo.IsFailable from throw/absent + propagated
        // failable callees now that all bodies (user + stdlib + synthesized) are analyzed, BEFORE
        // variant generation and codegen key the failable-carrier ABI on it.
        InferFailableRoutines();
        Mark(label: "Phase 5 global -> InferFailableRoutines");

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
            _registry.RegisterUserProgram(program: program, filePath: filePath, module: moduleName);
        }

        // Phase 5 global: compute type liveness — mark which concrete generic instances are
        // actually reachable from routine signatures.  Must run before Phase 6 synthesis so that
        // WiredRoutinePass and GMP only operate on live types, preventing phantom instantiations
        // (e.g. BTreeListNode[None]) from reaching codegen.
        new TypeLivenessPass(registry: _registry).Run();
        Mark(label: "Phase 5 global -> TypeLivenessPass");

        if (!SaOnly)
        {
            // Phase 6 global: error handling variants + future global passes (runs once)
            CollectStdlibBodiesForVariantGeneration();
            Mark(label: "Phase 6 global -> CollectStdlibBodiesForVariantGeneration");
            RunPhase6GlobalDesugaring();
            Mark(label: "Phase 6 global -> RunPhase6GlobalDesugaring");
            RunPhase8Instantiation();
            Mark(label: "Phase 8 -> Instantiation (monomorphization)");

            // Phase 9 per-file: type-aware lowering on verified, type-annotated AST
            foreach ((Program program, string filePath) in files)
            {
                RestoreImportState(filePath: filePath,
                    importSnapshots: importSnapshots,
                    symbolNameSnapshots: symbolNameSnapshots,
                    moduleNameSnapshots: moduleNameSnapshots);

                RunPhase9Postprocessing(program: program);
            }
            Mark(label: "Phase 9 per-file -> type-aware postprocessing");

            SurveyMarkerProtocolLeaks();
            RunPhase9PostDesugarChecks();
            Mark(label: "Phase 9 -> PostDesugarChecks");
            FinalizeReturnTypes();
            Mark(label: "Phase 9 -> FinalizeReturnTypes");
        }

        // Merge synthesized operator bodies and pre-transformed variant bodies
        var allSynthesized2 = _synthesizedBodies.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.Body);
        foreach ((string key, Statement variantBody) in _variantBodies)
        {
            allSynthesized2[key] = variantBody;
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
    /// Analyzes all synthesized AST bodies (derived operators registered in _synthesizedBodies).
    /// Provides semantic validation for bodies produced by GenerateDerivedOperators.
    /// </summary>
    private void AnalyzeSynthesizedBodies()
    {
        foreach ((string _, (RoutineInfo Routine, Statement Body) pair) in _synthesizedBodies)
        {
            AnalyzeCompilerGeneratedBody(routineInfo: pair.Routine, body: pair.Body,
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
        foreach ((string key, Statement body) in _variantBodies)
        {
            // Warm-restore: variants captured from the snapshot were already analyzed at capture time.
            if (_restoredVariantKeys.Contains(item: key))
            {
                continue;
            }

            RoutineInfo? routineInfo = _registry.LookupRoutine(fullName: key) ??
                _registry.GetAllRoutines()
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
                int dotIdx = routineInfo.Module.IndexOf('.');
                if (dotIdx > 0)
                    _importedModules.Add(item: routineInfo.Module[..dotIdx]);
            }
        }

        // BuilderQuery per-type entity-list routines (member_variable_info / protocol_info / routine_info)
        // synthesize bodies that construct FieldInfo/ProtocolInfo/RoutineInfo/Visibility values — all in
        // the BuilderQuery module. Their owner is a user/stdlib type, so the import scope restored above
        // does NOT include BuilderQuery; without this those constructors resolve to ErrorType (silently,
        // since synthesized-body errors are suppressed below) and the body is dropped, surfacing as an
        // over-prune "declared and called but never defined" at codegen. Make BuilderQuery visible.
        if (BuilderInfoProvider.IsBuilderQueryRoutine(name: routineInfo.Name))
            _importedModules.Add(item: "BuilderQuery");

        // Analyze the compiler-generated body in its OWNER's module, not whatever module happens to be
        // current when the body is first made live. A synthesized failable variant of a Core routine
        // (e.g. `Decimal.try_logb`) references Core module-private `secret` types (`U512`); resolved
        // under a user module those fail the secret-visibility check and silently become ErrorType,
        // which surfaces later as codegen "Error type found in codegen" for the variant — an
        // order/liveness-dependent (CI-flaky) failure. Pin the module to the routine's owner so
        // same-module secret types resolve.
        string? ownerModule = routineInfo.OwnerType?.Module ?? routineInfo.Module;
        if (!string.IsNullOrEmpty(value: ownerModule))
            _currentModuleName = ownerModule;

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
            _importedModules.Add(item: ns);
        _importedSymbolNames.Clear();
        foreach (string symbol in previousSymbols)
            _importedSymbolNames.Add(item: symbol);
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
        _currentModuleName = null;

        // Prefer this file's own realm when resolving its bare type names. A user `.sf` file's bare
        // `List` resolves to the SF-realm (bridged) `Core.List` — the approachable SF wrapper — while an
        // `.rf` file keeps ambient RF resolution. Mirrors the stdlib-body loop; zero effect on RF
        // (resolution realm == ambient there). Restored to AmbientRealm by the caller after the phase.
        _registry.ResolutionRealm =
            filePath.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase)
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
    }

    /// <summary>
    /// Performs the capture current import state snapshot step for this compiler phase.
    /// </summary>
    private void CaptureCurrentImportStateSnapshot(string filePath)
    {
        _importSnapshots[filePath] = new HashSet<string>(collection: _importedModules,
            comparer: StringComparer.OrdinalIgnoreCase);
        _symbolNameSnapshots[filePath] = new HashSet<string>(collection: _importedSymbolNames,
            comparer: StringComparer.Ordinal);
        _moduleNameSnapshots[filePath] = _currentModuleName;
    }

    /// <summary>
    /// Attempts to restore import state for routine and reports whether it succeeded.
    /// </summary>
    private bool TryRestoreImportStateForRoutine(RoutineInfo routineInfo)
    {
        string? locationFile = routineInfo.Location?.FileName;
        if (string.IsNullOrWhiteSpace(locationFile))
            return false;

        string? matchedFilePath = ResolveSnapshotFilePath(locationFile: locationFile);
        if (matchedFilePath == null)
            return false;

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
        if (_importSnapshots.ContainsKey(locationFile))
        {
            return locationFile;
        }

        string locationFileName = Path.GetFileName(path: locationFile);
        return _importSnapshots.Keys.FirstOrDefault(candidate =>
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
        if (SuppressedWarnings.Contains(item: code)) return;
        AddWarning(warning: new SemanticWarning(Code: code, Message: message, Location: location));
    }

    private static readonly HashSet<SemanticWarningCode> SuppressedWarnings = new()
    {
        SemanticWarningCode.UnusedRoutineReturnValue,
        SemanticWarningCode.UnhandledCrashableCall,
    };

    #endregion

    #region Type Resolution Delegation Stubs

    /// <summary>Resolves a type expression. Delegates to <see cref="TypeResolver"/>.</summary>
    public TypeSymbol ResolveType(TypeExpression? typeExpr) =>
        _typeResolver.ResolveType(typeExpr: typeExpr);

    /// <summary>Looks up a type by name, searching imported modules. Delegates to <see cref="TypeResolver"/>.</summary>
    internal TypeSymbol? LookupTypeWithImports(string name) =>
        _typeResolver.LookupTypeWithImports(name: name);

    /// <summary>Returns true if name is a generic type parameter in the current context. Delegates to <see cref="TypeResolver"/>.</summary>
    internal bool IsGenericParameter(string name) =>
        _typeResolver.IsGenericParameter(name: name);

    /// <summary>True when name is a GENUINE parameter of a generic-definition scope (so it shadows a
    /// same-named global type). Delegates to <see cref="TypeResolver"/>.</summary>
    internal bool IsGenericDefinitionScopeParam(string name) =>
        _typeResolver.IsGenericDefinitionScopeParam(name: name);

    /// <summary>The 0-based positional slot of the in-scope generic parameter. Delegates to <see cref="TypeResolver"/>.</summary>
    internal int GenericParameterSlot(string name) =>
        _typeResolver.GenericParameterSlot(name: name);

    /// <summary>Resolves a type expression in a protocol context (handles 'Me'). Delegates to <see cref="TypeResolver"/>.</summary>
    internal TypeSymbol ResolveProtocolType(TypeExpression? typeExpr) =>
        _typeResolver.ResolveProtocolType(typeExpr: typeExpr);

    /// <summary>Looks up a routine by name, searching Core and imported modules. Delegates to <see cref="TypeResolver"/>.</summary>
    internal RoutineInfo? LookupRoutineWithImports(string name) =>
        _typeResolver.LookupRoutineWithImports(name: name);

    /// <summary>Validates that type arguments satisfy generic constraints. Delegates to <see cref="TypeResolver"/>.</summary>
    internal void ValidateGenericConstraints(TypeSymbol genericDef, List<TypeSymbol> typeArgs,
        SourceLocation location) =>
        _typeResolver.ValidateGenericConstraints(genericDef: genericDef,
            typeArgs: typeArgs,
            location: location);

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
