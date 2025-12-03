using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing expression parsing (precedence chain, operators, primary expressions).
/// </summary>
public partial class RazorForgeParser
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
        Expression expr = ParseNoneCoalesce();

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

        // TODO: 10 downto 1/10 down to 0 by 2

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

        // Check for chained comparisons (a < b < c)
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };

        while (Match(TokenType.Less,
                   TokenType.LessEqual,
                   TokenType.Greater,
                   TokenType.GreaterEqual,
                   TokenType.Equal,
                   TokenType.NotEqual))
        {
            Token op = PeekToken(offset: -1);
            operators.Add(item: TokenToBinaryOperator(tokenType: op.Type));

            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        // If we have chained comparisons, create a ChainedComparisonExpression
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(Operands: operands,
                Operators: operators,
                Location: GetLocation());
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

    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/from/follows expressions when not in when pattern/clause context
        // When inside a when clause body, we don't want to parse 'is' as an expression operator
        // because it would consume the 'is' from the next when clause pattern
        while (!_inWhenPatternContext && !_inWhenClauseBody &&
               Match(TokenType.Is, TokenType.From, TokenType.Follows))
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
            // Handle standalone generic function calls like routine<T>!(args)
            // Only parse as generics if the identifier is followed by a type identifier or uppercase letter
            // to avoid confusing comparison operators with generics
            if (expr is IdentifierExpression && Check(type: TokenType.Less))
            {
                // Lookahead to check if this is likely a generic or a comparison
                // If the next token after '<' is a lowercase identifier or a comparison would make sense,
                // don't treat as generic
                int savedPos = Position;
                Advance(); // consume '<'

                bool isLikelyGeneric = Check(type: TokenType.TypeIdentifier) ||
                                       Check(type: TokenType.Identifier) &&
                                       char.IsUpper(c: CurrentToken.Text[index: 0]) ||
                                       Check(type: TokenType.Identifier) &&
                                       IsPrimitiveTypeName(name: CurrentToken.Text);

                Position = savedPos; // restore position

                if (!isLikelyGeneric)
                {
                    // Not a generic, let the comparison operator handle it
                    break;
                }

                Advance(); // consume '<' again
                var typeArgs = new List<TypeExpression>();
                do
                {
                    typeArgs.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.Greater,
                    errorMessage: "Expected '>' after generic type arguments");

                // Check for memory operation with !
                bool isMemoryOperation = Match(type: TokenType.Bang);

                if (Match(type: TokenType.LeftParen))
                {
                    // Use ParseArgumentList to support named arguments (name: value)
                    List<Expression> args = ParseArgumentList();

                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after arguments");

                    expr = new GenericMethodCallExpression(Object: expr,
                        MethodName: ((IdentifierExpression)expr).Name,
                        TypeArguments: typeArgs,
                        Arguments: args,
                        IsMemoryOperation: isMemoryOperation,
                        Location: expr.Location);
                }
                else if (Match(type: TokenType.LeftBrace))
                {
                    // Struct literal: Type<T> { field: value, ... }
                    string typeName = ((IdentifierExpression)expr).Name;
                    List<(string Name, Expression Value)> fields = ParseStructLiteralFields();
                    Consume(type: TokenType.RightBrace,
                        errorMessage: "Expected '}' after struct literal fields");
                    expr = new ConstructorExpression(TypeName: typeName,
                        TypeArguments: typeArgs,
                        Fields: fields,
                        Location: expr.Location);
                }
                else
                {
                    // Generic type reference without call
                    expr = new GenericMemberExpression(Object: expr,
                        MemberName: ((IdentifierExpression)expr).Name,
                        TypeArguments: typeArgs,
                        Location: expr.Location);
                }
            }
            // Struct literal for non-generic types: Type { field: value, ... }
            else if (expr is IdentifierExpression identExpr2 &&
                     char.IsUpper(c: identExpr2.Name[index: 0]) &&
                     Match(type: TokenType.LeftBrace))
            {
                List<(string Name, Expression Value)> fields = ParseStructLiteralFields();
                Consume(type: TokenType.RightBrace,
                    errorMessage: "Expected '}' after struct literal fields");
                expr = new ConstructorExpression(TypeName: identExpr2.Name,
                    TypeArguments: null,
                    Fields: fields,
                    Location: expr.Location);
            }
            // Throwable function call: identifier!(args) with named arguments
            else if (Check(type: TokenType.Bang) && PeekToken(offset: 1)
                        .Type == TokenType.LeftParen)
            {
                Advance(); // consume '!'
                Advance(); // consume '('

                // Function call - supports named arguments (name: value)
                List<Expression> args = ParseArgumentList();

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                // Mark as failable call by appending ! to the callee name
                if (expr is IdentifierExpression identExpr)
                {
                    expr = new CallExpression(
                        Callee: new IdentifierExpression(Name: identExpr.Name + "!",
                            Location: identExpr.Location),
                        Arguments: args,
                        Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr,
                        Arguments: args,
                        Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.LeftParen))
            {
                // Function call - supports named arguments (name: value)
                List<Expression> args = ParseArgumentList();

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                // Check if this is a slice constructor call
                if (expr is IdentifierExpression identifier &&
                    (identifier.Name == "DynamicSlice" || identifier.Name == "TemporarySlice") &&
                    args.Count == 1)
                {
                    expr = new SliceConstructorExpression(SliceType: identifier.Name,
                        SizeExpression: args[index: 0],
                        Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr,
                        Arguments: args,
                        Location: expr.Location);
                }
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
                // Member access - allow keywords like 'where', 'none' as method names
                string member = ConsumeMethodName(errorMessage: "Expected member name after '.'");

                // Check for generic method call with type parameters
                // Only parse as generics if the next token after '<' looks like a type
                // to avoid confusing comparison operators with generics (e.g., me.current < me.step)
                if (Check(type: TokenType.Less))
                {
                    // Lookahead to check if this is likely a generic or a comparison
                    int savedPos = Position;
                    Advance(); // consume '<'

                    bool isLikelyGeneric = Check(type: TokenType.TypeIdentifier) ||
                                           Check(type: TokenType.Identifier) &&
                                           char.IsUpper(c: CurrentToken.Text[index: 0]) ||
                                           Check(type: TokenType.Identifier) &&
                                           IsPrimitiveTypeName(name: CurrentToken.Text);

                    Position = savedPos; // restore position

                    if (isLikelyGeneric)
                    {
                        Advance(); // consume '<' again
                        var typeArgs = new List<TypeExpression>();
                        do
                        {
                            typeArgs.Add(item: ParseType());
                        } while (Match(type: TokenType.Comma));

                        Consume(type: TokenType.Greater,
                            errorMessage: "Expected '>' after generic type arguments");

                        // Check for method call with !
                        bool isGenericMemOp = Match(type: TokenType.Bang);

                        if (Match(type: TokenType.LeftParen))
                        {
                            // Use ParseArgumentList to support named arguments (name: value)
                            List<Expression> genericArgs = ParseArgumentList();

                            Consume(type: TokenType.RightParen,
                                errorMessage: "Expected ')' after arguments");

                            expr = new GenericMethodCallExpression(Object: expr,
                                MethodName: member,
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

                        continue; // continue the while loop to check for more postfix ops
                    }
                    // Not a generic, fall through to regular member access below
                }

                // Regular member access (no generic type args, or < was a comparison operator)
                // Check for memory operation with !
                bool isMemoryOperation = Match(type: TokenType.Bang);

                if (isMemoryOperation && Match(type: TokenType.LeftParen))
                {
                    // Use ParseArgumentList to support named arguments (name: value)
                    List<Expression> args = ParseArgumentList();

                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after arguments");

                    expr = new MemoryOperationExpression(Object: expr,
                        OperationName: member,
                        Arguments: args,
                        Location: expr.Location);
                }
                else
                {
                    expr = new MemberExpression(Object: expr,
                        PropertyName: member,
                        Location: expr.Location);
                }
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

            long intVal;
            // Handle hexadecimal literals (0x prefix)
            if (cleanValue.StartsWith(value: "0x") || cleanValue.StartsWith(value: "0X"))
            {
                string hexPart = cleanValue.Substring(startIndex: 2); // Remove "0x" prefix
                if (long.TryParse(s: hexPart,
                        style: System.Globalization.NumberStyles.HexNumber,
                        provider: null,
                        result: out intVal))
                {
                    return new LiteralExpression(Value: intVal,
                        LiteralType: token.Type,
                        Location: location);
                }
            }
            // Handle binary literals (0b prefix)
            else if (cleanValue.StartsWith(value: "0b") || cleanValue.StartsWith(value: "0B"))
            {
                string binaryPart = cleanValue.Substring(startIndex: 2); // Remove "0b" prefix
                try
                {
                    intVal = Convert.ToInt64(value: binaryPart, fromBase: 2);
                    return new LiteralExpression(Value: intVal,
                        LiteralType: token.Type,
                        Location: location);
                }
                catch (Exception)
                {
                    throw new ParseException(message: $"Invalid binary literal: {value}");
                }
            }
            // Handle decimal literals
            else if (long.TryParse(s: cleanValue, result: out intVal))
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

        // String literals
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

            // Handle RazorForge's formatted string literals (f"...")
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

            // Regular string literals - strip quotes and prefixes
            if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
            {
                value = value.Substring(startIndex: 1, length: value.Length - 2);
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

        // Memory size literals
        if (Match(TokenType.ByteLiteral,
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
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            // Extract numeric part from memory literals like "100mb", "4gb", etc.
            string numericPart = new(value: value.TakeWhile(predicate: char.IsDigit)
                                                 .ToArray());
            if (long.TryParse(s: numericPart, result: out long memoryVal))
            {
                return new LiteralExpression(Value: memoryVal,
                    LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid memory literal: {value}");
        }

        // Duration/time literals
        if (Match(TokenType.WeekLiteral,
                TokenType.DayLiteral,
                TokenType.HourLiteral,
                TokenType.MinuteLiteral,
                TokenType.SecondLiteral,
                TokenType.MillisecondLiteral,
                TokenType.MicrosecondLiteral,
                TokenType.NanosecondLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            // Extract numeric part from time literals like "30m", "24h", "500ms", etc.
            string numericPart = new(value: value.TakeWhile(predicate: char.IsDigit)
                                                 .ToArray());
            if (long.TryParse(s: numericPart, result: out long timeVal))
            {
                return new LiteralExpression(Value: timeVal,
                    LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid time literal: {value}");
        }

        // Arrow lambda expression: x => expr (single parameter, no parens)
        // ONLY parse as lambda if we're NOT inside a when clause pattern.
        // Inside when blocks, patterns like: a < b => action should not treat b => action as lambda.
        if (!_inWhenPatternContext && Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.FatArrow)
        {
            return ParseArrowLambdaExpression(location: location);
        }

        // Self/me keyword - used for referencing the current instance
        if (Match(type: TokenType.Self))
        {
            return new IdentifierExpression(Name: "me", Location: location);
        }

        // Identifiers
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return new IdentifierExpression(Name: PeekToken(offset: -1)
                   .Text,
                Location: location);
        }

        // Parenthesized expression or arrow lambda with parenthesized params
        if (Match(type: TokenType.LeftParen))
        {
            // Check if this is a lambda: (params) => expr
            if (IsArrowLambdaParameters())
            {
                return ParseParenthesizedArrowLambda(location: location);
            }

            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
        }

        // Lambda expression with routine keyword
        if (Match(type: TokenType.Routine))
        {
            return ParseLambdaExpression(location: location);
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

        // Conditional expression:
        // - Classic syntax: if A then B else C
        // - Block syntax: if A { B } else { C }
        if (Match(type: TokenType.If))
        {
            Expression condition = ParseExpression();

            Expression thenExpr;
            Expression elseExpr;

            if (Match(type: TokenType.Then))
            {
                // Classic syntax: if A then B else C
                thenExpr = ParseExpression();
                Consume(type: TokenType.Else,
                    errorMessage: "Expected 'else' in conditional expression");
                elseExpr = ParseExpression();
            }
            else if (Check(type: TokenType.LeftBrace))
            {
                // Block syntax: if A { B } else { C }
                thenExpr = ParseBlockExpression();
                Consume(type: TokenType.Else,
                    errorMessage: "Expected 'else' in conditional expression");
                elseExpr = ParseBlockExpression();
            }
            else
            {
                throw new ParseException(
                    message: "Expected 'then' or '{' in conditional expression");
            }

            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: location);
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
}
