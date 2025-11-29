/*
 * RazorForge Runtime - Decimal Floating Point Functions
 * IEEE 754 decimal floating point operations using libdfp
 *
 * NOTE: This file contains stub implementations. For full functionality,
 * link with libdfp and replace these stubs with proper implementations.
 */

#include <stdint.h>
#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <math.h>
#include "../include/razorforge_math.h"

// ============================================================================
// d32 operations (decimal32 - 7 significant digits)
// Stored as 32-bit unsigned integer in DPD or BID encoding
// ============================================================================

#ifdef HAVE_LIBDFP
// When libdfp is available, use native _Decimal32 type
#include <dfp/decimal.h>

uint32_t d32_add(uint32_t a, uint32_t b) {
    _Decimal32 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da + db;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint32_t d32_sub(uint32_t a, uint32_t b) {
    _Decimal32 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da - db;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint32_t d32_mul(uint32_t a, uint32_t b) {
    _Decimal32 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da * db;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint32_t d32_div(uint32_t a, uint32_t b) {
    _Decimal32 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da / db;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

int32_t d32_cmp(uint32_t a, uint32_t b) {
    _Decimal32 da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    if (da < db) return -1;
    if (da > db) return 1;
    return 0;
}

#else
// Stub implementations using double as fallback
// These lose precision but allow compilation without libdfp

uint32_t d32_add(uint32_t a, uint32_t b) {
    // Stub: treat as floats (loses decimal precision)
    float fa, fb;
    memcpy(&fa, &a, sizeof(fa));
    memcpy(&fb, &b, sizeof(fb));
    float result = fa + fb;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint32_t d32_sub(uint32_t a, uint32_t b) {
    float fa, fb;
    memcpy(&fa, &a, sizeof(fa));
    memcpy(&fb, &b, sizeof(fb));
    float result = fa - fb;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint32_t d32_mul(uint32_t a, uint32_t b) {
    float fa, fb;
    memcpy(&fa, &a, sizeof(fa));
    memcpy(&fb, &b, sizeof(fb));
    float result = fa * fb;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint32_t d32_div(uint32_t a, uint32_t b) {
    float fa, fb;
    memcpy(&fa, &a, sizeof(fa));
    memcpy(&fb, &b, sizeof(fb));
    float result = fa / fb;
    uint32_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

int32_t d32_cmp(uint32_t a, uint32_t b) {
    float fa, fb;
    memcpy(&fa, &a, sizeof(fa));
    memcpy(&fb, &b, sizeof(fb));
    if (fa < fb) return -1;
    if (fa > fb) return 1;
    return 0;
}

#endif // HAVE_LIBDFP

uint32_t d32_from_string(const char* str) {
    float val = (float)atof(str);
    uint32_t r;
    memcpy(&r, &val, sizeof(r));
    return r;
}

char* d32_to_string(uint32_t val) {
    float f;
    memcpy(&f, &val, sizeof(f));
    char* buf = (char*)malloc(32);
    snprintf(buf, 32, "%g", f);
    return buf;
}

// ============================================================================
// d64 operations (decimal64 - 16 significant digits)
// ============================================================================

#ifdef HAVE_LIBDFP

uint64_t d64_add(uint64_t a, uint64_t b) {
    _Decimal64 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da + db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint64_t d64_sub(uint64_t a, uint64_t b) {
    _Decimal64 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da - db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint64_t d64_mul(uint64_t a, uint64_t b) {
    _Decimal64 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da * db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint64_t d64_div(uint64_t a, uint64_t b) {
    _Decimal64 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da / db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

int32_t d64_cmp(uint64_t a, uint64_t b) {
    _Decimal64 da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    if (da < db) return -1;
    if (da > db) return 1;
    return 0;
}

#else
// Stub implementations using double as fallback

uint64_t d64_add(uint64_t a, uint64_t b) {
    double da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    double result = da + db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint64_t d64_sub(uint64_t a, uint64_t b) {
    double da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    double result = da - db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint64_t d64_mul(uint64_t a, uint64_t b) {
    double da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    double result = da * db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

uint64_t d64_div(uint64_t a, uint64_t b) {
    double da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    double result = da / db;
    uint64_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

int32_t d64_cmp(uint64_t a, uint64_t b) {
    double da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    if (da < db) return -1;
    if (da > db) return 1;
    return 0;
}

#endif // HAVE_LIBDFP

uint64_t d64_from_string(const char* str) {
    double val = atof(str);
    uint64_t r;
    memcpy(&r, &val, sizeof(r));
    return r;
}

char* d64_to_string(uint64_t val) {
    double d;
    memcpy(&d, &val, sizeof(d));
    char* buf = (char*)malloc(64);
    snprintf(buf, 64, "%.16g", d);
    return buf;
}

// ============================================================================
// d128 operations (decimal128 - 34 significant digits)
// ============================================================================

#ifdef HAVE_LIBDFP

d128_t d128_add(d128_t a, d128_t b) {
    _Decimal128 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da + db;
    d128_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

d128_t d128_sub(d128_t a, d128_t b) {
    _Decimal128 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da - db;
    d128_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

d128_t d128_mul(d128_t a, d128_t b) {
    _Decimal128 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da * db;
    d128_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

d128_t d128_div(d128_t a, d128_t b) {
    _Decimal128 da, db, result;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    result = da / db;
    d128_t r;
    memcpy(&r, &result, sizeof(r));
    return r;
}

int32_t d128_cmp(d128_t a, d128_t b) {
    _Decimal128 da, db;
    memcpy(&da, &a, sizeof(da));
    memcpy(&db, &b, sizeof(db));
    if (da < db) return -1;
    if (da > db) return 1;
    return 0;
}

#else
// Stub implementations - d128 requires proper 128-bit decimal support

d128_t d128_add(d128_t a, d128_t b) {
    // Very crude approximation using double (loses precision)
    d128_t r;
    r.low = a.low + b.low;
    r.high = a.high + b.high + (r.low < a.low ? 1 : 0);
    return r;
}

d128_t d128_sub(d128_t a, d128_t b) {
    d128_t r;
    r.low = a.low - b.low;
    r.high = a.high - b.high - (a.low < b.low ? 1 : 0);
    return r;
}

d128_t d128_mul(d128_t a, d128_t b) {
    // Stub - needs proper implementation
    d128_t r = {0, 0};
    return r;
}

d128_t d128_div(d128_t a, d128_t b) {
    // Stub - needs proper implementation
    d128_t r = {0, 0};
    return r;
}

int32_t d128_cmp(d128_t a, d128_t b) {
    if (a.high < b.high) return -1;
    if (a.high > b.high) return 1;
    if (a.low < b.low) return -1;
    if (a.low > b.low) return 1;
    return 0;
}

#endif // HAVE_LIBDFP

d128_t d128_from_string(const char* str) {
    d128_t r = {0, 0};
    // Stub implementation
    return r;
}

char* d128_to_string(d128_t val) {
    char* buf = (char*)malloc(64);
    snprintf(buf, 64, "d128(%llu,%llu)",
             (unsigned long long)val.low,
             (unsigned long long)val.high);
    return buf;
}

// ============================================================================
// Decimal math functions
// ============================================================================

uint32_t rf_d32_sqrt(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    f = sqrtf(f);
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint32_t rf_d32_abs(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    f = fabsf(f);
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint32_t rf_d32_ceil(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    f = ceilf(f);
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint32_t rf_d32_floor(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    f = floorf(f);
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint32_t rf_d32_round(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    f = roundf(f);
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint32_t rf_d32_trunc(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    f = truncf(f);
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint64_t rf_d64_sqrt(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    d = sqrt(d);
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

uint64_t rf_d64_abs(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    d = fabs(d);
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

uint64_t rf_d64_ceil(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    d = ceil(d);
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

uint64_t rf_d64_floor(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    d = floor(d);
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

uint64_t rf_d64_round(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    d = round(d);
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

uint64_t rf_d64_trunc(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    d = trunc(d);
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

// d128 math - stub implementations
d128_t rf_d128_sqrt(d128_t x) { return x; }
d128_t rf_d128_abs(d128_t x) { return x; }
d128_t rf_d128_ceil(d128_t x) { return x; }
d128_t rf_d128_floor(d128_t x) { return x; }
d128_t rf_d128_round(d128_t x) { return x; }
d128_t rf_d128_trunc(d128_t x) { return x; }

// ============================================================================
// Type conversions
// ============================================================================

// f32 to decimal conversions
uint32_t rf_f32_to_d32(float x) {
    uint32_t r;
    memcpy(&r, &x, sizeof(r));
    return r;
}

uint64_t rf_f32_to_d64(float x) {
    double d = (double)x;
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

d128_t rf_f32_to_d128(float x) {
    d128_t r = {0, 0};
    // Stub
    return r;
}

// f64 to decimal conversions
uint32_t rf_f64_to_d32(double x) {
    float f = (float)x;
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

uint64_t rf_f64_to_d64(double x) {
    uint64_t r;
    memcpy(&r, &x, sizeof(r));
    return r;
}

d128_t rf_f64_to_d128(double x) {
    d128_t r = {0, 0};
    // Stub
    return r;
}

// d32 conversions
float rf_d32_to_f32(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    return f;
}

double rf_d32_to_f64(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    return (double)f;
}

uint64_t rf_d32_to_d64(uint32_t x) {
    float f;
    memcpy(&f, &x, sizeof(f));
    double d = (double)f;
    uint64_t r;
    memcpy(&r, &d, sizeof(r));
    return r;
}

d128_t rf_d32_to_d128(uint32_t x) {
    d128_t r = {0, 0};
    return r;
}

// d64 conversions
float rf_d64_to_f32(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    return (float)d;
}

double rf_d64_to_f64(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    return d;
}

uint32_t rf_d64_to_d32(uint64_t x) {
    double d;
    memcpy(&d, &x, sizeof(d));
    float f = (float)d;
    uint32_t r;
    memcpy(&r, &f, sizeof(r));
    return r;
}

d128_t rf_d64_to_d128(uint64_t x) {
    d128_t r = {x, 0};
    return r;
}

// d128 conversions
float rf_d128_to_f32(d128_t x) {
    return 0.0f; // Stub
}

double rf_d128_to_f64(d128_t x) {
    return 0.0; // Stub
}

uint32_t rf_d128_to_d32(d128_t x) {
    return 0; // Stub
}

uint64_t rf_d128_to_d64(d128_t x) {
    return x.low; // Stub
}
