using System.Numerics;
using System.Runtime.InteropServices;

namespace Verification;

/// <summary>
/// P/Invoke bindings for native numeric literal parsing functions.
/// Used by the semantic analyzer to parse types without C# equivalents:
/// f128, d32, d64, d128, Integer, Decimal.
/// </summary>
public static class NumericLiteralParser
{
    private const string RuntimeLib = "razorforge_runtime";

    #region f128 (IEEE binary128)

    /// <summary>
    /// 128-bit IEEE binary floating point value.
    /// Stored as two 64-bit unsigned integers (little-endian).
    /// </summary>
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct F128
    {
        /// <summary>
        /// Gets or sets the low 64 bits of the binary128 payload.
        /// </summary>
        public ulong Lo;
        /// <summary>
        /// Gets or sets the high 64 bits of the binary128 payload.
        /// </summary>
        public ulong Hi;
        /// <inheritdoc/>

        public override string ToString()
        {
            return $"f128(0x{Hi:X16}{Lo:X16})";
        }
    }

    /// <summary>
    /// Parses a string to IEEE binary128 (f128) using LibBF.
    /// </summary>
    /// <param name="str">The string representation of the number.</param>
    /// <returns>The parsed f128 value.</returns>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_f128_from_string")]
    public static extern F128
        ParseF128([MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    #endregion

    #region Decimal floating-point (IEEE 754-2008)

    /// <summary>
    /// 32-bit IEEE decimal floating point value.
    /// </summary>
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct D32
    {
        /// <summary>
        /// Gets or sets the raw decimal32 bit pattern.
        /// </summary>
        public uint Value;
        /// <inheritdoc/>

        public override string ToString()
        {
            return $"d32(0x{Value:X8})";
        }
    }

    /// <summary>
    /// 64-bit IEEE decimal floating point value.
    /// </summary>
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct D64
    {
        /// <summary>
        /// Gets or sets the raw decimal64 bit pattern.
        /// </summary>
        public ulong Value;
        /// <inheritdoc/>

        public override string ToString()
        {
            return $"d64(0x{Value:X16})";
        }
    }

    /// <summary>
    /// 128-bit IEEE decimal floating point value.
    /// Stored as two 64-bit unsigned integers (little-endian).
    /// </summary>
    [StructLayout(layoutKind: LayoutKind.Sequential)]
    public struct D128
    {
        /// <summary>
        /// Gets or sets the low 64 bits of the decimal128 payload.
        /// </summary>
        public ulong Lo;
        /// <summary>
        /// Gets or sets the high 64 bits of the decimal128 payload.
        /// </summary>
        public ulong Hi;
        /// <inheritdoc/>

        public override string ToString()
        {
            return $"d128(0x{Hi:X16}{Lo:X16})";
        }
    }

    /// <summary>
    /// Parses a string to IEEE decimal32 (d32) using Intel DFP library.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_d32_from_string")]
    public static extern D32 ParseD32([MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Parses a string to IEEE decimal64 (d64) using Intel DFP library.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_d64_from_string")]
    public static extern D64 ParseD64([MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Parses a string to IEEE decimal128 (d128) using Intel DFP library.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_d128_from_string")]
    public static extern D128
        ParseD128([MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    #endregion

    #region Managed BID encoders (software decimal, replacing the decNumber/DPD FFI)

    // D128B uses a SINGLE-form BID layout: bit127 = sign, bits126..113 = biased exponent
    // (q + 6176, 14 bits), bits112..0 = the coefficient as a PLAIN binary integer. This works
    // because Pmax=34 and 10^34 < 2^113, so the whole coefficient always fits the trailing field
    // with no combination "Form 2" — matching Core.D128B.decode(). Specials use the combination
    // prefix (top5 = 0x1E Inf, 0x1F NaN), which the finite biased range (<= 12287) never reaches.
    // Overflow rounds to ±Inf, underflow flushes toward ±0; rounding is round-half-to-even (RNE).
    //
    // NOTE: D64B (Pmax16, 10^16 > 2^53) and D32B (Pmax7, 10^7 > 2^23) need the full TWO-form IEEE
    // BID (large coefficients spill into the combination field). Their encoders are deferred until
    // their engine decode() is read so the field split can be matched bit-exactly — see Phase C.

    /// <summary>Decoded decimal-literal fields: value = (-1)^sign * coeff * 10^exp10.</summary>
    private readonly record struct DecimalLiteralParts(bool Sign, BigInteger Coeff, int Exp10);

    /// <summary>
    /// Splits a decimal literal string into sign, integer coefficient, and base-10 exponent.
    /// Accepts an optional sign, an integer/fraction mantissa, and an optional <c>e</c>/<c>E</c>
    /// exponent. Digit-group separators (<c>_</c>) are ignored. The tokenizer has already stripped
    /// the type suffix (e.g. <c>_d128</c>) before this point.
    /// </summary>
    private static DecimalLiteralParts ParseDecimalLiteral(string str)
    {
        string cleaned = StripTypeSuffix(cleaned: str.Trim());

        ReadOnlySpan<char> s = cleaned.AsSpan();
        bool sign = false;
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            sign = s[0] == '-';
            s = s[1..];
        }

        // Split off the exponent.
        int exp10 = 0;
        int eIdx = s.IndexOfAny('e', 'E');
        if (eIdx >= 0)
        {
            exp10 = int.Parse(s[(eIdx + 1)..].ToString().Replace("_", ""));
            s = s[..eIdx];
        }

        // Mantissa: collect digits, tracking how many follow the decimal point.
        var digits = new System.Text.StringBuilder(s.Length);
        int fracDigits = 0;
        bool seenDot = false;
        foreach (char c in s)
        {
            if (c == '_') continue;
            if (c == '.') { seenDot = true; continue; }
            digits.Append(c);
            if (seenDot) fracDigits++;
        }

        exp10 -= fracDigits;
        string digitStr = digits.Length == 0 ? "0" : digits.ToString();
        BigInteger coeff = BigInteger.Parse(digitStr);
        return new DecimalLiteralParts(Sign: sign, Coeff: coeff, Exp10: exp10);
    }

    /// <summary>
    /// Strips a trailing decimal/float type suffix — WITH or WITHOUT the optional leading
    /// underscore — so all spellings (`3.14f128`, `3.14_f128`) work. The semantic-analyzer path
    /// passes the literal with its type suffix (e.g. "6.0_d128" or the underscore-less "6.0d128");
    /// codegen passes the cleaned digits. Longest-first avoids a short suffix matching prematurely.
    /// (Digit-group separators "_" between digits are handled by the caller.)
    /// </summary>
    private static string StripTypeSuffix(string cleaned)
    {
        foreach (string suf in new[] { "decimal", "f128", "d128", "d64", "d32", "dec", "dn" })
        {
            if (cleaned.EndsWith("_" + suf, StringComparison.OrdinalIgnoreCase))
            {
                return cleaned[..^(suf.Length + 1)];
            }
            if (cleaned.EndsWith(suf, StringComparison.OrdinalIgnoreCase))
            {
                return cleaned[..^suf.Length];
            }
        }

        return cleaned;
    }

    /// <summary>Number of decimal digits in a non-negative BigInteger (0 has 1 digit).</summary>
    private static int DecimalDigitCount(BigInteger v)
    {
        if (v.IsZero) return 1;
        int n = 0;
        while (v > 0) { v /= 10; n++; }
        return n;
    }

    /// <summary>
    /// Divides <paramref name="coeff"/> by 10^<paramref name="drop"/>, rounding the discarded
    /// low digits round-half-to-even. <paramref name="drop"/> must be &gt;= 0.
    /// </summary>
    private static BigInteger RneDivPow10(BigInteger coeff, int drop)
    {
        if (drop <= 0) return coeff;
        BigInteger pow = BigInteger.Pow(10, drop);
        BigInteger q = BigInteger.DivRem(coeff, pow, out BigInteger r);
        BigInteger twice = r * 2;
        if (twice > pow || (twice == pow && !q.IsEven))
            q += 1;
        return q;
    }

    /// <summary>
    /// Rounds (coeff, exp10) to at most <paramref name="pmax"/> significant digits (RNE), then
    /// clamps the exponent into [<paramref name="qMin"/>, <paramref name="qMax"/>], scaling the
    /// coefficient to compensate. Returns the packed (biasedExp, coeff) with coeff &lt; 10^pmax, or
    /// signals overflow (coeff = -1 sentinel → caller emits Inf) / a flushed zero.
    /// </summary>
    private static (bool Overflow, BigInteger Coeff, int BiasedExp) RoundAndClamp(
        BigInteger coeff, int exp10, int pmax, int bias, int qMin, int qMax)
    {
        // Round the coefficient down to at most pmax digits.
        (coeff, exp10) = RoundToPmaxDigits(coeff: coeff, exp10: exp10, pmax: pmax);

        if (coeff.IsZero)
            return (false, BigInteger.Zero, ClampZeroExp(exp10, bias, qMin, qMax));

        // Exponent too large: try to absorb it by appending zeros to the coefficient.
        if (exp10 > qMax)
        {
            (bool overflow, coeff, exp10) =
                AbsorbLargeExponent(coeff: coeff, exp10: exp10, pmax: pmax, qMax: qMax);
            if (overflow)
                return (true, BigInteger.Zero, 0); // overflow -> Inf
        }

        // Exponent too small: drop low digits (RNE), losing precision toward zero/subnormal.
        if (exp10 < qMin)
        {
            int drop = qMin - exp10;
            coeff = drop >= pmax + 2 ? BigInteger.Zero : RneDivPow10(coeff, drop);
            exp10 = qMin;
        }

        return (false, coeff, exp10 + bias);
    }

    /// <summary>
    /// Rounds <paramref name="coeff"/> down to at most <paramref name="pmax"/> significant digits
    /// (RNE), adjusting <paramref name="exp10"/> to compensate and renormalizing a rounding carry.
    /// </summary>
    private static (BigInteger Coeff, int Exp10) RoundToPmaxDigits(BigInteger coeff, int exp10, int pmax)
    {
        int nd = DecimalDigitCount(coeff);
        if (nd > pmax)
        {
            int drop = nd - pmax;
            coeff = RneDivPow10(coeff, drop);
            exp10 += drop;
            // Rounding up can carry to pmax+1 digits (e.g. 999..9 -> 1000..0); renormalize.
            if (DecimalDigitCount(coeff) > pmax)
            {
                coeff /= 10;
                exp10 += 1;
            }
        }

        return (coeff, exp10);
    }

    /// <summary>
    /// Absorbs an exponent above <paramref name="qMax"/> by appending zeros to the coefficient when
    /// it still fits <paramref name="pmax"/> digits; otherwise signals overflow (→ Inf).
    /// </summary>
    private static (bool Overflow, BigInteger Coeff, int Exp10) AbsorbLargeExponent(
        BigInteger coeff, int exp10, int pmax, int qMax)
    {
        int shift = exp10 - qMax;
        if (DecimalDigitCount(coeff) + shift <= pmax)
        {
            coeff *= BigInteger.Pow(10, shift);
            exp10 = qMax;
            return (false, coeff, exp10);
        }

        return (true, BigInteger.Zero, 0); // overflow -> Inf
    }

    private static int ClampZeroExp(int exp10, int bias, int qMin, int qMax)
    {
        int q = exp10 < qMin ? qMin : exp10 > qMax ? qMax : exp10;
        return q + bias;
    }

    /// <summary>
    /// Encodes a decimal literal into a software decimal128 (Core.D128B) BID bit pattern.
    /// Replaces the <c>rf_d128_from_string</c> (decNumber/DPD) FFI path.
    /// </summary>
    public static D128 EncodeD128Bid(string str)
    {
        DecimalLiteralParts p = ParseDecimalLiteral(str);
        (bool overflow, BigInteger coeff, int biased) =
            RoundAndClamp(p.Coeff, p.Exp10, pmax: 34, bias: 6176, qMin: -6176, qMax: 6111);

        // A numeric literal that overflows the type to infinity is a compile-time error (the
        // explicit `inf`/`nan` literals are handled before the encoder). The caller's catch turns
        // this into a semantic diagnostic.
        if (overflow)
            throw new OverflowException($"decimal literal '{str}' is out of range for D128 (overflows to infinity)");

        UInt128 bits = ((UInt128)(uint)biased << 113) | (UInt128)coeff;
        if (p.Sign)
            bits |= (UInt128)1 << 127;

        return new D128 { Lo = (ulong)(bits & ulong.MaxValue), Hi = (ulong)(bits >> 64) };
    }

    /// <summary>
    /// Encodes a decimal literal into a software decimal64 (Core.D64) two-form BID bit pattern.
    /// Matches Core.D64B.decode()/d64b_of_parts: Form 1 (coeff &lt; 2^53) packs the coefficient in
    /// bits 52:0 with the biased exponent in bits 62:53; Form 2 (larger coeff) sets bits 62:61=11,
    /// the exponent in bits 60:51, and the low 51 coefficient bits (the implicit 2^53 is restored on
    /// decode). bias 398, Pmax 16. inf/NaN use the combination prefix at bits 62:58.
    /// </summary>
    public static D64 EncodeD64Bid(string str)
    {
        DecimalLiteralParts p = ParseDecimalLiteral(str);
        (bool overflow, BigInteger coeff, int biased) =
            RoundAndClamp(p.Coeff, p.Exp10, pmax: 16, bias: 398, qMin: -398, qMax: 369);

        if (overflow)
            throw new OverflowException($"decimal literal '{str}' is out of range for D64 (overflows to infinity)");

        ulong c = (ulong)coeff;
        ulong e = (ulong)(uint)biased;
        ulong bits = c < (1UL << 53)
            ? (e << 53) | c                                  // Form 1
            : (0x3UL << 61) | (e << 51) | (c & ((1UL << 51) - 1)); // Form 2

        if (p.Sign)
            bits |= 1UL << 63;

        return new D64 { Value = bits };
    }

    /// <summary>
    /// Encodes a decimal literal into a software decimal32 (Core.D32) two-form BID bit pattern.
    /// Matches Core.D32B.decode()/d32b_of_parts: Form 1 (coeff &lt; 2^23) packs the coefficient in
    /// bits 22:0 with the biased exponent in bits 30:23; Form 2 sets bits 30:29=11, the exponent in
    /// bits 28:21, and the low 21 coefficient bits (implicit 2^23 restored on decode). bias 101,
    /// Pmax 7. inf/NaN use the combination prefix at bits 30:26.
    /// </summary>
    public static D32 EncodeD32Bid(string str)
    {
        DecimalLiteralParts p = ParseDecimalLiteral(str);
        (bool overflow, BigInteger coeff, int biased) =
            RoundAndClamp(p.Coeff, p.Exp10, pmax: 7, bias: 101, qMin: -101, qMax: 90);

        if (overflow)
            throw new OverflowException($"decimal literal '{str}' is out of range for D32 (overflows to infinity)");

        uint c = (uint)coeff;
        uint e = (uint)biased;
        uint bits = c < (1u << 23)
            ? (e << 23) | c                                  // Form 1
            : (0x3u << 29) | (e << 21) | (c & ((1u << 21) - 1)); // Form 2

        if (p.Sign)
            bits |= 1u << 31;

        return new D32 { Value = bits };
    }

    /// <summary>
    /// Encodes a decimal literal into IEEE binary128 (Core.F128, @llvm("i128")) bits, correctly
    /// rounded to nearest-even via exact BigInteger arithmetic. Replaces the
    /// <c>rf_f128_from_string</c> FFI. Throws <see cref="OverflowException"/> when the value is out
    /// of binary128's finite range (a compile-time literal overflow); the explicit <c>inf</c>/
    /// <c>nan</c> literals are handled by the caller before this is reached.
    /// </summary>
    public static F128 EncodeF128(string str)
    {
        DecimalLiteralParts p = ParseDecimalLiteral(str);
        if (p.Coeff.IsZero)
            return PackF128(sign: p.Sign, biasedExp: 0, mant: 0);

        // value = coeff * 10^exp10, written as the positive ratio num/den.
        BigInteger num, den;
        if (p.Exp10 >= 0) { num = p.Coeff * BigInteger.Pow(10, p.Exp10); den = BigInteger.One; }
        else { num = p.Coeff; den = BigInteger.Pow(10, -p.Exp10); }

        const int mantBits = 112;     // stored mantissa width
        const int bias = 16383;
        const int maxBiased = 0x7FFE; // largest finite biased exponent (0x7FFF = inf/nan)

        // Unbiased exponent E = floor(log2(value)); estimate from bit lengths then correct so that
        // 2^E <= value < 2^(E+1). CmpPow2 compares value (num/den) to 2^k exactly (left-shifts only).
        int e = (int)num.GetBitLength() - (int)den.GetBitLength();
        while (CmpPow2(num, den, e) < 0) e--;        // value >= 2^e
        while (CmpPow2(num, den, e + 1) >= 0) e++;   // value < 2^(e+1)

        // significand q = round(value * 2^(mantBits - e)) in [2^112, 2^113) (round half-to-even).
        BigInteger q = RoundedScale(num, den, mantBits - e);
        if (q >= (BigInteger.One << (mantBits + 1))) { q >>= 1; e++; } // carry 1.99..->2.0

        int biased = e + bias;
        if (biased > maxBiased)
            throw new OverflowException($"float literal '{str}' is out of range for F128 (overflows to infinity)");

        if (biased <= 0)
            return EncodeF128Subnormal(sign: p.Sign, num: num, den: den, mantBits: mantBits, bias: bias);

        UInt128 mant = (UInt128)(q - (BigInteger.One << mantBits));
        return PackF128(sign: p.Sign, biasedExp: biased, mant: mant);
    }

    /// <summary>
    /// Rounds the significand at the minimum exponent (biased 0) to produce a subnormal, the
    /// smallest normal, or a signed zero for a binary128 value that underflows the normal range.
    /// </summary>
    private static F128 EncodeF128Subnormal(bool sign, BigInteger num, BigInteger den, int mantBits, int bias)
    {
        int eMin = 1 - bias; // -16382
        BigInteger qs = RoundedScale(num, den, mantBits - eMin);
        if (qs.IsZero)
            return PackF128(sign: sign, biasedExp: 0, mant: 0);                 // -> +/-0
        if (qs >= (BigInteger.One << mantBits))
            return PackF128(sign: sign, biasedExp: 1, mant: (UInt128)(qs - (BigInteger.One << mantBits))); // smallest normal
        return PackF128(sign: sign, biasedExp: 0, mant: (UInt128)qs);           // subnormal
    }

    /// <summary>Exact sign of <c>(num/den) - 2^k</c>, i.e. compares the value to a power of two
    /// using only left-shifts so no bits are lost. Returns &lt;0, 0, or &gt;0.</summary>
    private static int CmpPow2(BigInteger num, BigInteger den, int k)
    {
        BigInteger lhs = num, rhs = den;
        if (k >= 0) rhs <<= k; else lhs <<= -k;
        return lhs.CompareTo(rhs);
    }

    /// <summary>Round-half-to-even of <c>num/den * 2^shift</c> to an integer (exact remainder).</summary>
    private static BigInteger RoundedScale(BigInteger num, BigInteger den, int shift)
    {
        BigInteger sn = num, sd = den;
        if (shift >= 0) sn <<= shift; else sd <<= -shift;
        BigInteger q = BigInteger.DivRem(sn, sd, out BigInteger r);
        BigInteger twice = r << 1;
        if (twice > sd || (twice == sd && !q.IsEven)) q += 1;
        return q;
    }

    private static F128 PackF128(bool sign, int biasedExp, UInt128 mant)
    {
        UInt128 bits = ((UInt128)(uint)biasedExp << 112) | (mant & (((UInt128)1 << 112) - 1));
        if (sign) bits |= (UInt128)1 << 127;
        return new F128 { Lo = (ulong)(bits & ulong.MaxValue), Hi = (ulong)(bits >> 64) };
    }

    /// <summary>A raw 256-bit value as four little-endian 64-bit words (W0 = bits 0..63).</summary>
    public struct Decimal256
    {
        /// <summary>Bits 0..63.</summary>
        public ulong W0;
        /// <summary>Bits 64..127.</summary>
        public ulong W1;
        /// <summary>Bits 128..191.</summary>
        public ulong W2;
        /// <summary>Bits 192..255.</summary>
        public ulong W3;
    }

    /// <summary>
    /// Encodes a decimal literal into the software 70-digit <c>Core.Decimal</c> (decimal256) BID bit
    /// pattern, matching <c>SoftFloat/DecimalA.rf</c> <c>decode()</c>/<c>decimalfixed_of_parts</c>:
    /// bit255 = sign, bits254..233 = biased exponent (22 bits, q + 1572932), bits232..0 = coefficient
    /// as a plain binary integer (&lt; 10^70 &lt; 2^233). Single-form (no combination field for finite
    /// values). Pmax 70, stored exponent q in [-1572932, 1572795]. Throws on overflow (compile-time
    /// literal range error); explicit inf/nan are handled before this is reached.
    /// </summary>
    /// <summary>
    /// Canonicalizes a <c>Core.Decimal</c> (decimal256) coefficient/exponent pair — stripping
    /// fractional trailing zeros so equal values share bits (2.50 and 2.5 → 25*10^-1), while
    /// leaving integers (exp &gt;= 0) as-is (100 stays 100*10^0). Mirrors the runtime
    /// <c>decimal_normalize_parts</c> so a literal and the arithmetic result of the same value are
    /// bit-identical. (Kept out of <c>RoundAndClamp</c>, which the IEEE encoders share and must NOT
    /// normalize.)
    /// </summary>
    private static (BigInteger Coeff, int Biased) CanonicalizeDecimal(BigInteger coeff, int biased)
    {
        const int decBias = 1572932;
        if (coeff.IsZero)
        {
            return (coeff, decBias); // canonical zero: exponent 0
        }

        int exp = biased - decBias;
        while (exp < 0 && (coeff % 10).IsZero) { coeff /= 10; exp++; }
        return (coeff, exp + decBias);
    }

    public static Decimal256 EncodeDecimal(string str)
    {
        DecimalLiteralParts p = ParseDecimalLiteral(str);
        (bool overflow, BigInteger coeff, int biased) =
            RoundAndClamp(p.Coeff, p.Exp10, pmax: 70, bias: 1572932, qMin: -1572932, qMax: 1572795);

        if (overflow)
            throw new OverflowException($"decimal literal '{str}' is out of range for Decimal (overflows to infinity)");

        // Canonicalize (strip fractional trailing zeros so equal values share bits).
        (coeff, biased) = CanonicalizeDecimal(coeff: coeff, biased: biased);

        BigInteger bits = ((BigInteger)biased << 233) | coeff;
        if (p.Sign)
            bits |= BigInteger.One << 255;

        return new Decimal256
        {
            W0 = (ulong)(bits & ulong.MaxValue),
            W1 = (ulong)((bits >> 64) & ulong.MaxValue),
            W2 = (ulong)((bits >> 128) & ulong.MaxValue),
            W3 = (ulong)((bits >> 192) & ulong.MaxValue),
        };
    }

    #endregion

    #region Arbitrary precision Integer (LibBF)

    /// <summary>
    /// Parses a string to an arbitrary precision integer using LibBF.
    /// Returns an opaque handle that must be freed with FreeInteger.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_cs_integer_from_string")]
    public static extern nint ParseInteger(
        [MarshalAs(unmanagedType: UnmanagedType.LPStr)] string str);

    /// <summary>
    /// Frees an arbitrary precision integer handle.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_cs_integer_free")]
    public static extern void FreeInteger(nint handle);

    /// <summary>
    /// Gets the byte size needed to store the integer as raw limbs.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_cs_integer_byte_size")]
    public static extern nuint GetIntegerByteSize(nint handle);

    /// <summary>
    /// Copies integer limbs to a buffer.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_cs_integer_to_bytes")]
    public static extern nuint IntegerToBytes(nint handle, byte[] buffer, nuint bufferSize);

    /// <summary>
    /// Gets the sign of the integer (0 = positive, 1 = negative).
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_cs_integer_sign")]
    public static extern int GetIntegerSign(nint handle);

    /// <summary>
    /// Gets the exponent of the integer.
    /// </summary>
    [DllImport(dllName: RuntimeLib,
        CallingConvention = CallingConvention.Cdecl,
        EntryPoint = "rf_cs_integer_exponent")]
    public static extern long GetIntegerExponent(nint handle);

    #endregion

    #region Helper memberRoutines for managed types

    /// <summary>
    /// Parses an arbitrary precision integer and returns it as a managed byte array.
    /// </summary>
    /// <param name="str">The string representation.</param>
    /// <returns>Tuple of (bytes, sign) where sign is 0 for positive, 1 for negative.</returns>
    public static (byte[] bytes, int sign) ParseIntegerToBytes(string str)
    {
        nint handle = ParseInteger(str: str);
        if (handle == nint.Zero)
        {
            return ([], 0);
        }

        try
        {
            nuint size = GetIntegerByteSize(handle: handle);
            int sign = GetIntegerSign(handle: handle);

            // Handle zero: libbf represents zero with len=0, but we need at least 1 byte
            if (size == 0)
            {
                return ([0], sign);
            }

            byte[] bytes = new byte[(int)size];
            IntegerToBytes(handle: handle, buffer: bytes, bufferSize: size);
            return (bytes, sign);
        }
        finally
        {
            FreeInteger(handle: handle);
        }
    }

    /// <summary>
    /// Parses an arbitrary precision decimal and returns metadata, using the managed
    /// <see cref="ParseDecimalLiteral"/> splitter (no native FFI — the old libbf/decNumber
    /// <c>rf_cs_decimal_from_string</c> backend has been retired). The returned tuple feeds the
    /// vestigial <c>ParsedDecimal</c> SA result; the compile-time bits come from
    /// <see cref="EncodeDecimal"/>, which is the single source of truth.
    /// </summary>
    /// <param name="str">The string representation (type suffix already optional).</param>
    /// <returns>Tuple of (stringValue, sign, exponent, significantDigits, isInteger).</returns>
    public static (string value, int sign, int exponent, int significantDigits, bool isInteger)
        ParseDecimalInfo(string str)
    {
        DecimalLiteralParts p = ParseDecimalLiteral(str);
        int sign = p.Sign ? 1 : 0;
        int sigDigits = DecimalDigitCount(p.Coeff);
        bool isInt = p.Exp10 >= 0;

        // Reconstruct a normalized "coeff * 10^exp" decimal string for the value field.
        string mag = p.Coeff.ToString();
        string value;
        if (p.Exp10 >= 0)
        {
            value = mag + new string('0', p.Exp10);
        }
        else
        {
            int frac = -p.Exp10;
            if (mag.Length <= frac)
            {
                value = "0." + new string('0', frac - mag.Length) + mag;
            }
            else
            {
                value = mag[..^frac] + "." + mag[^frac..];
            }
        }
        if (p.Sign && p.Coeff != 0) value = "-" + value;

        return (value, sign, p.Exp10, sigDigits, isInt);
    }

    #endregion
}
