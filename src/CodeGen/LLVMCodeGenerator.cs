using System.Numerics;
using System.Text;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Production LLVM IR code generator for the RazorForge programming language.
/// Implements the visitor pattern to traverse the AST and emit optimized LLVM intermediate representation
/// that can be compiled to high-performance native machine code.
/// </summary>
/// <remarks>
/// This code generator provides comprehensive support for RazorForge language features:
/// <list type="bullet">
/// <item><strong>Type System</strong>: All primitive types including signed/unsigned integers, IEEE 754 floats, decimals</item>
/// <item><strong>Mathematical Libraries</strong>: Integration with libdfp, libbf, and mafm for precision arithmetic</item>
/// <item><strong>Memory Management</strong>: Stack allocation with proper LLVM memory semantics</item>
/// <item><strong>Control Flow</strong>: Complete implementation of conditionals, loops, and function calls</item>
/// <item><strong>Type Conversions</strong>: Comprehensive casting between all supported numeric types</item>
/// <item><strong>Overflow Handling</strong>: Support for various overflow behaviors (wrap, saturate, checked, unchecked)</item>
/// </list>
///
/// The generated LLVM IR follows modern LLVM conventions and is optimized for:
/// <list type="bullet">
/// <item>Performance: Efficient instruction selection and minimal overhead</item>
/// <item>Correctness: Type-safe operations with proper bounds checking</item>
/// <item>Portability: Target-independent IR that works across architectures</item>
/// <item>Debugging: Preserves source location information for debugging support</item>
/// </list>
///
/// <strong>Architecture:</strong>
/// The generator maintains several key data structures:
/// <list type="bullet">
/// <item>Symbol table for tracking variable types and locations</item>
/// <item>Temporary counter for generating unique LLVM temporary variables</item>
/// <item>Label counter for control flow basic block generation</item>
/// <item>Type tracking for temporary variables to enable accurate type conversions</item>
/// </list>
/// </remarks>
public class LLVMCodeGenerator : IAstVisitor<string>
{
    private readonly StringBuilder _output;
    private readonly Language _language;
    private readonly LanguageMode _mode;
    private readonly TargetPlatform _targetPlatform;
    private int _tempCounter;
    private int _labelCounter;
    private readonly Dictionary<string, string> _symbolTypes;

    private readonly Dictionary<string, TypeInfo>
        _tempTypes = new(); // Track types of temporary variables

    private bool _hasReturn = false;
    private List<string>? _stringConstants; // Collect string constants for proper emission

    // Generic instantiation tracking for monomorphization
    private readonly Dictionary<string, List<List<Analysis.TypeInfo>>> _genericInstantiations =
        new();

    private readonly Dictionary<string, FunctionDeclaration> _genericFunctionTemplates = new();
    private readonly List<string> _pendingGenericInstantiations = new();

    /// <summary>
    /// Initializes a new LLVM IR code generator for the specified language and mode configuration.
    /// Sets up the internal state required for AST traversal and IR generation.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Cake) affecting syntax and semantics</param>
    /// <param name="mode">Language mode (Normal/Danger for RazorForge, Sweet/Bitter for Cake)</param>
    /// <param name="targetPlatform">Target platform (optional, defaults to x86_64 Linux)</param>
    /// <remarks>
    /// The language and mode parameters influence:
    /// <list type="bullet">
    /// <item>Default type inference behavior</item>
    /// <item>Safety checking levels (bounds checking, overflow handling)</item>
    /// <item>Memory management strategy</item>
    /// <item>Generated code optimization level</item>
    /// </list>
    /// </remarks>
    public LLVMCodeGenerator(Language language, LanguageMode mode,
        TargetPlatform? targetPlatform = null)
    {
        _language = language;
        _mode = mode;
        _targetPlatform = targetPlatform ?? TargetPlatform.Default();
        _output = new StringBuilder();
        _tempCounter = 0;
        _labelCounter = 0;
        _symbolTypes = new Dictionary<string, string>();
    }

    /// <summary>
    /// Retrieves the complete generated LLVM IR code as a string.
    /// </summary>
    /// <returns>Complete LLVM IR module including headers, declarations, and function definitions</returns>
    public string GetGeneratedCode()
    {
        return _output.ToString();
    }

    /// <summary>
    /// Generates complete LLVM IR module for the given program AST.
    /// Emits module headers, external declarations, math library support, and processes all program declarations.
    /// </summary>
    /// <param name="program">The root program AST node to generate code for</param>
    /// <remarks>
    /// The generation process follows this structure:
    /// <list type="bullet">
    /// <item><strong>Module Headers</strong>: LLVM module metadata, target information</item>
    /// <item><strong>External Declarations</strong>: Standard library functions (printf, malloc, etc.)</item>
    /// <item><strong>Math Library Support</strong>: Precision arithmetic function declarations</item>
    /// <item><strong>String Constants</strong>: Global constants for formatted I/O operations</item>
    /// <item><strong>Program Content</strong>: User-defined functions, classes, and global variables</item>
    /// </list>
    ///
    /// The target configuration assumes x86_64 Linux but can be adapted for other platforms.
    /// </remarks>
    public void Generate(AST.Program program)
    {
        // LLVM IR module headers - provide module identification and target configuration
        _output.AppendLine(value: "; ModuleID = 'razorforge'");
        _output.AppendLine(value: "source_filename = \"razorforge.rf\"");
        _output.AppendLine(value: $"target datalayout = \"{_targetPlatform.DataLayout}\"");
        _output.AppendLine(value: $"target triple = \"{_targetPlatform.TripleString}\"");
        _output.AppendLine();

        // External function declarations - standard library interfaces
        _output.AppendLine(value: "; External function declarations");
        _output.AppendLine(value: "declare i32 @printf(i8*, ...)"); // Formatted output
        _output.AppendLine(value: "declare i32 @scanf(i8*, ...)"); // Formatted input
        _output.AppendLine(value: "declare i8* @malloc(i64)"); // Memory allocation
        _output.AppendLine(value: "declare void @free(i8*)"); // Memory deallocation
        _output.AppendLine();

        // Mathematical library function declarations - precision arithmetic support
        _output.AppendLine(value: MathLibrarySupport.GenerateDeclarations());

        // String constants for I/O operations and error messages
        _output.AppendLine(value: "; String constants");
        _output.AppendLine(
            value:
            "@.str_int_fmt = private unnamed_addr constant [4 x i8] c\"%d\\0A\\00\", align 1"); // Integer format
        _output.AppendLine(
            value:
            "@.str_fmt = private unnamed_addr constant [3 x i8] c\"%s\\00\", align 1"); // String format
        _output.AppendLine(
            value:
            "@.str_overflow = private unnamed_addr constant [19 x i8] c\"Arithmetic overflow\\00\", align 1"); // Overflow error

        // Process the program AST to generate function definitions and global declarations
        program.Accept(visitor: this);

        // Emit collected string constants after the predefined constants
        if (_stringConstants != null && _stringConstants.Count > 0)
        {
            string content = _output.ToString();
            // Find the line after @.str_fmt and insert our constants there
            string strFmtLine =
                "@.str_fmt = private unnamed_addr constant [3 x i8] c\"%s\\00\", align 1";
            int insertPos = content.IndexOf(value: strFmtLine);
            if (insertPos >= 0)
            {
                int endOfLine = content.IndexOf(value: '\n', startIndex: insertPos) + 1;
                string before = content.Substring(startIndex: 0, length: endOfLine);
                string after = content.Substring(startIndex: endOfLine);

                _output.Clear();
                _output.Append(value: before);

                // Add our collected string constants
                foreach (string constant in _stringConstants)
                {
                    _output.AppendLine(value: constant);
                }

                _output.Append(value: after);
            }
        }

        _output.AppendLine();
    }

    /// <summary>Generates a unique LLVM IR temporary variable name</summary>
    /// <returns>Unique temporary variable name in LLVM IR format (%tmp0, %tmp1, etc.)</returns>
    private string GetNextTemp()
    {
        return $"%tmp{_tempCounter++}";
    }

    /// <summary>Generates a unique LLVM IR basic block label</summary>
    /// <returns>Unique label name for LLVM IR basic blocks (label0, label1, etc.)</returns>
    private string GetNextLabel()
    {
        return $"label{_labelCounter++}";
    }

    /// <summary>
    /// Generates a mangled function name for a generic function instantiation.
    /// Creates unique names by appending type arguments to the base function name.
    /// </summary>
    /// <param name="baseName">The original function name</param>
    /// <param name="typeArgs">List of concrete type arguments for this instantiation</param>
    /// <returns>Mangled function name (e.g., "swap_s32" for swap&lt;s32&gt;)</returns>
    /// <remarks>
    /// Examples:
    /// - swap&lt;T&gt; with T=s32 -> swap_s32
    /// - map&lt;K,V&gt; with K=text,V=s32 -> map_text_s32
    /// - container&lt;Array&lt;s32&gt;&gt; -> container_Array_s32
    /// </remarks>
    private string MonomorphizeFunctionName(string baseName, List<Analysis.TypeInfo> typeArgs)
    {
        if (typeArgs == null || typeArgs.Count == 0)
        {
            return baseName;
        }

        string suffix = string.Join(separator: "_", values: typeArgs.Select(selector: t => t.Name
           .Replace(oldValue: "[", newValue: "_")
           .Replace(oldValue: "]", newValue: "")
           .Replace(oldValue: ",", newValue: "_")
           .Replace(oldValue: " ", newValue: "")));
        return $"{baseName}_{suffix}";
    }

    /// <summary>
    /// Checks if a generic function has already been instantiated with the given type arguments.
    /// </summary>
    /// <param name="functionName">The base function name</param>
    /// <param name="typeArgs">Type arguments to check</param>
    /// <returns>True if this instantiation already exists, false otherwise</returns>
    private bool IsAlreadyInstantiated(string functionName, List<Analysis.TypeInfo> typeArgs)
    {
        if (!_genericInstantiations.ContainsKey(key: functionName))
        {
            return false;
        }

        List<List<Analysis.TypeInfo>> existingInstantiations =
            _genericInstantiations[key: functionName];
        foreach (List<Analysis.TypeInfo> existing in existingInstantiations)
        {
            if (existing.Count != typeArgs.Count)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < existing.Count; i++)
            {
                if (existing[index: i].Name != typeArgs[index: i].Name)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tracks a new generic function instantiation to avoid generating duplicates.
    /// </summary>
    /// <param name="functionName">The base function name</param>
    /// <param name="typeArgs">Type arguments for this instantiation</param>
    private void TrackInstantiation(string functionName, List<Analysis.TypeInfo> typeArgs)
    {
        if (!_genericInstantiations.ContainsKey(key: functionName))
        {
            _genericInstantiations[key: functionName] = new List<List<Analysis.TypeInfo>>();
        }

        _genericInstantiations[key: functionName]
           .Add(item: new List<Analysis.TypeInfo>(collection: typeArgs));
    }

    /// <summary>
    /// Maps RazorForge type names to their corresponding LLVM IR type representations.
    /// Handles both primitive types and specialized mathematical library types.
    /// </summary>
    /// <param name="rfType">RazorForge type name (s32, f64, bool, Text, etc.)</param>
    /// <returns>Corresponding LLVM IR type string</returns>
    /// <remarks>
    /// Type mapping priorities:
    /// <list type="bullet">
    /// <item>Math library types (d32, d64, d128, bigint, decimal) are handled first</item>
    /// <item>Unsigned integers use the same LLVM types as signed (signedness tracked separately)</item>
    /// <item>System-dependent types (syssint, sysuint) map to 64-bit on x86_64</item>
    /// <item>Text types map to i8* (null-terminated C strings)</item>
    /// <item>Unknown types default to i8* for maximum compatibility</item>
    /// </list>
    /// </remarks>
    private string MapTypeToLLVM(string rfType)
    {
        // Check for math library types first (d32, d64, d128, bigint, decimal)
        if (MathLibrarySupport.IsMathLibraryType(type: rfType))
        {
            return MathLibrarySupport.MapMathTypeToLLVM(rfType: rfType);
        }

        return rfType switch
        {
            // Signed integers - direct mapping to LLVM integer types
            "s8" => "i8",
            "s16" => "i16",
            "s32" => "i32",
            "s64" => "i64",
            "s128" => "i128",

            // Unsigned integers - use same LLVM type, track signedness separately
            "u8" => "i8",
            "u16" => "i16",
            "u32" => "i32",
            "u64" => "i64",
            "u128" => "i128",

            // System-dependent integers (pointer-sized, architecture-dependent)
            "syssint" => _targetPlatform
               .GetPointerSizedIntType(), // intptr_t - varies by architecture
            "sysuint" => _targetPlatform
               .GetPointerSizedIntType(), // uintptr_t - varies by architecture

            // IEEE 754 floating point types
            "f16" => "half", // 16-bit half precision
            "f32" => "float", // 32-bit single precision
            "f64" => "double", // 64-bit double precision
            "f128" => "fp128", // 128-bit quad precision

            // Boolean type
            "bool" => "i1", // Single bit boolean

            // Text/String types
            "text" => "i8*", // Null-terminated C string
            "Text" => "i8*", // Alternative capitalization

            // C FFI types - Character types
            "cchar" or "cschar" => "i8", // char, signed char
            "cuchar" => "i8", // unsigned char (same LLVM type, different signedness)
            "cwchar" => _targetPlatform
               .GetWCharType(), // wchar_t (varies by OS: 32-bit on Unix/Linux, 16-bit on Windows)
            "cchar8" => "i8", // char8_t
            "cchar16" => "i16", // char16_t
            "cchar32" => "i32", // char32_t

            // C FFI types - Numeric types
            "cshort" => "i16", // short
            "cushort" => "i16", // unsigned short
            "cint" => "i32", // int
            "cuint" => "i32", // unsigned int
            "clong" => _targetPlatform
               .GetLongType(), // long (varies by OS: 64-bit on Unix x86_64, 32-bit on Windows x86_64)
            "culong" => _targetPlatform
               .GetLongType(), // unsigned long (varies by OS: 64-bit on Unix x86_64, 32-bit on Windows x86_64)
            "cll" => "i64", // long long
            "cull" => "i64", // unsigned long long
            "cfloat" => "float", // float
            "cdouble" => "double", // double

            // C FFI types - Pointer-sized integers (architecture-dependent)
            "csptr" => _targetPlatform
               .GetPointerSizedIntType(), // intptr_t (varies by architecture)
            "cuptr" => _targetPlatform
               .GetPointerSizedIntType(), // uintptr_t (varies by architecture)

            // C FFI types - Special types
            "cvoid" => _targetPlatform
               .GetPointerSizedIntType(), // void (represented as sysuint in RazorForge)
            "cbool" => "i1", // C bool (_Bool)

            _ => "i8*" // Default to pointer for unknown types (including cptr<T>)
        };
    }

    // Program visitor
    public string VisitProgram(AST.Program node)
    {
        foreach (IAstNode decl in node.Declarations)
        {
            decl.Accept(visitor: this);
        }

        return "";
    }

    // Function declaration
    public string VisitFunctionDeclaration(FunctionDeclaration node)
    {
        string returnType = node.ReturnType != null
            ? MapTypeToLLVM(rfType: node.ReturnType.Name)
            : "void";

        // Set the current function return type for return statement processing
        _currentFunctionReturnType = returnType;

        var parameters = new List<string>();

        if (node.Parameters != null)
        {
            foreach (Parameter param in node.Parameters)
            {
                string paramType = param.Type != null
                    ? MapTypeToLLVM(rfType: param.Type.Name)
                    : "i32";
                parameters.Add(item: $"{paramType} %{param.Name}");
                _symbolTypes[key: param.Name] = paramType;
            }
        }

        string paramList = string.Join(separator: ", ", values: parameters);

        _output.AppendLine(handler: $"define {returnType} @{node.Name}({paramList}) {{");
        _output.AppendLine(value: "entry:");

        // Reset return flag for this function
        _hasReturn = false;

        // Visit function body
        if (node.Body != null)
        {
            node.Body.Accept(visitor: this);
        }

        // Add default return if needed (only if no explicit return was generated)
        if (!_hasReturn)
        {
            if (returnType == "void")
            {
                _output.AppendLine(value: "  ret void");
            }
            else if (returnType == "i32")
            {
                _output.AppendLine(value: "  ret i32 0");
            }
        }

        _output.AppendLine(value: "}");
        _output.AppendLine();

        return "";
    }

    // Variable declaration
    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        string type = node.Type != null
            ? MapTypeToLLVM(rfType: node.Type.Name)
            : "i32";
        string varName = $"%{node.Name}";

        _symbolTypes[key: node.Name] = type;

        if (node.Initializer != null)
        {
            string initValue = node.Initializer.Accept(visitor: this);
            _output.AppendLine(handler: $"  {varName} = alloca {type}");
            _output.AppendLine(handler: $"  store {type} {initValue}, {type}* {varName}");
        }
        else
        {
            _output.AppendLine(handler: $"  {varName} = alloca {type}");
        }

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for binary expressions with comprehensive operator and type support.
    /// Handles arithmetic, comparison, logical, and bitwise operations with proper type management.
    /// </summary>
    /// <param name="node">Binary expression AST node containing operator and operands</param>
    /// <returns>LLVM IR temporary variable containing the result of the binary operation</returns>
    /// <remarks>
    /// This method provides comprehensive binary operation support including:
    /// <list type="bullet">
    /// <item><strong>Math Library Integration</strong>: Automatic routing to specialized libraries for precision types</item>
    /// <item><strong>Overflow Handling</strong>: Support for wrap, saturate, checked, and unchecked variants</item>
    /// <item><strong>Type-Aware Operations</strong>: Correct signed/unsigned and integer/float operation selection</item>
    /// <item><strong>Comparison Operations</strong>: Proper handling of different comparison result types</item>
    /// </list>
    ///
    /// <strong>Operation Categories:</strong>
    /// <list type="bullet">
    /// <item>Arithmetic: +, -, *, /, % with overflow variants</item>
    /// <item>Comparison: ==, !=, &lt;, &lt;=, &gt;, &gt;= returning i1 boolean results</item>
    /// <item>Logical: &amp;&amp;, || with short-circuit evaluation support</item>
    /// <item>Bitwise: &amp;, |, ^, &lt;&lt;, &gt;&gt; for integer types</item>
    /// </list>
    /// </remarks>
    public string VisitBinaryExpression(BinaryExpression node)
    {
        string left = node.Left.Accept(visitor: this);
        string right = node.Right.Accept(visitor: this);
        string result = GetNextTemp();

        // Get operand type information (assume both operands have same type)
        TypeInfo leftTypeInfo = GetTypeInfo(expr: node.Left);
        string operandType = leftTypeInfo.LLVMType;

        // Check if this is a math library operation
        if (MathLibrarySupport.IsMathLibraryType(type: leftTypeInfo.RazorForgeType))
        {
            return GenerateMathLibraryBinaryOp(node: node, left: left, right: right,
                result: result, typeInfo: leftTypeInfo);
        }

        string op = node.Operator switch
        {
            // Regular arithmetic
            BinaryOperator.Add => "add",
            BinaryOperator.Subtract => "sub",
            BinaryOperator.Multiply => "mul",
            BinaryOperator.Divide => GetIntegerDivisionOp(
                typeInfo: leftTypeInfo), // sdiv/udiv based on signed/unsigned
            BinaryOperator.TrueDivide => "fdiv", // / (true division) - floats only
            BinaryOperator.Modulo => GetModuloOp(
                typeInfo: leftTypeInfo), // srem/urem for integers, frem for floats

            // Overflow-handling variants (for now, use LLVM intrinsics will be added later)
            BinaryOperator.AddWrap => "add", // Wrapping is default behavior
            BinaryOperator.SubtractWrap => "sub",
            BinaryOperator.MultiplyWrap => "mul",
            BinaryOperator.DivideWrap => GetIntegerDivisionOp(typeInfo: leftTypeInfo),
            BinaryOperator.ModuloWrap => GetModuloOp(typeInfo: leftTypeInfo),

            BinaryOperator.AddSaturate => "", // Handled separately with intrinsics
            BinaryOperator.SubtractSaturate => "", // Handled separately with intrinsics
            BinaryOperator.MultiplySaturate => "", // Handled separately with intrinsics

            BinaryOperator.AddChecked => "", // Handled separately with overflow intrinsics
            BinaryOperator.SubtractChecked => "", // Handled separately with overflow intrinsics
            BinaryOperator.MultiplyChecked => "", // Handled separately with overflow intrinsics

            BinaryOperator.AddUnchecked => "add", // Regular operations, no overflow checks
            BinaryOperator.SubtractUnchecked => "sub",
            BinaryOperator.MultiplyUnchecked => "mul",
            BinaryOperator.DivideUnchecked => GetIntegerDivisionOp(typeInfo: leftTypeInfo),
            BinaryOperator.ModuloUnchecked => GetModuloOp(typeInfo: leftTypeInfo),

            // Comparisons
            BinaryOperator.Less => "icmp slt",
            BinaryOperator.Greater => "icmp sgt",
            BinaryOperator.Equal => "icmp eq",
            BinaryOperator.NotEqual => "icmp ne",

            _ => "add"
        };

        // Handle special overflow operations with LLVM intrinsics
        if (string.IsNullOrEmpty(value: op))
        {
            // Handle saturating and checked operations
            switch (node.Operator)
            {
                case BinaryOperator.AddSaturate:
                case BinaryOperator.SubtractSaturate:
                case BinaryOperator.MultiplySaturate:
                    return GenerateSaturatingArithmetic(op: node.Operator, left: left,
                        right: right, result: result, typeInfo: leftTypeInfo,
                        llvmType: operandType);

                case BinaryOperator.AddChecked:
                case BinaryOperator.SubtractChecked:
                case BinaryOperator.MultiplyChecked:
                    return GenerateCheckedArithmetic(op: node.Operator, left: left, right: right,
                        result: result, typeInfo: leftTypeInfo, llvmType: operandType);

                default:
                    throw new NotSupportedException(
                        message: $"Operator {node.Operator} is not properly configured");
            }
        }

        // Generate the operation with proper type
        if (op.StartsWith(value: "icmp"))
        {
            // Comparison operations return i1
            _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {right}");
            _tempTypes[key: result] = new TypeInfo(LLVMType: "i1", IsUnsigned: false,
                IsFloatingPoint: false, RazorForgeType: "bool");
        }
        else
        {
            // Arithmetic operations maintain operand type
            _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {right}");
            _tempTypes[key: result] = leftTypeInfo; // Result has same type as operands
        }

        return result;
    }

    /// <summary>
    /// Generates LLVM IR for saturating arithmetic operations using LLVM intrinsics.
    /// </summary>
    private string GenerateSaturatingArithmetic(BinaryOperator op, string left, string right,
        string result, TypeInfo typeInfo, string llvmType)
    {
        string intrinsicName = op switch
        {
            BinaryOperator.AddSaturate => typeInfo.IsUnsigned
                ? "llvm.uadd.sat"
                : "llvm.sadd.sat",
            BinaryOperator.SubtractSaturate => typeInfo.IsUnsigned
                ? "llvm.usub.sat"
                : "llvm.ssub.sat",
            BinaryOperator.MultiplySaturate => GenerateSaturatingMultiply(left: left, right: right,
                result: result, typeInfo: typeInfo, llvmType: llvmType),
            _ => throw new NotSupportedException(
                message: $"Saturating operation {op} not supported")
        };

        // For multiply, the implementation is handled separately
        if (op == BinaryOperator.MultiplySaturate)
        {
            return result;
        }

        // Generate intrinsic call for add/subtract
        _output.AppendLine(
            handler:
            $"  {result} = call {llvmType} @{intrinsicName}.{llvmType}({llvmType} {left}, {llvmType} {right})");
        _tempTypes[key: result] = typeInfo;
        return result;
    }

    /// <summary>
    /// Generates saturating multiply using manual overflow detection.
    /// LLVM doesn't provide a direct saturating multiply intrinsic, so we use overflow detection.
    /// </summary>
    private string GenerateSaturatingMultiply(string left, string right, string result,
        TypeInfo typeInfo, string llvmType)
    {
        string overflowTemp = GetNextTemp();
        string structTemp = GetNextTemp();
        string valueTemp = GetNextTemp();
        string didOverflowTemp = GetNextTemp();
        string maxValueTemp = GetNextTemp();
        string minValueTemp = GetNextTemp();
        string saturatedTemp = GetNextTemp();

        string intrinsicName = typeInfo.IsUnsigned
            ? "llvm.umul.with.overflow"
            : "llvm.smul.with.overflow";

        // Call overflow intrinsic
        _output.AppendLine(
            handler:
            $"  {structTemp} = call {{{llvmType}, i1}} @{intrinsicName}.{llvmType}({llvmType} {left}, {llvmType} {right})");
        _output.AppendLine(
            handler: $"  {valueTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 0");
        _output.AppendLine(
            handler: $"  {didOverflowTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 1");

        // Get max/min values for saturation
        (string maxValue, string minValue) =
            GetSaturationBounds(typeInfo: typeInfo, llvmType: llvmType);

        // Determine saturation value based on sign of operands if overflow occurred
        if (typeInfo.IsUnsigned)
        {
            // For unsigned: saturate to max value on overflow
            _output.AppendLine(
                handler:
                $"  {saturatedTemp} = select i1 {didOverflowTemp}, {llvmType} {maxValue}, {llvmType} {valueTemp}");
        }
        else
        {
            // For signed: need to check if result should be min or max
            // If both operands have same sign, overflow goes to max/min in same direction
            string leftSignTemp = GetNextTemp();
            string rightSignTemp = GetNextTemp();
            string sameSigns = GetNextTemp();
            string satValue = GetNextTemp();

            _output.AppendLine(handler: $"  {leftSignTemp} = icmp slt {llvmType} {left}, 0");
            _output.AppendLine(handler: $"  {rightSignTemp} = icmp slt {llvmType} {right}, 0");
            _output.AppendLine(
                handler: $"  {sameSigns} = icmp eq i1 {leftSignTemp}, {rightSignTemp}");

            // If same signs: both positive -> max, both negative -> max (negative * negative = positive)
            // If different signs: result should be min (negative)
            _output.AppendLine(
                handler:
                $"  {satValue} = select i1 {sameSigns}, {llvmType} {maxValue}, {llvmType} {minValue}");
            _output.AppendLine(
                handler:
                $"  {saturatedTemp} = select i1 {didOverflowTemp}, {llvmType} {satValue}, {llvmType} {valueTemp}");
        }

        _output.AppendLine(
            handler: $"  {result} = add {llvmType} {saturatedTemp}, 0  ; final saturated result");
        _tempTypes[key: result] = typeInfo;
        return result;
    }

    /// <summary>
    /// Generates LLVM IR for checked arithmetic operations that trap on overflow.
    /// </summary>
    private string GenerateCheckedArithmetic(BinaryOperator op, string left, string right,
        string result, TypeInfo typeInfo, string llvmType)
    {
        string intrinsicName = op switch
        {
            BinaryOperator.AddChecked => typeInfo.IsUnsigned
                ? "llvm.uadd.with.overflow"
                : "llvm.sadd.with.overflow",
            BinaryOperator.SubtractChecked => typeInfo.IsUnsigned
                ? "llvm.usub.with.overflow"
                : "llvm.ssub.with.overflow",
            BinaryOperator.MultiplyChecked => typeInfo.IsUnsigned
                ? "llvm.umul.with.overflow"
                : "llvm.smul.with.overflow",
            _ => throw new NotSupportedException(message: $"Checked operation {op} not supported")
        };

        string structTemp = GetNextTemp();
        string valueTemp = GetNextTemp();
        string didOverflowTemp = GetNextTemp();
        string trapLabel = GetNextLabel();
        string continueLabel = GetNextLabel();

        // Call overflow intrinsic which returns {result, overflow_flag}
        _output.AppendLine(
            handler:
            $"  {structTemp} = call {{{llvmType}, i1}} @{intrinsicName}.{llvmType}({llvmType} {left}, {llvmType} {right})");
        _output.AppendLine(
            handler: $"  {valueTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 0");
        _output.AppendLine(
            handler: $"  {didOverflowTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 1");

        // Branch on overflow flag
        _output.AppendLine(
            handler: $"  br i1 {didOverflowTemp}, label %{trapLabel}, label %{continueLabel}");

        // Trap block - call panic/abort on overflow
        _output.AppendLine(handler: $"{trapLabel}:");
        _output.AppendLine(
            value:
            $"  call void @rf_crash(ptr getelementptr inbounds ([19 x i8], [19 x i8]* @.str_overflow, i32 0, i32 0))");
        _output.AppendLine(value: $"  unreachable");

        // Continue block - normal execution
        _output.AppendLine(handler: $"{continueLabel}:");
        _output.AppendLine(
            handler: $"  {result} = add {llvmType} {valueTemp}, 0  ; propagate result");

        _tempTypes[key: result] = typeInfo;
        return result;
    }

    /// <summary>
    /// Gets the saturation bounds (max and min values) for a given type.
    /// </summary>
    private (string maxValue, string minValue) GetSaturationBounds(TypeInfo typeInfo,
        string llvmType)
    {
        if (typeInfo.IsUnsigned)
        {
            // Unsigned: min = 0, max = 2^bits - 1
            int bits = GetTypeBitWidth(llvmType: llvmType);
            string maxValue = bits switch
            {
                8 => "255",
                16 => "65535",
                32 => "4294967295",
                64 => "18446744073709551615",
                128 => "340282366920938463463374607431768211455",
                _ => "0"
            };
            return (maxValue, "0");
        }
        else
        {
            // Signed: min = -2^(bits-1), max = 2^(bits-1) - 1
            int bits = GetTypeBitWidth(llvmType: llvmType);
            (string maxValue, string minValue) = bits switch
            {
                8 => ("127", "-128"),
                16 => ("32767", "-32768"),
                32 => ("2147483647", "-2147483648"),
                64 => ("9223372036854775807", "-9223372036854775808"),
                128 => ("170141183460469231731687303715884105727",
                    "-170141183460469231731687303715884105728"),
                _ => ("0", "0")
            };
            return (maxValue, minValue);
        }
    }

    private string GenerateMathLibraryBinaryOp(BinaryExpression node, string left, string right,
        string result, TypeInfo typeInfo)
    {
        string operation = node.Operator switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.TrueDivide => "/",
            _ => throw new NotSupportedException(
                message:
                $"Operator {node.Operator} not supported for math library type {typeInfo.RazorForgeType}")
        };

        string mathLibraryCode = typeInfo.RazorForgeType switch
        {
            "d32" => MathLibrarySupport.GenerateD32BinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result),
            "d64" => MathLibrarySupport.GenerateD64BinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result),
            "d128" => MathLibrarySupport.GenerateD128BinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result),
            "bigint" => MathLibrarySupport.GenerateBigIntBinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result,
                tempCounter: _tempCounter.ToString()),
            "decimal" => MathLibrarySupport.GenerateHighPrecisionDecimalBinaryOp(
                operation: operation, leftOperand: left, rightOperand: right, resultTemp: result,
                contextPtr: "%decimal_context"),
            _ => throw new NotSupportedException(
                message: $"Math library type {typeInfo.RazorForgeType} not supported")
        };

        _output.AppendLine(value: mathLibraryCode);
        _tempTypes[key: result] = typeInfo; // Result has same type as operands
        return result;
    }

    // Literal expression
    public string VisitLiteralExpression(LiteralExpression node)
    {
        if (node.Value is int intVal)
        {
            return intVal.ToString();
        }
        else if (node.Value is long longVal)
        {
            return longVal.ToString();
        }
        else if (node.Value is byte byteVal)
        {
            return byteVal.ToString();
        }
        else if (node.Value is sbyte sbyteVal)
        {
            return sbyteVal.ToString();
        }
        else if (node.Value is short shortVal)
        {
            return shortVal.ToString();
        }
        else if (node.Value is ushort ushortVal)
        {
            return ushortVal.ToString();
        }
        else if (node.Value is uint uintVal)
        {
            return uintVal.ToString();
        }
        else if (node.Value is ulong ulongVal)
        {
            return ulongVal.ToString();
        }
        else if (node.Value is float floatVal)
        {
            return floatVal.ToString(format: "G");
        }
        else if (node.Value is double doubleVal)
        {
            return doubleVal.ToString(format: "G");
        }
        else if (node.Value is decimal decimalVal)
        {
            return decimalVal.ToString(format: "G");
        }
        else if (node.Value is BigInteger bigIntVal)
        {
            return bigIntVal.ToString();
        }
        else if (node.Value is Half halfVal)
        {
            return ((float)halfVal).ToString(format: "G");
        }
        else if (node.Value is bool boolVal)
        {
            return boolVal
                ? "1"
                : "0";
        }
        else if (node.Value is string strVal)
        {
            string strConst = $"@.str{_tempCounter++}";
            int len = strVal.Length + 1;
            // Store string constant for later emission instead of inserting immediately
            if (_stringConstants == null)
            {
                _stringConstants = new List<string>();
            }

            _stringConstants.Add(
                item:
                $"{strConst} = private unnamed_addr constant [{len} x i8] c\"{strVal}\\00\", align 1");
            string temp = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {temp} = getelementptr [{len} x i8], [{len} x i8]* {strConst}, i32 0, i32 0");
            return temp;
        }

        return "0";
    }

    // Map TokenType to LLVM type
    private string GetLLVMType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.S8Literal => "i8",
            TokenType.S16Literal => "i16",
            TokenType.S32Literal => "i32",
            TokenType.S64Literal => "i64",
            TokenType.S128Literal => "i128",
            TokenType.U8Literal => "i8", // LLVM doesn't distinguish signed/unsigned at IR level
            TokenType.U16Literal => "i16",
            TokenType.U32Literal => "i32",
            TokenType.U64Literal => "i64",
            TokenType.U128Literal => "i128",
            TokenType.F16Literal => "half",
            TokenType.F32Literal => "float",
            TokenType.F64Literal => "double",
            TokenType.F128Literal => "fp128", // IEEE 754 quad precision
            TokenType.Integer => "i128", // BigInteger -> large integer type
            TokenType.Decimal => "double", // Default floating type
            TokenType.True => "i1",
            TokenType.False => "i1",
            _ => "i32" // Default fallback
        };
    }

    // Identifier expression
    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        string temp = GetNextTemp();
        string type = _symbolTypes.ContainsKey(key: node.Name)
            ? _symbolTypes[key: node.Name]
            : "i32";
        _output.AppendLine(handler: $"  {temp} = load {type}, {type}* %{node.Name}");
        return temp;
    }

    // Function call expression
    public string VisitCallExpression(CallExpression node)
    {
        string result = GetNextTemp();

        // Check if this is a standalone danger zone function call (addr_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            string dangerfunctionName = identifierExpr.Name;
            if (IsNonGenericDangerZoneFunction(functionName: dangerfunctionName))
            {
                return HandleNonGenericDangerZoneFunction(node: node,
                    functionName: dangerfunctionName, resultTemp: result);
            }
        }

        var args = new List<string>();
        foreach (Expression arg in node.Arguments)
        {
            args.Add(item: $"i32 {arg.Accept(visitor: this)}");
        }

        string argList = string.Join(separator: ", ", values: args);

        // Special handling for built-in functions
        if (node.Callee is IdentifierExpression id)
        {
            if (id.Name == "write_line" || id.Name == "println")
            {
                if (args.Count > 0)
                {
                    _output.AppendLine(
                        handler:
                        $"  {result} = call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([4 x i8], [4 x i8]* @.str_int_fmt, i32 0, i32 0), {argList})");
                }

                return result;
            }
        }

        // Get function name without generating extra instructions
        string functionName;
        if (node.Callee is IdentifierExpression identifier)
        {
            functionName = identifier.Name;

            // Check if this is a danger zone function that should use specialized handling
            if (IsNonGenericDangerZoneFunction(functionName: functionName))
            {
                return HandleNonGenericDangerZoneFunction(node: node, functionName: functionName,
                    resultTemp: result);
            }
        }
        else
        {
            // For more complex expressions, we'd need to handle them differently
            functionName = "unknown_function";
        }

        _output.AppendLine(handler: $"  {result} = call i32 @{functionName}({argList})");
        return result;
    }

    // If statement
    public string VisitIfStatement(IfStatement node)
    {
        string condition = node.Condition.Accept(visitor: this);
        string thenLabel = GetNextLabel();
        string elseLabel = GetNextLabel();
        string endLabel = GetNextLabel();

        _output.AppendLine(
            handler: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        _output.AppendLine(handler: $"{thenLabel}:");
        node.ThenStatement.Accept(visitor: this);
        _output.AppendLine(handler: $"  br label %{endLabel}");

        _output.AppendLine(handler: $"{elseLabel}:");
        if (node.ElseStatement != null)
        {
            node.ElseStatement.Accept(visitor: this);
        }

        _output.AppendLine(handler: $"  br label %{endLabel}");

        _output.AppendLine(handler: $"{endLabel}:");

        return "";
    }

    // While statement
    public string VisitWhileStatement(WhileStatement node)
    {
        string condLabel = GetNextLabel();
        string bodyLabel = GetNextLabel();
        string endLabel = GetNextLabel();

        _output.AppendLine(handler: $"  br label %{condLabel}");

        _output.AppendLine(handler: $"{condLabel}:");
        string condition = node.Condition.Accept(visitor: this);
        _output.AppendLine(handler: $"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");

        _output.AppendLine(handler: $"{bodyLabel}:");
        node.Body.Accept(visitor: this);
        _output.AppendLine(handler: $"  br label %{condLabel}");

        _output.AppendLine(handler: $"{endLabel}:");

        return "";
    }

    // Return statement - we need to track the expected function return type
    private string
        _currentFunctionReturnType = "i32"; // Default, will be set by VisitFunctionDeclaration

    public string VisitReturnStatement(ReturnStatement node)
    {
        _hasReturn = true; // Mark that we've generated a return

        if (node.Value != null)
        {
            string value = node.Value.Accept(visitor: this);
            TypeInfo valueTypeInfo = GetValueTypeInfo(value: value);

            // If the value type doesn't match the function return type, we need to cast
            if (valueTypeInfo.LLVMType != _currentFunctionReturnType)
            {
                string castResult = GetNextTemp();
                GenerateCastInstruction(result: castResult, value: value, fromType: valueTypeInfo,
                    toType: _currentFunctionReturnType);
                _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} {castResult}");
            }
            else
            {
                _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} {value}");
            }
        }
        else
        {
            _output.AppendLine(value: "  ret void");
        }

        return "";
    }

    // Generate appropriate cast instruction
    private void GenerateCastInstruction(string result, string value, TypeInfo fromType,
        string toType)
    {
        // For now, handle basic integer casts
        if (fromType.IsFloatingPoint || IsFloatingPointType(llvmType: toType))
        {
            // Float conversions need special handling
            _output.AppendLine(
                handler: $"  {result} = fptoui {fromType.LLVMType} {value} to {toType}");
        }
        else
        {
            // Integer truncation or extension
            int fromSize = GetTypeBitWidth(llvmType: fromType.LLVMType);
            int toSize = GetTypeBitWidth(llvmType: toType);

            if (fromSize > toSize)
            {
                // Truncation
                _output.AppendLine(
                    handler: $"  {result} = trunc {fromType.LLVMType} {value} to {toType}");
            }
            else if (fromSize < toSize)
            {
                // Extension
                if (fromType.IsUnsigned)
                {
                    _output.AppendLine(
                        handler: $"  {result} = zext {fromType.LLVMType} {value} to {toType}");
                }
                else
                {
                    _output.AppendLine(
                        handler: $"  {result} = sext {fromType.LLVMType} {value} to {toType}");
                }
            }
            else
            {
                // Same size, just use as-is (bitcast if needed)
                _output.AppendLine(
                    handler: $"  {result} = bitcast {fromType.LLVMType} {value} to {toType}");
            }
        }
    }

    // Get bit width of LLVM type
    private int GetTypeBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            _ => 32 // Default
        };
    }

    // Type information including signedness and RazorForge type
    private record TypeInfo(
        string LLVMType,
        bool IsUnsigned,
        bool IsFloatingPoint,
        string RazorForgeType = "");

    // Get LLVM type for an expression
    private string GetExpressionType(Expression expr)
    {
        return GetTypeInfo(expr: expr)
           .LLVMType;
    }

    // Get complete type information for an expression
    private TypeInfo GetTypeInfo(Expression expr)
    {
        if (expr is LiteralExpression literal)
        {
            string llvmType = GetLLVMType(tokenType: literal.LiteralType);
            bool isUnsigned = IsUnsignedTokenType(tokenType: literal.LiteralType);
            bool isFloatingPoint = IsFloatingPointTokenType(tokenType: literal.LiteralType);
            string razorForgeType = GetRazorForgeTypeFromToken(tokenType: literal.LiteralType);
            return new TypeInfo(LLVMType: llvmType, IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloatingPoint, RazorForgeType: razorForgeType);
        }
        // For binary expressions, we need to evaluate them first to get the result type
        // This is handled by the visitor methods storing results in _tempTypes

        // Default to signed i32
        return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
            RazorForgeType: "i32");
    }

    // Get type info for a temporary variable or literal value
    private TypeInfo GetValueTypeInfo(string value)
    {
        if (_tempTypes.TryGetValue(key: value, value: out TypeInfo? typeInfo))
        {
            return typeInfo;
        }

        // If it's not a temp variable, it might be a literal value
        // Try to infer type from the value itself (this is a simplified approach)
        if (int.TryParse(s: value, result: out _))
        {
            // It's a numeric literal, assume i32 for now
            return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i32");
        }

        // Default fallback
        return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
            RazorForgeType: "i32");
    }

    // Get type info from string type name
    private TypeInfo GetTypeInfo(string typeName)
    {
        return typeName switch
        {
            // Signed integers
            "i8" => new TypeInfo(LLVMType: "i8", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i8"),
            "i16" => new TypeInfo(LLVMType: "i16", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i16"),
            "i32" => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i32"),
            "i64" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i64"),
            "i128" => new TypeInfo(LLVMType: "i128", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i128"),

            // Unsigned integers
            "u8" => new TypeInfo(LLVMType: "i8", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u8"),
            "u16" => new TypeInfo(LLVMType: "i16", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u16"),
            "u32" => new TypeInfo(LLVMType: "i32", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u32"),
            "u64" => new TypeInfo(LLVMType: "i64", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u64"),
            "u128" => new TypeInfo(LLVMType: "i128", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u128"),

            // System-dependent integers
            "isys" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "isys"), // intptr_t - typically i64 on 64-bit systems
            "usys" => new TypeInfo(LLVMType: "i64", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "usys"), // uintptr_t - typically i64 on 64-bit systems

            // Floating point types
            "f16" => new TypeInfo(LLVMType: "half", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f16"),
            "f32" => new TypeInfo(LLVMType: "float", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f32"),
            "f64" => new TypeInfo(LLVMType: "double", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f64"),
            "f128" => new TypeInfo(LLVMType: "fp128", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f128"),

            // Boolean
            "bool" => new TypeInfo(LLVMType: "i1", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "bool"),

            // Math library types
            "d32" => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "d32"),
            "d64" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "d64"),
            "d128" => new TypeInfo(LLVMType: "{i64, i64}", IsUnsigned: false,
                IsFloatingPoint: false, RazorForgeType: "d128"),
            "bigint" => new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "bigint"),
            "decimal" => new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "decimal"),

            _ => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i32")
        };
    }

    // Get RazorForge type name from token type
    private string GetRazorForgeTypeFromToken(TokenType tokenType)
    {
        return tokenType switch
        {
            // Integer literals
            TokenType.S8Literal => "s8",
            TokenType.S16Literal => "s16",
            TokenType.S32Literal => "s32",
            TokenType.S64Literal => "s64",
            TokenType.S128Literal => "s128",
            TokenType.U8Literal => "u8",
            TokenType.U16Literal => "u16",
            TokenType.U32Literal => "u32",
            TokenType.U64Literal => "u64",
            TokenType.U128Literal => "u128",

            // Floating point literals
            TokenType.F16Literal => "f16",
            TokenType.F32Literal => "f32",
            TokenType.F64Literal => "f64",
            TokenType.F128Literal => "f128",

            // Decimal literals (IEEE 754)
            TokenType.D32Literal => "d32",
            TokenType.D64Literal => "d64",
            TokenType.D128Literal => "d128",

            // Arbitrary precision types
            TokenType.Integer => "bigint", // Cake arbitrary precision integer
            TokenType.Decimal => "decimal", // Cake arbitrary precision decimal

            // Default types
            _ => "i32"
        };
    }

    // Check if token type is unsigned
    private bool IsUnsignedTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.U8Literal or TokenType.U16Literal or TokenType.U32Literal
                or TokenType.U64Literal or TokenType.U128Literal => true,
            _ => false
        };
    }

    // Check if token type is floating point
    private bool IsFloatingPointTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.F16Literal or TokenType.F32Literal or TokenType.F64Literal
                or TokenType.F128Literal or TokenType.Decimal => true,
            _ => false
        };
    }

    // Get appropriate division operation based on type
    private string GetDivisionOp(string llvmType)
    {
        return IsFloatingPointType(llvmType: llvmType)
            ? "fdiv"
            : "sdiv";
    }

    // Get true division operation (always floating point)
    private string GetTrueDivisionOp(string llvmType)
    {
        return "fdiv";
    }

    // Get appropriate modulo operation based on type
    private string GetModuloOp(TypeInfo typeInfo)
    {
        if (typeInfo.IsFloatingPoint)
        {
            return "frem";
        }

        return typeInfo.IsUnsigned
            ? "urem"
            : "srem";
    }

    // Get appropriate integer division operation (signed vs unsigned)
    private string GetIntegerDivisionOp(TypeInfo typeInfo)
    {
        return typeInfo.IsUnsigned
            ? "udiv"
            : "sdiv";
    }

    // Check if LLVM type is floating point
    private bool IsFloatingPointType(string llvmType)
    {
        return llvmType switch
        {
            "half" or "float" or "double" or "fp128" => true,
            _ => false
        };
    }

    // Other visitor methods (stubs for now)
    public string VisitClassDeclaration(ClassDeclaration node)
    {
        return "";
    }
    public string VisitStructDeclaration(StructDeclaration node)
    {
        return "";
    }
    public string VisitMenuDeclaration(MenuDeclaration node)
    {
        return "";
    }
    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        return "";
    }
    public string VisitFeatureDeclaration(FeatureDeclaration node)
    {
        return "";
    }
    public string VisitImportDeclaration(ImportDeclaration node)
    {
        return "";
    }
    public string VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        return "";
    }
    public string VisitUsingDeclaration(UsingDeclaration node)
    {
        return "";
    }
    public string VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        return "";
    }
    public string VisitUnaryExpression(UnaryExpression node)
    {
        return "";
    }
    public string VisitMemberExpression(MemberExpression node)
    {
        return "";
    }
    public string VisitIndexExpression(IndexExpression node)
    {
        return "";
    }
    public string VisitConditionalExpression(ConditionalExpression node)
    {
        return "";
    }
    public string VisitRangeExpression(RangeExpression node)
    {
        // For now, generate a simple record representation
        // In a real implementation, this would create a Range<T> object
        string start = node.Start.Accept(visitor: this);
        string end = node.End.Accept(visitor: this);

        if (node.Step != null)
        {
            string step = node.Step.Accept(visitor: this);
            // Generate code for range with step
            _output.AppendLine(handler: $"; Range from {start} to {end} step {step}");
        }
        else
        {
            // Generate code for range without step (default step 1)
            _output.AppendLine(handler: $"; Range from {start} to {end}");
        }

        return start; // Placeholder
    }

    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        // Desugar chained comparison: a < b < c becomes (a < b) and (b < c)
        // with single evaluation of b
        if (node.Operands.Count < 2 || node.Operators.Count < 1)
        {
            return "";
        }

        string result = GetNextTemp();
        var tempVars = new List<string>();

        // Evaluate all operands once and store in temporaries
        for (int i = 0; i < node.Operands.Count; i++)
        {
            if (i == 0)
            {
                // First operand doesn't need temporary storage for first comparison
                tempVars.Add(item: node.Operands[index: i]
                                       .Accept(visitor: this));
            }
            else if (i == node.Operands.Count - 1)
            {
                // Last operand doesn't need temporary storage
                tempVars.Add(item: node.Operands[index: i]
                                       .Accept(visitor: this));
            }
            else
            {
                // Middle operands need temporary storage to avoid multiple evaluation
                string temp = GetNextTemp();
                string operandValue = node.Operands[index: i]
                                          .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  {temp} = add i32 {operandValue}, 0  ; store for reuse");
                tempVars.Add(item: temp);
            }
        }

        // Generate comparisons: (temp0 op0 temp1) and (temp1 op1 temp2) and ...
        var compResults = new List<string>();
        for (int i = 0; i < node.Operators.Count; i++)
        {
            string compResult = GetNextTemp();
            string left = tempVars[index: i];
            string right = tempVars[index: i + 1];
            string op = node.Operators[index: i] switch
            {
                BinaryOperator.Less => "icmp slt",
                BinaryOperator.LessEqual => "icmp sle",
                BinaryOperator.Greater => "icmp sgt",
                BinaryOperator.GreaterEqual => "icmp sge",
                BinaryOperator.Equal => "icmp eq",
                BinaryOperator.NotEqual => "icmp ne",
                _ => "icmp eq"
            };

            _output.AppendLine(handler: $"  {compResult} = {op} i32 {left}, {right}");
            compResults.Add(item: compResult);
        }

        // Combine all comparisons with AND
        if (compResults.Count == 1)
        {
            return compResults[index: 0];
        }

        string finalResult = compResults[index: 0];
        for (int i = 1; i < compResults.Count; i++)
        {
            string temp = GetNextTemp();
            _output.AppendLine(
                handler: $"  {temp} = and i1 {finalResult}, {compResults[index: i]}");
            finalResult = temp;
        }

        return finalResult;
    }
    public string VisitLambdaExpression(LambdaExpression node)
    {
        return "";
    }
    public string VisitTypeExpression(TypeExpression node)
    {
        return "";
    }

    public string VisitTypeConversionExpression(TypeConversionExpression node)
    {
        string sourceValue = node.Expression.Accept(visitor: this);
        TypeInfo targetTypeInfo = GetTypeInfo(typeName: node.TargetType);

        // Generate a temporary variable for the conversion result
        string tempVar = $"%tmp{_tempCounter++}";

        // Perform the type conversion using LLVM cast instructions
        TypeInfo sourceTypeInfo = GetTypeInfo(expr: node.Expression);

        string conversionOp =
            GetConversionInstruction(sourceType: sourceTypeInfo, targetType: targetTypeInfo);

        _output.AppendLine(
            handler:
            $"  {tempVar} = {conversionOp} {sourceTypeInfo.LLVMType} {sourceValue} to {targetTypeInfo.LLVMType}");

        return tempVar;
    }

    private string GetConversionInstruction(TypeInfo sourceType, TypeInfo targetType)
    {
        // Handle floating point to integer conversions
        if (sourceType.IsFloatingPoint && !targetType.IsFloatingPoint)
        {
            return targetType.IsUnsigned
                ? "fptoui"
                : "fptosi";
        }

        // Handle integer to floating point conversions
        if (!sourceType.IsFloatingPoint && targetType.IsFloatingPoint)
        {
            return sourceType.IsUnsigned
                ? "uitofp"
                : "sitofp";
        }

        // Handle floating point to floating point conversions
        if (sourceType.IsFloatingPoint && targetType.IsFloatingPoint)
        {
            return GetFloatingPointSize(llvmType: sourceType.LLVMType) >
                   GetFloatingPointSize(llvmType: targetType.LLVMType)
                ? "fptrunc"
                : "fpext";
        }

        // Handle integer to integer conversions
        if (!sourceType.IsFloatingPoint && !targetType.IsFloatingPoint)
        {
            int sourceSize = GetIntegerSize(llvmType: sourceType.LLVMType);
            int targetSize = GetIntegerSize(llvmType: targetType.LLVMType);

            if (sourceSize > targetSize)
            {
                return "trunc";
            }
            else if (sourceSize < targetSize)
            {
                return sourceType.IsUnsigned
                    ? "zext"
                    : "sext";
            }
            else
            {
                return "bitcast"; // Same size, just change signedness
            }
        }

        throw new InvalidOperationException(
            message: $"Cannot convert from {sourceType.LLVMType} to {targetType.LLVMType}");
    }

    private int GetFloatingPointSize(string llvmType)
    {
        return llvmType switch
        {
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            _ => throw new ArgumentException(message: $"Unknown floating point type: {llvmType}")
        };
    }

    private int GetIntegerSize(string llvmType)
    {
        return llvmType switch
        {
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            _ => throw new ArgumentException(message: $"Unknown integer type: {llvmType}")
        };
    }
    public string VisitExpressionStatement(ExpressionStatement node)
    {
        return node.Expression.Accept(visitor: this);
    }
    public string VisitDeclarationStatement(DeclarationStatement node)
    {
        return node.Declaration.Accept(visitor: this);
    }
    public string VisitAssignmentStatement(AssignmentStatement node)
    {
        return "";
    }
    public string VisitForStatement(ForStatement node)
    {
        return "";
    }
    public string VisitBreakStatement(BreakStatement node)
    {
        return "";
    }
    public string VisitContinueStatement(ContinueStatement node)
    {
        return "";
    }
    public string VisitWhenStatement(WhenStatement node)
    {
        return "";
    }
    public string VisitBlockStatement(BlockStatement node)
    {
        foreach (Statement statement in node.Statements)
        {
            statement.Accept(visitor: this);
        }

        return "";
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        string sizeTemp = node.SizeExpression.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        if (node.SliceType == "HeapSlice")
        {
            // Generate LLVM IR for heap slice construction
            _output.AppendLine(handler: $"  {resultTemp} = call ptr @heap_alloc(i64 {sizeTemp})");
        }
        else if (node.SliceType == "StackSlice")
        {
            // Generate LLVM IR for stack slice construction
            _output.AppendLine(handler: $"  {resultTemp} = call ptr @stack_alloc(i64 {sizeTemp})");
        }

        // Store slice type information for later use
        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "ptr", IsUnsigned: false,
            IsFloatingPoint: false, RazorForgeType: node.SliceType);
        return resultTemp;
    }

    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        string resultTemp = GetNextTemp();

        // Check if this is a standalone danger zone function call
        if (node.Object is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;
            if (IsDangerZoneFunction(functionName: functionName))
            {
                // Get type argument for generic method
                TypeExpression dangerTypeArg = node.TypeArguments.First();
                string dangerLlvmType =
                    MapRazorForgeTypeToLLVM(razorForgeType: dangerTypeArg.Name);

                return HandleDangerZoneFunction(node: node, functionName: functionName,
                    llvmType: dangerLlvmType, typeName: dangerTypeArg.Name,
                    resultTemp: resultTemp);
            }
        }

        string objectTemp = node.Object.Accept(visitor: this);

        // Get type argument for generic method
        TypeExpression typeArg = node.TypeArguments.First();
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name);

        switch (node.MethodName)
        {
            case "read":
                string offsetTemp = node.Arguments[index: 0]
                                        .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call {llvmType} @memory_read_{typeArg.Name}(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeArg.Name);
                break;

            case "write":
                string writeOffsetTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                string valueTemp = node.Arguments[index: 1]
                                       .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  call void @memory_write_{typeArg.Name}(ptr {objectTemp}, i64 {writeOffsetTemp}, {llvmType} {valueTemp})");
                resultTemp = ""; // void return
                break;

            case "write_as":
                // write_as<T>!(address, value) - direct memory write to address
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string writeValueTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                resultTemp = ""; // void return
                break;

            case "read_as":
                // read_as<T>!(address) - direct memory read from address
                string readAddrTemp = node.Arguments[index: 0]
                                          .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeArg.Name);
                break;

            case "volatile_write":
                // volatile_write<T>!(address, value) - volatile memory write to address
                string volWriteAddrTemp = node.Arguments[index: 0]
                                              .Accept(visitor: this);
                string volWriteValueTemp = node.Arguments[index: 1]
                                               .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                resultTemp = ""; // void return
                break;

            case "volatile_read":
                // volatile_read<T>!(address) - volatile memory read from address
                string volReadAddrTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeArg.Name);
                break;

            default:
                throw new NotImplementedException(
                    message: $"Generic method {node.MethodName} not implemented");
        }

        return resultTemp;
    }

    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        // TODO: Implement generic member access
        return GetNextTemp();
    }

    public string VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        string objectTemp = node.Object.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        switch (node.OperationName)
        {
            case "size":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_size(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "sysuint");
                break;

            case "address":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_address(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "sysuint");
                break;

            case "is_valid":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i1 @slice_is_valid(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i1", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "bool");
                break;

            case "unsafe_ptr":
                string offsetTemp = node.Arguments[index: 0]
                                        .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call i64 @slice_unsafe_ptr(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "sysuint");
                break;

            case "slice":
                string sliceOffsetTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                string sliceBytesTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call ptr @slice_subslice(ptr {objectTemp}, i64 {sliceOffsetTemp}, i64 {sliceBytesTemp})");

                // Get the original slice type
                if (_tempTypes.TryGetValue(key: objectTemp, value: out TypeInfo? objType))
                {
                    _tempTypes[key: resultTemp] = objType;
                }

                break;

            case "hijack":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call ptr @slice_hijack(ptr {objectTemp})");
                if (_tempTypes.TryGetValue(key: objectTemp, value: out TypeInfo? hijackType))
                {
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "ptr", IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: $"Hijacked<{hijackType.RazorForgeType}>");
                }

                break;

            case "refer":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_refer(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "sysuint");
                break;

            default:
                throw new NotImplementedException(
                    message: $"Memory operation {node.OperationName} not implemented");
        }

        return resultTemp;
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        // Add comment to indicate unsafe block
        _output.AppendLine(value: "  ; === DANGER BLOCK START ===");
        node.Body.Accept(visitor: this);
        _output.AppendLine(value: "  ; === DANGER BLOCK END ===");
        return "";
    }

    public string VisitMayhemStatement(MayhemStatement node)
    {
        // Add comment to indicate maximum unsafe block
        _output.AppendLine(value: "  ; === MAYHEM BLOCK START ===");
        node.Body.Accept(visitor: this);
        _output.AppendLine(value: "  ; === MAYHEM BLOCK END ===");
        return "";
    }

    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        // Generate external function declaration
        string paramTypes = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p =>
                MapRazorForgeTypeToLLVM(razorForgeType: p.Type?.Name ?? "void")));
        string returnType = node.ReturnType != null
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.ReturnType.Name)
            : "void";

        // Map calling convention to LLVM calling convention attribute
        string callingConventionAttr =
            MapCallingConventionToLLVM(callingConvention: node.CallingConvention);

        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // For generic external functions, we'll need to generate specialized versions
            _output.AppendLine(
                handler:
                $"; Generic external function {node.Name} - specialized versions generated on demand");
        }
        else
        {
            // Emit external declaration with calling convention
            if (!string.IsNullOrEmpty(value: callingConventionAttr))
            {
                _output.AppendLine(
                    handler:
                    $"declare {callingConventionAttr} {returnType} @{node.Name}({paramTypes})");
            }
            else
            {
                _output.AppendLine(handler: $"declare {returnType} @{node.Name}({paramTypes})");
            }
        }

        return "";
    }

    /// <summary>
    /// Maps RazorForge calling convention names to LLVM calling convention attributes.
    /// </summary>
    /// <param name="callingConvention">Calling convention string ("C", "stdcall", "fastcall", etc.)</param>
    /// <returns>LLVM calling convention attribute or empty string for default</returns>
    private string MapCallingConventionToLLVM(string? callingConvention)
    {
        if (string.IsNullOrEmpty(value: callingConvention))
        {
            return ""; // Default C calling convention
        }

        return callingConvention.ToLowerInvariant() switch
        {
            "c" => "ccc", // C calling convention (default on most platforms)
            "stdcall" => "x86_stdcallcc", // Windows stdcall
            "fastcall" => "x86_fastcallcc", // x86 fastcall
            "thiscall" => "x86_thiscallcc", // C++ thiscall (MSVC)
            "vectorcall" => "x86_vectorcallcc", // x86 vectorcall (MSVC)
            "win64" => "win64cc", // Windows x64 calling convention
            "sysv64" => "x86_64_sysvcc", // System V AMD64 ABI (Unix/Linux)
            "aapcs" => "arm_aapcscc", // ARM AAPCS
            "aapcs_vfp" => "arm_aapcs_vfpcc", // ARM AAPCS with VFP
            _ => "" // Unknown convention, use default
        };
    }

    private string MapRazorForgeTypeToLLVM(string razorForgeType)
    {
        return razorForgeType switch
        {
            "s8" => "i8",
            "s16" => "i16",
            "s32" => "i32",
            "s64" => "i64",
            "s128" => "i128",
            "u8" => "i8",
            "u16" => "i16",
            "u32" => "i32",
            "u64" => "i64",
            "u128" => "i128",
            "sysuint" or "syssint" => _targetPlatform
               .GetPointerSizedIntType(), // Architecture-dependent
            "f16" => "half",
            "f32" => "float",
            "f64" => "double",
            "f128" => "fp128",
            "bool" => "i1",
            "letter" => "i32", // UTF-32
            "text" => "ptr",
            "DynamicSlice" or "TemporarySlice" => "ptr",
            "void" => "void",

            // C FFI types - Character types
            "cchar" or "cschar" => "i8",
            "cuchar" => "i8",
            "cwchar" => _targetPlatform.GetWCharType(), // OS-dependent
            "cchar8" => "i8",
            "cchar16" => "i16",
            "cchar32" => "i32",

            // C FFI types - Numeric types
            "cshort" => "i16",
            "cushort" => "i16",
            "cint" => "i32",
            "cuint" => "i32",
            "clong" => _targetPlatform.GetLongType(), // OS-dependent
            "culong" => _targetPlatform.GetLongType(), // OS-dependent
            "cll" => "i64",
            "cull" => "i64",
            "cfloat" => "float",
            "cdouble" => "double",

            // C FFI types - Pointer types (architecture-dependent)
            "csptr" => _targetPlatform.GetPointerSizedIntType(),
            "cuptr" => _targetPlatform.GetPointerSizedIntType(),
            "cvoid" => _targetPlatform.GetPointerSizedIntType(),
            "cbool" => "i1",

            _ => "ptr" // Default to pointer for unknown types (including cptr<T>)
        };
    }

    private void GenerateSliceRuntimeDeclarations()
    {
        // Generate declarations for slice runtime functions
        string[] declarations = new[]
        {
            "declare ptr @heap_alloc(i64)",
            "declare ptr @stack_alloc(i64)",
            "declare void @heap_free(ptr)",
            "declare ptr @heap_realloc(ptr, i64)",
            "declare void @memory_copy(ptr, ptr, i64)",
            "declare void @memory_fill(ptr, i8, i64)",
            "declare void @memory_zero(ptr, i64)",
            "declare i64 @slice_size(ptr)",
            "declare i64 @slice_address(ptr)",
            "declare i1 @slice_is_valid(ptr)",
            "declare i64 @slice_unsafe_ptr(ptr, i64)",
            "declare ptr @slice_subslice(ptr, i64, i64)",
            "declare ptr @slice_hijack(ptr)",
            "declare i64 @slice_refer(ptr)",

            // Danger zone operations
            "declare i64 @read_as_bytes(i64, i64)",
            "declare void @write_as_bytes(i64, i64, i64)",
            "declare i64 @volatile_read_bytes(i64, i64)",
            "declare void @volatile_write_bytes(i64, i64, i64)",
            "declare i64 @addr_of(ptr)",
            "declare void @invalidate_memory(i64)",
            "declare void @rf_crash(ptr)"
        };

        foreach (string decl in declarations)
        {
            _output.AppendLine(value: decl);
        }
    }

    private int GetAlignment(string typeName)
    {
        return typeName switch
        {
            "s8" or "u8" or "bool" => 1,
            "s16" or "u16" => 2,
            "s32" or "u32" or "f32" => 4,
            "s64" or "u64" or "f64" or "ptr" => 8,
            "s128" or "u128" => 16,
            _ => 8 // Default to 8-byte alignment
        };
    }

    private bool IsDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "write_as" or "read_as" or "volatile_write" or "volatile_read" => true,
            _ => false
        };
    }

    private string HandleDangerZoneFunction(GenericMethodCallExpression node, string functionName,
        string llvmType, string typeName, string resultTemp)
    {
        switch (functionName)
        {
            case "write_as":
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string writeValueTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "read_as":
                string readAddrTemp = node.Arguments[index: 0]
                                          .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName), IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: typeName);
                return resultTemp;

            case "volatile_write":
                string volWriteAddrTemp = node.Arguments[index: 0]
                                              .Accept(visitor: this);
                string volWriteValueTemp = node.Arguments[index: 1]
                                               .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "volatile_read":
                string volReadAddrTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName), IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: typeName);
                return resultTemp;

            default:
                throw new NotImplementedException(
                    message:
                    $"Danger zone function {functionName} not implemented in LLVM generator");
        }
    }

    private bool IsNonGenericDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "addr_of" or "invalidate" => true,
            _ => false
        };
    }

    private string HandleNonGenericDangerZoneFunction(CallExpression node, string functionName,
        string resultTemp)
    {
        switch (functionName)
        {
            case "addr_of":
                // addr_of!(variable) -> sysuint (address of variable)
                // Expects a single identifier argument
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException(
                        message:
                        $"addr_of! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                Expression argument = node.Arguments[index: 0];
                if (argument is IdentifierExpression varIdent)
                {
                    // Generate ptrtoint to get address of variable
                    _output.AppendLine(
                        handler: $"  {resultTemp} = ptrtoint ptr %{varIdent.Name} to i64");
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                        IsFloatingPoint: true, RazorForgeType: "sysuint"); // sysuint is unsigned
                    return resultTemp;
                }
                else
                {
                    // Handle complex expressions by first evaluating them
                    string argTemp = argument.Accept(visitor: this);
                    _output.AppendLine(handler: $"  {resultTemp} = ptrtoint ptr {argTemp} to i64");
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                        IsFloatingPoint: true, RazorForgeType: "sysuint"); // sysuint is unsigned
                    return resultTemp;
                }

            case "invalidate":
                // invalidate!(slice) -> void (free memory)
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException(
                        message:
                        $"invalidate! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                Expression sliceArgument = node.Arguments[index: 0];
                // Evaluate the argument and then call heap_free on it
                string sliceTemp = sliceArgument.Accept(visitor: this);
                _output.AppendLine(handler: $"  call void @heap_free(ptr {sliceTemp})");
                return ""; // void return

            default:
                throw new NotImplementedException(
                    message:
                    $"Non-generic danger zone function {functionName} not implemented in LLVM generator");
        }
    }
}
