using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using LLVMSharp.Interop;

namespace Builder;

/// <summary>
/// In-process ORC LLJIT executor: JITs a full RazorForge LLVM-IR module at -O0 and calls its
/// <c>@main(argc, argv)</c> directly, skipping the opt/clang/lld subprocess chain, the on-disk exe, and
/// the process spawn that <see cref="Program.BuildAndRun"/>'s AOT path pays. This is Stage 2b of the
/// dev-loop-speed work — paired with the warm-compile daemon (which removes the ~5 s stdlib SA), it takes
/// the edit→run loop toward the sub-250 ms goal.
///
/// The emitted <c>@main</c> is self-contained: it calls <c>rf_runtime_init()</c>, sets trace mode, runs
/// <c>start()</c>, and returns 0 — so calling it directly does full runtime initialization exactly as the
/// AOT entry point would. The JIT runs IN THIS PROCESS, so the program's stdout/stderr/stdin are the
/// caller's own streams (no forwarding) and a program crash (RF fails loudly by aborting) surfaces as this
/// process's crash — identical to running the AOT exe. That is why JIT-and-run happens client-side, never
/// in the daemon (whose stdio the user can't see).
///
/// Version-skew note: the LLVMSharp bindings are 20.x but the staged <c>libLLVM.dll</c> is 21.x, which
/// dropped <c>LLVMOrcThreadSafeContextGetContext</c> in favour of
/// <c>LLVMOrcCreateNewThreadSafeContextFromLLVMContext</c> (which 20.x does not bind). We hand-resolve that
/// one export from our explicitly-loaded module handle; everything else lines up across the C-API surface.
/// (Proven end-to-end first by tests/Perf/OrcJitSpike.cs, incl. rf_* runtime-symbol resolution.)
/// </summary>
internal static unsafe class OrcJitExecutor
{
    private static readonly object InitLock = new();
    private static bool _initialized;
    private static IntPtr _llvmHandle;
    private static IntPtr _runtimeHandle;

    // LLVM 21 replaced LLVMOrcThreadSafeContextGetContext with this; LLVMSharp 20 doesn't bind it, so we
    // resolve it from our own libLLVM handle and call it through a function pointer.
    private static delegate* unmanaged[Cdecl]<LLVMOpaqueContext*, LLVMOrcOpaqueThreadSafeContext*> _fromCtx;

    /// <summary>Whether a JIT run is even possible in this layout (libLLVM + runtime DLL locatable).
    /// A false lets the caller fall back to the AOT build path with a clear message.</summary>
    public static bool TryInitialize(out string? error)
    {
        lock (InitLock)
        {
            if (_initialized)
            {
                error = null;
                return true;
            }

            try
            {
                string dir = Path.GetDirectoryName(path: typeof(OrcJitExecutor).Assembly.Location) ?? ".";

                // libLLVM: the LLVM-C shared library staged next to our assembly (falls back to a system
                // install). Loaded by absolute path so the export hand-resolution binds unambiguously.
                string stagedLlvm = Path.Combine(path1: dir, path2: "libLLVM.dll");
                string llvmSrc = File.Exists(path: stagedLlvm)
                    ? stagedLlvm
                    : @"C:\Program Files\LLVM\bin\LLVM-C.dll";
                _llvmHandle = NativeLibrary.Load(libraryPath: llvmSrc);
                if (!NativeLibrary.TryGetExport(handle: _llvmHandle,
                        name: "LLVMOrcCreateNewThreadSafeContextFromLLVMContext", address: out IntPtr fromCtxPtr))
                {
                    error = $"libLLVM at '{llvmSrc}' is missing LLVMOrcCreateNewThreadSafeContextFromLLVMContext.";
                    return false;
                }
                _fromCtx = (delegate* unmanaged[Cdecl]<LLVMOpaqueContext*, LLVMOrcOpaqueThreadSafeContext*>)fromCtxPtr;

                // Load the native runtime so its rf_* exports are visible to ORC's process-wide symbol
                // search (this is what lets JIT'd RF code link against rf_console_show, the scheduler, …).
                string rtPath = Path.Combine(path1: dir, path2: "razorforge_runtime.dll");
                _runtimeHandle = NativeLibrary.Load(
                    libraryPath: File.Exists(path: rtPath) ? rtPath : "razorforge_runtime");

                LLVM.InitializeNativeTarget();
                LLVM.InitializeNativeAsmPrinter();

                _initialized = true;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// JITs <paramref name="llvmIr"/> and calls its <c>@main</c> with the given program arguments (argv[0]
    /// is <paramref name="programName"/>). Returns the program's exit code. The JIT and code stay mapped
    /// for the life of the process — RF's runtime spawns scheduler threads that may outlive <c>main</c>'s
    /// return, so we deliberately do NOT tear the JIT down (the process exit that follows a one-shot run
    /// reclaims everything, exactly as the AOT exe's process exit does).
    /// </summary>
    public static int JitAndRun(string llvmIr, string programName, string[] programArgs)
    {
        if (!TryInitialize(out string? error))
        {
            throw new InvalidOperationException(message: $"ORC JIT unavailable: {error}");
        }

        byte[] ir = Encoding.ASCII.GetBytes(s: llvmIr);
        byte[] modName = Encoding.ASCII.GetBytes(s: "rf_jit\0");

        LLVMOpaqueContext* ctx = LLVM.ContextCreate();
        LLVMOpaqueModule* mod;
        sbyte* parseErr;
        fixed (byte* irp = ir)
        fixed (byte* np = modName)
        {
            LLVMOpaqueMemoryBuffer* buf =
                LLVM.CreateMemoryBufferWithMemoryRangeCopy(InputData: (sbyte*)irp, InputDataLength: (UIntPtr)ir.Length,
                    BufferName: (sbyte*)np);
            int rc = LLVM.ParseIRInContext(ContextRef: ctx, MemBuf: buf, OutM: &mod, OutMessage: &parseErr);
            if (rc != 0)
            {
                string m = parseErr != null ? new string(value: parseErr) : "unknown parse error";
                throw new InvalidOperationException(message: $"JIT IR parse failed: {m}");
            }
        }

        LLVMOrcOpaqueThreadSafeContext* tsCtx = _fromCtx(ctx);
        LLVMOrcOpaqueThreadSafeModule* tsm = LLVM.OrcCreateNewThreadSafeModule(M: mod, TSCtx: tsCtx);

        LLVMOrcOpaqueLLJITBuilder* builder = LLVM.OrcCreateLLJITBuilder();
        LLVMOrcOpaqueLLJIT* jit;
        CheckErr(err: LLVM.OrcCreateLLJIT(Result: &jit, Builder: builder), what: "OrcCreateLLJIT");

        LLVMOrcOpaqueJITDylib* dylib = LLVM.OrcLLJITGetMainJITDylib(J: jit);

        // Process-search generator: resolves any symbol already loaded in the process, including the rf_*
        // runtime exports (razorforge_runtime.dll is loaded in TryInitialize).
        sbyte prefix = LLVM.OrcLLJITGetGlobalPrefix(J: jit);
        LLVMOrcOpaqueDefinitionGenerator* gen;
        CheckErr(err: LLVM.OrcCreateDynamicLibrarySearchGeneratorForProcess(Result: &gen, GlobalPrefx: prefix,
                Filter: null, FilterCtx: null),
            what: "GeneratorForProcess");
        LLVM.OrcJITDylibAddGenerator(JD: dylib, DG: gen);

        CheckErr(err: LLVM.OrcLLJITAddLLVMIRModule(J: jit, JD: dylib, TSM: tsm), what: "OrcLLJITAddLLVMIRModule");

        byte[] mainName = Encoding.ASCII.GetBytes(s: "main\0");
        ulong addr;
        fixed (byte* sp = mainName)
        {
            CheckErr(err: LLVM.OrcLLJITLookup(J: jit, Result: &addr, Name: (sbyte*)sp), what: "OrcLLJITLookup(main)");
        }
        if (addr == 0)
        {
            throw new InvalidOperationException(message: "JIT could not resolve @main.");
        }

        // Build a C argv: argv[0] = program name, then the program args, NULL-terminated (argv[argc]).
        var args = new string[programArgs.Length + 1];
        args[0] = programName;
        Array.Copy(sourceArray: programArgs, sourceIndex: 0, destinationArray: args, destinationIndex: 1,
            length: programArgs.Length);
        int argc = args.Length;

        byte** argv = (byte**)Marshal.AllocHGlobal(cb: (argc + 1) * sizeof(nint));
        try
        {
            for (int i = 0; i < argc; i++)
            {
                argv[i] = (byte*)Marshal.StringToHGlobalAnsi(s: args[i]);
            }
            argv[argc] = null;

            var fn = (delegate* unmanaged[Cdecl]<int, byte**, int>)addr;
            return fn(argc, argv);
        }
        finally
        {
            for (int i = 0; i < argc; i++)
            {
                if (argv[i] != null)
                {
                    Marshal.FreeHGlobal(hglobal: (IntPtr)argv[i]);
                }
            }
            Marshal.FreeHGlobal(hglobal: (IntPtr)argv);
        }
    }

    private static void CheckErr(LLVMOpaqueError* err, string what)
    {
        if (err != null)
        {
            sbyte* msg = LLVM.GetErrorMessage(Err: err);
            string m = msg != null ? new string(value: msg) : "<null>";
            throw new InvalidOperationException(message: $"{what} failed: {m}");
        }
    }
}
