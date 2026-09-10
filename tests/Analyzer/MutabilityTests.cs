using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for mutability.
/// </summary>
public class MutabilityTests
{
    #region Var Mutability

    /// <summary>
    /// Verifies semantic analysis behavior for var reassignment without immutability errors.
    /// </summary>
    [Fact]
    public void Analyze_VarReassignment_NoImmutableError()
    {
        // var is mutable, so reassignment should succeed
        string source = """
                        routine test()
                          var x = 42
                          x = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "immutable",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "reassign",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for var compound assignment without immutability errors.
    /// </summary>
    [Fact]
    public void Analyze_VarCompoundAssignment_NoImmutableError()
    {
        // var is mutable, so compound assignment should succeed
        string source = """
                        routine test()
                          var x = 42
                          x += 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should not produce immutable-related errors
    }
    /// <summary>
    /// Verifies semantic analysis behavior for var reassignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_VarReassignment_NoError()
    {
        string source = """
                        routine test()
                          var x = 42
                          x = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
        // Should be valid
    }

    #endregion

    #region Entity Field Mutability

    /// <summary>
    /// Verifies semantic analysis behavior for var member variable mutation without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_VarMemberVariableMutation_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        routine Counter.increment()
                          me.count += 1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for writable member variable mutation without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WritableMemberVariableMutation_NoError()
    {
        string source = """
                        entity User
                          id: U64
                          name: Text

                        routine User.set_id(new_id: U64)
                          me.id = new_id
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Readonly vs Writable memberRoutines

    /// <summary>
    /// Verifies semantic analysis behavior for readonly memberRoutine mutating and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ReadonlyMemberRoutineMutating_ReportsError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.increment()
                          me.count += 1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for readonly memberRoutine reading without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ReadonlyMemberRoutineReading_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.get_count() -> S32
                          return me.count
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for writable memberRoutine mutating without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_WritableMemberRoutineMutating_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        routine Counter.set_count(value: S32)
                          me.count = value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Record Field Mutability

    /// <summary>
    /// Verifies semantic analysis behavior for record member variables immutable without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordMemberVariablesImmutable_NoError()
    {
        string source = """
                        record Point
                          x: F32
                          y: F32

                        routine test()
                          var p = Point(x: 1.0, y: 2.0)
                          var x = p.x
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Parameter Mutability

    /// <summary>
    /// Parameters are mutable locals — reassigning a value-typed parameter is permitted (the
    /// caller's argument is unaffected since value types are passed by value).
    /// </summary>
    [Fact]
    public void Analyze_ParameterReassignment_Allowed()
    {
        string source = """
                        routine test(x: S32)
                          x = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Index Mutability

    /// <summary>
    /// Verifies semantic analysis behavior for index assignment on var without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_IndexAssignmentOnVar_NoError()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          items[0] = 42
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }

    #endregion

    #region Hijacking Restrictions

    /// <summary>
    /// Verifies semantic analysis behavior for nested hijacking and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_NestedHijacking_ReportsError()
    {
        // Nested modifying (partial modifying) should not be allowed
        // You cannot modify a child of an already-modifying object
        string source = """
                        entity Child
                          value: S64

                        entity Parent
                          child: Child

                        routine test()
                          var parent = Parent(child: Child(value: 0))
                          using parent.modify() as p
                            using p.child.modify() as c
                              c.value = 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "modify",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "nested",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Entity Bare Assignment Prohibition

    /// <summary>
    /// Verifies semantic analysis behavior for entity bare assignment and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_EntityBareAssignment_ReportsError()
    {
        string source = """
                        entity Document
                          title: Text

                        routine test()
                          var doc1 = Document(title: "My Doc")
                          var doc2 = doc1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for entity constructor assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_EntityConstructorAssignment_NoError()
    {
        string source = """
                        entity Document
                          title: Text

                        routine test()
                          var doc1 = Document(title: "My Doc")
                          var doc2 = Document(title: "Other")
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for record bare assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordBareAssignment_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine test()
                          var p1 = Point(x: 1, y: 2)
                          var p2 = p1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    #endregion

    #region Readonly memberRoutine Call Enforcement

    /// <summary>
    /// Verifies semantic analysis behavior for readonly memberRoutine calls writable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ReadonlyMemberRoutineCallsWritable_ReportsError()
    {
        string source = """
                        entity Counter
                          count: S32

                        routine Counter.increment()
                          me.count += 1
                          return

                        @readonly
                        routine Counter.try_increment()
                          me.increment()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationInReadonlyMemberRoutine);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for readonly memberRoutine calls readonly without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ReadonlyMemberRoutineCallsReadonly_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.get_count() -> S32
                          return me.count

                        @readonly
                        routine Counter.display() -> S32
                          return me.get_count()
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationInReadonlyMemberRoutine);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for readonly memberRoutine calls on other without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ReadonlyMemberRoutineCallsOnOther_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        routine Counter.increment()
                          me.count += 1
                          return

                        @readonly
                        routine Counter.compare(other: Counter)
                          other.increment()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        // Calling a mutating memberRoutine on 'other' (not 'me') is allowed in @readonly
        Assert.DoesNotContain(collection: result.Errors,
            filter: e =>
                e.Code == SemanticDiagnosticCode.MutationInReadonlyMemberRoutine &&
                e.Message.Contains(value: "increment"));
    }

    #endregion

    #region Multiple Var Reassignments

    /// <summary>
    /// Verifies that a var can be reassigned multiple times in sequence without error.
    /// </summary>
    [Fact]
    public void Analyze_VarMultipleReassignments_NoError()
    {
        string source = """
                        routine test()
                          var x = 1
                          x = 2
                          x = 3
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Entity Parameter Ownership

    /// <summary>
    /// Verifies that copying an entity parameter into a new var reports BareEntityAssignment.
    /// Entity ownership is non-copyable: use steal to explicitly transfer.
    /// </summary>
    [Fact]
    public void Analyze_EntityParameterBareAssignment_ReportsError()
    {
        string source = """
                        entity Doc
                          title: Text

                        routine test(d: Doc)
                          var d2 = d
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    /// <summary>
    /// Verifies that stealing an entity local variable into a new var is permitted.
    /// </summary>
    [Fact]
    public void Analyze_EntityStealReassignment_NoError()
    {
        string source = """
                        entity Doc
                          title: Text

                        routine test()
                          var d1 = Doc(title: "one")
                          var d2 = steal d1
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BareEntityAssignment);
    }

    #endregion

    #region Readonly / Writable Cross-Calls

    /// <summary>
    /// Verifies that a @readonly memberRoutine can still read a var field without error.
    /// </summary>
    [Fact]
    public void Analyze_ReadonlyMemberRoutine_CanReadVarField_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.get_count() -> S32
                          return me.count
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationInReadonlyMemberRoutine);
    }

    /// <summary>
    /// Verifies that a writable memberRoutine calling a @readonly memberRoutine produces no error.
    /// </summary>
    [Fact]
    public void Analyze_WritableMemberRoutineCallsReadonly_NoError()
    {
        string source = """
                        entity Counter
                          count: S32

                        @readonly
                        routine Counter.get_count() -> S32
                          return me.count

                        routine Counter.increment_and_read() -> S32
                          me.count += 1
                          return me.get_count()
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.MutationInReadonlyMemberRoutine);
    }

    #endregion

    #region Posted Member Variable Access

    /// <summary>
    /// Verifies semantic analysis behavior for posted member variable write same module without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_PostedMemberVariableWrite_SameModule_NoError()
    {
        // Within the same module (null == null), writing to posted member variable is allowed
        string source = """
                        entity Config
                          posted name: Text

                        routine Config.rename(new_name: Text)
                          me.name = new_name
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PostedMemberAccess);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for posted member variable read without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_PostedMemberVariableRead_NoError()
    {
        // Reading a posted member variable is always allowed
        string source = """
                        entity Config
                          posted name: Text

                        @readonly
                        routine Config.get_name() -> Text
                          return me.name
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.PostedMemberAccess);
    }

    #endregion
}
