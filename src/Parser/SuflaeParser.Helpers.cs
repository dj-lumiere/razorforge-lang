using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing helper methods (intrinsics, native calls, indentation handling, etc.).
/// </summary>
public partial class SuflaeParser
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
                args.Add(item: ParseArgument());
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
                args.Add(item: ParseArgument());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen,
            errorMessage: "Expected ')' after native function arguments");

        return new NativeCallExpression(FunctionName: functionName,
            Arguments: args,
            Location: location);
    }

    /// <summary>
    /// Parses a single argument (named or positional).
    /// Named arguments have the form: name: expression
    /// </summary>
    private Expression ParseArgument()
    {
        SourceLocation location = GetLocation();

        // Check for named argument: identifier followed by colon
        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
               .Type == TokenType.Colon)
        {
            string argName = CurrentToken.Text;
            Advance(); // consume identifier
            Advance(); // consume colon

            // Parse the value expression
            Expression value = ParseExpression();

            return new NamedArgumentExpression(Name: argName, Value: value, Location: location);
        }

        // Regular positional argument
        return ParseExpression();
    }

    private void ConsumeStatementTerminator()
    {
        // Check for unnecessary semicolons
        CheckUnnecessarySemicolon();

        // Check for unnecessary closing braces
        CheckUnnecessaryBrace();

        // Accept newline as statement terminator
        if (!Check(type: TokenType.Dedent) && !Check(type: TokenType.Else) && !IsAtEnd)
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

        Token current = CurrentToken;
        throw new ParseException(
            message: $"{errorMessage}. Expected Identifier, got {current.Type}.");
    }

    /// <summary>
    /// Process an INDENT token by pushing a new indentation level
    /// </summary>
    private void ProcessIndentToken()
    {
        if (!Match(type: TokenType.Indent))
        {
            throw new ParseException(message: "Expected INDENT token");
        }

        _currentIndentationLevel++;
        _indentationStack.Push(item: _currentIndentationLevel);
    }

    /// <summary>
    /// Process one or more DEDENT tokens by popping indentation levels
    /// </summary>
    private void ProcessDedentTokens()
    {
        // Check for unnecessary closing braces before processing dedents
        CheckUnnecessaryBrace();

        while (Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            Advance(); // Consume the DEDENT token

            if (_indentationStack.Count > 1) // Keep base level
            {
                _indentationStack.Pop();
                _currentIndentationLevel = _indentationStack.Peek();
            }
            else
            {
                throw new ParseException(message: "Unexpected dedent - no matching indent");
            }
        }
    }

    /// <summary>
    /// Check if we're at a valid indentation level for statements
    /// </summary>
    private bool IsAtValidIndentationLevel()
    {
        return _indentationStack.Count > 0;
    }

    /// <summary>
    /// Get current indentation depth for debugging
    /// </summary>
    private int GetIndentationDepth()
    {
        return _indentationStack.Count - 1; // Subtract 1 for base level
    }
}
