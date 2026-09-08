#pragma warning disable CS1591
using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for the Assignable protocol — the gate for implicit-copy positions
/// (var binding, non-steal arg-pass, non-in-flight-T return, <c>with</c> base).
///
/// Coverage:
///   * Auto-derivation rule — records whose @llvm layout contains no `ptr` get
///     Assignable for free; ownership-bearing wrappers do not.
///   * Choice / Flags auto-derive Assignable unconditionally (scalar layout).
///   * Tuples cascade — Assignable iff every element is Assignable.
///   * Raw-pointer wrappers (Hijacked[T], CPtr) opt in with a one-line assign body.
///   * <c>with</c> expression now requires Assignable on the base; gate diagnostic
///     is <c>WithBaseNotAssignable</c> (S785).
/// </summary>
public class AssignableProtocolTests
{
    #region Auto-derivation: positive cases

    [Fact]
    public void Analyze_RecordOfPrimitives_AutoDerivesAssignable_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine start()
                          var a = Point(x: 1, y: 2)
                          var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_Choice_AutoDerivesAssignable_NoError()
    {
        string source = """
                        choice Color
                          Red
                          Green
                          Blue

                        routine start()
                          var a = Color.Red
                          var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_Flags_AutoDerivesAssignable_NoError()
    {
        string source = """
                        flags Perms
                          READ
                          WRITE
                          EXECUTE

                        routine start()
                          var a = Perms.READ
                          var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_TupleOfPrimitives_AutoDerivesAssignable_NoError()
    {
        string source = """
                        routine start()
                          var a = (1_s32, 2_s32, 3_s32)
                          var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_NestedRecordOfPrimitives_AutoDerivesAssignable_NoError()
    {
        string source = """
                        record Inner
                          v: S32

                        record Outer
                          inner: Inner
                          tag: S64

                        routine start()
                          var a = Outer(inner: Inner(v: 1), tag: 42)
                          var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    #endregion

    #region Auto-derivation: negative cases (ownership-bearing wrappers block derivation)

    [Fact]
    public void Analyze_RecordWithRetainedField_DoesNotAutoDerive_VarCopyRejected()
    {
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a))
                          var c = b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_RecordWithTrackedField_DoesNotAutoDerive_VarCopyRejected()
    {
        string source = """
                        entity Node
                          value: S64

                        record TrackedBox
                          handle: Tracked[Node]

                        routine start()
                          var a = Node(value: 1)
                          var b = TrackedBox(handle: Retained(from: steal a).observe())
                          var c = b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_TupleContainingRetained_DoesNotAutoDerive_VarCopyRejected()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var t = (1_s32, Retained(from: steal a))
                          var u = t
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    #endregion

    #region Raw-pointer wrappers (manual opt-in via stdlib)

    [Fact]
    public void Analyze_HijackedIsAssignable_VarCopyAccepted()
    {
        string source = """
                        routine start()
                          danger
                            var a = Hijacked[U8](0_addr)
                            var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_CPtrIsAssignable_VarCopyAccepted()
    {
        string source = """
                        routine start()
                          var a = cptr_none()
                          var b = a
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    #endregion

    #region `with` expression — Assignable gate (S785)

    [Fact]
    public void Analyze_With_OnPrimitiveRecord_NoAssignableError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine start()
                          var p = Point(x: 1, y: 2)
                          var q = p with .x = 5
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithBaseNotAssignable);
    }

    [Fact]
    public void Analyze_With_OnRecordWithRetainedField_ReportsAssignableError()
    {
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]
                          tag: S64

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a), tag: 0)
                          var c = b with .tag = 42
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithBaseNotAssignable);
    }

    [Fact]
    public void Analyze_With_OnNestedPrimitiveRecord_NoAssignableError()
    {
        string source = """
                        record Inner
                          v: S32

                        record Outer
                          inner: Inner
                          tag: S64

                        routine start()
                          var a = Outer(inner: Inner(v: 1), tag: 42)
                          var b = a with .tag = 99
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithBaseNotAssignable);
    }

    [Fact]
    public void Analyze_With_DiagnosticMentionsObeysAssignable()
    {
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]
                          tag: S64

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a), tag: 0)
                          var c = b with .tag = 42
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.WithBaseNotAssignable &&
                         e.Message.Contains(value: "Assignable",
                             comparisonType: StringComparison.Ordinal));
    }

    #endregion

    #region Call-arg / return enforcement still works via the Assignable check

    [Fact]
    public void Analyze_CallArg_RecordWithRetained_BareIdentifier_IsRejected()
    {
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]

                        routine take(box: Box)
                          return

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a))
                          take(box: b)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    [Fact]
    public void Analyze_CallArg_PrimitiveRecord_BareIdentifier_IsAccepted()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine take(p: Point)
                          return

                        routine start()
                          var a = Point(x: 1, y: 2)
                          take(p: a)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ImplicitWrapperCopy);
    }

    #endregion

    #region $assign synthesis — direct calls succeed on auto-derived Assignable types

    [Fact]
    public void Analyze_CopyMemberRoutine_DirectCall_OnPrimitiveRecord_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine start()
                          var a = Point(x: 1, y: 2)
                          var b = a.assign()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_CopyMemberRoutine_DirectCall_OnChoice_NoError()
    {
        string source = """
                        choice Color
                          Red
                          Green
                          Blue

                        routine start()
                          var a = Color.Red
                          var b = a.assign()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_CopyMemberRoutine_DirectCall_OnFlags_NoError()
    {
        string source = """
                        flags Perms
                          Read
                          Write
                          Execute

                        routine start()
                          var a = Perms.Read
                          var b = a.assign()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_CopyMemberRoutine_DirectCall_OnRecordWithRetained_NoMemberRoutineFound()
    {
        // Record with Retained field doesn't auto-derive Assignable → no synthesized $assign.
        // User-written $assign would be required; without it the direct call fails to resolve.
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a))
                          var c = b.assign()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotEmpty(collection: result.Errors);
    }

    #endregion

    #region clone() — Assignable obeys Cloneable, so auto-derived types get clone() too

    [Fact]
    public void Analyze_Clone_OnPrimitiveRecord_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine start()
                          var a = Point(x: 1, y: 2)
                          var b = a.duplicate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_Clone_OnChoice_NoError()
    {
        string source = """
                        choice Color
                          Red
                          Green
                          Blue

                        routine start()
                          var a = Color.Red
                          var b = a.duplicate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_Clone_OnFlags_NoError()
    {
        string source = """
                        flags Perms
                          Read
                          Write
                          Execute

                        routine start()
                          var a = Perms.Read
                          var b = a.duplicate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_Clone_OnNestedPrimitiveRecord_NoError()
    {
        string source = """
                        record Inner
                          v: S32

                        record Outer
                          inner: Inner
                          tag: S64

                        routine start()
                          var a = Outer(inner: Inner(v: 1), tag: 42)
                          var b = a.duplicate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void Analyze_Clone_OnRecordWithRetained_Fails()
    {
        // Record with Retained field does not auto-derive Assignable, so it also
        // does not auto-derive Cloneable — calling .duplicate() should fail to resolve.
        string source = """
                        entity Node
                          value: S64

                        record Box
                          handle: Retained[Node]

                        routine start()
                          var a = Node(value: 1)
                          var b = Box(handle: Retained(from: steal a))
                          var c = b.duplicate()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotEmpty(collection: result.Errors);
    }

    #endregion
}
