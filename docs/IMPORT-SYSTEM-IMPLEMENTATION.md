# Import System Implementation Plan

## Overview

This document outlines the implementation plan for the RazorForge module/import system.

## Design Summary

1. **Search Order**: `builtin/` → project root → external packages
2. **Access Style**: Unqualified (use `List<s32>` directly)
3. **Collisions**: Compiler error (must use alias)
4. **Loading**: Eager (all transitive imports before semantic analysis)
5. **Transitive Visibility**: None (explicit imports required)
6. **Namespace**: Declaration takes priority over folder structure

---

## Implementation Phases

### Phase 1: Parser Updates

**Goal**: Parse `namespace` declarations

**Files to modify**:

- `src/Lexer/TokenType.cs` - Add `Namespace` token (if not present)
- `src/Lexer/RazorForgeTokenizer.cs` - Add keyword mapping
- `src/Parser/RazorForgeParser.cs` - Parse namespace declaration
- `src/AST/Declarations.cs` - Add `NamespaceDeclaration` node

**Implementation**:

```csharp
// AST/Declarations.cs
public record NamespaceDeclaration(
    string NamespacePath,  // e.g., "Collections" or "errors/common"
    SourceLocation Location
) : Declaration;
```

```csharp
// Parser - at start of file, before other declarations
private NamespaceDeclaration? ParseNamespaceDeclaration()
{
    if (!Match(TokenType.Namespace))
        return null;

    string path = ParseImportPath();  // Reuse import path parsing
    return new NamespaceDeclaration(path, location);
}
```

---

### Phase 2: Module Data Structures

**Goal**: Create data structures for module tracking

**New file**: `src/Analysis/ModuleInfo.cs`

```csharp
namespace RazorForge.Analysis;

/// <summary>
/// Represents a loaded module
/// </summary>
public class ModuleInfo
{
    /// <summary>
    /// The namespace path (from declaration or folder structure)
    /// e.g., "Collections" for List.rf
    /// </summary>
    public string Namespace { get; init; }

    /// <summary>
    /// The type name (filename without extension)
    /// e.g., "List" for List.rf
    /// </summary>
    public string TypeName { get; init; }

    /// <summary>
    /// Full import path: Namespace/TypeName
    /// e.g., "Collections/List"
    /// </summary>
    public string ImportPath => $"{Namespace}/{TypeName}";

    /// <summary>
    /// Absolute file path
    /// </summary>
    public string FilePath { get; init; }

    /// <summary>
    /// Parsed AST of the module
    /// </summary>
    public ProgramNode AST { get; init; }

    /// <summary>
    /// Public symbols exported by this module
    /// </summary>
    public List<Symbol> PublicSymbols { get; } = new();

    /// <summary>
    /// Imports declared by this module (for transitive loading)
    /// </summary>
    public List<string> Imports { get; } = new();
}
```

---

### Phase 3: ModuleResolver

**Goal**: Resolve import paths to file paths

**New file**: `src/Analysis/ModuleResolver.cs`

```csharp
namespace RazorForge.Analysis;

public class ModuleResolver
{
    private readonly List<string> _searchPaths;
    private readonly Dictionary<string, string> _namespaceRegistry;

    public ModuleResolver(string builtinPath, string projectRoot)
    {
        _searchPaths = new List<string>
        {
            builtinPath,      // e.g., "L:/project/builtin"
            projectRoot,      // e.g., "L:/project/src"
            // TODO: external packages
        };
        _namespaceRegistry = new Dictionary<string, string>();
    }

    /// <summary>
    /// Register a namespace mapping (when a file declares namespace)
    /// </summary>
    public void RegisterNamespace(string importPath, string filePath)
    {
        _namespaceRegistry[importPath] = filePath;
    }

    /// <summary>
    /// Resolve an import path to a file path
    /// </summary>
    /// <returns>Absolute file path or null if not found</returns>
    public string? Resolve(string importPath)
    {
        // 1. Check namespace registry first
        if (_namespaceRegistry.TryGetValue(importPath, out string? registered))
            return registered;

        // 2. Search each path
        foreach (string searchPath in _searchPaths)
        {
            string filePath = Path.Combine(searchPath, importPath.Replace('/', Path.DirectorySeparatorChar) + ".rf");
            if (File.Exists(filePath))
                return filePath;

            // Also try .sf for Suflae
            filePath = Path.Combine(searchPath, importPath.Replace('/', Path.DirectorySeparatorChar) + ".sf");
            if (File.Exists(filePath))
                return filePath;
        }

        return null;
    }
}
```

---

### Phase 4: ModuleCache

**Goal**: Cache loaded modules to avoid re-parsing

**New file**: `src/Analysis/ModuleCache.cs`

```csharp
namespace RazorForge.Analysis;

public class ModuleCache
{
    private readonly Dictionary<string, ModuleInfo> _cache = new();
    private readonly HashSet<string> _loading = new();  // For circular detection

    public bool IsLoaded(string importPath) => _cache.ContainsKey(importPath);

    public bool IsLoading(string importPath) => _loading.Contains(importPath);

    public ModuleInfo? Get(string importPath)
    {
        _cache.TryGetValue(importPath, out ModuleInfo? module);
        return module;
    }

    public void BeginLoading(string importPath)
    {
        _loading.Add(importPath);
    }

    public void FinishLoading(string importPath, ModuleInfo module)
    {
        _loading.Remove(importPath);
        _cache[importPath] = module;
    }

    public void AbortLoading(string importPath)
    {
        _loading.Remove(importPath);
    }
}
```

---

### Phase 5: ModuleLoader

**Goal**: Orchestrate module loading with transitive dependencies

**New file**: `src/Analysis/ModuleLoader.cs`

```csharp
namespace RazorForge.Analysis;

public class ModuleLoader
{
    private readonly ModuleResolver _resolver;
    private readonly ModuleCache _cache;
    private readonly List<CompilerError> _errors;

    public ModuleLoader(ModuleResolver resolver, ModuleCache cache)
    {
        _resolver = resolver;
        _cache = cache;
        _errors = new List<CompilerError>();
    }

    public List<CompilerError> Errors => _errors;

    /// <summary>
    /// Load a module and all its transitive dependencies
    /// </summary>
    public ModuleInfo? LoadModule(string importPath)
    {
        // Already loaded?
        if (_cache.IsLoaded(importPath))
            return _cache.Get(importPath);

        // Circular dependency?
        if (_cache.IsLoading(importPath))
        {
            _errors.Add(new CompilerError(
                $"Circular import detected: {importPath}",
                SourceLocation.Empty));
            return null;
        }

        // Resolve path
        string? filePath = _resolver.Resolve(importPath);
        if (filePath == null)
        {
            _errors.Add(new CompilerError(
                $"Cannot resolve import: {importPath}",
                SourceLocation.Empty));
            return null;
        }

        _cache.BeginLoading(importPath);

        try
        {
            // Parse the file
            string source = File.ReadAllText(filePath);
            var tokens = Tokenizer.Tokenize(source, Language.RazorForge);
            var parser = new RazorForgeParser(tokens);
            var ast = parser.Parse();

            // Check for namespace declaration
            string actualNamespace = GetNamespaceFromAST(ast)
                ?? GetNamespaceFromPath(filePath, importPath);
            string typeName = Path.GetFileNameWithoutExtension(filePath);
            string actualImportPath = $"{actualNamespace}/{typeName}";

            // Register namespace if different from expected
            if (actualImportPath != importPath)
            {
                _resolver.RegisterNamespace(actualImportPath, filePath);
            }

            // Create module info
            var module = new ModuleInfo
            {
                Namespace = actualNamespace,
                TypeName = typeName,
                FilePath = filePath,
                AST = ast
            };

            // Extract imports from AST
            foreach (var import in GetImportsFromAST(ast))
            {
                module.Imports.Add(import);
            }

            // Load transitive dependencies
            foreach (string dependency in module.Imports)
            {
                LoadModule(dependency);  // Recursive
            }

            _cache.FinishLoading(importPath, module);
            return module;
        }
        catch (Exception ex)
        {
            _cache.AbortLoading(importPath);
            _errors.Add(new CompilerError(
                $"Error loading module {importPath}: {ex.Message}",
                SourceLocation.Empty));
            return null;
        }
    }

    private string? GetNamespaceFromAST(ProgramNode ast)
    {
        // Find NamespaceDeclaration in AST
        foreach (var decl in ast.Declarations)
        {
            if (decl is NamespaceDeclaration ns)
                return ns.NamespacePath;
        }
        return null;
    }

    private string GetNamespaceFromPath(string filePath, string importPath)
    {
        // Extract namespace from import path (everything except last segment)
        int lastSlash = importPath.LastIndexOf('/');
        return lastSlash > 0 ? importPath.Substring(0, lastSlash) : "";
    }

    private IEnumerable<string> GetImportsFromAST(ProgramNode ast)
    {
        foreach (var decl in ast.Declarations)
        {
            if (decl is ImportDeclaration import)
                yield return import.ModulePath;
        }
    }
}
```

---

### Phase 6: SemanticAnalyzer Integration

**Goal**: Process imports and add symbols to scope

**Files to modify**: `src/Analysis/SemanticAnalyzer.cs`

```csharp
// Add fields
private readonly ModuleLoader _moduleLoader;
private readonly HashSet<string> _importedSymbols = new();

// Update constructor
public SemanticAnalyzer(ModuleLoader moduleLoader, ...)
{
    _moduleLoader = moduleLoader;
    // ...
}

// Implement VisitImportDeclaration
public object? VisitImportDeclaration(ImportDeclaration node)
{
    string importPath = node.ModulePath;
    string? alias = node.Alias;

    // Load the module
    ModuleInfo? module = _moduleLoader.LoadModule(importPath);
    if (module == null)
    {
        AddError($"Cannot import '{importPath}'", node.Location);
        return null;
    }

    // Get public symbols from module
    var publicSymbols = ExtractPublicSymbols(module);

    foreach (var symbol in publicSymbols)
    {
        string name = alias ?? symbol.Name;

        // Check for collision
        if (_importedSymbols.Contains(name))
        {
            AddError(
                $"Name collision: '{name}' is already imported. Use 'import {importPath} as Alias' to resolve.",
                node.Location);
            continue;
        }

        // Add to symbol table
        _symbolTable.Define(symbol with { Name = name });
        _importedSymbols.Add(name);
    }

    return null;
}

private List<Symbol> ExtractPublicSymbols(ModuleInfo module)
{
    var symbols = new List<Symbol>();

    foreach (var decl in module.AST.Declarations)
    {
        // Extract public classes/records/entities
        if (decl is ClassDeclaration cls && cls.Visibility == VisibilityModifier.Public)
        {
            symbols.Add(new ClassSymbol(cls.Name, ...));
        }

        // Extract public routines
        if (decl is FunctionDeclaration func && func.Visibility == VisibilityModifier.Public)
        {
            symbols.Add(new FunctionSymbol(func.Name, ...));
        }

        // Extract public presets
        if (decl is PresetDeclaration preset && preset.Visibility == VisibilityModifier.Public)
        {
            symbols.Add(new VariableSymbol(preset.Name, ...));
        }
    }

    return symbols;
}
```

---

### Phase 7: CLI/Program.cs Integration

**Goal**: Wire everything together in compilation pipeline

**Files to modify**: `src/CLI/Program.cs`

```csharp
// In compilation flow, before semantic analysis:

// Setup module system
string builtinPath = FindBuiltinPath();  // Find builtin/ folder
string projectRoot = Path.GetDirectoryName(inputFile);

var resolver = new ModuleResolver(builtinPath, projectRoot);
var cache = new ModuleCache();
var loader = new ModuleLoader(resolver, cache);

// Parse main file
var ast = parser.Parse();

// Load all imports (eagerly, before semantic analysis)
foreach (var decl in ast.Declarations)
{
    if (decl is ImportDeclaration import)
    {
        loader.LoadModule(import.ModulePath);
    }
}

// Check for loader errors
if (loader.Errors.Any())
{
    foreach (var error in loader.Errors)
        Console.WriteLine($"Error: {error.Message}");
    return 1;
}

// Now do semantic analysis with module loader
var analyzer = new SemanticAnalyzer(loader, ...);
analyzer.Analyze(ast);
```

---

## Testing Plan

### Unit Tests

1. **ModuleResolver tests**
    - Resolve builtin path
    - Resolve project path
    - Handle missing module
    - Namespace registry lookup

2. **ModuleCache tests**
    - Cache hit
    - Cache miss
    - Circular detection

3. **ModuleLoader tests**
    - Load single module
    - Load transitive dependencies
    - Circular import error
    - Missing import error

4. **SemanticAnalyzer tests**
    - Import public symbols
    - Alias handling
    - Collision detection

### Integration Tests

1. **Basic import**
   ```razorforge
   import Collections/List
   let x = List<s32>()
   ```

2. **Transitive import**
   ```razorforge
   import Collections/List
   # Should NOT see DynamicSlice without explicit import
   ```

3. **Collision resolution**
   ```razorforge
   import A/Foo
   import B/Foo as BFoo
   ```

4. **Namespace override**
   ```razorforge
   # File in internal/helpers/Thing.rf with namespace Utils
   import Utils/Thing  # Should work
   ```

---

## Estimated Effort

| Phase     | Description                  | Effort          |
|-----------|------------------------------|-----------------|
| 1         | Parser updates (namespace)   | 1-2 hours       |
| 2         | Module data structures       | 1 hour          |
| 3         | ModuleResolver               | 2-3 hours       |
| 4         | ModuleCache                  | 1 hour          |
| 5         | ModuleLoader                 | 3-4 hours       |
| 6         | SemanticAnalyzer integration | 3-4 hours       |
| 7         | CLI integration              | 1-2 hours       |
| -         | Testing & debugging          | 3-4 hours       |
| **Total** |                              | **15-21 hours** |

---

## Future Enhancements

1. **Selective imports** - `import A/{B, C}` syntax
2. **Re-exports** - `export import` syntax
3. **External packages** - Package manifest and external search paths
4. **Unused import warnings**
5. **IDE support** - Auto-import suggestions

---

## Open Questions

1. Should we support `import *` (import all from namespace)?
    - **Recommendation**: No, keep imports explicit

2. Should circular imports between different types in same namespace be allowed?
    - **Recommendation**: No, keep it simple

3. Should we preload all of builtin?
    - **Recommendation**: No, only load what's imported (on-demand)
