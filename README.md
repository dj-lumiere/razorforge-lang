# RazorForge & Suflae

**Two languages. One family. From sweet to sharp, seamlessly.**

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Status](https://img.shields.io/badge/status-in%20development-orange.svg)

---

## What Are RazorForge and Suflae?

RazorForge and Suflae are modern programming languages designed to provide a smooth gradient from high-productivity
application development to high-performance systems programming.

### ⚔️ RazorForge: "Make Programming Sharp Again"

A precision systems language for absolute control and deterministic performance.

```razorforge
entity Cache {
    var data: Dict<s32, Text<letter>>
    var hits: s64
}

routine start() {
    # Theatrical memory tokens - explicit and visible
    let cache = Cache(data: Dict(), hits: 0)

    # Pattern matching for control flow
    when cache.view().hits > 0 {
        true => show("Cache active"),
        else => {
            # Scoped access for multiple operations
            hijacking cache as h {
                h.data.insert(1, "value")
                h.hits += 1
            }
        }
    }

    # Reference counting when needed
    let shared = cache.retain()  # Explicit Retained<T>

    # Inline access for single operations
    show(f"Hits: {shared.hits}")
}
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
entity DataStore:
    private items: Dict<Integer, Text>
    private count: Integer

public routine DataStore.__create__():
    me.items = Dict()
    me.count = 0

public suspended routine DataStore.fetch!(id: Integer):
    # Pattern matching with async/await
    when me.items.try_get(id):
        is None:
            # Arbitrary-precision math - no overflow!
            me.count += 1
            let data = waitfor http.get(f"/api/data/{id}")
            unless data:
                throw ElementNotFoundError("Not found")
            me.items.insert(id, data)
            return data
        else cached:
            return cached

routine start():
    # Actor model - automatic message passing!
    shared let store = DataStore()

    # Safe concurrent access - no locks needed
    spawn_task(lambda: waitfor store.fetch(999999999999))
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

### The Smooth Gradient Vision

Our mission is to provide a single, continuous path from your first "Hello World" to building world-class systems software. Each step builds naturally on the last, with no walls to climb.

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

# Build native runtime
cd native && mkdir build && cd build
cmake .. && make
```

### Using the Build Tools

```bash
# Create a new RazorForge project
forge new my-project
cd my-project

# Build and run
forge build
forge run

# For Suflae projects
bake new my-app
cd my-app
bake run
```

### Hello World in RazorForge

```razorforge
routine start() {
    show("Hello, RazorForge!")
}
```

### Hello World in Suflae

```suflae
routine start():
    show("Hello, Suflae!")
```

See: [Hello World Tutorial](wiki/Hello-World.md) | [Build System](wiki/Build-System.md)

---

## Key Features

### RazorForge

- **Theatrical Memory Model**: Inline tokens (`.view()`, `.hijack()`) + scoped blocks (`viewing`, `hijacking`) + explicit ownership (`.consume()`)
- **No Lifetime Annotations**: Inline-only tokens + scoped blocks = safety without complexity
- **Pay Only for What You Use**: No hidden costs, explicit tradeoffs
- **Danger Blocks**: Opt-in unsafe operations (`danger!`) for zero-overhead code
- **Resident Types**: Fixed-size reference types for embedded systems
- **Freestanding Mode**: Bare metal programming without runtime
- **C Subsystem**: Full FFI with C libraries
- **CompilerService**: Compile-time introspection at runtime (zero overhead)

### Suflae

- **Automatic Memory Management**: RC + GC hybrid (deterministic cleanup for most objects)
- **Arbitrary Precision by Default**: `Integer` and `Decimal` types (no overflow)
- **Actor Model Concurrency**: `shared` keyword + async/await (`suspended`/`waitfor`)
- **Type Safety**: Static types with inference
- **Native Performance**: AOT compilation to machine code
- **RazorForge Interop**: Import RazorForge libraries when you need systems control
- **Import-Based Performance**: Opt-in fixed-width types (`s32`, `f64`) via imports

### Shared Features

- **Pattern Matching**: Powerful `when` expressions with structural matching
- **Type System**: `entity` (reference), `record` (value), `choice` (tagged union), `variant` (runtime types)
- **Rich Collections**: `List`, `Dict`, `Set`, `Deque`, `SortedDict`, `PriorityQueue`, and more
- **Error Handling**: `Maybe<T>`, `Result<T>`, `Lookup<T>` (no exceptions)
- **Generics**: Full generic programming with type parameters
- **Lambdas**: Single-expression arrow syntax `x => x * 2` (parentheses optional for single param, multiline lambdas banned)
- **Modern Syntax**: Indentation-based, clear keywords (`routine`, `entity`, `when`)
- **Unified Standard Library**: Same collections and APIs across both languages

---

## Documentation

### Getting Started

- [Hello World](wiki/Hello-World.md) – Your first program
- [Choosing Between Languages](wiki/Choosing-Language.md) – RazorForge vs Suflae
- [IDE Support](wiki/IDE-Support.md) – Editor setup and tooling

### RazorForge

- [Memory Model](wiki/RazorForge-Memory-Model.md) – Theatrical memory management (inline tokens, scoped access)
- [Concurrency Model](wiki/RazorForge-Concurrency-Model.md) – `Shared<T, Policy>`, threading, message passing
- [Data Types](wiki/RazorForge-Data-Types.md) – Records, entities, residents, choices, variants
- [Residents](wiki/RazorForge-Residents.md) – Fixed-size reference types for embedded systems
- [Danger Blocks](wiki/RazorForge-Danger-Blocks.md) – Unsafe operations and raw memory access
- [C Subsystem](wiki/RazorForge-C-Subsystem.md) – FFI and C interop
- [Freestanding Mode](wiki/RazorForge-Freestanding-Mode.md) – Bare metal programming
- [Reality Bender](wiki/RazorForge-Reality-Bender.md) – Advanced low-level manipulation
- [Code Style](wiki/RazorForge-Code-Style.md) – Coding conventions
- [Core Prelude](wiki/RazorForge-Core.md) – Always-loaded types and primitives

### Suflae

- [Overview](wiki/Suflae-Overview.md) – Language introduction
- [Memory Model](wiki/Suflae-Memory-Model.md) – Automatic RC + GC memory management
- [Concurrency Model](wiki/Suflae-Concurrency-Model.md) – Actor model, `shared` keyword, async/await
- [Data Types](wiki/Suflae-Data-Types.md) – Entities, records, choices (simplified)
- [Error Handling](wiki/Suflae-Error-Handling.md) – `Maybe`, `Result`, error propagation
- [Text](wiki/Suflae-Text.md) – String handling and text processing
- [Code Style](wiki/Suflae-Code-Style.md) – Coding conventions
- [Core Prelude](wiki/Suflae-Core.md) – Always-loaded types and abstractions

### Language Reference

- [Keyword Comparison](wiki/Keyword-Comparison.md) – RazorForge vs Suflae syntax
- [Numeric Types](wiki/Numeric-Types.md) – `s32`, `f64`, `d128`, `Integer`, `Decimal`
- [Numeric Operators](wiki/Numeric-Operators.md) – Arithmetic operations
- [Comparison Operators](wiki/Comparison-Operators.md) – Equality and ordering
- [Collections](wiki/Collections.md) – `List`, `Dict`, `Set`, sorted collections, fixed-size
- [Pattern Matching](wiki/Pattern-Matching.md) – `when` expressions
- [RazorForge Error Handling](wiki/RazorForge-Error-Handling.md) – `Maybe`, `Result`, `Lookup`
- [Suflae Error Handling](wiki/Suflae-Error-Handling.md) – Error types and propagation
- [Generics](wiki/Generics.md) – Generic programming
- [Attributes](wiki/Attributes.md) – Metadata and annotations
- [Modules and Imports](wiki/Modules-and-Imports.md) – Module system

### System Reference

- [Build System](wiki/Build-System.md) – `forge`/`bake` commands, TOML manifests
- [Runtime](wiki/Runtime.md) – CompilerService and Reflection
- [Standard Libraries](wiki/Standard-Libraries.md) – Complete stdlib overview
- [Console I/O](wiki/Console-IO.md) – Terminal input/output
- [File I/O](wiki/File-IO.md) – File system operations
- [DateTime & Duration](wiki/DateTime-Duration.md) – Time handling
- [Memory Size Literals](wiki/Memory-Size-Literals.md) – Size units (KiB, MiB)

---

## Learning Paths

### Path 1: Application Developer (Suflae First)

1. [Hello World](wiki/Hello-World.md) – Your first program
2. [Suflae Overview](wiki/Suflae-Overview.md) – Language introduction
3. [Suflae Data Types](wiki/Suflae-Data-Types.md) – Understanding types
4. [Pattern Matching](wiki/Pattern-Matching.md) – Control flow
5. [Collections](wiki/Collections.md) – Data structures
6. [Suflae Concurrency](wiki/Suflae-Concurrency-Model.md) – Parallel programming

### Path 2: Systems Programmer (RazorForge First)

1. [Hello World](wiki/Hello-World.md) – Your first program
2. [RazorForge Data Types](wiki/RazorForge-Data-Types.md) – Type system
3. [RazorForge Memory Model](wiki/RazorForge-Memory-Model.md) – Memory management
4. [Collections](wiki/Collections.md) – Including fixed collections
5. [RazorForge Concurrency](wiki/RazorForge-Concurrency-Model.md) – Threading and message passing
6. [Danger Blocks](wiki/RazorForge-Danger-Blocks.md) – Unsafe operations

### Path 3: The Smooth Gradient (Suflae → RazorForge)

Start with Suflae for productivity, gradually learn RazorForge for control:

1. Master Suflae basics (Path 1)
2. [Choosing Between Languages](wiki/Choosing-Language.md) – When to use each
3. [RazorForge Memory Model](wiki/RazorForge-Memory-Model.md) – Understanding explicit control
4. [Residents](wiki/RazorForge-Residents.md) – Fixed-size reference types
5. [Freestanding Mode](wiki/RazorForge-Freestanding-Mode.md) – Bare metal programming
6. [Reality Bender](wiki/RazorForge-Reality-Bender.md) – Advanced techniques

---

## Examples

### RazorForge: Theatrical Memory Management

RazorForge makes memory operations **visible and explicit** through inline tokens and scoped blocks:

```razorforge
### High-performance connection pool with reference counting
###
### Demonstrates:
### - Inline tokens (.view()/.hijack()) for single operations
### - Scoped blocks (hijacking/viewing) for multiple operations
### - .consume() for explicit ownership transfer
### - Retained<T> for reference-counted ownership
### - Tracked<Retained<T>> for weak references (break cycles)
### - Pattern matching with when
entity ConnectionPool {
    var connections: List<Connection>
    var active_count: s64
    var max_connections: s64
}

routine acquire_connection(pool: Retained<ConnectionPool>) -> Connection? {
    # Scoped access for multiple operations
    hijacking pool as p {
        # Check capacity with pattern matching
        when p.active_count < p.max_connections {
            is true => {
                let conn = Connection()
                p.connections.push(conn.consume())  # Explicit ownership transfer
                p.active_count += 1
                return conn
            },
            else => return None  # Pool exhausted
        }
    }
}

routine monitor_pool(weak: Tracked<Retained<ConnectionPool>>) {
    # Try to recover weak reference
    when weak.try_recover() {
        is None => show("Pool was deallocated"),
        else strong => {
            # Inline access for single read
            show("Pool stats: {strong.view().active_count}/{strong.view().max_connections}")
        }
    }
}
```

### RazorForge: Thread-Safe Concurrency

RazorForge provides **explicit control** over concurrent access with zero hidden costs:

```razorforge
### Thread-safe counter with configurable locking
###
### Demonstrates:
### - Shared<T, Policy> for thread-safe references
### - inspecting for concurrent reads
### - seizing for exclusive writes
entity Counter {
    var value: s64
}

routine start() {
    let counter = Counter(value: 0)

    # Create thread-safe reference with MultiReadLock policy
    let shared = counter.share(policy: MultiReadLock)

    # Spawn reader threads - can run concurrently!
    for i in 0 to 5 {
        let reader = shared  # Clone Arc (atomic increment)
        spawn_thread({
            inspecting reader as r {
                show("Thread {i} reads: {r.value}")
            }  # Read lock released automatically
        })
    }

    # Writer thread - exclusive access
    let writer = shared
    spawn_thread({
        seizing writer as w {
            w.value += 1
        }  # Write lock released automatically
    })
}
```

### RazorForge: Lock-Free Atomic Operations

For high-performance scenarios, use **lock-free atomics**:

```razorforge
### High-performance metrics collector
###
### Demonstrates:
### - Atomic<T> for lock-free operations
### - Zero overhead for simple counters
entity MetricsCollector {
    request_count: Atomic<s64>
    error_count: Atomic<s64>
}

routine track_request(metrics: MetricsCollector, success: bool) {
    # Lock-free increment (~5-10 CPU cycles)
    metrics.request_count.fetch_add(1)

    if !success {
        metrics.error_count.fetch_add(1)
    }
}
```

### RazorForge: Message Passing

For clean thread communication, **transfer ownership** via channels:

```razorforge
### Worker pool processing tasks
###
### Demonstrates:
### - Channel<T> for ownership transfer
### - No shared state - just message passing
entity Task {
    id: s32
    data: Text<letter>
}

routine start() {
    let (tx, rx) = Channel<Task>()

    # Spawn worker threads
    for worker_id in 0 to 4 {
        let receiver = rx
        spawn_thread({
            loop {
                when receiver.try_receive() {
                    is None => break,  # Channel closed
                    else task => {
                        show("Worker {worker_id} processing task {task.id}")
                        process_task(task)
                    }
                }
            }
        })
    }

    # Send tasks (ownership transferred to channel)
    for i in 0 to 100 {
        let task = Task(id: i, data: "payload")
        tx.send(task.consume())  # Explicit: task becomes deadref
    }
}
```

### Suflae: Sweet Productivity

Suflae prioritizes **developer happiness** with automatic memory management and built-in concurrency:

```suflae
### User service with automatic async/await and actor model
###
### Demonstrates:
### - suspended routines for async operations
### - Arbitrary-precision Integer by default
### - shared keyword for actor model (automatic message passing)
### - Pattern matching with when
### - unless for error handling
entity UserCache:
    private cache: Dict<Integer, User>
    private hit_count: Integer
    private miss_count: Integer

public routine UserCache.__create__():
    me.cache = Dict()
    me.hit_count = 0
    me.miss_count = 0

public routine UserCache.get_stats() -> Text:
    let total = me.hit_count + me.miss_count  # Arbitrary precision!
    let rate = (me.hit_count * 100) / total if total > 0 else 0
    return f"Cache hits: {me.hit_count}, rate: {rate}%"

public suspended routine UserCache.fetch_user!(id: Integer) -> :
    # Check cache first
    when me.cache.try_get(id):
        is None:
            me.miss_count += 1
            let user = waitfor http.get(f"/api/users/{id}")
            unless user:
                throw NetworkError()
            me.cache.insert(id, user)
            return user
        else cached:
            me.hit_count += 1
            return cached

routine start():
    # shared entity uses actor model - automatic message passing!
    shared let cache = UserCache()

    # Multiple tasks can safely call cache methods concurrently
    spawn_task(() =>
        let user = waitfor cache.check_fetch_user(12345)
        when user:
            is Crashable e => show(f"Error: {e.msg}"),
            else data => show(f"Got user: {data.name}")
    )

    spawn_task(() =>
        let stats = cache.get_stats()  # Thread-safe automatic message
        show(stats)
    )
```

---

## Project Structure

```
RazorForge/
├── src/              # Compiler source code
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
- C compiler (GCC, Clang, or MSVC)

### Build Steps

```bash
# Clone the repository
git clone https://github.com/dj-lumiere/razorforge-lang.git
cd razorforge-lang

# Build the compiler
dotnet build

# Run tests
dotnet test

# Build native runtime (required for execution)
cd native
mkdir build && cd build
cmake ..
make  # or 'cmake --build .' on Windows
```

See [Build System](wiki/Build-System.md) for detailed build configuration.

---

## Roadmap

### Version 0.1 (Current)

- ✅ Lexer and parser for both languages
- ✅ Memory model implementation (inline tokens, scoped access, RC)
- ✅ LLVM code generation
- ✅ Pattern matching (`when` expressions)
- ✅ Collections (List, Dict, Set, etc.)
- ✅ Error handling (`Maybe`, `Result`, `Lookup`)
- ✅ CompilerService (compile-time introspection)
- 🚧 Standard library expansion
- 🚧 Module system refinement

### Version 0.2 (Next)

- Full generics implementation
- Complete C FFI support (C Subsystem)
- Freestanding mode for bare metal
- Full async/await for Suflae (`suspended`/`waitfor`)
- Package manager (`forge`/`bake` CLI tools)
- Language server protocol (LSP)
- VSCode extensions
- Reflection system

### Future

- Self-hosting compiler (compiler written in RazorForge)
- Native debugging support (DWARF/PDB generation)
- WASM backend
- Formal verification tools
- Advanced SIMD support
- Reality Bender toolkit (ObjectHacker, Mutant, Metamorph)

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

- **Rust** – Memory safety without garbage collection
- **Python** – Readable syntax and developer happiness
- **Zig** – Explicit control and simplicity
- **C#** – Modern language design and tooling
- **C/C++** – Systems programming power
- **Erlang/Elixir** – Actor model concurrency (Suflae)

But we believe we can do better by:
- Being honest about tradeoffs (no "zero-cost" marketing lies)
- Focusing on total development cost (not just runtime performance)
- Providing a smooth gradient (not a wall) between high-level and low-level programming
- Making memory management theatrical and explicit (not hidden or complex)

---

**Happy coding! ⚔️🍮**

*"From sweet to sharp, seamlessly."*
