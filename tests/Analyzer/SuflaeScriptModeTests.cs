using SyntaxTree;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Suflae script mode: a <c>.sf</c> file with loose top-level STATEMENTS needs no explicit entry point —
/// the parser folds those statements (and any top-level runtime <c>var</c> declarations) into an implicit
/// <c>routine start()</c>, in source order. These lock the AST transform: synthesis, var-sweeping, the
/// no-op on a normal module file, and the explicit-start conflict.
/// </summary>
public sealed class SuflaeScriptModeTests
{
    private static RoutineDeclaration? SynthesizedStart(Program program) =>
        program.Declarations.OfType<RoutineDeclaration>().FirstOrDefault(r => r.Name == "start");

    [Fact]
    public void Parse_ScriptStatements_SynthesizesStart()
    {
        Program program = ParseSuflae("""
                                      show("a")
                                      show("b")
                                      """);

        // No loose top-level statements survive — they went into start().
        Assert.DoesNotContain(program.Declarations, d => d is Statement);

        RoutineDeclaration? start = SynthesizedStart(program);
        Assert.NotNull(start);
        var body = Assert.IsType<BlockStatement>(start!.Body);
        // Two show()s + a synthesized trailing return.
        Assert.Equal(2, body.Statements.OfType<ExpressionStatement>().Count());
        Assert.IsType<ReturnStatement>(body.Statements[^1]);
    }

    [Fact]
    public void Parse_ScriptVarDecl_SweptIntoStart()
    {
        Program program = ParseSuflae("""
                                      var x = 5
                                      show(x)
                                      """);

        // The top-level `var` must become a start-LOCAL (a DeclarationStatement in the body), not a
        // module-level declaration — otherwise `show(x)` in start could not see it.
        Assert.DoesNotContain(program.Declarations, d => d is VariableDeclaration);

        RoutineDeclaration? start = SynthesizedStart(program);
        Assert.NotNull(start);
        var body = Assert.IsType<BlockStatement>(start!.Body);
        Assert.Contains(body.Statements, s => s is DeclarationStatement { Declaration: VariableDeclaration });
        Assert.Contains(body.Statements, s => s is ExpressionStatement);
    }

    [Fact]
    public void Parse_NormalModule_NoSynthesis()
    {
        // A file with an explicit start and NO loose statements is untouched — its start is the one the
        // user wrote, and no statements are swept.
        Program program = ParseSuflae("""
                                      module Test/Mod
                                      routine start()
                                        show("hi")
                                        return
                                      """);

        Assert.DoesNotContain(program.Declarations, d => d is Statement);
        RoutineDeclaration? start = SynthesizedStart(program);
        Assert.NotNull(start);
        // The body is exactly what the user wrote (a show + a return), not a wrapper.
        var body = Assert.IsType<BlockStatement>(start!.Body);
        Assert.Equal(2, body.Statements.Count);
    }

    [Fact]
    public void Parse_LooseStatementWithExplicitStart_ReportsError()
    {
        (Program _, Compiler.Parser.Parser parser) = ParseSuflaeWithErrors("""
                                                                           show("loose")
                                                                           routine start()
                                                                             return
                                                                           """);

        Assert.True(parser.HasErrors);
        Assert.Contains(parser.GetErrors(), e => e.Contains("cannot mix top-level statements"));
    }
}
