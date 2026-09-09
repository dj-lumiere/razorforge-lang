using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for f text format spec.
/// </summary>
public class FTextFormatSpecTests
{
    /// <summary>
    /// Verifies semantic analysis behavior for f text diagnose spec without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_FTextDiagnoseSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:?}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for f text name spec without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_FTextNameSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:=}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for f text name diagnose spec without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_FTextNameDiagnoseSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:=?}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for f text no spec without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_FTextNoSpec_NoError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for f text wrong order spec and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_FTextWrongOrderSpec_ReportsError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:?=}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for f text invalid spec d and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_FTextInvalidSpec_d_ReportsError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:d}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for f text invalid spec format and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_FTextInvalidSpec_Format_ReportsError()
    {
        string source = """
                        routine test(x: S64)
                          var msg = f"{x:0.2f}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidFTextFormatSpec);
    }
}
