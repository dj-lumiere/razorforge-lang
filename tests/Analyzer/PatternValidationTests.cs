using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for pattern validation.
/// </summary>
public class PatternValidationTests
{
    #region Variable Shadowing

    /// <summary>
    /// Verifies semantic analysis behavior for type pattern shadows outer variable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_TypePattern_ShadowsOuterVariable_ReportsError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 x => show(x)
                            else => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for else pattern shadows outer variable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ElsePattern_ShadowsOuterVariable_ReportsError()
    {
        string source = """
                        routine test()
                          var value: S32 = 5
                          when value
                            is S32 n => show(n)
                            else value => show(value)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for type pattern unique variable name no shadowing error.
    /// </summary>
    [Fact]
    public void Analyze_TypePattern_UniqueVariableName_NoShadowingError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 n => show(n)
                            else => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for type pattern same name different clauses no shadowing error.
    /// </summary>
    [Fact]
    public void Analyze_TypePattern_SameNameDifferentClauses_NoShadowingError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 n => show(n)
                            else n => show(n)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }

    #endregion

    #region Scope Isolation

    /// <summary>
    /// Verifies semantic analysis behavior for when expression clause scopes isolated.
    /// </summary>
    [Fact]
    public void Analyze_WhenExpression_ClauseScopesIsolated()
    {
        string source = """
                        routine test() -> S32
                          var x: S32 = 5
                          var result: S32 = when x
                            is S32 n => n
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // Pattern variables should not leak between clauses or outside the when expression
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }

    #endregion

    #region Pattern Variable Scope Leakage

    /// <summary>
    /// Verifies that a pattern binding variable is not accessible outside its when clause.
    /// </summary>
    [Fact]
    public void Analyze_PatternBinding_DoesNotLeakOutsideWhen_ReportsError()
    {
        string source = """
                        routine test() -> S32
                          var x: S32 = 5
                          when x
                            is S32 n => pass
                            else => pass
                          return n
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    /// <summary>
    /// Verifies that the same binding name used in separate when clauses does not shadow outer scope.
    /// </summary>
    [Fact]
    public void Analyze_SameBindingNameInSeparateClauses_NoShadowingError()
    {
        string source = """
                        variant Value
                          S32
                          F64

                        routine test(v: Value) -> S32
                          return when v
                            is S32 n => n
                            else n => 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.IdentifierShadowing);
    }

    #endregion

    #region Type Compatibility

    /// <summary>
    /// Verifies semantic analysis behavior for type pattern compatible type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_TypePattern_CompatibleType_NoError()
    {
        string source = """
                        routine test()
                          var x: S32 = 5
                          when x
                            is S32 n => show(n)
                            else => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for type pattern incompatible type and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_TypePattern_IncompatibleType_ReportsError()
    {
        string source = """
                        record Foo
                          x: S32
                        record Bar
                          y: S32
                        routine test()
                          var f = Foo(x: 1)
                          when f
                            is Bar b => pass
                            else => pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PatternTypeMismatch);
    }

    #endregion
}
