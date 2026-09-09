namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for operator.
/// </summary>
public class OperatorTests
{
    #region Standard Arithmetic Operators

    /// <summary>
    /// Verifies that the parser accepts addition.
    /// </summary>
    [Fact]
    public void Parse_Addition()
    {
        string source = """
                        routine test() -> S32
                          return 1 + 2
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts subtraction.
    /// </summary>
    [Fact]
    public void Parse_Subtraction()
    {
        string source = """
                        routine test() -> S32
                          return 5 - 3
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multiplication.
    /// </summary>
    [Fact]
    public void Parse_Multiplication()
    {
        string source = """
                        routine test() -> S32
                          return 4 * 5
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts division.
    /// </summary>
    [Fact]
    public void Parse_Division()
    {
        string source = """
                        routine test() -> F32
                          return 10.0_f32 / 3.0_f32
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts floor division.
    /// </summary>
    [Fact]
    public void Parse_FloorDivision()
    {
        string source = """
                        routine test() -> S32
                          return 10 // 3
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts remainder.
    /// </summary>
    [Fact]
    public void Parse_Remainder()
    {
        string source = """
                        routine test() -> S32
                          return 10 % 3
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts power.
    /// </summary>
    [Fact]
    public void Parse_Power()
    {
        string source = """
                        routine test() -> S32
                          return 2 ** 10
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts negation.
    /// </summary>
    [Fact]
    public void Parse_Negation()
    {
        string source = """
                        routine test() -> S32
                          return -42
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained arithmetic.
    /// </summary>
    [Fact]
    public void Parse_ChainedArithmetic()
    {
        string source = """
                        routine test() -> S32
                          return 1 + 2 * 3 - 4 // 2
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts parenthesized arithmetic.
    /// </summary>
    [Fact]
    public void Parse_ParenthesizedArithmetic()
    {
        string source = """
                        routine test() -> S32
                          return (1 + 2) * (3 - 4)
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Wrapping Arithmetic Operators

    /// <summary>
    /// Verifies that the parser accepts wrapping add.
    /// </summary>
    [Fact]
    public void Parse_WrappingAdd()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 250
                          var b: U8 = 10
                          return a +% b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts wrapping subtract.
    /// </summary>
    [Fact]
    public void Parse_WrappingSubtract()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 5
                          var b: U8 = 10
                          return a -% b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts wrapping multiply.
    /// </summary>
    [Fact]
    public void Parse_WrappingMultiply()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 20
                          var b: U8 = 20
                          return a *% b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts wrapping power.
    /// </summary>
    [Fact]
    public void Parse_WrappingPower()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 2
                          var b: U8 = 10
                          return a **% b
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Clamping Arithmetic Operators

    /// <summary>
    /// Verifies that the parser accepts clamping add.
    /// </summary>
    [Fact]
    public void Parse_ClampingAdd()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 250
                          var b: U8 = 10
                          return a +^ b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts clamping subtract.
    /// </summary>
    [Fact]
    public void Parse_ClampingSubtract()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 5
                          var b: U8 = 10
                          return a -^ b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts clamping multiply.
    /// </summary>
    [Fact]
    public void Parse_ClampingMultiply()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 20
                          var b: U8 = 20
                          return a *^ b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts clamping power.
    /// </summary>
    [Fact]
    public void Parse_ClampingPower()
    {
        string source = """
                        routine test() -> U8
                          var a: U8 = 2
                          var b: U8 = 10
                          return a **^ b
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Comparison Operators

    /// <summary>
    /// Verifies that the parser accepts equal.
    /// </summary>
    [Fact]
    public void Parse_Equal()
    {
        string source = """
                        routine test() -> bool
                          return a == b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts not equal.
    /// </summary>
    [Fact]
    public void Parse_NotEqual()
    {
        string source = """
                        routine test() -> bool
                          return a != b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts less than.
    /// </summary>
    [Fact]
    public void Parse_LessThan()
    {
        string source = """
                        routine test() -> bool
                          return a < b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts less or equal.
    /// </summary>
    [Fact]
    public void Parse_LessOrEqual()
    {
        string source = """
                        routine test() -> bool
                          return a <= b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts greater than.
    /// </summary>
    [Fact]
    public void Parse_GreaterThan()
    {
        string source = """
                        routine test() -> bool
                          return a > b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts greater or equal.
    /// </summary>
    [Fact]
    public void Parse_GreaterOrEqual()
    {
        string source = """
                        routine test() -> bool
                          return a >= b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained comparison.
    /// </summary>
    [Fact]
    public void Parse_ChainedComparison()
    {
        string source = """
                        routine test() -> bool
                          return 0 <= index < length
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained range comparison.
    /// </summary>
    [Fact]
    public void Parse_ChainedRangeComparison()
    {
        string source = """
                        routine test() -> bool
                          return min <= value <= max
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Logical Operators

    /// <summary>
    /// Verifies that the parser accepts logical and.
    /// </summary>
    [Fact]
    public void Parse_LogicalAnd()
    {
        string source = """
                        routine test() -> bool
                          return a and b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts logical or.
    /// </summary>
    [Fact]
    public void Parse_LogicalOr()
    {
        string source = """
                        routine test() -> bool
                          return a or b
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts logical not.
    /// </summary>
    [Fact]
    public void Parse_LogicalNot()
    {
        string source = """
                        routine test() -> bool
                          return not a
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained logical.
    /// </summary>
    [Fact]
    public void Parse_ChainedLogical()
    {
        string source = """
                        routine test() -> bool
                          return a and b or c and not d
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts logical with comparison.
    /// </summary>
    [Fact]
    public void Parse_LogicalWithComparison()
    {
        string source = """
                        routine test() -> bool
                          return x > 0 and x < 100
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Bitwise Operators

    /// <summary>
    /// Verifies that the parser accepts bitwise and.
    /// </summary>
    [Fact]
    public void Parse_BitwiseAnd()
    {
        string source = """
                        routine test() -> U32
                          return bits & mask
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise or.
    /// </summary>
    [Fact]
    public void Parse_BitwiseOr()
    {
        string source = """
                        routine test() -> U32
                          return bits | mask
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise xor.
    /// </summary>
    [Fact]
    public void Parse_BitwiseXor()
    {
        string source = """
                        routine test() -> U32
                          return bits ^ mask
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise not.
    /// </summary>
    [Fact]
    public void Parse_BitwiseNot()
    {
        string source = """
                        routine test() -> U32
                          return ~bits
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts left shift.
    /// </summary>
    [Fact]
    public void Parse_LeftShift()
    {
        string source = """
                        routine test() -> U32
                          return value << 4
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts right shift.
    /// </summary>
    [Fact]
    public void Parse_RightShift()
    {
        string source = """
                        routine test() -> U32
                          return value >> 4
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts logical left shift.
    /// </summary>
    [Fact]
    public void Parse_LogicalLeftShift()
    {
        string source = """
                        routine test() -> U32
                          return value <<< 4
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts logical right shift.
    /// </summary>
    [Fact]
    public void Parse_LogicalRightShift()
    {
        string source = """
                        routine test() -> U32
                          return value >>> 4
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region None Coalescing Operator

    /// <summary>
    /// Verifies that the parser accepts none coalescing.
    /// </summary>
    [Fact]
    public void Parse_NoneCoalescing()
    {
        string source = """
                        routine test() -> S32
                          var value: S32? = none
                          return value ?? 42
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained none coalescing.
    /// </summary>
    [Fact]
    public void Parse_ChainedNoneCoalescing()
    {
        string source = """
                        routine test() -> S32
                          var a: S32? = none
                          var b: S32? = none
                          var c: S32 = 100
                          return a ?? b ?? c
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts none coalescing with memberRoutine call.
    /// </summary>
    [Fact]
    public void Parse_NoneCoalescingWithMemberRoutineCall()
    {
        string source = """
                        routine test() -> User
                          return try_get_user(id) ?? default_user()
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Assignment Operators

    /// <summary>
    /// Verifies that the parser accepts simple assignment.
    /// </summary>
    [Fact]
    public void Parse_SimpleAssignment()
    {
        string source = """
                        routine test()
                          var x = 0
                          x = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts add assignment.
    /// </summary>
    [Fact]
    public void Parse_AddAssignment()
    {
        string source = """
                        routine test()
                          var x = 0
                          x += 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts subtract assignment.
    /// </summary>
    [Fact]
    public void Parse_SubtractAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x -= 1
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multiply assignment.
    /// </summary>
    [Fact]
    public void Parse_MultiplyAssignment()
    {
        string source = """
                        routine test()
                          var x = 2
                          x *= 3
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts divide assignment.
    /// </summary>
    [Fact]
    public void Parse_DivideAssignment()
    {
        string source = """
                        routine test()
                          var x = 10.0_f32
                          x /= 2.0_f32
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts floor divide assignment.
    /// </summary>
    [Fact]
    public void Parse_FloorDivideAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x //= 3
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts remainder assignment.
    /// </summary>
    [Fact]
    public void Parse_RemainderAssignment()
    {
        string source = """
                        routine test()
                          var x = 10
                          x %= 3
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise and assignment.
    /// </summary>
    [Fact]
    public void Parse_BitwiseAndAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0xFF
                          bits &= 0x0F
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise or assignment.
    /// </summary>
    [Fact]
    public void Parse_BitwiseOrAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0x00
                          bits |= 0x0F
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise xor assignment.
    /// </summary>
    [Fact]
    public void Parse_BitwiseXorAssignment()
    {
        string source = """
                        routine test()
                          var bits: U32 = 0xFF
                          bits ^= 0x0F
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts left shift assignment.
    /// </summary>
    [Fact]
    public void Parse_LeftShiftAssignment()
    {
        string source = """
                        routine test()
                          var x: U32 = 1
                          x <<= 4
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts right shift assignment.
    /// </summary>
    [Fact]
    public void Parse_RightShiftAssignment()
    {
        string source = """
                        routine test()
                          var x: U32 = 16
                          x >>= 2
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Text Operators

    /// <summary>
    /// Verifies that the parser accepts text concatenation.
    /// </summary>
    [Fact]
    public void Parse_TextConcatenation()
    {
        string source = """
                        routine test() -> Text
                          return "Hello, " + "World!"
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts text repetition.
    /// </summary>
    [Fact]
    public void Parse_TextRepetition()
    {
        string source = """
                        routine test() -> Text
                          return "-" * 40
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Range Operators

    /// <summary>
    /// Verifies that the parser accepts inclusive range.
    /// </summary>
    [Fact]
    public void Parse_InclusiveRange()
    {
        string source = """
                        routine test()
                          each i in 0 til 10
                            show(i)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts range with step.
    /// </summary>
    [Fact]
    public void Parse_RangeWithStep()
    {
        string source = """
                        routine test()
                          each i in 0 til 100 by 5
                            show(i)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Operator Expressions

    /// <summary>
    /// Verifies that the parser accepts complex expression.
    /// </summary>
    [Fact]
    public void Parse_ComplexExpression()
    {
        string source = """
                        routine test() -> S32
                          return (a + b) * c - d // e % f ** g
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts mixed operator precedence.
    /// </summary>
    [Fact]
    public void Parse_MixedOperatorPrecedence()
    {
        string source = """
                        routine test() -> bool
                          return a + b > c * d and not (e == f or g != h)
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts bitwise with comparison.
    /// </summary>
    [Fact]
    public void Parse_BitwiseWithComparison()
    {
        string source = """
                        routine test() -> bool
                          return (bits & mask) != 0
                        """;

        AssertParses(source: source);
    }

    #endregion
}
