# VS Code Extension Setup Guide

## Quick Start Testing

### 1. Install Dependencies

```bash
cd vscode-extension
npm install
```

### 2. Compile TypeScript

```bash
npm run compile
```

### 3. Test in VS Code

**Option A: Development Mode**

1. Open the `vscode-extension` folder in VS Code
2. Press `F5` to launch Extension Development Host
3. In the new window, open the `examples` folder
4. Open any `.rf` file to see syntax highlighting

**Option B: Install as VSIX**

```bash
npm install -g vsce
vsce package
code --install-extension razorforge-language-support-0.1.0.vsix
```

### 4. Configure RazorForge Path

In VS Code settings, add:

```json
{
  "razorforge.languageServer.path": "L:/programming/RiderProjects/RazorForge/bin/Debug/net9.0/RazorForge.exe",
  "razorforge.trace.server": "verbose"
}
```

### 5. Test LSP Integration

1. Open `examples/hello_world.rf`
2. Check Output panel for "RazorForge Language Server"
3. Look for LSP communication in "RazorForge LSP Trace"

## Expected Results

### ✅ Syntax Highlighting

- Keywords (`routine`, `let`, `if`) should be colored
- Types (`u32`, `f64`, `DynamicSlice`) should be highlighted
- Comments should appear in comment color
- Strings should be syntax highlighted

### ✅ File Icons

- `.rf` files should show suflae+sword emoji icon
- Different colors for light/dark themes

### ✅ LSP Features (if server works)

- Real-time error checking
- Autocomplete suggestions
- Hover information

## Troubleshooting

### No Syntax Highlighting

- Check file has `.rf` extension
- Reload VS Code window
- Verify extension is activated in Extensions panel

### LSP Server Not Starting

- Check RazorForge.exe path in settings
- Verify executable exists and runs with `--lsp` flag
- Check Output panel for error messages

### Extension Not Loading

- Check console in VS Code Developer Tools (Help > Toggle Developer Tools)
- Verify TypeScript compiled without errors
- Try restarting VS Code

## Testing Commands

Test these commands in Command Palette (`Ctrl+Shift+P`):

- `RazorForge: New File`
- `RazorForge: Restart Language Server`
- `RazorForge: Show Server Status`
