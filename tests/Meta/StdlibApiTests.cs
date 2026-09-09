using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Runs each <c>tests/Fixtures/Stdlib/*.rf</c> fixture through <c>buildandrun</c> and
/// diffs its stdout against the sibling <c>.expected.txt</c> snapshot. Provides
/// end-to-end behavior coverage for stdlib types/routines that <c>validate-stdlib</c>
/// (parse-and-typecheck only) does not catch.
/// </summary>
public sealed partial class StdlibApiTests
{
    private static readonly string RepoRoot = LocateRepoRoot();

    private static readonly string FixturesDir = Path.Combine(RepoRoot,
        "tests",
        "Fixtures",
        "Stdlib");

    private static readonly string CompilerDll =
        Path.Combine(AppContext.BaseDirectory, "RazorForge.dll");

    // The per-fixture `Fixture_OutputMatchesExpected` [Theory] (one full-stdlib compile per fixture,
    // ~165× ≈ 15 min, load-flaky) was replaced by the single-compile harness below.
    private static readonly string HarnessDir =
        Path.Combine(RepoRoot, "tests", "Fixtures", "StdlibHarness");

    private const string HarnessModule = "StdlibHarness";

    // Suflae (.sf) sibling harness. SF fixtures live under a DISTINCT namespace prefix
    // (Tests/StdlibSf/*) and their own fixtures dir so the SF bundle's prefix import pulls only `.sf`
    // files — a shared prefix would drag `.rf` fixtures into the SF project and trip BuildDriver's
    // cross-language import guard. SF reuses the RazorForge stdlib wholesale (SF's Core IS RF's Core),
    // so the ONLY difference here is the fixtures dir, entry extension, module name, and library path.
    private static readonly string SuflaeFixturesDir = Path.Combine(RepoRoot,
        "tests",
        "Fixtures",
        "StdlibSf");

    private static readonly string SuflaeHarnessDir =
        Path.Combine(RepoRoot, "tests", "Fixtures", "SuflaeHarness");

    private const string SuflaeHarnessModule = "SuflaeHarness";

    [GeneratedRegex(@"^\s*module\s+(\S+)\s*$")]
    private static partial Regex ModuleRe();

    [GeneratedRegex(@"error\[RF-|Warning:|Codegen bug|Synthesized body codegen failed|Unresolved generic|Error type found|undefined symbol|never defined|MARKER-LEAK|Unhandled exception|\bE0\d")]
    private static partial Regex StderrDiagnosticRe();

    /// <summary>
    /// Single-compile stdlib e2e test: generates <c>all_stdlib.rf</c> + <c>razorforge.toml</c> from
    /// every <c>Stdlib/*.rf</c> fixture (importing each by its declared module and calling its
    /// <c>start()</c> via the module leaf), compiles + runs the ONE program, splits the combined
    /// stdout on the <c>##### fixture #####</c> delimiters, and diffs each section against its
    /// <c>.expected.txt</c>. Compiles the stdlib + all fixtures ONCE instead of ~165 times.
    /// </summary>
    [Fact]
    public void StdlibHarness_AllFixturesOutputMatchExpected()
    {
        int fixtureCount = RunFixtureBundle(fixturesDir: FixturesDir, glob: "*.rf", harnessDir: HarnessDir,
            harnessModule: HarnessModule, bundleFileName: "all_stdlib.rf",
            packageName: "stdlib-harness", libraryRel: "../Stdlib");
        // Guard against a silently-empty bundle passing vacuously (e.g. fixtures dir moved/renamed).
        Assert.True(fixtureCount > 0, "No RazorForge stdlib fixtures were discovered — the harness ran nothing.");
    }

    /// <summary>
    /// Suflae (.sf) sibling of the stdlib harness. Bundles every <c>StdlibSf/*.sf</c> fixture into one
    /// <c>all_suflae.sf</c> (an SF project — entry extension picks the language), compiles + runs it
    /// ONCE over the shared RazorForge stdlib, and diffs each fixture's section against its snapshot.
    /// Proves SF programs behave correctly on the borrowed RF Core; where a <c>StdlibSf</c> fixture
    /// shares a stem with a <c>Stdlib</c> fixture, the equivalence test below locks their snapshots
    /// identical so the two frontends are held to the same observable behavior.
    /// </summary>
    [Fact]
    public void SuflaeHarness_AllFixturesOutputMatchExpected()
    {
        if (!Directory.Exists(SuflaeFixturesDir) ||
            !Directory.EnumerateFiles(SuflaeFixturesDir, "*.sf").Any())
        {
            return; // No SF fixtures authored yet — nothing to assert.
        }

        int fixtureCount = RunFixtureBundle(fixturesDir: SuflaeFixturesDir, glob: "*.sf",
            harnessDir: SuflaeHarnessDir, harnessModule: SuflaeHarnessModule,
            bundleFileName: "all_suflae.sf", packageName: "suflae-harness", libraryRel: "../StdlibSf");
        // The early return above guarantees at least one .sf fixture; confirm the bundle actually ran it.
        Assert.True(fixtureCount > 0, "SF fixtures exist but the harness discovered none to run.");
    }

    /// <summary>
    /// Equivalence lock: every fixture stem present in BOTH <c>Stdlib</c> (.rf) and <c>StdlibSf</c>
    /// (.sf) must have byte-identical (normalized) expected output. This is what makes the SF fixture
    /// an equivalence test rather than a standalone one — porting a .rf fixture to .sf and keeping the
    /// same snapshot asserts the two frontends produce the SAME observable behavior over the shared
    /// stdlib. If the SF output legitimately differs (e.g. a genuinely SF-only surface), give that
    /// fixture a stem with no .rf counterpart so it is exempt.
    /// </summary>
    [Fact]
    public void SuflaeAndRazorForge_SharedFixtures_HaveIdenticalExpected()
    {
        if (!Directory.Exists(SuflaeFixturesDir)) return;

        var mismatches = new List<string>();
        foreach (string sfExpected in Directory.EnumerateFiles(SuflaeFixturesDir, "*.expected.txt"))
        {
            string stem = Path.GetFileName(sfExpected);
            string rfExpected = Path.Combine(FixturesDir, stem);
            if (!File.Exists(rfExpected)) continue; // SF-only fixture — exempt.

            // Canonicalize the namespace before comparing. SF fixtures live under `Tests/StdlibSf/*`
            // (their own namespace so the SF bundle's prefix import pulls only `.sf` files), so any
            // fixture that prints its own fully-qualified type name (via `diagnose` / `:?`) emits
            // `Tests/StdlibSf/...` where its `.rf` twin emits `Tests/Stdlib/...`. That's a naming
            // artifact of the split, NOT a behavioral difference — fold it out so equivalence tracks
            // real observable behavior only.
            static string Canon(string s) => NormalizeForCompare(s).Replace("Tests/StdlibSf", "Tests/Stdlib");
            if (Canon(File.ReadAllText(rfExpected)) != Canon(File.ReadAllText(sfExpected)))
            {
                mismatches.Add(stem);
            }
        }

        Assert.True(mismatches.Count == 0,
            "Guarded RF/SF fixtures have divergent expected output (equivalence broken): " +
            string.Join(", ", mismatches) +
            ". Reconcile the snapshots, or rename the SF fixture stem to exempt it.");
    }

    /// <summary>
    /// Generates a single-program harness from every fixture in <paramref name="fixturesDir"/> matching
    /// <paramref name="glob"/> (importing each by its declared module, calling its <c>start()</c> via the
    /// module leaf), compiles + runs the ONE program, splits combined stdout on the
    /// <c>##### stem #####</c> delimiters, and diffs each section against its <c>.expected.txt</c>. Guarded
    /// by the RazorForge (.rf) and Suflae (.sf) harnesses — the language is selected by the entry file's
    /// extension, so the only per-language inputs are the fixtures dir, glob, module name, and paths.
    /// Returns the number of fixtures discovered and bundled (0 = nothing ran).
    /// </summary>
    private static int RunFixtureBundle(string fixturesDir, string glob, string harnessDir,
        string harnessModule, string bundleFileName, string packageName, string libraryRel)
    {
        // 1) Generate the harness program + manifest from the fixtures.
        var entries = new List<(string Stem, string Module, string Leaf)>();
        var leaves = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach (string src in Directory.EnumerateFiles(fixturesDir, glob)
                                       .OrderBy(keySelector: p => p, comparer: StringComparer.Ordinal))
        {
            string? module = ReadDeclaredModule(rfPath: src);
            if (module == null) continue;
            string leaf = module.Contains('/') ? module[(module.LastIndexOf('/') + 1)..] : module;
            if (leaves.TryGetValue(leaf, out string? other))
            {
                throw new Xunit.Sdk.XunitException(
                    $"Harness leaf collision '{leaf}': {other} vs {module} — module-qualified leaf calls " +
                    "would be ambiguous (RF-S513). Rename one fixture's module leaf.");
            }
            leaves[leaf] = module;
            entries.Add((Path.GetFileNameWithoutExtension(src), module, leaf));
        }

        Directory.CreateDirectory(harnessDir);
        var lines = new List<string> { $"module {harnessModule}", "", "import IO/Console" };
        // Prefix/package import: a single `import Tests/Stdlib` pulls in every Tests/Stdlib/* fixture
        // module (all fixtures share that namespace), replacing one `import` line per fixture. Falls
        // back to per-module imports if the fixtures ever stop sharing a common namespace prefix.
        string commonPrefix = entries.Count > 0 && entries[0].Module.Contains('/')
            ? entries[0].Module[..entries[0].Module.LastIndexOf('/')]
            : "";
        if (commonPrefix.Length > 0 &&
            entries.All(e => e.Module.StartsWith(commonPrefix + "/", StringComparison.Ordinal)))
            lines.Add($"import {commonPrefix}");
        else
            lines.AddRange(entries.Select(selector: e => $"import {e.Module}"));
        lines.Add("");
        lines.Add("routine start()");
        foreach ((string stem, _, string leaf) in entries)
        {
            lines.Add($"  show(\"##### {stem} #####\")");
            lines.Add($"  {leaf}.start()");
        }
        lines.Add("  return");
        lines.Add("");
        string harnessSrc = Path.Combine(harnessDir, bundleFileName);
        File.WriteAllText(harnessSrc, string.Join("\n", lines));

        string manifest =
            $"[package]\nname = \"{packageName}\"\nversion = \"0.0.1\"\nrazorforge-version = \"0.1.0\"\n\n" +
            $"[target]\nexecutable = \"{harnessModule}\"\nmode = \"debug\"\nlibrary = [\"{libraryRel}\"]\n";
        File.WriteAllText(Path.Combine(harnessDir, "config.toml"), manifest);

        // 2) Compile + run the ONE program (cwd = repo root so relative resource paths resolve).
        FixtureRun run = RunHarness(harnessRf: harnessSrc);
        Assert.True(run is { ExitCode: 0, TimedOut: false },
            $"Harness buildandrun failed (exit={run.ExitCode}, timedOut={run.TimedOut}).\n" +
            $"--- stdout ---\n{run.Stdout}\n--- stderr ---\n{run.Stderr}");

        // 2b) stderr must be CLEAN. The build's own progress banners go to stdout; anything on stderr is
        // a diagnostic — a compiler warning/error or a runtime fault. These are silently swallowed if we
        // only check stdout+exit (that is exactly how a flood of "Synthesized body codegen failed" /
        // "Unresolved generic memberRoutine 'Core.Dict.create'" warnings hid for so long). Fail on any of them.
        string[] offending = run.Stderr
            .Split('\n')
            .Select(selector: l => l.TrimEnd('\r'))
            .Where(predicate: l => StderrDiagnosticRe().IsMatch(l))
            .ToArray();
        Assert.True(offending.Length == 0,
            $"Harness stderr was not clean — {offending.Length} diagnostic line(s):\n" +
            string.Join("\n", offending.Take(40)));

        // 3) Split combined output on the delimiter lines.
        Dictionary<string, string> sections = SplitHarnessOutput(stdout: run.Stdout);

        // 4) Compare each fixture's section to its snapshot.
        var mismatches = new List<string>();
        foreach ((string stem, _, _) in entries)
        {
            string expectedPath = Path.Combine(fixturesDir, $"{stem}.expected.txt");
            if (!File.Exists(expectedPath)) continue;
            string expected = NormalizeForCompare(s: File.ReadAllText(expectedPath));
            string actual = NormalizeForCompare(s: sections.GetValueOrDefault(stem, "<no output section emitted>"));
            if (expected != actual) mismatches.Add(item: stem);
        }

        if (mismatches.Count > 0)
        {
            // Surface the first mismatch in full for a quick read.
            string first = mismatches[0];
            AssertOutputEqual(fixtureName: first,
                expected: NormalizeForCompare(s: File.ReadAllText(Path.Combine(fixturesDir, $"{first}.expected.txt"))),
                actual: NormalizeForCompare(s: sections.GetValueOrDefault(first, "<no output section emitted>")));
            throw new Xunit.Sdk.XunitException(
                $"{mismatches.Count} harness fixture(s) mismatched: {string.Join(", ", mismatches)}");
        }

        return entries.Count;
    }

    /// <summary>Reads the declared <c>module</c> path of a fixture (utf-8-sig for BOM), or null.</summary>
    private static string? ReadDeclaredModule(string rfPath)
    {
        foreach (string line in File.ReadLines(rfPath))
        {
            Match m = ModuleRe().Match(line);
            if (m.Success) return m.Groups[1].Value;
            string stripped = line.Trim();
            if (stripped.Length > 0 && !stripped.StartsWith('#')) return null;
        }
        return null;
    }

    /// <summary>Splits harness stdout into per-fixture sections keyed by the <c>##### stem #####</c> delimiters.</summary>
    private static Dictionary<string, string> SplitHarnessOutput(string stdout)
    {
        var sections = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        string? current = null;
        var buf = new StringBuilder();
        foreach (string rawLine in NormalizeNewlines(s: stdout).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.StartsWith("##### ") && line.EndsWith(" #####"))
            {
                if (current != null) sections[current] = buf.ToString();
                current = line[6..^6].Trim();
                buf.Clear();
                continue;
            }
            if (current != null) buf.Append(rawLine).Append('\n');
        }
        if (current != null) sections[current] = buf.ToString();
        return sections;
    }

    /// <summary>Runs <c>buildandrun</c> on the harness program with the harness dir as cwd.</summary>
    private static FixtureRun RunHarness(string harnessRf)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", harnessRf },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // The compiled program writes UTF-8 (e.g. an em-dash in a fixture string). Without pinning
            // the capture encoding, Windows decodes the pipe as the OEM codepage and mangles non-ASCII
            // to '?', producing a spurious mismatch against the UTF-8 .expected.txt.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            // Run from the repo root so fixtures that read repo-relative resource paths (e.g.
            // coro_async_read_api reads "tests/Fixtures/Stdlib/…") resolve. The manifest is still
            // found: buildandrun locates razorforge.toml by walking UP from the entry file's
            // directory (HarnessDir), not from the working directory.
            WorkingDirectory = RepoRoot,
            Environment = { ["DOTNET_gcServer"] = "0", ["DOTNET_GCConserveMemory"] = "9" }
        };
        using var p = Process.Start(psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 300_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return new FixtureRun(ExitCode: -1, Stdout: outTask.Result, Stderr: errTask.Result,
                TimedOut: true, TimeoutMs: timeoutMs);
        }
        return new FixtureRun(ExitCode: p.ExitCode, Stdout: outTask.Result, Stderr: errTask.Result,
            TimedOut: false, TimeoutMs: timeoutMs);
    }

    /// <summary>
    /// Compares fixture output and, on mismatch, throws with the COMPLETE expected and actual
    /// text (xUnit's <c>Assert.Equal</c> truncates long strings with <c>···</c>, which hides the
    /// real divergence). Also pinpoints the first differing line for a quick read.
    /// </summary>
    private static void AssertOutputEqual(string fixtureName, string expected, string actual)
    {
        if (expected == actual)
        {
            return;
        }

        string[] expLines = expected.Split('\n');
        string[] actLines = actual.Split('\n');
        var sb = new StringBuilder();
        sb.AppendLine($"Fixture output mismatch: {fixtureName}");

        int max = Math.Max(expLines.Length, actLines.Length);
        for (int i = 0; i < max; i++)
        {
            string e = i < expLines.Length
                ? expLines[i]
                : "<missing line>";
            string a = i < actLines.Length
                ? actLines[i]
                : "<missing line>";
            if (e == a)
            {
                continue;
            }

            sb.AppendLine($"First difference at line {i + 1}:");
            sb.AppendLine($"  expected: {e}");
            sb.AppendLine($"  actual:   {a}");
            break;
        }

        sb.AppendLine($"({expLines.Length} expected lines, {actLines.Length} actual lines)");
        sb.AppendLine("================= FULL EXPECTED =================");
        sb.AppendLine(expected);
        sb.AppendLine("================= FULL ACTUAL ===================");
        sb.AppendLine(actual);
        sb.AppendLine("================================================");
        throw new Xunit.Sdk.XunitException(sb.ToString());
    }

    /// <summary>
    /// Builds and runs a fixture, returning its stdout. Retries a SPURIOUS external kill — a
    /// `buildandrun` process terminated mid-compile by the environment (SIGTERM/SIGKILL under
    /// transient memory/scheduling pressure during a long sequential run), recognisable by a
    /// non-zero exit with NO stdout AND NO stderr (no compiler diagnostic, no program output).
    /// Genuine failures are NOT retried: a real compile error prints diagnostics to stderr and a
    /// real runtime crash prints output / a trace, so they carry output and are deterministic — the
    /// retry gate (empty output) never matches them, so a broken fixture still fails on attempt 1.
    /// </summary>
    private const int MaxRunAttempts = 2;

    private static string RunFixture(string rfPath)
    {
        string fixture = Path.GetFileName(rfPath);
        FixtureRun last = default;

        for (int attempt = 1; attempt <= MaxRunAttempts; attempt++)
        {
            last = RunFixtureOnce(rfPath);

            if (last.TimedOut)
            {
                // A hang is a real defect, never an environmental blip — surface it immediately.
                throw new Xunit.Sdk.XunitException(
                    $"buildandrun timed out after {last.TimeoutMs / 1000}s for {fixture}.\n" +
                    $"--- stdout (partial) ---\n{last.Stdout}\n--- stderr (partial) ---\n{last.Stderr}");
            }

            if (last.ExitCode == 0)
            {
                return last.Stdout;
            }

            bool spuriousKill = last.Stdout.Length == 0 && last.Stderr.Length == 0;
            if (!spuriousKill)
            {
                // Real failure (compile diagnostic or runtime output present) — fail fast.
                throw new Xunit.Sdk.XunitException(
                    $"buildandrun failed for {fixture} (exit={last.ExitCode}).\n" +
                    $"--- stdout ---\n{last.Stdout}\n--- stderr ---\n{last.Stderr}");
            }

            // Silent external kill: log and retry — but only after a SUBSTANTIAL backoff. The kill
            // came from a transient memory spike; the killed process's pages are already reclaimed,
            // and waiting several seconds lets the spike fully clear before we re-spawn a heavy
            // compile. (A short backoff re-spawns INTO the live pressure and can tip the test host
            // over too — so the wait is load-relieving, not cosmetic.)
            Console.Error.WriteLine(
                $"[StdlibApiTests] {fixture}: spurious kill (exit={last.ExitCode}, no output) " +
                $"on attempt {attempt}/{MaxRunAttempts}; backing off then retrying.");
            if (attempt >= MaxRunAttempts)
            {
                continue;
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            Thread.Sleep(4000);
        }

        throw new Xunit.Sdk.XunitException(
            $"buildandrun for {fixture} was killed with no output (exit={last.ExitCode}) on all " +
            $"{MaxRunAttempts} attempts — likely sustained environmental resource pressure, not a " +
            $"fixture failure (it produced no compiler diagnostic and no program output).");
    }

    private readonly record struct FixtureRun(
        int ExitCode,
        string Stdout,
        string Stderr,
        bool TimedOut,
        int TimeoutMs);

    private static FixtureRun RunFixtureOnce(string rfPath)
    {
        // Invoke the already-built compiler assembly directly. Using
        // `dotnet run --project …` here would re-evaluate the project file on
        // every fixture (~1-3s of SDK startup × ~150 fixtures = several minutes
        // of pure overhead).
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", rfPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = RepoRoot,
            // Each buildandrun child compiles the entire (~180-file) stdlib and runs opt -O2/-O3 on a
            // large module — the heaviest memory user in the e2e suite. By default .NET uses SERVER GC,
            // which reserves a managed heap PER CORE; multiplied across ~150 sequential children plus the
            // test host, that peak exhausts memory on constrained machines and the OS OOM-kills processes
            // across the tree (the "exit=143, no output" spurious kills, and occasionally the test host
            // itself). Force WORKSTATION GC + aggressive memory conservation on the child to slash its
            // footprint; it stays single-fixture sequential, so the small GC-throughput trade is invisible.
            Environment = { ["DOTNET_gcServer"] = "0", ["DOTNET_GCConserveMemory"] = "9" }
        };
        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 60_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); }
            catch { }

            return new FixtureRun(ExitCode: -1,
                Stdout: stdoutTask.Result,
                Stderr: stderrTask.Result,
                TimedOut: true,
                TimeoutMs: timeoutMs);
        }

        return new FixtureRun(ExitCode: p.ExitCode,
            Stdout: stdoutTask.Result,
            Stderr: stderrTask.Result,
            TimedOut: false,
            TimeoutMs: timeoutMs);
    }

    private static string NormalizeNewlines(string s) =>
        s.Replace("\r\n", "\n")
         .Replace("\r", "\n");

    /// <summary>
    /// Normalizes output for snapshot comparison: LF line endings (a snapshot checked out as CRLF on
    /// Windows must still match LF program output), trailing blank lines removed, and PER-LINE trailing
    /// whitespace stripped. The last is essential — editors trim trailing spaces on save, so a snapshot
    /// can't reliably hold them, yet programs legitimately emit them (e.g. <c>out + f"{x} "</c>).
    /// Trailing whitespace is never semantically meaningful for these stdlib fixtures, so trimming both
    /// sides avoids false mismatches while still catching every real content difference.
    /// </summary>
    private static string NormalizeForCompare(string s) =>
        string.Join("\n",
            NormalizeNewlines(s)
               .TrimEnd('\n')
               .Split('\n')
               .Select(line => line.TrimEnd()));

    private static string LocateRepoRoot()
    {
        // Walk up from the test assembly until we find RazorForge.csproj.
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
