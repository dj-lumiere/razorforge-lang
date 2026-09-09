using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the async-spawn argument boundary (RF-S632). A `threaded routine` spawns an OS thread
/// and — under M:N — a `suspended routine` yields a coroutine that may migrate to any worker in
/// parallel with its siblings, so BOTH boundaries enforce the same crossing rule: an argument is
/// safe when it is `steal`-moved (exclusive transfer), trivially-copyable value data (passed BY
/// VALUE), or a thread-shareable wrapper (Atomic/Guarded/Witnessed/Consulting/Amending — carries its
/// own synchronization). Anything else passed by copy — a bare entity, or a record/tuple that
/// transitively owns a single-threaded RC wrapper (Retained/Tracked) or single-threaded token
/// (Viewing/Modifying) — would silently alias unsynchronized state across parallel coroutines and
/// is rejected.
/// </summary>
public class ThreadArgShareabilityTests
{
    private const string Prelude = """
                                   import IO/Console

                                   record Point
                                     posted x: S64
                                     posted y: S64

                                   entity Node
                                     value: S64

                                   """;

    [Fact]
    public void Analyze_ScalarThreadArg_Ok()
    {
        string source = Prelude + """
                                  threaded routine work(n: S64) -> S64
                                    return n + 1

                                  routine start()
                                    var t = work(n: 5)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_ValueRecordThreadArg_Ok()
    {
        // A plain value record (no interior reference) is copied by value — safe to cross.
        string source = Prelude + """
                                  threaded routine work(p: Point) -> S64
                                    return p.x + p.y

                                  routine start()
                                    var origin = Point(x: 1, y: 2)
                                    var t = work(p: origin)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_AtomicThreadArg_Ok()
    {
        // Atomic carries its own synchronization — shared by reference across the boundary.
        string source = Prelude + """
                                  threaded routine work(cell: Atomic[S64]) -> S64
                                    discard cell.fetch_add(delta: 1_s64)
                                    return 0_s64

                                  routine start()
                                    var counter = Atomic[S64](initial: 0_s64)
                                    var t = work(cell: counter)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_SharedThreadArg_Ok()
    {
        // Guarded is an atomic Arc — explicitly shareable across threads.
        string source = Prelude + """
                                  threaded routine work(s: Guarded[Node, ReadOnly]) -> S64
                                    return 0_s64

                                  routine start()
                                    var s = Guarded[Node, ReadOnly](from: Node(value: 1))
                                    var t = work(s: s.share())
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_BareEntityThreadArg_Errors()
    {
        // A bare entity is a heap handle — passing it would alias the same object across threads.
        string source = Prelude + """
                                  threaded routine work(node: Node) -> S64
                                    return node.value

                                  routine start()
                                    var n = Node(value: 1)
                                    var t = work(node: n)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring:
            "of a threaded routine cannot cross the spawn boundary safely");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThreadArgNotShareable);
    }

    [Fact]
    public void Analyze_RecordOwningRetainedThreadArg_Errors()
    {
        // A record that transitively owns a single-threaded RC wrapper (Retained) would alias its
        // interior across threads — must use Guarded instead.
        string source = Prelude + """
                                  record Holder
                                    posted node: Retained[Node]

                                  threaded routine work(h: Holder) -> S64
                                    return 0_s64

                                  routine start()
                                    var h = Holder(node: Retained(from: Node(value: 1)))
                                    var t = work(h: h)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "transitively owns `Retained`");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThreadArgNotShareable);
    }

    [Fact]
    public void Analyze_StealBareEntityThreadArg_Ok()
    {
        // A `steal`-moved bare entity is an EXCLUSIVE transfer — the caller loses access, so exactly
        // one live handle survives. Safe to cross even though the param type is a bare entity. (This
        // is the over-strictness the steal-credit removed: the type-only check rejected it before.)
        string source = Prelude + """
                                  threaded routine work(node: Node) -> S64
                                    return node.value

                                  routine start()
                                    var n = Node(value: 1)
                                    var t = work(node: steal n)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_ScalarSuspendedArg_Ok()
    {
        // A trivially-copyable scalar is copied by value — safe across the suspended boundary too.
        string source = Prelude + """
                                  suspended routine work(n: S64) -> S64
                                    return n + 1

                                  routine start()
                                    var t = work(n: 5)
                                    discard t.retrieve!()
                                    return
                                  """;

        AssertAnalyzesSa(source: source);
    }

    [Fact]
    public void Analyze_RecordOwningRetainedSuspendedArg_Errors()
    {
        // The suspended boundary was previously UNCHECKED — harmless single-threaded, but a UAF hole
        // under M:N: a copied record owning a non-atomic Retained refcount would race across parallel
        // coroutines. The same crossing rule now applies here (RF-S632).
        string source = Prelude + """
                                  record Holder
                                    posted node: Retained[Node]

                                  suspended routine work(h: Holder) -> S64
                                    return 0_s64

                                  routine start()
                                    var h = Holder(node: Retained(from: Node(value: 1)))
                                    var t = work(h: h)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring:
            "of a suspended routine cannot cross the spawn boundary safely");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThreadArgNotShareable);
    }

    [Fact]
    public void Analyze_StealRetained_Rejected()
    {
        // A `Retained` is a reference-counted handle — SHARED ownership, not unique. `steal` (an
        // exclusive-transfer marker) is a category error on it: moving one handle proves nothing
        // about sibling handles racing the non-atomic count. So `steal r` is rejected at the steal
        // itself (RF-S617), independent of the async boundary — the honest fix is `Guarded`/`Witnessed`.
        string source = Prelude + """
                                  suspended routine work(r: Retained[Node]) -> S64
                                    return r.value

                                  routine start()
                                    var r = Retained(from: Node(value: 1))
                                    var t = work(r: steal r)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "a reference-counted handle is shared ownership");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.StealSharedOwnership);
    }

    [Fact]
    public void Analyze_StealRecordOwningRetainedSuspendedArg_Errors()
    {
        // `steal` on a plain record IS allowed (no-op), but stealing a record that transitively owns
        // a Retained does NOT make it safe to cross: moving the record doesn't make its interior
        // non-atomic refcount exclusive. The boundary credits `steal` ONLY for a bare entity, so this
        // is still rejected (RF-S632) — must use `Guarded`/`Witnessed`.
        string source = Prelude + """
                                  record Holder
                                    posted node: Retained[Node]

                                  suspended routine work(h: Holder) -> S64
                                    return 0_s64

                                  routine start()
                                    var h = Holder(node: Retained(from: Node(value: 1)))
                                    var t = work(h: steal h)
                                    discard t.retrieve!()
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring:
            "of a suspended routine cannot cross the spawn boundary safely");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThreadArgNotShareable);
    }
}
