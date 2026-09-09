using Compiler.Verification;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Cross-checks the managed <see cref="NumericLiteralParser.EncodeF128"/> (pure C#, BigInteger,
/// round-to-nearest-even) against the native <see cref="NumericLiteralParser.ParseF128"/> (TLFloat,
/// correctly rounded) for a battery of decimal literals. They must agree bit-for-bit. Once this is
/// green the native F128 parser (and TLFloat) can be retired.
/// </summary>
public sealed class F128EncoderTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("0.5")]
    [InlineData("0.25")]
    [InlineData("0.1")]
    [InlineData("0.2")]
    [InlineData("3.14159")]
    [InlineData("3.141592653589793238462643383279502884")]
    [InlineData("2.718281828459045235360287471352662498")]
    [InlineData("10")]
    [InlineData("100")]
    [InlineData("1000000")]
    [InlineData("123456789.123456789")]
    [InlineData("1e10")]
    [InlineData("1e-10")]
    [InlineData("1e100")]
    [InlineData("1e-100")]
    [InlineData("1e1000")]
    [InlineData("1e-1000")]
    [InlineData("1e4000")]
    [InlineData("1e-4000")]
    [InlineData("1.18973149535723176508575932662800702e4932")] // near F128_MAX
    [InlineData("3.36210314311209350626267781732175260e-4932")] // smallest normal
    [InlineData("9.99999999999999999999999999999999999e4931")]
    [InlineData("0.333333333333333333333333333333333333")]
    [InlineData("7")]
    [InlineData("0.0001220703125")] // exact binary fraction
    [InlineData("12345678901234567890123456789012345678")]
    public void EncodeF128_MatchesNativeParser_OnFiniteNormals(string s)
    {
        NumericLiteralParser.F128 managed = NumericLiteralParser.EncodeF128(str: s);
        NumericLiteralParser.F128 native = NumericLiteralParser.ParseF128(str: s);
        Assert.Equal(expected: (native.Hi, native.Lo), actual: (managed.Hi, managed.Lo));
    }

    /// <summary>
    /// The managed encoder produces correct binary128 SUBNORMALS; the native TLFloat parser
    /// (incorrectly) flushes them to zero. Verified self-consistently by round-trip: decoding the
    /// managed bits back to a rational reproduces the literal within half a ULP. (Another reason
    /// TLFloat is being retired.)
    /// </summary>
    [Theory]
    [InlineData("1e-4940")]
    [InlineData("5e-4945")]
    [InlineData("1.5e-4950")]
    [InlineData("1e-4960")]
    [InlineData("6.475175119438025110924438958227646552e-4966")] // smallest subnormal
    public void EncodeF128_SubnormalsAreCorrect_NativeFlushesToZero(string s)
    {
        NumericLiteralParser.F128 managed = NumericLiteralParser.EncodeF128(str: s);
        NumericLiteralParser.F128 native = NumericLiteralParser.ParseF128(str: s);

        // Native TLFloat flushes the subnormal to zero — the bug we're moving off of.
        Assert.Equal(expected: (0UL, 0UL), actual: (native.Hi, native.Lo));

        System.Numerics.BigInteger bits =
            (System.Numerics.BigInteger)managed.Hi << 64 | managed.Lo;
        int biasedExp = (int)(bits >> 112 & 0x7FFF);
        System.Numerics.BigInteger mant = bits & (System.Numerics.BigInteger.One << 112) - 1;
        Assert.Equal(expected: 0, actual: biasedExp); // subnormal
        Assert.NotEqual(expected: System.Numerics.BigInteger.Zero, actual: mant); // not flushed

        // value = mant * 2^-16494; compare to the literal num/den within 0.5 ULP (one ULP of the
        // mantissa equals `den` after scaling both sides by `den`).
        ParseLiteralRational(s: s,
            num: out System.Numerics.BigInteger num,
            den: out System.Numerics.BigInteger den);
        var diff = System.Numerics.BigInteger.Abs(value: mant * den - (num << 16494));
        Assert.True(condition: diff * 2 <= den,
            userMessage: $"managed subnormal off by > 0.5 ULP for {s}");
    }

    private static void ParseLiteralRational(string s, out System.Numerics.BigInteger num,
        out System.Numerics.BigInteger den)
    {
        int e = s.IndexOf(value: 'e');
        int exp10 = e >= 0
            ? int.Parse(s: s[(e + 1)..])
            : 0;
        string mant = e >= 0
            ? s[..e]
            : s;
        int dot = mant.IndexOf(value: '.');
        if (dot >= 0)
        {
            exp10 -= mant.Length - dot - 1;
            mant = mant.Remove(startIndex: dot, count: 1);
        }

        var coeff = System.Numerics.BigInteger.Parse(value: mant);
        if (exp10 >= 0)
        {
            num = coeff * System.Numerics.BigInteger.Pow(value: 10, exponent: exp10);
            den = System.Numerics.BigInteger.One;
        }
        else
        {
            num = coeff;
            den = System.Numerics.BigInteger.Pow(value: 10, exponent: -exp10);
        }
    }
}
