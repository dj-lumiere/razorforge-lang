using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing call expression handling and argument validation.
/// </summary>
public partial class SemanticAnalyzer
{
    /// <summary>
    /// Analyze function calls with special handling for memory operation methods.
    ///
    /// This is where the magic happens for memory operations like obj.retain(), obj.share(), etc.
    /// The analyzer detects method calls ending with '!' and routes them through the memory
    /// analyzer for proper ownership tracking and safety validation.
    ///
    /// Memory operations are the core of RazorForge's explicit memory model, allowing
    /// programmers to transform objects between different wrapper types (Owned, Retained,
    /// Shared, Hijacked, etc.) with compile-time safety guarantees.
    ///
    /// Regular function calls are handled with standard type checking and argument validation.
    /// </summary>
    public object? VisitCallExpression(CallExpression node)
    {
        // Check if this is a standalone danger zone function call (address_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;

            // Check for failable type conversion: s32!(expr), u64!(expr), Text!(expr), etc.
            // Type conversion functions end with '!' and the base name is a type
            if (functionName.EndsWith(value: "!"))
            {
                string baseTypeName = functionName.TrimEnd(trimChar: '!');
                if (IsTypeName(name: baseTypeName))
                {
                    // This is a failable type conversion
                    // Type check all arguments (should be exactly 1)
                    foreach (Expression arg in node.Arguments)
                    {
                        arg.Accept(visitor: this);
                    }

                    // Return the target type as the result type
                    var resultType = PrimitiveTypes.IsPrimitive(baseTypeName)
                        ? GetPrimitiveTypeInfo(baseTypeName)
                        : new TypeInfo(Name: baseTypeName, IsReference: false);
                    return SetResolvedType(node, resultType);
                }
            }

            // Check for type constructor calls: Type(args) where Type is a known type
            // This handles cases like MemorySize(bytes), DynamicSlice(size), etc.
            if (IsTypeName(name: functionName))
            {
                // This is a constructor call
                // Type check all arguments
                foreach (Expression arg in node.Arguments)
                {
                    arg.Accept(visitor: this);
                }

                // Return the constructed type
                Symbol? symbol = _symbolTable.Lookup(name: functionName);
                bool isReference = symbol is ClassSymbol;
                var resultType = new TypeInfo(Name: functionName, IsReference: isReference);
                return SetResolvedType(node, resultType);
            }

            // Check for error intrinsics (verify!, breach!, stop!)
            if (IsErrorIntrinsic(functionName: functionName))
            {
                // Validate arguments for each intrinsic
                if (functionName == "verify!")
                {
                    if (node.Arguments.Count == 0)
                    {
                        AddError(message: "verify!() requires at least one argument (condition)",
                            location: node.Location);
                    }
                    else
                    {
                        // Type check the condition argument
                        node.Arguments[index: 0]
                            .Accept(visitor: this);
                        // Optionally type check message argument
                        if (node.Arguments.Count > 1)
                        {
                            node.Arguments[index: 1]
                                .Accept(visitor: this);
                        }
                    }
                }
                else
                {
                    // breach! and stop! have optional message argument
                    foreach (Expression arg in node.Arguments)
                    {
                        arg.Accept(visitor: this);
                    }
                }

                // Error intrinsics return void (they don't return on failure)
                return SetResolvedType(node, new TypeInfo(Name: "void", IsReference: false));
            }

            if (IsNonGenericDangerZoneFunction(functionName: functionName))
            {
                // Only allow these functions in danger mode
                if (!_isInDangerMode)
                {
                    _errors.Add(item: new SemanticError(
                        Message:
                        $"Danger zone function '{functionName}!' can only be used inside danger blocks",
                        Location: node.Location));
                    return SetResolvedType(node, new TypeInfo(Name: "void", IsReference: false));
                }

                var dangerResult = ValidateNonGenericDangerZoneFunction(node: node,
                    functionName: functionName) as TypeInfo;
                return SetResolvedType(node, dangerResult);
            }
        }

        // CRITICAL: Detect memory operation method calls (ending with '!')
        // These are the core operations of RazorForge's memory model
        if (node.Callee is MemberExpression memberExpr &&
            IsMemoryOperation(methodName: memberExpr.PropertyName))
        {
            // Route through specialized memory operation handler
            var memOpResult = HandleMemoryOperationCall(memberExpr: memberExpr,
                operationName: memberExpr.PropertyName,
                arguments: node.Arguments,
                location: node.Location);
            return SetResolvedType(node, memOpResult);
        }

        // Handle namespaced function calls (e.g., Console.show_line())
        // When callee is a MemberExpression with an IdentifierExpression as object,
        // check if this is a namespaced function call before treating it as a method call
        if (node.Callee is MemberExpression namespacedCall &&
            namespacedCall.Object is IdentifierExpression namespaceId)
        {
            string qualifiedName = $"{namespaceId.Name}.{namespacedCall.PropertyName}";
            Symbol? funcSymbol = _symbolTable.Lookup(name: qualifiedName);
            if (funcSymbol != null)
            {
                // Type check all arguments
                foreach (Expression arg in node.Arguments)
                {
                    arg.Accept(visitor: this);
                }

                // Return the function's return type
                if (funcSymbol is FunctionSymbol func)
                {
                    return SetResolvedType(node, func.ReturnType);
                }
                else if (funcSymbol is FunctionOverloadSet overloadSet &&
                         overloadSet.Overloads.Count > 0)
                {
                    // TODO: Proper overload resolution based on argument types
                    // For now, return the first overload's return type
                    return SetResolvedType(node, overloadSet.Overloads[index: 0].ReturnType);
                }

                return SetResolvedType(node, funcSymbol.Type);
            }
        }

        // Handle method calls with known return types (e.g., obj.to_uaddr(), obj.to_s64())
        if (node.Callee is MemberExpression methodCall)
        {
            // Type check the object
            var objectType = methodCall.Object.Accept(visitor: this) as TypeInfo;

            // Check for known type conversion methods from Integral feature
            TypeInfo? methodReturnType = GetKnownMethodReturnType(
                objectType: objectType,
                methodName: methodCall.PropertyName);

            if (methodReturnType != null)
            {
                // Type check all arguments
                foreach (Expression arg in node.Arguments)
                {
                    arg.Accept(visitor: this);
                }

                return SetResolvedType(node, methodReturnType);
            }
        }

        // Standard function call type checking
        var functionType = node.Callee.Accept(visitor: this) as TypeInfo;

        // CRITICAL: Validate arguments for inline-only and scoped token rules
        ValidateCallArguments(node: node);

        // Type check all arguments
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // If the callee is a callable type (Routine<(Args), Return>), extract the return type
        if (functionType != null && functionType.Name == "Routine")
        {
            // Check if this is a Routine with generic arguments
            if (functionType.IsGeneric && functionType.GenericArguments != null &&
                functionType.GenericArguments.Count >= 2)
            {
                // The last generic argument is the return type
                // Format: Routine<ArgsTuple, ReturnType>
                return SetResolvedType(node, functionType.GenericArguments[index: functionType.GenericArguments.Count - 1]);
            }

            // Try parsing from FullName as fallback
            TypeInfo? returnType = ExtractRoutineReturnType(routineType: functionType.FullName);
            if (returnType != null)
            {
                return SetResolvedType(node, returnType);
            }
        }

        // Return function's actual return type based on signature
        return SetResolvedType(node, functionType);
    }

    /// <summary>
    /// Extracts the return type from a Routine type string.
    /// Parses formats like "Routine<(T), bool>" to extract "bool".
    /// </summary>
    private static TypeInfo? ExtractRoutineReturnType(string routineType)
    {
        // Format: Routine<(ArgTypes), ReturnType>
        // Find the last comma followed by space and the return type
        int lastComma = routineType.LastIndexOf(value: ", ");
        if (lastComma < 0)
        {
            return null;
        }

        // Extract the return type (everything between last comma and closing >)
        string remaining = routineType.Substring(startIndex: lastComma + 2);
        if (remaining.EndsWith(value: ">"))
        {
            string returnTypeName = remaining.Substring(startIndex: 0, length: remaining.Length - 1);
            return new TypeInfo(Name: returnTypeName, IsReference: false);
        }

        return null;
    }

    /// <summary>
    /// Gets the return type for known methods (type conversion, feature methods, etc.).
    /// This handles methods that are not explicitly declared but are part of features/interfaces.
    /// </summary>
    /// <param name="objectType">Type of the object the method is called on</param>
    /// <param name="methodName">Name of the method being called</param>
    /// <returns>Return type if known, null otherwise</returns>
    private TypeInfo? GetKnownMethodReturnType(TypeInfo? objectType, string methodName)
    {
        // Type conversion methods from Integral feature (and similar patterns)
        // These return the target type with proper protocols
        return methodName switch
        {
            // Integer conversion methods - use protocol-based types
            "to_uaddr" => GetPrimitiveTypeInfo("uaddr"),
            "to_saddr" => GetPrimitiveTypeInfo("saddr"),
            "to_u8" => GetPrimitiveTypeInfo("u8"),
            "to_u16" => GetPrimitiveTypeInfo("u16"),
            "to_u32" => GetPrimitiveTypeInfo("u32"),
            "to_u64" => GetPrimitiveTypeInfo("u64"),
            "to_u128" => GetPrimitiveTypeInfo("u128"),
            "to_s8" => GetPrimitiveTypeInfo("s8"),
            "to_s16" => GetPrimitiveTypeInfo("s16"),
            "to_s32" => GetPrimitiveTypeInfo("s32"),
            "to_s64" => GetPrimitiveTypeInfo("s64"),
            "to_s128" => GetPrimitiveTypeInfo("s128"),
            // Float conversion methods
            "to_f16" => GetPrimitiveTypeInfo("f16"),
            "to_f32" => GetPrimitiveTypeInfo("f32"),
            "to_f64" => GetPrimitiveTypeInfo("f64"),
            "to_f128" => GetPrimitiveTypeInfo("f128"),
            // Decimal conversion methods
            "to_d32" => GetPrimitiveTypeInfo("d32"),
            "to_d64" => GetPrimitiveTypeInfo("d64"),
            "to_d128" => GetPrimitiveTypeInfo("d128"),
            // Common utility methods
            "len" or "length" or "count" => GetPrimitiveTypeInfo("uaddr"),
            "is_empty" => GetPrimitiveTypeInfo("bool"),
            "bytes" => GetPrimitiveTypeInfo("uaddr"), // MemorySize.bytes()
            // Index resolution methods (BackIndex, Range)
            "resolve" => GetPrimitiveTypeInfo("uaddr"), // BackIndex.resolve()
            "resolve_start" => GetPrimitiveTypeInfo("uaddr"), // Range.resolve_start()
            "resolve_end" => GetPrimitiveTypeInfo("uaddr"), // Range.resolve_end()
            "get_step" => GetPrimitiveTypeInfo("uaddr"), // Range.get_step()
            // String/Text methods
            "to_text" => new TypeInfo(Name: "Text<letter>", IsReference: false),
            _ => null
        };
    }

    /// <summary>
    /// Validates function call arguments for memory safety rules:
    /// 1. Inline-only tokens (.view()/.hijack()) cannot be passed to container methods (push, insert, etc.)
    /// 2. Scoped tokens cannot be passed to container methods
    /// 3. Same Hijacked&lt;T&gt; token cannot be passed twice in the same call
    /// </summary>
    private void ValidateCallArguments(CallExpression node)
    {
        // Check for container methods that store values
        bool isContainerStorageMethod = false;
        if (node.Callee is MemberExpression memberCall)
        {
            isContainerStorageMethod = memberCall.PropertyName is "push" or "append" or "insert"
                or "add" or "set" or "put" or "enqueue" or "push_front" or "push_back";
        }

        // Track Hijacked tokens to detect duplicates
        var hijackedTokens = new HashSet<string>();

        foreach (Expression arg in node.Arguments)
        {
            // Rule 1: Check for inline-only method calls being stored in containers
            if (isContainerStorageMethod &&
                IsInlineOnlyMethodCall(expr: arg, methodName: out string? methodName))
            {
                AddError(message: $"Cannot store result of '.{methodName}()' in a container. " +
                                  $"Inline tokens (Viewed<T>, Hijacked<T>) cannot be stored. " +
                                  $"Extract the value first, or use ownership transfer with .consume().",
                    location: node.Location);
            }

            // Rule 2: Check for scoped tokens being stored in containers
            if (isContainerStorageMethod && arg is IdentifierExpression argIdent &&
                IsScopedToken(variableName: argIdent.Name))
            {
                AddError(message: $"Cannot store scoped token '{argIdent.Name}' in a container. " +
                                  $"Scoped tokens are bound to their scope and cannot escape. " +
                                  $"Extract the value first, or restructure to keep the token local.",
                    location: node.Location);
            }

            // Rule 3: Check for duplicate Hijacked<T> tokens in same call
            if (arg is IdentifierExpression tokenIdent)
            {
                TypeInfo? tokenType = _symbolTable.Lookup(name: tokenIdent.Name)
                                                 ?.Type;
                if (tokenType != null && tokenType.Name.StartsWith(value: "Hijacked<"))
                {
                    if (!hijackedTokens.Add(item: tokenIdent.Name))
                    {
                        AddError(
                            message:
                            $"Cannot pass the same Hijacked<T> token '{tokenIdent.Name}' twice in a single call. " +
                            $"Hijacked<T> represents unique exclusive access and cannot be duplicated.",
                            location: node.Location);
                    }
                }
            }
        }
    }
}
