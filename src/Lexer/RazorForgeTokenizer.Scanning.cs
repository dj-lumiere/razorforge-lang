namespace Compilers.RazorForge.Lexer;

using Compilers.Shared.Lexer;

/// <summary>
/// Partial class containing the main token scanning dispatch logic for the RazorForge tokenizer.
/// </summary>
/// <remarks>
/// This file contains the core <see cref="ScanToken"/> method which dispatches to
/// specialized scanning methods based on the current character. It serves as the
/// central routing point for all token recognition.
/// </remarks>
public partial class RazorForgeTokenizer
{
    #region Main Token Scanning

    /// <summary>
    /// Scans a single token from the current position in the source code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method is the main dispatch point for token recognition. It consumes
    /// one character and uses it to determine which type of token is being scanned,
    /// then delegates to the appropriate specialized scanning method.
    /// </para>
    /// <para>
    /// Token categories handled:
    /// <list type="bullet">
    ///   <item><description>Whitespace - consumed and ignored</description></item>
    ///   <item><description>Comments - # for regular, ### for documentation</description></item>
    ///   <item><description>Literals - strings, characters, numbers</description></item>
    ///   <item><description>Delimiters - parentheses, brackets, braces, comma, semicolon</description></item>
    ///   <item><description>Operators - arithmetic, comparison, bitwise, logical</description></item>
    ///   <item><description>Special tokens - @intrinsic, @native</description></item>
    ///   <item><description>Identifiers and keywords</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="LexerException">
    /// Thrown when an unterminated literal or invalid escape sequence is encountered.
    /// </exception>
    private void ScanToken()
    {
        char c = Advance();

        switch (c)
        {
            // Whitespace - consumed but not tokenized
            case ' ' or '\r' or '\t' or '\n':
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
            case 'l':
                if (!TryParseLetterPrefix()) ScanIdentifier();
                break;
            case 'r' or 'f' or 't':
                if (!TryParseTextPrefix()) ScanIdentifier();
                break;

            // Delimiters
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
            case '{':
                AddToken(TokenType.LeftBrace);
                break;
            case '}':
                AddToken(TokenType.RightBrace);
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
                AddToken(Match(':') ? TokenType.DoubleColon : TokenType.Colon);
                break;
            case '!':
                AddToken(Match('=')
                    ? Match('=') ? TokenType.ReferenceNotEqual : TokenType.NotEqual
                    : TokenType.Bang);
                break;
            case '?':
                AddToken(Match('?') ? TokenType.NoneCoalesce : TokenType.Question);
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

            // Special @ tokens
            case '@':
                ScanAtSign();
                break;

            // Numbers (special handling for 0x and 0b prefixes)
            case '0':
                if (Match('x') || Match('X'))
                    ScanPrefixedNumber(isHex: true);
                else if ((Peek() == 'b' || Peek() == 'B') &&
                         (Peek(1) == '0' || Peek(1) == '1' || Peek(1) == '_'))
                {
                    Advance(); // consume 'b' or 'B'
                    ScanPrefixedNumber(isHex: false);
                }
                else
                    ScanNumber();
                break;

            // Default: digits, identifiers, or unknown
            default:
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
