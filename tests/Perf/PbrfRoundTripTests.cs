using System.Diagnostics;
using System.Text.RegularExpressions;
using Compiler.Serialization;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Enums;
using Compiler.Verification;
using Compiler.Verification.Results;
using Xunit.Abstractions;

namespace RazorForge.Tests.Perf;

/// <summary>
/// De-risk spike for the .pbrf compiled-stdlib serialization (the "cold under 1 s" foundation):
/// capture the compiled stdlib, serialize it to a .pbrf byte stream, deserialize it, and assert that a
/// compile driven by the DESERIALIZED snapshot emits the EXACT same routine-define set as a cold compile.
/// This validates the reflection-based graph serializer round-trips the whole semantic model (TypeInfo/
/// RoutineInfo tables + lowered AST bodies) with reference identity intact. Also prints size/time.
/// </summary>
public sealed partial class PbrfRoundTripTests
{
    private readonly ITestOutputHelper _out;
    public PbrfRoundTripTests(ITestOutputHelper output)
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

    private static string Codegen(AnalysisResult r)
    {
        Compiler.Lowering.Passes.CancellationInstrumentationPass.Run(
            programs: r.Registry.UserPrograms,
            instantiatedBodies: r.InstantiatedGenericBodies,
            maySuspendKeys: r.MaySuspendRoutineKeys,
            registry: r.Registry);
        var gen = new Compiler.CodeGen.LlvmCodeGenerator(userPrograms: r.Registry.UserPrograms,
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

    [GeneratedRegex(pattern: @" !dbg ![0-9]+")]
    private static partial Regex DebugAnnotationPattern();

    private static HashSet<string> DefineSet(string ll)
    {
        return ll.Split(separator: '\n')
                 .Where(predicate: l =>
                      l.StartsWith(value: "define ", comparisonType: StringComparison.Ordinal))
                 .Select(selector: l => DebugAnnotationPattern()
                     .Replace(input: l.Split(separator: " {", count: 2)[0], replacement: ""))
                 .ToHashSet(comparer: StringComparer.Ordinal);
    }

    [Fact]
    public void Pbrf_RoundTrip_ProducesIdenticalDefines()
    {
        // Cold reference define set.
        var cold = new SemanticVerifier(language: Language.RazorForge);
        AnalysisResult coldResult = cold.Analyze(program: ParseTrivial());
        HashSet<string> coldDefs = DefineSet(ll: Codegen(r: coldResult));

        // Capture the compiled stdlib, serialize → .pbrf bytes, deserialize.
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: Language.RazorForge);

        byte[] bytes;
        var swSer = Stopwatch.StartNew();
        using (var ms = new MemoryStream())
        {
            PbrfSerializer.Serialize(stream: ms, root: warm);
            bytes = ms.ToArray();
        }

        swSer.Stop();

        SemanticVerifier.CompiledStdlibState restored;
        var swDe = Stopwatch.StartNew();
        using (var ms = new MemoryStream(buffer: bytes))
        {
            restored =
                PbrfSerializer.Deserialize<SemanticVerifier.CompiledStdlibState>(stream: ms);
        }

        swDe.Stop();

        _out.WriteLine(
            message:
            $".pbrf size={bytes.Length / 1024}KB  serialize={swSer.ElapsedMilliseconds}ms  deserialize={swDe.ElapsedMilliseconds}ms");

        // Compile the SAME file from the DESERIALIZED warm state; compare emitted defines to cold.
        var warmSa = new SemanticVerifier(language: Language.RazorForge, warm: restored);
        AnalysisResult warmResult = warmSa.Analyze(program: ParseTrivial());
        HashSet<string> warmDefs = DefineSet(ll: Codegen(r: warmResult));

        _out.WriteLine(message: $"coldDefs={coldDefs.Count}  warmDefs={warmDefs.Count}");
        _out.WriteLine(
            message:
            $"cold-only: {string.Join(separator: " | ", values: coldDefs.Except(second: warmDefs).Take(count: 5))}");
        _out.WriteLine(
            message:
            $"warm-only: {string.Join(separator: " | ", values: warmDefs.Except(second: coldDefs).Take(count: 5))}");

        Assert.Empty(collection: warmResult.Errors);
        Assert.Equal(expected: coldDefs, actual: warmDefs);
    }
}
