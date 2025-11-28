using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Lexer;

/// <summary>
/// Tokenizer implementation for the RazorForge programming language.
/// Handles RazorForge-specific syntax including braced blocks, overflow operators,
/// and various text literal prefixes. Uses semicolons as optional statement terminators.
/// </summary>
public class RazorForgeTokenizer : BaseTokenizer
{
    #region Fields and Keywords

    /// <summary>Dictionary mapping RazorForge keywords to their corresponding token types</summary>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        [key: "routine"] = TokenType.Routine,
        [key: "choice"] = TokenType.Choice,
        [key: "chimera"] = TokenType.Chimera,
        [key: "variant"] = TokenType.Variant,
        [key: "mutant"] = TokenType.Mutant,
        [key: "let"] = TokenType.Let,
        [key: "var"] = TokenType.Var,
        [key: "preset"] = TokenType.Preset,
        [key: "common"] = TokenType.TypeWise,
        [key: "private"] = TokenType.Private,
        [key: "public(family)"] = TokenType.PublicFamily,
        [key: "public(module)"] = TokenType.PublicModule,
        [key: "public"] = TokenType.Public,
        [key: "global"] = TokenType.Global,
        [key: "external"] = TokenType.External,
        [key: "me"] = TokenType.Self,
        [key: "parent"] = TokenType.Super,
        [key: "if"] = TokenType.If,
        [key: "elif"] = TokenType.Elif,
        [key: "else"] = TokenType.Else,
        [key: "then"] = TokenType.Then,
        [key: "unless"] = TokenType.Unless,
        [key: "break"] = TokenType.Break,
        [key: "continue"] = TokenType.Continue,
        [key: "return"] = TokenType.Return,
        [key: "throw"] = TokenType.Throw,
        [key: "absent"] = TokenType.Absent,
        [key: "for"] = TokenType.For,
        [key: "loop"] = TokenType.Loop,
        [key: "while"] = TokenType.While,
        [key: "when"] = TokenType.When,
        [key: "is"] = TokenType.Is,
        [key: "from"] = TokenType.From,
        [key: "follows"] = TokenType.Follows,
        [key: "import"] = TokenType.Import,
        [key: "define"] = TokenType.Define,
        [key: "using"] = TokenType.Using,
        [key: "as"] = TokenType.As,
        [key: "pass"] = TokenType.Pass,
        [key: "danger"] = TokenType.Danger,
        [key: "with"] = TokenType.With,
        [key: "where"] = TokenType.Where,
        [key: "isnot"] = TokenType.IsNot,
        [key: "notfrom"] = TokenType.NotFrom,
        [key: "notin"] = TokenType.NotIn,
        [key: "notfollows"] = TokenType.NotFollows,
        [key: "in"] = TokenType.In,
        [key: "to"] = TokenType.To,
        [key: "step"] = TokenType.Step,
        [key: "and"] = TokenType.And,
        [key: "or"] = TokenType.Or,
        [key: "not"] = TokenType.Not,
        [key: "true"] = TokenType.True,
        [key: "false"] = TokenType.False,
        [key: "none"] = TokenType.None,
        [key: "entity"] = TokenType.Entity,
        [key: "record"] = TokenType.Record,
        [key: "protocol"] = TokenType.Protocol,
        [key: "requires"] = TokenType.Requires,
        [key: "generate"] = TokenType.Generate,
        [key: "suspended"] = TokenType.Suspended,
        [key: "waitfor"] = TokenType.Waitfor,
        [key: "usurping"] = TokenType.Usurping,
        [key: "viewing"] = TokenType.Viewing,
        [key: "hijacking"] = TokenType.Hijacking,
        [key: "seizing"] = TokenType.Seizing,
        [key: "observing"] = TokenType.Observing
    };

    #endregion

    #region Initialization and Core Methods

    /// <summary>
    /// Initializes a new RazorForge tokenizer with the source code to tokenize.
    /// </summary>
    /// <param name="source">The RazorForge source code text</param>
    public RazorForgeTokenizer(string source) : base(source: source)
    {
    }

    /// <summary>
    /// Returns the RazorForge-specific keyword mappings.
    /// </summary>
    /// <returns>Dictionary of RazorForge keywords and their token types</returns>
    protected override Dictionary<string, TokenType> GetKeywords()
    {
        return _keywords;
    }

    /// <summary>
    /// Tokenizes the entire RazorForge source code into a list of tokens.
    /// </summary>
    /// <returns>List of tokens representing the RazorForge source code</returns>
    public override List<Token> Tokenize()
    {
        while (!IsAtEnd())
        {
            TokenStart = Position;
            TokenStartColumn = Column;
            TokenStartLine = Line;

            ScanToken();
        }

        Tokens.Add(item: new Token(Type: TokenType.Eof, Text: "", Line: Line, Column: Column,
            Position: Position));
        return Tokens;
    }

    #endregion

    #region Token Scanning

    /// <summary>
    /// Scans a single token from the current position in the source code.
    /// Handles all RazorForge-specific syntax and delegates to base entity for common constructs.
    /// </summary>
    private void ScanToken()
    {
        char c = Advance();

        switch (c)
        {
            // Whitespace (ignored)
            case ' ' or '\r' or '\t' or '\n': break;

            // Literals
            case '#': ScanComment(); break;
            case '"': ScanRazorForgeString(); break;
            case '\'': ScanChar(); break;
            case 'l':
                if (!TryParseLetterPrefix())
                {
                    ScanIdentifier();
                }

                break;
            case 'r' or 'f' or 't':
                if (!TryParseTextPrefix())
                {
                    ScanIdentifier();
                }

                break;

            // Single-character delimiters
            case '(': AddToken(type: TokenType.LeftParen); break;
            case ')': AddToken(type: TokenType.RightParen); break;
            case '[': AddToken(type: TokenType.LeftBracket); break;
            case ']': AddToken(type: TokenType.RightBracket); break;
            case '{': AddToken(type: TokenType.LeftBrace); break;
            case '}': AddToken(type: TokenType.RightBrace); break;
            case ',': AddToken(type: TokenType.Comma); break;
            case ';': AddToken(type: TokenType.Newline); break;

            // Multi-character delimiters and operators
            case '.':
                AddToken(type: Match(expected: '.')
                    ? Match(expected: '.')
                        ? TokenType.DotDotDot
                        : TokenType.DotDot
                    : TokenType.Dot); break;
            case ':':
                AddToken(type: Match(expected: ':')
                    ? TokenType.DoubleColon
                    : TokenType.Colon); break;
            case '!':
                AddToken(type: Match(expected: '=')
                    ? Match(expected: '=')
                        ? TokenType.ReferenceNotEqual
                        : TokenType.NotEqual
                    : TokenType.Bang); break;
            case '?':
                AddToken(type: Match(expected: ':')
                    ? TokenType.QuestionColon
                    : TokenType.Question); break;

            // Arithmetic operators (delegated to specialized methods)
            case '+': ScanPlusOperator(); break;
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
            case '*': ScanStarOperator(); break;
            case '/': ScanSlashOperator(); break;
            case '%': ScanPercentOperator(); break;

            // Comparison and assignment
            case '=':
                AddToken(type: Match(expected: '=') ? Match(expected: '=')
                        ? TokenType.ReferenceEqual
                        : TokenType.Equal :
                    Match(expected: '>') ? TokenType.FatArrow : TokenType.Assign); break;
            case '<':
                AddToken(type: Match(expected: '=') ? TokenType.LessEqual :
                    Match(expected: '<') ? TokenType.LeftShift : TokenType.Less); break;
            case '>':
                AddToken(type: Match(expected: '=') ? TokenType.GreaterEqual :
                    Match(expected: '>') ? TokenType.RightShift : TokenType.Greater); break;

            // Single-character operators
            case '&': AddToken(type: TokenType.Ampersand); break;
            case '|': AddToken(type: TokenType.Pipe); break;
            case '^': AddToken(type: TokenType.Caret); break;
            case '~': AddToken(type: TokenType.Tilde); break;
            case '@':
                // Check for @intrinsic
                if (Peek() == 'i' && PeekWord() == "intrinsic")
                {
                    // Consume "intrinsic"
                    for (int i = 0; i < 9; i++) Advance();
                    AddToken(type: TokenType.Intrinsic);
                }
                else
                {
                    AddToken(type: TokenType.At);
                }
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

            default:
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

    #region RazorForge-Specific Literal Scanning

    /// <summary>
    /// Scans a basic RazorForge text literal (without prefix).
    /// In RazorForge, regular text is an alias for text8 (8-bit text).
    /// </summary>
    private void ScanRazorForgeString()
    {
        ScanStringLiteralWithType(isRaw: false, isFormatted: false,
            tokenType: TokenType.Text8Literal, bitWidth: 8);
    }

    /// <summary>
    /// Attempts to parse a letter prefix (letter8, letter16, letter32) followed by a single-quoted character.
    /// </summary>
    /// <returns>True if a valid letter prefix was found and processed</returns>
    private bool TryParseLetterPrefix()
    {
        int startPos = Position - 1; // We already consumed first letter
        int originalPos = Position;
        int originalCol = Column;

        // Build the prefix string starting with the character we already consumed
        string prefix = Source[index: startPos]
           .ToString();

        // Continue building the prefix
        while (!IsAtEnd() && char.IsLetterOrDigit(c: Peek()))
        {
            prefix += Advance();
        }

        // Check if we have a quote after the prefix
        if (Peek() != '\'')
        {
            // Not a letter prefix, reset
            Position = originalPos;
            Column = originalCol;
            return false;
        }

        // Check for valid letter prefixes
        switch (prefix)
        {
            case "letter8":
                Advance(); // consume '\''
                ScanCharLiteral(tokenType: TokenType.Letter8Literal, bitWidth: 8);
                return true;
            case "letter16":
                Advance(); // consume '\''
                ScanCharLiteral(tokenType: TokenType.Letter16Literal, bitWidth: 16);
                return true;
            case "letter32":
                Advance(); // consume '\''
                ScanCharLiteral(tokenType: TokenType.LetterLiteral, bitWidth: 32);
                return true;
            default:
                // Not a letter prefix, reset
                Position = originalPos;
                Column = originalCol;
                return false;
        }
    }

    /// <summary>
    /// Scans a character literal with the specified token type.
    /// </summary>
    /// <param name="tokenType">The type of character literal token</param>
    /// <param name="bitWidth">The bitwidth for letter in case of unicode letter. It defaults to 32.</param>
    private void ScanCharLiteral(TokenType tokenType, int bitWidth = 32)
    {
        if (Peek() == '\\')
        {
            Advance();
            ScanEscapeSequence(bitWidth: bitWidth);
        }
        else
        {
            Advance();
        }

        if (!Match(expected: '\''))
        {
            throw new LexerException(message: $"Unterminated character literal at line {Line}");
        }

        AddToken(type: tokenType);
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Peeks ahead to check if the next characters match a specific word.
    /// </summary>
    /// <returns>The word starting at the current peek position, or empty string if not an identifier</returns>
    private string PeekWord()
    {
        int offset = 0;
        var sb = new System.Text.StringBuilder();

        while (true)
        {
            char c = Peek(offset);
            if (char.IsLetterOrDigit(c) || c == '_')
            {
                sb.Append(c);
                offset++;
            }
            else
            {
                break;
            }
        }

        return sb.ToString();
    }

    #endregion
}
