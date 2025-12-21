using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, blocks, scoped access, etc.).
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parses an if statement with optional elseif/else chains.
    /// Syntax: <c>if condition { body } elseif condition { body } else { body }</c>
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> AST node.</returns>
    private IfStatement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? thenBranch = ParseStatement();
        Statement? elseBranch = null;

        // Handle elseif chain
        while (Match(type: TokenType.Elseif))
        {
            Expression elseifCondition = ParseExpression();
            Statement? elseifBranch = ParseStatement();

            // Convert elseif to nested if-else
            var nestedIf = new IfStatement(Condition: elseifCondition,
                ThenStatement: elseifBranch,
                ElseStatement: null,
                Location: GetLocation(token: PeekToken(offset: -1)));

            elseBranch = elseBranch switch
            {
                null => nestedIf,
                IfStatement { ElseStatement: null } prevIf => new IfStatement(Condition: prevIf.Condition,
                    ThenStatement: prevIf.ThenStatement,
                    ElseStatement: nestedIf,
                    Location: prevIf.Location),
                _ => nestedIf
            };
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
                    ElseStatement: current.ElseStatement ?? finalElse,
                    Location: lastIf.Location);
            }
        }

        return new IfStatement(Condition: condition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses an unless statement (inverted if).
    /// Syntax: <c>unless condition { body }</c> = <c>if not condition { body }</c>
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> with negated condition.</returns>
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
        var negatedCondition = new UnaryExpression(Operator: UnaryOperator.Not, Operand: condition, Location: condition.Location);

        return new IfStatement(Condition: negatedCondition,
            ThenStatement: thenBranch,
            ElseStatement: elseBranch,
            Location: location);
    }

    /// <summary>
    /// Parses a while loop statement.
    /// Syntax: <c>while condition { body }</c>
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private WhileStatement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? body = ParseStatement();

        return new WhileStatement(Condition: condition, Body: body, Location: location);
    }

    /// <summary>
    /// Parses an infinite loop statement.
    /// Syntax: <c>loop { body }</c> = <c>while true { body }</c>
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> with always-true condition.</returns>
    private WhileStatement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // loop { body } is equivalent to while true { body }
        Expression trueCondition = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        Statement? body = ParseStatement();

        return new WhileStatement(Condition: trueCondition, Body: body, Location: location);
    }

    /// <summary>
    /// Parses a for-in loop statement (iteration over collections).
    /// Syntax: <c>for variable in iterable { body }</c>
    /// </summary>
    /// <returns>A <see cref="ForStatement"/> AST node.</returns>
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

    /// <summary>
    /// Parses a when statement (pattern matching).
    /// Syntax: <c>when expr { pattern =&gt; body, ... }</c> or <c>when { condition =&gt; body, ... }</c>
    /// Supports type patterns, literal patterns, wildcard patterns, and expression guards.
    /// </summary>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Check for standalone when block (pattern matching without subject)
        // when { pattern => body, ... }
        Expression expression;
        if (Check(type: TokenType.LeftBrace))
        {
            // Standalone when - use 'true' as the implicit subject
            expression = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
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
                    string varName = ConsumeIdentifier(errorMessage: "Expected variable name after 'else'");
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

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional comma or newline between clauses
            Match(TokenType.Comma, TokenType.Newline);
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after when clauses");

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location);
    }

    /// <summary>
    /// Parses a pattern for use in when clauses.
    /// Supports: wildcard (_), type patterns (Type varName), literal patterns, guard patterns (n if n &lt; 0).
    /// </summary>
    /// <returns>A <see cref="Pattern"/> AST node.</returns>
    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "_")
        {
            Advance();
            Pattern wildcardPattern = new WildcardPattern(Location: location);
            return TryParseGuard(innerPattern: wildcardPattern, location: location);
        }

        // Type pattern with optional variable binding: Type variableName or Type
        if (Check(type: TokenType.TypeIdentifier))
        {
            TypeExpression type = ParseType();

            // Check for variable binding
            string? variableName = null;
            if (Check(type: TokenType.Identifier))
            {
                variableName = ConsumeIdentifier(errorMessage: "Expected variable name for type pattern");
            }

            Pattern typePattern = new TypePattern(Type: type, VariableName: variableName, Location: location);
            return TryParseGuard(innerPattern: typePattern, location: location);
        }

        // Identifier pattern: variable binding (check before parsing full expression)
        if (Check(type: TokenType.Identifier))
        {
            string name = ConsumeIdentifier(errorMessage: "Expected identifier for pattern");
            Pattern identPattern = new IdentifierPattern(Name: name, Location: location);
            return TryParseGuard(innerPattern: identPattern, location: location);
        }

        // Literal pattern: constants like 42, "hello", true, etc.
        Expression expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            Pattern litPattern = new LiteralPattern(Value: literal.Value, LiteralType: literal.LiteralType, Location: location);
            return TryParseGuard(innerPattern: litPattern, location: location);
        }

        // Otherwise, treat as expression pattern
        return new ExpressionPattern(Expression: expr, Location: location);
    }

    /// <summary>
    /// Tries to parse a guard clause (if condition) after a pattern.
    /// Syntax: <c>pattern if condition</c>
    /// </summary>
    /// <param name="innerPattern">The pattern before the guard.</param>
    /// <param name="location">Source location of the pattern.</param>
    /// <returns>A <see cref="GuardPattern"/> if guard is present, otherwise the original pattern.</returns>
    private Pattern TryParseGuard(Pattern innerPattern, SourceLocation location)
    {
        if (Match(type: TokenType.If))
        {
            Expression guard = ParseExpression();
            return new GuardPattern(InnerPattern: innerPattern, Guard: guard, Location: location);
        }

        return innerPattern;
    }

    /// <summary>
    /// Parses a return statement.
    /// Syntax: <c>return</c> or <c>return value</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private ReturnStatement ParseReturnStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression? value = null;
        // Check if there's a return value - if next token is a statement terminator or block end, there isn't one
        if (!Check(type: TokenType.Newline) && !Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            value = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new ReturnStatement(Value: value, Location: location);
    }

    /// <summary>
    /// Parses a break statement (exits loop).
    /// Syntax: <c>break</c>
    /// </summary>
    /// <returns>A <see cref="BreakStatement"/> AST node.</returns>
    private BreakStatement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    /// <summary>
    /// Parses a continue statement (skips to next loop iteration).
    /// Syntax: <c>continue</c>
    /// </summary>
    /// <returns>A <see cref="ContinueStatement"/> AST node.</returns>
    private ContinueStatement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

    /// <summary>
    /// Parses a pass statement (no-op placeholder).
    /// Syntax: <c>pass</c>
    /// </summary>
    /// <returns>A <see cref="PassStatement"/> AST node.</returns>
    private PassStatement ParsePassStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new PassStatement(Location: location);
    }

    /// <summary>
    /// Parses a throw statement (raises an error).
    /// Syntax: <c>throw errorExpression</c>
    /// </summary>
    /// <returns>A <see cref="ThrowStatement"/> AST node.</returns>
    private ThrowStatement ParseThrowStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Expression error = ParseExpression();
        ConsumeStatementTerminator();
        return new ThrowStatement(Error: error, Location: location);
    }

    /// <summary>
    /// Parses an absent statement (returns None from failable function).
    /// Syntax: <c>absent</c>
    /// </summary>
    /// <returns>An <see cref="AbsentStatement"/> AST node.</returns>
    private AbsentStatement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new AbsentStatement(Location: location);
    }

    /// <summary>
    /// Parses a block statement (sequence of statements in braces).
    /// Syntax: <c>{ statement1\nstatement2\n... }</c>
    /// Handles variable declarations with var/let keywords.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> AST node.</returns>
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
                // Check if this is destructuring: let (field, field2) = expr
                if (Check(type: TokenType.LeftParen))
                {
                    Statement destructuring = ParseDestructuringDeclaration();
                    statements.Add(item: destructuring);
                }
                else
                {
                    VariableDeclaration varDecl = ParseVariableDeclaration();
                    // Wrap the variable declaration as a declaration statement
                    statements.Add(item: new DeclarationStatement(Declaration: varDecl, Location: varDecl.Location));
                }

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
    /// Parses a block expression: { expr } or { statements\nexpr }
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

    /// <summary>
    /// Parses an expression statement (expression used as statement).
    /// Syntax: <c>expression</c>
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    /// <summary>
    /// Parses a danger statement (unsafe memory operations block).
    /// Syntax: <c>danger! { unsafe_operations }</c>
    /// </summary>
    /// <returns>A <see cref="DangerStatement"/> AST node.</returns>
    private DangerStatement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Expect 'danger!'
        Consume(type: TokenType.Bang, errorMessage: "Expected '!' after 'danger'");

        BlockStatement body = ParseBlockStatement();

        return new DangerStatement(Body: body, Location: location);
    }

    /// <summary>
    /// Parses a viewing statement (scoped read-only memory access).
    /// Syntax: <c>viewing source as token { body }</c>
    /// Provides safe, immutable access to shared memory.
    /// </summary>
    /// <returns>A <see cref="ViewingStatement"/> AST node.</returns>
    private ViewingStatement ParseViewingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: viewing <expr> as <token>
        Expression source = ParseExpression();

        Consume(type: TokenType.As, errorMessage: "Expected 'as' after viewing source");

        string token = ConsumeIdentifier(errorMessage: "Expected token name after 'as'");

        BlockStatement body = ParseBlockStatement();

        return new ViewingStatement(Source: source,
            Token: token,
            Body: body,
            Location: location);
    }

    /// <summary>
    /// Parses a hijacking statement (scoped exclusive memory access).
    /// Syntax: <c>hijacking source as token { body }</c>
    /// Provides exclusive mutable access to memory.
    /// </summary>
    /// <returns>A <see cref="HijackingStatement"/> AST node.</returns>
    private HijackingStatement ParseHijackingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: hijacking <expr> as <handle>
        Expression source = ParseExpression();

        Consume(type: TokenType.As, errorMessage: "Expected 'as' after hijacking source");

        string handle = ConsumeIdentifier(errorMessage: "Expected handle name after 'as'");

        BlockStatement body = ParseBlockStatement();

        return new HijackingStatement(Source: source,
            Token: handle,
            Body: body,
            Location: location);
    }

    /// <summary>
    /// Parses an inspecting statement (thread-safe scoped read access).
    /// Syntax: <c>inspecting source as handle { body }</c>
    /// Provides thread-safe shared read access with locking.
    /// </summary>
    /// <returns>An <see cref="InspectingStatement"/> AST node.</returns>
    private InspectingStatement ParseInspectingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: hijacking <expr> as <handle>
        Expression source = ParseExpression();

        Consume(type: TokenType.As, errorMessage: "Expected 'as' after inspecting handle");

        string handle = ConsumeIdentifier(errorMessage: "Expected handle name after 'as'");

        BlockStatement body = ParseBlockStatement();

        return new InspectingStatement(Source: source,
            Token: handle,
            Body: body,
            Location: location);
    }

    /// <summary>
    /// Parses a seizing statement (thread-safe scoped exclusive access).
    /// Syntax: <c>seizing source as handle { body }</c>
    /// Provides thread-safe exclusive mutable access with locking.
    /// </summary>
    /// <returns>A <see cref="SeizingStatement"/> AST node.</returns>
    private SeizingStatement ParseSeizingStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // Parse source expression: hijacking <expr> as <handle>
        Expression source = ParseExpression();

        Consume(type: TokenType.As, errorMessage: "Expected 'as' after seizing handle");

        string handle = ConsumeIdentifier(errorMessage: "Expected handle name after 'as'");

        BlockStatement body = ParseBlockStatement();

        return new SeizingStatement(Source: source,
            Token: handle,
            Body: body,
            Location: location);
    }

    /// <summary>
    /// Parses record/entity destructuring: let (field, field2) = expr
    /// or let (field: alias, field2: alias2) = expr
    /// or nested: let ((x, y), radius) = circle
    /// Destructuring only works for types where ALL fields are public.
    /// </summary>
    private DestructuringStatement ParseDestructuringDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -2)); // -2 because we already consumed 'let'/'var'
        bool isMutable = PeekToken(offset: -2)
           .Type == TokenType.Var;

        // Parse the destructuring pattern (reuse ParseDestructuringBindings from Expressions)
        List<DestructuringBinding> bindings = ParseDestructuringBindings();

        var pattern = new DestructuringPattern(Bindings: bindings, Location: location);

        // Expect '='
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' in destructuring");

        // Parse the initializer expression
        Expression initializer = ParseExpression();

        ConsumeStatementTerminator();

        return new DestructuringStatement(Pattern: pattern,
            Initializer: initializer,
            IsMutable: isMutable,
            Location: location);
    }
}
