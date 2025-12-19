namespace Compilers.RazorForge.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing string and character literal scanning methods for the RazorForge tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// This file handles all text-based literal scanning including:
/// <list type="bullet">
///   <item><description>Basic string literals ("hello")</description></item>
///   <item><description>Prefixed strings (r"raw", f"formatted", t8"utf8", t16"utf16")</description></item>
///   <item><description>Character literals ('a' - 32-bit UTF-32 by default, emits LetterLiteral)</description></item>
///   <item><description>Prefixed character literals (l8'x' - 8-bit, l16'y' - 16-bit)</description></item>
///   <item><description>Escape sequence processing (\n, \t, \uXXXX, etc.)</description></item>
/// </list>
/// </para>
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region String Literals

    /// <summary>
    /// Scans a basic string literal (without prefix).
    /// </summary>
    /// <remarks>
    /// <para>
    /// In RazorForge, unprefixed strings are treated as UTF-8 text (Text8).
    /// The opening quote has already been consumed when this method is called.
    /// </para>
    /// <para>
    /// Escape sequences are processed by default. For raw strings without
    /// escape processing, use the r"..." prefix.
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when the string is unterminated.</exception>
    private void ScanString()
    {
        ScanTextLiteral(isRaw: false, isFormatted: false, TokenType.Text8Literal, bitWidth: 8);
    }

    /// <summary>
    /// Attempts to parse a text prefix (r, f, t8, t16, etc.) followed by a quoted string.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a valid text prefix was found and the string was processed;
    /// <c>false</c> if no valid prefix was found (position is restored).
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method uses greedy matching to find the longest valid prefix.
    /// For example, "t16rf" would match before "t16r" or "t16".
    /// </para>
    /// <para>
    /// If a valid prefix is found but not followed by a quote, the method
    /// restores the position and returns false, allowing the caller to
    /// treat the characters as an identifier.
    /// </para>
    /// <para>
    /// Prefix meanings:
    /// <list type="bullet">
    ///   <item><description>r - Raw (no escape processing)</description></item>
    ///   <item><description>f - Formatted (string interpolation)</description></item>
    ///   <item><description>t8 - UTF-8 encoding</description></item>
    ///   <item><description>t16 - UTF-16 encoding</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private bool TryParseTextPrefix()
    {
        int startPos = _position - 1;
        int originalPos = _position;
        int originalCol = _column;

        char firstChar = _source[startPos];
        string prefix = firstChar.ToString();

        // Greedy match: try to build the longest valid prefix
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

        // Determine bit width from prefix
        int bitWidth = 32; // default
        if (prefix.Contains("t8")) bitWidth = 8;
        else if (prefix.Contains("t16")) bitWidth = 16;

        ScanTextLiteral(isRaw, isFormatted, tokenType, bitWidth);
        return true;
    }

    /// <summary>
    /// Scans a string literal with the specified properties.
    /// </summary>
    /// <param name="isRaw">If <c>true</c>, escape sequences are not processed.</param>
    /// <param name="isFormatted">If <c>true</c>, the string supports interpolation (future use).</param>
    /// <param name="tokenType">The token type to emit for this string.</param>
    /// <param name="bitWidth">The character bit width for Unicode escapes (8, 16, or 32).</param>
    /// <remarks>
    /// <para>
    /// This method scans characters until a closing quote is found, processing
    /// escape sequences unless isRaw is true. The content is built into a
    /// StringBuilder with escape sequences converted to their actual characters.
    /// </para>
    /// <para>
    /// Multiline strings are supported - embedded newlines are preserved in the
    /// string content.
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when the string is unterminated or contains invalid escapes.</exception>
    private void ScanTextLiteral(bool isRaw, bool isFormatted, TokenType tokenType, int bitWidth = 32)
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
    /// <para>
    /// Character literals in RazorForge are enclosed in single quotes and contain
    /// a single character (or escape sequence). The opening quote has already
    /// been consumed when this method is called.
    /// </para>
    /// <para>
    /// Examples: 'a', '\n', '\u0041'
    /// </para>
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
    /// Attempts to parse a letter prefix (l8, l16) followed by a character literal.
    /// </summary>
    /// <returns>
    /// <c>true</c> if a valid letter prefix was found and the character was processed;
    /// <c>false</c> if no valid prefix was found (position is restored).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Letter prefixes specify the bit width of the character:
    /// <list type="bullet">
    ///   <item><description>l8'x' - 8-bit character (ASCII/Latin-1)</description></item>
    ///   <item><description>l16'x' - 16-bit character (BMP Unicode)</description></item>
    ///   <item><description>'x' (no prefix) - 32-bit character (full Unicode, default)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// The prefix affects the number of hex digits required for \u escapes:
    /// l8 requires 2, l16 requires 4, unprefixed requires 8.
    /// </para>
    /// </remarks>
    private bool TryParseLetterPrefix()
    {
        int startPos = _position - 1;
        int originalPos = _position;
        int originalCol = _column;

        string prefix = _source[startPos].ToString();

        // Build the complete prefix (l8 or l16)
        while (!IsAtEnd() && char.IsLetterOrDigit(Peek()))
            prefix += Advance();

        // Must be followed by a quote
        if (Peek() != '\'')
        {
            _position = originalPos;
            _column = originalCol;
            return false;
        }

        switch (prefix)
        {
            case "l8":
                Advance(); // consume opening quote
                ScanCharLiteral(TokenType.Letter8Literal, 8);
                return true;
            case "l16":
                Advance();
                ScanCharLiteral(TokenType.Letter16Literal, 16);
                return true;
            default:
                // No l32 - unprefixed 'x' is the 32-bit default
                _position = originalPos;
                _column = originalCol;
                return false;
        }
    }

    /// <summary>
    /// Scans a character literal with the specified token type and bit width.
    /// </summary>
    /// <param name="tokenType">The token type to emit.</param>
    /// <param name="bitWidth">The bit width for Unicode escape validation (8, 16, or 32).</param>
    /// <remarks>
    /// The opening quote has already been consumed. This method scans the character
    /// content (including escape sequences) and the closing quote.
    /// </remarks>
    /// <exception cref="LexerException">Thrown when the character literal is unterminated.</exception>
    private void ScanCharLiteral(TokenType tokenType, int bitWidth = 32)
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
    /// <param name="bitWidth">The bit width for Unicode escape validation (8, 16, or 32).</param>
    /// <remarks>
    /// <para>
    /// This method validates the escape sequence without converting it.
    /// The actual conversion is done by <see cref="ParseEscapeSequence"/>.
    /// </para>
    /// <para>
    /// Supported escape sequences:
    /// <list type="bullet">
    ///   <item><description>\n - newline</description></item>
    ///   <item><description>\t - tab</description></item>
    ///   <item><description>\r - carriage return</description></item>
    ///   <item><description>\\ - backslash</description></item>
    ///   <item><description>\" - double quote</description></item>
    ///   <item><description>\' - single quote</description></item>
    ///   <item><description>\0 - null character</description></item>
    ///   <item><description>\uXX, \uXXXX, \uXXXXXXXX - Unicode (based on bit width)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when the escape sequence is invalid or unterminated.</exception>
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
    /// <param name="bitWidth">The bit width determining the number of hex digits required.</param>
    /// <remarks>
    /// <para>
    /// The number of hex digits depends on the bit width:
    /// <list type="bullet">
    ///   <item><description>8-bit: 2 digits (\uXX)</description></item>
    ///   <item><description>16-bit: 4 digits (\uXXXX)</description></item>
    ///   <item><description>32-bit: 8 digits (\uXXXXXXXX)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">Thrown when insufficient hex digits are provided.</exception>
    private void ScanUnicodeEscape(int bitWidth)
    {
        int hexDigits = bitWidth switch
        {
            8 => 2,
            16 => 4,
            32 => 8,
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
    /// <param name="escapeStart">The position in source where the backslash is located.</param>
    /// <param name="bitWidth">The bit width for Unicode escape parsing (8, 16, or 32).</param>
    /// <returns>The character represented by the escape sequence.</returns>
    /// <remarks>
    /// This method reads from the source string at the specified position to
    /// parse and convert the escape sequence to its actual character value.
    /// </remarks>
    /// <exception cref="LexerException">Thrown when a Unicode value exceeds the bit width range.</exception>
    private char ParseEscapeSequence(int escapeStart, int bitWidth = 32)
    {
        char c = _source[escapeStart + 1]; // character after backslash

        if (c == 'u')
        {
            int hexDigits = bitWidth switch { 8 => 2, 16 => 4, 32 => 8, _ => 4 };
            string hexStr = _source.Substring(escapeStart + 2, hexDigits);
            int codePoint = Convert.ToInt32(hexStr, 16);

            if (bitWidth == 8 && codePoint > 0xFF)
                throw new LexerException($"Unicode escape value {codePoint:X} exceeds 8-bit range at line {_line}");
            else if (bitWidth == 16 && codePoint > 0xFFFF)
                throw new LexerException($"Unicode escape value {codePoint:X} exceeds 16-bit range at line {_line}");

            return (char)codePoint;
        }

        return EscapeCharacter(c);
    }

    /// <summary>
    /// Converts a simple escape character to its actual value.
    /// </summary>
    /// <param name="c">The character following the backslash.</param>
    /// <returns>The actual character represented by the escape sequence.</returns>
    /// <remarks>
    /// This method handles simple escapes like \n, \t, \r, etc.
    /// Unknown escape characters are returned unchanged.
    /// </remarks>
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
