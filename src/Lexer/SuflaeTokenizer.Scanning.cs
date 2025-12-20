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
            if (IsAtEnd())
            {
                return;
            }
        }

        // Skip non-newline whitespace and update token start
        while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r')
        {
            Advance();
        }

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
                if (!TryParseTextPrefix())
                {
                    ScanIdentifier();
                }

                break;
            case 'b':
                // Could be bytes prefix (b"..."), byte character (b'x'), or identifier
                if (!TryParseTextPrefix() && !TryParseByteLiteralPrefix())
                {
                    ScanIdentifier();
                }

                break;

            // Delimiters (no braces in Suflae)
            case '(':
                AddToken(type: TokenType.LeftParen);
                break;
            case ')':
                AddToken(type: TokenType.RightParen);
                break;
            case '[':
                AddToken(type: TokenType.LeftBracket);
                break;
            case ']':
                AddToken(type: TokenType.RightBracket);
                break;
            case ',':
                AddToken(type: TokenType.Comma);
                break;
            case ';':
                throw new LexerException(
                    message:
                    $"[{_line}:{_column}] Semicolons are not used in Suflae. Statements are terminated by newlines.");

            // Multi-character punctuation
            case '.':
                if (Match(expected: '.'))
                {
                    if (Match(expected: '.'))
                    {
                        AddToken(type: TokenType.DotDotDot);
                    }
                    else
                    {
                        throw new LexerException(
                            message:
                            $"[{_line}:{_column}] Range operator '..' is no longer supported. Use 'to' keyword instead (e.g., '1 to 10').");
                    }
                }
                else
                {
                    AddToken(type: TokenType.Dot);
                }

                break;
            case ':':
                if (Match(expected: ':'))
                {
                    throw new LexerException(
                        message:
                        $"[{_line}:{_column}] Static access operator '::' is no longer supported. Use '.' instead.");
                }

                AddToken(type: TokenType.Colon);
                _expectIndent = IsBlockStarterColon();
                break;

            // Arithmetic operators with overflow variants
            case '+':
                ScanPlusOperator();
                break;
            case '-':
                if (Match(expected: '>'))
                {
                    AddToken(type: TokenType.Arrow);
                }
                else
                {
                    ScanMinusOperator();
                }

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
                AddToken(type: Match(expected: '=') ? Match(expected: '=')
                        ? TokenType.ReferenceEqual
                        : TokenType.Equal :
                    Match(expected: '>') ? TokenType.FatArrow : TokenType.Assign);
                break;
            case '!':
                AddToken(type: Match(expected: '=')
                    ? Match(expected: '=')
                        ? TokenType.ReferenceNotEqual
                        : TokenType.NotEqual
                    : TokenType.Bang);
                break;
            case '<':
                ScanLessThanOperator();
                break;
            case '>':
                ScanGreaterThanOperator();
                break;

            // Single-character operators (with compound assignment variants)
            case '&':
                AddToken(type: Match(expected: '=') ? TokenType.AmpersandAssign : TokenType.Ampersand);
                break;
            case '|':
                AddToken(type: Match(expected: '=') ? TokenType.PipeAssign : TokenType.Pipe);
                break;
            case '^':
                AddToken(type: Match(expected: '=') ? TokenType.CaretAssign : TokenType.Caret);
                break;
            case '~':
                AddToken(type: TokenType.Tilde);
                break;
            case '?':
                if (Match(expected: '?'))
                {
                    // ?? or ??=
                    AddToken(type: Match(expected: '=') ? TokenType.NoneCoalesceAssign : TokenType.NoneCoalesce);
                }
                else
                {
                    AddToken(type: TokenType.Question);
                }
                break;

            // Special @ tokens
            case '@':
                ScanAtSign();
                break;

            // Numbers
            case '0':
                if (Match(expected: 'x') || Match(expected: 'X'))
                {
                    ScanPrefixedNumber(isHex: true);
                }
                else if (Match(expected: 'b') || Match(expected: 'B'))
                {
                    ScanPrefixedNumber(isHex: false);
                }
                else
                {
                    ScanNumber();
                }

                break;

            // Default: digits, identifiers, or unknown
            default:
                _hasTokenOnLine = true;
                if (char.IsDigit(c: c))
                {
                    ScanNumber();
                }
                else if (IsIdentifierStart(c: c))
                {
                    ScanIdentifier();
                }
                else
                {
                    AddToken(type: TokenType.Unknown);
                }

                break;
        }
    }

    #endregion
}
