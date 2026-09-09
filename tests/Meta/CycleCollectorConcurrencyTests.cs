using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RazorForge.Tests.Meta;

/// <summary>
/// End-to-end regressions for the Roamed cycle collector's concurrency + deep-graph fixes. Each compiles
/// and runs a committed <c>tests/Fixtures/CycleCollector/*.rf</c> fixture via <c>buildandrun</c> and
/// asserts a clean finish (exit 0, success marker on stdout, clean stderr):
/// <list type="bullet">
///   <item><b>MultithreadedCollection_NoUseAfterFree</b> — concurrent collector and mutator OS threads
///   churn/collect Roamed cycles. A controller built on one thread is trial-deleted on another, so a
///   missing stop-the-world lock would race the non-atomic collector state and crash/hang (UAF). Reclaim
///   counts are non-deterministic (genuine interleaving), so this asserts COMPLETION, not an exact count —
///   a re-introduced race shows up as a crash/hang, at least intermittently.</item>
///   <item><b>DeepCycle_NoStackOverflow</b> — a 200k-deep cyclic ring must collect via the explicit
///   worklist passes, not the old recursion (which would overflow the stack at that depth). Deterministic:
///   asserts <c>reclaimed=200000</c>.</item>
/// </list>
/// </summary>
public sealed partial class CycleCollectorConcurrencyTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly string CompilerDll =
        Path.Combine(path1: AppContext.BaseDirectory, path2: "RazorForge.dll");

    private static readonly string FixturesDir = Path.Combine(path1: RepoRoot,
        path2: "tests",
        path3: "Fixtures",
        path4: "CycleCollector");

    [Fact]
    public void MultithreadedCollection_NoUseAfterFree()
    {
        (int exit, string stdout, string stderr, bool timedOut) =
            RunFixture(fixture: "mt_uaf_stress.rf", timeoutMs: 180_000);

        Assert.False(condition: timedOut,
            userMessage:
            $"mt_uaf_stress hung — a stop-the-world deadlock?\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.True(condition: exit == 0,
            userMessage:
            $"mt_uaf_stress exited {exit} — a concurrent use-after-free?\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.Contains(expectedSubstring: "DONE - no UAF", actualString: stdout);
        AssertCleanStderr(stderr: stderr);
    }

    [Fact]
    public void DeepCycle_NoStackOverflow()
    {
        (int exit, string stdout, string stderr, bool timedOut) =
            RunFixture(fixture: "deep_cycle.rf", timeoutMs: 120_000);

        Assert.False(condition: timedOut,
            userMessage: $"deep_cycle hung.\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.True(condition: exit == 0,
            userMessage:
            $"deep_cycle exited {exit} — a collector stack overflow at depth?\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.Contains(expectedSubstring: "reclaimed=200000", actualString: stdout);
        Assert.Contains(expectedSubstring: "DONE", actualString: stdout);
        AssertCleanStderr(stderr: stderr);
    }

    /// <summary>Fails on any compiler diagnostic or runtime fault on stderr (mirrors StdlibApiTests' gate),
    /// so a fault that somehow left a zero exit is still caught.</summary>
    [GeneratedRegex(
        pattern:
        @"error\[RF-|Codegen bug|Synthesized body codegen failed|Unresolved generic|undefined symbol|never defined|Unhandled exception|Segmentation|AccessViolation")]
    private static partial Regex StderrFaultPattern();

    private static void AssertCleanStderr(string stderr)
    {
        string[] offending = stderr.Split(separator: '\n')
                                   .Select(selector: l => l.TrimEnd(trimChar: '\r'))
                                   .Where(predicate: l => StderrFaultPattern()
                                       .IsMatch(input: l))
                                   .ToArray();
        Assert.True(condition: offending.Length == 0,
            userMessage: "Fixture stderr was not clean:\n" +
                         string.Join(separator: "\n", values: offending.Take(count: 40)));
    }

    private static (int Exit, string Stdout, string Stderr, bool TimedOut) RunFixture(
        string fixture, int timeoutMs)
    {
        string rfPath = Path.Combine(path1: FixturesDir, path2: fixture);
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", rfPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
            // Workstation GC + memory conservation, matching StdlibApiTests — each child compiles the whole
            // stdlib and opt-O2s a large module, the heaviest memory user in the suite.
            Environment =
            {
                [key: "DOTNET_gcServer"] = "0", [key: "DOTNET_GCConserveMemory"] = "9"
            }
        };
        using var p = Process.Start(startInfo: psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(milliseconds: timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); }
            catch
            {
                /* best effort */
            }

            return (-1, outTask.Result, errTask.Result, true);
        }

        return (p.ExitCode, outTask.Result, errTask.Result, false);
    }

    private static string LocateRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(value: dir))
        {
            if (File.Exists(path: Path.Combine(path1: dir, path2: "RazorForge.csproj")))
            {
                return dir;
            }

            string? parent = Path.GetDirectoryName(path: dir);
            if (parent == null || parent == dir)
            {
                break;
            }

            dir = parent;
        }

        throw new InvalidOperationException(
            message:
            "Could not locate RazorForge.csproj walking up from test assembly directory.");
    }
}
