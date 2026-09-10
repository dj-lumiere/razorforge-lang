using SyntaxTree;
using TypeModel.Symbols;

namespace Compiler.Verification;

/// <summary>
/// Guarded call-classification logic used by both the semantic verifier (during SA) and
/// <see cref="Compiler.Declaration.CallOverloadResolutionPass"/> (post-instantiation).
/// Centralised here so the two phases always agree on which <see cref="CallLoweringKind"/>
/// a resolved routine maps to.
/// </summary>
internal static class CallClassifier
{
    /// <summary>
    /// Performs the classify standalone routine call step for this compiler phase.
    /// </summary>
    internal static CallLoweringKind ClassifyStandaloneRoutineCall(RoutineInfo routine)
    {
        if (routine.LlvmIrTemplate != null)
        {
            return CallLoweringKind.LlvmIntrinsic;
        }

        if (routine.IsSynthesized &&
            BuilderInfoProvider.IsBuilderQueryStandalone(name: routine.Name))
        {
            return CallLoweringKind.BuilderIntrinsic;
        }

        return CallLoweringKind.DirectRoutine;
    }

    /// <summary>
    /// Performs the classify memberRoutine call step for this compiler phase.
    /// </summary>
    internal static CallLoweringKind ClassifyMemberRoutineCall(RoutineInfo memberRoutine)
    {
        if (memberRoutine.LlvmIrTemplate != null)
        {
            return CallLoweringKind.LlvmIntrinsic;
        }

        if (memberRoutine.IsSynthesized &&
            BuilderInfoProvider.IsBuilderQueryRoutine(name: memberRoutine.Name))
        {
            return CallLoweringKind.BuilderIntrinsic;
        }

        return CallLoweringKind.DirectMemberRoutine;
    }
}
