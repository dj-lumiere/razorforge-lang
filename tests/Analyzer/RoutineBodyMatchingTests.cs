using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for routine body matching.
/// </summary>
public class RoutineBodyMatchingTests
{
    #region Overloaded $create Routines
    /// <summary>
    /// Verifies semantic analysis behavior for overloaded create both bodies match.
    /// </summary>

    [Fact]
    public void Analyze_OverloadedCreate_BothBodiesMatch()
    {
        // Two $create overloads with different parameter types should each
        // match their correct body via resolved overload keys
        string source = """
                        record Measurement
                          value: F64

                        routine Measurement.$create(from: S32) -> Measurement
                          return Measurement(value: from.F64())

                        routine Measurement.$create(from: F64) -> Measurement
                          return Measurement(value: from)

                        routine test()
                          var a = Measurement(from: 42)
                          var b = Measurement(from: 3.14)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion

    #region Generic Member Routines (External Syntax)
    /// <summary>
    /// Verifies semantic analysis behavior for generic owner external routine body matches.
    /// </summary>

    [Fact]
    public void Analyze_GenericOwnerExternalRoutine_BodyMatches()
    {
        // routine Box[T].unwrap() defined outside the type body
        // should match the registered declaration
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].unwrap() -> T
                          return me.value

                        routine test()
                          var b = Box[S32](value: 42)
                          var v = b.unwrap()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generic memberRoutine level param body matches.
    /// </summary>

    [Fact]
    public void Analyze_GenericMemberRoutineLevelParam_BodyMatches()
    {
        // memberRoutine-level generic params (Box[T].convert[U]) should match correctly
        string source = """
                        record Box[T]
                          value: T

                        routine Box[T].convert[U](new_val: U) -> Box[U]
                          return Box[U](value: new_val)

                        routine test()
                          var b = Box[S32](value: 42)
                          var c = b.convert[Bool](true)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion

    #region Module-Qualified Routines
    /// <summary>
    /// Verifies semantic analysis behavior for module qualified routine body matches.
    /// </summary>

    [Fact]
    public void Analyze_ModuleQualifiedRoutine_BodyMatches()
    {
        // Module-qualified routine body should match its registered declaration
        string source = """
                        module MyLib

                        routine compute(x: S32) -> S32
                          return x

                        routine test()
                          var r = compute(x: 10)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnresolvedRoutineBody);
    }

    #endregion
}
