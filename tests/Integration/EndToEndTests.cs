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

namespace RazorForge.Tests.Integration;

/// <summary>
/// End-to-end integration tests for the complete RazorForge compiler pipeline
/// </summary>
public class EndToEndTests
{
    private readonly SemanticAnalyzer _analyzer;
    private readonly LLVMCodeGenerator _codeGenerator;

    public EndToEndTests()
    {
        _analyzer = new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal);
        _codeGenerator =
            new LLVMCodeGenerator(language: Language.RazorForge, mode: LanguageMode.Normal);
    }

    private string CompileToLLVM(string code)
    {
        // Tokenize
        List<Token> tokens = Tokenizer.Tokenize(source: code, language: Language.RazorForge);
        Assert.NotEmpty(collection: tokens);

        // Parse
        var parser = new RazorForgeParser(tokens: tokens);
        Program program = parser.Parse();
        Assert.NotNull(@object: program);

        // Semantic Analysis
        _analyzer.Analyze(program: program);

        // Code Generation
        _codeGenerator.Generate(program: program);
        string llvmIr = _codeGenerator.GetGeneratedCode();
        Assert.NotNull(@object: llvmIr);
        Assert.NotEmpty(collection: llvmIr);

        return llvmIr;
    }

    [Fact]
    public void TestHelloWorldProgram()
    {
        string code = @"
routine main() -> s32 {
    print(""Hello, World!"")
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Verify the generated LLVM IR contains expected elements
        Assert.Contains(expectedSubstring: "define", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "Hello, World!", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "ret", actualString: llvmIr);
    }

    [Fact]
    public void TestFactorialProgram()
    {
        string code = @"
routine factorial(n: s32) -> s32 {
    if n <= 1:
        return 1
    else:
        return n * factorial(n - 1)
}

routine main() -> s32 {
    let result = factorial(5)
    print(f""Factorial of 5 is {result}"")
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should compile without errors and contain both functions
        Assert.Contains(expectedSubstring: "factorial", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "call",
            actualString: llvmIr); // Recursive and regular calls
    }

    [Fact]
    public void TestFibonacciProgram()
    {
        string code = @"
routine fibonacci(n: s32) -> s32 {
    if n <= 1:
        return n
    else:
        return fibonacci(n - 1) + fibonacci(n - 2)
}

routine main() -> s32 {
    for i in 0 to 10:
        let fib = fibonacci(i)
        print(f""fib({i}) = {fib}"")
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle recursive function and loops
        Assert.Contains(expectedSubstring: "fibonacci", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestArrayOperationsProgram()
    {
        string code = @"
routine array_sum(arr: DynamicSlice, size: s32) -> s32 {
    var sum = 0
    for i in 0 to size:
        sum = sum + arr[i]
    return sum
}

routine main() -> s32 {
    let numbers = DynamicSlice(10)

    # Initialize array
    for i in 0 to 10:
        numbers[i] = i * i

    let total = array_sum(numbers, 10)
    print(f""Sum of squares: {total}"")
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle memory operations and array access
        Assert.Contains(expectedSubstring: "array_sum", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "DynamicSlice", actualString: llvmIr);
    }

    [Fact]
    public void TestStringManipulationProgram()
    {
        string code = @"
routine string_length(text: Text) -> s32 {
    return text.length
}

routine concatenate(a: Text, b: Text) -> Text {
    return a + b
}

routine main() -> s32 {
    let greeting = ""Hello""
    let target = ""World""
    let message = concatenate(greeting, "" "") + target + ""!""

    let len = string_length(message)
    print(f""Message: {message} (length: {len})"")
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle string operations
        Assert.Contains(expectedSubstring: "string_length", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "concatenate", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "Hello", actualString: llvmIr);
    }

    [Fact]
    public void TestComplexMathProgram()
    {
        string code = @"
routine power(base: f64, exponent: s32) -> f64 {
    if exponent == 0:
        return 1.0
    elif exponent < 0:
        return 1.0 / power(base, -exponent)
    else:
        return base * power(base, exponent - 1)
}

routine main() -> s32 {
    let base = 2.0
    for exp in 0 to 10:
        let result = power(base, exp)
        print(f""{base}^{exp} = {result}"")
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle floating point operations
        Assert.Contains(expectedSubstring: "power", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "double", actualString: llvmIr); // f64 type
    }

    [Fact]
    public void TestMemoryManagementProgram()
    {
        string code = @"
routine memory_test() {
    let heap_buffer = DynamicSlice(1024)
    let stack_buffer = TemporarySlice(256)

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

routine main() -> s32 {
    memory_test()
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle different memory allocation types
        Assert.Contains(expectedSubstring: "DynamicSlice", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "TemporarySlice", actualString: llvmIr);
    }

    [Fact]
    public void TestErrorHandlingProgram()
    {
        string code = @"
routine safe_divide(a: s32, b: s32) -> Option(s32) {
    if b == 0:
        return None
    else:
        return Some(a / b)
}

routine main() -> s32 {
    let result1 = safe_divide(10, 2)
    let result2 = safe_divide(10, 0)

    when result1 is Some(value):
        print(f""10 / 2 = {value}"")

    when result2 is None:
        print(""Division by zero prevented"")

    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle Option types and pattern matching
        Assert.Contains(expectedSubstring: "safe_divide", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestGenericDataStructuresProgram()
    {
        string code = @"
routine list_operations() {
    let numbers = List(s32)()

    # Add elements
    numbers.add(1)
    numbers.add(2)
    numbers.add(3)

    # Iterate
    for num in numbers:
        print(f""Number: {num}"")

    let sum = numbers.fold(0, routine(acc, x) -> acc + x)
    print(f""Sum: {sum}"")
}

routine main() -> s32 {
    list_operations()
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle generic types and higher-order functions
        Assert.Contains(expectedSubstring: "list_operations", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestConcurrencyProgram()
    {
        string code = @"
routine worker(id: s32, data: ThreadShared(s32)) {
    for i in 0 to 1000:
        let old_value = data.get()
        data.set(old_value + 1)
    print(f""Worker {id} finished"")
}

routine main() -> s32 {
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

        string llvmIr = CompileToLLVM(code: code);

        // Should handle concurrency primitives
        Assert.Contains(expectedSubstring: "worker", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "ThreadShared", actualString: llvmIr);
    }

    [Fact]
    public void TestDangerModeProgram()
    {
        string code = @"
routine unsafe_memory_operations() {
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

routine main() -> s32 {
    unsafe_memory_operations()
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle unsafe operations in danger blocks
        Assert.Contains(expectedSubstring: "unsafe_memory_operations", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestOverflowHandlingProgram()
    {
        string code = @"
routine overflow_demo() {
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

routine main() -> s32 {
    overflow_demo()
    return 0
}";

        string llvmIr = CompileToLLVM(code: code);

        // Should handle different overflow semantics
        Assert.Contains(expectedSubstring: "overflow_demo", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestCompleteApplicationProgram()
    {
        string code = @"
# Simple calculator application
routine parse_number(input: Text) -> Option(s32) {
    # Simplified number parsing
    if input == ""0"": return Some(0)
    elif input == ""1"": return Some(1)
    elif input == ""2"": return Some(2)
    else: return None
}

routine calculator(op: Text, a: s32, b: s32) -> Result(s32, Text) {
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

routine main() -> s32 {
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

        string llvmIr = CompileToLLVM(code: code);

        // Should compile a complete application
        Assert.Contains(expectedSubstring: "parse_number", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "calculator", actualString: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestCompilerPerformance()
    {
        // Test compilation of a large program
        string code = @"
routine main() -> s32 {";

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
        string llvmIr = CompileToLLVM(code: code);
        stopwatch.Stop();

        // Should compile within reasonable time (less than 10 seconds)
        Assert.True(condition: stopwatch.ElapsedMilliseconds < 10000);
        Assert.NotNull(@object: llvmIr);
        Assert.Contains(expectedSubstring: "main", actualString: llvmIr);
    }

    [Fact]
    public void TestCompilerMemoryUsage()
    {
        // Test that compiler doesn't leak memory during compilation
        long initialMemory = GC.GetTotalMemory(forceFullCollection: true);

        for (int i = 0; i < 10; i++)
        {
            string code = @"
routine test_function() -> s32 {
    let x = 42
    return x * 2
}";
            CompileToLLVM(code: code);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long finalMemory = GC.GetTotalMemory(forceFullCollection: true);
        long memoryIncrease = finalMemory - initialMemory;

        // Memory increase should be reasonable (less than 10MB)
        Assert.True(condition: memoryIncrease < 10 * 1024 * 1024);
    }
}
