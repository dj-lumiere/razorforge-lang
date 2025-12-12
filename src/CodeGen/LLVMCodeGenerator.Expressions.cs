using System.Numerics;
using Compilers.Shared.Analysis;
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
        UpdateLocation(node.Location);
        string type;
        string? initValue = null;

        if (node.Type != null)
        {
            // Explicit type annotation
            // CRITICAL: Build full generic type name including type arguments
            // e.g., "TestType<s64>" not just "TestType"
            string fullTypeName = BuildFullTypeName(node.Type);
            type = MapTypeToLLVM(rfType: fullTypeName);
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
        // CRITICAL: Use BuildFullTypeName to preserve generic type arguments
        if (node.Type != null)
        {
            _symbolRfTypes[key: node.Name] = BuildFullTypeName(node.Type);
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

            // Check if we need type conversion
            string initType;
            if (_tempTypes.TryGetValue(key: initValue, value: out LLVMTypeInfo? initTypeInfo2))
            {
                initType = initTypeInfo2.LLVMType;
            }
            else
            {
                LLVMTypeInfo inferredInitType = GetTypeInfo(expr: node.Initializer);
                initType = inferredInitType.LLVMType;
            }

            // If types don't match, we need to cast the initializer value
            if (initType != type)
            {
                string castTemp = GetNextTemp();
                LLVMTypeInfo fromTypeInfo = _tempTypes.TryGetValue(initValue, out var tinfo)
                    ? tinfo
                    : GetTypeInfo(expr: node.Initializer);
                GenerateCastInstruction(result: castTemp, value: initValue, fromType: fromTypeInfo, toType: type);
                _output.AppendLine(handler: $"  store {type} {castTemp}, ptr {varName}");
            }
            else
            {
                _output.AppendLine(handler: $"  store {type} {initValue}, ptr {varName}");
            }
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
        UpdateLocation(node.Location);

        // Handle assignment specially - it stores to memory rather than computing a value
        if (node.Operator == BinaryOperator.Assign)
        {
            return GenerateAssignment(node: node);
        }

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

        // Detect floating-point types by LLVM type name, in case IsFloatingPoint flag isn't set
        // This handles both primitive FP types and record-wrapped FP types
        bool isActuallyFloatingPoint = leftTypeInfo.IsFloatingPoint ||
                                        operandType is "half" or "float" or "double" or "fp128" ||
                                        InferPrimitiveTypeFromRecordName(operandType) is "half" or "float" or "double" or "fp128";

        string op = node.Operator switch
        {
            // Regular arithmetic - use floating-point ops for FP types, integer ops otherwise
            BinaryOperator.Add => isActuallyFloatingPoint ? "fadd" : "add",
            BinaryOperator.Subtract => isActuallyFloatingPoint ? "fsub" : "sub",
            BinaryOperator.Multiply => isActuallyFloatingPoint ? "fmul" : "mul",
            BinaryOperator.FloorDivide => GetIntegerDivisionOp(
                typeInfo: leftTypeInfo), // sdiv/udiv based on signed/unsigned
            // TrueDivide (/) should be fdiv for floats
            // In RazorForge, / on integers is not allowed (should use // instead)
            // But for backwards compatibility and cases where semantic analyzer didn't catch it,
            // fall back to integer division
            BinaryOperator.TrueDivide => isActuallyFloatingPoint
                ? "fdiv"
                : GetIntegerDivisionOp(typeInfo: leftTypeInfo),
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


            // Comparisons - use fcmp for floats, icmp for integers
            BinaryOperator.Less => isActuallyFloatingPoint ? "fcmp olt" : "icmp slt",
            BinaryOperator.LessEqual => isActuallyFloatingPoint ? "fcmp ole" : "icmp sle",
            BinaryOperator.Greater => isActuallyFloatingPoint ? "fcmp ogt" : "icmp sgt",
            BinaryOperator.GreaterEqual => isActuallyFloatingPoint ? "fcmp oge" : "icmp sge",
            BinaryOperator.Equal => isActuallyFloatingPoint ? "fcmp oeq" : "icmp eq",
            BinaryOperator.NotEqual => isActuallyFloatingPoint ? "fcmp one" : "icmp ne",

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
                {
                    // Use ashr for signed, lshr for unsigned
                    string shiftOp = leftTypeInfo.IsUnsigned
                        ? "lshr"
                        : "ashr";

                    // Extract from left operand if it's a struct
                    string leftShift = left;
                    string shiftType = operandType;
                    bool isRecordType = false;
                    if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
                    {
                        string primitiveType = InferPrimitiveTypeFromRecordName(operandType);
                        if (primitiveType != operandType)
                        {
                            string leftExtract = GetNextTemp();
                            _output.AppendLine($"  {leftExtract} = extractvalue {operandType} {left}, 0");
                            leftShift = leftExtract;
                            shiftType = primitiveType;
                            isRecordType = true;
                        }
                    }

                    string shiftResult = GetNextTemp();
                    _output.AppendLine($"  {shiftResult} = {shiftOp} {shiftType} {leftShift}, {right}");

                    // Wrap result back into record if needed
                    if (isRecordType)
                    {
                        _output.AppendLine($"  {result} = insertvalue {operandType} undef, {shiftType} {shiftResult}, 0");
                        _tempTypes[key: result] = leftTypeInfo;
                    }
                    else
                    {
                        result = shiftResult;
                        _tempTypes[key: result] = leftTypeInfo;
                    }

                    return result;
                }

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

        // For floating-point operations, ensure operands have correct FP literal format
        // This fixes cases where literals are evaluated as one FP type but need another
        if (isActuallyFloatingPoint)
        {
            // Get the primitive type to match against (handle record-wrapped types)
            string targetFPType = operandType;
            if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
            {
                targetFPType = InferPrimitiveTypeFromRecordName(operandType);
            }

            // Ensure both operands use the correct FP format
            left = EnsureProperFPConstant(left, targetFPType);
            right = EnsureProperFPConstant(right, targetFPType);
        }

        // Handle type mismatch - truncate or extend as needed
        string rightOperand = right;

        // Check if types are pointers
        bool leftIsPointer = operandType.EndsWith(value: "*") || operandType == "ptr";
        bool rightIsPointer = rightTypeInfo.LLVMType.EndsWith(value: "*") || rightTypeInfo.LLVMType == "ptr";

        // Check if either operand is a record type - if so, skip type conversion here
        // Record types will be handled in the arithmetic section below
        bool leftIsRecord = operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr";
        bool rightIsRecord = rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr";

        if (rightTypeInfo.LLVMType != operandType && !rightTypeInfo.IsFloatingPoint &&
            !leftTypeInfo.IsFloatingPoint && !leftIsPointer && !rightIsPointer &&
            !leftIsRecord && !rightIsRecord)
        {
            // Determine the target type for conversion
            // If operandType is a record type, we need to convert to its primitive type instead
            string targetConversionType = operandType;
            bool targetIsRecord = false;

            if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
            {
                // Check if this is a record type
                if (_recordFields.TryGetValue(operandType, out var fields) && fields.Count > 0)
                {
                    targetConversionType = fields[0].Type;
                    targetIsRecord = true;
                }
                else
                {
                    string primitiveType = InferPrimitiveTypeFromRecordName(operandType);
                    if (primitiveType != operandType)
                    {
                        targetConversionType = primitiveType;
                        targetIsRecord = true;
                    }
                }
            }

            // Need to convert right operand to match left operand type
            int leftBits = GetIntegerBitWidth(llvmType: targetConversionType);
            int rightBits = GetIntegerBitWidth(llvmType: rightTypeInfo.LLVMType);

            if (rightBits > leftBits)
            {
                // If right operand is a struct, extract the primitive value first
                string rightToTrunc = right;
                string rightTypeToTrunc = rightTypeInfo.LLVMType;
                if (rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr")
                {
                    string primitiveType = InferPrimitiveTypeFromRecordName(rightTypeInfo.LLVMType);
                    string extractedRight = GetNextTemp();
                    _output.AppendLine($"  {extractedRight} = extractvalue {rightTypeInfo.LLVMType} {right}, 0");
                    rightToTrunc = extractedRight;
                    rightTypeToTrunc = primitiveType;
                }

                // Truncate right operand to match left
                string truncTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {truncTemp} = trunc {rightTypeToTrunc} {rightToTrunc} to {targetConversionType}");

                // Track the truncated temp type
                _tempTypes[key: truncTemp] = new LLVMTypeInfo(LLVMType: targetConversionType, IsUnsigned: rightTypeInfo.IsUnsigned, IsFloatingPoint: false);

                // If the left operand type is a record, wrap the truncated value back into the record
                // This ensures type consistency - if we're working with record types, results should be records
                if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
                {
                    string wrapTemp = GetNextTemp();
                    _output.AppendLine($"  {wrapTemp} = insertvalue {operandType} undef, {targetConversionType} {truncTemp}, 0");
                    _tempTypes[key: wrapTemp] = new LLVMTypeInfo(LLVMType: operandType, IsUnsigned: leftTypeInfo.IsUnsigned, IsFloatingPoint: leftTypeInfo.IsFloatingPoint, RazorForgeType: leftTypeInfo.RazorForgeType);
                    rightOperand = wrapTemp;
                }
                else
                {
                    rightOperand = truncTemp;
                }
            }
            else if (rightBits < leftBits)
            {
                // If right operand is a struct, extract the primitive value first
                string rightToExtend = right;
                string rightTypeToExtend = rightTypeInfo.LLVMType;
                if (rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr")
                {
                    string primitiveType = InferPrimitiveTypeFromRecordName(rightTypeInfo.LLVMType);
                    string extractedRight = GetNextTemp();
                    _output.AppendLine($"  {extractedRight} = extractvalue {rightTypeInfo.LLVMType} {right}, 0");
                    rightToExtend = extractedRight;
                    rightTypeToExtend = primitiveType;
                }

                // Extend right operand to match left
                string extTemp = GetNextTemp();
                string extOp = rightTypeInfo.IsUnsigned
                    ? "zext"
                    : "sext";
                _output.AppendLine(
                    handler:
                    $"  {extTemp} = {extOp} {rightTypeToExtend} {rightToExtend} to {targetConversionType}");

                // Track the extended temp type
                _tempTypes[key: extTemp] = new LLVMTypeInfo(LLVMType: targetConversionType, IsUnsigned: rightTypeInfo.IsUnsigned, IsFloatingPoint: false);

                // If the left operand type is a record, wrap the extended value back into the record
                if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
                {
                    string wrapTemp = GetNextTemp();
                    _output.AppendLine($"  {wrapTemp} = insertvalue {operandType} undef, {targetConversionType} {extTemp}, 0");
                    _tempTypes[key: wrapTemp] = new LLVMTypeInfo(LLVMType: operandType, IsUnsigned: leftTypeInfo.IsUnsigned, IsFloatingPoint: leftTypeInfo.IsFloatingPoint, RazorForgeType: leftTypeInfo.RazorForgeType);
                    rightOperand = wrapTemp;
                }
                else
                {
                    rightOperand = extTemp;
                }
            }
        }

        // Generate the operation with proper type
        if (op.StartsWith(value: "icmp") || op.StartsWith(value: "fcmp"))
        {
            // For record types (structs), we need to extract the inner value before comparison
            string leftCompare = left;
            string rightCompare = rightOperand;
            string compareType = operandType;

            // Check if either operand's type is a record (for type determination)
            // But only extract from operands that are actually record values
            bool leftIsRecordValue = leftTypeInfo.LLVMType.StartsWith("%") && !leftTypeInfo.LLVMType.EndsWith("*") && leftTypeInfo.LLVMType != "ptr";
            bool rightIsRecordValue = rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr";

            // Check if this is a record/struct type (starts with % but not a pointer)
            if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
            {
                // This is likely a record type - try to extract the value field
                if (_recordFields.TryGetValue(operandType, out var fields) && fields.Count > 0)
                {
                    // Record with fields - extract the first field for comparison
                    // This handles types like %uaddr { i64 }, %s32 { i32 }, etc.
                    var field = fields[0];
                    compareType = field.Type;

                    // Extract from left operand only if it's actually a record value
                    if (leftIsRecordValue)
                    {
                        string leftExtract = GetNextTemp();
                        _output.AppendLine($"  {leftExtract} = extractvalue {operandType} {left}, 0");
                        leftCompare = leftExtract;
                    }

                    // Extract from right operand only if it's actually a record value
                    if (rightIsRecordValue)
                    {
                        string rightExtract = GetNextTemp();
                        _output.AppendLine($"  {rightExtract} = extractvalue {operandType} {rightOperand}, 0");
                        rightCompare = rightExtract;
                    }
                }
                else
                {
                    // Fallback: assume it's a single-field wrapper with the primitive type
                    // Extract field name from type (e.g., %uaddr -> u64, %s32 -> i32)
                    // This is a heuristic for types we haven't seen definitions for yet
                    string primitiveType = InferPrimitiveTypeFromRecordName(operandType);
                    if (primitiveType != operandType)
                    {
                        compareType = primitiveType;

                        // Extract from left operand only if it's actually a record value
                        if (leftIsRecordValue)
                        {
                            string leftExtract = GetNextTemp();
                            _output.AppendLine($"  {leftExtract} = extractvalue {operandType} {left}, 0");
                            leftCompare = leftExtract;
                        }

                        // Extract from right operand only if it's actually a record value
                        if (rightIsRecordValue)
                        {
                            string rightExtract = GetNextTemp();
                            _output.AppendLine($"  {rightExtract} = extractvalue {operandType} {rightOperand}, 0");
                            rightCompare = rightExtract;
                        }
                    }
                }
            }

            // Comparison operations return i1
            _output.AppendLine(handler: $"  {result} = {op} {compareType} {leftCompare}, {rightCompare}");
            _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: "i1",
                IsUnsigned: false,
                IsFloatingPoint: false,
                RazorForgeType: "bool");
        }
        else
        {
            // Arithmetic operations - need to handle record types
            string leftArith = left;
            string rightArith = rightOperand;
            string arithType = operandType;
            bool isRecordType = false;
            string recordTypeForWrapping = null;

            // Check right operand type as well since it might be a record even if left isn't
            LLVMTypeInfo actualRightTypeInfo = rightTypeInfo;
            if (_tempTypes.TryGetValue(rightOperand, out var updatedRightType))
            {
                actualRightTypeInfo = updatedRightType;
            }

            // If right operand is a record but left isn't, use right's type as the operation type
            if ((!operandType.StartsWith("%") || operandType.EndsWith("*") || operandType == "ptr") &&
                actualRightTypeInfo.LLVMType.StartsWith("%") && !actualRightTypeInfo.LLVMType.EndsWith("*") && actualRightTypeInfo.LLVMType != "ptr")
            {
                // Right is a record but left isn't - use right's type
                operandType = actualRightTypeInfo.LLVMType;
                if (_recordFields.TryGetValue(actualRightTypeInfo.LLVMType, out var fields) && fields.Count > 0)
                {
                    arithType = fields[0].Type;
                }
                else
                {
                    arithType = InferPrimitiveTypeFromRecordName(actualRightTypeInfo.LLVMType);
                }
            }

            // Extract from left operand if it's a record type VALUE
            // Check if the left operand itself (not just the operation type) is a record
            bool leftActuallyIsRecord = leftTypeInfo.LLVMType.StartsWith("%") && !leftTypeInfo.LLVMType.EndsWith("*") && leftTypeInfo.LLVMType != "ptr";

            if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr" && leftActuallyIsRecord)
            {
                // This is likely a record type - try to extract the value field
                if (_recordFields.TryGetValue(operandType, out var fields) && fields.Count > 0)
                {
                    // Record with fields - extract the first field for arithmetic
                    var field = fields[0];
                    arithType = field.Type;
                    isRecordType = true;
                    recordTypeForWrapping = operandType;

                    // Extract from left operand
                    string leftExtract = GetNextTemp();
                    _output.AppendLine($"  {leftExtract} = extractvalue {operandType} {left}, 0");
                    leftArith = leftExtract;
                }
                else
                {
                    // Fallback: assume it's a single-field wrapper with the primitive type
                    string primitiveType = InferPrimitiveTypeFromRecordName(operandType);
                    if (primitiveType != operandType)
                    {
                        arithType = primitiveType;
                        isRecordType = true;
                        recordTypeForWrapping = operandType;

                        string leftExtract = GetNextTemp();
                        _output.AppendLine($"  {leftExtract} = extractvalue {operandType} {left}, 0");
                        leftArith = leftExtract;
                    }
                }
            }
            else if (operandType.StartsWith("%") && !operandType.EndsWith("*") && operandType != "ptr")
            {
                // Operation type is a record but left isn't - this means we need to set up for wrapping
                // without extracting from left
                if (_recordFields.TryGetValue(operandType, out var fields) && fields.Count > 0)
                {
                    arithType = fields[0].Type;
                    isRecordType = true;
                    recordTypeForWrapping = operandType;
                }
                else
                {
                    string primitiveType = InferPrimitiveTypeFromRecordName(operandType);
                    if (primitiveType != operandType)
                    {
                        arithType = primitiveType;
                        isRecordType = true;
                        recordTypeForWrapping = operandType;
                    }
                }
            }

            // Extract from right operand if it's also a record type
            // (actualRightTypeInfo already set above)
            if (actualRightTypeInfo.LLVMType.StartsWith("%") && !actualRightTypeInfo.LLVMType.EndsWith("*") && actualRightTypeInfo.LLVMType != "ptr")
            {
                // Right operand is a struct - extract its primitive value
                if (_recordFields.TryGetValue(actualRightTypeInfo.LLVMType, out var rightFields) && rightFields.Count > 0)
                {
                    string rightExtract = GetNextTemp();
                    _output.AppendLine($"  {rightExtract} = extractvalue {actualRightTypeInfo.LLVMType} {rightOperand}, 0");
                    rightArith = rightExtract;
                    // If left wasn't a record but right is, use right's type for wrapping
                    if (!isRecordType)
                    {
                        isRecordType = true;
                        recordTypeForWrapping = actualRightTypeInfo.LLVMType;
                        arithType = rightFields[0].Type;
                    }
                }
                else
                {
                    string rightPrimitiveType = InferPrimitiveTypeFromRecordName(actualRightTypeInfo.LLVMType);
                    if (rightPrimitiveType != actualRightTypeInfo.LLVMType)
                    {
                        string rightExtract = GetNextTemp();
                        _output.AppendLine($"  {rightExtract} = extractvalue {actualRightTypeInfo.LLVMType} {rightOperand}, 0");
                        rightArith = rightExtract;
                        // If left wasn't a record but right is, use right's type for wrapping
                        if (!isRecordType)
                        {
                            isRecordType = true;
                            recordTypeForWrapping = actualRightTypeInfo.LLVMType;
                            arithType = rightPrimitiveType;
                        }
                    }
                }
            }

            // Perform the arithmetic operation on the primitive values
            if (isRecordType)
            {
                // Do arithmetic on extracted primitives
                string primitiveResult = GetNextTemp();
                _output.AppendLine(handler: $"  {primitiveResult} = {op} {arithType} {leftArith}, {rightArith}");

                // Wrap the result back into the record type
                _output.AppendLine($"  {result} = insertvalue {recordTypeForWrapping} undef, {arithType} {primitiveResult}, 0");
                // Result type should match the record type we're wrapping into
                _tempTypes[key: result] = new LLVMTypeInfo(
                    LLVMType: recordTypeForWrapping,
                    IsUnsigned: leftTypeInfo.IsUnsigned,
                    IsFloatingPoint: leftTypeInfo.IsFloatingPoint,
                    RazorForgeType: leftTypeInfo.RazorForgeType);
            }
            else
            {
                // Regular arithmetic operation
                _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {rightOperand}");
                _tempTypes[key: result] = leftTypeInfo; // Result has same type as operands
            }
        }

        return result;
    }

    /// <summary>
    /// Infers the primitive LLVM type from a record type name.
    /// E.g., %uaddr -> i64, %s32 -> i32, %u8 -> i8
    /// </summary>
    private string InferPrimitiveTypeFromRecordName(string recordType)
    {
        // Map RazorForge record-wrapped primitives to their LLVM types
        return recordType switch
        {
            "%uaddr" => "i64",
            "%saddr" => "i64",
            "%u8" => "i8",
            "%u16" => "i16",
            "%u32" => "i32",
            "%u64" => "i64",
            "%u128" => "i128",
            "%s8" => "i8",
            "%s16" => "i16",
            "%s32" => "i32",
            "%s64" => "i64",
            "%s128" => "i128",
            "%f16" => "half",
            "%f32" => "float",
            "%f64" => "double",
            "%f128" => "fp128",
            "%d32" => "i32",  // decimal32 (stored as u32 bits)
            "%d64" => "i64",  // decimal64 (stored as u64 bits)
            "%d128" => "i128", // decimal128 (stored as {u64, u64} - multi-field, shouldn't reach here)
            "%bool" => "i1",   // boolean
            "%letter" or "%letter32" => "i32",  // UTF-32 character
            "%letter8" => "i8",    // UTF-8 code unit
            "%letter16" => "i16",  // UTF-16 code unit
            "%cstr" => "%uaddr",   // C string wraps uaddr
            _ => recordType // Unknown type, return as-is
        };
    }

    /// <summary>
    /// Formats a numeric value as an fp128 literal in LLVM IR hex format.
    /// fp128 in LLVM uses IEEE 754 quadruple precision format.
    /// For simplicity, we convert from double which may lose precision for large values.
    /// </summary>
    private string FormatFp128Literal(double value)
    {
        // Convert double to fp128 representation
        // For now, we'll use the double's bit pattern extended to 128 bits
        // This is a simplified approach - proper fp128 would need quad precision conversion
        long doubleBits = BitConverter.DoubleToInt64Bits(value);

        // fp128 hex format: 0xL followed by 32 hex digits (128 bits)
        // We'll put the double bits in the lower 64 bits and zero the upper 64 bits
        // Format: upper 64 bits (sign/exp/mantissa high) + lower 64 bits (mantissa low)
        return $"0xL{0:X16}{doubleBits:X16}";
    }

    /// <summary>
    /// Formats a numeric value as a half (f16) literal in LLVM IR hex format.
    /// </summary>
    private string FormatHalfLiteral(double value)
    {
        // Convert to Half, then to bits
        Half halfValue = (Half)value;
        ushort bits = BitConverter.HalfToUInt16Bits(halfValue);
        return $"0xH{bits:X4}";
    }

    /// <summary>
    /// Formats a numeric value as a float (f32) literal in LLVM IR hex format.
    /// LLVM IR hex floats are always 64-bit, so convert to double first.
    /// </summary>
    private string FormatFloatLiteral(double value)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        return $"0x{bits:X16}";
    }

    /// <summary>
    /// Formats a numeric value as a double (f64) literal in LLVM IR hex format.
    /// </summary>
    private string FormatDoubleLiteral(double value)
    {
        ulong bits = (ulong)BitConverter.DoubleToInt64Bits(value);
        return $"0x{bits:X16}";
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
            TokenType.SysuintLiteral => "i64", // uaddr - system pointer size (assume 64-bit)
            TokenType.SyssintLiteral => "i64", // saddr - system pointer size (assume 64-bit)
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
        UpdateLocation(node.Location);
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
        UpdateLocation(node.Location);
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
        for (int i = 0; i < node.Arguments.Count; i++)
        {
            Expression arg = node.Arguments[i];
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

            // TEMPORARY DEBUG: Always unwrap %uaddr or %cstr to i8* for external C functions
            // This is a workaround until we can properly track external function parameter types
            // Check both the inferred argType AND the registered type in _tempTypes
            string actualArgType = argType;
            if (_tempTypes.TryGetValue(argValue, out LLVMTypeInfo? registeredTypeInfo))
            {
                actualArgType = registeredTypeInfo.LLVMType;
            }

            if (actualArgType == "%uaddr" || actualArgType == "%cstr")
            {
                // Common pattern: C functions expecting char* receive uaddr/cstr from to_cstr()
                // %cstr is { %uaddr }, %uaddr is { i64 }
                // So for %cstr: extract %uaddr, then extract i64, then convert to i8*
                // For %uaddr: extract i64, then convert to i8*

                if (actualArgType == "%cstr")
                {
                    // Extract %uaddr from %cstr
                    string uaddrValue = GetNextTemp();
                    _output.AppendLine($"  {uaddrValue} = extractvalue %cstr {argValue}, 0");
                    argValue = uaddrValue;
                }

                // Now argValue is %uaddr, extract i64 and convert to i8*
                string unwrapped = GetNextTemp();
                _output.AppendLine($"  {unwrapped} = extractvalue %uaddr {argValue}, 0");
                string ptrValue = GetNextTemp();
                _output.AppendLine($"  {ptrValue} = inttoptr i64 {unwrapped} to i8*");
                argValue = ptrValue;
                argType = "i8*";
            }

            // CRITICAL: Multi-field structs must be passed by pointer per our ABI convention
            // If the argument is a multi-field struct VALUE (not already a pointer), we need to:
            // 1. Allocate stack space
            // 2. Store the value
            // 3. Pass the pointer
            if (argType.StartsWith("%") && !argType.EndsWith("*") && argType != "ptr")
            {
                string recordName = argType.TrimStart('%');
                if (_recordFields.TryGetValue(recordName, out var fields) && fields.Count > 1)
                {
                    // This is a multi-field struct value - need to convert to pointer
                    string stackSlot = GetNextTemp();
                    _output.AppendLine($"  {stackSlot} = alloca {argType}");
                    _output.AppendLine($"  store {argType} {argValue}, ptr {stackSlot}");
                    argValue = stackSlot;
                    argType = "ptr";
                }
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
                CallExpression => "<call>",
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
            else if (memberExpr.Object is CallExpression callExpr)
            {
                // Chained method call like me.min(x).max(y)
                // Visit the call expression to get its return type
                string callResult = callExpr.Accept(visitor: this);
                if (_tempTypes.TryGetValue(key: callResult, value: out LLVMTypeInfo? callTypeInfo))
                {
                    objectTypeName = callTypeInfo.RazorForgeType;
                }
                else if (callExpr.ResolvedType != null)
                {
                    objectTypeName = callExpr.ResolvedType.Name;
                }
            }
            else if (memberExpr.Object is MemberExpression nestedMemberExpr)
            {
                // Nested member access like range.start.resolve()
                // First try to get the type from semantic analysis
                if (nestedMemberExpr.ResolvedType != null)
                {
                    objectTypeName = nestedMemberExpr.ResolvedType.Name;
                }
                else
                {
                    // Visit the nested member expression to generate code and get its type
                    string nestedResult = nestedMemberExpr.Accept(visitor: this);
                    if (_tempTypes.TryGetValue(key: nestedResult, value: out LLVMTypeInfo? nestedTypeInfo))
                    {
                        objectTypeName = nestedTypeInfo.RazorForgeType;
                    }
                }
            }

            // If we still don't have a type, try to get it from the expression's ResolvedType
            // CRITICAL: Use FullName to get complete generic type info (e.g., "TestType<s64>" not "TestType")
            if (objectTypeName == null && memberExpr.Object.ResolvedType != null)
            {
                objectTypeName = memberExpr.Object.ResolvedType.FullName;
            }

            // For method calls, use the type name (if known) instead of the variable name
            // This converts me.length() to cstr.length(me) instead of me_length()
            string methodReceiverName = objectTypeName ?? objectName;
            functionName = $"{methodReceiverName}.{memberExpr.PropertyName}";

            string sanitizedFunctionName = SanitizeFunctionName(name: functionName);
            string methodReturnType = "i32";  // Default
            string rfReturnType = "s32";      // Default

            // CRITICAL: Check for generic template methods BEFORE looking in loaded modules
            // This allows methods on generic types defined in the same file to work
            bool isGenericMethod = false;
            if (objectTypeName != null && objectTypeName.Contains('<') && objectTypeName.Contains('>'))
            {
                // Try to find a matching generic template
                string? matchingTemplate = FindMatchingGenericTemplate(instantiatedType: objectTypeName, methodName: memberExpr.PropertyName);

                if (matchingTemplate != null)
                {
                    isGenericMethod = true;

                    // Extract concrete type arguments from the instantiated type
                    var typeArgs = ExtractTypeArguments(genericType: objectTypeName);

                    // Build the fully instantiated function name by replacing generic parameters with concrete types
                    // For BackIndex<I>.resolve with typeArgs=[uaddr], we want BackIndex<uaddr>.resolve
                    int genericStart = matchingTemplate.IndexOf('<');
                    int genericEnd = matchingTemplate.IndexOf('>');
                    int dotPos = matchingTemplate.IndexOf('.');

                    string baseTypeName = matchingTemplate.Substring(0, genericStart);
                    string methodNamePart = matchingTemplate.Substring(dotPos);
                    string fullyInstantiatedName = $"{baseTypeName}<{string.Join(", ", typeArgs)}>{methodNamePart}";

                    // Now sanitize it for LLVM: BackIndex<uaddr>.resolve -> BackIndex_uaddr.resolve
                    sanitizedFunctionName = SanitizeFunctionName(fullyInstantiatedName);

                    // Instantiate the generic method (this queues it for generation if not already done)
                    InstantiateGenericFunction(functionName: matchingTemplate, typeArguments: typeArgs);

                    // Get the return type from the template (before the function is generated)
                    methodReturnType = GetGenericFunctionReturnType(templateKey: matchingTemplate, concreteTypeArgs: typeArgs);
                    rfReturnType = methodReturnType; // For now, use the same value
                }
            }

            // If not a generic method from current file, check loaded modules
            if (!isGenericMethod)
            {
                // Check if this is a generic type from a loaded module that needs instantiation
                if (objectTypeName != null && objectTypeName.Contains('<') && objectTypeName.Contains('>'))
                {
                    // Try to find and instantiate a generic method template from loaded modules
                    string? loadedModuleTemplate = FindGenericMethodInLoadedModules(instantiatedType: objectTypeName, methodName: memberExpr.PropertyName);

                    if (loadedModuleTemplate != null)
                    {
                        isGenericMethod = true;

                        // Extract concrete type arguments from the instantiated type
                        var typeArgs = ExtractTypeArguments(genericType: objectTypeName);

                        // Build the fully instantiated function name
                        int genericStart = loadedModuleTemplate.IndexOf('<');
                        int genericEnd = loadedModuleTemplate.IndexOf('>');
                        int dotPos = loadedModuleTemplate.IndexOf('.');

                        string baseTypeName = loadedModuleTemplate.Substring(0, genericStart);
                        string methodNamePart = loadedModuleTemplate.Substring(dotPos);
                        string fullyInstantiatedName = $"{baseTypeName}<{string.Join(", ", typeArgs)}>{methodNamePart}";

                        // Sanitize for LLVM
                        sanitizedFunctionName = SanitizeFunctionName(fullyInstantiatedName);

                        // Instantiate the generic method from loaded module
                        InstantiateGenericFunction(functionName: loadedModuleTemplate, typeArguments: typeArgs);

                        // Get the return type from the template
                        methodReturnType = GetGenericFunctionReturnType(templateKey: loadedModuleTemplate, concreteTypeArgs: typeArgs);
                        rfReturnType = methodReturnType;
                    }
                }

                // If still not a generic method, lookup the method return type normally
                if (!isGenericMethod)
                {
                    (methodReturnType, rfReturnType) = LookupMethodReturnType(
                        objectTypeName: objectTypeName,
                        methodName: memberExpr.PropertyName);
                }
            }

            // For method calls on variables, we need to pass the object as the first argument
            string methodArgList;
            if (objectTypeName != null && objectTypeName != objectName)
            {
                // This is a method call on a typed variable - add the object as first argument
                string objectValue = memberExpr.Object.Accept(visitor: this);
                string objectLlvmType = _symbolTypes.TryGetValue(key: objectName, value: out string? storedType)
                    ? storedType
                    : MapTypeToLLVM(rfType: objectTypeName);

                // CRITICAL: Multi-field records receive 'me' as POINTER, single-field as VALUE
                // Check if the receiver type is a multi-field struct
                bool isMultiField = false;
                string receiverTypeName = MapTypeToLLVM(rfType: objectTypeName);
                if (receiverTypeName.StartsWith("%"))
                {
                    string recordName = receiverTypeName.TrimStart('%');
                    if (_recordFields.TryGetValue(recordName, out var fields) && fields.Count > 1)
                    {
                        isMultiField = true;
                    }
                }

                if (isMultiField)
                {
                    // Pass by pointer - need to check if objectValue is already a pointer or a value
                    // If it's a value (e.g., from a function return), we need to store it on the stack first
                    string objectPtr;
                    if (_tempTypes.TryGetValue(objectValue, out LLVMTypeInfo? objTypeInfo) &&
                        objTypeInfo.LLVMType == receiverTypeName)
                    {
                        // objectValue is a struct VALUE (from function return) - need to store on stack
                        objectPtr = GetNextTemp();
                        _output.AppendLine($"  {objectPtr} = alloca {receiverTypeName}");
                        _output.AppendLine($"  store {receiverTypeName} {objectValue}, ptr {objectPtr}");
                    }
                    else
                    {
                        // objectValue is already a pointer (from variable access)
                        objectPtr = objectValue;
                    }

                    methodArgList = args.Count > 0
                        ? $"ptr {objectPtr}, {argList}"
                        : $"ptr {objectPtr}";
                }
                else
                {
                    // Pass by value - need to load from pointer if objectValue is a pointer
                    if (objectValue.StartsWith("%__varptr_"))
                    {
                        string loadedValue = GetNextTemp();
                        _output.AppendLine($"  {loadedValue} = load {receiverTypeName}, ptr {objectValue}");
                        objectValue = loadedValue;
                    }
                    methodArgList = args.Count > 0
                        ? $"{receiverTypeName} {objectValue}, {argList}"
                        : $"{receiverTypeName} {objectValue}";
                }
            }
            else
            {
                methodArgList = argList;
            }

            // Handle void return type - don't assign to a variable
            if (methodReturnType == "void")
            {
                _output.AppendLine(
                    handler: $"  call void @{sanitizedFunctionName}({methodArgList})");
                return "";
            }

            _output.AppendLine(
                handler: $"  {result} = call {methodReturnType} @{sanitizedFunctionName}({methodArgList})");

            // Track the result type - use the RazorForge type from method lookup (NOT lossy GetRazorForgeTypeFromLLVM)
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

        // Handle void return type - don't assign to a variable
        if (defaultReturnType == "void")
        {
            _output.AppendLine(
                handler: $"  call void @{defaultSanitizedFunctionName}({argList})");
            return "";
        }

        _output.AppendLine(
            handler: $"  {result} = call {defaultReturnType} @{defaultSanitizedFunctionName}({argList})");

        // Track the result type in _tempTypes so return statements can find it
        string defaultRfReturnType = GetRazorForgeTypeFromLLVM(llvmType: defaultReturnType);
        _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: defaultReturnType,
            IsUnsigned: defaultRfReturnType.StartsWith(value: "u"),
            IsFloatingPoint: defaultReturnType.Contains(value: "float") ||
                             defaultReturnType.Contains(value: "double"),
            RazorForgeType: defaultRfReturnType);

        return result;
    }
    public string VisitUnaryExpression(UnaryExpression node)
    {
        UpdateLocation(node.Location);
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

        // Try to get type from temp types first (for call expressions and other complex expressions)
        LLVMTypeInfo operandType;
        if (_tempTypes.TryGetValue(key: operand, value: out LLVMTypeInfo? trackedType))
        {
            operandType = trackedType;
        }
        else
        {
            operandType = GetTypeInfo(expr: node.Operand);
        }

        string llvmType = operandType.LLVMType;

        switch (node.Operator)
        {
            case UnaryOperator.Minus:
                // Negation: fneg for floats, 0 - operand for integers
                string result = GetNextTemp();

                // Check if this is a floating-point type (primitive or record-wrapped)
                bool isFPType = operandType.IsFloatingPoint ||
                                llvmType is "half" or "float" or "double" or "fp128";

                // Check if this is a record-wrapped floating-point type
                bool isRecordWrappedFP = false;
                string primitiveType = llvmType;

                if (llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr")
                {
                    // Might be a record type - check if it wraps a floating-point primitive
                    string inferredPrimitive = InferPrimitiveTypeFromRecordName(llvmType);
                    if (inferredPrimitive is "half" or "float" or "double" or "fp128")
                    {
                        isRecordWrappedFP = true;
                        primitiveType = inferredPrimitive;
                        isFPType = true;
                    }
                }

                if (isFPType)
                {
                    if (isRecordWrappedFP)
                    {
                        // Extract the floating-point value from the record
                        string extracted = GetNextTemp();
                        _output.AppendLine($"  {extracted} = extractvalue {llvmType} {operand}, 0");

                        // Negate the floating-point value
                        string negated = GetNextTemp();
                        _output.AppendLine($"  {negated} = fneg {primitiveType} {extracted}");

                        // Wrap the result back into the record
                        _output.AppendLine($"  {result} = insertvalue {llvmType} undef, {primitiveType} {negated}, 0");
                    }
                    else
                    {
                        // Primitive floating-point type - use fneg directly
                        _output.AppendLine(handler: $"  {result} = fneg {llvmType} {operand}");
                    }
                }
                else
                {
                    // Integer type - use subtraction
                    _output.AppendLine(handler: $"  {result} = sub {llvmType} 0, {operand}");
                }

                _tempTypes[key: result] = operandType;
                return result;

            case UnaryOperator.Not:
                // Logical NOT: xor with 1 for i1 (bool)
                // Check if operand is a %bool record and extract if needed
                string boolOperand = operand;
                bool operandIsRecord = llvmType == "%bool";
                if (operandIsRecord)
                {
                    string extractedBool = GetNextTemp();
                    _output.AppendLine($"  {extractedBool} = extractvalue %bool {operand}, 0");
                    boolOperand = extractedBool;
                }

                string notResult = GetNextTemp();
                _output.AppendLine(handler: $"  {notResult} = xor i1 {boolOperand}, 1");

                // Wrap result back into %bool if operand was a record
                if (operandIsRecord)
                {
                    string wrappedResult = GetNextTemp();
                    _output.AppendLine($"  {wrappedResult} = insertvalue %bool undef, i1 {notResult}, 0");
                    _tempTypes[key: wrappedResult] = new LLVMTypeInfo(LLVMType: "%bool",
                        IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: "bool");
                    return wrappedResult;
                }

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
        UpdateLocation(node.Location);
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

        // Look up the RazorForge field type from the record definition
        string rfFieldType = fieldType;
        if (_recordFieldsRfTypes.TryGetValue(recordType, out var rfFieldsList))
        {
            var fieldInfo = rfFieldsList.FirstOrDefault(f => f.Name == node.PropertyName);
            if (fieldInfo != default)
            {
                rfFieldType = fieldInfo.RfType;
            }
        }

        // Fallback: Convert LLVM field type to RazorForge type for method lookup
        if (rfFieldType == fieldType && fieldType.StartsWith("%"))
        {
            rfFieldType = ConvertLLVMStructTypeToRazorForge(llvmType: fieldType);
        }

        _tempTypes[key: result] = new LLVMTypeInfo(LLVMType: fieldType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: rfFieldType);
        return result;

    }
    public string VisitIndexExpression(IndexExpression node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitConditionalExpression(ConditionalExpression node)
    {
        UpdateLocation(node.Location);

        // Evaluate the condition
        string condition = node.Condition.Accept(visitor: this);

        // For simple conditional expressions, we can use LLVM's select instruction
        // which is more efficient than phi nodes for simple cases
        // select i1 <cond>, <ty> <val1>, <ty> <val2>

        // Evaluate both branches
        string trueValue = node.TrueExpression.Accept(visitor: this);
        string falseValue = node.FalseExpression.Accept(visitor: this);

        // Get the type of the true branch (both should be the same type)
        LLVMTypeInfo trueTypeInfo = GetValueTypeInfo(value: trueValue);
        string resultType = trueTypeInfo.LLVMType;

        // Use select instruction for simple ternary operation
        string resultReg = GetNextTemp();
        _output.AppendLine($"  {resultReg} = select i1 {condition}, {resultType} {trueValue}, {resultType} {falseValue}");

        // Register the result type for this temporary variable
        _tempTypes[key: resultReg] = trueTypeInfo;

        return resultReg;
    }

    public string VisitBlockExpression(BlockExpression node)
    {
        UpdateLocation(node.Location);
        // A block expression evaluates to its inner expression
        return node.Value.Accept(visitor: this);
    }

    public string VisitRangeExpression(RangeExpression node)
    {
        UpdateLocation(node.Location);
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
        UpdateLocation(node.Location);
        // Desugar chained comparison: a < b < c becomes (a < b) and (b < c)
        // with single evaluation of b
        if (node.Operands.Count < 2 || node.Operators.Count < 1)
        {
            return "";
        }

        var tempVars = new List<string>();
        var tempTypes = new List<LLVMTypeInfo>();

        // Evaluate all operands once and store in temporaries
        for (int i = 0; i < node.Operands.Count; i++)
        {
            string operandValue = node.Operands[index: i].Accept(visitor: this);
            LLVMTypeInfo operandType;

            // Get the type of the operand
            if (_tempTypes.TryGetValue(key: operandValue, value: out LLVMTypeInfo? trackedType))
            {
                operandType = trackedType;
            }
            else
            {
                operandType = GetTypeInfo(expr: node.Operands[index: i]);
            }

            if (i > 0 && i < node.Operands.Count - 1)
            {
                // Middle operands need temporary storage to avoid multiple evaluation
                // Check if this is a record type
                bool isRecordType = operandType.LLVMType.StartsWith("%") &&
                                   !operandType.LLVMType.EndsWith("*") &&
                                   operandType.LLVMType != "ptr";

                if (isRecordType)
                {
                    // For record types, we can just reuse the SSA value directly
                    // (no need to "copy" in SSA form)
                    tempVars.Add(item: operandValue);
                }
                else
                {
                    // For primitive types, use add trick to create a new temp
                    string temp = GetNextTemp();
                    _output.AppendLine(
                        handler: $"  {temp} = add {operandType.LLVMType} {operandValue}, 0  ; store for reuse");
                    _tempTypes[key: temp] = operandType;
                    tempVars.Add(item: temp);
                }
            }
            else
            {
                tempVars.Add(item: operandValue);
            }

            tempTypes.Add(item: operandType);
        }

        // Generate comparisons: (temp0 op0 temp1) and (temp1 op1 temp2) and ...
        var compResults = new List<string>();
        LLVMTypeInfo boolTypeInfo = new LLVMTypeInfo(LLVMType: "i1",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: "bool");

        for (int i = 0; i < node.Operators.Count; i++)
        {
            string compResult = GetNextTemp();
            string left = tempVars[index: i];
            string right = tempVars[index: i + 1];

            // Get the types of the left and right operands for this comparison
            LLVMTypeInfo leftType = tempTypes[index: i];
            LLVMTypeInfo rightType = tempTypes[index: i + 1];

            // Check if operands are record types and extract primitives if needed
            bool leftIsRecord = leftType.LLVMType.StartsWith("%") && !leftType.LLVMType.EndsWith("*") && leftType.LLVMType != "ptr";
            bool rightIsRecord = rightType.LLVMType.StartsWith("%") && !rightType.LLVMType.EndsWith("*") && rightType.LLVMType != "ptr";

            string leftPrimitiveType = leftIsRecord ? InferPrimitiveTypeFromRecordName(leftType.LLVMType) : leftType.LLVMType;
            string rightPrimitiveType = rightIsRecord ? InferPrimitiveTypeFromRecordName(rightType.LLVMType) : rightType.LLVMType;

            // Extract from records if needed
            if (leftIsRecord)
            {
                string extractedLeft = GetNextTemp();
                _output.AppendLine($"  {extractedLeft} = extractvalue {leftType.LLVMType} {left}, 0");
                left = extractedLeft;
            }

            if (rightIsRecord)
            {
                string extractedRight = GetNextTemp();
                _output.AppendLine($"  {extractedRight} = extractvalue {rightType.LLVMType} {right}, 0");
                right = extractedRight;
            }

            // Use the larger type for the comparison
            string comparisonType;
            bool isUnsigned;
            if (leftPrimitiveType == rightPrimitiveType)
            {
                comparisonType = leftPrimitiveType;
                isUnsigned = leftType.IsUnsigned;
            }
            else
            {
                // Types differ - need to cast one to match the other
                // Choose the larger type
                int leftBits = GetIntegerBitWidth(llvmType: leftPrimitiveType);
                int rightBits = GetIntegerBitWidth(llvmType: rightPrimitiveType);

                if (leftBits >= rightBits)
                {
                    // Use left type, extend right if needed
                    comparisonType = leftPrimitiveType;
                    isUnsigned = leftType.IsUnsigned;

                    if (leftBits > rightBits)
                    {
                        string extTemp = GetNextTemp();
                        string extOp = rightType.IsUnsigned ? "zext" : "sext";
                        _output.AppendLine($"  {extTemp} = {extOp} {rightPrimitiveType} {right} to {comparisonType}");
                        right = extTemp;
                    }
                }
                else
                {
                    // Use right type, extend left
                    comparisonType = rightPrimitiveType;
                    isUnsigned = rightType.IsUnsigned;

                    string extTemp = GetNextTemp();
                    string extOp = leftType.IsUnsigned ? "zext" : "sext";
                    _output.AppendLine($"  {extTemp} = {extOp} {leftPrimitiveType} {left} to {comparisonType}");
                    left = extTemp;
                }
            }

            // Select signed or unsigned comparison based on operand type
            string op = node.Operators[index: i] switch
            {
                BinaryOperator.Less => isUnsigned ? "icmp ult" : "icmp slt",
                BinaryOperator.LessEqual => isUnsigned ? "icmp ule" : "icmp sle",
                BinaryOperator.Greater => isUnsigned ? "icmp ugt" : "icmp sgt",
                BinaryOperator.GreaterEqual => isUnsigned ? "icmp uge" : "icmp sge",
                BinaryOperator.Equal => "icmp eq",
                BinaryOperator.NotEqual => "icmp ne",
                _ => throw new NotSupportedException(
                    message: $"Chained comparison operator '{node.Operators[index: i]}' is not supported")
            };

            _output.AppendLine(handler: $"  {compResult} = {op} {comparisonType} {left}, {right}");
            _tempTypes[key: compResult] = boolTypeInfo;
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
            _tempTypes[key: temp] = boolTypeInfo;
            finalResult = temp;
        }

        return finalResult;
    }
    public string VisitTypeExpression(TypeExpression node)
    {
        UpdateLocation(node.Location);
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

        // Extract from %bool if needed
        LLVMTypeInfo leftTypeInfo = GetValueTypeInfo(left);
        if (leftTypeInfo.LLVMType == "%bool")
        {
            string extractedLeft = GetNextTemp();
            _output.AppendLine($"  {extractedLeft} = extractvalue %bool {left}, 0");
            left = extractedLeft;
        }

        // Get current block for phi node
        string leftBlock = GetCurrentBlockName();

        // Branch: if left is true, evaluate right; otherwise short-circuit to false
        _output.AppendLine(handler: $"  br i1 {left}, label %{evalRightLabel}, label %{endLabel}");

        // Evaluate right operand
        _output.AppendLine(handler: $"{evalRightLabel}:");
        string right = node.Right.Accept(visitor: this);

        // Extract from %bool if needed
        LLVMTypeInfo rightTypeInfo = GetValueTypeInfo(right);
        if (rightTypeInfo.LLVMType == "%bool")
        {
            string extractedRight = GetNextTemp();
            _output.AppendLine($"  {extractedRight} = extractvalue %bool {right}, 0");
            right = extractedRight;
        }

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

        // Extract from %bool if needed
        LLVMTypeInfo leftTypeInfo = GetValueTypeInfo(left);
        if (leftTypeInfo.LLVMType == "%bool")
        {
            string extractedLeft = GetNextTemp();
            _output.AppendLine($"  {extractedLeft} = extractvalue %bool {left}, 0");
            left = extractedLeft;
        }

        // Get current block for phi node
        string leftBlock = GetCurrentBlockName();

        // Branch: if left is true, evaluate right; otherwise short-circuit to true
        _output.AppendLine(handler: $"  br i1 {left}, label %{endLabel}, label %{evalRightLabel}");

        // Evaluate right operand
        _output.AppendLine(handler: $"{evalRightLabel}:");
        string right = node.Right.Accept(visitor: this);

        // Extract from %bool if needed
        LLVMTypeInfo rightTypeInfo = GetValueTypeInfo(right);
        if (rightTypeInfo.LLVMType == "%bool")
        {
            string extractedRight = GetNextTemp();
            _output.AppendLine($"  {extractedRight} = extractvalue %bool {right}, 0");
            right = extractedRight;
        }

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

    /// <summary>
    /// Generates LLVM IR for assignment expressions (lhs = rhs).
    /// Stores the right-hand side value into the left-hand side location.
    /// </summary>
    private string GenerateAssignment(BinaryExpression node)
    {
        // Evaluate the right-hand side first
        string rhs = node.Right.Accept(visitor: this);

        // Get the type of the right-hand side
        LLVMTypeInfo rhsTypeInfo;
        if (_tempTypes.TryGetValue(key: rhs, value: out LLVMTypeInfo? trackedType))
        {
            rhsTypeInfo = trackedType;
        }
        else
        {
            rhsTypeInfo = GetTypeInfo(expr: node.Right);
        }

        // Handle different left-hand side patterns
        switch (node.Left)
        {
            case IdentifierExpression identExpr:
            {
                // Simple variable assignment: x = value
                string varName = identExpr.Name;

                // Check if this is a function parameter that needs to be materialized
                // Function parameters are passed by value and are immutable in LLVM
                // If we're assigning to a parameter, we need to create a stack slot for it
                if (_functionParameters.Contains(item: varName))
                {
                    // Materialize the parameter: allocate stack space and copy parameter value
                    string paramType = _symbolTypes[key: varName];
                    string stackSlot = $"%{varName}_slot";

                    // Allocate stack space
                    _output.AppendLine(handler: $"  {stackSlot} = alloca {paramType}");

                    // Copy the original parameter value to the stack slot
                    _output.AppendLine(handler: $"  store {paramType} %{varName}, ptr {stackSlot}");

                    // Update tracking: remove from parameters, add to variables
                    _functionParameters.Remove(item: varName);
                    _symbolTypes[key: $"__varptr_{varName}"] = $"{varName}_slot";

                    // Now store the new value
                    _output.AppendLine(handler: $"  store {rhsTypeInfo.LLVMType} {rhs}, ptr {stackSlot}");

                    // Update the symbol type if needed
                    _symbolTypes[key: varName] = rhsTypeInfo.LLVMType;
                    if (!string.IsNullOrEmpty(rhsTypeInfo.RazorForgeType))
                    {
                        _symbolRfTypes[key: varName] = rhsTypeInfo.RazorForgeType;
                    }

                    return rhs;
                }

                // Get the variable's pointer (use unique name if scoped)
                string varPtr = _symbolTypes.TryGetValue(key: $"__varptr_{varName}", value: out string? uniqueName)
                    ? $"%{uniqueName}"
                    : $"%{varName}";

                // Store the value
                _output.AppendLine(handler: $"  store {rhsTypeInfo.LLVMType} {rhs}, ptr {varPtr}");

                // Update the symbol type if needed
                _symbolTypes[key: varName] = rhsTypeInfo.LLVMType;
                if (!string.IsNullOrEmpty(rhsTypeInfo.RazorForgeType))
                {
                    _symbolRfTypes[key: varName] = rhsTypeInfo.RazorForgeType;
                }

                return rhs; // Assignment expressions return the assigned value
            }

            case MemberExpression memberExpr:
            {
                // Field assignment: obj.field = value
                string? objectName = memberExpr.Object switch
                {
                    IdentifierExpression idExpr => idExpr.Name,
                    _ => null
                };

                if (objectName == null)
                {
                    throw CodeGenError.UnsupportedFeature(
                        feature: "complex member assignment target",
                        file: _currentFileName,
                        line: node.Location.Line,
                        column: node.Location.Column,
                        position: node.Location.Position);
                }

                // Get the record type
                string? recordType = null;
                if (_symbolRfTypes.TryGetValue(key: objectName, value: out string? rfType))
                {
                    recordType = rfType;
                }
                else if (_symbolTypes.TryGetValue(key: objectName, value: out string? llvmType) &&
                         llvmType.StartsWith(value: "%"))
                {
                    recordType = llvmType[1..];
                }

                // Handle generic type names
                if (recordType != null && recordType.Contains(value: '<'))
                {
                    recordType = recordType
                        .Replace(oldValue: "<", newValue: "_")
                        .Replace(oldValue: ">", newValue: "")
                        .Replace(oldValue: ", ", newValue: "_")
                        .Replace(oldValue: ",", newValue: "_");
                }

                if (recordType == null || !_recordFields.TryGetValue(key: recordType,
                        value: out List<(string Name, string Type)>? fields))
                {
                    throw CodeGenError.TypeResolutionFailed(
                        typeName: objectName,
                        context: "cannot determine record type for field assignment",
                        file: _currentFileName,
                        line: node.Location.Line,
                        column: node.Location.Column,
                        position: node.Location.Position);
                }

                // Find the field index
                int fieldIndex = -1;
                string? fieldType = null;
                for (int i = 0; i < fields.Count; i++)
                {
                    if (fields[index: i].Name == memberExpr.PropertyName)
                    {
                        fieldIndex = i;
                        fieldType = fields[index: i].Type;
                        break;
                    }
                }

                if (fieldIndex < 0 || fieldType == null)
                {
                    throw CodeGenError.TypeResolutionFailed(
                        typeName: memberExpr.PropertyName,
                        context: $"field not found in record type '{recordType}'",
                        file: _currentFileName,
                        line: node.Location.Line,
                        column: node.Location.Column,
                        position: node.Location.Position);
                }

                // Get the object's pointer
                // Check if objectName is a function parameter passed by value (struct type, not pointer)
                bool isValueParameter = _functionParameters.Contains(item: objectName) &&
                                        _symbolTypes.TryGetValue(key: objectName, value: out string? paramLlvmType) &&
                                        paramLlvmType.StartsWith(value: "%") &&
                                        !paramLlvmType.EndsWith(value: "*");

                string varPtr;
                if (isValueParameter)
                {
                    // Allocate stack space and store the struct value to get a pointer
                    varPtr = GetNextTemp();
                    _output.AppendLine(handler: $"  {varPtr} = alloca %{recordType}");
                    _output.AppendLine(handler: $"  store %{recordType} %{objectName}, ptr {varPtr}");
                }
                else
                {
                    varPtr = _symbolTypes.TryGetValue(key: $"__varptr_{objectName}", value: out string? uniqueName)
                        ? $"%{uniqueName}"
                        : $"%{objectName}";
                }

                // Generate getelementptr to get field address
                string fieldPtr = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {fieldPtr} = getelementptr inbounds %{recordType}, ptr {varPtr}, i32 0, i32 {fieldIndex}");

                // Store the value
                _output.AppendLine(handler: $"  store {rhsTypeInfo.LLVMType} {rhs}, ptr {fieldPtr}");

                return rhs;
            }

            case IndexExpression indexExpr:
            {
                // Array/slice element assignment: arr[i] = value
                // TODO: Implement array element assignment
                throw CodeGenError.UnsupportedFeature(
                    feature: "index assignment (array element assignment)",
                    file: _currentFileName,
                    line: node.Location.Line,
                    column: node.Location.Column,
                    position: node.Location.Position);
            }

            default:
                throw CodeGenError.UnsupportedFeature(
                    feature: $"assignment to {node.Left.GetType().Name}",
                    file: _currentFileName,
                    line: node.Location.Line,
                    column: node.Location.Column,
                    position: node.Location.Position);
        }
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
        UpdateLocation(node.Location);

        // Check if we need to handle typed literals based on resolved type
        if (node.ResolvedType != null)
        {
            var typeInfo = ConvertAstTypeInfoToLLVM(node.ResolvedType);
            string llvmType = typeInfo.LLVMType;
            bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";

            // Handle floating-point literals
            if (node.Value is double or float or decimal or Half)
            {
                // Get the primitive type for formatting
                string primitiveType = isRecordType ? InferPrimitiveTypeFromRecordName(llvmType) : llvmType;

                // Get the numeric value as double
                double numericValue = node.Value switch
                {
                    double d => d,
                    float f => (double)f,
                    decimal m => (double)m,
                    Half h => (double)h,
                    _ => 0.0
                };

                // Format based on the primitive LLVM type
                string formattedValue = primitiveType switch
                {
                    "fp128" => FormatFp128Literal(numericValue),
                    "half" => FormatHalfLiteral(numericValue),
                    "float" => FormatFloatLiteral(numericValue),
                    "double" => FormatDoubleLiteral(numericValue),
                    _ => numericValue.ToString("G")
                };

                // If the type is a record, wrap the literal in a struct
                if (isRecordType)
                {
                    string temp = GetNextTemp();
                    _output.AppendLine($"  {temp} = insertvalue {llvmType} undef, {primitiveType} {formattedValue}, 0");
                    _tempTypes[temp] = typeInfo;
                    return temp;
                }

                // Otherwise return the raw formatted value
                return formattedValue;
            }

            // Handle integer literals with record types
            if (isRecordType && node.Value is int or long or byte or sbyte or short or ushort or uint or ulong or BigInteger)
            {
                // Get the primitive type
                string primitiveType = InferPrimitiveTypeFromRecordName(llvmType);

                // Get the string representation of the integer
                string literalValue = node.Value.ToString()!;

                // Wrap the integer literal in a struct
                string temp = GetNextTemp();
                _output.AppendLine($"  {temp} = insertvalue {llvmType} undef, {primitiveType} {literalValue}, 0");
                _tempTypes[temp] = typeInfo;
                return temp;
            }
        }

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
            {
                // Fallback for untyped float literals
                double asDouble = (double)floatVal;
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(asDouble);
                return $"0x{bits:X16}";
            }
            case double doubleVal:
            {
                // Fallback for untyped double literals
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(doubleVal);
                return $"0x{bits:X16}";
            }
            case decimal decimalVal:
            {
                // Fallback for decimal literals
                double asDouble = (double)decimalVal;
                ulong bits = (ulong)BitConverter.DoubleToInt64Bits(asDouble);
                return $"0x{bits:X16}";
            }
            case BigInteger bigIntVal:
                return bigIntVal.ToString();
            case Half halfVal:
            {
                // Fallback for half literals
                ushort bits = BitConverter.HalfToUInt16Bits(halfVal);
                return $"0xH{bits:X4}";
            }
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
        UpdateLocation(node.Location);
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
        UpdateLocation(node.Location);
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
        UpdateLocation(node.Location);
        // TODO: Generate actual Dict allocation and initialization
        _output.AppendLine(handler: $"  ; Dict literal with {node.Pairs.Count} pairs");
        return "null"; // Placeholder
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        UpdateLocation(node.Location);
        string sizeTemp = node.SizeExpression.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        // Check if sizeTemp is a MemorySize or uaddr struct - if so, extract the i64 field
        string actualSizeValue = sizeTemp;
        if (_tempTypes.TryGetValue(key: sizeTemp, value: out LLVMTypeInfo? sizeTypeInfo))
        {
            if (sizeTypeInfo.RazorForgeType == "MemorySize")
            {
                // MemorySize is a struct { %uaddr } - extract %uaddr first, then extract i64
                string uaddrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {uaddrTemp} = extractvalue %MemorySize {sizeTemp}, 0");
                string extractTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {extractTemp} = extractvalue %uaddr {uaddrTemp}, 0");
                _tempTypes[key: extractTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: true,
                    IsFloatingPoint: false,
                    RazorForgeType: "u64");
                actualSizeValue = extractTemp;
            }
            else if (sizeTypeInfo.LLVMType == "%uaddr")
            {
                // uaddr is a struct { i64 } - extract the i64 field
                string extractTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {extractTemp} = extractvalue %uaddr {sizeTemp}, 0");
                _tempTypes[key: extractTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: true,
                    IsFloatingPoint: false,
                    RazorForgeType: "u64");
                actualSizeValue = extractTemp;
            }
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