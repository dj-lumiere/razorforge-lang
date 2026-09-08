using TypeModel.Symbols;

namespace Compiler.Verification;

/// <summary>
/// The may-suspend effect analysis — the instrumentation gate for v0.2.0 coroutine
/// abandon-teardown (coroutine-primitive design, §6).
///
/// A routine is <b>may-suspend</b> if it can ever be parked at a coroutine suspend point. That is
/// true when it: (a) directly invokes a suspend primitive (<see cref="SuspendPrimitives"/>), (b)
/// makes an indirect/dynamic call whose target the static graph cannot see (treated conservatively
/// as may-suspend), or (c) transitively calls another may-suspend routine.
///
/// Only may-suspend routines get cancellation frames in Phase 4; everything else keeps today's
/// exact codegen. Over-approximation is safe — it only adds shadow-stack push/pops (a perf cost),
/// never affects teardown correctness — so the analysis is deliberately conservative and starts
/// fully so: any unresolved indirect call poisons its routine.
///
/// Mechanically this is the dual of <see cref="MutationInference"/>: the same monotonic
/// call-graph fixpoint, but information flows callee → caller and across <i>every</i> edge (not
/// just calls on <c>me</c>), because a suspend anywhere below you can park you.
/// </summary>
public sealed class MaySuspendAnalysis
{
    private readonly CallGraph _callGraph;

    /// <summary>
    /// Initializes the analysis over <paramref name="callGraph"/>. The graph's per-node seed flags
    /// (<see cref="CallGraphNode.DirectlySuspends"/> / <see cref="CallGraphNode.HasIndirectCall"/>)
    /// must already be populated before <see cref="Compute"/> is called.
    /// </summary>
    public MaySuspendAnalysis(CallGraph callGraph)
    {
        _callGraph = callGraph;
    }

    /// <summary>
    /// Runs the fixpoint and returns the set of may-suspend routine registry keys. Also leaves
    /// the verdict on each <see cref="CallGraphNode.MaySuspend"/> for consumers that already hold
    /// nodes. Idempotent: the seed flags (<see cref="CallGraphNode.DirectlySuspends"/> /
    /// <see cref="CallGraphNode.HasIndirectCall"/>) must be set on the nodes before calling.
    /// </summary>
    public IReadOnlySet<string> Compute()
    {
        // Monotonic worklist-free fixpoint: rescan all nodes until a full pass makes no change.
        // Each node's flag can only flip false→true, so this terminates in at most O(V) passes
        // (bounded by the longest caller chain), and handles recursion/cycles for free — a cycle
        // simply doesn't propagate may-suspend unless something on it is a genuine seed.
        bool changed = true;
        while (changed)
        {
            changed = false;

            foreach (CallGraphNode node in _callGraph.AllNodes)
            {
                if (node.MaySuspend)
                {
                    continue; // already decided; nothing downgrades
                }

                if (IsMaySuspend(node: node))
                {
                    node.MaySuspend = true;
                    changed = true;
                }
            }
        }

        var result = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (CallGraphNode node in _callGraph.AllNodes)
        {
            if (node.MaySuspend)
            {
                result.Add(item: node.Routine.RegistryKey);
            }
        }

        return result;
    }

    /// <summary>
    /// True if this node should be may-suspend given the current state of its callees. A node is
    /// may-suspend when it is a seed (direct suspend or an unresolved indirect call) or any callee
    /// is already known may-suspend.
    /// </summary>
    private static bool IsMaySuspend(CallGraphNode node)
    {
        if (node.DirectlySuspends || node.HasIndirectCall)
        {
            return true;
        }

        foreach (CallEdge edge in node.Callees)
        {
            // Unlike mutation inference, suspension propagates through EVERY call, not only
            // calls on `me`: being parked deep in any callee parks this frame too.
            if (edge.Target.MaySuspend)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Single source of truth for which routines are coroutine suspend primitives. v0.2.0 ships no
/// suspend keyword (design §8); the substrate is the native <c>rf_coro_*</c> functions declared in
/// <c>Core/NativeDeclarations.rf</c>. The park point is <c>rf_coro_yield</c>; v0.3.0 channels add
/// <c>rf_channel_feed</c> / <c>rf_channel_next</c>, which park the calling coroutine internally
/// (rf_sched_park_external) on a full/empty buffer exactly like a yield.
/// </summary>
public static class SuspendPrimitives
{
    /// <summary>The native park primitive — switches the running coroutine out to its resumer.</summary>
    public const string Yield = "rf_coro_yield";

    /// <summary>
    /// Channel send/receive (v0.3.0). Both park the calling coroutine inside the native runtime when
    /// the buffer is full / empty, so a routine reaching either may suspend and needs the same
    /// teardown-across-park instrumentation as one reaching <see cref="Yield"/>.
    /// </summary>
    private static readonly HashSet<string> PrimitiveNames = new()
    {
        Yield,
        "rf_channel_feed",
        "rf_channel_next",
        "rf_signal_wait",
        "rf_signal_wait_deadline",
    };

    /// <summary>
    /// Whether <paramref name="routine"/> is a suspend primitive (the seed for
    /// <see cref="CallGraphNode.DirectlySuspends"/>). Matched by name: these are free
    /// <c>external("C")</c> routines with no owner, so the bare name is unambiguous.
    /// </summary>
    public static bool IsSuspendPrimitive(RoutineInfo routine)
    {
        return routine is { OwnerType: null } && PrimitiveNames.Contains(routine.Name);
    }
}