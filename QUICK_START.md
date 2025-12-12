# RazorForge Quick Start Guide

Get up and running with RazorForge in **VSCode** or **Rider** in under 5 minutes!

> **Note:** RazorForge supports two syntaxes:
> - **RazorForge** (`.rf`) - Go-like syntax with braces (no semicolons)
> - **Suflae** (`.sf`) - Python-like syntax with indentation

## One-Click Setup

### For VSCode Users

**Windows:**

```bash
setup-vscode.bat
```

**Linux/macOS:**

```bash
chmod +x setup-vscode.sh
./setup-vscode.sh
```

### For Rider Users

**Windows:**

```bash
setup-rider.bat
```

**Linux/macOS:**

```bash
chmod +x setup-rider.sh
./setup-rider.sh
```

These scripts will:

- ✅ Verify prerequisites (.NET SDK)
- ✅ Install recommended extensions (VSCode only)
- ✅ Build the RazorForge compiler
- ✅ Open your IDE with everything configured

---

## Manual Setup (Alternative)

### VSCode

1. Open: `code .`
2. Install recommended extensions (VSCode will prompt)
3. Build: `Ctrl+Shift+B`

### Rider

1. Open: `rider64.exe .` (or just double-click the `.sln`)
2. Rider auto-configures everything!
3. Build: `Ctrl+Shift+F9`

---

## First Steps

### 1. Build the Compiler

Press `Ctrl+Shift+B` or run:

```bash
dotnet build
```

### 2. Run an Example

**Option A: Using Task**

- `Ctrl+Shift+P` → "Tasks: Run Task" → "compile: console demo"

**Option B: Using Terminal**

```bash
dotnet run -- compile examples/console_demo.rf
```

**Option C: Using Debug**

- Press `F5` → Select "Debug: Compile Console Demo"

### 3. Create Your First Program

#### RazorForge Syntax (`.rf` - Go-like)

Create `hello.rf`:

```razorforge
routine start() {
    Console.print_line("Hello, RazorForge!")

    let x: s64 = 42_s64
    Console.print("The answer is: ")
    Console.print_line(x)
}
```

#### Suflae Syntax (`.sf` - Python-like)

Create `hello.sf`:

```python
routine start():
    show_line("Hello, Suflae!")

    let x: s64 = 42_s64
    show("The answer is: ")
    show_line(x)
```

Compile either:

```bash
dotnet run -- compile hello.rf
# or
dotnet run -- compile hello.sf
```

---

## Available Examples

All examples are in the `examples/` directory:

### RazorForge Syntax (`.rf`)

1. **console_demo.rf** - Interactive console I/O demonstrations
    - Number guessing game, calculator, prime checker, Fibonacci
    - Shows RazorForge's Go-like brace syntax (no semicolons)

2. **primitive_types_demo.rf** - Comprehensive primitive types showcase
    - s64, u64, f64 with all operations and conversions

3. **intrinsics_demo.rf** - Compiler intrinsics examples
    - Memory operations, arithmetic, bitwise, type conversions

### Suflae Syntax (`.sf`)

4. **suflae_console_demo.sf** - Same features as console_demo but with Python-like syntax
    - Shows indentation-based syntax
    - `if`/`elif`/`else`, `while` loops, etc.

Run any example:

```bash
dotnet run -- compile examples/console_demo.rf
dotnet run -- compile examples/suflae_console_demo.sf
```

---

## Common Commands

| Task             | Command                           |
|------------------|-----------------------------------|
| **Build**        | `Ctrl+Shift+B` or `dotnet build`  |
| **Run**          | `dotnet run`                      |
| **Test**         | `dotnet test`                     |
| **Clean**        | `dotnet clean`                    |
| **Compile File** | `dotnet run -- compile <file.rf>` |

---

## Keyboard Shortcuts

| Shortcut       | Action                |
|----------------|-----------------------|
| `Ctrl+Shift+B` | Build                 |
| `F5`           | Start Debugging       |
| `Ctrl+F5`      | Run Without Debugging |
| `Ctrl+Shift+P` | Command Palette       |
| `Ctrl+``       | Toggle Terminal       |

---

## Project Overview

```
RazorForge/
├── examples/          # Example RazorForge programs
├── stdlib/            # Standard library (.rf files)
│   ├── Console.rf    # Console I/O
│   └── NativeDataTypes/  # s64, u64, f64, etc.
├── src/               # Compiler source (C#)
├── native/            # C runtime
├── tests/             # Test files
└── docs/              # Documentation
```

---

## Next Steps

1. ✅ **Read the full setup guide:**
    - VSCode users: `VSCODE_SETUP.md`
    - Rider users: `RIDER_SETUP.md`
2. ✅ **Explore examples:** `examples/` directory
3. ✅ **Try both syntaxes:** Create both `.rf` and `.sf` files!
4. ✅ **Check documentation:** `docs/` directory

---

## Getting Help

- **VSCode Setup Issues:** See `VSCODE_SETUP.md` → Troubleshooting section
- **Language Documentation:** Check `docs/` directory
- **Intrinsics Reference:** `docs/INTRINSICS_API.md`
- **Memory Model:** `wiki/RazorForge-Memory-Model.md`

---

## Prerequisites

- **.NET 9.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Visual Studio Code** - [Download](https://code.visualstudio.com/)
- **CMake** (optional, for native development) - [Download](https://cmake.org/download/)

---

**Ready to start? Run the setup script and begin coding! 🚀**

```bash
# Windows
setup-vscode.bat

# Linux/macOS
./setup-vscode.sh
```
