using Compiler.Instantiation;
using Compiler.Declaration;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace Compiler.Verification;

public partial class SemanticVerifier
{
    /// <summary>
    /// Fully-processed stdlib state captured after a COMPLETE compile (Phases 1–7) of a minimal
    /// program. Restoring it lets a warm compile reuse the lowered / synthesized / monomorphized stdlib
    /// instead of reprocessing it every run (~5 s → ms). Holds the post-Phase-8 registry snapshot, the
    /// lowered stdlib program ASTs (read-only after lowering — safe to share across warm compiles), and
    /// the body dictionaries codegen consumes. This is the in-RAM state a compile daemon holds.
    /// </summary>
    public sealed class CompiledStdlibState
    {
        /// <summary>The language mode (RF or SF) the stdlib was compiled under.</summary>
        public required Language Language { get; init; }
        /// <summary>The post-SA type registry snapshot (all stdlib types and routines).</summary>
        public required TypeRegistry.StdlibSnapshot Registry { get; init; }
        /// <summary>The fully-lowered stdlib program ASTs, shared read-only across warm compiles.</summary>
        public required List<(Program Program, string FilePath, string Module)> StdlibPrograms { get; init; }
        /// <summary>Synthesized (wired/builder-generated) routine bodies keyed by registry key.</summary>
        public required Dictionary<string, (RoutineInfo Routine, Statement Body)> SynthesizedBodies { get; init; }
        /// <summary>Variant-generated routine bodies keyed by registry key.</summary>
        public required Dictionary<string, Statement> VariantBodies { get; init; }
        /// <summary>Monomorphized generic instantiation bodies keyed by instantiation key.</summary>
        public required Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; init; }
        /// <summary>All other stdlib routine bodies keyed by registry key.</summary>
        public required Dictionary<string, Statement> RoutineBodies { get; init; }

        /// <summary>
        /// Daemon-lifetime cache of per-body reachability scans, keyed by stdlib
        /// <see cref="RoutineDeclaration"/> reference. Shared by reference across every warm compile that
        /// restores from this state (the daemon reuses one <see cref="CompiledStdlibState"/>), so it grows
        /// as successive user programs reach more stdlib bodies and lets <c>RoutineReachabilityPass</c>
        /// skip re-walking them. Starts empty; safe because stdlib decls are stable across warm compiles.
        /// </summary>
        public Dictionary<RoutineDeclaration, RoutineBodyScan> BodyScanCache { get; init; }
            = new();
    }

    /// <summary>
    /// Runs one full compile of a minimal program to fully process the stdlib, then captures the
    /// result. Call once (e.g. at daemon startup); reuse via the restore constructor for warm compiles.
    /// (A "priming" snapshot that pre-instantiates the common generic surface was tried and measured NOT
    /// to help: the per-run cost is in GenericMonomorphizationPass's type-graph processing, whose
    /// per-instance working set resets each run and does not consult pre-seeded instantiation BODIES.
    /// Making it help requires seeding the type-level working sets, not the bodies — deferred.)
    /// </summary>
    public static CompiledStdlibState CaptureCompiledStdlib(Language language)
    {
        // Prime the snapshot with the WHOLE stdlib, not just Core: a daemon should hold the entire
        // analyzed stdlib resident in RAM. Without this, a warm compile of a program importing any non-Core
        // module (e.g. Collections.CircularList) finds it absent from _loadedModules and triggers a full
        // ScanStdlibFiles — re-parsing EVERY stdlib file, every run (~0.7 s, the Phase 3 cost). Importing
        // every module here makes the capture load+analyze+lower them once, so those imports short-circuit
        // on every warm run. The one-time capture cost grows; per-run latency drops.
        string stdlibPath = StdlibLoader.GetDefaultStdlibPath();
        var probe = new StdlibLoader(stdlibRoot: stdlibPath, language: language);
        var source = new System.Text.StringBuilder(value: "module __snapshot__\n");
        foreach (string moduleName in probe.ScanModuleNames())
            source.Append(value: "import ")
                  .Append(value: moduleName.Replace(oldChar: '.', newChar: '/'))
                  .Append(value: '\n');

        var sa = new SemanticVerifier(language: language);
        var tokens = new Compiler.Tokenizer.Tokenizer(source: source.ToString(),
            fileName: "__snapshot__", language: language).Tokenize();
        var parser = new Compiler.Parser.Parser(tokens: tokens, language: language,
            fileName: "__snapshot__");
        sa.Analyze(program: parser.Parse());
        return sa.CaptureCompiledState();
    }

    private CompiledStdlibState CaptureCompiledState() => new()
    {
        Language = _registry.Language,
        Registry = _registry.CaptureSnapshot(),
        StdlibPrograms = new List<(Program, string, string)>(_registry.StdlibPrograms),
        SynthesizedBodies = new Dictionary<string, (RoutineInfo, Statement)>(_synthesizedBodies),
        VariantBodies = new Dictionary<string, Statement>(_variantBodies),
        // Capture an EMPTY instantiation set. The snapshot is analyzed from a throwaway `import EVERY module`
        // program, so its demand collector materializes that program's monomorphizations (Maybe[X].assign,
        // Atomic[X].destroy, …) — which are NOT what any real warm build reaches. Carrying them pollutes every
        // warm build: codegen (a dumb translator) emits the whole InstantiatedGenericBodies set, so a warm
        // build of `show("hi")` would emit hundreds of unrelated derives that the equivalent cold build prunes
        // (the cold/warm define-set divergence). The daemon's value is the cached ANALYZED stdlib (parsed
        // programs + resolved types/routines, captured above); monomorphization is per-build and the collector
        // demand-rebuilds it deterministically from that cache — identical to a cold build.
        InstantiatedGenericBodies = new Dictionary<string, MonomorphizedBody>(comparer: StringComparer.Ordinal),
        RoutineBodies = new Dictionary<string, Statement>(_routineBodies),
    };

    /// <summary>
    /// Constructs a verifier pre-warmed from a full compiled-stdlib snapshot. Restores the lowered
    /// stdlib programs + body dicts and marks the registry to SKIP stdlib reprocessing, so a subsequent
    /// full <see cref="Analyze"/> only processes the user program (+ its incremental instantiations) and
    /// can codegen without redoing the ~5 s of stdlib desugaring/verification/monomorphization.
    /// </summary>
    public SemanticVerifier(Language language, CompiledStdlibState warm,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug)
    {
        _registry = new TypeRegistry(language: language, snapshot: warm.Registry);
        _registry.RestoreStdlibPrograms(programs: warm.StdlibPrograms);
        _registry.SkipStdlibReprocessing = true;
        // Re-lazy the primed whole-stdlib instance closure so this warm compile re-discovers only what the
        // USER program reaches (like cold), instead of GMP re-processing all ~638 primed instances (cold
        // reaches ~378, codegen keeps ~122). User-reachability un-lazies via MaterializeIfLazy.
        int _relazied = _registry.RelazyStdlibConcreteInstances();
        if (Diagnostics.DiagnosticFlags.PhaseTiming)
            Console.Error.WriteLine(value: $"[warm-restore] re-lazied {_relazied} primed concrete instances");
        _typeResolver = new TypeResolver(sa: this);
        _typeBodyResolver = new TypeBodyResolver(sa: this, typeResolver: _typeResolver);
        _signatureResolver = new SignatureResolver(sa: this, typeResolver: _typeResolver);
        _conformanceAnalyzer = new ProtocolConformanceAnalyzer(sa: this);
        _target = target ?? TargetConfig.ForCurrentHost();
        _buildMode = buildMode;
        _snapshotMode = true;

        // Seed the codegen-consumed body dicts from the captured (already-lowered/analyzed) stdlib.
        // NOTE: _routineBodies is deliberately NOT seeded — it is the synthesis working set that drives
        // ErrorHandlingVariantPass / WiredRoutinePass; seeding it makes them REGENERATE + re-analyze all
        // stdlib variant/wired bodies (the 3.6 s AnalyzeVariantBodies cost). CollectStdlibBodiesForVariant-
        // Generation is gated on SkipStdlibReprocessing so stdlib routines stay out of _routineBodies and
        // those passes only process USER routines; the stdlib variant/synthesized bodies come from here.
        // Keep the captured stdlib routine bodies as a LOOKUP-ONLY source (NOT merged into
        // `_routineBodies`, which must stay the user-only variant-generation working set). Protocol
        // default-impl lowering needs the stdlib extension templates (e.g. `Iterable[Text].join`) to
        // recognize + clone them per implementer; without this a warm compile can't specialize them and
        // the generic-def reaches codegen unresolved ("Unresolved generic member routine …join").
        _warmStdlibRoutineBodies = warm.RoutineBodies;
        foreach (var kv in warm.SynthesizedBodies) _synthesizedBodies[kv.Key] = kv.Value;
        _variantBodies = new Dictionary<string, Statement>(warm.VariantBodies);
        _restoredVariantKeys = new HashSet<string>(warm.VariantBodies.Keys, StringComparer.Ordinal);
        // Skip restoring EMPTY synthesized sentinels that have NO matching variant body. The stdlib
        // snapshot captures a placeholder body for a resolved routine whose owner was not live in the
        // stdlib-only snapshot program (e.g. `DictEmittable[Text,SerialValue].try_emit` — no stdlib code
        // iterates a `Dict[Text,SerialValue]`, so its emit variant was never materialized, only a
        // `new BlockStatement([])` stub). Restoring such a stub is HARMFUL: its key enters
        // `_restoredInstantiationKeys`, so GMP treats it as already-built and skips re-monomorphization
        // when the USER program legitimately reaches it — leaving the empty body for codegen to skip
        // (Phase B), which surfaces as the "declared+called but never defined" over-prune. Dropping the
        // placeholder lets the warm build re-materialize the real body from the user's live reachability,
        // exactly as a cold compile does. A sentinel WITH a matching variant body is real (Phase C emits
        // it) and is kept.
        var restoredInst = new Dictionary<string, MonomorphizedBody>();
        foreach (var kv in warm.InstantiatedGenericBodies)
        {
            bool emptySentinel = kv.Value is { IsSynthesized: true, Ast.Body: BlockStatement { Statements.Count: 0 } };
            if (emptySentinel && !warm.VariantBodies.ContainsKey(kv.Key))
                continue; // broken placeholder — let the warm build rebuild it
            restoredInst[kv.Key] = kv.Value;
        }
        _instantiatedGenericBodies = restoredInst;
        _restoredInstantiationKeys =
            new HashSet<string>(restoredInst.Keys, StringComparer.Ordinal);
        // Share the daemon-lifetime reachability body-scan cache by reference so it persists (and grows)
        // across every warm compile restored from this snapshot. Cold compiles leave it null → RRP walks.
        _bodyScanCache = warm.BodyScanCache;
        if (Diagnostics.DiagnosticFlags.PhaseTiming)
        {
            Console.Error.WriteLine(
                value: $"[warm-restore] seeded instantiations={_instantiatedGenericBodies.Count} variants={_variantBodies.Count} synth={_synthesizedBodies.Count}");
        }
    }

    /// <summary>Variant-body keys restored from a warm snapshot — already analyzed at capture time, so
    /// <see cref="AnalyzeVariantBodies"/> skips them instead of re-analyzing (the ~3.6 s warm cost).</summary>
    private HashSet<string> _restoredVariantKeys = new(StringComparer.Ordinal);

    /// <summary>Monomorphized-instantiation keys restored from a warm snapshot — already lowered to
    /// backend representation + validated at capture time, so <see cref="RunPhase9PostDesugarChecks"/>
    /// skips re-running <c>BackendRepresentationPass</c>/validation on them (redundant warm cost).</summary>
    private HashSet<string> _restoredInstantiationKeys = new(StringComparer.Ordinal);

    /// <summary>Daemon-lifetime reachability body-scan cache (see <see cref="CompiledStdlibState.BodyScanCache"/>);
    /// non-null only on a warm compile. Threaded into <c>InstantiationContext</c> so
    /// <c>RoutineReachabilityPass</c> can skip re-walking already-scanned stdlib bodies.</summary>
    private Dictionary<RoutineDeclaration, RoutineBodyScan>? _bodyScanCache;

    /// <summary>Warm-only stdlib routine template bodies (from the snapshot), threaded into
    /// <see cref="Compiler.Instantiation.InstantiationContext.StdlibTemplateBodies"/> so protocol
    /// default-impl lowering can find + clone stdlib extension templates. Null on a cold compile.</summary>
    private Dictionary<string, Statement>? _warmStdlibRoutineBodies;
}
