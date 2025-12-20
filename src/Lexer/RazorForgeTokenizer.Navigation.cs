using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Lexer;

/// <summary>
/// Partial class containing character navigation methods for the RazorForge tokenizer.
/// </summary>
/// <remarks>
/// These methods provide low-level character access and position tracking
/// for the tokenization process. They handle cursor movement, lookahead,
/// and end-of-input detection.
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region Character Navigation

    /// <summary>
    /// Consumes and returns the current character, advancing the position.
    /// </summary>
    /// <returns>
    /// The character at the current position, or '\0' if at end of source.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method advances the position pointer and updates line/column tracking.
    /// When a newline character is consumed, the line number increments and
    /// the column resets to 1.
    /// </para>
    /// <para>
    /// If the tokenizer is already at the end of the source, this method
    /// returns '\0' without advancing the position.
    /// </para>
    /// </remarks>
    private char Advance()
    {
        if (IsAtEnd())
        {
            return '\0';
        }

        char c = _source[index: _position];
        _position += 1;

        if (c == '\n')
        {
            _line += 1;
            _column = 1;
        }
        else
        {
            _column += 1;
        }

        return c;
    }

    /// <summary>
    /// Checks if the current character matches the expected character and consumes it if so.
    /// </summary>
    /// <param name="expected">The character to match against.</param>
    /// <returns>
    /// <c>true</c> if the current character matches and was consumed;
    /// <c>false</c> if at end of source or the character doesn't match.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method is useful for recognizing multi-character tokens like
    /// '==' (equality) or '->' (arrow). It combines a check and consume
    /// operation in a single call.
    /// </para>
    /// <para>
    /// Unlike <see cref="Advance"/>, this method only updates position and
    /// column tracking, not line tracking, as it's typically used for
    /// non-newline characters in operator sequences.
    /// </para>
    /// </remarks>
    private bool Match(char expected)
    {
        if (IsAtEnd())
        {
            return false;
        }

        if (_source[index: _position] != expected)
        {
            return false;
        }

        _position += 1;
        _column += 1;
        return true;
    }

    /// <summary>
    /// Peeks at a character ahead without consuming it.
    /// </summary>
    /// <param name="offset">
    /// How many characters ahead to look. Default is 0 (current character).
    /// </param>
    /// <returns>
    /// The character at the specified offset from the current position,
    /// or '\0' if the offset is beyond the end of source.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method allows lookahead without modifying the tokenizer state.
    /// It's essential for disambiguating tokens that share common prefixes,
    /// such as '.' vs '..' vs '...'.
    /// </para>
    /// <para>
    /// The offset parameter allows multi-character lookahead:
    /// <list type="bullet">
    ///   <item><description>Peek(0) - current character</description></item>
    ///   <item><description>Peek(1) - next character</description></item>
    ///   <item><description>Peek(2) - character after next</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    private char Peek(int offset = 0)
    {
        int pos = _position + offset;
        return pos >= _source.Length
            ? '\0'
            : _source[index: pos];
    }

    /// <summary>
    /// Checks if the tokenizer has reached the end of the source text.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the current position is at or beyond the end of source;
    /// <c>false</c> otherwise.
    /// </returns>
    /// <remarks>
    /// This method is used throughout the tokenization process to detect
    /// when to stop scanning and to handle edge cases like unterminated
    /// strings at end of file.
    /// </remarks>
    private bool IsAtEnd()
    {
        return _position >= _source.Length;
    }

    /// <summary>
    /// Peeks at the word starting at the current position without consuming it.
    /// </summary>
    /// <returns>
    /// The complete word (sequence of letters, digits, and underscores) starting
    /// at the current position, or an empty string if no word starts there.
    /// </returns>
    /// <remarks>
    /// <para>
    /// This method is used for multi-character token detection, particularly
    /// for recognizing @intrinsic and @native tokens.
    /// </para>
    /// <para>
    /// The method only performs lookahead and does not modify the tokenizer state.
    /// </para>
    /// </remarks>
    private string PeekWord()
    {
        int offset = 0;
        var sb = new System.Text.StringBuilder();

        while (true)
        {
            char c = Peek(offset: offset);
            if (char.IsLetterOrDigit(c: c) || c == '_')
            {
                sb.Append(value: c);
                offset += 1;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    #endregion

    #region Token Management

    /// <summary>
    /// Adds a token of the specified type using text from the token start to current position.
    /// </summary>
    /// <param name="type">The type of token to create.</param>
    /// <remarks>
    /// This method extracts the token text from the source using the stored
    /// token start position and the current position. It then delegates to
    /// the overload that accepts explicit text.
    /// </remarks>
    private void AddToken(TokenType type)
    {
        string text = _source.Substring(startIndex: _tokenStart, length: _position - _tokenStart);
        AddToken(type: type, text: text);
    }

    /// <summary>
    /// Adds a token with the specified type and explicit text content.
    /// </summary>
    /// <param name="type">The type of token to create.</param>
    /// <param name="text">The text content of the token.</param>
    /// <remarks>
    /// <para>
    /// This method creates a new <see cref="Token"/> with the specified type and text,
    /// using the stored token start position for location information.
    /// </para>
    /// <para>
    /// The explicit text parameter is useful when the token text differs from the
    /// raw source text, such as for string literals with processed escape sequences.
    /// </para>
    /// </remarks>
    private void AddToken(TokenType type, string text)
    {
        _tokens.Add(item: new Token(Type: type,
            Text: text,
            Line: _tokenStartLine,
            Column: _tokenStartColumn,
            Position: _tokenStart));
    }

    #endregion

    #region Helper Predicates

    /// <summary>
    /// Determines whether a character can start an identifier.
    /// </summary>
    /// <param name="c">The character to check.</param>
    /// <returns>
    /// <c>true</c> if the character is a letter or underscore; <c>false</c> otherwise.
    /// </returns>
    /// <remarks>
    /// In RazorForge, identifiers must start with a letter or underscore.
    /// Digits are not allowed as the first character.
    /// </remarks>
    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c: c) || c == '_';
    }

    /// <summary>
    /// Determines whether a character can be part of an identifier (after the first character).
    /// </summary>
    /// <param name="c">The character to check.</param>
    /// <returns>
    /// <c>true</c> if the character is a letter, digit, or underscore; <c>false</c> otherwise.
    /// </returns>
    /// <remarks>
    /// After the first character, identifiers can contain letters, digits, and underscores.
    /// </remarks>
    private static bool IsIdentifierPart(char c)
    {
        return char.IsLetterOrDigit(c: c) || c == '_';
    }

    /// <summary>
    /// Determines whether a character is a valid hexadecimal digit.
    /// </summary>
    /// <param name="c">The character to check.</param>
    /// <returns>
    /// <c>true</c> if the character is 0-9, a-f, or A-F; <c>false</c> otherwise.
    /// </returns>
    /// <remarks>
    /// Used for parsing hexadecimal literals (0x...) and Unicode escape sequences (\uXXXX).
    /// </remarks>
    private static bool IsHexDigit(char c)
    {
        return char.IsDigit(c: c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    #endregion
}
