using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for operator validation.
/// </summary>
public class OperatorValidationTests
{
    #region Index operator legality is governed by protocol conformance, not type kind

    /// <summary>
    /// An entity that follows Indexable may define $getitem — no protocol-conformance error.
    /// </summary>
    [Fact]
    public void Analyze_IndexOperatorOnEntity_NoError()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.$getitem(index: S32) -> S32
                        entity Grid obeys Indexable
                          size: S32
                        @readonly
                        routine Grid.$getitem(index: S32) -> S32
                          return 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.OperatorWithoutProtocol);
    }
    /// <summary>
    /// Records are indexable too (e.g. Array[T,N]/BitArray[N] are records), so a record that follows
    /// Indexable may define $getitem with no error — legality follows the protocol, not the type kind.
    /// </summary>
    [Fact]
    public void Analyze_IndexOperatorOnRecord_NoError()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.$getitem(index: S32) -> S32
                        record Pair obeys Indexable
                          x: S32
                          y: S32
                        @readonly
                        routine Pair.$getitem(index: S32) -> S32
                          return 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.OperatorWithoutProtocol);
    }

    /// <summary>
    /// Defining $getitem WITHOUT following Indexable is the actual error — governed by protocol
    /// conformance (RF-S411), independent of whether the owner is a record or an entity.
    /// </summary>
    [Fact]
    public void Analyze_IndexOperatorWithoutIndexable_ReportsError()
    {
        string source = """
                        record Pair
                          x: S32
                          y: S32
                        @readonly
                        routine Pair.$getitem(index: S32) -> S32
                          return 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.OperatorWithoutProtocol);
    }

    #endregion

    #region #117: Fixed-width numeric type mismatch

    /// <summary>
    /// Verifies semantic analysis behavior for same fixed width arithmetic without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_SameFixedWidthArithmetic_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(other: Me) -> Me
                        routine test(a: S32, b: S32) -> S32
                          return a + b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FixedWidthTypeMismatch);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for mixed fixed width arithmetic and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_MixedFixedWidthArithmetic_ReportsError()
    {
        string source = """
                        routine test(a: S32, b: S64) -> S64
                          return a + b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FixedWidthTypeMismatch);
    }

    #endregion

    #region #119: BackIndex in Range restriction

    /// <summary>
    /// Verifies semantic analysis behavior for back index in range and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_BackIndexInRange_ReportsError()
    {
        string source = """
                        routine test()
                          var r = (^1 to 10)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BackIndexOutsideSubscript);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for back index in slice without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_BackIndexInSlice_NoError()
    {
        string source = """
                        routine test(list: Sequence[S32])
                          var x = list[^1]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BackIndexOutsideSubscript);
    }

    #endregion

    #region Additional fixed-width mismatch cases

    /// <summary>
    /// Verifies semantic analysis behavior for S8 plus S64 arithmetic and reports a fixed-width mismatch.
    /// </summary>
    [Fact]
    public void Analyze_S8PlusS64_ReportsFixedWidthError()
    {
        string source = """
                        routine test(a: S8, b: S64) -> S64
                          return a + b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FixedWidthTypeMismatch);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for S64 arithmetic with itself produces no fixed-width error.
    /// </summary>
    [Fact]
    public void Analyze_S64PlusS64_NoFixedWidthError()
    {
        string source = """
                        routine test(a: S64, b: S64) -> S64
                          return a + b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FixedWidthTypeMismatch);
    }

    #endregion

    #region BackIndex in range — both ends

    /// <summary>
    /// Verifies that a back index on both ends of a range reports an error on the first back index.
    /// </summary>
    [Fact]
    public void Analyze_BackIndexBothEndsOfRange_ReportsError()
    {
        string source = """
                        routine test()
                          var r = (^5 to ^1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BackIndexOutsideSubscript);
    }

    #endregion

    #region S201: Binary operator type mismatch

    /// <summary>
    /// Verifies semantic analysis behavior for text plus list reports argument type mismatch.
    /// </summary>
    [Fact]
    public void Analyze_TextPlusList_ReportsArgumentTypeMismatch()
    {
        string source = """
                        routine test(t: Text, xs: List[S64]) -> Text
                          return t + xs
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArgumentTypeMismatch);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for text plus text without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_TextPlusText_NoError()
    {
        string source = """
                        routine test(a: Text, b: Text) -> Text
                          return a + b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArgumentTypeMismatch);
    }

    #endregion
}
