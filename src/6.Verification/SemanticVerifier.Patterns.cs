using Compiler.Diagnostics;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;
using Compiler.Verification.Scopes;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Pattern analysis for when/is expressions.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Pattern Analysis

    /// <summary>
    /// Declares a pattern binding variable, checking for shadowing of variables in outer scopes.
    /// </summary>
    private void DeclarePatternVariable(string name, TypeSymbol type, SourceLocation location)
    {
        // Check if this name exists in a parent scope (shadowing)
        Scope? parent = _registry.CurrentScope.Parent;
        if (parent?.LookupVariable(name: name) != null)
        {
            ReportError(code: SemanticDiagnosticCode.IdentifierShadowing,
                message:
                $"Pattern variable '{name}' shadows an existing variable in an outer scope.",
                location: location);
        }

        // Still declare the variable even if shadowing, to avoid cascading errors
        _registry.DeclareVariable(name: name, type: type, location: location);
    }

    private void AnalyzePattern(Pattern pattern, TypeSymbol matchedType)
    {
        switch (pattern)
        {
            case LiteralPattern:
                // Literal patterns don't bind variables
                break;

            case IdentifierPattern id:
                // Bind the matched value to the identifier
                DeclarePatternVariable(name: id.Name, type: matchedType, location: id.Location);
                break;

            case TypePattern typePat:
                HandleTypePattern(typePat: typePat, matchedType: matchedType);
                break;

            case WildcardPattern:
                // Wildcards don't bind variables
                break;

            case VariantPattern variant:
                // Handle variant case matching - look up case from matched type
                AnalyzeVariantPattern(pattern: variant, matchedType: matchedType);
                break;

            case GuardPattern guard:
                HandleGuardPattern(guard: guard, matchedType: matchedType);
                break;

            case ElsePattern elsePat:
                if (elsePat.VariableName != null)
                {
                    DeclarePatternVariable(name: elsePat.VariableName,
                        type: matchedType,
                        location: elsePat.Location);
                }

                break;

            case NonePattern:
            case CrashablePattern:
                // These don't bind variables directly
                break;

            case DestructuringPattern destruct:
                AnalyzeDestructuringPattern(pattern: destruct, sourceType: matchedType);
                break;

            case TypeDestructuringPattern typeDestruct:
                AnalyzeTypeDestructuringPattern(pattern: typeDestruct);
                break;

            case ExpressionPattern exprPat:
                HandleExpressionPattern(exprPat: exprPat);
                break;

            case FlagsPattern flagsPat:
                HandleFlagsPattern(flagsPat: flagsPat, matchedType: matchedType);
                break;

            case ComparisonPattern cmp when matchedType is ChoiceTypeInfo:
                // Choice types must use 'is CASE_NAME', not '== CASE_NAME'
                ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                    message: "Use 'is' instead of comparison operators for choice case matching.",
                    location: cmp.Location);
                break;
        }
    }

    /// <summary>
    /// Analyzes a <see cref="TypePattern"/> ('is None' / 'is CASE' / 'is Type'), dispatching to the
    /// None, choice-case, and flags-member special cases before the general type-compatibility path.
    /// </summary>
    private void HandleTypePattern(TypePattern typePat, TypeSymbol matchedType)
    {
        // None is a keyword, not a registered type — handle it directly
        if (typePat.Type.Name == "None")
        {
            HandleNoneTypePattern(typePat: typePat, matchedType: matchedType);
            return;
        }

        // Choice case pattern: 'is NORTH' or 'is Direction.NORTH'
        // When the matched type is a choice, check if the identifier is a case name
        // before attempting type resolution (which would fail for case names).
        if (matchedType is ChoiceTypeInfo choiceForIs)
        {
            HandleChoiceCaseTypePattern(typePat: typePat, choiceForIs: choiceForIs);
            return;
        }

        // Flags member pattern: 'is READ' when matched type is a flags type.
        // Single-flag tests are parsed as TypePattern by the parser.
        if (matchedType is FlagsTypeInfo flagsForIs)
        {
            HandleFlagsMemberTypePattern(typePat: typePat, flagsForIs: flagsForIs);
            return;
        }

        TypeSymbol patternType = ResolveType(typeExpr: typePat.Type);

        // Check type compatibility between matched type and pattern type
        if (patternType is not ErrorTypeInfo && matchedType is not ErrorTypeInfo &&
            !IsTypePatternCompatible(matchedType: matchedType, patternType: patternType))
        {
            ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                message:
                $"Type pattern 'is {patternType.Name}' can never match a value of type '{matchedType.Name}'.",
                location: typePat.Location);
        }

        if (typePat.VariableName != null)
        {
            DeclarePatternVariable(name: typePat.VariableName,
                type: patternType,
                location: typePat.Location);
        }

        // Process destructuring bindings if present
        if (typePat.Bindings is { Count: > 0 })
        {
            foreach (DestructuringBinding binding in typePat.Bindings)
            {
                TypeSymbol memberVariableType = LookupMemberVariableType(type: patternType,
                    memberVariableName: binding.MemberVariableName);

                if (binding.NestedPattern != null)
                {
                    AnalyzePattern(pattern: binding.NestedPattern,
                        matchedType: memberVariableType);
                }
                else if (binding.BindingName != null)
                {
                    DeclarePatternVariable(name: binding.BindingName,
                        type: memberVariableType,
                        location: binding.Location);
                }
            }
        }
    }

    /// <summary>
    /// Validates an 'is None' type pattern against the matched type — legal only on carrier/variant/
    /// nullable-entity types.
    /// </summary>
    private void HandleNoneTypePattern(TypePattern typePat, TypeSymbol matchedType)
    {
        bool allowsNone = matchedType is ErrorTypeInfo
            || IsMaybeType(type: matchedType)
            || GetCarrierBaseName(type: matchedType) == "Lookup"
            || matchedType is VariantTypeInfo
            // A `Result[None]` (void-success crashable) is matched on its None success arm
            // by `is None` — Ok(None) | Crashable. None is the void success value.
            // Only valid when the success type argument is itself None — `Result[S32]`'s
            // success arm is S32, so `is None` there is still a mismatch.
            || matchedType is CrashableTypeInfo
            || (GetCarrierBaseName(type: matchedType) == "Result"
                && matchedType is RecordTypeInfo { TypeArguments: [{ Name: "None" }, ..] })
            // Suflae: a nullable entity reference (`E?`) is a Roamed[E] handle that may be a
            // null/none handle, so `is None` / `isnot None` is a legal none-check on it.
            || (_registry.Language == Language.Suflae
                && matchedType is RecordTypeInfo
                    { GenericDefinition.Name: Declaration.RuntimeContract.Roamed });
        if (!allowsNone)
        {
            ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                message:
                $"Type pattern 'is None' can only match Maybe[T], Lookup[T], or a variant type — not '{matchedType.Name}'.",
                location: typePat.Location);
        }
    }

    /// <summary>
    /// Validates an 'is CASE' type pattern against a choice type — the case must exist and may not
    /// bind variables or destructure.
    /// </summary>
    private void HandleChoiceCaseTypePattern(TypePattern typePat, ChoiceTypeInfo choiceForIs)
    {
        string? choiceCaseName = ExtractChoiceCaseFromTypePattern(
            typePat: typePat,
            choice: choiceForIs);
        if (choiceCaseName != null)
        {
            // Valid choice case match via 'is' — no type resolution needed
            if (typePat.VariableName != null)
            {
                ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                    message: "Choice case patterns cannot bind variables.",
                    location: typePat.Location);
            }

            if (typePat.Bindings is { Count: > 0 })
            {
                ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                    message: "Choice case patterns cannot destructure.",
                    location: typePat.Location);
            }

            return;
        }

        // Not a valid case name — report specific error
        ReportError(code: SemanticDiagnosticCode.ChoiceCaseNotFound,
            message:
            $"Choice type '{choiceForIs.Name}' does not have a case named '{typePat.Type.Name}'.",
            location: typePat.Location);
    }

    /// <summary>
    /// Validates an 'is FLAG' type pattern against a flags type — the flag must be a member or a
    /// same-flags-typed variable (subset check); it may not bind variables or destructure.
    /// </summary>
    private void HandleFlagsMemberTypePattern(TypePattern typePat, FlagsTypeInfo flagsForIs)
    {
        string flagName = typePat.Type.Name;
        if (flagsForIs.Members.Any(predicate: m => m.Name == flagName))
        {
            if (typePat.VariableName != null)
            {
                ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                    message: "Flags member patterns cannot bind variables.",
                    location: typePat.Location);
            }

            if (typePat.Bindings is { Count: > 0 })
            {
                ReportError(code: SemanticDiagnosticCode.PatternTypeMismatch,
                    message: "Flags member patterns cannot destructure.",
                    location: typePat.Location);
            }

            return;
        }

        // Option A: `subj is <name>` where <name> is a variable of the same
        // flags type — lowered to subset check `(subj & rhs) == rhs`.
        var flagVar = _registry.CurrentScope.LookupVariable(name: flagName);
        if (flagVar?.Type is FlagsTypeInfo varFlagsType &&
            varFlagsType.Name == flagsForIs.Name)
        {
            return;
        }

        ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
            message:
            $"Flags type '{flagsForIs.Name}' does not have a member named '{flagName}'.",
            location: typePat.Location);
    }

    /// <summary>
    /// Analyzes a guard pattern: the inner pattern, then the guard expression (which must be bool).
    /// </summary>
    private void HandleGuardPattern(GuardPattern guard, TypeSymbol matchedType)
    {
        // First analyze the inner pattern
        AnalyzePattern(pattern: guard.InnerPattern, matchedType: matchedType);
        // Then analyze the guard expression (must be bool)
        TypeSymbol guardType = AnalyzeExpression(expression: guard.Guard);
        if (!IsBoolType(type: guardType))
        {
            ReportError(code: SemanticDiagnosticCode.PatternGuardNotBool,
                message: "Guard expression must be boolean.",
                location: guard.Guard.Location);
        }
    }

    /// <summary>
    /// Analyzes an expression pattern — the expression must be boolean.
    /// </summary>
    private void HandleExpressionPattern(ExpressionPattern exprPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: exprPat.Expression);
        if (!IsBoolType(type: exprType))
        {
            ReportError(code: SemanticDiagnosticCode.ExpressionPatternNotBool,
                message: "Expression pattern must be boolean.",
                location: exprPat.Location);
        }
    }

    /// <summary>
    /// Analyzes a flags pattern — the matched type must be a flags type and every named / excluded
    /// flag must exist on it.
    /// </summary>
    private void HandleFlagsPattern(FlagsPattern flagsPat, TypeSymbol matchedType)
    {
        if (matchedType is not FlagsTypeInfo flagsTypeForPat)
        {
            if (matchedType.Category != TypeCategory.Error)
            {
                ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                    message:
                    $"Flags pattern requires a flags type, but got '{matchedType.Name}'.",
                    location: flagsPat.Location);
            }

            return;
        }

        // Validate each flag name exists
        foreach (string flagName in flagsPat.FlagNames.Where(fn => flagsTypeForPat.Members.All(m => m.Name != fn)))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                message:
                $"Flags type '{flagsTypeForPat.Name}' does not have a member named '{flagName}'.",
                location: flagsPat.Location);
        }

        // Validate excluded flags
        if (flagsPat.ExcludedFlags != null)
        {
            foreach (string flagName in flagsPat.ExcludedFlags.Where(fn => flagsTypeForPat.Members.All(m => m.Name != fn)))
            {
                ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                    message:
                    $"Flags type '{flagsTypeForPat.Name}' does not have a member named '{flagName}'.",
                    location: flagsPat.Location);
            }
        }
    }

    private void AnalyzeDestructuringPattern(DestructuringPattern pattern, TypeSymbol sourceType)
    {
        foreach (DestructuringBinding binding in pattern.Bindings)
        {
            TypeSymbol memberVariableType = ErrorTypeInfo.Instance;

            // Get member variable type from source type
            if (binding.MemberVariableName != null && sourceType is RecordTypeInfo record)
            {
                memberVariableType = record
                                    .LookupMemberVariable(
                                         memberVariableName: binding.MemberVariableName)
                                   ?.Type ?? ErrorTypeInfo.Instance;
            }
            else if (binding.MemberVariableName != null && sourceType is EntityTypeInfo entity)
            {
                memberVariableType = entity
                                    .LookupMemberVariable(
                                         memberVariableName: binding.MemberVariableName)
                                   ?.Type ?? ErrorTypeInfo.Instance;
            }

            if (binding.NestedPattern != null)
            {
                // Handle nested destructuring
                AnalyzePattern(pattern: binding.NestedPattern, matchedType: memberVariableType);
            }
            else if (binding.BindingName != null)
            {
                DeclarePatternVariable(name: binding.BindingName,
                    type: memberVariableType,
                    location: binding.Location);
            }
        }
    }

    /// <summary>
    /// Analyzes a variant pattern, looking up the case type from the matched variant/mutant type.
    /// </summary>
    /// <param name="pattern">The variant pattern to analyze.</param>
    /// <param name="matchedType">The type being matched against.</param>
    private void AnalyzeVariantPattern(VariantPattern pattern, TypeSymbol matchedType)
    {
        // Get the members from the matched type
        List<VariantMemberInfo>? members = matchedType switch
        {
            VariantTypeInfo variant => variant.Members,
            _ => null
        };

        if (members == null)
        {
            ReportError(code: SemanticDiagnosticCode.VariantPatternOnNonVariant,
                message:
                $"Cannot match variant pattern against non-variant type '{matchedType.Name}'.",
                location: pattern.Location);
            // Still declare bindings with error type to avoid cascading errors
            DeclareBindingsWithErrorType(bindings: pattern.Bindings);
            return;
        }

        // Find the matching member by case name (type name or "None")
        VariantMemberInfo? matchedMember =
            members.FirstOrDefault(predicate: m => m.Name == pattern.CaseName);
        if (matchedMember == null)
        {
            ReportError(code: SemanticDiagnosticCode.VariantCaseNotFound,
                message:
                $"Variant type '{matchedType.Name}' does not have a member type '{pattern.CaseName}'.",
                location: pattern.Location);
            DeclareBindingsWithErrorType(bindings: pattern.Bindings);
            return;
        }

        // Bind the payload if present
        if (pattern.Bindings is not { Count: > 0 })
        {
            return;
        }

        if (matchedMember.IsNone)
        {
            ReportError(code: SemanticDiagnosticCode.VariantCaseNoPayload,
                message: "Variant member 'None' has no payload to destructure.",
                location: pattern.Location);
            return;
        }

        BindVariantPayload(pattern: pattern, payloadType: matchedMember.Type!);
    }

    /// <summary>
    /// Binds a variant pattern's payload: a single anonymous binding binds directly to the payload
    /// type, while multiple / named bindings destructure it via member-variable lookup.
    /// </summary>
    private void BindVariantPayload(VariantPattern pattern, TypeSymbol payloadType)
    {
        // Caller guarantees Bindings is non-empty (checked before calling), but the field is
        // nullable — guard here so subsequent indexing is clean.
        if (pattern.Bindings == null) return;

        // For a single binding without member variable name, bind directly to the payload
        if (pattern.Bindings.Count == 1 && pattern.Bindings[index: 0].MemberVariableName == null)
        {
            DestructuringBinding binding = pattern.Bindings[index: 0];
            if (binding.NestedPattern is { } nestedPattern)
            {
                AnalyzePattern(pattern: nestedPattern, matchedType: payloadType);
            }
            else if (binding.BindingName != null)
            {
                DeclarePatternVariable(name: binding.BindingName,
                    type: payloadType,
                    location: binding.Location);
            }
        }
        else
        {
            // Multiple bindings - payload must be a record/entity type
            foreach (DestructuringBinding binding in pattern.Bindings)
            {
                TypeSymbol memberVariableType = LookupMemberVariableType(type: payloadType,
                    memberVariableName: binding.MemberVariableName);

                if (binding.NestedPattern != null)
                {
                    AnalyzePattern(pattern: binding.NestedPattern,
                        matchedType: memberVariableType);
                }
                else if (binding.BindingName != null)
                {
                    DeclarePatternVariable(name: binding.BindingName,
                        type: memberVariableType,
                        location: binding.Location);
                }
            }
        }
    }

    /// <summary>
    /// Analyzes a type destructuring pattern, looking up member variable types from the target type.
    /// </summary>
    /// <param name="pattern">The type destructuring pattern to analyze.</param>
    private void AnalyzeTypeDestructuringPattern(TypeDestructuringPattern pattern)
    {
        TypeSymbol targetType = ResolveType(typeExpr: pattern.Type);

        foreach (DestructuringBinding binding in pattern.Bindings)
        {
            TypeSymbol memberVariableType = LookupMemberVariableType(type: targetType,
                memberVariableName: binding.MemberVariableName);

            if (binding.NestedPattern != null)
            {
                AnalyzePattern(pattern: binding.NestedPattern, matchedType: memberVariableType);
            }
            else if (binding.BindingName != null)
            {
                DeclarePatternVariable(name: binding.BindingName,
                    type: memberVariableType,
                    location: binding.Location);
            }
        }
    }

    /// <summary>
    /// Looks up a member variable type from a type. Returns ErrorTypeInfo if not found.
    /// </summary>
    private static TypeSymbol LookupMemberVariableType(TypeSymbol type, string? memberVariableName)
    {
        if (memberVariableName == null)
        {
            return ErrorTypeInfo.Instance;
        }

        return type switch
        {
            RecordTypeInfo record => record
                                    .LookupMemberVariable(memberVariableName: memberVariableName)
                                   ?.Type ?? ErrorTypeInfo.Instance,
            EntityTypeInfo entity => entity
                                    .LookupMemberVariable(memberVariableName: memberVariableName)
                                   ?.Type ?? ErrorTypeInfo.Instance,
            _ => ErrorTypeInfo.Instance
        };
    }

    /// <summary>
    /// Declares bindings with error type to prevent cascading errors.
    /// </summary>
    private void DeclareBindingsWithErrorType(List<DestructuringBinding>? bindings)
    {
        if (bindings == null)
        {
            return;
        }

        foreach (string bindingName in bindings.Select(b => b.BindingName).OfType<string>())
        {
            _registry.DeclareVariable(name: bindingName, type: ErrorTypeInfo.Instance);
        }
    }

    /// <summary>
    /// Checks if a type pattern can potentially match a value of the given matched type.
    /// Returns true if the match is possible, false if provably impossible.
    /// </summary>
    private bool IsTypePatternCompatible(TypeSymbol matchedType, TypeSymbol patternType)
    {
        // Same type - always compatible
        if (matchedType.Name == patternType.Name)
            return true;
        // If either is a type parameter, we can't know at analysis time
        if (matchedType.Category == TypeCategory.TypeParameter || patternType.Category == TypeCategory.TypeParameter)
            return true;
        // If matched type is a protocol, any concrete type could conform
        if (matchedType.Category == TypeCategory.Protocol)
            return true;
        // Carrier types (Maybe<T>, Result<T>, Lookup<T>) can be matched against any type/protocol
        if (IsCarrierType(type: matchedType))
            return true;
        // Variant: `is <MemberType>` matches if MemberType is one of the variant's members
        if (matchedType is VariantTypeInfo variantMatched && VariantHasMemberOfType(variant: variantMatched, patternType: patternType))
            return true;
        // If pattern type is a protocol, check if matched type implements it
        if (patternType.Category == TypeCategory.Protocol)
            return ImplementsProtocol(type: matchedType, protocolName: patternType.Name);
        // IsAssignableTo in either direction covers subtyping
        return IsAssignableTo(source: matchedType, target: patternType) ||
               IsAssignableTo(source: patternType, target: matchedType);
    }

    /// <summary>
    /// Returns true when any member of <paramref name="variant"/> carries the given <paramref name="patternType"/>
    /// by name or full name — used by <see cref="IsTypePatternCompatible"/> to determine whether an
    /// <c>is MemberType</c> pattern can ever match the variant.
    /// </summary>
    private static bool VariantHasMemberOfType(VariantTypeInfo variant, TypeSymbol patternType)
    {
        return variant.Members.Any(m =>
            m.Type != null &&
            (m.Type.Name == patternType.Name || m.Type.FullName == patternType.FullName));
    }

    #endregion

    #region Exhaustiveness Checking

    /// <summary>
    /// Result of exhaustiveness analysis.
    /// </summary>
    private readonly record struct ExhaustivenessResult(
        bool IsExhaustive,
        List<string> MissingCases);

    /// <summary>
    /// Checks whether the given when clauses exhaustively cover all cases of the matched type.
    /// </summary>
    private static ExhaustivenessResult CheckExhaustiveness(List<WhenClause> clauses,
        TypeSymbol matchedType)
    {
        // If any clause is a catch-all pattern, it's always exhaustive
        if (clauses.Any(clause => clause.Pattern is WildcardPattern or ElsePattern or IdentifierPattern))
        {
            return new ExhaustivenessResult(IsExhaustive: true, MissingCases: []);
        }

        if (matchedType is ChoiceTypeInfo choice)
        {
            return CheckChoiceExhaustiveness(clauses: clauses, choice: choice);
        }

        if (matchedType is VariantTypeInfo variant)
        {
            return CheckVariantExhaustiveness(clauses: clauses,
                members: variant.Members,
                typeName: variant.Name);
        }

        if (IsCarrierType(type: matchedType))
        {
            return CheckErrorHandlingExhaustiveness(clauses: clauses, carrierType: matchedType);
        }

        if (matchedType is FlagsTypeInfo)
        {
            // #129: Flags when always requires else — too many combinations to exhaustively check
            return new ExhaustivenessResult(IsExhaustive: false, MissingCases: ["else"]);
        }

        if (matchedType.Name == "Bool")
        {
            return CheckBoolExhaustiveness(clauses: clauses);
        }

        return new ExhaustivenessResult(IsExhaustive: false, MissingCases: []);
    }

    /// <summary>
    /// Checks whether all cases of a choice type are covered by 'is' TypePatterns.
    /// </summary>
    private static ExhaustivenessResult CheckChoiceExhaustiveness(
        List<WhenClause> clauses, ChoiceTypeInfo choice)
    {
        var coveredCases = clauses
            .Select(clause => ExtractChoiceCaseName(pattern: clause.Pattern))
            .OfType<string>()
            .ToHashSet();

        var missingCases = choice.Cases
                                 .Where(predicate: c => !coveredCases.Contains(item: c.Name))
                                 .Select(selector: c => c.Name)
                                 .ToList();

        return new ExhaustivenessResult(IsExhaustive: missingCases.Count == 0,
            MissingCases: missingCases);
    }

    /// <summary>
    /// Extracts the choice case name from a TypePattern ('is' keyword).
    /// Returns null if the pattern is not a choice case match.
    /// Choice matching only supports the 'is' syntax — '==' is not valid for choices.
    /// </summary>
    private static string? ExtractChoiceCaseName(Pattern pattern)
    {
        // TypePattern: is ACTIVE or is Status.ACTIVE
        if (pattern is TypePattern typePat)
        {
            string name = typePat.Type.Name;

            // Qualified: Direction.NORTH -> extract "NORTH"
            if (name.Contains(value: '.'))
            {
                return name[(name.LastIndexOf(value: '.') + 1)..];
            }

            // Shorthand: NORTH — caller validates against case list
            return name;
        }

        return null;
    }

    /// <summary>
    /// Extracts the choice case name from a TypePattern when the matched type is a choice.
    /// Handles both shorthand (NORTH) and qualified (Direction.NORTH) forms.
    /// Returns null if the pattern doesn't match any case.
    /// </summary>
    private static string? ExtractChoiceCaseFromTypePattern(TypePattern typePat,
        ChoiceTypeInfo choice)
    {
        string name = typePat.Type.Name;

        // Qualified form: Direction.NORTH -> extract "NORTH" if prefix matches choice name
        if (name.Contains(value: '.'))
        {
            int dotIndex = name.LastIndexOf(value: '.');
            string prefix = name[..dotIndex];
            string casePart = name[(dotIndex + 1)..];

            if (prefix == choice.Name && choice.Cases.Any(predicate: c => c.Name == casePart))
            {
                return casePart;
            }

            return null;
        }

        // Shorthand form: NORTH -> match directly against choice cases
        if (choice.Cases.Any(predicate: c => c.Name == name))
        {
            return name;
        }

        return null;
    }


    /// <summary>
    /// Checks whether all member types of a variant are covered.
    /// The parser creates TypePattern for variant matching (is S64, is None),
    /// not VariantPattern.
    /// </summary>
    private static ExhaustivenessResult CheckVariantExhaustiveness(
        List<WhenClause> clauses, List<VariantMemberInfo> members,
        string typeName)
    {
        var coveredMembers = clauses
            .Select(clause => ExtractVariantMemberName(pattern: clause.Pattern, typeName: typeName))
            .OfType<string>()
            .ToHashSet();

        var missingMembers = members.Where(predicate: m => !coveredMembers.Contains(item: m.Name))
                                    .Select(selector: m => m.Name)
                                    .ToList();

        return new ExhaustivenessResult(IsExhaustive: missingMembers.Count == 0,
            MissingCases: missingMembers);
    }

    /// <summary>
    /// Extracts the variant member type name from a pattern.
    /// Handles TypePattern with bare type names (is S64) or dotted (is Value.S64),
    /// as well as VariantPattern (if ever created).
    /// </summary>
    private static string? ExtractVariantMemberName(Pattern pattern, string typeName)
    {
        switch (pattern)
        {
            case TypePattern typePat:
            {
                string name = typePat.Type.Name;

                // Dotted form: "Value.S64" -> extract "S64"
                if (name.StartsWith(value: typeName + ".",
                        comparisonType: StringComparison.Ordinal))
                {
                    return name[(typeName.Length + 1)..];
                }

                // Bare form: "S64" or "None" — matches against member type names
                if (!name.Contains(value: '.'))
                {
                    return name;
                }

                return null;
            }
            case VariantPattern variant:
                return variant.CaseName;
            default:
                return null;
        }
    }

    /// <summary>
    /// Checks whether Maybe/Result/Lookup error handling types are exhaustively matched.
    /// </summary>
    private static ExhaustivenessResult CheckErrorHandlingExhaustiveness(
        List<WhenClause> clauses, TypeSymbol carrierType)
    {
        bool hasAbsent = false;
        bool hasCrashableCatchAll = false;
        bool hasValue = false;

        foreach (Pattern pattern in clauses.Select(clause => clause.Pattern))
        {
            if (IsAbsentPattern(pattern: pattern, carrierType: carrierType))
            {
                hasAbsent = true;
            }
            else if (IsCrashableCatchAll(pattern: pattern))
            {
                // Only generic 'is Crashable e' counts as catch-all, not specific error types (#89)
                hasCrashableCatchAll = true;
            }
            else if (!IsNonePattern(pattern: pattern) && pattern is not CrashablePattern)
            {
                // Any other pattern (type check, literal, etc.) counts as value arm
                hasValue = true;
            }
        }

        List<string> missing = CollectCarrierMissingCases(
            carrierBaseName: GetCarrierBaseName(type: carrierType),
            hasAbsent: hasAbsent,
            hasCrashableCatchAll: hasCrashableCatchAll,
            hasValue: hasValue);

        return new ExhaustivenessResult(IsExhaustive: missing.Count == 0, MissingCases: missing);
    }

    /// <summary>
    /// Computes the missing arms for a carrier type given which arm kinds were seen: Maybe needs
    /// None + value, Result needs Crashable + value, Lookup needs None + Crashable + value.
    /// </summary>
    private static List<string> CollectCarrierMissingCases(string? carrierBaseName,
        bool hasAbsent, bool hasCrashableCatchAll, bool hasValue)
    {
        var missing = new List<string>();

        switch (carrierBaseName)
        {
            case "Maybe":
                if (!hasAbsent)
                {
                    missing.Add(item: "None");
                }

                if (!hasValue)
                {
                    missing.Add(item: "value");
                }

                break;
            case "Result":
                if (!hasCrashableCatchAll)
                {
                    missing.Add(item: "Crashable");
                }

                if (!hasValue)
                {
                    missing.Add(item: "value");
                }

                break;
            case "Lookup":
                if (!hasAbsent)
                {
                    missing.Add(item: "None");
                }

                if (!hasCrashableCatchAll)
                {
                    missing.Add(item: "Crashable");
                }

                if (!hasValue)
                {
                    missing.Add(item: "value");
                }

                break;
        }

        return missing;
    }

    /// <summary>
    /// Checks whether both true and false are covered by literal patterns.
    /// </summary>
    private static ExhaustivenessResult CheckBoolExhaustiveness(List<WhenClause> clauses)
    {
        bool hasTrue = false;
        bool hasFalse = false;

        foreach (Pattern pattern in clauses.Select(clause => clause.Pattern))
        {
            if (pattern is LiteralPattern { LiteralType: TokenType.True })
            {
                hasTrue = true;
            }
            else if (pattern is LiteralPattern { LiteralType: TokenType.False })
            {
                hasFalse = true;
            }
        }

        var missing = new List<string>();
        if (!hasTrue)
        {
            missing.Add(item: "true");
        }

        if (!hasFalse)
        {
            missing.Add(item: "false");
        }

        return new ExhaustivenessResult(IsExhaustive: missing.Count == 0, MissingCases: missing);
    }

    /// <summary>
    /// Produces a string key from a pattern for duplicate detection.
    /// Returns null for patterns that cannot be meaningfully compared (identifiers, wildcards, else).
    /// </summary>
    private static string? GetPatternKey(Pattern pattern)
    {
        return pattern switch
        {
            LiteralPattern lit => $"literal:{lit.Value}",
            TypePattern tp => $"type:{tp.Type.Name}",
            VariantPattern vp => $"variant:{vp.CaseName}",
            NonePattern => "none",
            CrashablePattern => "crashable",
            FlagsPattern fp =>
                $"flags:{string.Join(separator: "|", values: fp.FlagNames.OrderBy(keySelector: n => n))}",
            // Identifier, wildcard, else, guard, expression patterns are not deduplicated
            _ => null
        };
    }

    #endregion
}
