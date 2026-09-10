using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for import validation.
/// </summary>
public class ImportValidationTests
{
    #region #106: Import position enforcement

    /// <summary>
    /// Verifies semantic analysis behavior for import after declaration and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ImportAfterDeclaration_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        import Core
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportPositionViolation);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for import before declaration without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ImportBeforeDeclaration_NoError()
    {
        string source = """
                        import Core
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportPositionViolation);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for multiple imports before declarations without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_MultipleImportsBeforeDeclarations_NoError()
    {
        string source = """
                        import Core
                        import Core.Text
                        import Core.Bool
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportPositionViolation);
    }

    #endregion

    #region #105: Import name collision

    /// <summary>
    /// Verifies semantic analysis behavior for duplicate imported symbol and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateImportedSymbol_ReportsError()
    {
        string source = """
                        import Core.[Text]
                        import Core.[Text]
                        record Dummy
                          value: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportNameCollision);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for disjoint specific imports without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_DisjointSpecificImports_NoError()
    {
        string source = """
                        import Core.[Text]
                        import Core.[Bool]
                        record Dummy
                          value: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImportNameCollision);
    }

    #endregion
}
