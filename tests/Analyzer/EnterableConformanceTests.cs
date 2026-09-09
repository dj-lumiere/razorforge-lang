using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the <c>Enterable</c> protocol and the language features underpinning it:
/// <list type="bullet">
///   <item>Failability covariance — a NON-failable implementation satisfies a FAILABLE (<c>!</c>)
///         protocol requirement (never-failing is a stronger contract); the reverse is an error.</item>
///   <item>The <c>?</c> in-flight mark is meaningless on value-type records, so a record memberRoutine that
///         returns the bare type satisfies a protocol requirement written as <c>?Me</c>.</item>
///   <item>A user type may obey <c>Enterable</c> with a pass-through <c>$enter</c> and drive a
///         <c>using</c> scope.</item>
/// </list>
/// </summary>
public class EnterableConformanceTests
{
    #region Failability covariance (general)

    [Fact]
    public void Analyze_NonFailableImpl_SatisfiesFailableRequirement_Ok()
    {
        string source = """
                        protocol Openable
                          routine Me.go!()

                        entity Door obeys Openable
                          secret n: S32

                        routine Door.go()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_FailableImpl_ForNonFailableRequirement_Errors()
    {
        string source = """
                        protocol Plain
                          routine Me.go()

                        entity Door obeys Plain
                          secret n: S32

                        routine Door.go!()
                          absent
                        """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "should be non-failable to match protocol 'Plain'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch);
    }

    #endregion

    #region Enterable conformance

    [Fact]
    public void Analyze_RecordObeysEnterable_BareReturnPassThrough_Ok()
    {
        // A record `$enter` returns the bare type (no `?`); it satisfies Enterable's `$enter!() -> ?Me`
        // because the `?` in-flight mark is normalized away for value-type records, and a non-failable
        // `$enter` covariantly satisfies the failable `$enter!` requirement.
        string source = """
                        record MyGuard obeys Enterable
                          secret n: S32

                        routine MyGuard.enter() -> MyGuard
                          return me

                        routine MyGuard.exit()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_EntityObeysEnterable_UsedInUsingScope_Ok()
    {
        string source = """
                        import IO/Console

                        entity Session obeys Enterable
                          secret n: S32

                        routine Session.enter() -> Session
                          return me

                        routine Session.exit()
                          return

                        routine start()
                          using Session(n: 1) as s
                            show("in scope")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region using-required enforcement (MT access tokens)

    [Fact]
    public void Analyze_BareInspect_NotUsingBound_Errors()
    {
        string source = """
                        import IO/Console
                        import BuilderQuery

                        entity Counter
                          value: S64

                        routine peek(c: Accessing[Counter])
                          show(f"{c.value}")
                          return

                        routine start()
                          var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                          peek(s.consult())
                          return
                        """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "must be opened with 'using'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MtTokenRequiresUsing);
    }

    [Fact]
    public void Analyze_InspectOpenedWithUsing_Ok()
    {
        string source = """
                        import IO/Console
                        import BuilderQuery

                        entity Counter
                          value: S64

                        routine start()
                          var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                          using s.consult() as v
                            show(f"{v.value}")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_InspectBoundToVar_Errors()
    {
        string source = """
                        import IO/Console
                        import BuilderQuery

                        entity Counter
                          value: S64

                        routine start()
                          var s = Guarded[Counter, MultiRead](from: Counter(value: 1))
                          var v = s.consult()
                          return
                        """;

        AnalysisResult result = AssertHasErrorSa(source: source, expectedErrorSubstring: "using");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MtTokenRequiresUsing);
    }

    #endregion

    #region Resources are using-CAPABLE, not using-required (only amend/consult are required)

    /// <summary>A `using` target must OBEY Enterable, not merely define $enter/$exit by name.</summary>
    [Fact]
    public void Analyze_UsingNonEnterableType_Errors()
    {
        string source = """
                        record Bare
                          n: S32

                        routine Bare.enter() -> Bare
                          return me

                        routine Bare.exit()
                          return

                        routine test()
                          var b = Bare(n: 1)
                          using b as x
                            var a: S32 = x.n
                          return
                        """;

        AnalysisResult result = AssertHasErrorSa(source: source,
            expectedErrorSubstring: "must obey 'Enterable'");
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    /// <summary>FileHandle obeys Enterable but is using-CAPABLE, not using-required.</summary>
    [Fact]
    public void Analyze_FileHandleVarBound_NotUsingRequired_Ok()
    {
        // FileHandle obeys Enterable but is NOT using-required (only the MT lock tokens are). A bare
        // `var f = open!(...)` is legal — RAII `$destroy` closes it at scope exit.
        string source = """
                        import IO/File

                        routine start!()
                          var f = open!(path: "x.txt", mode: FileMode.WRITE)
                          f.flush()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
