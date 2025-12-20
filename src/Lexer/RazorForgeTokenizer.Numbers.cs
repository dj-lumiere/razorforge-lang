namespace Compilers.RazorForge.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing numeric literal scanning methods for the RazorForge tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// This file handles all numeric literal scanning including:
/// <list type="bullet">
///   <item><description>Decimal integers (123, 1_000_000)</description></item>
///   <item><description>Floating-point numbers (3.14, 1.5e10)</description></item>
///   <item><description>Hexadecimal literals (0xFF, 0x1234_5678)</description></item>
///   <item><description>Binary literals (0b1010, 0b1111_0000)</description></item>
///   <item><description>Type suffixes (s32, u64, f32, etc.)</description></item>
///   <item><description>Memory size suffixes (kb, mib, gb, etc.)</description></item>
///   <item><description>Duration suffixes (s, ms, h, etc.)</description></item>
/// </list>
/// </para>
/// <para>
/// Underscores can be used as digit separators for readability in any numeric literal.
/// </para>
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region Decimal Numbers

    /// <summary>
    /// Scans a decimal numeric literal, handling integers, floats, and suffixed numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called after the first digit has been consumed by <see cref="ScanToken"/>.
    /// It continues scanning to build a complete numeric literal, which may include:
    /// <list type="bullet">
    ///   <item><description>Additional digits and underscores</description></item>
    ///   <item><description>Decimal point followed by fractional digits</description></item>
    ///   <item><description>Scientific notation (e or E with optional sign)</description></item>
    ///   <item><description>Type suffix</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// If no suffix is provided, the literal defaults to:
    /// <list type="bullet">
    ///   <item><description>Integer for whole numbers</description></item>
    ///   <item><description>Decimal for floating-point numbers</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when an unknown suffix is encountered.</exception>
    private void ScanNumber()
    {
        // Consume digits and underscores
        while (char.IsDigit(c: Peek()) || Peek() == '_')
        {
            Advance();
        }

        bool isFloat = false;

        // Check for decimal point followed by digit
        if (Peek() == '.' && char.IsDigit(c: Peek(offset: 1)))
        {
            isFloat = true;
            Advance(); // consume '.'

            // Consume fractional digits
            while (char.IsDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }

        // Check for scientific notation
        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            Advance(); // consume 'e' or 'E'

            // Optional sign
            if (Peek() == '+' || Peek() == '-')
            {
                Advance();
            }

            // Exponent digits
            while (char.IsDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

            if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                    value: out TokenType numericType))
            {
                AddToken(type: numericType);
            }
            else if (_memorySuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType memoryType))
            {
                AddToken(type: memoryType);
            }
            else if (_durationSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType durationToken))
            {
                AddToken(type: durationToken);
            }
            else
            {
                throw new LexerException(message: $"Unknown suffix '{suffix}' at line {_line}");
            }
        }
        else
        {
            // No suffix - use default type based on presence of decimal point
            AddToken(type: isFloat
                ? TokenType.Decimal
                : TokenType.Integer);
        }
    }

    #endregion

    #region Prefixed Numbers

    /// <summary>
    /// Scans a prefixed numeric literal (hexadecimal or binary).
    /// </summary>
    /// <param name="isHex">
    /// <c>true</c> for hexadecimal (0x prefix); <c>false</c> for binary (0b prefix).
    /// </param>
    /// <remarks>
    /// <para>
    /// This method is called after the prefix (0x or 0b) has been consumed.
    /// It scans the remaining digits and optional type suffix.
    /// </para>
    /// <para>
    /// Hexadecimal literals accept digits 0-9 and letters a-f (case insensitive).
    /// Binary literals accept only 0 and 1.
    /// </para>
    /// <para>
    /// Underscores can be used as digit separators in both formats.
    /// </para>
    /// <para>
    /// Only numeric type suffixes (s32, u64, etc.) are valid for prefixed literals.
    /// Memory and duration suffixes are not allowed.
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when an unknown or invalid suffix is encountered.</exception>
    private void ScanPrefixedNumber(bool isHex)
    {
        // Consume valid digits and underscores
        if (isHex)
        {
            while (IsHexDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
            {
                Advance();
            }
        }

        // Check for type suffix
        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                _source.Substring(startIndex: suffixStart, length: _position - suffixStart);

            if (_numericSuffixToTokenType.TryGetValue(key: suffix, value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                string baseType = isHex
                    ? "hex"
                    : "binary";
                throw new LexerException(
                    message: $"Unknown {baseType} suffix '{suffix}' at line {_line}");
            }
        }
        else
        {
            // No suffix - default to Integer
            AddToken(type: TokenType.Integer);
        }
    }

    #endregion
}
