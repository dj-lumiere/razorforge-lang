using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for record secret with.
/// </summary>
public class RecordSecretWithTests
{
    #region #45: With secret member prohibition

    /// <summary>
    /// Verifies semantic analysis behavior for with open member variable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WithOpenMemberVariable_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithSecretMemberProhibited);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for with secret member variable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_WithSecretMemberVariable_ReportsError()
    {
        string source = """
                        record SecretRecord
                          secret hash: S32
                          name: Text
                        routine test(r: SecretRecord)
                          var s = r with .hash = 42
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithSecretMemberProhibited);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for with posted member variable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WithPostedMemberVariable_NoError()
    {
        string source = """
                        record Info
                          posted status: S32
                          name: Text
                        routine test(info: Info)
                          var i = info with .name = "test"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithSecretMemberProhibited);
    }

    #endregion
}
