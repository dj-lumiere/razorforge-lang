# RazorForge

**Make programming sharp again.**

![License](https://img.shields.io/badge/license-MIT%20%7C%20Apache--2.0-blue.svg)
![Status](https://img.shields.io/badge/status-early%20alpha-orange.svg)

RazorForge is a natively compiled, statically typed programming language built around two ideas:

1. **Single-ownership memory management without a borrow checker** — containment is ownership,
   transfers are explicit (`steal`), borrows are scoped — so you get deterministic cleanup and
   use-after-move rejection without lifetime annotations or a garbage collector.
2. **Compiler-generated error handling** — write one failable routine (`routine parse!(...)`),
   and the compiler derives `try_parse` (returns `Maybe[T]`), `check_parse` (returns `Result[T]`),
   and `lookup_parse` variants for every calling style. No exceptions, no boilerplate.

It compiles to native code through LLVM and runs on Windows x86-64 and Linux x86-64 today.
It is aimed at **performance-minded application work** — CLIs, services, games, data tools —
where you want native speed and deterministic memory behavior. It is *not* a kernel/bare-metal
language: programs link against the RazorForge runtime library, and there is no freestanding mode.

> **Honesty first:** RazorForge is an early alpha. The compiler, runtime, and standard library
> work — 1,400+ unit tests and 140+ end-to-end snapshot fixtures run green in CI on every commit —
> but the language is young, APIs will change, and you will find bugs. Its sibling language
> **Suflae** is a *design* (see [Suflae status](#suflae-design-preview)) and is **not implemented yet**.

---

## A Taste of RazorForge

### Failable routines and generated variants

```razorforge
module Demo
import IO/Console

# `!` marks a failable routine. The compiler auto-generates try_/check_/lookup_
# variants, so callers choose how to consume failure — no exceptions involved.
routine get_text!(n: S64) -> Text
  when n
    == 0 => throw DivisionByZeroError()
    == 1 => return "hello"
    else => return "world"

routine start()
  var m = try_get_text(n: 0)   # generated variant -> Maybe[Text]
  when m
    is None => show("absent")
    else v  => show(f"present: {v}")
  return
```

### Ownership is explicit

```razorforge
entity Resource
  tag: S64

routine consume(r: ?Resource)
  show(f"consuming tag={r.tag}")
  return
  # r's $destroy fires here — exactly once, deterministically

routine start()
  var b = Resource(tag: 7)
  consume(r: steal b)   # ownership transferred; using `b` afterwards is a compile error
  return
```

- Entities have a single owner; assignment of an entity requires `steal` (error `RF-S413` otherwise).
- Scoped borrows (`view`/`modify`, `Hijacked[T]`) express intent without lifetime syntax.
- Reference counting (`Retained[T]`, `Tracked[T]`) is available when you opt into sharing.

### Calls are readable by default

```razorforge
# Multi-parameter calls require named arguments — call sites document themselves.
var g = gcd(a: 252, b: 105)
discard seen.add(value: v)   # ignoring a return value is explicit, too
```

---

## Quick Start

### Prerequisites

- .NET SDK 10.0+
- LLVM 20+ (`clang` and `opt` on PATH)
- CMake 3.20+ and a C compiler (for the native runtime)

### Build from source

```bash
git clone https://github.com/dj-lumiere/razorforge-suflae.git
cd razorforge-suflae

dotnet build        # builds the compiler AND the native runtime (via CMake)
dotnet test         # optional: run the test suite
```

### Hello, world

```razorforge
# hello.rf
module Hello
import IO/Console

routine start()
  show("Hello from RazorForge!")
  return
```

```bash
./bin/Debug/net10.0/RazorForge buildandrun hello.rf
# Windows: .\bin\Debug\net10.0\RazorForge.exe buildandrun hello.rf
```

### Using an AI assistant?

RazorForge isn't in any model's training data yet — assistants will guess
Rust/Python-flavored syntax that doesn't compile. Point yours at
[`RAZORFORGE-FOR-AI.md`](RAZORFORGE-FOR-AI.md) (also shipped inside every
release package): a compact reference of exactly where their assumptions
break, plus pointers to the 140+ CI-verified example programs in
[`tests/Fixtures/Stdlib/`](tests/Fixtures/Stdlib/).

### CLI reference

```
RazorForge <source-file>                  Parse file and show AST summary
RazorForge parse <source-file>            Parse file and show AST summary
RazorForge tokenize <source-file>         Tokenize file and show tokens
RazorForge codegen <source-file> [out.ll] Generate LLVM IR (single file)
RazorForge build [entry-file] [out.ll]    Build a multi-file project
RazorForge buildandrun [entry-file]       Build, link, and execute
RazorForge check [entry-file]             Type-check only (no codegen)
RazorForge validate-stdlib [rf]           Validate stdlib routine bodies
RazorForge help                           Show usage
RazorForge version                        Show compiler version
```

**There are no build flags.** All build configuration lives in the
`razorforge.toml` manifest's single `[target]` section:

```toml
[package]
name = "my-app"

[target]
executable = "MainModule"          # entry module (by `module` declaration, not file path)
library = ["../shared-utils"]      # external dependency directories (optional)
mode = "debug"                     # debug -O0 | release -O2 | release-time -O3 | release-space -Os
```

`library` entries are directories — relative to the manifest — whose modules join
the import search space, requirements.txt-style. (When the package manager lands,
entries will also accept versioned packages from the package site, e.g.
`"json-utils@1.2.0"`, fetched into a cache that builds consume the same way.)
With no entry file given, the CLI searches the current and parent directories for
`razorforge.toml` — `cd` into a project and `razorforge buildandrun` just works.

---

## What Works Today

- **Compiler pipeline**: lexer → parser → semantic analysis (350+ structured diagnostics with
  `error[RF-S###]: file:line:col` format) → desugaring/monomorphization → LLVM IR → native binary.
- **Memory model**: single-ownership entities with deterministic `$destroy`, explicit `steal`
  transfer, scoped borrows, `Retained`/`Tracked` reference counting, `danger` blocks for
  opt-in unsafe operations.
- **Error handling**: failable routines (`!`), `throw`/`absent`, generated `try_`/`check_`/`lookup_`
  variants, `Maybe[T]`/`Result[T]`/`Lookup[T]` carriers, `when` pattern matching.
- **Numerics**: `S8`–`S128`, `U8`–`U128`, `F16`–`F128`, decimal `D32`/`D64`/`D128`, arbitrary
  precision `Integer`/`Decimal`, complex numbers — with checked, wrapping, clamping, and
  overflow-reporting arithmetic variants.
- **Collections**: `List`, `Dict`, `Set`, `CircularList`, `BitList`, sorted collections, fixed-size
  `Array[T, N]`, iterator adapters (`select`, `where`, `zip`, `enumerate`, …).
- **Text**: UTF-32 `Text` type, f-string interpolation with format specs, `Bytes` with UTF-8
  iteration.
- **Interop**: `external("C")` declarations for calling C from RazorForge.
- **Generics**: type parameters with protocol constraints, const generics (`Array[T, N]`),
  monomorphization.
- **Concurrency**: stackful coroutines (`suspended routine`) and OS threads (`threaded routine`)
  behind one `Agent[T]` handle with an *uncolored* `retrieve!`; cooperative `waitfor`, `race!`
  (first finisher) and `gather!` (wait for all); abandon-on-drop teardown; and async file I/O
  (`read_text`/`write_text`) that parks a coroutine while it waits. *Async networking
  (sockets/HTTP) is not implemented yet.*
- **Quality gates**: every commit runs 1,400+ unit tests plus 143 end-to-end fixtures that
  compile, link, execute, and diff program output against snapshots — on Linux CI; Windows is
  exercised continuously during development.

### Platform support

| Platform       | Status                                       |
|----------------|----------------------------------------------|
| Windows x86-64 | Working (primary development platform)       |
| Linux x86-64   | Working (CI-verified on every commit)        |
| macOS / ARM64  | Target definitions exist, **not yet tested** |

---

## Suflae (design preview)

Suflae is RazorForge's planned sibling: same type system and standard library surface, but
garbage-collected, with arbitrary-precision numerics by default and an **actor model** — every
entity an actor with a mailbox, built on RazorForge's coroutine runtime (the `suspended`/`threaded`
primitives already live in RazorForge; Suflae adds actors and GC on top). The grammar is designed
and partially parsed, and the docs at [suflae.lumi-dev.xyz](https://suflae.lumi-dev.xyz/) describe
the design — **but there is no Suflae standard library or runtime yet, and Suflae programs cannot
run.** It will land after RazorForge stabilizes.

---

## Documentation

Full documentation lives at [razorforge.lumi-dev.xyz](https://razorforge.lumi-dev.xyz/)
(sources in `RazorForge-Wiki/`):

- [Hello World](https://razorforge.lumi-dev.xyz/Hello-World) ·
  [Data Types](https://razorforge.lumi-dev.xyz/Data-Types) ·
  [Pattern Matching](https://razorforge.lumi-dev.xyz/Pattern-Matching)
- [Memory Model](https://razorforge.lumi-dev.xyz/Memory-Model) — ownership, borrows, `steal`
- [Error Handling](https://razorforge.lumi-dev.xyz/Error-Handling) — failable routines and carriers
- [Collections](https://razorforge.lumi-dev.xyz/Collections) ·
  [Numeric Types](https://razorforge.lumi-dev.xyz/Numeric-Types) ·
  [Generics](https://razorforge.lumi-dev.xyz/Generics) ·
  [Protocols](https://razorforge.lumi-dev.xyz/Protocols)
- [Danger Blocks](https://razorforge.lumi-dev.xyz/Danger-Blocks) ·
  [C Subsystem](https://razorforge.lumi-dev.xyz/C-Subsystem) ·
  [Build System](https://razorforge.lumi-dev.xyz/Build-System)

The committed end-to-end fixtures in `tests/Fixtures/Stdlib/*.rf` double as a runnable,
always-green example gallery for nearly every language feature.

---

## Project Structure

```
RazorForge/
├── src/                 # Compiler (C#)
│   ├── Parser/          #   Lexing + parsing (RazorForge & Suflae grammars)
│   ├── Verification/    #   Semantic analysis & diagnostics
│   ├── Synthesis/       #   Wired routines, derived operators, variant generation
│   ├── Desugaring/      #   Global lowering passes
│   ├── Instantiation/   #   Generic monomorphization
│   ├── Postprocessing/  #   Type-aware lowering (operators, f-strings, folding)
│   ├── CodeGen/         #   LLVM IR emission
│   └── Execution/       #   CLI driver, build pipeline (opt + clang + run)
├── Standard/RazorForge/ # Standard library (.rf sources)
├── native/              # C runtime + vendored libs (decNumber, libbf, zstd, …)
├── tests/               # Unit tests + end-to-end snapshot fixtures
├── RazorForge-Wiki/     # Documentation sources
└── Suflae-Wiki/         # Suflae design documentation
```

---

## Roadmap

### Shipped (latest release: v0.1.1)

- [x] Working compiler → LLVM → native pipeline on Windows/Linux x86-64
- [x] Ownership model, failable-routine variants, generics, collections, 128-bit numerics
- [x] Fully green CI: unit tests + end-to-end output-snapshot fixtures
- [x] Diagnostics polish: source excerpts with carets, "did you mean" suggestions
- [x] Prebuilt release packages (win-x64, linux-x64)

### In development (v0.2.0)

- [x] Concurrency: coroutines + threads behind `Agent[T]`, `retrieve!`/`race!`/`gather!`/`waitfor`,
  abandon-on-drop teardown, async file I/O

### Next

- Async networking (TCP → HTTP), then higher-level protocols
- Channels and structured concurrency
- Suflae standard library and runtime (actor model + GC)
- Language server (LSP) and editor tooling beyond syntax highlighting
- macOS and ARM64 support
- Package management story

### Future

- Self-hosting compiler
- Native debug info (DWARF/PDB)
- WASM backend

---

## Philosophy

- **Total development cost** over raw runtime performance
- **Clear, descriptive words** over obscure historical terms
- **Explicit** over implicit — ownership transfers, named arguments, discarded returns
- **Honesty** over marketing hype — including in this README

Read more: [Design Philosophy](https://razorforge.lumi-dev.xyz/Design-Philosophy)

---

## Contributing

Bug reports, feature suggestions, documentation improvements, and code contributions are all
welcome. The best place to start is running the fixture suite (`dotnet test`) and reading
`tests/Fixtures/Stdlib/` for live examples of the language.

## License

Dual-licensed under MIT and Apache-2.0 — choose either.

## Community

- **GitHub**: [github.com/dj-lumiere/razorforge-suflae](https://github.com/dj-lumiere/razorforge-suflae)
- **Issues**: [Report bugs or request features](https://github.com/dj-lumiere/razorforge-suflae/issues)
- **Docs
  **: [razorforge.lumi-dev.xyz](https://razorforge.lumi-dev.xyz/) · [suflae.lumi-dev.xyz](https://suflae.lumi-dev.xyz/)

## Acknowledgments

RazorForge is inspired by **Rust** (memory safety without GC), **Python** (readability),
**Zig** (explicit control), **C#** (tooling), and **Swift/Erlang** (the actor model that
will power Suflae).

---

*"Make programming sharp again."*
