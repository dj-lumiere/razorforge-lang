using Compiler.Serialization;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Compiler.Verification;
using Compiler.Verification.Results;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Stage-2 oracle for modular .pbrf: partition the compiled
/// stdlib into per-module artifacts on disk, reassemble via the shell two-phase loader, and assert a compile
/// driven by the REASSEMBLED state emits the EXACT same routine-define set as a cold compile. This proves the
/// per-module partition + extern/shell re-link reproduces the whole semantic model with reference identity
/// intact (cross-module cycles and all).
/// </summary>
public sealed class ModularPbrfRoundTripTests
{
    private readonly ITestOutputHelper _out;
    public ModularPbrfRoundTripTests(ITestOutputHelper output) => _out = output;

    private const string Trivial = """
                                   module Bench
                                   import IO/Console
                                   routine start()
                                     show("hi")
                                     return
                                   """;

    private static Program ParseTrivial()
    {
        var tokens = new Tokenizer(source: Trivial, fileName: "bench.rf", language: Language.RazorForge).Tokenize();
        return new Compiler.Parser.Parser(tokens: tokens, language: Language.RazorForge, fileName: "bench.rf").Parse();
    }

    private static string Codegen(AnalysisResult r)
    {
        Compiler.Desugaring.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms, instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys, registry: r.Registry);
        var gen = new Compiler.CodeGen.LlvmCodeGenerator(
            userPrograms: r.Registry.UserPrograms, registry: r.Registry,
            stdlibPrograms: r.Registry.StdlibPrograms, synthesizedBodies: r.SynthesizedBodies,
            instantiatedGenericBodies: r.InstantiatedGenericBodies, liveRoutineKeys: r.LiveRoutineKeys,
            maySuspendRoutineKeys: r.MaySuspendRoutineKeys);
        return gen.Generate();
    }

    private static System.Collections.Generic.HashSet<string> DefineSet(string ll) =>
        ll.Split('\n')
          .Where(l => l.StartsWith("define ", System.StringComparison.Ordinal))
          .Select(l => System.Text.RegularExpressions.Regex.Replace(l.Split(" {", 2)[0], @" !dbg ![0-9]+", ""))
          .ToHashSet(System.StringComparer.Ordinal);

    [Fact]
    public void ModularRoundTrip_ProducesIdenticalDefines()
    {
        var cold = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult coldResult = cold.Analyze(program: ParseTrivial());
        var coldDefs = DefineSet(ll: Codegen(r: coldResult));

        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);

        string dir = Path.Combine(Path.GetTempPath(), "rf_modular_pbrf_" + System.Guid.NewGuid().ToString("N"));
        try
        {
            var labels = ModularStdlibCache.Serialize(state: warm, dir: dir);
            long total = Directory.GetFiles(dir).Sum(f => new FileInfo(f).Length);
            _out.WriteLine($"modules={labels.Count}  artifacts total={total / 1024}KB");

            SemanticVerifier.CompiledStdlibState restored = ModularStdlibCache.Deserialize(dir: dir);

            var warmSa = new SemanticVerifier(language: Language.RazorForge, warm: restored);
            AnalysisResult warmResult = warmSa.Analyze(program: ParseTrivial());
            var warmDefs = DefineSet(ll: Codegen(r: warmResult));

            _out.WriteLine($"coldDefs={coldDefs.Count}  warmDefs={warmDefs.Count}");
            _out.WriteLine($"cold-only: {string.Join(" | ", coldDefs.Except(warmDefs).Take(8))}");
            _out.WriteLine($"warm-only: {string.Join(" | ", warmDefs.Except(coldDefs).Take(8))}");

            Assert.Empty(warmResult.Errors);
            Assert.Equal(coldDefs, warmDefs);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }
}
