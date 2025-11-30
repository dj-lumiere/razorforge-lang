using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    // If statement
    public string VisitIfStatement(IfStatement node)
    {
        string condition = node.Condition.Accept(visitor: this);
        string thenLabel = GetNextLabel();
        string elseLabel = GetNextLabel();
        string endLabel = GetNextLabel();

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
        }

        _blockTerminated = false;

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


    public string VisitReturnStatement(ReturnStatement node)
    {
        _hasReturn = true; // Mark that we've generated a return
        _blockTerminated = true; // Mark block as terminated

        // Emit stack frame pop before return
        _stackTraceCodeGen?.EmitPopFrame();

        if (node.Value != null)
        {
            string value = node.Value.Accept(visitor: this);
            TypeInfo valueTypeInfo = GetValueTypeInfo(value: value);

            // If the value type doesn't match the function return type, we need to cast
            if (valueTypeInfo.LLVMType != _currentFunctionReturnType)
            {
                string castResult = GetNextTemp();
                GenerateCastInstruction(result: castResult,
                    value: value,
                    fromType: valueTypeInfo,
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
        bool fromIsPointer = fromType.LLVMType.EndsWith(value: "*");
        bool toIsPointer = toType.EndsWith(value: "*");

        // Handle pointer conversions
        if (!fromIsPointer && toIsPointer)
        {
            // Integer to pointer: use inttoptr
            _output.AppendLine(
                handler: $"  {result} = inttoptr {fromType.LLVMType} {value} to {toType}");
            return;
        }
        else if (fromIsPointer && !toIsPointer)
        {
            // Pointer to integer: use ptrtoint
            _output.AppendLine(
                handler: $"  {result} = ptrtoint {fromType.LLVMType} {value} to {toType}");
            return;
        }
        else if (fromIsPointer && toIsPointer)
        {
            // Pointer to pointer: use bitcast
            _output.AppendLine(
                handler: $"  {result} = bitcast {fromType.LLVMType} {value} to {toType}");
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
    public string VisitBlockStatement(BlockStatement node)
    {
        foreach (Statement statement in node.Statements)
        {
            statement.Accept(visitor: this);
        }

        return "";
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
}
