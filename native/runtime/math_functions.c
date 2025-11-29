/*
 * RazorForge Runtime - Math Functions
 * cmath-compliant wrappers for floating point operations
 */

#include <math.h>
#include <stdint.h>
#include "../include/razorforge_math.h"

// ============================================================================
// f32 (float) math functions - wrapping cmath functions
// ============================================================================

float rf_f32_sin(float x) { return sinf(x); }
float rf_f32_cos(float x) { return cosf(x); }
float rf_f32_tan(float x) { return tanf(x); }
float rf_f32_asin(float x) { return asinf(x); }
float rf_f32_acos(float x) { return acosf(x); }
float rf_f32_atan(float x) { return atanf(x); }
float rf_f32_atan2(float y, float x) { return atan2f(y, x); }
float rf_f32_sinh(float x) { return sinhf(x); }
float rf_f32_cosh(float x) { return coshf(x); }
float rf_f32_tanh(float x) { return tanhf(x); }
float rf_f32_asinh(float x) { return asinhf(x); }
float rf_f32_acosh(float x) { return acoshf(x); }
float rf_f32_atanh(float x) { return atanhf(x); }
float rf_f32_exp(float x) { return expf(x); }
float rf_f32_exp2(float x) { return exp2f(x); }
float rf_f32_expm1(float x) { return expm1f(x); }
float rf_f32_log(float x) { return logf(x); }
float rf_f32_log2(float x) { return log2f(x); }
float rf_f32_log10(float x) { return log10f(x); }
float rf_f32_log1p(float x) { return log1pf(x); }
float rf_f32_pow(float base, float exp) { return powf(base, exp); }
float rf_f32_sqrt(float x) { return sqrtf(x); }
float rf_f32_cbrt(float x) { return cbrtf(x); }
float rf_f32_hypot(float x, float y) { return hypotf(x, y); }
float rf_f32_ceil(float x) { return ceilf(x); }
float rf_f32_floor(float x) { return floorf(x); }
float rf_f32_trunc(float x) { return truncf(x); }
float rf_f32_round(float x) { return roundf(x); }
float rf_f32_fabs(float x) { return fabsf(x); }
float rf_f32_fmod(float x, float y) { return fmodf(x, y); }
float rf_f32_remainder(float x, float y) { return remainderf(x, y); }
float rf_f32_fma(float x, float y, float z) { return fmaf(x, y, z); }
float rf_f32_fmin(float x, float y) { return fminf(x, y); }
float rf_f32_fmax(float x, float y) { return fmaxf(x, y); }
float rf_f32_copysign(float x, float y) { return copysignf(x, y); }
int32_t rf_f32_isnan(float x) { return isnan(x) ? 1 : 0; }
int32_t rf_f32_isinf(float x) { return isinf(x) ? 1 : 0; }
int32_t rf_f32_isfinite(float x) { return isfinite(x) ? 1 : 0; }
int32_t rf_f32_isnormal(float x) { return isnormal(x) ? 1 : 0; }
int32_t rf_f32_signbit(float x) { return signbit(x) ? 1 : 0; }

// ============================================================================
// f64 (double) math functions - wrapping cmath functions
// ============================================================================

double rf_f64_sin(double x) { return sin(x); }
double rf_f64_cos(double x) { return cos(x); }
double rf_f64_tan(double x) { return tan(x); }
double rf_f64_asin(double x) { return asin(x); }
double rf_f64_acos(double x) { return acos(x); }
double rf_f64_atan(double x) { return atan(x); }
double rf_f64_atan2(double y, double x) { return atan2(y, x); }
double rf_f64_sinh(double x) { return sinh(x); }
double rf_f64_cosh(double x) { return cosh(x); }
double rf_f64_tanh(double x) { return tanh(x); }
double rf_f64_asinh(double x) { return asinh(x); }
double rf_f64_acosh(double x) { return acosh(x); }
double rf_f64_atanh(double x) { return atanh(x); }
double rf_f64_exp(double x) { return exp(x); }
double rf_f64_exp2(double x) { return exp2(x); }
double rf_f64_expm1(double x) { return expm1(x); }
double rf_f64_log(double x) { return log(x); }
double rf_f64_log2(double x) { return log2(x); }
double rf_f64_log10(double x) { return log10(x); }
double rf_f64_log1p(double x) { return log1p(x); }
double rf_f64_pow(double base, double exp) { return pow(base, exp); }
double rf_f64_sqrt(double x) { return sqrt(x); }
double rf_f64_cbrt(double x) { return cbrt(x); }
double rf_f64_hypot(double x, double y) { return hypot(x, y); }
double rf_f64_ceil(double x) { return ceil(x); }
double rf_f64_floor(double x) { return floor(x); }
double rf_f64_trunc(double x) { return trunc(x); }
double rf_f64_round(double x) { return round(x); }
double rf_f64_fabs(double x) { return fabs(x); }
double rf_f64_fmod(double x, double y) { return fmod(x, y); }
double rf_f64_remainder(double x, double y) { return remainder(x, y); }
double rf_f64_fma(double x, double y, double z) { return fma(x, y, z); }
double rf_f64_fmin(double x, double y) { return fmin(x, y); }
double rf_f64_fmax(double x, double y) { return fmax(x, y); }
double rf_f64_copysign(double x, double y) { return copysign(x, y); }
int32_t rf_f64_isnan(double x) { return isnan(x) ? 1 : 0; }
int32_t rf_f64_isinf(double x) { return isinf(x) ? 1 : 0; }
int32_t rf_f64_isfinite(double x) { return isfinite(x) ? 1 : 0; }
int32_t rf_f64_isnormal(double x) { return isnormal(x) ? 1 : 0; }
int32_t rf_f64_signbit(double x) { return signbit(x) ? 1 : 0; }

// ============================================================================
// Type conversions - f32/f64
// ============================================================================

double rf_f32_to_f64(float x) { return (double)x; }
float rf_f64_to_f32(double x) { return (float)x; }
