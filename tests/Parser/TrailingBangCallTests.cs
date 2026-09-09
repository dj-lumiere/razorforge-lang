namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Locks down the rule that `!` belongs inside a routine name, never after a
/// call expression's closing paren. <c>memberRoutine(args)!</c> is a parse error;
/// failability is invoked via <c>memberRoutine!(args)</c> and propagates through the
/// enclosing `!`-marked routine.
/// </summary>
public class TrailingBangCallTests
{
    /// <summary>Verifies that trailing bang on a memberRoutine call (memberRoutine(args)!) produces a parse error.</summary>
    [Fact]
    public void Parse_TrailingBang_OnMemberRoutineCall_IsParseError()
    {
        // `memberRoutine(args)!` form — must fail to parse. The correct form is
        // `memberRoutine!(args)` with the bang INSIDE the routine name.
        string source = """
                        module L/Test
                        routine f!() -> S64
                          return g!()!
                        routine g!() -> S64
                          return 0_s64
                        """;
        AssertParseError(source: source);
    }

    /// <summary>Verifies that trailing bang on a constructor call (Type(from: x)!) produces a parse error.</summary>
    [Fact]
    public void Parse_TrailingBang_OnConstructor_IsParseError()
    {
        // `Type(from: x)!` form — must fail to parse. Overload resolution picks
        // the failable `$create!` automatically when the matching overload is `!`.
        string source = """
                        module L/Test
                        routine narrow!(x: S128) -> S64
                          return S64(from: x)!
                        """;
        AssertParseError(source: source);
    }

    /// <summary>Verifies that trailing bang inside an if-expression produces a parse error.</summary>
    [Fact]
    public void Parse_TrailingBang_InsideIfExpression_IsParseError()
    {
        // The original BytesIO.rf regression — `!` after a constructor call
        // inside an inline if-then-else expression. Both branches are invalid.
        string source = """
                        module L/Test
                        routine pick!(cond: Bool, a: U64, b: U64) -> S64
                          return if cond then S64(from: a)! else S64(from: b)!
                        """;
        AssertParseError(source: source);
    }

    /// <summary>Verifies that f-string interpolation containing named-argument calls parses correctly.</summary>
    [Fact]
    public void Parse_FStringInterpolation_With_NamedArgCall_OK()
    {
        // Regression: the insertion-expression lexer used to drop `:` when it
        // appeared inside nested parens (e.g. inside a named-argument call
        // within an f-string interpolation). That produced parse errors of
        // the form "Expected ')' after arguments. Expected RightParen, got
        // UndecidedInteger". Locks down the fix in Tokenizer.Literals.cs.
        string source = """
                        module L/Test
                        import IO/Console
                        routine f(value: S64) -> S64
                          return value
                        routine start()
                          show(f"called: {f(value: 2)}")
                          return
                        """;
        AssertParses(source: source);
    }

    /// <summary>Verifies that the correct memberRoutine!(args) form with bang in the name parses without error.</summary>
    [Fact]
    public void Parse_BangInMemberRoutineName_OK()
    {
        // Positive control — the correct form `memberRoutine!(args)` must parse cleanly.
        // (Goes through `Parse`, not `AssertParseError`, so any throw fails the test.)
        string source = """
                        module L/Test
                        routine f!() -> S64
                          return g!()
                        routine g!() -> S64
                          return 0_s64
                        """;
        var result = Parse(source: source);
        Assert.NotNull(result);
    }
}