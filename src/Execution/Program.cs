using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Compiler.CodeGen;
using Compiler.Declaration;
using Compiler.Diagnostics;
using Compiler.Tokenizer;
using Compiler.Parser;
using Compiler.Targeting;
using Verification;
using Verification.Results;
using SyntaxTree;
using TypeModel.Enums;

namespace Builder;

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
    /// <see cref="Compiler.Resolution.BuildInfo"/>). Bump it in the csproj, NOT here.</summary>
    private static string SuflaeVersion => Compiler.Resolution.BuildInfo.SuflaeVersion;

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

        // Check if first arg is a command or a file
        bool isCommand = command is "parse" or "tokenize" or "codegen" or BuildCommand or "buildandrun" or "check" or "validate-stdlib" or "help" or "version" or "v";

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

        if (!isCommand)
        {
            // A bare source file RUNS (build + execute) when it is a Suflae script — either the `.sf`
            // extension or invocation under the `suflae`/`sf` alias — so `suflae hello.sf` behaves like
            // `python hello.py`. A bare `.rf` under `razorforge` keeps the dev default of parse-and-dump
            // (use the explicit `parse`/`tokenize`/`codegen` verbs to inspect an .sf without running it).
            if (InvokedAsSuflae || IsSuflaeFile(path: args[0]))
            {
                var forwarded = new string[args.Length + 1];
                forwarded[0] = BuildAndRunCommand;
                Array.Copy(sourceArray: args, sourceIndex: 0, destinationArray: forwarded,
                    destinationIndex: 1, length: args.Length);
                args = forwarded;
                command = BuildAndRunCommand;
            }
            else
            {
                // Default behavior: parse the file
                return ParseFile(sourceFile: args[0]);
            }
        }

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
            {
                // `build` compiles all the way to a native executable for the HOST OS
                // (codegen -> opt -> link -> stage runtime DLLs) but does NOT run it. The
                // intermediate <entry>.ll / .opt.ll are kept as byproducts for inspection;
                // `codegen` remains the IR-only verb. (All-OS artifacts come from the release CI.)
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
                    projectRoot: resolved.ProjectRoot,
                    buildMode: resolved.BuildMode,
                    dumpAst: resolved.DumpAst,
                    saTiming: resolved.SaTiming,
                    requireStartRoutine: resolved.RequireStartRoutine,
                    showBuildStages: resolved.ShowBuildStages,
                    libraryRoots: resolved.LibraryRoots,
                    cLibraries: resolved.CLibraries,
                    libraryPaths: resolved.LibraryPaths,
                    libraryConfigs: resolved.LibraryConfigs);
                if (buildRc == 0)
                {
                    Console.WriteLine(value: $"Executable written to: {Path.GetFullPath(path: builtExe)}");
                }

                return buildRc;
            }

            case "buildandrun":
            {
                ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
                if (resolved.EntryFile == null)
                {
                    return 1;
                }

                // ORC-JIT dev-loop path (RAZORFORGE_JIT=1): JIT the module in-process — no opt/clang/link,
                // no exe, no spawn. IR comes warm from the daemon when it's up, else a local cold compile.
                if (CompileDaemon.TryClientJitRun(resolved: resolved, exitCode: out int jrc))
                {
                    return jrc;
                }

                // Warm-daemon path: delegate the COMPILE to a running daemon (skips stdlib reprocessing),
                // then run the produced exe locally so interactive stdin/stdout stays with this process.
                if (CompileDaemon.TryClientBuildAndRun(resolved: resolved, exitCode: out int drc))
                {
                    return drc;
                }

                return BuildAndRun(entryFile: resolved.EntryFile,
                    projectRoot: resolved.ProjectRoot,
                    buildMode: resolved.BuildMode,
                    dumpAst: resolved.DumpAst,
                    saTiming: resolved.SaTiming,
                    requireStartRoutine: resolved.RequireStartRoutine,
                    showBuildStages: resolved.ShowBuildStages,
                    libraryRoots: resolved.LibraryRoots,
                    cLibraries: resolved.CLibraries,
                    libraryPaths: resolved.LibraryPaths,
                    libraryConfigs: resolved.LibraryConfigs);
            }

            case "check":
            {
                ResolvedEntry resolved = ResolveEntryFile(args: args, needsOutputArg: false);
                if (resolved.EntryFile == null)
                {
                    return 1;
                }

                return CheckMultiFile(entryFile: resolved.EntryFile, projectRoot: resolved.ProjectRoot,
                    libraryRoots: resolved.LibraryRoots);
            }

            case "validate-stdlib":
            {
                string lang = args.Length >= 2
                    ? args[1]
                       .ToLowerInvariant()
                    : (InvokedAsSuflae ? "sf" : "rf");
                Language stdlibLang = lang is "sf" or "suflae"
                    ? Language.Suflae
                    : Language.RazorForge;
                return ValidateStdlib(language: stdlibLang);
            }

            case "help":
                PrintUsage();
                return 0;

            default:
                PrintUsage();
                return 1;
        }
    }

    /// <summary>
    /// The fully-resolved build configuration for a <c>build</c>/<c>buildandrun</c>/<c>check</c> invocation:
    /// entry file, project root, build mode, and the external-library link config. Produced by
    /// <see cref="ResolveEntryFile"/> from the CLI args + the nearest <c>razorforge.toml</c>. A resolution
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
    }

    /// <summary>
    /// Resolves the <see cref="ResolvedEntry"/> for build/buildandrun/check commands.
    /// Searches for a razorforge.toml manifest in all cases: when no entry file is given the
    /// manifest supplies the executable; when an explicit entry file is given it overrides
    /// [target] executable but the manifest's other settings still apply.
    /// ALL build configuration lives in the manifest's [target] section (executable, library,
    /// mode, dump-ast, sa-timing, show-build-stages) — the CLI deliberately takes no flags.
    /// On error the returned entry's <see cref="ResolvedEntry.EntryFile"/> is null.
    /// </summary>
    private static ResolvedEntry ResolveEntryFile(string[] args, bool needsOutputArg) // NOSONAR S3776
    {
        // args[0] is the command name (build/buildandrun/check)
        string? explicitEntry = null;
        string? outputFile = null;

        // Parse remaining args (positional only: [entry-file] [out.ll])
        int i = 1;
        while (i < args.Length)
        {
            if (!args[i]
                   .StartsWith('-'))
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
                    $"Error: unknown option '{args[i]}'. RazorForge takes no build flags — configure builds in razorforge.toml ([target] executable, library, mode, ...).");
                return new ResolvedEntry();
            }
        }

        // Explicit source file given — use it as the entry point, but still honor the
        // nearest razorforge.toml (walking up from the file's directory): the manifest
        // remains the single source of build configuration (mode, library deps, debug
        // fields) even for single-file builds; only [target] executable is overridden.
        // .toml files are treated as manifests, not source files.
        if (explicitEntry != null &&
            !explicitEntry.EndsWith(value: ".toml", comparisonType: StringComparison.OrdinalIgnoreCase))
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
                return new ResolvedEntry
                {
                    EntryFile = explicitEntry, ProjectRoot = entryDir, OutputFile = outputFile,
                    RequireStartRoutine = true
                };
            }

            try
            {
                ProjectManifest manifest = ManifestLoader.Load(tomlPath: nearbyManifest,
                    resolveExecutable: false);
                BuildTarget target = manifest.Target;
                RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

                if (target.ShowBuildStages)
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

                return new ResolvedEntry
                {
                    EntryFile = explicitEntry, ProjectRoot = manifest.ManifestDirectory,
                    OutputFile = outputFile, BuildMode = buildMode, DumpAst = target.DumpAst,
                    SaTiming = target.SaTiming, RequireStartRoutine = true,
                    ShowBuildStages = target.ShowBuildStages, LibraryRoots = target.Libraries,
                    CLibraries = target.CLibraries, LibraryPaths = target.LibraryPaths,
                    LibraryConfigs = target.LibraryConfigs
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    value: $"Error loading {ManifestLoader.ManifestFileName}: {ex.Message}");
                return new ResolvedEntry();
            }
        }

        // No explicit entry (or .toml manifest given) — load manifest
        string? manifestPath = explicitEntry != null
            ? (File.Exists(path: explicitEntry)
                ? Path.GetFullPath(path: explicitEntry)
                : null)
            : ManifestLoader.FindManifest(startDir: Environment.CurrentDirectory);
        if (manifestPath == null)
        {
            if (explicitEntry != null)
            {
                Console.WriteLine(value: $"Error: Manifest '{explicitEntry}' not found.");
            }
            else
            {
                Console.WriteLine(
                    value: "Error: No entry file specified and no razorforge.toml found.");
                Console.WriteLine(
                    value: "Either provide an entry file or create a razorforge.toml manifest.");
            }

            return new ResolvedEntry();
        }

        try
        {
            ProjectManifest manifest = ManifestLoader.Load(tomlPath: manifestPath);
            BuildTarget target = manifest.Target;

            RfBuildMode buildMode = ParseBuildMode(mode: target.Mode);

            bool showBuildStages = target.ShowBuildStages;
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

            return new ResolvedEntry
            {
                EntryFile = target.Executable, ProjectRoot = manifest.ManifestDirectory,
                OutputFile = outputFile, BuildMode = buildMode, DumpAst = target.DumpAst,
                SaTiming = target.SaTiming, RequireStartRoutine = true, ShowBuildStages = showBuildStages,
                LibraryRoots = target.Libraries, CLibraries = target.CLibraries,
                LibraryPaths = target.LibraryPaths, LibraryConfigs = target.LibraryConfigs
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
            "debug" => RfBuildMode.Debug,
            "release" => RfBuildMode.Release,
            "release-time" => RfBuildMode.ReleaseTime,
            "release-space" => RfBuildMode.ReleaseSpace,
            _ => throw new InvalidOperationException(
                $"Unknown build mode '{mode}' in [target]. " +
                "Valid modes are: debug, release, release-time, release-space.")
        };
    }

    /// <summary>
    /// Prints the CLI usage instructions to standard output.
    /// </summary>
    private static void PrintUsage()
    {
        // The command name the user typed (the shipped `suflae`/`sf` aliases are copies of the
        // apphost), so examples echo how the tool was actually invoked.
        string tool = InvokedAsSuflae ? "suflae" : "razorforge";
        string header = InvokedAsSuflae
            ? $"{SuflaeLanguageName} v{SuflaeVersion}"
            : $"{RazorForgeLanguageName} Builder {GetVersionString()}";

        Console.WriteLine(value: header);
        Console.WriteLine();
        Console.WriteLine(value: "Usage:");
        Console.WriteLine(
            value: InvokedAsSuflae
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
            value: $"  {tool} build [entry-file]                   - Build a native executable (host OS, no run)");
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
                value: "  Invoked as suflae: a source with no .rf/.sf extension defaults to Suflae.");
        }

        Console.WriteLine(
            value: "  If no entry file is given, searches for razorforge.toml in the current");
        Console.WriteLine(value: "  directory and parent directories.");
        Console.WriteLine();
        Console.WriteLine(
            value: "  There are no build flags: all build configuration lives in razorforge.toml's");
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
    /// PropertyGroup value (via <see cref="Compiler.Resolution.BuildInfo"/>), then the assembly
    /// informational version (e.g. "0.0.1-alpha"), stripping any "+commit" suffix and prefixing <c>v</c>.
    /// </summary>
    private static string GetVersionString()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        string version = Compiler.Resolution.BuildInfo.AssemblyMetadata(key: "RazorForgeVersion")
                     ?? assembly
                        .GetCustomAttributes(
                             attributeType: typeof(System.Reflection.AssemblyInformationalVersionAttribute),
                             inherit: false)
                        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                        .FirstOrDefault()
                       ?.InformationalVersion
                     ?? assembly.GetName()
                                .Version
                               ?.ToString()
                     ?? "unknown";
        int plusIndex = version.IndexOf(value: '+');
        return plusIndex > 0 ? $"v{version[..plusIndex]}" : $"v{version}";
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
            value: $"Tokenizing {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;
            var tokenizer = new Tokenizer(source: code, fileName: sourceFile, language: language);
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
            value: $"Parsing {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            var tokenizer = new Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Parser(tokens: tokens, language: language, fileName: sourceFile);
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
                    value: "  A name the compiler hard-codes against the stdlib no longer resolves.");
                Console.WriteLine(
                    value: "  Update src/Resolution/RuntimeContract.cs to match the stdlib rename.");
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
            value: $"Building {sourceFile} as {(isSuflae ? SuflaeLanguageName : RazorForgeLanguageName)}...");
        Console.WriteLine();

        try
        {
            Language language = isSuflae
                ? Language.Suflae
                : Language.RazorForge;

            // Tokenize
            Console.WriteLine(value: "=== TOKENIZATION ===");
            var tokenizer = new Tokenizer(source: code, fileName: sourceFile, language: language);
            List<Token> tokens = tokenizer.Tokenize();
            Console.WriteLine(value: $"Generated {tokens.Count} tokens");

            // Parse
            Console.WriteLine();
            Console.WriteLine(value: "=== PARSING ===");
            var parser = new Parser(tokens: tokens, language: language, fileName: sourceFile);
            SyntaxTree.Program ast = parser.Parse();
            Console.WriteLine(value: $"Parsed {ast.Declarations.Count} declarations");

            // Semantic Analysis
            Console.WriteLine();
            Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");

            var target = TargetConfig.ForCurrentHost();
            var analyzer = new SemanticVerifier(language: language,
                target: target, buildMode: buildMode) { SaTiming = saTiming };
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
            List<(SyntaxTree.Program Program, string FilePath, string Module)>
                stdlibPrograms = result.Registry.StdlibPrograms;

            // 9-2: instrument may-suspend routine bodies with cancellation push/pop markers
            // (no-op unless something reaches a coroutine suspend point). Mutates `ast` in place,
            // which is the same AST object codegen consumes below.
            Compiler.Postprocessing.Passes.CancellationInstrumentationPass.Run(
                programs: [(ast, ast.Location.FileName, "")],
                instantiatedBodies: result.InstantiatedGenericBodies,
                maySuspendKeys: result.MaySuspendRoutineKeys,
                registry: result.Registry);

            var generator = new LlvmCodeGenerator(program: ast,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                target: target,
                buildMode: buildMode,
                synthesizedBodies: result.SynthesizedBodies,
                instantiatedGenericBodies: result.InstantiatedGenericBodies,
                liveRoutineKeys: result.LiveRoutineKeys,
                liveOwnerTypeNames: result.LiveOwnerTypeNames,
                maySuspendRoutineKeys: result.MaySuspendRoutineKeys)
            {
                Timing = saTiming,
                // Single-file codegen: the entry file's own module is the program entry.
                EntryModule = ast.Declarations.OfType<ModuleDeclaration>()
                                 .FirstOrDefault()?.Path
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

    /// <summary>
    /// Runs the multi-file build pipeline: BuildDriver (parse + resolve imports + topo sort)
    /// -> SemanticVerifier.AnalyzeMultiple -> LLVMCodeGenerator with multiple user programs.
    /// Returns 0 on success or 1 if any stage fails.
    /// </summary>
    private static int BuildMultiFile(string entryFile, string? outputFile,
        out IReadOnlyList<string> discoveredLinkLibraries,
        string? projectRoot = null, RfBuildMode buildMode = RfBuildMode.Debug,
        bool dumpAst = false, bool saTiming = false, bool requireStartRoutine = true,
        bool showBuildStages = false, IReadOnlyList<string>? libraryRoots = null,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null,
        Action<string>? irCallback = null)
    {
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
            // Use provided project root (from manifest) or fall back to entry file directory
            projectRoot ??= Path.GetDirectoryName(path: Path.GetFullPath(path: entryFile)) ?? ".";
            string stdlibRoot = StdlibLoader.GetDefaultStdlibPath();

            // Phase 1: Parse all files and resolve dependencies
            if (showBuildStages)
                Console.WriteLine(value: "=== BUILD DRIVER ===");
            var driver = new BuildDriver(projectRoot: projectRoot,
                stdlibRoot: stdlibRoot,
                language: language,
                libraryRoots: libraryRoots);
            BuildResult buildResult =
                driver.CompileFile(entryFile: Path.GetFullPath(path: entryFile));

            if (showBuildStages)
                Console.WriteLine(value: $"Parsed {buildResult.Units.Count} file(s)");

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
                Console.WriteLine(
                    value:
                    $"Initialization order: {string.Join(separator: " -> ", values: buildResult
                    .InitializationOrder)}");

            // Filter out stdlib files they are already loaded by TypeRegistry/StdlibLoader
            string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
            var userUnits = buildResult.Units
                                       .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                           .StartsWith(value: normalizedStdlib,
                                                comparisonType: StringComparison
                                                   .OrdinalIgnoreCase))
                                       .ToList();

            // Build file list in topological order
            var unitsByFile =
                new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (FileBuildUnit unit in userUnits)
            {
                unitsByFile[key: unit.FilePath] = unit;
            }

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
            foreach (string moduleName in buildResult.InitializationOrder)
            {
                if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            // Fallback: if init order doesn't cover all units (e.g., entry file with no module decl)
            foreach (FileBuildUnit unit in userUnits)
            {
                if (!orderedFiles.Any(predicate: f => string.Equals(a: f.FilePath,
                        b: unit.FilePath,
                        comparisonType: StringComparison.OrdinalIgnoreCase)))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            // Collect `@link("...")` C-library directives from the compiled files' declarations (only
            // files that passed the `@target` gate are in orderedFiles, so this is per-target correct).
            discoveredLinkLibraries = CollectLinkLibraries(
                programs: orderedFiles.Select(selector: f => f.Program));

            // Suflae `global` eager initialization: move each global's initializer into a runtime
            // assignment prepended to the entry `start()` (in module init order). This makes non-constant
            // and entity initializers work — the assignment flows through the whole pipeline (reachability,
            // lowering, codegen) and stores to the global's `@global` storage. Runs before SA so the
            // injected assignments are analyzed in context.
            if (!InjectGlobalInitializers(orderedFiles: orderedFiles))
            {
                Console.WriteLine();
                Console.Error.WriteLine(value: "Code generation aborted due to errors.");
                return 1;
            }

            // Phase 2: Semantic analysis (multi-file)
            if (showBuildStages)
            {
                Console.WriteLine();
                Console.WriteLine(value: "=== SEMANTIC ANALYSIS ===");
            }

            var target = TargetConfig.ForCurrentHost();
            // Warm path: a daemon supplies a fully-processed stdlib snapshot for this language, so the
            // restore ctor skips the ~5 s of stdlib desugaring/verification/monomorphization and only the
            // user program is analyzed. Cold path (warm == null) constructs a fresh verifier as before.
            SemanticVerifier.CompiledStdlibState? warm = warmProvider?.Invoke(language);
            bool _phaseTimingEarly = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_PHASE_TIMING")
                is not (null or "" or "0");
            var analyzer = warm != null
                ? new SemanticVerifier(language: language, warm: warm,
                    target: target, buildMode: buildMode) { SaTiming = saTiming || _phaseTimingEarly }
                : new SemanticVerifier(language: language,
                    target: target, buildMode: buildMode) { SaTiming = saTiming || _phaseTimingEarly };
            // Share the driver's fully-indexed resolver so SA-phase imports see the same
            // module set the build graph resolved (incl. [target] library directories).
            analyzer.Registry.UseModuleResolver(resolver: driver.Resolver);
            bool phaseTiming = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_PHASE_TIMING")
                is not (null or "" or "0");
            var _swPhase = phaseTiming ? System.Diagnostics.Stopwatch.StartNew() : null;
            AnalysisResult result = analyzer.AnalyzeMultiple(files: orderedFiles);
            if (_swPhase != null)
            {
                Console.Error.WriteLine(value: $"[phase] AnalyzeMultiple (SA+instantiation+postproc): {_swPhase.ElapsedMilliseconds} ms");
                _swPhase.Restart();
            }

            if (showBuildStages)
                Console.WriteLine(
                    value: $"Routines registered: {result.Registry.GetAllRoutines().Count()}");

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

            // Executable targets must declare a `start` or `start!` routine in one of the
            // user programs (stdlib doesn't count). Without it codegen would skip @main
            // synthesis and the link step would surface "subsystem must be defined" — make
            // it a hard build error here so the cause is obvious.
            if (requireStartRoutine)
            {
                var userFilePaths = orderedFiles.Select(selector: f => f.FilePath)
                    .ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
                bool hasStartRoutine = result.Registry.GetAllRoutines().Any(predicate: r =>
                    r.OwnerType == null &&
                    (r.Name == "start" || r.BaseName.EndsWith(value: ".start")) &&
                    r.Location != null && userFilePaths.Contains(item: r.Location.FileName));
                if (!hasStartRoutine)
                {
                    Console.WriteLine();
                    Console.WriteLine(
                        value:
                        "Error: executable target has no 'start' routine. " +
                        "Add 'routine start()' or 'routine start!()' to the entry module, " +
                        "or set the target type to 'library' in razorforge.toml.");
                    return 1;
                }
            }

            // Phase 3: Code generation (multi-program)
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

            List<(SyntaxTree.Program Program, string FilePath, string Module)>
                stdlibPrograms = result.Registry.StdlibPrograms;

            // 9-2: instrument may-suspend routine bodies with cancellation push/pop markers
            // (no-op unless something reaches a coroutine suspend point). Mutates the userPrograms
            // ASTs in place — the same objects codegen consumes below.
            Compiler.Postprocessing.Passes.CancellationInstrumentationPass.Run(
                programs: userPrograms,
                instantiatedBodies: result.InstantiatedGenericBodies,
                maySuspendKeys: result.MaySuspendRoutineKeys,
                registry: result.Registry);

            // The entry module (manifest executable) is the module declared by the entry file —
            // it, not an arbitrary imported module's `start`, is the program entry point.
            string entryFull = Path.GetFullPath(path: entryFile);
            string? entryModule = unitsByFile.TryGetValue(key: entryFull, value: out FileBuildUnit? entryUnit)
                ? entryUnit.Module
                : null;

            var generator = new LlvmCodeGenerator(userPrograms: userPrograms,
                registry: result.Registry,
                stdlibPrograms: stdlibPrograms,
                target: target,
                buildMode: buildMode,
                synthesizedBodies: result.SynthesizedBodies,
                instantiatedGenericBodies: result.InstantiatedGenericBodies,
                liveRoutineKeys: result.LiveRoutineKeys,
                liveOwnerTypeNames: result.LiveOwnerTypeNames,
                maySuspendRoutineKeys: result.MaySuspendRoutineKeys)
            {
                Timing = saTiming,
                EntryModule = entryModule
            };
            // dump-ast dumps the EXACT AST that LLVM codegen consumes — captured immediately BEFORE
            // Generate(), after all desugaring/monomorphization + the final CancellationInstrumentation
            // mutation. Codegen is a pure translator, so this snapshot fully defines its input.
            if (dumpAst)
            {
                string astPath = Path.ChangeExtension(path: entryFile, extension: ".rf.desugared");
                string astText = new RfSyntaxTreePrinter().PrintMultiProgram(
                    programs: userPrograms,
                    synthesizedBodies: result.SynthesizedBodies,
                    registry: result.Registry,
                    stdlibPrograms: stdlibPrograms,
                    instantiatedGenericBodies: result.InstantiatedGenericBodies);
                File.WriteAllText(path: astPath, contents: astText);
                if (showBuildStages)
                    Console.WriteLine(value: $"Codegen-input AST written to: {astPath}");
            }

            string llvmIr = generator.Generate();
            if (_swPhase != null)
            {
                Console.Error.WriteLine(value: $"[phase] codegen Generate(): {_swPhase.ElapsedMilliseconds} ms ({llvmIr.Length} chars, {generator.EmittedRoutineCount} routines)");
            }
            if (showBuildStages)
                Console.Error.WriteLine(value: $"Routines emitted: {generator.EmittedRoutineCount}");

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
                    Console.WriteLine(value: $"LLVM IR written to: {outPath}");
            }

            if (showBuildStages)
            {
                Console.WriteLine();
                Console.WriteLine(value: "Build successful!");
            }
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

    /// <summary>
    /// Runs the multi-file build pipeline through semantic analysis only (no codegen).
    /// Reports errors and warnings. Returns 0 if type-checking succeeds, 1 otherwise.
    /// </summary>
    private static int CheckMultiFile(string entryFile, string? projectRoot = null,
        IReadOnlyList<string>? libraryRoots = null) // NOSONAR S3776
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
                Console.Error.WriteLine(value: $"=== BUILD ERRORS ({buildResult.Errors.Count}) ===");
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

            // Filter out stdlib files
            string normalizedStdlib = Path.GetFullPath(path: stdlibRoot);
            var userUnits = buildResult.Units
                                       .Where(predicate: u => !Path.GetFullPath(path: u.FilePath)
                                           .StartsWith(value: normalizedStdlib,
                                                comparisonType: StringComparison
                                                   .OrdinalIgnoreCase))
                                       .ToList();

            var unitsByModule =
                new Dictionary<string, FileBuildUnit>(comparer: StringComparer.OrdinalIgnoreCase);
            foreach (FileBuildUnit unit in userUnits)
            {
                string moduleName =
                    unit.Module ?? Path.GetFileNameWithoutExtension(path: unit.FilePath);
                unitsByModule[key: moduleName] = unit;
            }

            var orderedFiles = new List<(SyntaxTree.Program Program, string FilePath)>();
            foreach (string moduleName in buildResult.InitializationOrder)
            {
                if (unitsByModule.TryGetValue(key: moduleName, value: out FileBuildUnit? unit))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

            foreach (FileBuildUnit unit in userUnits)
            {
                if (!orderedFiles.Any(predicate: f => string.Equals(a: f.FilePath,
                        b: unit.FilePath,
                        comparisonType: StringComparison.OrdinalIgnoreCase)))
                {
                    orderedFiles.Add(item: (unit.Ast, unit.FilePath));
                }
            }

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

    /// <summary>
    /// Builds a multi-file project and executes the resulting LLVM IR via lli.
    /// Returns 0 on success or 1 if build or execution fails.
    /// </summary>
    /// <summary>
    /// Compiles <paramref name="entryFile"/> all the way to a native executable
    /// (codegen → opt → link → stage runtime DLLs) but does NOT run it. On success returns 0 and
    /// sets <paramref name="exeFile"/> to the produced executable path. Guarded by the <c>buildexe</c>
    /// verb (stop here) and <c>buildandrun</c> (run it next) so the slow optimize+link is identical.
    /// </summary>
    /// <summary>
    /// Gathers C-library names declared in source via <c>@link("SDL2")</c> annotations on <c>C::</c>
    /// externs (or routines) across the compiled programs. De-duplicated, order-preserving. The caller
    /// passes only files that survived the <c>@target</c> gate, so the result is per-target correct.
    /// </summary>
    /// <summary>Name of the synthesized per-program entity that holds every Suflae module-level
    /// <c>global</c> as a field. Routing all globals through ONE <c>entity</c> behind a hidden
    /// <c>Roamed</c> singleton makes them thread-safe on the M:N worker pool: the per-statement
    /// access-lock brackets (RoamedLockBracketLoweringPass) serialize concurrent field RMW, and
    /// atomic-width scalar fields additionally get a lock-free <c>atomicrmw</c> fast-path on the field
    /// address.</summary>
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
        var collected = new List<(string Name, TypeExpression Type, Expression Init, SourceLocation Loc)>();
        foreach ((SyntaxTree.Program program, string _) in orderedFiles)
        {
            List<ISyntaxTreeNode> decls = program.Declarations;
            for (int i = 0; i < decls.Count; i++)
            {
                if (decls[i] is VariableDeclaration
                    { IsGlobal: true, Initializer: not null, Type: not null } g)
                {
                    collected.Add(item: (g.Name, g.Type, g.Initializer, g.Location));
                    decls[i] = g with { Initializer = null };
                }
            }
        }

        if (collected.Count == 0)
        {
            return true;
        }

        // Deduplicate by bare name — a duplicate declaration's LAST initializer wins (matching the
        // registry's last-write-wins global table). First-seen order gives a stable field layout.
        var seenOrder = new List<string>();
        var latest =
            new Dictionary<string, (TypeExpression Type, Expression Init, SourceLocation Loc)>(
                comparer: StringComparer.Ordinal);
        foreach ((string name, TypeExpression type, Expression init, SourceLocation loc) in collected)
        {
            if (!latest.ContainsKey(key: name)) seenOrder.Add(item: name);
            latest[key: name] = (type, init, loc);
        }
        var globals = seenOrder
            .Select(selector: name => (Name: name, latest[key: name].Type, latest[key: name].Init,
                latest[key: name].Loc))
            .ToList();

        // 2) Order the initializers so each global runs AFTER every other global it depends on (the
        //    dependency inits before its dependent). Globals with no inter-dependency keep source order
        //    (stable Kahn). A dependency cycle — including a self-reference — is a use-before-init and
        //    fails LOUD. Dependencies are TRANSITIVE through free-routine calls: if a global's
        //    initializer calls `f()` and `f`'s body reads another global, that is a dependency too — so
        //    `global a = compute()` where `compute` reads `b` correctly orders `b` before `a` (or reports
        //    a cycle) at build time, with zero runtime cost. (Member-routine calls are not followed — a
        //    global read hidden behind `x.foo()` is the remaining residual.)
        int n = globals.Count;
        var nameToIdx = new Dictionary<string, int>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < n; i++) nameToIdx[key: globals[index: i].Name] = i; // last decl of a dup name wins

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
            var d = new HashSet<int>();
            var visitedRoutines = new HashSet<string>(comparer: StringComparer.Ordinal);
            var toScan = new Queue<object>();
            toScan.Enqueue(item: globals[index: i].Init);
            while (toScan.Count > 0)
            {
                object root = toScan.Dequeue();
                AstWalker.WalkExpressions(root: root, visit: e =>
                {
                    if (e is IdentifierExpression id && nameToIdx.TryGetValue(key: id.Name, value: out int j))
                    {
                        d.Add(item: j);
                    }
                    // Follow a call into the callee's body once (transitive hidden dependency).
                    if (e is CallExpression { Callee: IdentifierExpression callee }
                        && routineBodies.TryGetValue(key: callee.Name, value: out Statement? calleeBody)
                        && visitedRoutines.Add(item: callee.Name))
                    {
                        toScan.Enqueue(item: calleeBody);
                    }
                });
            }
            deps.Add(item: d);
        }

        // Kahn's algorithm, stable in source order among ready nodes.
        var indegree = new int[n];
        for (int i = 0; i < n; i++)
            foreach (int j in deps[index: i])
                if (j != i) indegree[i]++; // edge j -> i (dependency j before dependent i)

        var order = new List<int>(capacity: n);
        bool ready;
        do
        {
            ready = false;
            for (int i = 0; i < n; i++)
            {
                if (indegree[i] == 0)
                {
                    indegree[i] = -1; // consumed
                    order.Add(item: i);
                    ready = true;
                    for (int k = 0; k < n; k++)
                        if (k != i && deps[index: k].Contains(item: i))
                            indegree[k]--;
                }
            }
        } while (ready);

        if (order.Count != n)
        {
            IEnumerable<string> cyclic = Enumerable.Range(start: 0, count: n)
                .Where(predicate: i => !order.Contains(value: i))
                .Select(selector: i => globals[index: i].Name);
            Console.Error.WriteLine(
                value: "error[RF-S436]: circular global initialization — these globals reference each " +
                       $"other (directly) before they are initialized: {string.Join(", ", cyclic)}. " +
                       "A global's initializer may only reference globals it does not (transitively) depend on.");
            return false;
        }

        // 3) Build the __ModuleGlobals entity — one field per global (`name: Type`, no initializer).
        SourceLocation loc0 = globals[index: 0].Loc;
        var fieldDecls = new List<Declaration>(capacity: n);
        for (int i = 0; i < n; i++)
        {
            fieldDecls.Add(item: new VariableDeclaration(Name: globals[index: i].Name,
                Type: globals[index: i].Type, Initializer: null, Visibility: VisibilityModifier.Open,
                Location: globals[index: i].Loc));
        }
        var entityDecl = new EntityDeclaration(Name: ModuleGlobalsEntityName, GenericParameters: null,
            Protocols: new List<TypeExpression>(), Members: fieldDecls,
            Visibility: VisibilityModifier.Open, Location: loc0);

        // 4) Constructor arguments: a field whose initializer touches no other global (deps empty) is set
        //    directly at construction with its REAL initializer; a dependent field is seeded with a type
        //    default and gets its real value from an ordered field assignment after construction (below).
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
                Expression? def = DefaultInitializerFor(type: globals[index: i].Type,
                    loc: globals[index: i].Loc);
                if (def == null)
                {
                    Console.Error.WriteLine(
                        value: $"error[RF-S437]: the global '{globals[index: i].Name}: " +
                               $"{globals[index: i].Type.Name}' has an initializer that depends on another " +
                               $"global, but dependent initialization is only supported for scalar/Text/Bool " +
                               $"types. Initialize it from a constant instead.");
                    return false;
                }
                value = def;
            }
            ctorArgs.Add(item: (globals[index: i].Name, value));
        }
        var construct = new CreatorExpression(TypeName: ModuleGlobalsEntityName, TypeArguments: null,
            MemberVariables: ctorArgs, Location: loc0);

        // 5) Init statements prepended to start(): construct + promote the singleton (IsGlobalInit lets
        //    RoamedSpawnPromotionLoweringPass arm its lock), then the ordered field assignments for the
        //    dependent globals. Each dependent assignment's target + any global reads inside its value are
        //    bare global identifiers that GlobalEntityRewritePass retargets to `__globals__.field`.
        var initStmts = new List<Statement>(capacity: n + 1)
        {
            new AssignmentStatement(
                Target: new IdentifierExpression(Name: ModuleGlobalsSingletonName, Location: loc0),
                Value: construct, Location: loc0) { IsGlobalInit = true }
        };
        foreach (int i in order)
        {
            if (deps[index: i].Count == 0) continue; // independent — already set by the constructor
            initStmts.Add(item: new AssignmentStatement(
                Target: new IdentifierExpression(Name: globals[index: i].Name,
                    Location: globals[index: i].Loc),
                Value: globals[index: i].Init, Location: globals[index: i].Loc));
        }

        // 6) The singleton declaration — an entity `global` whose storage is a promoted Roamed[E] handle.
        var singletonDecl = new VariableDeclaration(Name: ModuleGlobalsSingletonName,
            Type: new TypeExpression(Name: ModuleGlobalsEntityName, GenericArguments: null,
                Location: loc0),
            Initializer: null, Visibility: VisibilityModifier.Open, Location: loc0, IsGlobal: true);

        // 7) Splice into the entry file (the one with `start()`): declare the entity + singleton and
        //    prepend the init statements to start().
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

            if (startBlock == null) continue;

            // Append (NOT prepend) the synthesized declarations: imports must stay at the top of the
            // file (RF-S114). Declaration order does not matter for type collection.
            program.Declarations.Add(item: entityDecl);
            program.Declarations.Add(item: singletonDecl);
            startBlock.Statements.InsertRange(index: 0, collection: initStmts);
            return true;
        }

        Console.Error.WriteLine(
            value: "error[RF-S438]: module-level 'global' declarations require a 'routine start()' entry " +
                   "point to host their initialization.");
        return false;
    }

    /// <summary>A build-time default value for a <c>global</c> whose initializer depends on another
    /// global (so its real value is assigned after the singleton is constructed). A bare integer literal
    /// <c>0</c> conforms to any numeric type (int/float/decimal) via RF-S767; Text and Bool have their
    /// own empty/false defaults. Returns null for a type with no synthesizable default.</summary>
    private static Expression? DefaultInitializerFor(TypeExpression type, SourceLocation loc)
    {
        return type.Name switch
        {
            "Text" => new LiteralExpression(Value: "", LiteralType: Compiler.Tokenizer.TokenType.TextLiteral,
                Location: loc),
            "Bool" => new LiteralExpression(Value: false, LiteralType: Compiler.Tokenizer.TokenType.False,
                Location: loc),
            "S8" or "S16" or "S32" or "S64" or "S128" or "S256" or "U8" or "U16" or "U32" or "U64"
                or "U128" or "U256" or "F16" or "F32" or "F64" or "F128" or "F256" or "Decimal"
                or "D32" or "D64" or "D128" or "Integer" => new LiteralExpression(Value: "0",
                    LiteralType: Compiler.Tokenizer.TokenType.UndecidedInteger, Location: loc),
            _ => null
        };
    }

    private static IReadOnlyList<string> CollectLinkLibraries(IEnumerable<SyntaxTree.Program> programs)
    {
        var libs = new List<string>();
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        void Scan(List<string>? annotations)
        {
            if (annotations == null) return;
            foreach (string ann in annotations)
            {
                (string? lib, string? _) = TypeModel.Symbols.LinkAnnotation.Parse(annotation: ann);
                if (lib != null && seen.Add(item: lib)) libs.Add(item: lib);
            }
        }

        void Visit(ISyntaxTreeNode node)
        {
            switch (node)
            {
                case RoutineDeclaration r: Scan(annotations: r.Annotations); break;
                case ExternalDeclaration e: Scan(annotations: e.Annotations); break;
                case ExternalBlockDeclaration b:
                    foreach (Declaration d in b.Declarations) Visit(node: d);
                    break;
            }
        }

        foreach (SyntaxTree.Program prog in programs)
        foreach (ISyntaxTreeNode decl in prog.Declarations)
            Visit(node: decl);

        return libs;
    }

    /// <summary>
    /// Compiles a source file all the way to LLVM-IR TEXT (no opt/clang/link), returning the IR string.
    /// This is the front half of <see cref="BuildExecutable"/> — used by the ORC-JIT dev-loop path, which
    /// JITs the IR in-process instead of producing a native exe. Honors a warm-stdlib provider so a daemon
    /// can supply the fast path. Returns the build exit code (0 = success, and <paramref name="ir"/> holds
    /// the module); diagnostics are printed by <see cref="BuildMultiFile"/> as usual.
    /// </summary>
    private static int BuildToIr(string entryFile, out string ir, string? projectRoot = null,
        RfBuildMode buildMode = RfBuildMode.Debug, bool requireStartRoutine = true,
        IReadOnlyList<string>? libraryRoots = null,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        string captured = "";
        int rc = BuildMultiFile(entryFile: entryFile,
            outputFile: null,
            discoveredLinkLibraries: out _,
            projectRoot: projectRoot,
            buildMode: buildMode,
            dumpAst: false,
            saTiming: false,
            requireStartRoutine: requireStartRoutine,
            showBuildStages: false,
            libraryRoots: libraryRoots,
            warmProvider: warmProvider,
            irCallback: s => captured = s);
        ir = captured;
        return rc;
    }

    private static int BuildExecutable(string entryFile, out string exeFile, string? projectRoot = null,
        RfBuildMode buildMode = RfBuildMode.Debug, bool dumpAst = false, bool saTiming = false,
        bool requireStartRoutine = true, bool showBuildStages = false,
        IReadOnlyList<string>? libraryRoots = null, IReadOnlyList<string>? cLibraries = null,
        IReadOnlyList<string>? libraryPaths = null,
        IReadOnlyDictionary<string, CLibrary>? libraryConfigs = null,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        // Remove stale per-target outputs before rebuilding.
        string llFile = Path.ChangeExtension(path: entryFile, extension: ".ll");
        string optFile = Path.ChangeExtension(path: llFile, extension: ".opt.ll");
        exeFile = Path.ChangeExtension(path: llFile, extension: ".exe");
        NativeToolchain.CleanBuildAndRunOutputs(llFile: llFile, optFile: optFile, exeFile: exeFile);

        // Build first (to a temp .ll file). BuildMultiFile also reports any `@link(...)` C libraries
        // declared in the compiled source.
        int buildResult = BuildMultiFile(entryFile: entryFile,
            outputFile: llFile,
            discoveredLinkLibraries: out IReadOnlyList<string> discoveredLinks,
            projectRoot: projectRoot,
            buildMode: buildMode,
            dumpAst: dumpAst,
            saTiming: saTiming,
            requireStartRoutine: requireStartRoutine,
            showBuildStages: showBuildStages,
            libraryRoots: libraryRoots,
            warmProvider: warmProvider);
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
            string resolved = libraryConfigs != null &&
                              libraryConfigs.TryGetValue(key: lib, value: out CLibrary? cfg)
                ? cfg.Name
                : lib;
            if (!allCLibraries.Contains(item: resolved)) allCLibraries.Add(item: resolved);
        }
        if (cLibraries != null) foreach (string lib in cLibraries) AddLib(lib: lib);
        if (libraryConfigs != null) foreach (CLibrary cfg in libraryConfigs.Values) AddLib(lib: cfg.Name);
        foreach (string lib in discoveredLinks) AddLib(lib: lib);

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

        if (NativeToolchain.TryFindNativeBuildDirectory(exeDir: exeDir, nativeBuildDir: out string nativeBuildDir))
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
        else if (File.Exists(path: Path.Combine(path1: exeDir, path2: NativeToolchain.RuntimeLinkLibraryFileName)))
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
        int optResult = NativeToolchain.OptimizeIr(llFile: llFile, optFile: optFile,
            buildMode: buildMode);
        if (optResult != 0)
        {
            return optResult;
        }

        int linkResult = NativeToolchain.LinkExecutable(optFile: optFile, exeFile: exeFile,
            runtimeLibDir: runtimeLibDir, buildMode: buildMode,
            cLibraries: allCLibraries, libraryPaths: libraryPaths);
        if (linkResult != 0)
        {
            return linkResult;
        }

        // Copy the runtime DLL (and its shared-library dependencies) next to the
        // output .exe so the loader can find them at runtime.
        NativeToolchain.StageRuntimeDlls(exeDir: exeDir, exeFile: exeFile);
        return 0;
    }

    private static int BuildAndRun(string entryFile, string? projectRoot = null,
        RfBuildMode buildMode = RfBuildMode.Debug, bool dumpAst = false, bool saTiming = false,
        bool requireStartRoutine = true,
        bool showBuildStages = false, IReadOnlyList<string>? libraryRoots = null,
        IReadOnlyList<string>? cLibraries = null, IReadOnlyList<string>? libraryPaths = null,
        IReadOnlyDictionary<string, CLibrary>? libraryConfigs = null,
        Func<Language, SemanticVerifier.CompiledStdlibState?>? warmProvider = null)
    {
        int buildResult = BuildExecutable(entryFile: entryFile,
            exeFile: out string exeFile,
            projectRoot: projectRoot,
            buildMode: buildMode,
            dumpAst: dumpAst,
            saTiming: saTiming,
            requireStartRoutine: requireStartRoutine,
            showBuildStages: showBuildStages,
            libraryRoots: libraryRoots,
            cLibraries: cLibraries,
            libraryPaths: libraryPaths,
            libraryConfigs: libraryConfigs,
            warmProvider: warmProvider);
        if (buildResult != 0)
        {
            return buildResult;
        }

        return RunExecutable(exeFile: exeFile, showBuildStages: showBuildStages);
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
            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
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
