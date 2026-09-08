using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for error handling.
/// </summary>
public class ErrorHandlingTests
{
    #region Failable Routine Declaration
    /// <summary>
    /// Verifies that the parser accepts failable routine with bang.
    /// </summary>

    [Fact]
    public void Parse_FailableRoutine_WithBang()
    {
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Equal(expected: "get_value", actual: routine.Name);
        Assert.True(condition: routine.IsFailable);
    }
    /// <summary>
    /// Verifies that the parser accepts failable routine with parameter.
    /// </summary>

    [Fact]
    public void Parse_FailableRoutine_WithParameter()
    {
        string source = """
                        routine parse_int!(text: Text) -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);
        Assert.Single(collection: routine.Parameters);
    }
    /// <summary>
    /// Verifies that the parser accepts failable memberRoutine with bang.
    /// </summary>

    [Fact]
    public void Parse_FailableMemberRoutine_WithBang()
    {
        string source = """
                        routine User.validate!() -> bool
                          return true
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        // Name is the BARE member; the owner lives in the structured fields, and the owner-qualified
        // composite is rebuilt via QualifiedName (name-canonicalization).
        Assert.Equal(expected: "validate", actual: routine.Name);
        Assert.Equal(expected: "User", actual: routine.OwnerName);
        Assert.Equal(expected: "validate", actual: routine.MemberRoutineName);
        Assert.Equal(expected: "User.validate", actual: routine.QualifiedName);
        Assert.True(condition: routine.IsFailable);
    }
    /// <summary>
    /// Verifies that the parser accepts non failable routine.
    /// </summary>

    [Fact]
    public void Parse_NonFailableRoutine()
    {
        string source = """
                        routine get_value() -> S32
                          return 42
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.False(condition: routine.IsFailable);
    }

    #endregion

    #region Throw Statement
    /// <summary>
    /// Verifies that the parser accepts throw statement simple.
    /// </summary>

    [Fact]
    public void Parse_ThrowStatement_Simple()
    {
        string source = """
                        routine validate!(x: S32) -> S32
                          if x < 0
                            throw ValidationError("negative value")
                          return x
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        // Find throw statement in body
        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        var thenBlock = ifStmt.ThenStatement as BlockStatement;
        Assert.NotNull(@object: thenBlock);
        ThrowStatement? throwStmt = thenBlock.Statements
                                             .OfType<ThrowStatement>()
                                             .FirstOrDefault();
        Assert.NotNull(@object: throwStmt);
    }
    /// <summary>
    /// Verifies that the parser accepts throw statement with expression.
    /// </summary>

    [Fact]
    public void Parse_ThrowStatement_WithExpression()
    {
        string source = """
                        routine fail!() -> S32
                          throw CustomError(code: 123, message: "failed")
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        ThrowStatement throwStmt = body.Statements
                                       .OfType<ThrowStatement>()
                                       .First();
        Assert.NotNull(@object: throwStmt.Error);
    }

    #endregion

    #region Absent Statement
    /// <summary>
    /// Verifies that the parser accepts absent statement.
    /// </summary>

    [Fact]
    public void Parse_AbsentStatement()
    {
        string source = """
                        routine find!(id: U64) -> User
                          if id == 0
                            absent
                          return get_user(id)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        var thenBlock = ifStmt.ThenStatement as BlockStatement;
        Assert.NotNull(@object: thenBlock);
        AbsentStatement? absentStmt = thenBlock.Statements
                                               .OfType<AbsentStatement>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: absentStmt);
    }
    /// <summary>
    /// Verifies that the parser accepts absent statement in unless.
    /// </summary>

    [Fact]
    public void Parse_AbsentStatement_InUnless()
    {
        // unless parses til IfStatement with negated condition
        string source = """
                        routine get!(key: Text) -> Value
                          unless cache.has(key)
                            absent
                          return cache.get(key)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        // Unless is parsed as an IfStatement with negated condition
        IfStatement ifStmt = body.Statements
                                 .OfType<IfStatement>()
                                 .First();
        var thenBlock = ifStmt.ThenStatement as BlockStatement;
        Assert.NotNull(@object: thenBlock);
        AbsentStatement? absentStmt = thenBlock.Statements
                                               .OfType<AbsentStatement>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: absentStmt);
    }

    #endregion

    #region Combined Throw and Absent
    /// <summary>
    /// Verifies that the parser accepts routine with both throw and absent.
    /// </summary>

    [Fact]
    public void Parse_RoutineWithBothThrowAndAbsent()
    {
        string source = """
                        routine get_user!(id: U64) -> User
                          if id == 0
                            throw ValidationError("invalid id")
                          unless database.has_user(id)
                            absent
                          return database.get_user(id)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.True(condition: routine.IsFailable);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);

        // Has both throw and absent
        bool hasThrow = body.Statements
                            .OfType<IfStatement>()
                            .Any(predicate: s => (s.ThenStatement as BlockStatement)?.Statements
                                .OfType<ThrowStatement>()
                                .Any() ?? false);
        bool hasAbsent = body.Statements
                             .OfType<IfStatement>()
                             .Any(predicate: s => (s.ThenStatement as BlockStatement)?.Statements
                                 .OfType<AbsentStatement>()
                                 .Any() ?? false);

        Assert.True(condition: hasThrow);
        Assert.True(condition: hasAbsent);
    }

    #endregion

    #region Maybe Type (?)
    /// <summary>
    /// Verifies that the parser accepts maybe return type.
    /// </summary>

    [Fact]
    public void Parse_MaybeReturnType()
    {
        string source = """
                        routine try_get(id: U64) -> User?
                          if id == 0
                            return none
                          return get_user(id)
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        // Return type should be Maybe[User] or User?
        Assert.NotNull(@object: routine.ReturnType);
    }
    /// <summary>
    /// Verifies that the parser accepts maybe parameter.
    /// </summary>

    [Fact]
    public void Parse_MaybeParameter()
    {
        string source = """
                        routine process(data: Text?)
                          pass
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        Assert.Single(collection: routine.Parameters);
    }
    /// <summary>
    /// Verifies that the parser accepts maybe variable.
    /// </summary>

    [Fact]
    public void Parse_MaybeVariable()
    {
        string source = """
                        routine foo()
                          var x: S32? = none
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region None Coalescing (??)
    /// <summary>
    /// Verifies that the parser accepts none coalescing operator.
    /// </summary>

    [Fact]
    public void Parse_NoneCoalescingOperator()
    {
        string source = """
                        routine get_or_default() -> S32
                          var value: S32? = none
                          return value ?? 42
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained none coalescing.
    /// </summary>

    [Fact]
    public void Parse_ChainedNoneCoalescing()
    {
        string source = """
                        routine get_first_available() -> S32
                          var a: S32? = none
                          var b: S32? = none
                          var c: S32 = 100
                          return a ?? b ?? c
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Pattern Matching with Error Types
    /// <summary>
    /// Verifies that the parser accepts when expression with maybe.
    /// </summary>

    [Fact]
    public void Parse_WhenExpression_WithMaybe()
    {
        string source = """
                        routine handle(value: User?)
                          when value
                            is None => show("not found")
                            else u => show(u.name)
                          return
                        """;

        Program program = AssertParses(source: source);
        RoutineDeclaration routine = GetDeclaration<RoutineDeclaration>(program: program);

        var body = routine.Body as BlockStatement;
        Assert.NotNull(@object: body);
        WhenStatement whenStmt = body.Statements
                                     .OfType<WhenStatement>()
                                     .First();
        Assert.NotNull(@object: whenStmt);
    }

    // Result[T] and Lookup[T] parameter tests moved til Analyzer/ErrorVariantGenerationTests.cs
    // They parse correctly but should be rejected by semantic analysis

    #endregion

    #region Error Cases
    /// <summary>
    /// Verifies that the parser accepts throw in non failable routine so semantic analysis can validate it.
    /// </summary>

    [Fact]
    public void Parse_ThrowInNonFailableRoutine_ShouldParse()
    {
        // Parser accepts, semantic analyzer should reject
        string source = """
                        routine not_failable()
                          throw SomeError()
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
        // Semantic analysis will catch this error
    }
    /// <summary>
    /// Verifies that the parser accepts absent in non failable routine so semantic analysis can validate it.
    /// </summary>

    [Fact]
    public void Parse_AbsentInNonFailableRoutine_ShouldParse()
    {
        // Parser accepts, semantic analyzer should reject
        string source = """
                        routine not_failable()
                          absent
                          return
                        """;

        Program program = Parse(source: source);
        Assert.NotNull(@object: program);
    }
    /// <summary>
    /// Verifies that the parser accepts throw without expression and fails in the expected way.
    /// </summary>

    [Fact]
    public void Parse_ThrowWithoutExpression_Throws()
    {
        string source = """
                        routine fail!()
                          throw
                          return
                        """;

        // Parser uses error recovery, check for errors instead of exception
        AssertParseError(source: source);
    }

    #endregion
}
