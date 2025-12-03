using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing expression visitors (literal, binary, unary, member, etc.).
/// </summary>
public partial class SemanticAnalyzer
{
    /// <summary>
    /// Visits a literal expression and infers its type.
    /// </summary>
    /// <param name="node">Literal expression node</param>
    /// <returns>TypeInfo representing the literal type</returns>
    public object? VisitLiteralExpression(LiteralExpression node)
    {
        // Use the explicit LiteralType from the token to determine the type
        // This is more accurate than inferring from C# runtime type
        TypeInfo? resultType = node.LiteralType switch
        {
            // Explicitly typed integer literals (same for both languages)
            TokenType.S8Literal => GetPrimitiveTypeInfo("s8"),
            TokenType.S16Literal => GetPrimitiveTypeInfo("s16"),
            TokenType.S32Literal => GetPrimitiveTypeInfo("s32"),
            TokenType.S64Literal => GetPrimitiveTypeInfo("s64"),
            TokenType.S128Literal => GetPrimitiveTypeInfo("s128"),
            TokenType.SyssintLiteral => GetPrimitiveTypeInfo("saddr"),
            TokenType.U8Literal => GetPrimitiveTypeInfo("u8"),
            TokenType.U16Literal => GetPrimitiveTypeInfo("u16"),
            TokenType.U32Literal => GetPrimitiveTypeInfo("u32"),
            TokenType.U64Literal => GetPrimitiveTypeInfo("u64"),
            TokenType.U128Literal => GetPrimitiveTypeInfo("u128"),
            TokenType.SysuintLiteral => GetPrimitiveTypeInfo("uaddr"),

            // Untyped integer: RazorForge defaults to s64, Suflae defaults to Integer (arbitrary precision)
            TokenType.Integer => _language == Language.Suflae
                ? new TypeInfo(Name: "Integer", IsReference: false)
                : GetPrimitiveTypeInfo("s64"),

            // Explicitly typed floating-point literals (same for both languages)
            TokenType.F16Literal => GetPrimitiveTypeInfo("f16"),
            TokenType.F32Literal => GetPrimitiveTypeInfo("f32"),
            TokenType.F64Literal => GetPrimitiveTypeInfo("f64"),
            TokenType.F128Literal => GetPrimitiveTypeInfo("f128"),
            TokenType.D32Literal => GetPrimitiveTypeInfo("d32"),
            TokenType.D64Literal => GetPrimitiveTypeInfo("d64"),
            TokenType.D128Literal => GetPrimitiveTypeInfo("d128"),

            // Untyped decimal: RazorForge defaults to f64, Suflae defaults to Decimal (arbitrary precision)
            TokenType.Decimal => _language == Language.Suflae
                ? new TypeInfo(Name: "Decimal", IsReference: false)
                : GetPrimitiveTypeInfo("f64"),

            // Text literals: RazorForge has Text<letter>/Text<letter8>/Text<letter16>, Suflae has Text (UTF-8) and Bytes (no Text16)
            TokenType.TextLiteral or TokenType.FormattedText or TokenType.RawText
                or TokenType.RawFormattedText => new TypeInfo(Name: _language == Language.Suflae
                        ? "Text"
                        : "Text<letter>",
                    IsReference: false),
            TokenType.Text8Literal or TokenType.Text8FormattedText or TokenType.Text8RawText
                or TokenType.Text8RawFormattedText => new TypeInfo(
                    Name: _language == Language.Suflae
                        ? "Bytes"
                        : "Text<letter8>",
                    IsReference: false),
            // Text16 literals: Only supported in RazorForge, not in Suflae
            TokenType.Text16Literal or TokenType.Text16FormattedText or TokenType.Text16RawText
                or TokenType.Text16RawFormattedText => HandleText16Literal(node: node),

            // Suflae-only bytes literals (b"", br"", bf"", brf"")
            TokenType.BytesLiteral or TokenType.BytesRawLiteral or TokenType.BytesFormatted
                or TokenType.BytesRawFormatted => HandleBytesLiteral(node: node),

            // Duration literals - all produce Duration type
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral
                or TokenType.MinuteLiteral or TokenType.SecondLiteral
                or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral
                or TokenType.NanosecondLiteral => new TypeInfo(Name: "Duration",
                    IsReference: false),

            // Memory size literals - all produce MemorySize type
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral
                or TokenType.KilobitLiteral or TokenType.KibibitLiteral
                or TokenType.MegabyteLiteral or TokenType.MebibyteLiteral
                or TokenType.MegabitLiteral or TokenType.MebibitLiteral
                or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral
                or TokenType.GigabitLiteral or TokenType.GibibitLiteral
                or TokenType.TerabyteLiteral or TokenType.TebibyteLiteral
                or TokenType.TerabitLiteral or TokenType.TebibitLiteral
                or TokenType.PetabyteLiteral or TokenType.PebibyteLiteral
                or TokenType.PetabitLiteral
                or TokenType.PebibitLiteral =>
                new TypeInfo(Name: "MemorySize", IsReference: false),

            // Boolean and none literals (same for both languages)
            TokenType.True or TokenType.False => GetPrimitiveTypeInfo("bool"),
            TokenType.None => new TypeInfo(Name: "none", IsReference: false),
            _ => InferLiteralType(value: node.Value) // Fallback to runtime type inference
        };

        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits a list literal expression [1, 2, 3] and infers its type.
    /// </summary>
    public object? VisitListLiteralExpression(ListLiteralExpression node)
    {
        TypeInfo? elementType = null;

        // Analyze each element and infer element type from first element
        foreach (Expression element in node.Elements)
        {
            var type = element.Accept(visitor: this) as TypeInfo;
            if (elementType == null && type != null)
            {
                elementType = type;
            }
            // TODO: Check that all elements have compatible types
        }

        // Use explicit type annotation if provided
        if (node.ElementType != null)
        {
            elementType = ResolveType(typeExpr: node.ElementType);
        }

        string typeName = elementType?.Name ?? "unknown";
        var resultType = new TypeInfo(Name: "List",
            IsReference: true,
            GenericArguments: [new TypeInfo(Name: typeName, IsReference: false)]);
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits a set literal expression {1, 2, 3} and infers its type.
    /// </summary>
    public object? VisitSetLiteralExpression(SetLiteralExpression node)
    {
        TypeInfo? elementType = null;

        foreach (Expression element in node.Elements)
        {
            var type = element.Accept(visitor: this) as TypeInfo;
            if (elementType == null && type != null)
            {
                elementType = type;
            }
        }

        if (node.ElementType != null)
        {
            elementType = ResolveType(typeExpr: node.ElementType);
        }

        string typeName = elementType?.Name ?? "unknown";
        var resultType = new TypeInfo(Name: "Set",
            IsReference: true,
            GenericArguments: [new TypeInfo(Name: typeName, IsReference: false)]);
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits a dict literal expression {k: v} and infers its type.
    /// </summary>
    public object? VisitDictLiteralExpression(DictLiteralExpression node)
    {
        TypeInfo? keyType = null;
        TypeInfo? valueType = null;

        foreach ((Expression key, Expression value) in node.Pairs)
        {
            var kt = key.Accept(visitor: this) as TypeInfo;
            var vt = value.Accept(visitor: this) as TypeInfo;
            if (keyType == null && kt != null)
            {
                keyType = kt;
            }

            if (valueType == null && vt != null)
            {
                valueType = vt;
            }
        }

        if (node.KeyType != null)
        {
            keyType = ResolveType(typeExpr: node.KeyType);
        }

        if (node.ValueType != null)
        {
            valueType = ResolveType(typeExpr: node.ValueType);
        }

        string keyTypeName = keyType?.Name ?? "unknown";
        string valueTypeName = valueType?.Name ?? "unknown";
        var resultType = new TypeInfo(Name: "Dict",
            IsReference: true,
            GenericArguments:
            [
                new TypeInfo(Name: keyTypeName, IsReference: false),
                new TypeInfo(Name: valueTypeName, IsReference: false)
            ]);
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits an identifier expression and looks up its type in the symbol table.
    /// </summary>
    /// <param name="node">Identifier expression node</param>
    /// <returns>TypeInfo of the identifier</returns>
    public object? VisitIdentifierExpression(IdentifierExpression node)
    {
        Symbol? symbol = _symbolTable.Lookup(name: node.Name);
        if (symbol == null)
        {
            AddError(message: $"Undefined identifier '{node.Name}'", location: node.Location);
            return null;
        }

        // CRITICAL: Check if this source variable is invalidated by a scoped access statement
        if (IsSourceInvalidated(sourceName: node.Name))
        {
            string? accessType = GetInvalidationAccessType(sourceName: node.Name);
            AddError(
                message:
                $"Cannot access '{node.Name}' while it is being accessed via {accessType} statement. " +
                $"The source is temporarily unavailable while the scoped token exists. " +
                $"Access the data through the scoped token instead.",
                location: node.Location);
        }

        return SetResolvedType(node, symbol.Type);
    }

    /// <summary>
    /// Visits a binary expression and validates operand types.
    /// </summary>
    /// <param name="node">Binary expression node</param>
    /// <returns>TypeInfo of the result</returns>
    public object? VisitBinaryExpression(BinaryExpression node)
    {
        var leftType = node.Left.Accept(visitor: this) as TypeInfo;
        var rightType = node.Right.Accept(visitor: this) as TypeInfo;

        // Check for mixed-type arithmetic (REJECTED per user requirement)
        if (leftType != null && rightType != null && IsArithmeticOperator(op: node.Operator))
        {
            if (!AreTypesCompatible(left: leftType, right: rightType))
            {
                AddError(
                    message:
                    $"Mixed-type arithmetic is not allowed. Cannot perform {node.Operator} between {leftType.Name} and {rightType.Name}. Use explicit type conversion with {rightType.Name}!(x) or x.{rightType.Name}!().",
                    location: node.Location);
                return SetResolvedType(node, null);
            }
        }

        // TrueDivide (/) is only allowed for floating-point types
        // For integer division, use FloorDivide (//)
        if (node.Operator == BinaryOperator.TrueDivide && leftType != null)
        {
            if (leftType.IsInteger || IsIntegerType(typeName: leftType.Name))
            {
                AddError(
                    message:
                    $"True division (/) is not supported for integer type '{leftType.Name}'. " +
                    $"Use floor division (//) for integer division, or convert to a floating-point type first.",
                    location: node.Location);
                return SetResolvedType(node, null);
            }
        }

        // Comparison operators return Bool
        if (IsComparisonOperator(op: node.Operator))
        {
            return SetResolvedType(node, GetPrimitiveTypeInfo("bool"));
        }

        // Logical operators return Bool
        if (IsLogicalOperator(op: node.Operator))
        {
            return SetResolvedType(node, GetPrimitiveTypeInfo("bool"));
        }

        // Return the common type (they should be the same if we reach here)
        return SetResolvedType(node, leftType ?? rightType);
    }

    private bool IsArithmeticOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
                or BinaryOperator.TrueDivide or BinaryOperator.FloorDivide or BinaryOperator.Modulo
                or BinaryOperator.Power or BinaryOperator.AddWrap or BinaryOperator.SubtractWrap
                or BinaryOperator.MultiplyWrap or BinaryOperator.PowerWrap
                or BinaryOperator.AddSaturate or BinaryOperator.SubtractSaturate
                or BinaryOperator.MultiplySaturate or BinaryOperator.PowerSaturate
                or BinaryOperator.AddChecked or BinaryOperator.SubtractChecked
                or BinaryOperator.MultiplyChecked or BinaryOperator.FloorDivideChecked
                or BinaryOperator.ModuloChecked or BinaryOperator.PowerChecked => true,
            _ => false
        };
    }

    private bool IsComparisonOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
                or BinaryOperator.LessEqual or BinaryOperator.Greater
                or BinaryOperator.GreaterEqual or BinaryOperator.In or BinaryOperator.NotIn
                or BinaryOperator.Is or BinaryOperator.IsNot or BinaryOperator.From
                or BinaryOperator.NotFrom or BinaryOperator.Follows
                or BinaryOperator.NotFollows => true,
            _ => false
        };
    }

    private bool IsLogicalOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.And or BinaryOperator.Or => true,
            _ => false
        };
    }

    private bool AreTypesCompatible(TypeInfo left, TypeInfo right)
    {
        // If either type is a generic parameter, they are compatible
        // (concrete type checking happens at instantiation time)
        if (left.IsGenericParameter || right.IsGenericParameter)
        {
            return true;
        }

        // Types are compatible if they are exactly the same
        if (left.Name == right.Name && left.IsReference == right.IsReference)
        {
            return true;
        }

        // Allow arithmetic between pointer-sized types and their fixed-size equivalents
        // uaddr is the unsigned address type (platform-specific), compatible with u64/u32
        // saddr is the signed address type (platform-specific), compatible with s64/s32
        if (IsPointerSizedType(typeName: left.Name) || IsPointerSizedType(typeName: right.Name))
        {
            // Check both are integers of the same signedness
            bool leftUnsigned = IsUnsignedIntegerType(typeName: left.Name);
            bool rightUnsigned = IsUnsignedIntegerType(typeName: right.Name);
            bool leftSigned = IsSignedIntegerType(typeName: left.Name);
            bool rightSigned = IsSignedIntegerType(typeName: right.Name);

            // Both unsigned (u64 * uaddr) or both signed (s64 * saddr)
            if ((leftUnsigned && rightUnsigned) || (leftSigned && rightSigned))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Checks if the type is a pointer-sized integer type.
    /// </summary>
    private static bool IsPointerSizedType(string typeName)
    {
        return typeName == "uaddr" || typeName == "saddr";
    }

    /// <summary>
    /// Checks if the type is an unsigned integer type.
    /// </summary>
    private static bool IsUnsignedIntegerType(string typeName)
    {
        return typeName == "u8" || typeName == "u16" || typeName == "u32" ||
               typeName == "u64" || typeName == "u128" || typeName == "uaddr";
    }

    /// <summary>
    /// Checks if the type is a signed integer type.
    /// </summary>
    private static bool IsSignedIntegerType(string typeName)
    {
        return typeName == "s8" || typeName == "s16" || typeName == "s32" ||
               typeName == "s64" || typeName == "s128" || typeName == "saddr";
    }

    /// <summary>
    /// Visits a unary expression and validates the operand type.
    /// </summary>
    /// <param name="node">Unary expression node</param>
    /// <returns>TypeInfo of the result</returns>
    public object? VisitUnaryExpression(UnaryExpression node)
    {
        var operandType = node.Operand.Accept(visitor: this) as TypeInfo;
        // TODO: Check unary operator compatibility
        return SetResolvedType(node, operandType);
    }

    /// <summary>
    /// Visits a member access expression and validates the member exists.
    /// </summary>
    /// <param name="node">Member expression node</param>
    /// <returns>TypeInfo of the member</returns>
    public object? VisitMemberExpression(MemberExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;

        if (objectType == null)
        {
            return SetResolvedType(node, null);
        }

        string typeName = objectType.Name;

        // Check if accessing field through a wrapper type that allows transparent access
        // Hijacked<T>, Viewed<T>, Retained<T> all allow direct field access on inner type T
        if (IsTransparentWrapperType(typeName: objectType.Name,
                innerType: out string? innerTypeName))
        {
            // Unwrap to get the inner type and check member on that type
            typeName = innerTypeName ?? objectType.Name;
        }

        // Look up field type from type definition cache
        TypeInfo? fieldType = LookupFieldType(typeName: typeName, fieldName: node.PropertyName);
        if (fieldType != null)
        {
            return SetResolvedType(node, fieldType);
        }

        // Handle special "me" reference - we may not have cached the current type yet
        // In this case, allow the access and return unknown (it will be validated at code gen)
        if (node.Object is IdentifierExpression idExpr && idExpr.Name == "me")
        {
            // Allow member access on "me" - type checking will happen at code gen time
            return SetResolvedType(node, new TypeInfo(Name: "unknown", IsReference: false));
        }

        // For generic types or types not in cache, allow access but return unknown
        // This handles cases like slice types (DynamicSlice) which have built-in methods
        if (objectType.Name == "DynamicSlice" || objectType.Name == "TemporarySlice")
        {
            // Slice types have built-in fields/methods handled elsewhere
            return SetResolvedType(node, new TypeInfo(Name: "unknown", IsReference: false));
        }

        // Return unknown type to allow compilation to continue
        // Full member validation would require complete type information
        return SetResolvedType(node, new TypeInfo(Name: "unknown", IsReference: false));
    }

    /// <summary>
    /// Checks if a type is a transparent wrapper that allows direct field access.
    /// Transparent wrappers: Hijacked&lt;T&gt;, Viewed&lt;T&gt;, Retained&lt;T&gt;, Inspected&lt;T&gt;, Seized&lt;T&gt;
    /// Note: Snatched&lt;T&gt; requires explicit .reveal_as&lt;U&gt;() - not transparent
    /// </summary>
    private bool IsTransparentWrapperType(string typeName, out string? innerType)
    {
        // Match patterns: Hijacked<T>, Viewed<T>, Retained<T>, Inspected<T>, Seized<T>
        foreach (string wrapperPrefix in new[]
                 {
                     "Hijacked<",
                     "Viewed<",
                     "Retained<",
                     "Inspected<",
                     "Seized<"
                 })
        {
            if (typeName.StartsWith(value: wrapperPrefix) && typeName.EndsWith(value: ">"))
            {
                // Extract inner type T from Wrapper<T>
                innerType = typeName.Substring(startIndex: wrapperPrefix.Length,
                    length: typeName.Length - wrapperPrefix.Length - 1);
                return true;
            }
        }

        innerType = null;
        return false;
    }

    /// <summary>
    /// Checks if a type is a read-only wrapper that does not allow mutation.
    /// Read-only wrappers: Viewed&lt;T>, Inspected&lt;T>
    /// </summary>
    private bool IsReadOnlyWrapperType(string typeName)
    {
        return typeName.StartsWith(value: "Viewed<") || typeName.StartsWith(value: "Inspected<");
    }

    /// <summary>
    /// Visits an index expression and validates the indexing operation.
    /// </summary>
    /// <param name="node">Index expression node</param>
    /// <returns>TypeInfo of the indexed element</returns>
    public object? VisitIndexExpression(IndexExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        var indexType = node.Index.Accept(visitor: this) as TypeInfo;
        // TODO: Check indexing compatibility and infer element type
        return SetResolvedType(node, null);
    }

    /// <summary>
    /// Visits a conditional (ternary) expression and validates all branches.
    /// </summary>
    /// <param name="node">Conditional expression node</param>
    /// <returns>TypeInfo of the result</returns>
    public object? VisitConditionalExpression(ConditionalExpression node)
    {
        var conditionType = node.Condition.Accept(visitor: this) as TypeInfo;
        var trueType = node.TrueExpression.Accept(visitor: this) as TypeInfo;
        var falseType = node.FalseExpression.Accept(visitor: this) as TypeInfo;

        // Accept both Bool and bool for compatibility
        if (conditionType != null && conditionType.Name != "Bool" && conditionType.Name != "bool")
        {
            AddError(message: $"Conditional expression condition must be boolean",
                location: node.Location);
        }

        // TODO: Return common type of true/false branches
        return SetResolvedType(node, trueType);
    }

    /// <summary>
    /// Visits a block expression and returns the type of its value expression.
    /// </summary>
    /// <param name="node">Block expression node</param>
    /// <returns>TypeInfo of the block's value expression</returns>
    public object? VisitBlockExpression(BlockExpression node)
    {
        // A block expression evaluates to its inner expression
        var resultType = node.Value.Accept(visitor: this) as TypeInfo;
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits a lambda expression and validates parameter and return types.
    /// </summary>
    /// <param name="node">Lambda expression node</param>
    /// <returns>TypeInfo representing the function type</returns>
    public object? VisitLambdaExpression(LambdaExpression node)
    {
        // Enter scope for lambda parameters
        _symbolTable.EnterScope();

        try
        {
            foreach (Parameter param in node.Parameters)
            {
                TypeInfo? paramType = ResolveType(typeExpr: param.Type);
                var paramSymbol = new VariableSymbol(Name: param.Name,
                    Type: paramType,
                    IsMutable: false,
                    Visibility: VisibilityModifier.Private);
                _symbolTable.TryDeclare(symbol: paramSymbol);
            }

            var bodyType = node.Body.Accept(visitor: this) as TypeInfo;
            return SetResolvedType(node, bodyType);
        }
        finally
        {
            _symbolTable.ExitScope();
        }
    }

    /// <summary>
    /// Visits a chained comparison expression (e.g., a &lt; b &lt; c).
    /// </summary>
    /// <param name="node">Chained comparison expression node</param>
    /// <returns>TypeInfo of bool</returns>
    public object? VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        // Check that all operands are comparable
        TypeInfo? prevType = null;

        foreach (Expression operand in node.Operands)
        {
            var operandType = operand.Accept(visitor: this) as TypeInfo;

            if (prevType != null && operandType != null)
            {
                // TODO: Check if types are comparable
                if (!AreComparable(type1: prevType, type2: operandType))
                {
                    AddError(message: $"Cannot compare {prevType.Name} with {operandType.Name}",
                        location: node.Location);
                }
            }

            prevType = operandType;
        }

        // Chained comparisons always return boolean
        return SetResolvedType(node, GetPrimitiveTypeInfo("bool"));
    }

    /// <summary>
    /// Visits a range expression and validates the start and end values.
    /// </summary>
    /// <param name="node">Range expression node</param>
    /// <returns>TypeInfo representing the range type</returns>
    public object? VisitRangeExpression(RangeExpression node)
    {
        // Check start and end are numeric
        var startType = node.Start.Accept(visitor: this) as TypeInfo;
        var endType = node.End.Accept(visitor: this) as TypeInfo;

        if (startType != null && !IsNumericType(type: startType))
        {
            AddError(message: $"Range start must be numeric, got {startType.Name}",
                location: node.Location);
        }

        if (endType != null && !IsNumericType(type: endType))
        {
            AddError(message: $"Range end must be numeric, got {endType.Name}",
                location: node.Location);
        }

        // Check step if present
        if (node.Step != null)
        {
            var stepType = node.Step.Accept(visitor: this) as TypeInfo;
            if (stepType != null && !IsNumericType(type: stepType))
            {
                AddError(message: $"Range step must be numeric, got {stepType.Name}",
                    location: node.Location);
            }
        }

        // Range expressions return a Range<T> type
        return SetResolvedType(node, new TypeInfo(Name: "Range", IsReference: false));
    }

    /// <summary>
    /// Visits a type expression and resolves it to a TypeInfo.
    /// </summary>
    /// <param name="node">Type expression node</param>
    /// <returns>TypeInfo</returns>
    public object? VisitTypeExpression(TypeExpression node)
    {
        // Type expressions are handled during semantic analysis
        return SetResolvedType(node, null);
    }

    /// <summary>
    /// Visits a type conversion expression and validates the conversion.
    /// </summary>
    /// <param name="node">Type conversion expression node</param>
    /// <returns>TypeInfo of the target type</returns>
    public object? VisitTypeConversionExpression(TypeConversionExpression node)
    {
        // Analyze the source expression
        var sourceType = node.Expression.Accept(visitor: this) as TypeInfo;

        // Validate the target type exists
        string targetTypeName = node.TargetType;
        if (!IsValidType(typeName: targetTypeName))
        {
            _errors.Add(item: new SemanticError(Message: $"Unknown type: {targetTypeName}",
                Location: node.Location));
            return SetResolvedType(node, null);
        }

        // Check if the conversion is valid
        if (!IsValidConversion(sourceType: sourceType?.Name ?? "unknown",
                targetType: targetTypeName))
        {
            _errors.Add(item: new SemanticError(
                Message:
                $"Cannot convert from {sourceType?.Name ?? "unknown"} to {targetTypeName}",
                Location: node.Location));
            return SetResolvedType(node, null);
        }

        // Return the target type
        var resultType = PrimitiveTypes.IsPrimitive(targetTypeName)
            ? GetPrimitiveTypeInfo(targetTypeName)
            : new TypeInfo(Name: targetTypeName, IsReference: IsUnsignedType(typeName: targetTypeName));
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits a named argument expression (name: value).
    /// Named arguments allow explicit specification of which parameter receives which value.
    /// </summary>
    public object? VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        // Type check the value expression
        var resultType = node.Value.Accept(visitor: this) as TypeInfo;
        return SetResolvedType(node, resultType);
    }

    /// <summary>
    /// Visits a constructor expression (Type(field: value, ...)).
    /// Verifies the type exists and all fields are properly initialized.
    /// </summary>
    public object? VisitConstructorExpression(ConstructorExpression node)
    {
        // Type check all field value expressions
        foreach ((string Name, Expression Value) field in node.Fields)
        {
            field.Value.Accept(visitor: this);
        }

        // Look up the type to verify it exists
        Symbol? symbol = _symbolTable.Lookup(name: node.TypeName);

        // Check if it's a TypeWithConstructors (type + constructors share name)
        if (symbol is TypeWithConstructors typeWithCtors)
        {
            symbol = typeWithCtors.TypeSymbol;
        }

        if (symbol == null)
        {
            AddError(message: $"Unknown type '{node.TypeName}'", location: node.Location);
            return SetResolvedType(node, null);
        }

        // Return the type info for the struct/entity/record
        // Handle generic types by including type arguments
        string typeName = node.TypeName;
        if (node.TypeArguments != null && node.TypeArguments.Count > 0)
        {
            // For generic types, the full name includes type arguments
            // e.g., List<T> or Dict<K, V>
            typeName = node.TypeName;
        }

        // Determine if this is a reference type (entity) or value type (record)
        bool isReference = symbol is ClassSymbol;

        return SetResolvedType(node, new TypeInfo(Name: typeName, IsReference: isReference));
    }
}
