namespace Builder.Verification;

/// <summary>
/// Represents an edge in the call graph (a call from one routine to another).
/// </summary>
/// <param name="Target">The called routine's node.</param>
/// <param name="CallsOnMe">Whether this call is on the 'me' reference (affects modification propagation).</param>
public readonly record struct CallEdge(CallGraphNode Target, bool CallsOnMe);
