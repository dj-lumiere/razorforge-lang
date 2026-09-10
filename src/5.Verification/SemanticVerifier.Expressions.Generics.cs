using Builder.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Verification;

public sealed partial class SemanticVerifier
{
    private TypeSymbol AnalyzeGenericMemberRoutineCallExpression(
        GenericMemberRoutineCallExpression generic)
    {
        TypeSymbol result = AnalyzeGenericMemberRoutineCallExpressionCore(generic: generic);
        // A generic call (`hollow[Integer]()`, `obj.method[T](..)`) resolves through a path distinct from
        // the plain-call analyzer, so the Suflae unsafe-call gate is applied here too on its resolved routine.
        EnforceSuflaeUnsafeCall(resolved: generic.ResolvedRoutine, location: generic.Location);
        return result;
    }

    private TypeSymbol AnalyzeGenericMemberRoutineCallExpressionCore(
        GenericMemberRoutineCallExpression generic)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: generic.Object);

        // Resolve type arguments
        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression typeArg in generic.TypeArguments)
        {
            typeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        if (generic.IsCollectionLiteral)
        {
            generic.LoweringKind = CallLoweringKind.CollectionConstruction;
        }

        // Check if this is a generic type constructor call (e.g., Hijacked[U8](addr))
        // The parser creates GenericMemberRoutineCallExpression for both Type[Args](args) and obj.MemberRoutine[Args](args).
        // A FAILABLE construction `Type![Args](args)` parses with a BARE memberRoutineName equal to the
        // type name plus the structured `IsMemoryOperation` failable flag — recognize it here and
        // route to the type's failable `create` overload (e.g. the auto-generated variant arm
        // extractor `Dict[Text, SerialValue].create!(from: sv)`).
        bool isFailableCtor = generic.IsMemoryOperation &&
                              generic.Object is IdentifierExpression fctorId &&
                              generic.MemberRoutineName == fctorId.Name;
        if (generic.Object is IdentifierExpression typeId && objectType is TypeSymbol
            {
                IsGenericDefinition: true
            } typeInfo && (typeId.Name == generic.MemberRoutineName || isFailableCtor))
        {
            return AnalyzeGenericTypeConstructorCall(generic: generic,
                typeId: typeId,
                typeSymbol: typeInfo,
                typeArgs: typeArgs);
        }

        // Look up the memberRoutine on receiver type — LookupMemberRoutine handles generic resolutions
        RoutineInfo? memberRoutine = _registry.LookupMemberRoutine(type: objectType,
            memberRoutineName: generic.MemberRoutineName);
        if (memberRoutine != null)
        {
            return AnalyzeGenericReceiverMemberRoutineCall(generic: generic,
                objectType: objectType,
                memberRoutine: memberRoutine,
                typeArgs: typeArgs);
        }

        // Standalone generic function call (e.g., ptrtoint[Point, Address](p), hijacked_none[T]())
        // The object is an identifier that resolves to a routine, not a type or variable
        if (generic.Object is IdentifierExpression funcId)
        {
            // LookupGenericOverload covers generic free routines, which register only in the
            // generic-overload index — without it an explicit `gen_id[T](...)` call to a generic
            // routine in another module fails to resolve (concrete free routines resolve fine).
            RoutineInfo? routine = _registry.LookupRoutine(fullName: funcId.Name) ??
                                   _registry.LookupRoutineByName(name: funcId.Name) ??
                                   _registry.LookupGenericOverload(name: funcId.Name,
                                       preferredArity: generic.Arguments.Count);
            if (routine != null)
            {
                return AnalyzeStandaloneGenericRoutineCall(generic: generic,
                    funcId: funcId,
                    routine: routine,
                    typeArgs: typeArgs);
            }
        }

        // Analyze arguments
        foreach (Expression arg in generic.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Analyzes a generic MEMBER-routine call on a receiver (<c>obj.select_many[U](...)</c>): aligns
    /// owner-level and member-level type arguments, monomorphizes <paramref name="memberRoutine"/>,
    /// analyzes the arguments, and returns the return type with <c>Me</c> / member-generic substitution
    /// applied.
    /// </summary>
    private TypeSymbol AnalyzeGenericReceiverMemberRoutineCall(
        GenericMemberRoutineCallExpression generic, TypeSymbol objectType,
        RoutineInfo memberRoutine, List<TypeSymbol> typeArgs)
    {
        generic.LoweringKind = ClassifyMemberRoutineCall(memberRoutine: memberRoutine);

        // Resolve the memberRoutine's generics from the explicit type arguments BEFORE analyzing the
        // arguments, so a lambda argument whose parameter binds a memberRoutine-level generic receives a
        // concrete expected type (e.g. `select_many[S64](transform: x => …)` types `x` as S64
        // instead of the unbound generic, which would collapse to <error> and cascade S503/S450/S505).
        if (memberRoutine.IsGenericDefinition)
        {
            // Owner-level generic params (e.g. T from Hijacked[T]) are bound by the receiver.
            // Compare typeArgs only against memberRoutine-level params (e.g. U from recast_as[U]).
            RoutineInfo? mono = MonomorphizeMemberRoutineWithTypeArgs(generic: generic,
                objectType: objectType,
                memberRoutine: memberRoutine,
                typeArgs: typeArgs);
            if (mono == null)
            {
                return ErrorTypeSymbol.Instance;
            }

            memberRoutine = mono;
        }

        // Variadic member routine (e.g. `List[T].create(elements...: T)` called as
        // `List[S32].create(1, 2, 3)`): pack the K trailing args into an Array[T, K] literal first.
        PackVariadicCallArgs(arguments: generic.Arguments,
            routine: memberRoutine,
            location: generic.Location);

        // Analyze arguments against the resolved memberRoutine (param types now concrete), so lambda
        // parameters bound to memberRoutine generics are typed correctly. AnalyzeCallArguments also binds
        // named/positional args and applies any remaining owner-generic substitution.
        AnalyzeCallArguments(routine: memberRoutine,
            arguments: generic.Arguments,
            location: generic.Location,
            callObjectType: objectType);

        ValidateExclusiveTokenUniqueness(arguments: generic.Arguments, location: generic.Location);

        generic.ResolvedRoutine = memberRoutine;
        generic.LoweringKind = ClassifyMemberRoutineCall(memberRoutine: memberRoutine);
        generic.IsInFlight = memberRoutine.IsInFlightReturn;

        if (memberRoutine.ReturnType == null)
        {
            return _registry.LookupType(name: "None") ?? ErrorTypeSymbol.Instance;
        }

        TypeSymbol returnType = memberRoutine.ReturnType;

        // Bind ProtocolSelf (`Me`) in the return type to the receiver. A protocol-extension memberRoutine
        // like `select_many[U] -> ?SelectManyIterator[T, U, Me]` leaves `Me` in its return; left
        // unbound it leaks into the per-implementer collector symbol (SelectManyIterator[.., Me].List)
        // and never resolves. Rebuild the resolution explicitly — a ProtocolSelf argument suppresses
        // the IsGenericResolution flag, so the generic substitution helpers below skip it.
        if (returnType.TypeArguments is { Count: > 0 } retArgs &&
            retArgs.Any(predicate: a => a is ProtocolSelfTypeSymbol || a.Name == "Me") &&
            GetGenericDefinition(resolution: returnType) is { } retDef)
        {
            var boundArgs = retArgs.Select(selector: a =>
                                        a is ProtocolSelfTypeSymbol || a.Name == "Me"
                                            ? objectType
                                            : a)
                                   .ToList();
            returnType =
                _registry.GetOrCreateResolution(genericDef: retDef, typeArguments: boundArgs);
        }

        // Substitute memberRoutine's own generic params (U from obtain_as[U]).
        return SubstituteGenericParamsInReturnType(returnType: returnType,
            memberRoutine: memberRoutine,
            typeArgs: typeArgs);
    }

    /// <summary>
    /// Monomorphizes a standalone generic free routine against the caller's explicit type arguments.
    /// Validates arity, builds the substitution map in <paramref name="typeSubs"/>, replaces
    /// <paramref name="routine"/> with the concrete resolution, and returns true on success.
    /// Reports a <c>WrongTypeArgumentCount</c> error and returns false when the arity does not match.
    /// </summary>
    private bool TryMonomorphizeFreeRoutine(GenericMemberRoutineCallExpression generic,
        ref RoutineInfo routine, List<TypeSymbol> typeArgs,
        out Dictionary<string, TypeSymbol>? typeSubs)
    {
        if (routine.GenericParameters == null || routine.GenericParameters.Count != typeArgs.Count)
        {
            ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"Routine '{routine.Name}' expects {routine.GenericParameters?.Count ?? 0} type arguments, got {typeArgs.Count}.",
                location: generic.Location);
            typeSubs = null;
            return false;
        }

        typeSubs = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < routine.GenericParameters.Count; i++)
        {
            if (typeArgs[index: i] is TypeSymbol concreteArg)
            {
                typeSubs[key: routine.GenericParameters[index: i]] = concreteArg;
            }
        }

        routine = _registry.GetOrCreateRoutineResolution(genericDef: routine,
            typeArguments: typeArgs.ToList());
        return true;
    }

    /// <summary>
    /// Monomorphizes a generic member routine against the caller's explicit type arguments, aligning
    /// owner-level and member-level parameters. Returns the concrete routine resolution, or null and
    /// reports a <c>WrongTypeArgumentCount</c> error when the arity does not match.
    /// </summary>
    private RoutineInfo? MonomorphizeMemberRoutineWithTypeArgs(
        GenericMemberRoutineCallExpression generic, TypeSymbol objectType,
        RoutineInfo memberRoutine, List<TypeSymbol> typeArgs)
    {
        HashSet<string> ownerGenericParamNames =
            GetOwnerGenericParameterNames(ownerType: objectType);
        List<string> memberRoutineOnlyParams = memberRoutine.GenericParameters
                                                           ?.Where(predicate: gp =>
                                                                 !ownerGenericParamNames.Contains(
                                                                     item: gp))
                                                            .ToList() ?? new List<string>();

        if (memberRoutineOnlyParams.Count != typeArgs.Count)
        {
            ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"member routine '{memberRoutine.Name}' expects {memberRoutineOnlyParams.Count} type arguments, got {typeArgs.Count}.",
                location: generic.Location);
            return null;
        }

        List<TypeSymbol> fullTypeArgs;
        if (memberRoutine.GenericParameters != null &&
            memberRoutine.GenericParameters.Count != typeArgs.Count)
        {
            fullTypeArgs = new List<TypeSymbol>(capacity: memberRoutine.GenericParameters.Count);
            int memberRoutineArgIdx = 0;
            Dictionary<string, TypeSymbol> ownerBindings =
                BuildOwnerBindingMap(ownerType: objectType);
            foreach (string paramName in memberRoutine.GenericParameters)
            {
                if (ownerGenericParamNames.Contains(item: paramName) &&
                    ownerBindings.TryGetValue(key: paramName, value: out TypeSymbol? ownerArg))
                {
                    fullTypeArgs.Add(item: ownerArg);
                }
                else if (memberRoutineArgIdx < typeArgs.Count)
                {
                    fullTypeArgs.Add(item: typeArgs[index: memberRoutineArgIdx++]);
                }
            }
        }
        else
        {
            fullTypeArgs = typeArgs.ToList();
        }

        return _registry.GetOrCreateRoutineResolution(genericDef: memberRoutine,
            typeArguments: fullTypeArgs);
    }

    /// <summary>
    /// Substitutes member-routine-level generic parameters in <paramref name="returnType"/> using
    /// the caller's <paramref name="typeArgs"/>. Handles both a bare generic parameter return and a
    /// generic resolution (e.g. <c>Hijacked[U]</c>) whose arguments contain parameters to substitute.
    /// Returns the substituted type, or <paramref name="returnType"/> unchanged when nothing matches.
    /// </summary>
    private TypeSymbol SubstituteGenericParamsInReturnType(TypeSymbol returnType,
        RoutineInfo memberRoutine, List<TypeSymbol> typeArgs)
    {
        if (memberRoutine.GenericParameters == null)
        {
            return returnType;
        }

        if (returnType is GenericParameterTypeSymbol)
        {
            int paramIndex = memberRoutine.GenericParameters
                                          .ToList()
                                          .IndexOf(item: returnType.Name);
            if (paramIndex >= 0 && paramIndex < typeArgs.Count &&
                typeArgs[index: paramIndex] is TypeSymbol resolved)
            {
                return resolved;
            }
        }

        if (returnType is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol? substituted = SubstituteGenericResolutionArgs(returnType: returnType,
                genericParams: memberRoutine.GenericParameters,
                typeArgs: typeArgs);
            if (substituted != null)
            {
                return substituted;
            }
        }

        return returnType;
    }

    /// <summary>
    /// Substitutes the type arguments of a generic resolution type (e.g. <c>Hijacked[U]</c>) using
    /// the member-routine generic parameter map, and returns a new resolution when any argument
    /// changed. Returns null when no substitution occurred.
    /// </summary>
    private TypeSymbol? SubstituteGenericResolutionArgs(TypeSymbol returnType,
        List<string> genericParams, List<TypeSymbol> typeArgs)
    {
        var substitutedArgs = new List<TypeSymbol>();
        bool anySubstituted = false;
        foreach (TypeSymbol typeArg in returnType.TypeArguments!)
        {
            int idx = genericParams.ToList()
                                   .IndexOf(item: typeArg.Name);
            if (idx >= 0 && idx < typeArgs.Count && typeArgs[index: idx] is TypeSymbol sub)
            {
                substitutedArgs.Add(item: sub);
                anySubstituted = true;
            }
            else
            {
                substitutedArgs.Add(item: typeArg);
            }
        }

        if (anySubstituted)
        {
            TypeSymbol? genericDef = GetGenericDefinition(resolution: returnType);
            if (genericDef != null)
            {
                return _registry.GetOrCreateResolution(genericDef: genericDef,
                    typeArguments: substitutedArgs);
            }
        }

        return null;
    }

    /// <summary>
    /// Analyzes a standalone generic FREE-routine call (<c>ptrtoint[Point, Address](p)</c>,
    /// <c>hijacked_none[T]()</c>): monomorphizes <paramref name="routine"/> against the explicit type
    /// args, types the arguments through the resulting substitution, and returns the (substituted)
    /// return type.
    /// </summary>
    private TypeSymbol AnalyzeStandaloneGenericRoutineCall(
        GenericMemberRoutineCallExpression generic, IdentifierExpression funcId,
        RoutineInfo routine, List<TypeSymbol> typeArgs)
    {
        // Realm gate for a GENERIC foreign call `LLVM::add[U128](...)` — `funcId` (== generic.Object)
        // carries the `::` qualifier the parser preserved, so enforce it here just like the
        // non-generic free-call path.

        CheckCallRealm(callee: funcId, routine: routine, location: generic.Location);

        // Capture the generic-def shape BEFORE swapping to the resolution: explicit
        // type arguments give a complete substitution map, which (a) types bare-literal
        // arguments via expectedType (`sub[S8](a: 0, ...)` makes 0 an S8, not an
        // UndecidedInteger) and (b) produces a CONCRETE annotated return type. Without
        // this, the call used to annotate ResolvedType=T — harmless for direct emission
        // (intrinsics emit from the explicit TypeArguments) but fatal for any consumer
        // that needs the value's type, e.g. the Maybe-carrier construction inside
        // generated try_/check_ variant bodies.
        Dictionary<string, TypeSymbol>? typeSubs = null;
        List<ParamInfo> declParams = routine.Parameters;
        // Tracks whether the routine is fully monomorphized below. When true, its ReturnType is
        // already the substituted form, so the typeSubs re-substitution further down must be
        // skipped — re-applying the map to an already-substituted return double-wraps nested
        // type args (RF-S301).
        bool routineMonomorphized = false;
        if (routine.IsGenericDefinition)
        {
            if (!TryMonomorphizeFreeRoutine(generic: generic,
                    routine: ref routine,
                    typeArgs: typeArgs,
                    typeSubs: out typeSubs))
            {
                return ErrorTypeSymbol.Instance;
            }

            declParams = routine.Parameters;
            routineMonomorphized = true;
        }

        generic.ResolvedRoutine = routine;
        generic.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);
        generic.IsInFlight = routine.IsInFlightReturn;

        AnalyzeGenericCallArguments(arguments: generic.Arguments,
            declParams: declParams,
            typeSubs: typeSubs);

        if (routine.ReturnType == null)
        {
            return _registry.LookupType(name: "None") ?? ErrorTypeSymbol.Instance;
        }

        TypeSymbol returnType = routine.ReturnType;

        // Already monomorphized above → ReturnType is final; re-substituting would double-wrap.
        if (routineMonomorphized)
        {
            return returnType;
        }

        // Explicit type arguments: substitute them through the whole return type
        // (bare T, nested Hijacked[T], tuples, …) in one general pass.
        if (typeSubs != null)
        {
            return SubstituteTypeParams(type: returnType, substitution: typeSubs);
        }

        // Legacy fallback for routines looked up as pre-built resolutions: substitute generic
        // parameters through the return type using the same helper as the member-routine path.
        return SubstituteGenericParamsInReturnType(returnType: returnType,
            memberRoutine: routine,
            typeArgs: typeArgs);
    }

    private void AnalyzeGenericCallArguments(List<Expression> arguments,
        List<ParamInfo> declParams, Dictionary<string, TypeSymbol>? typeSubs)
    {
        for (int argIdx = 0; argIdx < arguments.Count; argIdx++)
        {
            Expression arg = arguments[index: argIdx];
            ParamInfo? param;
            if (arg is NamedArgumentExpression namedArg)
            {
                param = declParams.FirstOrDefault(predicate: p => p.Name == namedArg.Name);
            }
            else
            {
                param = argIdx < declParams.Count
                    ? declParams[index: argIdx]
                    : null;
            }

            TypeSymbol? expected = null;
            if (param?.Type is { } paramType)
            {
                expected = typeSubs != null
                    ? SubstituteTypeParams(type: paramType, substitution: typeSubs)
                    : paramType;
            }

            AnalyzeExpression(expression: arg, expectedType: expected);
        }
    }

    /// <summary>
    /// Analyzes a generic TYPE CONSTRUCTOR call (<c>Hijacked[U8](addr)</c> / failable
    /// <c>Dict[Text, SerialValue]![...]</c>): resolves the generic def with the type args, binds a
    /// matching <c>create</c> overload (or inline field-init construction), and returns the constructed
    /// type. <paramref name="typeSymbol"/> is the generic definition resolved from <paramref name="typeId"/>.
    /// </summary>
    private TypeSymbol AnalyzeGenericTypeConstructorCall(
        GenericMemberRoutineCallExpression generic, IdentifierExpression typeId, TypeSymbol typeSymbol,
        List<TypeSymbol> typeArgs)
    {
        // Honor an explicit `RF::`/`SF::` realm on a generic construction (`RF::Core.List[T]()`): the
        // Object was resolved realm-blind (prefers the file's resolution realm), so inside an SF file
        // `RF::Core.List[T]()` would resolve to the SF-realm list and the SF wrapper's constructor
        // `return List[T](inner: RF::Core.List[T]())` would self-recurse. Swap the generic DEF to the
        // qualified realm before resolving, so the inner construction reaches the RazorForge list.
        if (typeId.Realm is { } genCtorRealm && typeSymbol.Realm != genCtorRealm &&
            _registry.ReResolveInRealm(type: typeSymbol, realm: genCtorRealm) is TypeSymbol
            {
                IsGenericDefinition: true
            } realmCtorDef)
        {
            typeSymbol = realmCtorDef;
        }

        // Resolve the generic type with the provided type arguments
        TypeSymbol resolvedType = _registry.GetOrCreateResolution(genericDef: typeSymbol,
            typeArguments: typeArgs.ToList());
        generic.ConstructedType = resolvedType;
        generic.LoweringKind = ClassifyConstruction(type: resolvedType,
            isCollectionLiteral: generic.IsCollectionLiteral);

        // For field-init style (named args matching field names), pre-compute a field-name →
        // field-type map so literals see the field's declared type as their contextual expected type.
        Dictionary<string, TypeSymbol>? fieldTypeByName =
            BuildFieldTypeMap(resolvedType: resolvedType);
        List<TypeSymbol> argTypes =
            AnalyzeGenericConstructorArgs(generic: generic, fieldTypeByName: fieldTypeByName);
        return ResolveGenericConstructorResult(generic: generic,
            resolvedType: resolvedType,
            argTypes: argTypes);
    }

    /// <summary>
    /// Builds a field-name to field-type map for <paramref name="resolvedType"/> so that named
    /// constructor arguments see the correct contextual expected type. Generic type parameters in
    /// field types are substituted using the type's own type arguments. Returns null when the type
    /// has no member variables (e.g. a wrapper type or scalar).
    /// </summary>
    private Dictionary<string, TypeSymbol>? BuildFieldTypeMap(TypeSymbol resolvedType)
    {
        List<MemberVariableInfo>? memberVars = resolvedType switch
        {
            RecordTypeSymbol r => r.MemberVariables,
            EntityTypeSymbol e => e.MemberVariables,
            _ => null
        };
        if (memberVars == null)
        {
            return null;
        }

        var map = new Dictionary<string, TypeSymbol>();
        foreach (MemberVariableInfo mv in memberVars)
        {
            TypeSymbol ft = resolvedType is { IsGenericResolution: true, TypeArguments: not null }
                ? SubstituteTypeParameters(type: mv.Type, genericType: resolvedType)
                : mv.Type;
            map[key: mv.Name] = ft;
        }

        return map;
    }

    /// <summary>
    /// Analyzes each argument of a generic constructor call, using
    /// <paramref name="fieldTypeByName"/> to supply the expected type for named arguments.
    /// Returns the list of resolved argument types.
    /// </summary>
    private List<TypeSymbol> AnalyzeGenericConstructorArgs(
        GenericMemberRoutineCallExpression generic,
        Dictionary<string, TypeSymbol>? fieldTypeByName)
    {
        var argTypes = new List<TypeSymbol>(capacity: generic.Arguments.Count);
        foreach (Expression arg in generic.Arguments)
        {
            TypeSymbol? expectedArgType = null;
            if (fieldTypeByName != null && arg is NamedArgumentExpression named &&
                fieldTypeByName.TryGetValue(key: named.Name, value: out TypeSymbol? ft))
            {
                expectedArgType = ft;
            }

            argTypes.Add(item: AnalyzeExpression(expression: arg, expectedType: expectedArgType));
        }

        return argTypes;
    }

    /// <summary>
    /// Validates that all constructor arguments are named when the type has 2+ fields,
    /// and returns the constructed type (or the creator's return type when a matching overload exists).
    /// </summary>
    private TypeSymbol ResolveGenericConstructorResult(GenericMemberRoutineCallExpression generic,
        TypeSymbol resolvedType, List<TypeSymbol> argTypes)
    {
        RoutineInfo? creator =
            _registry.LookupCreatorOverload(type: resolvedType, argTypes: argTypes);
        if (creator != null && creator.Parameters.Count == argTypes.Count &&
            !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
        {
            generic.ResolvedRoutine = creator;
            ValidateExclusiveTokenUniqueness(arguments: generic.Arguments,
                location: generic.Location);
            // Prefer the concrete resolvedType over the creator's return type when that type is still
            // generic (contains GenericParameterTypeSymbol placeholders), to avoid unresolved type leaking
            // to downstream callers.
            bool returnTypeIsGenericOrUnresolved =
                creator.ReturnType is null or { IsGenericDefinition: true } ||
                creator.ReturnType.TypeArguments?.Any(
                    predicate: t => t is GenericParameterTypeSymbol) == true;
            return returnTypeIsGenericOrUnresolved
                ? resolvedType
                : creator.ReturnType!;
        }

        int memberCount = resolvedType switch
        {
            EntityTypeSymbol e => e.MemberVariables.Count,
            RecordTypeSymbol r => r.MemberVariables.Count,
            _ => 0
        };
        if (memberCount >= 2)
        {
            foreach (Expression arg in generic.Arguments.Where(predicate: a =>
                         a is not NamedArgumentExpression))
            {
                ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                    message:
                    $"Type '{resolvedType.Name}' has {memberCount} fields - all constructor arguments must be named.",
                    location: arg.Location);
            }
        }

        ValidateExclusiveTokenUniqueness(arguments: generic.Arguments, location: generic.Location);
        return resolvedType;
    }

    private TypeSymbol AnalyzeGenericMemberExpression(GenericMemberExpression genericMember)
    {
        TypeSymbol objectType = AnalyzeExpression(expression: genericMember.Object);

        // Resolve type arguments
        var resolvedTypeArgs = new List<TypeSymbol>(capacity: genericMember.TypeArguments.Count);
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            resolvedTypeArgs.Add(item: ResolveType(typeExpr: typeArg));
        }

        // Typewise receiver: `Ident[T]` parsed as GenericMemberExpression(Ident, Ident.Name, [T])
        // — the parser sets MemberName == Object.Name when the source was `Ident[Args]`, not
        // `obj.field[i]`. When that holds and Object resolves to a generic type, return the
        // monomorphized type so the outer `.MemberRoutine()` call has a proper typewise receiver type.
        if (genericMember.Object is IdentifierExpression idReceiver &&
            idReceiver.Name == genericMember.MemberName && objectType.IsGenericDefinition &&
            resolvedTypeArgs.Count == objectType.GenericParameters?.Count)
        {
            TypeSymbol resolved = _registry.GetOrCreateResolution(genericDef: objectType,
                typeArguments: resolvedTypeArgs);
            genericMember.ResolvedType = resolved;
            return resolved;
        }

        // Look up the member on the object type
        List<MemberVariableInfo>? memberVars = objectType switch
        {
            EntityTypeSymbol e => e.MemberVariables,
            RecordTypeSymbol r => r.MemberVariables,
            _ => null
        };
        MemberVariableInfo? memberVar =
            memberVars?.FirstOrDefault(predicate: mv => mv.Name == genericMember.MemberName);
        if (memberVar != null)
        {
            return AnalyzeGenericMemberIndexing(genericMember: genericMember,
                memberVar: memberVar);
        }

        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Handles the case where a <c>GenericMemberExpression</c>'s bracketed args are actually INDEX
    /// values into a member variable's collection value (not type arguments). Analyzes the index
    /// expressions and returns the element type (collection first type-arg, else <c>getitem</c>'s
    /// return type, else the member type itself).
    /// </summary>
    private TypeSymbol AnalyzeGenericMemberIndexing(GenericMemberExpression genericMember,
        MemberVariableInfo memberVar)
    {
        // Member found — the [args] are indexing into the member's value.
        // Analyze the "type arguments" as expressions (they're actually index values).
        foreach (TypeExpression typeArg in genericMember.TypeArguments)
        {
            // The type arg's Name is actually a variable name — analyze it as identifier
            if (typeArg.Name != null)
            {
                AnalyzeExpression(expression: new IdentifierExpression(Name: typeArg.Name,
                    Location: typeArg.Location));
            }
        }

        // Determine the element type of the member's collection type
        TypeSymbol? memberType = memberVar.Type;
        if (memberType is { TypeArguments: { Count: > 0 } })
        {
            // e.g., List[SortedDict[K,V]]  element is SortedDict[K,V]
            return memberType.TypeArguments[index: 0];
        }

        // If the member type has a getitem memberRoutine, use its return type
        RoutineInfo? getItem =
            _registry.LookupMemberRoutine(type: memberType, memberRoutineName: "getitem");
        if (getItem?.ReturnType != null)
        {
            return getItem.ReturnType;
        }

        return memberType;
    }

    private TypeSymbol AnalyzeIsPatternExpression(IsPatternExpression isPat)
    {
        TypeSymbol exprType = AnalyzeExpression(expression: isPat.Expression);

        // Analyze the pattern (may bind variables)
        AnalyzePattern(pattern: isPat.Pattern, matchedType: exprType);

        // 'is' expressions always return bool
        return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
    }

    private TypeSymbol AnalyzeFlagsTestExpression(FlagsTestExpression flagsTest)
    {
        TypeSymbol subjectType = AnalyzeExpression(expression: flagsTest.Subject);

        if (subjectType.Category == TypeCategory.Error)
        {
            return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        if (subjectType is not FlagsTypeSymbol flagsType)
        {
            ReportError(code: SemanticDiagnosticCode.FlagsTypeMismatch,
                message:
                $"Flags test operators (is/isnot) require a flags type, but got '{subjectType.Name}'.",
                location: flagsTest.Location);
            return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
        }

        // Validate each flag name exists in the type
        ValidateFlagNamesExist(flagsType: flagsType,
            flagNames: flagsTest.TestFlags,
            location: flagsTest.Location);

        // Validate excluded flags too
        if (flagsTest.ExcludedFlags != null)
        {
            ValidateFlagNamesExist(flagsType: flagsType,
                flagNames: flagsTest.ExcludedFlags,
                location: flagsTest.Location);
        }

        return _registry.LookupType(name: "Bool") ?? ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Reports RF-S (FlagsMemberNotFound) for each name in <paramref name="flagNames"/> that is not a
    /// member of <paramref name="flagsType"/>.
    /// </summary>
    private void ValidateFlagNamesExist(FlagsTypeSymbol flagsType, IEnumerable<string> flagNames,
        SourceLocation location)
    {
        foreach (string flagName in flagNames.Where(predicate: n =>
                     flagsType.Members.All(predicate: m => m.Name != n)))
        {
            ReportError(code: SemanticDiagnosticCode.FlagsMemberNotFound,
                message:
                $"Flags type '{flagsType.Name}' does not have a member named '{flagName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Resolves the generic DEFINITION backing an owner type (its <c>GenericDefinition</c>, or the type
    /// itself when it already IS a generic definition), or null when the owner is not generic. Shared by
    /// the owner-binding-map / owner-parameter-name walkers.
    /// </summary>
    private static TypeSymbol? ResolveGenericOwnerDefinition(TypeSymbol ownerType)
    {
        return ownerType switch
        {
            RecordTypeSymbol r => r.GenericDefinition ?? (r.IsGenericDefinition
                ? r
                : null),
            EntityTypeSymbol e => e.GenericDefinition ?? (e.IsGenericDefinition
                ? e
                : null),
            ProtocolTypeSymbol p => p.GenericDefinition ?? (p.IsGenericDefinition
                ? p
                : null),
            WrapperTypeSymbol w => w.IsGenericDefinition
                ? w
                : null,
            _ => ownerType.IsGenericDefinition
                ? ownerType
                : null
        };
    }

    private static Dictionary<string, TypeSymbol> BuildOwnerBindingMap(TypeSymbol? ownerType)
    {
        var map = new Dictionary<string, TypeSymbol>();
        if (ownerType == null)
        {
            return map;
        }

        TypeSymbol? def = ResolveGenericOwnerDefinition(ownerType: ownerType);

        List<string>? paramNames = def?.GenericParameters ?? ownerType.GenericParameters;
        List<TypeSymbol>? args = ownerType.TypeArguments;

        if (paramNames != null && args != null)
        {
            for (int i = 0; i < paramNames.Count && i < args.Count; i++)
            {
                map[key: paramNames[index: i]] = args[index: i];
            }

            return map;
        }

        // Fallback: receiver is an unsubstituted generic instance like Hijacked[T] inside its own
        // body. Map each param name to a same-named GenericParameterTypeSymbol placeholder.
        if (paramNames != null)
        {
            foreach (string p in paramNames)
            {
                map[key: p] = new GenericParameterTypeSymbol(name: p);
            }
        }

        return map;
    }

    private static HashSet<string> GetOwnerGenericParameterNames(TypeSymbol? ownerType)
    {
        var names = new HashSet<string>();
        if (ownerType == null)
        {
            return names;
        }

        TypeSymbol? def = ResolveGenericOwnerDefinition(ownerType: ownerType);

        if (def?.GenericParameters != null)
        {
            foreach (string p in def.GenericParameters)
            {
                names.Add(item: p);
            }
        }
        else if (ownerType.GenericParameters != null)
        {
            foreach (string p in ownerType.GenericParameters)
            {
                names.Add(item: p);
            }
        }

        return names;
    }

    private ErrorTypeSymbol HandleUnknownExpression(Expression expression)
    {
        ReportWarning(code: SemanticWarningCode.UnknownExpressionType,
            message:
            $"Internal: semantic analyzer has no handler for AST node '{expression.GetType().Name}'. This expression will be skipped; downstream type info may be incomplete. Please report as a compiler bug.",
            location: expression.Location);
        return ErrorTypeSymbol.Instance;
    }
}
