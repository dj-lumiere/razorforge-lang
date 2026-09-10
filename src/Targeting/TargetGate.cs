namespace Builder.Targeting;

/// <summary>
/// File-granularity conditional compilation for RazorForge. A <c>.rf</c> file may carry a leading
/// <c>@target(...)</c> annotation; when its conditions do not match the build target, the file is
/// EXCLUDED from the compile set before it is ever parsed.
///
/// <para>This gate reads the <c>@target(...)</c> annotation PRE-PARSE, straight from the file's leading
/// lines — the decision to compile a file must happen before parsing it. In a file that IS selected the
/// parser sees the same <c>@target(...)</c> as a real annotation (consumed and discarded before
/// <c>module</c>); keeping it a genuine <c>@</c>-annotation rather than a comment is what gives it editor
/// highlighting. FILE granularity (a whole file is in or out) avoids an in-file preprocessor — those
/// read badly in an indentation-based language, where <c>#if</c> blocks fight the significant
/// whitespace. Platform-specific code is split into separate files (<c>foo_windows.rf</c> /
/// <c>foo_linux.rf</c>) each guarded by its own directive.</para>
///
/// <para>RazorForge-only: <c>.sf</c> files are never gated (Suflae targets a REPL, not AOT
/// cross-builds). The directive must appear BEFORE any real code (before <c>module</c>) — the scan
/// skips leading blank/comment lines and stops at the first real line.</para>
///
/// <para>Syntax: <c>@target(os: "windows")</c>, <c>@target(os: "linux", "macos")</c>,
/// <c>@target(os: "windows", arch: "arm64")</c>. Keys: <c>os</c> (windows/linux/macos), <c>arch</c>
/// (x64|x86_64, arm64|aarch64). Keys are AND-ed; comma-separated values within a key are OR-ed; unknown
/// keys are ignored for forward compatibility. The match is against a <see cref="TargetConfig"/> — the
/// host today, an explicit cross-target once cross-compilation selects one.</para>
/// </summary>
public static class TargetGate
{
    private static readonly TargetConfig HostTarget = TargetConfig.ForCurrentHost();

    /// <summary>
    /// Returns false only when <paramref name="filePath"/> is a <c>.rf</c> file whose leading
    /// <c>#@target(...)</c> directive does not match <paramref name="target"/> (defaults to the host).
    /// Non-<c>.rf</c> files and files with no directive always compile.
    /// </summary>
    public static bool ShouldCompile(string filePath, TargetConfig? target = null)
    {
        if (!filePath.EndsWith(value: ".rf", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? inside = ReadTargetDirective(filePath: filePath);
        return inside == null || Matches(inside: inside, target: target ?? HostTarget);
    }

    /// <summary>
    /// Scans the file's leading comment block for a <c>#@target(...)</c> directive and returns the text
    /// inside the parentheses, or null if none precedes the first real line. Doc comments (<c>###</c>)
    /// and ordinary comments are skipped; the first non-comment, non-blank line ends the scan.
    /// </summary>
    private static string? ReadTargetDirective(string filePath)
    {
        foreach (string raw in File.ReadLines(path: filePath))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue; // blank line
            }

            if (line.StartsWith(value: '#'))
            {
                continue; // comment / doc comment — keep scanning header
            }

            if (line.StartsWith(value: "@target(") && line.EndsWith(value: ')'))
            {
                return line["@target(".Length..^1];
            }

            return
                null; // first real line (e.g. `module`) — the `@target` directive must precede it
        }

        return null;
    }

    private static bool Matches(string inside, TargetConfig target)
    {
        // Parse `key: "v1", "v2", other: "v3"` into key -> [values]. A comma-separated part WITH a
        // colon starts a new key; a part WITHOUT one is an additional value for the current key — so
        // `os: "linux", "macos"` reads as os ∈ {linux, macos}. Keys are AND-ed; values within a key
        // are OR-ed.
        var conditions =
            new Dictionary<string, List<string>>(comparer: StringComparer.OrdinalIgnoreCase);
        string? currentKey = null;
        foreach (string rawPart in inside.Split(separator: ','))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
            {
                continue;
            }

            int colon = part.IndexOf(value: ':');
            if (colon >= 0)
            {
                currentKey = part[..colon]
                   .Trim();
                conditions[key: currentKey] =
                [
                    part[(colon + 1)..]
                       .Trim()
                       .Trim(trimChar: '"')
                ];
            }
            else if (currentKey != null)
            {
                conditions[key: currentKey]
                   .Add(item: part.Trim(trimChar: '"'));
            }
        }

        foreach ((string key, List<string> values) in conditions)
        {
            bool ok = key switch
            {
                "os" => values.Any(predicate: v => string.Equals(a: v,
                    b: target.TargetOS,
                    comparisonType: StringComparison.OrdinalIgnoreCase)),
                "arch" => values.Any(predicate: v => string.Equals(a: NormalizeArch(arch: v),
                    b: NormalizeArch(arch: target.TargetArch),
                    comparisonType: StringComparison.OrdinalIgnoreCase)),
                _ => true // unknown key: forward-compatible, ignore
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Canonicalizes an architecture spelling so the directive's ergonomic aliases
    /// (<c>x64</c>/<c>arm64</c>) match <see cref="TargetConfig.TargetArch"/>'s LLVM names
    /// (<c>x86_64</c>/<c>aarch64</c>).</summary>
    private static string NormalizeArch(string arch)
    {
        return arch.Trim()
                   .ToLowerInvariant() switch
        {
            "x64" or "amd64" or "x86_64" => "x86_64",
            "arm64" or "aarch64" => "aarch64",
            var other => other
        };
    }
}
