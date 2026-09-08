using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification;
using TypeInfo = TypeModel.Types.TypeInfo;

using Compiler.Instantiation;

namespace Compiler.Collection.Passes;

/// <summary>
/// Strategy-B: builds the live-routine set by BFS from program entry points
/// (<c>start()</c>, <c>@test</c>, <c>@bench</c>) over <c>CallExpression.ResolvedRoutine</c> and
/// <c>GenericMemberRoutineCallExpression.ResolvedRoutine</c>. Tracks per-frame type substitutions so
/// generic-def call sites (e.g. <c>me.insertion_sort(...)</c> inside <c>List[T].sort</c>) resolve
/// to the correct concrete callee (<c>List[Text].insertion_sort</c>) for the calling
/// concrete instantiation.
/// </summary>
internal sealed class RoutineReachabilityPass(InstantiationContext ctx)
{
    private const string CreateMemberRoutineName = "create";
    private const string RepresentMemberRoutineName = RuntimeContract.Display.Represent;
    private const string DiagnoseMemberRoutineName = RuntimeContract.Display.Diagnose;
    private const string DestroyMemberRoutineName = "destroy";
    private const string SerializeMemberRoutineName = RuntimeContract.Serialize;

    private readonly HashSet<string> _live = new(comparer: StringComparer.Ordinal);
    private readonly HashSet<string> _visited = new(comparer: StringComparer.Ordinal);
    private readonly Queue<Frame> _worklist = new();
    private readonly HashSet<TypeInfo> _liveOwnerTypes =
        new(comparer: ReferenceEqualityComparer.Instance);

    private readonly Dictionary<string, List<RoutineDeclaration>> _userByName = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, List<RoutineDeclaration>> _stdlibByName = new(comparer: StringComparer.Ordinal);

    // Stdlib routine decls (by reference) — the ONLY decls whose body scan is cached cross-run in
    // ctx.BodyScanCache. User decls are new objects every compile (an edit reparses) and cheap to walk,
    // so they are never cached, keeping the shared cache bounded and free of stale results.
    private readonly HashSet<RoutineDeclaration> _stdlibDecls = new(comparer: ReferenceEqualityComparer.Instance);

    // Per-frame local variable type map (params + var decls). Used by ResolveMemberCall and
    // ResolveCallStyleConstructor to recover receiver types in stdlib bodies where SA didn't
    // populate ResolvedType on identifier expressions.
    private Dictionary<string, TypeInfo> _localTypes = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Generic-parameter substitutions active for the currently-processing frame. Used by
    /// <see cref="ResolveMemberCall"/> when a call's receiver type is a generic param
    /// (e.g. `value: T` inside the body of `show[T=S16]`): we look up the memberRoutine on the
    /// concrete substituted type, not the bare generic param.
    /// </summary>
    private Dictionary<string, TypeInfo> _currentFrameSubs = new(comparer: StringComparer.Ordinal);
    private TypeInfo? _meType;

    private readonly record struct Frame(RoutineInfo Routine, RoutineDeclaration Decl,
        Dictionary<string, TypeInfo> TypeSubs);

    public void Run()
    {
        BuildAstIndices();
        SeedFromEntryPoints();
        if (ctx.SeedAllStdlibRoutines) SeedAllConcreteStdlibRoutines();
        Drain();
        // STAGE 3 (RETIRED): force-seeding — force-marking every wired / implicit-contract / self-free /
        // iterator-adapter routine on every live owner (the old `SeedWiredRoutinesOnLiveTypes`, now DELETED) —
        // is owned END TO END by the demand collector (`RoutineCollectionPass` → `GenericMonomorphizationPass.
        // CollectReferencedInIsolation`'s ForceSeedOwner + PDIL fixpoint). The collector materializes AND lists
        // (LiveRoutineKeys/LiveOwnerTypeNames) exactly the referenced set, so reachability no longer needs to
        // over-approximate it here. Proven: full suite green with the seed retired (both header emitters key
        // linkage on a DETERMINISTIC structural signal, so cold==warm). This removes the push/pull divergence
        // that force-seeding's over-approximation created. The prior re-seed-until-fixpoint loop existed only
        // to rerun the seed on owners discovered mid-drain; with no seed, one Drain reaches fixpoint.
        // DEMAND path (normal build): the collector (RoutineCollectionPass → CollectReferencedInIsolation)
        // is the SOLE authority for LiveRoutineKeys/LiveOwnerTypeNames — it lists EXACTLY the referenced
        // closure. This pass over-approximates (it walks generic-DEF bodies and can't see which concrete
        // branches codegen prunes), so publishing ITS live sets pollutes the shared set with owners the
        // demand walk never reaches — e.g. a decimal↔integer conversion `S64(from: D128)` in a generic
        // numeric body marks D128 a live owner, and MaterializePerOwnerSynthesizedBodies then emits
        // D128.represent → the whole bignum/decimal cluster in an S64-only program. Publish only in BASE
        // mode (SeedAllStdlibRoutines), which must define every stdlib instance regardless of reachability.
        if (ctx.SeedAllStdlibRoutines)
        {
            foreach (string key in _live) ctx.LiveRoutineKeys.Add(item: key);
            foreach (TypeInfo owner in _liveOwnerTypes) ctx.LiveOwnerTypeNames.Add(item: owner.FullName);
        }

        string? dumpPath = Compiler.Diagnostics.DiagnosticFlags.ReachabilityDump;
        if (!string.IsNullOrEmpty(value: dumpPath))
        {
            var lines = new List<string> { "=== LIVE ROUTINES ===" };
            lines.AddRange(collection: _live.OrderBy(keySelector: s => s));
            lines.Add(item: "=== LIVE OWNER TYPES ===");
            lines.AddRange(collection: _liveOwnerTypes.Select(selector: t => t.FullName).OrderBy(keySelector: s => s));
            File.WriteAllLines(path: dumpPath, contents: lines);
        }
    }

    private void BuildAstIndices()
    {
        foreach ((Program program, _, string module) in ctx.UserPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                AddDecl(map: _userByName, name: decl.QualifiedName, decl: decl);
                IndexCreatorDecl(map: _userByName, decl: decl);
                // Also index MODULE-LEVEL routines under their module-qualified name so FindDecl can
                // disambiguate same-named routines across modules (e.g. each imported test module's
                // `start`). Members keep the bare index (their harness contamination is a separate
                // follow-up — module-qualified member indexing regressed generic-instance liveness).
                if (!string.IsNullOrEmpty(value: module) && decl.MemberRoutineName is null)
                {
                    AddDecl(map: _userByName, name: $"{module}.{decl.Name}", decl: decl);
                }
            }
        }

        foreach ((Program program, _, string module) in ctx.Registry.StdlibPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                _stdlibDecls.Add(item: decl);
                AddDecl(map: _stdlibByName, name: decl.QualifiedName, decl: decl);
                IndexCreatorDecl(map: _stdlibByName, decl: decl);
                // Imported project modules are carried here too; index MODULE-LEVEL routines under the
                // module-qualified name so FindDecl can disambiguate same-named routines across modules
                // (see the user-index note above).
                if (!string.IsNullOrEmpty(value: module) && decl.MemberRoutineName is null)
                {
                    AddDecl(map: _stdlibByName, name: $"{module}.{decl.Name}", decl: decl);
                }
            }
        }
    }

    /// <summary>
    /// A constructor `routine T(...)` / `routine T[params](...)` is registered with the canonical
    /// creator name "create" on its owner type (ResolvedInfo), but its AST decl.Name is just the bare
    /// base type ("CStr", "List"). FindDecl resolves a creator callee under "Owner.create" (concrete)
    /// or "Owner[params].create" (generic def). Index those keys here so the walk finds the
    /// constructor body — otherwise routines called only inside constructor bodies get over-pruned.
    /// </summary>
    private static void IndexCreatorDecl(Dictionary<string, List<RoutineDeclaration>> map,
        RoutineDeclaration decl)
    {
        if (decl.ResolvedInfo is not { Name: "create", OwnerType: { } owner }) return;
        AddDecl(map: map, name: $"{owner.Name}.create", decl: decl);
        if (owner.GenericParameters is { Count: > 0 } gps)
        {
            AddDecl(map: map, name: $"{owner.Name}[{string.Join(separator: ", ", values: gps)}].create",
                decl: decl);
        }
    }

    private static void AddDecl(Dictionary<string, List<RoutineDeclaration>> map, string name,
        RoutineDeclaration decl)
    {
        if (!map.TryGetValue(key: name, value: out List<RoutineDeclaration>? list))
        {
            list = new List<RoutineDeclaration>();
            map[key: name] = list;
        }
        list.Add(item: decl);
    }

    private void SeedFromEntryPoints()
    {
        foreach ((Program program, _, string module) in ctx.UserPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                bool isEntry = decl.Name == "start" ||
                               decl.Annotations.Any(predicate: a => a == "test" || a == "bench");
                if (!isEntry) continue;

                // Resolve the module-qualified routine so each module's entry maps to its OWN
                // RoutineInfo. LookupRoutineByName(bare) returns a first-wins single entry, so with
                // several modules defining `start` (e.g. a harness importing many test modules) it
                // would both seed the wrong root AND pair a mismatched (info, decl) — walking one
                // module's body under another's RoutineInfo — pruning the real entry's callees.
                RoutineInfo? info = (!string.IsNullOrEmpty(value: module)
                                        ? ctx.Registry.LookupRoutine(
                                            fullName: $"{module}.{decl.QualifiedName}")
                                        : null)
                                    ?? ctx.Registry.LookupRoutineByName(name: decl.QualifiedName);
                if (info == null) continue;
                Enqueue(routine: info, decl: decl, typeSubs: new Dictionary<string, TypeInfo>());
            }
        }

        SeedRuntimeSentinels();
    }

    /// <summary>
    /// Force-seeds runtime sentinel primitives that the compiler/runtime rely on but that reach the live
    /// set only TRANSITIVELY through another routine's body — so a substituted multi-level generic call
    /// chain can drop them and over-prune a routine codegen still emits. <c>cptr_none()</c> is the CPtr
    /// null sentinel: it is called from <c>CPtr.is_none</c>, wired as the default RoamController trace/free
    /// hook, and passed as coroutine <c>userdata</c>. It was reached only via <c>Agent.destroy</c>'s
    /// <c>me.coro.is_none()</c> (always concretely seeded by ScopeTeardownLoweringPass); routing that
    /// <c>is_none</c> through a generic member chain (e.g. <c>List[Agent[V]].race! → Agent[V].reap →
    /// CPtr.is_none → cptr_none</c>) dropped the seed. It is a tiny always-defined leaf, so seeding it
    /// unconditionally is free and removes the fragility.
    /// </summary>
    /// <summary>
    /// Roots EVERY concrete stdlib routine so the BFS + monomorphization materialize the full stdlib generic
    /// closure — used when building a precompiled stdlib base that must DEFINE everything it references, not
    /// just what an entry program reaches. Generic-definition routines are skipped (a def body's calls stay
    /// abstract from a root frame with no type substitutions; concrete instances materialize from concrete
    /// callers). try_/check_/lookup_ variant clones (<c>OriginalName != null</c>) are skipped too — their
    /// owner is a non-live monomorphized variant that trips <see cref="EnqueueCallee"/>'s owner-FullName
    /// fallback; they materialize transitively from the routines that call them.
    /// </summary>
    private void SeedAllConcreteStdlibRoutines()
    {
        foreach (RoutineInfo r in ctx.Registry.GetAllRoutines(requireLive: false).ToList())
        {
            if (r.IsGenericDefinition) continue;
            if (r.OwnerType is { IsGenericDefinition: true }) continue;
            if (r.OriginalName != null) continue;
            EnqueueCallee(callee: r);
        }

        // NOTE: do NOT blanket-mark every AllConcreteGenericInstances as a live owner here — that pulls in
        // genuinely-underivable instances (e.g. Array[SerialValue,63].assign, SerialValue's structural
        // Assignable satisfied only through its own arms) and CRASHES the base build on the abstract-derive
        // guard. Tried + reverted (see .claude-memory/base-completeness-const-generic-array-gap.md): the
        // enumeration crashes; adding a base-mode drop-on-abstract instead runs the closure away (>1400s).
        // The 687-symbol const-generic/self-free tail is left for the DELTA to define (base∪delta coverage
        // is already proven by GenerateBase_And_Delta_CoverPrunedBuild_WithTinyDelta).
    }

    private void SeedRuntimeSentinels()
    {
        if (ctx.Registry.LookupRoutineByName(name: "cptr_none") is { } cptrNone)
            EnqueueCallee(callee: cptrNone);
    }

    private void Enqueue(RoutineInfo routine, RoutineDeclaration decl,
        Dictionary<string, TypeInfo> typeSubs)
    {
        string key = routine.RegistryKey;
        if (routine.OwnerType is { } owner) _liveOwnerTypes.Add(item: owner);
        if (_live.Add(item: key))
        {
            ExpandSyntheticSiblings(callee: routine);
        }
        string visitKey = $"{key}|{string.Join(separator: ",", values: typeSubs.Select(selector: kv => $"{kv.Key}={kv.Value.FullName}"))}";
        if (!_visited.Add(item: visitKey)) return;
        _worklist.Enqueue(item: new Frame(Routine: routine, Decl: decl, TypeSubs: typeSubs));
    }

    private void Drain()
    {
        while (_worklist.Count > 0)
        {
            Frame frame = _worklist.Dequeue();
            ProcessFrame(frame: frame);
        }
    }

    private void ProcessFrame(Frame frame)
    {
        // Set up per-frame inference scope: parameter types + receiver type for `me`.
        _localTypes = BuildLocalTypes(decl: frame.Decl, typeSubs: frame.TypeSubs);
        _meType = frame.Routine.OwnerType;
        _currentFrameSubs = frame.TypeSubs;

        // Collect the body's call-like nodes AND its var-decl types. Both are INVARIANT (computed
        // without the frame's typeSubs — see CollectCallsAndLocalVarTypes), so for a stdlib body we cache
        // the result by decl reference and reuse it across every frame/warm-run that reaches this body,
        // skipping the AST walk (the biggest single RRP cost). Per-frame resolution/substitution below is
        // untouched, so the live set is identical. `_localTypes` already holds the frame's parameter types
        // (from BuildLocalTypes); we merge the (invariant) var-decl types on top, exactly as the single
        // fused walk did. The cached call list is COPIED before param-default calls are appended, so the
        // shared cache is never mutated.
        List<object> calls;
        Dictionary<RoutineDeclaration, RoutineBodyScan>? scanCache = ctx.BodyScanCache;
        if (scanCache != null && scanCache.TryGetValue(key: frame.Decl, value: out RoutineBodyScan? cachedScan))
        {
            // Merge the cached (invariant) var-decl types on top of the frame's params, exactly as the
            // fused walk did; copy the cached call list so appending param-default calls can't mutate it.
            foreach (KeyValuePair<string, TypeInfo> kv in cachedScan.VarDeclTypes) _localTypes[key: kv.Key] = kv.Value;
            calls = new List<object>(collection: cachedScan.Calls);
        }
        else
        {
            // Walk into `_localTypes` directly (so InferExpressionType sees earlier vars mid-walk — the
            // original behavior) while ALSO capturing just the var-decl entries for the cache.
            calls = new List<object>();
            Dictionary<string, TypeInfo>? captured =
                scanCache != null && _stdlibDecls.Contains(item: frame.Decl)
                    ? new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal)
                    : null;
            CollectCallsAndLocalVarTypes(node: frame.Decl.Body, sink: calls, map: _localTypes, captured: captured);
            if (captured != null)
                scanCache![key: frame.Decl] =
                    new RoutineBodyScan(Calls: new List<object>(collection: calls), VarDeclTypes: captured);
        }

        // Parameter default values never appear in any routine body (they are filled at call sites),
        // so walk them here too. A collection-literal default (e.g. `d: Dict[K,V] = {:}`) must seed
        // the collection's create/add, since codegen constructs it inline at the call site
        // (TryEmitEmptyCollectionDefault) and would otherwise reference a pruned routine. Default
        // values are never SA-analyzed, so a collection-literal default has a null ResolvedType —
        // stamp it from the parameter's resolved type so EnqueueImplicitLoweringCallees can resolve
        // the collection's create.
        foreach (var defParam in frame.Decl.Parameters)
        {
            if (defParam.DefaultValue == null) continue;
            if (defParam.DefaultValue is ListLiteralExpression or SetLiteralExpression
                    or DictLiteralExpression
                && defParam.DefaultValue.ResolvedType == null
                && defParam.Type?.ResolvedType != null)
            {
                defParam.DefaultValue.ResolvedType = defParam.Type.ResolvedType;
            }
            CollectCalls(node: defParam.DefaultValue, sink: calls);
        }

        foreach (object node in calls)
        {
            ProcessFrameCallNode(node: node, frame: frame);
        }

        // Variant bodies (synthesized represent / try_emit / wrapper forwarders) for this routine —
        // walk if present, using the same typeSubs.
        if (ctx.VariantBodies.TryGetValue(key: frame.Routine.RegistryKey, out Statement? variantBody))
        {
            var variantCalls = new List<object>();
            CollectCalls(node: variantBody, sink: variantCalls);
            foreach (object node in variantCalls)
            {
                ProcessFrameVariantCallNode(node: node, frame: frame);
            }
        }
    }

    /// <summary>
    /// Resolves one call-like node from the primary body scan and seeds its callee edges. Mirrors the
    /// synthesized-body handling but also walks roam-hook lowering and f-string interpolations.
    /// </summary>
    private void ProcessFrameCallNode(object node, Frame frame)
    {
        if (node is ThrowStatement throwStmt)
        {
            EnqueueThrowCrashMessage(throwStmt: throwStmt);
            return;
        }
        if (node is ListLiteralExpression or SetLiteralExpression or DictLiteralExpression or IndexExpression or UsingStatement or UnaryExpression { Operator: UnaryOperator.ForceUnwrap } or BinaryExpression)
        {
            EnqueueImplicitLoweringCallees(node: node, typeSubs: frame.TypeSubs);
            return;
        }
        if (node is InsertedTextExpression inserted)
        {
            EnqueueFStringCallees(inserted: inserted, typeSubs: frame.TypeSubs);
            return;
        }
        if (node is CrashableDispatchExpression dispatch)
        {
            EnqueueCrashableDispatchTargets(dispatch: dispatch);
            return;
        }
        RoutineInfo? resolved = ResolveFrameNode(node: node, frame: frame);
        if (resolved == null)
        {
            if (node is CallExpression indirectCe) NoteIfIndirectCall(caller: frame.Routine, ce: indirectCe);
            return;
        }

        RoutineInfo concreteCallee = SubstituteRoutine(routine: resolved, typeSubs: frame.TypeSubs);
        EnqueueCallee(callee: concreteCallee);
        RecordSuspendEdge(caller: frame.Routine, callee: concreteCallee);
        EnqueueRoamHookIfNeeded(node: node, callee: concreteCallee, typeSubs: frame.TypeSubs);

        // Pure synthesized represent / try_emit / wrapper-forwarders also have call sites
        // we may need to walk later; their bodies live in VariantBodies under the generic-def
        // key and are scanned via the synthesized-AST handling below.
    }

    /// <summary>
    /// Resolves one call-like node from a variant body scan and seeds its callee edges.
    /// </summary>
    private void ProcessFrameVariantCallNode(object node, Frame frame)
    {
        if (node is ThrowStatement throwStmt)
        {
            EnqueueThrowCrashMessage(throwStmt: throwStmt);
            return;
        }
        if (node is ListLiteralExpression or SetLiteralExpression or DictLiteralExpression or IndexExpression or UsingStatement or UnaryExpression { Operator: UnaryOperator.ForceUnwrap } or BinaryExpression)
        {
            EnqueueImplicitLoweringCallees(node: node, typeSubs: frame.TypeSubs);
            return;
        }
        if (node is CrashableDispatchExpression dispatch)
        {
            EnqueueCrashableDispatchTargets(dispatch: dispatch);
            return;
        }
        RoutineInfo? resolved = ResolveFrameNode(node: node, frame: frame);
        if (resolved == null)
        {
            if (node is CallExpression indirectCe) NoteIfIndirectCall(caller: frame.Routine, ce: indirectCe);
            return;
        }
        RoutineInfo variantCallee = SubstituteRoutine(routine: resolved, typeSubs: frame.TypeSubs);
        EnqueueCallee(callee: variantCallee);
        RecordSuspendEdge(caller: frame.Routine, callee: variantCallee);
    }

    /// <summary>
    /// The shared call-node → RoutineInfo resolution ladder used by both the primary and variant
    /// body scans: protocol-dispatch retarget, then SA-resolved routine, then the constructor /
    /// member / routine-value fallbacks.
    /// </summary>
    private RoutineInfo? ResolveFrameNode(object node, Frame frame)
    {
        return node switch
        {
            CallExpression ce => RetargetProtocolDispatch(ce: ce) ?? ce.ResolvedRoutine ?? ResolveNoArgConstructor(ce: ce) ?? ResolveCallStyleConstructor(ce: ce) ?? ResolveMemberCall(ce: ce),
            GenericMemberRoutineCallExpression gce => gce.ResolvedRoutine,
            CreatorExpression cre => ResolveCreatorRoutine(cre: cre),
            IdentifierExpression id => ResolveRoutineValueRef(id: id, frame: frame),
            _ => null
        };
    }

    /// <summary>
    /// Resolves and enqueues the implicit memberRoutine callees that Phase 8 lowering will introduce
    /// for collection literals and index-access expressions. Reachability runs BEFORE Phase 8
    /// (ExpressionLoweringPass / OperatorLoweringPass), so without this hook the resulting
    /// add_last / add / getitem! / setitem! calls would be unreachable and GMP would skip
    /// emitting their bodies — producing linker errors at the lowered call sites.
    /// </summary>
    private void EnqueueImplicitLoweringCallees(object node, Dictionary<string, TypeInfo> typeSubs)
    {
        // A collection literal whose type `obeys ListLiteral/SetLiteral/DictLiteral` lowers to a
        // `Type.from_literal[K](...)` call (see ExpressionLoweringPass); seed that monomorphized builder.
        // Its body (`create() + add`/`add_last`) is walked when the worklist processes it, so the add
        // member routines get seeded transitively. This is checked FIRST — before the ResolvedType-based
        // branches below — because a builder folded onto the literal by a late pass (e.g. the BuilderQuery
        // reflection fold's `from_literal(Array[Text, N])`) may carry NO ResolvedType, which would otherwise
        // hit the `rawType == null` early-return and drop the seed → codegen emits the call but reachability
        // never enlivens the definition → "declared and called but never defined" over-prune at link.
        if (node is Expression { ResolvedLiteralBuilder: { } literalBuilder })
        {
            EnqueueCallee(callee: literalBuilder);
            return;
        }

        // BinaryExpression handled separately — OperatorLoweringPass (Phase 8) lowers
        // `a op b` to `a.MemberRoutine(b)`, but reachability runs in Phase 7 before that.
        if (node is BinaryExpression bin)
        {
            EnqueueBinaryOperatorCallee(bin: bin, typeSubs: typeSubs);
            return;
        }

        TypeInfo? rawType = node switch
        {
            ListLiteralExpression l => l.ResolvedType,
            SetLiteralExpression s => s.ResolvedType,
            DictLiteralExpression d => d.ResolvedType,
            IndexExpression ix => ix.Object.ResolvedType,
            UsingStatement u => u.Resource.ResolvedType,
            UnaryExpression { Operator: UnaryOperator.ForceUnwrap } fu => fu.Operand.ResolvedType,
            _ => null
        };
        if (rawType == null) return;

        // Substitute generic parameters from the enclosing frame so List[T] inside a List[T]
        // routine becomes the concrete instantiation (e.g. List[S64]) when typeSubs has T → S64.
        TypeInfo concreteType = RoutineInfo.SubstituteType(type: rawType, substitution: typeSubs);

        // Unwrap Owned/Retained/Tracked — these are RecordTypeInfo (declared `record T`),
        // NOT WrapperTypeInfo. Match by base name + TypeArguments[0] so we get the inner
        // collection type the lowering will actually call memberRoutines on.
        TypeInfo collectionType = concreteType;
        while (collectionType.TypeArguments is { Count: 1 } args
               && GetCollectionBaseNameForReachability(collectionType) is RuntimeContract.Owned or RuntimeContract.Retained or RuntimeContract.Tracked)
        {
            collectionType = args[0];
        }


        switch (node)
        {
            case ListLiteralExpression:
            {
                string baseName = GetCollectionBaseNameForReachability(collectionType);
                // Array/BitArray are pure inline IR — no add memberRoutine synthesized.
                if (baseName is "Array" or "BitArray") return;
                // List/CircularList/BitList → add_last; everything else → add (mirrors
                // ExpressionLoweringPass.LowerListLiteral).
                string addMemberRoutine = baseName is "List" or "CircularList" or "BitList"
                    ? RuntimeContract.Collection.AddLast
                    : RuntimeContract.Collection.Add;
                EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: addMemberRoutine);
                EnqueueZeroArgCreateIfPresent(owner: collectionType);
                break;
            }
            case SetLiteralExpression or DictLiteralExpression:
                EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: RuntimeContract.Collection.Add);
                EnqueueZeroArgCreateIfPresent(owner: collectionType);
                break;
            case IndexExpression ixNode:
                EnqueueIndexAccessCallees(ixNode: ixNode, collectionType: collectionType, typeSubs: typeSubs);
                break;
            case UnaryExpression { Operator: UnaryOperator.ForceUnwrap }:
                // `expr!!` is lowered by OperatorLoweringPass (Phase 8) to `expr.unwrap()`.
                // Failability is a property, not part of the name — seed the bare `unwrap` on the
                // operand's resolved owner so Maybe/Result/Lookup carriers' unwrap bodies get
                // monomorphized (whether or not that `unwrap` is failable).
                EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: "unwrap");
                break;
            case UsingStatement usingNode:
                // `using r.view() as v` lowers (in Phase 8 — after this pass) to
                // `__uf.enter()` ... `__uf.exit()`. Seed both on the resource type so they
                // make it into the live set; codegen later emits calls to the same symbols.
                // Either memberRoutine may be absent on a given resource type — EnqueueMemberRoutineIfPresent
                // is a no-op when LookupMemberRoutine returns null.
                EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: "enter");
                EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: "exit");
                // A `fallback` branch lowers the entry to `__uf.try_enter()` instead of `enter`.
                if (usingNode.FallbackBody != null)
                    EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: "try_enter");
                break;
        }
    }

    /// <summary>
    /// Resolves and seeds the member routine that OperatorLoweringPass (Phase 8) will produce for
    /// `a op b`. Resolves the exact overload by argument type so two overloads of e.g. sub
    /// (LocalMoment.sub(Duration) and LocalMoment.sub(LocalMoment)) both reach the live set when
    /// their respective call sites exist in user code.
    /// </summary>
    private void EnqueueBinaryOperatorCallee(BinaryExpression bin, Dictionary<string, TypeInfo> typeSubs)
    {
        string? memberRoutineName = bin.Operator.GetMemberRoutineName();
        if (memberRoutineName == null) return;
        bool reversed = bin.Operator is BinaryOperator.In or BinaryOperator.NotIn;
        Expression recvExpr = reversed ? bin.Right : bin.Left;
        Expression argExpr = reversed ? bin.Left : bin.Right;
        TypeInfo? recvRaw = recvExpr.ResolvedType;
        TypeInfo? argRaw = argExpr.ResolvedType;
        if (recvRaw == null) return;
        TypeInfo recv = RoutineInfo.SubstituteType(type: recvRaw, substitution: typeSubs);
        TypeInfo? arg = argRaw != null
            ? RoutineInfo.SubstituteType(type: argRaw, substitution: typeSubs)
            : null;
        RoutineInfo? resolved = arg != null
            ? ctx.Registry.LookupMemberRoutineOverload(type: recv, memberRoutineName: memberRoutineName, argTypes: [arg])
            : null;
        resolved ??= ctx.Registry.LookupMemberRoutine(type: recv, memberRoutineName: memberRoutineName);
        // Operator memberRoutine names are bare; the failable `!` is a structured flag. If the plain
        // lookup missed, retry filtering for a same-named failable implementation.
        resolved ??= ctx.Registry.LookupMemberRoutine(type: recv, memberRoutineName: memberRoutineName, isFailable: true);
        if (resolved != null) EnqueueCallee(callee: resolved);
    }

    /// <summary>
    /// Seeds the getitem/setitem member routines (plus the back-index `count`/`back_resolve`
    /// helpers) that Phase 8/9 lowering introduces for an index-access expression.
    /// </summary>
    private void EnqueueIndexAccessCallees(IndexExpression ixNode, TypeInfo collectionType,
        Dictionary<string, TypeInfo> typeSubs)
    {
        // Both read (getitem) and write (setitem) — IndexExpression's role is determined
        // by parent context (assignment target or not). Enqueue both; if either is absent
        // on this type, LookupMemberRoutine returns null and EnqueueMemberRoutineIfPresent is a no-op.
        // Names use the bare form (no '!') — parser strips the failable suffix and
        // tracks failability on RoutineInfo separately. See TypeRegistry.MemberRoutineLookup.cs:236.
        EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: "getitem");
        EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: "setitem");
        // `coll[^n]` (a `^n` BackIndexExpression index) is desugared by OperatorLoweringPass
        // (Phase 9, after this pass) to `coll.getitem!(back_resolve(count: coll.count(),
        // offset: n))`. Seed the two helpers that desugar introduces so they aren't
        // linked-but-unemitted: the collection's `count()` and the free routine `back_resolve`.
        // The `getitem` forward (U64) form is already seeded above.
        if (ixNode.Index is BackIndexExpression)
        {
            EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: RuntimeContract.Collection.Count);
            EnqueueBackResolve();
        }
        else if (ixNode.Index.ResolvedType is { } idxRaw)
        {
            TypeInfo idxType = RoutineInfo.SubstituteType(type: idxRaw, substitution: typeSubs);
            EnqueueMemberRoutineOverloadIfPresent(owner: collectionType, memberRoutineName: "getitem",
                argType: idxType);
            EnqueueMemberRoutineOverloadIfPresent(owner: collectionType, memberRoutineName: "setitem",
                argType: idxType);
        }

        // A slice with end-relative bounds `coll[a til ^0]` has a Range index; each `^n`
        // endpoint is desugared by OperatorLoweringPass (Phase 9) to `back_resolve(count:
        // coll.count(), offset: n)`. Seed `count` and `back_resolve`, mirroring the scalar branch.
        if (ixNode.Index is RangeExpression rangeIx &&
            (rangeIx.Start is BackIndexExpression || rangeIx.End is BackIndexExpression))
        {
            EnqueueMemberRoutineIfPresent(owner: collectionType, memberRoutineName: RuntimeContract.Collection.Count);
            EnqueueBackResolve();
        }
    }

    private void EnqueueMemberRoutineIfPresent(TypeInfo owner, string memberRoutineName)
    {
        // Liveness SEED (not a unique call binding): enqueue EVERY concrete overload of this name on
        // the owner. A name-only unique lookup returns null once >1 overload shares the name (no
        // first-wins), which would silently DROP a genuinely-called accessor body — e.g. the scalar
        // `getitem(index: U64)` when a `getitem(range:)` sibling also exists → the "declared+called but
        // never defined" over-prune. Seeding an extra, uncalled overload is harmless (codegen emits it,
        // the linker drops it), so a conservative all-overloads seed is the safe choice here.
        var candidates = new List<RoutineInfo>();
        ctx.Registry.CollectMemberRoutineCandidates(type: owner, memberRoutineName: memberRoutineName,
            candidates: candidates);
        foreach (RoutineInfo routine in candidates)
            if (routine.OwnerType is not { IsGenericDefinition: true })
                EnqueueCallee(callee: routine);
    }

    /// <summary>
    /// Seeds the concrete crashable members a <see cref="CrashableDispatchExpression"/> reaches. Codegen
    /// lowers the dispatch to a <c>type_id</c> switch that calls <c>&lt;MemberName&gt;</c> on EVERY
    /// registered crashable; none of those calls exist as AST nodes, so — mirroring the codegen switch —
    /// seed the member on every crashable here, or they'd be over-pruned ("declared+called but never
    /// defined"). This replaces the per-type AST calls the old fan-out produced (which reachability walked).
    /// </summary>
    private void EnqueueCrashableDispatchTargets(CrashableDispatchExpression dispatch)
    {
        foreach (TypeInfo t in ctx.Registry.GetTypesByCategory(category: TypeModel.Enums.TypeCategory.Crashable))
            EnqueueMemberRoutineIfPresent(owner: t, memberRoutineName: dispatch.MemberName);
    }

    /// <summary>Seeds the free routine <c>back_resolve</c> that OperatorLoweringPass injects for each
    /// <c>coll[^n]</c> subscript / <c>coll[a til ^0]</c> slice bound (this pass runs before that
    /// desugaring, so the call is not yet in the AST to be walked).</summary>
    private void EnqueueBackResolve()
    {
        RoutineInfo? backResolve =
            ctx.Registry.LookupRoutine(fullName: $"Core.{RuntimeContract.BackResolve}", isFailable: true)
            ?? ctx.Registry.LookupRoutine(fullName: RuntimeContract.BackResolve, isFailable: true);
        if (backResolve != null) EnqueueCallee(callee: backResolve);
    }

    /// <summary>
    /// Enqueues the <paramref name="memberRoutineName"/> overload whose parameter list matches a single
    /// argument of <paramref name="argType"/>. Used to reach a type-specific index overload that the
    /// first-match <see cref="EnqueueMemberRoutineIfPresent"/> skips.
    /// No-op when no such overload exists (e.g. two-parameter <c>setitem!</c> against one argType).
    /// </summary>
    private void EnqueueMemberRoutineOverloadIfPresent(TypeInfo owner, string memberRoutineName, TypeInfo argType)
    {
        RoutineInfo? routine = ctx.Registry.LookupMemberRoutineOverload(type: owner,
            memberRoutineName: memberRoutineName, argTypes: [argType]);
        if (routine != null) EnqueueCallee(callee: routine);
    }

    /// <summary>
    /// Enqueue the zero-arg <c>create()</c> overload specifically. Types with multiple
    /// <c>create</c> overloads (List, Set, Dict have 2-3 each) can have LookupMemberRoutine
    /// pick the wrong one; the literal-lowering path always calls the no-arg form.
    /// Also seeds the mangled-name fallback so codegen's mangled call can resolve.
    /// </summary>
    private void EnqueueZeroArgCreateIfPresent(TypeInfo owner)
    {
        // Prefer LookupMemberRoutine's substituted routine for generic owners. Only accept it when
        // its arity matches (LookupMemberRoutine is first-match and may return create(capacity)).
        RoutineInfo? routine = ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: CreateMemberRoutineName);
        if (routine is { Parameters.Count: 0 })
        {
            EnqueueCallee(callee: routine);
            return;
        }

        // Find the no-arg overload on the generic def and substitute for the concrete owner so
        // GMP can monomorphize correctly. Without SubstituteMemberRoutineForOwner the callee carries the
        // generic-def's owner ({List[T]}) and EnqueueCallee can't build a {T → S64} sub map.
        TypeInfo? genDef = owner switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d,
            RecordTypeInfo { GenericDefinition: { } d } => d,
            _ => null
        };
        if (genDef != null && !ReferenceEquals(objA: genDef, objB: owner))
        {
            foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: genDef))
            {
                if (m is { Name: CreateMemberRoutineName, Parameters.Count: 0 })
                {
                    RoutineInfo substituted = ctx.Registry.SubstituteMemberRoutineForOwner(
                        memberRoutine: m, resolvedOwner: owner)!;
                    EnqueueCallee(callee: substituted);
                    return;
                }
            }
        }
    }

    /// <summary>
    /// f-string parts get lowered to `&lt;expr&gt;.represent()` / `&lt;expr&gt;.diagnose()` calls by
    /// FStringLoweringPass in Phase 8. Reachability runs in Phase 7 — before that lowering —
    /// so the calls don't exist yet for the worklist to follow. Seed them here so synthesized
    /// represent/diagnose bodies on the interpolated expressions' types make it into the
    /// live set. Without this, codegen emits a call to the symbol but skips emitting the
    /// definition (gated by the live set), producing a link-time undefined symbol.
    /// </summary>
    private void EnqueueFStringCallees(InsertedTextExpression inserted,
        Dictionary<string, TypeInfo> typeSubs)
    {
        foreach (InsertedTextPart part in inserted.Parts)
        {
            if (part is not ExpressionPart ep) continue;
            TypeInfo? rawType = ep.Expression.ResolvedType ?? InferExpressionType(e: ep.Expression);
            if (rawType == null) continue;
            TypeInfo concreteType = RoutineInfo.SubstituteType(type: rawType, substitution: typeSubs);
            // `?` format spec → diagnose, otherwise represent. FStringLoweringPass also
            // emits a add chain — Text.add is on a concrete type already and gets walked
            // through the resulting call expressions in subsequent frames.
            bool isDiagnose = ep.FormatSpec is "?";
            EnqueueMemberRoutineIfPresent(owner: concreteType,
                memberRoutineName: isDiagnose ? DiagnoseMemberRoutineName : RepresentMemberRoutineName);
        }
    }

    /// <summary>
    /// Picks the overload of <paramref name="memberRoutineName"/> on <paramref name="owner"/> matching
    /// both arity and failability. <see cref="TypeRegistry.LookupMemberRoutine"/> uses first-match by
    /// name and doesn't disambiguate when a type has multiple overloads with the same name but
    /// different parameter shapes (e.g. <c>get_by_rank!(U64)</c> vs <c>get_by_rank(node, rank)</c>).
    /// </summary>
    private RoutineInfo? LookupMemberRoutineMatchingParamTypes(TypeInfo owner, string memberRoutineName,
        RoutineInfo inputRoutine, Dictionary<string, TypeInfo> typeSubs)
    {
        int paramCount = inputRoutine.Parameters.Count;
        bool isFailable = inputRoutine.IsFailable;
        var inputSigs = new string[paramCount];
        for (int i = 0; i < paramCount; i++)
        {
            TypeInfo? pt = inputRoutine.Parameters[index: i].Type;
            if (pt == null) return null;
            TypeInfo subbed = RoutineInfo.SubstituteType(type: pt, substitution: typeSubs);
            inputSigs[i] = subbed.FullName ?? subbed.Name;
        }

        bool Matches(RoutineInfo m)
        {
            if (m.Name != memberRoutineName) return false;
            if (m.Parameters.Count != paramCount) return false;
            if (m.IsFailable != isFailable) return false;
            for (int i = 0; i < paramCount; i++)
            {
                TypeInfo? pt = m.Parameters[index: i].Type;
                if (pt == null) return false;
                TypeInfo subbed = RoutineInfo.SubstituteType(type: pt, substitution: typeSubs);
                string sig = subbed.FullName ?? subbed.Name;
                if (sig != inputSigs[i]) return false;
            }
            return true;
        }

        foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: owner))
            if (Matches(m: m)) return m;

        TypeInfo? genDef = owner switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d,
            RecordTypeInfo { GenericDefinition: { } d } => d,
            _ => null
        };
        if (genDef != null)
        {
            foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: genDef))
                if (Matches(m: m))
                    return BuildOwnerSubstitutedRoutine(genericMemberRoutine: m, concreteOwner: owner, genDef: genDef);
        }
        return null;
    }

    private RoutineInfo? LookupMemberRoutineMatchingSignature(TypeInfo owner, string memberRoutineName,
        int paramCount, bool isFailable)
    {
        // First try the owner's own registered memberRoutines (rare — most generics route via genDef).
        foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: owner))
        {
            if (m.Name == memberRoutineName
                && m.Parameters.Count == paramCount
                && m.IsFailable == isFailable)
                return m;
        }
        // Fall back to the generic-def's memberRoutines. memberRoutines on `SortedDict[K,V]` are registered
        // under the def, not the concrete `SortedDict[S64,S64]` resolution. We need to manually
        // substitute the def-level overload onto the concrete owner so the resulting routine has
        // the correct owner AND the correct parameter signature. Don't delegate to LookupMemberRoutine
        // — its FirstOrDefault picks the wrong overload when multiple have the same name + same
        // failability (e.g. `create()` vs `create(key: SecureHashKey)`, both non-failable).
        TypeInfo? genDef = owner switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d,
            RecordTypeInfo { GenericDefinition: { } d } => d,
            _ => null
        };
        if (genDef != null)
        {
            foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: genDef))
            {
                if (m.Name == memberRoutineName
                    && m.Parameters.Count == paramCount
                    && m.IsFailable == isFailable)
                {
                    // Manually substitute m's owner to the concrete `owner`. Build typeSubs from
                    // genDef.GenericParameters → owner.TypeArguments, then apply to params/return.
                    return BuildOwnerSubstitutedRoutine(genericMemberRoutine: m, concreteOwner: owner,
                        genDef: genDef);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Produces a concrete-owner-substituted clone of a generic-def memberRoutine, parallel to
    /// <see cref="TypeRegistry.SubstituteMemberRoutineForOwner"/> but invoked from reachability with
    /// an already-selected overload (so we don't go back through LookupMemberRoutine's first-match
    /// heuristic).
    /// </summary>
    private RoutineInfo BuildOwnerSubstitutedRoutine(RoutineInfo genericMemberRoutine,
        TypeInfo concreteOwner, TypeInfo genDef)
    {
        var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        List<string>? gParams = genDef.GenericParameters;
        List<TypeInfo>? typeArgs = concreteOwner.TypeArguments;
        if (gParams != null && typeArgs != null)
        {
            for (int i = 0; i < gParams.Count && i < typeArgs.Count; i++)
                subs[key: gParams[index: i]] = typeArgs[index: i];
        }
        var substParams = genericMemberRoutine.Parameters
            .Select(selector: p => RoutineInfo.SubstituteParameterType(param: p, substitution: subs))
            .ToList();
        TypeInfo? substReturn = genericMemberRoutine.ReturnType != null
            ? RoutineInfo.SubstituteType(type: genericMemberRoutine.ReturnType, substitution: subs)
            : null;
        var clone = new RoutineInfo(name: genericMemberRoutine.Name)
        {
            Kind = genericMemberRoutine.Kind,
            OwnerType = concreteOwner,
            Parameters = substParams,
            ReturnType = substReturn,
            // Preserve the receiver-handle type (a Suflae entity's `me` is Roamed[E]); dropping it
            // makes codegen bind `me` to the bare entity and read the RC controller instead of deref.
            MeType = genericMemberRoutine.MeType != null
                ? RoutineInfo.SubstituteType(type: genericMemberRoutine.MeType, substitution: subs)
                : null,
            IsFailable = genericMemberRoutine.IsFailable,
            DeclaredMutation = genericMemberRoutine.DeclaredMutation,
            MutationCategory = genericMemberRoutine.MutationCategory,
            GenericParameters = genericMemberRoutine.GenericParameters?
                .Where(predicate: gp => !subs.ContainsKey(key: gp))
                .ToList() is { Count: > 0 } mp ? mp : null,
            GenericConstraints = genericMemberRoutine.GenericConstraints,
            Visibility = genericMemberRoutine.Visibility,
            Location = genericMemberRoutine.Location,
            Module = genericMemberRoutine.Module,
            ModulePath = genericMemberRoutine.ModulePath,
            Annotations = genericMemberRoutine.Annotations,
            CallingConvention = genericMemberRoutine.CallingConvention,
            IsVariadic = genericMemberRoutine.IsVariadic,
            IsDangerous = genericMemberRoutine.IsDangerous,
            IsSynthesized = genericMemberRoutine.IsSynthesized,
            GenericDefinition = genericMemberRoutine.GenericDefinition ?? genericMemberRoutine,
            WrapperForwarderInnerMemberRoutine = genericMemberRoutine.WrapperForwarderInnerMemberRoutine,
            WrapperForwarderInnerGenericDef = genericMemberRoutine.WrapperForwarderInnerGenericDef,
            AsyncStatus = genericMemberRoutine.AsyncStatus,
            FailableVariant = genericMemberRoutine.FailableVariant,
            OriginalName = genericMemberRoutine.OriginalName,
        };
        return ctx.Registry.RegisterRoutineResolution(resolvedMemberRoutine: clone);
    }

    private static string GetCollectionBaseNameForReachability(TypeInfo type)
    {
        return type switch
        {
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => type.BareName
        };
    }

    /// <summary>
    /// Additively records a caller→callee edge into the v0.2.0 may-suspend call graph and seeds the
    /// caller's <see cref="CallGraphNode.DirectlySuspends"/> flag when the callee is a coroutine
    /// suspend primitive (<see cref="SuspendPrimitives"/>). This is pure side data consumed later by
    /// <see cref="MaySuspendAnalysis"/>: it never reads or mutates the live set, the worklist, or any
    /// resolution state, so it cannot change reachability or codegen. <c>callsOnMe</c> is irrelevant
    /// to suspension (it propagates across every call), so edges are recorded with it false.
    ///
    /// Edges are recorded only at the three real-call sites (the two ProcessFrame loops and the
    /// synthesized-body walk). Display/operator/wired/f-string seed paths reach only pure value
    /// routines that never suspend — a v0.2.0 invariant — so their missing incoming edges are sound.
    /// </summary>
    private void RecordSuspendEdge(RoutineInfo caller, RoutineInfo callee)
    {
        ctx.MaySuspendGraph.AddEdge(caller: caller, callee: callee, callsOnMe: false);
        if (SuspendPrimitives.IsSuspendPrimitive(routine: callee))
        {
            ctx.MaySuspendGraph.GetOrCreateNode(routine: caller).DirectlySuspends = true;
        }
    }

    /// <summary>
    /// Marks <paramref name="caller"/> as having an indirect call when <paramref name="ce"/> invokes
    /// a routine VALUE (a first-class routine or lambda held in a variable/parameter/field) rather
    /// than a statically-named routine — i.e. its callee expression itself evaluates to a
    /// <see cref="RoutineTypeInfo"/>. The static graph cannot see such a target, so the may-suspend
    /// analysis treats the caller conservatively as may-suspend (design §6): over-approximation only
    /// adds shadow-stack push/pops, never miscompiles teardown. Only reached when static resolution
    /// already failed, so direct calls (which carry a ResolvedRoutine) never land here.
    ///
    /// NOTE (5b hardening): protocol/virtual dispatch that fails to devirtualize still resolves to
    /// the protocol memberRoutine (non-null) and so is NOT flagged here yet. That is sound for v0.2.0 (no
    /// suspending protocol impls exist); tighten before any such impl ships.
    /// </summary>
    private void NoteIfIndirectCall(RoutineInfo caller, CallExpression ce)
    {
        if (ce.Callee.ResolvedType is RoutineTypeInfo)
        {
            ctx.MaySuspendGraph.GetOrCreateNode(routine: caller).HasIndirectCall = true;
        }
    }

    private void EnqueueCallee(RoutineInfo callee)
    {
        // Mark live first — synthesized leafs (no AST body) still need to be in the live set
        // so GMP emits them and codegen can resolve the symbol.
        bool isFirstSeen = _live.Add(item: callee.RegistryKey);
        if (callee.OwnerType is { } liveOwner) _liveOwnerTypes.Add(item: liveOwner);

        // Return type of an RC wrapper (Owned/Retained/Tracked) becomes a live owner: codegen
        // will emit implicit `release`/`destroy` calls on the returned value at scope exit.
        // Without this, e.g. `var vt = retained.track()` produces a `Tracked[Node]` value whose
        // destructor is never made reachable, because no memberRoutine on `Tracked[Node]` is called
        // explicitly in user code.
        if (callee.ReturnType is { } retType
            && GetCollectionBaseNameForReachability(retType) is RuntimeContract.Owned or RuntimeContract.Retained or RuntimeContract.Tracked)
        {
            _liveOwnerTypes.Add(item: retType);
        }

        if (isFirstSeen)
        {
            ExpandSyntheticSiblings(callee: callee);
        }

        // Compute substitution map for the callee from owner-type generics.
        Dictionary<string, TypeInfo> subs = BuildCalleeSubs(callee: callee);

        // Find AST decl by name. Routine name forms used in stdlib: "List[T].insertion_sort",
        // "S64.add", "show". For memberRoutines, we try the owner-base + name combinations.
        RoutineDeclaration? decl = FindDecl(callee: callee);
        if (decl == null)
        {
            WalkSynthesizedCalleeBody(callee: callee, subs: subs);
            return;
        }

        // Enqueue for body walking. Use a visit-key gate to avoid re-walking under same subs.
        string visitKey = $"{callee.RegistryKey}|{string.Join(separator: ",", values: subs.Select(selector: kv => $"{kv.Key}={kv.Value.FullName}"))}";
        if (!_visited.Add(item: visitKey)) return;
        _worklist.Enqueue(item: new Frame(Routine: callee, Decl: decl, TypeSubs: subs));
    }

    /// <summary>
    /// Computes the generic substitution map for a callee from its owner-type generics AND its own
    /// memberRoutine-level type arguments, plus the universal-memberRoutine `T → concrete owner` binding.
    /// </summary>
    private Dictionary<string, TypeInfo> BuildCalleeSubs(RoutineInfo callee)
    {
        var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);

        // memberRoutine-level generic params carry their type arguments on the RoutineInfo when
        // instantiated by SubstituteRoutine. The body references the params as receiver/argument
        // types — e.g. free `show[T]`'s body has `value.represent()` where value: T, and
        // `T.share[P]()`'s body constructs `GuardController[T, P](...)`. Without `T → ConcreteType`
        // / `P → ConcretePolicy` in the frame's TypeSubs, the body walker can't resolve those
        // types, leaving the concrete memberRoutine/constructor out of the live set. Codegen then emits a
        // call to it but never the definition → linker error. This applies to BOTH free functions
        // and member memberRoutines with memberRoutine-level generics (the owner-type params are bound below).
        if (callee is { GenericDefinition: { GenericParameters: { Count: > 0 } freeParams }, TypeArguments: { Count: > 0 } freeArgs }
                && freeParams.Count == freeArgs.Count)
        {
            for (int i = 0; i < freeParams.Count; i++)
            {
                subs[key: freeParams[index: i]] = freeArgs[index: i];
            }
        }

        if (callee.OwnerType is { } owner)
        {
            TypeInfo? genDef = owner switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
                _ => null
            };
            List<TypeInfo>? typeArgs = owner.TypeArguments;
            List<string>? gParams = genDef?.GenericParameters;
            if (gParams != null && typeArgs != null && gParams.Count == typeArgs.Count)
            {
                for (int i = 0; i < gParams.Count; i++)
                {
                    subs[key: gParams[index: i]] = typeArgs[index: i];
                }
            }

            // Universal memberRoutine instance: `T.foo()` substituted to `Bytes.foo()` keeps the original
            // universal memberRoutine (owner = GenericParameterTypeInfo("T")) as GenericDefinition. The
            // body's own `me.bar()` calls have ResolvedRoutine.OwnerType = T (still generic),
            // so SubstituteRoutine needs `T → concrete owner` in typeSubs to resolve them.
            // Without this, calls like `me.get_address()` inside `T.hijack()` never produce a
            // `Bytes.get_address` live key when this frame runs on `Bytes.hijack`.
            if (callee.GenericDefinition?.OwnerType is GenericParameterTypeInfo universalParam)
            {
                subs[key: universalParam.Name] = owner;
            }
        }

        return subs;
    }

    /// <summary>
    /// Handles a callee that has no source AST decl: locates a synthesized body (variant, generic-def
    /// form, or try_/check_/lookup_ underlying failable routine) and walks its calls under
    /// <paramref name="subs"/>, seeding each resolved callee.
    /// </summary>
    private void WalkSynthesizedCalleeBody(RoutineInfo callee, Dictionary<string, TypeInfo> subs)
    {
        // No source AST — but the routine may have a synthesized body in VariantBodies
        // (e.g. Tuple[S8, Bool].hash, generated record/entity eq/cmp/hash). Walk that
        // body directly so its calls (S8.hash, Bool.hash, ...) reach the live set.
        // Without this, synthesized routines are leaf nodes in the BFS and their callees
        // never become live -> linker errors.
        //
        // For concrete generic instantiations (e.g. Range[U64].iter), the synthesized body
        // is keyed under the generic-def routine (Range[T].iter). Fall back to that key and
        // walk with the substitution map so calls like me.is_ascending() resolve to the
        // concrete Range[U64].is_ascending and become live.
        Statement? synthBody = ResolveSynthesizedCalleeBody(callee: callee);
        if (synthBody == null) return;

        string synthVisitKey = $"{callee.RegistryKey}|{string.Join(separator: ",", values: subs.Select(selector: kv => $"{kv.Key}={kv.Value.FullName}"))}";
        if (!_visited.Add(item: synthVisitKey)) return;

        // Stash and restore _currentFrameSubs so ResolveMemberCall can substitute
        // generic-param receivers (e.g. `inner.keys_add_last` where inner: T) for
        // wrapper-forwarder bodies. Without this, ResolveMemberCall sees the bare
        // GenericParameterTypeInfo("T") and returns null, dropping the inner call
        // from reachability — leaves the inner memberRoutine's monomorphization unseeded
        // even though codegen later emits a call to the (correctly-mangled) symbol.
        Dictionary<string, TypeInfo> savedSubs = _currentFrameSubs;
        _currentFrameSubs = subs;
        try
        {
            var synthCalls = new List<object>();
            CollectCalls(node: synthBody, sink: synthCalls);
            foreach (object node in synthCalls)
            {
                WalkSynthesizedCallNode(node: node, callee: callee, subs: subs);
            }
        }
        finally
        {
            _currentFrameSubs = savedSubs;
        }
    }

    /// <summary>
    /// Locates the synthesized body statement for a callee with no source AST decl, trying the
    /// callee's own key, its generic-def form, then the underlying failable routine for
    /// try_/check_/lookup_ variants. Returns null when none is found.
    /// </summary>
    private Statement? ResolveSynthesizedCalleeBody(RoutineInfo callee)
    {
        if (ctx.VariantBodies.TryGetValue(key: callee.RegistryKey, out Statement? synthBody))
            return synthBody;

        RoutineInfo? genericDefRoutine = ResolveGenericDefRoutine(callee: callee);
        if (genericDefRoutine != null)
            ctx.VariantBodies.TryGetValue(key: genericDefRoutine.RegistryKey, out synthBody);

        // Fallback for try_/check_/lookup_ variants: ErrorHandlingVariantPass keys the
        // synthesized body by `variant.Routine.RegistryKey`, which depends on the
        // owner's generic-def shape AND the variant's signature. The lookup above can
        // miss when the variant body wasn't keyed under the generic-def form we
        // reconstructed. Use `callee.OriginalName` to find the underlying failable
        // routine (`emit!`) and walk its body from `ctx.RoutineBodies` — the variant
        // body is just a transformed copy of the same statements, so the calls it
        // makes are identical.
        if (synthBody == null && callee.OriginalName is { } origName)
        {
            RoutineInfo? original = ctx.Registry.LookupMemberRoutine(
                type: callee.OwnerType!, memberRoutineName: origName, isFailable: true);
            if (original != null)
                ctx.RoutineBodies.TryGetValue(key: original.RegistryKey, out synthBody);
            // Also try the generic-def owner form for monomorphized callees.
            if (synthBody == null && callee.OwnerType is { } cOwner)
            {
                TypeInfo? genericOwner = cOwner switch
                {
                    EntityTypeInfo { GenericDefinition: { } d } => d,
                    RecordTypeInfo { GenericDefinition: { } d } => d,
                    _ => null
                };
                if (genericOwner != null)
                {
                    RoutineInfo? originalOnGenDef = ctx.Registry.LookupMemberRoutine(
                        type: genericOwner, memberRoutineName: origName, isFailable: true);
                    if (originalOnGenDef != null)
                        ctx.RoutineBodies.TryGetValue(key: originalOnGenDef.RegistryKey, out synthBody);
                }
            }
        }

        return synthBody;
    }

    /// <summary>
    /// Resolves and seeds one call-like node found inside a synthesized callee body, using
    /// <paramref name="subs"/> as the substitution map.
    /// </summary>
    private void WalkSynthesizedCallNode(object node, RoutineInfo callee, Dictionary<string, TypeInfo> subs)
    {
        if (node is ThrowStatement throwStmt)
        {
            EnqueueThrowCrashMessage(throwStmt: throwStmt);
            return;
        }
        // Collection literals in synthesized bodies (e.g. MakeListReturn for
        // BuilderQuery.protocols) need the same implicit add/create seeding
        // as user-code literals; otherwise the literal's create body never
        // gets monomorphized and codegen emits a call to an undefined symbol.
        if (node is ListLiteralExpression or SetLiteralExpression or DictLiteralExpression or IndexExpression or UsingStatement or UnaryExpression { Operator: UnaryOperator.ForceUnwrap } or BinaryExpression)
        {
            EnqueueImplicitLoweringCallees(node: node, typeSubs: subs);
            return;
        }
        RoutineInfo? resolved = node switch
        {
            CallExpression ce => ce.ResolvedRoutine ?? ResolveNoArgConstructor(ce: ce) ?? ResolveCallStyleConstructor(ce: ce) ?? ResolveMemberCall(ce: ce),
            GenericMemberRoutineCallExpression gce => gce.ResolvedRoutine,
            CreatorExpression cre => ResolveCreatorRoutine(cre: cre),
            _ => null
        };
        if (resolved == null) return;
        RoutineInfo synthSubCallee = SubstituteRoutine(routine: resolved, typeSubs: subs);
        EnqueueCallee(callee: synthSubCallee);
        RecordSuspendEdge(caller: callee, callee: synthSubCallee);
    }

    /// <summary>
    /// Codegen synthesizes related routines unconditionally for a type that has any of its memberRoutines
    /// emitted. To keep the linker happy without forcing all-or-nothing emission, when we mark a
    /// routine live we also enqueue:
    /// — comparison-op group siblings (cmp ↔ lt/le/gt/ge, eq ↔ ne, contains ↔ notcontains)
    /// — display routines on the owner (represent, diagnose) — emitted unconditionally per type
    /// — wrapper transparency: same-named memberRoutine on the inner T of wrapper owners
    /// </summary>
    private void ExpandSyntheticSiblings(RoutineInfo callee)
    {
        TypeInfo? owner = callee.OwnerType;
        string name = callee.Name;

        // (1) Comparison op group on same owner.
        if (owner != null)
        {
            string[]? siblings = name switch
            {
                "cmp" => new[] { "lt", "le", "gt", "ge" },
                "lt" or "le" or "gt" or "ge" => new[] { "cmp" },
                "eq" => new[] { "ne" },
                "ne" => new[] { "eq" },
                "contains" => new[] { "notcontains" },
                "notcontains" => new[] { "contains" },
                _ => null
            };
            if (siblings != null)
            {
                foreach (string sib in siblings)
                {
                    RoutineInfo? sibInfo = ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: sib);
                    if (sibInfo != null) EnqueueCallee(callee: sibInfo);
                }
            }

            ExpandOwnerDisplayRoutines(owner: owner, name: name);
            ExpandPerMemberDestroy(owner: owner, name: name);
            ExpandPerMemberSerialize(owner: owner, name: name);
        }

        ExpandFreeGenericTypeArgSiblings(callee: callee, owner: owner, name: name);
    }

    /// <summary>
    /// Rule (2): codegen emits represent/diagnose for every owner that has any memberRoutine
    /// emitted, so force them live for transitive callers of show()/alert().
    /// </summary>
    private void ExpandOwnerDisplayRoutines(TypeInfo owner, string name)
    {
        // (2) Display routines: codegen emits represent/diagnose for every owner that has any
        // memberRoutine emitted. Force them live so transitive callers of show()/alert() resolve. (serialize
        // is NOT owner-seeded here — unlike represent/diagnose it is not truly universal: a
        // routine-typed member has represent but no serialize, so blanket-seeding serialize on every
        // live type would force it onto never-serialized routine-holding entities and fail to link.
        // The now-unconditional serialize derive is instead seeded transitively per-member below (2c).)
        if (name != RepresentMemberRoutineName && name != DiagnoseMemberRoutineName)
        {
            RoutineInfo? rep = ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: RepresentMemberRoutineName);
            if (rep != null) EnqueueCallee(callee: rep);
            RoutineInfo? diag = ctx.Registry.LookupMemberRoutine(type: owner, memberRoutineName: DiagnoseMemberRoutineName);
            if (diag != null) EnqueueCallee(callee: diag);
        }
    }

    /// <summary>
    /// Rule (2b): a live `destroy` runs the auto-derived teardown that calls each member's
    /// `.destroy()`; seed those per-member destructors so trivially-destructible leaves aren't pruned.
    /// </summary>
    private void ExpandPerMemberDestroy(TypeInfo owner, string name)
    {
        // (2b) A live `destroy` runs the auto-derived teardown, which walks EVERY member and calls
        // its `.destroy()` (`expand m in allmemvarof(T): me.$nameof(m).destroy()`). Reachability runs
        // BEFORE that expand unrolls, so it cannot see the per-field `.destroy()` calls; without help
        // it prunes a member's destroy that nothing ELSE calls — notably a trivially-destructible
        // `Hijacked[U]`/leaf whose destroy is elided at every ordinary teardown, leaving the
        // unconditional derive as its sole (invisible) caller ("declared+called but never defined").
        // Seed destroy on each concrete member type so the stdlib-defined (possibly no-op) destroy is
        // emitted. Recurses naturally: a member's destroy going live seeds ITS members' destroys.
        if (name == DestroyMemberRoutineName)
        {
            foreach (MemberVariableInfo mv in MemberVariablesOf(type: owner))
            {
                if (mv.Type is null or GenericParameterTypeInfo) continue;
                RoutineInfo? memberDestroy =
                    ctx.Registry.LookupMemberRoutine(type: mv.Type, memberRoutineName: DestroyMemberRoutineName);
                if (memberDestroy != null) EnqueueCallee(callee: memberDestroy);
            }
        }
    }

    /// <summary>
    /// Rule (2c): a live `serialize` derive walks every open member calling `.serialize()`; seed
    /// those per-member serializers transitively (precise, unlike a blanket owner-seed).
    /// </summary>
    private void ExpandPerMemberSerialize(TypeInfo owner, string name)
    {
        // (2c) The now-UNCONDITIONAL `serialize` derive walks every OPEN member calling
        // `me.$nameof(m).serialize()`. Reachability runs BEFORE that expand unrolls, so seed serialize
        // TRANSITIVELY on each concrete member type — precise (only members of an actually-live
        // serialize), unlike a blanket owner-seed which would wrongly force serialize onto a
        // never-serialized entity holding a routine-typed field (routines have no serialize). Recurses
        // naturally: a member's serialize going live seeds ITS members' serializes.
        if (name == SerializeMemberRoutineName)
        {
            foreach (MemberVariableInfo mv in MemberVariablesOf(type: owner))
            {
                if (mv.Type is null or GenericParameterTypeInfo) continue;
                RoutineInfo? memberSerialize =
                    ctx.Registry.LookupMemberRoutine(type: mv.Type, memberRoutineName: SerializeMemberRoutineName);
                if (memberSerialize != null) EnqueueCallee(callee: memberSerialize);
            }
        }
    }

    /// <summary>
    /// Rules (4) and (3): for a free generic function (no owner) with concrete type arguments, seed
    /// each type-argument's display routines, plus wrapper transparency to the inner T's same-named
    /// memberRoutine.
    /// </summary>
    private void ExpandFreeGenericTypeArgSiblings(RoutineInfo callee, TypeInfo? owner, string name)
    {
        // (4) Free generic function (no owner) with type arguments — e.g. `show[T]`
        // monomorphised to `show[S16]`. Its body references `value.represent()` where
        // `value: T`; after substitution to S16 the call needs `S16.represent` live.
        // Reachability's body walker can't resolve `T.represent` to the concrete form
        // (no such memberRoutine on the bare generic param), so seed each type-argument's
        // display routines here instead. Mirrors rule (2) but keyed on type-arguments
        // instead of the owner type.
        //
        // FIXME: this is a heuristic workaround, not a real fix. The proper fix is to
        // teach reachability to resolve `value.represent()` on a generic-param receiver
        // by substituting through the frame's TypeSubs (groundwork added via
        // _currentFrameSubs in ResolveMemberCall, but the CallExpression for
        // `value.represent()` doesn't even appear in CollectCalls' output — something
        // between stdlib parsing and Phase 7 strips it). Until that's localised, this
        // rule keeps `show[T]`-style display chains linker-clean. Narrowed to display
        // routines on type-arguments to keep the false-positive surface tiny.
        if (callee.OwnerType == null && callee.TypeArguments is { Count: > 0 } typeArgs)
        {
            foreach (TypeInfo arg in typeArgs)
            {
                if (arg is GenericParameterTypeInfo) continue;
                RoutineInfo? argRep = ctx.Registry.LookupMemberRoutine(type: arg, memberRoutineName: RepresentMemberRoutineName);
                if (argRep != null) EnqueueCallee(callee: argRep);
                RoutineInfo? argDiag = ctx.Registry.LookupMemberRoutine(type: arg, memberRoutineName: DiagnoseMemberRoutineName);
                if (argDiag != null) EnqueueCallee(callee: argDiag);
            }

            // (3) Wrapper transparency: forward to inner T's same-named memberRoutine.
            if (owner is WrapperTypeInfo { InnerType: not null } wrapper)
            {
                RoutineInfo? inner = ctx.Registry.LookupMemberRoutine(type: wrapper.InnerType, memberRoutineName: name);
                if (inner != null) EnqueueCallee(callee: inner);
            }
        }
    }

    /// <summary>
    /// The member variables of a record/entity owner (concrete instances carry substituted member
    /// types via <c>CreateInstance</c>), used to seed per-member teardown in rule (2b). Other type
    /// kinds (variants, tuples, @llvm leaves, generic defs) yield none.
    /// </summary>
    private static IReadOnlyList<MemberVariableInfo> MemberVariablesOf(TypeInfo type) => type switch
    {
        RecordTypeInfo r => r.MemberVariables,
        EntityTypeInfo e => e.MemberVariables,
        _ => []
    };

    /// <summary>
    /// Maps a concrete-owner routine (e.g. <c>Range[U64].iter</c>) to its generic-def
    /// counterpart (<c>Range[T].iter</c>). Returns null if the owner isn't a generic
    /// instantiation or the memberRoutine isn't found on the def.
    /// </summary>
    private RoutineInfo? ResolveCreatorRoutine(CreatorExpression cre)
    {
        // SA already resolved a routed creator (e.g. a variadic `create(elements...: T)` monomorphized
        // to `create[K]`, or a named-arg `create(named:)` route). That routine is authoritative — seed it
        // directly. GetMemberRoutinesForType does not surface a type's `create` constructors, so the
        // label-matching fallback below would miss it and the body would be pruned (declared-but-defined).
        if (cre.ResolvedCreatorRoutine is { } routedCreate)
        {
            EnqueueCallee(callee: routedCreate);
            return routedCreate;
        }

        TypeInfo? ct = cre.ConstructedType;
        if (ct == null) return null;
        // In a monomorphized frame the constructed type may be a generic parameter (e.g. `P()`
        // inside `GuardController[T, P].create`'s body, with P bound to a concrete LockPolicy).
        // Substitute through the active frame subs so we enqueue the concrete policy's `create`
        // (and emit its body) rather than a bogus `P.create`.
        ct = RoutineInfo.SubstituteType(type: ct, substitution: _currentFrameSubs);
        // Match overload by parameter count — Text() (no args) and Text(from: CStr) are
        // distinct create overloads on the same type. LookupMemberRoutine alone returns the
        // first-registered one and misses the no-arg variant when callers use it.
        int argCount = cre.MemberVariables.Count;
        // Use the call's named-argument labels to disambiguate sibling overloads. Without
        // this, all 1-arg `create` overloads (capacity: U64 / from: Set /
        // from: SortedList / from: SortedSet) get enqueued for any `List[T](x)` call —
        // an unrelated overload then drags its `.iter` etc. into the live set on programs
        // that never reference that type, producing LINKERR across the playground.
        var argLabels = cre.MemberVariables.Select(static mv => mv.Name).ToList();
        bool MatchesLabels(RoutineInfo m)
        {
            if (m.Parameters.Count != argCount) return false;
            for (int i = 0; i < argCount; i++)
            {
                if (m.Parameters[i].Name != argLabels[i]) return false;
            }
            return true;
        }
        bool found = false;
        RoutineInfo? matched = null;
        foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: ct))
        {
            if (m.Name == CreateMemberRoutineName && MatchesLabels(m))
            {
                EnqueueCallee(callee: m);
                if (matched == null) matched = m;
                found = true;
            }
        }
        TypeInfo? genDef = ct switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            _ => null
        };
        if (genDef != null && !ReferenceEquals(objA: genDef, objB: ct))
        {
            foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: genDef))
            {
                if (m.Name == CreateMemberRoutineName && MatchesLabels(m))
                {
                    // Substitute generic params onto the concrete owner so GMP can monomorphize.
                    // Without this, EnqueueCallee gets a routine with OwnerType = generic-def and
                    // can't build a {T → concrete} substitution map — body never emitted.
                    RoutineInfo substituted = ct.IsGenericResolution
                        ? ctx.Registry.SubstituteMemberRoutineForOwner(memberRoutine: m, resolvedOwner: ct)!
                        : m;
                    EnqueueCallee(callee: substituted);
                    if (matched == null) matched = substituted;
                    found = true;
                }
            }
        }
        if (!found && argCount == 0)
        {
            // Fallback for no-arg constructors that codegen calls by mangled name
            // <Type>.create even when no explicit RoutineInfo exists.
            _live.Add(item: $"{ct.FullName}.create");
        }
        // Return only the label-matched overload. The previous LookupMemberRoutine fallback returned
        // the first-registered create by name regardless of param shape — that pulled in
        // `List[T].create(from: Set[T])` (first 1-arg overload) for any field-init
        // CreatorExpression like `List[T](data:..., count:..., capacity:...)` that has no
        // matching create overload at all. Such field-init creators are emitted inline by
        // codegen and don't need a routine seeded.
        return matched;
    }

    /// <summary>
    /// SA leaves <c>CallExpression.ResolvedRoutine</c> null for no-arg type-creator calls (e.g.
    /// <c>Text()</c>) — only <c>ConstructedType</c> is set. Codegen emits a call to
    /// <c>&lt;Type&gt;.create</c> by mangled name, so we must mark that key live ourselves.
    /// </summary>
    private RoutineInfo? ResolveNoArgConstructor(CallExpression ce)
    {
        if (ce.Arguments.Count != 0) return null;
        TypeInfo? ct = ResolveNoArgConstructedType(ce: ce);
        if (ct == null) return null;

        RoutineInfo? zeroArg = FindZeroArgCreate(ct: ct);
        if (zeroArg != null) return zeroArg;

        RoutineInfo? chained = FindChainConstructorCreate(ce: ce, ct: ct);
        if (chained != null) return chained;

        // Fallback: codegen mangles by FullName regardless of registration.
        _live.Add(item: $"{ct.FullName}.create");
        return null;
    }

    /// <summary>
    /// Recovers the constructed type of a no-arg constructor call, resolving a generic-parameter
    /// callee through the active frame subs and substituting the result through those subs.
    /// </summary>
    private TypeInfo? ResolveNoArgConstructedType(CallExpression ce)
    {
        TypeInfo? ct = ce.ConstructedType;
        // A no-arg construction of a generic parameter (e.g. `P()` inside `GuardController[T, P]
        // .create`'s body) is parsed as a CallExpression whose callee is an IdentifierExpression
        // naming the param, and SA leaves ConstructedType null (the param has no concrete type yet).
        // In a monomorphized frame the param is bound, so recover the concrete type from the frame
        // subs — otherwise the concrete policy's `create` body is never enqueued (LINKERR).
        if (ct == null && ce.Callee is IdentifierExpression idCalleeForParam
            && _currentFrameSubs.TryGetValue(key: idCalleeForParam.Name, value: out TypeInfo? boundParamType))
        {
            ct = boundParamType;
        }
        if (ct == null) return null;
        // Substitute through the active frame subs so a constructed type that is itself a generic
        // parameter (or carries one) resolves to its concrete binding.
        return RoutineInfo.SubstituteType(type: ct, substitution: _currentFrameSubs);
    }

    /// <summary>Returns the zero-arg <c>create</c> overload on <paramref name="ct"/>, or null.</summary>
    private RoutineInfo? FindZeroArgCreate(TypeInfo ct)
    {
        foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: ct))
        {
            if (m is { Name: CreateMemberRoutineName, Parameters.Count: 0 }) return m;
        }
        return null;
    }

    /// <summary>
    /// memberRoutine-chain constructor: text.S32!() lowers to S32.create(receiver). The call has
    /// zero positional arguments but the member-receiver is the conversion source. Match the create
    /// overload whose single parameter accepts the receiver type so reachability marks the failable
    /// Text overload (not the first-registered S8 one).
    /// </summary>
    private RoutineInfo? FindChainConstructorCreate(CallExpression ce, TypeInfo ct)
    {
        if (ce.Callee is not MemberExpression chainMember) return null;
        TypeInfo? receiverType = chainMember.Object.ResolvedType
            ?? InferExpressionType(e: chainMember.Object);
        if (receiverType == null) return null;
        foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: ct))
        {
            if (m.Name != CreateMemberRoutineName || m.Parameters.Count != 1) continue;
            if (m.Parameters[index: 0].Type?.Name == receiverType.Name) return m;
        }
        return null;
    }

    /// <summary>
    /// When SA classified a call as CrashableDispatch because the receiver is a generic param
    /// constrained by a protocol (e.g. <c>me.alg.apply(...)</c> where <c>me.alg: M</c> and
    /// <c>M obeys LazyMonoid</c>), <c>ResolvedRoutine</c> points to the protocol's memberRoutine.
    /// In a monomorphized frame the param is bound to a concrete impl record, so the call
    /// becomes a static dispatch — but reachability would otherwise mark the *protocol*
    /// memberRoutine live and never reach the impl's body, leaving the linker with an undefined
    /// symbol at the rewritten call site. Re-resolve through the receiver so we enqueue the
    /// concrete impl memberRoutine (e.g. <c>NumericSumAdd[S64].apply</c>).
    /// </summary>
    private RoutineInfo? RetargetProtocolDispatch(CallExpression ce)
    {
        if (ce.ResolvedRoutine?.OwnerType is not ProtocolTypeInfo) return null;
        if (ce.Callee is not MemberExpression member) return null;
        TypeInfo? receiverType = member.Object.ResolvedType ?? InferExpressionType(e: member.Object);
        if (receiverType is not GenericParameterTypeInfo gp) return null;
        if (!_currentFrameSubs.TryGetValue(key: gp.Name, value: out TypeInfo? concrete)) return null;
        return ResolveMemberCall(ce: ce) ?? ctx.Registry.LookupMemberRoutine(type: concrete, memberRoutineName: ce.ResolvedRoutine.Name);
    }

    /// <summary>
    /// OperatorLoweringPass produces <c>receiver.op(you: arg)</c> CallExpressions but leaves
    /// <c>ResolvedRoutine = null</c> when the memberRoutine couldn't be resolved at lowering time
    /// (e.g. stdlib bodies whose receiver lacks <c>ResolvedType</c>). Retry the lookup here
    /// using the receiver's resolved type so primitive operators like <c>U32.bitand</c>
    /// reachable through stdlib bodies (<c>CStr.create</c>) become live.
    /// </summary>
    private RoutineInfo? ResolveMemberCall(CallExpression ce)
    {
        if (ce.Callee is not MemberExpression member) return null;
        TypeInfo? receiverType = ResolveMemberCallReceiverType(member: member);
        if (receiverType == null) return null;

        // MemberName is always bare; failability is carried structurally in member.IsFailable.
        // LookupMemberRoutine's name comparison runs against RoutineInfo.Name (also bare), so pass
        // isFailable explicitly to hit the failable variant.
        string baseName = member.MemberName;
        bool? isFailable = member.IsFailable ? true : null;

        // Disambiguate overloads by parameter count when multiple routines share the name (e.g.
        // `SortedDict[K,V].get_by_rank!(i: U64)` vs `SortedDict[K,V].get_by_rank(node, rank)`).
        // `LookupMemberRoutine` returns the first match — wrong for non-failable 2-arg calls if the
        // failable 1-arg variant was registered first.
        if (isFailable != true)
        {
            RoutineInfo? byCount = FindMemberOverloadByArgCount(receiverType: receiverType,
                baseName: baseName, isFailable: isFailable, argCount: ce.Arguments.Count);
            if (byCount != null) return byCount;
        }

        return ctx.Registry.LookupMemberRoutine(type: receiverType, memberRoutineName: baseName, isFailable: isFailable);
    }

    /// <summary>
    /// Resolves the receiver type of a member call, substituting a generic-param receiver through
    /// the active frame subs.
    /// </summary>
    private TypeInfo? ResolveMemberCallReceiverType(MemberExpression member)
    {
        TypeInfo? receiverType = member.Object.ResolvedType ?? InferExpressionType(e: member.Object);
        if (receiverType == null) return null;

        // When the receiver's resolved type is a generic param and the active frame has
        // a concrete substitution for it (e.g. `value: T` inside `show[T=S16]`'s body),
        // resolve the memberRoutine on the concrete substituted type. Otherwise the lookup
        // returns null (no memberRoutine on the bare `T`) and the call drops out of reachability,
        // leaving codegen with an undefined symbol when it later emits the monomorphised
        // call site.
        if (receiverType is GenericParameterTypeInfo genericReceiver
            && _currentFrameSubs.TryGetValue(key: genericReceiver.Name, value: out TypeInfo? substituted))
        {
            receiverType = substituted;
        }
        return receiverType;
    }

    /// <summary>
    /// Finds the first <paramref name="baseName"/> overload on <paramref name="receiverType"/>
    /// matching <paramref name="argCount"/> parameters (and failability), disambiguating same-named
    /// overloads that <see cref="TypeRegistry.LookupMemberRoutine"/>'s first-match would resolve wrong.
    /// </summary>
    private RoutineInfo? FindMemberOverloadByArgCount(TypeInfo receiverType, string baseName,
        bool? isFailable, int argCount)
    {
        foreach (RoutineInfo m in ctx.Registry.GetMemberRoutinesForType(type: receiverType))
        {
            if (m.Name != baseName) continue;
            if (m.Parameters.Count != argCount) continue;
            if (isFailable != null && m.IsFailable != isFailable) continue;
            return m;
        }
        return null;
    }

    /// <summary>
    /// A call to the cycle-collector hook intrinsic <c>&lt;entity&gt;.roam_trace_ref()</c> /
    /// <c>.roam_free_ref()</c> is lowered at codegen to a closure over the receiver entity's
    /// synthesized <c>roam_trace_impl</c> / <c>roam_free_impl</c> (see the intercept in
    /// <c>EmitMemberRoutineCall</c>). That reference is invisible to normal reachability walking, so
    /// mark the target impl live here, or its body is never emitted and the thunk links against an
    /// undefined symbol.
    /// </summary>
    private void EnqueueRoamHookIfNeeded(object node, RoutineInfo callee,
        Dictionary<string, TypeInfo> typeSubs)
    {
        if (callee.Name is not ("roam_trace_ref" or "roam_free_ref")) return;
        if (node is not CallExpression { Callee: MemberExpression member }) return;
        TypeInfo? recv = member.Object.ResolvedType ?? InferExpressionType(e: member.Object);
        if (recv is GenericParameterTypeInfo gp && typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? sub))
            recv = sub;
        if (recv is not EntityTypeInfo ent) return;
        string implName = callee.Name == "roam_trace_ref" ? "roam_trace_impl" : "roam_free_impl";
        // Resolve through LookupMemberRoutine (not GetOwnMemberRoutinesResolved): for a generic entity resolution
        // like List[Roamed[Node]] the resolution's own table holds only already-reachable memberRoutines, so
        // GetOwnMemberRoutinesResolved short-circuits on it and never surfaces the generic-def-registered
        // roam_trace_impl — the impl would never be enqueued, leaving the container's trace_hook wired
        // to cptr_none() and its held cycle uncollectable. LookupMemberRoutine substitutes from the generic
        // def; EnqueueCallee then monomorphizes the body into the resolution's table.
        RoutineInfo? impl = ctx.Registry.LookupMemberRoutine(type: ent, memberRoutineName: implName);
        if (impl != null) EnqueueCallee(callee: impl);
    }

    /// <summary>
    /// Handles call-style constructor invocations like <c>Byte(U8(cp))</c> — a CallExpression
    /// whose Callee is an IdentifierExpression naming a type, with no ResolvedRoutine. Codegen
    /// emits a call to <c>&lt;Type&gt;.create(...)</c> by mangled name; mark the matching
    /// create overload live by parameter count.
    /// </summary>
    private RoutineInfo? ResolveCallStyleConstructor(CallExpression ce)
    {
        if (ce.Arguments.Count == 0) return null; // ResolveNoArgConstructor handles those
        TypeInfo? ct = ce.ConstructedType;
        if (ct == null && ce.Callee is IdentifierExpression idCallee)
        {
            ct = ctx.Registry.LookupType(name: idCallee.Name);
        }
        if (ct == null) return null;
        int argCount = ce.Arguments.Count;

        // Infer arg types so we can disambiguate overloads like Byte.create(Byte)
        // vs Byte.create(U8).
        var argTypes = new TypeInfo?[argCount];
        for (int i = 0; i < argCount; i++)
            argTypes[i] = InferExpressionType(e: ce.Arguments[index: i]);

        RoutineInfo? PickOverload(IEnumerable<RoutineInfo> memberRoutines)
        {
            RoutineInfo? countOnly = null;
            foreach (RoutineInfo m in memberRoutines)
            {
                if (m.Name != CreateMemberRoutineName || m.Parameters.Count != argCount) continue;
                countOnly ??= m;
                bool typesMatch = true;
                for (int i = 0; i < argCount; i++)
                {
                    TypeInfo? at = argTypes[i];
                    if (at == null) continue; // unknown — accept
                    if (m.Parameters[index: i].Type?.Name != at.Name)
                    { typesMatch = false; break; }
                }
                if (typesMatch) return m;
            }
            return countOnly;
        }

        RoutineInfo? match = PickOverload(memberRoutines: ctx.Registry.GetMemberRoutinesForType(type: ct));
        if (match == null)
        {
            TypeInfo? genDef = ct switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                _ => null
            };
            if (genDef != null && !ReferenceEquals(objA: genDef, objB: ct))
                match = PickOverload(memberRoutines: ctx.Registry.GetMemberRoutinesForType(type: genDef));
        }

        // Direct named-field construction has no `create` routine — codegen synthesizes a struct
        // literal in `EmitRecordConstruction`. Without a routine to enqueue, reachability would
        // never mark the constructed type as a live owner, so the demand collector's owner-driven
        // force-seed would never reach its derived operators (ne/lt/...). Add the type to the
        // live-owner set directly when we recognise this construction pattern. Affects user records
        // like Point that obey Equatable/Comparable and rely on synthesised ne/lt/le/gt/ge bodies.
        if (match == null && ct is RecordTypeInfo or EntityTypeInfo)
        {
            _liveOwnerTypes.Add(item: ct);
        }
        return match;
    }

    /// <summary>
    /// Infers an expression's type using a local-variable map plus chained memberRoutine-call return
    /// types. Backstops <see cref="Expression.ResolvedType"/> for stdlib bodies where SA leaves
    /// receiver types unset (e.g. <c>buffer.offset(write_pos.bytes()).inject(...)</c>).
    /// </summary>
    private TypeInfo? InferExpressionType(Expression e)
    {
        if (e.ResolvedType != null) return e.ResolvedType;
        return e switch
        {
            IdentifierExpression id => InferIdentifierType(id: id),
            CallExpression ce => InferCallType(ce: ce),
            _ => null
        };
    }

    /// <summary>Infers the type of a bare identifier via `me`, the local-type map, or a type name.</summary>
    private TypeInfo? InferIdentifierType(IdentifierExpression id)
    {
        if (id.Name == "me") return _meType;
        if (_localTypes.TryGetValue(key: id.Name, value: out TypeInfo? t)) return t;
        // Treat bare type identifier as the type itself (for `Byte(...)` callee resolution).
        return ctx.Registry.LookupType(name: id.Name);
    }

    /// <summary>
    /// Infers a call expression's result type from its resolved routine / constructed type, or by
    /// looking up the member routine's return type on the inferred receiver.
    /// </summary>
    private TypeInfo? InferCallType(CallExpression ce)
    {
        if (ce.ResolvedRoutine?.ReturnType != null) return ce.ResolvedRoutine.ReturnType;
        if (ce.ConstructedType != null) return ce.ConstructedType;
        if (ce.Callee is MemberExpression mem)
        {
            TypeInfo? recv = InferExpressionType(e: mem.Object);
            if (recv == null) return null;
            RoutineInfo? mm = ctx.Registry.LookupMemberRoutine(type: recv, memberRoutineName: mem.MemberName);
            return mm?.ReturnType;
        }
        if (ce.Callee is IdentifierExpression idC)
            return ctx.Registry.LookupType(name: idC.Name);
        return null;
    }

    /// <summary>
    /// Builds the parameter portion of the local-type map. Var-decl types are added later by
    /// <see cref="CollectLocalVarTypes"/> after a full body walk.
    /// </summary>
    private Dictionary<string, TypeInfo> BuildLocalTypes(RoutineDeclaration decl,
        Dictionary<string, TypeInfo> typeSubs)
    {
        var map = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        foreach (Parameter p in decl.Parameters)
        {
            if (p.Type == null) continue;
            TypeInfo? t = ResolveTypeExpression(typeExpr: p.Type, typeSubs: typeSubs);
            if (t != null) map[key: p.Name] = t;
        }
        return map;
    }

    /// <summary>
    /// Walks the body and registers <c>var x = ...</c> bindings into the local-type map. Uses
    /// the explicit type annotation when present, falling back to inferring from the initializer.
    /// </summary>
    /// <summary>
    /// Single-traversal fusion of the former <c>CollectCalls</c> + <c>CollectLocalVarTypes</c> body
    /// walks: for each node it BOTH collects call-like nodes into <paramref name="sink"/> AND records
    /// var-decl types into <paramref name="map"/>. VariableDeclaration and the call-like node kinds are
    /// disjoint, so applying both checks per node is exactly the union of the two old passes — at one
    /// AST walk instead of two (the biggest per-frame RRP cost).
    /// </summary>
    private void CollectCallsAndLocalVarTypes(object? node, List<object> sink,
        Dictionary<string, TypeInfo> map, Dictionary<string, TypeInfo>? captured = null)
    {
        AstWalker.Walk(root: node, visit: n =>
        {
            if (n is VariableDeclaration vd)
            {
                TypeInfo? t = vd.Type != null
                    ? ResolveTypeExpression(typeExpr: vd.Type, typeSubs: null)
                    : (vd.Initializer != null ? InferExpressionType(e: vd.Initializer) : null);
                if (t != null)
                {
                    map[key: vd.Name] = t;
                    // For the reachability body-scan cache: record ONLY the var-decl types (not params),
                    // so a cached hit reproduces this exact map by merging them onto BuildLocalTypes params.
                    captured?[key: vd.Name] = t;
                }
            }
            if (IsCallLikeNode(n: n)) sink.Add(item: n);
        });
    }

    /// <summary>
    /// Resolves a TypeExpression to a TypeInfo via the registry. Honors generic arguments by
    /// recursively resolving and looking up the instantiation. Generic-param substitutions are
    /// applied first when <paramref name="typeSubs"/> is non-null.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr,
        Dictionary<string, TypeInfo>? typeSubs)
    {
        if (typeExpr.ResolvedType != null) return typeExpr.ResolvedType;
        if (typeSubs != null && typeSubs.TryGetValue(key: typeExpr.Name, value: out TypeInfo? sub))
            return sub;
        return ctx.Registry.LookupType(name: typeExpr.Name);
    }

    /// <summary>
    /// EmitThrow in codegen calls <c>errorType.crash_message</c> directly to format the panic
    /// message — there is no source-AST CallExpression to drive reachability. Mark it live here
    /// for every throw site we walk.
    /// </summary>
    private void EnqueueThrowCrashMessage(ThrowStatement throwStmt)
    {
        TypeInfo? errorType = throwStmt.Error.ResolvedType
            ?? (throwStmt.Error is CreatorExpression cre ? cre.ConstructedType : null);
        if (errorType == null) return;
        RoutineInfo? crashMsg = ctx.Registry.LookupMemberRoutine(type: errorType, memberRoutineName: RuntimeContract.CrashMessage);
        if (crashMsg != null) EnqueueCallee(callee: crashMsg);
    }

    private RoutineInfo? ResolveGenericDefRoutine(RoutineInfo callee)
    {
        TypeInfo? owner = callee.OwnerType;
        if (owner == null) return null;
        TypeInfo? genDef = owner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
            _ => null
        };
        if (genDef == null || ReferenceEquals(objA: genDef, objB: owner)) return null;
        return ctx.Registry.LookupMemberRoutine(type: genDef, memberRoutineName: callee.Name);
    }

    private RoutineDeclaration? FindDecl(RoutineInfo callee)
    {
        if (callee.OwnerType == null)
            return FindStandaloneDecl(callee: callee);

        // Member routine: stdlib decls have names like "List[T].insertion_sort" or "S32.add".
        TypeInfo owner = callee.OwnerType;
        return FindGenericOwnerMemberDecl(callee: callee, owner: owner)
               ?? FindConcreteOwnerMemberDecl(callee: callee, owner: owner)
               ?? FindUniversalMemberDecl(callee: callee);
    }

    /// <summary>
    /// Finds the AST decl for a standalone (owner-less) routine, preferring the module-qualified
    /// name (BaseName = "Module.name") over the bare name across both index tables.
    /// </summary>
    private RoutineDeclaration? FindStandaloneDecl(RoutineInfo callee)
    {
        // Standalone routine: prefer the module-qualified name (BaseName = "Module.name") so two
        // modules' same-named routines resolve to their OWN decl, then fall back to the bare name
        // (covers empty-module routines and stdlib). Qualified is tried in BOTH indices first —
        // imported project modules are carried in the stdlib index, so a bare-name match in the
        // user index (e.g. the entry module's own `start`) must not win over the correct
        // module-qualified decl.
        if (callee.BaseName != callee.Name)
        {
            if (_userByName.TryGetValue(key: callee.BaseName, value: out List<RoutineDeclaration>? uq))
                return MatchOverload(decls: uq, callee: callee);
            if (_stdlibByName.TryGetValue(key: callee.BaseName, value: out List<RoutineDeclaration>? sq))
                return MatchOverload(decls: sq, callee: callee);
        }
        if (_userByName.TryGetValue(key: callee.Name, value: out List<RoutineDeclaration>? u))
            return MatchOverload(decls: u, callee: callee);
        if (_stdlibByName.TryGetValue(key: callee.Name, value: out List<RoutineDeclaration>? s))
            return MatchOverload(decls: s, callee: callee);
        return null;
    }

    /// <summary>
    /// Finds the AST decl for a member routine keyed under a GENERIC-DEFINITION owner (protocol
    /// default / generic type), trying the short and identity key forms in both index tables.
    /// </summary>
    private RoutineDeclaration? FindGenericOwnerMemberDecl(RoutineInfo callee, TypeInfo owner)
    {
        // Resolve the generic-definition owner the decl is keyed under. A member routine's AST
        // decl lives under the owner it was DECLARED on, in generic-definition shape — NOT the
        // concrete receiver it was resolved onto. The authoritative declaring owner is
        // callee.GenericDefinition.OwnerType whenever that is itself a GENERIC owner: for a
        // protocol default / extension memberRoutine the receiver is a concrete type (`List[S64]`) but
        // the decl is keyed under the protocol (`Iterable[T].Set`); for an ordinary generic member
        // it is the generic type (`List[T].insertion_sort`). Fall back to the concrete owner's own
        // generic definition for callees with no such link. (A bare-generic-parameter declaring
        // owner — universal memberRoutines like `T.hijack` — is handled separately below.)
        TypeInfo? declGenericOwner =
            callee.GenericDefinition?.OwnerType is { GenericParameters.Count: > 0 } dgo
                ? dgo
                : owner switch
                {
                    RecordTypeInfo r => r.GenericDefinition,
                    EntityTypeInfo e => e.GenericDefinition,
                    WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
                    _ => null
                };
        if (declGenericOwner is not { GenericParameters.Count: > 0 } genDef) return null;

        string genericKey = $"{RoutineInfo.GetTypeIdentity(type: genDef)}.{callee.Name}";
        // Stdlib decl name is the SHORT form like "List[T].insertion_sort" / "Iterable[T].Set".
        string shortKey = $"{genDef.Name}[{string.Join(separator: ", ", values: genDef.GenericParameters!)}].{callee.Name}";
        // REALM-SCOPED: the decl key is realm-FREE (`List[T].create`), so the RazorForge-realm
        // `Core.List` and the Suflae-realm wrapper both index here. Without realm scoping the
        // first-registered (RF) body is walked for the SF wrapper create — so the wrapper's inner
        // `RF::Core.List[T]()` create is never seeded (over-prune). Prefer the callee-realm decl.
        if (_stdlibByName.TryGetValue(key: shortKey, value: out List<RoutineDeclaration>? gd))
            return MatchOverload(decls: RealmScoped(decls: gd, callee: callee), callee: callee);
        if (_stdlibByName.TryGetValue(key: genericKey, value: out List<RoutineDeclaration>? gd2))
            return MatchOverload(decls: RealmScoped(decls: gd2, callee: callee), callee: callee);
        // User-defined generic memberRoutines (e.g. `LinkedList[T].add_last` in playground code)
        // are keyed under the gendef shape in _userByName, NOT under the monomorphised
        // concrete-owner key. Without this lookup, FindDecl returns null for every
        // user-generic instantiation, so its body is never walked and calls inside it
        // (e.g. `node.retain()`) never reach the live set.
        if (_userByName.TryGetValue(key: shortKey, value: out List<RoutineDeclaration>? gdu))
            return MatchOverload(decls: RealmScoped(decls: gdu, callee: callee), callee: callee);
        if (_userByName.TryGetValue(key: genericKey, value: out List<RoutineDeclaration>? gdu2))
            return MatchOverload(decls: RealmScoped(decls: gdu2, callee: callee), callee: callee);
        return null;
    }

    /// <summary>
    /// Finds the AST decl for a member routine on a CONCRETE owner (e.g. "S32.add"), merging stdlib
    /// and user decls and scoping to the callee's module + realm.
    /// </summary>
    private RoutineDeclaration? FindConcreteOwnerMemberDecl(RoutineInfo callee, TypeInfo owner)
    {
        // Concrete owner: e.g. "S32.add" or "Bytes.split". Match over BOTH stdlib AND user decls
        // combined: a user program may define a NEW overload of a stdlib-type memberRoutine (e.g.
        // `routine F64.create(from: D32B)` in playground code, against Core.F64's many numeric
        // `create` overloads). Checking stdlib alone first lets MatchOverload's count-only
        // fallback bind to the wrong overload (`create(S8)`) and walk ITS body — so the real
        // D32B body's callees (`F64.from_bits`, `coeff.F64()`) never reach the live set. Merging
        // lets the exact type-signature match (Pass 1) pick the user D32B overload.
        string concreteKey = $"{owner.Name}.{callee.Name}";
        bool hasStdlib = _stdlibByName.TryGetValue(key: concreteKey, value: out List<RoutineDeclaration>? c);
        bool hasUser = _userByName.TryGetValue(key: concreteKey, value: out List<RoutineDeclaration>? cu);
        if (!hasStdlib && !hasUser) return null;

        var combined = new List<RoutineDeclaration>();
        if (hasStdlib) combined.AddRange(collection: c!);
        if (hasUser) combined.AddRange(collection: cu!);
        // MODULE-SCOPED: the index key is the BARE owner name ("Box"), so two modules that each
        // declare `record Box` collide here. Prefer the decl whose registered owner is the SAME
        // module as the callee — otherwise MatchOverload's count-only fallback walks the wrong
        // module's body (typing `me` as the other module's `Box`), so calls inside it (`me.hijack()`)
        // resolve to the wrong module's universal-memberRoutine instance and this module's stays undefined.
        List<RoutineDeclaration> scoped = combined
            .Where(predicate: d => d.ResolvedInfo?.OwnerType?.FullName == owner.FullName)
            .ToList();
        // FullName is realm-free, so the module scope above keeps BOTH world-lines' decls for a
        // wrapped Core type — narrow further to the callee's realm (RF vs SF).
        return MatchOverload(decls: RealmScoped(decls: scoped.Count > 0 ? scoped : combined, callee: callee), callee: callee);
    }

    /// <summary>
    /// Finds the AST decl for a universal-memberRoutine instance (callee produced by
    /// SubstituteMemberRoutineForOwner from a bare-generic-parameter receiver), keyed as
    /// `{T-param-name}.{memberRoutine-name}`.
    /// </summary>
    private RoutineDeclaration? FindUniversalMemberDecl(RoutineInfo callee)
    {
        // Universal-memberRoutine instance: callee was produced by SubstituteMemberRoutineForOwner from a
        // routine whose receiver is a bare generic parameter (e.g. `T.hijack()` substituted to
        // `Bytes.hijack`). The AST decl lives under the universal-memberRoutine form, keyed as
        // `{T-param-name}.{memberRoutine-name}` (e.g. "T.hijack").
        if (callee.GenericDefinition?.OwnerType is GenericParameterTypeInfo universalParam)
        {
            string universalKey = $"{universalParam.Name}.{callee.Name}";
            if (_stdlibByName.TryGetValue(key: universalKey, value: out List<RoutineDeclaration>? uMemberRoutine))
                return MatchOverload(decls: uMemberRoutine, callee: callee);
        }

        return null;
    }

    /// <summary>
    /// Narrows a realm-free-keyed decl list to the ones whose registered owner realm matches the
    /// callee's owner realm. The reachability decl indices key on the realm-FREE owner name, so a
    /// wrapped Core type (RazorForge `Core.List` + the Suflae `Core.List` wrapper) lands both realms'
    /// decls under one key. Walking the wrong realm's body (RF's real create vs the SF wrapper's
    /// `RF::Core.List[T]()` forwarder) leaves the other realm's callees unseeded → over-prune. Falls
    /// back to the full list when no decl matches the realm (RF-only compiles are unaffected: every
    /// decl is RF and the callee is RF, so the filter is a no-op).
    /// </summary>
    private static List<RoutineDeclaration> RealmScoped(List<RoutineDeclaration> decls, RoutineInfo callee)
    {
        string calleeRealm = callee.OwnerType?.Realm ?? "RF";
        List<RoutineDeclaration> matched = decls
            .Where(predicate: d => (d.ResolvedInfo?.OwnerType?.Realm ?? "RF") == calleeRealm)
            .ToList();
        return matched.Count > 0 ? matched : decls;
    }

    /// <summary>
    /// Picks the overload whose AST parameter list matches the callee's signature. Falls back
    /// to count-only match, then the first decl, so single-overload lookups stay correct.
    /// </summary>
    private static RoutineDeclaration? MatchOverload(List<RoutineDeclaration> decls, RoutineInfo callee)
    {
        if (decls.Count == 1) return decls[index: 0];
        int paramCount = callee.Parameters.Count;
        // Pass 1: match by param count + serialized param type signatures.
        foreach (RoutineDeclaration d in decls)
        {
            if (d.Parameters.Count != paramCount) continue;
            bool typesMatch = true;
            for (int i = 0; i < paramCount; i++)
            {
                string declSig = TypeExpressionSig(typeExpr: d.Parameters[index: i].Type);
                string calleeSig = callee.Parameters[index: i].Type?.Name ?? "";
                if (declSig != calleeSig) { typesMatch = false; break; }
            }
            if (typesMatch) return d;
        }
        // Pass 2: count-only.
        foreach (RoutineDeclaration d in decls)
        {
            if (d.Parameters.Count == paramCount) return d;
        }
        return decls[index: 0];
    }

    /// <summary>
    /// Serializes a TypeExpression to the same form TypeInfo.Name uses for generic instances:
    /// <c>Name[Arg1, Arg2]</c>. Used by overload matching to compare against callee parameter
    /// type names which include their generic args inline.
    /// </summary>
    private static string TypeExpressionSig(TypeExpression? typeExpr)
    {
        if (typeExpr == null) return "";
        if (typeExpr.GenericArguments is not { Count: > 0 }) return typeExpr.Name;
        var args = string.Join(separator: ", ",
            values: typeExpr.GenericArguments.Select(selector: TypeExpressionSig));
        return $"{typeExpr.Name}[{args}]";
    }

    /// <summary>
    /// Substitutes the generic-def callee owner+typeArgs through the calling frame's typeSubs
    /// to yield a concrete callee. E.g. callee = <c>List[T].insertion_sort</c>, frame subs
    /// {T -> Text} -> <c>List[Text].insertion_sort</c>.
    /// </summary>
    private RoutineInfo SubstituteRoutine(RoutineInfo routine, Dictionary<string, TypeInfo> typeSubs)
    {
        if (typeSubs.Count == 0) return routine;

        // Standalone generic routine (e.g. hijacked_from[T]). Build the concrete instantiation
        // and add its registry key directly to _live, so GMP picks it up via LiveRoutineKeys.
        if (routine.OwnerType == null)
        {
            // Case A: pure generic def — substitute by GenericParameters.
            if (routine.IsGenericDefinition)
            {
                List<string>? rgParams = routine.GenericParameters;
                if (rgParams != null)
                {
                    var concreteTypeArgs = new List<TypeInfo>(capacity: rgParams.Count);
                    bool allOk = true;
                    foreach (string p in rgParams)
                    {
                        if (typeSubs.TryGetValue(key: p, value: out TypeInfo? sub))
                            concreteTypeArgs.Add(item: sub);
                        else { allOk = false; break; }
                    }
                    if (allOk)
                    {
                        RoutineInfo resolved = ctx.Registry.GetOrCreateRoutineResolution(
                            genericDef: routine, typeArguments: concreteTypeArgs);
                        _live.Add(item: resolved.RegistryKey);
                        return resolved;
                    }
                }
            }
            // Case B: SA produced a "resolved" instance whose TypeArguments contain unresolved
            // generic parameters — either bare (e.g. `hijacked_from[T]` where T comes from the
            // enclosing `List[T]`) OR nested inside another generic type (e.g.
            // `hijacked_from[RetainController[T]]` inside `Tracked[T].release`). Substitute
            // recursively via SubstituteIncludingGenericDef so both shapes work.
            if (routine.TypeArguments is { Count: > 0 } tArgs
                && tArgs.Any(predicate: ContainsAnyGenericParameter))
            {
                var substArgs = new List<TypeInfo>(capacity: tArgs.Count);
                bool allOk = true;
                foreach (TypeInfo a in tArgs)
                {
                    TypeInfo subbed = SubstituteIncludingGenericDef(type: a, typeSubs: typeSubs);
                    if (ContainsAnyGenericParameter(type: subbed)) { allOk = false; break; }
                    substArgs.Add(item: subbed);
                }
                if (allOk)
                {
                    RoutineInfo? genDef = routine.GenericDefinition ?? routine;
                    RoutineInfo resolved = ctx.Registry.GetOrCreateRoutineResolution(
                        genericDef: genDef, typeArguments: substArgs);
                    _live.Add(item: resolved.RegistryKey);
                    return resolved;
                }
            }
            return routine;
        }

        TypeInfo owner = routine.OwnerType;

        // Universal everywhere-derive: owner is a BARE generic parameter (e.g. `T.duplicate() -> T`, the
        // Copyable/Assignable structural template). `me.value.duplicate()` in `Maybe[T].unwrap` binds here.
        // Substitute the owner param via the frame's typeSubs to the concrete RECEIVER type, mark THAT type a
        // live owner so `ProcessConcreteType` builds exactly its derives, and rebind to the concrete member.
        // Demand-driven + bounded: only concrete types a real derive-call names become live (a finite closure
        // that converges), unlike a blanket "process every instance" that combinatorially explodes.
        if (owner is GenericParameterTypeInfo gpOwner
            && typeSubs.TryGetValue(key: gpOwner.Name, value: out TypeInfo? concreteRecv)
            && !ContainsAnyGenericParameter(type: concreteRecv))
        {
            _liveOwnerTypes.Add(item: concreteRecv);
            RoutineInfo? concreteDerive = ctx.Registry.LookupMemberRoutine(
                type: concreteRecv, memberRoutineName: routine.Name, isFailable: routine.IsFailable);
            if (concreteDerive is { OwnerType: not GenericParameterTypeInfo and not { IsGenericDefinition: true } })
                return concreteDerive;
            // Concrete derive not registered as a distinct member yet — marking the owner live above lets
            // ProcessConcreteType materialize it from the generic def; keep the universal routine for now.
            return routine;
        }

        // Owner like ListEmitter[T] or Hijacked[BTreeSetNode[T]] — stored as a resolution whose
        // TypeArguments contain GenericParameterTypeInfo, possibly nested inside another generic
        // resolution. Substitute the params (recursively, via RoutineInfo.SubstituteType) to get
        // a concrete owner. The `ContainsAnyGenericParameter` check is needed for two-level
        // wrappers like `Hijacked[BTreeListNode[T]]` — the immediate arg `BTreeListNode[T]` is
        // an EntityTypeInfo (not a bare param), so a shallow `Any(t is GenericParameterTypeInfo)`
        // misses them and leaves `Hijacked[BTreeListNode[T]]` un-monomorphised.
        if (owner.TypeArguments is { Count: > 0 } ownerTArgs
            && ownerTArgs.Any(predicate: ContainsAnyGenericParameter))
        {
            TypeInfo? ownerGenDef = owner switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
                _ => null
            };
            if (ownerGenDef != null)
            {
                var substArgs = new List<TypeInfo>(capacity: ownerTArgs.Count);
                bool allOk = true;
                foreach (TypeInfo arg in ownerTArgs)
                {
                    // Recursive substitution: handles `BTreeListNode[T]` -> `BTreeListNode[S64]`,
                    // the bare `T` case, AND the bare-generic-def case (e.g. SA-emitted
                    // `Hijacked[BTreeListNode]` whose inner `BTreeListNode` is the generic-def
                    // itself rather than `BTreeListNode[T]`). `SubstituteIncludingGenericDef`
                    // adds the generic-def → instance step that plain SubstituteType skips.
                    TypeInfo substituted = SubstituteIncludingGenericDef(type: arg, typeSubs: typeSubs);
                    if (ContainsAnyGenericParameter(type: substituted)) { allOk = false; break; }
                    substArgs.Add(item: substituted);
                }
                if (allOk)
                {
                    TypeInfo concreteOwner = ctx.Registry.GetOrCreateResolution(
                        genericDef: ownerGenDef, typeArguments: substArgs);
                    _liveOwnerTypes.Add(item: concreteOwner);
                    // Use param-count + failability to disambiguate overloads — LookupMemberRoutine's
                    // first-match heuristic picks the wrong overload when the routine name has
                    // both failable and non-failable variants (e.g. SortedDict.get_by_rank!(U64)
                    // vs SortedDict.get_by_rank(BTreeDictNode, U64)). Without this, reach marks
                    // the failable 1-arg version live while codegen call sites need the
                    // non-failable 2-arg version.
                    RoutineInfo? resolved = LookupMemberRoutineMatchingParamTypes(
                        owner: concreteOwner, memberRoutineName: routine.Name,
                        inputRoutine: routine, typeSubs: typeSubs)
                        ?? LookupMemberRoutineMatchingSignature(
                            owner: concreteOwner, memberRoutineName: routine.Name,
                            paramCount: routine.Parameters.Count, isFailable: routine.IsFailable)
                        ?? ctx.Registry.LookupMemberRoutine(type: concreteOwner, memberRoutineName: routine.Name);
                    if (resolved != null) return TransferSubstitutedTypeArguments(input: routine, resolved: resolved, typeSubs: typeSubs);
                    // Fallback: synthesized routine, mark substituted RegistryKey live
                    var synthSubs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
                    List<string>? defParams = ownerGenDef.GenericParameters;
                    if (defParams != null)
                    {
                        for (int i = 0; i < defParams.Count && i < substArgs.Count; i++)
                            synthSubs[key: defParams[index: i]] = substArgs[index: i];
                    }
                    string concreteKey = ComputeConcreteRegistryKey(routine: routine, concreteOwner: concreteOwner, subs: synthSubs);
                    _live.Add(item: concreteKey);
                }
            }
        }
        // If owner is itself a generic param T and frame has T -> ConcreteType, substitute.
        if (owner is GenericParameterTypeInfo gp && typeSubs.TryGetValue(key: gp.Name, value: out TypeInfo? concrete))
        {
            RoutineInfo? resolved = ctx.Registry.LookupMemberRoutine(type: concrete, memberRoutineName: routine.Name);
            return resolved != null
                ? TransferSubstitutedTypeArguments(input: routine, resolved: resolved, typeSubs: typeSubs)
                : routine;
        }
        // If owner is a generic def (e.g. List[T]) referencing T from the frame, build the
        // concrete instantiation List[ConcreteT] and look up the memberRoutine on it.
        List<string>? gParams = owner.GenericParameters;
        if (gParams is { Count: > 0 } && owner.IsGenericDefinition)
        {
            var concreteArgs = new List<TypeInfo>(capacity: gParams.Count);
            bool allResolved = true;
            foreach (string p in gParams)
            {
                if (!typeSubs.TryGetValue(key: p, value: out TypeInfo? a))
                {
                    allResolved = false;
                    break;
                }
                concreteArgs.Add(item: a);
            }
            if (allResolved)
            {
                TypeInfo concreteOwner = ctx.Registry.GetOrCreateResolution(
                    genericDef: owner, typeArguments: concreteArgs);
                _liveOwnerTypes.Add(item: concreteOwner);
                // Disambiguate overloads by parameter signature. LookupMemberRoutine's first-match heuristic
                // picks the wrong overload when multiple share name + count + failability — e.g.
                // List[T] has several 1-arg non-failable `create` overloads (create(capacity: U64),
                // create(from: Set[T]), etc.). Match on substituted parameter type names so
                // `List[T].create#U64` substitutes to `List[S64].create(capacity: U64)` instead of
                // dragging an unrelated overload's `.iter` into the live set.
                RoutineInfo? resolved = LookupMemberRoutineMatchingParamTypes(
                    owner: concreteOwner, memberRoutineName: routine.Name,
                    inputRoutine: routine, typeSubs: typeSubs)
                    ?? LookupMemberRoutineMatchingSignature(
                        owner: concreteOwner, memberRoutineName: routine.Name,
                        paramCount: routine.Parameters.Count, isFailable: routine.IsFailable)
                    ?? ctx.Registry.LookupMemberRoutine(type: concreteOwner, memberRoutineName: routine.Name);
                if (resolved != null) return TransferSubstitutedTypeArguments(input: routine, resolved: resolved, typeSubs: typeSubs);
                // Synthesized routines (try_emit, represent, diagnose, wrapper forwarders) live
                // only on the generic-def. Manually mark the substituted RegistryKey live so the
                // codegen Phase B/C gates emit the concrete-form symbol that callers reference.
                string concreteKey = ComputeConcreteRegistryKey(routine: routine, concreteOwner: concreteOwner, subs: typeSubs);
                _live.Add(item: concreteKey);
            }
        }
        return routine;
    }

    private static bool ContainsAnyGenericParameter(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo) return true;
        // Bare generic-def appearing as a nested type argument (e.g. SA emitting
        // `Hijacked[BTreeListNode]` instead of `Hijacked[BTreeListNode[T]]` on the
        // ResolvedRoutine for a memberRoutine-generic call). The generic-def itself is
        // "uninstantiated" relative to the enclosing routine's typeSubs and needs the
        // same substitution treatment a `T` argument would.
        if (type is { IsGenericDefinition: true, GenericParameters.Count: > 0 }) return true;
        return type.TypeArguments is { Count: > 0 } args &&
               args.Any(a => ContainsAnyGenericParameter(type: a));
    }

    /// <summary>
    /// Substitutes <paramref name="type"/> with <paramref name="typeSubs"/>, also handling the
    /// case where <paramref name="type"/> is a bare generic definition whose own
    /// <see cref="TypeInfo.GenericParameters"/> can be filled from the substitution map. Plain
    /// <see cref="RoutineInfo.SubstituteType"/> only substitutes by matching <c>type.Name</c>
    /// against substitution keys (good for bare params) or recursing into existing TypeArguments,
    /// so it leaves bare generic-defs alone.
    /// </summary>
    private TypeInfo SubstituteIncludingGenericDef(TypeInfo type, Dictionary<string, TypeInfo> typeSubs)
    {
        if (type is { IsGenericDefinition: true, GenericParameters: { Count: > 0 } gParams })
        {
            var concreteArgs = new List<TypeInfo>(capacity: gParams.Count);
            bool allOk = true;
            foreach (string p in gParams)
            {
                if (typeSubs.TryGetValue(key: p, value: out TypeInfo? sub))
                {
                    // Recurse: sub may itself need further substitution (rare but possible
                    // when typeSubs maps to types involving generic params).
                    concreteArgs.Add(item: SubstituteIncludingGenericDef(type: sub, typeSubs: typeSubs));
                }
                else { allOk = false; break; }
            }
            if (allOk)
            {
                return ctx.Registry.GetOrCreateResolution(genericDef: type, typeArguments: concreteArgs);
            }
        }
        return RoutineInfo.SubstituteType(type: type, substitution: typeSubs);
    }

    /// <summary>
    /// Propagates the substituted memberRoutine-level <see cref="RoutineInfo.TypeArguments"/> from the
    /// caller's input routine (post-typeSubs) onto the routine returned by
    /// <c>LookupMemberRoutine</c>(concreteOwner). The lookup path goes through
    /// <c>SubstituteMemberRoutineForOwner</c>, which preserves <c>memberRoutine.TypeArguments</c> from the
    /// registered def — that is null/empty for stdlib defs, so the resulting RegistryKey omits
    /// the memberRoutine-level type args (e.g. <c>Hijacked[S64].recast_as</c> instead of
    /// <c>Hijacked[S64].recast_as[S64]</c>). Without this fix, definition emission and call-site
    /// references diverge -> linker error.
    ///
    /// We take the input routine's TypeArguments (which carry the SA-resolved memberRoutine-level
    /// bindings), substitute them through <paramref name="typeSubs"/>, and graft them onto
    /// <paramref name="resolved"/> as a fresh clone. Returns <paramref name="resolved"/>
    /// unchanged when there is nothing to graft or when substitution leaves a generic parameter
    /// behind.
    /// </summary>
    private RoutineInfo TransferSubstitutedTypeArguments(RoutineInfo input,
        RoutineInfo resolved, Dictionary<string, TypeInfo> typeSubs)
    {
        if (input.TypeArguments is not { Count: > 0 } tArgs) return resolved;

        // SA's parser collects every bare identifier from a routine signature into
        // RoutineDeclaration.GenericParameters — including owner-level params like the `T` in
        // `routine SortedList[T].get_by_rank(...)`. They appear in both the registered routine's
        // GenericParameters AND TypeArguments. After owner monomorphization (T → S64), this fix
        // would mistakenly clone the routine with `TypeArguments=[S64]`, producing a mangled
        // symbol `SortedList[S64].get_by_rank[S64]` that codegen call sites never reference.
        // Detect owner-leak: if every name in the def's GenericParameters also appears in the
        // owner's GenericParameters, the def has no true memberRoutine-level generics — skip transfer.
        List<string>? defParams = (input.GenericDefinition ?? input).GenericParameters;
        List<string>? ownerParams = input.OwnerType switch
        {
            EntityTypeInfo { GenericDefinition: { } d } => d.GenericParameters ?? input.OwnerType.GenericParameters,
            RecordTypeInfo { GenericDefinition: { } d } => d.GenericParameters ?? input.OwnerType.GenericParameters,
            { } o => o.GenericParameters,
            _ => null
        };
        if (defParams is { Count: > 0 } && ownerParams is { Count: > 0 }
            && defParams.All(predicate: ownerParams.Contains))
        {
            return resolved;
        }

        var newArgs = new List<TypeInfo>(capacity: tArgs.Count);
        foreach (TypeInfo t in tArgs)
        {
            TypeInfo subbed = RoutineInfo.SubstituteType(type: t, substitution: typeSubs);
            if (ContainsAnyGenericParameter(type: subbed)) return resolved; // partial — bail
            newArgs.Add(item: subbed);
        }

        // Already aligned (e.g. resolved.TypeArguments was already set the same way).
        if (resolved.TypeArguments is { Count: > 0 } existing
            && existing.Count == newArgs.Count
            && existing.Zip(newArgs, (a, b) => a.FullName == b.FullName).All(x => x))
        {
            return resolved;
        }

        var clone = new RoutineInfo(name: resolved.Name)
        {
            Kind = resolved.Kind,
            OwnerType = resolved.OwnerType,
            Parameters = resolved.Parameters,
            ReturnType = resolved.ReturnType,
            // Preserve the receiver-handle type (Suflae entity `me` = Roamed[E]); already concrete on
            // `resolved` (the SubstituteMemberRoutineForOwner output), so carry it through unchanged.
            MeType = resolved.MeType,
            IsFailable = resolved.IsFailable,
            DeclaredMutation = resolved.DeclaredMutation,
            MutationCategory = resolved.MutationCategory,
            // Clear memberRoutine-level generic parameters: `newArgs` carries the fully-substituted
            // memberRoutine-level type arguments, so this routine is no longer a generic definition.
            // Leaving `GenericParameters` populated produces a routine with BOTH a non-empty
            // `GenericParameters` and a non-empty `TypeArguments` — its `RegistryKey` collides
            // with what codegen's `GetOrCreateRoutineResolution` will compute later, so the
            // cache hands back this polluted entry and codegen's `IsGenericDefinition` check
            // still trips ("Explicit member routine generic call ... reached LLVM codegen unresolved").
            GenericParameters = null,
            GenericConstraints = null,
            Visibility = resolved.Visibility,
            Location = resolved.Location,
            Module = resolved.Module,
            ModulePath = resolved.ModulePath,
            Annotations = resolved.Annotations,
            CallingConvention = resolved.CallingConvention,
            IsVariadic = resolved.IsVariadic,
            IsDangerous = resolved.IsDangerous,
            IsSynthesized = resolved.IsSynthesized,
            TypeArguments = newArgs,
            // Point GenericDefinition at `resolved` itself (the SubstituteMemberRoutineForOwner result):
            // its GenericParameters are already filtered to memberRoutine-level only ([U] for
            // Hijacked[T].recast_as[U]) and its OwnerType is the concrete owner. That alignment
            // is what GenericMonomorphizationPass.BuildResolvedRoutineTypeSubstitutions expects
            // when zipping memberRoutineParams against memberRoutineTypeArgs.
            GenericDefinition = resolved,
            WrapperForwarderInnerMemberRoutine = resolved.WrapperForwarderInnerMemberRoutine,
            WrapperForwarderInnerGenericDef = resolved.WrapperForwarderInnerGenericDef,
            AsyncStatus = resolved.AsyncStatus,
            FailableVariant = resolved.FailableVariant,
            OriginalName = resolved.OriginalName,
            HasThrow = resolved.HasThrow,
            HasAbsent = resolved.HasAbsent,
            HasFailableCalls = resolved.HasFailableCalls,
            ThrowableTypes = resolved.ThrowableTypes,
        };
        // Register so GenericMonomorphizationPass.ProcessResolvedMemberRoutineGenericRoutines
        // (which iterates Registry.GetAllRoutineResolutions()) can build a body for this key.
        return ctx.Registry.RegisterRoutineResolution(resolvedMemberRoutine: clone);
    }

    private static bool ContainsSubstitutableParameter(TypeInfo type, Dictionary<string, TypeInfo> typeSubs)
    {
        if (type is GenericParameterTypeInfo gp) return typeSubs.ContainsKey(key: gp.Name);
        return type.TypeArguments is { Count: > 0 } args &&
               args.Any(a => ContainsSubstitutableParameter(type: a, typeSubs: typeSubs));
    }

    private static string ComputeConcreteRegistryKey(RoutineInfo routine, TypeInfo concreteOwner, Dictionary<string, TypeInfo> subs)
    {
        string ownerKey = concreteOwner.TypeArguments is { Count: > 0 }
            ? $"{concreteOwner.Module}.{concreteOwner.Name}[{string.Join(",", concreteOwner.TypeArguments.Select(t => t.FullName))}]"
            : string.IsNullOrEmpty(value: concreteOwner.Module)
                ? concreteOwner.Name
                : $"{concreteOwner.Module}.{concreteOwner.Name}";
        string baseName = $"{ownerKey}.{routine.Name}";
        if (routine.Parameters.Count == 0) return baseName;
        string paramTypes = string.Join(",", routine.Parameters.Select(p =>
        {
            TypeInfo? pt = p.Type;
            if (pt is GenericParameterTypeInfo gp && subs.TryGetValue(key: gp.Name, value: out TypeInfo? c)) pt = c;
            return pt?.FullName ?? "?";
        }));
        return $"{baseName}#{paramTypes}";
    }

    /// <summary>
    /// Collects every node that implies a callee edge: explicit calls, creator/throw, the
    /// collection literals + IndexExpression that Phase 8 lowers into memberRoutine calls, and
    /// operator BinaryExpressions whose operator maps to a memberRoutine. ProcessFrame then
    /// synthesizes the corresponding call edges.
    /// </summary>
    /// <summary>
    /// Resolves a bare routine name used as a first-class value (a function-pointer argument such
    /// as <c>select(transform: double)</c>) to its <see cref="RoutineInfo"/>, mirroring codegen's
    /// <c>TryResolveRoutineReference</c>. Returns null for identifiers that are NOT top-level
    /// routines (e.g. a local variable holding a <c>Routine</c> value) — those need no extra
    /// reachability marking. Lambdas are excluded: lifted-lambda liveness is handled separately.
    /// </summary>
    private RoutineInfo? ResolveRoutineValueRef(IdentifierExpression id, Frame frame)
    {
        if (id.ResolvedType is not RoutineTypeInfo routineType)
            return null;
        // A local variable shadowing the name is not a routine reference.
        if (_localTypes.ContainsKey(key: id.Name))
            return null;

        // Identifier names are bare; the failable `!` is a structured flag (routineType.IsFailable).
        string bareName = id.Name;
        List<TypeInfo> paramTypes = routineType.ParameterTypes.ToList();
        string? moduleName = frame.Routine.OwnerType?.Module ?? frame.Routine.Module;

        RoutineInfo? routine = null;
        if (moduleName != null && !bareName.Contains(value: '.'))
        {
            routine = ctx.Registry.LookupRoutineOverload(
                baseName: $"{moduleName}.{bareName}", argTypes: paramTypes);
            routine ??= ctx.Registry.LookupRoutine(fullName: $"{moduleName}.{bareName}",
                isFailable: routineType.IsFailable);
        }
        routine ??= ctx.Registry.LookupRoutineOverload(baseName: bareName, argTypes: paramTypes);
        routine ??= ctx.Registry.LookupRoutine(fullName: bareName,
            isFailable: routineType.IsFailable);

        // Lambdas are lifted/closure-converted elsewhere; only mark plain routines here.
        return routine is { IsLambda: false } ? routine : null;
    }

    private static void CollectCalls(object? node, List<object> sink)
    {
        AstWalker.Walk(root: node, visit: n =>
        {
            if (IsCallLikeNode(n: n)) sink.Add(item: n);
        });
    }

    /// <summary>
    /// The set of AST nodes reachability collects for callee resolution: direct calls, constructors,
    /// throws (crash_message), collection/index/f-string/using nodes (implicit lowering callees), a
    /// bare routine used as a first-class VALUE (resolves to RoutineTypeInfo — not a call callee, but
    /// still makes the referenced routine live), force-unwrap, and operator BinaryExpressions.
    /// Single source of truth shared by <see cref="CollectCalls"/> and
    /// <see cref="CollectCallsAndLocalVarTypes"/>.
    /// </summary>
    private static bool IsCallLikeNode(object? n) =>
        n is CallExpression or GenericMemberRoutineCallExpression or CreatorExpression
            or CrashableDispatchExpression
            or ThrowStatement
            or ListLiteralExpression or SetLiteralExpression
            or DictLiteralExpression or IndexExpression
            or InsertedTextExpression
            or UsingStatement
            or IdentifierExpression { ResolvedType: RoutineTypeInfo }
            or UnaryExpression { Operator: UnaryOperator.ForceUnwrap }
        || (n is BinaryExpression bin && bin.Operator.GetMemberRoutineName() != null);
}
