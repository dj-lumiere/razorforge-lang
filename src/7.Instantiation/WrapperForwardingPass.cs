using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeSymbol = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation;

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

    private const string LockEnter = "lock_enter";
    private const string LockExit  = "lock_exit";

    /// <summary>
    /// Bundles the call-site parameters that are common across all forwarder-body builder helpers,
    /// reducing the per-method parameter count.
    /// </summary>
    private sealed record ForwarderCallContext(
        RoutineInfo InnerMemberRoutine,
        string CallPropertyName,
        bool IsFailable,
        bool HasReturnValue,
        TypeSymbol InnerType,
        List<Expression> ForwardedArgs);

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
            return null;

        if (!TryResolveWrapperContext(wrapperType: wrapperType, memberRoutineName: memberRoutineName,
                isFailable: isFailable, out WrapperResolutionContext ctx))
        {
            return null;
        }

        // Representation-unified Suflae entity member routine: its me is ALREADY Roamed[E]
        // (SignatureResolver sets MeType), so the "bare" routine IS the Roamed routine — it does its
        // own lock_enter + project through RoamController.data. Wrapping it in the projecting forwarder
        // below would project the controller to the entity and hand a BARE entity to a routine that
        // projects AGAIN → double projection → the controller header is read as entity fields → crash.
        // Resolve a Roamed[E] receiver call straight to the inner routine (passing the Roamed handle
        // as me), exactly as a receiver that was still bare at SA already does.
        if (wrapperType.BareName == RuntimeContract.Roamed
            && ctx.InnerMemberRoutine.MeType is RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed }
                                      or WrapperTypeInfo { Name: RuntimeContract.Roamed })
        {
            return ctx.InnerMemberRoutine;
        }

        if (IsReadOnlyWrapper(type: wrapperType) && !ctx.InnerMemberRoutine.IsReadOnly)
            return null;

        // Roamed failable forwarders: the when-re-propagation body IS built (see
        // BuildWrapperForwarderBody's isFailable path) but is currently GATED OFF — the synthesized
        // re-throw's crash_message gets reachability-pruned ("declared+called but never defined").
        // Re-enable by fixing that seed (find the correct crash_message owner/lookup).
        if (wrapperType.BareName == RuntimeContract.Roamed && ctx.InnerMemberRoutine.IsFailable)
            return null;

        return SynthesizeForwarder(wrapperType: wrapperType, memberRoutineName: memberRoutineName,
            isFailable: isFailable, ctx: ctx);
    }

    private readonly record struct WrapperResolutionContext(
        TypeSymbol WrapperDef, string GenericParamName, TypeSymbol InnerType,
        TypeSymbol InnerLookupType, RoutineInfo InnerMemberRoutine);

    /// <summary>
    /// Resolves the wrapper definition, generic parameter name, inner type, inner lookup type, and inner
    /// member routine needed for forwarder synthesis. Returns false if any required component is absent.
    /// </summary>
    private bool TryResolveWrapperContext(TypeSymbol wrapperType, string memberRoutineName,
        bool isFailable, out WrapperResolutionContext ctx)
    {
        ctx = default;
        TypeSymbol? wrapperDef = wrapperType switch
        {
            RecordTypeInfo { GenericDefinition: { } def } => def,
            EntityTypeInfo { GenericDefinition: { } def } => def,
            WrapperTypeInfo => _registry.LookupType(name: wrapperType.Name),
            _ => wrapperType
        };

        if (wrapperDef == null || !wrapperDef.IsGenericDefinition
            || wrapperDef.GenericParameters is not { Count: 1 })
        {
            return false;
        }

        string genericParamName = wrapperDef.GenericParameters[index: 0];
        TypeSymbol? innerType = GetWrapperInnerType(wrapperType: wrapperType);
        if (innerType == null) return false;

        TypeSymbol innerLookupType = innerType switch
        {
            RecordTypeInfo { GenericDefinition: { } d } => d,
            EntityTypeInfo { GenericDefinition: { } d } => d,
            _ => innerType
        };

        // Resolve against the CONCRETE inner type first so owner type parameters bind correctly.
        // Non-generic inners have innerType == innerLookupType, so this is a no-op there.
        RoutineInfo? innerMemberRoutine =
            _registry.LookupMemberRoutine(type: innerType, memberRoutineName: memberRoutineName,
                isFailable: isFailable);
        if (innerMemberRoutine == null)
        {
            innerMemberRoutine = _registry.LookupMemberRoutine(type: innerLookupType,
                memberRoutineName: memberRoutineName, isFailable: isFailable);
        }

        // No concrete inner impl → no forwarder. A resolution to the ABSTRACT protocol member does
        // NOT count — forwarding to it would emit a call to an unimplemented abstract symbol.
        if (innerMemberRoutine == null || innerMemberRoutine.OwnerType is ProtocolTypeInfo)
            return false;

        ctx = new WrapperResolutionContext(wrapperDef, genericParamName, innerType,
            innerLookupType, innerMemberRoutine);
        return true;
    }

    /// <summary>
    /// Builds and registers a new forwarding routine on the wrapper type, or returns the already-registered
    /// one when synthesis was previously completed or a source-defined routine takes precedence.
    /// </summary>
    private RoutineInfo? SynthesizeForwarder(TypeSymbol wrapperType, string memberRoutineName,
        bool isFailable, WrapperResolutionContext ctx)
    {
        var (wrapperDef, genericParamName, innerType, innerLookupType, innerMemberRoutine) = ctx;
        string cacheKey = $"{wrapperDef.Name}.{memberRoutineName}#{(isFailable ? "!" : "")}";
        if (!_synthesizedForwarderKeys.Add(item: cacheKey))
        {
            return _registry.LookupMemberRoutine(type: wrapperType, memberRoutineName: memberRoutineName,
                       isFailable: isFailable)
                   ?? _registry.LookupMemberRoutine(type: wrapperDef, memberRoutineName: memberRoutineName,
                       isFailable: isFailable);
        }

        // Don't overwrite a routine already defined on the wrapper's generic def —
        // source-defined routines like represent, diagnose, destroy take precedence.
        var existingOnDef = _registry.LookupMemberRoutine(type: wrapperDef,
            memberRoutineName: memberRoutineName, isFailable: isFailable);
        if (existingOnDef != null)
        {
            return _registry.LookupMemberRoutine(type: wrapperType,
                memberRoutineName: memberRoutineName, isFailable: isFailable);
        }

        List<string>? innerOwnerParams = innerLookupType.GenericParameters;
        (List<string>? filteredGenericParams, List<GenericConstraintDeclaration>? filteredConstraints) =
            FilterOwnerLevelGenerics(innerMemberRoutine: innerMemberRoutine, innerOwnerParams: innerOwnerParams);

        // Resolve name collisions between the wrapper's generic params and the inner routine's
        // owner-level generic params (both commonly use `T`). Renamed params carry a
        // ForwarderOriginalName marker so substitution sites can recover the original inner-param name.
        Dictionary<string, TypeInfo>? innerRename =
            BuildInnerRenameMap(innerOwnerParams: innerOwnerParams, wrapperDef: wrapperDef);

        (List<ParameterInfo> forwarderParameters, TypeSymbol? forwarderReturnType) =
            ApplyInnerRename(innerMemberRoutine: innerMemberRoutine, innerRename: innerRename);

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
            genericParamName: genericParamName,
            innerMemberRoutine: innerMemberRoutine,
            parameters: innerMemberRoutine.Parameters,
            dataFieldName: dataFieldName,
            innerIsEntity: innerType is EntityTypeInfo);

        _registry.RegisterRoutine(routine: forwarder);
        _synthesizedBodies[key: forwarder.RegistryKey] = (forwarder, body);

        return _registry.LookupMemberRoutine(type: wrapperType, memberRoutineName: memberRoutineName,
            isFailable: isFailable) ?? forwarder;
    }

    /// <summary>
    /// Filters out owner-level generic parameters from the inner member routine's
    /// <see cref="RoutineInfo.GenericParameters"/> and <see cref="RoutineInfo.GenericConstraints"/>,
    /// keeping only true method-level generics (e.g. <c>[U]</c> on <c>Hijacked[T].recast_as[U]</c>).
    /// </summary>
    private static (List<string>? filteredParams, List<GenericConstraintDeclaration>? filteredConstraints)
        FilterOwnerLevelGenerics(RoutineInfo innerMemberRoutine, List<string>? innerOwnerParams)
    {
        // Filter out owner-level generics from the inner memberRoutine's GenericParameters.
        // `BTreeSetNode[T].keys_add_last(value: T)` registers a RoutineInfo whose
        // GenericParameters carries `T` (the owner-level param) — propagating that onto the
        // forwarder makes the forwarder look memberRoutine-generic in T, so GMP later mangles it as
        // `Owned[BTreeSetNode[S64]].keys_add_last[S64]` while codegen call sites use the
        // un-suffixed `Owned[BTreeSetNode[S64]].keys_add_last`. Strip owner-level params so
        // only true memberRoutine-level generics (e.g. `Hijacked[T].recast_as[U]` -> `[U]`) survive.
        List<string>? filteredParams = innerMemberRoutine.GenericParameters;
        if (filteredParams is { Count: > 0 } && innerOwnerParams is { Count: > 0 })
        {
            filteredParams = filteredParams
                .Where(predicate: gp => !innerOwnerParams.Contains(value: gp))
                .ToList();
            if (filteredParams.Count == 0) filteredParams = null;
        }

        List<GenericConstraintDeclaration>? filteredConstraints = innerMemberRoutine.GenericConstraints;
        if (filteredConstraints is { Count: > 0 } && innerOwnerParams is { Count: > 0 })
        {
            filteredConstraints = filteredConstraints
                .Where(predicate: c => !innerOwnerParams.Contains(value: c.ParameterName))
                .ToList();
            if (filteredConstraints.Count == 0) filteredConstraints = null;
        }

        return (filteredParams, filteredConstraints);
    }

    /// <summary>
    /// Builds a rename map for inner-owner generic parameters that collide with wrapper generic
    /// parameters. Each colliding param name is renamed to <c>__rfwd_{name}__</c> with a
    /// <see cref="GenericParameterTypeInfo.ForwarderOriginalName"/> marker so substitution sites
    /// can recover the original name without string-parsing the sentinel.
    /// </summary>
    private static Dictionary<string, TypeInfo>? BuildInnerRenameMap(
        List<string>? innerOwnerParams, TypeSymbol wrapperDef)
    {
        if (innerOwnerParams is not { Count: > 0 }) return null;
        if (wrapperDef.GenericParameters is not { Count: > 0 } wrapperParams) return null;

        Dictionary<string, TypeInfo>? innerRename = null;
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
        return innerRename;
    }

    /// <summary>
    /// Applies <paramref name="innerRename"/> (if non-null) to the inner member routine's
    /// parameters and return type, returning the substituted copies for use in the forwarder.
    /// </summary>
    private static (List<ParameterInfo> parameters, TypeSymbol? returnType) ApplyInnerRename(
        RoutineInfo innerMemberRoutine, Dictionary<string, TypeInfo>? innerRename)
    {
        List<ParameterInfo> parameters = innerMemberRoutine.Parameters;
        TypeSymbol? returnType = innerMemberRoutine.ReturnType;
        if (innerRename is not { Count: > 0 }) return (parameters, returnType);

        parameters = innerMemberRoutine.Parameters
            .Select(selector: p => p.WithSubstitutedType(
                newType: RoutineInfo.SubstituteType(type: p.Type, substitution: innerRename)))
            .ToList();
        if (returnType != null)
            returnType = RoutineInfo.SubstituteType(type: returnType, substitution: innerRename);

        return (parameters, returnType);
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
    private DangerStatement BuildWrapperForwarderBody(TypeSymbol wrapperType, string genericParamName,
        RoutineInfo innerMemberRoutine, List<ParameterInfo> parameters,
        string? dataFieldName = null, bool innerIsEntity = false)
    {
        // The forwarded call's name is always bare; its failability is carried structurally on the
        // callee MemberExpression (IsFailable), never appended to the name.
        TypeSymbol innerType = wrapperType.TypeArguments is { Count: > 0 }
            ? wrapperType.TypeArguments[0]
            : new GenericParameterTypeInfo(name: genericParamName);
        List<Expression> forwardedArgs = parameters
            .Where(p => p.Name != "me")
            .Select(p => (Expression)new NamedArgumentExpression(
                Name: p.Name,
                Value: new IdentifierExpression(Name: p.Name, Location: _synthLoc),
                Location: _synthLoc))
            .ToList();
        var callCtx = new ForwarderCallContext(
            InnerMemberRoutine: innerMemberRoutine,
            CallPropertyName: innerMemberRoutine.Name,
            IsFailable: innerMemberRoutine.IsFailable,
            HasReturnValue: innerMemberRoutine.ReturnType != null &&
                innerMemberRoutine.ReturnType.Name != "None",
            InnerType: innerType,
            ForwardedArgs: forwardedArgs);

        List<Statement> innerStatements;

        if (dataFieldName != null)
        {
            innerStatements = BuildRecordStructForwarderStatements(
                wrapperType: wrapperType, dataFieldName: dataFieldName, ctx: callCtx);
        }
        else if (wrapperType.BareName is RuntimeContract.Retained or RuntimeContract.Tracked or RuntimeContract.Roamed)
        {
            innerStatements = BuildRcWrapperForwarderStatements(
                wrapperType: wrapperType, genericParamName: genericParamName, ctx: callCtx);
        }
        else
        {
            innerStatements = BuildPointerWrapperForwarderStatements(
                genericParamName: genericParamName, innerIsEntity: innerIsEntity, ctx: callCtx);
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
        string dataFieldName, ForwarderCallContext ctx)
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
            ResolvedType = ctx.InnerType
        };
        var innerCall = new CallExpression(
            Callee: new MemberExpression(
                Object: readCall,
                MemberName: ctx.CallPropertyName,
                Location: _synthLoc) { IsFailable = ctx.IsFailable },
            Arguments: ctx.ForwardedArgs,
            Location: _synthLoc)
        {
            // ResolvedRoutine intentionally left null: this forwarder is generated once
            // per wrapperDef and reused across all inner T. Baking innerMemberRoutine here would
            // freeze the call to whichever inner type was resolved first (e.g. binding
            // to BTreeListNode.keys_add_last forever, even when monomorphized for
            // Modifying[BTreeSetNode[S64]]). Leaving it null lets RoutineReachabilityPass
            // re-resolve the call from the substituted receiver type at monomorphization.
            ResolvedType = ctx.InnerMemberRoutine.ReturnType
        };
        Statement callStmt = ctx.HasReturnValue
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
        string genericParamName, ForwarderCallContext ctx)
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
        TypeSymbol innerType = ctx.InnerType;
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
                MemberName: ctx.CallPropertyName,
                Location: _synthLoc) { IsFailable = ctx.IsFailable },
            Arguments: ctx.ForwardedArgs,
            Location: _synthLoc)
        {
            // ResolvedRoutine intentionally null — see record-struct branch for reasoning.
            ResolvedType = ctx.InnerMemberRoutine.ReturnType
        };
        if (isRoamed)
        {
            return BuildRoamedLockedForwarderStatements(wrapperType: wrapperType,
                rawDecl: rawDecl, ctrlDecl: ctrlDecl,
                innerRevealCall: innerRevealCall, innerCall: innerCall, ctx: ctx);
        }

        Statement callStmt = ctx.HasReturnValue
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
        Statement rawDecl, Statement ctrlDecl,
        CallExpression innerRevealCall, CallExpression innerCall, ForwarderCallContext ctx)
    {
        RoutineInfo? lockEnter = _registry.LookupMemberRoutine(type: wrapperType, memberRoutineName: LockEnter);
        RoutineInfo? lockExit = _registry.LookupMemberRoutine(type: wrapperType, memberRoutineName: LockExit);
        ExpressionStatement MkLock(RoutineInfo? m, string verb) => new ExpressionStatement(
            Expression: new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: "me", Location: _synthLoc) { ResolvedType = wrapperType },
                    MemberName: verb, Location: _synthLoc),
                Arguments: [], Location: _synthLoc) { ResolvedRoutine = m },
            Location: _synthLoc);

        if (ctx.IsFailable)
        {
            // Failable: call the throw-based `check_` variant (non-propagating carrier), then a
            // `when` re-propagates AFTER releasing the lock in each arm — mirrors
            // ErrorHandlingVariantPass.BuildCarrierPropagationWhen, but with lock_exit inserted
            // so the lock is freed on BOTH the failure (throw) and success paths.
            TypeSymbol innerDef = ctx.InnerType switch
            {
                EntityTypeInfo { GenericDefinition: { } ed } => ed,
                RecordTypeInfo { GenericDefinition: { } rd } => rd,
                _ => ctx.InnerType
            };
            string checkName = "check_" + ctx.CallPropertyName;
            RoutineInfo? checkM = _registry.LookupMemberRoutine(type: innerDef,
                memberRoutineName: checkName, isFailable: false);
            var checkSubject = new CallExpression(
                Callee: new MemberExpression(Object: innerRevealCall,
                    MemberName: checkName, Location: _synthLoc),
                Arguments: ctx.ForwardedArgs, Location: _synthLoc)
            { ResolvedType = checkM?.ReturnType };
            var whenStmt = new WhenStatement(
                Expression: checkSubject,
                Clauses:
                [
                    new WhenClause(
                        Pattern: new CrashablePattern(ErrorType: null, VariableName: "__rf_e", Location: _synthLoc),
                        Body: new BlockStatement(
                            Statements: [MkLock(lockExit, LockExit),
                                new ThrowStatement(Error: new IdentifierExpression(Name: "__rf_e", Location: _synthLoc), Location: _synthLoc)],
                            Location: _synthLoc),
                        Location: _synthLoc),
                    new WhenClause(
                        Pattern: new ElsePattern(VariableName: "__rf_v", Location: _synthLoc),
                        Body: new BlockStatement(
                            Statements: [MkLock(lockExit, LockExit),
                                new ReturnStatement(Value: new IdentifierExpression(Name: "__rf_v", Location: _synthLoc), Location: _synthLoc)],
                            Location: _synthLoc),
                        Location: _synthLoc)
                ],
                Location: _synthLoc);
            return [MkLock(lockEnter, LockEnter), rawDecl, ctrlDecl, whenStmt];
        }

        if (ctx.HasReturnValue)
        {
            Statement resultDecl = new DeclarationStatement(
                Declaration: new VariableDeclaration(Name: "__rf_locked", Type: null,
                    Initializer: innerCall, Visibility: VisibilityModifier.Open, Location: _synthLoc),
                Location: _synthLoc);
            Statement retStmt = new ReturnStatement(
                Value: new IdentifierExpression(Name: "__rf_locked", Location: _synthLoc)
                    { ResolvedType = ctx.InnerMemberRoutine.ReturnType },
                Location: _synthLoc);
            return [MkLock(lockEnter, LockEnter), rawDecl, ctrlDecl, resultDecl, MkLock(lockExit, LockExit), retStmt];
        }

        return [MkLock(lockEnter, LockEnter), rawDecl, ctrlDecl,
            new ExpressionStatement(Expression: innerCall, Location: _synthLoc), MkLock(lockExit, LockExit)];
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
    private List<Statement> BuildPointerWrapperForwarderStatements(string genericParamName,
        bool innerIsEntity, ForwarderCallContext ctx)
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
            innerType: ctx.InnerType,
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
            ResolvedType = ctx.InnerType
        };
        var innerCall = new CallExpression(
            Callee: new MemberExpression(
                Object: readCall,
                MemberName: ctx.CallPropertyName,
                Location: _synthLoc) { IsFailable = ctx.IsFailable },
            Arguments: ctx.ForwardedArgs,
            Location: _synthLoc)
        {
            // ResolvedRoutine intentionally left null — see the record-struct branch above
            // for the full reasoning. Same issue applies to pointer wrappers.
            ResolvedType = ctx.InnerMemberRoutine.ReturnType
        };
        Statement callStmt = ctx.HasReturnValue
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
    private static TypeSymbol? GetWrapperInnerType(TypeSymbol wrapperType)
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
