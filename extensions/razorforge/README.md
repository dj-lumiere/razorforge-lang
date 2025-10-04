# RazorForge Language Support for VS Code

This extension provides comprehensive language support for the RazorForge programming language, including syntax
highlighting, error checking, autocomplete, and other IDE features through Language Server Protocol (LSP) integration.

## Features

- **Syntax Highlighting**: Rich syntax highlighting for RazorForge (.rf) files
- **Error Checking**: Real-time syntax and semantic error detection
- **Autocomplete**: Intelligent code completion for keywords, functions, and symbols
- **Hover Information**: Type information and documentation on hover
- **Language Server**: Full LSP integration with the RazorForge compiler

## Requirements

- **RazorForge Compiler**: The RazorForge language server must be available in your PATH or configured manually
- **VS Code**: Version 1.74.0 or higher

## Installation

1. Install the extension from the VS Code marketplace
2. Ensure RazorForge compiler is installed and accessible
3. Open any `.rf` file to activate the extension

## Configuration

Configure the extension through VS Code settings:

```json
{
  "razorforge.languageServer.path": "path/to/RazorForge.exe",
  "razorforge.languageServer.args": ["--lsp"],
  "razorforge.trace.server": "verbose"
}
```

### Settings

- `razorforge.languageServer.path`: Path to the RazorForge Language Server executable
- `razorforge.languageServer.args`: Arguments to pass to the language server
- `razorforge.trace.server`: LSP communication tracing level (off/messages/verbose)

## Commands

- `RazorForge: New File` - Create a new RazorForge file with template
- `RazorForge: Restart Language Server` - Restart the LSP server connection
- `RazorForge: Show Server Status` - Display current server status

## Language Features

### Syntax Highlighting

The extension provides syntax highlighting for:

- Keywords: `recipe`, `class`, `struct`, `let`, `var`, `if`, `while`, etc.
- Types: `s32`, `u64`, `f32`, `bool`, `string`, `HeapSlice`, etc.
- Comments: Line (`//`) and block (`/* */`) comments
- Strings and literals
- Operators and punctuation

### RazorForge Keywords

**Control Flow**: `if`, `else`, `when`, `while`, `for`, `in`, `to`, `by`, `match`, `case`, `default`, `break`,
`continue`, `return`

**Declarations**: `recipe`, `class`, `struct`, `enum`, `chimera`, `variant`, `mutant`, `option`, `feature`

**Storage**: `let`, `var`, `const`, `mut`, `auto`

**Memory Types**: `HeapSlice`, `StackSlice`

**Danger Zone**: `danger`, `external`, `write_as!`, `read_as!`, `addr_of!`, `invalidate!`

## Example RazorForge Code

```razorforge
// Example RazorForge program
recipe fibonacci(n: u32) -> u32 {
    if n <= 1 {
        return n;
    }
    return fibonacci(n - 1) + fibonacci(n - 2);
}

recipe main() {
    let result = fibonacci(10);
    // Result: 55
}
```

## Troubleshooting

### Language Server Not Starting

1. Check that RazorForge compiler is installed and in PATH
2. Verify the `razorforge.languageServer.path` setting
3. Check the Output panel for error messages
4. Try restarting the language server with the command palette

### No Syntax Highlighting

1. Ensure file has `.rf` extension
2. Try reloading VS Code window
3. Check that the extension is activated

### LSP Features Not Working

1. Verify language server is running (check status command)
2. Enable LSP tracing: set `razorforge.trace.server` to `verbose`
3. Check the RazorForge LSP Trace output channel

## Development

To develop this extension:

1. Clone the repository
2. Run `npm install` to install dependencies
3. Open in VS Code and press F5 to launch extension host
4. Open a `.rf` file to test the extension

## Contributing

Contributions are welcome! Please see the main RazorForge repository for contribution guidelines.

## License

This extension is part of the RazorForge project. See the main repository for license information.
