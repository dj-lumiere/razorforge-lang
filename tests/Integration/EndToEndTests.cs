using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using Xunit;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.CodeGen;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;

namespace RazorForge.Tests.Integration
{
    /// <summary>
    /// End-to-end integration tests for the complete RazorForge compiler pipeline
    /// </summary>
    public class EndToEndTests
    {
        private readonly SemanticAnalyzer _analyzer;
        private readonly LLVMCodeGenerator _codeGenerator;

        public EndToEndTests()
        {
            _analyzer = new SemanticAnalyzer(Language.RazorForge, LanguageMode.Normal);
            _codeGenerator = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal);
        }

        private string CompileToLLVM(string code)
        {
            // Tokenize
            var tokens = Tokenizer.Tokenize(code, Language.RazorForge);
            Assert.NotEmpty(tokens);

            // Parse
            var parser = new RazorForgeParser(tokens);
            var program = parser.Parse();
            Assert.NotNull(program);

            // Semantic Analysis
            _analyzer.Analyze(program);

            // Code Generation
            _codeGenerator.Generate(program);
            var llvmIr = _codeGenerator.GetGeneratedCode();
            Assert.NotNull(llvmIr);
            Assert.NotEmpty(llvmIr);

            return llvmIr;
        }

        [Fact]
        public void TestHelloWorldProgram()
        {
            var code = @"
recipe main() -> s32 {
    print(""Hello, World!"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Verify the generated LLVM IR contains expected elements
            Assert.Contains("define", llvmIr);
            Assert.Contains("main", llvmIr);
            Assert.Contains("Hello, World!", llvmIr);
            Assert.Contains("ret", llvmIr);
        }

        [Fact]
        public void TestFactorialProgram()
        {
            var code = @"
recipe factorial(n: s32) -> s32 {
    if n <= 1:
        return 1
    else:
        return n * factorial(n - 1)
}

recipe main() -> s32 {
    let result = factorial(5)
    print(f""Factorial of 5 is {result}"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should compile without errors and contain both functions
            Assert.Contains("factorial", llvmIr);
            Assert.Contains("main", llvmIr);
            Assert.Contains("call", llvmIr); // Recursive and regular calls
        }

        [Fact]
        public void TestFibonacciProgram()
        {
            var code = @"
recipe fibonacci(n: s32) -> s32 {
    if n <= 1:
        return n
    else:
        return fibonacci(n - 1) + fibonacci(n - 2)
}

recipe main() -> s32 {
    for i in 0 to 10:
        let fib = fibonacci(i)
        print(f""fib({i}) = {fib}"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle recursive function and loops
            Assert.Contains("fibonacci", llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestArrayOperationsProgram()
        {
            var code = @"
recipe array_sum(arr: HeapSlice, size: s32) -> s32 {
    var sum = 0
    for i in 0 to size:
        sum = sum + arr[i]
    return sum
}

recipe main() -> s32 {
    let numbers = HeapSlice(10)

    # Initialize array
    for i in 0 to 10:
        numbers[i] = i * i

    let total = array_sum(numbers, 10)
    print(f""Sum of squares: {total}"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle memory operations and array access
            Assert.Contains("array_sum", llvmIr);
            Assert.Contains("HeapSlice", llvmIr);
        }

        [Fact]
        public void TestStringManipulationProgram()
        {
            var code = @"
recipe string_length(text: Text) -> s32 {
    return text.length
}

recipe concatenate(a: Text, b: Text) -> Text {
    return a + b
}

recipe main() -> s32 {
    let greeting = ""Hello""
    let target = ""World""
    let message = concatenate(greeting, "" "") + target + ""!""

    let len = string_length(message)
    print(f""Message: {message} (length: {len})"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle string operations
            Assert.Contains("string_length", llvmIr);
            Assert.Contains("concatenate", llvmIr);
            Assert.Contains("Hello", llvmIr);
        }

        [Fact]
        public void TestComplexMathProgram()
        {
            var code = @"
recipe power(base: f64, exponent: s32) -> f64 {
    if exponent == 0:
        return 1.0
    elif exponent < 0:
        return 1.0 / power(base, -exponent)
    else:
        return base * power(base, exponent - 1)
}

recipe main() -> s32 {
    let base = 2.0
    for exp in 0 to 10:
        let result = power(base, exp)
        print(f""{base}^{exp} = {result}"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle floating point operations
            Assert.Contains("power", llvmIr);
            Assert.Contains("double", llvmIr); // f64 type
        }

        [Fact]
        public void TestMemoryManagementProgram()
        {
            var code = @"
recipe memory_test() {
    let heap_buffer = HeapSlice(1024)
    let stack_buffer = StackSlice(256)

    # Write to heap buffer
    for i in 0 to 100:
        heap_buffer[i] = i * 2

    # Copy to stack buffer
    for i in 0 to 100:
        stack_buffer[i] = heap_buffer[i]

    # Verify data
    var sum = 0
    for i in 0 to 100:
        sum = sum + stack_buffer[i]

    print(f""Sum: {sum}"")
}

recipe main() -> s32 {
    memory_test()
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle different memory allocation types
            Assert.Contains("HeapSlice", llvmIr);
            Assert.Contains("StackSlice", llvmIr);
        }

        [Fact]
        public void TestErrorHandlingProgram()
        {
            var code = @"
recipe safe_divide(a: s32, b: s32) -> Option(s32) {
    if b == 0:
        return None
    else:
        return Some(a / b)
}

recipe main() -> s32 {
    let result1 = safe_divide(10, 2)
    let result2 = safe_divide(10, 0)

    when result1 is Some(value):
        print(f""10 / 2 = {value}"")

    when result2 is None:
        print(""Division by zero prevented"")

    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle Option types and pattern matching
            Assert.Contains("safe_divide", llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestGenericDataStructuresProgram()
        {
            var code = @"
recipe list_operations() {
    let numbers = List(s32)()

    # Add elements
    numbers.add(1)
    numbers.add(2)
    numbers.add(3)

    # Iterate
    for num in numbers:
        print(f""Number: {num}"")

    let sum = numbers.fold(0, recipe(acc, x) -> acc + x)
    print(f""Sum: {sum}"")
}

recipe main() -> s32 {
    list_operations()
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle generic types and higher-order functions
            Assert.Contains("list_operations", llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestConcurrencyProgram()
        {
            var code = @"
recipe worker(id: s32, data: ThreadShared(s32)) {
    for i in 0 to 1000:
        let old_value = data.get()
        data.set(old_value + 1)
    print(f""Worker {id} finished"")
}

recipe main() -> s32 {
    let shared_counter = ThreadShared(s32)(0)

    # Spawn worker threads
    spawn worker(1, shared_counter)
    spawn worker(2, shared_counter)
    spawn worker(3, shared_counter)

    # Wait for completion
    thread_join_all()

    let final_value = shared_counter.get()
    print(f""Final counter value: {final_value}"")
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle concurrency primitives
            Assert.Contains("worker", llvmIr);
            Assert.Contains("ThreadShared", llvmIr);
        }

        [Fact]
        public void TestDangerModeProgram()
        {
            var code = @"
recipe unsafe_memory_operations() {
    danger {
        let ptr = malloc(1024)

        # Direct memory manipulation
        *ptr = 42
        *(ptr + 1) = 43

        # Read back values
        let val1 = *ptr
        let val2 = *(ptr + 1)

        print(f""Values: {val1}, {val2}"")

        free(ptr)
    }
}

recipe main() -> s32 {
    unsafe_memory_operations()
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle unsafe operations in danger blocks
            Assert.Contains("unsafe_memory_operations", llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestOverflowHandlingProgram()
        {
            var code = @"
recipe overflow_demo() {
    let a: u8 = 200
    let b: u8 = 100

    # Different overflow behaviors
    let wrapped = a +% b      # Wrapping add (44)
    let saturated = a +^ b    # Saturating add (255)

    danger {
        let unchecked = a +! b    # Unchecked add (undefined)
    }

    # Checked add with error handling
    let checked_result = a +? b
    when checked_result is Err(overflow_error):
        print(""Overflow detected!"")

    print(f""Wrapped: {wrapped}, Saturated: {saturated}"")
}

recipe main() -> s32 {
    overflow_demo()
    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should handle different overflow semantics
            Assert.Contains("overflow_demo", llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestCompleteApplicationProgram()
        {
            var code = @"
# Simple calculator application
recipe parse_number(input: Text) -> Option(s32) {
    # Simplified number parsing
    if input == ""0"": return Some(0)
    elif input == ""1"": return Some(1)
    elif input == ""2"": return Some(2)
    else: return None
}

recipe calculator(op: Text, a: s32, b: s32) -> Result(s32, Text) {
    when op is:
        ""+"": return Ok(a + b)
        ""-"": return Ok(a - b)
        ""*"": return Ok(a * b)
        ""/"":
            if b == 0:
                return Err(""Division by zero"")
            else:
                return Ok(a / b)
        _: return Err(""Unknown operation"")
}

recipe main() -> s32 {
    let input_a = ""2""
    let input_b = ""3""
    let operation = ""+""

    let num_a = parse_number(input_a)
    let num_b = parse_number(input_b)

    when num_a is Some(a) and num_b is Some(b):
        let result = calculator(operation, a, b)
        when result is:
            Ok(value): print(f""{a} {operation} {b} = {value}"")
            Err(message): print(f""Error: {message}"")
    else:
        print(""Invalid input numbers"")

    return 0
}";

            var llvmIr = CompileToLLVM(code);

            // Should compile a complete application
            Assert.Contains("parse_number", llvmIr);
            Assert.Contains("calculator", llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestCompilerPerformance()
        {
            // Test compilation of a large program
            var code = @"
recipe main() -> s32 {";

            // Generate a large function with many operations
            for (int i = 0; i < 100; i++)
            {
                code += $@"
    let var{i} = {i}
    let result{i} = var{i} * 2 + 1";
            }

            code += @"
    return 0
}";

            var stopwatch = Stopwatch.StartNew();
            var llvmIr = CompileToLLVM(code);
            stopwatch.Stop();

            // Should compile within reasonable time (less than 10 seconds)
            Assert.True(stopwatch.ElapsedMilliseconds < 10000);
            Assert.NotNull(llvmIr);
            Assert.Contains("main", llvmIr);
        }

        [Fact]
        public void TestCompilerMemoryUsage()
        {
            // Test that compiler doesn't leak memory during compilation
            var initialMemory = GC.GetTotalMemory(true);

            for (int i = 0; i < 10; i++)
            {
                var code = @"
recipe test_function() -> s32 {
    let x = 42
    return x * 2
}";
                CompileToLLVM(code);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var finalMemory = GC.GetTotalMemory(true);
            var memoryIncrease = finalMemory - initialMemory;

            // Memory increase should be reasonable (less than 10MB)
            Assert.True(memoryIncrease < 10 * 1024 * 1024);
        }
    }
}