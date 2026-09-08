using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for routine declaration.
/// </summary>
public class RoutineDeclarationTests
{
    #region Duplicate Routine Definition
    /// <summary>
    /// Verifies semantic analysis behavior for duplicate routine and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_DuplicateRoutine_ReportsError()
    {
        string source = """
                        routine foo() -> S32
                          return 1
                        routine foo() -> S32
                          return 2
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for unique routines no duplicate error.
    /// </summary>

    [Fact]
    public void Analyze_UniqueRoutines_NoDuplicateError()
    {
        string source = """
                        routine foo() -> S32
                          return 1
                        routine bar() -> S32
                          return 2
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }

    #endregion

    #region Parameter validation

    /// <summary>
    /// Verifies that a routine with distinct parameter names produces no error.
    /// </summary>
    [Fact]
    public void Analyze_DistinctParameterNames_NoError()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }

    #endregion

    #region Return type validation

    /// <summary>
    /// Verifies that returning the wrong type from a routine reports a ReturnTypeMismatch error.
    /// </summary>
    [Fact]
    public void Analyze_ReturnTypeMismatch_ReportsError()
    {
        string source = """
                        routine get_text() -> Text
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReturnTypeMismatch);
    }

    /// <summary>
    /// Verifies that a void routine with a bare return produces no error.
    /// </summary>
    [Fact]
    public void Analyze_VoidRoutineWithBareReturn_NoError()
    {
        string source = """
                        routine do_work()
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReturnTypeMismatch);
    }

    #endregion

    #region Duplicate member routines

    /// <summary>
    /// Verifies that two implementations of the same member routine report a duplicate error.
    /// </summary>
    [Fact]
    public void Analyze_DuplicateMemberRoutine_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        @readonly
                        routine Point.area() -> S32
                          return me.x * me.y

                        @readonly
                        routine Point.area() -> S32
                          return 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DuplicateRoutineDefinition);
    }

    #endregion
}
