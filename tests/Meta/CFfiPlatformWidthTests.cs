using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RazorForge.Tests.Meta;

/// <summary>
/// End-to-end proof that the <c>@target(os: …)</c>-gated C FFI scalar/string wrappers
/// (<c>CLong</c>/<c>CULong</c>/<c>CWChar</c>/<c>CWStr</c>) actually take their platform width at runtime:
/// LP64 on Linux/macOS (<c>long</c>/<c>unsigned long</c> = 64-bit, <c>wchar_t</c> = 32-bit) vs LLP64 on
/// Windows (<c>long</c>/<c>unsigned long</c> = 32-bit, <c>wchar_t</c> = 16-bit).
///
/// The stdlib fixture harness (<see cref="StdlibApiTests"/>) diffs one OS-independent <c>.expected.txt</c>
/// per fixture, so it CANNOT express a value that differs across platforms — a divergent fixture would
/// fail on whichever OS it wasn't authored for (the platform-INDEPENDENT surface is covered there by
/// <c>csubsystem_api.rf</c>). This suite fills that gap by keying BOTH the probe source and the expected
/// result on the host OS, exercising exactly the range where the two data models diverge:
///   • byte width of each wrapper (4/4/2 on Windows vs 8/8/4 on Unix);
///   • narrowing at the 32-/16-bit boundary — a value that fits Unix's wider type but overflows Windows'
///     narrower one loudly crashes on Windows and round-trips on Unix;
///   • a supplementary (&gt; U+FFFF) code unit stored in a <c>CWStr</c> — representable in Unix's 32-bit
///     <c>wchar_t</c>, unrepresentable in Windows' 16-bit one (the element is a platform-width
///     <c>CWChar</c>, NOT a fixed 32-bit code unit).
/// On each CI runner it asserts that runner's data model; together the runners cover both.
/// </summary>
public sealed class CFfiPlatformWidthTests
{
    private static readonly string CompilerDll =
        Path.Combine(AppContext.BaseDirectory, "RazorForge.dll");

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Byte width of each platform-dependent wrapper via the BuilderQuery `data_size()` intrinsic.</summary>
    [Fact]
    public void CFfiScalarWrappers_TakePlatformWidth()
    {
        const string source =
            """
            module Playground

            import IO/Console
            import BuilderQuery

            routine start()
              show(f"CLong={CLong.data_size().byte_size()}")
              show(f"CULong={CULong.data_size().byte_size()}")
              show(f"CWChar={CWChar.data_size().byte_size()}")
              return
            """;

        // LLP64 (Windows): long/unsigned long = 4 bytes, wchar_t = 2 bytes.
        // LP64  (Unix):    long/unsigned long = 8 bytes, wchar_t = 4 bytes.
        string expected = IsWindows
            ? "CLong=4\nCULong=4\nCWChar=2"
            : "CLong=8\nCULong=8\nCWChar=4";

        (int exitCode, string stdout, string stderr, bool timedOut) = RunProgram(source);
        Assert.True(exitCode == 0 && !timedOut,
            $"buildandrun failed (exit={exitCode}, timedOut={timedOut}).\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");

        string actual = KeepPrefixed(stdout, "CLong=", "CULong=", "CWChar=");
        Assert.True(actual == expected,
            $"Platform width mismatch on {PlatformName}.\nexpected:\n{expected}\nactual:\n{actual}");
    }

    // A value that fits the Unix (wider) type but overflows the Windows (narrower) one. On Unix the
    // checked narrowing succeeds and the widen round-trips the value; on Windows it throws
    // IntegerOverflowError and the program aborts. `widen(from: T(from: value))` is the whole probe.
    public static TheoryData<string, string, string, string> BoundaryCases() => new()
    {
        // ctorType, value literal, widen routine, expected decimal on Unix
        { "CLong", "2147483648s64", "S64", "2147483648" },  // 2^31 — 1 past Windows 32-bit signed long
        { "CULong", "4294967296u64", "U64", "4294967296" }, // 2^32 — 1 past Windows 32-bit unsigned long
        { "CWChar", "65536u32", "U32", "65536" },            // 2^16 — 1 past Windows 16-bit wchar_t
    };

    [Theory]
    [MemberData(nameof(BoundaryCases))]
    public void CFfiNarrowing_AtBoundary_DivergesByPlatform(string ctorType, string valueLiteral, string widen,
        string expectedUnixDecimal)
    {
        // Token-substitution template (a non-interpolated raw string) so the RF f-string braces need no
        // C# escaping. Emits e.g. `show(f"v={S64(from: CLong(from: 2147483648s64))}")`.
        const string template =
            """
            module Playground

            import IO/Console

            routine start()
              show(f"v={WIDEN(from: CTOR(from: VALUE))}")
              return
            """;
        string source = template
            .Replace("WIDEN", widen)
            .Replace("CTOR", ctorType)
            .Replace("VALUE", valueLiteral);

        (int exitCode, string stdout, string stderr, bool timedOut) = RunProgram(source);
        Assert.False(timedOut, $"buildandrun timed out for {ctorType} boundary probe.");

        if (IsWindows)
        {
            // The narrowing constructor is failable on LLP64; the bare call propagates the crash.
            Assert.True(exitCode != 0,
                $"Expected {ctorType}(from: {valueLiteral}) to overflow Windows' narrower type, but it exited 0.\n{stdout}\n{stderr}");
            string combined = stdout + "\n" + stderr;
            Assert.True(combined.Contains("IntegerOverflowError", StringComparison.Ordinal),
                $"Expected an IntegerOverflowError on Windows for {ctorType}(from: {valueLiteral}).\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        else
        {
            Assert.True(exitCode == 0,
                $"Expected {ctorType}(from: {valueLiteral}) to fit Unix' wider type, but it exited {exitCode}.\n{stdout}\n{stderr}");
            Assert.Equal($"v={expectedUnixDecimal}", KeepPrefixed(stdout, "v="));
        }
    }

    /// <summary>
    /// A <c>CWStr</c> element is a platform-width <c>CWChar</c>, not a fixed 32-bit code unit: a
    /// supplementary code point (U+10000) fits Unix' 32-bit <c>wchar_t</c> and round-trips through
    /// <c>getitem</c>, but is unrepresentable in Windows' 16-bit <c>wchar_t</c>, so building the element
    /// overflows and the program aborts.
    /// </summary>
    [Fact]
    public void CWStr_SupplementaryCodeUnit_DivergesByPlatform()
    {
        const string source =
            """
            module Playground

            import IO/Console
            import BuilderQuery

            routine start()
              danger
                var elem = CWChar.data_size().byte_size()
                var raw = Hijacked[Byte](from: C::rf_allocate_dynamic(2u64 * elem))
                var buf = raw.recast_as[CWChar]()
                buf.stride(count: 0s64).poke(value: CWChar(from: 65536u32))
                buf.stride(count: 1s64).poke(value: CWChar(from: 0u32))
                var s = CWStr(from_ptr: buf)
                show(f"cp={s.getitem(index: 0u64).codepoint()} count={s.count()}")
              return
            """;

        (int exitCode, string stdout, string stderr, bool timedOut) = RunProgram(source);
        Assert.False(timedOut, "buildandrun timed out for the CWStr supplementary probe.");

        if (IsWindows)
        {
            Assert.True(exitCode != 0,
                $"Expected U+10000 to be unrepresentable in Windows' 16-bit wchar_t, but it exited 0.\n{stdout}\n{stderr}");
            Assert.True((stdout + "\n" + stderr).Contains("IntegerOverflowError", StringComparison.Ordinal),
                $"Expected an IntegerOverflowError on Windows.\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        }
        else
        {
            Assert.True(exitCode == 0,
                $"Expected U+10000 to fit Unix' 32-bit wchar_t, but it exited {exitCode}.\n{stdout}\n{stderr}");
            Assert.Equal("cp=65536 count=1", KeepPrefixed(stdout, "cp="));
        }
    }

    private static string PlatformName => IsWindows ? "Windows (LLP64)" : "Unix (LP64)";

    /// <summary>Writes <paramref name="source"/> to an isolated temp dir and runs it through buildandrun.</summary>
    private static (int ExitCode, string Stdout, string Stderr, bool TimedOut) RunProgram(string source)
    {
        // Isolated dir: buildandrun compiles EVERY .rf in the entry file's directory, so a shared temp
        // dir would pull sibling probes in and collide on `routine start` (RF-S406).
        string tempDir = Path.Combine(Path.GetTempPath(), "rf-cffi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string probe = Path.Combine(tempDir, "probe.rf");
        try
        {
            File.WriteAllText(probe, source);
            return RunBuildAndRun(probe);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr, bool TimedOut) RunBuildAndRun(string rfPath)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { CompilerDll, "buildandrun", rfPath },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            Environment = { ["DOTNET_gcServer"] = "0", ["DOTNET_GCConserveMemory"] = "9" }
        };
        using var p = Process.Start(psi)!;
        Task<string> outTask = p.StandardOutput.ReadToEndAsync();
        Task<string> errTask = p.StandardError.ReadToEndAsync();
        const int timeoutMs = 120_000;
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (-1, outTask.Result, errTask.Result, true);
        }
        return (p.ExitCode, outTask.Result, errTask.Result, false);
    }

    /// <summary>Keeps only the stdout lines starting with one of <paramref name="prefixes"/>, newline-joined.</summary>
    private static string KeepPrefixed(string stdout, params string[] prefixes)
    {
        var kept = new List<string>();
        foreach (string raw in stdout.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'))
        {
            string line = raw.Trim();
            foreach (string prefix in prefixes)
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    kept.Add(line);
                    break;
                }
            }
        }
        return string.Join("\n", kept);
    }
}
