using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing literal and inline flow-expression parsing.
/// </summary>
public partial class Parser
{
    private bool TryParseNumericLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        // Integer literals (S32/S64/S128 and Integer for arbitrary precision)
        if (CheckAndAdvance(TokenType.UndecidedInteger,
                TokenType.UndecidedDecimal,
                TokenType.IntegerLiteral,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.S256Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral,
                TokenType.DecimalLiteral,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.F128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal,
                TokenType.J32Literal,
                TokenType.J64Literal,
                TokenType.J128Literal,
                TokenType.JnLiteral))
        {
            Token token = PeekToken(offset: -1);
            result = new LiteralExpression(Value: token.Text,
                LiteralType: token.Type,
                Location: location);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to parse a text literal (text, formatted text, raw text, bytes).
    /// </summary>
    private bool TryParseTextLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!CheckAndAdvance(TokenType.TextLiteral,
                TokenType.RawText,
                TokenType.BytesLiteral,
                TokenType.BytesRawLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        // The tokenizer (ScanStringLiteral) already stripped the surrounding delimiters and the
        // `b`/`r`/`f` prefix and processed escapes — token.Text IS the final string value. Do NOT
        // re-strip a leading/trailing quote here: a literal whose CONTENT begins and ends with `"`
        // (e.g. `"\""` → the one-char value `"`) would be corrupted, and a single `"` would compute
        // a negative Substring length and throw.
        result = new LiteralExpression(Value: token.Text,
            LiteralType: token.Type,
            Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse an inserted text expression (f-string).
    /// Consumes InsertionStart, then text segments and expression parts, until InsertionEnd.
    /// </summary>
    private bool TryParseInsertedText(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!CheckAndAdvance(type: TokenType.InsertionStart))
        {
            return false;
        }

        Token startToken = PeekToken(offset: -1);
        bool isRaw = startToken.Text.StartsWith(value: "rf");
        var parts = new List<InsertedTextPart>();

        while (!IsAtEnd && !Check(type: TokenType.InsertionEnd))
        {
            if (CheckAndAdvance(type: TokenType.TextSegment))
            {
                Token textToken = PeekToken(offset: -1);
                parts.Add(item: new TextPart(Text: textToken.Text,
                    Location: GetLocation(token: textToken)));
            }
            else if (CheckAndAdvance(type: TokenType.LeftBrace))
            {
                Token braceToken = PeekToken(offset: -1);
                SourceLocation partLocation = GetLocation(token: braceToken);

                // Parse the expression inside the braces
                Expression expr = ParseExpression();

                // Check for optional format specifier
                string? formatSpec = null;
                if (CheckAndAdvance(type: TokenType.FormatSpec))
                {
                    formatSpec = PeekToken(offset: -1)
                       .Text;
                }

                Consume(type: TokenType.RightBrace,
                    errorMessage: "Expected '}' after insertion expression");

                parts.Add(item: new ExpressionPart(Expression: expr,
                    FormatSpec: formatSpec,
                    Location: partLocation));
            }
            else
            {
                break;
            }
        }

        Consume(type: TokenType.InsertionEnd,
            errorMessage: "Expected closing '\"' for inserted text");

        result = new InsertedTextExpression(Parts: parts, IsRaw: isRaw, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a character literal.
    /// </summary>
    private bool TryParseCharacterLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!CheckAndAdvance(TokenType.CharacterLiteral, TokenType.ByteLetterLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string parseCharacter = ParseCharacterContent(value: token.Text);
        result = new LiteralExpression(Value: parseCharacter,
            LiteralType: token.Type,
            Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a ByteSize literal (bytes, kilobytes, etc.).
    /// All ByteSize literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value and validate it fits in the target type.
    /// </summary>
    private bool TryParseByteSizeLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!CheckAndAdvance(TokenType.ByteLiteral,
                TokenType.KilobyteLiteral,
                TokenType.KibibyteLiteral,
                TokenType.MegabyteLiteral,
                TokenType.MebibyteLiteral,
                TokenType.GigabyteLiteral,
                TokenType.GibibyteLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        result = new LiteralExpression(Value: token.Text,
            LiteralType: token.Type,
            Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a duration/time literal (hours, minutes, seconds, etc.).
    /// All duration literals are stored as raw strings for semantic analysis.
    /// The semantic analyzer will parse the value and validate it fits in the target type.
    /// </summary>
    private bool TryParseDurationLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!CheckAndAdvance(TokenType.WeekLiteral,
                TokenType.DayLiteral,
                TokenType.HourLiteral,
                TokenType.MinuteLiteral,
                TokenType.SecondLiteral,
                TokenType.MillisecondLiteral,
                TokenType.MicrosecondLiteral,
                TokenType.NanosecondLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        result = new LiteralExpression(Value: token.Text,
            LiteralType: token.Type,
            Location: location);
        return true;
    }

    /// <summary>
    /// Parses the content of a character literal, handling escape sequences.
    /// </summary>
    private static string ParseCharacterContent(string value)
    {
        // Determine the actual character content — strip quotes if present
        string charContent;
        int quoteStart = value.IndexOf(value: '\'');
        int quoteEnd = value.Length - 1;

        if (quoteStart >= 0 && quoteEnd > quoteStart && value[index: quoteEnd] == '\'')
        {
            charContent = value.Substring(startIndex: quoteStart + 1,
                length: quoteEnd - quoteStart - 1);
        }
        else
        {
            charContent = value;
        }

        if (charContent.StartsWith(value: "\\u") && charContent.Length == 8)
        {
            int codePoint =
                Convert.ToInt32(value: charContent.Substring(startIndex: 2), fromBase: 16);
            return char.ConvertFromUtf32(utf32: codePoint);
        }

        return charContent switch
        {
            "\\'" => "'",
            @"\\" => "\\",
            "\\n" => "\n",
            "\\t" => "\t",
            "\\r" => "\r",
            "\\0" => "\0",
            _ => charContent
        };
    }
}
