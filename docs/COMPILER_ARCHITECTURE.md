# RazorForge Compiler Architecture

## Table of Contents

1. [Compilation Pipeline](#compilation-pipeline)
2. [Directory Structure](#directory-structure)
3. [Key Components](#key-components)
4. [Generic Types Implementation Status](#generic-types-implementation-status)
5. [Module/Import System Status](#moduleimport-system-status)
6. [Flow Charts](#flow-charts)
7. [Quick Reference](#quick-reference)

---

## Compilation Pipeline

```
Source Code (.rf/.sf)
        ↓
    TOKENIZATION (Lexer)
    - RazorForgeTokenizer / SuflaeTokenizer
    - Produces: List<Token>
        ↓
    PARSING (Parser)
    - RazorForgeParser / SuflaeParser
    - Produces: AST (Abstract Syntax Tree)
        ↓
    SEMANTIC ANALYSIS (Analyzer)
    - SemanticAnalyzer + MemoryAnalyzer
    - Type checking, symbol resolution
    - Produces: Validated AST + Errors
        ↓
    CODE GENERATION (Generators)
    - SimpleCodeGenerator → .out file
    - LLVMCodeGenerator → .ll file (LLVM IR)
        ↓
    EXECUTABLE GENERATION (clang)
    - Compiles LLVM IR to native executable
    - Produces: .exe/.out binary
```

**Compiler Entry Point:** `src/CLI/Program.cs:94` (Main method)

**Language Entry Point:** `routine main()` - Returns `Blank` (no return type specified, no return statement needed)

```razorforge
routine main() {
    Console.show_line("Hello, RazorForge!")
}
```

**C ABI Compatibility:** The code generator automatically treats `main()` specially:

- Generates `define i32 @main()` in LLVM IR (not `void`)
- Adds implicit `ret i32 0` at the end
- This ensures proper exit code handling on all platforms

---

## Directory Structure

```
src/
├── CLI/
│   └── Program.cs              # Entry point, orchestrates compilation
├── Lexer/
│   ├── BaseTokenizer.cs        # Base tokenizer logic
│   ├── RazorForgeTokenizer.cs  # RazorForge-specific tokenization
│   ├── SuflaeTokenizer.cs      # Suflae-specific tokenization
│   ├── Token.cs                # Token data structure
│   └── TokenType.cs            # All token types (keywords, operators, etc.)
├── Parser/
│   ├── BaseParser.cs           # Common parsing utilities
│   ├── RazorForgeParser.cs     # RazorForge syntax parser ⭐
│   └── SuflaeParser.cs         # Suflae syntax parser
├── AST/
│   ├── ASTNode.cs              # Base AST node interfaces
│   ├── Declarations.cs         # Declaration AST nodes ⭐
│   ├── Expressions.cs          # Expression AST nodes ⭐
│   └── Statements.cs           # Statement AST nodes
├── Analysis/
│   ├── SemanticAnalyzer.cs     # Type checking & validation ⭐
│   ├── SymbolTable.cs          # Symbol tracking & scoping ⭐
│   ├── MemoryAnalyzer.cs       # Memory safety analysis
│   ├── MemoryModel.cs          # Memory model definitions
│   ├── FunctionVariantGenerator.cs  # Generates try_/check_/find_ variants ⭐
│   ├── ModuleResolver.cs       # Module path resolution
│   └── Language.cs             # Language mode enums
└── CodeGen/
    ├── LLVMCodeGenerator.cs    # LLVM IR generation ⭐
    ├── SimpleCodeGenerator.cs  # Readable output generation
    └── TargetPlatform.cs       # Platform-specific config (Win/Linux/macOS)

⭐ = Files with generic/import/variant implementation
```

---

## Key Components

### 1. Tokenizer (Lexer Phase)

**Location:** `src/Lexer/`

**Purpose:** Convert source text into tokens

**Key Classes:**

- `Tokenizer` - Static entry point
- `RazorForgeTokenizer` / `SuflaeTokenizer` - Language-specific implementations
- `Token` - Data: type, value, location
- `TokenType` - Enum of all token types

**Flow:**

```csharp
string sourceCode = File.ReadAllText(filePath);
List<Token> tokens = Tokenizer.Tokenize(sourceCode, language);
```

**Supported Features:**

- Keywords: `entity`, `record`, `routine`, `import`, `throw`, `absent`, etc.
- Operators: `+`, `-`, `*`, `/`, `//`, `<`, `>`, etc.
- Literals: numbers, strings, characters
- Duration literals: `5w`, `3d`, `2h`, `30m`, `45s`, `100ms`, `500us`, `1000ns`
- Memory size literals: `1024b`, `64kb`, `64kib`, `1mb`, `1mib`, `2gb`, `2gib`, `3tb`, `3tib`, `4pb`, `4pib`
- Generic brackets: `<`, `>` (for `List<T>`)
- Path separator: `/` (for `import a/b/c`)
- Failable function marker: `!` suffix (for `routine name!()`)
- Error handling: `throw`, `absent` keywords

---

### 2. Parser (Syntax Analysis)

**Location:** `src/Parser/RazorForgeParser.cs`

**Purpose:** Build Abstract Syntax Tree from tokens

**Key Methods:**

- `ParseDeclaration()` - Top-level declarations
- `ParseExpression()` - All expression types
- `ParseStatement()` - Control flow, assignments
- `ParseTypeExpression()` - Type references (including `List<T>`)
- `ParseImportDeclaration()` - Import statements

**Generic Parsing:** ✅ **FULLY IMPLEMENTED**

```csharp
// Lines 425-437: Parses entity Buffer<T>
private ClassDeclaration ParseClassDeclaration(VisibilityModifier visibility)
{
    // ...
    List<string> genericParams = ParseGenericParameters(); // <T, U>
    // ...
}

// Lines 518-541: Namespace-qualified generics Console.show<T>
// Lines 510-536: Generic methods on generic types List<T>.select<U>
```

**Advanced Generic Syntax Supported:**

```razorforge
routine Console.show<T>(value: T) { }           # Namespace.method<T>
routine List<T>.select<U>(mapper: ...) { }      # Type<T>.method<U>
let f: Routine<(T), U>                          # Tuple types for lambda params
```

**Import Parsing:** ⚠️ **PARTIAL**

```csharp
// Lines 269-302: Parses import stdlib/memory/DynamicSlice
private ImportDeclaration ParseImportDeclaration()
{
    string modulePath = "";
    do {
        string part = ConsumeIdentifier();
        modulePath += part;
        if (Match(TokenType.Slash)) modulePath += "/";
        else break;
    } while (true);
    // ...
}
```

**Issue:** `ConsumeIdentifier()` doesn't accept keywords like `entity`, so `import Collections/entity/List` fails.

**When Statement Pattern Matching:** ✅ **IMPLEMENTED**

```csharp
// ParseWhenStatement() handles pattern matching
when value {
    42 => handle_literal()           // LiteralPattern
    x => use_variable(x)             // IdentifierPattern
    _ => handle_default()            // WildcardPattern
    is SomeType => handle_type()     // TypePattern (no binding)
    is SomeType x => handle_typed(x) // TypePattern with binding
}
```

**Context-Aware Parsing:**

The parser uses `_inWhenClauseBody` flag to prevent `is` expression operator parsing inside when clause bodies. This
allows `is TypeName` patterns without conflict with the `expr is Type` expression syntax.

```csharp
// In ParseIsExpression():
while (!_inWhenPatternContext && !_inWhenClauseBody && Match(TokenType.Is, ...))
{
    // Only parse 'is' as expression operator outside when contexts
}
```

---

### 3. AST (Abstract Syntax Tree)

**Location:** `src/AST/`

**Purpose:** Intermediate representation of program structure

**Key Node Types:**

#### Declarations (`Declarations.cs`)

```csharp
// Generic entity declaration
ClassDeclaration(
    Name: "Buffer",
    GenericParameters: ["T"],       // ✅ Supported
    // ...
)

// Import declaration
ImportDeclaration(
    ModulePath: "stdlib/memory/DynamicSlice",
    Alias: null,
    SpecificImports: null,          // ⚠️ Not parsed yet
    // ...
)
```

#### Expressions (`Expressions.cs`)

```csharp
// Generic type reference
TypeExpression(
    Name: "List",
    GenericArguments: [TypeExpression("s32")],  // List<s32>
    // ...
)

// Generic method call
GenericMethodCallExpression(
    Object: buffer,
    MethodName: "read",
    TypeArguments: [TypeExpression("s32")],     // buffer.read<s32>!()
    Arguments: [offset],
    // ...
)

// Tuple type (for Routine<(T), U> syntax)
TypeExpression(
    Name: "__Tuple",
    GenericArguments: [TypeExpression("T")],    // (T) tuple
    // ...
)

// Block expression (for if cond { expr } else { expr })
BlockExpression(
    Value: Expression,                          // The expression the block evaluates to
    // ...
)
```

---

### 4. Semantic Analyzer (Type Checking)

**Location:** `src/Analysis/SemanticAnalyzer.cs`

**Purpose:** Validate program semantics, resolve types, check memory safety

**Key Features:**

#### Generic Type Resolution ✅ **PARTIAL**

```csharp
// Lines 1353-1375: Resolves List<s32> to concrete type
private TypeInfo? ResolveGenericType(
    TypeInfo genericType,
    Dictionary<string, TypeInfo> genericBindings)
{
    // Substitutes T with s32
    // ...
}
```

#### Generic Method Calls ✅ **IMPLEMENTED**

```csharp
// Lines 2092-2122: Handles buffer.read<T>!()
public object? VisitGenericMethodCallExpression(
    GenericMethodCallExpression node)
{
    // Special handling for DynamicSlice operations
    if (methodName is "read" or "write" && IsSliceType(objectType))
    {
        // Validates memory operations
        return ResolveType(typeArg);
    }
    // TODO: Generic method validation for other types
}
```

#### Import Processing ❌ **NOT IMPLEMENTED**

```csharp
// Line 523
public object? VisitImportDeclaration(ImportDeclaration node)
{
    // TODO: Module system
    return null;
}
```

**Current Status:** Imports are parsed but completely ignored during semantic analysis.

---

### 5. Symbol Table

**Location:** `src/Analysis/SymbolTable.cs`

**Purpose:** Track symbols, manage scopes, resolve names

**Key Symbol Types:**

```csharp
// All support generic parameters
FunctionSymbol(
    Name: "map",
    GenericParameters: ["T", "U"],      // ✅ Supported
    GenericConstraints: [...],          // ✅ Supported
    // ...
)

ClassSymbol(
    Name: "List",
    GenericParameters: ["T"],           // ✅ Supported
    // ...
)
```

**Generic Constraints:**

```csharp
GenericConstraint(
    ParameterName: "T",
    BaseTypes: [TypeInfo("Comparable")],    // where T : Comparable
    IsValueType: false,                     // where T : record
    IsReferenceType: false                  // where T : entity
)
```

**Missing:** No module/namespace tracking. Symbols are global or local, no intermediate module scope.

---

### 6. Function Variant Generator

**Location:** `src/Analysis/FunctionVariantGenerator.cs`

**Purpose:** Generate safe function variants from failable (`!`) functions

**How It Works:**

When the compiler encounters a failable function (marked with `!`), it analyzes the function body for:

- `throw` statements - indicates the function can crash with an error
- `absent` statements - indicates the function can return "not found"

Based on these, it generates safe variants:

| `throw` | `absent` | Generated Variants                                 |
|---------|----------|----------------------------------------------------|
| no      | no       | Compile Error (must use throw or absent)           |
| no      | yes      | `try_` only (returns `Maybe<T>`)                   |
| yes     | no       | `try_`, `check_` (returns `Maybe<T>`, `Result<T>`) |
| yes     | yes      | `try_`, `find_` (returns `Maybe<T>`, `Lookup<T>`)  |

**Example:**

```razorforge
# User writes:
routine divide!(a: s32, b: s32) -> s32 {
    if b == 0 {
        throw DivisionError(message: "Division by zero")
    }
    return a / b
}

# Compiler generates:
routine try_divide(a: s32, b: s32) -> s32? { ... }
routine check_divide(a: s32, b: s32) -> Result<s32> { ... }
```

**Type Constructor Support:**

- Default constructor: `__create__`
- Failable constructor: `__create__!`
- Generated variants: `try___create__`, `check___create__`, etc.

See: [RazorForge Error Handling](../wiki/RazorForge-Error-Handling.md)

---

### 7. Code Generation

#### LLVM IR Generator

**Location:** `src/CodeGen/LLVMCodeGenerator.cs`

**Purpose:** Generate LLVM Intermediate Representation

**Generic Support:** ✅ **PARTIAL**

```csharp
// Lines 60-65: Tracks generic instantiations
private readonly Dictionary<string, List<List<TypeInfo>>>
    _genericInstantiations = new();

// Lines 201-227: Name mangling for generics
private string MangleGenericName(string baseName, List<TypeInfo> typeArgs)
{
    // List<s32> → List_s32
    // Dict<String, s32> → Dict_String_s32
}
```

**Generic Method Calls:** ✅ **IMPLEMENTED**

```csharp
// Lines 1744-1857: Handles read<T>!(), write<T>!()
public string VisitGenericMethodCallExpression(
    GenericMethodCallExpression node)
{
    if (methodName == "read")
    {
        // Generates: %tmp0 = call i32 @read_s32(ptr %slice)
    }
}
```

**TODO:**

- Generic member access (Line 1861)
- Full monomorphization (currently only tracks, doesn't generate all instances)

---

## Generic Types Implementation Status

### ✅ Implemented (Working)

1. **Parser Support**
    - Entity/record/variant with generic parameters: `entity Buffer<T>`
    - Generic type references: `List<s32>`
    - Generic method calls: `buffer.read<T>!()`
    - Multiple type parameters: `Dict<K, V>`
    - Namespace-qualified generic methods: `Console.show<T>(value)`
    - Generic methods on generic types: `List<T>.select<U>(mapper)`
    - Tuple types for lambda parameters: `Routine<(T), U>`

2. **AST Representation**
    - `TypeExpression.GenericArguments`
    - `GenericMethodCallExpression`
    - `GenericConstraint` definitions
    - `BlockExpression` for block-style conditionals

3. **Symbol Table**
    - `FunctionSymbol.GenericParameters`
    - `ClassSymbol.GenericParameters`
    - Generic constraint storage

4. **Code Generation**
    - Generic method call handling (for DynamicSlice operations)
    - Name mangling for generic types
    - Instantiation tracking

5. **Real Working Examples**
   ```razorforge
   # From tests/samples/hello_world.rf
   record Buffer<T> {
       private var data: DynamicSlice<T>
       public func push!(item: T) { ... }
   }

   var buffer = Buffer<s32>(10)
   buffer.push!(42)
   ```

### ⚠️ Partial / TODO

1. **Semantic Validation**
    - Generic constraint checking (Line 1483: "TODO: Check actual inheritance")
    - Generic type instantiation validation (count/kind of type args)
    - Type parameter inference (partial support)

2. **Code Generation**
    - Full monomorphization (generate code for all used instantiations)
    - Generic member access (Line 1861: "TODO: Implement")
    - Generic trait/feature methods

3. **Advanced Features**
    - Generic specialization
    - Variance (covariance/contravariance)
    - Default type parameters
    - Associated types

### ❌ Not Implemented

1. **Higher-kinded types** (types that take types as parameters)
2. **Generic closure support**
3. **Generic async functions**

---

## Module/Import System Design

### Design Decisions

1. **Search Order**: `builtin/` → project root → external packages

2. **What's Imported**: The entity/record and its public API (public routines, presets, etc.)

3. **Access Style**: Unqualified access - use `List<s32>` directly after import
    - **Name collisions**: Compiler error requiring alias to resolve

4. **Selective Imports**: `import Collections/{List, Dict}` imports both symbols

5. **Loading Strategy**: Eager loading - parse all transitive imports before semantic analysis

6. **Transitive Visibility**: None - must explicitly import each dependency
    - If `List.rf` imports `DynamicSlice`, users of `List` must explicitly `import memory/DynamicSlice`

7. **Namespace Resolution**:
    - If file declares `namespace Foo`, the import path is `Foo/TypeName`
    - Folder structure is only the default when no `namespace` is declared
    - Namespace declaration takes priority over physical file location
    - Example: File at `builtin/Collections/List.rf` with `namespace Foo` → import as `Foo/List`

### Import Syntax

```razorforge
# Basic import
import Collections/List

# Import with alias
import Collections/List as L

# Selective imports
import Collections/{List, Dict}

# Namespace declaration (in source file)
namespace MyNamespace
```

### Implementation Status

#### ✅ Implemented (Working)

1. **Parser Support**
    - Basic import paths: `import Collections/List`
    - Nested paths with `/`: `import a/b/c/d`
    - Import aliases: `import std as S`

2. **AST Representation**
    - `ImportDeclaration` node with all fields

3. **Token Support**
    - `Import`, `As`, `Slash` tokens

#### ⚠️ Partial

1. **Selective Imports** - AST defined but not parsed
   ```razorforge
   import Collections/{List, Dict}  # Not working yet
   ```

2. **Path Resolution** - Parser accepts keywords in paths inconsistently

#### ❌ Not Implemented

1. **ModuleResolver** - Resolve import paths to file paths
    - Search order: `builtin/` → project root → external packages
    - Handle namespace declarations overriding folder paths

2. **ModuleCache** - Track loaded modules to avoid re-parsing

3. **Symbol Table Population** - Add imported symbols to scope

4. **Transitive Import Loading** - Eagerly load all dependencies

5. **Namespace Declaration Parsing** - Parse `namespace` in source files

6. **Collision Detection** - Error on duplicate symbol names

7. **Circular Import Detection**

8. **Using Declarations** (type aliases)
   ```razorforge
   using IntList = List<s32>  # Not working
   ```

9. **Redefinition**
   ```razorforge
   redefinition OldName as NewName  # Not working
   ```

---

## Flow Charts

### Overall Compilation Flow

```
┌─────────────────┐
│   Source File   │
│  (.rf or .sf)   │
└────────┬────────┘
         │
         ▼
┌─────────────────────────────────┐
│      CLI/Program.cs:94          │
│  Main(string[] args)            │
│                                 │
│  Commands:                      │
│  - compile <file>               │
│  - run <file>                   │
│  - compileandrun <file>         │
└────────┬────────────────────────┘
         │
         ▼
┌─────────────────────────────────┐
│   PHASE 1: TOKENIZATION         │
│   Lexer/Tokenizer.cs            │
│                                 │
│   Source → List<Token>          │
│                                 │
│   Handles:                      │
│   - Keywords (entity, import)   │
│   - Operators (<, >, /)         │
│   - Literals (42, "text")       │
└────────┬────────────────────────┘
         │
         ▼
┌─────────────────────────────────┐
│   PHASE 2: PARSING              │
│   Parser/RazorForgeParser.cs    │
│                                 │
│   List<Token> → AST             │
│                                 │
│   Parses:                       │
│   - Generic syntax ✅            │
│   - Import statements ✅         │
│   - All declarations            │
└────────┬────────────────────────┘
         │
         ▼
┌─────────────────────────────────┐
│   PHASE 3: SEMANTIC ANALYSIS    │
│   Analysis/SemanticAnalyzer.cs  │
│   Analysis/SymbolTable.cs       │
│                                 │
│   AST → Validated AST           │
│                                 │
│   Checks:                       │
│   - Type correctness            │
│   - Symbol resolution           │
│   - Memory safety               │
│   - Generic constraints ⚠️       │
│   - Import resolution ❌         │
└────────┬────────────────────────┘
         │
         ├──────────┬──────────────┐
         ▼          ▼              ▼
   ┌─────────┐ ┌─────────┐   ┌─────────┐
   │ Simple  │ │  LLVM   │   │ Memory  │
   │ CodeGen │ │ CodeGen │   │Analyzer │
   └─────────┘ └─────────┘   └─────────┘
         │          │
         ▼          ▼
   ┌─────────┐ ┌──────────┐
   │ .out    │ │ .ll file │
   │ file    │ │ (LLVM IR)│
   └─────────┘ └────┬─────┘
                    │
                    ▼
              ┌──────────┐
              │  clang   │
              └────┬─────┘
                   │
                   ▼
              ┌──────────┐
              │   .exe   │
              └──────────┘
```

### Generic Type Processing Flow

```
Source: entity List<T> { ... }
         │
         ▼
┌────────────────────────────────┐
│  PARSER                        │
│  RazorForgeParser.cs:425       │
│                                │
│  ParseGenericParameters()      │
│  - Sees '<T>'                  │
│  - Extracts ["T"]              │
└─────────┬──────────────────────┘
          │
          ▼
     ┌─────────┐
     │   AST   │
     │ ClassDeclaration(          │
     │   Name: "List",            │
     │   GenericParameters: ["T"] │
     │ )                          │
     └─────────┬──────────────────┘
               │
               ▼
┌──────────────────────────────────┐
│  SEMANTIC ANALYZER               │
│  SemanticAnalyzer.cs:1353        │
│                                  │
│  When sees: List<s32>            │
│  1. Looks up "List" symbol       │
│  2. Checks GenericParameters     │
│  3. Binds T → s32                │
│  4. Creates TypeInfo(            │
│       Name: "List",              │
│       GenericArguments: [s32]    │
│     )                            │
└─────────┬────────────────────────┘
          │
          ▼
┌──────────────────────────────────┐
│  CODE GENERATOR                  │
│  LLVMCodeGenerator.cs:201        │
│                                  │
│  MangleGenericName()             │
│  List<s32> → "List_s32"          │
│                                  │
│  Generates:                      │
│  %struct.List_s32 = type {       │
│    ptr, i64, i64                 │
│  }                               │
└──────────────────────────────────┘
```

### Import Processing Flow (Current)

```
Source: import Collections/List
         │
         ▼
┌────────────────────────────────┐
│  PARSER                        │
│  RazorForgeParser.cs:269       │
│                                │
│  ParseImportDeclaration()      │
│  - Reads path segments         │
│  - Creates ImportDeclaration   │
└─────────┬──────────────────────┘
          │
          ▼
     ┌─────────┐
     │   AST   │
     │ ImportDeclaration(         │
     │   ModulePath:              │
     │     "Collections/List"     │
     │ )                          │
     └─────────┬──────────────────┘
               │
               ▼
┌──────────────────────────────────┐
│  SEMANTIC ANALYZER               │
│  SemanticAnalyzer.cs:523         │
│                                  │
│  VisitImportDeclaration()        │
│  {                               │
│    // TODO: Module system        │
│    return null;  ← STOPS HERE ❌ │
│  }                               │
│                                  │
│  Symbol "List" never added to    │
│  symbol table!                   │
└──────────────────────────────────┘
```

### Import Processing Flow (Target Design)

```
Source: import Collections/List
         │
         ▼
┌────────────────────────────────────────────┐
│  MODULE RESOLVER                           │
│  Analysis/ModuleResolver.cs                │
│                                            │
│  1. Search order:                          │
│     builtin/ → project root → external     │
│                                            │
│  2. For each search path:                  │
│     a. Check namespace registry first      │
│        (if "Collections" declared in       │
│         another file, use that location)   │
│     b. Fall back to folder structure       │
│        Collections/List → .../List.rf      │
│                                            │
│  3. Check ModuleCache - already loaded?    │
│  4. If not loaded:                         │
│     a. Parse the file                      │
│     b. Check for `namespace` declaration   │
│     c. Register namespace → file mapping   │
│     d. Recursively load its imports        │
│     e. Cache the module                    │
└─────────┬──────────────────────────────────┘
          │
          ▼
┌────────────────────────────────────────────┐
│  SEMANTIC ANALYZER                         │
│                                            │
│  1. Get module's public symbols:           │
│     - Public entities/records              │
│     - Public routines                      │
│     - Public presets                       │
│                                            │
│  2. Check for name collisions:             │
│     - If collision → Compiler Error        │
│     - User must use alias to resolve       │
│                                            │
│  3. Add symbols to current scope           │
│     (unqualified access)                   │
│                                            │
│  4. If alias provided (import X as Y):     │
│     - Register under alias name only       │
└────────────────────────────────────────────┘
```

### Namespace Resolution Example

```
# File: builtin/Collections/List.rf
namespace Collections    # Matches folder, import as Collections/List

# File: builtin/internal/OldList.rf
namespace Collections    # Override! Import as Collections/OldList, NOT internal/OldList

# File: builtin/Text/Text.rf
# (no namespace declaration)
# Default: import as Text/Text (folder path)
```

---

## Quick Reference

### Key Files by Feature

| Feature         | Parser                | AST                 | Semantic                   | CodeGen                |
|-----------------|-----------------------|---------------------|----------------------------|------------------------|
| **Generics**    | RazorForgeParser:425  | Declarations.cs:56  | SemanticAnalyzer:1353      | LLVMCodeGenerator:201  |
| **Imports**     | RazorForgeParser:269  | Declarations.cs:339 | SemanticAnalyzer:523 ❌     | N/A ❌                  |
| **Functions**   | RazorForgeParser:1685 | Declarations.cs:85  | SemanticAnalyzer:558       | LLVMCodeGenerator:392  |
| **Types**       | RazorForgeParser:1098 | Expressions.cs:506  | SemanticAnalyzer:1353      | LLVMCodeGenerator:1600 |
| **Memory**      | RazorForgeParser:2405 | Expressions.cs:600  | MemoryAnalyzer             | LLVMCodeGenerator:2264 |
| **Variants**    | N/A                   | N/A                 | FunctionVariantGenerator ✅ | LLVMCodeGenerator ✅    |
| **Console I/O** | N/A                   | N/A                 | N/A                        | LLVMCodeGenerator ✅    |

### Common Tasks

**Add new keyword:**

1. `src/Lexer/TokenType.cs` - Add to enum
2. `src/Lexer/RazorForgeTokenizer.cs` - Add to keyword map
3. Parser - Handle in appropriate method

**Add new AST node:**

1. `src/AST/` - Add record type
2. Parser - Create parse method
3. `SemanticAnalyzer` - Add Visit method
4. `LLVMCodeGenerator` - Add Visit method

**Debug compilation:**

1. Set breakpoint in `Program.cs:94`
2. Inspect tokens after tokenization
3. Inspect AST after parsing
4. Check `SemanticAnalyzer.Errors` list
5. View generated LLVM IR in `.ll` file

### Important Line Numbers

**Generic Type Resolution:**

- Parse: `RazorForgeParser.cs:425-437`
- Analyze: `SemanticAnalyzer.cs:1353-1375`
- Generate: `LLVMCodeGenerator.cs:201-227`

**Import Processing:**

- Parse: `RazorForgeParser.cs:269-302`
- Analyze: `SemanticAnalyzer.cs:523` ⚠️ TODO
- Generate: N/A ⚠️ Not needed

**Generic Method Calls:**

- Parse: `RazorForgeParser.cs:2405-2442`
- Analyze: `SemanticAnalyzer.cs:2092-2122`
- Generate: `LLVMCodeGenerator.cs:1744-1857`

---

## Next Steps for Full stdlib Support

### Priority 1: Complete Import System

1. Implement `ModuleResolver` class
2. Resolve import paths to file paths
3. Parse imported files
4. Populate symbol table with imported symbols
5. Handle transitive imports

**Estimated Effort:** 2-3 days

### Priority 2: Complete Generic Validation

1. Validate generic constraint satisfaction
2. Check type parameter counts match
3. Infer generic arguments where possible
4. Better error messages for generic errors

**Estimated Effort:** 1-2 days

### Priority 3: Full Monomorphization

1. Generate code for all used generic instantiations
2. Deduplicate identical instantiations
3. Handle recursive generic types

**Estimated Effort:** 2-3 days

---

## Conclusion

The compiler has **excellent foundation** for generics and imports:

- ✅ Syntax parsing works
- ✅ AST representation complete
- ✅ Symbol table supports generics
- ⚠️ Semantic analysis partial
- ❌ Module system not implemented

**The stdlib (`List<T>`, `Text<T>`) is architecturally correct and will work once these features are completed!**
