/*
 * RazorForge Runtime - Half-Precision (f16) Floating Point Functions
 * IEEE 754 binary16: 1 sign, 5 exponent, 10 mantissa bits
 *
 * f16 has limited range (~6.1e-5 to 65504) and ~3.3 decimal digits precision.
 * Basic arithmetic uses LLVM's native f16 support.
 * Transcendental functions promote to f32, compute, then demote back to f16.
 */

#include <stdint.h>
#include <math.h>
#include "../include/razorforge_math.h"

// IEEE 754 binary16 bit layout constants
#define F16_SIGN_MASK     0x8000
#define F16_EXP_MASK      0x7C00
#define F16_MANT_MASK     0x03FF
#define F16_EXP_BIAS      15
#define F16_EXP_MAX       31
#define F16_QNAN          0x7E00  // Quiet NaN
#define F16_INF           0x7C00  // Positive infinity
#define F16_NEG_INF       0xFC00  // Negative infinity
#define F16_ZERO          0x0000
#define F16_NEG_ZERO      0x8000
#define F16_EPSILON       0x1400  // ~0.000977 (2^-10)
#define F16_MIN_POSITIVE  0x0400  // ~6.1e-5 (smallest positive normal)
#define F16_MAX_VALUE     0x7BFF  // 65504 (largest finite)

// ============================================================================
// Conversion helpers (software implementation)
// These can be replaced with LLVM intrinsics in the code generator
// ============================================================================

// Convert f32 to f16 (with rounding)
uint16_t rf_f16_from_f32(float x)
{
    union { float f; uint32_t u; } conv;
    conv.f = x;
    uint32_t f32 = conv.u;

    uint32_t sign = (f32 >> 16) & F16_SIGN_MASK;
    int32_t exp = ((f32 >> 23) & 0xFF) - 127 + F16_EXP_BIAS;
    uint32_t mant = (f32 >> 13) & F16_MANT_MASK;

    // Handle special cases
    if ((f32 & 0x7FFFFFFF) == 0) {
        // Zero (preserve sign)
        return (uint16_t)sign;
    }

    if (exp <= 0) {
        // Underflow to zero or denormal
        if (exp < -10) {
            return (uint16_t)sign; // Too small, flush to zero
        }
        // Denormal
        mant = (mant | 0x0400) >> (1 - exp);
        return (uint16_t)(sign | mant);
    }

    if (exp >= F16_EXP_MAX) {
        // Overflow to infinity or NaN
        if ((f32 & 0x7FFFFFFF) > 0x7F800000) {
            // NaN - preserve some payload bits
            return (uint16_t)(sign | F16_QNAN | (mant >> 3));
        }
        return (uint16_t)(sign | F16_INF);
    }

    // Round to nearest even
    uint32_t round_bit = (f32 >> 12) & 1;
    uint32_t sticky = (f32 & 0x0FFF) != 0;
    if (round_bit && (sticky || (mant & 1))) {
        mant++;
        if (mant > F16_MANT_MASK) {
            mant = 0;
            exp++;
            if (exp >= F16_EXP_MAX) {
                return (uint16_t)(sign | F16_INF);
            }
        }
    }

    return (uint16_t)(sign | ((uint32_t)exp << 10) | mant);
}

// Convert f64 to f16
uint16_t rf_f16_from_f64(double x)
{
    return rf_f16_from_f32((float)x);
}

// Convert f16 to f32
float rf_f16_to_f32(uint16_t x)
{
    uint32_t sign = ((uint32_t)x & F16_SIGN_MASK) << 16;
    uint32_t exp = (x & F16_EXP_MASK) >> 10;
    uint32_t mant = x & F16_MANT_MASK;

    union { uint32_t u; float f; } conv;

    if (exp == 0) {
        if (mant == 0) {
            // Zero
            conv.u = sign;
            return conv.f;
        }
        // Denormal - normalize
        while ((mant & 0x0400) == 0) {
            mant <<= 1;
            exp--;
        }
        exp++;
        mant &= F16_MANT_MASK;
    } else if (exp == F16_EXP_MAX) {
        // Inf or NaN
        conv.u = sign | 0x7F800000 | (mant << 13);
        return conv.f;
    }

    // Normal number
    exp = exp - F16_EXP_BIAS + 127;
    conv.u = sign | (exp << 23) | (mant << 13);
    return conv.f;
}

// Convert f16 to f64
double rf_f16_to_f64(uint16_t x)
{
    return (double)rf_f16_to_f32(x);
}

// ============================================================================
// Arithmetic operations
// ============================================================================

uint16_t rf_f16_add(uint16_t a, uint16_t b)
{
    return rf_f16_from_f32(rf_f16_to_f32(a) + rf_f16_to_f32(b));
}

uint16_t rf_f16_sub(uint16_t a, uint16_t b)
{
    return rf_f16_from_f32(rf_f16_to_f32(a) - rf_f16_to_f32(b));
}

uint16_t rf_f16_mul(uint16_t a, uint16_t b)
{
    return rf_f16_from_f32(rf_f16_to_f32(a) * rf_f16_to_f32(b));
}

uint16_t rf_f16_div(uint16_t a, uint16_t b)
{
    return rf_f16_from_f32(rf_f16_to_f32(a) / rf_f16_to_f32(b));
}

uint16_t rf_f16_neg(uint16_t x)
{
    return x ^ F16_SIGN_MASK;
}

// ============================================================================
// Comparison operations
// ============================================================================

int32_t rf_f16_eq(uint16_t a, uint16_t b)
{
    // NaN != NaN
    if (rf_f16_isnan(a) || rf_f16_isnan(b)) return 0;
    // +0 == -0
    if ((a & ~F16_SIGN_MASK) == 0 && (b & ~F16_SIGN_MASK) == 0) return 1;
    return a == b;
}

int32_t rf_f16_ne(uint16_t a, uint16_t b)
{
    return !rf_f16_eq(a, b);
}

int32_t rf_f16_lt(uint16_t a, uint16_t b)
{
    if (rf_f16_isnan(a) || rf_f16_isnan(b)) return 0;
    return rf_f16_to_f32(a) < rf_f16_to_f32(b);
}

int32_t rf_f16_le(uint16_t a, uint16_t b)
{
    if (rf_f16_isnan(a) || rf_f16_isnan(b)) return 0;
    return rf_f16_to_f32(a) <= rf_f16_to_f32(b);
}

int32_t rf_f16_gt(uint16_t a, uint16_t b)
{
    if (rf_f16_isnan(a) || rf_f16_isnan(b)) return 0;
    return rf_f16_to_f32(a) > rf_f16_to_f32(b);
}

int32_t rf_f16_ge(uint16_t a, uint16_t b)
{
    if (rf_f16_isnan(a) || rf_f16_isnan(b)) return 0;
    return rf_f16_to_f32(a) >= rf_f16_to_f32(b);
}

// ============================================================================
// Basic math operations
// ============================================================================

uint16_t rf_f16_abs(uint16_t x)
{
    return x & ~F16_SIGN_MASK;
}

uint16_t rf_f16_copysign(uint16_t x, uint16_t y)
{
    return (x & ~F16_SIGN_MASK) | (y & F16_SIGN_MASK);
}

uint16_t rf_f16_min(uint16_t x, uint16_t y)
{
    if (rf_f16_isnan(x)) return y;
    if (rf_f16_isnan(y)) return x;
    return rf_f16_lt(x, y) ? x : y;
}

uint16_t rf_f16_max(uint16_t x, uint16_t y)
{
    if (rf_f16_isnan(x)) return y;
    if (rf_f16_isnan(y)) return x;
    return rf_f16_gt(x, y) ? x : y;
}

// ============================================================================
// Rounding operations
// ============================================================================

uint16_t rf_f16_ceil(uint16_t x)
{
    return rf_f16_from_f32(ceilf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_floor(uint16_t x)
{
    return rf_f16_from_f32(floorf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_trunc(uint16_t x)
{
    return rf_f16_from_f32(truncf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_round(uint16_t x)
{
    return rf_f16_from_f32(roundf(rf_f16_to_f32(x)));
}

// ============================================================================
// Square root and FMA
// ============================================================================

uint16_t rf_f16_sqrt(uint16_t x)
{
    return rf_f16_from_f32(sqrtf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_fma(uint16_t x, uint16_t y, uint16_t z)
{
    return rf_f16_from_f32(fmaf(rf_f16_to_f32(x), rf_f16_to_f32(y), rf_f16_to_f32(z)));
}

// ============================================================================
// Classification predicates
// ============================================================================

int32_t rf_f16_isnan(uint16_t x)
{
    return ((x & F16_EXP_MASK) == F16_EXP_MASK) && ((x & F16_MANT_MASK) != 0);
}

int32_t rf_f16_isinf(uint16_t x)
{
    return ((x & ~F16_SIGN_MASK) == F16_INF);
}

int32_t rf_f16_isfinite(uint16_t x)
{
    return (x & F16_EXP_MASK) != F16_EXP_MASK;
}

int32_t rf_f16_isnormal(uint16_t x)
{
    uint16_t exp = (x & F16_EXP_MASK) >> 10;
    return exp > 0 && exp < F16_EXP_MAX;
}

int32_t rf_f16_iszero(uint16_t x)
{
    return (x & ~F16_SIGN_MASK) == 0;
}

int32_t rf_f16_signbit(uint16_t x)
{
    return (x & F16_SIGN_MASK) != 0;
}

// ============================================================================
// Special values
// ============================================================================

uint16_t rf_f16_nan(void)
{
    return F16_QNAN;
}

uint16_t rf_f16_inf(void)
{
    return F16_INF;
}

uint16_t rf_f16_neg_inf(void)
{
    return F16_NEG_INF;
}

uint16_t rf_f16_epsilon(void)
{
    return F16_EPSILON;
}

uint16_t rf_f16_min_positive(void)
{
    return F16_MIN_POSITIVE;
}

uint16_t rf_f16_max_value(void)
{
    return F16_MAX_VALUE;
}

// ============================================================================
// Transcendental functions (via f32 promotion)
// ============================================================================

uint16_t rf_f16_sin(uint16_t x)
{
    return rf_f16_from_f32(sinf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_cos(uint16_t x)
{
    return rf_f16_from_f32(cosf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_tan(uint16_t x)
{
    return rf_f16_from_f32(tanf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_asin(uint16_t x)
{
    return rf_f16_from_f32(asinf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_acos(uint16_t x)
{
    return rf_f16_from_f32(acosf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_atan(uint16_t x)
{
    return rf_f16_from_f32(atanf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_atan2(uint16_t y, uint16_t x)
{
    return rf_f16_from_f32(atan2f(rf_f16_to_f32(y), rf_f16_to_f32(x)));
}

uint16_t rf_f16_sinh(uint16_t x)
{
    return rf_f16_from_f32(sinhf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_cosh(uint16_t x)
{
    return rf_f16_from_f32(coshf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_tanh(uint16_t x)
{
    return rf_f16_from_f32(tanhf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_asinh(uint16_t x)
{
    return rf_f16_from_f32(asinhf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_acosh(uint16_t x)
{
    return rf_f16_from_f32(acoshf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_atanh(uint16_t x)
{
    return rf_f16_from_f32(atanhf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_exp(uint16_t x)
{
    return rf_f16_from_f32(expf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_exp2(uint16_t x)
{
    return rf_f16_from_f32(exp2f(rf_f16_to_f32(x)));
}

uint16_t rf_f16_expm1(uint16_t x)
{
    return rf_f16_from_f32(expm1f(rf_f16_to_f32(x)));
}

uint16_t rf_f16_log(uint16_t x)
{
    return rf_f16_from_f32(logf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_log2(uint16_t x)
{
    return rf_f16_from_f32(log2f(rf_f16_to_f32(x)));
}

uint16_t rf_f16_log10(uint16_t x)
{
    return rf_f16_from_f32(log10f(rf_f16_to_f32(x)));
}

uint16_t rf_f16_log1p(uint16_t x)
{
    return rf_f16_from_f32(log1pf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_pow(uint16_t base, uint16_t exp)
{
    return rf_f16_from_f32(powf(rf_f16_to_f32(base), rf_f16_to_f32(exp)));
}

uint16_t rf_f16_cbrt(uint16_t x)
{
    return rf_f16_from_f32(cbrtf(rf_f16_to_f32(x)));
}

uint16_t rf_f16_hypot(uint16_t x, uint16_t y)
{
    return rf_f16_from_f32(hypotf(rf_f16_to_f32(x), rf_f16_to_f32(y)));
}

uint16_t rf_f16_fmod(uint16_t x, uint16_t y)
{
    return rf_f16_from_f32(fmodf(rf_f16_to_f32(x), rf_f16_to_f32(y)));
}

uint16_t rf_f16_remainder(uint16_t x, uint16_t y)
{
    return rf_f16_from_f32(remainderf(rf_f16_to_f32(x), rf_f16_to_f32(y)));
}