using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, blocks, scoped access, etc.).
/// </summary>
public partial class RazorForgeParser
{
    private IfStatement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? thenBranch = ParseStatement();
        Statement? elseBranch = null;

        // Handle elif chain
        while (Match(type: TokenType.Elseif))
        {
            Expression elifCondition = ParseExpression();
            Statement? elifBranch = ParseStatement();

            // Convert elif to nested if-else
            var nestedIf = new IfStatement(Condition: elifCondition,
                ThenStatement: elifBranch,
                ElseStatement: null,
                Location: GetLocation(token: PeekToken(offset: -1)));

            if (elseBranch == null)
            {
                elseBranch = nestedIf;
            }
            else
            {
                // Chain elifs together
                if (elseBranch is IfStatement prevIf && prevIf.ElseStatement == null)
                {
                    elseBranch = new IfStatement(Condition: prevIf.Condition,
                        ThenStatement: prevIf.ThenStatement,
                        ElseStatement: nestedIf,
                        Location: prevIf.Location);
                }
                else
                {
                    elseBranch = nestedIf;
                }
            }
        }

        if (Match(type: TokenType.Else))
        {
            Statement? finalElse = ParseStatement();

            if (elseBranch == null)
            {
                elseBranch = finalElse;
            }
            else if (elseBranch is IfStatement lastIf)
            {
                // Attach final else to the end of the elif chain
                IfStatement current = lastIf;
                while (current.ElseStatement is IfStatement nextIf)
                {
                    current = nextIf;
                }

                elseBranch = new IfStatement(Condition: lastIf.Condition,
                    ThenStatement: lastIf.ThenStatement,
                    ElseStatement: current.ElseStatement == null
                        ? finalElse
                        : current.ElseStatement,
                    Location: lastIf.Location);
            }
        }

        return new IfStatement(Condition: condition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    private IfStatement ParseUnlessStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? thenBranch = ParseStatement();
        Statement? elseBranch = null;

        if (Match(type: TokenType.Else))
        {
            elseBranch = ParseStatement();
        }

        // Unless is "if not condition"
        var negatedCondition = new UnaryExpression(Operator: UnaryOperator.Not,
            Operand: condition,
            Location: condition.Location);

        return new IfStatement(Condition: negatedCondition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    private WhileStatement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? body = ParseStatement();

        return new WhileStatement(Condition: condition, Body: body, Location: location);
    }

    private WhileStatement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // loop { body } is equivalent to while true { body }
        Expression trueCondition =
            new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        Statement? body = ParseStatement();

        return new WhileStatement(Condition: trueCondition, Body: body, Location: location);
    }

    private ForStatement ParseForStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string variable = ConsumeIdentifier(errorMessage: "Expected variable name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression iterable = ParseExpression();
        Statement? body = ParseStatement();

        return new ForStatement(Variable: variable,
            Iterable: iterable,
            Body: body,
            Location: location);
    }

    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Check for standalone when block (pattern matching without subject)
        // when { pattern => body, ... }
        Expression expression;
        if (Check(type: TokenType.LeftBrace))
        {
            // Standalone when - use 'true' as the implicit subject
            expression = new LiteralExpression(Value: true,
                LiteralType: TokenType.True,
                Location: location);
        }
        else
        {
            // Traditional when with explicit subject
            expression = ParseExpression();
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after when expression");

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            // Skip newlines
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            Pattern pattern;
            SourceLocation clauseLocation = GetLocation();
            // Handle 'is' keyword pattern: is None, is SomeType, is SomeType varName
            if (Match(type: TokenType.Is))
            {
                _inWhenPatternContext = true;
                pattern = ParsePattern();
                _inWhenPatternContext = false;
            }
            // Handle 'else' keyword for default case: else => body or else varName => body
            else if (Match(type: TokenType.Else))
            {
                // Check for variable binding: else varName =>
                if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                       .Type == TokenType.FatArrow)
                {
                    string varName =
                        ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
                    pattern = new IdentifierPattern(Name: varName, Location: clauseLocation);
                }
                else
                {
                    // Plain else without variable binding - treat as wildcard
                    pattern = new WildcardPattern(Location: clauseLocation);
                }
            }
            else
            {
                // Set context flag to prevent single-param lambdas from being parsed
                // inside when patterns (e.g., a < b => action should not treat b => action as lambda)
                _inWhenPatternContext = true;
                pattern = ParsePattern();
                _inWhenPatternContext = false;
            }

            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after pattern");

            // Set flag to prevent 'is' expression parsing in when clause bodies
            _inWhenClauseBody = true;
            Statement? body = ParseStatement();
            _inWhenClauseBody = false;

            clauses.Add(
                item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after when clauses");

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location);
    }

    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
        {
            Advance();
            return new WildcardPattern(Location: location);
        }

        // Type pattern with optional variable binding: Type variableName or Type
        if (Check(type: TokenType.TypeIdentifier))
        {
            TypeExpression type = ParseType();

            // Check for variable binding
            string? variableName = null;
            if (Check(type: TokenType.Identifier))
            {
                variableName =
                    ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
            }

            return new TypePattern(Type: type, VariableName: variableName, Location: location);
        }

        // Try parsing as expression (for standalone when blocks with boolean conditions)
        // This allows patterns like: b != 0, x > 10, etc.
        Expression expr = ParseExpression();

        // If it's a simple identifier, treat as identifier pattern (variable binding)
        if (expr is IdentifierExpression identExpr)
        {
            return new IdentifierPattern(Name: identExpr.Name, Location: location);
        }

        // If it's a literal, treat as literal pattern
        if (expr is LiteralExpression literal)
        {
            return new LiteralPattern(Value: literal.Value,
                LiteralType: literal.LiteralType,
                Location: location);
        }

        // Otherwise, treat as expression pattern (guard condition)
        return new ExpressionPattern(Expression: expr, Location: location);
    }

    private ReturnStatement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        if (!Check(type: TokenType.Newline) && !IsAtEnd)
        {
            value = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new ReturnStatement(Value: value, Location: location);
    }

    private BreakStatement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    private ContinueStatement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

    private ThrowStatement ParseThrowStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression error = ParseExpression();
        ConsumeStatementTerminator();
        return new ThrowStatement(Error: error, Location: location);
    }

    private AbsentStatement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new AbsentStatement(Location: location);
    }

    private BlockStatement ParseBlockStatement()
    {
        SourceLocation location = GetLocation();
        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{'");

        var statements = new List<Statement>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Handle variable declarations inside blocks
            if (Match(TokenType.Var, TokenType.Let))
            {
                VariableDeclaration varDecl = ParseVariableDeclaration();
                // Wrap the variable declaration as a declaration statement
                statements.Add(item: new DeclarationStatement(Declaration: varDecl,
                    Location: varDecl.Location));
                continue;
            }

            Statement? stmt = ParseStatement();
            if (stmt != null)
            {
                statements.Add(item: stmt);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}'");

        return new BlockStatement(Statements: statements, Location: location);
    }

    /// <summary>
    /// Parses a block expression: { expr } or { statements; expr }
    /// The block evaluates to the last expression.
    /// </summary>
    private Expression ParseBlockExpression()
    {
        SourceLocation location = GetLocation();
        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{'");

        // Skip leading newlines
        while (Match(type: TokenType.Newline))
        {
        }

        // Parse the expression (which will be the block's value)
        Expression expr = ParseExpression();

        // Skip trailing newlines
        while (Match(type: TokenType.Newline))
        {
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}'");

        // Return the expression wrapped in a BlockExpression
        return new BlockExpression(Value: expr, Location: location);
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    private DangerStatement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Expect 'danger!'
        Consume(type: TokenType.Bang, errorMessage: "Expected '!' after 'danger'");

        BlockStatement body = ParseBlockStatement();

        return new DangerStatement(Body: body, Location: location);
    }

    private MayhemStatement ParseMayhemStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Expect 'mayhem!'
        Consume(type: TokenType.Bang, errorMessage: "Expected '!' after 'mayhem'");

        BlockStatement body = ParseBlockStatement();

        return new MayhemStatement(Body: body, Location: location);
    }

    private ViewingStatement ParseViewingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: viewing <expr> as <handle>
        Expression source = ParseExpression();

        Consume(type: TokenType.As, errorMessage: "Expected 'as' after viewing source");

        string handle = ConsumeIdentifier(errorMessage: "Expected handle name after 'as'");

        BlockStatement body = ParseBlockStatement();

        return new ViewingStatement(Source: source,
            Handle: handle,
            Body: body,
            Location: location);
    }

    private HijackingStatement ParseHijackingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: hijacking <expr> as <handle>
        Expression source = ParseExpression();

        Consume(type: TokenType.As, errorMessage: "Expected 'as' after hijacking source");

        string handle = ConsumeIdentifier(errorMessage: "Expected handle name after 'as'");

        BlockStatement body = ParseBlockStatement();

        return new HijackingStatement(Source: source,
            Handle: handle,
            Body: body,
            Location: location);
    }

    private ObservingStatement ParseObservingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: observing <expr> from <handle>
        string handle = ConsumeIdentifier(errorMessage: "Expected handle name");

        Consume(type: TokenType.From, errorMessage: "Expected 'from' after observing handle");

        Expression source = ParseExpression();

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after observing source");

        BlockStatement body = ParseBlockStatement();

        return new ObservingStatement(Source: source,
            Handle: handle,
            Body: body,
            Location: location);
    }

    private SeizingStatement ParseSeizingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: seizing <expr> from <handle>
        string handle = ConsumeIdentifier(errorMessage: "Expected handle name");

        Consume(type: TokenType.From, errorMessage: "Expected 'from' after seizing handle");

        Expression source = ParseExpression();

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after seizing source");

        BlockStatement body = ParseBlockStatement();

        return new SeizingStatement(Source: source,
            Handle: handle,
            Body: body,
            Location: location);
    }
}
