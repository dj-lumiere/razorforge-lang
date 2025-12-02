# RazorForge Type Colors

## Color Scheme for Type Declarations

### Type Keywords and Names

| Type Kind | Color   | Hex Code | TextMate Scope                       |
|-----------|---------|----------|--------------------------------------|
| record    | Orange  | #FFA500  | entity.name.type.record.razorforge   |
| entity    | Green   | #4CAF50  | entity.name.type.entity.razorforge   |
| resident  | Yellow  | #FDD835  | entity.name.type.resident.razorforge |
| variant   | Lt Blue | #81D4FA  | entity.name.type.variant.razorforge  |
| choice    | Cyan    | #00BCD4  | entity.name.type.choice.razorforge   |

## Rider Configuration

### Method 1: TextMate Scopes (Recommended)

1. Open Settings: `File` → `Settings` (Ctrl+Alt+S)
2. Navigate: `Editor` → `Color Scheme` → `TextMate`
3. Add the following scope mappings:

```
entity.name.type.record.razorforge    → #FFA500 (Orange)
entity.name.type.entity.razorforge    → #4CAF50 (Green)
entity.name.type.resident.razorforge  → #FDD835 (Yellow)
entity.name.type.variant.razorforge   → #81D4FA (Light Blue)
entity.name.type.choice.razorforge    → #00BCD4 (Cyan)
```

### Method 2: Custom Language Colors (Alternative)

If TextMate scopes don't work:

1. Settings → `Editor` → `Color Scheme` → `Language Defaults`
2. Look for "Classes" or "Types"
3. You may need to create a plugin for full custom coloring

## VS Code Configuration

For VS Code, add to your `settings.json`:

```json
{
  "editor.tokenColorCustomizations": {
    "textMateRules": [
      {
        "scope": "entity.name.type.record.razorforge",
        "settings": {
          "foreground": "#FFA500"
        }
      },
      {
        "scope": "entity.name.type.entity.razorforge",
        "settings": {
          "foreground": "#4CAF50"
        }
      },
      {
        "scope": "entity.name.type.resident.razorforge",
        "settings": {
          "foreground": "#FDD835"
        }
      },
      {
        "scope": "entity.name.type.variant.razorforge",
        "settings": {
          "foreground": "#81D4FA"
        }
      },
      {
        "scope": "entity.name.type.choice.razorforge",
        "settings": {
          "foreground": "#00BCD4"
        }
      }
    ]
  }
}
```

## Example

```razorforge
record Point { x: f64, y: f64 }              # Point in orange
entity List<T> { data: DynamicSlice }        # List in green
resident FixedList<T, N> { ... }             # FixedList in yellow
variant Result<T, E> { Ok(T), Err(E) }       # Result in light blue
choice State { Loading, Ready, Error }       # State in cyan
```
