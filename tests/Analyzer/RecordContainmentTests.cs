using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for record containment.
/// </summary>
public class RecordContainmentTests
{
    #region Valid Record MemberVariables (no errors expected)

    /// <summary>
    /// Verifies semantic analysis behavior for record with primitive member variables without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithPrimitiveMemberVariables_NoErrors()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record with record member variable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithRecordMemberVariable_NoErrors()
    {
        string source = """
                        record Inner
                          value: S32
                        record Outer
                          inner: Inner
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record with choice member variable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithChoiceMemberVariable_NoErrors()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE
                        record Pixel
                          x: S32
                          y: S32
                          color: Color
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic record with type parameter without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecordWithTypeParameter_NoErrors()
    {
        // Generic type parameters are validated at instantiation time, not definition time
        string source = """
                        record Container[T]
                          value: T
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic record multiple type params without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_GenericRecordMultipleTypeParams_NoErrors()
    {
        string source = """
                        record Pair[K, V]
                          key: K
                          value: V
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with retained field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithRetainedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record Handle
                          ref: Retained[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with shared field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithSharedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record Handle
                          ref: Guarded[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with hijacked field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithHijackedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record RawHandle
                          ptr: Hijacked[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for nested record with retained field without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_NestedRecordWithRetainedField_NoErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record Inner
                          ref: Retained[Node]
                        record Outer
                          inner: Inner
                          count: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    #endregion

    #region Invalid Record MemberVariables (errors expected)

    /// <summary>
    /// Verifies a record MAY contain an entity-typed field. Entities are reference (pointer-shaped)
    /// types, so the field stores a reference — only the scoped access tokens (Viewing/Modifying/
    /// Consulting/Amending) are rejected as record members (see the token tests below).
    /// </summary>
    [Fact]
    public void Analyze_RecordWithEntityReferenceField_NoError()
    {
        string source = """
                        entity User
                          name: Text
                        record BadRecord
                          user: User
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with viewing field and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithViewedField_ReportsError()
    {
        // Scoped tokens are caught by S601 (TokenMemberVariableNotAllowed) before S412
        string source = """
                        entity Node
                          value: S32
                        record BadRecord
                          view: Viewing[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Code == SemanticDiagnosticCode.TokenMemberVariableNotAllowed &&
                e.Message.Contains(value: "view"));
    }

    /// <summary>
    /// Verifies semantic analysis behavior for record with modifying field and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithModifyingField_ReportsError()
    {
        // Scoped tokens are caught by S601 (TokenMemberVariableNotAllowed) before S412
        string source = """
                        entity Node
                          value: S32
                        record BadRecord
                          modifying: Modifying[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Code == SemanticDiagnosticCode.TokenMemberVariableNotAllowed &&
                e.Message.Contains(value: "modifying"));
    }

    #endregion

    #region Multiple token fields both rejected

    /// <summary>
    /// Verifies that two scoped-token fields on the same record both report TokenMemberVariableNotAllowed.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithTwoTokenFields_BothReportErrors()
    {
        string source = """
                        entity Node
                          value: S32
                        record BadRecord
                          reader: Viewing[Node]
                          writer: Modifying[Node]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count(predicate: e =>
            e.Code == SemanticDiagnosticCode.TokenMemberVariableNotAllowed) >= 2);
    }

    #endregion

    #region Variant field in record

    /// <summary>
    /// Verifies that a record can contain a variant-typed field.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithVariantField_NoError()
    {
        string source = """
                        variant Status
                          S32
                          Text

                        record Result
                          status: Status
                          code: S32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    #endregion

    #region With expression field validation

    /// <summary>
    /// Verifies that a with expression referencing a non-existent field reports an error.
    /// </summary>
    [Fact]
    public void Analyze_WithExpressionUnknownField_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .z = 5
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    /// <summary>
    /// Verifies that a with expression updating multiple fields with correct types produces no error.
    /// </summary>
    [Fact]
    public void Analyze_WithExpressionMultipleValidFields_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                          z: S32
                        routine test(p: Point)
                          var q = p with .x = 1, .z = 3
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }

    #endregion

    #region With Expression on Non-Records

    /// <summary>
    /// Verifies semantic analysis behavior for with on entity and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_WithOnEntity_ReportsError()
    {
        string source = """
                        entity Foo
                          x: S32
                        routine test(f: Foo)
                          var g = f with .x = 2
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for with on record without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WithOnRecord_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for with on record multi member variable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WithOnRecordMultiMemberVariable_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32
                        routine test(p: Point)
                          var q = p with .x = 2, .y = 3
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithExpressionNotRecord);
    }

    #endregion
}
