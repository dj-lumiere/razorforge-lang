using System.Globalization;
using System.Runtime.InteropServices;

namespace Compilers.Shared.Native;

/// <summary>
/// Arbitrary precision integer using libbf
/// Used for integer operations in RazorForge
/// </summary>
public class BigInteger : IDisposable, IComparable<BigInteger>, IEquatable<BigInteger>
{
    private LibBfInterop.BfContext _context;
    private LibBfInterop.BfNumber _number;
    private bool _disposed;

    public BigInteger()
    {
        LibBfInterop.bf_context_init(ref _context, IntPtr.Zero, IntPtr.Zero);
        LibBfInterop.bf_init(ref _context, ref _number);
    }

    public BigInteger(long value) : this()
    {
        LibBfInterop.bf_set_si(ref _number, value);
    }

    public BigInteger(ulong value) : this()
    {
        LibBfInterop.bf_set_ui(ref _number, value);
    }

    public BigInteger(string value) : this()
    {
        if (!TryParse(value, out var temp))
            throw new FormatException($"Invalid number format: {value}");
        
        _number = temp._number;
        temp._number = default; // Transfer ownership
    }

    public static BigInteger operator +(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_add(ref result._number, ref a._number, ref b._number, 0, 0);
        return result;
    }

    public static BigInteger operator -(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_sub(ref result._number, ref a._number, ref b._number, 0, 0);
        return result;
    }

    public static BigInteger operator *(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_mul(ref result._number, ref a._number, ref b._number, 0, 0);
        return result;
    }

    public static BigInteger operator /(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_div(ref result._number, ref a._number, ref b._number, 0, 0);
        return result;
    }

    public int CompareTo(BigInteger? other)
    {
        if (other is null) return 1;
        return LibBfInterop.bf_cmp(ref _number, ref other._number);
    }

    public bool Equals(BigInteger? other)
    {
        return CompareTo(other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return obj is BigInteger other && Equals(other);
    }

    public override int GetHashCode()
    {
        return ToString().GetHashCode();
    }

    public override string ToString()
    {
        var strPtr = LibBfInterop.bf_ftoa(IntPtr.Zero, ref _number, 10, 0, 0);
        if (strPtr == IntPtr.Zero) return "0";
        
        var result = Marshal.PtrToStringAnsi(strPtr) ?? "0";
        // Note: In a real implementation, you'd need to free the string returned by bf_ftoa
        return result;
    }

    public static bool TryParse(string value, out BigInteger result)
    {
        try
        {
            result = new BigInteger();
            // In a real implementation, you'd parse the string and set the bf_number
            // This is a simplified version
            if (long.TryParse(value, out var longVal))
            {
                LibBfInterop.bf_set_si(ref result._number, longVal);
                return true;
            }
            return false;
        }
        catch
        {
            result = new BigInteger();
            return false;
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            LibBfInterop.bf_delete(ref _number);
            LibBfInterop.bf_context_end(ref _context);
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~BigInteger()
    {
        Dispose();
    }
}