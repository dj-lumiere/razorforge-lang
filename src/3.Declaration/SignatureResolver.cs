using Compiler.Diagnostics;
using Compiler.Verification;
using Compiler.Verification.Enums;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Declaration;

using TypeSymbol = TypeInfo;

/// <summary>
/// Handles resolution and registration of routine signatures for the semantic analyzer.
/// </summary>
internal sealed class SignatureResolver
{
    private readonly SemanticVerifier _sa;
    private readonly TypeResolver _typeResolver;

    internal SignatureResolver(SemanticVerifier sa, TypeResolver typeResolver)
    {
        _sa = sa;
        _typeResolver = typeResolver;
    }

    /// <summary>
    /// Reports S802 for any `T` rvalue mark in a slot-position type expression.
    /// Vars, fields, and type-args hold lvalue, so `T` is rejected there. A routine return type
    /// and a parameter type may carry `T`: the return is an rvalue producer and a `T` parameter
    /// is an ownership-transfer (steal) slot. The top-level allowance does not recurse — a nested
    /// type argument is always an lvalue slot, so `List[T]` stays rejected even on a parameter.
    /// </summary>
    private void RejectRvalueMarkInSlot(TypeExpression? typeExpr, string positionDescription,
        bool allowTopLevelRvalue = false)
    {
        if (typeExpr is null) return;
        if (typeExpr.IsRvalue && !allowTopLevelRvalue)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.RvalueMarkInSlotPosition,
                message:
                $"`T` rvalue mark is not valid in {positionDescription}; rvalue is return-only.",
                location: typeExpr.Location);
        }

        if (typeExpr.GenericArguments is { } args)
        {
            foreach (TypeExpression arg in args)
            {
                RejectRvalueMarkInSlot(typeExpr: arg, positionDescription: "type argument");
            }
        }
    }

    #region Phase 4.1: Routine Signature Resolution and Registration

    /// <summary>
    /// Resolves routine signatures and registers them in the type registry.
    /// Processes pending routines collected during Phase 3 and Phase 4.
    /// Performs protocol-as-type desugaring and duplicate detection by full signature.
    /// </summary>
    /// <param name="filterFilePath">If set, only processes pending routines from this file.</param>
    internal void ResolveAndRegisterPendingRoutines(string? filterFilePath = null)
    {
        List<SemanticVerifier.PendingRoutine> toProcess;
        if (filterFilePath != null)
        {
            toProcess = _sa._pendingRoutines
                           .Where(predicate: p => p.FilePath == filterFilePath)
                           .ToList();
            _sa._pendingRoutines.RemoveAll(match: p => p.FilePath == filterFilePath);
        }
        else
        {
            toProcess = _sa._pendingRoutines.ToList();
            _sa._pendingRoutines.Clear();
        }

        foreach (SemanticVerifier.PendingRoutine pending in toProcess)
        {
            ResolveAndRegisterRoutine(pending: pending);
        }
    }

    /// <summary>
    /// Resolves a single pending routine's signature and registers it.
    /// </summary>
    private void ResolveAndRegisterRoutine(SemanticVerifier.PendingRoutine pending)
    {
        RoutineDeclaration routine = pending.Declaration;

        MutationCategory declaredModification =
            MutationCategoryExtensions.FromAnnotations(annotations: routine.Annotations);

        // Phase 4 (ResolveTypeBodies) replaces user-defined entity/record types in the registry
        // with new objects that carry resolved member variables. pending.OwnerType was captured
        // in Phase 3 and still points to the empty-member Phase 3 object. Re-look up by FullName
        // so that routines see the correct member variable list at body-analysis time (Phase 5).
        // For stdlib types the Phase 3 object is mutated in-place, so the lookup returns the
        // same object — no behaviour change there.
        // Realm-aware refresh: keep the owner in ITS OWN realm. A realm-blind lookup by FullName would
        // rebind an SF-realm `Core.List` routine's owner to the RazorForge-realm `Core.List` (same bare
        // key) → the routine keys bare → collides with / shadows the RF routine (RF-S406). For ambient-realm
        // owners `LookupType(name, realm)` reduces to the plain lookup, so RF is unaffected.
        TypeSymbol? refreshedOwnerType = pending.OwnerType != null
            ? (_sa._registry.LookupType(name: pending.OwnerType.FullName, realm: pending.OwnerType.Realm)
               ?? pending.OwnerType)
            : null;

        // Suflae representation unification: in a Suflae USER file, an `entity` is a `Roamed[E]` handle,
        // so entity types in its routine SIGNATURES (params + return + `me`) are substituted to
        // `Roamed[E]` — the same rule TypeBodyResolver applies to entity FIELDS. Gated to non-stdlib:
        // the borrowed RF stdlib is RazorForge source (bare single-owner entities), and its concrete
        // entity signatures must NOT be rewritten even though it's loaded under an SF compile.
        bool sfUserEntity = _sa._registry.Language == Language.Suflae
            && !_sa.IsStdlibFile(filePath: pending.FilePath);

        // Desugar homogeneous variadic params (`nums...: T`) into a const-generic `Array[T, __VarargN]`
        // BEFORE the generic-param filter below, so the implicit arity generic is picked up as a normal
        // routine generic. Mutates the AST decl in place (shared with the monomorph index).
        VariadicParamDesugar.Apply(routine: routine);

        // `needs T is TypeName` declares T as a generic type parameter (equivalent to `[T]`, a different
        // surface form) — fold declared names into the AST decl's GenericParameters HERE (SA layer, not
        // the parser) BEFORE they are read below, so `T` resolves in the signature/body and is inferable
        // at call sites exactly like a bracket param. Mutates the shared decl; idempotent.
        if (routine.GenericConstraints is { } typeNameDecls)
        {
            foreach (GenericConstraintDeclaration gc in typeNameDecls)
            {
                if (gc.ConstraintType != ConstraintKind.AnyType) continue;
                routine.GenericParameters ??= [];
                if (!routine.GenericParameters.Contains(item: gc.ParameterName))
                    routine.GenericParameters.Add(item: gc.ParameterName);
            }
        }

        // Filter routine.GenericParameters to exclude names that resolve to real types in the
        // registry — but ONLY for RECEIVER-derived leaves. The parser collects leaf identifiers from a
        // member routine's receiver type (`List[DictEntry[K, V]]`, `Iterable[Text]`); these mix genuine
        // params (K, V) with concrete bindings that must NOT re-enter as params (S64 in
        // `List[Pair[K, S64]]`, `Text` in `Iterable[Text].join`, `U16` in `List[U16].decode_as_utf16`).
        // A receiver leaf that resolves to a type is such a binding — drop it (the owner type still
        // carries any genuine receiver params, so they resolve via that scope).
        //
        // A param that is NOT receiver-derived — a free routine's own `[T]`, or a member routine's
        // memberRoutine-generic `[U]` — is an EXPLICIT declaration. It must NEVER be dropped just because a
        // user type shares its name (`record T` + `identity[T]`, `record U` + `Holder[A].mapped[U]`):
        // its identity is its slot, not the label. Filtering it here was the RF-S502 half of the
        // name-as-identity collision (the resolver-side half is TypeResolver's slot-first shadowing).
        HashSet<string> receiverLeaves = CollectReceiverLeafParamNames(routine.ReceiverType);
        List<string>? filteredGenericParams = routine.GenericParameters?
            .Where(predicate: p => !receiverLeaves.Contains(item: p)
                                   || _sa._registry.LookupType(name: p) is null)
            .ToList();
        if (filteredGenericParams is { Count: 0 }) filteredGenericParams = null;

        // Create preliminary RoutineInfo for generic parameter resolution context.
        // IsGenericParameter() checks _currentRoutine.GenericParameters to know which
        // type names are generic params (e.g., T, U) vs real types.
        var contextRoutine = new RoutineInfo(name: pending.RoutineName)
        {
            Kind = pending.Kind,
            OwnerType = refreshedOwnerType,
            GenericParameters = filteredGenericParams,
            GenericConstraints = routine.GenericConstraints,
            Module = pending.Module,
            IsFailable = routine.IsFailable,
            IsWiredMemberRoutine = routine.IsWiredMemberRoutine,
            Location = routine.Location
        };

        RoutineInfo? prevRoutine = _sa._currentRoutine;
        _sa._currentRoutine = contextRoutine;

        var parameters = new List<ParameterInfo>();
        var implicitGenerics = new List<string>();
        var implicitConstraints = new List<GenericConstraintDeclaration>();
        int implicitGenericCounter = 0;
        // AST parameter rewrites for protocol-as-generic desugaring: (param index, implicit
        // generic name). Applied to the AST decl after the loop so the decl mirrors the RoutineInfo.
        var astParamGenericNames = new List<(int Index, string GenericName)>();
        int paramIndex = -1;

        foreach (Parameter param in routine.Parameters)
        {
            paramIndex++;
            ResolveAndAppendParameter(param: param, paramIndex: paramIndex, parameters: parameters,
                implicitGenerics: implicitGenerics, implicitConstraints: implicitConstraints,
                astParamGenericNames: astParamGenericNames,
                implicitGenericCounter: ref implicitGenericCounter);
        }

        // S511: a user constructor may not occupy the all-fields memberwise signature.
        if (pending.Kind == RoutineKind.Creator)
        {
            CheckMemberwiseCreatorReserved(refreshedOwnerType: refreshedOwnerType,
                parameters: parameters, routine: routine);
        }

        // Resolve return type. Top-level `T` is legal (entity rvalue, return-position only);
        // nested `T` inside generic args is a slot position and rejected.
        if (routine.ReturnType?.GenericArguments is { } retArgs)
        {
            foreach (TypeExpression arg in retArgs)
            {
                RejectRvalueMarkInSlot(typeExpr: arg, positionDescription: "type argument");
            }
        }

        // SF entity RETURN types resolve to `Roamed[E]` via the ResolveType choke point, so `return me`
        // (me is `Roamed[E]` via MeType below) yields a retained handle to the SAME controller. `create`
        // returns the raw entity it builds; its return goes through the memberwise-synthesis path (not
        // this ResolveType call), so no explicit carve-out is needed here.
        TypeSymbol? returnType = routine.ReturnType != null
            ? _typeResolver.ResolveType(typeExpr: routine.ReturnType)
            : null;

        // Validate that Maybe<T>/Result<T>/Lookup<T> are not used as return types
        // These are builder-generated wrapper types for failable routines (!)
        ValidateReturnTypeNotCarrier(returnType: returnType, routine: routine);

        // Entity / generic-param returns are ALWAYS rvalue (in-flight) — INFERRED, not required
        // (RF-S803 relaxed 2026-07-13). A return produces a value, and single ownership means an
        // entity leaves a routine only by MOVE (implicit return-move) — there is no bound-lvalue-copy
        // mode for entities. The move-vs-link distinction that actually matters is already carried by
        // the type shape (bare `T` = move, borrow-wrapper = link) plus `steal` at use sites, so the
        // `T` return mark is redundant with position and is now inferred. The explicit mark is still
        // accepted for back-compat; for records the rvalue bit is a no-op.
        bool isRvalueReturn = (routine.ReturnType?.IsRvalue ?? false)
            || returnType is EntityTypeInfo or GenericParameterTypeInfo;

        (List<string> allGenericParams, List<GenericConstraintDeclaration> allConstraints) =
            MergeAndApplyImplicitGenerics(routine: routine,
                filteredGenericParams: filteredGenericParams,
                implicitGenerics: implicitGenerics, implicitConstraints: implicitConstraints,
                astParamGenericNames: astParamGenericNames);

        // Specialized-receiver member `me` (e.g. `List[Agent[V]]`), else the SF-user-entity Roamed[E]
        // handle. A specialized `meType` takes precedence over the SF wrap.
        TypeSymbol? meType = ResolveSpecializedReceiverMeType(pending: pending,
            refreshedOwnerType: refreshedOwnerType, routine: routine,
            filteredGenericParams: filteredGenericParams)
            ?? ResolveSuflaeEntityMeType(sfUserEntity: sfUserEntity, pending: pending,
                refreshedOwnerType: refreshedOwnerType);

        _sa._currentRoutine = prevRoutine;

        RoutineInfo finalRoutine = BuildFinalRoutineInfo(pending: pending,
            routine: routine, refreshedOwnerType: refreshedOwnerType, meType: meType,
            parameters: parameters, returnType: returnType, isRvalueReturn: isRvalueReturn,
            declaredModification: declaredModification, allGenericParams: allGenericParams,
            allConstraints: allConstraints);

        RegisterAndValidateRoutine(pending: pending, routine: routine, finalRoutine: finalRoutine);
    }

    /// <summary>
    /// Merges explicit and implicit generic parameters/constraints, and applies protocol-as-generic
    /// AST rewrites to the routine declaration so that downstream passes (GenericMonomorphizationPass,
    /// GenericAstRewriter) see the same implicit generics as the RoutineInfo.
    /// </summary>
    private static (List<string> AllParams, List<GenericConstraintDeclaration> AllConstraints)
        MergeAndApplyImplicitGenerics(RoutineDeclaration routine,
            List<string>? filteredGenericParams, List<string> implicitGenerics,
            List<GenericConstraintDeclaration> implicitConstraints,
            List<(int Index, string GenericName)> astParamGenericNames)
    {
        List<string> allGenericParams = filteredGenericParams?.ToList() ?? [];
        allGenericParams.AddRange(collection: implicitGenerics);
        List<GenericConstraintDeclaration> allConstraints =
            routine.GenericConstraints?.ToList() ?? [];
        allConstraints.AddRange(collection: implicitConstraints);

        // Desugar the protocol-as-generic rewrite onto the AST decl itself so downstream passes
        // that read the AST see the SAME implicit generics as the RoutineInfo. Without this the AST
        // keeps `r: Iterable[S64]` with no `[T]`, FindInStdlib rejects it as non-generic, and no
        // monomorphized body is emitted → declare-without-define → linker error. The `routine` node
        // is the same reference held in the program's declaration list, so mutating it propagates to
        // the GMP routine index (built later from program.Declarations).
        if (implicitGenerics.Count > 0)
        {
            foreach ((int idx, string genericName) in astParamGenericNames)
            {
                Parameter astParam = routine.Parameters[index: idx];
                routine.Parameters[index: idx] = astParam with
                {
                    Type = new TypeExpression(Name: genericName, GenericArguments: null,
                        Location: astParam.Type?.Location ?? astParam.Location)
                };
            }
            routine.GenericParameters = allGenericParams;
            routine.GenericConstraints = allConstraints;
        }

        return (allGenericParams, allConstraints);
    }

    /// <summary>
    /// Constructs the final <see cref="RoutineInfo"/> from the resolved signature components.
    /// </summary>
    private static RoutineInfo BuildFinalRoutineInfo(SemanticVerifier.PendingRoutine pending,
        RoutineDeclaration routine, TypeSymbol? refreshedOwnerType, TypeSymbol? meType,
        List<ParameterInfo> parameters, TypeSymbol? returnType, bool isRvalueReturn,
        MutationCategory declaredModification, List<string> allGenericParams,
        List<GenericConstraintDeclaration> allConstraints)
    {
        return new RoutineInfo(name: pending.RoutineName)
        {
            Kind = pending.Kind,
            OwnerType = refreshedOwnerType,
            MeType = meType,
            Parameters = parameters,
            ReturnType = returnType,
            IsFailable = routine.IsFailable,
            IsWiredMemberRoutine = routine.IsWiredMemberRoutine,
            IsInFlightReturn = isRvalueReturn,
            IsVariadic = routine.Parameters.Any(predicate: p => p.IsVariadic),
            GenericParameters = allGenericParams.Count > 0 ? allGenericParams : null,
            GenericConstraints = allConstraints.Count > 0 ? allConstraints : null,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Documentation = routine.Documentation,
            Module = pending.Module,
            ModulePath = pending.Module?.Split('/').ToList(),
            Annotations = routine.Annotations,
            DeclaredMutation = declaredModification,
            MutationCategory = declaredModification,
            IsDangerous = routine.IsDangerous,
            AsyncStatus = routine.Async
        };
    }

    /// <summary>
    /// Performs duplicate detection, registers the routine in the type registry, pins the decl→info
    /// binding, and runs post-registration protocol-conformance validations.
    /// </summary>
    private void RegisterAndValidateRoutine(SemanticVerifier.PendingRoutine pending,
        RoutineDeclaration routine, RoutineInfo finalRoutine)
    {
        // Duplicate detection by full signature (RegistryKey includes param types).
        // A user-written routine is allowed to shadow a synthesized (builder-generated) one.
        RoutineInfo? existingByKey = _sa._registry.LookupRoutine(fullName: finalRoutine.RegistryKey);
        if (existingByKey is { IsSynthesized: false })
        {
            _sa.ReportError(code: SemanticDiagnosticCode.DuplicateRoutineDefinition,
                message: $"Routine '{pending.RoutineName}' is already defined.",
                location: routine.Location);
            return;
        }

        // Constructor divergent-duplicate guard: hash the body so RegisterRoutine distinguishes
        // identical from divergent same-signature creators (mainly for stdlib cross-file paths).
        if (finalRoutine.IsCreator)
            finalRoutine.BodyHash = TypeRegistry.ComputeCreatorBodyHash(body: routine.Body);
        _sa._registry.RegisterRoutine(routine: finalRoutine);

        // Pin the decl → info binding so codegen reads it directly (module-blind name-parse avoided).
        routine.ResolvedInfo = finalRoutine;

        ValidateOperatorProtocolConformance(routineInfo: finalRoutine, location: routine.Location);
        ValidateProtocolMemberRoutineSignature(routineInfo: finalRoutine, location: routine.Location);
    }

    /// <summary>
    /// S511: reports <c>AllMemberVariablesCreatorReserved</c> when a user <c>create</c> occupies the
    /// all-fields memberwise signature — taking exactly the type's fields by BOTH name AND type. That
    /// shape is the built-in memberwise constructor and cannot be overridden. The match is by TYPE, not
    /// just name: a parsing/validating constructor that reuses a field name with a DIFFERENT type
    /// (e.g. <c>create(tag: S32)</c> for field <c>tag: S64</c>) is allowed and routes normally. The
    /// synthesized memberwise creator is registered elsewhere (AutoWiredRegistrationPass), so it never
    /// reaches here.
    /// </summary>
    private void CheckMemberwiseCreatorReserved(TypeSymbol? refreshedOwnerType,
        List<ParameterInfo> parameters, RoutineDeclaration routine)
    {
        List<MemberVariableInfo>? fields = refreshedOwnerType switch
        {
            EntityTypeInfo e => e.MemberVariables.ToList(),
            RecordTypeInfo r => r.MemberVariables.ToList(),
            _ => null
        };
        if (fields is { Count: > 0 }
            && parameters.Count == fields.Count
            && new HashSet<(string Name, string Type)>(
                    collection: parameters.Select(selector: p => (p.Name, p.Type.FullName)))
                .SetEquals(other: fields.Select(selector: f => (f.Name, f.Type.FullName))))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.AllMemberVariablesCreatorReserved,
                message:
                $"'create' cannot take exactly the fields ({string.Join(separator: ", ", values: fields.Select(selector: f => $"{f.Name}: {f.Type.Name}"))}) " +
                $"of '{refreshedOwnerType!.Name}' — that signature is the built-in memberwise constructor and " +
                "cannot be overridden. Use a distinct parameter shape (different names or types) or " +
                "`secret` fields with a named constructor.",
                location: routine.Location);
        }
    }

    /// <summary>
    /// Reports <c>ErrorHandlingTypeAsReturnType</c> when a non-stdlib routine returns a carrier type
    /// (Maybe/Result/Lookup — builder-generated wrappers for failable routines). Maybe is excluded via
    /// <see cref="IsCarrierType"/>'s callers elsewhere; here the full carrier set is rejected.
    /// </summary>
    private void ValidateReturnTypeNotCarrier(TypeSymbol? returnType, RoutineDeclaration routine)
    {
        if (returnType != null && IsCarrierType(type: returnType) &&
            !_sa.IsStdlibFile(filePath: _sa._currentFilePath))
        {
            string carrierName = GetCarrierBaseName(type: returnType)!;
            _sa.ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsReturnType,
                message: $"Routine cannot return '{carrierName}[T]'. " +
                         "These types are builder-generated for failable routines. " +
                         "Use a failable routine (!) with 'throw'/'absent' instead.",
                location: routine.ReturnType?.Location ?? routine.Location);
        }
    }

    /// <summary>
    /// Resolves one routine parameter and appends its <see cref="ParameterInfo"/> to
    /// <paramref name="parameters"/>. Rejects rvalue marks and carrier types, and performs
    /// protocol-as-type desugaring (a protocol-typed param becomes an implicit generic with an
    /// <c>obeys</c> constraint), recording the implicit generic name/constraint/AST-rewrite for the
    /// caller to merge and apply after the loop. <paramref name="implicitGenericCounter"/> is threaded
    /// by ref so implicit names stay globally sequential across params.
    /// </summary>
    private void ResolveAndAppendParameter(Parameter param, int paramIndex,
        List<ParameterInfo> parameters, List<string> implicitGenerics,
        List<GenericConstraintDeclaration> implicitConstraints,
        List<(int Index, string GenericName)> astParamGenericNames, ref int implicitGenericCounter)
    {
        if (param.Type == null)
        {
            // Type inference required - handle later
            parameters.Add(item: new ParameterInfo(name: param.Name,
                type: ErrorTypeInfo.Instance) { IsVariadicParam = param.IsVariadic });
            return;
        }

        RejectRvalueMarkInSlot(typeExpr: param.Type,
            positionDescription: $"parameter '{param.Name}'", allowTopLevelRvalue: true);
        // Suflae entity params resolve to `Roamed[E]` at the single ResolveType choke point
        // (TypeResolver.RoamSuflaeEntitySlot) — no per-site substitution here. The callee receives
        // the caller's Roamed handle directly (a BORROW; ScopeTeardownLoweringPass skips SF Roamed
        // params). `me` has no type expression (inferred from OwnerType) so it is set via MeType below.
        TypeSymbol paramType = _typeResolver.ResolveType(typeExpr: param.Type);

        // Variadic params are desugared to `Array[T, __VarargN]` up front (VariadicParamDesugar),
        // so param.Type already resolves to the Array template here — no List[T] wrapping.

        // Variants ARE valid parameter types — pass-by-value transfers ownership of
        // the payload (same rule as records containing entity fields).

        // Validate that Result<T> and Lookup<T> are not used as parameter types
        if (IsCarrierType(type: paramType) && !IsMaybeType(type: paramType))
        {
            string carrierName = GetCarrierBaseName(type: paramType)!;
            _sa.ReportError(code: SemanticDiagnosticCode.ErrorHandlingTypeAsParameter,
                message:
                $"'{carrierName}[T]' cannot be used as a parameter type. " +
                "Error handling types are internal for error propagation and should not be passed as arguments.",
                location: param.Location);
        }

        // Protocol-as-type desugaring: routine foo(x: Displayable) -> routine foo[T obeys Displayable](x: T)
        // Marker protocols Accessing[T]/Controlling[T] desugar the SAME way: `a: Accessing[S1]` becomes
        // `[V obeys Accessing[S1]](a: V)`, so the caller's concrete conformer (a Viewing/Modifying token
        // for an entity, or the value itself for a value type) binds V. Body uses that touch an ENTITY
        // member get `.access()`/`.control()` auto-inserted at member-access analysis; a value conformer's
        // `.access()`/`.control()` is identity. This replaces the old erase-to-inner-T model.
        if (paramType is ProtocolTypeInfo)
        {
            // Generate implicit generic parameter name
            string implicitGenericName = $"__T{implicitGenericCounter++}";
            implicitGenerics.Add(item: implicitGenericName);
            astParamGenericNames.Add(item: (paramIndex, implicitGenericName));

            // Create "obeys" constraint for the implicit generic
            var constraint = new GenericConstraintDeclaration(
                ParameterName: implicitGenericName,
                ConstraintType: ConstraintKind.Obeys,
                ConstraintTypes: [param.Type],
                Location: param.Location);
            implicitConstraints.Add(item: constraint);

            // Use the implicit generic as the parameter type
            var genericParamType = new GenericParameterTypeInfo(name: implicitGenericName)
            {
                Location = param.Location
            };

            parameters.Add(item: new ParameterInfo(name: param.Name, type: genericParamType)
            {
                DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
            });
        }
        else
        {
            parameters.Add(item: new ParameterInfo(name: param.Name, type: paramType)
            {
                DefaultValue = param.DefaultValue, IsVariadicParam = param.IsVariadic
            });
        }
    }

    /// <summary>
    /// Resolves the <c>me</c> type for a specialized-receiver member: if the receiver is a generic
    /// instantiation whose top-level type arguments are concrete types (not the owner's own bare
    /// generic params) — e.g. <c>List[Agent[V]]</c> — resolve it (the routine's generic params, incl.
    /// V, are in scope here) so <c>me</c> is typed as the specialized receiver and member access like
    /// <c>me[i]</c> yields the specialized element instead of the generic def's raw element. OwnerType
    /// stays the generic definition so registration and call-site lookup key on the base type. Returns
    /// <c>null</c> when the receiver is not a concrete specialization.
    /// </summary>
    private TypeSymbol? ResolveSpecializedReceiverMeType(SemanticVerifier.PendingRoutine pending,
        TypeSymbol? refreshedOwnerType, RoutineDeclaration routine, List<string>? filteredGenericParams)
    {
        if (pending.Kind == RoutineKind.MemberRoutine
            && refreshedOwnerType is EntityTypeInfo or RecordTypeInfo
            && pending.Kind is not RoutineKind.Creator
            && routine.RenderedReceiver is { } recvText
            && recvText.Contains(value: '['))
        {
            TypeExpression? recvExpr = SemanticVerifier.ParseTypeExpressionString(
                text: recvText, location: routine.Location);
            bool isSpecialized = recvExpr?.GenericArguments is { Count: > 0 } args
                && args.Any(predicate: a => a.Name != null
                    && !(filteredGenericParams?.Contains(item: a.Name) ?? false)
                    && _sa._registry.LookupType(name: a.Name) is not null);
            if (isSpecialized)
            {
                TypeSymbol resolvedRecv = _typeResolver.ResolveType(typeExpr: recvExpr!);
                if (resolvedRecv is not ErrorTypeInfo) return resolvedRecv;
            }
        }

        return null;
    }

    /// <summary>
    /// SF slice 2: resolves the <c>me</c> type of a USER entity member routine to the <c>Roamed[E]</c>
    /// handle (not bare <c>E</c>), so <c>me.field</c> routes through the Roamed access machinery and
    /// <c>return me</c> type-matches the now <c>Roamed[E]</c> return. Creators (<c>create</c>, incl. the
    /// failable ones) keep bare <c>me</c> — they build the raw entity before any controller exists.
    /// Returns <c>null</c> when the conditions do not apply.
    /// </summary>
    private TypeSymbol? ResolveSuflaeEntityMeType(bool sfUserEntity,
        SemanticVerifier.PendingRoutine pending, TypeSymbol? refreshedOwnerType)
    {
        if (sfUserEntity
            && pending.Kind == RoutineKind.MemberRoutine
            && pending.Kind is not RoutineKind.Creator
            && refreshedOwnerType is EntityTypeInfo ownerEntity
            && _sa._registry.LookupType(name: RuntimeContract.Roamed) is { } roamedOwnerDef)
        {
            // Wrap the entity APPLIED TO ITS OWN GENERIC PARAMS (`Box[T]`), not the bare definition —
            // otherwise `me` becomes `Roamed[Box]` with no `T` inside, and owner-monomorphization
            // (Box[S64].get) can't substitute `T` into the handle, so codegen falls back to a bare
            // entity access that reads the RC controller's refcount instead of the field. Mirrors the
            // `Me` handling in TypeResolver.
            TypeInfo entityForMe =
                ownerEntity is { IsGenericDefinition: true, GenericParameters: { } ownerParams }
                    ? _sa._registry.GetOrCreateResolution(genericDef: ownerEntity,
                        typeArguments: ownerParams
                            .Select(selector: p => (TypeInfo)new GenericParameterTypeInfo(name: p))
                            .ToList())
                    : ownerEntity;
            return _sa._registry.GetOrCreateResolution(
                genericDef: roamedOwnerDef, typeArguments: [entityForMe]);
        }

        return null;
    }

    /// <summary>
    /// Collects the leaf identifier names appearing in a member routine's RECEIVER type arguments
    /// (e.g. <c>List[DictEntry[K, V]]</c> → {K, V}, <c>Iterable[Text]</c> → {Text}). These are the
    /// receiver-DERIVED parameter names — the only ones the same-name-as-a-type filter may drop, since
    /// a receiver slot can bind a concrete type. memberRoutine-generic and free-routine parameters are NOT in
    /// the receiver, so they never appear here and are never filtered. Empty for a free routine (null
    /// receiver) or a bare type-parameter receiver. Dotted (module-qualified) names are excluded.
    /// </summary>
    private static HashSet<string> CollectReceiverLeafParamNames(TypeExpression? receiver)
    {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (receiver?.GenericArguments is { Count: > 0 } args)
        {
            foreach (TypeExpression arg in args)
            {
                CollectReceiverLeaves(type: arg, into: names);
            }
        }

        return names;
    }

    private static void CollectReceiverLeaves(TypeExpression type, HashSet<string> into)
    {
        if (type.GenericArguments is { Count: > 0 } args)
        {
            foreach (TypeExpression arg in args)
            {
                CollectReceiverLeaves(type: arg, into: into);
            }

            return;
        }

        if (type.Name.Contains(value: '.')) return;
        into.Add(item: type.Name);
    }

    /// <summary>
    /// Resolves external routine signatures (parameter types and return types).
    /// Externals are registered in Phase 3 and updated here with resolved types.
    /// </summary>
    internal void ResolveExternalSignatures(Program program)
    {
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            switch (declaration)
            {
                case ExternalDeclaration externalDecl:
                    ResolveExternalParameters(externalDecl: externalDecl);
                    break;

                case ExternalBlockDeclaration block:
                    foreach (SyntaxTree.Declaration decl in block.Declarations)
                    {
                        if (decl is ExternalDeclaration ext)
                        {
                            ResolveExternalParameters(externalDecl: ext);
                        }
                    }

                    break;
            }
        }

        CheckExternalSignatureConsistency(program: program);
    }

    /// <summary>
    /// Verifies that all <c>external("C")</c> declarations sharing a C symbol name agree on
    /// their resolved signature (calling convention, failability, variadicity, parameter types,
    /// return type). Two decls of the same C symbol with divergent signatures would silently
    /// pick one at link time and pass garbage at the other call site.
    /// </summary>
    private void CheckExternalSignatureConsistency(Program program)
    {
        var seen = new Dictionary<string, (ExternalDeclaration Decl, string Sig)>();
        foreach (ISyntaxTreeNode declaration in program.Declarations)
        {
            switch (declaration)
            {
                case ExternalDeclaration externalDecl:
                    VisitExternalDeclaration(ext: externalDecl, seen: seen);
                    break;
                case ExternalBlockDeclaration block:
                    VisitExternalBlockDeclarations(block: block, seen: seen);
                    break;
            }
        }
    }

    /// <summary>
    /// Checks one external declaration against the already-seen set for signature consistency and
    /// records it when no prior declaration exists.
    /// </summary>
    private void VisitExternalDeclaration(ExternalDeclaration ext,
        Dictionary<string, (ExternalDeclaration Decl, string Sig)> seen)
    {
        string sig = BuildExternalSignatureKey(ext: ext);
        if (seen.TryGetValue(key: ext.Name, value: out var prior))
        {
            if (prior.Sig != sig)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ExternalSignatureMismatch,
                    message:
                    $"external(\"{ext.CallingConvention ?? "C"}\") routine '{ext.Name}' is declared with " +
                    $"conflicting signatures: '{prior.Sig}' (at {prior.Decl.Location.Line}:{prior.Decl.Location.Column}) " +
                    $"vs '{sig}' (at {ext.Location.Line}:{ext.Location.Column}). " +
                    "All declarations of the same C symbol must agree on ABI.",
                    location: ext.Location);
            }
        }
        else
        {
            seen[key: ext.Name] = (ext, sig);
        }
    }

    /// <summary>
    /// Visits every <see cref="ExternalDeclaration"/> inside an <see cref="ExternalBlockDeclaration"/>
    /// and checks each one for signature consistency.
    /// </summary>
    private void VisitExternalBlockDeclarations(ExternalBlockDeclaration block,
        Dictionary<string, (ExternalDeclaration Decl, string Sig)> seen)
    {
        foreach (SyntaxTree.Declaration decl in block.Declarations)
        {
            if (decl is ExternalDeclaration ext)
                VisitExternalDeclaration(ext: ext, seen: seen);
        }
    }

    private string BuildExternalSignatureKey(ExternalDeclaration ext)
    {
        string conv = ext.CallingConvention ?? "C";
        string variadic = ext.IsVariadic ? "..." : "";
        string failable = ext.IsFailable ? "!" : "";
        List<string> parts = ext.Parameters
            .Select(p => p.Type != null
                ? _typeResolver.ResolveType(typeExpr: p.Type)
                : ErrorTypeInfo.Instance)
            .Select(t => t.FullName)
            .ToList();

        string paramSig = string.Join(separator: ", ", values: parts);
        if (variadic.Length > 0)
        {
            paramSig = paramSig.Length > 0 ? $"{paramSig}, {variadic}" : variadic;
        }

        string ret = ext.ReturnType != null
            ? _typeResolver.ResolveType(typeExpr: ext.ReturnType).FullName
            : "void";
        return $"extern(\"{conv}\") {ext.Name}{failable}({paramSig}) -> {ret}";
    }

    /// <summary>
    /// Validates that a memberRoutine's signature matches the protocol memberRoutine it implements.
    /// </summary>
    private void ValidateProtocolMemberRoutineSignature(RoutineInfo routineInfo, SourceLocation? location)
    {
        // Only check memberRoutines (not functions)
        if (routineInfo.OwnerType == null)
        {
            return;
        }

        // Re-lookup the owner type to get the updated version with protocols (realm-aware: keep the
        // owner in its own world-line so an SF-realm owner isn't rebound to the RF-realm same-name type).
        TypeSymbol? currentOwnerType = _sa._registry.LookupType(name: routineInfo.OwnerType.FullName, realm: routineInfo.OwnerType.Realm);
        if (currentOwnerType == null)
        {
            return;
        }

        // Get the list of implemented protocols for this type
        List<TypeSymbol>? implementedProtocols = currentOwnerType switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return;
        }

        // Check each protocol for a memberRoutine with this name
        foreach (TypeSymbol implemented in implementedProtocols)
        {
            if (implemented is not ProtocolTypeInfo protocol)
            {
                continue;
            }

            // Find the protocol memberRoutine with this name
            ProtocolMemberRoutineInfo? protoMemberRoutine = protocol.MemberRoutines.FirstOrDefault(
                predicate: m => m.Name == routineInfo.Name);

            if (protoMemberRoutine == null)
            {
                continue;
            }

            // Validate the signature matches
            ValidateMemberRoutineAgainstProtocol(typeMemberRoutine: routineInfo,
                protoMemberRoutine: protoMemberRoutine,
                protocol: protocol,
                location: location ?? new SourceLocation("", 0, 0, 0));
        }
    }

    /// <summary>
    /// Shared validation context threaded through protocol-parameter and return-type checks.
    /// Bundles the four repeated parameters that both check methods need so neither call site
    /// exceeds the 7-parameter limit.
    /// </summary>
    private readonly record struct ProtocolCheckContext(
        RoutineInfo TypeMemberRoutine,
        ProtocolTypeInfo Protocol,
        Dictionary<string, string>? Substitution,
        List<string>? InferableParams,
        SourceLocation? Location);

    /// <summary>
    /// Validates that a type memberRoutine matches the expected protocol memberRoutine signature.
    /// Reports specific errors for mismatches.
    /// </summary>
    private void ValidateMemberRoutineAgainstProtocol(RoutineInfo typeMemberRoutine,
        ProtocolMemberRoutineInfo protoMemberRoutine, ProtocolTypeInfo protocol, SourceLocation? location)
    {
        // Build substitution map for generic protocols (e.g., Supplier[S32]: T -> S32)
        Dictionary<string, string>? substitution = null;
        if (protocol.TypeArguments is { Count: > 0 })
        {
            ProtocolTypeInfo genericDef = protocol.GenericDefinition ?? protocol;
            if (genericDef.GenericParameters is { Count: > 0 })
            {
                substitution = new Dictionary<string, string>();
                for (int i = 0;
                     i < genericDef.GenericParameters.Count &&
                     i < protocol.TypeArguments.Count;
                     i++)
                {
                    substitution[key: genericDef.GenericParameters[index: i]] =
                        protocol.TypeArguments[index: i].Name;
                }
            }
        }

        // Bare `obeys Indexable` without type args: treat the protocol's generic parameters
        // as inferred-from-impl. We record the first binding we see for each param and check
        // subsequent positions for consistency, so getitem(key: S64)/setitem(key: S64) is
        // accepted but getitem(key: S64)/setitem(key: Text) is not.
        List<string>? inferableParams = null;
        if (substitution == null)
        {
            ProtocolTypeInfo genericDef = protocol.GenericDefinition ?? protocol;
            if (genericDef.GenericParameters is { Count: > 0 })
            {
                inferableParams = genericDef.GenericParameters.ToList();
                substitution = new Dictionary<string, string>();
            }
        }

        // Failability is COVARIANT: a NON-failable implementation may satisfy a FAILABLE (`!`)
        // protocol requirement — never failing is a stronger contract than may-fail, so it is always
        // a safe substitute (a `using` resource whose `enter!` can fail is satisfied by a `enter`
        // that never does). The REVERSE is unsound: a failable implementation cannot satisfy a
        // non-failable requirement, because its failures would escape unhandled at call sites that
        // assume the memberRoutine cannot fail.
        if (typeMemberRoutine.IsFailable && !protoMemberRoutine.IsFailable)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch,
                message:
                $"member routine '{typeMemberRoutine.Name}' should be non-failable to match protocol '{protocol.Name}', " +
                "but is failable (!).",
                location: location ?? new SourceLocation("", 0, 0, 0));
            return;
        }

        // Check parameter count (excluding 'me' parameter if present)
        // In-body memberRoutines have explicit 'me' as first parameter
        // Extension memberRoutines don't include 'me' in the parameter list
        int expectedParamCount = protoMemberRoutine.ParameterTypes.Count;
        bool hasMeParam = typeMemberRoutine.Parameters.Count > 0 &&
                          typeMemberRoutine.Parameters[index: 0].Name == "me";
        int actualParamCount = typeMemberRoutine.Parameters.Count - (hasMeParam
            ? 1
            : 0);

        if (actualParamCount != expectedParamCount)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch,
                message:
                $"member routine '{typeMemberRoutine.Name}' has {actualParamCount} parameter(s) but protocol '{protocol.Name}' expects {expectedParamCount}.",
                location: location ?? new SourceLocation("", 0, 0, 0));
            return;
        }

        var ctx = new ProtocolCheckContext(
            TypeMemberRoutine: typeMemberRoutine,
            Protocol: protocol,
            Substitution: substitution,
            InferableParams: inferableParams,
            Location: location);

        // Check parameter types - skip 'me' if present
        int startIndex = hasMeParam ? 1 : 0;
        for (int i = 0; i < expectedParamCount; i++)
        {
            TypeSymbol expectedType = protoMemberRoutine.ParameterTypes[index: i];
            TypeSymbol actualType = typeMemberRoutine.Parameters[index: startIndex + i].Type;
            CheckProtocolParameterType(ctx: ctx, protoMemberRoutine: protoMemberRoutine,
                paramIndex: i, expectedType: expectedType, actualType: actualType);
        }

        // Check return type
        if (protoMemberRoutine.ReturnType != null && typeMemberRoutine.ReturnType != null)
        {
            CheckProtocolReturnType(ctx: ctx,
                expectedReturn: protoMemberRoutine.ReturnType,
                actualReturn: typeMemberRoutine.ReturnType);
        }
    }

    /// <summary>
    /// Validates one parameter position of a type member routine against its protocol requirement,
    /// handling the protocol-self (Me) case and inferable-param substitution binding, reporting a
    /// mismatch when the types disagree.
    /// </summary>
    private void CheckProtocolParameterType(ProtocolCheckContext ctx,
        ProtocolMemberRoutineInfo protoMemberRoutine, int paramIndex,
        TypeSymbol expectedType, TypeSymbol actualType)
    {
        RoutineInfo typeMemberRoutine = ctx.TypeMemberRoutine;
        ProtocolTypeInfo protocol = ctx.Protocol;
        Dictionary<string, string>? substitution = ctx.Substitution;
        List<string>? inferableParams = ctx.InferableParams;
        SourceLocation? location = ctx.Location;

        // Handle protocol self type (Me) - should match the owner type
        if (expectedType is ProtocolSelfTypeInfo)
        {
            if (typeMemberRoutine.OwnerType != null &&
                !MeTypeMatches(actualType: actualType,
                    ownerType: typeMemberRoutine.OwnerType))
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch,
                    message:
                    $"Parameter '{protoMemberRoutine.ParameterNames[index: paramIndex]}' of '{typeMemberRoutine.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{typeMemberRoutine.OwnerType.Name}' (Me).",
                    location: location ?? new SourceLocation("", 0, 0, 0));
            }

            return;
        }

        string expectedName = substitution != null &&
                              substitution.TryGetValue(key: expectedType.Name,
                                  value: out string? substName)
            ? substName
            : expectedType.Name;
        if (inferableParams != null && inferableParams.Contains(item: expectedType.Name) &&
            !substitution!.ContainsKey(key: expectedType.Name))
        {
            substitution[key: expectedType.Name] = actualType.Name;
            expectedName = actualType.Name;
        }
        if (actualType.Name != expectedName)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch,
                message:
                $"Parameter '{protoMemberRoutine.ParameterNames[index: paramIndex]}' of '{typeMemberRoutine.Name}' has type '{actualType.Name}' but protocol '{protocol.Name}' expects '{expectedName}'.",
                location: location ?? new SourceLocation("", 0, 0, 0));
        }
    }

    /// <summary>
    /// Validates a type member routine's return type against its protocol requirement, handling the
    /// protocol-self (Me) case and inferable-param substitution binding, reporting a mismatch when the
    /// types disagree.
    /// </summary>
    private void CheckProtocolReturnType(ProtocolCheckContext ctx,
        TypeSymbol expectedReturn, TypeSymbol actualReturn)
    {
        RoutineInfo typeMemberRoutine = ctx.TypeMemberRoutine;
        ProtocolTypeInfo protocol = ctx.Protocol;
        Dictionary<string, string>? substitution = ctx.Substitution;
        List<string>? inferableParams = ctx.InferableParams;
        SourceLocation? location = ctx.Location;

        // Handle protocol self type (Me)
        if (expectedReturn is ProtocolSelfTypeInfo)
        {
            if (typeMemberRoutine.OwnerType != null &&
                !MeTypeMatches(actualType: actualReturn,
                    ownerType: typeMemberRoutine.OwnerType))
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch,
                    message:
                    $"member routine '{typeMemberRoutine.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{typeMemberRoutine.OwnerType.Name}' (Me).",
                    location: location ?? new SourceLocation("", 0, 0, 0));
            }

            return;
        }

        string expectedReturnName = substitution != null &&
                                    substitution.TryGetValue(key: expectedReturn.Name,
                                        value: out string? substRetName)
            ? substRetName
            : expectedReturn.Name;
        if (inferableParams != null && inferableParams.Contains(item: expectedReturn.Name) &&
            !substitution!.ContainsKey(key: expectedReturn.Name))
        {
            substitution[key: expectedReturn.Name] = actualReturn.Name;
            expectedReturnName = actualReturn.Name;
        }
        if (actualReturn.Name != expectedReturnName)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ProtocolMemberRoutineSignatureMismatch,
                message:
                $"member routine '{typeMemberRoutine.Name}' returns '{actualReturn.Name}' but protocol '{protocol.Name}' expects '{expectedReturnName}'.",
                location: location ?? new SourceLocation("", 0, 0, 0));
        }
    }

    /// <summary>
    /// Structural comparison: checks if an actual type matches the owner type for protocol Me type validation.
    /// Handles generic resolutions (e.g., Total[T] matches owner Total).
    /// </summary>
    private static bool MeTypeMatches(TypeSymbol actualType, TypeSymbol ownerType)
    {
        // Direct match
        if (ReferenceEquals(objA: actualType, objB: ownerType) ||
            actualType.Name == ownerType.Name)
        {
            return true;
        }

        // Generic resolution: actual is a generic instance of the owner type definition
        TypeSymbol? actualDef = actualType switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (actualDef != null &&
            (ReferenceEquals(objA: actualDef, objB: ownerType) ||
             actualDef.Name == ownerType.Name))
        {
            return true;
        }

        // Parameterized with own generic params: "Total[T]" matches owner "Total"
        if (ownerType.GenericParameters is { Count: > 0 } &&
            actualType.Name.StartsWith(value: ownerType.Name,
                comparisonType: StringComparison.Ordinal) &&
            actualType.Name.Length > ownerType.Name.Length &&
            actualType.Name[index: ownerType.Name.Length] == '[')
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates that a type obeys the required protocol when defining operator memberRoutines.
    /// For example, defining add requires the type to obey Addable.
    /// </summary>
    private void ValidateOperatorProtocolConformance(RoutineInfo routineInfo,
        SourceLocation? location)
    {
        // Only check memberRoutines (not functions)
        if (routineInfo.OwnerType == null)
        {
            return;
        }

        // Only WIRED operator memberRoutines (add, sub, …) require the operator protocol. A plain user
        // routine that merely shares the bare name (e.g. `routine Counter.add(n)`) is NOT an operator
        // and must not be forced to obey Addable — the name alone no longer distinguishes them, so
        // gate on the structural wired attribute.
        if (!routineInfo.IsWiredMemberRoutine)
        {
            return;
        }

        // Get the required protocol for this wired memberRoutine
        List<string>? requiredProtocols = SemanticVerifier.GetRequiredProtocols(wiredName: routineInfo.Name);
        if (requiredProtocols == null || requiredProtocols.Count == 0)
        {
            return; // Not an operator memberRoutine or no protocol required
        }

        // Re-lookup the owner type to get the updated version with protocols (realm-aware: keep the
        // owner in its own world-line so an SF-realm owner isn't rebound to the RF-realm same-name type).
        TypeSymbol? currentOwnerType = _sa._registry.LookupType(name: routineInfo.OwnerType.FullName, realm: routineInfo.OwnerType.Realm);
        if (currentOwnerType == null)
        {
            return;
        }

        // Check if the owner type EXPLICITLY obeys the required protocol
        // (structural conformance doesn't count - you must declare "obeys Protocol")
        bool followsAny = requiredProtocols.Any(predicate: proto =>
            ExplicitlyFollowsProtocol(type: currentOwnerType, protocolName: proto));
        if (!followsAny)
        {
            string protocolText = requiredProtocols.Count == 1
                ? $"'{requiredProtocols[0]}'"
                : string.Join(separator: " or ", values: requiredProtocols.Select(selector: p => $"'{p}'"));
            // Render the wired sigil ('$') the user actually wrote — the canonical Name is bare, but the
            // `$` remains surface syntax, so the diagnostic must name the operator as `add`, not `add`.
            string displayName = routineInfo.IsWiredMemberRoutine
                ? $"${routineInfo.Name}"
                : routineInfo.Name;
            _sa.ReportError(code: SemanticDiagnosticCode.OperatorWithoutProtocol,
                message:
                $"Type '{currentOwnerType.Name}' defines '{displayName}' but does not follow {protocolText}. " +
                $"Add the matching 'obeys' protocol to the type declaration.",
                location: location ?? new SourceLocation("", 0, 0, 0));
        }
    }

    /// <summary>
    /// Checks if a type explicitly declares obeying a protocol (not structural conformance).
    /// This is required for operator memberRoutines - you must explicitly declare "obeys Protocol".
    /// </summary>
    private bool ExplicitlyFollowsProtocol(TypeSymbol type, string protocolName)
    {
        // Get the list of explicitly declared protocols for this type
        List<TypeSymbol>? implementedProtocols = type switch
        {
            RecordTypeInfo record => record.ImplementedProtocols,
            EntityTypeInfo entity => entity.ImplementedProtocols,
            _ => null
        };

        if (implementedProtocols == null || implementedProtocols.Count == 0)
        {
            return false;
        }

        // Check if the protocol is directly declared (or via parent protocols recursively)
        return implementedProtocols.Any(implemented =>
            implemented.Name == protocolName ||
            implemented.BareName == protocolName ||
            (implemented is ProtocolTypeInfo proto &&
             _sa.CheckParentProtocols(proto: proto, targetName: protocolName)));
    }

    /// <summary>
    /// Resolves parameters for an external declaration.
    /// </summary>
    private void ResolveExternalParameters(ExternalDeclaration externalDecl)
    {
        RoutineInfo? routineInfo = _sa._registry.LookupRoutine(fullName: externalDecl.Name);
        if (routineInfo == null)
        {
            return;
        }

        // Set _currentRoutine so IsGenericParameter() can find generic params like T, To, From
        RoutineInfo? prevRoutine = _sa._currentRoutine;
        _sa._currentRoutine = routineInfo;

        var parameters = new List<ParameterInfo>();

        foreach (Parameter param in externalDecl.Parameters)
        {
            RejectRvalueMarkInSlot(typeExpr: param.Type,
                positionDescription: $"parameter '{param.Name}'");
            TypeSymbol paramType = param.Type != null
                ? _typeResolver.ResolveType(typeExpr: param.Type)
                : ErrorTypeInfo.Instance;

            parameters.Add(item: new ParameterInfo(name: param.Name, type: paramType)
            {
                DefaultValue = param.DefaultValue
            });
        }

        // Resolve return type. Top-level `T` legal; nested `T` in generic args rejected.
        if (externalDecl.ReturnType?.GenericArguments is { } extRetArgs)
        {
            foreach (TypeExpression arg in extRetArgs)
            {
                RejectRvalueMarkInSlot(typeExpr: arg, positionDescription: "type argument");
            }
        }

        TypeSymbol? returnType = externalDecl.ReturnType != null
            ? _typeResolver.ResolveType(typeExpr: externalDecl.ReturnType)
            : null;

        _sa._currentRoutine = prevRoutine;

        // Update the routine info with resolved parameters and generic info
        _sa._registry.UpdateRoutine(routine: routineInfo,
            parameters: parameters,
            returnType: returnType,
            genericParameters: externalDecl.GenericParameters,
            genericConstraints: externalDecl.GenericConstraints);
    }

    #endregion

    // Static helpers

    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeInfo r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is "Maybe" or "Result" or "Lookup" ? baseName : null;
    }

    private static bool IsCarrierType(TypeSymbol type) => GetCarrierBaseName(type: type) != null;

    private static bool IsMaybeType(TypeSymbol type) => GetCarrierBaseName(type: type) == "Maybe";
}
