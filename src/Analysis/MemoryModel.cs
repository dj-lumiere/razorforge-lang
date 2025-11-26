using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Memory model support for RazorForge and Suflae languages.
/// Handles ownership tracking, wrapper types, and memory safety analysis.
///
/// RazorForge uses explicit memory management with 6 wrapper types organized into 3 color-coded groups:
/// <list type="bullet">
/// <item>Group 1 (Red): Exclusive borrowing - Viewed&lt;T&gt;, Hijacked&lt;T&gt;</item>
/// <item>Group 2 (Green/Brown): Single-threaded RC - Retained&lt;T&gt;, Tracked&lt;Retained&lt;T&gt;&gt;</item>
/// <item>Group 3 (Blue/Purple): Multi-threaded RC - Shared&lt;T, Policy&gt;, Tracked&lt;Shared&lt;T, Policy&gt;&gt;, Observed&lt;T&gt;, Seized&lt;T&gt;</item>
/// <item>Unsafe (Black): Forcibly taken - Snatched&lt;T&gt; (danger! blocks only)</item>
/// </list>
///
/// Suflae uses automatic reference counting with incremental garbage collection.
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

    /// <summary>
    /// Scoped read-only access (Viewed&lt;T&gt;). Created by 'viewing' statement.
    /// Provides temporary read-only access with source invalidation during scope.
    /// Copyable within scope (can pass to multiple functions).
    /// Mutations through this handle are compile errors.
    /// Source automatically restored when scope exits.
    /// </summary>
    Viewed,

    /// <summary>
    /// Thread-safe scoped read-only access (Observed&lt;T&gt;). Created by 'observing' statement.
    /// Acquires read lock on Shared&lt;T, MultiReadLock&gt; for duration of scope.
    /// Copyable within scope, multiple readers can coexist.
    /// Mutations through this handle are compile errors.
    /// Read lock automatically released when scope exits.
    /// </summary>
    Observed,

    /// <summary>
    /// Thread-safe scoped exclusive access (Seized&lt;T&gt;). Created by 'seizing' statement.
    /// Acquires exclusive lock on Shared&lt;T, Policy&gt; for duration of scope.
    /// NOT copyable - unique access only.
    /// Allows mutations (exclusive write access).
    /// Lock automatically released when scope exits.
    /// </summary>
    Seized,

    // Group 2 - Single-threaded Reference Counting (Green/Brown 🟢🟤)
    /// <summary>
    /// Single-threaded shared ownership with interior mutability (Retained&lt;T&gt;).
    /// Multiple references can exist (RC > 1). Provides shared mutable access
    /// through reference counting similar to Rc&lt;RefCell&lt;T&gt;&gt; in Rust.
    /// Created via retain() operation.
    /// </summary>
    Retained,

    /// <summary>
    /// Weak observer wrapper - can track either Retained or Shared.
    /// Single-threaded: Tracked&lt;Retained&lt;T&gt;&gt; - does not prevent object destruction
    /// Multi-threaded: Tracked&lt;Shared&lt;T, Policy&gt;&gt; - thread-safe weak reference
    /// Must use try_recover() to upgrade to strong reference:
    ///   - Tracked&lt;Retained&lt;T&gt;&gt; → Maybe&lt;Retained&lt;T&gt;&gt;
    ///   - Tracked&lt;Shared&lt;T, Policy&gt;&gt; → Maybe&lt;Shared&lt;T, Policy&gt;&gt;
    /// Created via track() operation on Retained&lt;T&gt; or Shared&lt;T, Policy&gt;.
    /// </summary>
    Tracked,

    // Group 3 - Thread-Safe Reference Counting (Blue/Purple 🔵🟣)
    /// <summary>
    /// Thread-safe shared ownership (Shared&lt;T, Policy&gt;).
    /// Multiple references across threads (Arc > 1). Provides thread-safe
    /// shared mutable access through atomic reference counting and locking policy.
    /// Created via share() operation with explicit policy (Mutex, MultiReadLock, or RejectEdit).
    /// </summary>
    Shared,

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
    /// Contains: Retained, Tracked. Allows multiple references within single thread.
    /// Uses RC (reference counting) similar to Rc&lt;RefCell&lt;T&gt;&gt; in Rust.
    /// Types: Retained&lt;T&gt;, Tracked&lt;Retained&lt;T&gt;&gt;
    /// </summary>
    SingleThreaded = 2,

    /// <summary>
    /// Group 3: Multi-threaded reference counting (Blue/Purple 🔵🟣).
    /// Contains: Shared, Observed, Seized. Thread-safe shared ownership.
    /// Note: Tracked can also belong to this group when tracking Shared&lt;T, Policy&gt;.
    /// Uses Arc (atomic reference counting) similar to Arc&lt;Mutex&lt;T&gt;&gt; or Arc&lt;RwLock&lt;T&gt;&gt; in Rust.
    /// Types: Shared&lt;T, Policy&gt;, Tracked&lt;Shared&lt;T, Policy&gt;&gt;, Observed&lt;T&gt;, Seized&lt;T&gt;
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
/// Locking policy for Shared&lt;T, Policy&gt; and Tracked&lt;Shared&lt;T, Policy&gt;&gt; types.
/// Determines the synchronization mechanism used for multi-threaded access.
/// This is a compile-time choice that affects which scoped access operations are allowed.
/// </summary>
public enum LockingPolicy
{
    /// <summary>
    /// Pure mutex (Arc&lt;Mutex&lt;T&gt;&gt;) - exclusive access only.
    /// Performance: ~15-30ns for lock acquisition.
    /// Allows: seizing only (exclusive write access)
    /// Disallows: observing (compile error)
    /// Use case: Write-heavy workloads
    /// </summary>
    Mutex,

    /// <summary>
    /// Reader-writer lock (Arc&lt;RwLock&lt;T&gt;&gt;) - supports concurrent reads.
    /// Performance: ~10-20ns for read lock, ~20-35ns for write lock.
    /// Allows: both seizing (exclusive write) and observing (shared read)
    /// Use case: Read-heavy workloads with occasional writes
    /// </summary>
    MultiReadLock
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
    /// Caused by: memory operations (retain, hijack!, etc.), scope exit, or explicit invalidation.
    /// Attempting to use invalidated objects results in compile-time error.
    /// </summary>
    Invalidated,

    /// <summary>
    /// Object moved into container (RazorForge) or RC'd (Suflae).
    /// In RazorForge: object becomes deadref after moving into container.
    /// In Suflae: object remains valid but RC is incremented.
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
/// <param name="Wrapper">Current wrapper type (Owned, Retained, Hijacked, Shared, etc.)</param>
/// <param name="State">Current object state (Valid, Invalidated, Moved, Dangerous)</param>
/// <param name="ReferenceCount">Current reference count (for Retained/Shared types)</param>
/// <param name="Location">Source location where object was declared</param>
/// <param name="InvalidatedBy">Reason for invalidation (for error reporting)</param>
/// <param name="Policy">Locking policy for Shared&lt;T, Policy&gt;/Tracked&lt;Shared&lt;T, Policy&gt;&gt; (null for other wrapper types)</param>
public record MemoryObject(
    string Name,
    TypeInfo BaseType,
    WrapperType Wrapper,
    ObjectState State,
    int ReferenceCount,
    SourceLocation Location,
    string? InvalidatedBy = null,
    LockingPolicy? Policy = null)
{
    /// <summary>
    /// Get the memory group for this wrapper type.
    /// Used to enforce the core rule: cannot mix operations between different groups.
    /// Owned objects can become any group, but once transformed, they're locked to that group.
    /// Note: Tracked can belong to either SingleThreaded or MultiThreaded group depending on
    /// what it's tracking (Retained vs Shared), determined by Policy presence.
    /// </summary>
    public MemoryGroup Group => Wrapper switch
    {
        // Owned can become any group - it's the starting point
        WrapperType.Owned => MemoryGroup.Exclusive,

        // Group 1: Exclusive access and scoped borrows
        WrapperType.Hijacked or WrapperType.Viewed => MemoryGroup.Exclusive,

        // Group 2: Single-threaded reference counting
        WrapperType.Retained => MemoryGroup.SingleThreaded,

        // Tracked belongs to Group 2 if tracking Retained (no Policy), Group 3 if tracking Shared (has Policy)
        WrapperType.Tracked => Policy == null
            ? MemoryGroup.SingleThreaded
            : MemoryGroup.MultiThreaded,

        // Group 3: Multi-threaded reference counting and scoped locks
        WrapperType.Shared or WrapperType.Observed or WrapperType.Seized => MemoryGroup
           .MultiThreaded,

        // Unsafe: Danger zone
        WrapperType.Snatched => MemoryGroup.Unsafe,

        _ => throw new ArgumentException(message: $"Unknown wrapper type: {Wrapper}")
    };

    /// <summary>
    /// Check if this wrapper type is read-only (prevents mutations).
    /// Read-only wrappers allow reading but prevent field/index assignment.
    /// </summary>
    /// <returns>True if wrapper is read-only, false if mutable</returns>
    public bool IsReadOnly()
    {
        return Wrapper switch
        {
            WrapperType.Viewed => true, // viewing X as v { } - read-only
            WrapperType.Observed => true, // observing X as o { } - read-only lock
            _ => false // All others allow mutation
        };
    }

    /// <summary>
    /// Check if this object can be transformed to the target wrapper type.
    /// Implements the core transformation rules of the RazorForge memory model:
    /// 1. Objects must be Valid (unless in danger block)
    /// 2. Cannot mix between memory groups (except in danger block)
    /// 3. Some transformations increment RC rather than invalidating
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
            // Creating multiple Retained references increments RC
            (WrapperType.Retained, WrapperType.Retained) => true,
            // Create weak reference from Retained (doesn't invalidate source)
            (WrapperType.Retained, WrapperType.Tracked) => true,
            // Upgrade weak to strong via try_recover() (if object still exists)
            (WrapperType.Tracked, WrapperType.Retained) => true,
            // Multiple weak references are allowed
            (WrapperType.Tracked, WrapperType.Tracked) => true,

            // === Within Group 3 (Multi-threaded RC) ===
            // Creating multiple Shared references increments Arc
            (WrapperType.Shared, WrapperType.Shared) => true,
            // Create weak reference from Shared
            (WrapperType.Shared, WrapperType.Tracked) => true,
            // Upgrade weak to strong via try_recover()
            (WrapperType.Tracked, WrapperType.Shared) => true,

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
    /// Note: Tracked can belong to either Group 2 or Group 3, but this method
    /// cannot determine which without Policy info. Callers should use the Group property instead.
    /// </summary>
    /// <param name="wrapper">The wrapper type to get group for</param>
    /// <returns>The memory group this wrapper belongs to</returns>
    private static MemoryGroup GetGroup(WrapperType wrapper)
    {
        return wrapper switch
        {
            // Group 1: Exclusive access and scoped borrows
            WrapperType.Owned => MemoryGroup.Exclusive,
            WrapperType.Hijacked or WrapperType.Viewed => MemoryGroup.Exclusive,

            // Group 2: Single-threaded RC
            WrapperType.Retained => MemoryGroup.SingleThreaded,

            // Tracked: Ambiguous without Policy - default to SingleThreaded
            // (Callers should use instance Group property for accurate determination)
            WrapperType.Tracked => MemoryGroup.SingleThreaded,

            // Group 3: Multi-threaded RC and scoped locks
            WrapperType.Shared or WrapperType.Observed or WrapperType.Seized => MemoryGroup
               .MultiThreaded,

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
/// <item>Group Transformations: retain(), share() - change wrapper type</item>
/// <item>Weak References: track() - create non-owning references</item>
/// <item>RC Management: release!() - manually decrement reference count</item>
/// <item>Weak Upgrades: try_recover() - upgrade weak to strong (can fail)</item>
/// <item>Unsafe Operations: snatch!(), reveal!(), own!() - danger! block only</item>
/// </list>
/// </summary>
public enum MemoryOperation
{
    /// <summary>
    /// retain() - Transform to single-threaded shared ownership (Retained&lt;T&gt;).
    /// Creates reference-counted shared access with interior mutability.
    /// Multiple Retained references can coexist, each with RC tracking.
    /// Use case: When multiple references within single thread need mutable access.
    /// </summary>
    Retain,

    /// <summary>
    /// share() - Transform to thread-safe shared ownership (Shared&lt;T, Policy&gt;).
    /// Creates atomic reference-counted shared access across threads.
    /// Requires explicit locking policy (Mutex, MultiReadLock, or RejectEdit).
    /// Use case: Sharing mutable objects across thread boundaries.
    /// </summary>
    Share,

    /// <summary>
    /// track() - Create weak reference (Tracked&lt;Retained&lt;T&gt;&gt; or Tracked&lt;Shared&lt;T, Policy&gt;&gt;).
    /// Creates observer reference that doesn't prevent object destruction.
    /// Weak references don't contribute to reference count (RC = 0).
    /// Can be used with both Retained (single-threaded) and Shared (multi-threaded).
    /// Use case: Breaking cycles, observing without ownership responsibility.
    /// </summary>
    Track,

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
    /// recover!() / try_recover() - Upgrade weak to strong reference.
    /// Converts Tracked reference back to its strong form:
    /// <list type="bullet">
    /// <item>recover!(): Tracked&lt;Retained&lt;T&gt;&gt; → Retained&lt;T&gt; (crashes if deallocated)</item>
    /// <item>try_recover(): Tracked&lt;Retained&lt;T&gt;&gt; → Maybe&lt;Retained&lt;T&gt;&gt; (returns None if deallocated)</item>
    /// <item>recover!(): Tracked&lt;Shared&lt;T, Policy&gt;&gt; → Shared&lt;T, Policy&gt; (crashes if deallocated)</item>
    /// <item>try_recover(): Tracked&lt;Shared&lt;T, Policy&gt;&gt; → Maybe&lt;Shared&lt;T, Policy&gt;&gt; (returns None if deallocated)</item>
    /// </list>
    /// Use case: Upgrade from observer to participant (try_ variant is safer).
    /// </summary>
    Recover,

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
        /// Example: using 'obj' after 'obj.retain()' invalidated it.
        /// </summary>
        UseAfterInvalidation,

        /// <summary>
        /// Attempt to mix objects from different memory groups outside danger! blocks.
        /// Prevents unsafe aliasing by enforcing group separation.
        /// Example: trying to share a Retained object with a Shared object.
        /// </summary>
        MixedMemoryGroups,

        /// <summary>
        /// Invalid transformation between wrapper types.
        /// Caused by attempting disallowed wrapper type conversions.
        /// Example: trying to create hijacking statement on an already-hijacked object.
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
        /// Caused by non-usurping functions trying to return tokens from scoped statements.
        /// Example: regular function returning Hijacked&lt;T&gt; token without usurping declaration.
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
        /// Example: release!() when RC &lt;= 1.
        /// </summary>
        ReferenceCountError,

        /// <summary>
        /// Thread safety violation.
        /// Caused by improper use of single-threaded objects across thread boundaries.
        /// Example: passing Retained&lt;T&gt; to another thread instead of Shared&lt;T, Policy&gt;.
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
    /// Container holds single-threaded shared references with automatic RC management.
    /// Objects maintain their reference count and can be accessed from
    /// multiple locations within the same thread. Container increments RC on insertion.
    /// Example: RetainedVector&lt;Node&gt; shares Retained references to nodes.
    /// </summary>
    Retained,

    /// <summary>
    /// Container holds thread-safe references with atomic RC management.
    /// Similar to Retained but with thread-safe atomic operations.
    /// Can be safely passed between threads while maintaining memory safety.
    /// Example: SharedVector&lt;Node&gt; for concurrent access patterns with Shared&lt;Node, Policy&gt;.
    /// </summary>
    Shared
}

/// <summary>
/// Function classification for memory operations, defining what types of
/// memory objects functions are allowed to return. This enforces the
/// usurping function rule that prevents accidental exclusive token leakage.
/// </summary>
public enum FunctionType
{
    /// <summary>
    /// Regular function - cannot return scoped statement tokens.
    /// Most functions fall into this category and cannot return Hijacked&lt;T&gt;,
    /// Viewed&lt;T&gt;, Observed&lt;T&gt;, or Seized&lt;T&gt; since these are scoped tokens.
    /// Can return Owned, Retained, Tracked, Shared objects.
    /// Example: routine process_data(data: Node) → Node
    /// </summary>
    Regular,

    /// <summary>
    /// Usurping function - can return scoped tokens (Hijacked&lt;T&gt;, etc.).
    /// Special functions explicitly marked as 'usurping' that are allowed
    /// to return tokens from scoped statements. Used for factory functions or
    /// specialized operations that need to provide scoped access.
    /// Example: usurping public routine __create___exclusive() → Hijacked&lt;Node&gt;
    /// </summary>
    Usurping
}
