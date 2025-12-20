namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing indentation and newline handling methods for the Suflae tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// Suflae uses Python-style significant indentation for block structure.
/// This file contains the logic for:
/// </para>
/// <list type="bullet">
///   <item><description>Measuring indentation at the start of each line</description></item>
///   <item><description>Emitting INDENT tokens when indentation increases</description></item>
///   <item><description>Emitting DEDENT tokens when indentation decreases</description></item>
///   <item><description>Detecting block-starting colons (colons at end of line)</description></item>
///   <item><description>Determining which newlines are significant</description></item>
/// </list>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Indentation Handling

    /// <summary>
    /// Handles indentation at the start of a line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is called at the beginning of each line (when column == 1).
    /// It measures the leading whitespace and compares it to the current
    /// indentation level to determine whether INDENT or DEDENT tokens are needed.
    /// </para>
    /// <para>
    /// Indentation rules:
    /// <list type="bullet">
    ///   <item><description>Each indentation level is 4 spaces</description></item>
    ///   <item><description>Tabs are counted as 4 spaces</description></item>
    ///   <item><description>Indentation must be a multiple of 4</description></item>
    ///   <item><description>After a block-starter colon, indentation must increase</description></item>
    ///   <item><description>Unexpected increases in indentation are errors</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">
    /// Thrown when indentation is misaligned (not a multiple of 4),
    /// when expected indent is missing, or when unexpected indent is found.
    /// </exception>
    private void HandleIndentation()
    {
        int spaces = 0;

        // Count leading whitespace
        while (Peek() == ' ' || Peek() == '\t')
        {
            if (Peek() == ' ')
            {
                spaces += 1;
            }
            else // Tab counts as 4 spaces
            {
                spaces += 4;
            }

            Advance();
        }

        // Skip empty lines (don't change indentation state)
        if (Peek() == '\n' || IsAtEnd())
        {
            return;
        }

        // Skip lines with only comments (don't change indentation state)
        if (Peek() == '#')
        {
            return;
        }

        int newIndentLevel = spaces / 4;

        // Validate indentation alignment
        if (spaces % 4 != 0)
        {
            throw new LexerException(
                message:
                $"Indentation error at line {_line}: expected multiple of 4 spaces, got {spaces} spaces.");
        }

        // Handle expected indent after block-starter colon
        if (_expectIndent)
        {
            if (newIndentLevel <= _currentIndentLevel)
            {
                throw new LexerException(message: $"Expected indent after ':' at line {_line}");
            }

            AddToken(type: TokenType.Indent, text: "");
            _currentIndentLevel = newIndentLevel;
            _expectIndent = false;
            return;
        }

        // Handle dedents when indentation decreases
        while (newIndentLevel < _currentIndentLevel)
        {
            AddToken(type: TokenType.Dedent, text: "");
            _currentIndentLevel -= 1;
        }

        // Unexpected increase in indentation
        if (newIndentLevel > _currentIndentLevel)
        {
            throw new LexerException(message: $"Unexpected indent at line {_line}");
        }
    }

    #endregion

    #region Newline Handling

    /// <summary>
    /// Handles a newline character, determining whether it's significant.
    /// </summary>
    /// <remarks>
    /// <para>
    /// In Suflae, newlines are significant when they terminate a statement.
    /// However, newlines are ignored after certain tokens that indicate
    /// continuation (like open parentheses or binary operators).
    /// </para>
    /// <para>
    /// This method also resets the <see cref="_hasTokenOnLine"/> flag for
    /// the next line.
    /// </para>
    /// </remarks>
    private void HandleNewline()
    {
        bool isSignificant = IsNewlineSignificant();

        if (isSignificant)
        {
            AddToken(type: TokenType.Newline, text: "\\n");
        }

        _hasTokenOnLine = false;
    }

    /// <summary>
    /// Determines whether the current newline is significant (terminates a statement).
    /// </summary>
    /// <returns>
    /// <c>true</c> if the newline should produce a token; <c>false</c> if it should be ignored.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A newline is NOT significant (ignored) when:
    /// <list type="bullet">
    ///   <item><description>The line was empty (no tokens yet)</description></item>
    ///   <item><description>The last token was an opening delimiter ((, [)</description></item>
    ///   <item><description>The last token was a comma or dot</description></item>
    ///   <item><description>The last token was a binary operator (+, -, *, /, etc.)</description></item>
    ///   <item><description>The last token was an arrow (->, =>)</description></item>
    ///   <item><description>The last token was already a newline</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// A newline after a colon IS significant (the colon starts a block).
    /// </para>
    /// </remarks>
    private bool IsNewlineSignificant()
    {
        // No tokens on line = not significant
        if (!_hasTokenOnLine)
        {
            return false;
        }

        // No tokens at all = not significant
        if (_tokens.Count == 0)
        {
            return false;
        }

        TokenType lastToken = _tokens[^1].Type;

        return lastToken switch
        {
            // Opening delimiters - continuation expected
            TokenType.LeftParen => false,
            TokenType.LeftBracket => false,

            // Separators - continuation expected
            TokenType.Comma => false,
            TokenType.Dot => false,

            // Binary operators - continuation expected
            TokenType.Plus => false,
            TokenType.Minus => false,
            TokenType.Star => false,
            TokenType.Slash => false,
            TokenType.Equal => false,
            TokenType.Less => false,
            TokenType.Greater => false,
            TokenType.And => false,
            TokenType.Or => false,

            // Arrows - continuation expected
            TokenType.Arrow => false,
            TokenType.FatArrow => false,

            // Already a newline - don't duplicate
            TokenType.Newline => false,

            // Colon at end of line is significant (block starter)
            TokenType.Colon => true,

            // Everything else is significant
            _ => true
        };
    }

    /// <summary>
    /// Determines whether a colon at the current position starts a block.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the colon is followed only by whitespace/comments until newline;
    /// <c>false</c> if there's more content on the line (type annotation).
    /// </returns>
    /// <remarks>
    /// <para>
    /// In Suflae, colons have two meanings:
    /// <list type="bullet">
    ///   <item><description>Block starter: <c>if condition:</c> (followed by newline)</description></item>
    ///   <item><description>Type annotation: <c>let x: s32 = 5</c> (followed by type)</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// This method looks ahead to determine which case applies.
    /// </para>
    /// </remarks>
    private bool IsBlockStarterColon()
    {
        int pos = _position;

        // Skip whitespace
        while (pos < _source.Length && (_source[index: pos] == ' ' || _source[index: pos] == '\t'))
        {
            pos += 1;
        }

        // If we hit end of file or newline, it's a block starter
        if (pos >= _source.Length || _source[index: pos] == '\n' || _source[index: pos] == '\r')
        {
            return true;
        }

        // If we hit a comment, it's still a block starter
        if (_source[index: pos] == '#')
        {
            return true;
        }

        // Otherwise, it's a type annotation
        return false;
    }

    #endregion
}
