# RazorForge Language Server Protocol (LSP) TODO

**Status:** 🟡 **PARTIALLY IMPLEMENTED** (Basic infrastructure exists, core features missing)

**Last Updated:** 2025-12-14

This document tracks the implementation status of the RazorForge Language Server Protocol implementation and outlines
remaining work to achieve the IDE vision described in [IDE-Support.md](../wiki/IDE-Support.md).

---

## Table of Contents

- [Current Implementation Status](#current-implementation-status)
- [Core LSP Features](#core-lsp-features)
- [RazorForge-Specific Features](#razorforge-specific-features)
- [Vision Features from IDE-Support.md](#vision-features-from-ide-supportmd)
- [Implementation Priorities](#implementation-priorities)
- [Files to Modify](#files-to-modify)

---

## Current Implementation Status

### ✅ Implemented (Basic Infrastructure)

**Server Lifecycle:**
- ✅ Server initialization (`OnInitialize`, `OnStarted`)
- ✅ Server shutdown and cleanup
- ✅ Stdin/stdout communication
- ✅ Logging infrastructure

**Document Management:**
- ✅ Document open/close tracking (`DocumentManager`)
- ✅ Incremental text synchronization
- ✅ Document versioning
- ✅ Thread-safe document collection

**Compiler Integration:**
- ✅ Tokenization integration (`Tokenizer`)
- ✅ Parsing integration (`RazorForgeParser`)
- ✅ Semantic analysis integration (`SemanticAnalyzer`)
- ✅ Error extraction from compiler
- ✅ Symbol extraction (basic)
- ✅ AST generation

**Basic Services:**
- ✅ Code analysis pipeline (`RazorForgeCompilerService.AnalyzeCode`)
- ✅ Symbol extraction from AST
- ✅ Completion item generation (basic)
- ✅ Keyword completions
- ✅ Built-in function completions

### ❌ Missing (Core LSP Handlers)

**Critical Issue:**
- ❌ **NO LSP HANDLERS REGISTERED** - Line 78 in `RazorForgeLSP.cs` shows:
  ```csharp
  // TODO: Add handlers when interface compatibility is resolved
  ```
  This means the LSP server starts but **cannot respond to any client requests**.

**Missing Handlers:**
- ❌ Text document synchronization handlers
- ❌ Diagnostic publishing
- ❌ Completion request handler
- ❌ Hover request handler
- ❌ Go-to-definition handler
- ❌ Find references handler
- ❌ Document symbols handler
- ❌ Workspace symbols handler
- ❌ Code action handler
- ❌ Formatting handler
- ❌ Signature help handler

---

## Core LSP Features

### 1. Text Document Synchronization

**Status:** 🟡 **PARTIALLY IMPLEMENTED** (Backend exists, handlers missing)

**What's Implemented:**
- ✅ `DocumentManager.OpenDocument()` - handles document open events
- ✅ `DocumentManager.ChangeDocument()` - applies incremental changes
- ✅ `DocumentManager.CloseDocument()` - cleanup on close

**What's Missing:**
- ❌ LSP handler for `textDocument/didOpen`
- ❌ LSP handler for `textDocument/didChange`
- ❌ LSP handler for `textDocument/didClose`
- ❌ LSP handler for `textDocument/didSave`

**Implementation Needed:**
```csharp
// In RazorForgeLSP.cs - need to add these handlers
.WithHandler<TextDocumentSyncHandler>()
```

Create `TextDocumentSyncHandler.cs`:
```csharp
public class TextDocumentSyncHandler :
    TextDocumentSyncHandlerBase
{
    private readonly DocumentManager _documentManager;

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri)
    {
        return new TextDocumentAttributes(uri, "razorforge");
    }

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, ...)
    {
        _documentManager.OpenDocument(...);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, ...)
    {
        _documentManager.ChangeDocument(...);
        return Unit.Task;
    }

    // etc.
}
```

---

### 2. Diagnostics (Error/Warning Publishing)

**Status:** ❌ **NOT IMPLEMENTED**

**What's Implemented:**
- ✅ Error extraction from `SemanticAnalyzer`
- ✅ Error storage in `CompilationResult.Errors`

**What's Missing:**
- ❌ Publishing diagnostics to client
- ❌ Converting `SemanticError` to LSP `Diagnostic`
- ❌ Diagnostic severity mapping (error vs warning vs info)
- ❌ Real-time diagnostic updates on document change

**Implementation Needed:**

Create `DiagnosticsPublisher.cs`:
```csharp
public class DiagnosticsPublisher
{
    private readonly ILanguageServerFacade _server;

    public void PublishDiagnostics(DocumentState document)
    {
        var diagnostics = document.CompilationResult?.Errors
            .Select(error => new Diagnostic
            {
                Range = new Range(
                    new Position(error.Location.Line, error.Location.Column),
                    new Position(error.Location.Line, error.Location.Column + 1)
                ),
                Severity = DiagnosticSeverity.Error,
                Source = "razorforge",
                Message = error.Message
            })
            .ToArray() ?? Array.Empty<Diagnostic>();

        _server.TextDocument.PublishDiagnostics(new PublishDiagnosticsParams
        {
            Uri = document.Uri,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });
    }
}
```

Integrate into `DocumentManager.AnalyzeDocument()`:
```csharp
private void AnalyzeDocument(DocumentState document)
{
    // ... existing analysis code ...

    // Publish diagnostics to client
    _diagnosticsPublisher.PublishDiagnostics(updatedDocument);
}
```

---

### 3. Completion (Autocomplete)

**Status:** 🟡 **PARTIALLY IMPLEMENTED** (Backend exists, handler missing)

**What's Implemented:**
- ✅ `RazorForgeCompilerService.GetCompletions()` - generates suggestions
- ✅ Keyword completions
- ✅ Symbol completions from AST
- ✅ Built-in function completions (DynamicSlice, etc.)
- ✅ Completion item generation

**What's Missing:**
- ❌ LSP handler for `textDocument/completion`
- ❌ Context-aware completion (cursor position analysis)
- ❌ Trigger characters (`.`, `:`, `(`)
- ❌ Completion resolve (detailed info on demand)
- ❌ Snippet support (template expansion)
- ❌ Import suggestions
- ❌ Memory token completions (`.retain()`, `.share()`, etc.)

**Known Issues:**
- ⚠️ `GetCompletions()` doesn't use line/column parameters effectively
- ⚠️ No scope-aware filtering (local vs global symbols)
- ⚠️ No type-aware member access completion (e.g., `entity.` shows entity fields)

**Implementation Needed:**

Create `CompletionHandler.cs`:
```csharp
public class CompletionHandler : CompletionHandlerBase
{
    private readonly IRazorForgeCompilerService _compiler;
    private readonly DocumentManager _documentManager;

    public override Task<CompletionList> Handle(CompletionParams request, ...)
    {
        var document = _documentManager.GetDocument(request.TextDocument.Uri);
        var suggestions = _compiler.GetCompletions(
            document.Text,
            request.Position.Line,
            request.Position.Character
        );

        return Task.FromResult(new CompletionList(
            suggestions.Select(s => new CompletionItem
            {
                Label = s.Label,
                Detail = s.Detail,
                Documentation = s.Documentation,
                Kind = MapCompletionKind(s.Kind),
                InsertText = s.InsertText,
                InsertTextFormat = s.IsSnippet
                    ? InsertTextFormat.Snippet
                    : InsertTextFormat.PlainText
            })
        ));
    }

    protected override CompletionRegistrationOptions CreateRegistrationOptions(...)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = DocumentSelector.ForLanguage("razorforge"),
            TriggerCharacters = new[] { ".", ":", "(", "<" }
        };
    }
}
```

**Improvements Needed:**
1. Context-aware completion based on AST position
2. Member access completion (entity.field, entity.method())
3. Memory token method suggestions based on variable type
4. Generic type parameter completion

---

### 4. Hover (Documentation Tooltips)

**Status:** 🟡 **PARTIALLY IMPLEMENTED** (Backend exists, handler missing)

**What's Implemented:**
- ✅ `RazorForgeCompilerService.GetHoverInfo()` - extracts symbol info
- ✅ Symbol lookup by line
- ✅ Basic hover content generation

**What's Missing:**
- ❌ LSP handler for `textDocument/hover`
- ❌ Precise column-based symbol matching
- ❌ Markdown formatting for hover content
- ❌ Type information display
- ❌ Documentation from `###` comments
- ❌ Memory token state display (see IDE-Support.md vision)

**Known Issues:**
- ⚠️ Symbol matching uses `.FirstOrDefault()` - imprecise
- ⚠️ No column-based filtering
- ⚠️ Doesn't extract documentation comments

**Implementation Needed:**

Create `HoverHandler.cs`:
```csharp
public class HoverHandler : HoverHandlerBase
{
    private readonly IRazorForgeCompilerService _compiler;
    private readonly DocumentManager _documentManager;

    public override Task<Hover?> Handle(HoverParams request, ...)
    {
        var document = _documentManager.GetDocument(request.TextDocument.Uri);
        var hoverInfo = _compiler.GetHoverInfo(
            document.Text,
            request.Position.Line,
            request.Position.Character
        );

        if (hoverInfo == null)
            return Task.FromResult<Hover?>(null);

        return Task.FromResult<Hover?>(new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(
                new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"**{hoverInfo.Type}**\n\n{hoverInfo.Content}"
                }
            ),
            Range = new Range(
                new Position(hoverInfo.Location.Line, hoverInfo.Location.Column),
                new Position(hoverInfo.Location.Line, hoverInfo.Location.Column + 1)
            )
        });
    }
}
```

**Improvements Needed:**
1. Parse `###` documentation comments
2. Show memory token state (invalidated, retained, shared)
3. Show function signatures with parameter info
4. Show type definitions for entities/records

---

### 5. Go-to-Definition

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**
- ❌ Symbol definition tracking
- ❌ Cross-file symbol resolution
- ❌ Position-to-symbol mapping
- ❌ LSP handler for `textDocument/definition`

**Implementation Needed:**

Enhance `RazorForgeCompilerService`:
```csharp
public SourceLocation? GetDefinition(string code, int line, int column)
{
    // 1. Parse code to get AST
    // 2. Find identifier at position
    // 3. Resolve symbol in scope
    // 4. Return definition location
}
```

Create `DefinitionHandler.cs`:
```csharp
public class DefinitionHandler : DefinitionHandlerBase
{
    public override Task<LocationOrLocationLinks?> Handle(
        DefinitionParams request, ...)
    {
        // Use GetDefinition to find symbol location
        // Return LocationLink with target range
    }
}
```

---

### 6. Find References

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**
- ❌ Symbol usage tracking across files
- ❌ Workspace-wide symbol indexing
- ❌ LSP handler for `textDocument/references`

---

### 7. Document Symbols (Outline)

**Status:** 🟡 **PARTIALLY IMPLEMENTED** (Backend exists, handler missing)

**What's Implemented:**
- ✅ `RazorForgeCompilerService.GetSymbols()` - extracts all symbols
- ✅ Symbol extraction from declarations

**What's Missing:**
- ❌ LSP handler for `textDocument/documentSymbol`
- ❌ Hierarchical symbol tree (nested symbols)
- ❌ Symbol ranges (start/end positions)
- ❌ Selection ranges

**Implementation Needed:**

Create `DocumentSymbolHandler.cs`:
```csharp
public class DocumentSymbolHandler : DocumentSymbolHandlerBase
{
    public override Task<SymbolInformationOrDocumentSymbolContainer?> Handle(
        DocumentSymbolParams request, ...)
    {
        var symbols = _compiler.GetSymbols(document.Text);

        var documentSymbols = symbols.Select(s => new DocumentSymbol
        {
            Name = s.Name,
            Kind = MapSymbolKind(s.Kind),
            Range = ConvertRange(s.Location),
            SelectionRange = ConvertRange(s.Location),
            Detail = s.Type
        });

        return Task.FromResult<SymbolInformationOrDocumentSymbolContainer?>(
            new SymbolInformationOrDocumentSymbolContainer(documentSymbols)
        );
    }
}
```

---

### 8. Workspace Symbols (Global Search)

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**
- ❌ Workspace-wide symbol index
- ❌ Fast symbol search by name
- ❌ LSP handler for `workspace/symbol`

---

### 9. Code Actions (Quick Fixes)

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**
- ❌ Error-to-fix mapping
- ❌ Code transformation utilities
- ❌ LSP handler for `textDocument/codeAction`
- ❌ Suggested fixes from compiler errors

**Vision from IDE-Support.md:**
> One-click fixes for common errors (e.g., "Change to 'shared let doc ...'")

---

### 10. Formatting

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**
- ❌ Code formatter implementation
- ❌ LSP handler for `textDocument/formatting`
- ❌ Range formatting support

---

### 11. Signature Help (Parameter Hints)

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**
- ❌ Function signature extraction
- ❌ Active parameter detection
- ❌ LSP handler for `textDocument/signatureHelp`

---

## RazorForge-Specific Features

### Memory Token Analysis

**Status:** ❌ **NOT IMPLEMENTED**

**Vision from IDE-Support.md:**
> Live Memory Token Tracking - visually marks variables as "invalidated" when tokens are created

**What's Needed:**

1. **Token Creation Detection:**
   - Detect `.retain()`, `.share()`, `.track()`, `.consume()` calls
   - Track source variable invalidation
   - Track token lifespan

2. **Visual Invalidation Markers:**
   - Publish custom diagnostics for invalidated variables
   - Use `DiagnosticTag.Unnecessary` to grey out invalidated variables
   - Custom message: "Variable invalidated by token creation at line X"

3. **Hover Enhancement:**
   - Show token state: "Valid", "Invalidated (retained at line 42)", "Dead reference"
   - Show token type: "Retained<Node>", "Shared<Document, RWMutex>"
   - Link to token location

**Implementation:**

```csharp
public class MemoryTokenAnalyzer
{
    public List<TokenInvalidation> AnalyzeTokens(Program ast)
    {
        // 1. Find all .retain(), .share(), .consume() calls
        // 2. Track source variable
        // 3. Mark source as invalidated after token creation
        // 4. Return list of invalidations with locations
    }
}

public record TokenInvalidation(
    string VariableName,
    SourceLocation InvalidationSite,
    string TokenType,
    string TokenMethod
);
```

Publish as diagnostics:
```csharp
var diagnostic = new Diagnostic
{
    Range = invalidation.InvalidationSite.ToRange(),
    Severity = DiagnosticSeverity.Hint,
    Message = $"Variable '{invalidation.VariableName}' invalidated by {invalidation.TokenMethod}",
    Tags = new[] { DiagnosticTag.Unnecessary }, // Causes greying out
    Source = "razorforge-memory"
};
```

---

### Danger Block Visualization

**Status:** ❌ **NOT IMPLEMENTED**

**Vision from IDE-Support.md:**
> Danger blocks rendered with distinct background color

**What's Needed:**

1. **Semantic Token Support:**
   - Implement `textDocument/semanticTokens/full`
   - Define custom token type: `dangerBlock`
   - Mark all code inside `danger!` blocks

2. **Client-side Styling:**
   - VS Code extension provides custom theme colors
   - Other editors use semantic token modifiers

**Implementation:**

```csharp
public class SemanticTokensHandler : SemanticTokensHandlerBase
{
    public override Task<SemanticTokens?> Handle(
        SemanticTokensParams request, ...)
    {
        // 1. Parse AST
        // 2. Find all danger! blocks
        // 3. Mark token ranges as "dangerBlock"
        // 4. Return semantic tokens
    }
}
```

**Custom Token Legend:**
```json
{
  "tokenTypes": ["dangerBlock"],
  "tokenModifiers": []
}
```

**VS Code Theme Integration:**
```json
{
  "semanticHighlighting": true,
  "semanticTokenColors": {
    "dangerBlock": {
      "backgroundColor": "#ff000010"
    }
  }
}
```

---

### Structural Inference Visualization

**Status:** ❌ **NOT IMPLEMENTED**

**What's Needed:**

Based on the structural inference system (see [COMPILER_TODO.md #3](./COMPILER_TODO.md#3-iterator-permission-inference-and-structural-detection)):

1. **Structural Method Markers:**
   - Mark methods that modify DynamicSlice as structural
   - Show visual indicator (icon, color) for structural methods
   - Warning when calling structural methods on tokens

2. **Token Type Enforcement:**
   - Highlight invalid calls: `token.push(x)` where push is structural
   - Suggest fix: "Use owned container for structural modification"

---

### Function/Method Mutation Coloring (R/RW/RWS)

**Status:** ❌ **NOT IMPLEMENTED**

**Vision:**
Visually distinguish methods by their mutation category using semantic token coloring:
- **R (Read-only)** - methods that don't mutate `me` (green/blue tint)
- **RW (Read-write)** - methods that mutate `me` but not structural (yellow/orange tint)
- **RWS (Structural)** - methods that modify DynamicSlice control structures (red tint)

This provides instant visual feedback about what a method does, making the memory model transparent.

**Example:**
```razorforge
entity List<T> {
    private var _buffer: DynamicSlice
    private var _count: uaddr
}

// R - Read-only (colored green/blue)
routine List<T>.count() -> uaddr {
    return me._count
}

// R - Read-only (colored green/blue)
routine List<T>.get!(index: uaddr) -> T {
    return me._buffer.read_as<T>(offset: index)
}

// RW - Mutating (colored yellow/orange)
routine List<T>.__setitem__(index: uaddr, value: T) {
    me._buffer.write_as<T>(offset: index, value: value)
}

// RWS - Structural (colored red)
routine List<T>.push(value: T) {
    // Modifies DynamicSlice control structure
}

// RWS - Structural (colored red)
routine List<T>.pop!() -> T {
    // Modifies DynamicSlice control structure
}
```

**What's Needed:**

1. **Mutation Analysis Integration:**
   - Use compiler's mutation inference analysis (see [COMPILER_TODO.md #2](./COMPILER_TODO.md#2-method-mutation-inference))
   - Use compiler's structural inference analysis (see [COMPILER_TODO.md #3](./COMPILER_TODO.md#3-iterator-permission-inference-and-structural-detection))
   - Categorize each method as R, RW, or RWS

2. **Semantic Token Support:**
   - Extend semantic tokens handler
   - Define custom token modifiers:
     - `readonly` - for R methods
     - `mutating` - for RW methods
     - `structural` - for RWS methods

3. **Client-side Styling:**
   - VS Code extension provides theme colors
   - Other editors use semantic token modifiers

**Implementation:**

Add to `MutationAnalyzer.cs` (or create `FunctionCategorizer.cs`):
```csharp
public enum MethodCategory
{
    ReadOnly,      // R - doesn't mutate me
    Mutating,      // RW - mutates me, but not structural
    Structural     // RWS - modifies DynamicSlice control structures
}

public class FunctionCategorizer
{
    private readonly MutationAnalyzer _mutationAnalyzer;
    private readonly StructuralAnalyzer _structuralAnalyzer;

    public MethodCategory CategorizeMethod(RoutineDeclaration routine)
    {
        // Check structural first (most specific)
        if (_structuralAnalyzer.IsStructuralMutation(routine))
            return MethodCategory.Structural;

        // Check if mutating
        if (_mutationAnalyzer.IsMutating(routine))
            return MethodCategory.Mutating;

        // Default to read-only
        return MethodCategory.ReadOnly;
    }
}
```

Enhance `SemanticTokensHandler.cs`:
```csharp
public class SemanticTokensHandler : SemanticTokensHandlerBase
{
    private readonly FunctionCategorizer _categorizer;

    public override Task<SemanticTokens?> Handle(
        SemanticTokensParams request, ...)
    {
        var document = _documentManager.GetDocument(request.TextDocument.Uri);
        var ast = document.CompilationResult?.AST;

        var builder = new SemanticTokensBuilder();

        foreach (var declaration in ast.Declarations)
        {
            if (declaration is RoutineDeclaration routine)
            {
                var category = _categorizer.CategorizeMethod(routine);
                var modifier = category switch
                {
                    MethodCategory.ReadOnly => "readonly",
                    MethodCategory.Mutating => "mutating",
                    MethodCategory.Structural => "structural",
                    _ => ""
                };

                // Add semantic token for routine name
                builder.Push(
                    line: routine.Location.Line,
                    character: routine.Location.Column,
                    length: routine.Name.Length,
                    tokenType: SemanticTokenType.Function,
                    tokenModifiers: SemanticTokenModifier.From(modifier)
                );
            }
        }

        return Task.FromResult<SemanticTokens?>(builder.Build());
    }

    protected override SemanticTokensRegistrationOptions CreateRegistrationOptions(...)
    {
        return new SemanticTokensRegistrationOptions
        {
            DocumentSelector = DocumentSelector.ForLanguage("razorforge"),
            Legend = new SemanticTokensLegend
            {
                TokenTypes = new[]
                {
                    SemanticTokenType.Function,
                    SemanticTokenType.Method,
                    "dangerBlock"
                },
                TokenModifiers = new[]
                {
                    "readonly",
                    "mutating",
                    "structural"
                }
            },
            Full = true
        };
    }
}
```

**VS Code Extension - Theme Integration:**

Create `.vscode/extensions/razorforge/syntaxes/razorforge.tmLanguage.json`:
```json
{
  "semanticTokenColors": {
    "function.readonly": {
      "foreground": "#4EC9B0",
      "fontStyle": ""
    },
    "method.readonly": {
      "foreground": "#4EC9B0",
      "fontStyle": ""
    },
    "function.mutating": {
      "foreground": "#DCDCAA",
      "fontStyle": ""
    },
    "method.mutating": {
      "foreground": "#DCDCAA",
      "fontStyle": ""
    },
    "function.structural": {
      "foreground": "#F48771",
      "fontStyle": "bold"
    },
    "method.structural": {
      "foreground": "#F48771",
      "fontStyle": "bold"
    }
  }
}
```

**Benefits:**

1. **Instant Understanding:**
   - See at a glance which methods are safe to call on tokens
   - Understand mutation behavior without reading implementation

2. **Error Prevention:**
   - Red structural methods stand out - you know not to call them on tokens
   - Yellow mutating methods show you need `Hijacked<T>`, not `Viewed<T>`

3. **Documentation:**
   - Color serves as inline documentation
   - New developers learn the memory model through visual cues

4. **Consistency with Compiler:**
   - Uses the same inference as compiler error checking
   - No manual annotations needed

**Hover Enhancement:**

When hovering over a method, show its category:
```markdown
**routine List<T>.push(value: T)** [STRUCTURAL]

Adds an element to the list.

⚠️ This method modifies memory allocation (DynamicSlice).
Cannot be called on tokens (Viewed/Hijacked).
Use owned container for structural operations.
```

**Diagnostic Enhancement:**

When calling structural method on token:
```razorforge
hijacking list as h {
    h.push(item)  // ❌ Error highlighted with red underline
}
```

Error message:
```
Cannot call structural method 'push' on token Hijacked<List<T>>

Structural methods modify DynamicSlice control structures and require
ownership. Use the owned container instead:

  list.push(item)  // ✅ Correct

Or use consuming iteration:
  for item in list.consume() { ... }
```

---

## Vision Features from IDE-Support.md

### 1. Live Memory Token Tracking
- **Status:** ❌ Not implemented
- **Priority:** 🔴 HIGH (core differentiator)
- **See:** [Memory Token Analysis](#memory-token-analysis)

### 2. Context-Aware Assists (One-Click Fixes)
- **Status:** ❌ Not implemented
- **Priority:** 🔴 HIGH
- **See:** [Code Actions](#9-code-actions-quick-fixes)

### 3. Danger Zone Visualizer
- **Status:** ❌ Not implemented
- **Priority:** 🟡 MEDIUM
- **See:** [Danger Block Visualization](#danger-block-visualization)

### 4. Rich Documentation on Hover
- **Status:** 🟡 Partially implemented (basic hover exists)
- **Priority:** 🟡 MEDIUM
- **Needs:** Parse `###` comments, format as Markdown

### 5. Function/Method Mutation Coloring (R/RW/RWS)
- **Status:** ❌ Not implemented
- **Priority:** 🔴 HIGH (makes memory model transparent)
- **See:** [Function/Method Mutation Coloring](#functionmethod-mutation-coloring-rrwrws)

---

## Implementation Priorities

### Phase 1: Core LSP Functionality (CRITICAL)

**Goal:** Make the LSP server actually respond to requests

**Tasks:**
1. ✅ Fix handler registration (resolve interface compatibility issue)
2. ✅ Implement `TextDocumentSyncHandler`
3. ✅ Implement `DiagnosticsPublisher`
4. ✅ Implement `CompletionHandler`
5. ✅ Implement `HoverHandler`

**Estimated Effort:** 1-2 weeks

**Files:**
- `src/LanguageServer/RazorForgeLSP.cs` - remove TODO, register handlers
- `src/LanguageServer/Handlers/TextDocumentSyncHandler.cs` (new)
- `src/LanguageServer/Handlers/DiagnosticsPublisher.cs` (new)
- `src/LanguageServer/Handlers/CompletionHandler.cs` (new)
- `src/LanguageServer/Handlers/HoverHandler.cs` (new)

---

### Phase 2: Navigation Features

**Goal:** Enable go-to-definition, find references, document outline

**Tasks:**
1. ✅ Implement `DefinitionHandler`
2. ✅ Implement `DocumentSymbolHandler`
3. ✅ Implement symbol indexing for workspace
4. ✅ Implement `ReferencesHandler`

**Estimated Effort:** 2-3 weeks

---

### Phase 3: RazorForge-Specific Features

**Goal:** Implement memory token tracking, function coloring, and danger block visualization

**Tasks:**
1. ✅ Implement `FunctionCategorizer` (R/RW/RWS categorization)
2. ✅ Implement semantic tokens for function coloring
3. ✅ Implement `MemoryTokenAnalyzer`
4. ✅ Publish invalidation diagnostics
5. ✅ Enhance hover with token state and method category
6. ✅ Implement semantic tokens for danger blocks
7. ✅ Implement structural method markers
8. ✅ Enhanced error messages for structural violations

**Estimated Effort:** 3-4 weeks

---

### Phase 4: Developer Experience Enhancements

**Goal:** Code actions, formatting, signature help

**Tasks:**
1. ✅ Implement `CodeActionHandler` with quick fixes
2. ✅ Implement code formatter
3. ✅ Implement `SignatureHelpHandler`
4. ✅ Implement workspace symbols

**Estimated Effort:** 2-3 weeks

---

## Files to Modify

### Existing Files

**`src/LanguageServer/RazorForgeLSP.cs`:**
- Remove TODO comment on line 78
- Register all LSP handlers
- Configure server capabilities

**`src/LanguageServer/DocumentManager.cs`:**
- Integrate `DiagnosticsPublisher`
- Add method for position-to-symbol lookup

**`src/LanguageServer/RazorForgeCompilerService.cs`:**
- Fix `GetHoverInfo()` to use precise column matching
- Enhance `GetCompletions()` with context awareness
- Add `GetDefinition()` method
- Add `FindReferences()` method
- Parse `###` documentation comments

**`src/LanguageServer/IRazorForgeCompilerService.cs`:**
- Add `GetDefinition()` to interface
- Add `FindReferences()` to interface

---

### New Files to Create

**Handlers Directory:**
```
src/LanguageServer/Handlers/
├── TextDocumentSyncHandler.cs
├── CompletionHandler.cs
├── HoverHandler.cs
├── DefinitionHandler.cs
├── ReferencesHandler.cs
├── DocumentSymbolHandler.cs
├── WorkspaceSymbolHandler.cs
├── CodeActionHandler.cs
├── FormattingHandler.cs
├── SignatureHelpHandler.cs
└── SemanticTokensHandler.cs
```

**Analysis Components:**
```
src/LanguageServer/Analysis/
├── DiagnosticsPublisher.cs
├── MemoryTokenAnalyzer.cs
├── StructuralAnalyzer.cs
├── FunctionCategorizer.cs (R/RW/RWS classification)
└── DocumentationParser.cs
```

**Utilities:**
```
src/LanguageServer/Utilities/
├── LspHelpers.cs (Range conversion, etc.)
└── SymbolIndex.cs (Workspace-wide symbol tracking)
```

---

## Testing Requirements

### Unit Tests Needed

1. **DocumentManager Tests:**
   - Document lifecycle (open/change/close)
   - Incremental change application
   - Version tracking

2. **Compiler Service Tests:**
   - Symbol extraction accuracy
   - Completion filtering
   - Hover info generation

3. **Memory Token Analyzer Tests:**
   - Token invalidation detection
   - Token type tracking
   - Multi-statement analysis

### Integration Tests Needed

1. **LSP Protocol Tests:**
   - Handler registration
   - Request/response flow
   - Error handling

2. **End-to-End Tests:**
   - Full document editing workflow
   - Multi-file navigation
   - Diagnostic publishing

---

## Summary

**Current State:**
- ✅ Basic LSP server infrastructure exists
- ✅ Compiler integration works
- ❌ **Critical Issue:** No handlers registered - server cannot respond to requests
- 🟡 Backend services partially implemented

**Next Steps:**
1. **IMMEDIATE:** Fix handler registration and implement Phase 1 (Core LSP)
2. **SHORT-TERM:** Implement Phase 2 (Navigation)
3. **MEDIUM-TERM:** Implement Phase 3 (RazorForge-specific features)
4. **LONG-TERM:** Implement Phase 4 (Developer experience)

**Estimated Total Effort:** 9-13 weeks to full implementation

**Key Dependencies:**
- OmniSharp.Extensions.LanguageServer library interface compatibility
- Compiler analysis improvements:
  - Mutation inference (COMPILER_TODO.md #2) - required for R/RW categorization
  - Structural detection (COMPILER_TODO.md #3) - required for RWS categorization
  - DynamicSlice modification analysis - required for function coloring
- VS Code extension for client-side features:
  - Semantic token rendering (function coloring, danger blocks)
  - Custom theme colors for R/RW/RWS methods
  - Diagnostic tag support (greyed-out invalidated variables)
