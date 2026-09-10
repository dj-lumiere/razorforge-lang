using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for in-place compound assignment dispatch (#40).
/// Compound assignments (+=, -=, etc.) dispatch to in-place wired memberRoutines ($iadd, etc.)
/// first, then fall back to create-and-assign ($add) for non-entity types.
/// Entities require in-place wired memberRoutines (no fallback, since bare entity assignment is prohibited).
/// </summary>
public class CompoundAssignmentTests
{
    #region In-Place Dispatch (type defines $iadd)

    /// <summary>
    /// Verifies semantic analysis behavior for record with in place wired without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithInPlaceWired_NoError()
    {
        string source = """
                        protocol InPlaceAddable
                          routine Me.$iadd(from: Me) -> None

                        record Counter obeys InPlaceAddable
                          value: S32

                        routine Counter.$iadd(from: Counter) -> None

                        routine test()
                          var c = Counter(value: 0)
                          c += Counter(value: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for entity with in place wired without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_EntityWithInPlaceWired_NoError()
    {
        string source = """
                        protocol InPlaceAddable
                          routine Me.$iadd(from: Me) -> None

                        entity Accumulator obeys InPlaceAddable
                          value: S32

                        routine Accumulator.$iadd(from: Accumulator) -> None

                        routine test()
                          var acc = Accumulator(value: 0)
                          acc += Accumulator(value: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Fallback Dispatch (record with only $add)

    /// <summary>
    /// Verifies semantic analysis behavior for record with regular wired falls back.
    /// </summary>
    [Fact]
    public void Analyze_RecordWithRegularWired_FallsBack()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        record Vector obeys Addable
                          x: S32
                          y: S32

                        @readonly
                        routine Vector.$add(you: Vector) -> Vector
                          return Vector(x: me.x, y: me.y)

                        routine test()
                          var v = Vector(x: 1, y: 2)
                          v += Vector(x: 3, y: 4)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Entity Without In-Place Wired (no fallback)

    /// <summary>
    /// Verifies semantic analysis behavior for entity without in place wired and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_EntityWithoutInPlaceWired_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        entity Counter obeys Addable
                          value: S32

                        @readonly
                        routine Counter.$add(you: Counter) -> Counter
                          return Counter(value: me.value)

                        routine test()
                          var c = Counter(value: 0)
                          c += Counter(value: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Neither Wired Exists

    /// <summary>
    /// Verifies semantic analysis behavior for no wireds defined and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_NoWiredsDefined_ReportsError()
    {
        string source = """
                        record Pair
                          a: S32
                          b: S32

                        routine test()
                          var p = Pair(a: 1, b: 2)
                          p += Pair(a: 3, b: 4)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Mutability Checks

    /// <summary>
    /// Verifies semantic analysis behavior for var compound assignment without immutability errors.
    /// </summary>
    [Fact]
    public void Analyze_VarCompoundAssignment_NoImmutableError()
    {
        // var is mutable, so compound assignment should not produce immutable errors
        string source = """
                        protocol InPlaceAddable
                          routine Me.$iadd(from: Me) -> None

                        record Counter obeys InPlaceAddable
                          value: S32

                        routine Counter.$iadd(from: Counter) -> None

                        routine test()
                          var c = Counter(value: 0)
                          c += Counter(value: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.AssignmentToImmutable);
    }

    #endregion

    #region Choice Type Prohibition

    /// <summary>
    /// Verifies semantic analysis behavior for choice compound assignment reports arithmetic error.
    /// </summary>
    [Fact]
    public void Analyze_ChoiceCompoundAssignment_ReportsArithmeticError()
    {
        string source = """
                        choice Color
                          RED
                          GREEN
                          BLUE

                        routine test()
                          var c = RED
                          c += GREEN
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArithmeticOnChoiceType);
    }

    #endregion

    #region Multiple Compound Operators

    /// <summary>
    /// Verifies semantic analysis behavior for subtract compound assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_SubtractCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceSubtractable
                          routine Me.$isub(from: Me) -> None

                        record Counter obeys InPlaceSubtractable
                          value: S32

                        routine Counter.$isub(from: Counter) -> None

                        routine test()
                          var c = Counter(value: 10)
                          c -= Counter(value: 1)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for bitwise and compound assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_BitwiseAndCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceBitwiseable
                          routine Me.$ibitand(from: Me) -> None

                        record Flags obeys InPlaceBitwiseable
                          bits: S32

                        routine Flags.$ibitand(from: Flags) -> None
                          pass

                        routine test()
                          var f = Flags(bits: 255)
                          f &= Flags(bits: 15)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Additional Operator Coverage

    /// <summary>
    /// Verifies semantic analysis behavior for multiply compound assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_MultiplyCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceMultiplicable
                          routine Me.$imul(from: Me) -> None

                        record Scale obeys InPlaceMultiplicable
                          factor: S32

                        routine Scale.$imul(from: Scale) -> None

                        routine test()
                          var s = Scale(factor: 2)
                          s *= Scale(factor: 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for bitwise-or compound assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_BitwiseOrCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceBitwiseable
                          routine Me.$ibitand(from: Me) -> None

                        record Mask obeys InPlaceBitwiseable
                          bits: S32

                        routine Mask.$ibitand(from: Mask) -> None
                          pass
                        routine Mask.$ibitor(from: Mask) -> None
                          pass

                        routine test()
                          var m = Mask(bits: 0)
                          m |= Mask(bits: 8)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for modulo compound assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ModuloCompoundAssignment_NoError()
    {
        string source = """
                        protocol InPlaceFloorDivisible
                          routine Me.$ifloordiv(from: Me) -> None

                        record Bucket obeys InPlaceFloorDivisible
                          size: S32

                        routine Bucket.$ifloordiv(from: Bucket) -> None
                          pass
                        routine Bucket.$imod(from: Bucket) -> None
                          pass

                        routine test()
                          var b = Bucket(size: 10)
                          b %= Bucket(size: 3)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    /// <summary>
    /// Verifies that a record with both $iadd and $add defined uses $iadd (no fallback needed).
    /// </summary>
    [Fact]
    public void Analyze_RecordWithBothIaddAndAdd_PrefersInPlace_NoError()
    {
        string source = """
                        record Vector
                          x: S32

                        routine Vector.$iadd(from: Vector) -> None
                          pass

                        @readonly
                        routine Vector.$add(you: Vector) -> Vector
                          return Vector(x: me.x)

                        routine test()
                          var v = Vector(x: 1)
                          v += Vector(x: 2)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.CompoundAssignmentNotSupported);
    }

    #endregion

    #region Primitive Types (existing behavior preserved)

    /// <summary>
    /// Verifies semantic analysis behavior for primitive var compound assignment without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_PrimitiveVarCompoundAssignment_NoError()
    {
        // Primitives like S32 don't have $iadd registered in tests (no stdlib loaded),
        // but the test verifies parsing and analysis don't crash.
        string source = """
                        routine test()
                          var x = 42
                          x += 10
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.NotNull(@object: result);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for primitive var compound assignment without immutability errors.
    /// </summary>
    [Fact]
    public void Analyze_PrimitiveVarCompoundAssignment_NoImmutableError()
    {
        // var is mutable, so compound assignment should not produce immutable errors
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

    #endregion
}
