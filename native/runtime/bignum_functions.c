/*
 * RazorForge Runtime - Big Number Functions
 * Wrappers for LibTomMath (integers) and MAPM (decimals)
 */

#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <math.h>
#include "../include/razorforge_math.h"
#if defined(_MSC_VER)
#include <intrin.h>
#endif

// ============================================================================
// TEMPORARY (bench-only): hardware 128/64 -> 64 unsigned divide, the old M-G-replaced
// primitive. Kept solely so div_ab_bench.rf can A/B the shim's `divq` against M-G.
// Remove once benchmarking is done.
// ============================================================================
uint64_t rf_udivrem_128_64(uint64_t hi, uint64_t lo, uint64_t d, uint64_t* rem) {
#if defined(__x86_64__) && (defined(__GNUC__) || defined(__clang__))
    uint64_t q, r;
    __asm__("divq %[d]" : "=a"(q), "=d"(r) : "a"(lo), "d"(hi), [d] "r"(d));
    *rem = r;
    return q;
#elif defined(_MSC_VER) && defined(_M_X64)
    return _udiv128(hi, lo, d, rem);
#else
    unsigned __int128 n = ((unsigned __int128)hi << 64) | (unsigned __int128)lo;
    *rem = (uint64_t)(n % d);
    return (uint64_t)(n / d);
#endif
}

// ============================================================================
// LibTomMath wrappers for arbitrary precision integers
// ============================================================================

#ifdef HAVE_LIBTOMMATH
#include <tommath.h>

rf_bigint* rf_bigint_new(void)
{
    rf_bigint* a = (rf_bigint*)malloc(sizeof(rf_bigint));
    if (a)
    {
        mp_init((mp_int*)a);
    }
    return a;
}

int rf_bigint_init(rf_bigint* a)
{
    return mp_init((mp_int*)a);
}

void rf_bigint_clear(rf_bigint* a)
{
    if (a)
    {
        mp_clear((mp_int*)a);
        free(a);
    }
}

int rf_bigint_copy(rf_bigint* dest, rf_bigint* src)
{
    return mp_copy((mp_int*)src, (mp_int*)dest);
}

int rf_bigint_set_i64(rf_bigint* a, int64_t val)
{
    mp_set_i64((mp_int*)a, val);
    return 0;
}

int rf_bigint_set_u64(rf_bigint* a, uint64_t val)
{
    mp_set_u64((mp_int*)a, val);
    return 0;
}

int rf_bigint_set_str(rf_bigint* a, const char* str, int radix)
{
    return mp_read_radix((mp_int*)a, str, radix);
}

int64_t rf_bigint_get_i64(rf_bigint* a)
{
    return mp_get_i64((mp_int*)a);
}

uint64_t rf_bigint_get_u64(rf_bigint* a)
{
    return mp_get_u64((mp_int*)a);
}

char* rf_bigint_get_str(rf_bigint* a, int radix)
{
    size_t size;
    mp_radix_size((mp_int*)a, radix, &size);
    char* str = (char*)malloc(size);
    if (str)
    {
        mp_to_radix((mp_int*)a, str, size, NULL, radix);
    }
    return str;
}

int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_add((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_sub((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_mul((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b)
{
    return mp_div((mp_int*)a, (mp_int*)b, (mp_int*)quotient, (mp_int*)remainder);
}

int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_mod((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_neg(rf_bigint* result, rf_bigint* a)
{
    return mp_neg((mp_int*)a, (mp_int*)result);
}

int rf_bigint_abs(rf_bigint* result, rf_bigint* a)
{
    return mp_abs((mp_int*)a, (mp_int*)result);
}

int rf_bigint_cmp(rf_bigint* a, rf_bigint* b)
{
    return mp_cmp((mp_int*)a, (mp_int*)b);
}

int rf_bigint_cmp_i64(rf_bigint* a, int64_t b)
{
    mp_int tmp;
    mp_init(&tmp);
    mp_set_i64(&tmp, b);
    int result = mp_cmp((mp_int*)a, &tmp);
    mp_clear(&tmp);
    return result;
}

int rf_bigint_is_zero(rf_bigint* a)
{
    return mp_iszero((mp_int*)a);
}

int rf_bigint_is_neg(rf_bigint* a)
{
    return mp_isneg((mp_int*)a);
}

int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_and((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_or((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_xor((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits)
{
    return mp_mul_2d((mp_int*)a, bits, (mp_int*)result);
}

int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits)
{
    return mp_div_2d((mp_int*)a, bits, (mp_int*)result, NULL);
}

int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp)
{
    return mp_expt_n((mp_int*)base, (int)exp, (mp_int*)result);
}

int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a)
{
    return mp_sqrt((mp_int*)a, (mp_int*)result);
}

int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_gcd((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return mp_lcm((mp_int*)a, (mp_int*)b, (mp_int*)result);
}

#else
// Stub implementations when LibTomMath is not available

rf_bigint* rf_bigint_new(void)
{
    rf_bigint* a = (rf_bigint*)malloc(sizeof(rf_bigint));
    if (a)
    {
        memset(a, 0, sizeof(rf_bigint));
    }
    return a;
}

int rf_bigint_init(rf_bigint* a)
{
    memset(a, 0, sizeof(rf_bigint));
    return 0;
}

void rf_bigint_clear(rf_bigint* a)
{
    if (a)
    {
        if (a->dp) free(a->dp);
        free(a);
    }
}

int rf_bigint_copy(rf_bigint* dest, rf_bigint* src)
{
    if (!dest || !src) return -1;
    dest->used = src->used;
    dest->alloc = src->alloc;
    dest->sign = src->sign;
    if (src->dp)
    {
        dest->dp = malloc(sizeof(int64_t));
        if (dest->dp) *(int64_t*)dest->dp = *(int64_t*)src->dp;
    }
    return 0;
}

int rf_bigint_set_i64(rf_bigint* a, int64_t val)
{
    a->dp = malloc(sizeof(int64_t));
    if (a->dp) *(int64_t*)a->dp = val;
    a->used = 1;
    a->sign = val < 0 ? 1 : 0;
    return 0;
}

int rf_bigint_set_u64(rf_bigint* a, uint64_t val)
{
    a->dp = malloc(sizeof(uint64_t));
    if (a->dp) *(uint64_t*)a->dp = val;
    a->used = 1;
    a->sign = 0;
    return 0;
}

int rf_bigint_set_str(rf_bigint* a, const char* str, int radix)
{
    (void)radix;
    int64_t val = strtoll(str, NULL, radix);
    return rf_bigint_set_i64(a, val);
}

int64_t rf_bigint_get_i64(rf_bigint* a)
{
    if (a->dp) return *(int64_t*)a->dp;
    return 0;
}

uint64_t rf_bigint_get_u64(rf_bigint* a)
{
    if (a->dp) return *(uint64_t*)a->dp;
    return 0;
}

char* rf_bigint_get_str(rf_bigint* a, int radix)
{
    (void)radix;
    char* str = (char*)malloc(32);
    if (str) snprintf(str, 32, "%lld", (long long)rf_bigint_get_i64(a));
    return str;
}

// Stub arithmetic - uses int64_t (loses precision for large numbers)
int rf_bigint_add(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) + rf_bigint_get_i64(b));
}

int rf_bigint_sub(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) - rf_bigint_get_i64(b));
}

int rf_bigint_mul(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) * rf_bigint_get_i64(b));
}

int rf_bigint_div(rf_bigint* quotient, rf_bigint* remainder, rf_bigint* a, rf_bigint* b)
{
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    rf_bigint_set_i64(quotient, av / bv);
    rf_bigint_set_i64(remainder, av % bv);
    return 0;
}

int rf_bigint_mod(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) % rf_bigint_get_i64(b));
}

int rf_bigint_neg(rf_bigint* result, rf_bigint* a)
{
    return rf_bigint_set_i64(result, -rf_bigint_get_i64(a));
}

int rf_bigint_abs(rf_bigint* result, rf_bigint* a)
{
    int64_t v = rf_bigint_get_i64(a);
    return rf_bigint_set_i64(result, v < 0 ? -v : v);
}

int rf_bigint_cmp(rf_bigint* a, rf_bigint* b)
{
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    if (av < bv) return -1;
    if (av > bv) return 1;
    return 0;
}

int rf_bigint_cmp_i64(rf_bigint* a, int64_t b)
{
    int64_t av = rf_bigint_get_i64(a);
    if (av < b) return -1;
    if (av > b) return 1;
    return 0;
}

int rf_bigint_is_zero(rf_bigint* a)
{
    return rf_bigint_get_i64(a) == 0;
}

int rf_bigint_is_neg(rf_bigint* a)
{
    return rf_bigint_get_i64(a) < 0;
}

int rf_bigint_and(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) & rf_bigint_get_i64(b));
}

int rf_bigint_or(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) | rf_bigint_get_i64(b));
}

int rf_bigint_xor(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) ^ rf_bigint_get_i64(b));
}

int rf_bigint_shl(rf_bigint* result, rf_bigint* a, int bits)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) << bits);
}

int rf_bigint_shr(rf_bigint* result, rf_bigint* a, int bits)
{
    return rf_bigint_set_i64(result, rf_bigint_get_i64(a) >> bits);
}

int rf_bigint_pow(rf_bigint* result, rf_bigint* base, uint32_t exp)
{
    int64_t b = rf_bigint_get_i64(base);
    int64_t r = 1;
    for (uint32_t i = 0; i < exp; i++) r *= b;
    return rf_bigint_set_i64(result, r);
}

int rf_bigint_sqrt(rf_bigint* result, rf_bigint* a)
{
    int64_t v = rf_bigint_get_i64(a);
    int64_t r = (int64_t)sqrt((double)v);
    return rf_bigint_set_i64(result, r);
}

int rf_bigint_gcd(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
    int64_t av = rf_bigint_get_i64(a);
    int64_t bv = rf_bigint_get_i64(b);
    while (bv != 0)
    {
        int64_t t = bv;
        bv = av % bv;
        av = t;
    }
    return rf_bigint_set_i64(result, av < 0 ? -av : av);
}

int rf_bigint_lcm(rf_bigint* result, rf_bigint* a, rf_bigint* b)
{
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
