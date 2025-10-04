namespace Compilers.Shared.Analysis;

/// <summary>
/// Target language specification for the unified RazorForge/Cake compiler.
/// 
/// These two languages represent fundamentally different approaches to memory management
/// and system programming, unified under a single compiler infrastructure:
/// 
/// RazorForge: Explicit memory management with 6 wrapper types (Owned, Hijacked, Shared,
/// Watched, ThreadShared, ThreadWatched, Snatched) organized into color-coded memory groups.
/// Emphasizes programmer control, zero-cost abstractions, and compile-time safety.
/// Target use cases: systems programming, embedded development, performance-critical applications.
/// 
/// Cake: Automatic reference counting with incremental garbage collection for cycle detection.
/// Emphasizes programmer productivity, safety by default, and ease of use.
/// Target use cases: scripting, rapid prototyping, application development, REPL usage.
/// 
/// The compiler adapts its semantic analysis, memory model enforcement, and code generation
/// based on the target language, allowing developers to choose the right tool for their needs.
/// </summary>
public enum Language
{
    /// <summary>
    /// RazorForge: Explicit memory management with wrapper types and memory groups.
    /// Features manual memory operations (hijack!, share!, etc.), danger! blocks for unsafe code,
    /// usurping functions, and compile-time deadref protection. Compiles to high-performance
    /// native code with zero-cost abstractions.
    /// </summary>
    RazorForge,

    /// <summary>
    /// Cake: Automatic memory management with reference counting and incremental GC.
    /// Features automatic sharing, transparent reference counting, cycle detection,
    /// and both AOT compilation and live REPL execution. Prioritizes safety and ease of use.
    /// </summary>
    Cake
}

/// <summary>
/// Language modes provide fine-grained control over safety vs performance tradeoffs
/// within each language. These modes affect compiler behavior, optimization levels,
/// and available language features.
/// 
/// RazorForge modes control the strictness of memory safety enforcement:
/// <list type="bullet">
/// <item>Normal mode provides safe memory management with full safety guarantees</item>
/// <item>Danger mode allows unsafe operations for systems programming and performance</item>
/// </list>
///
/// Cake modes control the balance between convenience and performance:
/// <list type="bullet">
/// <item>Sweet mode optimizes for developer productivity and rapid iteration</item>
/// <item>Bitter mode enables lower-level optimizations for performance-critical code</item>
/// </list>
/// </summary>
public enum LanguageMode
{
    // === RazorForge Modes ===

    /// <summary>
    /// RazorForge Normal Mode: Safe memory management for systems programming.
    /// 
    /// Features:
    /// <list type="bullet">
    /// <item>Full memory safety enforcement with deadref protection</item>
    /// <item>All 6 wrapper types available with group separation rules</item>
    /// <item>Memory operations validated at compile time</item>
    /// <item>No danger! blocks allowed (unsafe operations forbidden)</item>
    /// <item>Usurping functions controlled and validated</item>
    /// <item>Zero-cost abstractions with compile-time guarantees</item>
    /// </list>
    /// 
    /// Use cases: Production systems code, embedded applications, safety-critical software
    /// </summary>
    Normal,

    /// <summary>
    /// RazorForge Danger Mode: Raw embedded programming with great power.
    /// 
    /// Features:
    /// <list type="bullet">
    /// <item>All Normal mode features plus unsafe operations</item>
    /// <item>danger! blocks enable memory safety rule violations</item>
    /// <item>snatch!(), reveal!(), own!() operations available</item>
    /// <item>Mixed memory group operations allowed in danger blocks</item>
    /// <item>Direct memory manipulation and low-level access</item>
    /// <item>"With great power comes great responsibility" philosophy</item>
    /// </list>
    /// 
    /// Use cases: Embedded systems, device drivers, performance optimization,
    /// interfacing with C code, memory-mapped I/O
    /// </summary>
    Danger,

    // === Cake Modes ===

    /// <summary>
    /// Cake Sweet Mode: Quick prototyping with maximum safety and convenience.
    /// 
    /// Features:
    /// <list type="bullet">
    /// <item>Automatic reference counting with transparent management</item>
    /// <item>Incremental garbage collection for cycle detection</item>
    /// <item>No manual memory operations (all automatic)</item>
    /// <item>Rich standard library with high-level abstractions</item>
    /// <item>Interactive REPL with live compilation support</item>
    /// <item>Optimized for developer productivity and learning</item>
    /// </list>
    /// 
    /// Use cases: Rapid prototyping, scripting, interactive development,
    /// educational environments, data analysis
    /// </summary>
    Sweet,

    /// <summary>
    /// Cake Bitter Mode: Performant scripting with lower-level optimizations.
    /// 
    /// Features:
    /// <list type="bullet">
    /// <item>All Sweet mode features plus performance optimizations</item>
    /// <item>AOT compilation with aggressive optimization passes</item>
    /// <item>Reduced GC overhead with tuned collection strategies</item>
    /// <item>Access to lower-level APIs and system interfaces</item>
    /// <item>Optimized data structures and algorithms</item>
    /// <item>Balance between safety and performance</item>
    /// </list>
    /// 
    /// Use cases: Production scripting, performance-sensitive applications,
    /// command-line tools, server-side development
    /// </summary>
    Bitter
}
