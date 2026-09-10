using Builder.Diagnostics;
using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

/// <summary>
/// Phase 5: Expression analysis.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Expression Analysis

    /// <summary>
    /// Analyzes an expression and returns its resolved type.
    /// Also sets the ResolvedType property on the expression.
    /// </summary>
    /// <param name="expression">The expression to analyze.</param>
    /// <param name="expectedType">Optional expected type for contextual inference (e.g., return type, parameter type).</param>
    /// <returns>The resolved type of the expression.</returns>
    private TypeSymbol AnalyzeExpression(Expression expression, TypeSymbol? expectedType = null)
    {
        TypeSymbol resultType = expression switch
        {
            LiteralExpression literal => AnalyzeLiteralExpression(literal: literal,
                expectedType: expectedType),
            IdentifierExpression id => AnalyzeIdentifierExpression(id: id),
            CompoundAssignmentExpression compound => AnalyzeCompoundAssignment(compound: compound),
            BinaryExpression binary => AnalyzeBinaryExpression(binary: binary),
            UnaryExpression unary => AnalyzeUnaryExpression(unary: unary),
            CallExpression call => AnalyzeCallExpression(call: call, expectedType: expectedType),
            MemberExpression member => AnalyzeMemberExpression(member: member),
            SpliceMemberExpression spliceMember => AnalyzeSpliceMemberExpression(
                spliceMember: spliceMember),
            SpliceExpression splice => AnalyzeSpliceExpression(splice: splice),
            OptionalMemberExpression optMember => AnalyzeOptionalMemberExpression(
                optMember: optMember),
            IndexExpression index => AnalyzeIndexExpression(index: index),
            ConditionalExpression cond => AnalyzeConditionalExpression(cond: cond),
            LambdaExpression lambda => AnalyzeLambdaExpression(lambda: lambda,
                expectedType: expectedType),
            RangeExpression range => AnalyzeRangeExpression(range: range,
                expectedType: expectedType),
            CreatorExpression creator => AnalyzeCreatorExpression(creator: creator),
            ListLiteralExpression list => AnalyzeListLiteralExpression(list: list,
                expectedType: expectedType),
            SetLiteralExpression set => AnalyzeSetLiteralExpression(set: set,
                expectedType: expectedType),
            DictLiteralExpression dict => AnalyzeDictLiteralExpression(dict: dict,
                expectedType: expectedType),
            TupleLiteralExpression tuple => AnalyzeTupleLiteralExpression(tuple: tuple,
                expectedType: expectedType),
            TypeConversionExpression conv => AnalyzeTypeConversionExpression(conv: conv),
            ChainedComparisonExpression chain => AnalyzeChainedComparisonExpression(chain: chain),
            BlockExpression block => AnalyzeBlockExpression(block: block),
            WithExpression with => AnalyzeWithExpression(with: with),
            NamedArgumentExpression named => AnalyzeExpression(expression: named.Value,
                expectedType: expectedType),
            DictEntryLiteralExpression dictEntry => AnalyzeDictEntryLiteralExpression(
                dictEntry: dictEntry,
                expectedType: expectedType),
            GenericMemberRoutineCallExpression generic =>
                AnalyzeGenericMemberRoutineCallExpression(generic: generic),
            GenericMemberExpression genericMember => AnalyzeGenericMemberExpression(
                genericMember: genericMember),
            IsPatternExpression isPat => AnalyzeIsPatternExpression(isPat: isPat),
            FlagsTestExpression flagsTest => AnalyzeFlagsTestExpression(flagsTest: flagsTest),
            StealExpression steal => AnalyzeStealExpression(steal: steal),
            BackIndexExpression back => AnalyzeBackIndexExpression(back: back),
            TypeExpression typeExpr => ResolveType(typeExpr: typeExpr),
            WhenExpression whenExpr => AnalyzeWhenExpression(when: whenExpr),
            InsertedTextExpression insertedText => AnalyzeInsertedTextExpression(
                insertedText: insertedText),
            _ => HandleUnknownExpression(expression: expression)
        };

        // Compiler-generated bodies are re-analyzed in a synthetic scope where some calls
        // cannot be re-resolved (generic-def owners, memberRoutine-generic locals, wired routines,
        // type names outside their import snapshot). Their synthesizer/cloner annotations are
        // correct by construction — never make an annotation WORSE there: don't replace a good
        // annotation with <error>, and don't replace a concrete type with one that still
        // contains generic parameters (e.g. `sub[S8](...)` annotated S8 by real analysis must
        // not regress to T when the variant-body re-analysis fails to resolve `S8`).
        // Downgrading cascades: a degraded receiver kills resolution of every enclosing call,
        // and codegen then rejects the whole body ("Synthesized body codegen failed").
        if (_isInCompilerGeneratedBody &&
            expression.ResolvedType is { } existingAnnotation and not ErrorTypeSymbol &&
            (resultType is ErrorTypeSymbol || ContainsUnresolvedGenericParameter(type: resultType) &&
                !ContainsUnresolvedGenericParameter(type: existingAnnotation)))
        {
            return existingAnnotation;
        }

        // Set the resolved type directly (no conversion needed)
        expression.ResolvedType = resultType;
        return resultType;
    }

    /// <summary>
    /// True when <paramref name="type"/> is, or structurally contains, an unresolved
    /// generic type parameter (a less-concrete annotation than any fully resolved type).
    /// </summary>
    private static bool ContainsUnresolvedGenericParameter(TypeSymbol type)
    {
        if (type is GenericParameterTypeSymbol)
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } args)
        {
            return args.Any(predicate: arg => ContainsUnresolvedGenericParameter(type: arg));
        }

        return false;
    }

    private TypeSymbol AnalyzeIdentifierExpression(IdentifierExpression id)
    {
        if (TryResolveSpecialIdentifier(id: id, result: out TypeSymbol? special))
        {
            return special!;
        }

        if (TryResolveFlagsContextIdentifier(id: id, result: out TypeSymbol? flagsResult))
        {
            return flagsResult!;
        }

        VariableInfo? varInfo = LookupVariableWithModulePrefix(name: id.Name);
        if (varInfo != null)
        {
            return ResolveVariableReference(id: id, varInfo: varInfo);
        }

        (ChoiceTypeSymbol ChoiceType, ChoiceCaseInfo CaseInfo)? choiceCase =
            _registry.LookupChoiceCase(caseName: id.Name);
        if (choiceCase.HasValue)
        {
            return choiceCase.Value.ChoiceType;
        }

        // An in-scope generic PARAMETER shadows a same-named global type, BEFORE the global lookup.
        // A parameter's NAME is only a label; its identity is its positional slot. Inside a
        // `common routine T.to_width()` body the receiver `T` is UNAMBIGUOUSLY that parameter — there
        // is no other reading — so a user `record T` must NOT hijack it as the type-level receiver of
        // `T.to_width(...)`. Without this the receiver resolved to the record, the call bound to
        // `record-T.to_width`, and GMP emitted it into a 256-bit numeric routine (garbage
        // `zext i64 to record-T` / `shl i256 <record-T>`). Mirrors TypeResolver.ResolveTypeCore; the
        // shadow is granted only for a GENUINE definition-scope parameter (generic-parameter
        // identity = SLOT, not name).
        if (IsGenericParameter(name: id.Name) && (LookupTypeWithImports(name: id.Name) is null ||
                                                  IsGenericDefinitionScopeParam(name: id.Name)))
        {
            return new GenericParameterTypeSymbol(name: id.Name,
                slot: GenericParameterSlot(name: id.Name));
        }

        // Types take precedence over routines when both share a bare name — bare type references for
        // static access (member access, type-as-value generic args, etc.) are the common case.
        TypeSymbol? type = LookupTypeWithImports(name: id.Name);
        if (type != null)
        {
            return type;
        }

        RoutineInfo? routine = LookupRoutineWithModulePrefix(name: id.Name);
        if (routine != null)
        {
            // First-class routine VALUE reference (bare `foo` used as a value, not called): record the
            // routine SA just resolved on the node so codegen consumes it directly (EmitIdentifier's
            // ResolvedRoutine fast-path) instead of re-doing name-based lookup at emission time.
            id.ResolvedRoutine = routine;
            return GetRoutineType(routine: routine);
        }

        // Try to look up as generic type parameter (e.g., T in "T.data_size()")
        if (IsGenericParameter(name: id.Name))
        {
            return new GenericParameterTypeSymbol(name: id.Name);
        }

        // A `secret` type of this name exists but lives in another module (module-private): resolution
        // above deliberately hid it. Say so explicitly — "Unknown identifier" reads like a typo and
        // hides the real reason (the type is intentionally not exported).
        if (TryReportSecretTypeAccess(name: id.Name, location: id.Location))
        {
            return ErrorTypeSymbol.Instance;
        }

        ReportError(code: SemanticDiagnosticCode.UnknownIdentifier,
            message:
            $"Unknown identifier '{id.Name}'.{DidYouMean(target: id.Name, candidates: IdentifierSuggestionCandidates())}",
            location: id.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Handles <c>me</c> and <c>None</c> — the special built-in identifiers. Returns true when
    /// the identifier was one of these and sets <paramref name="result"/>; false means the caller
    /// should continue with normal lookup.
    /// </summary>
    private bool TryResolveSpecialIdentifier(IdentifierExpression id, out TypeSymbol? result)
    {
        switch (id.Name)
        {
            case "me" when _currentType != null:
                result = _currentType;
                return true;
            case "me" when _currentRoutine?.OwnerType == null:
                ReportError(code: SemanticDiagnosticCode.MeOutsideTypeMemberRoutine,
                    message: "'me' can only be used inside a type member routine.",
                    location: id.Location);
                result = ErrorTypeSymbol.Instance;
                return true;
            case "me":
                // For extension member routines (routine Type.MemberRoutine)
                if (ResolveMeIdentifier() is { } meType)
                {
                    result = meType;
                    return true;
                }

                result = null;
                return false;
            case "None":
                // None represents Maybe.None - return a generic Maybe type
                result = _registry.LookupType(name: "Maybe") ?? ErrorTypeSymbol.Instance;
                return true;
            default:
                result = null;
                return false;
        }
    }

    /// <summary>
    /// Resolves a bare identifier against the active flags-context stack. When a flags type is
    /// the current context (e.g. inside an <c>is FLAG</c> test), a bare name like <c>READ</c>
    /// resolves to that type's flag member and stamps <see cref="IdentifierExpression.ResolvedFlagsBit"/>.
    /// Returns true and sets <paramref name="result"/> when matched; false otherwise.
    /// </summary>
    private bool TryResolveFlagsContextIdentifier(IdentifierExpression id, out TypeSymbol? result)
    {
        result = null;
        if (_flagsContextStack.Count == 0 || id.Name.Length == 0 || id.Name.Contains(value: '.'))
        {
            return false;
        }

        TypeSymbol flagsCtx = _flagsContextStack.Peek();
        if (flagsCtx is not FlagsTypeSymbol flagsTypeCtx)
        {
            return false;
        }

        FlagsMemberInfo? memberInfo =
            flagsTypeCtx.Members.FirstOrDefault(predicate: m => m.Name == id.Name);
        if (memberInfo == null)
        {
            return false;
        }

        id.ResolvedFlagsBit = memberInfo.BitPosition;
        result = flagsTypeCtx;
        return true;
    }

    /// <summary>
    /// Looks up a variable by bare name, then by module-qualified name when the bare lookup fails
    /// (for presets declared as <c>MyModule.MY_CONST</c> and referenced bare inside the same module).
    /// </summary>
    private VariableInfo? LookupVariableWithModulePrefix(string name)
    {
        VariableInfo? varInfo = _registry.LookupVariable(name: name);
        if (varInfo == null && _currentModuleName != null && !name.Contains(value: '.'))
        {
            varInfo = _registry.LookupVariable(name: $"{_currentModuleName}.{name}");
        }

        return varInfo;
    }

    /// <summary>
    /// Looks up a routine by bare name, then by module-qualified name. Falls back to the generic-
    /// overload table because generic free routines are not indexed under a plain name key.
    /// </summary>
    private RoutineInfo? LookupRoutineWithModulePrefix(string name)
    {
        // Identifier names are bare — the failable `!` is a structured flag, never in the name.
        RoutineInfo? routine = _registry.LookupRoutine(fullName: name);
        if (routine == null && _currentModuleName != null && !name.Contains(value: '.'))
        {
            routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{name}");
        }

        // Generic free routines are indexed only in the generic-overload table, not under a plain
        // name key, so LookupRoutine misses them. A bare reference — e.g. the receiver identifier of
        // an explicit `gen_id[T](...)` call — must consult it too.
        routine ??= _registry.LookupGenericOverload(name: name);
        return routine;
    }

    /// <summary>
    /// Resolves the type of the <c>me</c> receiver inside an extension/member routine: the specialized
    /// receiver (MeType), a generic-parameter owner, or a fresh module-qualified lookup of the owner type.
    /// Returns null when the owner type cannot be re-looked-up (caller falls through).
    /// </summary>
    private TypeSymbol? ResolveMeIdentifier()
    {
        // Specialized-receiver member (e.g. `routine List[Agent[V]].gather!()`): `me` is the
        // resolved specialized receiver so member access like `me[i]` yields Agent[V], not the
        // generic def's raw element. (OwnerType stays the generic def for registration.)
        if (_currentRoutine!.MeType != null)
        {
            return _currentRoutine.MeType;
        }

        // Generic type parameter owners (e.g., T in "routine T.view()") —
        // return the GenericParameterTypeSymbol directly, no registry lookup needed
        if (_currentRoutine.OwnerType is GenericParameterTypeSymbol)
        {
            return _currentRoutine.OwnerType;
        }

        // Re-lookup to get the updated type with resolved protocols/member variables.
        // Use the module-qualified FullName, not the bare Name: two modules can each declare
        // a `Point`, and a bare `LookupType("Point")` collapses to a first-wins short-name
        // match — binding `me` to the WRONG module's type (cross-module contamination).
        return _registry.LookupType(name: _currentRoutine.OwnerType!.FullName) ??
               _registry.LookupType(name: _currentRoutine.OwnerType.Name);
    }

    /// <summary>
    /// Resolves an identifier that bound to a variable: stamps the module-global flag, records the
    /// binding for the language server, reports use-after-steal, and applies flow-narrowing. Returns the
    /// (possibly narrowed) variable type.
    /// </summary>
    private TypeSymbol ResolveVariableReference(IdentifierExpression id, VariableInfo varInfo)
    {
        // Suflae `global`: stamp the reference so GlobalEntityRewritePass can retarget it to the
        // hidden __ModuleGlobals singleton field (thread-safe storage). LookupVariable checked local
        // scopes first, so a local shadowing a global returns the local (IsGlobal=false) and is NOT
        // stamped — the flag is shadowing-exact.
        id.IsModuleGlobal = varInfo.IsGlobal;

        // Record the exact binding for the language server (scope-precise references / rename /
        // go-to-definition). Reference identity distinguishes shadowed same-name bindings.
        id.ResolvedVariable = varInfo;

        // #11: Deadref tracking — report error if steal invalidated variable. Stamp the per-occurrence
        // dead state first (for the language server's grey-out) regardless of whether we error.
        id.IsDeadUse = _deadrefVariables.Contains(item: id.Name);
        if (id.IsDeadUse)
        {
            ReportError(code: SemanticDiagnosticCode.UseAfterSteal,
                message:
                $"Variable '{id.Name}' is a deadref — it was invalidated by a previous 'steal' or ownership transfer. " +
                "The variable can no longer be used.",
                location: id.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Check for type narrowing (e.g., after "unless x is None", or `if x is A` on a variant).
        TypeSymbol? narrowed = _registry.GetNarrowedType(name: id.Name);
        if (narrowed != null && narrowed.Name != varInfo.Type.Name &&
            (IsCarrierType(type: varInfo.Type) || varInfo.Type is VariantTypeSymbol))
        {
            // Flow-narrowed to a single arm/payload of a carrier or variant — mark this read so a
            // postprocessing pass rewrites it into a payload extraction from the underlying value.
            id.NarrowedFrom = varInfo.Type;
        }

        return narrowed ?? varInfo.Type;
    }

    /// <summary>
    /// If a <c>secret</c> (module-private) type whose bare name matches <paramref name="name"/> exists
    /// in a module OTHER than the current one, resolution deliberately hid it — report a dedicated
    /// RF-S402 explaining that (instead of letting the caller emit a misleading "unknown type/identifier"
    /// that reads like a typo), and return true. Returns false when no such type exists.
    /// </summary>
    internal bool TryReportSecretTypeAccess(string name, SourceLocation location)
    {
        foreach (TypeSymbol t in _registry.GetAllTypes())
        {
            if (t is { Visibility: VisibilityModifier.Secret } && t.Name == name &&
                t.Module != _currentModuleName)
            {
                ReportError(code: SemanticDiagnosticCode.SecretTypeAccess,
                    message:
                    $"'{name}' is a secret (module-private) type of module '{t.Module}' " +
                    $"and cannot be used from module '{_currentModuleName ?? "?"}'.",
                    location: location);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Analyzes binary expressions that remain as BinaryExpression nodes after parsing.
    /// Note: Most arithmetic, comparison, and bitwise operators are desugared to memberRoutine calls
    /// in the parser (e.g., a + b -> a.add(b)). This memberRoutine only handles operators that
    /// are NOT desugared:
    /// - Assignment (=)
    /// - Logical operators (and, or) — require short-circuit evaluation
    /// - Membership/type operators (in, notin, is, isnot, obeys, disobeys)
    /// - None coalescing (??) — requires short-circuit evaluation
    /// </summary>
    /// <summary>
    /// A type carries a comparable object identity iff it is an <c>entity</c> or one of the forwarding
    /// wrappers (Viewing/Modifying/Consulting/Amending/Retained/Tracked/Guarded/Witnessed/Roamed). Hijacked
    /// is deliberately excluded (its <c>==</c> is already identity). Value types have no identity.
    /// </summary>
    private static bool IsIdentityComparable(TypeSymbol type)
    {
        return type is EntityTypeSymbol ||
               Declaration.RuntimeContract.ForwardingWrapperTypes.Contains(item: type.BareName);
    }

    /// <summary>Reports RF-S440 if an <c>===</c>/<c>!==</c> operand is a value type, not a reference.</summary>
    private void ValidateIdentityOperand(TypeSymbol type, BinaryOperator op,
        SourceLocation location)
    {
        if (type is ErrorTypeSymbol || IsIdentityComparable(type: type))
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.IdentityOperandNotReference,
            message:
            $"Operator '{op.ToStringRepresentation()}' compares object IDENTITY, which needs a " +
            $"reference operand (an entity or a borrow/handle wrapper) — but '{type.Name}' is a " +
            "value type. Use '==' for value equality.",
            location: location);
    }

    private TypeSymbol AnalyzeBinaryExpression(BinaryExpression binary)
    {
        // Buildtime `expand` gate: a comparison/equality on a buildtime member value (me.$nameof(m)) is a
        // gated wired op (eq/cmp) — it needs the enclosing template's `needs P everywhere` guarantee.
        EnforceBinaryBuildtimeMemberGate(binary: binary);

        // Re-binding (lhs = rhs) revives a stolen-from identifier: clear deadref
        // BEFORE analyzing the LHS so the deadref-read check doesn't fire.
        if (binary is { Operator: BinaryOperator.Assign, Left: IdentifierExpression rebindId })
        {
            _deadrefVariables.Remove(item: rebindId.Name);
        }

        // Logical negation should eventually lower through member routines rather than a not operator.
        TypeSymbol leftType = AnalyzeExpression(expression: binary.Left);
        // Pass leftType as expected for assignments so RHS literals like `none`
        // see the target's carrier-slot type as their contextual expected type.
        TypeSymbol rightType = binary.Operator == BinaryOperator.Assign
            ? AnalyzeExpression(expression: binary.Right, expectedType: leftType)
            : AnalyzeExpression(expression: binary.Right);

        (leftType, rightType) = ReinferBinaryLiteralOperands(binary: binary,
            leftType: leftType,
            rightType: rightType);

        if (binary.Operator == BinaryOperator.Assign)
        {
            return AnalyzeAssignmentExpression(target: binary.Left,
                value: binary.Right,
                targetType: leftType,
                valueType: rightType,
                location: binary.Location);
        }

        if (TryAnalyzeFlagsOperator(binary: binary,
                leftType: leftType,
                rightType: rightType,
                result: out TypeSymbol? flagsResult))
        {
            return flagsResult!;
        }

        // Check for operator prohibitions on choice and flags types, and operator-protocol conformance.
        string? operatorMemberRoutine = binary.Operator.GetMemberRoutineName();
        if (operatorMemberRoutine != null && TryReportOperatorTypeViolation(binary: binary,
                leftType: leftType,
                rightType: rightType,
                operatorMemberRoutine: operatorMemberRoutine))
        {
            return ErrorTypeSymbol.Instance;
        }

        // #117: Fixed-width numeric types must match exactly (S32 + S64 = error).
        // System types (Address) are exempt. Shift operators are also exempt because
        // they intentionally use U32 for the shift amount regardless of the left-operand type.
        if (leftType.Name != rightType.Name && IsFixedWidthNumericType(type: leftType) &&
            IsFixedWidthNumericType(type: rightType) && !IsLogicalOperator(op: binary.Operator) &&
            !IsComparisonOperator(op: binary.Operator) && !IsShiftOperator(op: binary.Operator))
        {
            ReportError(code: SemanticDiagnosticCode.FixedWidthTypeMismatch,
                message:
                $"Fixed-width type mismatch: '{leftType.Name}' and '{rightType.Name}'. Explicit conversion required.",
                location: binary.Location);
            return ErrorTypeSymbol.Instance;
        }

        // S854: Unchecked operators require a danger block or @dangerous routine.
        if (binary.Operator is BinaryOperator.AddUnchecked or BinaryOperator.SubtractUnchecked
                or BinaryOperator.MultiplyUnchecked or BinaryOperator.TrueDivideUnchecked
                or BinaryOperator.FloorDivideUnchecked or BinaryOperator.ModuloUnchecked
                or BinaryOperator.PowerUnchecked && !InDangerBlock)
        {
            ReportError(code: SemanticDiagnosticCode.UncheckedOperatorOutsideDanger,
                message: $"Unchecked operator '{binary.Operator.ToStringRepresentation()}' " +
                         "requires a 'danger' block or '@dangerous' routine.",
                location: binary.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Flags combination: A and B -> bitwise OR (combines flags)
        if (binary.Operator == BinaryOperator.And && leftType is FlagsTypeSymbol &&
            leftType.Name == rightType.Name)
        {
            return leftType;
        }

        return AnalyzeBinaryExpressionByKind(binary: binary,
            leftType: leftType,
            rightType: rightType);
    }

    /// <summary>
    /// Handles the flags-specific operators <c>but</c> and <c>or</c> on flags types.
    /// Returns true when a result (including error result) was produced; false means
    /// the caller should continue with generic operator analysis.
    /// </summary>
    private bool TryAnalyzeFlagsOperator(BinaryExpression binary, TypeSymbol leftType,
        TypeSymbol rightType, out TypeSymbol? result)
    {
        result = null;
        switch (binary.Operator)
        {
            case BinaryOperator.But when leftType is not FlagsTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires a flags type on the left side, but got '{leftType.Name}'.",
                    location: binary.Location);
                result = ErrorTypeSymbol.Instance;
                return true;
            case BinaryOperator.But when rightType is not FlagsTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires a flags type on the right side, but got '{rightType.Name}'.",
                    location: binary.Location);
                result = ErrorTypeSymbol.Instance;
                return true;
            case BinaryOperator.But when leftType.Name != rightType.Name:
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"'but' operator requires both operands to be the same flags type, but got '{leftType.Name}' and '{rightType.Name}'.",
                    location: binary.Location);
                result = ErrorTypeSymbol.Instance;
                return true;
            case BinaryOperator.But:
                result = leftType;
                return true;
            // #128: 'or' cannot be used to combine flags outside is/isnot tests
            case BinaryOperator.Or when leftType is FlagsTypeSymbol || rightType is FlagsTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.FlagsOrInAssignment,
                    message:
                    "Cannot use 'or' to combine flags values. Use 'is FLAG_A or FLAG_B' for testing, " +
                    "or separate flag assignments.",
                    location: binary.Location);
                result = leftType;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Handles logical, identity, comparison, none-coalescing, and overloadable member-routine
    /// operators after the early exits (assignment, flags) in <see cref="AnalyzeBinaryExpression"/>.
    /// </summary>
    private TypeSymbol AnalyzeBinaryExpressionByKind(BinaryExpression binary, TypeSymbol leftType,
        TypeSymbol rightType)
    {
        // Logical operators (and, or) — require bool operands, return bool.
        // Not desugared because they need short-circuit evaluation.
        if (IsLogicalOperator(op: binary.Operator))
        {
            if (!IsBoolType(type: leftType) || !IsBoolType(type: rightType))
            {
                ReportError(code: SemanticDiagnosticCode.LogicalOperatorRequiresBool,
                    message:
                    $"Logical operator '{binary.Operator.ToStringRepresentation()}' requires boolean operands.",
                    location: binary.Location);
            }

            return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        // Reference-identity operators (===, !==): NOT overloadable, NOT lowered to `.eq()` — a
        // primitive pointer compare in codegen. Both operands must be reference-carrying (entity or a
        // forwarding wrapper); a value type has no identity. Result is always Bool.
        if (binary.Operator is BinaryOperator.IdentityEqual or BinaryOperator.IdentityNotEqual)
        {
            ValidateIdentityOperand(type: leftType,
                op: binary.Operator,
                location: binary.Left.Location);
            ValidateIdentityOperand(type: rightType,
                op: binary.Operator,
                location: binary.Right.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        // Comparison operators — all return Bool.
        // Includes overloadable (==, !=, <, <=, >, >=, in, notin) and non-overloadable (is, isnot, obeys, disobeys).
        if (IsComparisonOperator(op: binary.Operator))
        {
            ValidateComparisonOperands(left: leftType,
                right: rightType,
                op: binary.Operator,
                location: binary.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        // None coalescing operator (??) — not desugared because it needs short-circuit evaluation.
        if (binary.Operator == BinaryOperator.NoneCoalesce)
        {
            return AnalyzeNoneCoalesce(binary: binary, leftType: leftType, rightType: rightType);
        }

        // Overloadable operator: validate via the member routine's parameter type.
        return AnalyzeOverloadableOperator(binary: binary,
            leftType: leftType,
            rightType: rightType);
    }

    /// <summary>
    /// Resolves the result type of the <c>??</c> none-coalescing operator. Built-in carrier types
    /// (Maybe/Result) unwrap directly; user types are dispatched through <c>unwrap_or</c>.
    /// </summary>
    private TypeSymbol AnalyzeNoneCoalesce(BinaryExpression binary, TypeSymbol leftType,
        TypeSymbol rightType)
    {
        if (IsCarrierType(type: leftType) && leftType.TypeArguments is { Count: > 0 })
        {
            return leftType.TypeArguments[index: 0];
        }

        RoutineInfo? unwrapOrMemberRoutine =
            _registry.LookupMemberRoutine(type: leftType, memberRoutineName: "unwrap_or");
        if (unwrapOrMemberRoutine != null)
        {
            return unwrapOrMemberRoutine.ReturnType ?? rightType;
        }

        ReportError(code: SemanticDiagnosticCode.TypeDoesNotSupportOperator,
            message: $"Type '{leftType.Name}' does not support the '??' operator. " +
                     "Implement 'unwrap_or(default: T) -> T' to enable none coalescing.",
            location: binary.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Validates an overloadable binary operator against the member routine it lowers to:
    /// propagates failable-call metadata for checked integer ops, checks the RHS conforms
    /// to the routine's parameter type, and returns the routine's declared return type.
    /// </summary>
    private TypeSymbol AnalyzeOverloadableOperator(BinaryExpression binary, TypeSymbol leftType,
        TypeSymbol rightType)
    {
        string? memberRoutineName = binary.Operator.GetMemberRoutineName();
        if (memberRoutineName == null)
        {
            return leftType;
        }

        RoutineInfo? memberRoutine =
            _registry.LookupMemberRoutineOverload(type: leftType,
                memberRoutineName: memberRoutineName,
                argTypes: [rightType]) ?? _registry.LookupMemberRoutine(type: leftType,
                memberRoutineName: memberRoutineName);

        // Apply failable-call checking for integer arithmetic operators.
        // Floats (F16/F32/F64/F128) and software decimals (D32/D64/D128) are excluded
        // because the codegen emits raw float instructions (fadd/fmul/...) for them,
        // bypassing the checked dispatch path.
        bool isIntegerCheckedOp = memberRoutine is { IsFailable: true } &&
                                  leftType is RecordTypeSymbol
                                  {
                                      BackendType: not null, LlvmType: { } ltIr
                                  } && ltIr.StartsWith(value: 'i') && ltIr != "i1";
        if (isIntegerCheckedOp && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            if (!_currentRoutine.IsFailable && _currentRoutine.Name != "start" &&
                !_currentRoutine.IsSynthesized)
            {
                string opStr = binary.Operator.ToStringRepresentation();
                ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                    message: $"Operator '{opStr}' may throw. Either make the enclosing routine " +
                             "failable (!), use 'when' to handle the error, or use the wrapping " +
                             $"variant '{opStr}%' for silent overflow.",
                    location: binary.Location);
            }
        }

        if (memberRoutine is not { Parameters.Count: > 0 })
        {
            return leftType;
        }

        TypeSymbol paramType = memberRoutine.Parameters[index: 0].Type;

        // Substitute Me -> leftType for protocol-sourced member routines.
        if (paramType is ProtocolSelfTypeSymbol)
        {
            paramType = leftType;
        }

        // Contextually infer unsuffixed integer literals against the operator
        // parameter type so stdlib/operator lowering does not inherit a stale S64.
        if (binary.Right is LiteralExpression
            {
                LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal
            } && IsFixedWidthIntegerType(type: paramType))
        {
            rightType = AnalyzeExpression(expression: binary.Right, expectedType: paramType);
        }

        bool allowIntegralShiftAmount = IsShiftOperator(op: binary.Operator) &&
                                        IsIntegerType(type: rightType) &&
                                        IsIntegerType(type: paramType);

        if (!allowIntegralShiftAmount && !IsAssignableTo(source: rightType, target: paramType))
        {
            ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                message:
                $"Operator '{binary.Operator.ToStringRepresentation()}': cannot convert '{rightType.Name}' to '{paramType.Name}'.",
                location: binary.Location);
            return ErrorTypeSymbol.Instance;
        }

        return ResolveOperatorReturnType(routine: memberRoutine, leftType: leftType);
    }

    private static TypeSymbol ResolveOperatorReturnType(RoutineInfo routine, TypeSymbol leftType)
    {
        return routine.ReturnType is null or ProtocolSelfTypeSymbol
            ? leftType
            : routine.ReturnType;
    }

    private void EnforceBinaryBuildtimeMemberGate(BinaryExpression binary)
    {
        if (WiredNameForOperator(op: binary.Operator) is { } opWired &&
            (binary.Left is SpliceMemberExpression || binary.Right is SpliceMemberExpression))
        {
            EnforceBuildtimeMemberGate(wiredName: opWired, location: binary.Location);
        }
    }

    /// <summary>
    /// Re-infers unsuffixed integer-literal operands of a binary expression against their typed peer
    /// (so <c>me.strong_count == 0</c> types <c>0</c> to the field width, and <c>20 in list_of_s64</c>
    /// types <c>20</c> to the element type). Returns the (possibly re-inferred) operand types.
    /// </summary>
    private (TypeSymbol leftType, TypeSymbol rightType) ReinferBinaryLiteralOperands(
        BinaryExpression binary, TypeSymbol leftType, TypeSymbol rightType)
    {
        // Re-infer unsuffixed integer literals against the typed peer so
        // comparisons like 'me.strong_count == 0' don't default the literal to S64.
        if (binary.Right is LiteralExpression
            {
                LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal
                or TokenType.UndecidedInteger
            } && IsFixedWidthIntegerType(type: leftType) && leftType.Name != rightType.Name)
        {
            rightType = AnalyzeExpression(expression: binary.Right, expectedType: leftType);
        }
        else if (binary.Left is LiteralExpression
                 {
                     LiteralType: TokenType.IntegerLiteral or TokenType.S64Literal
                     or TokenType.UndecidedInteger
                 } && IsFixedWidthIntegerType(type: rightType) &&
                 leftType.Name != rightType.Name)
        {
            leftType = AnalyzeExpression(expression: binary.Left, expectedType: rightType);
        }

        // Membership (`x in coll` / `x notin coll`) reverses to `coll.contains(x)`: the LEFT operand
        // must conform to the collection's ELEMENT type, not stay at the bare-literal default. Without
        // this a Suflae `20 in list_of_s64` keeps `20` at the `Integer` default → the element type
        // never matches → `contains` is silently always false. (RF only escapes this by luck: its
        // default already IS S64.) Inferring the operand type through the container is the compiler's
        // job — unwrap SF's `Roamed`/RC wrappers to reach the collection, then take its first type
        // argument (List/Set/Array element, Dict key).
        if (binary.Operator is BinaryOperator.In or BinaryOperator.NotIn &&
            binary.Left is LiteralExpression { LiteralType: TokenType.UndecidedInteger })
        {
            TypeSymbol container = UnwrapCollectionLiteralExpectedType(type: rightType);
            if (container.TypeArguments is { Count: >= 1 } contArgs &&
                IsFixedWidthIntegerType(type: contArgs[index: 0]))
            {
                leftType = AnalyzeExpression(expression: binary.Left,
                    expectedType: contArgs[index: 0]);
            }
        }

        return (leftType, rightType);
    }

    /// <summary>
    /// Reports operator misuse on a choice/flags type, or an operator applied to a Record/Entity type
    /// that does not structurally obey the operator's required protocol. Returns true when a violation
    /// was reported (the caller returns <see cref="ErrorTypeSymbol"/>); false when the operator is allowed.
    /// </summary>
    private bool TryReportOperatorTypeViolation(BinaryExpression binary, TypeSymbol leftType,
        TypeSymbol rightType, string operatorMemberRoutine)
    {
        // Check for operator prohibitions on choice and flags types
        // Choices do not support ANY overloadable operators — use 'is' for case matching
        // Flags do not support arithmetic/comparison/bitwise operators — use 'is'/'isnot'/'but'
        switch (leftType)
        {
            case ChoiceTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                    message:
                    $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used with choice type '{leftType.Name}'. Use 'is' for case matching.",
                    location: binary.Location);
                return true;
            case FlagsTypeSymbol
                when binary.Operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual):
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                    message:
                    $"Operator '{binary.Operator.ToStringRepresentation()}' cannot be used " +
                    $"with flags type '{leftType.Name}'. Use 'is'/'isnot'/'but' for " + $"" +
                    $"flag" + $" operations.",
                    location: binary.Location);
                return true;
        }

        // An overloadable operator binds ONLY to a type that satisfies the operator's protocol —
        // it must not bind to a memberRoutine merely NAMED the same on a non-conforming type (e.g.
        // `Set.add(value:)->Bool` inserts an element; its signature does not match
        // `Addable.add(other:Self)->Self`, so `set + x` must be an error, not a silent insert).
        // Conformance here is STRUCTURAL (ImplementsProtocol checks the required memberRoutine signatures),
        // so a type need not spell `obeys` just to use `==`/`in`/`<` — but a coincidental name with
        // the wrong signature is correctly rejected. Only Record/Entity types are checked (that is
        // where ImplementsProtocol resolves structurally); tuples/numerically-intrinsic and generic
        // parameters (conformance via `needs` constraints, checked at instantiation) are deferred.
        // Membership operators (`in`/`notin`) reverse to `rhs.contains(lhs)`, so the RIGHT operand is
        // the receiver.
        bool operatorIsReversed = binary.Operator is BinaryOperator.In or BinaryOperator.NotIn;
        TypeSymbol operatorReceiverType = operatorIsReversed
            ? rightType
            : leftType;
        if (!_isReducedStdlibValidation &&
            operatorReceiverType is RecordTypeSymbol or EntityTypeSymbol &&
            GetRequiredProtocols(wiredName: operatorMemberRoutine) is
                { Count: > 0 } requiredProtocols && !requiredProtocols.Any(predicate: p =>
                ImplementsProtocol(type: operatorReceiverType, protocolName: p)))
        {
            string protoText = requiredProtocols.Count == 1
                ? $"'{requiredProtocols[index: 0]}'"
                : string.Join(separator: " or ",
                    values: requiredProtocols.Select(selector: p => $"'{p}'"));
            ReportError(code: SemanticDiagnosticCode.BinaryOperatorNotFound,
                message:
                $"Operator '{binary.Operator.ToStringRepresentation()}' is not defined for " +
                $"'{operatorReceiverType.Name}': the type must obey {protoText}.",
                location: binary.Location);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Analyzes an assignment expression (target = value).
    /// Validates mutability, member variable access, and type compatibility.
    /// </summary>
    /// <param name="target">The assignment target expression.</param>
    /// <param name="value">The value being assigned.</param>
    /// <param name="targetType">The resolved type of the target.</param>
    /// <param name="valueType">The resolved type of the value.</param>
    /// <param name="location">Source location for error reporting.</param>
    /// <returns>The type of the assignment expression (same as target type).</returns>
    private TypeSymbol AnalyzeAssignmentExpression(Expression target, Expression value,
        TypeSymbol targetType, TypeSymbol valueType, SourceLocation location)
    {
        // #173: Tuple assignment destructuring — (a, b) = (b, a)
        if (target is TupleLiteralExpression tupleLhs)
        {
            return AnalyzeTupleDestructuringAssignment(tupleLhs: tupleLhs,
                targetType: targetType,
                valueType: valueType,
                location: location);
        }

        // Check if target is assignable (variable, member variable, or index)
        if (!IsAssignableTarget(target: target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message:
                "Invalid assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be assigned to.",
                location: target.Location);
            return targetType;
        }

        switch (target)
        {
            case IdentifierExpression id:
                ValidateIdentifierAssignmentTarget(id: id, value: value, location: location);
                break;
            case MemberExpression member:
                ValidateMemberAssignmentTarget(member: member, value: value, location: location);
                break;
            case IndexExpression index:
                ValidateIndexAssignmentTarget(index: index, location: location);
                break;
        }

        ValidateAssignmentValueConstraints(target: target,
            value: value,
            targetType: targetType,
            valueType: valueType,
            location: location);

        return targetType;
    }

    /// <summary>
    /// Handles tuple destructuring assignment <c>(a, b) = rhs</c>: validates that every LHS element
    /// is an assignable target, checks preset immutability for identifier elements, and verifies
    /// that the RHS arity matches the LHS when the RHS is a known tuple type.
    /// </summary>
    private TypeSymbol AnalyzeTupleDestructuringAssignment(TupleLiteralExpression tupleLhs,
        TypeSymbol targetType, TypeSymbol valueType, SourceLocation location)
    {
        foreach (Expression element in tupleLhs.Elements)
        {
            if (!IsAssignableTarget(target: element))
            {
                ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                    message:
                    "All elements of tuple destructuring must be assignable targets (variables, member accesses, or indices).",
                    location: element.Location);
            }

            if (element is IdentifierExpression elemId)
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: elemId.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: $"Cannot assign to preset variable '{elemId.Name}'.",
                        location: location);
                }
            }
        }

        if (valueType is TupleTypeSymbol tupleType &&
            tupleLhs.Elements.Count != tupleType.ElementTypes.Count)
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Tuple destructuring has {tupleLhs.Elements.Count} targets but the value has {tupleType.ElementTypes.Count} elements.",
                location: location);
        }

        return targetType;
    }

    /// <summary>
    /// Validates an identifier (variable) as an assignment target: checks preset immutability and,
    /// in Suflae, updates the variable's nullability flow state based on the RHS.
    /// </summary>
    private void ValidateIdentifierAssignmentTarget(IdentifierExpression id, Expression value,
        SourceLocation location)
    {
        VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
        if (varInfo is { IsModifiable: false })
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                message: $"Cannot assign to preset variable '{id.Name}'.",
                location: location);
        }

        // Suflae flow typing: reassigning an entity reference re-derives its nullability.
        if (_registry.Language != Language.Suflae || varInfo == null ||
            !IsEntityRefType(type: varInfo.Type))
        {
            return;
        }

        bool valueNullable = IsNullableEntityRead(expr: value);
        if (varInfo.IsNullable)
        {
            // A nullable local: a possibly-none RHS re-nullifies it (shadowing any prior
            // null-check); a non-null RHS proves it non-none for the rest of this flow.
            if (valueNullable)
            {
                _registry.MarkVariableNullableAgain(name: id.Name);
            }
            else
            {
                _registry.MarkVariableNonNull(name: id.Name);
            }
        }
        else if (valueNullable)
        {
            // A non-null local cannot take a possibly-none value.
            ReportNullableIntoNonNull(target: $"variable '{id.Name}'",
                value: value,
                optionalHint: $"{id.Name}: <Type>?");
        }
    }

    /// <summary>
    /// Validates a member-access expression as an assignment target: checks read-only wrappers,
    /// setter visibility, Suflae non-nullable field nullability, and @readonly routine mutation.
    /// </summary>
    private void ValidateMemberAssignmentTarget(MemberExpression member, Expression value,
        SourceLocation location)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Read-only wrapper types (Viewing, Consulting) cannot be written through.
        if (IsReadOnlyWrapper(type: objectType))
        {
            ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                message:
                $"Cannot write to member '{member.MemberName}' through read-only wrapper '{objectType.Name}'. " +
                "Use Modifying[T] for exclusive write access or Amending[T] for locked write access.",
                location: location);
        }

        ValidateMemberVariableWriteAccess(objectType: objectType,
            memberVariableName: member.MemberName,
            location: location);

        // Suflae: a NON-NULLABLE entity field (`x: E`) rejects `o.x = <possibly-none>` — literal
        // `none` or an unchecked `E?` read. Only an optional field (`x: E?`) may hold a null Roamed
        // handle. Mirrors the construction check; the field's IsNullable is set in TypeBodyResolver.
        if (_registry.Language == Language.Suflae && objectType is EntityTypeSymbol writeEntity &&
            writeEntity.LookupMemberVariable(memberVariableName: member.MemberName) is
            {
                IsNullable: false,
                Type: RecordTypeSymbol { GenericDefinition.Name: Declaration.RuntimeContract.Roamed }
            } writeField && IsNullableEntityRead(expr: value))
        {
            ReportNullableIntoNonNull(target: $"field '{writeField.Name}'",
                value: value,
                optionalHint: $"{writeField.Name}: <Type>?");
        }

        // Check if we're in a @readonly member routine trying to modify 'me'.
        if (_registry.CompilationLanguage != Language.Suflae &&
            _currentRoutine is { IsReadOnly: true } &&
            member.Object is IdentifierExpression { Name: "me" })
        {
            ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMemberRoutine,
                message:
                $"Cannot mutate member variable '{member.MemberName}' in a @readonly member routine. " +
                "Use @reshaping to allow mutations.",
                location: location);
        }
    }

    /// <summary>
    /// Validates an index expression as an assignment target: propagates failable setitem metadata,
    /// checks read-only transparent protocols, and verifies the indexed variable is modifiable.
    /// </summary>
    private void ValidateIndexAssignmentTarget(IndexExpression index, SourceLocation location)
    {
        TypeSymbol indexedObjectType = AnalyzeExpression(expression: index.Object);

        // Failability: lookup setitem on the indexed type and propagate `!` to caller.
        // `arr[i] = v` desugars to `arr.setitem!(i, v)` for failable indexers; a
        // non-failable caller must mark HasFailableCalls so its `!` decl is justified.
        TryGetTransparentProtocolTarget(type: indexedObjectType,
            targetType: out TypeSymbol setLookupType);
        RoutineInfo? setItem =
            _registry.LookupMemberRoutine(type: setLookupType, memberRoutineName: "setitem") ??
            _registry.LookupMemberRoutine(type: setLookupType,
                memberRoutineName: "setitem",
                isFailable: true);
        if (setItem is { IsFailable: true } && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(item: setItem);
        }

        if (IsReadOnlyTransparentProtocol(type: indexedObjectType))
        {
            ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                message:
                $"Cannot write through index access on read-only protocol '{indexedObjectType.Name}'. " +
                "Use Controlling[T] or a writable token instead.",
                location: location);
        }

        // The object being indexed must be modifiable.
        if (index.Object is IdentifierExpression indexedVar)
        {
            VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
            if (varInfo is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                    message: $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// Post-target-validation checks shared by all scalar assignment forms: RF entity bare-assignment
    /// prohibition, implicit non-trivially-assignable wrapper copy detection, type-compatibility, and
    /// <c>??=</c> flow-narrowing.
    /// </summary>
    private void ValidateAssignmentValueConstraints(Expression target, Expression value,
        TypeSymbol targetType, TypeSymbol valueType, SourceLocation location)
    {
        // RazorForge: Entity bare assignment prohibition.
        // `b = a` where `a` is a bare identifier of entity type is a build error.
        if (_registry.Language == Language.RazorForge && value is IdentifierExpression &&
            valueType is EntityTypeSymbol)
        {
            ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                message: $"Cannot directly assign entity of type '{valueType.Name}'. " +
                         "Use '.share()' for shared ownership or 'steal' for ownership transfer.",
                location: location);
        }

        // Phase 1: warn when the RHS is a non-trivially-copyable wrapper reference.
        // See AnalyzeVariableDeclaration for the same rule applied to var initializers.
        if (_registry.Language == Language.RazorForge &&
            value is IdentifierExpression or MemberExpression &&
            !IsTriviallyAssignable(type: valueType))
        {
            (string Wrapper, string Path)? hint =
                FindNonTriviallyAssignableWrapper(type: valueType);
            if (hint != null)
            {
                string verb = NonTriviallyAssignableWrappers[key: hint.Value.Wrapper];
                string fieldNote = hint.Value.Path == "<value>"
                    ? $"value of type '{valueType.Name}' is a '{hint.Value.Wrapper}[…]' wrapper"
                    : $"field '{hint.Value.Path}' of type '{hint.Value.Wrapper}[…]'";
                ReportError(code: SemanticDiagnosticCode.ImplicitWrapperCopy,
                    message:
                    $"Implicit copy in assignment: {fieldNote} requires an explicit copy verb. " +
                    $"Spell out '{verb}' at the copy site, or reconstruct the record with each field's verb.",
                    location: location);
            }
        }

        // Check type compatibility.
        if (!IsAssignableTo(source: valueType, target: targetType))
        {
            ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                message:
                $"Cannot assign value of type '{valueType.Name}' to target of type '{targetType.Name}'.",
                location: location);
        }

        // Variant reassignment is gated by Assignability (variant is Assignable iff every
        // member is). The general AssignmentTypeMismatch/Assignable checks above already
        // enforce this through the structural rule — no variant-specific check needed.

        // #42: ??= narrowing — `a ??= b` is expanded to `a = a ?? b`.
        // When assigning `target = target ?? default` where target is Maybe[T],
        // narrow the variable to T after the coalescing assignment.
        if (target is IdentifierExpression narrowId &&
            value is BinaryExpression { Operator: BinaryOperator.NoneCoalesce } &&
            IsMaybeType(type: targetType) && targetType.TypeArguments is { Count: > 0 })
        {
            _registry.NarrowVariable(name: narrowId.Name,
                narrowedType: targetType.TypeArguments[index: 0]);
        }
    }

    /// <summary>
    /// Analyzes a compound assignment expression (e.g., a += b).
    /// Dispatch order: (0) verify target is var, (1) try in-place wired (iadd) -> None,
    /// (2) fallback to create-and-assign (add), (3) error if neither exists.
    /// </summary>
    private TypeSymbol AnalyzeCompoundAssignment(CompoundAssignmentExpression compound)
    {
        TypeSymbol targetType = AnalyzeExpression(expression: compound.Target);
        // Analyze the RHS too — without this, constructor calls like `n += S64(5)`
        // never get classified as TypeConstructor and reach codegen unlowered (S959).
        //
        // For a same-typed compound op (`+= -= *= /= //= %= **= &= |= ^= ??=`) the RHS
        // must conform to the TARGET type, so a bare literal like `1` in `i += 1` types
        // as the target (e.g. `U64`) rather than the language default — `Integer` in
        // Suflae, `S64` in RazorForge. Without this hint an SF `U64 += 1` leaves the `1`
        // an `Integer`, which mis-lowers (Integer.from_literal against a fixed-width
        // target) into a runaway/garbage result. Shift amounts (`<<= >>= <<<= >>>=`) are
        // U32, not the target type, so they keep their own inference.
        bool isShift = compound.Operator is BinaryOperator.ArithmeticLeftShift
            or BinaryOperator.ArithmeticRightShift or BinaryOperator.LogicalLeftShift
            or BinaryOperator.LogicalRightShift;
        AnalyzeExpression(expression: compound.Value,
            expectedType: isShift
                ? null
                : targetType);

        // Step 0: Verify target is assignable and modifiable
        if (!IsAssignableTarget(target: compound.Target))
        {
            ReportError(code: SemanticDiagnosticCode.InvalidAssignmentTarget,
                message:
                "Invalid compound assignment target. Only variables, member accesses (e.g. obj.field), and indexed expressions (e.g. list[i]) can be the target of `+=`, `-=`, etc.",
                location: compound.Target.Location);
            return targetType;
        }

        ValidateCompoundAssignmentTarget(compound: compound);

        // #67: Cannot use compound assignment on read-only token (Viewing or Consulting)
        if (targetType is WrapperTypeSymbol { IsReadOnly: true } readOnlyWrapper)
        {
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentOnReadOnlyToken,
                message:
                $"Cannot use compound assignment on read-only token '{readOnlyWrapper.Name}'. " +
                "Read-only tokens (Viewing, Consulting) do not allow modifications.",
                location: compound.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Don't try dispatch on error types (prevent cascade)
        if (targetType.Category == TypeCategory.Error)
        {
            return targetType;
        }

        switch (targetType)
        {
            // Choice types cannot use compound assignment — choices do not support operators
            case ChoiceTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                    message:
                    $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with choice type '{targetType.Name}'. " +
                    "Choice types do not support operators. Use 'is' for case matching.",
                    location: compound.Location);
                return ErrorTypeSymbol.Instance;
            // #134: Flags types cannot use arithmetic or compound assignment operators
            case FlagsTypeSymbol:
                ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                    message:
                    $"Operator '{compound.Operator.ToStringRepresentation()}=' cannot be used with flags type '{targetType.Name}'. " +
                    "Use 'but' to remove flags and 'is'/'isnot' to test flags.",
                    location: compound.Location);
                return ErrorTypeSymbol.Instance;
        }

        string? inPlaceMemberRoutine = compound.Operator.GetInPlaceMemberRoutineName();
        string? regularMemberRoutine = compound.Operator.GetMemberRoutineName();

        // Step 1: Try in-place wired (iadd, etc.)
        if (inPlaceMemberRoutine != null)
        {
            RoutineInfo? inPlaceRoutine =
                _registry.LookupRoutine(fullName: $"{targetType.Name}.{inPlaceMemberRoutine}");
            if (inPlaceRoutine != null)
            {
                // In-place memberRoutine found — returns None (modifies in-place)
                return _registry.LookupType(name: "None") ?? ErrorTypeSymbol.Instance;
            }
        }

        // Step 2: Fallback to create-and-assign (a = a.add(b)) — not allowed for entity types
        if (targetType.Category == TypeCategory.Entity)
        {
            string opSymbol = compound.Operator.ToStringRepresentation();
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                message:
                $"Entity type '{targetType.Name}' does not support compound assignment '{opSymbol}='. " +
                $"Define in-place operator '{inPlaceMemberRoutine}' (with @reshaping) to allow compound assignment.",
                location: compound.Location);
            return ErrorTypeSymbol.Instance;
        }

        if (regularMemberRoutine == null)
        {
            string opSymbol = compound.Operator.ToStringRepresentation();
            ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
                message:
                $"Type '{targetType.Name}' does not support compound assignment '{opSymbol}='. " +
                $"Define in-place operator '{inPlaceMemberRoutine}' or regular operator '{regularMemberRoutine}'.",
                location: compound.Location);

            return ErrorTypeSymbol.Instance;
        }

        RoutineInfo? regularRoutine =
            _registry.LookupRoutine(fullName: $"{targetType.Name}.{regularMemberRoutine}");
        if (regularRoutine != null)
        {
            TypeSymbol returnType = regularRoutine.ReturnType ?? targetType;
            if (!IsAssignableTo(source: returnType, target: targetType))
            {
                ReportError(code: SemanticDiagnosticCode.AssignmentTypeMismatch,
                    message:
                    $"Compound assignment: return type '{returnType.Name}' of '{regularMemberRoutine}' " +
                    $"is not assignable to target type '{targetType.Name}'.",
                    location: compound.Location);
            }

            return targetType;
        }

        // Step 3: neither in-place nor regular operator found on this type.
        ReportError(code: SemanticDiagnosticCode.CompoundAssignmentNotSupported,
            message:
            $"Type '{targetType.Name}' does not support compound assignment '{compound.Operator.ToStringRepresentation()}='. " +
            $"Define in-place operator '{inPlaceMemberRoutine}' or regular operator '{regularMemberRoutine}'.",
            location: compound.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Validates the modifiability / write-access of a compound-assignment target — the same rules as a
    /// plain assignment target: preset (immutable) variable, @readonly member-routine mutation of <c>me</c>,
    /// and read-only-protocol index writes.
    /// </summary>
    private void ValidateCompoundAssignmentTarget(CompoundAssignmentExpression compound)
    {
        switch (compound.Target)
        {
            case IdentifierExpression id:
            {
                VariableInfo? varInfo = _registry.LookupVariable(name: id.Name);
                if (varInfo is { IsModifiable: false })
                {
                    ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                        message: $"Cannot assign to preset variable '{id.Name}'.",
                        location: compound.Location);
                }

                break;
            }
            case MemberExpression member:
            {
                TypeSymbol objectType = AnalyzeExpression(expression: member.Object);
                ValidateMemberVariableWriteAccess(objectType: objectType,
                    memberVariableName: member.MemberName,
                    location: compound.Location);

                if (_registry.CompilationLanguage != Language.Suflae &&
                    _currentRoutine is { IsReadOnly: true } &&
                    member.Object is IdentifierExpression { Name: "me" })
                {
                    ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMemberRoutine,
                        message:
                        $"Cannot mutate member variable '{member.MemberName}' in a @readonly member routine. " +
                        "Use @reshaping to allow mutations.",
                        location: compound.Location);
                }

                break;
            }
            case IndexExpression index:
            {
                TypeSymbol indexedObjectType = AnalyzeExpression(expression: index.Object);
                if (IsReadOnlyTransparentProtocol(type: indexedObjectType))
                {
                    ReportError(code: SemanticDiagnosticCode.WriteThroughReadOnlyWrapper,
                        message:
                        $"Cannot write through index access on read-only protocol '{indexedObjectType.Name}'. " +
                        "Use Controlling[T] or a writable token instead.",
                        location: compound.Location);
                }

                if (index.Object is IdentifierExpression indexedVar)
                {
                    VariableInfo? varInfo = _registry.LookupVariable(name: indexedVar.Name);
                    if (varInfo is { IsModifiable: false })
                    {
                        ReportError(code: SemanticDiagnosticCode.AssignmentToImmutable,
                            message:
                            $"Cannot assign to index of preset variable '{indexedVar.Name}'.",
                            location: compound.Location);
                    }
                }

                break;
            }
        }
    }

    private TypeSymbol AnalyzeUnaryExpression(UnaryExpression unary)
    {
        TypeSymbol operandType = AnalyzeExpression(expression: unary.Operand);

        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                // Suppress for an ErrorTypeSymbol operand: either the operand already reported its own
                // error (cascade), or it is a buildtime splice deferred to monomorphization — e.g.
                // `not me.${m.name}.is_none()` in an `expand` body, where the splice-member call is
                // ErrorType pre-monomorph and the real Bool only exists per concrete field.
                if (!IsBoolType(type: operandType) && operandType is not ErrorTypeSymbol)
                {
                    ReportError(code: SemanticDiagnosticCode.LogicalNotRequiresBool,
                        message: "Logical 'not' operator requires a boolean operand.",
                        location: unary.Location);
                }

                return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;

            case UnaryOperator.Minus:
                if (operandType != ErrorTypeSymbol.Instance && !IsNumericType(type: operandType) &&
                    _registry.LookupMemberRoutine(type: operandType, memberRoutineName: "neg") ==
                    null)
                {
                    ReportError(code: SemanticDiagnosticCode.NegationRequiresNumeric,
                        message: "Negation operator requires a numeric operand.",
                        location: unary.Location);
                }

                return operandType;

            case UnaryOperator.BitwiseNot:
                if (!IsIntegerType(type: operandType))
                {
                    ReportError(code: SemanticDiagnosticCode.BitwiseNotRequiresInteger,
                        message: "Bitwise 'not' operator requires an integer operand.",
                        location: unary.Location);
                }

                return operandType;

            case UnaryOperator.ForceUnwrap:
                return AnalyzeForceUnwrap(unary: unary, operandType: operandType);

            case UnaryOperator.Steal:
            default:
                return operandType;
        }
    }

    /// <summary>
    /// Analyzes the force-unwrap operator (<c>!!</c>): a carrier's payload type (with the
    /// <c>Maybe[Owned[T]]!!</c> → <c>Modifying[T]</c> special case), else the type's <c>unwrap</c>
    /// member routine return, else RF-S (unsupported operator).
    /// </summary>
    private TypeSymbol AnalyzeForceUnwrap(UnaryExpression unary, TypeSymbol operandType)
    {
        if (IsCarrierType(type: operandType) && operandType.TypeArguments is { Count: > 0 })
        {
            TypeSymbol inner = operandType.TypeArguments[index: 0];
            // `Maybe[T]!!` yields `Modifying[T]` — the unwrap
            // is an exclusive scope-bound borrow, not a copy. Same LLVM repr (ptr), but
            // typed as Modifying so the destructor scheduler skips it.
            if (IsMaybeType(type: operandType) &&
                IsOwnedOf(type: inner, inner: out TypeSymbol ownedInner))
            {
                return _registry.GetOrCreateWrapperType(
                    wrapperName: Declaration.RuntimeContract.Modifying,
                    innerType: ownedInner,
                    isReadOnly: false);
            }

            return inner;
        }

        // User type — look up unwrap memberRoutine
        RoutineInfo? unwrapMemberRoutine =
            _registry.LookupMemberRoutine(type: operandType, memberRoutineName: "unwrap");
        if (unwrapMemberRoutine != null)
        {
            return unwrapMemberRoutine.ReturnType ?? ErrorTypeSymbol.Instance;
        }

        ReportError(code: SemanticDiagnosticCode.TypeDoesNotSupportOperator,
            message: $"Type '{operandType.Name}' does not support the '!!' operator. " +
                     "Implement 'unwrap() -> T' to enable force unwrap.",
            location: unary.Location);
        return ErrorTypeSymbol.Instance;
    }

    #endregion
}
