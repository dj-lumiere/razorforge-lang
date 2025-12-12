using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing declaration parsing (variable, function, class, struct, variant, etc.).
/// </summary>
public partial class RazorForgeParser
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

    /// <summary>
    /// Parses a field declaration in records: public name: Type or name: Type
    /// Fields are declared without var/let keywords.
    /// </summary>
    private VariableDeclaration ParseFieldDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation();

        string name = ConsumeIdentifier(errorMessage: "Expected field name");

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name");
        TypeExpression type = ParseType();

        Expression? initializer = null;
        if (Match(type: TokenType.Assign))
        {
            initializer = ParseExpression();
        }

        ConsumeStatementTerminator();

        // Fields are not mutable by default (use 'var' keyword for mutable fields if needed)
        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            IsMutable: false,
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
        string? alias = null;
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

    /// <summary>
    /// Parses a preset declaration: preset name: Type = value
    /// Preset is a compile-time constant.
    /// </summary>
    private PresetDeclaration ParsePresetDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected preset name");
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after preset name");
        TypeExpression type = ParseType();
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' after preset type");
        Expression value = ParseExpression();

        ConsumeStatementTerminator();

        return new PresetDeclaration(Name: name, Type: type, Value: value, Location: location);
    }

    private VisibilityModifier ParseVisibilityModifier()
    {
        if (Match(type: TokenType.Public))
        {
            return VisibilityModifier.Public;
        }

        if (Match(type: TokenType.Family))
        {
            return VisibilityModifier.Family;
        }

        if (Match(type: TokenType.Local))
        {
            return VisibilityModifier.Local;
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

    /// <summary>
    /// Parses attributes like @crash_only, @inline, etc.
    /// Attributes are prefixed with @ and followed by an identifier.
    /// </summary>
    private List<string> ParseAttributes()
    {
        var attributes = new List<string>();

        while (Match(type: TokenType.At))
        {
            string attrName = ConsumeIdentifier(errorMessage: "Expected attribute name after '@'");
            attributes.Add(item: attrName);
        }

        return attributes;
    }

    private FunctionDeclaration ParseFunctionDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private,
        List<string>? attributes = null,
        bool allowNoBody = false)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected function name");

        // Support generic type in name like Text<T>.method (generics BEFORE dot)
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            var result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");

            // After generic params, check for method syntax: Type<T>.method or Type<T>.method<U>
            if (Match(type: TokenType.Dot))
            {
                string methodName =
                    ConsumeMethodName(errorMessage: "Expected method name after '.'");
                name = name + "<" + string.Join(separator: ", ", values: genericParams) + ">." +
                       methodName;

                // Check for additional generic params on the method: Type<T>.method<U>
                if (Check(type: TokenType.Less))
                {
                    Token nextToken = PeekToken(offset: 1);
                    if (nextToken.Type == TokenType.Identifier ||
                        nextToken.Type == TokenType.TypeIdentifier ||
                        nextToken.Type == TokenType.Greater)
                    {
                        Match(type: TokenType.Less);
                        var methodResult = ParseGenericParametersWithConstraints();

                        ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");

                        // Merge the method generic params into the main generic params
                        genericParams.AddRange(collection: methodResult.genericParams);
                        if (methodResult.inlineConstraints != null)
                        {
                            inlineConstraints ??= new List<GenericConstraintDeclaration>();
                            inlineConstraints.AddRange(collection: methodResult.inlineConstraints);
                        }
                    }
                }
            }
        }

        // Support namespace-qualified names like Console.print or Console.show<T>
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");
            name = name + "." + part;

            // Check for generic params after the method name: Console.show<T>
            if (Check(type: TokenType.Less))
            {
                // Peek ahead to see if this looks like generic params (identifier/type after <)
                // or a comparison expression (which would have different tokens)
                Token nextToken = PeekToken(offset: 1);
                if (nextToken.Type == TokenType.Identifier ||
                    nextToken.Type == TokenType.TypeIdentifier ||
                    nextToken.Type == TokenType.Greater)
                {
                    Match(type: TokenType.Less);
                    var result = ParseGenericParametersWithConstraints();
                    genericParams = result.genericParams;
                    inlineConstraints = result.inlineConstraints;

                    ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
                }
            }
        }

        // Support ! suffix for failable functions
        if (Match(type: TokenType.Bang))
        {
            name = name + "!";
        }

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after function name");
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Handle 'me' parameter without type (self reference)
                if (Check(type: TokenType.Self))
                {
                    Token selfToken = Advance();
                    TypeExpression? selfType = null;
                    if (Match(type: TokenType.Colon))
                    {
                        selfType = ParseType();
                    }
                    parameters.Add(item: new Parameter(Name: "me",
                        Type: selfType,
                        DefaultValue: null,
                        Location: GetLocation(token: selfToken)));
                }
                else
                {
                    // Allow keywords as parameter names (e.g., 'from' in conversion routines)
                    string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name", allowKeywords: true);
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
                }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // Return type
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Body - check if allowed to have no body (for protocol method signatures)
        BlockStatement? body = null;
        if (Check(type: TokenType.LeftBrace))
        {
            body = ParseBlockStatement();
        }
        else if (!allowNoBody)
        {
            // If no body and not allowed, it's an error
            Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after function signature");
            body = new BlockStatement(Statements: new List<Statement>(), Location: location);
        }
        else
        {
            // No body - this is a signature-only declaration (protocol)
            ConsumeStatementTerminator();
            body = new BlockStatement(Statements: new List<Statement>(), Location: location);
        }

        return new FunctionDeclaration(Name: name,
            Parameters: parameters,
            ReturnType: returnType,
            Body: body,
            Visibility: visibility,
            Attributes: attributes ?? new List<string>(),
            Location: location,
            GenericParameters: genericParams,
            GenericConstraints: constraints);
    }

    private EntityDeclaration ParseClassDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            var result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

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

            if (ParseDeclaration() is Declaration member)
            {
                members.Add(item: member);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after entity body");

        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            BaseClass: baseClass,
            Interfaces: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    private RecordDeclaration ParseStructDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            var result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Parse interfaces/protocols the record follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                interfaces.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));
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

        return new RecordDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Interfaces: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    private ChoiceDeclaration ParseEnumDeclaration(
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

            variants.Add(item: new EnumVariant(Name: variantName,
                Value: value,
                Location: GetLocation()));

            if (!Match(type: TokenType.Comma))
            {
                Match(type: TokenType.Newline);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after enum body");

        return new ChoiceDeclaration(Name: name,
            Variants: variants,
            Methods: new List<FunctionDeclaration>(),
            Visibility: visibility,
            Location: location);
    }

    private VariantDeclaration ParseVariantDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private,
        VariantKind kind = VariantKind.Variant)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            var result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

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

                cases.Add(item: new VariantCase(Name: caseName,
                    AssociatedTypes: associatedTypes,
                    Location: GetLocation()));

                if (!Match(type: TokenType.Comma))
                {
                    Match(type: TokenType.Newline);
                }
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after variant body");

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Cases: cases,
            Methods: methods,
            Visibility: visibility,
            Kind: kind,
            Location: location);
    }

    private ProtocolDeclaration ParseFeatureDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Private)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected feature name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            var result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after feature header");

        var methods = new List<FunctionSignature>();
        var requiredFields = new List<FieldRequirement>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Check if this is a routine (method signature) or a field requirement
            if (Match(type: TokenType.Routine))
            {
                // Parse function signature (without body)
                string methodName = ConsumeIdentifier(errorMessage: "Expected method name");

                // Support ! suffix for failable methods
                if (Match(type: TokenType.Bang))
                {
                    methodName = methodName + "!";
                }

                // Parameters
                Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after method name");
                var parameters = new List<Parameter>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        // Handle 'me' parameter without type (self reference)
                        if (Check(type: TokenType.Self))
                        {
                            Token selfToken = Advance();
                            TypeExpression? selfType = null;
                            if (Match(type: TokenType.Colon))
                            {
                                selfType = ParseType();
                            }
                            parameters.Add(item: new Parameter(Name: "me",
                                Type: selfType,
                                DefaultValue: null,
                                Location: GetLocation(token: selfToken)));
                        }
                        else
                        {
                            string paramName =
                                ConsumeIdentifier(errorMessage: "Expected parameter name");

                            TypeExpression? paramType = null;
                            if (Match(type: TokenType.Colon))
                            {
                                paramType = ParseType();
                            }

                            parameters.Add(item: new Parameter(Name: paramName,
                                Type: paramType,
                                DefaultValue: null,
                                Location: GetLocation()));
                        }
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

                ConsumeStatementTerminator();
            }
            else if (Check(type: TokenType.Identifier))
            {
                // Parse field requirement: field_name: Type
                SourceLocation fieldLocation = GetLocation();
                string fieldName = ConsumeIdentifier(errorMessage: "Expected field name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after field name");
                TypeExpression fieldType = ParseType();
                requiredFields.Add(item: new FieldRequirement(Name: fieldName,
                    Type: fieldType,
                    Location: fieldLocation));
                ConsumeStatementTerminator();
            }
            else
            {
                // Unknown token, skip it
                Advance();
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after feature body");

        return new ProtocolDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Methods: methods,
            Visibility: visibility,
            Location: location,
            RequiredFields: requiredFields.Count > 0
                ? requiredFields
                : null);
    }

    private ExternalDeclaration ParseExternalDeclaration(string? callingConvention = null)
    {
        SourceLocation
            location =
                GetLocation(
                    token: PeekToken(
                        offset: -2)); // -2 because we consumed 'external' and 'routine'

        string name = ConsumeIdentifier(errorMessage: "Expected function name");

        // Support namespace-qualified names like Console.print
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeIdentifier(errorMessage: "Expected identifier after '.'");
            name = name + "." + part;
        }

        // Support ! suffix for failable functions
        if (Match(type: TokenType.Bang))
        {
            name = name + "!";
        }

        // Check for generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            var result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after function name");
        var parameters = new List<Parameter>();
        bool isVariadic = false;

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Check for variadic marker (...)
                if (Match(type: TokenType.DotDotDot))
                {
                    isVariadic = true;
                    break; // ... must be last
                }

                string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");
                Consume(type: TokenType.Colon, errorMessage: "Expected ':' after parameter name");
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

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        ConsumeStatementTerminator();

        // Default to "C" calling convention if not specified
        string effectiveCallingConvention = callingConvention ?? "C";

        return new ExternalDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Parameters: parameters,
            ReturnType: returnType,
            CallingConvention: effectiveCallingConvention,
            IsVariadic: isVariadic,
            Location: location);
    }
}
