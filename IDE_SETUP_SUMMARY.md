# RazorForge IDE Setup - Complete Summary

This document summarizes all IDE configurations for RazorForge development.

---

## Quick Start

### For VSCode Users

```bash
setup-vscode.bat    # Windows
./setup-vscode.sh   # Linux/macOS
```

**Full docs:** `VSCODE_SETUP.md`

### For Rider Users

```bash
setup-rider.bat     # Windows
./setup-rider.sh    # Linux/macOS
```

**Full docs:** `RIDER_SETUP.md`

---

## What Was Configured

### ✅ VSCode Configuration

**Location:** `.vscode/`

| File              | Purpose                                           |
|-------------------|---------------------------------------------------|
| `tasks.json`      | Build, run, test, and example compilation tasks   |
| `launch.json`     | Debug configurations for compiler and examples    |
| `settings.json`   | Workspace settings, file associations, formatting |
| `extensions.json` | Recommended extensions (C#, C++, Git, etc.)       |

**Key Features:**

- One-key build (`Ctrl+Shift+B`)
- Compile current file
- Debug configurations for all examples
- Pre-configured for both `.rf` and `.sf` files

---

### ✅ Rider Configuration

**Location:** `.idea/`

| Path                                         | Purpose                                      |
|----------------------------------------------|----------------------------------------------|
| `.idea.RazorForge/.idea/runConfigurations/`  | Run/debug configurations                     |
| `fileTemplates/`                             | File templates for new `.rf` and `.sf` files |
| `.idea.RazorForge/.idea/inspectionProfiles/` | Code inspection settings                     |

**Run Configurations:**

1. Build RazorForge
2. Compile: Current File
3. Compile: Intrinsics Demo
4. Compile: Primitive Types Demo
5. Compile: Console Demo
6. Compile: Suflae Current File
7. Compile: Suflae Console Demo
8. Language Server
9. Run: All Tests

**File Templates:**

- RazorForge File.rf
- RazorForge Record Type.rf
- RazorForge Entity Type.rf
- Suflae File.sf
- Suflae Record Type.sf
- Suflae Entity Type.sf

---

## Language Support

### RazorForge (`.rf` files)

- **Syntax:** Rust/C-like with braces `{}`
- **Example:** `examples/console_demo.rf`
- **File association:** Configured in both IDEs

### Suflae (`.sf` files)

- **Syntax:** Python-like with indentation
- **Example:** `examples/suflae_console_demo.sf`
- **File association:** Configured in both IDEs

---

## Available Examples

| Example                   | Description                    | Syntax     |
|---------------------------|--------------------------------|------------|
| `console_demo.rf`         | Interactive console I/O demos  | RazorForge |
| `primitive_types_demo.rf` | s64, u64, f64 operations       | RazorForge |
| `intrinsics_demo.rf`      | Compiler intrinsics showcase   | RazorForge |
| `suflae_console_demo.sf`  | Console I/O with Python syntax | Suflae     |

---

## Linting Options

### Option 1: Language Server (Recommended)

```bash
dotnet run -- lsp
```

- Real-time diagnostics
- Syntax and semantic analysis
- Type checking

### Option 2: File Watchers (Rider)

Configure in `File` → `Settings` → `Tools` → `File Watchers`

### Option 3: External Tool Integration

Add linting command as external tool in your IDE

### Option 4: Pre-commit Hooks

Use Git hooks for automatic linting before commits

**See `RIDER_SETUP.md`** for detailed linting setup instructions.

---

## Common Workflows

### Building

**VSCode:** `Ctrl+Shift+B`
**Rider:** `Ctrl+Shift+F9`

### Running Examples

**VSCode:** `Ctrl+Shift+P` → "Tasks: Run Task" → Select example
**Rider:** Dropdown → Select configuration → `Shift+F10`

### Debugging

**VSCode:** `F5` → Select debug configuration
**Rider:** `Shift+F9` → Debugger starts

### Compiling Current File

**VSCode:** `Ctrl+Shift+P` → "run: compile file"
**Rider:** Select "Compile: Current File" → `Shift+F10`

---

## Project Structure

```
RazorForge/
├── .vscode/                          # VSCode configuration
│   ├── tasks.json
│   ├── launch.json
│   ├── settings.json
│   └── extensions.json
├── .idea/                            # Rider configuration
│   ├── .idea.RazorForge/.idea/
│   │   ├── runConfigurations/       # Run configs
│   │   └── inspectionProfiles/      # Inspections
│   └── fileTemplates/               # File templates
├── examples/                         # Example programs
│   ├── console_demo.rf              # RazorForge syntax
│   ├── primitive_types_demo.rf
│   ├── intrinsics_demo.rf
│   └── suflae_console_demo.sf       # Suflae syntax
├── src/                              # Compiler source (C#)
├── stdlib/                           # Standard library
│   ├── Console.rf                   # Console I/O
│   └── NativeDataTypes/             # s64, u64, f64, etc.
├── native/                           # C runtime
├── tests/                            # Tests
└── docs/                             # Documentation
```

---

## Prerequisites

### Required

- **.NET 9.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
- **IDE:**
    - **VSCode** - [Download](https://code.visualstudio.com/)
    - **OR Rider** - [Download](https://www.jetbrains.com/rider/)

### Optional

- **CMake** (for native development) - [Download](https://cmake.org/download/)
- **Visual Studio Build Tools** (Windows native builds)
- **GCC/Clang** (Linux/macOS native builds)

---

## Documentation Map

| Document                          | Purpose                               |
|-----------------------------------|---------------------------------------|
| `QUICK_START.md`                  | Get started in 5 minutes              |
| `VSCODE_SETUP.md`                 | Complete VSCode configuration guide   |
| `RIDER_SETUP.md`                  | Complete Rider configuration guide    |
| `IDE_SETUP_SUMMARY.md`            | This file - overview of all IDE setup |
| `docs/INTRINSICS_API.md`          | Compiler intrinsics reference         |
| `wiki/RazorForge-Memory-Model.md` | Memory management documentation       |

---

## Troubleshooting

### Build Fails

- Verify .NET 9.0 SDK is installed: `dotnet --version`
- Clean and rebuild: `dotnet clean && dotnet build`

### IDE Doesn't Recognize Configuration

**VSCode:**

- Reload window: `Ctrl+Shift+P` → "Reload Window"
- Reinstall extensions

**Rider:**

- Invalidate caches: `File` → `Invalidate Caches` → "Invalidate and Restart"

### Native Build Fails

```bash
cd native
rm -rf build
mkdir build
cd build
cmake ..
cmake --build .
```

---

## Next Steps

1. ✅ Choose your IDE (VSCode or Rider)
2. ✅ Run the setup script
3. ✅ Build the project
4. ✅ Try compiling an example
5. ✅ Create your first `.rf` or `.sf` file
6. ✅ Explore the documentation

---

## Support

- **Issues:** Report at your GitHub issues page
- **Documentation:** See `docs/` directory
- **Examples:** See `examples/` directory

---

**Happy Coding with RazorForge! 🔥**

**Supported Syntaxes:**

- 🦀 RazorForge (`.rf`) - Rust/C-like
- 🐍 Suflae (`.sf`) - Python-like

**Supported IDEs:**

- 🎨 Visual Studio Code
- 🚀 JetBrains Rider
