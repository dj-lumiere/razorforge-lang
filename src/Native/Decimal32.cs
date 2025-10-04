using System.Globalization;
using System.Runtime.InteropServices;

namespace Compilers.Shared.Native;

/// <summary>
/// IEEE 754 Decimal32 floating point type using libdfp
/// Used for d32 type in RazorForge
/// </summary>
public readonly struct Decimal32 : IComparable<Decimal32>, IEquatable<Decimal32>
{
    private readonly uint _value;

    public Decimal32(uint value)
    {
        _value = value;
    }

    public Decimal32(string value)
    {
        _value = LibDfpInterop.d32_from_string(str: value);
    }

    public static implicit operator Decimal32(int value)
    {
        return new Decimal32(value: LibDfpInterop.d32_from_string(str: value.ToString()));
    }

    public static implicit operator Decimal32(float value)
    {
        return new Decimal32(
            value: LibDfpInterop.d32_from_string(
                str: value.ToString(provider: CultureInfo.InvariantCulture)));
    }

    public static Decimal32 operator +(Decimal32 a, Decimal32 b)
    {
        return new Decimal32(value: LibDfpInterop.d32_add(a: a._value, b: b._value));
    }

    public static Decimal32 operator -(Decimal32 a, Decimal32 b)
    {
        return new Decimal32(value: LibDfpInterop.d32_sub(a: a._value, b: b._value));
    }

    public static Decimal32 operator *(Decimal32 a, Decimal32 b)
    {
        return new Decimal32(value: LibDfpInterop.d32_mul(a: a._value, b: b._value));
    }

    public static Decimal32 operator /(Decimal32 a, Decimal32 b)
    {
        return new Decimal32(value: LibDfpInterop.d32_div(a: a._value, b: b._value));
    }

    public static bool operator ==(Decimal32 a, Decimal32 b)
    {
        return LibDfpInterop.d32_cmp(a: a._value, b: b._value) == 0;
    }

    public static bool operator !=(Decimal32 a, Decimal32 b)
    {
        return !(a == b);
    }

    public static bool operator <(Decimal32 a, Decimal32 b)
    {
        return LibDfpInterop.d32_cmp(a: a._value, b: b._value) < 0;
    }

    public static bool operator <=(Decimal32 a, Decimal32 b)
    {
        return LibDfpInterop.d32_cmp(a: a._value, b: b._value) <= 0;
    }

    public static bool operator >(Decimal32 a, Decimal32 b)
    {
        return LibDfpInterop.d32_cmp(a: a._value, b: b._value) > 0;
    }

    public static bool operator >=(Decimal32 a, Decimal32 b)
    {
        return LibDfpInterop.d32_cmp(a: a._value, b: b._value) >= 0;
    }

    public int CompareTo(Decimal32 other)
    {
        return LibDfpInterop.d32_cmp(a: _value, b: other._value);
    }

    public bool Equals(Decimal32 other)
    {
        return this == other;
    }

    public override bool Equals(object? obj)
    {
        return obj is Decimal32 other && Equals(other: other);
    }

    public override int GetHashCode()
    {
        return _value.GetHashCode();
    }

    public override string ToString()
    {
        nint strPtr = LibDfpInterop.d32_to_string(val: _value);
        if (strPtr == nint.Zero)
        {
            return "0";
        }

        string result = Marshal.PtrToStringAnsi(ptr: strPtr) ?? "0";
        // Note: In a real implementation, you'd need to free the string
        return result;
    }

    public static bool TryParse(string value, out Decimal32 result)
    {
        try
        {
            result = new Decimal32(value: value);
            return true;
        }
        catch
        {
            result = default;
            return false;
        }
    }

    public static Decimal32 Parse(string value)
    {
        return new Decimal32(value: value);
    }
}
