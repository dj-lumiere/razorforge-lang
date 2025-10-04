#include "razorforge_math.h"
#include <stdlib.h>
#include <string.h>

// This file provides wrapper functions that can be called from LLVM IR
// It bridges between the LLVM calling convention and the actual math libraries

// Helper functions for memory management
bf_number_t* bf_alloc_number(void) {
    return (bf_number_t*)malloc(sizeof(bf_number_t));
}

void bf_free_number(bf_number_t* num) {
    if (num) {
        bf_delete(num);
        free(num);
    }
}

mafm_number_t* mafm_alloc_number(void) {
    return (mafm_number_t*)malloc(sizeof(mafm_number_t));
}

void mafm_free_number(mafm_number_t* num) {
    if (num) {
        mafm_clear(num);
        free(num);
    }
}

mafm_context_t* mafm_alloc_context(void) {
    return (mafm_context_t*)malloc(sizeof(mafm_context_t));
}

void mafm_free_context(mafm_context_t* ctx) {
    if (ctx) {
        mafm_context_free(ctx);
        free(ctx);
    }
}

// Global default context for mafm operations (initialized once)
static mafm_context_t* global_mafm_context = NULL;

__attribute__((constructor))
static void init_global_context(void) {
    global_mafm_context = mafm_alloc_context();
    mafm_context_init(global_mafm_context, 50); // 50 digits precision by default
}

__attribute__((destructor))
static void cleanup_global_context(void) {
    if (global_mafm_context) {
        mafm_free_context(global_mafm_context);
        global_mafm_context = NULL;
    }
}

// LLVM-compatible wrapper functions for high-precision decimals
// These use the global context to simplify LLVM IR generation

int32_t mafm_add_simple(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b) {
    return mafm_add(result, a, b, global_mafm_context);
}

int32_t mafm_sub_simple(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b) {
    return mafm_sub(result, a, b, global_mafm_context);
}

int32_t mafm_mul_simple(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b) {
    return mafm_mul(result, a, b, global_mafm_context);
}

int32_t mafm_div_simple(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b) {
    return mafm_div(result, a, b, global_mafm_context);
}

// Placeholder implementations if libraries are not available
// These will be replaced with actual library implementations

#ifndef HAVE_LIBDFP
uint32_t d32_add(uint32_t a, uint32_t b) { return a + b; } // Placeholder
uint32_t d32_sub(uint32_t a, uint32_t b) { return a - b; } // Placeholder
uint32_t d32_mul(uint32_t a, uint32_t b) { return a * b; } // Placeholder
uint32_t d32_div(uint32_t a, uint32_t b) { return a / b; } // Placeholder
int32_t d32_cmp(uint32_t a, uint32_t b) { return (a > b) - (a < b); }
uint32_t d32_from_string(const char* str) { return (uint32_t)atoi(str); }
char* d32_to_string(uint32_t val) { 
    char* result = malloc(32);
    snprintf(result, 32, "%u", val);
    return result;
}

uint64_t d64_add(uint64_t a, uint64_t b) { return a + b; } // Placeholder
uint64_t d64_sub(uint64_t a, uint64_t b) { return a - b; } // Placeholder  
uint64_t d64_mul(uint64_t a, uint64_t b) { return a * b; } // Placeholder
uint64_t d64_div(uint64_t a, uint64_t b) { return a / b; } // Placeholder
int32_t d64_cmp(uint64_t a, uint64_t b) { return (a > b) - (a < b); }
uint64_t d64_from_string(const char* str) { return (uint64_t)atoll(str); }
char* d64_to_string(uint64_t val) {
    char* result = malloc(32);
    snprintf(result, 32, "%llu", val);
    return result;
}

d128_t d128_add(d128_t a, d128_t b) { 
    d128_t result = {a.low + b.low, a.high + b.high}; 
    return result; 
}
d128_t d128_sub(d128_t a, d128_t b) { 
    d128_t result = {a.low - b.low, a.high - b.high}; 
    return result; 
}
d128_t d128_mul(d128_t a, d128_t b) { 
    d128_t result = {a.low * b.low, a.high * b.high}; 
    return result; 
}
d128_t d128_div(d128_t a, d128_t b) { 
    d128_t result = {a.low / b.low, a.high / b.high}; 
    return result; 
}
int32_t d128_cmp(d128_t a, d128_t b) { 
    if (a.high != b.high) return (a.high > b.high) - (a.high < b.high);
    return (a.low > b.low) - (a.low < b.low);
}
d128_t d128_from_string(const char* str) { 
    d128_t result = {(uint64_t)atoll(str), 0}; 
    return result; 
}
char* d128_to_string(d128_t val) {
    char* result = malloc(64);
    snprintf(result, 64, "%llu:%llu", val.high, val.low);
    return result;
}
#endif

// Add similar placeholder implementations for libbf and mafm if needed
#ifndef HAVE_LIBBF
// Simplified bf_number structure for placeholder
typedef struct {
    int64_t value;
} bf_number_placeholder_t;

typedef struct {
    int dummy;
} bf_context_placeholder_t;

void bf_context_init(bf_context_t* ctx, void* realloc_func, void* free_func) {
    // Placeholder implementation
}

void bf_context_end(bf_context_t* ctx) {
    // Placeholder implementation
}

void bf_init(bf_context_t* ctx, bf_number_t* r) {
    // Placeholder implementation
    ((bf_number_placeholder_t*)r)->value = 0;
}

void bf_delete(bf_number_t* r) {
    // Placeholder implementation
}

int32_t bf_set_si(bf_number_t* r, int64_t a) {
    ((bf_number_placeholder_t*)r)->value = a;
    return 0;
}

int32_t bf_set_ui(bf_number_t* r, uint64_t a) {
    ((bf_number_placeholder_t*)r)->value = (int64_t)a;
    return 0;
}

int32_t bf_add(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags) {
    ((bf_number_placeholder_t*)r)->value = 
        ((bf_number_placeholder_t*)a)->value + ((bf_number_placeholder_t*)b)->value;
    return 0;
}

int32_t bf_sub(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags) {
    ((bf_number_placeholder_t*)r)->value = 
        ((bf_number_placeholder_t*)a)->value - ((bf_number_placeholder_t*)b)->value;
    return 0;
}

int32_t bf_mul(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags) {
    ((bf_number_placeholder_t*)r)->value = 
        ((bf_number_placeholder_t*)a)->value * ((bf_number_placeholder_t*)b)->value;
    return 0;
}

int32_t bf_div(bf_number_t* r, bf_number_t* a, bf_number_t* b, uint64_t prec, uint32_t flags) {
    ((bf_number_placeholder_t*)r)->value = 
        ((bf_number_placeholder_t*)a)->value / ((bf_number_placeholder_t*)b)->value;
    return 0;
}

int32_t bf_cmp(bf_number_t* a, bf_number_t* b) {
    int64_t av = ((bf_number_placeholder_t*)a)->value;
    int64_t bv = ((bf_number_placeholder_t*)b)->value;
    return (av > bv) - (av < bv);
}

char* bf_ftoa(size_t* plen, bf_number_t* a, int32_t radix, uint64_t prec, uint32_t flags) {
    char* result = malloc(32);
    snprintf(result, 32, "%lld", ((bf_number_placeholder_t*)a)->value);
    if (plen) *plen = strlen(result);
    return result;
}
#endif

#ifndef HAVE_MAFM
// Simplified mafm implementations
typedef struct {
    double value;
} mafm_number_placeholder_t;

typedef struct {
    int precision;
} mafm_context_placeholder_t;

void mafm_context_init(mafm_context_t* ctx, int32_t precision) {
    ((mafm_context_placeholder_t*)ctx)->precision = precision;
}

void mafm_context_free(mafm_context_t* ctx) {
    // Placeholder
}

void mafm_init(mafm_number_t* num) {
    ((mafm_number_placeholder_t*)num)->value = 0.0;
}

void mafm_clear(mafm_number_t* num) {
    // Placeholder
}

int32_t mafm_set_str(mafm_number_t* num, const char* str, int32_t radix) {
    ((mafm_number_placeholder_t*)num)->value = atof(str);
    return 0;
}

char* mafm_get_str(mafm_number_t* num, int32_t radix) {
    char* result = malloc(32);
    snprintf(result, 32, "%.15g", ((mafm_number_placeholder_t*)num)->value);
    return result;
}

int32_t mafm_add(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx) {
    ((mafm_number_placeholder_t*)result)->value = 
        ((mafm_number_placeholder_t*)a)->value + ((mafm_number_placeholder_t*)b)->value;
    return 0;
}

int32_t mafm_sub(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx) {
    ((mafm_number_placeholder_t*)result)->value = 
        ((mafm_number_placeholder_t*)a)->value - ((mafm_number_placeholder_t*)b)->value;
    return 0;
}

int32_t mafm_mul(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx) {
    ((mafm_number_placeholder_t*)result)->value = 
        ((mafm_number_placeholder_t*)a)->value * ((mafm_number_placeholder_t*)b)->value;
    return 0;
}

int32_t mafm_div(mafm_number_t* result, mafm_number_t* a, mafm_number_t* b, mafm_context_t* ctx) {
    ((mafm_number_placeholder_t*)result)->value = 
        ((mafm_number_placeholder_t*)a)->value / ((mafm_number_placeholder_t*)b)->value;
    return 0;
}

int32_t mafm_cmp(mafm_number_t* a, mafm_number_t* b) {
    double av = ((mafm_number_placeholder_t*)a)->value;
    double bv = ((mafm_number_placeholder_t*)b)->value;
    return (av > bv) - (av < bv);
}

int32_t mafm_set_si(mafm_number_t* num, int64_t val) {
    ((mafm_number_placeholder_t*)num)->value = (double)val;
    return 0;
}

int32_t mafm_set_d(mafm_number_t* num, double val) {
    ((mafm_number_placeholder_t*)num)->value = val;
    return 0;
}

int64_t mafm_get_si(mafm_number_t* num) {
    return (int64_t)((mafm_number_placeholder_t*)num)->value;
}

double mafm_get_d(mafm_number_t* num) {
    return ((mafm_number_placeholder_t*)num)->value;
}
#endif