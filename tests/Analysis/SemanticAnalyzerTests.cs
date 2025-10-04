using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;

namespace RazorForge.Tests.Analysis
{
    /// <summary>
    /// Unit tests for the Semantic Analyzer
    /// </summary>
    public class SemanticAnalyzerTests
    {
        private readonly SemanticAnalyzer _analyzer;

        public SemanticAnalyzerTests()
        {
            _analyzer = new SemanticAnalyzer(Language.RazorForge, LanguageMode.Normal);
        }

        private Program ParseCode(string code)
        {
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);
            var parser = new RazorForgeParser(tokens);
            return parser.Parse();
        }

        private void AnalyzeCode(string code)
        {
            var program = ParseCode(code);
            _analyzer.Analyze(program);
        }

        [Fact]
        public void TestVariableDeclarationAndUsage()
        {
            var code = @"
recipe test() {
    let x = 42
    return x
}";
            // Should not throw any semantic errors
            AnalyzeCode(code);
            Assert.True(true); // If we get here, analysis passed
        }

        [Fact]
        public void TestUndefinedVariableError()
        {
            var code = @"
recipe test() {
    return undefined_var
}";

            // Should detect undefined variable
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestTypeInference()
        {
            var code = @"
recipe test() {
    let x = 42        # Should infer s32
    let y = 3.14      # Should infer f64
    let z = ""hello""   # Should infer Text
    let w = true      # Should infer Bool
}";

            // Should successfully infer types
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestTypeMismatchError()
        {
            var code = @"
recipe test() {
    let x: s32 = ""not a number""
}";

            // Should detect type mismatch
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestRecipeParameterTypes()
        {
            var code = @"
recipe add(a: s32, b: s32) -> s32 {
    return a + b
}

recipe test() {
    let result = add(1, 2)
}";

            // Should validate parameter types and return type
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestRecipeCallArgumentMismatch()
        {
            var code = @"
recipe add(a: s32, b: s32) -> s32 {
    return a + b
}

recipe test() {
    let result = add(1)  # Wrong number of arguments
}";

            // Should detect argument count mismatch
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestRecipeCallTypeMismatch()
        {
            var code = @"
recipe add(a: s32, b: s32) -> s32 {
    return a + b
}

recipe test() {
    let result = add(""hello"", ""world"")  # Wrong argument types
}";

            // Should detect argument type mismatch
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestArithmeticOperatorTypes()
        {
            var code = @"
recipe math_ops() {
    let a = 10
    let b = 20
    let sum = a + b
    let diff = a - b
    let prod = a * b
    let quot = a / b
}";

            // Should validate arithmetic operations
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestInvalidArithmeticTypes()
        {
            var code = @"
recipe invalid_math() {
    let text = ""hello""
    let number = 42
    return text + number  # Invalid: can't add string to number
}";

            // Should detect invalid arithmetic operation
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestComparisonOperators()
        {
            var code = @"
recipe comparisons() -> Bool {
    let a = 10
    let b = 20
    let equal = a == b
    let not_equal = a != b
    let less = a < b
    let greater = a > b
    let less_equal = a <= b
    let greater_equal = a >= b
    return equal
}";

            // Should validate comparison operations
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestLogicalOperators()
        {
            var code = @"
recipe logic() -> Bool {
    let a = true
    let b = false
    let and_result = a and b
    let or_result = a or b
    let not_result = not a
    return and_result
}";

            // Should validate logical operations
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestIfStatementCondition()
        {
            var code = @"
recipe conditional() {
    let x = 10
    if x > 0:
        return 1
    else:
        return 0
}";

            // Should validate if condition is boolean
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestInvalidIfCondition()
        {
            var code = @"
recipe invalid_condition() {
    let x = ""hello""
    if x:  # String is not a boolean condition
        return 1
}";

            // Should detect non-boolean condition
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestWhileLoopCondition()
        {
            var code = @"
recipe loop() {
    var i = 0
    while i < 10:
        i = i + 1
}";

            // Should validate while condition is boolean
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestVariableReassignment()
        {
            var code = @"
recipe reassignment() {
    var x = 10
    x = 20  # Valid: x is mutable
}";

            // Should allow reassignment of mutable variables
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestImmutableVariableReassignment()
        {
            var code = @"
recipe immutable_error() {
    let x = 10
    x = 20  # Invalid: x is immutable
}";

            // Should detect reassignment of immutable variable
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestReturnTypeValidation()
        {
            var code = @"
recipe returns_number() -> s32 {
    return 42  # Valid
}

recipe returns_wrong_type() -> s32 {
    return ""not a number""  # Invalid
}";

            // Should detect return type mismatch in second function
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestRecursiveRecipeCall()
        {
            var code = @"
recipe factorial(n: s32) -> s32 {
    if n <= 1:
        return 1
    else:
        return n * factorial(n - 1)
}";

            // Should allow recursive calls
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestForwardDeclaration()
        {
            var code = @"
recipe caller() {
    return callee()
}

recipe callee() -> s32 {
    return 42
}";

            // Should handle forward declarations
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestUndefinedRecipeCall()
        {
            var code = @"
recipe test() {
    return undefined_recipe()
}";

            // Should detect undefined recipe
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestNestedScopes()
        {
            var code = @"
recipe nested_scopes() {
    let x = 10
    if true:
        let y = 20
        return x + y  # x should be accessible here
    # y should not be accessible here
}";

            // Should validate scope rules
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestVariableShadowing()
        {
            var code = @"
recipe shadowing() {
    let x = 10
    if true:
        let x = 20  # Should shadow outer x
        return x
}";

            // Should allow variable shadowing
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestMemorySliceOperations()
        {
            var code = @"
recipe slice_operations() {
    let heap_slice = HeapSlice(64)
    let stack_slice = StackSlice(32)

    heap_slice[0] = 42
    let value = stack_slice[10]
}";

            // Should validate slice operations
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestInvalidSliceIndex()
        {
            var code = @"
recipe invalid_index() {
    let slice = HeapSlice(64)
    slice[""not an index""] = 42  # Invalid index type
}";

            // Should detect invalid index type
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestMemberAccess()
        {
            var code = @"
recipe member_access() {
    let slice = HeapSlice(64)
    let size = slice.size
    let ptr = slice.ptr
}";

            // Should validate member access on known types
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestInvalidMemberAccess()
        {
            var code = @"
recipe invalid_member() {
    let x = 42
    return x.nonexistent_field  # Invalid member access
}";

            // Should detect invalid member access
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestBreakContinueInLoop()
        {
            var code = @"
recipe loop_control() {
    while true:
        if condition1:
            break     # Valid: inside loop
        if condition2:
            continue  # Valid: inside loop
}";

            // Should allow break/continue in loops
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestBreakContinueOutsideLoop()
        {
            var code = @"
recipe invalid_break() {
    break  # Invalid: not inside loop
}";

            // Should detect break outside loop
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestDangerModeValidation()
        {
            var code = @"
recipe safe_operations() {
    danger {
        # Should allow potentially unsafe operations here
        let ptr = get_raw_pointer()
    }
}";

            // Should validate danger blocks
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestTypeCoercion()
        {
            var code = @"
recipe coercion() {
    let small: s8 = 10
    let big: s32 = small  # Should allow implicit widening
}";

            // Should allow safe type coercions
            AnalyzeCode(code);
            Assert.True(true);
        }

        [Fact]
        public void TestUnsafeTypeCoercion()
        {
            var code = @"
recipe unsafe_coercion() {
    let big: s32 = 1000
    let small: s8 = big  # Should detect potential overflow
}";

            // Should detect unsafe narrowing conversion
            Assert.ThrowsAny<Exception>(() => AnalyzeCode(code));
        }

        [Fact]
        public void TestOverflowOperators()
        {
            var code = @"
recipe overflow_ops() {
    let a: u8 = 200
    let b: u8 = 100
    let wrapped = a +% b      # Wrapping add
    let saturated = a +^ b    # Saturating add
    let checked = a +? b      # Checked add
}";

            // Should validate overflow operators
            AnalyzeCode(code);
            Assert.True(true);
        }
    }
}