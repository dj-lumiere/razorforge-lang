using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using Compiler.Targeting;
using Verification;
using TypeModel.Enums;

namespace Builder;

internal partial class Program
{
    /// <summary>
    /// The warm-compile daemon: a long-lived process that captures the fully-processed stdlib
    /// (<see cref="SemanticVerifier.CompiledStdlibState"/>) in RAM once, then serves build/buildandrun
    /// requests over a per-user named pipe. Each request restores from that snapshot (the ~5 s of stdlib
    /// desugaring/verification/monomorphization is skipped) and only the user program is compiled, so the
    /// edit→result loop drops from ~6.5 s toward the sub-250 ms goal. Nested in <see cref="Program"/> so it
    /// can drive the private <c>BuildExecutable</c> path with a warm-state provider.
    ///
    /// Protocol: length-prefixed (4-byte little-endian) UTF-8 JSON, one request → one response per
    /// connection. The daemon handles requests SERIALLY, which also makes the transient Console
    /// redirection used to capture build diagnostics race-free.
    /// </summary>
    internal static class CompileDaemon
    {
        // ---- request / response DTOs ---------------------------------------------------------------

        private sealed class DaemonRequest
        {
            public string Verb { get; set; } = "";       // "build" | "ir" | "shutdown" | "ping"
            public string EntryFile { get; set; } = "";
            public string? ProjectRoot { get; set; }
            public int BuildMode { get; set; }
            public bool DumpAst { get; set; }
            public bool SaTiming { get; set; }
            public bool RequireStart { get; set; } = true;
            public bool ShowBuildStages { get; set; }
            public List<string> LibraryRoots { get; set; } = [];
            public List<string> CLibraries { get; set; } = [];
            public List<string> LibraryPaths { get; set; } = [];
        }

        private sealed class DaemonResponse
        {
            public int ExitCode { get; set; }
            public string Output { get; set; } = "";
            public string? ExePath { get; set; }
            public string? Ir { get; set; }
        }

        // ---- pipe naming / opt-in ------------------------------------------------------------------

        /// <summary>Per-user pipe name so two users on one machine don't collide. Overridable via
        /// <c>RAZORFORGE_DAEMON_PIPE</c> (e.g. to run more than one daemon).</summary>
        private static string PipeName()
        {
            string? overridden = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_DAEMON_PIPE");
            if (!string.IsNullOrWhiteSpace(value: overridden))
            {
                return overridden!;
            }

            string user = Environment.UserName;
            return $"razorforge-daemon-{user}";
        }

        /// <summary>Clients only reach for the daemon when <c>RAZORFORGE_DAEMON</c> opts in — the default
        /// CLI path stays cold and unchanged (so the test suite / CI are unaffected).</summary>
        private static bool ClientEnabled()
        {
            string? v = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_DAEMON");
            return v is not (null or "" or "0" or "false" or "no");
        }

        /// <summary>The ORC-JIT dev-loop path is opt-in via <c>RAZORFORGE_JIT</c>: <c>buildandrun</c> JITs
        /// the module in-process (no opt/clang/link/exe) instead of building and spawning a native exe.</summary>
        private static bool JitEnabled()
        {
            string? v = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_JIT");
            return v is not (null or "" or "0" or "false" or "no");
        }

        // ---- server --------------------------------------------------------------------------------

        private static readonly Dictionary<Language, SemanticVerifier.CompiledStdlibState> WarmCache = new();

        /// <summary>Lazily captures (and caches) the fully-processed stdlib for a language. The first
        /// request per language pays the ~5 s capture; every request after is warm.</summary>
        private static SemanticVerifier.CompiledStdlibState GetWarm(Language language)
        {
            if (WarmCache.TryGetValue(key: language, value: out SemanticVerifier.CompiledStdlibState? cached))
            {
                return cached;
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            Console.Error.WriteLine(value: $"[daemon] capturing {language} stdlib snapshot...");
            SemanticVerifier.CompiledStdlibState state =
                SemanticVerifier.CaptureCompiledStdlib(language: language);
            WarmCache[key: language] = state;
            Console.Error.WriteLine(
                value: $"[daemon] {language} stdlib snapshot ready ({sw.ElapsedMilliseconds} ms)");
            return state;
        }

        private static volatile bool _shutdownRequested;

        /// <summary>Runs the daemon server loop (foreground) until a shutdown request or Ctrl-C.</summary>
        public static int RunServer()
        {
            string pipe = PipeName();
            Console.Error.WriteLine(value: $"[daemon] razorforge warm-compile daemon on pipe '{pipe}'");
            Console.Error.WriteLine(value: "[daemon] set RAZORFORGE_DAEMON=1 in the client env to route builds here.");

            // Pre-warm the primary language so the first real build is already warm.
            try
            {
                GetWarm(language: InvokedAsSuflae ? Language.Suflae : Language.RazorForge);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"[daemon] pre-warm failed: {ex.Message}");
            }

            Console.CancelKeyPress += (_, e) =>
            {
                _shutdownRequested = true;
                e.Cancel = true;
            };

            while (!_shutdownRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(pipeName: pipe, direction: PipeDirection.InOut,
                        maxNumberOfServerInstances: 1, transmissionMode: PipeTransmissionMode.Byte,
                        options: PipeOptions.None);
                    server.WaitForConnection();

                    DaemonRequest? req = ReadMessage<DaemonRequest>(stream: server);
                    if (req == null)
                    {
                        continue;
                    }

                    if (req.Verb == "shutdown")
                    {
                        WriteMessage(stream: server, value: new DaemonResponse { ExitCode = 0, Output = "" });
                        _shutdownRequested = true;
                        break;
                    }

                    if (req.Verb == "ping")
                    {
                        WriteMessage(stream: server, value: new DaemonResponse { ExitCode = 0, Output = "pong" });
                        continue;
                    }

                    DaemonResponse resp = req.Verb == "ir" ? HandleIr(req: req) : HandleBuild(req: req);
                    WriteMessage(stream: server, value: resp);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(value: $"[daemon] request failed: {ex.Message}");
                }
            }

            Console.Error.WriteLine(value: "[daemon] shutting down.");
            return 0;
        }

        /// <summary>Runs one warm build, capturing all build diagnostics (Console.Out + Console.Error, in
        /// chronological order via a single writer) so they can be shipped back to the client.</summary>
        private static DaemonResponse HandleBuild(DaemonRequest req)
        {
            var captured = new StringWriter();
            TextWriter savedOut = Console.Out;
            TextWriter savedErr = Console.Error;
            int exit;
            string exePath;
            try
            {
                Console.SetOut(newOut: captured);
                Console.SetError(newError: captured);
                exit = BuildExecutable(entryFile: req.EntryFile,
                    exeFile: out exePath,
                    projectRoot: req.ProjectRoot,
                    buildMode: (RfBuildMode)req.BuildMode,
                    dumpAst: req.DumpAst,
                    saTiming: req.SaTiming,
                    requireStartRoutine: req.RequireStart,
                    showBuildStages: req.ShowBuildStages,
                    libraryRoots: req.LibraryRoots,
                    cLibraries: req.CLibraries,
                    libraryPaths: req.LibraryPaths,
                    libraryConfigs: null,
                    warmProvider: GetWarm);
            }
            catch (Exception ex)
            {
                captured.WriteLine(value: $"[daemon] build threw: {ex.Message}");
                exit = 1;
                exePath = "";
            }
            finally
            {
                Console.SetOut(newOut: savedOut);
                Console.SetError(newError: savedErr);
            }

            Console.Error.WriteLine(
                value: $"[daemon] {Path.GetFileName(path: req.EntryFile)} -> exit {exit}");
            return new DaemonResponse
            {
                ExitCode = exit,
                Output = captured.ToString(),
                ExePath = exit == 0 ? exePath : null
            };
        }

        /// <summary>Runs one warm compile-to-IR (no opt/clang/link), for the ORC-JIT client path. Captures
        /// build diagnostics and returns the IR text so the client can JIT-and-run it in-process.</summary>
        private static DaemonResponse HandleIr(DaemonRequest req)
        {
            var captured = new StringWriter();
            TextWriter savedOut = Console.Out;
            TextWriter savedErr = Console.Error;
            int exit;
            string ir;
            try
            {
                Console.SetOut(newOut: captured);
                Console.SetError(newError: captured);
                exit = BuildToIr(entryFile: req.EntryFile,
                    ir: out ir,
                    projectRoot: req.ProjectRoot,
                    buildMode: (RfBuildMode)req.BuildMode,
                    requireStartRoutine: req.RequireStart,
                    libraryRoots: req.LibraryRoots,
                    warmProvider: GetWarm);
            }
            catch (Exception ex)
            {
                captured.WriteLine(value: $"[daemon] compile-to-IR threw: {ex.Message}");
                exit = 1;
                ir = "";
            }
            finally
            {
                Console.SetOut(newOut: savedOut);
                Console.SetError(newError: savedErr);
            }

            Console.Error.WriteLine(
                value: $"[daemon] ir {Path.GetFileName(path: req.EntryFile)} -> exit {exit} ({ir.Length} chars)");
            return new DaemonResponse
            {
                ExitCode = exit,
                Output = captured.ToString(),
                Ir = exit == 0 ? ir : null
            };
        }

        /// <summary>Connects, sends <c>shutdown</c>, and returns 0 on success (used by <c>daemon-stop</c>).</summary>
        public static int StopServer()
        {
            try
            {
                DaemonResponse? _ = SendRequest(request: new DaemonRequest { Verb = "shutdown" }, timeoutMs: 2000);
                Console.WriteLine(value: "Daemon stop requested.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"No running daemon to stop ({ex.Message}).");
                return 1;
            }
        }

        // ---- client --------------------------------------------------------------------------------

        /// <summary>Client entry for the <c>build</c> verb: delegates the compile to a running daemon.
        /// Returns false (so the caller falls back to a cold build) when the daemon is disabled,
        /// unreachable, or the target uses unsupported [libraries.*] configs.</summary>
        internal static bool TryClientBuild(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            if (!TryClientCompile(resolved: resolved, response: out DaemonResponse? resp))
            {
                return false;
            }

            Console.Write(value: resp!.Output);
            if (resp.ExitCode == 0 && resp.ExePath != null)
            {
                Console.WriteLine(value: $"Executable written to: {Path.GetFullPath(path: resp.ExePath)}");
            }

            exitCode = resp.ExitCode;
            return true;
        }

        /// <summary>Client entry for <c>buildandrun</c>: the daemon performs the warm COMPILE and returns
        /// the exe path; this process runs it locally so interactive stdin/stdout stays here.</summary>
        internal static bool TryClientBuildAndRun(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            if (!TryClientCompile(resolved: resolved, response: out DaemonResponse? resp))
            {
                return false;
            }

            Console.Write(value: resp!.Output);
            if (resp.ExitCode != 0 || resp.ExePath == null)
            {
                exitCode = resp.ExitCode;
                return true;
            }

            exitCode = RunExecutable(exeFile: resp.ExePath, showBuildStages: resolved.ShowBuildStages);
            return true;
        }

        /// <summary>Client entry for the ORC-JIT dev loop: obtain the module IR (warm from the daemon when
        /// enabled+reachable, else a local cold compile) and JIT-and-run it in THIS process, so program
        /// stdio is the user's terminal. Returns false to fall back to the AOT build path when JIT is
        /// disabled or the JIT runtime can't initialize (e.g. libLLVM missing). A build error still counts
        /// as "handled" (returns true with the error's exit code — no point falling back to a build that
        /// will fail identically).</summary>
        internal static bool TryClientJitRun(ResolvedEntry resolved, out int exitCode)
        {
            exitCode = 0;
            if (!JitEnabled() || resolved.EntryFile == null)
            {
                return false;
            }

            // Rich [libraries.NAME] targets aren't carried by the daemon IR path, and a JIT'd module can't
            // link arbitrary user C libraries via the simple process-search generator — leave those to AOT.
            if (resolved.LibraryConfigs.Count > 0 || resolved.CLibraries.Count > 0)
            {
                return false;
            }

            if (!OrcJitExecutor.TryInitialize(error: out string? initErr))
            {
                Console.Error.WriteLine(value: $"[jit] unavailable ({initErr}); falling back to AOT build.");
                return false;
            }

            // Obtain IR: warm via the daemon when opted-in and reachable, else a cold in-process compile.
            string ir;
            int rc;
            var _swIr = System.Diagnostics.Stopwatch.StartNew();
            if (ClientEnabled() && TryDaemonIr(resolved: resolved, ir: out ir, exitCode: out rc))
            {
                _swIr.Stop();
                Console.Error.WriteLine(value: $"[timing] daemon IR fetch (warm SA+codegen): {_swIr.ElapsedMilliseconds} ms ({ir.Length} chars)");
            }
            else
            {
                rc = BuildToIr(entryFile: System.IO.Path.GetFullPath(path: resolved.EntryFile),
                    ir: out ir,
                    projectRoot: resolved.ProjectRoot,
                    buildMode: resolved.BuildMode,
                    requireStartRoutine: resolved.RequireStartRoutine,
                    libraryRoots: resolved.LibraryRoots,
                    warmProvider: null);
            }

            if (rc != 0 || string.IsNullOrEmpty(value: ir))
            {
                exitCode = rc != 0 ? rc : 1;
                return true;
            }

            try
            {
                var _swJit = System.Diagnostics.Stopwatch.StartNew();
                exitCode = OrcJitExecutor.JitAndRun(llvmIr: ir,
                    programName: System.IO.Path.GetFullPath(path: resolved.EntryFile),
                    programArgs: []);
                _swJit.Stop();
                Console.Error.WriteLine(value: $"[timing] JIT compile + run: {_swJit.ElapsedMilliseconds} ms");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(value: $"[jit] execution failed: {ex.Message}");
                exitCode = 1;
                return true;
            }
        }

        /// <summary>Requests warm IR from a running daemon. Returns false (cold fallback) when the daemon is
        /// unreachable. On success, writes the daemon's captured build diagnostics to the console and yields
        /// the IR + build exit code.</summary>
        private static bool TryDaemonIr(ResolvedEntry resolved, out string ir, out int exitCode)
        {
            ir = "";
            exitCode = 0;
            var req = new DaemonRequest
            {
                Verb = "ir",
                EntryFile = Path.GetFullPath(path: resolved.EntryFile!),
                ProjectRoot = resolved.ProjectRoot,
                BuildMode = (int)resolved.BuildMode,
                RequireStart = resolved.RequireStartRoutine,
                LibraryRoots = [..resolved.LibraryRoots]
            };
            try
            {
                DaemonResponse? resp = SendRequest(request: req, timeoutMs: 500);
                if (resp == null)
                {
                    return false;
                }

                Console.Write(value: resp.Output);
                ir = resp.Ir ?? "";
                exitCode = resp.ExitCode;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Shared client compile: gate on opt-in + supported config, connect, exchange one
        /// request/response. Returns false to signal a cold fallback (never throws for the caller).</summary>
        private static bool TryClientCompile(ResolvedEntry resolved, out DaemonResponse? response)
        {
            response = null;
            if (!ClientEnabled() || resolved.EntryFile == null)
            {
                return false;
            }

            // The daemon protocol doesn't carry rich [libraries.NAME] configs yet — such targets stay cold.
            if (resolved.LibraryConfigs.Count > 0)
            {
                return false;
            }

            var req = new DaemonRequest
            {
                Verb = "build",
                EntryFile = Path.GetFullPath(path: resolved.EntryFile),
                ProjectRoot = resolved.ProjectRoot,
                BuildMode = (int)resolved.BuildMode,
                DumpAst = resolved.DumpAst,
                SaTiming = resolved.SaTiming,
                RequireStart = resolved.RequireStartRoutine,
                ShowBuildStages = resolved.ShowBuildStages,
                LibraryRoots = [..resolved.LibraryRoots],
                CLibraries = [..resolved.CLibraries],
                LibraryPaths = [..resolved.LibraryPaths]
            };

            try
            {
                response = SendRequest(request: req, timeoutMs: 500);
                return response != null;
            }
            catch
            {
                // No daemon listening (or it died mid-request) — fall back to a cold in-process build.
                return false;
            }
        }

        private static DaemonResponse? SendRequest(DaemonRequest request, int timeoutMs)
        {
            using var client = new NamedPipeClientStream(serverName: ".", pipeName: PipeName(),
                direction: PipeDirection.InOut);
            client.Connect(timeout: timeoutMs);
            WriteMessage(stream: client, value: request);
            return ReadMessage<DaemonResponse>(stream: client);
        }

        // ---- length-prefixed JSON framing ----------------------------------------------------------

        private static void WriteMessage<T>(Stream stream, T value)
        {
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(value: value);
            Span<byte> len = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination: len, value: payload.Length);
            stream.Write(buffer: len);
            stream.Write(buffer: payload, offset: 0, count: payload.Length);
            stream.Flush();
        }

        private static T? ReadMessage<T>(Stream stream) where T : class
        {
            byte[] lenBuf = new byte[4];
            if (!ReadExact(stream: stream, buffer: lenBuf, count: 4))
            {
                return null;
            }

            int len = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(source: lenBuf);
            if (len <= 0 || len > 64 * 1024 * 1024)
            {
                return null;
            }

            byte[] payload = new byte[len];
            if (!ReadExact(stream: stream, buffer: payload, count: len))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(utf8Json: payload);
        }

        private static bool ReadExact(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = stream.Read(buffer: buffer, offset: read, count: count - read);
                if (n <= 0)
                {
                    return false;
                }

                read += n;
            }

            return true;
        }
    }
}
