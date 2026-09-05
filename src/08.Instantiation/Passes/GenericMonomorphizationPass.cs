using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Resolution;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Instantiation.Passes;

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
/// <see cref="Compiler.Synthesis.WiredRoutinePass"/> and emitted via
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
    // Routine-declaration index

    // Key: routine name (e.g. "List[T].getitem") -> list of matching declarations.
    // Built once in RunGlobal() before the fixed-point loop.
    private Dictionary<string, List<RoutineDeclaration>> _routineIndex = new();

    private void BuildRoutineIndex()
    {
        _routineIndex = new Dictionary<string, List<RoutineDeclaration>>();
        var allPrograms = ctx.Registry.StdlibPrograms.Concat(ctx.Registry.UserPrograms);
        foreach ((Program program, string _, string _) in allPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                AddDeclToIndex(key: decl.QualifiedName, decl: decl);

                // A constructor `routine T(...)` / `routine T[params](...)` is registered with the
                // canonical creator name "create" on its owner type (ResolvedInfo), but its AST
                // decl.Name is just the bare base type ("List", generics live in GenericParameters).
                // Monomorphization looks a creator up via BuildAstName(owner, "create") →
                // "List[T].create", so index it under that key too — otherwise generic constructor
                // bodies (List[Byte].create) never get monomorphized and codegen over-prunes them.
                if (decl.ResolvedInfo is { Name: "create", OwnerType: { } ctorOwner })
                {
                    AddDeclToIndex(key: BuildAstName(genDef: ctorOwner, routineName: "create"),
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
    public void RunIncremental() => SeedAndProcess(timing: false);

    private void SeedAndProcess(bool timing)
    {
        // Process concrete generic instances. Start with the liveness-filtered set, then use
        // a push-based queue to pick up types discovered during body rewriting
        // (e.g. ListEmitter[Byte] registered by GenericAstRewriter when rewriting List[Byte].iter).
        // Only types newly created by GetOrCreateResolution after tracking starts are enqueued —
        // pre-existing phantom types (BTreeDictNode stubs etc.) never enter the queue.
        var sw = timing ? Stopwatch.StartNew() : null;
        int startCount = _processedTypes.Count;

        // Seed with liveness-filtered instances + wrapper instances. On an incremental re-run the
        // Add returns false for everything already processed, so these scans become cheap lookups.
        foreach (TypeInfo concreteType in ctx.Registry.AllConcreteGenericInstances
                     .Concat(second: ctx.Registry.AllConcreteWrapperInstances)
                     .DistinctBy(type => type.FullName)
                     .ToArray())
        {
            if (_processedTypes.Add(concreteType.FullName))
                ProcessConcreteType(concreteType);
        }

        // One-time pass over ALL concrete types in _resolutions that weren't in the liveness set.
        // Catches types created before GMP started (e.g. List[Bytes] resolved during SA
        // but not reached by the liveness walk). Self-nesting types (Hijacked^N) are blocked by
        // the guard in GetOrCreateResolution, so this scan terminates.
        // Materialize to a list first — ProcessConcreteType modifies _resolutions during iteration.
        foreach (TypeInfo preExisting in ctx.Registry.AllConcreteGenericInstancesUnfiltered.ToList())
        {
            if (!_processedTypes.Add(preExisting.FullName)) continue;
            ProcessConcreteType(preExisting);
        }

        // Same for wrapper instances (Hijacked[T], T, etc.). Wrappers like Hijacked have
        // explicit memberRoutine definitions in stdlib that need monomorphization for each concrete T,
        // but live in _wrapperResolutions and aren't enqueued by NotifyConcreteRegistration.
        foreach (TypeInfo preExisting in ctx.Registry.AllConcreteWrapperInstancesUnfiltered.ToList())
        {
            if (!_processedTypes.Add(preExisting.FullName)) continue;
            ProcessConcreteType(preExisting);
        }

        // Fixed-point expansion: drain types created during body rewriting
        // (e.g. ListEmitter[Byte] registered when GenericAstRewriter rewrites List[Byte].iter).
        // The self-nesting guard in GetOrCreateResolution prevents Hijacked^N infinite chains.
        List<TypeInfo> discovered;
        bool madeProgress;
        do
        {
            madeProgress = false;
            while ((discovered = ctx.Registry.DrainGmpDiscoveryQueue()).Count > 0)
            {
                foreach (TypeInfo newType in discovered)
                {
                    if (!_processedTypes.Add(newType.FullName)) continue;
                    ProcessConcreteType(newType);
                    madeProgress = true;
                }
            }

            // Wrapper instances aren't enqueued by NotifyConcreteRegistration (it only handles
            // EntityTypeInfo/RecordTypeInfo). Re-scan _wrapperResolutions each round to pick up
            // new wrappers like Hijacked[Text] that GenericAstRewriter created while
            // rewriting List[Text] forwarder bodies.
            foreach (TypeInfo wrapper in ctx.Registry.AllConcreteWrapperInstancesUnfiltered.ToList())
            {
                if (!_processedTypes.Add(wrapper.FullName)) continue;
                ProcessConcreteType(wrapper);
                madeProgress = true;
            }
        } while (madeProgress);

        if (timing)
        {
            sw!.Stop();
            Console.Error.WriteLine(value:
                $"    GMP reachable types: {_processedTypes.Count} ({_processedTypes.Count - startCount} new) ({sw.ElapsedMilliseconds} ms)");
        }

        // Scan built bodies for memberRoutine-generic call sites (e.g. getitem[U64]! called from
        // List[Bytes].eq). SA only analyzes generic-def bodies, so these concrete
        // call sites are never registered in _routineResolutions. Register them now so
        // ProcessResolvedMemberRoutineGenericRoutines can build their bodies.
        var _sw2 = timing ? Stopwatch.StartNew() : null;
        void _s2(string label)
        {
            if (_sw2 == null) return;
            _sw2.Stop();
            Console.Error.WriteLine(value: $"        GMP.RG sub - {label}: {_sw2.ElapsedMilliseconds} ms");
            _sw2.Restart();
        }

        ScanAndRegisterMemberRoutineGenericCallResolutions();
        _s2(label: "ScanAndRegisterMemberRoutineGenericCallResolutions");

        ProcessResolvedMemberRoutineGenericRoutines();
        _s2(label: "ProcessResolvedMemberRoutineGenericRoutines");

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
        _s2(label: "ExpandLivenessThroughEmittedBodies");

        EmitGenericDefBuilderQueryBodies();
        _s2(label: "EmitGenericDefBuilderQueryBodies");
    }

    /// <summary>
    /// Propagates liveness through the bodies GMP already emitted, then emits the routines/types
    /// that become reachable as a result. Needed because <see cref="RoutineReachabilityPass"/> runs
    /// before <see cref="ProtocolDefaultImplLoweringPass"/>, so types/routines reachable only through
    /// the synthesized iterator-adapter chain were never marked live and got gated out. A fixed-point
    /// loop is required: enlivening one layer (e.g. <c>SelectIterator</c>) emits its <c>iter</c>,
    /// whose body references the next layer (<c>SelectEmitter</c>), and so on.
    /// </summary>
    private void ExpandLivenessThroughEmittedBodies()
    {
        // Both gates empty means fan-out mode (no liveness filtering) — everything is already emitted.
        if (ctx.LiveOwnerTypeNames.Count == 0 && ctx.LiveRoutineKeys.Count == 0)
            return;

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
            var newOwners = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);
            bool registeredRoutine = false;

            foreach ((string bodyKey, MonomorphizedBody mb) in ctx.InstantiatedGenericBodies.ToList())
            {
                // Only walk LIVE bodies. A dead (unreachable) instantiation is never emitted by codegen,
                // so the owner types / callees it references need no enlivening: any adapter reached only
                // through a LIVE body is marked live when that body is walked below (line ~306) and then
                // picked up as live in the next fixed-point round. This skips re-walking the thousands of
                // restored-but-dead stdlib instantiations every warm run (the resident-set cost that
                // priming the whole stdlib into the snapshot exposed). The live check is BEFORE
                // _walkedBodyKeys so a body that only BECOMES live in a later round is still walked then.
                // Empty LiveRoutineKeys = fan-out/no-filter mode (the method already early-returns when
                // BOTH liveness gates are empty), so keep walking everything in that case.
                if (ctx.LiveRoutineKeys.Count > 0 && !ctx.LiveRoutineKeys.Contains(item: bodyKey)) continue;
                if (!_walkedBodyKeys.Add(item: bodyKey) || mb.Ast?.Body == null) continue;
                // A `throw X` in an emitted body needs X's crash_message live: codegen's EmitThrow
                // calls it directly with no source CallExpression. RoutineReachabilityPass handles
                // throws in bodies it walks, but bodies emitted HERE (e.g. a specialized-receiver
                // member's retrieve!/race! that throws TaskSpawnError) were never walked by it.
                AstWalker.Walk(root: mb.Ast.Body, visit: node =>
                {
                    if (EnliveThrowCrashMessage(node: node)) changed = true;
                });
                AstWalker.WalkExpressions(root: mb.Ast.Body, visit: expr =>
                {
                    if (EnliveEmittedBodyExpression(expr: expr, newOwners: newOwners,
                            registeredRoutine: ref registeredRoutine))
                    {
                        changed = true;
                    }
                });
            }

            foreach ((string fullName, TypeInfo type) in newOwners)
            {
                if (ctx.LiveOwnerTypeNames.Add(item: fullName))
                {
                    _processedTypes.Add(item: fullName);
                    ProcessConcreteType(type);
                    changed = true;
                }
            }

            // Drain types created while rewriting the bodies just emitted.
            List<TypeInfo> discovered;
            while ((discovered = ctx.Registry.DrainGmpDiscoveryQueue()).Count > 0)
                foreach (TypeInfo newType in discovered)
                {
                    if (!_processedTypes.Add(item: newType.FullName)) continue;
                    ProcessConcreteType(newType);
                    changed = true;
                }

            // Only re-run the (expensive) resolved-routine builder when this round actually
            // registered a new resolution to build — e.g. a universal-memberRoutine instance.
            if (registeredRoutine)
                ProcessResolvedMemberRoutineGenericRoutines();
        }

        EnliveWiredLeafCallees();
    }

    /// <summary>
    /// A `throw X` in an emitted body needs X's crash_message live: codegen's EmitThrow calls it
    /// directly with no source CallExpression. Enlivens that crash_message. Returns true if it newly
    /// marked a routine live.
    /// </summary>
    private bool EnliveThrowCrashMessage(object? node)
    {
        if (node is not ThrowStatement throwStmt) return false;
        TypeInfo? errorType = throwStmt.Error.ResolvedType
            ?? (throwStmt.Error is CreatorExpression cre ? cre.ConstructedType : null);
        if (errorType == null) return false;
        RoutineInfo? crashMsg =
            ctx.Registry.LookupMemberRoutine(type: errorType, memberRoutineName: RuntimeContract.CrashMessage);
        return crashMsg != null && ctx.LiveRoutineKeys.Count > 0
               && ctx.LiveRoutineKeys.Add(item: crashMsg.RegistryKey);
    }

    /// <summary>
    /// Enlivens the concrete owner types and callees referenced by one expression in an emitted body.
    /// Returns true if it newly marked an owner type or routine live (so the fixed point continues).
    /// </summary>
    private bool EnliveEmittedBodyExpression(Expression expr, Dictionary<string, TypeInfo> newOwners,
        ref bool registeredRoutine)
    {
        bool changed = false;
        CollectConcreteOwnerTypes(t: expr.ResolvedType, sink: newOwners);
        if (expr is CreatorExpression creator)
            CollectConcreteOwnerTypes(t: creator.ConstructedType, sink: newOwners);
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
        if (calledRoutine is { } rr
            && ctx.LiveRoutineKeys.Count > 0
            && ctx.LiveRoutineKeys.Add(item: rr.RegistryKey))
        {
            if (rr.GenericDefinition != null
                && !ctx.InstantiatedGenericBodies.ContainsKey(rr.RegistryKey)
                && !ctx.VariantBodies.ContainsKey(rr.RegistryKey))
            {
                ctx.Registry.RegisterRoutineResolution(resolvedMemberRoutine: rr);
                registeredRoutine = true;
            }
            changed = true;
        }

        if (EnliveIndexAccessors(expr: expr, registeredRoutine: ref registeredRoutine))
            changed = true;
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
        if (expr is not IndexExpression { Object.ResolvedType: { } idxObjType } idxExpr
            || idxObjType is GenericParameterTypeInfo
            || ctx.LiveRoutineKeys.Count == 0)
        {
            return false;
        }

        bool changed = false;
        TypeInfo? idxType = idxExpr.Index.ResolvedType;
        foreach (string accessor in (ReadOnlySpan<string>)["getitem", "setitem"])
        {
            // Prefer the exact argType-matched overload. When the index type doesn't pin one (it is
            // null, or not the accessor's index-parameter type), enliven EVERY registered overload of
            // this accessor on the concrete owner instead of taking an arbitrary name-only pick — a
            // name-only lookup can't disambiguate >1 overload (no first-wins), and liveness is
            // conservative: an extra concrete accessor is harmless (codegen emits only the one it calls).
            var accs = new List<RoutineInfo>();
            RoutineInfo? typed = idxType != null
                ? ctx.Registry.LookupMemberRoutineOverload(type: idxObjType,
                    memberRoutineName: accessor, argTypes: [idxType])
                : null;
            if (typed != null)
                accs.Add(item: typed);
            else
                ctx.Registry.CollectMemberRoutineCandidates(type: idxObjType,
                    memberRoutineName: accessor, candidates: accs);

            foreach (RoutineInfo acc in accs)
            {
                if (acc is not { OwnerType: not { IsGenericDefinition: true } }
                    || !ctx.LiveRoutineKeys.Add(item: acc.RegistryKey))
                {
                    continue;
                }
                if (acc.GenericDefinition != null
                    && !ctx.InstantiatedGenericBodies.ContainsKey(acc.RegistryKey)
                    && !ctx.VariantBodies.ContainsKey(acc.RegistryKey))
                {
                    ctx.Registry.RegisterRoutineResolution(resolvedMemberRoutine: acc);
                    registeredRoutine = true;
                }
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>
    /// Closes the live-routine set under the implicit callees of compiler-synthesized routines.
    /// <para>
    /// Routines enlivened AFTER <see cref="RoutineReachabilityPass"/> (by this pass, e.g. a wired
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
        if (ctx.LiveRoutineKeys.Count == 0) return;

        var queue = new Queue<RoutineInfo>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        // Seed from every currently-live routine that resolves to a RoutineInfo.
        foreach (string key in ctx.LiveRoutineKeys.ToArray())
        {
            RoutineInfo? r = ctx.Registry.LookupRoutine(fullName: key);
            if (r != null && seen.Add(item: key)) queue.Enqueue(item: r);
        }

        while (queue.Count > 0)
        {
            RoutineInfo r = queue.Dequeue();
            foreach (RoutineInfo callee in WiredLeafCalleesOf(routine: r))
            {
                // Skip generic-def callees — those are emitted (with their callees) via
                // ProcessConcreteType for each concrete instantiation, not by this leaf closure.
                if (callee.OwnerType?.IsGenericDefinition == true) continue;
                ctx.LiveRoutineKeys.Add(item: callee.RegistryKey);
                if (seen.Add(item: callee.RegistryKey)) queue.Enqueue(item: callee);
            }
        }
    }

    /// <summary>
    /// Yields the wired routines a synthesized body for <paramref name="routine"/> implicitly calls.
    /// See <see cref="EnliveWiredLeafCallees"/> for the rationale and the verb→callee mapping.
    /// </summary>
    private IEnumerable<RoutineInfo> WiredLeafCalleesOf(RoutineInfo routine)
    {
        TypeInfo? owner = routine.OwnerType;
        if (owner == null) yield break;

        switch (routine.Name)
        {
            case "lt" or "le" or "gt" or "ge" or "cmp":
                // Derived comparisons (and composite cmp) reduce to a cmp whose ComparisonSign
                // result is tested with ComparisonSign.eq/ne.
                if (routine.Name != "cmp"
                    && ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: "cmp") is { } cmp)
                    yield return cmp;
                if (ctx.Registry.LookupType(name: "ComparisonSign") is { } cs)
                {
                    if (ctx.Registry.LookupMemberRoutine(type: cs, memberRoutineName: "eq") is { } cseq)
                        yield return cseq;
                    if (ctx.Registry.LookupMemberRoutine(type: cs, memberRoutineName: "ne") is { } csne)
                        yield return csne;
                }
                if (routine.Name == "cmp")
                    foreach (RoutineInfo fc in MemberVariableWiredCallees(owner: owner, verb: "cmp"))
                        yield return fc;
                break;
            case "ne":
                if (ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: "eq") is { } eq)
                    yield return eq;
                break;
            case "notcontains":
                if (ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: "contains") is { } contains)
                    yield return contains;
                break;
            case "destroy" or "assign" or "hash" or "eq":
                foreach (RoutineInfo fc in MemberVariableWiredCallees(owner: owner, verb: routine.Name))
                    yield return fc;
                break;
        }
    }

    /// <summary>
    /// Yields the <paramref name="verb"/> wired routine on each owned field/element/payload type of
    /// a composite <paramref name="owner"/> (record, entity, crashable, tuple, variant). Scalar
    /// kinds (choices, flags, <c>@llvm</c>-backed primitives) have no fields and yield nothing.
    /// Mirrors <c>WiredRoutinePass.BuildDestroyBody</c>'s field selection.
    /// </summary>
    private IEnumerable<RoutineInfo> MemberVariableWiredCallees(TypeInfo owner, string verb)
    {
        IEnumerable<TypeInfo> fieldTypes = owner switch
        {
            VariantTypeInfo v => v.Members
                .Where(predicate: m => m is { IsNone: false, Type: not null })
                .Select(selector: m => m.Type!),
            EntityTypeInfo e => e.MemberVariables.Select(selector: f => f.Type),
            TupleTypeInfo t => t.MemberVariables.Select(selector: f => f.Type),
            ChoiceTypeInfo or FlagsTypeInfo => [],
            RecordTypeInfo { BackendType: null } r =>
                r.MemberVariables.Select(selector: f => f.Type),
            _ => []
        };

        foreach (TypeInfo ft in fieldTypes)
        {
            if (ft == null) continue;
            if (ctx.Registry.LookupMemberRoutine(type: ft, memberRoutineName: verb) is { } callee)
                yield return callee;
        }
    }

    /// <summary>
    /// Adds every concrete (fully-resolved) generic owner type reachable from <paramref name="t"/>
    /// — including those nested in its type arguments — to <paramref name="sink"/>. Generic
    /// definitions and types with unresolved parameters are ignored.
    /// </summary>
    private static void CollectConcreteOwnerTypes(TypeInfo? t, Dictionary<string, TypeInfo> sink)
    {
        if (t == null || HasUnresolvedTypeArgs(t)) return;
        if (GetGenericBase(t) != null || t is WrapperTypeInfo)
            sink.TryAdd(key: t.FullName, value: t);
        if (t.TypeArguments is { Count: > 0 } args)
            foreach (TypeInfo arg in args)
                CollectConcreteOwnerTypes(t: arg, sink: sink);
    }
    // Per-type processing

    private void ProcessConcreteType(TypeInfo concreteType)
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
        if (ctx.LiveRoutineKeys.Count > 0
            && !ctx.LiveOwnerTypeNames.Contains(item: concreteType.FullName))
        {
            return;
        }

        TypeInfo? genDef = concreteType switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d,
            RecordTypeInfo { GenericDefinition: { } d } => d,
            WrapperTypeInfo wrapper => ctx.Registry.LookupType(name: wrapper.Name),
            _ => null
        };

        if (genDef?.GenericParameters == null || genDef.GenericParameters.Count == 0)
            return;

        List<TypeInfo>? typeArgs = concreteType.TypeArguments;
        if (typeArgs == null || typeArgs.Count != genDef.GenericParameters.Count)
            return;

        (Dictionary<string, TypeInfo> typeSubs, Dictionary<string, string> stringSubs) =
            BuildConcreteTypeSubstitutionMaps(genDef: genDef, typeArgs: typeArgs);

        foreach (RoutineInfo genMemberRoutine in ctx.Registry.GetMemberRoutinesForType(genDef))
        {
            ProcessConcreteTypeMemberRoutine(
                concreteType: concreteType,
                genDef: genDef,
                genMemberRoutine: genMemberRoutine,
                typeSubs: typeSubs,
                stringSubs: stringSubs);
        }
    }

    /// <summary>
    /// Builds the owner type-parameter substitution maps for a concrete generic instance.
    /// stringSubs uses FullName (e.g. "T" → "Core.S64") so rewritten AST type-expression names are
    /// fully qualified; typeSubs carries the resolved TypeInfo for ResolvedType annotation in
    /// GenericAstRewriter.
    /// </summary>
    private static (Dictionary<string, TypeInfo> typeSubs, Dictionary<string, string> stringSubs)
        BuildConcreteTypeSubstitutionMaps(TypeInfo genDef, List<TypeInfo> typeArgs)
    {
        var typeSubs = new Dictionary<string, TypeInfo>(capacity: genDef.GenericParameters!.Count);
        var stringSubs = new Dictionary<string, string>(capacity: genDef.GenericParameters.Count);
        for (int i = 0; i < genDef.GenericParameters.Count; i++)
        {
            typeSubs[genDef.GenericParameters[i]] = typeArgs[i];
            stringSubs[genDef.GenericParameters[i]] = typeArgs[i].FullName;
        }
        return (typeSubs, stringSubs);
    }

    /// <summary>
    /// Builds and stores the monomorphized body for one member routine of a concrete generic
    /// instance, applying the reachability + capability gates and the abstract-derive check.
    /// </summary>
    private void ProcessConcreteTypeMemberRoutine(
        TypeInfo concreteType,
        TypeInfo genDef,
        RoutineInfo genMemberRoutine,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        RoutineInfo? concreteInfo = BuildConcreteRoutineInfo(
            genMemberRoutine: genMemberRoutine,
            concreteOwner: concreteType,
            typeSubs: typeSubs);

        // Null means the forwarder's inner type doesn't have this memberRoutine — skip it.
        if (concreteInfo == null)
            return;

        string key = concreteInfo.RegistryKey;
        if (ctx.InstantiatedGenericBodies.ContainsKey(key))
            return;

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
        bool genuinelyLive = ctx.LiveRoutineKeys.Count == 0 || ctx.LiveRoutineKeys.Contains(item: key);
        if (!genuinelyLive && !IsWiredRoutineName(genMemberRoutine.Name))
            return;

        // Capability gate: skip wired comparison/containment/hashing routines whose
        // generic-def constraint (e.g. `needs T obeys Equatable`) is not satisfied by
        // this concrete owner. See RoutineApplicableToConcreteOwner.
        if (!RoutineApplicableToConcreteOwner(routine: concreteInfo, owner: concreteType))
            return;

        MonomorphizedBody? body = BuildBody(
            genMemberRoutine: genMemberRoutine,
            concreteInfo: concreteInfo,
            genDef: genDef,
            typeSubs: typeSubs,
            stringSubs: stringSubs);

        if (body != null && BodyHasAbstractDeriveCall(body: body, member: out string absMember))
        {
            body = HandleAbstractDeriveCall(genDef: genDef, concreteInfo: concreteInfo,
                absMember: absMember);
        }

        if (body != null)
        {
            ctx.InstantiatedGenericBodies[key] = body;
            // Keep codegen's Phase-B live gate in sync with GMP's wired-routine bypass.
            // LLVMCodeGenerator emits an instantiated body only when its key is in the live set,
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
    private static MonomorphizedBody? HandleAbstractDeriveCall(TypeInfo genDef,
        RoutineInfo concreteInfo, string absMember)
    {
        bool ownerIsWrapper =
            WrapperForwardingPass.WrapperTypeNames.Contains(genDef.Name);
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
    /// <see cref="Compiler.Resolution.WiredRoutineCatalog.AlwaysLiveNames"/>) — scope-exit teardown
    /// inserts <c>destroy</c> calls that must always have a concrete body, and the matching
    /// retaining <c>store</c> likewise; (2) <c>try_emit</c>, reachable only through synthesized
    /// for-loop iteration bodies whose owner type (ListEmitter[Byte], etc.) is created post-pass
    /// during GMP body rewriting, so ReachabilityPass cannot trace it. Kept narrow otherwise —
    /// broader sets cascade into derived-op chains (ne->eq) where the missing companion is the
    /// actual culprit.
    private static readonly HashSet<string> _gateBypassNames =
        new(collection: WiredRoutineCatalog.AlwaysLiveNames,
            comparer: StringComparer.Ordinal) { RuntimeContract.TryEmit };

    private static bool IsWiredRoutineName(string name) => _gateBypassNames.Contains(name);

    /// <summary>
    /// Returns false when this concrete instantiation does not actually have the wired
    /// routine. Example: `Array[T, N].eq` is declared `needs T obeys Equatable` — for
    /// `T = X` (not equatable), the routine does not exist on this owner. Body
    /// emission must skip it so derived companions (`ne`, `notcontains`) don't reference
    /// a missing symbol downstream in codegen. The actual protocol-to-wired-routine map
    /// lives in <see cref="TypeRegistry"/> (single source of truth).
    /// </summary>
    private bool RoutineApplicableToConcreteOwner(RoutineInfo routine, TypeInfo owner)
        => ctx.Registry.TypeHasWiredRoutine(type: owner, wiredName: routine.Name);

    private void ProcessResolvedMemberRoutineGenericRoutines()
    {
        var processed = new HashSet<string>(StringComparer.Ordinal);
        bool discoveredNew;
        do
        {
            discoveredNew = false;
            foreach (RoutineInfo resolvedRoutine in ctx.Registry.GetAllRoutineResolutions().ToList())
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
        if (resolvedRoutine.GenericDefinition == null ||
            ctx.InstantiatedGenericBodies.ContainsKey(resolvedRoutine.RegistryKey) ||
            ctx.VariantBodies.ContainsKey(resolvedRoutine.RegistryKey) ||
            resolvedRoutine.OwnerType is ProtocolTypeInfo)
        {
            return;
        }

        // Reachability gate: SA's RoutineResolutions index includes routines that were
        // type-checked during analysis but never reached from program entry points.
        // ProcessConcreteType applies this gate at line ~222; this path historically
        // bypassed it, causing Phase B to emit unreachable bodies whose call sites
        // reference further unreachable routines (linker errors).
        if (ctx.LiveRoutineKeys.Count > 0
            && !ctx.LiveRoutineKeys.Contains(item: resolvedRoutine.RegistryKey)
            && !IsWiredRoutineName(resolvedRoutine.Name))
        {
            return;
        }

        if (resolvedRoutine.OwnerType is { } resolvedOwner
            && !RoutineApplicableToConcreteOwner(routine: resolvedRoutine, owner: resolvedOwner))
        {
            return;
        }

        // Routine types are structural — no per-type wired handler in WiredRoutinePass — so the
        // universal represent/diagnose template would field-walk their (empty) member set into a
        // bogus `Routine()`. Emit the SIGNATURE (the routine type's own name, e.g.
        // `Routine[(S32,), S32]`) directly: no parens, no field walk. (serialize isn't a
        // universal memberRoutine, so routine serialize is handled where it's registered.)
        if (resolvedRoutine.OwnerType is RoutineTypeInfo routineOwner
            && resolvedRoutine.Name is RuntimeContract.Display.Represent
                or RuntimeContract.Display.Diagnose)
        {
            EmitRoutineTypeSignatureBody(resolvedRoutine: resolvedRoutine, routineOwner: routineOwner);
            return;
        }

        Dictionary<string, TypeInfo> typeSubs =
            BuildResolvedRoutineTypeSubstitutions(resolvedRoutine);
        if (typeSubs.Count == 0 ||
            typeSubs.Values.Any(predicate: HasUnresolvedTypeArgs))
        {
            return;
        }

        string astName = BuildAstNameForResolvedRoutine(resolvedRoutine);
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: astName,
            expectedParamCount: resolvedRoutine.GenericDefinition.Parameters.Count,
            typeSubs: typeSubs,
            expectedParamNames: resolvedRoutine.GenericDefinition.Parameters
                .Select(static p => p.Name).ToList(),
            expectedParamTypeNames: resolvedRoutine.GenericDefinition.Parameters
                .Select(static p => (string?)p.Type?.Name).ToList(),
            expectedOwnerModule: resolvedRoutine.OwnerType?.FullName is { } ownerFn
                ? TypeInfo.StripTypeArgs(name: ownerFn)
                : null);
        if (astDecl == null)
        {
            EmitResolvedRoutineBodyFromVariantFallback(resolvedRoutine: resolvedRoutine,
                typeSubs: typeSubs);
            return;
        }

        var stringSubs = typeSubs.ToDictionary(
            keySelector: kvp => kvp.Key,
            elementSelector: kvp => kvp.Value.FullName);

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: resolvedRoutine);

        ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] = new MonomorphizedBody(
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
    private void EmitRoutineTypeSignatureBody(RoutineInfo resolvedRoutine, RoutineTypeInfo routineOwner)
    {
        var sigBody = new ReturnStatement(
            Value: new LiteralExpression(Value: routineOwner.Name,
                LiteralType: Tokenizer.TokenType.TextLiteral,
                Location: resolvedRoutine.Location)
            {
                ResolvedType = ctx.Registry.LookupType(name: "Text")
            },
            Location: resolvedRoutine.Location);
        ctx.InstantiatedGenericBodies[key: resolvedRoutine.RegistryKey] =
            new MonomorphizedBody(
                Ast: WrapInShellDecl(name: resolvedRoutine.Name, body: sigBody,
                    info: resolvedRoutine),
                Info: resolvedRoutine,
                TypeSubs: new Dictionary<string, TypeInfo>(),
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
        Dictionary<string, TypeInfo> typeSubs)
    {
        // Check if the generic definition has a synthesized body in VariantBodies.
        // ProcessConcreteType->BuildBody checks genMemberRoutine.RegistryKey (the generic def key),
        // but this path only checked resolvedRoutine.RegistryKey (the concrete key).
        // Example: List[T].eq body is stored under "Core.List[T].eq#Core.List[T]",
        // but resolvedRoutine.RegistryKey is "Core.List[Core.Byte].eq#Core.List[Core.Byte]".
        string genDefRoutineKey = resolvedRoutine.GenericDefinition!.RegistryKey;
        if (ctx.VariantBodies.TryGetValue(key: genDefRoutineKey, out Statement? defVariantBody))
        {
            var stringSubs2 = typeSubs.ToDictionary(
                keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.FullName);
            Statement rewritten = GenericAstRewriter.RewriteStatement(
                defVariantBody, stringSubs2, typeSubs, ctx.Registry, resolvedRoutine);
            ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] = new MonomorphizedBody(
                Ast: WrapInShellDecl(name: resolvedRoutine.Name, body: rewritten,
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
            ctx.VariantBodies.TryGetValue(key: genDefGenDefKey, out Statement? genDefGenDefBody))
        {
            var stringSubs3 = typeSubs.ToDictionary(
                keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.FullName);
            Statement rewritten3 = GenericAstRewriter.RewriteStatement(
                genDefGenDefBody, stringSubs3, typeSubs, ctx.Registry, resolvedRoutine);
            ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] = new MonomorphizedBody(
                Ast: WrapInShellDecl(name: resolvedRoutine.Name, body: rewritten3,
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
                                 new SourceLocation("", 0, 0, 0);
            ctx.InstantiatedGenericBodies[resolvedRoutine.RegistryKey] =
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
        foreach (TypeInfo type in ctx.Registry.GetTypesWithMemberRoutines())
        {
            if (!type.IsGenericDefinition) continue;
            // Skip wrapper types: their concrete instances are handled by ProcessConcreteType
            // (which substitutes T with the concrete inner type). Emitting the wrapper generic-def
            // version would attempt to lower a body that still contains unresolved T references
            // (e.g. the as_entity() call), causing Phase B codegen failures.
            if (type is WrapperTypeInfo) continue;

            foreach (RoutineInfo routine in ctx.Registry.GetMemberRoutinesForType(type))
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
        if (!BuilderInfoProvider.IsBuilderQueryRoutine(name: routine.Name)) return;
        string key = routine.RegistryKey;
        if (ctx.InstantiatedGenericBodies.ContainsKey(key)) return;
        if (!ctx.VariantBodies.TryGetValue(key: key, value: out Statement? body)) return;

        ctx.InstantiatedGenericBodies[key] = new MonomorphizedBody(
            Ast: WrapInShellDecl(name: routine.Name, body: body, info: routine),
            Info: routine,
            TypeSubs: new Dictionary<string, TypeInfo>(),
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
        new(comparer: StringComparer.Ordinal) { "assign", "duplicate", "eq", "ne", "cmp", "hash" };

    /// <summary>
    /// After monomorphization, no call in a CONCRETE routine body may remain resolved to an ABSTRACT
    /// protocol member (owner is a <see cref="ProtocolTypeInfo"/>) for the derive family — the receiver is
    /// concrete, so it must resolve to that type's own derive. A leftover abstract call means the type
    /// "obeys P" but never got P's derive (e.g. a variant that obeys Copyable whose `copy` was never
    /// synthesized). Throw with the enclosing routine so the conformance/derive gap is unmissable.
    /// </summary>
    private static bool BodyHasAbstractDeriveCall(MonomorphizedBody body, out string member)
    {
        string? found = null;
        AstWalker.WalkExpressions(root: body.Ast, visit: expr =>
        {
            if (found != null)
            {
                return;
            }

            RoutineInfo? rr = (expr as CallExpression)?.ResolvedRoutine;
            if (rr?.OwnerType is ProtocolTypeInfo proto && _concreteRequiredDerives.Contains(item: rr.Name))
            {
                found = $"{proto.Name}.{rr.Name}()";
            }
        });
        member = found ?? "";
        return found != null;
    }

    private MonomorphizedBody? BuildBody(
        RoutineInfo genMemberRoutine,
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // Variant memberRoutines (try_/check_/lookup_)
        // These have OriginalName pointing back to the failable source routine.
        // Look for the body of that source routine (not the variant name).
        if (genMemberRoutine.OriginalName != null)
            return BuildVariantBody(
                genMemberRoutine: genMemberRoutine,
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs);
        // If the concrete memberRoutine still has unresolved memberRoutine-level generic parameters
        // (e.g. Text.getitem! where index: I is still GenericParameterTypeInfo),
        // skip this body here. ProcessResolvedMemberRoutineGenericRoutines handles per-concrete-
        // index-type specialization once OperatorLoweringPass registers the resolutions.
        if (concreteInfo.Parameters.Any(static p => p.Type is GenericParameterTypeInfo))
            return null;
        // WiredRoutinePass / ErrorHandlingVariantPass body in VariantBodies
        bool skippedVariantBodyForStdlibFallback = false;
        if (ctx.VariantBodies.TryGetValue(key: genMemberRoutine.RegistryKey, out Statement? variantBody))
        {
            MonomorphizedBody? variantMono = BuildVariantBodyFromVariantBodies(
                concreteInfo: concreteInfo,
                genDef: genDef,
                typeSubs: typeSubs,
                stringSubs: stringSubs,
                variantBody: variantBody,
                skippedForStdlibFallback: out skippedVariantBodyForStdlibFallback);
            if (variantMono != null)
                return variantMono;
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
        var paramNames = genMemberRoutine.Parameters.Select(static p => p.Name).ToList();
        var paramTypeNames = genMemberRoutine.Parameters.Select(static p => (string?)p.Type?.Name).ToList();
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: astName,
            expectedParamCount: genMemberRoutine.Parameters.Count,
            typeSubs: typeSubs,
            expectedParamNames: paramNames,
            expectedParamTypeNames: paramTypeNames,
            expectedOwnerModule: genDef.FullName is { } gdFn
                ? TypeInfo.StripTypeArgs(name: gdFn)
                : null,
            expectedOwnerRealm: genDef.Realm);

        if (astDecl == null)
        {
            string ck = concreteInfo.RegistryKey;
            if (ck.Contains("List[Core.Owned") && (ck.Contains("eq") || ck.Contains("contains")))
                Console.Error.WriteLine($"[BuildBody-stdlib-miss] gen={genMemberRoutine.RegistryKey} concrete={ck} astName={astName}");
            return null;
        }

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
                subs: stringSubs,
                typeSubs: typeSubs,
                registry: ctx.Registry,
                enclosingRoutine: concreteInfo);

        return new MonomorphizedBody(
            Ast: rewrittenDecl,
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
    private MonomorphizedBody? BuildVariantBodyFromVariantBodies(
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs,
        Statement variantBody,
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
            WrapperForwardingPass.WrapperTypeNames.Contains(genDef.Name) &&
            typeSubs.Count > 0 &&
            typeSubs.Values.First() is not EntityTypeInfo;
        if (!isWrapperWithNonEntityInner)
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(variantBody, stringSubs, typeSubs, ctx.Registry, concreteInfo);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: concreteInfo.Name, body: rewritten, info: concreteInfo),
                Info: concreteInfo,
                TypeSubs: typeSubs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized: true);  // treat as synthesized so codegen uses EmitSynthesizedBodyFromAst
        }
        skippedForStdlibFallback = true;
        return null;
    }

    private MonomorphizedBody? BuildVariantBody(
        RoutineInfo genMemberRoutine,
        RoutineInfo concreteInfo,
        TypeInfo genDef,
        Dictionary<string, TypeInfo> typeSubs,
        Dictionary<string, string> stringSubs)
    {
        // Compute carrier-unwrapping metadata for instantiated variant bodies.
        FailableVariant? variantStatus = null;
        TypeInfo? variantInnerType = null;

        if (concreteInfo.ReturnType?.TypeArguments is { Count: > 0 })
        {
            string? baseName = GetGenericBaseName(concreteInfo.ReturnType);
            if (baseName == "Lookup")
            {
                variantStatus = FailableVariant.Lookup;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[0];
            }
            else if (baseName == "Result")
            {
                variantStatus = FailableVariant.Check;
                variantInnerType = concreteInfo.ReturnType.TypeArguments[0];
            }
        }
        // TryBool variants return Bool (no type args) — detect via the failable-variant kind.
        if (variantStatus == null && concreteInfo.FailableVariant == FailableVariant.TryBool)
            variantStatus = FailableVariant.TryBool;

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
                Storage = concreteInfo.Storage,
                AsyncStatus = concreteInfo.AsyncStatus,
                FailableVariant = variantStatus.Value,
                OriginalName = concreteInfo.OriginalName
            };
        }

        // Pre-built variant body from ErrorHandlingVariantPass (keyed by generic memberRoutine RegistryKey)
        if (ctx.VariantBodies.TryGetValue(key: genMemberRoutine.RegistryKey, out Statement? prebuiltVariant))
        {
            Statement rewritten = GenericAstRewriter.RewriteStatement(prebuiltVariant, stringSubs, typeSubs, ctx.Registry, emitInfo);
            return new MonomorphizedBody(
                Ast: WrapInShellDecl(name: emitInfo.Name, body: rewritten, info: emitInfo),
                Info: emitInfo,
                TypeSubs: typeSubs,
                VariantStatus: variantStatus,
                VariantInnerType: variantInnerType,
                IsSynthesized: false);
        }

        // Fallback: search for the original failable routine's AST and compile it as a variant
        string fallbackAstName = BuildAstName(genDef: genDef, routineName: genMemberRoutine.OriginalName!);
        RoutineDeclaration? astDecl = FindInStdlib(
            genericAstName: fallbackAstName,
            expectedParamCount: genMemberRoutine.Parameters.Count);

        if (astDecl == null)
            return null;

        RoutineDeclaration rewrittenDecl =
            GenericAstRewriter.Rewrite(
                routine: astDecl,
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
            _ when emitInfo.ReturnType != null && GetGenericBaseName(emitInfo.ReturnType) == "Maybe" => ErrorHandlingVariantKind.Try,
            _ => null
        };
        if (fallbackKind != null)
        {
            Statement transformed = ErrorHandlingVariantPass.TransformBody(
                body: rewrittenDecl.Body, kind: fallbackKind.Value,
                rewriter: ErrorHandlingVariantPass.MakeNextVariantRewriter(registry: ctx.Registry),
                registry: ctx.Registry, nextOnlyPropagation: true);
            rewrittenDecl = rewrittenDecl with { Body = transformed };
        }

        return new MonomorphizedBody(
            Ast: rewrittenDecl,
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
    private RoutineInfo? BuildConcreteRoutineInfo(
        RoutineInfo genMemberRoutine,
        TypeInfo concreteOwner,
        Dictionary<string, TypeInfo> typeSubs)
    {
        // Wrapper-forwarder special case: the generic forwarder's signature came from the
        // inner-generic-def memberRoutine (e.g. List[T].getitem! returning T). Naive name-based
        // substitution using the wrapper's typeSubs would map List[T]'s T to the wrapper's
        // T-substitution (the whole inner type), not the inner's own T. Re-resolve the
        // signature against the concrete inner memberRoutine instead.
        if (genMemberRoutine is { IsSynthesized: true, WrapperForwarderInnerMemberRoutine: { } innerGenMemberRoutine }
            && concreteOwner.TypeArguments is { Count: 1 } wrapperArgs)
        {
            TypeInfo concreteInner = wrapperArgs[0];
            RoutineInfo? concreteInnerMemberRoutine = ctx.Registry.LookupMemberRoutine(
                type: concreteInner,
                memberRoutineName: innerGenMemberRoutine.Name,
                isFailable: innerGenMemberRoutine.IsFailable);
            if (concreteInnerMemberRoutine != null)
            {
                var fwdParams = concreteInnerMemberRoutine.Parameters
                    .Select(p => p.Name == "me"
                        ? p.WithSubstitutedType(newType: concreteOwner)
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
                    WrapperForwarderInnerGenericDef = genMemberRoutine.WrapperForwarderInnerGenericDef,
                    Storage = genMemberRoutine.Storage,
                    AsyncStatus = genMemberRoutine.AsyncStatus,
                    FailableVariant = genMemberRoutine.FailableVariant,
                    OriginalName = genMemberRoutine.OriginalName
                };
            }

            // The concrete inner type does not have this forwarded memberRoutine — skip it.
            return null;
        }

        var resolvedParams = genMemberRoutine.Parameters
            .Select(p =>
            {
                TypeInfo resolved = ResolveSubstitutedType(p.Type, typeSubs);
                // Final sweep: if ResolveSubstitutedType couldn't resolve a generic parameter
                // (e.g., TryGetResolution returned null for a wrapper type), fall back to a direct
                // name-based lookup in typeSubs. Post-GMP there must be no GenericParameterTypeInfo.
                if (resolved is GenericParameterTypeInfo gp &&
                    typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? directSub))
                    resolved = directSub;
                return p.WithSubstitutedType(newType: resolved);
            })
            .ToList();

        TypeInfo? resolvedReturn = genMemberRoutine.ReturnType != null
            ? ResolveSubstitutedType(genMemberRoutine.ReturnType, typeSubs)
            : null;
        if (resolvedReturn is GenericParameterTypeInfo retGp &&
            typeSubs.TryGetValue(key: retGp.Name, value: out TypeInfo? directRetSub))
            resolvedReturn = directRetSub;

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
            Storage = genMemberRoutine.Storage,
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
    /// Also converts <see cref="WrapperTypeInfo"/> to the concrete <see cref="RecordTypeInfo"/>
    /// so memberRoutine lookup and LLVM name mangling use the correct module-qualified type name.
    /// </summary>
    /// <summary>The inner X of a marker borrow protocol <c>Accessing[X]</c>/<c>Controlling[X]</c>, or null.
    /// A marker is ABI-transparent to its inner, so during monomorphization it is replaced by X — no marker
    /// protocol survives into a concrete signature, and the backend never sees a protocol-typed param.</summary>
    private static TypeInfo? MarkerInner(TypeInfo t) =>
        t is ProtocolTypeInfo { TypeArguments: [{ } inner] } p
        && RuntimeContract.IsMarkerProtocol(baseName: (p.GenericDefinition ?? p).BareName)
            ? inner
            : null;

    private TypeInfo ResolveSubstitutedType(TypeInfo type, Dictionary<string, TypeInfo> subs)
    {
        // A marker borrow protocol (directly, or reached through a substitution below) collapses to its
        // ABI-transparent inner — recurse so a still-generic inner is resolved too.
        if (MarkerInner(type) is { } directInner) return ResolveSubstitutedType(directInner, subs);

        if (subs.TryGetValue(key: type.Name, value: out TypeInfo? sub))
            return MarkerInner(sub) is { } subbedInner ? ResolveSubstitutedType(subbedInner, subs) : sub;

        // WrapperTypeInfo (e.g., Hijacked[T] or Hijacked[Core.Byte]) must always be resolved
        // to the real RecordTypeInfo so LookupMemberRoutine and LLVM mangled names work correctly.
        // Use TryGetResolution (lookup-only) -> GMP must not grow AllConcreteGenericInstances.
        // Any unresolved generic parameter left in a RoutineInfo should not reach codegen —
        // GenericAstRewriter resolves expression ResolvedType before codegen entry.
        if (type is WrapperTypeInfo wrapper)
        {
            TypeInfo? wrapperDef = ctx.Registry.LookupType(name: wrapper.Name);
            if (wrapperDef is { IsGenericDefinition: true } &&
                wrapper.TypeArguments is { Count: > 0 })
            {
                var resolvedInnerArgs = wrapper.TypeArguments
                    .Select(a => ResolveSubstitutedType(a, subs))
                    .ToList();
                return ctx.Registry.TryGetResolution(genericDef: wrapperDef,
                    typeArguments: resolvedInnerArgs) ?? type;
            }
        }

        // Tuples carry their elements in ElementTypes (not TypeArguments), so the generic-resolution
        // recursion below misses them — substitute element-wise (e.g. Tuple[U64, T] -> Tuple[U64, Text]).
        if (type is TupleTypeInfo tuple)
        {
            var subbedElems = tuple.ElementTypes
                .Select(selector: e => ResolveSubstitutedType(e, subs))
                .ToList();
            bool tupleChanged = subbedElems
                .Where(predicate: (e, i) => !ReferenceEquals(objA: e, objB: tuple.ElementTypes[index: i]))
                .Any();
            return tupleChanged ? new TupleTypeInfo(elementTypes: subbedElems) : type;
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            bool anySubstituted = false;
            var substitutedArgs = new List<TypeInfo>();
            foreach (TypeInfo arg in type.TypeArguments)
            {
                TypeInfo resolved = ResolveSubstitutedType(arg, subs);
                substitutedArgs.Add(resolved);
                if (!ReferenceEquals(resolved, arg)) anySubstituted = true;
            }
            if (anySubstituted)
            {
                TypeInfo? genericBase = GetGenericBase(type);
                if (genericBase != null)
                {
                    TypeInfo? alreadyResolved = ctx.Registry.TryGetResolution(
                        genericDef: genericBase,
                        typeArguments: substitutedArgs);
                    if (alreadyResolved != null) return alreadyResolved;
                    // Not yet registered — create it so the concrete signature reaches codegen
                    // without unresolved GenericParameterTypeInfo. Safe here because wrapper types
                    // are intercepted above (the WrapperTypeInfo branch) and never reach this path.
                    // GetOrCreateResolution also enqueues the new type for ProcessConcreteType.
                    if (substitutedArgs.All(predicate: a => a is not ErrorTypeInfo))
                        return ctx.Registry.GetOrCreateResolution(
                            genericDef: genericBase,
                            typeArguments: substitutedArgs);
                }
            }
        }

        if (type is { IsGenericDefinition: true, GenericParameters: not null, TypeArguments: null })
        {
            var typeArgs = type.GenericParameters
                .Select(gp => subs.TryGetValue(key: gp, value: out TypeInfo? s)
                    ? s
                    : ctx.Registry.LookupType(name: gp))
                .Where(t => t != null)
                .ToList();
            if (typeArgs.Count == type.GenericParameters.Count)
                return ctx.Registry.TryGetResolution(
                    genericDef: type,
                    typeArguments: typeArgs!) ?? type;
        }

        return type;
    }

    /// <summary>Builds the expected AST name for a routine on a generic type definition.</summary>
    private static string BuildAstName(TypeInfo genDef, string routineName)
    {
        if (genDef.GenericParameters is { Count: > 0 })
        {
            string paramList = string.Join(", ", genDef.GenericParameters);
            return $"{genDef.Name}[{paramList}].{routineName}";
        }
        return $"{genDef.Name}.{routineName}";
    }

    private string BuildAstNameForResolvedRoutine(RoutineInfo resolvedRoutine)
    {
        string astName;
        if (resolvedRoutine.OwnerType != null)
        {
            string ownerAstName;
            if (resolvedRoutine.GenericDefinition?.OwnerType is GenericParameterTypeInfo universalOwner)
            {
                ownerAstName = universalOwner.Name;
            }
            else
            {
                TypeInfo ownerType = resolvedRoutine.OwnerType;
                TypeInfo? ownerGenericDef = GetGenericBase(ownerType)
                    ?? (ownerType is WrapperTypeInfo ? ctx.Registry.LookupType(name: ownerType.Name) : null);
                if (ownerGenericDef?.GenericParameters is { Count: > 0 } gdParams)
                    ownerAstName = $"{ownerGenericDef.Name}[{string.Join(", ", gdParams)}]";
                else if (ownerType.IsGenericDefinition && ownerType.GenericParameters is { Count: > 0 } ownParams)
                    ownerAstName = $"{ownerType.Name}[{string.Join(", ", ownParams)}]";
                else
                    ownerAstName = ownerType.Name;
            }
            // Specialized-receiver member (e.g. List[Agent[V]].gather!): the AST decl is indexed
            // under its receiver PATTERN "List[Agent[V]]", not "List[T]". Format MeType to match.
            if (resolvedRoutine.GenericDefinition?.MeType is { } mePat)
            {
                ownerAstName = FormatReceiverPatternAstName(type: mePat);
            }
            astName = $"{ownerAstName}.{resolvedRoutine.Name}";
        }
        else
        {
            astName = resolvedRoutine.Name;
        }

        if (resolvedRoutine.GenericDefinition?.IsGenericDefinition == true)
        {
            astName += "[generic]";
        }

        return astName;
    }

    /// <summary>Formats a specialized-receiver pattern (a memberRoutine's MeType, e.g. List[Agent[V]]) into
    /// the short AST-name form used by the routine index ("List[Agent[V]]"), stripping module
    /// prefixes so it matches how the declaration's receiver was written in source.</summary>
    private string FormatReceiverPatternAstName(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo) return type.Name;
        // A Suflae entity member routine has `MeType = Roamed[E]` (SF slice 2 in SignatureResolver),
        // but its SOURCE receiver is the bare entity `E` — you never write `routine Roamed[Box[T]].get`.
        // Unwrap the Roamed handle so the AST-index name matches the declaration (`Box[T].get`),
        // otherwise the generic body is looked up under `Roamed[Box[T]].get`, never found, and codegen
        // falls back to a naive direct field access that reads the RC controller instead of the entity.
        if (type is RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed, TypeArguments: [{ } roamedInner] })
            return FormatReceiverPatternAstName(type: roamedInner);
        string baseName = type.BareName;
        int dot = baseName.LastIndexOf(value: '.');
        if (dot >= 0) baseName = baseName[(dot + 1)..];
        if (type.TypeArguments is { Count: > 0 } args)
        {
            return baseName + "[" +
                string.Join(separator: ", ", values: args.Select(selector: FormatReceiverPatternAstName)) + "]";
        }
        return baseName;
    }

    /// <summary>Recursively unifies a MeType pattern (List[Agent[V]]) against the concrete owner
    /// (List[Agent[S64]]), recording memberRoutine-generic bindings (V → S64) into <paramref name="into"/>.</summary>
    private static void UnifyMePatternIntoSubs(TypeInfo pattern, TypeInfo concrete,
        List<string>? genericParams, Dictionary<string, TypeInfo> into)
    {
        if (genericParams is not { Count: > 0 }) return;
        if (pattern is GenericParameterTypeInfo gp)
        {
            if (genericParams.Contains(item: gp.Name) && !into.ContainsKey(key: gp.Name) &&
                concrete is not GenericParameterTypeInfo)
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
                UnifyMePatternIntoSubs(pattern: pArgs[index: i], concrete: cArgs[index: i],
                    genericParams: genericParams, into: into);
            }
        }
    }

    private Dictionary<string, TypeInfo> BuildResolvedRoutineTypeSubstitutions(
        RoutineInfo resolvedRoutine)
    {
        var typeSubs = new Dictionary<string, TypeInfo>();

        // Specialized-receiver member: bind the receiver-derived memberRoutine generics (V → S64) by
        // unifying the generic definition's MeType (List[Agent[V]]) against the concrete owner
        // (List[Agent[S64]]), so the body rewriter substitutes V even though it carries no
        // memberRoutine-type-arg suffix.
        if (resolvedRoutine.GenericDefinition?.MeType is { } mePattern &&
            resolvedRoutine.OwnerType is { } resolvedOwnerForMe)
        {
            UnifyMePatternIntoSubs(pattern: mePattern, concrete: resolvedOwnerForMe,
                genericParams: resolvedRoutine.GenericDefinition.GenericParameters, into: typeSubs);
        }

        if (resolvedRoutine.GenericDefinition?.OwnerType is GenericParameterTypeInfo universalOwner &&
            resolvedRoutine.OwnerType != null)
        {
            typeSubs[universalOwner.Name] = resolvedRoutine.OwnerType;
        }

        if (resolvedRoutine.OwnerType is { TypeArguments: { Count: > 0 } } ownerType)
        {
            TypeInfo? ownerGenericDef = GetGenericBase(ownerType)
                ?? (ownerType is WrapperTypeInfo ? ctx.Registry.LookupType(name: ownerType.Name) : null);
            if (ownerGenericDef?.GenericParameters is { Count: > 0 })
            {
                for (int i = 0;
                     i < ownerGenericDef.GenericParameters.Count && i < ownerType.TypeArguments.Count;
                     i++)
                {
                    string paramName = ownerGenericDef.GenericParameters[index: i];
                    // Don't overwrite a universal-owner mapping (e.g. T->BTreeListNode[Byte] from
                    // case 1) with the owner's own type argument (e.g. T->Byte from BTreeListNode[T]).
                    // These are different uses of the same name T: the memberRoutine's universal-owner T
                    // refers to the whole owner type, not to the owner's element type.
                    if (!typeSubs.ContainsKey(key: paramName))
                        typeSubs[paramName] = ownerType.TypeArguments[index: i];
                }
            }

            // Wrapper forwarder over a generic inner type: the forwarder's parameters and body
            // reference the inner type's generic parameters (e.g. `Owned[BTreeDictNode[K,V]]`'s
            // synthesized `entries_add_last(k: K, v: V)` carries `K, V` from BTreeDictNode, not
            // from Owned). The outer ownerGenericDef.GenericParameters only knows Owned's `T`
            // — propagate the inner type's K, V substitutions too so the body rewriter can
            // resolve them when monomorphizing per concrete inner type.
            if (resolvedRoutine.WrapperForwarderInnerGenericDef is { GenericParameters: { Count: > 0 } } innerGenDef
                && ownerType.TypeArguments[index: 0] is { TypeArguments: { Count: > 0 } } innerInstance)
            {
                // Register the inner instance's type-args under their original inner-param
                // names. WrapperForwardingPass sets `ForwarderOriginalName = innerParamName`
                // on the disambiguated GenericParameterTypeInfo, and downstream substitution
                // sites (GenericAstRewriter.ResolveType, codegen.ResolveTypeSubstitution) look
                // up by that name when the disambiguated Name miss. So the map only needs the
                // original-name key — no `__rfwd_` sentinel.
                for (int i = 0;
                     i < innerGenDef.GenericParameters.Count && i < innerInstance.TypeArguments.Count;
                     i++)
                {
                    string innerParamName = innerGenDef.GenericParameters[index: i];
                    if (!typeSubs.ContainsKey(key: innerParamName))
                        typeSubs[innerParamName] = innerInstance.TypeArguments[index: i];
                }
            }
        }

        if (resolvedRoutine.GenericDefinition?.GenericParameters is { Count: > 0 } memberRoutineParams &&
            resolvedRoutine.TypeArguments is { Count: > 0 } memberRoutineTypeArgs)
        {
            for (int i = 0; i < memberRoutineParams.Count && i < memberRoutineTypeArgs.Count; i++)
            {
                string pName = memberRoutineParams[index: i];
                TypeInfo pVal = memberRoutineTypeArgs[index: i];
                if (typeSubs.TryGetValue(key: pName, value: out TypeInfo? existingOwnerValue))
                {
                    // Name collision: the owner already maps pName -> some type.
                    // When existingOwnerValue is a generic definition (TypeArguments=null),
                    // SubstituteType cannot recurse into it and returns it unchanged.
                    // Explicitly instantiate the generic def with pVal so we get a concrete type
                    // (e.g. T->BTreeListNode[T] + memberRoutine T->BuildMode -> T->BTreeListNode[BuildMode]).
                    TypeInfo newVal;
                    if (existingOwnerValue is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } innerParams } &&
                        innerParams.Any(predicate: p => p == pName))
                    {
                        var newArgs = innerParams
                            .Select(selector: p => p == pName ? pVal : (TypeInfo)new GenericParameterTypeInfo(name: p))
                            .ToList();
                        newVal = newArgs.All(predicate: a => a is not GenericParameterTypeInfo)
                            ? ctx.Registry.GetOrCreateResolution(genericDef: existingOwnerValue,
                                typeArguments: newArgs)
                            : existingOwnerValue;
                    }
                    else
                    {
                        var innerSub = new Dictionary<string, TypeInfo> { [pName] = pVal };
                        newVal = RoutineInfo.SubstituteType(type: existingOwnerValue,
                            substitution: innerSub);
                    }
                    typeSubs[pName] = newVal;
                }
                else
                {
                    typeSubs[pName] = pVal;
                }
            }
        }

        return typeSubs;
    }

    /// <summary>
    /// Searches all stdlib and user program ASTs for a routine declaration matching the given name.
    /// Uses a pre-built index for O(1) name lookup.
    /// When <paramref name="typeSubs"/> is provided, routines whose generic constraints are not
    /// satisfied by the concrete type arguments are skipped -> this prevents the record-layout
    /// overload of e.g. <c>Maybe[T]</c> from being selected when T is an entity type.
    /// </summary>
    private RoutineDeclaration? FindInStdlib(string genericAstName, int expectedParamCount = -1,
        Dictionary<string, TypeInfo>? typeSubs = null, List<string>? expectedParamNames = null,
        List<string?>? expectedParamTypeNames = null, string? expectedOwnerModule = null,
        string? expectedOwnerRealm = null)
    {
        bool requireGenericSuffix = genericAstName.EndsWith("[generic]");
        string baseName = requireGenericSuffix
            ? genericAstName[..genericAstName.IndexOf("[generic]", StringComparison.Ordinal)]
            : genericAstName;

        // Routine names (both index keys and the composed baseName) are bare — the failable `!` is
        // a structured flag (RoutineDeclaration.IsFailable), never part of the name — so a plain
        // lookup on the bare name already reaches failable overloads.
        if (!_routineIndex.TryGetValue(key: baseName, value: out List<RoutineDeclaration>? candidates))
        {
            return null;
        }

        // Module-scoped disambiguation: the same bare AST name (`List[T].add_last`) is declared in both
        // the RazorForge-realm `Core.List` and the Suflae-realm overlay `Suflae.List`. Without keying on
        // the owner module, the first-registered (Core) decl is cloned for the Suflae instantiation — so
        // `Suflae.List[S32].add_last` ran Core.List's body (its `reserve` call left the wrapper body's T
        // unsubstituted → monomorphization-incomplete crash). Prefer the candidate whose registered
        // owner module matches; fall back to the full set when none match (non-overlay types).
        if (expectedOwnerModule != null)
        {
            // Match on the type-arg-stripped owner FullName (e.g. "Suflae.List") rather than the
            // Module field: a concrete generic-instance owner (Suflae.List[Core.S32]) carries an empty
            // Module, but its FullName still embeds the realm/module prefix.
            var moduleMatched = candidates
                .Where(predicate: d => d.ResolvedInfo?.OwnerType?.FullName is { } fn
                                       && TypeInfo.StripTypeArgs(name: fn) == expectedOwnerModule)
                .ToList();
            if (moduleMatched.Count > 0) candidates = moduleMatched;
        }

        // Realm-scoped disambiguation: `Core.List` FullName is realm-FREE, so an SF-realm `List[T].m`
        // wrapper and the RF-realm `List[T].m` share owner FullName — the module filter above keeps BOTH,
        // and the first (RF) would win, cloning RF's body (e.g. `return me.count`) for the SF wrapper whose
        // real body is `return me.inner.count()`. Prefer the candidate whose owner realm matches the
        // requested owner; fall back to the full set when none match (RF compiles never narrow — RF-only).
        if (expectedOwnerRealm != null)
        {
            var realmMatched = candidates
                .Where(predicate: d => (d.ResolvedInfo?.OwnerType?.Realm ?? "RF") == expectedOwnerRealm)
                .ToList();
            if (realmMatched.Count > 0) candidates = realmMatched;
        }

        RoutineDeclaration? countOnlyMatch = null;
        RoutineDeclaration? firstMatch = null;
        foreach (RoutineDeclaration decl in candidates)
        {
            if (requireGenericSuffix && decl.GenericParameters is not { Count: > 0 }) continue;
            if (!ConstraintsSatisfied(routine: decl, subs: typeSubs)) continue;

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
                bool namesMatch = true;
                for (int i = 0; i < expectedParamNames.Count; i++)
                {
                    if (decl.Parameters[i].Name != expectedParamNames[i])
                    {
                        namesMatch = false;
                        break;
                    }
                }
                if (namesMatch)
                {
                    // Param names alone don't disambiguate same-name-different-type overloads
                    // (e.g. `create(from: Set[T])` vs `create(from: SortedSet[T])`). When type
                    // names are supplied, require those to match too. Without this gate the
                    // first-declared overload wins by source order, and Set's body ends up
                    // mounted under SortedSet's mangled signature (LINKERR on iter mismatch).
                    if (expectedParamTypeNames != null &&
                        decl.Parameters.Count == expectedParamTypeNames.Count)
                    {
                        bool typesMatch = true;
                        for (int i = 0; i < expectedParamTypeNames.Count; i++)
                        {
                            string? expected = expectedParamTypeNames[i];
                            if (expected == null) continue;
                            string? actual = decl.Parameters[i].Type?.Name;
                            if (actual == null) continue;
                            // Compare by base name (strip [T]/[K,V]) so `Set[T]` matches `Set`
                            // and `SortedSet[T]` matches `SortedSet` regardless of generic-arg form.
                            // Also unwrap a borrow/reference wrapper on the resolved side: a param
                            // declared `from: Accessing[SortedSet[T]]` carries Type.Name "SortedSet" in
                            // the AST (the wrapper is a modifier), but the resolved RoutineInfo keeps
                            // the full "Accessing[SortedSet[T]]". Without unwrapping, the right overload
                            // is rejected and FindInStdlib falls back to an arbitrary same-arity one
                            // (e.g. List's `capacity:U64` body mounted under the SortedSet creator).
                            string expectedBase = MatchableBaseName(expected);
                            string actualBase = MatchableBaseName(actual);
                            if (expectedBase != actualBase)
                            {
                                typesMatch = false;
                                break;
                            }
                        }
                        if (!typesMatch)
                        {
                            countOnlyMatch ??= decl;
                            continue;
                        }
                    }
                    return decl;
                }
                countOnlyMatch ??= decl;
                continue;
            }

            return decl;
        }

        return countOnlyMatch ?? firstMatch;
    }

    private static string StripGenericSuffix(string typeName) => TypeInfo.StripTypeArgs(name: typeName);

    /// <summary>Borrow/reference wrapper type names. A parameter declared with one of these
    /// (e.g. <c>from: Accessing[SortedSet[T]]</c>) reaches the AST as the bare inner type
    /// (<c>SortedSet</c>), while the resolved <see cref="RoutineInfo"/> keeps the full wrapper —
    /// so overload disambiguation must compare the inner type, not the wrapper.</summary>
    private static readonly HashSet<string> BorrowWrapperNames = new(StringComparer.Ordinal)
    {
        RuntimeContract.Accessing, RuntimeContract.Viewing, RuntimeContract.Controlling, RuntimeContract.Modifying, RuntimeContract.Hijacked,
        RuntimeContract.Consulting, RuntimeContract.Amending, RuntimeContract.Retained, RuntimeContract.Tracked, RuntimeContract.Guarded
    };

    /// <summary>Base type name for overload matching: strips generic args and unwraps a leading
    /// borrow/reference wrapper to its inner type (recursively), so <c>Accessing[SortedSet[T]]</c>
    /// and the AST's bare <c>SortedSet</c> compare equal.</summary>
    private static string MatchableBaseName(string typeName)
    {
        string baseName = StripGenericSuffix(typeName);
        if (BorrowWrapperNames.Contains(baseName)
            && TypeInfo.ExtractTypeArgsString(name: typeName) is { } inner)
            return MatchableBaseName(inner);
        return baseName;
    }

    /// <summary>
    /// Returns true if all explicit generic constraints on <paramref name="routine"/> are
    /// satisfied by the concrete type substitutions in <paramref name="subs"/>.
    /// </summary>
    private static bool ConstraintsSatisfied(RoutineDeclaration routine,
        Dictionary<string, TypeInfo>? subs)
    {
        if (routine.GenericConstraints is not { Count: > 0 }) return true;
        if (subs == null || subs.Count == 0) return true;

        foreach (GenericConstraintDeclaration c in routine.GenericConstraints)
        {
            if (!subs.TryGetValue(key: c.ParameterName, value: out TypeInfo? actual)) continue;
            bool ok = c.ConstraintType switch
            {
                ConstraintKind.RecordType     => IsRecordLike(actual),
                ConstraintKind.EntityType => actual is EntityTypeInfo,
                ConstraintKind.ChoiceType    => actual is ChoiceTypeInfo,
                ConstraintKind.FlagsType     => actual is FlagsTypeInfo,
                ConstraintKind.VariantType   => actual is VariantTypeInfo,
                ConstraintKind.Crashable     => actual is CrashableTypeInfo,
                ConstraintKind.ConstGeneric  => CheckStructuralConstGeneric(c: c, actual: actual),
                _                            => true, // Obeys/TypeEquality: trust SA
            };
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Checks a <see cref="ConstraintKind.ConstGeneric"/> constraint that may encode a structural
    /// type-category requirement.
    /// </summary>
    private static bool CheckStructuralConstGeneric(GenericConstraintDeclaration c, TypeInfo actual)
    {
        string? typeName = c.ConstraintTypes is { Count: > 0 } ? c.ConstraintTypes[0].Name : null;
        return typeName switch
        {
            "RecordType"  => IsRecordLike(actual),
            "EntityType"  => actual is EntityTypeInfo,
            "ChoiceType"  => actual is ChoiceTypeInfo,
            "FlagsType"   => actual is FlagsTypeInfo,
            "VariantType" => actual is VariantTypeInfo,
            "Crashable"   => actual is CrashableTypeInfo,
            _             => true
        };
    }

    /// Wrappers (Owned, Retained, Hijacked, ...) are declared with `record` syntax and behave as
    /// record-category types for constraint purposes; they are tracked as WrapperTypeInfo for layout
    /// reasons but a `needs T is RecordType` constraint must still accept them.
    private static bool IsRecordLike(TypeInfo type) =>
        type is RecordTypeInfo || type is WrapperTypeInfo;

    /// <summary>Wraps a pre-built body statement in a minimal shell RoutineDeclaration.</summary>
    private static RoutineDeclaration WrapInShellDecl(
        string name, Statement body, RoutineInfo info)
    {
        return new RoutineDeclaration(
            Name: name,
            Parameters: [],
            ReturnType: null,
            Body: body,
            Visibility: VisibilityModifier.Open,
            Annotations: [],
            Location: info.Location ?? new SourceLocation("", 0, 0, 0));
    }
    // Type helpers (no codegen dependency)

    /// <summary>
    /// Returns true when a type contains unresolved generic parameters at any nesting depth,
    /// or is itself a generic definition (free type params, no TypeArguments).
    /// Used to skip wrapper instances whose inner type still has free type params.
    /// </summary>
    private static bool HasUnresolvedTypeArgs(TypeInfo t)
    {
        if (t is GenericParameterTypeInfo or ErrorTypeInfo) return true;
        if (t.IsGenericDefinition) return true;
        if (t.TypeArguments is not { Count: > 0 } args) return false;
        return args.Any(predicate: HasUnresolvedTypeArgs);
    }

    private static TypeInfo? GetGenericBase(TypeInfo type) => type switch
    {
        RecordTypeInfo { GenericDefinition: { } d } => d,
        EntityTypeInfo { GenericDefinition: { } d } => d,
        ProtocolTypeInfo { GenericDefinition: { } d } => d,
        _ => null
    };

    private static string? GetGenericBaseName(TypeInfo type) => GetGenericBase(type)?.Name;

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
            if (body.Ast?.Body == null) continue;
            ScanStatementForMemberRoutineGenericCalls(body.Ast.Body);
        }
    }

    private void ScanStatementForMemberRoutineGenericCalls(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                foreach (Statement s in b.Statements) ScanStatementForMemberRoutineGenericCalls(s);
                break;
            case IfStatement ifs:
                ScanExprForMemberRoutineGenericCalls(ifs.Condition);
                ScanStatementForMemberRoutineGenericCalls(ifs.ThenStatement);
                if (ifs.ElseStatement != null) ScanStatementForMemberRoutineGenericCalls(ifs.ElseStatement);
                break;
            case WhileStatement w:
                ScanExprForMemberRoutineGenericCalls(w.Condition);
                ScanStatementForMemberRoutineGenericCalls(w.Body);
                break;
            case LoopStatement loop:
                ScanStatementForMemberRoutineGenericCalls(loop.Body);
                break;
            case EachStatement f:
                ScanExprForMemberRoutineGenericCalls(f.Iterable);
                ScanStatementForMemberRoutineGenericCalls(f.Body);
                break;
            case WhenStatement ws:
                ScanExprForMemberRoutineGenericCalls(ws.Expression);
                foreach (WhenClause c in ws.Clauses) ScanStatementForMemberRoutineGenericCalls(c.Body);
                break;
            case ReturnStatement { Value: { } rv }:
                ScanExprForMemberRoutineGenericCalls(rv);
                break;
            case AssignmentStatement assign:
                ScanExprForMemberRoutineGenericCalls(assign.Target);
                ScanExprForMemberRoutineGenericCalls(assign.Value);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: { } vi } }:
                ScanExprForMemberRoutineGenericCalls(vi);
                break;
            case ExpressionStatement es:
                ScanExprForMemberRoutineGenericCalls(es.Expression);
                break;
            case DiscardStatement ds:
                ScanExprForMemberRoutineGenericCalls(ds.Expression);
                break;
            case ThrowStatement ts:
                ScanExprForMemberRoutineGenericCalls(ts.Error);
                break;
            case VariantReturnStatement { Value: { } vv }:
                ScanExprForMemberRoutineGenericCalls(vv);
                break;
            case BecomesStatement bst:
                ScanExprForMemberRoutineGenericCalls(bst.Value);
                break;
            case DangerStatement danger:
                ScanStatementForMemberRoutineGenericCalls(danger.Body);
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
                ScanExprForMemberRoutineGenericCalls(bin.Left);
                ScanExprForMemberRoutineGenericCalls(bin.Right);
                break;
            case UnaryExpression un:
                ScanExprForMemberRoutineGenericCalls(un.Operand);
                break;
            case MemberExpression mem:
                ScanExprForMemberRoutineGenericCalls(mem.Object);
                break;
            case NamedArgumentExpression named:
                ScanExprForMemberRoutineGenericCalls(named.Value);
                break;
            case TypeConversionExpression conv:
                ScanExprForMemberRoutineGenericCalls(conv.Expression);
                break;
            case IndexExpression idx:
                ScanExprForMemberRoutineGenericCalls(idx.Object);
                ScanExprForMemberRoutineGenericCalls(idx.Index);
                RegisterIndexAccessorGenericResolutions(idx: idx);
                break;
            case CreatorExpression creator:
                foreach (var (_, v) in creator.MemberVariables) ScanExprForMemberRoutineGenericCalls(v);
                break;
            case ConditionalExpression cond:
                ScanExprForMemberRoutineGenericCalls(cond.Condition);
                ScanExprForMemberRoutineGenericCalls(cond.TrueExpression);
                ScanExprForMemberRoutineGenericCalls(cond.FalseExpression);
                break;
        }
    }

    /// <summary>
    /// Registers a memberRoutine-generic resolution for a call that targets a memberRoutine with
    /// unresolved memberRoutine-level generic params, then recurses into the callee and arguments.
    /// </summary>
    private void ScanCallForMemberRoutineGenericCalls(CallExpression call)
    {
        if (call.ResolvedRoutine is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } genParams } memberRoutine)
        {
            TypeInfo?[] inferred = InferMemberRoutineTypeArgsFromCall(memberRoutine, genParams, call);
            if (inferred.Length == genParams.Count && inferred.All(t => t != null))
            {
                ctx.Registry.GetOrCreateRoutineResolution(
                    genericDef: memberRoutine,
                    typeArguments: inferred.Cast<TypeInfo>().ToList());
            }
        }
        ScanExprForMemberRoutineGenericCalls(call.Callee);
        foreach (Expression arg in call.Arguments) ScanExprForMemberRoutineGenericCalls(arg);
    }

    /// <summary>
    /// Registers memberRoutine-generic getitem!/setitem! resolutions from an IndexExpression node.
    /// OperatorLoweringPass doesn't run on InstantiatedGenericBodies, so getitem/setitem calls in
    /// those bodies are still IndexExpression rather than CallExpression.
    /// </summary>
    private void RegisterIndexAccessorGenericResolutions(IndexExpression idx)
    {
        if (idx.Object.ResolvedType is not { } idxObjType ||
            idx.Index.ResolvedType is not ({ } idxIdxType and not GenericParameterTypeInfo))
        {
            return;
        }

        RoutineInfo? getItem = ctx.Registry.LookupMemberRoutine(
            type: idxObjType, memberRoutineName: "getitem", isFailable: true);
        if (getItem is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
        {
            ctx.Registry.GetOrCreateRoutineResolution(
                genericDef: getItem,
                typeArguments: [idxIdxType]);
        }
        RoutineInfo? setItem = ctx.Registry.LookupMemberRoutine(
            type: idxObjType, memberRoutineName: "setitem", isFailable: true);
        if (setItem is { IsGenericDefinition: true, GenericParameters.Count: > 0 })
        {
            ctx.Registry.GetOrCreateRoutineResolution(
                genericDef: setItem,
                typeArguments: [idxIdxType]);
        }
    }

    /// <summary>
    /// Infers memberRoutine-level type arguments by matching GenericParameterTypeInfo params
    /// against the corresponding call-site argument ResolvedTypes.
    /// </summary>
    private static TypeInfo?[] InferMemberRoutineTypeArgsFromCall(
        RoutineInfo memberRoutine, List<string> genParams, CallExpression call)
    {
        var result = new TypeInfo?[genParams.Count];

        foreach (ParameterInfo param in memberRoutine.Parameters)
        {
            if (param.Name == "me") continue;
            if (param.Type is not GenericParameterTypeInfo gp) continue;

            int idx = genParams.IndexOf(item: gp.Name);
            if (idx < 0 || result[idx] != null) continue;

            result[idx] = FindArgTypeForParam(call: call, paramName: param.Name);
        }

        return result;
    }

    /// <summary>
    /// Finds the ResolvedType of the argument bound to <paramref name="paramName"/> in the call,
    /// skipping arguments whose type is still an unresolved generic parameter.
    /// </summary>
    private static TypeInfo? FindArgTypeForParam(CallExpression call, string paramName)
    {
        foreach (Expression arg in call.Arguments)
        {
            Expression argValue = arg is NamedArgumentExpression nae && nae.Name == paramName
                ? nae.Value
                : arg;
            TypeInfo? argType = argValue.ResolvedType;
            if (argType != null && argType is not GenericParameterTypeInfo)
            {
                return argType;
            }
        }
        return null;
    }
}
