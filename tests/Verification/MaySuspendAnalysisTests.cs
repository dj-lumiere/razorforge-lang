using TypeModel.Symbols;
using Compiler.Verification;

namespace RazorForge.Tests.Verification;

/// <summary>
/// Unit tests for the v0.2.0 may-suspend effect analysis (the Phase 4 instrumentation gate).
/// Builds synthetic call graphs and asserts the backward fixpoint flags exactly the routines that
/// can transitively reach a suspend primitive — proving transitivity, conservative indirect-call
/// handling, cycle termination, and that suspension (unlike mutation) propagates across every edge.
/// </summary>
public class MaySuspendAnalysisTests
{
    /// <summary>Make a distinct, owner-less routine; RegistryKey == name so keys stay readable.</summary>
    private static RoutineInfo Routine(string name)
    {
        return new RoutineInfo(name: name);
    }

    private static CallGraphNode Node(CallGraph g, string name)
    {
        return g.GetOrCreateNode(routine: Routine(name: name));
    }

    [Fact]
    public void EmptyGraph_ProducesEmptyResult()
    {
        var g = new CallGraph();
        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();
        Assert.Empty(collection: result);
    }

    [Fact]
    public void DirectSuspend_IsFlagged()
    {
        var g = new CallGraph();
        CallGraphNode a = Node(g: g, name: "A");
        a.DirectlySuspends = true;

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Contains(expected: "A", collection: result);
        Assert.True(condition: a.MaySuspend);
    }

    [Fact]
    public void TransitiveSuspend_PropagatesToAllCallers()
    {
        // A -> B -> C(suspends);  D is unrelated and pure.
        var g = new CallGraph();
        RoutineInfo a = Routine(name: "A"),
            b = Routine(name: "B"),
            c = Routine(name: "C"),
            d = Routine(name: "D");
        g.AddEdge(caller: a, callee: b, callsOnMe: true);
        g.AddEdge(caller: b, callee: c, callsOnMe: true);
        g.GetOrCreateNode(routine: c)
         .DirectlySuspends = true;
        g.GetOrCreateNode(routine: d); // isolated

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Contains(expected: "A", collection: result);
        Assert.Contains(expected: "B", collection: result);
        Assert.Contains(expected: "C", collection: result);
        Assert.DoesNotContain(expected: "D", collection: result);
    }

    [Fact]
    public void PureChain_NoSuspend_FlagsNothing()
    {
        // A -> B -> C, none suspend.
        var g = new CallGraph();
        RoutineInfo a = Routine(name: "A"), b = Routine(name: "B"), c = Routine(name: "C");
        g.AddEdge(caller: a, callee: b, callsOnMe: true);
        g.AddEdge(caller: b, callee: c, callsOnMe: true);

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Empty(collection: result);
    }

    [Fact]
    public void IndirectCall_IsConservativelyMaySuspend()
    {
        // A -> B; B makes an unresolved indirect call (no suspend seed anywhere concrete).
        var g = new CallGraph();
        RoutineInfo a = Routine(name: "A"), b = Routine(name: "B");
        g.AddEdge(caller: a, callee: b, callsOnMe: true);
        g.GetOrCreateNode(routine: b)
         .HasIndirectCall = true;

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        // B is poisoned by its own indirect call; A inherits it through the edge.
        Assert.Contains(expected: "A", collection: result);
        Assert.Contains(expected: "B", collection: result);
    }

    [Fact]
    public void SuspensionPropagates_AcrossNonMeEdges()
    {
        // Contrast with mutation inference, which only propagates over calls on `me`.
        // Here the single edge is callsOnMe:false yet suspension must still flow A <- B.
        var g = new CallGraph();
        RoutineInfo a = Routine(name: "A"), b = Routine(name: "B");
        g.AddEdge(caller: a, callee: b, callsOnMe: false);
        g.GetOrCreateNode(routine: b)
         .DirectlySuspends = true;

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Contains(expected: "A", collection: result);
        Assert.Contains(expected: "B", collection: result);
    }

    [Fact]
    public void Cycle_WithSuspend_Terminates_AndFlagsCycle()
    {
        // A <-> B cycle, plus A -> S(suspends). Must terminate and flag both A and B.
        var g = new CallGraph();
        RoutineInfo a = Routine(name: "A"), b = Routine(name: "B"), s = Routine(name: "S");
        g.AddEdge(caller: a, callee: b, callsOnMe: true);
        g.AddEdge(caller: b, callee: a, callsOnMe: true);
        g.AddEdge(caller: a, callee: s, callsOnMe: true);
        g.GetOrCreateNode(routine: s)
         .DirectlySuspends = true;

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Contains(expected: "A", collection: result);
        Assert.Contains(expected: "B", collection: result);
        Assert.Contains(expected: "S", collection: result);
    }

    [Fact]
    public void Cycle_WithoutSuspend_Terminates_AndFlagsNothing()
    {
        // Pure X <-> Y cycle: the fixpoint must terminate (monotonic flags) and flag neither.
        var g = new CallGraph();
        RoutineInfo x = Routine(name: "X"), y = Routine(name: "Y");
        g.AddEdge(caller: x, callee: y, callsOnMe: true);
        g.AddEdge(caller: y, callee: x, callsOnMe: true);

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Empty(collection: result);
    }

    [Fact]
    public void Diamond_PropagatesThroughEitherBranch()
    {
        //      A
        //     / \
        //    B   C
        //     \ /
        //      D(suspends)
        var g = new CallGraph();
        RoutineInfo a = Routine(name: "A"),
            b = Routine(name: "B"),
            c = Routine(name: "C"),
            d = Routine(name: "D");
        g.AddEdge(caller: a, callee: b, callsOnMe: true);
        g.AddEdge(caller: a, callee: c, callsOnMe: true);
        g.AddEdge(caller: b, callee: d, callsOnMe: true);
        g.AddEdge(caller: c, callee: d, callsOnMe: true);
        g.GetOrCreateNode(routine: d)
         .DirectlySuspends = true;

        IReadOnlySet<string> result = new MaySuspendAnalysis(callGraph: g).Compute();

        Assert.Equal(expected: new HashSet<string> { "A", "B", "C", "D" }, actual: result);
    }

    [Fact]
    public void SuspendPrimitives_RecognizesYield_RejectsOthers()
    {
        Assert.True(
            condition: SuspendPrimitives.IsSuspendPrimitive(
                routine: Routine(name: SuspendPrimitives.Yield)));
        Assert.False(
            condition: SuspendPrimitives.IsSuspendPrimitive(
                routine: Routine(name: "rf_coro_resume")));
        Assert.False(
            condition: SuspendPrimitives.IsSuspendPrimitive(
                routine: Routine(name: "some_user_routine")));
    }
}
