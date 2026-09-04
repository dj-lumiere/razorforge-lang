using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Verification;
using Verification.Results;
using Xunit;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Regression lock for the warm-restore stdlib re-lowering crash. The compile daemon captures a fully
/// lowered stdlib snapshot once, then serves many warm compiles from it. A warm compile that imports a
/// non-Core module (e.g. <c>Collections.Deque</c>) triggers the fresh loader's <c>ScanStdlibFiles</c>,
/// which re-parses EVERY <c>module Core</c> file into <c>_corePrograms</c> as unanalyzed/unlowered ASTs.
/// Before the fix those stale copies leaked into <c>FreshlyLoadedStdlibPrograms</c>, so the postprocessing
/// pipeline tried to lower them and threw ("ConditionalExpression reached ExpressionLoweringPass without a
/// resolved type" on a D128 ternary). The fix flags the warm loader <c>CoreResident</c> so its
/// <c>AllLoadedPrograms</c> excludes the fresh Core re-parse. Runs multiple warm compiles on ONE shared
/// snapshot to also catch any cross-compile corruption of the restored ASTs.
/// </summary>
public sealed class WarmRestoreReproTests
{
    private readonly ITestOutputHelper _out;
    public WarmRestoreReproTests(ITestOutputHelper output) => _out = output;

    private static Program Parse(string src, string file) =>
        new Compiler.Parser.Parser(
            tokens: new Tokenizer(source: src, fileName: file, language: Language.RazorForge).Tokenize(),
            language: Language.RazorForge, fileName: file).Parse();

    [Fact]
    public void WarmRestore_ImportsNonCoreModule_DoesNotReLowerStdlibCore()
    {
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);

        // A non-Core import (Collections.Deque) is what makes the fresh loader re-scan stdlib and re-parse
        // every Core file — the trigger for the leaked-fresh-Core crash this test guards.
        const string user = """
                            module Bench
                            import IO/Console
                            import Collections.Deque
                            routine start()
                              var d: Deque[S32] = [1, 2, 3]
                              each v in d
                                show(f"{v}")
                              return
                            """;

        // The daemon serves many compiles from one snapshot — run several to catch cross-compile mutation.
        for (int i = 1; i <= 3; i++)
        {
            var analyzer = new SemanticVerifier(language: Language.RazorForge, warm: warm);
            AnalysisResult r = analyzer.AnalyzeMultiple(
                files: new List<(Program, string)> { (Parse(user, "bench.rf"), "bench.rf") });
            _out.WriteLine($"run {i}: errors={r.Errors.Count} fresh={analyzer.Registry.FreshlyLoadedStdlibPrograms.Count}");
            foreach (SemanticError e in r.Errors.Take(10)) _out.WriteLine(e.ToString());
            Assert.Empty(collection: r.Errors);
        }
    }
}
