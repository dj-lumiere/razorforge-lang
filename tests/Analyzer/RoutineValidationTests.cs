using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for routine validation.
/// </summary>
public class RoutineValidationTests
{
    #region #157: Mutation category conflict

    /// <summary>
    /// Verifies semantic analysis behavior for single mutation annotation without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_SingleMutationAnnotation_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        @readonly
                        routine Point.get_x() -> S32
                          return me.x
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationCategoryConflict);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for conflicting mutation annotations and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ConflictingMutationAnnotations_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        @readonly
                        @reshaping
                        routine Point.set_x(new_x: S32)
                          me.x = new_x
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationCategoryConflict);
    }

    #endregion

    #region Reshaping annotation

    /// <summary>
    /// Verifies that a single @reshaping annotation on a memberRoutine produces no conflict error.
    /// </summary>
    [Fact]
    public void Analyze_ReshapingAnnotationAlone_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @reshaping
                        routine Counter.reset(value: S32)
                          me.count = value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationCategoryConflict);
    }

    /// <summary>
    /// Verifies that @reshaping combined with @readonly reports a mutation category conflict.
    /// </summary>
    [Fact]
    public void Analyze_ReshapingAndReadonly_ReportsConflict()
    {
        string source = """
                        entity Counter
                          count: S32

                        @reshaping
                        @readonly
                        routine Counter.peek() -> S32
                          return me.count
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationCategoryConflict);
    }

    #endregion

    #region Common routine on entity

    /// <summary>
    /// Verifies that a common routine on an entity type works the same as on a record.
    /// </summary>
    [Fact]
    public void Analyze_CommonRoutineOnEntity_NoError()
    {
        string source = """
                        entity Node
                          value: S32

                        common routine Node.empty() -> Node
                          return Node(value: 0)

                        routine test()
                          var n = Node.empty()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CommonRoutineMismatch);
    }

    #endregion

    #region #151: Static/instance mismatch

    /// <summary>
    /// Verifies semantic analysis behavior for common routine called on instance and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_CommonRoutineCalledOnInstance_ReportsError()
    {
        // `make`, not `create`: `create` is the reserved constructor identity (RoutineKind.Creator),
        // so a `common routine T.create()` is folded to a constructor and could never be a common
        // routine. Use a plain common routine name to exercise the static/instance mismatch.
        string source = """
                        record Counter
                          value: S32
                        common routine Counter.make() -> Counter
                          return Counter(value: 0)

                        routine test()
                          var c = Counter(value: 0)
                          var d = c.make()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CommonRoutineMismatch);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for common routine called on type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_CommonRoutineCalledOnType_NoError()
    {
        string source = """
                        record Counter
                          value: S32
                        common routine Counter.create() -> Counter
                          return Counter(value: 0)

                        routine test()
                          var c = Counter.create()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CommonRoutineMismatch);
    }

    #endregion
}
