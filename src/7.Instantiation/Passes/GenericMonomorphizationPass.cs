using System.Diagnostics;
using Builder.Desugaring;
using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification;

namespace Builder.Instantiation.Passes;

/// <summary>
/// Phase 7 global pass that builds concrete generic memberRoutine bodies before codegen.
/// so the code generator never needs to search programs or perform AST substitution
/// for the common case.
///
/// <para>
/// The pass iterates every concrete generic type instance recorded in the
/// <see cref="TypeRegistry"/> during Phase 4 (e.g., <c>List[S64]</c>, <c>Maybe[Text]</c>)
/// and generates <see cref="MonomorphizedBody"/> entries for each of the generic
/// definition's memberRoutines.  Bodies are sourced from three places:
/// <list type="bullet">
///   <item><see cref="DesugaringContext.VariantBodies"/> -> WiredRoutinePass-generated
///         bodies (<c>represent</c>, <c>diagnose</c>) and ErrorHandlingVariantPass
///         bodies (<c>try_emit</c>, etc.).</item>
///   <item><c>Registry.StdlibPrograms</c> and <c>Registry.UserPrograms</c> AST declarations -> source bodies.</item>
/// </list>
/// Pure-synthesized memberRoutines (<see cref="RoutineInfo.IsSynthesized"/> = true with no
/// body anywhere) are skipped here; their AST bodies are produced by
/// <c>WiredRoutinePass</c> and emitted via
/// <c>EmitSynthesizedBodyFromAst</c>.
/// </para>
///
/// <para>
/// Results are stored in <see cref="DesugaringContext.InstantiatedGenericBodies"/>,
/// keyed by the concrete routine's <see cref="RoutineInfo.RegistryKey"/>.
/// Codegen checks this map before doing its own AST search.
/// </para>
/// </summary>
public sealed class GenericMonomorphizationPass(DesugaringContext ctx)
{
    // The "represent" member-routine name appears in several liveness seeds. Using the constant
    // avoids re-allocating the literal on each call and keeps the name in sync with RuntimeContract.
    private const string RepresentMemberRoutineName = RuntimeContract.Display.Represent;

    // Routine-declaration index

    // Key: routine name (e.g. "List[T].getitem") -> list of matching declarations.
    // Built once in RunGlobal() before the fixed-point loop.
    private Dictionary<string, List<RoutineDeclaration>> _routineIndex = new();

    private void BuildRoutineIndex()
    {
        _routineIndex = new Dictionary<string, List<RoutineDeclaration>>();
        IEnumerable<(Program Program, string FilePath, string Module)> allPrograms =
            ctx.Registry.StdlibPrograms.Concat(second: ctx.Registry.UserPrograms);
        foreach ((Program program, string _, string _) in allPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                AddDeclToIndex(key: decl.QualifiedName, decl: decl);

                // A constructor `routine T(...)` / `routine T[params](...)` is registered as a creator
                // (RoutineKind.Creator) on its owner type with NO member name, but its AST decl.Name is
                // just the bare base type ("List", generics live in GenericParameters). Monomorphization
                // looks a creator up via BuildAstName(owner, CreatorName), so index it under that same key
                // too — otherwise generic constructor bodies never get monomorphized and codegen
                // over-prunes them. (Both sides use the empty creator name, so the keys agree.)
                if (decl.ResolvedInfo is { IsCreator: true, OwnerType: { } ctorOwner })
                {
                    AddDeclToIndex(key: BuildAstName(genDef: ctorOwner,
                            routineName: RoutineInfo.CreatorName),
                        decl: decl);
                }
            }
        }
    }
    private void AddDeclToIndex(string key, RoutineDeclaration decl)
    {
        if (!_routineIndex.TryGetValue(key: key, value: out List<RoutineDeclaration>? bucket))
        {
            bucket = [];
            _routineIndex[key: key] = bucket;
        }

        bucket.Add(item: decl);
    }
    // Public entry point

    /// <summary>
    /// Public entry point
    /// </summary>
    // Persisted across RunGlobal + RunIncremental so a post-PDIL incremental re-run only does work
    // for genuinely-new types/bodies instead of re-seeding and re-walking everything (which made the
    // PDIL→GMP fixed point in GenericClosurePass quadratic).
    private readonly HashSet<string> _processedTypes = new(comparer: StringComparer.Ordinal);

    private readonly HashSet<string> _walkedBodyKeys = new(comparer: StringComparer.Ordinal);
    private bool _routineIndexBuilt;

    /// <summary>Runs global monomorphization of all generic types and routines in the registry.</summary>
    public void RunGlobal()
    {
        // Pre-build the routine-declaration index so FindInStdlib is O(1) per lookup.
        if (!_routineIndexBuilt)
        {
            BuildRoutineIndex();
            _routineIndexBuilt = true;
        }

        // Enable push-based discovery before the first ProcessConcreteType call so any types
        // created during rewriting are captured immediately.
        ctx.Registry.StartGmpDiscoveryTracking();

        SeedAndProcess(timing: ctx.SaTiming);
    }

    /// <summary>
    /// Cheap re-run after <see cref="ProtocolDefaultImplLoweringPass"/> synthesizes new bodies post-GMP
    /// (e.g. <c>List[S64].List</c>/<c>.Set</c> collectors whose call sites appear only inside
    /// GMP-monomorphized adapter <c>iter</c> bodies). The persisted <see cref="_processedTypes"/> /
    /// <see cref="_walkedBodyKeys"/> sets mean the seed scans short-circuit and the liveness expansion
    /// walks only the freshly-synthesized bodies, so this is bounded by NEW work — unlike re-running
    /// the full <see cref="RunGlobal"/> per round.
    /// </summary>
    public void RunIncremental()
    {
        SeedAndProcess(timing: false);
    }

    private void SeedAndProcess(bool timing)
    {
        // Process concrete generic instances. Start with the liveness-filtered set, then use
        // a push-based queue to pick up types discovered during body rewriting
        // (e.g. ListEmitter[Byte] registered by GenericAstRewriter when rewriting List[Byte].iter).
        // Only types newly created by GetOrCreateResolution after tracking starts are enqueued —
        // pre-existing phantom types (BTreeDictNode stubs etc.) never enter the queue.
        Stopwatch? sw = timing
            ? Stopwatch.StartNew()
            : null;
        int startCount = _processedTypes.Count;

        SeedInitialConcreteTypes();
        DrainDiscoveryQueueToFixedPoint();

        if (timing)
        {
            sw!.Stop();
            Console.Error.WriteLine(
                value:
                $"    GMP reachable types: {_processedTypes.Count} ({_processedTypes.Count - startCount} new) ({sw.ElapsedMilliseconds} ms)");
        }

        // Scan built bodies for memberRoutine-generic call sites (e.g. getitem[U64]! called from
        // List[Bytes].eq). SA only analyzes generic-def bodies, so these concrete
        // call sites are never registered in _routineResolutions. Register them now so
        // ProcessResolvedMemberRoutineGenericRoutines can build their bodies.
        Stopwatch? sw2 = timing
            ? Stopwatch.StartNew()
            : null;

        void LogSubTiming(string label)
        {
            if (sw2 == null)
            {
                return;
            }

            sw2.Stop();
            Console.Error.WriteLine(
                value: $"        GMP.RG sub - {label}: {sw2.ElapsedMilliseconds} ms");
            sw2.Restart();
        }

        ScanAndRegisterMemberRoutineGenericCallResolutions();
        LogSubTiming(label: "ScanAndRegisterMemberRoutineGenericCallResolutions");

        ProcessResolvedMemberRoutineGenericRoutines();
        LogSubTiming(label: "ProcessResolvedMemberRoutineGenericRoutines");

        // Liveness expansion across the bodies emitted above. RoutineReachabilityPass ran BEFORE
        // ProtocolDefaultImplLoweringPass synthesized the iterator-adapter bodies, so the nested
        // adapter/emitter types those bodies construct and iterate (e.g. SelectIterator and its
        // SelectEmitter, reached only through a chained `list.where(..).select(..)`) were never
        // seeded as live owners, and the universal `hijack` instances they call were never seeded
        // as live routines. Both were then silently gated out, leaving undefined symbols at link.
        // Walk the emitted (=live) bodies, enliven every concrete type they reference and every
        // routine they call, and re-process — iterating to a fixed point as newly-emitted bodies
        // surface deeper layers of the adapter chain.
        ExpandLivenessThroughEmittedBodies();
        LogSubTiming(label: "ExpandLivenessThroughEmittedBodies");

        EmitGenericDefBuilderQueryBodies();
        LogSubTiming(label: "EmitGenericDefBuilderQueryBodies");
    }

    /// <summary>Seeds the initial liveness-filtered concrete types and all pre-existing unfiltered instances.</summary>
    private void SeedInitialConcreteTypes()
    {
        // Seed with liveness-filtered instances + wrapper instances. On an incremental re-run the
        // Add returns false for everything already processed, so these scans become cheap lookups.
        foreach (TypeSymbol concreteType in ctx.Registry
                                             .AllConcreteGenericInstances
                                             .Concat(second: ctx.Registry
                                                                .AllConcreteWrapperInstances)
                                             .DistinctBy(keySelector: type => type.FullName)
                                             .Where(predicate: t =>
                                                  _processedTypes.Add(item: t.FullName))
                                             .ToArray())
        {
            ProcessConcreteType(concreteType: concreteType);
        }

        // One-time pass over ALL concrete types in _resolutions that weren't in the liveness set.
        // Catches types created before GMP started (e.g. List[Bytes] resolved during SA
        // but not reached by the liveness walk). Self-nesting types (Hijacked^N) are blocked by
        // the guard in GetOrCreateResolution, so this scan terminates.
        // Materialize to a list first — ProcessConcreteType modifies _resolutions during iteration.
        foreach (TypeSymbol preExisting in
                 ctx.Registry.AllConcreteGenericInstancesUnfiltered.ToList())
        {
            if (!_processedTypes.Add(item: preExisting.FullName))
            {
                continue;
            }

            ProcessConcreteType(concreteType: preExisting);
        }

        // Same for wrapper instances (Hijacked[T], T, etc.). Wrappers like Hijacked have
        // explicit memberRoutine definitions in stdlib that need monomorphization for each concrete T,
        // but live in _wrapperResolutions and aren't enqueued by NotifyConcreteRegistration.
        foreach (TypeSymbol preExisting in
                 ctx.Registry.AllConcreteWrapperInstancesUnfiltered.ToList())
        {
            if (!_processedTypes.Add(item: preExisting.FullName))
            {
                continue;
            }

            ProcessConcreteType(concreteType: preExisting);
        }
    }

    /// <summary>
    /// Fixed-point expansion: drains types created during body rewriting and rescans wrapper
    /// instances until no new types appear. The self-nesting guard in GetOrCreateResolution
    /// prevents Hijacked^N infinite chains.
    /// </summary>
    private void DrainDiscoveryQueueToFixedPoint()
    {
        bool madeProgress;
        do
        {
            madeProgress = false;
            List<TypeSymbol> discovered = ctx.Registry.DrainGmpDiscoveryQueue();
            while (discovered.Count > 0)
            {
                foreach (TypeSymbol newType in discovered)
                {
                    if (!_processedTypes.Add(item: newType.FullName))
                    {
                        continue;
                    }

                    ProcessConcreteType(concreteType: newType);
                    madeProgress = true;
                }

                discovered = ctx.Registry.DrainGmpDiscoveryQueue();
            }

            // Wrapper instances aren't enqueued by NotifyConcreteRegistration (it only handles
            // EntityTypeSymbol/RecordTypeSymbol). Re-scan _wrapperResolutions each round to pick up
            // new wrappers like Hijacked[Text] that GenericAstRewriter created while
            // rewriting List[Text] forwarder bodies.
            foreach (TypeSymbol wrapper in
                     ctx.Registry.AllConcreteWrapperInstancesUnfiltered.ToList())
            {
                if (!_processedTypes.Add(item: wrapper.FullName))
                {
                    continue;
                }

                ProcessConcreteType(concreteType: wrapper);
                madeProgress = true;
            }
        } while (madeProgress);
    }

    /// <summary>
    /// Propagates liveness through the bodies GMP already emitted, then emits the routines/types
    /// that become reachable as a result. Needed because <c>RoutineReachabilityPass</c> runs
    /// before <see cref="ProtocolDefaultImplLoweringPass"/>, so types/routines reachable only through
    /// the synthesized iterator-adapter chain were never marked live and got gated out. A fixed-point
    /// loop is required: enlivening one layer (e.g. <c>SelectIterator</c>) emits its <c>iter</c>,
    /// whose body references the next layer (<c>SelectEmitter</c>), and so on.
    /// </summary>
    private void ExpandLivenessThroughEmittedBodies()
    {
        // Both gates empty means fan-out mode (no liveness filtering) — everything is already emitted.
        if (ctx.LiveOwnerTypeNames.Count == 0 && ctx.LiveRoutineKeys.Count == 0)
        {
            return;
        }

        // Walk each emitted body at most once across the whole fixed point (not once per round) —
        // re-walking every body every round, plus re-running ProcessResolvedMemberRoutineGenericRoutines,
        // is quadratic and stalls compilation on a large stdlib. New bodies emitted by a round are
        // picked up in the next round's delta. _walkedBodyKeys is instance state so a later
        // RunIncremental pass only walks bodies synthesized since the last pass.
        int guard = 0;
        bool changed = true;
        while (changed && guard++ < 100)
        {
            changed = false;
            bool registeredRoutine = false;
            if (WalkEmittedBodiesForLiveness(changed: ref changed,
                    registeredRoutine: ref registeredRoutine))
            {
                changed = true;
            }

            DrainDiscoveryQueueForLiveness(changed: ref changed);
            if (registeredRoutine)
            {
                ProcessResolvedMemberRoutineGenericRoutines();
            }
        }

        EnliveWiredLeafCallees();
    }

    /// <summary>
    /// Walks all currently-emitted instantiated bodies that haven't been walked yet, enlivening
    /// throw crash-messages and expression-level owner types/callees. Returns true if any change occurred.
    /// </summary>
    private bool WalkEmittedBodiesForLiveness(ref bool changed, ref bool registeredRoutine)
    {
        var newOwners = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal);
        // Captured locals: ref parameters cannot be used inside the walk lambdas (CS1628); mirror them
        // in locals the closures can touch, then write the results back to the ref parameters at the end.
        bool localChanged = false;
        bool localRegistered = registeredRoutine;
        foreach ((string bodyKey, MonomorphizedBody mb) in ctx.InstantiatedGenericBodies.ToList())
        {
            // Only walk LIVE bodies. A dead (unreachable) instantiation is never emitted by codegen,
            // so the owner types / callees it references need no enlivening: any adapter reached only
            // through a LIVE body is marked live when that body is walked below and then picked up as
            // live in the next fixed-point round. The live check is BEFORE _walkedBodyKeys so a body
            // that only BECOMES live in a later round is still walked then.
            // Empty LiveRoutineKeys = fan-out/no-filter mode (early-returns when BOTH gates are empty).
            if (ctx.LiveRoutineKeys.Count > 0 && !ctx.LiveRoutineKeys.Contains(item: bodyKey))
            {
                continue;
            }

            if (!_walkedBodyKeys.Add(item: bodyKey) || mb.Ast?.Body == null)
            {
                continue;
            }

            // A `throw X` in an emitted body needs X's crash_message live: codegen's EmitThrow
            // calls it directly with no source CallExpression. RoutineReachabilityPass handles
            // throws in bodies it walks, but bodies emitted HERE (e.g. a specialized-receiver
            // member's retrieve!/race! that throws TaskSpawnError) were never walked by it.
            AstWalker.Walk(root: mb.Ast.Body,
                visit: node =>
                {
                    if (EnliveThrowCrashMessage(node: node))
                    {
                        localChanged = true;
                    }
                });
            AstWalker.WalkExpressions(root: mb.Ast.Body,
                visit: expr =>
                {
                    if (EnliveEmittedBodyExpression(expr: expr,
                            newOwners: newOwners,
                            registeredRoutine: ref localRegistered))
                    {
                        localChanged = true;
                    }
                });
        }

        if (localChanged)
        {
            changed = true;
        }

        registeredRoutine = localRegistered;
        return EnliveNewOwnerTypesFromMap(newOwners: newOwners);
    }

    /// <summary>
    /// Marks each discovered owner type in <paramref name="newOwners"/> live and processes it.
    /// Returns true if any new owner was enlivened.
    /// </summary>
    private bool EnliveNewOwnerTypesFromMap(Dictionary<string, TypeSymbol> newOwners)
    {
        bool changed = false;
        foreach ((string fullName, TypeSymbol type) in newOwners)
        {
            if (ctx.LiveOwnerTypeNames.Add(item: fullName))
            {
                _processedTypes.Add(item: fullName);
                ProcessConcreteType(concreteType: type);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Drains the GMP discovery queue after a body-walking round, processing any newly-created types.
    /// Sets <paramref name="changed"/> to true whenever a new type is processed.
    /// </summary>
    private void DrainDiscoveryQueueForLiveness(ref bool changed)
    {
        List<TypeSymbol> discovered = ctx.Registry.DrainGmpDiscoveryQueue();
        while (discovered.Count > 0)
        {
            foreach (TypeSymbol newType in discovered)
            {
                if (!_processedTypes.Add(item: newType.FullName))
                {
                    continue;
                }

                ProcessConcreteType(concreteType: newType);
                changed = true;
            }

            discovered = ctx.Registry.DrainGmpDiscoveryQueue();
        }
    }

    /// <summary>
    /// A `throw X` in an emitted body needs X's crash_message live: codegen's EmitThrow calls it
    /// directly with no source CallExpression. Enlivens that crash_message. Returns true if it newly
    /// marked a routine live.
    /// </summary>
    private bool EnliveThrowCrashMessage(object? node)
    {
        if (node is not ThrowStatement throwStmt)
        {
            return false;
        }

        TypeSymbol? errorType = throwStmt.Error.ResolvedType ??
                              (throwStmt.Error is CreatorExpression cre
                                  ? cre.ConstructedType
                                  : null);
        if (errorType == null)
        {
            return false;
        }

        RoutineInfo? crashMsg = ctx.Registry.LookupMemberRoutine(type: errorType,
            memberRoutineName: RuntimeContract.CrashMessage);
        if (crashMsg == null || ctx.LiveRoutineKeys.Count == 0)
        {
            return false;
        }

        bool changed = ctx.LiveRoutineKeys.Add(item: crashMsg.RegistryKey);
        // crash_message's body formats via `me.represent()` (no source AST call), so a thrown error's
        // represent is otherwise never built → link-undefined. Enliven it alongside crash_message.
        if (ctx.Registry.LookupMemberRoutine(type: errorType,
                memberRoutineName: RepresentMemberRoutineName) is { } rep)
        {
            changed |= ctx.LiveRoutineKeys.Add(item: rep.RegistryKey);
        }

        return changed;
    }

    /// <summary>
    /// Enlivens the concrete owner types and callees referenced by one expression in an emitted body.
    /// Returns true if it newly marked an owner type or routine live (so the fixed point continues).
    /// </summary>
    private bool EnliveEmittedBodyExpression(Expression expr,
        Dictionary<string, TypeSymbol> newOwners, ref bool registeredRoutine)
    {
        bool changed = false;
        CollectConcreteOwnerTypes(t: expr.ResolvedType, sink: newOwners);
        if (expr is CreatorExpression creator)
        {
            CollectConcreteOwnerTypes(t: creator.ConstructedType, sink: newOwners);
        }

        // A routine CALLED from an emitted body is reachable. This covers universal
        // memberRoutines (e.g. WhereIterator[..].hijack, whose generic-def owner is `T`) that
        // ProcessConcreteType never emits because they aren't owned by the receiver's
        // generic definition.
        // A resolved routine reached from an emitted body — either a plain CallExpression or a
        // GenericMemberRoutineCallExpression (`hijacked_from[${m.type}]` folded to `hijacked_from[F32]`
        // via the `$Col` expand substitution: its concrete instance is discovered HERE, during
        // the expand unroll, so it post-dates RoutineReachabilityPass and would otherwise be
        // "declared but never defined"). Both node kinds carry a settable ResolvedRoutine.
        RoutineInfo? calledRoutine = expr switch
        {
            CallExpression { ResolvedRoutine: { } cr } => cr,
            GenericMemberRoutineCallExpression { ResolvedRoutine: { } gr } => gr,
            _ => null
        };
        if (calledRoutine is { } rr && ctx.LiveRoutineKeys.Count > 0 &&
            ctx.LiveRoutineKeys.Add(item: rr.RegistryKey))
        {
            if (rr.GenericDefinition != null &&
                !ctx.InstantiatedGenericBodies.ContainsKey(key: rr.RegistryKey) &&
                !ctx.VariantBodies.ContainsKey(key: rr.RegistryKey))
            {
                ctx.Registry.RegisterRoutineResolution(resolvedMemberRoutine: rr);
                registeredRoutine = true;
            }

            changed = true;
        }

        if (EnliveIndexAccessors(expr: expr, registeredRoutine: ref registeredRoutine))
        {
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// An `obj[i]` access is still an IndexExpression in an emitted body (OperatorLoweringPass on
    /// instantiated bodies only rewrites it to a `getitem`/`setitem` CallExpression LATER, in
    /// GenericClosurePass, which runs AFTER this liveness pass). Its concrete callee — e.g.
    /// `Array[F32, 4].getitem`/`.setitem` reached only through a SoA container's monomorphized
    /// memberRoutine `me.${m.name}[index]` — was never seen by RoutineReachabilityPass and is left
    /// "declared but never defined" at codegen. Marks both index accessors live here, mirroring the
    /// CallExpression callee handling and the IndexExpression discovery at
    /// ScanExprForMemberRoutineGenericCalls. (A read never calls `setitem`, but emitting the extra
    /// concrete accessor is harmless — codegen only calls the one it needs.) Returns true if it newly
    /// marked an accessor live.
    /// </summary>
    private bool EnliveIndexAccessors(Expression expr, ref bool registeredRoutine)
    {
        if (expr is not IndexExpression { Object.ResolvedType: { } idxObjType } idxExpr ||
            idxObjType is GenericParameterTypeSymbol || ctx.LiveRoutineKeys.Count == 0)
        {
            return false;
        }

        bool changed = false;
        TypeSymbol? idxType = idxExpr.Index.ResolvedType;
        foreach (string accessor in (ReadOnlySpan<string>)["getitem", "setitem"])
        {
            if (EnliveOneIndexAccessor(idxObjType: idxObjType,
                    idxType: idxType,
                    accessorName: accessor,
                    registeredRoutine: ref registeredRoutine))
            {
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Enlivens a single index accessor (getitem or setitem) on the given owner type. Prefers the
    /// exact overload matched by index type; falls back to all candidates when no exact match exists.
    /// Returns true if it newly marked any accessor live.
    /// </summary>
    private bool EnliveOneIndexAccessor(TypeSymbol idxObjType, TypeSymbol? idxType,
        string accessorName, ref bool registeredRoutine)
    {
        // Prefer the exact argType-matched overload. When the index type doesn't pin one (it is
        // null, or not the accessor's index-parameter type), enliven EVERY registered overload of
        // this accessor on the concrete owner instead of taking an arbitrary name-only pick — a
        // name-only lookup can't disambiguate >1 overload (no first-wins), and liveness is
        // conservative: an extra concrete accessor is harmless (codegen emits only the one it calls).
        var accs = new List<RoutineInfo>();
        RoutineInfo? typed = idxType != null
            ? ctx.Registry.LookupMemberRoutineOverload(type: idxObjType,
                memberRoutineName: accessorName,
                argTypes: [idxType])
            : null;
        if (typed != null)
        {
            accs.Add(item: typed);
        }
        else
        {
            ctx.Registry.CollectMemberRoutineCandidates(type: idxObjType,
                memberRoutineName: accessorName,
                candidates: accs);
        }

        bool changed = false;
        foreach (RoutineInfo acc in accs)
        {
            if (acc is not { OwnerType: not { IsGenericDefinition: true } } ||
                !ctx.LiveRoutineKeys.Add(item: acc.RegistryKey))
            {
                continue;
            }

            if (acc.GenericDefinition != null &&
                !ctx.InstantiatedGenericBodies.ContainsKey(key: acc.RegistryKey) &&
                !ctx.VariantBodies.ContainsKey(key: acc.RegistryKey))
            {
                ctx.Registry.RegisterRoutineResolution(resolvedMemberRoutine: acc);
                registeredRoutine = true;
            }

            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Closes the live-routine set under the implicit callees of compiler-synthesized routines.
    /// <para>
    /// Routines enlivened AFTER <c>RoutineReachabilityPass</c> (by this pass, e.g. a wired
    /// <c>destroy</c> on <c>Tuple[S64, Bool]</c> pulled in by overflow-arithmetic machinery, or a
    /// derived <c>U64.lt</c> referenced from an emitted generic body) never had their own bodies
    /// walked: those bodies live in <c>SynthesizedBodies</c> (not <c>VariantBodies</c>, the only
    /// source the reachability BFS walks), and their leaf callees sit on NON-generic primitive
    /// owners that <see cref="ProcessConcreteType"/> never visits. The result is a live aggregate
    /// whose leaf callee is declared-but-undefined at link time:
    /// <list type="bullet">
    ///   <item>derived <c>lt/le/gt/ge</c> → owner <c>cmp</c> + <c>ComparisonSign.eq/ne</c></item>
    ///   <item><c>ne</c> → owner <c>eq</c>; <c>notcontains</c> → owner <c>contains</c></item>
    ///   <item>composite <c>destroy/store/hash/eq/cmp</c> → the same wired verb on
    ///         each field/element type (recursing to a fixed point)</item>
    /// </list>
    /// This mirrors <c>RoutineReachabilityPass.ExpandSyntheticSiblings</c> + its synthesized-body
    /// walk, applied to the post-GMP live set. Bounded by design: the verbs covered never cascade
    /// into formatting/IO (deliberately excludes <c>represent</c>/<c>diagnose</c>).
    /// </para>
    /// </summary>
    private void EnliveWiredLeafCallees()
    {
        if (ctx.LiveRoutineKeys.Count == 0)
        {
            return;
        }

        var queue = new Queue<RoutineInfo>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        // Seed from every currently-live routine that resolves to a RoutineInfo.
        foreach (string key in ctx.LiveRoutineKeys.ToArray())
        {
            RoutineInfo? r = ctx.Registry.LookupRoutine(fullName: key);
            if (r != null && seen.Add(item: key))
            {
                queue.Enqueue(item: r);
            }
        }

        while (queue.Count > 0)
        {
            RoutineInfo r = queue.Dequeue();
            foreach (RoutineInfo callee in WiredLeafCalleesOf(routine: r))
            {
                // Skip generic-def callees — those are emitted (with their callees) via
                // ProcessConcreteType for each concrete instantiation, not by this leaf closure.
                if (callee.OwnerType?.IsGenericDefinition == true)
                {
                    continue;
                }

                ctx.LiveRoutineKeys.Add(item: callee.RegistryKey);
                if (seen.Add(item: callee.RegistryKey))
                {
                    queue.Enqueue(item: callee);
                }
            }
        }
    }

    /// <summary>
    /// Post-fixpoint ISOLATED materialization of the entity self-free tail — the build-one-in-isolation
    /// primitive the whole-program AOT model lacks (the tractable core of on-demand/lazy materialization).
    /// Seeds the codegen-injected <c>hijack</c>/<c>Hijacked[E].invalidate</c> per concrete entity (no
    /// caller carries a ResolvedRoutine, so a pure call walk can't find them), then closes over the ACTUAL
    /// lifecycle call graph — each callee built via the SINGULAR resolved-routine builder (one body, NO
    /// transitive drain; its own callees stay extern declares). Because it follows real calls, it never
    /// builds an uncalled derive like <c>Array[SerialValue,63].assign</c> (the drain's crash/runaway). Runs
    /// ONLY post-fixpoint: mid-fixpoint, marking these keys live re-triggers a round whose
    /// <c>ProcessConcreteType</c> materializes that abstract derive → crash. Returns the count built.
    /// </summary>
    internal int MaterializeEntitySelfFreeInIsolation()
    {
        // This GMP instance is constructed fresh (not via RunGlobal), so its FindInStdlib index is empty.
        if (!_routineIndexBuilt)
        {
            BuildRoutineIndex();
            _routineIndexBuilt = true;
        }

        var worklist = new Queue<MonomorphizedBody>();
        int totalBuilt = 0;

        // SEED: the entity self-free tail (`me.hijack().invalidate()`) is CODEGEN-INJECTED with no
        // ResolvedRoutine, so the call-driven closure can't discover it — enumerate it per concrete entity.
        SeedEntitySelfFreeTail(worklist: worklist, totalBuilt: ref totalBuilt);

        // SEED the per-type LIFECYCLE HOOKS on EVERY REGISTERED instance. Codegen synthesizes a body per
        // type for each of these hooks — the teardown `destroy`/`roam_free` (field-walk `me.f.destroy()`
        // + entity self-free) and the cycle-tracer `roam_trace` (field-walk `me.f.cyclic_visit()` /
        // `Hijacked[f].cyclic_trace_buffer()`). Their calls are codegen-injected, so the call-driven walk
        // can't discover them; but the HOOKS themselves are registered per-type routines. Build the hooks on
        // every registered concrete + wrapper instance — bounded by the finite registry (NO
        // GetOrCreateWrapperType, NO field recursion → no speculative Hijacked[Hijacked[…]] runaway) and
        // SELF-CONTAINED (each hook's field-walk callee is itself a registered instance, discovered + built by
        // the call-driven closure below). We seed the HOOKS, not the leaf verbs (cyclic_trace_buffer etc.) —
        // those fall out of the closure. NOT display (represent/diagnose): that's a force-seeded closure whose
        // callees escape the registry (type_name, nested represents on non-registered members) → net-new
        // declares (measured: adding represent took the gap 140→367). Skip pseudo-concrete instances whose
        // const-generic arg is an UNFOLDED buildtime splice (`Array[U8, ${max(...)}]` carrier — its `N` never
        // folds → codegen `Unknown identifier N`; a FOLDED `Array[U8,63]` builds fine).
        SeedLifecycleHooks(worklist: worklist, totalBuilt: ref totalBuilt);

        // CALL-DRIVEN closure: walk each freshly-built body, isolated-build every ResolvedRoutine callee it
        // references that isn't built yet, enqueue the result. Follows the ACTUAL lifecycle call graph
        // (hijack → get_address / Hijacked.create; invalidate → Hijacked.address; …) — NOT the transitive
        // resolution drain (which also builds derives like the abstract Array[SerialValue,63].assign that
        // nothing calls). Bounded by the finite lifecycle call graph; a visited set walks each body once.
        DriveCallClosureWorklist(worklist: worklist, totalBuilt: ref totalBuilt);
        return totalBuilt;
    }

    /// <summary>
    /// Seeds the isolation worklist with hijack and Hijacked.invalidate for each concrete entity.
    /// These are codegen-injected with no ResolvedRoutine, so the call-driven closure cannot discover them.
    /// </summary>
    private void SeedEntitySelfFreeTail(Queue<MonomorphizedBody> worklist, ref int totalBuilt)
    {
        foreach (TypeSymbol t in ctx.Registry.AllConcreteGenericInstancesUnfiltered.ToArray())
        {
            if (t is not EntityTypeSymbol { IsGenericDefinition: false } entity)
            {
                continue;
            }

            if (BuildOneInIsolation(r: ctx.Registry.LookupMemberRoutine(type: entity,
                        memberRoutineName: RuntimeContract.RawPointer.Hijack),
                    totalBuilt: ref totalBuilt) is { } hb)
            {
                worklist.Enqueue(item: hb);
            }

            TypeSymbol hijacked = ctx.Registry.GetOrCreateWrapperType(
                wrapperName: RuntimeContract.Hijacked,
                innerType: entity,
                isReadOnly: false);
            if (BuildOneInIsolation(r: ctx.Registry.LookupMemberRoutine(type: hijacked,
                        memberRoutineName: RuntimeContract.RawPointer.Invalidate),
                    totalBuilt: ref totalBuilt) is { } ib)
            {
                worklist.Enqueue(item: ib);
            }
        }
    }

    /// <summary>
    /// Seeds the isolation worklist with lifecycle hooks (destroy/roam_free/roam_trace) on
    /// every registered concrete and wrapper instance that has no unfolded buildtime type argument.
    /// </summary>
    private void SeedLifecycleHooks(Queue<MonomorphizedBody> worklist, ref int totalBuilt)
    {
        string[] lifecycleHooks = ["destroy", "roam_free", "roam_trace"];
        foreach (TypeSymbol t in ctx.Registry
                                  .AllConcreteGenericInstancesUnfiltered
                                  .Concat(second: ctx.Registry
                                                     .AllConcreteWrapperInstancesUnfiltered)
                                  .ToArray())
        {
            if (HasUnfoldedBuildtimeArg(ty: t))
            {
                continue;
            }

            foreach (string hook in lifecycleHooks)
            {
                if (BuildOneInIsolation(
                        r: ctx.Registry.LookupMemberRoutine(type: t, memberRoutineName: hook),
                        totalBuilt: ref totalBuilt) is { } db)
                {
                    worklist.Enqueue(item: db);
                }
            }
        }
    }

    /// <summary>Returns true when any type argument is an unfolded buildtime splice, which cannot be codegen'd.</summary>
    private static bool HasUnfoldedBuildtimeArg(TypeSymbol ty)
    {
        return ty.TypeArguments is { Count: > 0 } a &&
               a.Any(predicate: x => x is BuildtimeConstGenericTypeSymbol);
    }

    /// <summary>
    /// Drives the call-closure worklist: walks each built body, isolated-builds every callee it
    /// references that isn't already built, and enqueues the result. Bounded by a guard counter.
    /// </summary>
    private void DriveCallClosureWorklist(Queue<MonomorphizedBody> worklist, ref int totalBuilt)
    {
        var walked = new HashSet<string>(comparer: StringComparer.Ordinal);
        int guard = 0;
        // ref parameter cannot be captured by the walk lambda (CS1628); mirror it in a local and write back.
        int localTotal = totalBuilt;
        while (worklist.Count > 0 && guard++ < 2_000_000)
        {
            MonomorphizedBody body = worklist.Dequeue();
            if (body.Ast?.Body == null || !walked.Add(item: body.Info.RegistryKey))
            {
                continue;
            }

            AstWalker.WalkExpressions(root: body.Ast.Body,
                visit: expr =>
                {
                    RoutineInfo? callee = expr switch
                    {
                        CallExpression { ResolvedRoutine: { } cr } => cr,
                        GenericMemberRoutineCallExpression { ResolvedRoutine: { } gr } => gr,
                        _ => null
                    };
                    if (BuildOneInIsolation(r: callee, totalBuilt: ref localTotal) is { } nb)
                    {
                        worklist.Enqueue(item: nb);
                    }
                });
        }

        totalBuilt = localTotal;
    }

    /// <summary>
    /// Builds ONE routine's body in isolation (singular builder — no transitive drain). Returns the new body
    /// if freshly built, else null. Callees are left as extern declares for a later round.
    /// </summary>
    private MonomorphizedBody? BuildOneInIsolation(RoutineInfo? r, ref int totalBuilt)
    {
        if (r?.GenericDefinition == null)
        {
            return null;
        }

        if (ctx.InstantiatedGenericBodies.ContainsKey(key: r.RegistryKey) ||
            ctx.VariantBodies.ContainsKey(key: r.RegistryKey))
        {
            return null;
        }

        ctx.LiveRoutineKeys.Add(item: r.RegistryKey);
        ProcessResolvedMemberRoutineGenericRoutine(resolvedRoutine: r);
        if (ctx.InstantiatedGenericBodies.TryGetValue(key: r.RegistryKey,
                value: out MonomorphizedBody? b))
        {
            totalBuilt++;
            return b;
        }

        return null;
    }

    /// <summary>
    /// Stage-② DEMAND COLLECTION (build-one-no-drain), generalized from
    /// <see cref="MaterializeEntitySelfFreeInIsolation"/>. Instead of seeding the entity self-free /
    /// lifecycle tail, it seeds from the generic-INSTANCE routines REFERENCED by the current program /
    /// variant / instantiated bodies, builds each missing one via the singular resolved-routine builder
    /// (no transitive drain), then closes over the actual call graph. Builds into THIS ctx's
    /// <c>InstantiatedGenericBodies</c> — pass an aliased-or-copied ctx per the caller's isolation needs.
    /// Bounded: only genuinely-referenced instances are built (never an uncalled derive like
    /// <c>Array[SerialValue,63].assign</c>), so it cannot run away like eager enumeration. Returns the
    /// count freshly built. This is the pull architecture's stage-② core; today it is driven only in
    /// SHADOW to measure completeness vs the push pipeline.
    /// </summary>
    internal int CollectReferencedInIsolation(IEnumerable<(string Key, Statement Body)> entrySeeds,
        IReadOnlyDictionary<string, Statement> programBodies,
        IReadOnlyDictionary<string, Statement>? synthesizedBodies = null)
    {
        if (!_routineIndexBuilt)
        {
            BuildRoutineIndex();
            _routineIndexBuilt = true;
        }

        return new IsolationCollector(gmp: this,
            ctx: ctx,
            programBodies: programBodies,
            synthesizedBodies: synthesizedBodies).Run(entrySeeds: entrySeeds);
    }

    /// <summary>
    /// Holds the mutable walk state for <see cref="CollectReferencedInIsolation"/> and exposes
    /// <see cref="Discover"/> / <see cref="ForceSeedOwner"/> as instance methods so their mutual
    /// recursion does not inflate the outer method's cognitive complexity.
    /// </summary>
    private sealed class IsolationCollector(
        GenericMonomorphizationPass gmp,
        DesugaringContext ctx,
        IReadOnlyDictionary<string, Statement> programBodies,
        IReadOnlyDictionary<string, Statement>? synthesizedBodies)
    {
        private readonly DesugaringContext _ctx = ctx;
        private int _totalBuilt;
        private readonly HashSet<string> _walked = new(comparer: StringComparer.Ordinal);

        private readonly Queue<(string Key, Statement Body)> _worklist = new();

        // Reached concrete owner types — each needs force-seeded codegen-injected verbs.
        private readonly HashSet<TypeSymbol> _reachedOwners =
            new(comparer: ReferenceEqualityComparer.Instance);

        internal int Run(IEnumerable<(string Key, Statement Body)> entrySeeds)
        {
            // SEED with the entry-point BODIES directly (start()/@test/@bench — their RoutineDeclaration.Body
            // is in hand from the caller, NOT looked up: a user `start` body lives in UserPrograms, not
            // RoutineBodies). Mark each SEED's OWN key live too — the fixpoint below only marks CALLEES live
            // (via Discover), so a root entry that nothing calls (e.g. the harness bundle's
            // `StdlibHarness.start` which invokes each fixture's `start`) would otherwise stay un-live and be
            // pruned by codegen's reachability gate — leaving the executable with no entry symbol.
            foreach ((string k, Statement b) in entrySeeds)
            {
                _ctx.LiveRoutineKeys.Add(item: k);
                _worklist.Enqueue(item: (k, b));
            }

            RunFixpoint();
            return _totalBuilt;
        }

        // FIXPOINT: drain the call-graph worklist, then force-seed any newly-reached owner types (their seeds
        // refill the worklist), and repeat until both are exhausted.
        private void RunFixpoint()
        {
            var seededOwners = new HashSet<TypeSymbol>(comparer: ReferenceEqualityComparer.Instance);
            // Dispatched Crashable members seen in any walked body (represent/diagnose/crash_message/crash_title).
            // Seeded on each reached crashable owner after the walk — see the CrashableDispatch case in WalkBody.
            var dispatchMembers = new HashSet<string>(comparer: StringComparer.Ordinal);
            int guard = 0;
            do
            {
                while (_worklist.Count > 0 && guard++ < 5_000_000)
                {
                    WalkBody(dispatchMembers: dispatchMembers);
                }

                foreach (TypeSymbol owner in _reachedOwners
                                          .Where(predicate: seededOwners.Add)
                                          .ToArray())
                {
                    ForceSeedOwner(type: owner);
                }

                // Seed each dispatched Crashable member on every REACHED crashable owner. A crashable is reached
                // only by being thrown, and only a thrown crashable can be in the carrier the dispatch reads — so
                // this is exactly the arm set codegen emits (gated on the member being live). Re-runs each round
                // as _reachedOwners grows; its Discover calls refill the worklist, extending the fixpoint.
                if (dispatchMembers.Count > 0)
                {
                    foreach (TypeSymbol owner in _reachedOwners.ToArray()
                                                             .OfType<CrashableTypeSymbol>())
                    {
                        foreach (string member in dispatchMembers)
                        {
                            Discover(r: _ctx.Registry.LookupMemberRoutine(type: owner,
                                memberRoutineName: member));
                        }
                    }
                }
            } while (_worklist.Count > 0);
        }

        // Walk one body off the worklist: mark throw-error owners, then discover all callee/creator refs.
        private void WalkBody(HashSet<string> dispatchMembers)
        {
            (string key, Statement body) = _worklist.Dequeue();
            if (!_walked.Add(item: key))
            {
                return;
            }

            // `throw E()` → codegen's EmitThrow calls `E.crash_message()` (→ `E.represent()`). The thrown
            // error is NOT reliably a CreatorExpression after lowering (an empty crashable like
            // TaskSpawnError lowers to a non-Creator node), so the CreatorExpression case below misses it.
            // Catch the ThrowStatement directly and MarkOwner its error TYPE — ForceSeedOwner then seeds
            // the crash formatter closure. Mirrors EmitThrow exactly (every reached throw ⇒ crash_message).
            AstWalker.Walk(root: body,
                visit: n =>
                {
                    if (n is ThrowStatement { Error.ResolvedType: { } errType })
                    {
                        MarkOwner(t: errType);
                    }
                });
            AstWalker.WalkExpressions(root: body,
                visit: expr =>
                {
                    switch (expr)
                    {
                        case CallExpression { ResolvedRoutine: { } cr }: Discover(r: cr); break;
                        case GenericMemberRoutineCallExpression { ResolvedRoutine: { } gr }:
                            Discover(r: gr); break;
                        // A constructor (`SelectEmittable(...)`) — reach its create routine AND mark the
                        // constructed type a live owner (an Emittable is often ONLY constructed, so its owner
                        // liveness — needed for Phase-C try_emit emission — comes from here, not a method call).
                        case CreatorExpression ce:
                            Discover(r: ce.ResolvedCreatorRoutine);
                            MarkOwner(t: ce.ConstructedType);
                            break;
                        // A type-erased crashable dispatch (`when is Crashable e => f"{e}"` / `e.diagnose()`).
                        // Codegen lowers it to a `type_id` switch calling `<MemberName>` on each REACHED crashable
                        // (a thrown error is the only thing that can land in the carrier). Record the member; the
                        // post-fixpoint pass seeds it on every reached crashable owner, so codegen (which emits an
                        // arm only for a LIVE member) and this walk agree on the SAME demand set — deterministic
                        // across cold (partial registry) and warm (full-stdlib registry).
                        case CrashableDispatchExpression cd:
                            dispatchMembers.Add(item: cd.MemberName);
                            break;
                        // A routine referenced AS A VALUE (not called) — a bare routine name passed as an
                        // argument (a coroutine/thread entry `coro_body`, a callback, a first-class routine).
                        // Codegen wraps these in an entry/value thunk (EnsureCoroEntryThunk/RoutineValueThunk)
                        // whose body calls the routine, so the routine — and its whole transitive closure
                        // (e.g. coro_body → Worker.do_work → Box's create/destroy) — is genuinely live even
                        // though no CallExpression names it. Discover it here so the demand walk follows it.
                        // Two forms: pre-resolved (ResolvedRoutine set) or a bare name whose ResolvedType is a
                        // RoutineTypeSymbol (codegen resolves it by name+param-types via TryResolveRoutineReference,
                        // e.g. a routine passed to a C function pointer like `rf_coro_create(entry: coro_body)`).
                        case IdentifierExpression { ResolvedRoutine: { } ir }:
                            Discover(r: ir); break;
                        case IdentifierExpression { ResolvedType: RoutineTypeSymbol rvt } rid:
                            Discover(
                                r: gmp.ResolveRoutineValueByName(name: rid.Name,
                                    routineType: rvt));
                            break;
                    }
                });
        }

        // Emittable body for a routine key: a monomorphized generic instance, a synthesized variant, or a
        // concrete non-generic routine body from the actual (lowered) program ASTs — `programBodies`, built by
        // the caller from the SAME source codegen emits from (Registry.StdlibPrograms + UserPrograms, keyed by
        // decl.ResolvedInfo.RegistryKey). This is what lets the walk step into `Bytes.create(from_list:)` and
        // see its `List[Byte].getitem/count` calls. (RoutineBodies — the synthesis working set — is NOT the
        // codegen source and misses flattened stdlib member bodies, so it is not consulted.)
        private Statement? GetBody(string key)
        {
            if (_ctx.InstantiatedGenericBodies.TryGetValue(key: key,
                    value: out MonomorphizedBody? mb))
            {
                return mb.Ast?.Body;
            }

            if (_ctx.VariantBodies.TryGetValue(key: key, value: out Statement? vb))
            {
                return vb;
            }

            if (programBodies.TryGetValue(key: key, value: out Statement? pb))
            {
                return pb;
            }

            // Lowest priority: a SYNTHESIZED body (represent/diagnose/crash_message/crash_title on a concrete
            // crashable, a derived operator) that no program AST holds. Consulting it lets the demand walk step
            // THROUGH a synthesized formatter so the whole closed formatter family is discovered and marked live.
            if (synthesizedBodies != null &&
                synthesizedBodies.TryGetValue(key: key, value: out Statement? sb))
            {
                return sb;
            }

            return null;
        }

        // Mark a reached concrete owner type LIVE + materialized. Codegen's Phase-C synthesized-body emitters
        // (e.g. iterator-adapter `try_emit` per concrete owner) loop AllConcreteGenericInstancesUnfiltered
        // (which excludes lazy instances) and gate on the owner being a live owner type — a reached-but-
        // unmarked Emittable owner leaves its synthesized `try_emit` undefined at link (the
        // warm-restore-overprune Category-B symptom). Called for a routine's owner AND for a
        // CreatorExpression's ConstructedType (an Emittable is often only CONSTRUCTED, never method-called).
        private void MarkOwner(TypeSymbol? t)
        {
            if (t is not { IsGenericDefinition: false } owner)
            {
                return;
            }

            _reachedOwners.Add(item: owner);
            _ctx.Registry.ClearStdlibLazy(type: owner);
            _ctx.LiveOwnerTypeNames.Add(item: owner.FullName);
        }

        // Discover a referenced routine: BUILD it if it is an unbuilt generic INSTANCE (singular builder, no
        // transitive drain); then, whether generic or NON-generic, queue its body for traversal so the walk
        // continues through it (this is how `start` → `Bytes.getitem` → `List[Byte].create` chains). Only
        // genuinely-referenced routines are walked and only referenced instances are built — DCE preserved.
        private void Discover(RoutineInfo? r)
        {
            if (r == null)
            {
                return;
            }

            MarkOwner(t: r.OwnerType);
            string key = r.RegistryKey;
            TriggerOnDemandAnalysis(r: r, key: key);
            // Mark EVERY reached routine live FIRST — generic instance OR non-generic (e.g.
            // `Bytes.create(from_list:)` reached through `Bytes.getitem`). Everything reachable from an entry
            // point is live by definition; codegen's liveness gate only emits a definition for a live key, so
            // a reached-but-unmarked non-generic body would link-fail as an undefined symbol. Must precede the
            // build below — `ProcessResolvedMemberRoutineGenericRoutine` itself gates on the key being live.
            bool newlyLive = _ctx.LiveRoutineKeys.Add(item: key);
            if (newlyLive)
            {
                SeedThrowableCrashPath(r: r);
            }

            BuildGenericInstanceIfNeeded(r: r, key: key);
            MaterializeDeriveTemplateBodyIfNeeded(r: r, key: key);
            if (!_walked.Contains(item: key) && GetBody(key: key) is { } body)
            {
                _worklist.Enqueue(item: (key, body));
            }
        }

        // A reached derive member (lt/le/gt/ge from cmp, or ANY capability-conferred derive — cmp/eq/hash/
        // represent — on a concrete owner that does not hand-write it) whose per-type body lives ONLY in the
        // derive-template store (DeriveText.rf `T.lt() -> me.cmp(you) is ME_SMALL`, etc.). NOTHING else
        // materializes these: they are not hand-written (programBodies), not synthesized by DerivedOperatorPass
        // (synthesizedBodies holds only ne/notcontains/crash_title/wrapper-forwarders), and not a generic
        // INSTANCE (BuildGenericInstanceIfNeeded). Clone the template for this concrete owner (T → owner) so the
        // walk steps THROUGH the body (discovering its cmp/eq callees) AND codegen has a body to emit. The clone
        // lands in InstantiatedGenericBodies as a FRESH entry, so the collector's LowerFreshBodies sweep lowers
        // it (the stored template body is the raw, pre-lowering decl body). Higher-priority body sources win via
        // the guards below, so a type that hand-writes the member (U64.cmp, Text.eq) keeps its own body.
        private void MaterializeDeriveTemplateBodyIfNeeded(RoutineInfo r, string key)
        {
            // An EMPTY synthesized sentinel (0-statement body) already in InstantiatedGenericBodies means
            // BuildGenericInstanceIfNeeded instantiated a generic-def member that has no real body of its own —
            // a DERIVE-ONLY member such as an entity `destroy` on a generic backing type (`ListEmittable[T]`,
            // `ArrayEmittable[T, N]`): the def carries only the signature, the per-type body must still be
            // cloned from the derive template. Codegen SKIPS a 0-statement synthesized body, so leaving the
            // sentinel in place emits no definition → the `destroy` call link-fails ("declared+called but never
            // defined"). So an empty sentinel must NOT block materialization — fall through and overwrite it
            // with the real template clone (the GetDeriveTemplate lookup below is the gate: no matching template
            // → the sentinel is left untouched, which is correct for a type that genuinely has no derive body).
            bool hasRealBody =
                _ctx.InstantiatedGenericBodies.TryGetValue(key: key,
                    value: out MonomorphizedBody? existing) &&
                !(existing.IsSynthesized &&
                  existing.Ast.Body is BlockStatement { Statements.Count: 0 });
            if (hasRealBody ||
                _ctx.VariantBodies.ContainsKey(key: key) ||
                programBodies.ContainsKey(key: key) ||
                (synthesizedBodies?.ContainsKey(key: key) ?? false))
            {
                return;
            }

            // Needs a genuine T → owner substitution: a concrete (fully-resolved) owner, never a generic
            // definition or a bare type-parameter placeholder.
            if (r.OwnerType is not { IsGenericDefinition: false } owner ||
                owner is GenericParameterTypeSymbol)
            {
                return;
            }

            if (_ctx.Registry.GetDeriveTemplate(name: r.Name,
                    arity: r.Parameters.Count,
                    forType: owner) is not { } template)
            {
                return;
            }

            var typeSubs = new Dictionary<string, TypeSymbol>(comparer: StringComparer.Ordinal)
            {
                [key: template.OwnerParam] = owner
            };
            Statement rewritten = GenericAstRewriter.RewriteStatement(stmt: template.Body,
                subs: new Dictionary<string, string>(comparer: StringComparer.Ordinal)
                {
                    [key: template.OwnerParam] = owner.FullName
                },
                typeSubs: typeSubs,
                registry: _ctx.Registry,
                enclosingRoutine: r);

            // The template body is RAW source AST (captured pre-analysis): substitution alone leaves its
            // operands/calls untyped, so operator lowering can't fold `me.type_name() + "("` and it reaches
            // codegen raw. SA-annotate it in the owner's context first (types/calls resolved), THEN it lands
            // as a FRESH body the collector's LowerFreshBodies sweep can lower.
            rewritten = _ctx.AnalyzeMaterializedDeriveBody?.Invoke(arg1: r, arg2: rewritten) ??
                        rewritten;

            // IsSynthesized: FALSE deliberately — unlike a DerivedOperatorPass body (built pre-resolved +
            // pre-lowered), this clone is raw source that STILL needs the fresh-body lowering sweep
            // (BodyDispatch skips IsSynthesized entries). Marking it non-synthesized routes it through the
            // same lowering + emit path as a hand-written stdlib body (which MaterializeReachedStdlibBodies
            // also stores IsSynthesized: false), so its `is ME_SMALL` / operator nodes get lowered.
            _ctx.InstantiatedGenericBodies[key: key] = new MonomorphizedBody(
                Ast: WrapInShellDecl(name: r.Name, body: rewritten, info: r),
                Info: r,
                TypeSubs: typeSubs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized: false);
        }

        // Stage-2 (pull/(B)) demand resolution: ensure this reached routine's body is analyzed (calls/types
        // resolved) BEFORE we read/monomorphize it. No-op under the pre-(B) eager pipeline (hook guarded there).
        // Also triggers on-demand analysis for each key in the whole generic-definition chain — the demand map
        // is keyed by the ROOT source decl's RegistryKey, so intermediate concrete-owner keys need the chain walk.
        private void TriggerOnDemandAnalysis(RoutineInfo r, string key)
        {
            bool desugaredNewFile = _ctx.AnalyzeRoutineOnDemand?.Invoke(arg: key) ?? false;
            for (RoutineInfo? gd = r.GenericDefinition; gd != null; gd = gd.GenericDefinition)
            {
                if (gd.RegistryKey is { } gdKey && gdKey != key)
                {
                    desugaredNewFile |= _ctx.AnalyzeRoutineOnDemand?.Invoke(arg: gdKey) ?? false;
                }
            }

            // The demand analyzer REASSIGNS decl.Body when it desugars a newly-reached file (immutable
            // rewriters → new decl in program.Declarations). Our _routineIndex snapshotted the OLD decls at
            // walk start, so ProcessResolvedMemberRoutineGenericRoutine below would clone the STALE, un-lowered
            // template. Rebuild the index so monomorphization sources the freshly-desugared def.
            if (desugaredNewFile)
            {
                gmp.BuildRoutineIndex();
            }
        }

        // Crash path per LIVE routine: codegen's EmitThrow calls `<E>.crash_message()` (→ `<E>.represent()`)
        // for every crashable E this routine DIRECTLY throws (RoutineInfo.ThrowableTypes — populated post Phase-4,
        // covering the routine and its check_/lookup_ variant). Seeding off ThrowableTypes matches codegen's
        // reference set EXACTLY — demand-scoped to live routines, so no over-materialization of unthrown errors.
        // ThrowableTypes is populated on the GENERIC DEF (Phase-4 body analysis), not copied onto each
        // monomorphized instance — so a generic throw needs the def's list too.
        private void SeedThrowableCrashPath(RoutineInfo r)
        {
            foreach (TypeSymbol thrown in r.ThrowableTypes.Concat(
                         second: r.GenericDefinition?.ThrowableTypes ??
                                 Enumerable.Empty<TypeSymbol>()))
            {
                if (_ctx.Registry.LookupMemberRoutine(type: thrown,
                        memberRoutineName: RuntimeContract.CrashMessage) is not { } tcm)
                {
                    continue;
                }

                Discover(r: tcm);
                Discover(r: _ctx.Registry.LookupMemberRoutine(type: thrown,
                    memberRoutineName: RepresentMemberRoutineName));
            }
        }

        // Build the monomorphized body for a generic instance if it hasn't been built yet.
        private void BuildGenericInstanceIfNeeded(RoutineInfo r, string key)
        {
            if (r.GenericDefinition == null)
            {
                return;
            }

            if (_ctx.InstantiatedGenericBodies.ContainsKey(key: key))
            {
                return;
            }

            if (_ctx.VariantBodies.ContainsKey(key: key))
            {
                return;
            }

            gmp.ProcessResolvedMemberRoutineGenericRoutine(resolvedRoutine: r);
            if (_ctx.InstantiatedGenericBodies.ContainsKey(key: key))
            {
                _totalBuilt++;
            }
        }

        // Force-seed the codegen-injected / synthesis-emitted routines on a reached OWNER type — they have no
        // AST call for the walk to follow. The collector is self-sufficient: this is the SOLE force-seeder now
        // (the reachability-side pass was retired + deleted, Stage-3). Each seed goes through
        // Discover (build-if-generic + mark-live + queue-body).
        private void ForceSeedOwner(TypeSymbol type)
        {
            // ROOT of the itertools builder StackOverflow: a NON-CONCRETE owner (unbound generic param, e.g. a
            // `Routine[(T,), Bool]` predicate-field type that was marked a reached owner while its `T` was still
            // open) must NOT have its universal derives materialized. Doing so binds the derive's own owner
            // param `T` to that owner — but the owner CONTAINS `T` (`T := Routine[(T,), Bool]`), an infinite
            // type that later crashes `ResolveType`. Only a fully-concrete owner is a real instance to seed.
            if (ContainsGenericParam(t: type))
            {
                return;
            }

            SeedNonCallDrivenWiredRoutines(type: type);
            SeedImplicitCodegenInserts(type: type);
            SeedEntitySelfFreeIfApplicable(type: type);
            SeedLifecycleHooksIfApplicable(type: type);
            SeedCommonUnpackedFloatHelpers(type: type);
        }

        // (1) Wired derives. The CALL-DRIVEN family is EXCLUDED (denylist): each is reached through a real
        // lowered call site. Force-seeding those per reached owner OVER-APPROXIMATES — drags in dead chains the
        // retired codegen liveness gate used to prune. Only non-call-driven, non-operator wired names are seeded.
        private void SeedNonCallDrivenWiredRoutines(TypeSymbol type)
        {
            foreach (string wiredName in WiredRoutineCatalog.BuildReachabilitySeedNames())
            {
                if (IsCallDrivenWiredName(wiredName: wiredName))
                {
                    continue;
                }

                // NOTE: `setitem` is NOT excluded (unlike the call-driven `getitem`). An index assignment
                // `a[i] = x` is lowered to a `setitem` CALL by CODEGEN at emission, not to an AST call the
                // demand walk can follow, so setitem is never reached call-driven and must be force-seeded per
                // reached owner or it links undefined (Array[S64,4]/Vector[F32,4]/BitArray/SplitArray/List).
                // The OPERATOR family is call-driven too. Skip every call-driven operator kind; the genuinely
                // non-discoverable framework hook (cyclic_visit / CycleTrace) and the denylist names above stay.
                if (WiredRoutineCatalog.TryGet(name: wiredName, entry: out WiredEntry seedEntry) &&
                    IsCallDrivenWiredKind(kind: seedEntry.Kind))
                {
                    continue;
                }

                if (!_ctx.Registry.TypeHasWiredRoutine(type: type, wiredName: wiredName))
                {
                    continue;
                }

                Discover(r: _ctx.Registry.LookupMemberRoutine(type: type,
                    memberRoutineName: wiredName));
            }
        }

        // Returns true for wired names that are always reached through a real lowered call-site (not codegen-injected).
        private static bool IsCallDrivenWiredName(string wiredName)
        {
            return wiredName is RepresentMemberRoutineName or "diagnose" or "hash" or "serialize"
                or "duplicate" or "assign" or "eq" or "ne" or "cmp" or "lt" or "le" or "gt" or "ge"
                or "contains" or "notcontains" or "getitem" or "iter";
        }

        // Returns true for wired operator kinds that are reached call-driven (force-seeding them over-approximates).
        private static bool IsCallDrivenWiredKind(WiredKind kind)
        {
            return kind is WiredKind.Arithmetic or WiredKind.ArithmeticWrap
                or WiredKind.ArithmeticClamp or WiredKind.ArithmeticUnchecked or WiredKind.Bitwise
                or WiredKind.Shift or WiredKind.Unary or WiredKind.Unwrap
                or WiredKind.InPlaceArithmetic or WiredKind.InPlaceBitwise
                or WiredKind.InPlaceShift;
        }

        // (2) Implicit codegen inserts (RC copy verb; Roamed promote/lock/raw_inner; display transparency).
        // (2b) Crash path — `throw X` is genuinely NON-discoverable (control flow, no AST call): codegen's
        //   EmitThrow calls `X.crash_message()`, whose body formats via `X.represent()`. Seed both per reached
        //   throwable owner. represent is on the call-driven denylist (f-string/show), so seed it explicitly here.
        // (2c) Lock-policy no-arg constructor. A `Guarded[T, P]` / `GuardController[T, P]` initializes its
        //   lock field by constructing P (Exclusive/MultiRead/ReadOnly), but the ctor is synthesized into the
        //   container's create with NO resolved-creator AST call the walk can follow. Seed P.create on each
        //   reached lock policy so codegen's lock-field init links. Demand-scoped to a reached policy type.
        private void SeedImplicitCodegenInserts(TypeSymbol type)
        {
            foreach ((TypeSymbol owner, string mn) in ImplicitCallContract.ForLiveType(
                         liveType: type))
            {
                Discover(r: _ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: mn));
            }

            if (_ctx.Registry.LookupMemberRoutine(type: type,
                    memberRoutineName: RuntimeContract.CrashMessage) is { } crashMsg)
            {
                Discover(r: crashMsg);
                Discover(r: _ctx.Registry.LookupMemberRoutine(type: type,
                    memberRoutineName: RepresentMemberRoutineName));
            }

            if (_ctx.Registry.DoesTypeObeyProtocol(type: type, protocolName: "LockPolicy"))
            {
                Discover(r: _ctx.Registry.LookupCreator(type: type));
            }
        }

        // (3) Entity self-free tail (hijack / Hijacked[E].invalidate — zero-arg universal, no AST call).
        // Skip a type that still carries a generic parameter: GetOrCreateWrapperType would mint
        // `Hijacked[RangeEmittable[T]]` recursively → unbounded monomorphization. Only fully-concrete entities.
        // (4) Text.replace — FStringLoweringPass synthesizes it for `:?`/`?` in-flight-entity interpolation.
        private void SeedEntitySelfFreeIfApplicable(TypeSymbol type)
        {
            if (type is { Name: "Text", Module: "Core" })
            {
                Discover(r: _ctx.Registry.LookupMemberRoutine(type: type,
                    memberRoutineName: RuntimeContract.Collection.Replace));
            }

            if (type is not EntityTypeSymbol { IsGenericDefinition: false })
            {
                return;
            }

            if (ContainsGenericParam(t: type))
            {
                return;
            }

            Discover(r: _ctx.Registry.LookupMemberRoutine(type: type,
                memberRoutineName: RuntimeContract.RawPointer.Hijack));
            TypeSymbol hijacked = _ctx.Registry.GetOrCreateWrapperType(
                wrapperName: RuntimeContract.Hijacked,
                innerType: type,
                isReadOnly: false);
            Discover(r: _ctx.Registry.LookupMemberRoutine(type: hijacked,
                memberRoutineName: RuntimeContract.RawPointer.Invalidate));
        }

        // (3b) Per-type LIFECYCLE HOOKS (destroy / roam_free / roam_trace). Codegen synthesizes
        // teardown + cycle-trace calls per reached type with NO source AST call (scope-exit destroy of a
        // temporary, a wrapper/routine-value's destroy). Base mode seeds these over ALL registered instances
        // (MaterializeEntitySelfFreeInIsolation); mirror that DEMAND-scoped on each reached concrete owner.
        // Also seeds the exact lifecycle-destroy GetLifecycle returns — routine-VALUEs carry a `destroy`
        // that `LookupMemberRoutine` misses but GetLifecycle surfaces.
        private void SeedLifecycleHooksIfApplicable(TypeSymbol type)
        {
            if (ContainsGenericParam(t: type))
            {
                return;
            }

            foreach (string hook in new[]
                     {
                         "destroy",
                         "roam_free",
                         "roam_trace"
                     })
            {
                if (_ctx.Registry.LookupMemberRoutine(type: type, memberRoutineName: hook) is
                    { } hookR)
                {
                    Discover(r: hookR);
                }
            }

            if (_ctx.Registry.GetLifecycle(type: type) is
                { IsBorrow: false, Destroy: { } lcDestroy })
            {
                Discover(r: lcDestroy);
            }
        }

        // (5) `common` routines. Force-seeding EVERY common member of a reached owner is an
        // over-approximation (NON-DETERMINISTIC — warm vs cold build differ). Only the genuinely
        // NON-discoverable common family is seeded: the UnpackedFloat integer-width helpers (to_width /
        // low_mask / from_words), reached only through nested generic-member instantiation with no AST call.
        private void SeedCommonUnpackedFloatHelpers(TypeSymbol type)
        {
            foreach (RoutineInfo commonR in _ctx.Registry
                                                .GetMemberRoutinesForType(type: type)
                                                .Where(predicate: r =>
                                                     r is
                                                     {
                                                         IsCommon: true, IsGenericDefinition: false
                                                     } && r.Name is "to_width" or "low_mask"
                                                         or "from_words"))
            {
                Discover(r: commonR);
            }
        }

        /// <summary>True when <paramref name="t"/> still carries a generic parameter (directly or
        /// nested in a type argument) — i.e. not yet a fully-concrete monomorphized type.</summary>
        private static bool ContainsGenericParam(TypeSymbol t)
        {
            // A RoutineTypeSymbol carries its holes in ParameterTypes/ReturnType, NOT TypeArguments — so a
            // non-concrete `Routine[(T,), Bool]` would slip past a TypeArguments-only scan and get force-seeded
            // as if concrete (then its universal derive binds `T := Routine[(T,), Bool]` → infinite type).
            if (t is RoutineTypeSymbol rt)
            {
                return rt.ParameterTypes.Any(predicate: ContainsGenericParam) ||
                       rt.ReturnType is { } ret && ContainsGenericParam(t: ret);
            }

            return t is GenericParameterTypeSymbol or ProtocolSelfTypeSymbol
                       or BuildtimeConstGenericTypeSymbol ||
                   (t.TypeArguments?.Any(predicate: ContainsGenericParam) ?? false);
        }
    }

    /// <summary>
    /// Resolves a bare routine-VALUE reference (an identifier typed as a <see cref="RoutineTypeSymbol"/>, e.g.
    /// a coroutine entry passed to a C function pointer) to its <see cref="RoutineInfo"/> by name + parameter
    /// types via the ONLY legitimate resolver, signature-based <see cref="TypeRegistry.LookupRoutineOverload"/>.
    /// (Name-only lookups are not a valid resolution path.) Returns null if unresolved (Discover no-ops on null).
    /// </summary>
    private RoutineInfo? ResolveRoutineValueByName(string name, RoutineTypeSymbol routineType)
    {
        return ctx.Registry.LookupRoutineOverload(baseName: name,
            argTypes: routineType.ParameterTypes.ToList());
    }

    /// <summary>
    /// Yields the wired routines a synthesized body for <paramref name="routine"/> implicitly calls.
    /// See <see cref="EnliveWiredLeafCallees"/> for the rationale and the verb→callee mapping.
    /// </summary>
    private IEnumerable<RoutineInfo> WiredLeafCalleesOf(RoutineInfo routine)
    {
        TypeSymbol? owner = routine.OwnerType;
        if (owner == null)
        {
            yield break;
        }

        switch (routine.Name)
        {
            case "lt" or "le" or "gt" or "ge" or "cmp":
                foreach (RoutineInfo r in YieldComparisonWiredCallees(owner: owner,
                             routineName: routine.Name))
                {
                    yield return r;
                }

                break;
            case "ne":
                if (ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: "eq") is
                    { } eq)
                {
                    yield return eq;
                }

                break;
            case "notcontains":
                if (ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: "contains") is
                    { } contains)
                {
                    yield return contains;
                }

                break;
            case "destroy" or "assign" or "hash" or "eq":
                foreach (RoutineInfo fc in MemberVariableWiredCallees(owner: owner,
                             verb: routine.Name))
                {
                    yield return fc;
                }

                break;
        }
    }

    /// <summary>
    /// Yields the wired callees for comparison routines (lt, le, gt, ge, cmp): the owner's cmp,
    /// the ComparisonSign eq/ne testers, and for composite cmp the field-wise cmp callees.
    /// </summary>
    private IEnumerable<RoutineInfo> YieldComparisonWiredCallees(TypeSymbol owner,
        string routineName)
    {
        if (routineName != "cmp" &&
            ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: "cmp") is { } cmp)
        {
            yield return cmp;
        }

        if (ctx.Registry.LookupType(name: "ComparisonSign") is { } cs)
        {
            if (ctx.Registry.LookupMemberRoutine(type: cs, memberRoutineName: "eq") is { } cseq)
            {
                yield return cseq;
            }

            if (ctx.Registry.LookupMemberRoutine(type: cs, memberRoutineName: "ne") is { } csne)
            {
                yield return csne;
            }
        }

        if (routineName == "cmp")
        {
            foreach (RoutineInfo fc in MemberVariableWiredCallees(owner: owner, verb: "cmp"))
            {
                yield return fc;
            }
        }
    }

    /// <summary>
    /// Yields the <paramref name="verb"/> wired routine on each owned field/element/payload type of
    /// a composite <paramref name="owner"/> (record, entity, crashable, tuple, variant). Scalar
    /// kinds (choices, flags, <c>@llvm</c>-backed primitives) have no fields and yield nothing.
    /// Mirrors <c>WiredRoutinePass.BuildDestroyBody</c>'s field selection.
    /// </summary>
    private IEnumerable<RoutineInfo> MemberVariableWiredCallees(TypeSymbol owner, string verb)
    {
        IEnumerable<TypeSymbol> fieldTypes = owner switch
        {
            VariantTypeSymbol v => v.Members
                                  .Where(predicate: m => m is { IsNone: false, Type: not null })
                                  .Select(selector: m => m.Type!),
            EntityTypeSymbol e => e.MemberVariables.Select(selector: f => f.Type),
            TupleTypeSymbol t => t.MemberVariables.Select(selector: f => f.Type),
            ChoiceTypeSymbol or FlagsTypeSymbol => [],
            RecordTypeSymbol { BackendType: null } r => r.MemberVariables.Select(selector: f =>
                f.Type),
            _ => []
        };

        foreach (TypeSymbol ft in fieldTypes)
        {
            if (ft == null)
            {
                continue;
            }

            if (ctx.Registry.LookupMemberRoutine(type: ft, memberRoutineName: verb) is { } callee)
            {
                yield return callee;
            }
        }
    }

    /// <summary>
    /// Adds every concrete (fully-resolved) generic owner type reachable from <paramref name="t"/>
    /// — including those nested in its type arguments — to <paramref name="sink"/>. Generic
    /// definitions and types with unresolved parameters are ignored.
    /// </summary>
    private static void CollectConcreteOwnerTypes(TypeSymbol? t, Dictionary<string, TypeSymbol> sink)
    {
        if (t == null || HasUnresolvedTypeArgs(t: t))
        {
            return;
        }

        if (GetGenericBase(type: t) != null || t is WrapperTypeSymbol)
        {
            sink.TryAdd(key: t.FullName, value: t);
        }

        if (t.TypeArguments is { Count: > 0 } args)
        {
            foreach (TypeSymbol arg in args)
            {
                CollectConcreteOwnerTypes(t: arg, sink: sink);
            }
        }
    }
    // Per-type processing

    private void ProcessConcreteType(TypeSymbol concreteType)
    {
        // Strategy-B reachability gate at the type level: skip concrete instances that no
        // reachable routine ever owned. This prevents unreachable types like Array[BuildMode, 63]
        // or BTreeListNode[Text] from emitting try_emit/getitem!/etc. via the wired-routine bypass
        // on the per-routine gate.
        //
        // The "reachability ran" signal is LiveRoutineKeys (NOT LiveOwnerTypeNames): reachability
        // always seeds at least the entry point, so a non-empty LiveRoutineKeys means the pass ran.
        // A program that uses no generic types at all produces a genuinely EMPTY LiveOwnerTypeNames
        // — gating on `LiveOwnerTypeNames.Count > 0` would misread that as "filter off" and fan out
        // over every concrete instance in the registry (BuilderQuery/BTree/numeric machinery),
        // force-emitting wired operators whose plain-helper callees (add_with_overflow, recast_as,
        // cmp) are never emitted → LINKERR. Mirror the per-routine gate at the BuildBody site below,
        // which already keys off LiveRoutineKeys. Both empty = legacy fan-out (reachability skipped).
        if (ctx.LiveRoutineKeys.Count > 0 &&
            !ctx.LiveOwnerTypeNames.Contains(item: concreteType.FullName))
        {
            return;
        }

        TypeSymbol? genDef = concreteType switch
        {
            EntityTypeSymbol { GenericDefinition: { } d } => d,
            RecordTypeSymbol { GenericDefinition: { } d } => d,
            WrapperTypeSymbol wrapper => ctx.Registry.LookupType(name: wrapper.Name),
            _ => null
        };

        if (genDef?.GenericParameters == null || genDef.GenericParameters.Count == 0)
        {
            return;
        }

        List<TypeSymbol>? typeArgs = concreteType.TypeArguments;
        if (typeArgs == null || typeArgs.Count != genDef.GenericParameters.Count)
        {
            return;
        }

        (Dictionary<string, TypeSymbol> typeSubs, Dictionary<string, string> stringSubs) =
            BuildConcreteTypeSubstitutionMaps(genDef: genDef, typeArgs: typeArgs);

        foreach (RoutineInfo genMemberRoutine in ctx.Registry.GetMemberRoutinesForType(
                     type: genDef))
        {
            ProcessConcreteTypeMemberRoutine(concreteType: concreteType,
                genDef: genDef,
                genMemberRoutine: genMemberRoutine,
                typeSubs: typeSubs,
                stringSubs: stringSubs);
        }
    }

    /// <summary>
    /// Builds the owner type-parameter substitution maps for a concrete generic instance.
    /// stringSubs uses FullName (e.g. "T" → "Core.S64") so rewritten AST type-expression names are
    /// fully qualified; typeSubs carries the resolved TypeSymbol for ResolvedType annotation in
    /// GenericAstRewriter.
    /// </summary>
    private static (Dictionary<string, TypeSymbol> typeSubs, Dictionary<string, string> stringSubs)
        BuildConcreteTypeSubstitutionMaps(TypeSymbol genDef, List<TypeSymbol> typeArgs)
    {
        var typeSubs = new Dictionary<string, TypeSymbol>(capacity: genDef.GenericParameters!.Count);
        var stringSubs = new Dictionary<string, string>(capacity: genDef.GenericParameters.Count);
        for (int i = 0; i < genDef.GenericParameters.Count; i++)
        {
            typeSubs[key: genDef.GenericParameters[index: i]] = typeArgs[index: i];
            stringSubs[key: genDef.GenericParameters[index: i]] = typeArgs[index: i].FullName;
        }

        return (typeSubs, stringSubs);
    }

    /// <summary>
    /// Builds and stores the monomorphized body for one member routine of a concrete generic
    /// instance, applying the reachability + capability gates and the abstract-derive check.
    /// </summary>
    private void ProcessConcreteTypeMemberRoutine(TypeSymbol concreteType, TypeSymbol genDef,
        RoutineInfo genMemberRoutine, Dictionary<string, TypeSymbol> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        RoutineInfo? concreteInfo = BuildConcreteRoutineInfo(genMemberRoutine: genMemberRoutine,
            concreteOwner: concreteType,
            typeSubs: typeSubs);

        // Null means the forwarder's inner type doesn't have this memberRoutine — skip it.
        if (concreteInfo == null)
        {
            return;
        }

        string key = concreteInfo.RegistryKey;
        if (ctx.InstantiatedGenericBodies.ContainsKey(key: key))
        {
            return;
        }

        // Strategy-B reachability gate: when LiveRoutineKeys is populated, only emit bodies
        // for routines reachable from program entry points. Empty set disables the filter
        // (legacy fan-out behavior). RoutineReachabilityPass populates the set.
        // Wired routines bypass the gate: codegen/synthesis emit them unconditionally for
        // every live owner type. Types created during GMP body rewriting (e.g.,
        // ListEmitter[Byte] from List[Byte].represent) post-date RoutineReachabilityPass
        // and so were never seeded — without this bypass their wired routines vanish.
        // Genuinely reachable from entry points (in the live set) vs merely wired-bypass-built for a
        // live OWNER (a wired routine synthesized for every owner even if never called). Only the former
        // is asserted below — a wired-bypass body (e.g. `Roamed[Node].eq` synthesized but never compared)
        // may legitimately carry an abstract-forwarding call that is dead and never emitted.
        bool genuinelyLive =
            ctx.LiveRoutineKeys.Count == 0 || ctx.LiveRoutineKeys.Contains(item: key);
        if (!genuinelyLive && !IsWiredRoutineName(name: genMemberRoutine.Name))
        {
            return;
        }

        // Capability gate: skip wired comparison/containment/hashing routines whose
        // generic-def constraint (e.g. `needs T obeys Equatable`) is not satisfied by
        // this concrete owner. See RoutineApplicableToConcreteOwner.
        if (!RoutineApplicableToConcreteOwner(routine: concreteInfo, owner: concreteType))
        {
            return;
        }

        MonomorphizedBody? body = BuildBody(genMemberRoutine: genMemberRoutine,
            concreteInfo: concreteInfo,
            genDef: genDef,
            typeSubs: typeSubs,
            stringSubs: stringSubs);

        if (body != null && BodyHasAbstractDeriveCall(body: body, member: out string absMember))
        {
            body = HandleAbstractDeriveCall(genDef: genDef,
                concreteInfo: concreteInfo,
                absMember: absMember);
        }

        if (body != null)
        {
            ctx.InstantiatedGenericBodies[key: key] = body;
            // Keep codegen's Phase-B live gate in sync with GMP's wired-routine bypass.
            // LlvmEmitter emits an instantiated body only when its key is in the live set,
            // with NO wired bypass — so a wired routine emitted here for a live owner that
            // post-dates RoutineReachabilityPass (e.g. try_emit on a chained iterator emitter
            // like SelectEmitter[S64, S64, ListEmitter[S64]]) was never seeded and would be
            // silently dropped at codegen. Marking the key live closes that gap; non-wired
            // emissions already have a live key, so this is a no-op for them.
            ctx.LiveRoutineKeys.Add(item: key);
        }
    }

    /// <summary>
    /// Handles a monomorphized body that still calls an abstract derive member. TWO cases:
    ///  * WRAPPER forwarder (Roamed/Retained/…): the inner type simply doesn't implement this
    ///    member (e.g. `Roamed[Node].eq` where the entity Node isn't Equatable). The forwarder is
    ///    DEAD — drop it (return null), exactly as if it were never synthesized.
    ///  * Anything else (a real collection/carrier like `List[Text].copy`): the receiver obeys the
    ///    protocol but has no concrete derive — a genuine conformance/derive gap. Blow up LOUDLY.
    /// </summary>
    private static MonomorphizedBody? HandleAbstractDeriveCall(TypeSymbol genDef,
        RoutineInfo concreteInfo, string absMember)
    {
        bool ownerIsWrapper = WrapperForwardingPass.WrapperTypeNames.Contains(item: genDef.Name);
        if (ownerIsWrapper)
        {
            return null;
        }

        throw new InvalidOperationException(
            message:
            $"Monomorphization left a call to the ABSTRACT protocol member '{absMember}' inside the " +
            $"concrete routine '{concreteInfo.RegistryKey}'. The receiver obeys the protocol but has " +
            "NO concrete derive/impl to resolve to — the conformance/derive layer must provide it " +
            "(a managed leaf like Text implements it; a variant that obeys Copyable gets its arm-walk " +
            "`copy`). Failing loudly pre-codegen instead of emitting an unresolved abstract symbol.");
    }

    /// Routines emitted for every live owner regardless of call-site reachability. Two sources:
    /// (1) the unified-teardown lifecycle routines (<c>destroy</c>/<c>store</c>, from
    /// <see cref="Builder.Declaration.WiredRoutineCatalog.AlwaysLiveNames"/>) — scope-exit teardown
    /// inserts <c>destroy</c> calls that must always have a concrete body, and the matching
    /// retaining <c>store</c> likewise; (2) <c>try_emit</c>, reachable only through synthesized
    /// for-loop iteration bodies whose owner type (ListEmitter[Byte], etc.) is created post-pass
    /// during GMP body rewriting, so ReachabilityPass cannot trace it. Kept narrow otherwise —
    /// broader sets cascade into derived-op chains (ne->eq) where the missing companion is the
    /// actual culprit.
    private static readonly HashSet<string> _gateBypassNames =
        new(collection: WiredRoutineCatalog.AlwaysLiveNames, comparer: StringComparer.Ordinal)
        {
            RuntimeContract.TryEmit
        };

    private static bool IsWiredRoutineName(string name)
    {
        return _gateBypassNames.Contains(item: name);
    }

    /// <summary>
    /// Returns false when this concrete instantiation does not actually have the wired
    /// routine. Example: `Array[T, N].eq` is declared `needs T obeys Equatable` — for
    /// `T = X` (not equatable), the routine does not exist on this owner. Body
    /// emission must skip it so derived companions (`ne`, `notcontains`) don't reference
    /// a missing symbol downstream in codegen. The actual protocol-to-wired-routine map
    /// lives in <see cref="TypeRegistry"/> (single source of truth).
    /// </summary>
    private bool RoutineApplicableToConcreteOwner(RoutineInfo routine, TypeSymbol owner)
    {
        return ctx.Registry.TypeHasWiredRoutine(type: owner, wiredName: routine.Name);
    }

    /// <summary>OCCURS-CHECK: true when <paramref name="t"/> transitively CONTAINS a generic parameter named
    /// <paramref name="paramName"/> — i.e. a substitution <c>paramName := t</c> is CYCLIC (substituting the
    /// param reintroduces the param, so re-resolution never terminates). Created by an inference/unification
    /// that bound a param to a type containing it with no occurs-check — the itertools lambda-predicate case
    /// binds the element `T := Routine[(T,), Bool]`, and later `ResolveType` follows `T → Routine[(T,),Bool] →
    /// T → …` forever, crashing the builder with a StackOverflow.</summary>
    private static bool TypeContainsParam(TypeSymbol t, string paramName)
    {
        if (t is GenericParameterTypeSymbol gp)
        {
            return gp.Name == paramName;
        }

        if (t is RoutineTypeSymbol rt)
        {
            return rt.ParameterTypes.Any(predicate: p => TypeContainsParam(t: p, paramName: paramName)) ||
                   rt.ReturnType is { } ret && TypeContainsParam(t: ret, paramName: paramName);
        }

        return t.TypeArguments is { } args &&
               args.Any(predicate: a => TypeContainsParam(t: a, paramName: paramName));
    }

    private void ProcessResolvedMemberRoutineGenericRoutines()
    {
        var processed = new HashSet<string>(comparer: StringComparer.Ordinal);
        bool discoveredNew;
        do
        {
            discoveredNew = false;
            foreach (RoutineInfo resolvedRoutine in ctx.Registry
                                                       .GetAllRoutineResolutions()
                                                       .ToList())
            {
                if (!processed.Add(item: resolvedRoutine.RegistryKey))
                {
                    continue;
                }

                discoveredNew = true;
                ProcessResolvedMemberRoutineGenericRoutine(resolvedRoutine: resolvedRoutine);
            }
        } while (discoveredNew);
    }

    /// <summary>
    /// Builds and stores the monomorphized body for a single resolved memberRoutine-generic
    /// resolution, applying the gate/reachability checks and the various body-source fallbacks.
    /// </summary>
    private void ProcessResolvedMemberRoutineGenericRoutine(RoutineInfo resolvedRoutine)
    {
        if (!ShouldProcessResolvedRoutine(resolvedRoutine: resolvedRoutine))
        {
            return;
        }

        // Routine types are structural — no per-type wired handler in WiredRoutinePass — so the
        // universal represent/diagnose template would field-walk their (empty) member set into a
        // bogus `Routine()`. Emit the SIGNATURE (the routine type's own name, e.g.
        // `Routine[(S32,), S32]`) directly: no parens, no field walk.
        if (resolvedRoutine.OwnerType is RoutineTypeSymbol routineOwner &&
            resolvedRoutine.Name is RuntimeContract.Display.Represent
                or RuntimeContract.Display.Diagnose)
        {
            EmitRoutineTypeSignatureBody(resolvedRoutine: resolvedRoutine,
                routineOwner: routineOwner);
            return;
        }

        Dictionary<string, TypeSymbol> typeSubs =
            BuildResolvedRoutineTypeSubstitutions(resolvedRoutine: resolvedRoutine);
        if (typeSubs.Count == 0 || typeSubs.Values.Any(predicate: HasUnresolvedTypeArgs))
        {
            return;
        }

        // OCCURS-CHECK. A universal derive (`T.roam_trace()` / `roam_free` / `cyclic_visit`) materialized onto
        // a routine-type owner such as `Routine[(T,), Bool]` binds its owner param `T` to that owner — but the
        // owner's OWN inner parameter is ALSO named `T` (a slot-vs-name collision), so the binding becomes the
        // self-referential `T := Routine[(T,), Bool]`. Substituting it re-introduces `T` without end, and a
        // later `ResolveType` follows `T → Routine[(T,),Bool] → T → …` forever, crashing the builder with a
        // StackOverflow. Such an instantiation is an infinite type — not a real reachable instance — so drop it.
        if (typeSubs.Any(predicate: kv => TypeContainsParam(t: kv.Value, paramName: kv.Key)))
        {
            return;
        }

        // Variant routines (iterator `try_emit`; and try_/check_/lookup_ of any failable) have NO source AST
        // under their own name — their real body is built from the ORIGINAL failable routine's AST via the
        // path-2 transform (BuildVariantBody → FindInStdlib(emit!) + ErrorHandlingVariantPass.TransformBody).
        if (TryBuildAndStoreVariantBody(resolvedRoutine: resolvedRoutine, typeSubs: typeSubs))
        {
            return;
        }

        EmitResolvedRoutineBodyFromAst(resolvedRoutine: resolvedRoutine, typeSubs: typeSubs);
    }

    /// <summary>
    /// Returns false when this resolved routine should be skipped (already built, not live, protocol-owned,
    /// or not applicable to its concrete owner).
    /// </summary>
    private bool ShouldProcessResolvedRoutine(RoutineInfo resolvedRoutine)
    {
        if (resolvedRoutine.GenericDefinition == null ||
            ctx.InstantiatedGenericBodies.ContainsKey(key: resolvedRoutine.RegistryKey) ||
            ctx.VariantBodies.ContainsKey(key: resolvedRoutine.RegistryKey) ||
            resolvedRoutine.OwnerType is ProtocolTypeSymbol)
        {
            return false;
        }

        // Reachability gate: SA's RoutineResolutions index includes routines that were
        // type-checked during analysis but never reached from program entry points.
        if (ctx.LiveRoutineKeys.Count > 0 &&
            !ctx.LiveRoutineKeys.Contains(item: resolvedRoutine.RegistryKey) &&
            !IsWiredRoutineName(name: resolvedRoutine.Name))
        {
            return false;
        }

        if (resolvedRoutine.OwnerType is { } resolvedOwner &&
            !RoutineApplicableToConcreteOwner(routine: resolvedRoutine, owner: resolvedOwner))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Attempts to build and store the monomorphized body for a variant routine (try_/check_/lookup_/try_emit).
    /// Returns true if the variant body was found and stored.
    /// </summary>
    private bool TryBuildAndStoreVariantBody(RoutineInfo resolvedRoutine,
        Dictionary<string, TypeSymbol> typeSubs)
    {
        if (resolvedRoutine.GenericDefinition?.OriginalName == null ||
            resolvedRoutine.GenericDefinition.OwnerType is not { } variantGenDefOwner)
        {
            return false;
        }

        MonomorphizedBody? variantBodyBuilt = BuildVariantBody(
            genMemberRoutine: resolvedRoutine.GenericDefinition,
            concreteInfo: resolvedRoutine,
            genDef: variantGenDefOwner,
            typeSubs: typeSubs,
            stringSubs: typeSubs.ToDictionary(keySelector: kv => kv.Key,
                elementSelector: kv => kv.Value.FullName));
        if (variantBodyBuilt == null)
        {
            return false;
        }

        ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] = variantBodyBuilt;
        return true;
    }

    /// <summary>
    /// Emits the monomorphized body for a resolved routine by finding its AST declaration in stdlib
    /// and rewriting it with type substitutions. Falls back to variant-body sources when no AST is found.
    /// </summary>
    private void EmitResolvedRoutineBodyFromAst(RoutineInfo resolvedRoutine,
        Dictionary<string, TypeSymbol> typeSubs)
    {
        string astName = BuildAstNameForResolvedRoutine(resolvedRoutine: resolvedRoutine);
        RoutineDeclaration? astDecl = FindInStdlib(genericAstName: astName,
            expectedParamCount: resolvedRoutine.GenericDefinition!.Parameters.Count,
            typeSubs: typeSubs,
            expectedParamNames: resolvedRoutine.GenericDefinition
                                               .Parameters
                                               .Select(selector: static p => p.Name)
                                               .ToList(),
            expectedParamTypeNames: resolvedRoutine.GenericDefinition
                                                   .Parameters
                                                   .Select(selector: static p => p.Type?.Name)
                                                   .ToList(),
            expectedOwnerModule: resolvedRoutine.OwnerType?.FullName is { } ownerFn
                ? TypeSymbol.StripTypeArgs(name: ownerFn)
                : null);
        if (astDecl == null)
        {
            EmitResolvedRoutineBodyFromVariantFallback(resolvedRoutine: resolvedRoutine,
                typeSubs: typeSubs);
            return;
        }

        var stringSubs = typeSubs.ToDictionary(keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.FullName);

        RoutineDeclaration rewrittenDecl = GenericAstRewriter.Rewrite(routine: astDecl,
            subs: stringSubs,
            typeSubs: typeSubs,
            registry: ctx.Registry,
            enclosingRoutine: resolvedRoutine);

        ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] = new MonomorphizedBody(
            Ast: rewrittenDecl,
            Info: resolvedRoutine,
            TypeSubs: typeSubs,
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false);
    }

    /// <summary>
    /// Emits the display body for a structural routine-type represent/diagnose as the routine type's
    /// own name literal (no field walk).
    /// </summary>
    private void EmitRoutineTypeSignatureBody(RoutineInfo resolvedRoutine,
        RoutineTypeSymbol routineOwner)
    {
        SourceLocation loc = resolvedRoutine.Location ??
                             new SourceLocation(FileName: "",
                                 Line: 0,
                                 Column: 0,
                                 Position: 0);
        var sigBody = new ReturnStatement(
            Value: new LiteralExpression(Value: routineOwner.Name,
                LiteralType: Tokenizer.TokenType.TextLiteral,
                Location: loc) { ResolvedType = ctx.Registry.LookupType(name: "Text") },
            Location: loc);
        ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] = new MonomorphizedBody(
            Ast: WrapInShellDecl(name: resolvedRoutine.Name, body: sigBody, info: resolvedRoutine),
            Info: resolvedRoutine,
            TypeSubs: new Dictionary<string, TypeSymbol>(),
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: true);
    }

    /// <summary>
    /// Fallback body sources for a resolved memberRoutine whose concrete AST decl was not found in
    /// stdlib: (1) a generic-def variant body keyed by the generic-def RegistryKey; (2) a two-level
    /// generic wrapper forwarder body keyed by GenericDefinition.GenericDefinition; (3) a pure-
    /// synthesized sentinel body. Stores whichever matches first.
    /// </summary>
    private void EmitResolvedRoutineBodyFromVariantFallback(RoutineInfo resolvedRoutine,
        Dictionary<string, TypeSymbol> typeSubs)
    {
        // Check if the generic definition has a synthesized body in VariantBodies.
        // ProcessConcreteType->BuildBody checks genMemberRoutine.RegistryKey (the generic def key),
        // but this path only checked resolvedRoutine.RegistryKey (the concrete key).
        // Example: List[T].eq body is stored under "Core.List[T].eq#Core.List[T]",
        // but resolvedRoutine.RegistryKey is "Core.List[Core.Byte].eq#Core.List[Core.Byte]".
        string genDefRoutineKey = resolvedRoutine.GenericDefinition!.RegistryKey;
        if (ctx.VariantBodies.TryGetValue(key: genDefRoutineKey,
                value: out Statement? defVariantBody))
        {
            var stringSubs2 = typeSubs.ToDictionary(keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.FullName);
            Statement rewritten = GenericAstRewriter.RewriteStatement(stmt: defVariantBody,
                subs: stringSubs2,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: resolvedRoutine);
            ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] =
                new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: resolvedRoutine.Name,
                        body: rewritten,
                        info: resolvedRoutine),
                    Info: resolvedRoutine,
                    TypeSubs: typeSubs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);
            return;
        }

        // Two-level generic wrapper forwarder: the generic-def body is stored under
        // GenericDefinition.GenericDefinition (the T forwarder), not under
        // GenericDefinition (the Text forwarder with memberRoutine-level generic I).
        // typeSubs already contains both T->Text (from owner) and I->U64 (from TypeArguments).
        string? genDefGenDefKey = resolvedRoutine.GenericDefinition.GenericDefinition?.RegistryKey;
        if (genDefGenDefKey != null &&
            resolvedRoutine.GenericDefinition.WrapperForwarderInnerMemberRoutine != null &&
            ctx.VariantBodies.TryGetValue(key: genDefGenDefKey,
                value: out Statement? genDefGenDefBody))
        {
            var stringSubs3 = typeSubs.ToDictionary(keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.FullName);
            Statement rewritten3 = GenericAstRewriter.RewriteStatement(stmt: genDefGenDefBody,
                subs: stringSubs3,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: resolvedRoutine);
            ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] =
                new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: resolvedRoutine.Name,
                        body: rewritten3,
                        info: resolvedRoutine),
                    Info: resolvedRoutine,
                    TypeSubs: typeSubs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);
            return;
        }

        // Pure-synthesized resolved routines have no source AST. Add a sentinel
        // MonomorphizedBody so EmitFromInstantiatedGenericBodies picks them up;
        // the body comes from WiredRoutinePass via ctx.VariantBodies.
        if (resolvedRoutine.IsSynthesized)
        {
            SourceLocation loc = resolvedRoutine.Location ??
                                 new SourceLocation(FileName: "",
                                     Line: 0,
                                     Column: 0,
                                     Position: 0);
            ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] =
                new MonomorphizedBody(
                    Ast: WrapInShellDecl(name: resolvedRoutine.Name,
                        body: new BlockStatement(Statements: [], Location: loc),
                        info: resolvedRoutine),
                    Info: resolvedRoutine,
                    TypeSubs: typeSubs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);
        }
    }

    /// <summary>
    /// Emits generic-definition variant bodies for BuilderQuery routines (e.g. member_variable_count,
    /// type_name, is_generic) directly for the generic def owner. These bodies are safe to emit without
    /// type substitution because they return fixed literals and never reference the generic type parameter.
    ///
    /// Wrapper forwarders (e.g. Hijacked[T].type_name) call the inner type's memberRoutine as T.MemberRoutine().
    /// When T is a generic def (e.g. BTreeDictNode[K,V]), the LLVM callee name is the generic def's
    /// mangled name (e.g. Collections.BTreeDictNode.type_name). This pass ensures that name has a
    /// definition so the linker does not fail.
    /// </summary>
    private void EmitGenericDefBuilderQueryBodies()
    {
        foreach (TypeSymbol type in ctx.Registry.GetTypesWithMemberRoutines())
        {
            if (!type.IsGenericDefinition)
            {
                continue;
            }

            // Skip wrapper types: their concrete instances are handled by ProcessConcreteType
            // (which substitutes T with the concrete inner type). Emitting the wrapper generic-def
            // version would attempt to lower a body that still contains unresolved T references
            // (e.g. the as_entity() call), causing Phase B codegen failures.
            if (type is WrapperTypeSymbol)
            {
                continue;
            }

            foreach (RoutineInfo routine in ctx.Registry.GetMemberRoutinesForType(type: type))
            {
                EmitGenericDefBuilderQueryBody(routine: routine);
            }
        }
    }

    /// <summary>
    /// Emits the generic-definition variant body for a single BuilderQuery routine, if it has one
    /// and hasn't already been emitted.
    /// </summary>
    private void EmitGenericDefBuilderQueryBody(RoutineInfo routine)
    {
        if (!BuilderInfoProvider.IsBuilderQueryRoutine(name: routine.Name))
        {
            return;
        }

        string key = routine.RegistryKey;
        if (ctx.InstantiatedGenericBodies.ContainsKey(key: key))
        {
            return;
        }

        if (!ctx.VariantBodies.TryGetValue(key: key, value: out Statement? body))
        {
            return;
        }

        ctx.InstantiatedGenericBodies[key: key] = new MonomorphizedBody(
            Ast: WrapInShellDecl(name: routine.Name, body: body, info: routine),
            Info: routine,
            TypeSubs: new Dictionary<string, TypeSymbol>(),
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: true);
    }

    // Body construction

    /// <summary>The base wired derives that MUST resolve to a CONCRETE per-type implementation — never to
    /// the abstract protocol member. If a monomorphized body still calls the abstract, the receiver type
    /// obeys the protocol but has no concrete derive/impl; that must blow up LOUDLY here (pre-codegen),
    /// naming the offending routine, rather than silently degrading to an unresolved abstract symbol.</summary>
    private static readonly HashSet<string> _concreteRequiredDerives =
        new(comparer: StringComparer.Ordinal)
        {
            "assign",
            "duplicate",
            "eq",
            "ne",
            "cmp",
            "hash"
        };

    /// <summary>
    /// After monomorphization, no call in a CONCRETE routine body may remain resolved to an ABSTRACT
    /// protocol member (owner is a <see cref="ProtocolTypeSymbol"/>) for the derive family — the receiver is
    /// concrete, so it must resolve to that type's own derive. A leftover abstract call means the type
    /// "obeys P" but never got P's derive (e.g. a variant that obeys Copyable whose `copy` was never
    /// synthesized). Throw with the enclosing routine so the conformance/derive gap is unmissable.
    /// </summary>
    private static bool BodyHasAbstractDeriveCall(MonomorphizedBody body, out string member)
    {
        string? found = null;
        AstWalker.WalkExpressions(root: body.Ast,
            visit: expr =>
            {
                if (found != null)
                {
                    return;
                }

                RoutineInfo? rr = (expr as CallExpression)?.ResolvedRoutine;
                if (rr?.OwnerType is ProtocolTypeSymbol proto &&
                    _concreteRequiredDerives.Contains(item: rr.Name))
                {
                    found = $"{proto.Name}.{rr.Name}()";
                }
            });
        member = found ?? "";
        return found != null;
    }

    private MonomorphizedBody? BuildBody(RoutineInfo genMemberRoutine, RoutineInfo concreteInfo,
        TypeSymbol genDef, Dictionary<string, TypeSymbol> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // Variant memberRoutines (try_/check_/lookup_)
        // These have OriginalName pointing back to the failable source routine.
        // Look for the body of that source routine (not the variant name).
        if (genMemberRoutine.OriginalName != null)
        {
            return BuildVariantBody(genMemberRoutine: genMemberRoutine,
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs);
        }

        // If the concrete memberRoutine still has unresolved memberRoutine-level generic parameters
        // (e.g. Text.getitem! where index: I is still GenericParameterTypeSymbol),
        // skip this body here. ProcessResolvedMemberRoutineGenericRoutines handles per-concrete-
        // index-type specialization once OperatorLoweringPass registers the resolutions.
        if (concreteInfo.Parameters.Any(predicate: static p => p.Type is GenericParameterTypeSymbol))
        {
            return null;
        }

        // WiredRoutinePass / ErrorHandlingVariantPass body in VariantBodies
        bool skippedVariantBodyForStdlibFallback = false;
        if (ctx.VariantBodies.TryGetValue(key: genMemberRoutine.RegistryKey,
                value: out Statement? variantBody))
        {
            MonomorphizedBody? variantMono = BuildVariantBodyFromVariantBodies(
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs,
                variantBody: variantBody,
                skippedForStdlibFallback: out skippedVariantBodyForStdlibFallback);
            if (variantMono != null)
            {
                return variantMono;
            }
        }

        // Pure synthesized: no AST body available.
        // If we skipped a variant body to force stdlib fallback (wrapper with non-entity T),
        // don't stop here — fall through to FindInStdlib below.
        if (genMemberRoutine.IsSynthesized && !skippedVariantBodyForStdlibFallback)
        {
            return null;
        }

        // Regular memberRoutine: search stdlib + user program ASTs
        string astName = BuildAstName(genDef: genDef, routineName: genMemberRoutine.Name);
        var paramNames = genMemberRoutine.Parameters
                                         .Select(selector: static p => p.Name)
                                         .ToList();
        var paramTypeNames = genMemberRoutine.Parameters
                                             .Select(selector: static p => p.Type?.Name)
                                             .ToList();
        RoutineDeclaration? astDecl = FindInStdlib(genericAstName: astName,
            expectedParamCount: genMemberRoutine.Parameters.Count,
            typeSubs: typeSubs,
            expectedParamNames: paramNames,
            expectedParamTypeNames: paramTypeNames,
            expectedOwnerModule: genDef.FullName is { } gdFn
                ? TypeSymbol.StripTypeArgs(name: gdFn)
                : null,
            expectedOwnerRealm: genDef.Realm);

        if (astDecl == null)
        {
            return null;
        }

        RoutineDeclaration rewrittenDecl = GenericAstRewriter.Rewrite(routine: astDecl,
            subs: stringSubs,
            typeSubs: typeSubs,
            registry: ctx.Registry,
            enclosingRoutine: concreteInfo);

        return new MonomorphizedBody(Ast: rewrittenDecl,
            Info: concreteInfo,
            TypeSubs: typeSubs,
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false);
    }

    /// <summary>
    /// Builds a monomorphized body from a WiredRoutinePass / ErrorHandlingVariantPass body found in
    /// VariantBodies. Returns null (and sets <paramref name="skippedForStdlibFallback"/> to true) when
    /// the body is a wrapper forwarder over a non-entity inner type, so the caller falls through to the
    /// stdlib AST lookup instead.
    /// </summary>
    private MonomorphizedBody? BuildVariantBodyFromVariantBodies(RoutineInfo concreteInfo,
        TypeSymbol genDef, Dictionary<string, TypeSymbol> typeSubs,
        Dictionary<string, string> stringSubs, Statement variantBody,
        out bool skippedForStdlibFallback)
    {
        skippedForStdlibFallback = false;
        // Wrapper forwarder bodies call as_entity()/extract() on the inner type, which requires
        // T is EntityType (entity layout). For wrapper types (Hijacked, Retained, Owned, etc.)
        // with a non-entity concrete T, skip the variant body (which may be a forwarder) and
        // fall through to the stdlib AST lookup below — the RF source handles all T without
        // requiring entity layout.
        // NOTE: both the synthesized forwarder RoutineInfo and the stdlib RoutineInfo for
        // the same wrapper memberRoutine share the same RegistryKey, so we check by wrapper type
        // name rather than genMemberRoutine.WrapperForwarderInnerMemberRoutine.
        bool isWrapperWithNonEntityInner =
            WrapperForwardingPass.WrapperTypeNames.Contains(item: genDef.Name) &&
            typeSubs.Count > 0 && typeSubs.Values.First() is not EntityTypeSymbol;
        if (!isWrapperWithNonEntityInner)
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(stmt: variantBody,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: concreteInfo);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: concreteInfo.Name, body: rewritten, info: concreteInfo),
                Info: concreteInfo,
                TypeSubs: typeSubs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized:
                true); // treat as synthesized so codegen uses EmitSynthesizedBodyFromAst
        }

        skippedForStdlibFallback = true;
        return null;
    }

    private MonomorphizedBody? BuildVariantBody(RoutineInfo genMemberRoutine,
        RoutineInfo concreteInfo, TypeSymbol genDef, Dictionary<string, TypeSymbol> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // Compute carrier-unwrapping metadata for instantiated variant bodies.
        FailableVariant? variantStatus = null;
        TypeSymbol? variantInnerType = null;

        if (concreteInfo.ReturnType?.TypeArguments is { Count: > 0 })
        {
            string? baseName = GetGenericBaseName(type: concreteInfo.ReturnType);
            if (baseName == "Lookup")
            {
                variantStatus = FailableVariant.Lookup;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[index: 0];
            }
            else if (baseName == "Result")
            {
                variantStatus = FailableVariant.Check;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[index: 0];
            }
        }

        // TryBool variants return Bool (no type args) — detect via the failable-variant kind.
        if (variantStatus == null && concreteInfo.FailableVariant == FailableVariant.TryBool)
        {
            variantStatus = FailableVariant.TryBool;
        }

        // When there is a carrier, the RoutineInfo.ReturnType is the inner type T,
        // not the carrier.
        RoutineInfo emitInfo = concreteInfo;
        if (variantStatus != null && variantInnerType != null)
        {
            emitInfo = new RoutineInfo(name: concreteInfo.Name)
            {
                Kind = concreteInfo.Kind,
                OwnerType = concreteInfo.OwnerType,
                Parameters = concreteInfo.Parameters,
                ReturnType = variantInnerType,
                IsFailable = concreteInfo.IsFailable,
                DeclaredMutation = concreteInfo.DeclaredMutation,
                MutationCategory = concreteInfo.MutationCategory,
                Visibility = concreteInfo.Visibility,
                Location = concreteInfo.Location,
                Module = concreteInfo.Module,
                Annotations = concreteInfo.Annotations,
                CallingConvention = concreteInfo.CallingConvention,
                IsVariadic = concreteInfo.IsVariadic,
                IsDangerous = concreteInfo.IsDangerous,
                AsyncStatus = concreteInfo.AsyncStatus,
                FailableVariant = variantStatus.Value,
                OriginalName = concreteInfo.OriginalName
            };
        }

        // Pre-built variant body from ErrorHandlingVariantPass (keyed by generic memberRoutine RegistryKey)
        if (ctx.VariantBodies.TryGetValue(key: genMemberRoutine.RegistryKey,
                value: out Statement? prebuiltVariant))
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(stmt: prebuiltVariant,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: emitInfo);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: emitInfo.Name, body: rewritten, info: emitInfo),
                Info: emitInfo,
                TypeSubs: typeSubs,
                VariantStatus: variantStatus,
                VariantInnerType: variantInnerType,
                IsSynthesized: false);
        }

        // Fallback: search for the original failable routine's AST and compile it as a variant
        string fallbackAstName =
            BuildAstName(genDef: genDef, routineName: genMemberRoutine.OriginalName!);
        RoutineDeclaration? astDecl = FindInStdlib(genericAstName: fallbackAstName,
            expectedParamCount: genMemberRoutine.Parameters.Count);

        if (astDecl == null)
        {
            return null;
        }

        RoutineDeclaration rewrittenDecl = GenericAstRewriter.Rewrite(routine: astDecl,
            subs: stringSubs,
            typeSubs: typeSubs,
            registry: ctx.Registry,
            enclosingRoutine: emitInfo);

        // The fallback body is the original failable AST (ReturnStatement nodes, not
        // VariantReturnStatement). Transform it so codegen emits carrier construction.
        //
        // Check/Lookup/TryBool are driven by variantStatus. The try_ (Maybe) variant leaves
        // variantStatus null — codegen natively wraps top-level `return`->Some and `absent`->None
        // — but a Maybe variant whose source uses a failable call in NON-tail position (e.g.
        // `var item = src.emit!()` in EnumerateEmitter) needs that inner call routed through its
        // own try_ variant; otherwise the raw `!` call hard-crashes at exhaustion. Run the
        // Try-kind transform for Maybe returns so TransformBlockStatements can splice in that
        // propagation. (ListEmitter etc. have no such inner call, so the transform is a no-op for
        // them beyond the equivalent VariantReturn rewrite codegen already understands.)
        ErrorHandlingVariantKind? fallbackKind = variantStatus switch
        {
            FailableVariant.Check => ErrorHandlingVariantKind.Check,
            FailableVariant.Lookup => ErrorHandlingVariantKind.Lookup,
            FailableVariant.TryBool => ErrorHandlingVariantKind.TryBool,
            _ when emitInfo.ReturnType != null &&
                   GetGenericBaseName(type: emitInfo.ReturnType) == "Maybe" =>
                ErrorHandlingVariantKind.Try,
            _ => null
        };
        if (fallbackKind != null)
        {
            Statement transformed = ErrorHandlingVariantPass.TransformBody(
                body: rewrittenDecl.Body,
                kind: fallbackKind.Value,
                rewriter: ErrorHandlingVariantPass.MakeNextVariantRewriter(registry: ctx.Registry),
                registry: ctx.Registry,
                nextOnlyPropagation: true);
            rewrittenDecl = rewrittenDecl with { Body = transformed };
        }

        return new MonomorphizedBody(Ast: rewrittenDecl,
            Info: emitInfo,
            TypeSubs: typeSubs,
            VariantStatus: variantStatus,
            VariantInnerType: variantInnerType,
            IsSynthesized: false);
    }
    // Helpers

    /// <summary>
    /// Builds a concrete <see cref="RoutineInfo"/> for an instantiated generic body by
    /// substituting owner and memberRoutine type parameters.
    /// </summary>
    private RoutineInfo? BuildConcreteRoutineInfo(RoutineInfo genMemberRoutine,
        TypeSymbol concreteOwner, Dictionary<string, TypeSymbol> typeSubs)
    {
        // Wrapper-forwarder special case: the generic forwarder's signature came from the
        // inner-generic-def memberRoutine (e.g. List[T].getitem! returning T). Naive name-based
        // substitution using the wrapper's typeSubs would map List[T]'s T to the wrapper's
        // T-substitution (the whole inner type), not the inner's own T. Re-resolve the
        // signature against the concrete inner memberRoutine instead.
        if (genMemberRoutine is
            {
                IsSynthesized: true, WrapperForwarderInnerMemberRoutine: { } innerGenMemberRoutine
            } && concreteOwner.TypeArguments is { Count: 1 } wrapperArgs)
        {
            TypeSymbol concreteInner = wrapperArgs[index: 0];
            RoutineInfo? concreteInnerMemberRoutine = ctx.Registry.LookupMemberRoutine(
                type: concreteInner,
                memberRoutineName: innerGenMemberRoutine.Name,
                isFailable: innerGenMemberRoutine.IsFailable);
            if (concreteInnerMemberRoutine != null)
            {
                var fwdParams = concreteInnerMemberRoutine.Parameters
                                                          .Select(selector: p => p.Name == "me"
                                                               ? p.WithSubstitutedType(
                                                                   newType: concreteOwner)
                                                               : p)
                                                          .ToList();
                return new RoutineInfo(name: genMemberRoutine.Name)
                {
                    Kind = genMemberRoutine.Kind,
                    OwnerType = concreteOwner,
                    Parameters = fwdParams,
                    ReturnType = concreteInnerMemberRoutine.ReturnType,
                    IsFailable = genMemberRoutine.IsFailable,
                    DeclaredMutation = genMemberRoutine.DeclaredMutation,
                    MutationCategory = genMemberRoutine.MutationCategory,
                    Visibility = genMemberRoutine.Visibility,
                    Location = genMemberRoutine.Location,
                    Module = genMemberRoutine.Module,
                    Annotations = genMemberRoutine.Annotations,
                    CallingConvention = genMemberRoutine.CallingConvention,
                    IsVariadic = genMemberRoutine.IsVariadic,
                    IsDangerous = genMemberRoutine.IsDangerous,
                    IsSynthesized = true,
                    WrapperForwarderInnerMemberRoutine = concreteInnerMemberRoutine,
                    WrapperForwarderInnerGenericDef =
                        genMemberRoutine.WrapperForwarderInnerGenericDef,
                    AsyncStatus = genMemberRoutine.AsyncStatus,
                    FailableVariant = genMemberRoutine.FailableVariant,
                    OriginalName = genMemberRoutine.OriginalName
                };
            }

            // The concrete inner type does not have this forwarded memberRoutine — skip it.
            return null;
        }

        var resolvedParams = genMemberRoutine.Parameters
                                             .Select(selector: p =>
                                              {
                                                  TypeSymbol resolved =
                                                      ResolveSubstitutedType(type: p.Type,
                                                          subs: typeSubs);
                                                  // Final sweep: if ResolveSubstitutedType couldn't resolve a generic parameter
                                                  // (e.g., TryGetResolution returned null for a wrapper type), fall back to a direct
                                                  // name-based lookup in typeSubs. Post-GMP there must be no GenericParameterTypeSymbol.
                                                  if (resolved is GenericParameterTypeSymbol gp &&
                                                      typeSubs.TryGetValue(key: gp.Name,
                                                          value: out TypeSymbol? directSub))
                                                  {
                                                      resolved = directSub;
                                                  }

                                                  return p.WithSubstitutedType(newType: resolved);
                                              })
                                             .ToList();

        TypeSymbol? resolvedReturn = genMemberRoutine.ReturnType != null
            ? ResolveSubstitutedType(type: genMemberRoutine.ReturnType, subs: typeSubs)
            : null;
        if (resolvedReturn is GenericParameterTypeSymbol retGp &&
            typeSubs.TryGetValue(key: retGp.Name, value: out TypeSymbol? directRetSub))
        {
            resolvedReturn = directRetSub;
        }

        return new RoutineInfo(name: genMemberRoutine.Name)
        {
            Kind = genMemberRoutine.Kind,
            OwnerType = concreteOwner,
            Parameters = resolvedParams,
            ReturnType = resolvedReturn,
            IsFailable = genMemberRoutine.IsFailable,
            DeclaredMutation = genMemberRoutine.DeclaredMutation,
            MutationCategory = genMemberRoutine.MutationCategory,
            Visibility = genMemberRoutine.Visibility,
            Location = genMemberRoutine.Location,
            Module = genMemberRoutine.Module,
            Annotations = genMemberRoutine.Annotations,
            CallingConvention = genMemberRoutine.CallingConvention,
            IsVariadic = genMemberRoutine.IsVariadic,
            IsDangerous = genMemberRoutine.IsDangerous,
            AsyncStatus = genMemberRoutine.AsyncStatus,
            FailableVariant = genMemberRoutine.FailableVariant,
            OriginalName = genMemberRoutine.OriginalName,
            // Carry the receiver-handle type through monomorphization: a Suflae entity's `me` is the
            // Roamed[E] handle (MeType), and its owner param must be substituted (Roamed[Box[T]] →
            // Roamed[Box[S64]]) so codegen binds `me` to the handle and deref's through the controller
            // instead of reading the RC refcount off the bare entity.
            MeType = genMemberRoutine.MeType != null
                ? ResolveSubstitutedType(type: genMemberRoutine.MeType, subs: typeSubs)
                : null
        };
    }

    /// <summary>
    /// Resolves a type by applying generic substitutions.
    /// Also converts <see cref="WrapperTypeSymbol"/> to the concrete <see cref="RecordTypeSymbol"/>
    /// so memberRoutine lookup and LLVM name mangling use the correct module-qualified type name.
    /// </summary>
    /// <summary>The inner X of a marker borrow protocol <c>Accessing[X]</c>/<c>Controlling[X]</c>, or null.
    /// A marker is ABI-transparent to its inner, so during monomorphization it is replaced by X — no marker
    /// protocol survives into a concrete signature, and the backend never sees a protocol-typed param.</summary>
    private static TypeSymbol? MarkerInner(TypeSymbol t)
    {
        return t is ProtocolTypeSymbol { TypeArguments: [{ } inner] } p &&
               RuntimeContract.IsMarkerProtocol(baseName: (p.GenericDefinition ?? p).BareName)
            ? inner
            : null;
    }

    private TypeSymbol ResolveSubstitutedType(TypeSymbol type, Dictionary<string, TypeSymbol> subs)
    {
        // A marker borrow protocol (directly, or reached through a substitution below) collapses to its
        // ABI-transparent inner — recurse so a still-generic inner is resolved too.
        if (MarkerInner(t: type) is { } directInner)
        {
            return ResolveSubstitutedType(type: directInner, subs: subs);
        }

        if (subs.TryGetValue(key: type.Name, value: out TypeSymbol? sub))
        {
            return MarkerInner(t: sub) is { } subbedInner
                ? ResolveSubstitutedType(type: subbedInner, subs: subs)
                : sub;
        }

        // WrapperTypeSymbol (e.g., Hijacked[T] or Hijacked[Core.Byte]) must always be resolved
        // to the real RecordTypeSymbol so LookupMemberRoutine and LLVM mangled names work correctly.
        // Use TryGetResolution (lookup-only) -> GMP must not grow AllConcreteGenericInstances.
        if (type is WrapperTypeSymbol wrapper)
        {
            return ResolveWrapperType(wrapper: wrapper, subs: subs);
        }

        // Tuples carry their elements in ElementTypes (not TypeArguments), so the generic-resolution
        // recursion below misses them — substitute element-wise (e.g. Tuple[U64, T] -> Tuple[U64, Text]).
        if (type is TupleTypeSymbol tuple)
        {
            return ResolveTupleType(tuple: tuple, subs: subs);
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            TypeSymbol? resolved = ResolveGenericResolutionType(type: type, subs: subs);
            if (resolved != null)
            {
                return resolved;
            }
        }

        if (type is
            {
                IsGenericDefinition: true, GenericParameters: not null, TypeArguments: null
            })
        {
            return TryResolveByGenericDefinition(type: type, subs: subs) ?? type;
        }

        return type;
    }

    /// <summary>
    /// Instantiates a generic definition by substituting each of its generic parameters from
    /// <paramref name="subs"/> or the registry, then looking up or creating the concrete instance.
    /// Returns null when not all parameters could be resolved.
    /// </summary>
    private TypeSymbol? TryResolveByGenericDefinition(TypeSymbol type,
        Dictionary<string, TypeSymbol> subs)
    {
        var typeArgs = type.GenericParameters!.Select(selector: gp =>
                                subs.TryGetValue(key: gp, value: out TypeSymbol? s)
                                    ? s
                                    : ctx.Registry.LookupType(name: gp))
                           .Where(predicate: t => t != null)
                           .ToList();
        if (typeArgs.Count != type.GenericParameters!.Count)
        {
            return null;
        }

        return ctx.Registry.TryGetResolution(genericDef: type, typeArguments: typeArgs!);
    }

    /// <summary>Resolves a wrapper type by substituting its inner type arguments and looking up the concrete instance.</summary>
    private TypeSymbol ResolveWrapperType(WrapperTypeSymbol wrapper, Dictionary<string, TypeSymbol> subs)
    {
        TypeSymbol? wrapperDef = ctx.Registry.LookupType(name: wrapper.Name);
        if (wrapperDef is { IsGenericDefinition: true } && wrapper.TypeArguments is { Count: > 0 })
        {
            var resolvedInnerArgs = wrapper.TypeArguments
                                           .Select(selector: a =>
                                                ResolveSubstitutedType(type: a, subs: subs))
                                           .ToList();
            return ctx.Registry.TryGetResolution(genericDef: wrapperDef,
                typeArguments: resolvedInnerArgs) ?? wrapper;
        }

        return wrapper;
    }

    /// <summary>Resolves a tuple type by substituting each element type.</summary>
    private TupleTypeSymbol ResolveTupleType(TupleTypeSymbol tuple, Dictionary<string, TypeSymbol> subs)
    {
        var subbedElems = tuple.ElementTypes
                               .Select(selector: e => ResolveSubstitutedType(type: e, subs: subs))
                               .ToList();
        bool tupleChanged = subbedElems.Where(predicate: (e, i) =>
                                            !ReferenceEquals(objA: e,
                                                objB: tuple.ElementTypes[index: i]))
                                       .Any();
        return tupleChanged
            ? new TupleTypeSymbol(elementTypes: subbedElems)
            : tuple;
    }

    /// <summary>
    /// Resolves a generic-resolution type by substituting each type argument and looking up or creating
    /// the concrete instance. Returns null if no substitution occurred or no generic base was found.
    /// </summary>
    private TypeSymbol? ResolveGenericResolutionType(TypeSymbol type,
        Dictionary<string, TypeSymbol> subs)
    {
        bool anySubstituted = false;
        var substitutedArgs = new List<TypeSymbol>();
        foreach (TypeSymbol arg in type.TypeArguments!)
        {
            TypeSymbol resolved = ResolveSubstitutedType(type: arg, subs: subs);
            substitutedArgs.Add(item: resolved);
            if (!ReferenceEquals(objA: resolved, objB: arg))
            {
                anySubstituted = true;
            }
        }

        if (!anySubstituted)
        {
            return null;
        }

        TypeSymbol? genericBase = GetGenericBase(type: type);
        if (genericBase == null)
        {
            return null;
        }

        TypeSymbol? alreadyResolved = ctx.Registry.TryGetResolution(
            genericDef: genericBase,
            typeArguments: substitutedArgs);
        if (alreadyResolved != null)
        {
            return alreadyResolved;
        }

        // Not yet registered — create it so the concrete signature reaches codegen
        // without unresolved GenericParameterTypeSymbol. Safe here because wrapper types
        // are intercepted above (the WrapperTypeSymbol branch) and never reach this path.
        // GetOrCreateResolution also enqueues the new type for ProcessConcreteType.
        if (substitutedArgs.All(predicate: a => a is not ErrorTypeSymbol))
        {
            return ctx.Registry.GetOrCreateResolution(genericDef: genericBase,
                typeArguments: substitutedArgs);
        }

        return null;
    }

    /// <summary>Builds the expected AST name for a routine on a generic type definition.</summary>
    private static string BuildAstName(TypeSymbol genDef, string routineName)
    {
        if (genDef.GenericParameters is { Count: > 0 })
        {
            string paramList = string.Join(separator: ", ", values: genDef.GenericParameters);
            return $"{genDef.Name}[{paramList}].{routineName}";
        }

        return $"{genDef.Name}.{routineName}";
    }

    private string BuildAstNameForResolvedRoutine(RoutineInfo resolvedRoutine)
    {
        string astName = resolvedRoutine.OwnerType != null
            ? $"{ResolvedRoutineOwnerAstName(resolvedRoutine: resolvedRoutine)}.{resolvedRoutine.Name}"
            : resolvedRoutine.Name;

        if (resolvedRoutine.GenericDefinition?.IsGenericDefinition == true)
        {
            astName += "[generic]";
        }

        return astName;
    }

    /// <summary>
    /// Computes the AST owner-name segment for a resolved routine, handling universal-owner,
    /// specialized-receiver (MeType), and concrete generic-def owner cases.
    /// </summary>
    private string ResolvedRoutineOwnerAstName(RoutineInfo resolvedRoutine)
    {
        // Specialized-receiver member (e.g. List[Agent[V]].gather!): the AST decl is indexed
        // under its receiver PATTERN "List[Agent[V]]", not "List[T]". Format MeType to match.
        if (resolvedRoutine.GenericDefinition?.MeType is { } mePat)
        {
            return FormatReceiverPatternAstName(type: mePat);
        }

        if (resolvedRoutine.GenericDefinition
                          ?.OwnerType is GenericParameterTypeSymbol universalOwner)
        {
            return universalOwner.Name;
        }

        TypeSymbol ownerType = resolvedRoutine.OwnerType!;
        TypeSymbol? ownerGenericDef = GetGenericBase(type: ownerType) ??
                                    (ownerType is WrapperTypeSymbol
                                        ? ctx.Registry.LookupType(name: ownerType.Name)
                                        : null);
        if (ownerGenericDef?.GenericParameters is { Count: > 0 } gdParams)
        {
            return $"{ownerGenericDef.Name}[{string.Join(separator: ", ", values: gdParams)}]";
        }

        if (ownerType.IsGenericDefinition &&
            ownerType.GenericParameters is { Count: > 0 } ownParams)
        {
            return $"{ownerType.Name}[{string.Join(separator: ", ", values: ownParams)}]";
        }

        return ownerType.Name;
    }

    /// <summary>Formats a specialized-receiver pattern (a memberRoutine's MeType, e.g. List[Agent[V]]) into
    /// the short AST-name form used by the routine index ("List[Agent[V]]"), stripping module
    /// prefixes so it matches how the declaration's receiver was written in source.</summary>
    private string FormatReceiverPatternAstName(TypeSymbol type)
    {
        if (type is GenericParameterTypeSymbol)
        {
            return type.Name;
        }

        // A Suflae entity member routine has `MeType = Roamed[E]` (SF slice 2 in SignatureResolver),
        // but its SOURCE receiver is the bare entity `E` — you never write `routine Roamed[Box[T]].get`.
        // Unwrap the Roamed handle so the AST-index name matches the declaration (`Box[T].get`),
        // otherwise the generic body is looked up under `Roamed[Box[T]].get`, never found, and codegen
        // falls back to a naive direct field access that reads the RC controller instead of the entity.
        if (type is RecordTypeSymbol
            {
                GenericDefinition.Name: RuntimeContract.Roamed,
                TypeArguments: [{ } roamedInner]
            })
        {
            return FormatReceiverPatternAstName(type: roamedInner);
        }

        string baseName = type.BareName;
        int dot = baseName.LastIndexOf(value: '.');
        if (dot >= 0)
        {
            baseName = baseName[(dot + 1)..];
        }

        if (type.TypeArguments is { Count: > 0 } args)
        {
            return baseName + "[" + string.Join(separator: ", ",
                values: args.Select(selector: FormatReceiverPatternAstName)) + "]";
        }

        return baseName;
    }

    /// <summary>Recursively unifies a MeType pattern (List[Agent[V]]) against the concrete owner
    /// (List[Agent[S64]]), recording memberRoutine-generic bindings (V → S64) into <paramref name="into"/>.</summary>
    private static void UnifyMePatternIntoSubs(TypeSymbol pattern, TypeSymbol concrete,
        List<string>? genericParams, Dictionary<string, TypeSymbol> into)
    {
        if (genericParams is not { Count: > 0 })
        {
            return;
        }

        if (pattern is GenericParameterTypeSymbol gp)
        {
            if (genericParams.Contains(item: gp.Name) && !into.ContainsKey(key: gp.Name) &&
                concrete is not GenericParameterTypeSymbol)
            {
                into[key: gp.Name] = concrete;
            }

            return;
        }

        if (pattern.TypeArguments is { Count: > 0 } pArgs &&
            concrete.TypeArguments is { Count: > 0 } cArgs)
        {
            for (int i = 0; i < pArgs.Count && i < cArgs.Count; i++)
            {
                UnifyMePatternIntoSubs(pattern: pArgs[index: i],
                    concrete: cArgs[index: i],
                    genericParams: genericParams,
                    into: into);
            }
        }
    }

    private Dictionary<string, TypeSymbol> BuildResolvedRoutineTypeSubstitutions(
        RoutineInfo resolvedRoutine)
    {
        var typeSubs = new Dictionary<string, TypeSymbol>();

        // (1) Specialized-receiver member: bind receiver-derived generics (V → S64) by unifying the
        // generic definition's MeType against the concrete owner.
        if (resolvedRoutine.GenericDefinition?.MeType is { } mePattern &&
            resolvedRoutine.OwnerType is { } resolvedOwnerForMe)
        {
            UnifyMePatternIntoSubs(pattern: mePattern,
                concrete: resolvedOwnerForMe,
                genericParams: resolvedRoutine.GenericDefinition.GenericParameters,
                into: typeSubs);
        }

        // (2) Universal-owner binding: map the single generic-owner parameter to the concrete owner.
        if (resolvedRoutine.GenericDefinition
                          ?.OwnerType is GenericParameterTypeSymbol universalOwner &&
            resolvedRoutine.OwnerType != null)
        {
            typeSubs[key: universalOwner.Name] = resolvedRoutine.OwnerType;
        }

        // (3) Owner type-argument bindings (including wrapper-forwarder inner-generic propagation).
        if (resolvedRoutine.OwnerType is { TypeArguments: { Count: > 0 } } ownerType)
        {
            AddOwnerTypeArgSubstitutions(typeSubs: typeSubs,
                resolvedRoutine: resolvedRoutine,
                ownerType: ownerType);
        }

        // (4) Method-generic type-argument bindings (e.g. getitem![I] where I → U64).
        if (resolvedRoutine.GenericDefinition?.GenericParameters is
                { Count: > 0 } memberRoutineParams && resolvedRoutine.TypeArguments is
                { Count: > 0 } memberRoutineTypeArgs)
        {
            AddMemberRoutineTypeArgSubstitutions(typeSubs: typeSubs,
                memberRoutineParams: memberRoutineParams,
                memberRoutineTypeArgs: memberRoutineTypeArgs);
        }

        return typeSubs;
    }

    /// <summary>
    /// Adds the owner's type-argument → generic-parameter substitutions, including propagation of a
    /// wrapper forwarder's inner-generic-def parameters (e.g. K, V from Owned[BTreeDictNode[K,V]]).
    /// </summary>
    private void AddOwnerTypeArgSubstitutions(Dictionary<string, TypeSymbol> typeSubs,
        RoutineInfo resolvedRoutine, TypeSymbol ownerType)
    {
        TypeSymbol? ownerGenericDef = GetGenericBase(type: ownerType) ??
                                    (ownerType is WrapperTypeSymbol
                                        ? ctx.Registry.LookupType(name: ownerType.Name)
                                        : null);
        if (ownerGenericDef?.GenericParameters is { Count: > 0 })
        {
            for (int i = 0;
                 i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments!.Count;
                 i++)
            {
                string paramName = ownerGenericDef.GenericParameters[index: i];
                // Don't overwrite a universal-owner mapping (e.g. T->BTreeListNode[Byte] from case 2)
                // with the owner's own type argument (e.g. T->Byte from BTreeListNode[T]).
                typeSubs.TryAdd(key: paramName, value: ownerType.TypeArguments[index: i]);
            }
        }

        // Wrapper forwarder over a generic inner type: propagate the inner type's K, V substitutions
        // so the body rewriter can resolve them when monomorphizing per concrete inner type.
        if (resolvedRoutine.WrapperForwarderInnerGenericDef is
                { GenericParameters: { Count: > 0 } } innerGenDef &&
            ownerType.TypeArguments![index: 0] is { TypeArguments: { Count: > 0 } } innerInstance)
        {
            for (int i = 0;
                 i < innerGenDef.GenericParameters.Count && i < innerInstance.TypeArguments.Count;
                 i++)
            {
                string innerParamName = innerGenDef.GenericParameters[index: i];
                if (!typeSubs.ContainsKey(key: innerParamName))
                {
                    typeSubs[key: innerParamName] = innerInstance.TypeArguments[index: i];
                }
            }
        }
    }

    /// <summary>
    /// Adds method-level generic type-argument substitutions, resolving name collisions by
    /// instantiating the already-mapped generic definition or by substituting into it.
    /// </summary>
    private void AddMemberRoutineTypeArgSubstitutions(Dictionary<string, TypeSymbol> typeSubs,
        List<string> memberRoutineParams, List<TypeSymbol> memberRoutineTypeArgs)
    {
        for (int i = 0; i < memberRoutineParams.Count && i < memberRoutineTypeArgs.Count; i++)
        {
            string pName = memberRoutineParams[index: i];
            TypeSymbol pVal = memberRoutineTypeArgs[index: i];
            if (typeSubs.TryGetValue(key: pName, value: out TypeSymbol? existingOwnerValue))
            {
                typeSubs[key: pName] = ResolveTypeArgCollision(pName: pName,
                    pVal: pVal,
                    existingOwnerValue: existingOwnerValue);
            }
            else
            {
                typeSubs[key: pName] = pVal;
            }
        }
    }

    /// <summary>
    /// Resolves a name collision between an existing owner substitution and a method-level type argument.
    /// When the existing value is a generic definition that uses the same name, instantiates it with
    /// the method arg. Otherwise substitutes the method arg into the existing mapping.
    /// </summary>
    private TypeSymbol ResolveTypeArgCollision(string pName, TypeSymbol pVal,
        TypeSymbol existingOwnerValue)
    {
        // Name collision: the owner already maps pName -> some type.
        // When existingOwnerValue is a generic definition (TypeArguments=null),
        // SubstituteType cannot recurse into it and returns it unchanged.
        // Explicitly instantiate the generic def with pVal so we get a concrete type
        // (e.g. T->BTreeListNode[T] + memberRoutine T->BuildMode -> T->BTreeListNode[BuildMode]).
        if (existingOwnerValue is
                { IsGenericDefinition: true, GenericParameters: { Count: > 0 } innerParams } &&
            innerParams.Contains(item: pName))
        {
            var newArgs = innerParams.Select(selector: p => p == pName
                                          ? pVal
                                          : (TypeSymbol)new GenericParameterTypeSymbol(name: p))
                                     .ToList();
            return newArgs.All(predicate: a => a is not GenericParameterTypeSymbol)
                ? ctx.Registry.GetOrCreateResolution(genericDef: existingOwnerValue,
                    typeArguments: newArgs)
                : existingOwnerValue;
        }

        var innerSub = new Dictionary<string, TypeSymbol> { [key: pName] = pVal };
        return RoutineInfo.SubstituteType(type: existingOwnerValue, substitution: innerSub);
    }

    /// <summary>
    /// Searches all stdlib and user program ASTs for a routine declaration matching the given name.
    /// Uses a pre-built index for O(1) name lookup.
    /// When <paramref name="typeSubs"/> is provided, routines whose generic constraints are not
    /// satisfied by the concrete type arguments are skipped -> this prevents the record-layout
    /// overload of e.g. <c>Maybe[T]</c> from being selected when T is an entity type.
    /// </summary>
    private RoutineDeclaration? FindInStdlib(string genericAstName, int expectedParamCount = -1,
        Dictionary<string, TypeSymbol>? typeSubs = null, List<string>? expectedParamNames = null,
        List<string?>? expectedParamTypeNames = null, string? expectedOwnerModule = null,
        string? expectedOwnerRealm = null)
    {
        bool requireGenericSuffix = genericAstName.EndsWith(value: "[generic]");
        string baseName = requireGenericSuffix
            ? genericAstName[..genericAstName.IndexOf(value: "[generic]",
                comparisonType: StringComparison.Ordinal)]
            : genericAstName;

        // Routine names (both index keys and the composed baseName) are bare — the failable `!` is
        // a structured flag (RoutineDeclaration.IsFailable), never part of the name — so a plain
        // lookup on the bare name already reaches failable overloads.
        if (!_routineIndex.TryGetValue(key: baseName,
                value: out List<RoutineDeclaration>? candidates))
        {
            return null;
        }

        candidates = NarrowCandidatesByModuleAndRealm(candidates: candidates,
            expectedOwnerModule: expectedOwnerModule,
            expectedOwnerRealm: expectedOwnerRealm);

        return FindBestCandidate(candidates: candidates,
            requireGenericSuffix: requireGenericSuffix,
            typeSubs: typeSubs,
            expectedParamCount: expectedParamCount,
            expectedParamNames: expectedParamNames,
            expectedParamTypeNames: expectedParamTypeNames);
    }

    /// <summary>
    /// Narrows a candidate list by module and realm, preferring exact matches while falling back to
    /// the full set when no match exists (non-overlay types or RF-only compiles).
    /// </summary>
    private static List<RoutineDeclaration> NarrowCandidatesByModuleAndRealm(
        List<RoutineDeclaration> candidates, string? expectedOwnerModule,
        string? expectedOwnerRealm)
    {
        // Module-scoped disambiguation: the same bare AST name (`List[T].add_last`) is declared in both
        // the RazorForge-realm `Core.List` and the Suflae-realm overlay `Suflae.List`. Prefer the
        // candidate whose registered owner module matches; fall back to the full set when none match.
        if (expectedOwnerModule != null)
        {
            var moduleMatched = candidates.Where(predicate: d =>
                                               d.ResolvedInfo?.OwnerType?.FullName is { } fn &&
                                               TypeSymbol.StripTypeArgs(name: fn) ==
                                               expectedOwnerModule)
                                          .ToList();
            if (moduleMatched.Count > 0)
            {
                candidates = moduleMatched;
            }
        }

        // Realm-scoped disambiguation: prefer the candidate whose owner realm matches; fall back when none.
        if (expectedOwnerRealm != null)
        {
            var realmMatched = candidates.Where(predicate: d =>
                                              (d.ResolvedInfo?.OwnerType?.Realm ?? "RF") ==
                                              expectedOwnerRealm)
                                         .ToList();
            if (realmMatched.Count > 0)
            {
                candidates = realmMatched;
            }
        }

        return candidates;
    }

    /// <summary>
    /// Picks the best matching declaration from a narrowed candidate list, preferring exact
    /// parameter-name+type matches over count-only matches over first-encountered fallbacks.
    /// </summary>
    private static RoutineDeclaration? FindBestCandidate(List<RoutineDeclaration> candidates,
        bool requireGenericSuffix, Dictionary<string, TypeSymbol>? typeSubs, int expectedParamCount,
        List<string>? expectedParamNames, List<string?>? expectedParamTypeNames)
    {
        RoutineDeclaration? countOnlyMatch = null;
        RoutineDeclaration? firstMatch = null;
        foreach (RoutineDeclaration decl in candidates)
        {
            if (requireGenericSuffix && decl.GenericParameters is not { Count: > 0 })
            {
                continue;
            }

            if (!ConstraintsSatisfied(routine: decl, subs: typeSubs))
            {
                continue;
            }

            if (expectedParamCount >= 0 && decl.Parameters.Count != expectedParamCount)
            {
                firstMatch ??= decl;
                continue;
            }

            // Count matches. If we also have expected param names, prefer the overload whose
            // param names match exactly — this disambiguates overloads like create(capacity: U64)
            // vs create(from: SortedList[T]) which both have 1 parameter.
            if (expectedParamNames != null && decl.Parameters.Count == expectedParamNames.Count)
            {
                if (MatchCandidateByParamNamesAndTypes(decl: decl,
                        expectedParamNames: expectedParamNames,
                        expectedParamTypeNames: expectedParamTypeNames))
                {
                    return decl;
                }

                countOnlyMatch ??= decl;
                continue;
            }

            return decl;
        }

        return countOnlyMatch ?? firstMatch;
    }

    /// <summary>
    /// Returns true when <paramref name="decl"/> is an exact match on both parameter names and
    /// types (when types are supplied). Param names alone don't disambiguate same-name-different-type
    /// overloads (e.g. <c>create(from: Set[T])</c> vs <c>create(from: SortedSet[T])</c>), so both
    /// checks are required.
    /// </summary>
    private static bool MatchCandidateByParamNamesAndTypes(RoutineDeclaration decl,
        List<string> expectedParamNames, List<string?>? expectedParamTypeNames)
    {
        return ParamNamesMatch(decl: decl, expectedParamNames: expectedParamNames) &&
               !ExpectedParameterTypesMismatch(decl: decl, expectedTypes: expectedParamTypeNames);
    }

    private static bool ExpectedParameterTypesMismatch(RoutineDeclaration decl,
        List<string?>? expectedTypes)
    {
        return expectedTypes != null && decl.Parameters.Count == expectedTypes.Count &&
               !ParamTypesMatch(decl: decl, expectedParamTypeNames: expectedTypes);
    }

    /// <summary>Returns true when every parameter name in <paramref name="decl"/> matches the expected list.</summary>
    private static bool ParamNamesMatch(RoutineDeclaration decl, List<string> expectedParamNames)
    {
        for (int i = 0; i < expectedParamNames.Count; i++)
        {
            if (decl.Parameters[index: i].Name != expectedParamNames[index: i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns true when every non-null expected parameter type name matches the corresponding
    /// declaration parameter type, comparing by base name (strips type args, unwraps borrow wrappers).
    /// </summary>
    private static bool ParamTypesMatch(RoutineDeclaration decl,
        List<string?> expectedParamTypeNames)
    {
        for (int i = 0; i < expectedParamTypeNames.Count; i++)
        {
            string? expected = expectedParamTypeNames[index: i];
            if (expected == null)
            {
                continue;
            }

            string? actual = decl.Parameters[index: i].Type?.Name;
            if (actual == null)
            {
                continue;
            }

            // Compare by base name (strip [T]/[K,V]) so `Set[T]` matches `Set`
            // and `SortedSet[T]` matches `SortedSet` regardless of generic-arg form.
            // Also unwrap a borrow/reference wrapper on the resolved side: a param
            // declared `from: Accessing[SortedSet[T]]` carries Type.Name "SortedSet" in
            // the AST (the wrapper is a modifier), but the resolved RoutineInfo keeps
            // the full "Accessing[SortedSet[T]]". Without unwrapping, the right overload
            // is rejected and FindInStdlib falls back to an arbitrary same-arity one.
            if (MatchableBaseName(typeName: expected) != MatchableBaseName(typeName: actual))
            {
                return false;
            }
        }

        return true;
    }

    private static string StripGenericSuffix(string typeName)
    {
        return TypeSymbol.StripTypeArgs(name: typeName);
    }

    /// <summary>Borrow/reference wrapper type names. A parameter declared with one of these
    /// (e.g. <c>from: Accessing[SortedSet[T]]</c>) reaches the AST as the bare inner type
    /// (<c>SortedSet</c>), while the resolved <see cref="RoutineInfo"/> keeps the full wrapper —
    /// so overload disambiguation must compare the inner type, not the wrapper.</summary>
    private static readonly HashSet<string> BorrowWrapperNames =
        new(comparer: StringComparer.Ordinal)
        {
            RuntimeContract.Accessing,
            RuntimeContract.Viewing,
            RuntimeContract.Controlling,
            RuntimeContract.Modifying,
            RuntimeContract.Hijacked,
            RuntimeContract.Consulting,
            RuntimeContract.Amending,
            RuntimeContract.Retained,
            RuntimeContract.Tracked,
            RuntimeContract.Guarded
        };

    /// <summary>Base type name for overload matching: strips generic args and unwraps a leading
    /// borrow/reference wrapper to its inner type (recursively), so <c>Accessing[SortedSet[T]]</c>
    /// and the AST's bare <c>SortedSet</c> compare equal.</summary>
    private static string MatchableBaseName(string typeName)
    {
        string baseName = StripGenericSuffix(typeName: typeName);
        if (BorrowWrapperNames.Contains(item: baseName) &&
            TypeSymbol.ExtractTypeArgsString(name: typeName) is { } inner)
        {
            return MatchableBaseName(typeName: inner);
        }

        return baseName;
    }

    /// <summary>
    /// Returns true if all explicit generic constraints on <paramref name="routine"/> are
    /// satisfied by the concrete type substitutions in <paramref name="subs"/>.
    /// </summary>
    private static bool ConstraintsSatisfied(RoutineDeclaration routine,
        Dictionary<string, TypeSymbol>? subs)
    {
        if (routine.GenericConstraints is not { Count: > 0 })
        {
            return true;
        }

        if (subs == null || subs.Count == 0)
        {
            return true;
        }

        foreach (GenericConstraintDeclaration c in routine.GenericConstraints)
        {
            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeSymbol? actual))
            {
                continue;
            }

            bool ok = c.ConstraintType switch
            {
                ConstraintKind.RecordType => IsRecordLike(type: actual),
                ConstraintKind.EntityType => actual is EntityTypeSymbol,
                ConstraintKind.ChoiceType => actual is ChoiceTypeSymbol,
                ConstraintKind.FlagsType => actual is FlagsTypeSymbol,
                ConstraintKind.VariantType => actual is VariantTypeSymbol,
                ConstraintKind.Crashable => actual is CrashableTypeSymbol,
                ConstraintKind.ConstGeneric => CheckStructuralConstGeneric(c: c, actual: actual),
                _ => true // Obeys/TypeEquality: trust SA
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Checks a <see cref="ConstraintKind.ConstGeneric"/> constraint that may encode a structural
    /// type-category requirement.
    /// </summary>
    private static bool CheckStructuralConstGeneric(GenericConstraintDeclaration c,
        TypeSymbol actual)
    {
        string? typeName = c.ConstraintTypes is { Count: > 0 }
            ? c.ConstraintTypes[index: 0].Name
            : null;
        return typeName switch
        {
            "RecordType" => IsRecordLike(type: actual),
            "EntityType" => actual is EntityTypeSymbol,
            "ChoiceType" => actual is ChoiceTypeSymbol,
            "FlagsType" => actual is FlagsTypeSymbol,
            "VariantType" => actual is VariantTypeSymbol,
            "Crashable" => actual is CrashableTypeSymbol,
            _ => true
        };
    }

    /// Wrappers (Owned, Retained, Hijacked, ...) are declared with `record` syntax and behave as
    /// record-category types for constraint purposes; they are tracked as WrapperTypeSymbol for layout
    /// reasons but a `needs T is RecordType` constraint must still accept them.
    private static bool IsRecordLike(TypeSymbol type)
    {
        return type is RecordTypeSymbol || type is WrapperTypeSymbol;
    }

    /// <summary>Wraps a pre-built body statement in a minimal shell RoutineDeclaration.</summary>
    private static RoutineDeclaration WrapInShellDecl(string name, Statement body,
        RoutineInfo info)
    {
        return new RoutineDeclaration(Name: name,
            Parameters: [],
            ReturnType: null,
            Body: body,
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: info.Location ?? new SourceLocation(FileName: "",
                Line: 0,
                Column: 0,
                Position: 0));
    }
    // Type helpers (no codegen dependency)

    /// <summary>
    /// Returns true when a type contains unresolved generic parameters at any nesting depth,
    /// or is itself a generic definition (free type params, no TypeArguments).
    /// Used to skip wrapper instances whose inner type still has free type params.
    /// </summary>
    private static bool HasUnresolvedTypeArgs(TypeSymbol t)
    {
        if (t is GenericParameterTypeSymbol or ErrorTypeSymbol)
        {
            return true;
        }

        if (t.IsGenericDefinition)
        {
            return true;
        }

        if (t.TypeArguments is not { Count: > 0 } args)
        {
            return false;
        }

        return args.Any(predicate: HasUnresolvedTypeArgs);
    }

    private static TypeSymbol? GetGenericBase(TypeSymbol type)
    {
        return type switch
        {
            RecordTypeSymbol { GenericDefinition: { } d } => d,
            EntityTypeSymbol { GenericDefinition: { } d } => d,
            ProtocolTypeSymbol { GenericDefinition: { } d } => d,
            _ => null
        };
    }

    private static string? GetGenericBaseName(TypeSymbol type)
    {
        return GetGenericBase(type: type)
          ?.Name;
    }

    // memberRoutine-generic call scanning

    /// <summary>
    /// Walks all built InstantiatedGenericBodies and registers memberRoutine-level generic
    /// call-site resolutions (e.g. getitem[U64]! called from List[Bytes].eq).
    /// SA only analyzes generic-def bodies, so these concrete resolutions are never in
    /// _routineResolutions. Registering them here lets ProcessResolvedMemberRoutineGenericRoutines
    /// build the bodies before codegen runs.
    /// </summary>
    private void ScanAndRegisterMemberRoutineGenericCallResolutions()
    {
        foreach ((string _, MonomorphizedBody body) in ctx.InstantiatedGenericBodies.ToList())
        {
            if (body.Ast?.Body == null)
            {
                continue;
            }

            ScanStatementForMemberRoutineGenericCalls(stmt: body.Ast.Body);
        }
    }

    private void ScanStatementForMemberRoutineGenericCalls(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                foreach (Statement s in b.Statements)
                {
                    ScanStatementForMemberRoutineGenericCalls(stmt: s);
                }

                break;
            case IfStatement ifs:
                ScanExprForMemberRoutineGenericCalls(expr: ifs.Condition);
                ScanStatementForMemberRoutineGenericCalls(stmt: ifs.ThenStatement);
                if (ifs.ElseStatement != null)
                {
                    ScanStatementForMemberRoutineGenericCalls(stmt: ifs.ElseStatement);
                }

                break;
            case WhileStatement w:
                ScanExprForMemberRoutineGenericCalls(expr: w.Condition);
                ScanStatementForMemberRoutineGenericCalls(stmt: w.Body);
                break;
            case LoopStatement loop:
                ScanStatementForMemberRoutineGenericCalls(stmt: loop.Body);
                break;
            case EachStatement f:
                ScanExprForMemberRoutineGenericCalls(expr: f.Iterable);
                ScanStatementForMemberRoutineGenericCalls(stmt: f.Body);
                break;
            case WhenStatement ws:
                ScanExprForMemberRoutineGenericCalls(expr: ws.Expression);
                foreach (WhenClause c in ws.Clauses)
                {
                    ScanStatementForMemberRoutineGenericCalls(stmt: c.Body);
                }

                break;
            case ReturnStatement { Value: { } rv }:
                ScanExprForMemberRoutineGenericCalls(expr: rv);
                break;
            case AssignmentStatement assign:
                ScanExprForMemberRoutineGenericCalls(expr: assign.Target);
                ScanExprForMemberRoutineGenericCalls(expr: assign.Value);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } vi } }:
                ScanExprForMemberRoutineGenericCalls(expr: vi);
                break;
            case ExpressionStatement es:
                ScanExprForMemberRoutineGenericCalls(expr: es.Expression);
                break;
            case DiscardStatement ds:
                ScanExprForMemberRoutineGenericCalls(expr: ds.Expression);
                break;
            case ThrowStatement ts:
                ScanExprForMemberRoutineGenericCalls(expr: ts.Error);
                break;
            case VariantReturnStatement { Value: { } vv }:
                ScanExprForMemberRoutineGenericCalls(expr: vv);
                break;
            case BecomesStatement bst:
                ScanExprForMemberRoutineGenericCalls(expr: bst.Value);
                break;
            case DangerStatement danger:
                ScanStatementForMemberRoutineGenericCalls(stmt: danger.Body);
                break;
        }
    }

    private void ScanExprForMemberRoutineGenericCalls(Expression expr)
    {
        switch (expr)
        {
            case LiteralExpression or IdentifierExpression or TypeIdExpression:
                return;

            case CallExpression call:
                ScanCallForMemberRoutineGenericCalls(call: call);
                break;
            case BinaryExpression bin:
                ScanExprForMemberRoutineGenericCalls(expr: bin.Left);
                ScanExprForMemberRoutineGenericCalls(expr: bin.Right);
                break;
            case UnaryExpression un:
                ScanExprForMemberRoutineGenericCalls(expr: un.Operand);
                break;
            case MemberExpression mem:
                ScanExprForMemberRoutineGenericCalls(expr: mem.Object);
                break;
            case NamedArgumentExpression named:
                ScanExprForMemberRoutineGenericCalls(expr: named.Value);
                break;
            case TypeConversionExpression conv:
                ScanExprForMemberRoutineGenericCalls(expr: conv.Expression);
                break;
            case IndexExpression idx:
                ScanExprForMemberRoutineGenericCalls(expr: idx.Object);
                ScanExprForMemberRoutineGenericCalls(expr: idx.Index);
                RegisterIndexAccessorGenericResolutions(idx: idx);
                break;
            case CreatorExpression creator:
                foreach ((string _, Expression v) in creator.MemberVariables)
                {
                    ScanExprForMemberRoutineGenericCalls(expr: v);
                }

                break;
            case ConditionalExpression cond:
                ScanExprForMemberRoutineGenericCalls(expr: cond.Condition);
                ScanExprForMemberRoutineGenericCalls(expr: cond.TrueExpression);
                ScanExprForMemberRoutineGenericCalls(expr: cond.FalseExpression);
                break;
        }
    }

    /// <summary>
    /// Registers a memberRoutine-generic resolution for a call that targets a memberRoutine with
    /// unresolved memberRoutine-level generic params, then recurses into the callee and arguments.
    /// </summary>
    private void ScanCallForMemberRoutineGenericCalls(CallExpression call)
    {
        if (call.ResolvedRoutine is
            {
                IsGenericDefinition: true, GenericParameters: { Count: > 0 } genParams
            } memberRoutine)
        {
            TypeSymbol?[] inferred = InferMemberRoutineTypeArgsFromCall(memberRoutine: memberRoutine,
                genParams: genParams,
                call: call);
            if (inferred.Length == genParams.Count && inferred.All(predicate: t => t != null))
            {
                ctx.Registry.GetOrCreateRoutineResolution(genericDef: memberRoutine,
                    typeArguments: inferred.Cast<TypeSymbol>()
                                           .ToList());
            }
        }

        ScanExprForMemberRoutineGenericCalls(expr: call.Callee);
        foreach (Expression arg in call.Arguments)
        {
            ScanExprForMemberRoutineGenericCalls(expr: arg);
        }
    }

    /// <summary>
    /// Registers memberRoutine-generic getitem!/setitem! resolutions from an IndexExpression node.
    /// OperatorLoweringPass doesn't run on InstantiatedGenericBodies, so getitem/setitem calls in
    /// those bodies are still IndexExpression rather than CallExpression.
    /// </summary>
    private void RegisterIndexAccessorGenericResolutions(IndexExpression idx)
    {
        if (idx.Object.ResolvedType is not { } idxObjType ||
            idx.Index.ResolvedType is not ({ } idxIdxType and not GenericParameterTypeSymbol))
        {
            return;
        }

        RoutineInfo? getItem = ctx.Registry.LookupMemberRoutine(type: idxObjType,
            memberRoutineName: "getitem",
            isFailable: true);
        if (getItem is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
        {
            ctx.Registry.GetOrCreateRoutineResolution(genericDef: getItem,
                typeArguments: [idxIdxType]);
        }

        RoutineInfo? setItem = ctx.Registry.LookupMemberRoutine(type: idxObjType,
            memberRoutineName: "setitem",
            isFailable: true);
        if (setItem is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
        {
            ctx.Registry.GetOrCreateRoutineResolution(genericDef: setItem,
                typeArguments: [idxIdxType]);
        }
    }

    /// <summary>
    /// Infers memberRoutine-level type arguments by matching GenericParameterTypeSymbol params
    /// against the corresponding call-site argument ResolvedTypes.
    /// </summary>
    private static TypeSymbol?[] InferMemberRoutineTypeArgsFromCall(RoutineInfo memberRoutine,
        List<string> genParams, CallExpression call)
    {
        var result = new TypeSymbol?[genParams.Count];

        foreach (ParamInfo param in memberRoutine.Parameters)
        {
            if (param.Name == "me")
            {
                continue;
            }

            if (param.Type is not GenericParameterTypeSymbol gp)
            {
                continue;
            }

            int idx = genParams.IndexOf(item: gp.Name);
            if (idx < 0 || result[idx] != null)
            {
                continue;
            }

            result[idx] = FindArgTypeForParam(call: call, paramName: param.Name);
        }

        return result;
    }

    /// <summary>
    /// Finds the ResolvedType of the argument bound to <paramref name="paramName"/> in the call,
    /// skipping arguments whose type is still an unresolved generic parameter.
    /// </summary>
    private static TypeSymbol? FindArgTypeForParam(CallExpression call, string paramName)
    {
        foreach (Expression arg in call.Arguments)
        {
            Expression argValue = arg is NamedArgumentExpression nae && nae.Name == paramName
                ? nae.Value
                : arg;
            TypeSymbol? argType = argValue.ResolvedType;
            if (argType != null && argType is not GenericParameterTypeSymbol)
            {
                return argType;
            }
        }

        return null;
    }
}
