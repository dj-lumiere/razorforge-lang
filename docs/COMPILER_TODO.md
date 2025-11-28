# Compiler Features TODO for Standard Library Support

This document tracks compiler features needed for full RazorForge standard library support.

## Current Status

### ✅ Working Features
- Basic function declarations and calls
- Primitive types (s32, u64, f64, bool, etc.)
- Duration literals (5w, 3d, 2h, 30m, 45s, 100ms, 500us, 1000ns)
- Memory size literals (1024b, 64kb, 1mb, 2gb, 3tib, etc.)
- Arithmetic and comparison operators
- If statements with proper Bool type checking
- Loop statements (while, for, loop with break)
- Local variables (let, var)
- LLVM IR code generation
- Import statement parsing (syntax recognized)
- Generic function monomorphization (`identity<s32>(42)` works)
- Nested generic type parsing (`List<List<s32>>`, `List<Text<letter8>>`)
- Arrow lambda parsing (`x => expr`, `(x, y) => expr`, `() => expr`)
- Lambda expression AST nodes
- Console I/O (`show`, `ask`) via C runtime (printf, scanf, strtol, strtod)
- Target platform detection (Windows/Linux/macOS)
- Function variant generation (`try_`, `check_`, `find_` from `!` functions)
- Failable function support (`!` suffix with `throw` and `absent` keywords)

### ⏳ Partially Implemented
- Generic types (functions work, entities/records need work)
- Module imports (parsed but not resolved)

### ❌ Missing Features Required for Stdlib

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

### What's Needed:

```razorforge
import Collections/List           # Need to resolve to stdlib/Collections/List.rf
import memory/DynamicSlice        # Handle transitive imports
```

### Implementation:
1. **ModuleResolver** - Map import paths to file paths
2. **Search paths** - stdlib/, local project, etc.
3. **Circular import detection** - Track loaded modules
4. **Symbol table integration** - Add imported symbols

---

## 3. Function Type Syntax

**Priority: HIGH** - Required for itertools/callbacks

### Idiomatic Syntax:
```razorforge
# Function type syntax: Routine<(ParamTypes), ReturnType>
mapper: Routine<(T), U>           # Single param
predicate: Routine<(T), bool>     # Returns bool
folder: Routine<(U, T), U>        # Two params
action: Routine<(T), void>        # No return value
```

### Parser Changes Needed:
- Parse `Routine<(T, U), R>` as a type expression
- Handle tuple syntax `(T, U)` for parameter types
- Support in function parameter types

---

## 4. Intrinsic Functions

**Priority: HIGH** - Required for List implementation

### Required Intrinsics:
```razorforge
sizeof<T>() -> u64                # Size of type T in bytes
crash(message: Text)              # Panic/abort with message
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

## Implementation Priority

### Phase 1: Core Generics (Current Focus)
1. ✅ Generic function monomorphization
2. ✅ Nested generic type parsing (`>>` fix)
3. ⏳ Generic entity/record support
4. ⏳ `Routine<(T), U>` type parsing

### Phase 2: Module System
1. Module resolution (file lookup)
2. Symbol import/export
3. Transitive imports
4. Circular import detection

### Phase 3: Intrinsics & Runtime
1. `sizeof<T>()` intrinsic
2. `crash()` intrinsic
3. Lambda code generation
4. Closure capture semantics

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

### ⏳ Waiting on Compiler
- Generic entity instantiation
- Module imports
- Intrinsics (`sizeof<T>`, `crash`)
- Lambda execution

---

## Recently Completed

### Function Variant Generation (Done)
Compiler generates safe variants from `!` (failable) functions:

```razorforge
# User writes ! function with throw/absent keywords
routine divide!(a: s32, b: s32) -> s32 {
    if b == 0 {
        throw DivisionError(message: "Division by zero")
    }
    return a / b
}
```

**Generation rules based on `throw` and `absent` usage:**

| `throw` | `absent` | Generated Variants |
|--------|----------|-------------------|
| no     | no       | Compile Error     |
| no     | yes      | `try_` only       |
| yes    | no       | `try_`, `check_`  |
| yes    | yes      | `try_`, `find_`   |

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

### Itertools Methods (Done)
Added to `List<T>`:
- `select<U>`, `where`, `where_not`, `select_many<U>`
- `take`, `take_while`, `skip`, `skip_while`
- `any`, `all`, `none`, `count`
- `first`, `first_or_default`, `last`, `last_or_default`
- `reverse`, `extend`, `chunk`, `fold<U>`, `for_each`, `to_list`
