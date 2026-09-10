using System.Text;
using Builder;
using Builder.Execution;

namespace RazorForge.Tests.Execution;

/// <summary>
/// Drives the compiler CLI (<c>src/Execution/Program.cs</c>) in-process via <see cref="Program.Main"/> for
/// the non-spawning verbs — <c>version</c>, <c>help</c>, <c>tokenize</c>, <c>parse</c>, <c>codegen</c>,
/// <c>check</c>, <c>validate-stdlib</c> — plus the arg-handling and error branches. These verbs run entirely
/// in the test host (no clang/opt/link subprocess), so they are coverage-instrumented; <c>build</c> /
/// <c>buildandrun</c> are intentionally excluded (they spawn a native toolchain in an uninstrumented child).
///
/// All tests share one class (xUnit runs a class's tests sequentially) because <see cref="Program.Main"/>
/// mutates process-global console encoding and the tests redirect <see cref="Console.Out"/>.
/// </summary>
public sealed class ProgramCliTests : IDisposable
{
    private readonly string _dir;
    private readonly string _rf;

    public ProgramCliTests()
    {
        _dir = Path.Combine(path1: Path.GetTempPath(),
            path2: "rf-cli-tests-" + Guid.NewGuid().ToString(format: "N"));
        Directory.CreateDirectory(path: _dir);
        _rf = Path.Combine(path1: _dir, path2: "sample.rf");
        File.WriteAllText(path: _rf,
            contents:
            "module Test/Cli\n" +
            "import IO/Console\n" +
            "\n" +
            "routine sum_ints(a: S32, b: S32) -> S32\n" +
            "  return a + b\n" +
            "\n" +
            "routine start()\n" +
            "  var total = sum_ints(a: 1_s32, b: 2_s32)\n" +
            "  show(f\"{total}\")\n" +
            "  return\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(path: _dir, recursive: true); }
        catch
        {
            /* best-effort temp cleanup */
        }
    }

    /// <summary>Invokes <see cref="Program.Main"/> with console output captured; returns (exitCode, stdout).</summary>
    private static (int Code, string Out) RunMain(params string[] args)
    {
        TextWriter savedOut = Console.Out;
        Encoding savedOutEnc = Console.OutputEncoding;
        Encoding savedInEnc = Console.InputEncoding;
        var sw = new StringWriter();
        try
        {
            Console.SetOut(newOut: sw);
            int code = Program.Main(args: args);
            return (code, sw.ToString());
        }
        finally
        {
            Console.SetOut(newOut: savedOut);
            // Main forces UTF-8 on the console encodings; restore so we don't perturb other tests.
            try
            {
                Console.OutputEncoding = savedOutEnc;
                Console.InputEncoding = savedInEnc;
            }
            catch
            {
                /* encoding restore is best-effort (redirected consoles reject it) */
            }
        }
    }

    [Fact]
    public void NoArgs_PrintsUsage_ReturnsZero()
    {
        (int code, string outp) = RunMain();
        Assert.Equal(expected: 0, actual: code);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: outp));
    }

    [Fact]
    public void Version_PrintsVersion_ReturnsZero()
    {
        (int code, string outp) = RunMain("version");
        Assert.Equal(expected: 0, actual: code);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: outp));
    }

    [Fact]
    public void Help_PrintsUsage_ReturnsZero()
    {
        (int code, string outp) = RunMain("help");
        Assert.Equal(expected: 0, actual: code);
        Assert.False(condition: string.IsNullOrWhiteSpace(value: outp));
    }

    [Fact]
    public void Tokenize_ValidFile_ReturnsZero()
    {
        (int code, _) = RunMain("tokenize", _rf);
        Assert.Equal(expected: 0, actual: code);
    }

    [Fact]
    public void Parse_ValidFile_ReturnsZero()
    {
        (int code, _) = RunMain("parse", _rf);
        Assert.Equal(expected: 0, actual: code);
    }

    [Fact]
    public void BareFile_DefaultsToParse_ReturnsZero()
    {
        (int code, _) = RunMain(_rf);
        Assert.Equal(expected: 0, actual: code);
    }

    [Fact]
    public void Codegen_ValidFile_WritesIrAndReturnsZero()
    {
        string outLl = Path.Combine(path1: _dir, path2: "sample.ll");
        (int code, _) = RunMain("codegen", _rf, outLl);
        Assert.Equal(expected: 0, actual: code);
        Assert.True(condition: File.Exists(path: outLl), userMessage: "codegen should write the .ll file");
        Assert.False(condition: string.IsNullOrWhiteSpace(value: File.ReadAllText(path: outLl)));
    }

    [Fact]
    public void Check_ValidFile_ReturnsZero()
    {
        (int code, _) = RunMain("check", _rf);
        Assert.Equal(expected: 0, actual: code);
    }

    [Fact]
    public void ValidateStdlib_RazorForge_ReturnsZero()
    {
        (int code, _) = RunMain("validate-stdlib", "rf");
        Assert.Equal(expected: 0, actual: code);
    }

    [Fact]
    public void Tokenize_MissingPathArg_ReturnsError()
    {
        (int code, _) = RunMain("tokenize");
        Assert.NotEqual(expected: 0, actual: code);
    }

    [Fact]
    public void Parse_MissingPathArg_ReturnsError()
    {
        (int code, _) = RunMain("parse");
        Assert.NotEqual(expected: 0, actual: code);
    }

    [Fact]
    public void Check_SourceWithError_ReturnsNonZero()
    {
        string bad = Path.Combine(path1: _dir, path2: "bad.rf");
        File.WriteAllText(path: bad,
            contents:
            "module Test/Bad\n" +
            "routine start()\n" +
            "  var x = undefined_symbol_here\n" +
            "  return\n");
        (int code, _) = RunMain("check", bad);
        Assert.NotEqual(expected: 0, actual: code);
    }
}
