# RazorForge Language Server Protocol (LSP) TODO

**Status:** 🟡 **PARTIALLY IMPLEMENTED** (Basic infrastructure exists, core features missing)

**Last Updated:** 2025-12-14

This document tracks the implementation status of the RazorForge Language Server Protocol implementation and outlines remaining work to achieve the IDE vision described in [IDE-Support.md](../wiki/IDE-Support.md).

---

## 🎯 IMMEDIATE PRIORITIES

### Phase 1: Core LSP Handlers (1-2 weeks) - CRITICAL BLOCKER

**Status:** 🔴 **LSP SERVER CANNOT RESPOND TO REQUESTS**

**Problem:** Line 78 in `src/LanguageServer/RazorForgeLSP.cs`:
```csharp
// TODO: Add handlers when interface compatibility is resolved
```

**Impact:** The LSP server starts but is **completely non-functional** - no diagnostics, no autocomplete, no hover.

**Quick Wins Available:** All backend code already exists! Just need to create handlers and register them.

**Estimated Effort:** 3-5 days to get working diagnostics, autocomplete, and hover!

---

### Day-by-Day Implementation Plan

#### Day 1: Create TextDocumentSyncHandler
**File:** `src/LanguageServer/Handlers/TextDocumentSyncHandler.cs`
- Wire up `DocumentManager.OpenDocument()`
- Wire up `DocumentManager.ChangeDocument()`
- Wire up `DocumentManager.CloseDocument()`
- Register with `.WithHandler<TextDocumentSyncHandler>()`

#### Day 2: Create DiagnosticsPublisher
**File:** `src/LanguageServer/Analysis/DiagnosticsPublisher.cs`
- Convert `SemanticError` to LSP `Diagnostic`
- Publish to client on document change
- Integrate into `DocumentManager.AnalyzeDocument()`

**Result:** ✅ Live error checking in IDE (red squiggles)

#### Day 3: Create CompletionHandler
**File:** `src/LanguageServer/Handlers/CompletionHandler.cs`
- Call `_compiler.GetCompletions()`
- Convert to LSP `CompletionItem`
- Set trigger characters: `.`, `:`, `(`, `<`

**Result:** ✅ Autocomplete works (Ctrl+Space)

#### Day 4: Create HoverHandler
**File:** `src/LanguageServer/Handlers/HoverHandler.cs`
- Call `_compiler.GetHoverInfo()`
- Convert to LSP `Hover` with Markdown
- Show type and documentation

**Result:** ✅ Hover tooltips work

#### Day 5: Register All Handlers
**File:** `src/LanguageServer/RazorForgeLSP.cs`
Replace line 78 TODO with:
```csharp
.WithHandler<TextDocumentSyncHandler>()
.WithHandler<CompletionHandler>()
.WithHandler<HoverHandler>()
.AddSingleton<DiagnosticsPublisher>()
```

#### Day 6-7: Test in VS Code
- Build LSP server
- Configure VS Code extension
- Test all features

**Success Criteria:** Working IDE experience with live errors, autocomplete, and tooltips!

---

## Current Implementation Status

### ✅ What Exists (Backend Services)

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

### ❌ What's Missing (LSP Handlers)

**Critical Issue:**
- ❌ **NO LSP HANDLERS REGISTERED** - Line 78 in `RazorForgeLSP.cs` shows a TODO
- This means the LSP server starts but **cannot respond to any client requests**

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
- ❌ Semantic tokens handler

### 🔴 Critical Blockers

1. **Handler Registration Issue:** Interface compatibility needs to be resolved to register handlers
2. **No Diagnostic Publishing:** Errors from compiler aren't shown to user
3. **Incomplete Position Tracking:** Symbol matching uses `.FirstOrDefault()` - imprecise column-based filtering
4. **Missing Context Awareness:** Completion and hover don't analyze cursor position in AST

---

## Implementation Phases

### Phase 1: Core LSP (1-2 weeks) - CRITICAL

**Goal:** Make the LSP server actually respond to requests

**Features:**
- Diagnostics (live error checking)
- Completion (autocomplete)
- Hover (documentation tooltips)
- Text document synchronization

**Tasks:**
1. Fix handler registration (resolve interface compatibility issue)
2. Implement `TextDocumentSyncHandler`
3. Implement `DiagnosticsPublisher`
4. Implement `CompletionHandler`
5. Implement `HoverHandler`

**Files:**
- `src/LanguageServer/RazorForgeLSP.cs` - remove TODO, register handlers
- `src/LanguageServer/Handlers/TextDocumentSyncHandler.cs` (new)
- `src/LanguageServer/Analysis/DiagnosticsPublisher.cs` (new)
- `src/LanguageServer/Handlers/CompletionHandler.cs` (new)
- `src/LanguageServer/Handlers/HoverHandler.cs` (new)

**Known Issues to Fix:**
- `GetCompletions()` doesn't use line/column parameters effectively
- `GetHoverInfo()` needs precise column-based symbol matching
- No scope-aware filtering (local vs global symbols)
- No type-aware member access completion (e.g., `entity.` shows entity fields)

---

### Phase 2: Navigation (2-3 weeks)

**Goal:** Enable go-to-definition, find references, document outline

**Features:**
- Go-to-definition
- Find references
- Document symbols (outline)
- Workspace symbols (global search)

**Tasks:**
1. Implement `DefinitionHandler`
2. Implement `DocumentSymbolHandler`
3. Implement symbol indexing for workspace
4. Implement `ReferencesHandler`
5. Implement `WorkspaceSymbolHandler`

**New Services Needed:**
- Symbol definition tracking
- Cross-file symbol resolution
- Position-to-symbol mapping
- Workspace-wide symbol indexing

---

### Phase 3: RazorForge-Specific Features (3-4 weeks)

**Goal:** Implement memory token tracking, function coloring, and danger block visualization

**Features:**
1. **Error Handling Category Visualization (4-Way)** 🔴 CRITICAL PRIORITY
   - Visually distinguish functions by error handling category
   - **Crash + Present** (`foo!() -> U`) - Red/orange (dangerous)
   - **Crash + Absent** (`foo!() -> U?`) - Red/orange with ? marker
   - **Check + Present** (`check_foo() -> Result<U>`) - Yellow (safe, verbose)
   - **Try + Absent** (`try_foo() -> U?`) - Blue/green (safe, simple)
   - Show auto-generated status in hover: "Auto-generated from foo!"
   - Diagnostic: Highlight `!` calls in non-`!` contexts
   - Quick fix: "Replace with try_foo()" or "Replace with check_foo()"
   - Makes error handling transparent at a glance

2. **Function/Method Permission Coloring (R/RW/RWM)** 🔴 HIGH PRIORITY
   - Visually distinguish methods by permission category
   - R (Read-only) - green/blue tint
   - RW (Writable) - yellow/orange tint
   - RWM (Migratable) - red tint
   - Makes memory model transparent at a glance

3. **Live Memory Token Tracking** 🔴 HIGH PRIORITY
   - Detect `.retain()`, `.share()`, `.track()`, `.consume()` calls
   - Visually mark variables as "invalidated" when tokens are created
   - Use `DiagnosticTag.Unnecessary` to grey out invalidated variables
   - Show token state in hover: "Valid", "Invalidated (retained at line 42)", etc.

4. **Danger Block Visualization** 🟡 MEDIUM PRIORITY
   - Render `danger!` blocks with distinct background color
   - Use semantic tokens to mark danger block ranges
   - Client-side styling via VS Code extension

5. **Migratable Operation Visualization**
   - Mark methods that can relocate DynamicSlice buffers as migratable
   - Visual indicator for migratable methods
   - Warning when calling migratable methods during iteration

**Tasks:**
1. Implement `ErrorHandlingCategorizer` (4-way crashable categorization)
2. Implement semantic tokens for error handling coloring (red/yellow/blue)
3. Implement diagnostics for crashable call violations
4. Implement quick fixes for crashable → try_/check_ replacement
5. Implement `FunctionCategorizer` (R/RW/RWM categorization)
6. Implement semantic tokens for permission coloring (R/RW/RWM)
7. Implement `MemoryTokenAnalyzer`
8. Publish invalidation diagnostics
9. Enhance hover with token state, method category, and error handling category
10. Implement semantic tokens for danger blocks
11. Implement migratable method markers
12. Enhanced error messages for migratable violations

**Dependencies:**
- Compiler analysis improvements:
  - Crashable function tracking (COMPILER_TODO.md #4) - CRITICAL for error handling visualization
  - Mutation inference (COMPILER_TODO.md #3) - required for R/RW categorization
  - Migratable detection - required for RWM categorization
  - DynamicSlice buffer relocation analysis
- VS Code extension for client-side features:
  - Semantic token rendering
  - Custom theme colors for R/RW/RWM methods
  - Diagnostic tag support

---

### Phase 4: Developer Experience Enhancements (2-3 weeks)

**Goal:** Code actions, formatting, signature help

**Features:**
- Code actions (quick fixes)
- Code formatting
- Signature help (parameter hints)
- Context-aware assists (one-click fixes)

**Tasks:**
1. Implement `CodeActionHandler` with quick fixes
2. Implement code formatter
3. Implement `SignatureHelpHandler`
4. Error-to-fix mapping
5. Code transformation utilities

**Vision Features:**
- One-click fixes for common errors (e.g., "Change to 'shared let doc ...'")
- Suggested fixes from compiler errors
- Format on save
- Parameter hints during function calls

---

## Reference Information

### File Locations

**Existing Files to Modify:**
- `src/LanguageServer/RazorForgeLSP.cs` - Register all LSP handlers
- `src/LanguageServer/DocumentManager.cs` - Integrate DiagnosticsPublisher
- `src/LanguageServer/RazorForgeCompilerService.cs` - Fix hover/completion, add definition/references
- `src/LanguageServer/IRazorForgeCompilerService.cs` - Add GetDefinition(), FindReferences()

**New Files to Create:**

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
├── ErrorHandlingCategorizer.cs (4-way crashable classification)
├── MemoryTokenAnalyzer.cs
├── MigratableAnalyzer.cs
├── FunctionCategorizer.cs (R/RW/RWM classification)
└── DocumentationParser.cs
```

**Utilities:**
```
src/LanguageServer/Utilities/
├── LspHelpers.cs (Range conversion, etc.)
└── SymbolIndex.cs (Workspace-wide symbol tracking)
```

---

### Architecture Notes

**TextDocumentSyncHandler Pattern:**
```csharp
public class TextDocumentSyncHandler : TextDocumentSyncHandlerBase
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
}
```

**DiagnosticsPublisher Pattern:**
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

**CompletionHandler Pattern:**
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
                InsertText = s.InsertText
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

**FunctionCategorizer (R/RW/RWM) Pattern:**
```csharp
public enum MethodCategory
{
    ReadOnly,      // R - doesn't mutate me
    Writable,      // RW - mutates me, but not migratable
    Migratable     // RWM - can relocate DynamicSlice buffers
}

public class FunctionCategorizer
{
    private readonly WritableAnalyzer _writableAnalyzer;
    private readonly MigratableAnalyzer _migratableAnalyzer;

    public MethodCategory CategorizeMethod(RoutineDeclaration routine)
    {
        // Check migratable first (most specific)
        if (_migratableAnalyzer.CanRelocateBuffer(routine))
            return MethodCategory.Migratable;

        // Check if writable
        if (_writableAnalyzer.IsWritable(routine))
            return MethodCategory.Writable;

        // Default to read-only
        return MethodCategory.ReadOnly;
    }
}
```

**MemoryTokenAnalyzer Pattern:**
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

---

### Testing Requirements

**Unit Tests Needed:**
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

4. **Function Categorizer Tests:**
   - R/RW/RWM classification accuracy
   - Migratable method detection
   - Writable method analysis integration

**Integration Tests Needed:**
1. **LSP Protocol Tests:**
   - Handler registration
   - Request/response flow
   - Error handling

2. **End-to-End Tests:**
   - Full document editing workflow
   - Multi-file navigation
   - Diagnostic publishing
   - Semantic token rendering

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
- Compiler analysis improvements (mutation inference, migratable detection)
- VS Code extension for client-side features (semantic tokens, custom theme colors)
