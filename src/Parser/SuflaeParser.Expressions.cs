using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence climbing chain).
/// </summary>
public partial class SuflaeParser
{
    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    private Expression ParseAssignment()
    {
        Expression expr = ParseTernary();

        if (Match(type: TokenType.Assign))
        {
            Expression value = ParseAssignment();
            // For now, treat assignment as a binary expression
            return new BinaryExpression(Left: expr,
                Operator: BinaryOperator.Assign,
                Right: value,
                Location: expr.Location);
        }

        return expr;
    }

    private Expression ParseTernary()
    {
        Expression expr = ParseLogicalOr();

        if (Match(type: TokenType.Question))
        {
            Expression thenExpr = ParseExpression();
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' in ternary expression");
            Expression elseExpr = ParseExpression();
            return new ConditionalExpression(Condition: expr,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: expr.Location);
        }

        return expr;
    }

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

    private Expression ParseRange()
    {
        Expression expr = ParseLogicalAnd();

        // Handle range expressions: A to B or A to B step C
        if (Match(type: TokenType.To))
        {
            Expression end = ParseLogicalAnd();
            Expression? step = null;

            if (Match(type: TokenType.By))
            {
                step = ParseLogicalAnd();
            }

            // Create a range expression - for now use a call expression to represent it
            var args = new List<Expression> { expr, end };
            if (step != null)
            {
                args.Add(item: step);
            }

            return new CallExpression(
                Callee: new IdentifierExpression(Name: "range", Location: expr.Location),
                Arguments: args,
                Location: expr.Location);
        }

        return expr;
    }

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

    private Expression ParseEquality()
    {
        Expression expr = ParseComparison();

        while (Match(TokenType.Equal, TokenType.NotEqual))
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

    private Expression ParseComparison()
    {
        Expression expr = ParseIsExpression();

        while (Match(TokenType.Less,
                   TokenType.LessEqual,
                   TokenType.Greater,
                   TokenType.GreaterEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseIsExpression();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type),
                Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/from/follows expressions when not in entity context
        while (Match(TokenType.Is, TokenType.From, TokenType.Follows))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            if (op.Type == TokenType.Is)
            {
                // Handle is expressions: expr is Type or expr is Type(pattern)
                TypeExpression type = ParseType();

                // Check if it's a pattern with variable binding
                string? variableName = null;
                if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
                {
                    variableName = PeekToken(offset: -1)
                       .Text;
                }

                // For now, represent as a call expression
                var args = new List<Expression> { expr };
                if (variableName != null)
                {
                    args.Add(
                        item: new IdentifierExpression(Name: variableName, Location: location));
                }

                expr = new CallExpression(
                    Callee: new IdentifierExpression(Name: $"is_{type.Name}", Location: location),
                    Arguments: args,
                    Location: location);
            }
            else
            {
                // Handle from/follows as comparison operators
                Expression right = ParseBitwiseOr();
                string operatorName = op.Type == TokenType.From
                    ? "from"
                    : "follows";

                expr = new CallExpression(
                    Callee: new IdentifierExpression(Name: operatorName, Location: location),
                    Arguments: new List<Expression> { expr, right },
                    Location: location);
            }
        }

        return expr;
    }

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

    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (Match(TokenType.Plus,
                   TokenType.Minus,
                   TokenType.PlusWrap,
                   TokenType.PlusSaturate,
                   TokenType.PlusUnchecked,
                   TokenType.PlusChecked,
                   TokenType.MinusWrap,
                   TokenType.MinusSaturate,
                   TokenType.MinusUnchecked,
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

    private Expression ParseMultiplicative()
    {
        Expression expr = ParsePower();

        while (Match(TokenType.Star,
                   TokenType.Slash,
                   TokenType.Percent,
                   TokenType.Divide,
                   TokenType.MultiplyWrap,
                   TokenType.MultiplySaturate,
                   TokenType.MultiplyUnchecked,
                   TokenType.MultiplyChecked,
                   TokenType.DivideWrap,
                   TokenType.DivideSaturate,
                   TokenType.DivideUnchecked,
                   TokenType.DivideChecked,
                   TokenType.ModuloWrap,
                   TokenType.ModuloSaturate,
                   TokenType.ModuloUnchecked,
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

    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        while (Match(TokenType.Power,
                   TokenType.PowerWrap,
                   TokenType.PowerSaturate,
                   TokenType.PowerUnchecked,
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

    private Expression ParseUnary()
    {
        if (Match(TokenType.Plus,
                TokenType.Minus,
                TokenType.Bang,
                TokenType.Not,
                TokenType.Tilde))
        {
            Token op = PeekToken(offset: -1);
            Expression expr = ParseUnary();
            return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
                Operand: expr,
                Location: GetLocation(token: op));
        }

        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            if (Match(type: TokenType.LeftParen))
            {
                // Function call
                var args = new List<Expression>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        args.Add(item: ParseArgument());
                    } while (Match(type: TokenType.Comma));
                }

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
                // Member access
                string member = ConsumeIdentifier(errorMessage: "Expected member name after '.'");
                expr = new MemberExpression(Object: expr,
                    PropertyName: member,
                    Location: expr.Location);
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expression ParsePrimary()
    {
        SourceLocation location = GetLocation();

        // Literals
        if (Match(type: TokenType.True))
        {
            return new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }

        if (Match(type: TokenType.False))
        {
            return new LiteralExpression(Value: false,
                LiteralType: TokenType.False,
                Location: location);
        }

        if (Match(type: TokenType.None))
        {
            return new LiteralExpression(Value: null!,
                LiteralType: TokenType.None,
                Location: location);
        }

        // Integer literals
        if (Match(TokenType.Integer,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.S128Literal,
                TokenType.SyssintLiteral,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.U128Literal,
                TokenType.SysuintLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            string cleanValue = value.Replace(oldValue: "s8", newValue: "")
                                     .Replace(oldValue: "s16", newValue: "")
                                     .Replace(oldValue: "s32", newValue: "")
                                     .Replace(oldValue: "s64", newValue: "")
                                     .Replace(oldValue: "s128", newValue: "")
                                     .Replace(oldValue: "saddr", newValue: "")
                                     .Replace(oldValue: "u8", newValue: "")
                                     .Replace(oldValue: "u16", newValue: "")
                                     .Replace(oldValue: "u32", newValue: "")
                                     .Replace(oldValue: "u64", newValue: "")
                                     .Replace(oldValue: "u128", newValue: "")
                                     .Replace(oldValue: "uaddr", newValue: "")
                                     .Replace(oldValue: "_", newValue: ""); // Remove underscores
            if (long.TryParse(s: cleanValue, result: out long intVal))
            {
                return new LiteralExpression(Value: intVal,
                    LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid integer literal: {value}");
        }

        // Float literals
        if (Match(TokenType.Decimal,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal,
                TokenType.F128Literal,
                TokenType.D32Literal,
                TokenType.D64Literal,
                TokenType.D128Literal))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            string cleanValue = value.Replace(oldValue: "f16", newValue: "")
                                     .Replace(oldValue: "f32", newValue: "")
                                     .Replace(oldValue: "f64", newValue: "")
                                     .Replace(oldValue: "f128", newValue: "")
                                     .Replace(oldValue: "d32", newValue: "")
                                     .Replace(oldValue: "d64", newValue: "")
                                     .Replace(oldValue: "d128", newValue: "")
                                     .Replace(oldValue: "_", newValue: ""); // Remove underscores
            if (double.TryParse(s: cleanValue, result: out double floatVal))
            {
                return new LiteralExpression(Value: floatVal,
                    LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid float literal: {value}");
        }

        // String literals with Suflae-specific handling
        if (Match(TokenType.TextLiteral,
                TokenType.FormattedText,
                TokenType.RawText,
                TokenType.RawFormattedText,
                TokenType.Text8Literal,
                TokenType.Text8FormattedText,
                TokenType.Text8RawText,
                TokenType.Text8RawFormattedText,
                TokenType.Text16Literal,
                TokenType.Text16FormattedText,
                TokenType.Text16RawText,
                TokenType.Text16RawFormattedText))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;

            // Handle Suflae's formatted string literals (f"...")
            if (value.StartsWith(value: "f\"") && value.EndsWith(value: "\""))
            {
                // This is a formatted string like f"{name} says hello"
                // For now, treat as regular string but mark as formatted
                value = value.Substring(startIndex: 2,
                    length: value.Length - 3); // Remove f" and "
                return new LiteralExpression(Value: value,
                    LiteralType: TokenType.FormattedText,
                    Location: location);
            }

            // Regular string literals
            if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
            {
                value = value.Substring(startIndex: 1, length: value.Length - 2);
            }

            // Handle text encoding prefixes (t8", t16", t32")
            if (value.StartsWith(value: "t8\"") || value.StartsWith(value: "t16\"") ||
                value.StartsWith(value: "t32\""))
            {
                int prefixEnd = value.IndexOf(value: '"');
                if (prefixEnd > 0)
                {
                    value = value.Substring(startIndex: prefixEnd + 1,
                        length: value.Length - prefixEnd - 2);
                }
            }

            return new LiteralExpression(Value: value,
                LiteralType: token.Type,
                Location: location);
        }

        // Character literals
        if (Match(TokenType.Letter8Literal, TokenType.Letter16Literal, TokenType.LetterLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;

            try
            {
                // Handle different prefixes: l8'a', l16'a', 'a'
                int quoteStart = value.LastIndexOf(value: '\'');
                int quoteEnd = value.Length - 1;

                if (quoteStart >= 0 && quoteEnd > quoteStart && value[index: quoteEnd] == '\'')
                {
                    string charContent = value.Substring(startIndex: quoteStart + 1,
                        length: quoteEnd - quoteStart - 1);

                    // Handle escape sequences
                    if (charContent == "\\'") // Single quote
                    {
                        return new LiteralExpression(Value: '\'',
                            LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\\\") // Backslash
                    {
                        return new LiteralExpression(Value: '\\',
                            LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\n") // Newline
                    {
                        return new LiteralExpression(Value: '\n',
                            LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\t") // Tab
                    {
                        return new LiteralExpression(Value: '\t',
                            LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\r") // Carriage return
                    {
                        return new LiteralExpression(Value: '\r',
                            LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent.Length == 1)
                    {
                        return new LiteralExpression(Value: charContent[index: 0],
                            LiteralType: token.Type,
                            Location: location);
                    }
                }
            }
            catch
            {
                // Fall through to error
            }

            // For now, just return a placeholder for invalid character literals
            return new LiteralExpression(Value: '?', LiteralType: token.Type, Location: location);
        }

        // Identifiers and Suflae-specific keywords
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string text = PeekToken(offset: -1)
               .Text;

            // Handle Suflae's "me" keyword for self-reference
            if (text == "me")
            {
                return new IdentifierExpression(Name: "this",
                    Location: location); // Map to C# "this"
            }

            return new IdentifierExpression(Name: text, Location: location);
        }

        // Parenthesized expression
        if (Match(type: TokenType.LeftParen))
        {
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
        }

        // Conditional expression: if A then B else C
        if (Match(type: TokenType.If))
        {
            Expression condition = ParseExpression();
            Consume(type: TokenType.Then,
                errorMessage: "Expected 'then' in conditional expression");
            Expression thenExpr = ParseExpression();
            Consume(type: TokenType.Else,
                errorMessage: "Expected 'else' in conditional expression");
            Expression elseExpr = ParseExpression();
            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: location);
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

        throw new ParseException(message: $"Unexpected token: {CurrentToken.Type}");
    }
}
