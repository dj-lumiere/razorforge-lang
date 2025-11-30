using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing danger!/mayhem! block handling and intrinsic validation.
/// </summary>
public partial class SemanticAnalyzer
{
    /// <summary>
    /// Visits a danger! block that disables safety checks.
    /// Tracks danger mode state for validation.
    /// </summary>
    /// <param name="node">Danger statement node</param>
    /// <returns>Null</returns>
    public object? VisitDangerStatement(DangerStatement node)
    {
        // Save current danger mode state
        bool previousDangerMode = _isInDangerMode;

        try
        {
            // Enable danger mode for this block
            _isInDangerMode = true;

            // Create new scope for variables declared in danger block
            _symbolTable.EnterScope();

            // Process the danger block body with elevated permissions
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the danger block scope
            _symbolTable.ExitScope();

            // Restore previous danger mode
            _isInDangerMode = previousDangerMode;
        }

        return null;
    }

    /// <summary>
    /// Visits a mayhem! block that disables all safety checks.
    /// Tracks mayhem mode state for validation.
    /// </summary>
    /// <param name="node">Mayhem statement node</param>
    /// <returns>Null</returns>
    public object? VisitMayhemStatement(MayhemStatement node)
    {
        // Save current mayhem mode state
        bool previousMayhemMode = _isInMayhemMode;

        try
        {
            // Enable mayhem mode for this block
            _isInMayhemMode = true;

            // Create new scope for variables declared in mayhem block
            _symbolTable.EnterScope();

            // Process the mayhem block body with maximum permissions
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the mayhem block scope
            _symbolTable.ExitScope();

            // Restore previous mayhem mode
            _isInMayhemMode = previousMayhemMode;
        }

        return null;
    }

    public object? VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        // Intrinsics can only be used inside danger! blocks
        if (!_isInDangerMode)
        {
            AddError(
                message:
                $"Intrinsic '{node.IntrinsicName}' can only be used inside danger! blocks",
                location: node.Location);
            return null;
        }

        // Visit arguments for type checking
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // For now, return a generic type - intrinsics will be validated in codegen
        // We could add more sophisticated type checking here based on the intrinsic name
        return new TypeInfo(Name: "intrinsic_result", IsReference: false);
    }

    public object? VisitNativeCallExpression(NativeCallExpression node)
    {
        // Native calls can only be used inside danger! blocks
        if (!_isInDangerMode)
        {
            AddError(
                message:
                $"Native call '@native.{node.FunctionName}' can only be used inside danger! blocks",
                location: node.Location);
            return null;
        }

        // Visit arguments for type checking
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // Native calls return uaddr by default (pointer-sized value)
        // The actual return type depends on the native function signature
        return new TypeInfo(Name: "uaddr", IsReference: false);
    }

    /// <summary>
    /// Visits an external declaration for FFI bindings.
    /// Registers external functions in the symbol table.
    /// </summary>
    /// <param name="node">External declaration node</param>
    /// <returns>Null</returns>
    public object? VisitExternalDeclaration(ExternalDeclaration node)
    {
        // Create function symbol for external declaration
        List<Parameter> parameters = node.Parameters;
        TypeInfo? returnType = node.ReturnType != null
            ? new TypeInfo(Name: node.ReturnType.Name, IsReference: false)
            : null;

        var functionSymbol = new FunctionSymbol(Name: node.Name,
            Parameters: parameters,
            ReturnType: returnType,
            Visibility: VisibilityModifier.External,
            IsUsurping: false,
            GenericParameters: node.GenericParameters?.ToList());

        if (!_symbolTable.TryDeclare(symbol: functionSymbol))
        {
            AddError(message: $"External function '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    private object? ValidateGenericMethodCall(GenericMethodCallExpression node,
        TypeInfo objectType)
    {
        // Check if this is actually a generic function call (not a method on an object)
        if (node.Object is IdentifierExpression funcIdent)
        {
            // Look up the function in the symbol table
            Symbol? funcSymbol = _symbolTable.Lookup(name: funcIdent.Name);

            // Check for function overload set
            if (funcSymbol is FunctionOverloadSet overloadSet)
            {
                // Find a matching overload with the right number of generic parameters
                foreach (FunctionSymbol overload in overloadSet.Overloads)
                {
                    if (overload.GenericParameters != null && overload.GenericParameters.Count ==
                        node.TypeArguments.Count)
                    {
                        funcSymbol = overload;
                        break;
                    }
                }
            }

            if (funcSymbol is FunctionSymbol func && func.GenericParameters != null &&
                func.GenericParameters.Count > 0)
            {
                // This is a generic function call - resolve the return type
                // by substituting the type arguments

                // Create binding map from type parameters to concrete types
                var genericBindings = new Dictionary<string, TypeInfo>();
                for (int i = 0;
                     i < Math.Min(val1: func.GenericParameters.Count,
                         val2: node.TypeArguments.Count);
                     i++)
                {
                    string paramName = func.GenericParameters[index: i];
                    TypeInfo? argType = ResolveType(typeExpr: node.TypeArguments[index: i]);
                    if (argType != null)
                    {
                        genericBindings[key: paramName] = argType;
                    }
                }

                // Resolve return type with generic substitution
                if (func.ReturnType != null)
                {
                    // If return type is a generic parameter, substitute it
                    if (genericBindings.TryGetValue(key: func.ReturnType.Name,
                            value: out TypeInfo? resolvedReturnType))
                    {
                        return resolvedReturnType;
                    }

                    return func.ReturnType;
                }

                return new TypeInfo(Name: "void", IsReference: false);
            }
        }

        // TODO: Implement validation for other generic method calls
        return new TypeInfo(Name: "unknown", IsReference: false);
    }

    private object? HandleMemoryModelOperation(MemoryOperationExpression node, TypeInfo sliceType)
    {
        // Integrate with memory analyzer for wrapper type operations
        MemoryOperation? memOp = GetMemoryOperation(operationName: node.OperationName);
        if (memOp != null)
        {
            // Create a temporary memory object for validation
            var memoryObject = new MemoryObject(Name: node.Object.ToString() ?? "slice",
                BaseType: sliceType,
                Wrapper: WrapperType.Owned,
                State: ObjectState.Valid,
                ReferenceCount: 1,
                Location: node.Location);

            MemoryOperationResult result = _memoryAnalyzer.ValidateMemoryOperation(
                memoryObject: memoryObject,
                operation: memOp.Value,
                location: node.Location);
            if (!result.IsSuccess)
            {
                foreach (MemoryError error in result.Errors)
                {
                    AddError(message: error.Message, location: error.Location);
                }
            }

            return CreateWrapperTypeInfo(baseType: sliceType, wrapper: result.NewWrapperType);
        }

        return sliceType;
    }

    private MemoryOperation? GetMemoryOperation(string operationName)
    {
        return ParseMemoryOperation(operationName: operationName);
    }

    private MemoryOperation? GetMemoryOperation(string operationName, SourceLocation location)
    {
        return GetMemoryOperation(operationName: operationName);
    }

    private object? ValidateSliceGenericMethod(GenericMethodCallExpression node,
        TypeInfo sliceType, SourceLocation location)
    {
        return ValidateSliceGenericMethod(node: node, sliceType: sliceType);
    }

    private object? ValidateGenericMethodCall(GenericMethodCallExpression node,
        TypeInfo objectType, SourceLocation location)
    {
        return ValidateGenericMethodCall(node: node, objectType: objectType);
    }

    private object? ValidateSliceMemoryOperation(MemoryOperationExpression node,
        TypeInfo sliceType, SourceLocation location)
    {
        return ValidateSliceMemoryOperation(node: node, sliceType: sliceType);
    }

    private object? HandleMemoryModelOperation(MemoryOperationExpression node, TypeInfo sliceType,
        SourceLocation location)
    {
        return HandleMemoryModelOperation(node: node, sliceType: sliceType);
    }

    private TypeInfo? HandleMemoryOperationCall(MemberExpression memberExpr, string operationName,
        List<Expression> arguments, SourceLocation location, SourceLocation nodeLocation)
    {
        return HandleMemoryOperationCall(memberExpr: memberExpr,
            operationName: operationName,
            arguments: arguments,
            location: location);
    }

    private TypeInfo CreateWrapperTypeInfo(TypeInfo baseType, WrapperType wrapper,
        SourceLocation location)
    {
        return CreateWrapperTypeInfo(baseType: baseType, wrapper: wrapper);
    }

    private bool IsIntegerType(string? typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" => true,
            "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" => true,
            _ => false
        };
    }

    private bool IsCompatibleType(string? sourceType, string? targetType)
    {
        if (sourceType == targetType)
        {
            return true;
        }

        // Allow integer type coercion
        if (IsIntegerType(typeName: sourceType) && IsIntegerType(typeName: targetType))
        {
            return true;
        }

        return false;
    }

    private bool IsDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "write_as" or "read_as" or "volatile_write" or "volatile_read" or "address_of"
                or "invalidate" => true,
            _ => false
        };
    }

    private object? ValidateDangerZoneFunction(GenericMethodCallExpression node,
        string functionName)
    {
        // These functions are only available in danger blocks
        if (!_isInDangerMode)
        {
            AddError(message: $"Function '{functionName}' is only available within danger! blocks",
                location: node.Location);
            return null;
        }

        List<Expression> args = node.Arguments;
        List<TypeExpression> typeArgs = node.TypeArguments;

        return functionName switch
        {
            "write_as" => ValidateWriteAs(args: args, typeArgs: typeArgs, location: node.Location),
            "read_as" => ValidateReadAs(args: args, typeArgs: typeArgs, location: node.Location),
            "volatile_write" => ValidateVolatileWrite(args: args,
                typeArgs: typeArgs,
                location: node.Location),
            "volatile_read" => ValidateVolatileRead(args: args,
                typeArgs: typeArgs,
                location: node.Location),
            "address_of" => ValidateAddrOf(args: args, location: node.Location),
            "invalidate" => ValidateInvalidate(args: args, location: node.Location),
            _ => throw new InvalidOperationException(
                message: $"Unknown danger zone function: {functionName}")
        };
    }

    private object? ValidateWriteAs(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        if (typeArgs.Count != 1)
        {
            AddError(message: "write_as<T>! requires exactly one type argument",
                location: location);
            return null;
        }

        if (args.Count != 2)
        {
            AddError(message: "write_as<T>! requires exactly two arguments (address, value)",
                location: location);
            return null;
        }

        string targetType = typeArgs[index: 0].Name;
        var addressType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;
        var valueType = args[index: 1]
           .Accept(visitor: this) as TypeInfo;

        // Address should be integer type (convertible to pointer)
        if (!IsIntegerType(typeName: addressType?.Name))
        {
            AddError(message: "write_as<T>! address must be an integer type", location: location);
        }

        // Value should be compatible with target type
        if (valueType?.Name != targetType &&
            !IsCompatibleType(sourceType: valueType?.Name, targetType: targetType))
        {
            AddError(message: $"write_as<T>! value must be of type {targetType}",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private object? ValidateReadAs(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        if (typeArgs.Count != 1)
        {
            AddError(message: "read_as<T>! requires exactly one type argument",
                location: location);
            return null;
        }

        if (args.Count != 1)
        {
            AddError(message: "read_as<T>! requires exactly one argument (address)",
                location: location);
            return null;
        }

        string targetType = typeArgs[index: 0].Name;
        var addressType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;

        // Address should be integer type (convertible to pointer)
        if (!IsIntegerType(typeName: addressType?.Name))
        {
            AddError(message: "read_as<T>! address must be an integer type", location: location);
        }

        return new TypeInfo(Name: targetType, IsReference: false);
    }

    private object? ValidateVolatileWrite(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        // Same validation as write_as but for volatile operations
        return ValidateWriteAs(args: args, typeArgs: typeArgs, location: location);
    }

    private object? ValidateVolatileRead(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        // Same validation as read_as but for volatile operations
        return ValidateReadAs(args: args, typeArgs: typeArgs, location: location);
    }

    private object? ValidateAddrOf(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "address_of! requires exactly one argument (variable)",
                location: location);
            return null;
        }

        // The argument should be a variable reference
        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;
        if (argType == null)
        {
            AddError(message: "address_of! argument must be a valid variable", location: location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo(Name: "uaddr", IsReference: false);
    }

    private object? ValidateInvalidate(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "invalidate! requires exactly one argument (slice or pointer)",
                location: location);
            return null;
        }

        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;

        // Should be a slice type or pointer
        if (argType?.Name != "DynamicSlice" && argType?.Name != "TemporarySlice" &&
            argType?.Name != "ptr")
        {
            AddError(message: "invalidate! argument must be a slice or pointer",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private bool IsNonGenericDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "address_of" or "invalidate" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if a function name is an error intrinsic (verify!, breach!, stop!).
    /// </summary>
    private bool IsErrorIntrinsic(string functionName)
    {
        return functionName switch
        {
            "verify!" or "breach!" or "stop!" => true,
            _ => false
        };
    }

    private object? ValidateNonGenericDangerZoneFunction(CallExpression node, string functionName)
    {
        // These functions are only available in danger blocks
        if (!_isInDangerMode)
        {
            AddError(message: $"Function '{functionName}' is only available within danger! blocks",
                location: node.Location);
            return null;
        }

        return functionName switch
        {
            "address_of" => ValidateAddrOfFunction(args: node.Arguments, location: node.Location),
            "invalidate" => ValidateInvalidateFunction(args: node.Arguments,
                location: node.Location),
            _ => throw new InvalidOperationException(
                message: $"Unknown non-generic danger zone function: {functionName}")
        };
    }

    private object? ValidateAddrOfFunction(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "address_of! requires exactly one argument (variable)",
                location: location);
            return null;
        }

        // The argument should be a variable reference (IdentifierExpression)
        Expression arg = args[index: 0];
        if (arg is not IdentifierExpression)
        {
            AddError(message: "address_of! argument must be a variable identifier",
                location: location);
            return null;
        }

        // Validate that the variable exists
        var argType = arg.Accept(visitor: this) as TypeInfo;
        if (argType == null)
        {
            AddError(message: "address_of! argument must be a valid variable", location: location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo(Name: "uaddr", IsReference: false);
    }

    private object? ValidateInvalidateFunction(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "invalidate! requires exactly one argument (slice or pointer)",
                location: location);
            return null;
        }

        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;

        // Should be a slice type or pointer
        if (argType?.Name != "DynamicSlice" && argType?.Name != "TemporarySlice" &&
            argType?.Name != "ptr")
        {
            AddError(message: "invalidate! argument must be a slice or pointer",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private bool IsInDangerBlock()
    {
        // Use the tracked danger/mayhem mode state
        return _isInDangerMode || _isInMayhemMode;
    }
}
