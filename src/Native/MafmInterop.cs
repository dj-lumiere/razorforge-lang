using System.Runtime.InteropServices;

namespace Compilers.Shared.Native;

/// <summary>
/// P/Invoke wrapper for MAFM (Multiple Arithmetic with Fixed Mantissa)
/// Used for decimal operations in RazorForge
/// </summary>
public static class MafmInterop
{
    private const string LibraryName = "razorforge_math";

    [StructLayout(LayoutKind.Sequential)]
    public struct MafmContext
    {
        public int Precision;
        public int RoundingMode;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MafmNumber
    {
        public IntPtr Digits;
        public int Length;
        public int Scale;
        public int Sign;
        public uint Flags;
    }

    // Context management
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mafm_context_init(ref MafmContext ctx, int precision);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mafm_context_free(ref MafmContext ctx);

    // Number management
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mafm_init(ref MafmNumber num);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void mafm_clear(ref MafmNumber num);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_set_str(ref MafmNumber num, [MarshalAs(UnmanagedType.LPStr)] string str, int radix);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr mafm_get_str(ref MafmNumber num, int radix);

    // Arithmetic operations
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_add(ref MafmNumber result, ref MafmNumber a, ref MafmNumber b, ref MafmContext ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_sub(ref MafmNumber result, ref MafmNumber a, ref MafmNumber b, ref MafmContext ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_mul(ref MafmNumber result, ref MafmNumber a, ref MafmNumber b, ref MafmContext ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_div(ref MafmNumber result, ref MafmNumber a, ref MafmNumber b, ref MafmContext ctx);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_cmp(ref MafmNumber a, ref MafmNumber b);

    // Conversion functions
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_set_si(ref MafmNumber num, long val);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int mafm_set_d(ref MafmNumber num, double val);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern long mafm_get_si(ref MafmNumber num);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern double mafm_get_d(ref MafmNumber num);

    // Rounding modes
    public const int MAFM_RNDN = 0; // Round to nearest, ties to even
    public const int MAFM_RNDZ = 1; // Round toward zero
    public const int MAFM_RNDU = 2; // Round toward +infinity
    public const int MAFM_RNDD = 3; // Round toward -infinity
    public const int MAFM_RNDA = 4; // Round away from zero
}