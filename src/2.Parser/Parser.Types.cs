using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;

namespace Compiler.Parser;

/// <summary>
/// Partial class containing type parsing and generic constraints.
/// </summary>
public partial class Parser
{
    /// <summary>
    /// Parses a type expression.
    /// Supports: named types, generic types (Type[T]),
    /// Me (self type), and nullable types (T? = Maybe[T]).
    /// </summary>
    /// <remarks>
    /// The old `T` rvalue-entity prefix was removed 2026-07-13: it was redundant with position and
    /// the move-vs-link distinction (bare entity = move, borrow-wrapper = link), and confusable with
    /// the `T?` Maybe suffix. Entity rvalue-ness is now inferred (see SignatureResolver return
    /// inference); moves are marked by `steal` at use sites.
    /// </remarks>
    /// <returns>A <see cref="TypeExpression"/> AST node.</returns>
    /// <summary>The recognized compiler-classified type-KIND names, all carrying a <c>-Type</c>
    /// suffix so a reader tells a kind-group membership (<c>is RecordType</c>) apart from a capability
    /// (<c>obeys Serializable</c>) or an identity/const-generic (<c>N is U64</c>). Written as
    /// <c>T is &lt;Name&gt;Type</c> in a constraint; the old <c>within &lt;Name&gt;</c> spelling and the
    /// bare lowercase <c>is record</c>/<c>is variant</c> forms are gone — this is the single surface.</summary>
    private static readonly Dictionary<string, ConstraintKind> TypeKindNames = new(StringComparer.Ordinal)
    {
        ["RoutineType"] = ConstraintKind.RoutineType,
        ["TupleType"] = ConstraintKind.TupleType,
        ["RecordType"] = ConstraintKind.RecordType,
        ["ChoiceType"] = ConstraintKind.ChoiceType,
        ["FlagsType"] = ConstraintKind.FlagsType,
        ["VariantType"] = ConstraintKind.VariantType,
        ["EntityType"] = ConstraintKind.EntityType,
        ["CrashableType"] = ConstraintKind.Crashable,
        // `RedirectType` = an `@llvm("…")` primitive that redirects to its raw LLVM repr.
        ["RedirectType"] = ConstraintKind.RedirectType,
        // `AnyType` = an unconstrained generic type parameter (satisfied by any type) — the classifier for
        // `[AnyType T]` / `needs AnyType T`.
        ["AnyType"] = ConstraintKind.AnyType
    };

    /// <summary>Recognizes a <c>T is &lt;Name&gt;Type</c> type-kind constraint. When the identifier after
    /// <c>is</c> is a known kind-group name, yields its <see cref="ConstraintKind"/>; otherwise the
    /// <c>is</c> target is an identity / const-generic type (<c>N is U64</c>).</summary>
    private static bool TryGetTypeKindConstraint(string name, out ConstraintKind kind) =>
        TypeKindNames.TryGetValue(key: name, value: out kind);

    /// <summary>True when the cursor sits on the NEW classifier-first generic-parameter declaration form
    /// — two consecutive identifiers <c>&lt;Kind&gt; &lt;name&gt;</c> (<c>AnyType T</c>, <c>ChoiceType T</c>,
    /// <c>U64 N</c>). Unambiguous because the OLD forms always have a keyword (<c>obeys</c>/<c>is</c>/
    /// <c>in</c>/<c>everywhere</c>) or a delimiter (<c>,</c>/<c>]</c>) as the second token — a bare
    /// <c>ident ident</c> pair never occurred in a bracket or <c>needs</c> clause before. Both surfaces
    /// coexist during the migration off <c>needs T is TypeName</c> → <c>[AnyType T]</c>.</summary>
    private bool IsClassifierFirstParamDecl() =>
        Check(type: TokenType.Identifier) && PeekToken(offset: 1).Type == TokenType.Identifier;

    /// <summary>Parses one NEW classifier-first generic parameter <c>&lt;Kind&gt; &lt;name&gt;</c> (cursor
    /// already confirmed by <see cref="IsClassifierFirstParamDecl"/>). A known kind keyword
    /// (<c>AnyType</c>/<c>ChoiceType</c>/…) yields that <see cref="ConstraintKind"/>; any other leading
    /// identifier is a concrete type, so the param is a CONST-generic of that type (<c>U64 N</c> → N is a
    /// U64 value). Returns the param name plus the constraint node — the SAME AST the old
    /// <c>T is &lt;Kind&gt;</c> / <c>N is U64</c> forms produced, so the resolver is unchanged.</summary>
    private (string paramName, GenericConstraintDeclaration constraint) ParseClassifierFirstParam()
    {
        SourceLocation location = GetLocation();
        string classifier = ConsumeIdentifier(
            errorMessage: "Expected a type-kind or type before the generic parameter name");
        string paramName = ConsumeIdentifier(errorMessage: "Expected generic parameter name");

        if (TryGetTypeKindConstraint(name: classifier, out ConstraintKind kind))
        {
            return (paramName, new GenericConstraintDeclaration(
                ParameterName: paramName,
                ConstraintType: kind,
                ConstraintTypes: null,
                Location: location));
        }

        // Concrete-type classifier → const-generic: `U64 N` means N is a build-time U64 value.
        var constType = new TypeExpression(Name: classifier, GenericArguments: null, Location: location);
        return (paramName, new GenericConstraintDeclaration(
            ParameterName: paramName,
            ConstraintType: ConstraintKind.ConstGeneric,
            ConstraintTypes: [constType],
            Location: location));
    }

    private const string TypeKindNamesHint =
        "RecordType, VariantType, EntityType, ChoiceType, FlagsType, TupleType, RoutineType, " +
        "ZeroMemvarType, CrashableType, TypeName";

    /// <summary>Parses the target of an <c>is</c> generic constraint after the <c>is</c> keyword has
    /// been consumed. A known <c>-Type</c> kind-group name (<c>is RecordType</c>) becomes a
    /// compiler-classified kind constraint; any other type identifier (<c>N is U64</c>) is a
    /// const-generic / identity constraint. Guarded by the inline (<c>[T is …]</c>) and <c>needs</c> sites
    /// so both accept exactly the same surface. NOTE: the runtime <c>is Crashable e</c> error-catch
    /// PATTERN is a different parse site (expression position) and is unaffected.</summary>
    private GenericConstraintDeclaration ParseIsConstraint(string paramName, SourceLocation location)
    {
        if (Check(type: TokenType.Identifier) &&
            TryGetTypeKindConstraint(name: CurrentToken.Text, out ConstraintKind kind))
        {
            Advance();
            return new GenericConstraintDeclaration(
                ParameterName: paramName,
                ConstraintType: kind,
                ConstraintTypes: null,
                Location: location);
        }

        if (Check(type: TokenType.Identifier))
        {
            // Const-generic / identity: `N is U64`. Validation is deferred to semantic analysis.
            TypeExpression constType = ParseType();
            return new GenericConstraintDeclaration(
                ParameterName: paramName,
                ConstraintType: ConstraintKind.ConstGeneric,
                ConstraintTypes: [constType],
                Location: location);
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.InvalidConstraintKind,
            message:
            $"Expected a type-kind ({TypeKindNamesHint}) or a type after 'is' in a constraint. " +
            "The lowercase 'is record'/'is variant' and 'within' forms were removed — use 'is RecordType' etc.");
    }

    private TypeExpression ParseType()
    {
        TypeExpression baseType = ParseBaseType();

        // Handle nullable suffix: T? Maybe[T]
        if (Match(type: TokenType.Question))
        {
            return new TypeExpression(Name: "Maybe",
                GenericArguments: [baseType],
                Location: baseType.Location);
        }

        return baseType;
    }

    /// <summary>
    /// Parses a base type expression without nullable suffix.
    /// </summary>
    /// <remarks>
    /// Type forms in priority order:
    /// 1. Me - Self type in protocols/member routines
    /// 2. Name[T, U] - Generic named type
    /// 3. Name - Simple named type
    ///
    /// Named types support qualified paths like razorforge/Collections.Dict
    /// for referencing types from other modules in type annotations.
    /// </remarks>
    private TypeExpression ParseBaseType()
    {
        SourceLocation location = GetLocation();

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 1: Me - self type in protocols/member routines (like Self in Rust)
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.MyType))
        {
            // `Me` may be followed by an associated-type projection: `Me/Iter`, `Me/Iter/Inner`.
            // Carry it in the flattened name; the resolver walks `/` segments (Me → owner type,
            // then each following segment is an associated-type projection).
            if (Check(type: TokenType.Slash))
            {
                var meSb = new System.Text.StringBuilder("Me");
                while (Match(type: TokenType.Slash))
                {
                    meSb.Append('/');
                    meSb.Append(ConsumeIdentifier(
                        errorMessage: "Expected associated-type name after '/' in projection"));
                }
                return new TypeExpression(Name: meSb.ToString(), GenericArguments: null,
                    Location: location);
            }
            return new TypeExpression(Name: "Me", GenericArguments: null, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 1b: None - the void / unit type (a keyword, so it can't be a bare Identifier below).
        // `None` is the canonical name for "nothing" — both a type (void return / field) and the
        // variant empty branch. It resolves to the zero-sized void type.
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.None))
        {
            return new TypeExpression(Name: "None", GenericArguments: null, Location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 1c: `${m.type}` — a comptime type-position splice of an expand handle's member type.
        // Used in decl-position expand column templates (e.g. `Array[${m.type}, N]`) and, later, in
        // type-arg / pattern positions. Resolves to the current member's static type at expansion.
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.SpliceOpen))
        {
            Expression spliced = ParseExpression();
            Consume(type: TokenType.RightBrace, errorMessage: "Expected '}' to close '${...}' splice");
            // `${handle.type}` — a comptime TYPE splice of an expand handle's member type.
            if (spliced is MemberExpression { Object: IdentifierExpression handleId, MemberName: "type" })
                return new TypeExpression(Name: "splice", GenericArguments: null, Location: location,
                    SpliceHandle: handleId.Name);
            // Otherwise a comptime VALUE splice used as a const-generic argument, e.g. the carrier
            // payload size `Array[U8, ${max(T.data_size().byte_size(), 8)}]`. Carry the expression for
            // the monomorphizer to fold into a ConstGenericValueTypeInfo.
            return new TypeExpression(Name: "splice_value", GenericArguments: null, Location: location,
                ComptimeValue: spliced);
        }

        // Brace-less comptime type splice: `$typeof(m)` (a TYPE splice of an expand handle's member type)
        // or `$sizeof(m)` etc. (a comptime VALUE splice used as a const-generic argument).
        if (Match(type: TokenType.Dollar))
        {
            Expression spliced = ParseDollarSpliceInner();
            if (spliced is CallExpression
                {
                    Callee: IdentifierExpression { Name: "typeof" },
                    Arguments: [IdentifierExpression handle]
                })
                return new TypeExpression(Name: "splice", GenericArguments: null, Location: location,
                    SpliceHandle: handle.Name);
            return new TypeExpression(Name: "splice_value", GenericArguments: null, Location: location,
                ComptimeValue: spliced);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 2: Tuple type - (T, U) or (T,)
        // ═══════════════════════════════════════════════════════════════════════════
        if (Match(type: TokenType.LeftParen))
        {
            return ParseTupleOrParenthesizedType(location: location);
        }

        // ═══════════════════════════════════════════════════════════════════════════
        // CASE 4/5: Named type - simple or generic
        // ═══════════════════════════════════════════════════════════════════════════
        return ParseNamedType(location: location);
    }

    /// <summary>
    /// Parses a tuple type or a single parenthesized type after the opening <c>(</c> has been consumed:
    /// the empty tuple <c>()</c>, a bare parenthesized <c>(T)</c>, a single-element tuple <c>(T,)</c>, or
    /// a multi-element tuple <c>(T, U, ...)</c>.
    /// </summary>
    private TypeExpression ParseTupleOrParenthesizedType(SourceLocation location)
    {
        var elementTypes = new List<TypeExpression>();

        // Empty tuple type `()` — a zero-element parameter list, the param slot of a no-argument
        // routine type: `Routine[(), None]`. Kept as a `Tuple` with an empty argument list so the
        // type resolver produces zero parameter types.
        if (Check(type: TokenType.RightParen))
        {
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple type");
            return new TypeExpression(Name: "Tuple",
                GenericArguments: elementTypes,
                Location: location);
        }

        elementTypes.Add(item: ParseType());

        if (!Match(type: TokenType.Comma))
        {
            // Single parenthesized type without comma: just (T)
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after type");
            return elementTypes[index: 0];
        }

        // Single-element tuple: (T,)
        if (Check(type: TokenType.RightParen))
        {
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple type");
            return new TypeExpression(Name: "Tuple",
                GenericArguments: elementTypes,
                Location: location);
        }

        // Multi-element tuple: (T, U, ...)
        do
        {
            elementTypes.Add(item: ParseType());
        } while (Match(type: TokenType.Comma) && !Check(type: TokenType.RightParen));

        Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after tuple type");
        return new TypeExpression(Name: "Tuple",
            GenericArguments: elementTypes,
            Location: location);
    }

    /// <summary>
    /// Parses a named type (CASE 4/5): a simple, qualified, realm-qualified, or generic type. Forms:
    /// a user simple type, <c>List[T]</c> generic, <c>Dict[Text, S32]</c> multi-param generic, or
    /// <c>FixedBytes[4]</c> const generic.
    /// </summary>
    private TypeExpression ParseNamedType(SourceLocation location)
    {
        if (!Match(type: TokenType.Identifier))
        {
            throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedType,
                message: $"Expected type, got {CurrentToken.Type} ('{CurrentToken.Text}')");
        }

        string name = PeekToken(offset: -1)
           .Text;

        string? realm = ReadRealmQualifier(name: ref name);

        // Support qualified type paths like RazorForge/Collections.Dict
        // This allows referencing types from other modules in type annotations
        name = ReadQualifiedTypePath(head: name);

        // ─────────────────────────────────────────────────────────────────────
        // Simple type without generics
        // ─────────────────────────────────────────────────────────────────────
        if (!Match(type: TokenType.LeftBracket))
        {
            return new TypeExpression(Name: name, GenericArguments: null, Location: location, Realm: realm);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Generic type with type arguments
        // ─────────────────────────────────────────────────────────────────────
        var typeArgs = new List<TypeExpression>();

        do
        {
            typeArgs.Add(item: ParseTypeOrConstGeneric());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightBracket, errorMessage: "Expected ']' after type arguments");

        return new TypeExpression(Name: name, GenericArguments: typeArgs, Location: location, Realm: realm);
    }

    /// <summary>
    /// Reads an optional realm qualifier (<c>RF::Core.List</c>) starting after the head identifier. When
    /// a <c>::</c> follows, the head is the realm tag and the <c>.</c>/<c>/</c>-segmented remainder is
    /// consumed into <paramref name="name"/>; returns the realm tag (or null when absent).
    /// </summary>
    private string? ReadRealmQualifier(ref string name)
    {
        // Realm qualifier: `RF::Core.List` — the identifier before `::` is a realm tag (RF/SF), and the
        // rest is a qualified type name resolved in that realm. `RF::` reaches the RazorForge/bare realm
        // from a Suflae file (the resolver skips the entity->Roamed lowering for it). The qualified name
        // after `::` uses `.`/`/` segment separators (e.g. `RF::Core.List`), consumed here so the general
        // `/`-path loop below is a no-op.
        if (!Match(type: TokenType.DoubleColon))
        {
            return null;
        }

        string realm = name;
        var realmSb = new System.Text.StringBuilder(
            ConsumeIdentifier(errorMessage: "Expected type name after realm qualifier '::'"));
        while (Check(type: TokenType.Dot) || Check(type: TokenType.Slash))
        {
            char sep = Match(type: TokenType.Dot) ? '.' : '/';
            if (sep == '/')
                Match(type: TokenType.Slash);
            realmSb.Append(sep);
            realmSb.Append(ConsumeIdentifier(
                errorMessage: "Expected name component after '.'/'/' in realm-qualified type"));
        }
        name = realmSb.ToString();
        return realm;
    }

    /// <summary>
    /// Reads a slash-separated qualified type path (<c>RazorForge/Collections.Dict</c>) starting from an
    /// already-consumed <paramref name="head"/> identifier, where a <c>.</c> separates the trailing type
    /// name from the module path and ends the path.
    /// </summary>
    private string ReadQualifiedTypePath(string head)
    {
        var nameSb = new System.Text.StringBuilder(head);
        while (Match(type: TokenType.Slash))
        {
            nameSb.Append('/');
            nameSb.Append(ConsumeIdentifier(errorMessage: "Expected module path component after '/'"));

            // Dot separates the type name from the slash-based module path: razorforge/Core.Bool
            if (Match(type: TokenType.Dot))
            {
                nameSb.Append('.');
                nameSb.Append(ConsumeIdentifier(errorMessage: "Expected type name after '.'"));
                break; // Dot marks the end of the path (rest is the type name)
            }
        }

        return nameSb.ToString();
    }

    /// <summary>
    /// Parses a type expression or a const generic literal.
    /// Used for generic arguments like FixedBytes[4].
    /// Supports: integers, booleans, letters, and choice values (e.g., Color.Red).
    /// </summary>
    private TypeExpression ParseTypeOrConstGeneric()
    {
        SourceLocation location = GetLocation();

        // Check for boolean literal (const generic)
        if (Match(TokenType.True, TokenType.False))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for integer literal (const generic)
        // Support both typed literals (10u32) and untyped literals (10)
        if (Match(TokenType.UndecidedInteger,
                TokenType.IntegerLiteral,
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
                TokenType.S256Literal,
                TokenType.U256Literal,
                TokenType.AddressLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Check for letter/character literal (const generic)
        if (Match(TokenType.CharacterLiteral, TokenType.ByteLetterLiteral))
        {
            string value = PeekToken(offset: -1)
               .Text;
            return new TypeExpression(Name: value, GenericArguments: null, Location: location);
        }

        // Otherwise parse as normal type
        return ParseType();
    }

    /// <summary>
    /// Parses a single bracket argument uniformly as an expression, for the parser's
    /// classification-free <see cref="SyntaxTree.BracketAccessExpression"/>.
    /// <see cref="BracketReclassifyPass"/> later decides whether the whole bracket is a generic
    /// type-argument list or a value index and, in the generic case, converts each argument to a
    /// <see cref="TypeExpression"/> via <c>ExpressionToTypeArg</c>.
    /// </summary>
    /// <remarks>
    /// The one grammar gap between "expression" and "type argument" is the self-TYPE token
    /// <c>Me</c> (<see cref="TokenType.MyType"/>, and its projections <c>Me/Iter</c>), which is not
    /// a valid expression primary. Those are handled here by materializing <c>Me</c>/<c>Me/Iter</c>
    /// as an identifier / <c>/</c>-chain expression — structurally identical to how <c>S/Iter</c>
    /// parses — so the reclassifier's <c>ExpressionToTypeArg</c> flattens both uniformly. Note this
    /// is the capitalized self-TYPE only; the lowercase <c>me</c> receiver
    /// (<see cref="TokenType.Me"/>) is a normal expression and is NOT intercepted here (so
    /// <c>me.list[me.index]</c> parses its inner <c>me.index</c> as an ordinary member access).
    /// Everything else (<c>S64</c>, <c>i+1</c>, <c>4</c>, <c>List[S64]</c>, <c>S/Iter</c>) is a
    /// plain expression.
    /// </remarks>
    private Expression ParseBracketArg()
    {
        if (Check(type: TokenType.MyType))
        {
            SourceLocation location = GetLocation();
            string headText = CurrentToken.Text;
            Advance(); // consume Me / MyType
            Expression head = new IdentifierExpression(Name: headText, Location: location);

            // Projection chain: Me/Iter, Me/Iter/Inner — modeled as a left-nested `/` chain,
            // matching the shape ordinary `S/Iter` produces from expression parsing.
            while (Match(type: TokenType.Slash))
            {
                string seg = ConsumeIdentifier(
                    errorMessage: "Expected associated-type name after '/' in projection");
                head = new BinaryExpression(Left: head,
                    Operator: BinaryOperator.TrueDivide,
                    Right: new IdentifierExpression(Name: seg, Location: location),
                    Location: location);
            }

            return head;
        }

        return ParseExpression();
    }

    /// <summary>
    /// Parses generic parameters with optional inline constraints like [T obeys Integral].
    /// Returns both the parameter names and any inline constraints found.
    /// </summary>
    /// <remarks>
    /// Inline constraint forms (inside brackets):
    ///
    /// PROTOCOL CONSTRAINTS (obeys):
    /// [T obeys Comparable] - Single protocol
    /// [T obeys Comparable, Hashable] - Multiple protocols
    ///
    /// TYPE KIND CONSTRAINTS (is):
    /// [T is record] - Must be a value type (record)
    /// [T is entity] - Must be a reference type (entity)
    /// [T is routine] - Must be a routine type
    /// [T is choice] - Must be a choice type
    /// [T is variant] - Must be a variant type
    /// [N is S32] - Const generic (N is a build-time constant of type S32)
    ///
    /// TYPE EQUALITY CONSTRAINTS (in):
    /// [T in [S32, S64, F64]] - T must be one of the listed types
    ///
    /// DISAMBIGUATION CHALLENGE:
    /// When parsing "T obeys A, B", we need to distinguish between:
    ///  - Multiple protocols for same param: [T obeys A, B]
    ///  - Next parameter with constraint: [T obeys A, U obeys B]
    /// We look ahead to check if the next identifier has obeys/is/in after it.
    /// </remarks>
    private (List<string> genericParams, List<GenericConstraintDeclaration>? inlineConstraints)
        ParseGenericParametersWithConstraints()
    {
        var genericParams = new List<string>();
        var inlineConstraints = new List<GenericConstraintDeclaration>();

        // ═══════════════════════════════════════════════════════════════════════════
        // Parse each generic parameter with optional inline constraint
        // ═══════════════════════════════════════════════════════════════════════════
        do
        {
            SourceLocation location = GetLocation();

            // ─────────────────────────────────────────────────────────────────────
            // NEW classifier-first form: `[AnyType T]`, `[ChoiceType T]`, `[U64 N]`.
            // The classifier carries the kind; a bare `AnyType` is just an unconstrained
            // param (no constraint node, matching old `[T]`), other kinds/const-types keep
            // their constraint.
            // ─────────────────────────────────────────────────────────────────────
            if (IsClassifierFirstParamDecl())
            {
                (string clsParam, GenericConstraintDeclaration clsConstraint) = ParseClassifierFirstParam();
                genericParams.Add(item: clsParam);
                if (clsConstraint.ConstraintType != ConstraintKind.AnyType)
                {
                    inlineConstraints.Add(item: clsConstraint);
                }
                continue;
            }

            string paramName = ConsumeIdentifier(errorMessage: "Expected generic parameter name");
            genericParams.Add(item: paramName);

            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 1: obeys - protocol conformance
            // ─────────────────────────────────────────────────────────────────────
            // Forms: T obeys Protocol
            // T obeys Protocol1, Protocol2 (multiple protocols)
            if (Match(type: TokenType.Obeys))
            {
                inlineConstraints.Add(item: new GenericConstraintDeclaration(
                    ParameterName: paramName,
                    ConstraintType: ConstraintKind.Obeys,
                    ConstraintTypes: ParseInlineObeysProtocolList(),
                    Location: location));
            }
            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 2: is - type kind or const generic
            // ─────────────────────────────────────────────────────────────────────
            // Type kinds: T is record/entity/routine/choice/variant
            // Const generic: N is S32 (N is a build-time S32 value)
            else if (Match(type: TokenType.Is))
            {
                inlineConstraints.Add(item: ParseIsConstraint(paramName: paramName, location: location));
            }
            // ─────────────────────────────────────────────────────────────────────
            // CONSTRAINT TYPE 3: in - type equality (must be one of listed types)
            // ─────────────────────────────────────────────────────────────────────
            // Form: T in [S32, S64, F64]
            else if (Match(type: TokenType.In))
            {
                inlineConstraints.Add(item: ParseInlineInConstraint(paramName: paramName,
                    location: location));
            }
            // No constraint for this parameter, continue to next
        } while (Match(type: TokenType.Comma));

        return (genericParams, inlineConstraints.Count > 0
            ? inlineConstraints
            : null);
    }

    /// <summary>
    /// Parses the protocol list of an INLINE <c>[T obeys A, B]</c> constraint after <c>obeys</c> has
    /// been consumed. A trailing comma ends the list when it is followed by <c>]</c> or a new
    /// <c>Param obeys/is/in</c> parameter constraint (distinguishing <c>[T obeys A, B]</c> —
    /// multiple protocols — from <c>[T obeys A, U obeys B]</c> — the next parameter).
    /// </summary>
    private List<TypeExpression> ParseInlineObeysProtocolList()
    {
        var constraintTypes = new List<TypeExpression>();
        do
        {
            constraintTypes.Add(item: ParseType());
            // Continue if comma but next token is NOT an identifier followed by obeys/is/in or greater
            // This handles both "T obeys A, B" (multiple protocols) and "T obeys A, U obeys B" (next param)
        } while (Match(type: TokenType.Comma) && !Check(type: TokenType.RightBracket) &&
                 !(Check(type: TokenType.Identifier) && (PeekToken(offset: 1)
                    .Type == TokenType.Obeys || PeekToken(offset: 1)
                    .Type == TokenType.Is || PeekToken(offset: 1)
                    .Type == TokenType.In)));

        return constraintTypes;
    }

    /// <summary>
    /// Parses an INLINE <c>[T in [S32, S64, F64]]</c> type-equality constraint after <c>in</c> has been
    /// consumed.
    /// </summary>
    private GenericConstraintDeclaration ParseInlineInConstraint(string paramName,
        SourceLocation location)
    {
        Consume(type: TokenType.LeftBracket,
            errorMessage: "Expected '[' after 'in' for type equality constraint");

        var equalityTypes = new List<TypeExpression>();
        do
        {
            equalityTypes.Add(item: ParseType());
        } while (Match(type: TokenType.Comma));

        Consume(type: TokenType.RightBracket,
            errorMessage: "Expected ']' after type list");

        return new GenericConstraintDeclaration(
            ParameterName: paramName,
            ConstraintType: ConstraintKind.TypeEquality,
            ConstraintTypes: equalityTypes,
            Location: location);
    }

    /// <summary>
    /// Parses generic constraints for type parameters using 'needs' clauses.
    /// Called after generic parameters have been parsed.
    /// </summary>
    /// <remarks>
    /// This parses the EXTERNAL needs clause form (after brackets):
    ///
    /// Example:
    /// record Container[T, U]
    /// needs T obeys Comparable, U is entity
    ///  ...
    ///
    /// The same constraint kinds are supported as inline constraints:
    /// - obeys: protocol conformance
    /// - is: type kind (record/entity/routine/choice/variant) or const generic
    /// - in: type equality (must be one of listed types)
    ///
    /// Multiple needs clauses can be chained, or constraints can be comma-separated:
    /// needs T obeys A needs U obeys B (chained)
    /// needs T obeys A, U obeys B (comma-separated)
    /// </remarks>
    /// <summary>Parses one protocol in a type header's <c>obeys</c> list, plus an optional trailing
    /// <c>onlyif (cond, …)</c> conditional-conformance clause attached to that protocol.</summary>
    private TypeExpression ParseObeysProtocol()
    {
        TypeExpression proto = ParseType();
        if (Match(type: TokenType.OnlyIf))
            proto.ConformanceConditions = ParseOnlyIfConditions();
        return proto;
    }

    /// <summary>Parses an <c>onlyif</c> clause into its AND-list of <c>&lt;param&gt; obeys &lt;protocol&gt;</c>
    /// conditions. Parens are OPTIONAL: a single condition may be written bare (<c>onlyif T obeys P</c>);
    /// MULTIPLE conditions need parens (<c>onlyif (T obeys P, U obeys Q)</c>) so their comma separators
    /// don't collide with the outer obeys-list comma. Comma = AND.</summary>
    private List<GenericConstraintDeclaration> ParseOnlyIfConditions()
    {
        var conds = new List<GenericConstraintDeclaration>();
        if (Match(type: TokenType.LeftParen))
        {
            do
            {
                while (Match(type: TokenType.Newline))
                {
                    // Skip newlines between comma-separated onlyif conditions.
                }
                conds.Add(item: ParseOneOnlyIfCondition());
            } while (Match(type: TokenType.Comma));
            Consume(type: TokenType.RightParen, errorMessage: "Expected ')' after 'onlyif' conditions");
        }
        else
        {
            // Bare single condition — a following comma belongs to the outer obeys list, not here.
            conds.Add(item: ParseOneOnlyIfCondition());
        }
        return conds;
    }

    /// <summary>Parses one <c>&lt;param&gt; obeys &lt;protocol&gt;</c> condition of an <c>onlyif</c> clause.</summary>
    private GenericConstraintDeclaration ParseOneOnlyIfCondition()
    {
        SourceLocation loc = GetLocation();
        string paramName =
            ConsumeIdentifier(errorMessage: "Expected type parameter name in 'onlyif' condition");
        Consume(type: TokenType.Obeys, errorMessage: "Expected 'obeys' in 'onlyif' condition");
        return new GenericConstraintDeclaration(
            ParameterName: paramName,
            ConstraintType: ConstraintKind.Obeys,
            ConstraintTypes: [ParseType()],
            Location: loc);
    }

    private List<GenericConstraintDeclaration>? ParseGenericConstraints(
        List<string>? genericParams,
        List<GenericConstraintDeclaration>? existingConstraints = null)
    {
        // Allow needs clauses even without explicit generic params (implicit generics from parameter types)
        // But only if there's actually a 'needs' keyword ahead — peek through newlines
        if (genericParams == null || genericParams.Count == 0)
        {
            int offset = 0;
            while (PeekToken(offset: offset)
                      .Type == TokenType.Newline)
            {
                offset++;
            }

            if (PeekToken(offset: offset)
                   .Type != TokenType.Needs)
            {
                return existingConstraints;
            }

            // genericParams may remain null or empty; constraint parsing proceeds without it.
        }

        List<GenericConstraintDeclaration> constraints = existingConstraints != null
            ? [..existingConstraints]
            : [];

        // ═══════════════════════════════════════════════════════════════════════════
        // Parse needs clauses: needs T obeys Protocol
        // ═══════════════════════════════════════════════════════════════════════════
        // Each parameter can have its own needs clause or they can be comma-separated
        // Skip newlines between needs clauses only when 'needs' obeys
        while (SkipNewlinesIfFollowedBy(type: TokenType.Needs) &&
               Match(type: TokenType.Needs))
        {
            do
            {
                SourceLocation location = GetLocation();

                // NEW classifier-first form: `needs AnyType T`, `needs ChoiceType T`, `needs U64 N`.
                // Keep the AnyType constraint here too (unlike the bracket): for a universal template
                // whose owner IS the param (`routine T.diagnose() needs AnyType T`), the AnyType
                // constraint is what folds T into the routine's GenericParameters downstream.
                if (IsClassifierFirstParamDecl())
                {
                    (string _, GenericConstraintDeclaration clsConstraint) = ParseClassifierFirstParam();
                    constraints.Add(item: clsConstraint);
                    continue;
                }

                string paramName = ConsumeIdentifier(errorMessage: "Expected type parameter name");

                // Note: Type parameter validation (whether paramName is in genericParams)
                // is intentionally deferred to semantic analysis for better error reporting.

                // ─────────────────────────────────────────────────────────────────────
                // Parse constraint kind and types (same logic as inline constraints)
                // ─────────────────────────────────────────────────────────────────────
                constraints.Add(item: ParseNeedsConstraintClause(paramName: paramName,
                    location: location));

                // Continue parsing if there's a comma
            } while (Match(type: TokenType.Comma));
        }

        return constraints.Count > 0
            ? constraints
            : null;
    }

    /// <summary>
    /// Parses one <c>needs</c>-clause constraint for the given parameter after its name has been
    /// consumed: <c>obeys</c> (protocol conformance list), <c>is</c> (type-kind / const-generic),
    /// <c>in</c> (type-equality list), or <c>everywhere</c> (standard-impl eligibility gate on <c>Me</c>).
    /// </summary>
    private GenericConstraintDeclaration ParseNeedsConstraintClause(string paramName,
        SourceLocation location)
    {
        if (Match(type: TokenType.Obeys))
        {
            // T obeys Protocol1, Protocol2
            List<TypeExpression> constraintTypes = ParseNeedsObeysProtocolList();
            return new GenericConstraintDeclaration(
                ParameterName: paramName,
                ConstraintType: ConstraintKind.Obeys,
                ConstraintTypes: constraintTypes,
                Location: location);
        }

        if (Match(type: TokenType.Is))
        {
            return ParseIsConstraint(paramName: paramName, location: location);
        }

        if (Match(type: TokenType.In))
        {
            // T in [s32, s64, u32] - type equality constraint with list syntax
            Consume(type: TokenType.LeftBracket,
                errorMessage: "Expected '[' after 'in' for type equality constraint");

            var equalityTypes = new List<TypeExpression>();
            do
            {
                equalityTypes.Add(item: ParseType());
            } while (Match(type: TokenType.Comma));

            Consume(type: TokenType.RightBracket,
                errorMessage: "Expected ']' after type list");

            return new GenericConstraintDeclaration(
                ParameterName: paramName,
                ConstraintType: ConstraintKind.TypeEquality,
                ConstraintTypes: equalityTypes,
                Location: location);
        }

        if (Match(type: TokenType.Everywhere))
        {
            // `needs <Protocol> everywhere` — standard-impl eligibility gate: the owner `Me`
            // obeys the protocol IFF every member (allmemvarof/branchof/caseof, per kind) obeys it.
            // There is no explicit subject; the identifier just consumed as `paramName` is
            // actually the protocol name, and the subject is implicitly `Me`.
            return new GenericConstraintDeclaration(
                ParameterName: "Me",
                ConstraintType: ConstraintKind.Everywhere,
                ConstraintTypes:
                [
                    new TypeExpression(Name: paramName, GenericArguments: null,
                        Location: location)
                ],
                Location: location);
        }

        throw ThrowParseError(code: GrammarDiagnosticCode.ExpectedConstraintType,
            message: "Expected 'obeys', 'is', 'in', or 'everywhere' in generic constraint");
    }

    /// <summary>
    /// Parses the protocol list of a <c>needs T obeys A, B</c> clause after <c>obeys</c> has been
    /// consumed. A comma is treated as a within-clause separator only when it is NOT followed by a new
    /// <c>Param obeys/is/in</c> constraint (that comma belongs to the outer constraint-separator loop).
    /// </summary>
    private List<TypeExpression> ParseNeedsObeysProtocolList()
    {
        var constraintTypes = new List<TypeExpression> { ParseType() };
        while (Check(type: TokenType.Comma))
        {
            // Peek PAST the comma (and any newlines): if a new "Param obeys/is/in"
            // constraint follows, this comma separates whole constraints — leave it
            // unconsumed for the outer constraint-separator loop. Consuming it here
            // (the old bug) dropped the next constraint, e.g. the `U obeys B` in
            // `needs T obeys A, U obeys B`, on routines and types alike.
            int peek = 1;
            while (PeekToken(offset: peek).Type == TokenType.Newline)
            {
                peek++;
            }

            if (PeekToken(offset: peek).Type == TokenType.Identifier &&
                PeekToken(offset: peek + 1).Type is TokenType.Obeys or TokenType.Is
                    or TokenType.In)
            {
                break;
            }

            Match(type: TokenType.Comma);
            while (Match(type: TokenType.Newline))
            {
                // Skip newlines between comma-separated constraint types.
            }
            constraintTypes.Add(item: ParseType());
        }

        return constraintTypes;
    }

    /// <summary>
    /// Parses <c>relates</c> clauses on a type declaration — a <c>needs</c>-sibling clause placed
    /// after the header (and any <c>needs</c>), before the indented body. Two forms:
    /// <list type="bullet">
    ///   <item>Protocol slot declaration: <c>relates Iter obeys Iterator[T]</c></item>
    ///   <item>Implementer binding: <c>relates ListEmitter[T] as Iter</c></item>
    /// </list>
    /// Returns the accumulated list (merged with <paramref name="existing"/>), or null if none.
    /// </summary>
    private List<AssociatedTypeDeclaration>? ParseRelatesClauses(
        List<AssociatedTypeDeclaration>? existing = null)
    {
        List<AssociatedTypeDeclaration> related = existing != null ? [..existing] : [];

        // Each clause may be preceded by doc comments and blank lines (a slot is often
        // documented just like a member). Only commit to consuming that trivia once a
        // `relates` keyword is confirmed to follow, so trivia before the indented body
        // (which has no `relates`) is left intact for the body parser.
        while (true)
        {
            int offset = 0;
            while (PeekToken(offset: offset).Type is TokenType.Newline or TokenType.DocComment)
            {
                offset++;
            }

            if (PeekToken(offset: offset).Type != TokenType.Relates)
            {
                break;
            }

            while (Match(TokenType.Newline, TokenType.DocComment))
            {
                // Skip newlines and doc-comments between relates clauses.
            }
            Match(type: TokenType.Relates);

            SourceLocation location = GetLocation();

            // Parse the first token group as a type. For a slot declaration it is a bare
            // identifier (the slot name); for a binding it is the concrete type.
            TypeExpression first = ParseType();

            if (Match(type: TokenType.Obeys))
            {
                // Constrained slot declaration: `relates Iter obeys Iterator[T]`.
                TypeExpression constraint = ParseType();
                related.Add(item: new AssociatedTypeDeclaration(
                    Name: first.Name,
                    Constraint: constraint,
                    Binding: null,
                    Location: location));
            }
            else if (Match(type: TokenType.As))
            {
                // Implementer binding: `relates ListEmitter[T] as Iter`.
                string slotName = ConsumeIdentifier(
                    errorMessage: "Expected associated-type name after 'as' in 'relates' clause");
                related.Add(item: new AssociatedTypeDeclaration(
                    Name: slotName,
                    Constraint: null,
                    Binding: first,
                    Location: location));
            }
            else
            {
                // Bare slot declaration: `relates Key` — an associated type with no
                // constraint and no binding (the implementer supplies it via `relates ... as`).
                related.Add(item: new AssociatedTypeDeclaration(
                    Name: first.Name,
                    Constraint: null,
                    Binding: null,
                    Location: location));
            }
        }

        return related.Count > 0 ? related : null;
    }


}
