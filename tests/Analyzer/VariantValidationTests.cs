using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for variant validation.
/// </summary>
public class VariantValidationTests
{
    #region #59: Variant member containment

    /// <summary>
    /// Verifies semantic analysis behavior for variant with record member without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_VariantWithRecordMember_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        variant Shape
                          S32
                          Point
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for variant with primitive member without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_VariantWithPrimitiveMember_NoError()
    {
        string source = """
                        variant Value
                          S32
                          F64
                          None
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    #endregion

    #region Additional Member Types

    /// <summary>
    /// Verifies that a variant can contain an entity-typed case.
    /// </summary>
    [Fact]
    public void Analyze_VariantWithEntityMember_NoError()
    {
        string source = """
                        entity Node
                          value: S32

                        variant Container
                          Node
                          S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    /// <summary>
    /// Verifies that a variant can contain a choice-typed case.
    /// </summary>
    [Fact]
    public void Analyze_VariantWithChoiceMember_NoError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        variant Value
                          Color
                          S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    /// <summary>
    /// Verifies that a variant can contain Text as a case.
    /// </summary>
    [Fact]
    public void Analyze_VariantWithTextMember_NoError()
    {
        string source = """
                        variant MaybeText
                          Text
                          None
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    /// <summary>
    /// Verifies that a variant with named cases and payload types produces no errors.
    /// </summary>
    [Fact]
    public void Analyze_VariantWithNamedCases_NoError()
    {
        string source = """
                        variant Result
                          SUCCESS: S32
                          FAILURE: Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.VariantCaseContainsInvalidType);
    }

    #endregion
}
