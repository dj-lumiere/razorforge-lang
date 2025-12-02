# RazorForge Standard Library Checklist

This document tracks the implementation status of all standard library components.

## Legend

- ✅ Complete - All operators/methods implemented and tested
- ⏳ Partial - Some functionality implemented
- ❌ Not Started - Stub only or missing
- 🔧 Needs Review - Implementation exists but may need updates

---

## Native Data Types (`stdlib/NativeDataTypes/`)

### Signed Integers

| Type | Arithmetic   | Overflow Variants | Bitwise    | Shifts              | Comparison        | Conversions         | Status |
|------|--------------|-------------------|------------|---------------------|-------------------|---------------------|--------|
| s8   | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s16  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s32  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s64  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| s128 | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |

**Power Operators for Signed Types:**

- ✅ `**` - Base power (throws `IndeterminateResultError` for 0**0, `NegativeExponentError` for negative exp)
- ✅ `**%` - Wrapping power (same error checks)
- ✅ `**^` - Saturating power (same error checks)
- ✅ `**?` - Checked power (returns `None` for 0 base or negative exp)

### Unsigned Integers

| Type | Arithmetic   | Overflow Variants | Bitwise    | Shifts              | Comparison        | Conversions         | Status |
|------|--------------|-------------------|------------|---------------------|-------------------|---------------------|--------|
| u8   | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u16  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u32  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u64  | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |
| u128 | ✅ +,-,*,//,% | ✅ %,^,? variants  | ✅ &,\|,^,~ | ✅ <<,<<?,>>,<<<,>>> | ✅ ==,!=,<,<=,>,>= | ✅ to/from all types | ✅      |

**Power Operators for Unsigned Types:**

- ✅ `**` - Base power (throws `IndeterminateResultError` for 0**0, no negative exp check)
- ✅ `**%` - Wrapping power (same error checks)
- ✅ `**^` - Saturating power (same error checks)
- ✅ `**?` - Checked power (returns `None` for 0 base)

### Address Types (Platform-Dependent)

| Type  | Wrapping Arithmetic | Checked Arithmetic | Bitwise    | Shifts                   | Comparison | Status |
|-------|---------------------|--------------------|------------|--------------------------|------------|--------|
| saddr | ✅ +%,-%, *%         | ✅ +?,-?,*?,//?,%   | ✅ &,\|,^,~ | ✅ <<?,>>,<<<,>>> (no <<) | ✅ all      | ✅      |
| uaddr | ✅ +%,-%, *%         | ✅ +?,-?,*?,//?,%   | ✅ &,\|,^,~ | ✅ <<?,<<<,>>> (no >>)    | ✅ all      | ✅      |

**Note:** Address types have NO base `+`, `-`, `*` operators - only wrapping/checked variants.

### Floating Point Types

| Type | Basic Arithmetic | Math Functions | Comparison | Conversions | Status |
|------|------------------|----------------|------------|-------------|--------|
| f16  | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |
| f32  | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |
| f64  | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |
| f128 | ⏳ +,-,*,/        | ❌              | ⏳          | ⏳           | ⏳      |

### Decimal Floating Point Types

| Type | Basic Arithmetic | Math Functions | Comparison | Conversions | Status |
|------|------------------|----------------|------------|-------------|--------|
| d32  | ❌                | ❌              | ❌          | ❌           | ❌      |
| d64  | ❌                | ❌              | ❌          | ❌           | ❌      |
| d128 | ❌                | ❌              | ❌          | ❌           | ❌      |

### Other Native Types

| Type  | Implementation                   | Status |
|-------|----------------------------------|--------|
| bool  | ✅ Logical operators, conversions | ✅      |
| Blank | ✅ Unit type                      | ✅      |
| None  | ✅ None type for Maybe            | ✅      |

---

## Error Types (`stdlib/errors/`)

| Type                     | Description                   | Status |
|--------------------------|-------------------------------|--------|
| Error                    | Base error type               | ✅      |
| Crashable                | Base for crash-causing errors | ✅      |
| DivisionByZeroError      | Division by zero              | ✅      |
| IntegerOverflowError     | Integer overflow              | ✅      |
| IndeterminateResultError | 0**0 case                     | ✅      |
| NegativeExponentError    | Negative exponent on integers | ✅      |
| IndexOutOfBoundsError    | Array/list index error        | ✅      |
| stackframe               | Stack frame info              | ⏳      |
| stacktrace               | Stack trace collection        | ⏳      |
| message                  | Error message handling        | ⏳      |
| common                   | Common error utilities        | ⏳      |

---

## Error Handling Types (`stdlib/ErrorHandling/`)

| Type      | Description                    | Status |
|-----------|--------------------------------|--------|
| Maybe<T>  | Optional value (value or None) | ✅      |
| Result<T> | Value or Error                 | ✅      |
| Lookup<T> | Value, None, or Error          | ✅      |

---

## Collections (`stdlib/Collections/`)

### Lists

| Type            | Type Kind | Core Operations                             | Iteration | Status |
|-----------------|-----------|---------------------------------------------|-----------|--------|
| List<T>         | entity    | ✅ push, pop, get, set, insert, remove, len  | ✅         | ✅      |
| FixedList<T, N> | resident  | ✅ push, pop, get, set, insert, remove       | ✅         | ✅      |
| ValueList<T, N> | record    | ✅ get only (immutable, use `with` for mods) | ✅         | ✅      |

### Deques (Double-Ended Queues)

| Type             | Type Kind | Core Operations                        | Iteration | Status |
|------------------|-----------|----------------------------------------|-----------|--------|
| Deque<T>         | entity    | ✅ push/pop front+back, rotate, reverse | ✅         | ✅      |
| FixedDeque<T, N> | resident  | ✅ push/pop front+back, rotate, reverse | ✅         | ✅      |

### Sets

| Type           | Type Kind | Strategy           | Core Operations                                  | Iteration | Status |
|----------------|-----------|--------------------|--------------------------------------------------|-----------|--------|
| Set<T>         | entity    | Separate chaining  | ✅ insert, remove, contains, set ops              | ✅         | ✅      |
| FixedSet<T, N> | resident  | Robin hood hashing | ✅ insert, remove, contains                       | ✅         | ✅      |
| SortedSet<T>   | entity    | B-tree             | ✅ insert, remove, contains, range, `__getitem__` | ✅         | ✅      |

### Dictionaries

| Type               | Type Kind | Strategy           | Core Operations                             | Iteration | Status |
|--------------------|-----------|--------------------|---------------------------------------------|-----------|--------|
| Dict<K, V>         | entity    | Separate chaining  | ✅ insert, remove, get, keys/values          | ✅         | ✅      |
| FixedDict<K, V, N> | resident  | Robin hood hashing | ✅ insert, remove, get, keys/values          | ✅         | ✅      |
| SortedDict<K, V>   | entity    | B-tree             | ✅ insert, remove, get, range, `__getitem__` | ✅         | ✅      |

### Sorted Collections

| Type          | Type Kind | Strategy | Core Operations                                  | Iteration | Status |
|---------------|-----------|----------|--------------------------------------------------|-----------|--------|
| SortedList<T> | entity    | B-tree   | ✅ insert, remove, contains, range, `__getitem__` | ✅         | ✅      |

### Priority Queues

| Type                               | Type Kind | Strategy    | Core Operations   | Iteration      | Status |
|------------------------------------|-----------|-------------|-------------------|----------------|--------|
| PriorityQueue<TElement, TPriority> | entity    | Binary heap | ✅ push, pop, peek | ✅ (heap order) | ✅      |

### Bit Collections

| Type            | Type Kind | Core Operations                               | Iteration | Status |
|-----------------|-----------|-----------------------------------------------|-----------|--------|
| BitList         | entity    | ✅ push, pop, get, set, count_ones/zeros, flip | ✅         | ✅      |
| FixedBitList<N> | resident  | ✅ push, pop, get, set, count_ones/zeros, flip | ✅         | ✅      |
| ValueBitList<N> | record    | ✅ get only (immutable, use `with` for mods)   | ✅         | ✅      |

### Tuples

| Type       | Type Kind | Core Operations | Iteration | Status |
|------------|-----------|-----------------|-----------|--------|
| Tuple      | entity    | ⏳               | ❌         | ⏳      |
| ValueTuple | record    | ⏳               | ❌         | ⏳      |

### Collection Implementation Notes

**Type Kinds:**

- `entity` - Heap-allocated, mutable, dynamic growth
- `resident` - Fixed-size at compile time, reference semantics, internal mutability
- `record` - Value type, immutable, use `with` statement for modifications

**Hashing Strategies:**

- **Separate chaining**: Uses SortedSet/SortedDict as bucket chains - O(log k) per chain
- **Robin hood hashing**: Open addressing with probe distance tracking - cache-friendly, O(1) average
- **B-tree**: Balanced tree with O(log n) operations, supports range queries and indexed access

---

## Text Types (`stdlib/Text/`)

| Type               | Core Operations      | Unicode | Iteration | Status |
|--------------------|----------------------|---------|-----------|--------|
| Text<T>            | ⏳                    | ⏳       | ⏳         | ⏳      |
| FixedText<T>       | ⏳                    | ⏳       | ⏳         | ⏳      |
| ValueText<T>       | ⏳                    | ⏳       | ⏳         | ⏳      |
| TextBuffer<T>      | ⏳                    | ⏳       | ⏳         | ⏳      |
| FixedTextBuffer<T> | ⏳                    | ⏳       | ⏳         | ⏳      |
| letter8            | ⏳ UTF-8 code unit    | -       | -         | ⏳      |
| letter16           | ⏳ UTF-16 code unit   | -       | -         | ⏳      |
| letter32           | ⏳ Unicode code point | -       | -         | ⏳      |

---

## Memory Types (`stdlib/memory/`)

### Core Memory Types

| Type              | Description            | Status |
|-------------------|------------------------|--------|
| DynamicSlice<T>   | Dynamic memory slice   | ⏳      |
| TemporarySlice<T> | Stack-allocated slice  | ⏳      |
| MemorySize        | Memory size with units | ✅      |

### Memory Wrappers (`stdlib/memory/wrapper/`)

| Type        | Description                              | Status |
|-------------|------------------------------------------|--------|
| Inspected<T> | Read-only borrowed reference             | ⏳      |
| Viewed<T>   | Read-write borrowed reference            | ⏳      |
| Shared<T>   | Reference-counted shared ownership       | ⏳      |
| Retained<T> | Strong reference (prevents deallocation) | ⏳      |
| Tracked<T>  | Tracked lifetime reference               | ⏳      |
| Seized<T>   | Exclusive ownership transfer             | ⏳      |
| Snatched<T> | Temporary exclusive access               | ⏳      |
| Hijacked<T> | Unsafe raw access                        | ⏳      |

### Memory Controllers (`stdlib/memory/controller/`)

| Type             | Description                   | Status |
|------------------|-------------------------------|--------|
| RetainController | Reference counting controller | ⏳      |
| ShareController  | Shared ownership controller   | ⏳      |

---

## Arbitrary Precision Types (Suflae)

| Type     | Basic Arithmetic | Advanced Operations | Status |
|----------|------------------|---------------------|--------|
| Integer  | ⏳ +,-,*,//,%     | ⏳                   | ⏳      |
| Fraction | ⏳ +,-,*,/        | ⏳                   | ⏳      |
| Decimal  | ⏳ +,-,*,/        | ⏳                   | ⏳      |

---

## Console I/O (`stdlib/Console.rf`)

| Function                        | Description                         | Status |
|---------------------------------|-------------------------------------|--------|
| show<T>(value)                  | Print value                         | ✅      |
| show_line<T>(value)             | Print value with newline            | ✅      |
| get_letters(prompt) -> Text     | Read individual letters             | ⏳      |
| get_word(prompt) -> Text        | Read single word (whitespace delim) | ⏳      |
| get_line(prompt) -> Text        | Read single line                    | ⏳      |
| get_words(prompt) -> List<Text> | Read multiple words                 | ⏳      |
| get_lines(prompt) -> List<Text> | Read multiple lines                 | ⏳      |
| get_all(prompt) -> Text         | Read all input until EOF            | ⏳      |

---

## Atomic Types (`stdlib/AtomicDataTypes/`)

| Type       | Description         | Status |
|------------|---------------------|--------|
| AtomicBool | Thread-safe boolean | ⏳      |

---

## Runtime (`stdlib/Runtime/`)

| Component       | Description         | Status |
|-----------------|---------------------|--------|
| compilerservice | Compiler intrinsics | ⏳      |

---

## Core (`stdlib/core.rf`)

| Component             | Description              | Status |
|-----------------------|--------------------------|--------|
| Core type definitions | Base types and utilities | ⏳      |

---

## C Subsystem (`stdlib/memory/CSubsystem.rf`)

| Component | Description         | Status |
|-----------|---------------------|--------|
| C interop | C function bindings | ⏳      |

---

## Operator Implementation Checklist

### Per-Type Operator Requirements

#### Signed Integers (sN)

- [x] `+`, `+%`, `+^`, `+?` - Addition variants
- [x] `-`, `-%`, `-^`, `-?` - Subtraction variants
- [x] `*`, `*%`, `*^`, `*?` - Multiplication variants
- [x] `//`, `//?` - Floor division
- [x] `%`, `%?` - Remainder
- [x] `**`, `**%`, `**^`, `**?` - Power (with 0**0 and negative exp checks)
- [x] `&`, `|`, `^`, `~` - Bitwise
- [x] `<<`, `<<?`, `>>`, `<<<`, `>>>` - Shifts
- [x] `==`, `!=`, `<`, `<=`, `>`, `>=` - Comparison
- [x] `-` (unary) - Negation
- [x] Type conversions to/from all numeric types

#### Unsigned Integers (uN)

- [x] `+`, `+%`, `+^`, `+?` - Addition variants
- [x] `-`, `-%`, `-^`, `-?` - Subtraction variants
- [x] `*`, `*%`, `*^`, `*?` - Multiplication variants
- [x] `//`, `//?` - Floor division
- [x] `%`, `%?` - Remainder
- [x] `**`, `**%`, `**^`, `**?` - Power (with 0**0 check, no negative exp)
- [x] `&`, `|`, `^`, `~` - Bitwise
- [x] `<<`, `<<?`, `>>`, `<<<`, `>>>` - Shifts
- [x] `==`, `!=`, `<`, `<=`, `>`, `>=` - Comparison (unsigned)
- [x] Type conversions to/from all numeric types

#### Address Types (saddr, uaddr)

- [x] `+%`, `+?` - Wrapping/checked addition (NO base `+`)
- [x] `-%`, `-?` - Wrapping/checked subtraction (NO base `-`)
- [x] `*%`, `*?` - Wrapping/checked multiplication (NO base `*`)
- [x] `//?`, `%?` - Checked division/remainder
- [x] `&`, `|`, `^`, `~` - Bitwise
- [x] Shifts (saddr: `<<?, >>, <<<, >>>`, uaddr: `<<?, <<<, >>>`)
- [x] `==`, `!=`, `<`, `<=`, `>`, `>=` - Comparison

---

## Error Types Checklist

- [x] Error (base)
- [x] Crashable (base for crash errors)
- [x] DivisionByZeroError
- [x] IntegerOverflowError
- [x] IndeterminateResultError
- [x] NegativeExponentError
- [x] IndexOutOfBoundsError
- [ ] FileNotFoundError
- [ ] PermissionDeniedError
- [ ] NetworkError
- [ ] ParseError
- [ ] ValidationError

---

## Testing Checklist

### Unit Tests Needed

- [ ] All signed integer operators (s8-s128)
- [ ] All unsigned integer operators (u8-u128)
- [ ] Address type operators (saddr, uaddr)
- [ ] Floating point operators (f16-f128)
- [ ] Decimal floating point operators (d32-d128)
- [ ] Error type creation and handling
- [ ] Collection operations
- [ ] Text operations
- [ ] Memory wrapper behavior

### Integration Tests Needed

- [ ] Cross-type conversions
- [ ] Error propagation through call stack
- [ ] Memory safety with wrappers
- [ ] Generic type instantiation
- [ ] Import/module resolution

---

## Notes

### Conventions

1. **Operator naming**: `__add__` for `+`, `__add_wrap__` for `+%`, etc.
2. **Failable functions**: Use `!` suffix, compiler generates `try_`, `check_`, `find_` variants
3. **`@crash_only`**: Prevents safe variant generation
4. **`danger!` blocks**: Required for intrinsic operations

### Priorities

1. **HIGH**: Native integer types (done), error types (done), collections (done)
2. **MEDIUM**: Floating point, text, memory wrappers
3. **LOW**: Decimal types, atomic types, advanced collections
