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

namespace RazorForge.Tests.CodeGen;

/// <summary>
/// Unit tests for the LLVM Code Generator
/// </summary>
public class CodeGeneratorTests
{
    private readonly SemanticAnalyzer _analyzer;
    private readonly LLVMCodeGenerator _codeGenerator;

    public CodeGeneratorTests()
    {
        _analyzer = new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal);
        _codeGenerator =
            new LLVMCodeGenerator(language: Language.RazorForge, mode: LanguageMode.Normal);
    }

    private Program ParseAndAnalyze(string code)
    {
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);
        var parser = new RazorForgeParser(tokens: tokens);
        Program program = parser.Parse();
        _analyzer.Analyze(program: program);
        return program;
    }

    private string GenerateCode(string code)
    {
        Program program = ParseAndAnalyze(code: code);
        _codeGenerator.Generate(program: program);
        return _codeGenerator.GetGeneratedCode();
    }

    [Fact]
    public void TestSimpleRecipeGeneration()
    {
        string code = @"
recipe main() -> s32 {
    return 42
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "define",
            actualString: llvmIr); // Should have function definition
        Assert.Contains(expectedSubstring: "main",
            actualString: llvmIr); // Should reference main function
        Assert.Contains(expectedSubstring: "ret",
            actualString: llvmIr); // Should have return instruction
        Assert.Contains(expectedSubstring: "42",
            actualString: llvmIr); // Should contain the literal value
    }

    [Fact]
    public void TestRecipeWithParameters()
    {
        string code = @"
recipe add(a: s32, b: s32) -> s32 {
    return a + b
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "define", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "add", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "i32",
            actualString: llvmIr); // Should use i32 for s32 type
        Assert.Contains(expectedSubstring: "add",
            actualString: llvmIr); // Should contain add instruction (or similar)
    }

    [Fact]
    public void TestVariableDeclaration()
    {
        string code = @"
recipe test() -> s32 {
    let x = 42
    return x
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "alloca",
            actualString: llvmIr); // Should allocate space for variable
        Assert.Contains(expectedSubstring: "store",
            actualString: llvmIr); // Should store value to variable
        Assert.Contains(expectedSubstring: "load",
            actualString: llvmIr); // Should load value from variable
    }

    [Fact]
    public void TestArithmeticOperations()
    {
        string code = @"
recipe math() -> s32 {
    let a = 10
    let b = 20
    let sum = a + b
    let diff = a - b
    let prod = a * b
    let quot = a / b
    return sum
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should contain arithmetic instructions
        bool hasArithmetic = llvmIr.Contains(value: "add") || llvmIr.Contains(value: "sub") ||
                             llvmIr.Contains(value: "mul") || llvmIr.Contains(value: "div");
        Assert.True(condition: hasArithmetic);
    }

    [Fact]
    public void TestComparisonOperations()
    {
        string code = @"
recipe compare(x: s32, y: s32) -> Bool {
    return x < y
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "icmp",
            actualString: llvmIr); // Should use integer comparison
    }

    [Fact]
    public void TestIfStatement()
    {
        string code = @"
recipe conditional(x: s32) -> s32 {
    if x > 0:
        return 1
    else:
        return -1
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "br",
            actualString: llvmIr); // Should have branch instructions
        Assert.Contains(expectedSubstring: "label",
            actualString: llvmIr); // Should have basic block labels
    }

    [Fact]
    public void TestWhileLoop()
    {
        string code = @"
recipe countdown(n: s32) -> s32 {
    var i = n
    while i > 0:
        i = i - 1
    return i
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "br",
            actualString: llvmIr); // Should have branch for loop
        Assert.Contains(expectedSubstring: "icmp",
            actualString: llvmIr); // Should have comparison for condition
    }

    [Fact]
    public void TestRecipeCall()
    {
        string code = @"
recipe helper() -> s32 {
    return 42
}

recipe main() -> s32 {
    return helper()
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "call",
            actualString: llvmIr); // Should have function call
        Assert.Contains(expectedSubstring: "helper",
            actualString: llvmIr); // Should reference helper function
    }

    [Fact]
    public void TestRecursiveCall()
    {
        string code = @"
recipe factorial(n: s32) -> s32 {
    if n <= 1:
        return 1
    else:
        return n * factorial(n - 1)
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "call",
            actualString: llvmIr); // Should have recursive call
        Assert.Contains(expectedSubstring: "factorial",
            actualString: llvmIr); // Should reference itself
    }

    [Fact]
    public void TestDifferentIntegerTypes()
    {
        string code = @"
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

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should use appropriate LLVM integer types
        Assert.Contains(expectedSubstring: "i8", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "i16", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "i32", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "i64", actualString: llvmIr);
    }

    [Fact]
    public void TestFloatingPointTypes()
    {
        string code = @"
recipe floats() {
    let f32_val: f32 = 3.14
    let f64_val: f64 = 2.718281828
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should use appropriate LLVM float types
        Assert.Contains(expectedSubstring: "float", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "double", actualString: llvmIr);
    }

    [Fact]
    public void TestStringLiterals()
    {
        string code = @"
recipe strings() -> Text {
    let message = ""Hello, World!""
    return message
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should handle string constants
        Assert.Contains(expectedSubstring: "Hello, World!", actualString: llvmIr);
    }

    [Fact]
    public void TestBooleanOperations()
    {
        string code = @"
recipe boolean_logic(a: Bool, b: Bool) -> Bool {
    let and_result = a and b
    let or_result = a or b
    let not_result = not a
    return and_result
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should use boolean operations
        Assert.Contains(expectedSubstring: "i1", actualString: llvmIr); // LLVM boolean type
    }

    [Fact]
    public void TestMemorySliceOperations()
    {
        string code = @"
recipe slice_test() {
    let heap_slice = DynamicSlice(64)
    heap_slice[0] = 42
    let value = heap_slice[0]
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should have memory allocation and access
        Assert.Contains(expectedSubstring: "call",
            actualString: llvmIr); // For DynamicSlice constructor
        Assert.Contains(expectedSubstring: "store", actualString: llvmIr); // For slice assignment
        Assert.Contains(expectedSubstring: "load", actualString: llvmIr); // For slice access
    }

    [Fact]
    public void TestOverflowOperators()
    {
        string code = @"
recipe overflow_math() {
    let a: u8 = 200
    let b: u8 = 100
    let wrapped = a +% b
    let saturated = a +^ b
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should generate appropriate overflow handling
        Assert.NotEmpty(collection: llvmIr);
    }

    [Fact]
    public void TestModuleStructure()
    {
        string code = @"
recipe first() -> s32 { return 1 }
recipe second() -> s32 { return 2 }
";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should have proper module structure
        Assert.Contains(expectedSubstring: "target datalayout", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "target triple", actualString: llvmIr);

        // Should contain both functions
        Assert.Contains(expectedSubstring: "first", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "second", actualString: llvmIr);
    }

    [Fact]
    public void TestOptimizationHints()
    {
        string code = @"
recipe optimized(x: s32) -> s32 {
    return x * 2  # Should be optimizable to left shift
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Code generator should produce valid LLVM IR
        // Actual optimization will be done by LLVM passes
        Assert.Contains(expectedSubstring: "define", actualString: llvmIr);
    }

    [Fact]
    public void TestConstantFolding()
    {
        string code = @"
recipe constants() -> s32 {
    return 2 + 3 * 4  # Should potentially fold to 14
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should generate valid IR (constant folding may happen in LLVM)
        Assert.Contains(expectedSubstring: "ret", actualString: llvmIr);
    }

    [Fact]
    public void TestErrorHandling()
    {
        string code = @"
recipe test() -> s32 {
    return divide_by_zero()  # Undefined function
}";

        // Should throw during semantic analysis phase
        Assert.ThrowsAny<Exception>(testCode: () => GenerateCode(code: code));
    }

    [Fact]
    public void TestComplexExpression()
    {
        string code = @"
recipe complex(a: s32, b: s32, c: s32) -> s32 {
    return (a + b) * c - (a - b) / (c + 1)
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should handle complex nested expressions
        Assert.Contains(expectedSubstring: "define", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "ret", actualString: llvmIr);
    }

    [Fact]
    public void TestStackAllocation()
    {
        string code = @"
recipe local_vars() {
    let a = 1
    let b = 2
    let c = 3
    let d = 4
    let e = 5
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should allocate stack space for local variables
        int allocaCount = llvmIr.Split(separator: "alloca")
                                .Length - 1;
        Assert.True(condition: allocaCount >= 5); // At least 5 allocations
    }

    [Fact]
    public void TestTypeConversions()
    {
        string code = @"
recipe conversions() {
    let small: s8 = 10
    let big: s32 = small  # Implicit widening
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should handle type conversions
        Assert.Contains(expectedSubstring: "sext",
            actualString: llvmIr); // Sign extension for widening
    }

    [Fact]
    public void TestNullReturn()
    {
        string code = @"
recipe void_func() {
    let x = 42
    # No explicit return
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should handle void functions properly
        Assert.Contains(expectedSubstring: "ret void", actualString: llvmIr);
    }

    [Fact]
    public void TestMultipleReturnPaths()
    {
        string code = @"
recipe multi_return(x: s32) -> s32 {
    if x > 0:
        return 1
    elif x < 0:
        return -1
    else:
        return 0
}";

        string llvmIr = GenerateCode(code: code);

        Assert.NotNull(@object: llvmIr);
        // Should handle multiple return paths
        int retCount = llvmIr.Split(separator: "ret")
                             .Length - 1;
        Assert.True(condition: retCount >= 3); // At least 3 return instructions
    }
}
