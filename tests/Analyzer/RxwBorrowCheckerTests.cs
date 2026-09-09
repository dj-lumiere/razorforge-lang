using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the readers-XOR-writer borrow checker (RF-S630). Within overlapping (nested) `using`
/// scopes on the SAME Guarded handle, an `amend()` (writer) conflicts with any other hold; `consult()`
/// readers may coexist with other readers. Non-overlapping (sequential) scopes and distinct handles
/// never conflict. Detection is name-based on the Guarded handle (v1).
/// </summary>
public class RxwBorrowCheckerTests
{
    private const string Prelude = """
                                   import IO/Console
                                   import BuilderQuery

                                   entity Counter
                                     value: S64

                                   """;

    [Fact]
    public void Analyze_NestedClaimClaim_SameHandle_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    using s.amend() as c1
                                      using s.amend() as c2
                                        show("nested")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "conflicts with an active 'amend()'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedClaimInspect_SameHandle_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    using s.amend() as c
                                      using s.consult() as v
                                        show("nested")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "conflicts with an active 'amend()'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedInspectClaim_SameHandle_Errors()
    {
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    using s.consult() as v
                                      using s.amend() as c
                                        show("nested")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "conflicts with an active 'consult()'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedInspectInspect_SameHandle_Ok()
    {
        // Readers coexist — multiple consult holds on the same handle are allowed.
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    using s.consult() as v1
                                      using s.consult() as v2
                                        show(f"{v1.value} {v2.value}")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_NestedClaim_DifferentHandles_Ok()
    {
        // Distinct handles do not conflict (name-based).
        string source = Prelude + """
                                  routine start()
                                    var s1 = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    var s2 = Guarded[Counter, MultiRead](from: Counter(value: 2))
                                    using s1.amend() as c1
                                      using s2.amend() as c2
                                        show("two handles")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_SequentialClaim_SameHandle_Ok()
    {
        // Non-overlapping scopes — the first amend is released before the second opens.
        string source = Prelude + """
                                  routine Counter.bump(inc: S64)
                                    me.value = me.value + inc
                                    return

                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    using s.amend() as c1
                                      c1.bump(inc: 1)
                                    using s.amend() as c2
                                      c2.bump(inc: 1)
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_NestedClaim_AliasedHandle_Errors()
    {
        // `s2 = s.share()` is a CLONE — a second handle to the SAME controller. Amending both in
        // overlapping scopes deadlocks at runtime; the identity-keyed check catches it even though
        // the handle names differ.
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    var s2 = s.share()
                                    using s.amend() as c1
                                      using s2.amend() as c2
                                        show("aliased")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "same shared data");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedClaimInspect_AliasedHandle_Errors()
    {
        // A reader on a clone still conflicts with a writer on the original (same controller).
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    var s2 = s.share()
                                    using s.amend() as c
                                      using s2.consult() as v
                                        show("aliased reader")
                                    return
                                  """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "same shared data");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReadersXorWriter);
    }

    [Fact]
    public void Analyze_NestedInspect_AliasedHandle_Ok()
    {
        // Two readers coexist even on the same controller — readers-XOR-writer permits shared reads.
        string source = Prelude + """
                                  routine start()
                                    var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                                    var s2 = s.share()
                                    using s.consult() as v1
                                      using s2.consult() as v2
                                        show("two readers, one controller")
                                    return
                                  """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
