using System.Diagnostics;
using System.Runtime.InteropServices;
using Compiler.Targeting;

namespace Builder;

/// <summary>
/// Native toolchain driver: resolves the LLVM tools (clang/opt) and the bundled native
/// runtime, optimizes/links emitted LLVM IR into an executable, stages the runtime's shared
/// libraries, and cleans stale build artifacts. Split out of <see cref="Program"/> so the CLI
/// entry point only parses arguments and dispatches; all platform/linker knowledge lives here.
/// </summary>
internal static class NativeToolchain
{
    /// <summary>
    /// Native DLLs a compiled program needs next to its .exe on Windows: the runtime
    /// itself plus the shared libraries it links dynamically (bdwgc builds as a shared
    /// gc.dll; whether the runtime's import table retains it varies by linker, so it is
    /// staged whenever present).
    /// </summary>
    private static readonly string[] NativeRuntimeDlls =
    [
        "razorforge_runtime.dll"
    ];

    /// <summary>The platform-specific link-time artifact of the bundled native runtime.</summary>
    internal static string RuntimeLinkLibraryFileName =>
        OperatingSystem.IsWindows() ? "razorforge_runtime.lib"
        : OperatingSystem.IsMacOS() ? "librazorforge_runtime.dylib"
        : "librazorforge_runtime.so";

    /// <summary>
    /// Resolves the directory containing the running RazorForge assembly.
    /// </summary>
    internal static string ResolveExecutableDirectory()
    {
        string? exeDir = Path.GetDirectoryName(path: typeof(NativeToolchain).Assembly.Location);
        return exeDir ?? throw new InvalidOperationException(
            "Unable to resolve the RazorForge executable directory.");
    }

    /// <summary>
    /// Locates the native runtime's CMake build tree (development checkouts only).
    /// Installed/published layouts ship prebuilt artifacts flat next to the executable
    /// and have no source tree — callers must treat a miss as "use the installed layout".
    /// </summary>
    internal static bool TryFindNativeBuildDirectory(string exeDir, out string nativeBuildDir)
    {
        string? current = exeDir;
        for (int i = 0; i < 6 && current != null; i++)
        {
            string candidate = Path.Combine(path1: current, path2: "native", path3: "build");
            if (File.Exists(path: Path.Combine(path1: candidate, path2: "build.ninja")) ||
                File.Exists(path: Path.Combine(path1: candidate, path2: "Makefile")))
            {
                nativeBuildDir = candidate;
                return true;
            }

            current = Path.GetDirectoryName(path: current);
        }

        nativeBuildDir = "";
        return false;
    }

    /// <summary>
    /// The macOS SDK path from `xcrun --show-sdk-path` (Command Line Tools). Apple's own
    /// clang infers the SDK automatically, but the bundled LLVM clang defaults to `/`,
    /// where modern macOS keeps no libSystem stubs — every link needs -isysroot.
    /// </summary>
    private static readonly Lazy<string?> MacSdkPath = new(valueFactory: () =>
    {
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/usr/bin/xcrun",
                Arguments = "--show-sdk-path",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(startInfo: psi);
            if (proc == null)
            {
                return null;
            }

            string path = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && Directory.Exists(path: path) ? path : null;
        }
        catch
        {
            return null;
        }
    });

    internal static int BuildNativeRuntime(string exeDir, string nativeBuildDir)
    {
        string buildArgs = $"--build \"{nativeBuildDir}\"";
        var psi = new ProcessStartInfo
        {
            FileName = "cmake",
            Arguments = buildArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        try
        {
            using var process = Process.Start(startInfo: psi);
            if (process == null)
            {
                throw new InvalidOperationException("Failed to start cmake.");
            }

            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.Error.Write(value: stderr);
                Console.WriteLine(
                    value:
                    $"Native runtime build failed (cmake exited with code {process.ExitCode})");
                return 1;
            }

            string nativeBinDir = Path.Combine(path1: nativeBuildDir, path2: "bin");
            string nativeLibDir = Path.Combine(path1: nativeBuildDir, path2: "lib");
            string exeNativeBinDir = Path.Combine(path1: exeDir,
                path2: "native",
                path3: "build",
                path4: "bin");
            string exeNativeLibDir = Path.Combine(path1: exeDir,
                path2: "native",
                path3: "build",
                path4: "lib");

            CopyDirectoryFiles(srcDir: nativeBinDir, dstDir: exeNativeBinDir);
            CopyDirectoryFiles(srcDir: nativeLibDir, dstDir: exeNativeLibDir);

            // Also copy DLLs to the exe root (matches csproj LinkBase="." behavior).
            // The compiler itself P/Invokes razorforge_runtime.dll, so the target file may be
            // locked by this process. In that case the already-loaded copy is what this run
            // will use anyway — warn and continue rather than failing the build.
            if (Directory.Exists(path: nativeBinDir))
            {
                foreach (string dll in Directory.GetFiles(path: nativeBinDir,
                             searchPattern: "*.dll"))
                {
                    string dst = Path.Combine(path1: exeDir, path2: Path.GetFileName(path: dll));
                    TryCopyTolerant(src: dll, dst: dst);
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to build native runtime: {ex.Message}");
            Console.WriteLine(
                value: "Make sure CMake is installed and the native runtime has been configured.");
            return 1;
        }
    }

    private static void CopyDirectoryFiles(string srcDir, string dstDir)
    {
        if (!Directory.Exists(path: srcDir))
        {
            return;
        }

        Directory.CreateDirectory(path: dstDir);
        foreach (string file in Directory.GetFiles(path: srcDir))
        {
            string dst = Path.Combine(path1: dstDir, path2: Path.GetFileName(path: file));
            TryCopyTolerant(src: file, dst: dst);
        }
    }

    // Copies a file, tolerating sharing violations when the target is already loaded into this
    // process (e.g. razorforge_runtime.dll, which the compiler itself P/Invokes). On Windows, a
    // loaded DLL is locked against overwrite but can still be renamed — so we move the locked
    // file aside under a unique name and then copy the fresh one into the original path. This
    // guarantees the on-disk artifact is always up to date; the renamed sidecar is harmless and
    // gets cleaned up on a future run when nothing has it open.
    private static void TryCopyTolerant(string src, string dst)
    {
        try
        {
            File.Copy(sourceFileName: src, destFileName: dst, overwrite: true);
            return;
        }
        catch (IOException) when (File.Exists(path: dst))
        {
            // Fall through to rename-aside fallback.
        }

        try
        {
            string staleSidecar =
                $"{dst}.stale-{Environment.ProcessId}-{DateTime.UtcNow.Ticks}";
            File.Move(sourceFileName: dst, destFileName: staleSidecar);
            File.Copy(sourceFileName: src, destFileName: dst, overwrite: false);
            TryDeleteSidecars(targetPath: dst);
        }
        catch (IOException ex)
        {
            Console.WriteLine(
                value:
                $"Warning: could not refresh '{dst}' ({ex.Message}). Using existing copy; rerun to pick up changes.");
        }
    }

    // Best-effort cleanup of previously renamed-aside DLL sidecars. Files still locked by
    // running processes will throw and are ignored.
    private static void TryDeleteSidecars(string targetPath)
    {
        string? dir = Path.GetDirectoryName(path: targetPath);
        if (dir == null) return;
        string prefix = Path.GetFileName(path: targetPath) + ".stale-";
        try
        {
            foreach (string old in Directory.EnumerateFiles(path: dir,
                         searchPattern: prefix + "*"))
            {
                try { File.Delete(path: old); } catch { /* still locked — leave it */ }
            }
        }
        catch { /* directory access issue — non-fatal */ }
    }

    /// <summary>
    /// Resolves an LLVM toolchain tool (clang/opt) to a concrete path. Resolution order:
    /// 1. RAZORFORGE_LLVM_HOME/bin/&lt;tool&gt; — explicit user override.
    /// 2. &lt;dir of RazorForge executable&gt;/toolchain/bin/&lt;tool&gt; — self-contained release
    ///    packages bundle a relocatable LLVM (llvm-mingw on Windows) there.
    /// 3. The bare tool name, resolved from PATH (dev setups).
    /// </summary>
    private static string ResolveToolchainTool(string name)
    {
        string exeName = OperatingSystem.IsWindows() ? name + ".exe" : name;

        string? llvmHome = Environment.GetEnvironmentVariable(variable: "RAZORFORGE_LLVM_HOME");
        if (!string.IsNullOrWhiteSpace(value: llvmHome))
        {
            string fromEnv = Path.Combine(path1: llvmHome, path2: "bin", path3: exeName);
            if (File.Exists(path: fromEnv))
            {
                return fromEnv;
            }
        }

        string bundled = Path.Combine(path1: AppContext.BaseDirectory, path2: "toolchain",
            path3: "bin", path4: exeName);
        return File.Exists(path: bundled) ? bundled : name;
    }

    private static readonly Lazy<string> ClangTool = new(valueFactory: () => ResolveToolchainTool(name: "clang"));
    private static readonly Lazy<string> OptTool = new(valueFactory: () => ResolveToolchainTool(name: "opt"));

    private static void ConfigureToolchainEnvironment(ProcessStartInfo psi, string toolPath)
    {
        if (toolPath == "clang" || toolPath == "opt")
        {
            return;
        }

        string? binDir = Path.GetDirectoryName(path: toolPath);
        string? toolchainDir = binDir == null ? null : Path.GetDirectoryName(path: binDir);
        if (toolchainDir == null)
        {
            return;
        }

        string libDir = Path.Combine(path1: toolchainDir, path2: "lib");
        if (!Directory.Exists(path: libDir))
        {
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            PrependEnvironmentPath(psi, variableName: "LD_LIBRARY_PATH", path: libDir);
        }
        else if (OperatingSystem.IsMacOS())
        {
            PrependEnvironmentPath(psi, variableName: "DYLD_LIBRARY_PATH", path: libDir);
        }
    }

    private static void PrependEnvironmentPath(ProcessStartInfo psi, string variableName, string path)
    {
        psi.Environment.TryGetValue(key: variableName, value: out string? existing);
        psi.Environment[variableName] = string.IsNullOrWhiteSpace(value: existing)
            ? path
            : path + Path.PathSeparator + existing;
    }

    /// <summary>
    /// True when the resolved clang targets *-windows-gnu (llvm-mingw). The bundled Windows
    /// toolchain is mingw-based so linking is self-contained (no Visual Studio / Windows SDK
    /// import libraries needed); its GNU-flavored linker rejects lld-link style /flags, so the
    /// link command line must be built differently.
    /// </summary>
    private static readonly Lazy<bool> ClangIsMingw = new(valueFactory: () =>
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClangTool.Value,
                Arguments = "-dumpmachine",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ConfigureToolchainEnvironment(psi: psi, toolPath: ClangTool.Value);
            using var proc = Process.Start(startInfo: psi);
            if (proc == null)
            {
                return false;
            }

            string triple = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return triple.Contains(value: "mingw") || triple.Contains(value: "windows-gnu");
        }
        catch
        {
            return false;
        }
    });

    /// <summary>
    /// Returns the full path to the compiler-rt builtins library (e.g. clang_rt.builtins-x86_64.lib)
    /// by asking clang where it lives.
    /// </summary>
    private static string? GetCompilerRtBuiltinsLib()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ClangTool.Value,
                Arguments = "--print-libgcc-file-name --rtlib=compiler-rt",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            ConfigureToolchainEnvironment(psi: psi, toolPath: ClangTool.Value);
            using var proc = Process.Start(psi);
            if (proc == null) return null;
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            if (proc.ExitCode == 0 && File.Exists(output))
                return output;
        }
        catch
        {
            // clang not available or doesn't support --print-libgcc-file-name
        }
        return null;
    }

    /// <summary>
    /// Detects the underlying linker tool name from clang's stderr output.
    /// </summary>
    private static string DetectLinkerFromStderr(string stderr)
    {
        if (stderr.Contains(value: "lld-link:"))
        {
            return "lld-link";
        }

        if (stderr.Contains(value: "ld.lld:"))
        {
            return "ld.lld";
        }

        if (stderr.Contains(value: "collect2:"))
        {
            return "collect2";
        }

        if (stderr.Contains(value: "LINK :") || stderr.Contains(value: "LINK:"))
        {
            return "link.exe";
        }

        if (stderr.Contains(value: "ld:"))
        {
            return "ld";
        }

        return "clang";
    }

    /// <summary>Maps a build mode to the LLVM optimization level token (O0/O2/O3/Os).</summary>
    private static string OptLevelString(RfBuildMode buildMode)
    {
        return buildMode switch
        {
            RfBuildMode.Release => "O2",
            RfBuildMode.ReleaseTime => "O3",
            RfBuildMode.ReleaseSpace => "Os",
            _ => "O0"
        };
    }

    /// <summary>
    /// Optimizes <paramref name="llFile"/> into <paramref name="optFile"/> by running LLVM `opt`.
    /// Debug builds run mem2reg+sroa at O0 (readability without semantic change); optimized builds
    /// run the full pipeline at the requested level. Returns 0 on success or 1 if opt fails.
    /// </summary>
    internal static int OptimizeIr(string llFile, string optFile, RfBuildMode buildMode)
    {
        string optPipelineLevel = OptLevelString(buildMode: buildMode);

        // Use -passes='default<Ox>,...' syntax (LLVM 14+; replaces the -Ox -passes=... split form).
        string optPipeline = buildMode == RfBuildMode.Debug
            ? $"default<{optPipelineLevel}>,mem2reg,sroa"
            : $"default<{optPipelineLevel}>";
        string optArgs = $"-S -passes={optPipeline} \"{llFile}\" -o \"{optFile}\"";
        var optPsi = new ProcessStartInfo
        {
            FileName = OptTool.Value,
            Arguments = optArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureToolchainEnvironment(psi: optPsi, toolPath: OptTool.Value);

        try
        {
            using var optProcess = Process.Start(startInfo: optPsi);
            if (optProcess == null)
            {
                Console.WriteLine(value: "Error: Failed to start opt.");
                return 1;
            }

            string optStderr = optProcess.StandardError.ReadToEnd();
            optProcess.WaitForExit();

            if (optProcess.ExitCode != 0)
            {
                Console.Error.WriteLine(value: optStderr.Trim());
                Console.WriteLine(value: $"Optimization failed (opt exited with code {optProcess.ExitCode})");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute opt: {ex.Message}");
            Console.WriteLine(value: "Make sure LLVM 'opt' is installed and on your PATH.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Target-architecture codegen feature flags for the clang codegen/link step.
    ///
    /// On x86-64, `F16` (LLVM `half`) requires the F16C hardware conversion instructions
    /// (vcvtph2ps / vcvtps2ph). Without `+f16c` the backend falls back to a soft-promotion
    /// path that MISCOMPILES half values crossing a call ABI boundary at -O3 — a half return
    /// value or a half spilled across a call decays to 0 (verified: an F16 transcendental loop
    /// accumulated 0 instead of the correct sum without the flag, correct with it). F16C is
    /// present on every x86-64 CPU since ~2012 (Intel Ivy Bridge, AMD Piledriver/Jaguar), which
    /// is well within the supported hardware floor.
    ///
    /// On AArch64 `half` is a first-class hardware type (mandatory FCVT half↔float conversion,
    /// native FP16 arithmetic on ARMv8.2+ / all Apple Silicon), so no extra flag is needed.
    ///
    /// Host-compilation only for now, so this keys on the machine architecture; when explicit
    /// cross-compilation targets land, this should key on the requested target triple instead.
    /// </summary>
    private static string TargetCodegenFlags()
    {
        return RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => " -mf16c",
            _ => "" // AArch64: native half; other arches fall back to clang defaults.
        };
    }

    /// <summary>Links the optimized IR <paramref name="optFile"/> into the native executable <paramref name="exeFile"/> via clang, bundling the runtime from <paramref name="runtimeLibDir"/>. Returns 0 on success.</summary>
    /// <summary>
    /// Builds the clang link-argument fragment for user-declared C libraries: a <c>-L"dir"</c> for each
    /// search path followed by a <c>-l name</c> for each library. Empty inputs yield an empty string.
    /// Pure and side-effect free so it can be unit-tested without invoking the toolchain. The link
    /// driver stays the bundled clang/lld regardless of platform — each <c>-l</c> name resolves to the
    /// platform's library form (<c>libX.so</c> / <c>X.lib</c> / <c>libX.dylib</c>).
    /// </summary>
    internal static string BuildUserLibraryArgs(IReadOnlyList<string>? cLibraries,
        IReadOnlyList<string>? libraryPaths)
    {
        var sb = new System.Text.StringBuilder();
        if (libraryPaths != null)
        {
            foreach (string path in libraryPaths)
            {
                if (!string.IsNullOrWhiteSpace(value: path))
                    sb.Append(value: $" -L\"{path}\"");
            }
        }

        if (cLibraries != null)
        {
            foreach (string lib in cLibraries)
            {
                if (!string.IsNullOrWhiteSpace(value: lib))
                    sb.Append(value: $" -l{lib.Trim()}");
            }
        }

        return sb.ToString();
    }

    // MSVC-target clang needs the CRT and kernel32 import libraries named explicitly when
    // linking from LLVM IR. The mingw-target clang (bundled self-contained toolchain) links
    // its own CRT and the Win32 import libraries automatically.
    private static string WindowsThreadingLibsFragment() =>
        OperatingSystem.IsWindows() && !ClangIsMingw.Value
            ? " -lucrt -lmsvcrt -lkernel32"
            : "";

    // On Linux/macOS the LLVM IR emits direct calls into libm (floor, exp, pow, …) and the
    // pthread/dl runtime. Modern ld defaults to --as-needed, so libm must be named explicitly
    // on the command line or linking fails with "DSO missing from command line". We also embed
    // an rpath pointing at the runtime library directory so the produced executable can locate
    // librazorforge_runtime.so at load time without requiring LD_LIBRARY_PATH.
    private static string UnixRuntimeLibsFragment(string runtimeLibDir) =>
        OperatingSystem.IsWindows()
            ? ""
            : $" -lm -lpthread -ldl -Wl,-rpath,\"{runtimeLibDir}\"";

    // Compiler-RT builtins resolve softfloat/softint symbols that LLVM emits for types
    // without direct hardware support:
    //   fp128 arithmetic: __addtf3, __subtf3, __multf3, __divtf3, __negtf2, __eqtf2, etc.
    //   f16 conversions:  __extendhfsf2, __truncsfhf2
    //   i128 arithmetic:  __divti3, __modti3, __udivti3, __umodti3
    //
    // On Windows, neither MSVC link.exe nor lld-link automatically searches for the clang
    // compiler-rt builtins library when linking an .ll/.obj that was generated from LLVM IR
    // (rather than from a C/C++ source file). We locate the library explicitly via
    //   clang --print-libgcc-file-name --rtlib=compiler-rt
    // and add it directly to the linker command line. Returns false (with a printed error) only
    // when the Windows compiler-rt library can't be located.
    private static bool TryBuildCompilerRtArg(out string compilerRtArg)
    {
        if (OperatingSystem.IsWindows() && !ClangIsMingw.Value)
        {
            string? compilerRtLib = GetCompilerRtBuiltinsLib();
            if (string.IsNullOrWhiteSpace(value: compilerRtLib))
            {
                Console.WriteLine(
                    value: "Failed to locate clang compiler-rt builtins library on Windows.");
                compilerRtArg = "";
                return false;
            }

            compilerRtArg = $" \"{compilerRtLib}\"";
        }
        else
        {
            compilerRtArg = " --rtlib=compiler-rt";
        }

        return true;
    }

    // Windows always links via lld. On Linux/macOS the system linker is fine for dev
    // setups, but when clang came from a bundled/explicit toolchain the host may have
    // no binutils at all — use that toolchain's own ld.lld (clang searches its own
    // bin directory for it first).
    private static string LldFlagFragment()
    {
        bool clangIsBundled = ClangTool.Value != "clang";
        return OperatingSystem.IsWindows() || clangIsBundled ? " -fuse-ld=lld" : "";
    }

    // lld-link-only flag (MSVC-target clang). The mingw toolchain's GNU-flavored ld.lld rejects
    // /slash-style options. /errorlimit:0 surfaces every undefined-symbol error instead of
    // capping at ~20.
    private static string LinkerErrorLimitFragment() =>
        OperatingSystem.IsWindows() && !ClangIsMingw.Value ? " -Wl,/errorlimit:0" : "";

    // lld-link-only flag (MSVC-target clang). The embedded asInvoker manifest stops Windows'
    // Application Information Service from heuristically requesting UAC elevation for exe names
    // containing "install"/"update"/"setup"/"patch"/"test_dispatch"/… (it never inspects the
    // binary itself).
    private static string ManifestUacFragment() =>
        OperatingSystem.IsWindows() && !ClangIsMingw.Value
            ? " -Wl,\"/MANIFESTUAC:level='asInvoker' uiAccess='false'\" -Wl,/MANIFEST:EMBED"
            : "";

    // The macOS system libraries (-lm/-lSystem/...) only exist as SDK stubs; point the driver at
    // the Command Line Tools SDK explicitly (see MacSdkPath).
    private static string MacSysrootFragment() =>
        OperatingSystem.IsMacOS() && !string.IsNullOrWhiteSpace(value: MacSdkPath.Value)
            ? $" -isysroot \"{MacSdkPath.Value}\""
            : "";

    internal static int LinkExecutable(string optFile, string exeFile, string runtimeLibDir,
        RfBuildMode buildMode, IReadOnlyList<string>? cLibraries = null,
        IReadOnlyList<string>? libraryPaths = null)
    {
        // Compile .ll -> .exe using clang (clang uses -Ox flag style, not opt's -passes= form)
        string clangOptLevel = $"-{OptLevelString(buildMode: buildMode)}";
        // Preserve frame pointers in debug/release for accurate platform stack unwinding.
        // release-time/release-space omit frame pointers for maximum performance.
        string framePointerFlag = buildMode is RfBuildMode.Debug or RfBuildMode.Release
            ? " -fno-omit-frame-pointer"
            : "";
        string windowsThreadingLibs = WindowsThreadingLibsFragment();
        string unixRuntimeLibs = UnixRuntimeLibsFragment(runtimeLibDir: runtimeLibDir);
        if (!TryBuildCompilerRtArg(compilerRtArg: out string compilerRtArg))
        {
            return 1;
        }

        string lldFlag = LldFlagFragment();
        string linkerErrorLimitFlag = LinkerErrorLimitFragment();
        string manifestUacFlag = ManifestUacFragment();
        string macSysrootArg = MacSysrootFragment();
        // User-declared C libraries (config.toml [target] c_libraries / library_paths). Placed
        // after the user object + runtime so `-l` symbol resolution sees the referencing objects first.
        string userLibArgs = BuildUserLibraryArgs(cLibraries: cLibraries, libraryPaths: libraryPaths);
        string clangArgs =
            $"{clangOptLevel}{framePointerFlag}{TargetCodegenFlags()}{lldFlag}{macSysrootArg} -o \"{exeFile}\" \"{optFile}\" -L\"{runtimeLibDir}\" -lrazorforge_runtime{userLibArgs}{compilerRtArg}{windowsThreadingLibs}{unixRuntimeLibs}{linkerErrorLimitFlag}{manifestUacFlag}";

        var clangPsi = new ProcessStartInfo
        {
            FileName = ClangTool.Value,
            Arguments = clangArgs,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        ConfigureToolchainEnvironment(psi: clangPsi, toolPath: ClangTool.Value);

        try
        {
            using var clangProcess = Process.Start(startInfo: clangPsi);
            if (clangProcess == null)
            {
                Console.WriteLine(value: "Error: Failed to start clang.");
                return 1;
            }

            // Read stdout/stderr concurrently to avoid pipe-buffer deadlock when clang/lld
            // emits a lot of output (e.g. many LNK2019 errors on a ~60k-line IR).
            string clangStdout = "";
            string clangStderr = "";
            var stdoutThread = new Thread(() => clangStdout = clangProcess.StandardOutput.ReadToEnd());
            var stderrThread = new Thread(() => clangStderr = clangProcess.StandardError.ReadToEnd());
            stdoutThread.Start();
            stderrThread.Start();
            clangProcess.WaitForExit();
            stdoutThread.Join();
            stderrThread.Join();

            if (clangProcess.ExitCode != 0)
            {
                // MSVC's link.exe sends detailed errors (LNK2019) to stdout,
                // while the summary (LNK1120) goes to stderr -> print both.
                if (!string.IsNullOrWhiteSpace(value: clangStdout))
                {
                    Console.Error.Write(value: clangStdout);
                }

                if (!string.IsNullOrWhiteSpace(value: clangStderr))
                {
                    Console.Error.Write(value: clangStderr);
                }

                string allOutput = clangStdout + clangStderr;
                string linker = DetectLinkerFromStderr(stderr: allOutput);
                Console.WriteLine(
                    value: $"Linking failed ({linker} exited with code {clangProcess.ExitCode})");
                return 1;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(value: $"Failed to execute clang: {ex.Message}");
            Console.WriteLine(
                value: "Make sure LLVM/Clang is installed and 'clang' is on your PATH.");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Copies the runtime DLL (and its shared-library dependencies) from <paramref name="exeDir"/>
    /// next to the output <paramref name="exeFile"/> so the loader can find them at runtime.
    /// </summary>
    internal static void StageRuntimeDlls(string exeDir, string exeFile)
    {
        string? outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile));
        if (outputDir == null)
        {
            return;
        }

        foreach (string dllName in NativeRuntimeDlls)
        {
            string srcDll = Path.Combine(path1: exeDir, path2: dllName);
            if (File.Exists(path: srcDll))
            {
                string dstDll = Path.Combine(path1: outputDir, path2: dllName);
                TryCopyTolerant(src: srcDll, dst: dstDll);
            }
        }
    }

    /// <summary>
    /// Stages each dynamically-linked <c>@link</c>/<c>c_libraries</c> dependency's shared object next to
    /// the output <paramref name="exeFile"/>, searching the <paramref name="libraryPaths"/> (<c>-L</c>
    /// dirs) for the platform filename (<c>NAME.dll</c> / <c>libNAME.so</c> / <c>libNAME.dylib</c>). A
    /// library declared <c>static</c> in a <c>[libraries.NAME]</c> table is baked into the exe and skipped;
    /// a library whose shared object is not found (e.g. a system library on the loader's default path) is
    /// simply not copied. Mirrors <see cref="StageRuntimeDlls"/> for the runtime's own DLL.
    /// </summary>
    internal static void StageUserLibraryDlls(string exeFile, IReadOnlyList<string> cLibraries,
        IReadOnlyList<string>? libraryPaths,
        IReadOnlyDictionary<string, CLibrary>? libraryConfigs)
    {
        if (libraryPaths is not { Count: > 0 })
        {
            return;
        }

        string? outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile));
        if (outputDir == null)
        {
            return;
        }

        // A statically-linked library has no runtime DLL to stage — it is pulled into the exe at link time.
        var staticNames = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (libraryConfigs != null)
        {
            foreach (CLibrary cfg in libraryConfigs.Values)
            {
                if (cfg.Kind == CLinkKind.Static)
                {
                    staticNames.Add(item: cfg.Name);
                }
            }
        }

        foreach (string lib in cLibraries)
        {
            if (staticNames.Contains(item: lib))
            {
                continue;
            }

            string dllName = SharedObjectFileName(libName: lib);
            foreach (string dir in libraryPaths)
            {
                string src = Path.Combine(path1: dir, path2: dllName);
                if (File.Exists(path: src))
                {
                    TryCopyTolerant(src: src, dst: Path.Combine(path1: outputDir, path2: dllName));
                    break;
                }
            }
        }
    }

    /// <summary>The platform shared-object filename for an <c>-l</c> link name: <c>NAME.dll</c> on Windows,
    /// <c>libNAME.dylib</c> on macOS, <c>libNAME.so</c> elsewhere.</summary>
    private static string SharedObjectFileName(string libName) =>
        OperatingSystem.IsWindows() ? $"{libName}.dll"
        : OperatingSystem.IsMacOS() ? $"lib{libName}.dylib"
        : $"lib{libName}.so";

    /// <summary>
    /// Deletes stale per-target outputs that can cause buildandrun to execute or link against
    /// previous artifacts after source, stdlib, or runtime changes.
    /// </summary>
    internal static void CleanBuildAndRunOutputs(string llFile, string optFile, string exeFile)
    {
        // Normalize before taking the directory: for a bare relative name like
        // "smoke.exe" GetDirectoryName returns "" (not null), and enumerating ""
        // throws. Full-path first makes the working-directory case work.
        string outputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile)) ?? ".";
        string basePath = Path.Combine(
            path1: outputDir,
            path2: Path.GetFileNameWithoutExtension(path: exeFile));

        // Sweep any leftover *.stale.* files from prior runs where TryRemoveBuildArtifact
        // had to fall back to rename-aside because the original was locked.
        try
        {
            foreach (string stale in Directory.EnumerateFiles(path: outputDir, searchPattern: "*.stale.*"))
            {
                TryRemoveBuildArtifact(path: stale);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Directory unreadable — non-fatal, just skip the sweep.
        }

        string exeOutputDir = Path.GetDirectoryName(path: Path.GetFullPath(path: exeFile)) ?? ".";
        string[] pathsToDelete =
        [
            llFile,
            optFile,
            exeFile,
            basePath + ".obj",
            basePath + ".pdb",
            basePath + ".ilk",
            basePath + ".exp",
            basePath + ".lib",
            .. NativeRuntimeDlls.Select(selector: dll =>
                Path.Combine(path1: exeOutputDir, path2: dll))
        ];

        foreach (string path in pathsToDelete.Distinct(comparer: StringComparer.OrdinalIgnoreCase))
        {
            TryRemoveBuildArtifact(path: path);
        }
    }

    /// <summary>
    /// Best-effort delete with retry + rename-aside fallback. Windows frequently briefly locks
    /// freshly-written PE files (Defender real-time scan, indexers, lingering child processes).
    /// A short retry covers transient locks; renaming the file out of the way unblocks the next
    /// link step even when the original handle is still open (works as long as the holder opened
    /// with FILE_SHARE_DELETE, which Defender and most scanners do).
    /// </summary>
    private static void TryRemoveBuildArtifact(string path)
    {
        if (!File.Exists(path: path)) return;

        int[] delaysMs = [0, 50, 100, 200];
        foreach (int delay in delaysMs)
        {
            if (delay > 0) Thread.Sleep(millisecondsTimeout: delay);
            try
            {
                File.Delete(path: path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (delay == delaysMs[^1])
                {
                    // Last attempt failed — try renaming out of the way so the next build can write here.
                    string aside = $"{path}.stale.{Guid.NewGuid():N}";
                    try
                    {
                        File.Move(sourceFileName: path, destFileName: aside);
                        return;
                    }
                    catch (Exception renameEx) when (renameEx is IOException or UnauthorizedAccessException)
                    {
                        Console.WriteLine(value: $"Warning: Could not remove or rename stale build artifact '{path}': {ex.Message}");
                    }
                }
            }
        }
    }
}
