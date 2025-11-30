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
        return node.LiteralType switch
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

            // Text literals: RazorForge has Text<letter>/Text<letter8>/Text<letter16>, Suflae has Text (UTF-8) and Bytes (no Text16)
            TokenType.TextLiteral or TokenType.FormattedText or TokenType.RawText or TokenType.RawFormattedText => new TypeInfo(Name: _language == Language.Suflae
                ? "Text"
                : "Text<letter>", IsReference: false),
            TokenType.Text8Literal or TokenType.Text8FormattedText or TokenType.Text8RawText or TokenType.Text8RawFormattedText => new TypeInfo(Name: _language == Language.Suflae
                ? "Bytes"
                : "Text<letter8>", IsReference: false),
            // Text16 literals: Only supported in RazorForge, not in Suflae
            TokenType.Text16Literal or TokenType.Text16FormattedText or TokenType.Text16RawText or TokenType.Text16RawFormattedText => HandleText16Literal(node: node),

            // Suflae-only bytes literals (b"", br"", bf"", brf"")
            TokenType.BytesLiteral or TokenType.BytesRawLiteral or TokenType.BytesFormatted or TokenType.BytesRawFormatted => HandleBytesLiteral(node: node),

            // Duration literals - all produce Duration type
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral or TokenType.MinuteLiteral or TokenType.SecondLiteral or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral or TokenType.NanosecondLiteral => new TypeInfo(Name: "Duration", IsReference: false),

            // Memory size literals - all produce MemorySize type
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral or TokenType.KilobitLiteral or TokenType.KibibitLiteral or TokenType.MegabyteLiteral or TokenType.MebibyteLiteral or TokenType.MegabitLiteral or TokenType.MebibitLiteral or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral or TokenType.GigabitLiteral or TokenType.GibibitLiteral or TokenType.TerabyteLiteral or TokenType.TebibyteLiteral or TokenType.TerabitLiteral or TokenType.TebibitLiteral or TokenType.PetabyteLiteral or TokenType.PebibyteLiteral or TokenType.PetabitLiteral or TokenType.PebibitLiteral => new TypeInfo(Name: "MemorySize", IsReference: false),

            // Boolean and none literals (same for both languages)
            TokenType.True or TokenType.False => new TypeInfo(Name: "Bool", IsReference: false),
            TokenType.None => new TypeInfo(Name: "none", IsReference: false),
            _ => InferLiteralType(value: node.Value) // Fallback to runtime type inference
        };
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
        return new TypeInfo(Name: "List", IsReference: true, GenericArguments: [new TypeInfo(Name: typeName, IsReference: false)]);
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
        return new TypeInfo(Name: "Set", IsReference: true, GenericArguments: [new TypeInfo(Name: typeName, IsReference: false)]);
    }

    /// <summary>
    /// Visits a dict literal expression {k: v} and infers its type.
    /// </summary>
    public object? VisitDictLiteralExpression(DictLiteralExpression node)
    {
        TypeInfo? keyType = null;
        TypeInfo? valueType = null;

        foreach (var (key, value) in node.Pairs)
        {
            var kt = key.Accept(visitor: this) as TypeInfo;
            var vt = value.Accept(visitor: this) as TypeInfo;
            if (keyType == null && kt != null) keyType = kt;
            if (valueType == null && vt != null) valueType = vt;
        }

        if (node.KeyType != null) keyType = ResolveType(typeExpr: node.KeyType);
        if (node.ValueType != null) valueType = ResolveType(typeExpr: node.ValueType);

        string keyTypeName = keyType?.Name ?? "unknown";
        string valueTypeName = valueType?.Name ?? "unknown";
        return new TypeInfo(Name: "Dict", IsReference: true, GenericArguments:
        [
            new TypeInfo(Name: keyTypeName, IsReference: false),
            new TypeInfo(Name: valueTypeName, IsReference: false)
        ]);
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
            AddError(message: $"Cannot access '{node.Name}' while it is being accessed via {accessType} statement. " + $"The source is temporarily unavailable while the scoped token exists. " + $"Access the data through the scoped token instead.", location: node.Location);
        }

        return symbol.Type;
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
                AddError(message: $"Mixed-type arithmetic is not allowed. Cannot perform {node.Operator} between {leftType.Name} and {rightType.Name}. Use explicit type conversion with {rightType.Name}!(x) or x.{rightType.Name}!().", location: node.Location);
                return null;
            }
        }

        // Comparison operators return Bool
        if (IsComparisonOperator(op: node.Operator))
        {
            return new TypeInfo(Name: "Bool", IsReference: false);
        }

        // Logical operators return Bool
        if (IsLogicalOperator(op: node.Operator))
        {
            return new TypeInfo(Name: "Bool", IsReference: false);
        }

        // Return the common type (they should be the same if we reach here)
        return leftType ?? rightType;
    }

    private bool IsArithmeticOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or BinaryOperator.TrueDivide or BinaryOperator.Divide or BinaryOperator.Modulo or BinaryOperator.Power or BinaryOperator.AddWrap or BinaryOperator.SubtractWrap or BinaryOperator.MultiplyWrap or BinaryOperator.DivideWrap or BinaryOperator.ModuloWrap or BinaryOperator.PowerWrap or BinaryOperator.AddSaturate or BinaryOperator.SubtractSaturate or BinaryOperator.MultiplySaturate or BinaryOperator.DivideSaturate or BinaryOperator.ModuloSaturate or BinaryOperator.PowerSaturate or BinaryOperator.AddUnchecked or BinaryOperator.SubtractUnchecked or BinaryOperator.MultiplyUnchecked or BinaryOperator.DivideUnchecked or BinaryOperator.ModuloUnchecked or BinaryOperator.PowerUnchecked or BinaryOperator.AddChecked or BinaryOperator.SubtractChecked or BinaryOperator.MultiplyChecked or BinaryOperator.DivideChecked or BinaryOperator.ModuloChecked or BinaryOperator.PowerChecked => true,
            _ => false
        };
    }

    private bool IsComparisonOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual or BinaryOperator.In or BinaryOperator.NotIn or BinaryOperator.Is or BinaryOperator.IsNot or BinaryOperator.From or BinaryOperator.NotFrom or BinaryOperator.Follows or BinaryOperator.NotFollows => true,
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
        return left.Name == right.Name && left.IsReference == right.IsReference;
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
        return operandType;
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
            return null;
        }

        // Check if accessing field through a wrapper type that allows transparent access
        // Hijacked<T>, Viewed<T>, Retained<T> all allow direct field access on inner type T
        if (IsTransparentWrapperType(typeName: objectType.Name, innerType: out string? innerTypeName))
        {
            // Unwrap to get the inner type and check member on that type
            // For now, we'll return a generic TypeInfo - full field validation would need type lookup
            // TODO: Look up field type from type definition of innerTypeName
            return new TypeInfo(Name: "unknown", IsReference: false);
        }

        // TODO: Check if member exists on objectType directly
        return null;
    }

    /// <summary>
    /// Checks if a type is a transparent wrapper that allows direct field access.
    /// Transparent wrappers: Hijacked&lt;T&gt;, Viewed&lt;T&gt;, Retained&lt;T&gt;, Observed&lt;T&gt;, Seized&lt;T&gt;
    /// Note: Snatched&lt;T&gt; requires explicit .reveal_as&lt;U&gt;() - not transparent
    /// </summary>
    private bool IsTransparentWrapperType(string typeName, out string? innerType)
    {
        // Match patterns: Hijacked<T>, Viewed<T>, Retained<T>, Observed<T>, Seized<T>
        foreach (string wrapperPrefix in new[]
                 {
                     "Hijacked<",
                     "Viewed<",
                     "Retained<",
                     "Observed<",
                     "Seized<"
                 })
        {
            if (typeName.StartsWith(value: wrapperPrefix) && typeName.EndsWith(value: ">"))
            {
                // Extract inner type T from Wrapper<T>
                innerType = typeName.Substring(startIndex: wrapperPrefix.Length, length: typeName.Length - wrapperPrefix.Length - 1);
                return true;
            }
        }

        innerType = null;
        return false;
    }

    /// <summary>
    /// Checks if a type is a read-only wrapper that does not allow mutation.
    /// Read-only wrappers: Viewed&lt;T>, Observed&lt;T>
    /// </summary>
    private bool IsReadOnlyWrapperType(string typeName)
    {
        return typeName.StartsWith(value: "Viewed<") || typeName.StartsWith(value: "Observed<");
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
        // TODO: Check indexing compatibility
        return null;
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

        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError(message: $"Conditional expression condition must be boolean", location: node.Location);
        }

        // TODO: Return common type of true/false branches
        return trueType;
    }

    /// <summary>
    /// Visits a block expression and returns the type of its value expression.
    /// </summary>
    /// <param name="node">Block expression node</param>
    /// <returns>TypeInfo of the block's value expression</returns>
    public object? VisitBlockExpression(BlockExpression node)
    {
        // A block expression evaluates to its inner expression
        return node.Value.Accept(visitor: this);
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
                var paramSymbol = new VariableSymbol(Name: param.Name, Type: paramType, IsMutable: false, Visibility: VisibilityModifier.Private);
                _symbolTable.TryDeclare(symbol: paramSymbol);
            }

            return node.Body.Accept(visitor: this);
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
                    AddError(message: $"Cannot compare {prevType.Name} with {operandType.Name}", location: node.Location);
                }
            }

            prevType = operandType;
        }

        // Chained comparisons always return boolean
        return new TypeInfo(Name: "Bool", IsReference: false);
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
            AddError(message: $"Range start must be numeric, got {startType.Name}", location: node.Location);
        }

        if (endType != null && !IsNumericType(type: endType))
        {
            AddError(message: $"Range end must be numeric, got {endType.Name}", location: node.Location);
        }

        // Check step if present
        if (node.Step != null)
        {
            var stepType = node.Step.Accept(visitor: this) as TypeInfo;
            if (stepType != null && !IsNumericType(type: stepType))
            {
                AddError(message: $"Range step must be numeric, got {stepType.Name}", location: node.Location);
            }
        }

        // Range expressions return a Range<T> type
        return new TypeInfo(Name: "Range", IsReference: false);
    }

    /// <summary>
    /// Visits a type expression and resolves it to a TypeInfo.
    /// </summary>
    /// <param name="node">Type expression node</param>
    /// <returns>TypeInfo</returns>
    public object? VisitTypeExpression(TypeExpression node)
    {
        // Type expressions are handled during semantic analysis
        return null;
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
            _errors.Add(item: new SemanticError(Message: $"Unknown type: {targetTypeName}", Location: node.Location));
            return null;
        }

        // Check if the conversion is valid
        if (!IsValidConversion(sourceType: sourceType?.Name ?? "unknown", targetType: targetTypeName))
        {
            _errors.Add(item: new SemanticError(Message: $"Cannot convert from {sourceType?.Name ?? "unknown"} to {targetTypeName}", Location: node.Location));
            return null;
        }

        // Return the target type
        return new TypeInfo(Name: targetTypeName, IsReference: IsUnsignedType(typeName: targetTypeName));
    }

    /// <summary>
    /// Visits a named argument expression (name: value).
    /// Named arguments allow explicit specification of which parameter receives which value.
    /// </summary>
    public object? VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        // Type check the value expression
        return node.Value.Accept(visitor: this);
    }

    /// <summary>
    /// Visits a struct literal expression (Type { field: value, ... }).
    /// Verifies the type exists and all fields are properly initialized.
    /// </summary>
    public object? VisitStructLiteralExpression(StructLiteralExpression node)
    {
        // Type check all field value expressions
        foreach (var field in node.Fields)
        {
            field.Value.Accept(visitor: this);
        }

        // TODO: Verify struct type exists and fields match
        return null;
    }
}
