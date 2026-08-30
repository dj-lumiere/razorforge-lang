using System.Collections.Generic;
using System.Linq;
using Compiler.Instantiation;
using Compiler.Resolution;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;

namespace Verification;

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
        var sa = new SemanticVerifier(language: language);
        var tokens = new Compiler.Tokenizer.Tokenizer(source: "module __snapshot__",
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
        InstantiatedGenericBodies =
            _instantiatedGenericBodies.ToDictionary(keySelector: kv => kv.Key, elementSelector: kv => kv.Value),
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
        foreach (var kv in warm.SynthesizedBodies) _synthesizedBodies[kv.Key] = kv.Value;
        _variantBodies = new Dictionary<string, Statement>(warm.VariantBodies);
        _restoredVariantKeys = new HashSet<string>(warm.VariantBodies.Keys, System.StringComparer.Ordinal);
        _instantiatedGenericBodies =
            new Dictionary<string, MonomorphizedBody>(warm.InstantiatedGenericBodies);
        if (Compiler.Diagnostics.DiagnosticFlags.PhaseTiming)
        {
            System.Console.Error.WriteLine(
                value: $"[warm-restore] seeded instantiations={_instantiatedGenericBodies.Count} variants={_variantBodies.Count} synth={_synthesizedBodies.Count}");
        }
    }

    /// <summary>Variant-body keys restored from a warm snapshot — already analyzed at capture time, so
    /// <see cref="AnalyzeVariantBodies"/> skips them instead of re-analyzing (the ~3.6 s warm cost).</summary>
    private HashSet<string> _restoredVariantKeys = new(System.StringComparer.Ordinal);
}
