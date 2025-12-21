using System.Numerics;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing base parser methods (token management, precedence, operators).
/// </summary>
public partial class SuflaeParser
{
    #region Token Management

    protected Token CurrentToken => Position < Tokens.Count
        ? Tokens[index: Position]
        : Tokens[^1];

    protected Token PeekToken(int offset = 1)
    {
        return Position + offset < Tokens.Count
            ? Tokens[index: Position + offset]
            : Tokens[^1];
    }

    protected bool IsAtEnd => Position >= Tokens.Count || CurrentToken.Type == TokenType.Eof;

    /// <summary>
    /// Advance to the next token and return the current one
    /// </summary>
    protected Token Advance()
    {
        Token token = CurrentToken;
        if (!IsAtEnd)
        {
            Position++;
        }

        return token;
    }

    /// <summary>
    /// Check if current token matches the expected type
    /// </summary>
    protected bool Check(TokenType type)
    {
        return !IsAtEnd && CurrentToken.Type == type;
    }

    /// <summary>
    /// Check if current token matches any of the expected types
    /// </summary>
    protected bool Check(params TokenType[] types)
    {
        return types.Any(predicate: Check);
    }

    /// <summary>
    /// Consume token if it matches expected type, otherwise throw error
    /// </summary>
    protected Token Consume(TokenType type, string errorMessage)
    {
        if (Check(type: type))
        {
            return Advance();
        }

        Token current = CurrentToken;
        throw new ParseException(message: $"{errorMessage}. Expected {type}, got {current.Type}.");
    }

    /// <summary>
    /// Consume token if it matches expected type, return whether successful
    /// </summary>
    protected bool Match(TokenType type)
    {
        if (!Check(type: type))
        {
            return false;
        }

        Advance();
        return true;
    }

    /// <summary>
    /// Consume token if it matches any expected type, return whether successful
    /// </summary>
    protected bool Match(params TokenType[] types)
    {
        foreach (TokenType type in types)
        {
            if (!Check(type: type))
            {
                continue;
            }

            Advance();
            return true;
        }

        return false;
    }

    #endregion

    #region Precedence

    /// <summary>
    /// Operator precedence levels (higher number = higher precedence).
    /// Used for Pratt parsing to determine operator binding order.
    /// </summary>
    protected enum Precedence
    {
        /// <summary>No precedence (sentinel value).</summary>
        None = 0,

        /// <summary>Lambda expressions: <c>x =&gt; x + 1</c>.</summary>
        Lambda = 1,

        /// <summary>Conditional (ternary) expressions: <c>if cond then x else y</c>.</summary>
        Conditional = 2,

        /// <summary>None-coalescing operator: <c>x ?? default</c>.</summary>
        NoneCoalesce = 3,

        /// <summary>Range expressions: <c>a to b step c</c>.</summary>
        Range = 4,

        /// <summary>Logical OR: <c>a or b</c>.</summary>
        LogicalOr = 5,

        /// <summary>Logical AND: <c>a and b</c>.</summary>
        LogicalAnd = 6,

        /// <summary>Logical NOT: <c>not a</c>.</summary>
        LogicalNot = 7,

        /// <summary>Comparison operators: <c>in, is, follows, &lt;, &lt;=, &gt;, &gt;=, ==, !=</c> and negated forms.</summary>
        Comparison = 8,

        /// <summary>Bitwise OR: <c>a | b</c>.</summary>
        BitwiseOr = 9,

        /// <summary>Bitwise XOR: <c>a ^ b</c>.</summary>
        BitwiseXor = 10,

        /// <summary>Bitwise AND: <c>a &amp; b</c>.</summary>
        BitwiseAnd = 11,

        /// <summary>Bit shift operators: <c>&lt;&lt;, &gt;&gt;</c>.</summary>
        Shift = 12,

        /// <summary>Addition and subtraction with overflow variants.</summary>
        Additive = 13,

        /// <summary>Multiplication, division, modulo with overflow variants.</summary>
        Multiplicative = 14,

        /// <summary>Unary prefix operators: <c>-x, ~x</c>.</summary>
        Unary = 15,

        /// <summary>Power/exponentiation with overflow variants.</summary>
        Power = 16,

        /// <summary>Postfix operators: indexing, member access, function calls.</summary>
        Postfix = 17,

        /// <summary>Primary expressions: literals, identifiers, parenthesized.</summary>
        Primary = 18
    }

    /// <summary>
    /// Get precedence for binary operators
    /// </summary>
    protected Precedence GetBinaryPrecedence(TokenType type)
    {
        return type switch
        {
            // None coalescing operator
            TokenType.NoneCoalesce => Precedence.NoneCoalesce,

            // Logical operators
            TokenType.Or => Precedence.LogicalOr,
            TokenType.And => Precedence.LogicalAnd,

            // Comparison operators
            TokenType.In or TokenType.Is or TokenType.Follows => Precedence.Comparison,
            TokenType.NotIn or TokenType.IsNot or TokenType.NotFollows => Precedence.Comparison,
            TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual => Precedence.Comparison,
            TokenType.Equal or TokenType.NotEqual or TokenType.ThreeWayComparison => Precedence.Comparison,

            // Bitwise operators
            TokenType.Pipe => Precedence.BitwiseOr,
            TokenType.Caret => Precedence.BitwiseXor,
            TokenType.Ampersand => Precedence.BitwiseAnd,

            // Shift operators
            TokenType.LeftShift or TokenType.LeftShiftChecked or TokenType.RightShift or TokenType.LogicalLeftShift or TokenType.LogicalRightShift => Precedence.Shift,

            // Additive operators
            TokenType.Plus or TokenType.Minus => Precedence.Additive,
            TokenType.PlusWrap or TokenType.PlusSaturate or TokenType.PlusChecked => Precedence.Additive,
            TokenType.MinusWrap or TokenType.MinusSaturate or TokenType.MinusChecked => Precedence.Additive,

            // Multiplicative operators
            TokenType.Star or TokenType.Slash or TokenType.Divide or TokenType.Percent => Precedence.Multiplicative,
            TokenType.MultiplyWrap or TokenType.MultiplySaturate or TokenType.MultiplyChecked => Precedence.Multiplicative,
            TokenType.DivideChecked or TokenType.ModuloChecked => Precedence.Multiplicative,

            // Power operators
            TokenType.Power => Precedence.Power,
            TokenType.PowerWrap or TokenType.PowerSaturate or TokenType.PowerChecked => Precedence.Power,

            _ => Precedence.None
        };
    }

    /// <summary>
    /// Checks if a token represents a comparison operator that can be chained.
    /// Only <c>&lt;, &lt;=, ==</c> can chain together, and <c>&gt;, &gt;=, ==</c> can chain together.
    /// Three-way comparator (<c>&lt;=&gt;</c>) cannot be chained.
    /// </summary>
    /// <param name="type">The token type to check.</param>
    /// <returns>True if the token is a chainable comparison operator.</returns>
    protected bool IsChainableComparisonOperator(TokenType type)
    {
        return type switch
        {
            TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual or TokenType.Equal => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets the comparison direction for validating chained comparisons.
    /// Only chainable operators have direction: <c>&lt;, &lt;=</c> (ascending), <c>&gt;, &gt;=</c> (descending), <c>==</c> (equality).
    /// </summary>
    /// <param name="type">The comparison operator token type.</param>
    /// <returns>-1 for descending (&gt;, &gt;=), 0 for equality (==), 1 for ascending (&lt;, &lt;=).</returns>
    protected int GetComparisonDirection(TokenType type)
    {
        return type switch
        {
            TokenType.Less or TokenType.LessEqual => 1, // Ascending
            TokenType.Greater or TokenType.GreaterEqual => -1, // Descending
            TokenType.Equal => 0, // Equality
            _ => 0
        };
    }

    /// <summary>
    /// Validate that comparison chain maintains consistent direction
    /// </summary>
    protected bool IsValidComparisonChain(List<BinaryOperator> operators)
    {
        if (operators.Count <= 1)
        {
            return true;
        }

        var directions = operators.Select(selector: op => GetComparisonDirection(type: BinaryOperatorToToken(op: op)))
                                  .ToList();

        // All equality is valid
        if (directions.All(predicate: d => d == 0))
        {
            return true;
        }

        // Check for consistent direction (all ascending, all descending, or mixed with equality only)
        var nonZeroDirections = directions.Where(predicate: d => d != 0)
                                          .Distinct()
                                          .ToList();
        return nonZeroDirections.Count <= 1; // Only one direction (plus equality)
    }

    /// <summary>
    /// Converts a chainable <see cref="BinaryOperator"/> enum value back to its corresponding <see cref="TokenType"/>.
    /// Used for comparison chain validation. Only chainable operators are supported.
    /// </summary>
    /// <param name="op">The binary operator to convert.</param>
    /// <returns>The corresponding token type.</returns>
    protected TokenType BinaryOperatorToToken(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Less => TokenType.Less,
            BinaryOperator.LessEqual => TokenType.LessEqual,
            BinaryOperator.Greater => TokenType.Greater,
            BinaryOperator.GreaterEqual => TokenType.GreaterEqual,
            BinaryOperator.Equal => TokenType.Equal,
            _ => TokenType.Equal // Non-chainable operators default to equality (won't affect chain validation)
        };
    }

    /// <summary>
    /// Check if operator is right-associative
    /// </summary>
    protected bool IsRightAssociative(TokenType type)
    {
        return type switch
        {
            // Power operators are right-associative
            TokenType.Power or TokenType.PowerWrap or TokenType.PowerSaturate or TokenType.PowerChecked => true,

            // Assignment operators would be right-associative
            TokenType.Assign => true,

            _ => false
        };
    }

    #endregion

    #region Error Recovery

    /// <summary>
    /// Skip tokens until we find a synchronization point for error recovery
    /// </summary>
    protected void Synchronize()
    {
        Advance();

        while (!IsAtEnd)
        {
            if (PeekToken(offset: -1)
                   .Type == TokenType.Newline)
            {
                return;
            }

            switch (CurrentToken.Type)
            {
                case TokenType.Entity:
                case TokenType.Record:
                case TokenType.Choice:
                case TokenType.Variant:
                case TokenType.Protocol:
                case TokenType.Routine:
                case TokenType.Var:
                case TokenType.Let:
                case TokenType.Preset:
                case TokenType.If:
                case TokenType.Unless:
                case TokenType.While:
                case TokenType.For:
                case TokenType.Return:
                case TokenType.Throw:
                case TokenType.Absent:
                    return;
            }

            Advance();
        }
    }

    #endregion

    #region Location Helpers

    /// <summary>
    /// Create a source location from the current token
    /// </summary>
    protected SourceLocation GetLocation()
    {
        return GetLocation(token: CurrentToken);
    }

    /// <summary>
    /// Creates a source location from the specified token.
    /// </summary>
    /// <param name="token">The token to get location from.</param>
    /// <returns>A <see cref="SourceLocation"/> for error reporting.</returns>
    protected SourceLocation GetLocation(Token token)
    {
        return new SourceLocation(FileName: fileName,
            Line: token.Line,
            Column: token.Column,
            Position: token.Position);
    }

    #endregion

    #region Operator Conversion

    /// <summary>
    /// Convert TokenType to BinaryOperator
    /// </summary>
    protected BinaryOperator TokenToBinaryOperator(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.Plus => BinaryOperator.Add,
            TokenType.Minus => BinaryOperator.Subtract,
            TokenType.Star => BinaryOperator.Multiply,
            TokenType.Slash => BinaryOperator.TrueDivide,
            TokenType.Divide => BinaryOperator.FloorDivide,
            TokenType.Percent => BinaryOperator.Modulo,
            TokenType.Power => BinaryOperator.Power,

            // Overflow variants
            TokenType.PlusWrap => BinaryOperator.AddWrap,
            TokenType.PlusSaturate => BinaryOperator.AddSaturate,
            TokenType.PlusChecked => BinaryOperator.AddChecked,
            TokenType.MinusWrap => BinaryOperator.SubtractWrap,
            TokenType.MinusSaturate => BinaryOperator.SubtractSaturate,
            TokenType.MinusChecked => BinaryOperator.SubtractChecked,
            TokenType.MultiplyWrap => BinaryOperator.MultiplyWrap,
            TokenType.MultiplySaturate => BinaryOperator.MultiplySaturate,
            TokenType.MultiplyChecked => BinaryOperator.MultiplyChecked,
            TokenType.ModuloChecked => BinaryOperator.ModuloChecked,
            TokenType.DivideChecked => BinaryOperator.FloorDivideChecked,
            TokenType.PowerWrap => BinaryOperator.PowerWrap,
            TokenType.PowerSaturate => BinaryOperator.PowerSaturate,
            TokenType.PowerChecked => BinaryOperator.PowerChecked,

            TokenType.Equal => BinaryOperator.Equal,
            TokenType.NotEqual => BinaryOperator.NotEqual,
            TokenType.Less => BinaryOperator.Less,
            TokenType.LessEqual => BinaryOperator.LessEqual,
            TokenType.Greater => BinaryOperator.Greater,
            TokenType.GreaterEqual => BinaryOperator.GreaterEqual,
            TokenType.ThreeWayComparison => BinaryOperator.ThreeWayComparator,
            TokenType.And => BinaryOperator.And,
            TokenType.Or => BinaryOperator.Or,
            TokenType.Ampersand => BinaryOperator.BitwiseAnd,
            TokenType.Pipe => BinaryOperator.BitwiseOr,
            TokenType.Caret => BinaryOperator.BitwiseXor,
            TokenType.LeftShift => BinaryOperator.ArithmeticLeftShift,
            TokenType.LeftShiftChecked => BinaryOperator.ArithmeticLeftShiftChecked,
            TokenType.RightShift => BinaryOperator.ArithmeticRightShift,
            TokenType.LogicalLeftShift => BinaryOperator.LogicalLeftShift,
            TokenType.LogicalRightShift => BinaryOperator.LogicalRightShift,
            TokenType.Assign => BinaryOperator.Assign,
            TokenType.In => BinaryOperator.In,
            TokenType.NotIn => BinaryOperator.NotIn,
            TokenType.Is => BinaryOperator.Is,
            TokenType.IsNot => BinaryOperator.IsNot,
            TokenType.Follows => BinaryOperator.Follows,
            TokenType.NotFollows => BinaryOperator.NotFollows,
            TokenType.NoneCoalesce => BinaryOperator.NoneCoalesce,

            _ => throw new ParseException(message: $"Unknown binary operator: {tokenType}")
        };
    }

    /// <summary>
    /// Convert TokenType to UnaryOperator
    /// </summary>
    protected UnaryOperator TokenToUnaryOperator(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.Minus => UnaryOperator.Minus,
            TokenType.Not => UnaryOperator.Not,
            TokenType.Tilde => UnaryOperator.BitwiseNot,

            _ => throw new ParseException(message: $"Unknown unary operator: {tokenType}")
        };
    }

    #endregion

    #region Numeric Parsing

    /// <summary>
    /// Parse numeric literal value
    /// </summary>
    protected object ParseNumericLiteral(Token token)
    {
        string text = token.Text;

        // Parse based on token type to preserve type information
        return token.Type switch
        {
            TokenType.S32Literal => ParseTypedInteger<int>(text: text, suffix: "s32"),
            TokenType.S64Literal => ParseTypedInteger<long>(text: text, suffix: "s64"),
            TokenType.S128Literal => ParseTypedInteger<Int128>(text: text, suffix: "s128"),
            TokenType.U32Literal => ParseTypedInteger<uint>(text: text, suffix: "u32"),
            TokenType.U64Literal => ParseTypedInteger<ulong>(text: text, suffix: "u64"),
            TokenType.U128Literal => ParseTypedInteger<UInt128>(text: text, suffix: "u128"),
            TokenType.F32Literal => ParseTypedFloat<float>(text: text, suffix: "f32"),
            TokenType.F64Literal => ParseTypedFloat<double>(text: text, suffix: "f64"),
            // D128 uses .NET decimal as runtime representation (close approximation)
            // Full IEEE 754 decimal128 support requires native Intel DFPL integration
            TokenType.D128Literal => ParseDecimal128(text: text),
            TokenType.Integer => BigInteger.Parse(value: text), // Variable-sized integer
            TokenType.Decimal => ParseBigDecimal(text: text), // Variable-sized decimal
            _ => text.Contains(value: '.')
                ? double.Parse(s: text)
                : BigInteger.Parse(value: text)
        };
    }

    private T ParseTypedInteger<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    private T ParseTypedFloat<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        if (typeof(T) == typeof(Half))
        {
            // Half is not directly supported, use float as approximation
            return (T)(object)(float)double.Parse(s: cleanText);
        }

        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    /// <summary>
    /// Parses a D128 literal (IEEE 754 decimal128).
    /// Uses .NET decimal as approximation; native library integration provides full precision.
    /// </summary>
    private decimal ParseDecimal128(string text)
    {
        // Remove the d128 suffix if present
        string cleanText = text.EndsWith(value: "d128", comparisonType: StringComparison.OrdinalIgnoreCase)
            ? text.Substring(startIndex: 0, length: text.Length - 4)
            : text;
        return decimal.Parse(s: cleanText);
    }

    /// <summary>
    /// Parses an arbitrary-precision decimal value.
    /// Currently uses .NET decimal; full arbitrary precision requires MAPM library integration.
    /// </summary>
    private decimal ParseBigDecimal(string text)
    {
        return decimal.Parse(s: text);
    }

    #endregion

    #region Warnings

    /// <summary>
    /// Add a compile warning to the list
    /// </summary>
    protected void AddWarning(string message, Token token, string warningCode,
        WarningSeverity severity = WarningSeverity.Warning)
    {
        Warnings.Add(item: new CompileWarning(message: message,
            line: token.Line,
            column: token.Column,
            severity: severity,
            warningCode: warningCode));
    }

    /// <summary>
    /// Get all warnings collected during parsing
    /// </summary>
    public IReadOnlyList<CompileWarning> GetWarnings()
    {
        return Warnings.AsReadOnly();
    }

    /// <summary>
    /// Check for unnecessary closing brace (for Suflae)
    /// </summary>
    protected void CheckUnnecessaryBrace()
    {
        if (CurrentToken.Type == TokenType.RightBrace)
        {
            AddWarning(message: "Unnecessary closing brace detected. Suflae uses indentation-based scoping, not braces.",
                token: CurrentToken,
                warningCode: WarningCodes.UnnecessaryBraces,
                severity: WarningSeverity.StyleViolation);
        }
    }

    #endregion
}
