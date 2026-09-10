using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Regression coverage for the RF-S625 (<see cref="SemanticDiagnosticCode.ReshapingDuringIteration"/>)
/// ban: calling a <c>@reshaping</c> member routine on the very collection an <c>each</c> loop is iterating
/// is rejected (the next element could no longer be the real next element). This check was DEAD CODE for a
/// while — the surface <c>EachStatement</c> is lowered to an iterator+<c>while</c> before body analysis, so
/// the iteration-source set was never populated on real loops — and was revived by threading
/// <c>LoopStatement.IterationSourceName</c> through the lowering. These tests lock that revival: the ban
/// must fire on the iterated collection, and must NOT fire on a different collection, a read-only call, or
/// a reshape after the loop.
/// </summary>
public sealed class ReshapingIterationTests
{
    /// <summary>Reshaping the iterated collection itself (List.add_last is @reshaping) is rejected.</summary>
    [Fact]
    public void Analyze_ReshapeIteratedCollection_ReportsError()
    {
        string source = """
                        routine grow(items: List[S32])
                          each item in items
                            items.add_last(value: item)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReshapingDuringIteration);
    }

    /// <summary>Reshaping a DIFFERENT collection during iteration is fine — only the iterated one is banned.</summary>
    [Fact]
    public void Analyze_ReshapeOtherCollection_NoError()
    {
        string source = """
                        routine copy_into(items: List[S32], other: List[S32])
                          each item in items
                            other.add_last(value: item)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReshapingDuringIteration);
    }

    /// <summary>A read-only call (List.count is not @reshaping) on the iterated collection is fine.</summary>
    [Fact]
    public void Analyze_ReadonlyCallOnIteratedCollection_NoError()
    {
        string source = """
                        routine count_while_iterating(items: List[S32]) -> U64
                          var n = 0u64
                          each item in items
                            n = items.count()
                          return n
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReshapingDuringIteration);
    }

    /// <summary>Reshaping the collection AFTER the loop has ended is fine — the iteration source is out of scope.</summary>
    [Fact]
    public void Analyze_ReshapeAfterLoop_NoError()
    {
        string source = """
                        routine grow_after(items: List[S32])
                          var n = 0u64
                          each item in items
                            n = n + 1u64
                          items.add_last(value: 0)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReshapingDuringIteration);
    }
}
