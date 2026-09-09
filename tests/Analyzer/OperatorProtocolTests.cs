using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for operator protocol.
/// </summary>
public class OperatorProtocolTests
{
    #region Correct Protocol Conformance

    /// <summary>
    /// Verifies semantic analysis behavior for addable with follows without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_AddableWithFollows_NoError()
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
                          return Vector(x: me.x +% you.x, y: me.y +% you.y)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for equatable with follows without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_EquatableWithFollows_NoError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        record Point obeys Equatable
                          x: S32
                          y: S32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for comparable with follows without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_ComparableWithFollows_NoError()
    {
        string source = """
                        choice ComparisonSign
                          ME_SMALL
                          SAME
                          ME_LARGE

                        protocol Comparable
                          @readonly
                          routine Me.$cmp(you: Me) -> ComparisonSign

                        record Score obeys Comparable
                          value: S32

                        @readonly
                        routine Score.$cmp(you: Score) -> ComparisonSign
                          if me.value < you.value
                            return ME_SMALL
                          if me.value > you.value
                            return ME_LARGE
                          return SAME
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for multiple operator protocols without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_MultipleOperatorProtocols_NoError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        protocol Subtractable
                          @readonly
                          routine Me.$sub(you: Me) -> Me

                        record Complex obeys Addable, Subtractable
                          real: F64
                          imag: F64

                        @readonly
                        routine Complex.$add(you: Complex) -> Complex
                          return Complex(real: me.real + you.real, imag: me.imag + you.imag)

                        @readonly
                        routine Complex.$sub(you: Complex) -> Complex
                          return Complex(real: me.real - you.real, imag: me.imag - you.imag)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Missing Protocol Conformance

    /// <summary>
    /// Verifies semantic analysis behavior for add without addable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_AddWithoutAddable_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        record Vector
                          x: S32
                          y: S32

                        @readonly
                        routine Vector.$add(you: Vector) -> Vector
                          return Vector(x: me.x + you.x, y: me.y + you.y)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Addable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "$add",
                    comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Addable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for eq without equatable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_EqWithoutEquatable_ReportsError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        record Point
                          x: S32
                          y: S32

                        @readonly
                        routine Point.$eq(you: Point) -> Bool
                          return me.x == you.x and me.y == you.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Equatable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "$eq",
                    comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Equatable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for cmp without comparable and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_CmpWithoutComparable_ReportsError()
    {
        string source = """
                        choice ComparisonSign
                          ME_SMALL
                          SAME
                          ME_LARGE

                        protocol Comparable
                          @readonly
                          routine Me.$cmp(you: Me) -> ComparisonSign

                        record Score
                          value: S32

                        @readonly
                        routine Score.$cmp(you: Score) -> ComparisonSign
                          return SAME
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0,
            userMessage: "Expected error for missing Comparable protocol");
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "$cmp",
                    comparisonType: StringComparison.OrdinalIgnoreCase) &&
                e.Message.Contains(value: "Comparable",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Wrong memberRoutine Signature

    /// <summary>
    /// Verifies that implementing $eq with the wrong return type produces an error.
    /// </summary>
    [Fact]
    public void Analyze_EqWithWrongReturnType_ReportsError()
    {
        string source = """
                        protocol Equatable
                          @readonly
                          routine Me.$eq(you: Me) -> Bool

                        record Point obeys Equatable
                          x: S32
                          y: S32

                        @readonly
                        routine Point.$eq(you: Point) -> S32
                          return 0_s32
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    /// <summary>
    /// Verifies that implementing $add with wrong parameter type produces an error.
    /// </summary>
    [Fact]
    public void Analyze_AddWithWrongParamType_ReportsError()
    {
        string source = """
                        protocol Addable
                          @readonly
                          routine Me.$add(you: Me) -> Me

                        record Vector obeys Addable
                          x: S32
                          y: S32

                        @readonly
                        routine Vector.$add(you: S32) -> Vector
                          return Vector(x: me.x, y: me.y)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Mixed Operator and Normal memberRoutines

    /// <summary>
    /// Verifies that a type can define both operator wired memberRoutines and regular business memberRoutines.
    /// </summary>
    [Fact]
    public void Analyze_MixedOperatorAndNormalMemberRoutines_NoError()
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
                          return Vector(x: me.x +% you.x, y: me.y +% you.y)

                        @readonly
                        routine Vector.magnitude_squared() -> S32
                          return me.x * me.x +% me.y * me.y
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Non-Operator memberRoutines (No Protocol Required)

    /// <summary>
    /// Verifies semantic analysis behavior for create without protocol without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_CreateWithoutProtocol_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        routine Point.create() -> Point
                          return Point(x: 0, y: 0)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for destroy without protocol without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_DestroyWithoutProtocol_NoError()
    {
        string source = """
                        entity Resource
                          handle: S32

                        dangerous routine Resource.destroy()
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for regular memberRoutine without protocol without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void Analyze_RegularMemberRoutineWithoutProtocol_NoError()
    {
        string source = """
                        record Point
                          x: S32
                          y: S32

                        @readonly
                        routine Point.magnitude() -> F64
                          return 0.0
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
