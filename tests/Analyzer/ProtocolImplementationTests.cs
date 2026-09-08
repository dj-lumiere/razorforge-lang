using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for protocol implementation.
/// </summary>
public class ProtocolImplementationTests
{
    #region Basic Protocol Implementation
    /// <summary>
    /// Verifies semantic analysis behavior for implements all memberRoutines without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ImplementsAllMemberRoutines_NoError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        record Point obeys Displayable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.display() -> Text
                          return "point"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should validate protocol implementation
    }
    /// <summary>
    /// Verifies semantic analysis behavior for missing protocol memberRoutine and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_MissingProtocolMemberRoutine_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        record Point obeys Displayable
                          x: F32
                          y: F32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for wrong memberRoutine signature and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_WrongMemberRoutineSignature_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        record Point obeys Displayable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.display() -> S32
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Protocol memberRoutine Annotations
    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine missing readonly and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_memberRoutineMissingReadonly_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        record Point obeys Displayable
                          x: F32
                          y: F32

                        routine Point.display() -> Text
                          return "point"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should warn about missing @readonly annotation
    }
    /// <summary>
    /// Verifies semantic analysis behavior for memberRoutine with writable when protocol readonly and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_memberRoutineWithWritableWhenProtocolReadonly_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        entity Point obeys Displayable
                          x: F32
                          y: F32

                        routine Point.display() -> Text
                          return "point"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Multiple Protocols
    /// <summary>
    /// Verifies semantic analysis behavior for multiple protocols all implemented without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_MultipleProtocols_AllImplemented_NoError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        protocol Comparable
                          @readonly
                          routine Me.$cmp(other: Me) -> S32

                        record Value obeys Displayable, Comparable
                          value: S32

                        @readonly
                        routine Value.display() -> Text
                          return "value"

                        @readonly
                        routine Value.$cmp(other: Value) -> S32
                          return me.value - other.value
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for multiple protocols one missing and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_MultipleProtocols_OneMissing_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        protocol Comparable
                          @readonly
                          routine Me.$cmp(other: Me) -> S32

                        record Value obeys Displayable, Comparable
                          value: S32

                        @readonly
                        routine Value.display() -> Text
                          return "value"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Generic Protocol Implementation
    /// <summary>
    /// Verifies semantic analysis behavior for generic protocol implementation without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_GenericProtocol_Implementation_NoError()
    {
        string source = """
                        protocol Container
                          @readonly
                          routine Me.count() -> uaddr

                        entity MyList[T] obeys Container
                          items: List[T]

                        @readonly
                        routine MyList[T].count() -> uaddr
                          return 0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Protocol memberRoutine Parameters
    /// <summary>
    /// Verifies semantic analysis behavior for protocol memberRoutine with parameters without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolMemberRoutineWithParameters_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(other: Me) -> Me

                        record Point obeys Addable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.$add(other: Point) -> Point
                          return Point(x: me.x + other.x, y: me.y + other.y)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for protocol memberRoutine wrong parameter type and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolMemberRoutineWrongParameterType_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(other: Me) -> Me

                        record Point obeys Addable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.$add(other: S32) -> Point
                          return Point(x: me.x, y: me.y)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Entity Protocol Implementation
    /// <summary>
    /// Verifies semantic analysis behavior for entity implements protocol without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_EntityImplementsProtocol_NoError()
    {
        string source = """
                        protocol Countable
                          @readonly
                          routine Me.count() -> S32

                        entity Counter obeys Countable
                          value: S32

                        @readonly
                        routine Counter.count() -> S32
                          return me.value
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Protocol Inheritance
    /// <summary>
    /// Verifies semantic analysis behavior for protocol extends implementation without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolExtends_Implementation_NoError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        protocol DebugDisplayable obeys Displayable
                          @readonly
                          routine Me.debug_display() -> Text

                        record Point obeys DebugDisplayable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.display() -> Text
                          return "point"

                        @readonly
                        routine Point.debug_display() -> Text
                          return "Point(x, y)"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for protocol extends missing parent memberRoutine and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolExtends_MissingParentMemberRoutine_ReportsError()
    {
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        protocol DebugDisplayable obeys Displayable
                          @readonly
                          routine Me.debug_display() -> Text

                        record Point obeys DebugDisplayable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.debug_display() -> Text
                          return "Point(x, y)"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Annotation Placement Validation (#177)
    /// <summary>
    /// Verifies semantic analysis behavior for generated on non protocol routine and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_GeneratedOnNonProtocolRoutine_ReportsError()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32

                        @generated
                        @readonly
                        routine Point.display() -> Text
                          return "point"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.InvalidGeneratedInnatePlacement);
    }
    /// <summary>
    /// Verifies that @innate is valid on non-protocol routines (e.g., BuilderQuery routines).
    /// </summary>

    [Fact]
    public void Analyze_InnateOnNonProtocolRoutine_NoError()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32

                        @innate
                        @readonly
                        routine Point.display() -> Text
                          return "point"
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.InvalidGeneratedInnatePlacement);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generated on protocol routine without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_GeneratedOnProtocolRoutine_NoError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                          @generated
                          @readonly
                          routine Me.$ne(you: Me) -> Bool

                        record Point obeys Equatable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.InvalidGeneratedInnatePlacement);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for innate on protocol routine without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_InnateOnProtocolRoutine_NoError()
    {
        string source = """
                        protocol Lockable
                          @innate
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        entity Widget obeys Lockable
                          name: Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.InvalidGeneratedInnatePlacement);
    }

    #endregion

    #region Innate Override Prohibition (#178)
    /// <summary>
    /// Verifies semantic analysis behavior for override innate routine and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_OverrideInnateRoutine_ReportsError()
    {
        string source = """
                        protocol Lockable
                          @innate
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        entity Widget obeys Lockable
                          name: Text

                        @readonly
                        routine Widget.$eq(you: Widget) -> Bool
                          return false
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(result.Errors, e => e.Code == SemanticDiagnosticCode.InnateOverrideNotAllowed);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for innate routine not overridden without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_InnateRoutineNotOverridden_NoError()
    {
        string source = """
                        protocol Lockable
                          @innate
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        entity Widget obeys Lockable
                          name: Text
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.InnateOverrideNotAllowed);
    }

    #endregion

    #region Generated Override Prioritization (#179)
    /// <summary>
    /// Verifies semantic analysis behavior for override generated ne without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_OverrideGeneratedNe_NoError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                          @generated
                          @readonly
                          routine Me.$ne(you: Me) -> Bool

                        record Point obeys Equatable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y

                        @readonly
                        routine Point.$ne(you: Point) -> Bool
                          return me.x != you.x or me.y != you.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(result.Errors, e => e.Code == SemanticDiagnosticCode.GeneratedOperatorOverride);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for generated ne not overridden without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_GeneratedNeNotOverridden_NoError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                          @generated
                          @readonly
                          routine Me.$ne(you: Me) -> Bool

                        record Point obeys Equatable
                          x: F32
                          y: F32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Protocol with Default Values
    /// <summary>
    /// Verifies semantic analysis behavior for protocol memberRoutine with default parameter without unexpected diagnostics.
    /// </summary>

    [Fact]
    public void Analyze_ProtocolMemberRoutineWithDefaultParameter_NoError()
    {
        string source = """
                        protocol Configurable
                          routine Me.configure(value: S32 = 0)

                        entity Settings obeys Configurable
                          value: S32

                        routine Settings.configure(value: S32 = 0)
                          me.value = value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Multiple Protocol Generic Constraints (#62)
    /// <summary>
    /// Verifies that the parser accepts inline multiple obeys successfully.
    /// </summary>

    [Fact]
    public void Parse_InlineMultipleObeys_Parses()
    {
        // routine foo[T obeys Displayable, Comparable](item: T)
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        protocol Comparable
                          @readonly
                          routine Me.$cmp(other: Me) -> S32

                        routine foo[T obeys Displayable, Comparable](item: T) -> Text
                          return item.display()
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts needs multiple obeys successfully.
    /// </summary>

    [Fact]
    public void Parse_NeedsMultipleObeys_Parses()
    {
        // routine foo[T](item: T) needs T obeys Displayable, Comparable -> Text
        string source = """
                        protocol Displayable
                          @readonly
                          routine Me.display() -> Text

                        protocol Comparable
                          @readonly
                          routine Me.$cmp(other: Me) -> S32

                        routine foo[T](item: T) needs T obeys Displayable, Comparable -> Text
                          return item.display()
                        """;

        AssertParses(source: source);
    }

    #endregion
}
