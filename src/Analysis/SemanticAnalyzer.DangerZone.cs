using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing danger! block handling and intrinsic validation.
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

    public object? VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        // Check if this is a safe intrinsic (can be used outside danger blocks)
        bool isSafeIntrinsic = IsSafeIntrinsic(intrinsicName: node.IntrinsicName);

        // Unsafe intrinsics can only be used inside danger! blocks
        if (!isSafeIntrinsic && !_isInDangerMode)
        {
            AddError(
                message:
                $"Intrinsic '{node.IntrinsicName}' can only be used inside danger! blocks",
                location: node.Location);
            return SetResolvedType(node, null);
        }

        // Visit arguments for type checking
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // Return appropriate type based on intrinsic
        var resultType = GetIntrinsicReturnType(node: node);
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Checks if an intrinsic is safe to use outside danger blocks.
    /// Safe intrinsics are compile-time operations that don't perform unsafe memory access.
    /// </summary>
    private static bool IsSafeIntrinsic(string intrinsicName)
    {
        return intrinsicName switch
        {
            // sizeof is a compile-time constant - no unsafe operations
            "sizeof" => true,
            // alignof is also compile-time
            "alignof" => true,
            // All other intrinsics require danger blocks
            _ => false
        };
    }

    /// <summary>
    /// Returns the appropriate type for an intrinsic call.
    /// </summary>
    private TypeInfo GetIntrinsicReturnType(IntrinsicCallExpression node)
    {
        return node.IntrinsicName switch
        {
            // sizeof returns uaddr (size type)
            "sizeof" => GetPrimitiveTypeInfo("uaddr"),
            // alignof returns uaddr (alignment value)
            "alignof" => GetPrimitiveTypeInfo("uaddr"),
            // load returns the type argument
            "load" or "volatile_load" or "atomic.load" =>
                node.TypeArguments.Count > 0
                    ? GetTypeInfoForName(node.TypeArguments[0])
                    : new TypeInfo(Name: "unknown", IsReference: false),
            // store, volatile_store return void
            "store" or "volatile_store" or "atomic.store" =>
                new TypeInfo(Name: "void", IsReference: false),
            // bitcast returns the second type argument
            "bitcast" =>
                node.TypeArguments.Count > 1
                    ? GetTypeInfoForName(node.TypeArguments[1])
                    : new TypeInfo(Name: "unknown", IsReference: false),
            // Arithmetic intrinsics return the type argument
            "add.wrapping" or "sub.wrapping" or "mul.wrapping" or "div.wrapping" or "rem.wrapping"
                or "add.saturating" or "sub.saturating" =>
                node.TypeArguments.Count > 0
                    ? GetTypeInfoForName(node.TypeArguments[0])
                    : new TypeInfo(Name: "unknown", IsReference: false),
            // Comparison intrinsics return bool
            var name when name.StartsWith("icmp.") || name.StartsWith("fcmp.") =>
                GetPrimitiveTypeInfo("bool"),
            // Bitwise intrinsics return the type argument
            "and" or "or" or "xor" or "not" or "shl" or "lshr" or "ashr" =>
                node.TypeArguments.Count > 0
                    ? GetTypeInfoForName(node.TypeArguments[0])
                    : new TypeInfo(Name: "unknown", IsReference: false),
            // Type conversion intrinsics return the target type
            "trunc" or "zext" or "sext" or "fptrunc" or "fpext"
                or "fptoui" or "fptosi" or "uitofp" or "sitofp" =>
                node.TypeArguments.Count > 1
                    ? GetTypeInfoForName(node.TypeArguments[1])
                    : new TypeInfo(Name: "unknown", IsReference: false),
            // Math intrinsics return the type argument
            "sqrt" or "abs" or "fabs" or "copysign" or "floor" or "ceil"
                or "trunc_float" or "round" or "pow" or "exp" or "log" or "log10"
                or "sin" or "cos" =>
                node.TypeArguments.Count > 0
                    ? GetTypeInfoForName(node.TypeArguments[0])
                    : GetPrimitiveTypeInfo("f64"),
            // Bit manipulation intrinsics
            "ctpop" or "ctlz" or "cttz" =>
                GetPrimitiveTypeInfo("u32"),
            "bswap" or "bitreverse" =>
                node.TypeArguments.Count > 0
                    ? GetTypeInfoForName(node.TypeArguments[0])
                    : new TypeInfo(Name: "unknown", IsReference: false),
            // Default: return generic result type
            _ => new TypeInfo(Name: "intrinsic_result", IsReference: false)
        };
    }

    /// <summary>
    /// Gets TypeInfo for a type name, using primitive type factory for known types.
    /// </summary>
    private TypeInfo GetTypeInfoForName(string typeName)
    {
        return PrimitiveTypes.IsPrimitive(typeName)
            ? GetPrimitiveTypeInfo(typeName)
            : new TypeInfo(Name: typeName, IsReference: false);
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
            return SetResolvedType(node, null);
        }

        // Visit arguments for type checking
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // Native calls return uaddr by default (pointer-sized value)
        // The actual return type depends on the native function signature
        return SetResolvedType(node, GetPrimitiveTypeInfo("uaddr"));
    }

    /// <summary>
    /// Visits an external declaration for FFI bindings.
    /// Registers external functions in the symbol table.
    /// </summary>
    /// <param name="node">Imported declaration node</param>
    /// <returns>Null</returns>
    public object? VisitImportedDeclaration(ExternalDeclaration node)
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
            AddError(message: $"Imported function '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    /// <summary>
    /// Visits a preset declaration for compile-time constants.
    /// Registers preset constants in the symbol table.
    /// </summary>
    /// <param name="node">Preset declaration node</param>
    /// <returns>Null</returns>
    public object? VisitPresetDeclaration(PresetDeclaration node)
    {
        // Create variable symbol for preset declaration (treated as immutable constant)
        TypeInfo typeInfo = new TypeInfo(Name: node.Type.Name, IsReference: false);

        var symbol = new VariableSymbol(Name: node.Name,
            Type: typeInfo,
            IsMutable: false,
            Visibility: VisibilityModifier.Private);

        if (!_symbolTable.TryDeclare(symbol: symbol))
        {
            AddError(message: $"Preset constant '{node.Name}' is already declared",
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
        // Use the tracked danger mode state
        return _isInDangerMode;
    }
}
