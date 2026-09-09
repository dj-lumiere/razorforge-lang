using System.Security.Cryptography;
using System.Text;
using TypeModel.Enums;
using Compiler.Verification;

namespace Compiler.Serialization;

/// <summary>
/// On-disk cache of the compiled-stdlib snapshot as a <c>.pbrf</c> ("prebuilt razorforge") file. This is
/// the "cold under 1 s" foundation: instead of re-analyzing the whole stdlib (~5–8 s) on every daemon
/// startup / cold build, we deserialize a previously-captured snapshot (~1–2 s). The cache is keyed by a
/// content hash of (all stdlib source files' path+size+mtime) + (the compiler assembly's mtime) +
/// (the .pbrf format version), so editing the stdlib OR rebuilding the compiler transparently invalidates
/// it — a stale snapshot can never be loaded against a changed semantic model.
/// </summary>
public static class StdlibSnapshotCache
{
    // Process-lifetime memo: repeated cold compiles in one process (e.g. a test run over many fixtures)
    // reuse ONE loaded snapshot instead of re-reading the 40 MB file each time. Reusing a single warm state
    // across compiles is proven safe by WarmCompile_Repeatable_FromSharedState_NoPoisoning.
    private static readonly System.Collections.Generic.Dictionary<Language, SemanticVerifier.CompiledStdlibState>
        _memo = new();

    /// <summary>Returns a compiled-stdlib snapshot, loading it from the on-disk <c>.pbrf</c> cache when the
    /// hash matches, else capturing it fresh and writing the cache for next time. Any load/save failure
    /// falls back to a fresh capture (the cache is an optimization, never a correctness dependency).</summary>
    public static SemanticVerifier.CompiledStdlibState LoadOrCapture(Language language,
        Action<string>? log = null)
    {
        lock (_memo)
            if (_memo.TryGetValue(key: language, value: out SemanticVerifier.CompiledStdlibState? memoized))
                return memoized;

        SemanticVerifier.CompiledStdlibState result = LoadOrCaptureUncached(language: language, log: log);
        lock (_memo) _memo[key: language] = result;
        return result;
    }

    private static SemanticVerifier.CompiledStdlibState LoadOrCaptureUncached(Language language,
        Action<string>? log)
    {
        // Prefer the MODULAR build-output artifacts (emitted by `emit-pbrf` as a dotnet-build byproduct) —
        // reassembled via the shell two-phase loader, guarded by a source-hash stamp so a stale set is never
        // loaded. This is the "cold under 1 s" path: no capture, no monolithic 40 MB read.
        if (TryLoadModular(language: language, log: log) is { } modular) return modular;

        string? path = CachePath(language: language);
        if (TryLoadMonolithic(language: language, path: path, log: log) is { } cached) return cached;

        var swCap = System.Diagnostics.Stopwatch.StartNew();
        SemanticVerifier.CompiledStdlibState fresh = SemanticVerifier.CaptureCompiledStdlib(language: language);
        log?.Invoke($"captured {language} stdlib fresh ({swCap.ElapsedMilliseconds} ms)");

        SaveMonolithic(path: path, fresh: fresh, log: log);
        return fresh;
    }

    /// <summary>Loads the monolithic <c>.pbrf</c> snapshot from <paramref name="path"/> when it exists; returns
    /// null on absence or any read/deserialize error (→ caller recaptures). The whole file is read into memory
    /// first, then deserialized: deserializing straight off a FileStream does many tiny reads (syscall per
    /// BinaryReader call) and is ~1.7× slower than a MemoryStream. (Deflate compression was tried and REMOVED
    /// — after string interning the remaining graph data is high-entropy, so it barely shrank the file but
    /// added ~0.8 s of decompress overhead: a net loss.)</summary>
    private static SemanticVerifier.CompiledStdlibState? TryLoadMonolithic(Language language, string? path,
        Action<string>? log)
    {
        try
        {
            if (path != null && File.Exists(path: path))
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var ms = new MemoryStream(buffer: File.ReadAllBytes(path: path), writable: false);
                var state = PbrfSerializer.Deserialize<SemanticVerifier.CompiledStdlibState>(stream: ms);
                log?.Invoke($"loaded {language} stdlib from {Path.GetFileName(path: path)} ({sw.ElapsedMilliseconds} ms)");
                return state;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"pbrf load failed ({ex.Message}); recapturing");
        }
        return null;
    }

    /// <summary>Writes <paramref name="fresh"/> to the monolithic <c>.pbrf</c> at <paramref name="path"/>
    /// (via a temp file + atomic move). Best-effort: any failure is logged and swallowed (the cache is an
    /// optimization, never a correctness dependency). No-op when <paramref name="path"/> is null.</summary>
    private static void SaveMonolithic(string? path, SemanticVerifier.CompiledStdlibState fresh,
        Action<string>? log)
    {
        if (path == null) return;
        try
        {
            Directory.CreateDirectory(path: Path.GetDirectoryName(path: path)!);
            string tmp = path + ".tmp";
            using (FileStream fs = File.Create(path: tmp))
            using (var buffered = new BufferedStream(stream: fs, bufferSize: 1 << 20))
                PbrfSerializer.Serialize(stream: buffered, root: fresh);
            File.Move(sourceFileName: tmp, destFileName: path, overwrite: true);
            log?.Invoke($"wrote {Path.GetFileName(path: path)}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"pbrf save failed ({ex.Message})");
        }
    }

    /// <summary>The build-output modular-artifact directory for a language: <c>&lt;stdlib&gt;/.pbrf/&lt;Lang&gt;/</c>.</summary>
    private static string ModularDir(Language language) => Path.Combine(
        path1: Compiler.Declaration.StdlibLoader.GetDefaultStdlibPath(), path2: ".pbrf", path3: language.ToString());

    /// <summary>Loads the modular build-output artifacts when present and their stamp matches the current
    /// stdlib+compiler hash; returns null (→ caller falls back) on absence, hash mismatch, or any error.</summary>
    private static SemanticVerifier.CompiledStdlibState? TryLoadModular(Language language, Action<string>? log)
    {
        try
        {
            string dir = ModularDir(language: language);
            string stampPath = Path.Combine(path1: dir, path2: "stamp.txt");
            string indexPath = Path.Combine(path1: dir, path2: "index.pbrf");
            if (!File.Exists(path: stampPath) || !File.Exists(path: indexPath)) return null;

            string? hash = ComputeHash(language: language);
            if (hash == null || File.ReadAllText(path: stampPath).Trim() != hash)
            {
                log?.Invoke($"modular .pbrf stamp mismatch for {language}; falling back");
                return null;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            SemanticVerifier.CompiledStdlibState state = ModularStdlibCache.Deserialize(dir: dir);
            log?.Invoke($"loaded {language} modular stdlib ({sw.ElapsedMilliseconds} ms)");
            return state;
        }
        catch (Exception ex)
        {
            log?.Invoke($"modular .pbrf load failed ({ex.Message}); falling back");
            return null;
        }
    }

    /// <summary>Full path to the <c>.pbrf</c> for this language+content-hash, or null if the stdlib root
    /// can't be resolved. The hash is embedded in the filename, so a changed stdlib/compiler simply targets
    /// a different file (miss → recapture); stale files are harmless leftovers.</summary>
    private static string? CachePath(Language language)
    {
        string? hash = ComputeHash(language: language);
        if (hash == null) return null;
        string dir = Path.Combine(
            path1: Environment.GetFolderPath(folder: Environment.SpecialFolder.LocalApplicationData),
            path2: "razorforge", path3: "pbrf");
        return Path.Combine(path1: dir, path2: $"stdlib-{language}-{hash}.pbrf");
    }

    /// <summary>Hashes the stdlib source file set (path+size+mtime) + the compiler assembly mtime + the
    /// format version. Uses file metadata (not contents) so it's fast — a dev editing a stdlib file bumps
    /// its mtime, and a compiler rebuild bumps the assembly mtime, either of which invalidates the cache.</summary>
    /// <summary>Public entry to the stdlib content hash (source files' path+size+mtime + compiler asm mtime
    /// + format version). Used by the <c>emit-pbrf</c> build step to skip regeneration when nothing changed.</summary>
    public static string? ComputeStdlibHash(Language language) => ComputeHash(language: language);

    private static string? ComputeHash(Language language)
    {
        string root = Compiler.Declaration.StdlibLoader.GetDefaultStdlibPath();
        if (string.IsNullOrEmpty(value: root) || !Directory.Exists(path: root)) return null;

        var sb = new StringBuilder();
        sb.Append(value: 'v').Append(value: PbrfSerializer.FormatVersion).Append(value: ';');
        sb.Append(value: language).Append(value: ';');

        AppendSourceFileMetadata(sb: sb, root: root, language: language);

        // The compiler assembly: a rebuild may change the serialized semantic-model classes, so a stale
        // .pbrf must not be loaded against them.
        try
        {
            string asm = typeof(PbrfSerializer).Assembly.Location;
            if (!string.IsNullOrEmpty(value: asm) && File.Exists(path: asm))
                sb.Append(value: "asm|").Append(value: new FileInfo(fileName: asm).LastWriteTimeUtc.Ticks);
        }
        catch
        {
            // best-effort; ignore
        }

        byte[] digest = SHA256.HashData(source: Encoding.UTF8.GetBytes(s: sb.ToString()));
        return Convert.ToHexString(inArray: digest).AsSpan(start: 0, length: 16).ToString().ToLowerInvariant();
    }

    /// <summary>Appends each stdlib source file's path+size+mtime to <paramref name="sb"/>, in Ordinal path
    /// order. Same scan roots as StdlibLoader: RazorForge/*.rf always; Suflae/*.sf under an SF build.</summary>
    private static void AppendSourceFileMetadata(StringBuilder sb, string root, Language language)
    {
        var roots = new System.Collections.Generic.List<(string Dir, string Glob)>
        {
            (Path.Combine(path1: root, path2: "RazorForge"), "*.rf")
        };
        if (language == Language.Suflae)
            roots.Add(item: (Path.Combine(path1: root, path2: "Suflae"), "*.sf"));

        foreach ((string dir, string glob) in roots)
        {
            if (!Directory.Exists(path: dir)) continue;
            foreach (string file in Directory.GetFiles(path: dir, searchPattern: glob,
                         searchOption: SearchOption.AllDirectories)
                     .OrderBy(keySelector: p => p, comparer: StringComparer.Ordinal))
            {
                var fi = new FileInfo(fileName: file);
                sb.Append(value: file).Append(value: '|').Append(value: fi.Length)
                  .Append(value: '|').Append(value: fi.LastWriteTimeUtc.Ticks).Append(value: '\n');
            }
        }
    }
}
