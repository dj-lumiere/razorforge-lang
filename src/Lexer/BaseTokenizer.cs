namespace Compilers.Shared.Lexer;

/// <summary>
/// Abstract base class for lexical analyzers that tokenize source code.
/// Provides common functionality for scanning and tokenizing text literals, numbers, 
/// identifiers, and other language constructs. Concrete implementations handle 
/// language-specific tokenization rules.
/// </summary>
public abstract class BaseTokenizer
{
    /// <summary>The complete source code text being tokenized</summary>
    protected readonly string Source;
    
    /// <summary>Current character position in the source text (0-based index)</summary>
    protected int Position;
    
    /// <summary>Current line number in the source text (1-based)</summary>
    protected int Line = 1;
    
    /// <summary>Current column number in the current line (1-based)</summary>
    protected int Column = 1;
    
    /// <summary>Starting position of the current token being processed</summary>
    protected int TokenStart = 0;
    
    /// <summary>Starting column of the current token being processed</summary>
    protected int TokenStartColumn = 0;
    
    /// <summary>Starting line of the current token being processed</summary>
    protected int TokenStartLine = 0;
    
    /// <summary>List of tokens that have been successfully parsed from the source</summary>
    protected readonly List<Token> Tokens = [];

    /// <summary>
    /// Initializes a new tokenizer with the source code to be tokenized.
    /// </summary>
    /// <param name="source">The source code text to tokenize</param>
    protected BaseTokenizer(string source)
    {
        Source = source;
    }

    /// <summary>
    /// Tokenizes the entire source code and returns a list of tokens.
    /// This method must be implemented by concrete tokenizer classes.
    /// </summary>
    /// <returns>A list of tokens representing the tokenized source code</returns>
    public abstract List<Token> Tokenize();
    
    /// <summary>
    /// Returns the keyword mappings for the specific language.
    /// This method must be implemented by concrete tokenizer classes.
    /// </summary>
    /// <returns>A dictionary mapping keyword strings to their corresponding token types</returns>
    protected abstract Dictionary<string, TokenType> GetKeywords();

    // ===== COMMON HELPER METHODS =====
    
    /// <summary>
    /// Advances to the next character in the source text and returns the current character.
    /// Updates line and column position tracking for newlines and regular characters.
    /// </summary>
    /// <returns>The current character, or '\0' if at end of source</returns>
    protected char Advance()
    {
        if (IsAtEnd()) return '\0';
        char c = Source[Position];
        Position += 1;

        if (c == '\n')
        {
            Line++;
            Column = 1;
        }
        else
        {
            Column++;
        }

        return c;
    }

    /// <summary>
    /// Checks if the current character matches the expected character and advances if it does.
    /// </summary>
    /// <param name="expected">The character to match against</param>
    /// <returns>True if the character matches and was consumed, false otherwise</returns>
    protected bool Match(char expected)
    {
        if (IsAtEnd()) return false;
        if (Source[Position] != expected) return false;
        Position++;
        Column++;
        return true;
    }

    /// <summary>
    /// Peeks at a character ahead without consuming it.
    /// </summary>
    /// <param name="offset">How many characters ahead to look (default 0 = current)</param>
    /// <returns>The character at the specified offset, or '\0' if beyond end</returns>
    protected char Peek(int offset = 0)
    {
        var pos = Position + offset;
        if (pos >= Source.Length) return '\0';
        return Source[pos];
    }

    /// <summary>Checks if the tokenizer has reached the end of the source text</summary>
    /// <returns>True if at or beyond the end of source</returns>
    protected bool IsAtEnd() => Position >= Source.Length;

    /// <summary>
    /// Adds a token of the specified type using the text from tokenStart to current position.
    /// </summary>
    /// <param name="type">The type of token to create</param>
    protected void AddToken(TokenType type)
    {
        var text = Source.Substring(TokenStart, Position - TokenStart);
        AddToken(type, text);
    }

    /// <summary>
    /// Adds a token with the specified type and text content.
    /// </summary>
    /// <param name="type">The type of token to create</param>
    /// <param name="text">The text content of the token</param>
    protected void AddToken(TokenType type, string text)
    {
        Tokens.Add(new Token(type, text, TokenStartLine, TokenStartColumn, TokenStart));
    }

    // ===== COMMON SCANNING METHODS =====
    
    /// <summary>
    /// Scans a numeric literal, handling integers, floats, and suffixed numbers.
    /// Supports decimal points, scientific notation, and type suffixes (i32, f64, etc.).
    /// </summary>
    protected void ScanNumber()
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
            else
            {
                throw new LexerException($"Unknown suffix '{suffix}' at line {Line}");
            }
        }
        else
        {
            // RazorForge defaults: s64 for integers, f64 for floats
            AddToken(isFloat
                ? TokenType.F64Literal
                : TokenType.S64Literal);
        }
    }

    /// <summary>
    /// Continues scanning a number that started with '0x' (hex) or '0b' (binary) prefix.
    /// Supports underscores for digit separation and type suffixes.
    /// </summary>
    /// <param name="isHex">True if scanning hexadecimal, false if binary</param>
    protected void ScanPrefixedNumber(bool isHex)
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
            // RazorForge default for unsuffixed hex/binary: s64
            AddToken(TokenType.S64Literal);
        }
    }


    /// <summary>
    /// Scans a character literal enclosed in single quotes.
    /// Handles escape sequences and validates proper termination.
    /// </summary>
    protected void ScanChar()
    {
        if (IsAtEnd())
            throw new LexerException($"Unterminated character literal at line {Line}");

        char value;
        if (Peek() == '\\')
        {
            Advance();
            value = EscapeCharacter(Advance());
        }
        else
        {
            value = Advance();
        }

        if (Peek() != '\'') // Fixed: was checking for " instead of '
            throw new LexerException($"Unterminated character literal at line {Line}");

        Advance();
        AddToken(TokenType.LetterLiteral, value.ToString());
    }

    /// <summary>
    /// Scans an escape sequence after encountering a backslash.
    /// Advances past the escape character and validates it.
    /// </summary>
    /// <param name="bitWidth">Optional bit width for Unicode escapes (8, 16, or 32)</param>
    protected void ScanEscapeSequence(int bitWidth = 32)
    {
        if (IsAtEnd())
        {
            throw new LexerException($"Unterminated escape sequence at line {Line}");
        }
        
        var escapeChar = Peek();
        switch (escapeChar)
        {
            case 'n':
            case 't':
            case 'r':
            case '\\':
            case '"':
            case '\'':
            case '0':
                Advance();
                break;
            case 'u':
                Advance(); // consume 'u'
                ScanUnicodeEscape(bitWidth);
                break;
            default:
                throw new LexerException($"Invalid escape sequence '\\{escapeChar}' at line {Line}");
        }
    }
    
    /// <summary>
    /// Scans a Unicode escape sequence (\uXX, \uXXXX, or \uXXXXXXXX based on bit width).
    /// </summary>
    /// <param name="bitWidth">The bit width determining the number of hex digits (8=2, 16=4, 32=8)</param>
    private void ScanUnicodeEscape(int bitWidth)
    {
        int hexDigits = bitWidth switch
        {
            8 => 2,   // \uXX
            16 => 4,  // \uXXXX
            32 => 8,  // \uXXXXXXXX
            _ => 4    // default to 16-bit
        };
        
        for (int i = 0; i < hexDigits; i++)
        {
            if (!IsHexDigit(Peek()))
            {
                throw new LexerException($"Invalid Unicode escape sequence at line {Line}: expected {hexDigits} hex digits for {bitWidth}-bit character");
            }
            Advance();
        }
    }
    
    /// <summary>
    /// Converts an escape sequence to its actual character value.
    /// </summary>
    /// <param name="escapeStart">The starting position of the escape sequence</param>
    /// <param name="bitWidth">The bit width for Unicode escapes (8, 16, or 32)</param>
    /// <returns>The actual character represented by the escape sequence</returns>
    protected char ParseEscapeSequence(int escapeStart, int bitWidth = 32)
    {
        var c = Source[escapeStart + 1]; // Character after backslash
        
        if (c == 'u')
        {
            // Parse Unicode escape
            int hexDigits = bitWidth switch
            {
                8 => 2,
                16 => 4,
                32 => 8,
                _ => 4
            };
            
            var hexStr = Source.Substring(escapeStart + 2, hexDigits);
            var codePoint = Convert.ToInt32(hexStr, 16);
            
            // Validate code point range based on bit width
            if (bitWidth == 8 && codePoint > 0xFF)
            {
                throw new LexerException($"Unicode escape value {codePoint:X} exceeds 8-bit range at line {Line}");
            }
            else if (bitWidth == 16 && codePoint > 0xFFFF)
            {
                throw new LexerException($"Unicode escape value {codePoint:X} exceeds 16-bit range at line {Line}");
            }
            
            return (char)codePoint;
        }
        
        return EscapeCharacter(c);
    }
    
    /// <summary>
    /// Converts a simple escape sequence character to its actual character value.
    /// </summary>
    /// <param name="c">The character following the backslash</param>
    /// <returns>The actual character represented by the escape sequence</returns>
    protected char EscapeCharacter(char c)
    {
        return c switch
        {
            'n' => '\n',
            't' => '\t',
            'r' => '\r',
            '\\' => '\\',
            '"' => '"',
            '\'' => '\'',
            '0' => '\0',
            _ => c
        };
    }

    /// <summary>
    /// Scans a comment, handling both regular (#) and documentation (##) comments.
    /// Regular comments are ignored, doc comments are tokenized.
    /// </summary>
    protected void ScanComment()
    {
        if (Peek() == '#' && Peek(1) == '#')
        {
            Advance();
            Advance();

            var start = Position;
            while (Peek() != '\n' && !IsAtEnd())
                Advance();

            var text = Source.Substring(start, Position - start);
            AddToken(TokenType.DocComment, text);
        }
        else
        {
            while (Peek() != '\n' && !IsAtEnd())
                Advance();
        }
    }

    /// <summary>
    /// Scans an identifier, handling keywords, regular identifiers, and type identifiers.
    /// Supports trailing ! and ? operators and distinguishes PascalCase from snake_case.
    /// </summary>
    protected virtual void ScanIdentifier()
    {
        while (IsIdentifierPart(Peek()))
            Advance();

        var text = Source.Substring(TokenStart, Position - TokenStart);

        // Note: ! is handled as a separate token, not part of identifiers
        // (removed the automatic inclusion of ! in identifier tokens)
        if (Peek() == '?')
        {
            Advance();
            text += "?";

            if (Peek() == '?')
            {
                Advance();
                text += "?";
            }
        }

        var keywords = GetKeywords();
        if (keywords.TryGetValue(text, out var type))
        {
            AddToken(type, text);
            return;
        }
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        AddToken(char.IsUpper(text[0])
            ? TokenType.TypeIdentifier
            : TokenType.Identifier, text);
    }

    /// <summary>Maps numeric suffixes (s32, f64, etc.) to their corresponding token types</summary>
    protected readonly Dictionary<string, TokenType> _numericSuffixToTokenType = new()
    {
        ["s8"] = TokenType.S8Literal,
        ["s16"] = TokenType.S16Literal,
        ["s32"] = TokenType.S32Literal,
        ["s64"] = TokenType.S64Literal,
        ["s128"] = TokenType.S128Literal,
        ["syssint"] = TokenType.SyssintLiteral,
        ["u8"] = TokenType.U8Literal,
        ["u16"] = TokenType.U16Literal,
        ["u32"] = TokenType.U32Literal,
        ["u64"] = TokenType.U64Literal,
        ["u128"] = TokenType.U128Literal,
        ["sysuint"] = TokenType.SysuintLiteral,
        ["f16"] = TokenType.F16Literal,
        ["f32"] = TokenType.F32Literal,
        ["f64"] = TokenType.F64Literal,
        ["f128"] = TokenType.F128Literal,
        ["d32"] = TokenType.D32Literal,
        ["d64"] = TokenType.D64Literal,
        ["d128"] = TokenType.D128Literal,
    };

    /// <summary>Maps memory size suffixes (kb, mib, etc.) to their corresponding token types</summary>
    protected readonly Dictionary<string, TokenType> _memorySuffixToTokenType = new()
    {
        ["b"] = TokenType.ByteLiteral,
        ["kb"] = TokenType.KilobyteLiteral,
        ["kib"] = TokenType.KibibyteLiteral,
        ["kbit"] = TokenType.KilobitLiteral,
        ["kibit"] = TokenType.KibibitLiteral,
        ["mb"] = TokenType.MegabyteLiteral,
        ["mib"] = TokenType.MebibyteLiteral,
        ["mbit"] = TokenType.MegabitLiteral,
        ["mibit"] = TokenType.MebibitLiteral,
        ["gb"] = TokenType.GigabyteLiteral,
        ["gib"] = TokenType.GibibyteLiteral,
        ["gbit"] = TokenType.GigabitLiteral,
        ["gibit"] = TokenType.GibibitLiteral,
        ["tb"] = TokenType.TerabyteLiteral,
        ["tib"] = TokenType.TebibyteLiteral,
        ["tbit"] = TokenType.TerabitLiteral,
        ["tibit"] = TokenType.TebibitLiteral,
        ["pb"] = TokenType.PetabyteLiteral,
        ["pib"] = TokenType.PebibyteLiteral,
        ["pbit"] = TokenType.PetabitLiteral,
        ["pibit"] = TokenType.PebibitLiteral,
    };

    /// <summary>Maps duration suffixes (s, ms, h, etc.) to their corresponding token types</summary>
    protected readonly Dictionary<string, TokenType> _durationSuffixToTokenType = new()
    {
        ["w"] = TokenType.WeekLiteral,
        ["d"] = TokenType.DayLiteral,
        ["h"] = TokenType.HourLiteral,
        ["m"] = TokenType.MinuteLiteral,
        ["s"] = TokenType.SecondLiteral,
        ["ms"] = TokenType.MillisecondLiteral,
        ["us"] = TokenType.MicrosecondLiteral,
        ["ns"] = TokenType.NanosecondLiteral,
    };

    /// <summary>Maps text prefixes (f, r, text8, etc.) to their corresponding token types</summary>
    private readonly Dictionary<string, TokenType> _textPrefixToTokenType = new()
    {
        ["r"] = TokenType.RawText,
        ["f"] = TokenType.FormattedText,
        ["rf"] = TokenType.RawFormattedText,
        ["text8"] = TokenType.Text8Literal,
        ["text8r"] = TokenType.Text8RawText,
        ["text8f"] = TokenType.Text8FormattedText,
        ["text8rf"] = TokenType.Text8RawFormattedText,
        ["text16"] = TokenType.Text16Literal,
        ["text16r"] = TokenType.Text16RawText,
        ["text16f"] = TokenType.Text16FormattedText,
        ["text16rf"] = TokenType.Text16RawFormattedText,
    };

    /// <summary>List of all valid text prefixes for prefix matching during tokenization</summary>
    private List<string> _textPrefixes = ["r", "f", "rf", "text8", "text8r", "text8f", "text8rf", "text16", "text16r", "text16f", "text16rf"];

    // ===== HELPER PREDICATES =====
    
    /// <summary>Checks if a character can start an identifier (letter or underscore)</summary>
    /// <param name="c">Character to check</param>
    /// <returns>True if the character can start an identifier</returns>
    protected static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    /// <summary>Checks if a character can be part of an identifier (letter, digit, or underscore)</summary>
    /// <param name="c">Character to check</param>
    /// <returns>True if the character can be part of an identifier</returns>
    protected bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
    /// <summary>Checks if a character is a valid hexadecimal digit (0-9, A-F, a-f)</summary>
    /// <param name="c">Character to check</param>
    /// <returns>True if the character is a hex digit</returns>
    protected bool IsHexDigit(char c) =>
        char.IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    /// <summary>Checks if a string is a valid numeric type suffix (i32, f64, etc.)</summary>
    /// <param name="s">String to check</param>
    /// <returns>True if the string is a recognized numeric suffix</returns>
    protected bool IsNumericSuffix(string s) => _numericSuffixToTokenType.ContainsKey(s);

    /// <summary>Checks if a string is a valid memory size suffix (kb, mib, etc.)</summary>
    /// <param name="s">String to check</param>
    /// <returns>True if the string is a recognized memory size suffix</returns>
    protected bool IsMemorySuffix(string s) => _memorySuffixToTokenType.ContainsKey(s);

    /// <summary>Checks if a string is a valid duration suffix (s, ms, h, etc.)</summary>
    /// <param name="s">String to check</param>
    /// <returns>True if the string is a recognized duration suffix</returns>
    protected bool IsDurationSuffix(string s) => _durationSuffixToTokenType.ContainsKey(s);

    /// <summary>
    /// Attempts to parse a text prefix (r, f, t8, etc.) followed by a quoted string.
    /// Uses the _textPrefixes list to dynamically match valid prefixes.
    /// </summary>
    /// <returns>True if a valid text prefix was found and processed</returns>
    protected bool TryParseTextPrefix()
    {
        var startPos = Position - 1; // We already consumed the first character
        var originalPos = Position;
        var originalCol = Column;
        
        // Backup the first character we already consumed
        var firstChar = Source[startPos];
        var prefix = firstChar.ToString();
        
        // Try to build the longest possible prefix
        while (!IsAtEnd() && char.IsLetterOrDigit(Peek()))
        {
            var testPrefix = prefix + Peek();
            if (_textPrefixes.Any(p => p.StartsWith(testPrefix)))
            {
                prefix += Advance();
            }
            else
            {
                break;
            }
        }
        
        // Check if we found a valid prefix
        if (!_textPrefixToTokenType.ContainsKey(prefix))
        {
            // Not a valid text prefix, reset and treat as identifier
            Position = originalPos;
            Column = originalCol;
            return false;
        }
        
        // Check if this prefix is followed by a quote
        if (Peek() != '"')
        {
            // Not a text prefix, reset and treat as identifier
            Position = originalPos;
            Column = originalCol;
            return false;
        }
        
        // Consume the opening quote
        Advance();
        
        // Get the token type from the dictionary
        var tokenType = _textPrefixToTokenType[prefix];
        
        // Determine the properties for scanning
        var isRaw = prefix.Contains('r');
        var isFormatted = prefix.Contains('f');
        
        // Determine bit width from prefix
        int bitWidth = 32; // default
        if (prefix.Contains("text8")) bitWidth = 8;
        else if (prefix.Contains("text16")) bitWidth = 16;
        // text32 is the default, so all other prefixes use 32-bit
        
        // Scan the string literal with the determined properties and specific token type
        ScanStringLiteralWithType(isRaw, isFormatted, tokenType, bitWidth);
        return true;
    }

    /// <summary>
    /// Scans a text literal with specified properties, determining the appropriate token type.
    /// </summary>
    /// <param name="isRaw">Whether the text is raw (no escape processing)</param>
    /// <param name="isFormatted">Whether the text supports interpolation</param>
    protected void ScanStringLiteral(bool isRaw, bool isFormatted)
    {
        var tokenType = (isRaw, isFormatted) switch
        {
            (true, true) => TokenType.RawFormattedText,
            (true, false) => TokenType.RawText,
            (false, true) => TokenType.FormattedText,
            _ => TokenType.TextLiteral
        };
        
        ScanStringLiteralWithType(isRaw, isFormatted, tokenType, 32); // Default 32-bit for regular text
    }

    /// <summary>
    /// Scans a text literal with the specified token type, handling escape sequences and interpolation.
    /// </summary>
    /// <param name="isRaw">Whether to process escape sequences</param>
    /// <param name="isFormatted">Whether the text supports interpolation (for future use)</param>
    /// <param name="tokenType">The specific token type to create</param>
    /// <param name="bitWidth">The bit width for Unicode escapes (8, 16, or 32)</param>
    protected void ScanStringLiteralWithType(bool isRaw, bool isFormatted, TokenType tokenType, int bitWidth = 32)
    {
        var startLine = Line;
        var startColumn = Column;
        var content = new System.Text.StringBuilder();

        while (!IsAtEnd() && Peek() != '"')
        {
            if (Peek() == '\n')
            {
                content.Append('\n');
                Advance();
            }
            else if (!isRaw && Peek() == '\\')
            {
                var escapeStart = Position;
                Advance(); // consume backslash
                ScanEscapeSequence(bitWidth);
                content.Append(ParseEscapeSequence(escapeStart, bitWidth));
            }
            else
            {
                content.Append(Advance());
            }
        }

        if (IsAtEnd())
        {
            throw new LexerException($"Unterminated text starting at line {startLine}, column {startColumn}");
        }

        Advance(); // consume closing quote
        AddToken(tokenType, content.ToString());
    }
    
    /// <summary>
    /// Scans plus-based operators including overflow variants (+%, +^, +!, +?).
    /// </summary>
    protected void ScanPlusOperator()
    {
        switch (Peek())
        {
            case '%': Advance(); AddToken(TokenType.PlusWrap); break;
            case '^': Advance(); AddToken(TokenType.PlusSaturate); break;
            case '!': Advance(); AddToken(TokenType.PlusUnchecked); break;
            case '?': Advance(); AddToken(TokenType.PlusChecked); break;
            default: AddToken(TokenType.Plus); break;
        }
    }

    /// <summary>
    /// Scans minus-based operators including overflow variants (-%, -^, -!, -?).
    /// </summary>
    protected void ScanMinusOperator()
    {
        switch (Peek())
        {
            case '%': Advance(); AddToken(TokenType.MinusWrap); break;
            case '^': Advance(); AddToken(TokenType.MinusSaturate); break;
            case '!': Advance(); AddToken(TokenType.MinusUnchecked); break;
            case '?': Advance(); AddToken(TokenType.MinusChecked); break;
            default: AddToken(TokenType.Minus); break;
        }
    }

    /// <summary>
    /// Scans star-based operators including power (**) and overflow variants (*%, *^, *!, *?).
    /// </summary>
    protected void ScanStarOperator()
    {
        var isPow = Match('*');
        switch (Peek())
        {
            case '%': Advance(); AddToken(isPow ? TokenType.PowerWrap : TokenType.MultiplyWrap); break;
            case '^': Advance(); AddToken(isPow ? TokenType.PowerSaturate : TokenType.MultiplySaturate); break;
            case '!': Advance(); AddToken(isPow ? TokenType.PowerUnchecked : TokenType.MultiplyUnchecked); break;
            case '?': Advance(); AddToken(isPow ? TokenType.PowerChecked : TokenType.MultiplyChecked); break;
            default: AddToken(isPow ? TokenType.Power : TokenType.Star); break;
        }
    }

    /// <summary>
    /// Scans slash-based operators, distinguishing between division (/) and integer division (//).
    /// Includes overflow variants for integer division (//%%, //^, //!, //?).
    /// </summary>
    protected void ScanSlashOperator()
    {
        switch (Match('/'))
        {
            case false: AddToken(TokenType.Slash); break;
            default:
                switch (Peek())
                {
                    case '%': Advance(); AddToken(TokenType.DivideWrap); break;
                    case '^': Advance(); AddToken(TokenType.DivideSaturate); break;
                    case '!': Advance(); AddToken(TokenType.DivideUnchecked); break;
                    case '?': Advance(); AddToken(TokenType.DivideChecked); break;
                    default: AddToken(TokenType.Divide); break;
                }
                break;
        }
    }

    /// <summary>
    /// Scans percent-based operators including overflow variants (%%, %^, %!, %?).
    /// </summary>
    protected void ScanPercentOperator()
    {
        switch (Peek())
        {
            case '%': Advance(); AddToken(TokenType.ModuloWrap); break;
            case '^': Advance(); AddToken(TokenType.ModuloSaturate); break;
            case '!': Advance(); AddToken(TokenType.ModuloUnchecked); break;
            case '?': Advance(); AddToken(TokenType.ModuloChecked); break;
            default: AddToken(TokenType.Percent); break;
        }
    }
}