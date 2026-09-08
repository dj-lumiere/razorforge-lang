using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Diagnostics;
using Compiler.Instantiation;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation.Passes;

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
/// <para>SHADOW MODE (current): <see cref="RunShadow"/> runs AFTER the existing pipeline and drives the
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
        var adapter = new DesugaringContext(registry: ctx.Registry, routineBodies: ctx.RoutineBodies,
            target: ctx.Target, buildMode: ctx.BuildMode)
        {
            SaTiming = ctx.SaTiming,
            VariantBodies = ctx.VariantBodies,
            InstantiatedGenericBodies = ctx.InstantiatedGenericBodies,
            LiveRoutineKeys = ctx.LiveRoutineKeys,
            LiveOwnerTypeNames = ctx.LiveOwnerTypeNames,
            SynthesizeAllDerives = ctx.SeedAllStdlibRoutines,
        };

        List<(string Key, Statement Body)> entrySeeds = CollectEntrySeeds();
        Dictionary<string, Statement> programBodies = BuildProgramBodyIndex();
        var allFresh = new List<string>();
        int totalBuilt = 0;

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
        var incrementalGmp = new GenericMonomorphizationPass(ctx: adapter);
        int guard = 0;
        while (guard++ < 100)
        {
            var before = new HashSet<string>(collection: adapter.InstantiatedGenericBodies.Keys,
                comparer: StringComparer.Ordinal);
            int built = new GenericMonomorphizationPass(ctx: adapter)
                .CollectReferencedInIsolation(entrySeeds: entrySeeds, programBodies: programBodies);
            // Synthesize + monomorphize protocol-default-impls referenced by the bodies built this round,
            // BEFORE lowering, so their fresh bodies join the same lower-all-fresh sweep below.
            bool pdilSynth = pdil.Run();
            if (pdilSynth) incrementalGmp.RunIncremental();
            Dictionary<string, MonomorphizedBody> freshBodies = adapter.InstantiatedGenericBodies
                .Where(predicate: kv => !before.Contains(item: kv.Key))
                .ToDictionary(keySelector: kv => kv.Key, elementSelector: kv => kv.Value,
                    comparer: StringComparer.Ordinal);
            if (freshBodies.Count > 0)
            {
                GenericClosurePass.LowerFreshBodies(ctx: ctx, adapter: adapter, freshBodies: freshBodies);
                // LowerFreshBodies REASSIGNS entries (`dict[key] = body with { … }`, MonomorphizedBody is a
                // record) on the `freshBodies` COPY, not the shared adapter map — so the lowered results
                // (FString/Operator/VariantReturn/…) live only in the copy. Merge them back or codegen reads
                // the UN-lowered originals (a composed iterator's try_emit reaching codegen with raw
                // VariantReturnStatement). Mirrors GenericClosurePass.RunClosure's merge-back.
                foreach ((string key, MonomorphizedBody body) in freshBodies)
                    adapter.InstantiatedGenericBodies[key: key] = body;
            }
            totalBuilt += built;
            allFresh.AddRange(collection: freshBodies.Keys);
            if (built == 0 && !pdilSynth) break;
        }

        if (totalBuilt > 0)
            Console.Error.WriteLine(value: $"[COLLECT] demand-built {totalBuilt} over-pruned routine(s): " +
                string.Join(separator: ", ",
                    values: allFresh.OrderBy(keySelector: k => k, comparer: StringComparer.Ordinal)));

        if (synthesizedBodies != null)
            MaterializePerOwnerSynthesizedBodies(synthesizedBodies: synthesizedBodies);
        MaterializeReachedStdlibBodies(programBodies: programBodies);

        // Classify (resolve call overloads / set LoweringKind) across EVERY collector-built body. With eager
        // monomorphization retired, InstantiatedGenericBodies is populated ENTIRELY by the collector (demand
        // fixpoint + materialization above), none of which passed through SemanticVerifier's Phase-8
        // CallOverloadResolutionPass (that ran on the then-empty set). A body with an unresolved call (e.g.
        // `me.assign()` in a record's `duplicate` derive, ResolvedRoutine=null) makes codegen throw. Resolve
        // them here so codegen — the dumb translator — receives fully-annotated bodies.
        var classCtx = new Postprocessing.PostprocessingContext(registry: ctx.Registry,
            variantBodies: ctx.VariantBodies, target: ctx.Target, buildMode: ctx.BuildMode);
        // Resolve BOTH the monomorphized bodies AND the variant bodies (failable originals + try_/check_/
        // lookup_ variants). A monomorphized variant like `List[S64].try_pick` lives in VariantBodies; its
        // member calls (`n == 0` → `n.eq(...)`) are lowered with LoweringKind set but ResolvedRoutine null and
        // reach codegen unresolved unless classified here. Idempotent — fully-classified calls are skipped.
        var resolver = new Postprocessing.Passes.CallOverloadResolutionPass(ctx: classCtx);
        resolver.RunOnStatements(
            statements: ctx.InstantiatedGenericBodies.Values.Select(selector: b => b.Ast.Body));
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
        var userKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program p, _, _) in ctx.UserPrograms)
            foreach (ISyntaxTreeNode d in p.Declarations)
                if (d is RoutineDeclaration { ResolvedInfo: { } ri }) userKeys.Add(item: ri.RegistryKey);

        foreach (string liveKey in ctx.LiveRoutineKeys.ToList())
        {
            if (userKeys.Contains(item: liveKey)) continue;
            // A concrete per-width SOURCE decl (e.g. the hand-written `common routine U64.from_digit_bytes_at`)
            // must SHADOW a universal-common monomorph that collided on the same key: `common routine
            // Integer.X` is a universal template, and force-seeding it per reached width instantiates it for
            // U64 too — producing a body (with `-acc` → `.neg`) under the SAME key as the hand-written U64
            // override. If that universal monomorph (Info.GenericDefinition != null) got built first it would
            // win and its `.neg` (unresolved for the substituted width) crashes codegen. So skip only when a
            // GENUINELY-concrete body already holds the key; overwrite a universal monomorph with the source
            // override below.
            if (ctx.InstantiatedGenericBodies.TryGetValue(key: liveKey,
                    value: out MonomorphizedBody? existing)
                && existing.Info.GenericDefinition == null) continue;
            if (ctx.VariantBodies.ContainsKey(key: liveKey)) continue;
            if (!programBodies.TryGetValue(key: liveKey, value: out Statement? body)) continue;
            RoutineInfo? info = ctx.Registry.LookupRoutine(fullName: liveKey);
            if (info is not { IsGenericDefinition: false }) continue;
            if (info.OwnerType?.IsGenericDefinition == true) continue;
            ctx.InstantiatedGenericBodies[key: liveKey] = new MonomorphizedBody(
                Ast: WrapInSynthShellDecl(name: info.Name, body: body, info: info),
                Info: info,
                TypeSubs: new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal),
                VariantStatus: null, VariantInnerType: null, IsSynthesized: false);
        }
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
    private void MaterializePerOwnerSynthesizedBodies(IReadOnlyDictionary<string, Statement> synthesizedBodies)
    {
        List<TypeInfo> concreteInstances = ctx.Registry.AllConcreteGenericInstancesUnfiltered.ToList();
        foreach ((string key, Statement synthBody) in synthesizedBodies)
        {
            RoutineInfo? synthInfo = ctx.Registry.LookupRoutine(fullName: key);
            if (synthInfo is not { IsSynthesized: true, IsGenericDefinition: false }) continue;

            // CONCRETE-owner synthesized body (a user record's `represent`, a concrete type's derived `ne`) —
            // the body is already concrete, no per-owner rewrite. Materialize it directly when reached (its
            // key is live, or it has no owner / a reached owner). Codegen used to emit these in Phase C.
            if (synthInfo.OwnerType is not { IsGenericDefinition: true })
            {
                // Only materialize a synth body the demand walk actually REFERENCED (its key is live) —
                // represent/diagnose are force-seeded per reached owner so they qualify, but an uncalled
                // derive (a record's `duplicate`, a flags `all_cases`/`count` nothing invokes) must NOT be
                // built: emitting a dead synth body would drag in its unresolved/absent callees.
                if (!ctx.LiveRoutineKeys.Contains(item: synthInfo.RegistryKey)) continue;
                if (ctx.InstantiatedGenericBodies.ContainsKey(key: synthInfo.RegistryKey)) continue;
                // Store the body AS CLONED (CloneUniversalDeriveBody already backfilled `me`'s concrete type).
                // Do NOT re-run GenericAstRewriter here — an empty-subs rewrite would strip `me`'s ResolvedType,
                // making the later CallOverloadResolutionPass bail on `me.assign()` (receiver type unknown).
                ctx.InstantiatedGenericBodies[key: synthInfo.RegistryKey] = new MonomorphizedBody(
                    Ast: WrapInSynthShellDecl(name: synthInfo.Name, body: synthBody, info: synthInfo),
                    Info: synthInfo,
                    TypeSubs: new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal),
                    VariantStatus: null, VariantInnerType: null, IsSynthesized: true);
                ctx.LiveRoutineKeys.Add(item: synthInfo.RegistryKey);
                continue;
            }

            // Wrapper-forwarder bodies (e.g. Retained[T].eq) are anchored on the generic-def owner and
            // rewritten per concrete inner type — a separate shape from the per-owner path below.
            if (synthInfo.WrapperForwarderInnerMemberRoutine != null
                && synthInfo.OwnerType.GenericParameters is { Count: 1 } wrapperParams)
            {
                MaterializeWrapperForwarderBodies(synthInfo: synthInfo, synthBody: synthBody,
                    wrapperParamName: wrapperParams[0]);
                continue;
            }

            if (synthInfo.OwnerType is not { IsGenericDefinition: true } genericOwner) continue;
            if (genericOwner.GenericParameters is not { Count: > 0 } gParams) continue;

            foreach (TypeInfo candidateOwner in concreteInstances)
            {
                if (candidateOwner.IsGenericDefinition) continue;
                if (candidateOwner.TypeArguments is not { Count: > 0 } tArgs) continue;
                if (tArgs.Count != gParams.Count) continue;
                TypeInfo? candidateGenDef = candidateOwner switch
                {
                    RecordTypeInfo r => r.GenericDefinition,
                    EntityTypeInfo e => e.GenericDefinition,
                    WrapperTypeInfo w => ctx.Registry.LookupType(name: w.Name),
                    _ => null
                };
                if (candidateGenDef == null || !ReferenceEquals(objA: candidateGenDef, objB: genericOwner))
                    continue;
                // Reached owners only — the collector marks these as it walks (demand-scoped).
                if (!ctx.LiveOwnerTypeNames.Contains(item: candidateOwner.FullName)) continue;
                RoutineInfo? concreteMemberRoutine = ctx.Registry.LookupMemberRoutine(
                    type: candidateOwner, memberRoutineName: synthInfo.Name);
                if (concreteMemberRoutine == null) continue;
                // Only the REFERENCED members (force-seeded represent/diagnose, or a genuinely-called derive) —
                // not every synth member of a reached owner, or a dead one drags in unresolved callees.
                if (!ctx.LiveRoutineKeys.Contains(item: concreteMemberRoutine.RegistryKey)) continue;
                if (ctx.InstantiatedGenericBodies.ContainsKey(key: concreteMemberRoutine.RegistryKey))
                    continue;

                var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
                for (int gi = 0; gi < gParams.Count; gi++) subs[key: gParams[index: gi]] = tArgs[index: gi];
                Statement rewritten = GenericAstRewriter.RewriteStatement(
                    stmt: synthBody,
                    subs: subs.ToDictionary(keySelector: kv => kv.Key, elementSelector: kv => kv.Value.FullName),
                    typeSubs: subs,
                    registry: ctx.Registry,
                    enclosingRoutine: concreteMemberRoutine);
                ctx.InstantiatedGenericBodies[key: concreteMemberRoutine.RegistryKey] = new MonomorphizedBody(
                    Ast: WrapInSynthShellDecl(name: concreteMemberRoutine.Name, body: rewritten,
                        info: concreteMemberRoutine),
                    Info: concreteMemberRoutine,
                    TypeSubs: subs,
                    VariantStatus: null,
                    VariantInnerType: null,
                    IsSynthesized: true);
                ctx.LiveRoutineKeys.Add(item: concreteMemberRoutine.RegistryKey);
            }
        }
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
            if (!concreteWf.IsSynthesized
                || concreteWf.WrapperForwarderInnerMemberRoutine == null
                || !ReferenceEquals(objA: concreteWf.GenericDefinition, objB: synthInfo)
                || concreteWf.OwnerType?.TypeArguments is not { Count: 1 })
                continue;
            // Demand-scope: only the reached wrapper instances whose forwarder is actually referenced.
            if (!ctx.LiveOwnerTypeNames.Contains(item: concreteWf.OwnerType.FullName)) continue;
            if (!ctx.LiveRoutineKeys.Contains(item: concreteWf.RegistryKey)) continue;
            if (ctx.InstantiatedGenericBodies.ContainsKey(key: concreteWf.RegistryKey)) continue;
            TypeInfo concreteInner = concreteWf.OwnerType!.TypeArguments![0];
            var wfSubs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal)
                { [wrapperParamName] = concreteInner };
            Statement rewritten = GenericAstRewriter.RewriteStatement(
                stmt: synthBody,
                subs: wfSubs.ToDictionary(keySelector: kv => kv.Key, elementSelector: kv => kv.Value.FullName),
                typeSubs: wfSubs,
                registry: ctx.Registry,
                enclosingRoutine: concreteWf);
            ctx.InstantiatedGenericBodies[key: concreteWf.RegistryKey] = new MonomorphizedBody(
                Ast: WrapInSynthShellDecl(name: concreteWf.Name, body: rewritten, info: concreteWf),
                Info: concreteWf,
                TypeSubs: wfSubs,
                VariantStatus: null,
                VariantInnerType: null,
                IsSynthesized: true);
            ctx.LiveRoutineKeys.Add(item: concreteWf.RegistryKey);
        }
    }

    private static RoutineDeclaration WrapInSynthShellDecl(string name, Statement body, RoutineInfo info)
        => new(Name: name, Parameters: [], ReturnType: null, Body: body,
            Visibility: VisibilityModifier.Open, Annotations: [],
            Location: info.Location ?? new SourceLocation(FileName: "", Line: 0, Column: 0, Position: 0));

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
                if (!isEntry) continue;
                RoutineInfo? info = (!string.IsNullOrEmpty(value: module)
                                        ? ctx.Registry.LookupRoutine(fullName: $"{module}.{decl.QualifiedName}")
                                        : null)
                                    ?? ctx.Registry.LookupRoutineByName(name: decl.QualifiedName);
                if (info != null) result.Add(item: (info.RegistryKey, decl.Body));
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
            foreach (var d in program.Declarations)
            {
                switch (d)
                {
                    case RoutineDeclaration { ResolvedInfo: { } ri, Body: { } body }:
                        idx[ri.RegistryKey] = body;
                        break;
                    case CrashableDeclaration crashable:
                        foreach (var m in crashable.Members)
                            if (m is RoutineDeclaration { ResolvedInfo: { } mri, Body: { } mbody })
                                idx[mri.RegistryKey] = mbody;
                        break;
                }
            }
        }
        foreach ((Program p, _, _) in ctx.Registry.StdlibPrograms) AddProgram(program: p);
        foreach ((Program p, _, _) in ctx.UserPrograms) AddProgram(program: p);
        return idx;
    }
}
