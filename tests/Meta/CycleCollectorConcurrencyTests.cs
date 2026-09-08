using System.Diagnostics;
using System.Text;

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
public sealed class CycleCollectorConcurrencyTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly string CompilerDll =
        Path.Combine(AppContext.BaseDirectory, "RazorForge.dll");

    private static readonly string FixturesDir =
        Path.Combine(RepoRoot, "tests", "Fixtures", "CycleCollector");

    [Fact]
    public void MultithreadedCollection_NoUseAfterFree()
    {
        (int exit, string stdout, string stderr, bool timedOut) =
            RunFixture(fixture: "mt_uaf_stress.rf", timeoutMs: 180_000);

        Assert.False(timedOut,
            $"mt_uaf_stress hung — a stop-the-world deadlock?\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.True(exit == 0,
            $"mt_uaf_stress exited {exit} — a concurrent use-after-free?\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.Contains("DONE - no UAF", stdout);
        AssertCleanStderr(stderr: stderr);
    }

    [Fact]
    public void DeepCycle_NoStackOverflow()
    {
        (int exit, string stdout, string stderr, bool timedOut) =
            RunFixture(fixture: "deep_cycle.rf", timeoutMs: 120_000);

        Assert.False(timedOut,
            $"deep_cycle hung.\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.True(exit == 0,
            $"deep_cycle exited {exit} — a collector stack overflow at depth?\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        Assert.Contains("reclaimed=200000", stdout);
        Assert.Contains("DONE", stdout);
        AssertCleanStderr(stderr: stderr);
    }

    /// <summary>Fails on any compiler diagnostic or runtime fault on stderr (mirrors StdlibApiTests' gate),
    /// so a fault that somehow left a zero exit is still caught.</summary>
    private static void AssertCleanStderr(string stderr)
    {
        string[] offending = stderr
            .Split('\n')
            .Select(selector: l => l.TrimEnd('\r'))
            .Where(predicate: l => System.Text.RegularExpressions.Regex.IsMatch(l,
                @"error\[RF-|Codegen bug|Synthesized body codegen failed|Unresolved generic|undefined symbol|never defined|Unhandled exception|Segmentation|AccessViolation"))
            .ToArray();
        Assert.True(offending.Length == 0,
            "Fixture stderr was not clean:\n" + string.Join("\n", offending.Take(40)));
    }

    private static (int Exit, string Stdout, string Stderr, bool TimedOut) RunFixture(string fixture,
        int timeoutMs)
    {
        string rfPath = Path.Combine(FixturesDir, fixture);
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
            Environment = { ["DOTNET_gcServer"] = "0", ["DOTNET_GCConserveMemory"] = "9" }
        };
        using var p = Process.Start(startInfo: psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(milliseconds: timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            return (-1, outTask.Result, errTask.Result, true);
        }

        return (p.ExitCode, outTask.Result, errTask.Result, false);
    }

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

        throw new InvalidOperationException(
            "Could not locate RazorForge.csproj walking up from test assembly directory.");
    }
}
