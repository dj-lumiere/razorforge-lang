using Compilers.Shared.AST;
using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Parser for RazorForge language
/// </summary>
public class RazorForgeParser : BaseParser
{
    public RazorForgeParser(List<Token> tokens) : base(tokens: tokens) { }

    public override Compilers.Shared.AST.Program Parse()
    {
        var declarations = new List<IAstNode>();

        while (!IsAtEnd)
        {
            try
            {
                // Skip newlines at top level
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                IAstNode? decl = ParseDeclaration();
                if (decl != null)
                {
                    declarations.Add(item: decl);
                }
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine(value: $"Parse error: {ex.Message}");
                Synchronize();
            }
        }

        return new Compilers.Shared.AST.Program(Declarations: declarations,
            Location: GetLocation());
    }

    private IAstNode? ParseDeclaration()
    {
        // Import declaration
        if (Match(type: TokenType.Import))
        {
            return ParseImportDeclaration();
        }

        // Redefinition
        if (Match(type: TokenType.Define))
        {
            return ParseRedefinitionDeclaration();
        }

        // Using declaration
        if (Match(type: TokenType.Using))
        {
            return ParseUsingDeclaration();
        }

        // Parse visibility modifier
        VisibilityModifier visibility = ParseVisibilityModifier();

        // External declaration with optional calling convention
        // Supports: external routine foo() or external("C") routine foo()
        if (visibility == VisibilityModifier.External)
        {
            string? callingConvention = null;

            // Check for calling convention: external("C")
            if (Match(type: TokenType.LeftParen))
            {
                if (Check(type: TokenType.TextLiteral))
                {
                    Token conventionToken = Advance();
                    // Remove quotes from the text literal
                    callingConvention = conventionToken.Text.Trim(trimChar: '"');
                }

                Consume(type: TokenType.RightParen,
                    errorMessage: "Expected ')' after calling convention");
            }

            if (Match(type: TokenType.Routine))
            {
                return ParseExternalDeclaration(callingConvention: callingConvention);
            }
        }

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let))
        {
            return ParseVariableDeclaration(visibility: visibility);
        }

        // Function declaration
        if (Match(type: TokenType.Routine))
        {
            return ParseFunctionDeclaration(visibility: visibility);
        }

        // Entity/Record/Enum declarations
        if (Match(type: TokenType.Entity))
        {
            return ParseClassDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Record))
        {
            return ParseStructDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Choice))
        {
            return ParseEnumDeclaration(visibility: visibility);
        }

        if (Match(type: TokenType.Chimera))
        {
            return ParseVariantDeclaration(visibility: visibility, kind: VariantKind.Chimera);
        }

        if (Match(type: TokenType.Variant))
        {
            return ParseVariantDeclaration(visibility: visibility, kind: VariantKind.Variant);
        }

        if (Match(type: TokenType.Mutant))
        {
            return ParseVariantDeclaration(visibility: visibility, kind: VariantKind.Mutant);
        }

        if (Match(type: TokenType.Protocol))
        {
            return ParseFeatureDeclaration(visibility: visibility);
        }

        // If we parsed a visibility modifier but no declaration follows, reset position
        if (visibility != VisibilityModifier.Private)
        {
            // Go back to before the visibility modifier
            Position--;
        }

        // Otherwise parse as statement
        return ParseStatement();
    }

    private Statement? ParseStatement()
    {
        // Control flow
        if (Match(type: TokenType.If))
        {
            return ParseIfStatement();
        }

        if (Match(type: TokenType.Unless))
        {
            return ParseUnlessStatement();
        }

        if (Match(type: TokenType.While))
        {
            return ParseWhileStatement();
        }

        if (Match(type: TokenType.For))
        {
            return ParseForStatement();
        }

        if (Match(type: TokenType.When))
        {
            return ParseWhenStatement();
        }

        if (Match(type: TokenType.Return))
        {
            return ParseReturnStatement();
        }

        if (Match(type: TokenType.Break))
        {
            return ParseBreakStatement();
        }

        if (Match(type: TokenType.Continue))
        {
            return ParseContinueStatement();
        }

        // Danger block
        if (Match(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        // Mayhem block
        if (Match(type: TokenType.Mayhem))
        {
            return ParseMayhemStatement();
        }

        // Viewing block (scoped read-only access)
        if (Match(type: TokenType.Viewing))
        {
            return ParseViewingStatement();
        }

        // Hijacking block (scoped exclusive access)
        if (Match(type: TokenType.Hijacking))
        {
            return ParseHijackingStatement();
        }

        // Observing block (thread-safe scoped read access)
        if (Match(type: TokenType.Observing))
        {
            return ParseObservingStatement();
        }

        // Seizing block (thread-safe scoped exclusive access)
        if (Match(type: TokenType.Seizing))
        {
            return ParseSeizingStatement();
        }

        // Block statement
        if (Check(type: TokenType.LeftBrace))
        {
            return ParseBlockStatement();
        }

        // Expression statement
        return ParseExpressionStatement();
    }

    private VariableDeclaration ParseVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        bool isMutable = PeekToken(offset: -1)
           .Type == TokenType.Var;

        string name = ConsumeIdentifier(errorMessage: "Expected variable name");

        TypeExpression? type = null;
        if (Match(type: TokenType.Colon))
        {
            type = ParseType();
        }

        Expression? initializer = null;
        if (Match(type: TokenType.Assign))
        {
            initializer = ParseExpression();
        }

        ConsumeStatementTerminator();

        return new VariableDeclaration(Name: name, Type: type, Initializer: initializer,
            Visibility: visibility, IsMutable: isMutable, Location: location);
    }

    private ImportDeclaration ParseImportDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string modulePath = "";
        string? alias = (string?)null;
        var specificImports = (List<string>?)null;

        // Parse module path - could be multiple identifiers separated by slashes
        do
        {
            string part = ConsumeIdentifier(errorMessage: "Expected module name");
            modulePath += part;
            if (Match(type: TokenType.Slash))
            {
                modulePath += "/";
            }
            else
            {
                break;
            }
        } while (true);

        // Optional alias
        if (Match(type: TokenType.As))
        {
            alias = ConsumeIdentifier(errorMessage: "Expected alias name");
        }

        ConsumeStatementTerminator();

        return new ImportDeclaration(ModulePath: modulePath, Alias: alias,
            SpecificImports: specificImports, Location: location);
    }

    private IAstNode ParseRedefinitionDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string oldName = ConsumeIdentifier(errorMessage: "Expected identifier after 'redefine'");
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in redefinition");
        string newName =
            ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

        ConsumeStatementTerminator();

        return new RedefinitionDeclaration(OldName: oldName, NewName: newName, Location: location);
    }

    private IAstNode ParseUsingDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        TypeExpression type = ParseType();
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in using declaration");
        string alias = ConsumeIdentifier(errorMessage: "Expected alias name in using declaration");

        ConsumeStatementTerminator();

        return new UsingDeclaration(Type: type, Alias: alias, Location: location);
    }

    private VisibilityModifier ParseVisibilityModifier()
    {
        if (Match(type: TokenType.Public))
        {
            return VisibilityModifier.Public;
        }

        if (Match(type: TokenType.PublicFamily))
        {
            return VisibilityModifier.Protected;
        }

        if (Match(type: TokenType.PublicModule))
        {
            return VisibilityModifier.Internal;
        }

        if (Match(type: TokenType.Private))
        {
            return VisibilityModifier.Private;
        }

        if (Match(type: TokenType.External))
        {
            return VisibilityModifier.External;
        }

        if (Match(type: TokenType.Global))
        {
            return VisibilityModifier.Global;
        }

        return VisibilityModifier.Private; // Default
    }

    private FunctionDeclaration ParseFunctionDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected function name");

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after function name");
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

                parameters.Add(item: new Parameter(Name: paramName, Type: paramType,
                    DefaultValue: defaultValue, Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // Return type
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Body
        BlockStatement body = ParseBlockStatement();

        return new FunctionDeclaration(Name: name, Parameters: parameters, ReturnType: returnType,
            Body: body, Visibility: visibility, Attributes: new List<string>(),
            Location: location);
    }

    private ClassDeclaration ParseClassDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater,
                errorMessage: "Expected '>' after generic parameters");
        }

        // Base entity - can use "from Animal" syntax
        TypeExpression? baseClass = null;
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.From))
        {
            baseClass = ParseType();
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after entity header");

        var members = new List<Declaration>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            var member = ParseDeclaration() as Declaration;
            if (member != null)
            {
                members.Add(item: member);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after entity body");

        return new ClassDeclaration(Name: name, GenericParameters: genericParams,
            BaseClass: baseClass, Interfaces: interfaces, Members: members, Visibility: visibility,
            Location: location);
    }

    private StructDeclaration ParseStructDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater,
                errorMessage: "Expected '>' after generic parameters");
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after record header");

        var members = new List<Declaration>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            var member = ParseDeclaration() as Declaration;
            if (member != null)
            {
                members.Add(item: member);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after record body");

        return new StructDeclaration(Name: name, GenericParameters: genericParams,
            Members: members, Visibility: visibility, Location: location);
    }

    private MenuDeclaration ParseEnumDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected enum name");

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after enum name");

        var variants = new List<EnumVariant>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            string variantName = ConsumeIdentifier(errorMessage: "Expected enum variant name");

            int? value = null;
            if (Match(type: TokenType.Assign))
            {
                Expression expr = ParseExpression();
                if (expr is LiteralExpression literal && literal.Value is int intVal)
                {
                    value = intVal;
                }
            }

            variants.Add(item: new EnumVariant(Name: variantName, Value: value,
                Location: GetLocation()));

            if (!Match(type: TokenType.Comma))
            {
                Match(type: TokenType.Newline);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after enum body");

        return new MenuDeclaration(Name: name, Variants: variants,
            Methods: new List<FunctionDeclaration>(), Visibility: visibility, Location: location);
    }

    private VariantDeclaration ParseVariantDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private,
        VariantKind kind = VariantKind.Chimera)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater,
                errorMessage: "Expected '>' after generic parameters");
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after variant header");

        var cases = new List<VariantCase>();
        var methods = new List<FunctionDeclaration>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Try to parse as function first
            if (Check(type: TokenType.Public) || Check(type: TokenType.Private) ||
                Check(type: TokenType.Routine))
            {
                var method = ParseDeclaration() as FunctionDeclaration;
                if (method != null)
                {
                    methods.Add(item: method);
                }
            }
            else
            {
                // Parse variant case
                string caseName = ConsumeIdentifier(errorMessage: "Expected variant case name");

                List<TypeExpression>? associatedTypes = null;
                if (Match(type: TokenType.LeftParen))
                {
                    associatedTypes = new List<TypeExpression>();
                    if (!Check(type: TokenType.RightParen))
                    {
                        do
                        {
                            associatedTypes.Add(item: ParseType());
                        } while (Match(type: TokenType.Comma));
                    }

                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after variant case types");
                }

                cases.Add(item: new VariantCase(Name: caseName, AssociatedTypes: associatedTypes,
                    Location: GetLocation()));

                if (!Match(type: TokenType.Comma))
                {
                    Match(type: TokenType.Newline);
                }
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after variant body");

        return new VariantDeclaration(Name: name, GenericParameters: genericParams, Cases: cases,
            Methods: methods, Visibility: visibility, Kind: kind, Location: location);
    }

    private FeatureDeclaration ParseFeatureDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected feature name");

        // Generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater,
                errorMessage: "Expected '>' after generic parameters");
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after feature header");

        var methods = new List<FunctionSignature>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Parse function signature
            Consume(type: TokenType.Routine, errorMessage: "Expected 'routine' in feature method");
            string methodName = ConsumeIdentifier(errorMessage: "Expected method name");

            // Parameters
            Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after method name");
            var parameters = new List<Parameter>();

            if (!Check(type: TokenType.RightParen))
            {
                do
                {
                    string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                    Consume(type: TokenType.Colon,
                        errorMessage: "Expected ':' after parameter name");
                    TypeExpression paramType = ParseType();
                    parameters.Add(item: new Parameter(Name: paramName, Type: paramType,
                        DefaultValue: null, Location: GetLocation()));
                } while (Match(type: TokenType.Comma));
            }

            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

            // Return type
            TypeExpression? returnType = null;
            if (Match(type: TokenType.Arrow))
            {
                returnType = ParseType();
            }

            methods.Add(item: new FunctionSignature(Name: methodName, Parameters: parameters,
                ReturnType: returnType, Location: GetLocation()));

            Match(type: TokenType.Newline);
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after feature body");

        return new FeatureDeclaration(Name: name, GenericParameters: genericParams,
            Methods: methods, Visibility: visibility, Location: location);
    }

    private IfStatement ParseIfStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? thenBranch = ParseStatement();
        Statement? elseBranch = null;

        // Handle elif chain
        while (Match(type: TokenType.Elif))
        {
            Expression elifCondition = ParseExpression();
            Statement? elifBranch = ParseStatement();

            // Convert elif to nested if-else
            var nestedIf = new IfStatement(Condition: elifCondition, ThenStatement: elifBranch,
                ElseStatement: null, Location: GetLocation(token: PeekToken(offset: -1)));

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
                        ThenStatement: prevIf.ThenStatement, ElseStatement: nestedIf,
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
                        : current.ElseStatement, Location: lastIf.Location);
            }
        }

        return new IfStatement(Condition: condition, ThenStatement: thenBranch,
            ElseStatement: elseBranch, Location: location);
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
        var negatedCondition = new UnaryExpression(Operator: UnaryOperator.Not, Operand: condition,
            Location: condition.Location);

        return new IfStatement(Condition: negatedCondition, ThenStatement: thenBranch,
            ElseStatement: elseBranch, Location: location);
    }

    private WhileStatement ParseWhileStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression condition = ParseExpression();
        Statement? body = ParseStatement();

        return new WhileStatement(Condition: condition, Body: body, Location: location);
    }

    private ForStatement ParseForStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string variable = ConsumeIdentifier(errorMessage: "Expected variable name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in for loop");
        Expression iterable = ParseExpression();
        Statement? body = ParseStatement();

        return new ForStatement(Variable: variable, Iterable: iterable, Body: body,
            Location: location);
    }

    private WhenStatement ParseWhenStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        Expression expression = ParseExpression();

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after when expression");

        var clauses = new List<WhenClause>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            // Skip newlines
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            Pattern pattern = ParsePattern();
            Consume(type: TokenType.FatArrow, errorMessage: "Expected '=>' after pattern");
            Statement? body = ParseStatement();

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
            return new LiteralPattern(Value: literal.Value, Location: location);
        }

        throw new ParseException(message: $"Expected pattern, got {CurrentToken.Type}");
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

    private ExpressionStatement ParseExpressionStatement()
    {
        Expression expr = ParseExpression();
        ConsumeStatementTerminator();
        return new ExpressionStatement(Expression: expr, Location: expr.Location);
    }

    private Expression ParseExpression()
    {
        return ParseAssignment();
    }

    private Expression ParseAssignment()
    {
        Expression expr = ParseTernary();

        if (Match(type: TokenType.Assign))
        {
            Expression value = ParseAssignment();
            // For now, treat assignment as a binary expression
            return new BinaryExpression(Left: expr, Operator: BinaryOperator.Assign, Right: value,
                Location: expr.Location);
        }

        return expr;
    }

    private Expression ParseTernary()
    {
        Expression expr = ParseLogicalOr();

        if (Match(type: TokenType.Question))
        {
            Expression thenExpr = ParseExpression();
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' in ternary expression");
            Expression elseExpr = ParseExpression();
            return new ConditionalExpression(Condition: expr, TrueExpression: thenExpr,
                FalseExpression: elseExpr, Location: expr.Location);
        }

        return expr;
    }

    private Expression ParseLogicalOr()
    {
        Expression expr = ParseRange();

        while (Match(type: TokenType.Or))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseRange();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseRange()
    {
        Expression expr = ParseLogicalAnd();

        // Handle range expressions: A to B or A to B step C
        if (Match(type: TokenType.To))
        {
            Expression end = ParseLogicalAnd();
            Expression? step = null;

            if (Match(type: TokenType.Step))
            {
                step = ParseLogicalAnd();
            }

            // Create a range expression - for now use a call expression to represent it
            var args = new List<Expression> { expr, end };
            if (step != null)
            {
                args.Add(item: step);
            }

            return new CallExpression(
                Callee: new IdentifierExpression(Name: "range", Location: expr.Location),
                Arguments: args, Location: expr.Location);
        }

        return expr;
    }

    private Expression ParseLogicalAnd()
    {
        Expression expr = ParseEquality();

        while (Match(type: TokenType.And))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseEquality();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseEquality()
    {
        Expression expr = ParseComparison();

        while (Match(TokenType.Equal, TokenType.NotEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseComparison();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseComparison()
    {
        Expression expr = ParseIsExpression();

        // Check for chained comparisons (a < b < c)
        var operators = new List<BinaryOperator>();
        var operands = new List<Expression> { expr };

        while (Match(TokenType.Less, TokenType.LessEqual, TokenType.Greater,
                   TokenType.GreaterEqual, TokenType.Equal, TokenType.NotEqual))
        {
            Token op = PeekToken(offset: -1);
            operators.Add(item: TokenToBinaryOperator(tokenType: op.Type));

            Expression right = ParseIsExpression();
            operands.Add(item: right);
        }

        // If we have chained comparisons, create a ChainedComparisonExpression
        if (operators.Count > 1)
        {
            return new ChainedComparisonExpression(Operands: operands, Operators: operators,
                Location: GetLocation());
        }
        else if (operators.Count == 1)
        {
            // Single comparison, create regular BinaryExpression
            return new BinaryExpression(Left: operands[index: 0], Operator: operators[index: 0],
                Right: operands[index: 1], Location: GetLocation());
        }

        return expr;
    }

    private Expression ParseIsExpression()
    {
        Expression expr = ParseBitwiseOr();

        // Handle is/from/follows expressions when not in entity context
        while (Match(TokenType.Is, TokenType.From, TokenType.Follows))
        {
            Token op = PeekToken(offset: -1);
            SourceLocation location = GetLocation(token: op);

            if (op.Type == TokenType.Is)
            {
                // Handle is expressions: expr is Type or expr is Type(pattern)
                TypeExpression type = ParseType();

                // Check if it's a pattern with variable binding
                string? variableName = null;
                if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
                {
                    variableName = PeekToken(offset: -1)
                       .Text;
                }

                // For now, represent as a call expression
                var args = new List<Expression> { expr };
                if (variableName != null)
                {
                    args.Add(
                        item: new IdentifierExpression(Name: variableName, Location: location));
                }

                expr = new CallExpression(
                    Callee: new IdentifierExpression(Name: $"is_{type.Name}", Location: location),
                    Arguments: args, Location: location);
            }
            else
            {
                // Handle from/follows as comparison operators
                Expression right = ParseBitwiseOr();
                string operatorName = op.Type == TokenType.From
                    ? "from"
                    : "follows";

                expr = new CallExpression(
                    Callee: new IdentifierExpression(Name: operatorName, Location: location),
                    Arguments: new List<Expression> { expr, right }, Location: location);
            }
        }

        return expr;
    }

    private Expression ParseBitwiseOr()
    {
        Expression expr = ParseBitwiseXor();

        while (Match(type: TokenType.Pipe))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseXor();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseBitwiseXor()
    {
        Expression expr = ParseBitwiseAnd();

        while (Match(type: TokenType.Caret))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseBitwiseAnd();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseBitwiseAnd()
    {
        Expression expr = ParseShift();

        while (Match(type: TokenType.Ampersand))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseShift();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseShift()
    {
        Expression expr = ParseAdditive();

        while (Match(TokenType.LeftShift, TokenType.RightShift))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseAdditive();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseAdditive()
    {
        Expression expr = ParseMultiplicative();

        while (Match(TokenType.Plus, TokenType.Minus, TokenType.PlusWrap, TokenType.PlusSaturate,
                   TokenType.PlusUnchecked, TokenType.PlusChecked, TokenType.MinusWrap,
                   TokenType.MinusSaturate, TokenType.MinusUnchecked, TokenType.MinusChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseMultiplicative();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseMultiplicative()
    {
        Expression expr = ParsePower();

        while (Match(TokenType.Star, TokenType.Slash, TokenType.Percent, TokenType.Divide,
                   TokenType.MultiplyWrap, TokenType.MultiplySaturate, TokenType.MultiplyUnchecked,
                   TokenType.MultiplyChecked, TokenType.DivideWrap, TokenType.DivideSaturate,
                   TokenType.DivideUnchecked, TokenType.DivideChecked, TokenType.ModuloWrap,
                   TokenType.ModuloSaturate, TokenType.ModuloUnchecked, TokenType.ModuloChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParsePower();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParsePower()
    {
        Expression expr = ParseUnary();

        while (Match(TokenType.Power, TokenType.PowerWrap, TokenType.PowerSaturate,
                   TokenType.PowerUnchecked, TokenType.PowerChecked))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseUnary();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
        }

        return expr;
    }

    private Expression ParseUnary()
    {
        if (Match(TokenType.Plus, TokenType.Minus, TokenType.Bang, TokenType.Not, TokenType.Tilde))
        {
            Token op = PeekToken(offset: -1);
            Expression expr = ParseUnary();
            return new UnaryExpression(Operator: TokenToUnaryOperator(tokenType: op.Type),
                Operand: expr, Location: GetLocation(token: op));
        }

        return ParsePostfix();
    }

    private Expression ParsePostfix()
    {
        Expression expr = ParsePrimary();

        while (true)
        {
            // Handle standalone generic function calls like routine<T>!(args)
            if (expr is IdentifierExpression && Check(type: TokenType.Less))
            {
                Advance(); // consume '<'
                var typeArgs = new List<TypeExpression>();
                do
                {
                    typeArgs.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.Greater,
                    errorMessage: "Expected '>' after generic type arguments");

                // Check for memory operation with !
                bool isMemoryOperation = Match(type: TokenType.Bang);

                if (Match(type: TokenType.LeftParen))
                {
                    var args = new List<Expression>();
                    if (!Check(type: TokenType.RightParen))
                    {
                        do
                        {
                            args.Add(item: ParseExpression());
                        } while (Match(type: TokenType.Comma));
                    }

                    Consume(type: TokenType.RightParen,
                        errorMessage: "Expected ')' after arguments");

                    expr = new GenericMethodCallExpression(Object: expr,
                        MethodName: ((IdentifierExpression)expr).Name, TypeArguments: typeArgs,
                        Arguments: args, IsMemoryOperation: isMemoryOperation,
                        Location: expr.Location);
                }
                else
                {
                    // Generic type reference without call
                    expr = new GenericMemberExpression(Object: expr,
                        MemberName: ((IdentifierExpression)expr).Name, TypeArguments: typeArgs,
                        Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.LeftParen))
            {
                // Function call
                var args = new List<Expression>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        args.Add(item: ParseExpression());
                    } while (Match(type: TokenType.Comma));
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after arguments");

                // Check if this is a slice constructor call
                if (expr is IdentifierExpression identifier &&
                    (identifier.Name == "DynamicSlice" || identifier.Name == "TemporarySlice") &&
                    args.Count == 1)
                {
                    expr = new SliceConstructorExpression(SliceType: identifier.Name,
                        SizeExpression: args[index: 0], Location: expr.Location);
                }
                else
                {
                    expr = new CallExpression(Callee: expr, Arguments: args,
                        Location: expr.Location);
                }
            }
            else if (Match(type: TokenType.LeftBracket))
            {
                // Array/map indexing
                Expression index = ParseExpression();
                Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after index");
                expr = new IndexExpression(Object: expr, Index: index, Location: expr.Location);
            }
            else if (Match(type: TokenType.Dot))
            {
                // Member access
                string member = ConsumeIdentifier(errorMessage: "Expected member name after '.'");

                // Check for generic method call with type parameters
                if (Match(type: TokenType.Less))
                {
                    var typeArgs = new List<TypeExpression>();
                    do
                    {
                        typeArgs.Add(item: ParseType());
                    } while (Match(type: TokenType.Comma));

                    Consume(type: TokenType.Greater,
                        errorMessage: "Expected '>' after generic type arguments");

                    // Check for method call with !
                    bool isMemoryOperation = Match(type: TokenType.Bang);

                    if (Match(type: TokenType.LeftParen))
                    {
                        var args = new List<Expression>();
                        if (!Check(type: TokenType.RightParen))
                        {
                            do
                            {
                                args.Add(item: ParseExpression());
                            } while (Match(type: TokenType.Comma));
                        }

                        Consume(type: TokenType.RightParen,
                            errorMessage: "Expected ')' after arguments");

                        expr = new GenericMethodCallExpression(Object: expr, MethodName: member,
                            TypeArguments: typeArgs, Arguments: args,
                            IsMemoryOperation: isMemoryOperation, Location: expr.Location);
                    }
                    else
                    {
                        expr = new GenericMemberExpression(Object: expr, MemberName: member,
                            TypeArguments: typeArgs, Location: expr.Location);
                    }
                }
                else
                {
                    // Check for memory operation with !
                    bool isMemoryOperation = Match(type: TokenType.Bang);

                    if (isMemoryOperation && Match(type: TokenType.LeftParen))
                    {
                        var args = new List<Expression>();
                        if (!Check(type: TokenType.RightParen))
                        {
                            do
                            {
                                args.Add(item: ParseExpression());
                            } while (Match(type: TokenType.Comma));
                        }

                        Consume(type: TokenType.RightParen,
                            errorMessage: "Expected ')' after arguments");

                        expr = new MemoryOperationExpression(Object: expr, OperationName: member,
                            Arguments: args, Location: expr.Location);
                    }
                    else
                    {
                        expr = new MemberExpression(Object: expr, PropertyName: member,
                            Location: expr.Location);
                    }
                }
            }
            else
            {
                break;
            }
        }

        return expr;
    }

    private Expression ParsePrimary()
    {
        SourceLocation location = GetLocation();

        // Literals
        if (Match(type: TokenType.True))
        {
            return new LiteralExpression(Value: true, LiteralType: TokenType.True,
                Location: location);
        }

        if (Match(type: TokenType.False))
        {
            return new LiteralExpression(Value: false, LiteralType: TokenType.False,
                Location: location);
        }

        if (Match(type: TokenType.None))
        {
            return new LiteralExpression(Value: null!, LiteralType: TokenType.None,
                Location: location);
        }

        // Integer literals
        if (Match(TokenType.Integer, TokenType.S8Literal, TokenType.S16Literal,
                TokenType.S32Literal, TokenType.S64Literal, TokenType.S128Literal,
                TokenType.SyssintLiteral, TokenType.U8Literal, TokenType.U16Literal,
                TokenType.U32Literal, TokenType.U64Literal, TokenType.U128Literal,
                TokenType.SysuintLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            string cleanValue = value.Replace(oldValue: "s8", newValue: "")
                                     .Replace(oldValue: "s16", newValue: "")
                                     .Replace(oldValue: "s32", newValue: "")
                                     .Replace(oldValue: "s64", newValue: "")
                                     .Replace(oldValue: "s128", newValue: "")
                                     .Replace(oldValue: "syssint", newValue: "")
                                     .Replace(oldValue: "u8", newValue: "")
                                     .Replace(oldValue: "u16", newValue: "")
                                     .Replace(oldValue: "u32", newValue: "")
                                     .Replace(oldValue: "u64", newValue: "")
                                     .Replace(oldValue: "u128", newValue: "")
                                     .Replace(oldValue: "sysuint", newValue: "");

            long intVal;
            // Handle hexadecimal literals (0x prefix)
            if (cleanValue.StartsWith(value: "0x") || cleanValue.StartsWith(value: "0X"))
            {
                string hexPart = cleanValue.Substring(startIndex: 2); // Remove "0x" prefix
                if (long.TryParse(s: hexPart, style: System.Globalization.NumberStyles.HexNumber,
                        provider: null, result: out intVal))
                {
                    return new LiteralExpression(Value: intVal, LiteralType: token.Type,
                        Location: location);
                }
            }
            // Handle binary literals (0b prefix)
            else if (cleanValue.StartsWith(value: "0b") || cleanValue.StartsWith(value: "0B"))
            {
                string binaryPart = cleanValue.Substring(startIndex: 2); // Remove "0b" prefix
                try
                {
                    intVal = Convert.ToInt64(value: binaryPart, fromBase: 2);
                    return new LiteralExpression(Value: intVal, LiteralType: token.Type,
                        Location: location);
                }
                catch (Exception)
                {
                    throw new ParseException(message: $"Invalid binary literal: {value}");
                }
            }
            // Handle decimal literals
            else if (long.TryParse(s: cleanValue, result: out intVal))
            {
                return new LiteralExpression(Value: intVal, LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid integer literal: {value}");
        }

        // Float literals
        if (Match(TokenType.Decimal, TokenType.F16Literal, TokenType.F32Literal,
                TokenType.F64Literal, TokenType.F128Literal, TokenType.D32Literal,
                TokenType.D64Literal, TokenType.D128Literal))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            string cleanValue = value.Replace(oldValue: "f16", newValue: "")
                                     .Replace(oldValue: "f32", newValue: "")
                                     .Replace(oldValue: "f64", newValue: "")
                                     .Replace(oldValue: "f128", newValue: "")
                                     .Replace(oldValue: "d32", newValue: "")
                                     .Replace(oldValue: "d64", newValue: "")
                                     .Replace(oldValue: "d128", newValue: "");
            if (double.TryParse(s: cleanValue, result: out double floatVal))
            {
                return new LiteralExpression(Value: floatVal, LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid float literal: {value}");
        }

        // String literals
        if (Match(TokenType.TextLiteral, TokenType.FormattedText, TokenType.RawText,
                TokenType.RawFormattedText, TokenType.Text8Literal, TokenType.Text8FormattedText,
                TokenType.Text8RawText, TokenType.Text8RawFormattedText, TokenType.Text16Literal,
                TokenType.Text16FormattedText, TokenType.Text16RawText,
                TokenType.Text16RawFormattedText))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;

            // Handle RazorForge's formatted string literals (f"...")
            if (value.StartsWith(value: "f\"") && value.EndsWith(value: "\""))
            {
                // This is a formatted string like f"{name} says hello"
                // For now, treat as regular string but mark as formatted
                value = value.Substring(startIndex: 2,
                    length: value.Length - 3); // Remove f" and "
                return new LiteralExpression(Value: value, LiteralType: TokenType.FormattedText,
                    Location: location);
            }

            // Regular string literals - strip quotes and prefixes
            if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
            {
                value = value.Substring(startIndex: 1, length: value.Length - 2);
            }

            return new LiteralExpression(Value: value, LiteralType: token.Type,
                Location: location);
        }

        // Character literals
        if (Match(TokenType.Letter8Literal, TokenType.Letter16Literal, TokenType.LetterLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;

            try
            {
                // Handle different prefixes: l8'a', l16'a', 'a'
                int quoteStart = value.LastIndexOf(value: '\'');
                int quoteEnd = value.Length - 1;

                if (quoteStart >= 0 && quoteEnd > quoteStart && value[index: quoteEnd] == '\'')
                {
                    string charContent = value.Substring(startIndex: quoteStart + 1,
                        length: quoteEnd - quoteStart - 1);

                    // Handle escape sequences
                    if (charContent == "\\'") // Single quote
                    {
                        return new LiteralExpression(Value: '\'', LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\\\") // Backslash
                    {
                        return new LiteralExpression(Value: '\\', LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\n") // Newline
                    {
                        return new LiteralExpression(Value: '\n', LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\t") // Tab
                    {
                        return new LiteralExpression(Value: '\t', LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent == "\\r") // Carriage return
                    {
                        return new LiteralExpression(Value: '\r', LiteralType: token.Type,
                            Location: location);
                    }
                    else if (charContent.Length == 1)
                    {
                        return new LiteralExpression(Value: charContent[index: 0],
                            LiteralType: token.Type, Location: location);
                    }
                }
            }
            catch
            {
                // Fall through to error
            }

            // For now, just return a placeholder for invalid character literals
            return new LiteralExpression(Value: '?', LiteralType: token.Type, Location: location);
        }

        // Memory size literals
        if (Match(TokenType.ByteLiteral, TokenType.KilobyteLiteral, TokenType.KibibyteLiteral,
                TokenType.KilobitLiteral, TokenType.KibibitLiteral, TokenType.MegabyteLiteral,
                TokenType.MebibyteLiteral, TokenType.MegabitLiteral, TokenType.MebibitLiteral,
                TokenType.GigabyteLiteral, TokenType.GibibyteLiteral, TokenType.GigabitLiteral,
                TokenType.GibibitLiteral, TokenType.TerabyteLiteral, TokenType.TebibyteLiteral,
                TokenType.TerabitLiteral, TokenType.TebibitLiteral, TokenType.PetabyteLiteral,
                TokenType.PebibyteLiteral, TokenType.PetabitLiteral, TokenType.PebibitLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            // Extract numeric part from memory literals like "100mb", "4gb", etc.
            string numericPart = new(value: value.TakeWhile(predicate: char.IsDigit)
                                                 .ToArray());
            if (long.TryParse(s: numericPart, result: out long memoryVal))
            {
                return new LiteralExpression(Value: memoryVal, LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid memory literal: {value}");
        }

        // Duration/time literals
        if (Match(TokenType.WeekLiteral, TokenType.DayLiteral, TokenType.HourLiteral,
                TokenType.MinuteLiteral, TokenType.SecondLiteral, TokenType.MillisecondLiteral,
                TokenType.MicrosecondLiteral, TokenType.NanosecondLiteral))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;
            // Extract numeric part from time literals like "30m", "24h", "500ms", etc.
            string numericPart = new(value: value.TakeWhile(predicate: char.IsDigit)
                                                 .ToArray());
            if (long.TryParse(s: numericPart, result: out long timeVal))
            {
                return new LiteralExpression(Value: timeVal, LiteralType: token.Type,
                    Location: location);
            }

            throw new ParseException(message: $"Invalid time literal: {value}");
        }

        // Identifiers
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            return new IdentifierExpression(Name: PeekToken(offset: -1)
               .Text, Location: location);
        }

        // Parenthesized expression
        if (Match(type: TokenType.LeftParen))
        {
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
        }

        // Lambda expression
        if (Match(type: TokenType.Routine))
        {
            return ParseLambdaExpression(location: location);
        }

        // Conditional expression: if A then B else C
        if (Match(type: TokenType.If))
        {
            Expression condition = ParseExpression();
            Consume(type: TokenType.Then,
                errorMessage: "Expected 'then' in conditional expression");
            Expression thenExpr = ParseExpression();
            Consume(type: TokenType.Else,
                errorMessage: "Expected 'else' in conditional expression");
            Expression elseExpr = ParseExpression();
            return new ConditionalExpression(Condition: condition, TrueExpression: thenExpr,
                FalseExpression: elseExpr, Location: location);
        }

        throw new ParseException(
            message:
            $"Unexpected token: {CurrentToken.Type} at line {CurrentToken.Line}, column {CurrentToken.Column}");
    }

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

                parameters.Add(item: new Parameter(Name: paramName, Type: paramType,
                    DefaultValue: defaultValue, Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after lambda parameters");

        // Body - for now just parse expression (block lambdas would need special handling)
        Expression body = ParseExpression();

        return new LambdaExpression(Parameters: parameters, Body: body, Location: location);
    }

    private TypeExpression ParseType()
    {
        SourceLocation location = GetLocation();

        // Basic types - use identifiers for type names
        // RazorForge doesn't have specific type tokens, just identifier tokens for types

        // Named type (identifier or type identifier)
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string name = PeekToken(offset: -1)
               .Text;

            // Check for generic type arguments
            if (Match(type: TokenType.Less))
            {
                var typeArgs = new List<TypeExpression>();

                do
                {
                    typeArgs.Add(item: ParseType());
                } while (Match(type: TokenType.Comma));

                Consume(type: TokenType.Greater,
                    errorMessage: "Expected '>' after type arguments");

                return new TypeExpression(Name: name, GenericArguments: typeArgs,
                    Location: location);
            }

            return new TypeExpression(Name: name, GenericArguments: null, Location: location);
        }

        throw new ParseException(
            message: $"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
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

        Token current = CurrentToken;
        throw new ParseException(
            message: $"{errorMessage} at line {current.Line}, column {current.Column}. " +
                     $"Expected Identifier or TypeIdentifier, got {current.Type}.");
    }

    private ExternalDeclaration ParseExternalDeclaration(string? callingConvention = null)
    {
        SourceLocation
            location =
                GetLocation(
                    token: PeekToken(
                        offset: -2)); // -2 because we consumed 'external' and 'routine'

        string name = ConsumeIdentifier(errorMessage: "Expected function name");

        // Check for generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(
                    item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater,
                errorMessage: "Expected '>' after generic parameters");
        }

        // Handle external functions with ! suffix (memory operations)
        Match(type: TokenType.Bang);

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after function name");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
                TypeExpression paramType = ParseType();
                parameters.Add(item: new Parameter(Name: paramName, Type: paramType,
                    DefaultValue: null, Location: GetLocation()));
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // Return type
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        ConsumeStatementTerminator();

        // Default to "C" calling convention if not specified
        string effectiveCallingConvention = callingConvention ?? "C";

        return new ExternalDeclaration(Name: name, GenericParameters: genericParams,
            Parameters: parameters, ReturnType: returnType,
            CallingConvention: effectiveCallingConvention, Location: location);
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

        return new ViewingStatement(Source: source, Handle: handle, Body: body,
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

        return new HijackingStatement(Source: source, Handle: handle, Body: body,
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

        return new ObservingStatement(Source: source, Handle: handle, Body: body,
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

        return new SeizingStatement(Source: source, Handle: handle, Body: body,
            Location: location);
    }

    private SliceConstructorExpression ParseSliceConstructor()
    {
        SourceLocation location = GetLocation();
        string typeName = ConsumeIdentifier(errorMessage: "Expected slice type name");

        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after slice type");
        Expression sizeExpr = ParseExpression();
        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after slice size");

        return new SliceConstructorExpression(SliceType: typeName, SizeExpression: sizeExpr,
            Location: location);
    }
}
