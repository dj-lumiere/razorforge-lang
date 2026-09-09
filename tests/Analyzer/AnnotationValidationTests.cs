using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for annotation and conditional expression validation rules:
/// #145: Nested conditional expression warning (defense-in-depth; parser currently prevents nesting)
/// </summary>
public class AnnotationValidationTests
{
    #region #145: Nested conditional expression warning

    /// <summary>
    /// Verifies semantic analysis behavior for simple conditional without unexpected warnings.
    /// </summary>
    [Fact]
    public void Analyze_SimpleConditional_NoWarning()
    {
        string source = """
                        routine test(x: Bool) -> S32
                          return if x then 1_s32 else 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for conditional in routine without unexpected warnings.
    /// </summary>
    [Fact]
    public void Analyze_ConditionalInRoutine_NoWarning()
    {
        string source = """
                        routine classify(x: S32) -> Text
                          return if x > 0_s32 then "positive" else "non-positive"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }

    #endregion

    #region Additional conditional expression contexts

    /// <summary>
    /// Verifies that a conditional assigned to a var produces no warning.
    /// </summary>
    [Fact]
    public void Analyze_ConditionalAssignedToVar_NoWarning()
    {
        string source = """
                        routine test(x: Bool)
                          var result = if x then 1_s32 else 0_s32
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }

    /// <summary>
    /// Verifies that two sequential conditional expressions in the same routine produce no warning.
    /// </summary>
    [Fact]
    public void Analyze_TwoSequentialConditionals_NoWarning()
    {
        string source = """
                        routine test(a: Bool, b: Bool) -> S32
                          var x = if a then 1_s32 else 0_s32
                          var y = if b then 2_s32 else 0_s32
                          return x
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }

    /// <summary>
    /// Verifies that a conditional with a non-trivial condition expression produces no warning.
    /// </summary>
    [Fact]
    public void Analyze_ConditionalWithComplexCondition_NoWarning()
    {
        string source = """
                        routine test(x: S32, y: S32) -> Text
                          return if x > y then "greater" else "not greater"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.NestedConditionalExpression);
    }

    #endregion
}
