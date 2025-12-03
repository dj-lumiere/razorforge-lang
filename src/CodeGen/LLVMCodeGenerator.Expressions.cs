using System.Numerics;
using Compilers.Shared.AST;
using Compilers.Shared.Errors;
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
        _currentLocation = node.Location;
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
            if (_tempTypes.TryGetValue(key: initValue, value: out LLVMTypeInfo? trackedType))
            {
                type = trackedType.LLVMType;
            }
            else
            {
                LLVMTypeInfo inferredType = GetTypeInfo(expr: node.Initializer);
                type = inferredType.LLVMType;
            }
        }
        else
        {
            // No type and no initializer - this is an error
            throw CodeGenError.TypeResolutionFailed(
                typeName: node.Name,
                context: "variable declaration must have either a type annotation or an initializer",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }

        // Generate unique variable name to avoid conflicts in different scopes (e.g., if-else branches)
        string uniqueName = GetUniqueVarName(baseName: node.Name);
        string varName = $"%{uniqueName}";
        _symbolTypes[key: node.Name] = type;
        // Map original name to unique name for lookups
        _symbolTypes[key: $"__varptr_{node.Name}"] = uniqueName;

        // Track RazorForge type for the variable
        if (node.Type != null)
        {
            _symbolRfTypes[key: node.Name] = node.Type.Name;
        }

        if (node.Initializer != null)
        {
            // If we already visited the initializer for type inference, use that value
            initValue ??= node.Initializer.Accept(visitor: this);

            // Track RazorForge type from initializer if not already set
            if (!_symbolRfTypes.ContainsKey(key: node.Name) &&
                _tempTypes.TryGetValue(key: initValue, value: out LLVMTypeInfo? initTypeInfo))
            {
                _symbolRfTypes[key: node.Name] = initTypeInfo.RazorForgeType;
            }

            _output.AppendLine(handler: $"  {varName} = alloca {type}");
            _output.AppendLine(handler: $"  store {type} {initValue}, ptr {varName}");
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
        _currentLocation = node.Location;
        // Handle NoneCoalesce (??) specially - it requires short-circuit evaluation
        if (node.Operator == BinaryOperator.NoneCoalesce)
        {
            return GenerateNoneCoalesce(node: node);
        }

        // Handle logical And/Or with short-circuit evaluation
        if (node.Operator == BinaryOperator.And)
        {
            return GenerateLogicalAnd(node: node);
        }

        if (node.Operator == BinaryOperator.Or)
        {
            return GenerateLogicalOr(node: node);
        }

        string left = node.Left.Accept(visitor: this);
        string right = node.Right.Accept(visitor: this);
        string result = GetNextTemp();

        // Get operand type information from the generated temp variable
        // This correctly handles CallExpressions that store their return type in _tempTypes
        // First try to get from the temp variable (preferred - has accurate type info from actual codegen)
        // Fall back to GetTypeInfo only if the temp isn't tracked (e.g., for literals)
        LLVMTypeInfo leftTypeInfo = GetValueTypeInfo(value: left);
        // If GetValueTypeInfo returned default i32 but the expression has ResolvedType, use that
        if (leftTypeInfo.LLVMType == "i32" && leftTypeInfo.RazorForgeType == "i32" && node.Left.ResolvedType != null)
        {
            leftTypeInfo = ConvertAstTypeInfoToLLVM(astType: node.Left.ResolvedType);
        }
        string operandType = leftTypeInfo.LLVMType;

        string op = node.Operator switch
        {
            // Regular arithmetic
            BinaryOperator.Add => "add",
            BinaryOperator.Subtract => "sub",
            BinaryOperator.Multiply => "mul",
            BinaryOperator.FloorDivide => GetIntegerDivisionOp(
                typeInfo: leftTypeInfo), // sdiv/udiv based on signed/unsigned
            BinaryOperator.TrueDivide => "fdiv",  // / only for floats (semantic analysis rejects for integers)
            BinaryOperator.Modulo => GetModuloOp(
                typeInfo: leftTypeInfo), // srem/urem for integers, frem for floats

            // Overflow-handling variants (for now, use LLVM intrinsics will be added later)
            BinaryOperator.AddWrap => "add", // Wrapping is default behavior
            BinaryOperator.SubtractWrap => "sub",
            BinaryOperator.MultiplyWrap => "mul",

            BinaryOperator.AddSaturate => "", // Handled separately with intrinsics
            BinaryOperator.SubtractSaturate => "", // Handled separately with intrinsics
            BinaryOperator.MultiplySaturate => "", // Handled separately with intrinsics

            BinaryOperator.AddChecked => "", // Handled separately with overflow intrinsics
            BinaryOperator.SubtractChecked => "", // Handled separately with overflow intrinsics
            BinaryOperator.MultiplyChecked => "", // Handled separately with overflow intrinsics


            // Comparisons
            BinaryOperator.Less => "icmp slt",
            BinaryOperator.LessEqual => "icmp sle",
            BinaryOperator.Greater => "icmp sgt",
            BinaryOperator.GreaterEqual => "icmp sge",
            BinaryOperator.Equal => "icmp eq",
            BinaryOperator.NotEqual => "icmp ne",

            // Bitwise operations
            BinaryOperator.BitwiseAnd => "and",
            BinaryOperator.BitwiseOr => "or",
            BinaryOperator.BitwiseXor => "xor",

            // Shift operations
            BinaryOperator.ArithmeticLeftShift => "shl",
            BinaryOperator.ArithmeticRightShift => "", // Handled separately (ashr/lshr based on signedness)
            BinaryOperator.LogicalLeftShift => "shl",
            BinaryOperator.LogicalRightShift => "lshr",
            BinaryOperator.ArithmeticLeftShiftChecked => "", // Handled separately with overflow check

            _ => throw new NotSupportedException(
                message: $"Binary operator '{node.Operator}' is not supported in code generation")
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

                case BinaryOperator.ArithmeticRightShift:
                    // Use ashr for signed, lshr for unsigned
                    string shiftOp = leftTypeInfo.IsUnsigned
                        ? "lshr"
                        : "ashr";
                    _output.AppendLine(
                        handler: $"  {result} = {shiftOp} {operandType} {left}, {right}");
                    _tempTypes[key: result] = leftTypeInfo;
                    return result;

                case BinaryOperator.ArithmeticLeftShiftChecked:
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

        // Get right operand type info for type matching (use same approach as left operand)
        LLVMTypeInfo rightTypeInfo = GetValueTypeInfo(value: right);
        if (rightTypeInfo.LLVMType == "i32" && rightTypeInfo.RazorForgeType == "i32" && node.Right.ResolvedType != null)
        {
            rightTypeInfo = ConvertAstTypeInfoToLLVM(astType: node.Right.ResolvedType);
        }

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
            _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: "i1",
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
        string result, LLVMTypeInfo typeInfo, string llvmType)
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
        LLVMTypeInfo typeInfo, string llvmType)
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
        string result, LLVMTypeInfo typeInfo, string llvmType)
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
    private (string maxValue, string minValue) GetSaturationBounds(LLVMTypeInfo typeInfo,
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
                _ => throw CodeGenError.TypeResolutionFailed(
                    typeName: llvmType,
                    context: $"unsupported bit width {bits} for unsigned saturation bounds",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position)
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
                _ => throw CodeGenError.TypeResolutionFailed(
                    typeName: llvmType,
                    context: $"unsupported bit width {bits} for signed saturation bounds",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position)
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
            _ => throw CodeGenError.TypeResolutionFailed(
                typeName: tokenType.ToString(),
                context: "unknown token type in GetLLVMType",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position)
        };
    }

    // Identifier expression
    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        _currentLocation = node.Location;
        // Handle special built-in values
        if (node.Name == "None")
        {
            // None is represented as null pointer for Maybe<T> types
            // When used in a return statement, the return type will be i8* (pointer)
            // so we return null constant
            string nextWord = GetNextTemp();
            _tempTypes[key: nextWord] = new LLVMTypeInfo(LLVMType: "i8*",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "None");
            // Return null as the None value - it will be used directly in inttoptr or ret
            return "null";
        }

        if (!_symbolTypes.TryGetValue(key: node.Name, value: out string? type))
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: node.Name,
                context: "identifier not found in symbol table [VisitIdentifierExpression]",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }

        // If this is a function parameter, it's already a value - no load needed
        if (_functionParameters.Contains(item: node.Name))
        {
            // Track the type so function calls can use it
            string paramType = type;
            string paramValue = $"%{node.Name}";
            string paramRfType = _symbolRfTypes.TryGetValue(key: node.Name, value: out string? storedParamRfType)
                ? storedParamRfType
                : node.Name;
            _tempTypes[key: paramValue] = new LLVMTypeInfo(LLVMType: paramType,
                IsUnsigned: paramType.StartsWith(value: "u"),
                IsFloatingPoint: paramType.StartsWith(value: "f") || paramType.StartsWith(value: "double"),
                RazorForgeType: paramRfType);
            return paramValue;
        }

        // For global constants (presets), use @name instead of %name
        string temp = GetNextTemp();
        if (_globalConstants.Contains(item: node.Name))
        {
            _output.AppendLine(handler: $"  {temp} = load {type}, ptr @{node.Name}");
        }
        else
        {
            // For local variables, we need to load from the stack
            // Use unique name if available (for scoped variables like those in if-else branches)
            string varPtr = _symbolTypes.TryGetValue(key: $"__varptr_{node.Name}", value: out string? uniqueName)
                ? $"%{uniqueName}"
                : $"%{node.Name}";
            _output.AppendLine(handler: $"  {temp} = load {type}, ptr {varPtr}");
        }

        // Track the type of this temp so it can be used correctly in function calls
        // Use stored RazorForge type if available, otherwise use variable name as fallback
        string rfType = _symbolRfTypes.TryGetValue(key: node.Name, value: out string? storedRfType)
            ? storedRfType
            : node.Name;
        _tempTypes[key: temp] = new LLVMTypeInfo(LLVMType: type,
            IsUnsigned: type.StartsWith(value: "u"),
            IsFloatingPoint: type.StartsWith(value: "f") || type.StartsWith(value: "double"),
            RazorForgeType: rfType);
        return temp;
    }

    // Function call expression
    public string VisitCallExpression(CallExpression node)
    {
        _currentLocation = node.Location;
        string result = GetNextTemp();

        switch (node.Callee)
        {
            // Check if this is a standalone danger zone function call (address_of!, invalidate!)
            case IdentifierExpression identifierExpr:
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

                break;
            }
            // Check for special handlers BEFORE visiting arguments to avoid double-visiting
            case MemberExpression memberExprEarly:
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

                break;
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

            // Check for primitive type conversions like saddr(value), uaddr(value), s32(value), etc.
            if (IsPrimitiveTypeCast(typeName: identifierEarly.Name))
            {
                return HandlePrimitiveTypeCast(targetTypeName: identifierEarly.Name,
                    arguments: node.Arguments,
                    resultTemp: result);
            }
        }

        // Check for generic type constructor calls like Text<letter8>(ptr: ptr)
        if (node.Callee is GenericMemberExpression genericTypeExpr)
        {
            // Build the full generic type name like "Text<letter8>"
            string typeArgs = string.Join(separator: ", ", values: genericTypeExpr.TypeArguments.Select(selector: t => t.Name));
            string genericTypeName = $"{genericTypeExpr.MemberName}<{typeArgs}>";
            string sanitizedGenericName = SanitizeFunctionName(name: genericTypeName);

            // This is a generic type constructor call - needs to find and call __create__ method
            if (TryHandleGenericTypeConstructor(typeName: genericTypeName,
                    arguments: node.Arguments,
                    resultTemp: result,
                    result: out string? constructorResult))
            {
                return constructorResult!;
            }

            // Check for record constructor calls on generic types
            if (IsRecordConstructorCall(typeName: genericTypeName))
            {
                return HandleRecordConstructorCall(typeName: genericTypeName,
                    arguments: node.Arguments,
                    resultTemp: result);
            }
        }

        var args = new List<string>();
        foreach (Expression arg in node.Arguments)
        {
            string argValue = arg.Accept(visitor: this);
            // Determine argument type - check if it's a tracked temp, otherwise infer from expression
            string argType;
            if (_tempTypes.TryGetValue(key: argValue, value: out LLVMTypeInfo? argTypeInfo))
            {
                argType = argTypeInfo.LLVMType;
            }
            else if (arg is LiteralExpression literal && literal.Value is string)
            {
                argType = "i8*"; // String literals produce pointers
            }
            else
            {
                // Try to get type from the expression itself
                LLVMTypeInfo inferredType = GetTypeInfo(expr: arg);
                argType = inferredType.LLVMType;
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
            // Handle member expression calls like Console.show, Error.from_text, me.length()
            // Also handle static method calls like Text<letter8>.from_cstr(ptr)
            string objectName = memberExpr.Object switch
            {
                IdentifierExpression idExpr => idExpr.Name,
                TypeExpression typeExpr => typeExpr.Name,
                GenericMemberExpression genExpr => $"{genExpr.MemberName}<{string.Join(separator: ", ", values: genExpr.TypeArguments.Select(selector: t => t.Name))}>",
                _ => "unknown"
            };

            // Try to look up the method return type based on the object's type
            string? objectTypeName = null;
            if (memberExpr.Object is IdentifierExpression objIdExpr)
            {
                // Try to get the RazorForge type from our tracking
                _symbolRfTypes.TryGetValue(key: objIdExpr.Name, value: out objectTypeName);
            }
            else if (memberExpr.Object is TypeExpression typeExpr)
            {
                // Static method call on a type - use the type name directly
                objectTypeName = typeExpr.Name;
            }
            else if (memberExpr.Object is GenericMemberExpression genExpr)
            {
                // Static method call on a generic type like Text<letter8>.from_cstr
                objectTypeName = $"{genExpr.MemberName}<{string.Join(separator: ", ", values: genExpr.TypeArguments.Select(selector: t => t.Name))}>";
            }

            // For method calls, use the type name (if known) instead of the variable name
            // This converts me.length() to cstr.length(me) instead of me_length()
            string methodReceiverName = objectTypeName ?? objectName;
            functionName = $"{methodReceiverName}.{memberExpr.PropertyName}";

            // Try to find the method return type in loaded modules
            string methodReturnType = LookupMethodReturnType(
                objectTypeName: objectTypeName,
                methodName: memberExpr.PropertyName);

            string sanitizedFunctionName = SanitizeFunctionName(name: functionName);

            // For method calls on variables, we need to pass the object as the first argument
            string methodArgList;
            if (objectTypeName != null && objectTypeName != objectName)
            {
                // This is a method call on a typed variable - add the object as first argument
                string objectValue = memberExpr.Object.Accept(visitor: this);
                string objectLlvmType = _symbolTypes.TryGetValue(key: objectName, value: out string? storedType)
                    ? storedType
                    : MapTypeToLLVM(rfType: objectTypeName);
                methodArgList = args.Count > 0
                    ? $"{objectLlvmType} {objectValue}, {argList}"
                    : $"{objectLlvmType} {objectValue}";
            }
            else
            {
                methodArgList = argList;
            }

            _output.AppendLine(
                handler: $"  {result} = call {methodReturnType} @{sanitizedFunctionName}({methodArgList})");

            // Track the result type
            string rfReturnType = GetRazorForgeTypeFromLLVM(llvmType: methodReturnType);
            _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: methodReturnType,
                IsUnsigned: rfReturnType.StartsWith(value: "u"),
                IsFloatingPoint: methodReturnType.Contains(value: "float") ||
                                 methodReturnType.Contains(value: "double"),
                RazorForgeType: rfReturnType);
            return result;
        }
        else
        {
            // For more complex expressions, we'd need to handle them differently
            functionName = "unknown_function";
        }

        string defaultSanitizedFunctionName = SanitizeFunctionName(name: functionName);
        string defaultReturnType = LookupFunctionReturnType(functionName: defaultSanitizedFunctionName);
        _output.AppendLine(
            handler: $"  {result} = call {defaultReturnType} @{defaultSanitizedFunctionName}({argList})");
        return result;
    }
    public string VisitUnaryExpression(UnaryExpression node)
    {
        _currentLocation = node.Location;
        // Special case: negative integer literals should be handled directly
        // to avoid type issues (e.g., -2147483648 should be i32, not i64)
        if (node.Operator == UnaryOperator.Minus && node.Operand is LiteralExpression lit)
        {
            switch (lit.Value)
            {
                // Check if this is an integer literal that we can negate directly
                case long longVal:
                {
                    long negated = -longVal;
                    // Check if negated value fits in i32 (s32 range)
                    if (negated is < int.MinValue or > int.MaxValue)
                    {
                        return negated.ToString();
                    }

                    // Return as i32 literal
                    _tempTypes[key: negated.ToString()] = new LLVMTypeInfo(LLVMType: "i32",
                        IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: "s32");
                    return negated.ToString();

                }
                case int intVal:
                    return (-intVal).ToString();
                case double doubleVal:
                    return (-doubleVal).ToString(format: "G");
                case float floatVal:
                    return (-floatVal).ToString(format: "G");
            }
        }

        string operand = node.Operand.Accept(visitor: this);
        LLVMTypeInfo operandType = GetTypeInfo(expr: node.Operand);
        string llvmType = operandType.LLVMType;

        switch (node.Operator)
        {
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
                _tempTypes[key: notResult] = new LLVMTypeInfo(LLVMType: "i1",
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
        _currentLocation = node.Location;
        string? objectName = node.Object switch
        {
            IdentifierExpression idExpr => idExpr.Name,
            _ => null
        };

        // Check if this is a record field access
        if (objectName == null)
        {
            return "";
        }

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

        // Convert generic type names to mangled form for lookup
        // e.g., Range<BackIndex<uaddr>> -> Range_BackIndex_uaddr
        if (recordType != null && recordType.Contains(value: '<'))
        {
            recordType = recordType
                .Replace(oldValue: "<", newValue: "_")
                .Replace(oldValue: ">", newValue: "")
                .Replace(oldValue: ", ", newValue: "_")
                .Replace(oldValue: ",", newValue: "_");
        }

        // If we have a record type, look up the field
        if (recordType == null || !_recordFields.TryGetValue(key: recordType,
                value: out List<(string Name, string Type)>? fields))
        {
            return "";
        }

        // Find the field index
        int fieldIndex = -1;
        string? fieldType = null;
        for (int i = 0; i < fields.Count; i++)
        {
            if (fields[index: i].Name == node.PropertyName)
            {
                fieldIndex = i;
                fieldType = fields[index: i].Type;
                break;
            }
        }

        // Not a record field access - handle other cases
        if (fieldIndex < 0 || fieldType == null)
        {
            return "";
        }

        // Generate getelementptr and load for field access
        string result = GetNextTemp();
        string fieldPtr = GetNextTemp();

        // Check if objectName is a function parameter passed by value (struct type, not pointer)
        // In LLVM, getelementptr requires a pointer operand, so for value-type parameters
        // we need to allocate stack space and store the value first
        // If the type ends with *, it's already a pointer and can be used directly
        bool isValueParameter = _functionParameters.Contains(item: objectName) &&
                                _symbolTypes.TryGetValue(key: objectName, value: out string? paramLlvmType) &&
                                paramLlvmType.StartsWith(value: "%") &&
                                !paramLlvmType.EndsWith(value: "*");

        if (isValueParameter)
        {
            // Allocate stack space and store the struct value to get a pointer
            string stackPtr = GetNextTemp();
            _output.AppendLine(handler: $"  {stackPtr} = alloca %{recordType}");
            _output.AppendLine(handler: $"  store %{recordType} %{objectName}, ptr {stackPtr}");
            _output.AppendLine(
                handler:
                $"  {fieldPtr} = getelementptr inbounds %{recordType}, ptr {stackPtr}, i32 0, i32 {fieldIndex}");
        }
        else
        {
            // For local variables, use unique name if available
            string varPtr = _symbolTypes.TryGetValue(key: $"__varptr_{objectName}", value: out string? uniqueName)
                ? $"%{uniqueName}"
                : $"%{objectName}";
            _output.AppendLine(
                handler:
                $"  {fieldPtr} = getelementptr inbounds %{recordType}, ptr {varPtr}, i32 0, i32 {fieldIndex}");
        }

        _output.AppendLine(handler: $"  {result} = load {fieldType}, ptr {fieldPtr}");
        _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: fieldType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: "");
        return result;

    }
    public string VisitIndexExpression(IndexExpression node)
    {
        _currentLocation = node.Location;
        return "";
    }
    public string VisitConditionalExpression(ConditionalExpression node)
    {
        _currentLocation = node.Location;
        return "";
    }

    public string VisitBlockExpression(BlockExpression node)
    {
        _currentLocation = node.Location;
        // A block expression evaluates to its inner expression
        return node.Value.Accept(visitor: this);
    }

    public string VisitRangeExpression(RangeExpression node)
    {
        _currentLocation = node.Location;
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
        _currentLocation = node.Location;
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
                _ => throw new NotSupportedException(
                    message: $"Chained comparison operator '{node.Operators[index: i]}' is not supported")
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
        _currentLocation = node.Location;
        return "";
    }
    /// <summary>
    /// Generates LLVM IR for the none coalescing operator (??).
    /// Returns the left value if it's valid (not None/Error), otherwise returns the right value.
    /// Works with Maybe, Result, and Lookup types.
    /// </summary>
    /// <summary>
    /// Generates LLVM IR for logical AND with short-circuit evaluation.
    /// If the left operand is false, the right operand is not evaluated.
    /// </summary>
    private string GenerateLogicalAnd(BinaryExpression node)
    {
        string result = GetNextTemp();
        string evalRightLabel = $"and_eval_right_{_labelCounter}";
        string endLabel = $"and_end_{_labelCounter}";
        _labelCounter++;

        // Evaluate left operand
        string left = node.Left.Accept(visitor: this);

        // Get current block for phi node
        string leftBlock = GetCurrentBlockName();

        // Branch: if left is true, evaluate right; otherwise short-circuit to false
        _output.AppendLine(handler: $"  br i1 {left}, label %{evalRightLabel}, label %{endLabel}");

        // Evaluate right operand
        _output.AppendLine(handler: $"{evalRightLabel}:");
        string right = node.Right.Accept(visitor: this);
        string rightBlock = GetCurrentBlockName();
        _output.AppendLine(handler: $"  br label %{endLabel}");

        // Merge with phi node
        _output.AppendLine(handler: $"{endLabel}:");
        _output.AppendLine(handler: $"  {result} = phi i1 [ 0, %{leftBlock} ], [ {right}, %{rightBlock} ]");

        _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: "i1",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: "bool");

        return result;
    }

    /// <summary>
    /// Generates LLVM IR for logical OR with short-circuit evaluation.
    /// If the left operand is true, the right operand is not evaluated.
    /// </summary>
    private string GenerateLogicalOr(BinaryExpression node)
    {
        string result = GetNextTemp();
        string evalRightLabel = $"or_eval_right_{_labelCounter}";
        string endLabel = $"or_end_{_labelCounter}";
        _labelCounter++;

        // Evaluate left operand
        string left = node.Left.Accept(visitor: this);

        // Get current block for phi node
        string leftBlock = GetCurrentBlockName();

        // Branch: if left is false, evaluate right; otherwise short-circuit to true
        _output.AppendLine(handler: $"  br i1 {left}, label %{endLabel}, label %{evalRightLabel}");

        // Evaluate right operand
        _output.AppendLine(handler: $"{evalRightLabel}:");
        string right = node.Right.Accept(visitor: this);
        string rightBlock = GetCurrentBlockName();
        _output.AppendLine(handler: $"  br label %{endLabel}");

        // Merge with phi node
        _output.AppendLine(handler: $"{endLabel}:");
        _output.AppendLine(handler: $"  {result} = phi i1 [ 1, %{leftBlock} ], [ {right}, %{rightBlock} ]");

        _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: "i1",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: "bool");

        return result;
    }

    /// <summary>
    /// Gets the name of the current basic block for phi node references.
    /// </summary>
    private string GetCurrentBlockName()
    {
        // Look back through the output to find the last label
        string output = _output.ToString();
        int lastNewline = output.LastIndexOf('\n');
        while (lastNewline > 0)
        {
            int prevNewline = output.LastIndexOf('\n', lastNewline - 1);
            string line = output.Substring(prevNewline + 1, lastNewline - prevNewline - 1).Trim();
            if (line.EndsWith(":") && !line.StartsWith(";"))
            {
                return line.TrimEnd(':');
            }
            lastNewline = prevNewline;
        }
        return "entry"; // Default to entry block
    }

    private string GenerateNoneCoalesce(BinaryExpression node)
    {
        // Evaluate the left side first
        string left = node.Left.Accept(visitor: this);
        LLVMTypeInfo leftTypeInfo = GetTypeInfo(expr: node.Left);
        string leftTypeName = leftTypeInfo.RazorForgeType;

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
        LLVMTypeInfo rightTypeInfo = GetTypeInfo(expr: node.Right);
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
        _currentLocation = node.Location;
        switch (node.Value)
        {
            case int intVal:
                return intVal.ToString();
            case long longVal:
                return longVal.ToString();
            case byte byteVal:
                return byteVal.ToString();
            case sbyte sbyteVal:
                return sbyteVal.ToString();
            case short shortVal:
                return shortVal.ToString();
            case ushort ushortVal:
                return ushortVal.ToString();
            case uint uintVal:
                return uintVal.ToString();
            case ulong ulongVal:
                return ulongVal.ToString();
            case float floatVal:
                return floatVal.ToString(format: "G");
            case double doubleVal:
                return doubleVal.ToString(format: "G");
            case decimal decimalVal:
                return decimalVal.ToString(format: "G");
            case BigInteger bigIntVal:
                return bigIntVal.ToString();
            case Half halfVal:
                return ((float)halfVal).ToString(format: "G");
            case bool boolVal:
                return boolVal
                    ? "1"
                    : "0";
            case string strVal:
            {
                string strConst = $"@.str{_tempCounter++}";
                int len = strVal.Length + 1;
                // Store string constant for later emission instead of inserting immediately
                _stringConstants ??= [];

                _stringConstants.Add(
                    item:
                    $"{strConst} = private unnamed_addr constant [{len} x i8] c\"{strVal}\\00\", align 1");
                string temp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {temp} = getelementptr [{len} x i8], [{len} x i8]* {strConst}, i32 0, i32 0");
                // Register the temp as a string pointer type
                _tempTypes[key: temp] = new LLVMTypeInfo(LLVMType: "i8*",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "Text");
                return temp;
            }
            default:
                return "0";
        }

    }

    /// <summary>
    /// Visits a list literal expression in the abstract syntax tree (AST) and generates the corresponding LLVM IR code.
    /// This method handles the initialization of a list and its corresponding elements.
    /// </summary>
    /// <param name="node">The list literal expression node containing the list elements, their inferred type, and location information.</param>
    /// <returns>A string representing the generated LLVM IR code for the list literal expression.</returns>
    public string VisitListLiteralExpression(ListLiteralExpression node)
    {
        _currentLocation = node.Location;
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
        _currentLocation = node.Location;
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
        _currentLocation = node.Location;
        // TODO: Generate actual Dict allocation and initialization
        _output.AppendLine(handler: $"  ; Dict literal with {node.Pairs.Count} pairs");
        return "null"; // Placeholder
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        _currentLocation = node.Location;
        string sizeTemp = node.SizeExpression.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        // Check if sizeTemp is a MemorySize struct - if so, extract the i64 field
        string actualSizeValue = sizeTemp;
        if (_tempTypes.TryGetValue(key: sizeTemp, value: out LLVMTypeInfo? sizeTypeInfo) &&
            sizeTypeInfo.RazorForgeType == "MemorySize")
        {
            // MemorySize is a struct { i64 } - extract the i64 field
            string extractTemp = GetNextTemp();
            _output.AppendLine(handler: $"  {extractTemp} = extractvalue %MemorySize {sizeTemp}, 0");
            _tempTypes[key: extractTemp] = new LLVMTypeInfo(LLVMType: "i64",
                IsUnsigned: true,
                IsFloatingPoint: false,
                RazorForgeType: "u64");
            actualSizeValue = extractTemp;
        }

        switch (node.SliceType)
        {
            case "DynamicSlice":
                // Generate LLVM IR for heap slice construction
                _output.AppendLine(handler: $"  {resultTemp} = call ptr @heap_alloc(i64 {actualSizeValue})");
                break;
            // TODO: this slice type is removed.
            case "TemporarySlice":
                // Generate LLVM IR for stack slice construction
                _output.AppendLine(handler: $"  {resultTemp} = call ptr @stack_alloc(i64 {actualSizeValue})");
                break;
        }

        // Store slice type information for later use
        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "ptr",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.SliceType);
        return resultTemp;
    }
}