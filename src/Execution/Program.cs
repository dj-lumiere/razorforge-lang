using System.Diagnostics;
using System.Text;
using Builder.LlvmEmit;
using Builder.Declaration;
using Builder.Diagnostics;
using Builder.Tokenizer;
using Builder.Parser;
using Builder.Targeting;
using Builder.Verification;
using Builder.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder.Execution;

/// <summary>
/// Command-line entry point for the RazorForge compiler toolchain.
/// </summary>
internal partial class Program
{
    private const string BuildCommand = "build";
    private const string BuildAndRunCommand = "buildandrun";
    private const string SuflaeLanguageName = "Suflae";
    private const string RazorForgeLanguageName = "RazorForge";

    /// <summary>Suflae's own version line — the <c>&lt;SuflaeVersion&gt;</c> PropertyGroup entry (via
    /// <see cref="Builder.Declaration.BuildInfo"/>). Bump it in the csproj, NOT here.</summary>
    private static string SuflaeVersion => BuildInfo.SuflaeVersion;

    /// <summary>True when the binary was invoked under a Suflae alias (<c>suflae</c>/<c>sf</c>)
    /// rather than <c>razorforge</c>/<c>rf</c>. Selects Suflae branding (version/usage) and makes
    /// Suflae the DEFAULT language when a source's extension does not decide it. The <c>.rf</c>/
    /// <c>.sf</c> extension always wins over this default. The package ships <c>suflae</c>/<c>sf</c>
    /// as copies of the apphost so the invoked name survives in <see cref="Environment.ProcessPath"/>.</summary>
    private static readonly bool InvokedAsSuflae = DetectSuflaeInvocation();

    /// <summary>Detects a Suflae-alias invocation from the executing binary's file name.</summary>
    private static bool DetectSuflaeInvocation()
    {
        try
        {
            string? proc = Environment.ProcessPath;
            if (proc is null)
            {
                return false;
            }

            string name = Path.GetFileNameWithoutExtension(path: proc)
                              .ToLowerInvariant();
            return name is "suflae" or "sf";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Entry point for the RazorForge builder CLI.
    /// Dispatches to the appropriate command handler based on the first argument.
    /// Returns 0 on success or 1 on error.
    /// </summary>
    public static int Main(string[] args)
    {
        RuntimeShadowLoader.Install();

        // Make the build driver byte-faithful for UTF-8. RF child processes write UTF-8 and
        // we forward their stdout/stderr to ours; if Console encodings default to the
        // system ACP (Korean CP949, Western CP1252, ...), every non-ASCII byte gets
        // rewritten as `?` somewhere in the read/write chain. Forcing UTF-8 on both input
        // and output makes the pipe a passthrough.
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        string command = args[0]
                        .ToLowerInvariant()
                        .TrimStart(trimChar: '-');

        int earlyResult = HandleEarlyCommands(command: command);
        if (earlyResult >= 0)
        {
            return earlyResult;
        }

        // Check if first arg is a command or a file
        bool isCommand = command is "parse" or "tokenize" or "codegen" or BuildCommand
            or "buildandrun" or "check" or "validate-stdlib" or "emit-pbrf" or "help";

        if (!isCommand && !TryRewriteBareSuflaeArgs(args: ref args, command: ref command))
        {
            // Default behavior for a bare .rf file: parse and show AST summary
            return ParseFile(sourceFile: args[0]);
        }

        return DispatchCommand(command: command, args: args);
    }

    /// <summary>
    /// Handles the version/lsp/daemon commands that exit before the main dispatch.
    /// Returns a non-negative exit code when the command was handled, or -1 to continue dispatch.
    /// </summary>
    private static int HandleEarlyCommands(string command)
    {
        if (command is "version" or "v")
        {
            PrintVersion();
            return 0;
        }

        // `--lsp` (or `lsp`): run the stdio Language Server. Takes no file argument; it speaks
        // LSP/JSON-RPC over stdin/stdout until the client sends `exit`.
        if (command is "lsp")
        {
            return LspServer.Run();
        }

        // `daemon`: run the warm-compile daemon (foreground). Captures the fully-processed stdlib in RAM
        // and serves warm build/check/buildandrun requests over a per-user named pipe, so a client skips
        // the ~5 s stdlib reprocessing on every invocation. Started explicitly by the developer; clients
        // opt in via RAZORFORGE_DAEMON=1. Ctrl-C / `daemon-stop` shuts it down.
        if (command is "daemon")
        {
            return CompileDaemon.RunServer();
        }

        if (command is "daemon-stop")
        {
            return CompileDaemon.StopServer();
        }

        return -1; // not handled
    }

    /// <summary>
    /// When the first arg is a bare Suflae file (not a known command), rewrites <paramref name="args"/>
    /// to prepend the <c>buildandrun</c> command so the script runs directly.
    /// Returns false when the file is not Suflae (caller falls back to the parse default).
    /// </summary>
    private static bool TryRewriteBareSuflaeArgs(ref string[] args, ref string command)
    {
        // A bare source file RUNS (build + execute) when it is a Suflae script — either the `.sf`
        // extension or invocation under the `suflae`/`sf` alias — so `suflae hello.sf` behaves like
        // `python hello.py`. A bare `.rf` under `razorforge` keeps the dev default of parse-and-dump
        // (use the explicit `parse`/`tokenize`/`codegen` verbs to inspect an .sf without running it).
        if (!InvokedAsSuflae && !IsSuflaeFile(path: args[0]))
        {
            return false;
        }

        string[] forwarded = new string[args.Length + 1];
        forwarded[0] = BuildAndRunCommand;
        Array.Copy(sourceArray: args,
            sourceIndex: 0,
            destinationArray: forwarded,
            destinationIndex: 1,
            length: args.Length);
        args = forwarded;
        command = BuildAndRunCommand;
        return true;
    }

    /// <summary>
    /// Dispatches a known command to its handler. Called after early-command and bare-file handling.
    /// </summary>
    private static int DispatchCommand(string command, string[] args)
    {
        switch (command)
        {
            case "parse":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: parse command requires a file path");
                    return 1;
                }

                return ParseFile(sourceFile: args[1]);

            case "tokenize":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: tokenize command requires a file path");
                    return 1;
                }

                return TokenizeFile(sourceFile: args[1]);

            case "codegen":
                if (args.Length < 2)
                {
                    Console.WriteLine(value: "Error: codegen command requires a file path");
                    return 1;
                }

                return GenerateCode(sourceFile: args[1],
                    outputFile: args.Length > 2
                        ? args[2]
                        : null,
                    buildMode: RfBuildMode.Debug);

            case BuildCommand:
                return RunBuildCommand(args: args);

            case "buildandrun":
                return RunBuildAndRunCommand(args: args);

            case "check":
                return RunCheckCommand(args: args);

            case "validate-stdlib":
                return RunValidateStdlibCommand(args: args);

            case "emit-pbrf":
                return EmitPbrf(args: args);

            case "help":
                PrintUsage();
                return 0;

            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>Runs the <c>check</c> command: resolves the entry file and type-checks without codegen.</summary>
    private static int RunCheckCommand(string[] args)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        return CheckMultiFile(entryFile: resolved.EntryFile,
            projectRoot: resolved.ProjectRoot,
            libraryRoots: resolved.LibraryRoots);
    }

    /// <summary>Runs the <c>validate-stdlib</c> command: validates stdlib routine bodies for the given language.</summary>
    private static int RunValidateStdlibCommand(string[] args)
    {
        string defaultLang = InvokedAsSuflae
            ? "sf"
            : "rf";
        string lang = args.Length >= 2
            ? args[1]
               .ToLowerInvariant()
            : defaultLang;
        Language stdlibLang = lang is "sf" or "suflae"
            ? Language.Suflae
            : Language.RazorForge;
        return ValidateStdlib(language: stdlibLang);
    }

    /// <summary>
    /// Handles the <c>build</c> verb: compiles all the way to a native executable for the HOST OS
    /// (codegen -> opt -> link -> stage runtime DLLs) but does NOT run it. The intermediate
    /// <c>&lt;entry&gt;.ll</c> / <c>.opt.ll</c> are kept as byproducts for inspection; <c>codegen</c> remains
    /// the IR-only verb. (All-OS artifacts come from the release CI.)
    /// </summary>
    private static int RunBuildCommand(string[] args)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        // Warm-daemon path: delegate the compile to a running daemon (skips stdlib reprocessing).
        if (CompileDaemon.TryClientBuild(resolved: resolved, exitCode: out int dbrc))
        {
            return dbrc;
        }

        int buildRc = BuildExecutable(entryFile: resolved.EntryFile,
            exeFile: out string builtExe,
            config: resolved);
        if (buildRc == 0)
        {
            Console.WriteLine(value: $"Executable written to: {Path.GetFullPath(path: builtExe)}");
        }

        return buildRc;
    }

    /// <summary>
    /// Handles the <c>buildandrun</c> verb: builds and executes. Prefers the in-process ORC-JIT dev loop and
    /// the warm-daemon compile path (both skip work) before falling back to a full local AOT build+run.
    /// </summary>
    private static int RunBuildAndRunCommand(string[] args)
    {
        ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            return 1;
        }

        // `[debug] dump-ir`: the ORC-JIT and warm-daemon paths keep the IR in-memory / in the daemon and
        // never write a `<entry>.ll`, so skip BOTH and take the local AOT build+run, which emits (and keeps)
        // `<entry>.ll` + `.opt.ll` beside the source for inspection.
        bool dumpIr = DiagnosticFlags.DumpIr;

        // ORC-JIT dev-loop path (RAZORFORGE_JIT=1): JIT the module in-process — no opt/clang/link,
        // no exe, no spawn. IR comes warm from the daemon when it's up, else a local cold compile.
        if (!dumpIr && CompileDaemon.TryClientJitRun(resolved: resolved, exitCode: out int jrc))
        {
            return jrc;
        }

        // Warm-daemon path: delegate the COMPILE to a running daemon (skips stdlib reprocessing),
        // then run the produced exe locally so interactive stdin/stdout stays with this process.
        if (!dumpIr &&
            CompileDaemon.TryClientBuildAndRun(resolved: resolved, exitCode: out int drc))
        {
            return drc;
        }

        return BuildAndRun(entryFile: resolved.EntryFile, config: resolved);
    }

    /// <summary>
    /// Emits the modular (per-module) compiled-stdlib <c>.pbrf</c> artifacts as a BUILD BYPRODUCT — the
    /// daemon / cold path then LOADS them instead of paying a ~8 s capture on first run. Invoked by the
    /// MSBuild post-build target (and manually). Writes to <c>&lt;Standard&gt;/.pbrf/&lt;Language&gt;/</c>.
    /// Incremental: a <c>stamp.txt</c> holds the stdlib content hash — if it matches and an index exists, the
    /// (re)capture is SKIPPED, so a no-stdlib-change rebuild is near-instant. Each language is best-effort:
    /// one failing does not fail the others (or the build — the caller uses ContinueOnError).
    /// Usage: <c>emit-pbrf [outDir] [--all|--sf]</c>. Default outDir = the resolved stdlib root's <c>.pbrf</c>.
    /// </summary>
    private static int EmitPbrf(string[] args)
    {
        string? outDir = args.Length > 1 && !args[1]
           .StartsWith(value: "--")
            ? args[1]
            : Path.Combine(path1: StdlibLoader.GetDefaultStdlibPath(), path2: ".pbrf");

        var langs = new List<Language> { Language.RazorForge };
        if (args.Contains(value: "--all") || args.Contains(value: "--sf"))
        {
            langs.Add(item: Language.Suflae);
        }

        foreach (Language lang in langs)
        {
            try
            {
                string langDir = Path.Combine(path1: outDir, path2: lang.ToString());
                string stampPath = Path.Combine(path1: langDir, path2: "stamp.txt");
                string? hash =
                    Builder.Serialization.StdlibSnapshotCache.ComputeStdlibHash(language: lang);

                if (hash != null && File.Exists(path: stampPath) && File
                       .ReadAllText(path: stampPath)
                       .Trim() == hash &&
                    File.Exists(path: Path.Combine(path1: langDir, path2: "index.pbrf")))
                {
                    Console.WriteLine(value: $"[emit-pbrf] {lang}: up to date");
                    continue;
                }

                var sw = Stopwatch.StartNew();
                SemanticVerifier.CompiledStdlibState state =
                    SemanticVerifier.CaptureCompiledStdlib(language: lang);
                if (Directory.Exists(path: langDir))
                {
                    Directory.Delete(path: langDir, recursive: true);
                }

                IReadOnlyList<string> labels =
                    Builder.Serialization.ModularStdlibCache.Serialize(state: state,
                        dir: langDir);
                if (hash != null)
                {
                    File.WriteAllText(path: stampPath, contents: hash);
                }

                Console.WriteLine(
                    value:
                    $"[emit-pbrf] {lang}: {labels.Count} modules ({sw.ElapsedMilliseconds} ms) -> {langDir}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    value: $"[emit-pbrf] {lang} FAILED (non-fatal): {ex.Message}");
            }
        }

        return 0;
    }

    /// <summary>
    /// Bundles the optional warm-stdlib provider delegates passed down the build pipeline so callers
    /// do not need to forward three separate nullable function parameters individually.
    /// </summary>
    private sealed record WarmProviders(
        Func<Language, SemanticVerifier.CompiledStdlibState?>? WarmProvider,
        Action<string>? IrCallback,
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            StdlibIndexProvider);

    /// <summary>
    /// Bundles the inputs that drive Phase 2 (semantic analysis) of the multi-file pipeline,
    /// reducing the parameter count of <see cref="RunPhase2SemanticAnalysis"/>.
    /// </summary>
    private sealed record Phase2Context(
        Language Language,
        RfBuildMode BuildMode,
        bool SaTiming,
        bool ShowBuildStages,
        bool RequireStartRoutine,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? WarmProvider);

    /// <summary>Groups the parameters for <see cref="RunPhase1BuildDriver"/>.</summary>
    private sealed record Phase1Context(
        string EntryFile,
        string ProjectRoot,
        string StdlibRoot,
        Language Language,
        IReadOnlyList<string>? LibraryRoots,
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            StdlibIndexProvider,
        bool ShowBuildStages,
        Stopwatch? SwBuild);

    /// <summary>Groups the parameters for <see cref="RunPhase3Codegen"/>.</summary>
    private sealed record Phase3Context(
        string EntryFile,
        string? OutputFile,
        TargetConfig Target,
        RfBuildMode BuildMode,
        bool SaTiming,
        bool DumpAst,
        bool ShowBuildStages,
        Action<string>? IrCallback,
        Stopwatch? SwPhase);

    /// <summary>
    /// The fully-resolved build configuration for a <c>build</c>/<c>buildandrun</c>/<c>check</c> invocation:
    /// entry file, project root, build mode, and the external-library link config. Produced by
    /// <see cref="ResolveEntryFile"/> from the CLI args + the nearest <c>config.toml</c>. A resolution
    /// FAILURE is signalled by <see cref="EntryFile"/> being null (all other fields keep their defaults).
    /// Replaces a former 12-tuple — the field count outgrew a tuple's readability.
    /// </summary>
    internal sealed record ResolvedEntry
    {
        /// <summary>The entry source file, or null when resolution failed (error already printed).</summary>
        public string? EntryFile { get; init; }

        /// <summary>The project root (manifest directory), used as the import search root.</summary>
        public string? ProjectRoot { get; init; }

        /// <summary>The optional explicit output file (codegen verb); null otherwise.</summary>
        public string? OutputFile { get; init; }

        /// <summary>The build optimization mode.</summary>
        public RfBuildMode BuildMode { get; init; } = RfBuildMode.Debug;

        /// <summary>Whether to dump the post-desugar AST alongside the build.</summary>
        public bool DumpAst { get; init; }

        /// <summary>Whether to print per-phase SA timings.</summary>
        public bool SaTiming { get; init; }

        /// <summary>Whether SA must find a <c>routine start()</c> (an executable build).</summary>
        public bool RequireStartRoutine { get; init; }

        /// <summary>Whether to print build-stage banners.</summary>
        public bool ShowBuildStages { get; init; }

        /// <summary>External RF library dependency directories (import search roots).</summary>
        public IReadOnlyList<string> LibraryRoots { get; init; } = [];

        /// <summary>Simple name-only C libraries to link (the <c>-l</c> names).</summary>
        public IReadOnlyList<string> CLibraries { get; init; } = [];

        /// <summary>Extra <c>-L</c> search directories for the C libraries.</summary>
        public IReadOnlyList<string> LibraryPaths { get; init; } = [];

        /// <summary>Richly-declared C libraries (<c>[libraries.NAME]</c>): linkage kind + calling convention.</summary>
        public IReadOnlyDictionary<string, CLibrary> LibraryConfigs { get; init; } =
            new Dictionary<string, CLibrary>();

        /// <summary>Route builds through a running warm daemon (manifest <c>[target] use-daemon</c>).</summary>
        public bool UseDaemon { get; init; }

        /// <summary>Use the ORC-JIT dev-loop path for buildandrun (manifest <c>mode = "debug-jit"</c>).</summary>
        public bool Jit { get; init; }

        /// <summary>Reserved: incremental compilation (manifest <c>[target] incremental</c>); not yet wired.</summary>
        public bool Incremental { get; init; }
    }

    /// <summary>
    /// Resolves the <see cref="ResolvedEntry"/> for build/buildandrun/check commands.
    /// Searches for a config.toml manifest in all cases: when no entry file is given the
    /// manifest supplies the executable; when an explicit entry file is given it overrides
    /// [target] executable but the manifest's other settings still apply.
    /// ALL build configuration lives in the manifest's [target] section (executable, library,
    /// mode, dump-ast, sa-timing, show-build-stages) — the CLI deliberately takes no flags.
    /// On error the returned entry's <see cref="ResolvedEntry.EntryFile"/> is null.
    /// </summary>
    private static ResolvedEntry ResolveEntryFile(string[] args, bool needsOutputArg)
    {
        // args[0] is the command name (build/buildandrun/check)
        if (!ParsePositionalArgs(args: args,
                needsOutputArg: needsOutputArg,
                explicitEntry: out string? explicitEntry,
                outputFile: out string? outputFile))
        {
            return new ResolvedEntry();
        }

        // Explicit source file given — use it as the entry point, but still honor the
        // nearest config.toml (walking up from the file's directory): the manifest
        // remains the single source of build configuration (mode, library deps, debug
        // fields) even for single-file builds; only [target] executable is overridden.
        // .toml files are treated as manifests, not source files.
        if (explicitEntry != null && !explicitEntry.EndsWith(value: ".toml",
                comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return ResolveExplicitEntry(explicitEntry: explicitEntry, outputFile: outputFile);
        }

        return ResolveManifestEntry(explicitEntry: explicitEntry, outputFile: outputFile);
    }

    /// <summary>Parses the positional CLI args (<c>[entry-file] [out.ll]</c>) after the command name. Returns
    /// false (after printing an error) if an unknown <c>-</c>-prefixed option is encountered.</summary>
    private static bool ParsePositionalArgs(string[] args, bool needsOutputArg,
        out string? explicitEntry, out string? outputFile)
    {
        explicitEntry = null;
        outputFile = null;

        int i = 1;
        while (i < args.Length)
        {
            if (!args[i]
                   .StartsWith(value: '-'))
            {
                if (explicitEntry == null)
                {
                    explicitEntry = args[i];
                }
                else if (needsOutputArg && outputFile == null)
                {
                    outputFile = args[i];
                }

                i++;
            }
            else
            {
                Console.WriteLine(
                    value:
                    $"Error: unknown option '{args[i]}'. RazorForge takes no build flags — configure builds in config.toml ([target] executable, library, mode, ...).");
                return false;
            }
        }

        return true;
    }

    /// <summary>Resolves an explicitly-given source-file entry: honors the nearest config.toml (or debug
    /// defaults when manifest-less); only [target] executable is overridden by the command-line entry.</summary>
    private static ResolvedEntry ResolveExplicitEntry(string explicitEntry, string? outputFile)
    {
        if (!File.Exists(path: explicitEntry))
        {
            Console.WriteLine(value: $"Error: File '{explicitEntry}' not found.");
            return new ResolvedEntry();
        }

        string entryDir =
            Path.GetDirectoryName(path: Path.GetFullPath(path: explicitEntry)) ?? ".";
        string? nearbyManifest = ManifestLoader.FindManifest(startDir: entryDir);
        if (nearbyManifest == null)
        {
            // Truly manifest-less — debug defaults. Assume an executable build so
            // codegen knows to synthesize @main and SA can require a 'start' routine.
            DiagnosticFlags.Reset();
            return new ResolvedEntry
            {
                EntryFile = explicitEntry,
                ProjectRoot = entryDir,
                OutputFile = outputFile,
                RequireStartRoutine = true
            };
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: nearbyManifest,
                resolveExecutable: false);
            BuildTarget target = manifest.Target;
            RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

            if (manifest.Debug.ShowBuildStages)
            {
                Console.WriteLine(value: $"Using manifest: {nearbyManifest}");
                Console.WriteLine(
                    value:
                    $"Executable: {explicitEntry} ({target.Mode}, entry from command line)");
                if (target.Libraries.Count > 0)
                {
                    Console.WriteLine(
                        value:
                        $"Libraries: {string.Join(separator: ", ", values: target.Libraries)}");
                }
            }

            ApplyDiagnosticFlags(manifest: manifest);
            return new ResolvedEntry
            {
                EntryFile = explicitEntry,
                ProjectRoot = manifest.ManifestDirectory,
                OutputFile = outputFile,
                BuildMode = buildMode,
                DumpAst = manifest.Debug.DumpAst,
                SaTiming = manifest.Debug.Timing,
                RequireStartRoutine = true,
                ShowBuildStages = manifest.Debug.ShowBuildStages,
                LibraryRoots = target.Libraries,
                CLibraries = target.CLibraries,
                LibraryPaths = target.LibraryPaths,
                LibraryConfigs = target.LibraryConfigs,
                UseDaemon = target.UseDaemon,
                Jit = ModeUsesJit(mode: target.Mode),
                Incremental = target.Incremental
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return new ResolvedEntry();
        }
    }

    /// <summary>Resolves the entry from a manifest: either a <c>.toml</c> passed explicitly, or (no explicit
    /// entry) the nearest config.toml found from the current directory. The manifest's [target] executable is
    /// the entry point.</summary>
    private static ResolvedEntry ResolveManifestEntry(string? explicitEntry, string? outputFile)
    {
        // No explicit entry (or .toml manifest given) — load manifest
        string? manifestPath;
        if (explicitEntry != null)
        {
            manifestPath = File.Exists(path: explicitEntry)
                ? Path.GetFullPath(path: explicitEntry)
                : null;
        }
        else
        {
            manifestPath = ManifestLoader.FindManifest(startDir: Environment.CurrentDirectory);
        }

        if (manifestPath == null)
        {
            if (explicitEntry != null)
            {
                Console.WriteLine(value: $"Error: Manifest '{explicitEntry}' not found.");
            }
            else
            {
                Console.WriteLine(
                    value: "Error: No entry file specified and no config.toml found.");
                Console.WriteLine(
                    value: "Either provide an entry file or create a config.toml manifest.");
            }

            return new ResolvedEntry();
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath);
            BuildTarget target = manifest.Target;

            RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

            bool showBuildStages = manifest.Debug.ShowBuildStages;
            if (showBuildStages)
            {
                Console.WriteLine(value: $"Using manifest: {manifestPath}");
                Console.WriteLine(value: $"Executable: {target.Executable} ({target.Mode})");
                if (target.Libraries.Count > 0)
                {
                    Console.WriteLine(
                        value:
                        $"Libraries: {string.Join(separator: ", ", values: target.Libraries)}");
                }
            }

            ApplyDiagnosticFlags(manifest: manifest);
            return new ResolvedEntry
            {
                EntryFile = target.Executable,
                ProjectRoot = manifest.ManifestDirectory,
                OutputFile = outputFile,
                BuildMode = buildMode,
                DumpAst = manifest.Debug.DumpAst,
                SaTiming = manifest.Debug.Timing,
                RequireStartRoutine = true,
                ShowBuildStages = showBuildStages,
                LibraryRoots = target.Libraries,
                CLibraries = target.CLibraries,
                LibraryPaths = target.LibraryPaths,
                LibraryConfigs = target.LibraryConfigs,
                UseDaemon = target.UseDaemon,
                Jit = ModeUsesJit(mode: target.Mode),
                Incremental = target.Incremental
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
            return new ResolvedEntry();
        }
    }

    /// <summary>
    /// Maps a [target] mode string to its <see cref="RfBuildMode"/>; throws on unknown modes.
    /// </summary>
    private static RfBuildMode ParseBuildMode(string mode)
    {
        return mode.ToLowerInvariant() switch
        {
            // `debug-jit` = the in-process ORC-JIT dev loop (same -O0 codegen as `debug`, but JIT-and-run
            // instead of AOT build+run). The JIT choice is derived from the mode string separately (see
            // ModeUsesJit); the optimization level here is Debug for both.
            "debug" or "debug-jit" => RfBuildMode.Debug,
            "release" => RfBuildMode.Release,
            "release-time" => RfBuildMode.ReleaseTime,
            "release-space" => RfBuildMode.ReleaseSpace,
            _ => throw new InvalidOperationException(
                message: $"Unknown build mode '{mode}' in [target]. " +
                         "Valid modes are: debug-jit, debug, release, release-time, release-space.")
        };
    }

    /// <summary>Whether a <c>[target] mode</c> string selects the in-process ORC-JIT dev loop.</summary>
    private static bool ModeUsesJit(string mode)
    {
        return string.Equals(a: mode?.Trim(),
            b: "debug-jit",
            comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Populates the process-wide <see cref="DiagnosticFlags"/> from a manifest's <c>[debug]</c> section,
    /// replacing the former <c>RF_*</c> / <c>RAZORFORGE_PHASE_TIMING</c> environment variables. Reset-then-set
    /// so a prior build's flags never linger (matters for the serial compile daemon).
    /// </summary>
    private static void ApplyDiagnosticFlags(ProjectManifest manifest)
    {
        DiagnosticFlags.Reset();
        DiagnosticFlags.PhaseTiming = manifest.Debug.Timing;
        DiagnosticFlags.PruneStats = manifest.Debug.PruneStats;
        DiagnosticFlags.JitTrace = manifest.Debug.JitTrace;
        DiagnosticFlags.DumpIr = manifest.Debug.DumpIr;
        DiagnosticFlags.ReachabilityDump = manifest.Debug.ReachabilityDump;
        DiagnosticFlags.MaySuspendDump = manifest.Debug.MaySuspendDump;
    }

    /// <summary>
    /// Prints the CLI usage instructions to standard output.
    /// </summary>
    private static void PrintUsage()
    {
        // The command name the user typed (the shipped `suflae`/`sf` aliases are copies of the
        // apphost), so examples echo how the tool was actually invoked.
        string tool = InvokedAsSuflae
            ? "suflae"
            : "razorforge";
        string header = InvokedAsSuflae
            ? $"{SuflaeLanguageName} v{SuflaeVersion}"
            : $"{RazorForgeLanguageName} Builder {GetVersionString()}";

        Console.WriteLine(value: header);
        Console.WriteLine();
        Console.WriteLine(value: "Usage:");
        Console.WriteLine(value: InvokedAsSuflae
            ? $"  {tool} <source-file>                        - Build and run the script"
            : $"  {tool} <source-file>                        - Parse file and show AST summary (a bare .sf runs)");
        Console.WriteLine(
            value:
            $"  {tool} parse <source-file>                  - Parse file and show AST summary");
        Console.WriteLine(
            value:
            $"  {tool} tokenize <source-file>               - Tokenize file and show tokens");
        Console.WriteLine(
            value:
            $"  {tool} codegen <source-file> [out.ll]       - Generate LLVM IR (single file)");
        Console.WriteLine(
            value:
            $"  {tool} build [entry-file]                   - Build a native executable (host OS, no run)");
        Console.WriteLine(
            value: $"  {tool} buildandrun [entry-file]             - Build and execute");
        Console.WriteLine(
            value:
            $"  {tool} check [entry-file]                   - Type-check only (no codegen)");
        Console.WriteLine(
            value:
            $"  {tool} validate-stdlib [rf|sf]              - Validate stdlib routine bodies");
        Console.WriteLine(
            value: $"  {tool} help                                 - Show this help");
        Console.WriteLine(
            value: $"  {tool} version                              - Show compiler version");
        Console.WriteLine();
        Console.WriteLine(
            value: "  <source-file>: .rf file for RazorForge or .sf file for Suflae");
        if (InvokedAsSuflae)
        {
            Console.WriteLine(
                value:
                "  Invoked as suflae: a source with no .rf/.sf extension defaults to Suflae.");
        }

        Console.WriteLine(
            value: "  If no entry file is given, searches for config.toml in the current");
        Console.WriteLine(value: "  directory and parent directories.");
        Console.WriteLine();
        Console.WriteLine(
            value: "  There are no build flags: all build configuration lives in config.toml's");
        Console.WriteLine(
            value: "  [target] section (executable, library, mode, show-build-stages, ...).");
    }

    /// <summary>Prints the compiler version to standard output. Under a Suflae invocation this
    /// reports Suflae's own version line; otherwise the RazorForge assembly version.</summary>
    private static void PrintVersion()
    {
        if (InvokedAsSuflae)
        {
            Console.WriteLine(value: $"{SuflaeLanguageName} v{SuflaeVersion}");
            return;
        }

        Console.WriteLine(value: $"{RazorForgeLanguageName} {GetVersionString()}");
    }

    /// <summary>
    /// Returns the RazorForge compiler version string, preferring the <c>&lt;RazorForgeVersion&gt;</c>
    /// PropertyGroup value (via <see cref="Builder.Declaration.BuildInfo"/>), then the assembly
    /// informational version (e.g. "0.0.1-alpha"), stripping any "+commit" suffix and prefixing <c>v</c>.
    /// </summary>
    private static string GetVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        string version = BuildInfo.AssemblyMetadata(key: "RazorForgeVersion") ?? assembly
           .GetCustomAttributes(
                attributeType: typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                inherit: false)
           .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
           .FirstOrDefault()
          ?.InformationalVersion ?? assembly.GetName()
                                            .Version
                                           ?.ToString() ?? "unknown";
        int plusIndex = version.IndexOf(value: '+');
        return plusIndex > 0
            ? $"v{version[..plusIndex]}"
            : $"v{version}";
    }

    /// <summary>Returns true if the given file path has a <c>.sf</c> extension (Suflae source file).</summary>
    private static bool IsSuflaeFile(string path)
    {
        return path.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Decides whether a source file should be compiled as Suflae. The <c>.sf</c>/<c>.rf</c>
    /// extension is authoritative; only when neither decides (extension-less entry) does the
    /// invocation default (<see cref="InvokedAsSuflae"/>) break the tie.</summary>
    private static bool IsSuflaeSource(string path)
    {
        if (IsSuflaeFile(path: path))
        {
            return true;
        }

        if (path.EndsWith(value: ".rf", comparisonType: StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return InvokedAsSuflae;
    }

    /// <summary>
    /// Tokenizes the given source file and prints each token with its position and text to standard output.
    /// Returns 0 on success or 1 if the file is not found or tokenization fails.
    /// </summary>
    private static int TokenizeFile(string sourceFile)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        bool isSuflae = IsSuflaeSource(path: sourceFile);

        Console.WriteLine(
            value:
            $"Tokenizing {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;
            var tokenizer = new Builder.Tokenizer.Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();

            Console.WriteLine(value: $"Generated {tokens.Count} tokens:");
            Console.WriteLine();

            foreach (Token tok in tokens)
            {
                Console.WriteLine(
                    value:
                    $"  {tok.Line,4}:{tok.Column,-3} {tok.Type,-25} '{EscapeString(s: tok.Text)}'");
            }

            Console.WriteLine();
            Console.WriteLine(value: "Tokenization successful!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Tokenization failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Tokenizes and parses the given source file, then prints a summary of the resulting AST
    /// along with any warnings. Returns 0 on success or 1 if the file is not found or parsing fails.
    /// </summary>
    private static int ParseFile(string sourceFile)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        bool isSuflae = IsSuflaeSource(path: sourceFile);

        Console.WriteLine(
            value:
            $"Parsing {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            var tokenizer = new Builder.Tokenizer.Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Builder.Parser.Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            List<BuildWarning> warnings = parser.GetWarnings();

            Console.WriteLine(
                value: $"Successfully parsed! AST contains {ast.Declarations.Count} declarations");

            // Show warnings if any
            if (warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({warnings.Count}) ===");
                foreach (BuildWarning warning in warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
                }
            }

            // Show AST summary
            Console.WriteLine();
            Console.WriteLine(value: "=== AST SUMMARY ===");
            foreach (ISyntaxTreeNode decl in ast.Declarations)
            {
                PrintDeclarationSummary(node: decl, indent: 0);
            }

            Console.WriteLine();
            Console.WriteLine(value: "Parsing successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: ex.Message);
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the semantic analyzer over the standard library routine bodies for the given language
    /// and reports any errors found. Returns 0 if all bodies are valid, or 1 if errors were found.
    /// </summary>
    private static int ValidateStdlib(Language language)
    {
        try
        {
            string langName = language == Language.Suflae
                ? SuflaeLanguageName
                : RazorForgeLanguageName;
            Console.WriteLine(value: $"Validating {langName} stdlib routine bodies...");
            Console.WriteLine();

            var analyzer = new SemanticVerifier(language: language);
            List<SemanticError> stdlibErrors = analyzer.ValidateStdlibBodies();

            // Compiler↔stdlib name-contract check: every routine/type/field name the compiler
            // hard-codes against the stdlib must still resolve. A rename that breaks a contract
            // fails HERE (loudly) instead of silently miscompiling at runtime.
            List<string> contractErrors = analyzer.CheckRuntimeContract();

            if (stdlibErrors.Count == 0 && contractErrors.Count == 0)
            {
                Console.WriteLine(value: "All stdlib routine bodies validated successfully!");
                return 0;
            }

            if (contractErrors.Count > 0)
            {
                Console.WriteLine(
                    value: $"=== RUNTIME-CONTRACT ERRORS ({contractErrors.Count}) ===");
                Console.WriteLine(
                    value:
                    "  A name the compiler hard-codes against the stdlib no longer resolves.");
                Console.WriteLine(
                    value:
                    "  Update src/Resolution/RuntimeContract.cs to match the stdlib rename.");
                foreach (string contractError in contractErrors)
                {
                    Console.WriteLine(value: $"    - {contractError}");
                }

                Console.WriteLine();
                if (stdlibErrors.Count == 0)
                {
                    return 1;
                }
            }

            // Group errors by file
            var errorsByFile = new Dictionary<string, List<SemanticError>>();
            foreach (SemanticError error in stdlibErrors)
            {
                string file = error.Location.FileName;
                if (!errorsByFile.TryGetValue(key: file, value: out List<SemanticError>? list))
                {
                    list = [];
                    errorsByFile[key: file] = list;
                }

                list.Add(item: error);
            }

            Console.WriteLine(
                value:
                $"=== STDLIB VALIDATION ERRORS ({stdlibErrors.Count} errors in {errorsByFile.Count} files) ===");
            Console.WriteLine();

            foreach ((string file, List<SemanticError> errors) in errorsByFile.OrderBy(
                         keySelector: kvp => kvp.Key))
            {
                Console.WriteLine(
                    value: $"  {Path.GetFileName(path: file)} ({errors.Count} errors):");
                foreach (SemanticError error in errors)
                {
                    DiagnosticRenderer.Print(error: error, indent: "    ");
                }

                Console.WriteLine();
            }

            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Stdlib validation failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Runs the full compiler pipeline (tokenize -> parse -> semantic analysis -> LLVM IR generation)
    /// on the given source file and writes the resulting IR to <paramref name="outputFile"/>,
    /// or to a default <c>.ll</c> file if no output path is specified.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int GenerateCode(string sourceFile, string? outputFile,
        RfBuildMode buildMode = RfBuildMode.Debug, bool saTiming = false)
    {
        if (!File.Exists(path: sourceFile))
        {
            Console.WriteLine(value: $"Error: File '{sourceFile}' not found.");
            return 1;
        }

        string code = File.ReadAllText(path: sourceFile);
        bool isSuflae = IsSuflaeSource(path: sourceFile);

        Console.WriteLine(
            value:
            $"Building {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            var tokenizer = new Builder.Tokenizer.Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Builder.Parser.Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            Console.WriteLine(value: $"Parsed {ast.Declarations.Count} declarations");

            // Semantic Analysis
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var target = TargetConfig.ForCurrentHost();
            var analyzer = new SemanticVerifier(language: language,
                target: target,
                buildMode: buildMode) { SaTiming = saTiming };
            AnalysisResult result = analyzer.Analyze(program: ast);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            // Show errors and warnings
            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: result.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                DiagnosticRenderer.PrintAll(warnings: result.Warnings);
            }

            // Code Generation
            Console.WriteLine();
            Console.WriteLine(value: "=== CODE GENERATION ===");

            // Pass stdlib programs to codegen so intrinsic routines get built
            List<(SyntaxTree.Program Program, string FilePath, string Module)> stdlibPrograms =
                result.Registry.StdlibPrograms;

            // 9-2: instrument may-suspend routine bodies with cancellation push/pop markers
            // (no-op unless something reaches a coroutine suspend point). Mutates `ast` in place,
            // which is the same AST object codegen consumes below.
            Builder.Lowering.Passes.CancellationInstrumentationPass.Run(programs:
                [(ast, ast.Location.FileName, "")],
                instantiatedBodies: result.InstantiatedGenericBodies,
                maySuspendKeys: result.MaySuspendRoutineKeys,
                registry: result.Registry);

            var generator = new LlvmEmitter(program: ast,
                registry: result.Registry,
                options: new LlvmEmitterOptions
                {
                    StdlibPrograms = stdlibPrograms,
                    Target = target,
                    BuildMode = buildMode,
                    SynthesizedBodies = result.SynthesizedBodies,
                    InstantiatedGenericBodies = result.InstantiatedGenericBodies,
                    LiveRoutineKeys = result.LiveRoutineKeys,
                    MaySuspendRoutineKeys = result.MaySuspendRoutineKeys
                })
            {
                Timing = saTiming,
                // Single-file codegen: the entry file's own module is the program entry.
                EntryModule = ast.Declarations
                                 .OfType<ModuleDeclaration>()
                                 .FirstOrDefault()
                                ?.Path
            };
            string llvmIr = generator.Generate();
            Console.WriteLine(value: $"Routines emitted: {generator.EmittedRoutineCount}");

            // Output
            if (outputFile != null)
            {
                File.WriteAllText(path: outputFile, contents: llvmIr);
                Console.WriteLine(value: $"LLVM IR written to: {outputFile}");
            }
            else
            {
                // Default output file
                string defaultOutput = Path.ChangeExtension(path: sourceFile, extension: ".ll");
                File.WriteAllText(path: defaultOutput, contents: llvmIr);
                Console.WriteLine(value: $"LLVM IR written to: {defaultOutput}");
            }

            Console.WriteLine();
            Console.WriteLine(value: "Code generation successful!");
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Build failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>Filters the build graph's units down to USER files (dropping the stdlib files already loaded
    /// by TypeRegistry/StdlibLoader — those under the normalized stdlib root).</summary>
    private static List<FileBuildUnit> FilterUserUnits(BuildResult buildResult, string stdlibRoot)
    {
        string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
        return buildResult.Units
                          .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                                      .StartsWith(value: normalizedStdlib,
                                                           comparisonType: StringComparison
                                                              .OrdinalIgnoreCase))
                          .ToList();
    }

    /// <summary>Orders the user file units by module initialization order, appending any unit not covered by
    /// that order (e.g. an entry file with no module decl) in encounter order.</summary>
    private static List<(SyntaxTree.Program Program, string FilePath)> OrderUserFiles(
        List<FileBuildUnit> userUnits, IReadOnlyList<string> initializationOrder)
    {
        // Map module names back to file units for ordering
        var unitsByModule =
            new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
        foreach (FileBuildUnit unit in userUnits)
        {
            string moduleName =
                unit.Module ?? Path.GetFileNameWithoutExtension(path: unit.FilePath);
            unitsByModule[key: moduleName] = unit;
        }

        var orderedFiles = new List<(SyntaxTree.Program Program, string FilePath)>();
        foreach (string moduleName in initializationOrder)
        {
            if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
            {
                orderedFiles.Add(item: (unit.Ast, unit.FilePath));
            }
        }

        // Fallback: if init order doesn't cover all units (e.g., entry file with no module decl)
        orderedFiles.AddRange(collection: userUnits
                                         .Where(predicate: unit =>
                                              !orderedFiles.Any(predicate: f =>
                                                  string.Equals(a: f.FilePath,
                                                      b: unit.FilePath,
                                                      comparisonType: StringComparison
                                                         .OrdinalIgnoreCase)))
                                         .Select(selector: unit => (unit.Ast, unit.FilePath)));

        return orderedFiles;
    }

    /// <summary>
    /// Runs the multi-file build pipeline: BuildDriver (parse + resolve imports + topo sort)
    /// -> SemanticVerifier.AnalyzeMultiple -> LlvmEmitter with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile,
        out IReadOnlyList<string> discoveredLinkLibraries, ResolvedEntry config,
        WarmProviders? warm = null)
    {
        string? projectRoot = config.ProjectRoot;
        RfBuildMode buildMode = config.BuildMode;
        bool dumpAst = config.DumpAst;
        bool saTiming = config.SaTiming;
        bool requireStartRoutine = config.RequireStartRoutine;
        bool showBuildStages = config.ShowBuildStages;
        IReadOnlyList<string>? libraryRoots = config.LibraryRoots;
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = warm?.WarmProvider;
        Action<string>? irCallback = warm?.IrCallback;
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            stdlibIndexProvider = warm?.StdlibIndexProvider;
        // C libraries declared in source via `@link("...")` on `C::` externs, gathered from the files
        // that actually compile (post `@target` gate) and surfaced to the link step. Assigned once the
        // AST is available; stays empty on the early-error paths below.
        discoveredLinkLibraries = [];
        if (!File.Exists(path: entryFile))
        {
            Console.WriteLine(value: $"Error: File '{entryFile}' not found.");
            return 1;
        }

        bool isSuflae = IsSuflaeSource(path: entryFile);
        Language language = isSuflae
            ? Language.Suflae
            : Language.RazorForge;

        if (showBuildStages)
        {
            Console.WriteLine(
                value:
                $"Building {entryFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)} (multi-file)...");
            Console.WriteLine();
        }

        try
        {
            Stopwatch? _swBuild = DiagnosticFlags.PhaseTiming
                ? Stopwatch.StartNew()
                : null;
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            int phase1Result = RunPhase1BuildDriver(
                p1: new Phase1Context(EntryFile: entryFile,
                    ProjectRoot: projectRoot,
                    StdlibRoot: stdlibRoot,
                    Language: language,
                    LibraryRoots: libraryRoots,
                    StdlibIndexProvider: stdlibIndexProvider,
                    ShowBuildStages: showBuildStages,
                    SwBuild: _swBuild),
                orderedFiles: out List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
                unitsByFile: out Dictionary<string, FileBuildUnit> unitsByFile,
                driver: out BuildDriver driver,
                discoveredLinks: out discoveredLinkLibraries);
            if (phase1Result != 0)
            {
                return phase1Result;
            }

            if (!InjectGlobalInitializers(orderedFiles: orderedFiles))
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            // Phase 2: Semantic analysis (multi-file) — extracted to keep this method's complexity ≤15.
            var phase2Ctx = new Phase2Context(Language: language,
                BuildMode: buildMode,
                SaTiming: saTiming,
                ShowBuildStages: showBuildStages,
                RequireStartRoutine: requireStartRoutine,
                WarmProvider: warmProvider);
            int phase2Result = RunPhase2SemanticAnalysis(ctx: phase2Ctx,
                driver: driver,
                orderedFiles: orderedFiles,
                swBuild: _swBuild,
                result: out AnalysisResult result,
                swPhase: out Stopwatch? _swPhase);
            if (phase2Result != 0)
            {
                return phase2Result;
            }

            // Phase 3: Code generation (multi-program)
            return RunPhase3Codegen(p3: new Phase3Context(EntryFile: entryFile,
                    OutputFile: outputFile,
                    Target: TargetConfig.ForCurrentHost(),
                    BuildMode: buildMode,
                    SaTiming: saTiming,
                    DumpAst: dumpAst,
                    ShowBuildStages: showBuildStages,
                    IrCallback: irCallback,
                    SwPhase: _swPhase),
                orderedFiles: orderedFiles,
                unitsByFile: unitsByFile,
                result: result);
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Build failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>
    /// Phase 2 of the multi-file build: constructs the semantic verifier (warm or cold path),
    /// runs <see cref="SemanticVerifier.AnalyzeMultiple"/>, reports errors and warnings, and
    /// checks that a <c>routine start()</c> is present when required. Returns 0 on success.
    /// </summary>
    private static int RunPhase2SemanticAnalysis(Phase2Context ctx, BuildDriver driver,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles, Stopwatch? swBuild,
        out AnalysisResult result, out Stopwatch? swPhase)
    {
        if (ctx.ShowBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");
        }

        var target = TargetConfig.ForCurrentHost();
        // Warm path: a daemon supplies a fully-processed stdlib snapshot for this language, so the
        // restore ctor skips the stdlib desugaring/verification/monomorphization and only the user
        // program is analyzed. Cold path (WarmProvider == null) constructs a fresh verifier.
        // A cold-path fallback through the snapshot cache is deferred — it exposed a warm-restore
        // over-prune on complex programs. Fix that liveness gap first, then re-enable.
        SemanticVerifier.CompiledStdlibState? warmState =
            ctx.WarmProvider?.Invoke(arg: ctx.Language);
        // The timing flag drives both granular SA sub-phase lines and the coarse phase lines below,
        // via the single DiagnosticFlags.PhaseTiming source.
        SemanticVerifier analyzer = warmState != null
            ? new SemanticVerifier(language: ctx.Language,
                warm: warmState,
                target: target,
                buildMode: ctx.BuildMode)
            {
                SaTiming = ctx.SaTiming || DiagnosticFlags.PhaseTiming
            }
            : new SemanticVerifier(language: ctx.Language,
                target: target,
                buildMode: ctx.BuildMode)
            {
                SaTiming = ctx.SaTiming || DiagnosticFlags.PhaseTiming
            };
        if (swBuild != null)
        {
            Console.Error.WriteLine(
                value:
                $"[timing] warm-restore ctor (rebuild verifier from snapshot): {swBuild.ElapsedMilliseconds} ms");
            swBuild.Restart();
        }

        analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
        swPhase = DiagnosticFlags.PhaseTiming
            ? Stopwatch.StartNew()
            : null;
        result = analyzer.AnalyzeMultiple(files: orderedFiles);
        if (swPhase != null)
        {
            Console.Error.WriteLine(
                value:
                $"[phase] AnalyzeMultiple (SA+instantiation+postproc): {swPhase.ElapsedMilliseconds} ms");
            swPhase.Restart();
        }

        if (ctx.ShowBuildStages)
        {
            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");
        }

        if (result.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
            DiagnosticRenderer.PrintAll(errors: result.Errors);
            Console.WriteLine();
            Console.Error.WriteLine(value: "Code generation aborted due to errors.");
            return 1;
        }

        if (result.Warnings.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
            DiagnosticRenderer.PrintAll(warnings: result.Warnings);
        }

        if (ctx.RequireStartRoutine)
        {
            int startCheck = CheckStartRoutinePresent(orderedFiles: orderedFiles, result: result);
            if (startCheck != 0)
            {
                return startCheck;
            }
        }

        return 0;
    }

    /// <summary>
    /// Phase 1 of the multi-file build: drives the <see cref="BuildDriver"/> to parse all source files,
    /// resolve imports, and produce the topologically-ordered user file list. Returns 0 on success.
    /// </summary>
    private static int RunPhase1BuildDriver(Phase1Context p1,
        out List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        out Dictionary<string, FileBuildUnit> unitsByFile, out BuildDriver driver,
        out IReadOnlyList<string> discoveredLinks)
    {
        string entryFile = p1.EntryFile, projectRoot = p1.ProjectRoot, stdlibRoot = p1.StdlibRoot;
        Language language = p1.Language;
        IReadOnlyList<string>? libraryRoots = p1.LibraryRoots;
        Func<Language, IReadOnlyList<string>, IReadOnlyDictionary<string, string>?>?
            stdlibIndexProvider = p1.StdlibIndexProvider;
        bool showBuildStages = p1.ShowBuildStages;
        Stopwatch? swBuild = p1.SwBuild;
        orderedFiles = [];
        unitsByFile =
            new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
        discoveredLinks = [];

        if (showBuildStages)
        {
            Console.WriteLine(value: "=== BUILD DRIVER ===");
        }

        // Daemon-cached stdlib import index (built once): lets the driver skip the ~0.8 s per-request
        // stdlib re-parse. Null on a cold build → the driver parses the stdlib as before.
        IReadOnlyDictionary<string, string>? cachedStdlibIndex =
            stdlibIndexProvider?.Invoke(arg1: language, arg2: libraryRoots ?? []);
        driver = new BuildDriver(projectRoot: projectRoot,
            stdlibRoot: stdlibRoot,
            language: language,
            libraryRoots: libraryRoots,
            cachedStdlibIndex: cachedStdlibIndex);
        BuildResult buildResult = driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

        if (swBuild != null)
        {
            Console.Error.WriteLine(
                value:
                $"[timing] build-driver (parse+module-resolve): {swBuild.ElapsedMilliseconds} ms");
            swBuild.Restart();
        }

        if (showBuildStages)
        {
            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");
        }

        if (buildResult.Errors.Count > 0)
        {
            Console.WriteLine();
            Console.Error.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
            DiagnosticRenderer.PrintAll(errors: buildResult.Errors);
            Console.WriteLine();
            Console.Error.WriteLine(value: "Build aborted due to errors.");
            return 1;
        }

        if (buildResult.Warnings.Count > 0)
        {
            Console.Error.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
            foreach (BuildWarning warning in buildResult.Warnings)
            {
                DiagnosticRenderer.Print(warning: warning);
            }
        }

        if (showBuildStages)
        {
            Console.WriteLine(
                value:
                $"Initialization order: {string.Join(separator: " -> ", values: buildResult.InitializationOrder)}");
        }

        List<FileBuildUnit> userUnits =
            FilterUserUnits(buildResult: buildResult, stdlibRoot: stdlibRoot);
        foreach (FileBuildUnit unit in userUnits)
        {
            unitsByFile[key: unit.FilePath] = unit;
        }

        orderedFiles = OrderUserFiles(userUnits: userUnits,
            initializationOrder: buildResult.InitializationOrder);
        discoveredLinks =
            CollectLinkLibraries(programs: orderedFiles.Select(selector: f => f.Program));
        return 0;
    }

    /// <summary>
    /// Verifies that a <c>routine start()</c> is present in the user programs (not stdlib). Returns
    /// 0 if found, 1 (with error printed) if missing.
    /// </summary>
    private static int CheckStartRoutinePresent(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles, AnalysisResult result)
    {
        var userFilePaths = orderedFiles.Select(selector: f => f.FilePath)
                                        .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
        bool hasStartRoutine = result.Registry
                                     .GetAllRoutines()
                                     .Any(predicate: r =>
                                          r.OwnerType == null &&
                                          (r.Name == "start" ||
                                           r.BaseName.EndsWith(value: ".start")) &&
                                          r.Location != null &&
                                          userFilePaths.Contains(item: r.Location.FileName));
        if (!hasStartRoutine)
        {
            Console.WriteLine();
            Console.WriteLine(value: "Error: executable target has no 'start' routine. " +
                                     "Add 'routine start()' or 'routine start!()' to the entry module, " +
                                     "or set the target type to 'library' in config.toml.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Phase 3 of the multi-file build: runs LLVM IR codegen over the analyzed programs, optionally
    /// dumps the AST, and writes the IR to the output file or invokes the IR callback
    /// configured in <paramref name="p3"/>. Returns 0 on success.
    /// </summary>
    private static int RunPhase3Codegen(Phase3Context p3,
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        Dictionary<string, FileBuildUnit> unitsByFile, AnalysisResult result)
    {
        string entryFile = p3.EntryFile;
        string? outputFile = p3.OutputFile;
        TargetConfig target = p3.Target;
        RfBuildMode buildMode = p3.BuildMode;
        bool saTiming = p3.SaTiming, dumpAst = p3.DumpAst, showBuildStages = p3.ShowBuildStages;
        Action<string>? irCallback = p3.IrCallback;
        Stopwatch? swPhase = p3.SwPhase;
        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== CODE GENERATION ===");
        }

        var userPrograms = orderedFiles.Select(selector: f =>
                                        {
                                            string module =
                                                unitsByFile.TryGetValue(key: f.FilePath,
                                                    value: out FileBuildUnit? u)
                                                    ? u.Module ?? ""
                                                    : "";
                                            return (f.Program, f.FilePath, module);
                                        })
                                       .ToList();

        List<(SyntaxTree.Program Program, string FilePath, string Module)> stdlibPrograms =
            result.Registry.StdlibPrograms;

        // 9-2: instrument may-suspend routine bodies with cancellation push/pop markers
        // (no-op unless something reaches a coroutine suspend point). Mutates the userPrograms
        // ASTs in place — the same objects codegen consumes below.
        Builder.Lowering.Passes.CancellationInstrumentationPass.Run(programs: userPrograms,
            instantiatedBodies: result.InstantiatedGenericBodies,
            maySuspendKeys: result.MaySuspendRoutineKeys,
            registry: result.Registry);

        // The entry module (manifest executable) is the module declared by the entry file —
        // it, not an arbitrary imported module's `start`, is the program entry point.
        string entryFull = Path.GetFullPath(path: entryFile);
        string? entryModule =
            unitsByFile.TryGetValue(key: entryFull, value: out FileBuildUnit? entryUnit)
                ? entryUnit.Module
                : null;

        var generator = new LlvmEmitter(userPrograms: userPrograms,
            registry: result.Registry,
            options: new LlvmEmitterOptions
            {
                StdlibPrograms = stdlibPrograms,
                Target = target,
                BuildMode = buildMode,
                SynthesizedBodies = result.SynthesizedBodies,
                InstantiatedGenericBodies = result.InstantiatedGenericBodies,
                LiveRoutineKeys = result.LiveRoutineKeys,
                MaySuspendRoutineKeys = result.MaySuspendRoutineKeys
            }) { Timing = saTiming, EntryModule = entryModule };

        // dump-ast dumps the EXACT AST that LLVM codegen consumes — captured immediately BEFORE
        // Generate(), after all desugaring/monomorphization + the final CancellationInstrumentation
        // mutation. Codegen is a pure translator, so this snapshot fully defines its input.
        if (dumpAst)
        {
            string astPath = Path.ChangeExtension(path: entryFile, extension: ".rf.desugared");
            string astText = new RfSyntaxTreePrinter().PrintMultiProgram(programs: userPrograms,
                synthesizedBodies: result.SynthesizedBodies,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                instantiatedGenericBodies: result.InstantiatedGenericBodies);
            File.WriteAllText(path: astPath, contents: astText);
            if (showBuildStages)
            {
                Console.WriteLine(value: $"Codegen-input AST written to: {astPath}");
            }
        }

        string llvmIr = generator.Generate();
        if (swPhase != null)
        {
            Console.Error.WriteLine(
                value:
                $"[phase] codegen Generate(): {swPhase.ElapsedMilliseconds} ms ({llvmIr.Length} chars, {generator.EmittedRoutineCount} routines)");
        }

        if (showBuildStages)
        {
            Console.Error.WriteLine(value: $"Routines emitted: {generator.EmittedRoutineCount}");
        }

        // Output. The JIT path (irCallback set) takes the IR IN MEMORY — no temp .ll write + read-back.
        if (irCallback != null)
        {
            irCallback(obj: llvmIr);
        }
        else
        {
            string outPath = outputFile ?? Path.ChangeExtension(path: entryFile, extension: ".ll");
            File.WriteAllText(path: outPath, contents: llvmIr);
            if (showBuildStages)
            {
                Console.WriteLine(value: $"LLVM IR written to: {outPath}");
            }
        }

        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "Build successful!");
        }

        return 0;
    }

    /// <summary>
    /// Runs the multi-file build pipeline through semantic analysis only (no codegen).
    /// Reports errors and warnings. Returns 0 if type-checking succeeds, 1 otherwise.
    /// </summary>
    private static int CheckMultiFile(string entryFile, string? projectRoot = null,
        IReadOnlyList<string>? libraryRoots = null)
    {
        if (!File.Exists(path: entryFile))
        {
            Console.WriteLine(value: $"Error: File '{entryFile}' not found.");
            return 1;
        }

        bool isSuflae = IsSuflaeSource(path: entryFile);
        Language language = isSuflae
            ? Language.Suflae
            : Language.RazorForge;

        Console.WriteLine(
            value:
            $"Checking {entryFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)} (multi-file)...");
        Console.WriteLine();

        try
        {
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language,
                libraryRoots: libraryRoots);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

            if (buildResult.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(
                    value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: buildResult.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (buildResult.Warnings.Count > 0)
            {
                Console.Error.WriteLine(value: $"Warnings: {buildResult.Warnings.Count}");
                foreach (BuildWarning warning in buildResult.Warnings)
                {
                    DiagnosticRenderer.Print(warning: warning);
                }
            }

            // Filter out stdlib files, then order the user files in initialization order.
            List<FileBuildUnit> userUnits =
                FilterUserUnits(buildResult: buildResult, stdlibRoot: stdlibRoot);
            List<(SyntaxTree.Program Program, string FilePath)> orderedFiles =
                OrderUserFiles(userUnits: userUnits,
                    initializationOrder: buildResult.InitializationOrder);

            // Phase 2: Semantic analysis (multi-file) -> no codegen
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var analyzer = new SemanticVerifier(language: language);
            // Share the driver's fully-indexed resolver so SA-phase imports see the same
            // module set the build graph resolved (incl. [target] library directories).
            analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);

            Console.WriteLine(
                value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

            if (result.Errors.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== ERRORS ({result.Errors.Count}) ===");
                DiagnosticRenderer.PrintAll(errors: result.Errors);

                Console.WriteLine();
                Console.Error.WriteLine(value: "Check failed due to errors.");
                return 1;
            }

            if (result.Warnings.Count > 0)
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: $"=== WARNINGS ({result.Warnings.Count}) ===");
                DiagnosticRenderer.PrintAll(warnings: result.Warnings);
            }

            Console.WriteLine();
            Console.WriteLine(value: "Check passed!");
            return 0;
        }
        catch (GrammarException ex)
        {
            DiagnosticRenderer.Print(ex: ex);
            return 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Check failed: {ex.Message}");
            Console.WriteLine(value: ex.StackTrace);
            return 1;
        }
    }

    /// <summary>Name of the synthesized per-program entity that holds every Suflae module-level
    /// <c>global</c> as a field. Routing all globals through ONE <c>entity</c> behind a hidden
    /// <c>Roamed</c> singleton makes them thread-safe on the M:N worker pool: the per-statement
    /// access-lock brackets (RoamedLockBracketLoweringPass) serialize concurrent field RMW, and
    /// atomic-width scalar fields additionally get a lock-free <c>atomicrmw</c> fast-path on the field
    /// address. The singleton is constructed and promoted at the top of <c>start()</c>.</summary>
    internal const string ModuleGlobalsEntityName = "__ModuleGlobals";

    /// <summary>Name of the hidden singleton holding the one <see cref="ModuleGlobalsEntityName"/>
    /// instance. Every module-level global access <c>g</c> is rewritten to <c>__globals__.g</c> by
    /// GlobalEntityRewritePass.</summary>
    internal const string ModuleGlobalsSingletonName = "__globals__";

    /// <summary>
    /// Suflae <c>global</c> eager initialization + thread-safe storage synthesis. Each module-level
    /// <c>global name: T = init</c> becomes a FIELD of one hidden per-program entity
    /// <see cref="ModuleGlobalsEntityName"/>, stored behind a single <c>Roamed</c> singleton
    /// <see cref="ModuleGlobalsSingletonName"/> (constructed + promoted at the top of <c>start()</c>).
    /// Field initializers run in dependency order (a global's init may read another global; a cycle —
    /// including self-reference, including through a free-routine call — is RF-S436). Initializers that
    /// touch no other global are folded straight into the singleton constructor; a dependent initializer
    /// runs as an ordered field assignment AFTER construction so its read sees the already-initialized
    /// field. The original <c>global</c> declarations are kept (init stripped) ONLY so semantic analysis
    /// registers them and stamps <c>IdentifierExpression.IsModuleGlobal</c> on every reference;
    /// GlobalEntityRewritePass then deletes them and rewrites each stamped reference to
    /// <c>__globals__.field</c>.
    /// </summary>
    private static bool InjectGlobalInitializers(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles)
    {
        // 1) Collect globals (in encounter order) and strip their initializers off the declarations. The
        //    (now init-less) declaration is kept so SA still registers the global for reference resolution.
        var collected =
            new List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>();
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            List<ISyntaxTreeNode> decls = program.Declarations;
            for (int i = 0; i < decls.Count; i++)
            {
                if (decls[index: i] is VariableDeclaration
                    {
                        IsGlobal: true, Initializer: not null, Type: not null
                    } g)
                {
                    collected.Add(item: (g.Name, g.Type, g.Initializer, g.Location));
                    decls[index: i] = g with { Initializer = null };
                }
            }
        }

        if (collected.Count == 0)
        {
            return true;
        }

        List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> globals =
            DeduplicateGlobals(collected: collected);
        int n = globals.Count;
        List<HashSet<int>> deps =
            ComputeGlobalDependencies(orderedFiles: orderedFiles, globals: globals);
        List<int> order = KahnOrder(deps: deps, n: n);

        if (order.Count != n)
        {
            IEnumerable<string> cyclic = Enumerable.Range(start: 0, count: n)
                                                   .Where(predicate: i =>
                                                        !order.Contains(value: i))
                                                   .Select(selector: i => globals[index: i].Name);
            Console.Error.WriteLine(
                value:
                "error[RF-S436]: circular global initialization — these globals reference each " +
                $"other (directly) before they are initialized: {string.Join(separator: ", ", values: cyclic)}. " +
                "A global's initializer may only reference globals it does not (transitively) depend on.");
            return false;
        }

        if (!TryBuildModuleGlobalsSynthesis(globals: globals,
                deps: deps,
                order: order,
                entityDecl: out EntityDeclaration entityDecl,
                singletonDecl: out VariableDeclaration singletonDecl,
                initStmts: out List<Statement> initStmts))
        {
            return false;
        }

        return SpliceGlobalsIntoStart(orderedFiles: orderedFiles,
            entityDecl: entityDecl,
            singletonDecl: singletonDecl,
            initStmts: initStmts);
    }

    /// <summary>Deduplicates collected globals by name (last-write-wins) while preserving first-seen order
    /// for a stable field layout.</summary>
    private static List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>
        DeduplicateGlobals(
            List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>
                collected)
    {
        var seenOrder = new List<string>();
        var latest =
            new Dictionary<string, (TypeExpression Type, Expression Init, SourceLocation Loc)>(
                comparer: StringComparer.Ordinal);
        foreach ((string name, TypeExpression type, Expression init, SourceLocation loc) in
                 collected)
        {
            if (!latest.ContainsKey(key: name))
            {
                seenOrder.Add(item: name);
            }

            latest[key: name] = (type, init, loc);
        }

        return seenOrder.Select(selector: name => (Name: name, latest[key: name].Type,
                             latest[key: name].Init, latest[key: name].Loc))
                        .ToList();
    }

    /// <summary>Builds the synthesized entity declaration, singleton declaration, and the init-statement
    /// list that gets prepended to <c>start()</c>. Returns false (with error printed) if a dependent field
    /// has a type with no synthesizable default (RF-S437).</summary>
    private static bool TryBuildModuleGlobalsSynthesis(
        List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> globals,
        List<HashSet<int>> deps, List<int> order, out EntityDeclaration entityDecl,
        out VariableDeclaration singletonDecl, out List<Statement> initStmts)
    {
        int n = globals.Count;
        SourceLocation loc0 = globals[index: 0].Loc;

        // 3) Build the __ModuleGlobals entity — one field per global (`name: Type`, no initializer).
        var fieldDecls = new List<SyntaxTree.Declaration>(capacity: n);
        for (int i = 0; i < n; i++)
        {
            fieldDecls.Add(item: new VariableDeclaration(Name: globals[index: i].Name,
                Type: globals[index: i].Type,
                Initializer: null,
                Visibility: VisibilityModifier.Open,
                Location: globals[index: i].Loc));
        }

        entityDecl = new EntityDeclaration(Name: ModuleGlobalsEntityName,
            GenericParameters: null,
            Protocols: new List<TypeExpression>(),
            Members: fieldDecls,
            Visibility: VisibilityModifier.Open,
            Location: loc0);

        // 4) Constructor arguments: independent fields use their real initializer; dependent fields
        //    are seeded with a type default and get their real value from a post-construction assignment.
        var ctorArgs = new List<(string Name, Expression Value)>(capacity: n);
        for (int i = 0; i < n; i++)
        {
            Expression value;
            if (deps[index: i].Count == 0)
            {
                value = globals[index: i].Init;
            }
            else
            {
                LiteralExpression? def = DefaultInitializerFor(type: globals[index: i].Type,
                    loc: globals[index: i].Loc);
                if (def == null)
                {
                    Console.Error.WriteLine(
                        value: $"error[RF-S437]: the global '{globals[index: i].Name}: " +
                               $"{globals[index: i].Type.Name}' has an initializer that depends on another " +
                               $"global, but dependent initialization is only supported for scalar/Text/Bool " +
                               $"types. Initialize it from a constant instead.");
                    singletonDecl = null!;
                    initStmts = null!;
                    return false;
                }

                value = def;
            }

            ctorArgs.Add(item: (globals[index: i].Name, value));
        }

        var construct = new CreatorExpression(TypeName: ModuleGlobalsEntityName,
            TypeArguments: null,
            MemberVariables: ctorArgs,
            Location: loc0);

        // 5) Init statements: construct + promote singleton, then ordered assignments for dependent globals.
        initStmts = new List<Statement>(capacity: n + 1)
        {
            new AssignmentStatement(
                Target: new IdentifierExpression(Name: ModuleGlobalsSingletonName,
                    Location: loc0),
                Value: construct,
                Location: loc0) { IsGlobalInit = true }
        };
        foreach (int i in order)
        {
            if (deps[index: i].Count == 0)
            {
                continue; // independent — already set by the constructor
            }

            initStmts.Add(item: new AssignmentStatement(
                Target: new IdentifierExpression(Name: globals[index: i].Name,
                    Location: globals[index: i].Loc),
                Value: globals[index: i].Init,
                Location: globals[index: i].Loc));
        }

        // 6) The singleton declaration — an entity `global` stored behind a promoted Roamed[E] handle.
        singletonDecl = new VariableDeclaration(Name: ModuleGlobalsSingletonName,
            Type: new TypeExpression(Name: ModuleGlobalsEntityName,
                GenericArguments: null,
                Location: loc0),
            Initializer: null,
            Visibility: VisibilityModifier.Open,
            Location: loc0,
            IsGlobal: true);
        return true;
    }

    /// <summary>Splices the synthesized entity/singleton declarations and init statements into the first
    /// program that contains <c>routine start()</c>. Returns false (with error printed) if none is found
    /// (RF-S438).</summary>
    private static bool SpliceGlobalsIntoStart(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        EntityDeclaration entityDecl, VariableDeclaration singletonDecl, List<Statement> initStmts)
    {
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            BlockStatement? startBlock = null;
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is RoutineDeclaration { Name: "start", Body: BlockStatement block })
                {
                    startBlock = block;
                    break;
                }
            }

            if (startBlock == null)
            {
                continue;
            }

            // Append (NOT prepend) the synthesized declarations: imports must stay at the top of the
            // file (RF-S114). Declaration order does not matter for type collection.
            program.Declarations.Add(item: entityDecl);
            program.Declarations.Add(item: singletonDecl);
            startBlock.Statements.InsertRange(index: 0, collection: initStmts);
            return true;
        }

        Console.Error.WriteLine(
            value:
            "error[RF-S438]: module-level 'global' declarations require a 'routine start()' entry " +
            "point to host their initialization.");
        return false;
    }

    /// <summary>Computes, for each global, the set of OTHER globals it (transitively) depends on. A global's
    /// initializer reading another global is a dependency; a free-routine call is followed once into the
    /// callee's body so a hidden read (`global a = compute()` where `compute` reads `b`) counts too.
    /// (Member-routine calls are not followed — a global read hidden behind `x.foo()` is the residual.)</summary>
    private static List<HashSet<int>> ComputeGlobalDependencies(
        List<(SyntaxTree.Program Program, string FilePath)> orderedFiles,
        List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)> globals)
    {
        int n = globals.Count;
        var nameToIdx = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            nameToIdx[key: globals[index: i].Name] = i; // last decl of a dup name wins
        }

        // Index every free routine's body by bare name so the dependency scan can follow calls.
        var routineBodies = new Dictionary<string, Statement>(comparer: StringComparer.Ordinal);
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is RoutineDeclaration { Body: { } body } r)
                {
                    routineBodies[key: r.Name] = body;
                }
            }
        }

        var deps = new List<HashSet<int>>(capacity: n);
        for (int i = 0; i < n; i++)
        {
            deps.Add(item: ComputeSingleGlobalDeps(init: globals[index: i].Init,
                nameToIdx: nameToIdx,
                routineBodies: routineBodies));
        }

        return deps;
    }

    /// <summary>BFS over AST expressions from the given initializer; collects indices of globals this
    /// global directly or transitively depends on by following free-routine call bodies one level.</summary>
    private static HashSet<int> ComputeSingleGlobalDeps(Expression init,
        Dictionary<string, int> nameToIdx, Dictionary<string, Statement> routineBodies)
    {
        var d = new HashSet<int>();
        var visitedRoutines = new HashSet<string>(comparer: StringComparer.Ordinal);
        var toScan = new Queue<object>();
        toScan.Enqueue(item: init);
        while (toScan.Count > 0)
        {
            object root = toScan.Dequeue();
            AstWalker.WalkExpressions(root: root,
                visit: e =>
                {
                    if (e is IdentifierExpression id &&
                        nameToIdx.TryGetValue(key: id.Name, value: out int j))
                    {
                        d.Add(item: j);
                    }

                    // Follow a call into the callee's body once (transitive hidden dependency).
                    if (e is CallExpression { Callee: IdentifierExpression callee } &&
                        routineBodies.TryGetValue(key: callee.Name,
                            value: out Statement? calleeBody) &&
                        visitedRoutines.Add(item: callee.Name))
                    {
                        toScan.Enqueue(item: calleeBody);
                    }
                });
        }

        return d;
    }

    /// <summary>Kahn's topological sort over the dependency edges (dependency j before dependent i), stable
    /// in source order among ready nodes. A returned order shorter than <paramref name="n"/> signals a cycle
    /// (the caller reports the un-ordered globals as RF-S436).</summary>
    private static List<int> KahnOrder(List<HashSet<int>> deps, int n)
    {
        int[] indegree = new int[n];
        for (int i = 0; i < n; i++)
        {
            indegree[i] += deps[index: i]
               .Count(predicate: j => j != i); // edge j -> i (dependency j before dependent i)
        }

        var order = new List<int>(capacity: n);
        bool ready;
        do
        {
            ready = KahnSweep(deps: deps,
                indegree: indegree,
                n: n,
                order: order);
        } while (ready);

        return order;
    }

    /// <summary>Single Kahn sweep: enqueues all zero-indegree nodes into <paramref name="order"/> and
    /// decrements their dependents' indegrees. Returns true if at least one node was emitted.</summary>
    private static bool KahnSweep(List<HashSet<int>> deps, int[] indegree, int n,
        List<int> order)
    {
        bool any = false;
        for (int i = 0; i < n; i++)
        {
            if (indegree[i] != 0)
            {
                continue;
            }

            indegree[i] = -1; // consumed
            order.Add(item: i);
            any = true;
            for (int k = 0; k < n; k++)
            {
                if (k != i && deps[index: k]
                       .Contains(item: i))
                {
                    indegree[k]--;
                }
            }
        }

        return any;
    }

    /// <summary>A build-time default value for a <c>global</c> whose initializer depends on another
    /// global (so its real value is assigned after the singleton is constructed). A bare integer literal
    /// <c>0</c> conforms to any numeric type (int/float/decimal) via RF-S767; Text and Bool have their
    /// own empty/false defaults. Returns null for a type with no synthesizable default.</summary>
    private static LiteralExpression? DefaultInitializerFor(TypeExpression type,
        SourceLocation loc)
    {
        return type.Name switch
        {
            "Text" => new LiteralExpression(Value: "",
                LiteralType: TokenType.TextLiteral,
                Location: loc),
            "Bool" => new LiteralExpression(Value: false,
                LiteralType: TokenType.False,
                Location: loc),
            "S8" or "S16" or "S32" or "S64" or "S128" or "S256" or "U8" or "U16" or "U32" or "U64"
                or "U128" or "U256" or "F16" or "F32" or "F64" or "F128" or "F256" or "Decimal"
                or "D32" or "D64" or "D128" or "Integer" => new LiteralExpression(Value: "0",
                    LiteralType: TokenType.UndecidedInteger,
                    Location: loc),
            _ => null
        };
    }

    private static List<string> CollectLinkLibraries(IEnumerable<SyntaxTree.Program> programs)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (SyntaxTree.Program prog in programs)
        {
            foreach (ISyntaxTreeNode decl in prog.Declarations)
            {
                VisitLinkDeclaration(node: decl, libs: libs, seen: seen);
            }
        }

        return libs;
    }

    private static void ScanLinkAnnotations(List<string>? annotations, List<string> libs,
        HashSet<string> seen)
    {
        if (annotations == null)
        {
            return;
        }

        foreach (string ann in annotations)
        {
            (string? lib, string? _) = TypeModel.Symbols.LinkAnnotation.Parse(annotation: ann);
            if (lib != null && seen.Add(item: lib))
            {
                libs.Add(item: lib);
            }
        }
    }

    private static void VisitLinkDeclaration(ISyntaxTreeNode node, List<string> libs,
        HashSet<string> seen)
    {
        switch (node)
        {
            case RoutineDeclaration r:
                ScanLinkAnnotations(annotations: r.Annotations, libs: libs, seen: seen); break;
            case ExternalDeclaration e:
                ScanLinkAnnotations(annotations: e.Annotations, libs: libs, seen: seen); break;
            case ExternalBlockDeclaration b:
                foreach (SyntaxTree.Declaration d in b.Declarations)
                {
                    VisitLinkDeclaration(node: d, libs: libs, seen: seen);
                }

                break;
        }
    }

    /// <summary>
    /// Compiles a source file all the way to LLVM-IR TEXT (no opt/clang/link), returning the IR string.
    /// This is the front half of <see cref="BuildExecutable"/> — used by the ORC-JIT dev-loop path, which
    /// JITs the IR in-process instead of producing a native exe. Honors a warm-stdlib provider so a daemon
    /// can supply the fast path. Returns the build exit code (0 = success, and <paramref name="ir"/> holds
    /// the module); diagnostics are printed by <see cref="BuildMultiFile"/> as usual.
    /// </summary>
    private static int BuildToIr(string entryFile, out string ir, ResolvedEntry config,
        WarmProviders? warm = null)
    {
        string captured = "";
        int rc = BuildMultiFile(entryFile: entryFile,
            outputFile: null,
            discoveredLinkLibraries: out _,
            config: config,
            warm: new WarmProviders(WarmProvider: warm?.WarmProvider,
                IrCallback: s => captured = s,
                StdlibIndexProvider: warm?.StdlibIndexProvider));
        ir = captured;
        return rc;
    }

    /// <summary>
    /// Test-only in-process compile-to-IR entry. Resolves <paramref name="entryFile"/>'s manifest
    /// exactly as the <c>codegen</c> verb does, then runs the FULL front pipeline (tokenize → parse →
    /// declaration → desugaring → collection → verification → instantiation → codegen) WITHOUT
    /// opt/clang/link and WITHOUT executing the produced program, returning the build exit code
    /// (0 = success) and the emitted IR. This lets the test suite exercise the codegen/desugaring/
    /// collection/instantiation stack IN-PROCESS — a subprocess <c>buildandrun</c> runs that stack in
    /// a child process, invisible to coverage instrumentation. Diagnostics are printed to the console
    /// by <see cref="BuildMultiFile"/> as usual; the caller may redirect the console to capture them.
    /// </summary>
    internal static int CompileEntryToIrForTests(string entryFile, out string ir)
    {
        ResolvedEntry resolved =
            ResolveEntryFile(args: ["codegen", entryFile], needsOutputArg: false);
        if (resolved.EntryFile == null)
        {
            ir = "";
            return 1;
        }

        return BuildToIr(entryFile: resolved.EntryFile, ir: out ir, config: resolved);
    }

    private static int BuildExecutable(string entryFile, out string exeFile, ResolvedEntry config,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        IReadOnlyList<string> cLibraries = config.CLibraries;
        IReadOnlyList<string> libraryPaths = config.LibraryPaths;
        IReadOnlyDictionary<string, CLibrary> libraryConfigs = config.LibraryConfigs;

        // Remove stale per-target outputs before rebuilding.
        string llFile = Path.ChangeExtension(path: entryFile, extension: ".ll");
        string optFile = Path.ChangeExtension(path: llFile, extension: ".opt.ll");
        exeFile = Path.ChangeExtension(path: llFile, extension: ".exe");
        NativeToolchain.CleanBuildAndRunOutputs(llFile: llFile,
            optFile: optFile,
            exeFile: exeFile);

        // Build first (to a temp .ll file). BuildMultiFile also reports any `@link(...)` C libraries
        // declared in the compiled source.
        int buildResult = BuildMultiFile(entryFile: entryFile,
            outputFile: llFile,
            discoveredLinkLibraries: out IReadOnlyList<string> discoveredLinks,
            config: config,
            warm: warmProvider != null
                ? new WarmProviders(WarmProvider: warmProvider,
                    IrCallback: null,
                    StdlibIndexProvider: null)
                : null);
        if (buildResult != 0)
        {
            return buildResult;
        }

        // Merge manifest [target] c_libraries with source `@link(...)` directives (manifest first),
        // de-duplicated, for the link step. A source `@link(lib: "X")` name is remapped through a
        // [libraries.X] declaration's `name` override (e.g. "SDL2" → "SDL2-2.0") when present, so the
        // real `-l` link name is used. The declared libraries' own names are also linked.
        var allCLibraries = new List<string>();

        void AddLib(string lib)
        {
            string resolved = libraryConfigs.TryGetValue(key: lib, value: out CLibrary? cfg)
                ? cfg.Name
                : lib;
            if (!allCLibraries.Contains(item: resolved))
            {
                allCLibraries.Add(item: resolved);
            }
        }

        foreach (string lib in cLibraries)
        {
            AddLib(lib: lib);
        }

        foreach (CLibrary cfg in libraryConfigs.Values)
        {
            AddLib(lib: cfg.Name);
        }

        foreach (string lib in discoveredLinks)
        {
            AddLib(lib: lib);
        }

        return LinkAndStageExecutable(exeFile: exeFile,
            llFile: llFile,
            optFile: optFile,
            buildMode: config.BuildMode,
            allCLibraries: allCLibraries,
            libraryPaths: libraryPaths,
            libraryConfigs: libraryConfigs);
    }

    /// <summary>
    /// Optimizes, links, and stages the runtime DLLs after code generation, factored out of
    /// <see cref="BuildExecutable"/> to reduce its cognitive complexity.
    /// </summary>
    private static int LinkAndStageExecutable(string exeFile, string llFile, string optFile,
        RfBuildMode buildMode, List<string> allCLibraries, IReadOnlyList<string> libraryPaths,
        IReadOnlyDictionary<string, CLibrary> libraryConfigs)
    {
        string exeDir;
        string runtimeLibDir;
        try
        {
            exeDir = NativeToolchain.ResolveExecutableDirectory();
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to resolve native runtime layout: {ex.Message}");
            return 1;
        }

        if (NativeToolchain.TryFindNativeBuildDirectory(exeDir: exeDir,
                nativeBuildDir: out string nativeBuildDir))
        {
            // Development checkout: rebuild the native runtime incrementally before linking.
            int nativeResult = NativeToolchain.BuildNativeRuntime(exeDir: exeDir,
                nativeBuildDir: nativeBuildDir);
            if (nativeResult != 0)
            {
                return nativeResult;
            }

            runtimeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");
        }
        else if (File.Exists(path: Path.Combine(path1: exeDir,
                     path2: NativeToolchain.RuntimeLinkLibraryFileName)))
        {
            // Installed/published layout: prebuilt runtime artifacts ship flat next to the
            // executable (csproj LinkBase="." / the packaging scripts) — nothing to rebuild.
            runtimeLibDir = exeDir;
        }
        else
        {
            Console.WriteLine(
                value:
                $"Failed to resolve the RazorForge native runtime: expected either a development 'native/build' tree near the executable, or '{NativeToolchain.RuntimeLinkLibraryFileName}' next to it (installed layout).");
            return 1;
        }

        // Optimize the emitted IR, then link it into a native executable.
        int optResult =
            NativeToolchain.OptimizeIr(llFile: llFile, optFile: optFile, buildMode: buildMode);
        if (optResult != 0)
        {
            return optResult;
        }

        int linkResult = NativeToolchain.LinkExecutable(optFile: optFile,
            exeFile: exeFile,
            runtimeLibDir: runtimeLibDir,
            buildMode: buildMode,
            cLibraries: allCLibraries,
            libraryPaths: libraryPaths);
        if (linkResult != 0)
        {
            return linkResult;
        }

        // Copy the runtime DLL (and its shared-library dependencies) next to the
        // output .exe so the loader can find them at runtime.
        NativeToolchain.StageRuntimeDlls(exeDir: exeDir, exeFile: exeFile);
        // Also stage each dynamically-linked @link/c_libraries dependency DLL from the -L search paths,
        // so a freshly-built exe runs without the user hand-copying its foreign libraries.
        NativeToolchain.StageUserLibraryDlls(exeFile: exeFile,
            cLibraries: allCLibraries,
            libraryPaths: libraryPaths,
            libraryConfigs: libraryConfigs);
        return 0;
    }

    private static int BuildAndRun(string entryFile, ResolvedEntry config,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        int buildResult = BuildExecutable(entryFile: entryFile,
            exeFile: out string exeFile,
            config: config,
            warmProvider: warmProvider);
        if (buildResult != 0)
        {
            return buildResult;
        }

        return RunExecutable(exeFile: exeFile, showBuildStages: config.ShowBuildStages);
    }

    /// <summary>
    /// Runs an already-built native executable, forwarding stdin and faithfully draining stdout/stderr
    /// (UTF-8), and returns its exit code. Factored out of <see cref="BuildAndRun"/> so the daemon client
    /// can run the exe locally after a warm build performed the compile in the daemon process.
    /// </summary>
    private static int RunExecutable(string exeFile, bool showBuildStages)
    {
        if (showBuildStages)
        {
            Console.WriteLine();
            Console.WriteLine(value: "=== EXECUTION ===");
        }

        bool stdinIsPiped = Console.IsInputRedirected;
        var psi = new ProcessStartInfo
        {
            FileName = Path.GetFullPath(path: exeFile),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinIsPiped,
            // RF programs write UTF-8 (rf_runtime_init sets SetConsoleOutputCP(65001) on
            // Windows and stdlib paths encode every Text via UTF-8). Without these explicit
            // encodings, .NET's StreamReader defaults to the parent's Console.OutputEncoding
            // (system ACP — CP949 / CP1252 / etc. depending on locale) and rewrites every
            // non-ACP byte as `?`, garbling all non-ASCII output (Korean, emoji, accented
            // Latin, …). Setting these to UTF-8 makes the readers byte-faithful.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                Console.WriteLine(value: "Error: Failed to start the compiled executable.");
                return 1;
            }

            // Forward our stdin to the child CONCURRENTLY — never synchronously before draining
            // the child's output. When this process's own stdin is a redirected pipe that never
            // reaches EOF (the common in-harness / CI case), a synchronous CopyTo blocks forever:
            // it waits for our stdin to end while the child fills its stdout pipe with nobody
            // draining it, so both sides wedge (the long-standing "buildandrun stalls in harness"
            // bug). On a background task the copy can't stall the output drain; it ends when our
            // stdin closes or the child's stdin pipe does. It's a background thread, so a copy that
            // never completes (parent stdin held open) does not keep the process alive.
            if (stdinIsPiped)
            {
                _ = Task.Run(action: () =>
                {
                    try
                    {
                        Console.OpenStandardInput()
                               .CopyTo(destination: process.StandardInput.BaseStream);
                        process.StandardInput.Close();
                    }
                    catch
                    {
                        // Child exited / its stdin pipe closed — nothing left to forward.
                    }
                });
            }

            // Drain stdout and stderr CONCURRENTLY. Reading them sequentially (all of stdout,
            // then all of stderr) deadlocks whenever the child fills the OS stderr pipe buffer
            // while we are still blocked on stdout: the child blocks writing stderr, we block
            // reading stdout, and neither side progresses. Kicking off both async reads first
            // keeps both pipes draining continuously.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            string stdout = stdoutTask.GetAwaiter()
                                      .GetResult();
            string stderr = stderrTask.GetAwaiter()
                                      .GetResult();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(value: stdout))
            {
                Console.Write(value: stdout);
            }

            if (!string.IsNullOrEmpty(value: stderr))
            {
                Console.Error.Write(value: stderr);
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute {exeFile}: {ex.Message}");
            return 1;
        }
    }
}
