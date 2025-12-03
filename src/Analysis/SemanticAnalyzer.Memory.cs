using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing memory model helpers (operations, tokens, wrappers).
/// </summary>
public partial class SemanticAnalyzer
{
    /// <summary>
    /// Detect memory operation method calls by their distinctive '!' suffix.
    ///
    /// Memory operations are the heart of RazorForge's explicit memory model:
    /// - retain() - create single-threaded RC (green group)
    /// - share() - create multi-threaded RC with policy (blue group)
    /// - track() - create weak reference (green/blue group)
    /// - recover!() - upgrade weak to strong (crashes if dead)
    /// - try_recover() - upgrade weak to strong (returns Maybe)
    /// - snatch!() - force ownership (danger! only)
    /// - release!() - manual RC decrement
    /// - reveal!(), own!() - handle snatched objects (danger! only)
    ///
    /// Scoped access constructs (compile-time borrows):
    /// - viewing/hijacking - immutable/mutable borrow
    /// - inspecting/seizing - runtime-locked immutable/mutable access
    ///
    /// The '!' suffix indicates these operations can potentially crash/panic
    /// if used incorrectly, emphasizing their power and responsibility.
    /// </summary>
    /// <param name="methodName">Method name to check</param>
    /// <returns>True if this is a memory operation method</returns>
    private bool IsMemoryOperation(string methodName)
    {
        return methodName switch
        {
            // Core memory transformation operations
            "retain" or "share" or "track" or "snatch!" or "try_recover" or "recover!"
                or "try_seize" or "check_seize" or "try_inspect" or "check_inspect" => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if an expression is an inline-only method call (.view() or .hijack()).
    ///
    /// These methods produce temporary tokens (Viewed&lt;T&gt; or Hijacked&lt;T&gt;) that:
    /// - Cannot be stored in variables (inline-only)
    /// - Cannot be returned from routines (no-return rule)
    /// - Must be used directly: obj.view().field or obj.hijack().method()
    ///
    /// For multiple operations on the same entity, use scoped syntax instead:
    /// - viewing obj as v { ... }
    /// - hijacking obj as h { ... }
    ///
    /// This restriction eliminates the need for lifetime annotations by ensuring
    /// tokens never escape their immediate usage context.
    /// </summary>
    /// <param name="expr">Expression to check</param>
    /// <param name="methodName">Output: the method name if inline-only, null otherwise</param>
    /// <returns>True if this is an inline-only method call</returns>
    private bool IsInlineOnlyMethodCall(Expression expr, out string? methodName)
    {
        methodName = null;

        // Check for method call pattern: obj.view() or obj.hijack()
        if (expr is CallExpression call && call.Callee is MemberExpression member)
        {
            if (member.PropertyName is "view" or "hijack")
            {
                methodName = member.PropertyName;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detect usurping functions through naming conventions (temporary implementation).
    ///
    /// Usurping functions are special RazorForge functions that can return exclusive tokens
    /// (Hijacked&lt;T&gt; objects). This prevents accidental exclusive token leakage from regular
    /// functions, which would violate exclusive access guarantees.
    ///
    /// TODO: This should be replaced with an IsUsurping property on FunctionDeclaration
    /// for proper language support. Current implementation uses naming heuristics.
    ///
    /// Examples of usurping functions:
    /// - usurping public routine __create___exclusive() -> Hijacked&lt;Node&gt;
    /// - usurping routine factory_method() -> Hijacked&lt;Widget&gt;
    /// </summary>
    /// <param name="node">Function declaration to check</param>
    /// <returns>True if this function can return exclusive tokens</returns>
    private bool CheckIfUsurpingFunction(FunctionDeclaration node)
    {
        // Temporary heuristic-based detection
        // TODO: Replace with proper AST property when language syntax is finalized
        return node.Name.StartsWith(value: "usurping_") || node.Name.Contains(value: "Usurping");
    }

    /// <summary>
    /// Handle memory operation method calls - the core of RazorForge's memory model.
    ///
    /// This method processes calls like obj.retain(), obj.share(), etc., which are
    /// the primary way programmers interact with RazorForge's explicit memory management.
    ///
    /// The process:
    /// 1. Extract the object name (currently limited to simple identifiers)
    /// 2. Parse the operation name to identify the specific memory operation
    /// 3. Delegate to MemoryAnalyzer for ownership tracking and safety validation
    /// 4. Create appropriate wrapper type information for the result
    ///
    /// Memory operations transform objects between wrapper types while enforcing
    /// safety rules like group separation, reference count constraints, and
    /// use-after-invalidation prevention.
    /// </summary>
    /// <param name="memberExpr">Member expression (obj.method!)</param>
    /// <param name="operationName">Name of memory operation (e.g., "share")</param>
    /// <param name="arguments">Method arguments (usually empty for memory ops)</param>
    /// <param name="location">Source location for error reporting</param>
    /// <returns>Wrapper type info for the result, or None if operation failed</returns>
    private TypeInfo? HandleMemoryOperationCall(MemberExpression memberExpr, string operationName,
        List<Expression> arguments, SourceLocation location)
    {
        // Extract object name - currently limited to simple identifiers
        // TODO: Support more complex expressions like container[index].share()
        if (memberExpr.Object is not IdentifierExpression objId)
        {
            AddError(message: "Memory operations can only be called on simple identifiers",
                location: location);
            return null;
        }

        // Parse operation name to memory operation enum
        MemoryOperation? operation = ParseMemoryOperation(operationName: operationName);
        if (operation == null)
        {
            AddError(message: $"Unknown memory operation: {operationName}", location: location);
            return null;
        }

        // Validate that fallible lock operations are used in 'when' context
        if (RequiresWhenContext(operation: operation.Value) && !_isInWhenCondition)
        {
            AddError(
                message:
                $"Operation '{operationName}' returns a scope-bound token and must be used directly in a 'when' expression. " +
                $"Tokens cannot be stored in variables.",
                location: location);
            return null;
        }

        // Extract policy argument for share()
        LockingPolicy? policy = null;
        if (operation == MemoryOperation.Share)
        {
            // share(Mutex) or share(MultiReadLock)
            // For now, accept policy as a simple identifier argument
            if (arguments.Count > 0 && arguments[index: 0] is IdentifierExpression policyId)
            {
                policy = policyId.Name switch
                {
                    "Mutex" => LockingPolicy.Mutex,
                    "MultiReadLock" => LockingPolicy.MultiReadLock,
                    _ => null
                };

                if (policy == null)
                {
                    AddError(
                        message:
                        $"Invalid policy '{policyId.Name}'. Expected 'Mutex' or 'MultiReadLock'",
                        location: location);
                    return null;
                }
            }
            else
            {
                // Default to Mutex if no policy specified
                policy = LockingPolicy.Mutex;
            }
        }

        // CRITICAL: Delegate to memory analyzer for safety validation and ownership tracking
        // This is where all the memory safety magic happens
        MemoryObject? resultObj = _memoryAnalyzer.HandleMemoryOperation(objectName: objId.Name,
            operation: operation.Value,
            location: location,
            policy: policy);
        if (resultObj == null)
        {
            // Operation failed - error already reported by memory analyzer
            return null;
        }

        // Create type information for the result wrapper type
        return CreateWrapperTypeInfo(baseType: resultObj.BaseType, wrapper: resultObj.Wrapper);
    }

    /// <summary>
    /// Parse memory operation method names to their corresponding enum values.
    ///
    /// This mapping connects the source code syntax (method names ending with '!')
    /// to the internal memory operation representation used by the memory analyzer.
    ///
    /// The systematic naming reflects the memory model's organization:
    /// <list type="bullet">
    /// <item>Single-threaded RC: retain, track</item>
    /// <item>Multi-threaded RC: share</item>
    /// <item>Weak reference ops: track, try_recover, recover!</item>
    /// <item>Unsafe operations (danger! only): snatch!</item>
    /// </list>
    /// </summary>
    /// <param name="operationName">Method name from source code</param>
    /// <returns>Corresponding memory operation enum, or null if not found</returns>
    private MemoryOperation? ParseMemoryOperation(string operationName)
    {
        return operationName switch
        {
            // Group 2: Single-threaded reference counting
            "retain" => MemoryOperation.Retain,
            "track" => MemoryOperation.Track,
            "try_recover" => MemoryOperation.Recover,
            "recover!" => MemoryOperation.Recover,

            // Group 3: Multi-threaded reference counting
            "share" => MemoryOperation.Share,

            // Fallible lock acquisition (must be used with 'when')
            "try_seize" => MemoryOperation.TrySeize,
            "check_seize" => MemoryOperation.CheckSeize,
            "try_inspect" => MemoryOperation.TryInsepct,
            "check_inspect" => MemoryOperation.CheckInspect,

            // Unsafe operations (danger! block only)
            "snatch!" => MemoryOperation.Snatch,
            _ => null
        };
    }

    private MemoryOperation? ParseMemoryOperation(string operationName, SourceLocation location)
    {
        return ParseMemoryOperation(operationName: operationName);
    }

    /// <summary>
    /// Check if a memory operation returns a scope-bound token that must be used in 'when' expression.
    /// These operations return Maybe&lt;Token&gt; or Result&lt;Token, Error&gt; that cannot be stored.
    /// </summary>
    /// <param name="operation">The memory operation to check</param>
    /// <returns>True if operation requires immediate 'when' usage</returns>
    private bool RequiresWhenContext(MemoryOperation operation)
    {
        return operation switch
        {
            MemoryOperation.TrySeize or MemoryOperation.CheckSeize or MemoryOperation.TryInsepct
                or MemoryOperation.CheckInspect => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if a type is a scoped token type that cannot escape its scope.
    /// Scoped tokens: Viewed&lt;T&gt;, Hijacked&lt;T&gt;, Seized&lt;T&gt;, Inspected&lt;T&gt;.
    /// These are created by scoped access statements (viewing, hijacking, seizing, inspecting).
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>True if type is a scoped token</returns>
    private bool IsScopedTokenType(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        string typeName = type.Name;
        return typeName.StartsWith(value: "Viewed<") || typeName.StartsWith(value: "Hijacked<") ||
               typeName.StartsWith(value: "Seized<") || typeName.StartsWith(value: "Inspected<");
    }

    /// <summary>
    /// Check if a type is a fallible lock token (from try_seize, check_seize, try_inspect, check_inspect).
    /// These operations return Maybe&lt;Seized&lt;T&gt;&gt;, Result&lt;Seized&lt;T&gt;, E&gt;,
    /// Maybe&lt;Inspected&lt;T&gt;&gt;, or Result&lt;Inspected&lt;T&gt;, E&gt;.
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>True if type is a fallible lock token wrapper</returns>
    private bool IsFallibleLockToken(TypeInfo? type)
    {
        if (type == null)
        {
            return false;
        }

        string typeName = type.Name;

        // Check for Maybe<Seized<T>> or Maybe<Inspected<T>>
        if (typeName.StartsWith(value: "Maybe<"))
        {
            string inner = ExtractGenericTypeArgument(typeName: typeName, wrapperName: "Maybe");
            return inner.StartsWith(value: "Seized<") || inner.StartsWith(value: "Inspected<");
        }

        // Check for Result<Seized<T>, E> or Result<Inspected<T>, E>
        if (typeName.StartsWith(value: "Result<"))
        {
            string firstArg =
                ExtractFirstGenericTypeArgument(typeName: typeName, wrapperName: "Result");
            return firstArg.StartsWith(value: "Seized<") ||
                   firstArg.StartsWith(value: "Inspected<");
        }

        return false;
    }

    /// <summary>
    /// Extract the type argument from a generic type (e.g., Maybe&lt;Seized&lt;T&gt;&gt; → Seized&lt;T&gt;).
    /// </summary>
    private string ExtractGenericTypeArgument(string typeName, string wrapperName)
    {
        int startIdx = typeName.IndexOf(value: '<');
        int endIdx = typeName.LastIndexOf(value: '>');

        if (startIdx > 0 && endIdx > startIdx)
        {
            return typeName.Substring(startIndex: startIdx + 1, length: endIdx - startIdx - 1);
        }

        return string.Empty;
    }

    /// <summary>
    /// Extract the first type argument from a multi-parameter generic type
    /// (e.g., Result&lt;Seized&lt;T&gt;, Error&gt; → Seized&lt;T&gt;).
    /// </summary>
    private string ExtractFirstGenericTypeArgument(string typeName, string wrapperName)
    {
        int startIdx = typeName.IndexOf(value: '<');
        if (startIdx <= 0)
        {
            return string.Empty;
        }

        // Find the matching comma that separates first and second type arguments
        // Need to track angle bracket depth to handle nested generics
        int depth = 0;
        for (int i = startIdx + 1; i < typeName.Length; i++)
        {
            if (typeName[index: i] == '<')
            {
                depth++;
            }
            else if (typeName[index: i] == '>')
            {
                depth--;
            }
            else if (typeName[index: i] == ',' && depth == 0)
            {
                return typeName.Substring(startIndex: startIdx + 1, length: i - startIdx - 1)
                               .Trim();
            }
        }

        // If no comma found, return the entire inner content
        int endIdx = typeName.LastIndexOf(value: '>');
        if (endIdx > startIdx)
        {
            return typeName.Substring(startIndex: startIdx + 1, length: endIdx - startIdx - 1)
                           .Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Register a scoped token variable that cannot escape the current scope.
    /// </summary>
    /// <param name="tokenName">Name of the token variable</param>
    private void RegisterScopedToken(string tokenName)
    {
        _scopedTokens[key: tokenName] = _scopeDepth;
    }

    /// <summary>
    /// Check if a variable is a scoped token that cannot escape its scope.
    /// </summary>
    /// <param name="variableName">Name of the variable to check</param>
    /// <returns>True if variable is a scoped token</returns>
    private bool IsScopedToken(string variableName)
    {
        return _scopedTokens.ContainsKey(key: variableName);
    }

    /// <summary>
    /// Clean up scoped tokens that are going out of scope.
    /// Called when exiting a scope to remove tokens bound to that scope.
    /// </summary>
    private void ExitScopeCleanupTokens()
    {
        // Remove all tokens that were created at the current scope depth
        var tokensToRemove = _scopedTokens.Where(predicate: kvp => kvp.Value == _scopeDepth)
                                          .Select(selector: kvp => kvp.Key)
                                          .ToList();

        foreach (string token in tokensToRemove)
        {
            _scopedTokens.Remove(key: token);
        }
    }

    /// <summary>
    /// Invalidate a source variable during a scoped access statement.
    /// The source cannot be accessed while the scoped token exists.
    /// </summary>
    /// <param name="sourceName">Name of the source variable to invalidate</param>
    /// <param name="accessType">Type of access (viewing, hijacking, seizing, inspecting)</param>
    private void InvalidateSource(string sourceName, string accessType)
    {
        _invalidatedSources[key: sourceName] = (_scopeDepth, accessType);
    }

    /// <summary>
    /// Check if a source variable is currently invalidated.
    /// </summary>
    /// <param name="sourceName">Name of the source variable</param>
    /// <returns>True if source is invalidated</returns>
    private bool IsSourceInvalidated(string sourceName)
    {
        return _invalidatedSources.ContainsKey(key: sourceName);
    }

    /// <summary>
    /// Get the access type for an invalidated source.
    /// </summary>
    /// <param name="sourceName">Name of the source variable</param>
    /// <returns>Access type (e.g., "viewing", "hijacking") or null if not invalidated</returns>
    private string? GetInvalidationAccessType(string sourceName)
    {
        return _invalidatedSources.TryGetValue(key: sourceName,
            value: out (int scopeDepth, string accessType) info)
            ? info.accessType
            : null;
    }

    /// <summary>
    /// Restore invalidated sources when exiting a scope.
    /// Called when a scoped statement exits to re-enable source access.
    /// </summary>
    private void RestoreInvalidatedSources()
    {
        // Remove all sources that were invalidated at the current scope depth
        var sourcesToRestore = _invalidatedSources
                              .Where(predicate: kvp => kvp.Value.scopeDepth == _scopeDepth)
                              .Select(selector: kvp => kvp.Key)
                              .ToList();

        foreach (string source in sourcesToRestore)
        {
            _invalidatedSources.Remove(key: source);
        }
    }

    /// <summary>
    /// Create TypeInfo instances for wrapper types in RazorForge's memory model.
    ///
    /// This method generates the type names that appear in the type system for
    /// memory-wrapped objects. Each wrapper type has a distinctive generic syntax:
    /// <list type="bullet">
    /// <item>Owned: Direct type name (Node, List&lt;s32&gt;)</item>
    /// <item>Hijacked&lt;T&gt;: Exclusive access wrapper (red group 🔴)</item>
    /// <item>Retained&lt;T&gt;: Single-threaded shared ownership wrapper (green group 🟢)</item>
    /// <item>Tracked&lt;T&gt;: Weak observer wrapper (brown group 🟤)</item>
    /// <item>Shared&lt;T, Policy&gt;: Multi-threaded shared wrapper (blue group 🔵)</item>
    /// <item>Snatched&lt;T&gt;: Contaminated ownership wrapper (black group 💀)</item>
    /// </list>
    ///
    /// These type names provide clear indication of memory semantics in error messages,
    /// IDE tooltips, and documentation.
    /// </summary>
    /// <param name="baseType">Underlying object type</param>
    /// <param name="wrapper">Memory wrapper type</param>
    /// <returns>TypeInfo with appropriate wrapper type name</returns>
    private TypeInfo CreateWrapperTypeInfo(TypeInfo baseType, WrapperType wrapper)
    {
        string typeName = wrapper switch
        {
            // Direct ownership - no wrapper syntax
            WrapperType.Owned => baseType.Name,

            // Memory wrapper types with generic syntax
            WrapperType.Hijacked => $"Hijacked<{baseType.Name}>", // Exclusive access 🔴
            WrapperType.Retained => $"Retained<{baseType.Name}>", // Single-threaded RC 🟢
            WrapperType.Tracked => $"Tracked<{baseType.Name}>", // Weak observer 🟤
            WrapperType.Shared => $"Shared<{baseType.Name}>", // Thread-safe shared 🔵
            WrapperType.Snatched => $"Snatched<{baseType.Name}>", // Contaminated ownership 💀

            _ => baseType.Name
        };

        return new TypeInfo(Name: typeName, IsReference: baseType.IsReference);
    }
}
