/*
 * RazorForge Runtime - Big Number Functions
 * Wrappers for LibTomMath (integers) and MAPM (decimals)
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include "../include/razorforge_math.h"

// ============================================================================
// LibTomMath wrappers for arbitrary precision integers
// ============================================================================

#ifdef HAVE_LIBTOMMATH
#include <tommath.h>

rf_bigint* rf_bigint_new(void) {
    rf_bigint* a = (rf_bigint*)malloc(sizeof(rf_bigint));
    if (a) {
        mp_init((mp_int*)a);
    }
    return a;
}

int rf_bigint_init(rf_bigint* a) {
    return mp_init((mp_int*)a);
}

void rf_bigint_clear(rf_bigint* a) {
    if (a) {
        mp_clear((mp_int*)a);
        free(a);
    }
}

int rf_bigint_copy(rf_bigint* dest, rf_bigint* src) {
    return mp_copy((mp_int*)src, (mp_int*)dest);
}

int rf_bigint_set_i64(rf_bigint* a, int64_t val) {
    mp_set_i64((mp_int*)a, val);
    return 0;
}

int rf_bigint_set_u64(rf_bigint* a, uint64_t val) {
    mp_set_u64((mp_int*)a, val);
    return 0;
}

int rf_bigint_set_str(rf_bigint* a, const char* str, int radix) {
    return mp_read_radix((mp_int*)a, str, radix);
}

int64_t rf_bigint_get_i64(rf_bigint* a) {
    return mp_get_i64((mp_int*)a);
}

uint64_t rf_bigint_get_u64(rf_bigint* a) {
    return mp_get_u64((mp_int*)a);
}

char* rf_bigint_get_str(rf_bigint* a, int radix) {
    size_t size;
    mp_radix_size((mp_int*)a, radix, &size);
    char* str = (char*)malloc(size);
    if (str) {
        mp_to_radix((mp_int*)a, str, size, NULL, radix);
    }
    return str;
}

int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_add((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_sub((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_mul((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b) {
    return mp_div((mp_int*)a, (mp_int*)b, (mp_int*)quotient, (mp_int*)remainder);
}

int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_mod((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_neg(rf_bigint* result, rf_bigint* a) {
    return mp_neg((mp_int*)a, (mp_int*)result);
}

int rf_bigint_abs(rf_bigint* result, rf_bigint* a) {
    return mp_abs((mp_int*)a, (mp_int*)result);
}

int rf_bigint_cmp(rf_bigint* a, rf_bigint* b) {
    return mp_cmp((mp_int*)a, (mp_int*)b);
}

int rf_bigint_cmp_i64(rf_bigint* a, int64_t b) {
    mp_int tmp;
    mp_init(&tmp);
    mp_set_i64(&tmp, b);
    int result = mp_cmp((mp_int*)a, &tmp);
    mp_clear(&tmp);
    return result;
}

int rf_bigint_is_zero(rf_bigint* a) {
    return mp_iszero((mp_int*)a);
}

int rf_bigint_is_neg(rf_bigint* a) {
    return mp_isneg((mp_int*)a);
}

int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_and((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_or((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_xor((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits) {
    return mp_mul_2d((mp_int*)a, bits, (mp_int*)result);
}

int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits) {
    return mp_div_2d((mp_int*)a, bits, (mp_int*)result, NULL);
}

int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp) {
    return mp_expt_u32((mp_int*)base, exp, (mp_int*)result);
}

int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a) {
    return mp_sqrt((mp_int*)a, (mp_int*)result);
}

int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_gcd((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return mp_lcm((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

#else
// Stub implementations when LibTomMath is not available

rf_bigint* rf_bigint_new(void) {
    rf_bigint* a = (rf_bigint*)malloc(sizeof(rf_bigint));
    if (a) {
        memset(a, 0, sizeof(rf_bigint));
    }
    return a;
}

int rf_bigint_init(rf_bigint* a) {
    memset(a, 0, sizeof(rf_bigint));
    return 0;
}

void rf_bigint_clear(rf_bigint* a) {
    if (a) {
        if (a->dp) free(a->dp);
        free(a);
    }
}

int rf_bigint_copy(rf_bigint* dest, rf_bigint* src) {
    if (!dest || !src) return -1;
    dest->used = src->used;
    dest->alloc = src->alloc;
    dest->sign = src->sign;
    if (src->dp) {
        dest->dp = malloc(sizeof(int64_t));
        if (dest->dp) *(int64_t*)dest->dp = *(int64_t*)src->dp;
    }
    return 0;
}

int rf_bigint_set_i64(rf_bigint* a, int64_t val) {
    a->dp = malloc(sizeof(int64_t));
    if (a->dp) *(int64_t*)a->dp = val;
    a->used = 1;
    a->sign = val < 0 ? 1 : 0;
    return 0;
}

int rf_bigint_set_u64(rf_bigint* a, uint64_t val) {
    a->dp = malloc(sizeof(uint64_t));
    if (a->dp) *(uint64_t*)a->dp = val;
    a->used = 1;
    a->sign = 0;
    return 0;
}

int rf_bigint_set_str(rf_bigint* a, const char* str, int radix) {
    (void)radix;
    int64_t val = strtoll(str, NULL, radix);
    return rf_bigint_set_i64(a, val);
}

int64_t rf_bigint_get_i64(rf_bigint* a) {
    if (a->dp) return *(int64_t*)a->dp;
    return 0;
}

uint64_t rf_bigint_get_u64(rf_bigint* a) {
    if (a->dp) return *(uint64_t*)a->dp;
    return 0;
}

char* rf_bigint_get_str(rf_bigint* a, int radix) {
    (void)radix;
    char* str = (char*)malloc(32);
    if (str) snprintf(str, 32, "%lld", (long long)rf_bigint_get_i64(a));
    return str;
}

// Stub arithmetic - uses int64_t (loses precision for large numbers)
int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) + rf_bigint_get_i64(b));
}

int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) - rf_bigint_get_i64(b));
}

int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) * rf_bigint_get_i64(b));
}

int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b) {
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    rf_bigint_set_i64(quotient, av / bv);
    rf_bigint_set_i64(remainder, av % bv);
    return 0;
}

int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) % rf_bigint_get_i64(b));
}

int rf_bigint_neg(rf_bigint* result, rf_bigint* a) {
    return rf_bigint_set_i64(result, -rf_bigint_get_i64(a));
}

int rf_bigint_abs(rf_bigint* result, rf_bigint* a) {
    int64_t v = rf_bigint_get_i64(a);
    return rf_bigint_set_i64(result, v < 0 ? -v : v);
}

int rf_bigint_cmp(rf_bigint* a, rf_bigint* b) {
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    if (av < bv) return -1;
    if (av > bv) return 1;
    return 0;
}

int rf_bigint_cmp_i64(rf_bigint* a, int64_t b) {
    int64_t av = rf_bigint_get_i64(a);
    if (av < b) return -1;
    if (av > b) return 1;
    return 0;
}

int rf_bigint_is_zero(rf_bigint* a) {
    return rf_bigint_get_i64(a) == 0;
}

int rf_bigint_is_neg(rf_bigint* a) {
    return rf_bigint_get_i64(a) < 0;
}

int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) & rf_bigint_get_i64(b));
}

int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) | rf_bigint_get_i64(b));
}

int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) ^ rf_bigint_get_i64(b));
}

int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) << bits);
}

int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits) {
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) >> bits);
}

int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp) {
    int64_t b = rf_bigint_get_i64(base);
    int64_t r = 1;
    for (uint32_t i = 0; i < exp; i++) r *= b;
    return rf_bigint_set_i64(result, r);
}

int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a) {
    int64_t v = rf_bigint_get_i64(a);
    int64_t r = (int64_t)sqrt((double)v);
    return rf_bigint_set_i64(result, r);
}

int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    while (bv != 0) {
        int64_t t = bv;
        bv = av % bv;
        av = t;
    }
    return rf_bigint_set_i64(result, av < 0 ? -av : av);
}

int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b) {
    rf_bigint gcd_result;
    rf_bigint_init(&gcd_result);
    rf_bigint_gcd(&gcd_result, a, b);
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    int64_t gv = rf_bigint_get_i64(&gcd_result);
    rf_bigint_clear(&gcd_result);
    return rf_bigint_set_i64(result, (av / gv) * bv);
}

#endif // HAVE_LIBTOMMATH

// ============================================================================
// MAPM wrappers for arbitrary precision decimals
// ============================================================================

#ifdef HAVE_MAPM
#include <m_apm.h>

rf_bigdecimal rf_bigdec_new(void) {
    return m_apm_init();
}

void rf_bigdec_free(rf_bigdecimal a) {
    m_apm_free((M_APM)a);
}

rf_bigdecimal rf_bigdec_copy(rf_bigdecimal a) {
    M_APM result = m_apm_init();
    m_apm_copy(result, (M_APM)a);
    return result;
}

void rf_bigdec_set_i64(rf_bigdecimal a, int64_t val) {
    char buf[32];
    snprintf(buf, sizeof(buf), "%lld", (long long)val);
    m_apm_set_string((M_APM)a, buf);
}

void rf_bigdec_set_f64(rf_bigdecimal a, double val) {
    m_apm_set_double((M_APM)a, val);
}

void rf_bigdec_set_str(rf_bigdecimal a, const char* str) {
    m_apm_set_string((M_APM)a, (char*)str);
}

int64_t rf_bigdec_get_i64(rf_bigdecimal a) {
    char* str = rf_bigdec_get_str(a, 0);
    int64_t result = strtoll(str, NULL, 10);
    free(str);
    return result;
}

double rf_bigdec_get_f64(rf_bigdecimal a) {
    char buf[64];
    m_apm_to_string(buf, 15, (M_APM)a);
    return atof(buf);
}

char* rf_bigdec_get_str(rf_bigdecimal a, int decimal_places) {
    int places = decimal_places > 0 ? decimal_places : m_apm_significant_digits((M_APM)a);
    char* str = (char*)malloc(places + 32);
    if (str) {
        m_apm_to_string(str, places, (M_APM)a);
    }
    return str;
}

void rf_bigdec_add(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b) {
    m_apm_add((M_APM)result, (M_APM)a, (M_APM)b);
}

void rf_bigdec_sub(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b) {
    m_apm_subtract((M_APM)result, (M_APM)a, (M_APM)b);
}

void rf_bigdec_mul(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b) {
    m_apm_multiply((M_APM)result, (M_APM)a, (M_APM)b);
}

void rf_bigdec_div(rf_bigdecimal result, int precision, rf_bigdecimal a, rf_bigdecimal b) {
    m_apm_divide((M_APM)result, precision, (M_APM)a, (M_APM)b);
}

void rf_bigdec_neg(rf_bigdecimal result, rf_bigdecimal a) {
    m_apm_negate((M_APM)result, (M_APM)a);
}

void rf_bigdec_abs(rf_bigdecimal result, rf_bigdecimal a) {
    m_apm_absolute_value((M_APM)result, (M_APM)a);
}

int rf_bigdec_cmp(rf_bigdecimal a, rf_bigdecimal b) {
    return m_apm_compare((M_APM)a, (M_APM)b);
}

int rf_bigdec_is_zero(rf_bigdecimal a) {
    return m_apm_sign((M_APM)a) == 0;
}

int rf_bigdec_is_neg(rf_bigdecimal a) {
    return m_apm_sign((M_APM)a) < 0;
}

void rf_bigdec_sqrt(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_sqrt((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_pow(rf_bigdecimal result, int precision, rf_bigdecimal base, rf_bigdecimal exp) {
    m_apm_pow((M_APM)result, precision, (M_APM)base, (M_APM)exp);
}

void rf_bigdec_exp(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_exp((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_log(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_log((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_log10(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_log10((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_sin(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_sin((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_cos(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_cos((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_tan(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_tan((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_asin(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_asin((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_acos(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_acos((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_atan(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_atan((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_sinh(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_sinh((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_cosh(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_cosh((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_tanh(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    m_apm_tanh((M_APM)result, precision, (M_APM)a);
}

void rf_bigdec_ceil(rf_bigdecimal result, rf_bigdecimal a) {
    m_apm_ceil((M_APM)result, (M_APM)a);
}

void rf_bigdec_floor(rf_bigdecimal result, rf_bigdecimal a) {
    m_apm_floor((M_APM)result, (M_APM)a);
}

void rf_bigdec_round(rf_bigdecimal result, int decimal_places, rf_bigdecimal a) {
    m_apm_round((M_APM)result, decimal_places, (M_APM)a);
}

void rf_bigdec_trunc(rf_bigdecimal result, int decimal_places, rf_bigdecimal a) {
    // MAPM doesn't have a direct truncate, use integer part
    m_apm_integer_divide((M_APM)result, (M_APM)a, MM_One);
}

void rf_bigdec_pi(rf_bigdecimal result, int precision) {
    m_apm_copy((M_APM)result, MM_PI);
}

void rf_bigdec_e(rf_bigdecimal result, int precision) {
    m_apm_copy((M_APM)result, MM_E);
}

#else
// Stub implementations when MAPM is not available

rf_bigdecimal rf_bigdec_new(void) {
    double* p = (double*)malloc(sizeof(double));
    if (p) *p = 0.0;
    return p;
}

void rf_bigdec_free(rf_bigdecimal a) {
    free(a);
}

rf_bigdecimal rf_bigdec_copy(rf_bigdecimal a) {
    double* p = (double*)malloc(sizeof(double));
    if (p && a) *p = *(double*)a;
    return p;
}

void rf_bigdec_set_i64(rf_bigdecimal a, int64_t val) {
    if (a) *(double*)a = (double)val;
}

void rf_bigdec_set_f64(rf_bigdecimal a, double val) {
    if (a) *(double*)a = val;
}

void rf_bigdec_set_str(rf_bigdecimal a, const char* str) {
    if (a) *(double*)a = atof(str);
}

int64_t rf_bigdec_get_i64(rf_bigdecimal a) {
    return a ? (int64_t)*(double*)a : 0;
}

double rf_bigdec_get_f64(rf_bigdecimal a) {
    return a ? *(double*)a : 0.0;
}

char* rf_bigdec_get_str(rf_bigdecimal a, int decimal_places) {
    char* str = (char*)malloc(64);
    if (str && a) {
        snprintf(str, 64, "%.*g", decimal_places > 0 ? decimal_places : 15, *(double*)a);
    }
    return str;
}

void rf_bigdec_add(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b) {
    if (result && a && b) *(double*)result = *(double*)a + *(double*)b;
}

void rf_bigdec_sub(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b) {
    if (result && a && b) *(double*)result = *(double*)a - *(double*)b;
}

void rf_bigdec_mul(rf_bigdecimal result, rf_bigdecimal a, rf_bigdecimal b) {
    if (result && a && b) *(double*)result = *(double*)a * *(double*)b;
}

void rf_bigdec_div(rf_bigdecimal result, int precision, rf_bigdecimal a, rf_bigdecimal b) {
    (void)precision;
    if (result && a && b) *(double*)result = *(double*)a / *(double*)b;
}

void rf_bigdec_neg(rf_bigdecimal result, rf_bigdecimal a) {
    if (result && a) *(double*)result = -*(double*)a;
}

void rf_bigdec_abs(rf_bigdecimal result, rf_bigdecimal a) {
    if (result && a) *(double*)result = fabs(*(double*)a);
}

int rf_bigdec_cmp(rf_bigdecimal a, rf_bigdecimal b) {
    if (!a || !b) return 0;
    double av = *(double*)a, bv = *(double*)b;
    if (av < bv) return -1;
    if (av > bv) return 1;
    return 0;
}

int rf_bigdec_is_zero(rf_bigdecimal a) {
    return a ? (*(double*)a == 0.0) : 1;
}

int rf_bigdec_is_neg(rf_bigdecimal a) {
    return a ? (*(double*)a < 0.0) : 0;
}

void rf_bigdec_sqrt(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = sqrt(*(double*)a);
}

void rf_bigdec_pow(rf_bigdecimal result, int precision, rf_bigdecimal base, rf_bigdecimal exp) {
    (void)precision;
    if (result && base && exp) *(double*)result = pow(*(double*)base, *(double*)exp);
}

void rf_bigdec_exp(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = exp(*(double*)a);
}

void rf_bigdec_log(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = log(*(double*)a);
}

void rf_bigdec_log10(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = log10(*(double*)a);
}

void rf_bigdec_sin(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = sin(*(double*)a);
}

void rf_bigdec_cos(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = cos(*(double*)a);
}

void rf_bigdec_tan(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = tan(*(double*)a);
}

void rf_bigdec_asin(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = asin(*(double*)a);
}

void rf_bigdec_acos(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = acos(*(double*)a);
}

void rf_bigdec_atan(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = atan(*(double*)a);
}

void rf_bigdec_sinh(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = sinh(*(double*)a);
}

void rf_bigdec_cosh(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = cosh(*(double*)a);
}

void rf_bigdec_tanh(rf_bigdecimal result, int precision, rf_bigdecimal a) {
    (void)precision;
    if (result && a) *(double*)result = tanh(*(double*)a);
}

void rf_bigdec_ceil(rf_bigdecimal result, rf_bigdecimal a) {
    if (result && a) *(double*)result = ceil(*(double*)a);
}

void rf_bigdec_floor(rf_bigdecimal result, rf_bigdecimal a) {
    if (result && a) *(double*)result = floor(*(double*)a);
}

void rf_bigdec_round(rf_bigdecimal result, int decimal_places, rf_bigdecimal a) {
    (void)decimal_places;
    if (result && a) *(double*)result = round(*(double*)a);
}

void rf_bigdec_trunc(rf_bigdecimal result, int decimal_places, rf_bigdecimal a) {
    (void)decimal_places;
    if (result && a) *(double*)result = trunc(*(double*)a);
}

void rf_bigdec_pi(rf_bigdecimal result, int precision) {
    (void)precision;
    if (result) *(double*)result = 3.14159265358979323846;
}

void rf_bigdec_e(rf_bigdecimal result, int precision) {
    (void)precision;
    if (result) *(double*)result = 2.71828182845904523536;
}

#endif // HAVE_MAPM
