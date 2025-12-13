# RazorForge Compiler TODO

**Last Updated:** 2025-12-13

This document tracks compiler features needed for RazorForge and Suflae. For standard library implementation tasks,
see [STDLIB_TODO.md](STDLIB_TODO.md).

---

## 🎉 Generics Implementation - ✅ CORE FUNCTIONALITY COMPLETE!

**Status:** ✅ **MAJOR MILESTONE ACHIEVED (2025-12-04)** - Basic generics fully working!

**What This Means:** RazorForge now has working generics for most use cases:

- ✅ Can use generic types: `List<T>`, `Text<T>`, custom `MyType<T>`
- ✅ Can call generic methods: `instance.get_value()` on `MyType<s64>`
- ✅ Can instantiate generic types: `TestType<s64>(value: 42)`
- ✅ Type-safe compilation with full type checking
- ✅ **Stdlib development unblocked!**

**See:** [GENERICS_STATUS.md](GENERICS_STATUS.md) for comprehensive analysis of the generics implementation.

### What Works ✅

1. **Generic function monomorphization** - `identity<T>`, `swap<T>` work
2. **Namespace-qualified generic methods** - `Console.show<T>` parses correctly
3. **Nested generic type parsing** - `List<List<s32>>`, `Range<BackIndex<uaddr>>` parse correctly
4. **Generic function templates** - Stored and deferred correctly
5. **✨ Generic type instantiation** - `TestType<s64>(...)` works! (NEW)
6. **✨ Generic method calls** - `instance.method()` works! (NEW)
7. **✨ Template matching** - Concrete types match templates automatically (NEW)

### Remaining Generics Work (Non-Critical)

#### 1. Generic Method Template Matching (Bug 12.13) - **✅ FULLY COMPLETE!**

**Status:** ✅ **FULLY RESOLVED (2025-12-04)** - End-to-end generics working!

**See:** [BUG_12.13_ANALYSIS.md](BUG_12.13_ANALYSIS.md) for comprehensive analysis.

**What Now Works:** Methods on generic types compile and execute correctly!

```razorforge
record TestType<T> {
    value: T
}

routine TestType<T>.get_value(me: TestType<T>) -> T {
    return me.value
}

routine start() {
    let instance: TestType<s64> = TestType<s64>(value: 42_s64)  # ✅ Works!
    let result: s64 = instance.get_value()  # ✅ Works!
}
```

**Result:** `✅ Compilation successful!`

**Complete Implementation (7 components):**

1. ✅ **Template Matching System** - `src/Analysis/GenericTypeResolver.cs` (NEW FILE, 256 lines)
    - Pattern matching: extracts base names and type arguments
    - Instance matching: `TestType<s64>` → `TestType<T>` with `{T: "s64"}`
    - Type substitution: replaces template parameters with concrete types
    - Candidate generation: searches for method templates in symbol table

2. ✅ **Semantic Analyzer** - `src/Analysis/SemanticAnalyzer.Calls.cs:271`
    - Fixed to use `FullName` (e.g., "TestType<s64>") instead of `Name` (e.g., "TestType")
    - Correctly resolves method return types through template substitution

3. ✅ **BuildFullTypeName Helper** - `src/CodeGen/LLVMCodeGenerator.cs:822-844`
    - Recursively constructs full generic type names from TypeExpression AST nodes
    - Handles nested generics: `List<List<s32>>`

4. ✅ **Variable Type Tracking** - `src/CodeGen/LLVMCodeGenerator.Expressions.cs:29-71`
    - Stores full generic type names (not just base names) in symbol table
    - Preserves type arguments through code generation

5. ✅ **Generic Constructor Calls** - `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs:93-105`
    - Handles `TestType<s64>(...)` constructor syntax
    - Instantiates generic records and creates structs

6. ✅ **Generic Method Resolution** - `src/CodeGen/LLVMCodeGenerator.Expressions.cs:937-995`
    - Uses `ResolvedType.FullName` to preserve generic type info
    - Finds and instantiates generic method templates before calling

7. ✅ **Type Substitution in Functions** - `src/CodeGen/LLVMCodeGenerator.Functions.cs:98-120`
    - Applies type parameter substitutions to receiver types
    - Correctly generates `me: TestType<s64>` from template `me: TestType<T>`

**Files Modified:**

- ✅ `src/Analysis/GenericTypeResolver.cs` - NEW FILE (complete template system)
- ✅ `src/Analysis/SemanticAnalyzer.Calls.cs` - Use FullName for matching
- ✅ `src/CodeGen/LLVMCodeGenerator.cs` - Added BuildFullTypeName()
- ✅ `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Multiple full-type tracking fixes
- ✅ `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - Generic constructor handling
- ✅ `src/CodeGen/LLVMCodeGenerator.Functions.cs` - Type substitution in parameters

**Impact:**

- ✅ Can write, type-check, AND COMPILE generic code end-to-end
- ✅ Generic records work: `TestType<s64>`, `TestType<Text>`, etc.
- ✅ Generic methods work: `instance.get_value()` on `TestType<s64>`
- ✅ Generic constructors work: `TestType<s64>(value: 42)`
- ✅ Template matching works: automatic resolution of concrete types to templates
- ✅ Stdlib generic types should now work: `Range<T>`, `BackIndex<I>`, etc.
- ✅ **STDLIB DEVELOPMENT FULLY UNBLOCKED**

---

#### 2. Generic Record/Entity Instantiation - **SECOND PRIORITY**

**Problem:** Generic types are not fully instantiated.

```razorforge
record List<T> {
    data: DynamicSlice
    length: uaddr
}

let list: List<s32> = List<s32>()  # Partially works but fragile
```

**Root Cause:**

- Struct definitions generated immediately, not deferred
- No dependency ordering (must generate `Node<T>` before `List<Node<T>>`)
- Circular dependencies not handled (`Node<T>` contains `Node<T>`)
- Fields may reference types that don't exist yet

**Solution Needed:**

1. Two-pass generation: collect all needed instantiations first
2. Build dependency graph (topological sort)
3. Handle recursive types with forward declarations
4. Generate all struct definitions before any functions

**Files:**

- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - `InstantiateGenericRecord()` line ~245
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - `InstantiateGenericEntity()` line ~290

---

#### 3. Generic Function Overload Resolution - **THIRD PRIORITY**

**Problem:** Cannot choose between generic and non-generic versions.

```razorforge
routine Console.show(value: Text<letter8>) {
    # Non-generic version
}

routine Console.show<T>(value: T) {
    # Generic version - CANNOT COEXIST!
}

Console.show(42)  # Which one to call?
```

**Solution Needed:**

1. Type inference: Deduce `T=s32` from argument
2. Constraint checking: Verify `s32` implements required protocols
3. Specificity rules: Prefer non-generic over generic when both match
4. Generate meaningful errors for ambiguous calls

**Files:**

- `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - `VisitCallExpression()`
- Need new: `ResolveOverload()` method

---

#### 4. Generic Constraints - **FOURTH PRIORITY**

**Problem:** Cannot express type requirements.

```razorforge
routine max<T: Comparable>(a: T, b: T) -> T {
    # Need to ensure T has comparison operators
}

routine show<T: Printable>(value: T) {
    # Need to ensure T has to_text() method
}
```

**Solution Needed:**

1. Parser: Recognize `<T: Protocol>` syntax
2. Parser: Handle `where T: T follows Protocol` clauses
3. Semantic Analyzer: Validate constraints during instantiation
4. Code Generator: Check protocol membership before generating code

**Files:**

- `src/Parser/RazorForgeParser.cs` - Extend generic parameter parsing
- `src/Analysis/SemanticAnalyzer.Declarations.cs` - Constraint validation

---

### Implementation Roadmap

**✅ COMPLETED (2025-12-04): Bug 12.13 - Full Generic Method Template Matching**

- [x] Created `GenericTypeResolver.cs` with pattern matching utilities
- [x] Implemented template candidate generation
- [x] Implemented type parameter substitution
- [x] Fixed `GetKnownMethodReturnType()` to use `FullName`
- [x] Fixed variable type tracking to preserve generic arguments
- [x] Fixed generic constructor calls in code generator
- [x] Fixed generic method resolution in code generator
- [x] Fixed type substitution in function parameter generation
- [x] Test with simple generic types - PASSING ✅
- [x] End-to-end compilation test - PASSING ✅

**Next Priority: Generic Record Instantiation (Item #2 above)**

- [ ] Implement two-pass struct generation
- [ ] Build type dependency graph
- [ ] Topological sort for generation order
- [ ] Handle recursive types (forward declarations)
- [ ] Test with `List<T>`, `Node<T>`, nested types

**Future Work: Overload Resolution (Item #3 above)**

- [ ] Implement type inference for generic calls
- [ ] Implement specificity ranking (non-generic > generic)
- [ ] Handle multiple generic candidates
- [ ] Test with `Console.show<T>` vs `Console.show(Text)`

**Future Work: Generic Constraints (Item #4 above)**

- [ ] Parse `<T: Protocol>` syntax
- [ ] Parse `where` clauses
- [ ] Validate constraints during instantiation
- [ ] Generate constraint violation errors
- [ ] Test with `max<T: Comparable>`, `sort<T: Comparable>`

---

## 2. Method Mutation Inference (CRITICAL FOR TOKEN SYSTEM)

**Priority:** 🔴 **CRITICAL** (blocks token API design)

**Status:** ❌ **NOT STARTED**

### Overview

The token system (`Viewed<T>`, `Hijacked<T>`, `Inspected<T>`, `Seized<T>`) requires the compiler to know which methods mutate `me` (self-modifying) and which don't. This enables:

1. **No API duplication** - Single method works with both readonly and mutable tokens
2. **Automatic token propagation** - Return types adapt based on receiver token type
3. **No const correctness** - No viral annotation spreading through call chains

### The Problem

Without knowing which methods mutate, we face three bad options:
- **API fragmentation**: Different methods available on `Viewed<T>` vs `Hijacked<T>`
- **API duplication**: `get()`/`get_mut()` pattern everywhere
- **Runtime failures**: All methods compile but some panic at runtime

### The Solution: Automatic Inference

**Compiler automatically infers** which methods mutate by analyzing:
1. **Direct field writes** - `me.field = value` (cheap, AST walk)
2. **Call graph** - If A calls mutating B, then A is mutating (topological sort)
3. **Cross-module caching** - Store inference results in compiled artifacts

**No manual annotations needed!** Methods just work.

### Example Behavior

```razorforge
entity Counter {
    var value: s32
}

# Compiler infers: NOT mutating (only reads)
routine Counter.get_value() -> s32 {
    return me.value
}

# Compiler infers: mutating (writes field)
routine Counter.increment() {
    me.value += 1
}

# Compiler infers: mutating (calls mutating method)
routine Counter.double_increment() {
    me.increment()  # Calls mutating method, so this is mutating too
    me.increment()
}

# Usage with tokens
let counter = Counter(value: 0)

viewing counter as v {
    show(v.get_value())  # ✅ Works - non-mutating method
    v.increment()        # ❌ Compile error: increment() is mutating
}

hijacking counter as h {
    h.increment()        # ✅ Works - all methods available
    show(h.get_value())  # ✅ Works - non-mutating methods also work
}
```

### Automatic Return Type Adaptation

Methods returning `T` automatically wrap based on receiver:

```razorforge
entity List<T> {
    var _buffer: pointer
}

routine List<T>.get!(index: uaddr) -> T {
    danger! { return @load(_buffer, index) }
}

# Single method definition, but return type adapts:
viewing list as v {
    let item = v.get!(0)  # item is Viewed<T> (readonly propagates)
}

hijacking list as l {
    let item = l.get!(0)  # item is Hijacked<T> (mutable propagates)
}
```

### Implementation Requirements

**Analysis Phase (Semantic Analyzer):**

1. **Direct mutation detection** (O(method body size))
   ```csharp
   // Detect field writes in method bodies
   private bool CheckForFieldMutations(RoutineDeclaration routine)
   {
       // Walk AST, look for BinaryExpression with = operator where left is FieldAccess
   }
   ```

2. **Call graph construction** (O(methods + calls))
   ```csharp
   // Build who-calls-whom graph
   private Dictionary<string, HashSet<string>> BuildCallGraph()
   {
       // For each method, track which methods it calls
   }
   ```

3. **Transitive propagation** (O(call graph size))
   ```csharp
   // Topological sort + marking
   private void PropagateNutationInfo(Dictionary<string, HashSet<string>> callGraph)
   {
       // If method calls mutating method, mark as mutating
   }
   ```

4. **Metadata caching**
   ```csharp
   // Store in TypeInfo or separate dictionary
   public Dictionary<string, bool> MethodMutationInfo = new();

   // Cache in compiled artifacts for cross-module use
   ```

**Code Generation Phase:**

1. **Token type enforcement**
   ```csharp
   // When generating method call on Viewed<T> or Inspected<T>
   if (receiverIsReadonly && methodIsMutating)
   {
       Error("Cannot call mutating method through readonly token");
   }
   ```

2. **Return type wrapping**
   ```csharp
   // When method returns T and receiver is Viewed<T>
   // Wrap return value in Viewed<T>
   ```

### Performance Impact

| Analysis               | Complexity                    | Cost Estimate      |
|------------------------|-------------------------------|--------------------|
| Direct mutation detect | O(method bodies)              | ~2-5% compile time |
| Call graph build       | O(methods + calls)            | ~1-2% compile time |
| Transitive propagation | O(call graph)                 | ~1-2% compile time |
| **Total**              | Similar to type inference     | **~5-10% total**   |

**Space cost:** 1 bit per method (mutating: yes/no)

### Files To Modify

**New File:**
- `src/Analysis/MutationAnalyzer.cs` - Core analysis logic

**Modified Files:**
- `src/Analysis/SemanticAnalyzer.cs` - Integrate mutation analysis pass
- `src/Analysis/TypeInfo.cs` - Add mutation info storage
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Token type enforcement
- `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - Return type wrapping

### Benefits

✅ **No const correctness** - No viral annotations spreading
✅ **No API duplication** - Single `get()` method, no `get_mut()`
✅ **No API fragmentation** - Different enforcement, not different APIs
✅ **Compile-time safe** - Errors at call site, not runtime
✅ **Automatic propagation** - Return types adapt based on receiver
✅ **Developer friendly** - Just write methods normally
✅ **Separate compilation** - Cached in module metadata

### Trade-offs

⚠️ **Not visible in source** - Mutation status only in IDE/docs/errors
⚠️ **Compilation cost** - Adds ~5-10% to compile time
⚠️ **Cross-module metadata** - Need to store/load inference results

### Next Steps

1. Implement `MutationAnalyzer.cs` with direct mutation detection
2. Add call graph construction
3. Add transitive propagation
4. Integrate into semantic analysis pass
5. Add token type enforcement in code generation
6. Add return type wrapping for token propagation
7. Test with stdlib types (List, Text, etc.)
8. Document in wiki how inference works

**See Also:**
- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md) - Token types
- [RazorForge Routines](../wiki/RazorForge-Routines.md) - Method declarations

---

## 3. Native Runtime Library (BLOCKING EXECUTION)

**Priority:** 🔴 **HIGH** (but doesn't block development)

**Status:** ❌ **NOT STARTED**

The compiler generates valid LLVM IR but cannot link executables due to missing native functions.

**Error:**

```
Clang error: clang: error: linker command failed with exit code 1120
```

**Missing Functions:**

- Text formatting: `format_s64`, `format_s32`, `format_f64`, etc.
- Stack trace runtime: `__rf_init_symbol_tables`, `__rf_stack_push`, `__rf_stack_pop`
- Console I/O: `rf_console_print_cstr`, `rf_console_get_line`, etc.
- Memory management: `rf_memory_alloc`, `rf_memory_free`, `rf_memory_copy`

**Next Steps:**

1. Create `native/runtime.c` with implementations
2. Update build system to compile and link runtime.c
3. Test end-to-end: source → IR → executable → run

**Files:**

- `native/runtime.c` (create)
- `native/build.sh` (update)
- Build system integration

---

## 3. Module System & Core Prelude (MEDIUM PRIORITY)

**Priority:** 🟡 **MEDIUM**

**Status:** ⏳ **PARTIALLY IMPLEMENTED** (parsing works, resolution doesn't)

### Current State

- `import` statement parsing works
- Modules not resolved or loaded
- Symbol table not populated from imports
- **Core prelude not implemented** - needs automatic loading

### Design Decisions (Finalized)

1. **Search Order:** `stdlib/` → project root → external packages
2. **Import Style:** Unqualified access - `import Collections/List` lets you use `List<T>` directly
3. **Selective Imports:** `import Collections/{List, Dict}`
4. **Namespace Declaration:** `namespace MyNamespace` overrides folder path
5. **Implicit Namespaces:** No namespace declaration = file path becomes namespace
6. **Core Prelude:** `core` namespace automatically loaded in every file (no import needed)
7. **Loading Strategy:** Eager - load all transitive imports before semantic analysis

### Core Prelude Auto-Loading (NEW FEATURE)

**Requirement:** All files in `stdlib/core/` namespace must be automatically loaded without requiring import.

**Core namespace contents (all marked with `namespace core`):**

- **Primitives:** s8-s128, u8-u128, saddr, uaddr, f16-f128, d32-d128, bool, Blank
- **Letters:** letter8, letter16, letter32
- **Error handling:** Maybe, Result, Lookup, Crashable, Error
- **Memory:** DynamicSlice, MemorySize
- **FFI:** cstr, cint, etc.
- **Utilities:** BackIndex, Range, Duration, Integral (protocol)

**Implementation needs:**

```csharp
// In SemanticAnalyzer initialization
private void LoadCorePrelude()
{
    // Automatically load all files in stdlib/core/ namespace
    var coreFiles = Directory.GetFiles(stdlibPath + "/core", "*.rf", SearchOption.AllDirectories);
    foreach (var file in coreFiles)
    {
        var module = LoadModule(file);
        // Add to symbol table without requiring import
    }
}
```

**Usage example:**

```razorforge
# No import needed for core types!
routine start() {
    let x: s64 = 42                    # ✅ core - always available
    let maybe: Maybe<u64> = Some(100)  # ✅ core - always available
    let slice: DynamicSlice = ...      # ✅ core - always available

    # But non-core types still need imports
    import Console                     # ❌ Not core - must import
    Console.show("Hello!")
}
```

### Implementation Tasks

- [ ] **Core Prelude Loader** - Auto-load all `stdlib/core/` files at startup
- [ ] **Implicit Namespace Detection** - If no `namespace` declaration, use file path
- [ ] **ModuleResolver** - Resolve import paths to file paths
- [ ] **Stdlib Search Hierarchy** - `--stdlib` flag → source dir → compiler dir → env var
- [ ] **ModuleCache** - Track loaded modules to avoid re-parsing
- [ ] **Symbol Table Population** - Add imported symbols to scope
- [ ] **Collision Detection** - Error on duplicate names
- [ ] **Transitive Loading** - Load dependencies recursively
- [ ] **Circular Import Detection** - Track import stack

**Files:**

- `src/ModuleSystem/ModuleResolver.cs` (create/enhance)
- `src/ModuleSystem/CorePreludeLoader.cs` (create)
- `src/ModuleSystem/ModuleCache.cs` (create)
- `src/Analysis/SemanticAnalyzer.cs` - Integrate module resolution and core prelude

**See Also:** [Modules-and-Imports.md](../wiki/Modules-and-Imports.md) for complete Namespace-as-a-Module (NaaM)
documentation

---

## 4. Protocol System (MEDIUM-HIGH PRIORITY)

**Priority:** 🟠 **MEDIUM-HIGH** (needed for constraints)

**Status:** ⏳ **INFRASTRUCTURE EXISTS, IMPLEMENTATION INCOMPLETE**

### Current State

- `WellKnownProtocols` class defines standard protocols
- `TypeInfo.Protocols` tracks protocol membership
- Primitive types have correct protocols assigned
- **Missing:** Protocol declarations, implementation checking, constraint validation

### Needed Parser Features

```razorforge
# Protocol declaration
protocol Printable {
    routine to_text() -> Text<letter32>
}

# Protocol implementation
record Point follows Printable, Hashable {
    x: f32
    y: f32

    routine to_text() -> Text<letter32> { ... }
    routine hash() -> u64 { ... }
}

# Generic constraints
routine show<T: Printable>(value: T) { ... }
```

### Implementation Tasks

- [ ] Parse `protocol` keyword and declaration syntax
- [ ] Parse `follows` keyword in type declarations
- [ ] Parse `<T: Protocol>` constraint syntax
- [ ] Parse `where T: T follows Protocol` clauses
- [ ] Semantic analysis: Verify protocol methods are implemented
- [ ] Semantic analysis: Check constraints during generic instantiation
- [ ] Code generation: Generate protocol witness tables (if needed)

**Files:**

- `src/Parser/RazorForgeParser.cs` - Add protocol parsing
- `src/Analysis/SemanticAnalyzer.Declarations.cs` - Protocol validation

---

## 5. Missing Parser Features (MEDIUM PRIORITY)

**Priority:** 🟡 **MEDIUM**

### 5.1 External Declarations (FFI)

```razorforge
external("C") routine printf(format: cstr, ...) -> cint
external("C") routine malloc(size: uaddr) -> uaddr
```

**Tasks:**

- [ ] Parse `external` keyword with calling convention string
- [ ] Parse function signature without body
- [ ] Handle variadic `...` parameter

### 5.2 Preset (Compile-Time Constants)

```razorforge
preset PI: f64 = 3.14159265359
preset MAX_SIZE: uaddr = 1024
```

**Tasks:**

- [ ] Parse `preset` keyword
- [ ] Store in symbol table as compile-time constant
- [ ] Inline at use sites

### 5.3 Common (Static Methods)

```razorforge
record Point {
    common routine origin() -> Point {
        return Point(x: 0.0, y: 0.0)
    }
}
```

**Tasks:**

- [ ] Parse `common` modifier before `routine`
- [ ] Mark as static/class-level in AST
- [ ] Code generation: Don't require receiver

### 5.4 Inheritance Control Keywords

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

### 5.5 Scoped Access Syntax

```razorforge
hijacking doc as d { d.edit() }
viewing doc as d { print(d.title) }
seizing mutex as m { m.value = 10 }
```

**Tasks:**

- [ ] Parse `hijacking`, `viewing`, `seizing`, `inspecting`, `using`
- [ ] Parse `as` binding
- [ ] Semantic analysis: Verify access semantics
- [ ] Code generation: Generate locking/unlocking code

**Files:**

- `src/Lexer/Lexer.cs` - Add keywords
- `src/Parser/RazorForgeParser.cs` - Parse syntax

### 5.6 String Literal Enhancements

**Status:** ⏳ **PARTIALLY IMPLEMENTED** (regular and raw strings work, backslash continuation missing)

#### Current State

- ✅ Regular multi-line strings: `"Line 1\nLine 2"` (newlines become `\n`)
- ✅ Raw strings: `r"C:\Users\Path"` (no escape interpretation)
- ❌ **Backslash continuation:** `"Long text \<newline>continues"` - **NOT IMPLEMENTED**

#### Backslash Continuation (NEEDED for 80-char line limit)

**Purpose:** Allow long strings to span multiple source lines without introducing newlines in the result.

**Syntax:**

```razorforge
# ✅ Backslash at end of line - removes newline and leading whitespace
let error = "Error: The connection to the database failed \
because the credentials provided were invalid or the \
server is offline."
# Result: "Error: The connection to the database failed because the credentials provided were invalid or the server is offline."

# ✅ Space before \ is preserved
let msg = "Word1 \
Word2"
# Result: "Word1 Word2"

# ❌ No space before \ - words run together
let bad = "Word1\
Word2"
# Result: "Word1Word2"

# ✅ Works with escape sequences
let formatted = "Line 1.\t\
Line 2 continues on same output line."
# Result: "Line 1.\tLine 2 continues on same output line."
```

**String Literal Types:**

| Type | Newlines | Escape Sequences | Backslash Continuation |
|------|----------|------------------|------------------------|
| `"..."` | Preserved as `\n` | Interpreted | **TO IMPLEMENT** |
| `r"..."` | Preserved as `\n` | **NOT** interpreted | N/A (raw = no escapes) |

**Implementation Tasks:**

- [ ] **Lexer:** Detect `\` followed by newline in string literals
- [ ] **Lexer:** Remove the `\`, newline, and leading whitespace from next line
- [ ] **Lexer:** Join string segments into single token
- [ ] **Parser:** No changes needed (single token from lexer)
- [ ] **Code Generation:** No changes needed (already handles string literals)
- [ ] **Test:** Various continuation scenarios (middle of string, multiple continuations, with escapes)

**Example Implementation (Lexer pseudocode):**

```csharp
private string ProcessStringLiteral(char quote, bool isRaw)
{
    StringBuilder result = new StringBuilder();

    while (CurrentChar != quote)
    {
        if (CurrentChar == '\\' && !isRaw)
        {
            char next = PeekChar();

            if (next == '\n')  // Backslash continuation
            {
                Advance();  // Skip \
                Advance();  // Skip \n

                // Skip leading whitespace on next line
                while (CurrentChar == ' ' || CurrentChar == '\t')
                    Advance();

                continue;  // Don't append anything
            }
            else  // Regular escape sequence
            {
                result.Append(ProcessEscapeSequence());
            }
        }
        else
        {
            result.Append(CurrentChar);
            Advance();
        }
    }

    return result.ToString();
}
```

**Files:**

- `src/Lexer/BaseTokenizer.cs` - Implement backslash continuation in string literal tokenization

**See Also:**

- [RazorForge Code Style - Line Length Limit](../wiki/RazorForge-Code-Style.md#line-length-limit)
- [Suflae Code Style - Line Length Limit](../wiki/Suflae-Code-Style.md#line-length-limit)

### 5.7 `dedent()` Built-in Function

**Status:** ❌ **NOT IMPLEMENTED**

**Purpose:** Strip common leading whitespace from multi-line strings to allow indented string literals in source code.

**Use Case:**

```razorforge
routine show_help() {
    # String is indented for code readability, but we want to strip the indentation
    let message = dedent("
        Welcome to RazorForge!

        This is a multi-line help message.
        Each line is indented in the source code for readability.

        Commands:
          build  - Build the project
          run    - Run the project
          test   - Run tests
        ")

    show(message)
}

# Output (leading indentation stripped, but internal indentation preserved):
# Welcome to RazorForge!
#
# This is a multi-line help message.
# Each line is indented in the source code for readability.
#
# Commands:
#   build  - Build the project
#   run    - Run the project
#   test   - Run tests
```

**Behavior:**

1. Find the common leading whitespace across all non-empty lines
2. Remove that common whitespace from each line
3. Preserve relative indentation (e.g., the "build/run/test" lines stay indented)
4. Remove leading and trailing blank lines

**Example:**

```razorforge
# Source code (indented for readability)
let sql = dedent("
    SELECT users.name, orders.total
    FROM users
    JOIN orders ON users.id = orders.user_id
    WHERE orders.status = 'completed'
    ORDER BY orders.total DESC
    ")

# Result (common leading whitespace removed):
# "SELECT users.name, orders.total
# FROM users
# JOIN orders ON users.id = orders.user_id
# WHERE orders.status = 'completed'
# ORDER BY orders.total DESC"
```

**Comparison with Backslash Continuation:**

```razorforge
# Backslash continuation - manual line breaking, no indentation
let msg1 = "This is a long message that \
spans multiple lines in source code."

# dedent() - preserves structure, strips common indentation
let msg2 = dedent("
    This is a long message that
    spans multiple lines in source code.
    ")

# Both produce same result, but dedent() is more readable for multi-line text
```

**Implementation Tasks:**

- [ ] **Add to stdlib:** Implement as `Text.dedent(me: Text) -> Text` in `stdlib/Text/Text.rf`
- [ ] **Algorithm:**
  1. Split string into lines
  2. Find minimum leading whitespace (ignoring empty lines)
  3. Remove that amount of whitespace from each line
  4. Strip leading/trailing blank lines
  5. Join lines back together
- [ ] **Test cases:**
  - Empty string
  - Single line
  - Multiple lines with same indentation
  - Multiple lines with different indentation
  - Lines with tabs vs spaces
  - Blank lines (should be ignored when finding common indent)

**Alternative Design - String Literal Syntax:**

Could also implement as a string literal prefix (like raw strings):

```razorforge
# Option A: Function (more flexible)
let msg = dedent("
    Line 1
    Line 2
    ")

# Option B: Literal prefix (more concise, but less flexible)
let msg = d"
    Line 1
    Line 2
    "

# Option C: Triple-quote syntax (Python-like)
let msg = """
    Line 1
    Line 2
    """
```

**Recommendation:** Start with **function approach** (Option A) as it's:
- More explicit
- Can be called on any string (not just literals)
- Easier to implement (no parser changes)
- Can be added to stdlib without compiler changes

**Files:**

- `stdlib/Text/Text.rf` - Add `dedent()` method
- Test file for `dedent()` functionality

**See Also:**

- Python's `textwrap.dedent()`
- Kotlin's `trimIndent()`
- Rust's indoc crate

---

## 6. Lambda Code Generation (MEDIUM PRIORITY)

**Priority:** 🟡 **MEDIUM**

**Status:** ⏳ **PARSING WORKS, CODEGEN INCOMPLETE**

### Current State

- Arrow lambda parsing works: `x => x * 2`, `(a, b) => a + b` (parentheses optional for single param)
- AST nodes created correctly
- Code generation not implemented

### Language Design Restriction

**⚠️ IMPORTANT**: Multiline lambdas are **BANNED** in both RazorForge and Suflae.

```razorforge
# ✅ OK: Single expression lambdas
let double = x => x * 2                # Parentheses optional for single param
let sum = (a, b) => a + b              # Parentheses required for multiple params
let result = list.select(x => x * 2)  # Clean and concise

# ❌ BANNED: Multiline lambdas
let compute = x => {
    let temp = x * 2
    temp + 1
}
```

**Rationale**: Multiline lambdas add complexity without clear benefits. Use named functions for multi-statement logic.

### Needed

```razorforge
let double = x => x * 2
let result = list.select(x => x * 2)
```

**Tasks:**

- [ ] Generate function pointer type for lambda
- [ ] Create anonymous function in LLVM IR
- [ ] Handle captures (closure semantics)
- [ ] Implement `Routine<(T), U>` type properly
- [ ] Enforce single-expression restriction in parser (reject block syntax)

**Files:**

- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - `VisitLambdaExpression()`
- `src/Parser/RazorForgeParser.Expressions.cs` - Add lambda block syntax validation

---

## 7. Known Code Generation Bugs

### 7.1 Type Constructor Calls (Bug 12.7) - OPEN

```razorforge
routine u8.to_saddr(me: u8) -> saddr {
    return saddr(me)  # Generated as function call, not type conversion!
}
```

**Generated (wrong):**

```llvm
%tmp2 = call i32 @saddr(i8 %me)  ; Function doesn't exist!
```

**Expected:**

```llvm
%tmp2 = sext i8 %me to i64
```

**Fix:** Detect type constructor calls and generate appropriate conversion intrinsics.

---

### 7.2 Integer Literal Type Inference (Bug 12.10) - OPEN

```razorforge
routine double_it(x: s32) -> s32 {
    return x * 2  # Error: s32 * s64 (literal defaults to s64)
}
```

**Fix:** Propagate expected type to literal expressions during semantic analysis.

---

### 7.3 Function Call Return Type (Bug 12.11) - OPEN

```razorforge
routine get_value() -> s64 { return 42 }
let x: s64 = get_value()  # Type mismatch: expected s64, got i32
```

**Fix:** Look up actual return type from function signature, don't default to i32.

---

### 7.4 Dunder Method Variant Names (Bug 12.12) - OPEN

```razorforge
routine s32.__add__!(other: s32) -> s32 { ... }
# Should generate: try_add, check_add
# Currently generates: try___add__, check___add__
```

**Fix:** Strip `__` prefix/suffix before adding variant prefix.

---

### 7.5 Error Location Tracking (Bug 12.14) - OPEN

**Problem:** Error messages show wrong file and line numbers, referencing intermediate output files instead of source
files.

**Example:**

```
Error at playground/main.rf:27:46
```

But `main.rf` only has 9 lines! The error is actually referencing the SimpleCodeGenerator output file (`main.out`).

**Root Cause:**

- `_currentFileName` and `_currentLocation` in code generator are set once at start
- Not updated when processing imported modules or different AST nodes
- Error reporting uses these stale values

**Solution:**

- Track file/line info from AST node locations (`node.Location`)
- Update `_currentFileName` and `_currentLocation` when processing each node
- Use AST node location info in all error messages

**Files:**

- `src/CodeGen/LLVMCodeGenerator.cs` - Error reporting methods
- All `LLVMCodeGenerator.*.cs` partial class files - Update location tracking

---

### 7.6 Optional Field Syntax (Bug 12.17) - OPEN

**Problem:** Parser rejects `?` syntax for optional/nullable fields in entity and record declarations.

**Example:**

```razorforge
entity SortedDict<K, V> {
    private root: BTreeDictNode<K, V>?  # Parse error: Expected Greater, got Question
    private count: uaddr
}

entity SortedSet<T> {
    private root: BTreeSetNode<T>?  # Parse error: Expected Greater, got Question
    private count: uaddr
}
```

**Current Behavior:**

- Parser fails with "Unexpected token: Question" when `?` appears after type in field declarations
- Forces developers to work around by removing `?` and handling null checks manually

**Expected Behavior:**

- `?` suffix should indicate optional/nullable field
- Parser should accept `Type?` syntax in field declarations
- Type system should track nullability information
- Code generator should handle nullable field semantics

**Root Cause:**

- Parser's field declaration parsing doesn't recognize `?` as a valid type suffix
- Type expression parsing may need enhancement to support optional type modifier

**Solution Needed:**

1. Extend `ParseTypeExpression()` to recognize and parse `?` suffix for optional types
2. Add `IsOptional` flag to `TypeExpression` AST node
3. Update semantic analyzer to track nullability in type information
4. Update code generator to handle nullable field semantics (pointer vs value, null checks)

**Impact:**

- Currently blocks expressive nullable field declarations
- Developers must use workarounds (e.g., separate boolean flag, or accepting that `None` is valid)
- Entity references are implicitly nullable, but explicit `?` syntax would be clearer

**Workaround:**

- Remove `?` from field declarations
- Entity references are nullable by default without explicit `?`
- Use `is None` checks to test for null values

**Files:**

- `src/Parser/RazorForgeParser.Expressions.cs` - Type expression parsing
- `src/AST/ASTNode.cs` - Add `IsOptional` to TypeExpression
- `src/Analysis/SemanticAnalyzer.Types.cs` - Track nullability
- `src/CodeGen/LLVMCodeGenerator.Types.cs` - Generate nullable field code

**Priority:** 🟡 **MEDIUM** - Nice to have for clarity, but workarounds exist

---

### 7.7 Module Resolution (Bug 12.15) - OPEN

**Problem:** Import statements fail to resolve modules even with `--stdlib` flag.

**Example:**

```razorforge
import Console
import NativeDataTypes/s64

routine start() {
    Console.show("Hello")  # Error: Module not found
}
```

**Compilation Error:**

```
Semantic error[main.rf:2:1]: Failed to import module 'Console': Module not found: Console
```

**Root Cause:**

- Module resolver not finding stdlib files
- Path resolution may be broken
- `--stdlib` flag not being properly applied

**Solution:**

- Debug `ModuleResolver.cs` to understand path resolution
- Verify stdlib path is correctly passed to module resolver
- Check file path construction for imports

**Files:**

- `src/Analysis/ModuleResolver.cs` - Module path resolution
- `src/CLI/Program.cs` - Command-line argument handling
- `src/Analysis/SemanticAnalyzer.Declarations.cs` - Import processing

---

### 7.7 Pointer Type in Binary Expressions (Bug 12.16) - ✅ FIXED (2025-12-04)

**Problem:** Code generator crashed when encountering pointer types in binary expressions.

**Error:**

```
Failed to resolve type 'i8*' during code generation: unknown LLVM integer type in GetIntegerBitWidth
```

**Root Cause:**

- `GetIntegerBitWidth()` was being called on pointer types like `i8*`
- Function only handles integer types (i8, i16, i32, i64, i128)

**Solution:** ✅ FIXED

- Added pointer type detection before calling `GetIntegerBitWidth()`
- Check if type ends with `*` or equals `ptr`
- Skip integer width comparison for pointer types

**Files Modified:**

- ✅ `src/CodeGen/LLVMCodeGenerator.Expressions.cs:293-298` - Added pointer checks

```csharp
// Check if types are pointers
bool leftIsPointer = operandType.EndsWith(value: "*") || operandType == "ptr";
bool rightIsPointer = rightTypeInfo.LLVMType.EndsWith(value: "*") || rightTypeInfo.LLVMType == "ptr";

if (rightTypeInfo.LLVMType != operandType && !rightTypeInfo.IsFloatingPoint &&
    !leftTypeInfo.IsFloatingPoint && !leftIsPointer && !rightIsPointer)
{
    // Only call GetIntegerBitWidth for non-pointer types
    int leftBits = GetIntegerBitWidth(llvmType: operandType);
    int rightBits = GetIntegerBitWidth(llvmType: rightTypeInfo.LLVMType);
    // ...
}
```

---

## 8. Auto-Generated Methods for Data Types

**Priority:** 🟡 **MEDIUM-LOW**

Compiler should auto-generate default methods for:

### Record

- `to_text()`, `to_debug()`, `memory_size()`, `hash()`, `==`, `!=`, `is`, `isnot`

### Entity

- `to_text()`, `to_debug()`, `memory_size()`, `id()`, `copy()`, `==`, `!=`, `===`, `!==`

### Choice

- `to_text()`, `hash()`, `to_integer()`, `all_cases()`, `from_text()`, `==`, `!=`

### Variant

- `to_debug()`, `hash()`, `is_<case>()`, `try_get_<case>()`, `==`, `!=`

**Files:**

- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Add auto-generation logic

---

## Recently Completed

### Code Generation Fixes (2025-12-04 Session)

12. **Parameter assignment materialization** - Function parameters can be reassigned
13. **Shift intrinsic type conversion** - Shift amounts auto-converted to match value type
14. **When statement label generation** - Pre-allocate labels for correct ordering
15. **Standalone when detection** - Fixed detection to check for `bool true`
16. **Boolean type handling in when clauses** - Use i1 directly without conversion
17. **Expression returns in when clauses** - Generate return statements for expression-only bodies
18. **Text type representation** - Changed from opaque struct to `ptr`
19. **Stack trace runtime declarations** - Added `EmitGlobalDeclarations()` call
20. **Native function auto-declaration** - Auto-declare native functions before use

### Record Type System Consistency (2025-12-06 Session)

**Status:** ✅ **FULLY RESOLVED**

Fixed systematic issues ensuring ALL types consistently use record wrappers (e.g., `%s32`, `%u64`, `%f128`, `%bool`)
instead of bare primitives. The "everything is a record" architecture is now enforced throughout the entire codebase.

**Fixes Applied:**

1. **Core Type Mapping Consistency** - Updated `MapTypeToLLVM` to return record types for ALL primitives
2. **When Expression Return Values** - When clause bodies check value type and cast if needed
3. **Bitcast Intrinsic** - Proper handling of record types with extract → bitcast → wrap pattern
4. **Decimal Type Mappings** - Fixed decimal types to map to their actual record fields (d32→u32, d64→u64, d128→u128)
5. **Arithmetic Right Shift** - Extract, shift, and wrap pattern for record types
6. **Bit Manipulation Intrinsics** - ctpop, ctlz, cttz now extract from record arguments
7. **Boolean NOT Operator** - Extract from `%bool`, XOR, and wrap result
8. **Chained Comparison Value Reuse** - Avoid illegal `add %s32` for record types
9. **Logical AND/OR Short-Circuit** - Extract `i1` from `%bool` before branches
10. **Integer Literal Wrapping** - Wrap integer literals when they have resolved record types

**Key Pattern Established:** The Extract → Operate → Wrap Pattern is now consistently applied:

```llvm
; Extract primitive from record
%tmp1 = extractvalue %s32 %value, 0
; Operate on primitive
%tmp2 = add i32 %tmp1, 42
; Wrap result back into record
%tmp3 = insertvalue %s32 undef, i32 %tmp2, 0
```

**Files Modified:**

- `src/CodeGen/LLVMCodeGenerator.cs` - Core type mapping
- `src/CodeGen/LLVMCodeGenerator.Errors.cs` - When expression returns
- `src/CodeGen/LLVMCodeGenerator.Intrinsics.cs` - Bitcast and bit manipulation intrinsics
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Multiple fixes for decimal types, shift ops, NOT, comparisons,
  logical ops, literals

### Generic Type Instantiation (2025-12-07 Session)

**Status:** ✅ **FULLY RESOLVED**

Fixed issue where generic types like `Maybe<saddr>` were not being instantiated and emitted to LLVM IR when used in
function signatures.

**Root Cause:** Generic type instantiation was happening during function signature generation, writing type definitions
in the middle of the functions section.

**Solution:** Modified to use a **deferred/queued approach**:

1. `InstantiateGenericRecord` adds types to pending queue instead of generating immediately
2. `GeneratePendingTypeInstantiations` uses while loop to process pending types until queue is empty
3. Handles **recursive dependencies** (e.g., `Range<BackIndex<uaddr>>` needs `BackIndex<uaddr>`)
4. Added pending type generation call after processing imported module functions

**Files Modified:**

- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Queued instantiation system
- `src/CodeGen/LLVMCodeGenerator.Functions.cs` - Call to generate pending types

### Module Loading for Transitive Dependencies (2025-12-07 Session)

**Status:** ✅ **FULLY RESOLVED**

Fixed issue where `DataHandle` and `DataState` modules (imported by `Maybe.rf`) were not being loaded/processed by the
code generator.

**Root Causes:**

1. Prelude modules not loading transitive dependencies - `LoadPrelude()` only loaded direct modules
2. Missing namespace declarations - `DataHandle.rf` and `DataState.rf` had no `namespace` declaration

**Solution:**

1. Changed to use `LoadModuleWithDependencies()` which recursively loads all transitive imports
2. Added `namespace core` to DataHandle.rf and DataState.rf

**Files Modified:**

- `src/Analysis/SemanticAnalyzer.cs` - Use LoadModuleWithDependencies in LoadPrelude
- `stdlib/ErrorHandling/DataHandle.rf` - Added namespace core
- `stdlib/ErrorHandling/DataState.rf` - Added namespace core

### Decimal Type Module Loading and Code Generation (2025-12-07 Session)

**Status:** ✅ **FULLY RESOLVED**

Fixed multiple issues with decimal type modules (d32, d64, d128) not being loaded and code generation problems with
multi-field record types.

**Root Causes:**

1. Decimal modules lacking `namespace core` declarations
2. Multi-field record constant initialization trying to wrap single primitive values
3. Nested record type constant initialization inserting primitives instead of records
4. Code generator extracting fields for operators instead of calling dunder methods for multi-field structs
5. Bitcast between decimal types causing type mismatches

**Solution:**

1. Added `namespace core` to all decimal type files
2. Enhanced `VisitPresetDeclaration` to detect multi-field records and use `zeroinitializer`
3. Check actual field types and use `zeroinitializer` for nested record types
4. Modified d128.rf to call external functions explicitly within `danger!` blocks instead of using operators
5. Replaced bitcast calls with `to_bits()` and `from_bits()` methods

**Files Modified:**

- `stdlib/NativeDataTypes/d32.rf`, `d64.rf`, `d128.rf` - Added namespace core, workarounds for code generator
  limitations
- `src/CodeGen/LLVMCodeGenerator.External.cs` - Enhanced preset constant generation

**Note:** The d128 workarounds are necessary due to current code generator limitations:

- Binary operator dispatch doesn't properly dispatch to dunder methods for multi-field structs
- Global constant field access generates `%name` instead of `@name`
- Bitcast intrinsic tries to be too clever with extract/insert for same-layout records

### D32/D64 Nested Record Field Fix (2025-12-07 Session)

**Status:** ✅ **FULLY RESOLVED**

Fixed the bitcast intrinsic and decimal type workarounds to properly handle nested record types where a record's field
is itself a record (e.g., `%d32 = type { %u32 }` where `%u32 = type { i32 }`).

**Root Cause:** When bitcasting or performing operations on types with nested record fields, the code generator was:

1. Extracting the outer record's field (which is itself a record) and getting a record instead of primitive
2. Trying to insert a primitive where a record was expected
3. Using comparison operators directly which tried to operate on nested records

**Solution:**

1. **Updated bitcast intrinsic** (`LLVMCodeGenerator.Intrinsics.cs` lines 122-173):
    - When extracting from a struct whose field is a record, now extracts twice (outer → inner → primitive)
    - When inserting into a struct whose field is a record, now wraps primitive into inner record first
    - Checks `_recordFields` to determine if field type is itself a record

2. **Updated d32/d64 methods** to avoid all operator overloading in their implementations:
    - `__neg__()` - Uses external `d32_sub`/`d64_sub` with bitcast
    - `min()`, `max()` - Uses external `d32_cmp`/`d64_cmp` with bitcast
    - `is_positive()`, `is_negative()`, `is_zero()` - Uses external comparison functions
    - `signum()` - Uses external comparison with explicit `__neg__()` call

**Files Modified:**

- `src/CodeGen/LLVMCodeGenerator.Intrinsics.cs` - Enhanced bitcast to handle nested records (lines 122-173)
- `stdlib/NativeDataTypes/d32.rf` - Fixed 7 methods to use danger blocks with external functions
- `stdlib/NativeDataTypes/d64.rf` - Fixed 7 methods to use danger blocks with external functions

**Generated Code Example (correct):**

```llvm
; Bitcast from %u32 to %d32 (both have nested record structure)
%tmp543 = load %u32, ptr %bits_12
%tmp544 = extractvalue %u32 %tmp543, 0        ; Extract i32 from u32
%tmp545 = insertvalue %u32 undef, i32 %tmp544, 0  ; Wrap i32 back into u32
%tmp542 = insertvalue %d32 undef, %u32 %tmp545, 0 ; Insert u32 into d32 ✅
```

**Pattern for d32/d64 methods:**

```razorforge
routine d32.is_positive(me: d32) -> bool {
    danger! {
        let me_bits = @intrinsic.bitcast<d32, u32>(me)
        let zero_bits = @intrinsic.bitcast<d32, u32>(D32_ZERO)
        return d32_cmp(me_bits, zero_bits) > 0
    }
}
```

**Test Results:** ✅ Compilation successful with no Clang type mismatch errors

### Core Prelude Auto-Loading System (2025-12-08 Session)

**Status:** ✅ **FULLY IMPLEMENTED**

Implemented automatic loading of all stdlib files marked with `namespace core` declaration, eliminating the need to
manually import fundamental types and functions.

**Problem:** Previously, core types like primitive numeric types, Maybe/Result, and other fundamental types had to be
explicitly imported. This was verbose and inconsistent with most programming languages.

**Solution:** Created `CorePreludeLoader.cs` that:

1. Recursively scans the entire stdlib directory tree
2. Parses every `.rf` file to check for `namespace core` declaration
3. Automatically loads all core files without requiring explicit imports
4. Integrates with module resolver cache for proper dependency tracking

**Implementation Details:**

**New File: `src/Analysis/CorePreludeLoader.cs` (100 lines)**

- `LoadCorePrelude()` - Main entry point, returns dictionary of module info
- `ScanDirectory()` - Recursive directory traversal
- `LoadFileIfCoreNamespace()` - Parses and checks for core namespace
- Handles parse errors gracefully (logs warnings, continues)

**Modified: `src/Analysis/SemanticAnalyzer.cs`**

- Added `LoadCorePrelude()` method to SemanticAnalyzer initialization
- Core prelude loads BEFORE any user code is analyzed
- Uses `LoadModuleWithDependencies()` for transitive imports
- All core types now available in global scope without imports

**Impact:**

- ✅ Primitive types (s8-s128, u8-u128, f16-f128, d32-d128, bool, Blank) always available
- ✅ Error handling types (Maybe, Result, Lookup, DataHandle, DataState) always available
- ✅ Memory types (DynamicSlice, MemorySize, BackIndex, Range) always available
- ✅ Letter types (letter8, letter16, letter32) always available
- ✅ FFI types (cstr, cint, etc.) always available
- ✅ Cleaner user code - no more `import s64` or `import Maybe`
- ✅ Matches expectations from other languages

**Example Before/After:**

```razorforge
# BEFORE - Had to manually import everything
import s64
import u32
import Maybe
import Result

routine start() {
    let x: s64 = 42
    let maybe: Maybe<u32> = Some(100)
}

# AFTER - Core types automatically available
routine start() {
    let x: s64 = 42
    let maybe: Maybe<u32> = Some(100)
}
```

**Files Modified:**

- ✅ `src/Analysis/CorePreludeLoader.cs` - NEW FILE (100 lines)
- ✅ `src/Analysis/SemanticAnalyzer.cs` - Added LoadCorePrelude() call (lines 181, 372-401)
- ✅ `src/Analysis/ModuleResolver.cs` - Added AddToCache() method

### Comprehensive Code Generator Refactoring (2025-12-08 Session)

**Status:** ✅ **MAJOR OVERHAUL COMPLETE**

Massive refactoring and enhancement of the LLVM code generator to fix numerous edge cases, improve type handling, and
support more complex language features.

**Summary of Changes:**

**1. Location Tracking (`UpdateLocation()` method)**

- Created centralized `UpdateLocation(SourceLocation)` method in LLVMCodeGenerator.cs
- Replaced all `_currentLocation = node.Location` assignments with `UpdateLocation(node.Location)`
- Ensures consistent error reporting with correct file names and line numbers
- Fixes Bug 12.14 (Error Location Tracking)

**2. Generic Type Full Name Handling**

- Added `BuildFullTypeName()` method to recursively construct generic type names
- Fixed variable declarations to preserve generic type arguments (e.g., `TestType<s64>` not just `TestType`)
- Enhanced `MapTypeWithSubstitution()` to handle nested generics with regex-based substitution
- Proper handling of complex types like `Range<BackIndex<uaddr>>`

**3. Type Conversion and Casting**

- Enhanced variable initialization to auto-cast mismatched types
- Added type checking before store instructions
- Proper handling of record type conversions
- Support for truncation and extension with record wrapping

**4. Binary Expression Enhancements**

- Added `GenerateAssignment()` handling for assignment operator
- Floating-point type detection with fallback logic
- Proper `fadd`/`fsub`/`fmul`/`fdiv`/`fcmp` for FP types vs `add`/`sub`/`mul`/`sdiv`/`icmp` for integers
- Fixed type conversion for pointer and record types
- Enhanced right operand conversion with record extraction/wrapping

**5. Arithmetic Right Shift Fix**

- Extract-operate-wrap pattern for record types
- Properly detects primitive type inside record wrapper
- Generates correct `ashr`/`lshr` instructions

**6. Floating-Point Constant Handling**

- Added `EnsureProperFPConstant()` to fix FP literal format issues
- Ensures FP operations use proper constant representation

**7. Function Generation Improvements**

- Implicit `me` parameter for type methods (vs namespace functions)
- Namespace detection to differentiate `s64.__add__` (method) from `Console.show` (namespace function)
- Type parameter substitution in receiver types
- Regex-based substitution for generic parameters in function signatures
- Proper RazorForge type tracking with substitutions applied

**8. Generic Function Template Matching**

- Added `FindMatchingGenericTemplate()` to search for templates
- Added `ExtractTypeArguments()` for parsing type argument lists
- Added `GetGenericFunctionReturnType()` with type substitution
- Handles complex cases like `BackIndex<I>.resolve` → `BackIndex<uaddr>.resolve`

**9. Method Call Enhancements**

- Improved argument type resolution with fallback to `GetTypeInfo()`
- Better handling of literals and untracked expressions
- Fixed temp name generation to avoid `%ptr_%tmp` (strips `%` prefix)
- Enhanced return type lookup to return both LLVM and RazorForge types

**10. Generic Record Constructor Calls**

- Support for `TestType<s64>(value: 42)` syntax
- Checks `_genericRecordTemplates` before instantiation
- Proper struct initialization code generation

**11. Native Function Declaration Tracking**

- Added `_declaredNativeFunctions` and `_nativeFunctionDeclarations` tracking
- Added `EmitNativeFunctionDeclarations()` method
- Prevents duplicate external function declarations

**12. Record Field Type Tracking**

- Added `_recordFieldsRfTypes` to track RazorForge field types before LLVM conversion
- Enables better method lookup on record fields
- Supports generic record field resolution

**Files Modified (Massive Changes):**

- `src/CodeGen/LLVMCodeGenerator.cs` - 418+ lines changed
    - Added `UpdateLocation()`, generic template methods, field tracking
- `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - 1365+ lines changed
    - Binary expression overhaul, type conversion, FP handling
- `src/CodeGen/LLVMCodeGenerator.Functions.cs` - 433+ lines changed
    - Implicit me parameters, type substitution, template instantiation
- `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - 170+ lines changed
    - Generic constructor calls, argument type resolution
- `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - 153+ lines changed
- `src/CodeGen/LLVMCodeGenerator.Intrinsics.cs` - 797+ lines changed
- `src/CodeGen/LLVMCodeGenerator.Errors.cs` - 188+ lines changed
- `src/CodeGen/LLVMCodeGenerator.External.cs` - 320+ lines changed
- `src/CodeGen/LLVMCodeGenerator.Statements.cs` - 306+ lines changed
- `src/CodeGen/LLVMCodeGenerator.Types.cs` - 133+ lines changed
- `src/CodeGen/LLVMCodeGenerator.Constructors.cs` - 734+ lines changed

**Impact:**

- ✅ Fixed Bug 12.14 - Error location tracking shows correct file/line
- ✅ Fixed numerous type conversion edge cases
- ✅ Generic types work end-to-end with proper instantiation
- ✅ Methods on generic types resolve correctly
- ✅ Floating-point arithmetic generates correct LLVM IR
- ✅ Record type system consistently enforced throughout
- ✅ Native function declarations properly managed

### Semantic Analyzer Enhancements (2025-12-08 Session)

**Status:** ✅ **COMPLETED**

Enhanced the semantic analyzer with better type inference, namespace tracking, and module loading capabilities.

**Changes:**

**1. Expected Type Context for Inference**

- Added `_expectedType` field for type inference (Bug 12.10 foundation)
- Enables contextual type inference for integer literals
- Will propagate expected types down expression tree

**2. Current Namespace Tracking**

- Added `_currentNamespace` field to track namespace declarations
- Public `CurrentNamespace` property for code generator access
- Distinguishes namespace-qualified functions from type methods
- Critical for fixing method vs function ambiguity

**3. Symbol Table Namespace Detection**

- Made `_fileName` public for cross-component access
- Enhanced symbol table to support namespace queries
- Enables `IsNamespace()` checks during code generation

**4. Core Prelude Integration**

- Integrated `CorePreludeLoader` into analyzer initialization
- Loads ALL transitive dependencies for core modules
- Ensures types like DataHandle (imported by Maybe) are available
- Fixes missing type errors during compilation

**Files Modified:**

- `src/Analysis/SemanticAnalyzer.cs` - 63+ lines changed
    - Added expected type tracking, namespace tracking, core prelude loading
- `src/Analysis/SemanticAnalyzer.Declarations.cs` - 145+ lines changed
- `src/Analysis/SemanticAnalyzer.Expressions.cs` - 66+ lines changed
- `src/Analysis/SemanticAnalyzer.Calls.cs` - 98+ lines changed
- `src/Analysis/SemanticAnalyzer.Statements.cs` - 36+ lines changed
- `src/Analysis/SymbolTable.cs` - 36+ lines changed
- `src/Analysis/FunctionVariantGenerator.cs` - 99+ lines changed

### Parser Improvements (2025-12-08 Session)

**Status:** ✅ **COMPLETED**

Enhanced parser to better handle generic types, expressions, and statements.

**Changes:**

- `src/Parser/RazorForgeParser.Expressions.cs` - 90+ lines changed
    - Better generic type argument parsing
    - Improved expression precedence handling
- `src/Parser/RazorForgeParser.Statements.cs` - 73+ lines changed
    - Enhanced statement parsing
- `src/Parser/RazorForgeParser.Helpers.cs` - 23+ lines changed
    - Added helper methods for common parsing patterns

**Files Modified:**

- `src/Parser/RazorForgeParser.Expressions.cs` - 90+ lines
- `src/Parser/RazorForgeParser.Statements.cs` - 73+ lines
- `src/Parser/RazorForgeParser.Helpers.cs` - 23+ lines
- `src/Parser/BaseParser.cs` - 5+ lines
- `src/Lexer/BaseTokenizer.cs` - 11+ lines

### Standard Library Updates (2025-12-08 Session)

**Status:** ✅ **COMPLETED**

Updated standard library files with namespace declarations and bug fixes.

**Core Namespace Additions:**

- ✅ `stdlib/ErrorHandling/DataHandle.rf` – Added `namespace core`
- ✅ `stdlib/ErrorHandling/DataState.rf` – Added `namespace core`
- ✅ `stdlib/ErrorHandling/Maybe.rf` – Added `namespace core`
- ✅ `stdlib/ErrorHandling/Result.rf` – Enhanced
- ✅ `stdlib/ErrorHandling/Lookup.rf` – Added `namespace core`
- ✅ All NativeDataTypes files – Already had `namespace core`

**Other Files Modified:**

- `stdlib/Console.rf` – 58+ lines changed
- `stdlib/FFI/cstr.rf` – 10+ lines changed
- Collection type files – Minor updates

### Build System and Native Runtime (2025-12-08 Session)

**Status:** ⏳ **IN PROGRESS**

Added new native runtime functions and updated build configuration.

**Files Modified:**

- `native/CMakeLists.txt` - Build system updates
- `native/build.bat` - Windows build script fixes
- `native/build.sh` - Unix build script fixes
- `native/runtime/memory.c` - 19+ lines added
- `native/runtime/text_functions.c` - NEW FILE (for future text operations)

### LLVM Code Generation ABI and Control Flow Fixes (2025-12-08 Session 2)

**Status:** ✅ **COMPLETED**

Fixed critical code generation issues related to multi-field struct ABI conventions, control flow termination, and
generic entity method instantiation.

**Problems Fixed:**

**1. Multi-field Struct Argument Passing (d128 issue)**

- **Problem:** When method returned multi-field struct VALUE (like `%d128 = {%u64, %u64}`), and that value was used as
  argument expecting pointer, compiler generated invalid LLVM
- **Example:** `%tmp631 = call %d128 @d128.min(...)` returns value, then `call @d128.max(ptr %tmp631, ...)` tried to
  pass value as pointer
- **Root Cause:** Method call receiver handling assumed multi-field structs were already pointers from variable access,
  didn't handle return values
- **Solution:** Added check in `LLVMCodeGenerator.Expressions.cs:1528-1550` to detect when receiver is struct VALUE via
  `_tempTypes`, then allocate stack + store + pass pointer
- **Result:** Multi-field struct chaining now works correctly (e.g., `d128.min(...).max(...)`)

**2. If Statement Block Termination**

- **Problem:** When both branches of if statement terminated (with `ret`), compiler still generated unreachable code
  after the if
- **Example:** Both branches return, but code continues with `call @__rf_stack_pop()` after, causing LLVM validation
  error
- **Root Cause:** Always set `_blockTerminated = false` after if statement, even when both branches terminated
- **Solution:** Modified `LLVMCodeGenerator.Statements.cs:54-62` to only set `_blockTerminated = false` when there's
  continuation (end label). When both terminate, set `_blockTerminated = true`
- **Result:** No more unreachable code after terminated if statements

**3. Generic Entity Method Registration**

- **Problem:** Methods on generic entities like `List<T>.__create__()` were not being registered as instantiable
  templates
- **Root Cause:** Multiple issues:
    - Entity members (methods inside `entity { }` blocks) were processed but generics skipped
    - Top-level methods on generic types (like `routine List<T>.__create__()`) were being skipped as having method-level
      generics
    - No distinction between type-level generics (from `List<T>`) vs method-level generics
- **Solution:**
    - `LLVMCodeGenerator.Functions.cs:816-891` - Process methods inside generic record declarations, register as
      templates
    - `LLVMCodeGenerator.Functions.cs:879-941` - Process methods inside generic entity declarations, register as
      templates
    - `LLVMCodeGenerator.Functions.cs:983-1024` - Detect top-level methods on generic types (`List<T>.method`) and
      register as templates
    - Distinguished between `hasMethodLevelGenerics && !isGenericTypeMethod` (skip) vs `isGenericTypeMethod` (register)
- **Result:** Generic entity/record methods now properly registered and available for instantiation

**4. Generic Constructor Instantiation Trigger**

- **Problem:** When calling generic constructors like `List<letter8>(len)`, the template was registered but
  instantiation never triggered
- **Root Cause:** `TryHandleGenericTypeConstructor` built mangled name and generated call, but didn't instantiate the
  template
- **Solution:** Added instantiation trigger in `LLVMCodeGenerator.Constructors.cs:1771-1780` - checks if template
  exists, extracts type args, calls `InstantiateGenericFunction()`
- **Result:** Constructor calls now trigger proper template instantiation

**5. Entity Method Generation from Imports**

- **Problem:** Only record methods were being generated from imported modules, entity methods were ignored
- **Root Cause:** `GenerateImportedModuleFunctions` only had loop for `RecordDeclaration.Members`, not
  `EntityDeclaration.Members`
- **Solution:** Added parallel loop for entity members at `LLVMCodeGenerator.Functions.cs:879-941`
- **Result:** Both record AND entity methods now generated from stdlib modules

**Files Modified:**

- ✅ `src/CodeGen/LLVMCodeGenerator.Expressions.cs` - Multi-field struct argument passing (lines 1528-1550)
- ✅ `src/CodeGen/LLVMCodeGenerator.Statements.cs` - If statement termination (lines 54-62)
- ✅ `src/CodeGen/LLVMCodeGenerator.Functions.cs` - Generic method registration (3 locations: records, entities,
  top-level)
- ✅ `src/CodeGen/LLVMCodeGenerator.Constructors.cs` - Generic constructor instantiation trigger (lines 1771-1780)

**Impact:**

- ✅ Multi-field structs (d32, d64, d128) can be chained in method calls
- ✅ Control flow correctly handled - no unreachable code after terminated blocks
- ✅ Generic entity methods (List<T>, Text<T>) registered and instantiable
- ✅ Generic constructors trigger template instantiation
- ✅ Stdlib entity types can now be used (List, Text, etc.)

**Known Remaining Issues:**

- ⏸️ Type resolution during generic instantiation - when generating instantiated generic code, some type lookups fail
- ⏸️ Stdlib bugs - some stdlib files use wrong generic parameter names (e.g., `T` instead of `LetterType`)
- ⏸️ Dependency issues - some stdlib modules reference types not loaded (e.g., Text not loaded when needed)

**Test Results:**

- ✅ d128 multi-field struct passing works correctly
- ✅ If statement termination generates valid LLVM IR
- ✅ Generic templates registered (List<T>.__create__, etc.)
- ✅ Constructor calls trigger instantiation
- ⏸️ Full compilation blocked by type resolution issues during instantiation

### Generic Entity Constructor Calls (2025-12-08 Session 3)

**Status:** ✅ **COMPLETED**

Fixed critical code generation issue preventing generic entity constructors from working properly.

**Problems Fixed:**

**1. Generic Entity Template Detection in Constructor Calls**

- **Problem:** When calling `Text<letter8>(letters: list)`, code generator couldn't find entity templates
- **Root Cause:** Only checking `_genericRecordTemplates`, not `_genericEntityTemplates` in method call handling
- **Solution:** Added entity template check in `LLVMCodeGenerator.MethodCalls.cs:107-119` - checks both record and
  entity templates
- **Result:** Generic entity constructors now properly detected and instantiated

**2. Entity Field Tracking for Constructors**

- **Problem:** `HandleRecordConstructorCall` couldn't find field information for entities like `Text_letter8`
- **Root Cause:** `GenerateEntityType` didn't store field information in `_recordFields` dictionary (only records did)
- **Solution:** Added field tracking to `GenerateEntityType` in `LLVMCodeGenerator.Declarations.cs:151-185` - mirrors
  record field tracking
- **Result:** Entity constructors can now access field information for initialization

**3. Heap Allocation for Entity Constructors**

- **Problem:** `HandleRecordConstructorCall` allocated on stack, but entities need heap allocation
- **Root Cause:** No separate handler for entity constructors - used same code path as records
- **Solution:**
    - Created `HandleEntityConstructorCall` in `LLVMCodeGenerator.Constructors.cs:926-1022`
    - Uses `malloc` for heap allocation instead of `alloca`
    - Returns pointer directly (reference type) instead of loading value (value type)
- **Result:** Entities properly heap-allocated and returned as pointers

**4. Parser Bug with Generic Type Parameters**

- **Problem:** Methods like `Text<letter8>.to_cstr` had `letter8` incorrectly placed in `GenericParameters`
- **Root Cause:** Parser bug - receiver type arguments end up in method's `GenericParameters` list
- **Workaround:** Modified `LLVMCodeGenerator.Functions.cs:991-998` to detect and ignore this case
- **Result:** Concrete methods on specific instantiations no longer treated as generic templates

**5. Stdlib Type Corrections**

- **Problem:** Several numeric types used undefined `Text<LetterType>` parameter type
- **Root Cause:** `LetterType` wasn't defined, should use concrete type like `letter8`
- **Solution:** Changed `Text<LetterType>` to `Text<letter8>` in:
    - `stdlib/NativeDataTypes/d128.rf:55`
    - `stdlib/NativeDataTypes/d32.rf:49`
    - `stdlib/NativeDataTypes/d64.rf:48`
    - `stdlib/NativeDataTypes/s32.rf:21`
- **Result:** Numeric type constructors from text now compile correctly

**6. Text Entity Generic Parameter Consistency**

- **Problem:** `Text` entity declared as `Text<LetterType>` but methods used `T`
- **Root Cause:** Naming inconsistency in generic parameter between entity and methods
- **Solution:** Changed entity declaration to `Text<T>` in `stdlib/Text/Text.rf:15` for consistency
- **Result:** All `Text<T>` code uses same generic parameter name

**Files Modified:**

- ✅ `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` - Added entity template check (lines 107-119)
- ✅ `src/CodeGen/LLVMCodeGenerator.Declarations.cs` - Added entity field tracking (lines 151-185)
- ✅ `src/CodeGen/LLVMCodeGenerator.Constructors.cs` - Created `HandleEntityConstructorCall` (lines 926-1022)
- ✅ `src/CodeGen/LLVMCodeGenerator.Functions.cs` - Parser bug workaround (lines 991-998)
- ✅ `stdlib/NativeDataTypes/d128.rf` - Fixed Text<LetterType> → Text<letter8>
- ✅ `stdlib/NativeDataTypes/d32.rf` - Fixed Text<LetterType> → Text<letter8>
- ✅ `stdlib/NativeDataTypes/d64.rf` - Fixed Text<LetterType> → Text<letter8>
- ✅ `stdlib/NativeDataTypes/s32.rf` - Fixed Text<LetterType> → Text<letter8>
- ✅ `stdlib/Text/Text.rf` - Changed Text<LetterType> → Text<T>

**Impact:**

- ✅ Generic entity constructors work: `Text<letter8>(letters: list)`
- ✅ Entities properly heap-allocated with `malloc`
- ✅ Entity field initialization works correctly
- ✅ Text type can be instantiated and used
- ✅ Numeric type constructors from text strings work
- ⏸️ Remaining issue: Methods on specific instantiations (e.g., `Text<letter8>.to_cstr`) still not generated due to
  parser bug placing receiver type args in GenericParameters

**Known Remaining Parser Bug:**

- **Issue:** Parser places receiver type arguments in method's `GenericParameters` list
- **Example:** `Text<letter8>.to_cstr` has `letter8` in `GenericParameters` when it shouldn't
- **Current State:** Workaround prevents these from being treated as templates, but they still don't generate
- **Fix Needed:** Parser needs to correctly distinguish receiver type arguments from method-level generics
- **Impact:** Methods on concrete instantiations (like `Text<letter8>.to_cstr`) are not available
- **Workaround:** Use `__create__` constructors or generic methods (like `Text<T>.length`) instead

**Test Results:**

- ✅ `Text<letter8>()` constructor works
- ✅ `Text<letter8>(from_list: list)` constructor works
- ✅ Entity types properly instantiated and tracked
- ✅ Stdlib numeric types compile without Text-related errors
- ⏸️ `Text<letter8>.to_cstr()` method still unavailable (parser bug)

---

## Implementation Priority Summary

### ✅ COMPLETED (Major Milestone!)

1. ✅ **Bug 12.13** - Generic method template matching - FULLY WORKING!

### 🟠 HIGH PRIORITY (Core Language Features)

2. **Generic record instantiation** - Two-pass generation with dependency ordering
3. **Native runtime library** - Link and execute programs (currently blocks execution)
4. **Type constructor fixes** - Bug 12.7, 12.10, 12.11 (blocking stdlib development)

### 🟡 MEDIUM-HIGH (Important for Usability)

5. **Protocol system** - Declarations, implementation checking
6. **Module system** - Import resolution and symbol loading
7. **Generic function overload resolution** - Choose between generic/non-generic
8. **Generic constraints** - `<T: Protocol>` syntax and validation

### 🟢 MEDIUM (Quality of Life)

9. **Missing parser features** - `external`, `preset`, `common`, etc.
10. **Lambda code generation** - Execute higher-order functions
11. **Auto-generated methods** - Reduce boilerplate

### ⚪ LOW (Nice to Have)

12. **Method-chain constructors** - `"42".s32!()`
13. **Range validation** - Compile-time bounds checking

---

## External Documentation

- [GENERICS_STATUS.md](GENERICS_STATUS.md) - Comprehensive generics analysis
- [BUG_12.13_ANALYSIS.md](BUG_12.13_ANALYSIS.md) - Generic method matching deep dive
- [STDLIB_TODO.md](STDLIB_TODO.md) - Standard library implementation tasks
- [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md) - Error handling design
- [RazorForge Memory Model](../wiki/RazorForge-Memory-Model.md) - Memory management design

---

**Next Session Focus:** Generic record instantiation (two-pass generation) OR native runtime library OR type constructor
fixes - choose based on immediate needs.

---

## 🔴 CRITICAL Language Design Changes (Added 2025-12-10, Updated 2025-12-12)

**Priority:** 🔴 **CRITICAL** (blocking language spec finalization)

**Status:** ✅ **NEARLY COMPLETE** (Items 1-5 done, 6 pending)

These are fundamental language design changes that must be implemented to match the finalized language specification.

**Completion Summary:**
- ✅ Item 1: Entry point `main` → `start` (COMPLETE)
- ✅ Item 2: Reserved names & namespace rules (COMPLETE)
- ✅ Item 3: Crashable entry point design (COMPLETE)
- ✅ Item 4: Hybrid namespace system (COMPLETE)
- ✅ Item 5: Inline conditional restrictions (COMPLETE)
- ⏳ Item 6: Suflae `eternal` keyword (PENDING)

### 1. Entry Point Rename: `main` → `start` - ✅ **COMPLETE**

**Status:** ✅ **COMPLETE (2025-12-12)**

**Change:** The entry point routine must be named `start`, not `main`.

**Design Decision:** `start()` is **ALWAYS crash-capable** (no `!` suffix or `@crash_only` attribute needed).
Crashes at the entry point become exit codes automatically. This is simpler and matches user expectations.

```razorforge
# OLD (deprecated)
routine start() {
    show_line("Hello, RazorForge!")
}

# NEW (required)
routine start() {
    show_line("Hello, RazorForge!")

    # Can use throw/absent without special attributes
    let config = load_config()
    unless config {
        throw ConfigError("Failed to load config")
    }
}
```

**Completed Tasks:**

- [x] ~~Update lexer to reserve `start` keyword~~ - NOT NEEDED (start is regular identifier)
- [x] Code generator maps `start` to LLVM `main` function
- [x] Semantic analyzer validates `start` signature (no required params, Blank return)
- [x] Reserved name validation prevents misuse of `start`/`start!`
- [ ] Update all example code in documentation - PENDING
- [ ] Update all test files - PENDING

**Files Modified:**

- ✅ `src/CodeGen/LLVMCodeGenerator.Functions.cs:108-123, 280-286` - Maps start → main
- ✅ `src/Analysis/SemanticAnalyzer.cs:276-330` - Validates entry point
- ✅ `src/Analysis/SemanticAnalyzer.Declarations.cs:212-222` - Reserves start/start! names

---

### 2. Reserved Routine Names & Namespace Rules - ✅ **COMPLETE**

**Status:** ✅ **COMPLETE (2025-12-12)** - Enhanced with namespace restrictions

**Changes:**

1. **`start!` is PROHIBITED** - Entry point is always `start` (always crash-capable)
2. **`start` reserved for entry point** - Only zero-parameter, global namespace
3. **Namespace requirement** - Entry point MUST be in global namespace (project root files)

```razorforge
# ❌ CE: 'start!' is never allowed
routine start!() {
    throw ConfigError()  # Error: Use 'start' instead (always crash-capable)
}

# ❌ CE: 'start' with parameters is reserved
routine start(x: s32) -> s32 {
    return x * 2  # Error: start is reserved for entry point
}

# ❌ CE: 'start' in namespace is not allowed
namespace app.core
routine start() {  # Error: Entry point must be in global namespace
    show("Not allowed here")
}

# ✅ OK: Zero-parameter entry point in global namespace (root file)
# File: myproject/main.rf (no namespace declaration)
routine start() {
    show("Application started")
}
```

**Namespace Rules for Entry Point:**

| File Location | Namespace | `start()` Allowed? |
|--------------|-----------|-------------------|
| `myproject/main.rf` | Global (no namespace decl) | ✅ YES |
| `myproject/app.rf` | Global (no namespace decl) | ✅ YES |
| `myproject/src/core.rf` | Implicit `src` namespace | ❌ NO |
| With `namespace foo` | Explicit `foo` namespace | ❌ NO |

**Completed Tasks:**

- [x] Prohibit `start!` entirely (always use `start`)
- [x] Reserved name validation in `SemanticAnalyzer.Declarations.cs`
- [x] Error if `start` used with required parameters
- [x] Error if `start` declared in non-global namespace
- [x] Allow only zero-parameter `start` in global namespace

**Files Modified:**

- ✅ `src/Analysis/SemanticAnalyzer.Declarations.cs:212-246` - Reserved name & namespace checks

---

### 3. Crashable Entry Point - ✅ **DESIGN CHANGED**

**Status:** ✅ **COMPLETE (2025-12-12)** - Different approach taken

**Design Decision:** NO `@crash_only` attribute needed. The entry point `start()` is **ALWAYS crash-capable** by design.

**Rationale:**
- Simpler mental model (no need to remember `start()` vs `@crash_only routine start!()`)
- Matches user expectations (where else would crashes go at the top level?)
- Aligns with other languages (main() can return errors/panic)
- More explicit (crashes naturally happen, runtime converts to exit codes)

```razorforge
# ✅ Simple: start() is always crash-capable
routine start() {
    throw FatalError()  # OK - becomes exit code automatically
    absent               # OK - absence becomes exit code
}

# ❌ NO LONGER NEEDED: start! variant or @crash_only attribute
```

**Completed:**

- [x] Design decision: Always crash-capable by default
- [x] No special syntax or attributes needed
- [x] Runtime handles crash → exit code conversion (future work)

**Future Enhancement:**

For complex project structures, we may add TOML configuration to override the default:

```toml
# forge.toml or bake.toml (FUTURE)
[build]
entry_point = "app.cli.start"  # Override default global namespace requirement
# OR
entry_file = "src/main.rf"     # Specify exact entry file
```

This would allow libraries and large projects more flexibility while maintaining the simple default behavior.

---

### 4. Hybrid Namespace System - ✅ **COMPLETE**

**Status:** ✅ **COMPLETE (2025-12-12)** - Stdlib, project root, and path-based namespaces implemented

**Changes:**

1. **Forward slashes everywhere** - Consistent namespace syntax
2. **Stdlib path resolution** - TOML → Environment → Default priority
3. **Stdlib validation** - MUST have explicit namespace (CE)
4. **Project root** - Global namespace (no declaration needed)
5. **Subdirectories** - Auto-inferred namespace from path

**Design:**

```razorforge
# ========== STDLIB FILES ==========
# File: stdlib/Collections/List.rf
namespace Collections  # ✅ REQUIRED - Compile error if missing

# File: stdlib/Text/Text.rf
namespace Text  # ✅ REQUIRED

# ========== PROJECT ROOT FILES ==========
# File: myproject/main.rf (no namespace declaration)
routine start() {  # ✅ Global namespace (null)
    show("Hello")
}

# File: myproject/app.rf (no namespace declaration)
routine init() {  # ✅ Global namespace (null)
    # ...
}

# ========== PROJECT SUBDIRECTORY FILES ==========
# File: myproject/src/core.rf (no namespace declaration)
# ✅ AUTO-INFERRED: namespace "src"
routine process() {
    # ...
}

# File: myproject/lib/utils/helpers.rf (no namespace declaration)
# ✅ AUTO-INFERRED: namespace "lib/utils"
routine format() {
    # ...
}

# ========== EXPLICIT OVERRIDE ==========
# File: myproject/src/core.rf
namespace app/core  # ✅ Explicit override (ignores path)
routine init() {
    # ...
}
```

**Namespace Rules Table:**

| File Location | Explicit `namespace` | Actual Namespace | Notes |
|--------------|----------------------|------------------|-------|
| `stdlib/Collections/List.rf` | None | ❌ **CE** | Stdlib MUST have namespace |
| `stdlib/Collections/List.rf` | `namespace Collections` | `Collections` | ✅ Required |
| `myproject/main.rf` | None | Global (null) | ✅ Root file |
| `myproject/app.rf` | `namespace myapp` | `myapp` | ✅ Explicit override |
| `myproject/src/core.rf` | None | `src` | ✅ Inferred from path |
| `myproject/lib/utils.rf` | None | `lib/utils` | ✅ Inferred from path |
| `myproject/src/foo.rf` | `namespace bar` | `bar` | ✅ Explicit override |

**Stdlib Path Resolution (Priority Order):**

```toml
# 1. TOML config (highest priority)
# forge.toml or bake.toml
[build]
stdlib_path = "/custom/path/to/stdlib"

# 2. Environment variable (middle priority)
# RAZORFORGE_STDLIB_PATH=/opt/razorforge/stdlib

# 3. Default location (fallback)
# <compiler_exe_dir>/stdlib
# or parent directories for development builds
```

**Usage Examples:**

```razorforge
# Project structure:
# myproject/
#   ├── forge.toml
#   ├── main.rf          # Global namespace
#   └── src/
#       └── utils.rf     # namespace "src"

# main.rf (no namespace declaration)
import src/utils  # Import from inferred namespace

routine start() {
    src/utils.process()
}

# src/utils.rf (no namespace declaration - inferred as "src")
routine process() {
    show("Processing...")
}
```

**Completed Tasks:**

- [x] Add stdlib path resolution (TOML → Env → Default) to `ModuleResolver`
- [x] Add `FindProjectRoot()` to detect forge.toml/bake.toml
- [x] Add `IsStdlibFile()` check using stdlib path
- [x] Add `InferNamespaceFromPath()` with forward slash conversion
- [x] Parser already supports forward slashes in namespace declarations
- [x] Add namespace validation in `SemanticAnalyzer`:
  - Stdlib files without namespace → CE
  - Project files without namespace → infer from path
  - Root files → global namespace

**Files Modified:**

- ✅ `src/Analysis/ModuleResolver.cs:15-470` - Stdlib path resolution, IsStdlibFile(), InferNamespaceFromPath(), FindProjectRoot()
- ✅ `src/Analysis/SemanticAnalyzer.cs:276-387` - ValidateNamespaceRules() implementation

---

### 4.5. Module Closure: Prevent External Namespace Pollution - ⏸️ **TODO**

**Status:** ⏸️ **TODO** - Design decision confirmed, implementation needed

**Problem:** Once a module is imported, external code should NOT be able to add to that module's namespace.

**Design Decision:** Modules should be **closed** - you cannot declare `namespace X` if module `X` is imported from stdlib or another package.

**Examples:**

```razorforge
# File: stdlib/Collections/List.rf
namespace Collections

public entity List<T> {
    # Core implementation
}
```

```razorforge
# File: myproject/main.rf
import Collections/List  # Import the Collections module

# ❌ CE: Cannot add to imported module's namespace
namespace Collections
routine my_helper() {  # Error: Cannot extend imported module namespace
    # ...
}

# ✅ OK: Use your own namespace
namespace myapp
routine my_helper() {
    # ...
}

# ✅ OK: Global namespace
routine main_helper() {
    # ...
}
```

**Rationale:**

1. **Prevent namespace pollution** - External code cannot inject into stdlib or third-party namespaces
2. **Clear module boundaries** - Each module owns its namespace exclusively
3. **Extension methods still work** - You can still add methods to types from any namespace, but cannot declare new types/functions in imported namespaces
4. **Consistent with `public(module)` access** - Module-scoped privacy already treats modules as boundaries

**Rules:**

| Scenario | Namespace Declaration | Result |
|----------|----------------------|--------|
| Own module/local file | `namespace myapp` | ✅ OK |
| Stdlib module imported | `namespace Collections` | ❌ CE: Cannot extend imported namespace |
| Third-party imported | `namespace external` | ❌ CE: Cannot extend imported namespace |
| Extension method | `routine List.my_method()` | ✅ OK (methods, not namespace) |

**Implementation Tasks:**

- [ ] Track imported namespaces in `ModuleResolver` or `SemanticAnalyzer`
- [ ] Validate `namespace` declarations against imported namespaces
- [ ] Error if user tries to declare namespace matching an imported module
- [ ] Allow extension methods (don't confuse with namespace declarations)

**Error Messages:**

```
Semantic error: Cannot declare namespace 'Collections' - this namespace is imported from module 'Collections/List'
Note: To extend types from this module, use extension methods (e.g., 'routine List.my_method()')
```

---

### 5. Inline Conditional: Block Syntax NOT Supported - ✅ **COMPLETE**

**Status:** ✅ **COMPLETE (2025-12-12)**

**Change:** `if cond then A else B` is ONLY for single expressions, NOT blocks.

```razorforge
# ✅ OK: Inline expression only
let x = if count > 0 then "items" else "empty"
let max = if a > b then a else b
return if success then result else default_value

# ❌ CE: Cannot use blocks in inline conditional
let y = if condition then {
    let temp = compute()
    temp * 2
} else {
    default_value
}
# Parse error: "Block syntax (if condition { ... }) is not supported for inline conditionals"

# ❌ CE: Cannot nest inline conditionals
let x = if a > 0 then (if a < 0 then -1 else 0) else 1
# Parse error: "Nested inline conditionals are not allowed. Use statement-form 'if' or 'when'"

# ✅ OK: Use statement form for blocks
let y = {
    if condition {
        let temp = compute()
        temp * 2
    } else {
        default_value
    }
}
```

**Rationale:** Keep inline conditionals simple. Blocks should use statement syntax.

**Completed Tasks:**

- [x] Added `_parsingInlineConditional` flag to track parsing state
- [x] Updated parser to reject block syntax in `if-then-else` expressions
- [x] Ensured `if-then-else` only accepts single expressions
- [x] Generated clear compile errors for block syntax attempts
- [x] Rejected nested inline conditionals (even with parentheses)
- [x] Applied same restrictions to both RazorForge and Suflae parsers
- [x] Tested all restrictions with comprehensive test cases
- [ ] Update documentation to clarify inline vs statement forms (PENDING)

**Files Modified:**

- ✅ `src/Parser/RazorForgeParser.cs:20-21` - Added `_parsingInlineConditional` flag
- ✅ `src/Parser/RazorForgeParser.Expressions.cs:1111-1156` - Enforced restrictions
- ✅ `src/Parser/SuflaeParser.cs:18-19` - Added `_parsingInlineConditional` flag
- ✅ `src/Parser/SuflaeParser.Expressions.cs:660-699` - Enforced restrictions

**Stdlib Files Updated:**
- ✅ `stdlib/Collections/BitList.rf` - Converted all inline conditionals to new syntax
- ✅ `stdlib/Collections/Dict.rf` - Updated
- ✅ `stdlib/Collections/Set.rf` - Updated
- ✅ `stdlib/Collections/List.rf` - Updated
- ✅ `stdlib/Collections/Deque.rf` - Updated
- ✅ `stdlib/Collections/FixedDeque.rf` - Updated
- ✅ `stdlib/Collections/PriorityQueue.rf` - Updated
- ✅ `stdlib/NativeDataTypes/*.rf` - All numeric types updated (s8-s128, u8-u128, d128)

---

### 6. Ban In-Scope Method Declarations - ⏸️ **TODO**

**Status:** ⏸️ **TODO** - Design decision confirmed, implementation needed

**Problem:** Methods MUST be declared outside type scope. In-scope method declarations should be banned.

**Design Decision:** All methods must use `routine TypeName.method_name()` syntax declared outside the type body. Methods inside entity/resident/record scope are compile errors.

**Examples:**

```razorforge
# ✅ OK: Methods declared outside type scope
public entity List<T> {
    private var _buffer: pointer
    local var _count: uaddr
}

routine List<T>.push(value: T) {
    # Implementation
}

routine List<T>.pop!() -> T {
    # Implementation
}

# ❌ CE: In-scope methods not allowed
public entity List<T> {
    private var _buffer: pointer

    routine push(value: T) {  # ❌ Compile Error: Methods must be declared outside type scope
        # ...
    }

    private routine internal_helper() {  # ❌ Compile Error: Methods must be declared outside type scope
        # ...
    }
}
```

**Rationale:**

1. **Consistency** - Internal methods and extension methods use identical syntax
2. **Multi-file organization** - Methods can be naturally split across files in the same namespace
3. **No distinction** - No syntactic difference between "internal" and "extension" methods
4. **Cleaner type definitions** - Types show data structure clearly, methods are separate concerns
5. **Simpler parser** - One syntax for methods, not two separate paths
6. **Access control clarity** - Access modifiers on methods are unambiguous

**Implementation Tasks:**

- [ ] Update parser to reject method declarations inside type scope
- [ ] Generate clear error message: "Methods must be declared outside type scope using 'routine TypeName.method_name()' syntax"
- [ ] Apply to all type kinds (entity, resident, record)
- [ ] Update both RazorForge and Suflae parsers
- [ ] Add test cases for banned syntax

**Error Messages:**

```
Parse error: Methods cannot be declared inside type scope
Note: Use 'routine TypeName.method_name()' syntax outside the type definition
```

**Documentation:**

- ✅ `wiki/RazorForge-Routines.md` - Methods section updated with ban explanation
- ✅ `wiki/Suflae-Routines.md` - Methods section updated with ban explanation
- ✅ `wiki/RazorForge-Code-Style.md` - Type and Method Declaration section added
- ✅ `wiki/Suflae-Code-Style.md` - Type and Method Declaration section added

---

### 7. Suflae `eternal` Keyword Support

**Change:** Suflae needs `eternal` keyword for application-scoped singleton actors.

```suflae
# start.sf - Suflae entry point
eternal AppCore:
    logger: Logger
    config: Config
    metrics: Metrics

routine start():
    AppCore.logger = Logger()
    AppCore.config = load_config()
    # ...
```

**Requirements:**

- **Start file only** - Can only declare in entry point file
- **Single declaration** - Only one `eternal` type per application
- **Actor semantics** - All access through message passing
- **Application scope** - Accessible everywhere without import
- **Immortal lifetime** - Never garbage collected

**Tasks:**

- [ ] Add `eternal` keyword to Suflae lexer
- [ ] Parse `eternal TypeName:` syntax
- [ ] Validate single eternal declaration per application
- [ ] Validate eternal only in start file
- [ ] Generate actor wrapper code
- [ ] Make eternal accessible globally (no import needed)
- [ ] Mark as immortal in GC
- [ ] Update Suflae documentation

**Files (Suflae only):**

- `src/Lexer/SuflaeLexer.cs` - Add `eternal` keyword
- `src/Parser/SuflaeParser.cs` - Parse eternal declaration
- `src/Analysis/SemanticAnalyzer.cs` - Validate eternal rules
- `src/CodeGen/SuflaeCodeGenerator.cs` - Generate actor wrapper

**See:**

- [Suflae Eternal Documentation](../wiki/Suflae-Eternal.md)
- [Resident vs Eternal Comparison](../wiki/Resident-vs-Eternal.md)

---

## Implementation Priority for Critical Changes

**Suggested Order:**

1. **Entry point `main` → `start`** (Straightforward refactor)
2. **Reserve `start`/`start!`** (Small addition to validation)
3. **`@crash_only` for `start!`** (Attribute validation)
4. **Inline conditional restrictions** (Parser validation)
5. **`eternal` keyword** (Suflae only, most complex)

**Timeline Suggestion:**

- Items 1-5: Can be done in single session (3-4 hours)
- Item 6 (`eternal`): Separate session (requires actor model integration)

