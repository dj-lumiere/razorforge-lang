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
// Möller-Granlund reciprocal division for targets WITHOUT a hardware 128/64
// divide (arm64, wasm, ...). On x86-64 the `_pre` primitive uses `divq`/`_udiv128`
// and the reciprocal is a no-op; only the #else paths compile/run the M-G code.
// ============================================================================
#if (defined(__x86_64__) && (defined(__GNUC__) || defined(__clang__))) || (defined(_MSC_VER) && defined(_M_X64))
#define RF_HAS_HW_UDIV128 1
#else
#define RF_HAS_HW_UDIV128 0
#endif

#if !RF_HAS_HW_UDIV128
// reciprocal_2by1 seed table (intx): t[i] = 0x7fd00 / (i + 256).
static const uint16_t mg_recip_seed[256] = {
  2045, 2037, 2029, 2021, 2013, 2005, 1998, 1990, 1983, 1975, 1968, 1960, 1953, 1946, 1938, 1931,
  1924, 1917, 1910, 1903, 1896, 1889, 1883, 1876, 1869, 1863, 1856, 1849, 1843, 1836, 1830, 1824,
  1817, 1811, 1805, 1799, 1792, 1786, 1780, 1774, 1768, 1762, 1756, 1750, 1745, 1739, 1733, 1727,
  1722, 1716, 1710, 1705, 1699, 1694, 1688, 1683, 1677, 1672, 1667, 1661, 1656, 1651, 1646, 1641,
  1636, 1630, 1625, 1620, 1615, 1610, 1605, 1600, 1596, 1591, 1586, 1581, 1576, 1572, 1567, 1562,
  1558, 1553, 1548, 1544, 1539, 1535, 1530, 1526, 1521, 1517, 1513, 1508, 1504, 1500, 1495, 1491,
  1487, 1483, 1478, 1474, 1470, 1466, 1462, 1458, 1454, 1450, 1446, 1442, 1438, 1434, 1430, 1426,
  1422, 1418, 1414, 1411, 1407, 1403, 1399, 1396, 1392, 1388, 1384, 1381, 1377, 1374, 1370, 1366,
  1363, 1359, 1356, 1352, 1349, 1345, 1342, 1338, 1335, 1332, 1328, 1325, 1322, 1318, 1315, 1312,
  1308, 1305, 1302, 1299, 1295, 1292, 1289, 1286, 1283, 1280, 1276, 1273, 1270, 1267, 1264, 1261,
  1258, 1255, 1252, 1249, 1246, 1243, 1240, 1237, 1234, 1231, 1228, 1226, 1223, 1220, 1217, 1214,
  1211, 1209, 1206, 1203, 1200, 1197, 1195, 1192, 1189, 1187, 1184, 1181, 1179, 1176, 1173, 1171,
  1168, 1165, 1163, 1160, 1158, 1155, 1153, 1150, 1148, 1145, 1143, 1140, 1138, 1135, 1133, 1130,
  1128, 1125, 1123, 1121, 1118, 1116, 1113, 1111, 1109, 1106, 1104, 1102, 1099, 1097, 1095, 1092,
  1090, 1088, 1086, 1083, 1081, 1079, 1077, 1074, 1072, 1070, 1068, 1066, 1064, 1061, 1059, 1057,
  1055, 1053, 1051, 1049, 1047, 1044, 1042, 1040, 1038, 1036, 1034, 1032, 1030, 1028, 1026, 1024
};
// Moller-Granlund Algorithm 3: reciprocal of a NORMALIZED d (MSB set). Divide-free
// (table seed + multiply-only Newton); intx reciprocal_2by1 verbatim.
static uint64_t mg_reciprocal_2by1(uint64_t d) {
    uint64_t d9  = d >> 55;
    uint64_t v0  = mg_recip_seed[d9 - 256];
    uint64_t d40 = (d >> 24) + 1;
    uint64_t v1  = (v0 << 11) - ((v0 * v0 * d40) >> 40) - 1;
    uint64_t v2  = (v1 << 13) + ((v1 * (0x1000000000000000ULL - v1 * d40)) >> 47);
    uint64_t d0  = d & 1;
    uint64_t d63 = (d >> 1) + d0;
    uint64_t e   = ((v2 >> 1) & (0 - d0)) - v2 * d63;
    uint64_t v3  = (uint64_t)(((unsigned __int128)v2 * e) >> 64);
    v3 = (v3 >> 1) + (v2 << 31);
    uint64_t v4  = v3 - (uint64_t)(((unsigned __int128)v3 * d + d) >> 64) - d;
    return v4;
}
#endif

uint64_t rf_reciprocal_word(uint64_t d) {
#if RF_HAS_HW_UDIV128
    (void)d;
    return 0;
#else
    return mg_reciprocal_2by1(d);
#endif
}

uint64_t rf_udivrem_128_64_pre(uint64_t hi, uint64_t lo, uint64_t d, uint64_t v, uint64_t* rem) {
#if defined(__x86_64__) && (defined(__GNUC__) || defined(__clang__))
    (void)v;
    uint64_t q, r;
    __asm__("divq %[d]" : "=a"(q), "=d"(r) : "a"(lo), "d"(hi), [d] "r"(d));
    *rem = r;
    return q;
#elif defined(_MSC_VER) && defined(_M_X64)
    (void)v;
    return _udiv128(hi, lo, d, rem);
#else
    unsigned __int128 q = (unsigned __int128)v * (unsigned __int128)hi;
    q += ((unsigned __int128)hi << 64) | (unsigned __int128)lo;
    uint64_t q1 = (uint64_t)(q >> 64) + 1;
    uint64_t q0 = (uint64_t)q;
    uint64_t r  = lo - q1 * d;
    if (r > q0) { q1--; r += d; }
    if (r >= d) { q1++; r -= d; }
    *rem = r;
    return q1;
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
