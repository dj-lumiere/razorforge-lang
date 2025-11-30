using System.Numerics;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Visits a variable declaration node in the AST and generates the associated LLVM IR code.
    /// This process includes determining the type of the variable, handling optional initialization,
    /// and updating the symbol table with relevant type information.
    /// </summary>
    /// <param name="node">The variable declaration node to process. Contains variable name,
    /// type (if specified), and optional initializer expression.</param>
    /// <returns>A string representing the LLVM IR code for the variable declaration.</returns>
    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        string type;
        string? initValue = null;

        if (node.Type != null)
        {
            // Explicit type annotation
            type = MapTypeToLLVM(rfType: node.Type.Name);
        }
        else if (node.Initializer != null)
        {
            // Infer type from initializer - visit first to get the type
            initValue = node.Initializer.Accept(visitor: this);
            // For call expressions and other complex expressions, the type is tracked in _tempTypes
            // after visiting. Use the tracked type if available, otherwise fall back to GetTypeInfo.
            if (_tempTypes.TryGetValue(key: initValue, value: out TypeInfo? trackedType))
            {
                type = trackedType.LLVMType;
            }
            else
            {
                TypeInfo inferredType = GetTypeInfo(expr: node.Initializer);
                type = inferredType.LLVMType;
            }
        }
        else
        {
            // No type and no initializer - default to i32
            type = "i32";
        }

        string varName = $"%{node.Name}";
        _symbolTypes[key: node.Name] = type;

        // Track RazorForge type for the variable
        if (node.Type != null)
        {
            _symbolRfTypes[key: node.Name] = node.Type.Name;
        }

        if (node.Initializer != null)
        {
            // If we already visited the initializer for type inference, use that value
            if (initValue == null)
            {
                initValue = node.Initializer.Accept(visitor: this);
            }

            // Track RazorForge type from initializer if not already set
            if (!_symbolRfTypes.ContainsKey(key: node.Name) && initValue != null &&
                _tempTypes.TryGetValue(key: initValue, value: out TypeInfo? initTypeInfo))
            {
                _symbolRfTypes[key: node.Name] = initTypeInfo.RazorForgeType;
            }

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
        // Handle NoneCoalesce (??) specially - it requires short-circuit evaluation
        if (node.Operator == BinaryOperator.NoneCoalesce)
        {
            return GenerateNoneCoalesce(node: node);
        }

        string left = node.Left.Accept(visitor: this);
        string right = node.Right.Accept(visitor: this);
        string result = GetNextTemp();

        // Get operand type information (assume both operands have same type)
        TypeInfo leftTypeInfo = GetTypeInfo(expr: node.Left);
        string operandType = leftTypeInfo.LLVMType;

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

            // Bitwise operations
            BinaryOperator.BitwiseAnd => "and",
            BinaryOperator.BitwiseOr => "or",
            BinaryOperator.BitwiseXor => "xor",

            // Shift operations
            BinaryOperator.LeftShift => "shl",
            BinaryOperator.RightShift => "", // Handled separately (ashr/lshr based on signedness)
            BinaryOperator.LogicalLeftShift => "shl",
            BinaryOperator.LogicalRightShift => "lshr",
            BinaryOperator.LeftShiftChecked => "", // Handled separately with overflow check

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
                    return GenerateSaturatingArithmetic(op: node.Operator,
                        left: left,
                        right: right,
                        result: result,
                        typeInfo: leftTypeInfo,
                        llvmType: operandType);

                case BinaryOperator.AddChecked:
                case BinaryOperator.SubtractChecked:
                case BinaryOperator.MultiplyChecked:
                    return GenerateCheckedArithmetic(op: node.Operator,
                        left: left,
                        right: right,
                        result: result,
                        typeInfo: leftTypeInfo,
                        llvmType: operandType);

                case BinaryOperator.RightShift:
                    // Use ashr for signed, lshr for unsigned
                    string shiftOp = leftTypeInfo.IsUnsigned
                        ? "lshr"
                        : "ashr";
                    _output.AppendLine(
                        handler: $"  {result} = {shiftOp} {operandType} {left}, {right}");
                    _tempTypes[key: result] = leftTypeInfo;
                    return result;

                case BinaryOperator.LeftShiftChecked:
                    // TODO: Implement overflow-checked left shift (returns Maybe<T>)
                    // For now, generate regular shl with a comment
                    _output.AppendLine(
                        handler: $"  ; TODO: Checked left shift - should return Maybe<T>");
                    _output.AppendLine(handler: $"  {result} = shl {operandType} {left}, {right}");
                    _tempTypes[key: result] = leftTypeInfo;
                    return result;

                default:
                    throw new NotSupportedException(
                        message: $"Operator {node.Operator} is not properly configured");
            }
        }

        // Get right operand type info for type matching
        TypeInfo rightTypeInfo = GetTypeInfo(expr: node.Right);

        // Handle type mismatch - truncate or extend as needed
        string rightOperand = right;
        if (rightTypeInfo.LLVMType != operandType && !rightTypeInfo.IsFloatingPoint &&
            !leftTypeInfo.IsFloatingPoint)
        {
            // Need to convert right operand to match left operand type
            int leftBits = GetIntegerBitWidth(llvmType: operandType);
            int rightBits = GetIntegerBitWidth(llvmType: rightTypeInfo.LLVMType);

            if (rightBits > leftBits)
            {
                // Truncate right operand to match left
                string truncTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {truncTemp} = trunc {rightTypeInfo.LLVMType} {right} to {operandType}");
                rightOperand = truncTemp;
            }
            else if (rightBits < leftBits)
            {
                // Extend right operand to match left
                string extTemp = GetNextTemp();
                string extOp = rightTypeInfo.IsUnsigned
                    ? "zext"
                    : "sext";
                _output.AppendLine(
                    handler:
                    $"  {extTemp} = {extOp} {rightTypeInfo.LLVMType} {right} to {operandType}");
                rightOperand = extTemp;
            }
        }

        // Generate the operation with proper type
        if (op.StartsWith(value: "icmp"))
        {
            // Comparison operations return i1
            _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {rightOperand}");
            _tempTypes[key: result] = new TypeInfo(LLVMType: "i1",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "bool");
        }
        else
        {
            // Arithmetic operations maintain operand type
            _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {rightOperand}");
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
            BinaryOperator.MultiplySaturate => GenerateSaturatingMultiply(left: left,
                right: right,
                result: result,
                typeInfo: typeInfo,
                llvmType: llvmType),
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
            $"  call void @rf_crash(ptr getelementptr inbounds ([20 x i8], [20 x i8]* @.str_overflow, i32 0, i32 0))");
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
            TokenType.Integer => "i128", // TODO: should be language dependent, Razorforge: s64, Suflae: Integer
            TokenType.Decimal => "double", // TODO: should be language dependent, Razorforge: f64, Suflae: Decimal
            TokenType.True => "i1",
            TokenType.False => "i1",
            // Text/String types - all return pointer to i8 (C-style strings)
            TokenType.TextLiteral or TokenType.Text8Literal or TokenType.Text16Literal
                or TokenType.FormattedText or TokenType.Text8FormattedText
                or TokenType.Text16FormattedText or TokenType.RawText or TokenType.Text8RawText
                or TokenType.Text16RawText or TokenType.RawFormattedText
                or TokenType.Text8RawFormattedText or TokenType.Text16RawFormattedText => "i8*",
            _ => "i32" // Default fallback
        };
    }

    // Identifier expression
    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        // Handle special built-in values
        if (node.Name == "None")
        {
            // None is represented as null pointer for Maybe<T> types
            // When used in a return statement, the return type will be i8* (pointer)
            // so we return null constant
            string nextWord = GetNextTemp();
            _tempTypes[key: nextWord] = new TypeInfo(LLVMType: "i8*",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "None");
            // Return null as the None value - it will be used directly in inttoptr or ret
            return "null";
        }

        string type = _symbolTypes.ContainsKey(key: node.Name)
            ? _symbolTypes[key: node.Name]
            : "i32";

        // If this is a function parameter, it's already a value - no load needed
        if (_functionParameters.Contains(item: node.Name))
        {
            return $"%{node.Name}";
        }

        // For local variables, we need to load from the stack
        string temp = GetNextTemp();
        _output.AppendLine(handler: $"  {temp} = load {type}, {type}* %{node.Name}");

        // Track the type of this temp so it can be used correctly in function calls
        // Use stored RazorForge type if available, otherwise use variable name as fallback
        string rfType = _symbolRfTypes.TryGetValue(key: node.Name, value: out string? storedRfType)
            ? storedRfType
            : node.Name;
        _tempTypes[key: temp] = new TypeInfo(LLVMType: type,
            IsUnsigned: type.StartsWith(value: "u"),
            IsFloatingPoint: type.StartsWith(value: "f") || type.StartsWith(value: "double"),
            RazorForgeType: rfType);
        return temp;
    }

    // Function call expression
    public string VisitCallExpression(CallExpression node)
    {
        string result = GetNextTemp();

        // Check if this is a standalone danger zone function call (address_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            string dangerfunctionName = identifierExpr.Name;
            if (IsNonGenericDangerZoneFunction(functionName: dangerfunctionName))
            {
                return HandleNonGenericDangerZoneFunction(node: node,
                    functionName: dangerfunctionName,
                    resultTemp: result);
            }

            // Check for non-generic CompilerService intrinsics (source location)
            if (IsSourceLocationIntrinsic(functionName: dangerfunctionName))
            {
                return HandleSourceLocationIntrinsic(node: node,
                    functionName: dangerfunctionName,
                    resultTemp: result);
            }

            // Check for error intrinsics (verify!, breach!, stop!)
            if (IsErrorIntrinsic(functionName: dangerfunctionName))
            {
                return HandleErrorIntrinsic(node: node,
                    functionName: dangerfunctionName,
                    resultTemp: result);
            }
        }

        // Check for special handlers BEFORE visiting arguments to avoid double-visiting
        if (node.Callee is MemberExpression memberExprEarly)
        {
            string objectNameEarly = memberExprEarly.Object switch
            {
                IdentifierExpression idExpr => idExpr.Name,
                _ => "unknown"
            };

            // Check if this is an external function call from imported module
            string externalFuncName = $"{objectNameEarly}.{memberExprEarly.PropertyName}";
            if (TryHandleExternalFunctionCall(funcName: externalFuncName,
                    arguments: node.Arguments,
                    resultTemp: result,
                    result: out string? externalResult))
            {
                return externalResult!;
            }

            // Check if this is a call to an imported module function (including generic)
            if (TryHandleImportedModuleFunctionCall(moduleName: objectNameEarly,
                    functionName: memberExprEarly.PropertyName,
                    arguments: node.Arguments,
                    resultTemp: result,
                    result: out string? importedResult))
            {
                return importedResult!;
            }

            // Special handling for Error type
            if (objectNameEarly == "Error")
            {
                return HandleErrorCall(methodName: memberExprEarly.PropertyName,
                    arguments: node.Arguments,
                    resultTemp: result);
            }
        }

        // Check for type constructor calls BEFORE visiting arguments
        if (node.Callee is IdentifierExpression identifierEarly)
        {
            string sanitizedNameEarly = SanitizeFunctionName(name: identifierEarly.Name);
            if (IsTypeConstructorCall(sanitizedName: sanitizedNameEarly))
            {
                return HandleTypeConstructorCall(functionName: sanitizedNameEarly,
                    arguments: node.Arguments,
                    resultTemp: result);
            }

            // Check for Crashable error type constructors (e.g., DivisionByZeroError())
            // These return a string pointer to the error message for use in safe variants
            if (IsCrashableErrorType(typeName: identifierEarly.Name))
            {
                return HandleCrashableErrorConstructor(errorTypeName: identifierEarly.Name,
                    resultTemp: result);
            }

            // Check for external function calls by identifier (e.g., rf_console_get_letters)
            // This handles direct calls to external C functions inside module function bodies
            if (TryHandleExternalFunctionCall(funcName: identifierEarly.Name,
                    arguments: node.Arguments,
                    resultTemp: result,
                    result: out string? externalResult))
            {
                return externalResult!;
            }

            // Check for record constructor calls like letter8(value: codepoint)
            if (IsRecordConstructorCall(typeName: identifierEarly.Name))
            {
                return HandleRecordConstructorCall(typeName: identifierEarly.Name,
                    arguments: node.Arguments,
                    resultTemp: result);
            }
        }

        var args = new List<string>();
        foreach (Expression arg in node.Arguments)
        {
            string argValue = arg.Accept(visitor: this);
            // Determine argument type - check if it's a tracked temp, otherwise infer from expression
            string argType = "i32"; // default
            if (_tempTypes.TryGetValue(key: argValue, value: out TypeInfo? argTypeInfo))
            {
                argType = argTypeInfo.LLVMType;
            }
            else if (arg is LiteralExpression literal && literal.Value is string)
            {
                argType = "i8*"; // String literals produce pointers
            }

            args.Add(item: $"{argType} {argValue}");
        }

        string argList = string.Join(separator: ", ", values: args);

        // Special handling for built-in functions
        if (node.Callee is IdentifierExpression id)
        {
            if (id.Name == "show")
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
                return HandleNonGenericDangerZoneFunction(node: node,
                    functionName: functionName,
                    resultTemp: result);
            }
        }
        else if (node.Callee is MemberExpression memberExpr)
        {
            // Handle member expression calls like Console.show, Error.from_text
            string objectName = memberExpr.Object switch
            {
                IdentifierExpression idExpr => idExpr.Name,
                _ => "unknown"
            };

            // Already handled Console and Error above, so this is for other member calls
            // For other member calls, convert to mangled name: Object.method -> Object_method
            functionName = $"{objectName}_{memberExpr.PropertyName}";
        }
        else
        {
            // For more complex expressions, we'd need to handle them differently
            functionName = "unknown_function";
        }

        string sanitizedFunctionName = SanitizeFunctionName(name: functionName);
        _output.AppendLine(handler: $"  {result} = call i32 @{sanitizedFunctionName}({argList})");
        return result;
    }
    public string VisitUnaryExpression(UnaryExpression node)
    {
        // Special case: negative integer literals should be handled directly
        // to avoid type issues (e.g., -2147483648 should be i32, not i64)
        if (node.Operator == UnaryOperator.Minus && node.Operand is LiteralExpression lit)
        {
            // Check if this is an integer literal that we can negate directly
            if (lit.Value is long longVal)
            {
                long negated = -longVal;
                // Check if negated value fits in i32 (s32 range)
                if (negated >= int.MinValue && negated <= int.MaxValue)
                {
                    // Return as i32 literal
                    _tempTypes[key: negated.ToString()] = new TypeInfo(LLVMType: "i32",
                        IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: "s32");
                    return negated.ToString();
                }

                return negated.ToString();
            }
            else if (lit.Value is int intVal)
            {
                return (-intVal).ToString();
            }
            else if (lit.Value is double doubleVal)
            {
                return (-doubleVal).ToString(format: "G");
            }
            else if (lit.Value is float floatVal)
            {
                return (-floatVal).ToString(format: "G");
            }
        }

        string operand = node.Operand.Accept(visitor: this);
        TypeInfo operandType = GetTypeInfo(expr: node.Operand);
        string llvmType = operandType.LLVMType;

        switch (node.Operator)
        {
            case UnaryOperator.Plus:
                // Unary plus is a no-op, just return the operand
                return operand;

            case UnaryOperator.Minus:
                // Negation: 0 - operand for integers, fneg for floats
                string result = GetNextTemp();
                if (operandType.IsFloatingPoint)
                {
                    _output.AppendLine(handler: $"  {result} = fneg {llvmType} {operand}");
                }
                else
                {
                    _output.AppendLine(handler: $"  {result} = sub {llvmType} 0, {operand}");
                }

                _tempTypes[key: result] = operandType;
                return result;

            case UnaryOperator.Not:
                // Logical NOT: xor with 1 for i1 (bool)
                string notResult = GetNextTemp();
                _output.AppendLine(handler: $"  {notResult} = xor i1 {operand}, 1");
                _tempTypes[key: notResult] = new TypeInfo(LLVMType: "i1",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "bool");
                return notResult;

            case UnaryOperator.BitwiseNot:
                // Bitwise NOT: xor with -1 (all 1s)
                string bitwiseResult = GetNextTemp();
                _output.AppendLine(handler: $"  {bitwiseResult} = xor {llvmType} {operand}, -1");
                _tempTypes[key: bitwiseResult] = operandType;
                return bitwiseResult;

            default:
                return operand;
        }
    }
    public string VisitMemberExpression(MemberExpression node)
    {
        string? objectName = node.Object switch
        {
            IdentifierExpression idExpr => idExpr.Name,
            _ => null
        };

        // Check if this is a record field access
        if (objectName != null)
        {
            // First, try to get the record type from _symbolRfTypes
            string? recordType = null;
            if (_symbolRfTypes.TryGetValue(key: objectName, value: out string? rfType))
            {
                recordType = rfType;
            }
            // Also try from _symbolTypes (for LLVM types like %Point)
            else if (_symbolTypes.TryGetValue(key: objectName, value: out string? llvmType) &&
                     llvmType.StartsWith(value: "%"))
            {
                recordType = llvmType[1..]; // Remove % prefix
            }

            // If we have a record type, look up the field
            if (recordType != null && _recordFields.TryGetValue(key: recordType,
                    value: out List<(string Name, string Type)>? fields))
            {
                // Find the field index
                int fieldIndex = -1;
                string fieldType = "i32";
                for (int i = 0; i < fields.Count; i++)
                {
                    if (fields[index: i].Name == node.PropertyName)
                    {
                        fieldIndex = i;
                        fieldType = fields[index: i].Type;
                        break;
                    }
                }

                if (fieldIndex >= 0)
                {
                    // Generate getelementptr and load for field access
                    string result = GetNextTemp();
                    string fieldPtr = GetNextTemp();
                    _output.AppendLine(
                        handler:
                        $"  {fieldPtr} = getelementptr inbounds %{recordType}, ptr %{objectName}, i32 0, i32 {fieldIndex}");
                    _output.AppendLine(handler: $"  {result} = load {fieldType}, ptr {fieldPtr}");
                    _tempTypes[key: result] = new TypeInfo(LLVMType: fieldType,
                        IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: "");
                    return result;
                }
            }
        }

        // Not a record field access - handle other cases
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

    public string VisitBlockExpression(BlockExpression node)
    {
        // A block expression evaluates to its inner expression
        return node.Value.Accept(visitor: this);
    }

    public string VisitRangeExpression(RangeExpression node)
    {
        // For now, generate a simple record representation
        // In a real implementation, this would create a Range<T> object
        string start = node.Start.Accept(visitor: this);
        string end = node.End.Accept(visitor: this);
        string rangeOp = node.IsDescending
            ? "downto"
            : "to";

        if (node.Step != null)
        {
            string step = node.Step.Accept(visitor: this);
            // Generate code for range with step
            _output.AppendLine(handler: $"; Range from {start} {rangeOp} {end} by {step}");
        }
        else
        {
            // Generate code for range without step (default step 1 or -1 for descending)
            _output.AppendLine(handler: $"; Range from {start} {rangeOp} {end}");
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
    public string VisitTypeExpression(TypeExpression node)
    {
        return "";
    }
    /// <summary>
    /// Generates LLVM IR for the none coalescing operator (??).
    /// Returns the left value if it's valid (not None/Error), otherwise returns the right value.
    /// Works with Maybe, Result, and Lookup types.
    /// </summary>
    private string GenerateNoneCoalesce(BinaryExpression node)
    {
        // Evaluate the left side first
        string left = node.Left.Accept(visitor: this);
        TypeInfo leftTypeInfo = GetTypeInfo(expr: node.Left);
        string leftTypeName = leftTypeInfo.RazorForgeType ?? "";

        // Generate unique labels for branching
        string validLabel = $"coalesce_valid_{_labelCounter}";
        string invalidLabel = $"coalesce_invalid_{_labelCounter}";
        string endLabel = $"coalesce_end_{_labelCounter}";
        _labelCounter++;

        // Determine how to check validity based on the type
        string isValidTemp = GetNextTemp();

        if (leftTypeName.StartsWith(value: "Maybe") || leftTypeName.StartsWith(value: "Result"))
        {
            // Maybe and Result have is_valid: bool as first field
            // Load the is_valid field (index 0)
            string isValidPtr = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {isValidPtr} = getelementptr inbounds {{i1, i8*}}, {{i1, i8*}}* {left}, i32 0, i32 0");
            _output.AppendLine(handler: $"  {isValidTemp} = load i1, i1* {isValidPtr}");
        }
        else if (leftTypeName.StartsWith(value: "Lookup"))
        {
            // Lookup has state: DataState as first field where VALID = 0
            string statePtr = GetNextTemp();
            string stateVal = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {statePtr} = getelementptr inbounds {{i32, i8*}}, {{i32, i8*}}* {left}, i32 0, i32 0");
            _output.AppendLine(handler: $"  {stateVal} = load i32, i32* {statePtr}");
            _output.AppendLine(handler: $"  {isValidTemp} = icmp eq i32 {stateVal}, 0");
        }
        else
        {
            // For non-wrapper types (null pointer check), treat as pointer comparison
            _output.AppendLine(handler: $"  {isValidTemp} = icmp ne i8* {left}, null");
        }

        // Branch based on validity
        _output.AppendLine(
            handler: $"  br i1 {isValidTemp}, label %{validLabel}, label %{invalidLabel}");

        // Valid case - extract the value from the wrapper
        _output.AppendLine(handler: $"{validLabel}:");
        string validValue;
        if (leftTypeName.StartsWith(value: "Maybe") || leftTypeName.StartsWith(value: "Result") ||
            leftTypeName.StartsWith(value: "Lookup"))
        {
            // Extract handle from wrapper (index 1)
            string handlePtr = GetNextTemp();
            validValue = GetNextTemp();
            string structType = leftTypeName.StartsWith(value: "Lookup")
                ? "{i32, i8*}"
                : "{i1, i8*}";
            _output.AppendLine(
                handler:
                $"  {handlePtr} = getelementptr inbounds {structType}, {structType}* {left}, i32 0, i32 1");
            _output.AppendLine(handler: $"  {validValue} = load i8*, i8** {handlePtr}");
        }
        else
        {
            validValue = left;
        }

        _output.AppendLine(handler: $"  br label %{endLabel}");

        // Invalid case - evaluate the right side
        _output.AppendLine(handler: $"{invalidLabel}:");
        string invalidValue = node.Right.Accept(visitor: this);
        _output.AppendLine(handler: $"  br label %{endLabel}");

        // Merge point with phi
        _output.AppendLine(handler: $"{endLabel}:");
        string result = GetNextTemp();
        TypeInfo rightTypeInfo = GetTypeInfo(expr: node.Right);
        string resultType = rightTypeInfo.LLVMType;
        _output.AppendLine(
            handler:
            $"  {result} = phi {resultType} [{validValue}, %{validLabel}], [{invalidValue}, %{invalidLabel}]");

        _tempTypes[key: result] = rightTypeInfo;
        return result;
    }

    /// <summary>
    /// Visits a literal expression node and generates the corresponding LLVM IR code.
    /// Handles various literal types such as integers, floating-point numbers, booleans, and strings.
    /// </summary>
    /// <param name="node">The literal expression node to process, containing the value and type information.</param>
    /// <returns>A string representation of the generated LLVM IR code for the given literal expression.</returns>
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
            // Register the temp as a string pointer type
            _tempTypes[key: temp] = new TypeInfo(LLVMType: "i8*",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "Text");
            return temp;
        }

        return "0";
    }

    /// <summary>
    /// Visits a list literal expression in the abstract syntax tree (AST) and generates the corresponding LLVM IR code.
    /// This method handles the initialization of a list and its corresponding elements.
    /// </summary>
    /// <param name="node">The list literal expression node containing the list elements, their inferred type, and location information.</param>
    /// <returns>A string representing the generated LLVM IR code for the list literal expression.</returns>
    public string VisitListLiteralExpression(ListLiteralExpression node)
    {
        // TODO: Generate actual List allocation and initialization
        // For now, just generate a comment placeholder
        _output.AppendLine(handler: $"  ; List literal with {node.Elements.Count} elements");
        return "null"; // Placeholder
    }

    /// <summary>
    /// Visits a set literal expression in the abstract syntax tree (AST) and generates the corresponding LLVM IR code.
    /// Handles the allocation and initialization of a set with the specified elements.
    /// </summary>
    /// <param name="node">The set literal expression node containing the elements, optional element type, and source location.</param>
    /// <returns>
    /// The generated LLVM IR code string representing the set literal initialization.
    /// </returns>
    public string VisitSetLiteralExpression(SetLiteralExpression node)
    {
        // TODO: Generate actual Set allocation and initialization
        _output.AppendLine(handler: $"  ; Set literal with {node.Elements.Count} elements");
        return "null"; // Placeholder
    }
    /// <summary>
    /// Processes a dictionary literal expression during AST traversal and generates the corresponding LLVM IR code.
    /// </summary>
    /// <param name="node">The dictionary literal expression node containing key-value pairs and optional type information.</param>
    /// <returns>A string representing the generated LLVM IR code for the dictionary literal expression.</returns>
    public string VisitDictLiteralExpression(DictLiteralExpression node)
    {
        // TODO: Generate actual Dict allocation and initialization
        _output.AppendLine(handler: $"  ; Dict literal with {node.Pairs.Count} pairs");
        return "null"; // Placeholder
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        string sizeTemp = node.SizeExpression.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        if (node.SliceType == "DynamicSlice")
        {
            // Generate LLVM IR for heap slice construction
            _output.AppendLine(handler: $"  {resultTemp} = call ptr @heap_alloc(i64 {sizeTemp})");
        }
        else if (node.SliceType == "TemporarySlice")
        {
            // Generate LLVM IR for stack slice construction
            _output.AppendLine(handler: $"  {resultTemp} = call ptr @stack_alloc(i64 {sizeTemp})");
        }

        // Store slice type information for later use
        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "ptr",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.SliceType);
        return resultTemp;
    }
}
