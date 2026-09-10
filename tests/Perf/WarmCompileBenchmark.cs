using System.Diagnostics;
using System.Text.RegularExpressions;
using Compiler.Declaration;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Compiler.Verification;
using Compiler.Verification.Results;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Milestone-1 de-risk: measures the compile-time ceiling when the stdlib is processed ONCE and
/// reused via the registry snapshot, versus the cold path that reprocesses the stdlib every run.
/// Not a correctness assertion — it prints timings via test output and always passes.
/// </summary>
public sealed partial class WarmCompileBenchmark
{
    [GeneratedRegex(pattern: @" !dbg ![0-9]+")]
    private static partial Regex DebugMetaRefPattern();

    private readonly ITestOutputHelper _out;
    public WarmCompileBenchmark(ITestOutputHelper output)
    {
        _out = output;
    }

    private const string Trivial = """
                                   module Bench
                                   import IO/Console
                                   routine start()
                                     show("hi")
                                     return
                                   """;

    private static Program ParseTrivial()
    {
        List<Token> tokens =
            new Tokenizer(source: Trivial, fileName: "bench.rf", language: Language.RazorForge)
               .Tokenize();
        return new Compiler.Parser.Parser(tokens: tokens,
            language: Language.RazorForge,
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
        double cold = TimeMs(a: () =>
            snap = SemanticVerifier.CaptureStdlibSnapshot(language: Language.RazorForge));
        _out.WriteLine(message: $"COLD  capture stdlib snapshot (load+Phase1-5): {cold:N0} ms");

        // WARM (SA-only): restore snapshot + analyze the trivial user file, stopping at Phase 5.
        double warmSa = TimeMs(a: () =>
        {
            var sa = new SemanticVerifier(language: Language.RazorForge, snapshot: snap)
            {
                SaOnly = true
            };
            sa.Analyze(program: ParseTrivial());
        });
        _out.WriteLine(message: $"WARM  restore + SA-only analyze(trivial):      {warmSa:N1} ms");

        // WARM (full pipeline): restore snapshot + full analyze (Phase 4/6/7). The restored registry
        // has no StdlibPrograms, so the stdlib desugar/monomorph loops are no-ops — this is the ceiling
        // for a full compile once the stdlib is not reprocessed.
        AnalysisResult warmResult = null!;
        double warmFull = TimeMs(a: () =>
        {
            var sa = new SemanticVerifier(language: Language.RazorForge, snapshot: snap);
            warmResult = sa.Analyze(program: ParseTrivial());
        });
        _out.WriteLine(
            message: $"WARM  restore + FULL analyze(trivial):         {warmFull:N1} ms  " +
                     $"(errors={warmResult.Errors.Count})");

        // Second warm full to show steady-state (JIT/GC warmed), WITH per-phase timing to stderr.
        double warmFull2 = TimeMs(a: () =>
        {
            var sa = new SemanticVerifier(language: Language.RazorForge, snapshot: snap)
            {
                SaTiming = true
            };
            sa.Analyze(program: ParseTrivial());
        });
        _out.WriteLine(
            message: $"WARM2 restore + FULL analyze(trivial):         {warmFull2:N1} ms");

        _out.WriteLine(message: $"=> cold={cold:N0}ms  warm-full={warmFull2:N1}ms  " +
                                $"speedup≈{cold / Math.Max(val1: warmFull2, val2: 0.01):N0}x");

        // The ceiling claim: reusing the processed stdlib makes SA dramatically faster than the cold
        // load+analyze. (Generous bound so the guard is not machine-speed flaky.)
        Assert.True(condition: warmSa < cold / 2.0,
            userMessage:
            $"warm SA-only ({warmSa:N0}ms) should be far below cold snapshot capture ({cold:N0}ms)");
    }

    private static string Codegen(AnalysisResult r)
    {
        Compiler.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);
        var gen = new Compiler.LlvmEmit.LlvmEmitter(userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new Compiler.LlvmEmit.LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies,
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            });
        return gen.Generate();
    }

    private static string PrintCodegenAst(AnalysisResult r)
    {
        return new Builder.RfSyntaxTreePrinter().PrintMultiProgram(
            programs: r.Registry.UserPrograms,
            synthesizedBodies: r.SynthesizedBodies,
            registry: r.Registry,
            stdlibPrograms: r.Registry.StdlibPrograms,
            instantiatedGenericBodies: r.InstantiatedGenericBodies);
    }

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
        var cold = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult coldResult = cold.Analyze(program: ParseTrivial());
        string coldAst = PrintCodegenAst(r: coldResult);

        // WARM: capture the fully-processed stdlib once, then compile the SAME file from the restore.
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);
        double warmMs = 0;
        AnalysisResult warmResult = null!;
        var sw = Stopwatch.StartNew();
        var warmSa = new SemanticVerifier(language: Language.RazorForge, warm: warm);
        warmResult = warmSa.Analyze(program: ParseTrivial());
        warmMs = sw.Elapsed.TotalMilliseconds;
        string warmAst = PrintCodegenAst(r: warmResult);

        // The true codegen-equivalence oracle: the emitted .ll. Codegen only emits REACHABLE routines, so
        // extra unreachable instantiations in the AST are irrelevant — what matters is byte-identical IR.
        string coldLl = Codegen(r: coldResult);
        string warmLl = Codegen(r: warmResult);
        _out.WriteLine(
            message:
            $"cold errors={coldResult.Errors.Count}  warm errors={warmResult.Errors.Count}");
        _out.WriteLine(
            message:
            $"cold live={coldResult.LiveRoutineKeys.Count}  warm live={warmResult.LiveRoutineKeys.Count}");
        _out.WriteLine(message: $"cold .ll len={coldLl.Length}  warm .ll len={warmLl.Length}");
        _out.WriteLine(message: $"warm analyze: {warmMs:N1} ms");

        // Correctness oracle: the WARM compile must emit the EXACT SAME set of routine definitions as the
        // COLD compile (no truncation, no divergence). Each `define @"…"(…)` header is compared as a set.
        // (The full byte stream still differs cosmetically — emission ORDER cascades to %tmp/!dbg
        // numbering, and unreachable `declare`s / debug trace strings vary — but every EMITTED routine and
        // its body are identical; a follow-up can make emission order deterministic for byte-equal output.)
        static HashSet<string> Defines(string ll)
        {
            return ll.Split(separator: '\n')
                     .Where(predicate: l =>
                          l.StartsWith(value: "define ", comparisonType: StringComparison.Ordinal))
                     .Select(selector: l => DebugMetaRefPattern()
                         .Replace(input: l.Split(separator: " {", count: 2)[0], replacement: ""))
                     .ToHashSet(comparer: StringComparer.Ordinal);
        }

        HashSet<string> coldDefs = Defines(ll: coldLl);
        HashSet<string> warmDefs = Defines(ll: warmLl);
        _out.WriteLine(message: $"cold defines={coldDefs.Count}  warm defines={warmDefs.Count}");
        _out.WriteLine(
            message:
            $"cold-only: {string.Join(separator: " | ", values: coldDefs.Except(second: warmDefs).Take(count: 5))}");
        _out.WriteLine(
            message:
            $"warm-only: {string.Join(separator: " | ", values: warmDefs.Except(second: coldDefs).Take(count: 5))}");

        Assert.Empty(collection: coldResult.Errors);
        Assert.Empty(collection: warmResult.Errors);
        Assert.Equal(expected: coldDefs, actual: warmDefs);
    }

    private static HashSet<string> DefineSet(string ll)
    {
        return ll.Split(separator: '\n')
                 .Where(predicate: l =>
                      l.StartsWith(value: "define ", comparisonType: StringComparison.Ordinal))
                 .Select(selector: l => DebugMetaRefPattern()
                     .Replace(input: l.Split(separator: " {", count: 2)[0], replacement: ""))
                 .ToHashSet(comparer: StringComparer.Ordinal);
    }

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

        Program ParseB()
        {
            return new Compiler.Parser.Parser(
                tokens: new Tokenizer(source: SrcB,
                    fileName: "b.rf",
                    language: Language.RazorForge).Tokenize(),
                language: Language.RazorForge,
                fileName: "b.rf").Parse();
        }

        HashSet<string> coldADefs = DefineSet(ll: Codegen(
            r: new SemanticVerifier(language: Language.RazorForge)
               .Analyze(program: ParseTrivial())));
        HashSet<string> coldBDefs = DefineSet(ll: Codegen(
            r: new SemanticVerifier(language: Language.RazorForge).Analyze(program: ParseB())));

        // Capture the warm state ONCE — the daemon's resident in-RAM stdlib.
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);

        // Reuse it across builds, as a daemon would: A, A again, then a DIFFERENT file B.
        AnalysisResult a1 =
            new SemanticVerifier(language: Language.RazorForge, warm: warm).Analyze(
                program: ParseTrivial());
        HashSet<string> a1Defs = DefineSet(ll: Codegen(r: a1));
        AnalysisResult a2 =
            new SemanticVerifier(language: Language.RazorForge, warm: warm).Analyze(
                program: ParseTrivial());
        HashSet<string> a2Defs = DefineSet(ll: Codegen(r: a2));
        AnalysisResult b1 =
            new SemanticVerifier(language: Language.RazorForge, warm: warm).Analyze(
                program: ParseB());
        HashSet<string> b1Defs = DefineSet(ll: Codegen(r: b1));
        // A THIRD build of A after B — catches poisoning that only a DIFFERENT-file build introduces.
        AnalysisResult a3 =
            new SemanticVerifier(language: Language.RazorForge, warm: warm).Analyze(
                program: ParseTrivial());
        HashSet<string> a3Defs = DefineSet(ll: Codegen(r: a3));

        _out.WriteLine(
            message:
            $"errors: a1={a1.Errors.Count} a2={a2.Errors.Count} b1={b1.Errors.Count} a3={a3.Errors.Count}");
        _out.WriteLine(
            message:
            $"defines: coldA={coldADefs.Count} a1={a1Defs.Count} a2={a2Defs.Count} a3={a3Defs.Count} | coldB={coldBDefs.Count} b1={b1Defs.Count}");
        _out.WriteLine(
            message:
            $"a1 vs a2 diff: -{string.Join(separator: ",", values: a1Defs.Except(second: a2Defs).Take(count: 6))} +{string.Join(separator: ",", values: a2Defs.Except(second: a1Defs).Take(count: 6))}");
        _out.WriteLine(
            message:
            $"a1 vs a3 diff: -{string.Join(separator: ",", values: a1Defs.Except(second: a3Defs).Take(count: 6))} +{string.Join(separator: ",", values: a3Defs.Except(second: a1Defs).Take(count: 6))}");
        _out.WriteLine(
            message:
            $"coldB vs b1 diff: -{string.Join(separator: ",", values: coldBDefs.Except(second: b1Defs).Take(count: 6))} +{string.Join(separator: ",", values: b1Defs.Except(second: coldBDefs).Take(count: 6))}");

        Assert.Empty(collection: a1.Errors);
        Assert.Empty(collection: a2.Errors);
        Assert.Empty(collection: b1.Errors);
        Assert.Empty(collection: a3.Errors);
        // P2: every warm build must match its cold reference, and repeated builds must be identical —
        // i.e. reusing the shared warm state does NOT poison it.
        Assert.Equal(expected: coldADefs, actual: a1Defs);
        Assert.Equal(expected: a1Defs, actual: a2Defs);
        Assert.Equal(expected: coldBDefs, actual: b1Defs);
        Assert.Equal(expected: a1Defs, actual: a3Defs);
    }
}
