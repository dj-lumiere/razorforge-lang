using Compilers.Shared.Lexer;

namespace Compilers.Cake.Lexer;

/// <summary>
/// Tokenizer implementation for the Cake programming language.
/// Handles Cake-specific syntax including significant indentation (Python-style blocks),
/// colon-based block starters, and indent/dedent tokens for block structure.
/// </summary>
public class CakeTokenizer : BaseTokenizer
{
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
        ["recipe"] = TokenType.recipe,
        ["entity"] = TokenType.Entity,
        ["record"] = TokenType.Record,
        ["choice"] = TokenType.Choice,
        ["requires"] = TokenType.Requires,
        ["chimera"] = TokenType.Chimera,
        ["variant"] = TokenType.Variant,
        ["mutant"] = TokenType.Mutant,
        ["protocol"] = TokenType.Protocol,
        ["let"] = TokenType.Let,
        ["var"] = TokenType.Var,
        ["preset"] = TokenType.Preset,
        ["common"] = TokenType.TypeWise,
        ["private"] = TokenType.Private,
        ["public(family)"] = TokenType.PublicFamily,
        ["public(module)"] = TokenType.PublicModule,
        ["public"] = TokenType.Public,
        ["global"] = TokenType.Global,
        ["external"] = TokenType.External,
        ["me"] = TokenType.Self,
        ["parent"] = TokenType.Super,
        ["if"] = TokenType.If,
        ["elif"] = TokenType.Elif,
        ["else"] = TokenType.Else,
        ["then"] = TokenType.Then,
        ["unless"] = TokenType.Unless,
        ["break"] = TokenType.Break,
        ["continue"] = TokenType.Continue,
        ["return"] = TokenType.Return,
        ["for"] = TokenType.For,
        ["loop"] = TokenType.Loop,
        ["while"] = TokenType.While,
        ["when"] = TokenType.When,
        ["is"] = TokenType.Is,
        ["from"] = TokenType.From,
        ["follows"] = TokenType.Follows,
        ["import"] = TokenType.Import,
        ["define"] = TokenType.Define,
        ["using"] = TokenType.Using,
        ["as"] = TokenType.As,
        ["pass"] = TokenType.Pass,
        ["bitter"] = TokenType.Bitter,
        ["danger"] = TokenType.Danger,
        ["mayhem"] = TokenType.Mayhem,
        ["with"] = TokenType.With,
        ["where"] = TokenType.Where,
        ["isnot"] = TokenType.IsNot,
        ["notfrom"] = TokenType.NotFrom,
        ["notin"] = TokenType.NotIn,
        ["notfollows"] = TokenType.NotFollows,
        ["in"] = TokenType.In,
        ["to"] = TokenType.To,
        ["step"] = TokenType.Step,
        ["and"] = TokenType.And,
        ["or"] = TokenType.Or,
        ["not"] = TokenType.Not,
        ["true"] = TokenType.True,
        ["false"] = TokenType.False,
        ["none"] = TokenType.None,
        ["bitter"] = TokenType.Bitter,
        ["generate"] = TokenType.Generate,
        ["suspended"] = TokenType.Suspended,
        ["waitfor"] = TokenType.Waitfor,
        ["usurping"] = TokenType.Usurping,
    };

    /// <summary>
    /// Initializes a new Cake tokenizer with the source code to tokenize.
    /// </summary>
    /// <param name="source">The Cake source code text</param>
    public CakeTokenizer(string source) : base(source)
    {
    }

    public bool IsScriptMode => !_hasDefinitions;

    /// <summary>
    /// Returns the Cake-specific keyword mappings.
    /// </summary>
    /// <returns>Dictionary of Cake keywords and their token types</returns>
    protected override Dictionary<string, TokenType> GetKeywords() => _keywords;

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
        for (var i = 0; i < _currentIndentLevel; i++)
        {
            AddToken(TokenType.Dedent, "");
        }

        Tokens.Add(new Token(TokenType.Eof, "", Line, Column, Position));
        return Tokens;
    }

    /// <summary>
    /// Scans a single token from the current position, handling Cake-specific indentation rules.
    /// </summary>
    private void ScanToken()
    {
        // Handle indentation at start of line
        if (Column == 1)
        {
            HandleIndentation();
            if (IsAtEnd()) return;
        }

        // Skip non-newline whitespace and update token start
        while (Peek() == ' ' || Peek() == '\t' || Peek() == '\r') Advance();
        TokenStart = Position;
        TokenStartColumn = Column;
        var c = Advance();

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
                if (!TryParseLetterPrefix()) ScanIdentifier(); break;
            case 'r' or 'f' or 't': 
                if (!TryParseTextPrefix()) ScanIdentifier(); break;

            // Delimiters (no braces in Cake - uses indentation)
            case '(': AddToken(TokenType.LeftParen); break;
            case ')': AddToken(TokenType.RightParen); break;
            case '[': AddToken(TokenType.LeftBracket); break;
            case ']': AddToken(TokenType.RightBracket); break;
            case ',': AddToken(TokenType.Comma); break;
            
            // Multi-character delimiters
            case '.': AddToken(Match('.') ? (Match('.') ? TokenType.DotDotDot : TokenType.DotDot) : TokenType.Dot); break;
            case ':':
                if (Match(':'))
                    AddToken(TokenType.DoubleColon);
                else
                {
                    AddToken(TokenType.Colon);
                    _expectIndent = IsBlockStarterColon();
                }
                break;

            // Arithmetic operators
            case '+': ScanPlusOperator(); break;
            case '-': if (Match('>')) AddToken(TokenType.Arrow); else ScanMinusOperator(); break;
            case '*': ScanStarOperator(); break;
            case '/': ScanSlashOperator(); break;
            case '%': ScanPercentOperator(); break;
            
            // Comparison and assignment
            case '=': 
                AddToken(Match('=') ? TokenType.Equal : (Match('>') ? TokenType.FatArrow : TokenType.Assign)); break;
            case '!': AddToken(Match('=') ? TokenType.NotEqual : TokenType.Bang); break;
            case '<': 
                AddToken(Match('=') ? TokenType.LessEqual : (Match('<') ? TokenType.LeftShift : TokenType.Less)); break;
            case '>': 
                AddToken(Match('=') ? TokenType.GreaterEqual : (Match('>') ? TokenType.RightShift : TokenType.Greater)); break;

            // Single-character operators
            case '&': AddToken(TokenType.Ampersand); break;
            case '|': AddToken(TokenType.Pipe); break;
            case '^': AddToken(TokenType.Caret); break;
            case '~': AddToken(TokenType.Tilde); break;
            case '?': AddToken(Match(':') ? TokenType.QuestionColon : TokenType.Question); break;
            case '@': AddToken(TokenType.At); break;

            // Numbers
            case '0': 
                if (Match('x') || Match('X')) ScanCakePrefixedNumber(isHex: true);
                else if (Match('b') || Match('B')) ScanCakePrefixedNumber(isHex: false);
                else ScanCakeNumber();
                break;

            default:
                _hasTokenOnLine = true;
                if (char.IsDigit(c)) ScanCakeNumber();
                else if (IsIdentifierStart(c)) ScanIdentifier();
                else AddToken(TokenType.Unknown);
                break;
        }
    }

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

        var newIndentLevel = spaces / 4;

        // Check for misaligned indentation
        if (spaces % 4 != 0)
        {
            throw new LexerException(
                $"Indentation error at line {Line}: expected multiple of 4 spaces, got {spaces} spaces.");
        }

        if (_expectIndent)
        {
            if (newIndentLevel <= _currentIndentLevel)
            {
                throw new LexerException($"Expected indent after ':' at line {Line}");
            }

            AddToken(TokenType.Indent, "");
            _currentIndentLevel = newIndentLevel;
            _expectIndent = false;
            return;
        }

        // Handle dedents
        while (newIndentLevel < _currentIndentLevel)
        {
            AddToken(TokenType.Dedent, "");
            _currentIndentLevel -= 1;
        }

        // Check for unexpected indent
        if (newIndentLevel > _currentIndentLevel)
        {
            throw new LexerException($"Unexpected indent at line {Line}");
        }
    }

    private void HandleNewline()
    {
        bool isSignificant = IsNewlineSignificant();

        if (isSignificant)
        {
            AddToken(TokenType.Newline, "\\n");
        }

        _hasTokenOnLine = false;
    }

    private bool IsNewlineSignificant()
    {
        if (!_hasTokenOnLine) return false;
        if (Tokens.Count == 0) return false;

        var lastToken = Tokens[^1].Type;

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
        var startPos = Position - 1; // We already consumed first letter
        var originalPos = Position;
        var originalCol = Column;

        // Build the prefix string starting with the character we already consumed
        var prefix = Source[startPos].ToString();

        // Continue building the prefix
        while (!IsAtEnd() && char.IsLetterOrDigit(Peek()))
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
                ScanCharLiteral(TokenType.Letter8Literal, 8);
                return true;
            case "letter16":
                Advance(); // consume '\''
                ScanCharLiteral(TokenType.Letter16Literal, 16);
                return true;
            case "letter32":
                Advance(); // consume '\''
                ScanCharLiteral(TokenType.LetterLiteral, 32);
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
            ScanEscapeSequence(bitWidth);
        }
        else
        {
            Advance();
        }
        
        if (!Match('\''))
        {
            throw new LexerException($"Unterminated character literal at line {Line}");
        }
        
        AddToken(tokenType);
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
        var pos = Position;
        
        // Skip whitespace
        while (pos < Source.Length && (Source[pos] == ' ' || Source[pos] == '\t'))
        {
            pos++;
        }
        
        // If we hit end of file or newline, it's a block starter
        if (pos >= Source.Length || Source[pos] == '\n' || Source[pos] == '\r')
        {
            return true;
        }
        
        // If we hit a comment, it's still a block starter
        if (Source[pos] == '#')
        {
            return true;
        }
        
        // Otherwise, it's likely a type annotation
        return false;
    }

    protected override void ScanIdentifier()
    {
        _hasTokenOnLine = true;
        base.ScanIdentifier();

        // Track definition keywords for script mode detection
        if (Tokens.Count > 0)
        {
            var lastToken = Tokens[^1].Type;
            if (lastToken is TokenType.recipe or TokenType.Entity or TokenType.Record or TokenType.Choice
                or TokenType.Chimera or TokenType.Variant or TokenType.Mutant or TokenType.Protocol)
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
        while (char.IsDigit(Peek()) || Peek() == '_')
            Advance();

        var isFloat = false;

        if (Peek() == '.' && char.IsDigit(Peek(1)))
        {
            isFloat = true;
            Advance();
            while (char.IsDigit(Peek()) || Peek() == '_')
                Advance();
        }

        if (Peek() == 'e' || Peek() == 'E')
        {
            isFloat = true;
            Advance();
            if (Peek() == '+' || Peek() == '-')
                Advance();
            while (char.IsDigit(Peek()))
                Advance();
        }

        if (char.IsLetter(Peek()))
        {
            var suffixStart = Position;
            while (char.IsLetterOrDigit(Peek()))
                Advance();

            var suffix = Source.Substring(suffixStart, Position - suffixStart);

            if (_numericSuffixToTokenType.TryGetValue(suffix, out var numericTokenType))
            {
                AddToken(numericTokenType);
            }
            else if (_memorySuffixToTokenType.TryGetValue(suffix, out var memoryTokenType))
            {
                AddToken(memoryTokenType);
            }
            else if (_durationSuffixToTokenType.TryGetValue(suffix, out var durationTokenType))
            {
                AddToken(durationTokenType);
            }
            else if (suffix == "d")
            {
                AddToken(TokenType.Decimal);
            }
            else
            {
                throw new LexerException($"Unknown suffix '{suffix}' at line {Line}");
            }
        }
        else
        {
            // Use Cake's default types: Integer for whole numbers, Decimal for floating point
            AddToken(isFloat ? TokenType.Decimal : TokenType.Integer);
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
            while (IsHexDigit(Peek()) || Peek() == '_')
                Advance();
        }
        else
        {
            while (Peek() == '0' || Peek() == '1' || Peek() == '_')
                Advance();
        }

        if (char.IsLetter(Peek()))
        {
            var suffixStart = Position;
            while (char.IsLetterOrDigit(Peek()))
                Advance();
            
            var suffix = Source.Substring(suffixStart, Position - suffixStart);
            if (_numericSuffixToTokenType.TryGetValue(suffix, out var tokenType))
            {
                AddToken(tokenType);
            }
            else
            {
                var baseType = isHex ? "hex" : "binary";
                throw new LexerException($"Unknown {baseType} suffix '{suffix}' at line {Line}");
            }
        }
        else
        {
            // Use Cake's default Integer type for unsuffixed hex/binary numbers
            AddToken(TokenType.Integer);
        }
    }
}