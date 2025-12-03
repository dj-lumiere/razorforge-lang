using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing statement parsing (if, while, for, when, return, etc.).
/// </summary>
public partial class SuflaeParser
{
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

    private Statement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after while condition");
        Statement body = ParseIndentedBlock();

        return new WhileStatement(Condition: condition, Body: body, Location: location);
    }

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

            Pattern pattern = ParsePattern();
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after pattern");

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

            clauses.Add(
                item: new WhenClause(Pattern: pattern, Body: body, Location: GetLocation()));

            // Optional newline between clauses
            Match(type: TokenType.Newline);
        }

        Consume(type: TokenType.Dedent, errorMessage: "Expected dedent after when clauses");

        return new WhenStatement(Expression: expression, Clauses: clauses, Location: location);
    }

    private Pattern ParsePattern()
    {
        SourceLocation location = GetLocation();

        // Wildcard pattern: _
        if (Match(type: TokenType.Identifier) && PeekToken(offset: -1)
               .Text == "_")
        {
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

        // Identifier pattern: variable binding
        if (Check(type: TokenType.Identifier))
        {
            string name = ConsumeIdentifier(errorMessage: "Expected identifier for pattern");
            return new IdentifierPattern(Name: name, Location: location);
        }

        // Literal pattern: constants like 42, "hello", true, etc.
        Expression expr = ParsePrimary();
        if (expr is LiteralExpression literal)
        {
            return new LiteralPattern(Value: literal.Value,
                LiteralType: literal.LiteralType,
                Location: location);
        }

        throw new ParseException(message: $"Expected pattern, got {CurrentToken.Type}");
    }

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

    private Statement ParseFailStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // fail requires an error expression (Crashable type)
        Expression error = ParseExpression();

        ConsumeStatementTerminator();

        return new ThrowStatement(Error: error, Location: location);
    }

    private Statement ParseAbsentStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // absent takes no arguments
        ConsumeStatementTerminator();

        return new AbsentStatement(Location: location);
    }

    private Statement ParseBreakStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new BreakStatement(Location: location);
    }

    private Statement ParseContinueStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        ConsumeStatementTerminator();
        return new ContinueStatement(Location: location);
    }

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

    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }
}
