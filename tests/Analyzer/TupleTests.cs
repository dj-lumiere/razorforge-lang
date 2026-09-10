using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for tuple.
/// </summary>
public class TupleTests
{
    #region Type Inference

    /// <summary>
    /// Verifies semantic analysis behavior for infers tuple.
    /// </summary>
    [Fact]
    public void Analyze_InfersTuple()
    {
        // All tuples are inline structs regardless of element types
        string source = """
                        routine test()
                          var tuple = (1_s32, 2_s32, 3_s32)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for contains entity and infers tuple type information.
    /// </summary>
    [Fact]
    public void Analyze_ContainsEntity_InfersTuple()
    {
        // Entity fields stored as ptr in the tuple struct
        string source = """
                        entity Point
                          x: S32
                          y: S32

                        routine test()
                          var p = Point(x: 1, y: 2)
                          var tuple = (1, p)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for nested tuples and infers the expected type information.
    /// </summary>
    [Fact]
    public void Analyze_NestedTuples_InfersCorrectly()
    {
        string source = """
                        routine test()
                          var nested = (1, (2, 3))
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for single element tuple with trailing comma.
    /// </summary>
    [Fact]
    public void Analyze_SingleElementTuple_WithTrailingComma()
    {
        string source = """
                        routine test()
                          var single = (42,)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Tuple Type Category

    /// <summary>
    /// Verifies semantic analysis behavior for tuple type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_TupleType_NoErrors()
    {
        // Tuple types are created in the instantiations cache
        // This test verifies tuple analysis succeeds without errors
        string source = """
                        routine test()
                          var tuple = (1s32, 2s32)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Mixed Types

    /// <summary>
    /// Verifies semantic analysis behavior for mixed numeric types successfully.
    /// </summary>
    [Fact]
    public void Analyze_MixedNumericTypes_Works()
    {
        string source = """
                        routine test()
                          var mixed = (1_s32, 2.5f64, true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for tuple with entity and infers tuple type information.
    /// </summary>
    [Fact]
    public void Analyze_TupleWithEntity_InfersTuple()
    {
        string source = """
                        entity User
                          id: S32

                        routine test()
                          var user = User(id: 42s32)
                          var tuple = (1, user)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region For-Loop Destructuring

    /// <summary>
    /// Verifies semantic analysis behavior for for loop destructuring non tuple and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_EachLoopDestructuring_NonTuple_ReportsError()
    {
        // Destructuring on non-tuple iterable (range produces integers, not tuples)
        string source = """
                        routine test()
                          each (a, b) in 0 til 10
                            var x = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DestructuringArityMismatch);
    }

    #endregion

    #region Tuple destructuring arity

    /// <summary>
    /// Verifies that assigning a 3-element tuple to a 2-element destructure target reports an arity error.
    /// </summary>
    [Fact]
    public void Analyze_TupleAssignmentArityMismatch_TooManySource_ReportsError()
    {
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          (a, b) = (1, 2, 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DestructuringArityMismatch);
    }

    /// <summary>
    /// Verifies that assigning a 2-element tuple to a 3-element destructure target reports an arity error.
    /// </summary>
    [Fact]
    public void Analyze_TupleAssignmentArityMismatch_TooFewSource_ReportsError()
    {
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          var c = 3
                          (a, b, c) = (1, 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DestructuringArityMismatch);
    }

    #endregion

    #region #173: Tuple assignment destructuring

    /// <summary>
    /// Verifies semantic analysis behavior for tuple assignment destructuring without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_TupleAssignmentDestructuring_NoError()
    {
        string source = """
                        routine test()
                          var a = 1
                          var b = 2
                          (a, b) = (b, a)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for tuple assignment non assignable target and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_TupleAssignmentNonAssignableTarget_ReportsError()
    {
        string source = """
                        routine test()
                          var a = 1
                          (a, 42) = (1, 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.InvalidAssignmentTarget);
    }

    #endregion
}
