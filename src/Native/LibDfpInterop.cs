using System.Runtime.InteropServices;

namespace Compilers.Shared.Native;

/// <summary>
/// P/Invoke wrapper for libdfp (IEEE 754 decimal floating point)
/// Used for d32, d64, d128 decimal types in RazorForge
/// </summary>
public static class LibDfpInterop
{
    private const string LibraryName = "razorforge_math";

    // Decimal32 operations
    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint d32_add(uint a, uint b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint d32_sub(uint a, uint b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint d32_mul(uint a, uint b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint d32_div(uint a, uint b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int d32_cmp(uint a, uint b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern uint d32_from_string(
        [MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint d32_to_string(uint val);

    // Decimal64 operations
    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong d64_add(ulong a, ulong b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong d64_sub(ulong a, ulong b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong d64_mul(ulong a, ulong b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong d64_div(ulong a, ulong b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int d64_cmp(ulong a, ulong b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong d64_from_string(
        [MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint d64_to_string(ulong val);

    // Decimal128 operations
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct Decimal128
    {
        public ulong Low;
        public ulong High;
    }

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Decimal128 d128_add(Decimal128 a, Decimal128 b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Decimal128 d128_sub(Decimal128 a, Decimal128 b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Decimal128 d128_mul(Decimal128 a, Decimal128 b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Decimal128 d128_div(Decimal128 a, Decimal128 b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int d128_cmp(Decimal128 a, Decimal128 b);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern Decimal128 d128_from_string(
        [MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    [DllImport(dllName: LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern nint d128_to_string(Decimal128 val);
}
