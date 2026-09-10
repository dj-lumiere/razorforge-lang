using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for type prohibition.
/// </summary>
public class TypeProhibitionTests
{
    #region None as Type Argument (rejected)

    /// <summary>
    /// Verifies semantic analysis behavior for blank nullable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_NoneNullable_ReportsError()
    {
        // None? desugars to Maybe<None>, which is prohibited
        string source = """
                        routine foo(x: None?)
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for explicit maybe blank and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ExplicitMaybeNone_ReportsError()
    {
        string source = """
                        routine bar() -> Maybe[None]
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }

    #endregion

    #region Normal nullable types (allowed)

    /// <summary>
    /// Verifies semantic analysis behavior for normal nullable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_NormalNullable_NoErrors()
    {
        string source = """
                        routine foo(x: S32?)
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for blank direct type without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_NoneDirectType_NoErrors()
    {
        // None as a direct type (not wrapped in a generic) is fine
        string source = """
                        routine foo() -> None
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for result blank without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ResultNone_NoErrors()
    {
        // Result<None> is allowed for failable void routines
        string source = """
                        routine foo(x: Result[None])
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lookup blank and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_LookupNone_ReportsError()
    {
        // Lookup<None> is ambiguous: None is also the absent sentinel in the type_id carrier.
        string source = """
                        routine foo(x: Lookup[None])
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }

    #endregion

    #region Nested Maybe Prohibition

    /// <summary>
    /// Verifies semantic analysis behavior for nested maybe and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_NestedMaybe_ReportsError()
    {
        string source = """
                        routine test(x: Maybe[Maybe[S32]])
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NestedMaybeProhibited);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for single maybe without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_SingleMaybe_NoError()
    {
        string source = """
                        routine test(x: S32?)
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NestedMaybeProhibited);
    }

    #endregion

    #region Additional nullable and collection prohibitions

    /// <summary>
    /// Verifies that Text? (nullable Text) is allowed and produces no error.
    /// </summary>
    [Fact]
    public void Analyze_TextNullable_NoError()
    {
        string source = """
                        routine find!(text: Text?) -> Text
                          if text is None
                            absent
                          return text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NoneAsTypeArgument);
    }

    /// <summary>
    /// Verifies that Maybe[S32?] (explicit nested Maybe) is rejected.
    /// </summary>
    [Fact]
    public void Analyze_ExplicitNestedMaybe_ReportsError()
    {
        string source = """
                        routine test(x: Maybe[S32?])
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.NestedMaybeProhibited);
    }

    #endregion

    #region Byte Literal ASCII Validation

    /// <summary>
    /// Verifies semantic analysis behavior for byte literal non ascii and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ByteLiteralNonAscii_ReportsError()
    {
        // Non-ASCII byte literals are rejected at the lexer level (RF-G005)
        string source = """
                        routine test()
                          var x: Byte = b'é'
                          return
                        """;

        Assert.ThrowsAny<GrammarException>(testCode: () => AnalyzeSa(source: source));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for byte literal ascii without grammar diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ByteLiteralAscii_NoGrammarError()
    {
        // ASCII byte literals pass the lexer without throwing
        string source = """
                        routine test()
                          var x: Byte = b'a'
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // No GrammarException thrown -> lexer accepts ASCII byte literals
        Assert.NotNull(@object: result);
    }

    #endregion
}
