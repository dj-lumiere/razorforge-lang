using Compiler.Instantiation.Passes;
using Compiler.Collection.Passes;

namespace Compiler.Instantiation;

/// <summary>
/// Phase 7 pipeline boundary for generic closure.
/// This is intentionally separate from current codegen-era monomorphization logic.
/// </summary>
public sealed class InstantiationPipeline(InstantiationContext ctx)
{
    /// <summary>
    /// Runs generic reachability, closure expansion, and canonical body generation. The may-suspend
    /// analysis over the graph <see cref="RoutineReachabilityPass"/> populates runs in
    /// <c>SemanticVerifier</c> after this (so it also covers the timed pass-by-pass path).
    /// </summary>
    public void Run()
    {
        new ReachableGenericCollectionPass(ctx: ctx).Run();
        new RoutineReachabilityPass(ctx: ctx).Run();
        new GenericClosurePass(ctx: ctx).Run();
        GenericCanonicalizationPass.Run();
    }
}
