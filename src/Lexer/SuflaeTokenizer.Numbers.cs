namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing numeric literal scanning methods for the Suflae tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Suflae has a simplified numeric type system compared to RazorForge.
/// Only 32-bit and larger types are directly supported:
/// </para>
/// <list type="bullet">
///   <item><description>Integers: s32, s64, s128, saddr, u32, u64, u128, uaddr</description></item>
///   <item><description>Floats: f32, f64</description></item>
///   <item><description>Decimals: d128</description></item>
///   <item><description>Memory sizes: b, kb, mb, gb, tb, pb (and binary variants)</description></item>
///   <item><description>Durations: s, ms, us, ns, m, h, d, w</description></item>
/// </list>
/// <para>
/// The 8-bit and 16-bit types (s8, s16, u8, u16, f16) are not available in Suflae.
/// </para>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Decimal Numbers

    /// <summary>
    /// Scans a decimal numeric literal, handling integers, floats, and suffixed numbers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In Suflae, unsuffixed integers default to Integer type and unsuffixed
    /// floating-point numbers default to Decimal type (arbitrary precision).
    /// </para>
    /// <para>
    /// The 'd' suffix explicitly marks a Decimal literal.
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when an unknown suffix is encountered.</exception>
    private void ScanNumber()
    {
        // Consume digits and underscores
        while (char.IsDigit(Peek()) || Peek() == '_')
            Advance();

        bool isFloat = false;

        // Check for decimal point followed by digit
        if (Peek() == '.' && char.IsDigit(Peek(1)))
        {
            isFloat = true;
            Advance(); // consume '.'

            // Consume fractional digits
            while (char.IsDigit(Peek()) || Peek() == '_')
                Advance();
        }

        // Check for scientific notation
        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            Advance(); // consume 'e' or 'E'

            // Optional sign
            if (Peek() == '+' || Peek() == '-')
                Advance();

            // Exponent digits
            while (char.IsDigit(Peek()))
                Advance();
        }

        // Check for type suffix
        if (char.IsLetter(Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(Peek()))
                Advance();

            string suffix = _source.Substring(suffixStart, _position - suffixStart);

            if (_numericSuffixToTokenType.TryGetValue(suffix, out TokenType numericType))
                AddToken(numericType);
            else if (_memorySuffixToTokenType.TryGetValue(suffix, out TokenType memoryType))
                AddToken(memoryType);
            else if (_durationSuffixToTokenType.TryGetValue(suffix, out TokenType durationToken))
                AddToken(durationToken);
            else if (suffix == "d")
                AddToken(TokenType.Decimal); // explicit Decimal suffix
            else
                throw new LexerException($"Unknown suffix '{suffix}' at line {_line}");
        }
        else
        {
            // Suflae defaults: Integer for whole numbers, Decimal for floating point
            AddToken(isFloat ? TokenType.Decimal : TokenType.Integer);
        }
    }

    #endregion

    #region Prefixed Numbers

    /// <summary>
    /// Scans a prefixed numeric literal (hexadecimal or binary).
    /// </summary>
    /// <param name="isHex">
    /// <c>true</c> for hexadecimal (0x); <c>false</c> for binary (0b).
    /// </param>
    /// <remarks>
    /// <para>
    /// Prefixed numbers in Suflae can have numeric type suffixes (s32, u64, etc.)
    /// but default to Integer if no suffix is provided.
    /// </para>
    /// <para>
    /// Only numeric suffixes are valid for prefixed literals; memory and duration
    /// suffixes are not allowed.
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when an unknown or invalid suffix is encountered.</exception>
    private void ScanPrefixedNumber(bool isHex)
    {
        // Consume valid digits and underscores
        if (isHex)
        {
            while (IsHexDigit(Peek()) || Peek() == '_')
                Advance();
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
                Advance();
        }

        // Check for type suffix
        if (char.IsLetter(Peek()))
        {
            int suffixStart = _position;
            while (char.IsLetterOrDigit(Peek()))
                Advance();

            string suffix = _source.Substring(suffixStart, _position - suffixStart);

            if (_numericSuffixToTokenType.TryGetValue(suffix, out TokenType tokenType))
                AddToken(tokenType);
            else
            {
                string baseType = isHex ? "hex" : "binary";
                throw new LexerException($"Unknown {baseType} suffix '{suffix}' at line {_line}");
            }
        }
        else
        {
            // Default to Integer
            AddToken(TokenType.Integer);
        }
    }

    #endregion
}
