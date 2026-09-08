namespace Compiler.Instantiation.Passes;

/// <summary>
/// Future Phase 7 pass: normalize wrapper/entity-specialized generic resolutions so
/// later phases see one canonical concrete representation.
/// </summary>
#pragma warning disable CS9113
internal sealed class GenericCanonicalizationPass(InstantiationContext ctx)
#pragma warning restore CS9113
{
    public static void Run()
    {
        // TODO: Implement generic canonicalization logic
        // Remaining work:
        // Canonicalize duplicate concrete identities (wrapper facade vs record def,
        // entity specialization vs primary generic definition, etc.).
    }
}
