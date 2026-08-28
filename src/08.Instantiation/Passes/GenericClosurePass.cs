using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Desugaring.Passes;
using Compiler.Postprocessing.Passes;
using SyntaxTree;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// Phase 7 closure pass: reuse the existing generic monomorphization implementation, but run it
/// behind the explicit instantiation pipeline boundary instead of the old desugaring pipeline.
/// </summary>
internal sealed class GenericClosurePass(InstantiationContext ctx)
{
    public void Run()
    {
        var adapter = new DesugaringContext(registry: ctx.Registry,
            routineBodies: ctx.RoutineBodies,
            target: ctx.Target,
            buildMode: ctx.BuildMode) { SaTiming = ctx.SaTiming };

        // Warm-restore incrementalization: the instantiated bodies already in the context on entry were
        // captured POST-lowering (empty on a cold compile — nothing seeds this map before Phase 8), so the
        // per-body lowering chain below only needs to touch the NEW bodies GMP builds this run. Skipping the
        // (potentially thousands of) already-lowered restored bodies is the bulk of the warm-compile win;
        // an empty pre-existing set makes this a no-op for cold builds.
        var preExistingInstantiationKeys = new HashSet<string>(collection: ctx.InstantiatedGenericBodies.Keys);

        foreach ((string key, Statement body) in ctx.VariantBodies)
        {
            adapter.VariantBodies[key] = body;
        }

        foreach ((string key, MonomorphizedBody body) in ctx.InstantiatedGenericBodies)
        {
            adapter.InstantiatedGenericBodies[key] = body;
        }

        foreach (string key in ctx.LiveRoutineKeys)
        {
            adapter.LiveRoutineKeys.Add(item: key);
        }

        foreach (string typeName in ctx.LiveOwnerTypeNames)
        {
            adapter.LiveOwnerTypeNames.Add(item: typeName);
        }

        // Lower protocol-default-impl routines (e.g. Iterable[Text].join) to per-implementer
        // routines BEFORE GMP, so the synthesized implementer-owned bodies flow through the
        // normal monomorphization machinery.
        var pdil = new ProtocolDefaultImplLoweringPass(ctx: ctx);
        pdil.Run();
        SyncCtxToAdapter(ctx: ctx, adapter: adapter);

        // One GMP instance reused across the fixed point so its processed-type / walked-body sets
        // persist — a re-run only does work for newly-synthesized bodies.
        var gmp = new GenericMonomorphizationPass(ctx: adapter);
        gmp.RunGlobal();

        // PDIL → GMP fixed point. PDIL runs before GMP, so a protocol-default-impl call that appears
        // ONLY inside a GMP-monomorphized body — e.g. `me.source.as_entity().List()` inside
        // `ReverseIterator[T,S].iter`, or `.Set()` inside `IntersectIterator.iter` — was never seen
        // by PDIL and its per-implementer body (`List[S64].List`/`.Set`) was never synthesized,
        // leaving an undefined symbol at link. Re-run PDIL over the now-larger body set; if it
        // synthesizes anything new, fold it in with a cheap incremental GMP pass and repeat. Most
        // (non-iterator) programs converge immediately — the second PDIL pass finds nothing.
        int guard = 0;
        while (guard++ < 16)
        {
            SyncAdapterToCtx(adapter: adapter, ctx: ctx);
            if (!pdil.Run())
                break;
            SyncCtxToAdapter(ctx: ctx, adapter: adapter);
            // The collector bodies PDIL just synthesized (List[S64].List/.Set) are usually
            // self-contained, but a freshly-synthesized body may reference an as-yet-unmonomorphized
            // type — fold those in with a cheap incremental GMP pass (its persisted processed-type /
            // walked-body sets keep the re-run bounded to NEW work).
            gmp.RunIncremental();
        }
        // The NEW bodies GMP produced this run (everything not already lowered on entry). The restored
        // bodies stay in adapter.InstantiatedGenericBodies for LOOKUPS (iterator inlining, etc.) but are
        // excluded from the per-body lowering ITERATION below. Cold: preExisting is empty → freshBodies is
        // the full set (unchanged behavior).
        var freshBodies = preExistingInstantiationKeys.Count == 0
            ? adapter.InstantiatedGenericBodies
            : adapter.InstantiatedGenericBodies
                     .Where(predicate: kv => !preExistingInstantiationKeys.Contains(item: kv.Key))
                     .ToDictionary(keySelector: kv => kv.Key, elementSelector: kv => kv.Value);

        // ControlFlowLowering for instantiated bodies: protocol-default-impl clones (from
        // ProtocolDefaultImplLoweringPass above) carry raw `for` loops from the stdlib AST
        // that never went through Phase 6 desugaring. Lower them before subsequent passes.
        new ControlFlowLoweringPass(ctx: adapter)
            .RunOnInstantiatedGenericBodies(freshBodies);
        // Inline simple iterator `emit!` bodies into their for-loops, replacing the `try_emit`
        // call with the spliced advance. Runs AFTER ControlFlowLowering (which produced the flagged
        // iterator loops) and AFTER monomorphization (so the concrete `emit!` bodies exist in
        // InstantiatedGenericBodies for lookup). Composed/filtering iterators fall back to try_emit.
        new IteratorInlineLoweringPass(registry: ctx.Registry, monoBodies: adapter.InstantiatedGenericBodies)
            .RunOnInstantiatedGenericBodies(freshBodies);
        // Lower the cycle-collector hook intrinsics (`<entity>.roam_trace_ref()` /
        // `.roam_free_ref()`) into explicit routine-VALUE references now that GMP has substituted the
        // generic `RoamController[T]` receiver to a concrete entity. Codegen then materializes the
        // closure from the stamped ResolvedRoutine — it no longer picks the impl via LookupMemberRoutine.
        new RoamHookRefLoweringPass(registry: ctx.Registry)
            .RunOnInstantiatedGenericBodies(freshBodies);
        new GenericCallLoweringPass(ctx: adapter).RunOnInstantiatedGenericBodies();
        new BuilderQueryInliningPass(ctx: adapter).RunOnInstantiatedGenericBodies();
        // Operator lowering for instantiated bodies: GMP's clones inherit unlowered
        // BinaryExpression/UnaryExpression nodes from the generic-def AST (the Phase 8
        // RunGlobal sweep finished before GMP populated the InstantiatedGenericBodies
        // map). Without this, `me.size = me.size + 1_u64` in a monomorphized routine
        // reaches codegen as a raw `BinaryExpression(Add)` and trips the codegen guard.
        var postCtx = new Postprocessing.PostprocessingContext(
            registry: ctx.Registry,
            variantBodies: ctx.VariantBodies,
            target: ctx.Target,
            buildMode: ctx.BuildMode);
        // FStringLoweringPass runs BEFORE OperatorLoweringPass (per the per-file pipeline order);
        // monomorphized represent/diagnose bodies need f-strings lowered to represent/diagnose
        // memberRoutine calls + Text.add before operator lowering can fold the `+` chain.
        new FStringLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        // ExpressionLoweringPass: handles RangeExpression, UnaryExpression(Not), pattern lowering
        // etc. Must run before OperatorLoweringPass — operator lowering folds the BinaryExpressions
        // ExpressionLowering produces (e.g. `1 til n` -> a range record with `+ 1` / `< n` checks).
        // Mirror the per-file PLP → ELP → PLP → ELP cycle: the first PLP folds Maybe/Result/Lookup
        // when-chains (introducing UnaryExpression(Not)); ELP lowers those Not nodes; a second
        // PLP catches the WhenStatements that ELP synthesized for `??` / `?.`; final ELP lowers
        // any Not nodes the second PLP added. Without PLP here, `when subj is None => … else x =>`
        // over `Maybe[Wrapper[T]]` reaches codegen as raw TypePattern/ElsePattern; the codegen
        // TypePattern path falls through to an unconditional match → the first arm always wins.
        new PatternLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        new ExpressionLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        new PatternLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        new ExpressionLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        new OperatorLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        // Copy lowering for instantiated bodies: at generic-def time a field of generic type T looks
        // borrow-tier (no retaining store), so a monomorphized body that returns/stores a value with
        // a now-concrete refcounted field (e.g. DictEntry[Text, S64] from entry_get) never retained
        // it — torn down per use then freed again at container teardown. Re-run here, post-mono, so
        // GetLifecycle sees the concrete field types and injects the balancing store.
        new RecordCopyLoweringPass(ctx: postCtx)
            .RunOnInstantiatedGenericBodies(freshBodies);
        // NOTE: RcRetainLoweringPass deleted. The per-field retain on a record copy lives in the type's
        // own assign/copy derive (post-mono, RecordCopyLoweringPass above routes the copy through it);
        // the pass bumping on top double-counted → teardown double-free. Decrement = scope-exit teardown.

        // The lowering passes REASSIGN dict entries (`dict[key] = body with { ... }`, MonomorphizedBody is
        // a record), so when freshBodies is a separate (warm-restore) dict the lowered results live there,
        // not in the adapter. Merge them back so codegen sees the lowered fresh bodies. No-op on cold
        // (freshBodies IS the adapter map).
        if (!ReferenceEquals(objA: freshBodies, objB: adapter.InstantiatedGenericBodies))
        {
            foreach ((string key, MonomorphizedBody body) in freshBodies)
            {
                adapter.InstantiatedGenericBodies[key] = body;
            }
        }

        // Track-C tripwire (C1): after all instantiated-body lowering, assert every fully-concrete
        // monomorphized body is free of residual generics. This replaces the codegen-time guards —
        // a trip here means the rewriter is incomplete, not that codegen must substitute.
        MonomorphizationCompletenessAssertionPass.Run(adapter.InstantiatedGenericBodies);

        ctx.VariantBodies.Clear();
        foreach ((string key, Statement body) in adapter.VariantBodies)
        {
            ctx.VariantBodies[key] = body;
        }

        ctx.InstantiatedGenericBodies.Clear();
        foreach ((string key, MonomorphizedBody body) in adapter.InstantiatedGenericBodies)
        {
            ctx.InstantiatedGenericBodies[key] = body;
        }

        // Propagate liveness discovered DURING monomorphization back to the shared context.
        // GMP expands LiveRoutineKeys/LiveOwnerTypeNames while emitting bodies for types that
        // only became reachable through the synthesized iterator-adapter chain (which post-dates
        // RoutineReachabilityPass). Codegen reads these sets from the outer InstantiationContext
        // (result.LiveRoutineKeys) to gate Phase-B emission, so without copying the adapter's
        // additions back, the freshly-emitted bodies (e.g. chained-emitter try_emit) would be
        // silently dropped at codegen, leaving undefined symbols at link.
        foreach (string key in adapter.LiveRoutineKeys)
            ctx.LiveRoutineKeys.Add(item: key);
        foreach (string ownerName in adapter.LiveOwnerTypeNames)
            ctx.LiveOwnerTypeNames.Add(item: ownerName);
    }

    /// <summary>Copies the monomorphization-relevant state from the shared context into the GMP adapter.</summary>
    private static void SyncCtxToAdapter(InstantiationContext ctx, DesugaringContext adapter)
    {
        foreach ((string key, MonomorphizedBody body) in ctx.InstantiatedGenericBodies)
            adapter.InstantiatedGenericBodies[key] = body;
        foreach (string key in ctx.LiveRoutineKeys)
            adapter.LiveRoutineKeys.Add(item: key);
        foreach (string ownerName in ctx.LiveOwnerTypeNames)
            adapter.LiveOwnerTypeNames.Add(item: ownerName);
    }

    /// <summary>
    /// Copies GMP output (and the liveness it expanded) from the adapter back into the shared context,
    /// so the next ProtocolDefaultImplLoweringPass pass walks the freshly-monomorphized bodies.
    /// </summary>
    private static void SyncAdapterToCtx(DesugaringContext adapter, InstantiationContext ctx)
    {
        foreach ((string key, MonomorphizedBody body) in adapter.InstantiatedGenericBodies)
            ctx.InstantiatedGenericBodies[key] = body;
        foreach (string key in adapter.LiveRoutineKeys)
            ctx.LiveRoutineKeys.Add(item: key);
        foreach (string ownerName in adapter.LiveOwnerTypeNames)
            ctx.LiveOwnerTypeNames.Add(item: ownerName);
    }
}
