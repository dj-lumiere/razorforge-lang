using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Analysis;
using Compilers.RazorForge.Parser;
using Compilers.Shared.Parser;

namespace RazorForge.Tests.Parser
{
    /// <summary>
    /// Unit tests for the RazorForge parser
    /// </summary>
    public class ParserTests
    {
        private Program ParseCode(string code)
        {
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);
            var parser = new RazorForgeParser(tokens);
            return parser.Parse();
        }

        private List<Token> TokenizeCode(string code)
        {
            return Tokenizer.Tokenize(code, Language.RazorForge);
        }

        [Fact]
        public void TestEmptyProgram()
        {
            var program = ParseCode("");
            Assert.NotNull(program);
            Assert.Empty(program.Declarations);
        }

        [Fact]
        public void TestSimpleRecipeDeclaration()
        {
            var code = @"recipe main() { }";
            var program = ParseCode(code);

            Assert.NotNull(program);
            Assert.Single(program.Declarations);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            Assert.Equal("main", recipe.Name);
            Assert.Empty(recipe.Parameters);
            Assert.Null(recipe.ReturnType);
            Assert.NotNull(recipe.Body);
        }

        [Fact]
        public void TestRecipeWithParameters()
        {
            var code = @"recipe add(a: s32, b: s32) -> s32 { return a + b }";
            var program = ParseCode(code);

            Assert.NotNull(program);
            Assert.Single(program.Declarations);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            Assert.Equal("add", recipe.Name);
            Assert.Equal(2, recipe.Parameters.Count);

            // Check first parameter
            Assert.Equal("a", recipe.Parameters[0].Name);
            Assert.Equal("s32", recipe.Parameters[0].Type?.Name);

            // Check second parameter
            Assert.Equal("b", recipe.Parameters[1].Name);
            Assert.Equal("s32", recipe.Parameters[1].Type?.Name);

            // Check return type
            Assert.NotNull(recipe.ReturnType);
            Assert.Equal("s32", recipe.ReturnType.Name);

            // Check body has return statement
            Assert.NotNull(recipe.Body);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);
        }

        [Fact]
        public void TestVariableDeclarations()
        {
            var code = @"
recipe test() {
    let x = 42
    var y: s32 = 100
    var z = ""hello""
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(3, bodyBlock.Statements.Count);

            // Test immutable variable
            var letStmt = bodyBlock.Statements[0] as DeclarationStatement;
            Assert.NotNull(letStmt);
            var letDecl = letStmt.Declaration as VariableDeclaration;
            Assert.NotNull(letDecl);
            Assert.Equal("x", letDecl.Name);
            Assert.False(letDecl.IsMutable);
            Assert.NotNull(letDecl.Initializer);

            // Test mutable variable with type
            var varStmt = bodyBlock.Statements[1] as DeclarationStatement;
            Assert.NotNull(varStmt);
            var varDecl = varStmt.Declaration as VariableDeclaration;
            Assert.NotNull(varDecl);
            Assert.Equal("y", varDecl.Name);
            Assert.True(varDecl.IsMutable);
            Assert.NotNull(varDecl.Type);
            Assert.NotNull(varDecl.Initializer);

            // Test mutable variable without explicit type
            var varStmt2 = bodyBlock.Statements[2] as DeclarationStatement;
            Assert.NotNull(varStmt2);
            var varDecl2 = varStmt2.Declaration as VariableDeclaration;
            Assert.NotNull(varDecl2);
            Assert.Equal("z", varDecl2.Name);
            Assert.True(varDecl2.IsMutable);
            Assert.NotNull(varDecl2.Initializer);
        }

        [Fact]
        public void TestArithmeticExpressions()
        {
            var code = @"recipe calc() { return 2 + 3 * 4 - 1 }";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);

            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            var returnStmt = bodyBlock.Statements[0] as ReturnStatement;
            Assert.NotNull(returnStmt);
            Assert.NotNull(returnStmt.Expression);

            // The expression should be a binary expression
            var expr = returnStmt.Expression as BinaryExpression;
            Assert.NotNull(expr);
        }

        [Fact]
        public void TestComparisonExpressions()
        {
            var code = @"recipe compare() {
    return x == y
    return a != b
    return p < q
    return m >= n
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(4, bodyBlock.Statements.Count);

            // Check each return statement has a comparison expression
            foreach (var stmt in bodyBlock.Statements)
            {
                var returnStmt = stmt as ReturnStatement;
                Assert.NotNull(returnStmt);
                var expr = returnStmt.Expression as BinaryExpression;
                Assert.NotNull(expr);
                Assert.Contains(expr.Operator.ToStringRepresentation(), new[] { "==", "!=", "<", ">=" });
            }
        }

        [Fact]
        public void TestLogicalExpressions()
        {
            var code = @"recipe logic() {
    return x and y
    return a or b
    return not c
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(3, bodyBlock.Statements.Count);
        }

        [Fact]
        public void TestIfStatement()
        {
            var code = @"
recipe test() {
    if x > 0:
        return 1
    elif x < 0:
        return -1
    else:
        return 0
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);

            var ifStmt = bodyBlock.Statements[0] as IfStatement;
            Assert.NotNull(ifStmt);
            Assert.NotNull(ifStmt.Condition);
            Assert.NotNull(ifStmt.ThenBranch);
            Assert.NotNull(ifStmt.ElseBranch); // Should be another if statement (elif)
        }

        [Fact]
        public void TestWhileLoop()
        {
            var code = @"
recipe countdown() {
    while i > 0:
        i = i - 1
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);

            var whileStmt = bodyBlock.Statements[0] as WhileStatement;
            Assert.NotNull(whileStmt);
            Assert.NotNull(whileStmt.Condition);
            Assert.NotNull(whileStmt.Body);
        }

        [Fact]
        public void TestForLoop()
        {
            var code = @"
recipe iterate() {
    for i in 1 to 10:
        print(i)
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);

            var forStmt = bodyBlock.Statements[0] as ForStatement;
            Assert.NotNull(forStmt);
            Assert.NotNull(forStmt.Variable);
            Assert.NotNull(forStmt.Iterable);
            Assert.NotNull(forStmt.Body);
        }

        [Fact]
        public void TestRecipeCall()
        {
            var code = @"
recipe test() {
    let result = add(1, 2)
    print(""Hello"", ""World"")
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(2, bodyBlock.Statements.Count);

            // First statement: variable declaration with function call
            var varDeclStmt = bodyBlock.Statements[0] as DeclarationStatement;
            Assert.NotNull(varDeclStmt);
            var varDecl = varDeclStmt.Declaration as VariableDeclaration;
            Assert.NotNull(varDecl);
            var callExpr = varDecl.Initializer as CallExpression;
            Assert.NotNull(callExpr);
            Assert.Equal("add", callExpr.Name);
            Assert.Equal(2, callExpr.Arguments.Count);

            // Second statement: expression statement with function call
            var exprStmt = bodyBlock.Statements[1] as ExpressionStatement;
            Assert.NotNull(exprStmt);
            var printCall = exprStmt.Expression as CallExpression;
            Assert.NotNull(printCall);
            Assert.Equal("print", printCall.Name);
            Assert.Equal(2, printCall.Arguments.Count);
        }

        [Fact]
        public void TestMemberAccess()
        {
            var code = @"
recipe access() {
    return obj.field
    return obj.method()
    return obj.field.nested
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(3, bodyBlock.Statements.Count);

            // Each return statement should have a member access expression
            foreach (var stmt in bodyBlock.Statements)
            {
                var returnStmt = stmt as ReturnStatement;
                Assert.NotNull(returnStmt);
                Assert.NotNull(returnStmt.Expression);
            }
        }

        [Fact]
        public void TestArrayAccess()
        {
            var code = @"
recipe array_ops() {
    return arr[0]
    return matrix[i][j]
    arr[index] = value
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(3, bodyBlock.Statements.Count);
        }

        [Fact]
        public void TestStringInterpolation()
        {
            var code = @"
recipe format() {
    return f""Value: {x}""
    return f""Hello {name}, you are {age} years old""
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(2, bodyBlock.Statements.Count);

            foreach (var stmt in bodyBlock.Statements)
            {
                var returnStmt = stmt as ReturnStatement;
                Assert.NotNull(returnStmt);
                Assert.NotNull(returnStmt.Expression);
            }
        }

        [Fact]
        public void TestTypeAnnotations()
        {
            var code = @"
recipe typed(x: s32, y: f64, name: Text) -> Bool {
    let result: Bool = x > 0
    return result
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            Assert.Equal("typed", recipe.Name);
            Assert.Equal(3, recipe.Parameters.Count);
            Assert.NotNull(recipe.ReturnType);
            Assert.Equal("Bool", recipe.ReturnType.Name);

            // Check parameter types
            Assert.Equal("s32", recipe.Parameters[0].Type?.Name);
            Assert.Equal("f64", recipe.Parameters[1].Type?.Name);
            Assert.Equal("Text", recipe.Parameters[2].Type?.Name);

            // Check variable declaration with type annotation
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            var varDeclStmt = bodyBlock.Statements[0] as DeclarationStatement;
            Assert.NotNull(varDeclStmt);
            var varDecl = varDeclStmt.Declaration as VariableDeclaration;
            Assert.NotNull(varDecl);
            Assert.NotNull(varDecl.Type);
            Assert.Equal("Bool", varDecl.Type.Name);
        }

        [Fact]
        public void TestComments()
        {
            var code = @"
# This is a single-line comment
recipe test() { # End of line comment
    let x = 42 # Another comment
}

## This is a documentation comment
recipe documented() {
    return 1
}";
            var program = ParseCode(code);

            // Comments should be ignored by parser, so we should get 2 recipe declarations
            Assert.Equal(2, program.Declarations.Count);

            var recipe1 = program.Declarations[0] as RecipeDeclaration;
            Assert.NotNull(recipe1);
            Assert.Equal("test", recipe1.Name);

            var recipe2 = program.Declarations[1] as RecipeDeclaration;
            Assert.NotNull(recipe2);
            Assert.Equal("documented", recipe2.Name);
        }

        [Fact]
        public void TestErrorRecovery()
        {
            // Test parser's ability to recover from syntax errors
            var code = @"
recipe good1() { return 1 }
recipe bad() { syntax error here
recipe good2() { return 2 }";

            // This should not throw an exception but should attempt to parse what it can
            var program = ParseCode(code);
            Assert.NotNull(program);

            // Should have at least some valid declarations
            Assert.True(program.Declarations.Count >= 1);
        }

        [Fact]
        public void TestNestedExpressions()
        {
            var code = @"
recipe nested() {
    return ((a + b) * (c - d)) / (e + f)
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);

            var returnStmt = bodyBlock.Statements[0] as ReturnStatement;
            Assert.NotNull(returnStmt);
            Assert.NotNull(returnStmt.Expression);
        }

        [Fact]
        public void TestBreakAndContinue()
        {
            var code = @"
recipe loop_control() {
    while true:
        if condition1:
            break
        if condition2:
            continue
        do_something()
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);

            var whileStmt = bodyBlock.Statements[0] as WhileStatement;
            Assert.NotNull(whileStmt);
            Assert.NotNull(whileStmt.Body);
        }

        [Fact]
        public void TestSliceOperations()
        {
            var code = @"
recipe slice_test() {
    let heap_slice = HeapSlice(64)
    let stack_slice = StackSlice(32)

    heap_slice[0] = 42
    let value = stack_slice[index]
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Equal(4, bodyBlock.Statements.Count);

            // Check slice constructor calls
            var heapDeclStmt = bodyBlock.Statements[0] as DeclarationStatement;
            Assert.NotNull(heapDeclStmt);
            var heapDecl = heapDeclStmt.Declaration as VariableDeclaration;
            Assert.NotNull(heapDecl);
            var heapCall = heapDecl.Initializer as CallExpression;
            Assert.NotNull(heapCall);
            Assert.Equal("HeapSlice", heapCall.Name);

            var stackDeclStmt = bodyBlock.Statements[1] as DeclarationStatement;
            Assert.NotNull(stackDeclStmt);
            var stackDecl = stackDeclStmt.Declaration as VariableDeclaration;
            Assert.NotNull(stackDecl);
            var stackCall = stackDecl.Initializer as CallExpression;
            Assert.NotNull(stackCall);
            Assert.Equal("StackSlice", stackCall.Name);
        }

        [Fact]
        public void TestDangerBlocks()
        {
            var code = @"
recipe unsafe_ops() {
    danger {
        # Unsafe operations here
        let ptr = get_raw_pointer()
        *ptr = 42
    }
}";
            var program = ParseCode(code);

            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            var bodyBlock = recipe.Body as BlockStatement;
            Assert.NotNull(bodyBlock);
            Assert.Single(bodyBlock.Statements);

            // Should parse as a block statement or special danger statement
            Assert.NotNull(bodyBlock.Statements[0]);
        }

        [Fact]
        public void TestBitterMode()
        {
            var code = @"
bitter recipe low_level() {
    # Low-level operations
    return 0
}";
            var program = ParseCode(code);

            Assert.Single(program.Declarations);
            var recipe = program.Declarations.First() as RecipeDeclaration;
            Assert.NotNull(recipe);
            Assert.Equal("low_level", recipe.Name);
        }
    }
}