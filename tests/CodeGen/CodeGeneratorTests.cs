using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.CodeGen;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;

namespace RazorForge.Tests.CodeGen
{
    /// <summary>
    /// Unit tests for the LLVM Code Generator
    /// </summary>
    public class CodeGeneratorTests
    {
        private readonly SemanticAnalyzer _analyzer;
        private readonly LLVMCodeGenerator _codeGenerator;

        public CodeGeneratorTests()
        {
            _analyzer = new SemanticAnalyzer(Language.RazorForge, LanguageMode.Normal);
            _codeGenerator = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal);
        }

        private Program ParseAndAnalyze(string code)
        {
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);
            var parser = new RazorForgeParser(tokens);
            var program = parser.Parse();
            _analyzer.Analyze(program);
            return program;
        }

        private string GenerateCode(string code)
        {
            var program = ParseAndAnalyze(code);
            _codeGenerator.Generate(program);
            return _codeGenerator.GetGeneratedCode();
        }

        [Fact]
        public void TestSimpleRecipeGeneration()
        {
            var code = @"
recipe main() -> s32 {
    return 42
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("define", llvmIr); // Should have function definition
            Assert.Contains("main", llvmIr);   // Should reference main function
            Assert.Contains("ret", llvmIr);    // Should have return instruction
            Assert.Contains("42", llvmIr);     // Should contain the literal value
        }

        [Fact]
        public void TestRecipeWithParameters()
        {
            var code = @"
recipe add(a: s32, b: s32) -> s32 {
    return a + b
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("define", llvmIr);
            Assert.Contains("add", llvmIr);
            Assert.Contains("i32", llvmIr);    // Should use i32 for s32 type
            Assert.Contains("add", llvmIr);    // Should contain add instruction (or similar)
        }

        [Fact]
        public void TestVariableDeclaration()
        {
            var code = @"
recipe test() -> s32 {
    let x = 42
    return x
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("alloca", llvmIr); // Should allocate space for variable
            Assert.Contains("store", llvmIr);  // Should store value to variable
            Assert.Contains("load", llvmIr);   // Should load value from variable
        }

        [Fact]
        public void TestArithmeticOperations()
        {
            var code = @"
recipe math() -> s32 {
    let a = 10
    let b = 20
    let sum = a + b
    let diff = a - b
    let prod = a * b
    let quot = a / b
    return sum
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should contain arithmetic instructions
            var hasArithmetic = llvmIr.Contains("add") || llvmIr.Contains("sub") ||
                               llvmIr.Contains("mul") || llvmIr.Contains("div");
            Assert.True(hasArithmetic);
        }

        [Fact]
        public void TestComparisonOperations()
        {
            var code = @"
recipe compare(x: s32, y: s32) -> Bool {
    return x < y
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("icmp", llvmIr); // Should use integer comparison
        }

        [Fact]
        public void TestIfStatement()
        {
            var code = @"
recipe conditional(x: s32) -> s32 {
    if x > 0:
        return 1
    else:
        return -1
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("br", llvmIr);    // Should have branch instructions
            Assert.Contains("label", llvmIr); // Should have basic block labels
        }

        [Fact]
        public void TestWhileLoop()
        {
            var code = @"
recipe countdown(n: s32) -> s32 {
    var i = n
    while i > 0:
        i = i - 1
    return i
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("br", llvmIr);     // Should have branch for loop
            Assert.Contains("icmp", llvmIr);   // Should have comparison for condition
        }

        [Fact]
        public void TestRecipeCall()
        {
            var code = @"
recipe helper() -> s32 {
    return 42
}

recipe main() -> s32 {
    return helper()
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("call", llvmIr);   // Should have function call
            Assert.Contains("helper", llvmIr); // Should reference helper function
        }

        [Fact]
        public void TestRecursiveCall()
        {
            var code = @"
recipe factorial(n: s32) -> s32 {
    if n <= 1:
        return 1
    else:
        return n * factorial(n - 1)
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            Assert.Contains("call", llvmIr);      // Should have recursive call
            Assert.Contains("factorial", llvmIr); // Should reference itself
        }

        [Fact]
        public void TestDifferentIntegerTypes()
        {
            var code = @"
recipe types() {
    let a: s8 = 10
    let b: s16 = 1000
    let c: s32 = 100000
    let d: s64 = 10000000000

    let ua: u8 = 255
    let ub: u16 = 65535
    let uc: u32 = 4294967295
    let ud: u64 = 18446744073709551615
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should use appropriate LLVM integer types
            Assert.Contains("i8", llvmIr);
            Assert.Contains("i16", llvmIr);
            Assert.Contains("i32", llvmIr);
            Assert.Contains("i64", llvmIr);
        }

        [Fact]
        public void TestFloatingPointTypes()
        {
            var code = @"
recipe floats() {
    let f32_val: f32 = 3.14
    let f64_val: f64 = 2.718281828
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should use appropriate LLVM float types
            Assert.Contains("float", llvmIr);
            Assert.Contains("double", llvmIr);
        }

        [Fact]
        public void TestStringLiterals()
        {
            var code = @"
recipe strings() -> Text {
    let message = ""Hello, World!""
    return message
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should handle string constants
            Assert.Contains("Hello, World!", llvmIr);
        }

        [Fact]
        public void TestBooleanOperations()
        {
            var code = @"
recipe boolean_logic(a: Bool, b: Bool) -> Bool {
    let and_result = a and b
    let or_result = a or b
    let not_result = not a
    return and_result
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should use boolean operations
            Assert.Contains("i1", llvmIr); // LLVM boolean type
        }

        [Fact]
        public void TestMemorySliceOperations()
        {
            var code = @"
recipe slice_test() {
    let heap_slice = HeapSlice(64)
    heap_slice[0] = 42
    let value = heap_slice[0]
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should have memory allocation and access
            Assert.Contains("call", llvmIr);    // For HeapSlice constructor
            Assert.Contains("store", llvmIr);   // For slice assignment
            Assert.Contains("load", llvmIr);    // For slice access
        }

        [Fact]
        public void TestOverflowOperators()
        {
            var code = @"
recipe overflow_math() {
    let a: u8 = 200
    let b: u8 = 100
    let wrapped = a +% b
    let saturated = a +^ b
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should generate appropriate overflow handling
            Assert.NotEmpty(llvmIr);
        }

        [Fact]
        public void TestModuleStructure()
        {
            var code = @"
recipe first() -> s32 { return 1 }
recipe second() -> s32 { return 2 }
";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should have proper module structure
            Assert.Contains("target datalayout", llvmIr);
            Assert.Contains("target triple", llvmIr);

            // Should contain both functions
            Assert.Contains("first", llvmIr);
            Assert.Contains("second", llvmIr);
        }

        [Fact]
        public void TestOptimizationHints()
        {
            var code = @"
recipe optimized(x: s32) -> s32 {
    return x * 2  # Should be optimizable to left shift
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Code generator should produce valid LLVM IR
            // Actual optimization will be done by LLVM passes
            Assert.Contains("define", llvmIr);
        }

        [Fact]
        public void TestConstantFolding()
        {
            var code = @"
recipe constants() -> s32 {
    return 2 + 3 * 4  # Should potentially fold to 14
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should generate valid IR (constant folding may happen in LLVM)
            Assert.Contains("ret", llvmIr);
        }

        [Fact]
        public void TestErrorHandling()
        {
            var code = @"
recipe test() -> s32 {
    return divide_by_zero()  # Undefined function
}";

            // Should throw during semantic analysis phase
            Assert.ThrowsAny<Exception>(() => GenerateCode(code));
        }

        [Fact]
        public void TestComplexExpression()
        {
            var code = @"
recipe complex(a: s32, b: s32, c: s32) -> s32 {
    return (a + b) * c - (a - b) / (c + 1)
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should handle complex nested expressions
            Assert.Contains("define", llvmIr);
            Assert.Contains("ret", llvmIr);
        }

        [Fact]
        public void TestStackAllocation()
        {
            var code = @"
recipe local_vars() {
    let a = 1
    let b = 2
    let c = 3
    let d = 4
    let e = 5
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should allocate stack space for local variables
            var allocaCount = llvmIr.Split("alloca").Length - 1;
            Assert.True(allocaCount >= 5); // At least 5 allocations
        }

        [Fact]
        public void TestTypeConversions()
        {
            var code = @"
recipe conversions() {
    let small: s8 = 10
    let big: s32 = small  # Implicit widening
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should handle type conversions
            Assert.Contains("sext", llvmIr); // Sign extension for widening
        }

        [Fact]
        public void TestNullReturn()
        {
            var code = @"
recipe void_func() {
    let x = 42
    # No explicit return
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should handle void functions properly
            Assert.Contains("ret void", llvmIr);
        }

        [Fact]
        public void TestMultipleReturnPaths()
        {
            var code = @"
recipe multi_return(x: s32) -> s32 {
    if x > 0:
        return 1
    elif x < 0:
        return -1
    else:
        return 0
}";

            var llvmIr = GenerateCode(code);

            Assert.NotNull(llvmIr);
            // Should handle multiple return paths
            var retCount = llvmIr.Split("ret").Length - 1;
            Assert.True(retCount >= 3); // At least 3 return instructions
        }
    }
}