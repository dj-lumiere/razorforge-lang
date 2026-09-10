using Builder.Diagnostics;
using Builder.Tokenizer;
using Builder.Verification.Results;
using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for lexer parser edge case.
/// </summary>
public class LexerParserEdgeCaseTests
{
    private const string RoutineTestSignature = "routine test()";
    private const string ReturnStatement = "  return";

    /// <summary>
    /// Verifies that the tokenizer rejects tabs at each major whitespace breakpoint.
    /// </summary>
    /// <param name="source">The source containing a tab.</param>
    /// <param name="expectedLine">The expected diagnostic line.</param>
    /// <param name="expectedColumn">The expected diagnostic column.</param>
    [Theory]
    [InlineData("routine test()\n\treturn\n", 2, 1)]
    [InlineData("routine test()\n \treturn\n", 2, 2)]
    [InlineData("routine test()\n  return\t\n", 2, 9)]
    [InlineData("routine\ttest()\n  return\n", 1, 8)]
    [InlineData("routine test()\n  # comment\twith tab\n  return\n", 2, 12)]
    public void Tokenize_Tabs_ThrowsInvalidCharacter(string source, int expectedLine,
        int expectedColumn)
    {
        GrammarException exception = AssertInvalidCharacter(source: source);

        Assert.Equal(expected: expectedLine, actual: exception.Line);
        Assert.Equal(expected: expectedColumn, actual: exception.Column);
    }

    /// <summary>
    /// Verifies that odd indentation widths are rejected while even widths remain valid.
    /// </summary>
    /// <param name="spaces">The number of leading spaces before return.</param>
    /// <param name="shouldParse">Whether the indentation width is valid.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(4, true)]
    public void Parse_IndentWidth_OnlyAllowsMultiplesOfTwo(int spaces, bool shouldParse)
    {
        string source = $"routine test()\n{new string(c: ' ', count: spaces)}return\n";

        if (shouldParse)
        {
            AssertParses(source: source);
            return;
        }

        GrammarException exception = Assert.Throws<GrammarException>(
            testCode: () => Tokenize(source: source));
        Assert.Equal(expected: GrammarDiagnosticCode.InconsistentIndentation,
            actual: exception.Code);
    }

    /// <summary>
    /// Verifies that the tokenizer rejects null bytes wherever they appear in source text.
    /// </summary>
    /// <param name="source">The source containing a null byte.</param>
    /// <param name="expectedLine">The expected diagnostic line.</param>
    /// <param name="expectedColumn">The expected diagnostic column.</param>
    [Theory]
    [InlineData("\0routine test()\n  return\n", 1, 1)]
    [InlineData("routine test()\n  var va\0lue = 1\n  return\n", 2, 9)]
    [InlineData("routine test()\n  var value = \"a\0b\"\n  return\n", 2, 17)]
    [InlineData("routine test()\n  return\n\0", 3, 1)]
    public void Tokenize_NullBytes_ThrowsInvalidCharacter(string source, int expectedLine,
        int expectedColumn)
    {
        GrammarException exception = AssertInvalidCharacter(source: source);

        Assert.Equal(expected: expectedLine, actual: exception.Line);
        Assert.Equal(expected: expectedColumn, actual: exception.Column);
    }

    /// <summary>
    /// Verifies that Unicode space and format characters are rejected rather than treated as source whitespace.
    /// </summary>
    /// <param name="hiddenCharacter">The unsupported Unicode character.</param>
    [Theory]
    [InlineData("\u00A0")]
    [InlineData("\u2003")]
    [InlineData("\u202F")]
    [InlineData("\u3000")]
    [InlineData("\u200B")]
    [InlineData("\u2060")]
    public void Tokenize_UnicodeWhitespaceAndFormatCharacters_ThrowInvalidCharacter(
        string hiddenCharacter)
    {
        string source = $"routine{hiddenCharacter}test()\n  return\n";

        GrammarException exception = AssertInvalidCharacter(source: source);

        Assert.Equal(expected: 1, actual: exception.Line);
        Assert.Equal(expected: 8, actual: exception.Column);
    }

    /// <summary>
    /// Verifies that the tokenizer accepts a UTF-8 BOM at the beginning of the source.
    /// </summary>
    [Fact]
    public void Parse_BomAtStart_Parses()
    {
        string source = "\uFEFFroutine test()\n  return\n";

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that a BOM is only accepted as the first character of the source.
    /// </summary>
    /// <param name="source">The source containing a non-leading BOM.</param>
    /// <param name="expectedLine">The expected diagnostic line.</param>
    /// <param name="expectedColumn">The expected diagnostic column.</param>
    [Theory]
    [InlineData("routine \uFEFFtest()\n  return\n", 1, 9)]
    [InlineData("\uFEFF\uFEFFroutine test()\n  return\n", 1, 1)]
    [InlineData("routine test()\n  \uFEFFreturn\n", 2, 3)]
    public void Tokenize_NonLeadingBom_ThrowsInvalidCharacter(string source, int expectedLine,
        int expectedColumn)
    {
        GrammarException exception = AssertInvalidCharacter(source: source);

        Assert.Equal(expected: expectedLine, actual: exception.Line);
        Assert.Equal(expected: expectedColumn, actual: exception.Column);
    }

    /// <summary>
    /// Verifies that a leading BOM does not offset token locations.
    /// </summary>
    [Fact]
    public void Tokenize_BomAtStart_DoesNotShiftTokenLocations()
    {
        string source = "\uFEFFroutine test()\n  return\n";

        Token routine = Tokenize(source: source)
           .First(predicate: token => token.Type == TokenType.Routine);

        Assert.Equal(expected: 1, actual: routine.Line);
        Assert.Equal(expected: 1, actual: routine.Column);
    }

    /// <summary>
    /// Verifies that all common line endings are normalized and parsed consistently.
    /// </summary>
    /// <param name="lineEnding">The line ending sequence to use.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Parse_LineEndings_Parses(string lineEnding)
    {
        string source = string.Join(separator: lineEnding,
            value:
            [
                RoutineTestSignature,
                "  var value = 1",
                ReturnStatement,
                ""
            ]);

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that newline normalization preserves line numbers and emits a stable token stream.
    /// </summary>
    /// <param name="lineEnding">The line ending sequence to use.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Tokenize_LineEndings_PreserveTokenLineNumbers(string lineEnding)
    {
        string source = string.Join(separator: lineEnding,
            value:
            [
                RoutineTestSignature,
                "  var value = 1",
                ReturnStatement,
                ""
            ]);

        List<Token> tokens = Tokenize(source: source);
        Token returnToken = tokens.Single(predicate: token => token.Type == TokenType.Return);

        Assert.Equal(expected: 3, actual: returnToken.Line);
        Assert.Equal(expected: 3, actual: returnToken.Column);
        Assert.Equal(expected: 3,
            actual: tokens.Count(predicate: token => token.Type == TokenType.Newline));
    }

    /// <summary>
    /// Verifies that mixed line endings normalize to the same parse behavior.
    /// </summary>
    [Fact]
    public void Parse_MixedLineEndings_Parses()
    {
        string source = "routine test()\r\n  var value = 1\r  return\n";

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the parser handles very deep but finite nesting without overflowing.
    /// </summary>
    [Fact]
    public void Parse_VeryDeepNesting_Parses()
    {
        const int nestingDepth = 128;
        var lines = new List<string> { RoutineTestSignature };

        for (int depth = 0; depth < nestingDepth; depth += 1)
        {
            lines.Add(item: $"{new string(c: ' ', count: (depth + 1) * 2)}if true");
        }

        lines.Add(item: $"{new string(c: ' ', count: (nestingDepth + 1) * 2)}pass");
        lines.Add(item: ReturnStatement);

        string source = string.Join(separator: "\n", values: lines);

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that very deep nesting emits balanced indent and dedent tokens.
    /// </summary>
    [Fact]
    public void Tokenize_VeryDeepNesting_EmitsBalancedIndentAndDedentTokens()
    {
        const int nestingDepth = 64;
        string source = CreateDeeplyNestedSource(nestingDepth: nestingDepth);

        List<Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: nestingDepth + 1,
            actual: tokens.Count(predicate: token => token.Type == TokenType.Indent));
        Assert.Equal(expected: nestingDepth + 1,
            actual: tokens.Count(predicate: token => token.Type == TokenType.Dedent));
    }

    /// <summary>
    /// Verifies that deep expression nesting inside brackets does not emit indentation tokens.
    /// </summary>
    [Fact]
    public void Tokenize_DeepBracketNesting_DoesNotEmitIndentationTokens()
    {
        const int nestingDepth = 256;
        string expression = new string(c: '(', count: nestingDepth) + "1" +
                            new string(c: ')', count: nestingDepth);
        string source = $"routine test()\n  var value = {expression}\n  return\n";

        List<Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: 1,
            actual: tokens.Count(predicate: token => token.Type == TokenType.Indent));
        Assert.Equal(expected: 1,
            actual: tokens.Count(predicate: token => token.Type == TokenType.Dedent));
    }

    /// <summary>
    /// Verifies that very long source lines do not break tokenization or parsing.
    /// </summary>
    [Fact]
    public void Parse_VeryLongLine_Parses()
    {
        string longName = "value_" + new string(c: 'x', count: 12_000);
        string source = $"routine test()\n  var {longName} = 1\n  return\n";

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the tokenizer preserves a very long identifier as a single token.
    /// </summary>
    [Fact]
    public void Tokenize_VeryLongIdentifier_PreservesTokenText()
    {
        string longName = "value_" + new string(c: 'x', count: 12_000);
        string source = $"routine test()\n  var {longName} = 1\n  return\n";

        Token identifier = Tokenize(source: source)
           .Single(predicate: token => token.Text == longName);

        Assert.Equal(expected: TokenType.Identifier, actual: identifier.Type);
    }

    /// <summary>
    /// Verifies that very long comments do not affect the following logical line.
    /// </summary>
    [Fact]
    public void Parse_VeryLongCommentLine_ParsesFollowingStatement()
    {
        string source = "routine test()\n  #" + new string(c: 'x', count: 25_000) + "\n  return\n";

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that an empty source file parses to an empty program instead of crashing.
    /// </summary>
    [Fact]
    public void Parse_EmptySourceFile_ProducesEmptyProgram()
    {
        Program program = Parse(source: "");

        Assert.NotNull(@object: program);
        Assert.Empty(collection: program.Declarations);
    }

    /// <summary>
    /// Verifies that whitespace-only source files compile to an empty program.
    /// </summary>
    /// <param name="source">The source containing only allowed whitespace.</param>
    [Theory]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("\uFEFF")]
    public void Parse_WhitespaceOnlySourceFile_ProducesEmptyProgram(string source)
    {
        Program program = Parse(source: source);

        Assert.NotNull(@object: program);
        Assert.Empty(collection: program.Declarations);
    }

    /// <summary>
    /// Verifies that comment-only source files compile to an empty program.
    /// </summary>
    [Fact]
    public void Parse_CommentOnlySourceFile_ProducesEmptyProgram()
    {
        Program program = Parse(source: "# comment\n  # indented comment\n");

        Assert.NotNull(@object: program);
        Assert.Empty(collection: program.Declarations);
    }

    /// <summary>
    /// Verifies that the parser accepts trailing whitespace within block successfully.
    /// </summary>
    [Fact]
    public void Parse_TrailingWhitespaceWithinBlock_Parses()
    {
        string source = "routine test()   \n  var x = 1   \n  return   \n";

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the parser accepts empty lines within block successfully.
    /// </summary>
    [Fact]
    public void Parse_EmptyLinesWithinBlock_Parses()
    {
        string source = """
                        routine test()

                          var x = 1

                          return
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the parser accepts continuation line after binary operator successfully.
    /// </summary>
    [Fact]
    public void Parse_ContinuationLineAfterBinaryOperator_Parses()
    {
        string source = """
                        routine total() -> S32
                          return 1 +
                          2
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the parser accepts deeply nested scopes successfully.
    /// </summary>
    [Fact]
    public void Parse_DeeplyNestedScopes_Parses()
    {
        string source = """
                        routine test(items: List[S32]) -> S32
                          if true
                            while true
                              each item in items
                                when item
                                  == 0 => return 0
                                  else => return item
                          return 1
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the tokenizer handles dedent jumping multiple levels emits multiple dedents.
    /// </summary>
    [Fact]
    public void Tokenize_DedentJumpingMultipleLevels_EmitsMultipleDedents()
    {
        string source = """
                        routine test()
                          if true
                            if true
                              var x = 1
                          return
                        """;

        List<Token> tokens = Tokenize(source: source);
        int dedentCount = tokens.Count(predicate: t => t.Type == TokenType.Dedent);

        Assert.True(condition: dedentCount >= 2,
            userMessage: $"Expected at least 2 dedent tokens, got {dedentCount}.");
    }

    /// <summary>
    /// Verifies that the parser accepts comment only lines with unusual indentation successfully.
    /// </summary>
    [Fact]
    public void Parse_CommentOnlyLinesWithUnusualIndentation_Parses()
    {
        string source = """
                        routine test()
                          # regular comment
                                # oddly indented comment-only line
                          var x = 1
                          return
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the parser accepts eof without final newline successfully.
    /// </summary>
    [Fact]
    public void Parse_EofWithoutFinalNewline_Parses()
    {
        string source = "routine test()\n  return";

        Program program = Parse(source: source);

        Assert.NotNull(@object: program);
        Assert.NotEmpty(collection: program.Declarations);
    }

    /// <summary>
    /// Verifies that the parser accepts empty routine body and fails in the expected way.
    /// </summary>
    [Fact]
    public void Parse_EmptyRoutineBody_Fails()
    {
        string source = """
                        routine test()
                        routine other()
                          pass
                          return
                        """;

        AssertParseError(source: source);
    }

    /// <summary>
    /// Verifies semantic analysis behavior for empty type body and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_EmptyTypeBody_ReportsError()
    {
        string source = """
                        record Empty
                        record Other
                          pass
                        """;

        AnalysisResult result = Analyze(source: source);

        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyBlockWithoutPass);
    }

    private static GrammarException AssertInvalidCharacter(string source)
    {
        GrammarException exception = Assert.Throws<GrammarException>(
            testCode: () => Tokenize(source: source));
        Assert.Equal(expected: GrammarDiagnosticCode.InvalidCharacter, actual: exception.Code);
        return exception;
    }

    private static string CreateDeeplyNestedSource(int nestingDepth)
    {
        var lines = new List<string> { RoutineTestSignature };

        for (int depth = 0; depth < nestingDepth; depth += 1)
        {
            lines.Add(item: $"{new string(c: ' ', count: (depth + 1) * 2)}if true");
        }

        lines.Add(item: $"{new string(c: ' ', count: (nestingDepth + 1) * 2)}pass");
        lines.Add(item: ReturnStatement);

        return string.Join(separator: "\n", values: lines);
    }
}
