# ✅ RESOLVED - See COMPILER_TODO.md for status
# Bug 12.13: Generic Method Template Matching - Detailed Analysis

**Date:** 2025-12-04
**Status:** Partially Fixed (Code Generator), Semantic Analyzer Still Needs Work

## Summary

Generic methods on generic types cannot be instantiated because the compiler fails to match the instantiated type name to the generic template. This affects both semantic analysis and code generation.

## The Core Problem

### What Should Work

```razorforge
record BackIndex<I> {
    offset: I
}

routine BackIndex<I>.resolve(me: BackIndex<I>, length: I) -> I {
    return length - me.offset
}

let bi = BackIndex<uaddr>(offset: 5_uaddr)
let result: uaddr = bi.resolve(length: 10_uaddr)  # SHOULD WORK!
```

### What Actually Happens

1. **Template Storage:** Method template stored as `BackIndex<I>.resolve`
2. **Method Call:** When called on `BackIndex<uaddr>`, looks for `BackIndex_uaddr.resolve`
3. **Mismatch:** Cannot match `BackIndex_uaddr` to template `BackIndex<I>`
4. **Result:** Method not found or wrong types inferred

## Affected Components

### 1. Semantic Analyzer (`src/Analysis/SemanticAnalyzer.Calls.cs`)

**Problem:** Cannot resolve return types for methods on generic type instances.

**Location:** `GetKnownMethodReturnType()` method (lines 262-303)

**Current Behavior:**
- Only knows about hardcoded methods (`to_uaddr`, `resolve`, etc.)
- Returns fixed types (`uaddr`, `bool`, etc.)
- Does NOT look up methods in symbol table
- Does NOT perform generic type parameter substitution

**Needed Fix:**
```csharp
private TypeInfo? GetKnownMethodReturnType(TypeInfo? objectType, string methodName)
{
    // NEW: Check if objectType is a generic instantiation (e.g., "BackIndex<uaddr>")
    if (objectType != null && objectType.Name.Contains('<'))
    {
        // Extract: "BackIndex<uaddr>" -> base="BackIndex", args=["uaddr"]
        string baseName = ExtractGenericBaseName(objectType.Name);
        var typeArgs = ExtractTypeArguments(objectType.Name);

        // Look for template: "BackIndex<I>.methodName"
        string templateName = $"{baseName}<{string.Join(", ", GetTemplateParameters(baseName))}>.{methodName}";
        Symbol? methodSymbol = _symbolTable.Lookup(templateName);

        if (methodSymbol is FunctionSymbol func)
        {
            // Substitute type parameters in return type
            // If return type is "I" and typeArgs=["uaddr"], return "uaddr"
            return SubstituteTypeParameters(func.ReturnType, typeArgs);
        }
    }

    // Fallback to existing hardcoded methods...
    return methodName switch { ... };
}
```

**Impact:** Without this, generic method calls return `unknown` type, causing semantic errors.

---

###2. Code Generator (`src/CodeGen/LLVMCodeGenerator.MethodCalls.cs`)

**Problem:** Argument types not tracked for generic method calls with literal arguments.

**Location:** `VisitGenericMethodCallExpression()` method (lines 54-72, 220-235, 299-314)

**Fix Applied:** ✅ **COMPLETED**

**Changes Made:**
```csharp
// OLD CODE: Failed when argument not in _tempTypes
if (!_tempTypes.TryGetValue(key: argTemp, value: out LLVMTypeInfo? ti))
{
    throw CodeGenError.TypeResolutionFailed(...);
}

// NEW CODE: Fall back to GetTypeInfo for literals and other expressions
LLVMTypeInfo ti;
if (!_tempTypes.TryGetValue(key: argTemp, value: out ti))
{
    ti = GetTypeInfo(expr: arg);  // Get from semantic analysis
}
```

**Files Modified:**
- `src/CodeGen/LLVMCodeGenerator.MethodCalls.cs` (3 locations)

**Impact:** Generic constructors and methods with literal arguments now get proper type information.

---

### 3. Code Generator Template Matching (`src/CodeGen/LLVMCodeGenerator.Functions.cs`)

**Problem:** Cannot match instantiated method names to generic templates.

**Location:** `FindMatchingGenericTemplate()` method (likely around line 950-983 in Expressions.cs based on context)

**Current Behavior:**
- Already implemented for matching types like `BackIndex<uaddr>` to templates `BackIndex<I>`
- Used in `VisitCallExpression` for member expressions (lines 952-983)
- Should work but may have edge cases

**Potential Issue:**
The existing `FindMatchingGenericTemplate` might not be called in all the right places, or might not handle all naming variations correctly.

---

## Test Cases

### Minimal Test Case

```razorforge
record TestType<T> {
    value: T
}

routine TestType<T>.get_value(me: TestType<T>) -> T {
    return me.value
}

routine start() {
    let instance: TestType<s64> = TestType<s64>(value: 42_s64)
    let result = instance.get_value()  # Should infer type as s64
}
```

**Current Status:**
- ❌ Semantic Analysis: Passes (type is `unknown`)
- ❌ Code Generation: Fails - tries to map `TestType` to LLVM

**Expected:**
- ✅ Semantic Analysis: Should infer `result` type as `s64`
- ✅ Code Generation: Should generate call to `TestType_s64.get_value`

---

### Real-World Test Case (BackIndex)

```razorforge
import core/BackIndex

routine start() {
    let bi = BackIndex<uaddr>(offset: 5_uaddr)
    let result = bi.resolve(length: 10_uaddr)
}
```

**Current Status:**
- ❌ Fails at constructor: `BackIndex<uaddr>(...)` treated as nested function
- Cannot even reach the method call test

**Blockers:**
1. Generic type constructor handling (separate issue)
2. After that's fixed, method call will hit Bug 12.13

---

## Root Cause Analysis

### Why This Is Hard

**Multi-Stage Compilation:**
```
Source → Parser → Semantic Analyzer → Code Generator → LLVM
         ^        ^                    ^
         |        |                    |
   Stores     Should resolve       Should match
  templates   method types        templates
```

**The Problem:**
- Parser: Stores `BackIndex<I>.resolve` as template
- Semantic Analyzer: Sees call on `BackIndex<uaddr>` instance
- Semantic Analyzer: Cannot find method (doesn't match template)
- Semantic Analyzer: Returns `unknown` type
- Code Generator: Tries to map `unknown` type to LLVM
- Code Generator: Fails

**The Solution:**
- Semantic Analyzer needs to perform template matching JUST LIKE code generator does
- Must extract type arguments from instantiated type
- Must substitute into return type
- Must validate generic constraints (future)

---

## Implementation Plan

### Phase 1: Semantic Analyzer Template Matching (**NEEDED NOW**)

**File:** `src/Analysis/SemanticAnalyzer.Calls.cs`

**Tasks:**
1. Add `ExtractGenericBaseName(string typeName)` helper
2. Add `ExtractTypeArguments(string typeName)` helper (might exist)
3. Add `GetTemplateParameters(string baseName)` - look up template definition
4. Add `SubstituteTypeParameters(TypeInfo returnType, List<string> args)` method
5. Modify `GetKnownMethodReturnType()` to check symbol table for generic methods
6. Handle method overloading (generic vs non-generic)

**Estimated Complexity:** Medium (2-3 hours)

**Risk:** Low - semantic analyzer already has similar logic for type resolution

---

### Phase 2: Code Generator Template Matching Verification (**VERIFY**)

**File:** `src/CodeGen/LLVMCodeGenerator.Expressions.cs` (lines 952-983)

**Tasks:**
1. Verify `FindMatchingGenericTemplate` is called correctly
2. Add debug logging to see what templates are being matched
3. Test with `BackIndex<uaddr>.resolve` and `TestType<s64>.get_value`
4. Ensure `InstantiateGenericFunction` is called with correct arguments

**Estimated Complexity:** Low (1 hour)

**Risk:** Low - code already exists, just needs verification

---

### Phase 3: Generic Type Constructor Handling (**SEPARATE BUG**)

**File:** Parser/Semantic Analyzer/Code Generator

**Problem:** `BackIndex<uaddr>(offset: 5_uaddr)` treated as function call, not constructor

**This is NOT Bug 12.13** - this is a separate issue with how generic type constructors are parsed and generated.

---

## Workarounds

### For Testing

```razorforge
# Instead of:
let result: s64 = instance.get_value()  # Semantic error

# Use:
let result = instance.get_value()  # No type check, but still fails in codegen

# Best workaround: Don't use generics yet
```

---

## Dependencies

**Bug 12.13 BLOCKS:**
- All stdlib generic types (`List<T>`, `Text<T>`, `BackIndex<I>`, `Range<T>`)
- Generic `Console.show<T>()` function
- Any user-defined generic types with methods

**Bug 12.13 DEPENDS ON:**
- ✅ Generic function monomorphization (working)
- ✅ Generic type parsing (working)
- ❌ Semantic analyzer template matching (NEEDS IMPLEMENTATION)
- ✅ Code generator argument type resolution (FIXED)

---

## Related Issues

- **Generic Type Constructors:** Separate parsing/codegen issue
- **Generic Constraints:** Future feature (`<T: Protocol>`)
- **Generic Overload Resolution:** Future feature (choose between generic/non-generic)

---

## Conclusion

Bug 12.13 is **partially fixed**:
- ✅ Code generator can handle literal arguments (fixed)
- ❌ Semantic analyzer cannot resolve generic method types (CRITICAL)
- ⚠️  Code generator template matching exists but needs verification

**Next Steps:**
1. Implement semantic analyzer template matching
2. Test with simple generic types
3. Test with stdlib generic types
4. Mark as complete when semantic analysis passes

**Estimated Time to Complete:** 3-4 hours of focused work

**Priority:** 🚨 **CRITICAL** - Blocks all generic type usage in RazorForge
