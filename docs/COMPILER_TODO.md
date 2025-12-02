# Compiler Features TODO for Standard Library Support

This document tracks compiler features needed for full RazorForge standard library support.

## Current Status

### ✅ Working Features

- Entry point: `routine main()` returns `Blank` (no return type, no return statement needed) - compiler automatically
  generates `i32` return for C ABI compatibility
- Basic function declarations and calls
- Primitive types (s8, s16, s32, s64, s128, u8, u16, u32, u64, u128, f16, f32, f64, f128, bool, etc.)
- Duration literals (5w, 3d, 2h, 30m, 45s, 100ms, 500us, 1000ns)
- Memory size literals (1024b, 64kb, 1mb, 2gb, 3tib, etc.) - including `0b` disambiguation (0 bytes vs binary)
- Arithmetic and comparison operators
- If statements with proper Bool type checking
- Loop statements (while, for, loop with break)
- Local variables (let, var)
- LLVM IR code generation
- Import statement parsing (syntax recognized)
- Generic function monomorphization (`identity<s32>(42)` works)
- Nested generic type parsing (`List<List<s32>>`, `List<Text<letter8>>`)
- **Namespace-qualified generic methods**: `Console.show<T>(value)` syntax
- **Generic methods on generic types**: `List<T>.select<U>(mapper)` syntax
- **Tuple types in function signatures**: `Routine<(T), R>` for lambda parameter types
- **Block-style conditional expressions**: `if cond { expr } else { expr }`
- Arrow lambda parsing (`x => expr`, `(x, y) => expr`, `() => expr`)
- Lambda expression AST nodes
- Console I/O (`show`, `ask`) via C runtime (printf, scanf, strtol, strtod)
- Target platform detection (Windows/Linux/macOS)
- Function variant generation (`try_`, `check_`, `find_` from `!` functions)
- Failable function support (`!` suffix with `throw` and `absent` keywords)
- **Intrinsics system** (`@intrinsic.*` for LLVM operations)
- **`@crash_only` attribute** - prevents generation of safe variants
- **`danger!` blocks** - for unsafe/native operations
- **Error type hierarchy** (Error, Crashable, specific error types)
- **Protocol-based type system** - types defined by protocols they implement, not name patterns

### ⏳ Partially Implemented

- Generic types (functions work, entities/records need work)
- Module imports (parsed but not resolved)
- Intrinsic operations (arithmetic, bitwise, shift - need more validation)
- **Generic type instantiation** - types are now instantiated immediately when referenced (fixed ordering issue)
- **Nested generic mangling** - `Range<BackIndex<uaddr>>` → `Range_BackIndex_uaddr` (fixed)

### ❌ Missing Features Required for Stdlib

- Generic entity/record support
- Module/import system resolution
- Lambda code generation
- Auto-generated methods for data types (record, entity, choice, resident, variant)
- **`preset` keyword** - compile-time constants
- **`follows` keyword** - protocol implementation syntax
- **Generic constraints** - `<T: Protocol>` or `where T: T follows Protocol` syntax
- **Protocol declarations** - `protocol Name { ... }`
- **`common` keyword** - static/class-level methods
- **`external` declarations** - for FFI function declarations
- **Extension method syntax** - `routine Type.method_name()` outside type declaration
- **Method-chain style constructors** - allow calling constructors via method chain syntax (see below)

---

## 1. Generic Entity/Record Support

**Priority: HIGH** - Required for `List<T>`, `Text<T>`, and all collection types

### What's Needed:

Generic functions already work via monomorphization. Need same for entities/records:

```razorforge
# These need to work:
entity List<T> {
    private data: DynamicSlice
    private len: u64
}

record ListIterator<T> {
    list: List<T>
    index: u64
}

let list: List<s32> = List<s32>()
```

### Implementation Approach:

1. Parse generic entity/record declarations (may already work)
2. Track generic type definitions in symbol table
3. Monomorphize when concrete type is used: `List<s32>` → `List_s32`
4. Generate separate struct for each instantiation

---

## 2. Module/Import System Resolution

**Priority: HIGH** - Required for any stdlib usage

### Design Decisions (Finalized)

1. **Search Order**: `builtin/` → project root → external packages

2. **What's Imported**: The entity/record and its public API

3. **Access Style**: Unqualified - use `List<s32>` directly
    - **Name collisions**: Compiler error requiring alias

4. **Selective Imports**: `import Collections/{List, Dict}`

5. **Loading Strategy**: Eager loading - parse all transitive imports before semantic analysis

6. **Transitive Visibility**: None - require explicit imports

7. **Namespace Resolution**:
    - `namespace` declaration takes priority over folder path
    - Folder structure is default when no `namespace` declared

### Syntax

```razorforge
import Collections/List           # Basic import
import Collections/List as L      # With alias
import Collections/{List, Dict}   # Selective imports
namespace MyNamespace             # Declare namespace in source file
```

### Implementation Tasks

1. **ModuleResolver** – Resolve import paths to file paths
    - Search order: `builtin/` → project root → external packages
    - Handle namespace declarations overriding folder paths
    - Build namespace registry as files are parsed

2. **ModuleCache** – Track loaded modules to avoid re-parsing

3. **Namespace Parsing** – Parse `namespace` keyword in source files

4. **Symbol Table Population** – Add imported public symbols to scope

5. **Collision Detection** – Error on duplicate symbol names

6. **Transitive Import Loading** – Eagerly load all dependencies

7. **Circular Import Detection** – Track import stack

---

## 3. Function Type Syntax

**Priority: HIGH** - Required for itertools/callbacks ✅ **PARSING IMPLEMENTED**

### Idiomatic Syntax:

```razorforge
# Function type syntax: Routine<(ParamTypes), ReturnType>
mapper: Routine<(T), U>           # Single param
predicate: Routine<(T), bool>     # Returns bool
folder: Routine<(U, T), U>        # Two params
action: Routine<(T), void>        # No return value
```

### Parser Status:

- ✅ Parse `Routine<(T, U), R>` as a type expression
- ✅ Handle tuple syntax `(T, U)` for parameter types
- ✅ Support in function parameter types
- ⏳ Semantic validation of Routine types
- ⏳ Code generation for function pointers

---

## 4. Intrinsic Functions

**Priority: HIGH** - Required for stdlib implementation ✅ **MOSTLY IMPLEMENTED**

### Implemented Intrinsics:

```razorforge
# Arithmetic (standard, wrapping, saturating, overflow-checked)
@intrinsic.add<T>(a, b)           # Standard add
@intrinsic.add.wrapping<T>(a, b)  # Wrapping add
@intrinsic.add.saturating<T>(a, b) # Saturating add
@intrinsic.add.overflow<T>(a, b)  # Returns (result, overflow_flag)
# Same patterns for: sub, mul

# Division (signed/unsigned)
@intrinsic.sdiv<T>(a, b)          # Signed division
@intrinsic.udiv<T>(a, b)          # Unsigned division
@intrinsic.srem<T>(a, b)          # Signed remainder
@intrinsic.urem<T>(a, b)          # Unsigned remainder

# Bitwise operations
@intrinsic.and<T>(a, b)
@intrinsic.or<T>(a, b)
@intrinsic.xor<T>(a, b)
@intrinsic.not<T>(a)

# Shift operations
@intrinsic.shl<T>(value, bits)    # Shift left
@intrinsic.ashr<T>(value, bits)   # Arithmetic shift right (sign-preserving)
@intrinsic.lshr<T>(value, bits)   # Logical shift right (zero-fill)

# Comparison (signed/unsigned)
@intrinsic.icmp.eq<T>(a, b)       # Equal
@intrinsic.icmp.ne<T>(a, b)       # Not equal
@intrinsic.icmp.slt<T>(a, b)      # Signed less than
@intrinsic.icmp.sle<T>(a, b)      # Signed less or equal
@intrinsic.icmp.sgt<T>(a, b)      # Signed greater than
@intrinsic.icmp.sge<T>(a, b)      # Signed greater or equal
@intrinsic.icmp.ult<T>(a, b)      # Unsigned less than
@intrinsic.icmp.ule<T>(a, b)      # Unsigned less or equal
@intrinsic.icmp.ugt<T>(a, b)      # Unsigned greater than
@intrinsic.icmp.uge<T>(a, b)      # Unsigned greater or equal

# Bit manipulation
@intrinsic.ctpop<T>(value)        # Count ones (population count)
@intrinsic.ctlz<T>(value)         # Count leading zeros
@intrinsic.cttz<T>(value)         # Count trailing zeros
@intrinsic.bitreverse<T>(value)   # Reverse bits
@intrinsic.bswap<T>(value)        # Byte swap

# Type conversions
@intrinsic.sext<From, To>(value)  # Sign extend
@intrinsic.zext<From, To>(value)  # Zero extend
@intrinsic.trunc<From, To>(value) # Truncate
@intrinsic.bitcast<From, To>(value) # Bitcast (same size)
@intrinsic.sitofp<Int, Float>(value) # Signed int to float
@intrinsic.uitofp<Int, Float>(value) # Unsigned int to float
@intrinsic.fptosi<Float, Int>(value) # Float to signed int
@intrinsic.fptoui<Float, Int>(value) # Float to unsigned int

# Math
@intrinsic.abs<T>(value)          # Absolute value
@intrinsic.neg<T>(value)          # Negation
```

### Still Needed:

```razorforge
sizeof<T>() -> u64                # Size of type T in bytes
alignof<T>() -> u64               # Alignment of type T
```

### Already Have (external C):

```razorforge
external("C") routine rf_memory_alloc(bytes: uaddr) -> uaddr
external("C") routine rf_memory_free(address: uaddr)
external("C") routine rf_memory_copy(src: uaddr, dest: uaddr, bytes: uaddr)
```

---

## 5. Lambda Code Generation

**Priority: MEDIUM** - Needed for itertools to execute

### Current State:

- Lambda parsing works ✅
- AST nodes created ✅
- Code generation incomplete ❌

### What's Needed:

```razorforge
# This parses but doesn't generate correct LLVM IR:
let double = x => x * 2
let result = list.select(x => x * 2)
```

### Implementation:

1. Generate function pointer type for lambda
2. Create anonymous function in LLVM IR
3. Handle captures (closure semantics per memory model)

---

## 6. Type System Enhancements

**Priority: MEDIUM**

### 6.1 Option Types

```razorforge
routine next() -> T? {      # Optional return type
    return none
    return some(value)
}
```

### 6.2 Type Inference Improvements

```razorforge
let list = List<s32>()     # Infer type from constructor
```

---

## 7. Method-Chain Style Constructors

**Priority: LOW** - Nice to have for ergonomic type conversions

### Description

Allow type constructors with a single argument (or with `forward` keyword for the first parameter) to be called using
method-chain syntax on the value being converted.

### Syntax Examples

```razorforge
# Traditional constructor declaration with single argument
routine s32.__create__!(from_text: Text<letter32>) -> s32

# Can be called as method chain:
let x = "42".s32!()  # Equivalent to: s32!("42")

# Constructor with forward keyword for first parameter
routine s32.__create__!(forward from_text: Text<letter32>, base: s32 = 10) -> s32

# Can be called as method chain with additional arguments:
let x = "42".s32!(base: 16)  # Equivalent to: s32!("42", base: 16)
```

### Implementation Notes

1. When parsing `expr.TypeName!(args)`, check if `TypeName` has a `__create__!` constructor
2. If the constructor has:
    - Exactly one parameter, OR
    - First parameter marked `forward`
      Then allow the method-chain syntax
3. Transform `expr.TypeName!(args)` into `TypeName.__create__!(expr, args)`
4. This enables fluent conversion chains like `"42".s32!().f64!()`

### Benefits

- More natural for conversions: `value.TargetType!()` reads better than `TargetType!(value)`
- Enables method chaining for sequential conversions
- Consistent with other method-chain operations

---

## Implementation Priority

### Phase 1: Core Generics (Current Focus)

1. ✅ Generic function monomorphization
2. ✅ Nested generic type parsing (`>>` fix)
3. ✅ `Routine<(T), U>` type parsing (tuple types)
4. ✅ `Console.show<T>` namespace-qualified generic methods
5. ✅ `List<T>.select<U>` generic methods on generic types
6. ✅ Block-style conditional expressions (`if cond { } else { }`)
7. ✅ `0b` byte literal vs binary literal disambiguation
8. ✅ Intrinsics system for native operations
9. ⏳ Generic entity/record support (semantic analysis)

### Phase 2: Module System

1. Module resolution (file lookup)
2. Symbol import/export
3. Transitive imports
4. Circular import detection

### Phase 3: Intrinsics & Runtime

1. ✅ Arithmetic intrinsics (add, sub, mul, div, rem)
2. ✅ Bitwise intrinsics (and, or, xor, not)
3. ✅ Shift intrinsics (shl, ashr, lshr)
4. ✅ Comparison intrinsics (eq, ne, lt, le, gt, ge - signed/unsigned)
5. ✅ Type conversion intrinsics (sext, zext, trunc, bitcast, etc.)
6. ✅ Bit manipulation intrinsics (ctpop, ctlz, cttz, bitreverse, bswap)
7. ⏳ `sizeof<T>()` intrinsic
8. ⏳ Lambda code generation
9. ⏳ Closure capture semantics

### Phase 4: Advanced Features

1. Generic constraints (`where T: Comparable`)
2. Multiple type parameters (`Dict<K, V>`)
3. Option types (`T?`)
4. Type inference improvements

---

## Standard Library Status

### ✅ Architecturally Complete

- `List<T>` - stdlib/Collections/List.rf (with itertools)
- `Text<T>` - stdlib/Text/Text.rf
- `Console` - stdlib/Console.rf
- Memory wrappers - stdlib/memory/wrapper/
- **Native data types** - stdlib/NativeDataTypes/ (s8-s128, u8-u128, f16-f128, bool)
- **Error types** - stdlib/errors/ (Error, Crashable, common errors)
- **Error handling types** - stdlib/ErrorHandling/ (Maybe, Result, Lookup)

### ⏳ Waiting on Compiler

- Generic entity instantiation
- Module imports
- Lambda execution

---

## Recently Completed

### Protocol-Based Type System (Done)

The type system now uses protocols instead of hardcoded name patterns. This allows user-defined types to implement
standard protocols.

**Key Changes:**

1. **WellKnownProtocols class** - Defines protocol constants:
    - Numeric protocols: `Numeric`, `SignedNumeric`, `Integer`, `SignedInteger`, `UnsignedInteger`, `FloatingPoint`,
      `DecimalFloatingPoint`, `BinaryFloatingPoint`, `FixedWidth`, `Integral`
    - Comparison protocols: `Equatable`, `Comparable`, `Hashable`
    - Text protocols: `Parsable`, `Printable`, `TextConvertible`
    - Memory protocols: `Copyable`, `Movable`, `Droppable`, `DefaultConstructible`
    - Error protocols: `Crashable`
    - Collection protocols: `Iterable`, `Indexable`, `Collection`

2. **TypeInfo record** - Enhanced with protocol support:
    - `Protocols` HashSet for protocol membership
    - `Implements(protocol)` method for checking
    - Helper properties: `IsNumeric`, `IsInteger`, `IsFloatingPoint`, `IsSigned`, `IsUnsigned`, etc.

3. **PrimitiveTypes factory** - Creates types with correct protocols:
    - All primitive types (s8-s128, u8-u128, f16-f128, d32-d128, bool) have proper protocols assigned
    - `GetTypeInfo(typeName)` returns protocol-aware TypeInfo
    - `IsPrimitive(typeName)` checks if a type is primitive

4. **Expression.ResolvedType** - Type information flows from semantic analysis to code generation:
    - Semantic analyzer sets `ResolvedType` on all expression nodes
    - LLVM code generator checks `ResolvedType` before falling back to inference

**Benefits:**

- User-defined types (e.g., `i256` from two `s128`) can declare protocols like `SignedInteger`, `FixedWidth`, etc.
- No more hardcoded checks like `Name.StartsWith("s")` for determining signedness
- Cleaner separation between semantic types and LLVM types

---

### Native Data Type Stdlib (Done)

All native integer types now have complete operator implementations:

**Signed Types (s8, s16, s32, s64, s128):**

- Arithmetic: `+`, `+%`, `+^`, `+?`, `-`, `-%`, `-^`, `-?`, `*`, `*%`, `*^`, `*?`
- Division: `//`, `//?`, `%`, `%?`
- Power: `**`, `**%`, `**^`, `**?` (with `IndeterminateResultError` for 0**0, `NegativeExponentError` for negative exp)
- Bitwise: `&`, `|`, `^`, `~`
- Shift: `<<`, `<<?`, `>>`, `<<<`, `>>>`
- Comparison: `==`, `!=`, `<`, `<=`, `>`, `>=`
- Unary: `-` (negation)

**Unsigned Types (u8, u16, u32, u64, u128):**

- Same as signed, except:
    - No negative exponent check (unsigned can't be negative)
    - Uses unsigned comparison intrinsics (ult, ule, ugt, uge)
    - Uses unsigned division intrinsics (udiv, urem)

**Address Types (saddr, uaddr):**

- Wrapping-only arithmetic: `+%`, `+?`, `-%`, `-?`, `*%`, `*?`
- No base `+`, `-`, `*` operators (platform-dependent overflow)
- saddr: Has `>>` (sign-preserving right shift), no `<<` (use `<<?` or `<<<`)
- uaddr: No `>>` (use `>>>`), has `<<<`

### Error Type Hierarchy (Done)

Error handling uses protocol-based checking, not hardcoded error types:

```razorforge
# Base protocols
protocol Error { ... }
protocol Crashable from Error { ... }

# Any type implementing Crashable can be thrown
entity MyCustomError is Crashable { ... }

routine my_operation!() -> s32 {
    throw MyCustomError()  # Valid - implements Crashable
}
```

The compiler checks if thrown types implement the `Crashable` protocol, rather than checking against a hardcoded list of
error types.

### Intrinsics System (Done)

- `@intrinsic.*` syntax for LLVM operations
- Used in `danger!` blocks for unsafe code
- Supports generic type parameters
- Full arithmetic, bitwise, shift, comparison, and conversion operations

### @crash_only Attribute (Done)

- Prevents compiler from generating safe variants (`try_`, `check_`, `find_`)
- Used for operations that are intended to crash on error
- Example: `@crash_only routine s32.__add__!(other: s32) -> s32`

### Function Variant Generation (Done)

Compiler generates safe variants from `!` (failable) functions:

```razorforge
# User writes ! function with throw/absent keywords
routine divide!(a: s32, b: s32) -> s32 {
    if b == 0 {
        throw DivisionByZeroError()
    }
    return a / b
}
```

**Generation rules based on `throw` and `absent` usage:**

| `throw` | `absent` | Generated Variants |
|---------|----------|--------------------|
| no      | no       | Compile Error      |
| no      | yes      | `try_` only        |
| yes     | no       | `try_`, `check_`   |
| yes     | yes      | `try_`, `find_`    |

- `try_*` → Returns `Maybe<T>` (value or None)
- `check_*` → Returns `Result<T>` (value or Error)
- `find_*` → Returns `Lookup<T>` (value, None, or Error)

See: [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)

### Console I/O via C Runtime (Done)

- `show(value)` → printf with format specifiers
- `ask(prompt) -> Text` → fgets/scanf
- `ask_s32(prompt) -> s32` → scanf + strtol
- `ask_f64(prompt) -> f64` → scanf + strtod
- Target platform detection for Windows/Linux/macOS

### Type Constructor Naming Pattern (Done)

- Default constructor: `__create__`
- Failable constructor: `__create__!`
- Type method calls: `Type.__create__()` or `Type.__create__!()`

### Arrow Lambda Parsing (Done)

```razorforge
x => x * 2              # Single param, no parens
(x) => x * x            # Single param with parens
(a, b) => a + b         # Multi-param
() => 42                # No params
(x: s32) => x * 3       # Typed params
```

### Nested Generic Tokenization (Done)

```razorforge
List<List<s32>>         # >> no longer parsed as right-shift
List<Text<letter8>>     # Works correctly
```

### When Statement Pattern Matching (Done)

Pattern matching in `when` statements now supports:

```razorforge
when value {
    42 => handle_specific_value()           # Literal pattern
    x => use_variable(x)                    # Identifier pattern (binds value)
    _ => handle_default()                   # Wildcard pattern
    is SomeType => handle_type()            # Type pattern (no binding)
    is SomeType x => handle_typed(x)        # Type pattern with binding
}
```

### Itertools Methods (Done)

Added to `List<T>`:

- `select<U>`, `where`, `where_not`, `select_many<U>`
- `take`, `take_while`, `skip`, `skip_while`
- `any`, `all`, `none`, `count`
- `first`, `first_or_default`, `last`, `last_or_default`
- `reverse`, `extend`, `chunk`, `fold<U>`, `for_each`, `to_list`

---

## 7. Auto-Generated Methods for Data Types

**Priority: MEDIUM** - Required for complete type system support

The compiler needs to auto-generate default methods and operators for each data type declaration.

### 7.1 Record

```razorforge
record Point { x: f32, y: f32 }
```

**Auto-generated:**

| Method/Operator | Description                                             |
|-----------------|---------------------------------------------------------|
| `to_text()`     | String representation (e.g., `"Point(x: 1.0, y: 2.0)"`) |
| `to_debug()`    | Detailed debug representation                           |
| `memory_size()` | Size in bytes (fixed at compile time)                   |
| `hash()`        | Content-based hash value                                |
| `==`, `!=`      | Value equality (compares all fields)                    |
| `is`, `isnot`   | Type check                                              |

### 7.2 Entity

```razorforge
entity Document { title: Text }
```

**Auto-generated:**

| Method/Operator | Description                                             |
|-----------------|---------------------------------------------------------|
| `to_text()`     | String representation with type and public fields       |
| `to_debug()`    | Detailed debug representation (includes private fields) |
| `memory_size()` | Current size in bytes (can be dynamic)                  |
| `id()`          | Unique identity                                         |
| `copy()`        | Deep copy (creates independent duplicate)               |
| `==`, `!=`      | Value equality (compares content)                       |
| `is`, `isnot`   | Type check                                              |
| `===`, `!==`    | Reference equality (same instance)                      |

### 7.3 Choice

```razorforge
choice FileAccess { READ, WRITE, READ_WRITE }
```

**Auto-generated:**

| Method/Operator   | Description                                               |
|-------------------|-----------------------------------------------------------|
| `to_text()`       | Case name as string (e.g., `"READ"`)                      |
| `hash()`          | Hash value for use as dictionary key                      |
| `to_integer()`    | Zero-based integer value of case                          |
| `all_cases()`     | Common method returning list of all cases                 |
| `from_text(Text)` | Common method parsing string to case (returns `Maybe<T>`) |
| `==`, `!=`        | Equality comparison                                       |
| `is`, `isnot`     | Type check                                                |

### 7.4 Resident

```razorforge
resident SystemLogger { var log_count: s32 }
```

**Auto-generated:**

| Method/Operator | Description                           |
|-----------------|---------------------------------------|
| `to_text()`     | String representation                 |
| `to_debug()`    | Detailed debug representation         |
| `memory_size()` | Size in bytes (fixed at compile time) |
| `id()`          | Unique identity (stable address)      |
| `==`, `!=`      | Value equality (compares content)     |
| `is`, `isnot`   | Type check                            |
| `===`, `!==`    | Reference equality (same instance)    |

### 7.5 Variant

```razorforge
variant NetworkEvent {
    CONNECT(None),
    DISCONNECT(None),
    DATA_RECEIVED(payload: ValueText<letter8, 1024>)
}
```

**Auto-generated:**

| Method/Operator    | Description                                                     |
|--------------------|-----------------------------------------------------------------|
| `to_debug()`       | Debug string showing case and payload                           |
| `hash()`           | Hash value (if all contained records are hashable)              |
| `is_<case>()`      | Boolean convenience method for each case (e.g., `is_connect()`) |
| `try_get_<case>()` | Safe payload extraction returning `Maybe<T>`                    |
| `==`, `!=`         | Equality (same case and equal payload)                          |
| `is`, `isnot`      | Type check                                                      |

### Implementation Notes:

1. **`to_text()` vs `to_debug()`**:
    - `to_text()` shows public/public(module)/public(family) fields
    - `to_debug()` shows all fields including private

2. **Format style**: Use constructor syntax `TypeName(field: value, ...)` not Rust-style `TypeName { field: value }`

3. **User override**: Allow users to override any auto-generated method with custom implementation

---

## 8. Range Expression Validation

**Priority: MEDIUM** - Compile-time validation for range expressions

The compiler must enforce correct ordering of range bounds:

### Rules

| Expression   | Requirement | Error if violated                               |
|--------------|-------------|-------------------------------------------------|
| `a to b`     | `b >= a`    | CE: End of range must not be smaller than start |
| `a downto b` | `b <= a`    | CE: End of range must not be larger than start  |

### Examples

```razorforge
# ✅ Valid
for i in 0 to 10 { ... }
for i in 10 downto 0 { ... }

# ❌ CE: Invalid range bounds
for i in 10 to 0 { ... }      # Error: End (0) is smaller than start (10) in 'to' range
for i in 0 downto 10 { ... }  # Error: End (10) is larger than start (0) in 'downto' range
```

### Implementation Notes

1. **Compile-time check**: When both bounds are compile-time constants, validate immediately
2. **Runtime check**: When bounds are variables, generate runtime validation that crashes on invalid range
3. **Empty range**: `a to a` and `a downto a` are valid (single iteration or empty depending on exclusivity)

---

## 9. Reserved Routine Naming Rules

**Priority: HIGH** - Enforce routine naming rules

The compiler must enforce three naming rules for routines.

### 9.1 Crashing Routines Require `!` Suffix

Any routine that can crash (contains `throw`) must have the `!` suffix in its name.

```razorforge
# ✅ Valid
routine divide!(a: s32, b: s32) -> s32 {
    if b == 0 {
        throw DivisionByZeroError()
    }
    return a / b
}

# ❌ CE: Missing ! suffix
routine divide(a: s32, b: s32) -> s32 {
    if b == 0 {
        throw DivisionByZeroError()  # Error: Routine with 'throw' must have '!' suffix
    }
    return a / b
}
```

### 9.2 Reserved Prefixes: `try_`, `check_`, `find_`

These prefixes are reserved for compiler-generated safe variants. Users cannot define routines with these prefixes.

```razorforge
# ❌ CE: All of these are invalid
routine try_parse(text: Text) -> Maybe<s32> { ... }    # Error: 'try_' prefix is reserved
routine check_bounds(index: s32) -> Result<s32> { ... } # Error: 'check_' prefix is reserved
routine find_element(list: List<T>) -> Lookup<T> { ... } # Error: 'find_' prefix is reserved
```

### 9.3 Reserved Dunder Methods

The `__xxx__` naming pattern is reserved for compiler-generated special methods. Users cannot define methods with this
naming pattern.

```razorforge
# ❌ CE: All of these are invalid
routine __create__() { ... }           # Error: '__create__' is reserved for compiler-generated methods
routine __add__(other: T) -> T { ... } # Error: '__add__' is reserved for compiler-generated methods
routine __custom__() { ... }           # Error: '__custom__' is reserved for compiler-generated methods
routine __foo__!() { ... }             # Error: '__foo__!' is reserved for compiler-generated methods
```

### Summary

| Rule              | Pattern                      | Error                                                        |
|-------------------|------------------------------|--------------------------------------------------------------|
| Crashing routines | `throw` without `!` suffix   | CE: Routine with 'throw' must have '!' suffix                |
| Reserved prefixes | `try_*`, `check_*`, `find_*` | CE: Prefix is reserved for compiler-generated methods        |
| Reserved dunder   | `__*__`, `__*__!`            | CE: Dunder names are reserved for compiler-generated methods |

---

## 10. Missing Parser Features

**Priority: HIGH** - Required for stdlib to parse without errors

The following language features are documented in the wiki but not yet implemented in the parser.

### 10.1 `open`, `sealed`, `override` Keywords (Inheritance Control)

**Note:** These were previously implemented as attributes (`@open`, `@sealed`, `@override`) but are being reverted to
keywords for consistency with other language modifiers.

These keywords control method inheritance behavior for entities:

```razorforge
entity Animal {
    # Can be overridden by subclasses
    open routine speak() -> Text {
        return "..."
    }

    # Cannot be overridden (default for routines)
    routine id() -> u64 {
        return me._internal_id
    }
}

entity Dog from Animal {
    # Explicitly overrides parent method
    override routine speak() -> Text {
        return "Woof!"
    }
}

# Sealed entity - cannot be inherited
sealed entity FinalClass {
    value: s32
}
```

**Semantics:**

- `open` - Routine can be overridden by subclasses (routines are final by default)
- `sealed` - Entity cannot be inherited (entities are open by default)
- `override` - Explicitly marks a routine as overriding a parent routine (required when overriding)

**Parser needs:**

- Recognize `open`, `sealed`, `override` as keywords/tokens
- Parse `sealed` before `entity` declarations
- Parse `open` and `override` before `routine` in method declarations
- Validate: `override` requires parent routine exists and is `open`
- Validate: Cannot override a non-`open` routine

### 10.2 `preset` Keyword (Compile-Time Constants)

```razorforge
preset MAX_CONNECTIONS: s32 = 100
preset PI: f64 = 3.14159265359
preset LETTER_NULL: letter8 = letter8(value: 0x00)
```

**Parser needs:**

- Recognize `preset` as a keyword/token
- Parse `preset NAME: Type = value` declarations
- Store in symbol table as compile-time constant

### 10.2 `follows` Keyword (Protocol Implementation)

```razorforge
record Point follows Hashable, Comparable {
    x: f32,
    y: f32
}

entity Document follows Serializable {
    content: Text
}
```

**Parser needs:**

- Parse `follows ProtocolList` after type name in record/entity/resident declarations
- Support comma-separated list of protocols

### 10.3 Generic Constraints

Two syntaxes are used:

```razorforge
# Inline constraint syntax
routine find_max<T: Comparable>(items: List<T>) -> T { ... }
routine process<T: Hashable, Serializable>(item: T) { ... }

# Where clause syntax
routine sort<T>(list: List<T>) where T: T follows Comparable { ... }
record IndexedSet<T> where T: T follows Hashable, Comparable { ... }
```

**Parser needs:**

- Parse `<T: Constraint>` in generic parameter lists
- Parse `where T: T follows Protocol` clauses
- Support multiple constraints with comma separation

### 10.4 Protocol Declarations

```razorforge
protocol Drawable {
    routine draw() -> None
    routine bounds() -> Rectangle
}

protocol Serializable {
    common routine deserialize(data: Text) -> Result<me>
    routine serialize() -> Result<Text>
}
```

**Parser needs:**

- Recognize `protocol` keyword
- Parse method signatures without bodies
- Support `common routine` for static protocol methods
- Handle `me` as a placeholder for the implementing type

### 10.5 `common` Keyword (Static Methods)

```razorforge
record Point {
    x: f32, y: f32

    common routine origin() -> Point {
        return Point(x: 0.0, y: 0.0)
    }
}

# Called as: Point.origin()
```

**Parser needs:**

- Recognize `common` as a modifier before `routine`
- Mark the method as static/class-level in AST

### 10.6 `external` Declarations (FFI)

```razorforge
external("C") routine printf(format: cstr, ...) -> cint
external("C") routine malloc(size: uaddr) -> uaddr
external("C") routine free(ptr: uaddr)
```

**Parser needs:**

- Recognize `external` keyword with calling convention string
- Parse function signature without body
- Handle variadic `...` parameter

### 10.7 Extension Method Syntax

```razorforge
# Extend existing type with new methods (defined outside the type)
routine s32.is_even(me: s32) -> bool {
    return me % 2 == 0
}

routine List<T>.second<T>(me: List<T>) -> T? {
    if me.length() < 2 {
        return None
    }
    return me[1]
}
```

**Parser needs:**

- Parse `routine TypeName.method_name()` syntax
- Support generic types in extension (`List<T>.method<T>()`)
- Handle as method attached to existing type

### 10.8 Scoped Access Syntax

The `as` keyword is used for **scoped access** to entities (NOT for type casting - use constructor form instead):

```razorforge
# Scoped exclusive access
hijacking doc as h {
    h.title = "Updated"
    h.version += 1
}

# Scoped read-only access
viewing doc as v {
    show(v.title)
}

# Thread-safe exclusive access
seizing shared as s {
    s.value = 100
}

# Thread-safe read-only access
inspecting shared as o {
    show(o.value)
}

# Resource management
using file as f {
    f.write("data")
}
```

**Parser needs:**

- Parse `keyword expression as identifier { block }` for scoped access statements
- Keywords: `hijacking`, `viewing`, `seizing`, `inspecting`, `using`

**Note:** Type conversions use constructor syntax (`s32(value)`), NOT `as` casting.

### Token Status

Most tokens already exist in `TokenType.cs` but parsing logic is missing:

| Token        | Keyword      | Lexer | Parser | Usage                               |
|--------------|--------------|-------|--------|-------------------------------------|
| `Open`       | `open`       | ❌     | ❌      | Method can be overridden            |
| `Sealed`     | `sealed`     | ❌     | ❌      | Method cannot be overridden         |
| `Override`   | `override`   | ❌     | ❌      | Explicitly overrides parent method  |
| `Preset`     | `preset`     | ✅     | ❌      | Compile-time constants              |
| `Follows`    | `follows`    | ✅     | ❌      | Protocol implementation             |
| `Protocol`   | `protocol`   | ✅     | ❌      | Protocol declaration                |
| `Common`     | `common`     | ✅     | ❌      | Static method modifier              |
| `External`   | `external`   | ✅     | ❌      | FFI function declaration            |
| `Where`      | `where`      | ✅     | ❌      | Generic constraint clause           |
| `SelfType`   | `MyType`     | ✅     | ❌      | Type of `me` in protocols/methods   |
| `As`         | `as`         | ✅     | ❌      | Scoped access binding, import alias |
| `Hijacking`  | `hijacking`  | ✅     | ❌      | Scoped exclusive access             |
| `Viewing`    | `viewing`    | ✅     | ❌      | Scoped read-only access             |
| `Seizing`    | `seizing`    | ✅     | ❌      | Thread-safe exclusive access        |
| `Inspecting` | `inspecting` | ✅     | ❌      | Thread-safe read-only access        |
| `Using`      | `using`      | ✅     | ❌      | Resource management                 |

**Note:** The lexer recognizes most keywords as tokens, but the parser doesn't handle them in the grammar yet.

---

## 11. Error Handling Type API and Storage Rules

**Priority: HIGH** - Required for correct Maybe/Result/Lookup usage

### 11.1 Storage Rules (Compiler Enforcement)

The compiler must enforce which error handling types can be stored:

| Type        | Storable | Reason                                          |
|-------------|----------|-------------------------------------------------|
| `Maybe<T>`  | ✅ Yes    | Represents optional **data** - valid to persist |
| `Result<T>` | ❌ No     | Represents **operation outcome** - transient    |
| `Lookup<T>` | ❌ No     | Represents **query result** - transient         |

**Valid usage:**

```razorforge
# ✅ Maybe in fields
record User {
    email: Text?           # Optional data
    middle_name: Text?     # Some people don't have one
}

# ✅ Maybe in collections
let optionals: List<s32?> = [1, None, 3, None]

# ✅ Result/Lookup as local variables
routine process() {
    let result: Result<s32> = check_parse("42")  # OK - local
    let lookup: Lookup<User> = find_user(id)     # OK - local
}

# ✅ Result/Lookup as return types
routine check_foo() -> Result<Data>
routine find_bar() -> Lookup<Item>

# ✅ Result/Lookup as parameters
routine handle(result: Result<s32>)
```

**Invalid usage (compile errors):**

```razorforge
# ❌ CE: Result<T> cannot be stored in fields
record BadDesign {
    operation_result: Result<Data>  # Error: Result<T> cannot be stored
}

# ❌ CE: Lookup<T> cannot be stored in collections
let lookups: List<Lookup<User>>  # Error: Lookup<T> cannot be stored

# ❌ CE: Result<T> cannot be stored in fields
entity Service {
    last_result: Result<Response>  # Error: Result<T> cannot be stored
}
```

### 11.1.1 Nesting Rules (Compiler Enforcement)

`Maybe<T>`, `Result<T>`, and `Lookup<T>` have strict nesting rules:

1. **No nesting between each other** - Cannot have `Maybe<Result<T>>`, `Result<Maybe<T>>`, etc.
2. **Must be the outermost generic type** - Error handling types must wrap the final type, not be wrapped

**Valid:**

```razorforge
Maybe<List<s32>>           # ✅ Maybe wraps List
Result<Dict<Text, User>>   # ✅ Result wraps Dict
Lookup<List<Text>>         # ✅ Lookup wraps List
List<s32?>                 # ✅ Shorthand: List contains optional elements
```

**Invalid:**

```razorforge
# ❌ CE: Cannot nest error handling types
Maybe<Result<s32>>         # Error: Cannot nest Result inside Maybe
Result<Maybe<s32>>         # Error: Cannot nest Maybe inside Result
Result<Lookup<s32>>        # Error: Cannot nest Lookup inside Result
Lookup<Maybe<s32>>         # Error: Cannot nest Maybe inside Lookup

# ❌ CE: Error handling types must be outermost
List<Result<s32>>          # Error: Result must be outermost type
Dict<Text, Lookup<User>>   # Error: Lookup must be outermost type
```

**Rationale:** Error handling should happen at the boundary, not nested within data structures. If you need a list where
each element might fail, return `Result<List<T>>` and handle errors before populating the list.

### 11.2 Maybe<T> API

```razorforge
record Maybe<T> {
    private state: DataState
    private handle: DataHandle

    # NO constructors - compiler auto-wraps based on type signature
    # Return `None` keyword or value directly

    # Query
    routine is_valid() -> bool
    routine is_none() -> bool
    routine state() -> DataState

    # Coalescing (?? operator)
    routine __unwrap__(default: T) -> T
    routine __unwrap__(else: Routine<(T), U>) -> T

    # Convert
    routine Maybe<T>.__create__(value: Result<T>)
    routine Maybe<T>.__create__(value: Lookup<T>)
}
```

**Pattern matching:**

```razorforge
when maybe_value {
    is None => handle_missing()
    else v => use(v)  # Auto-unwrapped to T
}
```

### 11.3 Result<T> API

```razorforge
record Result<T> {
    private state: DataState
    private handle: DataHandle

    # NO constructors - compiler generates check_ variants from ! functions

    # Query
    routine is_valid() -> bool
    routine is_error() -> bool
    routine state() -> DataState

    # Coalescing (?? operator)
    routine __unwrap__(default: T) -> T
    routine __unwrap__(else: Routine<(T), U>) -> T

    # Access
    routine Result<T>.__create__(value: Maybe<T>, none_error: Crashable)
    routine Result<T>.__create__(value: Lookup<T>, none_error: Crashable)
}
```

**Pattern matching:**

```razorforge
when result_value {
    is Crashable e => handle_error(e)
    else v => use(v)  # Auto-unwrapped to T
}
```

### 11.4 Lookup<T> API

```razorforge
choice DataState {
    FOUND,
    ABSENT,
    ERROR
}

record Lookup<T> {
    private state: DataState
    private handle: DataHandle

    # NO constructors - compiler generates find_ variants from ! functions

    # Query
    routine is_valid() -> bool
    routine is_none() -> bool
    routine is_error() -> bool
    routine state() -> DataState

    # Coalescing (?? operator)
    routine __unwrap__(default: T) -> T
    routine __unwrap__(else: Routine<(T), U>) -> T
}
```

**Pattern matching (three-way):**

```razorforge
when lookup_value {
    is Crashable e => handle_error(e)
    is None => handle_missing()
    else v => use(v)  # Auto-unwrapped to T
}
```

### 11.5 None Runtime Representation

`None` is **not a runtime value**. It is a compile-time semantic concept encoded via discriminant fields:

| Type        | Discriminant       | None State           |
|-------------|--------------------|----------------------|
| `Maybe<T>`  | `is_valid: bool`   | `is_valid == false`  |
| `Result<T>` | `is_valid: bool`   | N/A (uses Crashable) |
| `Lookup<T>` | `state: DataState` | `state == ABSENT`    |

There is no `rf_None` type in the C runtime. The `DataHandle` field is simply ignored when the discriminant indicates
absence.

### 11.6 Key Design Principle: No Wrapper Functions

The type signature determines wrapping behavior - no `Some()`, `Ok()`, `Fail()`, `Found()`, `Absent()`, `Error()`
constructors:

```razorforge
# Compiler auto-wraps based on return type
routine try_find(id: s32) -> User? {
    unless database.has(id) {
        return None  # Keyword, not constructor
    }
    return database.get(id)  # Auto-wrapped by T? signature
}

# User writes ! function, compiler generates check_ variant
routine divide!(a: s32, b: s32) -> s32 {
    if b == 0 {
        throw DivisionError(message: "Division by zero")
    }
    return a / b
}
# Compiler generates: check_divide(a, b) -> Result<s32>

# User writes ! function with throw AND absent, compiler generates find_ variant
routine get_user!(id: u64) -> User {
    unless db.connected() {
        throw DatabaseError(message: "Not connected")
    }
    unless db.has_user(id) {
        absent  # Keyword triggers Lookup generation
    }
    return db.get_user(id)
}
# Compiler generates: find_get_user(id) -> Lookup<User>
```

---

## 12. Known Code Generation Bugs

**Priority: HIGH** - These bugs cause invalid LLVM IR generation

### 12.1 Boolean `and`/`or` Expression Code Generation - FIXED

~~The code generator incorrectly handles boolean expressions with `and`/`or` operators when the operands are comparison
expressions.~~

**Fixed:** Added `LessEqual` and `GreaterEqual` operators to the operator mapping in `VisitBinaryExpression`. They were
missing, causing fallback to `add` instead of `icmp sle`/`icmp sge`.

### 12.2 Generic Type Names in Function Calls - FIXED

Function names containing generic type arguments like `Text<letter8>.to_cstr` were not sanitized, causing LLVM errors.

**Fixed:** Updated `SanitizeFunctionName` to replace `<`, `>`, and `,` with `_`.

### 12.3 Variable Scoping in If-Else Branches - FIXED

Variables declared in different branches of if-else had the same name, causing LLVM "multiple definition" errors.

**Fixed:** Added `_varCounter` and `GetUniqueVarName()` to generate unique variable names (e.g., `diff_0`, `diff_1`).

### 12.4 Pointer vs Value Parameter for Member Access - FIXED

When accessing struct fields on pointer parameters (like `%Range_BackIndex_uaddr*`), the code was treating them as value
types and trying to store them.

**Fixed:** Updated `VisitMemberExpression` to check if parameter type ends with `*` before deciding to allocate/store.

### 12.5 Integer Division for True Divide - SUPERSEDED

~~The `/` operator was using `fdiv` even for integer types, causing LLVM type errors.~~

**Superseded by 12.8:** True division (`/`) is now rejected for integer types entirely. Integer division must use floor
division (`//`).

### 12.6 Generic Type Names in Record Field Lookup - FIXED

Member access on generic types like `Range<BackIndex<uaddr>>` failed because the record field lookup used the
unsanitized type name.

**Fixed:** Added sanitization in `VisitMemberExpression` to convert `Range<BackIndex<uaddr>>` to `Range_BackIndex_uaddr`
before `_recordFields` lookup.

### 12.7 Type Constructors as Function Calls (OPEN)

Type conversions like `saddr(me)` or `uaddr(me)` in Integral.rf are being generated as function calls instead of proper
LLVM type conversions (sext/zext/trunc).

**Example:**

```razorforge
routine u8.to_saddr(me: u8) -> saddr {
    return saddr(me)  # Should be: sext i8 %me to i64
}
```

**Generated (incorrect):**

```llvm
%tmp2 = call i32 @saddr(i8 %me)  ; Function call to non-existent @saddr
```

**Expected:**

```llvm
%tmp2 = sext i8 %me to i64  ; Type extension intrinsic
```

**Root Cause:** The code generator treats `TypeName(value)` as a function call instead of recognizing it as a type
constructor/conversion.

### 12.8 True Division Rejected for Integer Types - FIXED

True division (`/`) is now rejected at semantic analysis for integer types. Users must use floor division (`//`) for
integers.

**Example:**

```razorforge
let a: s32 = 10
let b: s32 = 3
let c = a / b   # ❌ Error: True division (/) is not supported for integer type 's32'
let d = a // b  # ✅ Use floor division for integers
```

**Error Message:**

```
True division (/) is not supported for integer type 's32'. Use floor division (//) for integer division, or convert to a floating-point type first.
```

**Fixed in:** `src/Analysis/SemanticAnalyzer.Expressions.cs` - `VisitBinaryExpression()`

### 12.9 Record Field Access with Unique Variable Names - FIXED

Member access on local variables (`p.x`) was using the original variable name (`%p`) instead of the unique name (`%p_0`)
created by the variable declaration.

**Fixed in:** `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - `VisitMemberExpression()` now looks up `__varptr_`
mapping for local variables.

### 12.10 Integer Literal Type Inference (OPEN)

Integer literals like `5` and `42` currently default to `s64`, causing mixed-type errors when used with smaller types
like `s32`.

**Example:**

```razorforge
routine double_it(x: s32) -> s32 {
    return x * 2  # Error: Cannot multiply s32 by s64 (literal 2)
}

routine main() {
    let a: s32 = 5  # Error: Cannot assign s64 to s32
}
```

**Expected behavior:**

- Literals should infer their type from context when possible
- `let a: s32 = 5` should make `5` an s32
- `x * 2` where `x: s32` should make `2` an s32
- Untyped context defaults to `s64` (current behavior)

**Implementation approach:**

1. During semantic analysis, propagate expected type into literal expressions
2. In `VisitVariableDeclaration`, if type annotation exists, set expected type for initializer
3. In `VisitBinaryExpression`, if one operand has known type, use it as expected type for literals
4. In `VisitCallExpression`, use parameter types as expected types for arguments

### 12.11 Function Call Return Type Mismatch (OPEN)

When calling functions, the code generator uses default `i32` return type instead of the actual function's return type.
This causes LLVM type errors when the function returns a different type like `i64`.

**Example:**

```razorforge
routine double_it(x: s64) -> s64 {
    return x * 2
}

routine main() {
    let result: s64 = double_it(5)
}
```

**Generated (incorrect):**

```llvm
%tmp1 = call i32 @double_it(i64 %a_0)  ; Wrong: should be call i64
store i64 %tmp1, ptr %result_1          ; Type mismatch!
```

**Root Cause:** `VisitCallExpression` doesn't look up the callee's return type from the function signature.

### 12.12 Dunder Method Variant Generation Underscore Handling (OPEN)

When generating `try_`, `check_`, or `find_` variants of dunder methods (like `__add__!`), the underscores should be
removed to produce clean method names.

**Example:**

```razorforge
routine s32.__add__!(other: s32) -> s32 {
    # Overflow-checked addition
    ...
}
```

**Current (incorrect):**

- Generates: `try___add__`, `check___add__`

**Expected:**

- Should generate: `try_add`, `check_add`

**Implementation:** When generating variant names, strip the leading `__` and trailing `__` from dunder method names
before prepending the variant prefix.

### 12.2 Prelude Core Types

The following core types are now loaded in the prelude:

- `core/Integral` - Protocol for integer types
- `core/BackIndex` - Backward indexing (`^1`, `^2`, etc.)
- `core/Range` - Range type for iteration and slicing

These are dependencies of `Text` and `List` which are also in the prelude.
