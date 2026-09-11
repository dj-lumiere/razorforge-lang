using Builder.Tokenizer;
using Builder.Desugaring.Passes;
using Builder.Lowering.Passes;
using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Instantiation;

/// <summary>
/// Deep-clones a RoutineDeclaration AST, replacing all occurrences of generic type
/// parameter names with their concrete substitutions. This allows codegen to work
/// with a fully-resolved AST where no generic parameters remain.
/// </summary>
internal static class GenericAstRewriter
{
    // Projection name shared by FoldHandleProjection, FoldMetadataIntrinsic, and the BuilderQuery
    // `type_id()` fold, to avoid the S1192 duplicate-literal warning.
    private const string TypeIdProjection = "type_id";

    /// <summary>
    /// Rewrites a generic routine declaration by substituting all type parameter references
    /// with concrete type names. Returns a deep clone -> the original is not modified.
    /// <para>
    /// When <paramref name="typeSubs"/> and <paramref name="registry"/> are provided, the
    /// rewriter also sets <see cref="Expression.ResolvedType"/> on every cloned expression
    /// whose original <c>ResolvedType</c> was a <see cref="GenericParameterTypeSymbol"/> or a
    /// generic resolution containing generic parameters. This removes the need for
    /// <c>_typeSubstitutions</c> fallback lookups during codegen emission.
    /// </para>
    /// </summary>
    public static RoutineDeclaration Rewrite(RoutineDeclaration routine,
        IReadOnlyDictionary<string, string> subs,
        IReadOnlyDictionary<string, TypeSymbol>? typeSubs = null, TypeRegistry? registry = null,
        RoutineInfo? enclosingRoutine = null)
    {
        RewriteContext ctx = typeSubs != null && registry != null
            ? new RewriteContext(stringSubs: subs, typeSubs: typeSubs, registry: registry)
            : new RewriteContext(stringSubs: subs, typeSubs: null, registry: null);

        var rewrittenParams = routine.Parameters
                                     .Select(selector: p => RewriteParameter(param: p, ctx: ctx))
                                     .ToList();

        foreach (Parameter p in rewrittenParams)
        {
            TypeSymbol? pType = p.Type?.ResolvedType ?? (p.Type != null
                ? ctx.ResolveTypeExpressionPublic(typeExpr: p.Type)
                : null);
            if (pType != null)
            {
                ctx.ParamTypes[key: p.Name] = pType;
            }
        }

        if (enclosingRoutine?.OwnerType is { } ownerType)
        {
            TypeSymbol resolvedOwner = ctx.ResolveType(original: ownerType) ?? ownerType;
            ctx.ParamTypes[key: "me"] = resolvedOwner;
        }

        TypeExpression? rewrittenReturnType = routine.ReturnType != null
            ? RewriteType(type: routine.ReturnType, ctx: ctx)
            : null;
        Statement rewrittenBody = RewriteStatement(stmt: routine.Body, ctx: ctx);

        return routine with
        {
            Parameters = rewrittenParams,
            ReturnType = rewrittenReturnType,
            Body = rewrittenBody,
            GenericParameters = null // No longer generic after substitution
        };
    }

    private static Parameter RewriteParameter(Parameter param, RewriteContext ctx)
    {
        TypeExpression? rewrittenType = param.Type != null
            ? RewriteType(type: param.Type, ctx: ctx)
            : null;
        Expression? rewrittenDefault = param.DefaultValue != null
            ? RewriteExpression(expr: param.DefaultValue, ctx: ctx)
            : null;
        return param with { Type = rewrittenType, DefaultValue = rewrittenDefault };
    }

    //  RewriteContext

    /// <summary>
    /// Carries the string-name substitution map (always present) and the optional
    /// TypeSymbol substitution map + registry (used for ResolvedType annotation).
    /// </summary>
    private sealed class RewriteContext(
        IReadOnlyDictionary<string, string> stringSubs,
        IReadOnlyDictionary<string, TypeSymbol>? typeSubs,
        TypeRegistry? registry)
    {
        public IReadOnlyDictionary<string, string> StringSubs { get; } = stringSubs;
        public IReadOnlyDictionary<string, TypeSymbol>? TypeSubs { get; } = typeSubs;
        public TypeRegistry? Registry { get; } = registry;


        /// <summary>
        /// PURE STRUCTURAL DEEP-COPY mode. When true, NO buildtime folding/unrolling runs — every
        /// <c>expand</c> loop, <c>when T … is X</c> type-switch, arm-expansion, splice, and metadata
        /// intrinsic (<c>nameof</c>/<c>orderof</c>/…) is left INTACT and merely recursed into structurally.
        /// Used to clone a GENERIC-DEFINITION template body (which must stay UNFOLDED until real
        /// monomorphization binds its type params) so each warm build owns a private copy — on-demand SA
        /// then mutates the copy, never the shared stdlib snapshot template (the warm/cold define-set
        /// leak). A non-clone rewrite (real subs) leaves this false so folding proceeds as before.
        /// </summary>
        public bool CloneOnly { get; init; }

        /// <summary>
        /// The active buildtime <c>expand</c> handle name (e.g. <c>m</c>) while its body is being
        /// unrolled for one member, or null outside any expand. When set, <c>m.name</c>/<c>m.id</c>
        /// member accesses fold to literals and <c>x.${m.name}</c> splices become real member
        /// accesses on <see cref="ActiveMemberName"/>. Save/restore around the loop for nesting.
        /// </summary>
        public string? ActiveExpandHandle { get; set; }

        /// <summary>The current member's field name during expand unrolling.</summary>
        public string? ActiveMemberName { get; set; }

        /// <summary>The current member's 0-based ordinal during expand unrolling.</summary>
        public long ActiveMemberIndex { get; set; }

        /// <summary>The current member's static type during expand unrolling (for annotating the
        /// unrolled member access so the chained call resolves).</summary>
        public TypeSymbol? ActiveMemberType { get; set; }

        /// <summary>The current member's full OPEN/POSTED/SECRET visibility during expand unrolling,
        /// folded by <c>visibilityof(m)</c> to the matching <c>Visibility</c> choice case.</summary>
        public VisibilityModifier ActiveMemberVisibility { get; set; }

        /// <summary>The current member's byte OFFSET within its parent struct (repr-C layout, folded by
        /// <c>placeof(m)</c>). Computed over the FULL declaration-order member list — a <c>secret</c>/
        /// <c>posted</c> field ahead still pushes the offset — so it is layout-faithful even when
        /// <c>openmemvarof</c> filtered the iteration.</summary>
        public long ActiveMemberOffset { get; set; }

        /// <summary>The current case's numeric constant during a <c>caseof</c> expand unrolling — a
        /// choice's S32 discriminant or a flags member's U64 bit value — folded by <c>${c.value}</c>.</summary>
        public long ActiveCaseValue { get; set; }

        /// <summary>True while unrolling <c>caseof</c> over a FLAGS type (value is a U64 bit); false for
        /// a choice (value is an S32 discriminant). Selects the literal type for <c>${c.value}</c>.</summary>
        public bool ActiveCaseIsFlags { get; set; }

        /// <summary>
        /// Payload-binding name → concrete type while cloning an <c>branchof</c> arm clause body, so a
        /// reference to the bound payload (e.g. <c>x</c> in <c>is ${m.type} x => x.represent()</c>)
        /// resolves to the arm's type. Cleared per arm.
        /// </summary>
        public Dictionary<string, TypeSymbol> ActiveBindingTypes { get; } = new();

        /// <summary>
        /// Map of parameter name to substituted TypeSymbol for the routine being rewritten.
        /// Populated by <see cref="Rewrite"/> and the public <c>RewriteStatement</c>
        /// overloads that accept a routine. Used to backfill <c>IdentifierExpression.ResolvedType</c>
        /// when SA failed to annotate a parameter reference (e.g. <c>you.address()</c> in
        /// <c>Hijacked[T].cmp</c>'s generic-def body).
        /// </summary>
        public Dictionary<string, TypeSymbol> ParamTypes { get; } = new();

        /// <summary>
        /// Local variables whose type must be RE-INFERRED after monomorphization because their
        /// initializer re-dispatched to a concrete owner. Specifically: a `var it = r.iter()` where
        /// `r` was a protocol-constrained generic param (`__T0 obeys Iterable[S64]`) is typed by SA
        /// as the abstract protocol return (`Iterator[S64]`), but after `__T0 → Range[S64]` the call
        /// re-dispatches to `Range[S64].iter` returning the CONCRETE `RangeIterator[S64]`. Recording
        /// that here lets later references (`it.try_emit()`) re-dispatch against the concrete iterator
        /// instead of the abstract protocol memberRoutine (which has no body → linker error). Only used to
        /// concretize references whose stale type is itself a protocol, so other locals are untouched.
        /// </summary>
        public Dictionary<string, TypeSymbol> LocalReinferredTypes { get; } = new();

        /// <summary>
        /// Resolves a <see cref="TypeSymbol"/> through the substitution map. Returns null
        /// when the registry is not available or the type has no substitution.
        /// </summary>
        public TypeSymbol? ResolveType(TypeSymbol? original)
        {
            if (original == null || TypeSubs == null || Registry == null)
            {
                return null;
            }

            if (ResolveSpecialType(original: original) is { } special)
            {
                return special;
            }

            // Generic resolution with substitutable type arguments: List[T] -> List[S64]
            if (original is { IsGenericResolution: true, TypeArguments: not null } &&
                ResolveGenericArguments(original: original) is { } resolvedArguments)
            {
                return resolvedArguments;
            }

            // Generic definition used as a concrete type (e.g., owner type List[T] where T -> S64).
            // This arises when SA annotates `me.ResolvedType = List[T]` (the generic def) and the
            // rewriter encounters it while building a concrete body. Substitute all params from
            // TypeSubs and look up the concrete resolution so downstream scanners see the right owner.
            if (original is
                {
                    IsGenericDefinition: true, GenericParameters: not null, TypeArguments: null
                } && ResolveGenericDefinition(original: original) is { } resolvedDefinition)
            {
                return resolvedDefinition;
            }

            // WrapperTypeSymbol (Hijacked[T] -> Hijacked[S64], or Hijacked[S64] -> stays): always
            // resolve to the real RecordTypeSymbol so LLVM mangled names use "Core.Hijacked[S64]"
            // (from RecordTypeSymbol.FullName) rather than "Hijacked[Core.S64]" (WrapperTypeSymbol
            // with Module=null). The WrapperTypeSymbol.FullName appends inner.FullName which includes
            // the module prefix, producing the wrong "Hijacked[Core.S64]" format.
            if (original is WrapperTypeSymbol wrapper)
            {
                return ResolveWrapper(wrapper: wrapper);
            }

            // Routine value type (`Routine[(T,), U]` -> `Routine[(S64,), S64]`): RoutineTypeSymbol
            // carries its parameter/return types in dedicated slots, NOT TypeArguments, so the
            // generic-resolution branch above never reaches them. A chained iterator emitter stores
            // its projection as `secret transform: Routine[(T,), U]`; the `me.transform(item)` call's
            // indirect return type is read straight off this RoutineTypeSymbol at codegen, so an
            // unsubstituted `U` here reaches GetLlvmType. Resolve each slot recursively (handles
            // nested generics/wrappers/tuples uniformly) and rebuild.
            if (original is RoutineTypeSymbol routineType)
            {
                return ResolveRoutineType(routineType: routineType);
            }

            // Tuple (`Tuple[T, Bool]` -> `Tuple[U8, Bool]`): TupleTypeSymbol is not an
            // IsGenericResolution, so substitute each element type and rebuild. Without this a
            // tuple carrying `T` in a CallExpression.ResolvedType survives into codegen.
            if (original is TupleTypeSymbol tuple)
            {
                return ResolveTupleType(tuple: tuple);
            }

            return null;
        }

        private TypeSymbol? ResolveSpecialType(TypeSymbol original)
        {
            // A marker borrow protocol (Accessing[X]/Controlling[X]) is ABI-transparent to its inner X:
            // collapse it so no protocol type survives on a monomorphized expression's ResolvedType (the
            // backend then reads the concrete inner — an entity ptr / a value — never a protocol).
            if (original is ProtocolTypeSymbol { TypeArguments: [{ } markerInner] } markerProto &&
                RuntimeContract.IsMarkerProtocol(
                    baseName: (markerProto.GenericDefinition ?? markerProto).BareName))
            {
                return ResolveType(original: markerInner) ?? markerInner;
            }

            // Protocol self (`Me`/ProtocolSelf) -> the bound implementer (TypeSubs["Me"]).
            if (original is ProtocolSelfTypeSymbol &&
                TypeSubs!.TryGetValue(key: "Me", value: out TypeSymbol? meBound))
            {
                return meBound;
            }

            // Associated-type projection (`S/Iter`) -> resolve base then its binding.
            if (original is AssociatedProjectionTypeSymbol proj &&
                ResolveAssociatedType(proj: proj) is { } resolvedProjection)
            {
                return resolvedProjection;
            }

            // Direct generic parameter substitution: T -> S64
            if (original is GenericParameterTypeSymbol gp && ResolveGenericParameter(gp: gp) is
                    { } resolvedParameter)
            {
                return resolvedParameter;
            }

            return null;
        }

        private TypeSymbol? ResolveAssociatedType(AssociatedProjectionTypeSymbol proj)
        {
            TypeSymbol newBase = ResolveType(original: proj.Base) ?? proj.Base;
            TypeSymbol? bound = RecordTypeSymbol.ProjectAssociatedBinding(baseType: newBase,
                slot: proj.SlotName);
            if (bound != null)
            {
                return ResolveType(original: bound) ?? bound;
            }

            return null;
        }

        private TypeSymbol? ResolveGenericParameter(GenericParameterTypeSymbol gp)
        {
            // `$Col` is the decl-position expand-column placeholder. In an EXPRESSION-position type
            // splice — `hijacked_from[${m.type}]` / `blank[Hijacked[${m.type}]]` inside an
            // `expand m in allmemvarof(T)` body — it must fold to the CURRENT member's concrete type,
            // mirroring TypeRegistry.ExpandSoAColumns' decl-position substitution but driven here by
            // the active expand unroll at monomorphization. Without this the `$Col` GenericParameter
            // reaches codegen's GetLlvmType and trips the "all generic parameters must be substituted".
            if (gp.Name == MemberExpandTemplateInfo.ColumnPlaceholderName &&
                ActiveMemberType != null)
            {
                return ActiveMemberType;
            }

            if (TypeSubs!.TryGetValue(key: gp.Name, value: out TypeSymbol? direct))
            {
                return direct;
            }

            // Wrapper-forwarder rename fallback: the param carries the original inner-T
            // name as a structural marker (`ForwarderOriginalName`). At monomorphization
            // time the binding lives under that name, not under the disambiguated `Name`.
            if (gp.ForwarderOriginalName is { } originalInnerName &&
                TypeSubs.TryGetValue(key: originalInnerName, value: out TypeSymbol? renamed))
            {
                return renamed;
            }

            return null;
        }

        private TypeSymbol? ResolveGenericArguments(TypeSymbol original)
        {
            bool anyChanged = false;
            var newArgs = new List<TypeSymbol>(capacity: original.TypeArguments!.Count);
            foreach (TypeSymbol arg in original.TypeArguments)
            {
                TypeSymbol? resolved = ResolveType(original: arg);
                if (resolved != null && !ReferenceEquals(objA: resolved, objB: arg))
                {
                    newArgs.Add(item: resolved);
                    anyChanged = true;
                }
                else
                {
                    newArgs.Add(item: arg);
                }
            }

            if (anyChanged)
            {
                TypeSymbol? genericBase = original switch
                {
                    RecordTypeSymbol { GenericDefinition: { } d } => d,
                    EntityTypeSymbol { GenericDefinition: { } d } => d,
                    ProtocolTypeSymbol { GenericDefinition: { } d } => d,
                    _ => null
                };
                if (genericBase != null)
                {
                    // Prefer cached resolution if already created — else CREATE one via
                    // GetOrCreateResolution so nested-generic args (e.g. Retained[ListNode[T]]
                    // with T → S64 producing Retained[ListNode[S64]]) actually get registered.
                    // TryGetResolution alone falls back to `original` if the registry hasn't
                    // seen the combination, leaving the inner type-arg substitution lost.
                    return Registry!.TryGetResolution(genericDef: genericBase,
                               typeArguments: newArgs) ??
                           Registry.GetOrCreateResolution(genericDef: genericBase,
                               typeArguments: newArgs);
                }
            }

            return null;
        }

        private TypeSymbol? ResolveGenericDefinition(TypeSymbol original)
        {
            var typeArgs = new List<TypeSymbol>(capacity: original.GenericParameters!.Count);
            bool complete = true;
            foreach (string gpName in original.GenericParameters)
            {
                if (TypeSubs!.TryGetValue(key: gpName, value: out TypeSymbol? subType))
                {
                    typeArgs.Add(item: subType);
                }
                else
                {
                    complete = false;
                    break;
                }
            }

            if (complete && typeArgs.Count > 0)
            {
                return Registry!.TryGetResolution(genericDef: original, typeArguments: typeArgs);
            }

            return null;
        }

        private WrapperTypeSymbol? ResolveWrapper(WrapperTypeSymbol wrapper)
        {
            var newWrapperArgs = new List<TypeSymbol>(capacity: wrapper.TypeArguments?.Count ?? 1);
            foreach (TypeSymbol arg in wrapper.TypeArguments ?? [])
            {
                TypeSymbol? resolved = ResolveType(original: arg);
                newWrapperArgs.Add(
                    item: resolved != null && !ReferenceEquals(objA: resolved, objB: arg)
                        ? resolved
                        : arg);
            }

            if (Registry != null && newWrapperArgs.Count == 1)
            {
                // Create-if-missing — body rewriting can encounter wrapper parameterizations
                // (e.g., Hijacked[Text]) that no earlier pass materialized. Without
                // creation here, GMP never sees the type and codegen emits unresolved symbols.
                return Registry.GetOrCreateWrapperType(wrapperName: wrapper.Name,
                    innerType: newWrapperArgs[index: 0],
                    isReadOnly: wrapper.IsReadOnly);
            }

            return null;
        }

        private RoutineTypeSymbol? ResolveRoutineType(RoutineTypeSymbol routineType)
        {
            bool anyRoutineChanged = false;
            var newParams = new List<TypeSymbol>(capacity: routineType.ParameterTypes.Count);
            foreach (TypeSymbol p in routineType.ParameterTypes)
            {
                TypeSymbol? resolved = ResolveType(original: p);
                newParams.Add(item: resolved != null && !ReferenceEquals(objA: resolved, objB: p)
                    ? resolved
                    : p);
                if (resolved != null && !ReferenceEquals(objA: resolved, objB: p))
                {
                    anyRoutineChanged = true;
                }
            }

            TypeSymbol? newReturn = routineType.ReturnType != null
                ? ResolveType(original: routineType.ReturnType)
                : null;
            if (newReturn != null &&
                !ReferenceEquals(objA: newReturn, objB: routineType.ReturnType))
            {
                anyRoutineChanged = true;
            }

            if (anyRoutineChanged)
            {
                return new RoutineTypeSymbol(parameterTypes: newParams,
                    returnType: newReturn ?? routineType.ReturnType)
                {
                    IsFailable = routineType.IsFailable
                };
            }

            return null;
        }

        private TupleTypeSymbol? ResolveTupleType(TupleTypeSymbol tuple)
        {
            bool anyChanged = false;
            var newElems = new List<TypeSymbol>(capacity: tuple.ElementTypes.Count);
            foreach (TypeSymbol elem in tuple.ElementTypes)
            {
                TypeSymbol? resolved = ResolveType(original: elem);
                if (resolved != null && !ReferenceEquals(objA: resolved, objB: elem))
                {
                    newElems.Add(item: resolved);
                    anyChanged = true;
                }
                else
                {
                    newElems.Add(item: elem);
                }
            }

            if (anyChanged)
            {
                return new TupleTypeSymbol(elementTypes: newElems);
            }

            return null;
        }

        public RoutineInfo? ResolveRoutine(RoutineInfo? original, TypeSymbol? expressionType = null,
            List<TypeSymbol>? callArgTypes = null)
        {
            if (original == null || Registry == null)
            {
                return null;
            }

            TypeSymbol? resolvedOwner = ResolveTypeForLookup(original: original.OwnerType);
            var resolvedParamTypes = original.Parameters
                                             .Select(selector: selector =>
                                                  ResolveTypeForLookup(original: selector.Type) ??
                                                  selector.Type)
                                             .ToList();
            // Prefer concrete call-site arg types for memberRoutine-generic inference — routine's own
            // Parameters still carry generic param refs (e.g., I) that ResolveTypeForLookup
            // can't concretize since they're memberRoutine-level, not owner-level.
            List<TypeSymbol> memberRoutineInferArgTypes = callArgTypes is { Count: > 0 }
                ? callArgTypes
                : resolvedParamTypes;

            if (resolvedOwner != null)
            {
                RoutineInfo? onOwner = ResolveRoutineOnOwner(original: original,
                    resolvedOwner: resolvedOwner,
                    resolvedParamTypes: resolvedParamTypes,
                    memberRoutineInferArgTypes: memberRoutineInferArgTypes);
                if (onOwner != null)
                {
                    return onOwner;
                }
            }

            if (original.IsCreator)
            {
                RoutineInfo? creator = ResolveCreateTarget(original: original,
                    expressionType: expressionType,
                    resolvedParamTypes: resolvedParamTypes);
                if (creator != null)
                {
                    return creator;
                }
            }

            if (original.OwnerType == null)
            {
                RoutineInfo? free = ResolveFreeRoutine(original: original,
                    resolvedParamTypes: resolvedParamTypes);
                if (free != null)
                {
                    return free;
                }
            }

            if (original.IsGenericDefinition || original.OwnerType is GenericParameterTypeSymbol or
                    { IsGenericDefinition: true })
            {
                return null;
            }

            return original;
        }

        /// <summary>
        /// Resolves <paramref name="original"/> as a memberRoutine on the (concrete) resolved owner,
        /// monomorphizing a memberRoutine-generic definition form when needed. Returns null when no
        /// concrete-owner memberRoutine binds here (the caller then falls through to other strategies).
        /// </summary>
        private RoutineInfo? ResolveRoutineOnOwner(RoutineInfo original, TypeSymbol resolvedOwner,
            List<TypeSymbol> resolvedParamTypes, List<TypeSymbol> memberRoutineInferArgTypes)
        {
            RoutineInfo? resolvedMemberRoutine = ResolveMemberRoutineOnConcreteOwner(
                ownerType: resolvedOwner,
                memberRoutineName: original.Name,
                argTypes: resolvedParamTypes,
                isFailable: original.IsFailable);
            if (resolvedMemberRoutine == null)
            {
                return null;
            }

            // If LookupMemberRoutine returned the generic-definition form of a memberRoutine-generic
            // routine (e.g., Array[T,N].getitem[I]), monomorphize it using the
            // substituted argument types so codegen gets a concrete routine, not a
            // generic-def one.
            if (resolvedMemberRoutine is
                { IsGenericDefinition: true, GenericParameters.Count: > 0 })
            {
                RoutineInfo? memberRoutineResolved = TryResolveMemberRoutineGeneric(
                    routine: resolvedMemberRoutine,
                    argTypes: memberRoutineInferArgTypes);
                if (memberRoutineResolved != null)
                {
                    return memberRoutineResolved;
                }
            }

            if (resolvedMemberRoutine.OwnerType is not { IsGenericDefinition: true })
            {
                return resolvedMemberRoutine;
            }

            return null;
        }

        /// <summary>
        /// Resolves a <c>create</c> routine on the concrete construction target derived from
        /// <paramref name="expressionType"/>. Returns null when the target or a concrete creator
        /// cannot be resolved.
        /// </summary>
        private RoutineInfo? ResolveCreateTarget(RoutineInfo original, TypeSymbol? expressionType,
            List<TypeSymbol> resolvedParamTypes)
        {
            TypeSymbol? resolvedTarget = ResolveTypeForLookup(original: expressionType);
            if (resolvedTarget == null)
            {
                return null;
            }

            RoutineInfo? resolvedCreator = ResolveMemberRoutineOnConcreteOwner(
                ownerType: resolvedTarget,
                memberRoutineName: RoutineInfo.CreatorName,
                argTypes: resolvedParamTypes,
                isFailable: original.IsFailable);
            resolvedCreator ??= Registry!.LookupCreatorOverload(
                type: resolvedTarget,
                argTypes: resolvedParamTypes);
            if (resolvedCreator?.OwnerType is { IsGenericDefinition: true })
            {
                resolvedCreator = null;
            }

            return resolvedCreator;
        }

        /// <summary>
        /// Resolves an owner-less (free) routine: instantiate it if generic, else look up a matching
        /// overload with the same failability. Returns null when neither strategy binds.
        /// </summary>
        private RoutineInfo? ResolveFreeRoutine(RoutineInfo original,
            List<TypeSymbol> resolvedParamTypes)
        {
            RoutineInfo? instantiatedRoutine = TryInstantiateRoutine(routine: original);
            if (instantiatedRoutine != null)
            {
                return instantiatedRoutine;
            }

            RoutineInfo? resolvedRoutine =
                Registry!.LookupRoutineOverload(baseName: original.BaseName,
                    argTypes: resolvedParamTypes);
            if (resolvedRoutine != null && resolvedRoutine.IsFailable == original.IsFailable)
            {
                return resolvedRoutine;
            }

            return null;
        }

        // Mirrors OperatorLoweringPass.ResolveMemberRoutineGenericRoutine: infer memberRoutine-level
        // generic arguments from call-site param types, then GetOrCreateRoutineResolution.
        private RoutineInfo? TryResolveMemberRoutineGeneric(RoutineInfo routine,
            List<TypeSymbol> argTypes)
        {
            if (Registry == null || !routine.IsGenericDefinition ||
                routine.GenericParameters == null)
            {
                return null;
            }

            var inferred = new TypeSymbol?[routine.GenericParameters.Count];
            int count = Math.Min(val1: routine.Parameters.Count, val2: argTypes.Count);
            for (int i = 0; i < count; i++)
            {
                InferMemberRoutineParam(paramType: routine.Parameters[index: i].Type,
                    argType: argTypes[index: i],
                    genericParams: routine.GenericParameters,
                    inferred: inferred);
            }

            if (inferred.Any(predicate: t => t == null))
            {
                return null;
            }

            return Registry.GetOrCreateRoutineResolution(genericDef: routine,
                typeArguments: inferred.Select(selector: t => t!)
                                       .ToList());
        }

        private static void InferMemberRoutineParam(TypeSymbol? paramType, TypeSymbol argType,
            List<string> genericParams, TypeSymbol?[] inferred)
        {
            if (paramType == null)
            {
                return;
            }

            if (paramType is GenericParameterTypeSymbol gp)
            {
                for (int idx = 0; idx < genericParams.Count; idx++)
                {
                    if (genericParams[index: idx] == gp.Name)
                    {
                        inferred[idx] ??= argType;
                        break;
                    }
                }

                return;
            }

            if (paramType.TypeArguments is { Count: > 0 } pArgs &&
                argType.TypeArguments is { Count: > 0 } aArgs)
            {
                int n = Math.Min(val1: pArgs.Count, val2: aArgs.Count);
                for (int i = 0; i < n; i++)
                {
                    InferMemberRoutineParam(paramType: pArgs[index: i],
                        argType: aArgs[index: i],
                        genericParams: genericParams,
                        inferred: inferred);
                }
            }
        }

        private RoutineInfo? TryInstantiateRoutine(RoutineInfo routine)
        {
            RoutineInfo genericDefinition = routine.GenericDefinition ?? routine;
            if (!genericDefinition.IsGenericDefinition ||
                genericDefinition.GenericParameters == null)
            {
                return null;
            }

            var resolvedTypeArgs =
                new List<TypeSymbol>(capacity: genericDefinition.GenericParameters.Count);
            for (int i = 0; i < genericDefinition.GenericParameters.Count; i++)
            {
                TypeSymbol? resolvedArg = null;
                if (routine.TypeArguments is { Count: > 0 } && i < routine.TypeArguments.Count)
                {
                    resolvedArg = ResolveTypeForLookup(original: routine.TypeArguments[index: i]);
                }

                resolvedArg ??= TypeSubs != null && TypeSubs.TryGetValue(
                    key: genericDefinition.GenericParameters[index: i],
                    value: out TypeSymbol? substituted)
                    ? substituted
                    : null;

                if (resolvedArg == null || resolvedArg is GenericParameterTypeSymbol)
                {
                    return null;
                }

                resolvedTypeArgs.Add(item: resolvedArg);
            }

            return Registry?.GetOrCreateRoutineResolution(genericDef: genericDefinition,
                typeArguments: resolvedTypeArgs);
        }

        private RoutineInfo? ResolveMemberRoutineOnConcreteOwner(TypeSymbol ownerType,
            string memberRoutineName, List<TypeSymbol> argTypes, bool isFailable)
        {
            string lookupMemberRoutineName =
                NormalizeMemberRoutineLookupName(memberRoutineName: memberRoutineName);
            RoutineInfo? overload = Registry!.LookupMemberRoutineOverload(type: ownerType,
                memberRoutineName: lookupMemberRoutineName,
                argTypes: argTypes);
            if (overload?.OwnerType is not { IsGenericDefinition: true })
            {
                return overload ?? Registry.LookupMemberRoutine(type: ownerType,
                    memberRoutineName: lookupMemberRoutineName,
                    isFailable: isFailable);
            }

            return Registry.LookupMemberRoutine(type: ownerType,
                memberRoutineName: lookupMemberRoutineName,
                isFailable: isFailable);
        }

        private static string NormalizeMemberRoutineLookupName(string memberRoutineName)
        {
            int dotIndex = memberRoutineName.LastIndexOf(value: '.');
            return dotIndex >= 0 && dotIndex + 1 < memberRoutineName.Length
                ? memberRoutineName[(dotIndex + 1)..]
                : memberRoutineName;
        }

        public RoutineInfo? ResolveCallRoutine(CallExpression call, List<TypeSymbol> callArgTypes)
        {
            if (Registry == null)
            {
                return null;
            }

            return call.Callee switch
            {
                MemberExpression member => ResolveMemberCallRoutine(member: member,
                    callArgTypes: callArgTypes),
                IdentifierExpression identifier => ResolveFreeCallRoutine(call: call,
                    identifier: identifier,
                    callArgTypes: callArgTypes),
                _ => null
            };
        }

        private RoutineInfo? ResolveMemberCallRoutine(MemberExpression member,
            List<TypeSymbol> callArgTypes)
        {
            // The receiver's own ResolvedType is the RAW template annotation — for a universal derive
            // (`T.destroy()`) cloned onto a GENERIC owner it is the bare owner GENERIC-DEF (`ListEmittable[T]`),
            // whose own param `T` COLLIDES BY NAME with the template's owner param `T`. Re-resolving it through
            // the name-keyed substitution then wrongly maps `ListEmittable`'s inner `T` and drops the concrete
            // arg — `me.hijack()` resolves on the bare `ListEmittable`, so the chained `.invalidate()` links to
            // the undefined `Hijacked[ListEmittable]` instead of `Hijacked[ListEmittable[S32]]`. When the
            // receiver is a parameter / `me`, its concrete type is already known authoritatively in ParamTypes
            // (set from the enclosing routine's RESOLVED owner/params), so prefer it — no colliding re-resolve.
            TypeSymbol? receiverType =
                member.Object is IdentifierExpression { Name: { } recvName } &&
                ParamTypes.TryGetValue(key: recvName, value: out TypeSymbol? paramRecv)
                    ? paramRecv
                    : ResolveTypeForLookup(original: member.Object.ResolvedType);
            // A const-generic value receiver (e.g. `N` in `Array[T, N]` monomorphized to the value 4)
            // has no memberRoutines of its own — arithmetic/comparison resolves on its underlying numeric
            // type. Mirror CallOverloadResolutionPass's const-generic handling so `N - 1` lowers to a
            // real `U64.sub!` instead of leaving `.sub` unresolved in the monomorphized body.
            if (receiverType is ConstGenericValueTypeSymbol constRecv && Registry != null)
            {
                receiverType = Registry.LookupType(name: constRecv.ExplicitTypeName ?? "U64") ??
                               receiverType;
            }

            if (receiverType == null)
            {
                return null;
            }

            return ResolveMemberRoutineOnConcreteOwner(ownerType: receiverType,
                memberRoutineName: member.MemberName,
                argTypes: callArgTypes,
                isFailable: member.IsFailable);
        }

        private RoutineInfo? ResolveFreeCallRoutine(CallExpression call,
            IdentifierExpression identifier, List<TypeSymbol> callArgTypes)
        {
            // Identifier names are bare; the failable `!` is a structured flag on the call node.
            string callName = identifier.Name;
            bool isFailable = call.IsFailable;

            RoutineInfo? InstantiateFreeRoutine(RoutineInfo candidate)
            {
                if (candidate.IsGenericDefinition && call.TypeArguments is { Count: > 0 })
                {
                    var resolvedTypeArgs = call.TypeArguments
                                               .Select(selector: selector =>
                                                    ResolveTypeExpression(typeExpr: selector))
                                               .Where(predicate: t =>
                                                    t is not null and not ErrorTypeSymbol)
                                               .Cast<TypeSymbol>()
                                               .ToList();

                    if (candidate.GenericParameters?.Count == resolvedTypeArgs.Count)
                    {
                        return Registry.GetOrCreateRoutineResolution(genericDef: candidate,
                            typeArguments: resolvedTypeArgs);
                    }
                }

                return TryInstantiateRoutine(routine: candidate) ?? candidate;
            }

            RoutineInfo? routine = Registry!.LookupRoutineOverload(baseName: callName,
                argTypes: callArgTypes);
            if (routine != null && routine.IsFailable == isFailable)
            {
                return InstantiateFreeRoutine(candidate: routine);
            }

            // A construction call `TypeName(args)` whose (typed) args matched no free-routine overload above
            // must NOT be name-only-rescued to a WRONG-ARITY same-named creator: `BitList(data:, count:,
            // capacity:)` would resolve to the 0-arg `BitList()` — whose OWN body is that very construction —
            // and self-recurse to a StackOverflow. Leave it unresolved so RebindPlainCall keeps the call's
            // SA-stamped construction (ConstructedType). A genuine creator overload was already returned above.
            if (Registry.LookupType(name: callName) is { IsGenericDefinition: false })
            {
                return null;
            }

            routine = Registry.LookupRoutine(fullName: callName, isFailable: isFailable) ??
                      Registry.LookupRoutineByName(name: callName, isFailable: isFailable);
            if (routine != null)
            {
                return InstantiateFreeRoutine(candidate: routine);
            }

            // (A construction call carries the TYPE name as its callee identifier, resolved on the owner
            // above via expressionType; no bare "create"-named identifier is ever produced, so there is
            // no name-based creator fallback here.)

            return null;
        }

        /// <summary>
        /// Re-instantiates a memberRoutine-level generic routine (e.g. <c>Hijacked[T].recast_as[U]</c>) from
        /// its EXPLICIT rewritten type-argument expressions. The generic-def <c>recast_as</c> stays
        /// bound after owner substitution because its <c>U</c> comes from the call's own
        /// <c>[U]</c> type-argument, not from an operand type — so resolve those args to concrete
        /// TypeInfos and materialize the concrete routine. Returns null when the routine is not a
        /// memberRoutine-generic definition or the args can't be fully resolved.
        /// </summary>
        public RoutineInfo? ResolveMemberRoutineGenericFromTypeArgs(RoutineInfo? routine,
            IReadOnlyList<TypeExpression>? typeArgExprs)
        {
            if (Registry == null || routine == null || typeArgExprs is not { Count: > 0 })
            {
                return null;
            }

            // Resolve the explicit `[U]` type-argument expressions to concrete TypeInfos.
            var explicitArgs = new List<TypeSymbol>(capacity: typeArgExprs.Count);
            foreach (TypeExpression te in typeArgExprs)
            {
                TypeSymbol? arg = te.ResolvedType is { } rt and not ErrorTypeSymbol
                    ? ResolveType(original: rt) ?? rt
                    : ResolveTypeExpression(typeExpr: te);
                if (arg == null || arg is ErrorTypeSymbol || HasGenericParam(t: arg))
                {
                    return null;
                }

                explicitArgs.Add(item: arg);
            }

            // Re-home the memberRoutine on the CONCRETE owner first (so its owner is already bound), then
            // bind its own memberRoutine-generic params from the explicit args. LookupMemberRoutine on a concrete
            // owner returns a form whose GenericParameters are just the memberRoutine's own params (e.g.
            // `[U]`), so CreateInstance keeps the concrete owner — unlike resolving the combined
            // owner+memberRoutine gen-def, which would leave the owner as `Hijacked[T]`.
            RoutineInfo? ownerBound = routine.OwnerType is
                { IsGenericDefinition: false } concreteOwner
                ? Registry.LookupMemberRoutine(type: concreteOwner,
                    memberRoutineName: routine.Name,
                    isFailable: routine.IsFailable)
                : null;
            RoutineInfo? target = ownerBound ?? routine.GenericDefinition ?? routine;
            if (!target.IsGenericDefinition ||
                target.GenericParameters?.Count != explicitArgs.Count)
            {
                return null;
            }

            return Registry.GetOrCreateRoutineResolution(genericDef: target,
                typeArguments: explicitArgs);
        }

        private static bool HasGenericParam(TypeSymbol t)
        {
            if (t is GenericParameterTypeSymbol)
            {
                return true;
            }

            if (t is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
            {
                return true;
            }

            return t.TypeArguments is { Count: > 0 } args &&
                   args.Any(predicate: a => HasGenericParam(t: a));
        }

        public TypeSymbol? ResolveTypeExpressionPublic(TypeExpression typeExpr)
        {
            return ResolveTypeExpression(typeExpr: typeExpr);
        }

        private TypeSymbol? ResolveTypeExpression(TypeExpression typeExpr)
        {
            if (Registry == null)
            {
                return null;
            }

            if (TypeSubs != null &&
                TypeSubs.TryGetValue(key: typeExpr.Name, value: out TypeSymbol? substituted))
            {
                return substituted;
            }

            TypeSymbol? baseType = Registry.LookupType(name: typeExpr.Name);
            if (baseType == null)
            {
                return null;
            }

            if (baseType.IsGenericDefinition && typeExpr.GenericArguments is { Count: > 0 })
            {
                var resolvedArgs = typeExpr.GenericArguments
                                           .Select(selector: selector =>
                                                ResolveTypeExpression(typeExpr: selector))
                                           .Where(predicate: t => t != null)
                                           .Cast<TypeSymbol>()
                                           .ToList();

                if (baseType.GenericParameters?.Count == resolvedArgs.Count)
                {
                    return Registry.GetOrCreateResolution(genericDef: baseType,
                        typeArguments: resolvedArgs);
                }
            }

            return baseType;
        }

        private TypeSymbol? ResolveTypeForLookup(TypeSymbol? original)
        {
            if (original == null)
            {
                return null;
            }

            TypeSymbol resolved = ResolveType(original: original) ?? original;
            if (resolved is WrapperTypeSymbol wrapperType && Registry != null &&
                Registry.LookupType(name: wrapperType.Name) is
                    { IsGenericDefinition: true } wrapperDef && wrapperType.TypeArguments is
                    { Count: > 0 })
            {
                return Registry.TryGetResolution(genericDef: wrapperDef,
                    typeArguments: wrapperType.TypeArguments) ?? resolved;
            }

            return resolved;
        }
    }

    #region Type Rewriting

    private static TypeExpression RewriteType(TypeExpression type, RewriteContext ctx)
    {
        // Associated-type projection `Base/Slot` (e.g. `S/Iter`): resolve the base through the
        // monomorphization type-subs, walk its associated-type binding(s), and emit the concrete
        // bound type. Without this the raw `S/Iter` name survives into codegen (TypeParameter).
        if (type.Name.Contains(value: '/') && ctx.TypeSubs != null)
        {
            TypeExpression? projected = RewriteProjection(type: type, ctx: ctx);
            if (projected != null)
            {
                return projected;
            }
        }

        string name = ctx.StringSubs.TryGetValue(key: type.Name, value: out string? sub)
            ? sub
            : type.Name;
        var args = type.GenericArguments
                      ?.Select(selector: a => RewriteType(type: a, ctx: ctx))
                       .ToList();
        // The TypeExpression's own ResolvedType (SA-annotated on the generic-def AST) may still be a
        // bare `T` / a generic resolution carrying `T`; substitute it so no generic parameter survives
        // on a type-expression node (C1). Falls back to the original when there is nothing to resolve.
        TypeSymbol? resolvedType = ctx.ResolveType(original: type.ResolvedType) ?? type.ResolvedType;

        if (name == type.Name && args == null && type.GenericArguments == null &&
            ReferenceEquals(objA: resolvedType, objB: type.ResolvedType))
        {
            return type; // No change
        }

        return type with { Name = name, GenericArguments = args, ResolvedType = resolvedType };
    }

    /// <summary>
    /// Resolves an associated-type projection type expression (<c>Base/Slot</c>) during
    /// monomorphization: looks up the base in the type substitution map, then walks each slot's
    /// binding on the (now concrete) base. Returns the bound type as an expression (with
    /// <c>ResolvedType</c> set), or null when the base/binding can't be resolved.
    /// </summary>
    private static TypeExpression? RewriteProjection(TypeExpression type, RewriteContext ctx)
    {
        string[] segments = type.Name.Split(separator: '/');
        if (segments.Length < 2 || ctx.TypeSubs is null ||
            !ctx.TypeSubs.TryGetValue(key: segments[0], value: out TypeSymbol? current))
        {
            return null;
        }

        for (int i = 1; i < segments.Length; i++)
        {
            TypeSymbol? bound = RecordTypeSymbol.ProjectAssociatedBinding(baseType: current,
                slot: segments[i]);
            if (bound is null)
            {
                return null;
            }

            current = bound;
        }

        return TypeInfoToTypeExpr(type: current, location: type.Location) with
        {
            ResolvedType = current
        };
    }

    /// <summary>Builds a <see cref="TypeExpression"/> for a (resolved) <see cref="TypeSymbol"/>.</summary>
    private static TypeExpression TypeInfoToTypeExpr(TypeSymbol type, SourceLocation location)
    {
        string baseName = type switch
        {
            RecordTypeSymbol { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeSymbol { GenericDefinition: not null } e => e.GenericDefinition.Name,
            ProtocolTypeSymbol { GenericDefinition: not null } p => p.GenericDefinition.Name,
            _ => type.IsGenericResolution
                ? type.BareName
                : type.Name
        };
        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments
                  .Select(selector: a => TypeInfoToTypeExpr(type: a, location: location))
                  .ToList()
            : null;
        return new TypeExpression(Name: baseName, GenericArguments: args, Location: location);
    }

    #endregion

    #region Expression Rewriting

    private static Expression RewriteExpression(Expression expr, RewriteContext ctx)
    {
        Expression result = RewriteConstructionExpression(expr: expr, ctx: ctx) ??
                            RewriteBuildtimeCallExpression(expr: expr, ctx: ctx) ??
                            RewriteOperatorExpression(expr: expr, ctx: ctx) ??
                            RewriteCompositeExpression(expr: expr, ctx: ctx) ??
                            RewriteLeafExpression(expr: expr, ctx: ctx) ?? expr;

        // Annotate the cloned expression's ResolvedType with the substituted concrete type.
        // This lets codegen's GetExpressionType() return the correct type without falling back
        // on _typeSubstitutions (the mutable global-state fallback).
        if (!ReferenceEquals(objA: result, objB: expr))
        {
            AnnotateRewrittenExpression(result: result, expr: expr, ctx: ctx);
        }

        return result;
    }

    private static Expression? RewriteConstructionExpression(Expression expr, RewriteContext ctx)
    {
        return expr switch
        {
            TypeExpression te => RewriteType(type: te, ctx: ctx),

            GenericMemberRoutineCallExpression gmc => gmc with
            {
                Object = RewriteExpression(expr: gmc.Object, ctx: ctx),
                TypeArguments = gmc.TypeArguments
                                   .Select(selector: a => RewriteType(type: a, ctx: ctx))
                                   .ToList(),
                Arguments = gmc.Arguments
                               .Select(selector: a => RewriteExpression(expr: a, ctx: ctx))
                               .ToList()
            },

            CreatorExpression creator => creator with
            {
                TypeName =
                ctx.StringSubs.TryGetValue(key: creator.TypeName, value: out string? cSub)
                    ? cSub
                    : creator.TypeName,
                TypeArguments = creator.TypeArguments
                                      ?.Select(selector: a => RewriteType(type: a, ctx: ctx))
                                       .ToList(),
                MemberVariables = creator.MemberVariables
                                         .Select(selector: mv => (mv.Name,
                                              Value: RewriteExpression(expr: mv.Value, ctx: ctx)))
                                         .ToList(),
                // The cloned stdlib body is SA-annotated: the resolved ConstructedType carries
                // `Me`/projection types that codegen uses directly. Substitute them too, else a
                // construction like `EnumerateIterator[T, Me]` leaks `ProtocolSelf` into codegen.
                ConstructedType =
                ctx.ResolveType(original: creator.ConstructedType) ?? creator.ConstructedType,
                ResolvedType = ctx.ResolveType(original: creator.ResolvedType) ??
                               creator.ResolvedType
            },

            TypeConversionExpression tce => tce with
            {
                TargetType =
                ctx.StringSubs.TryGetValue(key: tce.TargetType, value: out string? tSub)
                    ? tSub
                    : tce.TargetType,
                Expression = RewriteExpression(expr: tce.Expression, ctx: ctx)
            },

            ListLiteralExpression lle => lle with
            {
                Elements = lle.Elements
                              .Select(selector: e => RewriteExpression(expr: e, ctx: ctx))
                              .ToList(),
                ElementType = lle.ElementType != null
                    ? RewriteType(type: lle.ElementType, ctx: ctx)
                    : null
            },

            SetLiteralExpression sle => sle with
            {
                Elements = sle.Elements
                              .Select(selector: e => RewriteExpression(expr: e, ctx: ctx))
                              .ToList(),
                ElementType = sle.ElementType != null
                    ? RewriteType(type: sle.ElementType, ctx: ctx)
                    : null
            },

            DictLiteralExpression dle => dle with
            {
                Pairs = dle.Pairs
                           .Select(selector: p => (Key: RewriteExpression(expr: p.Key, ctx: ctx),
                                Value: RewriteExpression(expr: p.Value, ctx: ctx)))
                           .ToList(),
                KeyType = dle.KeyType != null
                    ? RewriteType(type: dle.KeyType, ctx: ctx)
                    : null,
                ValueType = dle.ValueType != null
                    ? RewriteType(type: dle.ValueType, ctx: ctx)
                    : null
            },

            _ => null
        };
    }

    private static Expression? RewriteBuildtimeCallExpression(Expression expr, RewriteContext ctx)
    {
        return expr switch
        {
            // Buildtime type test `T is X` (T a generic parameter concrete in this instantiation) folds
            // to a bool literal — so `if T is X` selects a branch with no runtime IsPatternExpression.
            IsPatternExpression { Pattern: TypePattern tsp } ipeType when !ctx.CloneOnly &&
                TryEvalTypeParamIs(
                    operand: ipeType.Expression,
                    typePattern: tsp,
                    ctx: ctx) is bool matched => MakeBoolLiteral(value: matched ^ ipeType.IsNegated,
                ctx: ctx,
                loc: ipeType.Location),

            IsPatternExpression ipe => ipe with
            {
                Expression = RewriteExpression(expr: ipe.Expression, ctx: ctx),
                Pattern = RewritePattern(pattern: ipe.Pattern, ctx: ctx)
            },

            // Fold T.BS_ROUTINE() -> compile-time literal during monomorphization.
            // After substituting T -> Byte (or S64 etc.), the identifier is now a concrete
            // type name. BuilderQueryInliningPass handles the static/concrete cases:
            // this fold handles the residual case where the receiver name still matches a
            // type-param string substitution (e.g., the receiver is IdentifierExpression("T")
            // and stringSubs["T"] = "Core.Byte" -> the name hasn't been rewritten yet when
            // the switch arm fires).
            CallExpression
                {
                    Callee: MemberExpression { MemberName: var bsName } bsCallee,
                    Arguments: { Count: 0 }
                } bsCall when
                !ctx.CloneOnly && ctx.Registry != null &&
                BuilderQueryInliningPass.IsFoldable(routineName: bsName) =>
                TryFoldBsCallViaStringSubs(callee: bsCallee,
                    location: bsCall.Location,
                    ctx: ctx) ??
                bsCall with
                {
                    Callee = RewriteExpression(expr: bsCall.Callee, ctx: ctx),
                    Arguments = [],
                    TypeArguments = null
                },

            // Buildtime metadata intrinsic: nameof(m) / orderof(m) / typeof(m) / typeidof(m) / valueof(c) /
            // placeof(m) / sizeof(m|T) -> folded off the active expand-unroll context. Placed BEFORE the
            // generic CallExpression clone so it never resolves as a real routine call.
            CallExpression
                {
                    Callee: IdentifierExpression ofId, Arguments: [Expression ofArg]
                } ofCall when !ctx.CloneOnly &&
                              Verification.SemanticVerifier.IsMetadataIntrinsic(name: ofId.Name) &&
                              IsFoldableMetadataArg(name: ofId.Name, arg: ofArg, ctx: ctx) =>
                FoldMetadataIntrinsic(name: ofId.Name,
                    arg: ofArg,
                    ctx: ctx,
                    location: ofCall.Location),

            CallExpression call => CloneCall(call: call, ctx: ctx),

            // Buildtime splice selector: x.${m.name} -> real member access on the current field.
            // CloneOnly: keep the splice node intact (structural clone) — folding it needs the expand
            // context a bare template clone doesn't have.
            SpliceMemberExpression sm when !ctx.CloneOnly => RewriteSpliceMember(sm: sm, ctx: ctx),

            SpliceMemberExpression sm => sm with
            {
                Object = RewriteExpression(expr: sm.Object, ctx: ctx),
                Selector = (SpliceExpression)RewriteExpression(expr: sm.Selector, ctx: ctx)
            },

            // Buildtime splice in expression position: fold the inner projection (real subs only).
            SpliceExpression se when !ctx.CloneOnly => RewriteExpression(expr: se.Inner, ctx: ctx),

            SpliceExpression se => se with
            {
                Inner = RewriteExpression(expr: se.Inner, ctx: ctx)
            },

            MemberExpression me => CloneMember(me: me, ctx: ctx),

            _ => null
        };
    }

    private static Expression? RewriteOperatorExpression(Expression expr, RewriteContext ctx)
    {
        return expr switch
        {
            OptionalMemberExpression ome => ome with
            {
                Object = RewriteExpression(expr: ome.Object, ctx: ctx)
            },

            GenericMemberExpression gme => gme with
            {
                Object = RewriteExpression(expr: gme.Object, ctx: ctx),
                TypeArguments = gme.TypeArguments
                                   .Select(selector: a => RewriteType(type: a, ctx: ctx))
                                   .ToList()
            },

            IndexExpression idx => idx with
            {
                Object = RewriteExpression(expr: idx.Object, ctx: ctx),
                Index = RewriteExpression(expr: idx.Index, ctx: ctx)
            },

            BinaryExpression bin => bin with
            {
                Left = RewriteExpression(expr: bin.Left, ctx: ctx),
                Right = RewriteExpression(expr: bin.Right, ctx: ctx)
            },

            UnaryExpression un => un with
            {
                Operand = RewriteExpression(expr: un.Operand, ctx: ctx)
            },

            CompoundAssignmentExpression ca => ca with
            {
                Target = RewriteExpression(expr: ca.Target, ctx: ctx),
                Value = RewriteExpression(expr: ca.Value, ctx: ctx)
            },

            ConditionalExpression cond => cond with
            {
                Condition = RewriteExpression(expr: cond.Condition, ctx: ctx),
                TrueExpression = RewriteExpression(expr: cond.TrueExpression, ctx: ctx),
                FalseExpression = RewriteExpression(expr: cond.FalseExpression, ctx: ctx)
            },

            StealExpression steal => steal with
            {
                Operand = RewriteExpression(expr: steal.Operand, ctx: ctx)
            },

            BlockExpression block => block with
            {
                Value = RewriteExpression(expr: block.Value, ctx: ctx)
            },

            LambdaExpression lambda => lambda with
            {
                Parameters = lambda.Parameters
                                   .Select(selector: p => RewriteParameter(param: p, ctx: ctx))
                                   .ToList(),
                Body = RewriteExpression(expr: lambda.Body, ctx: ctx)
            },

            TupleLiteralExpression tuple => tuple with
            {
                Elements = tuple.Elements
                                .Select(selector: e => RewriteExpression(expr: e, ctx: ctx))
                                .ToList()
            },

            _ => null
        };
    }

    private static Expression? RewriteCompositeExpression(Expression expr, RewriteContext ctx)
    {
        return expr switch
        {
            RangeExpression range => range with
            {
                Start = RewriteExpression(expr: range.Start, ctx: ctx),
                End = RewriteExpression(expr: range.End, ctx: ctx),
                Step = range.Step != null
                    ? RewriteExpression(expr: range.Step, ctx: ctx)
                    : null
            },

            ChainedComparisonExpression chain => chain with
            {
                Operands = chain.Operands
                                .Select(selector: o => RewriteExpression(expr: o, ctx: ctx))
                                .ToList()
            },

            WithExpression we => we with
            {
                Base = RewriteExpression(expr: we.Base, ctx: ctx),
                Updates = we.Updates
                            .Select(selector: u => (u.MemberVariablePath, u.Index != null
                                 ? RewriteExpression(expr: u.Index, ctx: ctx)
                                 : null, RewriteExpression(expr: u.Value, ctx: ctx)))
                            .ToList()
            },

            InsertedTextExpression ite => ite with
            {
                Parts = ite.Parts
                           .Select(selector: p => RewriteInsertedTextPart(part: p, ctx: ctx))
                           .ToList()
            },

            DictEntryLiteralExpression del => del with
            {
                Key = RewriteExpression(expr: del.Key, ctx: ctx),
                Value = RewriteExpression(expr: del.Value, ctx: ctx)
            },

            WaitforExpression wf => wf with
            {
                Operand = RewriteExpression(expr: wf.Operand, ctx: ctx),
                Timeout = wf.Timeout != null
                    ? RewriteExpression(expr: wf.Timeout, ctx: ctx)
                    : null
            },

            DependentWaitforExpression dwf => dwf with
            {
                Dependencies = dwf.Dependencies
                                  .Select(selector: d => d with
                                   {
                                       DependencyExpr =
                                       RewriteExpression(expr: d.DependencyExpr, ctx: ctx)
                                   })
                                  .ToList(),
                Operand = RewriteExpression(expr: dwf.Operand, ctx: ctx),
                Timeout = dwf.Timeout != null
                    ? RewriteExpression(expr: dwf.Timeout, ctx: ctx)
                    : null
            },

            BackIndexExpression bi => bi with
            {
                Operand = RewriteExpression(expr: bi.Operand, ctx: ctx)
            },

            WhenExpression we => we with
            {
                Expression = we.Expression != null
                    ? RewriteExpression(expr: we.Expression, ctx: ctx)
                    : null,
                Clauses = we.Clauses
                            .Select(selector: c => RewriteWhenClause(clause: c, ctx: ctx))
                            .ToList()
            },

            _ => null
        };
    }

    private static Expression? RewriteLeafExpression(Expression expr, RewriteContext ctx)
    {
        return expr switch
        {
            FlagsTestExpression fte => fte with
            {
                Subject = RewriteExpression(expr: fte.Subject, ctx: ctx)
            },

            // Carrier pattern expressions (generated by CrashableExpansionPass + PatternLoweringPass)
            CarrierPayloadExpression cpe => cpe with
            {
                Carrier = RewriteExpression(expr: cpe.Carrier, ctx: ctx)
            },

            // Crashable-dispatch (generated by CrashableExpansionPass): substitute the carrier so the
            // monomorphized clone's `me` receiver carries the concrete Result[<T>] type at codegen.
            CrashableDispatchExpression cde => cde with
            {
                Carrier = RewriteExpression(expr: cde.Carrier, ctx: ctx)
            },

            NamedArgumentExpression nae => nae with
            {
                Value = RewriteExpression(expr: nae.Value, ctx: ctx)
            },

            IdentifierExpression identifier => identifier with { },

            // Leaf nodes have no children, but their ResolvedType may still carry a generic
            // parameter (e.g. `none : Maybe[Hijacked[BTreeListNode[T]]]`). Clone so the ResolvedType
            // substitution block below runs (it is gated on a fresh reference).
            LiteralExpression literal => literal with { },

            _ => null
        };
    }

    /// <summary>
    /// After an expression is cloned+substituted, backfills its <see cref="Expression.ResolvedType"/>
    /// with the concrete type and re-binds any resolved routine / constructed type carried by the node.
    /// Mutates <paramref name="result"/> in place.
    /// </summary>
    private static void AnnotateRewrittenExpression(Expression result, Expression expr,
        RewriteContext ctx)
    {
        TypeSymbol? resolvedType = RefineResolvedType(
            initial: ctx.ResolveType(original: expr.ResolvedType) ?? expr.ResolvedType,
            result: result,
            expr: expr,
            ctx: ctx);

        result.ResolvedType = resolvedType;

        TypeSymbol? routineResultType = resolvedType ?? result.ResolvedType ?? expr.ResolvedType;
        RebindRewrittenNode(result: result,
            expr: expr,
            routineResultType: routineResultType,
            ctx: ctx);

        // Stdlib bodies are processed by SA on the generic definition, so some
        // intermediate call expressions (e.g. me.address() inside cmp or diagnose)
        // may arrive with ResolvedType=null when the SA annotation on the generic
        // body's call was not preserved through cloning.  If GMP resolved the routine
        // after the switch, propagate its ReturnType so downstream chained calls
        // (outer .MemberRoutine() or CallOverloadResolutionPass) can see the receiver type.
        if (result.ResolvedType == null && result is CallExpression
            {
                ResolvedRoutine.ReturnType: { } inferredReturnType
            })
        {
            result.ResolvedType = inferredReturnType;
        }
    }

    /// <summary>
    /// Applies a chain of special-case overrides to the initial resolved type, in priority order:
    /// const-generic identifier, <c>me</c> receiver, branchof binding, stale-protocol local,
    /// SoA splice column, and typewise fold.
    /// </summary>
    private static TypeSymbol? RefineResolvedType(TypeSymbol? initial, Expression result,
        Expression expr, RewriteContext ctx)
    {
        TypeSymbol? resolvedType = initial;

        // Const-generic identifiers (e.g. N in Array[T, N]) have ResolvedType=null in the
        // generic body because SA doesn't run on stdlib bodies before Phase 6.  After GMP
        // substitutes N -> ConstGenericValueTypeSymbol("63"), annotate the rewritten identifier
        // so CallOverloadResolutionPass can resolve operator calls like N.sub!(1u64).
        if (resolvedType == null && result is IdentifierExpression cgIdent &&
            ctx.TypeSubs?.TryGetValue(key: cgIdent.Name, value: out TypeSymbol? cgSub) == true &&
            cgSub is ConstGenericValueTypeSymbol)
        {
            resolvedType = cgSub;
        }

        // `me` (the receiver) in a re-homed protocol-default-impl body must be the concrete
        // implementer (ParamTypes["me"]), not the protocol's substituted element type.
        if (result is IdentifierExpression { Name: "me" } &&
            ctx.ParamTypes.TryGetValue(key: "me", value: out TypeSymbol? meReceiverType))
        {
            resolvedType = meReceiverType;
        }

        // An `branchof` arm payload binding (`x` in `is ${m.type} x => …`) has no SA annotation on
        // the generic template — supply the concrete arm type so the chained call re-resolves.
        if (resolvedType is null or ErrorTypeSymbol && result is IdentifierExpression bindingRef &&
            ctx.ActiveBindingTypes.TryGetValue(key: bindingRef.Name,
                value: out TypeSymbol? bindType))
        {
            resolvedType = bindType;
        }

        // Concretize a reference to a local whose type was re-inferred from a re-dispatched
        // initializer. Guarded to only replace a stale PROTOCOL type so concrete references are left untouched.
        if (result is IdentifierExpression localRef && resolvedType is ProtocolTypeSymbol &&
            ctx.LocalReinferredTypes.TryGetValue(key: localRef.Name,
                value: out TypeSymbol? concreteLocal))
        {
            resolvedType = concreteLocal;
        }

        return RefineSoAAndTypewiseType(resolvedType: resolvedType, result: result, expr: expr,
            ctx: ctx);
    }

    /// <summary>
    /// Applies the final two override layers: SoA splice column type preservation and typewise fold
    /// concrete-type preservation. Both share the same shape (keep <c>result.ResolvedType</c> when
    /// the computed <paramref name="resolvedType"/> would otherwise be null or an error sentinel).
    /// </summary>
    private static TypeSymbol? RefineSoAAndTypewiseType(TypeSymbol? resolvedType, Expression result,
        Expression expr, RewriteContext ctx)
    {
        // A `${m.name}` splice on a SoA container resolves to the COLUMN type, not the source member
        // type. Preserve the splice-computed type when the object is an SoA container.
        if (expr is SpliceMemberExpression && result is MemberExpression spliceMember &&
            result.ResolvedType is not (null or ErrorTypeSymbol) &&
            IsSoAContainerType(type: ResolveSpliceObjectType(obj: spliceMember.Object, ctx: ctx)))
        {
            resolvedType = result.ResolvedType;
        }

        // A folded buildtime type projection yields a TYPEWISE IdentifierExpression; it must keep that
        // concrete type rather than being overwritten with the source's deferred ErrorTypeSymbol placeholder.
        bool exprFoldsTypewise = FoldsTypewise(expr: expr);
        if (resolvedType is null or ErrorTypeSymbol &&
            result.ResolvedType is not (null or ErrorTypeSymbol) && exprFoldsTypewise)
        {
            resolvedType = result.ResolvedType;
        }

        return resolvedType;
    }

    /// <summary>
    /// Re-binds the resolved routine and constructed type of a cloned node (call / creator /
    /// conversion / generic memberRoutine call) against the concrete monomorphization context.
    /// Mutates <paramref name="result"/> in place.
    /// </summary>
    private static void RebindRewrittenNode(Expression result, Expression expr,
        TypeSymbol? routineResultType, RewriteContext ctx)
    {
        switch (result)
        {
            case CallExpression { ResolvedRoutine: not null } call:
                RebindResolvedCall(call: call,
                    expr: expr,
                    routineResultType: routineResultType,
                    ctx: ctx);
                break;

            case CallExpression call:
                RebindPlainCall(call: call, expr: expr, ctx: ctx);
                break;

            case CreatorExpression creator:
                RebindCreator(creator: creator, expr: expr, ctx: ctx);
                break;

            case TypeConversionExpression conversion:
                conversion.ConstructedType =
                    ctx.ResolveType(original: conversion.ConstructedType) ??
                    conversion.ConstructedType ??
                    (expr is TypeConversionExpression originalConversion
                        ? originalConversion.ConstructedType
                        : null);
                break;

            // A type-construction GMC (`WhereIterable[T, Me](...)`) carries a ConstructedType but
            // NO ResolvedRoutine (it constructs via ConstructedType, not a routine call), so it
            // must still have its ConstructedType concretized here — otherwise the generic-def
            // struct name reaches codegen's GEP. Resolve it, falling back to the already-concrete
            // ResolvedType when the bare def can't be resolved from this position.
            case GenericMemberRoutineCallExpression { ResolvedRoutine: null } ctorCall
                when ctorCall.ConstructedType is { } ctorCt:
                ctorCall.ConstructedType = ctx.ResolveType(original: ctorCt) ??
                                           (ctorCt.IsGenericResolution ||
                                            ctorCt.IsGenericDefinition
                                               ? ctx.ResolveType(
                                                     original: ctorCall.ResolvedType) ??
                                                 ctorCall.ResolvedType ?? ctorCt
                                               : ctorCt);
                break;

            case GenericMemberRoutineCallExpression { ResolvedRoutine: not null } genericCall:
                RebindGenericMemberRoutineCall(genericCall: genericCall,
                    expr: expr,
                    routineResultType: routineResultType,
                    ctx: ctx);
                break;
        }
    }

    /// <summary>Re-binds a cloned <see cref="CallExpression"/> that already carries a resolved routine.</summary>
    private static void RebindResolvedCall(CallExpression call, Expression expr,
        TypeSymbol? routineResultType, RewriteContext ctx)
    {
        call.ConstructedType = ctx.ResolveType(original: call.ConstructedType) ??
                               call.ConstructedType ??
                               (expr is CallExpression originalCallWithRoutine
                                   ? originalCallWithRoutine.ConstructedType
                                   : null);
        var callArgTypes = call.Arguments
                               .Select(selector: a => (a is NamedArgumentExpression nae
                                    ? nae.Value
                                    : a).ResolvedType)
                               .Where(predicate: t => t != null)
                               .Cast<TypeSymbol>()
                               .ToList();
        RoutineInfo? rewrittenRoutine = ctx.ResolveRoutine(original: call.ResolvedRoutine,
            expressionType: routineResultType,
            callArgTypes: callArgTypes);
        if (rewrittenRoutine == null || CallRoutineNeedsRebinding(routine: rewrittenRoutine))
        {
            rewrittenRoutine = ctx.ResolveCallRoutine(call: call, callArgTypes: callArgTypes) ??
                               rewrittenRoutine;
        }

        // A memberRoutine-generic callee whose type param is supplied by an explicit
        // `.MemberRoutine[U]()` type-argument (e.g. `recast_as[T]`) stays generic after
        // owner/arg resolution — re-instantiate from the callee's rewritten type args.
        rewrittenRoutine = ReinstantiateMemberRoutineGenericCallee(
            call: call,
            resolved: rewrittenRoutine,
            ctx: ctx);

        call.ResolvedRoutine = rewrittenRoutine ?? call.ResolvedRoutine;
    }

    /// <summary>Re-binds a cloned <see cref="CallExpression"/> that has no resolved routine yet.</summary>
    private static void RebindPlainCall(CallExpression call, Expression expr, RewriteContext ctx)
    {
        call.ConstructedType = ctx.ResolveType(original: call.ConstructedType) ??
                               call.ConstructedType ?? (expr is CallExpression originalCall
                                   ? originalCall.ConstructedType
                                   : null);
        var callArgTypes = call.Arguments
                               .Select(selector: a => (a is NamedArgumentExpression nae
                                    ? nae.Value
                                    : a).ResolvedType)
                               .Where(predicate: t => t != null)
                               .Cast<TypeSymbol>()
                               .ToList();
        RoutineInfo? plainResolved =
            ctx.ResolveCallRoutine(call: call, callArgTypes: callArgTypes) ?? call.ResolvedRoutine;
        call.ResolvedRoutine =
            ReinstantiateMemberRoutineGenericCallee(call: call,
                resolved: plainResolved,
                ctx: ctx) ?? plainResolved;
    }

    /// <summary>Re-binds a cloned <see cref="CreatorExpression"/>'s constructed type and creator routine.</summary>
    private static void RebindCreator(CreatorExpression creator, Expression expr,
        RewriteContext ctx)
    {
        creator.ConstructedType = ctx.ResolveType(original: creator.ConstructedType) ??
                                  creator.ConstructedType ??
                                  (expr is CreatorExpression originalCreator
                                      ? originalCreator.ConstructedType
                                      : null);
        if (creator.ResolvedCreatorRoutine != null)
        {
            // Rewriting `List[T](capacity: …)` → `List[S64](capacity: …)` updates
            // TypeArguments/ConstructedType above, but `ResolvedCreatorRoutine`
            // still points at the generic-def `List[T].create`. Codegen reads
            // `creatorRoutine.FullName` directly, so without this resolve the
            // emitted call references the unsubstituted symbol and links fail.
            creator.ResolvedCreatorRoutine = ctx.ResolveRoutine(
                original: creator.ResolvedCreatorRoutine,
                expressionType: creator.ConstructedType) ?? creator.ResolvedCreatorRoutine;
        }
    }

    /// <summary>Re-binds a cloned <see cref="GenericMemberRoutineCallExpression"/> that carries a resolved routine.</summary>
    private static void RebindGenericMemberRoutineCall(
        GenericMemberRoutineCallExpression genericCall, Expression expr,
        TypeSymbol? routineResultType, RewriteContext ctx)
    {
        genericCall.ConstructedType = ctx.ResolveType(original: genericCall.ConstructedType) ??
                                      genericCall.ConstructedType ??
                                      (expr is GenericMemberRoutineCallExpression
                                          originalGenericCall
                                          ? originalGenericCall.ConstructedType
                                          : null);
        RoutineInfo? gcResolved = ctx.ResolveRoutine(original: genericCall.ResolvedRoutine,
            expressionType: routineResultType);
        // If the routine is a memberRoutine-generic (`recast_as[U]`) whose `U` comes from the
        // explicit `[U]` type-argument, owner-based resolution can't concretize it —
        // re-instantiate from the (rewritten) explicit type-argument expressions.
        if (gcResolved is null or { IsGenericDefinition: true } or
            { OwnerType.IsGenericDefinition: true })
        {
            gcResolved = ctx.ResolveMemberRoutineGenericFromTypeArgs(
                routine: genericCall.ResolvedRoutine,
                typeArgExprs: genericCall.TypeArguments) ?? gcResolved;
        }

        genericCall.ResolvedRoutine = gcResolved ?? genericCall.ResolvedRoutine;
    }

    /// <summary>
    /// When a call's resolved routine is still a memberRoutine-generic definition (its type param comes
    /// from an explicit <c>.MemberRoutine[U]()</c> type-argument, not an operand), re-instantiate it from
    /// the callee's rewritten type arguments. Returns <paramref name="resolved"/> unchanged when it
    /// is already concrete or no explicit type arguments are available.
    /// </summary>
    private static RoutineInfo? ReinstantiateMemberRoutineGenericCallee(CallExpression call,
        RoutineInfo? resolved, RewriteContext ctx)
    {
        if (!IsUnconcretizedMemberRoutineGeneric(routine: resolved))
        {
            return resolved;
        }

        IReadOnlyList<TypeExpression>? typeArgs = call.Callee switch
        {
            GenericMemberExpression gme => gme.TypeArguments,
            _ => call.TypeArguments
        };
        return ctx.ResolveMemberRoutineGenericFromTypeArgs(
            routine: resolved ?? call.ResolvedRoutine,
            typeArgExprs: typeArgs) ?? resolved;
    }

    /// <summary>
    /// True when a resolved routine still needs its memberRoutine-level type param bound from an explicit
    /// call type-argument: a generic definition, owned by one, or (concrete-owner case like
    /// <c>Hijacked[BTreeListNode[S64]].recast_as() -&gt; Hijacked[U]</c>) still carrying a generic
    /// parameter in its return or parameter types.
    /// </summary>
    private static bool IsUnconcretizedMemberRoutineGeneric(RoutineInfo? routine)
    {
        if (routine is null or { IsGenericDefinition: true } or
            { OwnerType.IsGenericDefinition: true })
        {
            return routine != null;
        }

        if (routine.ReturnType != null && TypeHasGenericParam(t: routine.ReturnType))
        {
            return true;
        }

        return routine.Parameters.Any(predicate: p =>
            p.Type != null && TypeHasGenericParam(t: p.Type));
    }

    private static bool TypeHasGenericParam(TypeSymbol t)
    {
        if (t is GenericParameterTypeSymbol)
        {
            return true;
        }

        if (t is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
        {
            return true;
        }

        return t.TypeArguments is { Count: > 0 } a &&
               a.Any(predicate: x => TypeHasGenericParam(t: x));
    }

    private static CallExpression CloneCall(CallExpression call, RewriteContext ctx)
    {
        Expression rewrittenCallee = RewriteExpression(expr: call.Callee, ctx: ctx);
        // `T(0)` parses as CallExpression(Callee=IdentifierExpression("T")). The plain
        // IdentifierExpression rewrite arm doesn't substitute names (variables vs. type
        // params are ambiguous). For a CallExpression callee that *is* a generic-param
        // identifier, swap "T" -> "Core.S64" so codegen emits the right constructor.
        if (rewrittenCallee is IdentifierExpression idCallee &&
            ctx.StringSubs.TryGetValue(key: idCallee.Name, value: out string? sub))
        {
            rewrittenCallee = idCallee with { Name = sub };
        }

        CallExpression rewritten = call with
        {
            Callee = rewrittenCallee,
            Arguments = call.Arguments
                            .Select(selector: a => RewriteExpression(expr: a, ctx: ctx))
                            .ToList(),
            TypeArguments = call.TypeArguments
                               ?.Select(selector: ta => RewriteType(type: ta, ctx: ctx))
                                .ToList()
        };
        rewritten.ResolvedRoutine = call.ResolvedRoutine;
        rewritten.LoweringKind = call.LoweringKind;
        rewritten.ConstructedType = call.ConstructedType;
        rewritten.IsCollectionLiteral = call.IsCollectionLiteral;
        rewritten.ResolvedType = call.ResolvedType;
        return rewritten;
    }

    private static MemberExpression CloneMember(MemberExpression me, RewriteContext ctx)
    {
        Expression newObj = RewriteExpression(expr: me.Object, ctx: ctx);
        if (newObj.ResolvedType == null && newObj is IdentifierExpression idObj &&
            ctx.ParamTypes.TryGetValue(key: idObj.Name, value: out TypeSymbol? paramType))
        {
            newObj.ResolvedType = paramType;
        }

        // TYPE-PARAM MEMBER RECEIVER: `T.blank()` — the receiver is a bare generic PARAMETER identifier.
        // The general IdentifierExpression rewrite arm deliberately does NOT rename bare identifiers (a
        // var vs. a type param are ambiguous there), so a type-param used as a member-call receiver stays
        // literally "T" after substitution → codegen sees an unresolved `T.member()`. Here the position is
        // UNAMBIGUOUS (a member receiver), so substitute it to the concrete type it binds to (TypeSubs["T"])
        // — rename + carry the ResolvedType so the static/wired member resolves against the real type.
        if (newObj is IdentifierExpression { ResolvedType: null } tpObj && ctx.TypeSubs != null &&
            ctx.TypeSubs.TryGetValue(key: tpObj.Name, value: out TypeSymbol? boundTy) &&
            boundTy is { IsGenericDefinition: false })
        {
            newObj = tpObj with { Name = boundTy.Name };
            newObj.ResolvedType = boundTy;
        }

        MemberExpression rewritten = me with { Object = newObj };
        // Substitute the member-access type through the type map: a field read `me.inner` typed
        // `Core.List[T]` on the generic def must become `Core.List[Core.S32]` in the instantiation.
        // Copying the raw generic-def type left `T` unsubstituted, so a downstream re-resolution of the
        // unresolved-`T` receiver mis-bound the memberRoutine to the shadowing same-named type (the SF-overlay
        // wrapper) → self-recursion.
        rewritten.ResolvedType = ctx.ResolveType(original: me.ResolvedType) ?? me.ResolvedType;
        return rewritten;
    }

    //  BuilderQuery constant folding

    /// <summary>
    /// Attempts to fold <c>T.BS_ROUTINE()</c> to a literal expression when the receiver
    /// can be resolved through the string-substitution map (e.g. T ??"Core.Byte").
    /// This covers the monomorphization case where the receiver name hasn't been rewritten
    /// to the concrete identifier yet when the switch arm fires.
    /// Returns null if the type cannot be resolved (caller falls back to normal rewrite).
    /// </summary>
    private static Expression? TryFoldBsCallViaStringSubs(MemberExpression callee,
        SourceLocation location, RewriteContext ctx)
    {
        // Resolve the receiver name through the string substitution map first,
        // then fall back to the literal identifier name (already concrete).
        // Handles both IdentifierExpression("T") and TypeExpression("T") receivers ??
        // the RF parser may produce either depending on context.
        string? typeName = callee.Object switch
        {
            IdentifierExpression { Name: "me" } when ctx.ParamTypes.TryGetValue(key: "me",
                value: out TypeSymbol? meType) => meType.FullName,
            IdentifierExpression id when ctx.StringSubs.TryGetValue(key: id.Name,
                value: out string? sub) => sub,
            IdentifierExpression id => id.Name,
            TypeExpression te when ctx.StringSubs.TryGetValue(key: te.Name,
                value: out string? teSub) => teSub,
            TypeExpression te => te.Name,
            _ => null
        };
        if (typeName == null)
        {
            return null;
        }

        TypeSymbol? typeInfo = ctx.Registry!.LookupType(name: typeName);
        if (typeInfo == null)
        {
            return null;
        }

        // Delegate actual folding to BuilderQueryInliningPass so both code paths
        // use identical constant-computation logic.
        TypeSymbol? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeSymbol? s64Type = ctx.Registry.LookupType(name: "S64");
        TypeSymbol? textType = ctx.Registry.LookupType(name: "Text");
        TypeSymbol? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeSymbol? byteSizeType = ctx.Registry.LookupType(name: "ByteSize");

        return callee.MemberName switch
        {
            RuntimeContract.DataSize when u64Type != null && byteSizeType != null =>
                BuilderQueryInliningPass.MakeByteSizeCreatorPublic(
                    value: BuilderQueryInliningPass.CalculateDataSizeForType(type: typeInfo),
                    u64Type: u64Type,
                    byteSizeType: byteSizeType,
                    loc: location),

            TypeIdProjection when u64Type != null => new LiteralExpression(
                Value: TypeIdHelper.ComputeTypeId(fullName: typeInfo.FullName),
                LiteralType: TokenType.U64Literal,
                Location: location) { ResolvedType = u64Type },

            "type_name" when textType != null => new LiteralExpression(
                Value: typeInfo.ShortTypeName,
                LiteralType: TokenType.TextLiteral,
                Location: location) { ResolvedType = textType },

            "module_name" when textType != null => new LiteralExpression(
                Value: typeInfo.Module ?? "",
                LiteralType: TokenType.TextLiteral,
                Location: location) { ResolvedType = textType },

            "full_type_name" when textType != null => new LiteralExpression(
                Value: typeInfo.QualifiedTypeName,
                LiteralType: TokenType.TextLiteral,
                Location: location) { ResolvedType = textType },

            "member_variable_count" when s64Type != null => new LiteralExpression(
                Value: (long)(typeInfo switch
                {
                    TupleTypeSymbol t => t.MemberVariables.Count,
                    ChoiceTypeSymbol ch => ch.Cases.Count,
                    FlagsTypeSymbol f => f.Members.Count,
                    VariantTypeSymbol v => v.Members.Count,
                    RecordTypeSymbol r => r.MemberVariables.Count,
                    EntityTypeSymbol e => e.MemberVariables.Count,
                    _ => 0
                }),
                LiteralType: TokenType.S64Literal,
                Location: location) { ResolvedType = s64Type },

            "is_generic" when boolType != null => new LiteralExpression(
                Value: typeInfo.IsGenericDefinition,
                LiteralType: typeInfo.IsGenericDefinition
                    ? TokenType.True
                    : TokenType.False,
                Location: location) { ResolvedType = boolType },

            // is_in_flight: monomorphized receivers reaching this rewriter are bound — folder at
            // BSInliningPass handles in-flight literal receivers earlier in the pipeline.
            "is_in_flight" when boolType != null => new LiteralExpression(Value: false,
                LiteralType: TokenType.False,
                Location: location) { ResolvedType = boolType },

            _ => null
        };
    }

    private static InsertedTextPart RewriteInsertedTextPart(InsertedTextPart part,
        RewriteContext ctx)
    {
        return part switch
        {
            ExpressionPart ep => ep with
            {
                Expression = RewriteExpression(expr: ep.Expression, ctx: ctx)
            },
            _ => part // TextPart has no expressions
        };
    }

    private static bool CallRoutineNeedsRebinding(RoutineInfo routine)
    {
        if (routine.IsGenericDefinition || routine.OwnerType is GenericParameterTypeSymbol
                or ProtocolTypeSymbol or { IsGenericDefinition: true })
        {
            // A protocol-owned memberRoutine is abstract (no body) — after monomorphization the call must
            // re-dispatch to the concrete implementer (e.g. Iterator[S64].try_emit, resolved via a
            // constrained generic param's iter, must rebind to RangeIterator[S64].try_emit).
            return true;
        }

        return ContainsGenericPlaceholder(type: routine.OwnerType) ||
               ContainsGenericPlaceholder(type: routine.ReturnType) || routine.Parameters.Any(
                   predicate: predicate => ContainsGenericPlaceholder(type: predicate.Type));
    }

    private static bool ContainsGenericPlaceholder(TypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        if (type is GenericParameterTypeSymbol or ErrorTypeSymbol)
        {
            return true;
        }

        if (type is { IsGenericDefinition: true, TypeArguments: not { Count: > 0 } })
        {
            return true;
        }

        if (type.TypeArguments is { Count: > 0 } &&
            type.TypeArguments.Any(predicate: ContainsGenericPlaceholder))
        {
            return true;
        }

        return type is TupleTypeSymbol tuple &&
               tuple.ElementTypes.Any(predicate: ContainsGenericPlaceholder);
    }

    #endregion

    #region Statement Rewriting

    private static readonly Dictionary<string, string> NoStringSubs =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Public entry point: rewrites a pre-transformed variant body <see cref="Statement"/>
    /// by substituting all generic type parameter references with concrete names.
    /// Used by the monomorphization loop for variant bodies stored in <c>_synthesizedBodies</c>.
    /// </summary>
    public static Statement RewriteStatement(Statement stmt, Dictionary<string, string> subs)
    {
        return RewriteStatement(stmt: stmt,
            ctx: new RewriteContext(stringSubs: subs, typeSubs: null, registry: null));
    }

    public static Statement RewriteStatement(Statement stmt,
        IReadOnlyDictionary<string, string> subs, IReadOnlyDictionary<string, TypeSymbol>? typeSubs,
        TypeRegistry? registry, RoutineInfo? enclosingRoutine = null)
    {
        RewriteContext ctx = typeSubs != null && registry != null
            ? new RewriteContext(stringSubs: subs, typeSubs: typeSubs, registry: registry)
            : new RewriteContext(stringSubs: subs, typeSubs: null, registry: null);
        if (enclosingRoutine?.Parameters != null)
        {
            foreach (ParamInfo p in enclosingRoutine.Parameters)
            {
                TypeSymbol? resolvedParamType = ctx.ResolveType(original: p.Type) ?? p.Type;
                if (resolvedParamType != null)
                {
                    ctx.ParamTypes[key: p.Name] = resolvedParamType;
                }
            }
        }

        if (enclosingRoutine?.OwnerType is { } ownerType)
        {
            TypeSymbol resolvedOwner = ctx.ResolveType(original: ownerType) ?? ownerType;
            ctx.ParamTypes[key: "me"] = resolvedOwner;
        }

        return RewriteStatement(stmt: stmt, ctx: ctx);
    }

    private static Statement RewriteStatement(Statement stmt, RewriteContext ctx)
    {
        return stmt switch
        {
            BlockStatement block => block with
            {
                Statements = block.Statements
                                  .Select(selector: s => RewriteStatement(stmt: s, ctx: ctx))
                                  .ToList()
            },

            ExpressionStatement es => es with
            {
                Expression = RewriteExpression(expr: es.Expression, ctx: ctx)
            },

            DeclarationStatement ds => ds with
            {
                Declaration = RewriteDeclaration(decl: ds.Declaration, ctx: ctx)
            },

            AssignmentStatement assign => assign with
            {
                Target = RewriteExpression(expr: assign.Target, ctx: ctx),
                Value = RewriteExpression(expr: assign.Value, ctx: ctx)
            },

            ReturnStatement ret => ret with
            {
                Value = ret.Value != null
                    ? RewriteExpression(expr: ret.Value, ctx: ctx)
                    : null
            },

            BecomesStatement becomes => becomes with
            {
                Value = RewriteExpression(expr: becomes.Value, ctx: ctx)
            },

            IfStatement ifs => RewriteIf(ifs: ifs, ctx: ctx),

            WhileStatement ws => ws with
            {
                Condition = RewriteExpression(expr: ws.Condition, ctx: ctx),
                Body = RewriteStatement(stmt: ws.Body, ctx: ctx),
                ElseBranch = ws.ElseBranch != null
                    ? RewriteStatement(stmt: ws.ElseBranch, ctx: ctx)
                    : null
            },

            LoopStatement ls => ls with { Body = RewriteStatement(stmt: ls.Body, ctx: ctx) },

            EachStatement fs => fs with
            {
                Iterable = RewriteExpression(expr: fs.Iterable, ctx: ctx),
                Body = RewriteStatement(stmt: fs.Body, ctx: ctx),
                ElseBranch = fs.ElseBranch != null
                    ? RewriteStatement(stmt: fs.ElseBranch, ctx: ctx)
                    : null
            },

            // Buildtime member-expansion: unroll the body once per member of the concrete source
            // type. Never survives to codegen — replaced by a flat block of the per-member clones.
            // CloneOnly: leave the expand INTACT (structural recurse) so the generic-def template stays
            // foldable by a later real monomorphization.
            ExpandStatement expand when !ctx.CloneOnly => RewriteExpandStatement(expand: expand,
                ctx: ctx),

            ExpandStatement expand => expand with
            {
                Body = RewriteStatement(stmt: expand.Body, ctx: ctx)
            },

            WhenStatement { ArmExpansion: not null } armWhen when !ctx.CloneOnly =>
                RewriteWhenArmExpansion(ws: armWhen, ctx: ctx),

            // Buildtime type-switch: `when T … is X => …` where T is this instantiation's concrete type.
            // Fold to the matching arm (or else / no-op) so codegen never sees `T` in value position.
            WhenStatement typeSwitch when !ctx.CloneOnly &&
                                          IsTypeParamSubject(expr: typeSwitch.Expression, ctx: ctx)
                => FoldTypeSwitch(ws: typeSwitch, ctx: ctx),

            WhenStatement ws => ws with
            {
                Expression = RewriteExpression(expr: ws.Expression, ctx: ctx),
                Clauses = ws.Clauses
                            .Select(selector: c => RewriteWhenClause(clause: c, ctx: ctx))
                            .ToList()
            },

            ThrowStatement ts => ts with { Error = RewriteExpression(expr: ts.Error, ctx: ctx) },

            DiscardStatement disc => disc with
            {
                Expression = RewriteExpression(expr: disc.Expression, ctx: ctx)
            },

            DangerStatement danger => danger with
            {
                Body = (BlockStatement)RewriteStatement(stmt: danger.Body, ctx: ctx)
            },

            UsingStatement us => us with
            {
                Resource = RewriteExpression(expr: us.Resource, ctx: ctx),
                Body = RewriteStatement(stmt: us.Body, ctx: ctx),
                FallbackBody = us.FallbackBody != null
                    ? RewriteStatement(stmt: us.FallbackBody, ctx: ctx)
                    : null
            },

            DestructuringStatement destruct => destruct with
            {
                Initializer = RewriteExpression(expr: destruct.Initializer, ctx: ctx)
            },

            VariantReturnStatement vr => vr with
            {
                Value = vr.Value != null
                    ? RewriteExpression(expr: vr.Value, ctx: ctx)
                    : null
            },

            // Leaf statements
            BreakStatement or ContinueStatement or PassStatement or AbsentStatement => stmt,

            _ => stmt
        };
    }

    /// <summary>
    /// Pure structural DEEP-CLONE of a statement (no substitutions, no buildtime folding). Every node is
    /// copied so mutation of the clone (e.g. on-demand SA annotating <c>ResolvedCreatorRoutine</c> /
    /// <c>ResolvedType</c>) cannot leak into the original. Unlike the substituting rewrite, buildtime
    /// constructs (<c>expand</c>, type-switch <c>when</c>, arm-expansion, splices, metadata intrinsics)
    /// are LEFT INTACT — so a GENERIC-DEFINITION template body stays foldable by a later real
    /// monomorphization. This is the isolation primitive for warm builds sharing one stdlib snapshot.
    /// </summary>
    public static Statement DeepCloneStatement(Statement stmt)
    {
        return RewriteStatement(stmt: stmt,
            ctx: new RewriteContext(stringSubs: NoStringSubs, typeSubs: null, registry: null)
            {
                CloneOnly = true
            });
    }

    /// <summary>
    /// Unrolls a buildtime <c>expand m in allmemvarof(T)</c> loop at monomorphization: resolves the
    /// concrete source type, then clones the body once per member variable with the handle
    /// projections folded (<c>m.name</c>→Text literal, <c>m.id</c>→U64 literal) and the
    /// <c>x.${m.name}</c> splices rewritten to real member accesses. The per-member clones are
    /// flattened into one block (no per-iteration scope, so an outer accumulator var stays visible).
    /// </summary>
    private static BlockStatement RewriteExpandStatement(ExpandStatement expand,
        RewriteContext ctx)
    {
        TypeSymbol? source = ResolveExpandSource(sourceType: expand.SourceType, ctx: ctx);

        // caseof(T): iterate a choice's cases (S32 discriminants) or a flags' members (U64 bit values),
        // exposing c.name / c.id (ordinal) / c.value (the numeric constant, spliced via ${c.value}).
        if (expand.SourceKind == ExpandSourceKind.Cases)
        {
            return RewriteCaseExpand(expand: expand, source: source, ctx: ctx);
        }

        // openmemvarof/allmemvarof work over any field-carrying aggregate: records, tuples (a
        // RecordTypeSymbol subtype), and entities (their own MemberVariables list).
        List<MemberVariableInfo>? members = source switch
        {
            RecordTypeSymbol record => record.MemberVariables,
            EntityTypeSymbol entity => entity.MemberVariables,
            _ => null
        };

        // Byte offset of each member within the parent struct (repr-C layout), keyed by member identity.
        // Computed over the FULL declaration-order list BEFORE the openmemvarof filter, so a `secret`/
        // `posted` field ahead still pushes the offsets `placeof(m)` folds to.
        Dictionary<MemberVariableInfo, long> offsets = ComputeMemberOffsets(members: members,
            packed: source is RecordTypeSymbol { IsPacked: true });

        // openmemvarof(T) yields only the publicly-readable members (OPEN ∪ POSTED) — a `secret` field is
        // filtered out. allmemvarof(T) yields every member. This visibility split is
        // the sole filter (the old `if not m.is_secret` gate is gone — pick the intrinsic instead).
        if (members != null && expand.SourceKind == ExpandSourceKind.OpenMemberVariables)
        {
            members = members.Where(predicate: mv => mv.Visibility != VisibilityModifier.Secret)
                             .ToList();
        }

        var outStmts = new List<Statement>();
        if (members != null)
        {
            // Save/restore the active-member state so nested expands (later phases) don't clash.
            string? prevHandle = ctx.ActiveExpandHandle;
            string? prevName = ctx.ActiveMemberName;
            long prevIndex = ctx.ActiveMemberIndex;
            TypeSymbol? prevType = ctx.ActiveMemberType;
            VisibilityModifier prevVis = ctx.ActiveMemberVisibility;
            long prevOffset = ctx.ActiveMemberOffset;

            ctx.ActiveExpandHandle = expand.HandleName;
            foreach (MemberVariableInfo mv in members)
            {
                ctx.ActiveMemberName = mv.Name;
                ctx.ActiveMemberIndex = mv.Index;
                ctx.ActiveMemberType = mv.Type;
                ctx.ActiveMemberVisibility = mv.Visibility;
                ctx.ActiveMemberOffset = offsets.TryGetValue(key: mv, value: out long off)
                    ? off
                    : 0;

                Statement clone = RewriteStatement(stmt: expand.Body, ctx: ctx);
                if (clone is BlockStatement block)
                {
                    outStmts.AddRange(collection: block.Statements);
                }
                else
                {
                    outStmts.Add(item: clone);
                }
            }

            ctx.ActiveExpandHandle = prevHandle;
            ctx.ActiveMemberName = prevName;
            ctx.ActiveMemberIndex = prevIndex;
            ctx.ActiveMemberType = prevType;
            ctx.ActiveMemberVisibility = prevVis;
            ctx.ActiveMemberOffset = prevOffset;
        }

        return new BlockStatement(Statements: outStmts, Location: expand.Location);
    }

    /// <summary>Byte size of a member/type for repr-C layout, guarded: a non-concrete <c>@llvm</c>
    /// template hole (a generic-def body being cloned before substitution) throws inside
    /// <see cref="TypeSymbol.SizeBytes"/>, so treat it as 0 rather than detonating the whole rewrite.</summary>
    private const int PointerSize = 8;

    private static int SafeSizeBytes(TypeSymbol? type)
    {
        if (type is null)
        {
            return 0;
        }

        // A template hole surfaces as either an unknown LLVM scalar (InvalidOperationException from
        // SizeOfArbitraryInt) or an unparseable array count (FormatException from int.Parse); anything
        // else is a real bug and must not be swallowed.
        try { return type.SizeBytes(pointerSize: PointerSize); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 0;
        }
    }

    /// <summary>Natural (ABI) alignment of <paramref name="type"/> with the same non-throwing guard as
    /// <see cref="SafeSizeBytes"/> (a non-concrete <c>@llvm</c> template hole yields 1, not a throw).</summary>
    private static int SafeAlignment(TypeSymbol? type)
    {
        if (type is null)
        {
            return 1;
        }

        try { return type.Alignment(pointerSize: PointerSize); }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException)
        {
            return 1;
        }
    }

    /// <summary>
    /// Computes each member's byte OFFSET within the parent struct using the repr-C layout formula
    /// (<c>alignment = member natural alignment; size = AlignTo(size, alignment); offset = size; size
    /// += memberSize</c>), matching <see cref="RecordTypeSymbol.SizeBytes"/> — so <c>placeof(m)</c> equals C
    /// <c>offsetof</c> even for nested aggregates. Keyed by member identity so
    /// the caller can look up an offset after filtering the iteration set. Empty when the source carries
    /// no member list.
    /// </summary>
    private static Dictionary<MemberVariableInfo, long> ComputeMemberOffsets(
        List<MemberVariableInfo>? members, bool packed = false)
    {
        var offsets = new Dictionary<MemberVariableInfo, long>();
        if (members == null)
        {
            return offsets;
        }

        long size = 0;
        foreach (MemberVariableInfo mv in members)
        {
            int memberSize = SafeSizeBytes(type: mv.Type);
            // @layout("packed"): no inter-field padding, so each member sits at the running byte
            // offset (alignment 1) — matches RecordTypeSymbol.SizeBytes's packed branch and the packed
            // LLVM `<{...}>` layout, so placeof(m) stays truthful for a packed record.
            int alignment = packed
                ? 1
                : SafeAlignment(type: mv.Type);
            size = AlignTo(size: size, alignment: alignment);
            offsets[key: mv] = size;
            size += memberSize;
        }

        return offsets;
    }

    /// <summary>Rounds <paramref name="size"/> up to the next multiple of <paramref name="alignment"/>
    /// (repr-C member alignment); mirrors <c>RecordTypeSymbol.AlignTo</c>.</summary>
    private static long AlignTo(long size, int alignment)
    {
        if (alignment <= 1)
        {
            return size;
        }

        long rem = size % alignment;
        return rem == 0
            ? size
            : size + (alignment - rem);
    }

    /// <summary>
    /// Unrolls <c>expand c in caseof(T)</c>: one body clone per choice case / flags member, with
    /// <c>c.name</c> (Text), <c>c.id</c> (ordinal) and <c>c.value</c> (the S32 discriminant / U64 bit)
    /// folded to literals. A choice case's value is its computed discriminant; a flags member's value
    /// is <c>1 &lt;&lt; bitPosition</c>.
    /// </summary>
    private static BlockStatement RewriteCaseExpand(ExpandStatement expand, TypeSymbol? source,
        RewriteContext ctx)
    {
        List<(string Name, long Value)>? cases = source switch
        {
            ChoiceTypeSymbol choice => choice.Cases
                                           .Select(selector: c => (c.Name, (long)c.ComputedValue))
                                           .ToList(),
            FlagsTypeSymbol flags => flags.Members
                                        .Select(selector: m =>
                                             (m.Name, (long)(1UL << m.BitPosition)))
                                        .ToList(),
            _ => null
        };

        var outStmts = new List<Statement>();
        if (cases != null)
        {
            string? prevHandle = ctx.ActiveExpandHandle;
            string? prevName = ctx.ActiveMemberName;
            long prevIndex = ctx.ActiveMemberIndex;
            long prevCaseValue = ctx.ActiveCaseValue;
            bool prevIsFlags = ctx.ActiveCaseIsFlags;

            ctx.ActiveExpandHandle = expand.HandleName;
            ctx.ActiveCaseIsFlags = source is FlagsTypeSymbol;
            long idx = 0;
            foreach ((string caseName, long caseValue) in cases)
            {
                ctx.ActiveMemberName = caseName;
                ctx.ActiveMemberIndex = idx++;
                ctx.ActiveCaseValue = caseValue;

                Statement clone = RewriteStatement(stmt: expand.Body, ctx: ctx);
                if (clone is BlockStatement block)
                {
                    outStmts.AddRange(collection: block.Statements);
                }
                else
                {
                    outStmts.Add(item: clone);
                }
            }

            ctx.ActiveExpandHandle = prevHandle;
            ctx.ActiveMemberName = prevName;
            ctx.ActiveMemberIndex = prevIndex;
            ctx.ActiveCaseValue = prevCaseValue;
            ctx.ActiveCaseIsFlags = prevIsFlags;
        }

        return new BlockStatement(Statements: outStmts, Location: expand.Location);
    }

    /// <summary>
    /// Unrolls a buildtime <c>when me</c> / <c>expand m in branchof(T)</c> / <c>is ${m.type} x => …</c>
    /// at monomorphization: resolves the concrete variant, then clones the template clause once per
    /// arm with <c>${m.type}</c> folded to the arm type and the payload binding annotated. Payload-
    /// less arms (None / None) are skipped for now — a template with a binding cannot serve them;
    /// handling those is a follow-up (they need a bindingless arm form).
    /// </summary>
    /// <summary>
    /// True when <paramref name="expr"/> is a bare generic parameter this instantiation substitutes to
    /// a concrete type — i.e. a buildtime type-switch subject (`when T …`) we can fold here.
    /// </summary>
    private static bool IsTypeParamSubject(Expression expr, RewriteContext ctx)
    {
        return expr is IdentifierExpression id && ctx.TypeSubs != null &&
               ctx.TypeSubs.ContainsKey(key: id.Name);
    }

    /// <summary>
    /// Buildtime type test: <c>&lt;T&gt; is &lt;Type&gt;</c> where <c>T</c> resolves to a concrete type in
    /// this instantiation. RF has no inheritance, so this is exact type-identity equality. Returns null
    /// when it can't be decided here (operand not a substituted type parameter, or target unresolved).
    /// </summary>
    private static bool? TryEvalTypeParamIs(Expression operand, TypePattern typePattern,
        RewriteContext ctx)
    {
        if (operand is not IdentifierExpression id || ctx.TypeSubs == null ||
            !ctx.TypeSubs.TryGetValue(key: id.Name, value: out TypeSymbol? concrete))
        {
            return null;
        }

        // ResolveType returns null when the target needs no substitution (a fully concrete pattern
        // type like `Text`), so fall back to the pattern's own resolved type. When the pattern type
        // is UNRESOLVED (ResolvedType == null) — the case for a derive template materialized from RAW
        // pre-analysis source (`MaterializeDeriveTemplateBodyIfNeeded` folds BEFORE the body is SA'd),
        // where every `is X` arm's TypeExpression has no ResolvedType yet — resolve it by NAME from the
        // registry instead, so the arm can still match (otherwise every arm fails → the else field-walk
        // is always taken, e.g. `S32.serialize()` boxing to `{}` instead of its SerialValue arm).
        TypeSymbol? target = typePattern.Type.ResolvedType == null
            ? ctx.ResolveTypeExpressionPublic(typeExpr: typePattern.Type)
            : ctx.ResolveType(original: typePattern.Type.ResolvedType) ??
              typePattern.Type.ResolvedType;
        return target == null
            ? null
            : string.Equals(a: concrete.FullName,
                b: target.FullName,
                comparisonType: StringComparison.Ordinal);
    }

    /// <summary>
    /// Folds a buildtime type-switch <c>when T … is X =&gt; …</c> to the SINGLE arm whose type pattern
    /// matches the concrete substitution for <c>T</c> (or the <c>else</c> arm, or a no-op). Non-matching
    /// arms — which may use operations valid only for their arm type — are REMOVED, not dead-branched.
    /// </summary>
    private static Statement FoldTypeSwitch(WhenStatement ws, RewriteContext ctx)
    {
        Statement? elseBody = null;
        foreach (WhenClause clause in ws.Clauses)
        {
            switch (clause.Pattern)
            {
                case TypePattern tp
                    when TryEvalTypeParamIs(operand: ws.Expression, typePattern: tp, ctx: ctx) ==
                         true:
                    return RewriteStatement(stmt: clause.Body, ctx: ctx);
                case ElsePattern or WildcardPattern:
                    elseBody = clause.Body;
                    break;
            }
        }

        return elseBody != null
            ? RewriteStatement(stmt: elseBody, ctx: ctx)
            : new BlockStatement(Statements: [], Location: ws.Location);
    }

    /// <summary>A folded <c>true</c>/<c>false</c> literal (with a resolved <c>Bool</c> type when available).</summary>
    private static LiteralExpression MakeBoolLiteral(bool value, RewriteContext ctx,
        SourceLocation loc)
    {
        return new LiteralExpression(Value: value,
            LiteralType: value
                ? TokenType.True
                : TokenType.False,
            Location: loc) { ResolvedType = ctx.Registry?.LookupType(name: "Bool") };
    }

    private static WhenStatement RewriteWhenArmExpansion(WhenStatement ws, RewriteContext ctx)
    {
        WhenArmExpansion arm = ws.ArmExpansion!;
        Expression subject = RewriteExpression(expr: ws.Expression, ctx: ctx);
        TypeSymbol? source = ResolveExpandSource(sourceType: arm.SourceType, ctx: ctx);
        string? binding = (arm.Template.Pattern as SpliceTypePattern)?.VariableName;
        SourceLocation loc = arm.Template.Location;

        // Explicit clauses written alongside the expand (e.g. `is None => …`) come first.
        var clauses = ws.Clauses
                        .Select(selector: c => RewriteWhenClause(clause: c, ctx: ctx))
                        .ToList();
        if (source is VariantTypeSymbol variant)
        {
            string? prevHandle = ctx.ActiveExpandHandle;
            string? prevName = ctx.ActiveMemberName;
            long prevIndex = ctx.ActiveMemberIndex;
            TypeSymbol? prevType = ctx.ActiveMemberType;

            ctx.ActiveExpandHandle = arm.HandleName;
            foreach (VariantMemberInfo vm in variant.Members)
            {
                // Skip payload-less arms (see summary) — the template binds a payload.
                if (vm.IsNone || vm.Type is null || vm.Type.Name == "None")
                {
                    continue;
                }

                ctx.ActiveMemberType = vm.Type;
                ctx.ActiveMemberName = vm.Name;
                ctx.ActiveMemberIndex = vm.Ordinal;

                var typeExpr =
                    new TypeExpression(Name: vm.Type.Name, GenericArguments: null, Location: loc)
                    {
                        ResolvedType = vm.Type
                    };
                var pattern = new TypePattern(Type: typeExpr,
                    VariableName: binding,
                    Bindings: null,
                    Location: loc);

                if (binding != null)
                {
                    ctx.ActiveBindingTypes[key: binding] = vm.Type;
                }

                Statement body = RewriteStatement(stmt: arm.Template.Body, ctx: ctx);

                if (binding != null)
                {
                    ctx.ActiveBindingTypes.Remove(key: binding);
                }

                clauses.Add(item: new WhenClause(Pattern: pattern, Body: body, Location: loc));
            }

            ctx.ActiveExpandHandle = prevHandle;
            ctx.ActiveMemberName = prevName;
            ctx.ActiveMemberIndex = prevIndex;
            ctx.ActiveMemberType = prevType;
        }

        return new WhenStatement(Expression: subject,
            Clauses: clauses,
            Location: ws.Location,
            ArmExpansion: null);
    }

    /// <summary>
    /// Resolves the concrete <see cref="TypeSymbol"/> that an <c>expand</c> loop iterates over. A
    /// bare generic parameter (<c>T</c>) resolves through the type-substitution map; otherwise the
    /// (string-substituted) name is looked up in the registry.
    /// </summary>
    private static TypeSymbol? ResolveExpandSource(TypeExpression sourceType, RewriteContext ctx)
    {
        if (ctx.TypeSubs != null &&
            ctx.TypeSubs.TryGetValue(key: sourceType.Name, value: out TypeSymbol? bound))
        {
            return bound;
        }

        string name = ctx.StringSubs.TryGetValue(key: sourceType.Name, value: out string? sub)
            ? sub
            : sourceType.Name;
        return ctx.Registry?.LookupType(name: name);
    }

    /// <summary>
    /// Folds a buildtime expand-handle projection to a literal, dispatched by the function-form
    /// metadata intrinsic (<see cref="FoldMetadataIntrinsic"/>): <c>name</c>→Text field name (nameof),
    /// <c>id</c>→U64 ordinal (orderof), <c>value</c>→the caseof constant (valueof), <c>type_id</c>→the
    /// arm/member type's stable id (typeidof).
    /// </summary>
    private static Expression FoldHandleProjection(string projection, RewriteContext ctx,
        SourceLocation location)
    {
        return projection switch
        {
            "name" => FoldProjectionName(ctx: ctx, location: location),
            "value" => FoldProjectionValue(ctx: ctx, location: location),
            TypeIdProjection => FoldProjectionTypeId(ctx: ctx, location: location),
            _ => FoldProjectionId(ctx: ctx, location: location) // "id"
        };
    }

    private static LiteralExpression FoldProjectionName(RewriteContext ctx,
        SourceLocation location)
    {
        return new LiteralExpression(Value: ctx.ActiveMemberName ?? "",
            LiteralType: TokenType.TextLiteral,
            Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "Text") };
    }

    private static LiteralExpression FoldProjectionValue(RewriteContext ctx,
        SourceLocation location)
    {
        // caseof `c.value`: a choice's S32 discriminant or a flags member's U64 bit value.
        return ctx.ActiveCaseIsFlags
            ? new LiteralExpression(Value: (ulong)ctx.ActiveCaseValue,
                LiteralType: TokenType.U64Literal,
                Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "U64") }
            : new LiteralExpression(Value: ctx.ActiveCaseValue,
                LiteralType: TokenType.S32Literal,
                Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "S32") };
    }

    private static LiteralExpression FoldProjectionTypeId(RewriteContext ctx,
        SourceLocation location)
    {
        // The current arm/member type's stable type id (branchof `m.type_id`), matching the C#
        // `TypeIdHelper.ComputeTypeId(FullName)` used by variant `diagnose`.
        ulong typeId = ctx.ActiveMemberType?.FullName is { } fn
            ? TypeIdHelper.ComputeTypeId(fullName: fn)
            : 0UL;
        return new LiteralExpression(Value: typeId,
            LiteralType: TokenType.U64Literal,
            Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "U64") };
    }

    private static LiteralExpression FoldProjectionId(RewriteContext ctx, SourceLocation location)
    {
        return new LiteralExpression(Value: (ulong)ctx.ActiveMemberIndex,
            LiteralType: TokenType.U64Literal,
            Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "U64") };
    }

    /// <summary>
    /// True when a metadata intrinsic's argument can be folded in the current context: the argument
    /// names the active expand handle (member/case metadata), or — for <c>sizeof</c>/<c>typeof</c> — a
    /// resolvable type. Otherwise the call is left for ordinary resolution (e.g. a same-named local).
    /// </summary>
    private static bool IsFoldableMetadataArg(string name, Expression arg, RewriteContext ctx)
    {
        if (arg is IdentifierExpression argId && ctx.ActiveExpandHandle != null &&
            argId.Name == ctx.ActiveExpandHandle)
        {
            return true;
        }

        if (name is "sizeof" or "typeof" && arg is IdentifierExpression typeId)
        {
            return ResolveMetadataType(typeName: typeId.Name, ctx: ctx) != null;
        }

        return false;
    }

    /// <summary>Resolves a metadata intrinsic type argument (a type param like <c>T</c> or a concrete
    /// type name) to a concrete <see cref="TypeSymbol"/> for <c>sizeof(T)</c>/<c>typeof(T)</c>.</summary>
    private static TypeSymbol? ResolveMetadataType(string typeName, RewriteContext ctx)
    {
        if (ctx.TypeSubs != null &&
            ctx.TypeSubs.TryGetValue(key: typeName, value: out TypeSymbol? sub))
        {
            return ConcretizeGenericDef(type: sub, ctx: ctx);
        }

        TypeSymbol? t = ctx.Registry?.LookupType(name: typeName);
        return t != null
            ? ConcretizeGenericDef(type: t, ctx: ctx)
            : null;
    }

    /// <summary>The concrete type a metadata intrinsic argument denotes: the active member's type when
    /// the argument is the expand handle, else a resolved type name (for <c>sizeof(T)</c>/<c>typeof(T)</c>).</summary>
    private static TypeSymbol? MetadataArgType(Expression arg, RewriteContext ctx)
    {
        if (arg is IdentifierExpression argId)
        {
            if (ctx.ActiveExpandHandle != null && argId.Name == ctx.ActiveExpandHandle)
            {
                return ctx.ActiveMemberType;
            }

            return ResolveMetadataType(typeName: argId.Name, ctx: ctx);
        }

        return null;
    }

    /// <summary>
    /// Folds a buildtime metadata intrinsic (<c>nameof</c>/<c>orderof</c>/<c>typeof</c>/<c>typeidof</c>/
    /// <c>valueof</c>/<c>placeof</c>/<c>sizeof</c>) to a literal or typewise receiver off the active
    /// expand-unroll context. The name/order/type_id/value cases delegate to
    /// <see cref="FoldHandleProjection"/>; <c>typeof</c> builds a typewise receiver directly, and
    /// <c>placeof</c>/<c>sizeof</c> are repr-C layout reads.
    /// </summary>
    private static Expression FoldMetadataIntrinsic(string name, Expression arg,
        RewriteContext ctx, SourceLocation location)
    {
        switch (name)
        {
            case "nameof":
                return FoldHandleProjection(projection: "name", ctx: ctx, location: location);
            case "orderof":
                return FoldHandleProjection(projection: "id", ctx: ctx, location: location);
            case "typeidof":
                return FoldHandleProjection(projection: TypeIdProjection,
                    ctx: ctx,
                    location: location);
            case "visibilityof":
            {
                // Fold to the matching `Visibility` choice case (OPEN/POSTED/SECRET), a bare case
                // reference annotated with the choice type so a following `is SECRET` narrows correctly.
                string caseName = ctx.ActiveMemberVisibility switch
                {
                    VisibilityModifier.Secret => "SECRET",
                    VisibilityModifier.Posted => "POSTED",
                    _ => "OPEN"
                };
                return new IdentifierExpression(Name: caseName, Location: location)
                {
                    // Visibility lives in `module BuilderQuery` — qualify (bare lookup relied on the scan).
                    ResolvedType = ctx.Registry?.LookupType(name: "BuilderQuery.Visibility")
                };
            }
            case "valueof":
                return FoldHandleProjection(projection: "value", ctx: ctx, location: location);
            case "placeof":
                return new LiteralExpression(Value: (ulong)ctx.ActiveMemberOffset,
                    LiteralType: TokenType.U64Literal,
                    Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "U64") };
            case "sizeof":
                return new LiteralExpression(
                    Value: (ulong)SafeSizeBytes(type: MetadataArgType(arg: arg, ctx: ctx)),
                    LiteralType: TokenType.U64Literal,
                    Location: location) { ResolvedType = ctx.Registry?.LookupType(name: "U64") };
            default
                : // "typeof" — a typewise receiver naming the concrete member/type (see FoldHandleProjection "type").
            {
                TypeSymbol? t = MetadataArgType(arg: arg, ctx: ctx);
                return new IdentifierExpression(Name: t?.Name ?? "None", Location: location)
                {
                    ResolvedType = t
                };
            }
        }
    }

    /// <summary>
    /// Rewrites an <c>if</c>, buildtime-PRUNING it when — inside an active expand unroll — its condition
    /// folded to a constant Bool (e.g. a <c>T is X</c> type test over a now-concrete member/param type).
    /// Only the taken branch is kept, so codegen never sees the dead branch, which may contain a call
    /// that is invalid for this concrete member. Outside expand, or with a non-constant condition, both
    /// branches are preserved as an ordinary runtime <c>if</c>.
    /// </summary>
    private static Statement RewriteIf(IfStatement ifs, RewriteContext ctx)
    {
        Expression cond = RewriteExpression(expr: ifs.Condition, ctx: ctx);

        // A folded constant condition may be wrapped in `not` (e.g. `if not (T is X)`); fold the
        // negation so the constant-condition prune below still fires.
        if (cond is UnaryExpression
            {
                Operator: UnaryOperator.Not, Operand: LiteralExpression { Value: bool inner }
            } negLit)
        {
            cond = new LiteralExpression(Value: !inner,
                LiteralType: !inner
                    ? TokenType.True
                    : TokenType.False,
                Location: negLit.Location) { ResolvedType = negLit.ResolvedType };
        }

        if (ctx.ActiveExpandHandle != null && cond is LiteralExpression { Value: bool taken })
        {
            Statement? branch = taken
                ? ifs.ThenStatement
                : ifs.ElseStatement;
            return branch != null
                ? RewriteStatement(stmt: branch, ctx: ctx)
                : new BlockStatement(Statements: [], Location: ifs.Location);
        }

        return ifs with
        {
            Condition = cond,
            ThenStatement = RewriteStatement(stmt: ifs.ThenStatement, ctx: ctx),
            ElseStatement = ifs.ElseStatement != null
                ? RewriteStatement(stmt: ifs.ElseStatement, ctx: ctx)
                : null
        };
    }

    /// <summary>
    /// Rewrites a splice-selector member access (<c>x.${m.name}</c>) to a real member access on the
    /// current member (<c>x.field</c>), annotated with the member's static type so the chained call
    /// (e.g. <c>.represent()</c>) re-resolves against the concrete field type.
    /// </summary>
    private static MemberExpression RewriteSpliceMember(SpliceMemberExpression sm,
        RewriteContext ctx)
    {
        Expression obj = RewriteExpression(expr: sm.Object, ctx: ctx);
        string memberName = ctx.ActiveMemberName ?? "";

        // Annotate with the ACTUAL member's type on the object's concrete type when it can be
        // determined. This differs from the expand-source member type when the object is a CONTAINER
        // whose members are derived from the source type — the SoA case: `me.x` on a `SplitArray[Point]`
        // is the column `Array[F32, N]`, not the source field `F32`. For the common case (the object IS
        // the expand source, e.g. `me` is the record being walked, or `result` is a fresh element), the
        // lookup falls back to the source member type.
        TypeSymbol? objType = ResolveSpliceObjectType(obj: obj, ctx: ctx);
        TypeSymbol? memberType =
            LookupMemberType(type: objType, name: memberName) ?? ctx.ActiveMemberType;

        var member = new MemberExpression(Object: obj,
            MemberName: memberName,
            Location: sm.Location) { ResolvedType = memberType };
        return member;
    }

    /// <summary>Best-effort concrete type of an expand splice's object (<c>me</c>, a param, or an
    /// annotated expression), used to look up the real member a <c>${m.name}</c> splice targets.</summary>
    private static TypeSymbol? ResolveSpliceObjectType(Expression obj, RewriteContext ctx)
    {
        // `me` → the concrete owner type bound for this monomorphization.
        if (obj is IdentifierExpression { Name: "me" })
        {
            if (ctx.ParamTypes.TryGetValue(key: "me", value: out TypeSymbol? meParam))
            {
                return ConcretizeGenericDef(type: ctx.ResolveType(original: meParam) ?? meParam,
                    ctx: ctx);
            }

            if (ctx.TypeSubs != null &&
                ctx.TypeSubs.TryGetValue(key: "Me", value: out TypeSymbol? meBound))
            {
                return ConcretizeGenericDef(type: meBound, ctx: ctx);
            }
        }

        // A parameter reference whose type the context tracks.
        if (obj is IdentifierExpression id &&
            ctx.ParamTypes.TryGetValue(key: id.Name, value: out TypeSymbol? paramType))
        {
            return ConcretizeGenericDef(type: ctx.ResolveType(original: paramType) ?? paramType,
                ctx: ctx);
        }

        // An SA-annotated object type, substituted to concrete.
        if (obj.ResolvedType != null)
        {
            return ConcretizeGenericDef(
                type: ctx.ResolveType(original: obj.ResolvedType) ?? obj.ResolvedType,
                ctx: ctx);
        }

        return null;
    }

    /// <summary>
    /// If <paramref name="type"/> is still an unbound generic definition (e.g. the owner
    /// <c>SplitArray[T, N]</c> of a memberRoutine under monomorphization), materialize the concrete instance
    /// by binding its parameters through the current type-substitution map. This is what surfaces the
    /// decl-position expand columns (they live on the concrete instance, not the definition).
    /// </summary>
    private static TypeSymbol? ConcretizeGenericDef(TypeSymbol? type, RewriteContext ctx)
    {
        if (type == null || ctx.Registry == null || ctx.TypeSubs == null)
        {
            return type;
        }

        List<string>? genericParams = type switch
        {
            RecordTypeSymbol { IsGenericDefinition: true } r => r.GenericParameters,
            EntityTypeSymbol { IsGenericDefinition: true } e => e.GenericParameters,
            _ => null
        };
        if (genericParams is not { Count: > 0 })
        {
            return type;
        }

        var args = new List<TypeSymbol>(capacity: genericParams.Count);
        foreach (string p in genericParams)
        {
            if (!ctx.TypeSubs.TryGetValue(key: p, value: out TypeSymbol? bound))
            {
                return type; // can't fully bind — leave as-is
            }

            // CYCLE-BREAK: a self-referential binding — binding a parameter of `type` to a type that is (or
            // nests) `type`'s OWN generic definition — would build `RangeEmittable[RangeEmittable[T]]`, whose
            // own concretization nests again → unbounded `RangeEmittable[RangeEmittable[…]]` monomorphization
            // runaway (a stale/self-referential TypeSubs entry from an over-eager derive monomorphization).
            // Leave the splice's object type deferred (return the def) instead of materializing the nest.
            if (BoundReferencesDef(bound: bound, def: type))
            {
                return type;
            }

            args.Add(item: bound);
        }

        return ctx.Registry.GetOrCreateResolution(genericDef: type, typeArguments: args);
    }

    /// <summary>True when <paramref name="bound"/> is, or nests within a type argument, the same generic
    /// definition as <paramref name="def"/> — i.e. binding a parameter of <c>def</c> to <c>bound</c> would
    /// make <c>def</c> its own (transitive) type argument, a self-referential nest that concretizes without
    /// end. Compared by generic-definition identity (reference, else bare name).</summary>
    private static bool BoundReferencesDef(TypeSymbol bound, TypeSymbol def)
    {
        TypeSymbol? boundDef = bound switch
        {
            RecordTypeSymbol r => r.GenericDefinition ?? r,
            EntityTypeSymbol e => e.GenericDefinition ?? e,
            _ => null
        };
        if (boundDef != null &&
            (ReferenceEquals(objA: boundDef, objB: def) || boundDef.Name == def.Name))
        {
            return true;
        }

        return bound.TypeArguments?.Any(predicate: a => BoundReferencesDef(bound: a, def: def)) ??
               false;
    }

    /// <summary>True when <paramref name="type"/> is a container declaring decl-position
    /// <c>expand</c> (SoA) columns — its <c>${m.name}</c> members are generated column types
    /// (<c>Array[F32, N]</c>) that diverge from the expand source's member types, so their
    /// monomorph-computed type must be preserved rather than overwritten with the deferred splice
    /// placeholder.</summary>
    private static bool IsSoAContainerType(TypeSymbol? type)
    {
        // A concrete instance (SplitArray[Point, 4]) carries the materialized columns in
        // MemberVariables but NOT the ExpandTemplates list (those stay on the generic definition —
        // ExpandSoAColumns reads them from the def), so also consult the GenericDefinition.
        return type switch
        {
            RecordTypeSymbol { ExpandTemplates.Count: > 0 } => true,
            EntityTypeSymbol { ExpandTemplates.Count: > 0 } => true,
            RecordTypeSymbol
            {
                GenericDefinition: RecordTypeSymbol { ExpandTemplates.Count: > 0 }
            } => true,
            EntityTypeSymbol
            {
                GenericDefinition: EntityTypeSymbol { ExpandTemplates.Count: > 0 }
            } => true,
            _ => false
        };
    }

    /// <summary>Looks up a member variable's type by name on a record/entity type, or null.</summary>
    private static TypeSymbol? LookupMemberType(TypeSymbol? type, string name)
    {
        return type switch
        {
            RecordTypeSymbol r => r.MemberVariables.FirstOrDefault(predicate: mv => mv.Name == name)
                                ?.Type,
            EntityTypeSymbol e => e.MemberVariables.FirstOrDefault(predicate: mv => mv.Name == name)
                                ?.Type,
            _ => null
        };
    }

    private static WhenClause RewriteWhenClause(WhenClause clause, RewriteContext ctx)
    {
        return clause with
        {
            Pattern = RewritePattern(pattern: clause.Pattern, ctx: ctx),
            Body = RewriteStatement(stmt: clause.Body, ctx: ctx)
        };
    }

    #endregion

    #region Pattern Rewriting

    private static Pattern RewritePattern(Pattern pattern, RewriteContext ctx)
    {
        return pattern switch
        {
            TypePattern tp => tp with
            {
                Type = RewriteType(type: tp.Type, ctx: ctx),
                Bindings = tp.Bindings
                            ?.Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                             .ToList()
            },

            NegatedTypePattern ntp => ntp with { Type = RewriteType(type: ntp.Type, ctx: ctx) },

            TypeDestructuringPattern tdp => tdp with
            {
                Type = RewriteType(type: tdp.Type, ctx: ctx),
                Bindings = tdp.Bindings
                              .Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                              .ToList()
            },

            GuardPattern gp => gp with
            {
                InnerPattern = RewritePattern(pattern: gp.InnerPattern, ctx: ctx),
                Guard = RewriteExpression(expr: gp.Guard, ctx: ctx)
            },

            ExpressionPattern ep => ep with
            {
                Expression = RewriteExpression(expr: ep.Expression, ctx: ctx)
            },

            ComparisonPattern cp => cp with
            {
                Value = RewriteExpression(expr: cp.Value, ctx: ctx)
            },

            VariantPattern vp => vp with
            {
                Bindings = vp.Bindings
                            ?.Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                             .ToList()
            },

            CrashablePattern crash => crash with
            {
                ErrorType = crash.ErrorType != null
                    ? RewriteType(type: crash.ErrorType, ctx: ctx)
                    : null
            },

            DestructuringPattern dp => dp with
            {
                Bindings = dp.Bindings
                             .Select(selector: b => RewriteBinding(binding: b, ctx: ctx))
                             .ToList()
            },

            // Leaf patterns
            LiteralPattern or IdentifierPattern or WildcardPattern or NonePattern or ElsePattern
                or FlagsPattern => pattern,

            _ => pattern
        };
    }

    private static DestructuringBinding RewriteBinding(DestructuringBinding binding,
        RewriteContext ctx)
    {
        return binding with
        {
            NestedPattern = binding.NestedPattern != null
                ? RewritePattern(pattern: binding.NestedPattern, ctx: ctx)
                : null
        };
    }

    #endregion

    #region Declaration Rewriting (for DeclarationStatements)

    private static SyntaxTree.Declaration RewriteDeclaration(SyntaxTree.Declaration decl,
        RewriteContext ctx)
    {
        switch (decl)
        {
            case VariableDeclaration vd:
            {
                Expression? newInit = vd.Initializer != null
                    ? RewriteExpression(expr: vd.Initializer, ctx: ctx)
                    : null;

                // Record a re-inferred local type when the initializer re-dispatched to a concrete
                // owner (its return type is now non-protocol) but SA had typed the binding via an
                // abstract protocol (e.g. `var it = r.iter()` : Iterator[S64] → RangeIterator[S64]).
                // This lets later references concretize so subsequent member calls re-dispatch to a
                // real implementer rather than the bodyless protocol memberRoutine. Only record when the
                // SA-time type was a protocol and the new return type isn't, so we never demote a
                // correctly-typed local.
                if (vd.Type == null &&
                    newInit is CallExpression { ResolvedRoutine.ReturnType: { } reinferred } &&
                    vd.Initializer?.ResolvedType is ProtocolTypeSymbol &&
                    reinferred is not ProtocolTypeSymbol and not GenericParameterTypeSymbol)
                {
                    ctx.LocalReinferredTypes[key: vd.Name] = reinferred;
                }

                return vd with
                {
                    Type = vd.Type != null
                        ? RewriteType(type: vd.Type, ctx: ctx)
                        : null,
                    Initializer = newInit
                };
            }

            default:
                return decl; // Other declarations in statement context are rare
        }
    }

    #endregion

    private static bool FoldsTypewise(Expression expr)
    {
        return expr is SpliceExpression or MemberExpression { Object: IdentifierExpression } ||
               expr is CallExpression { Callee: IdentifierExpression ofCallId } &&
               Verification.SemanticVerifier.IsMetadataIntrinsic(name: ofCallId.Name);
    }
}
