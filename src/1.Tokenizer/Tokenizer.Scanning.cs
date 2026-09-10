using Builder.Diagnostics;

namespace Builder.Tokenizer;

/// <summary>
/// Partial class containing the main token scanning dispatch logic for the unified tokenizer.
/// </summary>
public partial class Tokenizer
{
    #region Main Token Scanning

    /// <summary>
    /// Scans a single token from the current position in the source code.
    /// </summary>
    private void ScanToken()
    {
        // Handle indentation at start of line
        if (_column == 1 && ScanIndentationAndCheckEnd())
        {
            return;
        }

        // Skip non-newline whitespace and update token start
        while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r')
        {
            Advance();
        }

        _tokenStart = _position;
        _tokenStartColumn = _column;
        _tokenStartLine = _line;
        char c = Advance();

        switch (c)
        {
            // Whitespace already handled above, but case needed for post-indent whitespace
            case ' ' or '\r' or '\t':
                break;

            // Newlines are significant
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
                ScanCharacter();
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
                ScanBPrefixOrIdentifier();
                break;

            // Opening bracket delimiters — increment depth
            case '(' or '[' or '{':
                ScanOpenBracket(c: c);
                break;

            // Closing bracket delimiters — decrement depth
            case ')' or ']' or '}':
                ScanCloseBracket(c: c);
                break;

            case ',':
                AddToken(type: TokenType.Comma);
                break;
            case ';':
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidCharacter,
                    message: "Semicolons are not used. Statements are terminated by newlines.",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);

            // Multi-character punctuation
            case '.':
                ScanDotOperator();
                break;
            case ':':
                // `::` is the realm-qualifier separator (e.g. `RF::Core.List`); bare `:` is a colon.
                AddToken(type: Match(expected: ':')
                    ? TokenType.DoubleColon
                    : TokenType.Colon);
                break;

            // Arithmetic operators with overflow variants
            case '+':
                ScanPlusOperator();
                break;
            case '-':
                ScanMinusOrArrow();
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
                ScanEqualsOperator();
                break;
            case '!':
                ScanBangOperator();
                break;
            case '<':
                ScanLessThanOperator();
                break;
            case '>':
                ScanGreaterThanOperator();
                break;

            // Bitwise operators with compound-assignment variants
            case '&':
                ScanAmpersand();
                break;
            case '|':
                ScanPipe();
                break;
            case '^':
                ScanCaret();
                break;
            case '~':
                AddToken(type: TokenType.Tilde);
                break;
            case '?':
                ScanQuestionOperator();
                break;

            // Special @ tokens
            case '@':
                ScanAtSign();
                break;

            // Numbers (special handling for 0x, 0b, and 0o prefixes)
            case '0':
                ScanZeroPrefixedNumber();
                break;

            // Default: digits, identifiers, or unknown
            default:
                ScanDefaultCharacter(c: c);
                break;
        }
    }

    /// <summary>
    /// Handles line-start indentation and returns <c>true</c> when the end of input is reached
    /// after processing, signalling that <see cref="ScanToken"/> should return immediately.
    /// </summary>
    private bool ScanIndentationAndCheckEnd()
    {
        HandleIndentation();
        return IsAtEnd();
    }

    /// <summary>
    /// Scans a <c>b</c> character that may begin a bytes-string literal (<c>b"…"</c>), a
    /// byte-character literal (<c>b'x'</c>), or a plain identifier.
    /// </summary>
    private void ScanBPrefixOrIdentifier()
    {
        if (!TryParseTextPrefix() && !TryParseByteLiteralPrefix())
        {
            ScanIdentifier();
        }
    }

    /// <summary>Scans an opening bracket delimiter and increments the bracket depth.</summary>
    private void ScanOpenBracket(char c)
    {
        TokenType type = c switch
        {
            '(' => TokenType.LeftParen,
            '[' => TokenType.LeftBracket,
            _ => TokenType.LeftBrace
        };
        AddToken(type: type);
        _bracketDepth++;
    }

    /// <summary>Scans a closing bracket delimiter and decrements the bracket depth.</summary>
    private void ScanCloseBracket(char c)
    {
        TokenType type = c switch
        {
            ')' => TokenType.RightParen,
            ']' => TokenType.RightBracket,
            _ => TokenType.RightBrace
        };
        AddToken(type: type);
        if (_bracketDepth > 0)
        {
            _bracketDepth--;
        }
    }

    /// <summary>Scans a '-' token: '->' arrow or a minus/minus-assign operator.</summary>
    private void ScanMinusOrArrow()
    {
        if (Match(expected: '>'))
        {
            AddToken(type: TokenType.Arrow);
        }
        else
        {
            ScanMinusOperator();
        }
    }

    /// <summary>Scans '&amp;' or '&amp;=' (bitwise AND or AND-assign).</summary>
    private void ScanAmpersand()
    {
        AddToken(type: Match(expected: '=')
            ? TokenType.AmpersandAssign
            : TokenType.Ampersand);
    }

    /// <summary>Scans '|' or '|=' (bitwise OR or OR-assign).</summary>
    private void ScanPipe()
    {
        AddToken(type: Match(expected: '=')
            ? TokenType.PipeAssign
            : TokenType.Pipe);
    }

    /// <summary>Scans '^' or '^=' (bitwise XOR or XOR-assign).</summary>
    private void ScanCaret()
    {
        AddToken(type: Match(expected: '=')
            ? TokenType.CaretAssign
            : TokenType.Caret);
    }

    /// <summary>
    /// Scans a '.' token: '...' variadic/spread, the removed '..' range (error), or a bare dot.
    /// </summary>
    private void ScanDotOperator()
    {
        if (Match(expected: '.'))
        {
            if (Match(expected: '.'))
            {
                AddToken(type: TokenType.DotDotDot);
            }
            else
            {
                throw new GrammarException(code: GrammarDiagnosticCode.InvalidCharacter,
                    message:
                    "Range operator '..' is no longer supported. Use 'to' keyword instead (e.g., '1 to 10').",
                    fileName: _fileName,
                    line: _line,
                    column: _column,
                    language: _language);
            }
        }
        else
        {
            AddToken(type: TokenType.Dot);
        }
    }

    /// <summary>
    /// Scans a '=' token: '==' equality, '===' reference identity, '=>' fat arrow, or '=' assign.
    /// </summary>
    private void ScanEqualsOperator()
    {
        if (Match(expected: '='))
        {
            // `==` value equality, or `===` reference identity (longest match).
            AddToken(type: Match(expected: '=')
                ? TokenType.IdentityEqual
                : TokenType.Equal);
        }
        else if (Match(expected: '>'))
        {
            AddToken(type: TokenType.FatArrow);
        }
        else
        {
            AddToken(type: TokenType.Assign);
        }
    }

    /// <summary>
    /// Scans a '!' token: '!=' inequality, '!==' reference non-identity, '!!' force unwrap, or '!'.
    /// </summary>
    private void ScanBangOperator()
    {
        if (Match(expected: '='))
        {
            // `!=` value inequality, or `!==` reference non-identity (longest match).
            AddToken(type: Match(expected: '=')
                ? TokenType.IdentityNotEqual
                : TokenType.NotEqual);
        }
        else if (Match(expected: '!'))
        {
            // !! (force unwrap)
            AddToken(type: TokenType.BangBang);
        }
        else
        {
            // ! (failable marker or negation)
            AddToken(type: TokenType.Bang);
        }
    }

    /// <summary>
    /// Scans a '?' token: '?.' optional access, '??'/'??=' none-coalescing, or a bare question mark.
    /// </summary>
    private void ScanQuestionOperator()
    {
        if (Match(expected: '.'))
        {
            AddToken(type: TokenType.QuestionDot);
        }
        else if (Match(expected: '?'))
        {
//?? or ??=
            AddToken(type: Match(expected: '=')
                ? TokenType.NoneCoalesceAssign
                : TokenType.NoneCoalesce);
        }
        else
        {
            AddToken(type: TokenType.Question);
        }
    }

    /// <summary>
    /// Scans a numeric literal beginning with '0', dispatching on the base prefix (0x hex, 0b binary,
    /// 0o octal) or falling back to a decimal number.
    /// </summary>
    private void ScanZeroPrefixedNumber()
    {
        if (Match(expected: 'x') || Match(expected: 'X'))
        {
            ScanPrefixedNumber(isHex: true);
        }
        else if ((Peek() == 'b' || Peek() == 'B') && (Peek(offset: 1) == '0' ||
                                                      Peek(offset: 1) == '1' ||
                                                      Peek(offset: 1) == '_'))
        {
            Advance(); // consume 'b' or 'B'
            ScanPrefixedNumber(isHex: false);
        }
        else if ((Peek() == 'o' || Peek() == 'O') &&
                 (Peek(offset: 1) >= '0' && Peek(offset: 1) <= '7' || Peek(offset: 1) == '_'))
        {
            Advance(); // consume 'o' or 'O'
            ScanOctalNumber();
        }
        else
        {
            ScanNumber();
        }
    }

    /// <summary>
    /// Scans the default character case: a digit begins a number, an identifier-start begins an
    /// identifier, and anything else is an unknown token.
    /// </summary>
    private void ScanDefaultCharacter(char c)
    {
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
    }

    #endregion
}
