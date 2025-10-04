#pragma once
#include <stdint>

typedef int8_t rf_i8;
typedef int16_t rf_i16;
typedef int32_t rf_i32;
typedef int64_t rf_i64;
// llvm i128 to rf_i128
typedef intptr_t rf_isys;

typedef uint8_t rf_u8;
typedef uint16_t rf_u16;
typedef uint32_t rf_u32;
typedef uint64_t rf_u64;
// llvm u128 to rf_u128
typedef uintptr_t rf_usys;

// llvm half to rf_f16
typedef float rf_f32;
typedef double rf_f64;
// llvm float128 to rf_f128

typedef bool rf_bool;
typedef void* rf_cptr;
typedef const void* rf_ccptr;
typedef short rf_cshort;
typedef unsigned short rf_cushort;
typedef int rf_cint;
typedef unsigned int rf_cuint;
typedef long rf_clong;
typedef unsigned long rf_culong;
typedef long long rf_cll;
typedef unsigned long long rf_cull;

typedef char rf_cchar;
typedef char* rf_cstr;
typedef const char* rf_ccstr;
typedef wchar_t rf_cwchar;
typedef wchar_t* rf_cwstr;
typedef const wchar_t* rf_ccwstr;
typedef char16_t rf_cchar16;
typedef char16_t* rf_cchar16str;
typedef const char16_t* rf_ccchar16str;
typedef char32_t rf_cchar32;
typedef char32_t* rf_cchar32str;
typedef const char32_t* rf_ccchar32str;

typedef void rf_cvoid;
#define rf_null nullptr
#define rf_true true
#define rf_false false

typedef struct
{
    rf_usys len;
    rf_u8* data;
} rf_MemorySlice;

typedef struct
{
    rf_u32 tag;
    rf_MemorySlice data;
} rf_Variant;

typedef rf_u32 rf_Enum;
typedef struct{} rf_None;
typedef void* rf_FnPtr;

typedef void* cake_Object;
