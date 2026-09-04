using System.Collections.Generic;
using System.Linq;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Synthesis;

/// <summary>
/// Phase D synthesizer: lazily generates transparent-forwarding routines on wrapper
/// types (T, Viewing[T], Modifying[T], etc.) when user code calls a memberRoutine that
/// exists on the inner type T but not directly on the wrapper.
///
/// Synthesis anchors on the wrapper's generic definition (e.g. T) so that
/// monomorphization handles per-instance specialization.  The forwarder body is:
///
///   danger
///     var raw = Hijacked[T](me)
///     return raw.extract().MemberRoutine(arg1: arg1, ...)
///
/// where T is the wrapper's generic parameter.  When monomorphized with T->List[Byte],
/// the body's Hijacked[T] becomes Hijacked[List[Byte]], and expression types resolve
/// transitively through raw.extract() to the concrete inner type.
///
/// Policy:
///   - Read-only wrappers (Viewing, Consulting) forward ONLY @readonly memberRoutines of T.
///   - All other wrappers forward any modification category.
///
/// Signature synthesis: params/return are taken from the inner memberRoutine's signature
/// on the inner-generic-def type (e.g. List[T].getitem!).  GMP's
/// BuildConcreteRoutineInfo performs name-based substitution at monomorphization
/// time.  For memberRoutines whose return depends on the inner's generic param (e.g.
/// List[T].getitem! returning T), the forwarder is marked with
/// <see cref="RoutineInfo.WrapperForwarderInnerMemberRoutine"/> so GMP can re-resolve the
/// signature against the concrete inner type.
/// </summary>
internal sealed class WrapperForwardingPass
{
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, (RoutineInfo Routine, Statement Body)> _synthesizedBodies;
    private readonly HashSet<string> _synthesizedForwarderKeys;

    /// <summary>Synthetic source location used for compiler-generated AST nodes.</summary>
    private static readonly SourceLocation _synthLoc = new(FileName: "", Line: 0, Column: 0, Position: 0);

    /// <summary>
    /// All wrapper types recognized by the compiler for layout/dispatch purposes
    /// (codegen write-through, GMP body selection, auto-wired registration, etc.).
    /// </summary>
    private static readonly IReadOnlySet<string> WrapperTypes = RuntimeContract.WrapperTypes;

    /// <summary>
    /// Wrapper types that transparently forward inner-type memberRoutines. Hijacked[T] is the
    /// raw-pointer escape hatch — callers must explicitly use peek() / as_entity() — so
    /// it is excluded here even though it is a wrapper for layout purposes.
    /// </summary>
    private static readonly IReadOnlySet<string> ForwardingWrapperTypes =
        RuntimeContract.ForwardingWrapperTypes;

    /// <summary>
    /// memberRoutine names that codegen invokes implicitly on wrappers without going through
    /// semantic analysis (so the lazy synthesis path in member-access / call dispatch
    /// will not fire for them). RunEager seeds these for every concrete wrapper instance;
    /// all other memberRoutines are synthesized lazily on first reference.
    /// </summary>
    private static readonly HashSet<string> ImplicitlyInvokedMemberRoutines =
    [
        "destroy",
        // Operators/hashing/display: invoked from generic stdlib container
        // bodies after monomorphization, so they bypass SA's lazy synthesis
        // path. Wrappers do not define these themselves — they transparently
        // forward to inner T (e.g. Text.eq -> Text.eq).
        "eq",
        "ne",
        "cmp",
        "lt",
        "le",
        "gt",
        "ge",
        "hash",
        "represent",
        "diagnose"
    ];

    /// <summary>
    /// Exposes the wrapper type base names so GMP can detect wrapper memberRoutines during body selection.
    /// </summary>
    internal static IReadOnlySet<string> WrapperTypeNames => WrapperTypes;

    /// <summary>
    /// Read-only wrapper types that can only access @readonly memberRoutines.
    /// </summary>
    private static readonly IReadOnlySet<string> ReadOnlyWrapperTypes =
        RuntimeContract.ReadOnlyWrapperTypes;

    public WrapperForwardingPass(TypeRegistry registry,
        Dictionary<string, (RoutineInfo Routine, Statement Body)> synthesizedBodies,
        HashSet<string> synthesizedForwarderKeys)
    {
        _registry = registry;
        _synthesizedBodies = synthesizedBodies;
        _synthesizedForwarderKeys = synthesizedForwarderKeys;
    }

    /// <summary>
    /// Eagerly synthesizes forwarders on all concrete wrapper-type instantiations for every
    /// memberRoutine found on their inner type.  Called after stdlib body analysis so that wrapper
    /// memberRoutines used only implicitly (e.g. release() via scope cleanup) are still forwarded.
    /// </summary>
    public void RunEager()
    {
        // Collect from both resolution caches: RecordTypeInfo resolutions AND WrapperTypeInfo resolutions.
        var candidates =
            _registry.AllConcreteGenericInstances
                     .Where(predicate: IsWrapperType)
                     .Concat(_registry.AllConcreteWrapperInstances)
                     .Distinct()
                     .ToList();

        foreach (TypeSymbol wrapperType in candidates)
        {
            TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
            if (innerType is null or GenericParameterTypeInfo)
                continue;

            TypeSymbol innerLookupType = innerType switch
            {
                RecordTypeInfo { GenericDefinition: { } d } => d,
                EntityTypeInfo { GenericDefinition: { } d } => d,
                _ => innerType
            };

            // Narrow to implicit-call memberRoutines only. User-visible calls hit the lazy path
            // (TrySynthesizeWrapperForwarder in member-access / call dispatch); only memberRoutines
            // codegen invokes without semantic analysis (scope cleanup, RC ops) need eager
            // seeding. This avoids fanning out a forwarder per (wrapper × every memberRoutine of T).
            foreach (RoutineInfo innerMemberRoutine in _registry.GetMemberRoutinesForOwner(ownerType: innerLookupType))
            {
                if (!ImplicitlyInvokedMemberRoutines.Contains(item: innerMemberRoutine.Name))
                    continue;
                if (innerMemberRoutine.Annotations.Contains(value: "innate")) continue;
                TrySynthesize(wrapperType: wrapperType,
                    memberRoutineName: innerMemberRoutine.Name,
                    isFailable: innerMemberRoutine.IsFailable);
            }
        }
    }

    /// <summary>
    /// Attempts to synthesize a forwarding routine on a wrapper type that delegates to
    /// a matching memberRoutine on the wrapper's inner type T. Returns null if synthesis is
    /// not applicable (not a wrapper, no inner T, no matching inner memberRoutine, or read-only
    /// wrapper rejecting a non-readonly inner memberRoutine).
    /// </summary>
    public RoutineInfo? TrySynthesize(TypeSymbol wrapperType, string memberRoutineName, bool isFailable)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        TypeSymbol? wrapperDef = wrapperType switch
        {
            RecordTypeInfo { GenericDefinition: { } def } => def,
            EntityTypeInfo { GenericDefinition: { } def } => def,
            WrapperTypeInfo => _registry.LookupType(name: wrapperType.Name),
            _ => wrapperType
        };

        if (wrapperDef == null || !wrapperDef.IsGenericDefinition ||
            wrapperDef.GenericParameters is not { Count: 1 })
        {
            return null;
        }

        string genericParamName = wrapperDef.GenericParameters[index: 0];

        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null)
        {
            return null;
        }

        TypeSymbol innerLookupType = innerType switch
        {
            RecordTypeInfo { GenericDefinition: { } d } => d,
            EntityTypeInfo { GenericDefinition: { } d } => d,
            _ => innerType
        };

        // Resolve the memberRoutine against the CONCRETE inner type first (e.g. `Box[S64]`, `List[S64]`) so a
        // generic entity's owner type parameters bind (`Box[T].get` → `Box[S64].get -> S64`). Falling
        // straight to the generic DEFINITION (innerLookupType = `Box[T]`) would return an
        // un-monomorphized `-> T` memberRoutine, which codegen rejects ("no concrete SA-resolved return type").
        // Non-generic inners have innerType == innerLookupType, so this is a no-op there.
        RoutineInfo? innerMemberRoutine =
            _registry.LookupMemberRoutine(type: innerType, memberRoutineName: memberRoutineName, isFailable: isFailable)
            ?? _registry.LookupMemberRoutine(type: innerLookupType, memberRoutineName: memberRoutineName,
                isFailable: isFailable);
        // No concrete inner impl → no forwarder. A resolution to the ABSTRACT protocol member (e.g. an
        // eagerly-seeded `eq`/`cmp`/`hash` whose inner entity does NOT obey Equatable/Comparable/Hashable,
        // so the lookup falls back to `Equatable.eq`) does NOT count — forwarding to it would emit a call to
        // an unimplemented abstract symbol. The wrapper only forwards what the inner actually implements.
        if (innerMemberRoutine == null || innerMemberRoutine.OwnerType is ProtocolTypeInfo)
        {
            return null;
        }

        // Representation-unified Suflae entity memberRoutine: its `me` is ALREADY `Roamed[E]` (SignatureResolver
        // sets MeType), so the "bare" memberRoutine IS the Roamed memberRoutine — it does its own lock_enter + project
        // through RoamController.data. Wrapping it in the projecting forwarder below would project the
        // controller to the entity and hand a BARE entity to a memberRoutine that projects AGAIN → double
        // projection → the controller header is read as entity fields → crash. So resolve a `Roamed[E]`
        // receiver call straight to the inner memberRoutine (passing the Roamed handle as `me`), exactly as a
        // receiver that was still bare at SA already does. No forwarder is registered.
        if (wrapperType.BareName == RuntimeContract.Roamed
            && innerMemberRoutine.MeType is RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed }
                                  or WrapperTypeInfo { Name: RuntimeContract.Roamed })
        {
            return innerMemberRoutine;
        }

        if (IsReadOnlyWrapper(type: wrapperType) && !innerMemberRoutine.IsReadOnly)
        {
            return null;
        }

        // Roamed failable forwarders: the `when`-re-propagation body IS built (see
        // BuildWrapperForwarderBody's isFailable path) but is currently GATED OFF — the synthesized
        // re-throw's `Core.Crashable.crash_message` gets reachability-pruned ("declared+called but never
        // defined"); the seed attempt in RoutineReachabilityPass (LookupType("Crashable")) did not
        // resolve it. Re-enable by fixing that seed (find the correct crash_message owner/lookup).
        if (wrapperType.BareName == RuntimeContract.Roamed
            && innerMemberRoutine.IsFailable)
        {
            return null;
        }


        string cacheKey = $"{wrapperDef.Name}.{memberRoutineName}#{(isFailable ? "!" : "")}";
        if (!_synthesizedForwarderKeys.Add(item: cacheKey))
        {
            var cached = _registry.LookupMemberRoutine(type: wrapperType,
                memberRoutineName: memberRoutineName,
                isFailable: isFailable) ??
                _registry.LookupMemberRoutine(type: wrapperDef,
                    memberRoutineName: memberRoutineName,
                    isFailable: isFailable);
            return cached;
        }

        // Don't overwrite a memberRoutine already defined on the wrapper's generic def
        // (source-defined routines like represent, diagnose, destroy take precedence).
        var existingOnDef = _registry.LookupMemberRoutine(type: wrapperDef,
                memberRoutineName: memberRoutineName,
                isFailable: isFailable);
        if (existingOnDef != null)
        {
            return _registry.LookupMemberRoutine(type: wrapperType,
                memberRoutineName: memberRoutineName,
                isFailable: isFailable);
        }

        // Filter out owner-level generics from the inner memberRoutine's GenericParameters.
        // `BTreeSetNode[T].keys_add_last(value: T)` registers a RoutineInfo whose
        // GenericParameters carries `T` (the owner-level param) — propagating that onto the
        // forwarder makes the forwarder look memberRoutine-generic in T, so GMP later mangles it as
        // `Owned[BTreeSetNode[S64]].keys_add_last[S64]` while codegen call sites use the
        // un-suffixed `Owned[BTreeSetNode[S64]].keys_add_last`. Strip owner-level params so
        // only true memberRoutine-level generics (e.g. `Hijacked[T].recast_as[U]` -> `[U]`) survive.
        List<string>? innerOwnerParams = innerLookupType.GenericParameters;
        List<string>? filteredGenericParams = innerMemberRoutine.GenericParameters;
        if (filteredGenericParams is { Count: > 0 } && innerOwnerParams is { Count: > 0 })
        {
            filteredGenericParams = filteredGenericParams
                .Where(predicate: gp => !innerOwnerParams.Contains(value: gp))
                .ToList();
            if (filteredGenericParams.Count == 0) filteredGenericParams = null;
        }
        List<GenericConstraintDeclaration>? filteredConstraints = innerMemberRoutine.GenericConstraints;
        if (filteredConstraints is { Count: > 0 } && innerOwnerParams is { Count: > 0 })
        {
            filteredConstraints = filteredConstraints
                .Where(predicate: c => !innerOwnerParams.Contains(value: c.ParameterName))
                .ToList();
            if (filteredConstraints.Count == 0) filteredConstraints = null;
        }

        // Resolve name collisions between the wrapper's generic params and the inner memberRoutine's
        // owner-level generic params (both commonly use `T`). The forwarder's signature carries
        // the inner memberRoutine's parameter types verbatim, which reference inner-T by name. Without
        // distinct names, GMP's typeSubs dict gets a single key `T` mapping to the wrapper's
        // inner instance (e.g. `T -> BTreeSetNode[S64]`) and never registers `inner-T -> S64`.
        // Result: a value-of-T parameter codegens as ptr instead of i64 → ABI mismatch.
        //
        // Rename colliding inner names so the forwarder's param/return types reference a
        // disambiguated GenericParameterTypeInfo with a `ForwarderOriginalName` marker.
        // Substitution sites (GenericAstRewriter.ResolveType, codegen's ResolveTypeSubstitution,
        // BuildResolvedRoutineTypeSubstitutions) check that property to recover the original
        // inner-param name — this avoids string-matching a `__rfwd_T__` sentinel everywhere.
        Dictionary<string, TypeInfo>? innerRename = null;
        if (innerOwnerParams is { Count: > 0 } &&
            wrapperDef.GenericParameters is { Count: > 0 } wrapperParams)
        {
            foreach (string ip in innerOwnerParams)
            {
                if (!wrapperParams.Contains(value: ip)) continue;
                innerRename ??= new Dictionary<string, TypeInfo>();
                // The Name still has to be unique vs the wrapper's own param so dict-keyed
                // lookups don't collide; the structural marker is `ForwarderOriginalName`.
                innerRename[ip] = new GenericParameterTypeInfo(name: $"__rfwd_{ip}__")
                {
                    ForwarderOriginalName = ip
                };
            }
        }

        List<ParameterInfo> forwarderParameters = innerMemberRoutine.Parameters;
        TypeSymbol? forwarderReturnType = innerMemberRoutine.ReturnType;
        if (innerRename is { Count: > 0 })
        {
            forwarderParameters = innerMemberRoutine.Parameters
                .Select(selector: p => p.WithSubstitutedType(
                    newType: RoutineInfo.SubstituteType(type: p.Type, substitution: innerRename)))
                .ToList();
            if (forwarderReturnType != null)
            {
                forwarderReturnType = RoutineInfo.SubstituteType(
                    type: forwarderReturnType, substitution: innerRename);
            }
        }

        var forwarder = new RoutineInfo(name: innerMemberRoutine.Name)
        {
            Kind = RoutineKind.MemberRoutine,
            OwnerType = wrapperDef,
            Parameters = forwarderParameters,
            ReturnType = forwarderReturnType,
            IsFailable = innerMemberRoutine.IsFailable,
            DeclaredMutation = innerMemberRoutine.DeclaredMutation,
            MutationCategory = innerMemberRoutine.MutationCategory,
            Visibility = innerMemberRoutine.Visibility,
            Location = innerMemberRoutine.Location,
            Module = innerMemberRoutine.Module,
            Annotations = innerMemberRoutine.Annotations,
            IsSynthesized = true,
            WrapperForwarderInnerMemberRoutine = innerMemberRoutine,
            WrapperForwarderInnerGenericDef = innerLookupType,
            GenericParameters = filteredGenericParams,
            GenericConstraints = filteredConstraints,
        };

        // RC record wrappers (Retained[T], Tracked[T]) are structs with a `data: Hijacked[T]`
        // field — cannot cast `me` to a pointer. Pointer wrappers use Hijacked[T](me) directly.
        // Detect by checking for an actual `data` member on the record def.
        string? dataFieldName = wrapperDef is RecordTypeInfo recDef &&
                                recDef.LookupMemberVariable(memberVariableName: "data") != null
            ? "data"
            : null;

        Statement body = BuildWrapperForwarderBody(
            wrapperType: wrapperDef,
            innerMemberRoutine: innerMemberRoutine,
            genericParamName: genericParamName,
            memberRoutineName: innerMemberRoutine.Name,
            isFailable: innerMemberRoutine.IsFailable,
            parameters: innerMemberRoutine.Parameters,
            hasReturnValue: innerMemberRoutine.ReturnType != null &&
                innerMemberRoutine.ReturnType.Name != "None",
            dataFieldName: dataFieldName,
            innerIsEntity: innerType is EntityTypeInfo);

        _registry.RegisterRoutine(routine: forwarder);
        _synthesizedBodies[key: forwarder.RegistryKey] = (forwarder, body);

        return _registry.LookupMemberRoutine(type: wrapperType,
            memberRoutineName: memberRoutineName,
            isFailable: isFailable) ?? forwarder;
    }

    /// <summary>
    /// Builds the AST body:
    ///
    ///   Pointer wrappers (dataFieldName == null):
    ///     danger
    ///       var raw = Hijacked[T](me)
    ///       [return] raw.extract().MemberRoutineName(param1: param1, ...)
    ///
    ///   Record-struct wrappers (dataFieldName == "data"):
    ///     danger
    ///       [return] me.data.extract().MemberRoutineName(param1: param1, ...)
    ///
    /// where T is the wrapper's generic parameter name.
    /// </summary>
    private DangerStatement BuildWrapperForwarderBody(TypeSymbol wrapperType, RoutineInfo innerMemberRoutine,
        string genericParamName,
        string memberRoutineName, bool isFailable, List<ParameterInfo> parameters,
        bool hasReturnValue, string? dataFieldName = null, bool innerIsEntity = false)
    {
        // The forwarded call's name is always bare; its failability is carried structurally on the
        // callee MemberExpression (IsFailable), never appended to the name.
        string callPropertyName = memberRoutineName;
        TypeSymbol innerType = wrapperType.TypeArguments is { Count: > 0 }
            ? wrapperType.TypeArguments[0]
            : new GenericParameterTypeInfo(name: genericParamName);
        var forwardedArgs = new List<Expression>();
        foreach (ParameterInfo p in parameters)
        {
            if (p.Name == "me")
                continue;
            forwardedArgs.Add(item: new NamedArgumentExpression(
                Name: p.Name,
                Value: new IdentifierExpression(Name: p.Name, Location: _synthLoc),
                Location: _synthLoc));
        }

        List<Statement> innerStatements;

        if (dataFieldName != null)
        {
            innerStatements = BuildRecordStructForwarderStatements(wrapperType: wrapperType,
                innerMemberRoutine: innerMemberRoutine, dataFieldName: dataFieldName,
                callPropertyName: callPropertyName, isFailable: isFailable, hasReturnValue: hasReturnValue,
                innerType: innerType, forwardedArgs: forwardedArgs);
        }
        else if (wrapperType.BareName is RuntimeContract.Retained or RuntimeContract.Tracked or RuntimeContract.Roamed)
        {
            innerStatements = BuildRcWrapperForwarderStatements(wrapperType: wrapperType,
                innerMemberRoutine: innerMemberRoutine, genericParamName: genericParamName,
                memberRoutineName: memberRoutineName, callPropertyName: callPropertyName,
                isFailable: isFailable, hasReturnValue: hasReturnValue, innerType: innerType,
                forwardedArgs: forwardedArgs);
        }
        else
        {
            innerStatements = BuildPointerWrapperForwarderStatements(innerMemberRoutine: innerMemberRoutine,
                genericParamName: genericParamName, callPropertyName: callPropertyName,
                isFailable: isFailable, hasReturnValue: hasReturnValue, innerIsEntity: innerIsEntity,
                innerType: innerType, forwardedArgs: forwardedArgs);
        }

        return new DangerStatement(
            Body: new BlockStatement(Statements: innerStatements, Location: _synthLoc),
            Location: _synthLoc);
    }

    /// <summary>
    /// Record-struct wrapper (dataFieldName == "data"): <c>me.data.peek().MemberRoutine(...)</c>.
    /// Skips the `raw` variable entirely — no type inference needed.
    /// </summary>
    private List<Statement> BuildRecordStructForwarderStatements(TypeSymbol wrapperType,
        RoutineInfo innerMemberRoutine, string dataFieldName, string callPropertyName, bool isFailable,
        bool hasReturnValue, TypeSymbol innerType, List<Expression> forwardedArgs)
    {
        TypeInfo? wrapperDataType =
            (wrapperType as RecordTypeInfo)?.LookupMemberVariable(memberVariableName: dataFieldName)?.Type;
        var meRef = new IdentifierExpression(Name: "me", Location: _synthLoc)
            { ResolvedType = wrapperType };
        var dataAccess = new MemberExpression(
            Object: meRef,
            MemberName: dataFieldName,
            Location: _synthLoc)
        {
            ResolvedType = wrapperDataType
        };
        RoutineInfo? extractMemberRoutine = wrapperDataType != null
            ? _registry.LookupMemberRoutine(type: wrapperDataType, memberRoutineName: RuntimeContract.RawPointer.Peek)
            : null;
        var readCall = new CallExpression(
            Callee: new MemberExpression(
                Object: dataAccess,
                MemberName: RuntimeContract.RawPointer.Peek,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc)
        {
            ResolvedRoutine = extractMemberRoutine,
            ResolvedType = innerType
        };
        var innerCall = new CallExpression(
            Callee: new MemberExpression(
                Object: readCall,
                MemberName: callPropertyName,
                Location: _synthLoc) { IsFailable = isFailable },
            Arguments: forwardedArgs,
            Location: _synthLoc)
        {
            // ResolvedRoutine intentionally left null: this forwarder is generated once
            // per wrapperDef and reused across all inner T. Baking innerMemberRoutine here would
            // freeze the call to whichever inner type was resolved first (e.g. binding
            // to BTreeListNode.keys_add_last forever, even when monomorphized for
            // Modifying[BTreeSetNode[S64]]). Leaving it null lets RoutineReachabilityPass
            // re-resolve the call from the substituted receiver type at monomorphization.
            ResolvedType = innerMemberRoutine.ReturnType
        };
        Statement callStmt = hasReturnValue
            ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
            : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);
        return [callStmt];
    }

    /// <summary>
    /// RC wrappers (Retained/Tracked/Roamed): `me` is a ptr to <c>RetainController[T]</c>, NOT to T
    /// directly. Reaching T requires double-indirection through the controller's <c>data: Hijacked[T]</c>
    /// field:
    /// <code>
    ///   danger
    ///     var raw  = Hijacked[RetainController[T]](me)
    ///     var ctrl = raw.as_entity()              # RetainController[T] ptr
    ///     [return] ctrl.raw_data().as_entity().MemberRoutine(args...)
    /// </code>
    /// Without this, the pointer-wrapper path would emit
    /// <c>Hijacked[T](me).as_entity().MemberRoutine(...)</c>, treating the controller's strong+weak
    /// counts (first 8 bytes) as if they were T's first 8 bytes. A `Roaming` guard indirects through
    /// <c>RoamController.data_ptr()</c>; Retained/Tracked through <c>RetainController.raw_data()</c>.
    /// Both just reach the inner entity — for `Roaming` the lock is already held by the enclosing
    /// `using` (enter), so the forwarder only reaches + calls (release happens at exit on every path).
    /// </summary>
    private List<Statement> BuildRcWrapperForwarderStatements(TypeSymbol wrapperType,
        RoutineInfo innerMemberRoutine, string genericParamName, string memberRoutineName,
        string callPropertyName, bool isFailable, bool hasReturnValue, TypeSymbol innerType,
        List<Expression> forwardedArgs)
    {
        bool isRoamed = wrapperType.BareName == RuntimeContract.Roamed;
        bool viaRoamController = isRoamed;
        string controllerName = viaRoamController ? "RoamController" : "RetainController";
        string dataRevealName = viaRoamController
            ? "data_ptr"
            : RuntimeContract.RefCount.RawData;
        var controllerTypeExpr = new TypeExpression(
            Name: controllerName,
            GenericArguments:
            [
                new TypeExpression(Name: genericParamName, GenericArguments: null,
                    Location: _synthLoc)
            ],
            Location: _synthLoc);
        var hijackedCtrlCtor = new CreatorExpression(
            TypeName: RuntimeContract.Hijacked,
            TypeArguments: [controllerTypeExpr],
            MemberVariables:
                [("", new IdentifierExpression(Name: "me", Location: _synthLoc))],
            Location: _synthLoc);
        var rawDecl = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: "raw",
                Type: null,
                Initializer: hijackedCtrlCtor,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc);
        // Build TypeInfo annotations so codegen's type-resolution gate accepts the
        // synthesized AST. Mirror the pointer-wrapper branch below: ResolvedType on
        // each `raw`/`ctrl` identifier and ResolvedRoutine + ResolvedType on each Call.
        // The inner T may still be a GenericParameterTypeInfo at synth time; codegen's
        // ApplyTypeSubstitutions substitutes T at monomorphization.
        //
        // Annotate with the OPEN instantiation RetainController[T] (T = the wrapper's
        // param), never the bare generic def. The shared synth body is re-resolved by
        // later consumers (SA lazy analysis, GMP rewrite, codegen re-lookup) under a
        // substitution keyed on T; the open form is idempotent there — the same shape
        // SA bakes into Retained.rf's source bodies — while a bare def gets freshly
        // instantiated with whatever binding is at hand, double-wrapping the controller
        // (RetainController[RetainController[X]]) and killing forwarder body emission
        // (undefined symbol at link).
        TypeSymbol? retainControllerDef = _registry.LookupType(name: controllerName);
        TypeSymbol? retainControllerType = retainControllerDef is { IsGenericDefinition: true }
            ? _registry.GetOrCreateResolution(genericDef: retainControllerDef,
                typeArguments: [innerType])
            : retainControllerDef;
        TypeSymbol hijackedCtrlType = new WrapperTypeInfo(
            wrapperName: RuntimeContract.Hijacked,
            innerType: retainControllerType ?? innerType,
            isReadOnly: false);
        TypeSymbol hijackedInnerType = new WrapperTypeInfo(
            wrapperName: RuntimeContract.Hijacked,
            innerType: innerType,
            isReadOnly: false);
        RoutineInfo? ctrlRevealMemberRoutine = _registry.LookupMemberRoutine(
            type: hijackedCtrlType, memberRoutineName: RuntimeContract.RawPointer.AsEntity);
        RoutineInfo? borrowDataMemberRoutine = retainControllerType != null
            ? _registry.LookupMemberRoutine(type: retainControllerType, memberRoutineName: dataRevealName)
            : null;
        RoutineInfo? innerRevealMemberRoutine = _registry.LookupMemberRoutine(
            type: hijackedInnerType, memberRoutineName: RuntimeContract.RawPointer.AsEntity);

        var ctrlCall = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: "raw", Location: _synthLoc)
                    { ResolvedType = hijackedCtrlType },
                MemberName: RuntimeContract.RawPointer.AsEntity,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc)
        {
            ResolvedRoutine = ctrlRevealMemberRoutine,
            ResolvedType = retainControllerType
        };
        var ctrlDecl = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: "ctrl",
                Type: null,
                Initializer: ctrlCall,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc);
        var borrowCall = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: "ctrl", Location: _synthLoc)
                    { ResolvedType = retainControllerType },
                MemberName: RuntimeContract.RefCount.RawData,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc)
        {
            ResolvedRoutine = borrowDataMemberRoutine,
            ResolvedType = hijackedInnerType
        };
        var innerRevealCall = new CallExpression(
            Callee: new MemberExpression(
                Object: borrowCall,
                MemberName: RuntimeContract.RawPointer.AsEntity,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc)
        {
            ResolvedRoutine = innerRevealMemberRoutine,
            ResolvedType = innerType
        };
        var innerCall = new CallExpression(
            Callee: new MemberExpression(
                Object: innerRevealCall,
                MemberName: callPropertyName,
                Location: _synthLoc) { IsFailable = isFailable },
            Arguments: forwardedArgs,
            Location: _synthLoc)
        {
            // ResolvedRoutine intentionally null — see record-struct branch for reasoning.
            ResolvedType = innerMemberRoutine.ReturnType
        };
        if (isRoamed)
        {
            return BuildRoamedLockedForwarderStatements(wrapperType: wrapperType,
                innerMemberRoutine: innerMemberRoutine, memberRoutineName: memberRoutineName,
                isFailable: isFailable, hasReturnValue: hasReturnValue, innerType: innerType,
                forwardedArgs: forwardedArgs, rawDecl: rawDecl, ctrlDecl: ctrlDecl,
                innerRevealCall: innerRevealCall, innerCall: innerCall);
        }

        Statement callStmt = hasReturnValue
            ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
            : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);
        return [rawDecl, ctrlDecl, callStmt];
    }

    /// <summary>
    /// Wraps the RC-forwarder body of a <c>Roamed</c> wrapper in a mode-checked lock, released
    /// EXPLICITLY (synthesized forwarder bodies are not run through ScopeTeardownLoweringPass, so an
    /// owned-guard destroy would never be inserted). Failable calls route through the throw-based
    /// <c>check_</c> variant and a <c>when</c> that re-propagates AFTER releasing the lock in each arm.
    /// </summary>
    private List<Statement> BuildRoamedLockedForwarderStatements(TypeSymbol wrapperType,
        RoutineInfo innerMemberRoutine, string memberRoutineName, bool isFailable, bool hasReturnValue,
        TypeSymbol innerType, List<Expression> forwardedArgs, Statement rawDecl, Statement ctrlDecl,
        CallExpression innerRevealCall, CallExpression innerCall)
    {
        RoutineInfo? lockEnter = _registry.LookupMemberRoutine(type: wrapperType, memberRoutineName: "lock_enter");
        RoutineInfo? lockExit = _registry.LookupMemberRoutine(type: wrapperType, memberRoutineName: "lock_exit");
        ExpressionStatement MkLock(RoutineInfo? m, string verb) => new ExpressionStatement(
            Expression: new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = wrapperType },
                    MemberName: verb, Location: _synthLoc),
                Arguments: [], Location: _synthLoc) { ResolvedRoutine = m },
            Location: _synthLoc);

        if (isFailable)
        {
            // Failable: call the throw-based `check_` variant (non-propagating carrier), then a
            // `when` re-propagates AFTER releasing the lock in each arm — mirrors
            // ErrorHandlingVariantPass.BuildCarrierPropagationWhen, but with lock_exit inserted
            // so the lock is freed on BOTH the failure (throw) and success paths.
            TypeSymbol innerDef = innerType switch
            {
                EntityTypeInfo { GenericDefinition: { } ed } => ed,
                RecordTypeInfo { GenericDefinition: { } rd } => rd,
                _ => innerType
            };
            RoutineInfo? checkM = _registry.LookupMemberRoutine(type: innerDef,
                memberRoutineName: "check_" + memberRoutineName, isFailable: false);
            var checkSubject = new CallExpression(
                Callee: new MemberExpression(Object: innerRevealCall,
                    MemberName: "check_" + memberRoutineName, Location: _synthLoc),
                Arguments: forwardedArgs, Location: _synthLoc)
            { ResolvedType = checkM?.ReturnType };
            var whenStmt = new WhenStatement(
                Expression: checkSubject,
                Clauses:
                [
                    new WhenClause(
                        Pattern: new CrashablePattern(ErrorType: null, VariableName: "__rf_e", Location: _synthLoc),
                        Body: new BlockStatement(
                            Statements: [MkLock(lockExit, "lock_exit"),
                                new ThrowStatement(Error: new IdentifierExpression(Name: "__rf_e", Location: _synthLoc), Location: _synthLoc)],
                            Location: _synthLoc),
                        Location: _synthLoc),
                    new WhenClause(
                        Pattern: new ElsePattern(VariableName: "__rf_v", Location: _synthLoc),
                        Body: new BlockStatement(
                            Statements: [MkLock(lockExit, "lock_exit"),
                                new ReturnStatement(Value: new IdentifierExpression(Name: "__rf_v", Location: _synthLoc), Location: _synthLoc)],
                            Location: _synthLoc),
                        Location: _synthLoc)
                ],
                Location: _synthLoc);
            return [MkLock(lockEnter, "lock_enter"), rawDecl, ctrlDecl, whenStmt];
        }

        if (hasReturnValue)
        {
            Statement resultDecl = new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: "__rf_locked", Type: null,
                    Initializer: innerCall, Visibility: VisibilityModifier.Open, Location: _synthLoc),
                Location: _synthLoc);
            Statement retStmt = new ReturnStatement(
                Value: new IdentifierExpression(Name: "__rf_locked", Location: _synthLoc)
                    { ResolvedType = innerMemberRoutine.ReturnType },
                Location: _synthLoc);
            return [MkLock(lockEnter, "lock_enter"), rawDecl, ctrlDecl, resultDecl, MkLock(lockExit, "lock_exit"), retStmt];
        }

        return [MkLock(lockEnter, "lock_enter"), rawDecl, ctrlDecl,
            new ExpressionStatement(Expression: innerCall, Location: _synthLoc), MkLock(lockExit, "lock_exit")];
    }

    /// <summary>
    /// Pointer wrapper: <c>var raw = Hijacked[T](me); raw.as_entity()/peek().MemberRoutine(...)</c>.
    /// Entity inner types: as_entity() reinterprets the ptr directly as T (no dereference) — correct
    /// for T where me IS the entity ptr, not a slot holding one. Record inner types: peek()
    /// dereferences the ptr to load the value — correct for Hijacked[RecordType] where the ptr points
    /// to a heap/stack slot. innerIsEntity is determined from the concrete inner type at the call site
    /// so generic-def forwarder bodies (where innerType is GenericParameterTypeInfo) get the correct
    /// access memberRoutine even before T is substituted.
    /// </summary>
    private List<Statement> BuildPointerWrapperForwarderStatements(RoutineInfo innerMemberRoutine,
        string genericParamName, string callPropertyName, bool isFailable, bool hasReturnValue,
        bool innerIsEntity, TypeSymbol innerType, List<Expression> forwardedArgs)
    {
        string accessMemberRoutineName = innerIsEntity ? RuntimeContract.RawPointer.AsEntity : RuntimeContract.RawPointer.Peek;
        var hijackedCall = new CreatorExpression(
            TypeName: RuntimeContract.Hijacked,
            TypeArguments:
            [
                new TypeExpression(Name: genericParamName, GenericArguments: null,
                    Location: _synthLoc)
            ],
            MemberVariables:
                [("", new IdentifierExpression(Name: "me", Location: _synthLoc))],
            Location: _synthLoc);
        var rawDecl = new DeclarationStatement(
            Declaration: new VariableDeclaration(
                Name: "raw",
                Type: null,
                Initializer: hijackedCall,
                Visibility: VisibilityModifier.Open,
                Location: _synthLoc),
            Location: _synthLoc);
        TypeSymbol hijackedInnerType = new WrapperTypeInfo(
            wrapperName: RuntimeContract.Hijacked,
            innerType: innerType,
            isReadOnly: false);
        RoutineInfo? accessMemberRoutine = _registry.LookupMemberRoutine(type: hijackedInnerType,
            memberRoutineName: accessMemberRoutineName);
        var readCall = new CallExpression(
            Callee: new MemberExpression(
                Object: new IdentifierExpression(Name: "raw", Location: _synthLoc)
                    { ResolvedType = hijackedInnerType },
                MemberName: accessMemberRoutineName,
                Location: _synthLoc),
            Arguments: [],
            Location: _synthLoc)
        {
            ResolvedRoutine = accessMemberRoutine,
            ResolvedType = innerType
        };
        var innerCall = new CallExpression(
            Callee: new MemberExpression(
                Object: readCall,
                MemberName: callPropertyName,
                Location: _synthLoc) { IsFailable = isFailable },
            Arguments: forwardedArgs,
            Location: _synthLoc)
        {
            // ResolvedRoutine intentionally left null — see the record-struct branch above
            // for the full reasoning. Same issue applies to pointer wrappers.
            ResolvedType = innerMemberRoutine.ReturnType
        };
        Statement callStmt = hasReturnValue
            ? new ReturnStatement(Value: innerCall, Location: _synthLoc)
            : new ExpressionStatement(Expression: innerCall, Location: _synthLoc);
        return [rawDecl, callStmt];
    }

    /// <summary>
    /// Checks if a type is a forwarding wrapper (Viewing, Modifying, Guarded, etc.).
    /// Hijacked is intentionally excluded — its API is the explicit extract/as_entity/inject
    /// surface, not transparent forwarding of T's memberRoutines.
    /// </summary>
    private static bool IsWrapperType(TypeSymbol type)
    {
        string baseName = type.BareName;
        return ForwardingWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Checks if a wrapper type is read-only (Viewing, Consulting).
    /// </summary>
    private static bool IsReadOnlyWrapper(TypeSymbol type)
    {
        string baseName = type.BareName;
        return ReadOnlyWrapperTypes.Contains(value: baseName);
    }

    /// <summary>
    /// Gets the inner type from a wrapper type (e.g., T from Viewing&lt;T&gt;).
    /// </summary>
    private TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
    {
        if (!IsWrapperType(type: wrapperType))
        {
            return null;
        }

        // Wrapper types have their inner type as the first type argument
        if (wrapperType.TypeArguments is { Count: > 0 })
        {
            return wrapperType.TypeArguments[index: 0];
        }

        return null;
    }
}
