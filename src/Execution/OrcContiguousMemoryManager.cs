using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LLVMSharp.Interop;

namespace Builder;

/// <summary>
/// A JIT object-linking-layer memory manager that allocates every section of one linked object from a
/// SINGLE contiguous virtual-memory slab (bump allocation, in RuntimeDyld's allocation order). This fixes
/// the intermittent Windows-x64 JIT crash <c>IMAGE_REL_AMD64_ADDR32NB relocation requires an ordered
/// section layout</c>: that image-relative relocation (emitted by SEH unwind data) is only resolvable by
/// ORC's RuntimeDyld COFF linker when the referenced section sits at a predictable, ordered address — which
/// the default SectionMemoryManager does not guarantee (it mmaps sections independently, sometimes >2GB
/// apart / out of order). JITLink (LLVM's newer linker that handles COFF SEH natively) is not reachable
/// through the LLVM-C API, so a contiguous slab is the robust in-C-API fix.
///
/// Wired into an LLJIT via <see cref="LLVM.OrcLLJITBuilderSetObjectLinkingLayerCreator"/> + a layer built
/// with <see cref="LLVM.OrcCreateRTDyldObjectLinkingLayerWithMCJITMemoryManagerLikeCallbacks"/>. The
/// per-object context (slab + section ranges) is a managed object kept alive through a <see cref="GCHandle"/>
/// passed as the callbacks' <c>Opaque</c>.
/// </summary>
internal static unsafe class OrcContiguousMemoryManager
{
    // 64 MiB reserved+committed per linked object. Windows demand-pages committed memory, so the resident
    // footprint only grows with the code/data actually written — the reservation is effectively free.
    private const nuint SlabSize = 64 * 1024 * 1024;

    // ── Win32 virtual memory ────────────────────────────────────────────────────────────────────
    private const uint MEM_COMMIT = 0x1000, MEM_RESERVE = 0x2000, MEM_RELEASE = 0x8000;
    private const uint PAGE_READWRITE = 0x04, PAGE_EXECUTE_READWRITE = 0x40;

    [DllImport("kernel32", SetLastError = true)]
    private static extern void* VirtualAlloc(void* addr, nuint size, uint type, uint protect);
    [DllImport("kernel32", SetLastError = true)]
    private static extern int VirtualFree(void* addr, nuint size, uint type);
    [DllImport("kernel32", SetLastError = true)]
    private static extern int VirtualProtect(void* addr, nuint size, uint newProtect, uint* oldProtect);
    [DllImport("kernel32")]
    private static extern int FlushInstructionCache(IntPtr process, void* addr, nuint size);
    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Per-linked-object slab state: one contiguous block, bump-allocated. The whole used range is
    /// flipped to RWX at finalize — a dev-loop JIT trades W^X for simplicity, and some sections genuinely
    /// need both (RuntimeDyld places emulated-TLS control blocks, which are mutated at runtime, next to
    /// executable code), so per-section RX/RW protection faults; RWX is the correct pragmatic choice here.</summary>
    private sealed class Slab
    {
        public byte* Base;
        public nuint Offset;
    }

    private static byte* Bump(Slab slab, nuint size, uint alignment)
    {
        nuint align = alignment == 0 ? 1 : alignment;
        nuint aligned = (slab.Offset + (align - 1)) & ~(align - 1);
        if (aligned + size > SlabSize)
        {
            return null; // slab exhausted — the module is larger than SlabSize (raise it if this ever trips)
        }
        byte* p = slab.Base + aligned;
        slab.Offset = aligned + size;
        return p;
    }

    // ── memory-manager callbacks (unmanaged entry points) ───────────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void* CreateContext(void* _)
    {
        try
        {
            void* baseAddr = VirtualAlloc(null, SlabSize, MEM_RESERVE | MEM_COMMIT, PAGE_READWRITE);
            if (baseAddr == null)
            {
                return null;
            }
            var slab = new Slab { Base = (byte*)baseAddr, Offset = 0 };
            return (void*)(IntPtr)GCHandle.Alloc(value: slab);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NotifyTerminating(void* _) { }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* AllocateCodeSection(void* opaque, nuint size, uint align, uint sectionId, sbyte* name)
    {
        try
        {
            var slab = (Slab)GCHandle.FromIntPtr(value: (IntPtr)opaque).Target!;
            return Bump(slab: slab, size: size, alignment: align);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte* AllocateDataSection(void* opaque, nuint size, uint align, uint sectionId, sbyte* name,
        int isReadOnly)
    {
        try
        {
            var slab = (Slab)GCHandle.FromIntPtr(value: (IntPtr)opaque).Target!;
            return Bump(slab: slab, size: size, alignment: align);
        }
        catch
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FinalizeMemory(void* opaque, sbyte** errMsg)
    {
        try
        {
            var slab = (Slab)GCHandle.FromIntPtr(value: (IntPtr)opaque).Target!;
            uint old;
            // DIAGNOSTIC: whole slab RWX to rule out any page-protection fault (data mutated at runtime,
            // e.g. emulated-TLS control blocks). Will tighten to code=RX / data=RW once execution is clean.
            VirtualProtect(addr: slab.Base, size: slab.Offset, newProtect: PAGE_EXECUTE_READWRITE, oldProtect: &old);
            FlushInstructionCache(process: GetCurrentProcess(), addr: slab.Base, size: slab.Offset);
            return 0; // LLVMBool: 0 = success
        }
        catch
        {
            return 1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destroy(void* opaque)
    {
        try
        {
            GCHandle h = GCHandle.FromIntPtr(value: (IntPtr)opaque);
            if (h.Target is Slab slab && slab.Base != null)
            {
                VirtualFree(addr: slab.Base, size: 0, type: MEM_RELEASE);
            }
            h.Free();
        }
        catch
        {
            // best-effort teardown
        }
    }

    // ── object-linking-layer creator (set on the LLJIT builder) ─────────────────────────────────

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static LLVMOrcOpaqueObjectLayer* CreateObjectLinkingLayer(void* ctx,
        LLVMOrcOpaqueExecutionSession* es, sbyte* triple)
    {
        return LLVM.OrcCreateRTDyldObjectLinkingLayerWithMCJITMemoryManagerLikeCallbacks(
            ES: es,
            CreateContextCtx: null,
            CreateContext: &CreateContext,
            NotifyTerminating: &NotifyTerminating,
            AllocateCodeSection: &AllocateCodeSection,
            AllocateDataSection: &AllocateDataSection,
            FinalizeMemory: &FinalizeMemory,
            Destroy: &Destroy);
    }

    /// <summary>Installs the contiguous-slab object-linking-layer creator on an LLJIT builder, so the LLJIT
    /// created from it links JIT'd objects into single contiguous slabs (fixing the ADDR32NB flake).</summary>
    public static void InstallOn(LLVMOrcOpaqueLLJITBuilder* builder)
    {
        LLVM.OrcLLJITBuilderSetObjectLinkingLayerCreator(Builder: builder,
            F: &CreateObjectLinkingLayer, Ctx: null);
    }
}
