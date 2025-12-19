namespace Compilers.Suflae.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing the main token scanning dispatch logic for the Suflae tokenizer.
/// </summary>
/// <remarks>
/// This file contains the core <see cref="ScanToken"/> method which routes character
/// recognition to specialized scanning methods. Key differences from RazorForge:
/// <list type="bullet">
///   <item><description>Indentation handling at start of each line</description></item>
///   <item><description>No braces (uses indentation for blocks)</description></item>
///   <item><description>Colon detection for block starters</description></item>
///   <item><description>Significant newline handling</description></item>
/// </list>
/// </remarks>
public partial class SuflaeTokenizer
{
    #region Main Token Scanning

    /// <summary>
    /// Scans a single token from the current position in the source code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method handles indentation at the start of lines, then dispatches
    /// to specialized scanning methods based on the current character.
    /// </para>
    /// <para>
    /// Unlike RazorForge, Suflae:
    /// <list type="bullet">
    ///   <item><description>Has no braces - uses indentation-based INDENT/DEDENT tokens</description></item>
    ///   <item><description>Tracks whether colons start blocks (for _expectIndent)</description></item>
    ///   <item><description>Treats newlines as significant in most contexts</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">
    /// Thrown when invalid syntax is encountered.
    /// </exception>
    private void ScanToken()
    {
        // Handle indentation at start of line
        if (_column == 1)
        {
            HandleIndentation();
            if (IsAtEnd()) return;
        }

        // Skip non-newline whitespace and update token start
        while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r')
            Advance();

        _tokenStart = _position;
        _tokenStartColumn = _column;
        char c = Advance();

        switch (c)
        {
            // Whitespace already handled above, but case needed for post-indent whitespace
            case ' ' or '\r' or '\t':
                break;

            // Newlines are significant in Suflae
            case '\n':
                HandleNewline();
                break;

            // Comments
            case '#':
                ScanComment();
                break;

            // String and character literals
            case '"':
                ScanString();
                break;
            case '\'':
                ScanLetter();
                break;

            // Potential prefixed literals or identifiers
            case 'r' or 'f':
                if (!TryParseTextPrefix()) ScanIdentifier();
                break;
            case 'b':
                // Could be bytes prefix (b"..."), byte character (b'x'), or identifier
                if (!TryParseTextPrefix() && !TryParseByteLiteralPrefix())
                    ScanIdentifier();
                break;

            // Delimiters (no braces in Suflae)
            case '(':
                AddToken(TokenType.LeftParen);
                break;
            case ')':
                AddToken(TokenType.RightParen);
                break;
            case '[':
                AddToken(TokenType.LeftBracket);
                break;
            case ']':
                AddToken(TokenType.RightBracket);
                break;
            case ',':
                AddToken(TokenType.Comma);
                break;

            // Multi-character punctuation
            case '.':
                AddToken(Match('.')
                    ? Match('.') ? TokenType.DotDotDot : TokenType.DotDot
                    : TokenType.Dot);
                break;
            case ':':
                if (Match(':'))
                {
                    AddToken(TokenType.DoubleColon);
                }
                else
                {
                    AddToken(TokenType.Colon);
                    _expectIndent = IsBlockStarterColon();
                }
                break;

            // Arithmetic operators with overflow variants
            case '+':
                ScanPlusOperator();
                break;
            case '-':
                if (Match('>')) AddToken(TokenType.Arrow);
                else ScanMinusOperator();
                break;
            case '*':
                ScanStarOperator();
                break;
            case '/':
                ScanSlashOperator();
                break;
            case '%':
                ScanPercentOperator();
                break;

            // Comparison and assignment
            case '=':
                AddToken(Match('=')
                    ? Match('=') ? TokenType.ReferenceEqual : TokenType.Equal
                    : Match('>') ? TokenType.FatArrow : TokenType.Assign);
                break;
            case '!':
                AddToken(Match('=')
                    ? Match('=') ? TokenType.ReferenceNotEqual : TokenType.NotEqual
                    : TokenType.Bang);
                break;
            case '<':
                ScanLessThanOperator();
                break;
            case '>':
                ScanGreaterThanOperator();
                break;

            // Single-character operators
            case '&':
                AddToken(TokenType.Ampersand);
                break;
            case '|':
                AddToken(TokenType.Pipe);
                break;
            case '^':
                AddToken(TokenType.Caret);
                break;
            case '~':
                AddToken(TokenType.Tilde);
                break;
            case '?':
                AddToken(Match('?') ? TokenType.NoneCoalesce : TokenType.Question);
                break;

            // Special @ tokens
            case '@':
                ScanAtSign();
                break;

            // Numbers
            case '0':
                if (Match('x') || Match('X'))
                    ScanPrefixedNumber(isHex: true);
                else if (Match('b') || Match('B'))
                    ScanPrefixedNumber(isHex: false);
                else
                    ScanNumber();
                break;

            // Default: digits, identifiers, or unknown
            default:
                _hasTokenOnLine = true;
                if (char.IsDigit(c))
                    ScanNumber();
                else if (IsIdentifierStart(c))
                    ScanIdentifier();
                else
                    AddToken(TokenType.Unknown);
                break;
        }
    }

    #endregion
}
