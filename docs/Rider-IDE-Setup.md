# RazorForge IDE Setup for JetBrains Rider

This guide helps you configure Rider for optimal RazorForge development with syntax highlighting, live templates, and
proper file associations.

## Quick Setup

### 1. File Type Association

**Option A: Automatic (Recommended)**

1. Open any `.rf` file in Rider
2. When prompted, select "Associate with C#" for syntax highlighting
3. Check "Remember for this session"

**Option B: Manual Configuration**

1. Go to `File → Settings → Editor → File Types`
2. Select "C#" from the list
3. Add pattern: `*.rf`
4. Add pattern: `*.razorforge`

### 2. Color Scheme Import

1. Download `docs/razorforgesuflae.icls`
2. Go to `File → Manage IDE Settings → Import Settings`
3. Select the `.icls` file
4. Restart Rider
5. Go to `File → Settings → Editor → Color Scheme`
6. Select "RazorForge & Suflae" scheme

### 3. Live Templates

1. Go to `File → Settings → Editor → Live Templates`
2. Click "+" → "Import Live Templates"
3. Select `docs/rider-live-templates.xml`
4. Enable the "RazorForge" template group

## File Association Details

### Supported Extensions

- `.rf` - RazorForge source files
- `.razorforge` - Alternative RazorForge files

### Syntax Highlighting

RazorForge files are associated with C# syntax highlighting, which provides:

- **Keywords**: `recipe`, `record`, `let`, `var`, `danger`, etc.
- **Memory Operations**: `hijack!`, `share!`, `steal!`, etc. (highlighted in red)
- **Slice Operations**: `size()`, `address()`, `unsafe_ptr!()` (highlighted in cyan)
- **Danger Zone**: `read_as!`, `write_as!`, `volatile_read!` (highlighted with red background)
- **Types**: `HeapSlice`, `StackSlice`, `s32`, `sysuint`, etc.
- **Comments**: `#` single line, `###` documentation blocks

## Live Templates Reference

Type these abbreviations and press Tab to expand:

### Memory Management

- `heap` → `var buffer = HeapSlice(64)`
- `stack` → `var buffer = StackSlice(64)`
- `slicewrite` → `buffer.write<s32>!(0, 42)`
- `sliceread` → `let value = buffer.read<s32>!(0)`

### Memory Operations

- `hijack` → `let hijacked = object.hijack!()`
- `share` → `let shared = object.share!()`

### Functions

- `recipe` → Complete recipe template with documentation
- `forloop` → `for i in 0 to 10 { ... }`

### Danger Zone

- `danger` → `danger! { ... }`
- `readas` → `let value = read_as<s32>!(address)`
- `writeas` → `write_as<s32>!(address, 42)`

### Imports

- `importmem` → Import HeapSlice and StackSlice
- `importconsole` → Import write_line

## Color Scheme Details

The RazorForge color scheme provides semantic highlighting:

### RazorForge Colors

- **Memory Operations** (`hijack!`, `share!`): Red (`#FF6B6B`)
- **Slice Operations** (`size()`, `address()`): Cyan (`#8BE9FD`)
- **Danger Operations** (`read_as!`, `write_as!`): Red with dark background
- **danger! Blocks**: Red text with dark red background and underline
- **Memory Types** (`HeapSlice`, `StackSlice`): Green (`#50FA7B`)
- **External Declarations**: Purple (`#BD93F9`)

### Suflae Colors (Future)

- **Keywords**: Pink (`#FF79C6`)
- **LINQ Operations**: Green (`#50FA7B`)

## Advanced Configuration

### Custom File Icon

1. Install "File Icon Provider" plugin
2. Add custom icon mapping for `.rf` files
3. Use a forge/hammer icon to distinguish RazorForge files

### Code Formatting

1. Go to `File → Settings → Editor → Code Style → C#`
2. Adjust indentation to match RazorForge conventions:
    - Use 4 spaces for indentation
    - Continuation indent: 4 spaces
    - Keep blank lines: 2 maximum

### Inspections

Disable C#-specific inspections that don't apply to RazorForge:

1. Go to `File → Settings → Editor → Inspections → C#`
2. Disable:
    - "Redundant using directive"
    - "Possible null reference"
    - C# syntax errors (since RazorForge has different syntax)

## File Templates

Create new RazorForge files with proper templates:

### Recipe File Template

```razorforge
# RazorForge Recipe Template

import system/console/write_line

###
Function description
@param param - parameter description
@return return description
###
recipe function_name(parameters) return_type {
    # Implementation
}
```

### Record File Template

```razorforge
# RazorForge Record Template

###
Record description
###
record StructName {
    private field1: type
    private field2: type

    ###
    Constructor
    @param param - parameter description
    ###
    public recipe __create__(param: type) -> StructName {
        me.field1 = param
        return me
    }
}
```

## Troubleshooting

### Syntax Highlighting Not Working

1. Verify file extension is `.rf`
2. Check File Type association in Settings
3. Restart Rider if changes don't appear

### Live Templates Not Expanding

1. Ensure templates are enabled in Settings
2. Check context (should be "OTHER" for .rf files)
3. Try typing template name + Tab

### Color Scheme Not Applied

1. Verify scheme is imported correctly
2. Select "RazorForge & Suflae" in Color Scheme settings
3. Restart Rider to apply changes

## Integration with Build System

### MSBuild Integration

Add to your `.csproj` file:

```xml
<ItemGroup>
  <None Include="**/*.rf" />
  <None Include="**/*.razorforge" />
</ItemGroup>
```

### Custom Build Action

```xml
<ItemGroup>
  <RazorForgeSource Include="**/*.rf" />
</ItemGroup>

<Target Name="CompileRazorForge" BeforeTargets="Build">
  <Exec Command="razorforge-compiler %(RazorForgeSource.Identity)" />
</Target>
```

This setup provides a complete RazorForge development environment in Rider with proper syntax highlighting, intelligent
templates, and semantic colors for memory operations.
