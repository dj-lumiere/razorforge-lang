using System.Numerics;
using Compiler.Verification;

namespace RazorForge.Tests.Meta;

/// <summary>
/// Validates the managed <see cref="NumericLiteralParser.EncodeDecimal"/> (compile-time decimal
/// literal → 70-digit BID decimal256 bits) by round-trip: decoding the produced i256 back to
/// (sign, exp, coeff) — exactly as <c>SoftFloat/DecimalA.rf</c> <c>decode()</c> does — must
/// reproduce the literal (exact for ≤70 significant digits). Layout: bit255 sign, bits254..233
/// biased exponent (bias 1572932), bits232..0 coefficient.
/// </summary>
public sealed class DecimalEncoderTests
{
    private const int Bias = 1572932;

    private static (bool sign, int exp, BigInteger coeff) Decode(NumericLiteralParser.Decimal256 d)
    {
        BigInteger bits = ((BigInteger)d.W3 << 192) | ((BigInteger)d.W2 << 128)
                          | ((BigInteger)d.W1 << 64) | d.W0;
        bool sign = (bits & (BigInteger.One << 255)) != 0;
        int biased = (int)((bits >> 233) & 0x3FFFFF);
        BigInteger coeff = bits & ((BigInteger.One << 233) - 1);
        return (sign, biased - Bias, coeff);
    }

    [Theory]
    [InlineData("0", false, 0)]
    [InlineData("1", false, 0)]
    [InlineData("3.14159", false, -5)]
    [InlineData("-2.5", true, -1)]
    [InlineData("100", false, 0)]
    [InlineData("0.001", false, -3)]
    [InlineData("1234567890123456789012345678901234567890", false, 0)] // 40 digits
    [InlineData("9.999999999999999999999999999999999999999999999999999999999999999999999", false, -69)] // 70 nines
    [InlineData("1e1000", false, 1000)]
    [InlineData("1e-1000", false, -1000)]
    [InlineData("6.02214076e23", false, 15)]
    public void EncodeDecimal_RoundTrips(string s, bool wantSign, int wantExpHint)
    {
        NumericLiteralParser.Decimal256 enc = NumericLiteralParser.EncodeDecimal(s);
        (bool sign, int exp, BigInteger coeff) = Decode(enc);

        Assert.Equal(wantSign, sign);

        // Reconstruct value = (sign) coeff * 10^exp and compare to the literal num/den exactly
        // (these inputs all have <= 70 significant digits, so encoding is exact).
        BigInteger sCoeff = sign ? -coeff : coeff;
        ParseRational(s, out BigInteger num, out BigInteger den);
        BigInteger lhs, rhs;
        if (exp >= 0) { lhs = sCoeff * BigInteger.Pow(10, exp) * den; rhs = num; }
        else { lhs = sCoeff * den; rhs = num * BigInteger.Pow(10, -exp); }
        Assert.Equal(rhs, lhs);
    }

    [Fact]
    public void EncodeDecimal_OverflowThrows()
    {
        // q max is 1572795; 1e1572800 (coeff 1, exp 1572800) exceeds it -> overflow -> throw.
        Assert.Throws<OverflowException>(() => NumericLiteralParser.EncodeDecimal("1e1572900"));
    }

    private static void ParseRational(string s, out BigInteger num, out BigInteger den)
    {
        int e = s.IndexOf('e');
        int exp10 = e >= 0 ? int.Parse(s[(e + 1)..]) : 0;
        string mant = e >= 0 ? s[..e] : s;
        bool neg = mant.StartsWith('-');
        if (neg || mant.StartsWith('+')) mant = mant[1..];
        int dot = mant.IndexOf('.');
        if (dot >= 0) { exp10 -= mant.Length - dot - 1; mant = mant.Remove(dot, 1); }
        BigInteger coeff = BigInteger.Parse(mant);
        if (neg) coeff = -coeff;
        if (exp10 >= 0) { num = coeff * BigInteger.Pow(10, exp10); den = BigInteger.One; }
        else { num = coeff; den = BigInteger.Pow(10, -exp10); }
    }
}
