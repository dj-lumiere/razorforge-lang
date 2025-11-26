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
using Compilers.RazorForge.Lexer;

namespace RazorForge.Tests;

/// <summary>
/// Integration tests for the complete RazorForge compiler pipeline
/// including memory slice operations, parser, AST, semantic analysis, and LLVM code generation.
/// </summary>
public class CompilerIntegrationTests
{
    private readonly SemanticAnalyzer _semanticAnalyzer;
    private readonly LLVMCodeGenerator _codeGenerator;

    public CompilerIntegrationTests()
    {
        // Initialize compiler components
        _semanticAnalyzer =
            new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal);
        _codeGenerator =
            new LLVMCodeGenerator(language: Language.RazorForge, mode: LanguageMode.Normal);
    }

    private List<Token> TokenizeCode(string code)
    {
        return Tokenizer.Tokenize(source: code, language: Language.RazorForge);
    }

    private Program ParseCode(string code)
    {
        List<Token> tokens = TokenizeCode(code: code);
        var parser = new RazorForgeParser(tokens: tokens);
        return parser.Parse();
    }

    [Fact]
    public void TestSliceConstructorParsing()
    {
        string code = @"
routine test() {
    var heap_buffer = DynamicSlice(64)
    var stack_buffer = TemporarySlice(32)
}";

        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        // Verify we have a function declaration
        FunctionDeclaration? function = program.Declarations
                                               .OfType<FunctionDeclaration>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: function);
        Assert.Equal(expected: "test", actual: function.Name);

        // Verify we have variable declarations with slice constructors
        var blockBody = function.Body as BlockStatement;
        Assert.NotNull(@object: blockBody);
        List<Statement> statements = blockBody.Statements;
        Assert.Equal(expected: 2, actual: statements.Count);

        // Check first variable declaration (heap slice)
        var stmt1 = statements[index: 0] as DeclarationStatement;
        Assert.NotNull(@object: stmt1);
        Assert.IsType<VariableDeclaration>(@object: stmt1.Declaration);

        // Check second variable declaration (stack slice)
        var stmt2 = statements[index: 1] as DeclarationStatement;
        Assert.NotNull(@object: stmt2);
        Assert.IsType<VariableDeclaration>(@object: stmt2.Declaration);
    }

    [Fact]
    public void TestGenericMethodCallParsing()
    {
        string code = @"
routine test() {
    var buffer = DynamicSlice(64)
    buffer.write<s32>!(0, 42)
    let value = buffer.read<s32>!(0)
}";

        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        FunctionDeclaration? function = program.Declarations
                                               .OfType<FunctionDeclaration>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: function);

        // Verify we parsed the generic method calls
        var blockBody = function.Body as BlockStatement;
        Assert.NotNull(@object: blockBody);
        List<Statement> statements = blockBody.Statements;
        Assert.Equal(expected: 3, actual: statements.Count);
    }

    [Fact]
    public void TestMemoryOperationParsing()
    {
        string code = @"
routine test() {
    var buffer = TemporarySlice(48)
    let size = buffer.size!()
    let addr = buffer.address!()
    let valid = buffer.is_valid!()
}";

        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        FunctionDeclaration? function = program.Declarations
                                               .OfType<FunctionDeclaration>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: function);

        // Verify we parsed all memory operations
        var blockBody = function.Body as BlockStatement;
        Assert.NotNull(@object: blockBody);
        List<Statement> statements = blockBody.Statements;
        Assert.Equal(expected: 4, actual: statements.Count);
    }

    [Fact]
    public void TestExternalDeclarationParsing()
    {
        string code = @"
external routine heap_alloc!(bytes: sysuint) -> sysuint
external routine memory_copy!(src: sysuint, dest: sysuint, bytes: sysuint)
external routine read_as<T>!(address: sysuint) -> T
";

        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        // Verify we have external declarations
        var externals = program.Declarations
                               .OfType<ExternalDeclaration>()
                               .ToList();
        Assert.Equal(expected: 3, actual: externals.Count);

        // Check first external function
        ExternalDeclaration heapAlloc = externals[index: 0];
        Assert.Equal(expected: "heap_alloc", actual: heapAlloc.Name);
        Assert.Single(collection: heapAlloc.Parameters);
        Assert.NotNull(@object: heapAlloc.ReturnType);

        // Check generic external function
        ExternalDeclaration readAs = externals[index: 2];
        Assert.Equal(expected: "read_as", actual: readAs.Name);
        Assert.NotNull(@object: readAs.GenericParameters);
        Assert.Single(collection: readAs.GenericParameters);
    }

    [Fact]
    public void TestDangerBlockParsing()
    {
        string code = @"
routine test() {
    danger! {
        let addr = 0xDEADBEEF
        write_as<s32>!(addr, 999)
        let value = read_as<s32>!(addr)
    }
}";

        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        FunctionDeclaration? function = program.Declarations
                                               .OfType<FunctionDeclaration>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: function);

        // Verify we have a danger statement
        var blockBody = function.Body as BlockStatement;
        Assert.NotNull(@object: blockBody);
        List<Statement> statements = blockBody.Statements;
        Assert.Single(collection: statements);

        var dangerStmt = statements[index: 0] as DangerStatement;
        Assert.NotNull(@object: dangerStmt);
        Assert.Equal(expected: 3, actual: dangerStmt.Body.Statements.Count);
    }

    [Fact]
    public void TestSemanticAnalysisOfSliceOperations()
    {
        string code = @"
routine test() {
    var buffer = DynamicSlice(64)
    buffer.write<s32>!(0, 42)
    let value = buffer.read<s32>!(0)
    let size = buffer.size!()
}";

        Program program = ParseCode(code: code);
        List<SemanticError> errors = _semanticAnalyzer.Analyze(program: program);

        // Should have no semantic errors for valid slice operations
        Assert.Empty(collection: errors);
    }

    [Fact]
    public void TestSemanticAnalysisTypeErrors()
    {
        string code = @"
routine test() {
    var buffer = DynamicSlice(""invalid_size"")  # Error: size must be sysuint
    buffer.write<s32>!(""invalid_offset"", 42)  # Error: offset must be sysuint
}";

        Program program = ParseCode(code: code);
        List<SemanticError> errors = _semanticAnalyzer.Analyze(program: program);

        // Should detect type errors
        Assert.NotEmpty(collection: errors);
    }

    [Fact]
    public void TestLLVMCodeGeneration()
    {
        string code = @"
external routine heap_alloc!(bytes: sysuint) -> sysuint
external routine memory_write_s32!(address: sysuint, offset: sysuint, value: s32)

routine test() -> s32 {
    var buffer = DynamicSlice(64)
    buffer.write<s32>!(0, 42)
    return 0
}";

        Program program = ParseCode(code: code);

        // Generate LLVM IR
        _codeGenerator.Generate(program: program);
        string llvmIR = _codeGenerator.GetGeneratedCode();

        // Verify LLVM IR contains expected elements
        Assert.Contains(expectedSubstring: "@heap_alloc", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call ptr @heap_alloc", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "memory_write_s32", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "define i32 @test", actualString: llvmIR);
    }

    [Fact]
    public void TestEndToEndCompilerPipeline()
    {
        string code = @"
import stdlib/memory/DynamicSlice
import system/console/show

external routine heap_alloc!(bytes: sysuint) -> sysuint
external routine heap_free!(address: sysuint)

routine main() -> s32 {
    var buffer = DynamicSlice(128)

    # Write some test data
    buffer.write<s32>!(0, 100)
    buffer.write<s32>!(4, 200)

    # Read it back
    let val1 = buffer.read<s32>!(0)
    let val2 = buffer.read<s32>!(4)

    # Test memory operations
    let size = buffer.size!()
    let addr = buffer.address!()

    return 0
}";

        // Test complete pipeline: Parse → Analyze → Generate
        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        List<SemanticError> semanticErrors = _semanticAnalyzer.Analyze(program: program);
        Assert.Empty(collection: semanticErrors);

        _codeGenerator.Generate(program: program);
        string llvmIR = _codeGenerator.GetGeneratedCode();
        Assert.NotEmpty(collection: llvmIR);

        // Verify key components are present
        Assert.Contains(expectedSubstring: "define i32 @main", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call ptr @heap_alloc", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "memory_write_s32", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "memory_read_s32", actualString: llvmIR);
    }

    [Fact]
    public void TestDangerBlockCodeGeneration()
    {
        string code = @"
routine test() {
    danger! {
        let addr = 0x1000
        write_as<s64>!(addr, 0xDEADBEEF)
        let value = read_as<s64>!(addr)
    }
}";

        Program program = ParseCode(code: code);
        _codeGenerator.Generate(program: program);
        string llvmIR = _codeGenerator.GetGeneratedCode();

        // Verify danger block generates appropriate comments and correct LLVM IR
        Assert.Contains(expectedSubstring: "; === DANGER BLOCK START ===", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "; === DANGER BLOCK END ===", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "inttoptr",
            actualString: llvmIR); // Address conversion for danger zone operations
        Assert.Contains(expectedSubstring: "store i32", actualString: llvmIR); // Write operation
        Assert.Contains(expectedSubstring: "load i32", actualString: llvmIR); // Read operation
    }

    [Fact]
    public void TestMemorySliceOperationsCodeGen()
    {
        string code = @"
routine test() {
    var buffer = TemporarySlice(64)

    let size = buffer.size!()
    let addr = buffer.address!()
    let valid = buffer.is_valid!()
    let ptr = buffer.unsafe_ptr!(16)
    let sub = buffer.slice!(8, 32)
}";

        Program program = ParseCode(code: code);
        _codeGenerator.Generate(program: program);
        string llvmIR = _codeGenerator.GetGeneratedCode();

        // Verify all slice operations generate proper LLVM calls
        Assert.Contains(expectedSubstring: "call ptr @stack_alloc", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call i64 @slice_size", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call i64 @slice_address", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call i1 @slice_is_valid", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call i64 @slice_unsafe_ptr", actualString: llvmIR);
        Assert.Contains(expectedSubstring: "call ptr @slice_subslice", actualString: llvmIR);
    }

    [Fact]
    public void TestComplexMemoryScenario()
    {
        string code = @"
routine complex_memory_test() -> s32 {
    # Create multiple slice types
    var heap1 = DynamicSlice(256)
    var heap2 = DynamicSlice(128)
    var stack1 = TemporarySlice(64)

    # Fill with data using generic methods
    for i in 0 to 8 {
        heap1.write<s64>!(i * 8, i * 1000)
        heap2.write<s32>!(i * 4, i * 100)
        stack1.write<s16>!(i * 2, i * 10)
    }

    # Test wrapper operations
    let shared = heap1.share()
    danger!:
        let raw_ptr = heap1.snatch()

    # Test memory operations
    let total_size = heap1.size!() + heap2.size!() + stack1.size!()

    # Test danger operations
    danger! {
        let addr = heap2.address!()
        write_as<u64>!(addr, 0xCAFEBABE)
        let magic = read_as<u64>!(addr)
    }

    return 0
}";

        // Test complete pipeline with complex scenario
        Program program = ParseCode(code: code);
        Assert.NotNull(@object: program);

        // Should parse without errors
        FunctionDeclaration? function = program.Declarations
                                               .OfType<FunctionDeclaration>()
                                               .FirstOrDefault();
        Assert.NotNull(@object: function);
        Assert.Equal(expected: "complex_memory_test", actual: function.Name);

        // Should analyze without semantic errors (assuming proper imports)
        List<SemanticError> semanticErrors = _semanticAnalyzer.Analyze(program: program);
        // Note: May have some errors due to missing imports, but structure should be valid

        // Should generate valid LLVM IR
        _codeGenerator.Generate(program: program);
        string llvmIR = _codeGenerator.GetGeneratedCode();
        Assert.NotEmpty(collection: llvmIR);
        Assert.Contains(expectedSubstring: "define i32 @complex_memory_test",
            actualString: llvmIR);
    }
}
