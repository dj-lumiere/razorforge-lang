using TypeModel.Symbols;

namespace Compiler.Verification;

/// <summary>
/// Represents a call graph for analyzing memberRoutine dependencies.
/// Used for modification inference and reshaping inference.
/// </summary>
public sealed class CallGraph
{
    /// <summary>
    /// All nodes in the call graph, keyed by routine full name.
    /// </summary>
    private readonly Dictionary<string, CallGraphNode> _nodes = new();

    /// <summary>
    /// Gets or creates a node for the given routine.
    /// </summary>
    /// <param name="routine">The routine to get or create a node for.</param>
    /// <returns>The call graph node for the routine.</returns>
    public CallGraphNode GetOrCreateNode(RoutineInfo routine)
    {
        if (_nodes.TryGetValue(key: routine.RegistryKey, value: out CallGraphNode? node))
        {
            return node;
        }

        node = new CallGraphNode(routine: routine);
        _nodes[key: routine.RegistryKey] = node;

        return node;
    }

    /// <summary>
    /// Adds an edge from caller to callee.
    /// </summary>
    /// <param name="caller">The calling routine.</param>
    /// <param name="callee">The called routine.</param>
    /// <param name="callsOnMe">Whether this call is on the 'me' reference.</param>
    public void AddEdge(RoutineInfo caller, RoutineInfo callee, bool callsOnMe)
    {
        CallGraphNode callerNode = GetOrCreateNode(routine: caller);
        CallGraphNode calleeNode = GetOrCreateNode(routine: callee);

        callerNode.AddCallee(callee: calleeNode, callsOnMe: callsOnMe);
        calleeNode.AddCaller(caller: callerNode);
    }

    /// <summary>
    /// Gets all nodes in the call graph.
    /// </summary>
    public IEnumerable<CallGraphNode> AllNodes => _nodes.Values;

    /// <summary>
    /// Looks up a node by routine name.
    /// </summary>
    /// <param name="fullName">The full name of the routine.</param>
    /// <returns>The node if found, null otherwise.</returns>
    public CallGraphNode? LookupNode(string fullName)
    {
        return _nodes.TryGetValue(key: fullName, value: out CallGraphNode? node)
            ? node
            : null;
    }
}
