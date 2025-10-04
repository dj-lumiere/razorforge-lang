using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Memory model support for RazorForge and Cake languages.
/// Handles ownership tracking, wrapper types, and memory safety analysis.
/// 
/// RazorForge uses explicit memory management with 6 wrapper types organized into 3 color-coded groups:
/// <list type="bullet">
/// <item>Group 1 (Red): Exclusive borrowing - Hijacked&lt;T&gt;</item>
/// <item>Group 2 (Green/Brown): Single-threaded RC - Shared&lt;T&gt;, Watched&lt;T&gt;</item>
/// <item>Group 3 (Blue/Purple): Multi-threaded RC - ThreadShared&lt;T&gt;, ThreadWatched&lt;T&gt;</item>
/// <item>Unsafe (Black): Forcibly taken - Snatched&lt;T&gt; (danger! blocks only)</item>
/// </list>
/// 
/// Cake uses automatic reference counting with incremental garbage collection.
/// </summary>
/// <summary>
/// Memory wrapper types for RazorForge explicit memory management.
/// Each wrapper type corresponds to a different access pattern and ownership model.
/// The wrapper types are organized into groups that cannot be mixed except in danger! blocks.
/// </summary>
public enum WrapperType
{
    /// <summary>
    /// Direct ownership (no wrapper). Object is owned directly by a variable.
    /// Can be transformed into any other wrapper type. Most basic form of ownership.
    /// </summary>
    Owned,

    // Group 1 - Exclusive Borrowing (Red 🔴)
    /// <summary>
    /// Exclusive write access (Hijacked&lt;T&gt;). Only one can exist at a time.
    /// Provides exclusive mutable access to the object. Cannot be copied or shared.
    /// Must be explicitly transferred between functions using usurping.
    /// </summary>
    Hijacked,

    // Group 2 - Single-threaded Reference Counting (Green/Brown 🟢🟤)
    /// <summary>
    /// Shared ownership with interior mutability (Shared&lt;T&gt;).
    /// Multiple references can exist (RC > 1). Provides shared mutable access
    /// through reference counting similar to Rc&lt;RefCell&lt;T&gt;&gt; in Rust.
    /// </summary>
    Shared,

    /// <summary>
    /// Weak observer (Watched&lt;T&gt;). Does not prevent object destruction.
    /// Can observe shared objects without affecting reference count.
    /// Must use try_share!() to upgrade to strong reference.
    /// </summary>
    Watched,

    // Group 3 - Thread-Safe Reference Counting (Blue/Purple 🔵🟣)
    /// <summary>
    /// Thread-safe shared ownership (ThreadShared&lt;T&gt;).
    /// Multiple references across threads (Arc > 1). Provides thread-safe
    /// shared mutable access through atomic reference counting and mutex.
    /// </summary>
    ThreadShared,

    /// <summary>
    /// Thread-safe weak observer (ThreadWatched&lt;T&gt;).
    /// Thread-safe weak reference that doesn't prevent destruction.
    /// Can observe thread-shared objects across thread boundaries.
    /// </summary>
    ThreadWatched,

    // Unsafe (Black 💀)
    /// <summary>
    /// Forcibly taken (Snatched&lt;T&gt;). Created by snatch!() in danger! blocks.
    /// Indicates unsafe provenance - object was forcibly taken ignoring RC.
    /// Can only be used within danger! blocks. Marks contaminated ownership.
    /// </summary>
    Snatched
}

/// <summary>
/// Memory access groups that define which wrapper types can interact with each other.
/// The core rule: cannot mix between groups except in danger! blocks.
/// This prevents unsafe aliasing and ensures clear ownership semantics.
/// </summary>
public enum MemoryGroup
{
    /// <summary>
    /// Group 1: Exclusive borrowing (Red 🔴). 
    /// Contains: Owned, Hijacked. Provides exclusive access to objects.
    /// Cannot coexist with other groups - enforces single-owner semantics.
    /// </summary>
    Exclusive = 1,

    /// <summary>
    /// Group 2: Single-threaded reference counting (Green/Brown 🟢🟤).
    /// Contains: Shared, Watched. Allows multiple references within single thread.
    /// Uses RC (reference counting) similar to Rc&lt;RefCell&lt;T&gt;&gt; in Rust.
    /// </summary>
    SingleThreaded = 2,

    /// <summary>
    /// Group 3: Multi-threaded reference counting (Blue/Purple 🔵🟣).
    /// Contains: ThreadShared, ThreadWatched. Thread-safe shared ownership.
    /// Uses Arc (atomic reference counting) similar to Arc&lt;Mutex&lt;T&gt;&gt; in Rust.
    /// </summary>
    MultiThreaded = 3,

    /// <summary>
    /// Unsafe: Anything goes (Black 💀).
    /// Contains: Snatched. Only usable in danger! blocks.
    /// Allows breaking all safety rules for emergency situations.
    /// </summary>
    Unsafe = 4
}

/// <summary>
/// Object lifetime state tracking for memory safety analysis.
/// Tracks the current accessibility state of objects throughout their lifecycle.
/// Core principle: invalidated objects cannot be accessed (deadref protection).
/// </summary>
public enum ObjectState
{
    /// <summary>
    /// Object exists and is accessible. Normal state for active objects.
    /// All memory operations are allowed based on wrapper type rules.
    /// </summary>
    Valid,

    /// <summary>
    /// Object has been invalidated (deadref). Cannot be accessed anymore.
    /// Caused by: memory operations (share!, hijack!, etc.), scope exit, or explicit invalidation.
    /// Attempting to use invalidated objects results in compile-time error.
    /// </summary>
    Invalidated,

    /// <summary>
    /// Object moved into container (RazorForge) or RC'd (Cake).
    /// In RazorForge: object becomes deadref after moving into container.
    /// In Cake: object remains valid but RC is incremented.
    /// </summary>
    Moved,

    /// <summary>
    /// Object is within danger! block where unsafe rules apply.
    /// Allows operations that would normally be forbidden (snatch!, mixed groups).
    /// Used for emergency operations and low-level memory management.
    /// </summary>
    Dangerous
}

/// <summary>
/// Represents a memory-managed object with complete ownership tracking information.
/// This is the core data structure for tracking object lifetime, wrapper type,
/// reference counting, and invalidation state throughout the compilation process.
/// </summary>
/// <param name="Name">Variable name that holds reference to this object</param>
/// <param name="BaseType">The underlying type of the object (e.g., Node, List&lt;i32&gt;)</param>
/// <param name="Wrapper">Current wrapper type (Owned, Shared, Hijacked, etc.)</param>
/// <param name="State">Current object state (Valid, Invalidated, Moved, Dangerous)</param>
/// <param name="ReferenceCount">Current reference count (for Shared/ThreadShared types)</param>
/// <param name="Location">Source location where object was declared</param>
/// <param name="InvalidatedBy">Reason for invalidation (for error reporting)</param>
public record MemoryObject(
    string Name,
    TypeInfo BaseType,
    WrapperType Wrapper,
    ObjectState State,
    int ReferenceCount,
    SourceLocation Location,
    string? InvalidatedBy = null)
{
    /// <summary>
    /// Get the memory group for this wrapper type.
    /// Used to enforce the core rule: cannot mix operations between different groups.
    /// Owned objects can become any group, but once transformed, they're locked to that group.
    /// </summary>
    public MemoryGroup Group => Wrapper switch
    {
        // Owned can become any group - it's the starting point
        WrapperType.Owned => MemoryGroup.Exclusive,

        // Group 1: Exclusive access
        WrapperType.Hijacked => MemoryGroup.Exclusive,

        // Group 2: Single-threaded reference counting
        WrapperType.Shared or WrapperType.Watched => MemoryGroup.SingleThreaded,

        // Group 3: Multi-threaded reference counting  
        WrapperType.ThreadShared or WrapperType.ThreadWatched => MemoryGroup.MultiThreaded,

        // Unsafe: Danger zone
        WrapperType.Snatched => MemoryGroup.Unsafe,

        _ => throw new ArgumentException(message: $"Unknown wrapper type: {Wrapper}")
    };

    /// <summary>
    /// Check if this object can be transformed to the target wrapper type.
    /// Implements the core transformation rules of the RazorForge memory model:
    /// 1. Objects must be Valid (unless in danger block)
    /// 2. Cannot mix between memory groups (except in danger block)
    /// 3. steal!() only works when RC = 1
    /// 4. Some transformations increment RC rather than invalidating
    /// </summary>
    /// <param name="target">The target wrapper type to transform to</param>
    /// <param name="inDangerBlock">Whether we're in a danger! block (allows unsafe operations)</param>
    /// <returns>True if transformation is allowed, false otherwise</returns>
    public bool CanTransformTo(WrapperType target, bool inDangerBlock = false)
    {
        // Rule 1: Only valid objects can be transformed (unless in danger block)
        if (State != ObjectState.Valid && !inDangerBlock)
        {
            return false;
        }

        // Rule 2: Danger blocks allow everything - escape hatch for unsafe code
        if (inDangerBlock)
        {
            return true;
        }

        MemoryGroup targetGroup = GetGroup(wrapper: target);

        return (Wrapper, target) switch
        {
            // Owned objects can become anything - they're the starting point
            (WrapperType.Owned, _) => true,

            // === Within Group 1 (Exclusive) ===
            // Cannot hijack already hijacked object (only one exclusive access allowed)
            (WrapperType.Hijacked, WrapperType.Hijacked) => false,

            // === Within Group 2 (Single-threaded RC) ===
            // Creating multiple shared references increments RC
            (WrapperType.Shared, WrapperType.Shared) => true,
            // Create weak reference from shared (doesn't invalidate source)
            (WrapperType.Shared, WrapperType.Watched) => true,
            // Upgrade weak to strong via try_share!() (if object still exists)
            (WrapperType.Watched, WrapperType.Shared) => true,
            // Multiple weak references are allowed
            (WrapperType.Watched, WrapperType.Watched) => true,

            // === Within Group 3 (Multi-threaded RC) ===
            // Creating multiple thread-shared references increments Arc
            (WrapperType.ThreadShared, WrapperType.ThreadShared) => true,
            // Create weak reference from thread-shared
            (WrapperType.ThreadShared, WrapperType.ThreadWatched) => true,
            // Upgrade weak to strong via try_thread_share!()
            (WrapperType.ThreadWatched, WrapperType.ThreadShared) => true,
            // Multiple weak references are allowed
            (WrapperType.ThreadWatched, WrapperType.ThreadWatched) => true,

            // === steal!() operations - reclaim direct ownership ===
            // Can only steal when you're the sole owner (RC = 1)
            (WrapperType.Shared, WrapperType.Owned) => ReferenceCount == 1,
            (WrapperType.ThreadShared, WrapperType.Owned) => ReferenceCount == 1,
            // Can always steal from hijacked (exclusive access guaranteed)
            (WrapperType.Hijacked, WrapperType.Owned) => true,

            // === Cross-group transformations are forbidden ===
            // This prevents mixing different memory management strategies
            _ when Group != targetGroup => false,

            // All other combinations are invalid
            _ => false
        };
    }

    /// <summary>
    /// Helper method to get memory group for any wrapper type.
    /// Used for cross-group validation and transformation rules.
    /// </summary>
    /// <param name="wrapper">The wrapper type to get group for</param>
    /// <returns>The memory group this wrapper belongs to</returns>
    private static MemoryGroup GetGroup(WrapperType wrapper)
    {
        return wrapper switch
        {
            // Group 1: Exclusive access
            WrapperType.Owned => MemoryGroup.Exclusive,
            WrapperType.Hijacked => MemoryGroup.Exclusive,

            // Group 2: Single-threaded RC
            WrapperType.Shared or WrapperType.Watched => MemoryGroup.SingleThreaded,

            // Group 3: Multi-threaded RC
            WrapperType.ThreadShared or WrapperType.ThreadWatched => MemoryGroup.MultiThreaded,

            // Unsafe zone
            WrapperType.Snatched => MemoryGroup.Unsafe,

            _ => throw new ArgumentException(message: $"Unknown wrapper type: {wrapper}")
        };
    }
}

/// <summary>
/// Memory operation types corresponding to method calls in RazorForge source code.
/// Each operation transforms objects between different wrapper types or modifies reference counts.
/// Operations ending with '!' can potentially crash/panic on invalid use.
/// 
/// The operations are grouped by their primary purpose:
/// <list type="bullet">
/// <item>Group Transformations: hijack!(), share!(), thread_share!() - change wrapper type</item>
/// <item>Weak References: watch!(), thread_watch!() - create non-owning references</item>
/// <item>Ownership Reclaim: steal!() - get back direct ownership when RC = 1</item>
/// <item>RC Management: release!() - manually decrement reference count</item>
/// <item>Weak Upgrades: try_share!(), try_thread_share!() - upgrade weak to strong (can fail)</item>
/// <item>Unsafe Operations: snatch!(), reveal!(), own!() - danger! block only</item>
/// </list>
/// </summary>
public enum MemoryOperation
{
    /// <summary>
    /// hijack!() - Transform to exclusive access (Hijacked&lt;T&gt;).
    /// Creates exclusive mutable access by invalidating the source object.
    /// Only one Hijacked reference can exist at a time (exclusive ownership).
    /// Use case: When you need guaranteed exclusive write access.
    /// </summary>
    Hijack,

    /// <summary>
    /// share!() - Transform to shared ownership (Shared&lt;T&gt;).
    /// Creates reference-counted shared access with interior mutability.
    /// Multiple Shared references can coexist, each with RC tracking.
    /// Use case: When multiple references need mutable access to same object.
    /// </summary>
    Share,

    /// <summary>
    /// watch!() - Create weak reference (Watched&lt;T&gt;).
    /// Creates observer reference that doesn't prevent object destruction.
    /// Weak references don't contribute to reference count (RC = 0).
    /// Use case: Breaking cycles, observing without ownership responsibility.
    /// </summary>
    Watch,

    /// <summary>
    /// thread_share!() - Transform to thread-safe shared (ThreadShared&lt;T&gt;).
    /// Creates atomic reference-counted shared access across threads.
    /// Uses Arc (atomic reference count) similar to Arc&lt;Mutex&lt;T&gt;&gt; in Rust.
    /// Use case: Sharing mutable objects across thread boundaries.
    /// </summary>
    ThreadShare,

    /// <summary>
    /// thread_watch!() - Create thread-safe weak reference (ThreadWatched&lt;T&gt;).
    /// Creates thread-safe observer that doesn't prevent destruction.
    /// Can observe ThreadShared objects without affecting Arc count.
    /// Use case: Thread-safe cycle breaking and observation.
    /// </summary>
    ThreadWatch,

    /// <summary>
    /// steal!() - Reclaim direct ownership (back to Owned), requires RC = 1.
    /// Only works when you're the sole owner of a shared object.
    /// Converts Shared/ThreadShared/Hijacked back to direct ownership.
    /// Use case: Optimizing access when you know you're the only owner.
    /// </summary>
    Steal,

    /// <summary>
    /// snatch!() - Force ownership ignoring RC (danger! only).
    /// Forcibly takes ownership regardless of reference count.
    /// Creates Snatched wrapper indicating unsafe provenance.
    /// Use case: Emergency memory operations when safety guarantees are broken.
    /// </summary>
    Snatch,

    /// <summary>
    /// release!() - Decrement RC and invalidate this reference.
    /// Manually decrements reference count and marks this reference as invalid.
    /// Used for early cleanup when you're done with a reference.
    /// Use case: Manual memory optimization, early resource cleanup.
    /// </summary>
    Release,

    /// <summary>
    /// try_share!() - Attempt to upgrade weak to strong reference.
    /// Tries to convert Watched reference back to Shared reference.
    /// Can fail if the original object was already destroyed.
    /// Use case: Safe upgrade from observer to participant.
    /// </summary>
    TryShare,

    /// <summary>
    /// try_thread_share!() - Attempt to upgrade thread-weak to thread-strong.
    /// Tries to convert ThreadWatched reference back to ThreadShared.
    /// Thread-safe version of try_share!() with atomic operations.
    /// Use case: Safe thread-safe upgrade from observer to participant.
    /// </summary>
    TryThreadShare,

    /// <summary>
    /// reveal!() - Access snatched object (danger! only).
    /// Allows access to Snatched objects within danger! blocks.
    /// Does not change ownership, just provides access.
    /// Use case: Reading/using forcibly snatched objects safely.
    /// </summary>
    Reveal,

    /// <summary>
    /// own!() - Convert snatched to owned (danger! only).
    /// Converts Snatched wrapper back to clean Owned wrapper.
    /// Cleans up unsafe provenance and restores normal ownership.
    /// Use case: Legitimizing forcibly snatched objects after danger! block.
    /// </summary>
    Own
}

/// <summary>
/// Memory safety error record for tracking violations of the RazorForge memory model.
/// Each error contains the violation message, source location, and categorized error type
/// for precise error reporting and debugging assistance.
/// </summary>
/// <param name="Message">Human-readable error description</param>
/// <param name="Location">Source code location where the error occurred</param>
/// <param name="Type">Categorized error type for error handling and reporting</param>
public record MemoryError(
    string Message,
    SourceLocation Location,
    MemoryError.MemoryErrorType Type)
{
    /// <summary>
    /// Categories of memory safety violations that can occur in RazorForge.
    /// Each type corresponds to a different class of memory safety rule violation,
    /// allowing for targeted error messages and potential recovery strategies.
    /// </summary>
    public enum MemoryErrorType
    {
        /// <summary>
        /// Attempt to use an object that has been invalidated (deadref protection).
        /// Caused by using objects after memory operations invalidated them.
        /// Example: using 'obj' after 'obj.share!()' invalidated it.
        /// </summary>
        UseAfterInvalidation,

        /// <summary>
        /// Attempt to mix objects from different memory groups outside danger! blocks.
        /// Prevents unsafe aliasing by enforcing group separation.
        /// Example: trying to share a Hijacked object with a Shared object.
        /// </summary>
        MixedMemoryGroups,

        /// <summary>
        /// Invalid transformation between wrapper types.
        /// Caused by attempting disallowed wrapper type conversions.
        /// Example: trying to hijack an already-hijacked object.
        /// </summary>
        InvalidTransformation,

        /// <summary>
        /// Error when moving objects into containers.
        /// Caused by container operations on invalid objects or wrong container types.
        /// Example: pushing invalidated object into vector.
        /// </summary>
        ContainerMoveError,

        /// <summary>
        /// Violation of usurping function rules.
        /// Caused by non-usurping functions trying to return Hijacked&lt;T&gt;.
        /// Example: regular function returning exclusive token without usurping declaration.
        /// </summary>
        UsurpingViolation,

        /// <summary>
        /// Improper use of danger! block features.
        /// Caused by using danger!-only operations outside danger! blocks.
        /// Example: using snatch!() or reveal!() in safe code.
        /// </summary>
        DangerBlockViolation,

        /// <summary>
        /// Reference count constraint violation.
        /// Caused by operations that require specific RC values failing those constraints.
        /// Example: steal!() when RC > 1, or release!() when RC <= 1.
        /// </summary>
        ReferenceCountError,

        /// <summary>
        /// Thread safety violation.
        /// Caused by improper use of single-threaded objects across thread boundaries.
        /// Example: passing Shared&lt;T&gt; to another thread instead of ThreadShared&lt;T&gt;.
        /// </summary>
        ThreadSafetyViolation
    }
}

/// <summary>
/// Container semantics for memory management, defining how containers interact with
/// the memory model in terms of ownership transfer and reference management.
/// Different container types handle object insertion and removal differently
/// based on their memory management strategy.
/// </summary>
public enum ContainerSemantics
{
    /// <summary>
    /// Container owns contents with move semantics (RazorForge default).
    /// When objects are inserted, they are moved into the container and
    /// the source reference becomes invalid (deadref). This prevents
    /// external mutation after insertion, ensuring container controls access.
    /// Example: Vector&lt;Node&gt; takes ownership of nodes when push() is called.
    /// </summary>
    Owned,

    /// <summary>
    /// Container holds shared references with automatic RC management.
    /// Objects maintain their reference count and can be accessed from
    /// multiple locations. Container increments RC on insertion.
    /// Example: SharedVector&lt;Node&gt; shares references to nodes.
    /// </summary>
    Shared,

    /// <summary>
    /// Container holds thread-safe references with atomic RC management.
    /// Similar to Shared but with thread-safe atomic operations.
    /// Can be safely passed between threads while maintaining memory safety.
    /// Example: ThreadSafeVector&lt;Node&gt; for concurrent access patterns.
    /// </summary>
    ThreadSafe
}

/// <summary>
/// Function classification for memory operations, defining what types of
/// memory objects functions are allowed to return. This enforces the
/// usurping function rule that prevents accidental exclusive token leakage.
/// </summary>
public enum FunctionType
{
    /// <summary>
    /// Regular function - cannot return exclusive tokens.
    /// Most functions fall into this category and cannot return Hijacked&lt;T&gt;
    /// objects since that would violate exclusive access guarantees.
    /// Can return Owned, Shared, Watched, ThreadShared, ThreadWatched objects.
    /// Example: fn process_data(data: Node) -&gt; Node
    /// </summary>
    Regular,

    /// <summary>
    /// Usurping function - can return Hijacked&lt;T&gt; (exclusive tokens).
    /// Special functions explicitly marked as 'usurping' that are allowed
    /// to return exclusive access objects. Used for factory functions or
    /// specialized operations that need to provide exclusive access.
    /// Example: usurping fn create_exclusive() -&gt; Hijacked&lt;Node&gt;
    /// </summary>
    Usurping
}
