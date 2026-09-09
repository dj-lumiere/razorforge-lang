using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing operator and precedence-chain parsing.
/// </summary>
public partial class Parser
{
    private Expression ParseAssignment()
    {
        Expression expr = ParseWith();
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

        // Base operators with in-place wired support -> CompoundAssignmentExpression
        if (compoundOp.Value.GetInPlaceMemberRoutineName() != null)
        {
            return new CompoundAssignmentExpression(Target: expr,
                Operator: compoundOp.Value,
                Value: value,
                Location: expr.Location);
        }

        // Overflow variants and ??= -> expand to a = a op b
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
            TokenType.PlusClampAssign => BinaryOperator.AddClamp,
            TokenType.MinusClampAssign => BinaryOperator.SubtractClamp,
            TokenType.MultiplyClampAssign => BinaryOperator.MultiplyClamp,
            TokenType.SlashClampAssign => BinaryOperator.TrueDivClamp,
            TokenType.PowerClampAssign => BinaryOperator.PowerClamp,
            _ => null
        };

        if (op.HasValue)
        {
            Advance(); // Consume the compound assignment token
        }

        return op;
    }

    /// <summary>
    /// Parses with expressions (lowest precedence operator).
    /// Syntax: <c>expr with .memberVar = value, .nested.memberVar = value, [index] = value</c>
    /// </summary>
    /// <returns>The parsed expression, possibly a with expression.</returns>
    private Expression ParseWith()
    {
        Expression expr = ParseInlineConditional();

        if (Match(type: TokenType.With))
        {
            SourceLocation withLocation = GetLocation(token: PeekToken(offset: -1));
            var updates =
                new List<(List<string>? MemberVariablePath, Expression? Index, Expression Value
                    )>();

            do
            {
                List<string>? fieldPath = null;
                Expression? indexExpr = null;

                if (Match(type: TokenType.LeftBracket))
                {
                    // Index update: [expr] = value
                    indexExpr = ParseExpression();
                    Consume(type: TokenType.RightBracket,
                        errorMessage: "Expected ']' after index in with expression");
                }
                else if (Match(type: TokenType.Dot))
                {
                    // Member variable update: .memberVar or .memberVar.nested
                    fieldPath = [];
                    Token memberVariableToken = Consume(type: TokenType.Identifier,
                        errorMessage:
                        "Expected member variable name after '.' in with expression");
                    fieldPath.Add(item: memberVariableToken.Text);

                    // Parse nested member variable path: .address.city
                    while (Check(type: TokenType.Dot) && PeekToken(offset: 1)
                              .Type == TokenType.Identifier)
                    {
                        Advance(); // consume dot
                        Token nestedToken = Consume(type: TokenType.Identifier,
                            errorMessage: "Expected member variable name in with expression");
                        fieldPath.Add(item: nestedToken.Text);
                    }
                }
                else
                {
                    throw new GrammarException(code: GrammarDiagnosticCode.UnexpectedToken,
                        message: "Expected '.' or '[' in with expression",
                        fileName: FileName,
                        line: CurrentToken.Line,
                        column: CurrentToken.Column,
                        language: _language);
                }

                Consume(type: TokenType.Assign,
                    errorMessage:
                    "Expected '=' after member variable or index in with expression");
                Expression value = ParseInlineConditional();
                updates.Add(item: (fieldPath, indexExpr, value));
            } while (Match(type: TokenType.Comma));

            expr = new WithExpression(Base: expr, Updates: updates, Location: withLocation);
        }

        return expr;
    }

    /// <summary>
    /// Parses inline conditional expressions.
    /// Syntax: <c>if condition then thenExpr else elseExpr</c>
    /// Note: This is expression-level only; block-level if uses ParseIfStatement.
    /// Nested inline conditionals are forbidden for readability.
    /// </summary>
    /// <returns>The parsed expression, possibly a conditional.</returns>
    private Expression ParseInlineConditional()
    {
        // Check for inline if-then-else expression
        // Skip if already inside an inline conditional (prevents nesting for readability)
        if (_parsingInlineConditional || !Match(type: TokenType.If))
        {
            return ParseNoneCoalesce();
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Set flag to prevent nested inline conditionals
        _parsingInlineConditional = true;

        try
        {
            Expression condition = ParseNoneCoalesce();

            Consume(type: TokenType.Then,
                errorMessage: "Expected 'then' after condition in inline if");
            Expression
                thenExpr =
                    ParseExpression(); // Full expression, but flag prevents nested inline if

            Consume(type: TokenType.Else, errorMessage: "Expected 'else' in inline if expression");
            Expression
                elseExpr =
                    ParseExpression(); // Full expression, but flag prevents nested inline if

            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: location);
        }
        finally
        {
            _parsingInlineConditional = false;
        }
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

        while (Match(TokenType.Or, TokenType.But))
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

        // Handle exclusive range expressions: A til B or A til B by C
        if (Match(type: TokenType.Til))
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
                Location: expr.Location,
                IsExclusive: true);
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

        // `!=` value inequality, plus the non-chainable reference-identity operators `===` / `!==`.
        while (Match(TokenType.NotEqual, TokenType.IdentityEqual, TokenType.IdentityNotEqual))
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

            operators.Add(item: binOp);
            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        // If we have chained comparisons, create a ChainedComparisonExpression
        // Note: Chained comparisons are NOT desugared because they need special
        // handling to evaluate middle operands only once (a < b < c)
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(Operands: operands,
                Operators: operators,
                Location: GetLocation());
        }

        if (operators.Count == 1)
        {
            // Single comparison
            return new BinaryExpression(Left: operands[index: 0],
                Operator: operators[index: 0],
                Right: operands[index: 1],
                Location: GetLocation());
        }

        return expr;
    }

    /// <summary>
    /// Parses type-checking, membership, and pattern matching expressions.
    /// Syntax:
    /// - <c>expr is Type</c> - type check
    /// - <c>expr is Type binding</c> - type check with variable binding
    /// - <c>expr is Type (field1, field2)</c> - destructuring pattern
    /// - <c>expr is Type (memberVar: binding)</c> - named destructuring
    /// - <c>expr isnot Type</c> - negated type check
    /// - <c>expr in collection</c> - membership test (desugars to collection.contains(expr))
    /// - <c>expr notin collection</c> - negated membership test
    /// - <c>expr obeys Protocol</c> - protocol conformance check
    /// - <c>expr disobeys Protocol</c> - negated protocol check
    /// Context-sensitive: disabled inside when clause bodies to avoid ambiguity.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/isnot/in/notin/obeys/disobeys expressions when not in when pattern/clause context
        while (!_inWhenPatternContext && !_inWhenClauseBody && Match(TokenType.Is,
                   TokenType.IsNot,
                   TokenType.In,
                   TokenType.NotIn,
                   TokenType.Obeys,
                   TokenType.Disobeys))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            if (op.Type is TokenType.Is or TokenType.IsNot)
            {
                expr = ParseIsOrIsNotPattern(expr: expr, op: op, location: location);
            }
            else
            {
                // Handle obeys/disobeys as binary operators
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
    /// Parses the type/pattern following an <c>is</c> / <c>isnot</c> operator (the leading operator token
    /// has already been consumed). Handles <c>None</c>, qualified choice cases, flags-test chains, and
    /// the destructuring/single-binding/simple-type-check pattern forms.
    /// </summary>
    private Expression ParseIsOrIsNotPattern(Expression expr, Token op, SourceLocation location)
    {
        bool isNegated = op.Type == TokenType.IsNot;

        TypeExpression type = ParseIsPatternType(location: location);

        // `None` carries no payload, so it binds nothing: reject a binding (`is None x`) or a
        // destructuring (`is None (x, y)`) after it.
        if (type.Name == "None" &&
            ((Check(type: TokenType.Identifier) && !IsKeywordToken(token: CurrentToken)) ||
             Check(type: TokenType.LeftParen)))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidPattern,
                message:
                "The 'None' pattern binds no value — remove the binding or destructuring after 'None'.");
        }

        // Check if this is a flags test chain: identifier followed by and/or/but
        if (Check(type: TokenType.And) || Check(type: TokenType.Or) ||
            Check(type: TokenType.But))
        {
            string firstFlag = type.Name;
            return ParseFlagsTestChain(subject: expr,
                firstFlag: firstFlag,
                isNegated: isNegated,
                location: location);
        }

        return BuildIsPatternExpression(expr: expr, type: type, isNegated: isNegated,
            location: location);
    }

    /// <summary>
    /// Parses the type after an <c>is</c>/<c>isnot</c>: either the <c>None</c> keyword or a type with an
    /// optional dotted qualifier (`is Color.RED`).
    /// </summary>
    private TypeExpression ParseIsPatternType(SourceLocation location)
    {
        // Handle 'is None' or 'isnot None' as a special case - None is a keyword
        if (Match(type: TokenType.None))
        {
            return new TypeExpression(Name: "None",
                GenericArguments: null,
                Location: location);
        }

        TypeExpression type = ParseType();

        // Accept qualified choice/variant cases: `is Color.RED`, `isnot HttpStatus.OK`.
        // Mirrors ParseTypePattern's dotted-name handling so f-string holes like
        // `f"{c is Color.RED}"` parse — without this, `.RED` leaks out of the hole
        // and the f-string parser sees a stray Dot before the closing brace.
        while (Match(type: TokenType.Dot))
        {
            string member = ConsumeIdentifier(
                errorMessage: "Expected identifier after '.' in 'is' pattern");
            type = type with { Name = type.Name + "." + member };
        }

        return type;
    }

    /// <summary>
    /// Builds the IsPatternExpression for the destructuring / single-binding / simple-type-check forms
    /// after the type is parsed and flags/None special cases are ruled out.
    /// </summary>
    private IsPatternExpression BuildIsPatternExpression(Expression expr, TypeExpression type, bool isNegated,
        SourceLocation location)
    {
        switch (isNegated)
        {
            // Check for destructuring pattern: is Type (...)
            case false when Check(type: TokenType.LeftParen):
            {
                // Destructuring pattern: is Point (x, y) or is Point (x: a, y: b)
                List<DestructuringBinding> bindings = ParseDestructuringBindings();
                var pattern = new TypeDestructuringPattern(Type: type,
                    Bindings: bindings,
                    Location: location);
                return new IsPatternExpression(Expression: expr,
                    Pattern: pattern,
                    IsNegated: false,
                    Location: location);
            }
            // Check for single binding: is Type identifier (only for 'is', not 'isnot')
            case false when Check(type: TokenType.Identifier) &&
                            !IsKeywordToken(token: CurrentToken):
            {
                string variableName = Advance()
                   .Text;
                var pattern = new TypePattern(Type: type,
                    VariableName: variableName,
                    Bindings: null,
                    Location: location);
                return new IsPatternExpression(Expression: expr,
                    Pattern: pattern,
                    IsNegated: false,
                    Location: location);
            }
            default:
            {
                // Simple type check: is Type or isnot Type
                var pattern = new TypePattern(Type: type,
                    VariableName: null,
                    Bindings: null,
                    Location: location);
                return new IsPatternExpression(Expression: expr,
                    Pattern: pattern,
                    IsNegated: isNegated,
                    Location: location);
            }
        }
    }
}
