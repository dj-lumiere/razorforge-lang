#pragma once
#include <stdint.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <wchar.h>

// ==========================================
// 1. RazorForge Primitives (Fixed Width)
// ==========================================
// These MUST match exact bit widths.
typedef int8_t rf_s8;
typedef int16_t rf_s16;
typedef int32_t rf_s32;
typedef int64_t rf_s64;
// typedef __int128_t rf_s128; // Uncomment if compiler supports it

typedef uint8_t rf_u8;
typedef uint16_t rf_u16;
typedef uint32_t rf_u32;
typedef uint64_t rf_u64;
// typedef __uint128_t rf_u128; // Uncomment if compiler supports it

// ==========================================
// 2. System & Data Types
// ==========================================
typedef intptr_t rf_saddr;   // Signed Pointer-Sized
typedef uintptr_t rf_uaddr;  // Unsigned Pointer-Sized

typedef uint8_t sf_byte;      // 'byte' is raw data (u8)
typedef uint32_t sf_letter;   // 'letter' is Unicode Codepoint
typedef uint32_t rf_letter32; // 'letter32' is Unicode Codepoint
typedef uint16_t rf_letter16; // 'letter16' is Unicode Codepoint
typedef uint8_t rf_letter8;   // 'letter8' is UTF-8 unit

typedef bool rf_bool;         // Standard boolean

// ==========================================
// 3. C Interop Types (Platform Dependent)
// ==========================================
// These map to the C compiler's native types.
typedef void rf_cvoid;
#define rf_cnullptr    NULL

typedef char rf_cchar;              // Ambiguous sign
typedef signed char rf_cschar;      // Explicit signed
typedef unsigned char rf_cuchar;    // Explicit unsigned
typedef char* rf_cstr;
typedef short rf_cshort;
typedef unsigned short rf_cushort;

typedef int rf_cint;
typedef unsigned int rf_cuint;

typedef long rf_clong;              // 32-bit on Win64, 64-bit on Linux64
typedef unsigned long rf_culong;

typedef long long rf_clonglong;
typedef unsigned long long rf_culonglong;

typedef float rf_cfloat;
typedef double rf_cdouble;
typedef wchar_t rf_cwchar;

// ==========================================
// 4. The Raw Pointer Wrapper
// ==========================================
// Since Snatched<T> is just a raw pointer at runtime:
#define rf_Snatched(T) T*

// DynamicSlice - heap-allocated memory with address and size
typedef struct rf_DynamicSlice {
    rf_uaddr starting_address;
    rf_uaddr allocated_bytes;
} rf_DynamicSlice;

// List<T> / Text<letter8> structure
// Text<letter8> is a thin wrapper for List<letter8>
typedef struct rf_List {
    rf_DynamicSlice data;
    rf_u64 count;
    rf_u64 capacity;
} rf_List;

// Alias for Text<letter8>
typedef rf_List rf_Text8;

// Variant (tagged union)
typedef struct rf_Variant {
    rf_u32 tag;
    rf_DynamicSlice data;
} rf_Variant;

// Other types
typedef rf_u32 rf_Enum;
typedef void* rf_FnPtr;

// Note: None is NOT a runtime type. In RazorForge, None represents the absence
// of a value and is encoded via discriminant fields (is_valid, state) in wrapper
// types like Maybe<T>, Result<T>, and Lookup<T>. There is no rf_None type.
typedef void* suflae_Object;
