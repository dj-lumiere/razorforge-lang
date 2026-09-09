using Compiler.CodeGen;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Compiler.Verification;
using Compiler.Verification.Results;
using System.Text.RegularExpressions;
using Xunit.Abstractions;

#pragma warning disable xUnit1004
namespace RazorForge.Tests.Perf;

/// <summary>
/// Resident-JIT incremental Phase 0a (see internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md §2A.5): validates
/// <see cref="LlvmCodeGenerator.GenerateBase"/> — the NON-PRUNED precompiled stdlib base. The load-bearing
/// correctness property is that the base is a SUPERSET of any pruned build's stdlib defines: a delta module
/// only ever emits an extern <c>declare</c> for a symbol it doesn't define, so every such symbol MUST be
/// present in the base or JIT-link fails with an undefined symbol.
/// </summary>
public sealed partial class BaseEmissionTests
{
    [GeneratedRegex(@",? ?\d+")]
    private static partial Regex ArityDigitsRegex();

    private readonly ITestOutputHelper _out;
    public BaseEmissionTests(ITestOutputHelper output) => _out = output;

    private const string Trivial = """
                                   module Bench
                                   import IO/Console
                                   routine start()
                                     show("hi")
                                     return
                                   """;

    private static Program Parse(string src, string file) =>
        new Compiler.Parser.Parser(
            tokens: new Tokenizer(source: src, fileName: file, language: Language.RazorForge).Tokenize(),
            language: Language.RazorForge, fileName: file).Parse();

    /// <summary>The mangled symbol defined by each `define ...` line (the first <c>@"..."</c>/<c>@ident</c>).</summary>
    private static HashSet<string> DefinedSymbols(string ll) => SymbolsOnLinesStartingWith(ll, "define ");

    /// <summary>The mangled symbol referenced by each `declare ...` line (an extern reference).</summary>
    private static HashSet<string> DeclaredSymbols(string ll) => SymbolsOnLinesStartingWith(ll, "declare ");

    private static HashSet<string> SymbolsOnLinesStartingWith(string ll, string prefix)
    {
        var set = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string line in ll.Split('\n'))
        {
            if (!line.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal)) continue;
            int at = line.IndexOf(value: '@');
            if (at < 0) continue;
            string sym;
            if (at + 1 < line.Length && line[at + 1] == '"')
            {
                int end = line.IndexOf(value: '"', startIndex: at + 2);
                if (end < 0) continue;
                sym = line.Substring(startIndex: at + 1, length: end - at); // includes surrounding quotes
            }
            else
            {
                int end = line.IndexOf(value: '(', startIndex: at);
                if (end < 0) continue;
                sym = line.Substring(startIndex: at + 1, length: end - at - 1).Trim();
            }
            set.Add(item: sym);
        }
        return set;
    }

    /// <summary>The normal whole-program (pruned) build — what ships today.</summary>
    private static string PrunedBuild(AnalysisResult r) => new LlvmCodeGenerator(
        userPrograms: r.Registry.UserPrograms,
        registry: r.Registry,
        options: new LlvmCodeGeneratorOptions
        {
            StdlibPrograms = r.Registry.StdlibPrograms,
            SynthesizedBodies = r.SynthesizedBodies,
            InstantiatedGenericBodies = r.InstantiatedGenericBodies,
            LiveRoutineKeys = r.LiveRoutineKeys,
            MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
        }).Generate();

    /// <summary>The delta build: user code with the base's symbols marked resident (⇒ declare, not define).</summary>
    private static string DeltaBuild(AnalysisResult r, IReadOnlyCollection<string> residentSymbols) =>
        new LlvmCodeGenerator(
            userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies,
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys,
                ResidentSymbols = residentSymbols
            }).Generate();

    [Fact]
    public void GenerateBase_And_Delta_CoverPrunedBuild_WithTinyDelta()
    {
        // Cold analyze retains StdlibPrograms (the warm/snapshot path drops them).
        AnalysisResult r = new SemanticVerifier(language: Language.RazorForge).Analyze(program: Parse(Trivial, "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);

        // BASE: empty user programs + null live set (⇒ non-pruned) + stdlib present. Runs to completion
        // over the full stdlib (the non-pruned-emission risk) and emits no @main.
        var baseGen = new LlvmCodeGenerator(
            userPrograms: new List<(Program, string, string)>(),
            registry: r.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies
            });
        (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

        Assert.False(condition: string.IsNullOrWhiteSpace(value: baseIr));
        Assert.DoesNotContain(expectedSubstring: "define i32 @main(", actualString: baseIr);
        Assert.True(condition: baseSyms.Count > 100,
            userMessage: $"base should emit hundreds of stdlib symbols non-pruned, got {baseSyms.Count}");

        // DELTA: user build with base symbols resident. C4 makes it declare (not define) resident symbols;
        // if the over-prune tripwire misfired on a resident symbol this would THROW here.
        string deltaIr = DeltaBuild(r, residentSymbols: baseSyms);

        HashSet<string> baseDefs = DefinedSymbols(ll: baseIr);
        HashSet<string> deltaDefs = DefinedSymbols(ll: deltaIr);
        HashSet<string> prunedDefs = DefinedSymbols(ll: PrunedBuild(r));

        // Correctness: base ∪ delta covers everything the pruned whole-program build defines.
        HashSet<string> union = new(collection: baseDefs, comparer: StringComparer.Ordinal);
        union.UnionWith(other: deltaDefs);
        List<string> uncovered = prunedDefs.Except(second: union).OrderBy(keySelector: s => s).ToList();
        _out.WriteLine($"base={baseDefs.Count}  delta={deltaDefs.Count}  pruned={prunedDefs.Count}  uncovered={uncovered.Count}");
        _out.WriteLine("DELTA defines:\n  " + string.Join("\n  ", deltaDefs.OrderBy(s => s)));
        if (uncovered.Count > 0)
            _out.WriteLine("UNCOVERED:\n  " + string.Join("\n  ", uncovered.Take(count: 40)));
        Assert.True(condition: uncovered.Count == 0,
            userMessage: $"{uncovered.Count} pruned define(s) in NEITHER base nor delta: " +
                         string.Join(", ", uncovered.Take(count: 10)));

        // The payoff: the delta is tiny — user routines + the handful of user-triggered stdlib variants
        // (e.g. the show(value,end) overload the call site synthesizes), NOT the whole stdlib.
        Assert.True(condition: deltaDefs.Count < 40,
            userMessage: $"delta should be tiny (user + user-triggered variants), got {deltaDefs.Count}");
    }

    /// <summary>
    /// Phase 0a (a') JIT-COMPLETENESS oracle (see RESIDENT-JIT-INCREMENTAL-V0.5.md ★ MAJOR FINDING): the
    /// non-pruned base PARSES clean (bugs #1/#2/#3 fixed) yet still fails to JIT because it DECLARES far more
    /// than it DEFINES — a large transitive tail of instantiations/variants/derives the empty-program analyze
    /// never materialized. The coverage test misses this (it only checks base∪delta ⊇ the small PRUNED set).
    /// This oracle measures <c>declared − defined</c> directly, split into RF-mangled symbols (quoted — MUST
    /// be defined by the base or JIT-materialization fails) vs bare runtime externs (rf_/llvm./__/lowercase C
    /// — legitimately resolved from the runtime DLL, NOT the base's job to define). Drive the RF-mangled gap
    /// to zero to make the non-pruned base JIT-complete. Pure string analysis — no libLLVM, runs in CI.
    /// Skip'd until the (a') closure-materialization work lands (currently ~782 RF-mangled gap, dominated by
    /// const-generic Array[T,N]/from_literal variadic machinery + UnpackedFloat[U,UN] per-width transcendentals
    /// — generic instantiations the empty-program analyze never monomorphized). Run locally to measure the gap.
    /// With SeedAllStdlibRoutines + the LiveBackendType fix + removing dead list-BuilderQuery reflection
    /// routines, the gap is currently ~317 (down from 782). The remaining tail is a scattered set of real
    /// generic instantiations (const-generic Array[U&lt;w&gt;,N] trace-buffer support, List[enum].from_literal
    /// data tables, add_range, Maybe[BTreeNode].destroy) referenced by concrete stdlib bodies but not yet
    /// materialized — the genuine (a') closure tail, now free of BuilderQuery artifacts.
    /// </summary>
    [Fact(Skip = "WIP resident-JIT base define-completeness oracle. Base BUILDS at gap=102/defined=12309 (was 695). ISOLATED-BUILD primitive (GenericClosurePass.RunIsolatedTail, post-fixpoint, build-one-no-drain) closes: entity self-free tail (call-driven closure) + all per-type LIFECYCLE HOOKS (destroy/roam_free_impl/roam_trace_impl) built on every registered concrete+wrapper instance (bounded, self-contained; the call-driven closure discovers the leaf callees field.destroy/cyclic_visit/Hijacked.cyclic_trace_buffer; skip unfolded-comptime Array[U8,${...}] carriers). Remaining 102 = represent(98)+serialize: represent is a force-seeded display closure that does NOT converge (its callees escape the registry — adding it took gap 140->367); needs delta-definition or codegen-materialize. base+delta coverage proven by GenerateBase_And_Delta_CoverPrunedBuild_WithTinyDelta. See .claude-memory/base-completeness-const-generic-array-gap.md.")]
    public void GenerateBase_Standalone_DefineCompleteness()
    {
        var baseSa = new SemanticVerifier(language: Language.RazorForge) { SeedAllStdlibRoutines = true };
        AnalysisResult baseR = baseSa.Analyze(program: Parse("module Base\nroutine start()\n  return", "base.rf"));
        Assert.Empty(collection: baseR.Errors);
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: baseR.Registry.UserPrograms,
            instantiatedBodies: baseR.InstantiatedGenericBodies,
            maySuspendKeys: baseR.MaySuspendRoutineKeys,
            registry: baseR.Registry);

        var baseGen = new LlvmCodeGenerator(
            userPrograms: new List<(Program, string, string)>(),
            registry: baseR.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = baseR.Registry.StdlibPrograms,
                SynthesizedBodies = baseR.SynthesizedBodies,
                InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
            });
        (string baseIr, _) = baseGen.GenerateBase();
        System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rf_base.ll"), baseIr);

        HashSet<string> defined = DefinedSymbols(ll: baseIr);
        HashSet<string> declared = DeclaredSymbols(ll: baseIr);
        // gap = referenced-but-not-defined. A quoted symbol ("[member] ...", "$...") is RF-emitted code that
        // the base OWNS (must define); a bare ident is a runtime extern resolved from the runtime DLL.
        List<string> gap = declared.Except(second: defined).OrderBy(keySelector: s => s).ToList();
        List<string> rfGap = gap.Where(predicate: s => s.StartsWith('"')).ToList();
        List<string> externGap = gap.Where(predicate: s => !s.StartsWith('"')).ToList();

        _out.WriteLine($"base: defined={defined.Count} declared={declared.Count} gap={gap.Count} (rf-mangled={rfGap.Count}, bare-extern={externGap.Count})");
        // Category histogram: collapse arity/type args so we see the SHAPE of the gap, not 700 near-dupes.
        var byShape = rfGap
            .Select(selector: s => ArityDigitsRegex().Replace(input: s, replacement: "N"))
            .GroupBy(keySelector: s => s)
            .Select(selector: g => (Shape: g.Key, Count: g.Count()))
            .OrderByDescending(keySelector: g => g.Count)
            .ToList();
        _out.WriteLine($"RF-MANGLED GAP by shape ({byShape.Count} distinct shapes):\n  " +
                       string.Join("\n  ", byShape.Take(count: 60).Select(g => $"{g.Count,4}  {g.Shape}")));
        _out.WriteLine("RF-MANGLED GAP (raw, first 60):\n  " +
                       string.Join("\n  ", rfGap.Take(count: 60)));
        _out.WriteLine("BARE-EXTERN GAP (runtime DLL resolves these — expected):\n  " +
                       string.Join("\n  ", externGap.Take(count: 40)));

        Assert.True(condition: rfGap.Count == 0,
            userMessage: $"{rfGap.Count} RF-mangled symbol(s) referenced but never defined by the non-pruned base " +
                         $"(JIT would 'Failed to materialize' these): {string.Join(", ", rfGap.Take(count: 12))}");
    }

    /// <summary>
    /// Phase 0a (a') hardening harness: build a STANDALONE stdlib base (empty user program, so its
    /// instantiations are stdlib-internal only — no user-triggered pollution) and PARSE-validate the base
    /// IR. No native execution, so this isolates genuine malformed-emission bugs in non-pruned stdlib
    /// routines. NEEDS libLLVM staged next to the test binary (parse is an LLVM call) → Skip'd in CI.
    /// </summary>
    [Fact(Skip = "Local Phase 0a (a') hardening harness: needs libLLVM staged next to the test binary (parse is an LLVM call). Currently FAILS — non-pruned base surfaces a genuine stdlib-internal monomorphization gap: List[Bytes].duplicate() calls abstract Core.Copyable.duplicate() (ptr) instead of concrete Bytes.duplicate(). See RESIDENT-JIT doc Phase 0a finding.")]
    public void GenerateBase_Standalone_ParsesValid()
    {
        AnalysisResult baseR = new SemanticVerifier(language: Language.RazorForge)
            .Analyze(program: Parse("module Base\nroutine start()\n  return", "base.rf"));
        Assert.Empty(collection: baseR.Errors);
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: baseR.Registry.UserPrograms,
            instantiatedBodies: baseR.InstantiatedGenericBodies,
            maySuspendKeys: baseR.MaySuspendRoutineKeys,
            registry: baseR.Registry);

        var baseGen = new LlvmCodeGenerator(
            userPrograms: new List<(Program, string, string)>(),
            registry: baseR.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = baseR.Registry.StdlibPrograms,
                SynthesizedBodies = baseR.SynthesizedBodies,
                InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
            });
        (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

        bool ok = Builder.OrcJitExecutor.TryParseIr(llvmIr: baseIr, out string? err);
        _out.WriteLine($"standalone base: syms={baseSyms.Count} chars={baseIr.Length} parse={(ok ? "OK" : err)}");
        Assert.True(condition: ok, userMessage: $"standalone base IR failed to parse: {err}");
    }

    /// <summary>
    /// Phase 0a step 3: actually JIT-and-run the base+delta split (single dylib, option-3 disposable
    /// client) and assert <c>@main</c> runs to a 0 exit. NEEDS <c>libLLVM.dll</c> staged next to the test
    /// binary AND runs the native RF runtime IN-PROCESS (scheduler threads, process-global runtime state),
    /// so it is Skip'd in CI/suite — run locally to validate execution.
    /// </summary>
    [Fact(Skip = "Local ORC-JIT run: base+delta JIT now PARSES clean (root declare fix); blocked on base DEFINE-completeness — main transitively needs the ~65 declared-but-undefined base symbols. See RESIDENT-JIT-INCREMENTAL-V0.5.md.")]
    public void JitAndRunSplit_BasePlusDelta_RunsMain()
    {
        // BASE: a full-closure analyze (SeedAllStdlibRoutines) so the resident base is JIT-complete —
        // it must DEFINE every stdlib symbol the delta will mark resident (extern-declare).
        var baseSa = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult baseR = baseSa.Analyze(program: Parse("module Base\nroutine start()\n  return", "base.rf"));
        Assert.Empty(collection: baseR.Errors);
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: baseR.Registry.UserPrograms,
            instantiatedBodies: baseR.InstantiatedGenericBodies,
            maySuspendKeys: baseR.MaySuspendRoutineKeys,
            registry: baseR.Registry);
        var baseGen = new LlvmCodeGenerator(
            userPrograms: new List<(Program, string, string)>(),
            registry: baseR.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = baseR.Registry.StdlibPrograms,
                SynthesizedBodies = baseR.SynthesizedBodies,
                InstantiatedGenericBodies = baseR.InstantiatedGenericBodies
            });
        (string baseIr, IReadOnlyCollection<string> baseSyms) = baseGen.GenerateBase();

        // DELTA: the actual user program, with the base's symbols marked resident (⇒ extern declare).
        AnalysisResult r = new SemanticVerifier(language: Language.RazorForge).Analyze(program: Parse(Trivial, "bench.rf"));
        Assert.Empty(collection: r.Errors);
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);
        string deltaIr = DeltaBuild(r, residentSymbols: baseSyms);

        int rc = Builder.OrcJitExecutor.JitAndRunSplit(
            baseIr: baseIr, deltaIr: deltaIr, programName: "test", programArgs: Array.Empty<string>());
        _out.WriteLine($"JitAndRunSplit exit code = {rc}");
        Assert.Equal(expected: 0, actual: rc);
    }
}
