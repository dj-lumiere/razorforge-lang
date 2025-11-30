using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing scoped access statement visitors (viewing, hijacking, observing, seizing).
/// </summary>
public partial class SemanticAnalyzer
{
       /// <summary>
    /// Visits a viewing statement node (scoped read-only access).
    /// Syntax: viewing &lt;source&gt; as &lt;handle&gt; { ... }
    /// Creates a temporary Viewed&lt;T&gt; handle with read-only access.
    /// </summary>
    public object? VisitViewingStatement(ViewingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot view expression with unknown type", location: node.Location);
            return null;
        }

        // Create a new scope for the viewing block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create a Viewed<T> type for the handle
            var viewedType = new TypeInfo(Name: $"Viewed<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: viewedType, Visibility: VisibilityModifier.Private, IsMutable: false);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope", location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            RegisterScopedToken(tokenName: node.Handle);

            // CRITICAL: Invalidate source during scope - prevent concurrent access
            // The source should not be accessible while the Viewed<T> token exists
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceName: sourceIdent.Name, accessType: "viewing");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits a hijacking statement node (scoped exclusive access).
    /// Syntax: hijacking &lt;source&gt; as &lt;handle&gt; { ... }
    /// Creates a temporary Hijacked&lt;T&gt; handle with exclusive write access.
    /// </summary>
    public object? VisitHijackingStatement(HijackingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot hijack expression with unknown type", location: node.Location);
            return null;
        }

        // Create a new scope for the hijacking block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create a Hijacked<T> type for the handle
            var hijackedType = new TypeInfo(Name: $"Hijacked<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: hijackedType, Visibility: VisibilityModifier.Private, IsMutable: true);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope", location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            // Note: Hijacked<T> CAN escape from usurping functions, but that's validated in return statement
            RegisterScopedToken(tokenName: node.Handle);

            // CRITICAL: Invalidate source during scope - prevent concurrent access
            // The source should not be accessible while the Hijacked<T> token exists
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceName: sourceIdent.Name, accessType: "hijacking");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits an observing statement node (thread-safe scoped read access).
    /// Syntax: observing &lt;handle&gt; from &lt;source&gt;: { ... }
    /// Creates a temporary Observed&lt;T&gt; handle with shared read lock.
    /// IMPORTANT: Only works with Shared&lt;T, MultiReadLock&gt;, not Shared&lt;T, Mutex&gt;.
    /// </summary>
    public object? VisitObservingStatement(ObservingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot observe expression with unknown type", location: node.Location);
            return null;
        }

        // Extract the source object to check its policy
        if (node.Source is IdentifierExpression sourceId)
        {
            MemoryObject? sourceObj = _memoryAnalyzer.GetObject(name: sourceId.Name);
            if (sourceObj != null)
            {
                // COMPILE-TIME CHECK: observing requires MultiReadLock policy
                if (sourceObj.Wrapper == WrapperType.Shared && sourceObj.Policy != LockingPolicy.MultiReadLock)
                {
                    AddError(message: $"observing requires Shared<T, MultiReadLock>. " + $"Object '{sourceId.Name}' has policy {sourceObj.Policy}. " + $"Use seizing for exclusive access, or create with MultiReadLock policy.", location: node.Location);
                    return null;
                }
            }
        }

        // Create a new scope for the observing block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create an Observed<T> type for the handle (not Witnessed<T>)
            var observedType = new TypeInfo(Name: $"Observed<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: observedType, Visibility: VisibilityModifier.Private, IsMutable: false);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope", location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            RegisterScopedToken(tokenName: node.Handle);

            // CRITICAL: Acquire read lock on source - invalidate during lock
            // The source Shared<T, MultiReadLock> must have its read lock acquired
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceName: sourceIdent.Name, accessType: "observing");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - read lock released, handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits a seizing statement node (thread-safe scoped exclusive access).
    /// Syntax: seizing &lt;handle&gt; from &lt;source&gt;: { ... }
    /// Creates a temporary Seized&lt;T&gt; handle with exclusive write lock.
    /// Works with both Vault&lt;T, Mutex&gt; and Vault&lt;T, MultiReadLock&gt;.
    /// </summary>
    public object? VisitSeizingStatement(SeizingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot seize expression with unknown type", location: node.Location);
            return null;
        }

        // Create a new scope for the seizing block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create a Seized<T> type for the handle
            var seizedType = new TypeInfo(Name: $"Seized<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: seizedType, Visibility: VisibilityModifier.Private, IsMutable: true);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope", location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            RegisterScopedToken(tokenName: node.Handle);

            // CRITICAL: Acquire write lock on source - invalidate during lock
            // The source Shared<T, Policy> must have its write lock acquired
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceName: sourceIdent.Name, accessType: "seizing");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - write lock released, handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }
}
