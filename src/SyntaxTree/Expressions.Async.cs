namespace SyntaxTree;

#region Async Expressions

/// <summary>
/// Expression that waits for a suspended computation to complete.
/// Used in suspended/threaded routines to await Task-like values.
/// </summary>
/// <param name="Operand">The suspended computation to wait for</param>
/// <param name="Timeout">Optional timeout duration (with 'within' keyword)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Waitfor expression examples:
/// <list type="bullet">
/// <item>waitfor task - wait for task to complete</item>
/// <item>waitfor task within 5s - wait with 5 second timeout</item>
/// <item>waitfor http.get(url) within 30s - wait for HTTP request with timeout</item>
/// </list>
/// Waitfor can only be used inside suspended/threaded routines.
/// </remarks>
public record WaitforExpression(Expression Operand, Expression? Timeout, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitWaitforExpression(node: this);
    }
}

/// <summary>
/// Represents a single task dependency in an 'after' clause.
/// Specifies a Lookup&lt;T&gt; variable to depend on and an optional binding name.
/// </summary>
/// <param name="DependencyExpr">The Lookup&lt;T&gt; variable to depend on</param>
/// <param name="BindingName">Optional 'as' binding name for the unwrapped value (null if no 'as')</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Task dependency examples:
/// <list type="bullet">
/// <item>after a - dependency without value binding (ordering only)</item>
/// <item>after a as val - dependency with value binding</item>
/// <item>after (a, b) as (va, vb) - tuple dependencies with bindings</item>
/// </list>
/// The binding name introduces a variable of type T (unwrapped from Lookup&lt;T&gt;)
/// that is only valid within the associated waitfor expression.
/// </remarks>
public record TaskDependency(
    Expression DependencyExpr,
    string? BindingName,
    SourceLocation Location);

/// <summary>
/// Expression that waits for a suspended computation with explicit task dependencies.
/// Extends waitfor with 'after' clause for declarative task dependency graphs.
/// </summary>
/// <param name="Dependencies">List of 'after' dependencies (empty means no dependencies)</param>
/// <param name="Operand">The suspended computation to wait for</param>
/// <param name="Timeout">Optional timeout duration (with 'within' keyword)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Dependent waitfor expression examples:
/// <list type="bullet">
/// <item>after a as val waitfor step2!(val) within 5s - single dependency with timeout</item>
/// <item>after (a, b) as (va, vb) waitfor step3!(va, vb) - multiple dependencies</item>
/// <item>after a waitfor step2!() - ordering-only dependency (no value binding)</item>
/// </list>
/// If any dependency fails (error/None), dependent tasks are not spawned (short-circuit).
/// The result type is Lookup&lt;R&gt; where R is the return type of the awaited task.
/// </remarks>
public record DependentWaitforExpression(
    List<TaskDependency> Dependencies,
    Expression Operand,
    Expression? Timeout,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDependentWaitforExpression(node: this);
    }
}

#endregion

#region Back Index Expressions

/// <summary>
/// Expression that creates an index from the end of a sequence.
/// The ^ prefix operator creates a back-index value that counts from the end.
/// </summary>
/// <param name="Operand">The index offset from the end (^1 = last, ^2 = second to last)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Back index expression examples:
/// <list type="bullet">
/// <item>^1 - last element</item>
/// <item>^2 - second to last element</item>
/// <item>list[^1] - access last element of list</item>
/// <item>text[^3] - access third to last character</item>
/// </list>
/// The operand must be a non-negative integer. ^0 is valid but refers to "one past the end".
/// Used with IndexExpression for end-relative indexing.
/// </remarks>
public record BackIndexExpression(Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitBackIndexExpression(node: this);
    }
}

#endregion

#region Pattern Matching Expressions

/// <summary>
/// Expression that performs pattern matching with 'is' or 'isnot' operators.
/// Supports type checking, variable binding, and destructuring.
/// </summary>
/// <param name="Expression">The expression being matched against the pattern</param>
/// <param name="Pattern">The pattern to match (TypePattern, DestructuringPattern, etc. from Statements.cs)</param>
/// <param name="IsNegated">True for 'isnot', false for 'is'</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Pattern matching expression examples:
/// <list type="bullet">
/// <item>value is s32 - type check</item>
/// <item>value is Text t - type check with binding</item>
/// <item>value is Point (x, y) - destructuring</item>
/// <item>value isnot None - negated type check</item>
/// </list>
/// Note: 'isnot' cannot have bindings or destructuring (what would you bind to if not matched?).
/// </remarks>
public record IsPatternExpression(
    Expression Expression,
    Pattern Pattern,
    bool IsNegated,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitIsPatternExpression(node: this);
    }
}

/// <summary>Flags test kind: is or isnot.</summary>
public enum FlagsTestKind
{
    /// <summary>Tests whether the subject has the specified flags.</summary>
    Is,

    /// <summary>Tests whether the subject does not have the specified flags.</summary>
    IsNot
}

/// <summary>Connective for combining flag names in a flags test.</summary>
public enum FlagsTestConnective
{
    /// <summary>All named flags must match.</summary>
    And,

    /// <summary>Any named flag may match.</summary>
    Or
}

/// <summary>
/// Expression that tests whether a flags value has, lacks, or exactly matches specific flags.
/// </summary>
/// <param name="Subject">The expression being tested (must be a flags type)</param>
/// <param name="Kind">The kind of test: is or isnot</param>
/// <param name="TestFlags">The flag names being tested for</param>
/// <param name="Connective">How the flags are combined: and (all required) or or (any required)</param>
/// <param name="ExcludedFlags">Optional flags to exclude with 'but' (only valid with And connective)</param>
/// <param name="Location">Source location information</param>
public record FlagsTestExpression(
    Expression Subject,
    FlagsTestKind Kind,
    List<string> TestFlags,
    FlagsTestConnective Connective,
    List<string>? ExcludedFlags,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitFlagsTestExpression(node: this);
    }
}

/// <summary>
/// Pattern matching expression that evaluates to a value based on pattern matching.
/// Similar to Rust's match expression or Kotlin's when expression.
/// </summary>
/// <param name="Expression">The value being matched against patterns</param>
/// <param name="Clauses">The list of pattern-body pairs to evaluate</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// When expression examples:
/// <list type="bullet">
/// <item>return when x { 0 => "zero", 1 => "one", else => "many" }</item>
/// <item>var desc = when status { is ACTIVE => "Running", else => "Not running" }</item>
/// </list>
/// The body of each clause must evaluate to a value of the same type.
/// </remarks>
public record WhenExpression(
    Expression? Expression,
    List<WhenClause> Clauses,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitWhenExpression(node: this);
    }
}

#endregion
