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
    public void RunCollect()
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
