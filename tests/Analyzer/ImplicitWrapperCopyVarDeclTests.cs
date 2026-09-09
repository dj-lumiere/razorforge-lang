using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the S420 ImplicitWrapperCopy rule on variable declarations.
/// `var b = a` where `a` carries a non-trivially-copyable wrapper must spell out
/// the explicit verb (`steal` / `.share()`). Trivially-copyable
/// values (primitives, Hijacked[T], primitive-only records) still copy bitwise.
/// </summary>
public class ImplicitWrapperCopyVarDeclTests
{
    /// <summary>Bare `var b = a` where `a: Retained[T]` is rejected.</summary>
    [Fact]
    public void Analyze_VarDecl_BareRetainedCopy_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var rb = ra
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "Implicit copy",
                    comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "a.share()",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>`var b = obj.field` where the field is `Retained[T]` is rejected.</summary>
    [Fact]
    public void Analyze_VarDecl_BorrowedMemberRetainedCopy_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a))
                          var taken = b.handle
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "Implicit copy",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Explicit `.retain()` at the copy site is accepted.</summary>
    [Fact]
    public void Analyze_VarDecl_ExplicitRetain_IsAccepted()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var rb = ra.share()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Trivially-Assignable record (all-primitive) bitwise copies — no error.</summary>
    [Fact]
    public void Analyze_VarDecl_TriviallyAssignableRecord_IsAccepted()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        routine start()
                          var p1 = Point(x: 1, y: 2)
                          var p2 = p1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Initializer that is a fresh call result (not a borrowed reference) is accepted.</summary>
    [Fact]
    public void Analyze_VarDecl_FreshCallResult_IsAccepted()
    {
        // `.retain()` returns a freshly-owned Retained handle — the var-decl rule only
        // fires on borrowed identifier / member references, not on call results.
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>`var v = a.view()` is rejected: Viewing[T] is a scoped token that can't escape.</summary>
    [Fact]
    public void Analyze_VarDecl_ViewedFromCall_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var v = a.view()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy &&
                e.Message.Contains(value: "Viewing",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>`var g = a.modify()` is rejected: Modifying[T] is a scoped token that can't escape.</summary>
    [Fact]
    public void Analyze_VarDecl_GraspedFromCall_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var g = a.modify()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy &&
                e.Message.Contains(value: "Modifying",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Inline chained access `a.view().value` is accepted — desugars as a scoped `using`.</summary>
    [Fact]
    public void Analyze_InlineMemberAccess_OnViewingCall_IsAccepted()
    {
        string source = """
                        import IO/Console

                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          show(f"{a.view().value}")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>`var t = ra.track()` produces a fresh Tracked handle — accepted as a call result.</summary>
    [Fact]
    public void Analyze_VarDecl_TrackResultIsAccepted()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var t = ra.observe()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>Bare copy of a `Retained[T]` variable into a second var is rejected even inside a record field read.</summary>
    [Fact]
    public void Analyze_VarDecl_RetainedFromTwoLevels_IsError()
    {
        string source = """
                        entity Node
                          value: S64

                        record Wrapper
                          ref: Retained[Node]

                        record Box
                          inner: Wrapper

                        routine start()
                          var a = Node(value: 1)
                          var w = Wrapper(ref: Retained(from: steal a))
                          var b = Box(inner: w)
                          var taken = b.inner.ref
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    /// <summary>`using a.view() as v` is the supported form — accepted.</summary>
    [Fact]
    public void Analyze_UsingBlock_ViewingFromCall_IsAccepted()
    {
        string source = """
                        import IO/Console

                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          using a.view() as v
                            show(f"{v.value}")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.ImplicitWrapperCopy);
    }
}
