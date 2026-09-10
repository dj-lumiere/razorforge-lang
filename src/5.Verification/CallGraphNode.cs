using TypeModel.Symbols;
using Compiler.Verification.Enums;

namespace Compiler.Verification;

/// <summary>
/// Represents a node in the call graph (a single routine).
/// </summary>
public sealed class CallGraphNode
{
    /// <summary>The routine this node represents.</summary>
    public RoutineInfo Routine { get; }

    /// <summary>Routines that this routine calls.</summary>
    private readonly List<CallEdge> _callees = [];

    /// <summary>Routines that call this routine.</summary>
    private readonly List<CallGraphNode> _callers = [];

    /// <summary>
    /// The inferred mutation category for this routine.
    /// Seeded from the routine's declared category so user annotations are never downgraded;
    /// propagation can only upgrade toward Reshaping.
    /// </summary>
    public MutationCategory InferredMutation { get; set; }

    /// <summary>
    /// Whether this routine directly writes to member variables of 'me'.
    /// </summary>
    public bool DirectlyMutates { get; set; }

    /// <summary>
    /// Whether this routine directly mutates the internal Hijacked buffer (reshaping).
    /// </summary>
    public bool DirectlyMigrates { get; set; }

    /// <summary>
    /// Whether this node has been visited during propagation.
    /// </summary>
    public bool Visited { get; set; }

    /// <summary>
    /// Whether this routine's body directly invokes a coroutine suspend primitive
    /// (e.g. <c>rf_coro_yield</c>). Seed for the may-suspend backward fixpoint
    /// (<see cref="MaySuspendAnalysis"/>); set when the call graph is built.
    /// </summary>
    public bool DirectlySuspends { get; set; }

    /// <summary>
    /// Whether this routine makes an indirect or dynamically-dispatched call (a first-class
    /// routine value, lambda, or protocol dispatch) whose target the static call graph cannot
    /// see. v0.2.0 treats these conservatively as may-suspend (design §6): over-approximating
    /// only adds cancellation push/pops (a perf cost), never miscompiles teardown.
    /// </summary>
    public bool HasIndirectCall { get; set; }

    /// <summary>
    /// Result of the may-suspend fixpoint: true once this routine is known to transitively reach
    /// a suspend primitive. Monotonic — only ever flips false→true.
    /// </summary>
    public bool MaySuspend { get; set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="CallGraphNode"/> class.
    /// </summary>
    /// <param name="routine">The routine this node represents.</param>
    public CallGraphNode(RoutineInfo routine)
    {
        Routine = routine;
        InferredMutation = routine.DeclaredMutation;
    }

    /// <summary>
    /// Adds a callee (routine that this routine calls).
    /// </summary>
    /// <param name="callee">The called routine's node.</param>
    /// <param name="callsOnMe">Whether this call is on the 'me' reference.</param>
    public void AddCallee(CallGraphNode callee, bool callsOnMe)
    {
        _callees.Add(item: new CallEdge(Target: callee, CallsOnMe: callsOnMe));
    }

    /// <summary>
    /// Adds a caller (routine that calls this routine).
    /// </summary>
    /// <param name="caller">The calling routine's node.</param>
    public void AddCaller(CallGraphNode caller)
    {
        _callers.Add(item: caller);
    }

    /// <summary>Gets all callees (routines called by this routine).</summary>
    public List<CallEdge> Callees => _callees;

    /// <summary>Gets all callers (routines that call this routine).</summary>
    public List<CallGraphNode> Callers => _callers;
}
