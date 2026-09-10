using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for protocol validation.
/// </summary>
public class ProtocolValidationTests
{
    #region #61: Protocol mutation contract violation

    /// <summary>
    /// Verifies semantic analysis behavior for protocol readonly impl readonly without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ProtocolReadonlyImplReadonly_NoError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        record Foo obeys Displayable
                          value: S32
                        @readonly
                        routine Foo.display() -> Text
                          return "foo"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for protocol readonly impl writable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ProtocolReadonlyImplWritable_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text
                        record Bar obeys Displayable
                          value: S32
                        routine Bar.display() -> Text
                          return "bar"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for protocol writable impl readonly without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ProtocolWritableImplReadonly_NoError()
    {
        string source = """
                        protocol Mutator
                          routine Me.mutate()
                        record Baz obeys Mutator
                          value: S32
                        @readonly
                        routine Baz.mutate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for protocol writable impl reshaping and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ProtocolWritableImplReshaping_ReportsError()
    {
        // Protocol memberRoutine is writable by default; the impl is @reshaping — MORE mutating than
        // the contract allows, so it is rejected (a Modifying-token caller could trigger relocation).
        string source = """
                        protocol Mutator
                          routine Me.mutate()
                        entity Thing obeys Mutator
                          value: S32
                        @reshaping
                        routine Thing.mutate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }

    #endregion

    #region Reshaping protocol contract

    /// <summary>
    /// Verifies that a @reshaping protocol memberRoutine implemented as @reshaping produces no error.
    /// </summary>
    [Fact]
    public void Analyze_ProtocolReshapingImplReshaping_NoError()
    {
        string source = """
                        protocol Relocatable
                          @reshaping
                          routine Me.relocate()
                        entity Widget obeys Relocatable
                          value: S32
                        @reshaping
                        routine Widget.relocate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }

    /// <summary>
    /// Verifies that a @reshaping protocol memberRoutine implemented as plain writable produces no error
    /// (writable is more restrictive than reshaping, so the contract is satisfied).
    /// </summary>
    [Fact]
    public void Analyze_ProtocolReshapingImplWritable_NoError()
    {
        string source = """
                        protocol Relocatable
                          @reshaping
                          routine Me.relocate()
                        entity Widget obeys Relocatable
                          value: S32
                        routine Widget.relocate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }

    /// <summary>
    /// Verifies that a @readonly protocol memberRoutine implemented as @reshaping reports a contract violation.
    /// </summary>
    [Fact]
    public void Analyze_ProtocolReadonlyImplReshaping_ReportsError()
    {
        string source = """
                        protocol Reader
                          @readonly
                          routine Me.read() -> S32
                        entity Sensor obeys Reader
                          value: S32
                        @reshaping
                        routine Sensor.read() -> S32
                          return me.value
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMutationContractViolation);
    }

    #endregion
}
