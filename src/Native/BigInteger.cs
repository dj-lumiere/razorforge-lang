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
        LibBfInterop.bf_context_init(ctx: ref _context, realloc_func: nint.Zero,
            free_func: nint.Zero);
        LibBfInterop.bf_init(ctx: ref _context, r: ref _number);
    }

    public BigInteger(long value) : this()
    {
        LibBfInterop.bf_set_si(r: ref _number, a: value);
    }

    public BigInteger(ulong value) : this()
    {
        LibBfInterop.bf_set_ui(r: ref _number, a: value);
    }

    public BigInteger(string value) : this()
    {
        if (!TryParse(value: value, result: out BigInteger temp))
        {
            throw new FormatException(message: $"Invalid number format: {value}");
        }

        _number = temp._number;
        temp._number = default; // Transfer ownership
    }

    public static BigInteger operator +(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_add(r: ref result._number, a: ref a._number, b: ref b._number, prec: 0,
            flags: 0);
        return result;
    }

    public static BigInteger operator -(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_sub(r: ref result._number, a: ref a._number, b: ref b._number, prec: 0,
            flags: 0);
        return result;
    }

    public static BigInteger operator *(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_mul(r: ref result._number, a: ref a._number, b: ref b._number, prec: 0,
            flags: 0);
        return result;
    }

    public static BigInteger operator /(BigInteger a, BigInteger b)
    {
        var result = new BigInteger();
        LibBfInterop.bf_div(r: ref result._number, a: ref a._number, b: ref b._number, prec: 0,
            flags: 0);
        return result;
    }

    public int CompareTo(BigInteger? other)
    {
        if (other is null)
        {
            return 1;
        }

        return LibBfInterop.bf_cmp(a: ref _number, b: ref other._number);
    }

    public bool Equals(BigInteger? other)
    {
        return CompareTo(other: other) == 0;
    }

    public override bool Equals(object? obj)
    {
        return obj is BigInteger other && Equals(other: other);
    }

    public override int GetHashCode()
    {
        return ToString()
           .GetHashCode();
    }

    public override string ToString()
    {
        nint strPtr = LibBfInterop.bf_ftoa(plen: nint.Zero, a: ref _number, radix: 10, prec: 0,
            flags: 0);
        if (strPtr == nint.Zero)
        {
            return "0";
        }

        string result = Marshal.PtrToStringAnsi(ptr: strPtr) ?? "0";
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
            if (long.TryParse(s: value, result: out long longVal))
            {
                LibBfInterop.bf_set_si(r: ref result._number, a: longVal);
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
            LibBfInterop.bf_delete(r: ref _number);
            LibBfInterop.bf_context_end(ctx: ref _context);
            _disposed = true;
        }

        GC.SuppressFinalize(obj: this);
    }

    ~BigInteger()
    {
        Dispose();
    }
}
