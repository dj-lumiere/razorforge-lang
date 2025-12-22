using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser; // For ParseException

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence climbing chain).
/// </summary>
public partial class SuflaeParser
{
    /// <summary>
    /// Entry point for expression parsing. Delegates to the assignment level.
    /// </summary>
    /// <returns>The parsed <see cref="Expression"/> AST node.</returns>
    /// <remarks>Audited at 2025.12.21</remarks>
    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    /// <summary>
    /// Parses assignment expressions including compound assignments (lowest precedence).
    /// Syntax: <c>target = value</c>, <c>target += value</c>, etc.
    /// Right-associative to support chained assignments.
    /// Compound assignments are desugared: <c>a += b</c> becomes <c>a = a + b</c>.
    /// </summary>
    /// <returns>The parsed expression, possibly an assignment.</returns>
    private Expression ParseAssignment()
    {
        Expression expr = ParseInlineConditional();
        Expression value;
        // Check for simple assignment
        if (Match(type: TokenType.Assign))
        {
            value = ParseAssignment();
            return new BinaryExpression(Left: expr,
                Operator: BinaryOperator.Assign,
                Right: value,
                Location: expr.Location);
        }

        // Check for compound assignment operators
        BinaryOperator? compoundOp = TryMatchCompoundAssignment();
        if (!compoundOp.HasValue)
        {
            return expr;
        }

        value = ParseAssignment();
        // Desugar: a += b becomes a = a + b
        Expression binaryExpr = new BinaryExpression(Left: expr,
            Operator: compoundOp.Value,
            Right: value,
            Location: expr.Location);
        return new BinaryExpression(Left: expr,
            Operator: BinaryOperator.Assign,
            Right: binaryExpr,
            Location: expr.Location);
    }

    /// <summary>
    /// Tries to match a compound assignment operator and returns the corresponding binary operator.
    /// Returns null if the current token is not a compound assignment.
    /// </summary>
    private BinaryOperator? TryMatchCompoundAssignment()
    {
        TokenType current = CurrentToken.Type;
        BinaryOperator? op = current switch
        {
            TokenType.PlusAssign => BinaryOperator.Add,
            TokenType.MinusAssign => BinaryOperator.Subtract,
            TokenType.StarAssign => BinaryOperator.Multiply,
            TokenType.SlashAssign => BinaryOperator.TrueDivide,
            TokenType.DivideAssign => BinaryOperator.FloorDivide,
            TokenType.PercentAssign => BinaryOperator.Modulo,
            TokenType.PowerAssign => BinaryOperator.Power,
            TokenType.AmpersandAssign => BinaryOperator.BitwiseAnd,
            TokenType.PipeAssign => BinaryOperator.BitwiseOr,
            TokenType.CaretAssign => BinaryOperator.BitwiseXor,
            TokenType.LeftShiftAssign => BinaryOperator.ArithmeticLeftShift,
            TokenType.RightShiftAssign => BinaryOperator.ArithmeticRightShift,
            TokenType.LogicalLeftShiftAssign => BinaryOperator.LogicalLeftShift,
            TokenType.LogicalRightShiftAssign => BinaryOperator.LogicalRightShift,
            TokenType.NoneCoalesceAssign => BinaryOperator.NoneCoalesce,
            // Overflow variant compound assignments
            TokenType.PlusWrapAssign => BinaryOperator.AddWrap,
            TokenType.MinusWrapAssign => BinaryOperator.SubtractWrap,
            TokenType.MultiplyWrapAssign => BinaryOperator.MultiplyWrap,
            TokenType.PowerWrapAssign => BinaryOperator.PowerWrap,
            TokenType.PlusSaturateAssign => BinaryOperator.AddSaturate,
            TokenType.MinusSaturateAssign => BinaryOperator.SubtractSaturate,
            TokenType.MultiplySaturateAssign => BinaryOperator.MultiplySaturate,
            TokenType.PowerSaturateAssign => BinaryOperator.PowerSaturate,
            _ => null
        };

        if (op.HasValue)
        {
            Advance(); // Consume the compound assignment token
        }

        return op;
    }

    /// <summary>
    /// Parses inline conditional expressions.
    /// Syntax: <c>if condition then thenExpr else elseExpr</c>
    /// Note: This is expression-level only; block-level if uses ParseIfStatement.
    /// </summary>
    /// <returns>The parsed expression, possibly a conditional.</returns>
    private Expression ParseInlineConditional()
    {
        // Check for inline if-then-else expression
        if (Match(type: TokenType.If))
        {
            SourceLocation location = GetLocation(token: PeekToken(offset: -1));
            Expression condition = ParseNoneCoalesce();

            Consume(type: TokenType.Then, errorMessage: "Expected 'then' after condition in inline if");
            Expression thenExpr = ParseNoneCoalesce();

            Consume(type: TokenType.Else, errorMessage: "Expected 'else' in inline if expression");
            Expression elseExpr = ParseNoneCoalesce();

            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: location);
        }

        return ParseNoneCoalesce();
    }

    /// <summary>
    /// Parses none-coalescing expressions.
    /// Syntax: <c>a ?? b</c>
    /// Returns a if a is not None, otherwise returns b.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseNoneCoalesce()
    {
        Expression expr = ParseLogicalOr();

        while (Match(type: TokenType.NoneCoalesce))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseLogicalOr();
            expr = new BinaryExpression(Left: expr,
                Operator: BinaryOperator.NoneCoalesce,
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses logical OR expressions.
    /// Syntax: <c>a or b</c>
    /// Left-associative, short-circuit evaluation.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseLogicalOr()
    {
        Expression expr = ParseRange();

        while (Match(type: TokenType.Or))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseRange();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses range expressions.
    /// Syntax: <c>start to end</c>, <c>start to end by step</c>,
    /// <c>start downto end</c>, or <c>start downto end by step</c>
    /// Used for iteration ranges and slicing.
    /// </summary>
    /// <returns>The parsed expression, possibly a range.</returns>
    private Expression ParseRange()
    {
        Expression expr = ParseLogicalAnd();

        // Handle ascending range expressions: A to B or A to B by C
        if (Match(type: TokenType.To))
        {
            Expression end = ParseLogicalAnd();
            Expression? step = null;

            if (Match(type: TokenType.By))
            {
                step = ParseLogicalAnd();
            }

            return new RangeExpression(Start: expr,
                End: end,
                Step: step,
                IsDescending: false,
                Location: expr.Location);
        }

        // Handle descending range expressions: A downto B or A downto B by C
        if (Match(type: TokenType.Downto))
        {
            Expression end = ParseLogicalAnd();
            Expression? step = null;

            if (Match(type: TokenType.By))
            {
                step = ParseLogicalAnd();
            }

            return new RangeExpression(Start: expr,
                End: end,
                Step: step,
                IsDescending: true,
                Location: expr.Location);
        }

        return expr;
    }

    /// <summary>
    /// Parses logical AND expressions.
    /// Syntax: <c>a and b</c>
    /// Left-associative, short-circuit evaluation.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseLogicalAnd()
    {
        Expression expr = ParseEquality();

        while (Match(type: TokenType.And))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseEquality();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses equality expressions.
    /// Syntax: <c>a != b</c>
    /// Note: <c>==</c> is handled in ParseComparison for chaining support.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseEquality()
    {
        Expression expr = ParseComparison();

        while (Match(type: TokenType.NotEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseComparison();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses comparison expressions with support for chaining.
    /// Syntax: <c>a &lt; b</c>, <c>a &lt;= b &lt;= c</c> (chained), <c>a &lt;=&gt; b</c> (three-way)
    /// Supports chained comparisons only with consistent direction:
    /// - Ascending chain: &lt;, &lt;=, == (e.g., a &lt; b &lt;= c == d)
    /// - Descending chain: &gt;, &gt;=, == (e.g., a &gt; b &gt;= c == d)
    /// </summary>
    /// <returns>The parsed expression (may be <see cref="ChainedComparisonExpression"/> for chains).</returns>
    private Expression ParseComparison()
    {
        Expression expr = ParseIsExpression();

        // Check for chained comparisons
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };

        while (Match(TokenType.Less,
                   TokenType.LessEqual,
                   TokenType.Greater,
                   TokenType.GreaterEqual,
                   TokenType.Equal,
                   TokenType.ThreeWayComparison))
        {
            Token op = PeekToken(offset: -1);
            BinaryOperator binOp = TokenToBinaryOperator(tokenType: op.Type);

            // Validate chain direction consistency
            if (operators.Count > 0)
            {
                // Determine the direction of existing chain
                bool isAscending = operators.All(predicate: o =>
                    o == BinaryOperator.Less ||
                    o == BinaryOperator.LessEqual ||
                    o == BinaryOperator.Equal);
                bool isDescending = operators.All(predicate: o =>
                    o == BinaryOperator.Greater ||
                    o == BinaryOperator.GreaterEqual ||
                    o == BinaryOperator.Equal);

                // Check if new operator is compatible
                bool newIsAscending = binOp == BinaryOperator.Less ||
                                      binOp == BinaryOperator.LessEqual ||
                                      binOp == BinaryOperator.Equal;
                bool newIsDescending = binOp == BinaryOperator.Greater ||
                                       binOp == BinaryOperator.GreaterEqual ||
                                       binOp == BinaryOperator.Equal;

                if ((isAscending && !newIsAscending) || (isDescending && !newIsDescending))
                {
                    throw new ParseException(
                        message: "Invalid comparison chain: cannot mix ascending (<, <=) and descending (>, >=) operators");
                }
            }

            operators.Add(item: binOp);
            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        // If we have chained comparisons, create a ChainedComparisonExpression
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(Operands: operands, Operators: operators, Location: GetLocation());
        }
        else if (operators.Count == 1)
        {
            // Single comparison, create regular BinaryExpression
            return new BinaryExpression(Left: operands[index: 0],
                Operator: operators[index: 0],
                Right: operands[index: 1],
                Location: GetLocation());
        }

        return expr;
    }

    /// <summary>
    /// Parses type-checking and pattern matching expressions.
    /// Syntax:
    /// - <c>expr is Type</c> - type check
    /// - <c>expr is Type binding</c> - type check with variable binding
    /// - <c>expr is Type (field1, field2)</c> - destructuring pattern
    /// - <c>expr is Type (field: binding)</c> - named destructuring
    /// - <c>expr isnot Type</c> - negated type check
    /// - <c>expr follows Protocol</c> - protocol conformance check
    /// - <c>expr notfollows Protocol</c> - negated protocol check
    /// Context-sensitive: disabled inside when clause bodies to avoid ambiguity.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/isnot/follows/notfollows expressions when not in when pattern/clause context
        while (!_inWhenPatternContext && !_inWhenClauseBody && Match(TokenType.Is,
                   TokenType.IsNot,
                   TokenType.Follows,
                   TokenType.NotFollows))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            if (op.Type == TokenType.Is || op.Type == TokenType.IsNot)
            {
                bool isNegated = op.Type == TokenType.IsNot;
                TypeExpression type = ParseType();

                // Check for destructuring pattern: is Type (...)
                if (!isNegated && Check(type: TokenType.LeftParen))
                {
                    // Destructuring pattern: is Point (x, y) or is Point (x: a, y: b)
                    List<DestructuringBinding> bindings = ParseDestructuringBindings();
                    var pattern = new TypeDestructuringPattern(Type: type, Bindings: bindings, Location: location);
                    expr = new IsPatternExpression(Expression: expr,
                        Pattern: pattern,
                        IsNegated: false,
                        Location: location);
                }
                // Check for single binding: is Type identifier (only for 'is', not 'isnot')
                else if (!isNegated && Check(TokenType.Identifier, TokenType.TypeIdentifier) && !IsKeywordToken(token: CurrentToken))
                {
                    string variableName = Advance()
                       .Text;
                    var pattern = new TypePattern(Type: type, VariableName: variableName, Location: location);
                    expr = new IsPatternExpression(Expression: expr,
                        Pattern: pattern,
                        IsNegated: false,
                        Location: location);
                }
                else
                {
                    // Simple type check: is Type or isnot Type
                    var pattern = new TypePattern(Type: type, VariableName: null, Location: location);
                    expr = new IsPatternExpression(Expression: expr,
                        Pattern: pattern,
                        IsNegated: isNegated,
                        Location: location);
                }
            }
            else
            {
                // Handle follows/notfollows as binary operators
                Expression right = ParseBitwiseOr();
                expr = new BinaryExpression(Left: expr,
                    Operator: TokenToBinaryOperator(tokenType: op.Type),
                    Right: right,
                    Location: location);
            }
        }

        return expr;
    }

    /// <summary>
    /// Parses destructuring bindings for pattern matching.
    /// Syntax: (field1, field2) or (field: binding, ...) or nested ((x: x1, y: y1), ...)
    /// </summary>
    private List<DestructuringBinding> ParseDestructuringBindings()
    {
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' for destructuring pattern");

        var bindings = new List<DestructuringBinding>();

        do
        {
            SourceLocation bindingLocation = GetLocation();

            // Check for nested pattern: (...)
            if (Check(type: TokenType.LeftParen))
            {
                // Nested destructuring without type name - used for anonymous record fields
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a field name, use index-based binding
                bindings.Add(item: new DestructuringBinding(FieldName: null,
                    BindingName: "_nested",
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(FieldName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                // Named or positional binding
                string name = ConsumeIdentifier(errorMessage: "Expected field name or binding in destructuring pattern");

                if (Match(type: TokenType.Colon))
                {
                    // Named binding: field: binding
                    // Check if binding is a nested pattern
                    if (Check(type: TokenType.LeftParen))
                    {
                        List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                        bindings.Add(item: new DestructuringBinding(FieldName: name,
                            BindingName: name,
                            NestedPattern: new DestructuringPattern(Bindings: nestedBindings, Location: bindingLocation),
                            Location: bindingLocation));
                    }
                    else
                    {
                        string bindingName = ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
                        bindings.Add(item: new DestructuringBinding(FieldName: name,
                            BindingName: bindingName,
                            NestedPattern: null,
                            Location: bindingLocation));
                    }
                }
                else
                {
                    // Positional binding: name binds to field of same name
                    bindings.Add(item: new DestructuringBinding(FieldName: name,
                        BindingName: name,
                        NestedPattern: null,
                        Location: bindingLocation));
                }
            }
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after destructuring pattern");

        return bindings;
    }

    /// <summary>
    /// Checks if the current token is a keyword that should not be treated as an identifier.
    /// Used to prevent parsing keywords as binding names in pattern matching.
    /// </summary>
    private bool IsKeywordToken(Token token)
    {
        return token.Type switch
        {
            TokenType.And or TokenType.Or or TokenType.Not or TokenType.Is or TokenType.IsNot or TokenType.In or TokenType.NotIn or TokenType.Follows or TokenType.NotFollows or TokenType.If or TokenType.Else or TokenType.While or TokenType.For or TokenType.Return or TokenType.Throw or TokenType.When or TokenType.Then or TokenType.To or TokenType.Downto or TokenType.By => true,
            _ => false
        };
    }

    /// <summary>
    /// Parses bitwise OR expressions.
    /// Syntax: <c>a | b</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwiseOr()
    {
        Expression expr = ParseBitwiseXor();

        while (Match(type: TokenType.Pipe))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseXor();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bitwise XOR expressions.
    /// Syntax: <c>a ^ b</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwiseXor()
    {
        Expression expr = ParseBitwiseAnd();

        while (Match(type: TokenType.Caret))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseAnd();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bitwise AND expressions.
    /// Syntax: <c>a &amp; b</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseBitwiseAnd()
    {
        Expression expr = ParseShift();

        while (Match(type: TokenType.Ampersand))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseShift();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses bit shift expressions.
    /// Syntax: <c>a &lt;&lt; b</c>, <c>a &gt;&gt; b</c>, and checked variants.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseShift()
    {
        Expression expr = ParseAdditive();

        while (Match(TokenType.LeftShift,
                   TokenType.LeftShiftChecked,
                   TokenType.RightShift,
                   TokenType.LogicalLeftShift,
                   TokenType.LogicalRightShift))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseAdditive();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses additive expressions.
    /// Syntax: <c>a + b</c>, <c>a - b</c>, and overflow variants (+%, +^, +!, -%, -^, -!).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (Match(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusSaturate,
                   TokenType.PlusChecked,
                   TokenType.MinusWrap,
                   TokenType.MinusSaturate,
                   TokenType.MinusChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseMultiplicative();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses multiplicative expressions.
    /// Syntax: <c>a * b</c>, <c>a / b</c>, <c>a % b</c>, and overflow variants.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseMultiplicative()
    {
        Expression expr = ParsePower();

        while (Match(TokenType.Star,
                   TokenType.Slash,
                   TokenType.Percent,
                   TokenType.Divide,
                   TokenType.MultiplyWrap,
                   TokenType.MultiplySaturate,
                   TokenType.MultiplyChecked,
                   TokenType.DivideChecked,
                   TokenType.ModuloChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses power/exponentiation expressions.
    /// Syntax: <c>a ** b</c> and overflow variants (**%, **^, **!).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        while (Match(TokenType.Power,
                   TokenType.PowerWrap,
                   TokenType.PowerSaturate,
                   TokenType.PowerChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseUnary();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses unary prefix expressions.
    /// Syntax: <c>-x</c> (negation), <c>not x</c> (logical not), <c>~x</c> (bitwise not).
    /// Special handling for unary minus on numeric literals to support min values.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseUnary()
    {
        if (!Match(TokenType.Minus, TokenType.Not, TokenType.Tilde))
        {
            return ParsePostfix();
        }

        Token op = PeekToken(offset: -1);
        SourceLocation opLocation = GetLocation(token: op);

        // Special handling for unary minus on numeric literals
        // This allows parsing negative min values like -9_223_372_036_854_775_808_s64
        if (op.Type == TokenType.Minus && Check(TokenType.Integer,
                TokenType.S64Literal,
                TokenType.U64Literal,
                TokenType.S32Literal,
                TokenType.U32Literal,
                TokenType.S16Literal,
                TokenType.U16Literal,
                TokenType.S8Literal,
                TokenType.U8Literal,
                TokenType.S128Literal,
                TokenType.U128Literal,
                TokenType.SaddrLiteral,
                TokenType.UaddrLiteral,
                TokenType.Decimal,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.F128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal))
        {
            // Parse the literal
            Expression literal = ParsePostfix();

            // If it's a literal expression, apply the sign directly to the value
            if (literal is LiteralExpression { Value: not null } litExpr)
            {
                return litExpr.Value switch
                {
                    // Negate the value
                    long longVal => new LiteralExpression(Value: -longVal, LiteralType: litExpr.LiteralType, Location: opLocation),
                    double doubleVal => new LiteralExpression(Value: -doubleVal, LiteralType: litExpr.LiteralType, Location: opLocation),
                    _ => literal
                };
            }
        }

        Expression expr = ParseUnary();
        return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type), Operand: expr, Location: opLocation);
    }

    /// <summary>
    /// Parses postfix expressions: function calls, indexing, member access, and more.
    /// Syntax: <c>f(args)</c>, <c>x[index]</c>, <c>obj.member</c>, <c>obj.method!()</c>, <c>value with (field: newVal)</c>
    /// Handles generic method calls: <c>func&lt;T&gt;()</c>, <c>obj.method&lt;T&gt;()</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            // Handle standalone generic function calls like routine!<T>(args) or routine<T>(args)
            // The ! must come BEFORE < if present: func!<T>() not func<T>!()
            if (expr is IdentifierExpression beforeExpr && (Check(type: TokenType.Less) || Check(type: TokenType.Bang) && PeekToken(offset: 1)
                   .Type == TokenType.Less))
            {
                // Check for failable marker ! before generic parameters: func!<T>
                bool isMemoryOperation = Match(type: TokenType.Bang);

                // Now we should be at '<'
                if (!Check(type: TokenType.Less))
                {
                    break;
                }

                // Lookahead to check if this is likely a generic or a comparison
                int savedPos = Position;
                Advance(); // consume '<'

                bool isLikelyGeneric = false;
                int scanPos = Position;
                int depth = 1;

                while (scanPos < Tokens.Count && depth > 0)
                {
                    TokenType tt = Tokens[index: scanPos].Type;
                    if (tt == TokenType.Less)
                    {
                        depth++;
                    }
                    else if (tt == TokenType.Greater)
                    {
                        depth--;
                        if (depth == 0)
                        {
                            if (scanPos + 1 < Tokens.Count && (Tokens[index: scanPos + 1].Type == TokenType.LeftParen || Tokens[index: scanPos + 1].Type == TokenType.Dot))
                            {
                                isLikelyGeneric = true;
                            }

                            break;
                        }
                    }
                    else if (tt == TokenType.RightShift)
                    {
                        depth -= 2;
                        if (depth == 0)
                        {
                            if (scanPos + 1 < Tokens.Count && (Tokens[index: scanPos + 1].Type == TokenType.LeftParen || Tokens[index: scanPos + 1].Type == TokenType.Dot))
                            {
                                isLikelyGeneric = true;
                            }

                            break;
                        }
                        else if (depth < 0)
                        {
                            break;
                        }
                    }
                    else if (tt == TokenType.Newline || tt == TokenType.Colon)
                    {
                        break;
                    }

                    scanPos++;
                }

                Position = savedPos;

                if (!isLikelyGeneric)
                {
                    break;
                }

                Advance(); // consume '<' again
                var typeArgs = new List<TypeExpression>();
                do
                {
                    typeArgs.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic type arguments");

                if (Match(type: TokenType.LeftParen))
                {
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    string methodName = ((IdentifierExpression)expr).Name;
                    if (isMemoryOperation)
                    {
                        methodName += "!";
                    }

                    expr = new GenericMethodCallExpression(Object: expr,
                        MethodName: methodName,
                        TypeArguments: typeArgs,
                        Arguments: args,
                        IsMemoryOperation: isMemoryOperation,
                        Location: expr.Location);
                }
                else
                {
                    expr = new GenericMemberExpression(Object: expr,
                        MemberName: ((IdentifierExpression)expr).Name,
                        TypeArguments: typeArgs,
                        Location: expr.Location);
                }
            }
            // Throwable function call: identifier!(args) with named arguments
            else if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                        .Type == TokenType.LeftParen)
            {
                Advance(); // consume '!'
                Advance(); // consume '('

                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                if (expr is IdentifierExpression identExpr)
                {
                    expr = new CallExpression(Callee: new IdentifierExpression(Name: identExpr.Name + "!", Location: identExpr.Location), Arguments: args, Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.LeftParen))
            {
                // Function call - supports named arguments (name: value)
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
            }
            else if (Match(type: TokenType.LeftBracket))
            {
                // Array/map indexing
                Expression index = ParseExpression();
                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index");
                expr = new IndexExpression(Object: expr, Index: index, Location: expr.Location);
            }
            else if (Match(type: TokenType.Dot))
            {
                // Member access - allow failable methods with ! suffix
                string member = ConsumeMethodName(errorMessage: "Expected member name after '.'");

                // Check for failable marker ! before generic parameters: obj.method!<T>
                bool isGenericMemOp = false;
                if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                       .Type == TokenType.Less)
                {
                    isGenericMemOp = true;
                    Match(type: TokenType.Bang);
                }

                // Check for generic method call with type parameters
                if (Check(type: TokenType.Less))
                {
                    int savedPos = Position;
                    Advance(); // consume '<'

                    bool isLikelyGeneric = false;

                    if (Check(type: TokenType.TypeIdentifier) || Check(type: TokenType.Identifier))
                    {
                        string identText = CurrentToken.Text;
                        if (IsKnownTypeName(name: identText) || Check(type: TokenType.TypeIdentifier))
                        {
                            int scanPos = Position;
                            int depth = 1;
                            while (scanPos < Tokens.Count && depth > 0)
                            {
                                TokenType tt = Tokens[index: scanPos].Type;
                                if (tt == TokenType.Less)
                                {
                                    depth++;
                                }
                                else if (tt == TokenType.Greater)
                                {
                                    depth--;
                                    if (depth == 0)
                                    {
                                        if (scanPos + 1 < Tokens.Count && (Tokens[index: scanPos + 1].Type == TokenType.LeftParen || Tokens[index: scanPos + 1].Type == TokenType.Dot))
                                        {
                                            isLikelyGeneric = true;
                                        }

                                        break;
                                    }
                                }
                                else if (tt == TokenType.Newline || tt == TokenType.Colon)
                                {
                                    break;
                                }

                                scanPos++;
                            }
                        }
                    }

                    Position = savedPos;

                    if (isLikelyGeneric)
                    {
                        Advance(); // consume '<' again
                        var typeArgs = new List<TypeExpression>();
                        do
                        {
                            typeArgs.Add(item: ParseType());
                        } while (Match(type: TokenType.Comma));

                        ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic type arguments");

                        if (Match(type: TokenType.LeftParen))
                        {
                            List<Expression> genericArgs = ParseArgumentList();
                            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                            string methodName = member;
                            if (isGenericMemOp)
                            {
                                methodName += "!";
                            }

                            expr = new GenericMethodCallExpression(Object: expr,
                                MethodName: methodName,
                                TypeArguments: typeArgs,
                                Arguments: genericArgs,
                                IsMemoryOperation: isGenericMemOp,
                                Location: expr.Location);
                        }
                        else
                        {
                            expr = new GenericMemberExpression(Object: expr,
                                MemberName: member,
                                TypeArguments: typeArgs,
                                Location: expr.Location);
                        }

                        continue;
                    }
                }

                // Regular member access
                // Check for failable method call with ! suffix
                if (Match(type: TokenType.Bang) && Match(type: TokenType.LeftParen))
                {
                    // Failable method call: obj.method!(args)
                    // Represented as CallExpression with MemberExpression callee
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr, PropertyName: member + "!", Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr, Arguments: args, Location: expr.Location);
                }
                else if (Match(type: TokenType.LeftParen))
                {
                    // Regular method call: obj.method(args)
                    // Represented as CallExpression with MemberExpression callee
                    List<Expression> args = ParseArgumentList();
                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                    Expression memberExpr = new MemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
                    expr = new CallExpression(Callee: memberExpr, Arguments: args, Location: expr.Location);
                }
                else
                {
                    expr = new MemberExpression(Object: expr, PropertyName: member, Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.With))
            {
                // Functional update expression for records: record with (field: newVal)
                // Only valid on record types - semantic analyzer will validate this
                SourceLocation withLocation = GetLocation(token: PeekToken(offset: -1));
                Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'with'");

                var updates = new List<(string FieldName, Expression Value)>();

                // Parse update list: (field: value, field2: value2, ...)
                do
                {
                    Token fieldToken = Consume(type: TokenType.Identifier, errorMessage: "Expected field name in with expression");
                    Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name in with expression");
                    Expression value = ParseExpression();
                    updates.Add(item: (fieldToken.Text, value));
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after with updates");

                expr = new WithExpression(Base: expr, Updates: updates, Location: withLocation);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    /// <summary>
    /// Parses primary expressions (literals, identifiers, parenthesized, intrinsics).
    /// This is the highest precedence level in the expression parser.
    /// Handles: true, false, none, numbers, strings, characters, identifiers,
    /// parenthesized expressions, inline conditionals, @intrinsic, @native.
    /// </summary>
    /// <returns>A primary expression AST node.</returns>
    private Expression ParsePrimary()
    {
        SourceLocation location = GetLocation();

        // Boolean and none literals
        if (Match(type: TokenType.True))
        {
            return new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        }

        if (Match(type: TokenType.False))
        {
            return new LiteralExpression(Value: false, LiteralType: TokenType.False, Location: location);
        }

        if (Match(type: TokenType.None))
        {
            return new LiteralExpression(Value: null!, LiteralType: TokenType.None, Location: location);
        }

        // Numeric literals (integers and floats)
        if (TryParseNumericLiteral(location: location, result: out Expression? numericExpr))
        {
            return numericExpr!;
        }

        // Text literals
        if (TryParseTextLiteral(location: location, result: out Expression? textExpr))
        {
            return textExpr!;
        }

        // Letter literals
        if (TryParseLetterLiteral(location: location, result: out Expression? letterExpr))
        {
            return letterExpr!;
        }

        // Memory size literals
        if (TryParseMemoryLiteral(location: location, result: out Expression? memoryExpr))
        {
            return memoryExpr!;
        }

        // Duration/time literals
        if (TryParseDurationLiteral(location: location, result: out Expression? durationExpr))
        {
            return durationExpr!;
        }

        // Arrow lambda expression: x => expr (single parameter, no parens)
        if (!_inWhenPatternContext && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.FatArrow)
        {
            return ParseArrowLambdaExpression(location: location);
        }

        // Identifiers and Suflae-specific keywords
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string text = PeekToken(offset: -1)
               .Text;
            if (text == "me")
            {
                return new IdentifierExpression(Name: "this", Location: location);
            }

            return new IdentifierExpression(Name: text, Location: location);
        }

        // Parenthesized expression or arrow lambda with parenthesized params
        if (Match(type: TokenType.LeftParen))
        {
            if (IsArrowLambdaParameters())
            {
                return ParseParenthesizedArrowLambda(location: location);
            }

            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
        }

        // Intrinsic function call: @intrinsic.operation<T>(args)
        if (Match(type: TokenType.Intrinsic))
        {
            return ParseIntrinsicCall(location: location);
        }

        // Native function call: @native.function_name(args)
        if (Match(type: TokenType.Native))
        {
            return ParseNativeCall(location: location);
        }

        // List literal: [expr, expr, ...]
        if (Match(type: TokenType.LeftBracket))
        {
            return ParseListLiteral(location: location);
        }

        // Set or Dict literal: {expr, expr, ...} or {key: value, ...}
        if (Match(type: TokenType.LeftBrace))
        {
            return ParseSetOrDictLiteral(location: location);
        }

        throw new ParseException(message: $"Unexpected token: {CurrentToken.Type}");
    }

    /// <summary>
    /// Tries to parse an integer or float literal.
    /// </summary>
    private bool TryParseNumericLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        // Integer literals
        if (Match(TokenType.Integer,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.SaddrLiteral,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.UaddrLiteral))
        {
            Token token = PeekToken(offset: -1);
            string cleanValue = token.Text
                                     .Replace(oldValue: "s32", newValue: "")
                                     .Replace(oldValue: "s64", newValue: "")
                                     .Replace(oldValue: "s128", newValue: "")
                                     .Replace(oldValue: "saddr", newValue: "")
                                     .Replace(oldValue: "u32", newValue: "")
                                     .Replace(oldValue: "u64", newValue: "")
                                     .Replace(oldValue: "u128", newValue: "")
                                     .Replace(oldValue: "uaddr", newValue: "")
                                     .Replace(oldValue: "_", newValue: "");

            result = ParseIntegerValue(cleanValue: cleanValue, token: token, location: location);
            return true;
        }

        // Float literals
        if (Match(TokenType.Decimal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.D128Literal))
        {
            Token token = PeekToken(offset: -1);
            string cleanValue = token.Text
                                     .Replace(oldValue: "f32", newValue: "")
                                     .Replace(oldValue: "f64", newValue: "")
                                     .Replace(oldValue: "d128", newValue: "")
                                     .Replace(oldValue: "_", newValue: "");

            if (double.TryParse(s: cleanValue, result: out double floatVal))
            {
                result = new LiteralExpression(Value: floatVal, LiteralType: token.Type, Location: location);
                return true;
            }

            throw new ParseException(message: $"Invalid float literal: {token.Text}");
        }

        return false;
    }

    /// <summary>
    /// Parses an integer value from a cleaned string, handling hex, binary, and decimal formats.
    /// </summary>
    /// <param name="cleanValue">The cleaned integer literal without type suffix.</param>
    /// <param name="token">The original token for error reporting.</param>
    /// <param name="location">Source location of the literal.</param>
    /// <returns>A <see cref="LiteralExpression"/> representing the integer value.</returns>
    private static LiteralExpression ParseIntegerValue(string cleanValue, Token token, SourceLocation location)
    {
        // Handle hexadecimal literals (0x prefix)
        if (cleanValue.StartsWith(value: "0x") || cleanValue.StartsWith(value: "0X"))
        {
            string hexPart = cleanValue.Substring(startIndex: 2);
            if (long.TryParse(s: hexPart,
                    style: System.Globalization.NumberStyles.HexNumber,
                    provider: null,
                    result: out long hexVal))
            {
                return new LiteralExpression(Value: hexVal, LiteralType: token.Type, Location: location);
            }
        }
        // Handle binary literals (0b prefix)
        else if (cleanValue.StartsWith(value: "0b") || cleanValue.StartsWith(value: "0B"))
        {
            string binaryPart = cleanValue.Substring(startIndex: 2);
            try
            {
                long binVal = Convert.ToInt64(value: binaryPart, fromBase: 2);
                return new LiteralExpression(Value: binVal, LiteralType: token.Type, Location: location);
            }
            catch (Exception)
            {
                throw new ParseException(message: $"Invalid binary literal: {token.Text}");
            }
        }
        // Handle decimal literals
        else if (long.TryParse(s: cleanValue, result: out long intVal))
        {
            return new LiteralExpression(Value: intVal, LiteralType: token.Type, Location: location);
        }
        // Handle large unsigned values
        else if (ulong.TryParse(s: cleanValue, result: out ulong ulongVal))
        {
            return new LiteralExpression(Value: unchecked((long)ulongVal), LiteralType: token.Type, Location: location);
        }

        throw new ParseException(message: $"Invalid integer literal: {token.Text}");
    }

    /// <summary>
    /// Tries to parse a text literal (text, formatted text, raw text, bytes).
    /// </summary>
    private bool TryParseTextLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.TextLiteral,
                TokenType.FormattedText,
                TokenType.RawText,
                TokenType.RawFormattedText,
                TokenType.BytesLiteral,
                TokenType.BytesFormatted,
                TokenType.BytesRawLiteral,
                TokenType.BytesRawFormatted))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string value = token.Text;

        // Formatted text literals (f"...")
        if (value.StartsWith(value: "f\"") && value.EndsWith(value: "\""))
        {
            value = value.Substring(startIndex: 2, length: value.Length - 3);
            result = new LiteralExpression(Value: value, LiteralType: TokenType.FormattedText, Location: location);
            return true;
        }

        // Regular text literals
        if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
        {
            value = value.Substring(startIndex: 1, length: value.Length - 2);
        }
        else if (value.StartsWith(value: "b\""))
        {
            int prefixEnd = value.IndexOf(value: '"');
            if (prefixEnd > 0)
            {
                value = value.Substring(startIndex: prefixEnd + 1, length: value.Length - prefixEnd - 2);
            }
        }

        result = new LiteralExpression(Value: value, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a letter literal (letter8, letter16, letter, or byte letter).
    /// </summary>
    private bool TryParseLetterLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.Letter8Literal, TokenType.Letter16Literal, TokenType.LetterLiteral, TokenType.ByteLetterLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        char parsedLetter = ParseLetterContent(value: token.Text);
        result = new LiteralExpression(Value: parsedLetter, LiteralType: token.Type, Location: location);
        return true;
    }

    /// <summary>
    /// Tries to parse a memory size literal (bytes, kilobytes, etc.).
    /// </summary>
    private bool TryParseMemoryLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.ByteLiteral,
                TokenType.KilobyteLiteral,
                TokenType.KibibyteLiteral,
                TokenType.KilobitLiteral,
                TokenType.KibibitLiteral,
                TokenType.MegabyteLiteral,
                TokenType.MebibyteLiteral,
                TokenType.MegabitLiteral,
                TokenType.MebibitLiteral,
                TokenType.GigabyteLiteral,
                TokenType.GibibyteLiteral,
                TokenType.GigabitLiteral,
                TokenType.GibibitLiteral,
                TokenType.TerabyteLiteral,
                TokenType.TebibyteLiteral,
                TokenType.TerabitLiteral,
                TokenType.TebibitLiteral,
                TokenType.PetabyteLiteral,
                TokenType.PebibyteLiteral,
                TokenType.PetabitLiteral,
                TokenType.PebibitLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string numericPart = new(value: token.Text
                                             .TakeWhile(predicate: char.IsDigit)
                                             .ToArray());

        if (long.TryParse(s: numericPart, result: out long memoryVal))
        {
            result = new LiteralExpression(Value: memoryVal, LiteralType: token.Type, Location: location);
            return true;
        }

        throw new ParseException(message: $"Invalid memory literal: {token.Text}");
    }

    /// <summary>
    /// Tries to parse a duration/time literal (hours, minutes, seconds, etc.).
    /// </summary>
    private bool TryParseDurationLiteral(SourceLocation location, out Expression? result)
    {
        result = null;

        if (!Match(TokenType.WeekLiteral,
                TokenType.DayLiteral,
                TokenType.HourLiteral,
                TokenType.MinuteLiteral,
                TokenType.SecondLiteral,
                TokenType.MillisecondLiteral,
                TokenType.MicrosecondLiteral,
                TokenType.NanosecondLiteral))
        {
            return false;
        }

        Token token = PeekToken(offset: -1);
        string numericPart = new(value: token.Text
                                             .TakeWhile(predicate: char.IsDigit)
                                             .ToArray());

        if (long.TryParse(s: numericPart, result: out long timeVal))
        {
            result = new LiteralExpression(Value: timeVal, LiteralType: token.Type, Location: location);
            return true;
        }

        throw new ParseException(message: $"Invalid duration literal: {token.Text}");
    }

    /// <summary>
    /// Parses the content of a letter literal, handling escape sequences.
    /// </summary>
    private static char ParseLetterContent(string value)
    {
        int quoteStart = value.LastIndexOf(value: '\'');
        int quoteEnd = value.Length - 1;

        if (quoteStart >= 0 && quoteEnd > quoteStart && value[index: quoteEnd] == '\'')
        {
            string charContent = value.Substring(startIndex: quoteStart + 1, length: quoteEnd - quoteStart - 1);

            return charContent switch
            {
                "\\'" => '\'',
                "\\\\" => '\\',
                "\\n" => '\n',
                "\\t" => '\t',
                "\\r" => '\r',
                _ when charContent.Length == 1 => charContent[index: 0],
                _ => '?'
            };
        }

        return '?';
    }
}
