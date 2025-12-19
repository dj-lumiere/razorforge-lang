# RazorForge Compiler Overhaul Plan

## Current State Assessment

### What's Worth Preserving

- **Lexer/Parser** - Stable and working well
- **AST node definitions** - Mostly solid
- **The stdlib `.rf` files** - They define the language semantics

### What Needs Rethinking

#### 1. Semantic Analysis

- Needs a proper type system model (not just string matching)
- Generic instantiation should happen here, not scattered into codegen
- Should produce a fully typed, resolved AST

#### 2. Code Generation

- Should receive a fully typed AST (no type guessing)
- Needs a clear model for RazorForge types → LLVM types
- Consider an intermediate step (Typed IR → LLVM IR)

#### 3. LSP

- Should share the semantic analysis infrastructure
- Incremental parsing/analysis for responsiveness

---

## Proposed Architecture

```
Source → Lexer → Tokens → Parser → AST
                                    ↓
                         Semantic Analyzer
                         (type checking, generic instantiation,
                          symbol resolution)
                                    ↓
                              Typed AST / IR
                              ↙         ↘
                      LLVM CodeGen      LSP Services
```

**Key Insight:** Semantic Analysis should do the heavy lifting so CodeGen just translates a fully-resolved
representation.

---

## Current Problems in Detail

### Fragmented Type Tracking

Types are tracked in `_tempTypes`, `_symbolTypes`, `_recordFields`, `_functionParameters`, etc. with no unified model.
Every operation needs to check multiple places.

### No First-Class Understanding of Record Wrappers

The "single-field record = value type wrapper" pattern is central to RazorForge, but the code generator treats it as
special cases scattered throughout (extract here, wrap there, check if it's a struct vs primitive...).

### Generic Instantiation is Bolted On

Type substitution happens in some places but not others. The `_currentTypeSubstitutions` field is passed around
inconsistently.

### Intrinsics Have No Type Context

Each intrinsic handler has to manually figure out types, extract primitives, wrap results. There's no shared
infrastructure.

---

## Symbol Resolution is Broken

This is perhaps the core problem. The compiler has no unified understanding of "what exists and what type is it."

### Type Detection Issues

- Types are sometimes `"s32"`, sometimes `"%s32"`, sometimes `"Atomic<T>"`, sometimes `"Atomic_s32"`
- No canonical representation - string matching everywhere
- Generic instantiation (`Atomic<T>` → `Atomic_s32`) happens ad-hoc in multiple places
- The "single-field record wrapper" concept is detected by checking `StartsWith("%")` - not first-class
- Types like `Maybe_uaddr` are referenced but never generated

### Routine Detection Issues

- Method lookup fails: `"Atomic_s32.fetch_add"` not found
- Mangling is inconsistent: `Atomic<T>.fetch_add` → `Atomic_T.fetch_add_s32` (confusing)
- Extension methods, constructors (`__create__`), and regular methods handled differently
- No registry of "what methods exist for what types"

### Variable Detection Issues

- Variables tracked in `_symbolTypes` but lookups fail for parameters
- `"overflow"` identifier used in stdlib but not found in symbol table
- Temp variables (`%tmp1234`) tracked separately from named variables
- No scope hierarchy - just flat dictionaries

### Field Detection Issues

- Field access on generic types fails
- `_recordFields` doesn't have instantiated generic records
- No understanding of "this type has these fields"

### Root Cause

There's no unified **Symbol Table** or **Type Registry** that understands:

- What entities exist (types, functions, variables, fields)
- Their relationships (method belongs to type, field belongs to record)
- Their fully-resolved types (after generic instantiation)
- Scope hierarchy (what's visible where)

Instead: multiple dictionaries (`_tempTypes`, `_symbolTypes`, `_recordFields`, `_functionParameters`,
`_registeredRecords`, etc.) with ad-hoc lookups scattered throughout.

### What's Needed

A proper **Type System** that reflects RazorForge's actual design.

#### Key Insight: No User-Visible Primitives

RazorForge/Suflae have **no primitive types** from the user's perspective. Everything is one of these type categories:

| Type       | Semantics              | Size    | Reassignable | Purpose                                  |
|------------|------------------------|---------|--------------|------------------------------------------|
| `record`   | Value (copy on assign) | Fixed   | Yes (`var`)  | Simple values, wrappers                  |
| `entity`   | Reference              | Dynamic | **No**       | Complex objects, heap-allocated          |
| `resident` | Reference + Fixed size | Fixed   | **No**       | Global state, embedded (RazorForge only) |
| `choice`   | Value                  | Fixed   | Yes (`var`)  | Simple enumeration, CAN have methods     |
| `variant`  | Value                  | Fixed   | **No**       | Tagged union, local-only, NO methods     |
| `mutant`   | Value                  | Fixed   | Yes (`var`)  | Untagged union (danger zone only)        |
| `protocol` | N/A                    | N/A     | N/A          | Interface/trait definition               |

**Note:** `entity` and `resident` variables cannot be reassigned - they have stable identity.

What looks like "primitives" (`s32`, `bool`, `uaddr`) are actually **single-field records** wrapping LLVM native types
via `@intrinsic.*`:

```razorforge
record s32 { value: @intrinsic.i32 }
record bool { value: @intrinsic.i1 }
record uaddr { value: @intrinsic.uptr }  # pointer-sized unsigned
record saddr { value: @intrinsic.iptr }  # pointer-sized signed
record f64 { value: @intrinsic.f64 }
```

#### LLVM Intrinsic Types (`@intrinsic.*`)

**Integers:**
| Intrinsic | Size | Purpose |
|-----------|------|---------|
| `@intrinsic.i1` | 1 bit | Boolean |
| `@intrinsic.i8` | 8 bit | s8, u8, letter8 |
| `@intrinsic.i16` | 16 bit | s16, u16, letter16 |
| `@intrinsic.i32` | 32 bit | s32, u32, letter32 |
| `@intrinsic.i64` | 64 bit | s64, u64 |
| `@intrinsic.i128` | 128 bit | s128, u128 |
| `@intrinsic.iptr` | Pointer-sized (signed) | saddr |
| `@intrinsic.uptr` | Pointer-sized (unsigned) | uaddr |

**Floats:**
| Intrinsic | Size | Purpose |
|-----------|------|---------|
| `@intrinsic.f16` | 16 bit | f16 (half precision) |
| `@intrinsic.f32` | 32 bit | f32 (single precision) |
| `@intrinsic.f64` | 64 bit | f64 (double precision) |
| `@intrinsic.f128` | 128 bit | f128 (quad precision) |

**Portability:** `iptr`/`uptr` adapt to target (32-bit on WASM32, 64-bit on x64). LLVM handles lowering for unsupported
types (i128, f16, f128 on WASM).

These are **internal implementation details**, never directly exposed to users - only used inside stdlib record
definitions.

#### Error Handling Types (Compiler-Generated Variants)

These are special variants with compiler magic:

| Type              | Purpose                            | Created By                           |
|-------------------|------------------------------------|--------------------------------------|
| `Maybe<T>` / `T?` | Optional value (None or value)     | `absent` only in `!` function        |
| `Result<T>`       | Error or value                     | `throw` only in `!` function         |
| `Lookup<T>`       | Three-way: error, absent, or value | `throw` AND `absent` in `!` function |

Compiler generates safe variants from `!` functions:

- `foo!()` with `absent` only → generates `try_foo()` returning `T?`
- `foo!()` with `throw` only → generates `check_foo()` returning `Result<T>` + `try_foo()` returning `T?`
- `foo!()` with both → generates `find_foo()` returning `Lookup<T>` + `try_foo()` returning `T?`

#### Memory Spaces (RazorForge Only)

| Space        | Traditional | Purpose                         |
|--------------|-------------|---------------------------------|
| `temporary`  | Stack       | Auto-cleanup, function lifetime |
| `dynamic`    | Heap        | Manual lifetime, entities       |
| `persistent` | Static/BSS  | Program lifetime, residents     |

#### Handles vs Tokens (RazorForge Only)

**Handles (storable, owning):**

- Direct entity ownership
- `Shared<T>` / `Shared<T, Policy>` - reference counted
- `Tracked<T>` - weak reference
- `Snatched<T>` - raw pointer (danger zone)

**Tokens (temporary, non-storable):**

- `Viewed<T>` - read-only access (`.view()`)
- `Hijacked<T>` - exclusive access (`.hijack()`)
- `Inspected<T>` - read lock (multi-threaded)
- `Seized<T>` - write lock (multi-threaded)

#### Suflae Differences

- No `resident` keyword
- No manual memory management (garbage collected)
- `Shared<T>` exists but is **actor-based** (multi-threaded message passing), not reference counting
- Uses `.share()` to create actor handles for concurrent communication
- Same `record`, `entity`, `choice`, `variant` type categories
- No `Viewed`, `Hijacked`, `Snatched` etc. (no borrow checking - GC handles it)

#### Proposed Type Registry

```
TypeRegistry
├── Intrinsic types (internal only - never exposed to user code)
│   └── i8, i16, i32, i64, i128, f16, f32, f64, f128, i1, ptr
│
├── Records (value semantics, copy on assign)
│   ├── s32: { fields: [{value: @llvm.i32}], isSingleField: true, llvmType: "i32" }
│   ├── bool: { fields: [{value: @llvm.i1}], isSingleField: true, llvmType: "i1" }
│   ├── Point: { fields: [{x: s32}, {y: s32}], isSingleField: false }
│   └── Atomic<T>: { generic: true, fields: [{value: T}] }
│       └── instantiations: {Atomic_s32: {T → s32, fields resolved}}
│
├── Entities (reference semantics, heap-allocated)
│   ├── Document: { fields: [...], methods: [...] }
│   └── User<T>: { generic: true, ... }
│
├── Residents (reference semantics, fixed size - RazorForge only)
│   └── SystemLogger: { fields: [{log_count: s32}], fixedSize: 4 }
│
├── Choices (enumeration, CAN have methods)
│   │   # SCREAMING_SNAKE_CASE
│   │   # Numbering: all-or-nothing (either all have values or none)
│   │   # choice Direction { NORTH, SOUTH, EAST, WEST }
│   │   # choice HttpStatus { OK: 200, NOT_FOUND: 404, ERROR: 500 }
│   ├── Direction: { cases: [NORTH, SOUTH, EAST, WEST] }
│   └── HttpStatus: { cases: {OK: 200, NOT_FOUND: 404, ERROR: 500} }
│
├── Variants (tagged union, local-only, NO methods)
│   │   # SCREAMING_SNAKE_CASE, payload is single type (not tuple-like)
│   │   # variant Event { CONNECT, DISCONNECT, DATA: Text }
│   ├── ParseResult: { cases: {SUCCESS: s32, ERROR: Text} }
│   └── NetworkEvent: { cases: [CONNECT, DISCONNECT, {DATA: Text}] }
│
├── Mutants (untagged union, danger zone only, NO methods)
│   │   # Same syntax as variant (payload is single type)
│   └── RawData: { cases: {INT: s32, FLOAT: f32} }
│
├── BuiltinErrorTypes (compiler-special, NOT regular variants)
│   ├── Maybe<T>: pattern matches with `is None` or `else value`
│   ├── Result<T>: pattern matches with `is Crashable e` or `else value`
│   └── Lookup<T>: pattern matches with `is Crashable e`, `is None`, or `else value`
│
├── Protocols (interfaces)
│   ├── Crashable: { methods: [message() -> Text] }
│   └── Iterable<T>: { methods: [iterate() -> Iterator<T>] }
│
├── Functions
│   ├── global functions
│   ├── type methods (owner + name)
│   ├── extension methods
│   ├── constructors (__create__)
│   └── generic instantiations
│
├── Handles & Tokens (RazorForge only)
│   ├── Shared<T>, Shared<T, Policy>
│   ├── Tracked<T>, Snatched<T>
│   └── Viewed<T>, Hijacked<T>, Inspected<T>, Seized<T>
│
└── Scopes
    ├── global scope
    ├── module scopes
    └── function scopes → local variables with resolved types
```

**The semantic analyzer builds this completely.** The code generator:

1. Queries it for type info - never guesses
2. Single-field records → knows to extract/insert for LLVM ops
3. Generic types → already instantiated with concrete types
4. All symbols resolved with full type information

---

## Next Steps

### Phase 1: Design the Type System Infrastructure

1. Design the `TypeRegistry` class structure
2. Define how types are represented (records, entities, residents, choices, variants, protocols)
3. Define how generic types and their instantiations are tracked
4. Define how functions/methods are registered and looked up

### Phase 2: Rewrite Semantic Analyzer

1. Build the `TypeRegistry` during semantic analysis
2. Resolve all types completely (no string matching)
3. Instantiate all generic types used in the program
4. Produce a fully-typed AST where every expression has a resolved type
5. Generate error handling variants (`try_`, `check_`, `find_`) from `!` functions

### Phase 3: Rewrite Code Generator

1. Consume the fully-typed AST - no type guessing
2. Query `TypeRegistry` for all type information
3. Handle single-field records uniformly (extract/insert pattern)
4. Support both RazorForge and Suflae from the same typed AST

### Phase 4: LSP Integration

1. Reuse `TypeRegistry` for hover info, completions, go-to-definition
2. Incremental parsing and analysis for responsiveness
3. Share infrastructure with compiler

### Key Principle

**Semantic analysis does the hard work. Code generation is mechanical translation.**

---

## Record/Entity Destructuring

Destructuring works on records and entities with **all public fields**. Three syntaxes are supported:

```razorforge
record Circle {
    center: Point
    radius: f64
}

record Point {
    x: f64
    y: f64
}

# 1. Field-name matching (variable name = field name)
let (center, radius) = Circle(Point(5, 6), 7)

# 2. Aliased destructuring (field_name: alias)
let (center: c, radius: r) = Circle(Point(5, 6), 7)

# 3. Nested destructuring
let ((x, y), radius) = Circle(Point(5, 6), 7)
```

This also works in pattern matching:

```razorforge
variant Shape {
    CIRCLE: Circle
    RECTANGLE: Rectangle
}

when shape {
    is CIRCLE (center, radius) => { ... }           # Field-name matching
    is CIRCLE (center: c, radius: r) => { ... }     # Aliased
    is CIRCLE ((x, y), radius) => { ... }           # Nested
}
```

**Requirement:** ALL fields must be public for destructuring to work.

---

## Wiki Reference by Compiler Phase

The Lexer and Parser are stable and working well. The following wiki files are relevant for the phases that need work.

### Semantic Analysis (Type Checking & Validation)

These documents define type rules and semantic constraints:

#### Core Type System

| Wiki File                         | Priority | Key Validation Rules                                   |
|-----------------------------------|----------|--------------------------------------------------------|
| `RazorForge-Compiler-Analysis.md` | HIGH     | Mutation inference, migratable inference, error gen    |
| `Generics.md`                     | HIGH     | Generic instantiation, constraint validation           |
| `RazorForge-Data-Types.md`        | HIGH     | Type categories: record/entity/resident/choice/variant |
| `Suflae-Data-Types.md`            | MEDIUM   | Suflae type system differences                         |

#### Memory Model Validation

| Wiki File                           | Priority | Key Validation Rules                               |
|-------------------------------------|----------|----------------------------------------------------|
| `RazorForge-Memory-Model.md`        | HIGH     | Tokens (Viewed/Hijacked), handles (Shared/Tracked) |
| `RazorForge-Memory-Access-Rules.md` | HIGH     | Token cannot return, borrow checking               |
| `RazorForge-Handles-vs-Tokens.md`   | HIGH     | Handle vs Token semantics                          |
| `Memory-Slice-Implementation.md`    | MEDIUM   | DynamicSlice/TemporarySlice implementation         |
| `Resident-vs-Eternal.md`            | MEDIUM   | Resident vs eternal lifetime rules                 |
| `Suflae-Memory-Model.md`            | MEDIUM   | Suflae GC-based memory model                       |

#### Access Control

| Wiki File             | Priority | Key Validation Rules                                  |
|-----------------------|----------|-------------------------------------------------------|
| `Access-Modifiers.md` | HIGH     | public/private/family/internal visibility enforcement |

### Code Generation (LLVM IR)

These documents define runtime behavior and implementation details:

#### Core Types and Prelude

| Wiki File            | Priority | Implementation Details                                         |
|----------------------|----------|----------------------------------------------------------------|
| `RazorForge-Core.md` | HIGH     | s8-s128, u8-u128, f16-f128, d32-d128, bool, DynamicSlice, cstr |
| `Suflae-Core.md`     | MEDIUM   | Integer, Decimal, Fraction, Text, List - arbitrary precision   |
| `Numeric-Types.md`   | HIGH     | Integer/float representation, overflow behavior                |

#### Collections and Containers

| Wiki File                                | Priority | Implementation Details                        |
|------------------------------------------|----------|-----------------------------------------------|
| `RazorForge-Collections.md`              | HIGH     | List, Dict, Set, Deque, PriorityQueue codegen |
| `RazorForge-Containers-And-Iterators.md` | HIGH     | Iterator protocol, R/RW/RWM permissions       |
| `Suflae-Collections.md`                  | MEDIUM   | Suflae collections                            |
| `Itertools.md`                           | LOW      | Iterator utilities                            |

#### Text and I/O

| Wiki File            | Priority | Implementation Details                      |
|----------------------|----------|---------------------------------------------|
| `RazorForge-Text.md` | HIGH     | Text<letter8/16/32>, ValueText codegen      |
| `Console-IO.md`      | HIGH     | show, show_line, get_line, get_word codegen |
| `File-IO.md`         | MEDIUM   | File operations codegen                     |
| `Suflae-Text.md`     | MEDIUM   | Suflae text handling                        |

#### Unsafe and FFI

| Wiki File                     | Priority | Implementation Details                       |
|-------------------------------|----------|----------------------------------------------|
| `RazorForge-Danger-Blocks.md` | HIGH     | danger!, `@intrinsic.*`, `@native.*` codegen |
| `RazorForge-C-Subsystem.md`   | HIGH     | imported routines, C FFI, cstr/cint types    |

#### Concurrency

| Wiki File                              | Priority | Implementation Details                        |
|----------------------------------------|----------|-----------------------------------------------|
| `RazorForge-Concurrency-Model.md`      | HIGH     | Shared<T, Policy>, inspecting/seizing codegen |
| `RazorForge-Concurrency-Shared.md`     | HIGH     | Multi-threaded Shared handle implementation   |
| `RazorForge-Concurrency-Primitives.md` | MEDIUM   | Mutex, MultiReadLock, RejectEdit policies     |
| `RazorForge-Concurrency-Patterns.md`   | MEDIUM   | Common concurrency patterns                   |
| `RazorForge-Advanced-Locking.md`       | LOW      | Advanced locking strategies                   |
| `RazorForge-Lock-Policies.md`          | LOW      | Custom lock policy implementation             |
| `Suflae-Concurrency-Model.md`          | MEDIUM   | Suflae actor-based concurrency                |

#### Runtime and Miscellaneous

| Wiki File              | Priority | Implementation Details                               |
|------------------------|----------|------------------------------------------------------|
| `Runtime.md`           | HIGH     | Runtime initialization, memory allocation strategies |
| `DateTime-Duration.md` | MEDIUM   | Duration type implementation                         |

### Reference and Design (Not Directly Compiler-Related)

These documents provide context and style guidance but don't require compiler implementation:

| Wiki File                         | Category   | Purpose                           |
|-----------------------------------|------------|-----------------------------------|
| `Home.md`                         | Reference  | Wiki home page                    |
| `_Sidebar.md`                     | Reference  | Wiki navigation                   |
| `FAQ.md`                          | Reference  | Frequently asked questions        |
| `RazorForge-Design-Philosophy.md` | Philosophy | Language design rationale         |
| `Philosophy.md`                   | Philosophy | Overall project philosophy        |
| `Choosing-Language.md`            | Reference  | RazorForge vs Suflae comparison   |
| `RazorForge-Code-Style.md`        | Style      | Code formatting guidelines        |
| `Suflae-Code-Style.md`            | Style      | Suflae formatting guidelines      |
| `Hello-World.md`                  | Tutorial   | Getting started example           |
| `IDE-Support.md`                  | Tooling    | IDE/editor configuration          |
| `Build-System.md`                 | Tooling    | Build configuration and options   |
| `Standard-Libraries.md`           | Reference  | Standard library overview         |
| `RazorForge-Freestanding-Mode.md` | Advanced   | Bare-metal/embedded configuration |
| `RazorForge-Reality-Bender.md`    | Advanced   | Metaprogramming features          |
| `Suflae-Eternal.md`               | Advanced   | Suflae eternal objects            |

---

## Implementation Checklist

Check off features as they are fully implemented:

### Semantic Analysis Checklist

- [ ] Type category validation (record/entity/resident/choice/variant)
- [ ] Mutation inference (RazorForge-Compiler-Analysis.md)
- [ ] Migratable inference (RazorForge-Compiler-Analysis.md)
- [ ] Generic instantiation and constraint checking
- [ ] Memory access validation (tokens/handles)
- [ ] Access modifier enforcement
- [ ] Error variant generation (try_/check_/find_)

### Code Generation Checklist

- [ ] Core types (s8-s128, u8-u128, f16-f128, d32-d128, bool)
- [ ] Collections (List, Dict, Set, etc.)
- [ ] Text types (Text<T>, ValueText<T, N>)
- [ ] I/O operations (show, show_line, get_line)
- [ ] FFI (imported routines, @native.*, @intrinsic.*)
- [ ] Concurrency (Shared, inspecting/seizing)
- [ ] Runtime initialization

---

## Documentation Review Needed

Some wiki documentation contains outdated information that needs updating alongside the overhaul:

| Document                   | Issue                                         |
|----------------------------|-----------------------------------------------|
| `mutant` docs              | Outdated data structure, needs heavy revision |
| (add others as discovered) |                                               |

These should be updated after the type system is finalized to ensure consistency.
