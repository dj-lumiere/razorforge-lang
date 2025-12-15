# Multi-Target Code Generation in RazorForge

This document explains how to use RazorForge's multi-target code generation system to compile for different platforms
and architectures.

## Overview

RazorForge supports cross-compilation to multiple target platforms through the `TargetPlatform` class. This allows you
to generate LLVM IR optimized for specific combinations of CPU architecture and operating system.

## Architecture-Dependent Types

The following RazorForge types automatically adapt to the target platform:

| Type               | Description            | Size Variation                                    |
|--------------------|------------------------|---------------------------------------------------|
| `uaddr` / `saddr`  | Pointer-sized integers | 32-bit on x86, 64-bit on x86_64/ARM64             |
| `cwchar`           | C wchar_t              | 16-bit on Windows, 32-bit on Unix/Linux/macOS     |
| `clong` / `culong` | C long/unsigned long   | 32-bit on Windows, 64-bit on Unix (LLP64 vs LP64) |
| `csptr` / `cuptr`  | C intptr_t/uintptr_t   | Follows pointer size                              |
| `cvoid`            | C void (as integer)    | Follows pointer size                              |

## Supported Platforms

### Tested Platforms

#### x86_64 (64-bit Intel/AMD)

- **Linux** (System V ABI)
    - Target triple: `x86_64-pc-linux-gnu`
    - Pointer size: 64-bit
    - wchar_t: 32-bit (4 bytes)
    - long: 64-bit (LP64 model)

- **Windows** (Microsoft ABI)
    - Target triple: `x86_64-pc-windows-msvc`
    - Pointer size: 64-bit
    - wchar_t: 16-bit (2 bytes)
    - long: 32-bit (LLP64 model)

- **macOS** (Darwin ABI)
    - Target triple: `x86_64-apple-darwin`
    - Pointer size: 64-bit
    - wchar_t: 32-bit (4 bytes)
    - long: 64-bit (LP64 model)

#### x86 (32-bit Intel/AMD)

- **Linux**
    - Target triple: `i686-pc-linux-gnu`
    - Pointer size: 32-bit
    - wchar_t: 32-bit
    - long: 32-bit

- **Windows**
    - Target triple: `i686-pc-windows-msvc`
    - Pointer size: 32-bit
    - wchar_t: 16-bit
    - long: 32-bit

#### ARM64 (AArch64)

- **Linux**
    - Target triple: `aarch64-unknown-linux-gnu`
    - Pointer size: 64-bit
    - wchar_t: 32-bit
    - long: 64-bit

- **macOS** (Apple Silicon)
    - Target triple: `arm64-apple-darwin`
    - Pointer size: 64-bit
    - wchar_t: 32-bit
    - long: 64-bit

- **Windows**
    - Target triple: `aarch64-pc-windows-msvc`
    - Pointer size: 64-bit
    - wchar_t: 16-bit
    - long: 32-bit

#### ARM (32-bit)

- **Linux**
    - Target triple: `armv7-unknown-linux-gnueabihf`
    - Pointer size: 32-bit
    - wchar_t: 32-bit
    - long: 32-bit

#### RISC-V

- **RISC-V 64-bit Linux**
    - Target triple: `riscv64-unknown-linux-gnu`
    - Pointer size: 64-bit
    - wchar_t: 32-bit
    - long: 64-bit

- **RISC-V 32-bit Linux**
    - Target triple: `riscv32-unknown-linux-gnu`
    - Pointer size: 32-bit
    - wchar_t: 32-bit
    - long: 32-bit

#### WebAssembly

- **WASM32**
    - Target triple: `wasm32-unknown-wasi`
    - Pointer size: 32-bit
    - wchar_t: 32-bit
    - long: 32-bit

- **WASM64**
    - Target triple: `wasm64-unknown-wasi`
    - Pointer size: 64-bit
    - wchar_t: 32-bit
    - long: 64-bit

## Usage Examples

### Basic Usage - Default Target

```csharp
using Compilers.Shared.CodeGen;

// Use default target (x86_64 Linux)
var codegen = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal);
codegen.Generate(programAst);
string llvmIR = codegen.GetGeneratedCode();
```

### Specifying a Target Platform

```csharp
using Compilers.Shared.CodeGen;

// Windows x86_64
var windowsTarget = new TargetPlatform(TargetArchitecture.X86_64, TargetOS.Windows);
var codegen = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal, windowsTarget);
codegen.Generate(programAst);
string llvmIR = codegen.GetGeneratedCode();
```

### Using Target Triple Strings

```csharp
using Compilers.Shared.CodeGen;

// Parse from LLVM target triple
var platform = TargetPlatform.FromTriple("aarch64-apple-darwin");
var codegen = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal, platform);
codegen.Generate(programAst);
```

### Cross-Compilation Examples

#### Compile for ARM64 Linux

```csharp
var armLinux = new TargetPlatform(TargetArchitecture.ARM64, TargetOS.Linux);
var codegen = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal, armLinux);
codegen.Generate(programAst);
// Output will use 64-bit pointers, 32-bit wchar_t, 64-bit long
```

#### Compile for Windows ARM64

```csharp
var armWindows = new TargetPlatform(TargetArchitecture.ARM64, TargetOS.Windows);
var codegen = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal, armWindows);
codegen.Generate(programAst);
// Output will use 64-bit pointers, 16-bit wchar_t, 32-bit long
```

#### Compile for WebAssembly

```csharp
var wasmTarget = new TargetPlatform(TargetArchitecture.WASM32, TargetOS.WASI);
var codegen = new LLVMCodeGenerator(Language.RazorForge, LanguageMode.Normal, wasmTarget);
codegen.Generate(programAst);
// Output will use 32-bit pointers and WebAssembly-compatible IR
```

## Generated Output Differences

### Example: uaddr on Different Platforms

**RazorForge Code:**

```razorforge
routine example!() -> uaddr {
    let ptr_value: uaddr = 0x1000
    return ptr_value
}
```

**x86_64 Linux (64-bit):**

```llvm
define i64 @example() {
entry:
  %ptr_value = alloca i64
  store i64 4096, i64* %ptr_value
  %tmp0 = load i64, i64* %ptr_value
  ret i64 %tmp0
}
```

**x86 Linux (32-bit):**

```llvm
define i32 @example() {
entry:
  %ptr_value = alloca i32
  store i32 4096, i32* %ptr_value
  %tmp0 = load i32, i32* %ptr_value
  ret i32 %tmp0
}
```

### Example: cwchar on Different Platforms

**RazorForge Code:**

```razorforge
external routine wcslen!(str: cptr<cwchar>) -> culong
```

**Unix/Linux (32-bit wchar_t):**

```llvm
declare i64 @wcslen(i32*)
```

**Windows (16-bit wchar_t):**

```llvm
declare i32 @wcslen(i16*)
```

## Platform Detection

The `TargetPlatform` class automatically configures:

1. **LLVM Target Triple** - Identifies architecture, vendor, OS, and ABI
2. **Data Layout String** - Specifies endianness, pointer sizes, and alignment
3. **Type Sizes** - Sets correct sizes for architecture-dependent types

## Data Layout Strings

Each platform has a unique data layout string that tells LLVM how to organize data in memory:

- `e` - Little-endian
- `p:32:32` - 32-bit pointers with 32-bit alignment
- `p:64:64` - 64-bit pointers with 64-bit alignment
- `i64:64` - 64-bit integers aligned to 64 bits
- `n32:64` - Native integer widths are 32 and 64 bits

## Best Practices

1. **Always specify the target** when cross-compiling
2. **Use architecture-independent types** (`s32`, `u64`) for portable code
3. **Use architecture-dependent types** (`uaddr`, `saddr`) only when interfacing with platform APIs
4. **Test on the target platform** or use proper cross-compilation toolchains

## CLI Integration (Future)

Future versions will support specifying the target via command-line:

```bash
# Target-specific compilation
razorforge compile --target x86_64-pc-linux-gnu mycode.rf
razorforge compile --target aarch64-apple-darwin mycode.rf
razorforge compile --target wasm32-unknown-wasi mycode.rf

# Architecture shorthand
razorforge compile --arch arm64 --os macos mycode.rf
```

## Limitations

- Cross-compilation requires appropriate linkers and system libraries for the target
- Some intrinsics may only be available on specific architectures
- WebAssembly has limited standard library support
- Windows calling conventions may differ from Unix for some functions

## See Also

- [C FFI Types Documentation](C_FFI_IMPLEMENTATION.md)
- [Numeric Types Reference](NUMERIC_TYPES.md)
- [LLVM Target Triples](https://llvm.org/docs/LangRef.html#target-triple)
