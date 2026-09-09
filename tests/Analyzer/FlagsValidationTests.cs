using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for flags validation.
/// </summary>
public class FlagsValidationTests
{
    #region Valid Flags (no errors expected)

    /// <summary>
    /// Verifies flags validation behavior for simple declaration without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_SimpleDeclaration_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsDuplicateMember);
    }
    /// <summary>
    /// Verifies flags validation behavior for is test valid member without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_IsTest_ValidMember_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms is READ
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }
    /// <summary>
    /// Verifies flags validation behavior for is not test valid member without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_IsNotTest_ValidMember_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms isnot WRITE
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
    }
    /// <summary>
    /// Verifies that exact-flags equality via '==' produces no errors.
    /// </summary>
    [Fact]
    public void Flags_ExactEqualityWithAnd_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms == (Permissions.READ and Permissions.WRITE)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies flags validation behavior for but operator same type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_ButOperator_SameType_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(a: Permissions, b: Permissions)
                          var result = a but b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }
    /// <summary>
    /// Verifies flags validation behavior for and combiner same type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_AndCombiner_SameType_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(a: Permissions, b: Permissions)
                          var combined = a and b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LogicalOperatorRequiresBool);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }
    /// <summary>
    /// Verifies flags validation behavior for and combiner different types and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_AndCombiner_DifferentTypes_ReportsError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE

                        flags Roles
                          ADMIN
                          USER

                        routine test(p: Perms, r: Roles)
                          var combined = p and r
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LogicalOperatorRequiresBool);
    }
    /// <summary>
    /// Verifies flags validation behavior for all on all off without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_AllOnAllOff_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test()
                          var all = Permissions.all_on()
                          var none = Permissions.all_off()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberRoutineNotFound);
    }

    #endregion

    #region #127: Max 64 members

    /// <summary>
    /// Verifies flags validation behavior for more than64 members and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_MoreThan64Members_ReportsError()
    {
        string members = string.Join(separator: "\n  ",
            values: Enumerable.Range(start: 0, count: 65)
                              .Select(selector: i => $"FLAG_{i}"));
        string source = $$"""
                          flags TooMany
                            {{members}}
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
    }
    /// <summary>
    /// Verifies flags validation behavior for exactly64 members without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_Exactly64Members_NoError()
    {
        string members = string.Join(separator: "\n  ",
            values: Enumerable.Range(start: 0, count: 64)
                              .Select(selector: i => $"FLAG_{i}"));
        string source = $$"""
                          flags Max64
                            {{members}}
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
    }

    #endregion

    #region Duplicate members

    /// <summary>
    /// Verifies flags validation behavior for duplicate member and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_DuplicateMember_ReportsError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE
                          READ
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsDuplicateMember);
    }

    #endregion

    #region #128: or in assignment

    /// <summary>
    /// Verifies flags validation behavior for or in assignment and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_OrInAssignment_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(a: Permissions, b: Permissions)
                          var combined = a or b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsOrInAssignment);
    }

    #endregion

    #region #129: Flags when requires else

    /// <summary>
    /// Verifies flags validation behavior for when expression without else and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_WhenExpressionWithoutElse_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var desc = when perms
                            is READ => "read"
                            is WRITE => "write"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }
    /// <summary>
    /// Verifies flags validation behavior for when expression with else without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Flags_WhenExpressionWithElse_NoError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var desc = when perms
                            is READ => "read"
                            else => "other"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NonExhaustiveMatch);
    }

    #endregion


    #region #134: No arithmetic on flags

    /// <summary>
    /// Verifies flags validation behavior for arithmetic and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_Arithmetic_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(a: Permissions, b: Permissions)
                          var result = a + b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnFlagsType);
    }

    #endregion

    #region #135: No custom operators on flags

    /// <summary>
    /// Verifies flags validation behavior for custom operator and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_CustomOperator_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        @readonly
                        routine Permissions.$add(you: Permissions) -> Permissions
                          return me
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsCustomOperatorNotAllowed);
    }

    #endregion

    #region Flag member validation

    /// <summary>
    /// Verifies flags validation behavior for is test unknown member and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_IsTest_UnknownMember_ReportsError()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test(perms: Permissions)
                          var result = perms is EXECUTE
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
    }
    /// <summary>
    /// Verifies flags validation behavior for is test on non flags and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_IsTestOnNonFlags_ReportsError()
    {
        string source = """
                        routine test(x: S32)
                          var result = x is READ
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // 'READ' is not a type, so this produces UnknownType (not FlagsTypeMismatch)
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownType);
    }
    /// <summary>
    /// Verifies flags validation behavior for but operator type mismatch and reports the expected error.
    /// </summary>
    [Fact]
    public void Flags_ButOperator_TypeMismatch_ReportsError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE

                        flags Roles
                          ADMIN
                          USER

                        routine test(p: Perms, r: Roles)
                          var result = p but r
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTypeMismatch);
    }

    #endregion

    #region Additional valid flag patterns

    /// <summary>
    /// Verifies that a single-member flags type is valid.
    /// </summary>
    [Fact]
    public void Flags_SingleMember_NoErrors()
    {
        string source = """
                        flags Flag
                          ONLY_ONE
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsTooManyMembers);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsDuplicateMember);
    }

    /// <summary>
    /// Verifies that exact-flags equality via '==' with three members produces no error.
    /// </summary>
    [Fact]
    public void Flags_ExactEqualityWithThreeMembers_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms == (Permissions.READ and Permissions.WRITE and Permissions.EXECUTE)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that an is test combining two valid members with and produces no error.
    /// </summary>
    [Fact]
    public void Flags_IsTest_CombinedAnd_NoErrors()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test(perms: Permissions)
                          var result = perms is READ and WRITE
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
    }

    #endregion

    #region Member Access (C98)

    /// <summary>
    /// Verifies flags validation behavior for member access as value.
    /// </summary>
    [Fact]
    public void Flags_MemberAccess_AsValue()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE
                          EXECUTE

                        routine test()
                          var f = Permissions.READ
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FlagsMemberNotFound);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>
    /// Verifies flags validation behavior for member access invalid member.
    /// </summary>
    [Fact]
    public void Flags_MemberAccess_InvalidMember()
    {
        string source = """
                        flags Permissions
                          READ
                          WRITE

                        routine test()
                          var f = Permissions.EXECUTE
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }

    #endregion
}
