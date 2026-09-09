using Compiler.CodeGen;
using Compiler.Diagnostics;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using Compiler.Verification;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains cross-phase tests for source input edge cases after parsing.
/// </summary>
public class CompilerPipelineInputEdgeCaseTests
{
    private const string TestRoutineIrSignature = "define void @\"[independent] test()\"";

    /// <summary>
    /// Verifies that source forms with no declarations survive semantic analysis as empty programs.
    /// </summary>
    /// <param name="source">The source text to analyze.</param>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("# comment\n  # indented comment\n")]
    [InlineData("\uFEFF")]
    public void Analyze_DeclarationFreeSources_ProducesNoErrors(string source)
    {
        AnalysisResult result = Analyze(source: source);

        Assert.Empty(collection: result.Errors);
        Assert.Empty(collection: result.Warnings);
        Assert.Empty(collection: Parse(source: source)
           .Declarations);
    }

    /// <summary>
    /// Verifies that normalized source text reaches all semantic pipeline phases successfully.
    /// </summary>
    /// <param name="source">The source text to analyze.</param>
    [Theory]
    [InlineData("\uFEFFroutine test()\n  return\n")]
    [InlineData("routine test()\r\n  return\r\n")]
    [InlineData("routine test()\r  return\r")]
    [InlineData("routine test()\r\n  var value = 1\r  return\n")]
    public void Analyze_NormalizedSourceForms_ProducesNoErrors(string source)
    {
        AnalysisResult result = Analyze(source: source);

        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that lexical source validation stops invalid input before semantic analysis.
    /// </summary>
    /// <param name="source">The invalid source text.</param>
    [Theory]
    [InlineData("routine test()\n\treturn\n")]
    [InlineData("routine test()\n  var value = 1\0\n  return\n")]
    [InlineData("routine\u00A0test()\n  return\n")]
    [InlineData("routine\u200Btest()\n  return\n")]
    [InlineData("routine \uFEFFtest()\n  return\n")]
    public void Analyze_InvalidSourceForms_ThrowGrammarException(string source)
    {
        GrammarException exception = Assert.Throws<GrammarException>(
            testCode: () => Analyze(source: source));

        Assert.Equal(expected: GrammarDiagnosticCode.InvalidCharacter, actual: exception.Code);
    }

    /// <summary>
    /// Verifies that code generation accepts a declaration-free analyzed program.
    /// </summary>
    /// <param name="source">The declaration-free source text.</param>
    [Theory]
    [InlineData("")]
    [InlineData("\uFEFF")]
    [InlineData("# comment\n")]
    public void Codegen_DeclarationFreeSources_GeneratesModuleHeader(string source)
    {
        string llvmIr = GenerateIr(source: source);

        Assert.Contains(expectedSubstring: "; ModuleID = 'razorforge_module'",
            actualString: llvmIr);
    }

    /// <summary>
    /// Verifies that code generation accepts normalized line endings.
    /// </summary>
    /// <param name="lineEnding">The line ending sequence to use.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Codegen_LineEndingVariants_GenerateNormalizedIr(string lineEnding)
    {
        string source = string.Join(separator: lineEnding,
            value:
            [
                "routine test()",
                "  return",
                ""
            ]);

        string llvmIr = GenerateIr(source: source);

        Assert.Contains(expectedSubstring: TestRoutineIrSignature, actualString: llvmIr);
        Assert.DoesNotContain(expectedSubstring: "\r", actualString: llvmIr);
    }

    /// <summary>
    /// Verifies that a leading BOM survives the whole parser-analyzer-codegen path.
    /// </summary>
    [Fact]
    public void Codegen_BomAtStart_GeneratesRoutineDefinition()
    {
        string llvmIr = GenerateIr(source: "\uFEFFroutine test()\n  return\n");

        Assert.Contains(expectedSubstring: TestRoutineIrSignature, actualString: llvmIr);
    }

    /// <summary>
    /// Verifies that long valid source lines survive analyzer and codegen.
    /// </summary>
    [Fact]
    public void Codegen_VeryLongLine_GeneratesRoutineDefinition()
    {
        string longName = "value_" + new string(c: 'x', count: 12_000);
        string source = $"routine test()\n  var {longName} = 1\n  return\n";

        string llvmIr = GenerateIr(source: source);

        Assert.Contains(expectedSubstring: TestRoutineIrSignature, actualString: llvmIr);
    }

    /// <summary>
    /// Verifies that deeply nested valid source survives analyzer and codegen.
    /// </summary>
    /// <remarks>
    /// Capped at 30s. Build 886 ran 1h 35m before failing — a 36-line source (32 nested
    /// `if true` blocks) should never need more than a fraction of a second. A timeout here
    /// surfaces algorithmic regressions in seconds instead of bleeding CI throughput.
    /// Repro source mirrored at <c>playground/deeply_nested_repro.rf</c>.
    /// </remarks>
    // Timeout is generous (not the ~6 s baseline) on purpose: the work is a full fresh stdlib
    // semantic analysis, and the macOS/ARM64 CI runners are slow and contended enough to spike
    // several-fold. A true algorithmic regression (exponential in nesting) would run for minutes or
    // hang, so a 2-minute cap still surfaces it while tolerating runner variance.
    [Fact(Timeout = 120_000)]
    public async Task Codegen_DeeplyNestedSource_GeneratesRoutineDefinitionAsync()
    {
        // Task.Run (not a custom large-stack thread): depth 32 fits a default stack on every
        // platform, and a 64 MB-stack thread is pathologically slow to set up on macOS (eager stack
        // commit), which previously turned a passing 6 s test into a 30 s+ timeout. Task.Run also
        // keeps xUnit's Timeout effective on the async body.
        string llvmIr = await Task.Run(function: () =>
            GenerateIr(source: CreateDeeplyNestedSource(nestingDepth: 32)));

        Assert.Contains(expectedSubstring: TestRoutineIrSignature, actualString: llvmIr);
    }

    private static string GenerateIr(string source)
    {
        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge,
            buildMode: RfBuildMode.ReleaseSpace);
        AnalysisResult result = analyzer.Analyze(program: program);
        Assert.Empty(collection: result.Errors);

        var generator = new LlvmCodeGenerator(program: program,
            registry: result.Registry,
            options: new LlvmCodeGeneratorOptions
            {
                StdlibPrograms = result.Registry.StdlibPrograms,
                BuildMode = RfBuildMode.ReleaseSpace,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies
            });

        return generator.Generate();
    }

    private static string CreateDeeplyNestedSource(int nestingDepth)
    {
        var lines = new List<string> { "routine test()" };

        for (int depth = 0; depth < nestingDepth; depth += 1)
        {
            lines.Add(item: $"{new string(c: ' ', count: (depth + 1) * 2)}if true");
        }

        lines.Add(item: $"{new string(c: ' ', count: (nestingDepth + 1) * 2)}pass");
        lines.Add(item: "  return");

        return string.Join(separator: "\n", values: lines);
    }
}
