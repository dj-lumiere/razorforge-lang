using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.RazorForge.Parser;
using Compilers.Shared.Parser;

namespace RazorForge.Tests.Parser;

/// <summary>
/// Unit tests for the RazorForge parser
/// </summary>
public class ParserTests
{
    private Program ParseCode(string code)
    {
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);
        var parser = new RazorForgeParser(tokens: tokens);
        return parser.Parse();
    }

    private List<Token> TokenizeCode(string code)
    {
        return Tokenizer.Tokenize(source: code, language: Language.RazorForge);
    }

    [Fact]
    public void TestEmptyProgram()
    {
        Program program = ParseCode(code: "");
        Assert.NotNull(@object: program);
        Assert.Empty(collection: program.Declarations);
    }

    [Fact]
    public void TestSimpleRoutineDeclaration()
    {
        string code = @"routine main() { }";
        Program program = ParseCode(code: code);

        Assert.NotNull(@object: program);
        Assert.Single(collection: program.Declarations);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        Assert.Equal(expected: "main", actual: routine.Name);
        Assert.Empty(collection: routine.Parameters);
        Assert.Null(@object: routine.ReturnType);
        Assert.NotNull(@object: routine.Body);
    }

    [Fact]
    public void TestRoutineWithParameters()
    {
        string code = @"routine add(a: s32, b: s32) -> s32 { return a + b }";
        Program program = ParseCode(code: code);

        Assert.NotNull(@object: program);
        Assert.Single(collection: program.Declarations);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        Assert.Equal(expected: "add", actual: routine.Name);
        Assert.Equal(expected: 2, actual: routine.Parameters.Count);

        // Check first parameter
        Assert.Equal(expected: "a", actual: routine.Parameters[index: 0].Name);
        Assert.Equal(expected: "s32", actual: routine.Parameters[index: 0].Type?.Name);

        // Check second parameter
        Assert.Equal(expected: "b", actual: routine.Parameters[index: 1].Name);
        Assert.Equal(expected: "s32", actual: routine.Parameters[index: 1].Type?.Name);

        // Check return type
        Assert.NotNull(@object: routine.ReturnType);
        Assert.Equal(expected: "s32", actual: routine.ReturnType.Name);

        // Check body has return statement
        Assert.NotNull(@object: routine.Body);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);
    }

    [Fact]
    public void TestVariableDeclarations()
    {
        string code = @"
routine test() {
    let x = 42
    var y: s32 = 100
    var z = ""hello""
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 3, actual: bodyBlock.Statements.Count);

        // Test immutable variable
        var letStmt = bodyBlock.Statements[index: 0] as DeclarationStatement;
        Assert.NotNull(@object: letStmt);
        var letDecl = letStmt.Declaration as VariableDeclaration;
        Assert.NotNull(@object: letDecl);
        Assert.Equal(expected: "x", actual: letDecl.Name);
        Assert.False(condition: letDecl.IsMutable);
        Assert.NotNull(@object: letDecl.Initializer);

        // Test mutable variable with type
        var varStmt = bodyBlock.Statements[index: 1] as DeclarationStatement;
        Assert.NotNull(@object: varStmt);
        var varDecl = varStmt.Declaration as VariableDeclaration;
        Assert.NotNull(@object: varDecl);
        Assert.Equal(expected: "y", actual: varDecl.Name);
        Assert.True(condition: varDecl.IsMutable);
        Assert.NotNull(@object: varDecl.Type);
        Assert.NotNull(@object: varDecl.Initializer);

        // Test mutable variable without explicit type
        var varStmt2 = bodyBlock.Statements[index: 2] as DeclarationStatement;
        Assert.NotNull(@object: varStmt2);
        var varDecl2 = varStmt2.Declaration as VariableDeclaration;
        Assert.NotNull(@object: varDecl2);
        Assert.Equal(expected: "z", actual: varDecl2.Name);
        Assert.True(condition: varDecl2.IsMutable);
        Assert.NotNull(@object: varDecl2.Initializer);
    }

    [Fact]
    public void TestArithmeticExpressions()
    {
        string code = @"routine calc() { return 2 + 3 * 4 - 1 }";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);

        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        var returnStmt = bodyBlock.Statements[index: 0] as ReturnStatement;
        Assert.NotNull(@object: returnStmt);
        Assert.NotNull(@object: returnStmt.Expression);

        // The expression should be a binary expression
        var expr = returnStmt.Expression as BinaryExpression;
        Assert.NotNull(@object: expr);
    }

    [Fact]
    public void TestComparisonExpressions()
    {
        string code = @"routine compare() {
    return x == y
    return a != b
    return p < q
    return m >= n
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 4, actual: bodyBlock.Statements.Count);

        // Check each return statement has a comparison expression
        foreach (Statement stmt in bodyBlock.Statements)
        {
            var returnStmt = stmt as ReturnStatement;
            Assert.NotNull(@object: returnStmt);
            var expr = returnStmt.Expression as BinaryExpression;
            Assert.NotNull(@object: expr);
            Assert.Contains(expected: expr.Operator.ToStringRepresentation(),
                collection: new[]
                {
                    "==",
                    "!=",
                    "<",
                    ">="
                });
        }
    }

    [Fact]
    public void TestLogicalExpressions()
    {
        string code = @"routine logic() {
    return x and y
    return a or b
    return not c
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 3, actual: bodyBlock.Statements.Count);
    }

    [Fact]
    public void TestIfStatement()
    {
        string code = @"
routine test() {
    if x > 0:
        return 1
    elif x < 0:
        return -1
    else:
        return 0
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);

        var ifStmt = bodyBlock.Statements[index: 0] as IfStatement;
        Assert.NotNull(@object: ifStmt);
        Assert.NotNull(@object: ifStmt.Condition);
        Assert.NotNull(@object: ifStmt.ThenBranch);
        Assert.NotNull(@object: ifStmt.ElseBranch); // Should be another if statement (elif)
    }

    [Fact]
    public void TestWhileLoop()
    {
        string code = @"
routine countdown() {
    while i > 0:
        i = i - 1
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);

        var whileStmt = bodyBlock.Statements[index: 0] as WhileStatement;
        Assert.NotNull(@object: whileStmt);
        Assert.NotNull(@object: whileStmt.Condition);
        Assert.NotNull(@object: whileStmt.Body);
    }

    [Fact]
    public void TestForLoop()
    {
        string code = @"
routine iterate() {
    for i in 1 to 10:
        print(i)
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);

        var forStmt = bodyBlock.Statements[index: 0] as ForStatement;
        Assert.NotNull(@object: forStmt);
        Assert.NotNull(@object: forStmt.Variable);
        Assert.NotNull(@object: forStmt.Iterable);
        Assert.NotNull(@object: forStmt.Body);
    }

    [Fact]
    public void TestRoutineCall()
    {
        string code = @"
routine test() {
    let result = add(1, 2)
    print(""Hello"", ""World"")
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 2, actual: bodyBlock.Statements.Count);

        // First statement: variable declaration with function call
        var varDeclStmt = bodyBlock.Statements[index: 0] as DeclarationStatement;
        Assert.NotNull(@object: varDeclStmt);
        var varDecl = varDeclStmt.Declaration as VariableDeclaration;
        Assert.NotNull(@object: varDecl);
        var callExpr = varDecl.Initializer as CallExpression;
        Assert.NotNull(@object: callExpr);
        Assert.Equal(expected: "add", actual: callExpr.Name);
        Assert.Equal(expected: 2, actual: callExpr.Arguments.Count);

        // Second statement: expression statement with function call
        var exprStmt = bodyBlock.Statements[index: 1] as ExpressionStatement;
        Assert.NotNull(@object: exprStmt);
        var printCall = exprStmt.Expression as CallExpression;
        Assert.NotNull(@object: printCall);
        Assert.Equal(expected: "print", actual: printCall.Name);
        Assert.Equal(expected: 2, actual: printCall.Arguments.Count);
    }

    [Fact]
    public void TestMemberAccess()
    {
        string code = @"
routine access() {
    return obj.field
    return obj.method()
    return obj.field.nested
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 3, actual: bodyBlock.Statements.Count);

        // Each return statement should have a member access expression
        foreach (Statement stmt in bodyBlock.Statements)
        {
            var returnStmt = stmt as ReturnStatement;
            Assert.NotNull(@object: returnStmt);
            Assert.NotNull(@object: returnStmt.Expression);
        }
    }

    [Fact]
    public void TestArrayAccess()
    {
        string code = @"
routine array_ops() {
    return arr[0]
    return matrix[i][j]
    arr[index] = value
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 3, actual: bodyBlock.Statements.Count);
    }

    [Fact]
    public void TestStringInterpolation()
    {
        string code = @"
routine format() {
    return f""Value: {x}""
    return f""Hello {name}, you are {age} years old""
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 2, actual: bodyBlock.Statements.Count);

        foreach (Statement stmt in bodyBlock.Statements)
        {
            var returnStmt = stmt as ReturnStatement;
            Assert.NotNull(@object: returnStmt);
            Assert.NotNull(@object: returnStmt.Expression);
        }
    }

    [Fact]
    public void TestTypeAnnotations()
    {
        string code = @"
routine typed(x: s32, y: f64, name: Text) -> Bool {
    let result: Bool = x > 0
    return result
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        Assert.Equal(expected: "typed", actual: routine.Name);
        Assert.Equal(expected: 3, actual: routine.Parameters.Count);
        Assert.NotNull(@object: routine.ReturnType);
        Assert.Equal(expected: "Bool", actual: routine.ReturnType.Name);

        // Check parameter types
        Assert.Equal(expected: "s32", actual: routine.Parameters[index: 0].Type?.Name);
        Assert.Equal(expected: "f64", actual: routine.Parameters[index: 1].Type?.Name);
        Assert.Equal(expected: "Text", actual: routine.Parameters[index: 2].Type?.Name);

        // Check variable declaration with type annotation
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        var varDeclStmt = bodyBlock.Statements[index: 0] as DeclarationStatement;
        Assert.NotNull(@object: varDeclStmt);
        var varDecl = varDeclStmt.Declaration as VariableDeclaration;
        Assert.NotNull(@object: varDecl);
        Assert.NotNull(@object: varDecl.Type);
        Assert.Equal(expected: "Bool", actual: varDecl.Type.Name);
    }

    [Fact]
    public void TestComments()
    {
        string code = @"
# This is a single-line comment
routine test() { # End of line comment
    let x = 42 # Another comment
}

## This is a documentation comment
routine documented() {
    return 1
}";
        Program program = ParseCode(code: code);

        // Comments should be ignored by parser, so we should get 2 routine declarations
        Assert.Equal(expected: 2, actual: program.Declarations.Count);

        var routine1 = program.Declarations[index: 0] as RoutineDeclaration;
        Assert.NotNull(@object: routine1);
        Assert.Equal(expected: "test", actual: routine1.Name);

        var routine2 = program.Declarations[index: 1] as RoutineDeclaration;
        Assert.NotNull(@object: routine2);
        Assert.Equal(expected: "documented", actual: routine2.Name);
    }

    [Fact]
    public void TestErrorRecovery()
    {
        // Test parser's ability to recover from syntax errors
        string code = @"
routine good1() { return 1 }
routine bad() { syntax error here
routine good2() { return 2 }";

        // This should not throw an exception but should attempt to parse what it can
        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        // Should have at least some valid declarations
        Assert.True(condition: program.Declarations.Count >= 1);
    }

    [Fact]
    public void TestNestedExpressions()
    {
        string code = @"
routine nested() {
    return ((a + b) * (c - d)) / (e + f)
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);

        var returnStmt = bodyBlock.Statements[index: 0] as ReturnStatement;
        Assert.NotNull(@object: returnStmt);
        Assert.NotNull(@object: returnStmt.Expression);
    }

    [Fact]
    public void TestBreakAndContinue()
    {
        string code = @"
routine loop_control() {
    while true:
        if condition1:
            break
        if condition2:
            continue
        do_something()
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);

        var whileStmt = bodyBlock.Statements[index: 0] as WhileStatement;
        Assert.NotNull(@object: whileStmt);
        Assert.NotNull(@object: whileStmt.Body);
    }

    [Fact]
    public void TestSliceOperations()
    {
        string code = @"
routine slice_test() {
    let heap_slice = DynamicSlice(64)
    let stack_slice = TemporarySlice(32)

    heap_slice[0] = 42
    let value = stack_slice[index]
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Equal(expected: 4, actual: bodyBlock.Statements.Count);

        // Check slice constructor calls
        var heapDeclStmt = bodyBlock.Statements[index: 0] as DeclarationStatement;
        Assert.NotNull(@object: heapDeclStmt);
        var heapDecl = heapDeclStmt.Declaration as VariableDeclaration;
        Assert.NotNull(@object: heapDecl);
        var heapCall = heapDecl.Initializer as CallExpression;
        Assert.NotNull(@object: heapCall);
        Assert.Equal(expected: "DynamicSlice", actual: heapCall.Name);

        var stackDeclStmt = bodyBlock.Statements[index: 1] as DeclarationStatement;
        Assert.NotNull(@object: stackDeclStmt);
        var stackDecl = stackDeclStmt.Declaration as VariableDeclaration;
        Assert.NotNull(@object: stackDecl);
        var stackCall = stackDecl.Initializer as CallExpression;
        Assert.NotNull(@object: stackCall);
        Assert.Equal(expected: "TemporarySlice", actual: stackCall.Name);
    }

    [Fact]
    public void TestDangerBlocks()
    {
        string code = @"
routine unsafe_ops() {
    danger {
        # Unsafe operations here
        let ptr = get_raw_pointer()
        *ptr = 42
    }
}";
        Program program = ParseCode(code: code);

        var routine = program.Declarations.First() as RoutineDeclaration;
        Assert.NotNull(@object: routine);
        var bodyBlock = routine.Body as BlockStatement;
        Assert.NotNull(@object: bodyBlock);
        Assert.Single(collection: bodyBlock.Statements);

        // Should parse as a block statement or special danger statement
        Assert.NotNull(@object: bodyBlock.Statements[index: 0]);
    }
}
