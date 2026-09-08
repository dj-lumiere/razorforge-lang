using TypeModel.Symbols;
using TypeModel.Types;

using Compiler.Instantiation;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase D synthesizer: delegates to <see cref="WrapperForwardingPass"/> for
/// transparent-forwarding routine synthesis on wrapper types.
/// </summary>
public sealed partial class SemanticVerifier
{
    /// <summary>Keyed by (wrapperDefName, memberRoutineName, isFailable) — caches synthesized forwarders.</summary>
    private readonly HashSet<string> _synthesizedForwarderKeys = [];

    /// <summary>Lazily initialized pass instance, shared across eager and lazy synthesis calls.</summary>
    private WrapperForwardingPass? _wrapperForwardingPass;

    private WrapperForwardingPass GetOrCreateWrapperForwardingPass()
    {
        return _wrapperForwardingPass ??= new WrapperForwardingPass(
            _registry, _synthesizedBodies, _synthesizedForwarderKeys);
    }

    /// <summary>
    /// Eagerly synthesizes forwarders on all concrete wrapper-type instantiations for every
    /// memberRoutine found on their inner type.  Called after stdlib body analysis so that wrapper
    /// memberRoutines used only implicitly (e.g. release() via scope cleanup) are still forwarded.
    /// </summary>
    private void EagerSynthesizeAllWrapperForwarders()
    {
        GetOrCreateWrapperForwardingPass().RunEager();
    }

    /// <summary>
    /// Attempts to synthesize a forwarding routine on a wrapper type that delegates to
    /// a matching memberRoutine on the wrapper's inner type T.
    /// </summary>
    private RoutineInfo? TrySynthesizeWrapperForwarder(TypeSymbol wrapperType,
        string memberRoutineName, bool isFailable)
    {
        return GetOrCreateWrapperForwardingPass().TrySynthesize(
            wrapperType: wrapperType,
            memberRoutineName: memberRoutineName,
            isFailable: isFailable);
    }
}
