#ifndef RAZORFORGE_MATH_H
#define RAZORFORGE_MATH_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// libdfp - IEEE 754 decimal floating point operations
// d32 operations
uint32_t d32_add(uint32_t a, uint32_t b);
uint32_t d32_sub(uint32_t a, uint32_t b);
uint32_t d32_mul(uint32_t a, uint32_t b);
uint32_t d32_div(uint32_t a, uint32_t b);
int32_t d32_cmp(uint32_t a, uint32_t b);
uint32_t d32_from_string(const char* str);
char* d32_to_string(uint32_t val);

// d64 operations
uint64_t d64_add(uint64_t a, uint64_t b);
uint64_t d64_sub(uint64_t a, uint64_t b);
uint64_t d64_mul(uint64_t a, uint64_t b);
uint64_t d64_div(uint64_t a, uint64_t b);
int32_t d64_cmp(uint64_t a, uint64_t b);
uint64_t d64_from_string(const char* str);
char* d64_to_string(uint64_t val);

// d128 operations (using struct for 128-bit values)
typedef struct {
    uint64_t low;
    uint64_t high;
} d128_t;

d128_t d128_add(d128_t a, d128_t b);
d128_t d128_sub(d128_t a, d128_t b);
d128_t d128_mul(d128_t a, d128_t b);
d128_t d128_div(d128_t a, d128_t b);
int32_t d128_cmp(d128_t a, d128_t b);
d128_t d128_from_string(const char* str);
char* d128_to_string(d128_t val);

// libbf - Arbitrary precision arithmetic
typedef struct bf_context_s bf_context_t;
typedef struct bf_number_s bf_number_t;

// Context management
void bf_context_init(bf_context_t* ctx, void* realloc_func, void* free_func);
void bf_context_end(bf_context_t* ctx);

// Number management
void bf_init(bf_context_t* ctx, bf_number_t* r);
void bf_delete(bf_number_t* r);

// Memory allocation helpers
bf_number_t* bf_alloc_number(void);
void bf_free_number(bf_number_t* num);

// Basic operations
int32_t bf_set_si(bf_number_t* r, int64_t a);
int32_t bf_set_ui(bf_number_t* r, uint64_t a);
int32_t bf_add(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags);
int32_t bf_sub(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags);
int32_t bf_mul(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags);
int32_t bf_div(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags);
int32_t bf_cmp(bf_number_t* a, bf_number_t* b);
char* bf_ftoa(size_t* plen, bf_number_t* a, int32_t radix, uint64_t prec, uint32_t flags);

// mafm - Multiple precision arithmetic for decimals
typedef struct mafm_context_s mafm_context_t;
typedef struct mafm_number_s mafm_number_t;

// Context management
void mafm_context_init(mafm_context_t* ctx, int32_t precision);
void mafm_context_free(mafm_context_t* ctx);

// Number management
void mafm_init(mafm_number_t* num);
void mafm_clear(mafm_number_t* num);

// Memory allocation helpers
mafm_number_t* mafm_alloc_number(void);
void mafm_free_number(mafm_number_t* num);
mafm_context_t* mafm_alloc_context(void);
void mafm_free_context(mafm_context_t* ctx);

// String operations
int32_t mafm_set_str(mafm_number_t* num, const char* str, int32_t radix);
char* mafm_get_str(mafm_number_t* num, int32_t radix);

// Arithmetic operations
int32_t mafm_add(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx);
int32_t mafm_sub(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx);
int32_t mafm_mul(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx);
int32_t mafm_div(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx);
int32_t mafm_cmp(mafm_number_t* a, mafm_number_t* b);

// Conversion operations
int32_t mafm_set_si(mafm_number_t* num, int64_t val);
int32_t mafm_set_d(mafm_number_t* num, double val);
int64_t mafm_get_si(mafm_number_t* num);
double mafm_get_d(mafm_number_t* num);

// Rounding modes
#define MAFM_RNDN 0  // Round to nearest, ties to even
#define MAFM_RNDZ 1  // Round toward zero
#define MAFM_RNDU 2  // Round toward +infinity
#define MAFM_RNDD 3  // Round toward -infinity
#define MAFM_RNDA 4  // Round away from zero

#ifdef __cplusplus
}
#endif

#endif // RAZORFORGE_MATH_H