using Compilers.Shared.AST;
using Compilers.Shared.Parser;
using Compilers.Shared.Lexer;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Parser for Suflae language (indentation-based syntax)
/// Handles Python-like indentation with colons and blocks
/// </summary>
public class SuflaeParser : BaseParser
{
    private readonly Stack<int> _indentationStack = new();
    private int _currentIndentationLevel = 0;

    public SuflaeParser(List<Token> tokens) : base(tokens: tokens)
    {
        _indentationStack.Push(item: 0); // Base indentation level
    }

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

                // Handle dedent tokens (should not occur at top level, but be safe)
                if (Check(type: TokenType.Dedent))
                {
                    ProcessDedentTokens();
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

        // Variable declarations
        if (Match(TokenType.Var, TokenType.Let))
        {
            return ParseVariableDeclaration(visibility: visibility);
        }

        // Routine (function) declaration - using 'routine' keyword in Suflae
        if (Match(type: TokenType.Routine))
        {
            return ParseRoutineDeclaration(visibility: visibility);
        }

        // Entity/Record/Choice declarations
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
            return ParseChoiceDeclaration(visibility: visibility); // choice in Suflae
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

        // Implementation blocks (Type follows Trait:)
        if (CheckImplementation())
        {
            return ParseImplementationDeclaration();
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

    private bool CheckImplementation()
    {
        // Look for pattern: Identifier follows Identifier:
        if (Check(type: TokenType.Identifier) || Check(type: TokenType.TypeIdentifier))
        {
            int saved = Position;
            Advance(); // Skip type name
            if (Match(type: TokenType.Follows))
            {
                Position = saved; // Reset position
                return true;
            }

            Position = saved; // Reset position
        }

        return false;
    }

    private Statement? ParseStatement()
    {
        // Handle dedent tokens
        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }

        // Skip newlines
        while (Match(type: TokenType.Newline)) { }

        if (IsAtEnd)
        {
            return null;
        }

        // Control flow
        if (Match(type: TokenType.If))
        {
            return ParseIfStatement();
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

        // Danger and mayhem blocks
        if (Match(type: TokenType.Danger))
        {
            return ParseDangerStatement();
        }

        if (Match(type: TokenType.Mayhem))
        {
            return ParseMayhemStatement();
        }

        // Variable declarations (can appear in statement context)
        if (Match(TokenType.Var, TokenType.Let))
        {
            VariableDeclaration varDecl = ParseVariableDeclaration();
            return new ExpressionStatement(
                Expression: new IdentifierExpression(Name: $"var {varDecl.Name}",
                    Location: GetLocation()), Location: GetLocation());
        }

        // Suflae's display statement (equivalent to print/console.log)
        if (Check(type: TokenType.Identifier) && CurrentToken.Text == "display")
        {
            Advance();
            Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'display'");
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen,
                errorMessage: "Expected ')' after display expression");
            Match(type: TokenType.Newline);

            // Convert to a function call expression
            var displayCall = new CallExpression(
                Callee: new IdentifierExpression(Name: "Console.WriteLine",
                    Location: GetLocation()), Arguments: new List<Expression> { expr },
                Location: GetLocation());

            return new ExpressionStatement(Expression: displayCall, Location: GetLocation());
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

    private FunctionDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
                TypeExpression paramType = ParseType();

                Expression? defaultValue = null;
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

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after routine header");

        // Body (indented block)
        Statement body = ParseIndentedBlock();

        return new FunctionDeclaration(Name: name, Parameters: parameters, ReturnType: returnType,
            Body: body, Visibility: visibility, Attributes: new List<string>(),
            Location: location);
    }

    private ClassDeclaration ParseClassDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Base entity - can use "from Animal" syntax
        TypeExpression? baseClass = null;
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.From))
        {
            baseClass = ParseType();
        }

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after entity header");

        var members = new List<Declaration>();

        // Parse indented members
        if (Match(type: TokenType.Indent))
        {
            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
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

            if (!Match(type: TokenType.Dedent))
            {
                // If no dedent token, we might be at end of file
                if (!IsAtEnd)
                {
                    throw new ParseException(message: "Expected dedent after entity body");
                }
            }
        }

        return new ClassDeclaration(Name: name, GenericParameters: null, BaseClass: baseClass,
            Interfaces: interfaces, Members: members, Visibility: visibility, Location: location);
    }

    private StructDeclaration ParseStructDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after record header");

        var members = new List<Declaration>();

        // Parse record body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
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

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException(message: "Expected dedent after record body");
            }
        }

        return new StructDeclaration(Name: name, GenericParameters: null, Members: members,
            Visibility: visibility, Location: location);
    }

    private MenuDeclaration ParseChoiceDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected option name");

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after option header");

        var variants = new List<EnumVariant>();
        var methods = new List<FunctionDeclaration>();

        // Parse option body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Check if it's a method (routine)
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
                    // Parse enum variant
                    string variantName =
                        ConsumeIdentifier(errorMessage: "Expected option variant name");

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
                    Match(type: TokenType.Newline);
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException(message: "Expected dedent after option body");
            }
        }

        return new MenuDeclaration(Name: name, Variants: variants, Methods: methods,
            Visibility: visibility, Location: location);
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

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after variant header");

        var cases = new List<VariantCase>();
        var methods = new List<FunctionDeclaration>();

        // Parse variant body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Check if it's a method (routine)
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
                    string caseName =
                        ConsumeIdentifier(errorMessage: "Expected variant case name");

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

                    cases.Add(item: new VariantCase(Name: caseName,
                        AssociatedTypes: associatedTypes, Location: GetLocation()));
                    Match(type: TokenType.Newline);
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException(message: "Expected dedent after variant body");
            }
        }

        return new VariantDeclaration(Name: name, GenericParameters: genericParams, Cases: cases,
            Methods: methods, Visibility: visibility, Kind: kind, Location: location);
    }

    private FeatureDeclaration ParseFeatureDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected feature name");

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after feature header");

        var methods = new List<FunctionSignature>();

        // Parse feature body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                // Parse routine signature
                Consume(type: TokenType.Routine,
                    errorMessage: "Expected 'routine' in feature method");
                string methodName = ConsumeIdentifier(errorMessage: "Expected method name");

                // Parameters
                Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after method name");
                var parameters = new List<Parameter>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        string paramName =
                            ConsumeIdentifier(errorMessage: "Expected parameter name");
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

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException(message: "Expected dedent after feature body");
            }
        }

        return new FeatureDeclaration(Name: name, GenericParameters: null, Methods: methods,
            Visibility: visibility, Location: location);
    }

    private ImplementationDeclaration ParseImplementationDeclaration()
    {
        SourceLocation location = GetLocation();

        TypeExpression type = ParseType();
        Consume(type: TokenType.Follows, errorMessage: "Expected 'follows' in implementation");
        TypeExpression trait = ParseType();

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after implementation header");

        var methods = new List<FunctionDeclaration>();

        // Parse implementation body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(type: TokenType.Newline))
                {
                    continue;
                }

                var method = ParseDeclaration() as FunctionDeclaration;
                if (method != null)
                {
                    methods.Add(item: method);
                }
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw new ParseException(message: "Expected dedent after implementation body");
            }
        }

        return new ImplementationDeclaration(Type: type, Trait: trait, Methods: methods,
            Location: location);
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
        // Handle Suflae's special visibility syntax: public(family), public(module)
        if (Match(type: TokenType.Public))
        {
            if (Match(type: TokenType.LeftParen))
            {
                string modifier = ConsumeIdentifier(errorMessage: "Expected visibility modifier");
                Consume(type: TokenType.RightParen,
                    errorMessage: "Expected ')' after visibility modifier");

                return modifier switch
                {
                    "family" => VisibilityModifier.Protected,
                    "module" => VisibilityModifier.Internal,
                    _ => throw new ParseException(
                        message: $"Unknown visibility modifier: {modifier}")
                };
            }

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

        return new IfStatement(Condition: condition, ThenStatement: thenBranch,
            ElseStatement: elseBranch, Location: location);
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

        return new ForStatement(Variable: variable, Iterable: iterable, Body: body,
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
            return new LiteralPattern(Value: literal.Value, Location: location);
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

    private Statement ParseDangerStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after 'danger'");
        var body = (BlockStatement)ParseIndentedBlock();
        return new DangerStatement(Body: body, Location: location);
    }

    private Statement ParseMayhemStatement()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after 'mayhem'");
        var body = (BlockStatement)ParseIndentedBlock();
        return new MayhemStatement(Body: body, Location: location);
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

        while (Match(TokenType.Less, TokenType.LessEqual, TokenType.Greater,
                   TokenType.GreaterEqual))
        {
            Token op = PeekToken(offset: -1);
            Expression right = ParseIsExpression();
            expr = new BinaryExpression(Left: expr,
                Operator: TokenToBinaryOperator(tokenType: op.Type), Right: right,
                Location: GetLocation(token: op));
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
            if (Match(type: TokenType.LeftParen))
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
                expr = new CallExpression(Callee: expr, Arguments: args, Location: expr.Location);
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
                expr = new MemberExpression(Object: expr, PropertyName: member,
                    Location: expr.Location);
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
            if (long.TryParse(s: cleanValue, result: out long intVal))
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

        // String literals with Suflae-specific handling
        if (Match(TokenType.TextLiteral, TokenType.FormattedText, TokenType.RawText,
                TokenType.RawFormattedText, TokenType.Text8Literal, TokenType.Text8FormattedText,
                TokenType.Text8RawText, TokenType.Text8RawFormattedText, TokenType.Text16Literal,
                TokenType.Text16FormattedText, TokenType.Text16RawText,
                TokenType.Text16RawFormattedText))
        {
            Token token = PeekToken(offset: -1);
            string value = token.Text;

            // Handle Suflae's formatted string literals (f"...")
            if (value.StartsWith(value: "f\"") && value.EndsWith(value: "\""))
            {
                // This is a formatted string like f"{name} says hello"
                // For now, treat as regular string but mark as formatted
                value = value.Substring(startIndex: 2,
                    length: value.Length - 3); // Remove f" and "
                return new LiteralExpression(Value: value, LiteralType: TokenType.FormattedText,
                    Location: location);
            }

            // Regular string literals
            if (value.StartsWith(value: "\"") && value.EndsWith(value: "\""))
            {
                value = value.Substring(startIndex: 1, length: value.Length - 2);
            }

            // Handle text encoding prefixes (t8", t16", t32")
            if (value.StartsWith(value: "t8\"") || value.StartsWith(value: "t16\"") ||
                value.StartsWith(value: "t32\""))
            {
                int prefixEnd = value.IndexOf(value: '"');
                if (prefixEnd > 0)
                {
                    value = value.Substring(startIndex: prefixEnd + 1,
                        length: value.Length - prefixEnd - 2);
                }
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

        // Identifiers and Suflae-specific keywords
        if (Match(TokenType.Identifier, TokenType.TypeIdentifier))
        {
            string text = PeekToken(offset: -1)
               .Text;

            // Handle Suflae's "me" keyword for self-reference
            if (text == "me")
            {
                return new IdentifierExpression(Name: "this",
                    Location: location); // Map to C# "this"
            }

            return new IdentifierExpression(Name: text, Location: location);
        }

        // Parenthesized expression
        if (Match(type: TokenType.LeftParen))
        {
            Expression expr = ParseExpression();
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after expression");
            return expr;
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

    private TypeExpression ParseType()
    {
        SourceLocation location = GetLocation();

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

        throw new ParseException(message: $"Expected type, got {CurrentToken.Type}");
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
            message: $"{errorMessage} at line {current.Line}, column {current.Column}. " +
                     $"Expected Identifier, got {current.Type}.");
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
