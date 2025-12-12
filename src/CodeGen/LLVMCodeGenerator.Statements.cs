using Compilers.Shared.AST;
using Compilers.Shared.Errors;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    // If statement
    public string VisitIfStatement(IfStatement node)
    {
        UpdateLocation(node.Location);
        string condition = node.Condition.Accept(visitor: this);
        string thenLabel = GetNextLabel();
        string elseLabel = GetNextLabel();
        string endLabel = GetNextLabel();

        // If condition is a %bool record, extract the primitive i1
        if (_tempTypes.TryGetValue(condition, out var condTypeInfo) &&
            condTypeInfo.LLVMType == "%bool")
        {
            string extractedCond = GetNextTemp();
            _output.AppendLine($"  {extractedCond} = extractvalue %bool {condition}, 0");
            condition = extractedCond;
        }

        _output.AppendLine(
            handler: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then block
        _output.AppendLine(handler: $"{thenLabel}:");
        _blockTerminated = false;
        node.ThenStatement.Accept(visitor: this);
        bool thenTerminated = _blockTerminated;
        if (!thenTerminated)
        {
            _output.AppendLine(handler: $"  br label %{endLabel}");
        }

        // Else block
        _output.AppendLine(handler: $"{elseLabel}:");
        _blockTerminated = false;
        if (node.ElseStatement != null)
        {
            node.ElseStatement.Accept(visitor: this);
        }

        bool elseTerminated = _blockTerminated;
        if (!elseTerminated)
        {
            _output.AppendLine(handler: $"  br label %{endLabel}");
        }

        // End label (only if needed)
        if (!thenTerminated || !elseTerminated)
        {
            _output.AppendLine(handler: $"{endLabel}:");
            _blockTerminated = false; // There's a continuation point
        }
        else
        {
            _blockTerminated = true; // Both branches terminated, so the if statement is terminated
        }

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for an if statement used as an expression.
    /// Uses phi nodes to merge values from both branches.
    /// </summary>
    /// <param name="node">The if statement node</param>
    /// <param name="resultType">The LLVM type of the result value</param>
    /// <returns>The SSA register containing the merged result</returns>
    public string GenerateIfExpression(IfStatement node, string resultType)
    {
        UpdateLocation(node.Location);

        // Evaluate condition
        string condition = node.Condition.Accept(visitor: this);

        // If condition is a %bool record, extract the primitive i1
        if (_tempTypes.TryGetValue(condition, out var condTypeInfo) &&
            condTypeInfo.LLVMType == "%bool")
        {
            string extractedCond = GetNextTemp();
            _output.AppendLine($"  {extractedCond} = extractvalue %bool {condition}, 0");
            condition = extractedCond;
        }

        // Create labels for control flow
        string thenLabel = GetNextLabel();
        string elseLabel = GetNextLabel();
        string mergeLabel = GetNextLabel();

        // Branch based on condition
        _output.AppendLine($"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then block
        _output.AppendLine($"{thenLabel}:");
        string thenValue = GenerateBlockExpressionValue(node.ThenStatement);
        string thenEndLabel = GetCurrentLabel();  // May have changed if block had branches
        _output.AppendLine($"  br label %{mergeLabel}");

        // Else block
        _output.AppendLine($"{elseLabel}:");
        string elseValue;
        if (node.ElseStatement != null)
        {
            elseValue = GenerateBlockExpressionValue(node.ElseStatement);
        }
        else
        {
            // If no else block, this shouldn't be used as expression
            throw new InvalidOperationException(
                "If statement used as expression must have an else branch");
        }
        string elseEndLabel = GetCurrentLabel();  // May have changed if block had branches
        _output.AppendLine($"  br label %{mergeLabel}");

        // Merge point with phi node
        _output.AppendLine($"{mergeLabel}:");
        string resultReg = GetNextTemp();
        _output.AppendLine($"  {resultReg} = phi {resultType} [ {thenValue}, %{thenEndLabel} ], [ {elseValue}, %{elseEndLabel} ]");

        return resultReg;
    }

    /// <summary>
    /// Extracts the expression value from a statement block.
    /// For blocks, the last expression is the value. For single expressions, returns that value.
    /// </summary>
    private string GenerateBlockExpressionValue(Statement statement)
    {
        if (statement is BlockStatement block)
        {
            // Process all statements except the last
            for (int i = 0; i < block.Statements.Count - 1; i++)
            {
                block.Statements[i].Accept(visitor: this);
            }

            // The last statement should be an expression or return its value
            if (block.Statements.Count > 0)
            {
                var lastStmt = block.Statements[^1];

                // If it's an expression statement, return the expression value
                if (lastStmt is ExpressionStatement exprStmt)
                {
                    return exprStmt.Expression.Accept(visitor: this);
                }
                // Otherwise visit it and return empty (will be handled by caller)
                else
                {
                    return lastStmt.Accept(visitor: this);
                }
            }

            return "";
        }
        else if (statement is ExpressionStatement exprStmt)
        {
            return exprStmt.Expression.Accept(visitor: this);
        }
        else
        {
            // For other statement types, visit and return result
            return statement.Accept(visitor: this);
        }
    }

    /// <summary>
    /// Gets the current label (last one created). Used for phi nodes.
    /// </summary>
    private string GetCurrentLabel()
    {
        return $"label{_labelCounter - 1}";
    }

    // While statement
    public string VisitWhileStatement(WhileStatement node)
    {
        UpdateLocation(node.Location);
        string condLabel = GetNextLabel();
        string bodyLabel = GetNextLabel();
        string endLabel = GetNextLabel();

        _output.AppendLine(handler: $"  br label %{condLabel}");

        _output.AppendLine(handler: $"{condLabel}:");
        string condition = node.Condition.Accept(visitor: this);

        // If condition is a %bool record, extract the primitive i1
        if (_tempTypes.TryGetValue(condition, out var condTypeInfo) &&
            condTypeInfo.LLVMType == "%bool")
        {
            string extractedCond = GetNextTemp();
            _output.AppendLine($"  {extractedCond} = extractvalue %bool {condition}, 0");
            condition = extractedCond;
        }

        _output.AppendLine(handler: $"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");

        _output.AppendLine(handler: $"{bodyLabel}:");
        node.Body.Accept(visitor: this);
        _output.AppendLine(handler: $"  br label %{condLabel}");

        _output.AppendLine(handler: $"{endLabel}:");

        return "";
    }


    public string VisitReturnStatement(ReturnStatement node)
    {
        UpdateLocation(node.Location);
        _hasReturn = true; // Mark that we've generated a return
        _blockTerminated = true; // Mark block as terminated

        // Emit stack frame pop before return
        _stackTraceCodeGen?.EmitPopFrame();

        if (node.Value != null)
        {
            string value = node.Value.Accept(visitor: this);

            // For floating-point return types, ensure literals are in the correct format
            bool isReturningFP = _currentFunctionReturnType is "half" or "float" or "double" or "fp128";
            if (isReturningFP && (value.StartsWith("0x") || value.StartsWith("0xL") || value.StartsWith("0xH")))
            {
                value = EnsureProperFPConstant(value, _currentFunctionReturnType);
            }

            LLVMTypeInfo valueTypeInfo = GetValueTypeInfo(value: value);

            // CRITICAL FIX: If returning 'me' which is a pointer to a record, but the function returns the record by value,
            // we need to LOAD the value from the pointer, not cast it
            if (value == "%me" && valueTypeInfo.LLVMType == "ptr" &&
                _currentFunctionReturnType.StartsWith("%") && _currentFunctionReturnType != "ptr")
            {
                string loadedValue = GetNextTemp();
                _output.AppendLine($"  {loadedValue} = load {_currentFunctionReturnType}, ptr %me");
                _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} {loadedValue}");
            }
            // If the value type doesn't match the function return type, we need to cast
            else if (valueTypeInfo.LLVMType != _currentFunctionReturnType)
            {
                // CRITICAL: Cannot return a value from a void function
                if (_currentFunctionReturnType == "void")
                {
                    // This is a semantic error - function declared as void but returning a value
                    // Generate a warning and just return void
                    Console.WriteLine($"Warning: Function returning a value but declared as void. Ignoring return value.");
                    _output.AppendLine(value: "  ret void");
                }
                else
                {
                    string castResult = GetNextTemp();
                    GenerateCastInstruction(result: castResult,
                        value: value,
                        fromType: valueTypeInfo,
                        toType: _currentFunctionReturnType);
                    _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} {castResult}");
                }
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
    private void GenerateCastInstruction(string result, string value, LLVMTypeInfo fromType,
        string toType)
    {
        // If source is a struct, extract the primitive value first
        if (fromType.LLVMType.StartsWith("%") && !fromType.LLVMType.EndsWith("*") && fromType.LLVMType != "ptr")
        {
            string primitiveType = InferPrimitiveTypeFromRecordName(fromType.LLVMType);
            string extractedValue = GetNextTemp();
            _output.AppendLine($"  {extractedValue} = extractvalue {fromType.LLVMType} {value}, 0");
            value = extractedValue;
            // Update fromType to the extracted primitive type
            fromType = new LLVMTypeInfo(LLVMType: primitiveType, IsUnsigned: fromType.IsUnsigned, IsFloatingPoint: fromType.IsFloatingPoint);
        }

        // Check for pointer types - both opaque ptr and typed pointers (e.g., i8*, %Type*)
        bool fromIsPointer = fromType.LLVMType.EndsWith(value: "*") || fromType.LLVMType == "ptr";
        bool toIsPointer = toType.EndsWith(value: "*") || toType == "ptr";

        // Handle pointer conversions
        if (!fromIsPointer && toIsPointer)
        {
            // Integer to pointer: use inttoptr
            _output.AppendLine(
                handler: $"  {result} = inttoptr {fromType.LLVMType} {value} to {toType}");
            return;
        }
        else if (fromIsPointer && toIsPointer)
        {
            // Pointer to pointer: use bitcast
            _output.AppendLine(
                handler: $"  {result} = bitcast {fromType.LLVMType} {value} to {toType}");
            return;
        }
        else if (fromIsPointer && !toIsPointer)
        {
            // Check if target is a struct/record type
            if (toType.StartsWith("%") && !toType.EndsWith("*") && toType != "ptr")
            {
                // CRITICAL: Cannot cast pointer to multi-field struct
                // This only works for single-field record wrappers like %u64 = type { i64 }
                // For complex types like %DynamicSlice = type { %uaddr, %uaddr }, this is invalid

                // Check if this is a multi-field struct by looking it up
                if (_recordFields.TryGetValue(toType.TrimStart('%'), out var fields) && fields.Count > 1)
                {
                    throw CodeGenError.TypeResolutionFailed(
                        typeName: toType,
                        context: $"Cannot cast pointer to multi-field struct. " +
                                $"This conversion is only valid for single-field record wrappers.",
                        file: _currentFileName,
                        line: 0, column: 0, position: 0);
                }

                // Pointer to single-field struct: convert to primitive, then construct struct
                string primitiveType = InferPrimitiveTypeFromRecordName(toType);
                string tempPrimitive = GetNextTemp();
                _output.AppendLine($"  {tempPrimitive} = ptrtoint {fromType.LLVMType} {value} to {primitiveType}");
                // Now construct the struct
                _output.AppendLine($"  {result} = insertvalue {toType} undef, {primitiveType} {tempPrimitive}, 0");
                return;
            }
            // Pointer to integer: use ptrtoint
            _output.AppendLine(
                handler: $"  {result} = ptrtoint {fromType.LLVMType} {value} to {toType}");
            return;
        }

        // Check if target type is a record/struct that needs construction
        if (toType.StartsWith("%") && !toType.EndsWith("*") && toType != "ptr")
        {
            // Target is likely a record type - need to construct it from the primitive value
            // First, get or convert the value to the appropriate primitive type
            string primitiveType = InferPrimitiveTypeFromRecordName(toType);
            string convertedValue = value;

            // If the inferred primitive type is different from source, do conversion first
            if (primitiveType != fromType.LLVMType)
            {
                string tempConvert = GetNextTemp();

                // Check if this is a floating-point conversion
                bool fromIsFloat = fromType.IsFloatingPoint || IsFloatingPointType(fromType.LLVMType);
                bool toIsFloat = IsFloatingPointType(primitiveType);

                if (fromIsFloat && toIsFloat)
                {
                    // Float to float conversion
                    int fromSize = GetTypeBitWidth(llvmType: fromType.LLVMType);
                    int toSize = GetTypeBitWidth(llvmType: primitiveType);

                    if (fromSize > toSize)
                    {
                        _output.AppendLine($"  {tempConvert} = fptrunc {fromType.LLVMType} {value} to {primitiveType}");
                    }
                    else if (fromSize < toSize)
                    {
                        _output.AppendLine($"  {tempConvert} = fpext {fromType.LLVMType} {value} to {primitiveType}");
                    }
                    else
                    {
                        tempConvert = value; // Same type, no conversion needed
                    }
                }
                else if (fromIsFloat && !toIsFloat)
                {
                    // Float to int conversion
                    if (fromType.IsUnsigned)
                    {
                        _output.AppendLine($"  {tempConvert} = fptoui {fromType.LLVMType} {value} to {primitiveType}");
                    }
                    else
                    {
                        _output.AppendLine($"  {tempConvert} = fptosi {fromType.LLVMType} {value} to {primitiveType}");
                    }
                }
                else if (!fromIsFloat && toIsFloat)
                {
                    // Int to float conversion
                    if (fromType.IsUnsigned)
                    {
                        _output.AppendLine($"  {tempConvert} = uitofp {fromType.LLVMType} {value} to {primitiveType}");
                    }
                    else
                    {
                        _output.AppendLine($"  {tempConvert} = sitofp {fromType.LLVMType} {value} to {primitiveType}");
                    }
                }
                else
                {
                    // Integer to integer conversion
                    int fromSize = GetTypeBitWidth(llvmType: fromType.LLVMType);
                    int toSize = GetTypeBitWidth(llvmType: primitiveType);

                    if (fromSize > toSize)
                    {
                        _output.AppendLine($"  {tempConvert} = trunc {fromType.LLVMType} {value} to {primitiveType}");
                    }
                    else if (fromSize < toSize)
                    {
                        if (fromType.IsUnsigned)
                        {
                            _output.AppendLine($"  {tempConvert} = zext {fromType.LLVMType} {value} to {primitiveType}");
                        }
                        else
                        {
                            _output.AppendLine($"  {tempConvert} = sext {fromType.LLVMType} {value} to {primitiveType}");
                        }
                    }
                    else
                    {
                        tempConvert = value; // Same type, no conversion needed
                    }
                }
                convertedValue = tempConvert;
            }

            // Construct the record using insertvalue
            // For single-field records, just insert the value at index 0 into undef
            _output.AppendLine($"  {result} = insertvalue {toType} undef, {primitiveType} {convertedValue}, 0");
            return;
        }

        // Handle floating point conversions
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
        // Handle struct types by getting the underlying primitive type
        if (llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr")
        {
            string primitiveType = InferPrimitiveTypeFromRecordName(llvmType);
            // Prevent infinite recursion if primitive type is the same as input or is still a record
            if (primitiveType != llvmType && !primitiveType.StartsWith("%"))
            {
                return GetTypeBitWidth(primitiveType);  // Recursive call with primitive type
            }
            // For complex record types (like DynamicSlice with multiple fields),
            // we can't determine a single bit width. Return default for pointer-sized data.
            return 64;
        }

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
            _ => 64 // Default changed to 64 for debugging
        };
    }

    public string VisitForStatement(ForStatement node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitBreakStatement(BreakStatement node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitContinueStatement(ContinueStatement node)
    {
        UpdateLocation(node.Location);
        return "";
    }
    public string VisitBlockStatement(BlockStatement node)
    {
        UpdateLocation(node.Location);
        foreach (Statement statement in node.Statements)
        {
            statement.Accept(visitor: this);
        }

        return "";
    }
    public string VisitExpressionStatement(ExpressionStatement node)
    {
        UpdateLocation(node.Location);
        return node.Expression.Accept(visitor: this);
    }
    public string VisitDeclarationStatement(DeclarationStatement node)
    {
        UpdateLocation(node.Location);
        return node.Declaration.Accept(visitor: this);
    }
    public string VisitAssignmentStatement(AssignmentStatement node)
    {
        UpdateLocation(node.Location);
        return "";
    }

    public string VisitTupleDestructuringStatement(TupleDestructuringStatement node)
    {
        UpdateLocation(node.Location);

        // TODO: Implement tuple destructuring code generation
        // For now, this is a placeholder that visits the initializer
        node.Initializer.Accept(visitor: this);

        // When intrinsics that return tuples are implemented, this should:
        // 1. Generate code for the initializer expression (must be a tuple-returning expression)
        // 2. Extract tuple elements using extractvalue
        // 3. Allocate and store each variable
        // 4. Track variables in a variable map

        return "";
    }
}
