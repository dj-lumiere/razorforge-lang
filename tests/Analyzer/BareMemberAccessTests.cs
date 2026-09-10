using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// A bare member access (<c>x.name</c>, no parentheses) reads a member VARIABLE; it must not
/// silently resolve to and auto-call a zero-arg memberRoutine <c>name()</c>. Auto-calling masked real
/// bugs — e.g. a record that drops a <c>sign</c> field but keeps a <c>sign()</c> accessor let
/// <c>var s = w.sign</c> typecheck as the call result, so SA passed code that only failed at
/// codegen ("Member variable 'sign' not found"). See the .field-vs-.MemberRoutine() distinction.
/// </summary>
public class BareMemberAccessTests
{
    /// <summary>
    /// Bare <c>w.tag</c> where <c>tag</c> is a zero-arg memberRoutine (not a member variable) must be a
    /// hard error, not an implicit call.
    /// </summary>
    [Fact]
    public void Analyze_BareAccessToZeroArgMemberRoutine_ReportsMemberNotFound()
    {
        string source = """
                        record Widget
                          size: S32
                        @readonly
                        routine Widget.tag() -> S32
                          return 7_s32
                        routine test() -> S32
                          var w = Widget(size: 1_s32)
                          return w.tag
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }

    /// <summary>
    /// The explicit call form <c>w.tag()</c> is correct and must NOT be flagged.
    /// </summary>
    [Fact]
    public void Analyze_ExplicitCallToZeroArgMemberRoutine_NoError()
    {
        string source = """
                        record Widget
                          size: S32
                        @readonly
                        routine Widget.tag() -> S32
                          return 7_s32
                        routine test() -> S32
                          var w = Widget(size: 1_s32)
                          return w.tag()
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }

    /// <summary>
    /// Reading a genuine member variable bare (<c>w.size</c>) stays legal.
    /// </summary>
    [Fact]
    public void Analyze_BareAccessToMemberVariable_NoError()
    {
        string source = """
                        record Widget
                          size: S32
                        routine test() -> S32
                          var w = Widget(size: 1_s32)
                          return w.size
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MemberNotFound);
    }
}
