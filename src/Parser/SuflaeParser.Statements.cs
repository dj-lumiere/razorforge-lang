using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser; // For ParseException

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, return, etc.).
/// </summary>
public partial class SuflaeParser
{
    /// <summary>
    /// Parses an if statement with indented blocks.
    /// Syntax: <c>if condition:</c> followed by indented body, optional <c>else:</c> block.
    /// </summary>
    /// <returns>An <see cref="IfStatement"/> AST node.</returns>
    private Statement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after if condition");
        Statement thenBranch = ParseIndentedBlock();

        Statement? elseBranch = null;
        if (Match(type: TokenType.Else))
        {
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after else");
            elseBranch = ParseIndentedBlock();
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
    private Statement ParseUnlessStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after if condition");
        Statement thenBranch = ParseIndentedBlock();
        Statement? elseBranch = null;

        if (Match(type: TokenType.Else))
        {
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after else");
            elseBranch = ParseIndentedBlock();
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
    /// Syntax: <c>while condition:</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node.</returns>
    private Statement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after while condition");
        Statement body = ParseIndentedBlock();

        return new WhileStatement(Condition: condition, Body: body, Location: location);
    }

    /// <summary>
    /// Parses a loop statement (infinite loop).
    /// Syntax: <c>loop:</c> followed by indented body.
    /// Equivalent to <c>while true:</c>
    /// </summary>
    /// <returns>A <see cref="WhileStatement"/> AST node with true condition.</returns>
    private Statement ParseLoopStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // loop: body is equivalent to while true: body
        Expression trueCondition = new LiteralExpression(Value: true, LiteralType: TokenType.True, Location: location);
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after loop");
        Statement body = ParseIndentedBlock();

        return new WhileStatement(Condition: trueCondition, Body: body, Location: location);
    }

    /// <summary>
    /// Parses a for-in loop statement.
    /// Syntax: <c>for variable in iterable:</c> followed by indented body.
    /// </summary>
    /// <returns>A <see cref="ForStatement"/> AST node.</returns>
    private Statement ParseForStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string variable = ConsumeIdentifier(errorMessage: "Expected variable name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression iterable = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after for header");
        Statement body = ParseIndentedBlock();

        return new ForStatement(Variable: variable,
            Iterable: iterable,
            Body: body,
            Location: location);
    }

    /// <summary>
    /// Parses a when (pattern matching) statement.
    /// Syntax: <c>when expression:</c> followed by indented pattern clauses with<c> =&gt;</c>.
    /// Similar to Rust's match or C# switch expressions.
    /// </summary>
    /// <returns>A <see cref="WhenStatement"/> AST node.</returns>
    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression expression = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after when expression");

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after when header");
        Consume(type: TokenType.Indent, errorMessage: "Expected indented block after when");

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
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
            Statement body;
            if (Check(type: TokenType.Colon))
            {
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after '=>'");
                body = ParseIndentedBlock();
            }
            else
            {
                body = ParseExpressionStatement();
            }

            _inWhenClauseBody = false;

            clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional newline between clauses
            Match(type: TokenType.Newline);
        }

        Consume(type: TokenType.Dedent, errorMessage: "Expected dedent after when clauses");

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location);
    }

    /// <summary>
    /// Parses a pattern for use in when clauses.
    /// Supports: wildcard (_), type patterns, identifier bindings, literal patterns, guard patterns (n if n &lt; 0).
    /// </summary>
    /// <returns>A <see cref="Pattern"/> AST node.</returns>
    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Match(type: TokenType.Identifier) && PeekToken(offset: -1)
               .Text == "_")
        {
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

        // Identifier pattern: variable binding
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

        throw new ParseException(message: $"Expected pattern, got {CurrentToken.Type}");
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
    /// Syntax: <c>return</c> or <c>return expression</c>
    /// </summary>
    /// <returns>A <see cref="ReturnStatement"/> AST node.</returns>
    private Statement ParseReturnStatement()
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

    /// <summary>
    /// Parses a throw (fail) statement for error propagation.
    /// Syntax: <c>throw errorExpression</c>
    /// Used with Crashable types for error handling.
    /// </summary>
    /// <returns>A <see cref="ThrowStatement"/> AST node.</returns>
    private Statement ParseThrowStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // fail requires an error expression (Crashable type)
        Expression error = ParseExpression();

        ConsumeStatementTerminator();

        return new ThrowStatement(Error: error, Location: location);
    }

    /// <summary>
    /// Parses an absent statement (return none/null).
    /// Syntax: <c>absent</c>
    /// Indicates the function returns no value (for optional types).
    /// </summary>
    /// <returns>An <see cref="AbsentStatement"/> AST node.</returns>
    private Statement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // absent takes no arguments
        ConsumeStatementTerminator();

        return new AbsentStatement(Location: location);
    }

    /// <summary>
    /// Parses a break statement for exiting loops.
    /// Syntax: <c>break</c>
    /// </summary>
    /// <returns>A <see cref="BreakStatement"/> AST node.</returns>
    private Statement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    /// <summary>
    /// Parses a continue statement for skipping to next loop iteration.
    /// Syntax: <c>continue</c>
    /// </summary>
    /// <returns>A <see cref="ContinueStatement"/> AST node.</returns>
    private Statement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

    /// <summary>
    /// Parses a pass statement (empty statement placeholder).
    /// Syntax: <c>pass</c>
    /// Used as a placeholder in empty blocks or protocols.
    /// </summary>
    /// <returns>A <see cref="PassStatement"/> AST node.</returns>
    private PassStatement ParsePassStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new PassStatement(Location: location);
    }

    /// <summary>
    /// Parses an indented block of statements.
    /// Expects INDENT token, parses statements until DEDENT.
    /// This is core to Suflae's Python-like indentation syntax.
    /// </summary>
    /// <returns>A <see cref="BlockStatement"/> containing the parsed statements.</returns>
    private Statement ParseIndentedBlock()
    {
        SourceLocation location = GetLocation();
        var statements = new List<Statement>();

        // Consume newline after colon
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        // Must have an indent token for a proper indented block
        if (!Check(type: TokenType.Indent))
        {
            throw new ParseException(message: "Expected indented block after ':'");
        }

        // Process the indent token
        ProcessIndentToken();

        // Parse statements until we hit a dedent
        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            // Skip empty lines
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            Statement? stmt = ParseStatement();
            if (stmt != null)
            {
                statements.Add(item: stmt);
            }
        }

        // Process dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw new ParseException(message: "Expected dedent to close indented block");
        }

        return new BlockStatement(Statements: statements, Location: location);
    }

    /// <summary>
    /// Parses an expression statement (expression followed by newline).
    /// Used for function calls, assignments, and other expressions at statement level.
    /// </summary>
    /// <returns>An <see cref="ExpressionStatement"/> AST node.</returns>
    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    /// <summary>
    /// Parses record/entity destructuring in let/var bindings.
    /// Syntax: <c>let (field, field2) = expr</c> or <c>let (field: alias, field2: alias2) = expr</c>
    /// or nested: <c>let ((x, y), radius) = circle</c>
    /// Destructuring only works for types where ALL fields are public.
    /// </summary>
    /// <returns>A <see cref="DestructuringStatement"/> AST node.</returns>
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
