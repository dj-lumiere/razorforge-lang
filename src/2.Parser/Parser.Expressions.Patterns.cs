using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing pattern and flags-test expression parsing helpers.
/// </summary>
public partial class Parser
{
    private const string ExpectedFlagNameAfterAnd = "Expected flag name after 'and'";

    private FlagsTestExpression ParseFlagsTestChain(Expression subject, string firstFlag,
        bool isNegated, SourceLocation location)
    {
        var flags = new List<string> { firstFlag };
        List<string>? excluded = null;
        FlagsTestKind kind = isNegated
            ? FlagsTestKind.IsNot
            : FlagsTestKind.Is;

        if (Match(type: TokenType.And))
        {
            // 'and' chain: READ and WRITE and ...
            flags.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
            while (Match(type: TokenType.And))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
            }

            // Optional 'but' exclusion
            if (Match(type: TokenType.But))
            {
                excluded = ParseButExclusionList();
            }

            return new FlagsTestExpression(Subject: subject,
                Kind: kind,
                TestFlags: flags,
                Connective: FlagsTestConnective.And,
                ExcludedFlags: excluded,
                Location: location);
        }

        if (Match(type: TokenType.Or))
        {
            flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            while (Match(type: TokenType.Or))
            {
                flags.Add(item: ConsumeIdentifier(errorMessage: "Expected flag name after 'or'"));
            }

            return new FlagsTestExpression(Subject: subject,
                Kind: kind,
                TestFlags: flags,
                Connective: FlagsTestConnective.Or,
                ExcludedFlags: null,
                Location: location);
        }

        if (Match(type: TokenType.But))
        {
            // Single flag with but exclusion: is READ but WRITE
            excluded = ParseButExclusionList();

            return new FlagsTestExpression(Subject: subject,
                Kind: kind,
                TestFlags: flags,
                Connective: FlagsTestConnective.And,
                ExcludedFlags: excluded,
                Location: location);
        }

        // Should not reach here (caller checked for and/or/but)
        throw ThrowParseError(message: "Expected 'and', 'or', or 'but' in flags test");
    }

    /// <summary>
    /// Parses the excluded-flag list following a <c>but</c> keyword (already consumed):
    /// the first flag name, then any <c>and</c>-chained additional flag names.
    /// </summary>
    private List<string> ParseButExclusionList()
    {
        var excluded = new List<string>
        {
            ConsumeIdentifier(errorMessage: "Expected flag name after 'but'")
        };
        while (Match(type: TokenType.And))
        {
            excluded.Add(
                item: ConsumeIdentifier(errorMessage: ExpectedFlagNameAfterAnd));
        }

        return excluded;
    }

    /// <summary>
    /// Parses destructuring bindings for pattern matching.
    /// Syntax: (memberVar1, memberVar2) or (memberVar: binding, ...) or nested ((x: x1, y: y1), ...)
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
                // Nested destructuring without type name - used for anonymous record member variables
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a member variable name, use index-based binding
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_nested",
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                bindings.Add(item: ParseNamedOrPositionalBinding(bindingLocation: bindingLocation));
            }
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after destructuring pattern");

        return bindings;
    }

    /// <summary>
    /// Parses a single named or positional destructuring binding: <c>name</c>, <c>memberVar: binding</c>,
    /// or <c>memberVar: (nested)</c>. Shared by <see cref="ParseDestructuringBindings"/> and
    /// <see cref="ParseDestructuringBindingList"/>.
    /// </summary>
    private DestructuringBinding ParseNamedOrPositionalBinding(SourceLocation bindingLocation)
    {
        // Named or positional binding
        string name = ConsumeIdentifier(
            errorMessage:
            "Expected member variable name or binding in destructuring pattern");

        if (Match(type: TokenType.Colon))
        {
            // Named binding: memberVar: binding
            // Check if binding is a nested pattern
            if (Check(type: TokenType.LeftParen))
            {
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                return new DestructuringBinding(MemberVariableName: name,
                    BindingName: name,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation);
            }

            string bindingName =
                ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: bindingName,
                NestedPattern: null,
                Location: bindingLocation);
        }

        // Positional binding: name binds to member variable of same name
        return new DestructuringBinding(MemberVariableName: name,
            BindingName: name,
            NestedPattern: null,
            Location: bindingLocation);
    }

    /// <summary>
    /// Parses a list of destructuring bindings (without consuming the surrounding parentheses).
    /// Used by ParseTypePattern() for type patterns with destructuring like: is CIRCLE ((x, y), radius)
    /// </summary>
    private List<DestructuringBinding> ParseDestructuringBindingList()
    {
        var bindings = new List<DestructuringBinding>();

        if (Check(type: TokenType.RightParen))
        {
            return bindings; // Empty list
        }

        do
        {
            SourceLocation bindingLocation = GetLocation();

            // Check for nested pattern: (...)
            if (Check(type: TokenType.LeftParen))
            {
                // Nested destructuring - recursively parse with parens
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                // For nested patterns without a member variable name, use null MemberVariableName
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: null,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation));
            }
            // Check for wildcard: _
            else if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
            {
                Advance();
                bindings.Add(item: new DestructuringBinding(MemberVariableName: null,
                    BindingName: "_",
                    NestedPattern: null,
                    Location: bindingLocation));
            }
            else
            {
                bindings.Add(
                    item: ParseNamedOrPositionalBindingForList(bindingLocation: bindingLocation));
            }
        } while (Match(type: TokenType.Comma));

        return bindings;
    }

    /// <summary>
    /// Parses a single named or positional binding for <see cref="ParseDestructuringBindingList"/>:
    /// <c>name</c>, <c>memberVar: binding</c>, or <c>memberVar: (nested)</c>. Differs from
    /// <see cref="ParseNamedOrPositionalBinding"/> only in that a named nested pattern uses a null
    /// BindingName.
    /// </summary>
    private DestructuringBinding ParseNamedOrPositionalBindingForList(SourceLocation bindingLocation)
    {
        // Named or positional binding
        string name = ConsumeIdentifier(
            errorMessage:
            "Expected member variable name or binding in destructuring pattern");

        if (Match(type: TokenType.Colon))
        {
            // Named binding: memberVar: binding or memberVar: (nested)
            if (Check(type: TokenType.LeftParen))
            {
                List<DestructuringBinding> nestedBindings = ParseDestructuringBindings();
                return new DestructuringBinding(MemberVariableName: name,
                    BindingName: null,
                    NestedPattern: new DestructuringPattern(Bindings: nestedBindings,
                        Location: bindingLocation),
                    Location: bindingLocation);
            }

            string bindingName =
                ConsumeIdentifier(errorMessage: "Expected binding name after ':'");
            return new DestructuringBinding(MemberVariableName: name,
                BindingName: bindingName,
                NestedPattern: null,
                Location: bindingLocation);
        }

        // Positional binding: name binds to member variable of same name
        return new DestructuringBinding(MemberVariableName: name,
            BindingName: name,
            NestedPattern: null,
            Location: bindingLocation);
    }

    /// <summary>
    /// Checks if the current token is a keyword that should not be treated as an identifier.
    /// Used to prevent parsing keywords as binding names in pattern matching.
    /// </summary>
    private static bool IsKeywordToken(Token token)
    {
        return token.Type switch
        {
            TokenType.And or TokenType.Or or TokenType.Not or TokenType.Is or TokenType.IsNot
                or TokenType.In or TokenType.NotIn or TokenType.Obeys or TokenType.Disobeys
                or TokenType.If or TokenType.Else or TokenType.While or TokenType.Each
                or TokenType.Return or TokenType.Throw or TokenType.Pierce or TokenType.When or TokenType.Then
                or TokenType.To or TokenType.Til or TokenType.By => true,
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
    /// Syntax: <c>a &lt;&lt; b</c>, <c>a &gt;&gt; b</c>.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseShift()
    {
        Expression expr = ParseAdditive();

        while (Match(TokenType.LeftShift,
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
    /// Syntax: <c>a + b</c>, <c>a - b</c>, and overflow variants (+%, +^, -%, -^).
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (Match(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusClamp,
                   TokenType.MinusWrap,
                   TokenType.MinusClamp,
                   TokenType.PlusUnchecked,
                   TokenType.MinusUnchecked))
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
                   TokenType.MultiplyClamp,
                   TokenType.SlashClamp,
                   TokenType.MultiplyUnchecked,
                   TokenType.SlashUnchecked,
                   TokenType.DivideUnchecked,
                   TokenType.PercentUnchecked))
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
    /// Syntax: <c>a ** b</c> and overflow variants (**%, **^).
    /// Power is right-associative: <c>a ** b ** c</c> = <c>a ** (b ** c)</c>
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        if (Match(TokenType.Power, TokenType.PowerWrap, TokenType.PowerClamp, TokenType.PowerUnchecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower(); // Recursive call for right-associativity
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    /// <summary>
    /// Parses unary prefix expressions.
    /// Syntax: <c>-x</c> (negation), <c>not x</c> (logical not), <c>~x</c> (bitwise not), <c>^n</c> (backindex).
    /// Special handling for unary minus on numeric literals to support min values.
    /// </summary>
    /// <returns>The parsed expression.</returns>
    private Expression ParseUnary()
    {
        // Handle steal expression (steal expr = ownership transfer, RazorForge only)
        if (Match(type: TokenType.Steal))
        {
            SourceLocation stealLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new StealExpression(Operand: operand, Location: stealLocation);
        }

        // Handle backindex operator (^n = index from end)
        if (Match(type: TokenType.Caret))
        {
            SourceLocation caretLocation = GetLocation(token: PeekToken(offset: -1));
            Expression operand = ParseUnary(); // Right-associative
            return new BackIndexExpression(Operand: operand, Location: caretLocation);
        }

        if (!Match(TokenType.Minus, TokenType.Not, TokenType.Tilde))
        {
            return ParsePostfix();
        }

        Token op = PeekToken(offset: -1);
        SourceLocation opLocation = GetLocation(token: op);

        // Special handling for unary minus on numeric literals
        // This allows parsing negative min values like -9_223_372_036_854_775_808_s64
        // All numeric literals are now strings, so we prepend "-" to the string
        if (op.Type == TokenType.Minus && Check(TokenType.UndecidedInteger,
                TokenType.UndecidedDecimal,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.S256Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral,
                TokenType.IntegerLiteral,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.F128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal,
                TokenType.DecimalLiteral,
                TokenType.J32Literal,
                TokenType.J64Literal,
                TokenType.J128Literal,
                TokenType.JnLiteral))
        {
            // Parse the literal
            Expression literal = ParsePostfix();

            // If it's a literal expression with string value, prepend negative sign
            if (literal is LiteralExpression { Value: string strVal } litExpr)
            {
                // Toggle negative sign: if already negative, remove it; otherwise add it
                string newValue = strVal.StartsWith('-')
                    ? strVal[1..]
                    : "-" + strVal;
                return new LiteralExpression(Value: newValue,
                    LiteralType: litExpr.LiteralType,
                    Location: opLocation);
            }
        }

        Expression expr = ParseUnary();
        return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
            Operand: expr,
            Location: opLocation);
    }
}
