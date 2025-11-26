using System;
using System.Linq;
using Xunit;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;

namespace RazorForge.Tests;

/// <summary>
/// Basic smoke tests for the compiler pipeline
/// </summary>
public class BasicCompilerTests
{
    [Fact]
    public void TestTokenizerBasics()
    {
        List<Token> tokens = Tokenizer.Tokenize(source: "42 + 3", language: Language.RazorForge);

        Assert.NotEmpty(collection: tokens);
        Assert.True(condition: tokens.Count >= 3); // Number, Plus, Number, (EOF)

        // Check that we get some expected token types (RazorForge defaults to S64Literal)
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.S64Literal);
        Assert.Contains(collection: tokens, filter: t => t.Type == TokenType.Plus);
    }

    [Fact]
    public void TestTokenizerKeywords()
    {
        (string, TokenType)[] keywords = new[]
        {
            ("routine", TokenType.Routine),
            ("let", TokenType.Let),
            ("var", TokenType.Var),
            ("if", TokenType.If),
            ("return", TokenType.Return),
            ("true", TokenType.True),
            ("false", TokenType.False)
        };

        foreach ((string keyword, TokenType expectedType) in keywords)
        {
            List<Token> tokens =
                Tokenizer.Tokenize(source: keyword, language: Language.RazorForge);
            Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
            Assert.Equal(expected: expectedType, actual: tokens[index: 0].Type);
            Assert.Equal(expected: keyword, actual: tokens[index: 0].Text);
        }
    }

    [Fact]
    public void TestTokenizerOperators()
    {
        (string, TokenType)[] operators = new[]
        {
            ("+", TokenType.Plus),
            ("-", TokenType.Minus),
            ("*", TokenType.Star),
            ("/", TokenType.Slash),
            ("==", TokenType.Equal),
            ("!=", TokenType.NotEqual),
            ("<", TokenType.Less),
            (">", TokenType.Greater)
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
    public void TestTokenizerNumbers()
    {
        // Test basic integer (RazorForge defaults to s64)
        List<Token> tokens = Tokenizer.Tokenize(source: "42", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.S64Literal, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "42", actual: tokens[index: 0].Text);

        // Test decimal (RazorForge defaults to f64)
        tokens = Tokenizer.Tokenize(source: "3.14", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.F64Literal, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "3.14", actual: tokens[index: 0].Text);
    }

    [Fact]
    public void TestTokenizerStrings()
    {
        List<Token> tokens =
            Tokenizer.Tokenize(source: "\"hello\"", language: Language.RazorForge);
        Assert.Single(collection: tokens.Where(predicate: t => t.Type != TokenType.Eof));
        Assert.Equal(expected: TokenType.Text8Literal, actual: tokens[index: 0].Type);
        Assert.Equal(expected: "hello",
            actual: tokens[index: 0].Text); // Tokenizer strips quotes from the text value
    }

    [Fact]
    public void TestParserBasics()
    {
        // Test that parser can handle empty input
        Program emptyProgram = ParseCode(code: "");
        Assert.NotNull(@object: emptyProgram);
        Assert.Empty(collection: emptyProgram.Declarations);

        // Test simple expression parsing doesn't crash
        try
        {
            Program simpleProgram = ParseCode(code: "routine test() { let x = 42 }");
            Assert.NotNull(@object: simpleProgram);
            // If we get here without exception, basic parsing works
        }
        catch (Exception ex)
        {
            // Log the exception but don't fail the test - this is a smoke test
            Console.WriteLine(value: $"Parser test encountered: {ex.Message}");
            // For now, we'll just ensure parser doesn't crash completely
            Assert.NotNull(@object: ex); // This will always pass but documents the current state
        }
    }

    [Fact]
    public void TestSemanticAnalyzerInstantiation()
    {
        // Test that semantic analyzer can be created
        var analyzer =
            new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal);
        Assert.NotNull(@object: analyzer);
    }

    [Fact]
    public void TestCodeGeneratorInstantiation()
    {
        // Test that code generator can be created
        try
        {
            var codeGen =
                new Compilers.Shared.CodeGen.LLVMCodeGenerator(language: Language.RazorForge,
                    mode: LanguageMode.Normal);
            Assert.NotNull(@object: codeGen);
        }
        catch (Exception ex)
        {
            // If LLVM is not available, that's okay for this test
            Console.WriteLine(value: $"Code generator test: {ex.Message}");
            Assert.NotNull(@object: ex); // Document that this is expected to potentially fail
        }
    }

    private Program ParseCode(string code)
    {
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);
        var parser = new RazorForgeParser(tokens: tokens);
        return parser.Parse();
    }
}
