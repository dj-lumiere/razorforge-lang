using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing slice and generic method expression handling.
/// </summary>
public partial class SemanticAnalyzer
{
       /// <summary>
    /// Visits a slice constructor expression and validates the slice creation.
    /// </summary>
    /// <param name="node">Slice constructor expression node</param>
    /// <returns>TypeInfo of the slice</returns>
    public object? VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        // Validate size expression is compatible with uaddr type
        var sizeType = node.SizeExpression.Accept(visitor: this) as TypeInfo;
        if (sizeType == null)
        {
            AddError(message: $"Slice size expression has unknown type", location: node.Location);
        }
        else if (sizeType.Name != "uaddr")
        {
            // Allow integer literals to be coerced to uaddr
            if (sizeType.IsInteger)
            {
                // Implicit conversion from any integer type to uaddr for slice sizes
                // This handles cases like DynamicSlice(64) where 64 might be typed as s32, s64, etc.
            }
            else
            {
                AddError(message: $"Slice size must be of type uaddr or compatible integer type, found {sizeType.Name}", location: node.Location);
            }
        }

        // Return appropriate slice type
        string sliceTypeName = node.SliceType;
        return new TypeInfo(Name: sliceTypeName, IsReference: false); // Slice types are value types (structs)
    }

    /// <summary>
    /// Visits a generic method call expression and validates type arguments.
    /// </summary>
    /// <param name="node">Generic method call expression node</param>
    /// <returns>TypeInfo of the return value</returns>
    public object? VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        // Check if this is a standalone global function call (e.g., write_as<T>!, read_as<T>!)
        if (node.Object is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;

            // Handle built-in danger zone operations
            if (IsDangerZoneFunction(functionName: functionName))
            {
                return ValidateDangerZoneFunction(node: node, functionName: functionName);
            }
        }

        // Validate object type supports the generic method
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot call method on null object", location: node.Location);
            return null;
        }

        // Check if this is a slice operation
        if (objectType.Name == "DynamicSlice" || objectType.Name == "TemporarySlice")
        {
            return ValidateSliceGenericMethod(node: node, sliceType: objectType);
        }

        // Handle other generic method calls
        return ValidateGenericMethodCall(node: node, objectType: objectType);
    }

    /// <summary>
    /// Visits a generic member access expression and validates type arguments.
    /// </summary>
    /// <param name="node">Generic member expression node</param>
    /// <returns>TypeInfo of the member</returns>
    public object? VisitGenericMemberExpression(GenericMemberExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot access member on null object", location: node.Location);
            return null;
        }

        // Validate type arguments are well-formed
        foreach (TypeExpression typeArg in node.TypeArguments)
        {
            TypeInfo? resolvedType = ResolveTypeExpression(typeExpr: typeArg);
            if (resolvedType == null)
            {
                AddError(message: $"Unknown type argument '{typeArg.Name}'", location: node.Location);
            }
        }

        // Validate member exists and is generic
        TypeInfo? memberType = ValidateGenericMember(objectType: objectType, memberName: node.MemberName, typeArguments: node.TypeArguments, location: node.Location);

        return memberType ?? new TypeInfo(Name: "unknown", IsReference: false);
    }

    /// <summary>
    /// Visits a memory operation expression (e.g., retain, share, track) and validates safety.
    /// </summary>
    /// <param name="node">Memory operation expression node</param>
    /// <returns>TypeInfo of the wrapped result</returns>
    public object? VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot perform memory operation on null object", location: node.Location);
            return null;
        }

        // Check if this is a slice operation
        if (objectType.Name == "DynamicSlice" || objectType.Name == "TemporarySlice")
        {
            return ValidateSliceMemoryOperation(node: node, sliceType: objectType);
        }

        // Handle other memory operations through memory analyzer
        MemoryOperation? memOp = GetMemoryOperation(operationName: node.OperationName);
        if (memOp == null)
        {
            return objectType;
        }

        MemoryObject? memoryObject = _memoryAnalyzer.GetMemoryObject(name: node.Object.ToString() ?? "");
        if (memoryObject == null)
        {
            return objectType;
        }

        MemoryOperationResult result = _memoryAnalyzer.ValidateMemoryOperation(memoryObject: memoryObject, operation: memOp.Value, location: node.Location);
        if (result.IsSuccess)
        {
            return CreateWrapperTypeInfo(baseType: memoryObject.BaseType, wrapper: result.NewWrapperType);
        }

        foreach (MemoryError error in result.Errors)
        {
            AddError(message: error.Message, location: error.Location);
        }

        return CreateWrapperTypeInfo(baseType: memoryObject.BaseType, wrapper: result.NewWrapperType);
    }
    private object? ValidateSliceGenericMethod(GenericMethodCallExpression node, TypeInfo sliceType)
    {
        string methodName = node.MethodName;
        List<TypeExpression> typeArgs = node.TypeArguments;
        List<Expression> args = node.Arguments;

        // Validate type arguments
        if (typeArgs.Count != 1)
        {
            AddError(message: $"Slice method '{methodName}' requires exactly one type argument", location: node.Location);
            return null;
        }

        TypeExpression targetType = typeArgs[index: 0];

        switch (methodName)
        {
            case "read":
                // read<T>!(offset: uaddr) -> T
                if (args.Count != 1)
                {
                    AddError(message: "read<T>! requires exactly one argument (offset)", location: node.Location);
                    return null;
                }

                var offsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                if (offsetType?.Name != "uaddr" && !IsIntegerType(typeName: offsetType?.Name))
                {
                    AddError(message: "read<T>! offset must be of type uaddr", location: node.Location);
                }

                return new TypeInfo(Name: targetType.Name, IsReference: false);

            case "write":
                // write<T>!(offset: uaddr, value: T)
                if (args.Count != 2)
                {
                    AddError(message: "write<T>! requires exactly two arguments (offset, value)", location: node.Location);
                    return null;
                }

                var writeOffsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                var valueType = args[index: 1]
                   .Accept(visitor: this) as TypeInfo;

                if (writeOffsetType?.Name != "uaddr" && !IsIntegerType(typeName: writeOffsetType?.Name))
                {
                    AddError(message: "write<T>! offset must be of type uaddr", location: node.Location);
                }

                if (valueType?.Name != targetType.Name && !IsCompatibleType(sourceType: valueType?.Name, targetType: targetType.Name))
                {
                    AddError(message: $"write<T>! value must be of type {targetType.Name}", location: node.Location);
                }

                return new TypeInfo(Name: "void", IsReference: false);

            default:
                AddError(message: $"Unknown slice generic method: {methodName}", location: node.Location);
                return null;
        }
    }

    private object? ValidateSliceMemoryOperation(MemoryOperationExpression node, TypeInfo sliceType)
    {
        string operationName = node.OperationName;
        List<Expression> args = node.Arguments;

        switch (operationName)
        {
            case "size":
                if (args.Count != 0)
                {
                    AddError(message: "size! operation takes no arguments", location: node.Location);
                }

                return new TypeInfo(Name: "uaddr", IsReference: false);

            case "address":
                if (args.Count != 0)
                {
                    AddError(message: "address! operation takes no arguments", location: node.Location);
                }

                return new TypeInfo(Name: "uaddr", IsReference: false);

            case "is_valid":
                if (args.Count != 0)
                {
                    AddError(message: "is_valid! operation takes no arguments", location: node.Location);
                }

                return new TypeInfo(Name: "bool", IsReference: false);

            case "unsafe_ptr":
                if (args.Count != 1)
                {
                    AddError(message: "unsafe_ptr! requires exactly one argument (offset)", location: node.Location);
                    return null;
                }

                var offsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                if (offsetType?.Name != "uaddr")
                {
                    AddError(message: "unsafe_ptr! offset must be of type uaddr", location: node.Location);
                }

                return new TypeInfo(Name: "uaddr", IsReference: false);

            case "slice":
                if (args.Count != 2)
                {
                    AddError(message: "slice! requires exactly two arguments (offset, bytes)", location: node.Location);
                    return null;
                }

                return new TypeInfo(Name: sliceType.Name, IsReference: true); // Returns same slice type

            case "hijack":
            case "refer":
                // Memory model operations - delegate to memory analyzer
                return HandleMemoryModelOperation(node: node, sliceType: sliceType);

            default:
                AddError(message: $"Unknown slice operation: {operationName}", location: node.Location);
                return null;
        }
    }
}
