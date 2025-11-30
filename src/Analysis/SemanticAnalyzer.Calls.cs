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
                    return new TypeInfo(Name: baseTypeName, IsReference: false);
                }
            }

            // Check for error intrinsics (verify!, breach!, stop!)
            if (IsErrorIntrinsic(functionName: functionName))
            {
                // Validate arguments for each intrinsic
                if (functionName == "verify!")
                {
                    if (node.Arguments.Count == 0)
                    {
                        AddError(message: "verify!() requires at least one argument (condition)", location: node.Location);
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
                return new TypeInfo(Name: "void", IsReference: false);
            }

            if (IsNonGenericDangerZoneFunction(functionName: functionName))
            {
                // Only allow these functions in danger mode
                if (!_isInDangerMode)
                {
                    _errors.Add(item: new SemanticError(Message: $"Danger zone function '{functionName}!' can only be used inside danger blocks", Location: node.Location));
                    return new TypeInfo(Name: "void", IsReference: false);
                }

                return ValidateNonGenericDangerZoneFunction(node: node, functionName: functionName);
            }
        }

        // CRITICAL: Detect memory operation method calls (ending with '!')
        // These are the core operations of RazorForge's memory model
        if (node.Callee is MemberExpression memberExpr && IsMemoryOperation(methodName: memberExpr.PropertyName))
        {
            // Route through specialized memory operation handler
            return HandleMemoryOperationCall(memberExpr: memberExpr, operationName: memberExpr.PropertyName, arguments: node.Arguments, location: node.Location);
        }

        // Handle namespaced function calls (e.g., Console.show_line())
        // When callee is a MemberExpression with an IdentifierExpression as object,
        // check if this is a namespaced function call before treating it as a method call
        if (node.Callee is MemberExpression namespacedCall && namespacedCall.Object is IdentifierExpression namespaceId)
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
                    return func.ReturnType;
                }
                else if (funcSymbol is FunctionOverloadSet overloadSet && overloadSet.Overloads.Count > 0)
                {
                    // TODO: Proper overload resolution based on argument types
                    // For now, return the first overload's return type
                    return overloadSet.Overloads[index: 0].ReturnType;
                }

                return funcSymbol.Type;
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

        // TODO: Return function's actual return type based on signature
        return functionType;
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
            isContainerStorageMethod = memberCall.PropertyName is "push" or "append" or "insert" or "add" or "set" or "put" or "enqueue" or "push_front" or "push_back";
        }

        // Track Hijacked tokens to detect duplicates
        var hijackedTokens = new HashSet<string>();

        foreach (Expression arg in node.Arguments)
        {
            // Rule 1: Check for inline-only method calls being stored in containers
            if (isContainerStorageMethod && IsInlineOnlyMethodCall(expr: arg, methodName: out string? methodName))
            {
                AddError(message: $"Cannot store result of '.{methodName}()' in a container. " + $"Inline tokens (Viewed<T>, Hijacked<T>) cannot be stored. " + $"Extract the value first, or use ownership transfer with .consume().", location: node.Location);
            }

            // Rule 2: Check for scoped tokens being stored in containers
            if (isContainerStorageMethod && arg is IdentifierExpression argIdent && IsScopedToken(variableName: argIdent.Name))
            {
                AddError(message: $"Cannot store scoped token '{argIdent.Name}' in a container. " + $"Scoped tokens are bound to their scope and cannot escape. " + $"Extract the value first, or restructure to keep the token local.", location: node.Location);
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
                        AddError(message: $"Cannot pass the same Hijacked<T> token '{tokenIdent.Name}' twice in a single call. " + $"Hijacked<T> represents unique exclusive access and cannot be duplicated.", location: node.Location);
                    }
                }
            }
        }
    }
}
