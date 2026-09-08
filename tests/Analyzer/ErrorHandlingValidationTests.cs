using Compiler.Diagnostics;
using Compiler.Verification.Results;
using TypeModel.Symbols;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for error handling validation.
/// </summary>
public class ErrorHandlingValidationTests
{
    #region Throw Needs Named Crashable Type
    /// <summary>
    /// Verifies semantic analysis behavior for throw entity no record error.
    /// </summary>

    [Fact]
    public void Analyze_ThrowEntity_NoRecordError()
    {
        string source = """
                        crashable BadError
                          message: Text
                        routine test!() -> S32
                          throw BadError(message: "oops")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for throw record no record error.
    /// </summary>

    [Fact]
    public void Analyze_ThrowRecord_NoRecordError()
    {
        string source = """
                        crashable MyError
                          message: Text
                        routine test!() -> S32
                          throw MyError(message: "oops")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
    }

    #endregion

    #region Failable Without Throw or Absent
    /// <summary>
    /// Verifies semantic analysis behavior for failable without throw or absent and reports the expected warning.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithoutThrowOrAbsent_ReportsWarning()
    {
        string source = """
                        routine useless!() -> S32
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for failable with throw without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithThrow_NoError()
    {
        string source = """
                        crashable MyError
                          message: Text
                        routine useful!() -> S32
                          throw MyError(message: "bad")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for failable with absent without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithAbsent_NoError()
    {
        string source = """
                        routine find!() -> S32
                          absent
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    #endregion

    #region @crash_only Validation (#76)
    /// <summary>
    /// Verifies semantic analysis behavior for crash only on non failable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_CrashOnlyOnNonFailable_ReportsError()
    {
        string source = """
                        @crash_only
                        routine safe_routine() -> S32
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CrashOnlyOnNonFailable);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for crash only on failable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_CrashOnlyOnFailable_NoError()
    {
        string source = """
                        crashable MyError
                          message: Text
                        @crash_only
                        routine crash_routine!() -> S32
                          throw MyError(message: "fatal")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CrashOnlyOnNonFailable);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for crash only suppresses variant generation.
    /// </summary>

    [Fact]
    public void Analyze_CrashOnlySuppressesVariantGeneration()
    {
        string source = """
                        crashable MyError
                          message: Text
                        @crash_only
                        routine crash_routine!() -> S32
                          throw MyError(message: "fatal")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Should NOT generate try_, check_, or lookup_ variants
        Assert.Null(@object: result.Registry.GetRoutine(name: "try_crash_routine"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "check_crash_routine"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "lookup_crash_routine"));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for non crash only generates variants.
    /// </summary>

    [Fact]
    public void Analyze_NonCrashOnlyGeneratesVariants()
    {
        string source = """
                        crashable MyError
                          message: Text
                        routine normal_routine!() -> S32
                          throw MyError(message: "error")

                        routine trigger_variants() -> S32?
                          return try_normal_routine()
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Without @crash_only, variants are synthesized ON DEMAND — the trigger call above generates it.
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_normal_routine");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Unhandled Crashable Call (#159)
    /// <summary>
    /// A bare failable call used as a statement is currently NOT flagged with the
    /// UnhandledCrashableCall warning — that advisory is intentionally not enforced.
    /// </summary>

    [Fact]
    public void Analyze_FailableCallAsStatement_InNonFailable_NotFlagged()
    {
        string source = """
                        crashable ParseError
                          message: Text
                        routine parse!(data: S32) -> S32
                          throw ParseError(message: "bad")
                        routine caller() -> S32
                          parse!(data: 1)
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.UnhandledCrashableCall);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for failable call as statement in failable without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_FailableCallAsStatement_InFailable_NoError()
    {
        string source = """
                        crashable ParseError
                          message: Text
                        routine parse!(data: S32) -> S32
                          throw ParseError(message: "bad")
                        routine caller!() -> S32
                          parse!(data: 1)
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Warnings,
            filter: w => w.Code == SemanticWarningCode.UnhandledCrashableCall);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for lookup variable not dismantled before scope exit and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_LookupVariable_NotDismantledBeforeScopeExit_ReportsError()
    {
        string source = """
                        crashable DbError
                          message: Text

                        @readonly
                        routine DbError.crash_message() -> Text
                          return me.message

                        routine get_value!(id: U64) -> S32
                          if id == 0
                            throw DbError(message: "bad")
                          unless id == 1
                            absent
                          return 42

                        routine test()
                          var pending = lookup_get_value(id: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LookupNotDismantled);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for result copied from variable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ResultCopiedFromVariable_ReportsError()
    {
        string source = """
                        crashable ParseError
                          message: Text

                        @readonly
                        routine ParseError.crash_message() -> Text
                          return me.message

                        routine validate!(value: S32) -> S32
                          if value < 0
                            throw ParseError(message: "negative")
                          return value

                        routine test()
                          var first = check_validate(value: 1)
                          var second = first
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeStoredInVariable);
    }

    #endregion

    #region Throw target validation

    /// <summary>
    /// Verifies that throwing a primitive integer reports ThrowRequiresRecordType.
    /// </summary>
    [Fact]
    public void Analyze_ThrowPrimitive_ReportsError()
    {
        string source = """
                        routine test!() -> S32
                          throw 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowNotCrashable);
    }

    /// <summary>
    /// Verifies that a failable routine can throw different crashable types on different paths.
    /// </summary>
    [Fact]
    public void Analyze_MultipleCrashableTypes_AllThrow_NoError()
    {
        string source = """
                        crashable ErrA
                          message: Text
                        crashable ErrB
                          code: S32
                        routine test!(flag: Bool) -> S32
                          if flag
                            throw ErrA(message: "a")
                          throw ErrB(code: 1)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowRequiresRecordType);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    /// <summary>
    /// Verifies that a failable routine calling another failable propagates failability without error.
    /// </summary>
    [Fact]
    public void Analyze_FailableCallingFailable_Propagates_NoError()
    {
        string source = """
                        crashable MyErr
                          message: Text
                        routine inner!(value: S32) -> S32
                          if value < 0
                            throw MyErr(message: "bad")
                          return value
                        routine outer!(value: S32) -> S32
                          return inner!(value: value)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    #endregion

    // NOTE: #81 (Result/Lookup storage restriction) tests require multi-module test
    // infrastructure since check_/lookup_ variants are generated in Phase 5 but body
    // analysis happens in Phase 3. The implementation is in place and will be validated
    // when multi-module compilation support is available for tests.
}
