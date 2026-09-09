using System.Runtime.InteropServices;
using System.Text;
using LLVMSharp.Interop;
using Xunit.Abstractions;

#pragma warning disable xUnit1004
namespace RazorForge.Tests.Perf;

/// <summary>
/// STAGE 2 SPIKE (ORC JIT feasibility): can LLVMSharp drive LLVM-C's ORC LLJIT in-process on Windows to
/// JIT a trivial module and CALL its function? De-risks the whole dev-loop-JIT direction BEFORE any real
/// wiring: LLVMSharp bindings are 20.x, the system LLVM-C.dll is 21.x, and the bundled toolchain is 22 —
/// this proves the C-API/ORC surface lines up (or shows exactly where the version skew breaks). Stage
/// markers go to stderr FLUSHED, so a hard native crash still leaves a trail of the last stage reached.
/// </summary>
public sealed unsafe class OrcJitSpike
{
    private readonly ITestOutputHelper _out;
    public OrcJitSpike(ITestOutputHelper output)
    {
        _out = output;
    }

    // LLVMSharp.Interop installs its OWN DllImportResolver for "libLLVM", so we cannot add another. Pre-load
    // an LLVM-C shared library under the base name "libLLVM" by ABSOLUTE path: Windows caches it by base
    // name, so the later DllImport("libLLVM") resolves to it (LLVMSharp's resolver returns Zero → default
    // resolution finds the cached module).
    private static nint _llvmHandle;
    static OrcJitSpike()
    {
        string dir = Path.GetDirectoryName(path: typeof(OrcJitSpike).Assembly.Location) ?? ".";
        string staged = Path.Combine(path1: dir, path2: "libLLVM.dll");
        string src = File.Exists(path: staged)
            ? staged
            : @"C:\Program Files\LLVM\bin\LLVM-C.dll";
        _llvmHandle = NativeLibrary.Load(libraryPath: src);
        bool hasSym = NativeLibrary.TryGetExport(handle: _llvmHandle,
            name: "LLVMOrcCreateNewThreadSafeContextFromLLVMContext",
            address: out nint fromCtxPtr);
        _fromCtx =
            (delegate* unmanaged[Cdecl]<LLVMOpaqueContext*, LLVMOrcOpaqueThreadSafeContext*>)
            fromCtxPtr;
        Console.Error.WriteLine(
            value: $"[ORC-SPIKE] loaded {src} | FromLLVMContext export={hasSym}");
        Console.Error.Flush();
    }

    // LLVM removed LLVMOrcThreadSafeContextGetContext (which LLVMSharp 20 relies on) around LLVM 21 and
    // replaced it with LLVMOrcCreateNewThreadSafeContextFromLLVMContext, which LLVMSharp 20 does NOT bind.
    // Resolve it from OUR explicitly-loaded handle (a plain DllImport("libLLVM") binds ambiguously when
    // more than one libLLVM is in the process).
    private static delegate* unmanaged[Cdecl]<LLVMOpaqueContext*, LLVMOrcOpaqueThreadSafeContext*>
        _fromCtx;

    private static void Stage(string s)
    {
        Console.Error.WriteLine(value: $"[ORC-SPIKE] {s}");
        Console.Error.Flush();
    }

    private static void Check(LLVMOpaqueError* err, string what)
    {
        if (err != null)
        {
            sbyte* msg = LLVM.GetErrorMessage(Err: err);
            string m = msg != null
                ? new string(value: msg)
                : "<null>";
            throw new Exception(message: $"{what} failed: {m}");
        }
    }

    [Fact(Skip =
        "Local ORC-JIT feasibility spike: needs a system/bundled libLLVM staged next to the test binary; not run in CI.")]
    public void OrcJit_TrivialModule_ReturnsAnswer()
    {
        Stage(s: "start");

        // Initialize the native target — ORC needs the host target/asm printer registered.
        LLVM.InitializeNativeTarget();
        LLVM.InitializeNativeAsmPrinter();
        Stage(s: "native target initialized");

        byte[] ir =
            Encoding.ASCII.GetBytes(s: "define i32 @answer() {\nentry:\n  ret i32 42\n}\n");
        byte[] modName = Encoding.ASCII.GetBytes(s: "spike\0");
        byte[] symName = Encoding.ASCII.GetBytes(s: "answer\0");

        // Own a plain context, parse the IR into it, then hand the context to a ThreadSafeContext.
        LLVMOpaqueContext* ctx = LLVM.ContextCreate();
        LLVMOpaqueModule* mod;
        sbyte* parseErr;
        fixed (byte* irp = ir)
        fixed (byte* np = modName)
        {
            LLVMOpaqueMemoryBuffer* buf = LLVM.CreateMemoryBufferWithMemoryRangeCopy(
                InputData: (sbyte*)irp,
                InputDataLength: (nuint)ir.Length,
                BufferName: (sbyte*)np);
            int rc = LLVM.ParseIRInContext(ContextRef: ctx,
                MemBuf: buf,
                OutM: &mod,
                OutMessage: &parseErr);
            Assert.True(condition: rc == 0,
                userMessage:
                $"ParseIRInContext failed: {(parseErr != null ? new string(value: parseErr) : "?")}");
        }

        Stage(s: "IR parsed");

        LLVMOrcOpaqueThreadSafeContext* tsCtx = _fromCtx(ctx);
        Stage(s: "threadsafe context created (from our context)");
        LLVMOrcOpaqueThreadSafeModule* tsm =
            LLVM.OrcCreateNewThreadSafeModule(M: mod, TSCtx: tsCtx);
        LLVMOrcOpaqueLLJITBuilder* builder = LLVM.OrcCreateLLJITBuilder();
        LLVMOrcOpaqueLLJIT* jit;
        Check(err: LLVM.OrcCreateLLJIT(Result: &jit, Builder: builder), what: "OrcCreateLLJIT");
        Stage(s: "LLJIT created");

        LLVMOrcOpaqueJITDylib* dylib = LLVM.OrcLLJITGetMainJITDylib(J: jit);
        Check(err: LLVM.OrcLLJITAddLLVMIRModule(J: jit, JD: dylib, TSM: tsm),
            what: "OrcLLJITAddLLVMIRModule");
        Stage(s: "module added");

        ulong addr;
        fixed (byte* sp = symName)
        {
            Check(err: LLVM.OrcLLJITLookup(J: jit, Result: &addr, Name: (sbyte*)sp),
                what: "OrcLLJITLookup");
        }

        Stage(s: $"looked up answer @ 0x{addr:X}");
        Assert.NotEqual(expected: 0UL, actual: addr);

        var fn = (delegate* unmanaged[Cdecl]<int>)addr;
        int result = fn();
        Stage(s: $"called answer() => {result}");
        _out.WriteLine(message: $"answer() returned {result}");

        Assert.Equal(expected: 42, actual: result);
    }

    /// <summary>
    /// SUB-SPIKE #1 (runtime symbol resolution): the last core uncertainty for JIT-and-run — can JIT'd
    /// code call an <c>rf_*</c> native-runtime function? Loads razorforge_runtime.dll into the process,
    /// adds an ORC process-search generator, JITs a module that CALLS <c>rf_current_thread_id()</c>, and
    /// asserts the JIT'd result equals a direct call to the same runtime function on the same thread.
    /// </summary>
    [Fact(Skip =
        "Local ORC-JIT feasibility spike: needs a system/bundled libLLVM staged next to the test binary; not run in CI.")]
    public void OrcJit_ResolvesRuntimeSymbol()
    {
        Stage(s: "rt: start");

        // Load the native runtime so its rf_* exports are visible to a process-wide symbol search.
        string dir = Path.GetDirectoryName(path: typeof(OrcJitSpike).Assembly.Location) ?? ".";
        string rtPath = Path.Combine(path1: dir, path2: "razorforge_runtime.dll");
        nint rt = NativeLibrary.Load(libraryPath: File.Exists(path: rtPath)
            ? rtPath
            : "razorforge_runtime");
        Stage(s: $"rt: runtime loaded ({rtPath})");

        LLVM.InitializeNativeTarget();
        LLVM.InitializeNativeAsmPrinter();

        // Module: declare the runtime function, define a wrapper that calls it.
        byte[] ir = Encoding.ASCII.GetBytes(s: "declare i64 @rf_current_thread_id()\n" +
                                               "define i64 @callrt() {\nentry:\n  %t = call i64 @rf_current_thread_id()\n  ret i64 %t\n}\n");
        byte[] modName = Encoding.ASCII.GetBytes(s: "rtspike\0");
        byte[] symName = Encoding.ASCII.GetBytes(s: "callrt\0");

        LLVMOpaqueContext* ctx = LLVM.ContextCreate();
        LLVMOpaqueModule* mod;
        sbyte* parseErr;
        fixed (byte* irp = ir)
        fixed (byte* np = modName)
        {
            LLVMOpaqueMemoryBuffer* buf = LLVM.CreateMemoryBufferWithMemoryRangeCopy(
                InputData: (sbyte*)irp,
                InputDataLength: (nuint)ir.Length,
                BufferName: (sbyte*)np);
            int rc = LLVM.ParseIRInContext(ContextRef: ctx,
                MemBuf: buf,
                OutM: &mod,
                OutMessage: &parseErr);
            Assert.True(condition: rc == 0,
                userMessage:
                $"ParseIRInContext failed: {(parseErr != null ? new string(value: parseErr) : "?")}");
        }

        LLVMOrcOpaqueThreadSafeContext* tsCtx = _fromCtx(ctx);
        LLVMOrcOpaqueThreadSafeModule* tsm =
            LLVM.OrcCreateNewThreadSafeModule(M: mod, TSCtx: tsCtx);

        LLVMOrcOpaqueLLJITBuilder* builder = LLVM.OrcCreateLLJITBuilder();
        LLVMOrcOpaqueLLJIT* jit;
        Check(err: LLVM.OrcCreateLLJIT(Result: &jit, Builder: builder), what: "OrcCreateLLJIT");
        LLVMOrcOpaqueJITDylib* dylib = LLVM.OrcLLJITGetMainJITDylib(J: jit);

        // Process-search generator: resolves any symbol loaded in the process (incl. rf_* now that the
        // runtime DLL is loaded). This is what lets JIT'd RF code link against the native runtime.
        sbyte prefix = LLVM.OrcLLJITGetGlobalPrefix(J: jit);
        LLVMOrcOpaqueDefinitionGenerator* gen;
        Check(err: LLVM.OrcCreateDynamicLibrarySearchGeneratorForProcess(Result: &gen,
                GlobalPrefx: prefix,
                Filter: null,
                FilterCtx: null),
            what: "GeneratorForProcess");
        LLVM.OrcJITDylibAddGenerator(JD: dylib, DG: gen);
        Stage(s: "rt: process-search generator added");

        Check(err: LLVM.OrcLLJITAddLLVMIRModule(J: jit, JD: dylib, TSM: tsm),
            what: "OrcLLJITAddLLVMIRModule");

        ulong addr;
        fixed (byte* sp = symName)
        {
            Check(err: LLVM.OrcLLJITLookup(J: jit, Result: &addr, Name: (sbyte*)sp),
                what: "OrcLLJITLookup(callrt)");
        }

        Stage(s: $"rt: looked up callrt @ 0x{addr:X}");

        ulong jitTid = ((delegate* unmanaged[Cdecl]<ulong>)addr)();
        // Direct call to the same runtime export, on the same thread.
        NativeLibrary.TryGetExport(handle: rt,
            name: "rf_current_thread_id",
            address: out nint directPtr);
        ulong directTid = ((delegate* unmanaged[Cdecl]<ulong>)directPtr)();
        Stage(s: $"rt: jitTid={jitTid} directTid={directTid}");
        _out.WriteLine(message: $"JIT'd rf_current_thread_id()={jitTid}  direct={directTid}");

        Assert.NotEqual(expected: 0UL, actual: jitTid);
        Assert.Equal(expected: directTid, actual: jitTid);
    }
}
