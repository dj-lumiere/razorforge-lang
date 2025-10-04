# Error Handling in RazorForge

## Safe Sum Types vs Dangerous Chimeras

RazorForge provides **two different approaches** to algebraic data types:

### ✅ Safe Sum Types (`variant`)

- **Compile-time optimized** and memory-safe
- **No danger blocks required**
- Used for `Result<T>` and `Maybe<T>`
- Pattern matching with direct field access: `Ok(value) => ...`

```razorforge
# Safe - no danger block needed
variant Result<T> {
    Ok(T),
    Err(Error)
}

variant Maybe<T> {
    Some(T),
    None
}
```

### ⚠️ Dangerous Reality Benders (`chimera`)

- **Runtime type checking required**
- **Must be used in `danger!` blocks**
- Break compile-time safety guarantees
- Used only when absolutely necessary (FFI, emergency cases)

```razorforge
# Dangerous - requires danger! block
danger! {
    chimera UnsafeUnion {
        Integer(s32),
        Text(Text)
    }
}
```

## The Rule

- **`Result<T>` and `Maybe<T>` are safe `variant` types**
- They do NOT require `danger!` blocks
- They are compile-time optimized and memory-safe
- The Reality-Bender documentation confirms: *"`Result<T>` and `Maybe<T>` are always safe and compile-time
  optimized—they are NOT chimeras"*

## Pattern Matching Syntax

```razorforge
# Safe variants - no "is" keyword needed
when result {
    Ok(value) => handle_success(value),
    Err(error) => handle_error(error)
}

when maybe {
    Some(value) => use_value(value),
    None => handle_none()
}
```

## Available Types

- **`Error`** - Standard error record with message and optional code
- **`Result<T>`** - Safe error handling variant (Ok/Err)
- **`Maybe<T>`** - Safe nullable variant (Some/None)
