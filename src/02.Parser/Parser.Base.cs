using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing base parser memberRoutines (token management, precedence, operators).
/// </summary>
public partial class Parser
{
    #region Token Management

    private Token CurrentToken => _position < _tokens.Count
        ? _tokens[index: _position]
        : _tokens[^1];

    private Token PeekToken(int offset = 1)
    {
        return _position + offset < _tokens.Count
            ? _tokens[index: _position + offset]
            : _tokens[^1];
    }

    private bool IsAtEnd => _position >= _tokens.Count || CurrentToken.Type == TokenType.Eof;

    /// <summary>
    /// Advance to the next token and return the current one
    /// </summary>
    private Token Advance()
    {
        Token token = CurrentToken;
        if (!IsAtEnd)
        {
            _position++;
        }

        return token;
    }

    /// <summary>
    /// Check if current token matches the expected type
    /// </summary>
    private bool Check(TokenType type)
    {
        return !IsAtEnd && CurrentToken.Type == type;
    }

    /// <summary>
    /// Check if current token matches any of the expected types
    /// </summary>
    private bool Check(params TokenType[] types)
    {
        return types.Any(predicate: Check);
    }

    /// <summary>
    /// Consume token if it matches expected type, otherwise throw error
    /// </summary>
    private Token Consume(TokenType type, string errorMessage)
    {
        if (Check(type: type))
        {
            return Advance();
        }

        Token current = CurrentToken;
        throw new GrammarException(code: GetExpectedTokenCode(type: type),
            message: $"{errorMessage}. Expected {type}, got {current.Type}",
            fileName: FileName,
            line: current.Line,
            column: current.Column,
            language: _language);
    }

    /// <summary>
    /// Maps an expected token type to the most specific grammar diagnostic code.
    /// Used by <see cref="Consume"/> to provide precise error codes.
    /// </summary>
    private static GrammarDiagnosticCode GetExpectedTokenCode(TokenType type)
    {
        return type switch
        {
            TokenType.RightParen => GrammarDiagnosticCode.ExpectedClosingParen,
            TokenType.RightBracket => GrammarDiagnosticCode.ExpectedClosingBracket,
            TokenType.RightBrace => GrammarDiagnosticCode.ExpectedClosingBrace,
            TokenType.Colon => GrammarDiagnosticCode.ExpectedColon,
            TokenType.Arrow => GrammarDiagnosticCode.ExpectedArrow,
            TokenType.FatArrow => GrammarDiagnosticCode.ExpectedFatArrow,
            TokenType.Assign => GrammarDiagnosticCode.ExpectedEquals,
            TokenType.Comma => GrammarDiagnosticCode.ExpectedComma,
            TokenType.Dot => GrammarDiagnosticCode.ExpectedDot,
            TokenType.Identifier => GrammarDiagnosticCode.ExpectedIdentifier,
            TokenType.LeftParen => GrammarDiagnosticCode.ExpectedLeftParen,
            TokenType.As => GrammarDiagnosticCode.ExpectedAs,
_ => GrammarDiagnosticCode.UnexpectedToken
        };
    }

    /// <summary>
    /// Consume token if it matches expected type, return whether successful
    /// </summary>
    private bool Match(TokenType type)
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


    /// <summary>
    /// Skips Newline tokens only if the specified token type follows them.
    /// Returns true always (to be used in while loop conditions).
    /// </summary>
    private bool SkipNewlinesIfFollowedBy(TokenType type)
    {
        int offset = 0;
        while (PeekToken(offset: offset)
                  .Type == TokenType.Newline)
        {
            offset++;
        }

        if (PeekToken(offset: offset)
               .Type != type)
        {
            return true; // Don't skip, but let caller check Match
        }

        // Actually consume the newlines
        while (Match(type: TokenType.Newline)) { } // NOSONAR S108: intentional newline-consuming loop

        return true;
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

        /// <summary>Comparison operators: <c>in, is, obeys, &lt;, &lt;=, &gt;, &gt;=, ==, !=</c> and negated forms.</summary>
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
    protected static Precedence GetBinaryPrecedence(TokenType type)
    {
        return type switch
        {
            // None coalescing operator
            TokenType.NoneCoalesce => Precedence.NoneCoalesce,

            // Logical operators
            TokenType.Or => Precedence.LogicalOr,
            TokenType.And => Precedence.LogicalAnd,

            // Comparison operators
            TokenType.In or TokenType.Is or TokenType.Obeys => Precedence.Comparison,
            TokenType.NotIn or TokenType.IsNot or TokenType.Disobeys => Precedence.Comparison,
            TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual =>
                Precedence.Comparison,
            TokenType.Equal or TokenType.NotEqual or TokenType.ThreeWayComparison => Precedence
               .Comparison,
            TokenType.IdentityEqual or TokenType.IdentityNotEqual => Precedence.Comparison,

            // Bitwise operators
            TokenType.Pipe => Precedence.BitwiseOr,
            TokenType.Caret => Precedence.BitwiseXor,
            TokenType.Ampersand => Precedence.BitwiseAnd,

            // Shift operators
            TokenType.LeftShift or TokenType.RightShift or TokenType.LogicalLeftShift
                or TokenType.LogicalRightShift => Precedence.Shift,

            // Additive operators
            TokenType.Plus or TokenType.Minus => Precedence.Additive,
            TokenType.PlusWrap or TokenType.PlusClamp => Precedence.Additive,
            TokenType.MinusWrap or TokenType.MinusClamp => Precedence.Additive,
            TokenType.PlusUnchecked or TokenType.MinusUnchecked => Precedence.Additive,

            // Multiplicative operators
            TokenType.Star or TokenType.Slash or TokenType.Divide or TokenType.Percent =>
                Precedence.Multiplicative,
            TokenType.MultiplyWrap or TokenType.MultiplyClamp => Precedence.Multiplicative,
            TokenType.SlashClamp => Precedence.Multiplicative,
            TokenType.MultiplyUnchecked or TokenType.SlashUnchecked or TokenType.DivideUnchecked
                or TokenType.PercentUnchecked => Precedence.Multiplicative,

            // Power operators
            TokenType.Power => Precedence.Power,
            TokenType.PowerWrap or TokenType.PowerClamp => Precedence.Power,
            TokenType.PowerUnchecked => Precedence.Power,

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
    protected static bool IsChainableComparisonOperator(TokenType type)
    {
        return type switch
        {
            TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual
                or TokenType.Equal => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets the comparison direction for validating chained comparisons.
    /// Only chainable operators have direction: <c>&lt;, &lt;=</c> (ascending), <c>&gt;, &gt;=</c> (descending), <c>==</c> (equality).
    /// </summary>
    /// <param name="type">The comparison operator token type.</param>
    /// <returns>-1 for descending (&gt;, &gt;=), 0 for equality (==), 1 for ascending (&lt;, &lt;=).</returns>
    protected static int GetComparisonDirection(TokenType type)
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
    protected static bool IsValidComparisonChain(List<BinaryOperator> operators)
    {
        if (operators.Count <= 1)
        {
            return true;
        }

        var directions = operators
                        .Select(selector: op =>
                             GetComparisonDirection(type: BinaryOperatorToToken(op: op)))
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
    protected static TokenType BinaryOperatorToToken(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Less => TokenType.Less,
            BinaryOperator.LessEqual => TokenType.LessEqual,
            BinaryOperator.Greater => TokenType.Greater,
            BinaryOperator.GreaterEqual => TokenType.GreaterEqual,
            BinaryOperator.Equal => TokenType.Equal,
            _ => TokenType
               .Equal // Non-chainable operators default to equality (won't affect chain validation)
        };
    }

    /// <summary>
    /// Check if operator is right-associative
    /// </summary>
    protected static bool IsRightAssociative(TokenType type)
    {
        return type switch
        {
            // Power operators are right-associative
            TokenType.Power or TokenType.PowerWrap or TokenType.PowerClamp => true,

            // Assignment operators would be right-associative
            TokenType.Assign => true,

            _ => false
        };
    }

    #endregion

    #region Error Recovery

    /// <summary>
    /// Skip tokens until we find a top-level synchronization point for error recovery.
    /// Indent-aware: whole indented regions are skipped as units (otherwise the parser
    /// resumes inside a block body it never entered, misparses every line of it, and a
    /// single error cascades through the rest of the file). The indentation bookkeeping
    /// is reset to base first, because the error may have escaped from inside blocks
    /// whose levels were already pushed.
    /// </summary>
    protected void Synchronize()
    {
        while (_indentationStack.Count > 1)
        {
            _indentationStack.Pop();
        }
        _currentIndentationLevel = _indentationStack.Peek();

        int depth = 0;
        bool first = true;
        while (!IsAtEnd)
        {
            TokenType t = CurrentToken.Type;
            if (t == TokenType.Indent)
            {
                depth++;
                Advance();
                first = false;
                continue;
            }

            if (t == TokenType.Dedent)
            {
                if (depth > 0) depth--;
                Advance();
                first = false;
                continue;
            }

            if (!first && depth == 0 && IsTopLevelSynchronizationPoint(t: t))
            {
                return;
            }

            Advance();
            first = false;
        }
    }

    /// <summary>
    /// Decides whether the token <paramref name="t"/> at base indentation depth is a top-level
    /// synchronization point where <see cref="Synchronize"/> can safely resume parsing — either the
    /// previous token ended a statement (Newline/Dedent) or <paramref name="t"/> begins a top-level
    /// declaration/statement keyword.
    /// </summary>
    private bool IsTopLevelSynchronizationPoint(TokenType t)
    {
        if (PeekToken(offset: -1)
               .Type is TokenType.Newline or TokenType.Dedent)
        {
            return true;
        }

        return t switch
        {
            TokenType.Entity => true,
            TokenType.Record => true,
            TokenType.Choice => true,
            TokenType.Flags => true,
            TokenType.Variant => true,
            TokenType.Protocol => true,
            TokenType.Routine => true,
            TokenType.Var => true,
            TokenType.Preset => true,
            TokenType.If => true,
            TokenType.Unless => true,
            TokenType.While => true,
            TokenType.Each => true,
            TokenType.Return => true,
            TokenType.Throw => true,
            TokenType.Pierce => true,
            TokenType.Absent => true,
            _ => false
        };
    }

    /// <summary>
    /// Statement-level error recovery inside an indented block: skips to the start of
    /// the next statement at THIS block's nesting depth without escaping the block.
    /// Nested Indent/Dedent pairs belonging to the failed statement's sub-blocks are
    /// skipped as opaque regions (their levels were never pushed onto the indentation
    /// stack once the inner ParseIndentedBlock catch handles its own errors).
    /// Stops without consuming a Dedent that would close this block, so the enclosing
    /// block loop terminates normally.
    /// </summary>
    protected void SynchronizeWithinBlock()
    {
        int depth = 0;
        while (!IsAtEnd)
        {
            TokenType t = CurrentToken.Type;
            if (t == TokenType.Indent)
            {
                depth++;
                Advance();
                continue;
            }

            if (t == TokenType.Dedent)
            {
                if (depth == 0)
                {
                    return; // closes OUR block — leave it for the block loop
                }

                depth--;
                Advance();
                continue;
            }

            if (t == TokenType.Newline && depth == 0)
            {
                Advance();
                if (Check(type: TokenType.Indent))
                {
                    // Orphaned body of the failed statement (e.g. the block under a bad
                    // `if` header) — skip it as a region instead of tripping over it.
                    continue;
                }

                return; // next token starts a fresh statement at our level
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
        return new SourceLocation(FileName: FileName,
            Line: token.Line,
            Column: token.Column,
            Position: token.Position);
    }

    /// <summary>
    /// Creates a GrammarException with the current token's location information.
    /// Use as: throw ThrowParseError("message");
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>The exception to throw.</returns>
    protected GrammarException ThrowParseError(string message)
    {
        Token token = CurrentToken;
        return new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
            message: message,
            fileName: FileName,
            line: token.Line,
            column: token.Column,
            language: _language);
    }

    /// <summary>
    /// Creates a GrammarException with the specified token's location information.
    /// Use as: throw ThrowParseError("message", token);
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="token">The token where the error occurred.</param>
    /// <returns>The exception to throw.</returns>
    protected GrammarException ThrowParseError(string message, Token token)
    {
        return new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
            message: message,
            fileName: FileName,
            line: token.Line,
            column: token.Column,
            language: _language);
    }

    /// <summary>
    /// Creates a GrammarException with a specific diagnostic code and the current token's location.
    /// Use as: throw ThrowParseError(code, "message");
    /// </summary>
    /// <param name="code">The diagnostic code for the error.</param>
    /// <param name="message">The error message.</param>
    /// <returns>The exception to throw.</returns>
    protected GrammarException ThrowParseError(GrammarDiagnosticCode code, string message)
    {
        Token token = CurrentToken;
        return new GrammarException(code: code,
            message: message,
            fileName: FileName,
            line: token.Line,
            column: token.Column,
            language: _language);
    }

    /// <summary>
    /// Creates a GrammarException with a specific diagnostic code and token location.
    /// Use as: throw ThrowParseError(code, "message", token);
    /// </summary>
    /// <param name="code">The diagnostic code for the error.</param>
    /// <param name="message">The error message.</param>
    /// <param name="token">The token where the error occurred.</param>
    /// <returns>The exception to throw.</returns>
    protected GrammarException ThrowParseError(GrammarDiagnosticCode code, string message,
        Token token)
    {
        return new GrammarException(code: code,
            message: message,
            fileName: FileName,
            line: token.Line,
            column: token.Column,
            language: _language);
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

            // Overflow variants (wrap and clamp)
            TokenType.PlusWrap => BinaryOperator.AddWrap,
            TokenType.PlusClamp => BinaryOperator.AddClamp,
            TokenType.MinusWrap => BinaryOperator.SubtractWrap,
            TokenType.MinusClamp => BinaryOperator.SubtractClamp,
            TokenType.MultiplyWrap => BinaryOperator.MultiplyWrap,
            TokenType.MultiplyClamp => BinaryOperator.MultiplyClamp,
            TokenType.SlashClamp => BinaryOperator.TrueDivClamp,
            TokenType.PowerWrap => BinaryOperator.PowerWrap,
            TokenType.PowerClamp => BinaryOperator.PowerClamp,

            // Unchecked variants
            TokenType.PlusUnchecked => BinaryOperator.AddUnchecked,
            TokenType.MinusUnchecked => BinaryOperator.SubtractUnchecked,
            TokenType.MultiplyUnchecked => BinaryOperator.MultiplyUnchecked,
            TokenType.SlashUnchecked => BinaryOperator.TrueDivideUnchecked,
            TokenType.DivideUnchecked => BinaryOperator.FloorDivideUnchecked,
            TokenType.PercentUnchecked => BinaryOperator.ModuloUnchecked,
            TokenType.PowerUnchecked => BinaryOperator.PowerUnchecked,

            TokenType.Equal => BinaryOperator.Equal,
            TokenType.NotEqual => BinaryOperator.NotEqual,
            TokenType.IdentityEqual => BinaryOperator.IdentityEqual,
            TokenType.IdentityNotEqual => BinaryOperator.IdentityNotEqual,
            TokenType.Less => BinaryOperator.Less,
            TokenType.LessEqual => BinaryOperator.LessEqual,
            TokenType.Greater => BinaryOperator.Greater,
            TokenType.GreaterEqual => BinaryOperator.GreaterEqual,
            TokenType.ThreeWayComparison => BinaryOperator.ThreeWayComparator,
            TokenType.And => BinaryOperator.And,
            TokenType.Or => BinaryOperator.Or,
            TokenType.But => BinaryOperator.But,
            TokenType.Ampersand => BinaryOperator.BitwiseAnd,
            TokenType.Pipe => BinaryOperator.BitwiseOr,
            TokenType.Caret => BinaryOperator.BitwiseXor,
            TokenType.LeftShift => BinaryOperator.ArithmeticLeftShift,
            TokenType.RightShift => BinaryOperator.ArithmeticRightShift,
            TokenType.LogicalLeftShift => BinaryOperator.LogicalLeftShift,
            TokenType.LogicalRightShift => BinaryOperator.LogicalRightShift,
            TokenType.Assign => BinaryOperator.Assign,
            TokenType.In => BinaryOperator.In,
            TokenType.NotIn => BinaryOperator.NotIn,
            TokenType.Is => BinaryOperator.Is,
            TokenType.IsNot => BinaryOperator.IsNot,
            TokenType.Obeys => BinaryOperator.Obeys,
            TokenType.Disobeys => BinaryOperator.Disobeys,
            TokenType.NoneCoalesce => BinaryOperator.NoneCoalesce,

            _ => throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                message: $"Unknown binary operator: {tokenType}",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language)
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

            _ => throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                message: $"Unknown unary operator: {tokenType}",
                fileName: FileName,
                line: CurrentToken.Line,
                column: CurrentToken.Column,
                language: _language)
        };
    }

    #endregion

    #region Numeric Parsing

    /// <summary>
    /// Parse numeric literal value
    /// </summary>
    /// <remarks>
    /// Types without direct C# equivalents (f128, d32, d64, d128, Integer, Decimal) are stored
    /// as raw strings in the AST. The semantic analyzer handles parsing these using native libraries.
    /// </remarks>
    protected static object ParseNumericLiteral(Token token)
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

            // 256-bit integers have no .NET equivalent — defer the raw digits to the semantic
            // analyzer (BigInteger range check) and codegen (emits the LLVM i256 constant).
            TokenType.S256Literal => CleanNumericSuffix(text: text, suffix: "s256"),
            TokenType.U256Literal => CleanNumericSuffix(text: text, suffix: "u256"),

            // Fixed-width floats with C# equivalents - parse immediately
            TokenType.F16Literal => ParseTypedFloat<Half>(text: text, suffix: "f16"),
            TokenType.F32Literal => ParseTypedFloat<float>(text: text, suffix: "f32"),
            TokenType.F64Literal => ParseTypedFloat<double>(text: text, suffix: "f64"),

            // Deferred types - store raw string for semantic analyzer to parse with native libraries
            // f128: IEEE binary128, requires LibBF
            // d32/d64/d128: IEEE decimal floating-point, requires decNumber
            // Integer: arbitrary precision, requires LibTomMath
            // Decimal: arbitrary precision, requires decNumber
            TokenType.F128Literal => CleanNumericSuffix(text: text, suffix: "f128"),
            TokenType.D32Literal => CleanNumericSuffix(text: text, suffix: "d32"),
            TokenType.D64Literal => CleanNumericSuffix(text: text, suffix: "d64"),
            TokenType.D128Literal => CleanNumericSuffix(text: text, suffix: "d128"),
            TokenType.IntegerLiteral => text,
            TokenType.DecimalLiteral => text,
            TokenType.UndecidedInteger => text,
            TokenType.UndecidedDecimal => text,

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
            ? text[..^suffix.Length]
            : text;
        return cleaned.Replace(oldValue: "_", newValue: "");
    }

    private static T ParseTypedInteger<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text[..^suffix.Length]
            : text;
        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    private static T ParseTypedFloat<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text[..^suffix.Length]
            : text;
        if (typeof(T) == typeof(Half))
        {
            // Half is not directly supported, use float as approximation
            return (T)(object)(float)double.Parse(s: cleanText);
        }

        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    #endregion

    #region Warnings

    /// <summary>
    /// Add a build warning to the list
    /// </summary>
    protected void AddWarning(string message, Token token, string warningCode,
        WarningSeverity severity = WarningSeverity.Warning)
    {
        _warnings.Add(item: new BuildWarning(message: message,
            line: token.Line,
            column: token.Column,
            severity: severity,
            warningCode: warningCode,
            fileName: FileName));
    }

    /// <summary>
    /// Get all warnings collected during parsing
    /// </summary>
    public List<BuildWarning> GetWarnings()
    {
        return _warnings.ToList();
    }

    /// <summary>
    /// Check for unnecessary closing brace (for indentation-based syntax)
    /// </summary>
    protected void CheckUnnecessaryBrace()
    {
        if (CurrentToken.Type == TokenType.RightBrace)
        {
            AddWarning(
                message:
                "Unnecessary closing brace detected. This language uses indentation-based scoping, not braces.",
                token: CurrentToken,
                warningCode: WarningCodes.UnnecessaryBraces,
                severity: WarningSeverity.StyleViolation);
        }
    }

    #endregion
}
