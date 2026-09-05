/*
 * RazorForge Compiler-RT Shim
 *
 * Provides missing 128-bit integer runtime functions for Windows.
 * LibBF uses __int128 on 64-bit platforms which requires these functions
 * that are normally provided by compiler-rt (Clang) or libgcc (GCC).
 *
 * On Windows, neither is typically available, so we provide our own.
 */

#include <stdint.h>

/*
 * Force these builtins into the DLL export table. CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS only exports symbols
 * from a target's OWN object files, NOT ones pulled in from a linked static library (this shim). Without an
 * explicit dllexport they stay linked-but-hidden, so the in-process ORC JIT — which resolves symbols via a
 * process-wide dynamic-library search over the loaded razorforge_runtime.dll's exports — can't find them, and
 * a JIT'd program doing 128-bit integer division/modulo fails with "Symbols not found: __udivti3 …". AOT
 * builds link compiler-rt directly and never hit this; only the JIT dev-loop needs the export.
 */
#if defined(_WIN32)
#define RT_EXPORT __declspec(dllexport)
#else
#define RT_EXPORT
#endif

/* Only needed when __int128 is available (64-bit platforms with Clang/GCC) */
#if defined(__SIZEOF_INT128__)

typedef unsigned __int128 tu_int;
typedef __int128 ti_int;

/* Helper: count leading zeros for 64-bit */
static inline int clz64_impl(uint64_t x) {
    if (x == 0) return 64;
#if defined(__GNUC__) || defined(__clang__)
    return __builtin_clzll(x);
#else
    int n = 0;
    if ((x & 0xFFFFFFFF00000000ULL) == 0) { n += 32; x <<= 32; }
    if ((x & 0xFFFF000000000000ULL) == 0) { n += 16; x <<= 16; }
    if ((x & 0xFF00000000000000ULL) == 0) { n += 8;  x <<= 8; }
    if ((x & 0xF000000000000000ULL) == 0) { n += 4;  x <<= 4; }
    if ((x & 0xC000000000000000ULL) == 0) { n += 2;  x <<= 2; }
    if ((x & 0x8000000000000000ULL) == 0) { n += 1; }
    return n;
#endif
}

/* Count leading zeros for 128-bit */
static inline int clz128(tu_int x) {
    uint64_t hi = (uint64_t)(x >> 64);
    uint64_t lo = (uint64_t)x;
    if (hi != 0)
        return clz64_impl(hi);
    return 64 + clz64_impl(lo);
}

/*
 * __udivti3 - Unsigned 128-bit division
 * Returns: a / b
 *
 * Algorithm: Binary long division
 */
RT_EXPORT tu_int __udivti3(tu_int a, tu_int b) {
    if (b == 0) {
        /* Division by zero - return max value (undefined behavior) */
        return ~(tu_int)0;
    }

    if (b > a) {
        return 0;
    }

    if (b == a) {
        return 1;
    }

    /* Fast path for divisor fits in 64 bits and dividend high is 0 */
    uint64_t b_hi = (uint64_t)(b >> 64);
    if (b_hi == 0) {
        uint64_t b_lo = (uint64_t)b;
        uint64_t a_hi = (uint64_t)(a >> 64);

        if (a_hi == 0) {
            /* Both fit in 64 bits */
            return (tu_int)((uint64_t)a / b_lo);
        }
    }

    /* Binary long division */
    int shift = clz128(b) - clz128(a);
    b <<= shift;

    tu_int quotient = 0;
    for (int i = 0; i <= shift; i++) {
        quotient <<= 1;
        if (a >= b) {
            a -= b;
            quotient |= 1;
        }
        b >>= 1;
    }

    return quotient;
}

/*
 * __umodti3 - Unsigned 128-bit modulo
 * Returns: a % b
 */
RT_EXPORT tu_int __umodti3(tu_int a, tu_int b) {
    if (b == 0) {
        return 0;  /* Undefined behavior */
    }

    if (b > a) {
        return a;
    }

    if (b == a) {
        return 0;
    }

    /* Fast path */
    uint64_t b_hi = (uint64_t)(b >> 64);
    if (b_hi == 0) {
        uint64_t b_lo = (uint64_t)b;
        uint64_t a_hi = (uint64_t)(a >> 64);

        if (a_hi == 0) {
            return (tu_int)((uint64_t)a % b_lo);
        }
    }

    /* Binary long division - we only need the remainder */
    int shift = clz128(b) - clz128(a);
    b <<= shift;

    for (int i = 0; i <= shift; i++) {
        if (a >= b) {
            a -= b;
        }
        b >>= 1;
    }

    return a;
}

/*
 * __divti3 - Signed 128-bit division
 * Returns: a / b
 */
RT_EXPORT ti_int __divti3(ti_int a, ti_int b) {
    int neg = 0;

    if (a < 0) {
        a = -a;
        neg = !neg;
    }
    if (b < 0) {
        b = -b;
        neg = !neg;
    }

    tu_int result = __udivti3((tu_int)a, (tu_int)b);

    if (neg) {
        return -(ti_int)result;
    }
    return (ti_int)result;
}

/*
 * __modti3 - Signed 128-bit modulo
 * Returns: a % b
 */
RT_EXPORT ti_int __modti3(ti_int a, ti_int b) {
    int neg = 0;

    if (a < 0) {
        a = -a;
        neg = 1;
    }
    if (b < 0) {
        b = -b;
    }

    tu_int result = __umodti3((tu_int)a, (tu_int)b);

    if (neg) {
        return -(ti_int)result;
    }
    return (ti_int)result;
}

/*
 * __udivmodti4 - Combined unsigned 128-bit division and modulo
 * Returns: a / b, stores a % b in *rem
 */
RT_EXPORT tu_int __udivmodti4(tu_int a, tu_int b, tu_int *rem) {
    if (b == 0) {
        if (rem) *rem = 0;
        return ~(tu_int)0;
    }

    if (b > a) {
        if (rem) *rem = a;
        return 0;
    }

    if (b == a) {
        if (rem) *rem = 0;
        return 1;
    }

    /* Fast path */
    uint64_t b_hi = (uint64_t)(b >> 64);
    if (b_hi == 0) {
        uint64_t b_lo = (uint64_t)b;
        uint64_t a_hi = (uint64_t)(a >> 64);

        if (a_hi == 0) {
            if (rem) *rem = (tu_int)((uint64_t)a % b_lo);
            return (tu_int)((uint64_t)a / b_lo);
        }
    }

    /* Binary long division */
    int shift = clz128(b) - clz128(a);
    b <<= shift;

    tu_int quotient = 0;
    for (int i = 0; i <= shift; i++) {
        quotient <<= 1;
        if (a >= b) {
            a -= b;
            quotient |= 1;
        }
        b >>= 1;
    }

    if (rem) *rem = a;
    return quotient;
}

#endif /* __SIZEOF_INT128__ */
