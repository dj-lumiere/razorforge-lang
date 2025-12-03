/*
 * RazorForge Runtime - Decimal Floating Point Functions
 * IEEE 754-2008 decimal floating point operations using Intel Decimal Library
 *
 * This implementation uses Intel's BID (Binary Integer Decimal) library for
 * proper IEEE 754-2008 compliant decimal floating point arithmetic.
 */

#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include "../include/razorforge_math.h"

// Intel Decimal Library configuration
// Using value semantics (CALL_BY_REF=0) and global rounding (GLOBAL_RND=1)
#include "../inteldecimal/LIBRARY/src/bid_conf.h"
#include "../inteldecimal/LIBRARY/src/bid_functions.h"

// ============================================================================
// d32 operations (decimal32 - 7 significant digits)
// ============================================================================

uint32_t d32_add(uint32_t a, uint32_t b)
{
    return bid32_add(a, b);
}

uint32_t d32_sub(uint32_t a, uint32_t b)
{
    return bid32_sub(a, b);
}

uint32_t d32_mul(uint32_t a, uint32_t b)
{
    return bid32_mul(a, b);
}

uint32_t d32_div(uint32_t a, uint32_t b)
{
    return bid32_div(a, b);
}

int32_t d32_cmp(uint32_t a, uint32_t b)
{
    if (bid32_quiet_less(a, b)) return -1;
    if (bid32_quiet_greater(a, b)) return 1;
    return 0;
}

uint32_t d32_neg(uint32_t a)
{
    return bid32_negate(a);
}

uint32_t d32_from_string(const char* str)
{
    return bid32_from_string((char*)str);
}

char* d32_to_string(uint32_t val)
{
    char* buf = (char*)malloc(64);
    bid32_to_string(buf, val);
    return buf;
}

uint32_t d32_from_s32(int32_t val)
{
    return bid32_from_int32(val);
}

uint32_t d32_from_s64(int64_t val)
{
    return bid32_from_int64(val);
}

uint32_t d32_from_u32(uint32_t val)
{
    return bid32_from_uint32(val);
}

uint32_t d32_from_u64(uint64_t val)
{
    return bid32_from_uint64(val);
}

int32_t d32_to_i32(uint32_t val)
{
    return bid32_to_int32_int(val);
}

int64_t d32_to_i64(uint32_t val)
{
    return bid32_to_int64_int(val);
}

// ============================================================================
// d64 operations (decimal64 - 16 significant digits)
// ============================================================================

uint64_t d64_add(uint64_t a, uint64_t b)
{
    return bid64_add(a, b);
}

uint64_t d64_sub(uint64_t a, uint64_t b)
{
    return bid64_sub(a, b);
}

uint64_t d64_mul(uint64_t a, uint64_t b)
{
    return bid64_mul(a, b);
}

uint64_t d64_div(uint64_t a, uint64_t b)
{
    return bid64_div(a, b);
}

int32_t d64_cmp(uint64_t a, uint64_t b)
{
    if (bid64_quiet_less(a, b)) return -1;
    if (bid64_quiet_greater(a, b)) return 1;
    return 0;
}

uint64_t d64_neg(uint64_t a)
{
    return bid64_negate(a);
}

uint64_t d64_from_string(const char* str)
{
    return bid64_from_string((char*)str);
}

char* d64_to_string(uint64_t val)
{
    char* buf = (char*)malloc(64);
    bid64_to_string(buf, val);
    return buf;
}

uint64_t d64_from_s32(int32_t val)
{
    return bid64_from_int32(val);
}

uint64_t d64_from_s64(int64_t val)
{
    return bid64_from_int64(val);
}

uint64_t d64_from_u32(uint32_t val)
{
    return bid64_from_uint32(val);
}

uint64_t d64_from_u64(uint64_t val)
{
    return bid64_from_uint64(val);
}

int32_t d64_to_s32(uint64_t val)
{
    return bid64_to_int32_int(val);
}

int64_t d64_to_s64(uint64_t val)
{
    return bid64_to_int64_int(val);
}

// ============================================================================
// d128 operations (decimal128 - 34 significant digits)
// ============================================================================

// Helper to convert between d128_t and BID_UINT128
static inline BID_UINT128 to_bid128(d128_t x)
{
    BID_UINT128 r;
    r.w[0] = x.low;
    r.w[1] = x.high;
    return r;
}

static inline d128_t from_bid128(BID_UINT128 x)
{
    d128_t r;
    r.low = x.w[0];
    r.high = x.w[1];
    return r;
}

d128_t d128_add(d128_t a, d128_t b)
{
    return from_bid128(bid128_add(to_bid128(a), to_bid128(b)));
}

d128_t d128_sub(d128_t a, d128_t b)
{
    return from_bid128(bid128_sub(to_bid128(a), to_bid128(b)));
}

d128_t d128_mul(d128_t a, d128_t b)
{
    return from_bid128(bid128_mul(to_bid128(a), to_bid128(b)));
}

d128_t d128_div(d128_t a, d128_t b)
{
    return from_bid128(bid128_div(to_bid128(a), to_bid128(b)));
}

int32_t d128_cmp(d128_t a, d128_t b)
{
    BID_UINT128 ba = to_bid128(a);
    BID_UINT128 bb = to_bid128(b);
    if (bid128_quiet_less(ba, bb)) return -1;
    if (bid128_quiet_greater(ba, bb)) return 1;
    return 0;
}

d128_t d128_neg(d128_t a)
{
    return from_bid128(bid128_negate(to_bid128(a)));
}

d128_t d128_from_string(const char* str)
{
    return from_bid128(bid128_from_string((char*)str));
}

char* d128_to_string(d128_t val)
{
    char* buf = (char*)malloc(128);
    bid128_to_string(buf, to_bid128(val));
    return buf;
}

d128_t d128_from_s32(int32_t val)
{
    return from_bid128(bid128_from_int32(val));
}

d128_t d128_from_s64(int64_t val)
{
    return from_bid128(bid128_from_int64(val));
}

d128_t d128_from_u32(uint32_t val)
{
    return from_bid128(bid128_from_uint32(val));
}

d128_t d128_from_u64(uint64_t val)
{
    return from_bid128(bid128_from_uint64(val));
}

int32_t d128_to_s32(d128_t val)
{
    return bid128_to_int32_int(to_bid128(val));
}

int64_t d128_to_s64(d128_t val)
{
    return bid128_to_int64_int(to_bid128(val));
}

// ============================================================================
// Type conversions between decimal types
// ============================================================================

uint64_t d32_to_d64(uint32_t x)
{
    return bid32_to_bid64(x);
}

d128_t d32_to_d128(uint32_t x)
{
    return from_bid128(bid32_to_bid128(x));
}

uint32_t d64_to_d32(uint64_t x)
{
    return bid64_to_bid32(x);
}

d128_t d64_to_d128(uint64_t x)
{
    return from_bid128(bid64_to_bid128(x));
}

uint32_t d128_to_d32(d128_t x)
{
    return bid128_to_bid32(to_bid128(x));
}

uint64_t d128_to_d64(d128_t x)
{
    return bid128_to_bid64(to_bid128(x));
}

// ============================================================================
// Binary float to decimal conversions
// ============================================================================

uint32_t rf_f32_to_d32(float x)
{
    return binary32_to_bid32(x);
}

uint64_t rf_f32_to_d64(float x)
{
    return binary32_to_bid64(x);
}

d128_t rf_f32_to_d128(float x)
{
    return from_bid128(binary32_to_bid128(x));
}

uint32_t rf_f64_to_d32(double x)
{
    return binary64_to_bid32(x);
}

uint64_t rf_f64_to_d64(double x)
{
    return binary64_to_bid64(x);
}

d128_t rf_f64_to_d128(double x)
{
    return from_bid128(binary64_to_bid128(x));
}

// ============================================================================
// Decimal to binary float conversions
// ============================================================================

float rf_d32_to_f32(uint32_t x)
{
    return bid32_to_binary32(x);
}

double rf_d32_to_f64(uint32_t x)
{
    return bid32_to_binary64(x);
}

uint64_t rf_d32_to_d64(uint32_t x)
{
    return bid32_to_bid64(x);
}

d128_t rf_d32_to_d128(uint32_t x)
{
    return from_bid128(bid32_to_bid128(x));
}

float rf_d64_to_f32(uint64_t x)
{
    return bid64_to_binary32(x);
}

double rf_d64_to_f64(uint64_t x)
{
    return bid64_to_binary64(x);
}

uint32_t rf_d64_to_d32(uint64_t x)
{
    return bid64_to_bid32(x);
}

d128_t rf_d64_to_d128(uint64_t x)
{
    return from_bid128(bid64_to_bid128(x));
}

float rf_d128_to_f32(d128_t x)
{
    return bid128_to_binary32(to_bid128(x));
}

double rf_d128_to_f64(d128_t x)
{
    return bid128_to_binary64(to_bid128(x));
}

uint32_t rf_d128_to_d32(d128_t x)
{
    return bid128_to_bid32(to_bid128(x));
}

uint64_t rf_d128_to_d64(d128_t x)
{
    return bid128_to_bid64(to_bid128(x));
}

// ============================================================================
// d32 math functions (basic operations that don't require float128 emulation)
// ============================================================================

uint32_t rf_d32_sqrt(uint32_t x)
{
    return bid32_sqrt(x);
}

uint32_t rf_d32_abs(uint32_t x)
{
    return bid32_abs(x);
}

uint32_t rf_d32_ceil(uint32_t x)
{
    return bid32_round_integral_positive(x);
}

uint32_t rf_d32_floor(uint32_t x)
{
    return bid32_round_integral_negative(x);
}

uint32_t rf_d32_round(uint32_t x)
{
    return bid32_round_integral_nearest_away(x);
}

uint32_t rf_d32_trunc(uint32_t x)
{
    return bid32_round_integral_zero(x);
}

uint32_t rf_d32_fmod(uint32_t x, uint32_t y)
{
    return bid32_fmod(x, y);
}

uint32_t rf_d32_fma(uint32_t x, uint32_t y, uint32_t z)
{
    return bid32_fma(x, y, z);
}

uint32_t rf_d32_min(uint32_t x, uint32_t y)
{
    return bid32_minnum(x, y);
}

uint32_t rf_d32_max(uint32_t x, uint32_t y)
{
    return bid32_maxnum(x, y);
}

int32_t rf_d32_isnan(uint32_t x)
{
    return bid32_isNaN(x);
}

int32_t rf_d32_isinf(uint32_t x)
{
    return bid32_isInf(x);
}

int32_t rf_d32_isfinite(uint32_t x)
{
    return bid32_isFinite(x);
}

int32_t rf_d32_isnormal(uint32_t x)
{
    return bid32_isNormal(x);
}

int32_t rf_d32_iszero(uint32_t x)
{
    return bid32_isZero(x);
}

int32_t rf_d32_signbit(uint32_t x)
{
    return bid32_isSigned(x);
}

// ============================================================================
// d64 math functions (basic operations that don't require float128 emulation)
// ============================================================================

uint64_t rf_d64_sqrt(uint64_t x)
{
    return bid64_sqrt(x);
}

uint64_t rf_d64_abs(uint64_t x)
{
    return bid64_abs(x);
}

uint64_t rf_d64_ceil(uint64_t x)
{
    return bid64_round_integral_positive(x);
}

uint64_t rf_d64_floor(uint64_t x)
{
    return bid64_round_integral_negative(x);
}

uint64_t rf_d64_round(uint64_t x)
{
    return bid64_round_integral_nearest_away(x);
}

uint64_t rf_d64_trunc(uint64_t x)
{
    return bid64_round_integral_zero(x);
}

uint64_t rf_d64_fmod(uint64_t x, uint64_t y)
{
    return bid64_fmod(x, y);
}

uint64_t rf_d64_fma(uint64_t x, uint64_t y, uint64_t z)
{
    return bid64_fma(x, y, z);
}

uint64_t rf_d64_min(uint64_t x, uint64_t y)
{
    return bid64_minnum(x, y);
}

uint64_t rf_d64_max(uint64_t x, uint64_t y)
{
    return bid64_maxnum(x, y);
}

int32_t rf_d64_isnan(uint64_t x)
{
    return bid64_isNaN(x);
}

int32_t rf_d64_isinf(uint64_t x)
{
    return bid64_isInf(x);
}

int32_t rf_d64_isfinite(uint64_t x)
{
    return bid64_isFinite(x);
}

int32_t rf_d64_isnormal(uint64_t x)
{
    return bid64_isNormal(x);
}

int32_t rf_d64_iszero(uint64_t x)
{
    return bid64_isZero(x);
}

int32_t rf_d64_signbit(uint64_t x)
{
    return bid64_isSigned(x);
}

// ============================================================================
// d128 math functions (basic operations that don't require float128 emulation)
// ============================================================================

d128_t rf_d128_sqrt(d128_t x)
{
    return from_bid128(bid128_sqrt(to_bid128(x)));
}

d128_t rf_d128_abs(d128_t x)
{
    return from_bid128(bid128_abs(to_bid128(x)));
}

d128_t rf_d128_ceil(d128_t x)
{
    return from_bid128(bid128_round_integral_positive(to_bid128(x)));
}

d128_t rf_d128_floor(d128_t x)
{
    return from_bid128(bid128_round_integral_negative(to_bid128(x)));
}

d128_t rf_d128_round(d128_t x)
{
    return from_bid128(bid128_round_integral_nearest_away(to_bid128(x)));
}

d128_t rf_d128_trunc(d128_t x)
{
    return from_bid128(bid128_round_integral_zero(to_bid128(x)));
}

d128_t rf_d128_fmod(d128_t x, d128_t y)
{
    return from_bid128(bid128_fmod(to_bid128(x), to_bid128(y)));
}

d128_t rf_d128_fma(d128_t x, d128_t y, d128_t z)
{
    return from_bid128(bid128_fma(to_bid128(x), to_bid128(y), to_bid128(z)));
}

d128_t rf_d128_min(d128_t x, d128_t y)
{
    return from_bid128(bid128_minnum(to_bid128(x), to_bid128(y)));
}

d128_t rf_d128_max(d128_t x, d128_t y)
{
    return from_bid128(bid128_maxnum(to_bid128(x), to_bid128(y)));
}

int32_t rf_d128_isnan(d128_t x)
{
    return bid128_isNaN(to_bid128(x));
}

int32_t rf_d128_isinf(d128_t x)
{
    return bid128_isInf(to_bid128(x));
}

int32_t rf_d128_isfinite(d128_t x)
{
    return bid128_isFinite(to_bid128(x));
}

int32_t rf_d128_isnormal(d128_t x)
{
    return bid128_isNormal(to_bid128(x));
}

int32_t rf_d128_iszero(d128_t x)
{
    return bid128_isZero(to_bid128(x));
}

int32_t rf_d128_signbit(d128_t x)
{
    return bid128_isSigned(to_bid128(x));
}

// ============================================================================
// Special values
// ============================================================================

uint32_t rf_d32_nan(void)
{
    return bid32_nan("");
}

uint32_t rf_d32_inf(void)
{
    return bid32_inf();
}

uint32_t rf_d32_neg_inf(void)
{
    return bid32_negate(bid32_inf());
}

uint64_t rf_d64_nan(void)
{
    return bid64_nan("");
}

uint64_t rf_d64_inf(void)
{
    return bid64_inf();
}

uint64_t rf_d64_neg_inf(void)
{
    return bid64_negate(bid64_inf());
}

d128_t rf_d128_nan(void)
{
    return from_bid128(bid128_nan(""));
}

d128_t rf_d128_inf(void)
{
    return from_bid128(bid128_inf());
}

d128_t rf_d128_neg_inf(void)
{
    return from_bid128(bid128_negate(bid128_inf()));
}

// ============================================================================
// Comparison predicates
// ============================================================================

int32_t rf_d32_eq(uint32_t a, uint32_t b)
{
    return bid32_quiet_equal(a, b);
}

int32_t rf_d32_ne(uint32_t a, uint32_t b)
{
    return bid32_quiet_not_equal(a, b);
}

int32_t rf_d32_lt(uint32_t a, uint32_t b)
{
    return bid32_quiet_less(a, b);
}

int32_t rf_d32_le(uint32_t a, uint32_t b)
{
    return bid32_quiet_less_equal(a, b);
}

int32_t rf_d32_gt(uint32_t a, uint32_t b)
{
    return bid32_quiet_greater(a, b);
}

int32_t rf_d32_ge(uint32_t a, uint32_t b)
{
    return bid32_quiet_greater_equal(a, b);
}

int32_t rf_d64_eq(uint64_t a, uint64_t b)
{
    return bid64_quiet_equal(a, b);
}

int32_t rf_d64_ne(uint64_t a, uint64_t b)
{
    return bid64_quiet_not_equal(a, b);
}

int32_t rf_d64_lt(uint64_t a, uint64_t b)
{
    return bid64_quiet_less(a, b);
}

int32_t rf_d64_le(uint64_t a, uint64_t b)
{
    return bid64_quiet_less_equal(a, b);
}

int32_t rf_d64_gt(uint64_t a, uint64_t b)
{
    return bid64_quiet_greater(a, b);
}

int32_t rf_d64_ge(uint64_t a, uint64_t b)
{
    return bid64_quiet_greater_equal(a, b);
}

int32_t rf_d128_eq(d128_t a, d128_t b)
{
    return bid128_quiet_equal(to_bid128(a), to_bid128(b));
}

int32_t rf_d128_ne(d128_t a, d128_t b)
{
    return bid128_quiet_not_equal(to_bid128(a), to_bid128(b));
}

int32_t rf_d128_lt(d128_t a, d128_t b)
{
    return bid128_quiet_less(to_bid128(a), to_bid128(b));
}

int32_t rf_d128_le(d128_t a, d128_t b)
{
    return bid128_quiet_less_equal(to_bid128(a), to_bid128(b));
}

int32_t rf_d128_gt(d128_t a, d128_t b)
{
    return bid128_quiet_greater(to_bid128(a), to_bid128(b));
}

int32_t rf_d128_ge(d128_t a, d128_t b)
{
    return bid128_quiet_greater_equal(to_bid128(a), to_bid128(b));
}
