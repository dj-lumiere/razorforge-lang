using Compiler.Desugaring;
using Compiler.Lowering;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using TypeInfo = TypeModel.Types.TypeInfo;
using Compiler.Instantiation;
using Compiler.Instantiation.Passes;

namespace Compiler.Collection.Passes;

/// <summary>
/// Stage ② of the demand-driven ("pull") codegen architecture:
/// <c>desugar-all → [COLLECT + BUILD] → dumb codegen</c>.
///
/// <para>This pass UNIFIES what today are three separate steps — <see cref="RoutineReachabilityPass"/>
/// (walk references from entry points), <see cref="GenericMonomorphizationPass"/> (monomorphize the live
/// set), and per-instance lowering — into ONE demand loop: follow every reference (AST
/// <c>CallExpression.ResolvedRoutine</c>) and monomorphize + lower each referenced routine ON DEMAND, to
/// fixpoint. The output is a COMPLETE materialized body set, so codegen becomes a dumb translator with no
/// liveness gate and no over-prune tripwire.</para>
///
/// <para>Why this kills the recurring bug class: the current push model has reachability PRE-COMPUTE a
/// live set that can diverge from what codegen actually references (the over-prune tripwire fires on that
/// diff; the base define-completeness gap is the same divergence). A pull collector only ever builds what
/// is genuinely referenced, so the divergence — and the runaway of eager enumeration — cannot occur.</para>
///
/// <para>SHADOW MODE (current): <c>RunShadow</c> runs AFTER the existing pipeline and drives the
/// demand collector (<see cref="GenericMonomorphizationPass.CollectReferencedInIsolation"/>) into an
/// ISOLATED COPY of the body/liveness state, then REPORTS how many extra routines it materialized on top
/// of the push pipeline — i.e. exactly the symbols the push pipeline over-prunes. Zero behavior change
/// (flag-gated, builds into the copy, never the real <see cref="InstantiationContext"/>). Once the
/// collector is shown to build the over-pruned symbols (brc <c>List[Byte]</c>, warm <c>try_emit</c>)
/// AND converge (no runaway), codegen flips to consume it and reachability/closure retire.</para>
/// </summary>
internal sealed class RoutineCollectionPass(InstantiationContext ctx)
{
    /// <summary>
    /// Demand collector (additive): from the entry points, materialize + lower any referenced-but-unbuilt
    /// generic instance the push pipeline over-pruned, into the REAL ctx, so codegen sees a COMPLETE set and
    /// the over-prune tripwire cannot fire. Additive — reachability/GenericClosure still run; a program with
    /// no gap builds 0 (a no-op). VERIFIED: the brc byte-slice repro builds+runs with NO workaround
    /// (demand-builds List[Byte].create/add_last/count/getitem/reserve + hijacked_from). Runs POST-Phase-9
    /// (user bodies lowered → the entry walk follows start()'s chain) and resolves callee bodies via
    /// <see cref="BuildProgramBodyIndex"/> (the SAME lowered program ASTs codegen emits from, so it steps
    /// through non-generic bodies like <c>Bytes.create(from_list:)</c>). Always on — the push DCE's over-prune
    /// gap is closed here.
    /// </summary>
    public void RunCollect(IReadOnlyDictionary<string, Statement>? synthesizedBodies = null)
    {
        // ALIAS the real pipeline dicts: freshly-built + lowered bodies land in the real
        // ctx.InstantiatedGenericBodies (== what codegen reads) and their keys in ctx.LiveRoutineKeys.
        var adapter =
            new DesugaringContext(registry: ctx.Registry,
                routineBodies: ctx.RoutineBodies,
                target: ctx.Target,
                buildMode: ctx.BuildMode)
            {
                SaTiming = ctx.SaTiming,
                VariantBodies = ctx.VariantBodies,
                InstantiatedGenericBodies = ctx.InstantiatedGenericBodies,
                LiveRoutineKeys = ctx.LiveRoutineKeys,
                LiveOwnerTypeNames = ctx.LiveOwnerTypeNames,
                SynthesizeAllDerives = ctx.SeedAllStdlibRoutines,
                AnalyzeRoutineOnDemand = ctx.AnalyzeRoutineOnDemand
            };

        List<(string Key, Statement Body)> entrySeeds = CollectEntrySeeds();
        Dictionary<string, Statement> programBodies;

        // FIXPOINT: a freshly-built body is walked PRE-lowering, so references revealed only by lowering (a
        // subscript `list[i]` → `list.getitem(i)`) aren't seen the first round. Loop: collect → LOWER the
        // fresh bodies → collect again (the lowered bodies now expose their callees) → … until a round builds
        // nothing. Bounded by the finite reachable set.
        // PDIL synthesizes per-implementer protocol-default-impl bodies (e.g. `List[S64].List`/`.Set`).
        // Such a call can appear ONLY inside a demand-monomorphized iterator body (`source.List()` inside
        // `ReverseIterable.iter`), which PDIL cannot see until the collector builds that body — so PDIL runs
        // INSIDE the collector fixpoint (real ctx, whose InstantiatedGenericBodies the collector aliases) and
        // its synthesized bodies are folded in by the incremental GMP. Mirrors GenericClosurePass's PDIL↔GMP
        // fixpoint, which the push pipeline relies on but which reachability-disabled demand build bypasses.
        var pdil = new ProtocolDefaultImplLoweringPass(ctx: ctx);
        int guard = 0;
        while (guard++ < 100)
        {
            var before = new HashSet<string>(collection: adapter.InstantiatedGenericBodies.Keys,
                comparer: StringComparer.Ordinal);
            int liveBefore = ctx.LiveRoutineKeys.Count;
            // REBUILD the program-body index EACH round: a reached stdlib file is SA'd + desugared ON DEMAND
            // during the walk (GMP.Discover → AnalyzeRoutineOnDemand), and the lowering passes REASSIGN
            // decl.Body (immutable rewriters → a NEW decl in program.Declarations). The index captured at
            // RunCollect start therefore holds STALE or ABSENT bodies for files reached this round; a routine
            // Discovered here would be marked LIVE but GetBody would MISS it → it never gets walked, yet
            // MaterializeReachedStdlibBodies below still emits it → its callees (a bounds-check `throw`'s
            // crash_message, a `create`'s `gt`, a nested `getitem`) are never seeded → link over-prune (the ④
            // collector-walk-completeness residual). Rebuilding here lets the next walk step THROUGH those
            // freshly-lowered bodies and discover their callees.
            programBodies = BuildProgramBodyIndex();
            int built = new GenericMonomorphizationPass(ctx: adapter).CollectReferencedInIsolation(
                entrySeeds: entrySeeds,
                programBodies: programBodies,
                synthesizedBodies: synthesizedBodies);
            // Synthesize protocol-default-impls referenced by the bodies built this round, BEFORE lowering, so
            // their fresh bodies join the same lower-all-fresh sweep below. Their OWN referenced generic
            // instances are built DEMAND-scoped by the NEXT fixpoint round's CollectReferencedInIsolation walk
            // (a PDIL body is call-reached from an entry point, so the walk steps into it once it exists) — NOT
            // by GMP.RunIncremental, whose registry-wide `AllConcrete*InstancesUnfiltered` drain builds every
            // instance the registry holds. That drain is the warm/cold divergence: warm's restored registry
            // holds the whole stdlib's instances, so it over-materialized derives (Maybe[X].duplicate,
            // *Emittable.destroy) a cold partial-registry build never reaches. Demand-only keeps them identical.
            bool pdilSynth = pdil.Run();
            var freshBodies = adapter.InstantiatedGenericBodies
                                     .Where(predicate: kv => !before.Contains(item: kv.Key))
                                     .ToDictionary(keySelector: kv => kv.Key,
                                          elementSelector: kv => kv.Value,
                                          comparer: StringComparer.Ordinal);
            if (freshBodies.Count > 0)
            {
                GenericClosurePass.LowerFreshBodies(ctx: ctx,
                    adapter: adapter,
                    freshBodies: freshBodies);
                // LowerFreshBodies REASSIGNS entries (`dict[key] = body with { … }`, MonomorphizedBody is a
                // record) on the `freshBodies` COPY, not the shared adapter map — so the lowered results
                // (FString/Operator/VariantReturn/…) live only in the copy. Merge them back or codegen reads
                // the UN-lowered originals (a composed iterator's try_emit reaching codegen with raw
                // VariantReturnStatement). Mirrors GenericClosurePass.RunClosure's merge-back.
                foreach ((string key, MonomorphizedBody body) in freshBodies)
                {
                    adapter.InstantiatedGenericBodies[key: key] = body;
                }
            }

            // MATERIALIZE reached bodies INSIDE the fixpoint (was post-loop). Every body that will be EMITTED
            // must also be WALKED so its codegen-inserted callees are seeded — so materialize now (into
            // InstantiatedGenericBodies, which GetBody consults FIRST), and let the NEXT round's walk step
            // through the freshly-materialized bodies to discover their callees. Materialized stdlib bodies
            // are already lowered (copied from the rebuilt, on-demand-lowered programBodies), so they need no
            // LowerFreshBodies; they land in `before` next round and are not re-processed.
            // Re-read programBodies AFTER the walk: THIS round's walk triggered on-demand SA+lowering of newly
            // reached stdlib files (U64.rf etc.), which reassigned their decl.Body to lowered form. The
            // top-of-round index predates that, so materializing from it would emit an UN-lowered body (a raw
            // `me == 0u64` in U64.represent). Rebuild so materialization copies the lowered decls.
            programBodies = BuildProgramBodyIndex();
            if (synthesizedBodies != null)
            {
                MaterializePerOwnerSynthesizedBodies(synthesizedBodies: synthesizedBodies);
            }

            MaterializeReachedStdlibBodies(programBodies: programBodies);
            // RESOLVE this round's built + materialized bodies BEFORE the next walk. A body materialized from a
            // comptime-`expand`/SoA template reaches here with un-resolved member calls (`me.col[index]` →
            // `Array[S64,4].getitem`); the post-loop CallOverloadResolutionPass resolves them, but by then the
            // walk is over — so the walk never Discovers `Array[S64,4].getitem` and it link-over-prunes. Resolve
            // per-round so the NEXT walk sees the resolved calls and seeds their definitions (the resolve-then-
            // walk ordering that closes the SoA-column-accessor + throw-`create` over-prune). Idempotent on
            // already-resolved calls; scoped to this round's fresh + materialized set via the live keys.
            var roundResolver = new Declaration.CallOverloadResolutionPass(
                ctx: new PostprocessingContext(registry: ctx.Registry,
                    variantBodies: ctx.VariantBodies,
                    target: ctx.Target,
                    buildMode: ctx.BuildMode));
            roundResolver.RunOnBodiesWithOwners(
                bodies: ctx.InstantiatedGenericBodies.Values.Select(selector: b =>
                    (b.Ast.Body, b.Info.OwnerType,
                        (IReadOnlyList<ParameterInfo>?)b.Info.Parameters)));
            // Terminate only when a round adds NO new built instance, NO PDIL synth, AND NO new live key. The
            // live-key check is load-bearing: a reached NON-generic stdlib body (U64.represent, Text.create)
            // grows LiveRoutineKeys without incrementing `built`, and its callees are only discovered when the
            // next round walks it — so stopping on `built == 0` alone would leave those callees unseeded.
            if (built == 0 && !pdilSynth && ctx.LiveRoutineKeys.Count == liveBefore)
            {
                break;
            }
        }

        // REBUILD the program-body index: under the demand flip, each reached stdlib file is SA'd + desugared
        // ON DEMAND during the fixpoint above, and the lowering passes REASSIGN decl.Body (immutable records →
        // new decl in program.Declarations). The index built at RunCollect start therefore holds STALE
        // UN-desugared body references for on-demand-processed files; materializing from it would feed codegen
        // raw bodies (un-inlined presets, un-lowered operators). Re-read now that all reached files are lowered.
        programBodies = BuildProgramBodyIndex();
        if (synthesizedBodies != null)
        {
            MaterializePerOwnerSynthesizedBodies(synthesizedBodies: synthesizedBodies);
        }

        MaterializeReachedStdlibBodies(programBodies: programBodies);

        // Systematic GMCE sweep over the COMPLETE body set: bodies materialized AFTER the fixpoint
        // (MaterializePerOwnerSynthesizedBodies / MaterializeReachedStdlibBodies) never went through
        // GenericClosurePass.LowerFreshBodies, so a GenericMemberRoutineCallExpression in them survives to
        // codegen (which hard-errors). Lower any remaining GMCE across the whole map here — idempotent on
        // already-lowered bodies. (Fixes the class, not one symbol.)
        new Compiler.Desugaring.Passes.GenericCallLoweringPass(ctx: adapter)
           .RunOnInstantiatedGenericBodies(bodies: ctx.InstantiatedGenericBodies);

        // Classify (resolve call overloads / set LoweringKind) across EVERY collector-built body. With eager
        // monomorphization retired, InstantiatedGenericBodies is populated ENTIRELY by the collector (demand
        // fixpoint + materialization above), none of which passed through SemanticVerifier's Phase-8
        // CallOverloadResolutionPass (that ran on the then-empty set). A body with an unresolved call (e.g.
        // `me.assign()` in a record's `duplicate` derive, ResolvedRoutine=null) makes codegen throw. Resolve
        // them here so codegen — the dumb translator — receives fully-annotated bodies.
        var classCtx = new PostprocessingContext(registry: ctx.Registry,
            variantBodies: ctx.VariantBodies,
            target: ctx.Target,
            buildMode: ctx.BuildMode);
        // Resolve BOTH the monomorphized bodies AND the variant bodies (failable originals + try_/check_/
        // lookup_ variants). A monomorphized variant like `List[S64].try_pick` lives in VariantBodies; its
        // member calls (`n == 0` → `n.eq(...)`) are lowered with LoweringKind set but ResolvedRoutine null and
        // reach codegen unresolved unless classified here. Idempotent — fully-classified calls are skipped.
        var resolver = new Declaration.CallOverloadResolutionPass(ctx: classCtx);
        resolver.RunOnBodiesWithOwners(bodies: ctx.InstantiatedGenericBodies.Values.Select(
            selector: b => (b.Ast.Body, b.Info.OwnerType,
                (IReadOnlyList<ParameterInfo>?)b.Info.Parameters)));
        resolver.RunOnVariantBodies();
    }

    /// <summary>
    /// Adds every REACHED concrete non-generic STDLIB routine body to <c>InstantiatedGenericBodies</c> so
    /// codegen emits it from the one unified body set — no Phase-A stdlib resolution/filter of its own. USER
    /// routines are excluded (codegen emits those directly from their program ASTs). This is the pull move of
    /// codegen's Phase-A stdlib iteration + <c>ResolveStdlibRoutineInfo</c> upstream: the collector already
    /// resolved each reached routine (via the call graph) and holds its lowered body in
    /// <see cref="BuildProgramBodyIndex"/>, so codegen needs to make no resolution decision.
    /// </summary>
    private void MaterializeReachedStdlibBodies(Dictionary<string, Statement> programBodies)
    {
        HashSet<string> userKeys = CollectUserRoutineKeys();
        foreach (string liveKey in ctx.LiveRoutineKeys.Where(predicate: k =>
                     !userKeys.Contains(item: k)))
        {
            TryMaterializeStdlibBody(liveKey: liveKey, programBodies: programBodies);
        }
    }

    /// <summary>
    /// Collects the registry keys of all routine declarations in the user programs, used to distinguish
    /// user-program routines from stdlib routines when materializing reached stdlib bodies.
    /// </summary>
    private HashSet<string> CollectUserRoutineKeys()
    {
        var userKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program p, _, _) in ctx.UserPrograms)
        foreach (ISyntaxTreeNode d in p.Declarations)
        {
            if (d is RoutineDeclaration { ResolvedInfo: { } ri })
            {
                userKeys.Add(item: ri.RegistryKey);
            }
        }

        return userKeys;
    }

    /// <summary>
    /// Materializes one reached stdlib routine body into <c>InstantiatedGenericBodies</c>, applying the
    /// concrete-source-shadow rule: a universal monomorph already holding the key is overwritten by the
    /// hand-written per-width source decl; a GENUINELY-concrete entry is skipped.
    /// </summary>
    private void TryMaterializeStdlibBody(string liveKey,
        Dictionary<string, Statement> programBodies)
    {
        // Skip only when a GENUINELY-concrete body already holds the key; overwrite a universal
        // monomorph (Info.GenericDefinition != null) with the hand-written source decl below.
        if (ctx.InstantiatedGenericBodies.TryGetValue(key: liveKey,
                value: out MonomorphizedBody? existing) && existing.Info.GenericDefinition == null)
        {
            return;
        }

        if (ctx.VariantBodies.ContainsKey(key: liveKey))
        {
            return;
        }

        if (!programBodies.TryGetValue(key: liveKey, value: out Statement? body))
        {
            return;
        }

        RoutineInfo? info = ctx.Registry.LookupRoutine(fullName: liveKey);
        if (info is not { IsGenericDefinition: false })
        {
            return;
        }

        if (info.OwnerType?.IsGenericDefinition == true)
        {
            return;
        }

        ctx.InstantiatedGenericBodies[key: liveKey] = new MonomorphizedBody(
            Ast: WrapInSynthShellDecl(name: info.Name, body: body, info: info),
            Info: info,
            TypeSubs: new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal),
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: false);
    }

    /// <summary>
    /// Materializes, for each SYNTHESIZED generic-def body (represent/diagnose/hash/eq/try_emit/derived
    /// operators — keyed by the generic-def routine key) and each REACHED concrete owner instantiation, the
    /// per-owner rewritten concrete body into <c>InstantiatedGenericBodies</c>. This is the pull-architecture
    /// move of codegen's Phase-C <c>EmitSynthesizedBodyPerConcreteOwner</c> upstream: the concrete synthesized
    /// bodies now PRE-EXIST before codegen instead of codegen rewriting them at emission time (which the demand
    /// collector could not see). Mirrors that emitter's matching + <see cref="GenericAstRewriter"/> rewrite,
    /// but produces a <see cref="MonomorphizedBody"/> instead of IR. Only reached owners
    /// (<c>LiveOwnerTypeNames</c>) are materialized, so it stays demand-scoped.
    /// </summary>
    private void MaterializePerOwnerSynthesizedBodies(
        IReadOnlyDictionary<string, Statement> synthesizedBodies)
    {
        var concreteInstances = ctx.Registry.AllConcreteGenericInstancesUnfiltered.ToList();
        foreach ((string key, Statement synthBody) in synthesizedBodies)
        {
            RoutineInfo? synthInfo = ctx.Registry.LookupRoutine(fullName: key);
            if (synthInfo is not { IsSynthesized: true, IsGenericDefinition: false })
            {
                continue;
            }

            if (synthInfo.OwnerType is not { IsGenericDefinition: true })
            {
                MaterializeConcreteOwnerSynthBody(synthInfo: synthInfo, synthBody: synthBody);
                continue;
            }

            if (synthInfo.WrapperForwarderInnerMemberRoutine != null &&
                synthInfo.OwnerType.GenericParameters is { Count: 1 } wrapperParams)
            {
                MaterializeWrapperForwarderBodies(synthInfo: synthInfo,
                    synthBody: synthBody,
                    wrapperParamName: wrapperParams[index: 0]);
                continue;
            }

            if (synthInfo.OwnerType is not { IsGenericDefinition: true } genericOwner)
            {
                continue;
            }

            if (genericOwner.GenericParameters is not { Count: > 0 } gParams)
            {
                continue;
            }

            MaterializeGenericOwnerSynthBodies(synthInfo: synthInfo,
                synthBody: synthBody,
                genericOwner: genericOwner,
                gParams: gParams,
                concreteInstances: concreteInstances);
        }
    }

    /// <summary>
    /// Materializes a concrete-owner synthesized body (e.g. a user record's <c>represent</c>) directly —
    /// the body is already concrete, no per-owner rewrite is needed. Only materializes when the key is live
    /// (demand walk referenced it) and not yet in <c>InstantiatedGenericBodies</c>.
    /// </summary>
    private void MaterializeConcreteOwnerSynthBody(RoutineInfo synthInfo, Statement synthBody)
    {
        // Only materialize a synth body the demand walk actually REFERENCED (its key is live) —
        // represent/diagnose are force-seeded per reached owner so they qualify, but an uncalled
        // derive (a record's `duplicate`, a flags `all_cases`/`count` nothing invokes) must NOT be
        // built: emitting a dead synth body would drag in its unresolved/absent callees.
        if (!ctx.LiveRoutineKeys.Contains(item: synthInfo.RegistryKey))
        {
            return;
        }

        if (ctx.InstantiatedGenericBodies.ContainsKey(key: synthInfo.RegistryKey))
        {
            return;
        }

        // Store AS CLONED (CloneUniversalDeriveBody already backfilled `me`'s concrete type).
        // Do NOT re-run GenericAstRewriter — an empty-subs rewrite would strip `me`'s ResolvedType,
        // making the later CallOverloadResolutionPass bail on `me.assign()` (receiver type unknown).
        ctx.InstantiatedGenericBodies[key: synthInfo.RegistryKey] = new MonomorphizedBody(
            Ast: WrapInSynthShellDecl(name: synthInfo.Name, body: synthBody, info: synthInfo),
            Info: synthInfo,
            TypeSubs: new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal),
            VariantStatus: null,
            VariantInnerType: null,
            IsSynthesized: true);
        ctx.LiveRoutineKeys.Add(item: synthInfo.RegistryKey);
    }

    /// <summary>
    /// Materializes a generic-owner synthesized body (e.g. <c>List[T].represent</c>) once per reached
    /// concrete owner instantiation, substituting generic parameters with concrete type arguments.
    /// </summary>
    private void MaterializeGenericOwnerSynthBodies(RoutineInfo synthInfo, Statement synthBody,
        TypeInfo genericOwner, List<string> gParams, List<TypeInfo> concreteInstances)
    {
        foreach (TypeInfo candidateOwner in concreteInstances)
        {
            TryMaterializeForCandidate(synthInfo: synthInfo,
                synthBody: synthBody,
                genericOwner: genericOwner,
                gParams: gParams,
                candidateOwner: candidateOwner);
        }
    }

    /// <summary>
    /// Attempts to materialize one concrete-owner specialization of a generic synthesized body
    /// (e.g. <c>List[S32].represent</c>). Skips the candidate if it is generic, has no matching
    /// type arguments, belongs to a different generic definition, has not been reached by the
    /// demand walk, or already has a body in <c>InstantiatedGenericBodies</c>.
    /// </summary>
    private void TryMaterializeForCandidate(RoutineInfo synthInfo, Statement synthBody,
        TypeInfo genericOwner, List<string> gParams, TypeInfo candidateOwner)
    {
        if (candidateOwner.IsGenericDefinition)
        {
            return;
        }

        if (candidateOwner.TypeArguments is not { Count: > 0 } tArgs)
        {
            return;
        }

        if (tArgs.Count != gParams.Count)
        {
            return;
        }

        TypeInfo? candidateGenDef = candidateOwner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
            _ => null
        };
        if (candidateGenDef == null || !ReferenceEquals(objA: candidateGenDef, objB: genericOwner))
        {
            return;
        }

        // Reached owners only — the collector marks these as it walks (demand-scoped).
        if (!ctx.LiveOwnerTypeNames.Contains(item: candidateOwner.FullName))
        {
            return;
        }

        RoutineInfo? concreteMemberRoutine = ctx.Registry.LookupMemberRoutine(
            type: candidateOwner,
            memberRoutineName: synthInfo.Name);
        if (concreteMemberRoutine == null)
        {
            return;
        }

        // Only the REFERENCED members (force-seeded represent/diagnose, or a genuinely-called derive) —
        // not every synth member of a reached owner, or a dead one drags in unresolved callees.
        if (!ctx.LiveRoutineKeys.Contains(item: concreteMemberRoutine.RegistryKey))
        {
            return;
        }

        if (ctx.InstantiatedGenericBodies.ContainsKey(key: concreteMemberRoutine.RegistryKey))
        {
            return;
        }

        var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        for (int gi = 0; gi < gParams.Count; gi++)
        {
            subs[key: gParams[index: gi]] = tArgs[index: gi];
        }

        Statement rewritten = GenericAstRewriter.RewriteStatement(stmt: synthBody,
            subs: subs.ToDictionary(keySelector: kv => kv.Key,
                elementSelector: kv => kv.Value.FullName),
            typeSubs: subs,
            registry: ctx.Registry,
            enclosingRoutine: concreteMemberRoutine);
        ctx.InstantiatedGenericBodies[key: concreteMemberRoutine.RegistryKey] =
            new MonomorphizedBody(
                Ast: WrapInSynthShellDecl(name: concreteMemberRoutine.Name,
                    body: rewritten,
                    info: concreteMemberRoutine),
                Info: concreteMemberRoutine,
                TypeSubs: subs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized: true);
        ctx.LiveRoutineKeys.Add(item: concreteMemberRoutine.RegistryKey);
    }

    /// <summary>
    /// Materializes a synthesized WRAPPER-FORWARDER body (e.g. <c>Retained[T].eq</c>) once per concrete
    /// single-arg wrapper resolution, substituting the wrapper's sole type parameter with each concrete inner
    /// type. Mirrors codegen's retired <c>EmitWrapperForwarderBodyPerConcreteInner</c> but produces a
    /// <see cref="MonomorphizedBody"/>.
    /// </summary>
    private void MaterializeWrapperForwarderBodies(RoutineInfo synthInfo, Statement synthBody,
        string wrapperParamName)
    {
        foreach (RoutineInfo concreteWf in ctx.Registry.GetAllRoutineResolutions())
        {
            if (!concreteWf.IsSynthesized ||
                concreteWf.WrapperForwarderInnerMemberRoutine == null ||
                !ReferenceEquals(objA: concreteWf.GenericDefinition, objB: synthInfo) ||
                concreteWf.OwnerType?.TypeArguments is not { Count: 1 })
            {
                continue;
            }

            // Demand-scope: only the reached wrapper instances whose forwarder is actually referenced.
            if (!ctx.LiveOwnerTypeNames.Contains(item: concreteWf.OwnerType.FullName))
            {
                continue;
            }

            if (!ctx.LiveRoutineKeys.Contains(item: concreteWf.RegistryKey))
            {
                continue;
            }

            if (ctx.InstantiatedGenericBodies.ContainsKey(key: concreteWf.RegistryKey))
            {
                continue;
            }

            TypeInfo concreteInner = concreteWf.OwnerType.TypeArguments[index: 0];
            var wfSubs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal)
            {
                [key: wrapperParamName] = concreteInner
            };
            Statement rewritten = GenericAstRewriter.RewriteStatement(stmt: synthBody,
                subs: wfSubs.ToDictionary(keySelector: kv => kv.Key,
                    elementSelector: kv => kv.Value.FullName),
                typeSubs: wfSubs,
                registry: ctx.Registry,
                enclosingRoutine: concreteWf);
            ctx.InstantiatedGenericBodies[key: concreteWf.RegistryKey] = new MonomorphizedBody(
                Ast: WrapInSynthShellDecl(name: concreteWf.Name,
                    body: rewritten,
                    info: concreteWf),
                Info: concreteWf,
                TypeSubs: wfSubs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized: true);
            ctx.LiveRoutineKeys.Add(item: concreteWf.RegistryKey);
        }
    }

    private static RoutineDeclaration WrapInSynthShellDecl(string name, Statement body,
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

    /// <summary>
    /// The program's entry-point routine bodies (<c>start()</c>, <c>@test</c>, <c>@bench</c>) — the roots of
    /// the demand closure. Returns each as <c>(RegistryKey, decl.Body)</c>: the body is taken DIRECTLY from
    /// the <see cref="RoutineDeclaration"/> (a user <c>start</c> body lives in <c>UserPrograms</c>, not in the
    /// RoutineBodies store), and the key resolves via the module-qualified <see cref="RoutineInfo"/> so a
    /// harness with several <c>start</c>s maps each to its own root. Mirrors
    /// <see cref="RoutineReachabilityPass"/>'s <c>SeedFromEntryPoints</c>.
    /// </summary>
    private List<(string Key, Statement Body)> CollectEntrySeeds()
    {
        var result = new List<(string, Statement)>();
        foreach ((Program program, _, string module) in ctx.UserPrograms)
        {
            foreach (RoutineDeclaration decl in program.Declarations.OfType<RoutineDeclaration>())
            {
                bool isEntry = decl.Name == "start" ||
                               decl.Annotations.Any(predicate: a => a == "test" || a == "bench");
                if (!isEntry)
                {
                    continue;
                }

                RoutineInfo? info = (!string.IsNullOrEmpty(value: module)
                    ? ctx.Registry.LookupRoutine(fullName: $"{module}.{decl.QualifiedName}")
                    : null) ?? ctx.Registry.LookupRoutineByName(name: decl.QualifiedName);
                if (info != null)
                {
                    result.Add(item: (info.RegistryKey, decl.Body));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Indexes every emittable non-generic routine body by RegistryKey, from the SAME source codegen defines
    /// from: the stdlib + user Program ASTs (top-level RoutineDeclarations — stdlib member routines are
    /// flattened to top-level with a <c>Type.method</c> name — plus crashable members), keyed by
    /// <c>decl.ResolvedInfo.RegistryKey</c>. These are the fully-lowered bodies, so walking one (e.g.
    /// <c>Bytes.create(from_list:)</c>) exposes the concrete calls it makes (<c>List[Byte].getitem/count</c>).
    /// </summary>
    private Dictionary<string, Statement> BuildProgramBodyIndex()
    {
        var idx = new Dictionary<string, Statement>(comparer: StringComparer.Ordinal);

        void AddProgram(Program program)
        {
            foreach (ISyntaxTreeNode d in program.Declarations)
            {
                switch (d)
                {
                    case RoutineDeclaration { ResolvedInfo: { } ri, Body: { } body }:
                        idx[key: ri.RegistryKey] = body;
                        break;
                    case CrashableDeclaration crashable:
                        foreach (SyntaxTree.Declaration m in crashable.Members)
                        {
                            if (m is RoutineDeclaration { ResolvedInfo: { } mri, Body: { } mbody })
                            {
                                idx[key: mri.RegistryKey] = mbody;
                            }
                        }

                        break;
                }
            }
        }

        foreach ((Program p, _, _) in ctx.Registry.StdlibPrograms)
        {
            AddProgram(program: p);
        }

        foreach ((Program p, _, _) in ctx.UserPrograms)
        {
            AddProgram(program: p);
        }

        return idx;
    }
}
