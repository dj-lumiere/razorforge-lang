# RazorForge Native Mathematics Libraries

This directory contains the native C/C++ libraries used by RazorForge for high-precision arithmetic operations.

## Libraries Included

### libdfp - IEEE 754 Decimal Floating Point
- **Purpose**: Implements d32, d64, and d128 decimal floating point types
- **Location**: `native/libdfp/`
- **Usage**: Used for precise decimal arithmetic in RazorForge
- **Integration**: Via `LibDfpInterop.cs` and `Decimal32.cs`, `Decimal64.cs`, `Decimal128.cs`

### libbf - Arbitrary Precision Arithmetic
- **Purpose**: Provides arbitrary precision integers and rationals
- **Location**: `native/libbf/`
- **Usage**: Used for integer operations that may exceed standard integer ranges
- **Integration**: Via `LibBfInterop.cs` and `BigInteger.cs`

### mafm - Multiple Arithmetic with Fixed Mantissa
- **Purpose**: Multiple precision arithmetic for decimal numbers
- **Location**: `native/mafm/`
- **Usage**: Used for decimal operations requiring arbitrary precision
- **Integration**: Via `MafmInterop.cs` and `HighPrecisionDecimal.cs`

## Setup Instructions

1. **Add your library source code**:
   ```bash
   # Copy your libdfp sources to:
   native/libdfp/
   
   # Copy your libbf sources to:
   native/libbf/
   
   # Copy your mafm sources to:
   native/mafm/
   ```

2. **Build the libraries**:
   ```bash
   # On Windows:
   native\build.bat
   
   # On Linux/macOS:
   ./native/build.sh
   ```

3. **The build system will**:
   - Compile all three libraries into a single `razorforge_math` shared library
   - Copy the library to your output directories
   - Make it available for P/Invoke calls from C#

## Library Structure

```
native/
├── CMakeLists.txt          # Main build configuration
├── build.bat              # Windows build script
├── build.sh               # Linux/macOS build script
├── include/               # Exported headers
├── build/                 # Build output directory
├── libdfp/                # IEEE 754 decimal floating point
│   ├── CMakeLists.txt
│   └── [your libdfp sources]
├── libbf/                 # Arbitrary precision arithmetic
│   ├── CMakeLists.txt
│   └── [your libbf sources]
├── mafm/                  # Multiple precision arithmetic
│   ├── CMakeLists.txt
│   └── [your mafm sources]
└── runtime/               # RazorForge runtime support
    ├── runtime.c
    ├── i8.c
    ├── i16.c
    └── types.h
```

## Integration with C#

The native libraries are integrated into the C# project through:

1. **P/Invoke Interop Classes**: 
   - `LibDfpInterop.cs` - Direct FFI bindings
   - `LibBfInterop.cs` - Direct FFI bindings  
   - `MafmInterop.cs` - Direct FFI bindings

2. **High-level Wrapper Classes**:
   - `Decimal32.cs`, `Decimal64.cs`, `Decimal128.cs` - IEEE decimal types
   - `BigInteger.cs` - Arbitrary precision integers
   - `HighPrecisionDecimal.cs` - Arbitrary precision decimals

3. **Automatic Build Integration**:
   - Native libraries are automatically built before C# compilation
   - Libraries are copied to output directories
   - Runtime loading is handled automatically

## Requirements

- **CMake 3.20+**
- **C11/C++17 compatible compiler**
- **Windows**: Visual Studio 2019+ or MinGW
- **Linux**: GCC 7+ or Clang 6+
- **macOS**: Xcode 10+ or Command Line Tools

## Usage in RazorForge Code

```csharp
// Using IEEE 754 decimal types
var d32 = new Decimal32("123.456");
var result = d32 + new Decimal32("78.901");

// Using arbitrary precision integers
var bigInt = new BigInteger("123456789012345678901234567890");
var doubled = bigInt * 2;

// Using high precision decimals
var decimal = new HighPrecisionDecimal("0.123456789012345678901234567890");
var precise = decimal * new HighPrecisionDecimal("2.5");
```