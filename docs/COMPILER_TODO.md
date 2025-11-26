# Compiler Features TODO for Standard Library Support

This document outlines the compiler features that need to be implemented to fully support the RazorForge standard library (List<T>, Text<T>, etc.).

## Current Status

### ✅ Working Features
- Basic function declarations and calls
- Primitive types (s32, u64, f64, bool, etc.)
- Arithmetic and comparison operators
- If statements with proper Bool type checking
- Loop statements (basic loop with break)
- Local variables (let, var)
- LLVM IR code generation (with parameter loading fix)
- Import statement parsing (syntax recognized)

### ❌ Missing Features Required for Stdlib

## 1. Generic Types Support

**Priority: HIGH** - Required for `List<T>`, `Text<T>`, and all collection types

### What's Needed:

#### 1.1 Parser Changes
```csharp
// Need to parse generic type parameters
TypeIdentifier ParseTypeIdentifier() {
    string name = ConsumeTypeIdentifier();

    // Check for generic parameters: List<T>
    if (Match(TokenType.Less)) {
        List<TypeIdentifier> genericArgs = ParseGenericTypeArguments();
        Consume(TokenType.Greater);
        return new GenericTypeIdentifier(name, genericArgs);
    }

    return new SimpleTypeIdentifier(name);
}
```

#### 1.2 Semantic Analysis Changes
```csharp
// Need to handle generic type resolution
// When we see List<s32>, we need to:
// 1. Find the generic type definition List<T>
// 2. Substitute T with s32
// 3. Create a monomorphized version (like C++ templates or Rust)
```

#### 1.3 Code Generation Changes
```csharp
// Need to generate separate code for each concrete type:
// List<s32> -> List_s32 in LLVM IR
// List<u64> -> List_u64 in LLVM IR
// This is called "monomorphization"
```

### Examples Currently Failing:
```razorforge
let list: List<s32> = List<s32>()  # Generic type syntax not parsed
entity List<T> { ... }              # Generic entity not supported
routine push<T>(value: T) { ... }   # Generic methods not supported
```

---

## 2. Module/Import System

**Priority: HIGH** - Required for any stdlib usage

### What's Needed:

#### 2.1 Module Resolution
```csharp
// When we see: import Collections/List
// Need to:
// 1. Find the file: stdlib/Collections/List.rf
// 2. Parse it
// 3. Add its declarations to the symbol table
// 4. Handle transitive imports (List imports memory/DynamicSlice)
```

#### 2.2 Import Path Resolution
```csharp
class ModuleResolver {
    // Resolve import path to file system path
    string ResolveImport(string importPath) {
        // "Collections/List" -> "stdlib/Collections/List.rf"
        // Handle multiple search paths (stdlib, local, etc.)
    }

    // Handle circular imports
    HashSet<string> _loadedModules;
}
```

#### 2.3 Symbol Table Management
```csharp
// Need to track which symbols come from which module
// Support qualified names: Collections.List vs just List
// Handle name conflicts
class SymbolTable {
    Dictionary<string, Symbol> _symbols;
    Dictionary<string, Module> _modules;

    void AddImportedSymbols(Module module, ImportDeclaration import);
}
```

### Examples Currently Failing:
```razorforge
import Collections/List           # Parsed but not resolved
import memory/DynamicSlice        # Multi-level imports not working
let list = List<s32>()            # List not found in symbol table
```

---

## 3. Advanced Type Features

**Priority: MEDIUM** - Needed for full stdlib

### 3.1 Method Syntax (already partially working?)
```razorforge
routine List<T>.push(me: List<T>, value: T) { ... }
list.push(42)  # Needs proper method call resolution
```

### 3.2 Struct Field Access
```razorforge
entity List<T> {
    private data: DynamicSlice
    private len: u64
}

# Need to access fields
me.len        # Field access
me.data       # Field access
```

### 3.3 Generic Method Calls
```razorforge
me.data.read<T>(offset)    # Generic method call
me.data.write<T>(offset, value)
sizeof<T>()                # Generic intrinsic
```

---

## 4. Intrinsic Functions

**Priority: HIGH** - Required for List implementation

### Required Intrinsics:
```razorforge
# Memory operations (used by List)
sizeof<T>() -> u64                    # Size of type T in bytes
crash(message: text)                   # Panic/abort

# DynamicSlice operations
external("C") routine claim_dynamic!(bytes: sysuint) -> sysuint
external("C") routine release_dynamic!(address: sysuint)
external("C") routine memory_copy!(src: sysuint, dest: sysuint, bytes: sysuint)
```

### Current Status:
- Some may already be implemented
- Need to verify they work with generic types

---

## 5. Type System Enhancements

**Priority: MEDIUM**

### 5.1 Option Types
```razorforge
routine next() -> T? {      # Optional return type
    return none
    return some(value)
}
```

### 5.2 Type Inference Improvements
```razorforge
let list = List<s32>()     # Currently needs explicit type annotation
# Could infer from constructor call
```

---

## Implementation Priority

### Phase 1: Minimum Viable (Can test List)
1. **Generic type parsing** - Parse `List<T>` syntax
2. **Module resolution** - Load imported files
3. **Basic monomorphization** - Generate code for `List<s32>`
4. **Intrinsics** - `sizeof<T>()`, `crash()`

### Phase 2: Full Collection Support
1. **Generic constraints** - `where T: Comparable`
2. **Multiple type parameters** - `Dict<K, V>`
3. **Associated types** - For iterators
4. **Option types** - `T?` for `none`/`some`

### Phase 3: Advanced Features
1. **Type inference** - Reduce verbosity
2. **Method chaining** - Fluent APIs
3. **Generic bounds** - Type constraints
4. **Specialization** - Optimize specific types

---

## Testing Roadmap

### Stage 1: Non-Generic Collections
Test with simple types that don't need generics:
```razorforge
# Create specialized versions without generics for testing
entity ListS32 { ... }  # List specialized for s32
```

### Stage 2: Single Generic Parameter
Once generics work:
```razorforge
entity List<T> { ... }
let numbers = List<s32>()
let flags = List<bool>()
```

### Stage 3: Complex Generics
```razorforge
entity Dict<K, V> { ... }
entity Result<T, E> { ... }
```

---

## Current Workarounds

Until generics are implemented, you can:

1. **Create specialized types**
   ```razorforge
   entity ListS32 {
       private data: DynamicSlice
       private len: u64
   }
   ```

2. **Test without imports**
   - Inline the code instead of importing
   - Test individual functions

3. **Focus on non-generic code**
   - Test arithmetic, conditionals, loops
   - Build up working compiler incrementally

---

## Standard Library Status

### ✅ Ready (Architecturally Sound)
- `List<T>` - stdlib/Collections/List.rf
- `Text<T>` - stdlib/Text/entity/Text.rf
- `letter` - stdlib/Text/record/letter.rf
- `DynamicSlice` - stdlib/memory/DynamicSlice.rf

### ⏳ Waiting on Compiler
- All entity collections (List, Dict, Set, etc.)
- All text types (Text, TextBuffer)
- Generic functions and methods
- Module imports

### 📝 Architecture Correct
The stdlib is designed correctly according to your wiki documentation. Once the compiler supports generics and imports, it should work immediately!

---

## Resources for Implementation

### Reference Implementations
1. **Rust** - Good example of monomorphization and borrow checker
2. **C++** - Template instantiation model
3. **TypeScript** - Module resolution system
4. **Swift** - Generic constraints and protocols

### Key Papers
- "Type Classes: Exploring the Design Space" (Jones, 1997)
- "Implementing Type Classes" (Jones, 1993)
- "Generic Java" (Bracha et al., 1998)

---

## Questions to Consider

1. **Monomorphization vs Boxing**
   - Generate code for each type (Rust, C++) ✅ Recommended
   - Use type erasure/boxing (Java, Go)

2. **Separate Compilation**
   - How to compile generic code before knowing concrete types?
   - Do we inline generic code at call sites?

3. **Error Messages**
   - How to give good errors for generic code?
   - Show which instantiation failed?

4. **Compilation Time**
   - Monomorphization can be slow (many instantiations)
   - Need incremental compilation?

---

## Summary

The standard library (List, Text, etc.) is **ready and well-designed**. The main blocker is compiler support for:

1. **Generic types** (highest priority)
2. **Module imports** (highest priority)
3. **Intrinsic functions** (needed for memory ops)

Once these are implemented, the entire collection system should work!

Good night! 🌙
