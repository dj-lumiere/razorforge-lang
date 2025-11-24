# RazorForge & Suflae

**Two languages. One family. From sweet to sharp, seamlessly.**

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Status](https://img.shields.io/badge/status-in%20development-orange.svg)

---

## What Are RazorForge and Suflae?

RazorForge and Suflae are modern programming languages designed to provide a smooth gradient from high-productivity application development to high-performance systems programming.

### ⚔️ RazorForge: "Make Programming Sharp Again"

A precision systems language for absolute control and deterministic performance.

```razorforge
recipe main() -> s32:
    # Explicit memory control with theatrical tokens
    let data = Vector<s32>()
    data.push(42)

    # Share ownership with reference counting
    let shared = data.share!()

    # Hijack exclusive access
    hijacking borrowed from shared:
        borrowed.push(100)

    return 0
```

**Use RazorForge for:**
- Systems programming
- Game engines and real-time applications
- Embedded systems and firmware
- Performance-critical hot paths
- Manual memory control

### 🍮 Suflae: "Make Programming Sweet Again"

A productivity-first language for building modern applications quickly and safely.

```suflae
recipe main():
    # Automatic memory management - no tokens needed
    let items = List<Integer>()
    items.push(42)

    # Thread-safe sharing with one keyword
    shared let cache = Dict<Text, Data>()
    spawn_task(worker, cache)

    # Arbitrary-precision by default
    let big = 999999999999999999999999
    display(big + 1)
```

**Use Suflae for:**
- Web applications and APIs
- CLI tools and scripts
- Data processing and analytics
- Business logic
- Rapid prototyping

---

## Philosophy

We believe programming should be **enjoyable, not a battle with the language.**

### Core Principles

- **Total development cost** over raw runtime performance
- **Clear, descriptive words** over obscure historical terms
- **Explicit** over implicit
- **Consistency** over clever inconsistency
- **Honesty** over marketing hype

Read more: [Philosophy](wiki/Philosophy.md) | [Our Mission](wiki/Our-Mission.md)

---

## Quick Start

### Installation

```bash
# Clone the repository
git clone https://github.com/dj-lumiere/razorforge-lang.git
cd razorforge-lang

# Build the compiler
dotnet build

# Run a RazorForge program
./RazorForge hello_world.rf

# Run a Suflae program
./RazorForge hello_world.sf --suflae
```

### Hello World in RazorForge

```razorforge
recipe main() -> s32:
    show("Hello, RazorForge!")
    return 0
```

### Hello World in Suflae

```suflae
recipe main():
    display("Hello, Suflae!")
```

---

## Key Features

### RazorForge

- **Theatrical Memory Model**: `hijack!`, `share!`, `steal!` - make memory operations visible
- **Zero-Cost Abstractions**: Pay only for what you use
- **Danger Blocks**: Opt-in unsafe operations for zero-overhead code
- **Resident Types**: Fixed-size reference types for embedded systems
- **Freestanding Mode**: Bare metal programming without runtime
- **C Interop**: Seamless FFI with C libraries

### Suflae

- **Automatic Memory Management**: GC + RC hybrid for best of both worlds
- **Arbitrary Precision by Default**: No integer overflow surprises
- **Built-in Concurrency**: `shared` keyword and async/await
- **Type Safety**: Static types with inference
- **Native Performance**: AOT compilation to machine code
- **RazorForge Interop**: Call RazorForge libraries when you need control

### Shared Features

- **Pattern Matching**: Powerful `when` expressions
- **Type System**: Entities, records, choices, variants
- **Collections**: Lists, dicts, sets with consistent APIs
- **Error Handling**: Result types (no exceptions)
- **Generics**: Full generic programming support
- **Modern Syntax**: Python-like indentation, readable keywords

---

## Documentation

### Getting Started

- [Hello World](wiki/Hello-World.md) - Your first program
- [Choosing Between Languages](wiki/Choosing-Language.md) - RazorForge vs Suflae
- [IDE Support](wiki/IDE-Support.md) - Editor setup and tooling

### RazorForge

- [Memory Model](wiki/RazorForge-Memory-Model.md) - Theatrical memory management
- [Concurrency Model](wiki/RazorForge-Concurrency-Model.md) - Multi-threading primitives
- [Data Types](wiki/RazorForge-Data-Types.md) - Records, entities, residents
- [Danger Blocks](wiki/RazorForge-Danger-Blocks.md) - Unsafe operations
- [Code Style](wiki/RazorForge-Code-Style.md) - Coding conventions

### Suflae

- [Overview](wiki/Suflae-Overview.md) - Language introduction
- [Memory Model](wiki/Suflae-Memory-Model.md) - Automatic memory management
- [Concurrency Model](wiki/Suflae-Concurrency-Model.md) - Easy parallelism
- [Data Types](wiki/Suflae-Data-Types.md) - Simplified type system
- [Code Style](wiki/Suflae-Code-Style.md) - Coding conventions

### Reference

- [Keyword Comparison](wiki/Keyword-Comparison.md) - Side-by-side syntax
- [Numeric Types](wiki/Numeric-Types.md) - Integer and floating-point types
- [Collections](wiki/Collections.md) - Data structures reference
- [Pattern Matching](wiki/Pattern-Matching.md) - Patterns and matching
- [Error Handling](wiki/Error-Handling.md) - Result types

---

## Examples

### RazorForge: Systems Programming

```razorforge
# Memory-efficient linked list node
resident ListNode<T>:
    value: T
    next: ListNode<T>?  # Optional next node

    recipe new(value: T) -> ListNode<T>:
        return ListNode(value: value, next: None)

recipe main() -> s32:
    # Allocate on stack (fixed size)
    let node = ListNode.new(42)

    # Explicit memory control
    let shared = node.share!()

    return 0
```

### Suflae: Application Development

```suflae
suspended recipe fetch_users() -> Result<List<User>>:
    let response = waitfor http.get("/api/users")
    unless response:
        return Error("Network error")

    return parse_users(response.body)

recipe main():
    # Automatic async/await
    let users = waitfor fetch_users()
    when users:
        is Ok data => display(f"Found {data.length()} users")
        is Error msg => display(f"Error: {msg}")
```

---

## Project Structure

```
RazorForge/
├── src/               # Compiler source code
│   ├── Lexer/        # Tokenization
│   ├── Parser/       # Parsing (RazorForge & Suflae)
│   ├── AST/          # Abstract syntax tree
│   ├── Analysis/     # Semantic analysis
│   └── CodeGen/      # LLVM code generation
├── native/           # Native runtime components
├── extensions/       # IDE extensions
│   ├── razorforge/   # RazorForge VSCode extension
│   └── suflae/       # Suflae VSCode extension
├── tests/            # Test files
├── wiki/             # Language documentation
└── docs/             # Technical documentation
```

---

## Building from Source

### Prerequisites

- .NET SDK 8.0 or later
- LLVM 15+ (for code generation)
- CMake 3.20+ (for native components)

### Build Steps

```bash
# Clone the repository
git clone https://github.com/dj-lumiere/razorforge-lang.git
cd razorforge-lang

# Build the compiler
dotnet build

# Run tests
dotnet test

# Build native runtime
cd native
mkdir build && cd build
cmake ..
make
```

---

## Roadmap

### Version 0.1 (Current)

- ✅ Basic lexer and parser for both languages
- ✅ Memory model implementation (tokens, RC)
- ✅ LLVM code generation
- ✅ Pattern matching
- ✅ Basic collections
- 🚧 Standard library cleanup
- 🚧 Error handling (Result types)

### Version 0.2 (Next)

- Generics implementation
- C FFI support
- Freestanding mode
- Async/await for Suflae
- Package manager
- Language server protocol (LSP)

### Future

- Self-hosting compiler
- Native debugging support
- WASM backend
- Formal verification tools

---

## Contributing

We welcome contributions! Whether it's:

- 🐛 Bug reports
- 💡 Feature suggestions
- 📖 Documentation improvements
- 🔧 Code contributions

Please read [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines.

---

## License

RazorForge and Suflae are dual-licensed under:

- MIT License
- Apache License 2.0

Choose the license that works best for your project.

---

## Community

- **GitHub**: [github.com/dj-lumiere/razorforge-lang](https://github.com/dj-lumiere/razorforge-lang)
- **Issues**: [Report bugs or request features](https://github.com/dj-lumiere/razorforge-lang/issues)
- **Wiki**: [Full documentation](https://github.com/dj-lumiere/razorforge-lang/wiki)

---

## Acknowledgments

RazorForge and Suflae are inspired by:

- **Rust** - Memory safety without garbage collection
- **Python** - Readable syntax and developer happiness
- **Zig** - Explicit control and simplicity
- **Swift** - Modern language design
- **C** - Systems programming power

But we believe we can do better by being honest about tradeoffs and focusing on total developer happiness.

---

**Happy coding! ⚔️🍮**

*"From sweet to sharp, seamlessly."*
