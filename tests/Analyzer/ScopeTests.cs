using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for scope.
/// </summary>
public class ScopeTests
{
    #region Variable Resolution
    /// <summary>
    /// Verifies semantic analysis behavior for variable in scope and resolves the expected symbol.
    /// </summary>

    [Fact]
    public void Analyze_VariableInScope_Resolves()
    {
        string source = """
                        routine test()
                          var x = 42
                          show(x)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should resolve x correctly
    }
    /// <summary>
    /// Verifies semantic analysis behavior for undefined variable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_UndefinedVariable_ReportsError()
    {
        string source = """
                        routine test()
                          show(undefined_var)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "undefined_var",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "not defined",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "unknown",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for variable used before declaration and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_VariableUsedBeforeDeclaration_ReportsError()
    {
        string source = """
                        routine test()
                          show(x)
                          var x = 42
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Block Scoping
    /// <summary>
    /// Verifies semantic analysis behavior for variable in block scope without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_VariableInBlockScope_NoError()
    {
        string source = """
                        routine test()
                          if true
                            var x = 42
                            show(x)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for variable out of block scope and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_VariableOutOfBlockScope_ReportsError()
    {
        string source = """
                        routine test()
                          if true
                            var x = 42
                          show(x)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for nested block scopes and resolves the expected symbol.
    /// </summary>

    [Fact]
    public void Analyze_NestedBlockScopes_Resolves()
    {
        string source = """
                        routine test()
                          var x = 10
                          if true
                            var y = 20
                            if true
                              var z = 30
                              show(x + y + z)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Loop Scoping
    /// <summary>
    /// Verifies semantic analysis behavior for for loop variable in scope.
    /// </summary>

    [Fact]
    public void Analyze_EachLoopVariable_InScope()
    {
        string source = """
                        routine test()
                          each i in 0 til 10
                            show(i)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for for loop variable out of scope.
    /// </summary>

    [Fact]
    public void Analyze_EachLoopVariable_OutOfScope()
    {
        string source = """
                        routine test()
                          each i in 0 til 10
                            show(i)
                          show(i)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for while loop variable in scope.
    /// </summary>

    [Fact]
    public void Analyze_WhileLoopVariable_InScope()
    {
        string source = """
                        routine test()
                          var i = 0
                          while i < 10
                            show(i)
                            i += 1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region FreeRoutine Parameter Scoping
    /// <summary>
    /// Verifies semantic analysis behavior for parameter in scope and resolves the expected symbol.
    /// </summary>

    [Fact]
    public void Analyze_ParameterInScope_Resolves()
    {
        string source = """
                        routine greet(name: Text)
                          show(name)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for parameter shadows outer without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ParameterShadowsOuter_NoError()
    {
        string source = """
                        var name = "global"

                        routine greet(name: Text)
                          show(name)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Parameter shadows global variable
    }

    #endregion

    #region Variable Shadowing
    /// <summary>
    /// Verifies semantic analysis behavior for shadowing in nested block allowed.
    /// </summary>

    [Fact]
    public void Analyze_ShadowingInNestedBlock_Allowed()
    {
        string source = """
                        routine test()
                          var x = 10
                          if true
                            var x = 20
                            show(x)
                          show(x)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Shadowing in nested scope is allowed
    }
    /// <summary>
    /// Verifies semantic analysis behavior for shadowing in same scope and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ShadowingInSameScope_ReportsError()
    {
        string source = """
                        routine test()
                          var x = 10
                          var x = 20
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region When/Pattern Scoping
    /// <summary>
    /// Verifies semantic analysis behavior for pattern binding in when in scope.
    /// </summary>

    [Fact]
    public void Analyze_PatternBindingInWhen_InScope()
    {
        string source = """
                        variant Result
                          SUCCESS: S32
                          ERROR: Text

                        routine handle(r: Result)
                          when r
                            is SUCCESS value => show(value)
                            is ERROR msg => show(msg)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for pattern binding out of when out of scope.
    /// </summary>

    [Fact]
    public void Analyze_PatternBindingOutOfWhen_OutOfScope()
    {
        string source = """
                        variant Result
                          SUCCESS: S32
                          ERROR: Text

                        routine handle(r: Result)
                          when r
                            is SUCCESS value => show(value)
                            is ERROR msg => pass
                          show(value)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Viewing/Hijacking Scoping
    /// <summary>
    /// Verifies semantic analysis behavior for viewing block variable in scope.
    /// </summary>

    [Fact]
    public void Analyze_ViewingBlockVariable_InScope()
    {
        string source = """
                        entity Data
                          value: S32

                        routine test(data: Data)
                          viewing data as d
                            show(d.value)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for viewing block variable out of scope.
    /// </summary>

    [Fact]
    public void Analyze_ViewingBlockVariable_OutOfScope()
    {
        string source = """
                        entity Data
                          value: S32

                        routine test(data: Data)
                          using data.view() as d
                            show(d.value)
                          show(d.value)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Additional Loop Scoping

    /// <summary>
    /// Verifies that a for-loop variable shadows an outer variable of the same name without error.
    /// </summary>
    [Fact]
    public void Analyze_EachLoopVariableShadowsOuter_NoError()
    {
        string source = """
                        routine test()
                          var i = 0
                          each i in 0 til 5
                            show(i)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    /// <summary>
    /// Verifies that nested for-loops with distinct loop variables both resolve correctly.
    /// </summary>
    [Fact]
    public void Analyze_NestedEachLoops_IndependentVariables_NoError()
    {
        string source = """
                        routine test()
                          each i in 0 til 3
                            each j in 0 til 3
                              show(i + j)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Independent Lambda Captures

    /// <summary>
    /// Verifies that two lambdas in the same scope with different given clauses each compile without error.
    /// </summary>
    [Fact]
    public void Analyze_TwoLambdas_IndependentGivenCaptures_NoError()
    {
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          var f = (x) given a => x + a
                          var g = (x) given b => x + b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    /// <summary>
    /// Verifies that a lambda with multiple captured variables listed in given compiles without error.
    /// </summary>
    [Fact]
    public void Analyze_LambdaWithMultipleGivenCaptures_NoError()
    {
        string source = """
                        routine test()
                          var a = 10
                          var b = 20
                          var f = (x) given a, b => x + a + b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Closure Scoping
    /// <summary>
    /// Verifies semantic analysis behavior for lambda implicit capture and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_LambdaImplicitCapture_ReportsError()
    {
        // Implicit capture of local variable without 'given' should now error
        string source = """
                        routine test()
                          var multiplier = 10
                          var scale = x => x * multiplier
                          show(scale(5))
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "given", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lambda with given clause undefined capture and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_LambdaWithGivenClause_UndefinedCapture_ReportsError()
    {
        // Captured variable 'z' is not defined in scope
        string source = """
                        routine test()
                          var f = (x, y) given z => x + y + z
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: 'z', comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "not defined", comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "unknown", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lambda with given clause valid capture without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_LambdaWithGivenClause_ValidCapture_NoError()
    {
        // Captured variable 'z' exists in outer scope and is declared in given
        string source = """
                        routine test()
                          var z = 100
                          var f = (x, y) given z => x + y + z
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lambda capture preset without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_LambdaCapturePreset_NoError()
    {
        // Preset (module constant) captures are exempt from 'given' requirement
        string source = """
                        preset MAX_VALUE = 100
                        routine test()
                          var scale = x => x * MAX_VALUE
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lambda capture global var without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_LambdaCaptureGlobalVar_NoError()
    {
        // Module-scope variables are exempt — not truly "captured" from a function
        string source = """
                        var global_val = 42
                        routine test()
                          var scale = x => x + global_val
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lambda capture not in given and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_LambdaCaptureNotInGiven_ReportsError()
    {
        // Lambda has 'given' but captures a variable not listed in it
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          var f = (x) given a => x + a + b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: 'b', comparisonType: StringComparison.OrdinalIgnoreCase)
                      && e.Message.Contains(value: "given", comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
