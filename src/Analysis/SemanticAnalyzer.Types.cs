using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing type resolution, inference, and validation helpers.
/// </summary>
public partial class SemanticAnalyzer
{
        private bool IsValidType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" or "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" or "f16" or "f32" or "f64" or "f128" or "d32" or "d64" or "d128" or "bool" or "letter8" or "letter16" or "letter32" => true,
            _ => false
        };
    }

    private bool IsValidConversion(string sourceType, string targetType)
    {
        // For now, allow all conversions between numeric types
        // In a production compiler, this would have more sophisticated rules
        return IsNumericType(typeName: sourceType) && IsNumericType(typeName: targetType);
    }

    private bool IsNumericType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" or "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" or "f16" or "f32" or "f64" or "f128" or "d32" or "d64" or "d128" => true,
            _ => false
        };
    }

    private bool IsUnsignedType(string typeName)
    {
        return typeName.StartsWith(value: "u");
    }

    // Helper methods
    private TypeInfo? ResolveType(TypeExpression? typeExpr)
    {
        if (typeExpr == null)
        {
            return null;
        }

        // Check if this is a type parameter (e.g., T in a generic function)
        Symbol? symbol = _symbolTable.Lookup(name: typeExpr.Name);
        if (symbol is TypeParameterSymbol typeParam)
        {
            // Return type info marked as a generic parameter
            return new TypeInfo(Name: typeExpr.Name, IsReference: false, IsGenericParameter: true);
        }

        // Handle generic types with type arguments
        if (typeExpr.GenericArguments != null && typeExpr.GenericArguments.Count > 0)
        {
            var resolvedArgs = typeExpr.GenericArguments
                                       .Select(selector: arg => ResolveType(typeExpr: arg))
                                       .Where(predicate: t => t != null)
                                       .Cast<TypeInfo>()
                                       .ToList();

            return new TypeInfo(Name: typeExpr.Name, IsReference: false, // TODO: Determine based on base type
                GenericArguments: resolvedArgs);
        }

        return new TypeInfo(Name: typeExpr.Name, IsReference: false);
    }

    private TypeInfo? ResolveType(TypeExpression? typeExpr, SourceLocation location)
    {
        return ResolveType(typeExpr: typeExpr);
    }

    /// <summary>
    /// Resolves a generic type expression with substitution from generic bindings.
    /// Used when instantiating generic functions or classes with concrete types.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve</param>
    /// <param name="genericBindings">Map of generic parameter names to concrete types</param>
    /// <returns>Resolved type with generic parameters substituted</returns>
    private TypeInfo? ResolveGenericType(TypeExpression? typeExpr, Dictionary<string, TypeInfo> genericBindings)
    {
        if (typeExpr == null)
        {
            return null;
        }

        // If this is a generic parameter, substitute with the actual type
        if (genericBindings.TryGetValue(key: typeExpr.Name, value: out TypeInfo? actualType))
        {
            return actualType;
        }

        // If it has generic arguments, recursively resolve them
        if (typeExpr.GenericArguments != null && typeExpr.GenericArguments.Count > 0)
        {
            var resolvedArgs = typeExpr.GenericArguments
                                       .Select(selector: arg => ResolveGenericType(typeExpr: arg, genericBindings: genericBindings))
                                       .Where(predicate: t => t != null)
                                       .Cast<TypeInfo>()
                                       .ToList();

            return new TypeInfo(Name: typeExpr.Name, IsReference: false, GenericArguments: resolvedArgs);
        }

        // Regular non-generic type
        return ResolveType(typeExpr: typeExpr);
    }

    /// <summary>
    /// Validates that a concrete type satisfies generic constraints.
    /// Checks base type requirements and value/reference type constraints.
    /// </summary>
    /// <param name="genericParam">Name of the generic parameter being constrained</param>
    /// <param name="actualType">The concrete type being validated</param>
    /// <param name="constraint">The constraint to validate against</param>
    /// <param name="location">Source location for error reporting</param>
    private void ValidateGenericConstraints(string genericParam, TypeInfo actualType, GenericConstraint? constraint, SourceLocation location)
    {
        if (constraint == null)
        {
            return;
        }

        // Validate value type constraint (record)
        if (constraint.IsValueType && actualType.IsReference)
        {
            AddError(message: $"Type argument '{actualType.Name}' for generic parameter '{genericParam}' must be a value type (record)", location: location);
        }

        // Validate reference type constraint (entity)
        if (constraint.IsReferenceType && !actualType.IsReference)
        {
            AddError(message: $"Type argument '{actualType.Name}' for generic parameter '{genericParam}' must be a reference type (entity)", location: location);
        }

        // Validate base type constraints
        if (constraint.BaseTypes != null)
        {
            foreach (TypeInfo baseType in constraint.BaseTypes)
            {
                if (!IsAssignableFrom(baseType: baseType, derivedType: actualType))
                {
                    AddError(message: $"Type argument '{actualType.Name}' for generic parameter '{genericParam}' does not satisfy constraint '{baseType.Name}'", location: location);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a derived type is assignable to a base type.
    /// Used for validating generic constraints that require base types.
    /// </summary>
    private bool IsAssignableFrom(TypeInfo baseType, TypeInfo derivedType)
    {
        // Same type is always assignable
        if (baseType.Name == derivedType.Name)
        {
            return true;
        }

        // TODO: Check actual inheritance hierarchy from symbol table
        // For now, we'll just check type name equality
        // In a full implementation, this would check:
        // 1. Direct inheritance (Entity Derived from Base)
        // 2. Interface implementation (Entity implements Protocol)
        // 3. Variant case matching

        return false;
    }

    /// <summary>
    /// Performs type inference for generic method calls.
    /// Attempts to deduce generic type arguments from the method arguments.
    /// </summary>
    /// <param name="funcSymbol">The generic function being called</param>
    /// <param name="arguments">The arguments passed to the function</param>
    /// <param name="genericBindings">Output dictionary of inferred type bindings</param>
    private void InferGenericTypes(FunctionSymbol funcSymbol, List<Expression> arguments, Dictionary<string, TypeInfo> genericBindings)
    {
        if (funcSymbol.GenericParameters == null || funcSymbol.GenericParameters.Count == 0)
        {
            return;
        }

        // Match argument types with parameter types to infer generic arguments
        for (int i = 0; i < Math.Min(val1: funcSymbol.Parameters.Count, val2: arguments.Count); i++)
        {
            Parameter param = funcSymbol.Parameters[index: i];
            Expression arg = arguments[index: i];

            var argType = arg.Accept(visitor: this) as TypeInfo;
            if (argType == null || param.Type == null)
            {
                continue;
            }

            // Try to match the parameter type pattern with the argument type
            InferGenericTypeFromPattern(paramType: param.Type, argType: argType, genericBindings: genericBindings);
        }
    }

    /// <summary>
    /// Recursively matches a parameter type pattern against an argument type
    /// to infer generic type parameter bindings.
    /// </summary>
    private void InferGenericTypeFromPattern(TypeExpression paramType, TypeInfo argType, Dictionary<string, TypeInfo> genericBindings)
    {
        // If the parameter type is a generic parameter (e.g., T), bind it
        // We need to check if it's in the function's generic parameters list
        // For now, use a simple heuristic: single uppercase letter names are generic params
        if (paramType.Name.Length == 1 && char.IsUpper(c: paramType.Name[index: 0]) && !genericBindings.ContainsKey(key: paramType.Name))
        {
            genericBindings[key: paramType.Name] = argType;
            return;
        }

        // If both have generic arguments, recursively match them
        if (paramType.GenericArguments != null && argType.GenericArguments != null && paramType.GenericArguments.Count == argType.GenericArguments.Count)
        {
            for (int i = 0; i < paramType.GenericArguments.Count; i++)
            {
                InferGenericTypeFromPattern(paramType: paramType.GenericArguments[index: i], argType: argType.GenericArguments[index: i], genericBindings: genericBindings);
            }
        }
    }

    private TypeInfo InferLiteralType(object? value)
    {
        return value switch
        {
            bool => new TypeInfo(Name: "bool", IsReference: false),
            int => new TypeInfo(Name: "s32", IsReference: false),
            long => new TypeInfo(Name: "s64", IsReference: false),
            float => new TypeInfo(Name: "f32", IsReference: false),
            double => new TypeInfo(Name: "f64", IsReference: false),
            string => new TypeInfo(Name: "text", IsReference: false),
            null => new TypeInfo(Name: "none", IsReference: false),
            _ => new TypeInfo(Name: "unknown", IsReference: false)
        };
    }

    private TypeInfo InferLiteralType(object? value, SourceLocation location)
    {
        return InferLiteralType(value: value);
    }

    /// <summary>
    /// Handles Text16 literals - only supported in RazorForge, not in Suflae.
    /// Text16 literals (t16"...", t16f"...", t16r"...", t16rf"...") produce Text&lt;letter16&gt; in RazorForge.
    /// In Suflae, these literals are a compile error since Suflae doesn't support UTF-16 text.
    /// </summary>
    /// <param name="node">The literal expression node</param>
    /// <returns>TypeInfo for Text&lt;letter16&gt; in RazorForge, or error for Suflae</returns>
    private TypeInfo HandleText16Literal(LiteralExpression node)
    {
        if (_language == Language.Suflae)
        {
            AddError(message: "Text16 literals (t16\"...\") are not supported in Suflae. " + "Suflae uses Text (UTF-8) and Bytes. Use regular string literals (\"...\") for Text " + "or bytes literals (b\"...\") for raw byte data.", location: node.Location);
            // Return Text as fallback type for error recovery
            return new TypeInfo(Name: "Text", IsReference: false);
        }

        // RazorForge: Text16 literals produce Text<letter16>
        return new TypeInfo(Name: "Text<letter16>", IsReference: false);
    }

    /// <summary>
    /// Handles Suflae bytes literals (b"", br"", bf"", brf"").
    /// These are Suflae-only - they produce Bytes type for raw byte data.
    /// In RazorForge, use Text&lt;letter8&gt; for UTF-8 byte strings instead.
    /// </summary>
    /// <param name="node">The literal expression node</param>
    /// <returns>TypeInfo for Bytes in Suflae, or error for RazorForge</returns>
    private TypeInfo HandleBytesLiteral(LiteralExpression node)
    {
        if (_language == Language.RazorForge)
        {
            AddError(message: "Bytes literals (b\"...\", br\"...\", bf\"...\", brf\"...\") are Suflae-only syntax. " + "In RazorForge, use t8\"...\" for Text<letter8> (UTF-8) byte strings, " + "or use a DynamicSlice for raw byte data.", location: node.Location);
            // Return Text<letter8> as fallback type for error recovery
            return new TypeInfo(Name: "Text<letter8>", IsReference: false);
        }

        // Suflae: Bytes literals produce Bytes type
        return new TypeInfo(Name: "Bytes", IsReference: false);
    }

    /// <summary>
    /// Infers the type of a pattern literal using its explicit TokenType.
    /// This ensures typed literals like 1_s32 in patterns are correctly identified as s32
    /// instead of defaulting to s64 based on C# runtime type.
    /// </summary>
    private TypeInfo InferPatternLiteralType(object? value, TokenType literalType)
    {
        return literalType switch
        {
            // Explicitly typed integer literals (same for both languages)
            TokenType.S8Literal => new TypeInfo(Name: "s8", IsReference: false),
            TokenType.S16Literal => new TypeInfo(Name: "s16", IsReference: false),
            TokenType.S32Literal => new TypeInfo(Name: "s32", IsReference: false),
            TokenType.S64Literal => new TypeInfo(Name: "s64", IsReference: false),
            TokenType.S128Literal => new TypeInfo(Name: "s128", IsReference: false),
            TokenType.SyssintLiteral => new TypeInfo(Name: "saddr", IsReference: false),
            TokenType.U8Literal => new TypeInfo(Name: "u8", IsReference: false),
            TokenType.U16Literal => new TypeInfo(Name: "u16", IsReference: false),
            TokenType.U32Literal => new TypeInfo(Name: "u32", IsReference: false),
            TokenType.U64Literal => new TypeInfo(Name: "u64", IsReference: false),
            TokenType.U128Literal => new TypeInfo(Name: "u128", IsReference: false),
            TokenType.SysuintLiteral => new TypeInfo(Name: "uaddr", IsReference: false),

            // Untyped integer: RazorForge defaults to s64, Suflae defaults to Integer (arbitrary precision)
            TokenType.Integer => new TypeInfo(Name: _language == Language.Suflae
                ? "Integer"
                : "s64", IsReference: false),

            // Explicitly typed floating-point literals (same for both languages)
            TokenType.F16Literal => new TypeInfo(Name: "f16", IsReference: false),
            TokenType.F32Literal => new TypeInfo(Name: "f32", IsReference: false),
            TokenType.F64Literal => new TypeInfo(Name: "f64", IsReference: false),
            TokenType.F128Literal => new TypeInfo(Name: "f128", IsReference: false),
            TokenType.D32Literal => new TypeInfo(Name: "d32", IsReference: false),
            TokenType.D64Literal => new TypeInfo(Name: "d64", IsReference: false),
            TokenType.D128Literal => new TypeInfo(Name: "d128", IsReference: false),

            // Untyped decimal: RazorForge defaults to f64, Suflae defaults to Decimal (arbitrary precision)
            TokenType.Decimal => new TypeInfo(Name: _language == Language.Suflae
                ? "Decimal"
                : "f64", IsReference: false),

            // Text literals
            TokenType.TextLiteral or TokenType.FormattedText or TokenType.RawText or TokenType.RawFormattedText => new TypeInfo(Name: _language == Language.Suflae
                ? "Text"
                : "Text<letter>", IsReference: false),
            TokenType.Text8Literal or TokenType.Text8FormattedText or TokenType.Text8RawText or TokenType.Text8RawFormattedText => new TypeInfo(Name: _language == Language.Suflae
                ? "Bytes"
                : "Text<letter8>", IsReference: false),
            TokenType.Text16Literal or TokenType.Text16FormattedText or TokenType.Text16RawText or TokenType.Text16RawFormattedText => new TypeInfo(Name: "Text<letter16>", IsReference: false),

            // Suflae-only bytes literals
            TokenType.BytesLiteral or TokenType.BytesRawLiteral or TokenType.BytesFormatted or TokenType.BytesRawFormatted => new TypeInfo(Name: "Bytes", IsReference: false),

            // Duration literals - all produce Duration type
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral or TokenType.MinuteLiteral or TokenType.SecondLiteral or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral => new TypeInfo(Name: "Duration", IsReference: false),

            // Memory size literals - all produce MemorySize type
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral or TokenType.KilobitLiteral or TokenType.KibibitLiteral or TokenType.MegabyteLiteral or TokenType.MebibyteLiteral or TokenType.MegabitLiteral or TokenType.MebibitLiteral or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral or TokenType.GigabitLiteral or TokenType.GibibitLiteral or TokenType.TerabyteLiteral or TokenType.TebibyteLiteral or TokenType.TerabitLiteral or TokenType.TebibitLiteral or TokenType.PetabyteLiteral or TokenType.PebibyteLiteral or TokenType.PetabitLiteral or TokenType.PebibitLiteral => new TypeInfo(Name: "MemorySize", IsReference: false),

            // Boolean and none literals (same for both languages)
            TokenType.True or TokenType.False => new TypeInfo(Name: "Bool", IsReference: false),
            TokenType.None => new TypeInfo(Name: "none", IsReference: false),

            // Fallback to runtime type inference for unknown types
            _ => InferLiteralType(value: value)
        };
    }

    private bool IsAssignable(TypeInfo target, TypeInfo? source)
    {
        if (source == null)
        {
            return false;
        }

        // TODO: Implement proper type compatibility rules
        return target.Name == source.Name;
    }

    private bool AreComparable(TypeInfo type1, TypeInfo type2)
    {
        // If either type is a generic parameter, defer comparison to instantiation time
        if (type1.IsGenericParameter || type2.IsGenericParameter)
        {
            return true;
        }

        // TODO: Implement proper comparability rules
        // For now, allow comparison between same types and numeric types
        return type1.Name == type2.Name || IsNumericType(type: type1) && IsNumericType(type: type2);
    }

    private bool IsNumericType(TypeInfo type)
    {
        return type.Name switch
        {
            "I8" or "I16" or "I32" or "I64" or "I128" or "Isys" or "U8" or "U16" or "U32" or "U64" or "U128" or "Usys" or "F16" or "F32" or "F64" or "F128" or "D32" or "D64" or "D128" or "Integer" or "Decimal" => true, // Suflae arbitrary precision types
            _ => false
        };
    }

    private void AddError(string message, SourceLocation location)
    {
        _errors.Add(item: new SemanticError(Message: message, Location: location, FileName: _fileName));
    }

    private bool IsEntityType(TypeExpression type)
    {
        // Check if type is declared as entity in symbol table
        Symbol? symbol = _symbolTable.Lookup(name: type.Name);
        if (symbol?.Type != null)
        {
            // Check if it's marked as a reference type (entities are reference types)
            return symbol.Type.IsReference;
        }

        // Fallback to naming convention patterns for common entity types
        return type.Name.EndsWith(value: "Entity") || type.Name.EndsWith(value: "Service") || type.Name.EndsWith(value: "Controller");
    }

    /// <summary>
    /// Determines whether an assignment should use move semantics or copy semantics.
    /// In RazorForge, move semantics transfer ownership while copy semantics create a new reference.
    /// </summary>
    /// <param name="valueExpr">The expression being assigned</param>
    /// <param name="targetType">The type of the target variable</param>
    /// <returns>True if move semantics should be used, false for copy semantics</returns>
    private bool DetermineMoveSemantics(Expression valueExpr, TypeInfo targetType)
    {
        // Rule 1: Primitive types are always copied (never moved)
        if (IsPrimitiveType(type: targetType))
        {
            return false; // Copy
        }

        // Rule 2: Literals and computed expressions are always copies
        if (valueExpr is not IdentifierExpression)
        {
            return false; // Copy (new allocation)
        }

        // Rule 3: Check if the value type is copyable
        // Records are copyable (they have copy semantics)
        // Entities and wrapper types require explicit operations
        if (IsRecordType(type: targetType))
        {
            return false; // Copy (records support automatic copy)
        }

        // Rule 4: For entities and heap-allocated objects, default to move
        // unless explicitly wrapped in share!() or other wrapper operations
        if (IsEntityType(type: targetType) || IsHeapAllocatedType(type: targetType))
        {
            // Check if the expression is wrapped in a sharing operation
            // For now, assume move unless we detect explicit copy markers
            return true; // Move (transfer ownership)
        }

        // Rule 5: Default to copy for safety
        return false; // Copy
    }

    /// <summary>
    /// Checks if a type is a primitive type (built-in scalar types).
    /// </summary>
    private bool IsPrimitiveType(TypeInfo type)
    {
        string[] primitiveTypes = new[]
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "f16",
            "f32",
            "f64",
            "f128",
            "d32",
            "d64",
            "d128",
            "bool",
            "letter",
            "saddr",
            "uaddr"
        };

        return primitiveTypes.Contains(value: type.Name);
    }

    /// <summary>
    /// Checks if a type is a record type (value type with copy semantics).
    /// </summary>
    private bool IsRecordType(TypeInfo type)
    {
        // Records are non-reference types in the symbol table
        // Or follow naming conventions
        if (!type.IsReference)
        {
            // Check if it's registered as a record in the symbol table
            return true; // Non-reference types are typically records
        }

        return type.Name.EndsWith(value: "Record") || type.Name.StartsWith(value: "Record");
    }

    /// <summary>
    /// Checks if a type requires heap allocation (slices, collections, etc.).
    /// </summary>
    private bool IsHeapAllocatedType(TypeInfo type)
    {
        string[] heapTypes = new[]
        {
            "DynamicSlice",
            "List",
            "Dict",
            "Set",
            "Text"
        };

        return heapTypes.Contains(value: type.Name) || type.Name.StartsWith(value: "Retained<") || type.Name.StartsWith(value: "Shared<");
    }

    /// <summary>
    /// Checks if a type is an entity type (accepts TypeInfo instead of TypeExpression).
    /// </summary>
    private bool IsEntityType(TypeInfo type)
    {
        // Entities are reference types
        if (type.IsReference)
        {
            return true;
        }

        // Fallback to naming conventions
        return type.Name.EndsWith(value: "Entity") || type.Name.EndsWith(value: "Service") || type.Name.EndsWith(value: "Controller");
    }

    /// <summary>
    /// Validates that a pattern is compatible with the matched expression type.
    /// Binds pattern variables to the appropriate scope.
    /// </summary>
    private void ValidatePatternMatch(Pattern pattern, TypeInfo? expressionType, SourceLocation location)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                // Literal patterns: check that literal type matches expression type
                // Use the explicit LiteralType from the pattern to determine the type accurately
                TypeInfo literalType = InferPatternLiteralType(value: literalPattern.Value, literalType: literalPattern.LiteralType);
                if (expressionType != null && !AreTypesCompatible(left: literalType, right: expressionType))
                {
                    AddError(message: $"Pattern literal type '{literalType.Name}' is not compatible with expression type '{expressionType.Name}'", location: location);
                }

                break;

            case IdentifierPattern identifierPattern:
                // Identifier patterns: bind the matched value to a new variable
                if (expressionType != null)
                {
                    var patternVar = new VariableSymbol(Name: identifierPattern.Name, Type: expressionType, IsMutable: false, // Pattern variables are immutable by default
                        Visibility: VisibilityModifier.Private);
                    _symbolTable.TryDeclare(symbol: patternVar);

                    // CRITICAL: Check if this is a fallible lock token (from try_seize/check_seize/try_observe/check_observe)
                    // These return Maybe<Seized<T>>, Result<Seized<T>, E>, Maybe<Observed<T>>, or Result<Observed<T>, E>
                    // The inner token (Seized/Observed) is scoped and cannot escape
                    if (IsFallibleLockToken(type: expressionType))
                    {
                        // Register the pattern variable as a scoped token
                        // Even though it's wrapped in Maybe/Result, the success case contains a scoped token
                        RegisterScopedToken(tokenName: identifierPattern.Name);
                    }
                }

                break;

            case TypePattern typePattern:
                // Type patterns: check type compatibility and bind variable if provided
                TypeInfo? patternType = ResolveTypeExpression(typeExpr: typePattern.Type);

                if (expressionType != null && patternType != null)
                {
                    // Check if the pattern type is compatible with expression type
                    // For type patterns, we check if expressionType can be narrowed to patternType
                    if (!IsTypeNarrowable(fromType: expressionType, toType: patternType))
                    {
                        AddError(message: $"Type pattern '{patternType.Name}' cannot match expression of type '{expressionType.Name}'", location: location);
                    }

                    // Bind variable if provided
                    if (typePattern.VariableName != null)
                    {
                        var patternVar = new VariableSymbol(Name: typePattern.VariableName, Type: patternType, // Variable has the narrowed type
                            IsMutable: false, Visibility: VisibilityModifier.Private);
                        _symbolTable.TryDeclare(symbol: patternVar);
                    }
                }

                break;

            case WildcardPattern:
                // Wildcard patterns match everything, no type checking needed
                break;

            case ExpressionPattern expressionPattern:
                // Expression patterns are used for guard conditions in standalone when blocks
                // The expression should evaluate to a boolean
                var exprType = expressionPattern.Expression.Accept(visitor: this) as TypeInfo;
                if (exprType != null && exprType.Name != "Bool" && exprType.Name != "bool")
                {
                    AddError(message: $"Expression pattern must be a boolean expression, got '{exprType.Name}'", location: location);
                }

                break;

            default:
                AddError(message: $"Unknown pattern type: {pattern.GetType().Name}", location: location);
                break;
        }
    }


    /// <summary>
    /// Checks if a type can be narrowed to another type in pattern matching.
    /// Used for type patterns to validate runtime type checks.
    /// </summary>
    private bool IsTypeNarrowable(TypeInfo fromType, TypeInfo toType)
    {
        // Same type is always narrowable
        if (fromType.Name == toType.Name)
        {
            return true;
        }

        // If fromType is a base type and toType is derived (requires inheritance info)
        // For now, allow any reference type to be narrowed to any other reference type
        if (fromType.IsReference && toType.IsReference)
        {
            return true; // Runtime check will validate
        }

        // Numeric types can be narrowed if they're in the same family
        if (AreNumericTypesCompatible(type1: fromType.Name, type2: toType.Name))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if two numeric types are in the same family (signed int, unsigned int, float).
    /// </summary>
    private bool AreNumericTypesCompatible(string type1, string type2)
    {
        string[] signedInts =
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "saddr"
        };
        string[] unsignedInts =
        {
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "uaddr"
        };
        string[] floats =
        {
            "f16",
            "f32",
            "f64",
            "f128"
        };
        string[] decimals =
        {
            "d32",
            "d64",
            "d128"
        };

        return signedInts.Contains(value: type1) && signedInts.Contains(value: type2) || unsignedInts.Contains(value: type1) && unsignedInts.Contains(value: type2) || floats.Contains(value: type1) && floats.Contains(value: type2) || decimals.Contains(value: type1) && decimals.Contains(value: type2);
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        // Handle generic types
        if (typeExpr.GenericArguments != null && typeExpr.GenericArguments.Count > 0)
        {
            // Resolve generic arguments
            var resolvedArgs = new List<TypeInfo>();
            foreach (var arg in typeExpr.GenericArguments)
            {
                var resolved = ResolveTypeExpression(typeExpr: arg);
                if (resolved != null)
                {
                    resolvedArgs.Add(item: resolved);
                }
            }

            // Build the full generic type name (e.g., "Snatched<letter8>")
            string fullName = typeExpr.Name;
            if (resolvedArgs.Count > 0)
            {
                string argNames = string.Join(separator: ", ", values: resolvedArgs.Select(selector: a => a.Name));
                fullName = $"{typeExpr.Name}<{argNames}>";
            }

            // Check for special pointer types (Snatched is a raw pointer)
            bool isReference = typeExpr.Name == "Snatched" || typeExpr.Name == "List" || typeExpr.Name == "Array" || typeExpr.Name == "DynamicSlice";

            return new TypeInfo(Name: fullName, IsReference: isReference, GenericArguments: resolvedArgs.Count > 0
                ? resolvedArgs
                : null);
        }

        // Look up in symbol table
        Symbol? symbol = _symbolTable.Lookup(name: typeExpr.Name);
        if (symbol?.Type != null)
        {
            return symbol.Type;
        }

        // Check if it's a built-in type
        if (IsBuiltInType(typeName: typeExpr.Name))
        {
            return new TypeInfo(Name: typeExpr.Name, IsReference: false);
        }

        return null;
    }

    /// <summary>
    /// Checks if a type name is a built-in type.
    /// </summary>
    private bool IsBuiltInType(string typeName)
    {
        string[] builtInTypes =
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "f16",
            "f32",
            "f64",
            "f128",
            "d32",
            "d64",
            "d128",
            "bool",
            "letter",
            "letter8",
            "letter16",
            "letter32",
            "Text",
            "saddr",
            "uaddr",
            "DynamicSlice",
            "TemporarySlice",
            "Snatched" // C FFI pointer type
            // cstr and cwstr are now stdlib record types, not built-in
        };

        return builtInTypes.Contains(value: typeName);
    }

    /// <summary>
    /// Checks if a name is a valid type name (primitive, built-in, or declared type).
    /// Used for detecting failable type conversions like s32!(expr).
    /// </summary>
    private bool IsTypeName(string name)
    {
        // Check primitive types
        string[] primitiveTypes =
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "saddr",
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "uaddr",
            "f16",
            "f32",
            "f64",
            "f128",
            "d32",
            "d64",
            "d128",
            "bool",
            "letter",
            "letter8",
            "letter16",
            "Text",
            "Bytes"
        };

        if (primitiveTypes.Contains(value: name))
        {
            return true;
        }

        // Check built-in types
        if (IsBuiltInType(typeName: name))
        {
            return true;
        }

        // Check declared types in symbol table
        Symbol? symbol = _symbolTable.Lookup(name: name);
        if (symbol is TypeSymbol or StructSymbol or ClassSymbol or VariantSymbol)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates a generic member access and determines the result type.
    /// Checks that the member exists on the object type and validates type arguments.
    /// </summary>
    private TypeInfo? ValidateGenericMember(TypeInfo objectType, string memberName, List<TypeExpression> typeArguments, SourceLocation location)
    {
        // For collections, validate common generic members
        if (IsCollectionType(typeName: objectType.Name))
        {
            return ValidateCollectionGenericMember(collectionType: objectType, memberName: memberName, typeArguments: typeArguments, location: location);
        }

        // For wrapper types (Shared<T>, Hijacked<T>, etc.), validate wrapper members
        if (IsWrapperType(typeName: objectType.Name))
        {
            return ValidateWrapperGenericMember(wrapperType: objectType, memberName: memberName, typeArguments: typeArguments, location: location);
        }

        // For custom types, check if member is declared
        // For now, return unknown type - proper implementation would query the type's members
        AddError(message: $"Type '{objectType.Name}' does not have a generic member '{memberName}'", location: location);

        return null;
    }

    /// <summary>
    /// Validates generic members on collection types (List, Dict, Set, etc.).
    /// </summary>
    private TypeInfo? ValidateCollectionGenericMember(TypeInfo collectionType, string memberName, List<TypeExpression> typeArguments, SourceLocation location)
    {
        // Common collection generic members
        switch (memberName)
        {
            case "get":
            case "at":
                // Returns element type (first type parameter of collection)
                if (typeArguments.Count != 0)
                {
                    AddError(message: $"'{memberName}' does not take type arguments", location: location);
                }

                // Extract element type from collection (e.g., List<T> -> T)
                return ExtractElementType(collectionType: collectionType);

            case "map":
            case "filter":
            case "transform":
                // Generic transformation methods
                if (typeArguments.Count != 1)
                {
                    AddError(message: $"'{memberName}' requires exactly one type argument", location: location);
                    return null;
                }

                return ResolveTypeExpression(typeExpr: typeArguments[index: 0]);

            default:
                AddError(message: $"Collection type '{collectionType.Name}' does not have generic member '{memberName}'", location: location);
                return null;
        }
    }

    /// <summary>
    /// Validates generic members on wrapper types (Retained&lt;T&gt;, Shared&lt;T, Policy&gt;, etc.).
    /// </summary>
    private TypeInfo? ValidateWrapperGenericMember(TypeInfo wrapperType, string memberName, List<TypeExpression> typeArguments, SourceLocation location)
    {
        switch (memberName)
        {
            case "unwrap":
            case "get":
                // Returns the wrapped type
                if (typeArguments.Count != 0)
                {
                    AddError(message: $"'{memberName}' does not take type arguments", location: location);
                }

                return ExtractWrappedType(wrapperType: wrapperType);

            default:
                AddError(message: $"Wrapper type '{wrapperType.Name}' does not have generic member '{memberName}'", location: location);
                return null;
        }
    }

    /// <summary>
    /// Checks if a type is a collection type.
    /// </summary>
    private bool IsCollectionType(string typeName)
    {
        string[] collectionTypes =
        {
            "List",
            "Dict",
            "Set",
            "Deque",
            "PriorityQueue"
        };
        return collectionTypes.Contains(value: typeName) || collectionTypes.Any(predicate: ct => typeName.StartsWith(value: ct + "<"));
    }

    /// <summary>
    /// Checks if a type is a wrapper type.
    /// </summary>
    private bool IsWrapperType(string typeName)
    {
        string[] wrapperTypes =
        {
            "Retained",
            "Tracked",
            "Shared",
            "Hijacked",
            "Snatched"
        };
        return wrapperTypes.Contains(value: typeName) || wrapperTypes.Any(predicate: wt => typeName.StartsWith(value: wt + "<"));
    }

    /// <summary>
    /// Extracts the element type from a collection type (e.g., List&lt;s32&gt; → s32).
    /// </summary>
    private TypeInfo? ExtractElementType(TypeInfo collectionType)
    {
        // Parse generic type syntax: CollectionName<ElementType>
        string typeName = collectionType.Name;
        int startIdx = typeName.IndexOf(value: '<');
        int endIdx = typeName.LastIndexOf(value: '>');

        if (startIdx > 0 && endIdx > startIdx)
        {
            string elementTypeName = typeName.Substring(startIndex: startIdx + 1, length: endIdx - startIdx - 1);
            return new TypeInfo(Name: elementTypeName, IsReference: false);
        }

        // If not parameterized, return unknown
        return new TypeInfo(Name: "unknown", IsReference: false);
    }

    /// <summary>
    /// Extracts the wrapped type from a wrapper type (e.g., Shared&lt;Point&gt; → Point).
    /// </summary>
    private TypeInfo? ExtractWrappedType(TypeInfo wrapperType)
    {
        // Parse generic type syntax: WrapperName<WrappedType>
        string typeName = wrapperType.Name;
        int startIdx = typeName.IndexOf(value: '<');
        int endIdx = typeName.LastIndexOf(value: '>');

        if (startIdx > 0 && endIdx > startIdx)
        {
            string wrappedTypeName = typeName.Substring(startIndex: startIdx + 1, length: endIdx - startIdx - 1);
            return new TypeInfo(Name: wrappedTypeName, IsReference: true);
        }

        // If not parameterized, return unknown
        return new TypeInfo(Name: "unknown", IsReference: false);
    }
}
