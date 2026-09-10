using Compiler.Declaration;
using Compiler.Targeting;
using SyntaxTree;

namespace Compiler.Lowering;

/// <summary>
/// Guarded context for Phase 8 postprocessing work.
/// </summary>
public sealed class PostprocessingContext
{
    /// <summary>
    /// Semantic registry used for type and routine lookups while lowering.
    /// </summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// Target platform information used when lowering platform-dependent constructs.
    /// </summary>
    public TargetConfig Target { get; }

    /// <summary>
    /// Build mode used by BuilderQuery and other compile-time metadata lowering.
    /// </summary>
    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Pre-transformed bodies for error-handling variant routines, keyed by RoutineInfo.RegistryKey.
    /// Written by Phase 6 ErrorHandlingVariantPass; Phase 8 passes lower their expressions in-place.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; }

    /// <summary>
    /// AST bodies for compiler-generated derived operator routines (ne, lt, le, gt, ge, notcontains),
    /// keyed by RoutineInfo.RegistryKey. Written by Phase 2.6 DerivedOperatorPass.
    /// Phase 8 CallOverloadResolutionPass runs on these to classify all CallExpression nodes.
    /// </summary>
    public Dictionary<string, Statement>? SynthesizedBodies { get; }

    /// <summary>
    /// Monomorphized generic routine instances (keyed by RegistryKey). Exposed so late lowering passes
    /// (e.g. VariantReturnLoweringPass) can rewrite carrier-return sites inside concrete instances too.
    /// </summary>
    public Dictionary<string, Instantiation.MonomorphizedBody>? MonomorphizedBodies { get; }

    /// <summary>Base build (SeedAllStdlibRoutines): when true, the pull/(B) demand flip is bypassed so the
    /// eager stdlib sweep still runs — a precompiled base must lower the WHOLE stdlib, not a demand slice.</summary>
    public bool SynthesizeAllDerives { get; init; }

    /// <summary>
    /// Initializes shared state for the Phase 8 lowering pipeline.
    /// </summary>
    public PostprocessingContext(TypeRegistry registry,
        Dictionary<string, Statement>? variantBodies = null,
        Dictionary<string, Statement>? synthesizedBodies = null, TargetConfig? target = null,
        RfBuildMode buildMode = RfBuildMode.Debug,
        Dictionary<string, Instantiation.MonomorphizedBody>? monomorphizedBodies = null)
    {
        Registry = registry;
        VariantBodies = variantBodies ?? new Dictionary<string, Statement>();
        SynthesizedBodies = synthesizedBodies;
        Target = target ?? TargetConfig.ForCurrentHost();
        BuildMode = buildMode;
        MonomorphizedBodies = monomorphizedBodies;
    }
}
