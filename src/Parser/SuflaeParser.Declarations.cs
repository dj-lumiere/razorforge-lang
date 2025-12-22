using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser;

namespace Compilers.Suflae.Parser;

/// <summary>
/// Partial class containing declaration parsing (variables, routines, entities, records, variants, etc.).
/// </summary>
public partial class SuflaeParser
{
    /// <summary>
    /// Parses attributes like @crash_only, @inline, etc.
    /// Attributes are prefixed with @ and followed by an identifier, optionally with arguments.
    /// </summary>
    private List<string> ParseAttributes()
    {
        var attributes = new List<string>();

        // Handle both @attribute and @intrinsic (which gets special tokenization)
        while (Check(TokenType.At, TokenType.Intrinsic))
        {
            string attrName;

            if (Match(type: TokenType.Intrinsic))
            {
                // @intrinsic was tokenized as a single Intrinsic token
                attrName = "intrinsic";
            }
            else if (Match(type: TokenType.At))
            {
                // Regular attribute: @identifier
                attrName = ConsumeIdentifier(errorMessage: "Expected attribute name after '@'");
            }
            else
            {
                break; // No more attributes
            }

            // Check for attribute arguments: @something("size_of") or @config(name: "value", count: 5)
            if (Match(type: TokenType.LeftParen))
            {
                var arguments = new List<string>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        // Check for named argument: name: value
                        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                               .Type == TokenType.Colon)
                        {
                            string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after argument name");
                            string argValue = ParseAttributeValue();
                            arguments.Add(item: $"{argName}={argValue}");
                        }
                        else
                        {
                            // Positional argument (string literal, number, identifier)
                            arguments.Add(item: ParseAttributeValue());
                        }
                    } while (Match(type: TokenType.Comma));
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after attribute arguments");

                // Store attribute as: intrinsic("size_of") or config(name="value", count=5)
                attrName += "(" + string.Join(separator: ", ", values: arguments) + ")";
            }

            attributes.Add(item: attrName);
        }

        return attributes;
    }

    /// <summary>
    /// Parses a single attribute argument value (string, number, bool, or identifier).
    /// </summary>
    /// <returns>String representation of the attribute value.</returns>
    private string ParseAttributeValue()
    {
        // TODO: Should it be really limited by string/number/bool/identifier?
        // String literal
        if (Check(TokenType.TextLiteral, TokenType.BytesLiteral))
        {
            return Advance()
               .Text;
        }
        // Boolean literals
        if (Match(type: TokenType.True))
        {
            return "true";
        }

        if (Match(type: TokenType.False))
        {
            return "false";
        }

        // Numeric literals
        if (Check(TokenType.Integer,
                TokenType.S64Literal,
                TokenType.U64Literal,
                TokenType.S32Literal,
                TokenType.U32Literal,
                TokenType.S16Literal,
                TokenType.U16Literal,
                TokenType.S8Literal,
                TokenType.U8Literal,
                TokenType.S128Literal,
                TokenType.U128Literal,
                TokenType.SaddrLiteral,
                TokenType.UaddrLiteral))
        {
            return Advance()
               .Text;
        }

        // Identifier (for choice values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw new ParseException(message: $"Expected attribute value, got {CurrentToken.Type}");
    }

    /// <summary>
    /// Parses a variable declaration.
    /// Syntax: <c>var name: Type = value</c> or <c>let name: Type = value</c> or <c>preset name: Type = value</c>
    /// </summary>
    /// <param name="visibility">Access modifier for the getter (default private).</param>
    /// <param name="setterVisibility">Optional separate visibility for the setter.</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseVariableDeclaration(VisibilityModifier visibility = VisibilityModifier.Public, VisibilityModifier? setterVisibility = null)
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
            Location: location,
            SetterVisibility: setterVisibility);
    }

    /// <summary>
    /// Parses a field declaration in records.
    /// Syntax: <c>name: Type</c> or <c>public name: Type = value</c>
    /// Fields are declared without var/let keywords.
    /// </summary>
    /// <param name="visibility">Access modifier for the field getter.</param>
    /// <param name="setterVisibility">Optional separate visibility for the setter.</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseFieldDeclaration(VisibilityModifier visibility = VisibilityModifier.Public, VisibilityModifier? setterVisibility = null)
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

        // Fields are not mutable by default in record
        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            IsMutable: false,
            Location: location,
            SetterVisibility: setterVisibility);
    }

    /// <summary>
    /// Parses a routine (function) declaration.
    /// Syntax: <c>routine name(params) -&gt; ReturnType:</c> followed by indented body.
    /// Supports ! suffix for failable routines.
    /// </summary>
    /// <param name="visibility">Access modifier for the routine.</param>
    /// <param name="attributes">List of attributes applied to the routine.</param>
    /// <returns>A <see cref="FunctionDeclaration"/> AST node.</returns>
    private FunctionDeclaration ParseRoutineDeclaration(VisibilityModifier visibility = VisibilityModifier.Public, List<string>? attributes = null)
    {
        // Visibility: public, internal, private, common, global, imported
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support ! suffix for failable functions
        if (Match(type: TokenType.Bang))
        {
            name += "!";
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
            Attributes: attributes ?? [],
            Location: location);
    }

    /// <summary>
    /// Parses an entity (class/reference type) declaration.
    /// Syntax: <c>entity Name&lt;T&gt; follows Protocol:</c> followed by indented body.
    /// Entities are heap-allocated reference types.
    /// </summary>
    /// <param name="visibility">Access modifier for the entity.</param>
    /// <returns>An <see cref="EntityDeclaration"/> AST node.</returns>
    private EntityDeclaration ParseEntityDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        // Parse interfaces/protocols the entity follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                interfaces.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));
        }

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after entity header");

        var members = new List<Declaration>();

        // Parse indented members
        if (!Match(type: TokenType.Indent))
        {
            // Pop generic parameter scope
            if (genericParams is { Count: > 0 })
            {
                _genericParameterScopes.Pop();
            }

            return new EntityDeclaration(Name: name,
                GenericParameters: genericParams,
                GenericConstraints: constraints,
                Protocols: interfaces,
                Members: members,
                Visibility: visibility,
                Location: location);
        }

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

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        if (Match(type: TokenType.Dedent))
        {
            return new EntityDeclaration(Name: name,
                GenericParameters: genericParams,
                GenericConstraints: constraints,
                Protocols: interfaces,
                Members: members,
                Visibility: visibility,
                Location: location);
        }

        // If no dedent token, we might be at end of file
        if (!IsAtEnd)
        {
            throw new ParseException(message: "Expected dedent after entity body");
        }

        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a record (struct/value type) declaration.
    /// Syntax: <c>record Name&lt;T&gt; follows Protocol:</c> followed by indented members.
    /// Records are stack-allocated value types.
    /// </summary>
    /// <param name="visibility">Access modifier for the record.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        // Parse interfaces/protocols the record follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                interfaces.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));
        }

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after record header");

        var members = new List<Declaration>();

        // Parse record body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        // Enable field declaration syntax inside record body
        bool wasParsingRecordBody = _parsingRecordBody;
        _parsingRecordBody = true;

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

        _parsingRecordBody = wasParsingRecordBody;

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new RecordDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Interfaces: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a choice (C-style enum) declaration.
    /// Syntax: <c>choice Name:</c> followed by indented cases with optional values.
    /// Choices are simple enumerations with integer-backed values.
    /// </summary>
    /// <param name="visibility">Access modifier for the choice.</param>
    /// <returns>A <see cref="ChoiceDeclaration"/> AST node.</returns>
    private ChoiceDeclaration ParseChoiceDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected option name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after option header");

        var variants = new List<ChoiceCase>();
        var methods = new List<FunctionDeclaration>();

        // Parse option body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (!Check(type: TokenType.Indent))
        {
            return new ChoiceDeclaration(Name: name,
                Variants: variants,
                Methods: methods,
                Visibility: visibility,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Check if it's a method (routine) - no visibility modifiers allowed in choice
            if (Check(type: TokenType.Routine))
            {
                Advance(); // consume 'routine'
                FunctionDeclaration method = ParseRoutineDeclaration();
                methods.Add(item: method);
            }
            else
            {
                // Parse enum variant
                string variantName = ConsumeIdentifier(errorMessage: "Expected option variant name");

                int? value = null;
                if (Match(type: TokenType.Assign))
                {
                    Expression expr = ParseExpression();
                    if (expr is LiteralExpression literal && literal.Value is int intVal)
                    {
                        value = intVal;
                    }
                }

                variants.Add(item: new ChoiceCase(Name: variantName, Value: value, Location: GetLocation()));
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

        return new ChoiceDeclaration(Name: name,
            Variants: variants,
            Methods: methods,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a variant (Rust-style tagged union) declaration.
    /// Syntax: <c>variant Name:</c> followed by indented cases with optional associated types.
    /// Variants are sum types where each case can carry different data.
    /// </summary>
    /// <param name="visibility">Access modifier for the variant.</param>
    /// <param name="kind">Whether this is a Variant or Mutant (mutable variant).</param>
    /// <returns>A <see cref="VariantDeclaration"/> AST node.</returns>
    private VariantDeclaration ParseVariantDeclaration(VisibilityModifier visibility = VisibilityModifier.Public, VariantKind kind = VariantKind.Variant)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Generic parameters
        List<string>? genericParams = null;
        if (Match(type: TokenType.Less))
        {
            genericParams = new List<string>();
            do
            {
                genericParams.Add(item: ConsumeIdentifier(errorMessage: "Expected generic parameter name"));
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.Greater, errorMessage: "Expected '>' after generic parameters");
        }

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after variant header");

        var cases = new List<VariantCase>();
        var methods = new List<FunctionDeclaration>();

        // Parse variant body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (!Check(type: TokenType.Indent))
        {
            // Pop generic parameter scope
            if (genericParams is { Count: > 0 })
            {
                _genericParameterScopes.Pop();
            }

            return new VariantDeclaration(Name: name,
                GenericParameters: genericParams,
                Cases: cases,
                Methods: methods,
                Visibility: visibility,
                Kind: kind,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Check if it's a method (routine) - no visibility modifiers allowed in variant/mutant
            if (Check(type: TokenType.Routine))
            {
                Advance(); // consume 'routine'
                FunctionDeclaration method = ParseRoutineDeclaration();
                methods.Add(item: method);
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

                    Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after variant case types");
                }

                cases.Add(item: new VariantCase(Name: caseName, AssociatedTypes: associatedTypes, Location: GetLocation()));
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

        // Pop generic parameter scope
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Pop();
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            Cases: cases,
            Methods: methods,
            Visibility: visibility,
            Kind: kind,
            Location: location);
    }

    /// <summary>
    /// Parses a protocol (trait/interface) declaration.
    /// Called "protocol" in Suflae, but uses the same AST as protocols.
    /// Syntax: <c>protocol Name:</c> followed by indented routine signatures.
    /// </summary>
    /// <param name="visibility">Access modifier for the protocol.</param>
    /// <returns>A <see cref="ProtocolDeclaration"/> AST node.</returns>
    private ProtocolDeclaration ParseProtocolDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Register this type name for generic disambiguation
        _knownTypeNames.Add(item: name);

        // Colon to start indented block
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after protocol header");

        var methods = new List<RoutineSignature>();

        // Parse protocol body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after ':'");

        if (!Check(type: TokenType.Indent))
        {
            return new ProtocolDeclaration(Name: name,
                GenericParameters: null,
                Methods: methods,
                Visibility: visibility,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Parse routine signature
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

            methods.Add(item: new RoutineSignature(Name: methodName,
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
            throw new ParseException(message: "Expected dedent after protocol body");
        }

        return new ProtocolDeclaration(Name: name,
            GenericParameters: null,
            Methods: methods,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a namespace declaration.
    /// Syntax: <c>namespace path/to/module</c>
    /// Uses slash separators for namespace paths.
    /// </summary>
    /// <returns>A <see cref="NamespaceDeclaration"/> AST node.</returns>
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

    /// <summary>
    /// Parses an import declaration.
    /// Syntax: <c>import path/to/module</c> or <c>import path/to/module as alias</c>
    /// Uses slash separators for module paths.
    /// </summary>
    /// <returns>An <see cref="ImportDeclaration"/> AST node.</returns>
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

        // Register imported types/namespaces for generic disambiguation
        // import Collections/SortedDict -> adds "SortedDict" to known types (bare name usage)
        // import Collections -> adds "Collections" to namespaces (qualified name usage)
        if (modulePath.Contains(value: '/'))
        {
            // Specific type import: Collections/SortedDict
            string typeName = modulePath.Substring(startIndex: modulePath.LastIndexOf(value: '/') + 1);
            _knownTypeNames.Add(item: typeName);
        }
        else
        {
            // Namespace import: Collections
            _importedNamespaces.Add(item: modulePath);
        }

        return new ImportDeclaration(ModulePath: modulePath,
            Alias: alias,
            SpecificImports: specificImports,
            Location: location);
    }

    /// <summary>
    /// Parses a define (type alias/redefinition) declaration.
    /// Syntax: <c>define OldName as NewName</c>
    /// Creates a type alias for cleaner code.
    /// </summary>
    /// <returns>A <see cref="DefineDeclaration"/> AST node.</returns>
    private IAstNode ParseDefineDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string oldName = ConsumeIdentifier(errorMessage: "Expected identifier after 'define'");
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in redefinition");
        string newName = ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

        ConsumeStatementTerminator();

        return new DefineDeclaration(OldName: oldName, NewName: newName, Location: location);
    }

    /// <summary>
    /// Parses a preset (compile-time constant) declaration.
    /// Syntax: <c>preset name: Type = value</c>
    /// </summary>
    /// <returns>A <see cref="PresetDeclaration"/> AST node.</returns>
    private PresetDeclaration ParsePresetDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected preset name");
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after preset name");
        TypeExpression type = ParseType();
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' after preset type");
        Expression value = ParseExpression();

        ConsumeStatementTerminator();

        return new PresetDeclaration(Name: name,
            Type: type,
            Value: value,
            Location: location);
    }

    /// <summary>
    /// Parses a using declaration for local type aliases.
    /// Syntax: <c>using Type as Alias</c>
    /// Creates a local alias for a type within the current scope.
    /// </summary>
    /// <returns>A <see cref="UsingDeclaration"/> AST node.</returns>
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
    /// Parses a visibility modifier keyword.
    /// Supports: public, internal, private, common, global, imported.
    /// </summary>
    /// <returns>The parsed <see cref="VisibilityModifier"/> enum value.</returns>
    private VisibilityModifier ParseVisibilityModifier()
    {
        if (Match(type: TokenType.Public))
        {
            return VisibilityModifier.Public;
        }

        if (Match(type: TokenType.Internal))
        {
            return VisibilityModifier.Internal;
        }

        if (Match(type: TokenType.Private))
        {
            return VisibilityModifier.Private;
        }

        if (Match(type: TokenType.Common))
        {
            return VisibilityModifier.Common;
        }

        if (Match(type: TokenType.Global))
        {
            return VisibilityModifier.Global;
        }

        if (Match(type: TokenType.Imported))
        {
            return VisibilityModifier.Imported;
        }

        return VisibilityModifier.Public; // Default
    }

    /// <summary>
    /// Parses getter/setter visibility modifiers.
    /// Supports syntax like: public private(set) var x
    /// Returns tuple of (getterVisibility, setterVisibility)
    /// If no setter specified, setterVisibility is null (same as getter)
    /// Only private, internal, public are valid for setter visibility.
    /// </summary>
    private (VisibilityModifier getter, VisibilityModifier? setter) ParseGetterSetterVisibility()
    {
        VisibilityModifier getterVisibility = ParseVisibilityModifier();
        VisibilityModifier? setterVisibility = null;

        // Check for setter visibility: private(set), internal(set), public(set)
        // Other modifiers like common(set), global(set), imported(set) are invalid
        bool isValidSetterModifier = Check(TokenType.Private, TokenType.Internal, TokenType.Public);
        bool isKnownInvalidSetterModifier = Check(TokenType.Common, TokenType.Global, TokenType.Imported);

        // Handle case like "asdf(set)" - any identifier followed by (set) is invalid
        // We need lookahead to detect this pattern
        if (!isValidSetterModifier && !isKnownInvalidSetterModifier)
        {
            // Check for identifier(set) pattern - this catches things like "asdf(set)"
            if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                   .Type == TokenType.LeftParen)
            {
                int savedPos = Position;
                Token unknownToken = CurrentToken;
                Advance(); // skip identifier
                Advance(); // skip (
                if (Check(type: TokenType.Identifier) && CurrentToken.Text == "set")
                {
                    throw new ParseException(message: $"'{unknownToken.Text}' is not a valid setter visibility. Only 'private', 'internal', or 'public' can be used with (set).");
                }

                Position = savedPos; // Not a (set) pattern, backtrack
            }

            return (getterVisibility, setterVisibility);
        }

        int savedPosition = Position;

        if (isKnownInvalidSetterModifier)
        {
            // Lookahead to check if this is modifier(set) pattern
            Token invalidToken = CurrentToken;
            Advance();
            if (Match(type: TokenType.LeftParen))
            {
                if (Check(type: TokenType.Identifier) && CurrentToken.Text == "set")
                {
                    throw new ParseException(message: $"'{invalidToken.Text}' is not a valid setter visibility. Only 'private', 'internal', or 'public' can be used with (set).");
                }
            }

            // Not a setter pattern, backtrack
            Position = savedPosition;
            return (getterVisibility, setterVisibility);
        }

        VisibilityModifier possibleSetter = ParseVisibilityModifier();

        // Must be followed by (set)
        if (Match(type: TokenType.LeftParen))
        {
            if (Check(type: TokenType.Identifier) && CurrentToken.Text == "set")
            {
                Advance(); // consume 'set'
                if (Match(type: TokenType.RightParen))
                {
                    // Valid setter syntax
                    setterVisibility = possibleSetter;

                    // Validate hierarchy: setter must be more restrictive than getter
                    // private(2) > internal(1) > public(0)
                    int getterLevel = getterVisibility switch
                    {
                        VisibilityModifier.Public => 0,
                        VisibilityModifier.Internal => 1,
                        VisibilityModifier.Private => 2,
                        _ => 0
                    };

                    int setterLevel = possibleSetter switch
                    {
                        VisibilityModifier.Public => 0,
                        VisibilityModifier.Internal => 1,
                        VisibilityModifier.Private => 2,
                        _ => 0
                    };

                    if (setterLevel < getterLevel)
                    {
                        throw new ParseException(message: $"Setter visibility '{possibleSetter}' cannot be less restrictive than getter visibility '{getterVisibility}'");
                    }
                }
                else
                {
                    // Not valid, backtrack
                    Position = savedPosition;
                }
            }
            else
            {
                // Not 'set', backtrack
                Position = savedPosition;
            }
        }
        else
        {
            // No parenthesis, backtrack
            Position = savedPosition;
        }

        return (getterVisibility, setterVisibility);
    }
}
