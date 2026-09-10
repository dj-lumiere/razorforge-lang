using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for named argument.
/// </summary>
public class NamedArgumentTests
{
    #region Unknown Named Argument (#152 / S505)

    /// <summary>
    /// Verifies semantic analysis behavior for unknown named argument and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_UnknownNamedArgument_ReportsError()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        routine main()
                          greet(unknown: "Alice")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for valid named argument without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ValidNamedArgument_NoError()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        routine main()
                          greet(name: "Alice")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for unknown named argument multiple params and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_UnknownNamedArgument_MultipleParams_ReportsError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, c: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
    }

    #endregion

    #region Duplicate Named Argument (#153 / S506)

    /// <summary>
    /// Verifies semantic analysis behavior for duplicate named argument and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateNamedArgument_ReportsError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, a: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for positional then same named and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_PositionalThenSameNamed_ReportsError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(1, a: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for distinct named arguments without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_DistinctNamedArguments_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, b: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }

    #endregion

    #region Positional After Named (#154 / S507)

    /// <summary>
    /// Verifies semantic analysis behavior for positional after named s510 subsumes s507.
    /// </summary>
    [Fact]
    public void Analyze_PositionalAfterNamed_ReportsMixed()
    {
        // A positional arg after named args is a mix → the all-or-nothing rule reports S512
        // (MixedPositionalAndNamedArguments), which subsumes the old S507 PositionalAfterNamed.
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MixedPositionalAndNamedArguments);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for all named without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_AllNamed_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, b: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for named out of order without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_NamedOutOfOrder_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(b: 2, a: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownNamedArgument);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateNamedArgument);
    }

    #endregion

    #region Named Argument Enforcement (S510)

    /// <summary>
    /// Verifies semantic analysis behavior for two params all positional reports s510.
    /// </summary>
    [Fact]
    public void Analyze_TwoParams_AllPositional_WarnsRecommended()
    {
        // 2-param positional calls are allowed but emit the advisory W258 warning;
        // only 3+ non-defaulted params is a hard S510 error.
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(1, 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
        Assert.Contains(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NamedArgumentRecommended);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for two params all named no s510.
    /// </summary>
    [Fact]
    public void Analyze_TwoParams_AllNamed_NoS510()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(a: 1, b: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for two params mixed positional named reports s510.
    /// </summary>
    [Fact]
    public void Analyze_TwoParams_MixedPositionalNamed_ReportsMixed()
    {
        // Any mix of positional and named args is a compile error (all-or-nothing), even at
        // 2 params where all-positional would only warn.
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(1, b: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MixedPositionalAndNamedArguments);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for one param positional no s510.
    /// </summary>
    [Fact]
    public void Analyze_OneParam_Positional_NoS510()
    {
        string source = """
                        routine greet(name: Text) -> Text
                          return name
                        routine main()
                          greet("Alice")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for zero params no s510.
    /// </summary>
    [Fact]
    public void Analyze_ZeroParams_NoS510()
    {
        string source = """
                        routine noop() -> S32
                          return 0
                        routine main()
                          noop()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for member routine one non me param positional no s510.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutine_OneNonMeParam_Positional_NoS510()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine Point.get_x() -> S32
                          return me.x

                        routine Point.offset_x(dx: S32) -> S32
                          return me.x

                        routine main()
                          var p = Point(x: 1, y: 2)
                          p.offset_x(5)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for member routine two non me params positional reports s510.
    /// </summary>
    [Fact]
    public void Analyze_memberRoutine_ThreeNonMeParams_Positional_ReportsS510()
    {
        // `me` is excluded from the param count; 3 explicit positional args trip S510.
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine Point.offset(dx: S32, dy: S32, dz: S32) -> S32
                          return me.x

                        routine main()
                          var p = Point(x: 1, y: 2)
                          p.offset(3, 4, 5)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for two params named out of order no s510.
    /// </summary>
    [Fact]
    public void Analyze_TwoParams_NamedOutOfOrder_NoS510()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        routine main()
                          add(b: 2, a: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }

    #endregion

    #region @positional Opt-Out (S512)

    /// <summary>
    /// A `@positional` routine accepts all-positional calls even with 3+ params (S510 relaxed).
    /// </summary>
    [Fact]
    public void Analyze_Positional_AllPositional_NoS510()
    {
        string source = """
                        @positional
                        routine make(a: S32, b: S32, c: S32) -> S32
                          return a
                        routine main()
                          make(1, 2, 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MixedPositionalAndNamedArguments);
    }
    /// <summary>
    /// Named arguments still work on a `@positional` routine (the annotation only relaxes).
    /// </summary>
    [Fact]
    public void Analyze_Positional_AllNamed_NoError()
    {
        string source = """
                        @positional
                        routine make(a: S32, b: S32, c: S32) -> S32
                          return a
                        routine main()
                          make(a: 1, b: 2, c: 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MixedPositionalAndNamedArguments);
    }
    /// <summary>
    /// Mixing positional and named in a single `@positional` call is rejected (S512).
    /// </summary>
    [Fact]
    public void Analyze_Positional_Mixed_ReportsS512()
    {
        string source = """
                        @positional
                        routine make(a: S32, b: S32, c: S32) -> S32
                          return a
                        routine main()
                          make(1, b: 2, c: 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MixedPositionalAndNamedArguments);
        // S507 is suppressed for @positional — S512 covers the mixing.
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PositionalAfterNamed);
    }
    /// <summary>
    /// Without `@positional`, a 3-param all-positional call still reports S510 (regression guard).
    /// </summary>
    [Fact]
    public void Analyze_NoPositional_ThreeParams_AllPositional_ReportsS510()
    {
        string source = """
                        routine make(a: S32, b: S32, c: S32) -> S32
                          return a
                        routine main()
                          make(1, 2, 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }
    /// <summary>
    /// Foreign (C::) routines are positional by nature — their parameter identity is argument
    /// order + types, not names — so a 3+-param all-positional C:: call must NOT report S510.
    /// This is the gap the SDL2 FFI milestone surfaced (SDL_CreateWindow has 6 params).
    /// </summary>
    [Fact]
    public void Analyze_ForeignRoutine_ThreeParams_AllPositional_NoS510()
    {
        string source = """
                        routine C::sdl_make(a: S32, b: S32, c: S32) -> S32
                        routine main()
                          C::sdl_make(1, 2, 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NamedArgumentRequired);
    }

    #endregion
}
