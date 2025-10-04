using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Memory safety analyzer for RazorForge and Cake memory models.
/// 
/// This analyzer tracks object ownership, validates memory operations, and enforces
/// memory safety rules throughout the compilation process. It handles the fundamental
/// differences between RazorForge's explicit memory management and Cake's automatic
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
    /// <summary>Target language (RazorForge or Cake) - determines memory model behavior</summary>
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
    /// List of memory safety errors detected during analysis.
    /// Contains detailed error information for reporting to the user.
    /// </summary>
    private readonly List<MemoryError> _errors = new();
    
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
    /// <param name="language">Target language (RazorForge or Cake)</param>
    /// <param name="mode">Language mode for additional customization</param>
    public MemoryAnalyzer(Language language, LanguageMode mode)
    {
        _language = language;
        _mode = mode;
        // Initialize with global scope
        _scopes.Push(new Dictionary<string, MemoryObject>());
    }

    /// <summary>
    /// Get all memory safety errors detected during analysis.
    /// Used by the compiler to report memory safety violations to the user.
    /// </summary>
    public List<MemoryError> Errors => _errors;

    /// <summary>
    /// Enter a new lexical scope (function, block, loop, etc.).
    /// Creates a new scope level for tracking objects declared within this scope.
    /// Objects declared in this scope will be automatically invalidated when
    /// the scope is exited, preventing use-after-scope errors.
    /// </summary>
    public void EnterScope()
    {
        _scopes.Push(new Dictionary<string, MemoryObject>());
    }

    /// <summary>
    /// Exit the current lexical scope, invalidating all objects declared within it.
    /// This implements automatic scope-based cleanup where objects become deadref
    /// when they go out of scope. Essential for preventing use-after-scope errors.
    /// The global scope cannot be exited (minimum scope depth of 1).
    /// </summary>
    public void ExitScope()
    {
        if (_scopes.Count > 1)
        {
            var scope = _scopes.Pop();
            // All objects in this scope become invalid (go out of scope)
            // This is a fundamental safety mechanism preventing access to
            // objects that have been destroyed by scope exit
            foreach (var obj in scope.Values)
            {
                InvalidateObject(obj.Name, "scope end", obj.Location);
            }
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
        if (_language == Language.Cake)
        {
            // Cake does not have danger blocks - it uses automatic memory management
            // and doesn't expose unsafe operations to the programmer
            AddError("Danger blocks are not allowed in Cake", location, MemoryError.MemoryErrorType.DangerBlockViolation);
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
    /// Enter a usurping function that is allowed to return exclusive tokens.
    /// Usurping functions are special RazorForge functions explicitly marked
    /// as being able to return Hijacked&lt;T&gt; objects. This prevents accidental
    /// exclusive token leakage from regular functions.
    /// </summary>
    public void EnterUsurpingFunction()
    {
        if (_language == Language.Cake)
        {
            // Cake doesn't have usurping functions since it uses automatic
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
    /// Register a new object from variable declaration or function return.
    /// This creates a new memory object with appropriate initial wrapper type
    /// based on the target language's memory model.
    /// 
    /// RazorForge: Objects start as Owned with RC=1 (direct ownership)
    /// Cake: Objects start as Shared with automatic RC management
    /// </summary>
    /// <param name="name">Variable name for the object</param>
    /// <param name="type">Type information for the object</param>
    /// <param name="location">Source location for error reporting</param>
    /// <param name="isFloating">Whether this is a floating literal/temporary (Cake only)</param>
    public void RegisterObject(string name, TypeInfo type, SourceLocation location, bool isFloating = false)
    {
        // Different default wrapper types based on language memory model
        var wrapper = _language == Language.Cake ? WrapperType.Shared : WrapperType.Owned;
        var obj = new MemoryObject(name, type, wrapper, ObjectState.Valid, 1, location);
        
        var currentScope = _scopes.Peek();
        currentScope[name] = obj;
        
        // Cake automatic reference counting: increment RC when creating non-floating references
        if (_language == Language.Cake && !isFloating)
        {
            // Simulate automatic RC increment for assignment to variables
            // Floating literals don't get RC increment until assigned
            obj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            currentScope[name] = obj;
        }
    }

    /// <summary>
    /// Handle assignment in Cake with automatic reference counting.
    /// When assigning one reference to another in Cake, the reference count
    /// is automatically incremented for both the source and target objects.
    /// This simulates Cake's automatic RC management during compilation.
    /// 
    /// Example: let b = a  // RC of 'a' object increases, 'b' points to same object
    /// </summary>
    /// <param name="target">Target variable name receiving the assignment</param>
    /// <param name="source">Source variable name being assigned from</param>
    /// <param name="location">Source location for error reporting</param>
    public void HandleCakeAssignment(string target, string source, SourceLocation location)
    {
        // This method only applies to Cake's automatic RC model
        if (_language != Language.Cake) return;

        var sourceObj = GetObject(source);
        if (sourceObj == null)
        {
            AddError($"Source object '{source}' not found", location, MemoryError.MemoryErrorType.UseAfterInvalidation);
            return;
        }

        if (sourceObj.State != ObjectState.Valid)
        {
            AddError($"Cannot assign from invalidated object '{source}'", location, MemoryError.MemoryErrorType.UseAfterInvalidation);
            return;
        }

        // Cake automatic RC management: both source and target share the same object
        // with incremented reference count
        var newObj = sourceObj with 
        { 
            Name = target, 
            ReferenceCount = sourceObj.ReferenceCount + 1 
        };
        
        // Create new reference for target
        SetObject(target, newObj);
        
        // Update source object RC to reflect the new reference
        SetObject(source, sourceObj with { ReferenceCount = sourceObj.ReferenceCount + 1 });
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
    /// <returns>New memory object state after operation, or null if operation failed</returns>
    public MemoryObject? HandleMemoryOperation(string objectName, MemoryOperation operation, SourceLocation location)
    {
        var obj = GetObject(objectName);
        if (obj == null)
        {
            AddError($"Object '{objectName}' not found", location, MemoryError.MemoryErrorType.UseAfterInvalidation);
            return null;
        }

        // Core safety rule: cannot operate on invalidated objects (unless in danger! block)
        if (obj.State != ObjectState.Valid && !_inDangerBlock)
        {
            AddError($"Cannot perform {operation} on invalidated object '{objectName}' (invalidated by: {obj.InvalidatedBy})", 
                    location, MemoryError.MemoryErrorType.UseAfterInvalidation);
            return null;
        }

        // Dispatch to specific operation handlers
        return operation switch
        {
            MemoryOperation.Hijack => HandleHijack(obj, location),
            MemoryOperation.Share => HandleShare(obj, location),
            MemoryOperation.Watch => HandleWatch(obj, location),
            MemoryOperation.ThreadShare => HandleThreadShare(obj, location),
            MemoryOperation.ThreadWatch => HandleThreadWatch(obj, location),
            MemoryOperation.Steal => HandleSteal(obj, location),
            MemoryOperation.Snatch => HandleSnatch(obj, location),
            MemoryOperation.Release => HandleRelease(obj, location),
            MemoryOperation.TryShare => HandleTryShare(obj, location),
            MemoryOperation.TryThreadShare => HandleTryThreadShare(obj, location),
            MemoryOperation.Reveal => HandleReveal(obj, location),
            MemoryOperation.Own => HandleOwn(obj, location),
            _ => throw new ArgumentException($"Unknown memory operation: {operation}")
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
        if (!obj.CanTransformTo(WrapperType.Hijacked, _inDangerBlock))
        {
            AddError($"Cannot hijack object of type {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Invalidate source to enforce exclusive access - no other references allowed
        InvalidateObject(obj.Name, "hijack!()", location);
        
        // Return hijacked object with exclusive access
        return obj with 
        { 
            Wrapper = WrapperType.Hijacked, 
            ReferenceCount = 1  // Always 1 for exclusive access
        };
    }

    /// <summary>
    /// Handle share!() operation - transform to shared ownership with reference counting.
    /// Creates a Shared&lt;T&gt; wrapper that allows multiple mutable references with RC tracking.
    /// If already shared, increments reference count. Otherwise converts and invalidates source.
    /// </summary>
    private MemoryObject? HandleShare(MemoryObject obj, SourceLocation location)
    {
        if (!obj.CanTransformTo(WrapperType.Shared, _inDangerBlock))
        {
            AddError($"Cannot share object of type {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (obj.Wrapper == WrapperType.Shared)
        {
            // Already shared - increment reference count for new reference
            var newObj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            SetObject(obj.Name, newObj);
            return newObj;
        }

        // Convert to shared ownership, invalidate source (one-time transformation)
        InvalidateObject(obj.Name, "share!()", location);
        
        return obj with 
        { 
            Wrapper = WrapperType.Shared, 
            ReferenceCount = 1  // First shared reference
        };
    }

    /// <summary>
    /// Handle watch!() operation - create weak observer reference.
    /// Creates a Watched&lt;T&gt; weak reference that doesn't prevent object destruction.
    /// Can only be created from Shared objects. Doesn't invalidate source or affect RC.
    /// Used for breaking reference cycles and observing without ownership responsibility.
    /// </summary>
    private MemoryObject? HandleWatch(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.Shared)
        {
            AddError($"Can only watch Shared objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Create weak reference - doesn't invalidate source or affect its RC
        return obj with 
        { 
            Wrapper = WrapperType.Watched, 
            ReferenceCount = 0  // Weak references don't contribute to RC
        };
    }

    /// <summary>
    /// Handle thread_share!() operation - transform to thread-safe shared ownership.
    /// Creates a ThreadShared&lt;T&gt; wrapper with atomic reference counting (Arc).
    /// Safe to pass between threads. If already thread-shared, increments Arc count.
    /// Similar to Arc&lt;Mutex&lt;T&gt;&gt; in Rust.
    /// </summary>
    private MemoryObject? HandleThreadShare(MemoryObject obj, SourceLocation location)
    {
        if (!obj.CanTransformTo(WrapperType.ThreadShared, _inDangerBlock))
        {
            AddError($"Cannot thread_share object of type {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (obj.Wrapper == WrapperType.ThreadShared)
        {
            // Already thread-shared - increment atomic reference count
            var newObj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            SetObject(obj.Name, newObj);
            return newObj;
        }

        // Convert to thread-safe shared ownership, invalidate source
        InvalidateObject(obj.Name, "thread_share!()", location);
        
        return obj with 
        { 
            Wrapper = WrapperType.ThreadShared, 
            ReferenceCount = 1  // First thread-shared reference
        };
    }

    /// <summary>
    /// Handle thread_watch!() operation - create thread-safe weak observer.
    /// Creates a ThreadWatched&lt;T&gt; weak reference for thread-safe observation.
    /// Can only be created from ThreadShared objects. Doesn't affect Arc count.
    /// Used for breaking cycles in multi-threaded environments.
    /// </summary>
    private MemoryObject? HandleThreadWatch(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.ThreadShared)
        {
            AddError($"Can only thread_watch ThreadShared objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Create thread-safe weak reference - doesn't invalidate source or affect Arc
        return obj with 
        { 
            Wrapper = WrapperType.ThreadWatched, 
            ReferenceCount = 0  // Weak references don't contribute to Arc
        };
    }

    /// <summary>
    /// Handle steal!() operation - reclaim direct ownership from shared objects.
    /// Converts Shared/ThreadShared/Hijacked back to direct Owned wrapper.
    /// Only works when you're the sole owner (RC=1 for shared objects).
    /// Used for optimization when you know you're the only reference holder.
    /// </summary>
    private MemoryObject? HandleSteal(MemoryObject obj, SourceLocation location)
    {
        // Reference count validation for shared objects
        if (obj.Wrapper == WrapperType.Shared && obj.ReferenceCount != 1)
        {
            AddError($"Cannot steal from Shared object with RC={obj.ReferenceCount} (need RC=1)", 
                    location, MemoryError.MemoryErrorType.ReferenceCountError);
            return null;
        }

        if (obj.Wrapper == WrapperType.ThreadShared && obj.ReferenceCount != 1)
        {
            AddError($"Cannot steal from ThreadShared object with Arc={obj.ReferenceCount} (need Arc=1)", 
                    location, MemoryError.MemoryErrorType.ReferenceCountError);
            return null;
        }

        // Can only steal from shared objects or hijacked objects
        if (obj.Wrapper != WrapperType.Shared && obj.Wrapper != WrapperType.ThreadShared && obj.Wrapper != WrapperType.Hijacked)
        {
            AddError($"Cannot steal from {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Invalidate source reference, return direct ownership
        InvalidateObject(obj.Name, "steal!()", location);
        
        return obj with 
        { 
            Wrapper = WrapperType.Owned, 
            ReferenceCount = 1  // Direct ownership always has RC=1
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
            AddError("snatch!() can only be used in danger! blocks", location, MemoryError.MemoryErrorType.DangerBlockViolation);
            return null;
        }

        // Forcibly take ownership regardless of current state or RC
        InvalidateObject(obj.Name, "snatch!()", location);
        
        return obj with 
        { 
            Wrapper = WrapperType.Snatched,  // Marks contaminated ownership
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
        if (obj.Wrapper != WrapperType.Shared && obj.Wrapper != WrapperType.ThreadShared)
        {
            AddError($"Can only release Shared or ThreadShared objects, not {obj.Wrapper}", 
                    location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (obj.ReferenceCount <= 1)
        {
            AddError($"Cannot release object with RC={obj.ReferenceCount} (would drop to 0)", 
                    location, MemoryError.MemoryErrorType.ReferenceCountError);
            return null;
        }

        // Invalidate this reference and decrement the overall reference count
        InvalidateObject(obj.Name, "release!()", location);
        
        return obj with { ReferenceCount = obj.ReferenceCount - 1 };
    }

    /// <summary>
    /// Handle try_share!() operation - attempt to upgrade weak to strong reference.
    /// Tries to convert Watched reference back to Shared reference.
    /// In a real implementation, this could fail if the object was destroyed.
    /// For analysis purposes, we assume success but real runtime may fail.
    /// </summary>
    private MemoryObject? HandleTryShare(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.Watched)
        {
            AddError($"Can only try_share on Watched objects, not {obj.Wrapper}", 
                    location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Try to upgrade weak to strong - in real implementation this could fail
        // if the original object was already destroyed
        return obj with 
        { 
            Wrapper = WrapperType.Shared, 
            ReferenceCount = 1  // New strong reference
        };
    }

    /// <summary>
    /// Handle try_thread_share!() operation - attempt thread-safe weak-to-strong upgrade.
    /// Tries to convert ThreadWatched reference back to ThreadShared reference.
    /// Thread-safe version of try_share!() with atomic operations.
    /// Can fail at runtime if the object was destroyed, but assumed successful for analysis.
    /// </summary>
    private MemoryObject? HandleTryThreadShare(MemoryObject obj, SourceLocation location)
    {
        if (obj.Wrapper != WrapperType.ThreadWatched)
        {
            AddError($"Can only try_thread_share on ThreadWatched objects, not {obj.Wrapper}", 
                    location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        // Try to upgrade thread-weak to thread-strong - could fail at runtime
        // if the original object was already destroyed
        return obj with 
        { 
            Wrapper = WrapperType.ThreadShared, 
            ReferenceCount = 1  // New strong thread-shared reference
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
            AddError($"Can only reveal Snatched objects, not {obj.Wrapper}", 
                    location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (!_inDangerBlock)
        {
            AddError("reveal!() can only be used in danger! blocks", location, MemoryError.MemoryErrorType.DangerBlockViolation);
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
            AddError($"Can only own Snatched objects, not {obj.Wrapper}", 
                    location, MemoryError.MemoryErrorType.InvalidTransformation);
            return null;
        }

        if (!_inDangerBlock)
        {
            AddError("own!() can only be used in danger! blocks", location, MemoryError.MemoryErrorType.DangerBlockViolation);
            return null;
        }

        // Convert snatched to owned and invalidate source to prevent reuse
        InvalidateObject(obj.Name, "own!()", location);
        
        return obj with { Wrapper = WrapperType.Owned };
    }

    /// <summary>
    /// Handle container operations like push(), insert(), add() that move objects into containers.
    /// 
    /// RazorForge: Uses move semantics - object is transferred to container and source becomes deadref.
    /// This prevents external mutation after insertion, ensuring container controls access.
    /// 
    /// Cake: Uses automatic reference counting - container shares reference with automatic RC increment.
    /// Original reference remains valid and can be used alongside container reference.
    /// 
    /// This fundamental difference reflects the memory model philosophies of each language.
    /// </summary>
    /// <param name="objectName">Name of object being moved into container</param>
    /// <param name="containerName">Name of container for error reporting</param>
    /// <param name="location">Source location for error reporting</param>
    public void HandleContainerMove(string objectName, string containerName, SourceLocation location)
    {
        var obj = GetObject(objectName);
        if (obj == null)
        {
            AddError($"Object '{objectName}' not found", location, MemoryError.MemoryErrorType.UseAfterInvalidation);
            return;
        }

        if (obj.State != ObjectState.Valid)
        {
            AddError($"Cannot move invalidated object '{objectName}' into container", 
                    location, MemoryError.MemoryErrorType.ContainerMoveError);
            return;
        }

        // Language-specific container semantics
        if (_language == Language.RazorForge)
        {
            // RazorForge: move semantics - invalidate source after moving to container
            InvalidateObject(objectName, $"moved into container '{containerName}'", location);
        }
        else if (_language == Language.Cake)
        {
            // Cake: automatic reference counting - container shares reference
            var newObj = obj with { ReferenceCount = obj.ReferenceCount + 1 };
            SetObject(objectName, newObj);
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
        if (_language == Language.Cake) return; // Cake has no usurping functions

        // Check if return type is Hijacked<T> without usurping declaration
        if (IsHijackedType(returnType) && !_inUsurpingFunction)
        {
            AddError("Only usurping functions can return Hijacked<T>", 
                    location, MemoryError.MemoryErrorType.UsurpingViolation);
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
        return type.Name.StartsWith("Hijacked<");
    }

    /// <summary>
    /// Look up a memory object by variable name across all scopes.
    /// Searches from innermost to outermost scope following lexical scoping rules.
    /// Returns the first matching object found, or null if not found.
    /// </summary>
    /// <param name="name">Variable name to look up</param>
    /// <returns>Memory object for the variable, or null if not found</returns>
    private MemoryObject? GetObject(string name)
    {
        // Search from innermost to outermost scope
        foreach (var scope in _scopes)
        {
            if (scope.TryGetValue(name, out var obj))
                return obj;
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
        foreach (var scope in _scopes)
        {
            if (scope.ContainsKey(name))
            {
                scope[name] = obj;
                return;
            }
        }
        // If not found in any scope, add to current scope (new declaration)
        _scopes.Peek()[name] = obj;
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
        var obj = GetObject(name);
        if (obj != null)
        {
            var invalidated = obj with 
            { 
                State = ObjectState.Invalidated, 
                InvalidatedBy = reason  // Track reason for better error messages
            };
            SetObject(name, invalidated);
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
    private void AddError(string message, SourceLocation location, MemoryError.MemoryErrorType type)
    {
        _errors.Add(new MemoryError(message, location, type));
    }

    /// <summary>
    /// Get a memory object by name for external semantic analysis.
    /// Used by SemanticAnalyzer to access memory objects for validation.
    /// </summary>
    /// <param name="name">Variable name to look up</param>
    /// <returns>Memory object if found, null otherwise</returns>
    public MemoryObject? GetMemoryObject(string name)
    {
        return GetObject(name);
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
    public MemoryOperationResult ValidateMemoryOperation(MemoryObject memoryObject, MemoryOperation operation, SourceLocation location)
    {
        var errors = new List<MemoryError>();

        // Check if object is valid (unless in danger block)
        if (memoryObject.State != ObjectState.Valid && !_inDangerBlock)
        {
            errors.Add(new MemoryError($"Cannot perform {operation} on invalidated object '{memoryObject.Name}' (invalidated by: {memoryObject.InvalidatedBy})",
                                     location, MemoryError.MemoryErrorType.UseAfterInvalidation));
            return new MemoryOperationResult(false, memoryObject.Wrapper, errors);
        }

        // Validate specific operations
        var newWrapperType = operation switch
        {
            MemoryOperation.Hijack => ValidateHijack(memoryObject, location, errors),
            MemoryOperation.Share => ValidateShare(memoryObject, location, errors),
            MemoryOperation.Watch => ValidateWatch(memoryObject, location, errors),
            MemoryOperation.ThreadShare => ValidateThreadShare(memoryObject, location, errors),
            MemoryOperation.ThreadWatch => ValidateThreadWatch(memoryObject, location, errors),
            MemoryOperation.Steal => ValidateSteal(memoryObject, location, errors),
            MemoryOperation.Snatch => ValidateSnatch(memoryObject, location, errors),
            MemoryOperation.Release => ValidateRelease(memoryObject, location, errors),
            MemoryOperation.TryShare => ValidateTryShare(memoryObject, location, errors),
            MemoryOperation.TryThreadShare => ValidateTryThreadShare(memoryObject, location, errors),
            MemoryOperation.Reveal => ValidateReveal(memoryObject, location, errors),
            MemoryOperation.Own => ValidateOwn(memoryObject, location, errors),
            _ => WrapperType.Owned
        };

        return new MemoryOperationResult(errors.Count == 0, newWrapperType, errors);
    }

    // Validation helper methods
    private WrapperType ValidateHijack(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (!obj.CanTransformTo(WrapperType.Hijacked, _inDangerBlock))
        {
            errors.Add(new MemoryError($"Cannot hijack object of type {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.Hijacked;
    }

    private WrapperType ValidateShare(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (!obj.CanTransformTo(WrapperType.Shared, _inDangerBlock))
        {
            errors.Add(new MemoryError($"Cannot share object of type {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.Shared;
    }

    private WrapperType ValidateWatch(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Shared)
        {
            errors.Add(new MemoryError($"Can only watch Shared objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.Watched;
    }

    private WrapperType ValidateThreadShare(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (!obj.CanTransformTo(WrapperType.ThreadShared, _inDangerBlock))
        {
            errors.Add(new MemoryError($"Cannot thread_share object of type {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.ThreadShared;
    }

    private WrapperType ValidateThreadWatch(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.ThreadShared)
        {
            errors.Add(new MemoryError($"Can only thread_watch ThreadShared objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.ThreadWatched;
    }

    private WrapperType ValidateSteal(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper == WrapperType.Shared && obj.ReferenceCount != 1)
        {
            errors.Add(new MemoryError($"Cannot steal from Shared object with RC={obj.ReferenceCount} (need RC=1)", location, MemoryError.MemoryErrorType.ReferenceCountError));
            return obj.Wrapper;
        }
        if (obj.Wrapper == WrapperType.ThreadShared && obj.ReferenceCount != 1)
        {
            errors.Add(new MemoryError($"Cannot steal from ThreadShared object with Arc={obj.ReferenceCount} (need Arc=1)", location, MemoryError.MemoryErrorType.ReferenceCountError));
            return obj.Wrapper;
        }
        if (obj.Wrapper != WrapperType.Shared && obj.Wrapper != WrapperType.ThreadShared && obj.Wrapper != WrapperType.Hijacked)
        {
            errors.Add(new MemoryError($"Cannot steal from {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.Owned;
    }

    private WrapperType ValidateSnatch(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (!_inDangerBlock)
        {
            errors.Add(new MemoryError("snatch!() can only be used in danger! blocks", location, MemoryError.MemoryErrorType.DangerBlockViolation));
            return obj.Wrapper;
        }
        return WrapperType.Snatched;
    }

    private WrapperType ValidateRelease(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Shared && obj.Wrapper != WrapperType.ThreadShared)
        {
            errors.Add(new MemoryError($"Can only release Shared or ThreadShared objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        if (obj.ReferenceCount <= 1)
        {
            errors.Add(new MemoryError($"Cannot release object with RC={obj.ReferenceCount} (would drop to 0)", location, MemoryError.MemoryErrorType.ReferenceCountError));
            return obj.Wrapper;
        }
        return obj.Wrapper;
    }

    private WrapperType ValidateTryShare(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Watched)
        {
            errors.Add(new MemoryError($"Can only try_share on Watched objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.Shared;
    }

    private WrapperType ValidateTryThreadShare(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.ThreadWatched)
        {
            errors.Add(new MemoryError($"Can only try_thread_share on ThreadWatched objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        return WrapperType.ThreadShared;
    }

    private WrapperType ValidateReveal(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Snatched)
        {
            errors.Add(new MemoryError($"Can only reveal Snatched objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        if (!_inDangerBlock)
        {
            errors.Add(new MemoryError("reveal!() can only be used in danger! blocks", location, MemoryError.MemoryErrorType.DangerBlockViolation));
            return obj.Wrapper;
        }
        return WrapperType.Owned;
    }

    private WrapperType ValidateOwn(MemoryObject obj, SourceLocation location, List<MemoryError> errors)
    {
        if (obj.Wrapper != WrapperType.Snatched)
        {
            errors.Add(new MemoryError($"Can only own Snatched objects, not {obj.Wrapper}", location, MemoryError.MemoryErrorType.InvalidTransformation));
            return obj.Wrapper;
        }
        if (!_inDangerBlock)
        {
            errors.Add(new MemoryError("own!() can only be used in danger! blocks", location, MemoryError.MemoryErrorType.DangerBlockViolation));
            return obj.Wrapper;
        }
        return WrapperType.Owned;
    }
}

/// <summary>
/// Result of validating a memory operation
/// </summary>
public record MemoryOperationResult(bool IsSuccess, WrapperType NewWrapperType, List<MemoryError> Errors);