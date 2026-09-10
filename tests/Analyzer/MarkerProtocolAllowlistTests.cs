using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the marker-protocol closed allowlist (S707) and the @llvm("typename") pass-body
/// rule (S708). Together these enforce the soundness invariant for build-time-dispatched
/// marker protocols (<c>Accessing[T]</c>/<c>Controlling[T]</c>): obeyers must share T's ptr
/// layout, which is guaranteed only for a closed set of stdlib wrappers whose @llvm("ptr")
/// annotation dictates the layout and whose bodies are `pass`.
/// </summary>
public class MarkerProtocolAllowlistTests
{
    #region S707 — MarkerProtocolLayoutViolation

    /// <summary>Verifies user-defined record obeying Accessing[T] reports S707.</summary>
    [Fact]
    public void UserRecord_ObeysReferring_ReportsS707()
    {
        string source = """
                        entity Bar
                          pass
                        @llvm("ptr")
                        record Weird[T] obeys Accessing[T]
                        needs EntityType T
                          pass
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MarkerProtocolLayoutViolation);
    }

    /// <summary>Verifies user-defined record obeying Controlling[T] reports S707.</summary>
    [Fact]
    public void UserRecord_ObeysControlling_ReportsS707()
    {
        // Controlling[T] extends Accessing[T] — same allowlist applies.
        string source = """
                        entity Bar
                          pass
                        @llvm("ptr")
                        record Sneaky[T] obeys Controlling[T]
                        needs EntityType T
                          pass
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MarkerProtocolLayoutViolation);
    }

    /// <summary>Verifies blessed stdlib wrappers (Owned, Retained, etc.) do not trigger S707.</summary>
    [Fact]
    public void StdlibWrapper_ObeysControlling_NoS707()
    {
        // Retained/Viewing/Modifying/Hijacked/Tracked are blessed — they declare obeys
        // Accessing/Controlling in stdlib without tripping the check. Importing IO/Console
        // forces stdlib to load (and re-validate) in the test harness.
        string source = """
                        import IO/Console
                        routine start()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MarkerProtocolLayoutViolation);
    }

    /// <summary>Verifies implicit entity conformance to Accessing[T] does not trigger S707.</summary>
    [Fact]
    public void UserEntity_AutoConformanceToReferring_NoS707()
    {
        // Entity T trivially obeys Accessing[T]/Controlling[T] via _implicitProtocolConformances —
        // the closed-allowlist check exempts implicit conformances.
        string source = """
                        entity Foo
                          pass
                        routine start()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MarkerProtocolLayoutViolation);
    }

    #endregion

    #region S708 — LlvmAnnotatedRecordMustHavePassBody

    /// <summary>Verifies @llvm-annotated record with fields reports S708.</summary>
    [Fact]
    public void LlvmAnnotatedRecord_WithFields_ReportsS708()
    {
        string source = """
                        entity Bar
                          pass
                        @llvm("ptr")
                        record BadWrapper[T]
                        needs EntityType T
                          extra_field: S64
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LlvmAnnotatedRecordMustHavePassBody);
    }

    /// <summary>Verifies @llvm-annotated record with pass body does not trigger S708.</summary>
    [Fact]
    public void LlvmAnnotatedRecord_PassBody_NoS708()
    {
        string source = """
                        entity Bar
                          pass
                        @llvm("ptr")
                        record TightWrapper[T]
                        needs EntityType T
                          pass
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LlvmAnnotatedRecordMustHavePassBody);
    }

    /// <summary>Verifies non-@llvm record with fields does not trigger S708.</summary>
    [Fact]
    public void NonLlvmAnnotatedRecord_WithFields_NoS708()
    {
        // S708 is gated on @llvm presence — normal records freely declare fields.
        string source = """
                        record Plain
                          value: S64
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LlvmAnnotatedRecordMustHavePassBody);
    }

    #endregion
}
