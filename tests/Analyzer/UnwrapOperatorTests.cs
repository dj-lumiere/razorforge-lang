using Compiler.Diagnostics;
using SyntaxTree;
using Compiler.Verification.Results;
using TypeModel.Symbols;
using TypeModel.Types;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for unwrap operator.
/// </summary>
public class UnwrapOperatorTests
{
    #region Built-in error handling infrastructure -> try_ variant generation

    /// <summary>
    /// Verifies that the test validates variant generated and returns the generated Maybe type.
    /// </summary>
    [Fact]
    public void TryVariant_Generated_ReturnsMaybeType()
    {
        // Verify that failable routines produce try_ variants with error handling return types
        string source = """
                        routine get!(flag: Bool) -> S64
                          if flag
                            absent
                          return 42

                        routine trigger_variants(flag: Bool) -> S64?
                          return try_get(flag: flag)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // Variant is synthesized ON DEMAND — the trigger call above generates try_get.
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_get");
        Assert.NotNull(@object: tryVariant);
        Assert.IsType<RecordTypeInfo>(@object: tryVariant.ReturnType);
    }

    /// <summary>
    /// Verifies that the test validates unwrap direct call in failable and returns the expected inner type.
    /// </summary>
    [Fact]
    public void ForceUnwrap_DirectCallInFailable_ReturnsInnerType()
    {
        // get!() in a failable context propagates error, returns S64 (inner type).
        // !! on S64 should report TypeDoesNotSupportOperator since the call
        // already unwraps in failable context.
        string source = """
                        routine get!(flag: Bool) -> S64
                          if flag
                            absent
                          return 42
                        routine test!() -> S64
                          var x = get!(flag: true)!!
                          return x
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // get!() returns S64 in failable context ??!! on S64 is invalid
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates coalesce direct call in failable and returns the expected inner type.
    /// </summary>
    [Fact]
    public void NoneCoalesce_DirectCallInFailable_ReturnsInnerType()
    {
        // get!() in a failable context returns S64, not Maybe[S64].
        // ?? on S64 should report TypeDoesNotSupportOperator.
        string source = """
                        routine get!(flag: Bool) -> S64
                          if flag
                            absent
                          return 42
                        routine test!() -> S64
                          var x = get!(flag: true) ?? 0
                          return x
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region !! on plain types without $unwrap -> error

    /// <summary>
    /// Verifies that the test validates unwrap on S64 and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnS64_ReportsError()
    {
        string source = """
                        routine test()
                          var x = 42!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates unwrap on bool and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnBool_ReportsError()
    {
        string source = """
                        routine test()
                          var x = true!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates unwrap on record and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnRecord_ReportsError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64
                        routine test()
                          var p = Point(x: 1, y: 2)
                          var q = p!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates unwrap on entity and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnEntity_ReportsError()
    {
        string source = """
                        entity Node
                          value: S64
                        routine test()
                          var n = Node(value: 1)
                          var m = n!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates unwrap on text and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnText_ReportsError()
    {
        string source = """
                        routine test()
                          var t = "hello"!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region ?? on plain types without $unwrap_or -> error

    /// <summary>
    /// Verifies that the test validates coalesce on S64 and reports the expected error.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnS64_ReportsError()
    {
        string source = """
                        routine test()
                          var x = 42 ?? 0
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates coalesce on bool and reports the expected error.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnBool_ReportsError()
    {
        string source = """
                        routine test()
                          var x = true ?? false
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates coalesce on record and reports the expected error.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnRecord_ReportsError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64
                        routine test()
                          var p = Point(x: 1, y: 2)
                          var q = Point(x: 0, y: 0)
                          var r = p ?? q
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region !! on user type with $unwrap -> no error

    /// <summary>
    /// Verifies that the test validates unwrap on user type with unwrap without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnUserTypeWithUnwrap_NoError()
    {
        string source = """
                        record Wrapper
                          value: S64
                        @readonly
                        routine Wrapper.unwrap() -> S64
                          return me.value
                        routine test()
                          var w = Wrapper(value: 42)
                          var x = w!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates unwrap on entity with unwrap without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnEntityWithUnwrap_NoError()
    {
        string source = """
                        entity Box
                          value: S64
                        @readonly
                        routine Box.unwrap() -> S64
                          return me.value
                        routine test()
                          var b = Box(value: 99)
                          var x = b!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region ?? on user type with $unwrap_or -> no error

    /// <summary>
    /// Verifies that the test validates coalesce on user type with unwrap or without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnUserTypeWithUnwrapOr_NoError()
    {
        string source = """
                        record Wrapper
                          value: S64
                        @readonly
                        routine Wrapper.$unwrap_or(default: S64) -> S64
                          return me.value
                        routine test()
                          var w = Wrapper(value: 42)
                          var x = w ?? 0
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates coalesce on entity with unwrap or without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnEntityWithUnwrapOr_NoError()
    {
        string source = """
                        entity Box
                          value: S64
                        @readonly
                        routine Box.$unwrap_or(default: S64) -> S64
                          return me.value
                        routine test()
                          var b = Box(value: 99)
                          var x = b ?? 0
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region $unwrap / $unwrap_or are recognized as valid wired memberRoutines

    /// <summary>
    /// Verifies that the test validates declared on record no unknown wired error.
    /// </summary>
    [Fact]
    public void UnwrapDeclaredOnRecord_NoUnknownWiredError()
    {
        string source = """
                        record Wrapper
                          value: S64
                        @readonly
                        routine Wrapper.unwrap() -> S64
                          return me.value
                        @readonly
                        routine Wrapper.$unwrap_or(default: S64) -> S64
                          return me.value
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownWiredRoutine);
    }

    #endregion

    #region !! and ?? on non-failable call -> error (no error handling type)

    /// <summary>
    /// Verifies that the test validates unwrap on non failable call and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnNonFailableCall_ReportsError()
    {
        // A regular routine returns S64, !! on S64 is invalid
        string source = """
                        routine get_value() -> S64
                          return 42
                        routine test()
                          var x = get_value()!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates coalesce on non failable call and reports the expected error.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnNonFailableCall_ReportsError()
    {
        // A regular routine returns S64, ?? on S64 is invalid
        string source = """
                        routine get_value() -> S64
                          return 42
                        routine test()
                          var x = get_value() ?? 0
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region !! on choice/flags -> error (no operators on choice/flags)

    /// <summary>
    /// Verifies that the test validates unwrap on choice and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnChoice_ReportsError()
    {
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                        routine test()
                          var d = NORTH
                          var x = d!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    /// <summary>
    /// Verifies that the test validates coalesce on choice and reports the expected error.
    /// </summary>
    [Fact]
    public void NoneCoalesce_OnChoice_ReportsError()
    {
        // ?? is caught by the choice operator prohibition check (ArithmeticOnChoiceType)
        // before reaching the NoneCoalesce handler -> correct behavior
        string source = """
                        choice Direction
                          NORTH
                          SOUTH
                        routine test()
                          var d = NORTH
                          var e = SOUTH
                          var x = d ?? e
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    /// <summary>
    /// Verifies that the test validates unwrap on flags and reports the expected error.
    /// </summary>
    [Fact]
    public void ForceUnwrap_OnFlags_ReportsError()
    {
        string source = """
                        flags Permission
                          READ
                          WRITE
                        routine test()
                          var p = READ
                          var x = p!!
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TypeDoesNotSupportOperator);
    }

    #endregion

    #region GetMemberRoutineName mapping ??$unwrap and $unwrap_or registered

    /// <summary>
    /// Verifies that the test validates unwrap get memberRoutine name and returns the unwrap memberRoutine name.
    /// </summary>
    [Fact]
    public void ForceUnwrap_GetMemberRoutineName_ReturnsUnwrap()
    {
        UnaryOperator op = UnaryOperator.ForceUnwrap;
        Assert.Equal(expected: "unwrap", actual: op.GetMemberRoutineName());
    }

    /// <summary>
    /// Verifies that the test validates coalesce get memberRoutine name and returns the unwrap-or memberRoutine name.
    /// </summary>
    [Fact]
    public void NoneCoalesce_GetMemberRoutineName_ReturnsUnwrapOr()
    {
        BinaryOperator op = BinaryOperator.NoneCoalesce;
        Assert.Equal(expected: "unwrap_or", actual: op.GetMemberRoutineName());
    }

    #endregion
}
