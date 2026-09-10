using Builder.Tokenizer;
using SyntaxTree;

namespace Builder.Parser;

/// <summary>
/// Partial class containing lambda expression parsing.
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Parse the 'given' clause for explicit lambda captures.
    /// Supports both forms:
    /// - given x          (single capture without parentheses)
    /// - given (x, y, z)  (multiple captures with parentheses)
    /// </summary>
    private List<string> ParseGivenClause()
    {
        var captures = new List<string>();

        // Check if parenthesized or single identifier
        if (CheckAndAdvance(type: TokenType.LeftParen))
        {
            // Parenthesized form: given (x, y, z)
            if (!Check(type: TokenType.RightParen))
            {
                do
                {
                    string captureName =
                        ConsumeIdentifier(errorMessage: "Expected capture variable name");
                    captures.Add(item: captureName);
                } while (CheckAndAdvance(type: TokenType.Comma));
            }

            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after capture list");
        }
        else
        {
            // Single identifier form: given x
            string captureName =
                ConsumeIdentifier(errorMessage: "Expected capture variable name after 'given'");
            captures.Add(item: captureName);
        }

        return captures;
    }

    /// <summary>
    /// Parse arrow lambda with single unparenthesized parameter: x => expr or x given y => expr
    /// Syntax: <c>x => expression</c> or <c>x given (a, b) => expression</c>
    /// Lambda bodies must be single expressions (one-liner only).
    /// </summary>
    private LambdaExpression ParseArrowLambdaExpression(SourceLocation location)
    {
        // Single parameter without parentheses: x => expr or x given y => expr
        string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");

        var parameters = new List<Parameter>
        {
            new(Name: paramName,
                Type: null,
                DefaultValue: null,
                Location: location)
        };

        // Check for 'given' clause for explicit captures
        List<string>? captures = null;
        if (CheckAndAdvance(type: TokenType.Given))
        {
            captures = ParseGivenClause();
        }

        Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' in lambda expression");

        Expression body = ParseExpression();
        return new LambdaExpression(Parameters: parameters,
            Body: body,
            Captures: captures,
            Location: location);
    }

    /// <summary>
    /// Check if we're inside parenthesized lambda parameters.
    /// Called after consuming '(' - scans ahead to see if we have:
    /// - identifier [, identifier]* ) =>
    /// - identifier [, identifier]* ) given ... =>
    /// </summary>
    /// <remarks>
    /// LOOKAHEAD FUNCTION - Does not consume tokens permanently.
    ///
    /// This function distinguishes between:
    ///   (x, y) => x + y         - Lambda expression
    ///   (x + y)                 - Parenthesized expression
    ///   (x, y)                  - Tuple expression (future)
    ///
    /// Lambda parameter patterns we accept:
    ///   ()                      - No parameters
    ///   (x)                     - Single untyped parameter
    ///   (x: S32)                - Single typed parameter
    ///   (x, y)                  - Multiple untyped parameters
    ///   (x: S32, y: Text)       - Multiple typed parameters
    ///   (x, y) given (a, b)     - With explicit captures
    ///
    /// The key discriminator is the '=>' or 'given' after the ')'.
    /// </remarks>
    private bool IsArrowLambdaParameters()
    {
        int savedPosition = _position;

        try
        {
            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 1: Empty params - () => or () given =>
            // ═══════════════════════════════════════════════════════════════════════════
            if (Check(type: TokenType.RightParen))
            {
                Advance(); // consume )
                bool result = Check(type: TokenType.FatArrow) || Check(type: TokenType.Given);
                _position = savedPosition;
                return result;
            }

            // ═══════════════════════════════════════════════════════════════════════════
            // CASE 2: Scan parameter list - identifier [: type]? [, ...]* ) [given] =>
            // ═══════════════════════════════════════════════════════════════════════════
            return ScanArrowLambdaParameterList(savedPosition: savedPosition);
        }
        catch
        {
            _position = savedPosition;
            return false;
        }
    }

    /// <summary>
    /// Lookahead scan (CASE 2) of a parenthesized parameter list — <c>identifier [: type]? [, ...]* )
    /// [given] =></c> — starting after the opening <c>(</c> and the empty-list check. Always restores
    /// <paramref name="savedPosition"/> before returning; true only when the closing <c>)</c> is
    /// followed by <c>=></c> or <c>given</c>.
    /// </summary>
    private bool ScanArrowLambdaParameterList(int savedPosition)
    {
        while (true)
        {
            // ─────────────────────────────────────────────────────────────────────
            // Each parameter must start with identifier
            // ─────────────────────────────────────────────────────────────────────
            if (!Check(type: TokenType.Identifier))
            {
                _position = savedPosition;
                return false;
            }

            Advance(); // consume identifier

            // ─────────────────────────────────────────────────────────────────────
            // Optional type annotation - skip over it
            // ─────────────────────────────────────────────────────────────────────
            if (Check(type: TokenType.Colon))
            {
                SkipLambdaParameterTypeAnnotation();
            }

            // ─────────────────────────────────────────────────────────────────────
            // Check for comma (more params) or closing paren (end of list)
            // ─────────────────────────────────────────────────────────────────────
            if (Check(type: TokenType.Comma))
            {
                Advance(); // consume comma, continue loop
            }
            else if (Check(type: TokenType.RightParen))
            {
                Advance(); // consume )
                // Accept either direct => or given ... =>
                bool result = Check(type: TokenType.FatArrow) || Check(type: TokenType.Given);
                _position = savedPosition;
                return result;
            }
            else
            {
                // Not a valid lambda parameter list (e.g., expression like (x + y))
                _position = savedPosition;
                return false;
            }
        }
    }

    /// <summary>
    /// Skips over a lambda parameter's type annotation during lookahead, starting at the <c>:</c>. Tracks
    /// <c>[ ]</c> nesting depth so nested generics (e.g. <c>List[Dict[Text, S32]]</c>) are skipped whole,
    /// stopping at the top-level <c>,</c> or <c>)</c>.
    /// </summary>
    private void SkipLambdaParameterTypeAnnotation()
    {
        Advance(); // consume :
        // Skip the type (track [ ] depth for generics)
        int depth = 0;
        while (!IsAtEnd)
        {
            if (Check(type: TokenType.LeftBracket))
            {
                depth++;
            }
            else if (Check(type: TokenType.RightBracket))
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

    /// <summary>
    /// Parse arrow lambda with parenthesized parameters: () => expr, (x) => expr, or (x, y) given (a, b) => expr
    /// Called after '(' has been consumed and IsArrowLambdaParameters() returned true.
    /// Lambda bodies must be single expressions (one-liner only).
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

                if (CheckAndAdvance(type: TokenType.Colon))
                {
                    paramType = ParseType();
                }

                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: null,
                    Location: GetLocation()));
            } while (CheckAndAdvance(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after lambda parameters");

        // Check for 'given' clause for explicit captures
        List<string>? captures = null;
        if (CheckAndAdvance(type: TokenType.Given))
        {
            captures = ParseGivenClause();
        }

        Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after lambda parameters");

        Expression body = ParseExpression();
        return new LambdaExpression(Parameters: parameters,
            Body: body,
            Captures: captures,
            Location: location);
    }
}
