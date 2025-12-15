# RazorForge Compiler Intrinsics API

## Overview

Compiler intrinsics are special functions prefixed with `@intrinsic.` that map directly to low-level operations. They
are used exclusively in the standard library to implement primitive operations, unsafe memory access, and type
conversions.

**Key Principles:**

- Intrinsics are **only available in `danger!` blocks**
- They map directly to LLVM IR instructions (and can be ported to other backends)
- They are **not callable from user code** - only stdlib can use them
- Type parameters (e.g., `<T>`) are substituted at compile time

---

## Memory Intrinsics

Used by `Snatched<T>` and unsafe memory operations.

### `@intrinsic.load<T>(addr: uaddr) -> T`

Load a value of type `T` from memory address.

**LLVM IR:**

```llvm
%ptr = inttoptr i64 %addr to ptr
%result = load {T}, ptr %ptr
```

**Example:**

```razorforge
routine Snatched<T>.read() -> T {
    danger! {
        return @intrinsic.load<T>(me.address)
    }
}
```

### `@intrinsic.store<T>(addr: uaddr, value: T)`

Store a value of type `T` to memory address.

**LLVM IR:**

```llvm
%ptr = inttoptr i64 %addr to ptr
store {T} %value, ptr %ptr
```

**Example:**

```razorforge
routine Snatched<T>.write(value: T) {
    danger! {
        @intrinsic.store<T>(me.address, value)
    }
}
```

### `@intrinsic.volatile_load<T>(addr: uaddr) -> T`

Volatile load - prevents compiler optimizations from eliminating/reordering.

**LLVM IR:**

```llvm
%ptr = inttoptr i64 %addr to ptr
%result = load volatile {T}, ptr %ptr
```

**Example:**

```razorforge
routine Snatched<T>.volatile_read() -> T {
    danger! {
        return @intrinsic.volatile_load<T>(me.address)
    }
}
```

### `@intrinsic.volatile_store<T>(addr: uaddr, value: T)`

Volatile store - prevents compiler optimizations.

**LLVM IR:**

```llvm
%ptr = inttoptr i64 %addr to ptr
store volatile {T} %value, ptr %ptr
```

### `@intrinsic.bitcast<T, U>(value: T) -> U`

Reinterpret the bits of value as type `U` (type punning).

**LLVM IR:**

```llvm
%src_ptr = alloca {T}
store {T} %value, ptr %src_ptr
%dest_ptr = bitcast ptr %src_ptr to ptr
%result = load {U}, ptr %dest_ptr
```

**Example:**

```razorforge
routine T.reveal_as<U>() -> U {
    danger! {
        return @intrinsic.bitcast<T, U>(me)
    }
}
```

### `@intrinsic.invalidate(addr: uaddr)`

Free/deallocate memory at address.

**LLVM IR:**

```llvm
%ptr = inttoptr i64 %addr to ptr
call void @free(ptr %ptr)
```

**Example:**

```razorforge
routine Snatched<T>.invalidate!() {
    danger! {
        @intrinsic.invalidate(me.address)
    }
}
```

---

## Arithmetic Intrinsics

### Wrapping Arithmetic (No Overflow Checks)

Default behavior for normal arithmetic operators.

#### `@intrinsic.add.wrapping<T>(a: T, b: T) -> T`

Wrapping addition (overflows wrap around).

**LLVM IR:** `add {T} %a, %b`

**Example:**

```razorforge
routine s32.__add__(other: s32) -> s32 {
    danger! {
        return @intrinsic.add.wrapping<i32>(me, other)
    }
}
```

#### `@intrinsic.sub.wrapping<T>(a: T, b: T) -> T`

**LLVM IR:** `sub {T} %a, %b`

#### `@intrinsic.mul.wrapping<T>(a: T, b: T) -> T`

**LLVM IR:** `mul {T} %a, %b`

#### `@intrinsic.div.wrapping<T>(a: T, b: T) -> T`

**LLVM IR:** `sdiv {T} %a, %b` (signed) or `udiv {T} %a, %b` (unsigned)

#### `@intrinsic.rem.wrapping<T>(a: T, b: T) -> T`

**LLVM IR:** `srem {T} %a, %b` (signed) or `urem {T} %a, %b` (unsigned)

### Overflow-Checking Arithmetic

Returns `(result, overflow_occurred)` tuple.

#### `@intrinsic.add.overflow<T>(a: T, b: T) -> (T, bool)`

Addition with overflow detection.

**LLVM IR:**

```llvm
%result_struct = call { {T}, i1 } @llvm.sadd.with.overflow.{T}({T} %a, {T} %b)
%result = extractvalue { {T}, i1 } %result_struct, 0
%overflow = extractvalue { {T}, i1 } %result_struct, 1
```

**Example:**

```razorforge
routine s32.checked_add(other: s32) -> Result<s32, OverflowError> {
    danger! {
        let (result, overflow) = @intrinsic.add.overflow<i32>(me, other)
        return when overflow {
            true => Err(OverflowError()),
            false => Ok(result)
        }
    }
}
```

#### `@intrinsic.sub.overflow<T>(a: T, b: T) -> (T, bool)`

**LLVM IR:** `@llvm.ssub.with.overflow` or `@llvm.usub.with.overflow`

#### `@intrinsic.mul.overflow<T>(a: T, b: T) -> (T, bool)`

**LLVM IR:** `@llvm.smul.with.overflow` or `@llvm.umul.with.overflow`

### Saturating Arithmetic

Clamps to min/max on overflow instead of wrapping.

#### `@intrinsic.add.saturating<T>(a: T, b: T) -> T`

**LLVM IR:** `@llvm.sadd.sat` or `@llvm.uadd.sat`

**Example:**

```razorforge
routine u8.saturating_add(other: u8) -> u8 {
    danger! {
        return @intrinsic.add.saturating<i8>(me, other)
    }
}
```

#### `@intrinsic.sub.saturating<T>(a: T, b: T) -> T`

**LLVM IR:** `@llvm.ssub.sat` or `@llvm.usub.sat`

---

## Comparison Intrinsics

### Integer Comparisons

#### `@intrinsic.icmp.eq<T>(a: T, b: T) -> bool`

Integer equality comparison.

**LLVM IR:** `icmp eq {T} %a, %b`

#### `@intrinsic.icmp.ne<T>(a: T, b: T) -> bool`

Integer inequality.

**LLVM IR:** `icmp ne {T} %a, %b`

#### `@intrinsic.icmp.slt<T>(a: T, b: T) -> bool`

Signed less than.

**LLVM IR:** `icmp slt {T} %a, %b`

#### `@intrinsic.icmp.ult<T>(a: T, b: T) -> bool`

Unsigned less than.

**LLVM IR:** `icmp ult {T} %a, %b`

#### `@intrinsic.icmp.sle<T>(a: T, b: T) -> bool`

Signed less than or equal.

**LLVM IR:** `icmp sle {T} %a, %b`

#### `@intrinsic.icmp.ule<T>(a: T, b: T) -> bool`

Unsigned less than or equal.

**LLVM IR:** `icmp ule {T} %a, %b`

#### `@intrinsic.icmp.sgt<T>(a: T, b: T) -> bool`

Signed greater than.

**LLVM IR:** `icmp sgt {T} %a, %b`

#### `@intrinsic.icmp.ugt<T>(a: T, b: T) -> bool`

Unsigned greater than.

**LLVM IR:** `icmp ugt {T} %a, %b`

#### `@intrinsic.icmp.sge<T>(a: T, b: T) -> bool`

Signed greater than or equal.

**LLVM IR:** `icmp sge {T} %a, %b`

#### `@intrinsic.icmp.uge<T>(a: T, b: T) -> bool`

Unsigned greater than or equal.

**LLVM IR:** `icmp uge {T} %a, %b`

### Float Comparisons

#### `@intrinsic.fcmp.oeq<T>(a: T, b: T) -> bool`

Ordered float equality (false if NaN).

**LLVM IR:** `fcmp oeq {T} %a, %b`

#### `@intrinsic.fcmp.one<T>(a: T, b: T) -> bool`

Ordered float inequality.

**LLVM IR:** `fcmp one {T} %a, %b`

#### `@intrinsic.fcmp.olt<T>(a: T, b: T) -> bool`

Ordered less than.

**LLVM IR:** `fcmp olt {T} %a, %b`

#### `@intrinsic.fcmp.ole<T>(a: T, b: T) -> bool`

Ordered less than or equal.

**LLVM IR:** `fcmp ole {T} %a, %b`

#### `@intrinsic.fcmp.ogt<T>(a: T, b: T) -> bool`

Ordered greater than.

**LLVM IR:** `fcmp ogt {T} %a, %b`

#### `@intrinsic.fcmp.oge<T>(a: T, b: T) -> bool`

Ordered greater than or equal.

**LLVM IR:** `fcmp oge {T} %a, %b`

#### `@intrinsic.fcmp.ueq<T>(a: T, b: T) -> bool`

Unordered float equality (true if either is NaN).

**LLVM IR:** `fcmp ueq {T} %a, %b`

---

## Bitwise Intrinsics

#### `@intrinsic.and<T>(a: T, b: T) -> T`

Bitwise AND.

**LLVM IR:** `and {T} %a, %b`

#### `@intrinsic.or<T>(a: T, b: T) -> T`

Bitwise OR.

**LLVM IR:** `or {T} %a, %b`

#### `@intrinsic.xor<T>(a: T, b: T) -> T`

Bitwise XOR.

**LLVM IR:** `xor {T} %a, %b`

#### `@intrinsic.not<T>(value: T) -> T`

Bitwise NOT (flip all bits).

**LLVM IR:** `xor {T} %value, -1`

#### `@intrinsic.shl<T>(value: T, amount: u32) -> T`

Shift left logical.

**LLVM IR:** `shl {T} %value, %amount`

#### `@intrinsic.lshr<T>(value: T, amount: u32) -> T`

Shift right logical (zero-fill).

**LLVM IR:** `lshr {T} %value, %amount`

#### `@intrinsic.ashr<T>(value: T, amount: u32) -> T`

Shift right arithmetic (sign-extend).

**LLVM IR:** `ashr {T} %value, %amount`

---

## Type Conversion Intrinsics

### Integer Conversions

#### `@intrinsic.trunc<From, To>(value: From) -> To`

Truncate integer to smaller size.

**LLVM IR:** `trunc {From} %value to {To}`

**Example:**

```razorforge
routine s64.to_s32() -> s32 {
    danger! {
        return @intrinsic.trunc<i64, i32>(me)
    }
}
```

#### `@intrinsic.zext<From, To>(value: From) -> To`

Zero-extend integer to larger size.

**LLVM IR:** `zext {From} %value to {To}`

**Example:**

```razorforge
routine u32.to_u64() -> u64 {
    danger! {
        return @intrinsic.zext<i32, i64>(me)
    }
}
```

#### `@intrinsic.sext<From, To>(value: From) -> To`

Sign-extend integer to larger size.

**LLVM IR:** `sext {From} %value to {To}`

**Example:**

```razorforge
routine s32.to_s64() -> s64 {
    danger! {
        return @intrinsic.sext<i32, i64>(me)
    }
}
```

### Float Conversions

#### `@intrinsic.fptrunc<From, To>(value: From) -> To`

Truncate float to smaller precision.

**LLVM IR:** `fptrunc {From} %value to {To}`

**Example:**

```razorforge
routine f64.to_f32() -> f32 {
    danger! {
        return @intrinsic.fptrunc<double, float>(me)
    }
}
```

#### `@intrinsic.fpext<From, To>(value: From) -> To`

Extend float to larger precision.

**LLVM IR:** `fpext {From} %value to {To}`

**Example:**

```razorforge
routine f32.to_f64() -> f64 {
    danger! {
        return @intrinsic.fpext<float, double>(me)
    }
}
```

### Float ↔ Integer Conversions

#### `@intrinsic.fptoui<From, To>(value: From) -> To`

Float to unsigned integer.

**LLVM IR:** `fptoui {From} %value to {To}`

#### `@intrinsic.fptosi<From, To>(value: From) -> To`

Float to signed integer.

**LLVM IR:** `fptosi {From} %value to {To}`

**Example:**

```razorforge
routine f32.to_s32() -> s32 {
    danger! {
        return @intrinsic.fptosi<float, i32>(me)
    }
}
```

#### `@intrinsic.uitofp<From, To>(value: From) -> To`

Unsigned integer to float.

**LLVM IR:** `uitofp {From} %value to {To}`

#### `@intrinsic.sitofp<From, To>(value: From) -> To`

Signed integer to float.

**LLVM IR:** `sitofp {From} %value to {To}`

**Example:**

```razorforge
routine s32.to_f32() -> f32 {
    danger! {
        return @intrinsic.sitofp<i32, float>(me)
    }
}
```

---

## Math Intrinsics

### Basic Math

#### `@intrinsic.sqrt<T>(value: T) -> T`

Square root (floating-point only).

**LLVM IR:** `call {T} @llvm.sqrt.{T}({T} %value)`

#### `@intrinsic.abs<T>(value: T) -> T`

Absolute value.

**LLVM IR:** `call {T} @llvm.abs.{T}({T} %value, i1 false)`

#### `@intrinsic.fabs<T>(value: T) -> T`

Floating-point absolute value.

**LLVM IR:** `call {T} @llvm.fabs.{T}({T} %value)`

#### `@intrinsic.copysign<T>(mag: T, sign: T) -> T`

Copy sign bit from `sign` to `mag`.

**LLVM IR:** `call {T} @llvm.copysign.{T}({T} %mag, {T} %sign)`

#### `@intrinsic.floor<T>(value: T) -> T`

Floor function (round down).

**LLVM IR:** `call {T} @llvm.floor.{T}({T} %value)`

#### `@intrinsic.ceil<T>(value: T) -> T`

Ceiling function (round up).

**LLVM IR:** `call {T} @llvm.ceil.{T}({T} %value)`

#### `@intrinsic.trunc_float<T>(value: T) -> T`

Truncate to integer (round toward zero).

**LLVM IR:** `call {T} @llvm.trunc.{T}({T} %value)`

#### `@intrinsic.round<T>(value: T) -> T`

Round to nearest integer.

**LLVM IR:** `call {T} @llvm.round.{T}({T} %value)`

### Advanced Math

#### `@intrinsic.pow<T>(base: T, exp: T) -> T`

Power function.

**LLVM IR:** `call {T} @llvm.pow.{T}({T} %base, {T} %exp)`

#### `@intrinsic.exp<T>(value: T) -> T`

Exponential (e^x).

**LLVM IR:** `call {T} @llvm.exp.{T}({T} %value)`

#### `@intrinsic.log<T>(value: T) -> T`

Natural logarithm.

**LLVM IR:** `call {T} @llvm.log.{T}({T} %value)`

#### `@intrinsic.log10<T>(value: T) -> T`

Base-10 logarithm.

**LLVM IR:** `call {T} @llvm.log10.{T}({T} %value)`

#### `@intrinsic.sin<T>(value: T) -> T`

Sine function.

**LLVM IR:** `call {T} @llvm.sin.{T}({T} %value)`

#### `@intrinsic.cos<T>(value: T) -> T`

Cosine function.

**LLVM IR:** `call {T} @llvm.cos.{T}({T} %value)`

---

## Bit Manipulation Intrinsics

#### `@intrinsic.ctpop<T>(value: T) -> T`

Count population (number of 1 bits).

**LLVM IR:** `call {T} @llvm.ctpop.{T}({T} %value)`

#### `@intrinsic.ctlz<T>(value: T) -> T`

Count leading zeros.

**LLVM IR:** `call {T} @llvm.ctlz.{T}({T} %value, i1 false)`

#### `@intrinsic.cttz<T>(value: T) -> T`

Count trailing zeros.

**LLVM IR:** `call {T} @llvm.cttz.{T}({T} %value, i1 false)`

#### `@intrinsic.bswap<T>(value: T) -> T`

Byte swap (reverse byte order).

**LLVM IR:** `call {T} @llvm.bswap.{T}({T} %value)`

#### `@intrinsic.bitreverse<T>(value: T) -> T`

Reverse all bits.

**LLVM IR:** `call {T} @llvm.bitreverse.{T}({T} %value)`

---

## Atomic Intrinsics

For thread-safe operations without locks.

#### `@intrinsic.atomic.load<T>(addr: uaddr) -> T`

Atomic load with sequentially consistent ordering.

**LLVM IR:** `load atomic {T}, ptr %addr seq_cst`

#### `@intrinsic.atomic.store<T>(addr: uaddr, value: T)`

Atomic store.

**LLVM IR:** `store atomic {T} %value, ptr %addr seq_cst`

#### `@intrinsic.atomic.add<T>(addr: uaddr, value: T) -> T`

Atomic fetch-and-add (returns old value).

**LLVM IR:** `atomicrmw add ptr %addr, {T} %value seq_cst`

#### `@intrinsic.atomic.sub<T>(addr: uaddr, value: T) -> T`

Atomic fetch-and-subtract.

**LLVM IR:** `atomicrmw sub ptr %addr, {T} %value seq_cst`

#### `@intrinsic.atomic.xchg<T>(addr: uaddr, value: T) -> T`

Atomic exchange (swap).

**LLVM IR:** `atomicrmw xchg ptr %addr, {T} %value seq_cst`

#### `@intrinsic.atomic.cmpxchg<T>(addr: uaddr, expected: T, desired: T) -> (T, bool)`

Compare-and-swap. Returns `(old_value, success)`.

**LLVM IR:**

```llvm
%result = cmpxchg ptr %addr, {T} %expected, {T} %desired seq_cst seq_cst
%old_value = extractvalue { {T}, i1 } %result, 0
%success = extractvalue { {T}, i1 } %result, 1
```

---

## Type Mapping Reference

### RazorForge → LLVM Type Mapping

| RazorForge | LLVM IR  | Notes                              |
|------------|----------|------------------------------------|
| `s8`       | `i8`     | 8-bit signed                       |
| `s16`      | `i16`    | 16-bit signed                      |
| `s32`      | `i32`    | 32-bit signed                      |
| `s64`      | `i64`    | 64-bit signed                      |
| `s128`     | `i128`   | 128-bit signed                     |
| `u8`       | `i8`     | 8-bit unsigned                     |
| `u16`      | `i16`    | 16-bit unsigned                    |
| `u32`      | `i32`    | 32-bit unsigned                    |
| `u64`      | `i64`    | 64-bit unsigned                    |
| `u128`     | `i128`   | 128-bit unsigned                   |
| `f16`      | `half`   | 16-bit float                       |
| `f32`      | `float`  | 32-bit float                       |
| `f64`      | `double` | 64-bit float                       |
| `f128`     | `fp128`  | 128-bit float                      |
| `uaddr`    | `i64`    | Pointer-sized (platform-dependent) |
| `sysint`   | `i64`    | Pointer-sized (platform-dependent) |
| `bool`     | `i1`     | 1-bit boolean                      |

---

## Implementation Notes

### Compiler Implementation

1. **Lexer:** Recognize `@intrinsic` as special token
2. **Parser:** Parse `@intrinsic.operation<T>(args)` as IntrinsicCallExpression
3. **Semantic Analyzer:** Validate intrinsics only used in `danger!` blocks
4. **Code Generator:** Map each intrinsic to corresponding LLVM IR

### Example Codegen Structure

```csharp
public string VisitIntrinsicCallExpression(IntrinsicCallExpression node)
{
    string resultTemp = GetNextTemp();
    string intrinsicName = node.IntrinsicName;  // e.g., "add.wrapping"

    // Dispatch to specific handler
    return intrinsicName switch
    {
        "add.wrapping" => EmitAddWrapping(node, resultTemp),
        "load" => EmitLoad(node, resultTemp),
        "icmp.slt" => EmitIntCompare(node, "slt", resultTemp),
        // ... etc
    };
}
```

### Safety Rules

1. Intrinsics can ONLY be called from within `danger!` blocks
2. Type parameters must be statically known at compile time
3. Memory intrinsics bypass all safety checks
4. Incorrect use leads to undefined behavior

---

## Summary

This intrinsics API provides:

- ✅ **Memory operations** - Load, store, bitcast, invalidate
- ✅ **Arithmetic** - Wrapping, overflow-checked, saturating
- ✅ **Comparisons** - Signed/unsigned, ordered/unordered floats
- ✅ **Bitwise** - AND, OR, XOR, shifts, rotates
- ✅ **Type conversions** - All integer and float conversions
- ✅ **Math** - Square root, trig, exponentials
- ✅ **Atomics** - Lock-free thread-safe operations

Total: ~80 intrinsics covering all low-level operations needed for stdlib implementation.
