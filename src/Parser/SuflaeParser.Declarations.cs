using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing declaration parsing (variables, routines, entities, records, variants, etc.).
/// </summary>
public partial class SuflaeParser
{
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

        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            IsMutable: isMutable,
            Location: location);
    }

    private FunctionDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support ! suffix for failable functions
        if (Match(type: TokenType.Bang))
        {
            name = name + "!";
        }

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

                parameters.Add(item: new Parameter(Name: paramName,
                    Type: paramType,
                    DefaultValue: defaultValue,
                    Location: GetLocation()));
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

        return new FunctionDeclaration(Name: name,
            Parameters: parameters,
            ReturnType: returnType,
            Body: body,
            Visibility: visibility,
            Attributes: new List<string>(),
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

        return new ClassDeclaration(Name: name,
            GenericParameters: null,
            BaseClass: baseClass,
            Interfaces: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
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

        return new StructDeclaration(Name: name,
            GenericParameters: null,
            Members: members,
            Visibility: visibility,
            Location: location);
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

                    variants.Add(item: new EnumVariant(Name: variantName,
                        Value: value,
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

        return new MenuDeclaration(Name: name,
            Variants: variants,
            Methods: methods,
            Visibility: visibility,
            Location: location);
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
                        AssociatedTypes: associatedTypes,
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
                throw new ParseException(message: "Expected dedent after variant body");
            }
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            Cases: cases,
            Methods: methods,
            Visibility: visibility,
            Kind: kind,
            Location: location);
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
                        parameters.Add(item: new Parameter(Name: paramName,
                            Type: paramType,
                            DefaultValue: null,
                            Location: GetLocation()));
                    } while (Match(type: TokenType.Comma));
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

                // Return type
                TypeExpression? returnType = null;
                if (Match(type: TokenType.Arrow))
                {
                    returnType = ParseType();
                }

                methods.Add(item: new FunctionSignature(Name: methodName,
                    Parameters: parameters,
                    ReturnType: returnType,
                    Location: GetLocation()));
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

        return new FeatureDeclaration(Name: name,
            GenericParameters: null,
            Methods: methods,
            Visibility: visibility,
            Location: location);
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

        return new ImplementationDeclaration(Type: type,
            Trait: trait,
            Methods: methods,
            Location: location);
    }

    private NamespaceDeclaration ParseNamespaceDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string namespacePath = "";

        // Parse namespace path - could be multiple identifiers separated by slashes
        // e.g., namespace standard/errors
        do
        {
            string part = ConsumeIdentifier(errorMessage: "Expected namespace name");
            namespacePath += part;
            if (Match(type: TokenType.Slash))
            {
                namespacePath += "/";
            }
            else
            {
                break;
            }
        } while (true);

        ConsumeStatementTerminator();

        return new NamespaceDeclaration(Path: namespacePath, Location: location);
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

        return new ImportDeclaration(ModulePath: modulePath,
            Alias: alias,
            SpecificImports: specificImports,
            Location: location);
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
}
