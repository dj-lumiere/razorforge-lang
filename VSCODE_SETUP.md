# VSCode One-Click Setup Guide for RazorForge

This guide will help you set up Visual Studio Code for RazorForge development with minimal effort.

## Quick Start (One-Click Setup)

1. **Open in VSCode:**
   ```bash
   code .
   ```

2. **Install Recommended Extensions:**
    - VSCode will automatically prompt you to install recommended extensions
    - Click "Install All" when prompted
    - **OR** manually: `Ctrl+Shift+P` → "Extensions: Show Recommended Extensions" → Install all

3. **Build the Project:**
    - Press `Ctrl+Shift+B` to build (uses default build task)
    - **OR** `Ctrl+Shift+P` → "Tasks: Run Build Task"

4. **Start Developing!**
    - All tasks and debug configurations are pre-configured
    - See sections below for available commands

---

## Prerequisites

### Required

- **.NET 9.0 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/9.0)
- **Visual Studio Code** - [Download](https://code.visualstudio.com/)
- **C# Dev Kit Extension** (will be prompted to install)

### For Native Development

- **Windows:** Visual Studio Build Tools or MinGW
- **Linux/macOS:** GCC or Clang
- **CMake** - [Download](https://cmake.org/download/)

---

## Available Tasks

Access tasks via: `Ctrl+Shift+P` → "Tasks: Run Task" or `Ctrl+Shift+B` for build

### Build Tasks

| Task                        | Description                             | Shortcut       |
|-----------------------------|-----------------------------------------|----------------|
| **build**                   | Build the RazorForge compiler (default) | `Ctrl+Shift+B` |
| **rebuild**                 | Clean and rebuild from scratch          | -              |
| **clean**                   | Remove build artifacts                  | -              |
| **build: native libraries** | Build C runtime libraries               | -              |

### Run Tasks

| Task                     | Description                          |
|--------------------------|--------------------------------------|
| **run**                  | Run the RazorForge compiler          |
| **run: compile file**    | Compile the currently open .rf file  |
| **run: language server** | Start the RazorForge Language Server |

### Test Tasks

| Task                    | Description                  |
|-------------------------|------------------------------|
| **test**                | Run all tests                |
| **test: with coverage** | Run tests with code coverage |

### Example Compilation Tasks

| Task                              | Description                              |
|-----------------------------------|------------------------------------------|
| **compile: intrinsics demo**      | Compile examples/intrinsics_demo.rf      |
| **compile: primitive types demo** | Compile examples/primitive_types_demo.rf |
| **compile: console demo**         | Compile examples/console_demo.rf         |

### Watch Tasks

| Task      | Description                                      |
|-----------|--------------------------------------------------|
| **watch** | Watch for file changes and rebuild automatically |

---

## Debug Configurations

Access via: `F5` or Debug panel (`Ctrl+Shift+D`)

### Available Configurations

1. **Debug RazorForge Compiler**
    - Debug the main compiler executable
    - No arguments

2. **Debug: Compile Current File**
    - Debug compilation of the currently open .rf file
    - Automatically passes `${file}` as argument

3. **Debug: Compile Intrinsics Demo**
    - Debug compilation of intrinsics_demo.rf example
    - Breakpoints in compiler code will be hit

4. **Debug: Compile Primitive Types Demo**
    - Debug compilation of primitive_types_demo.rf example

5. **Debug: Compile Console Demo**
    - Debug compilation of console_demo.rf example

6. **Debug: Language Server**
    - Debug the RazorForge Language Server Protocol implementation
    - Background process

7. **Debug: All Tests**
    - Debug all unit tests
    - Breakpoints in test code will be hit

8. **Attach to Process**
    - Attach debugger to a running RazorForge process

---

## Common Workflows

### Compiling a RazorForge File

**Method 1: Task**

1. Open a `.rf` file
2. `Ctrl+Shift+P` → "Tasks: Run Task" → "run: compile file"

**Method 2: Debug**

1. Open a `.rf` file
2. `F5` → Select "Debug: Compile Current File"

### Running Examples

**Intrinsics Demo:**

```bash
# From terminal (Ctrl+`)
dotnet run -- compile examples/intrinsics_demo.rf
```

**Primitive Types Demo:**

```bash
dotnet run -- compile examples/primitive_types_demo.rf
```

**Console Demo:**

```bash
dotnet run -- compile examples/console_demo.rf
```

### Building Native Libraries

**Method 1: Task**

- `Ctrl+Shift+P` → "Tasks: Run Task" → "build: native libraries"

**Method 2: Terminal**

```bash
cd native
./build.bat       # Windows
bash build.sh     # Linux/macOS
```

### Running Tests

**Method 1: Task**

- `Ctrl+Shift+P` → "Tasks: Run Task" → "test"

**Method 2: Terminal**

```bash
dotnet test
```

**Method 3: Test Explorer**

- Open Test Explorer panel
- Click "Run All Tests" button

---

## Keyboard Shortcuts

| Shortcut       | Action                |
|----------------|-----------------------|
| `Ctrl+Shift+B` | Build (default task)  |
| `F5`           | Start Debugging       |
| `Ctrl+F5`      | Run Without Debugging |
| `Ctrl+Shift+P` | Command Palette       |
| `Ctrl+``       | Toggle Terminal       |
| `Ctrl+Shift+D` | Open Debug Panel      |
| `Ctrl+Shift+E` | Open Explorer         |
| `Ctrl+Shift+F` | Search in Files       |
| `Ctrl+Shift+G` | Open Source Control   |
| `Ctrl+Shift+X` | Open Extensions       |

---

## Recommended Extensions

The following extensions will be automatically recommended:

### C# Development

- **C#** (`ms-dotnettools.csharp`) - C# language support
- **C# Dev Kit** (`ms-dotnettools.csdevkit`) - Enhanced C# development

### C/C++ Development

- **C/C++** (`ms-vscode.cpptools`) - C/C++ IntelliSense
- **CMake Tools** (`ms-vscode.cmake-tools`) - CMake integration
- **clangd** (`llvm-vs-code-extensions.vscode-clangd`) - Clang language server

### Git Integration

- **GitLens** (`eamodio.gitlens`) - Enhanced Git capabilities

### Documentation

- **Markdown All in One** (`yzhang.markdown-all-in-one`) - Markdown support
- **Markdown Lint** (`davidanson.vscode-markdownlint`) - Markdown linting

### Code Quality

- **EditorConfig** (`editorconfig.editorconfig`) - EditorConfig support

### Utilities

- **Todo Tree** (`gruntfuggly.todo-tree`) - TODO/FIXME highlighting
- **Better Comments** (`aaron-bond.better-comments`) - Comment highlighting
- **TODO Highlight** (`wayou.vscode-todo-highlight`) - Highlight TODOs

---

## File Associations

The workspace is pre-configured with the following file associations:

| Extension          | Language   |
|--------------------|------------|
| `.rf`              | RazorForge |
| `.sf`              | Suflae     |
| `.tmLanguage.json` | JSON       |

---

## Settings Highlights

### Editor

- **Format on Save:** Enabled
- **Format on Paste:** Enabled
- **Tab Size:** 4 spaces
- **Detect Indentation:** Disabled (consistent formatting)

### C# Specific

- **Default Formatter:** C# extension
- **Tab Size:** 4 spaces
- **Ruler:** 120 characters
- **Organize Imports on Format:** Enabled

### C/C++ Specific

- **Default Formatter:** C/C++ extension
- **Tab Size:** 4 spaces
- **Include Paths:** Automatically configured for native/

### Git

- **Auto Fetch:** Enabled
- **Smart Commit:** Enabled

---

## Troubleshooting

### Build Fails

**Issue:** Build task fails with "dotnet not found"
**Solution:** Install .NET 9.0 SDK and restart VSCode

**Issue:** Native build fails
**Solution:**

```bash
cd native
./build.bat  # Windows
bash build.sh  # Linux/macOS
```

### Extensions Not Loading

**Issue:** Recommended extensions not shown
**Solution:**

1. `Ctrl+Shift+P` → "Extensions: Show Recommended Extensions"
2. Install all manually

### IntelliSense Not Working

**Issue:** C# IntelliSense not working
**Solution:**

1. `Ctrl+Shift+P` → "OmniSharp: Restart OmniSharp"
2. Wait for OmniSharp to load (check status bar)

**Issue:** C/C++ IntelliSense not working
**Solution:**

1. `Ctrl+Shift+P` → "C/C++: Reload IntelliSense"
2. Ensure CMake configuration is complete

### Debug Not Starting

**Issue:** Debugger fails to start
**Solution:**

1. Ensure project is built: `Ctrl+Shift+B`
2. Check that `bin/Debug/net9.0/RazorForge.dll` exists
3. If not, run: `dotnet build`

---

## Project Structure

```
RazorForge/
├── .vscode/                    # VSCode configuration
│   ├── tasks.json             # Build/run/test tasks
│   ├── launch.json            # Debug configurations
│   ├── settings.json          # Workspace settings
│   └── extensions.json        # Recommended extensions
├── src/                       # C# compiler source
│   ├── Lexer/                # Tokenization
│   ├── Parser/               # AST construction
│   ├── Analysis/             # Semantic analysis
│   ├── CodeGen/              # Code generation
│   └── LanguageServer/       # LSP implementation
├── stdlib/                    # Standard library (.rf files)
│   ├── Console.rf            # Console I/O
│   ├── NativeDataTypes/      # Primitive types (s64, u64, f64)
│   ├── Collections/          # Data structures
│   └── memory/               # Memory management
├── native/                    # C runtime
│   └── runtime/
│       ├── runtime.c         # Core runtime functions
│       └── memory.c          # Memory operations
├── examples/                  # Example programs
│   ├── intrinsics_demo.rf
│   ├── primitive_types_demo.rf
│   └── console_demo.rf
├── tests/                     # Test files
└── docs/                      # Documentation
```

---

## Next Steps

1. **Build the project:** `Ctrl+Shift+B`
2. **Run an example:** `Ctrl+Shift+P` → "Tasks: Run Task" → "compile: console demo"
3. **Start coding:** Create a new `.rf` file and compile it
4. **Explore examples:** Open `examples/` directory
5. **Read documentation:** Check `docs/` directory

---

## Additional Resources

- **RazorForge Documentation:** See `docs/` directory
- **Intrinsics API:** `docs/INTRINSICS_API.md`
- **Memory Model:** `wiki/RazorForge-Memory-Model.md`
- **Issue Tracker:** GitHub Issues (if applicable)

---

## Support

If you encounter any issues:

1. Check the **Troubleshooting** section above
2. Verify all prerequisites are installed
3. Check VSCode Output panel (`Ctrl+Shift+U`) for errors
4. Restart VSCode
5. Rebuild from scratch: `dotnet clean && dotnet build`

---

**Happy Coding with RazorForge! 🔥**
