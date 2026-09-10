using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Builder.Verification;
using Builder.Verification.Results;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Cross-build isolation guard: many warm compiles served from ONE captured
/// <see cref="SemanticVerifier.CompiledStdlibState"/> must each emit the IDENTICAL LLVM define-set. The
/// captured stdlib type/routine/AST objects are shared BY REFERENCE across every warm build, so a build that
/// mutates one of them in place (e.g. a lowering/classification pass rewriting a reached stdlib body) leaks
/// into the next build — the warm/cold define-set divergence. This runs several successive warm builds of the
/// same trivial program and asserts define-set stability (a regression lock for the per-build isolation fix:
/// the collector clones a reached stdlib body before lowering; the lazy flag lives in a per-registry
/// side-table; the warm template lookup clones on read).
/// </summary>
public sealed class WarmLeakDiagTests
{
    private readonly ITestOutputHelper _out;
    public WarmLeakDiagTests(ITestOutputHelper output) => _out = output;

    private static Program Parse(string src, string path)
    {
        List<Token> tokens =
            new Tokenizer(source: src, fileName: path, language: Language.RazorForge).Tokenize();
        return new Builder.Parser.Parser(tokens: tokens, language: Language.RazorForge,
            fileName: path).Parse();
    }

    private static readonly System.Text.RegularExpressions.Regex DbgRe =
        new(@" !dbg ![0-9]+");

    private static HashSet<string> DefineSet(string ll)
    {
        return ll.Split('\n')
                 .Where(l => l.StartsWith("define ", StringComparison.Ordinal))
                 .Select(l => DbgRe.Replace(l.Split(" {", 2)[0], ""))
                 .ToHashSet(StringComparer.Ordinal);
    }

    private static string Codegen(AnalysisResult r)
    {
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys, registry: r.Registry);
        var gen = new Builder.LlvmEmit.LlvmEmitter(userPrograms: r.Registry.UserPrograms,
            registry: r.Registry,
            options: new Builder.LlvmEmit.LlvmEmitterOptions
            {
                StdlibPrograms = r.Registry.StdlibPrograms,
                SynthesizedBodies = r.SynthesizedBodies,
                InstantiatedGenericBodies = r.InstantiatedGenericBodies,
                LiveRoutineKeys = r.LiveRoutineKeys,
                MaySuspendRoutineKeys = r.MaySuspendRoutineKeys
            });
        return gen.Generate();
    }

    [Fact]
    public void SuccessiveWarmBuilds_SameProgram_IdenticalDefines()
    {
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);

        // Exercise the each/til → Range[U64] iterator chain: the field-init Range creator inside the
        // (generic-def) Range iterator template is exactly the +1 define that leaked across warm builds
        // when the shared snapshot template was mutated in place by on-demand SA.
        const string user = """
                            module Bench
                            import IO/Console
                            routine start()
                              var total: U64 = 0u64
                              each i in 0u64 til 5u64
                                total = total + i
                              show(f"{total}")
                              return
                            """;

        var sets = new List<HashSet<string>>();
        for (int i = 0; i < 4; i++)
            sets.Add(DefineSet(Codegen(
                new SemanticVerifier(language: Language.RazorForge, warm: warm).Analyze(
                    Parse(user, "bench.rf")))));

        _out.WriteLine($"counts: {string.Join(", ", sets.Select(s => s.Count))}");
        for (int i = 1; i < sets.Count; i++)
        {
            var newly = sets[i].Except(sets[0]).OrderBy(s => s).ToArray();
            var gone = sets[0].Except(sets[i]).OrderBy(s => s).ToArray();
            Assert.True(newly.Length == 0 && gone.Length == 0,
                $"warm build {i + 1} diverged from build 1: newly=[{string.Join(", ", newly)}] gone=[{string.Join(", ", gone)}]");
        }
    }
}
