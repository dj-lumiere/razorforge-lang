using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing declaration parsing (variables, routines, entities, records, variants, etc.).
/// Unified parser for both RazorForge and Suflae languages.
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Parses annotations like @crash_only, @inline, @llvm("i32"), @[readonly, inline], etc.
    /// Annotations are prefixed with @ and followed by an identifier, optionally with arguments.
    /// Also supports compound annotations: @[attr1, attr2, attr3]
    /// </summary>
    private VariableDeclaration ParseVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
        List<string>? annotations = null,
        bool isLateInit = false)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

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
            Location: location,
            Annotations: annotations?.Count > 0 ? annotations : null,
            IsLateInit: isLateInit);
    }

    /// <summary>
    /// Parses a module-level mutable global: <c>global counter: S64 = 0</c>.
    /// Unlike <c>var</c>, both the type annotation AND an initializer are REQUIRED (a global has no
    /// enclosing scope to infer from and must have a definite start value). Produces a
    /// <see cref="VariableDeclaration"/> tagged <c>IsGlobal = true</c> — no dedicated AST node, so no
    /// visitor churn; SA/codegen branch on the flag. The leading <c>global</c> keyword is already consumed.
    /// </summary>
    private VariableDeclaration ParseGlobalDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open,
        List<string>? annotations = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected global name");
        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after global name");
        TypeExpression type = ParseType();
        Consume(type: TokenType.Assign, errorMessage: "Expected '=' after global type (a global must be initialized)");
        Expression initializer = ParseExpression();
        ConsumeStatementTerminator();

        return new VariableDeclaration(Name: name,
            Type: type,
            Initializer: initializer,
            Visibility: visibility,
            Location: location,
            Annotations: annotations?.Count > 0 ? annotations : null,
            IsGlobal: true);
    }

    /// <summary>
    /// Parses a member variable declaration in records.
    /// Syntax: <c>name: Type</c> or <c>public name: Type = value</c>
    /// MemberVariables are declared without var keywords.
    /// </summary>
    /// <param name="visibility">Access modifier (public, published, internal, private).</param>
    /// <returns>A <see cref="VariableDeclaration"/> AST node.</returns>
    private VariableDeclaration ParseMemberVariableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation();

        string name = ConsumeIdentifier(errorMessage: "Expected member variable name");

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after member variable name");
        TypeExpression type = ParseType();

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
            Location: location);
    }

    /// <summary>
    /// Parses a decl-position <c>expand m in allmemvarof(T)</c> inside a record/entity body: an indented
    /// block of member-variable column templates (<c>[secret] ${namesplice}: Type</c>) materialized once
    /// per member of the concrete source type at instantiation. See <see cref="ExpandMemberDeclaration"/>.
    /// </summary>
    private ExpandMemberDeclaration ParseExpandMemberDeclaration()
    {
        SourceLocation location = GetLocation();
        Consume(type: TokenType.Expand, errorMessage: "Expected 'expand'");
        string handle = ConsumeIdentifier(errorMessage: "Expected expand handle name");
        Consume(type: TokenType.In, errorMessage: "Expected 'in' in expand directive");
        // A decl-position expand lays a column per member and always covers EVERY field, so it accepts
        // `allmemvarof(T)` (which yields all members). `openmemvarof` (a public-only filter) has no
        // decl-position use case and would need visibility threading.
        // Name-agnostic: a decl-position expand's source (always `allmemvarof`) is read as a plain
        // identifier — the parser does not know the BuilderExpansion intrinsic names.
        ConsumeIdentifier(errorMessage: "A decl-position expand only supports 'allmemvarof(T)'.");
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after 'allmemvarof'");
        TypeExpression sourceType = ParseType();
        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after allmemvarof type");
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after expand header");

        var templates = new List<ExpandMemberTemplate>();
        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();
            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                if (Match(TokenType.Newline, TokenType.DocComment))
                {
                    continue;
                }
                templates.Add(item: ParseExpandMemberTemplate(handle: handle));
            }
            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    message: "Expected dedent after expand body");
            }
        }

        return new ExpandMemberDeclaration(HandleName: handle,
            SourceType: sourceType,
            Templates: templates,
            Location: location);
    }

    /// <summary>
    /// Parses one member-variable column template inside a decl-position expand:
    /// <c>[secret|posted|open] ${ ["prefix" +] handle.name }: Type</c>. The name splice yields
    /// <c>NamePrefix + fieldName</c>; the type may carry a <c>${handle.type}</c> splice.
    /// </summary>
    private ExpandMemberTemplate ParseExpandMemberTemplate(string handle)
    {
        SourceLocation loc = GetLocation();
        (VisibilityModifier visibility, _) = ParseModifiers();

        // Brace-less form: `[secret] $nameof(m): Type`. The name is the field name; no prefix.
        if (Match(type: TokenType.Dollar))
        {
            Expression inner = ParseDollarSpliceInner();
            if (inner is not CallExpression
                {
                    Callee: IdentifierExpression { Name: "nameof" },
                    Arguments: [IdentifierExpression nameHandleNew]
                } || nameHandleNew.Name != handle)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                    message: $"An expand-column name must be '$nameof({handle})'.");
            }
            Consume(type: TokenType.Colon, errorMessage: "Expected ':' after '$nameof(m)' column name");
            TypeExpression colType = ParseType();
            ConsumeStatementTerminator();
            return new ExpandMemberTemplate(NamePrefix: "",
                Type: colType,
                Visibility: visibility,
                Location: loc);
        }

        Consume(type: TokenType.SpliceOpen,
            errorMessage: "Expected a '$nameof(m)' member-name splice in the expand body");

        // Optional literal prefix: `${"inner_" + m.name}` — else bare `${m.name}`.
        string prefix = "";
        if (Check(type: TokenType.TextLiteral))
        {
            prefix = Advance().Text;
            Consume(type: TokenType.Plus,
                errorMessage: "Expected '+' after the name prefix in a '${...}' member-name splice");
        }

        string nameHandle = ConsumeIdentifier(errorMessage: "Expected the expand handle in '${...}' splice");
        if (nameHandle != handle)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                message: $"Expected the expand handle '{handle}' in '${{...}}', got '{nameHandle}'.");
        }
        Consume(type: TokenType.Dot, errorMessage: "Expected '.name' in the '${...}' member-name splice");
        string proj = ConsumeIdentifier(errorMessage: "Expected 'name' after '.' in name splice");
        if (proj != "name")
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.UnexpectedToken,
                message: $"A member-name splice must be '${{{handle}.name}}', not '${{{handle}.{proj}}}'.");
        }
        Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' to close the '${...}' name splice");

        Consume(type: TokenType.Colon, errorMessage: "Expected ':' after the member-name splice");
        TypeExpression type = ParseType();
        ConsumeStatementTerminator();

        return new ExpandMemberTemplate(NamePrefix: prefix,
            Type: type,
            Visibility: visibility,
            Location: loc);
    }

    /// <summary>
    /// Parses a routine declaration.
    /// Syntax: <c>routine name(params) -&gt; ReturnType</c> followed by indented body.
    /// Supports generic parameters, slash-based module paths, failable routines (!), and inline constraints.
    /// </summary>
    /// <remarks>
    /// Parsing phases:
    ///
    /// PHASE 1: VALIDATION
    ///   - Reject nested routine declarations
    ///
    /// PHASE 2: NAME AND FAILABLE MARKER
    ///   - Parse routine name
    ///   - Parse optional type-level generic parameters
    ///   - Parse dot-separated qualified name (for member routines)
    ///   - Check for ! suffix (failable routine)
    ///
    /// PHASE 3: PARAMETERS
    ///   - Parse parameter list: (name: Type, name: Type = default)
    ///   - Handle 'me' self-reference parameter
    ///
    /// PHASE 4: RETURN TYPE
    ///   - Optional: -> ReturnType
    ///
    /// PHASE 5: GENERIC CONSTRAINTS
    ///   - Optional: where T obeys Protocol
    ///
    /// PHASE 6: BODY
    ///   - Parse indented block
    /// </remarks>
    /// <param name="visibility">Access modifier for the routine.</param>
    /// <param name="annotations">List of annotations applied to the routine.</param>
    /// <param name="isCommon">Whether this is a <c>common</c> (type-level static) member routine.</param>
    /// <param name="asyncStatus">Suspended or threaded status of the routine.</param>
    /// <param name="isDangerous">Whether the routine is marked as dangerous (RF only).</param>
    /// <returns>A <see cref="RoutineDeclaration"/> AST node.</returns>
    private RoutineDeclaration ParseRoutineDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open, List<string>? annotations = null,
        bool isCommon = false, AsyncStatus asyncStatus = AsyncStatus.None,
        bool isDangerous = false)
    {
        // ===============================================================================
        // PHASE 1: VALIDATION
        // ===============================================================================
        if (_inRoutineBody)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.NestedRoutineNotAllowed,
                message:
                "Nested routine declarations are not allowed. Define routines at module or type level.");
        }

        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // ===============================================================================
        // PHASE 2: NAME PARSING - Base name + optional type-level generic parameters
        // ===============================================================================
        // Examples:
        //   "foo"          -> name="foo", no generics
        //   "List[T]"      -> name="List", genericParams=["T"]
        //   "Point.get_x"  -> name="Point.get_x"
        // ===============================================================================
        // A leading `$` (wired member routine like `store`) is a separate Dollar token, recorded
        // structurally (IsWiredMemberRoutine) and dropped from the bare name.
        _routineNameWired = false;
        if (Match(type: TokenType.Dollar))
        {
            _routineNameWired = true;
        }
        // `None` (a keyword — the void type) is a legal routine owner: `routine None.represent()`.
        string name = Match(type: TokenType.None)
            ? "None"
            : ConsumeIdentifier(errorMessage: "Expected routine name");

        List<string>? genericParams = null;
        // Serialized type-arg strings used to rebuild the routine name (e.g., "DictEntry[K, V]"),
        // distinct from genericParams which holds the leaf identifiers that are bound (e.g., K, V).
        List<string>? receiverTypeArgStrings = null;
        List<TypeExpression>? receiverArgExprs = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        bool hasGenericParams = false;

        // Check for type-level generic params BEFORE the dot (e.g., "List[T].append")
        if (Match(type: TokenType.LeftBracket))
        {
            ParseReceiverTypeArgs(genericParams: out genericParams,
                receiverTypeArgStrings: out receiverTypeArgStrings,
                receiverArgExprs: out receiverArgExprs,
                inlineConstraints: out inlineConstraints,
                hasGenericParams: out hasGenericParams);
        }

        // ===============================================================================
        // PHASE 2b: Parse dot-separated qualified name (for member routines)
        // ===============================================================================
        // Examples:
        //   "Console.print"           -> name="Console.print"
        //   "List[T].append"          -> name="List[T].append" (generics embedded in name)
        //   "Dict[K, V].get[I]"       -> name="Dict[K, V].get", genericParams=["K","V","I"]
        // ===============================================================================
        var nameSb = new System.Text.StringBuilder(name);

        // Structural owner/memberRoutine capture (name-canonicalization): the parser knows the owner base
        // identifier (`name`) and each memberRoutine segment as SEPARATE tokens; record them so consumers
        // never re-split the concatenated `Name` string. `name` still holds the bare owner base here
        // (it isn't reassigned to nameSb.ToString() until after this loop).
        string? memberOwnerName = null;
        string? memberMemberRoutineName = null;
        bool memberHasReceiverTypeArgs = false;
        // The receiver AS WRITTEN incl type-args ("List[T]", "Iterable[Text]"), assembled HERE from the
        // separate owner/bracket/arg tokens — so consumers read it structurally instead of slicing it back
        // out of the concatenated `Name`.
        string? memberRenderedReceiver = null;

        while (Match(type: TokenType.Dot))
        {
            AppendRoutineMemberSegment(
                baseName: name,
                nameSb: nameSb,
                memberOwnerName: ref memberOwnerName,
                memberMemberRoutineName: ref memberMemberRoutineName,
                memberHasReceiverTypeArgs: ref memberHasReceiverTypeArgs,
                memberRenderedReceiver: ref memberRenderedReceiver,
                hasGenericParams: ref hasGenericParams,
                receiverTypeArgStrings: receiverTypeArgStrings,
                genericParams: ref genericParams,
                inlineConstraints: ref inlineConstraints);
        }

        name = nameSb.ToString();

        // ===============================================================================
        // PHASE 2c: Parse failable marker (!)
        // ===============================================================================
        // Support ! suffix for failable routines (can appear after qualified name).
        // The `!` is a separate Bang token — ConsumeIdentifier/ConsumeMemberRoutineName never fold it
        // into the name, so the stored name is always bare and only this flag records failability.
        bool isFailable = Match(type: TokenType.Bang);

        // A failable free routine writes the bang immediately after the base name, with its
        // type-level generics following it: `race![T](...)`. (Member routines instead carry their
        // generics on the receiver before the dot, e.g. `Agent[T].retrieve!()`.) Parse those
        // post-bang generics here so they bind exactly like the pre-name form.
        if (isFailable && !hasGenericParams && Match(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;
            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }

        // ===============================================================================
        // PHASE 3: PARAMETERS
        // ===============================================================================
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after routine name");
        List<Parameter> parameters = ParseRoutineParameters();
        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");

        // ===============================================================================
        // PHASE 4: GENERIC CONSTRAINTS (needs clause — before or after return type)
        // ===============================================================================
        // Supports both orderings:
        //   routine foo[T](x: T) needs T obeys P -> Text      (needs before ->)
        //   routine foo[T](x: T) -> Text \n needs T obeys P   (needs after -> on next line)
        List<GenericConstraintDeclaration>? constraints = ParseGenericConstraints(
            genericParams: genericParams,
            existingConstraints: inlineConstraints);

        // ===============================================================================
        // PHASE 5: RETURN TYPE
        // ===============================================================================
        TypeExpression? returnType = null;
        if (Match(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        // Try constraints again after return type (supports needs on next line after ->)
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        // ===============================================================================
        // PHASE 6: BODY (indented block)
        // ===============================================================================
        // @innate routines are compiler-intrinsic: the body is supplied by the compiler,
        // not the source. Allow them to have no written body at all.

        Statement body = ParseRoutineBody(annotations: annotations, location: location);

        // Name is the BARE member for an extension-syntax member routine (owner lives in the structured
        // OwnerName/RenderedReceiver/ReceiverType fields); free routines and type-body members keep their
        // already-bare name. The owner-qualified composite is rebuilt on demand via QualifiedName.
        return new RoutineDeclaration(Name: memberMemberRoutineName ?? name,
            Parameters: parameters,
            ReturnType: returnType,
            Body: body,
            Visibility: visibility,
            Annotations: annotations ?? [],
            Location: location,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            IsFailable: isFailable,
            IsCommon: isCommon,
            Async: asyncStatus,
            IsDangerous: isDangerous,
            IsWiredMemberRoutine: _routineNameWired)
        {
            OwnerName = memberOwnerName,
            MemberRoutineName = memberMemberRoutineName,
            HasReceiverTypeArgs = memberHasReceiverTypeArgs,
            RenderedReceiver = memberRenderedReceiver,
            ReceiverType = memberOwnerName != null
                ? new TypeExpression(Name: memberOwnerName, GenericArguments: receiverArgExprs,
                    Location: location)
                : null
        };
    }

    /// <summary>
    /// Parses a routine's parameter list (the opening <c>(</c> is already consumed, the closing <c>)</c>
    /// is NOT): a <c>me</c> self-parameter or a regular parameter (<c>name: Type = default</c>, including
    /// variadic <c>name...: Type</c> and keyword-named params).
    /// </summary>
    private List<Parameter> ParseRoutineParameters()
    {
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                parameters.Add(item: Check(type: TokenType.Me)
                    ? ParseSelfParameter()
                    : ParseRegularParameter());
            } while (Match(type: TokenType.Comma));
        }

        return parameters;
    }

    /// <summary>
    /// Parses a <c>me</c> self-parameter: advances past the <c>me</c> token, then optionally reads a
    /// colon-annotated type. Returns the resulting <see cref="Parameter"/>.
    /// </summary>
    private Parameter ParseSelfParameter()
    {
        Token selfToken = Advance();
        TypeExpression? selfType = null;
        if (Match(type: TokenType.Colon))
        {
            selfType = ParseType();
        }

        return new Parameter(Name: "me",
            Type: selfType,
            DefaultValue: null,
            Location: GetLocation(token: selfToken));
    }

    /// <summary>
    /// Parses a regular (non-<c>me</c>) routine parameter: <c>name[...]: Type = default</c>,
    /// including variadic (<c>name...: Type</c>) and keyword-named params.
    /// </summary>
    private Parameter ParseRegularParameter()
    {
        // allowKeywords=true lets us use 'from', 'to', etc. as param names
        string paramName = ConsumeIdentifier(errorMessage: "Expected parameter name",
            allowKeywords: true);
        bool isVariadic = Match(type: TokenType.DotDotDot);
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

        return new Parameter(Name: paramName,
            Type: paramType,
            DefaultValue: defaultValue,
            Location: GetLocation(),
            IsVariadic: isVariadic);
    }

    /// <summary>
    /// Parses the body of a routine declaration. For <c>@innate</c> routines with no written body an empty
    /// <see cref="BlockStatement"/> is returned; otherwise the next indented block is parsed. Correctly
    /// guards <see cref="_inRoutineBody"/> in a try/finally so nested-routine detection stays accurate.
    /// </summary>
    private Statement ParseRoutineBody(List<string>? annotations, SourceLocation location)
    {
        bool isInnate = annotations != null && annotations.Contains(item: "innate");
        // A body exists when the next tokens are Newline+Indent or just Indent.
        // A bare Newline without a following Indent means no body (next declaration follows).
        bool hasBody = Check(type: TokenType.Indent) ||
                       (Check(type: TokenType.Newline) &&
                        PeekToken(offset: 1).Type == TokenType.Indent);

        _inRoutineBody = true;
        try
        {
            return isInnate && !hasBody
                ? new BlockStatement(Statements: [], Location: location)
                : ParseIndentedBlock();
        }
        finally
        {
            _inRoutineBody = false;
        }
    }

    /// <summary>
    /// Parses member-routine-level generic parameters after the routine name (the opening <c>[</c> is
    /// already consumed): e.g. the <c>[I]</c> in <c>List[T].get[I]</c>. Merges the parsed params (and any
    /// inline constraints, in the non-nested case) into the existing type-level lists. Consumes the
    /// closing <c>]</c>.
    /// </summary>
    private void ParseMemberRoutineGenericParams(ref List<string>? genericParams,
        ref List<GenericConstraintDeclaration>? inlineConstraints)
    {
        if (HasNestedBrackets())
        {
            // Nested generics in member-routine-level params
            var typeArgs = new List<string>();
            do
            {
                TypeExpression typeArg = ParseTypeOrConstGeneric();
                typeArgs.Add(item: SerializeTypeExpression(type: typeArg));
            } while (Match(type: TokenType.Comma));

            if (genericParams is { Count: > 0 })
            {
                genericParams = new List<string>(collection: genericParams);
                genericParams.AddRange(collection: typeArgs);
            }
            else
            {
                genericParams = typeArgs;
            }

            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }
        else
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>?
                inlineConstraints) result = ParseGenericParametersWithConstraints();

            // Merge type-level and member-routine-level generic parameters
            if (genericParams is { Count: > 0 })
            {
                genericParams = new List<string>(collection: genericParams);
                genericParams.AddRange(collection: result.genericParams);
                if (inlineConstraints != null && result.inlineConstraints != null)
                {
                    inlineConstraints =
                        new List<GenericConstraintDeclaration>(
                            collection: inlineConstraints);
                    inlineConstraints.AddRange(collection: result.inlineConstraints);
                }
                else if (result.inlineConstraints != null)
                {
                    inlineConstraints = result.inlineConstraints;
                }
            }
            else
            {
                genericParams = result.genericParams;
                inlineConstraints = result.inlineConstraints;
            }

            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }
    }

    /// <summary>
    /// Parses type-level generic params on a routine receiver directly after the opening <c>[</c>
    /// is consumed. Handles both nested generics (e.g. <c>DictEntry[K, V]</c>) and plain param lists
    /// (e.g. <c>[T, U]</c>), populating the out parameters and consuming the closing <c>]</c>.
    /// </summary>
    private void ParseReceiverTypeArgs(
        out List<string>? genericParams,
        out List<string>? receiverTypeArgStrings,
        out List<TypeExpression>? receiverArgExprs,
        out List<GenericConstraintDeclaration>? inlineConstraints,
        out bool hasGenericParams)
    {
        if (HasNestedBrackets())
        {
            // Nested generics: parse as type expressions (e.g., List[DictEntry[K, V]]).
            // The bound generic parameters are the leaf identifiers (no further generics)
            // — e.g., for `List[DictEntry[K, V]]` → bind K and V, not "DictEntry".
            var typeArgStrings = new List<string>();
            var leafParams = new List<string>();
            var typeArgExprs = new List<TypeExpression>();
            do
            {
                TypeExpression typeArg = ParseTypeOrConstGeneric();
                typeArgStrings.Add(item: SerializeTypeExpression(type: typeArg));
                typeArgExprs.Add(item: typeArg);
                CollectLeafGenericParams(type: typeArg, into: leafParams);
            } while (Match(type: TokenType.Comma));

            genericParams = leafParams;
            receiverTypeArgStrings = typeArgStrings;
            receiverArgExprs = typeArgExprs;
            hasGenericParams = true;
            inlineConstraints = null;
            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }
        else
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;
            hasGenericParams = true;
            // Receiver args as structured type expressions (each param name is a named type,
            // e.g. `List[T]` → [T]); mirrors ParseTypeExpressionString on the serialized owner.
            receiverArgExprs = result.genericParams
               .Select(selector: p => new TypeExpression(Name: p, GenericArguments: null,
                    Location: GetLocation()))
               .ToList();
            receiverTypeArgStrings = null;
            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }
    }

    /// <summary>
    /// Processes one dot-segment of a qualified routine name (the leading <c>.</c> was already consumed).
    /// Embeds previously parsed type-args into the name builder on the first segment, appends the member
    /// name on subsequent segments, and parses any member-routine-level generics that follow.
    /// </summary>
    private void AppendRoutineMemberSegment(
        string baseName,
        System.Text.StringBuilder nameSb,
        ref string? memberOwnerName,
        ref string? memberMemberRoutineName,
        ref bool memberHasReceiverTypeArgs,
        ref string? memberRenderedReceiver,
        ref bool hasGenericParams,
        List<string>? receiverTypeArgStrings,
        ref List<string>? genericParams,
        ref List<GenericConstraintDeclaration>? inlineConstraints)
    {
        string part = ConsumeMemberRoutineName(errorMessage: "Expected member routine name after '.'");
        memberOwnerName ??= baseName;
        memberMemberRoutineName = part;

        // If we parsed generic params before the dot, embed them in the name.
        // This transforms: name="List", generics=["T"], part="append"  →  "List[T].append".
        // For nested receivers (e.g. List[DictEntry[K, V]]), use the serialized type-arg
        // strings rather than the bound leaf identifiers so the name preserves structure.
        if (hasGenericParams && !nameSb.ToString().Contains(value: '.') &&
            (receiverTypeArgStrings != null || genericParams != null))
        {
            List<string> nameArgs = receiverTypeArgStrings ?? genericParams!;
            string renderedArgs = string.Join(separator: ", ", values: nameArgs);
            memberRenderedReceiver = $"{baseName}[{renderedArgs}]"; // e.g. "List[T]", "List[DictEntry[K, V]]"
            nameSb.Append('[');
            nameSb.Append(renderedArgs);
            nameSb.Append("].");
            nameSb.Append(part);
            memberHasReceiverTypeArgs = true; // owner carried type-args (List[T].append)
            hasGenericParams = false; // Only embed once
        }
        else
        {
            // Owner rendered so far (the bare owner, e.g. "S32", "Iterable") — captured BEFORE the
            // member segment is appended.
            memberRenderedReceiver = nameSb.ToString();
            nameSb.Append('.');
            nameSb.Append(part);
        }

        // Check for member-routine-level generic params AFTER the routine name
        // e.g., "List[T].get[I]" - the [I] belongs to the member routine
        if (Match(type: TokenType.LeftBracket))
        {
            ParseMemberRoutineGenericParams(genericParams: ref genericParams,
                inlineConstraints: ref inlineConstraints);
        }
    }

    // Entity declaration parsing lives in Parser.Declarations.Types.cs.
    private (VisibilityModifier Visibility, bool IsCommon) ParseModifiers()
    {
        VisibilityModifier visibility = VisibilityModifier.Open; // Default
        bool isCommon = false; // Default
        bool hasVisibility = false;
        bool hasStorage = false;

        // Parse modifiers in any order (visibility and common can appear in any order)
        while (true)
        {
            // Visibility modifiers (Open keyword removed - open is default, not a keyword)
            if (!hasVisibility && Match(type: TokenType.Posted))
            {
                visibility = VisibilityModifier.Posted;
                hasVisibility = true;
            }
            else if (!hasVisibility && Match(type: TokenType.Secret))
            {
                visibility = VisibilityModifier.Secret;
                hasVisibility = true;
            }
            // `common` — a type-level (static) member routine
            else if (!hasStorage && Match(type: TokenType.Common))
            {
                isCommon = true;
                hasStorage = true;
            }
            else
            {
                break; // No more modifiers
            }
        }

        return (visibility, isCommon);
    }

    /// <summary>
    /// Checks whether the current bracket content contains nested generics (e.g., DictEntry[K, V]).
    /// Must be called after consuming the opening '['.
    /// Uses lookahead without advancing the parser position.
    /// </summary>
    private bool HasNestedBrackets()
    {
        int offset = 0;
        int depth = 0;

        while (true)
        {
            Token token = PeekToken(offset: offset);
            if (token.Type == TokenType.Eof)
            {
                break;
            }

            if (token.Type == TokenType.LeftBracket)
            {
                // A '[' at depth 0 means nested generics (we're already inside the outer '[')
                if (depth == 0)
                {
                    return true;
                }

                depth++;
            }
            else if (token.Type == TokenType.RightBracket)
            {
                if (depth == 0)
                {
                    break; // End of outer brackets
                }

                depth--;
            }

            offset++;
        }

        return false;
    }

    /// <summary>
    /// Walks a TypeExpression and appends every leaf identifier (no further generic args)
    /// into <paramref name="into"/> in left-to-right order, deduplicating. Used to extract
    /// generic-parameter identifiers from nested receiver types like `List[DictEntry[K, V]]`
    /// → ["K", "V"]. Identifiers with dots (qualified names) are excluded.
    /// </summary>
    private static void CollectLeafGenericParams(TypeExpression type, List<string> into)
    {
        if (type.GenericArguments is { Count: > 0 } args)
        {
            foreach (TypeExpression arg in args)
            {
                CollectLeafGenericParams(type: arg, into: into);
            }
            return;
        }

        if (type.Name.Contains(value: '.')) return;
        if (into.Contains(item: type.Name)) return;
        into.Add(item: type.Name);
    }

    /// <summary>
    /// Serializes a TypeExpression back to its string form.
    /// e.g., TypeExpression("DictEntry", [TypeExpression("K"), TypeExpression("V")]) -> "DictEntry[K, V]"
    /// </summary>
    private static string SerializeTypeExpression(TypeExpression type)
    {
        if (type.GenericArguments is not { Count: > 0 })
        {
            return type.Name;
        }

        return type.Name + "[" + string.Join(separator: ", ",
            values: type.GenericArguments.Select(selector: SerializeTypeExpression)) + "]";
    }
}
