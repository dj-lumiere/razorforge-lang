using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Partial class containing scoped access statement code generation.
/// Handles viewing, hijacking, inspecting, and seizing statements for memory model compliance.
/// </summary>
public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Generates LLVM IR for a viewing statement (scoped read-only access).
    ///
    /// For single-threaded code, Viewed&lt;T&gt; is a compile-time concept -
    /// the handle is simply an alias to the source pointer. No runtime locks needed.
    ///
    /// Syntax: viewing source as handle { body }
    ///
    /// Generated IR:
    /// 1. Evaluate source expression to get pointer
    /// 2. Create handle as alias to source pointer
    /// 3. Execute body (handle provides read-only access)
    /// 4. Handle goes out of scope (no cleanup needed for single-threaded)
    /// </summary>
    public string VisitViewingStatement(ViewingStatement node)
    {
        // Evaluate the source expression to get a pointer
        string sourcePtr = node.Source.Accept(visitor: this);

        // Get the type of the source
        string sourceType = "ptr";
        if (node.Source is IdentifierExpression sourceId &&
            _symbolTypes.ContainsKey(key: sourceId.Name))
        {
            sourceType = _symbolTypes[key: sourceId.Name];
        }

        // Create the handle as an alias to the source pointer
        // For single-threaded Viewed<T>, this is just a pointer copy
        string handleName = $"%{node.Handle}";
        _symbolTypes[key: node.Handle] = sourceType;

        // The handle is just an alias - no alloca needed, just use the same pointer
        // For stack-allocated values, we need to load the address
        if (sourcePtr.StartsWith(value: '%') && !sourcePtr.StartsWith(value: "%t"))
        {
            // Source is a named variable - the handle points to same location
            _output.AppendLine(
                handler: $"  ; viewing {node.Source} as {node.Handle} - read-only alias");
            _output.AppendLine(
                handler: $"  {handleName} = bitcast ptr {sourcePtr} to ptr");
        }
        else
        {
            // Source is a temporary or literal - store in alloca for handle
            _output.AppendLine(
                handler: $"  ; viewing expression as {node.Handle} - read-only access");
            _output.AppendLine(handler: $"  {handleName} = alloca {sourceType}");
            _output.AppendLine(
                handler: $"  store {sourceType} {sourcePtr}, ptr {handleName}");
        }

        // Execute the body with the handle available
        node.Body.Accept(visitor: this);

        // No cleanup needed for single-threaded viewing
        _output.AppendLine(handler: $"  ; end viewing {node.Handle}");

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for a hijacking statement (scoped exclusive access).
    ///
    /// For single-threaded code, Hijacked&lt;T&gt; is a compile-time concept -
    /// the handle is an alias to the source pointer with exclusive access.
    /// The semantic analyzer ensures no aliasing violations at compile time.
    ///
    /// Syntax: hijacking source as handle { body }
    ///
    /// Generated IR:
    /// 1. Evaluate source expression to get pointer
    /// 2. Create handle as alias to source pointer (exclusive access enforced at compile time)
    /// 3. Execute body (handle provides read/write access)
    /// 4. Handle goes out of scope (source is restored - compile-time guarantee)
    /// </summary>
    public string VisitHijackingStatement(HijackingStatement node)
    {
        // Evaluate the source expression to get a pointer
        string sourcePtr = node.Source.Accept(visitor: this);

        // Get the type of the source
        string sourceType = "ptr";
        if (node.Source is IdentifierExpression sourceId &&
            _symbolTypes.ContainsKey(key: sourceId.Name))
        {
            sourceType = _symbolTypes[key: sourceId.Name];
        }

        // Create the handle as an alias to the source pointer
        // For single-threaded Hijacked<T>, this is just a pointer with exclusive access
        string handleName = $"%{node.Handle}";
        _symbolTypes[key: node.Handle] = sourceType;

        // The handle is an exclusive alias - same pointer, exclusive access enforced at compile time
        if (sourcePtr.StartsWith(value: "%") && !sourcePtr.StartsWith(value: "%t"))
        {
            // Source is a named variable - the handle points to same location
            _output.AppendLine(
                handler: $"  ; hijacking {node.Source} as {node.Handle} - exclusive access");
            _output.AppendLine(
                handler: $"  {handleName} = bitcast ptr {sourcePtr} to ptr");
        }
        else
        {
            // Source is a temporary or literal - store in alloca for handle
            _output.AppendLine(
                handler: $"  ; hijacking expression as {node.Handle} - exclusive access");
            _output.AppendLine(handler: $"  {handleName} = alloca {sourceType}");
            _output.AppendLine(
                handler: $"  store {sourceType} {sourcePtr}, ptr {handleName}");
        }

        // Execute the body with the handle available (exclusive read/write access)
        node.Body.Accept(visitor: this);

        // No runtime cleanup needed - source restoration is a compile-time guarantee
        _output.AppendLine(handler: $"  ; end hijacking {node.Handle} - source restored");

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for an inspecting statement (thread-safe scoped read access).
    ///
    /// For multi-threaded code with Shared&lt;T, MultiReadLock&gt;, this acquires a read lock.
    /// Multiple observers can coexist (RwLock semantics).
    ///
    /// Syntax: inspecting shared as handle { body }
    ///
    /// Generated IR:
    /// 1. Call runtime to acquire read lock on Shared
    /// 2. Create handle pointing to inner data
    /// 3. Execute body
    /// 4. Call runtime to release read lock
    /// </summary>
    public string VisitObservingStatement(ObservingStatement node)
    {
        // Evaluate the source (should be a Shared<T, Policy>)
        string sourcePtr = node.Source.Accept(visitor: this);

        string handleName = $"%{node.Handle}";

        _output.AppendLine(
            handler: $"  ; inspecting {node.Source} as {node.Handle} - acquiring read lock");

        // Call runtime to acquire read lock
        // The runtime function returns a pointer to the inner data
        string innerPtr = GetNextTemp();
        _output.AppendLine(
            handler: $"  {innerPtr} = call ptr @razorforge_rwlock_read_lock(ptr {sourcePtr})");

        // Create handle as alias to inner data
        _output.AppendLine(handler: $"  {handleName} = bitcast ptr {innerPtr} to ptr");
        _symbolTypes[key: node.Handle] = "ptr";

        // Execute the body with read access
        node.Body.Accept(visitor: this);

        // Release the read lock
        _output.AppendLine(
            handler: $"  call void @razorforge_rwlock_read_unlock(ptr {sourcePtr})");
        _output.AppendLine(handler: $"  ; end inspecting {node.Handle} - read lock released");

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for a seizing statement (thread-safe scoped exclusive access).
    ///
    /// For multi-threaded code with Shared&lt;T, Policy&gt;, this acquires an exclusive lock.
    /// - Mutex policy: Simple mutex lock
    /// - MultiReadLock policy: Write lock (blocks all readers and writers)
    ///
    /// Syntax: seizing shared as handle { body }
    ///
    /// Generated IR:
    /// 1. Call runtime to acquire exclusive lock on Shared
    /// 2. Create handle pointing to inner data
    /// 3. Execute body
    /// 4. Call runtime to release exclusive lock
    /// </summary>
    public string VisitSeizingStatement(SeizingStatement node)
    {
        // Evaluate the source (should be a Shared<T, Policy>)
        string sourcePtr = node.Source.Accept(visitor: this);

        string handleName = $"%{node.Handle}";

        _output.AppendLine(
            handler: $"  ; seizing {node.Source} as {node.Handle} - acquiring exclusive lock");

        // Call runtime to acquire exclusive lock
        // The runtime function returns a pointer to the inner data
        string innerPtr = GetNextTemp();
        _output.AppendLine(
            handler: $"  {innerPtr} = call ptr @razorforge_mutex_lock(ptr {sourcePtr})");

        // Create handle as alias to inner data
        _output.AppendLine(handler: $"  {handleName} = bitcast ptr {innerPtr} to ptr");
        _symbolTypes[key: node.Handle] = "ptr";

        // Execute the body with exclusive access
        node.Body.Accept(visitor: this);

        // Release the exclusive lock
        _output.AppendLine(handler: $"  call void @razorforge_mutex_unlock(ptr {sourcePtr})");
        _output.AppendLine(handler: $"  ; end seizing {node.Handle} - exclusive lock released");

        return "";
    }
}
