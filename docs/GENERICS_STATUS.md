# RazorForge Generics: Current Status and Challenges

## Executive Summary

Generics in RazorForge are **partially working but complex** due to the multiple stages of compilation and the interaction between different generic features. Here's what works, what doesn't, and why it's hard.

---

## What Currently Works ✅

### 1. Generic Functions (Monomorphization)

**Status:** ✅ **WORKING**

```razorforge
routine identity<T>(value: T) -> T {
    return value
}

let x = identity<s32>(42)  # Works!
```

**How it works:**
1. Parser creates AST with generic parameters
2. Semantic analyzer validates the template
3. Code generator stores the template in `_genericFunctionTemplates`
4. When `identity<s32>` is called:
   - `InstantiateGenericFunction("identity", ["s32"])` is called
   - Creates mangled name: `identity_s32`
   - Queues instantiation for later generation
   - At the end of compilation, `GeneratePendingInstantiations()` generates concrete code

**Why this works:** Simple 1:1 mapping of type parameters to concrete types.

---

### 2. Namespace-Qualified Generic Methods

**Status:** ✅ **WORKING**

```razorforge
routine Console.show<T>(value: T) {
    # Works if uncommented!
}

Console.show<s32>(42)
```

**Why this works:** Same as generic functions, just with a namespace prefix.

---

### 3. Generic Type Parsing

**Status:** ✅ **WORKING**

```razorforge
List<s32>                    # Parses correctly
List<List<s32>>              # Nested generics work
Range<BackIndex<uaddr>>      # Complex nested types work
```

**Why this works:** Parser correctly handles `<>` brackets and nesting without confusing them with comparison operators.

---

## What Doesn't Work Yet ❌

### 1. Generic Records/Entities (Type Instantiation)

**Status:** ❌ **BROKEN** (Partially implemented but incomplete)

```razorforge
record List<T> {
    data: DynamicSlice
    length: uaddr
}

let list: List<s32> = List<s32>()  # Fails!
```

**Problem:**
- Templates are stored in `_genericRecordTemplates`
- `InstantiateGenericRecord` creates mangled type name (`List_s32`)
- BUT: The struct definition is generated immediately, not deferred
- Field types may not be resolved yet
- No dependency ordering between generic types

**Why it's hard:**
- Records have **fields** that might be other generic types
- Order matters: Must generate `Node<T>` before `List<Node<T>>`
- Circular dependencies possible: `Node<T>` has field `List<Node<T>>`, which needs `Node<T>`
- LLVM requires struct types be defined before use

---

### 2. Generic Methods on Generic Types

**Status:** ❌ **BROKEN** (Bug 12.13 in TODO)

```razorforge
record BackIndex<I> {
    offset: I
}

routine BackIndex<I>.resolve(me: BackIndex<I>, length: I) -> I {
    return length - me.offset
}

let bi = BackIndex<uaddr>(offset: 5_uaddr)
bi.resolve(length: 10_uaddr)  # FAILS!
```

**Problem:**
- Template stored with key: `BackIndex<I>.resolve`
- When called on `BackIndex<uaddr>`, we look for: `BackIndex_uaddr.resolve`
- Key mismatch - template lookup fails
- Function never gets instantiated

**Why it's hard:**
- Need to match `BackIndex_uaddr.resolve` → `BackIndex<I>.resolve<I>`
- Type parameters appear in TWO places: the type AND the method
- Must extract `I=uaddr` from the mangled type name
- Must then substitute in BOTH the method receiver type AND method body

---

### 3. Generic Function Overload Resolution

**Status:** ❌ **NOT IMPLEMENTED**

```razorforge
routine Console.show(value: Text<letter8>) {
    # Non-generic version
}

routine Console.show<T>(value: T) {
    # Generic version - commented out!
}

Console.show(42)  # Which one? Needs overload resolution!
```

**Problem:**
- Need to choose between generic and non-generic versions
- Need to check type constraints (`T: Printable`)
- Need to prefer more specific matches over generic ones
- Currently: Only one version can exist

**Why it's hard:**
- Type inference: Must infer `T` from argument types
- Constraint checking: Must verify `s32` implements `Printable` protocol
- Specificity rules: Non-generic should win over generic
- Multiple candidates: What if there are multiple generic versions?

---

### 4. Generic Constraints

**Status:** ❌ **NOT IMPLEMENTED**

```razorforge
routine max<T: Comparable>(a: T, b: T) -> T {
    # Need to ensure T has comparison operators
}
```

**Problem:**
- Parser doesn't recognize `: Comparable` syntax yet
- Semantic analyzer doesn't validate constraints
- Code generator doesn't check if concrete type satisfies constraint

**Why it's hard:**
- Need protocol system fully implemented
- Need to check protocols during instantiation
- Need meaningful error messages when constraints fail

---

### 5. Text Type Conversion Methods

**Status:** ❌ **NOT IMPLEMENTED**

```razorforge
# From Console.show<T>:
let text_value: Text<letter32> = value.to_text()  # to_text() doesn't exist!
let text8_value: Text<letter8> = text_value.to_8bit()  # to_8bit() doesn't exist!
```

**Problem:**
- `to_text()` protocol method not implemented for primitive types
- `Text<letter32>` to `Text<letter8>` conversion not implemented
- Generic `Text<T>` type itself not fully implemented

---

## Why Generics Are Hard: The Core Issues

### Issue 1: Multi-Stage Compilation

```
Source Code
    ↓ [Parser]
AST with generic templates
    ↓ [Semantic Analyzer]
Validated templates (no concrete types yet!)
    ↓ [Code Generator - First Pass]
Queue instantiations as they're encountered
    ↓ [Code Generator - Second Pass]
Generate all pending instantiations
    ↓ [LLVM]
Concrete machine code
```

**Problem:** Can't generate code until all types are known, but types depend on code being generated!

Example:
```razorforge
record Node<T> { value: T, next: Node<T>? }
```

To generate `Node<s32>`:
1. Need to know field types
2. Field `next` has type `Node<s32>?`
3. But `Node<s32>` is what we're trying to define!
4. Circular dependency!

**Solution:** Forward declarations + deferred field generation, but LLVM's opaque pointers make this tricky.

---

### Issue 2: Type Dependency Graphs

```razorforge
record List<T> { head: Node<T>? }
record Node<T> { value: T, next: Node<T>? }
```

To instantiate `List<s32>`:
1. Need `Node<s32>` for the `head` field
2. `Node<s32>` needs `Node<s32>` for its `next` field (recursive!)
3. Must generate in correct order

**Problem:** Need topological sort of generic type dependencies, accounting for:
- Direct dependencies (`List<T>` → `Node<T>`)
- Recursive dependencies (`Node<T>` → `Node<T>`)
- Cross-dependencies (`A<T>` → `B<T>` → `A<T>`)

---

### Issue 3: Template Key Matching

```razorforge
# Template stored as:
"BackIndex<I>.resolve<I>"

# Called as:
"BackIndex_uaddr.resolve"

# Must match and extract: I = uaddr
```

**Problem:**
- Mangled names lose generic parameter names
- Need reverse mapping: `BackIndex_uaddr` → `BackIndex<I>` where `I=uaddr`
- Then apply to method: `resolve<I>` → `resolve<uaddr>`

Current approach:
```csharp
// Check if this is a method on a generic type (contains '<' before '.')
if (functionName.Contains('<') && functionName.Contains('.'))
{
    int genericStart = functionName.IndexOf('<');
    int dotIndex = functionName.IndexOf('.');

    if (genericStart < dotIndex)
    {
        // Extract type parameters from the type name
        // ... complex string parsing logic ...
    }
}
```

This is **fragile** and **error-prone**!

---

### Issue 4: Memory Layout and Struct Generation

LLVM requires:
```llvm
; Define struct BEFORE using it
%List_s32 = type { ptr, i64 }

; Then you can use it
define void @process_list(%List_s32* %list) {
    ; ...
}
```

But with generics:
1. Don't know what types are needed until code generation
2. Can't define structs lazily - must define before first use
3. Recursive types need special handling (opaque types + cycles)

Current approach:
- `InstantiateGenericRecord` generates struct **immediately**
- This causes ordering issues if fields aren't ready

Better approach (not implemented):
1. Collect all needed instantiations first
2. Sort by dependency order
3. Generate all struct definitions
4. Then generate all functions

---

### Issue 5: Type Inference and Substitution

```razorforge
routine map<T, U>(list: List<T>, mapper: Routine<(T), U>) -> List<U> {
    # T appears in: list parameter, mapper parameter, mapper return type
    # U appears in: mapper return type, function return type
}
```

When instantiating `map<s32, f64>`:
1. Substitute `T` → `s32` in ALL positions
2. Substitute `U` → `f64` in ALL positions
3. Handle nested generics: `List<T>` → `List<s32>`
4. Handle function types: `Routine<(T), U>` → `Routine<(s32), f64>`

Current code does this, but it's **complex** and **fragile**:
```csharp
// Create type substitution map
var substitutions = new Dictionary<string, string>();
for (int i = 0; i < template.GenericParameters.Count; i++)
{
    substitutions[template.GenericParameters[i]] = typeArguments[i];
}

// Substitute in every type expression throughout the AST
string concreteReturnType = SubstituteTypeParameters(
    template.ReturnType,
    substitutions
);
```

---

## Why Console.show<T> is Commented Out

The generic `Console.show<T>()` is disabled because it needs **FOUR** features that aren't ready:

1. **Generic function overload resolution**
   - Must choose between `show(Text)` and `show<T>(T)`
   - Currently only one definition can exist

2. **Protocol constraints**
   - `show<T: Printable>` to ensure `T` has `to_text()`
   - Constraint system not implemented

3. **Text type conversion**
   - `value.to_text()` returns `Text<letter32>`
   - `text.to_8bit()` converts to `Text<letter8>`
   - Neither method implemented

4. **Generic Text<T> type**
   - `Text<letter32>` and `Text<letter8>` are different types
   - Generic record instantiation is broken

---

## The Path Forward

### Short Term (Immediate)

1. **Fix Bug 12.13**: Generic method template matching
   - Make `BackIndex_uaddr.resolve` find `BackIndex<I>.resolve`
   - Extract type parameters from mangled names
   - This unblocks most stdlib code

### Medium Term (Next Phase)

2. **Implement generic record instantiation properly**
   - Two-pass generation: declarations first, then definitions
   - Dependency graph sorting
   - Handle recursive types correctly

3. **Implement basic overload resolution**
   - Prefer non-generic over generic
   - Allow multiple definitions with different specificity

### Long Term (Future)

4. **Implement constraint system**
   - Parse `<T: Protocol>` syntax
   - Validate constraints during instantiation
   - Generate meaningful errors

5. **Implement Text methods**
   - `to_text()` protocol for all types
   - `to_8bit()` / `to_32bit()` for Text<T>
   - String interpolation

---

## Why Not Just Copy Rust/C++/Swift?

**Rust's approach:** Monomorphization + trait bounds
- **Pro:** Same as we're doing
- **Con:** Rust compiler is 1M+ lines, took 10+ years

**C++'s approach:** Template instantiation on demand
- **Pro:** Very powerful
- **Con:** Exponential code bloat, slow compilation, complex error messages

**Swift's approach:** Generic witness tables + dynamic dispatch
- **Pro:** Less code bloat
- **Con:** Runtime overhead, complex runtime system

**RazorForge's approach:** Monomorphization (like Rust) but simpler
- **Pro:** No runtime overhead, predictable codegen
- **Con:** Must get the dependency ordering and template matching right

---

## Current Bottleneck

**The #1 blocker is Bug 12.13: Generic method template matching.**

Until we can instantiate methods like `BackIndex<I>.resolve`, most stdlib code is broken. This is why `Console.show<T>` being commented out is a symptom, not the root cause.

Once template matching works, the other issues become tractable.

---

## Conclusion

**Generics are hard because:**
1. Multi-stage compilation with deferred instantiation
2. Type dependency graphs (ordering, cycles, recursion)
3. String-based template key matching (fragile!)
4. LLVM struct definition ordering requirements
5. Complex type substitution throughout AST

**Why generic functions work but generic types don't:**
- Functions have no dependencies (just code)
- Types have field dependencies (complex graph)
- Functions can be generated lazily at the end
- Types must be defined before first use

**To enable Console.show<T>:**
1. Fix method template matching (Bug 12.13) ← **CRITICAL**
2. Implement protocol constraints
3. Implement Text<T> methods (to_text, to_8bit)
4. Implement overload resolution

This is a **multi-month effort** for a production-ready implementation, but we can get a working prototype much faster by focusing on Bug 12.13 first.
