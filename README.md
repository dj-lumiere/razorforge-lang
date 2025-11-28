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

routine main() -> s32 {
    # Theatrical memory tokens - explicit and visible
    let cache = Cache(data: Dict(), hits: 0)

    # Pattern matching for control flow
    when cache.view().hits > 0 {
        is true => show("Cache active"),
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
    show(f"Hits: {shared.view().hits}")

    return 0
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
shared entity DataStore:
    private items: Dict<Integer, Text>
    private count: Integer

    public routine __create__():
        me.items = Dict()
        me.count = 0

    public suspended routine fetch(id: Integer) -> Result<Text>:
        # Pattern matching with async/await
        when me.items.get(id):
            is None:
                # Arbitrary-precision math - no overflow!
                me.count += 1
                let data = waitfor http.get(f"/api/data/{id}")
                unless data:
                    return Error("Not found")
                me.items.insert(id, data)
                return data
            else cached:
                return cached

routine main():
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
routine main() -> s32 {
    show("Hello, RazorForge!")
}
```

### Hello World in Suflae

```suflae
routine main():
    show("Hello, Suflae!")
```

---

## Key Features

### RazorForge

- **Theatrical Memory Model**: Inline tokens (`.view()`, `.hijack()`) + scoped blocks (`viewing`, `hijacking`) +
  explicit ownership (`.consume()`)
- **No Lifetime Annotations**: Inline-only tokens + scoped blocks - safety without complexity
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

- [Hello World](wiki/Hello-World.md) – Your first program
- [Choosing Between Languages](wiki/Choosing-Language.md) – RazorForge vs Suflae
- [IDE Support](wiki/IDE-Support.md) – Editor setup and tooling

### RazorForge

- [Memory Model](wiki/RazorForge-Memory-Model.md) – Theatrical memory management
- [Concurrency Model](wiki/RazorForge-Concurrency-Model.md) – Multi-threading primitives
- [Data Types](wiki/RazorForge-Data-Types.md) – Records, entities, residents
- [Danger Blocks](wiki/RazorForge-Danger-Blocks.md) – Unsafe operations
- [Code Style](wiki/RazorForge-Code-Style.md) – Coding conventions

### Suflae

- [Overview](wiki/Suflae-Overview.md) – Language introduction
- [Memory Model](wiki/Suflae-Memory-Model.md) – Automatic memory management
- [Concurrency Model](wiki/Suflae-Concurrency-Model.md) – Easy parallelism
- [Data Types](wiki/Suflae-Data-Types.md) – Simplified type system
- [Code Style](wiki/Suflae-Code-Style.md) – Coding conventions

### Reference

- [Keyword Comparison](wiki/Keyword-Comparison.md) – Side-by-side syntax
- [Numeric Types](wiki/Numeric-Types.md) – Integer and floating-point types
- [Collections](wiki/Collections.md) – Data structures reference
- [Pattern Matching](wiki/Pattern-Matching.md) – Patterns and matching
- [Error Handling](wiki/Error-Handling.md) – Result types

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
### - observing for concurrent reads
### - seizing for exclusive writes
entity Counter {
    var value: s64
}

routine main() -> s32 {
    let counter = Counter(value: 0)

    # Create thread-safe reference with MultiReadLock policy
    let shared = counter.share(policy: MultiReadLock)

    # Spawn reader threads - can run concurrently!
    for i in 0 to 5 {
        let reader = shared  # Clone Arc (atomic increment)
        spawn_thread({
            observing reader as r {
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

    return 0
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

routine main() -> s32 {
    let (tx, rx) = Channel<Task>()

    # Spawn worker threads
    for worker_id in 0 to 4 {
        let receiver = rx
        spawn_thread({
            loop {
                when receiver.receive() {
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

    return 0
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
shared entity UserCache:
    private cache: Dict<Integer, User>
    private hit_count: Integer
    private miss_count: Integer

    public routine __create__():
        me.cache = Dict()
        me.hit_count = 0
        me.miss_count = 0

    public routine get_stats() -> Text:
        let total = me.hit_count + me.miss_count  # Arbitrary precision!
        let rate = (me.hit_count * 100) / total if total > 0 else 0
        return f"Cache hits: {me.hit_count}, rate: {rate}%"

    public suspended routine fetch_user(id: Integer) -> Result<User>:
        # Check cache first
        when me.cache.get(id):
            is None:
                me.miss_count += 1
                let user = waitfor http.get(f"/api/users/{id}")
                unless user:
                    return Error("Network error")
                me.cache.insert(id, user)
                return user
            else cached:
                me.hit_count += 1
                return cached

routine main():
    # shared entity uses actor model - automatic message passing!
    shared let cache = UserCache()

    # Multiple tasks can safely call cache methods concurrently
    spawn_task(lambda:
        let user = waitfor cache.fetch_user(12345)
        when user:
            is Error msg => show(f"Error: {msg}"),
            else data => show(f"Got user: {data.name}")
    )

    spawn_task(lambda:
        let stats = cache.get_stats()  # Thread-safe automatic message
        show(stats)
    )
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

- **Rust** – Memory safety without garbage collection
- **Python** – Readable syntax and developer happiness
- **Zig** – Explicit control and simplicity
- **C#** – Modern language design
- **C/C++** – Systems programming power

But we believe we can do better by being honest about tradeoffs and focusing on total developer happiness.

---

**Happy coding! ⚔️🍮**

*"From sweet to sharp, seamlessly."*
