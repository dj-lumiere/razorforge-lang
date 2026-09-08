using Compiler.Tokenizer;
using SyntaxTree;

namespace RazorForge.Tests.Parser;

using static TestHelpers;

/// <summary>
/// Contains tests for expression.
/// </summary>
public class ExpressionTests
{
    #region Routine Call Tests
    /// <summary>
    /// Verifies that the parser accepts simple memberRoutine call.
    /// </summary>

    [Fact]
    public void Parse_SimpleMemberRoutineCall()
    {
        string source = """
                        routine test()
                          show("hello")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts memberRoutine call with multiple args.
    /// </summary>

    [Fact]
    public void Parse_memberRoutineCallWithMultipleArgs()
    {
        string source = """
                        routine test()
                          compute(1, 2, 3)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts memberRoutine call with named args.
    /// </summary>

    [Fact]
    public void Parse_memberRoutineCallWithNamedArgs()
    {
        string source = """
                        routine test()
                          create_user(name: "Alice", age: 30)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts memberRoutine call chain.
    /// </summary>

    [Fact]
    public void Parse_memberRoutineCallChain()
    {
        string source = """
                        routine test()
                          data.where().select().to_list()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts memberRoutine call on literal.
    /// </summary>

    [Fact]
    public void Parse_memberRoutineCallOnLiteral()
    {
        string source = """
                        routine test()
                          var len = "hello".length()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts memberRoutine call with conversion.
    /// </summary>

    [Fact]
    public void Parse_memberRoutineCallWithConversion()
    {
        string source = """
                        routine test()
                          var result = "42".S32!()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts static memberRoutine call.
    /// </summary>

    [Fact]
    public void Parse_StaticMemberRoutineCall()
    {
        string source = """
                        routine test()
                          var pi = Math.pi()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that a static call on a generic type with multiple type arguments parses —
    /// <c>Type[A, B, C].MemberRoutine(arg: x)</c>. The top-level commas mark type arguments (indexing
    /// has none), so this must not be misparsed as a single-index expression.
    /// </summary>

    [Fact]
    public void Parse_GenericTypeStaticMemberRoutineCall()
    {
        string source = """
                        routine test()
                          var v = UnpackedFloat[M, L, W].zero(sign: me.sign)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies a generic-type static access with no call arguments still parses —
    /// <c>Type[A, B].member</c>.
    /// </summary>

    [Fact]
    public void Parse_GenericTypeStaticMemberAccess()
    {
        string source = """
                        routine test()
                          var v = Pair[A, B].default
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Member Variable Access Tests
    /// <summary>
    /// Verifies that the parser accepts simple member variable access.
    /// </summary>

    [Fact]
    public void Parse_SimpleMemberVariableAccess()
    {
        string source = """
                        routine test()
                          var x = point.x
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts chained member variable access.
    /// </summary>

    [Fact]
    public void Parse_ChainedMemberVariableAccess()
    {
        string source = """
                        routine test()
                          var city = user.address.city
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts mixed member variable and memberRoutine access.
    /// </summary>

    [Fact]
    public void Parse_MixedMemberVariableAndMemberRoutineAccess()
    {
        string source = """
                        routine test()
                          var result = user.name.to_upper().length()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts me member variable access.
    /// </summary>

    [Fact]
    public void Parse_MeMemberVariableAccess()
    {
        string source = """
                        @readonly
                        routine Point.get_x() -> F32
                          return me.x
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Indexing Tests
    /// <summary>
    /// Verifies that the parser accepts array indexing.
    /// </summary>

    [Fact]
    public void Parse_ArrayIndexing()
    {
        string source = """
                        routine test()
                          var first = items[0]
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multi dimensional indexing.
    /// </summary>

    [Fact]
    public void Parse_MultiDimensionalIndexing()
    {
        string source = """
                        routine test()
                          var cell = matrix[i][j]
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts dict indexing.
    /// </summary>

    [Fact]
    public void Parse_DictIndexing()
    {
        string source = """
                        routine test()
                          var value = dict["key"]
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts index assignment.
    /// </summary>

    [Fact]
    public void Parse_IndexAssignment()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          items[0] = 42
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Constructor Tests
    /// <summary>
    /// Verifies that the parser accepts record constructor.
    /// </summary>

    [Fact]
    public void Parse_RecordConstructor()
    {
        string source = """
                        routine test()
                          var point = Point(x: 10.0, y: 20.0)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts entity constructor.
    /// </summary>

    [Fact]
    public void Parse_EntityConstructor()
    {
        string source = """
                        routine test()
                          var user = User(name: "Alice", age: 30)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts nested constructor.
    /// </summary>

    [Fact]
    public void Parse_NestedConstructor()
    {
        string source = """
                        routine test()
                          var circle = Circle(center: Point(x: 0.0, y: 0.0), radius: 10.0)
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts generic constructor.
    /// </summary>

    [Fact]
    public void Parse_GenericConstructor()
    {
        string source = """
                        routine test()
                          var container = Container[S32](value: 42)
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Lambda and Closure Tests
    /// <summary>
    /// Verifies that the parser accepts simple lambda.
    /// </summary>

    [Fact]
    public void Parse_SimpleLambda()
    {
        string source = """
                        routine test()
                          var add = (a, b) => a + b
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts single param lambda.
    /// </summary>

    [Fact]
    public void Parse_SingleParamLambda()
    {
        string source = """
                        routine test()
                          var double = x => x * 2
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda as argument.
    /// </summary>

    [Fact]
    public void Parse_LambdaAsArgument()
    {
        string source = """
                        routine test()
                          items.select(x => x * 2)
                          return
                        """;

        AssertParses(source: source);
    }
    // Parser accepts free captures as syntactically valid. Rejecting undeclared captures
    // (variables not listed in a given clause) is the semantic analyzer's responsibility.
    /// <summary>
    /// Verifies that the parser accepts lambda with capture.
    /// </summary>
    [Fact]
    public void Parse_LambdaWithCapture()
    {
        string source = """
                        routine test()
                          var multiplier = 10
                          var scale = x => x * multiplier
                          return
                        """;

        AssertParses(source: source);
    }

    /// <summary>
    /// Verifies that the parser accepts lambda with given clause single capture.
    /// </summary>
    [Fact]
    public void Parse_LambdaWithGivenClause_SingleCapture()
    {
        string source = """
                        routine test()
                          var lo = 0
                          var hi = 100
                          var in_range = x given lo => lo <= x
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda with given clause multiple captures.
    /// </summary>

    [Fact]
    public void Parse_LambdaWithGivenClause_MultipleCaptures()
    {
        string source = """
                        routine test()
                          var lo = 0
                          var hi = 100
                          var in_range = x given (lo, hi) => lo <= x and x < hi
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda with given clause zero params.
    /// </summary>

    [Fact]
    public void Parse_LambdaWithGivenClause_ZeroParams()
    {
        string source = """
                        routine test()
                          var value = 42
                          var getter = () given value => value
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda with given clause parenthesized params.
    /// </summary>

    [Fact]
    public void Parse_LambdaWithGivenClause_ParenthesizedParams()
    {
        string source = """
                        routine test()
                          var scale = 2
                          var multiply = (x, y) given scale => (x + y) * scale
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda invalid comma before given.
    /// </summary>

    [Fact]
    public void Parse_LambdaInvalid_CommaBeforeGiven()
    {
        // x, given y => x + y - invalid, comma before 'given' breaks parsing
        string source = """
                        routine test()
                          var f = x, given y => x + y
                          return
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda invalid trailing comma in given.
    /// </summary>

    [Fact]
    public void Parse_LambdaInvalid_TrailingCommaInGiven()
    {
        // x given y, => x + y - invalid, trailing comma after capture
        string source = """
                        routine test()
                          var f = x given y, => x + y
                          return
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda invalid multiple unparenthesized captures.
    /// </summary>

    [Fact]
    public void Parse_LambdaInvalid_MultipleUnparenthesizedCaptures()
    {
        // x given y,z => x+y+z - invalid, multiple captures need parentheses
        string source = """
                        routine test()
                          var f = x given y,z => x + y + z
                          return
                        """;

        AssertParseError(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts lambda invalid multiple unparenthesized params.
    /// </summary>

    [Fact]
    public void Parse_LambdaInvalid_MultipleUnparenthesizedParams()
    {
        // x, y given z => x+y+z - invalid, multiple params need parentheses
        string source = """
                        routine test()
                          var f = x, y given z => x + y + z
                          return
                        """;

        AssertParseError(source: source);
    }

    #endregion

    #region String Interpolation Tests
    /// <summary>
    /// Verifies that the parser accepts simple interpolation.
    /// </summary>

    [Fact]
    public void Parse_SimpleInterpolation()
    {
        string source = """
                        routine test()
                          var msg = f"Hello, {name}!"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts interpolation with expression.
    /// </summary>

    [Fact]
    public void Parse_InterpolationWithExpression()
    {
        string source = """
                        routine test()
                          var msg = f"Sum: {a + b}"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multiple interpolations.
    /// </summary>

    [Fact]
    public void Parse_MultipleInterpolations()
    {
        string source = """
                        routine test()
                          var msg = f"{first} + {second} = {first + second}"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts interpolation with memberRoutine call.
    /// </summary>

    [Fact]
    public void Parse_InterpolationWithMemberRoutineCall()
    {
        string source = """
                        routine test()
                          var msg = f"Name: {user.name.to_upper()}"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts interpolation with formatting.
    /// </summary>

    [Fact]
    public void Parse_InterpolationWithFormatting()
    {
        string source = """
                        routine test()
                          var msg = f"Value: {value:0.2f}"
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Inserted Text AST Tests

    /// <summary>
    /// Helper to extract InsertedTextExpression from f-string assignment in a routine.
    /// </summary>
    private static InsertedTextExpression GetInsertedText(string source)
    {
        Program program = AssertParses(source: source);
        var routine = (RoutineDeclaration)program.Declarations[0];
        var block = (BlockStatement)routine.Body;
        var declStmt = (DeclarationStatement)block.Statements[0];
        var varDecl = (VariableDeclaration)declStmt.Declaration;
        return (InsertedTextExpression)varDecl.Initializer!;
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text simple interpolation with three inserted-text parts.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_SimpleInterpolation_HasThreeParts()
    {
        string source = """
                        routine test()
                          var msg = f"Hello, {name}!"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Equal(expected: 3, actual: expr.Parts.Count);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "Hello, ", actual: ((TextPart)expr.Parts[0]).Text);
        Assert.IsType<ExpressionPart>(expr.Parts[1]);
        Assert.IsType<IdentifierExpression>(((ExpressionPart)expr.Parts[1]).Expression);
        Assert.IsType<TextPart>(expr.Parts[2]);
        Assert.Equal(expected: "!", actual: ((TextPart)expr.Parts[2]).Text);
        Assert.False(condition: expr.IsRaw);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text multiple insertions with five inserted-text parts.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_MultipleInsertions_HasFiveParts()
    {
        string source = """
                        routine test()
                          var msg = f"{a} + {b} = {a + b}"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Equal(expected: 5, actual: expr.Parts.Count);
        Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<TextPart>(expr.Parts[1]);
        Assert.Equal(expected: " + ", actual: ((TextPart)expr.Parts[1]).Text);
        Assert.IsType<ExpressionPart>(expr.Parts[2]);
        Assert.IsType<TextPart>(expr.Parts[3]);
        Assert.Equal(expected: " = ", actual: ((TextPart)expr.Parts[3]).Text);
        Assert.IsType<ExpressionPart>(expr.Parts[4]);
        // The third expression should be a + b (binary expression)
        Assert.IsType<BinaryExpression>(((ExpressionPart)expr.Parts[4]).Expression);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text escaped braces as a single text part.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_EscapedBraces_SingleTextPart()
    {
        string source = """
                        routine test()
                          var msg = f"Set: {{1, 2}}"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Single(collection: expr.Parts);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "Set: {1, 2}", actual: ((TextPart)expr.Parts[0]).Text);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text format spec.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_FormatSpec()
    {
        string source = """
                        routine test()
                          var msg = f"{value:D2}"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Single(collection: expr.Parts);
        var exprPart = Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.Equal(expected: "D2", actual: exprPart.FormatSpec);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text nested brackets index access.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_NestedBrackets_IndexAccess()
    {
        string source = """
                        routine test()
                          var msg = f"{list[0]}"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Single(collection: expr.Parts);
        var exprPart = Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<IndexExpression>(exprPart.Expression);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text nested brackets function call.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_NestedBrackets_FunctionCall()
    {
        string source = """
                        routine test()
                          var msg = f"{compute(x, y)}"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Single(collection: expr.Parts);
        var exprPart = Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<CallExpression>(exprPart.Expression);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text no insertions as a single text part.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_NoInsertions_SingleTextPart()
    {
        string source = """
                        routine test()
                          var msg = f"plain text"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Single(collection: expr.Parts);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "plain text", actual: ((TextPart)expr.Parts[0]).Text);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text adjacent insertions.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_AdjacentInsertions()
    {
        string source = """
                        routine test()
                          var msg = f"{x}{y}"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.Equal(expected: 2, actual: expr.Parts.Count);
        Assert.IsType<ExpressionPart>(expr.Parts[0]);
        Assert.IsType<ExpressionPart>(expr.Parts[1]);
    }
    /// <summary>
    /// Verifies that the parser accepts inserted text raw formatted.
    /// </summary>

    [Fact]
    public void Parse_InsertedText_RawFormatted()
    {
        string source = """
                        routine test()
                          var msg = rf"path: {dir}\file"
                          return
                        """;

        InsertedTextExpression expr = GetInsertedText(source: source);
        Assert.True(condition: expr.IsRaw);
        Assert.Equal(expected: 3, actual: expr.Parts.Count);
        Assert.IsType<TextPart>(expr.Parts[0]);
        Assert.Equal(expected: "path: ", actual: ((TextPart)expr.Parts[0]).Text);
        Assert.IsType<ExpressionPart>(expr.Parts[1]);
        Assert.IsType<TextPart>(expr.Parts[2]);
        // Raw mode: backslash is preserved
        Assert.Contains(expectedSubstring: "\\", actualString: ((TextPart)expr.Parts[2]).Text);
    }

    #endregion

    #region Type Conversion Tests
    /// <summary>
    /// Verifies that the parser accepts type conversion memberRoutine.
    /// </summary>

    [Fact]
    public void Parse_TypeConversionMemberRoutine()
    {
        string source = """
                        routine test()
                          var x = value.S64!()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts type conversion from literal.
    /// </summary>

    [Fact]
    public void Parse_TypeConversionFromLiteral()
    {
        string source = """
                        routine test()
                          var x = 42.F64()
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Parenthesized Expression Tests
    /// <summary>
    /// Verifies that the parser accepts parenthesized expression.
    /// </summary>

    [Fact]
    public void Parse_ParenthesizedExpression()
    {
        string source = """
                        routine test()
                          var result = (a + b) * c
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts nested parentheses.
    /// </summary>

    [Fact]
    public void Parse_NestedParentheses()
    {
        string source = """
                        routine test()
                          var result = ((a + b) * (c - d)) / e
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Range Expression Tests
    /// <summary>
    /// Verifies that the parser accepts range expression.
    /// </summary>

    [Fact]
    public void Parse_RangeExpression()
    {
        string source = """
                        routine test()
                          var range = 0 til 10
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts range expression with step.
    /// </summary>

    [Fact]
    public void Parse_RangeExpressionWithStep()
    {
        string source = """
                        routine test()
                          var range = 0 til 100 by 5
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Complex Expression Tests
    /// <summary>
    /// Verifies that the parser accepts complex chained expression.
    /// </summary>

    [Fact]
    public void Parse_ComplexChainedExpression()
    {
        string source = """
                        routine test()
                          var result = items
                          .where(x => x > 0)
                          .select(x => x * 2)
                          .take(10)
                          .to_list()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts conditional expression.
    /// </summary>

    [Fact]
    public void Parse_ConditionalExpression()
    {
        string source = """
                        routine test()
                          var value = if condition then compute_a() else compute_b()
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts when as expression.
    /// </summary>

    [Fact]
    public void Parse_WhenAsExpression()
    {
        string source = """
                        routine test()
                          var description = when status
                            is PENDING => "Waiting"
                            is ACTIVE => "Running"
                            else => "Unknown"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts none coalescing chain.
    /// </summary>

    [Fact]
    public void Parse_NoneCoalescingChain()
    {
        string source = """
                        routine test()
                          var value = first_option() ?? second_option() ?? default_value
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Variant Construction Tests
    /// <summary>
    /// Verifies that the parser accepts variant construction.
    /// </summary>

    [Fact]
    public void Parse_VariantConstruction()
    {
        string source = """
                        routine test()
                          var msg = Message.TEXT("Hello")
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts variant without payload.
    /// </summary>

    [Fact]
    public void Parse_VariantWithoutPayload()
    {
        string source = """
                        routine test()
                          var msg = Message.QUIT
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Choice Value Tests
    /// <summary>
    /// Verifies that the parser accepts choice value.
    /// </summary>

    [Fact]
    public void Parse_ChoiceValue()
    {
        string source = """
                        routine test()
                          var status = Status.ACTIVE
                          return
                        """;

        AssertParses(source: source);
    }

    #endregion

    #region Unary vs Binary Operator Tests

    /// <summary>
    /// Verifies that the parser accepts binary subtraction not unary minus.
    /// </summary>
    [Fact]
    public void Parse_BinarySubtraction_NotUnaryMinus()
    {
        string source = """
                        routine test()
                          var result = 3 - 2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var varDeclTyped = (VariableDeclaration)varDecl.Declaration;
        var binary = varDeclTyped.Initializer as BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Subtract, binary!.Operator);

        // Left operand should be "3", not "-3"
        var left = binary.Left as LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2", not "-2"
        var right = binary.Right as LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    /// <summary>
    /// Verifies that the parser accepts unary minus on literal.
    /// </summary>
    [Fact]
    public void Parse_UnaryMinus_OnLiteral()
    {
        string source = """
                        routine test()
                          var result = -2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var literal = ((VariableDeclaration)varDecl.Declaration).Initializer as LiteralExpression;

        Assert.NotNull(literal);
        Assert.Equal("-2", literal!.Value);
    }

    /// <summary>
    /// Verifies that the parser accepts binary subtraction with imaginary literal.
    /// </summary>
    [Fact]
    public void Parse_BinarySubtraction_WithImaginaryLiteral()
    {
        string source = """
                        routine test()
                          var result = 3 - 2j
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var binary = ((VariableDeclaration)varDecl.Declaration).Initializer as BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Subtract, binary!.Operator);

        // Left operand should be "3"
        var left = binary.Left as LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2j", NOT "-2j"
        var right = binary.Right as LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2j", right!.Value);
    }

    /// <summary>
    /// Verifies that the parser accepts unary minus and binary subtraction.
    /// </summary>
    [Fact]
    public void Parse_UnaryMinusAndBinarySubtraction()
    {
        string source = """
                        routine test()
                          var result = -3 - 2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var binary = ((VariableDeclaration)varDecl.Declaration).Initializer as BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Subtract, binary!.Operator);

        // Left operand should be "-3" (unary minus applied til literal)
        var left = binary.Left as LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("-3", left!.Value);

        // Right operand should be "2", NOT "-2"
        var right = binary.Right as LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    /// <summary>
    /// Verifies that the parser accepts binary addition not affected by unary handling.
    /// </summary>
    [Fact]
    public void Parse_BinaryAddition_NotAffectedByUnaryHandling()
    {
        string source = """
                        routine test()
                          var result = 3 + 2
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var binary = ((VariableDeclaration)varDecl.Declaration).Initializer as BinaryExpression;

        Assert.NotNull(binary);
        Assert.Equal(BinaryOperator.Add, binary!.Operator);

        // Left operand should be "3"
        var left = binary.Left as LiteralExpression;
        Assert.NotNull(left);
        Assert.Equal("3", left!.Value);

        // Right operand should be "2"
        var right = binary.Right as LiteralExpression;
        Assert.NotNull(right);
        Assert.Equal("2", right!.Value);
    }

    #endregion

    #region Byte Literal Tests
    /// <summary>
    /// Verifies that the parser accepts byte string literal.
    /// </summary>

    [Fact]
    public void Parse_ByteStringLiteral()
    {
        string source = """
                        routine test()
                          var data = b"hello"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts byte char literal.
    /// </summary>

    [Fact]
    public void Parse_ByteCharLiteral()
    {
        string source = """
                        routine test()
                          var ch = b'A'
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts byte string hex escape.
    /// </summary>

    [Fact]
    public void Parse_ByteStringHexEscape()
    {
        string source = """
                        routine test()
                          var data = b"\x48\x65\x6C\x6C\x6F"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts byte raw string literal.
    /// </summary>

    [Fact]
    public void Parse_ByteRawStringLiteral()
    {
        string source = """
                        routine test()
                          var data = br"no escapes here \n"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the tokenizer handles byte string literal with the expected token type.
    /// </summary>

    [Fact]
    public void Tokenize_ByteStringLiteral_CorrectTokenType()
    {
        string source = """b"hello" """;
        List<Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: TokenType.BytesLiteral,
            actual: tokens[index: 0].Type);
    }
    /// <summary>
    /// Verifies that the tokenizer handles byte char literal with the expected token type.
    /// </summary>

    [Fact]
    public void Tokenize_ByteCharLiteral_CorrectTokenType()
    {
        string source = """b'A' """;
        List<Token> tokens = Tokenize(source: source);

        Assert.Equal(expected: TokenType.ByteLetterLiteral,
            actual: tokens[index: 0].Type);
    }
    /// <summary>
    /// Verifies that the tokenizer handles byte string non ascii and fails in the expected way.
    /// </summary>

    [Fact]
    public void Tokenize_ByteStringNonAscii_ThrowsError()
    {
        string source = "b\"\uD55C\uAD6D\""; // b"한국" - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }
    /// <summary>
    /// Verifies that the tokenizer handles byte string unicode escape and fails in the expected way.
    /// </summary>

    [Fact]
    public void Tokenize_ByteStringUnicodeEscape_ThrowsError()
    {
        string source = """b"\u00E9" """;

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }
    /// <summary>
    /// Verifies that the tokenizer handles byte char non ascii and fails in the expected way.
    /// </summary>

    [Fact]
    public void Tokenize_ByteCharNonAscii_ThrowsError()
    {
        string source = "b'\u00E9'"; // b'é' - non-ASCII

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }
    /// <summary>
    /// Verifies that the tokenizer handles unicode escape exactly6 digits.
    /// </summary>

    [Fact]
    public void Tokenize_UnicodeEscape_Exactly6Digits()
    {
        // \u00004E is exactly 6 hex digits — valid
        string source = "\"\\u00004E\"";

        List<Token> tokens = Tokenize(source: source);
        Assert.Equal(expected: TokenType.TextLiteral,
            actual: tokens[index: 0].Type);
    }
    /// <summary>
    /// Verifies that the tokenizer handles unicode escape too few digits and fails in the expected way.
    /// </summary>

    [Fact]
    public void Tokenize_UnicodeEscape_TooFewDigits_ThrowsError()
    {
        // \u00E9 is only 4 hex digits — should fail (need exactly 6)
        string source = "\"\\u00E9\"";

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }
    /// <summary>
    /// Verifies that the tokenizer handles unicode escape two digits and fails in the expected way.
    /// </summary>

    [Fact]
    public void Tokenize_UnicodeEscape_TwoDigits_ThrowsError()
    {
        // \u41 is only 2 hex digits — should fail
        string source = "\"\\u41\"";

        Assert.ThrowsAny<Exception>(testCode: () => Tokenize(source: source));
    }

    #endregion

    #region With Expression Tests
    /// <summary>
    /// Verifies that the parser accepts with member variable update.
    /// </summary>

    [Fact]
    public void Parse_WithMemberVariableUpdate()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts with multiple fields.
    /// </summary>

    [Fact]
    public void Parse_WithMultipleFields()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts with index update.
    /// </summary>

    [Fact]
    public void Parse_WithIndexUpdate()
    {
        string source = """
                        routine test()
                          var c2 = coords with [0] = 5.0
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts with nested member variable.
    /// </summary>

    [Fact]
    public void Parse_WithNestedMemberVariable()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "Shelbyville"
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts with mixed updates.
    /// </summary>

    [Fact]
    public void Parse_WithMixedUpdates()
    {
        string source = """
                        routine test()
                          var p2 = data with .name = "test", [0] = 42
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts with expression into the expected AST shape.
    /// </summary>

    [Fact]
    public void Parse_WithExpression_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = p1 with .x = 5.0, .y = 3.0
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var withExpr = ((VariableDeclaration)varDecl.Declaration).Initializer
            as WithExpression;

        Assert.NotNull(withExpr);
        Assert.Equal(expected: 2, actual: withExpr!.Updates.Count);
        Assert.Equal(expected: "x", actual: withExpr.Updates[0].MemberVariablePath![0]);
        Assert.Equal(expected: "y", actual: withExpr.Updates[1].MemberVariablePath![0]);
        Assert.Null(withExpr.Updates[0].Index);
        Assert.Null(withExpr.Updates[1].Index);
    }
    /// <summary>
    /// Verifies that the parser accepts with expression nested member variable path into the expected AST shape.
    /// </summary>

    [Fact]
    public void Parse_WithExpression_NestedMemberVariablePath_ASTStructure()
    {
        string source = """
                        routine test()
                          var p2 = person with .address.city = "NYC"
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var withExpr = ((VariableDeclaration)varDecl.Declaration).Initializer
            as WithExpression;

        Assert.NotNull(withExpr);
        Assert.Single(withExpr!.Updates);
        Assert.Equal(expected: 2, actual: withExpr.Updates[0].MemberVariablePath!.Count);
        Assert.Equal(expected: "address", actual: withExpr.Updates[0].MemberVariablePath![0]);
        Assert.Equal(expected: "city", actual: withExpr.Updates[0].MemberVariablePath![1]);
    }
    /// <summary>
    /// Verifies that the parser accepts with expression index update into the expected AST shape.
    /// </summary>

    [Fact]
    public void Parse_WithExpression_IndexUpdate_ASTStructure()
    {
        string source = """
                        routine test()
                          var c2 = coords with [0] = 5.0
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var withExpr = ((VariableDeclaration)varDecl.Declaration).Initializer
            as WithExpression;

        Assert.NotNull(withExpr);
        Assert.Single(withExpr!.Updates);
        Assert.Null(withExpr.Updates[0].MemberVariablePath);
        Assert.NotNull(withExpr.Updates[0].Index);
    }

    #endregion

    #region Multi-Line Bracketed Expression Tests (L21)
    /// <summary>
    /// Verifies that the parser accepts multi line constructor call with named args.
    /// </summary>

    [Fact]
    public void Parse_MultiLineConstructorCall_WithNamedArgs()
    {
        string source = """
                        routine test()
                          var result = BitList(
                            data: 0u64,
                            count: 0u64,
                            capacity: 0u64
                          )
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multi line function call with positional args.
    /// </summary>

    [Fact]
    public void Parse_MultiLineFunctionCall_WithPositionalArgs()
    {
        string source = """
                        routine test()
                          var result = compute(
                            1,
                            2,
                            3
                          )
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts nested brackets multi line.
    /// </summary>

    [Fact]
    public void Parse_NestedBrackets_MultiLine()
    {
        string source = """
                        routine test()
                          var result = f(
                            g(
                              x
                            )
                          )
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multi line list literal.
    /// </summary>

    [Fact]
    public void Parse_MultiLineListLiteral()
    {
        string source = """
                        routine test()
                          var items = [
                            1,
                            2,
                            3
                          ]
                          return
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the parser accepts multi line return with constructor.
    /// </summary>

    [Fact]
    public void Parse_MultiLineReturn_WithConstructor()
    {
        string source = """
                        routine create() -> S32
                          return BitList(
                            data: 0u64,
                            count: 0u64,
                            capacity: 0u64
                          )
                        """;

        AssertParses(source: source);
    }
    /// <summary>
    /// Verifies that the tokenizer handles multi line parens no indent dedent.
    /// </summary>

    [Fact]
    public void Tokenize_MultiLineParens_NoIndentDedent()
    {
        string source = """
                        routine test()
                          var x = f(
                            1,
                            2
                          )
                          return
                        """;

        List<Token> tokens = Tokenize(source: source);

        // Find the LeftParen and RightParen for f(...)
        int leftParenIndex = tokens.FindIndex(match: t => t is { Type: TokenType.LeftParen, Text: "(" });
        // Skip the first LeftParen (routine params), find the second one
        int callParenIndex = tokens.FindIndex(startIndex: leftParenIndex + 1, match: t => t.Type == TokenType.LeftParen);
        int rightParenIndex = tokens.FindIndex(startIndex: callParenIndex, match: t => t.Type == TokenType.RightParen);

        // Between the call parens, there should be no Indent or Dedent tokens
        for (int i = callParenIndex + 1; i < rightParenIndex; i++)
        {
            Assert.NotEqual(expected: TokenType.Indent, actual: tokens[i].Type);
            Assert.NotEqual(expected: TokenType.Dedent, actual: tokens[i].Type);
        }
    }

    #endregion

    #region Slice Expression Tests
    /// <summary>
    /// Verifies that the parser accepts regular index expression still successfully.
    /// </summary>

    [Fact]
    public void Parse_RegularIndexExpression_StillWorks()
    {
        string source = """
                        routine test()
                          var item = list[5]
                          return
                        """;

        var ast = Parse(source: source);
        var routine = ast.Declarations.OfType<RoutineDeclaration>().First();
        var block = (BlockStatement)routine.Body;
        var varDecl = block.Statements.OfType<DeclarationStatement>().First();
        var initializer = ((VariableDeclaration)varDecl.Declaration).Initializer;

        Assert.IsType<IndexExpression>(initializer);
    }

    #endregion
}
