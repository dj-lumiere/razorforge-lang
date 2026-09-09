using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing type, entity, and routine declaration parsing.
/// </summary>
public partial class Parser
{
    private const string ExpectedRightBracketAfterGenericParameters = "Expected ']' after generic parameters";

    private EntityDeclaration ParseEntityDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected entity name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        // Supports needs before or after obeys
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);

        List<TypeExpression> interfaces = ParseObeysProtocolList();

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        // Associated-type bindings: `relates ConcreteType as Iter` (needs-sibling clause).
        List<AssociatedTypeDeclaration>? associatedTypes = ParseRelatesClauses();

        var members = new List<SyntaxTree.Declaration>();

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after entity header");

        // Entities allow modifiers on member variables (unlike records).
        bool hasPass = ParseIndentedTypeMembers(
            members: members,
            typeName: "entity",
            strictRecord: false);

        return new EntityDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location,
            HasPassBody: hasPass)
        {
            AssociatedTypes = associatedTypes
        };
    }

    /// <summary>
    /// Parses a record (struct/value type) declaration.
    /// Syntax: <c>record Name[T] obeys Protocol</c> followed by indented members.
    /// Records are stack-allocated value types.
    /// </summary>
    /// <param name="visibility">Access modifier for the record.</param>
    /// <param name="annotations">Optional annotations attached to the record declaration.</param>
    /// <returns>A <see cref="RecordDeclaration"/> AST node.</returns>
    private RecordDeclaration ParseRecordDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open, List<string>? annotations = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        // `None` is a keyword (the void type / variant empty branch) but is a legal record name — the
        // void unit type is declared `record None`.
        string name = CheckAndAdvance(type: TokenType.None)
            ? "None"
            : ConsumeIdentifier(errorMessage: "Expected record name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);

        List<TypeExpression> interfaces = ParseObeysProtocolList();

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        // Associated-type bindings: `relates ConcreteType as Iter` (needs-sibling clause).
        List<AssociatedTypeDeclaration>? associatedTypes = ParseRelatesClauses();

        var members = new List<SyntaxTree.Declaration>();

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after record header");

        // Records are strict: no modifiers allowed on member variables.
        bool hasPass = ParseIndentedTypeMembers(
            members: members,
            typeName: "record",
            strictRecord: true);

        return new RecordDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Protocols: interfaces,
            Members: members,
            Visibility: visibility,
            Location: location,
            HasPassBody: hasPass,
            Annotations: annotations)
        {
            AssociatedTypes = associatedTypes
        };
    }

    /// <summary>
    /// Parses a choice (C-style enum) declaration.
    /// Syntax: <c>choice Name</c> followed by indented cases with optional values.
    /// Choices are simple enumerations with integer-backed values.
    /// </summary>
    /// <param name="visibility">Access modifier for the choice.</param>
    /// <returns>A <see cref="ChoiceDeclaration"/> AST node.</returns>
    private ChoiceDeclaration ParseChoiceDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected choice name");

        var variants = new List<ChoiceCase>();
        var memberRoutines = new List<RoutineDeclaration>();

        // Parse choice body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after choice header");

        if (!Check(type: TokenType.Indent))
        {
            return new ChoiceDeclaration(Name: name,
                Cases: variants,
                MemberRoutines: memberRoutines,
                Visibility: visibility,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
            {
                continue;
            }

            // Inline routines are not allowed in choice bodies.
            // Use 'routine ChoiceName.MemberRoutine()' external syntax instead.
            if (Check(type: TokenType.Routine))
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message: "Routines cannot be declared inside choice bodies. Use 'routine ChoiceName.MemberRoutine()' syntax instead.");
            }
            else
            {
                // Parse enum variant
                string variantName =
                    ConsumeIdentifier(errorMessage: "Expected choice variant name");

                // CASE: value syntax for choice values (e.g., OK: 200)
                // Store expression as-is; semantic analyzer will validate and convert
                Expression? value = null;
                if (CheckAndAdvance(type: TokenType.Colon))
                {
                    value = ParseExpression();
                }

                variants.Add(item: new ChoiceCase(Name: variantName,
                    Value: value,
                    Location: GetLocation()));
                CheckAndAdvance(type: TokenType.Newline);
            }
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after choice body");
        }

        return new ChoiceDeclaration(Name: name,
            Cases: variants,
            MemberRoutines: memberRoutines,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a flags declaration (combinable bitflag set).
    /// Grammar: "flags" IDENTIFIER NEWLINE INDENT FlagsMember { FlagsMember } DEDENT
    /// FlagsMember = UPPER_IDENTIFIER NEWLINE
    /// </summary>
    /// <param name="visibility">Access modifier for this flags type.</param>
    /// <returns>A <see cref="FlagsDeclaration"/> AST node.</returns>
    private FlagsDeclaration ParseFlagsDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected flags name");

        var members = new List<string>();

        // Parse flags body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after flags header");

        if (!Check(type: TokenType.Indent))
        {
            return new FlagsDeclaration(Name: name,
                Members: members,
                Visibility: visibility,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (CheckAndAdvance(type: TokenType.Newline))
            {
                continue;
            }

            string memberName = ConsumeIdentifier(errorMessage: "Expected flags member name");
            members.Add(item: memberName);
            CheckAndAdvance(type: TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after flags body");
        }

        return new FlagsDeclaration(Name: name,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses a crashable type declaration.
    /// Syntax: <c>crashable Name</c> followed by an optional indented body with
    /// field declarations.
    /// </summary>
    private CrashableDeclaration ParseCrashableDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));
        string name = ConsumeIdentifier(errorMessage: "Expected crashable type name");

        var members = new List<SyntaxTree.Declaration>();

        Consume(type: TokenType.Newline, errorMessage: "Expected newline after crashable header");

        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = false;

        if (Check(type: TokenType.Indent))
        {
            ParseCrashableBody(members: members);
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;

        return new CrashableDeclaration(Name: name,
            Members: members,
            Visibility: visibility,
            Location: location);
    }

    /// <summary>
    /// Parses the indented body of a crashable declaration (the opening indent is confirmed but not yet
    /// consumed) into <paramref name="members"/>. Skips newlines/doc-comments and <c>pass</c>, collects
    /// declaration members, and consumes the trailing dedent.
    /// </summary>
    private void ParseCrashableBody(List<SyntaxTree.Declaration> members)
    {
        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
                continue;

            if (CheckAndAdvance(type: TokenType.Pass))
            {
                CheckAndAdvance(type: TokenType.Newline);
                continue;
            }

            ISyntaxTreeNode node = ParseDeclaration();
            if (node is SyntaxTree.Declaration member)
                members.Add(item: member);
            else
                throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                    message: $"Expected declaration inside crashable body, got {node.GetType().Name}");
        }

        if (Check(type: TokenType.Dedent))
            ProcessDedentTokens();
        else if (!IsAtEnd)
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after crashable body");
    }

    /// <summary>
    /// Skips an optional line-break before <c>obeys</c>, then parses the comma-separated protocol list
    /// that follows an <c>obeys</c> keyword (if present). Returns the list (empty when no <c>obeys</c>).
    /// When <paramref name="allowOnlyIf"/> is <see langword="true"/> (the default for entity/record),
    /// each item is parsed via <see cref="ParseObeysProtocol"/> (which handles <c>onlyif</c> conditions);
    /// when <see langword="false"/> (for protocol parent-protocol lists), each item is a plain
    /// <see cref="ParseType"/> call. Newlines between comma-separated items are consumed silently.
    /// </summary>
    private List<TypeExpression> ParseObeysProtocolList(bool allowOnlyIf = true)
    {
        // Allow a line break before 'obeys' in the type header.
        while (Check(type: TokenType.Newline) && PeekToken(offset: 1).Type == TokenType.Obeys)
        {
            Advance();
        }

        var interfaces = new List<TypeExpression>();
        if (!CheckAndAdvance(type: TokenType.Obeys))
        {
            return interfaces;
        }

        do
        {
            // Skip intermediate newlines between comma-separated protocol names.
            while (CheckAndAdvance(type: TokenType.Newline))
            {
                // Consume newline; continue to the next protocol name.
            }

            interfaces.Add(item: allowOnlyIf ? ParseObeysProtocol() : ParseType());
        } while (CheckAndAdvance(type: TokenType.Comma));

        return interfaces;
    }

    /// <summary>
    /// Parses an indented member-declaration body (entity or record) after the header newline is already
    /// consumed. Saves and restores the <see cref="_parsingTypeBody"/> and
    /// <see cref="_parsingStrictRecordBody"/> flags around the loop. Returns whether a <c>pass</c> token
    /// was encountered (used to mark an intentionally empty body).
    /// <para><paramref name="typeName"/> is used in diagnostic messages (e.g. <c>"entity"</c> or
    /// <c>"record"</c>); <paramref name="typeNamePascal"/> is the PascalCase form used in usage hints
    /// (e.g. <c>"Entity"</c> or <c>"Record"</c>).</para>
    /// </summary>
    private bool ParseIndentedTypeMembers(
        List<SyntaxTree.Declaration> members,
        string typeName,
        bool strictRecord,
        string? typeNamePascal = null)
    {
        string pascal = typeNamePascal ?? (char.ToUpperInvariant(typeName[0]) + typeName[1..]);
        bool wasParsingTypeBody = _parsingTypeBody;
        bool wasParsingStrictRecordBody = _parsingStrictRecordBody;
        _parsingTypeBody = true;
        _parsingStrictRecordBody = strictRecord;

        bool hasPass = false;

        if (Check(type: TokenType.Indent))
        {
            ProcessIndentToken();

            while (!Check(type: TokenType.Dedent) && !IsAtEnd)
            {
                hasPass = ParseTypeMemberLoopIteration(members: members,
                    typeName: typeName, pascal: pascal, hasPass: hasPass);
            }

            if (Check(type: TokenType.Dedent))
            {
                ProcessDedentTokens();
            }
            else if (!IsAtEnd)
            {
                throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                    message: $"Expected dedent after {typeName} body");
            }
        }

        _parsingTypeBody = wasParsingTypeBody;
        _parsingStrictRecordBody = wasParsingStrictRecordBody;
        return hasPass;
    }

    /// <summary>
    /// Processes one iteration of the type-member parsing loop: skips blank lines and doc comments,
    /// handles the <c>pass</c> keyword, and parses a single member declaration. Returns the updated
    /// <paramref name="hasPass"/> flag.
    /// </summary>
    private bool ParseTypeMemberLoopIteration(
        List<SyntaxTree.Declaration> members,
        string typeName,
        string pascal,
        bool hasPass)
    {
        if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
        {
            return hasPass;
        }

        if (CheckAndAdvance(type: TokenType.Pass))
        {
            CheckAndAdvance(type: TokenType.Newline);
            return true;
        }

        ISyntaxTreeNode node = ParseDeclaration();
        if (node is RoutineDeclaration)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: $"Routines cannot be declared inside {typeName} bodies. Use 'routine {pascal}Name.MemberRoutine()' syntax instead.");
        }

        if (node is SyntaxTree.Declaration member)
        {
            members.Add(item: member);
        }
        else
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: $"Expected declaration inside {typeName} body, got {node.GetType().Name}");
        }

        return hasPass;
    }

    /// <summary>
    /// Parses a variant (tagged union) declaration.
    /// Syntax: <c>variant Name</c> followed by indented cases with optional associated types.
    /// Variants are sum types where each case can carry different data.
    /// </summary>
    /// <returns>A <see cref="VariantDeclaration"/> AST node.</returns>
    private VariantDeclaration ParseVariantDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected variant name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);


        var members = new List<VariantMember>();

        // Parse variant body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after variant header");

        if (!Check(type: TokenType.Indent))
        {
            return new VariantDeclaration(Name: name,
                GenericParameters: genericParams,
                GenericConstraints: constraints,
                Members: members,
                Location: location);
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            if (CheckAndAdvance(type: TokenType.Newline))
            {
                continue;
            }

            // Each member is a type expression (or None keyword)
            SourceLocation memberLoc = GetLocation();
            TypeExpression memberType;
            if (CheckAndAdvance(type: TokenType.None))
            {
                memberType = new TypeExpression(Name: "None",
                    GenericArguments: null,
                    Location: memberLoc);
            }
            else
            {
                memberType = ParseType();
            }

            members.Add(item: new VariantMember(Type: memberType, Location: memberLoc));
            CheckAndAdvance(type: TokenType.Newline);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after variant body");
        }

        return new VariantDeclaration(Name: name,
            GenericParameters: genericParams,
            GenericConstraints: constraints,
            Members: members,
            Location: location);
    }

    /// <summary>
    /// Parses a protocol (trait/interface) declaration.
    /// Syntax: <c>protocol Name</c> followed by indented routine signatures.
    /// </summary>
    /// <param name="visibility">Access modifier for the protocol.</param>
    /// <returns>A <see cref="ProtocolDeclaration"/> AST node.</returns>
    private ProtocolDeclaration ParseProtocolDeclaration(
        VisibilityModifier visibility = VisibilityModifier.Open)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string name = ConsumeIdentifier(errorMessage: "Expected protocol name");

        // Generic parameters with inline constraints
        List<string>? genericParams = null;
        List<GenericConstraintDeclaration>? inlineConstraints = null;
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
                result = ParseGenericParametersWithConstraints();
            genericParams = result.genericParams;
            inlineConstraints = result.inlineConstraints;

            Consume(type: TokenType.RightBracket,
                errorMessage: ExpectedRightBracketAfterGenericParameters);
        }

        // Parse generic constraints (where clause) - merge with inline constraints
        List<GenericConstraintDeclaration>? constraints =
            ParseGenericConstraints(genericParams: genericParams,
                existingConstraints: inlineConstraints);

        // Parse parent protocols (protocol X obeys Y, Z) using shared helper.
        // Protocol parent-protocol items are plain types (no onlyif conditions here).
        List<TypeExpression> parentProtocols = ParseObeysProtocolList(allowOnlyIf: false);

        // Try constraints again after obeys (supports needs on next line)
        constraints = ParseGenericConstraints(genericParams: genericParams,
            existingConstraints: constraints);

        // Associated-type slot declarations: `relates Iter obeys Iterator[T]` (needs-sibling clause).
        List<AssociatedTypeDeclaration>? associatedTypes = ParseRelatesClauses();

        var memberRoutines = new List<RoutineSignature>();

        // Parse protocol body as indented block
        Consume(type: TokenType.Newline, errorMessage: "Expected newline after protocol header");

        if (!Check(type: TokenType.Indent))
        {
            return new ProtocolDeclaration(Name: name,
                GenericParameters: genericParams,
                ParentProtocols: parentProtocols,
                MemberRoutines: memberRoutines,
                Visibility: visibility,
                Location: location,
                GenericConstraints: constraints)
            {
                AssociatedTypes = associatedTypes
            };
        }

        ProcessIndentToken();

        while (!Check(type: TokenType.Dedent) && !IsAtEnd)
        {
            ParseProtocolBodyItem(
                memberRoutines: memberRoutines,
                associatedTypes: ref associatedTypes);
        }

        if (Check(type: TokenType.Dedent))
        {
            ProcessDedentTokens();
        }
        else if (!IsAtEnd)
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedDedentAfterBody,
                message: "Expected dedent after protocol body");
        }

        return new ProtocolDeclaration(Name: name,
            GenericParameters: genericParams,
            ParentProtocols: parentProtocols,
            MemberRoutines: memberRoutines,
            Visibility: visibility,
            Location: location,
            GenericConstraints: constraints)
        {
            AssociatedTypes = associatedTypes
        };
    }

    /// <summary>
    /// Processes one item in a protocol body loop. Handles newlines/doc-comments, <c>pass</c>,
    /// annotations, <c>common</c>/<c>dangerous</c> qualifiers, <c>relates</c> slot declarations, and
    /// <c>routine</c> signatures. Mutates <paramref name="memberRoutines"/> and
    /// <paramref name="associatedTypes"/> in place.
    /// </summary>
    private void ParseProtocolBodyItem(
        List<RoutineSignature> memberRoutines,
        ref List<AssociatedTypeDeclaration>? associatedTypes)
    {
        if (CheckAndAdvance(TokenType.Newline, TokenType.DocComment))
        {
            return;
        }

        // 'pass' is valid in a protocol body that defines no memberRoutines (marker protocol)
        if (CheckAndAdvance(type: TokenType.Pass))
        {
            CheckAndAdvance(type: TokenType.Newline);
            return;
        }

        // Parse optional annotations on routine signatures (e.g., @readonly)
        List<string> memberRoutineAnnotations = ParseAnnotations();

        // Skip newlines between annotations and routine keyword.
        while (CheckAndAdvance(type: TokenType.Newline))
        {
            // Consume intermediate newlines before the routine keyword.
        }

        // Optional `common` storage-class qualifier — type-level (static) protocol memberRoutine,
        // e.g. `common routine Me.identity() -> V`. The `common` flag is propagated downstream so
        // TypeBodyResolver can set IsInstanceMemberRoutine = false.
        bool memberRoutineIsCommon = CheckAndAdvance(type: TokenType.Common);

        // Optional `dangerous` qualifier — marks the protocol memberRoutine as requiring a `danger`
        // block at the call site (mirrors the impl-side `dangerous routine` syntax).
        bool memberRoutineIsDangerous = CheckAndAdvance(type: TokenType.Dangerous);

        // Allow either qualifier order: `dangerous common routine` is just as valid as
        // `common dangerous routine`.
        if (!memberRoutineIsCommon && CheckAndAdvance(type: TokenType.Common))
        {
            memberRoutineIsCommon = true;
        }

        // Associated-type slot declaration inside protocol body: `relates Key` or `relates Key obeys Hashable`
        if (CheckAndAdvance(type: TokenType.Relates))
        {
            associatedTypes ??= [];
            associatedTypes.Add(item: ParseProtocolRelatesSlot());
            CheckAndAdvance(type: TokenType.Newline);
            return;
        }

        // Parse routine signature
        if (CheckAndAdvance(type: TokenType.Routine))
        {
            memberRoutines.Add(item: ParseProtocolRoutineSignature(
                memberRoutineAnnotations: memberRoutineAnnotations,
                memberRoutineIsCommon: memberRoutineIsCommon,
                memberRoutineIsDangerous: memberRoutineIsDangerous));
            CheckAndAdvance(type: TokenType.Newline);
        }
        else
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.InvalidDeclarationInBody,
                message: $"Unexpected '{CurrentToken.Text}' in protocol body. Only 'routine' signatures are allowed.");
        }
    }

    /// <summary>
    /// Parses an associated-type slot declaration inside a protocol body (the leading <c>relates</c>
    /// keyword is already consumed): <c>relates Key</c> or <c>relates Key obeys Hashable</c>.
    /// </summary>
    private AssociatedTypeDeclaration ParseProtocolRelatesSlot()
    {
        SourceLocation relatesLocation = GetLocation();
        TypeExpression slotNameType = ParseType();
        TypeExpression? constraint = null;
        if (CheckAndAdvance(type: TokenType.Obeys))
        {
            constraint = ParseType();
        }

        return new AssociatedTypeDeclaration(
            Name: slotNameType.Name,
            Constraint: constraint,
            Binding: null,
            Location: relatesLocation);
    }

    /// <summary>
    /// Parses a routine signature inside a protocol body (the leading <c>routine</c> keyword is already
    /// consumed): the name (optionally <c>Me.</c>-qualified), the optional failable <c>!</c>, the
    /// parameter list (including a <c>me</c> self-parameter and variadic params), and the return type.
    /// Appends the pre-parsed <c>common</c>/<c>dangerous</c> qualifier flags to the annotations list.
    /// </summary>
    private RoutineSignature ParseProtocolRoutineSignature(List<string> memberRoutineAnnotations,
        bool memberRoutineIsCommon, bool memberRoutineIsDangerous)
    {
        _routineNameWired = false;
        if (CheckAndAdvance(type: TokenType.Dollar))
        {
            _routineNameWired = true;
        }
        var memberRoutineNameSb = new System.Text.StringBuilder(
            ConsumeIdentifier(errorMessage: "Expected member routine name"));

        // Handle Me.MemberRoutineName syntax for instance member routines
        // Protocol member routines can be: "routine Me.MemberRoutineName()" or "routine memberRoutineName()"
        while (CheckAndAdvance(type: TokenType.Dot))
        {
            memberRoutineNameSb.Append('.');
            memberRoutineNameSb.Append(ConsumeMemberRoutineName(errorMessage: "Expected member routine name after '.'"));
        }

        string memberRoutineName = memberRoutineNameSb.ToString();

        // Support failable member routines: "routine!". The `!` is a STRUCTURED flag on
        // the RoutineSignature — the name stays bare.
        bool memberRoutineIsFailable = CheckAndAdvance(type: TokenType.Bang);

        // Parameters
        Consume(type: TokenType.LeftParen, errorMessage: "Expected '(' after member routine name");
        List<Parameter> parameters = ParseProtocolRoutineParameters();

        // Return type
        TypeExpression? returnType = null;
        if (CheckAndAdvance(type: TokenType.Arrow))
        {
            returnType = ParseType();
        }

        if (memberRoutineIsCommon)
        {
            memberRoutineAnnotations.Add(item: "common");
        }
        if (memberRoutineIsDangerous)
        {
            memberRoutineAnnotations.Add(item: "dangerous");
        }

        return new RoutineSignature(Name: memberRoutineName,
            Parameters: parameters,
            ReturnType: returnType,
            Annotations: memberRoutineAnnotations.Count > 0
                ? memberRoutineAnnotations
                : null,
            Location: GetLocation())
        {
            IsFailable = memberRoutineIsFailable
        };
    }

    /// <summary>
    /// Parses a protocol routine-signature parameter list (the opening <c>(</c> is already consumed):
    /// a <c>me</c> self-parameter or a regular parameter (including variadic <c>name...: T</c>).
    /// Consumes the closing <c>)</c>.
    /// </summary>
    private List<Parameter> ParseProtocolRoutineParameters()
    {
        var parameters = new List<Parameter>();

        if (!Check(type: TokenType.RightParen))
        {
            do
            {
                // Handle 'me' parameter (self-reference, optionally typed)
                if (Check(type: TokenType.Me))
                {
                    Token selfToken = Advance();
                    TypeExpression? selfType = null;
                    if (CheckAndAdvance(type: TokenType.Colon))
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
                    // Regular parameter — supports variadic `name...: T` (a protocol may require a
                    // variadic member, e.g. `common Me.from_literal(elements...: T)` for the literal
                    // protocols). Mirrors the routine-declaration param parse.
                    string paramName =
                        ConsumeIdentifier(errorMessage: "Expected parameter name");
                    bool isVariadic = CheckAndAdvance(type: TokenType.DotDotDot);

                    TypeExpression? paramType = null;
                    if (CheckAndAdvance(type: TokenType.Colon))
                    {
                        paramType = ParseType();
                    }

                    parameters.Add(item: new Parameter(Name: paramName,
                        Type: paramType,
                        DefaultValue: null,
                        Location: GetLocation(),
                        IsVariadic: isVariadic));
                }
            } while (CheckAndAdvance(type: TokenType.Comma));
        }

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after parameters");
        return parameters;
    }

    /// <summary>
    /// Parses a module declaration.
    /// Syntax: <c>module path/to/module</c>
    /// Uses slash separators for module paths.
    /// </summary>
    /// <returns>A <see cref="ModuleDeclaration"/> AST node.</returns>
    private ModuleDeclaration ParseModuleDeclaration()
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        var modulePathSb = new System.Text.StringBuilder();

        // Parse module path - could be multiple identifiers separated by slashes
        // e.g., module standard/errors
        do
        {
            modulePathSb.Append(ConsumeIdentifier(errorMessage: "Expected module name"));
            if (CheckAndAdvance(type: TokenType.Slash))
            {
                modulePathSb.Append('/');
            }
            else
            {
                break;
            }
        } while (true);

        ConsumeStatementTerminator();

        return new ModuleDeclaration(Path: modulePathSb.ToString(), Location: location);
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

        var modulePathSb = new System.Text.StringBuilder();
        string? alias = null;
        List<string>? specificImports = null;
        List<(string Realm, string Name)>? realmImports = null;

        // Parse module path - could be multiple identifiers separated by slashes
        // Dot marks a specific type within the module: import razorforge/Core.Bool
        do
        {
            modulePathSb.Append(ConsumeIdentifier(errorMessage: "Expected module name"));
            if (CheckAndAdvance(type: TokenType.Slash))
            {
                modulePathSb.Append('/');
            }
            else if (CheckAndAdvance(type: TokenType.Dot))
            {
                ParseImportDotClause(modulePathSb: modulePathSb,
                    specificImports: ref specificImports,
                    realmImports: ref realmImports);
                break;
            }
            else
            {
                break;
            }
        } while (true);

        string modulePath = modulePathSb.ToString();

        // Optional alias
        if (CheckAndAdvance(type: TokenType.As))
        {
            alias = ConsumeIdentifier(errorMessage: "Expected alias name");
        }

        ConsumeStatementTerminator();

        return new ImportDeclaration(ModulePath: modulePath,
            Alias: alias,
            SpecificImports: specificImports,
            Location: location,
            RealmImports: realmImports);
    }

    /// <summary>
    /// Parses the clause following a <c>.</c> in an import path (the dot is already consumed): either a
    /// selective import (<c>Module.[A, B, C]</c>), a realm-qualified foreign routine
    /// (<c>Module.C::qsort</c>), or a single type (<c>Core.Bool</c>). Mutates the accumulating module
    /// path and the selective/realm import lists.
    /// </summary>
    private void ParseImportDotClause(System.Text.StringBuilder modulePathSb,
        ref List<string>? specificImports, ref List<(string Realm, string Name)>? realmImports)
    {
        if (CheckAndAdvance(type: TokenType.LeftBracket))
        {
            // Selective imports: Module.[A, B, C]
            specificImports = [];
            do
            {
                string name =
                    ConsumeIdentifier(
                        errorMessage: "Expected type name in selective import");
                specificImports.Add(item: name);
            } while (CheckAndAdvance(type: TokenType.Comma));

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after selective imports");
        }
        else
        {
            // Single member after the dot. `Core.Bool` selects a type; `Module.C::qsort`
            // selects a realm-qualified foreign routine to bring into BARE scope (the `::`
            // disambiguates it from a plain type import).
            string member = ConsumeIdentifier(errorMessage: "Expected name after '.'");
            if (Check(type: TokenType.DoubleColon))
            {
                Advance();
                string routineName = ConsumeIdentifier(
                    errorMessage: "Expected routine name after realm qualifier '::'");
                realmImports ??= [];
                realmImports.Add(item: (member, routineName));
            }
            else
            {
                // Single type: Core.Bool -> module "Core", type "Bool"
                modulePathSb.Append('.');
                modulePathSb.Append(member);
            }
        }
    }

    /// <summary>
    /// Parses a define (type alias/redefinition) declaration.
    /// Syntax: <c>define OldName as NewName</c>
    /// Creates a type alias for cleaner code.
    /// </summary>
    /// <returns>A <see cref="DefineDeclaration"/> AST node.</returns>
    private DefineDeclaration ParseDefineDeclaration(List<string>? annotations = null)
    {
        SourceLocation location = GetLocation(token: PeekToken(offset: -1));

        string oldName = ConsumeIdentifier(errorMessage: "Expected identifier after 'define'");
        Consume(type: TokenType.As, errorMessage: "Expected 'as' in redefinition");
        string newName =
            ConsumeIdentifier(errorMessage: "Expected new identifier in redefinition");

        ConsumeStatementTerminator();

        return new DefineDeclaration(OldName: oldName, NewName: newName, Location: location,
            Annotations: annotations is { Count: > 0 } ? annotations : null);
    }

    /// <summary>
    /// Parses a preset (build-time constant) declaration.
    /// Syntax: <c>preset name: Type = value</c>
    /// </summary>
    /// <returns>A <see cref="PresetDeclaration"/> AST node.</returns>
    private PresetDeclaration ParsePresetDeclaration(bool isSecret = false)
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
            Location: location) { IsSecret = isSecret };
    }

}
