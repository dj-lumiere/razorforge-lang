using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Memory safety analyzer for RazorForge and Suflae memory models.
///
/// This analyzer tracks object ownership, validates memory operations, and enforces
/// memory safety rules throughout the compilation process. It handles the fundamental
/// differences between RazorForge's explicit memory management and Suflae's automatic
/// reference counting with incremental garbage collection.
///
/// Key responsibilities:
/// <list type="bullet">
/// <item>Track object lifetime and wrapper type transformations</item>
/// <item>Validate memory operation method calls (hijack!, share!, etc.)</item>
/// <item>Enforce memory group separation rules (no mixing between groups)</item>
/// <item>Detect use-after-invalidation errors (deadref protection)</item>
/// <item>Handle scope-based invalidation and cleanup</item>
/// <item>Manage danger! block exceptions and usurping function rules</item>
/// <item>Simulate reference counting for steal!() validation</item>
/// </list>
///
/// The analyzer maintains a stack of scopes with object tracking, allowing for
/// proper invalidation when objects go out of scope or are transformed by
/// memory operations.
/// </summary>
public class MemoryAnalyzer
{
    /// <summary>Target language (RazorForge or Suflae) - determines memory model behavior</summary>
    private readonly Language _language;

    /// <summary>Language mode for additional behavior customization</summary>
    private readonly LanguageMode _mode;

    /// <summary>
    /// Current objects in all scopes (flattened view for quick lookup).
    /// Maps variable names to their current memory object state.
    /// </summary>
    private readonly Dictionary<string, MemoryObject> _objects = new();

    /// <summary>
    /// Stack of scopes for proper lexical scoping and cleanup.
    /// Each scope contains objects declared within that scope.
    /// When a scope exits, all its objects become invalid.
    /// </summary>
    private readonly Stack<Dictionary<string, MemoryObject>> _scopes = new();

    /// <summary>
    /// Whether we're currently inside a danger! block.
    /// Danger blocks allow unsafe operations that would normally be forbidden,
    /// such as snatch!(), mixed memory groups, and access to invalidated objects.
    /// </summary>
    private bool _inDangerBlock = false;

    /// <summary>
    /// Whether we're currently inside a usurping function.
    /// Usurping functions are allowed to return Hijacked&lt;T&gt; objects,
    /// while regular functions cannot return exclusive tokens.
    /// </summary>
    private bool _inUsurpingFunction = false;

    /// <summary>
    /// Initialize memory analyzer for the specified language and mode.
    /// Creates the global scope and sets up language-specific behavior.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Suflae)</param>
    /// <param name="mode">Language mode for additional customization</param>
    public MemoryAnalyzer(Language language, LanguageMode mode)
    {
        _language = language;
        _mode = mode;
        // Initialize with global scope
        _scopes.Push(item: new Dictionary<string, MemoryObject>());
    }

    /// <summary>
    /// Get all memory safety errors detected during analysis.
    /// Used by the compiler to report memory safety violations to the user.
    /// </summary>
    public List<MemoryError> Errors { get; } = [];

    /// <summary>
    /// Enter a new lexical scope (function, block, loop, etc.).
    /// Creates a new scope level for tracking objects declared within this scope.
    /// Objects declared in this scope will be automatically invalidated when
    /// the scope is exited, preventing use-after-scope errors.
    /// </summary>
    public void EnterScope()
    {
        _scopes.Push(item: new Dictionary<string, MemoryObject>());
    }

    /// <summary>
    /// Exit the current lexical scope, invalidating all objects declared within it.
    /// This implements automatic scope-based cleanup where objects become deadref
    /// when they go out of scope. Essential for preventing use-after-scope errors.
    /// The global scope cannot be exited (minimum scope depth of 1).
    /// </summary>
    public void ExitScope()
    {
        if (_scopes.Count <= 1)
        {
            return;
        }

        Dictionary<string, MemoryObject> scope = _scopes.Pop();
        // All objects in this scope become invalid (go out of scope)
        // This is a fundamental safety mechanism preventing access to
        // objects that have been destroyed by scope exit
        foreach (MemoryObject obj in scope.Values)
        {
            InvalidateObject(name: obj.Name, reason: "scope end", location: obj.Location);
        }
    }

    /// <summary>
    /// Enter a danger! block where unsafe operations are permitted.
    /// Danger blocks are RazorForge-only and allow breaking normal safety rules
    /// for emergency situations. Operations like snatch!(), mixed memory groups,
    /// and access to invalidated objects become legal within danger blocks.
    /// </summary>
    /// <param name="location">Source location of the danger! block for error reporting</param>
    public void EnterDangerBlock(SourceLocation location)
    {
        if (_language == Language.Suflae)
        {
            // Suflae does not have danger blocks - it uses automatic memory management
            // and doesn't expose unsafe operations to the programmer
            AddError(message: "Danger blocks are not allowed in Suflae", location: location,
                type: MemoryError.MemoryErrorType.DangerBlockViolation);
            return;
        }

        _inDangerBlock = true;
    }

    /// <summary>
    /// Exit a danger! block, returning to normal memory safety rules.
    /// All unsafe operations become forbidden again after exiting the danger block.
    /// </summary>
    public void ExitDangerBlock()
    {
        _inDangerBlock = false;
    }

    /// <summary>
    /// Enter a usurping function allowed to return exclusive tokens.
    /// Usurping functions are special RazorForge functions explicitly marked
    /// as being able to return Hijacked&lt;T&gt; objects. This prevents accidental
    /// exclusive token leakage from regular functions.
    /// </summary>
    public void EnterUsurpingFunction()
    {
        if (_language == Language.Suflae)
        {
            // Suflae doesn't have usurping functions since it uses automatic
            // reference counting and doesn't expose exclusive access tokens
            return;
        }

        _inUsurpingFunction = true;
    }

    /// <summary>
    /// Exit a usurping function, returning to regular function rules.
    /// The function can no longer return Hijacked&lt;T&gt; objects.
    /// </summary>
    public void ExitUsurpingFunction()
    {
        _inUsurpingFunction = false;
    }

    /// <summary>
    /// Register a new object from a variable declaration or function return.
    /// This creates a new memory object with the appropriate initial wrapper type
    /// based on the target language's memory model.
    ///
    /// RazorForge: Objects start as Owned with RC=1 (direct ownership)
    /// Suflae: Objects start as Shared with automatic RC management
    /// </summary>
    /// <param name="name">Variable name for the object</param>
    /// <param name="type">Type information for the object</param>
    /// <param name="location">Source location for error reporting</param>
    /// <param name="isFloating">Whether this is a floating literal/temporary (Suflae only)</param>
    public void RegisterObject(string name, TypeInfo type, SourceLocation location,
        bool isFloating = false)
    {
        // Different default wrapper types based on a language memory model
        WrapperType wrapper = _language == Language.Suflae
            ? WrapperType.Shared
            : WrapperType.Owned;
        var obj = new MemoryObject(Name: name, BaseType: type, Wrapper: wrapper,
            State: ObjectState.Valid, ReferenceCount: 1, Location: location);

        Dictionary<string, MemoryObject> currentScope = _scopes.Peek();
        currentScope[key: name] = obj;

        // Suflae automatic reference counting: increment RC when creating non-floating references
        if (_language != Language.Suflae || isFloating)
        {
            return;
        }

        // Simulate automatic RC increment for assignment to variables
        // Floating literals don't get RC increment until assigned
        obj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
        currentScope[key: name] = obj;
    }

    /// <summary>
    /// Handle assignment in Suflae with automatic reference counting.
    /// When assigning one reference to another in Suflae, the reference count
    /// is automatically incremented for both the source and target objects.
    /// This simulates Suflae's automatic RC management during compilation.
    ///
    /// Example: let b = a  // RC of 'a' object increases, 'b' points to the same object
    /// </summary>
    /// <param name="target">Target variable name receiving the assignment</param>
    /// <param name="source">Source variable name being assigned from</param>
    /// <param name="location">Source location for error reporting</param>
    public void HandleSuflaeAssignment(string target, string source, SourceLocation location)
    {
        // This method only applies to Suflae's automatic RC model
        if (_language != Language.Suflae)
        {
            return;
        }

        MemoryObject? sourceObj = GetObject(name: source);
        if (sourceObj == null)
        {
            AddError(message: $"Source object '{source}' not found", location: location,
                type: MemoryError.MemoryErrorType.UseAfterInvalidation);
            return;
        }

        if (sourceObj.State != ObjectState.Valid)
        {
            AddError(message: $"Cannot assign from invalidated object '{source}'",
                location: location, type: MemoryError.MemoryErrorType.UseAfterInvalidation);
            return;
        }

        // Suflae automatic RC management: both source and target share the same object
        // with incremented reference count
        MemoryObject newObj = sourceObj with
        {
            Name = target, ReferenceCount = sourceObj.ReferenceCount + 1
        };

        // Create new reference for target
        SetObject(name: target, obj: newObj);

        // Update source object RC to reflect the new reference
        SetObject(name: source,
            obj: sourceObj with { ReferenceCount = sourceObj.ReferenceCount + 1 });
    }

    /// <summary>
    /// Handle memory operation method call from RazorForge source code.
    /// These are the core memory transformation operations like obj.share!(), obj.hijack!(), etc.
    /// Each operation validates the current object state, checks transformation rules,
    /// and returns the new memory object state after the operation.
    ///
    /// The method enforces key safety rules:
    /// <list type="bullet">
    /// <item>Objects must be valid (unless in danger! block)</item>
    /// <item>Transformations must follow wrapper type compatibility rules</item>
    /// <item>Memory group mixing is forbidden (except in danger! blocks)</item>
    /// <item>Reference count constraints must be satisfied</item>
    /// </list>
    /// </summary>
    /// <param name="objectName">Name of the object being operated on</param>
    /// <param name="operation">The memory operation being performed</param>
    /// <param name="location">Source location for error reporting</param>
    /// <param name="policy">Locking policy for thread_share!() operation (Mutex or MultiReadLock)</param>
    /// <returns>New memory object state after operation, or null if operation failed</returns>
    public MemoryObject? HandleMemoryOperation(string objectName, MemoryOperation operation,
        SourceLocation location, LockingPolicy? policy = null)
    {
        MemoryObject? obj = GetObject(name: objectName);
        if (obj == null)
        {
            AddError(message: $"Object '{objectName}' not found", location: location,
                type: MemoryError.MemoryErrorType.UseAfterInvalidation);
            return null;
        }

        // Core safety rule: cannot operate on invalidated objects (unless in danger! block)
        if (obj.State != ObjectState.Valid && !_inDangerBlock)
        {
            AddError(
                message:
                $"Cannot perform {operation} on invalidated object '{objectName}' (invalidated by: {obj.InvalidatedBy})",
                location: location, type: MemoryError.MemoryErrorType.UseAfterInvalidation);
            return null;
        }

        // Dispatch to specific operation handlers
        return operation switch
        {
            MemoryOperation.Retain => HandleRetain(obj: obj, location: location),
            MemoryOperation.Share => HandleShare(obj: obj, location: location,
                policy: policy ?? LockingPolicy.Mutex),
            MemoryOperation.Track => HandleTrack(obj: obj, location: location, policy: policy),
            MemoryOperation.Steal => HandleSteal(obj: obj, location: location),
            MemoryOperation.Snatch => HandleSnatch(obj: obj, location: location),
            MemoryOperation.Release => HandleRelease(obj: obj, location: location),
            MemoryOperation.Recover => HandleRecover(obj: obj, location: location),
            MemoryOperation.Reveal => HandleReveal(obj: obj, location: location),
            MemoryOperation.Own => HandleOwn(obj: obj, location: location),
            _ => throw new ArgumentException(message: $"Unknown memory operation: {operation}")
        };
    }

    /// <summary>
    /// Handle hijack!() operation - transform object to exclusive access.
    /// Creates a Hijacked&lt;T&gt; wrapper that provides exclusive mutable access.
    /// Only one hijacked reference can exist at a time (exclusive ownership).
    /// The source object is invalidated to enforce exclusivity.
    /// </summary>
    private MemoryObject? HandleHijack(MemoryObject obj, SourceLocation location)
    {
        if (!obj.CanTransformTo(target: WrapperType.Hijacked, inDangerBlock: _inDangerBlock))
        {
            AddError(message: $"Cannot hijack object of type {obj.Wrapper}", location: location,
                type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Invalidate source to enforce exclusive access - no other references allowed
        InvalidateObject(name: obj.Name, reason: "hijack!()", location: location);

        // Return a hijacked object with exclusive access
        return obj with
        {
            Wrapper = WrapperType.Hijacked, ReferenceCount = 1 // Always 1 for exclusive access
        };
    }

    /// <summary>
    /// Handle retain!() operation - transform to single-threaded shared ownership with reference counting.
    /// Creates a Retained&lt;T&gt; wrapper that allows multiple mutable references with RC tracking.
    /// If already retained, increments reference count. Otherwise converts and invalidates source.
    /// </summary>
    private MemoryObject? HandleRetain(MemoryObject obj, SourceLocation location)
    {
        if (!obj.CanTransformTo(target: WrapperType.Retained, inDangerBlock: _inDangerBlock))
        {
            AddError(message: $"Cannot retain object of type {obj.Wrapper}", location: location,
                type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (obj.Wrapper == WrapperType.Retained)
        {
            // Already retained - increment reference count for new reference
            MemoryObject newObj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            SetObject(name: obj.Name, obj: newObj);
            return newObj;
        }

        // Convert to single-threaded shared ownership, invalidate source (one-time transformation)
        InvalidateObject(name: obj.Name, reason: "retain!()", location: location);

        return obj with
        {
            Wrapper = WrapperType.Retained, ReferenceCount = 1 // First retained reference
        };
    }

    /// <summary>
    /// Handle track!() operation - create a weak observer reference.
    /// Creates a Tracked&lt;Retained&lt;T&gt;&gt; or Tracked&lt;Shared&lt;T, Policy&gt;&gt; weak reference.
    /// Can be created from Retained (single-threaded) or Shared (multi-threaded) objects.
    /// Doesn't invalidate source or affect RC. Used for breaking reference cycles.
    /// </summary>
    private MemoryObject? HandleTrack(MemoryObject obj, SourceLocation location, LockingPolicy? policy)
    {
        // Create a weak reference - doesn't invalidate source or affect its RC
        if (obj.Wrapper == WrapperType.Retained)
        {
            return obj with
            {
                Wrapper = WrapperType.Tracked,
                ReferenceCount = 0, // Weak references don't contribute to RC
                Policy = null // Single-threaded, no policy
            };
        }

        if (obj.Wrapper == WrapperType.Shared)
        {
            return obj with
            {
                Wrapper = WrapperType.Tracked,
                ReferenceCount = 0, // Weak references don't contribute to Arc
                Policy = obj.Policy // Inherit policy from parent Shared
            };
        }

        AddError(message: $"Can only track Retained or Shared objects, not {obj.Wrapper}",
            location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
        return null;
    }

    /// <summary>
    /// Handle share!() operation - transform to thread-safe shared ownership.
    /// Creates a Shared&lt;T, Policy&gt; wrapper with atomic reference counting (Arc).
    /// Safe to pass between threads. If already shared, increments Arc count.
    /// Policy determines synchronization: Mutex (Arc&lt;Mutex&lt;T&gt;&gt;), MultiReadLock (Arc&lt;RwLock&lt;T&gt;&gt;), or RejectEdit (Arc&lt;T&gt;).
    /// </summary>
    /// <param name="policy">Locking policy (Mutex, MultiReadLock, or RejectEdit)</param>
    private MemoryObject? HandleShare(MemoryObject obj, SourceLocation location,
        LockingPolicy policy)
    {
        if (!obj.CanTransformTo(target: WrapperType.Shared, inDangerBlock: _inDangerBlock))
        {
            AddError(message: $"Cannot share object of type {obj.Wrapper}",
                location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (obj.Wrapper == WrapperType.Shared)
        {
            // Already shared - verify policy matches and increment atomic reference count
            if (obj.Policy != policy)
            {
                AddError(
                    message:
                    $"Cannot change locking policy from {obj.Policy} to {policy} on existing Shared object",
                    location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
                return null;
            }

            MemoryObject newObj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            SetObject(name: obj.Name, obj: newObj);
            return newObj;
        }

        // Convert to thread-safe shared ownership, invalidate source
        InvalidateObject(name: obj.Name, reason: "share!()", location: location);

        return obj with
        {
            Wrapper = WrapperType.Shared,
            ReferenceCount = 1, // First shared reference
            Policy = policy // Store the locking policy
        };
    }

    /// <summary>
    /// Handle steal!() operation - reclaim direct ownership from RC'd objects.
    /// Converts Retained/Shared back to direct Owned wrapper.
    /// Only works when you're the sole owner (RC=1 for RC'd objects).
    /// Used for optimization when you know you're the only reference holder.
    /// </summary>
    private MemoryObject? HandleSteal(MemoryObject obj, SourceLocation location)
    {
        // Reference count validation for Retained objects
        if (obj.Wrapper == WrapperType.Retained && obj.ReferenceCount != 1)
        {
            AddError(
                message:
                $"Cannot steal from Retained object with RC={obj.ReferenceCount} (need RC=1)",
                location: location, type: MemoryError.MemoryErrorType.ReferenceCountError);
            return null;
        }

        if (obj.Wrapper == WrapperType.Shared && obj.ReferenceCount != 1)
        {
            AddError(
                message:
                $"Cannot steal from Shared object with Arc={obj.ReferenceCount} (need Arc=1)",
                location: location, type: MemoryError.MemoryErrorType.ReferenceCountError);
            return null;
        }

        // Can only steal from RC'd objects
        if (obj.Wrapper != WrapperType.Retained && obj.Wrapper != WrapperType.Shared)
        {
            AddError(message: $"Cannot steal from {obj.Wrapper}", location: location,
                type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Invalidate source reference, return direct ownership
        InvalidateObject(name: obj.Name, reason: "steal!()", location: location);

        return obj with
        {
            Wrapper = WrapperType.Owned, ReferenceCount = 1 // Direct ownership always has RC=1
        };
    }

    /// <summary>
    /// Handle snatch!() operation - forcibly take ownership (danger! only).
    /// Creates a Snatched&lt;T&gt; wrapper indicating unsafe provenance.
    /// Ignores all normal safety rules including reference counts.
    /// Only legal within danger! blocks for emergency memory operations.
    /// </summary>
    private MemoryObject? HandleSnatch(MemoryObject obj, SourceLocation location)
    {
        if (!_inDangerBlock)
        {
            AddError(message: "snatch!() can only be used in danger! blocks", location: location,
                type: MemoryError.MemoryErrorType.DangerBlockViolation);
            return null;
        }

        // Forcibly take ownership regardless of current state or RC
        InvalidateObject(name: obj.Name, reason: "snatch!()", location: location);

        return obj with
        {
            Wrapper = WrapperType.Snatched, // Marks contaminated ownership
            ReferenceCount = 1
        };
    }

    /// <summary>
    /// Handle release!() operation - manually decrement reference count.
    /// Decrements RC/Arc and invalidates this reference for early cleanup.
    /// Only works on Shared/ThreadShared objects with RC > 1.
    /// Used for manual memory optimization when done with a reference early.
    /// </summary>
    private MemoryObject? HandleRelease(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.Retained && obj.Wrapper != WrapperType.Shared)
        {
            AddError(
                message: $"Can only release Retained or Shared objects, not {obj.Wrapper}",
                location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (obj.ReferenceCount <= 1)
        {
            AddError(
                message: $"Cannot release object with RC={obj.ReferenceCount} (would drop to 0)",
                location: location, type: MemoryError.MemoryErrorType.ReferenceCountError);
            return null;
        }

        // Invalidate this reference and decrease the overall reference count
        InvalidateObject(name: obj.Name, reason: "release!()", location: location);

        return obj with { ReferenceCount = obj.ReferenceCount - 1 };
    }

    /// <summary>
    /// Handle recover!() / try_recover() operation - upgrade weak to strong reference.
    /// Tries to convert Tracked reference back to its strong form:
    ///   - Tracked&lt;Retained&lt;T&gt;&gt; → Retained&lt;T&gt;
    ///   - Tracked&lt;Shared&lt;T, Policy&gt;&gt; → Shared&lt;T, Policy&gt;
    /// In a real implementation, this could fail if the object was destroyed.
    /// For analysis purposes, we assume success but real runtime may fail (try_recover returns Maybe).
    /// </summary>
    private MemoryObject? HandleRecover(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.Tracked)
        {
            AddError(
                message: $"Can only recover Tracked objects, not {obj.Wrapper}",
                location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Try to upgrade weak to strong - in real implementation this could fail
        // if the original object was already destroyed
        // Determine target type based on Policy (null = Retained, non-null = Shared)
        WrapperType targetWrapper = obj.Policy == null ? WrapperType.Retained : WrapperType.Shared;

        return obj with
        {
            Wrapper = targetWrapper,
            ReferenceCount = 1, // New strong reference
            Policy = obj.Policy // Preserve policy for Shared, null for Retained
        };
    }

    /// <summary>
    /// Handle reveal!() operation - access snatched objects safely (danger! only).
    /// Allows reading/using Snatched objects within danger! blocks.
    /// Does not change ownership, just provides safe access to contaminated objects.
    /// Used for accessing forcibly snatched objects in emergency code.
    /// </summary>
    private MemoryObject? HandleReveal(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.Snatched)
        {
            AddError(message: $"Can only reveal Snatched objects, not {obj.Wrapper}",
                location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (!_inDangerBlock)
        {
            AddError(message: "reveal!() can only be used in danger! blocks", location: location,
                type: MemoryError.MemoryErrorType.DangerBlockViolation);
            return null;
        }

        // Convert snatched to owned - cleaning up contaminated provenance
        return obj with { Wrapper = WrapperType.Owned };
    }

    /// <summary>
    /// Handle own!() operation - legitimize snatched objects (danger! only).
    /// Converts Snatched wrapper back to clean Owned wrapper.
    /// Cleans up unsafe provenance and restores normal ownership semantics.
    /// Invalidates the source snatched reference to prevent double-use.
    /// </summary>
    private MemoryObject? HandleOwn(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.Snatched)
        {
            AddError(message: $"Can only own Snatched objects, not {obj.Wrapper}",
                location: location, type: MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (!_inDangerBlock)
        {
            AddError(message: "own!() can only be used in danger! blocks", location: location,
                type: MemoryError.MemoryErrorType.DangerBlockViolation);
            return null;
        }

        // Convert snatched to owned and invalidate source to prevent reuse
        InvalidateObject(name: obj.Name, reason: "own!()", location: location);

        return obj with { Wrapper = WrapperType.Owned };
    }

    /// <summary>
    /// Handle container operations like push(), insert(), add() that move objects into containers.
    ///
    /// RazorForge: Uses move semantics - object is transferred to container and source becomes deadref.
    /// This prevents external mutation after insertion, ensuring container controls access.
    ///
    /// Suflae: Uses automatic reference counting - container shares reference with automatic RC increment.
    /// Original reference remains valid and can be used alongside container reference.
    ///
    /// This fundamental difference reflects the memory model philosophies of each language.
    /// </summary>
    /// <param name="objectName">Name of object being moved into container</param>
    /// <param name="containerName">Name of container for error reporting</param>
    /// <param name="location">Source location for error reporting</param>
    public void HandleContainerMove(string objectName, string containerName,
        SourceLocation location)
    {
        MemoryObject? obj = GetObject(name: objectName);
        if (obj == null)
        {
            AddError(message: $"Object '{objectName}' not found", location: location,
                type: MemoryError.MemoryErrorType.UseAfterInvalidation);
            return;
        }

        if (obj.State != ObjectState.Valid)
        {
            AddError(message: $"Cannot move invalidated object '{objectName}' into container",
                location: location, type: MemoryError.MemoryErrorType.ContainerMoveError);
            return;
        }

        // Language-specific container semantics
        if (_language == Language.RazorForge)
        {
            // RazorForge: move semantics - invalidate source after moving to container
            InvalidateObject(name: objectName, reason: $"moved into container '{containerName}'",
                location: location);
        }
        else if (_language == Language.Suflae)
        {
            // Suflae: automatic reference counting - container shares reference
            MemoryObject newObj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            SetObject(name: objectName, obj: newObj);
        }
    }

    /// <summary>
    /// Validate function return type against usurping function rules.
    /// Regular functions cannot return exclusive tokens (Hijacked&lt;T&gt;) because
    /// that would violate exclusive access guarantees. Only functions explicitly
    /// marked as 'usurping' are allowed to return exclusive tokens.
    ///
    /// This prevents accidental exclusive token leakage from normal functions.
    /// </summary>
    /// <param name="returnType">The function's return type to validate</param>
    /// <param name="location">Source location for error reporting</param>
    public void ValidateFunctionReturn(TypeInfo returnType, SourceLocation location)
    {
        if (_language == Language.Suflae)
        {
            return; // Suflae has no usurping functions
        }

        // Check if return type is Hijacked<T> without usurping declaration
        if (IsHijackedType(type: returnType) && !_inUsurpingFunction)
        {
            AddError(message: "Only usurping functions can return Hijacked<T>", location: location,
                type: MemoryError.MemoryErrorType.UsurpingViolation);
        }
    }

    /// <summary>
    /// Check if a type is a Hijacked&lt;T&gt; wrapper type.
    /// This is used to enforce usurping function rules - only usurping functions
    /// can return exclusive tokens. Currently uses simplified name-based checking.
    /// In a full implementation, this would do proper type analysis.
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>True if type is Hijacked&lt;T&gt;, false otherwise</returns>
    private bool IsHijackedType(TypeInfo type)
    {
        // Simplified check based on type name - in full implementation
        // would properly analyze generic type structure
        return type.Name.StartsWith(value: "Hijacked<");
    }

    /// <summary>
    /// Look up a memory object by variable name across all scopes.
    /// Searches from innermost to outermost scope following lexical scoping rules.
    /// Returns the first matching object found, or null if not found.
    /// </summary>
    /// <param name="name">Variable name to look up</param>
    /// <returns>Memory object for the variable, or null if not found</returns>
    public MemoryObject? GetObject(string name)
    {
        // Search from innermost to outermost scope
        foreach (Dictionary<string, MemoryObject> scope in _scopes)
        {
            if (scope.TryGetValue(key: name, value: out MemoryObject? obj))
            {
                return obj;
            }
        }

        return null;
    }

    /// <summary>
    /// Update a memory object by variable name, searching through scopes.
    /// Updates the object in the scope where it was originally declared.
    /// If not found in any scope, adds to the current (innermost) scope.
    /// This maintains proper lexical scoping for object updates.
    /// </summary>
    /// <param name="name">Variable name to update</param>
    /// <param name="obj">New memory object state</param>
    private void SetObject(string name, MemoryObject obj)
    {
        // Find the scope where this object was declared and update it there
        foreach (Dictionary<string, MemoryObject> scope in _scopes)
        {
            if (scope.ContainsKey(key: name))
            {
                scope[key: name] = obj;
                return;
            }
        }

        // If not found in any scope, add to current scope (new declaration)
        _scopes.Peek()[key: name] = obj;
    }

    /// <summary>
    /// Mark an object as invalidated (deadref) with the reason for invalidation.
    /// This is the core mechanism for preventing use-after-invalidation errors.
    /// Once invalidated, objects cannot be used unless in a danger! block.
    ///
    /// Objects become invalidated through:
    /// <list type="bullet">
    /// <item>Memory operations (hijack!(), share!(), steal!(), etc.)</item>
    /// <item>Scope exit (automatic cleanup)</item>
    /// <item>Container moves (RazorForge only)</item>
    /// <item>Manual release operations</item>
    /// </list>
    /// </summary>
    /// <param name="name">Variable name to invalidate</param>
    /// <param name="reason">Human-readable reason for invalidation</param>
    /// <param name="location">Source location for error reporting</param>
    private void InvalidateObject(string name, string reason, SourceLocation location)
    {
        MemoryObject? obj = GetObject(name: name);
        if (obj != null)
        {
            MemoryObject invalidated = obj with
            {
                State = ObjectState.Invalidated,
                InvalidatedBy = reason // Track reason for better error messages
            };
            SetObject(name: name, obj: invalidated);
        }
    }

    /// <summary>
    /// Add a memory safety error to the error list for reporting.
    /// Errors are collected throughout analysis and reported to the user
    /// at the end of compilation with detailed information about the violation.
    /// </summary>
    /// <param name="message">Human-readable error message</param>
    /// <param name="location">Source location where error occurred</param>
    /// <param name="type">Categorized error type for error handling</param>
    private void AddError(string message, SourceLocation location,
        MemoryError.MemoryErrorType type)
    {
        Errors.Add(item: new MemoryError(Message: message, Location: location, Type: type));
    }

    /// <summary>
    /// Get a memory object by name for external semantic analysis.
    /// Used by SemanticAnalyzer to access memory objects for validation.
    /// </summary>
    /// <param name="name">Variable name to look up</param>
    /// <returns>Memory object if found, null otherwise</returns>
    public MemoryObject? GetMemoryObject(string name)
    {
        return GetObject(name: name);
    }

    /// <summary>
    /// Validate a memory operation without executing it.
    /// Used by SemanticAnalyzer to check if a memory operation would be valid
    /// before actually performing it in the AST.
    /// </summary>
    /// <param name="memoryObject">Object to operate on</param>
    /// <param name="operation">Memory operation to validate</param>
    /// <param name="location">Source location for error reporting</param>
    /// <returns>Validation result with success/failure and potential new wrapper type</returns>
    public MemoryOperationResult ValidateMemoryOperation(MemoryObject memoryObject,
        MemoryOperation operation, SourceLocation location)
    {
        var errors = new List<MemoryError>();

        // Check if object is valid (unless in danger block)
        if (memoryObject.State != ObjectState.Valid && !_inDangerBlock)
        {
            errors.Add(item: new MemoryError(
                Message:
                $"Cannot perform {operation} on invalidated object '{memoryObject.Name}' (invalidated by: {memoryObject.InvalidatedBy})",
                Location: location, Type: MemoryError.MemoryErrorType.UseAfterInvalidation));
            return new MemoryOperationResult(IsSuccess: false,
                NewWrapperType: memoryObject.Wrapper, Errors: errors);
        }

        // Validate specific operations
        WrapperType newWrapperType = operation switch
        {
            MemoryOperation.Retain => ValidateRetain(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Share => ValidateShare(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Track => ValidateTrack(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Steal => ValidateSteal(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Snatch => ValidateSnatch(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Release => ValidateRelease(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Recover => ValidateRecover(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Reveal => ValidateReveal(obj: memoryObject, location: location,
                errors: errors),
            MemoryOperation.Own => ValidateOwn(obj: memoryObject, location: location,
                errors: errors),
            _ => WrapperType.Owned
        };

        return new MemoryOperationResult(IsSuccess: errors.Count == 0,
            NewWrapperType: newWrapperType, Errors: errors);
    }

    // Validation helper methods
    private WrapperType ValidateHijack(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (!obj.CanTransformTo(target: WrapperType.Hijacked, inDangerBlock: _inDangerBlock))
        {
            errors.Add(item: new MemoryError(
                Message: $"Cannot hijack object of type {obj.Wrapper}", Location: location,
                Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        return WrapperType.Hijacked;
    }

    private WrapperType ValidateRetain(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (!obj.CanTransformTo(target: WrapperType.Retained, inDangerBlock: _inDangerBlock))
        {
            errors.Add(item: new MemoryError(Message: $"Cannot retain object of type {obj.Wrapper}",
                Location: location, Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        return WrapperType.Retained;
    }

    private WrapperType ValidateTrack(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Retained && obj.Wrapper != WrapperType.Shared)
        {
            errors.Add(item: new MemoryError(
                Message: $"Can only track Retained or Shared objects, not {obj.Wrapper}", Location: location,
                Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        return WrapperType.Tracked;
    }

    private WrapperType ValidateShare(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (!obj.CanTransformTo(target: WrapperType.Shared, inDangerBlock: _inDangerBlock))
        {
            errors.Add(item: new MemoryError(
                Message: $"Cannot share object of type {obj.Wrapper}", Location: location,
                Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        return WrapperType.Shared;
    }

    private WrapperType ValidateSteal(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (obj.Wrapper == WrapperType.Retained && obj.ReferenceCount != 1)
        {
            errors.Add(item: new MemoryError(
                Message:
                $"Cannot steal from Retained object with RC={obj.ReferenceCount} (need RC=1)",
                Location: location, Type: MemoryError.MemoryErrorType.ReferenceCountError));
            return obj.Wrapper;
        }

        if (obj.Wrapper == WrapperType.Shared && obj.ReferenceCount != 1)
        {
            errors.Add(item: new MemoryError(
                Message:
                $"Cannot steal from Shared object with Arc={obj.ReferenceCount} (need Arc=1)",
                Location: location, Type: MemoryError.MemoryErrorType.ReferenceCountError));
            return obj.Wrapper;
        }

        if (obj.Wrapper != WrapperType.Retained && obj.Wrapper != WrapperType.Shared)
        {
            errors.Add(item: new MemoryError(Message: $"Cannot steal from {obj.Wrapper}",
                Location: location, Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        return WrapperType.Owned;
    }

    private WrapperType ValidateSnatch(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (!_inDangerBlock)
        {
            errors.Add(item: new MemoryError(
                Message: "snatch!() can only be used in danger! blocks", Location: location,
                Type: MemoryError.MemoryErrorType.DangerBlockViolation));
            return obj.Wrapper;
        }

        return WrapperType.Snatched;
    }

    private WrapperType ValidateRelease(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Retained && obj.Wrapper != WrapperType.Shared)
        {
            errors.Add(item: new MemoryError(
                Message: $"Can only release Retained or Shared objects, not {obj.Wrapper}",
                Location: location, Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        if (obj.ReferenceCount <= 1)
        {
            errors.Add(item: new MemoryError(
                Message: $"Cannot release object with RC={obj.ReferenceCount} (would drop to 0)",
                Location: location, Type: MemoryError.MemoryErrorType.ReferenceCountError));
            return obj.Wrapper;
        }

        return obj.Wrapper;
    }

    private WrapperType ValidateRecover(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Tracked)
        {
            errors.Add(item: new MemoryError(
                Message: $"Can only recover Tracked objects, not {obj.Wrapper}",
                Location: location, Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        // Determine target type based on Policy (null = Retained, non-null = Shared)
        return obj.Policy == null ? WrapperType.Retained : WrapperType.Shared;
    }

    private WrapperType ValidateReveal(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Snatched)
        {
            errors.Add(item: new MemoryError(
                Message: $"Can only reveal Snatched objects, not {obj.Wrapper}",
                Location: location, Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        if (!_inDangerBlock)
        {
            errors.Add(item: new MemoryError(
                Message: "reveal!() can only be used in danger! blocks", Location: location,
                Type: MemoryError.MemoryErrorType.DangerBlockViolation));
            return obj.Wrapper;
        }

        return WrapperType.Owned;
    }

    private WrapperType ValidateOwn(MemoryObject obj, SourceLocation location,
        List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Snatched)
        {
            errors.Add(item: new MemoryError(
                Message: $"Can only own Snatched objects, not {obj.Wrapper}", Location: location,
                Type: MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }

        if (!_inDangerBlock)
        {
            errors.Add(item: new MemoryError(Message: "own!() can only be used in danger! blocks",
                Location: location, Type: MemoryError.MemoryErrorType.DangerBlockViolation));
            return obj.Wrapper;
        }

        return WrapperType.Owned;
    }
}

/// <summary>
/// Result of validating a memory operation
/// </summary>
public record MemoryOperationResult(
    bool IsSuccess,
    WrapperType NewWrapperType,
    List<MemoryError> Errors);
