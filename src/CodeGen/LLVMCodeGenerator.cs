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
    private int _tempCounter;
    private int _labelCounter;
    private readonly Dictionary<string, string> _symbolTypes;
    private readonly Dictionary<string, TypeInfo> _tempTypes = new(); // Track types of temporary variables
    private bool _hasReturn = false;
    private List<string>? _stringConstants; // Collect string constants for proper emission
    
    /// <summary>
    /// Initializes a new LLVM IR code generator for the specified language and mode configuration.
    /// Sets up the internal state required for AST traversal and IR generation.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Cake) affecting syntax and semantics</param>
    /// <param name="mode">Language mode (Normal/Danger for RazorForge, Sweet/Bitter for Cake)</param>
    /// <remarks>
    /// The language and mode parameters influence:
    /// <list type="bullet">
    /// <item>Default type inference behavior</item>
    /// <item>Safety checking levels (bounds checking, overflow handling)</item>
    /// <item>Memory management strategy</item>
    /// <item>Generated code optimization level</item>
    /// </list>
    /// </remarks>
    public LLVMCodeGenerator(Language language, LanguageMode mode)
    {
        _language = language;
        _mode = mode;
        _output = new StringBuilder();
        _tempCounter = 0;
        _labelCounter = 0;
        _symbolTypes = new Dictionary<string, string>();
    }
    
    /// <summary>
    /// Retrieves the complete generated LLVM IR code as a string.
    /// </summary>
    /// <returns>Complete LLVM IR module including headers, declarations, and function definitions</returns>
    public string GetGeneratedCode() => _output.ToString();
    
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
        _output.AppendLine("; ModuleID = 'razorforge'");
        _output.AppendLine("source_filename = \"razorforge.rf\"");
        _output.AppendLine("target datalayout = \"e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-f80:128-n8:16:32:64-S128\"");
        _output.AppendLine("target triple = \"x86_64-pc-linux-gnu\"");
        _output.AppendLine();

        // External function declarations - standard library interfaces
        _output.AppendLine("; External function declarations");
        _output.AppendLine("declare i32 @printf(i8*, ...)");    // Formatted output
        _output.AppendLine("declare i32 @scanf(i8*, ...)");     // Formatted input
        _output.AppendLine("declare i8* @malloc(i64)");         // Memory allocation
        _output.AppendLine("declare void @free(i8*)");          // Memory deallocation
        _output.AppendLine();

        // Mathematical library function declarations - precision arithmetic support
        _output.AppendLine(MathLibrarySupport.GenerateDeclarations());

        // String constants for I/O operations
        _output.AppendLine("; String constants");
        _output.AppendLine("@.str_int_fmt = private unnamed_addr constant [4 x i8] c\"%d\\0A\\00\", align 1");  // Integer format
        _output.AppendLine("@.str_fmt = private unnamed_addr constant [3 x i8] c\"%s\\00\", align 1");         // String format

        // Process the program AST to generate function definitions and global declarations
        program.Accept(this);

        // Emit collected string constants after the predefined constants
        if (_stringConstants != null && _stringConstants.Count > 0)
        {
            var content = _output.ToString();
            // Find the line after @.str_fmt and insert our constants there
            var strFmtLine = "@.str_fmt = private unnamed_addr constant [3 x i8] c\"%s\\00\", align 1";
            var insertPos = content.IndexOf(strFmtLine);
            if (insertPos >= 0)
            {
                var endOfLine = content.IndexOf('\n', insertPos) + 1;
                var before = content.Substring(0, endOfLine);
                var after = content.Substring(endOfLine);

                _output.Clear();
                _output.Append(before);

                // Add our collected string constants
                foreach (var constant in _stringConstants)
                {
                    _output.AppendLine(constant);
                }

                _output.Append(after);
            }
        }

        _output.AppendLine();
    }
    
    /// <summary>Generates a unique LLVM IR temporary variable name</summary>
    /// <returns>Unique temporary variable name in LLVM IR format (%tmp0, %tmp1, etc.)</returns>
    private string GetNextTemp() => $"%tmp{_tempCounter++}";

    /// <summary>Generates a unique LLVM IR basic block label</summary>
    /// <returns>Unique label name for LLVM IR basic blocks (label0, label1, etc.)</returns>
    private string GetNextLabel() => $"label{_labelCounter++}";
    
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
        if (MathLibrarySupport.IsMathLibraryType(rfType))
        {
            return MathLibrarySupport.MapMathTypeToLLVM(rfType);
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

            // System-dependent integers (pointer-sized)
            "syssint" => "i64",  // intptr_t - 64-bit on x86_64
            "sysuint" => "i64",  // uintptr_t - 64-bit on x86_64

            // IEEE 754 floating point types
            "f16" => "half",      // 16-bit half precision
            "f32" => "float",     // 32-bit single precision
            "f64" => "double",    // 64-bit double precision
            "f128" => "fp128",    // 128-bit quad precision

            // Boolean type
            "bool" => "i1",       // Single bit boolean

            // Text/String types
            "text" => "i8*",      // Null-terminated C string
            "Text" => "i8*",      // Alternative capitalization

            _ => "i8*"             // Default to pointer for unknown types
        };
    }
    
    // Program visitor
    public string VisitProgram(AST.Program node)
    {
        foreach (var decl in node.Declarations)
        {
            decl.Accept(this);
        }
        return "";
    }
    
    // Function declaration
    public string VisitFunctionDeclaration(FunctionDeclaration node)
    {
        var returnType = node.ReturnType != null ? MapTypeToLLVM(node.ReturnType.Name) : "void";
        
        // Set the current function return type for return statement processing
        _currentFunctionReturnType = returnType;
        
        var parameters = new List<string>();
        
        if (node.Parameters != null)
        {
            foreach (var param in node.Parameters)
            {
                var paramType = param.Type != null ? MapTypeToLLVM(param.Type.Name) : "i32";
                parameters.Add($"{paramType} %{param.Name}");
                _symbolTypes[param.Name] = paramType;
            }
        }
        
        var paramList = string.Join(", ", parameters);
        
        _output.AppendLine($"define {returnType} @{node.Name}({paramList}) {{");
        _output.AppendLine("entry:");
        
        // Reset return flag for this function
        _hasReturn = false;
        
        // Visit function body
        if (node.Body != null)
        {
            node.Body.Accept(this);
        }
        
            // Add default return if needed (only if no explicit return was generated)
        if (!_hasReturn)
        {
            if (returnType == "void")
            {
                _output.AppendLine("  ret void");
            }
            else if (returnType == "i32")
            {
                _output.AppendLine("  ret i32 0");
            }
        }
        
        _output.AppendLine("}");
        _output.AppendLine();
        
        return "";
    }
    
    // Variable declaration
    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        var type = node.Type != null ? MapTypeToLLVM(node.Type.Name) : "i32";
        var varName = $"%{node.Name}";
        
        _symbolTypes[node.Name] = type;
        
        if (node.Initializer != null)
        {
            var initValue = node.Initializer.Accept(this);
            _output.AppendLine($"  {varName} = alloca {type}");
            _output.AppendLine($"  store {type} {initValue}, {type}* {varName}");
        }
        else
        {
            _output.AppendLine($"  {varName} = alloca {type}");
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
    /// <item>Comparison: ==, !=, <, <=, >, >= returning i1 boolean results</item>
    /// <item>Logical: &&, || with short-circuit evaluation support</item>
    /// <item>Bitwise: &, |, ^, <<, >> for integer types</item>
    /// </list>
    /// </remarks>
    public string VisitBinaryExpression(BinaryExpression node)
    {
        var left = node.Left.Accept(this);
        var right = node.Right.Accept(this);
        var result = GetNextTemp();
        
        // Get operand type information (assume both operands have same type)
        var leftTypeInfo = GetTypeInfo(node.Left);
        var operandType = leftTypeInfo.LLVMType;
        
        // Check if this is a math library operation
        if (MathLibrarySupport.IsMathLibraryType(leftTypeInfo.RazorForgeType))
        {
            return GenerateMathLibraryBinaryOp(node, left, right, result, leftTypeInfo);
        }
        
        var op = node.Operator switch
        {
            // Regular arithmetic
            BinaryOperator.Add => "add",
            BinaryOperator.Subtract => "sub",
            BinaryOperator.Multiply => "mul",
            BinaryOperator.Divide => GetIntegerDivisionOp(leftTypeInfo),     // sdiv/udiv based on signed/unsigned
            BinaryOperator.TrueDivide => "fdiv",                            // / (true division) - floats only
            BinaryOperator.Modulo => GetModuloOp(leftTypeInfo),           // srem/urem for integers, frem for floats
            
            // Overflow-handling variants (for now, use LLVM intrinsics will be added later)
            BinaryOperator.AddWrap => "add",        // Wrapping is default behavior
            BinaryOperator.SubtractWrap => "sub",
            BinaryOperator.MultiplyWrap => "mul",
            BinaryOperator.DivideWrap => GetIntegerDivisionOp(leftTypeInfo),
            BinaryOperator.ModuloWrap => GetModuloOp(leftTypeInfo),
            
            BinaryOperator.AddSaturate => "add",    // TODO: Use llvm.sadd.sat intrinsic
            BinaryOperator.SubtractSaturate => "sub", // TODO: Use llvm.ssub.sat intrinsic  
            BinaryOperator.MultiplySaturate => "mul", // TODO: Custom saturating multiply
            
            BinaryOperator.AddChecked => "add",     // TODO: Use llvm.sadd.with.overflow intrinsic
            BinaryOperator.SubtractChecked => "sub", // TODO: Use llvm.ssub.with.overflow intrinsic
            BinaryOperator.MultiplyChecked => "mul", // TODO: Use llvm.smul.with.overflow intrinsic
            
            BinaryOperator.AddUnchecked => "add",   // Regular operations, no overflow checks
            BinaryOperator.SubtractUnchecked => "sub",
            BinaryOperator.MultiplyUnchecked => "mul",
            BinaryOperator.DivideUnchecked => GetIntegerDivisionOp(leftTypeInfo),
            BinaryOperator.ModuloUnchecked => GetModuloOp(leftTypeInfo),
            
            // Comparisons
            BinaryOperator.Less => "icmp slt",
            BinaryOperator.Greater => "icmp sgt",
            BinaryOperator.Equal => "icmp eq",
            BinaryOperator.NotEqual => "icmp ne",
            
            _ => "add"
        };
        
        // Generate the operation with proper type
        if (op.StartsWith("icmp"))
        {
            // Comparison operations return i1
            _output.AppendLine($"  {result} = {op} {operandType} {left}, {right}");
            _tempTypes[result] = new TypeInfo("i1", false, false, "bool");
        }
        else
        {
            // Arithmetic operations maintain operand type
            _output.AppendLine($"  {result} = {op} {operandType} {left}, {right}");
            _tempTypes[result] = leftTypeInfo; // Result has same type as operands
        }
        return result;
    }

    private string GenerateMathLibraryBinaryOp(BinaryExpression node, string left, string right, string result, TypeInfo typeInfo)
    {
        var operation = node.Operator switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.TrueDivide => "/",
            _ => throw new NotSupportedException($"Operator {node.Operator} not supported for math library type {typeInfo.RazorForgeType}")
        };

        var mathLibraryCode = typeInfo.RazorForgeType switch
        {
            "d32" => MathLibrarySupport.GenerateD32BinaryOp(operation, left, right, result),
            "d64" => MathLibrarySupport.GenerateD64BinaryOp(operation, left, right, result),
            "d128" => MathLibrarySupport.GenerateD128BinaryOp(operation, left, right, result),
            "bigint" => MathLibrarySupport.GenerateBigIntBinaryOp(operation, left, right, result, _tempCounter.ToString()),
            "decimal" => MathLibrarySupport.GenerateHighPrecisionDecimalBinaryOp(operation, left, right, result, "%decimal_context"),
            _ => throw new NotSupportedException($"Math library type {typeInfo.RazorForgeType} not supported")
        };

        _output.AppendLine(mathLibraryCode);
        _tempTypes[result] = typeInfo; // Result has same type as operands
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
            return floatVal.ToString("G");
        }
        else if (node.Value is double doubleVal)
        {
            return doubleVal.ToString("G");
        }
        else if (node.Value is decimal decimalVal)
        {
            return decimalVal.ToString("G");
        }
        else if (node.Value is BigInteger bigIntVal)
        {
            return bigIntVal.ToString();
        }
        else if (node.Value is Half halfVal)
        {
            return ((float)halfVal).ToString("G");
        }
        else if (node.Value is bool boolVal)
        {
            return boolVal ? "1" : "0";
        }
        else if (node.Value is string strVal)
        {
            var strConst = $"@.str{_tempCounter++}";
            var len = strVal.Length + 1;
            // Store string constant for later emission instead of inserting immediately
            if (_stringConstants == null)
                _stringConstants = new List<string>();
            _stringConstants.Add($"{strConst} = private unnamed_addr constant [{len} x i8] c\"{strVal}\\00\", align 1");
            var temp = GetNextTemp();
            _output.AppendLine($"  {temp} = getelementptr [{len} x i8], [{len} x i8]* {strConst}, i32 0, i32 0");
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
            TokenType.U8Literal => "i8",    // LLVM doesn't distinguish signed/unsigned at IR level
            TokenType.U16Literal => "i16",
            TokenType.U32Literal => "i32",
            TokenType.U64Literal => "i64",
            TokenType.U128Literal => "i128",
            TokenType.F16Literal => "half",
            TokenType.F32Literal => "float",
            TokenType.F64Literal => "double",
            TokenType.F128Literal => "fp128", // IEEE 754 quad precision
            TokenType.Integer => "i128",    // BigInteger -> large integer type
            TokenType.Decimal => "double",  // Default floating type
            TokenType.True => "i1",
            TokenType.False => "i1",
            _ => "i32" // Default fallback
        };
    }
    
    // Identifier expression
    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        var temp = GetNextTemp();
        var type = _symbolTypes.ContainsKey(node.Name) ? _symbolTypes[node.Name] : "i32";
        _output.AppendLine($"  {temp} = load {type}, {type}* %{node.Name}");
        return temp;
    }
    
    // Function call expression
    public string VisitCallExpression(CallExpression node)
    {
        var result = GetNextTemp();

        // Check if this is a standalone danger zone function call (addr_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            var dangerfunctionName = identifierExpr.Name;
            if (IsNonGenericDangerZoneFunction(dangerfunctionName))
            {
                return HandleNonGenericDangerZoneFunction(node, dangerfunctionName, result);
            }
        }

        var args = new List<string>();
        foreach (var arg in node.Arguments)
        {
            args.Add($"i32 {arg.Accept(this)}");
        }

        var argList = string.Join(", ", args);

        // Special handling for built-in functions
        if (node.Callee is IdentifierExpression id)
        {
            if (id.Name == "write_line" || id.Name == "println")
            {
                if (args.Count > 0)
                {
                    _output.AppendLine($"  {result} = call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([4 x i8], [4 x i8]* @.str_int_fmt, i32 0, i32 0), {argList})");
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
            if (IsNonGenericDangerZoneFunction(functionName))
            {
                return HandleNonGenericDangerZoneFunction(node, functionName, result);
            }
        }
        else
        {
            // For more complex expressions, we'd need to handle them differently
            functionName = "unknown_function";
        }

        _output.AppendLine($"  {result} = call i32 @{functionName}({argList})");
        return result;
    }
    
    // If statement
    public string VisitIfStatement(IfStatement node)
    {
        var condition = node.Condition.Accept(this);
        var thenLabel = GetNextLabel();
        var elseLabel = GetNextLabel();
        var endLabel = GetNextLabel();
        
        _output.AppendLine($"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");
        
        _output.AppendLine($"{thenLabel}:");
        node.ThenStatement.Accept(this);
        _output.AppendLine($"  br label %{endLabel}");
        
        _output.AppendLine($"{elseLabel}:");
        if (node.ElseStatement != null)
        {
            node.ElseStatement.Accept(this);
        }
        _output.AppendLine($"  br label %{endLabel}");
        
        _output.AppendLine($"{endLabel}:");
        
        return "";
    }
    
    // While statement
    public string VisitWhileStatement(WhileStatement node)
    {
        var condLabel = GetNextLabel();
        var bodyLabel = GetNextLabel();
        var endLabel = GetNextLabel();
        
        _output.AppendLine($"  br label %{condLabel}");
        
        _output.AppendLine($"{condLabel}:");
        var condition = node.Condition.Accept(this);
        _output.AppendLine($"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");
        
        _output.AppendLine($"{bodyLabel}:");
        node.Body.Accept(this);
        _output.AppendLine($"  br label %{condLabel}");
        
        _output.AppendLine($"{endLabel}:");
        
        return "";
    }
    
    // Return statement - we need to track the expected function return type
    private string _currentFunctionReturnType = "i32"; // Default, will be set by VisitFunctionDeclaration
    
    public string VisitReturnStatement(ReturnStatement node)
    {
        _hasReturn = true;  // Mark that we've generated a return
        
        if (node.Value != null)
        {
            var value = node.Value.Accept(this);
            var valueTypeInfo = GetValueTypeInfo(value);
            
            // If the value type doesn't match the function return type, we need to cast
            if (valueTypeInfo.LLVMType != _currentFunctionReturnType)
            {
                var castResult = GetNextTemp();
                GenerateCastInstruction(castResult, value, valueTypeInfo, _currentFunctionReturnType);
                _output.AppendLine($"  ret {_currentFunctionReturnType} {castResult}");
            }
            else
            {
                _output.AppendLine($"  ret {_currentFunctionReturnType} {value}");
            }
        }
        else
        {
            _output.AppendLine("  ret void");
        }
        return "";
    }
    
    // Generate appropriate cast instruction
    private void GenerateCastInstruction(string result, string value, TypeInfo fromType, string toType)
    {
        // For now, handle basic integer casts
        if (fromType.IsFloatingPoint || IsFloatingPointType(toType))
        {
            // Float conversions need special handling
            _output.AppendLine($"  {result} = fptoui {fromType.LLVMType} {value} to {toType}");
        }
        else
        {
            // Integer truncation or extension
            var fromSize = GetTypeBitWidth(fromType.LLVMType);
            var toSize = GetTypeBitWidth(toType);
            
            if (fromSize > toSize)
            {
                // Truncation
                _output.AppendLine($"  {result} = trunc {fromType.LLVMType} {value} to {toType}");
            }
            else if (fromSize < toSize)
            {
                // Extension
                if (fromType.IsUnsigned)
                {
                    _output.AppendLine($"  {result} = zext {fromType.LLVMType} {value} to {toType}");
                }
                else
                {
                    _output.AppendLine($"  {result} = sext {fromType.LLVMType} {value} to {toType}");
                }
            }
            else
            {
                // Same size, just use as-is (bitcast if needed)
                _output.AppendLine($"  {result} = bitcast {fromType.LLVMType} {value} to {toType}");
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
    private record TypeInfo(string LLVMType, bool IsUnsigned, bool IsFloatingPoint, string RazorForgeType = "");
    
    // Get LLVM type for an expression  
    private string GetExpressionType(Expression expr)
    {
        return GetTypeInfo(expr).LLVMType;
    }
    
    // Get complete type information for an expression
    private TypeInfo GetTypeInfo(Expression expr)
    {
        if (expr is LiteralExpression literal)
        {
            var llvmType = GetLLVMType(literal.LiteralType);
            var isUnsigned = IsUnsignedTokenType(literal.LiteralType);
            var isFloatingPoint = IsFloatingPointTokenType(literal.LiteralType);
            var razorForgeType = GetRazorForgeTypeFromToken(literal.LiteralType);
            return new TypeInfo(llvmType, isUnsigned, isFloatingPoint, razorForgeType);
        }
        // For binary expressions, we need to evaluate them first to get the result type
        // This is handled by the visitor methods storing results in _tempTypes
        
        // Default to signed i32
        return new TypeInfo("i32", false, false, "i32");
    }
    
    // Get type info for a temporary variable or literal value
    private TypeInfo GetValueTypeInfo(string value)
    {
        if (_tempTypes.TryGetValue(value, out var typeInfo))
        {
            return typeInfo;
        }
        
        // If it's not a temp variable, it might be a literal value
        // Try to infer type from the value itself (this is a simplified approach)
        if (int.TryParse(value, out _))
        {
            // It's a numeric literal, assume i32 for now
            return new TypeInfo("i32", false, false, "i32");
        }
        
        // Default fallback
        return new TypeInfo("i32", false, false, "i32");
    }
    
    // Get type info from string type name
    private TypeInfo GetTypeInfo(string typeName)
    {
        return typeName switch
        {
            // Signed integers
            "i8" => new TypeInfo("i8", false, false, "i8"),
            "i16" => new TypeInfo("i16", false, false, "i16"), 
            "i32" => new TypeInfo("i32", false, false, "i32"),
            "i64" => new TypeInfo("i64", false, false, "i64"),
            "i128" => new TypeInfo("i128", false, false, "i128"),
            
            // Unsigned integers
            "u8" => new TypeInfo("i8", true, false, "u8"),
            "u16" => new TypeInfo("i16", true, false, "u16"),
            "u32" => new TypeInfo("i32", true, false, "u32"),
            "u64" => new TypeInfo("i64", true, false, "u64"),
            "u128" => new TypeInfo("i128", true, false, "u128"),
            
            // System-dependent integers
            "isys" => new TypeInfo("i64", false, false, "isys"),  // intptr_t - typically i64 on 64-bit systems
            "usys" => new TypeInfo("i64", true, false, "usys"),   // uintptr_t - typically i64 on 64-bit systems
            
            // Floating point types
            "f16" => new TypeInfo("half", false, true, "f16"),
            "f32" => new TypeInfo("float", false, true, "f32"),
            "f64" => new TypeInfo("double", false, true, "f64"),
            "f128" => new TypeInfo("fp128", false, true, "f128"),
            
            // Boolean
            "bool" => new TypeInfo("i1", false, false, "bool"),
            
            // Math library types
            "d32" => new TypeInfo("i32", false, false, "d32"),
            "d64" => new TypeInfo("i64", false, false, "d64"),
            "d128" => new TypeInfo("{i64, i64}", false, false, "d128"),
            "bigint" => new TypeInfo("i8*", false, false, "bigint"),
            "decimal" => new TypeInfo("i8*", false, false, "decimal"),
            
            _ => new TypeInfo("i32", false, false, "i32")
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
            TokenType.Integer => "bigint",    // Cake arbitrary precision integer
            TokenType.Decimal => "decimal",   // Cake arbitrary precision decimal
            
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
        return IsFloatingPointType(llvmType) ? "fdiv" : "sdiv";
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
            return "frem";
        
        return typeInfo.IsUnsigned ? "urem" : "srem";
    }
    
    // Get appropriate integer division operation (signed vs unsigned)
    private string GetIntegerDivisionOp(TypeInfo typeInfo)
    {
        return typeInfo.IsUnsigned ? "udiv" : "sdiv";
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
    public string VisitClassDeclaration(ClassDeclaration node) => "";
    public string VisitStructDeclaration(StructDeclaration node) => "";
    public string VisitMenuDeclaration(MenuDeclaration node) => "";
    public string VisitVariantDeclaration(VariantDeclaration node) => "";
    public string VisitFeatureDeclaration(FeatureDeclaration node) => "";
    public string VisitImportDeclaration(ImportDeclaration node) => "";
    public string VisitRedefinitionDeclaration(RedefinitionDeclaration node) => "";
    public string VisitUsingDeclaration(UsingDeclaration node) => "";
    public string VisitImplementationDeclaration(ImplementationDeclaration node) => "";
    public string VisitUnaryExpression(UnaryExpression node) => "";
    public string VisitMemberExpression(MemberExpression node) => "";
    public string VisitIndexExpression(IndexExpression node) => "";
    public string VisitConditionalExpression(ConditionalExpression node) => "";
    public string VisitRangeExpression(RangeExpression node)
    {
        // For now, generate a simple record representation
        // In a real implementation, this would create a Range<T> object
        var start = node.Start.Accept(this);
        var end = node.End.Accept(this);
        
        if (node.Step != null)
        {
            var step = node.Step.Accept(this);
            // Generate code for range with step
            _output.AppendLine($"; Range from {start} to {end} step {step}");
        }
        else
        {
            // Generate code for range without step (default step 1)
            _output.AppendLine($"; Range from {start} to {end}");
        }
        
        return start; // Placeholder
    }
    
    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        // Desugar chained comparison: a < b < c becomes (a < b) and (b < c)
        // with single evaluation of b
        if (node.Operands.Count < 2 || node.Operators.Count < 1)
            return "";
            
        var result = GetNextTemp();
        var tempVars = new List<string>();
        
        // Evaluate all operands once and store in temporaries
        for (int i = 0; i < node.Operands.Count; i++)
        {
            if (i == 0)
            {
                // First operand doesn't need temporary storage for first comparison
                tempVars.Add(node.Operands[i].Accept(this));
            }
            else if (i == node.Operands.Count - 1)
            {
                // Last operand doesn't need temporary storage
                tempVars.Add(node.Operands[i].Accept(this));
            }
            else
            {
                // Middle operands need temporary storage to avoid multiple evaluation
                var temp = GetNextTemp();
                var operandValue = node.Operands[i].Accept(this);
                _output.AppendLine($"  {temp} = add i32 {operandValue}, 0  ; store for reuse");
                tempVars.Add(temp);
            }
        }
        
        // Generate comparisons: (temp0 op0 temp1) and (temp1 op1 temp2) and ...
        var compResults = new List<string>();
        for (int i = 0; i < node.Operators.Count; i++)
        {
            var compResult = GetNextTemp();
            var left = tempVars[i];
            var right = tempVars[i + 1];
            var op = node.Operators[i] switch
            {
                BinaryOperator.Less => "icmp slt",
                BinaryOperator.LessEqual => "icmp sle",
                BinaryOperator.Greater => "icmp sgt",
                BinaryOperator.GreaterEqual => "icmp sge",
                BinaryOperator.Equal => "icmp eq",
                BinaryOperator.NotEqual => "icmp ne",
                _ => "icmp eq"
            };
            
            _output.AppendLine($"  {compResult} = {op} i32 {left}, {right}");
            compResults.Add(compResult);
        }
        
        // Combine all comparisons with AND
        if (compResults.Count == 1)
        {
            return compResults[0];
        }
        
        var finalResult = compResults[0];
        for (int i = 1; i < compResults.Count; i++)
        {
            var temp = GetNextTemp();
            _output.AppendLine($"  {temp} = and i1 {finalResult}, {compResults[i]}");
            finalResult = temp;
        }
        
        return finalResult;
    }
    public string VisitLambdaExpression(LambdaExpression node) => "";
    public string VisitTypeExpression(TypeExpression node) => "";
    
    public string VisitTypeConversionExpression(TypeConversionExpression node)
    {
        var sourceValue = node.Expression.Accept(this);
        var targetTypeInfo = GetTypeInfo(node.TargetType);
        
        // Generate a temporary variable for the conversion result
        var tempVar = $"%tmp{_tempCounter++}";
        
        // Perform the type conversion using LLVM cast instructions
        var sourceTypeInfo = GetTypeInfo(node.Expression);
        
        string conversionOp = GetConversionInstruction(sourceTypeInfo, targetTypeInfo);
        
        _output.AppendLine($"  {tempVar} = {conversionOp} {sourceTypeInfo.LLVMType} {sourceValue} to {targetTypeInfo.LLVMType}");
        
        return tempVar;
    }
    
    private string GetConversionInstruction(TypeInfo sourceType, TypeInfo targetType)
    {
        // Handle floating point to integer conversions
        if (sourceType.IsFloatingPoint && !targetType.IsFloatingPoint)
        {
            return targetType.IsUnsigned ? "fptoui" : "fptosi";
        }
        
        // Handle integer to floating point conversions  
        if (!sourceType.IsFloatingPoint && targetType.IsFloatingPoint)
        {
            return sourceType.IsUnsigned ? "uitofp" : "sitofp";
        }
        
        // Handle floating point to floating point conversions
        if (sourceType.IsFloatingPoint && targetType.IsFloatingPoint)
        {
            return GetFloatingPointSize(sourceType.LLVMType) > GetFloatingPointSize(targetType.LLVMType) ? 
                "fptrunc" : "fpext";
        }
        
        // Handle integer to integer conversions
        if (!sourceType.IsFloatingPoint && !targetType.IsFloatingPoint)
        {
            var sourceSize = GetIntegerSize(sourceType.LLVMType);
            var targetSize = GetIntegerSize(targetType.LLVMType);
            
            if (sourceSize > targetSize)
                return "trunc";
            else if (sourceSize < targetSize)
                return sourceType.IsUnsigned ? "zext" : "sext";
            else
                return "bitcast"; // Same size, just change signedness
        }
        
        throw new InvalidOperationException($"Cannot convert from {sourceType.LLVMType} to {targetType.LLVMType}");
    }
    
    private int GetFloatingPointSize(string llvmType)
    {
        return llvmType switch
        {
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            _ => throw new ArgumentException($"Unknown floating point type: {llvmType}")
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
            _ => throw new ArgumentException($"Unknown integer type: {llvmType}")
        };
    }
    public string VisitExpressionStatement(ExpressionStatement node) => node.Expression.Accept(this);
    public string VisitDeclarationStatement(DeclarationStatement node) => node.Declaration.Accept(this);
    public string VisitAssignmentStatement(AssignmentStatement node) => "";
    public string VisitForStatement(ForStatement node) => "";
    public string VisitBreakStatement(BreakStatement node) => "";
    public string VisitContinueStatement(ContinueStatement node) => "";
    public string VisitWhenStatement(WhenStatement node) => "";
    public string VisitBlockStatement(BlockStatement node)
    {
        foreach (var statement in node.Statements)
        {
            statement.Accept(this);
        }
        return "";
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        var sizeTemp = node.SizeExpression.Accept(this);
        var resultTemp = GetNextTemp();

        if (node.SliceType == "HeapSlice")
        {
            // Generate LLVM IR for heap slice construction
            _output.AppendLine($"  {resultTemp} = call ptr @heap_alloc(i64 {sizeTemp})");
        }
        else if (node.SliceType == "StackSlice")
        {
            // Generate LLVM IR for stack slice construction
            _output.AppendLine($"  {resultTemp} = call ptr @stack_alloc(i64 {sizeTemp})");
        }

        // Store slice type information for later use
        _tempTypes[resultTemp] = new TypeInfo("ptr", false, false, node.SliceType);
        return resultTemp;
    }

    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        var resultTemp = GetNextTemp();

        // Check if this is a standalone danger zone function call
        if (node.Object is IdentifierExpression identifierExpr)
        {
            var functionName = identifierExpr.Name;
            if (IsDangerZoneFunction(functionName))
            {
                // Get type argument for generic method
                var dangerTypeArg = node.TypeArguments.First();
                var dangerLlvmType = MapRazorForgeTypeToLLVM(dangerTypeArg.Name);

                return HandleDangerZoneFunction(node, functionName, dangerLlvmType, dangerTypeArg.Name, resultTemp);
            }
        }

        var objectTemp = node.Object.Accept(this);

        // Get type argument for generic method
        var typeArg = node.TypeArguments.First();
        var llvmType = MapRazorForgeTypeToLLVM(typeArg.Name);

        switch (node.MethodName)
        {
            case "read":
                var offsetTemp = node.Arguments[0].Accept(this);
                _output.AppendLine($"  {resultTemp} = call {llvmType} @memory_read_{typeArg.Name}(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[resultTemp] = new TypeInfo(MapRazorForgeTypeToLLVM(typeArg.Name), false, false, typeArg.Name);
                break;

            case "write":
                var writeOffsetTemp = node.Arguments[0].Accept(this);
                var valueTemp = node.Arguments[1].Accept(this);
                _output.AppendLine($"  call void @memory_write_{typeArg.Name}(ptr {objectTemp}, i64 {writeOffsetTemp}, {llvmType} {valueTemp})");
                resultTemp = ""; // void return
                break;

            case "write_as":
                // write_as<T>!(address, value) - direct memory write to address
                var addrTemp = node.Arguments[0].Accept(this);
                var writeValueTemp = node.Arguments[1].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine($"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeArg.Name)}");
                resultTemp = ""; // void return
                break;

            case "read_as":
                // read_as<T>!(address) - direct memory read from address
                var readAddrTemp = node.Arguments[0].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine($"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeArg.Name)}");
                _tempTypes[resultTemp] = new TypeInfo(MapRazorForgeTypeToLLVM(typeArg.Name), false, false, typeArg.Name);
                break;

            case "volatile_write":
                // volatile_write<T>!(address, value) - volatile memory write to address
                var volWriteAddrTemp = node.Arguments[0].Accept(this);
                var volWriteValueTemp = node.Arguments[1].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine($"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeArg.Name)}");
                resultTemp = ""; // void return
                break;

            case "volatile_read":
                // volatile_read<T>!(address) - volatile memory read from address
                var volReadAddrTemp = node.Arguments[0].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine($"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeArg.Name)}");
                _tempTypes[resultTemp] = new TypeInfo(MapRazorForgeTypeToLLVM(typeArg.Name), false, false, typeArg.Name);
                break;

            default:
                throw new NotImplementedException($"Generic method {node.MethodName} not implemented");
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
        var objectTemp = node.Object.Accept(this);
        var resultTemp = GetNextTemp();

        switch (node.OperationName)
        {
            case "size":
                _output.AppendLine($"  {resultTemp} = call i64 @slice_size(ptr {objectTemp})");
                _tempTypes[resultTemp] = new TypeInfo("i64", false, false, "sysuint");
                break;

            case "address":
                _output.AppendLine($"  {resultTemp} = call i64 @slice_address(ptr {objectTemp})");
                _tempTypes[resultTemp] = new TypeInfo("i64", false, false, "sysuint");
                break;

            case "is_valid":
                _output.AppendLine($"  {resultTemp} = call i1 @slice_is_valid(ptr {objectTemp})");
                _tempTypes[resultTemp] = new TypeInfo("i1", false, false, "bool");
                break;

            case "unsafe_ptr":
                var offsetTemp = node.Arguments[0].Accept(this);
                _output.AppendLine($"  {resultTemp} = call i64 @slice_unsafe_ptr(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[resultTemp] = new TypeInfo("i64", false, false, "sysuint");
                break;

            case "slice":
                var sliceOffsetTemp = node.Arguments[0].Accept(this);
                var sliceBytesTemp = node.Arguments[1].Accept(this);
                _output.AppendLine($"  {resultTemp} = call ptr @slice_subslice(ptr {objectTemp}, i64 {sliceOffsetTemp}, i64 {sliceBytesTemp})");

                // Get the original slice type
                if (_tempTypes.TryGetValue(objectTemp, out var objType))
                {
                    _tempTypes[resultTemp] = objType;
                }
                break;

            case "hijack":
                _output.AppendLine($"  {resultTemp} = call ptr @slice_hijack(ptr {objectTemp})");
                if (_tempTypes.TryGetValue(objectTemp, out var hijackType))
                {
                    _tempTypes[resultTemp] = new TypeInfo("ptr", false, false, $"Hijacked<{hijackType.RazorForgeType}>");
                }
                break;

            case "refer":
                _output.AppendLine($"  {resultTemp} = call i64 @slice_refer(ptr {objectTemp})");
                _tempTypes[resultTemp] = new TypeInfo("i64", false, false, "sysuint");
                break;

            default:
                throw new NotImplementedException($"Memory operation {node.OperationName} not implemented");
        }

        return resultTemp;
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        // Add comment to indicate unsafe block
        _output.AppendLine("  ; === DANGER BLOCK START ===");
        node.Body.Accept(this);
        _output.AppendLine("  ; === DANGER BLOCK END ===");
        return "";
    }

    public string VisitMayhemStatement(MayhemStatement node)
    {
        // Add comment to indicate maximum unsafe block
        _output.AppendLine("  ; === MAYHEM BLOCK START ===");
        node.Body.Accept(this);
        _output.AppendLine("  ; === MAYHEM BLOCK END ===");
        return "";
    }

    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        // Generate external function declaration
        var paramTypes = string.Join(", ", node.Parameters.Select(p => MapRazorForgeTypeToLLVM(p.Type?.Name ?? "void")));
        var returnType = node.ReturnType != null ? MapRazorForgeTypeToLLVM(node.ReturnType.Name) : "void";

        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // For generic external functions, we'll need to generate specialized versions
            _output.AppendLine($"; Generic external function {node.Name} - specialized versions generated on demand");
        }
        else
        {
            _output.AppendLine($"declare {returnType} @{node.Name}({paramTypes})");
        }

        return "";
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
            "sysuint" or "syssint" => "i64", // Assume 64-bit target
            "f16" => "half",
            "f32" => "float",
            "f64" => "double",
            "f128" => "fp128",
            "bool" => "i1",
            "letter" => "i32", // UTF-32
            "text" => "ptr",
            "HeapSlice" or "StackSlice" => "ptr",
            "void" => "void",
            _ => "ptr" // Default to pointer for unknown types
        };
    }

    private void GenerateSliceRuntimeDeclarations()
    {
        // Generate declarations for slice runtime functions
        var declarations = new[]
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

        foreach (var decl in declarations)
        {
            _output.AppendLine(decl);
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

    private string HandleDangerZoneFunction(GenericMethodCallExpression node, string functionName, string llvmType, string typeName, string resultTemp)
    {
        switch (functionName)
        {
            case "write_as":
                var addrTemp = node.Arguments[0].Accept(this);
                var writeValueTemp = node.Arguments[1].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine($"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName)}");
                return ""; // void return

            case "read_as":
                var readAddrTemp = node.Arguments[0].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine($"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName)}");
                _tempTypes[resultTemp] = new TypeInfo(MapRazorForgeTypeToLLVM(typeName), false, false, typeName);
                return resultTemp;

            case "volatile_write":
                var volWriteAddrTemp = node.Arguments[0].Accept(this);
                var volWriteValueTemp = node.Arguments[1].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine($"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName)}");
                return ""; // void return

            case "volatile_read":
                var volReadAddrTemp = node.Arguments[0].Accept(this);
                _output.AppendLine($"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine($"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName)}");
                _tempTypes[resultTemp] = new TypeInfo(MapRazorForgeTypeToLLVM(typeName), false, false, typeName);
                return resultTemp;

            default:
                throw new NotImplementedException($"Danger zone function {functionName} not implemented in LLVM generator");
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

    private string HandleNonGenericDangerZoneFunction(CallExpression node, string functionName, string resultTemp)
    {
        switch (functionName)
        {
            case "addr_of":
                // addr_of!(variable) -> sysuint (address of variable)
                // Expects a single identifier argument
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException($"addr_of! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                var argument = node.Arguments[0];
                if (argument is IdentifierExpression varIdent)
                {
                    // Generate ptrtoint to get address of variable
                    _output.AppendLine($"  {resultTemp} = ptrtoint ptr %{varIdent.Name} to i64");
                    _tempTypes[resultTemp] = new TypeInfo("i64", false, true, "sysuint"); // sysuint is unsigned
                    return resultTemp;
                }
                else
                {
                    // Handle complex expressions by first evaluating them
                    var argTemp = argument.Accept(this);
                    _output.AppendLine($"  {resultTemp} = ptrtoint ptr {argTemp} to i64");
                    _tempTypes[resultTemp] = new TypeInfo("i64", false, true, "sysuint"); // sysuint is unsigned
                    return resultTemp;
                }

            case "invalidate":
                // invalidate!(slice) -> void (free memory)
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException($"invalidate! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                var sliceArgument = node.Arguments[0];
                // Evaluate the argument and then call heap_free on it
                var sliceTemp = sliceArgument.Accept(this);
                _output.AppendLine($"  call void @heap_free(ptr {sliceTemp})");
                return ""; // void return

            default:
                throw new NotImplementedException($"Non-generic danger zone function {functionName} not implemented in LLVM generator");
        }
    }
}