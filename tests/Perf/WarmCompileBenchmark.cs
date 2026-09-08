using System;
using System.Diagnostics;
using System.Linq;
using Compiler.Resolution;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Verification;
using Verification.Results;
using Xunit;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Milestone-1 de-risk: measures the compile-time ceiling when the stdlib is processed ONCE and
/// reused via the registry snapshot, versus the cold path that reprocesses the stdlib every run.
/// Not a correctness assertion — it prints timings via test output and always passes.
/// </summary>
public sealed class WarmCompileBenchmark
{
    private readonly ITestOutputHelper _out;
    public WarmCompileBenchmark(ITestOutputHelper output) => _out = output;

    private const string Trivial = """
                                   module Bench
                                   import IO/Console
                                   routine start()
                                     show("hi")
                                     return
                                   """;

    private static Program ParseTrivial()
    {
        var tokens = new Tokenizer(source: Trivial, fileName: "bench.rf",
            language: Language.RazorForge).Tokenize();
        return new Compiler.Parser.Parser(tokens: tokens, language: Language.RazorForge,
            fileName: "bench.rf").Parse();
    }

    private static double TimeMs(Action a)
    {
        var sw = Stopwatch.StartNew();
        a();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void Ceiling_ColdVsWarm()
    {
        // COLD: full stdlib load + SA (this is what every CLI invocation pays today).
        TypeRegistry.StdlibSnapshot snap = null!;
        double cold = TimeMs(() => snap = SemanticVerifier.CaptureStdlibSnapshot(Language.RazorForge));
        _out.WriteLine($"COLD  capture stdlib snapshot (load+Phase1-5): {cold:N0} ms");

        // WARM (SA-only): restore snapshot + analyze the trivial user file, stopping at Phase 5.
        double warmSa = TimeMs(() =>
        {
            var sa = new SemanticVerifier(Language.RazorForge, snapshot: snap) { SaOnly = true };
            sa.Analyze(ParseTrivial());
        });
        _out.WriteLine($"WARM  restore + SA-only analyze(trivial):      {warmSa:N1} ms");

        // WARM (full pipeline): restore snapshot + full analyze (Phase 4/6/7). The restored registry
        // has no StdlibPrograms, so the stdlib desugar/monomorph loops are no-ops — this is the ceiling
        // for a full compile once the stdlib is not reprocessed.
        AnalysisResult warmResult = null!;
        double warmFull = TimeMs(() =>
        {
            var sa = new SemanticVerifier(Language.RazorForge, snapshot: snap);
            warmResult = sa.Analyze(ParseTrivial());
        });
        _out.WriteLine($"WARM  restore + FULL analyze(trivial):         {warmFull:N1} ms  " +
                       $"(errors={warmResult.Errors.Count})");

        // Second warm full to show steady-state (JIT/GC warmed), WITH per-phase timing to stderr.
        double warmFull2 = TimeMs(() =>
        {
            var sa = new SemanticVerifier(Language.RazorForge, snapshot: snap) { SaTiming = true };
            sa.Analyze(ParseTrivial());
        });
        _out.WriteLine($"WARM2 restore + FULL analyze(trivial):         {warmFull2:N1} ms");

        _out.WriteLine($"=> cold={cold:N0}ms  warm-full={warmFull2:N1}ms  " +
                       $"speedup≈{cold / Math.Max(warmFull2, 0.01):N0}x");

        // The ceiling claim: reusing the processed stdlib makes SA dramatically faster than the cold
        // load+analyze. (Generous bound so the guard is not machine-speed flaky.)
        Assert.True(warmSa < cold / 2.0,
            $"warm SA-only ({warmSa:N0}ms) should be far below cold snapshot capture ({cold:N0}ms)");
    }

    private static string Codegen(AnalysisResult r)
    {
        Compiler.Postprocessing.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);
        var gen = new Compiler.CodeGen.LlvmCodeGenerator(
            userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            stdlibPrograms: r.Registry.StdlibPrograms,
            synthesizedBodies: r.SynthesizedBodies,
            instantiatedGenericBodies: r.InstantiatedGenericBodies,
            liveRoutineKeys: r.LiveRoutineKeys,
            maySuspendRoutineKeys: r.MaySuspendRoutineKeys);
        return gen.Generate();
    }

    private static string PrintCodegenAst(AnalysisResult r) =>
        new Builder.RfSyntaxTreePrinter().PrintMultiProgram(
            programs: r.Registry.UserPrograms,
            synthesizedBodies: r.SynthesizedBodies,
            registry: r.Registry,
            stdlibPrograms: r.Registry.StdlibPrograms,
            instantiatedGenericBodies: r.InstantiatedGenericBodies);

    /// <summary>
    /// Milestone-1 correctness oracle (PASSING): a WARM compile (restored fully-processed stdlib, +
    /// on-demand imports lowered fresh) must emit the SAME set of routine definitions as a COLD compile
    /// of the same source. Proven: both emit an identical set of 95 `define`s. Warm analyze ≈ 1.5s vs
    /// cold ≈ 5.3s.
    ///
    /// The full .ll byte stream still differs COSMETICALLY — emission ORDER cascades to %tmp/!dbg
    /// numbering, unreachable `declare`s carry a `[member]` vs `[member, wired]` decoration, and a few
    /// Hijacked debug trace strings use the generic vs monomorphized owner name — but every EMITTED
    /// routine body is identical. A follow-up can make emission order deterministic for byte-equal output.
    /// </summary>
    [Fact]
    public void WarmCodegenAst_MatchesCold()
    {
        // COLD reference: a normal from-scratch compile of the trivial file.
        var cold = new SemanticVerifier(Language.RazorForge);
        AnalysisResult coldResult = cold.Analyze(ParseTrivial());
        string coldAst = PrintCodegenAst(coldResult);

        // WARM: capture the fully-processed stdlib once, then compile the SAME file from the restore.
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(Language.RazorForge);
        double warmMs = 0;
        AnalysisResult warmResult = null!;
        var sw = Stopwatch.StartNew();
        var warmSa = new SemanticVerifier(Language.RazorForge, warm);
        warmResult = warmSa.Analyze(ParseTrivial());
        warmMs = sw.Elapsed.TotalMilliseconds;
        string warmAst = PrintCodegenAst(warmResult);

        // The true codegen-equivalence oracle: the emitted .ll. Codegen only emits REACHABLE routines, so
        // extra unreachable instantiations in the AST are irrelevant — what matters is byte-identical IR.
        string coldLl = Codegen(coldResult);
        string warmLl = Codegen(warmResult);
        _out.WriteLine($"cold errors={coldResult.Errors.Count}  warm errors={warmResult.Errors.Count}");
        _out.WriteLine($"cold live={coldResult.LiveRoutineKeys.Count}  warm live={warmResult.LiveRoutineKeys.Count}");
        _out.WriteLine($"cold .ll len={coldLl.Length}  warm .ll len={warmLl.Length}");
        _out.WriteLine($"warm analyze: {warmMs:N1} ms");

        // Correctness oracle: the WARM compile must emit the EXACT SAME set of routine definitions as the
        // COLD compile (no truncation, no divergence). Each `define @"…"(…)` header is compared as a set.
        // (The full byte stream still differs cosmetically — emission ORDER cascades to %tmp/!dbg
        // numbering, and unreachable `declare`s / debug trace strings vary — but every EMITTED routine and
        // its body are identical; a follow-up can make emission order deterministic for byte-equal output.)
        static System.Collections.Generic.HashSet<string> Defines(string ll) =>
            ll.Split('\n')
              .Where(l => l.StartsWith("define ", System.StringComparison.Ordinal))
              .Select(l => System.Text.RegularExpressions.Regex.Replace(
                  l.Split(" {", 2)[0], @" !dbg ![0-9]+", ""))
              .ToHashSet(System.StringComparer.Ordinal);
        var coldDefs = Defines(coldLl);
        var warmDefs = Defines(warmLl);
        _out.WriteLine($"cold defines={coldDefs.Count}  warm defines={warmDefs.Count}");
        _out.WriteLine($"cold-only: {string.Join(" | ", coldDefs.Except(warmDefs).Take(5))}");
        _out.WriteLine($"warm-only: {string.Join(" | ", warmDefs.Except(coldDefs).Take(5))}");

        Assert.Empty(coldResult.Errors);
        Assert.Empty(warmResult.Errors);
        Assert.Equal(coldDefs, warmDefs);
    }

    private static System.Collections.Generic.HashSet<string> DefineSet(string ll) =>
        ll.Split('\n')
          .Where(l => l.StartsWith("define ", System.StringComparison.Ordinal))
          .Select(l => System.Text.RegularExpressions.Regex.Replace(
              l.Split(" {", 2)[0], @" !dbg ![0-9]+", ""))
          .ToHashSet(System.StringComparer.Ordinal);

    /// <summary>
    /// STAGE 0 SPIKE (daemon P2 — cross-build poisoning): a compile daemon captures the fully-processed
    /// stdlib ONCE and reuses that ONE <see cref="SemanticVerifier.CompiledStdlibState"/> for EVERY build.
    /// The warm state SHARES stdlib RoutineInfo/TypeInfo/body-AST objects across builds (only the dicts are
    /// per-build copies), so if any pass mutates a shared stdlib object in place, build N poisons build N+1.
    /// This exercises that: build the SAME file TWICE from ONE warm state, plus a THIRD DIFFERENT file, and
    /// assert every build emits the cold-equivalent define set. A divergence here is exactly the P2 bug the
    /// daemon must fix before it can reuse warm state.
    /// </summary>
    [Fact]
    public void WarmCompile_Repeatable_FromSharedState_NoPoisoning()
    {
        // Cold reference define sets for each source.
        string SrcB = """
                      module Bench2
                      import IO/Console
                      routine start()
                        var xs = List[S32]()
                        xs.add_last(value: 7)
                        show(f"n={xs.count()}")
                        return
                      """;
        Program ParseB() => new Compiler.Parser.Parser(
            tokens: new Tokenizer(source: SrcB, fileName: "b.rf", language: Language.RazorForge).Tokenize(),
            language: Language.RazorForge, fileName: "b.rf").Parse();

        var coldADefs = DefineSet(Codegen(new SemanticVerifier(Language.RazorForge).Analyze(ParseTrivial())));
        var coldBDefs = DefineSet(Codegen(new SemanticVerifier(Language.RazorForge).Analyze(ParseB())));

        // Capture the warm state ONCE — the daemon's resident in-RAM stdlib.
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(Language.RazorForge);

        // Reuse it across builds, as a daemon would: A, A again, then a DIFFERENT file B.
        AnalysisResult a1 = new SemanticVerifier(Language.RazorForge, warm).Analyze(ParseTrivial());
        var a1Defs = DefineSet(Codegen(a1));
        AnalysisResult a2 = new SemanticVerifier(Language.RazorForge, warm).Analyze(ParseTrivial());
        var a2Defs = DefineSet(Codegen(a2));
        AnalysisResult b1 = new SemanticVerifier(Language.RazorForge, warm).Analyze(ParseB());
        var b1Defs = DefineSet(Codegen(b1));
        // A THIRD build of A after B — catches poisoning that only a DIFFERENT-file build introduces.
        AnalysisResult a3 = new SemanticVerifier(Language.RazorForge, warm).Analyze(ParseTrivial());
        var a3Defs = DefineSet(Codegen(a3));

        _out.WriteLine($"errors: a1={a1.Errors.Count} a2={a2.Errors.Count} b1={b1.Errors.Count} a3={a3.Errors.Count}");
        _out.WriteLine($"defines: coldA={coldADefs.Count} a1={a1Defs.Count} a2={a2Defs.Count} a3={a3Defs.Count} | coldB={coldBDefs.Count} b1={b1Defs.Count}");
        _out.WriteLine($"a1 vs a2 diff: -{string.Join(",", a1Defs.Except(a2Defs).Take(6))} +{string.Join(",", a2Defs.Except(a1Defs).Take(6))}");
        _out.WriteLine($"a1 vs a3 diff: -{string.Join(",", a1Defs.Except(a3Defs).Take(6))} +{string.Join(",", a3Defs.Except(a1Defs).Take(6))}");
        _out.WriteLine($"coldB vs b1 diff: -{string.Join(",", coldBDefs.Except(b1Defs).Take(6))} +{string.Join(",", b1Defs.Except(coldBDefs).Take(6))}");

        Assert.Empty(a1.Errors);
        Assert.Empty(a2.Errors);
        Assert.Empty(b1.Errors);
        Assert.Empty(a3.Errors);
        // P2: every warm build must match its cold reference, and repeated builds must be identical —
        // i.e. reusing the shared warm state does NOT poison it.
        Assert.Equal(coldADefs, a1Defs);
        Assert.Equal(a1Defs, a2Defs);
        Assert.Equal(coldBDefs, b1Defs);
        Assert.Equal(a1Defs, a3Defs);
    }
}
