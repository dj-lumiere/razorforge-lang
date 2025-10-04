using System.Runtime.InteropServices;

namespace Compilers.Shared.Native;

/// <summary>
/// P/Invoke wrapper for libbf (arbitrary precision arithmetic)
/// Used for integer operations in RazorForge
/// </summary>
public static class LibBfInterop
{
    private const string LibraryName = "razorforge_math";

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct BfContext
    {
        public IntPtr Realloc;
        public IntPtr Free;
        public IntPtr Opaque;
        public ulong Flags;
        public ulong Precision;
        public int ExpMin;
        public int ExpMax;
    }

    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct BfNumber
    {
        public IntPtr Tab;
        public int Len;
        public int Size;
        public long Exp;
        public int Sign;
        public byte Flags;
    }

    // Basic operations
    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void
        bf_context_init(ref BfContext ctx, nint realloc_func, nint free_func);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void bf_context_end(ref BfContext ctx);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void bf_init(ref BfContext ctx, ref BfNumber r);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void bf_delete(ref BfNumber r);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_set_si(ref BfNumber r, long a);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_set_ui(ref BfNumber r, ulong a);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_add(ref BfNumber r, ref BfNumber a, ref BfNumber b,
        ulong prec, uint flags);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_sub(ref BfNumber r, ref BfNumber a, ref BfNumber b,
        ulong prec, uint flags);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_mul(ref BfNumber r, ref BfNumber a, ref BfNumber b,
        ulong prec, uint flags);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_div(ref BfNumber r, ref BfNumber a, ref BfNumber b,
        ulong prec, uint flags);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int bf_cmp(ref BfNumber a, ref BfNumber b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint bf_ftoa(nint plen, ref BfNumber a, int radix,
        ulong prec, uint flags);
}
