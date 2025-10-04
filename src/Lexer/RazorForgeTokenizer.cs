using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Lexer;

/// <summary>
/// Tokenizer implementation for the RazorForge programming language.
/// Handles RazorForge-specific syntax including braced blocks, overflow operators,
/// and various text literal prefixes. Uses semicolons as optional statement terminators.
/// </summary>
public class RazorForgeTokenizer : BaseTokenizer
{
    /// <summary>Dictionary mapping RazorForge keywords to their corresponding token types</summary>
    private readonly Dictionary<string, TokenType> _keywords = new()
    {
        ["recipe"] = TokenType.recipe,
        ["choice"] = TokenType.Choice,
        ["chimera"] = TokenType.Chimera,
        ["variant"] = TokenType.Variant,
        ["mutant"] = TokenType.Mutant,
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
        ["entity"] = TokenType.Entity,
        ["record"] = TokenType.Record,
        ["protocol"] = TokenType.Protocol,
        ["requires"] = TokenType.Requires,
        ["generate"] = TokenType.Generate,
        ["suspended"] = TokenType.Suspended,
        ["waitfor"] = TokenType.Waitfor,
    };

    /// <summary>
    /// Initializes a new RazorForge tokenizer with the source code to tokenize.
    /// </summary>
    /// <param name="source">The RazorForge source code text</param>
    public RazorForgeTokenizer(string source) : base(source)
    {
    }

    /// <summary>
    /// Returns the RazorForge-specific keyword mappings.
    /// </summary>
    /// <returns>Dictionary of RazorForge keywords and their token types</returns>
    protected override Dictionary<string, TokenType> GetKeywords() => _keywords;

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

        Tokens.Add(new Token(TokenType.Eof, "", Line, Column, Position));
        return Tokens;
    }

    /// <summary>
    /// Scans a single token from the current position in the source code.
    /// Handles all RazorForge-specific syntax and delegates to base entity for common constructs.
    /// </summary>
    private void ScanToken()
    {
        var c = Advance();

        switch (c)
        {
            // Whitespace (ignored)
            case ' ' or '\r' or '\t' or '\n': break;
            
            // Literals
            case '#': ScanComment(); break;
            case '"': ScanRazorForgeString(); break;
            case '\'': ScanChar(); break;
            case 'l': if (!TryParseLetterPrefix()) ScanIdentifier(); break;
            case 'r' or 'f' or 't': if (!TryParseTextPrefix()) ScanIdentifier(); break;

            // Single-character delimiters
            case '(': AddToken(TokenType.LeftParen); break;
            case ')': AddToken(TokenType.RightParen); break;
            case '[': AddToken(TokenType.LeftBracket); break;
            case ']': AddToken(TokenType.RightBracket); break;
            case '{': AddToken(TokenType.LeftBrace); break;
            case '}': AddToken(TokenType.RightBrace); break;
            case ',': AddToken(TokenType.Comma); break;
            case ';': AddToken(TokenType.Newline); break;

            // Multi-character delimiters and operators
            case '.': AddToken(Match('.') ? (Match('.') ? TokenType.DotDotDot : TokenType.DotDot) : TokenType.Dot); break;
            case ':': AddToken(Match(':') ? TokenType.DoubleColon : TokenType.Colon); break;
            case '!': AddToken(Match('=') ? TokenType.NotEqual : TokenType.Bang); break;
            case '?': AddToken(Match(':') ? TokenType.QuestionColon : TokenType.Question); break;
            
            // Arithmetic operators (delegated to specialized methods)
            case '+': ScanPlusOperator(); break;
            case '-': if (Match('>')) AddToken(TokenType.Arrow); else ScanMinusOperator(); break;
            case '*': ScanStarOperator(); break;
            case '/': ScanSlashOperator(); break;
            case '%': ScanPercentOperator(); break;
            
            // Comparison and assignment
            case '=': 
                AddToken(Match('=') ? TokenType.Equal : (Match('>') ? TokenType.FatArrow : TokenType.Assign)); break;
            case '<': 
                AddToken(Match('=') ? TokenType.LessEqual : (Match('<') ? TokenType.LeftShift : TokenType.Less)); break;
            case '>': 
                AddToken(Match('=') ? TokenType.GreaterEqual : (Match('>') ? TokenType.RightShift : TokenType.Greater)); break;

            // Single-character operators
            case '&': AddToken(TokenType.Ampersand); break;
            case '|': AddToken(TokenType.Pipe); break;
            case '^': AddToken(TokenType.Caret); break;
            case '~': AddToken(TokenType.Tilde); break;
            case '@': AddToken(TokenType.At); break;

            // Numbers
            case '0': 
                if (Match('x') || Match('X')) ScanPrefixedNumber(isHex: true);
                else if (Match('b') || Match('B')) ScanPrefixedNumber(isHex: false);
                else ScanNumber();
                break;

            default:
                if (char.IsDigit(c)) ScanNumber();
                else if (IsIdentifierStart(c)) ScanIdentifier();
                else AddToken(TokenType.Unknown);
                break;
        }
    }

    /// <summary>
    /// Scans a basic RazorForge text literal (without prefix).
    /// In RazorForge, regular text is an alias for text8 (8-bit text).
    /// </summary>
    private void ScanRazorForgeString()
    {
        ScanStringLiteralWithType(isRaw: false, isFormatted: false, TokenType.Text8Literal, 8);
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
}