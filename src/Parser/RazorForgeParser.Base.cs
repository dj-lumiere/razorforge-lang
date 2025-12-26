using System.Numerics;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing base parser methods (token management, precedence, operators).
/// </summary>
public partial class RazorForgeParser
{
    #region Token Management

    /// <summary>
    /// Gets the token at the current position, or the last token if past the end.
    /// </summary>
    protected Token CurrentToken => Position < tokens.Count
        ? tokens[index: Position]
        : tokens[^1];

    /// <summary>
    /// Looks ahead in the token stream without consuming tokens.
    /// </summary>
    /// <param name="offset">Number of positions to look ahead (default 1).</param>
    /// <returns>The token at the specified offset, or the last token if past the end.</returns>
    protected Token PeekToken(int offset = 1)
    {
        return Position + offset < tokens.Count
            ? tokens[index: Position + offset]
            : tokens[^1];
    }

    /// <summary>
    /// Gets whether the parser has reached the end of the token stream.
    /// True if position is past the last token or if current token is EOF.
    /// </summary>
    protected bool IsAtEnd => Position >= tokens.Count || CurrentToken.Type == TokenType.Eof;

    /// <summary>
    /// Advances to the next token and returns the current one.
    /// Does not advance past the end of the token stream.
    /// </summary>
    /// <returns>The token at the current position before advancing.</returns>
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
    /// Checks if the current token matches the expected type without consuming it.
    /// </summary>
    /// <param name="type">The token type to check for.</param>
    /// <returns>True if the current token matches; false if at end or type doesn't match.</returns>
    protected bool Check(TokenType type)
    {
        return !IsAtEnd && CurrentToken.Type == type;
    }

    /// <summary>
    /// Checks if the current token matches any of the expected types without consuming it.
    /// </summary>
    /// <param name="types">The token types to check for.</param>
    /// <returns>True if the current token matches any of the types; false otherwise.</returns>
    protected bool Check(params TokenType[] types)
    {
        return types.Any(predicate: Check);
    }

    /// <summary>
    /// Consumes the current token if it matches the expected type, otherwise throws an error.
    /// </summary>
    /// <param name="type">The expected token type.</param>
    /// <param name="errorMessage">Error message to include in the exception if token doesn't match.</param>
    /// <returns>The consumed token.</returns>
    /// <exception cref="ParseException">Thrown if the current token doesn't match the expected type.</exception>
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
    /// Consumes the current token if it matches the expected type.
    /// </summary>
    /// <param name="type">The token type to match.</param>
    /// <returns>True if the token was consumed; false otherwise.</returns>
    protected bool Match(TokenType type)
    {
        if (Check(type: type))
        {
            Advance();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Consumes the current token if it matches any of the expected types.
    /// </summary>
    /// <param name="types">The token types to match.</param>
    /// <returns>True if the token was consumed; false otherwise.</returns>
    protected bool Match(params TokenType[] types)
    {
        foreach (TokenType type in types)
        {
            if (Check(type: type))
            {
                Advance();
                return true;
            }
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
        /// <summary>No precedence (used as sentinel value).</summary>
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

        /// <summary>Comparison operators: <c>in, is, from, follows, &lt;, &lt;=, &gt;, &gt;=, ==, !=</c> and their negated forms.</summary>
        Comparison = 8,

        /// <summary>Bitwise OR: <c>a | b</c>.</summary>
        BitwiseOr = 9,

        /// <summary>Bitwise XOR: <c>a ^ b</c>.</summary>
        BitwiseXor = 10,

        /// <summary>Bitwise AND: <c>a &amp; b</c>.</summary>
        BitwiseAnd = 11,

        /// <summary>Bit shift operators: <c>&lt;&lt;, &gt;&gt;</c> (arithmetic and logical).</summary>
        Shift = 12,

        /// <summary>Addition and subtraction with overflow variants: <c>+, -, +%, +^, +?, +!</c>, etc.</summary>
        Additive = 13,

        /// <summary>Multiplication, division, modulo with overflow variants: <c>*, /, //, %</c>, etc.</summary>
        Multiplicative = 14,

        /// <summary>Unary prefix operators: <c>+x, -x, ~x</c>.</summary>
        Unary = 15,

        /// <summary>Power/exponentiation with overflow variants: <c>**, **%, **^, **?, **!</c>.</summary>
        Power = 16,

        /// <summary>Postfix operators: indexing, member access, function calls: <c>x[i], x.member, x()</c>.</summary>
        Postfix = 17,

        /// <summary>Primary expressions: literals, identifiers, parenthesized expressions.</summary>
        Primary = 18
    }

    /// <summary>
    /// Gets the precedence level for a binary operator token.
    /// Used in Pratt parsing to determine how tightly operators bind.
    /// </summary>
    /// <param name="type">The token type representing the operator.</param>
    /// <returns>The precedence level, or <see cref="Precedence.None"/> if not a binary operator.</returns>
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
    /// RazorForge supports chained comparisons like <c>a &lt; b &lt; c</c>.
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
    /// Ensures chains are consistent (all ascending or all descending).
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
    /// Validates that a comparison chain maintains consistent direction.
    /// Valid: <c>a &lt; b &lt; c</c> (all ascending), <c>a == b == c</c> (all equality).
    /// Invalid: <c>a &lt; b &gt; c</c> (mixed ascending/descending).
    /// </summary>
    /// <param name="operators">The list of comparison operators in the chain.</param>
    /// <returns>True if the chain is valid; false if directions are inconsistent.</returns>
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
    /// Checks if an operator is right-associative.
    /// Right-associative operators group from right to left: <c>a ** b ** c</c> = <c>a ** (b ** c)</c>.
    /// </summary>
    /// <param name="type">The operator token type.</param>
    /// <returns>True if the operator is right-associative; false for left-associative.</returns>
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
    /// Skips tokens until a synchronization point is found for error recovery.
    /// Synchronization points include newlines and declaration keywords (entity, record, routine, etc.).
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
                case TokenType.Mutant:
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
    /// Creates a source location from the current token.
    /// </summary>
    /// <returns>A <see cref="SourceLocation"/> for the current token position.</returns>
    protected SourceLocation GetLocation()
    {
        return GetLocation(token: CurrentToken);
    }

    /// <summary>
    /// Creates a source location from the specified token.
    /// </summary>
    /// <param name="token">The token to get location from.</param>
    /// <returns>A <see cref="SourceLocation"/> with the token's file, line, column, and position.</returns>
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
    /// Converts a <see cref="TokenType"/> to the corresponding <see cref="BinaryOperator"/> enum value.
    /// </summary>
    /// <param name="tokenType">The token type representing the operator.</param>
    /// <returns>The corresponding binary operator.</returns>
    /// <exception cref="ParseException">Thrown if the token type is not a valid binary operator.</exception>
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
    /// Converts a <see cref="TokenType"/> to the corresponding <see cref="UnaryOperator"/> enum value.
    /// </summary>
    /// <param name="tokenType">The token type representing the operator.</param>
    /// <returns>The corresponding unary operator.</returns>
    /// <exception cref="ParseException">Thrown if the token type is not a valid unary operator.</exception>
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
    /// Parses a numeric literal token into its runtime value.
    /// Handles all numeric types including sized integers (s8-s128, u8-u128),
    /// floats (f16-f64), and deferred types (f128, d32, d64, d128, Integer, Decimal).
    /// </summary>
    /// <param name="token">The numeric literal token to parse.</param>
    /// <returns>The parsed value as the appropriate numeric type, or raw string for deferred types.</returns>
    /// <remarks>
    /// Types without direct C# equivalents (f128, d32, d64, d128, Integer, Decimal) are stored
    /// as raw strings in the AST. The semantic analyzer handles parsing these using native libraries.
    /// </remarks>
    protected object ParseNumericLiteral(Token token)
    {
        string text = token.Text;

        // Parse based on token type to preserve type information
        return token.Type switch
        {
            // Fixed-width integers with C# equivalents - parse immediately
            TokenType.S8Literal => ParseTypedInteger<sbyte>(text: text, suffix: "s8"),
            TokenType.S16Literal => ParseTypedInteger<short>(text: text, suffix: "s16"),
            TokenType.S32Literal => ParseTypedInteger<int>(text: text, suffix: "s32"),
            TokenType.S64Literal => ParseTypedInteger<long>(text: text, suffix: "s64"),
            TokenType.S128Literal => ParseTypedInteger<Int128>(text: text, suffix: "s128"),
            TokenType.U8Literal => ParseTypedInteger<byte>(text: text, suffix: "u8"),
            TokenType.U16Literal => ParseTypedInteger<ushort>(text: text, suffix: "u16"),
            TokenType.U32Literal => ParseTypedInteger<uint>(text: text, suffix: "u32"),
            TokenType.U64Literal => ParseTypedInteger<ulong>(text: text, suffix: "u64"),
            TokenType.U128Literal => ParseTypedInteger<UInt128>(text: text, suffix: "u128"),

            // Fixed-width floats with C# equivalents - parse immediately
            TokenType.F16Literal => ParseTypedFloat<Half>(text: text, suffix: "f16"),
            TokenType.F32Literal => ParseTypedFloat<float>(text: text, suffix: "f32"),
            TokenType.F64Literal => ParseTypedFloat<double>(text: text, suffix: "f64"),

            // Deferred types - store raw string for semantic analyzer to parse with native libraries
            // f128: IEEE binary128, requires LibBF
            // d32/d64/d128: IEEE decimal floating-point, requires Intel DFP library
            // Integer/Decimal: arbitrary precision, requires LibBF/MAPM
            TokenType.F128Literal => CleanNumericSuffix(text: text, suffix: "f128"),
            TokenType.D32Literal => CleanNumericSuffix(text: text, suffix: "d32"),
            TokenType.D64Literal => CleanNumericSuffix(text: text, suffix: "d64"),
            TokenType.D128Literal => CleanNumericSuffix(text: text, suffix: "d128"),
            TokenType.Integer => text,
            TokenType.Decimal => text,

            _ => text.Contains(value: '.')
                ? double.Parse(s: text)
                : text // Store as string for unknown numeric types
        };
    }

    /// <summary>
    /// Cleans a numeric literal by removing the type suffix and underscores.
    /// Returns raw string for deferred parsing by semantic analyzer.
    /// </summary>
    /// <param name="text">The literal text including suffix.</param>
    /// <param name="suffix">The type suffix to remove.</param>
    /// <returns>Cleaned string representation of the number.</returns>
    private static string CleanNumericSuffix(string text, string suffix)
    {
        string cleaned = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        return cleaned.Replace(oldValue: "_", newValue: "");
    }

    /// <summary>
    /// Parses a typed integer literal by removing the suffix and converting to the target type.
    /// </summary>
    /// <typeparam name="T">The target integer type (sbyte, short, int, long, etc.).</typeparam>
    /// <param name="text">The literal text including suffix.</param>
    /// <param name="suffix">The type suffix to remove (e.g., "s32", "u64").</param>
    /// <returns>The parsed integer value.</returns>
    private T ParseTypedInteger<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    /// <summary>
    /// Parses a typed float literal by removing the suffix and converting to the target type.
    /// </summary>
    /// <typeparam name="T">The target float type (Half, float, double, decimal).</typeparam>
    /// <param name="text">The literal text including suffix.</param>
    /// <param name="suffix">The type suffix to remove (e.g., "f32", "f64").</param>
    /// <returns>The parsed float value.</returns>
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
    /// Parses an arbitrary precision decimal literal.
    /// Currently uses C# decimal as a placeholder; a proper BigDecimal implementation is needed.
    /// </summary>
    /// <param name="text">The decimal literal text.</param>
    /// <returns>The parsed decimal value.</returns>
    private decimal ParseBigDecimal(string text)
    {
        // For now, use C#'s decimal as a placeholder for arbitrary precision decimal
        // In a real implementation, this would use a proper BigDecimal library
        return decimal.Parse(s: text);
    }

    #endregion

    #region Warnings

    /// <summary>
    /// Adds a compile warning to the warnings collection.
    /// </summary>
    /// <param name="message">The warning message to display.</param>
    /// <param name="token">The token where the warning occurred (for location info).</param>
    /// <param name="warningCode">The warning code (e.g., from <see cref="WarningCodes"/>).</param>
    /// <param name="severity">The severity level (default: Warning).</param>
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
    /// Gets all warnings collected during parsing as a read-only list.
    /// </summary>
    /// <returns>A read-only list of compile warnings.</returns>
    public IReadOnlyList<CompileWarning> GetWarnings()
    {
        return Warnings.AsReadOnly();
    }

    #endregion
}

/// <summary>
/// Exception thrown when a parsing error occurs that cannot be recovered from.
/// Contains the error message describing what went wrong.
/// </summary>
public class ParseException : Exception
{
    /// <summary>
    /// Initializes a new ParseException with the specified error message.
    /// </summary>
    /// <param name="message">A description of the parse error.</param>
    public ParseException(string message) : base(message: message)
    {
    }

    /// <summary>
    /// Initializes a new ParseException with the specified error message and inner exception.
    /// </summary>
    /// <param name="message">A description of the parse error.</param>
    /// <param name="innerException">The exception that caused this parse error.</param>
    public ParseException(string message, Exception innerException) : base(message: message, innerException: innerException)
    {
    }
}
