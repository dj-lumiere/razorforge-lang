using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing lambda expression and collection literal parsing.
/// </summary>
public partial class RazorForgeParser
{
    private LambdaExpression ParseLambdaExpression(SourceLocation location)
    {
        // Parse parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'routine' in lambda");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                TypeExpression? paramType = null;
                Expression? defaultValue = null;

                if (Match(type: TokenType.Colon))
                {
                    paramType = ParseType();
                }

                if (Match(type: TokenType.Assign))
                {
                    defaultValue = ParseExpression();
                }

                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: defaultValue,
                    Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after lambda parameters");

        // Body - for now just parse expression (block lambdas would need special handling)
        Expression body = ParseExpression();

        return new LambdaExpression(Parameters: parameters, Body: body, Location: location);
    }

    /// <summary>
    /// Parse arrow lambda with single unparenthesized parameter: x => expr
    /// </summary>
    private LambdaExpression ParseArrowLambdaExpression(SourceLocation location)
    {
        // Single parameter without parentheses: x => expr
        string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
        Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' in lambda expression");

        var parameters = new List<Parameter>
        {
            new(Name: paramName,
                Type: null,
                DefaultValue: null,
                Location: location)
        };

        Expression body = ParseExpression();
        return new LambdaExpression(Parameters: parameters, Body: body, Location: location);
    }

    /// <summary>
    /// Check if we're inside parenthesized lambda parameters.
    /// Called after consuming '(' - scans ahead to see if we have: identifier [, identifier]* ) =>
    /// </summary>
    private bool IsArrowLambdaParameters()
    {
        int savedPosition = Position;

        try
        {
            // Empty params case: () =>
            if (Check(type: TokenType.RightParen))
            {
                Advance(); // consume )
                bool result = Check(type: TokenType.FatArrow);
                Position = savedPosition;
                return result;
            }

            // Look for pattern: identifier [: type]? [, identifier [: type]?]* ) =>
            while (true)
            {
                // Must start with identifier
                if (!Check(type: TokenType.Identifier))
                {
                    Position = savedPosition;
                    return false;
                }

                Advance(); // consume identifier

                // Optional type annotation
                if (Check(type: TokenType.Colon))
                {
                    Advance(); // consume :
                    // Skip the type (simplified - just skip until comma or rparen)
                    int depth = 0;
                    while (!IsAtEnd)
                    {
                        if (Check(type: TokenType.Less))
                        {
                            depth++;
                        }
                        else if (Check(type: TokenType.Greater))
                        {
                            depth--;
                        }
                        else if (depth == 0 && (Check(type: TokenType.Comma) ||
                                                Check(type: TokenType.RightParen)))
                        {
                            break;
                        }

                        Advance();
                    }
                }

                // Check for comma (more params) or end
                if (Check(type: TokenType.Comma))
                {
                    Advance(); // consume comma, continue loop
                }
                else if (Check(type: TokenType.RightParen))
                {
                    Advance(); // consume )
                    bool result = Check(type: TokenType.FatArrow);
                    Position = savedPosition;
                    return result;
                }
                else
                {
                    // Not a valid lambda parameter list
                    Position = savedPosition;
                    return false;
                }
            }
        }
        catch
        {
            Position = savedPosition;
            return false;
        }
    }

    /// <summary>
    /// Parse arrow lambda with parenthesized parameters: (x) => expr or (x, y) => expr
    /// Called after '(' has been consumed and IsArrowLambdaParameters() returned true.
    /// </summary>
    private LambdaExpression ParseParenthesizedArrowLambda(SourceLocation location)
    {
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                string paramName =
                    ConsumeIdentifier(errorMessage: "Expected parameter name in lambda");
                TypeExpression? paramType = null;

                if (Match(type: TokenType.Colon))
                {
                    paramType = ParseType();
                }

                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: null,
                    Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after lambda parameters");
        Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after lambda parameters");

        Expression body = ParseExpression();
        return new LambdaExpression(Parameters: parameters, Body: body, Location: location);
    }

    /// <summary>
    /// Parse list literal: [expr, expr, ...]
    /// The opening '[' has already been consumed.
    /// </summary>
    private ListLiteralExpression ParseListLiteral(SourceLocation location)
    {
        var elements = new List<Expression>();

        if (!Check(type: TokenType.RightBracket))
        {
            do
            {
                elements.Add(item: ParseExpression());
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after list elements");

        return new ListLiteralExpression(Elements: elements,
            ElementType: null,
            Location: location);
    }

    /// <summary>
    /// Parse set or dict literal: {expr, expr, ...} or {key: value, ...}
    /// The opening '{' has already been consumed.
    /// Disambiguation: If the first element contains ':', it's a dict; otherwise it's a set.
    /// Empty {} is treated as an empty set.
    /// </summary>
    private Expression ParseSetOrDictLiteral(SourceLocation location)
    {
        // Empty braces -> empty set
        if (Match(type: TokenType.RightBrace))
        {
            return new SetLiteralExpression(Elements: new List<Expression>(),
                ElementType: null,
                Location: location);
        }

        // Parse first element to determine if set or dict
        Expression firstExpr = ParseExpression();

        // If we see a colon, this is a dict literal
        if (Match(type: TokenType.Colon))
        {
            return ParseDictLiteralContinuation(firstKey: firstExpr, location: location);
        }

        // Otherwise it's a set literal
        return ParseSetLiteralContinuation(firstElement: firstExpr, location: location);
    }

    /// <summary>
    /// Continue parsing dict literal after first key and colon: {key: value, ...}
    /// </summary>
    private DictLiteralExpression ParseDictLiteralContinuation(Expression firstKey,
        SourceLocation location)
    {
        var pairs = new List<(Expression Key, Expression Value)>();

        // Parse value for first key
        Expression firstValue = ParseExpression();
        pairs.Add(item: (firstKey, firstValue));

        // Parse remaining key-value pairs
        while (Match(type: TokenType.Comma))
        {
            Expression key = ParseExpression();
            Consume(type: TokenType.Colon,
                errorMessage: "Expected ':' between dict key and value");
            Expression value = ParseExpression();
            pairs.Add(item: (key, value));
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after dict elements");

        return new DictLiteralExpression(Pairs: pairs,
            KeyType: null,
            ValueType: null,
            Location: location);
    }

    /// <summary>
    /// Continue parsing set literal after first element: {expr, expr, ...}
    /// </summary>
    private SetLiteralExpression ParseSetLiteralContinuation(Expression firstElement,
        SourceLocation location)
    {
        var elements = new List<Expression> { firstElement };

        // Parse remaining elements
        while (Match(type: TokenType.Comma))
        {
            elements.Add(item: ParseExpression());
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after set elements");

        return new SetLiteralExpression(Elements: elements, ElementType: null, Location: location);
    }
}
