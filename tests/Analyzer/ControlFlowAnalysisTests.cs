using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for control flow analysis in the semantic analyzer:
/// reachability, unreachable code, return path analysis.
/// </summary>
public class ControlFlowAnalysisTests
{
    private const string NotAllPathsReturnMessage = "not all code paths return";

    #region Return Path Analysis
    /// <summary>
    /// Verifies semantic analysis behavior for all paths return without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_AllPathsReturn_NoError()
    {
        string source = """
                        routine get_value(condition: bool) -> S32
                          if condition
                            return 1
                          else
                            return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should have no missing return errors
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression return without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionReturn_NoError()
    {
        string source = """
                        choice Status
                          ACTIVE
                          INACTIVE
                          PENDING

                        routine get_description(s: Status) -> Text
                          return when s
                            is ACTIVE => "Running"
                            is INACTIVE => "Stopped"
                            is PENDING => "Waiting"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for early return in loop without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_EarlyReturnInLoop_NoError()
    {
        string source = """
                        routine find!(items: List[S32], target: S32) -> S32
                          each item in items
                            if item == target
                              return item
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for unless else return without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_UnlessElseReturn_NoError()
    {
        string source = """
                        routine validate!(value: S32) -> S32
                          unless value > 0
                            throw ValueError("Must be positive")
                          return value
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Unreachable Code
    /// <summary>
    /// Verifies semantic analysis behavior for code after return and reports the expected warning.
    /// </summary>

    [Fact]
    public void Analyze_CodeAfterReturn_ReportsWarning()
    {
        string source = """
                        routine test() -> S32
                          return 42
                          var x = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should have warning about unreachable code
    }
    /// <summary>
    /// Verifies semantic analysis behavior for code after absent and reports the expected warning.
    /// </summary>

    [Fact]
    public void Analyze_CodeAfterAbsent_ReportsWarning()
    {
        string source = """
                        routine test!() -> S32
                          absent
                          var x = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for code after throw and reports the expected warning.
    /// </summary>

    [Fact]
    public void Analyze_CodeAfterThrow_ReportsWarning()
    {
        string source = """
                        routine test!() -> S32
                          throw ValueError("error")
                          var x = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Break and Continue Analysis
    /// <summary>
    /// Verifies semantic analysis behavior for break inside loop without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_BreakInsideLoop_NoError()
    {
        string source = """
                        routine test()
                          each i in 0 til 10
                            if i == 5
                              break
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for continue inside loop without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ContinueInsideLoop_NoError()
    {
        string source = """
                        routine test()
                          each i in 0 til 10
                            if i == 5
                              continue
                            show(i)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for break outside loop and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          break
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for continue outside loop and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ContinueOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          continue
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Failable Routine Analysis
    /// <summary>
    /// Failability is now INFERRED: an <c>absent</c> in a routine NOT declared <c>!</c> no longer emits
    /// AbsentOutsideFailableFunction — the routine is inferred-failable instead.
    /// </summary>
    [Fact]
    public void Analyze_AbsentInNonFailable_NoLongerErrors()
    {
        string source = """
                        routine test() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.AbsentOutsideFailableFunction);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for throw in non failable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInNonFailable_ReportsError()
    {
        string source = """
                        routine test() -> S32
                          throw ValueError("error")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for absent in failable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_AbsentInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should be valid
    }

    #endregion

    #region Infinite Loop Termination (RF-S305)
    // An unconditional `loop` has a fall-through edge ONLY through a `break` that targets it. With no
    // such break the loop can only be left via return/throw/absent, so control never falls past it and
    // the routine always terminates. These lock `StatementAlwaysTerminates`/`LoopBodyCanBreakOut` so the
    // missing-return check neither rejects a valid infinite loop (false positive) nor accepts a real
    // fall-off through a break (false negative).

    /// <summary>A `loop` whose body immediately returns needs no trailing return — it never falls through.</summary>
    [Fact]
    public void Analyze_InfiniteLoopReturnsValue_NoMissingReturn()
    {
        string source = """
                        routine spin() -> S32
                          loop
                            return 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MissingReturn);
    }

    /// <summary>
    /// The real-world shape (this is exactly what stdlib `CWStr.eq`/`cmp` look like): an infinite loop
    /// exited only by conditional returns, with no trailing return after the loop.
    /// </summary>
    [Fact]
    public void Analyze_InfiniteLoopConditionalReturnsOnly_NoMissingReturn()
    {
        string source = """
                        routine scan(n: S32) -> S32
                          var i = 0
                          loop
                            if i >= n
                              return 0 - 1
                            if i == 3
                              return i
                            i = i + 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MissingReturn);
    }

    /// <summary>
    /// A `loop` that CAN break with nothing after it genuinely falls off the routine end — the missing
    /// return must still fire (guards against a blanket `loop => true` false negative).
    /// </summary>
    [Fact]
    public void Analyze_InfiniteLoopWithBreak_NoTrailingReturn_ReportsMissingReturn()
    {
        string source = """
                        routine scan(n: S32) -> S32
                          loop
                            if n > 0
                              break
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MissingReturn);
    }

    /// <summary>A breakable loop followed by a return terminates on every path.</summary>
    [Fact]
    public void Analyze_InfiniteLoopWithBreakThenReturn_NoMissingReturn()
    {
        string source = """
                        routine scan(n: S32) -> S32
                          loop
                            if n > 0
                              break
                          return n
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MissingReturn);
    }

    /// <summary>
    /// A break in an INNER loop targets that inner loop, not the enclosing one — the outer `loop` stays
    /// infinite and terminating, so no trailing return is required.
    /// </summary>
    [Fact]
    public void Analyze_NestedInnerBreak_OuterInfinite_NoMissingReturn()
    {
        string source = """
                        routine scan(n: S32) -> S32
                          loop
                            var i = 0
                            loop
                              if i >= n
                                break
                              i = i + 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MissingReturn);
    }

    #endregion

    #region Failable Routine Analysis (continued)

    /// <summary>
    /// Verifies semantic analysis behavior for throw in failable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ThrowInFailable_NoError()
    {
        string source = """
                        routine test!() -> S32
                          throw ValueError("error")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Throw Terminality (S215/S216)

    /// <summary>
    /// Verifies semantic analysis behavior for throw terminates no missing return.
    /// </summary>
    [Fact]
    public void Analyze_ThrowTerminates_NoMissingReturn()
    {
        string source = """
                        routine fail!(value: S32) -> S32
                          throw ValueError("bad value")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: NotAllPathsReturnMessage,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for throw in all branches no missing return.
    /// </summary>
    [Fact]
    public void Analyze_ThrowInAllBranches_NoMissingReturn()
    {
        string source = """
                        routine validate!(value: S32) -> S32
                          if value > 0
                            throw ValueError("too big")
                          else
                            throw ValueError("too small")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: NotAllPathsReturnMessage,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for absent terminates no missing return.
    /// </summary>
    [Fact]
    public void Analyze_AbsentTerminates_NoMissingReturn()
    {
        string source = """
                        routine lookup!(key: S32) -> S32
                          absent
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: NotAllPathsReturnMessage,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for all paths throw no missing return.
    /// </summary>
    [Fact]
    public void Analyze_AllPathsThrow_NoMissingReturn()
    {
        string source = """
                        routine always_fails!(condition: bool) -> S32
                          if condition
                            throw ValueError("a")
                          else
                            throw ValueError("b")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: NotAllPathsReturnMessage,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for throw with dead code no missing return.
    /// </summary>
    [Fact]
    public void Analyze_ThrowWithDeadCode_NoMissingReturn()
    {
        string source = """
                        routine fail!(value: S32) -> S32
                          throw ValueError("error")
                          var x = 10
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: NotAllPathsReturnMessage,
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Conditional Return Analysis
    /// <summary>
    /// Verifies semantic analysis behavior for if without else no return and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_IfWithoutElse_NoReturn_ReportsError()
    {
        string source = """
                        routine test(condition: bool) -> S32
                          if condition
                            return 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should report missing return path
    }
    /// <summary>
    /// Verifies semantic analysis behavior for nested if else all paths return without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_NestedIfElse_AllPathsReturn_NoError()
    {
        string source = """
                        routine test(a: bool, b: bool) -> S32
                          if a
                            if b
                              return 1
                            else
                              return 2
                          else
                            return 3
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Becomes Statement Validation
    /// <summary>
    /// Verifies semantic analysis behavior for when expression block with becomes without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionBlockWithBecomes_NoError()
    {
        // Multi-statement block with becomes is valid
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              var x = value * 2
                              becomes x
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression block missing becomes and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionBlockMissingBecomes_ReportsError()
    {
        // Multi-statement block in when expression without becomes should error
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              var x = value * 2
                              x
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "requires 'becomes'", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression single becomes block and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionSingleBecomesBlock_ReportsError()
    {
        // Block containing only 'becomes' should use => syntax instead
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 =>
                              becomes 42
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "'=>' syntax", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when expression arrow syntax without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_WhenExpressionArrowSyntax_NoError()
    {
        // Single expression with => is valid
        string source = """
                        routine test(value: S32) -> S32
                          var result = when value
                            == 1 => 42
                            else => 0
                          return result
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for when statement block without becomes without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_WhenStatementBlockWithoutBecomes_NoError()
    {
        // When statement (not expression) doesn't need becomes
        string source = """
                        routine test(value: S32)
                          when value
                            == 1 =>
                              var x = value * 2
                              show(x)
                            else =>
                              show(value)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Message.Contains(value: "becomes", comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    #endregion
}
