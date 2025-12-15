# RazorForge Compiler TODO

**Last Updated:** 2025-12-14

This document tracks compiler features needed for RazorForge and Suflae. For standard library implementation tasks, see [STDLIB-TODO.md](STDLIB-TODO.md).

---

## IMMEDIATE PRIORITIES

### 1. Generic Record Instantiation (BLOCKING STDLIB)
**Status:** 🔴 **CRITICAL** - Generic methods work, but generic type instantiation is fragile

**Problem:** Struct definitions generated immediately without dependency ordering. Cannot handle recursive types or complex instantiation patterns.

**Root Cause:**
- No dependency ordering (must generate `Node<T>` before `List<Node<T>>`)
- Circular dependencies not handled (`Node<T>` contains `Node<T>`)
- Fields may reference types that don't exist yet

**Concrete Tasks:**
1. Implement two-pass generation: collect all needed instantiations first
2. Build dependency graph and perform topological sort
3. Handle recursive types with forward declarations
4. Generate all struct definitions before any functions

**Files to Modify:**
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - `InstantiateGenericRecord()` line ~245
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - `InstantiateGenericEntity()` line ~290

**Estimated Effort:** 2-3 weeks

---

### 2. Native Runtime Library (BLOCKING EXECUTION)
**Status:** 🔴 **HIGH** - Valid LLVM IR generated but cannot link executables

**Missing Functions:**
- Text formatting: `format_s64`, `format_s32`, `format_f64`, etc.
- Stack trace runtime: `__rf_init_symbol_tables`, `__rf_stack_push`, `__rf_stack_pop`
- Console I/O: `rf_console_print_cstr`, `rf_console_get_line`, etc.
- Memory management: `rf_memory_alloc`, `rf_memory_free`, `rf_memory_copy`

**Concrete Tasks:**
1. Create `native/runtime.c` with implementations
2. Update build system to compile and link runtime.c
3. Test end-to-end: source → IR → executable → run

**Files:**
- `native/runtime.c` (create)
- `native/build.sh` (update)
- Build system integration

---

### 3. Method Mutation Inference (CRITICAL FOR TOKEN SYSTEM)
**Status:** 🔴 **CRITICAL** - Blocks token API design

**Why Critical:** Token system (`Viewed<T>`, `Hijacked<T>`, `Inspected<T>`, `Seized<T>`) requires knowing which methods mutate `me`. Without this:
- API fragmentation (different methods on `Viewed<T>` vs `Hijacked<T>`)
- API duplication (`get()`/`get_mut()` pattern everywhere)
- Runtime failures (all methods compile but some panic)

**Concrete Tasks:**
1. Implement `MutationAnalyzer.cs` with direct mutation detection (field writes)
2. Add call graph construction
3. Add transitive propagation (if A calls mutating B, then A is mutating)
4. Integrate into semantic analysis pass
5. Add token type enforcement in code generation
6. Add return type wrapping for token propagation

**Files:**
- `src/Analysis/MutationAnalyzer.cs` (NEW FILE)
- `src/Analysis/SemanticAnalyzer.cs` - Integrate mutation analysis
- `src/Analysis/TypeInfo.cs` - Add mutation info storage
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Token enforcement
- `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - Return type wrapping

**See Also:** [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md), [RazorForge Routines](../wiki/RazorForge-Routines.md)

---

### 4. Crashable Function Tracking & Variant Generation (CRITICAL FOR ERROR HANDLING)
**Status:** 🔴 **NOT IMPLEMENTED** - Blocks error handling system

**Problem:** No tracking of which functions can crash (use `!` suffix). No auto-generation of error-handling variants.

**The Four Function Types:**

| Type | Naming | Who Writes | Behavior | Example |
|------|--------|------------|----------|---------|
| **1. Crashable** | `foo!()` | User | Crashes on error/absent | `read_file!(path) -> Text` |
| **2. Try** | `try_foo()` | Compiler | Returns `T?` (None on error/absent) | `try_read_file(path) -> Text?` |
| **3. Check** | `check_foo()` | Compiler | Returns `Result<T>` (error info) | `check_read_file(path) -> Result<Text>` |
| **4. Find** | `find_foo()` | Compiler | Returns `Lookup<T>` (error/absent/value) | `find_get_user(id) -> Lookup<User>` |

**Auto-Generation Rules:**

The compiler generates variants based on what keywords appear in the `!` function body:

| `throw` in body? | `absent` in body? | Compiler generates |
|------------------|-------------------|--------------------|
| ❌ No | ❌ No | **Compile Error** - `!` functions must have `throw` or `absent` |
| ❌ No | ✅ Yes | `try_` only |
| ✅ Yes | ❌ No | `try_`, `check_` |
| ✅ Yes | ✅ Yes | `try_`, `find_` |

**Examples for Each Rule:**

```razorforge
# Rule 1: No throw, no absent → Compile Error
routine calculate!(a: s32, b: s32) -> s32 {
    return a + b  # ❌ ERROR: No throw or absent in ! function
}

# Rule 2: absent only (no throw) → Generates try_ only
routine get_item!(id: u64) -> Item {
    unless storage.has(id) {
        absent  # Only absent, no throw
    }
    return storage.get(id)
}
# Compiler generates: try_get_item() -> Item?
# User wrote: get_item!() -> Item (crashes with AbsentValueError)

# Rule 3: throw only (no absent) → Generates try_, check_
routine process_data!(input: Text<letter32>) -> Data {
    if input.is_empty() {
        throw ValidationError(message: "Empty input")
    }
    return Data(input)
}
# Compiler generates:
#   try_process_data() -> Data?
#   check_process_data() -> Result<Data>
# User wrote: process_data!() -> Data (crashes on throw)

# Rule 4: BOTH throw AND absent → Generates try_, find_
routine get_user!(id: u64) -> User {
    if id == 0 {
        throw ValidationError(message: "Invalid ID")
    }
    unless database.has(id) {
        absent  # Has both throw and absent → generates find_
    }
    return database.get(id)
}
# Compiler generates:
#   try_get_user() -> User?
#   find_get_user() -> Lookup<User>
# User wrote: get_user!() -> User (crashes on throw or absent)
```

**Critical Insight - Body Transformation:**

When generating non-crashable variants, statements are transformed as follows:
- `check_` variant (returns `Result<T>`): `throw E` → `return E`, `return V` → `return V`
- `try_` variant (returns `T?`): `throw E` → `return None`, `absent` → `return None`, `return V` → `return V`
- `find_` variant (returns `Lookup<T>`): `throw E` → `return E`, `absent` → `return None`, `return V` → `return V`

**Current Issues:**
- ❌ No detection of `!` function calls
- ❌ No enforcement that non-`!` functions can't call `!` functions directly
- ❌ No auto-generation of `try_`, `check_`, and `find_` variants
- ❌ No body transformation (`throw`/`absent` → return conversion)
- ❌ No detection of `throw` and `absent` keywords in function bodies
- ❌ No validation of `@crash_only` attribute
- ❌ No tracking of which variant is being called

**Concrete Tasks:**

1. **Detect keywords in `!` function bodies:**
   - Walk AST to find `throw` statements
   - Walk AST to find `absent` statements
   - Validate: `!` functions MUST have at least one `throw` or `absent`
   - Store keyword presence in function metadata

2. **Track function error category in AST/symbol table:**
   - Detect `!` suffix on function name (user-written crashable)
   - Store generated variant types: `try_`, `check_`, `find_`
   - Store in `FunctionInfo` with enum: `Crashable | TryVariant | CheckVariant | FindVariant`

3. **Call site validation:**
   - Detect calls to `!` functions
   - Enforce: only callable from other `!` functions or with explicit `try_`/`check_`/`find_` prefix
   - Generate error: "Cannot call crashable function 'foo!' from non-crashable context. Use try_foo(), check_foo(), or find_foo()"

4. **Auto-generate variants with body transformation:**
   - From `foo!(x: T) -> U`, generate 1-2 variants based on body content:
     - `try_foo(x: T) -> U?` - ALWAYS generated
     - `check_foo(x: T) -> Result<U>` - if body has `throw` but NOT `absent`
     - `find_foo(x: T) -> Lookup<U>` - if body has BOTH `throw` AND `absent`
   - Store all versions in symbol table
   - Mark generated functions as compiler-generated (cannot be manually written)

5. **Body transformation algorithm:**
   ```
   For try_ variant (returns T?):
   - Walk AST of ! function body
   - Replace: throw <expr> → return None
   - Replace: absent → return None
   - Replace: return <expr> → return <expr>
   - Replace calls to other ! functions: foo!(x) → try_foo(x)?

   For check_ variant (returns Result<T>):
   - Walk AST of ! function body
   - Replace: throw <expr> → return <expr>
   - Replace: return <expr> → return <expr>
   - Replace calls to other ! functions: foo!(x) → check_foo(x)?

   For find_ variant (returns Lookup<T>):
   - Walk AST of ! function body
   - Replace: throw <expr> → return <expr>
   - Replace: absent → return None
   - Replace: return <expr> → return <expr>
   - Replace calls to other ! functions: foo!(x) → find_foo(x)?
   ```

6. **Validate `@crash_only` attribute:**
   - Only allowed on `start!` function
   - Required if `start` has `!` suffix
   - Enables crashable calls in entry point without transformation

7. **Transitive analysis:**
   - If function A calls crashable function B without try_/check_/find_ prefix, then A must be crashable
   - Build call graph and propagate crashability

**Example Enforcement:**
```razorforge
# User writes crashable function with throw only
routine parse_config!(path: Text<letter32>) -> Config {
    let data = read_file!(path)  # ✅ OK - parse_config! is crashable
    unless data.contains("version") {
        throw ConfigError(message: "Missing version field")
    }
    return Config.deserialize!(data)  # ✅ OK
}

# Non-crashable function cannot call ! directly
routine safe_parse(path: Text<letter32>) -> Config? {
    let data = read_file!(path)  # ❌ ERROR: Cannot call crashable function
    # Fix: use try_ variant
    let data = try_read_file(path)?  # ✅ OK - returns Text? (None on error)
    return try_Config_deserialize(data)?  # ✅ OK
}

# Compiler auto-generates try_ and check_ (because body has throw but no absent):
routine try_parse_config(path: Text<letter32>) -> Config? {
    let data = try_read_file(path)?  # Transformed: read_file! → try_read_file
    unless data.contains("version") {
        return None  # Transformed: throw → return None
    }
    return try_Config_deserialize(data)?  # Transformed
}

routine check_parse_config(path: Text<letter32>) -> Result<Config> {
    let data = check_read_file(path)?  # Transformed: read_file! → check_read_file
    unless data.contains("version") {
        return ConfigError(message: "Missing version field")  # Transformed: throw E → return E
    }
    return check_Config_deserialize(data)?  # Transformed
}

# Example with both throw AND absent → generates try_ and find_
routine get_user!(id: u64) -> User {
    if id == 0 {
        throw ValidationError(message: "Invalid ID")
    }
    unless database.has(id) {
        absent  # Has both throw and absent
    }
    return database.get(id)
}

# Compiler auto-generates:
routine try_get_user(id: u64) -> User? {
    if id == 0 {
        return None  # throw → None
    }
    unless database.has(id) {
        return None  # absent → None
    }
    return database.get(id)
}

routine find_get_user(id: u64) -> Lookup<User> {
    if id == 0 {
        return ValidationError(message: "Invalid ID")  # throw E → return E
    }
    unless database.has(id) {
        return None  # absent → None
    }
    return database.get(id)
}
```

**Files:**
- `src/Analysis/CrashableAnalyzer.cs` (NEW FILE - detect `throw`/`absent` in bodies, validation)
- `src/Analysis/FunctionVariantGenerator.cs` (NEW FILE - auto-gen try_/check_/find_ with body transform)
- `src/AST/ASTTransformer.cs` (NEW FILE - throw/absent → return conversion)
- `src/Analysis/SemanticAnalyzer.cs` - Integrate crashable validation
- `src/Analysis/SymbolTable.cs` - Add function variant enum: Crashable | TryVariant | CheckVariant | FindVariant
- `src/CodeGen/LLVMCodeGenerator.Functions.cs` - Generate all variants (1 user + 1-2 generated)
- `src/Parser/RazorForgeParser.cs` - Parse `@crash_only` attribute, `throw` and `absent` statements

**LSP Integration:**
The LSP should track and visualize all four function types:
- Color-code by error handling: `!` functions in red, `check_` in yellow, `try_` in blue, `find_` in purple
- Show in hover: "Crashable function", "Auto-generated try_ variant from foo!", etc.
- Diagnostic: Highlight crashable calls in non-crashable contexts
- Quick fix: "Replace with try_foo()", "Replace with check_foo()", or "Replace with find_foo()"

**See Also:** [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)

---

## CORE FEATURES STATUS

### Generics
**Status:** ✅ **CORE FUNCTIONALITY COMPLETE** (2025-12-04)

**What Works:**
- ✅ Generic function monomorphization (`identity<T>`, `swap<T>`)
- ✅ Namespace-qualified generic methods (`Console.show<T>`)
- ✅ Nested generic types (`List<List<s32>>`, `Range<BackIndex<uaddr>>`)
- ✅ Generic type instantiation (`TestType<s64>(...)`)
- ✅ Generic method calls (`instance.method()`)
- ✅ Template matching (concrete types match templates automatically)
- ✅ End-to-end compilation with full type checking

**Remaining Work:**
1. **Generic Record Instantiation** (See Immediate Priorities #1)
2. **Generic Function Overload Resolution** - Cannot choose between generic and non-generic versions
3. **Generic Constraints** - Cannot express type requirements (`<T: Comparable>`)

**See:** [GENERICS-STATUS.md](GENERICS-STATUS.md), [BUG-12.13-ANALYSIS.md](BUG-12.13-ANALYSIS.md)

---

### Type System
**Status:** ⏳ **PARTIALLY IMPLEMENTED**

**Working:**
- ✅ Record wrapper architecture ("everything is a record")
- ✅ Extract-operate-wrap pattern consistently applied
- ✅ Multi-field struct handling
- ✅ Nested record types (e.g., `%d32 = type { %u32 }` where `%u32 = type { i32 }`)
- ✅ Type conversion and casting with auto-cast for variable initialization
- ✅ Floating-point type detection and operations

**Missing:**
- ❌ Optional field syntax (`field: Type?`) - Parser rejects `?` in field declarations (Bug 12.17)
- ❌ Type constructor calls - `saddr(me)` generates function call instead of type conversion (Bug 12.7)
- ❌ Integer literal type inference - Literals don't respect expected type context (Bug 12.10)
- ❌ Function return type tracking - Defaults to i32 instead of actual return type (Bug 12.11)

---

### Module System & Core Prelude
**Status:** ✅ **CORE PRELUDE WORKING** | ⏳ **MODULE IMPORTS BROKEN**

**Working:**
- ✅ Core prelude auto-loading (`namespace core` files loaded automatically)
- ✅ Primitive types always available (s8-s128, u8-u128, f16-f128, d32-d128, bool)
- ✅ Error types always available (Maybe, Result, Lookup, DataHandle, DataState)
- ✅ Memory types always available (DynamicSlice, MemorySize, BackIndex, Range)
- ✅ Transitive dependency loading for core modules

**Broken:**
- ❌ Import statements fail to resolve modules even with `--stdlib` flag (Bug 12.15)
- ❌ Module resolver not finding stdlib files
- ❌ Selective imports not working (`import Collections/{List, Dict}`)

**Design Decisions (Finalized):**
1. **Search Order:** `stdlib/` → project root → external packages
2. **Import Style:** Unqualified access - `import Collections/List` → use `List<T>` directly
3. **Core Prelude:** `core` namespace automatically loaded (no import needed)
4. **Loading Strategy:** Eager - load all transitive imports before semantic analysis

**Implementation Tasks:**
- [ ] Fix module path resolution (Bug 12.15)
- [ ] Implement selective imports
- [ ] Add circular import detection
- [ ] Add collision detection for duplicate names

**Files:**
- `src/Analysis/ModuleResolver.cs` - Fix path resolution
- `src/Analysis/CorePreludeLoader.cs` - ✅ Complete
- `src/Analysis/SemanticAnalyzer.cs` - Fix import processing

**See Also:** [Modules-and-Imports.md](../wiki/Modules-and-Imports.md)

---

### Protocol System
**Status:** ⏳ **INFRASTRUCTURE EXISTS, IMPLEMENTATION INCOMPLETE**

**Current State:**
- `WellKnownProtocols` class defines standard protocols
- `TypeInfo.Protocols` tracks protocol membership
- Primitive types have correct protocols assigned

**Missing:**
- ❌ Protocol declarations (parser support for `protocol` keyword)
- ❌ Protocol fields (mixin pattern)
- ❌ Default implementations
- ❌ Mutation annotations (`@readonly`, `@writable`, `@migratable`)
- ❌ Implementation checking and verification
- ❌ Generic constraints (`<T: Protocol>`)

**Key Design Points:**
- Protocols are both contracts AND mixins (provide fields + default implementations)
- Mutation annotations are part of contract
- Default implementations declared OUTSIDE protocol block (saves indentation)
- `Me` (capitalized) as type placeholder, `me` (lowercase) as instance reference

**Implementation Priority:**
1. Parser support (protocol declarations)
2. Abstract methods (contract verification)
3. Mutation annotations (integrate with #3 mutation inference)
4. Protocol fields (mixin pattern)
5. Default implementations
6. Generic constraints

**Depends On:**
- Method Mutation Inference (Immediate Priorities #3)
- Iterator Permission Inference (for migratable detection)

**See Also:** [RazorForge-Protocols.md](../wiki/RazorForge-Protocols.md)

---

### Memory Model & Tokens
**Status:** ⏳ **DESIGN COMPLETE, INFERENCE MISSING**

**Token Types:** `Viewed<T>` (readonly), `Hijacked<T>` (mutable), `Inspected<T>` (shared readonly), `Seized<T>` (shared mutable)

**Blocking Issue:** Requires Method Mutation Inference (Immediate Priorities #3)

**Additional Work Needed:**
- Iterator Permission Inference (R/RW/RWM detection)
- Migratable operation detection (DynamicSlice buffer relocations)
- Token type enforcement at call sites
- Return type wrapping for token propagation

**See Also:** [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md)

---

## FUTURE WORK (Lower Priority)

### Parser Features

#### External Declarations (FFI)
```razorforge
external("C") routine printf(format: cstr, ...) -> cint
```
**Tasks:**
- [ ] Parse `external` keyword with calling convention
- [ ] Parse variadic `...` parameter
- [ ] Handle function signature without body

---

#### Preset (Compile-Time Constants)
```razorforge
preset PI: f64 = 3.14159265359
```
**Tasks:**
- [ ] Parse `preset` keyword
- [ ] Store in symbol table as compile-time constant
- [ ] Inline at use sites

---

#### Common (Static Methods)
```razorforge
record Point {
    common routine origin() -> Point { ... }
}
```
**Tasks:**
- [ ] Parse `common` modifier before `routine`
- [ ] Mark as static/class-level in AST
- [ ] Code generation without receiver

---

#### Inheritance Control
```razorforge
sealed entity FinalClass { ... }
entity Animal {
    open routine speak() -> Text { ... }
}
entity Dog from Animal {
    override routine speak() -> Text { ... }
}
```
**Tasks:**
- [ ] Add `open`, `sealed`, `override` to lexer
- [ ] Parse modifiers in correct positions
- [ ] Validate override relationships

---

#### Scoped Access Syntax
```razorforge
hijacking doc as d { d.edit() }
viewing doc as d { print(d.title) }
seizing mutex as m { m.value = 10 }
```
**Tasks:**
- [ ] Parse `hijacking`, `viewing`, `seizing`, `inspecting`, `using`
- [ ] Parse `as` binding
- [ ] Semantic analysis: verify access semantics
- [ ] Code generation: generate locking/unlocking

---

#### String Literal Enhancements

**Status:** ⏳ **PARTIALLY IMPLEMENTED** (regular and raw strings work)

**Missing:**
- ❌ Backslash continuation - `"Long text \<newline>continues"` (needed for 80-char limit)
- ❌ `dedent()` function - Strip common leading whitespace from multi-line strings

**Backslash Continuation:**
```razorforge
let error = "Error: The connection failed \
because the credentials were invalid."
# Result: "Error: The connection failed because the credentials were invalid."
```

**Tasks:**
- [ ] Lexer: Detect `\` followed by newline
- [ ] Lexer: Remove `\`, newline, and leading whitespace from next line
- [ ] Test with various scenarios

**dedent() Function:**
```razorforge
let message = dedent("
    Welcome to RazorForge!
    Commands:
      build  - Build project
      run    - Run project
    ")
```

**Tasks:**
- [ ] Implement in stdlib: `Text.dedent(me: Text) -> Text`
- [ ] Algorithm: find min indent, strip from all lines
- [ ] Test cases for edge cases

**Files:**
- `src/Lexer/BaseTokenizer.cs` - Backslash continuation
- `stdlib/Text/Text.rf` - dedent() method

**See Also:** [Code Style - Line Length Limit](../wiki/RazorForge-Code-Style.md#line-length-limit)

---

### Lambda Code Generation
**Status:** ⏳ **PARSING WORKS, CODEGEN INCOMPLETE**

**Current State:**
- ✅ Arrow lambda parsing: `x => x * 2`, `(a, b) => a + b`
- ✅ AST nodes created correctly
- ❌ Code generation not implemented

**Language Restriction:** ⚠️ Multiline lambdas are BANNED (use named functions instead)

```razorforge
# ✅ OK
let double = x => x * 2
let sum = (a, b) => a + b
let result = list.select(x => x * 2)

# ❌ BANNED
let compute = x => {
    let temp = x * 2
    temp + 1
}
```

**Tasks:**
- [ ] Generate function pointer type for lambda
- [ ] Create anonymous function in LLVM IR
- [ ] Handle captures (closure semantics)
- [ ] Implement `Routine<(T), U>` type
- [ ] Enforce single-expression restriction in parser

**Files:**
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - `VisitLambdaExpression()`
- `src/Parser/RazorForgeParser.Expressions.cs` - Validate no block syntax

---

### Auto-Generated Methods for Data Types

Compiler should auto-generate default methods for:

**Record:**
- `to_text()`, `to_debug()`, `memory_size()`, `hash()`, `==`, `!=`, `is`, `isnot`

**Entity:**
- `to_text()`, `to_debug()`, `memory_size()`, `id()`, `copy()`, `==`, `!=`, `===`, `!==`

**Choice:**
- `to_text()`, `hash()`, `to_integer()`, `all_cases()`, `from_text()`, `==`, `!=`

**Variant:**
- `to_debug()`, `hash()`, `is_<case>()`, `try_get_<case>()`, `==`, `!=`

**Files:**
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Add auto-generation logic

---

### Language Design Changes (TODO)

#### 1. Entry Point: `main` → `start`
**Status:** ⏸️ **TODO** - Design decision confirmed, implementation needed

**Change:** Rename entry point function from `main` to `start`

**Rationale:**
- Consistency with Suflae (`routine start()`)
- Avoid confusion with C's `main()`
- More intuitive for modern language

**Tasks:**
- [ ] Update compiler to look for `start()` instead of `main()`
- [ ] Update all examples and documentation
- [ ] Add migration note for existing code

---

#### 2. Reserve `start` and `start!` as Keywords
**Status:** ⏸️ **TODO**

**Restriction:** Prevent user code from defining routines named `start` or `start!` (except entry points)

**Tasks:**
- [ ] Add semantic check: reject `start`/`start!` declarations outside entry point
- [ ] Generate clear error message
- [ ] Allow in entry point file only

---

#### 3. `@crash_only` Attribute for `start!`
**Status:** ⏸️ **TODO**

**Requirement:** Crashable entry points must use `@crash_only` attribute

```razorforge
@crash_only
routine start!() {
    let file = open_file!("data.txt")  # Can use crashable calls
}
```

**Tasks:**
- [ ] Parse `@crash_only` attribute
- [ ] Validate only on `start!` functions
- [ ] Enable crashable calls in attributed functions
- [ ] Error if `start!` missing attribute

---

#### 4. Inline Conditional Restrictions
**Status:** ✅ **COMPLETE** (2025-12-08)

**Restrictions:**
- ❌ No block syntax in inline conditionals
- ❌ No nested inline conditionals

```razorforge
# ✅ OK
let x = if count > 0 then "items" else "empty"

# ❌ CE: Cannot use blocks
let y = if cond then { compute() } else { default() }

# ❌ CE: Cannot nest
let z = if a then (if b then 1 else 2) else 3
```

**Completed:**
- [x] Parser restrictions enforced
- [x] Clear compile errors generated
- [ ] Documentation updates (pending)

---

#### 5. Ban In-Scope Method Declarations
**Status:** ⏸️ **TODO** - Design decision confirmed

**Restriction:** Methods MUST be declared outside type scope

```razorforge
# ✅ OK
public entity List<T> {
    private var _buffer: pointer
}

routine List<T>.push(value: T) {
    # Implementation
}

# ❌ CE: In-scope methods not allowed
public entity List<T> {
    routine push(value: T) {  # Error!
        # ...
    }
}
```

**Rationale:**
- Consistency (internal and extension methods use same syntax)
- Multi-file organization (methods can be split across files)
- Cleaner type definitions
- Simpler parser

**Tasks:**
- [ ] Update parser to reject in-scope method declarations
- [ ] Generate clear error message
- [ ] Apply to entity/resident/record
- [ ] Update documentation

---

#### 6. Suflae `eternal` Keyword
**Status:** ⏸️ **TODO** (Suflae only)

**Feature:** Application-scoped singleton actors

```suflae
# start.sf
eternal AppCore:
    logger: Logger
    config: Config
    metrics: Metrics

routine start():
    AppCore.logger = Logger()
    # ...
```

**Requirements:**
- Start file only
- Single declaration per application
- Actor semantics (message passing)
- Immortal lifetime (never GC'd)

**Tasks:**
- [ ] Add `eternal` keyword to Suflae lexer
- [ ] Parse `eternal TypeName:` syntax
- [ ] Validate rules (single, start file only)
- [ ] Generate actor wrapper code
- [ ] Make globally accessible

**Files:**
- `src/Lexer/SuflaeLexer.cs`
- `src/Parser/SuflaeParser.cs`
- `src/Analysis/SemanticAnalyzer.cs`
- `src/CodeGen/SuflaeCodeGenerator.cs`

**See:** [Suflae Eternal](../wiki/Suflae-Eternal.md), [Resident vs Eternal](../wiki/Resident-vs-Eternal.md)

---

## KNOWN BUGS

### Bug 12.7: Type Constructor Calls
```razorforge
routine u8.to_saddr(me: u8) -> saddr {
    return saddr(me)  # Generates function call instead of type conversion!
}
```
**Fix:** Detect type constructor calls and generate appropriate conversion intrinsics.

---

### Bug 12.10: Integer Literal Type Inference
```razorforge
routine double_it(x: s32) -> s32 {
    return x * 2  # Error: s32 * s64 (literal defaults to s64)
}
```
**Fix:** Propagate expected type to literal expressions during semantic analysis.

---

### Bug 12.11: Function Call Return Type
```razorforge
routine get_value() -> s64 { return 42 }
let x: s64 = get_value()  # Type mismatch: expected s64, got i32
```
**Fix:** Look up actual return type from function signature.

---

### Bug 12.12: Dunder Method Variant Names
```razorforge
routine s32.__add__!(other: s32) -> s32 { ... }
# Should generate: try_add, check_add
# Currently generates: try___add__, check___add__
```
**Fix:** Strip `__` prefix/suffix before adding variant prefix.

---

### Bug 12.14: Error Location Tracking
**Status:** ✅ **FIXED** (2025-12-08)

Error messages now show correct file and line numbers using AST node locations.

---

### Bug 12.15: Module Resolution
**Problem:** Import statements fail to resolve modules even with `--stdlib` flag.

```razorforge
import Console  # Error: Module not found
```

**Fix:** Debug `ModuleResolver.cs` path resolution and stdlib flag handling.

**Files:**
- `src/Analysis/ModuleResolver.cs`
- `src/CLI/Program.cs`
- `src/Analysis/SemanticAnalyzer.Declarations.cs`

---

### Bug 12.16: Pointer Type in Binary Expressions
**Status:** ✅ **FIXED** (2025-12-04)

Added pointer type detection before calling `GetIntegerBitWidth()`.

---

### Bug 12.17: Optional Field Syntax
**Problem:** Parser rejects `?` syntax for optional fields.

```razorforge
entity SortedDict<K, V> {
    private root: BTreeDictNode<K, V>?  # Parse error!
}
```

**Fix:** Extend `ParseTypeExpression()` to recognize `?` suffix.

**Files:**
- `src/Parser/RazorForgeParser.Expressions.cs`
- `src/AST/ASTNode.cs` - Add `IsOptional` flag
- `src/Analysis/SemanticAnalyzer.Types.cs`
- `src/CodeGen/LLVMCodeGenerator.Types.cs`

**Workaround:** Entity references are nullable by default without explicit `?`

---

## REFERENCE INFORMATION

### File Locations

**Core Compiler:**
- `src/Parser/RazorForgeParser.cs` - Main parser
- `src/Analysis/SemanticAnalyzer.cs` - Semantic analysis and type checking
- `src/CodeGen/LLVMCodeGenerator.cs` - LLVM IR code generation
- `src/Analysis/GenericTypeResolver.cs` - Generic template matching (NEW, 256 lines)
- `src/Analysis/CorePreludeLoader.cs` - Auto-load core namespace (NEW, 100 lines)

**Module System:**
- `src/Analysis/ModuleResolver.cs` - Module path resolution (BROKEN)
- `src/Analysis/SymbolTable.cs` - Symbol tracking

**Standard Library:**
- `stdlib/core/` - Auto-loaded types (primitives, Maybe, Result, etc.)
- `stdlib/Collections/` - List, Dict, Set, etc.
- `stdlib/Text/` - Text type and operations
- `stdlib/Console.rf` - Console I/O

**Native Runtime:**
- `native/runtime/` - C implementations of native functions
- `native/build.sh` - Build script

---

### Implementation Patterns

#### Extract-Operate-Wrap Pattern
```llvm
; Extract primitive from record
%tmp1 = extractvalue %s32 %value, 0
; Operate on primitive
%tmp2 = add i32 %tmp1, 42
; Wrap result back into record
%tmp3 = insertvalue %s32 undef, i32 %tmp2, 0
```

#### Generic Type Instantiation
1. `InstantiateGenericRecord` queues types instead of generating immediately
2. `GeneratePendingTypeInstantiations` uses while loop to process queue
3. Handles recursive dependencies (e.g., `Range<BackIndex<uaddr>>`)
4. All types generated before any functions

#### Mutation Inference (Planned)
1. Direct mutation detection - Walk AST for field writes
2. Call graph construction - Track who-calls-whom
3. Transitive propagation - Topological sort + marking
4. Metadata caching - Store in TypeInfo, cache in compiled artifacts

---

### Related Documentation

**Language Design:**
- [GENERICS-STATUS.md](GENERICS-STATUS.md) - Comprehensive generics analysis
- [BUG-12.13-ANALYSIS.md](BUG-12.13-ANALYSIS.md) - Generic method template matching
- [RazorForge-Protocols.md](../wiki/RazorForge-Protocols.md) - Protocol system spec
- [Modules-and-Imports.md](../wiki/Modules-and-Imports.md) - Module system (NaaM)

**Memory Model:**
- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md) - Token types and iteration
- [RazorForge Concurrency Model](../wiki/RazorForge-Concurrency-Model.md) - Multi-threaded iteration
- [RazorForge Routines](../wiki/RazorForge-Routines.md) - Method declarations

**Code Style:**
- [RazorForge Code Style](../wiki/RazorForge-Code-Style.md) - Style guide
- [Suflae Code Style](../wiki/Suflae-Code-Style.md) - Suflae style guide

**Suflae-Specific:**
- [Suflae Eternal](../wiki/Suflae-Eternal.md) - `eternal` keyword
- [Resident vs Eternal](../wiki/Resident-vs-Eternal.md) - Comparison

---

## RECENTLY COMPLETED WORK

### Generic Type System (2025-12-04)
✅ **MAJOR MILESTONE** - End-to-end generics fully working!
- Generic type instantiation (`TestType<s64>`)
- Generic method calls (`instance.method()`)
- Template matching system (GenericTypeResolver.cs)
- Full type safety with compilation

**See:** [GENERICS-STATUS.md](GENERICS-STATUS.md), [BUG-12.13-ANALYSIS.md](BUG-12.13-ANALYSIS.md)

---

### Record Type System Consistency (2025-12-06)
✅ Systematic fixes ensuring ALL types use record wrappers (`%s32`, `%u64`, etc.)
- Core type mapping consistency
- When expression return values
- Bitcast intrinsic
- Decimal type mappings
- Arithmetic operations
- Boolean operations
- Integer literal wrapping

---

### Core Prelude Auto-Loading (2025-12-08)
✅ Automatic loading of all `namespace core` files
- No more `import s64` or `import Maybe`
- Transitive dependency loading
- Recursive stdlib scanning
- Clean user code

**Files:** `src/Analysis/CorePreludeLoader.cs` (NEW)

---

### Comprehensive Code Generator Refactoring (2025-12-08)
✅ **MAJOR OVERHAUL** - Numerous edge cases and enhancements
- Location tracking with `UpdateLocation()` method (Bug 12.14 fix)
- Generic type full name handling
- Type conversion and casting
- Binary expression enhancements
- Floating-point constant handling
- Function generation improvements
- Generic template matching
- Method call enhancements
- Native function declaration tracking
- Record field type tracking

---

### LLVM ABI and Control Flow Fixes (2025-12-08)
✅ Multi-field struct argument passing
✅ Control flow termination
✅ Generic entity method instantiation
✅ Proper stack allocation for struct values used as pointers

---

### Inline Conditional Restrictions (2025-12-08)
✅ Parser restrictions enforced
✅ No block syntax in inline conditionals
✅ No nested inline conditionals
✅ Clear compile errors
✅ Applied to both RazorForge and Suflae
