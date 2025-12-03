#ifndef RAZORFORGE_MATH_H
#define RAZORFORGE_MATH_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// ============================================================================
// IEEE 754-2008 Decimal Floating Point Types
// Using Intel Decimal Library (BID format)
// ============================================================================

// d128 type (decimal128 - 34 significant digits)
typedef struct d128_t {
    uint64_t low;
    uint64_t high;
} d128_t;

// ============================================================================
// d32 operations (decimal32 - 7 significant digits)
// ============================================================================

// Arithmetic
uint32_t d32_add(uint32_t a, uint32_t b);
uint32_t d32_sub(uint32_t a, uint32_t b);
uint32_t d32_mul(uint32_t a, uint32_t b);
uint32_t d32_div(uint32_t a, uint32_t b);
uint32_t d32_neg(uint32_t a);
int32_t d32_cmp(uint32_t a, uint32_t b);

// Conversion from/to string
uint32_t d32_from_string(const char* str);
char* d32_to_string(uint32_t val);

// Conversion from integers
uint32_t d32_from_i32(int32_t val);
uint32_t d32_from_i64(int64_t val);
uint32_t d32_from_u32(uint32_t val);
uint32_t d32_from_u64(uint64_t val);

// Conversion to integers
int32_t d32_to_i32(uint32_t val);
int64_t d32_to_i64(uint32_t val);
uint32_t d32_to_u32(uint32_t val);
uint64_t d32_to_u64(uint32_t val);

// Conversion to other decimal types
uint64_t d32_to_d64(uint32_t x);
d128_t d32_to_d128(uint32_t x);

// ============================================================================
// d64 operations (decimal64 - 16 significant digits)
// ============================================================================

// Arithmetic
uint64_t d64_add(uint64_t a, uint64_t b);
uint64_t d64_sub(uint64_t a, uint64_t b);
uint64_t d64_mul(uint64_t a, uint64_t b);
uint64_t d64_div(uint64_t a, uint64_t b);
uint64_t d64_neg(uint64_t a);
int32_t d64_cmp(uint64_t a, uint64_t b);

// Conversion from/to string
uint64_t d64_from_string(const char* str);
char* d64_to_string(uint64_t val);

// Conversion from integers
uint64_t d64_from_i32(int32_t val);
uint64_t d64_from_i64(int64_t val);
uint64_t d64_from_u32(uint32_t val);
uint64_t d64_from_u64(uint64_t val);

// Conversion to integers
int32_t d64_to_i32(uint64_t val);
int64_t d64_to_i64(uint64_t val);

// Conversion to other decimal types
uint32_t d64_to_d32(uint64_t x);
d128_t d64_to_d128(uint64_t x);

// ============================================================================
// d128 operations (decimal128 - 34 significant digits)
// ============================================================================

// Arithmetic
d128_t d128_add(d128_t a, d128_t b);
d128_t d128_sub(d128_t a, d128_t b);
d128_t d128_mul(d128_t a, d128_t b);
d128_t d128_div(d128_t a, d128_t b);
d128_t d128_neg(d128_t a);
int32_t d128_cmp(d128_t a, d128_t b);

// Conversion from/to string
d128_t d128_from_string(const char* str);
char* d128_to_string(d128_t val);

// Conversion from integers
d128_t d128_from_i32(int32_t val);
d128_t d128_from_i64(int64_t val);
d128_t d128_from_u32(uint32_t val);
d128_t d128_from_u64(uint64_t val);

// Conversion to integers
int32_t d128_to_i32(d128_t val);
int64_t d128_to_i64(d128_t val);

// Conversion to other decimal types
uint32_t d128_to_d32(d128_t x);
uint64_t d128_to_d64(d128_t x);

// ============================================================================
// Binary float to decimal conversions
// ============================================================================

uint32_t rf_f32_to_d32(float x);
uint64_t rf_f32_to_d64(float x);
d128_t rf_f32_to_d128(float x);
double rf_f32_to_f64(float x);

uint32_t rf_f64_to_d32(double x);
uint64_t rf_f64_to_d64(double x);
d128_t rf_f64_to_d128(double x);
float rf_f64_to_f32(double x);

// ============================================================================
// Decimal to binary float conversions
// ============================================================================

float rf_d32_to_f32(uint32_t x);
double rf_d32_to_f64(uint32_t x);
uint64_t rf_d32_to_d64(uint32_t x);
d128_t rf_d32_to_d128(uint32_t x);

float rf_d64_to_f32(uint64_t x);
double rf_d64_to_f64(uint64_t x);
uint32_t rf_d64_to_d32(uint64_t x);
d128_t rf_d64_to_d128(uint64_t x);

float rf_d128_to_f32(d128_t x);
double rf_d128_to_f64(d128_t x);
uint32_t rf_d128_to_d32(d128_t x);
uint64_t rf_d128_to_d64(d128_t x);

// ============================================================================
// d32 math functions
// Basic operations that don't require float128 emulation
// NOTE: Transcendental functions (sin, cos, exp, log, etc.) require Intel's
// float128 emulation code which needs their full build system.
// For transcendental decimal math, use rf_bigdec_* (MAPM) functions instead.
// ============================================================================

uint32_t rf_d32_sqrt(uint32_t x);
uint32_t rf_d32_abs(uint32_t x);
uint32_t rf_d32_ceil(uint32_t x);
uint32_t rf_d32_floor(uint32_t x);
uint32_t rf_d32_round(uint32_t x);
uint32_t rf_d32_trunc(uint32_t x);

uint32_t rf_d32_fmod(uint32_t x, uint32_t y);
uint32_t rf_d32_fma(uint32_t x, uint32_t y, uint32_t z);
uint32_t rf_d32_min(uint32_t x, uint32_t y);
uint32_t rf_d32_max(uint32_t x, uint32_t y);

int32_t rf_d32_isnan(uint32_t x);
int32_t rf_d32_isinf(uint32_t x);
int32_t rf_d32_isfinite(uint32_t x);
int32_t rf_d32_isnormal(uint32_t x);
int32_t rf_d32_iszero(uint32_t x);
int32_t rf_d32_signbit(uint32_t x);

// ============================================================================
// d64 math functions
// Basic operations that don't require float128 emulation
// ============================================================================

uint64_t rf_d64_sqrt(uint64_t x);
uint64_t rf_d64_abs(uint64_t x);
uint64_t rf_d64_ceil(uint64_t x);
uint64_t rf_d64_floor(uint64_t x);
uint64_t rf_d64_round(uint64_t x);
uint64_t rf_d64_trunc(uint64_t x);

uint64_t rf_d64_fmod(uint64_t x, uint64_t y);
uint64_t rf_d64_fma(uint64_t x, uint64_t y, uint64_t z);
uint64_t rf_d64_min(uint64_t x, uint64_t y);
uint64_t rf_d64_max(uint64_t x, uint64_t y);

int32_t rf_d64_isnan(uint64_t x);
int32_t rf_d64_isinf(uint64_t x);
int32_t rf_d64_isfinite(uint64_t x);
int32_t rf_d64_isnormal(uint64_t x);
int32_t rf_d64_iszero(uint64_t x);
int32_t rf_d64_signbit(uint64_t x);

// ============================================================================
// d128 math functions
// Basic operations that don't require float128 emulation
// ============================================================================

d128_t rf_d128_sqrt(d128_t x);
d128_t rf_d128_abs(d128_t x);
d128_t rf_d128_ceil(d128_t x);
d128_t rf_d128_floor(d128_t x);
d128_t rf_d128_round(d128_t x);
d128_t rf_d128_trunc(d128_t x);

d128_t rf_d128_fmod(d128_t x, d128_t y);
d128_t rf_d128_fma(d128_t x, d128_t y, d128_t z);
d128_t rf_d128_min(d128_t x, d128_t y);
d128_t rf_d128_max(d128_t x, d128_t y);

int32_t rf_d128_isnan(d128_t x);
int32_t rf_d128_isinf(d128_t x);
int32_t rf_d128_isfinite(d128_t x);
int32_t rf_d128_isnormal(d128_t x);
int32_t rf_d128_iszero(d128_t x);
int32_t rf_d128_signbit(d128_t x);

// ============================================================================
// Special values
// ============================================================================

uint32_t rf_d32_nan(void);
uint32_t rf_d32_inf(void);
uint32_t rf_d32_neg_inf(void);

uint64_t rf_d64_nan(void);
uint64_t rf_d64_inf(void);
uint64_t rf_d64_neg_inf(void);

d128_t rf_d128_nan(void);
d128_t rf_d128_inf(void);
d128_t rf_d128_neg_inf(void);

// ============================================================================
// Comparison predicates
// ============================================================================

int32_t rf_d32_eq(uint32_t a, uint32_t b);
int32_t rf_d32_ne(uint32_t a, uint32_t b);
int32_t rf_d32_lt(uint32_t a, uint32_t b);
int32_t rf_d32_le(uint32_t a, uint32_t b);
int32_t rf_d32_gt(uint32_t a, uint32_t b);
int32_t rf_d32_ge(uint32_t a, uint32_t b);

int32_t rf_d64_eq(uint64_t a, uint64_t b);
int32_t rf_d64_ne(uint64_t a, uint64_t b);
int32_t rf_d64_lt(uint64_t a, uint64_t b);
int32_t rf_d64_le(uint64_t a, uint64_t b);
int32_t rf_d64_gt(uint64_t a, uint64_t b);
int32_t rf_d64_ge(uint64_t a, uint64_t b);

int32_t rf_d128_eq(d128_t a, d128_t b);
int32_t rf_d128_ne(d128_t a, d128_t b);
int32_t rf_d128_lt(d128_t a, d128_t b);
int32_t rf_d128_le(d128_t a, d128_t b);
int32_t rf_d128_gt(d128_t a, d128_t b);
int32_t rf_d128_ge(d128_t a, d128_t b);

// ============================================================================
// LibTomMath - Arbitrary precision integer arithmetic
// https://github.com/libtom/libtommath (Public Domain)
// ============================================================================

// Forward declaration for LibTomMath's mp_int type
// When using LibTomMath, include <tommath.h> directly
typedef struct rf_bigint {
    int used, alloc, sign;
    void* dp; // mp_digit array
} rf_bigint;

// RazorForge wrappers for LibTomMath operations
// Lifecycle management
rf_bigint* rf_bigint_new(void);
void rf_bigint_clear(rf_bigint* a);
int rf_bigint_copy(rf_bigint* dest, rf_bigint* src);
int rf_bigint_init(rf_bigint* a);

// Initialization from primitives
int rf_bigint_set_i64(rf_bigint* a, int64_t val);
int rf_bigint_set_u64(rf_bigint* a, uint64_t val);
int rf_bigint_set_str(rf_bigint* a, const char* str, int radix);

// Conversion to primitives
int64_t rf_bigint_get_i64(rf_bigint* a);
uint64_t rf_bigint_get_u64(rf_bigint* a);
char* rf_bigint_get_str(rf_bigint* a, int radix);

// Arithmetic operations
int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b);
int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_neg(rf_bigint* result, rf_bigint* a);
int rf_bigint_abs(rf_bigint* result, rf_bigint* a);

// Comparison
int rf_bigint_cmp(rf_bigint* a, rf_bigint* b); // -1, 0, 1
int rf_bigint_cmp_i64(rf_bigint* a, int64_t b);
int rf_bigint_is_zero(rf_bigint* a);
int rf_bigint_is_neg(rf_bigint* a);

// Bitwise operations
int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits);
int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits);

// Advanced operations
int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp);
int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a);
int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b);
int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b);

// ============================================================================
// MAPM - Mike's Arbitrary Precision Math Library
// https://github.com/LuaDist/mapm (Freeware)
// ============================================================================

// Forward declaration for MAPM's M_APM type
// When using MAPM, include <m_apm.h> directly
typedef void* rf_bigdecimal; // Opaque pointer to M_APM

// Lifecycle management
rf_bigdecimal rf_bigdec_new(void);
void rf_bigdec_free(rf_bigdecimal a);
rf_bigdecimal rf_bigdec_copy(rf_bigdecimal a);

// Initialization
void rf_bigdec_set_i64(rf_bigdecimal a, int64_t val);
void rf_bigdec_set_f64(rf_bigdecimal a, double val);
void rf_bigdec_set_str(rf_bigdecimal a, const char* str);

// Conversion
int64_t rf_bigdec_get_i64(rf_bigdecimal a);
double rf_bigdec_get_f64(rf_bigdecimal a);
char* rf_bigdec_get_str(rf_bigdecimal a, int decimal_places);

// Arithmetic operations (with precision parameter)
void rf_bigdec_add(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_sub(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_mul(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_div(rf_bigdecimal result, int precision, rf_bigdecimal a, rf_bigdecimal b);
void rf_bigdec_neg(rf_bigdecimal result, rf_bigdecimal a);
void rf_bigdec_abs(rf_bigdecimal result, rf_bigdecimal a);

// Comparison
int rf_bigdec_cmp(rf_bigdecimal a, rf_bigdecimal b); // -1, 0, 1
int rf_bigdec_is_zero(rf_bigdecimal a);
int rf_bigdec_is_neg(rf_bigdecimal a);

// Math functions (with precision parameter)
void rf_bigdec_sqrt(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_pow(rf_bigdecimal result, int precision, rf_bigdecimal base, rf_bigdecimal exp);
void rf_bigdec_exp(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_log(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_log10(rf_bigdecimal result, int precision, rf_bigdecimal a);

// Trigonometric functions (with precision parameter)
void rf_bigdec_sin(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_cos(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_tan(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_asin(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_acos(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_atan(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_sinh(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_cosh(rf_bigdecimal result, int precision, rf_bigdecimal a);
void rf_bigdec_tanh(rf_bigdecimal result, int precision, rf_bigdecimal a);

// Rounding
void rf_bigdec_ceil(rf_bigdecimal result, rf_bigdecimal a);
void rf_bigdec_floor(rf_bigdecimal result, rf_bigdecimal a);
void rf_bigdec_round(rf_bigdecimal result, int decimal_places, rf_bigdecimal a);
void rf_bigdec_trunc(rf_bigdecimal result, int decimal_places, rf_bigdecimal a);

// Constants
void rf_bigdec_pi(rf_bigdecimal result, int precision);
void rf_bigdec_e(rf_bigdecimal result, int precision);

// ============================================================================
// cmath-compliant math functions for binary floating point types
// ============================================================================

// ============================================================================
// f16 (half-precision float) math functions
// IEEE 754 binary16: 1 sign, 5 exponent, 10 mantissa bits
// Range: ~6.1e-5 to 65504, ~3.3 decimal digits precision
//
// NOTE: f16 is stored as uint16_t in C. LLVM handles f16 natively for basic
// arithmetic. Transcendental functions are computed by promoting to f32,
// computing, then demoting back to f16.
// ============================================================================

// Conversion functions
uint16_t rf_f16_from_f32(float x);
uint16_t rf_f16_from_f64(double x);
float rf_f16_to_f32(uint16_t x);
double rf_f16_to_f64(uint16_t x);

// Arithmetic (these can be LLVM intrinsics)
uint16_t rf_f16_add(uint16_t a, uint16_t b);
uint16_t rf_f16_sub(uint16_t a, uint16_t b);
uint16_t rf_f16_mul(uint16_t a, uint16_t b);
uint16_t rf_f16_div(uint16_t a, uint16_t b);
uint16_t rf_f16_neg(uint16_t x);

// Comparison
int32_t rf_f16_eq(uint16_t a, uint16_t b);
int32_t rf_f16_ne(uint16_t a, uint16_t b);
int32_t rf_f16_lt(uint16_t a, uint16_t b);
int32_t rf_f16_le(uint16_t a, uint16_t b);
int32_t rf_f16_gt(uint16_t a, uint16_t b);
int32_t rf_f16_ge(uint16_t a, uint16_t b);

// Basic math (can be LLVM intrinsics or simple operations)
uint16_t rf_f16_abs(uint16_t x);
uint16_t rf_f16_copysign(uint16_t x, uint16_t y);
uint16_t rf_f16_min(uint16_t x, uint16_t y);
uint16_t rf_f16_max(uint16_t x, uint16_t y);

// Rounding (LLVM has intrinsics for these)
uint16_t rf_f16_ceil(uint16_t x);
uint16_t rf_f16_floor(uint16_t x);
uint16_t rf_f16_trunc(uint16_t x);
uint16_t rf_f16_round(uint16_t x);

// Square root (LLVM intrinsic)
uint16_t rf_f16_sqrt(uint16_t x);

// Fused multiply-add (LLVM intrinsic)
uint16_t rf_f16_fma(uint16_t x, uint16_t y, uint16_t z);

// Classification predicates
int32_t rf_f16_isnan(uint16_t x);
int32_t rf_f16_isinf(uint16_t x);
int32_t rf_f16_isfinite(uint16_t x);
int32_t rf_f16_isnormal(uint16_t x);
int32_t rf_f16_iszero(uint16_t x);
int32_t rf_f16_signbit(uint16_t x);

// Special values
uint16_t rf_f16_nan(void);
uint16_t rf_f16_inf(void);
uint16_t rf_f16_neg_inf(void);
uint16_t rf_f16_epsilon(void);   // Smallest x such that 1.0 + x != 1.0
uint16_t rf_f16_min_positive(void); // Smallest positive normal
uint16_t rf_f16_max_value(void);    // Largest finite value (65504)

// Transcendental functions (computed via f32 promotion)
uint16_t rf_f16_sin(uint16_t x);
uint16_t rf_f16_cos(uint16_t x);
uint16_t rf_f16_tan(uint16_t x);
uint16_t rf_f16_asin(uint16_t x);
uint16_t rf_f16_acos(uint16_t x);
uint16_t rf_f16_atan(uint16_t x);
uint16_t rf_f16_atan2(uint16_t y, uint16_t x);

uint16_t rf_f16_sinh(uint16_t x);
uint16_t rf_f16_cosh(uint16_t x);
uint16_t rf_f16_tanh(uint16_t x);
uint16_t rf_f16_asinh(uint16_t x);
uint16_t rf_f16_acosh(uint16_t x);
uint16_t rf_f16_atanh(uint16_t x);

uint16_t rf_f16_exp(uint16_t x);
uint16_t rf_f16_exp2(uint16_t x);
uint16_t rf_f16_expm1(uint16_t x);
uint16_t rf_f16_log(uint16_t x);
uint16_t rf_f16_log2(uint16_t x);
uint16_t rf_f16_log10(uint16_t x);
uint16_t rf_f16_log1p(uint16_t x);

uint16_t rf_f16_pow(uint16_t base, uint16_t exp);
uint16_t rf_f16_cbrt(uint16_t x);
uint16_t rf_f16_hypot(uint16_t x, uint16_t y);
uint16_t rf_f16_fmod(uint16_t x, uint16_t y);
uint16_t rf_f16_remainder(uint16_t x, uint16_t y);

// f32 (float) math functions
float rf_f32_sin(float x);
float rf_f32_cos(float x);
float rf_f32_tan(float x);
float rf_f32_asin(float x);
float rf_f32_acos(float x);
float rf_f32_atan(float x);
float rf_f32_atan2(float y, float x);
float rf_f32_sinh(float x);
float rf_f32_cosh(float x);
float rf_f32_tanh(float x);
float rf_f32_asinh(float x);
float rf_f32_acosh(float x);
float rf_f32_atanh(float x);
float rf_f32_exp(float x);
float rf_f32_exp2(float x);
float rf_f32_expm1(float x);
float rf_f32_log(float x);
float rf_f32_log2(float x);
float rf_f32_log10(float x);
float rf_f32_log1p(float x);
float rf_f32_pow(float base, float exp);
float rf_f32_sqrt(float x);
float rf_f32_cbrt(float x);
float rf_f32_hypot(float x, float y);
float rf_f32_ceil(float x);
float rf_f32_floor(float x);
float rf_f32_trunc(float x);
float rf_f32_round(float x);
float rf_f32_fabs(float x);
float rf_f32_fmod(float x, float y);
float rf_f32_remainder(float x, float y);
float rf_f32_fma(float x, float y, float z);
float rf_f32_fmin(float x, float y);
float rf_f32_fmax(float x, float y);
float rf_f32_copysign(float x, float y);
int32_t rf_f32_isnan(float x);
int32_t rf_f32_isinf(float x);
int32_t rf_f32_isfinite(float x);
int32_t rf_f32_isnormal(float x);
int32_t rf_f32_signbit(float x);

// f64 (double) math functions
double rf_f64_sin(double x);
double rf_f64_cos(double x);
double rf_f64_tan(double x);
double rf_f64_asin(double x);
double rf_f64_acos(double x);
double rf_f64_atan(double x);
double rf_f64_atan2(double y, double x);
double rf_f64_sinh(double x);
double rf_f64_cosh(double x);
double rf_f64_tanh(double x);
double rf_f64_asinh(double x);
double rf_f64_acosh(double x);
double rf_f64_atanh(double x);
double rf_f64_exp(double x);
double rf_f64_exp2(double x);
double rf_f64_expm1(double x);
double rf_f64_log(double x);
double rf_f64_log2(double x);
double rf_f64_log10(double x);
double rf_f64_log1p(double x);
double rf_f64_pow(double base, double exp);
double rf_f64_sqrt(double x);
double rf_f64_cbrt(double x);
double rf_f64_hypot(double x, double y);
double rf_f64_ceil(double x);
double rf_f64_floor(double x);
double rf_f64_trunc(double x);
double rf_f64_round(double x);
double rf_f64_fabs(double x);
double rf_f64_fmod(double x, double y);
double rf_f64_remainder(double x, double y);
double rf_f64_fma(double x, double y, double z);
double rf_f64_fmin(double x, double y);
double rf_f64_fmax(double x, double y);
double rf_f64_copysign(double x, double y);
int32_t rf_f64_isnan(double x);
int32_t rf_f64_isinf(double x);
int32_t rf_f64_isfinite(double x);
int32_t rf_f64_isnormal(double x);
int32_t rf_f64_signbit(double x);

#ifdef __cplusplus
}
#endif

#endif // RAZORFORGE_MATH_H
