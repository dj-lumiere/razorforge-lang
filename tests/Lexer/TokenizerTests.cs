using System;
using System.Linq;
using Xunit;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.RazorForge.Lexer;

namespace RazorForge.Tests.Lexer;

/// <summary>
/// Unit tests for the RazorForge tokenizer/lexer
/// </summary>
public class TokenizerTests
{
    [Fact]
    public void TestIntegerLiterals()
    {
        // Test decimal integers
        List<Token> tokens = Tokenizer.Tokenize(source: "42", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Integer, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "42", actual: tokens[index: 0].Text);

        // Test hexadecimal
        tokens = Tokenizer.Tokenize(source: "0xFF", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Integer, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "0xFF", actual: tokens[index: 0].Text);

        // Test binary
        tokens = Tokenizer.Tokenize(source: "0b1010", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Integer, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "0b1010", actual: tokens[index: 0].Text);

        // Test octal
        tokens = Tokenizer.Tokenize(source: "0o777", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Integer, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "0o777", actual: tokens[index: 0].Text);
    }

    [Fact]
    public void TestTypedIntegerLiterals()
    {
        // Test signed integers
        (string, TokenType)[] testCases = new[]
        {
            ("42s8", TokenType.S8Literal),
            ("100s16", TokenType.S16Literal),
            ("1000s32", TokenType.S32Literal),
            ("99999s64", TokenType.S64Literal),
            ("12345s128", TokenType.S128Literal)
        };

        foreach ((string input, TokenType expectedType) in testCases)
        {
            List<Token> tokens = Tokenizer.Tokenize(source: input, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: input, actual: tokens[index: 0].Text);
        }

        // Test unsigned integers
        testCases = new[]
        {
            ("255u8", TokenType.U8Literal),
            ("65535u16", TokenType.U16Literal),
            ("4294967295u32", TokenType.U32Literal),
            ("18446744073709551615u64", TokenType.U64Literal),
            ("340282366920938463463374607431768211455u128", TokenType.U128Literal)
        };

        foreach ((string input, TokenType expectedType) in testCases)
        {
            List<Token> tokens = Tokenizer.Tokenize(source: input, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: input, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestFloatingPointLiterals()
    {
        // Test decimal numbers
        List<Token> tokens = Tokenizer.Tokenize(source: "3.14", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Decimal, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "3.14", actual: tokens[index: 0].Text);

        // Test typed floats
        (string, TokenType)[] floatTests = new[]
        {
            ("3.14f16", TokenType.F16Literal),
            ("2.718f32", TokenType.F32Literal),
            ("1.414f64", TokenType.F64Literal),
            ("0.577f128", TokenType.F128Literal)
        };

        foreach ((string input, TokenType expectedType) in floatTests)
        {
            tokens = Tokenizer.Tokenize(source: input, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: input, actual: tokens[index: 0].Text);
        }

        // Test decimals
        (string, TokenType)[] decimalTests = new[]
        {
            ("99.99d32", TokenType.D32Literal),
            ("123.456d64", TokenType.D64Literal),
            ("789.012d128", TokenType.D128Literal)
        };

        foreach ((string input, TokenType expectedType) in decimalTests)
        {
            tokens = Tokenizer.Tokenize(source: input, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: input, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestStringLiterals()
    {
        // Test basic string
        List<Token> tokens =
            Tokenizer.Tokenize(source: "\"hello world\"", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.TextLiteral, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "\"hello world\"", actual: tokens[index: 0].Text);

        // Test raw string
        tokens = Tokenizer.Tokenize(source: "r\"C:\\path\\file\"", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.RawText, actual: tokens[index: 0].Type);

        // Test formatted string
        tokens = Tokenizer.Tokenize(source: "f\"Value: {x}\"", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.FormattedText, actual: tokens[index: 0].Type);

        // Test 8-bit string
        tokens = Tokenizer.Tokenize(source: "t8\"ASCII\"", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Text8Literal, actual: tokens[index: 0].Type);

        // Test 16-bit string
        tokens = Tokenizer.Tokenize(source: "t16\"Unicode\"", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Text16Literal, actual: tokens[index: 0].Type);
    }

    [Fact]
    public void TestCharacterLiterals()
    {
        // Test basic character
        List<Token> tokens = Tokenizer.Tokenize(source: "'a'", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.LetterLiteral, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "'a'", actual: tokens[index: 0].Text);

        // Test 8-bit character
        tokens = Tokenizer.Tokenize(source: "l8'x'", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Letter8Literal, actual: tokens[index: 0].Type);

        // Test 16-bit character
        tokens = Tokenizer.Tokenize(source: "l16'⌘'", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Letter16Literal, actual: tokens[index: 0].Type);

        // Test escape sequences
        tokens = Tokenizer.Tokenize(source: "'\\n'", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.LetterLiteral, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "'\\n'", actual: tokens[index: 0].Text);
    }

    [Fact]
    public void TestMemorySizeLiterals()
    {
        (string, TokenType)[] memorySizeTests = new[]
        {
            ("100b", TokenType.ByteLiteral),
            ("8kb", TokenType.KilobyteLiteral),
            ("8kib", TokenType.KibibyteLiteral),
            ("100mb", TokenType.MegabyteLiteral),
            ("100mib", TokenType.MebibyteLiteral),
            ("4gb", TokenType.GigabyteLiteral),
            ("4gib", TokenType.GibibyteLiteral),
            ("1tb", TokenType.TerabyteLiteral),
            ("1tib", TokenType.TebibyteLiteral),
            ("5pb", TokenType.PetabyteLiteral),
            ("5pib", TokenType.PebibyteLiteral)
        };

        foreach ((string input, TokenType expectedType) in memorySizeTests)
        {
            List<Token> tokens = Tokenizer.Tokenize(source: input, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: input, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestDurationLiterals()
    {
        (string, TokenType)[] durationTests = new[]
        {
            ("2w", TokenType.WeekLiteral),
            ("30d", TokenType.DayLiteral),
            ("24h", TokenType.HourLiteral),
            ("30m", TokenType.MinuteLiteral),
            ("45s", TokenType.SecondLiteral),
            ("500ms", TokenType.MillisecondLiteral),
            ("100us", TokenType.MicrosecondLiteral),
            ("50ns", TokenType.NanosecondLiteral)
        };

        foreach ((string input, TokenType expectedType) in durationTests)
        {
            List<Token> tokens = Tokenizer.Tokenize(source: input, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: input, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestKeywords()
    {
        (string, TokenType)[] keywords = new[]
        {
            ("routine", routine: TokenType.Routine),
            ("entity", TokenType.Entity),
            ("record", TokenType.Record),
            ("choice", TokenType.Choice),
            ("chimera", TokenType.Chimera),
            ("variant", TokenType.Variant),
            ("mutant", TokenType.Mutant),
            ("danger", TokenType.Danger),
            ("protocol", TokenType.Protocol),
            ("let", TokenType.Let),
            ("var", TokenType.Var),
            ("common", TokenType.TypeWise),
            ("if", TokenType.If),
            ("elseif", TokenType.Elseif),
            ("else", TokenType.Else),
            ("unless", TokenType.Unless),
            ("when", TokenType.When),
            ("is", TokenType.Is),
            ("loop", TokenType.Loop),
            ("while", TokenType.While),
            ("for", TokenType.For),
            ("break", TokenType.Break),
            ("continue", TokenType.Continue),
            ("return", TokenType.Return),
            ("import", TokenType.Import),
            ("using", TokenType.Using),
            ("as", TokenType.As),
            ("with", TokenType.With),
            ("where", TokenType.Where),
            ("in", TokenType.In),
            ("to", TokenType.To),
            ("by", TokenType.By),
            ("and", TokenType.And),
            ("or", TokenType.Or),
            ("not", TokenType.Not),
            ("true", TokenType.True),
            ("false", TokenType.False),
            ("none", TokenType.None),
            ("danger", TokenType.Danger),
            ("entity", TokenType.Entity),
            ("record", TokenType.Record),
            ("protocol", TokenType.Protocol),
            ("requires", TokenType.Requires),
            ("generate", TokenType.Generate),
            ("suspended", TokenType.Suspended),
            ("waitfor", TokenType.Waitfor)
        };

        foreach ((string keyword, TokenType expectedType) in keywords)
        {
            List<Token> tokens =
                Tokenizer.Tokenize(source: keyword, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: keyword.ToLower(),
                actual: tokens[index: 0]
                       .Text
                       .ToLower());
        }
    }

    [Fact]
    public void TestIdentifiers()
    {
        // Test snake_case identifier
        List<Token> tokens =
            Tokenizer.Tokenize(source: "my_variable", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Identifier, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "my_variable", actual: tokens[index: 0].Text);

        // Test identifier with bang
        tokens = Tokenizer.Tokenize(source: "is_valid!", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Identifier, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "is_valid!", actual: tokens[index: 0].Text);

        // Test PascalCase type identifier
        tokens = Tokenizer.Tokenize(source: "MyClass", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.TypeIdentifier, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "MyClass", actual: tokens[index: 0].Text);
    }

    [Fact]
    public void TestOperators()
    {
        (string, TokenType)[] operators = new[]
        {
            ("+", TokenType.Plus),
            ("-", TokenType.Minus),
            ("*", TokenType.Star),
            ("/", TokenType.Slash),
            ("%", TokenType.Percent),
            ("//", TokenType.Divide),
            ("**", TokenType.Power),
            ("==", TokenType.Equal),
            ("!=", TokenType.NotEqual),
            ("<", TokenType.Less),
            ("<=", TokenType.LessEqual),
            (">", TokenType.Greater),
            (">=", TokenType.GreaterEqual),
            ("&", TokenType.Ampersand),
            ("|", TokenType.Pipe),
            ("^", TokenType.Caret),
            ("~", TokenType.Tilde),
            ("<<", TokenType.LeftShift),
            (">>", TokenType.RightShift),
            ("=", TokenType.Assign),
            ("!", TokenType.Bang),
            ("?", TokenType.Question),
            ("@", TokenType.At),
            ("#", TokenType.Hash),
            ("->", TokenType.Arrow),
            ("=>", TokenType.FatArrow)
        };

        foreach ((string op, TokenType expectedType) in operators)
        {
            List<Token> tokens = Tokenizer.Tokenize(source: op, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: op, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestOverflowOperators()
    {
        (string, TokenType)[] overflowOps = new[]
        {
            ("+%", TokenType.PlusWrap),
            ("+^", TokenType.PlusSaturate),
            ("+!", TokenType.PlusUnchecked),
            ("+?", TokenType.PlusChecked),
            ("-%", TokenType.MinusWrap),
            ("-^", TokenType.MinusSaturate),
            ("-!", TokenType.MinusUnchecked),
            ("-?", TokenType.MinusChecked),
            ("*%", TokenType.MultiplyWrap),
            ("*^", TokenType.MultiplySaturate),
            ("*!", TokenType.MultiplyUnchecked),
            ("*?", TokenType.MultiplyChecked),
            ("//%", TokenType.DivideWrap),
            ("//^", TokenType.DivideSaturate),
            ("//!", TokenType.DivideUnchecked),
            ("//?", TokenType.DivideChecked)
        };

        foreach ((string op, TokenType expectedType) in overflowOps)
        {
            List<Token> tokens = Tokenizer.Tokenize(source: op, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: op, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestDelimiters()
    {
        (string, TokenType)[] delimiters = new[]
        {
            ("(", TokenType.LeftParen),
            (")", TokenType.RightParen),
            ("[", TokenType.LeftBracket),
            ("]", TokenType.RightBracket),
            ("{", TokenType.LeftBrace),
            ("}", TokenType.RightBrace),
            (".", TokenType.Dot),
            (",", TokenType.Comma),
            (":", TokenType.Colon),
            ("::", TokenType.DoubleColon),
            ("..", TokenType.DotDot),
            ("...", TokenType.DotDotDot)
        };

        foreach ((string delimiter, TokenType expectedType) in delimiters)
        {
            List<Token> tokens =
                Tokenizer.Tokenize(source: delimiter, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: delimiter, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestComplexExpression()
    {
        string code = "routine add(a: s32, b: s32) -> s32 { return a + b }";
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);

        // Verify the sequence of tokens
        TokenType[] expectedTypes = new[]
        {
            TokenType.Routine,
            TokenType.Identifier,
            TokenType.LeftParen,
            TokenType.Identifier,
            TokenType.Colon,
            TokenType.TypeIdentifier,
            TokenType.Comma,
            TokenType.Identifier,
            TokenType.Colon,
            TokenType.TypeIdentifier,
            TokenType.RightParen,
            TokenType.Arrow,
            TokenType.TypeIdentifier,
            TokenType.LeftBrace,
            TokenType.Return,
            TokenType.Identifier,
            TokenType.Plus,
            TokenType.Identifier,
            TokenType.RightBrace,
            TokenType.Eof
        };

        Assert.Equal(expected: expectedTypes.Length, actual: tokens.Count);
        for (int i = 0; i < expectedTypes.Length; i++)
        {
            Assert.Equal(expected: expectedTypes[i], actual: tokens[index: i].Type);
        }
    }

    [Fact]
    public void TestComments()
    {
        // Single-line comment
        string code = "42 # This is a comment";
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);
        Assert.Equal(expected: 2, actual: tokens.Count); // Number and EOF
        Assert.Equal(expected: TokenType.Integer, actual: tokens[index: 0].Type);

        // Documentation comment
        code = "## This is a doc comment\n42";
        tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.DocComment);
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.Integer);
    }

    [Fact]
    public void TestNewlinesAndIndentation()
    {
        string code = "if true:\n    42\n    43";
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);

        // Should have If, True, Colon, Newline, possible Indent, 42, Newline, 43, Eof
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.If);
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.True);
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.Colon);
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.Newline);
    }

    [Fact]
    public void TestErrorHandling()
    {
        // Test invalid character
        string code = "42 $ invalid";
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);

        // Should still tokenize what it can
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.Integer);

        // May have Unknown token for $
        IEnumerable<Token> unknownTokens =
            tokens.Where(predicate: t => t.Type == TokenType.Unknown);
        Assert.NotEmpty(collection: unknownTokens);
    }
}
