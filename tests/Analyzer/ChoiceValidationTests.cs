using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for choice type validation.
/// Choices are S32-backed enums with all-or-nothing explicit values,
/// duplicate value detection, and range validation.
/// </summary>
public class ChoiceValidationTests
{
    #region Valid Choices (no errors expected)

    /// <summary>
    /// Verifies semantic analysis behavior for simple choice without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_SimpleChoice_NoErrors()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                          EAST
                          WEST
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice with explicit values without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceWithExplicitValues_NoErrors()
    {
        string source = """
                        choice HttpStatus
                          OK: 200
                          NOT_FOUND: 404
                          ERROR: 500
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice with negative values without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceWithNegativeValues_NoErrors()
    {
        string source = """
                        choice ComparisonSign
                          ME_SMALL: -1
                          SAME: 0
                          ME_LARGE: 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseValueOverflow);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice with large values overflow error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceWithLargeValues_OverflowError()
    {
        // Values exceeding S32 range should produce overflow errors
        string source = """
                        choice BigValues
                          SMALL: 0
                          LARGE: 3000000000
                          HUGE: 9000000000000000000
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseValueOverflow);
    }

    #endregion

    #region Mixed Values (error expected)

    /// <summary>
    /// Verifies semantic analysis behavior for choice mixed values and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceMixedValues_ReportsError()
    {
        string source = """
                        choice Bad
                          FIRST: 1
                          SECOND
                          THIRD: 3
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
    }

    #endregion

    #region Duplicate Values (error expected)

    /// <summary>
    /// Verifies semantic analysis behavior for choice duplicate explicit values and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceDuplicateExplicitValues_ReportsError()
    {
        string source = """
                        choice Duplicated
                          FIRST: 1
                          SECOND: 2
                          THIRD: 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }

    #endregion

    #region Operator Prohibition (choices do not support any operators)

    /// <summary>
    /// Verifies semantic analysis behavior for choice addition and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceAddition_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test()
                          var d = NORTH
                          var x = d + SOUTH
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice compound assignment and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceCompoundAssignment_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test()
                          var d = NORTH
                          d += SOUTH
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice bitwise and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceBitwise_ReportsError()
    {
        string source = """
                        choice Flags
                          A
                          B

                        routine test()
                          var f = A
                          var x = f & B
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice comparison and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceComparison_ReportsError()
    {
        string source = """
                        choice Priority
                          LOW
                          HIGH

                        routine test()
                          var p = LOW
                          var x = p < HIGH
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for choice equality and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceEquality_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test()
                          var d = NORTH
                          var same = d == SOUTH
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    #endregion

    #region Single-Member and Zero-Value Choices

    /// <summary>
    /// Verifies that a choice with a single member produces no errors.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceWithSingleMember_NoError()
    {
        string source = """
                        choice Singleton
                          ONLY
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceMixedValues);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }

    /// <summary>
    /// Verifies that zero is a valid explicit choice value.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceWithZeroValue_NoError()
    {
        string source = """
                        choice Ternary
                          NEG: -1
                          ZERO: 0
                          POS: 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceCaseValueOverflow);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }

    /// <summary>
    /// Verifies that two members with the same explicit value report a duplicate-value error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceTwoMembersSameValue_ReportsError()
    {
        string source = """
                        choice Alias
                          FIRST: 5
                          SECOND: 5
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ChoiceDuplicateValue);
    }

    /// <summary>
    /// Verifies that a choice used unqualified inside a routine resolves without error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceUnqualifiedInRoutine_NoError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH

                        routine test(d: Direction) -> Bool
                          return d == NORTH
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }

    #endregion

    #region Member Access (C98)

    /// <summary>
    /// Verifies that the test validates member access as value.
    /// </summary>
    [Fact]
    public void Choice_MemberAccess_AsValue()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = Color.RED
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>
    /// Verifies that the test validates member access invalid case.
    /// </summary>
    [Fact]
    public void Choice_MemberAccess_InvalidCase()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = Color.PURPLE
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }
    /// <summary>
    /// Verifies that the test validates member access assignment and comparison.
    /// </summary>
    [Fact]
    public void Choice_MemberAccess_Assignment_And_Comparison()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = Color.RED
                          var same = (c == Color.BLUE)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }

    #endregion
}
