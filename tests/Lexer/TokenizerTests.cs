using System;
using System.Linq;
using Xunit;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.RazorForge.Lexer;

namespace RazorForge.Tests.Lexer
{
    /// <summary>
    /// Unit tests for the RazorForge tokenizer/lexer
    /// </summary>
    public class TokenizerTests
    {
        [Fact]
        public void TestIntegerLiterals()
        {
            // Test decimal integers
            var tokens = Tokenizer.Tokenize("42", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Integer, tokens[0].Type);
            Assert.Equal("42", tokens[0].Text);

            // Test hexadecimal
            tokens = Tokenizer.Tokenize("0xFF", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Integer, tokens[0].Type);
            Assert.Equal("0xFF", tokens[0].Text);

            // Test binary
            tokens = Tokenizer.Tokenize("0b1010", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Integer, tokens[0].Type);
            Assert.Equal("0b1010", tokens[0].Text);

            // Test octal
            tokens = Tokenizer.Tokenize("0o777", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Integer, tokens[0].Type);
            Assert.Equal("0o777", tokens[0].Text);
        }

        [Fact]
        public void TestTypedIntegerLiterals()
        {
            // Test signed integers
            var testCases = new[]
            {
                ("42s8", TokenType.S8Literal),
                ("100s16", TokenType.S16Literal),
                ("1000s32", TokenType.S32Literal),
                ("99999s64", TokenType.S64Literal),
                ("12345s128", TokenType.S128Literal),
            };

            foreach (var (input, expectedType) in testCases)
            {
                var tokens = Tokenizer.Tokenize(input, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(input, tokens[0].Text);
            }

            // Test unsigned integers
            testCases = new[]
            {
                ("255u8", TokenType.U8Literal),
                ("65535u16", TokenType.U16Literal),
                ("4294967295u32", TokenType.U32Literal),
                ("18446744073709551615u64", TokenType.U64Literal),
                ("340282366920938463463374607431768211455u128", TokenType.U128Literal),
            };

            foreach (var (input, expectedType) in testCases)
            {
                var tokens = Tokenizer.Tokenize(input, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(input, tokens[0].Text);
            }
        }

        [Fact]
        public void TestFloatingPointLiterals()
        {
            // Test decimal numbers
            var tokens = Tokenizer.Tokenize("3.14", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Decimal, tokens[0].Type);
            Assert.Equal("3.14", tokens[0].Text);

            // Test typed floats
            var floatTests = new[]
            {
                ("3.14f16", TokenType.F16Literal),
                ("2.718f32", TokenType.F32Literal),
                ("1.414f64", TokenType.F64Literal),
                ("0.577f128", TokenType.F128Literal),
            };

            foreach (var (input, expectedType) in floatTests)
            {
                tokens = Tokenizer.Tokenize(input, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(input, tokens[0].Text);
            }

            // Test decimals
            var decimalTests = new[]
            {
                ("99.99d32", TokenType.D32Literal),
                ("123.456d64", TokenType.D64Literal),
                ("789.012d128", TokenType.D128Literal),
            };

            foreach (var (input, expectedType) in decimalTests)
            {
                tokens = Tokenizer.Tokenize(input, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(input, tokens[0].Text);
            }
        }

        [Fact]
        public void TestStringLiterals()
        {
            // Test basic string
            var tokens = Tokenizer.Tokenize("\"hello world\"", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.TextLiteral, tokens[0].Type);
            Assert.Equal("\"hello world\"", tokens[0].Text);

            // Test raw string
            tokens = Tokenizer.Tokenize("r\"C:\\path\\file\"", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.RawText, tokens[0].Type);

            // Test formatted string
            tokens = Tokenizer.Tokenize("f\"Value: {x}\"", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.FormattedText, tokens[0].Type);

            // Test 8-bit string
            tokens = Tokenizer.Tokenize("t8\"ASCII\"", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Text8Literal, tokens[0].Type);

            // Test 16-bit string
            tokens = Tokenizer.Tokenize("t16\"Unicode\"", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Text16Literal, tokens[0].Type);
        }

        [Fact]
        public void TestCharacterLiterals()
        {
            // Test basic character
            var tokens = Tokenizer.Tokenize("'a'", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.LetterLiteral, tokens[0].Type);
            Assert.Equal("'a'", tokens[0].Text);

            // Test 8-bit character
            tokens = Tokenizer.Tokenize("l8'x'", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Letter8Literal, tokens[0].Type);

            // Test 16-bit character
            tokens = Tokenizer.Tokenize("l16'⌘'", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Letter16Literal, tokens[0].Type);

            // Test escape sequences
            tokens = Tokenizer.Tokenize("'\\n'", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.LetterLiteral, tokens[0].Type);
            Assert.Equal("'\\n'", tokens[0].Text);
        }

        [Fact]
        public void TestMemorySizeLiterals()
        {
            var memorySizeTests = new[]
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
                ("5pib", TokenType.PebibyteLiteral),
            };

            foreach (var (input, expectedType) in memorySizeTests)
            {
                var tokens = Tokenizer.Tokenize(input, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(input, tokens[0].Text);
            }
        }

        [Fact]
        public void TestDurationLiterals()
        {
            var durationTests = new[]
            {
                ("2w", TokenType.WeekLiteral),
                ("30d", TokenType.DayLiteral),
                ("24h", TokenType.HourLiteral),
                ("30m", TokenType.MinuteLiteral),
                ("45s", TokenType.SecondLiteral),
                ("500ms", TokenType.MillisecondLiteral),
                ("100us", TokenType.MicrosecondLiteral),
                ("50ns", TokenType.NanosecondLiteral),
            };

            foreach (var (input, expectedType) in durationTests)
            {
                var tokens = Tokenizer.Tokenize(input, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(input, tokens[0].Text);
            }
        }

        [Fact]
        public void TestKeywords()
        {
            var keywords = new[]
            {
                ("recipe", TokenType.recipe),
                ("entity", TokenType.Entity),
                ("record", TokenType.Record),
                ("choice", TokenType.Choice),
                ("chimera", TokenType.Chimera),
                ("variant", TokenType.Variant),
                ("mutant", TokenType.Mutant),
                ("danger", TokenType.Danger),
                ("mayhem", TokenType.Mayhem),
                ("protocol", TokenType.Protocol),
                ("let", TokenType.Let),
                ("var", TokenType.Var),
                ("common", TokenType.TypeWise),
                ("if", TokenType.If),
                ("elif", TokenType.Elif),
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
                ("step", TokenType.Step),
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
                ("waitfor", TokenType.Waitfor),
            };

            foreach (var (keyword, expectedType) in keywords)
            {
                var tokens = Tokenizer.Tokenize(keyword, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(keyword.ToLower(), tokens[0].Text.ToLower());
            }
        }

        [Fact]
        public void TestIdentifiers()
        {
            // Test snake_case identifier
            var tokens = Tokenizer.Tokenize("my_variable", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Identifier, tokens[0].Type);
            Assert.Equal("my_variable", tokens[0].Text);

            // Test identifier with bang
            tokens = Tokenizer.Tokenize("is_valid!", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.Identifier, tokens[0].Type);
            Assert.Equal("is_valid!", tokens[0].Text);

            // Test PascalCase type identifier
            tokens = Tokenizer.Tokenize("MyClass", Language.RazorForge);
            Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
            Assert.Equal(TokenType.TypeIdentifier, tokens[0].Type);
            Assert.Equal("MyClass", tokens[0].Text);
        }

        [Fact]
        public void TestOperators()
        {
            var operators = new[]
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
                ("=>", TokenType.FatArrow),
            };

            foreach (var (op, expectedType) in operators)
            {
                var tokens = Tokenizer.Tokenize(op, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(op, tokens[0].Text);
            }
        }

        [Fact]
        public void TestOverflowOperators()
        {
            var overflowOps = new[]
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
                ("//?", TokenType.DivideChecked),
            };

            foreach (var (op, expectedType) in overflowOps)
            {
                var tokens = Tokenizer.Tokenize(op, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(op, tokens[0].Text);
            }
        }

        [Fact]
        public void TestDelimiters()
        {
            var delimiters = new[]
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
                ("...", TokenType.DotDotDot),
            };

            foreach (var (delimiter, expectedType) in delimiters)
            {
                var tokens = Tokenizer.Tokenize(delimiter, Language.RazorForge);
                Assert.Single(tokens.Where(t => t.Type != TokenType.Eof));
                Assert.Equal(expectedType, tokens[0].Type);
                Assert.Equal(delimiter, tokens[0].Text);
            }
        }

        [Fact]
        public void TestComplexExpression()
        {
            var code = "recipe add(a: s32, b: s32) -> s32 { return a + b }";
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);

            // Verify the sequence of tokens
            var expectedTypes = new[]
            {
                TokenType.recipe,
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

            Assert.Equal(expectedTypes.Length, tokens.Count);
            for (int i = 0; i < expectedTypes.Length; i++)
            {
                Assert.Equal(expectedTypes[i], tokens[i].Type);
            }
        }

        [Fact]
        public void TestComments()
        {
            // Single-line comment
            var code = "42 # This is a comment";
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);
            Assert.Equal(2, tokens.Count); // Number and EOF
            Assert.Equal(TokenType.Integer, tokens[0].Type);

            // Documentation comment
            code = "## This is a doc comment\n42";
            tokens = Tokenizer.Tokenize(code, Language.RazorForge);
            Assert.Contains(tokens, t => t.Type == TokenType.DocComment);
            Assert.Contains(tokens, t => t.Type == TokenType.Integer);
        }

        [Fact]
        public void TestNewlinesAndIndentation()
        {
            var code = "if true:\n    42\n    43";
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);

            // Should have If, True, Colon, Newline, possible Indent, 42, Newline, 43, Eof
            Assert.Contains(tokens, t => t.Type == TokenType.If);
            Assert.Contains(tokens, t => t.Type == TokenType.True);
            Assert.Contains(tokens, t => t.Type == TokenType.Colon);
            Assert.Contains(tokens, t => t.Type == TokenType.Newline);
        }

        [Fact]
        public void TestErrorHandling()
        {
            // Test invalid character
            var code = "42 $ invalid";
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);

            // Should still tokenize what it can
            Assert.Contains(tokens, t => t.Type == TokenType.Integer);

            // May have Unknown token for $
            var unknownTokens = tokens.Where(t => t.Type == TokenType.Unknown);
            Assert.NotEmpty(unknownTokens);
        }
    }
}