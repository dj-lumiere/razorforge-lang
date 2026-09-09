using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Declaration;
using Compiler.Targeting;
using SyntaxTree;

namespace Compiler.Desugaring;

/// <summary>
/// Guarded context for all desugaring passes.
/// </summary>
public sealed class DesugaringContext
{
    /// <summary>The type registry from semantic analysis.</summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// Routine bodies collected during Phase 4 body analysis, keyed by RoutineInfo.RegistryKey.
    /// Used by <see cref="ErrorHandlingVariantPass"/> to generate try_/check_/lookup_ variants.
    /// </summary>
    public IReadOnlyDictionary<string, Statement> RoutineBodies { get; }

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines, keyed by the variant
    /// RoutineInfo.RegistryKey. Written by <see cref="ErrorHandlingVariantPass"/>
    /// and consumed by codegen so that variant functions emit carrier construction without
    /// relying on mutable flag fields.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; init; } = new();

    /// <summary>
    /// Concrete generic bodies produced by <see cref="GenericMonomorphizationPass"/>,
    /// keyed by the concrete <see cref="TypeModel.Symbols.RoutineInfo.RegistryKey"/>.
    /// Codegen checks this map before doing its own AST search and rewriting, so most
    /// generic memberRoutine bodies are ready before the first IR line is emitted.
    /// </summary>
    public Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; init; } = new();

    /// <summary>
    /// Variant-body keys RESTORED from a warm daemon snapshot: already fully desugared/lowered at capture
    /// time. The <c>RunOnVariantBodies</c> passes skip these (re-lowering an already-lowered body is a no-op
    /// but still walks the whole tree) so a warm compile only processes the USER-added variants, not the
    /// ~2400 stdlib ones re-seeded each request. Empty on a cold compile → every variant is processed.
    /// </summary>
    public HashSet<string> RestoredVariantKeys { get; init; } = new(comparer: StringComparer.Ordinal);

    /// <summary>Target platform — drives BuilderQuery platform constants.</summary>
    public TargetConfig Target { get; }

    /// <summary>Build mode — drives BuilderQuery.build_mode.</summary>
    public RfBuildMode BuildMode { get; }

    /// <summary>When true, diagnostic passes print per-iteration timings to stderr.</summary>
    public bool SaTiming { get; set; }

    /// <summary>Stage-2 (pull/(B)) demand-resolution hook, mirrored from
    /// <see cref="InstantiationContext.AnalyzeRoutineOnDemand"/> when the collector runs on this adapter.
    /// Given a reached routine key, ensures its body is analyzed before the collector walks it.</summary>
    public Func<string, bool>? AnalyzeRoutineOnDemand { get; set; }

    /// <summary>
    /// When true, synthesize structural derive bodies (destroy/represent/hash/…) for ALL concrete types,
    /// not just the ones reachability marked live. Used when emitting a precompiled stdlib "base" that must
    /// DEFINE every routine it references — e.g. the const-generic <c>Array[T,N]</c> destroy/represent that a
    /// <c>from_literal</c> body calls, whose <c>Array[T,N]</c> type is created lazily and would otherwise
    /// never get its derives built. Normal builds leave this false → liveness-filtered synthesis (unchanged).
    /// </summary>
    public bool SynthesizeAllDerives { get; init; }

    /// <summary>
    /// Strategy-B live routine set (RegistryKey values reachable from program entry points).
    /// When non-empty, GMP gates body emission on membership; empty disables filtering.
    /// </summary>
    public HashSet<string> LiveRoutineKeys { get; init; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Live concrete owner-type FullNames mirrored from
    /// <c>InstantiationContext.LiveOwnerTypeNames</c>. GMP skips
    /// <c>ProcessConcreteType</c> for any concrete type not in this set when non-empty.
    /// </summary>
    public HashSet<string> LiveOwnerTypeNames { get; init; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Initializes shared state for passes that rewrite verified syntax before instantiation.
    /// </summary>
    public DesugaringContext(TypeRegistry registry,
        IReadOnlyDictionary<string, Statement> routineBodies,
        TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug)
    {
        Registry = registry;
        RoutineBodies = routineBodies;
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
    }
}
