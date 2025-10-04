using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.CodeGen;
using Compilers.Shared.Lexer;
using Compilers.RazorForge.Parser;

namespace RazorForge.Tests;

/// <summary>
/// Utility methods for testing the RazorForge compiler
/// </summary>
public static class TestUtilities
{
    /// <summary>
    /// Tokenize RazorForge source code
    /// </summary>
    public static List<Token> Tokenize(string code)
    {
        return Tokenizer.Tokenize(source: code, language: Language.RazorForge);
    }

    /// <summary>
    /// Parse RazorForge source code into an AST
    /// </summary>
    public static Program Parse(string code)
    {
        List<Token> tokens = Tokenize(code: code);
        var parser = new RazorForgeParser(tokens: tokens);
        return parser.Parse();
    }

    /// <summary>
    /// Parse and analyze RazorForge source code
    /// </summary>
    public static Program Analyze(string code)
    {
        Program program = Parse(code: code);
        var analyzer =
            new SemanticAnalyzer(language: Language.RazorForge, mode: LanguageMode.Normal);
        analyzer.Analyze(program: program);
        return program;
    }

    /// <summary>
    /// Compile RazorForge source code to LLVM IR
    /// </summary>
    public static string CompileToLLVM(string code)
    {
        Program program = Analyze(code: code);
        var codeGenerator =
            new LLVMCodeGenerator(language: Language.RazorForge, mode: LanguageMode.Normal);
        codeGenerator.Generate(program: program);
        return codeGenerator.GetGeneratedCode();
    }

    /// <summary>
    /// Assert that code compilation throws an exception (for error testing)
    /// </summary>
    public static void AssertCompilationError<T>(string code) where T : Exception
    {
        try
        {
            CompileToLLVM(code: code);
            throw new Exception(message: $"Expected {typeof(T).Name} but compilation succeeded");
        }
        catch (T)
        {
            // Expected exception
        }
        catch (Exception ex)
        {
            throw new Exception(
                message: $"Expected {typeof(T).Name} but got {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Assert that code compilation succeeds
    /// </summary>
    public static string AssertCompilationSuccess(string code)
    {
        try
        {
            return CompileToLLVM(code: code);
        }
        catch (Exception ex)
        {
            throw new Exception(
                message:
                $"Expected compilation to succeed but got {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Create a simple recipe with the given body
    /// </summary>
    public static string CreateRecipe(string name, string body, string parameters = "",
        string returnType = "")
    {
        string returnClause = string.IsNullOrEmpty(value: returnType)
            ? ""
            : $" -> {returnType}";
        return $@"
recipe {name}({parameters}){returnClause} {{
{body}
}}";
    }

    /// <summary>
    /// Create a test program with main recipe
    /// </summary>
    public static string CreateProgram(string mainBody, string additionalRecipes = "")
    {
        return $@"
{additionalRecipes}

recipe main() -> s32 {{
{mainBody}
    return 0
}}";
    }

    /// <summary>
    /// Get all tokens of a specific type from code
    /// </summary>
    public static List<Token> GetTokensOfType(string code, TokenType tokenType)
    {
        List<Token> tokens = Tokenize(code: code);
        return tokens.Where(predicate: t => t.Type == tokenType)
                     .ToList();
    }

    /// <summary>
    /// Check if LLVM IR contains specific instructions
    /// </summary>
    public static bool LLVMContains(string llvmIr, params string[] instructions)
    {
        return instructions.All(predicate: instruction => llvmIr.Contains(value: instruction));
    }

    /// <summary>
    /// Count occurrences of a substring in LLVM IR
    /// </summary>
    public static int CountInLLVM(string llvmIr, string substring)
    {
        if (string.IsNullOrEmpty(value: llvmIr) || string.IsNullOrEmpty(value: substring))
        {
            return 0;
        }

        int count = 0;
        int index = 0;
        while ((index = llvmIr.IndexOf(value: substring, startIndex: index)) != -1)
        {
            count++;
            index += substring.Length;
        }

        return count;
    }

    /// <summary>
    /// Create test data for parameterized tests
    /// </summary>
    public static class TestData
    {
        public static readonly object[][] BasicArithmeticOperators = new object[][]
        {
            new object[]
            {
                "+",
                "add"
            },
            new object[]
            {
                "-",
                "sub"
            },
            new object[]
            {
                "*",
                "mul"
            },
            new object[]
            {
                "/",
                "div"
            },
            new object[]
            {
                "%",
                "rem"
            }
        };

        public static readonly object[][] ComparisonOperators = new object[][]
        {
            new object[]
            {
                "==",
                "icmp eq"
            },
            new object[]
            {
                "!=",
                "icmp ne"
            },
            new object[]
            {
                "<",
                "icmp slt"
            },
            new object[]
            {
                "<=",
                "icmp sle"
            },
            new object[]
            {
                ">",
                "icmp sgt"
            },
            new object[]
            {
                ">=",
                "icmp sge"
            }
        };

        public static readonly object[][] IntegerTypes = new object[][]
        {
            new object[]
            {
                "s8",
                "i8"
            },
            new object[]
            {
                "s16",
                "i16"
            },
            new object[]
            {
                "s32",
                "i32"
            },
            new object[]
            {
                "s64",
                "i64"
            },
            new object[]
            {
                "u8",
                "i8"
            },
            new object[]
            {
                "u16",
                "i16"
            },
            new object[]
            {
                "u32",
                "i32"
            },
            new object[]
            {
                "u64",
                "i64"
            }
        };

        public static readonly object[][] FloatTypes = new object[][]
        {
            new object[]
            {
                "f32",
                "float"
            },
            new object[]
            {
                "f64",
                "double"
            }
        };

        public static readonly string[] ValidIdentifiers = new string[]
        {
            "variable",
            "my_variable",
            "snake_case_name",
            "with_numbers123",
            "ending_with_bang!",
            "_underscore_start"
        };

        public static readonly string[] ValidTypeIdentifiers = new string[]
        {
            "Type",
            "MyClass",
            "PascalCase",
            "HTTPResponse",
            "XMLParser"
        };

        public static readonly string[] InvalidIdentifiers = new string[]
        {
            "123starts_with_number",
            "kebab-case",
            "space name",
            "special@char",
            ""
        };
    }

    /// <summary>
    /// Sample RazorForge programs for testing
    /// </summary>
    public static class SamplePrograms
    {
        public static readonly string HelloWorld = @"
recipe main() -> s32 {
    print(""Hello, World!"")
    return 0
}";

        public static readonly string Factorial = @"
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

        public static readonly string FibonacciIterative = @"
recipe fibonacci(n: s32) -> s32 {
    if n <= 1:
        return n

    var a = 0
    var b = 1
    var result = 0

    for i in 2 to n + 1:
        result = a + b
        a = b
        b = result

    return result
}

recipe main() -> s32 {
    for i in 0 to 10:
        let fib = fibonacci(i)
        print(f""fib({i}) = {fib}"")
    return 0
}";

        public static readonly string ArrayOperations = @"
recipe array_sum(arr: HeapSlice, size: s32) -> s32 {
    var sum = 0
    for i in 0 to size:
        sum = sum + arr[i]
    return sum
}

recipe main() -> s32 {
    let numbers = HeapSlice(10)

    for i in 0 to 10:
        numbers[i] = i * i

    let total = array_sum(numbers, 10)
    print(f""Sum of squares: {total}"")
    return 0
}";

        public static readonly string ComplexMath = @"
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

        public static readonly string ErrorHandling = @"
recipe safe_divide(a: s32, b: s32) -> Option(s32) {
    if b == 0:
        return None
    else:
        return Some(a / b)
}

recipe main() -> s32 {
    let result = safe_divide(10, 2)
    when result is Some(value):
        print(f""Result: {value}"")
    when result is None:
        print(""Division by zero prevented"")
    return 0
}";
    }
}
