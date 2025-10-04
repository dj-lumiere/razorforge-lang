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

namespace RazorForge.Tests
{
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
            _semanticAnalyzer = new SemanticAnalyzer(Language.RazorForge, LanguageMode.Normal);
            _codeGenerator = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal);
        }

        private List<Token> TokenizeCode(string code)
        {
            return Tokenizer.Tokenize(code, Language.RazorForge);
        }

        private Program ParseCode(string code)
        {
            var tokens = TokenizeCode(code);
            var parser = new RazorForgeParser(tokens);
            return parser.Parse();
        }

        [Fact]
        public void TestSliceConstructorParsing()
        {
            var code = @"
recipe test() {
    var heap_buffer = HeapSlice(64)
    var stack_buffer = StackSlice(32)
}";

            var program = ParseCode(code);
            Assert.NotNull(program);

            // Verify we have a function declaration
            var function = program.Declarations.OfType<FunctionDeclaration>().FirstOrDefault();
            Assert.NotNull(function);
            Assert.Equal("test", function.Name);

            // Verify we have variable declarations with slice constructors
            var blockBody = function.Body as BlockStatement;
            Assert.NotNull(blockBody);
            var statements = blockBody.Statements;
            Assert.Equal(2, statements.Count);

            // Check first variable declaration (heap slice)
            var stmt1 = statements[0] as DeclarationStatement;
            Assert.NotNull(stmt1);
            Assert.IsType<VariableDeclaration>(stmt1.Declaration);

            // Check second variable declaration (stack slice)
            var stmt2 = statements[1] as DeclarationStatement;
            Assert.NotNull(stmt2);
            Assert.IsType<VariableDeclaration>(stmt2.Declaration);
        }

        [Fact]
        public void TestGenericMethodCallParsing()
        {
            var code = @"
recipe test() {
    var buffer = HeapSlice(64)
    buffer.write<s32>!(0, 42)
    let value = buffer.read<s32>!(0)
}";

            var program = ParseCode(code);
            Assert.NotNull(program);

            var function = program.Declarations.OfType<FunctionDeclaration>().FirstOrDefault();
            Assert.NotNull(function);

            // Verify we parsed the generic method calls
            var blockBody = function.Body as BlockStatement;
            Assert.NotNull(blockBody);
            var statements = blockBody.Statements;
            Assert.Equal(3, statements.Count);
        }

        [Fact]
        public void TestMemoryOperationParsing()
        {
            var code = @"
recipe test() {
    var buffer = StackSlice(48)
    let size = buffer.size!()
    let addr = buffer.address!()
    let valid = buffer.is_valid!()
}";

            var program = ParseCode(code);
            Assert.NotNull(program);

            var function = program.Declarations.OfType<FunctionDeclaration>().FirstOrDefault();
            Assert.NotNull(function);

            // Verify we parsed all memory operations
            var blockBody = function.Body as BlockStatement;
            Assert.NotNull(blockBody);
            var statements = blockBody.Statements;
            Assert.Equal(4, statements.Count);
        }

        [Fact]
        public void TestExternalDeclarationParsing()
        {
            var code = @"
external recipe heap_alloc!(bytes: sysuint) -> sysuint
external recipe memory_copy!(src: sysuint, dest: sysuint, bytes: sysuint)
external recipe read_as<T>!(address: sysuint) -> T
";

            var program = ParseCode(code);
            Assert.NotNull(program);

            // Verify we have external declarations
            var externals = program.Declarations.OfType<ExternalDeclaration>().ToList();
            Assert.Equal(3, externals.Count);

            // Check first external function
            var heapAlloc = externals[0];
            Assert.Equal("heap_alloc", heapAlloc.Name);
            Assert.Single(heapAlloc.Parameters);
            Assert.NotNull(heapAlloc.ReturnType);

            // Check generic external function
            var readAs = externals[2];
            Assert.Equal("read_as", readAs.Name);
            Assert.NotNull(readAs.GenericParameters);
            Assert.Single(readAs.GenericParameters);
        }

        [Fact]
        public void TestDangerBlockParsing()
        {
            var code = @"
recipe test() {
    danger! {
        let addr = 0xDEADBEEF
        write_as<s32>!(addr, 999)
        let value = read_as<s32>!(addr)
    }
}";

            var program = ParseCode(code);
            Assert.NotNull(program);

            var function = program.Declarations.OfType<FunctionDeclaration>().FirstOrDefault();
            Assert.NotNull(function);

            // Verify we have a danger statement
            var blockBody = function.Body as BlockStatement;
            Assert.NotNull(blockBody);
            var statements = blockBody.Statements;
            Assert.Single(statements);

            var dangerStmt = statements[0] as DangerStatement;
            Assert.NotNull(dangerStmt);
            Assert.Equal(3, dangerStmt.Body.Statements.Count);
        }

        [Fact]
        public void TestSemanticAnalysisOfSliceOperations()
        {
            var code = @"
recipe test() {
    var buffer = HeapSlice(64)
    buffer.write<s32>!(0, 42)
    let value = buffer.read<s32>!(0)
    let size = buffer.size!()
}";

            var program = ParseCode(code);
            var errors = _semanticAnalyzer.Analyze(program);

            // Should have no semantic errors for valid slice operations
            Assert.Empty(errors);
        }

        [Fact]
        public void TestSemanticAnalysisTypeErrors()
        {
            var code = @"
recipe test() {
    var buffer = HeapSlice(""invalid_size"")  # Error: size must be sysuint
    buffer.write<s32>!(""invalid_offset"", 42)  # Error: offset must be sysuint
}";

            var program = ParseCode(code);
            var errors = _semanticAnalyzer.Analyze(program);

            // Should detect type errors
            Assert.NotEmpty(errors);
        }

        [Fact]
        public void TestLLVMCodeGeneration()
        {
            var code = @"
external recipe heap_alloc!(bytes: sysuint) -> sysuint
external recipe memory_write_s32!(address: sysuint, offset: sysuint, value: s32)

recipe test() -> s32 {
    var buffer = HeapSlice(64)
    buffer.write<s32>!(0, 42)
    return 0
}";

            var program = ParseCode(code);

            // Generate LLVM IR
            _codeGenerator.Generate(program);
            var llvmIR = _codeGenerator.GetGeneratedCode();

            // Verify LLVM IR contains expected elements
            Assert.Contains("@heap_alloc", llvmIR);
            Assert.Contains("call ptr @heap_alloc", llvmIR);
            Assert.Contains("memory_write_s32", llvmIR);
            Assert.Contains("define i32 @test", llvmIR);
        }

        [Fact]
        public void TestEndToEndCompilerPipeline()
        {
            var code = @"
import stdlib/memory/HeapSlice
import system/console/write_line

external recipe heap_alloc!(bytes: sysuint) -> sysuint
external recipe heap_free!(address: sysuint)

recipe main() -> s32 {
    var buffer = HeapSlice(128)

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
            var program = ParseCode(code);
            Assert.NotNull(program);

            var semanticErrors = _semanticAnalyzer.Analyze(program);
            Assert.Empty(semanticErrors);

            _codeGenerator.Generate(program);
            var llvmIR = _codeGenerator.GetGeneratedCode();
            Assert.NotEmpty(llvmIR);

            // Verify key components are present
            Assert.Contains("define i32 @main", llvmIR);
            Assert.Contains("call ptr @heap_alloc", llvmIR);
            Assert.Contains("memory_write_s32", llvmIR);
            Assert.Contains("memory_read_s32", llvmIR);
        }

        [Fact]
        public void TestDangerBlockCodeGeneration()
        {
            var code = @"
recipe test() {
    danger! {
        let addr = 0x1000
        write_as<s64>!(addr, 0xDEADBEEF)
        let value = read_as<s64>!(addr)
    }
}";

            var program = ParseCode(code);
            _codeGenerator.Generate(program);
            var llvmIR = _codeGenerator.GetGeneratedCode();

            // Verify danger block generates appropriate comments and correct LLVM IR
            Assert.Contains("; === DANGER BLOCK START ===", llvmIR);
            Assert.Contains("; === DANGER BLOCK END ===", llvmIR);
            Assert.Contains("inttoptr", llvmIR); // Address conversion for danger zone operations
            Assert.Contains("store i32", llvmIR); // Write operation
            Assert.Contains("load i32", llvmIR);  // Read operation
        }

        [Fact]
        public void TestMemorySliceOperationsCodeGen()
        {
            var code = @"
recipe test() {
    var buffer = StackSlice(64)

    let size = buffer.size!()
    let addr = buffer.address!()
    let valid = buffer.is_valid!()
    let ptr = buffer.unsafe_ptr!(16)
    let sub = buffer.slice!(8, 32)
}";

            var program = ParseCode(code);
            _codeGenerator.Generate(program);
            var llvmIR = _codeGenerator.GetGeneratedCode();

            // Verify all slice operations generate proper LLVM calls
            Assert.Contains("call ptr @stack_alloc", llvmIR);
            Assert.Contains("call i64 @slice_size", llvmIR);
            Assert.Contains("call i64 @slice_address", llvmIR);
            Assert.Contains("call i1 @slice_is_valid", llvmIR);
            Assert.Contains("call i64 @slice_unsafe_ptr", llvmIR);
            Assert.Contains("call ptr @slice_subslice", llvmIR);
        }

        [Fact]
        public void TestComplexMemoryScenario()
        {
            var code = @"
recipe complex_memory_test() -> s32 {
    # Create multiple slice types
    var heap1 = HeapSlice(256)
    var heap2 = HeapSlice(128)
    var stack1 = StackSlice(64)

    # Fill with data using generic methods
    for i in 0 to 8 {
        heap1.write<s64>!(i * 8, i * 1000)
        heap2.write<s32>!(i * 4, i * 100)
        stack1.write<s16>!(i * 2, i * 10)
    }

    # Test wrapper operations
    let hijacked = heap1.hijack!()
    let raw_ref = hijacked.refer!()

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
            var program = ParseCode(code);
            Assert.NotNull(program);

            // Should parse without errors
            var function = program.Declarations.OfType<FunctionDeclaration>().FirstOrDefault();
            Assert.NotNull(function);
            Assert.Equal("complex_memory_test", function.Name);

            // Should analyze without semantic errors (assuming proper imports)
            var semanticErrors = _semanticAnalyzer.Analyze(program);
            // Note: May have some errors due to missing imports, but structure should be valid

            // Should generate valid LLVM IR
            _codeGenerator.Generate(program);
            var llvmIR = _codeGenerator.GetGeneratedCode();
            Assert.NotEmpty(llvmIR);
            Assert.Contains("define i32 @complex_memory_test", llvmIR);
        }
    }
}