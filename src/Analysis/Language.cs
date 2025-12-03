namespace Compilers.Shared.Analysis;

/// <summary>
/// Target language specification for the unified RazorForge/Suflae compiler.
///
/// These two languages represent fundamentally different approaches to memory management
/// and system programming, unified under a single compiler infrastructure:
///
/// RazorForge: Explicit memory management with 6 wrapper types (Owned, Hijacked, Retained,
/// Tracked, Shared, Snatched) organized into color-coded memory groups.
/// Emphasizes programmer control, zero-cost abstractions, and compile-time safety.
/// Target use cases: systems programming, embedded development, performance-critical applications.
///
/// Suflae: Automatic reference counting with incremental garbage collection for cycle detection.
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
    /// Features manual memory operations (hijack!, share, etc.), danger! blocks for unsafe code,
    /// usurping functions, and compile-time deadref protection. Compiles to high-performance
    /// native code with zero-cost abstractions.
    /// </summary>
    RazorForge,

    /// <summary>
    /// Suflae: Automatic memory management with reference counting and incremental GC.
    /// Features automatic sharing, transparent reference counting, cycle detection,
    /// and both AOT compilation and live REPL execution. Prioritizes safety and ease of use.
    /// </summary>
    Suflae
}

/// <summary>
/// Language modes provide fine-grained control over compilation targets and available features
/// within each language. These modes affect compiler behavior, available libraries,
/// and runtime requirements.
///
/// RazorForge modes:
/// <list type="bullet">
/// <item>Normal mode: Full standard library with OS integration</item>
/// <item>Freestanding mode: No OS dependencies, for embedded/kernel development</item>
/// </list>
///
/// Note: danger! blocks are a block-level construct within RazorForge code, not a separate
/// language mode. They allow unsafe operations within a scoped region while the rest of the
/// code maintains full safety guarantees.
/// </summary>
public enum LanguageMode
{
    // === RazorForge Modes ===

    /// <summary>
    /// RazorForge Normal Mode: Full-featured systems programming with OS integration.
    ///
    /// Features:
    /// <list type="bullet">
    /// <item>Full memory safety enforcement with deadref protection</item>
    /// <item>All 6 wrapper types available with group separation rules</item>
    /// <item>Memory operations validated at compile time</item>
    /// <item>danger! blocks available for scoped unsafe operations</item>
    /// <item>Full standard library with OS-dependent features</item>
    /// <item>Zero-cost abstractions with compile-time guarantees</item>
    /// </list>
    ///
    /// Use cases: Production systems code, applications, libraries
    /// </summary>
    Normal,

    /// <summary>
    /// RazorForge Freestanding Mode: Embedded and kernel development without OS dependencies.
    ///
    /// Features:
    /// <list type="bullet">
    /// <item>No standard library dependencies on OS</item>
    /// <item>No heap allocation by default</item>
    /// <item>Minimal runtime requirements</item>
    /// <item>Custom entry points (no main() requirement)</item>
    /// <item>danger! blocks for hardware access</item>
    /// </list>
    ///
    /// Use cases: Embedded systems, bootloaders, kernel development, bare-metal programming
    /// </summary>
    Freestanding,

    // === Suflae Modes ===

    /// <summary>
    /// Suflae Mode: Quick prototyping with maximum safety and convenience.
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
    Suflae
}
