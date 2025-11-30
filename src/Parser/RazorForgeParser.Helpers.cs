using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing helper methods (intrinsics, native calls, argument parsing, etc.).
/// </summary>
public partial class RazorForgeParser
{
    private IntrinsicCallExpression ParseIntrinsicCall(SourceLocation location)
    {
        // Expect: .operation<T>(args)
        // The @intrinsic token has already been consumed

        Consume(type: TokenType.Dot, errorMessage: "Expected '.' after '@intrinsic'");

        // Parse intrinsic operation name (can contain dots like "add.wrapping", "icmp.slt")
        string intrinsicName =
            ConsumeIdentifier(errorMessage: "Expected intrinsic operation name");

        // Handle dotted names like "add.wrapping" or "icmp.slt"
        while (Match(type: TokenType.Dot))
        {
            intrinsicName +=
                "." + ConsumeIdentifier(errorMessage: "Expected identifier after '.'");
        }

        // Parse optional type arguments: <T> or <T, U>
        var typeArgs = new List<string>();
        if (Match(type: TokenType.Less))
        {
            do
            {
                // For now, parse type arguments as simple identifiers
                // (more complex type expressions could be supported later)
                if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
                {
                    typeArgs.Add(item: PeekToken(offset: -1)
                       .Text);
                }
                else
                {
                    throw new ParseException(message: "Expected type argument");
                }
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after type arguments");
        }

        // Parse arguments: (arg1, arg2, ...)
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after intrinsic name");

        var args = new List<Expression>();
        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                args.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after intrinsic arguments");

        return new IntrinsicCallExpression(IntrinsicName: intrinsicName,
            TypeArguments: typeArgs,
            Arguments: args,
            Location: location);
    }

    private NativeCallExpression ParseNativeCall(SourceLocation location)
    {
        // Expect: .function_name(args)
        // The @native token has already been consumed

        Consume(type: TokenType.Dot, errorMessage: "Expected '.' after '@native'");

        // Parse function name (can contain underscores like rf_bigint_new)
        string functionName = ConsumeIdentifier(errorMessage: "Expected native function name");

        // Parse arguments: (arg1, arg2, ...)
        Consume(type: TokenType.LeftParen,
            errorMessage: "Expected '(' after native function name");

        var args = new List<Expression>();
        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                args.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after native function arguments");

        return new NativeCallExpression(FunctionName: functionName,
            Arguments: args,
            Location: location);
    }

    private void ConsumeStatementTerminator()
    {
        // Check for unnecessary semicolon and issue warning
        CheckUnnecessarySemicolon();

        // Accept newline as statement terminator
        if (!Check(type: TokenType.RightBrace) && !Check(type: TokenType.Else) && !IsAtEnd)
        {
            Match(type: TokenType.Newline);
        }
    }

    private string ConsumeIdentifier(string errorMessage)
    {
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return PeekToken(offset: -1)
               .Text;
        }

        // Allow 'me' (Self token) as a valid identifier for method parameters
        if (Match(type: TokenType.Self))
        {
            return "me";
        }

        Token current = CurrentToken;
        throw new ParseException(
            message:
            $"{errorMessage}. Expected Identifier or TypeIdentifier, got {current.Type}.");
    }

    private SliceConstructorExpression ParseSliceConstructor()
    {
        SourceLocation location = GetLocation();
        string typeName = ConsumeIdentifier(errorMessage: "Expected slice type name");

        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after slice type");
        Expression sizeExpr = ParseExpression();
        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after slice size");

        return new SliceConstructorExpression(SliceType: typeName,
            SizeExpression: sizeExpr,
            Location: location);
    }

    /// <summary>
    /// Parses a single argument which may be named (name: value) or positional (value).
    /// </summary>
    private Expression ParseArgument()
    {
        SourceLocation location = GetLocation();

        // Check if this is a named argument: identifier followed by colon
        // We need to look ahead to distinguish between named argument and ternary/type annotation
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            // Could be named argument, check that what follows isn't a type (for ternary with typed vars)
            // Named arguments: name: expression
            int savedPos = Position;
            string potentialName = CurrentToken.Text;
            Advance(); // consume identifier
            Advance(); // consume colon

            // Parse the value expression
            Expression value = ParseExpression();

            return new NamedArgumentExpression(Name: potentialName,
                Value: value,
                Location: location);
        }

        // Regular positional argument
        return ParseExpression();
    }

    /// <summary>
    /// Parses a comma-separated list of arguments (named or positional).
    /// Called after '(' has been consumed.
    /// </summary>
    private List<Expression> ParseArgumentList()
    {
        var args = new List<Expression>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                args.Add(item: ParseArgument());
            } while (Match(type: TokenType.Comma));
        }

        return args;
    }

    /// <summary>
    /// Parses field initializers for struct literals: { field1: value1, field2: value2 }
    /// Called after '{' has been consumed.
    /// </summary>
    private List<(string Name, Expression Value)> ParseStructLiteralFields()
    {
        var fields = new List<(string Name, Expression Value)>();

        if (!Check(type: TokenType.RightBrace))
        {
            do
            {
                // Parse field name
                string fieldName =
                    ConsumeIdentifier(errorMessage: "Expected field name in struct literal");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name");

                // Parse field value
                Expression value = ParseExpression();
                fields.Add(item: (fieldName, value));
            } while (Match(type: TokenType.Comma));
        }

        return fields;
    }

    /// <summary>
    /// Consumes an identifier or a keyword that can be used as a method/function name.
    /// Keywords like 'where', 'none', etc. are allowed as method names.
    /// </summary>
    private string ConsumeMethodName(string errorMessage)
    {
        // Allow identifiers
        if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
        {
            string name = CurrentToken.Text;
            Advance();
            return name;
        }

        // Allow certain keywords as method names
        if (Check(type: TokenType.Where) || Check(type: TokenType.None))
        {
            string name = CurrentToken.Text;
            Advance();
            return name;
        }

        throw new ParseException(message: errorMessage);
    }
}
