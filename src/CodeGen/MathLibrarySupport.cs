using System.Text;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Static utility class for generating LLVM IR code for native mathematical library operations.
/// Provides support for IEEE 754 decimal arithmetic, arbitrary precision integers, and high-precision decimals
/// through integration with specialized mathematical libraries.
/// </summary>
/// <remarks>
/// This class integrates with three key mathematical libraries:
/// <list type="bullet">
/// <item><strong>libdfp</strong>: IEEE 754 decimal floating point operations (d32, d64, d128)</item>
/// <item><strong>libbf</strong>: Arbitrary precision integer arithmetic (BigInteger support)</item>
/// <item><strong>mafm</strong>: Multiple precision arithmetic for high-precision decimal calculations</item>
/// </list>
///
/// The generated LLVM IR provides a bridge between RazorForge's high-level numeric types
/// and optimized native mathematical implementations, ensuring both precision and performance.
/// All generated code follows LLVM IR conventions and includes proper memory management.
/// </remarks>
public static class MathLibrarySupport
{
    /// <summary>
    /// Generates comprehensive LLVM IR function declarations for all supported math libraries.
    /// Creates the necessary external function signatures that can be linked with native implementations.
    /// </summary>
    /// <returns>
    /// Complete LLVM IR declaration block containing function signatures for:
    /// libdfp (decimal floating point), libbf (arbitrary precision), and mafm (multiple precision) operations
    /// </returns>
    /// <remarks>
    /// The generated declarations include:
    /// <list type="bullet">
    /// <item>Arithmetic operations: add, subtract, multiply, divide</item>
    /// <item>Comparison operations: equality and relational comparisons</item>
    /// <item>Conversion functions: string to/from numeric representations</item>
    /// <item>Memory management: allocation and deallocation functions</item>
    /// </list>
    /// </remarks>
    public static string GenerateDeclarations()
    {
        var sb = new StringBuilder();

        sb.AppendLine(value: "; Native math library function declarations");
        sb.AppendLine();

        // libdfp - IEEE 754 decimal floating point
        sb.AppendLine(value: "; libdfp - IEEE 754 decimal floating point operations");
        GenerateLibDfpDeclarations(sb: sb);
        sb.AppendLine();

        // libbf - Arbitrary precision arithmetic
        sb.AppendLine(value: "; libbf - Arbitrary precision arithmetic operations");
        GenerateLibBfDeclarations(sb: sb);
        sb.AppendLine();

        // mafm - Multiple precision arithmetic
        sb.AppendLine(value: "; mafm - Multiple precision arithmetic operations");
        GenerateMafmDeclarations(sb: sb);
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Generates LLVM IR declarations for libdfp (IEEE 754 decimal floating point) functions.
    /// Covers all precision levels: 32-bit, 64-bit, and 128-bit decimal operations.
    /// </summary>
    /// <param name="sb">StringBuilder to append the declarations to</param>
    /// <remarks>
    /// libdfp provides IEEE 754-2008 compliant decimal floating point arithmetic:
    /// <list type="bullet">
    /// <item>d32: 32-bit decimal (7 decimal digits precision)</item>
    /// <item>d64: 64-bit decimal (16 decimal digits precision)</item>
    /// <item>d128: 128-bit decimal (34 decimal digits precision)</item>
    /// </list>
    /// All operations preserve decimal precision without binary floating point conversion errors.
    /// </remarks>
    private static void GenerateLibDfpDeclarations(StringBuilder sb)
    {
        // Decimal32 operations - 32-bit IEEE 754 decimal floating point
        sb.AppendLine(value: "declare i32 @d32_add(i32, i32)"); // Addition
        sb.AppendLine(value: "declare i32 @d32_sub(i32, i32)"); // Subtraction
        sb.AppendLine(value: "declare i32 @d32_mul(i32, i32)"); // Multiplication
        sb.AppendLine(value: "declare i32 @d32_div(i32, i32)"); // Division
        sb.AppendLine(value: "declare i32 @d32_cmp(i32, i32)"); // Comparison (-1, 0, 1)
        sb.AppendLine(value: "declare i32 @d32_from_string(i8*)"); // Parse from string
        sb.AppendLine(value: "declare i8* @d32_to_string(i32)"); // Convert to string

        // Decimal64 operations - 64-bit IEEE 754 decimal floating point
        sb.AppendLine(value: "declare i64 @d64_add(i64, i64)");
        sb.AppendLine(value: "declare i64 @d64_sub(i64, i64)");
        sb.AppendLine(value: "declare i64 @d64_mul(i64, i64)");
        sb.AppendLine(value: "declare i64 @d64_div(i64, i64)");
        sb.AppendLine(value: "declare i32 @d64_cmp(i64, i64)");
        sb.AppendLine(value: "declare i64 @d64_from_string(i8*)");
        sb.AppendLine(value: "declare i8* @d64_to_string(i64)");

        // Decimal128 operations - 128-bit IEEE 754 decimal floating point
        // Uses {i64, i64} struct to represent 128-bit values in LLVM IR
        sb.AppendLine(value: "declare {i64, i64} @d128_add({i64, i64}, {i64, i64})");
        sb.AppendLine(value: "declare {i64, i64} @d128_sub({i64, i64}, {i64, i64})");
        sb.AppendLine(value: "declare {i64, i64} @d128_mul({i64, i64}, {i64, i64})");
        sb.AppendLine(value: "declare {i64, i64} @d128_div({i64, i64}, {i64, i64})");
        sb.AppendLine(value: "declare i32 @d128_cmp({i64, i64}, {i64, i64})");
        sb.AppendLine(value: "declare {i64, i64} @d128_from_string(i8*)");
        sb.AppendLine(value: "declare i8* @d128_to_string({i64, i64})");
    }

    /// <summary>
    /// Generates LLVM IR declarations for libbf (arbitrary precision) mathematical functions.
    /// Supports unlimited precision integer and floating point arithmetic operations.
    /// </summary>
    /// <param name="sb">StringBuilder to append the declarations to</param>
    /// <remarks>
    /// libbf provides arbitrary precision arithmetic capabilities:
    /// <list type="bullet">
    /// <item>Unlimited precision integers (BigInteger equivalent)</item>
    /// <item>Context-based memory management for performance</item>
    /// <item>Configurable precision and rounding modes</item>
    /// <item>Comprehensive arithmetic operations with overflow handling</item>
    /// </list>
    /// Memory management is explicit to provide fine-grained control over allocation.
    /// </remarks>
    private static void GenerateLibBfDeclarations(StringBuilder sb)
    {
        // Context management - provides memory pools and configuration
        sb.AppendLine(
            value:
            "declare void @bf_context_init(i8*, i8*, i8*)"); // Initialize computation context
        sb.AppendLine(value: "declare void @bf_context_end(i8*)"); // Clean up context resources

        // Number lifecycle management
        sb.AppendLine(value: "declare void @bf_init(i8*, i8*)"); // Initialize bf_number structure
        sb.AppendLine(value: "declare void @bf_delete(i8*)"); // Free bf_number resources

        // Value assignment operations
        sb.AppendLine(value: "declare i32 @bf_set_si(i8*, i64)"); // Set from signed integer
        sb.AppendLine(value: "declare i32 @bf_set_ui(i8*, i64)"); // Set from unsigned integer

        // Core arithmetic operations (result, operand1, operand2, precision, rounding_mode)
        sb.AppendLine(value: "declare i32 @bf_add(i8*, i8*, i8*, i64, i32)"); // Addition
        sb.AppendLine(value: "declare i32 @bf_sub(i8*, i8*, i8*, i64, i32)"); // Subtraction
        sb.AppendLine(value: "declare i32 @bf_mul(i8*, i8*, i8*, i64, i32)"); // Multiplication
        sb.AppendLine(value: "declare i32 @bf_div(i8*, i8*, i8*, i64, i32)"); // Division

        // Comparison and string conversion
        sb.AppendLine(value: "declare i32 @bf_cmp(i8*, i8*)"); // Compare two numbers
        sb.AppendLine(value: "declare i8* @bf_ftoa(i8*, i8*, i32, i64, i32)"); // Format to string

        // Memory allocation helpers for bf_number structures
        sb.AppendLine(value: "declare i8* @bf_alloc_number()"); // Allocate bf_number structure
        sb.AppendLine(value: "declare void @bf_free_number(i8*)"); // Free bf_number structure
    }

    private static void GenerateMafmDeclarations(StringBuilder sb)
    {
        // Context management
        sb.AppendLine(value: "declare void @mafm_context_init(i8*, i32)");
        sb.AppendLine(value: "declare void @mafm_context_free(i8*)");

        // Number management
        sb.AppendLine(value: "declare void @mafm_init(i8*)");
        sb.AppendLine(value: "declare void @mafm_clear(i8*)");

        // String operations
        sb.AppendLine(value: "declare i32 @mafm_set_str(i8*, i8*, i32)");
        sb.AppendLine(value: "declare i8* @mafm_get_str(i8*, i32)");

        // Arithmetic operations
        sb.AppendLine(value: "declare i32 @mafm_add(i8*, i8*, i8*, i8*)");
        sb.AppendLine(value: "declare i32 @mafm_sub(i8*, i8*, i8*, i8*)");
        sb.AppendLine(value: "declare i32 @mafm_mul(i8*, i8*, i8*, i8*)");
        sb.AppendLine(value: "declare i32 @mafm_div(i8*, i8*, i8*, i8*)");
        sb.AppendLine(value: "declare i32 @mafm_cmp(i8*, i8*)");

        // Conversion operations
        sb.AppendLine(value: "declare i32 @mafm_set_si(i8*, i64)");
        sb.AppendLine(value: "declare i32 @mafm_set_d(i8*, double)");
        sb.AppendLine(value: "declare i64 @mafm_get_si(i8*)");
        sb.AppendLine(value: "declare double @mafm_get_d(i8*)");

        // Memory allocation for mafm_number structures
        sb.AppendLine(value: "declare i8* @mafm_alloc_number()");
        sb.AppendLine(value: "declare void @mafm_free_number(i8*)");
        sb.AppendLine(value: "declare i8* @mafm_alloc_context()");
        sb.AppendLine(value: "declare void @mafm_free_context(i8*)");
    }

    /// <summary>
    /// Generates LLVM IR code for performing binary arithmetic operations on 32-bit decimal values.
    /// Creates function calls to the appropriate libdfp library functions.
    /// </summary>
    /// <param name="operation">The arithmetic operation symbol (+, -, *, /)</param>
    /// <param name="leftOperand">LLVM IR value representing the left operand</param>
    /// <param name="rightOperand">LLVM IR value representing the right operand</param>
    /// <param name="resultTemp">LLVM IR temporary variable name to store the result</param>
    /// <returns>LLVM IR instruction string for the decimal32 operation</returns>
    /// <exception cref="ArgumentException">Thrown when an unsupported operation is specified</exception>
    /// <remarks>
    /// Supported operations map to libdfp functions:
    /// <list type="bullet">
    /// <item>"+" → d32_add: IEEE 754 decimal addition</item>
    /// <item>"-" → d32_sub: IEEE 754 decimal subtraction</item>
    /// <item>"*" → d32_mul: IEEE 754 decimal multiplication</item>
    /// <item>"/" → d32_div: IEEE 754 decimal division</item>
    /// </list>
    /// </remarks>
    public static string GenerateD32BinaryOp(string operation, string leftOperand,
        string rightOperand, string resultTemp)
    {
        string funcName = operation switch
        {
            "+" => "d32_add",
            "-" => "d32_sub",
            "*" => "d32_mul",
            "/" => "d32_div",
            _ => throw new ArgumentException(message: $"Unsupported d32 operation: {operation}")
        };

        return $"  {resultTemp} = call i32 @{funcName}(i32 {leftOperand}, i32 {rightOperand})";
    }

    /// <summary>
    /// Generates LLVM IR code for performing binary arithmetic operations on 64-bit decimal values.
    /// Creates function calls to the appropriate libdfp library functions with higher precision.
    /// </summary>
    /// <param name="operation">The arithmetic operation symbol (+, -, *, /)</param>
    /// <param name="leftOperand">LLVM IR value representing the left operand</param>
    /// <param name="rightOperand">LLVM IR value representing the right operand</param>
    /// <param name="resultTemp">LLVM IR temporary variable name to store the result</param>
    /// <returns>LLVM IR instruction string for the decimal64 operation</returns>
    /// <exception cref="ArgumentException">Thrown when an unsupported operation is specified</exception>
    public static string GenerateD64BinaryOp(string operation, string leftOperand,
        string rightOperand, string resultTemp)
    {
        string funcName = operation switch
        {
            "+" => "d64_add",
            "-" => "d64_sub",
            "*" => "d64_mul",
            "/" => "d64_div",
            _ => throw new ArgumentException(message: $"Unsupported d64 operation: {operation}")
        };

        return $"  {resultTemp} = call i64 @{funcName}(i64 {leftOperand}, i64 {rightOperand})";
    }

    /// <summary>
    /// Generates LLVM IR code for performing binary arithmetic operations on 128-bit decimal values.
    /// Uses struct representation {i64, i64} to handle the 128-bit values in LLVM IR.
    /// </summary>
    /// <param name="operation">The arithmetic operation symbol (+, -, *, /)</param>
    /// <param name="leftOperand">LLVM IR struct value representing the left operand</param>
    /// <param name="rightOperand">LLVM IR struct value representing the right operand</param>
    /// <param name="resultTemp">LLVM IR temporary variable name to store the result</param>
    /// <returns>LLVM IR instruction string for the decimal128 operation</returns>
    /// <exception cref="ArgumentException">Thrown when an unsupported operation is specified</exception>
    /// <remarks>
    /// Decimal128 operations provide maximum precision (34 decimal digits) and are represented
    /// as LLVM structs containing two 64-bit integers to form the 128-bit representation.
    /// </remarks>
    public static string GenerateD128BinaryOp(string operation, string leftOperand,
        string rightOperand, string resultTemp)
    {
        string funcName = operation switch
        {
            "+" => "d128_add",
            "-" => "d128_sub",
            "*" => "d128_mul",
            "/" => "d128_div",
            _ => throw new ArgumentException(message: $"Unsupported d128 operation: {operation}")
        };

        return
            $"  {resultTemp} = call {{i64, i64}} @{funcName}({{i64, i64}} {leftOperand}, {{i64, i64}} {rightOperand})";
    }

    /// <summary>
    /// Generates LLVM IR code for arbitrary precision integer arithmetic using the libbf library.
    /// Handles memory allocation, operation execution, and result management for BigInteger operations.
    /// </summary>
    /// <param name="operation">The arithmetic operation symbol (+, -, *, /)</param>
    /// <param name="leftOperand">LLVM IR pointer to the left bf_number operand</param>
    /// <param name="rightOperand">LLVM IR pointer to the right bf_number operand</param>
    /// <param name="resultTemp">LLVM IR temporary variable name prefix for the result</param>
    /// <param name="tempCounter">Counter for generating unique temporary variable names</param>
    /// <returns>Multi-line LLVM IR instruction block for the BigInteger operation</returns>
    /// <exception cref="ArgumentException">Thrown when an unsupported operation is specified</exception>
    /// <remarks>
    /// This method generates complete LLVM IR including:
    /// <list type="bullet">
    /// <item>Memory allocation for result bf_number structure</item>
    /// <item>Initialization of the result structure</item>
    /// <item>Function call to the appropriate libbf operation</item>
    /// <item>Loading of the final result value</item>
    /// </list>
    /// The generated code assumes proper context management is handled elsewhere.
    /// </remarks>
    public static string GenerateBigIntBinaryOp(string operation, string leftOperand,
        string rightOperand, string resultTemp, string tempCounter)
    {
        var sb = new StringBuilder();

        // Allocate result bf_number
        sb.AppendLine(handler: $"  {resultTemp}_ptr = call i8* @bf_alloc_number()");
        sb.AppendLine(handler: $"  call void @bf_init(i8* null, i8* {resultTemp}_ptr)");

        string funcName = operation switch
        {
            "+" => "bf_add",
            "-" => "bf_sub",
            "*" => "bf_mul",
            "/" => "bf_div",
            _ => throw new ArgumentException(message: $"Unsupported BigInt operation: {operation}")
        };

        // Perform the operation
        sb.AppendLine(
            handler:
            $"  call i32 @{funcName}(i8* {resultTemp}_ptr, i8* {leftOperand}, i8* {rightOperand}, i64 0, i32 0)");
        sb.AppendLine(handler: $"  {resultTemp} = load i8*, i8** {resultTemp}_ptr");

        return sb.ToString()
                 .TrimEnd();
    }

    /// <summary>
    /// Generates LLVM IR code for high-precision decimal arithmetic using the mafm library.
    /// Provides configurable precision decimal calculations with proper context management.
    /// </summary>
    /// <param name="operation">The arithmetic operation symbol (+, -, *, /)</param>
    /// <param name="leftOperand">LLVM IR pointer to the left mafm_number operand</param>
    /// <param name="rightOperand">LLVM IR pointer to the right mafm_number operand</param>
    /// <param name="resultTemp">LLVM IR temporary variable name prefix for the result</param>
    /// <param name="contextPtr">LLVM IR pointer to the mafm computation context</param>
    /// <returns>Multi-line LLVM IR instruction block for the high-precision decimal operation</returns>
    /// <exception cref="ArgumentException">Thrown when an unsupported operation is specified</exception>
    /// <remarks>
    /// mafm operations support user-configurable precision and are ideal for financial calculations
    /// or scientific computing requiring exact decimal arithmetic beyond IEEE 754 limits.
    /// </remarks>
    public static string GenerateHighPrecisionDecimalBinaryOp(string operation, string leftOperand,
        string rightOperand, string resultTemp, string contextPtr)
    {
        var sb = new StringBuilder();

        // Allocate result mafm_number
        sb.AppendLine(handler: $"  {resultTemp}_ptr = call i8* @mafm_alloc_number()");
        sb.AppendLine(handler: $"  call void @mafm_init(i8* {resultTemp}_ptr)");

        string funcName = operation switch
        {
            "+" => "mafm_add",
            "-" => "mafm_sub",
            "*" => "mafm_mul",
            "/" => "mafm_div",
            _ => throw new ArgumentException(
                message: $"Unsupported HighPrecisionDecimal operation: {operation}")
        };

        // Perform the operation
        sb.AppendLine(
            handler:
            $"  call i32 @{funcName}(i8* {resultTemp}_ptr, i8* {leftOperand}, i8* {rightOperand}, i8* {contextPtr})");
        sb.AppendLine(handler: $"  {resultTemp} = load i8*, i8** {resultTemp}_ptr");

        return sb.ToString()
                 .TrimEnd();
    }

    /// <summary>
    /// Maps RazorForge mathematical type names to their corresponding LLVM IR type representations.
    /// Provides the type mapping interface between high-level language types and LLVM IR.
    /// </summary>
    /// <param name="rfType">RazorForge type name (d32, d64, d128, bigint, decimal)</param>
    /// <returns>Corresponding LLVM IR type string</returns>
    /// <exception cref="ArgumentException">Thrown when an unknown math type is specified</exception>
    /// <remarks>
    /// Type mappings:
    /// <list type="bullet">
    /// <item>d32 → i32: 32-bit IEEE 754 decimal</item>
    /// <item>d64 → i64: 64-bit IEEE 754 decimal</item>
    /// <item>d128 → {i64, i64}: 128-bit IEEE 754 decimal as struct</item>
    /// <item>bigint → i8*: Pointer to bf_number structure</item>
    /// <item>decimal → i8*: Pointer to mafm_number structure</item>
    /// </list>
    /// </remarks>
    public static string MapMathTypeToLLVM(string rfType)
    {
        return rfType switch
        {
            "d32" => "i32",
            "d64" => "i64",
            "d128" => "{i64, i64}",
            "bigint" => "i8*", // Pointer to bf_number structure
            "decimal" => "i8*", // Pointer to mafm_number structure
            _ => throw new ArgumentException(message: $"Unknown math type: {rfType}")
        };
    }

    /// <summary>
    /// Determines whether a given type requires native mathematical library support.
    /// Used to route operations through appropriate mathematical library implementations.
    /// </summary>
    /// <param name="type">Type name to check</param>
    /// <returns>true if the type requires math library support; false for standard LLVM operations</returns>
    /// <remarks>
    /// Math library types include all precision-critical and arbitrary-precision types
    /// that cannot be efficiently implemented using standard LLVM IR operations alone.
    /// </remarks>
    public static bool IsMathLibraryType(string type)
    {
        return type is "d32" or "d64" or "d128" or "bigint" or "decimal";
    }
}
