# Standard Library TODO

This document tracks standard library implementation tasks that depend on compiler features being completed first.

## Current Status (Updated 2025-12-04)

✅ **MAJOR MILESTONE:** Bug 12.13 (Generic Method Template Matching) is now **FULLY RESOLVED!**

This means:
- Generic types can now be instantiated: `TestType<s64>(value: 42)`
- Generic methods now work: `instance.get_value()` on `TestType<s64>`
- Template matching works automatically: `TestType<s64>` → `TestType<T>`
- **Stdlib generic types are ready for testing!**

Most stdlib code is **architecturally complete** and many features are now **ready for testing** with the working generics implementation.

---

## 🔄 STDLIB REORGANIZATION: Core Prelude Namespace

### New Organization Structure

**Goal:** Move all fundamental types into the `core` namespace, which is automatically loaded without requiring imports.

All files in `stdlib/core/` should be marked with `namespace core` and will be available in every RazorForge file automatically.

### Migration Plan

#### Files to Move into `stdlib/core/` and mark with `namespace core`:

1. **Primitives** (from `stdlib/NativeDataTypes/`)
   - [ ] s8.rf, s16.rf, s32.rf, s64.rf, s128.rf
   - [ ] u8.rf, u16.rf, u32.rf, u64.rf, u128.rf
   - [ ] saddr.rf, uaddr.rf
   - [ ] f16.rf, f32.rf, f64.rf, f128.rf
   - [ ] d32.rf, d64.rf, d128.rf
   - [ ] bool.rf

2. **Letters** (from `stdlib/NativeDataTypes/` or create in `stdlib/core/text/`)
   - [ ] letter8.rf
   - [ ] letter16.rf
   - [ ] letter32.rf

3. **Error Handling** (from various locations → `stdlib/core/errors/`)
   - [ ] Maybe.rf
   - [ ] Result.rf
   - [ ] Lookup.rf
   - [ ] Crashable.rf
   - [ ] Error.rf

4. **Memory** (from `stdlib/memory/` → `stdlib/core/memory/`)
   - [ ] DynamicSlice.rf
   - [ ] MemorySize.rf
   - [ ] ~~TemporarySlice.rf~~ (REMOVED - doesn't work)

5. **FFI** (from `stdlib/FFI/` → `stdlib/core/ffi/`)
   - [ ] cstr.rf
   - [ ] cint.rf
   - [ ] Other C interop types

6. **Utilities** (from `stdlib/core/` → keep in `stdlib/core/`)
   - [ ] BackIndex.rf
   - [ ] Range.rf
   - [ ] Integral.rf

7. **Types** (create new `stdlib/core/types/`)
   - [ ] Blank.rf (unit type)
   - [ ] Duration.rf (time literals)

#### Files to Keep Outside Core (require import):

- **Console.rf** → `stdlib/Console.rf` (namespace Console or implicit)
- **Text/Text.rf** → `stdlib/Text/Text.rf` (namespace Text/Text or implicit)
- **Collections/** → `stdlib/Collections/` (namespace Collections)
- **Integer.rf, Decimal.rf, Fraction.rf** → `stdlib/` (arbitrary precision types)

### New Directory Structure

```
stdlib/
├── core/                        # Always-loaded prelude (namespace: core)
│   ├── primitives/
│   │   ├── s8.rf - s128.rf     (namespace core)
│   │   ├── u8.rf - u128.rf     (namespace core)
│   │   ├── f16.rf - f128.rf    (namespace core)
│   │   ├── d32.rf - d128.rf    (namespace core)
│   │   ├── saddr.rf            (namespace core)
│   │   ├── uaddr.rf            (namespace core)
│   │   └── bool.rf             (namespace core)
│   ├── text/
│   │   ├── letter8.rf          (namespace core)
│   │   ├── letter16.rf         (namespace core)
│   │   └── letter32.rf         (namespace core)
│   ├── errors/
│   │   ├── Maybe.rf            (namespace core)
│   │   ├── Result.rf           (namespace core)
│   │   ├── Lookup.rf           (namespace core)
│   │   ├── Crashable.rf        (namespace core)
│   │   └── Error.rf            (namespace core)
│   ├── ffi/
│   │   ├── cstr.rf             (namespace core)
│   │   └── cint.rf             (namespace core)
│   ├── memory/
│   │   ├── DynamicSlice.rf     (namespace core)
│   │   └── MemorySize.rf       (namespace core)
│   ├── types/
│   │   ├── Blank.rf            (namespace core)
│   │   └── Duration.rf         (namespace core)
│   ├── BackIndex.rf            (namespace core)
│   ├── Range.rf                (namespace core)
│   └── Integral.rf             (namespace core)
├── Console.rf                   # Namespace: Console (or implicit)
├── Text/
│   └── Text.rf                  # Namespace: Text/Text (or implicit)
├── Collections/
│   ├── List.rf                  # Namespace: Collections (or implicit)
│   ├── Dict.rf
│   └── Set.rf
├── Integer.rf
├── Decimal.rf
└── Fraction.rf
```

### Migration Checklist

For each file being moved to `core`:

1. [ ] Move file to appropriate `stdlib/core/` subdirectory
2. [ ] Add `namespace core` declaration at top of file
3. [ ] Update imports in other files that reference it
4. [ ] Test that file compiles with namespace declaration
5. [ ] Verify auto-loading when core prelude is implemented

---

## 1. Core Data Types

### ✅ Completed

**Native Integer Types** (stdlib/NativeDataTypes/)
- s8, s16, s32, s64, s128 (signed)
- u8, u16, u32, u64, u128 (unsigned)
- saddr, uaddr (platform-dependent)
- All arithmetic, bitwise, shift, and comparison operators
- Checked, wrapping, and saturating variants
- Full intrinsic implementations

**Native Float Types** (stdlib/NativeDataTypes/)
- f16, f32, f64, f128 (binary floating point)
- d32, d64, d128 (decimal floating point)
- Basic operators defined

**Boolean Type** (stdlib/NativeDataTypes/bool.rf)
- Logical operators (and, or, not)
- Comparison operators

### ⏳ Waiting on Compiler

**Letter Types** (stdlib/NativeDataTypes/)
- letter8, letter16, letter32 (Unicode code points)
- **Blocker:** Generic record instantiation

---

## 2. Text and String Processing

### ⏳ Waiting on Compiler

**Text<T> Type** (stdlib/Text/Text.rf)
- Generic over code point size (letter8/16/32)
- UTF-8/UTF-16/UTF-32 support
- String operations (concat, slice, search, etc.)
- **Blockers:**
  - ~~Generic method template matching (Bug 12.13)~~ ✅ FIXED!
  - Generic record instantiation (two-pass generation)
  - `to_text()` protocol implementation
  - `to_8bit()` / `to_32bit()` conversion methods

---

## 3. Collections

### ⏳ Waiting on Compiler

**List<T>** (stdlib/Collections/List.rf)
- Dynamic array with growth
- Itertools methods (select, where, fold, etc.)
- **Blockers:**
  - ~~Generic method template matching (Bug 12.13)~~ ✅ FIXED!
  - Generic entity instantiation (two-pass generation)

**Dict<K, V>** (stdlib/Collections/Dict.rf)
- Hash table implementation
- **Blockers:**
  - Generic entity instantiation
  - Multiple type parameters
  - Generic constraints (`K: Hashable`)

**Set<T>** (stdlib/Collections/Set.rf)
- Hash set implementation
- **Blockers:**
  - Generic entity instantiation
  - Generic constraints (`T: Hashable`)

---

## 4. Console I/O

### ✅ Completed (Partial)

**Non-Generic Console Functions** (stdlib/Console.rf)
- `Console.show(value: Text<letter8>)` - print text
- `Console.show_line(value: Text<letter8>)` - print with newline
- `Console.alert(value: Text<letter8>)` - print to stderr
- `Console.alert_line(value: Text<letter8>)` - stderr with newline
- `Console.get_word()`, `Console.get_line()`, etc. - input functions

### ⏳ Waiting on Compiler

**Generic Console Functions** (stdlib/Console.rf - currently commented out)
```razorforge
routine Console.show<T>(value: T) { ... }
routine Console.show_line<T>(value: T) { ... }
routine Console.alert<T>(value: T) { ... }
routine Console.alert_line<T>(value: T) { ... }
```

**Blockers:**
1. Generic function overload resolution (choose between generic/non-generic)
2. Protocol constraints (`<T: Printable>`)
3. `to_text()` protocol implementation for all types
4. `Text<letter32>.to_8bit()` conversion method

**Impact:** Without this, you can only print `Text<letter8>`. Cannot do `Console.show(42)`.

---

## 5. Error Handling

### ✅ Completed

**Error Types** (stdlib/errors/)
- Base `Error` protocol
- `Crashable` protocol
- Specific errors: `DivisionByZeroError`, `IntegerOverflowError`, `NegativeExponentError`, `IndeterminateResultError`, etc.

**Error Handling Types** (stdlib/ErrorHandling/)
- `Maybe<T>` - optional values
- `Result<T>` - operation outcomes with errors
- `Lookup<T>` - query results (found/absent/error)
- `DataState` choice type
- `DataHandle` record

### ⏳ Waiting on Compiler

**Error Type Methods**
- `Maybe<T>.is_valid()`, `is_none()`, etc.
- `Result<T>.is_error()`, etc.
- `Lookup<T>.is_none()`, `is_error()`, etc.
- **Blocker:** Generic method instantiation

**Pattern Matching on Error Types**
```razorforge
when maybe_value {
    is None => handle_missing()
    else v => use(v)
}
```
- **Blocker:** Type pattern matching in when statements

---

## 6. Memory Management

### ✅ Completed

**Memory Wrappers** (stdlib/memory/wrapper/)
- Low-level memory operations
- Pointer arithmetic utilities

### ⏳ Waiting on Compiler

**Smart Pointers**
- `Unique<T>` - unique ownership
- `Shared<T>` - reference counted
- `Weak<T>` - weak references
- **Blocker:** Generic entity instantiation

---

## 7. FFI and Native Interop

### ✅ Completed

**cstr Type** (stdlib/FFI/cstr.rf)
- C string representation
- Pointer wrapper for FFI

**External C Functions** (stdlib/Console.rf, stdlib/memory/)
- `rf_console_print_cstr`, `rf_console_get_line`, etc.
- `rf_memory_alloc`, `rf_memory_free`, etc.
- **Note:** Need C runtime implementation (see runtime.c TODO)

### ⏳ Waiting on Parser

**External Declarations**
```razorforge
external("C") routine printf(format: cstr, ...) -> cint
external("C") routine malloc(size: uaddr) -> uaddr
```
- **Blocker:** `external` keyword parsing
- **Blocker:** Variadic `...` parameter parsing

---

## 8. Protocols and Traits

### ⏳ Waiting on Compiler

**Protocol Declarations**
```razorforge
protocol Printable {
    routine to_text() -> Text<letter32>
}

protocol Hashable {
    routine hash() -> u64
}

protocol Comparable {
    routine __lt__(other: me) -> bool
    routine __le__(other: me) -> bool
    # etc.
}
```

**Blockers:**
- `protocol` keyword parsing
- Protocol implementation checking
- Generic constraints (`<T: Comparable>`)

---

## 9. Ranges and Iteration

### ⏳ Waiting on Compiler

**Range<T>** (stdlib/core/Range.rf)
- `0 to 10`, `10 downto 0`
- Iterator protocol
- **Blocker:** Generic record instantiation

**BackIndex<I>** (stdlib/core/BackIndex.rf)
- Backward indexing: `^1`, `^2`
- **Blockers:**
  - ~~Bug 12.13 (method template matching)~~ ✅ FIXED!
  - Generic record instantiation (two-pass generation)

---

## 10. Iterators and Itertools

### ⏳ Waiting on Compiler

**Iterator Protocol**
```razorforge
protocol Iterator<T> {
    routine next() -> T?
    routine has_next() -> bool
}
```

**Itertools Functions**
- Already defined on `List<T>` but cannot be tested
- `select<U>`, `where`, `fold<U>`, etc.
- **Blockers:**
  - Generic entity methods
  - Lambda code generation
  - Higher-order functions

---

## Implementation Priority

### Phase 1: After Generic Types Work
1. Enable generic Console functions (critical for usability)
2. Test `Text<letter8>` implementation
3. Test `List<T>` with simple types (`List<s32>`)

### Phase 2: After Protocol System
1. Implement `to_text()` for all primitive types
2. Implement `Hashable` for primitives
3. Test `Dict<K, V>` and `Set<T>`

### Phase 3: Advanced Features
1. Smart pointers (`Unique<T>`, `Shared<T>`)
2. Advanced itertools with lambdas
3. String interpolation

---

## Testing Status

**Current State:** Most stdlib code compiles (parses and analyzes) but cannot be executed or tested due to missing compiler features.

**Next Steps:**
1. ~~Fix Bug 12.13 (generic method template matching)~~ ✅ COMPLETED (2025-12-04)
2. **Test simple generic types** (Range<s64>, TestType<s64>, etc.) - NOW POSSIBLE!
3. Implement generic record instantiation properly (two-pass generation)
4. Enable and test `Console.show<T>` (requires overload resolution)
5. Test `List<T>` with basic operations
6. Expand testing to other generic types

---

## Dependencies on Compiler Features

| Stdlib Feature | Compiler Requirement | Status |
|----------------|---------------------|--------|
| Generic Console functions | Overload resolution + constraints | ⏳ Waiting |
| Text<T> methods | ~~Generic method instantiation (Bug 12.13)~~ | ✅ READY! |
| List<T> operations | Generic entity instantiation + methods | ✅ Methods ready! |
| Dict<K, V> | Multiple type params + constraints | ⏳ Waiting |
| Protocol implementations | Protocol system + constraint checking | ⏳ Waiting |
| Smart pointers | Generic entities + drop semantics | ⏳ Waiting |
| Itertools with lambdas | Lambda code generation | ⏳ Waiting |

**See:** [COMPILER_TODO_NEW.md](COMPILER_TODO_NEW.md) for compiler feature roadmap.