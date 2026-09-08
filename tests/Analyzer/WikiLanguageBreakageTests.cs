using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains wiki-derived RazorForge snippets that should fail at a specific compiler boundary.
/// </summary>
public class WikiLanguageBreakageTests
{
    /// <summary>
    /// Verifies that nested routine declarations are rejected.
    /// </summary>
    [Fact]
    public void Parse_NestedRoutineDeclaration_ReportsError()
    {
        string source = """
                        routine outer()
                          routine inner()
                            return
                          return
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies that member routines cannot be declared inside a type body.
    /// </summary>
    [Fact]
    public void Parse_InScopeMemberRoutineDeclaration_ReportsError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                          routine distance_to(other: Point) -> S32
                            return 0
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies that record member variables cannot use var syntax.
    /// </summary>
    [Fact]
    public void Parse_RecordMemberWithVarKeyword_ReportsError()
    {
        string source = """
                        record Point
                          var x: S32
                          y: S32
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies that external is not a valid record member modifier.
    /// </summary>
    [Fact]
    public void Parse_RecordMemberWithExternalModifier_ReportsError()
    {
        string source = """
                        record NativeShape
                          external handle: Address
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies that colon-style generic constraints are rejected by grammar recovery.
    /// </summary>
    [Fact]
    public void Parse_ColonGenericConstraint_ReportsError()
    {
        string source = """
                        record Box[T: Hashable]
                          value: T
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies that an indented needs clause is treated as an invalid body declaration.
    /// </summary>
    [Fact]
    public void Parse_IndentedNeedsClause_ReportsError()
    {
        string source = """
                        record Box[T]
                          needs T obeys Hashable
                          value: T
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies that multiline lambdas are rejected.
    /// </summary>
    [Fact]
    public void Parse_MultilineLambda_ReportsError()
    {
        string source = """
                        routine test()
                          var process = (data) =>
                            var result = data
                            result
                          return
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Failability is now INFERRED: a <c>throw</c> inside a routine NOT declared <c>!</c> no longer
    /// errors (the declaration is optional). The routine is inferred-failable instead.
    /// </summary>
    [Fact]
    public void Analyze_ThrowInNonFailableRoutine_NoLongerErrors()
    {
        string source = """
                        crashable SampleError
                          message: Text

                        routine test()
                          throw SampleError(message: "boom")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowOutsideFailableFunction);
    }

    /// <summary>
    /// Failability is now INFERRED: an <c>absent</c> inside a routine NOT declared <c>!</c> no longer
    /// errors (the declaration is optional). The routine is inferred-failable instead.
    /// </summary>
    [Fact]
    public void Analyze_AbsentInNonFailableRoutine_NoLongerErrors()
    {
        string source = """
                        routine test() -> S32
                          absent
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.AbsentOutsideFailableFunction);
    }

    /// <summary>
    /// Verifies that a failable routine must contain throw or absent.
    /// </summary>
    [Fact]
    public void Analyze_FailableRoutineWithoutThrowOrAbsent_ReportsError()
    {
        string source = """
                        routine test!() -> S32
                          return 1
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.FailableWithoutThrowOrAbsent);
    }

    /// <summary>
    /// Verifies that @crash_only only applies to failable routines.
    /// </summary>
    [Fact]
    public void Analyze_CrashOnlyOnNonFailableRoutine_ReportsError()
    {
        string source = """
                        @crash_only
                        routine test()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CrashOnlyOnNonFailable);
    }

    /// <summary>
    /// Verifies that dangerous routines cannot be called outside danger blocks.
    /// </summary>
    [Fact]
    public void Analyze_DangerousRoutineCallOutsideDangerBlock_ReportsError()
    {
        string source = """
                        dangerous routine read_raw() -> S32
                          return 1

                        routine test()
                          var value = read_raw()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DangerousCallOutsideDangerBlock);
    }

    /// <summary>
    /// Verifies that dangerous routines can be called inside danger blocks.
    /// </summary>
    [Fact]
    public void Analyze_DangerousRoutineCallInsideDangerBlock_DoesNotReportDangerError()
    {
        string source = """
                        dangerous routine read_raw() -> S32
                          return 1

                        routine test()
                          danger
                            var value = read_raw()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DangerousCallOutsideDangerBlock);
    }

    /// <summary>
    /// Verifies that user routines cannot directly return Maybe.
    /// </summary>
    [Fact]
    public void Analyze_UserRoutineReturningMaybe_ReportsError()
    {
        string source = """
                        routine test() -> Maybe[S32]
                          return none
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType);
    }

    /// <summary>
    /// Verifies that Result and Lookup are not valid parameter types for user APIs.
    /// </summary>
    /// <param name="typeName">The error-handling type name.</param>
    [Theory]
    [InlineData("Result[S32]")]
    [InlineData("Lookup[S32]")]
    public void Analyze_ErrorHandlingTypeAsParameter_ReportsError(string typeName)
    {
        string source = $"""
                         routine test(value: {typeName})
                           return
                         """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsParameter);
    }

    /// <summary>
    /// Verifies that Result and Lookup are not valid member variable types.
    /// </summary>
    /// <param name="typeName">The error-handling type name.</param>
    [Theory]
    [InlineData("Result[S32]")]
    [InlineData("Lookup[S32]")]
    public void Analyze_ErrorHandlingTypeAsMemberVariable_ReportsError(string typeName)
    {
        string source = $"""
                         entity Holder
                           value: {typeName}
                         """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ErrorHandlingTypeAsMemberVariable);
    }

    /// <summary>
    /// Verifies a record MAY store an entity field — entities are reference (pointer-shaped) types,
    /// so the field holds a reference; only scoped access tokens are rejected as record members.
    /// </summary>
    [Fact]
    public void Analyze_RecordContainingEntityReference_NoError()
    {
        string source = """
                        entity Node
                          value: S32

                        record BadRecord
                          node: Node
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies that access tokens cannot be stored in record member variables.
    /// </summary>
    [Fact]
    public void Analyze_RecordContainingViewedToken_ReportsError()
    {
        string source = """
                        record BadRecord
                          view: Viewing[S32]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code is SemanticDiagnosticCode.TokenMemberVariableNotAllowed
                or SemanticDiagnosticCode.RecordContainsNonValueType);
    }

    /// <summary>
    /// Verifies that access tokens cannot be returned from routines.
    /// </summary>
    [Fact]
    public void Analyze_RoutineReturningViewedToken_ReportsError()
    {
        string source = """
                        entity Node
                          value: S32

                        routine get_view(node: Node) -> Viewing[Node]
                          return node.view()
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.TokenReturnNotAllowed);
    }

    /// <summary>
    /// Verifies that a lambda must declare routine-scope captures in given.
    /// </summary>
    [Fact]
    public void Analyze_LambdaCaptureWithoutGiven_ReportsError()
    {
        string source = """
                        routine test()
                          var threshold = 100
                          var f = x => x > threshold
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.LambdaCaptureWithoutGiven);
    }

    /// <summary>
    /// Verifies that a hand-written try_/check_/lookup_ routine colliding with the variant the
    /// compiler generates for a failable base of the same signature is rejected (RF-S409).
    /// </summary>
    /// <param name="routineName">The colliding variant name.</param>
    /// <param name="failStatement">
    /// The failure statement in `parse!` that drives which variant is synthesized: `absent` -> try_,
    /// `throw x` -> try_+check_, both -> try_+lookup_.
    /// </param>
    [Theory]
    [InlineData("try_parse", "absent")]
    [InlineData("check_parse", "throw x")]
    [InlineData("lookup_parse", "absent\n    throw x")]
    public void Analyze_ReservedGeneratedRoutinePrefix_CollidingWithFailableBase_ReportsError(
        string routineName, string failStatement)
    {
        // `parse!` is failable, so the compiler synthesizes the matching try_/check_/lookup_ variant
        // with parse!'s exact signature — the hand-written routine below collides with it.
        string source = $"""
                         routine parse!(x: S32) -> S32
                           if x < 0
                             {failStatement}
                           return x
                         routine {routineName}(x: S32) -> S32
                           return x
                         """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReservedRoutinePrefix);
    }

    /// <summary>
    /// Verifies the reserved prefixes are collision-only: a try_/check_/lookup_ routine with no
    /// failable base of the same signature (e.g. the industry lock idiom <c>try_lock</c>) is
    /// allowed and never reported as a reserved-prefix error.
    /// </summary>
    /// <param name="routineName">The routine name.</param>
    [Theory]
    [InlineData("try_lock")]
    [InlineData("check_status")]
    [InlineData("lookup_row")]
    public void Analyze_ReservedGeneratedRoutinePrefix_NoFailableBase_Allowed(string routineName)
    {
        string source = $"""
                         routine {routineName}() -> S32
                           return 1
                         """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ReservedRoutinePrefix);
    }


    /// <summary>
    /// Verifies that break cannot appear outside a loop.
    /// </summary>
    [Fact]
    public void Analyze_BreakOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          break
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BreakOutsideLoop);
    }

    /// <summary>
    /// Verifies that continue cannot appear outside a loop.
    /// </summary>
    [Fact]
    public void Analyze_ContinueOutsideLoop_ReportsError()
    {
        string source = """
                        routine test()
                          continue
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ContinueOutsideLoop);
    }

    /// <summary>
    /// Verifies that me cannot be used outside a member routine.
    /// </summary>
    [Fact]
    public void Analyze_MeOutsideMemberRoutine_ReportsError()
    {
        string source = """
                        routine test() -> S32
                          return me.value
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MeOutsideTypeMemberRoutine);
    }
}
