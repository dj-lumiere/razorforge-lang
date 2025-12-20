namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing character navigation and token management methods for the Suflae tokenizer.
/// </summary>
public partial class SuflaeTokenizer
{
    #region Character Navigation

    /// <summary>
    /// Consumes and returns the current character, advancing the position.
    /// </summary>
    /// <returns>
    /// The character at the current position, or '\0' if at end of source.
    /// </returns>
    /// <remarks>
    /// This method updates line and column tracking. When a newline is consumed,
    /// the line number increments and column resets to 1.
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
    /// <param name="offset">How many characters ahead to look (default: 0 for current).</param>
    /// <returns>
    /// The character at the specified offset, or '\0' if beyond end of source.
    /// </returns>
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
    /// <returns><c>true</c> if at or beyond end of source; <c>false</c> otherwise.</returns>
    private bool IsAtEnd()
    {
        return _position >= _source.Length;
    }

    /// <summary>
    /// Peeks at the word starting at the current position without consuming it.
    /// </summary>
    /// <returns>
    /// The word (sequence of letters, digits, underscores) at current position.
    /// </returns>
    /// <remarks>
    /// Used for multi-character token detection like @intrinsic and @native.
    /// </remarks>
    private string PeekWord()
    {
        var word = new System.Text.StringBuilder();
        int offset = 0;

        while (!IsAtEnd() && char.IsLetterOrDigit(c: Peek(offset: offset)))
        {
            word.Append(value: Peek(offset: offset));
            offset += 1;
        }

        return word.ToString();
    }

    #endregion

    #region Token Management

    /// <summary>
    /// Adds a token of the specified type using text from token start to current position.
    /// </summary>
    /// <param name="type">The type of token to create.</param>
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
    /// <returns><c>true</c> if the character is a letter or underscore.</returns>
    private static bool IsIdentifierStart(char c)
    {
        return char.IsLetter(c: c) || c == '_';
    }

    /// <summary>
    /// Determines whether a character can be part of an identifier.
    /// </summary>
    /// <param name="c">The character to check.</param>
    /// <returns><c>true</c> if the character is a letter, digit, or underscore.</returns>
    private static bool IsIdentifierPart(char c)
    {
        return char.IsLetterOrDigit(c: c) || c == '_';
    }

    /// <summary>
    /// Determines whether a character is a valid hexadecimal digit.
    /// </summary>
    /// <param name="c">The character to check.</param>
    /// <returns><c>true</c> if the character is 0-9, a-f, or A-F.</returns>
    private static bool IsHexDigit(char c)
    {
        return char.IsDigit(c: c) || c is >= 'a' and <= 'f' or >= 'A' and <= 'F';
    }

    #endregion
}
