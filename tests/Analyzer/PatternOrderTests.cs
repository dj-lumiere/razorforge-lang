using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for pattern order.
/// </summary>
public class PatternOrderTests
{
    #region #88: Pattern order enforcement

    /// <summary>
    /// Verifies semantic analysis behavior for when else before other patterns and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_WhenElseBeforeOtherPatterns_ReportsError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            else => 0_s32
                            is Color.RED => 1_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternOrderViolation);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when else last without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WhenElseLast_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is Color.RED => 1_s32
                            is Color.GREEN => 2_s32
                            else => 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternOrderViolation);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for when wildcard before specific pattern and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_WhenWildcardBeforeSpecificPattern_ReportsError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            _ => 0_s32
                            is Color.RED => 1_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternOrderViolation);
    }

    #endregion

    #region Wildcard placement

    /// <summary>
    /// Verifies that a wildcard placed last (after specific patterns) produces no order violation.
    /// </summary>
    [Fact]
    public void Analyze_WildcardLast_NoError()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            == 1 => 10_s32
                            == 2 => 20_s32
                            _ => 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternOrderViolation);
    }

    /// <summary>
    /// Verifies that two wildcard arms in the same when report an error (second is unreachable).
    /// </summary>
    [Fact]
    public void Analyze_TwoWildcardPatterns_ReportsError()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            == 1 => 10_s32
                            _ => 0_s32
                            _ => 99_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    /// <summary>
    /// Verifies that two else arms in the same when report an error.
    /// </summary>
    [Fact]
    public void Analyze_TwoElseArms_ReportsError()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            == 1 => 10_s32
                            else => 0_s32
                            else => 99_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region #130/#148: Duplicate pattern detection

    /// <summary>
    /// Verifies semantic analysis behavior for duplicate literal pattern and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateLiteralPattern_ReportsError()
    {
        string source = """
                        routine test(x: S32) -> S32
                          return when x
                            42 => 1_s32
                            42 => 2_s32
                            else => 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicatePattern);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for duplicate choice case pattern and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateChoiceCasePattern_ReportsError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is Color.RED => 1_s32
                            is Color.RED => 2_s32
                            else => 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicatePattern);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for distinct patterns without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_DistinctPatterns_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        routine test(c: Color) -> S32
                          return when c
                            is Color.RED => 1_s32
                            is Color.GREEN => 2_s32
                            is Color.BLUE => 3_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicatePattern);
    }

    #endregion
}
