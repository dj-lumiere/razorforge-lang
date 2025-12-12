using Compilers.Shared.AST;
using Compilers.Shared.Errors;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Partial class containing intrinsic code generation methods.
/// Handles @intrinsic.* operations for memory, arithmetic, bitwise, comparison, conversion, and math operations.
/// </summary>
public partial class LLVMCodeGenerator
{
    #region Intrinsic Helper Methods

    private string EmitMemoryIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "memory intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        switch (intrinsicName)
        {
            case "load":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  {resultTemp} = load {llvmType}, ptr {ptrTemp}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: llvmType,
                    IsUnsigned: false,
                    IsFloatingPoint: llvmType.Contains(value: "float") ||
                                     llvmType.Contains(value: "double"),
                    RazorForgeType: node.TypeArguments[index: 0]);
                return resultTemp;
            }

            case "store":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string valueTemp = node.Arguments[index: 1]
                                       .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  store {llvmType} {valueTemp}, ptr {ptrTemp}");
                return ""; // void
            }

            case "volatile_load":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler: $"  {resultTemp} = load volatile {llvmType}, ptr {ptrTemp}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: llvmType,
                    IsUnsigned: false,
                    IsFloatingPoint: llvmType.Contains(value: "float") ||
                                     llvmType.Contains(value: "double"),
                    RazorForgeType: node.TypeArguments[index: 0]);
                return resultTemp;
            }

            case "volatile_store":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string valueTemp = node.Arguments[index: 1]
                                       .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler: $"  store volatile {llvmType} {valueTemp}, ptr {ptrTemp}");
                return ""; // void
            }

            case "bitcast":
            {
                string valueTemp = node.Arguments[index: 0]
                                       .Accept(visitor: this);
                string fromType =
                    MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);
                string toType =
                    MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 1]);

                // Check the ACTUAL type of the value, not just the type argument mapping
                // Since RazorForge wraps primitives in records, the actual value might be %saddr even if iptr maps to i64
                LLVMTypeInfo? actualValueType = null;
                if (_tempTypes.TryGetValue(valueTemp, out var tempType))
                {
                    actualValueType = tempType;
                }
                else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolType))
                {
                    actualValueType = new LLVMTypeInfo(LLVMType: symbolType, IsUnsigned: false, IsFloatingPoint: false);
                }

                // Use actual value type if available, otherwise fall back to mapped type
                string actualFromType = actualValueType?.LLVMType ?? fromType;

                // Check if target is a record/struct type
                bool toIsStruct = toType.StartsWith("%") && !toType.EndsWith("*") && toType != "ptr";
                bool fromIsStruct = actualFromType.StartsWith("%") && !actualFromType.EndsWith("*") && actualFromType != "ptr";

                if (fromIsStruct || toIsStruct)
                {
                    // For struct types, we need to handle differently
                    // Extract primitive type and construct/deconstruct structs
                    string primitiveType = toIsStruct ? InferPrimitiveTypeFromRecordName(toType) : toType;
                    string sourcePrimitive = fromIsStruct ? InferPrimitiveTypeFromRecordName(actualFromType) : actualFromType;

                    // If source is a struct, extract its primitive value first
                    string primitiveValue = valueTemp;
                    string currentType = actualFromType;
                    if (fromIsStruct)
                    {
                        // Extract field 0 from the source struct
                        string extractedTemp = GetNextTemp();
                        _output.AppendLine($"  {extractedTemp} = extractvalue {actualFromType} {valueTemp}, 0");

                        // Check if the extracted field is itself a record type
                        string sourceRecordName = actualFromType.TrimStart('%');
                        if (_recordFields.TryGetValue(sourceRecordName, out var sourceFields) && sourceFields.Count > 0)
                        {
                            string fieldType = sourceFields[0].Type;

                            // If the field is a record, extract the primitive from it
                            if (fieldType.StartsWith("%") && !fieldType.EndsWith("*") && fieldType != "ptr")
                            {
                                string innerExtractTemp = GetNextTemp();
                                _output.AppendLine($"  {innerExtractTemp} = extractvalue {fieldType} {extractedTemp}, 0");
                                primitiveValue = innerExtractTemp;
                                currentType = sourcePrimitive;
                            }
                            else
                            {
                                // Field is already a primitive
                                primitiveValue = extractedTemp;
                                currentType = fieldType;
                            }
                        }
                        else
                        {
                            // No field info, assume it's a primitive
                            primitiveValue = extractedTemp;
                            currentType = sourcePrimitive;
                        }
                    }

                    // Bitcast the primitive value if types differ
                    if (currentType != primitiveType)
                    {
                        // Use alloca/store/load for bitcast
                        string srcPtr = GetNextTemp();
                        _output.AppendLine($"  {srcPtr} = alloca {currentType}");
                        _output.AppendLine($"  store {currentType} {primitiveValue}, ptr {srcPtr}");
                        string bitcastResult = GetNextTemp();
                        _output.AppendLine($"  {bitcastResult} = load {primitiveType}, ptr {srcPtr}");
                        primitiveValue = bitcastResult;
                    }

                    // If target is a struct, construct it from the primitive
                    if (toIsStruct)
                    {
                        // Check if the target struct's field is itself a record type
                        string targetRecordName = toType.TrimStart('%');
                        string fieldType = primitiveType; // Default to primitive
                        string valueToInsert = primitiveValue;

                        if (_recordFields.TryGetValue(targetRecordName, out var fields) && fields.Count > 0)
                        {
                            fieldType = fields[0].Type; // Get the actual field type

                            // If the field type is a record (starts with %), we need to wrap the primitive first
                            if (fieldType.StartsWith("%") && !fieldType.EndsWith("*") && fieldType != "ptr")
                            {
                                // Wrap the primitive value into the field record type
                                string wrappedTemp = GetNextTemp();
                                _output.AppendLine($"  {wrappedTemp} = insertvalue {fieldType} undef, {primitiveType} {primitiveValue}, 0");
                                valueToInsert = wrappedTemp;
                            }
                        }

                        _output.AppendLine($"  {resultTemp} = insertvalue {toType} undef, {fieldType} {valueToInsert}, 0");
                    }
                    else
                    {
                        // Just use the primitive value
                        resultTemp = primitiveValue;
                    }
                }
                else
                {
                    // Original bitcast logic for non-struct types
                    // Allocate space for source type
                    string srcPtr = GetNextTemp();
                    _output.AppendLine(handler: $"  {srcPtr} = alloca {actualFromType}");
                    _output.AppendLine(handler: $"  store {actualFromType} {valueTemp}, ptr {srcPtr}");

                    // Load as destination type
                    _output.AppendLine(handler: $"  {resultTemp} = load {toType}, ptr {srcPtr}");
                }
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: toType,
                    IsUnsigned: false,
                    IsFloatingPoint: toType.Contains(value: "float") ||
                                     toType.Contains(value: "double"),
                    RazorForgeType: node.TypeArguments[index: 1]);
                return resultTemp;
            }

            case "invalidate":
            {
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string ptrTemp = GetNextTemp();
                _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  call void @free(ptr {ptrTemp})");
                return ""; // void
            }

            default:
                throw new NotImplementedException(
                    message: $"Memory intrinsic {intrinsicName} not implemented");
        }
    }

    /// <summary>
    /// Ensures that floating-point constants are properly formatted for the target LLVM type.
    /// Converts hex constants between different FP formats as needed.
    /// </summary>
    private string EnsureProperFPConstant(string value, string llvmType)
    {
        // Check if this is a hex constant (starts with 0x or looks like one)
        if (!value.StartsWith("0x") && !value.StartsWith("0xL") && !value.StartsWith("0xH"))
        {
            return value; // Not a constant literal, return as-is
        }

        // Parse the hex value
        if (value.StartsWith("0xL"))
        {
            // Already fp128 format
            if (llvmType == "fp128") return value;
            // Need to convert from fp128 to other format - just use the lower 64 bits
            string hexPart = value.Substring(3); // Remove "0xL"
            if (hexPart.Length > 16)
            {
                hexPart = hexPart.Substring(hexPart.Length - 16); // Get lower 64 bits
            }
            return $"0x{hexPart}";
        }
        else if (value.StartsWith("0xH"))
        {
            // Already half format
            if (llvmType == "half") return value;
            // For other types, this should not happen in normal use
            return value;
        }
        else if (value.StartsWith("0x"))
        {
            // Double/float hex format (64-bit)
            if (llvmType == "fp128")
            {
                // Need to convert to fp128 format - extend with zeros
                string hexPart = value.Substring(2); // Remove "0x"
                // Pad to 16 hex digits if needed
                hexPart = hexPart.PadLeft(16, '0');
                return $"0xL{new string('0', 16)}{hexPart}";
            }
            else if (llvmType == "half")
            {
                // Convert to half - extract lower 16 bits (simplified)
                string hexPart = value.Substring(2);
                if (hexPart.Length > 4)
                {
                    hexPart = hexPart.Substring(hexPart.Length - 4);
                }
                return $"0xH{hexPart.PadLeft(4, '0')}";
            }
            // Already in double/float format
            return value;
        }

        return value;
    }

    private string EmitArithmeticIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "arithmetic intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        // Check if llvmType is a record wrapper - if so, get the primitive type for actual operations
        string primitiveType = llvmType;
        bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";
        if (isRecordType)
        {
            primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
        }

        // Determine if type is unsigned or signed
        bool isUnsigned = node.TypeArguments.Count > 0 && node.TypeArguments[index: 0]
                                                              .StartsWith(value: "u");
        bool isFloat = primitiveType.Contains(value: "float") || primitiveType.Contains(value: "double") ||
                       primitiveType.Contains(value: "half") || primitiveType.Contains(value: "fp128");

        // Handle unary neg operation
        if (intrinsicName == "neg")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);

            // Extract from operand if it's a struct
            LLVMTypeInfo? valueTypeInfo = null;
            if (_tempTypes.TryGetValue(valueTemp, out var tempValueType))
            {
                valueTypeInfo = tempValueType;
            }
            // Also check _symbolTypes for parameters (like 'me')
            else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolValueType))
            {
                valueTypeInfo = new LLVMTypeInfo(LLVMType: symbolValueType, IsUnsigned: false, IsFloatingPoint: false);
            }

            if (valueTypeInfo != null && valueTypeInfo.LLVMType.StartsWith("%") && !valueTypeInfo.LLVMType.EndsWith("*") && valueTypeInfo.LLVMType != "ptr")
            {
                string extractedValue = GetNextTemp();
                _output.AppendLine($"  {extractedValue} = extractvalue {valueTypeInfo.LLVMType} {valueTemp}, 0");
                valueTemp = extractedValue;
            }

            // Perform the negation on primitives
            if (isFloat)
            {
                _output.AppendLine(handler: $"  {resultTemp} = fneg {primitiveType} {valueTemp}");
            }
            else
            {
                _output.AppendLine(handler: $"  {resultTemp} = sub {primitiveType} 0, {valueTemp}");
            }

            // Always wrap the result if the type argument was a record (which it now always is for RazorForge types)
            if (isRecordType)
            {
                string wrappedTemp = GetNextTemp();
                _output.AppendLine($"  {wrappedTemp} = insertvalue {llvmType} undef, {primitiveType} {resultTemp}, 0");
                _tempTypes[key: wrappedTemp] = new LLVMTypeInfo(LLVMType: llvmType,
                    IsUnsigned: isUnsigned,
                    IsFloatingPoint: isFloat,
                    RazorForgeType: node.TypeArguments[index: 0]);
                return wrappedTemp;
            }

            _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: primitiveType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: node.TypeArguments[index: 0]);
            return resultTemp;
        }

        // Binary operations need two arguments
        string leftTemp = node.Arguments[index: 0]
                              .Accept(visitor: this);
        string rightTemp = node.Arguments[index: 1]
                               .Accept(visitor: this);

        // Check if operands are struct types and need to be extracted
        // The intrinsic operates on primitives, but arguments might be wrapped in records
        // Extract from left operand if it's a struct
        LLVMTypeInfo? leftTypeInfo = null;
        if (_tempTypes.TryGetValue(leftTemp, out var tempLeftType))
        {
            leftTypeInfo = tempLeftType;
        }
        // Also check _symbolTypes for parameters (like 'me')
        else if (leftTemp.StartsWith("%") && _symbolTypes.TryGetValue(leftTemp.Substring(1), out var symbolLeftType))
        {
            leftTypeInfo = new LLVMTypeInfo(LLVMType: symbolLeftType, IsUnsigned: false, IsFloatingPoint: false);
        }

        if (leftTypeInfo != null && leftTypeInfo.LLVMType.StartsWith("%") && !leftTypeInfo.LLVMType.EndsWith("*") && leftTypeInfo.LLVMType != "ptr")
        {
            string extractedLeft = GetNextTemp();
            _output.AppendLine($"  {extractedLeft} = extractvalue {leftTypeInfo.LLVMType} {leftTemp}, 0");
            leftTemp = extractedLeft;
        }

        // Extract from right operand if it's a struct
        LLVMTypeInfo? rightTypeInfo = null;
        if (_tempTypes.TryGetValue(rightTemp, out var tempRightType))
        {
            rightTypeInfo = tempRightType;
        }
        // Also check _symbolTypes for parameters
        else if (rightTemp.StartsWith("%") && _symbolTypes.TryGetValue(rightTemp.Substring(1), out var symbolRightType))
        {
            rightTypeInfo = new LLVMTypeInfo(LLVMType: symbolRightType, IsUnsigned: false, IsFloatingPoint: false);
        }

        if (rightTypeInfo != null && rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr")
        {
            string extractedRight = GetNextTemp();
            _output.AppendLine($"  {extractedRight} = extractvalue {rightTypeInfo.LLVMType} {rightTemp}, 0");
            rightTemp = extractedRight;
        }

        // For floating-point types, ensure constants are properly formatted
        // This handles cases like 0.0_f128 which might be evaluated as double hex format
        if (isFloat)
        {
            leftTemp = EnsureProperFPConstant(leftTemp, primitiveType);
            rightTemp = EnsureProperFPConstant(rightTemp, primitiveType);
        }

        switch (intrinsicName)
        {
            // Basic arithmetic (trapping on overflow for integers, IEEE for floats)
            // For integers, we use overflow intrinsics and trap if overflow occurs
            case "add" when isFloat:
                _output.AppendLine(
                    handler: $"  {resultTemp} = fadd {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "add":
            {
                // Use overflow intrinsic and trap on overflow
                string llvmFunc = isUnsigned
                    ? $"@llvm.uadd.with.overflow.{primitiveType}"
                    : $"@llvm.sadd.with.overflow.{primitiveType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {primitiveType}, i1 }} {llvmFunc}({primitiveType} {leftTemp}, {primitiveType} {rightTemp})");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {overflowFlag} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 1");
                // Trap on overflow
                string trapLabel = $"trap.add.{_tempCounter}";
                string contLabel = $"cont.add.{_tempCounter}";
                _output.AppendLine(
                    handler: $"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine(handler: $"{trapLabel}:");
                _output.AppendLine(value: $"  call void @llvm.trap()");
                _output.AppendLine(value: $"  unreachable");
                _output.AppendLine(handler: $"{contLabel}:");
                break;
            }
            case "sub" when isFloat:
                _output.AppendLine(
                    handler: $"  {resultTemp} = fsub {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "sub":
            {
                string llvmFunc = isUnsigned
                    ? $"@llvm.usub.with.overflow.{primitiveType}"
                    : $"@llvm.ssub.with.overflow.{primitiveType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {primitiveType}, i1 }} {llvmFunc}({primitiveType} {leftTemp}, {primitiveType} {rightTemp})");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {overflowFlag} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 1");
                string trapLabel = $"trap.sub.{_tempCounter}";
                string contLabel = $"cont.sub.{_tempCounter}";
                _output.AppendLine(
                    handler: $"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine(handler: $"{trapLabel}:");
                _output.AppendLine(value: $"  call void @llvm.trap()");
                _output.AppendLine(value: $"  unreachable");
                _output.AppendLine(handler: $"{contLabel}:");
                break;
            }
            case "mul" when isFloat:
                _output.AppendLine(
                    handler: $"  {resultTemp} = fmul {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "mul":
            {
                string llvmFunc = isUnsigned
                    ? $"@llvm.umul.with.overflow.{primitiveType}"
                    : $"@llvm.smul.with.overflow.{primitiveType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {primitiveType}, i1 }} {llvmFunc}({primitiveType} {leftTemp}, {primitiveType} {rightTemp})");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {overflowFlag} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 1");
                string trapLabel = $"trap.mul.{_tempCounter}";
                string contLabel = $"cont.mul.{_tempCounter}";
                _output.AppendLine(
                    handler: $"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine(handler: $"{trapLabel}:");
                _output.AppendLine(value: $"  call void @llvm.trap()");
                _output.AppendLine(value: $"  unreachable");
                _output.AppendLine(handler: $"{contLabel}:");
                break;
            }
            case "sdiv":
                _output.AppendLine(
                    handler: $"  {resultTemp} = sdiv {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "udiv":
                _output.AppendLine(
                    handler: $"  {resultTemp} = udiv {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "srem":
                _output.AppendLine(
                    handler: $"  {resultTemp} = srem {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "urem":
                _output.AppendLine(
                    handler: $"  {resultTemp} = urem {primitiveType} {leftTemp}, {rightTemp}");
                break;
            // Wrapping arithmetic (no overflow checks - uses LLVM's default wrapping behavior)
            case "add.wrapping":
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = {(isFloat ? "fadd" : "add")} {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "sub.wrapping":
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = {(isFloat ? "fsub" : "sub")} {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "mul.wrapping":
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = {(isFloat ? "fmul" : "mul")} {primitiveType} {leftTemp}, {rightTemp}");
                break;
            case "div.wrapping":
            {
                string divOp = isFloat ? "fdiv" : isUnsigned ? "udiv" : "sdiv";
                _output.AppendLine(
                    handler: $"  {resultTemp} = {divOp} {primitiveType} {leftTemp}, {rightTemp}");
                break;
            }
            case "rem.wrapping":
            {
                string remOp = isFloat ? "frem" : isUnsigned ? "urem" : "srem";
                _output.AppendLine(
                    handler: $"  {resultTemp} = {remOp} {primitiveType} {leftTemp}, {rightTemp}");
                break;
            }
            // Overflow-checking arithmetic
            case "add.overflow":
            case "sub.overflow":
            case "mul.overflow":
            {
                string op = intrinsicName.Split(separator: '.')[0]; // "add", "sub", or "mul"
                string llvmFunc = isUnsigned
                    ? $"@llvm.u{op}.with.overflow.{llvmType}"
                    : $"@llvm.s{op}.with.overflow.{llvmType}";

                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {primitiveType}, i1 }} {llvmFunc}({primitiveType} {leftTemp}, {llvmType} {rightTemp})");

                string valueTemp = GetNextTemp();
                string overflowTemp = GetNextTemp();
                _output.AppendLine(
                    handler: $"  {valueTemp} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 0");
                _output.AppendLine(
                    handler: $"  {overflowTemp} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 1");

                // For now, just return the value (tuple support would need more work)
                _tempTypes[key: valueTemp] = new LLVMTypeInfo(LLVMType: llvmType,
                    IsUnsigned: isUnsigned,
                    IsFloatingPoint: isFloat,
                    RazorForgeType: node.TypeArguments[index: 0]);
                return valueTemp;
            }
            // Saturating arithmetic
            case "add.saturating":
            {
                string llvmFunc = isUnsigned
                    ? $"@llvm.uadd.sat.{llvmType}"
                    : $"@llvm.sadd.sat.{llvmType}";
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call {llvmType} {llvmFunc}({primitiveType} {leftTemp}, {llvmType} {rightTemp})");
                break;
            }
            case "sub.saturating":
            {
                string llvmFunc = isUnsigned
                    ? $"@llvm.usub.sat.{llvmType}"
                    : $"@llvm.ssub.sat.{llvmType}";
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call {llvmType} {llvmFunc}({primitiveType} {leftTemp}, {llvmType} {rightTemp})");
                break;
            }
            case "mul.saturating":
            {
                // LLVM doesn't have direct saturating multiply, so we use overflow detection
                // For now, use a placeholder that traps on overflow (TODO: implement proper saturation)
                string llvmFunc = isUnsigned
                    ? $"@llvm.umul.with.overflow.{primitiveType}"
                    : $"@llvm.smul.with.overflow.{primitiveType}";
                string structTemp = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {structTemp} = call {{ {primitiveType}, i1 }} {llvmFunc}({primitiveType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine(
                    handler: $"  {resultTemp} = extractvalue {{ {primitiveType}, i1 }} {structTemp}, 0");
                // TODO: Check overflow flag and saturate to MAX/MIN
                break;
            }
            default:
                throw new NotImplementedException(
                    message: $"Arithmetic intrinsic {intrinsicName} not implemented");
        }

        // Determine if we need to wrap the result back into a struct type
        // If either operand was a struct, we need to wrap the result
        string wrapperStructType = null;
        if (leftTypeInfo != null && leftTypeInfo.LLVMType.StartsWith("%") && !leftTypeInfo.LLVMType.EndsWith("*") && leftTypeInfo.LLVMType != "ptr")
        {
            wrapperStructType = leftTypeInfo.LLVMType;
        }
        else if (rightTypeInfo != null && rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr")
        {
            wrapperStructType = rightTypeInfo.LLVMType;
        }

        // If we extracted from a struct, wrap the result back
        // Always wrap the result if the type argument was a record (which it now always is for RazorForge types)
        if (isRecordType)
        {
            string wrappedTemp = GetNextTemp();
            _output.AppendLine($"  {wrappedTemp} = insertvalue {llvmType} undef, {primitiveType} {resultTemp}, 0");
            _tempTypes[key: wrappedTemp] = new LLVMTypeInfo(LLVMType: llvmType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: node.TypeArguments[index: 0]);
            return wrappedTemp;
        }

        // For raw LLVM types (i1, i8, etc. used directly), return the primitive
        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: primitiveType,
            IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloat,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitComparisonIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "comparison intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        // Check if llvmType is a record wrapper - if so, get the primitive type for actual operations
        string primitiveType = llvmType;
        bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";
        if (isRecordType)
        {
            primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
        }

        string leftTemp = node.Arguments[index: 0]
                              .Accept(visitor: this);
        string rightTemp = node.Arguments[index: 1]
                               .Accept(visitor: this);

        // Extract from left operand if it's a struct
        LLVMTypeInfo? leftTypeInfo = null;
        if (_tempTypes.TryGetValue(leftTemp, out var tempLeftType))
        {
            leftTypeInfo = tempLeftType;
        }
        // Also check _symbolTypes for parameters (like 'me')
        else if (leftTemp.StartsWith("%") && _symbolTypes.TryGetValue(leftTemp.Substring(1), out var symbolLeftType))
        {
            leftTypeInfo = new LLVMTypeInfo(LLVMType: symbolLeftType, IsUnsigned: false, IsFloatingPoint: false);
        }

        if (leftTypeInfo != null && leftTypeInfo.LLVMType.StartsWith("%") && !leftTypeInfo.LLVMType.EndsWith("*") && leftTypeInfo.LLVMType != "ptr")
        {
            string extractedLeft = GetNextTemp();
            _output.AppendLine($"  {extractedLeft} = extractvalue {leftTypeInfo.LLVMType} {leftTemp}, 0");
            leftTemp = extractedLeft;
        }

        // Extract from right operand if it's a struct
        LLVMTypeInfo? rightTypeInfo = null;
        if (_tempTypes.TryGetValue(rightTemp, out var tempRightType))
        {
            rightTypeInfo = tempRightType;
        }
        // Also check _symbolTypes for parameters
        else if (rightTemp.StartsWith("%") && _symbolTypes.TryGetValue(rightTemp.Substring(1), out var symbolRightType))
        {
            rightTypeInfo = new LLVMTypeInfo(LLVMType: symbolRightType, IsUnsigned: false, IsFloatingPoint: false);
        }

        if (rightTypeInfo != null && rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr")
        {
            string extractedRight = GetNextTemp();
            _output.AppendLine($"  {extractedRight} = extractvalue {rightTypeInfo.LLVMType} {rightTemp}, 0");
            rightTemp = extractedRight;
        }

        // Extract comparison predicate from intrinsic name
        // e.g., "icmp.eq", "icmp.slt", "fcmp.oeq"
        string[] parts = intrinsicName.Split(separator: '.');
        string cmpType = parts[0]; // "icmp" or "fcmp"
        string predicate = parts[1]; // "eq", "slt", "oeq", etc.

        if (cmpType == "icmp")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = icmp {predicate} {primitiveType} {leftTemp}, {rightTemp}");
        }
        else if (cmpType == "fcmp")
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = fcmp {predicate} {primitiveType} {leftTemp}, {rightTemp}");
        }

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i1",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: "bool");
        return resultTemp;
    }

    private string EmitBitwiseIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "bitwise intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        // Check if llvmType is a record wrapper - if so, get the primitive type for actual operations
        string primitiveType = llvmType;
        bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";
        if (isRecordType)
        {
            primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
        }

        if (intrinsicName == "not")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);

            // Extract from operand if it's a struct
            LLVMTypeInfo? valueTypeInfo = null;
            if (_tempTypes.TryGetValue(valueTemp, out var tempValueType))
            {
                valueTypeInfo = tempValueType;
            }
            // Also check _symbolTypes for parameters (like 'me')
            else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolValueType))
            {
                valueTypeInfo = new LLVMTypeInfo(LLVMType: symbolValueType, IsUnsigned: false, IsFloatingPoint: false);
            }

            if (valueTypeInfo != null && valueTypeInfo.LLVMType.StartsWith("%") && !valueTypeInfo.LLVMType.EndsWith("*") && valueTypeInfo.LLVMType != "ptr")
            {
                string extractedValue = GetNextTemp();
                _output.AppendLine($"  {extractedValue} = extractvalue {valueTypeInfo.LLVMType} {valueTemp}, 0");
                valueTemp = extractedValue;
                // Update llvmType to match the extracted primitive type
                llvmType = InferPrimitiveTypeFromRecordName(valueTypeInfo.LLVMType);
            }

            _output.AppendLine(handler: $"  {resultTemp} = xor {llvmType} {valueTemp}, -1");
        }
        else if (intrinsicName == "and" || intrinsicName == "or" || intrinsicName == "xor")
        {
            string leftTemp = node.Arguments[index: 0]
                                  .Accept(visitor: this);
            string rightTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);

            // Extract from left operand if it's a struct
            LLVMTypeInfo? leftTypeInfo = null;
            if (_tempTypes.TryGetValue(leftTemp, out var tempLeftType))
            {
                leftTypeInfo = tempLeftType;
            }
            // Also check _symbolTypes for parameters (like 'me')
            else if (leftTemp.StartsWith("%") && _symbolTypes.TryGetValue(leftTemp.Substring(1), out var symbolLeftType))
            {
                leftTypeInfo = new LLVMTypeInfo(LLVMType: symbolLeftType, IsUnsigned: false, IsFloatingPoint: false);
            }

            if (leftTypeInfo != null && leftTypeInfo.LLVMType.StartsWith("%") && !leftTypeInfo.LLVMType.EndsWith("*") && leftTypeInfo.LLVMType != "ptr")
            {
                string extractedLeft = GetNextTemp();
                _output.AppendLine($"  {extractedLeft} = extractvalue {leftTypeInfo.LLVMType} {leftTemp}, 0");
                leftTemp = extractedLeft;
                // Update llvmType to match the extracted primitive type
                llvmType = InferPrimitiveTypeFromRecordName(leftTypeInfo.LLVMType);
            }

            // Extract from right operand if it's a struct
            LLVMTypeInfo? rightTypeInfo = null;
            if (_tempTypes.TryGetValue(rightTemp, out var tempRightType))
            {
                rightTypeInfo = tempRightType;
            }
            // Also check _symbolTypes for parameters
            else if (rightTemp.StartsWith("%") && _symbolTypes.TryGetValue(rightTemp.Substring(1), out var symbolRightType))
            {
                rightTypeInfo = new LLVMTypeInfo(LLVMType: symbolRightType, IsUnsigned: false, IsFloatingPoint: false);
            }

            if (rightTypeInfo != null && rightTypeInfo.LLVMType.StartsWith("%") && !rightTypeInfo.LLVMType.EndsWith("*") && rightTypeInfo.LLVMType != "ptr")
            {
                string extractedRight = GetNextTemp();
                _output.AppendLine($"  {extractedRight} = extractvalue {rightTypeInfo.LLVMType} {rightTemp}, 0");
                rightTemp = extractedRight;
            }

            _output.AppendLine(
                handler: $"  {resultTemp} = {intrinsicName} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "shl" || intrinsicName == "lshr" || intrinsicName == "ashr")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);
            string amountTemp = node.Arguments[index: 1]
                                    .Accept(visitor: this);

            // Extract from value operand if it's a struct
            LLVMTypeInfo? valueTypeInfo = null;
            if (_tempTypes.TryGetValue(valueTemp, out var tempValueType))
            {
                valueTypeInfo = tempValueType;
            }
            // Also check _symbolTypes for parameters (like 'me')
            else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolValueType))
            {
                valueTypeInfo = new LLVMTypeInfo(LLVMType: symbolValueType, IsUnsigned: false, IsFloatingPoint: false);
            }

            if (valueTypeInfo != null && valueTypeInfo.LLVMType.StartsWith("%") && !valueTypeInfo.LLVMType.EndsWith("*") && valueTypeInfo.LLVMType != "ptr")
            {
                string extractedValue = GetNextTemp();
                _output.AppendLine($"  {extractedValue} = extractvalue {valueTypeInfo.LLVMType} {valueTemp}, 0");
                valueTemp = extractedValue;
                // Update llvmType to match the extracted primitive type
                llvmType = InferPrimitiveTypeFromRecordName(valueTypeInfo.LLVMType);
            }

            // Extract from amount operand if it's a struct
            LLVMTypeInfo? amountTypeInfoStruct = null;
            if (_tempTypes.TryGetValue(amountTemp, out var tempAmountType))
            {
                amountTypeInfoStruct = tempAmountType;
            }
            // Also check _symbolTypes for parameters
            else if (amountTemp.StartsWith("%") && _symbolTypes.TryGetValue(amountTemp.Substring(1), out var symbolAmountType))
            {
                amountTypeInfoStruct = new LLVMTypeInfo(LLVMType: symbolAmountType, IsUnsigned: false, IsFloatingPoint: false);
            }

            if (amountTypeInfoStruct != null && amountTypeInfoStruct.LLVMType.StartsWith("%") && !amountTypeInfoStruct.LLVMType.EndsWith("*") && amountTypeInfoStruct.LLVMType != "ptr")
            {
                string extractedAmount = GetNextTemp();
                _output.AppendLine($"  {extractedAmount} = extractvalue {amountTypeInfoStruct.LLVMType} {amountTemp}, 0");
                amountTemp = extractedAmount;
            }

            // LLVM shift instructions require both operands to have the same type
            // If the shift amount has a different type, we need to extend/truncate it
            LLVMTypeInfo amountTypeInfo = GetValueTypeInfo(value: amountTemp);
            string finalAmountTemp = amountTemp;

            if (amountTypeInfo.LLVMType != llvmType)
            {
                // Need to convert shift amount to match the value type
                int valueBits = GetIntegerBitWidth(llvmType: llvmType);
                int amountBits = GetIntegerBitWidth(llvmType: amountTypeInfo.LLVMType);

                if (amountBits < valueBits)
                {
                    // Extend shift amount (always zero-extend for shift amounts)
                    string extTemp = GetNextTemp();
                    _output.AppendLine(
                        handler: $"  {extTemp} = zext {amountTypeInfo.LLVMType} {amountTemp} to {llvmType}");
                    finalAmountTemp = extTemp;
                }
                else if (amountBits > valueBits)
                {
                    // Truncate shift amount
                    string truncTemp = GetNextTemp();
                    _output.AppendLine(
                        handler: $"  {truncTemp} = trunc {amountTypeInfo.LLVMType} {amountTemp} to {llvmType}");
                    finalAmountTemp = truncTemp;
                }
            }

            _output.AppendLine(
                handler: $"  {resultTemp} = {intrinsicName} {llvmType} {valueTemp}, {finalAmountTemp}");
        }
        else
        {
            throw new NotImplementedException(
                message: $"Bitwise intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitConversionIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string fromType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);
        string toType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 1]);

        // Get primitive types for actual conversion
        string fromPrimitive = fromType.StartsWith("%") && !fromType.EndsWith("*") && fromType != "ptr"
            ? InferPrimitiveTypeFromRecordName(fromType)
            : fromType;
        string toPrimitive = toType.StartsWith("%") && !toType.EndsWith("*") && toType != "ptr"
            ? InferPrimitiveTypeFromRecordName(toType)
            : toType;
        bool toIsRecord = toType.StartsWith("%") && !toType.EndsWith("*") && toType != "ptr";

        string valueTemp = node.Arguments[index: 0]
                               .Accept(visitor: this);

        // Extract if value is a record
        LLVMTypeInfo? valueTypeInfo = null;
        if (_tempTypes.TryGetValue(valueTemp, out var tempType))
        {
            valueTypeInfo = tempType;
        }
        else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolType))
        {
            valueTypeInfo = new LLVMTypeInfo(LLVMType: symbolType, IsUnsigned: false, IsFloatingPoint: false);
        }

        if (valueTypeInfo != null && valueTypeInfo.LLVMType.StartsWith("%") && !valueTypeInfo.LLVMType.EndsWith("*") && valueTypeInfo.LLVMType != "ptr")
        {
            string extracted = GetNextTemp();
            _output.AppendLine($"  {extracted} = extractvalue {valueTypeInfo.LLVMType} {valueTemp}, 0");
            valueTemp = extracted;
        }

        _output.AppendLine(
            handler: $"  {resultTemp} = {intrinsicName} {fromPrimitive} {valueTemp} to {toPrimitive}");

        bool isFloat = toPrimitive.Contains(value: "float") || toPrimitive.Contains(value: "double") ||
                       toPrimitive.Contains(value: "half");
        bool isUnsigned = node.TypeArguments[index: 1]
                              .StartsWith(value: "u");

        // Wrap result if target is a record
        if (toIsRecord)
        {
            string wrapped = GetNextTemp();
            _output.AppendLine($"  {wrapped} = insertvalue {toType} undef, {toPrimitive} {resultTemp}, 0");
            _tempTypes[key: wrapped] = new LLVMTypeInfo(LLVMType: toType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: node.TypeArguments[index: 1]);
            return wrapped;
        }

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: toPrimitive,
            IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloat,
            RazorForgeType: node.TypeArguments[index: 1]);
        return resultTemp;
    }

    private string EmitMathIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "math intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        // Check if llvmType is a record wrapper - if so, get the primitive type for actual operations
        string primitiveType = llvmType;
        bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";
        if (isRecordType)
        {
            primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
        }

        if (intrinsicName == "sqrt" || intrinsicName == "fabs" || intrinsicName == "floor" ||
            intrinsicName == "ceil" || intrinsicName == "trunc_float" ||
            intrinsicName == "round" || intrinsicName == "exp" || intrinsicName == "log" ||
            intrinsicName == "log10" || intrinsicName == "sin" || intrinsicName == "cos")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);

            // Check the ACTUAL type of the argument value, not just the type argument
            string actualArgType = null;
            if (_tempTypes.TryGetValue(valueTemp, out var argTypeInfo))
            {
                actualArgType = argTypeInfo.LLVMType;
            }
            else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolType))
            {
                actualArgType = symbolType;
            }

            // Extract primitive if the argument is actually a record type
            bool actualIsRecord = actualArgType != null && actualArgType.StartsWith("%") &&
                                  !actualArgType.EndsWith("*") && actualArgType != "ptr";
            if (actualIsRecord)
            {
                string extractedValue = GetNextTemp();
                _output.AppendLine($"  {extractedValue} = extractvalue {actualArgType} {valueTemp}, 0");
                valueTemp = extractedValue;
            }

            string llvmFunc = intrinsicName == "trunc_float"
                ? "trunc"
                : intrinsicName;
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {primitiveType} @llvm.{llvmFunc}.{primitiveType}({primitiveType} {valueTemp})");

            // Wrap result if return type is a record
            if (isRecordType)
            {
                string wrappedTemp = GetNextTemp();
                _output.AppendLine($"  {wrappedTemp} = insertvalue {llvmType} undef, {primitiveType} {resultTemp}, 0");
                resultTemp = wrappedTemp;
            }
        }
        else if (intrinsicName == "abs")
        {
            string valueTemp = node.Arguments[index: 0]
                                   .Accept(visitor: this);

            // Check the ACTUAL type of the argument value
            string actualArgType = null;
            if (_tempTypes.TryGetValue(valueTemp, out var argTypeInfo))
            {
                actualArgType = argTypeInfo.LLVMType;
            }
            else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolType))
            {
                actualArgType = symbolType;
            }

            // Extract primitive if the argument is actually a record type
            bool actualIsRecord = actualArgType != null && actualArgType.StartsWith("%") &&
                                  !actualArgType.EndsWith("*") && actualArgType != "ptr";
            if (actualIsRecord)
            {
                string extractedValue = GetNextTemp();
                _output.AppendLine($"  {extractedValue} = extractvalue {actualArgType} {valueTemp}, 0");
                valueTemp = extractedValue;
            }

            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {primitiveType} @llvm.abs.{primitiveType}({primitiveType} {valueTemp}, i1 false)");

            // Wrap result if return type is a record
            if (isRecordType)
            {
                string wrappedTemp = GetNextTemp();
                _output.AppendLine($"  {wrappedTemp} = insertvalue {llvmType} undef, {primitiveType} {resultTemp}, 0");
                resultTemp = wrappedTemp;
            }
        }
        else if (intrinsicName == "copysign" || intrinsicName == "pow")
        {
            string leftTemp = node.Arguments[index: 0]
                                  .Accept(visitor: this);
            string rightTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);

            // Check the ACTUAL type of the left argument
            string actualLeftType = null;
            if (_tempTypes.TryGetValue(leftTemp, out var leftArgTypeInfo))
            {
                actualLeftType = leftArgTypeInfo.LLVMType;
            }
            else if (leftTemp.StartsWith("%") && _symbolTypes.TryGetValue(leftTemp.Substring(1), out var leftSymbolType))
            {
                actualLeftType = leftSymbolType;
            }

            // Extract primitive if the left argument is actually a record type
            bool leftIsRecord = actualLeftType != null && actualLeftType.StartsWith("%") &&
                               !actualLeftType.EndsWith("*") && actualLeftType != "ptr";
            if (leftIsRecord)
            {
                string extractedLeft = GetNextTemp();
                _output.AppendLine($"  {extractedLeft} = extractvalue {actualLeftType} {leftTemp}, 0");
                leftTemp = extractedLeft;
            }

            // Check the ACTUAL type of the right argument
            string actualRightType = null;
            if (_tempTypes.TryGetValue(rightTemp, out var rightArgTypeInfo))
            {
                actualRightType = rightArgTypeInfo.LLVMType;
            }
            else if (rightTemp.StartsWith("%") && _symbolTypes.TryGetValue(rightTemp.Substring(1), out var rightSymbolType))
            {
                actualRightType = rightSymbolType;
            }

            // Extract primitive if the right argument is actually a record type
            bool rightIsRecord = actualRightType != null && actualRightType.StartsWith("%") &&
                                !actualRightType.EndsWith("*") && actualRightType != "ptr";
            if (rightIsRecord)
            {
                string extractedRight = GetNextTemp();
                _output.AppendLine($"  {extractedRight} = extractvalue {actualRightType} {rightTemp}, 0");
                rightTemp = extractedRight;
            }

            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {primitiveType} @llvm.{intrinsicName}.{primitiveType}({primitiveType} {leftTemp}, {primitiveType} {rightTemp})");

            // Wrap result if return type is a record
            if (isRecordType)
            {
                string wrappedTemp = GetNextTemp();
                _output.AppendLine($"  {wrappedTemp} = insertvalue {llvmType} undef, {primitiveType} {resultTemp}, 0");
                resultTemp = wrappedTemp;
            }
        }
        else
        {
            throw new NotImplementedException(
                message: $"Math intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: true,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitAtomicIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "atomic intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        // Check if llvmType is a record wrapper - if so, get the primitive type for actual operations
        string primitiveType = llvmType;
        bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";
        if (isRecordType)
        {
            primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
        }

        string addrTemp = node.Arguments[index: 0]
                              .Accept(visitor: this);
        string ptrTemp = GetNextTemp();
        _output.AppendLine(handler: $"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");

        if (intrinsicName == "atomic.load")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = load atomic {llvmType}, ptr {ptrTemp} seq_cst, align 8");
        }
        else if (intrinsicName == "atomic.store")
        {
            string valueTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);
            _output.AppendLine(
                handler: $"  store atomic {llvmType} {valueTemp}, ptr {ptrTemp} seq_cst, align 8");
            return ""; // void
        }
        else if (intrinsicName == "atomic.add" || intrinsicName == "atomic.sub" ||
                 intrinsicName == "atomic.xchg")
        {
            string valueTemp = node.Arguments[index: 1]
                                   .Accept(visitor: this);
            string op = intrinsicName.Split(separator: '.')[1]; // "add", "sub", "xchg"
            _output.AppendLine(
                handler:
                $"  {resultTemp} = atomicrmw {op} ptr {ptrTemp}, {llvmType} {valueTemp} seq_cst");
        }
        else if (intrinsicName == "atomic.cmpxchg")
        {
            string expectedTemp = node.Arguments[index: 1]
                                      .Accept(visitor: this);
            string desiredTemp = node.Arguments[index: 2]
                                     .Accept(visitor: this);

            string cmpxchgTemp = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {cmpxchgTemp} = cmpxchg ptr {ptrTemp}, {llvmType} {expectedTemp}, {llvmType} {desiredTemp} seq_cst seq_cst");

            // Extract old value and success flag (tuple support needed)
            _output.AppendLine(
                handler: $"  {resultTemp} = extractvalue {{ {primitiveType}, i1 }} {cmpxchgTemp}, 0");
        }
        else
        {
            throw new NotImplementedException(
                message: $"Atomic intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitBitManipIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "bit manipulation intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[index: 0]);

        // Check if llvmType is a record wrapper - if so, get the primitive type for actual operations
        string primitiveType = llvmType;
        bool isRecordType = llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr";
        if (isRecordType)
        {
            primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
        }

        string valueTemp = node.Arguments[index: 0]
                               .Accept(visitor: this);

        // Check the ACTUAL type of the argument value, not just the type argument
        string actualArgType = null;
        if (_tempTypes.TryGetValue(valueTemp, out var argTypeInfo))
        {
            actualArgType = argTypeInfo.LLVMType;
        }
        else if (valueTemp.StartsWith("%") && _symbolTypes.TryGetValue(valueTemp.Substring(1), out var symbolType))
        {
            actualArgType = symbolType;
        }

        // Extract primitive if the argument is actually a record type
        bool actualIsRecord = actualArgType != null && actualArgType.StartsWith("%") &&
                              !actualArgType.EndsWith("*") && actualArgType != "ptr";
        if (actualIsRecord)
        {
            string extractedValue = GetNextTemp();
            _output.AppendLine($"  {extractedValue} = extractvalue {actualArgType} {valueTemp}, 0");
            valueTemp = extractedValue;
        }

        if (intrinsicName == "ctlz" || intrinsicName == "cttz")
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {primitiveType} @llvm.{intrinsicName}.{primitiveType}({primitiveType} {valueTemp}, i1 false)");
        }
        else
        {
            _output.AppendLine(
                handler:
                $"  {resultTemp} = call {primitiveType} @llvm.{intrinsicName}.{primitiveType}({primitiveType} {valueTemp})");
        }

        // Wrap result if return type is a record
        if (isRecordType)
        {
            string wrappedTemp = GetNextTemp();
            _output.AppendLine($"  {wrappedTemp} = insertvalue {llvmType} undef, {primitiveType} {resultTemp}, 0");
            resultTemp = wrappedTemp;
        }

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: llvmType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: node.TypeArguments[index: 0]);
        return resultTemp;
    }

    private string EmitTypeInfoIntrinsic(IntrinsicCallExpression node, string resultTemp, string intrinsicName)
    {
        // sizeof<T>() and alignof<T>() return the size/alignment of the type in bytes
        if (node.TypeArguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: intrinsicName,
                context: "sizeof/alignof intrinsic requires a type argument",
                file: _currentFileName,
                line: node.Location.Line,
                column: node.Location.Column,
                position: node.Location.Position);
        }
        string typeArg = node.TypeArguments[index: 0];

        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: typeArg);

        // Get the size or alignment of the type
        int value = intrinsicName == "sizeof"
            ? GetTypeSizeInBytes(llvmType: llvmType)
            : GetTypeAlignment(llvmType: llvmType);

        // Return as a constant uaddr (i64)
        _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {value}");

        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
            IsUnsigned: true,
            IsFloatingPoint: false,
            RazorForgeType: "uaddr");
        return resultTemp;
    }

    /// <summary>
    /// Gets the size of an LLVM type in bytes.
    /// </summary>
    private static int GetTypeSizeInBytes(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 1,
            "i16" => 2,
            "i32" => 4,
            "i64" => 8,
            "i128" => 16,
            "half" => 2,
            "float" => 4,
            "double" => 8,
            "fp128" => 16,
            "ptr" => 8, // Assume 64-bit pointers
            _ => 8 // Default to 8 bytes for unknown types
        };
    }

    /// <summary>
    /// Gets the alignment of an LLVM type in bytes.
    /// </summary>
    private static int GetTypeAlignment(string llvmType)
    {
        // For most types, alignment equals size up to 8 bytes
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 1,
            "i16" => 2,
            "i32" => 4,
            "i64" => 8,
            "i128" => 16,
            "half" => 2,
            "float" => 4,
            "double" => 8,
            "fp128" => 16,
            "ptr" => 8,
            _ => 8
        };
    }

    #endregion
}