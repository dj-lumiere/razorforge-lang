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

    protected BaseParser(List<Token> tokens)
    {
        Tokens = tokens;
    }

    /// <summary>
    /// Parse the tokens into an AST
    /// </summary>
    public abstract Compilers.Shared.AST.Program Parse();

    // Token management
    protected Token CurrentToken => Position < Tokens.Count
        ? Tokens[Position]
        : Tokens[^1];

    protected Token PeekToken(int offset = 1) =>
        Position + offset < Tokens.Count
            ? Tokens[Position + offset]
            : Tokens[^1];

    protected bool IsAtEnd => Position >= Tokens.Count || CurrentToken.Type == TokenType.Eof;

    /// <summary>
    /// Advance to the next token and return the current one
    /// </summary>
    protected Token Advance()
    {
        var token = CurrentToken;
        if (!IsAtEnd) Position++;
        return token;
    }

    /// <summary>
    /// Check if current token matches the expected type
    /// </summary>
    protected bool Check(TokenType type) => !IsAtEnd && CurrentToken.Type == type;

    /// <summary>
    /// Check if current token matches any of the expected types
    /// </summary>
    protected bool Check(params TokenType[] types) => types.Any(Check);

    /// <summary>
    /// Consume token if it matches expected type, otherwise throw error
    /// </summary>
    protected Token Consume(TokenType type, string errorMessage)
    {
        if (Check(type)) return Advance();

        var current = CurrentToken;
        throw new ParseException(
            $"{errorMessage} at line {current.Line}, column {current.Column}. " +
            $"Expected {type}, got {current.Type}.");
    }

    /// <summary>
    /// Consume token if it matches expected type, return whether successful
    /// </summary>
    protected bool Match(TokenType type)
    {
        if (Check(type))
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
        foreach (var type in types)
        {
            if (Check(type))
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
        Range = 3, // a to b step c
        LogicalOr = 4, // or
        LogicalAnd = 5, // and
        LogicalNot = 6, // not

        Comparison =
            7, // in, is, from, follows, <, <=, >, >=, ==, !=, notin, isnot, notfrom, notfollows
        BitwiseOr = 8, // |
        BitwiseXor = 9, // ^
        BitwiseAnd = 10, // &
        Shift = 11, // <<, >>
        Additive = 12, // +, - and variants
        Multiplicative = 13, // *, /, //, % and variants
        Unary = 14, // +x, -x, ~x
        Power = 15, // **, **%, **^, **?, **!
        Postfix = 16, // x[index], x.member, x()
        Primary = 17 // literals, identifiers, ()
    }

    /// <summary>
    /// Get precedence for binary operators
    /// </summary>
    protected Precedence GetBinaryPrecedence(TokenType type)
    {
        return type switch
        {
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
            TokenType.LeftShift or TokenType.RightShift => Precedence.Shift,

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
        if (operators.Count <= 1) return true;

        var directions = operators.Select(op => GetComparisonDirection(BinaryOperatorToToken(op)))
                                  .ToList();

        // All equality is valid
        if (directions.All(d => d == 0)) return true;

        // Check for consistent direction (all ascending, all descending, or mixed with equality only)
        var nonZeroDirections = directions.Where(d => d != 0)
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
            if (PeekToken(-1)
                   .Type == TokenType.Newline) return;

            switch (CurrentToken.Type)
            {
                case TokenType.Entity:
                case TokenType.Record:
                case TokenType.Choice:
                case TokenType.Chimera:
                case TokenType.Variant:
                case TokenType.Mutant:
                case TokenType.Protocol:
                case TokenType.recipe:
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
    protected SourceLocation GetLocation() => GetLocation(CurrentToken);
    protected SourceLocation GetLocation(Token token) =>
        new(token.Line, token.Column, token.Position);

    /// <summary>
    /// Parse expression using Pratt parser with precedence climbing
    /// </summary>
    protected Expression ParseExpression(Precedence minPrecedence = Precedence.None)
    {
        // Handle 'if then else' conditional expressions at the lowest precedence
        if (minPrecedence <= Precedence.Conditional && Match(TokenType.If))
        {
            var condition = ParseExpression(Precedence.Range);
            Consume(TokenType.Then, "Expected 'then' in conditional expression");
            var thenExpr = ParseExpression(Precedence.Range);
            Consume(TokenType.Else, "Expected 'else' in conditional expression");
            // Parse else expression at higher precedence to prevent nesting
            var elseExpr = ParseExpression(Precedence.Range);
            return new ConditionalExpression(condition, thenExpr, elseExpr, GetLocation());
        }

        // Parse prefix expression (unary operators, literals, etc.)
        var left = ParsePrefixExpression();

        // Handle range expressions: a to b [step c]
        if (minPrecedence <= Precedence.Range && Match(TokenType.To))
        {
            var end = ParseExpression(Precedence.LogicalOr);
            Expression? step = null;

            if (Match(TokenType.Step))
            {
                step = ParseExpression(Precedence.LogicalOr);
            }

            left = new RangeExpression(left, end, step, GetLocation());
        }

        // Check for comparison chaining at comparison precedence level
        if (minPrecedence <= Precedence.Comparison && IsComparisonOperator(CurrentToken.Type))
        {
            var chainResult = TryParseComparisonChain(left);
            if (chainResult != null)
            {
                left = chainResult;
            }
        }

        // Parse infix expressions (binary operators) with precedence climbing
        while (!IsAtEnd)
        {
            var precedence = GetBinaryPrecedence(CurrentToken.Type);
            if (precedence <= minPrecedence) break;

            // Skip comparison operators if we already handled chaining
            if (precedence == Precedence.Comparison && left is ChainedComparisonExpression)
                break;

            // Skip range operators - they're handled above
            if (CurrentToken.Type == TokenType.To || CurrentToken.Type == TokenType.Step)
                break;

            // Handle right-associative operators
            if (IsRightAssociative(CurrentToken.Type))
            {
                left = ParseInfixExpression(left, (Precedence)((int)precedence - 1));
            }
            else
            {
                left = ParseInfixExpression(left, precedence);
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
        if (Match(TokenType.Plus, TokenType.Minus, TokenType.Tilde, TokenType.Not, TokenType.Bang))
        {
            var op = PeekToken(-1);
            var operand = ParseExpression(Precedence.Unary);
            return new UnaryExpression(TokenToUnaryOperator(op.Type), operand, GetLocation(op));
        }

        // Parse postfix expression (which includes primary)
        return ParsePostfixExpression();
    }

    /// <summary>
    /// Parse postfix expressions (member access, indexing, function calls)
    /// </summary>
    protected Expression ParsePostfixExpression()
    {
        var expr = ParsePrimaryExpression();

        while (!IsAtEnd)
        {
            if (Match(TokenType.LeftBracket))
            {
                // Array/index access: x[index]
                var index = ParseExpression();
                Consume(TokenType.RightBracket, "Expected ']' after index");
                expr = new IndexExpression(expr, index, GetLocation());
            }
            else if (Match(TokenType.Dot))
            {
                // Check if this is a type conversion: x.i32!()
                if (IsTypeConversion())
                {
                    var typeToken = Advance();
                    var typeName = IsNumericTypeToken(typeToken.Type) ? 
                        GetTypeNameFromToken(typeToken.Type) : typeToken.Text;
                    Consume(TokenType.Bang, "Expected '!' after type name");
                    Consume(TokenType.LeftParen, "Expected '(' after type conversion");
                    Consume(TokenType.RightParen, "Expected ')' after type conversion");
                    expr = new TypeConversionExpression(typeName, expr, true, GetLocation());
                }
                else
                {
                    // Regular member access: x.member
                    var member = Consume(TokenType.Identifier, "Expected member name after '.'");
                    expr = new MemberExpression(expr, member.Text, GetLocation());
                }
            }
            else if (Check(TokenType.LeftParen) && !IsNewExpressionAhead())
            {
                // Function call: x()
                Match(TokenType.LeftParen);
                var args = ParseArgumentList();
                Consume(TokenType.RightParen, "Expected ')' after arguments");
                expr = new CallExpression(expr, args, GetLocation());
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
        var token = PeekToken();
        return (IsTypeIdentifier(token.Type) || 
                (token.Type == TokenType.Identifier && IsTypeNameIdentifier(token.Text))) && 
               PeekToken(1).Type == TokenType.Bang &&
               PeekToken(2).Type == TokenType.LeftParen;
    }

    /// <summary>
    /// Check if a token type represents a type identifier
    /// </summary>
    protected bool IsTypeIdentifier(TokenType type)
    {
        return type == TokenType.Identifier || 
               type == TokenType.TypeIdentifier ||
               IsNumericTypeToken(type);
    }
    
    /// <summary>
    /// Check if a token represents a numeric type name (for identifiers)
    /// </summary>
    protected bool IsTypeNameIdentifier(string tokenText)
    {
        return tokenText switch
        {
            "i8" or "i16" or "i32" or "i64" or
            "u8" or "u16" or "u32" or "u64" or
            "f16" or "f32" or "f64" or "f128" => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if a token represents a numeric type (i8, i32, f32, etc.)
    /// </summary>
    protected bool IsNumericTypeToken(TokenType type)
    {
        return type is TokenType.S8Literal or TokenType.S16Literal or TokenType.S32Literal or
               TokenType.S64Literal or TokenType.U8Literal or TokenType.U16Literal or
               TokenType.U32Literal or TokenType.U64Literal or TokenType.F16Literal or
               TokenType.F32Literal or TokenType.F64Literal;
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
            _ => throw new ArgumentException($"Invalid numeric type token: {type}")
        };
    }

    /// <summary>
    /// Parse infix expression (binary operators)
    /// </summary>
    protected Expression ParseInfixExpression(Expression left, Precedence minPrecedence)
    {
        var op = Advance();

        // Regular binary operators
        var right = ParseExpression(minPrecedence);
        return new BinaryExpression(left, TokenToBinaryOperator(op.Type), right, GetLocation(op));
    }

    /// <summary>
    /// Parse primary expressions (literals, identifiers, parenthesized expressions)
    /// </summary>
    protected Expression ParsePrimaryExpression()
    {
        // Boolean literals
        if (Match(TokenType.True))
            return new LiteralExpression(true, TokenType.True, GetLocation(PeekToken(-1)));
        if (Match(TokenType.False))
            return new LiteralExpression(false, TokenType.False, GetLocation(PeekToken(-1)));
        if (Match(TokenType.None))
            return new LiteralExpression(null, TokenType.None, GetLocation(PeekToken(-1)));

        // Numeric literals
        if (Match(TokenType.Integer, TokenType.Decimal, TokenType.S8Literal, TokenType.S16Literal,
                TokenType.S32Literal, TokenType.S64Literal, TokenType.U8Literal,
                TokenType.U16Literal, TokenType.U32Literal, TokenType.U64Literal,
                TokenType.F16Literal, TokenType.F32Literal, TokenType.F64Literal))
        {
            var token = PeekToken(-1);
            return new LiteralExpression(ParseNumericLiteral(token), token.Type,
                GetLocation(token));
        }

        // String literals
        if (Match(TokenType.TextLiteral, TokenType.FormattedText, TokenType.RawText, TokenType.RawFormattedText,
                TokenType.Text8Literal, TokenType.Text8RawText, TokenType.Text8FormattedText, TokenType.Text8RawFormattedText,
                TokenType.Text16Literal, TokenType.Text16RawText, TokenType.Text16FormattedText, TokenType.Text16RawFormattedText))
        {
            var token = PeekToken(-1);
            return new LiteralExpression(token.Text, token.Type, GetLocation(token));
        }

        // Check for type conversion function call: i32!(expr)
        if ((IsNumericTypeToken(PeekToken().Type) || 
             (PeekToken().Type == TokenType.Identifier && IsTypeNameIdentifier(PeekToken().Text))) && 
             PeekToken(1).Type == TokenType.Bang)
        {
            var typeToken = Advance();
            var typeName = IsNumericTypeToken(typeToken.Type) ?
                GetTypeNameFromToken(typeToken.Type) : typeToken.Text;
            Consume(TokenType.Bang, "Expected '!' after type name");
            Consume(TokenType.LeftParen, "Expected '(' after type conversion");
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return new TypeConversionExpression(typeName, expr, false, GetLocation(typeToken));
        }

        // Identifiers
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            var token = PeekToken(-1);
            return new IdentifierExpression(token.Text, GetLocation(token));
        }

        // Type names as identifiers (when not followed by !)
        if (IsNumericTypeToken(PeekToken().Type))
        {
            var token = Advance();
            var typeName = GetTypeNameFromToken(token.Type);
            return new IdentifierExpression(typeName, GetLocation(token));
        }

        // Parenthesized expression
        if (Match(TokenType.LeftParen))
        {
            var expr = ParseExpression();
            Consume(TokenType.RightParen, "Expected ')' after expression");
            return expr;
        }

        // Lambda expression
        if (Check(TokenType.recipe))
        {
            return ParseLambdaExpression();
        }

        throw new ParseException($"Unexpected token in expression: {CurrentToken.Type}");
    }

    /// <summary>
    /// Parse argument list for function calls
    /// </summary>
    protected List<Expression> ParseArgumentList()
    {
        var args = new List<Expression>();

        if (!Check(TokenType.RightParen))
        {
            do
            {
                args.Add(ParseExpression());
            } while (Match(TokenType.Comma));
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
        throw new NotImplementedException("Lambda expressions not yet implemented");
    }

    /// <summary>
    /// Try to parse a comparison chain starting from the given left operand
    /// Returns null if not a valid chain
    /// </summary>
    protected ChainedComparisonExpression? TryParseComparisonChain(Expression left)
    {
        if (!IsComparisonOperator(CurrentToken.Type))
            return null;

        var operands = new List<Expression> { left };
        var operators = new List<BinaryOperator>();

        // Parse the chain: left op1 middle op2 right op3 ...
        while (IsComparisonOperator(CurrentToken.Type))
        {
            var opToken = Advance();
            var op = TokenToBinaryOperator(opToken.Type);
            operators.Add(op);

            // Parse the next operand (right side of this comparison)
            var nextOperand =
                ParseExpression(Precedence.Shift); // Higher precedence than comparison
            operands.Add(nextOperand);

            // Check if we should continue the chain
            if (!IsComparisonOperator(CurrentToken.Type))
                break;
        }

        // Only create chained comparison if we have more than one operator
        if (operators.Count <= 1)
        {
            // Just a single comparison, return as regular binary expression
            return null;
        }

        // Validate chain direction
        if (!IsValidComparisonChain(operators))
        {
            throw new ParseException(
                $"Invalid comparison chain: mixed ascending and descending operators at line {CurrentToken.Line}");
        }

        return new ChainedComparisonExpression(operands, operators, GetLocation());
    }

    /// <summary>
    /// Parse numeric literal value
    /// </summary>
    protected object ParseNumericLiteral(Token token)
    {
        var text = token.Text;
        
        // Parse based on token type to preserve type information
        return token.Type switch
        {
            TokenType.S8Literal => ParseTypedInteger<sbyte>(text, "s8"),
            TokenType.S16Literal => ParseTypedInteger<short>(text, "s16"),
            TokenType.S32Literal => ParseTypedInteger<int>(text, "s32"),
            TokenType.S64Literal => ParseTypedInteger<long>(text, "s64"),
            TokenType.S128Literal => ParseTypedInteger<Int128>(text, "s128"),
            TokenType.U8Literal => ParseTypedInteger<byte>(text, "u8"),
            TokenType.U16Literal => ParseTypedInteger<ushort>(text, "u16"),
            TokenType.U32Literal => ParseTypedInteger<uint>(text, "u32"),
            TokenType.U64Literal => ParseTypedInteger<ulong>(text, "u64"),
            TokenType.U128Literal => ParseTypedInteger<UInt128>(text, "u128"),
            TokenType.F16Literal => ParseTypedFloat<Half>(text, "f16"),
            TokenType.F32Literal => ParseTypedFloat<float>(text, "f32"),
            TokenType.F64Literal => ParseTypedFloat<double>(text, "f64"),
            TokenType.F128Literal => ParseTypedFloat<decimal>(text, "f128"), // Using decimal as approximation
            TokenType.Integer => BigInteger.Parse(text), // Variable-sized integer
            TokenType.Decimal => ParseBigDecimal(text), // Variable-sized decimal
            _ => text.Contains('.') ? double.Parse(text) : BigInteger.Parse(text)
        };
    }
    
    private T ParseTypedInteger<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        var cleanText = text.EndsWith(suffix) ? text.Substring(0, text.Length - suffix.Length) : text;
        return (T)Convert.ChangeType(cleanText, typeof(T));
    }
    
    private T ParseTypedFloat<T>(string text, string suffix) where T : struct
    {
        // Remove the type suffix
        var cleanText = text.EndsWith(suffix) ? text.Substring(0, text.Length - suffix.Length) : text;
        if (typeof(T) == typeof(Half))
        {
            // Half is not directly supported, use float as approximation
            return (T)(object)(float)double.Parse(cleanText);
        }
        return (T)Convert.ChangeType(cleanText, typeof(T));
    }
    
    private BigInteger ParseBigInteger(string text, string suffix)
    {
        // Remove the type suffix
        var cleanText = text.EndsWith(suffix) ? text.Substring(0, text.Length - suffix.Length) : text;
        return BigInteger.Parse(cleanText);
    }

    private decimal ParseBigDecimal(string text)
    {
        // For now, use C#'s decimal as a placeholder for arbitrary precision decimal
        // In a real implementation, this would use a proper BigDecimal library
        return decimal.Parse(text);
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
            TokenType.LeftShift or TokenType.RightShift => 10,

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
            TokenType.Divide => BinaryOperator.Divide,
            TokenType.Percent => BinaryOperator.Modulo,
            TokenType.Power => BinaryOperator.Power,

            // Overflow variants
            TokenType.PlusWrap => BinaryOperator.AddWrap,
            TokenType.PlusSaturate => BinaryOperator.AddSaturate,
            TokenType.PlusUnchecked => BinaryOperator.AddUnchecked,
            TokenType.PlusChecked => BinaryOperator.AddChecked,
            TokenType.MinusWrap => BinaryOperator.SubtractWrap,
            TokenType.MinusSaturate => BinaryOperator.SubtractSaturate,
            TokenType.MinusUnchecked => BinaryOperator.SubtractUnchecked,
            TokenType.MinusChecked => BinaryOperator.SubtractChecked,
            TokenType.MultiplyWrap => BinaryOperator.MultiplyWrap,
            TokenType.MultiplySaturate => BinaryOperator.MultiplySaturate,
            TokenType.MultiplyUnchecked => BinaryOperator.MultiplyUnchecked,
            TokenType.MultiplyChecked => BinaryOperator.MultiplyChecked,
            TokenType.ModuloWrap => BinaryOperator.ModuloWrap,
            TokenType.ModuloSaturate => BinaryOperator.ModuloSaturate,
            TokenType.ModuloUnchecked => BinaryOperator.ModuloUnchecked,
            TokenType.ModuloChecked => BinaryOperator.ModuloChecked,
            TokenType.DivideWrap => BinaryOperator.DivideWrap,
            TokenType.DivideSaturate => BinaryOperator.DivideSaturate,
            TokenType.DivideChecked => BinaryOperator.DivideChecked,
            TokenType.DivideUnchecked => BinaryOperator.DivideUnchecked,
            TokenType.PowerWrap => BinaryOperator.PowerWrap,
            TokenType.PowerSaturate => BinaryOperator.PowerSaturate,
            TokenType.PowerUnchecked => BinaryOperator.PowerUnchecked,
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
            TokenType.LeftShift => BinaryOperator.LeftShift,
            TokenType.RightShift => BinaryOperator.RightShift,
            TokenType.Assign => BinaryOperator.Assign,
            TokenType.In => BinaryOperator.In,
            TokenType.NotIn => BinaryOperator.NotIn,
            TokenType.Is => BinaryOperator.Is,
            TokenType.IsNot => BinaryOperator.IsNot,
            TokenType.From => BinaryOperator.From,
            TokenType.NotFrom => BinaryOperator.NotFrom,
            TokenType.Follows => BinaryOperator.Follows,
            TokenType.NotFollows => BinaryOperator.NotFollows,

            _ => throw new ParseException($"Unknown binary operator: {tokenType}")
        };
    }

    /// <summary>
    /// Convert TokenType to UnaryOperator
    /// </summary>
    protected UnaryOperator TokenToUnaryOperator(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.Plus => UnaryOperator.Plus,
            TokenType.Minus => UnaryOperator.Minus,
            TokenType.Bang => UnaryOperator.Not,
            TokenType.Not => UnaryOperator.Not,
            TokenType.Tilde => UnaryOperator.BitwiseNot,

            _ => throw new ParseException($"Unknown unary operator: {tokenType}")
        };
    }

    /// <summary>
    /// Add a compile warning to the list
    /// </summary>
    protected void AddWarning(string message, Token token, string warningCode, WarningSeverity severity = WarningSeverity.Warning)
    {
        Warnings.Add(new CompileWarning(message, token.Line, token.Column, severity, warningCode));
    }

    /// <summary>
    /// Get all warnings collected during parsing
    /// </summary>
    public IReadOnlyList<CompileWarning> GetWarnings() => Warnings.AsReadOnly();

    /// <summary>
    /// Check for unnecessary semicolon (for RazorForge)
    /// </summary>
    protected void CheckUnnecessarySemicolon()
    {
        if (CurrentToken.Type == TokenType.Semicolon)
        {
            AddWarning(
                "Unnecessary semicolon detected. RazorForge uses expression-based syntax without statement terminators.",
                CurrentToken,
                WarningCodes.UnnecessarySemicolon,
                WarningSeverity.StyleViolation
            );
        }
    }

    /// <summary>
    /// Check for unnecessary closing brace (for Cake)
    /// </summary>
    protected void CheckUnnecessaryBrace()
    {
        if (CurrentToken.Type == TokenType.RightBrace)
        {
            AddWarning(
                "Unnecessary closing brace detected. Cake uses indentation-based scoping, not braces.",
                CurrentToken,
                WarningCodes.UnnecessaryBraces,
                WarningSeverity.StyleViolation
            );
        }
    }
}

/// <summary>
/// Exception thrown during parsing
/// </summary>
public class ParseException : Exception
{
    public ParseException(string message) : base(message)
    {
    }
    public ParseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}