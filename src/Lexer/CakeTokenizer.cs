using Compilers.Shared.Lexer;

namespace Compilers.Cake.Lexer;

/// <summary>
/// Tokenizer implementation for the Cake programming language.
/// Handles Cake-specific syntax including significant indentation (Python-style blocks),
/// colon-based block starters, and indent/dedent tokens for block structure.
/// </summary>
public class CakeTokenizer : BaseTokenizer
{
    #region Fields and Keywords

    /// <summary>Current indentation level (number of spaces from start of line)</summary>
    private int _currentIndentLevel;

    /// <summary>Flag indicating that an indent token should be generated on the next line</summary>
    private bool _expectIndent;

    /// <summary>Flag tracking whether any non-whitespace tokens have been processed on current line</summary>
    private bool _hasTokenOnLine;

    private bool _hasDefinitions;

    /// <summary>Dictionary mapping Cake keywords to their corresponding token types</summary>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        [key: "recipe"] = TokenType.Recipe,
        [key: "entity"] = TokenType.Entity,
        [key: "record"] = TokenType.Record,
        [key: "choice"] = TokenType.Choice,
        [key: "requires"] = TokenType.Requires,
        [key: "chimera"] = TokenType.Chimera,
        [key: "variant"] = TokenType.Variant,
        [key: "mutant"] = TokenType.Mutant,
        [key: "protocol"] = TokenType.Protocol,
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
        [key: "mayhem"] = TokenType.Mayhem,
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
        [key: "generate"] = TokenType.Generate,
        [key: "suspended"] = TokenType.Suspended,
        [key: "waitfor"] = TokenType.Waitfor,
        [key: "usurping"] = TokenType.Usurping
    };

    #endregion

    #region Initialization and Core Methods

    /// <summary>
    /// Initializes a new Cake tokenizer with the source code to tokenize.
    /// </summary>
    /// <param name="source">The Cake source code text</param>
    public CakeTokenizer(string source) : base(source: source)
    {
    }

    public bool IsScriptMode => !_hasDefinitions;

    /// <summary>
    /// Returns the Cake-specific keyword mappings.
    /// </summary>
    /// <returns>Dictionary of Cake keywords and their token types</returns>
    protected override Dictionary<string, TokenType> GetKeywords()
    {
        return _keywords;
    }

    /// <summary>
    /// Tokenizes the entire Cake source code into a list of tokens.
    /// Handles significant indentation and generates indent/dedent tokens.
    /// </summary>
    /// <returns>List of tokens representing the Cake source code</returns>
    public override List<Token> Tokenize()
    {
        while (!IsAtEnd())
        {
            TokenStart = Position;
            TokenStartColumn = Column;
            TokenStartLine = Line;

            ScanToken();
        }

        // Emit remaining dedents at end of file
        for (int i = 0; i < _currentIndentLevel; i++)
        {
            AddToken(type: TokenType.Dedent, text: "");
        }

        Tokens.Add(item: new Token(Type: TokenType.Eof, Text: "", Line: Line, Column: Column,
            Position: Position));
        return Tokens;
    }

    #endregion

    #region Token Scanning

    /// <summary>
    /// Scans a single token from the current position, handling Cake-specific indentation rules.
    /// </summary>
    private void ScanToken()
    {
        // Handle indentation at start of line
        if (Column == 1)
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

        TokenStart = Position;
        TokenStartColumn = Column;
        char c = Advance();

        switch (c)
        {
            // Whitespace (remaining after skip above)
            case ' ' or '\r' or '\t': break;
            case '\n': HandleNewline(); break;

            // Literals
            case '#': ScanComment(); break;
            case '"': ScanCakeString(); break;
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

            // Delimiters (no braces in Cake - uses indentation)
            case '(': AddToken(type: TokenType.LeftParen); break;
            case ')': AddToken(type: TokenType.RightParen); break;
            case '[': AddToken(type: TokenType.LeftBracket); break;
            case ']': AddToken(type: TokenType.RightBracket); break;
            case ',': AddToken(type: TokenType.Comma); break;

            // Multi-character delimiters
            case '.':
                AddToken(type: Match(expected: '.')
                    ? Match(expected: '.')
                        ? TokenType.DotDotDot
                        : TokenType.DotDot
                    : TokenType.Dot); break;
            case ':':
                if (Match(expected: ':'))
                {
                    AddToken(type: TokenType.DoubleColon);
                }
                else
                {
                    AddToken(type: TokenType.Colon);
                    _expectIndent = IsBlockStarterColon();
                }

                break;

            // Arithmetic operators
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
                AddToken(type: Match(expected: '=')
                    ? Match(expected: '=')
                        ? TokenType.ReferenceEqual
                        : TokenType.Equal
                    : Match(expected: '>')
                        ? TokenType.FatArrow
                        : TokenType.Assign); break;
            case '!':
                AddToken(type: Match(expected: '=')
                    ? Match(expected: '=')
                        ? TokenType.ReferenceNotEqual
                        : TokenType.NotEqual
                    : TokenType.Bang); break;
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
            case '?':
                AddToken(type: Match(expected: ':')
                    ? TokenType.QuestionColon
                    : TokenType.Question); break;
            case '@': AddToken(type: TokenType.At); break;

            // Numbers
            case '0':
                if (Match(expected: 'x') || Match(expected: 'X'))
                {
                    ScanCakePrefixedNumber(isHex: true);
                }
                else if (Match(expected: 'b') || Match(expected: 'B'))
                {
                    ScanCakePrefixedNumber(isHex: false);
                }
                else
                {
                    ScanCakeNumber();
                }

                break;

            default:
                _hasTokenOnLine = true;
                if (char.IsDigit(c: c))
                {
                    ScanCakeNumber();
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

    #region Indentation and Newline Handling

    /// <summary>
    /// Handles indentation processing at the beginning of lines, generating indent/dedent tokens.
    /// Manages indentation level tracking and validates consistent spacing.
    /// </summary>
    private void HandleIndentation()
    {
        int spaces = 0;
        while (Peek() == ' ' || Peek() == '\t')
        {
            if (Peek() == ' ')
            {
                spaces += 1;
            }
            else // Tab
            {
                spaces += 4;
            }

            Advance();
        }

        // Skip empty lines
        if (Peek() == '\n' || IsAtEnd())
        {
            return;
        }

        // Skip lines with only comments
        if (Peek() == '#')
        {
            return;
        }

        int newIndentLevel = spaces / 4;

        // Check for misaligned indentation
        if (spaces % 4 != 0)
        {
            throw new LexerException(
                message:
                $"Indentation error at line {Line}: expected multiple of 4 spaces, got {spaces} spaces.");
        }

        if (_expectIndent)
        {
            if (newIndentLevel <= _currentIndentLevel)
            {
                throw new LexerException(message: $"Expected indent after ':' at line {Line}");
            }

            AddToken(type: TokenType.Indent, text: "");
            _currentIndentLevel = newIndentLevel;
            _expectIndent = false;
            return;
        }

        // Handle dedents
        while (newIndentLevel < _currentIndentLevel)
        {
            AddToken(type: TokenType.Dedent, text: "");
            _currentIndentLevel -= 1;
        }

        // Check for unexpected indent
        if (newIndentLevel > _currentIndentLevel)
        {
            throw new LexerException(message: $"Unexpected indent at line {Line}");
        }
    }

    private void HandleNewline()
    {
        bool isSignificant = IsNewlineSignificant();

        if (isSignificant)
        {
            AddToken(type: TokenType.Newline, text: "\\n");
        }

        _hasTokenOnLine = false;
    }

    private bool IsNewlineSignificant()
    {
        if (!_hasTokenOnLine)
        {
            return false;
        }

        if (Tokens.Count == 0)
        {
            return false;
        }

        TokenType lastToken = Tokens[^1].Type;

        return lastToken switch
        {
            TokenType.LeftParen => false,
            TokenType.LeftBracket => false,
            TokenType.Comma => false,
            TokenType.Dot => false,
            TokenType.Plus => false,
            TokenType.Minus => false,
            TokenType.Star => false,
            TokenType.Slash => false,
            TokenType.Equal => false,
            TokenType.Less => false,
            TokenType.Greater => false,
            TokenType.And => false,
            TokenType.Or => false,
            TokenType.Arrow => false,
            TokenType.FatArrow => false,
            TokenType.Newline => false,
            TokenType.Colon => true, // Colon starts a block
            _ => true
        };
    }

    #endregion

    #region Cake-Specific Literal Scanning

    /// <summary>
    /// Scans a basic Cake text literal (without prefix).
    /// </summary>
    private void ScanCakeString()
    {
        ScanStringLiteral(isRaw: false, isFormatted: false);
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
    /// <param name="bitWidth">The bit width for Unicode escapes (8, 16, or 32)</param>
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


    /// <summary>
    /// Checks if a colon starts a new block by looking ahead for newlines or comments.
    /// Block-starting colons are followed by whitespace/newlines, not inline content.
    /// </summary>
    /// <returns>True if the colon starts a block and should generate an indent expectation</returns>
    private bool IsBlockStarterColon()
    {
        // A colon starts a block if it's followed by whitespace/newline and not more text
        // Look ahead to see if there's significant content after the colon on the same line
        int pos = Position;

        // Skip whitespace
        while (pos < Source.Length && (Source[index: pos] == ' ' || Source[index: pos] == '\t'))
        {
            pos++;
        }

        // If we hit end of file or newline, it's a block starter
        if (pos >= Source.Length || Source[index: pos] == '\n' || Source[index: pos] == '\r')
        {
            return true;
        }

        // If we hit a comment, it's still a block starter
        if (Source[index: pos] == '#')
        {
            return true;
        }

        // Otherwise, it's likely a type annotation
        return false;
    }

    #endregion

    #region Overridden Base Methods

    protected override void ScanIdentifier()
    {
        _hasTokenOnLine = true;
        base.ScanIdentifier();

        // Track definition keywords for script mode detection
        if (Tokens.Count > 0)
        {
            TokenType lastToken = Tokens[^1].Type;
            if (lastToken is TokenType.Recipe or TokenType.Entity or TokenType.Record
                or TokenType.Choice or TokenType.Chimera or TokenType.Variant or TokenType.Mutant
                or TokenType.Protocol)
            {
                _hasDefinitions = true;
            }
        }
    }

    /// <summary>
    /// Overrides base number scanning to use Cake's default Integer and Decimal types
    /// for unsuffixed numbers instead of IntegerLiteral and FloatLiteral.
    /// </summary>
    protected void ScanCakeNumber()
    {
        while (char.IsDigit(c: Peek()) || Peek() == '_')
        {
            Advance();
        }

        bool isFloat = false;

        if (Peek() == '.' && char.IsDigit(c: Peek(offset: 1)))
        {
            isFloat = true;
            Advance();
            while (char.IsDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }

        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            Advance();
            if (Peek() == '+' || Peek() == '-')
            {
                Advance();
            }

            while (char.IsDigit(c: Peek()))
            {
                Advance();
            }
        }

        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = Position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                Source.Substring(startIndex: suffixStart, length: Position - suffixStart);

            if (_numericSuffixToTokenType.TryGetValue(key: suffix,
                    value: out TokenType numericTokenType))
            {
                AddToken(type: numericTokenType);
            }
            else if (_memorySuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType memoryTokenType))
            {
                AddToken(type: memoryTokenType);
            }
            else if (_durationSuffixToTokenType.TryGetValue(key: suffix,
                         value: out TokenType durationTokenType))
            {
                AddToken(type: durationTokenType);
            }
            else if (suffix == "d")
            {
                AddToken(type: TokenType.Decimal);
            }
            else
            {
                throw new LexerException(message: $"Unknown suffix '{suffix}' at line {Line}");
            }
        }
        else
        {
            // Use Cake's default types: Integer for whole numbers, Decimal for floating point
            AddToken(type: isFloat
                ? TokenType.Decimal
                : TokenType.Integer);
        }
    }

    /// <summary>
    /// Overrides prefixed number scanning to use Cake's Integer type for unsuffixed numbers.
    /// </summary>
    /// <param name="isHex">True if scanning hexadecimal, false if binary</param>
    protected void ScanCakePrefixedNumber(bool isHex)
    {
        if (isHex)
        {
            while (IsHexDigit(c: Peek()) || Peek() == '_')
            {
                Advance();
            }
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
            {
                Advance();
            }
        }

        if (char.IsLetter(c: Peek()))
        {
            int suffixStart = Position;
            while (char.IsLetterOrDigit(c: Peek()))
            {
                Advance();
            }

            string suffix =
                Source.Substring(startIndex: suffixStart, length: Position - suffixStart);
            if (_numericSuffixToTokenType.TryGetValue(key: suffix, value: out TokenType tokenType))
            {
                AddToken(type: tokenType);
            }
            else
            {
                string baseType = isHex
                    ? "hex"
                    : "binary";
                throw new LexerException(
                    message: $"Unknown {baseType} suffix '{suffix}' at line {Line}");
            }
        }
        else
        {
            // Use Cake's default Integer type for unsuffixed hex/binary numbers
            AddToken(type: TokenType.Integer);
        }
    }

    #endregion
}
