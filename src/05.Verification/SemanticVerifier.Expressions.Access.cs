using System.Collections.Generic;
using System.Linq;
using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification.Enums;

namespace Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    private const string GetItemMemberRoutineName = "getitem";

    private static bool TryGetTransparentProtocolTarget(TypeSymbol type, out TypeSymbol targetType)
    {
        if (type is ProtocolTypeInfo { TypeArguments: { Count: > 0 } } proto
            && HasOnlyMarkerCoercionMemberRoutines(proto))
        {
            targetType = proto.TypeArguments![index: 0]!;
            return true;
        }

        targetType = type;
        return false;
    }

    /// <summary>
    /// True if the protocol declares no memberRoutines other than the implicit-coercion markers
    /// refer/control. Such protocols (Accessing[T], Controlling[T]) are transparent for
    /// member access — `param.member` falls through to the inner T.
    /// </summary>
    private static bool HasOnlyMarkerCoercionMemberRoutines(ProtocolTypeInfo proto)
    {
        foreach (ProtocolMemberRoutineInfo m in proto.MemberRoutines)
        {
            if (m.Name != "access" && m.Name != "control") return false;
        }
        return true;
    }

    private static bool IsReadOnlyTransparentProtocol(TypeSymbol type)
    {
        return type is ProtocolTypeInfo proto &&
               (proto.GenericDefinition ?? proto).BareName == Compiler.Resolution.RuntimeContract.Accessing;
    }

    /// <summary>
    /// Analyzes a comptime splice-selector member access (<c>x.${m.name}</c>). The receiver and
    /// the selector splice are analyzed for real (surfacing mistakes in either), but the selected
    /// field's concrete type is unknown until monomorphization, so this defers to
    /// <see cref="ErrorTypeInfo"/> — the same cascade-suppressing deferral used for unresolved
    /// generic-body expressions. The real "does this field have that member?" check runs on the
    /// unrolled member access at instantiation.
    /// </summary>
    private TypeSymbol AnalyzeSpliceMemberExpression(SpliceMemberExpression spliceMember)
    {
        AnalyzeExpression(expression: spliceMember.Object);
        AnalyzeExpression(expression: spliceMember.Selector);
        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// Analyzes a comptime splice (<c>${expr}</c>). The inner projection is analyzed for real; a
    /// selector-position splice must name a field (fold to <c>Text</c>). The splice's own value is
    /// comptime-only, so it types as <see cref="ErrorTypeInfo"/> (deferred to monomorphization).
    /// </summary>
    private TypeSymbol AnalyzeSpliceExpression(SpliceExpression splice)
    {
        TypeSymbol innerType = AnalyzeExpression(expression: splice.Inner);
        if (splice.RequiredKind == SpliceKind.Selector
            && innerType is not ErrorTypeInfo
            && innerType.Name != "Text")
        {
            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message:
                "A member-selector splice 'x.${...}' must name a field: its inner expression has to be a Text name (e.g. 'm.name').",
                location: splice.Location);
        }

        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeMemberExpression(MemberExpression member)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Comptime `expand` handle projection: `m.name` (field name, Text), `m.id` (ordinal, U64).
        // The handle is a sentinel; its projections type leniently so the expand body typechecks
        // before monomorphization. Any other projection on the handle is a clear mistake.
        if (objectType is ComptimeHandleTypeInfo)
        {
            switch (member.MemberName)
            {
                case "name":
                    return _registry.LookupType(name: "Text") ?? ErrorTypeInfo.Instance;
                case "id":
                    return _registry.LookupType(name: "U64") ?? ErrorTypeInfo.Instance;
                case "is_secret":
                case "is_routine":
                case "is_inert":
                    return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
                case "value":
                    // caseof `c.value` — a choice's S32 discriminant / a flags member's U64 bit. Only
                    // ever spliced (`${c.value}`, deferred); type leniently as S32 for a bare reference.
                    return _registry.LookupType(name: "S32") ?? ErrorTypeInfo.Instance;
                case "type_id":
                    // branchof `m.type_id` — the arm type's stable id (U64), used by variant diagnose.
                    return _registry.LookupType(name: "U64") ?? ErrorTypeInfo.Instance;
                case "type":
                    // `${m.type}` in EXPRESSION position — the member/arm type as a comptime typewise
                    // receiver (e.g. `${m.type}.data_size()` / `.type_id()`, or a column-buffer size in a
                    // SoA memberRoutine). Deferred like the type/pattern-position splice: the real type only
                    // exists at monomorphization, so a bare projection types leniently and the static
                    // call on it is re-resolved on the folded concrete type post-monomorph.
                    return ErrorTypeInfo.Instance;
                default:
                    ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                        message:
                        $"Comptime expand handle has no projection '{member.MemberName}'. Available: 'name' (Text), 'id' (U64), 'is_secret'/'is_routine' (Bool), 'value' (caseof).",
                        location: member.Location);
                    return ErrorTypeInfo.Instance;
            }
        }

        // The receiver already failed to resolve (its own error was reported). A follow-on
        // "Type '<error>' does not have a member ..." is pure cascade noise — bail quietly.
        if (objectType is ErrorTypeInfo)
        {
            return ErrorTypeInfo.Instance;
        }

        // Suflae flow typing: dereferencing (member access / memberRoutine call) a possibly-none entity
        // reference is rejected until it has been null-checked. Covers both a nullable local/param
        // (`x.field` on an unchecked `x: E?`) and a nullable field-chain (`a.b.c` where `b: E?`) — a
        // field read is never flow-narrowed (Kotlin doesn't smart-cast mutable fields either), so it
        // must always be bound to a local and checked there.
        if (_registry.Language == Language.Suflae && IsNullableEntityRead(expr: member.Object))
        {
            string receiver = member.Object is IdentifierExpression idRecv
                ? $"'{idRecv.Name}'"
                : member.Object is MemberExpression mRecv
                    ? $"'{mRecv.MemberName}'"
                    : "the value";
            string hint = member.Object is IdentifierExpression idHint
                ? $"Null-check it first (e.g. 'if {idHint.Name} isnot None' or 'if {idHint.Name} is None: return')."
                : "Bind it to a local and null-check that local first (e.g. 'var v = …' then 'if v isnot None').";
            ReportError(code: SemanticDiagnosticCode.NullableEntityDeref,
                message:
                $"Cannot access member '{member.MemberName}' on possibly-none entity {receiver}. {hint}",
                location: member.Location);
        }

        bool hasTransparentTarget = TryGetTransparentProtocolTarget(type: objectType,
            targetType: out TypeSymbol lookupType);

        // Look up the member variable/property on the type
        if (lookupType is RecordTypeInfo record)
        {
            MemberVariableInfo? memberVariable =
                record.LookupMemberVariable(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                // Validate member variable access (read access)
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }

            // Wrapper type forwarding for record-based wrappers (Viewing[T], Modifying[T], etc.)
            if (IsWrapperType(type: lookupType))
            {
                MemberVariableInfo? innerMemberVariable =
                    LookupMemberVariableOnWrapperInnerType(wrapperType: lookupType,
                        memberVariableName: member.MemberName);
                if (innerMemberVariable != null)
                {
                    ValidateMemberVariableAccess(memberVariable: innerMemberVariable,
                        isWrite: false,
                        accessLocation: member.Location);
                    return innerMemberVariable.Type;
                }

                RoutineInfo? innerMemberRoutine =
                    TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                        memberRoutineName: member.MemberName, isFailable: false)
                    ?? _registry.LookupMemberRoutine(type: lookupType,
                        memberRoutineName: member.MemberName);
                if (innerMemberRoutine != null)
                {
                    ValidateReadOnlyWrapperMemberRoutineAccess(wrapperType: lookupType,
                        memberRoutine: innerMemberRoutine,
                        location: member.Location);
                    ValidateRoutineAccess(routine: innerMemberRoutine, accessLocation: member.Location);
                    return innerMemberRoutine.ReturnType ??
                           _registry.LookupType(name: "None") ?? ErrorTypeInfo.Instance;
                }
            }
        }
        else if (lookupType is TupleTypeInfo tupleType)
        {
            MemberVariableInfo? memberVariable =
                tupleType.GetField(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                return memberVariable.Type;
            }
        }
        else if (lookupType is EntityTypeInfo entity)
        {
            MemberVariableInfo? memberVariable =
                entity.LookupMemberVariable(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                // Validate member variable access (read access)
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }
        }
        else if (lookupType is CrashableTypeInfo crashable)
        {
            MemberVariableInfo? memberVariable =
                crashable.LookupMemberVariable(memberVariableName: member.MemberName);
            if (memberVariable != null)
            {
                ValidateMemberVariableAccess(memberVariable: memberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return memberVariable.Type;
            }
        }
        // Wrapper type forwarding: Viewing<T>, Modifying<T>, Guarded<T>, etc.
        else if (IsWrapperType(type: lookupType))
        {
            // Try to forward member variable access to the inner type
            MemberVariableInfo? innerMemberVariable =
                LookupMemberVariableOnWrapperInnerType(wrapperType: lookupType,
                    memberVariableName: member.MemberName);
            if (innerMemberVariable != null)
            {
                // Validate member variable access on the inner type
                ValidateMemberVariableAccess(memberVariable: innerMemberVariable,
                    isWrite: false,
                    accessLocation: member.Location);
                return innerMemberVariable.Type;
            }

            // Try to forward memberRoutine access to the inner type via Phase D synthesized forwarders
            RoutineInfo? innerMemberRoutine =
                TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                    memberRoutineName: member.MemberName, isFailable: false)
                ?? _registry.LookupMemberRoutine(type: lookupType, memberRoutineName: member.MemberName);
            if (innerMemberRoutine != null)
            {
                // Validate read-only wrapper restrictions
                ValidateReadOnlyWrapperMemberRoutineAccess(wrapperType: lookupType,
                    memberRoutine: innerMemberRoutine,
                    location: member.Location);
                // Validate memberRoutine access
                ValidateRoutineAccess(routine: innerMemberRoutine, accessLocation: member.Location);
                // Return type is None if not specified
                return innerMemberRoutine.ReturnType ??
                       _registry.LookupType(name: "None") ?? ErrorTypeInfo.Instance;
            }
        }

        // Choice case member access: Color.RED -> ChoiceTypeInfo
        if (lookupType is ChoiceTypeInfo choice)
        {
            ChoiceCaseInfo? caseInfo =
                choice.Cases.FirstOrDefault(predicate: c => c.Name == member.MemberName);
            if (caseInfo != null)
            {
                return choice; // Color.RED has type Color
            }

            // Fall through to memberRoutine lookup — choice types can have memberRoutines
        }

        // Flags member access: Permissions.READ -> FlagsTypeInfo
        if (lookupType is FlagsTypeInfo flags)
        {
            FlagsMemberInfo? memberInfo =
                flags.Members.FirstOrDefault(predicate: m => m.Name == member.MemberName);
            if (memberInfo != null)
            {
                return flags; // Permissions.READ has type Permissions
            }

            // Fall through to memberRoutine lookup — flags types can have builder service memberRoutines
        }

        // Could be a member-routine reference - use LookupMemberRoutine which handles generic resolutions.
        // MemberName is always bare; failability is carried structurally in member.IsFailable.
        string lookupName = member.MemberName;
        RoutineInfo? memberRoutine = _registry.LookupMemberRoutine(type: lookupType, memberRoutineName: lookupName);
        if (memberRoutine != null)
        {
            // A BARE member access (`x.name`, no `()`) reads a member VARIABLE — it must not silently
            // invoke a zero-arg memberRoutine. (memberRoutine calls `x.name()` are resolved in AnalyzeCallExpression,
            // which never routes through here.) Auto-calling masked real bugs: e.g. after a record drops
            // a `sign` field but keeps a `sign()` accessor, `var s = w.sign` typechecked as the call
            // result here, so validate-stdlib passed code that only failed at codegen
            // ("Member variable 'sign' not found"). Keep `.name` (field) and `.name()` (call) distinct.
            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message:
                $"'{lookupName}' is a member routine on '{objectType.Name}', not a member variable. " +
                $"Bare `.{lookupName}` reads a member variable; call the member routine as `{member.MemberName}()`.",
                location: member.Location);
            return ErrorTypeInfo.Instance;
        }

        // For-loop destructuring lowering produces item0, item1, ... accesses on the element type.
        // Currently only Tuple[...] supports destructuring. Record breakdown is planned for the future.
        // When the element type is not a tuple, this means the user wrote `for (a, b) in non_tuple`.
        if (lookupType is not TupleTypeInfo &&
            System.Text.RegularExpressions.Regex.IsMatch(input: member.MemberName,
                pattern: @"^item\d+$"))
        {
            ReportError(code: SemanticDiagnosticCode.DestructuringArityMismatch,
                message:
                $"Cannot destructure: type '{objectType.Name}' is not a tuple and does not support tuple destructuring.",
                location: member.Location);
        }
        else
        {
            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message:
                $"Type '{objectType.Name}' does not have a member '{member.MemberName}'.{DidYouMean(target: member.MemberName, candidates: MemberSuggestionCandidates(type: lookupType))}",
                location: member.Location);
        }
        return ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeOptionalMemberExpression(OptionalMemberExpression optMember)
    {
        // Analyze the object expression to get its type
        AnalyzeExpression(expression: optMember.Object);

        // Delegate to regular member analysis for the property lookup
        // The result is wrapped in Maybe[T] since the access may produce none
        var regularMember = new MemberExpression(Object: optMember.Object,
            MemberName: optMember.MemberName,
            Location: optMember.Location);
        TypeSymbol memberType = AnalyzeMemberExpression(member: regularMember);

        return memberType;
    }

    /// <summary>
    /// Resolves the index parameter type of a `getitem` routine for a given lookup type, with
    /// owner generic parameters substituted. Returns null when the routine or parameter is missing.
    /// `me` is implicit and not in <see cref="RoutineInfo.Parameters"/>; the index is at index 0.
    /// </summary>
    private TypeSymbol? ResolveIndexParameterType(RoutineInfo? getItem, TypeSymbol lookupType)
    {
        if (getItem is not { Parameters.Count: >= 1 }) return null;
        TypeSymbol paramType = getItem.Parameters[index: 0].Type;
        paramType = SubstituteOwnerGenerics(paramType: paramType, lookupType: lookupType,
            ownerType: getItem.OwnerType);

        // The index param is frequently a by-reference marker wrapper — `Dict.getitem!(key:
        // Accessing[K])` / `Controlling[K]` — the `refer`/`control` coercion the container declares on
        // its key. That wrapper is TRANSPARENT: the caller passes a bare `K`. Unwrap it to the inner key
        // type so a bare integer key literal (`d[1]`) conforms to e.g. S64 instead of stalling at the
        // Suflae `Integer` default (RF escapes this only because its default already IS S64). Inferring
        // the key type through the coercion wrapper is the compiler's job.
        if (paramType.TypeArguments is { Count: >= 1 } referArgs &&
            GetTypeBaseName(type: paramType) is Compiler.Resolution.RuntimeContract.Accessing
                or Compiler.Resolution.RuntimeContract.Controlling)
        {
            paramType = referArgs[index: 0];
        }

        return paramType;
    }

    private TypeSymbol? SubstituteOwnerGenerics(TypeSymbol paramType, TypeSymbol lookupType,
        TypeSymbol? ownerType)
    {
        if (lookupType.TypeArguments is not { Count: > 0 }) return paramType;

        TypeSymbol? lookupGenericDef = GetGenericDefinition(resolution: lookupType);
        List<string>? ownerGenericParams = lookupGenericDef?.GenericParameters
            ?? ownerType?.GenericParameters;
        if (ownerGenericParams is not { Count: > 0 }) return paramType;

        var substitutions = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < ownerGenericParams.Count && i < lookupType.TypeArguments.Count; i++)
        {
            substitutions[key: ownerGenericParams[index: i]] = lookupType.TypeArguments[index: i];
        }
        return substitutions.Count > 0
            ? SubstituteWithMapping(type: paramType, substitutions: substitutions)
            : paramType;
    }

    /// <summary>
    /// The <c>Range[U64]</c> resolution — the type an index-range slice (<c>s[a til b]</c>) is forced
    /// to, since collection indices are always <c>U64</c>. Returns null if <c>Range</c>/<c>U64</c> are
    /// not registered (should not happen with the stdlib loaded).
    /// </summary>
    private TypeSymbol? MakeRangeU64Type()
    {
        TypeInfo? rangeDef = _registry.LookupType(name: "Range");
        TypeInfo? u64 = _registry.LookupType(name: "U64");
        return rangeDef != null && u64 != null
            ? _registry.GetOrCreateResolution(genericDef: rangeDef, typeArguments: [u64])
            : null;
    }

    private TypeSymbol AnalyzeIndexExpression(IndexExpression index) // NOSONAR S3776
    {
        // Type-as-value generic instantiation: when the object is a bare type name (no
        // shadowing variable) referring to a generic type, reinterpret the brackets as
        // generic-arg syntax — `NumericSumAdd[T].identity_lazy()` should produce the
        // resolved type `NumericSumAdd[T]`, not run getitem on the gen-def.
        if (index.Object is IdentifierExpression typeRefId &&
            _registry.LookupVariable(name: typeRefId.Name) == null &&
            LookupTypeWithImports(name: typeRefId.Name) is { GenericParameters.Count: > 0 } typeRef)
        {
            var typeArgs = new List<TypeSymbol>();
            List<Expression> argExprs = index.Index is TupleLiteralExpression tup
                ? tup.Elements
                : [index.Index];
            foreach (Expression argExpr in argExprs)
            {
                TypeSymbol argType = argExpr switch
                {
                    IdentifierExpression argId when IsGenericParameter(name: argId.Name)
                        => new GenericParameterTypeInfo(name: argId.Name),
                    IdentifierExpression argId when LookupTypeWithImports(name: argId.Name) is { } t
                        => t,
                    _ => AnalyzeExpression(expression: argExpr)
                };
                typeArgs.Add(item: argType);
            }
            if (typeArgs.Count == typeRef.GenericParameters.Count)
            {
                return _registry.GetOrCreateResolution(genericDef: typeRef,
                    typeArguments: typeArgs);
            }
        }

        TypeSymbol objectType = AnalyzeExpression(expression: index.Object);
        TryGetTransparentProtocolTarget(type: objectType, targetType: out TypeSymbol lookupType);

        // A slice `text[a til b]` — a RangeExpression index — binds to the `getitem(range: Range[U64])`
        // overload (returning the sub-collection), NOT the scalar `getitem(index)`. An index range is
        // ALWAYS U64, so analyze it with `Range[U64]` expected — bare (`s[0 til 5]`) and explicit
        // (`s[0u64 til 5u64]`) both become Range[U64] — then bind the slice overload by that arg type.
        // SA's type then matches what OperatorLoweringPass lowers to; otherwise SA types the slice as
        // the scalar element and codegen emits a Text, tripping an LLVM type mismatch. Falls through
        // to the scalar lookup below.
        RoutineInfo? getItem = null;
        if (index.Index is RangeExpression)
        {
            TypeSymbol? rangeU64 = MakeRangeU64Type();
            if (rangeU64 != null)
            {
                AnalyzeExpression(expression: index.Index, expectedType: rangeU64);
                getItem = _registry.LookupMemberRoutineOverload(type: lookupType,
                    memberRoutineName: GetItemMemberRoutineName, argTypes: [rangeU64]);
            }
        }

        // Look for getitem memberRoutine — LookupMemberRoutine handles generic resolutions
        getItem ??= _registry.LookupMemberRoutine(type: lookupType, memberRoutineName: GetItemMemberRoutineName);
        // Try failable variant if non-failable not found
        if (getItem == null)
        {
            getItem = _registry.LookupMemberRoutine(type: lookupType, memberRoutineName: GetItemMemberRoutineName,
                isFailable: true);
        }
        // Phase D: synthesize a wrapper forwarder if still not found
        if (getItem == null && IsWrapperType(type: lookupType))
        {
            getItem = TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                memberRoutineName: GetItemMemberRoutineName, isFailable: false)
                ?? TrySynthesizeWrapperForwarder(wrapperType: lookupType,
                    memberRoutineName: GetItemMemberRoutineName, isFailable: true);
        }

        // Analyze the index expression with the indexer parameter type as expected type so
        // untyped integer literals (`arr[0]`) retype to U64/S64/etc. instead of defaulting to S64
        // and tripping S767 fixed-width-mixing diagnostics.
        TypeSymbol? indexExpectedType = ResolveIndexParameterType(getItem: getItem,
            lookupType: lookupType);
        AnalyzeExpression(expression: index.Index, expectedType: indexExpectedType);

        // Failability propagation: the resolved getitem may be `!` per its protocol contract
        // (e.g. Indexable.getitem!). A non-failable caller using `arr[i]` must propagate that.
        if (getItem is { IsFailable: true } && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(getItem);
        }

        if (getItem?.ReturnType != null)
        {
            TypeSymbol returnType = getItem.ReturnType;
            List<string>? ownerGenericParams = null;
            if (lookupType.TypeArguments is { Count: > 0 })
            {
                TypeSymbol? lookupGenericDef = GetGenericDefinition(resolution: lookupType);
                ownerGenericParams = lookupGenericDef?.GenericParameters ??
                                     getItem.OwnerType?.GenericParameters;
            }

            // Only substitute when `getitem` came from the GENERIC DEFINITION (its ReturnType is the
            // bare owner param, e.g. List[T]'s `T`). If it was resolved against the instantiated
            // owner, its ReturnType is ALREADY expressed in the resolution's type arguments —
            // re-substituting would double-apply. That double-application is silent for `List[S64]`
            // (S64 mentions no param) but corrupts `List[Box[T]]`: the owner's formal param name "T"
            // collides with the routine's own "T" inside the element `Box[T]`, yielding a wrongly
            // nested `Box[Box[T]]`. Guard on the owner carrying type arguments (= already resolved).
            bool memberRoutineAlreadyResolved = getItem.OwnerType is { TypeArguments.Count: > 0 };

            if (!memberRoutineAlreadyResolved &&
                lookupType.TypeArguments is { Count: > 0 } &&
                ownerGenericParams is { Count: > 0 })
            {
                var substitutions = new Dictionary<string, TypeSymbol>();
                for (int i = 0; i < ownerGenericParams.Count &&
                                i < lookupType.TypeArguments.Count; i++)
                {
                    substitutions[key: ownerGenericParams[index: i]] =
                        lookupType.TypeArguments[index: i];
                }

                if (substitutions.Count > 0)
                {
                    returnType = SubstituteWithMapping(type: returnType,
                        substitutions: substitutions);
                }
            }

            return returnType;
        }

        // No `getitem` resolved. If the lookup type is fully concrete (no unresolved generic
        // parameters), it genuinely does not support indexing — report it cleanly here rather
        // than letting `arr[i]` slip through to codegen, which would crash with
        // "reached codegen ... but no resolved member routine". Types that removed their `getitem`
        // (e.g. SortedList, replaced by the named `get_by_rank!`) land here.
        if (getItem == null && !ContainsUnresolvedTypeParameter(type: lookupType))
        {
            ReportError(code: SemanticDiagnosticCode.TypeNotIndexable,
                message:
                $"Type '{lookupType.Name}' does not support indexing with '[]' (no 'getitem' routine).",
                location: index.Location);
            return ErrorTypeInfo.Instance;
        }

        // For generic types like List<T> whose `getitem` resolves only after monomorphization,
        // return the element type.
        if (lookupType.TypeArguments is { Count: > 0 })
        {
            return lookupType.TypeArguments[index: 0];
        }

        return ErrorTypeInfo.Instance;
    }

    /// <summary>
    /// True when <paramref name="type"/> is or contains an unresolved generic parameter,
    /// protocol self-type, or error type — cases where memberRoutine resolution may legitimately
    /// complete only after monomorphization, so a missing routine is not yet a hard error.
    /// </summary>
    private static bool ContainsUnresolvedTypeParameter(TypeSymbol type)
    {
        if (type is GenericParameterTypeInfo or ProtocolSelfTypeInfo or ErrorTypeInfo)
        {
            return true;
        }

        return type.TypeArguments is { Count: > 0 } args &&
               args.Any(predicate: ContainsUnresolvedTypeParameter);
    }

    private TypeSymbol AnalyzeConditionalExpression(ConditionalExpression cond)
    {
        // #145: Track nesting depth for deep conditional warning
        _conditionalNestingDepth++;
        if (_conditionalNestingDepth > 2)
        {
            ReportWarning(code: SemanticWarningCode.NestedConditionalExpression,
                message:
                "Deeply nested conditional expression. Consider using 'when' for readability.",
                location: cond.Location);
        }

        TypeSymbol conditionType = AnalyzeExpression(expression: cond.Condition);

        if (!IsBoolType(type: conditionType))
        {
            ReportError(code: SemanticDiagnosticCode.ConditionalNotBool,
                message:
                $"Conditional expression requires a boolean condition, got '{conditionType.Name}'.",
                location: cond.Condition.Location);
        }

        TypeSymbol trueType = AnalyzeExpression(expression: cond.TrueExpression);
        TypeSymbol falseType = AnalyzeExpression(expression: cond.FalseExpression);

        // Both branches must be compatible
        if (!IsAssignableTo(source: trueType, target: falseType) &&
            !IsAssignableTo(source: falseType, target: trueType))
        {
            ReportError(code: SemanticDiagnosticCode.ConditionalBranchTypeMismatch,
                message:
                $"Conditional expression branches have incompatible types: '{trueType.Name}' and '{falseType.Name}'.",
                location: cond.Location);
        }

        _conditionalNestingDepth--;

        // Return the common type (for now, use the true branch type)
        return trueType;
    }

    private RoutineTypeInfo AnalyzeLambdaExpression(LambdaExpression lambda,
        TypeSymbol? expectedType = null)
    {
        // Collect variables from enclosing scope that might be captured
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables =
            _registry.GetAllVariablesInScope();
        // Collect only local (function-level) variables — these require 'given' to capture
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables =
            _registry.GetLocalScopeVariables();

        _registry.EnterScope(kind: ScopeKind.Function, name: "lambda");

        // Extract expected parameter types from context (e.g., Routine[(S64, S64), Bool])
        List<TypeSymbol>? expectedParamTypes = expectedType is RoutineTypeInfo rt
            ? rt.ParameterTypes
            : null;

        // Register lambda parameters and collect their types
        var parameterNames = new HashSet<string>();
        var parameterTypes = new List<TypeSymbol>();
        for (int pi = 0; pi < lambda.Parameters.Count; pi++)
        {
            Parameter param = lambda.Parameters[index: pi];
            TypeSymbol paramType;
            if (param.Type != null)
            {
                paramType = ResolveType(typeExpr: param.Type);
            }
            else if (expectedParamTypes != null && pi < expectedParamTypes.Count)
            {
                paramType = expectedParamTypes[index: pi];
            }
            else
            {
                // No annotation AND no typed context to infer from. RazorForge is statically typed with
                // no runtime routine lookup, so this parameter has no resolvable type — report it (else
                // it silently becomes ErrorTypeInfo, lifts to an `@[lambda]…(<error>,…)` symbol whose
                // body is never emitted, and blows up at the linker). Suflae is left as-is: its
                // open-world `Unknown` top + runtime dispatch will default un-inferable params to Unknown
                // (gated on that machinery being real — see [[cabi-callback-ffi]]/[[object-top-type]]).
                if (_registry.Language == Language.RazorForge)
                {
                    ReportError(code: SemanticDiagnosticCode.LambdaParameterTypeNotInferable,
                        message:
                        $"Cannot infer the type of lambda parameter '{param.Name}' — there is no type " +
                        "annotation and no typed target to infer from. Annotate it (e.g. " +
                        "`(a: S32, b: S32) => a + b`) or assign the lambda to a typed target " +
                        "(`var f: Routine[(S32, S32), S32] = …`).",
                        location: param.Location);
                }
                paramType = ErrorTypeInfo.Instance;
            }

            _registry.DeclareVariable(name: param.Name, type: paramType, location: param.Location);
            parameterNames.Add(item: param.Name);
            parameterTypes.Add(item: paramType);
        }

        // Analyze body and get return type
        TypeSymbol returnType = AnalyzeExpression(expression: lambda.Body);

        // Validate captured variables (RazorForge only)
        // Lambda bodies can reference variables from enclosing scope - these are captures
        ValidateLambdaCaptures(lambda: lambda,
            enclosingScopeVariables: enclosingScopeVariables,
            localScopeVariables: localScopeVariables,
            parameterNames: parameterNames);

        _registry.ExitScope();

        // Create a proper function type: (ParamTypes) -> ReturnType
        return _registry.GetOrCreateRoutineType(parameterTypes: parameterTypes,
            returnType: returnType,
            isFailable: false);
    }

    /// <summary>
    /// Validates that lambda captures don't include forbidden types and that all
    /// local-scope captures are declared in the 'given' clause (RazorForge only).
    /// </summary>
    /// <param name="lambda">The lambda expression being analyzed.</param>
    /// <param name="enclosingScopeVariables">All variables available in the enclosing scope.</param>
    /// <param name="localScopeVariables">Variables from local (function-level) scopes only — require 'given'.</param>
    /// <param name="parameterNames">Names of lambda parameters (not captures).</param>
    private void ValidateLambdaCaptures(LambdaExpression lambda,
        IReadOnlyDictionary<string, VariableInfo> enclosingScopeVariables,
        IReadOnlyDictionary<string, VariableInfo> localScopeVariables,
        HashSet<string> parameterNames) // NOSONAR S3776
    {
        // Find all identifier expressions in the lambda body
        List<IdentifierExpression> identifiers = CollectIdentifiers(expression: lambda.Body);

        // Build set of given captures for quick lookup
        HashSet<string>? givenNames = lambda.Captures != null
            ? [..lambda.Captures]
            : null;

        foreach (IdentifierExpression id in identifiers)
        {
            // Skip if it's a parameter (not a capture)
            if (parameterNames.Contains(item: id.Name))
            {
                continue;
            }

            // Skip special identifiers
            if (id.Name is "me" or "none")
            {
                continue;
            }

            // Check if this identifier refers to a captured variable
            if (enclosingScopeVariables.TryGetValue(key: id.Name,
                    value: out VariableInfo? varInfo))
            {
                // Validate that the captured type is allowed
                ValidateCapturedType(varName: id.Name,
                    varType: varInfo.Type,
                    location: id.Location);

                // Check 'given' clause enforcement for local captures (RazorForge only)
                if (_registry.Language == Language.RazorForge &&
                    localScopeVariables.ContainsKey(key: id.Name) && !varInfo.IsPreset)
                {
                    if (givenNames == null)
                    {
                        // No 'given' clause — implicit capture of local variable
                        ReportError(code: SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                            message:
                            $"Lambda captures local variable '{id.Name}' without declaring it in 'given' clause. " +
                            "All local captures must be explicit via 'given'.",
                            location: id.Location);
                    }
                    else if (!givenNames.Contains(item: id.Name))
                    {
                        // Has 'given' clause but this variable isn't in it
                        ReportError(code: SemanticDiagnosticCode.LambdaCaptureWithoutGiven,
                            message:
                            $"Lambda captures local variable '{id.Name}' but it is not listed in the 'given' clause.",
                            location: id.Location);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Validates that a captured variable's type is allowed in lambda captures.
    /// </summary>
    /// <param name="varName">Name of the captured variable.</param>
    /// <param name="varType">Type of the captured variable.</param>
    /// <param name="location">Source location for error reporting.</param>
    private void ValidateCapturedType(string varName, TypeSymbol varType, SourceLocation location)
    {
        // Check for memory tokens (scope-bound, cannot be captured)
        if (IsMemoryToken(type: varType))
        {
            string tokenKind = GetMemoryTokenKind(type: varType);
            ReportError(code: SemanticDiagnosticCode.LambdaCaptureToken,
                message: $"Cannot capture '{varName}' of type '{tokenKind}' in lambda - " +
                         $"scope-bound tokens cannot escape their scope. " +
                         $"Use a handle type (Guarded[T] or Witnessed[T]) instead.",
                location: location);
            return;
        }

        // Check for raw entities (must use handles for capture)
        if (IsRawEntityType(type: varType))
        {
            ReportError(code: SemanticDiagnosticCode.LambdaCaptureRawEntity,
                message:
                $"Cannot capture raw entity '{varName}' of type '{varType.Name}' in lambda - " +
                $"raw entities cannot be captured. " +
                $"Wrap in a handle type (Guarded[T] or Witnessed[T]) before capturing.",
                location: location);
        }
    }

    /// <summary>
    /// Checks if a type is a raw entity (not wrapped in a handle or token).
    /// </summary>
    private static bool IsRawEntityType(TypeSymbol type)
    {
        // Raw entities are entity types that are not wrapped
        return type.Category == TypeCategory.Entity && !IsMemoryToken(type: type) &&
               !IsWrapperType(type: type) && !IsHijacked(type: type);
    }

    /// <summary>
    /// Collects all identifier expressions in an expression tree.
    /// </summary>
    private static List<IdentifierExpression> CollectIdentifiers(Expression expression)
    {
        var identifiers = new List<IdentifierExpression>();
        CollectIdentifiersRecursive(expression: expression, identifiers: identifiers);
        return identifiers;
    }

    /// <summary>
    /// Recursively collects identifier expressions.
    /// </summary>
    private static void CollectIdentifiersRecursive(Expression expression,
        List<IdentifierExpression> identifiers) // NOSONAR S3776
    {
        switch (expression)
        {
            case IdentifierExpression id:
                identifiers.Add(item: id);
                break;

            case CompoundAssignmentExpression compound:
                CollectIdentifiersRecursive(expression: compound.Target, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: compound.Value, identifiers: identifiers);
                break;

            case BinaryExpression binary:
                CollectIdentifiersRecursive(expression: binary.Left, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: binary.Right, identifiers: identifiers);
                break;

            case UnaryExpression unary:
                CollectIdentifiersRecursive(expression: unary.Operand, identifiers: identifiers);
                break;

            case StealExpression steal:
                CollectIdentifiersRecursive(expression: steal.Operand, identifiers: identifiers);
                break;

            case BackIndexExpression back:
                CollectIdentifiersRecursive(expression: back.Operand, identifiers: identifiers);
                break;

            case CallExpression call:
                CollectIdentifiersRecursive(expression: call.Callee, identifiers: identifiers);
                foreach (Expression arg in call.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }

                break;

            case MemberExpression member:
                CollectIdentifiersRecursive(expression: member.Object, identifiers: identifiers);
                break;

            case IndexExpression index:
                CollectIdentifiersRecursive(expression: index.Object, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: index.Index, identifiers: identifiers);
                break;

            case ConditionalExpression cond:
                CollectIdentifiersRecursive(expression: cond.Condition, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.TrueExpression,
                    identifiers: identifiers);
                CollectIdentifiersRecursive(expression: cond.FalseExpression,
                    identifiers: identifiers);
                break;

            case LambdaExpression:
                // Don't descend into nested lambdas - they have their own capture context
                break;

            case RangeExpression range:
                CollectIdentifiersRecursive(expression: range.Start, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: range.End, identifiers: identifiers);
                if (range.Step != null)
                {
                    CollectIdentifiersRecursive(expression: range.Step, identifiers: identifiers);
                }

                break;

            case CreatorExpression creator:
                foreach ((_, Expression value) in creator.MemberVariables)
                {
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }

                break;

            case ListLiteralExpression list:
                foreach (Expression elem in list.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }

                break;

            case SetLiteralExpression set:
                foreach (Expression elem in set.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }

                break;

            case DictLiteralExpression dict:
                foreach ((Expression key, Expression value) in dict.Pairs)
                {
                    CollectIdentifiersRecursive(expression: key, identifiers: identifiers);
                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }

                break;

            case TupleLiteralExpression tuple:
                foreach (Expression elem in tuple.Elements)
                {
                    CollectIdentifiersRecursive(expression: elem, identifiers: identifiers);
                }

                break;

            case BlockExpression block:
                CollectIdentifiersRecursive(expression: block.Value, identifiers: identifiers);
                break;

            case WithExpression with:
                CollectIdentifiersRecursive(expression: with.Base, identifiers: identifiers);
                foreach ((_, Expression? index, Expression value) in with.Updates)
                {
                    if (index != null)
                    {
                        CollectIdentifiersRecursive(expression: index, identifiers: identifiers);
                    }

                    CollectIdentifiersRecursive(expression: value, identifiers: identifiers);
                }

                break;

            case IsPatternExpression isPat:
                CollectIdentifiersRecursive(expression: isPat.Expression,
                    identifiers: identifiers);
                break;

            case NamedArgumentExpression named:
                CollectIdentifiersRecursive(expression: named.Value, identifiers: identifiers);
                break;

            case DictEntryLiteralExpression dictEntry:
                CollectIdentifiersRecursive(expression: dictEntry.Key, identifiers: identifiers);
                CollectIdentifiersRecursive(expression: dictEntry.Value, identifiers: identifiers);
                break;

            case GenericMemberRoutineCallExpression generic:
                CollectIdentifiersRecursive(expression: generic.Object, identifiers: identifiers);
                foreach (Expression arg in generic.Arguments)
                {
                    CollectIdentifiersRecursive(expression: arg, identifiers: identifiers);
                }

                break;

            case GenericMemberExpression genericMember:
                CollectIdentifiersRecursive(expression: genericMember.Object,
                    identifiers: identifiers);
                break;

            case TypeConversionExpression conv:
                CollectIdentifiersRecursive(expression: conv.Expression, identifiers: identifiers);
                break;

            case ChainedComparisonExpression chain:
                foreach (Expression operand in chain.Operands)
                {
                    CollectIdentifiersRecursive(expression: operand, identifiers: identifiers);
                }

                break;

            // Literal expressions and type expressions have no identifiers to collect
            case LiteralExpression:
            case TypeExpression:
                break;
        }
    }

    private TypeSymbol AnalyzeRangeExpression(RangeExpression range, TypeSymbol? expectedType = null)
    {
        // When the range flows into a `Range[T]` context (e.g. a subscript slice `s[a til b]`, where
        // the element type is forced to U64), propagate T as the endpoint expected type so untyped
        // literals (`0 til 5`) retype to it instead of defaulting to S32. The returned Range element
        // type is the start type, so this also makes the whole range resolve to `Range[T]`.
        TypeSymbol? endpointExpected =
            expectedType is { TypeArguments: { Count: 1 } expArgs } && expectedType.BareName == "Range"
                ? expArgs[index: 0]
                : null;

        TypeSymbol startType = AnalyzeExpression(expression: range.Start, expectedType: endpointExpected);
        TypeSymbol endType = AnalyzeExpression(expression: range.End, expectedType: endpointExpected);

        if (range.Step != null)
        {
            AnalyzeExpression(expression: range.Step, expectedType: endpointExpected);
        }

        // BackIndex (^n) endpoints are valid ONLY inside a subscript slice (`s[a til ^0]`), where the
        // element type is forced (U64) — a `^n` there lowers to `count - n` (see OperatorLoweringPass).
        // `inSubscript` is exactly "an expected Range[T] flowed in", which only the subscript path does.
        // Outside a subscript a `^n` bound is meaningless (there is no collection to count from).
        bool inSubscript = endpointExpected != null;
        bool startIsBack = range.Start is BackIndexExpression;
        bool endIsBack = range.End is BackIndexExpression;
        if ((startIsBack || endIsBack) && !inSubscript)
        {
            ReportError(code: SemanticDiagnosticCode.BackIndexOutsideSubscript,
                message:
                "BackIndex (^n) can only be used in a subscript [^n] or slice [a til b], not a bare range.",
                location: (startIsBack ? range.Start : range.End).Location);
        }

        // Range bound types must be numeric — const-generic parameters (e.g. `N is U64`) and numeric-
        // constrained generic params qualify (they hold a numeric value at each monomorphization). A
        // BackIndex bound is exempt: it is a `^n` marker that resolves to a numeric `count - n`.
        bool startOk = startIsBack || IsNumericType(type: startType) || IsNumericGenericParam(type: startType);
        bool endOk = endIsBack || IsNumericType(type: endType) || IsNumericGenericParam(type: endType);
        if (!startOk || !endOk)
        {
            ReportError(code: SemanticDiagnosticCode.RangeBoundsNotNumeric,
                message: "Range bounds must be numeric types.",
                location: range.Location);
        }

        // Element type: a subscript forces it (U64); otherwise the non-BackIndex start (or end) drives
        // it. Return the resolved `Range[T]`.
        TypeSymbol elementType = endpointExpected ?? (startIsBack ? endType : startType);
        TypeInfo? rangeGenericDef = _registry.LookupType(name: "Range");
        if (rangeGenericDef != null && elementType is not ErrorTypeInfo)
        {
            return _registry.GetOrCreateResolution(genericDef: rangeGenericDef,
                typeArguments: new List<TypeInfo> { elementType });
        }

        return rangeGenericDef ?? ErrorTypeInfo.Instance;
    }

    private TypeSymbol AnalyzeCreatorExpression(CreatorExpression creator)
    {
        TypeSymbol? type = LookupTypeWithImports(name: creator.TypeName);
        if (type == null)
        {
            ReportError(code: SemanticDiagnosticCode.UnknownType,
                message:
                $"Unknown type '{creator.TypeName}'. Check the spelling, and make sure the module that defines it is imported.{DidYouMean(target: creator.TypeName, candidates: TypeSuggestionCandidates())}",
                location: creator.Location);
            return ErrorTypeInfo.Instance;
        }

        // Handle generic type arguments
        if (creator.TypeArguments is { Count: > 0 })
        {
            var typeArgs = new List<TypeSymbol>();
            foreach (TypeExpression typeArg in creator.TypeArguments)
            {
                typeArgs.Add(item: ResolveType(typeExpr: typeArg));
            }

            // Arity guard: a wrong count of explicit type args (e.g. `Guarded[ReadOnly](from: n)` — one arg
            // for a two-param `Guarded[T, P]`) must be a clean diagnostic. Without the early return,
            // GetOrCreateResolution → RecordTypeInfo.CreateInstance zips params↔args and crashes.
            if (type.GenericParameters is { } creatorParams && creatorParams.Count != typeArgs.Count)
            {
                ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                    message:
                    $"Type '{type.Name}' expects {creatorParams.Count} type arguments, got {typeArgs.Count}.",
                    location: creator.Location);
                return ErrorTypeInfo.Instance;
            }

            ValidateGenericConstraints(genericDef: type,
                typeArgs: typeArgs,
                location: creator.Location);
            type = _registry.GetOrCreateResolution(genericDef: type, typeArguments: typeArgs);
        }

        creator.ConstructedType = type;
        creator.LoweringKind = ClassifyConstruction(type: type, isCollectionLiteral: false);

        // Propagate the in-flight bit from the resolved type's implicit constructor.
        // If TryRouteCreatorToCreate routes through a user-declared `create` below,
        // it overrides this with that routine's IsInFlightReturn.
        creator.IsInFlight = type.ImplicitConstructorReturnsInFlight;

        // Named-arg → create routing: if the provided names don't match any field but DO match
        // a `create(named:)` overload's parameter names, dispatch through that creator instead
        // of doing inline field-init. Lets `SegTreeLazy[..](size: 10, alg: alg)` route to
        // `SegTreeLazy.create(size:, alg:)` even though `size`/`alg` aren't field names.
        if (creator.MemberVariables.Count > 0 &&
            TryRouteCreatorToCreate(type: type, creator: creator))
        {
            return type;
        }

        // Validate member variable initializers
        ValidateCreatorMemberVariables(type: type,
            memberVariables: creator.MemberVariables,
            location: creator.Location);

        return type;
    }

    /// <summary>
    /// Tries to route a CreatorExpression's named args to a matching `create(named:)` overload.
    /// Returns true if a matching overload was found and resolved (caller should skip field-init
    /// validation). The match requires that every provided arg name corresponds to a parameter
    /// name on some `create` overload, AND that at least one provided name is NOT a field name
    /// (field-init pattern is still preferred when all names are fields). Routing is purely
    /// name-based; arg analysis (with proper expected types) happens via the standard pipeline
    /// later when codegen evaluates the call.
    /// </summary>
    private bool TryRouteCreatorToCreate(TypeSymbol type, CreatorExpression creator)
    {
        var providedNames = creator.MemberVariables.Select(selector: mv => mv.Name).ToList();

        // Name-based match against create overloads. Iterate type's memberRoutines looking for ones
        // named `create` whose parameter names match the provided set exactly. If multiple
        // overloads share the same param names (e.g. `S64.create(from: S8)` vs
        // `S64.create(from: ComparisonSign)`), bail out — disambiguation by arg type is the
        // job of the legacy path and we don't want to silently pick the wrong overload.
        // Use CollectMemberRoutineCandidates: it walks the generic definition for
        // generic resolutions (e.g. List[V] → List[T]) and runs SubstituteMemberRoutineForOwner
        // so parameter types come back resolved to the receiver's concrete type args.
        var providedNameSet = new HashSet<string>(collection: providedNames);
        var nameMatches = new List<RoutineInfo>();
        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: type, memberRoutineName: "create",
            candidates: candidates);
        // Also pull the concrete type's own routines directly — CollectMemberRoutineCandidates
        // can miss a user-declared `create` on an entity, while GetMemberRoutinesForType returns it
        // (this is how the entity `destroy` resolves correctly elsewhere). Failable creators keep the
        // bare `create` Name (IsFailable is a structured flag), so one name matches both.
        candidates.AddRange(collection: _registry.GetMemberRoutinesForType(type: type)
            .Where(predicate: m => m.Name is "create"));
        foreach (RoutineInfo m in candidates)
        {
            if (m.Parameters.Count != creator.MemberVariables.Count) continue;

            var pNames = new HashSet<string>(
                collection: m.Parameters.Select(selector: p => p.Name));
            if (pNames.SetEquals(other: providedNameSet))
            {
                nameMatches.Add(item: m);
            }
        }

        // Prefer a user-defined (non-synthesized) `create`. Entities/records also get an
        // auto-synthesized all-args `create` (AutoWiredRegistrationPass) whose only job is inline
        // field-init ("stuffing") — when that's the sole match we fall through to inline
        // construction below. A user `create` with the same signature as the all-args creator
        // (e.g. `Resource.create(tag:)` where `tag` is the only field) is the real constructor
        // and must be called so its body/side-effects run.
        // Dedupe by registry key — CollectMemberRoutineCandidates can surface the same overload
        // through more than one path (owner table + protocol/universal walk), which would make a
        // single user `create` look ambiguous and wrongly fall back to inline construction.
        var userMatches = nameMatches.Where(predicate: m => !m.IsSynthesized)
            .GroupBy(keySelector: m => m.RegistryKey)
            .Select(selector: g => g.First())
            .ToList();

        // `T(...)` written *inside* T's own `create` is the field-init base case ONLY when it
        // resolves back to the SAME `create` we are compiling (genuine self-recursion). A call to
        // a *different* `create` overload (e.g. `F128(from: hi)` -> `create(from: U64)` inside
        // `create(from: U128)`) is an ordinary conversion and must route to that overload;
        // otherwise codegen falls back to inline field-init and mis-lowers bit-carrier types like
        // F128 to a raw integer reinterpret of the IEEE storage.
        bool insideOwnCreate = _currentRoutine is { Name: "create" } currentCreate
            && currentCreate.OwnerType != null
            && (currentCreate.OwnerType.FullName == type.FullName
                || currentCreate.OwnerType.Name == type.Name)
            && userMatches.Count == 1
            && ReferenceEquals(objA: userMatches[index: 0], objB: currentCreate);

        // Route through a unique user-defined `create` (so its body runs). Otherwise — no
        // user match, ambiguous user overloads, or self-reference inside the creator — fall back
        // to inline field-init / standard validation.
        if (userMatches.Count != 1 || insideOwnCreate)
        {
            return false;
        }

        RoutineInfo match = userMatches[index: 0];

        // Analyze each arg with the matching parameter's type as the expected type so integer
        // literals coerce correctly (e.g. `size: 10` → S8 if the param is S8, not default S32).
        var paramByName = match.Parameters.ToDictionary(keySelector: p => p.Name);
        foreach ((string argName, Expression val) in creator.MemberVariables)
        {
            TypeSymbol? expected = paramByName.TryGetValue(key: argName,
                value: out ParameterInfo? p)
                ? p.Type
                : null;
            AnalyzeExpression(expression: val, expectedType: expected);
        }

        creator.ResolvedCreatorRoutine = match;
        creator.LoweringKind = CallLoweringKind.TypeConstructor;
        creator.IsInFlight = match.IsInFlightReturn;

        if (match.IsFailable && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(match);
        }

        return true;
    }

    /// <summary>
    /// Validates creator member variable initializers:
    /// - Each provided member variable exists on the type
    /// - Value types are assignable to member variable types
    /// - No duplicate member variable assignments
    /// - All required member variables are provided
    /// </summary>
    private void ValidateCreatorMemberVariables(TypeSymbol type,
        List<(string Name, Expression Value)> memberVariables, SourceLocation location)
    {
        // Get the type's member variables
        List<MemberVariableInfo>? typeMemberVariables = type switch
        {
            RecordTypeInfo record => record.MemberVariables,
            EntityTypeInfo entity => entity.MemberVariables,
            _ => null
        };

        if (typeMemberVariables == null)
        {
            if (memberVariables.Count > 0)
            {
                ReportError(code: SemanticDiagnosticCode.TypeNotMemberVariableInitializable,
                    message:
                    $"Type '{type.Name}' does not support member variable initialization.",
                    location: location);
            }

            return;
        }

        // Build a lookup for expected member variables
        var memberVariableLookup = new Dictionary<string, MemberVariableInfo>();
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            memberVariableLookup[key: memberVariable.Name] = memberVariable;
        }

        // Track which member variables have been provided (to detect duplicates and missing member variables)
        var providedMemberVariables = new HashSet<string>();

        // Validate each provided member variable
        foreach ((string memberVariableName, Expression value) in memberVariables)
        {
            // Check for duplicates
            if (!providedMemberVariables.Add(item: memberVariableName))
            {
                ReportError(code: SemanticDiagnosticCode.DuplicateMemberVariableInitializer,
                    message: $"Duplicate member variable initializer for '{memberVariableName}'.",
                    location: value.Location);
                continue;
            }

            // Check if member variable exists
            if (!memberVariableLookup.TryGetValue(key: memberVariableName,
                    value: out MemberVariableInfo? expectedMemberVariable))
            {
                ReportError(code: SemanticDiagnosticCode.MemberVariableNotFound,
                    message:
                    $"Type '{type.Name}' does not have a member variable named '{memberVariableName}'.",
                    location: value.Location);
                AnalyzeExpression(expression: value); // Still analyze the value
                continue;
            }

            // Analyze value with expected type for contextual inference
            TypeSymbol memberVariableType = expectedMemberVariable.Type;

            // For generic resolutions, substitute type parameters in member variable type
            if (type is { IsGenericResolution: true, TypeArguments: not null })
            {
                memberVariableType =
                    SubstituteTypeParameters(type: memberVariableType, genericType: type);
            }

            TypeSymbol valueType =
                AnalyzeExpression(expression: value, expectedType: memberVariableType);

            // Check type compatibility
            if (!IsAssignableTo(source: valueType, target: memberVariableType))
            {
                ReportError(code: SemanticDiagnosticCode.MemberVariableTypeMismatch,
                    message:
                    $"Cannot assign '{valueType.Name}' to member variable '{memberVariableName}' of type '{memberVariableType.Name}'.",
                    location: value.Location);
            }
        }

        // Check for missing required member variables (member variables without default values)
        foreach (MemberVariableInfo memberVariable in typeMemberVariables)
        {
            if (!providedMemberVariables.Contains(item: memberVariable.Name) &&
                !memberVariable.HasDefaultValue)
            {
                ReportError(code: SemanticDiagnosticCode.MissingRequiredMemberVariable,
                    message:
                    $"Missing required member variable '{memberVariable.Name}' in creator for '{type.Name}'.",
                    location: location);
            }
        }
    }
}
