using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing statement visitors (if, while, for, when, return, etc.).
/// </summary>
public partial class SemanticAnalyzer
{
    /// <summary>
    /// Visits an expression statement that evaluates an expression for its side effects.
    /// </summary>
    /// <param name="node">Expression statement node</param>
    /// <returns>Null</returns>
    public object? VisitExpressionStatement(ExpressionStatement node)
    {
        node.Expression.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Visits a declaration statement and registers the variable in the symbol table.
    /// Performs type checking and memory safety analysis.
    /// </summary>
    /// <param name="node">Declaration statement node</param>
    /// <returns>Null</returns>
    public object? VisitDeclarationStatement(DeclarationStatement node)
    {
        node.Declaration.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Analyze assignment statements with language-specific memory model handling.
    ///
    /// This method demonstrates the fundamental difference between RazorForge and Suflae:
    ///
    /// RazorForge: Assignments use move semantics - objects are transferred and source may become invalid.
    /// The analyzer needs sophisticated analysis to determine when moves occur vs copies.
    ///
    /// Suflae: Assignments use automatic reference counting - both source and target share the object
    /// with automatic RC increment. No invalidation occurs, promoting safe sharing.
    ///
    /// This difference reflects each language's memory management philosophy:
    /// explicit control vs automatic safety.
    /// </summary>
    public object? VisitAssignmentStatement(AssignmentStatement node)
    {
        // Standard type compatibility checking
        var targetType = node.Target.Accept(visitor: this) as TypeInfo;
        var valueType = node.Value.Accept(visitor: this) as TypeInfo;

        if (targetType != null && valueType != null &&
            !IsAssignable(target: targetType, source: valueType))
        {
            AddError(message: $"Cannot assign {valueType.Name} to {targetType.Name}",
                location: node.Location);
        }

        // CRITICAL: Check for inline-only method calls (.view(), .hijack())
        // These produce temporary tokens that cannot be stored via assignment
        if (IsInlineOnlyMethodCall(expr: node.Value, methodName: out string? methodName))
        {
            AddError(message: $"Cannot assign result of '.{methodName}()' to a variable. " +
                              $"Inline tokens must be used directly (e.g., 'obj.{methodName}().field') " +
                              $"or use scoped syntax (e.g., '{(methodName == "view" ? "viewing" : "hijacking")} obj as handle {{ ... }}').",
                location: node.Location);
        }

        // CRITICAL: Prevent mutation through read-only wrapper types
        // Viewed<T> and Observed<T> provide read-only access - cannot mutate through them
        if (node.Target is MemberExpression memberTarget)
        {
            var objectType = memberTarget.Object.Accept(visitor: this) as TypeInfo;
            if (objectType != null && IsReadOnlyWrapperType(typeName: objectType.Name))
            {
                AddError(
                    message:
                    $"Cannot mutate field through read-only wrapper '{objectType.Name}'. " +
                    $"Read-only wrappers (Viewed<T>, Observed<T>) do not allow mutation. " +
                    $"Use hijacking or seizing for mutable access.",
                    location: node.Location);
            }
        }

        // CRITICAL: Prevent scoped tokens from escaping their scope
        // Scoped tokens (Viewed, Hijacked, Seized, Observed) cannot be assigned to variables
        if (node.Value is IdentifierExpression valIdent &&
            IsScopedToken(variableName: valIdent.Name))
        {
            AddError(message: $"Cannot assign scoped token '{valIdent.Name}' to a variable. " +
                              $"Scoped tokens are bound to their declaring scope and cannot escape. " +
                              $"Use the token directly within the scoped statement block.",
                location: node.Location);
        }

        // CRITICAL: Language-specific memory model handling for assignments
        if (node.Target is IdentifierExpression targetId &&
            node.Value is IdentifierExpression valueId)
        {
            if (_language == Language.Suflae)
            {
                // Suflae: Automatic reference counting - both variables share the same object
                // Source remains valid, RC is incremented, no invalidation occurs
                _memoryAnalyzer.HandleSuflaeAssignment(target: targetId.Name,
                    source: valueId.Name,
                    location: node.Location);
            }
            else if (_language == Language.RazorForge)
            {
                // RazorForge: Move semantics - determine if assignment is copy or move
                if (targetType != null)
                {
                    bool isMove =
                        DetermineMoveSemantics(valueExpr: node.Value, targetType: targetType);

                    if (isMove)
                    {
                        // Move operation: Transfer ownership from source to target
                        if (node.Value is IdentifierExpression sourceId)
                        {
                            // In move semantics, the source is invalidated
                            // For now, we register the target and note that ownership transferred
                            // TODO: Add validation to prevent use-after-move for the source
                        }

                        // Register new object with ownership transferred
                        _memoryAnalyzer.RegisterObject(name: targetId.Name,
                            type: targetType,
                            location: node.Location);
                    }
                    else
                    {
                        // Copy operation: Create new reference
                        _memoryAnalyzer.RegisterObject(name: targetId.Name,
                            type: targetType,
                            location: node.Location);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Visits a return statement and validates the return value type.
    /// </summary>
    /// <param name="node">Return statement node</param>
    /// <returns>Null</returns>
    public object? VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value != null)
        {
            var returnType = node.Value.Accept(visitor: this) as TypeInfo;

            // CRITICAL: Prevent inline-only tokens from being returned (no-return rule)
            // .view() and .hijack() produce tokens that cannot escape the immediate expression
            if (IsInlineOnlyMethodCall(expr: node.Value, methodName: out string? methodName))
            {
                AddError(message: $"Cannot return result of '.{methodName}()' from a routine. " +
                                  $"Inline tokens (Viewed<T>, Hijacked<T>) cannot escape their usage context. " +
                                  $"Return the extracted value instead, or use a callback pattern.",
                    location: node.Location);
            }

            // CRITICAL: Prevent scoped tokens from escaping via return
            // Only usurping functions can return Hijacked<T> tokens
            // Viewed, Seized, Observed tokens can NEVER escape (even from usurping functions)
            if (node.Value is IdentifierExpression returnId &&
                IsScopedToken(variableName: returnId.Name))
            {
                // Check if this is a Hijacked<T> token and we're in a usurping function
                if (returnType != null && returnType.Name.StartsWith(value: "Hijacked<") &&
                    _isInUsurpingFunction)
                {
                    // Allowed: usurping functions can return Hijacked<T>
                }
                else
                {
                    string tokenType = returnType?.Name ?? "scoped token";
                    AddError(
                        message:
                        $"Cannot return scoped token '{returnId.Name}' of type {tokenType}. " +
                        $"Scoped tokens are bound to their declaring scope and cannot escape. " +
                        $"Only usurping functions can return Hijacked<T> tokens. " +
                        $"Viewed, Seized, and Observed tokens can never escape.",
                        location: node.Location);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Visits an if statement and validates the condition and branches.
    /// </summary>
    /// <param name="node">If statement node</param>
    /// <returns>Null</returns>
    public object? VisitIfStatement(IfStatement node)
    {
        // Check condition is boolean
        var conditionType = node.Condition.Accept(visitor: this) as TypeInfo;
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError(message: $"If condition must be boolean, got {conditionType.Name}",
                location: node.Location);
        }

        node.ThenStatement.Accept(visitor: this);
        node.ElseStatement?.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Visits a while loop statement and validates the condition and body.
    /// </summary>
    /// <param name="node">While statement node</param>
    /// <returns>Null</returns>
    public object? VisitWhileStatement(WhileStatement node)
    {
        // Check condition is boolean
        var conditionType = node.Condition.Accept(visitor: this) as TypeInfo;
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError(message: $"While condition must be boolean, got {conditionType.Name}",
                location: node.Location);
        }

        node.Body.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Visits a for loop statement and validates the iterator and body.
    /// </summary>
    /// <param name="node">For statement node</param>
    /// <returns>Null</returns>
    public object? VisitForStatement(ForStatement node)
    {
        // Enter new scope for loop variable
        _symbolTable.EnterScope();

        try
        {
            // Check iterable type
            var iterableType = node.Iterable.Accept(visitor: this) as TypeInfo;
            // TODO: Check if iterable implements Iterable interface

            // Add loop variable to scope
            var loopVarSymbol = new VariableSymbol(Name: node.Variable,
                Type: null,
                IsMutable: false,
                Visibility: VisibilityModifier.Private);
            _symbolTable.TryDeclare(symbol: loopVarSymbol);

            node.Body.Accept(visitor: this);
        }
        finally
        {
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits a when (pattern matching) statement and validates all pattern clauses.
    /// </summary>
    /// <param name="node">When statement node</param>
    /// <returns>Null</returns>
    public object? VisitWhenStatement(WhenStatement node)
    {
        // Set context flag - operations like try_seize/check_seize must be in when condition
        bool wasInWhenCondition = _isInWhenCondition;
        _isInWhenCondition = true;

        var expressionType = node.Expression.Accept(visitor: this) as TypeInfo;

        // Reset context after evaluating the condition
        _isInWhenCondition = wasInWhenCondition;

        foreach (WhenClause clause in node.Clauses)
        {
            // Enter new scope for pattern variables and scoped tokens
            _symbolTable.EnterScope();
            _memoryAnalyzer.EnterScope();
            _scopeDepth++;

            try
            {
                // Type check pattern against expression and bind pattern variables
                // This may register scoped tokens from fallible lock operations
                ValidatePatternMatch(pattern: clause.Pattern,
                    expressionType: expressionType,
                    location: clause.Location);

                clause.Body.Accept(visitor: this);
            }
            finally
            {
                // Clean up scoped tokens and restore sources when clause exits
                RestoreInvalidatedSources();
                ExitScopeCleanupTokens();
                _scopeDepth--;
                _memoryAnalyzer.ExitScope();
                _symbolTable.ExitScope();
            }
        }

        return null;
    }

    /// <summary>
    /// Analyze block statements with proper scope management for both symbols and memory objects.
    /// Block scopes are fundamental to memory safety - when a scope exits, all objects declared
    /// within become invalid (deadref protection). This prevents use-after-scope errors.
    ///
    /// The memory analyzer automatically invalidates all objects in the scope when it exits,
    /// implementing the core principle that objects cannot outlive their lexical scope.
    /// </summary>
    public object? VisitBlockStatement(BlockStatement node)
    {
        // Enter new lexical scope for both symbol resolution and memory tracking
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();

        try
        {
            // Analyze all statements within the protected scope
            foreach (Statement statement in node.Statements)
            {
                statement.Accept(visitor: this);
            }
        }
        finally
        {
            // CRITICAL: Scope cleanup automatically invalidates all objects in this scope
            // This is a fundamental memory safety mechanism preventing use-after-scope
            _symbolTable.ExitScope();
            _memoryAnalyzer.ExitScope(); // Invalidates all objects declared in this scope
        }

        return null;
    }

    /// <summary>
    /// Visits a break statement that exits a loop.
    /// </summary>
    /// <param name="node">Break statement node</param>
    /// <returns>Null</returns>
    public object? VisitBreakStatement(BreakStatement node)
    {
        return null;
    }
    /// <summary>
    /// Visits a continue statement that skips to the next loop iteration.
    /// </summary>
    /// <param name="node">Continue statement node</param>
    /// <returns>Null</returns>
    public object? VisitContinueStatement(ContinueStatement node)
    {
        return null;
    }

    /// <summary>
    /// Visits a throw statement that returns an error via Result.
    /// Validates that only Crashable types can be thrown.
    /// </summary>
    /// <param name="node">Throw statement node</param>
    /// <returns>Null</returns>
    public object? VisitThrowStatement(ThrowStatement node)
    {
        // Visit the error expression
        node.Error.Accept(visitor: this);

        // Validate that throw only accepts Crashable types, not string literals
        if (node.Error is LiteralExpression literal)
        {
            if (literal.Value is string)
            {
                AddError(
                    message:
                    "Cannot throw a string literal. Use a Crashable error type instead (e.g., throw MyError())",
                    location: node.Location);
            }
            else
            {
                AddError(
                    message: "Cannot throw a literal value. Use a Crashable error type instead",
                    location: node.Location);
            }

            return null;
        }

        // Check if it's a call expression to a Crashable type constructor
        if (node.Error is CallExpression callExpr)
        {
            string? typeName = null;
            if (callExpr.Callee is IdentifierExpression ident)
            {
                typeName = ident.Name;
            }

            if (typeName != null)
            {
                Symbol? symbol = _symbolTable.Lookup(name: typeName);
                if (symbol is StructSymbol structSymbol)
                {
                    if (!structSymbol.IsCrashable)
                    {
                        AddError(
                            message:
                            $"Cannot throw '{typeName}': type does not implement Crashable feature",
                            location: node.Location);
                    }
                }
                else if (symbol == null)
                {
                    // Type not found - will be caught by other semantic checks
                }
                else
                {
                    AddError(
                        message:
                        $"Cannot throw '{typeName}': only Crashable record types can be thrown",
                        location: node.Location);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Visits an absent statement that indicates value not found.
    /// </summary>
    /// <param name="node">Absent statement node</param>
    /// <returns>Null</returns>
    public object? VisitAbsentStatement(AbsentStatement node)
    {
        // No expression to visit for absent
        return null;
    }
}
