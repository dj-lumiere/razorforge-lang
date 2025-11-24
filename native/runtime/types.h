#pragma once
#include <stdint>

using rf_i8 = int8_t;
using rf_i16 = int16_t;
using rf_i32 = int32_t;
using rf_i64 = int64_t;
// llvm i128 to rf_i128
using rf_isys = intptr_t;

using rf_u8 = uint8_t;
using rf_u16 = uint16_t;
using rf_u32 = uint32_t;
using rf_u64 = uint64_t;
// llvm u128 to rf_u128
using rf_usys = uintptr_t;

// llvm half to rf_f16
using rf_f32 = float;
using rf_f64 = double;
// llvm float128 to rf_f128

using rf_bool = bool;
using rf_cptr = void*;
using rf_ccptr = const void*;
using rf_cshort = short;
using rf_cushort = unsigned short;
using rf_cint = int;
using rf_cuint = unsigned int;
using rf_clong = long;
using rf_culong = unsigned long;
using rf_cll = long long;
using rf_cull = unsigned long long;

using rf_cchar = char;
using rf_cstr = char*;
using rf_ccstr = const char*;
using rf_cwchar = wchar_t;
using rf_cwstr = wchar_t*;
using rf_ccwstr = const wchar_t*;
using rf_cchar16 = char16_t;
using rf_cchar16str = char16_t*;
using rf_ccchar16str = const char16_t*;
using rf_cchar32 = char32_t;
using rf_cchar32str = char32_t*;
using rf_ccchar32str = const char32_t*;

using rf_cvoid = void;
#define rf_null nullptr
#define rf_true true
#define rf_false false

using rf_MemorySlice = struct
{
    rf_usys len;
    rf_u8* data;
};

using rf_Variant = struct
{
    rf_u32 tag;
    rf_MemorySlice data;
};

using rf_Enum = rf_u32;
using rf_None = struct
{
};
using rf_FnPtr = void*;

using suflae_Object = void*;
