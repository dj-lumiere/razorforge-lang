using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.RazorForge.Parser;

/// <summary>
/// Partial class containing declaration parsing (variable, routine, entity, record, variant, protocol, etc.).
/// </summary>
public partial class RazorForgeParser
{
    /// <summary>
    /// Parses a variable declaration with var/let/preset keyword.
    /// Syntax: <c>var name: Type = value</c> or <c>let name = value</c> or <c>preset name = value</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the getter (default: Public).</param>
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
    /// Parses a field declaration in records: public name: Type or name: Type
    /// Fields are declared without var/let keywords.
    /// </summary>
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
    /// Parses a namespace declaration.
    /// Syntax: <c>namespace path/to/module</c>
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
    /// Syntax: <c>import module/path</c> or <c>import module/path as alias</c>
    /// Registers imported types/namespaces for generic disambiguation.
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
    /// Parses a redefinition declaration (type alias using 'define').
    /// Syntax: <c>define OldName as NewName</c>
    /// </summary>
    /// <returns>A <see cref="DefineDeclaration"/> AST node.</returns>
    private IAstNode ParseDefineDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string oldName = ConsumeIdentifier(errorMessage: "Expected identifier after 'redefine'");
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in redefinition");
        string newName = ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

        ConsumeStatementTerminator();

        return new DefineDeclaration(OldName: oldName, NewName: newName, Location: location);
    }

    /// <summary>
    /// Parses a using declaration (local type alias).
    /// Syntax: <c>using Type as Alias</c>
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

        return new PresetDeclaration(Name: name,
            Type: type,
            Value: value,
            Location: location);
    }

    /// <summary>
    /// Parses a visibility modifier keyword.
    /// Supported modifiers: public, internal, private, common, global, imported.
    /// </summary>
    /// <returns>The parsed visibility modifier, or Public if none found.</returns>
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
            if (!Check(type: TokenType.Identifier) || PeekToken(offset: 1)
                   .Type != TokenType.LeftParen)
            {
                return (getterVisibility, setterVisibility);
            }

            int savedPos = Position;
            Token unknownToken = CurrentToken;
            Advance(); // skip identifier
            Advance(); // skip (
            if (Check(type: TokenType.Identifier) && CurrentToken.Text == "set")
            {
                throw new ParseException(message: $"'{unknownToken.Text}' is not a valid setter visibility. Only 'private', 'internal', or 'public' can be used with (set).");
            }

            Position = savedPos; // Not a (set) pattern, backtrack
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

    /// <summary>
    /// Parses attributes like @crash_only, @inline, @intrinsic("name"), etc.
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

            // Check for attribute arguments: @intrinsic.size_of() or @config(name: "value", count: 5)
            if (Match(type: TokenType.LeftParen))
            {
                var arguments = new List<string>();

                if (!Check(type: TokenType.RightParen))
                {
                    do
                    {
                        // Check for named argument: name = value
                        if (Check(type: TokenType.Identifier) && PeekToken(offset: 1)
                               .Type == TokenType.Colon)
                        {
                            string argName = ConsumeIdentifier(errorMessage: "Expected argument name");
                            Consume(type: TokenType.Colon, errorMessage: "Expected '=' after argument name");
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

        // Identifier (for enum values or constant references)
        if (Check(type: TokenType.Identifier))
        {
            return Advance()
               .Text;
        }

        throw new ParseException(message: $"Expected attribute value, got {CurrentToken.Type}");
    }

    /// <summary>
    /// Parses a function/routine declaration.
    /// Syntax: <c>routine name&lt;T&gt;(params) -&gt; ReturnType where T is/isnot/in/notin/follows/notfollows Constraint { body }</c>
    /// Supports generic parameters, namespace-qualified names, failable routines (!), and inline constraints.
    /// </summary>
    /// <param name="visibility">The visibility modifier for the function.</param>
    /// <param name="attributes">List of attributes applied to the function (e.g., @intrinsic).</param>
    /// <param name="allowNoBody">If true, allows signature-only declarations (for protocols/intrinsics/imported).</param>
    /// <returns>A <see cref="RoutineDeclaration"/> AST node.</returns>
    private RoutineDeclaration ParseRoutineDeclaration(VisibilityModifier visibility = VisibilityModifier.Public, List<string>? attributes = null, bool allowNoBody = false)
    {
        // Visibility: public, internal, private, common, global, imported
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support generic type in name like Text<T>.method (generics BEFORE dot)
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        bool hasGenericParams = false;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;
            hasGenericParams = true;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Support namespace-qualified names like Console.print or Console.show<T>
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");

            // If we just parsed generic params before the dot, include them in the name
            if (hasGenericParams && !name.Contains(value: "."))
            {
                name = name + "<" + string.Join(separator: ", ", values: genericParams) + ">." + part;
                hasGenericParams = false; // Only add once
            }
            else
            {
                name = name + "." + part;
            }

            // Check for generic params after the method name: Console.show<T>
            if (!Check(type: TokenType.Less))
            {
                continue;
            }

            // Peek ahead to see if this looks like generic params (identifier/type after <)
            // or a comparison expression (which would have different tokens)
            Token nextToken = PeekToken(offset: 1);
            if (nextToken.Type != TokenType.Identifier && nextToken.Type != TokenType.TypeIdentifier && nextToken.Type != TokenType.Greater)
            {
                continue;
            }

            Match(type: TokenType.Less);
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();

            // CRITICAL: Merge method generic params with type generic params
            // For List<T>.__setitem__!<I>, we need both T (from List) and I (from method)
            if (genericParams != null && genericParams.Count > 0)
            {
                // Merge: type params first, then method params
                genericParams = new List<string>(collection: genericParams);
                genericParams.AddRange(collection: result.genericParams);

                // Merge constraints too
                if (inlineConstraints != null && result.inlineConstraints != null)
                {
                    inlineConstraints = new List<GenericConstraintDeclaration>(collection: inlineConstraints);
                    inlineConstraints.AddRange(collection: result.inlineConstraints);
                }
                else if (result.inlineConstraints != null)
                {
                    inlineConstraints = result.inlineConstraints;
                }
            }
            else
            {
                // No type params, just use method params
                genericParams = result.genericParams;
                inlineConstraints = result.inlineConstraints;
            }

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Support ! suffix for failable routines
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
                // Handle 'me' parameter without type (self reference)
                if (Check(type: TokenType.Me))
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
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope before parsing body
        if (genericParams is { Count: > 0 })
        {
            _genericParameterScopes.Push(item: [..genericParams]);
        }

        // Body - check if allowed to have no body (for protocol method signatures or @intrinsic routines)
        // Routines with @intrinsic attribute don't have bodies
        bool hasIntrinsicAttribute = attributes != null && attributes.Any(predicate: a => a.StartsWith(value: "intrinsic"));
        bool canSkipBody = allowNoBody || hasIntrinsicAttribute;

        BlockStatement? body = null;
        if (Check(type: TokenType.LeftBrace))
        {
            body = ParseBlockStatement();
        }
        else if (!canSkipBody)
        {
            // If no body and not allowed, it's an error
            Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after routine signature");
            body = new BlockStatement(Statements: new List<Statement>(), Location: location);
        }
        else
        {
            // No body - this is a signature-only declaration (protocol or intrinsic)
            ConsumeStatementTerminator();
            body = new BlockStatement(Statements: new List<Statement>(), Location: location);
        }

        // Pop generic parameter scope after parsing body
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Pop();
        }

        return new RoutineDeclaration(Name: name,
            Parameters: parameters,
            ReturnType: returnType,
            Body: body,
            Visibility: visibility,
            Attributes: attributes ?? new List<string>(),
            Location: location,
            GenericParameters: genericParams,
            GenericConstraints: constraints);
    }

    /// <summary>
    /// Parses an entity declaration (heap-allocated reference type).
    /// Syntax: <c>entity Name&lt;T&gt; from BaseClass follows Protocol { members }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the entity.</param>
    /// <returns>An <see cref="EntityDeclaration"/> AST node.</returns>
    private EntityDeclaration ParseEntityDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        var protocols = new List<TypeExpression>();

        // Parse interfaces/protocols the entity follows
        if (Match(type: TokenType.Follows))
        {
            do
            {
                // Skip newlines before protocol name (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                protocols.Add(item: ParseType());

                // Skip newlines after protocol name (before comma or brace)
                while (Match(type: TokenType.Newline)) { }
            } while (Match(type: TokenType.Comma));
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

        // Pop generic parameter scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Pop();
        }

        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: protocols,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a record declaration (stack-allocated value type).
    /// Syntax: <c>record Name&lt;T&gt; follows Protocol { members }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the record.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected record name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        // Parse interfaces/protocols the record follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                // Skip newlines before protocol name (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                interfaces.Add(item: ParseType());

                // Skip newlines after protocol name (before comma or brace)
                while (Match(type: TokenType.Newline)) { }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after record header");

        var members = new List<Declaration>();

        // Enable field declaration syntax inside record body
        bool wasParsingRecordBody = _parsingRecordBody;
        _parsingRecordBody = true;

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

        _parsingRecordBody = wasParsingRecordBody;

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after record body");

        // Pop generic parameter scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Pop();
        }

        return new RecordDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a resident declaration (singleton static type).
    /// Syntax: <c>resident Name&lt;T&gt; follows Protocol { members }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the resident.</param>
    /// <returns>A <see cref="ResidentDeclaration"/> AST node.</returns>
    private ResidentDeclaration ParseResidentDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected resident name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        // Parse interfaces/protocols the resident follows
        var interfaces = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                // Skip newlines before protocol name (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                interfaces.Add(item: ParseType());

                // Skip newlines after protocol name (before comma or brace)
                while (Match(type: TokenType.Newline)) { }
            } while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after resident header");

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

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after resident body");

        // Pop generic parameter scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Pop();
        }

        return new ResidentDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a choice declaration (C-style enum with integer values).
    /// Syntax: <c>choice Name { Case1, Case2 = 5, Case3 }</c>
    /// </summary>
    /// <param name="visibility">The visibility modifier for the choice.</param>
    /// <returns>A <see cref="ChoiceDeclaration"/> AST node.</returns>
    private ChoiceDeclaration ParseChoiceDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected choice name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after choice name");

        var variants = new List<ChoiceCase>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            string variantName = ConsumeIdentifier(errorMessage: "Expected choice variant name");

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

            if (!Match(type: TokenType.Comma))
            {
                Match(type: TokenType.Newline);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after choice body");

        return new ChoiceDeclaration(Name: name,
            Cases: variants,
            Methods: new List<RoutineDeclaration>(),
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a variant declaration (Rust-style tagged union with associated data).
    /// Syntax: <c>variant Name&lt;T&gt; { Case1, Case2(Type) }</c>
    /// </summary>
    /// <param name="kind">The variant kind: Variant (immutable) or Mutant (mutable).</param>
    /// <returns>A <see cref="VariantDeclaration"/> AST node.</returns>
    private VariantDeclaration ParseVariantDeclaration(VariantKind kind = VariantKind.Variant)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after variant header");

        var cases = new List<VariantCase>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Parse variant case
            string caseName = ConsumeIdentifier(errorMessage: "Expected variant case name");

            TypeExpression? associatedType = null;
            if (Match(type: TokenType.LeftParen))
            {
                if (!Check(type: TokenType.RightParen))
                {
                    associatedType = ParseType();
                }

                Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after variant case type");
            }

            cases.Add(item: new VariantCase(Name: caseName, AssociatedTypes: associatedType, Location: GetLocation()));

            if (!Match(type: TokenType.Comma))
            {
                Match(type: TokenType.Newline);
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after variant body");

        // Pop generic parameter scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Pop();
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Cases: cases,
            Kind: kind,
            Location: location);
    }

    /// <summary>
    /// Parses a protocol declaration (interface/trait definition).
    /// Syntax: <c>protocol Name&lt;T&gt; follows Parent { routine method(me) -&gt; Type }</c>
    /// Supports method signatures (without bodies), field requirements, and protocol inheritance.
    /// </summary>
    /// <param name="visibility">The visibility modifier for the protocol.</param>
    /// <returns>A <see cref="ProtocolDeclaration"/> AST node.</returns>
    private ProtocolDeclaration ParseProtocolDeclaration(VisibilityModifier visibility = VisibilityModifier.Public)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Register this type name
        _knownTypeNames.Add(item: name);

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        // Push generic parameters into scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Push(item: new HashSet<string>(collection: genericParams));
        }

        // Parse parent protocols (protocol X follows Y, Z)
        var parentProtocols = new List<TypeExpression>();
        if (Match(type: TokenType.Follows))
        {
            do
            {
                // Skip newlines before protocol name (for multi-line formatting)
                while (Match(type: TokenType.Newline)) { }

                parentProtocols.Add(item: ParseType());

                // Skip newlines after protocol name (before comma or brace)
                while (Match(type: TokenType.Newline)) { }
            }
            while (Match(type: TokenType.Comma));
        }

        Consume(type: TokenType.LeftBrace, errorMessage: "Expected '{' after protocol header");

        var methods = new List<RoutineSignature>();

        while (!Check(type: TokenType.RightBrace) && !IsAtEnd)
        {
            if (Match(type: TokenType.Newline))
            {
                continue;
            }

            // Parse attributes before routine or field declaration
            List<string> attributes = ParseAttributes();

            // Check if this is a routine (method signature) or a field requirement
            if (Match(type: TokenType.Routine))
            {
                // Parse function signature (without body)
                string methodName = ConsumeIdentifier(errorMessage: "Expected method name");

                // Support namespace-qualified names like Me.method or Type.method
                while (Match(type: TokenType.Dot))
                {
                    string part = ConsumeMethodName(errorMessage: "Expected method name after '.'");
                    methodName = methodName + "." + part;
                }

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
                        if (Check(type: TokenType.Me))
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
                            string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name");

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

                methods.Add(item: new RoutineSignature(Name: methodName,
                    Parameters: parameters,
                    ReturnType: returnType,
                    Attributes: attributes.Count > 0 ? attributes : null,
                    Location: GetLocation()));

                ConsumeStatementTerminator();
            }
            else
            {
                // Unknown token, skip it
                Advance();
            }
        }

        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' after protocol body");

        // Pop generic parameter scope
        if (genericParams != null && genericParams.Count > 0)
        {
            _genericParameterScopes.Pop();
        }

        return new ProtocolDeclaration(Name: name,
            GenericParameters: genericParams,
            ParentProtocols: parentProtocols,
            Methods: methods,
            Visibility: visibility,
            Location: location,
            GenericConstraints: constraints);
    }

    /// <summary>
    /// Parses an imported (external/FFI) function declaration.
    /// Syntax: <c>imported("C") routine name(param: Type, ...) -&gt; ReturnType</c>
    /// Supports variadic functions and calling convention specification.
    /// </summary>
    /// <param name="callingConvention">The calling convention (e.g., "C"). Defaults to "C" if null.</param>
    /// <returns>An <see cref="ImportedDeclaration"/> AST node.</returns>
    private ImportedDeclaration ParseImportedDeclaration(string? callingConvention = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -2)); // -2 because we consumed 'imported' and 'routine'

        string name = ConsumeIdentifier(errorMessage: "Expected routine name");

        // Support namespace-qualified names like Console.print
        while (Match(type: TokenType.Dot))
        {
            string part = ConsumeIdentifier(errorMessage: "Expected identifier after '.'");
            name = name + "." + part;
        }

        // Support ! suffix for failable routines
        if (Match(type: TokenType.Bang))
        {
            name = name + "!";
        }

        // Check for generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (Match(type: TokenType.Less))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints) result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            ConsumeGreaterForGeneric(errorMessage: "Expected '>' after generic parameters");
        }

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
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
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(genericParams: genericParams, existingConstraints: inlineConstraints);

        ConsumeStatementTerminator();

        // Default to "C" calling convention if not specified
        string effectiveCallingConvention = callingConvention ?? "C";

        return new ImportedDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Parameters: parameters,
            ReturnType: returnType,
            CallingConvention: effectiveCallingConvention,
            IsVariadic: isVariadic,
            Location: location);
    }
}
