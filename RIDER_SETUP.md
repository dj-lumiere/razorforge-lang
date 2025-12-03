# Rider One-Click Setup Guide for RazorForge

This guide will help you set up JetBrains Rider for RazorForge development with minimal effort.

## Quick Start (Automatic Setup)

### Option 1: Just Open the Project

```bash
# Rider will automatically detect the .csproj and configure everything
rider64.exe .
```

That's it! Rider will:

- ✅ Auto-detect the .NET solution
- ✅ Load all run configurations
- ✅ Configure C# inspections
- ✅ Set up file templates
- ✅ Enable CMake support for native code

### Option 2: Run Setup Script

**Windows:**

```bash
setup-rider.bat
```

This will verify prerequisites and open Rider with everything configured.

---

## Prerequisites

### Required

- **JetBrains Rider** - [Download](https://www.jetbrains.com/rider/download/)
- **.NET 9.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)

### Optional (for native development)

- **Visual Studio Build Tools** or **MinGW** (Windows)
- **GCC** or **Clang** (Linux/macOS)
- **CMake** - [Download](https://cmake.org/download/)

---

## Pre-Configured Run Configurations

All run configurations are already set up in `.idea/.idea.RazorForge/.idea/runConfigurations/`:

### Available Configurations

Access via: `Alt+Shift+F10` (Run) or `Alt+Shift+F9` (Debug)

1. **Build RazorForge**
    - Builds the entire compiler project
    - Keyboard: `Ctrl+Shift+F9`

2. **Compile: Current File**
    - Compiles the currently open `.rf` file
    - Uses `$FilePath$` macro
    - Perfect for quick testing

3. **Compile: Intrinsics Demo**
    - Compiles `examples/intrinsics_demo.rf`
    - Demonstrates compiler intrinsics

4. **Compile: Primitive Types Demo**
    - Compiles `examples/primitive_types_demo.rf`
    - Shows s64, u64, f64 operations

5. **Compile: Console Demo**
    - Compiles `examples/console_demo.rf`
    - Interactive console I/O examples

6. **Language Server**
    - Starts the RazorForge LSP server
    - For language server development

7. **Run: All Tests**
    - Runs all xUnit tests
    - Keyboard: `Ctrl+U, L` (Run All)

---

## Pre-Configured File Templates

Create new RazorForge files using templates:

### How to Use Templates

1. Right-click in Project Explorer
2. **New** → **File from Template**
3. Select a RazorForge template

### Available Templates

| Template                   | Description                         |
|----------------------------|-------------------------------------|
| **RazorForge File**        | Basic .rf file with main() routine  |
| **RazorForge Record Type** | Record type with example methods    |
| **RazorForge Entity Type** | Entity type with construction logic |

---

## Common Workflows

### Building the Project

**Method 1: Menu**

- `Build` → `Build Solution` (`Ctrl+Shift+F9`)

**Method 2: Run Configuration**

- Select "Build RazorForge" → Press `Shift+F10`

**Method 3: Terminal**

- `Alt+F12` → `dotnet build`

### Running Examples

**Quick Way:**

1. Select run configuration dropdown (top-right)
2. Choose "Compile: Console Demo"
3. Press `Shift+F10` (Run) or `Shift+F9` (Debug)

**Terminal Way:**

```bash
dotnet run -- compile examples/console_demo.rf
```

### Compiling Current File

1. Open any `.rf` file
2. Select "Compile: Current File" from dropdown
3. Press `Shift+F10`

### Running Tests

**Method 1: Test Explorer**

- `View` → `Tool Windows` → `Unit Tests` (`Ctrl+Alt+U`)
- Click "Run All Tests"

**Method 2: Run Configuration**

- Select "Run: All Tests" → `Shift+F10`

**Method 3: In-Editor**

- Click green arrow next to test method
- Or: `Alt+Enter` → "Run Test"

### Debugging

**Debug Compiler:**

1. Set breakpoints in C# code
2. Select any "Compile: ..." configuration
3. Press `Shift+F9` (Debug)
4. Debugger will hit breakpoints during compilation

**Debug Tests:**

1. Set breakpoints in test code
2. Right-click test → "Debug Test"
3. Or: `Ctrl+U, D` (Debug All)

---

## Keyboard Shortcuts

| Shortcut        | Action                       |
|-----------------|------------------------------|
| `Ctrl+Shift+F9` | Build Solution               |
| `Shift+F10`     | Run Selected Configuration   |
| `Shift+F9`      | Debug Selected Configuration |
| `Alt+Shift+F10` | Show Run Configurations      |
| `Alt+Shift+F9`  | Show Debug Configurations    |
| `Ctrl+U, L`     | Run All Tests                |
| `Ctrl+U, D`     | Debug All Tests              |
| `Alt+F12`       | Open Terminal                |
| `Ctrl+E, C`     | Commit Changes (Git)         |
| `Ctrl+T`        | Go to Type                   |
| `Ctrl+Shift+T`  | Go to File                   |
| `Alt+Enter`     | Show Context Actions         |
| `Ctrl+Space`    | IntelliSense/Code Completion |

---

## Code Inspections

Pre-configured inspection profile: **RazorForge**

Location: `.idea/.idea.RazorForge/.idea/inspectionProfiles/RazorForge.xml`

### Enabled Inspections

- ✅ C# warnings and errors
- ✅ Code style violations
- ✅ Null reference analysis
- ✅ Best practices

### Disabled Inspections

- ❌ Unused member warnings (reduces noise in compiler development)
- ❌ "Can be private" hints
- ❌ Unused parameter warnings
- ❌ Never instantiated class warnings

### Changing Inspection Severity

1. `File` → `Settings` (`Ctrl+Alt+S`)
2. `Editor` → `Inspection Settings`
3. Select "RazorForge" profile
4. Modify as needed

---

## Linting RazorForge Files

Since RazorForge is a custom language, you have several options for linting:

### Option 1: Built-in Language Server (Recommended)

The RazorForge compiler includes a Language Server Protocol (LSP) implementation:

**Setup:**

1. Run the "Language Server" configuration
2. Configure your editor to use it

**Features:**

- ✅ Syntax error detection
- ✅ Semantic analysis
- ✅ Type checking
- ✅ Real-time diagnostics

**Start Language Server:**

```bash
dotnet run -- lsp
```

### Option 2: Custom Rider Plugin

You can create a Rider plugin for `.rf` files:

**Steps:**

1. Use IntelliJ Platform SDK
2. Define language structure (`RazorForge.bnf`)
3. Implement lexer/parser
4. Add inspections

**Resources:**

- [IntelliJ Platform SDK Docs](https://plugins.jetbrains.com/docs/intellij/)
- [Custom Language Support Tutorial](https://plugins.jetbrains.com/docs/intellij/custom-language-support.html)

### Option 3: External Linter Integration

Create a standalone linter and integrate it:

**Create Linter:**

```bash
dotnet run -- lint examples/console_demo.rf
```

**Integrate with Rider:**

1. `File` → `Settings` → `Tools` → `External Tools`
2. Add new tool:
    - Program: `dotnet`
    - Arguments: `run -- lint $FilePath$`
    - Working directory: `$ProjectFileDir$`

3. Assign keyboard shortcut
4. Or add to "Compile: Current File" as pre-run step

### Option 4: File Watchers

Use Rider's File Watchers for automatic linting:

**Setup:**

1. `File` → `Settings` → `Tools` → `File Watchers`
2. Click `+` → `Custom`
3. Configure:
    - **Name:** RazorForge Linter
    - **File type:** `*.rf`
    - **Scope:** Project Files
    - **Program:** `dotnet`
    - **Arguments:** `run -- lint $FilePath$`
    - **Output paths:** (your lint output format)
    - **Working directory:** `$ProjectFileDir$`

### Option 5: Add Linting to Compiler

Enhance the compiler with a dedicated lint command:

**Add to src/:**

```csharp
public class Linter
{
    public static void LintFile(string filePath)
    {
        // Tokenize
        var tokens = Tokenizer.Tokenize(File.ReadAllText(filePath));

        // Parse
        var ast = Parser.Parse(tokens);

        // Run semantic analysis
        var analyzer = new SemanticAnalyzer();
        var diagnostics = analyzer.Analyze(ast);

        // Report issues
        foreach (var diag in diagnostics)
        {
            Console.WriteLine($"{diag.Severity}: {diag.Message} at {diag.Location}");
        }
    }
}
```

**Use it:**

```bash
dotnet run -- lint myfile.rf
```

---

## Project Structure

```
RazorForge/
├── .idea/                                    # Rider configuration
│   ├── .idea.RazorForge/.idea/
│   │   ├── runConfigurations/               # Run configurations
│   │   │   ├── Build_RazorForge.xml
│   │   │   ├── Compile__Current_File.xml
│   │   │   ├── Compile__Intrinsics_Demo.xml
│   │   │   ├── Compile__Primitive_Types_Demo.xml
│   │   │   ├── Compile__Console_Demo.xml
│   │   │   ├── Language_Server.xml
│   │   │   └── Run__All_Tests.xml
│   │   └── inspectionProfiles/
│   │       └── RazorForge.xml               # Inspection settings
│   └── fileTemplates/                        # File templates
│       ├── RazorForge File.rf
│       ├── RazorForge Record Type.rf
│       └── RazorForge Entity Type.rf
├── src/                                      # C# compiler source
├── stdlib/                                   # Standard library
├── native/                                   # C runtime
├── examples/                                 # Example programs
└── tests/                                    # Tests
```

---

## Code Style Settings

Rider automatically uses `.editorconfig` if present. Create one:

**`.editorconfig`:**

```ini
root = true

[*]
charset = utf-8
insert_final_newline = true
trim_trailing_whitespace = true

[*.cs]
indent_style = space
indent_size = 4

[*.rf]
indent_style = space
indent_size = 4

[*.{json,yml,yaml}]
indent_style = space
indent_size = 2
```

---

## Git Integration

Rider has excellent Git integration:

### Common Git Operations

| Action       | Shortcut                  |
|--------------|---------------------------|
| Commit       | `Ctrl+K`                  |
| Push         | `Ctrl+Shift+K`            |
| Pull         | `Ctrl+T`                  |
| Show History | `Alt+9`                   |
| Show Changes | `Alt+9` → "Local Changes" |

### Viewing Changes

- `Alt+9` → "Git" tool window
- Or: `Ctrl+Alt+Z` → "Local Changes"

### Committing

1. `Ctrl+K` (Commit)
2. Select files to commit
3. Write commit message
4. Click "Commit" or "Commit and Push"

---

## Native Development (C Runtime)

Rider supports CMake projects:

### Opening CMakeLists.txt

1. Navigate to `native/CMakeLists.txt`
2. Right-click → "Load CMake Project"
3. Rider will configure CMake toolchain

### Building Native Libraries

**Method 1: Terminal**

```bash
cd native
./build.bat       # Windows
bash build.sh     # Linux/macOS
```

**Method 2: CMake Tool Window**

1. `View` → `Tool Windows` → `CMake`
2. Select configuration
3. Click "Build"

---

## Performance Tips

### Speed Up Rider

1. **Exclude Build Directories:**
    - Right-click `bin/` → "Mark Directory as" → "Excluded"
    - Right-click `obj/` → "Mark Directory as" → "Excluded"
    - Right-click `native/build/` → "Mark Directory as" → "Excluded"

2. **Increase Memory:**
    - `Help` → `Change Memory Settings`
    - Increase to 4096 MB (or higher)
    - Restart Rider

3. **Disable Unused Plugins:**
    - `File` → `Settings` → `Plugins`
    - Disable plugins you don't use

4. **Power Save Mode (when needed):**
    - `File` → `Power Save Mode`
    - Disables inspections temporarily

---

## Troubleshooting

### Build Fails

**Issue:** "dotnet command not found"
**Solution:**

- Install .NET 9.0 SDK
- Restart Rider
- Or: `File` → `Invalidate Caches` → "Invalidate and Restart"

### Run Configurations Not Showing

**Issue:** Run configurations don't appear
**Solution:**

- `File` → `Invalidate Caches` → "Invalidate and Restart"
- Or: Delete `.idea/` folder and reopen project

### IntelliSense Slow

**Issue:** Code completion is slow
**Solution:**

- `File` → `Invalidate Caches` → "Invalidate and Restart"
- Exclude `bin/`, `obj/`, `build/` directories
- Increase Rider memory allocation

### Tests Not Discovered

**Issue:** Unit tests not showing in Test Explorer
**Solution:**

- `Tools` → `Unit Tests` → "Discover Tests"
- Or: Right-click project → "Run Unit Tests"
- Or: Build solution first (`Ctrl+Shift+F9`)

### Native Build Fails

**Issue:** CMake or native build fails
**Solution:**

```bash
cd native
rm -rf build
mkdir build
cd build
cmake ..
cmake --build .
```

---

## Additional Features

### Code Formatting

- **Format File:** `Ctrl+Alt+Enter`
- **Format Selection:** `Ctrl+Alt+Enter` (with selection)
- **Organize Imports:** `Ctrl+Alt+O`

### Navigation

- **Go to Definition:** `F12`
- **Find Usages:** `Shift+F12`
- **Go to Type:** `Ctrl+T`
- **Go to File:** `Ctrl+Shift+T`
- **Go to Symbol:** `Ctrl+Shift+Alt+N`

### Refactoring

- **Rename:** `Ctrl+R, R` or `F2`
- **Extract Method:** `Ctrl+R, M`
- **Inline:** `Ctrl+R, I`
- **Change Signature:** `Ctrl+F6`

### Debugging

- **Toggle Breakpoint:** `F9`
- **Step Over:** `F10`
- **Step Into:** `F11`
- **Step Out:** `Shift+F11`
- **Resume:** `F5`
- **Evaluate Expression:** `Alt+F8`

---

## Next Steps

1. ✅ **Open project in Rider** (it auto-configures)
2. ✅ **Build:** `Ctrl+Shift+F9`
3. ✅ **Run an example:** Select "Compile: Console Demo" → `Shift+F10`
4. ✅ **Set up linting:** Choose an option from "Linting RazorForge Files"
5. ✅ **Start coding:** Create new `.rf` files using templates

---

## Additional Resources

- **RazorForge Documentation:** `docs/` directory
- **Intrinsics API:** `docs/INTRINSICS_API.md`
- **Memory Model:** `wiki/RazorForge-Memory-Model.md`
- **Rider Docs:** [jetbrains.com/rider/documentation](https://www.jetbrains.com/rider/documentation/)

---

**Happy Coding with RazorForge in Rider! 🚀**
