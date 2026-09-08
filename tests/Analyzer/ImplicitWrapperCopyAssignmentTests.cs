using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the S420 ImplicitWrapperCopy rule on plain assignments (`b = a` outside
/// of `var` declarations). Mirrors the var-decl rule: borrowed-reference RHS of a
/// non-trivially-copyable type requires the explicit verb at the copy site.
/// </summary>
public class ImplicitWrapperCopyAssignmentTests
{
    /// <summary>`b = a` where `a: Retained[T]` is rejected.</summary>
    [Fact]
    public void Analyze_Assignment_BareRetainedCopy_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var rb = ra.share()
                          rb = ra
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy &&
                e.Message.Contains(value: "in assignment",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>`b = a.retain()` at the assignment site is accepted.</summary>
    [Fact]
    public void Analyze_Assignment_ExplicitRetain_IsAccepted()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var rb = ra.share()
                          rb = ra.share()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Assignment of a trivially-Assignable record copies bitwise — no error.</summary>
    [Fact]
    public void Analyze_Assignment_TriviallyAssignableRecord_IsAccepted()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        routine start()
                          var p1 = Point(x: 1, y: 2)
                          var p2 = Point(x: 3, y: 4)
                          p2 = p1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Assigning a primitive value is a trivial copy — no error.</summary>
    [Fact]
    public void Analyze_Assignment_Primitive_IsAccepted()
    {
        string source = """
                        routine start()
                          var a = 1_s64
                          var b = 2_s64
                          b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>`b = a.track()` at the assignment site produces a fresh handle — accepted.</summary>
    [Fact]
    public void Analyze_Assignment_ExplicitTrack_IsAccepted()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var rb = ra.share()
                          rb = ra.observe()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }
}
