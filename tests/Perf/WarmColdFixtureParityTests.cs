using System.Text.RegularExpressions;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Compiler.Verification;
using Compiler.Verification.Results;
using Xunit.Abstractions;

#pragma warning disable xUnit1004
namespace RazorForge.Tests.Perf;

/// <summary>
/// WARM/COLD PARITY over the real stdlib fixtures — the systematic guard against the recurring
/// "warm and cold emit different routine define-sets" divergence. A COLD compile (fresh full-stdlib
/// SA) and a WARM compile (restored captured stdlib + the same source) must emit the IDENTICAL set of
/// LLVM <c>define</c> headers for every fixture. Any difference is a restore-fidelity / demand-seeding
/// non-determinism bug — the exact class the pull collector must never reintroduce.
///
/// <para>The curated <see cref="RiskyFixtures"/> theory runs in CI (fast subset covering errors/throws,
/// entities, iterators, generics, locks, choices, variants, routine values). <see cref="AllFixtures_Parity"/>
/// sweeps EVERY fixture and is opt-in (slow: one cold full-SA per fixture).</para>
/// </summary>
public sealed partial class WarmColdFixtureParityTests
{
    private readonly ITestOutputHelper _out;
    public WarmColdFixtureParityTests(ITestOutputHelper output) => _out = output;

    private static readonly string FixtureDir =
        Path.Combine(LocateRepoRoot(), "tests", "Fixtures", "Stdlib");

    private static string LocateRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "RazorForge.csproj"))) return dir;
            string? parent = Path.GetDirectoryName(dir);
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new InvalidOperationException("Could not locate RazorForge.csproj.");
    }

    // Captured ONCE (the daemon's resident stdlib) and shared across all cases in this class.
    private static readonly SemanticVerifier.CompiledStdlibState Warm =
        SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);

    /// <summary>Risky-surface subset — the feature areas where demand-seeding has historically drifted
    /// between warm and cold (throw→crash_message/represent, entity self-free, iterator adapters, lock
    /// policies, choice/variant formatters, routine-value teardown).</summary>
    public static TheoryData<string, string> RiskyFixtures()
    {
        // Parity-clean fixtures — ENFORCED. Adding a fixture here locks its warm≡cold define-set forever.
        string[] names =
        [
            "error_paths_api", "crashable_api", "list_api", "dict_api", "set_api",
            "sorted_dict_api", "choice_api", "variant_api", "generic_routine_api",
            "guarded_api", "guarded_access_api", "fallible_lock_api", "agent_api",
            "array_api", "range_api", "filesystem_api", "itertools_api",
        ];
        var data = new TheoryData<string, string>();
        foreach (string n in names)
        {
            string p = Path.Combine(FixtureDir, n + ".rf");
            if (File.Exists(p)) data.Add(n, p);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(RiskyFixtures))]
    public void RiskyFixture_WarmMatchesCold(string name, string path)
    {
        (int cold, int warm, string[] coldOnly, string[] warmOnly) = CompareDefines(path);
        _out.WriteLine($"{name}: coldDefs={cold} warmDefs={warm}");
        if (coldOnly.Length > 0) _out.WriteLine($"  cold-only: {string.Join(" | ", coldOnly.Take(10))}");
        if (warmOnly.Length > 0) _out.WriteLine($"  warm-only: {string.Join(" | ", warmOnly.Take(10))}");
        Assert.True(coldOnly.Length == 0 && warmOnly.Length == 0,
            $"{name}: warm/cold define-set diverged — cold-only=[{string.Join(", ", coldOnly)}] " +
            $"warm-only=[{string.Join(", ", warmOnly)}]");
    }

    [Fact(Skip = "Slow full sweep (one cold full-SA per fixture, ~192×). Un-skip to audit ALL fixtures.")]
    public void AllFixtures_Parity()
    {
        var diverged = new List<string>();
        foreach (string path in Directory.GetFiles(FixtureDir, "*.rf").OrderBy(p => p))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            (_, _, string[] coldOnly, string[] warmOnly) = CompareDefines(path);
            if (coldOnly.Length > 0 || warmOnly.Length > 0)
            {
                diverged.Add(name);
                _out.WriteLine($"DIVERGED {name}: cold-only=[{string.Join(", ", coldOnly.Take(6))}] " +
                    $"warm-only=[{string.Join(", ", warmOnly.Take(6))}]");
            }
        }
        Assert.True(diverged.Count == 0, $"{diverged.Count} fixtures diverged: {string.Join(", ", diverged)}");
    }

    private static (int cold, int warm, string[] coldOnly, string[] warmOnly) CompareDefines(string path)
    {
        string src = File.ReadAllText(path);
        var coldDefs = DefineSet(Codegen(new SemanticVerifier(language: Language.RazorForge)
            .Analyze(program: Parse(src, path))));
        var warmDefs = DefineSet(Codegen(new SemanticVerifier(language: Language.RazorForge, warm: Warm)
            .Analyze(program: Parse(src, path))));
        return (coldDefs.Count, warmDefs.Count,
            coldDefs.Except(warmDefs).OrderBy(s => s).ToArray(),
            warmDefs.Except(coldDefs).OrderBy(s => s).ToArray());
    }

    private static Program Parse(string src, string path)
    {
        var tokens = new Tokenizer(source: src, fileName: path, language: Language.RazorForge).Tokenize();
        return new Compiler.Parser.Parser(tokens: tokens, language: Language.RazorForge,
            fileName: path).Parse();
    }

    private static string Codegen(AnalysisResult r)
    {
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms, instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys, registry: r.Registry);
        var gen = new Compiler.CodeGen.LlvmCodeGenerator(
            userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new Compiler.CodeGen.LlvmCodeGeneratorOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies,
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            });
        return gen.Generate();
    }

    [GeneratedRegex(@" !dbg ![0-9]+")]
    private static partial Regex DbgAnnotationRegex();

    private static HashSet<string> DefineSet(string ll) =>
        ll.Split('\n')
          .Where(l => l.StartsWith("define ", StringComparison.Ordinal))
          .Select(l => DbgAnnotationRegex().Replace(l.Split(" {", 2)[0], ""))
          .ToHashSet(StringComparer.Ordinal);
}
