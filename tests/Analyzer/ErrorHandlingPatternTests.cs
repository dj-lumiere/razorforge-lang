using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for error handling pattern.
/// </summary>
public class ErrorHandlingPatternTests
{
    /// <summary>
    /// Verifies that Result[T] is rejected as a routine parameter type — carriers are internal
    /// error-propagation types and may not be passed as arguments.
    /// </summary>
    [Fact]
    public void Analyze_ResultAsParameter_ReportsError()
    {
        string source = """
                        routine test(value: Result[S32])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsParameter);
    }

    /// <summary>
    /// Verifies that Lookup[T] is rejected as a routine parameter type.
    /// </summary>
    [Fact]
    public void Analyze_LookupAsParameter_ReportsError()
    {
        string source = """
                        routine test(value: Lookup[S32])
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsParameter);
    }

    /// <summary>
    /// Verifies that Maybe[T] (T?) IS allowed as a routine parameter type — it is a Assignable
    /// presence-carrying value, unlike Result/Lookup.
    /// </summary>
    [Fact]
    public void Analyze_MaybeAsParameter_NoError()
    {
        string source = """
                        routine test(value: S32?)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsParameter);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for result is none reports pattern mismatch.
    /// </summary>
    [Fact]
    public void Analyze_ResultIsNone_ReportsPatternMismatch()
    {
        string source = """
                        routine test(value: Result[S32])
                          when value
                            is None => pass
                            else => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for lookup uses blank absent arm without pattern mismatch errors.
    /// </summary>
    [Fact]
    public void Analyze_LookupUsesNoneAbsentArm_NoPatternMismatch()
    {
        string source = """
                        routine test(value: Lookup[S32])
                          when value
                            is None => pass
                            is Crashable err => pass
                            else v => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for result blank uses blank value arm without pattern mismatch errors.
    /// </summary>
    [Fact]
    public void Analyze_ResultNoneUsesNoneValueArm_NoPatternMismatch()
    {
        string source = """
                        routine test(value: Result[None])
                          when value
                            is Crashable err => pass
                            is None => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

}
