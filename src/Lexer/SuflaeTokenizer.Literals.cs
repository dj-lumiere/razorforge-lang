namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing string and character literal scanning methods for the Suflae tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Suflae has a simplified literal system compared to RazorForge:
/// </para>
/// <list type="bullet">
///   <item><description>Basic strings: "hello" (UTF-32/letter by default)</description></item>
///   <item><description>Raw strings: r"no \escape"</description></item>
///   <item><description>Formatted strings: f"value is {x}"</description></item>
///   <item><description>Byte strings: b"bytes"</description></item>
///   <item><description>Character literals: 'a' (letter/UTF-32, emits LetterLiteral)</description></item>
///   <item><description>Byte character literals: b'x' (8-bit, emits ByteLetterLiteral)</description></item>
/// </list>
/// <para>
/// Unlike RazorForge, Suflae does not support explicit letter prefixes (letter8'x', letter16'x', letter32'x').
/// </para>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region String Literals

    /// <summary>
    /// Scans a basic string literal (without prefix).
    /// </summary>
    /// <remarks>
    /// In Suflae, unprefixed strings are letter (UTF-32) strings.
    /// </remarks>
    /// <exception cref="LexerException">Thrown when the string is unterminated.</exception>
    private void ScanString()
    {
        ScanStringLiteral(isRaw: false, isFormatted: false, TokenType.TextLiteral, bitWidth: 32);
    }

    /// <summary>
    /// Attempts to parse a text prefix (r, f, rf, b, br, bf, brf) followed by a quoted string.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a valid text prefix was found; <c>false</c> otherwise.
    /// </returns>
    private bool TryParseTextPrefix()
    {
        int startPos = _position - 1;
        int originalPos = _position;
        int originalCol = _column;

        char firstChar = _source[startPos];
        string prefix = firstChar.ToString();

        // Greedy match: build the longest valid prefix
        while (!IsAtEnd() && char.IsLetterOrDigit(Peek()))
        {
            string testPrefix = prefix + Peek();
            if (_textPrefixes.Any(p => p.StartsWith(testPrefix)))
                prefix += Advance();
            else
                break;
        }

        // Check if we found a valid prefix
        if (!_textPrefixToTokenType.ContainsKey(prefix))
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        // Must be followed by a quote
        if (Peek() != '"')
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        Advance(); // consume opening quote

        TokenType tokenType = _textPrefixToTokenType[prefix];
        bool isRaw = prefix.Contains('r');
        bool isFormatted = prefix.Contains('f');

        // Byte strings are 8-bit, regular strings are 32-bit
        int bitWidth = prefix.Contains('b') ? 8 : 32;

        ScanStringLiteral(isRaw, isFormatted, tokenType, bitWidth);
        return true;
    }

    /// <summary>
    /// Scans a string literal with the specified properties.
    /// </summary>
    /// <param name="isRaw">If <c>true</c>, escape sequences are not processed.</param>
    /// <param name="isFormatted">If <c>true</c>, the string supports interpolation.</param>
    /// <param name="tokenType">The token type to emit.</param>
    /// <param name="bitWidth">Character bit width for Unicode escapes (8 or 32).</param>
    /// <exception cref="LexerException">Thrown when the string is unterminated or contains invalid escapes.</exception>
    private void ScanStringLiteral(bool isRaw, bool isFormatted, TokenType tokenType, int bitWidth = 32)
    {
        int startLine = _line;
        int startColumn = _column;
        var content = new System.Text.StringBuilder();

        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\n')
            {
                content.Append('\n');
                Advance();
            }
            else if (!isRaw && Peek() == '\\')
            {
                int escapeStart = _position;
                Advance(); // consume backslash
                ScanEscapeSequence(bitWidth);
                content.Append(ParseEscapeSequence(escapeStart, bitWidth));
            }
            else
            {
                content.Append(Advance());
            }
        }

        if (IsAtEnd())
            throw new LexerException($"Unterminated text starting at line {startLine}, column {startColumn}");

        Advance(); // consume closing quote
        AddToken(tokenType, content.ToString());
    }

    #endregion

    #region Character Literals

    /// <summary>
    /// Scans a basic character literal (single-quoted).
    /// </summary>
    /// <remarks>
    /// In Suflae, unprefixed character literals 'x' are letter (UTF-32) characters.
    /// </remarks>
    /// <exception cref="LexerException">Thrown when the character literal is unterminated.</exception>
    private void ScanLetter()
    {
        if (IsAtEnd())
            throw new LexerException($"Unterminated character literal at line {_line}");

        char value;
        if (Peek() == '\\')
        {
            Advance(); // consume backslash
            value = EscapeCharacter(Advance());
        }
        else
        {
            value = Advance();
        }

        if (Peek() != '\'')
            throw new LexerException($"Unterminated character literal at line {_line}");

        Advance(); // consume closing quote
        AddToken(TokenType.LetterLiteral, value.ToString());
    }

    /// <summary>
    /// Attempts to parse a b'x' byte character literal.
    /// </summary>
    /// <returns>
    /// <c>true</c> if byte character literal found; <c>false</c> otherwise.
    /// </returns>
    /// <remarks>
    /// This handles the case where 'b' starts a token - it could be:
    /// <list type="bullet">
    ///   <item><description>b"string" - byte string (handled by TryParseTextPrefix)</description></item>
    ///   <item><description>b'x' - byte character literal (8-bit)</description></item>
    ///   <item><description>identifier starting with 'b'</description></item>
    /// </list>
    /// This method specifically checks for b'x' syntax and emits ByteLetterLiteral.
    /// </remarks>
    private bool TryParseByteLiteralPrefix()
    {
        // We already consumed 'b', check if followed by single quote
        if (Peek() != '\'')
        {
            // Not a byte character literal
            return false;
        }

        Advance(); // consume opening quote
        ScanLetterLiteral(TokenType.ByteLetterLiteral, 8);
        return true;
    }

    /// <summary>
    /// Scans a character literal with the specified token type and bit width.
    /// </summary>
    /// <param name="tokenType">The token type to emit.</param>
    /// <param name="bitWidth">The bit width for Unicode escape validation.</param>
    /// <exception cref="LexerException">Thrown when the character literal is unterminated.</exception>
    private void ScanLetterLiteral(TokenType tokenType, int bitWidth = 32)
    {
        if (Peek() == '\\')
        {
            Advance(); // consume backslash
            ScanEscapeSequence(bitWidth);
        }
        else
        {
            Advance(); // consume the character
        }

        if (!Match('\''))
            throw new LexerException($"Unterminated character literal at line {_line}");

        AddToken(tokenType);
    }

    #endregion

    #region Escape Sequences

    /// <summary>
    /// Scans and validates an escape sequence.
    /// </summary>
    /// <param name="bitWidth">The bit width for Unicode escape validation.</param>
    /// <exception cref="LexerException">Thrown when the escape sequence is invalid.</exception>
    private void ScanEscapeSequence(int bitWidth = 32)
    {
        if (IsAtEnd())
            throw new LexerException($"Unterminated escape sequence at line {_line}");

        char escapeChar = Peek();
        switch (escapeChar)
        {
            case 'n' or 't' or 'r' or '\\' or '"' or '\'' or '0':
                Advance();
                break;
            case 'u':
                Advance(); // consume 'u'
                ScanUnicodeEscape(bitWidth);
                break;
            default:
                throw new LexerException($"Invalid escape sequence '\\{escapeChar}' at line {_line}");
        }
    }

    /// <summary>
    /// Scans and validates a Unicode escape sequence.
    /// </summary>
    /// <param name="bitWidth">Determines hex digits required: 8-bit=2, 32-bit=8.</param>
    /// <exception cref="LexerException">Thrown when insufficient hex digits are provided.</exception>
    private void ScanUnicodeEscape(int bitWidth)
    {
        int hexDigits = bitWidth switch
        {
            8 => 2,   // \uXX
            16 => 4,  // \uXXXX
            32 => 8,  // \uXXXXXXXX
            _ => 4
        };

        for (int i = 0; i < hexDigits; i += 1)
        {
            if (!IsHexDigit(Peek()))
                throw new LexerException(
                    $"Invalid Unicode escape sequence at line {_line}: expected {hexDigits} hex digits for {bitWidth}-bit character");
            Advance();
        }
    }

    /// <summary>
    /// Parses an escape sequence and returns the actual character value.
    /// </summary>
    /// <param name="escapeStart">Position in source where the backslash is located.</param>
    /// <param name="bitWidth">The bit width for Unicode escape parsing.</param>
    /// <returns>The character represented by the escape sequence.</returns>
    /// <exception cref="LexerException">Thrown when a Unicode value exceeds the bit width range.</exception>
    private char ParseEscapeSequence(int escapeStart, int bitWidth = 32)
    {
        char c = _source[escapeStart + 1]; // character after backslash

        if (c != 'u')
        {
            return EscapeCharacter(c);
        }

        int hexDigits = bitWidth switch { 8 => 2, 16 => 4, 32 => 8, _ => 4 };
        string hexStr = _source.Substring(escapeStart + 2, hexDigits);
        int codePoint = Convert.ToInt32(hexStr, 16);

        if (bitWidth == 8 && codePoint > 0xFF)
            throw new LexerException($"Unicode escape value {codePoint:X} exceeds 8-bit range at line {_line}");

        return (char)codePoint;
    }

    /// <summary>
    /// Converts a simple escape character to its actual value.
    /// </summary>
    /// <param name="c">The character following the backslash.</param>
    /// <returns>The actual character represented by the escape.</returns>
    private static char EscapeCharacter(char c) => c switch
    {
        'n' => '\n',
        't' => '\t',
        'r' => '\r',
        '\\' => '\\',
        '"' => '"',
        '\'' => '\'',
        '0' => '\0',
        _ => c
    };

    #endregion
}
