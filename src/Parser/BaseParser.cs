using System;
using System.Numerics;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.Parser;

/// <summary>
/// Base entity for recursive descent parsers
/// </summary>
public abstract class BaseParser
{
    protected readonly List<Token> Tokens;
    protected int Position = 0;
    protected readonly List<CompileWarning> Warnings = new();
    public string fileName = "";

    protected BaseParser(List<Token> tokens, string fileName)
    {
        Tokens = tokens;
        this.fileName = fileName;
    }

    /// <summary>
    /// Parse the tokens into an AST
    /// </summary>
    public abstract Compilers.Shared.AST.Program Parse();

    // Token management
    protected Token CurrentToken => Position < Tokens.Count
        ? Tokens[index: Position]
        : Tokens[^1];

    protected Token PeekToken(int offset = 1)
    {
        return Position + offset < Tokens.Count
            ? Tokens[index: Position + offset]
            : Tokens[^1];
    }

    protected bool IsAtEnd => Position >= Tokens.Count || CurrentToken.Type == TokenType.Eof;

    /// <summary>
    /// Advance to the next token and return the current one
    /// </summary>
    protected Token Advance()
    {
        Token token = CurrentToken;
        if (!IsAtEnd)
        {
            Position++;
        }

        return token;
    }

    /// <summary>
    /// Check if current token matches the expected type
    /// </summary>
    protected bool Check(TokenType type)
    {
        return !IsAtEnd && CurrentToken.Type == type;
    }

    /// <summary>
    /// Check if current token matches any of the expected types
    /// </summary>
    protected bool Check(params TokenType[] types)
    {
        return types.Any(predicate: Check);
    }

    /// <summary>
    /// Consume token if it matches expected type, otherwise throw error
    /// </summary>
    protected Token Consume(TokenType type, string errorMessage)
    {
        if (Check(type: type))
        {
            return Advance();
        }

        Token current = CurrentToken;
        throw new ParseException(message: $"{errorMessage}. Expected {type}, got {current.Type}.");
    }

    /// <summary>
    /// Consume token if it matches expected type, return whether successful
    /// </summary>
    protected bool Match(TokenType type)
    {
        if (Check(type: type))
        {
            Advance();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Consume token if it matches any expected type, return whether successful
    /// </summary>
    protected bool Match(params TokenType[] types)
    {
        foreach (TokenType type in types)
        {
            if (Check(type: type))
            {
                Advance();
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Operator precedence levels (higher number = higher precedence)
    /// </summary>
    protected enum Precedence
    {
        None = 0,
        Lambda = 1, // lambda expressions
        Conditional = 2, // if then else
        NoneCoalesce = 3, // ??
        Range = 4, // a to b step c
        LogicalOr = 5, // or
        LogicalAnd = 6, // and
        LogicalNot = 7, // not

        Comparison =
            8, // in, is, from, follows, <, <=, >, >=, ==, !=, notin, isnot, notfrom, notfollows
        BitwiseOr = 9, // |
        BitwiseXor = 10, // ^
        BitwiseAnd = 11, // &
        Shift = 12, // <<, >>
        Additive = 13, // +, - and variants
        Multiplicative = 14, // *, /, //, % and variants
        Unary = 15, // +x, -x, ~x
        Power = 16, // **, **%, **^, **?, **!
        Postfix = 17, // x[index], x.member, x()
        Primary = 18 // literals, identifiers, ()
    }

    /// <summary>
    /// Get precedence for binary operators
    /// </summary>
    protected Precedence GetBinaryPrecedence(TokenType type)
    {
        return type switch
        {
            // None coalescing operator
            TokenType.NoneCoalesce => Precedence.NoneCoalesce,

            // Logical operators
            TokenType.Or => Precedence.LogicalOr,
            TokenType.And => Precedence.LogicalAnd,

            // Comparison operators
            TokenType.In or TokenType.Is or TokenType.From or TokenType.Follows => Precedence
               .Comparison,
            TokenType.NotIn or TokenType.IsNot or TokenType.NotFrom or TokenType.NotFollows =>
                Precedence.Comparison,
            TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual =>
                Precedence.Comparison,
            TokenType.Equal or TokenType.NotEqual => Precedence.Comparison,

            // Bitwise operators
            TokenType.Pipe => Precedence.BitwiseOr,
            TokenType.Caret => Precedence.BitwiseXor,
            TokenType.Ampersand => Precedence.BitwiseAnd,

            // Shift operators
            TokenType.LeftShift or TokenType.LeftShiftChecked or TokenType.RightShift
                or TokenType.LogicalLeftShift or TokenType.LogicalRightShift => Precedence.Shift,

            // Additive operators
            TokenType.Plus or TokenType.Minus => Precedence.Additive,
            TokenType.PlusWrap or TokenType.PlusSaturate or TokenType.PlusUnchecked
                or TokenType.PlusChecked => Precedence.Additive,
            TokenType.MinusWrap or TokenType.MinusSaturate or TokenType.MinusUnchecked
                or TokenType.MinusChecked => Precedence.Additive,

            // Multiplicative operators
            TokenType.Star or TokenType.Slash or TokenType.Divide or TokenType.Percent =>
                Precedence.Multiplicative,
            TokenType.MultiplyWrap or TokenType.MultiplySaturate or TokenType.MultiplyUnchecked
                or TokenType.MultiplyChecked => Precedence.Multiplicative,
            TokenType.DivideWrap or TokenType.DivideSaturate or TokenType.DivideUnchecked
                or TokenType.DivideChecked => Precedence.Multiplicative,
            TokenType.ModuloWrap or TokenType.ModuloSaturate or TokenType.ModuloUnchecked
                or TokenType.ModuloChecked => Precedence.Multiplicative,

            // Power operators
            TokenType.Power => Precedence.Power,
            TokenType.PowerWrap or TokenType.PowerSaturate or TokenType.PowerUnchecked
                or TokenType.PowerChecked => Precedence.Power,

            _ => Precedence.None
        };
    }

    /// <summary>
    /// Check if token is a comparison operator that can be chained
    /// </summary>
    protected bool IsComparisonOperator(TokenType type)
    {
        return type switch
        {
            TokenType.Less or TokenType.LessEqual or TokenType.Greater or TokenType.GreaterEqual
                or TokenType.Equal or TokenType.In or TokenType.Is or TokenType.From
                or TokenType.Follows or TokenType.NotIn or TokenType.IsNot or TokenType.NotFrom
                or TokenType.NotFollows => true,
            _ => false
        };
    }

    /// <summary>
    /// Get comparison direction: -1 for descending (>, >=), 0 for equality (==, is, etc.), 1 for ascending (<, <=)
    /// </summary>
    protected int GetComparisonDirection(TokenType type)
    {
        return type switch
        {
            TokenType.Less or TokenType.LessEqual or TokenType.In or TokenType.From
                or TokenType.Follows => 1, // Ascending
            TokenType.Greater or TokenType.GreaterEqual or TokenType.NotIn or TokenType.NotFrom
                or TokenType.NotFollows => -1, // Descending
            TokenType.Equal or TokenType.Is or TokenType.IsNot => 0, // Equality
            _ => 0
        };
    }

    /// <summary>
    /// Validate that comparison chain maintains consistent direction
    /// </summary>
    protected bool IsValidComparisonChain(List<BinaryOperator> operators)
    {
        if (operators.Count <= 1)
        {
            return true;
        }

        var directions = operators
                        .Select(selector: op =>
                             GetComparisonDirection(type: BinaryOperatorToToken(op: op)))
                        .ToList();

        // All equality is valid
        if (directions.All(predicate: d => d == 0))
        {
            return true;
        }

        // Check for consistent direction (all ascending, all descending, or mixed with equality only)
        var nonZeroDirections = directions.Where(predicate: d => d != 0)
                                          .Distinct()
                                          .ToList();
        return nonZeroDirections.Count <= 1; // Only one direction (plus equality)
    }

    /// <summary>
    /// Convert BinaryOperator back to TokenType for validation
    /// </summary>
    protected TokenType BinaryOperatorToToken(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Less => TokenType.Less,
            BinaryOperator.LessEqual => TokenType.LessEqual,
            BinaryOperator.Greater => TokenType.Greater,
            BinaryOperator.GreaterEqual => TokenType.GreaterEqual,
            BinaryOperator.Equal => TokenType.Equal,
            BinaryOperator.In => TokenType.In,
            BinaryOperator.NotIn => TokenType.NotIn,
            BinaryOperator.Is => TokenType.Is,
            BinaryOperator.IsNot => TokenType.IsNot,
            BinaryOperator.From => TokenType.From,
            BinaryOperator.NotFrom => TokenType.NotFrom,
            BinaryOperator.Follows => TokenType.Follows,
            BinaryOperator.NotFollows => TokenType.NotFollows,
            _ => TokenType.Equal
        };
    }

    /// <summary>
    /// Check if operator is right-associative
    /// </summary>
    protected bool IsRightAssociative(TokenType type)
    {
        return type switch
        {
            // Power operators are right-associative
            TokenType.Power or TokenType.PowerWrap or TokenType.PowerSaturate
                or TokenType.PowerUnchecked or TokenType.PowerChecked => true,

            // Assignment operators would be right-associative
            TokenType.Assign => true,

            _ => false
        };
    }

    /// <summary>
    /// Skip tokens until we find a synchronization point for error recovery
    /// </summary>
    protected void Synchronize()
    {
        Advance();

        while (!IsAtEnd)
        {
            if (PeekToken(offset: -1)
                   .Type == TokenType.Newline)
            {
                return;
            }

            switch (CurrentToken.Type)
            {
                case TokenType.Entity:
                case TokenType.Record:
                case TokenType.Choice:
                case TokenType.Variant:
                case TokenType.Mutant:
                case TokenType.Protocol:
                case TokenType.Routine:
                case TokenType.Var:
                case TokenType.Let:
                case TokenType.If:
                case TokenType.While:
                case TokenType.For:
                case TokenType.Return:
                    return;
            }

            Advance();
        }
    }

    /// <summary>
    /// Create a source location from the current token
    /// </summary>
    protected SourceLocation GetLocation()
    {
        return GetLocation(token: CurrentToken);
    }
    protected SourceLocation GetLocation(Token token)
    {
        return new SourceLocation(
            FileName: fileName,
            Line: token.Line,
            Column: token.Column,
            Position: token.Position);
    }

    /// <summary>
    /// Parse expression using Pratt parser with precedence climbing
    /// </summary>
    protected Expression ParseExpression(Precedence minPrecedence = Precedence.None)
    {
        // Handle 'if then else' conditional expressions at the lowest precedence
        if (minPrecedence <= Precedence.Conditional && Match(type: TokenType.If))
        {
            Expression condition = ParseExpression(minPrecedence: Precedence.Range);
            Consume(type: TokenType.Then,
                errorMessage: "Expected 'then' in conditional expression");
            Expression thenExpr = ParseExpression(minPrecedence: Precedence.Range);
            Consume(type: TokenType.Else,
                errorMessage: "Expected 'else' in conditional expression");
            // Parse else expression at higher precedence to prevent nesting
            Expression elseExpr = ParseExpression(minPrecedence: Precedence.Range);
            return new ConditionalExpression(Condition: condition,
                TrueExpression: thenExpr,
                FalseExpression: elseExpr,
                Location: GetLocation());
        }

        // Parse prefix expression (unary operators, literals, etc.)
        Expression left = ParsePrefixExpression();

        // Handle range expressions: a to b [by c] or a downto b [by c]
        if (minPrecedence <= Precedence.Range &&
            (Match(type: TokenType.To) || Match(type: TokenType.Downto)))
        {
            bool isDescending = PeekToken(offset: -1)
               .Type == TokenType.Downto;
            Expression end = ParseExpression(minPrecedence: Precedence.LogicalOr);
            Expression? step = null;

            if (Match(type: TokenType.By))
            {
                step = ParseExpression(minPrecedence: Precedence.LogicalOr);
            }

            left = new RangeExpression(Start: left,
                End: end,
                Step: step,
                IsDescending: isDescending,
                Location: GetLocation());
        }

        // Check for comparison chaining at comparison precedence level
        if (minPrecedence <= Precedence.Comparison &&
            IsComparisonOperator(type: CurrentToken.Type))
        {
            ChainedComparisonExpression? chainResult = TryParseComparisonChain(left: left);
            if (chainResult != null)
            {
                left = chainResult;
            }
        }

        // Parse infix expressions (binary operators) with precedence climbing
        while (!IsAtEnd)
        {
            Precedence precedence = GetBinaryPrecedence(type: CurrentToken.Type);
            if (precedence <= minPrecedence)
            {
                break;
            }

            // Skip comparison operators if we already handled chaining
            if (precedence == Precedence.Comparison && left is ChainedComparisonExpression)
            {
                break;
            }

            // Skip range operators - they're handled above
            if (CurrentToken.Type == TokenType.To || CurrentToken.Type == TokenType.Downto ||
                CurrentToken.Type == TokenType.By)
            {
                break;
            }

            // Handle right-associative operators
            if (IsRightAssociative(type: CurrentToken.Type))
            {
                left = ParseInfixExpression(left: left,
                    minPrecedence: (Precedence)((int)precedence - 1));
            }
            else
            {
                left = ParseInfixExpression(left: left, minPrecedence: precedence);
            }
        }

        return left;
    }

    /// <summary>
    /// Parse prefix expression (unary operators, primary expressions)
    /// </summary>
    protected Expression ParsePrefixExpression()
    {
        // Handle unary operators
        if (Match(TokenType.Plus,
                TokenType.Minus,
                TokenType.Tilde,
                TokenType.Not,
                TokenType.Bang))
        {
            Token op = PeekToken(offset: -1);
            Expression operand = ParseExpression(minPrecedence: Precedence.Unary);
            return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
                Operand: operand,
                Location: GetLocation(token: op));
        }

        // Parse postfix expression (which includes primary)
        return ParsePostfixExpression();
    }

    /// <summary>
    /// Parse postfix expressions (member access, indexing, function calls)
    /// </summary>
    protected Expression ParsePostfixExpression()
    {
        Expression expr = ParsePrimaryExpression();

        while (!IsAtEnd)
        {
            if (Match(type: TokenType.LeftBracket))
            {
                // Array/index access: x[index]
                Expression index = ParseExpression();
                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index");
                expr = new IndexExpression(Object: expr, Index: index, Location: GetLocation());
            }
            else if (Match(type: TokenType.Dot))
            {
                // Check if this is a type conversion: x.i32!()
                if (IsTypeConversion())
                {
                    Token typeToken = Advance();
                    string typeName = IsNumericTypeToken(type: typeToken.Type)
                        ? GetTypeNameFromToken(type: typeToken.Type)
                        : typeToken.Text;
                    Consume(type: TokenType.Bang, errorMessage: "Expected '!' after type name");
                    Consume(type: TokenType.LeftParen,
                        errorMessage: "Expected '(' after type conversion");
                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after type conversion");
                    expr = new TypeConversionExpression(TargetType: typeName,
                        Expression: expr,
                        IsMethodStyle: true,
                        Location: GetLocation());
                }
                else
                {
                    // Regular member access: x.member
                    Token member = Consume(type: TokenType.Identifier,
                        errorMessage: "Expected member name after '.'");
                    expr = new MemberExpression(Object: expr,
                        PropertyName: member.Text,
                        Location: GetLocation());
                }
            }
            else if (Check(type: TokenType.LeftParen) && !IsNewExpressionAhead())
            {
                // Function call: x()
                Match(type: TokenType.LeftParen);
                List<Expression> args = ParseArgumentList();
                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");
                expr = new CallExpression(Callee: expr, Arguments: args, Location: GetLocation());
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    /// <summary>
    /// Check if the current position represents a type conversion: type!()
    /// </summary>
    protected bool IsTypeConversion()
    {
        Token token = PeekToken();
        return (IsTypeIdentifier(type: token.Type) || token.Type == TokenType.Identifier &&
            IsTypeNameIdentifier(tokenText: token.Text)) && PeekToken(offset: 1)
           .Type == TokenType.Bang && PeekToken(offset: 2)
           .Type == TokenType.LeftParen;
    }

    /// <summary>
    /// Check if a token type represents a type identifier
    /// </summary>
    protected bool IsTypeIdentifier(TokenType type)
    {
        return type == TokenType.Identifier || type == TokenType.TypeIdentifier ||
               IsNumericTypeToken(type: type);
    }

    /// <summary>
    /// Check if a token represents a numeric type name (for identifiers)
    /// </summary>
    protected bool IsTypeNameIdentifier(string tokenText)
    {
        return tokenText switch
        {
            "i8" or "i16" or "i32" or "i64" or "u8" or "u16" or "u32" or "u64" or "f16" or "f32"
                or "f64" or "f128" => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if a token represents a numeric type (i8, i32, f32, etc.)
    /// </summary>
    protected bool IsNumericTypeToken(TokenType type)
    {
        return type is TokenType.S8Literal or TokenType.S16Literal or TokenType.S32Literal
            or TokenType.S64Literal or TokenType.U8Literal or TokenType.U16Literal
            or TokenType.U32Literal or TokenType.U64Literal or TokenType.F16Literal
            or TokenType.F32Literal or TokenType.F64Literal;
    }

    /// <summary>
    /// Get the type name string from a numeric type token
    /// </summary>
    protected string GetTypeNameFromToken(TokenType type)
    {
        return type switch
        {
            TokenType.S8Literal => "s8",
            TokenType.S16Literal => "s16",
            TokenType.S32Literal => "s32",
            TokenType.S64Literal => "s64",
            TokenType.U8Literal => "u8",
            TokenType.U16Literal => "u16",
            TokenType.U32Literal => "u32",
            TokenType.U64Literal => "u64",
            TokenType.F16Literal => "f16",
            TokenType.F32Literal => "f32",
            TokenType.F64Literal => "f64",
            _ => throw new ArgumentException(message: $"Invalid numeric type token: {type}")
        };
    }

    /// <summary>
    /// Parse infix expression (binary operators)
    /// </summary>
    protected Expression ParseInfixExpression(Expression left, Precedence minPrecedence)
    {
        Token op = Advance();

        // Regular binary operators
        Expression right = ParseExpression(minPrecedence: minPrecedence);
        return new BinaryExpression(Left: left,
            Operator: TokenToBinaryOperator(tokenType: op.Type),
            Right: right,
            Location: GetLocation(token: op));
    }

    /// <summary>
    /// Parse primary expressions (literals, identifiers, parenthesized expressions)
    /// </summary>
    protected Expression ParsePrimaryExpression()
    {
        // Boolean literals
        if (Match(type: TokenType.True))
        {
            return new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: GetLocation(token: PeekToken(offset: -1)));
        }

        if (Match(type: TokenType.False))
        {
            return new LiteralExpression(Value: false,
                LiteralType: TokenType.False,
                Location: GetLocation(token: PeekToken(offset: -1)));
        }

        if (Match(type: TokenType.None))
        {
            return new LiteralExpression(Value: null,
                LiteralType: TokenType.None,
                Location: GetLocation(token: PeekToken(offset: -1)));
        }

        // Numeric literals
        if (Match(TokenType.Integer,
                TokenType.Decimal,
                TokenType.S8Literal,
                TokenType.S16Literal,
                TokenType.S32Literal,
                TokenType.S64Literal,
                TokenType.U8Literal,
                TokenType.U16Literal,
                TokenType.U32Literal,
                TokenType.U64Literal,
                TokenType.F16Literal,
                TokenType.F32Literal,
                TokenType.F64Literal))
        {
            Token token = PeekToken(offset: -1);
            return new LiteralExpression(Value: ParseNumericLiteral(token: token),
                LiteralType: token.Type,
                Location: GetLocation(token: token));
        }

        // String literals
        if (Match(TokenType.TextLiteral,
                TokenType.FormattedText,
                TokenType.RawText,
                TokenType.RawFormattedText,
                TokenType.Text8Literal,
                TokenType.Text8RawText,
                TokenType.Text8FormattedText,
                TokenType.Text8RawFormattedText,
                TokenType.Text16Literal,
                TokenType.Text16RawText,
                TokenType.Text16FormattedText,
                TokenType.Text16RawFormattedText))
        {
            Token token = PeekToken(offset: -1);
            return new LiteralExpression(Value: token.Text,
                LiteralType: token.Type,
                Location: GetLocation(token: token));
        }

        // Check for type conversion function call: i32!(expr)
        if ((IsNumericTypeToken(type: PeekToken()
               .Type) || PeekToken()
               .Type == TokenType.Identifier && IsTypeNameIdentifier(tokenText: PeekToken()
               .Text)) && PeekToken(offset: 1)
               .Type == TokenType.Bang)
        {
            Token typeToken = Advance();
            string typeName = IsNumericTypeToken(type: typeToken.Type)
                ? GetTypeNameFromToken(type: typeToken.Type)
                : typeToken.Text;
            Consume(type: TokenType.Bang, errorMessage: "Expected '!' after type name");
            Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after type conversion");
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return new TypeConversionExpression(TargetType: typeName,
                Expression: expr,
                IsMethodStyle: false,
                Location: GetLocation(token: typeToken));
        }

        // Identifiers
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            Token token = PeekToken(offset: -1);
            return new IdentifierExpression(Name: token.Text, Location: GetLocation(token: token));
        }

        // Type names as identifiers (when not followed by !)
        if (IsNumericTypeToken(type: PeekToken()
               .Type))
        {
            Token token = Advance();
            string typeName = GetTypeNameFromToken(type: token.Type);
            return new IdentifierExpression(Name: typeName, Location: GetLocation(token: token));
        }

        // Parenthesized expression
        if (Match(type: TokenType.LeftParen))
        {
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
        }

        // Lambda expression
        if (Check(type: TokenType.Routine))
        {
            return ParseLambdaExpression();
        }

        throw new ParseException(message: $"Unexpected token in expression: {CurrentToken.Type}");
    }

    /// <summary>
    /// Parse argument list for function calls
    /// </summary>
    protected List<Expression> ParseArgumentList()
    {
        var args = new List<Expression>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                args.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        return args;
    }

    /// <summary>
    /// Check if next tokens form a new expression (to disambiguate from function call)
    /// </summary>
    protected bool IsNewExpressionAhead()
    {
        // This would check for patterns like "new Type(" vs regular function calls
        return false; // Simplified for now
    }

    /// <summary>
    /// Parse lambda expression
    /// </summary>
    protected virtual Expression ParseLambdaExpression()
    {
        // To be implemented by derived parsers
        throw new NotImplementedException(message: "Lambda expressions not yet implemented");
    }

    /// <summary>
    /// Try to parse a comparison chain starting from the given left operand
    /// Returns null if not a valid chain
    /// </summary>
    protected ChainedComparisonExpression? TryParseComparisonChain(Expression left)
    {
        if (!IsComparisonOperator(type: CurrentToken.Type))
        {
            return null;
        }

        var operands = new List<Expression> { left };
        var operators = new List<BinaryOperator>();

        // Parse the chain: left op1 middle op2 right op3 ...
        while (IsComparisonOperator(type: CurrentToken.Type))
        {
            Token opToken = Advance();
            BinaryOperator op = TokenToBinaryOperator(tokenType: opToken.Type);
            operators.Add(item: op);

            // Parse the next operand (right side of this comparison)
            Expression nextOperand =
                ParseExpression(
                    minPrecedence: Precedence.Shift); // Higher precedence than comparison
            operands.Add(item: nextOperand);

            // Check if we should continue the chain
            if (!IsComparisonOperator(type: CurrentToken.Type))
            {
                break;
            }
        }

        // Only create chained comparison if we have more than one operator
        if (operators.Count <= 1)
        {
            // Just a single comparison, return as regular binary expression
            return null;
        }

        // Validate chain direction
        if (!IsValidComparisonChain(operators: operators))
        {
            throw new ParseException(
                message: $"Invalid comparison chain: mixed ascending and descending operators");
        }

        return new ChainedComparisonExpression(Operands: operands,
            Operators: operators,
            Location: GetLocation());
    }

    /// <summary>
    /// Parse numeric literal value
    /// </summary>
    protected object ParseNumericLiteral(Token token)
    {
        string text = token.Text;

        // Parse based on token type to preserve type information
        return token.Type switch
        {
            TokenType.S8Literal => ParseTypedInteger<sbyte>(text: text, suffix: "s8"),
            TokenType.S16Literal => ParseTypedInteger<short>(text: text, suffix: "s16"),
            TokenType.S32Literal => ParseTypedInteger<int>(text: text, suffix: "s32"),
            TokenType.S64Literal => ParseTypedInteger<long>(text: text, suffix: "s64"),
            TokenType.S128Literal => ParseTypedInteger<Int128>(text: text, suffix: "s128"),
            TokenType.U8Literal => ParseTypedInteger<byte>(text: text, suffix: "u8"),
            TokenType.U16Literal => ParseTypedInteger<ushort>(text: text, suffix: "u16"),
            TokenType.U32Literal => ParseTypedInteger<uint>(text: text, suffix: "u32"),
            TokenType.U64Literal => ParseTypedInteger<ulong>(text: text, suffix: "u64"),
            TokenType.U128Literal => ParseTypedInteger<UInt128>(text: text, suffix: "u128"),
            TokenType.F16Literal => ParseTypedFloat<Half>(text: text, suffix: "f16"),
            TokenType.F32Literal => ParseTypedFloat<float>(text: text, suffix: "f32"),
            TokenType.F64Literal => ParseTypedFloat<double>(text: text, suffix: "f64"),
            TokenType.F128Literal => ParseTypedFloat<decimal>(text: text,
                suffix: "f128"), // Using decimal as approximation
            TokenType.Integer => BigInteger.Parse(value: text), // Variable-sized integer
            TokenType.Decimal => ParseBigDecimal(text: text), // Variable-sized decimal
            _ => text.Contains(value: '.')
                ? double.Parse(s: text)
                : BigInteger.Parse(value: text)
        };
    }

    private T ParseTypedInteger<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    private T ParseTypedFloat<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        if (typeof(T) == typeof(Half))
        {
            // Half is not directly supported, use float as approximation
            return (T)(object)(float)double.Parse(s: cleanText);
        }

        return (T)Convert.ChangeType(value: cleanText, conversionType: typeof(T));
    }

    private BigInteger ParseBigInteger(string text, string suffix)
    {
        // Remove the type suffix
        string cleanText = text.EndsWith(value: suffix)
            ? text.Substring(startIndex: 0, length: text.Length - suffix.Length)
            : text;
        return BigInteger.Parse(value: cleanText);
    }

    private decimal ParseBigDecimal(string text)
    {
        // For now, use C#'s decimal as a placeholder for arbitrary precision decimal
        // In a real implementation, this would use a proper BigDecimal library
        return decimal.Parse(s: text);
    }

    /// <summary>
    /// Parse operator precedence
    /// </summary>
    protected int GetPrecedence(TokenType tokenType)
    {
        return tokenType switch
        {
            // Assignment (lowest precedence)
            TokenType.Assign => 1,

            // Ternary
            TokenType.Question => 2,

            // Logical OR
            TokenType.Or => 3,

            // Logical AND
            TokenType.And => 4,

            // Equality
            TokenType.Equal or TokenType.NotEqual => 5,

            // Comparison
            TokenType.Less or TokenType.LessEqual or TokenType.Greater
                or TokenType.GreaterEqual => 6,

            // Bitwise OR
            TokenType.Pipe => 7,

            // Bitwise XOR
            TokenType.Caret => 8,

            // Bitwise AND
            TokenType.Ampersand => 9,

            // Bitwise Shift
            TokenType.LeftShift or TokenType.LeftShiftChecked or TokenType.RightShift
                or TokenType.LogicalLeftShift or TokenType.LogicalRightShift => 10,

            // Addition, Subtraction
            TokenType.Plus or TokenType.Minus or TokenType.PlusWrap or TokenType.PlusSaturate
                or TokenType.PlusUnchecked or TokenType.PlusChecked or TokenType.MinusWrap
                or TokenType.MinusSaturate or TokenType.MinusUnchecked
                or TokenType.MinusChecked => 11,

            // Multiplication, Division, Modulo
            TokenType.Star or TokenType.Slash or TokenType.Percent or TokenType.Divide
                or TokenType.MultiplyWrap or TokenType.MultiplySaturate
                or TokenType.MultiplyUnchecked or TokenType.MultiplyChecked or TokenType.DivideWrap
                or TokenType.DivideSaturate or TokenType.DivideUnchecked or TokenType.DivideChecked
                or TokenType.ModuloWrap or TokenType.ModuloSaturate or TokenType.ModuloUnchecked
                or TokenType.ModuloChecked => 12,

            // Power
            TokenType.Power or TokenType.PowerWrap or TokenType.PowerSaturate
                or TokenType.PowerUnchecked or TokenType.PowerChecked => 13,

            // Unary (handled separately)
            // Member access, calls, indexing (highest precedence, handled in primary)

            _ => 0
        };
    }

    /// <summary>
    /// Convert TokenType to BinaryOperator
    /// </summary>
    protected BinaryOperator TokenToBinaryOperator(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.Plus => BinaryOperator.Add,
            TokenType.Minus => BinaryOperator.Subtract,
            TokenType.Star => BinaryOperator.Multiply,
            TokenType.Slash => BinaryOperator.TrueDivide,
            TokenType.Divide => BinaryOperator.FloorDivide,
            TokenType.Percent => BinaryOperator.Modulo,
            TokenType.Power => BinaryOperator.Power,

            // Overflow variants
            TokenType.PlusWrap => BinaryOperator.AddWrap,
            TokenType.PlusSaturate => BinaryOperator.AddSaturate,
            TokenType.PlusChecked => BinaryOperator.AddChecked,
            TokenType.MinusWrap => BinaryOperator.SubtractWrap,
            TokenType.MinusSaturate => BinaryOperator.SubtractSaturate,
            TokenType.MinusChecked => BinaryOperator.SubtractChecked,
            TokenType.MultiplyWrap => BinaryOperator.MultiplyWrap,
            TokenType.MultiplySaturate => BinaryOperator.MultiplySaturate,
            TokenType.MultiplyChecked => BinaryOperator.MultiplyChecked,
            TokenType.ModuloChecked => BinaryOperator.ModuloChecked,
            TokenType.DivideChecked => BinaryOperator.FloorDivideChecked,
            TokenType.PowerWrap => BinaryOperator.PowerWrap,
            TokenType.PowerSaturate => BinaryOperator.PowerSaturate,
            TokenType.PowerChecked => BinaryOperator.PowerChecked,

            TokenType.Equal => BinaryOperator.Equal,
            TokenType.NotEqual => BinaryOperator.NotEqual,
            TokenType.Less => BinaryOperator.Less,
            TokenType.LessEqual => BinaryOperator.LessEqual,
            TokenType.Greater => BinaryOperator.Greater,
            TokenType.GreaterEqual => BinaryOperator.GreaterEqual,
            TokenType.And => BinaryOperator.And,
            TokenType.Or => BinaryOperator.Or,
            TokenType.Ampersand => BinaryOperator.BitwiseAnd,
            TokenType.Pipe => BinaryOperator.BitwiseOr,
            TokenType.Caret => BinaryOperator.BitwiseXor,
            TokenType.LeftShift => BinaryOperator.ArithmeticLeftShift,
            TokenType.LeftShiftChecked => BinaryOperator.ArithmeticLeftShiftChecked,
            TokenType.RightShift => BinaryOperator.ArithmeticRightShift,
            TokenType.LogicalLeftShift => BinaryOperator.LogicalLeftShift,
            TokenType.LogicalRightShift => BinaryOperator.LogicalRightShift,
            TokenType.Assign => BinaryOperator.Assign,
            TokenType.In => BinaryOperator.In,
            TokenType.NotIn => BinaryOperator.NotIn,
            TokenType.Is => BinaryOperator.Is,
            TokenType.IsNot => BinaryOperator.IsNot,
            TokenType.From => BinaryOperator.From,
            TokenType.NotFrom => BinaryOperator.NotFrom,
            TokenType.Follows => BinaryOperator.Follows,
            TokenType.NotFollows => BinaryOperator.NotFollows,
            TokenType.NoneCoalesce => BinaryOperator.NoneCoalesce,

            _ => throw new ParseException(message: $"Unknown binary operator: {tokenType}")
        };
    }

    /// <summary>
    /// Convert TokenType to UnaryOperator
    /// </summary>
    protected UnaryOperator TokenToUnaryOperator(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.Minus => UnaryOperator.Minus,
            TokenType.Bang => UnaryOperator.Not,
            TokenType.Not => UnaryOperator.Not,
            TokenType.Tilde => UnaryOperator.BitwiseNot,

            _ => throw new ParseException(message: $"Unknown unary operator: {tokenType}")
        };
    }

    /// <summary>
    /// Add a compile warning to the list
    /// </summary>
    protected void AddWarning(string message, Token token, string warningCode,
        WarningSeverity severity = WarningSeverity.Warning)
    {
        Warnings.Add(item: new CompileWarning(message: message,
            line: token.Line,
            column: token.Column,
            severity: severity,
            warningCode: warningCode));
    }

    /// <summary>
    /// Get all warnings collected during parsing
    /// </summary>
    public IReadOnlyList<CompileWarning> GetWarnings()
    {
        return Warnings.AsReadOnly();
    }

    /// <summary>
    /// Check for unnecessary semicolon (for RazorForge)
    /// </summary>
    protected void CheckUnnecessarySemicolon()
    {
        if (CurrentToken.Type == TokenType.Semicolon)
        {
            AddWarning(
                message:
                "Unnecessary semicolon detected. RazorForge uses expression-based syntax without statement terminators.",
                token: CurrentToken,
                warningCode: WarningCodes.UnnecessarySemicolon,
                severity: WarningSeverity.StyleViolation);
        }
    }

    /// <summary>
    /// Check for unnecessary closing brace (for Suflae)
    /// </summary>
    protected void CheckUnnecessaryBrace()
    {
        if (CurrentToken.Type == TokenType.RightBrace)
        {
            AddWarning(
                message:
                "Unnecessary closing brace detected. Suflae uses indentation-based scoping, not braces.",
                token: CurrentToken,
                warningCode: WarningCodes.UnnecessaryBraces,
                severity: WarningSeverity.StyleViolation);
        }
    }
}

/// <summary>
/// Exception thrown during parsing
/// </summary>
public class ParseException : Exception
{
    public ParseException(string message) : base(message: message)
    {
    }
    public ParseException(string message, Exception innerException) : base(message: message,
        innerException: innerException)
    {
    }
}
