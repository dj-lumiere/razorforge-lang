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
    public OrcJitSpike(ITestOutputHelper output) => _out = output;

    // LLVMSharp.Interop installs its OWN DllImportResolver for "libLLVM", so we cannot add another. Pre-load
    // an LLVM-C shared library under the base name "libLLVM" by ABSOLUTE path: Windows caches it by base
    // name, so the later DllImport("libLLVM") resolves to it (LLVMSharp's resolver returns Zero → default
    // resolution finds the cached module).
    private static IntPtr _llvmHandle;
    static OrcJitSpike()
    {
        string dir = Path.GetDirectoryName(typeof(OrcJitSpike).Assembly.Location) ?? ".";
        string staged = Path.Combine(dir, "libLLVM.dll");
        string src = File.Exists(staged) ? staged : @"C:\Program Files\LLVM\bin\LLVM-C.dll";
        _llvmHandle = NativeLibrary.Load(src);
        bool hasSym = NativeLibrary.TryGetExport(_llvmHandle,
            "LLVMOrcCreateNewThreadSafeContextFromLLVMContext", out IntPtr fromCtxPtr);
        _fromCtx = (delegate* unmanaged[Cdecl]<LLVMOpaqueContext*, LLVMOrcOpaqueThreadSafeContext*>)fromCtxPtr;
        Console.Error.WriteLine($"[ORC-SPIKE] loaded {src} | FromLLVMContext export={hasSym}");
        Console.Error.Flush();
    }

    // LLVM removed LLVMOrcThreadSafeContextGetContext (which LLVMSharp 20 relies on) around LLVM 21 and
    // replaced it with LLVMOrcCreateNewThreadSafeContextFromLLVMContext, which LLVMSharp 20 does NOT bind.
    // Resolve it from OUR explicitly-loaded handle (a plain DllImport("libLLVM") binds ambiguously when
    // more than one libLLVM is in the process).
    private static delegate* unmanaged[Cdecl]<LLVMOpaqueContext*, LLVMOrcOpaqueThreadSafeContext*> _fromCtx;

    private static void Stage(string s)
    {
        Console.Error.WriteLine($"[ORC-SPIKE] {s}");
        Console.Error.Flush();
    }

    private static void Check(LLVMOpaqueError* err, string what)
    {
        if (err != null)
        {
            sbyte* msg = LLVM.GetErrorMessage(err);
            string m = msg != null ? new string(msg) : "<null>";
            throw new Exception($"{what} failed: {m}");
        }
    }

    [Fact(Skip = "Local ORC-JIT feasibility spike: needs a system/bundled libLLVM staged next to the test binary; not run in CI.")]
    public void OrcJit_TrivialModule_ReturnsAnswer()
    {
        Stage("start");

        // Initialize the native target — ORC needs the host target/asm printer registered.
        LLVM.InitializeNativeTarget();
        LLVM.InitializeNativeAsmPrinter();
        Stage("native target initialized");

        byte[] ir = Encoding.ASCII.GetBytes("define i32 @answer() {\nentry:\n  ret i32 42\n}\n");
        byte[] modName = Encoding.ASCII.GetBytes("spike\0");
        byte[] symName = Encoding.ASCII.GetBytes("answer\0");

        // Own a plain context, parse the IR into it, then hand the context to a ThreadSafeContext.
        LLVMOpaqueContext* ctx = LLVM.ContextCreate();
        LLVMOpaqueModule* mod;
        sbyte* parseErr;
        fixed (byte* irp = ir)
        fixed (byte* np = modName)
        {
            LLVMOpaqueMemoryBuffer* buf =
                LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)irp, (UIntPtr)ir.Length, (sbyte*)np);
            int rc = LLVM.ParseIRInContext(ctx, buf, &mod, &parseErr);
            Assert.True(rc == 0, $"ParseIRInContext failed: {(parseErr != null ? new string(parseErr) : "?")}");
        }
        Stage("IR parsed");

        LLVMOrcOpaqueThreadSafeContext* tsCtx = _fromCtx(ctx);
        Stage("threadsafe context created (from our context)");
        LLVMOrcOpaqueThreadSafeModule* tsm = LLVM.OrcCreateNewThreadSafeModule(mod, tsCtx);
        LLVMOrcOpaqueLLJITBuilder* builder = LLVM.OrcCreateLLJITBuilder();
        LLVMOrcOpaqueLLJIT* jit;
        Check(LLVM.OrcCreateLLJIT(&jit, builder), "OrcCreateLLJIT");
        Stage("LLJIT created");

        LLVMOrcOpaqueJITDylib* dylib = LLVM.OrcLLJITGetMainJITDylib(jit);
        Check(LLVM.OrcLLJITAddLLVMIRModule(jit, dylib, tsm), "OrcLLJITAddLLVMIRModule");
        Stage("module added");

        ulong addr;
        fixed (byte* sp = symName)
        {
            Check(LLVM.OrcLLJITLookup(jit, &addr, (sbyte*)sp), "OrcLLJITLookup");
        }
        Stage($"looked up answer @ 0x{addr:X}");
        Assert.NotEqual(0UL, addr);

        var fn = (delegate* unmanaged[Cdecl]<int>)addr;
        int result = fn();
        Stage($"called answer() => {result}");
        _out.WriteLine($"answer() returned {result}");

        Assert.Equal(42, result);
    }

    /// <summary>
    /// SUB-SPIKE #1 (runtime symbol resolution): the last core uncertainty for JIT-and-run — can JIT'd
    /// code call an <c>rf_*</c> native-runtime function? Loads razorforge_runtime.dll into the process,
    /// adds an ORC process-search generator, JITs a module that CALLS <c>rf_current_thread_id()</c>, and
    /// asserts the JIT'd result equals a direct call to the same runtime function on the same thread.
    /// </summary>
    [Fact(Skip = "Local ORC-JIT feasibility spike: needs a system/bundled libLLVM staged next to the test binary; not run in CI.")]
    public void OrcJit_ResolvesRuntimeSymbol()
    {
        Stage("rt: start");

        // Load the native runtime so its rf_* exports are visible to a process-wide symbol search.
        string dir = Path.GetDirectoryName(typeof(OrcJitSpike).Assembly.Location) ?? ".";
        string rtPath = Path.Combine(dir, "razorforge_runtime.dll");
        IntPtr rt = NativeLibrary.Load(File.Exists(rtPath) ? rtPath : "razorforge_runtime");
        Stage($"rt: runtime loaded ({rtPath})");

        LLVM.InitializeNativeTarget();
        LLVM.InitializeNativeAsmPrinter();

        // Module: declare the runtime function, define a wrapper that calls it.
        byte[] ir = Encoding.ASCII.GetBytes(
            "declare i64 @rf_current_thread_id()\n" +
            "define i64 @callrt() {\nentry:\n  %t = call i64 @rf_current_thread_id()\n  ret i64 %t\n}\n");
        byte[] modName = Encoding.ASCII.GetBytes("rtspike\0");
        byte[] symName = Encoding.ASCII.GetBytes("callrt\0");

        LLVMOpaqueContext* ctx = LLVM.ContextCreate();
        LLVMOpaqueModule* mod;
        sbyte* parseErr;
        fixed (byte* irp = ir)
        fixed (byte* np = modName)
        {
            LLVMOpaqueMemoryBuffer* buf =
                LLVM.CreateMemoryBufferWithMemoryRangeCopy((sbyte*)irp, (UIntPtr)ir.Length, (sbyte*)np);
            int rc = LLVM.ParseIRInContext(ctx, buf, &mod, &parseErr);
            Assert.True(rc == 0, $"ParseIRInContext failed: {(parseErr != null ? new string(parseErr) : "?")}");
        }
        LLVMOrcOpaqueThreadSafeContext* tsCtx = _fromCtx(ctx);
        LLVMOrcOpaqueThreadSafeModule* tsm = LLVM.OrcCreateNewThreadSafeModule(mod, tsCtx);

        LLVMOrcOpaqueLLJITBuilder* builder = LLVM.OrcCreateLLJITBuilder();
        LLVMOrcOpaqueLLJIT* jit;
        Check(LLVM.OrcCreateLLJIT(&jit, builder), "OrcCreateLLJIT");
        LLVMOrcOpaqueJITDylib* dylib = LLVM.OrcLLJITGetMainJITDylib(jit);

        // Process-search generator: resolves any symbol loaded in the process (incl. rf_* now that the
        // runtime DLL is loaded). This is what lets JIT'd RF code link against the native runtime.
        sbyte prefix = LLVM.OrcLLJITGetGlobalPrefix(jit);
        LLVMOrcOpaqueDefinitionGenerator* gen;
        Check(LLVM.OrcCreateDynamicLibrarySearchGeneratorForProcess(&gen, prefix, null, null),
            "GeneratorForProcess");
        LLVM.OrcJITDylibAddGenerator(dylib, gen);
        Stage("rt: process-search generator added");

        Check(LLVM.OrcLLJITAddLLVMIRModule(jit, dylib, tsm), "OrcLLJITAddLLVMIRModule");

        ulong addr;
        fixed (byte* sp = symName)
            Check(LLVM.OrcLLJITLookup(jit, &addr, (sbyte*)sp), "OrcLLJITLookup(callrt)");
        Stage($"rt: looked up callrt @ 0x{addr:X}");

        ulong jitTid = ((delegate* unmanaged[Cdecl]<ulong>)addr)();
        // Direct call to the same runtime export, on the same thread.
        NativeLibrary.TryGetExport(rt, "rf_current_thread_id", out IntPtr directPtr);
        ulong directTid = ((delegate* unmanaged[Cdecl]<ulong>)directPtr)();
        Stage($"rt: jitTid={jitTid} directTid={directTid}");
        _out.WriteLine($"JIT'd rf_current_thread_id()={jitTid}  direct={directTid}");

        Assert.NotEqual(0UL, jitTid);
        Assert.Equal(directTid, jitTid);
    }
}
