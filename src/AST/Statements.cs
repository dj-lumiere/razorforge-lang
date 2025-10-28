namespace Compilers.Shared.AST;

#region Base Statement Types

/// <summary>
/// Base entity for all statement nodes in the AST.
/// Statements are executable constructs that perform actions but do not produce values.
/// They represent imperative commands that change program state or control flow.
/// </summary>
/// <param name="Location">Source location information for error reporting and debugging</param>
/// <remarks>
/// Statements form the backbone of imperative programming constructs:
/// <list type="bullet">
/// <item>Control flow statements (if, while, for, when)</item>
/// <item>Assignment and variable manipulation</item>
/// <item>Expression evaluation for side effects</item>
/// <item>Function returns and loop control</item>
/// </list>
/// </remarks>
public abstract record Statement(SourceLocation Location) : AstNode(Location: Location);

#endregion

#region Simple Statements

/// <summary>
/// Statement that evaluates an expression for its side effects.
/// Used when an expression is called primarily for its side effects rather than its return value.
/// </summary>
/// <param name="Expression">The expression to evaluate</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Common uses include:
/// <list type="bullet">
/// <item>Function calls that modify state: print("Hello"), array.append(item)</item>
/// <item>Method invocations: object.doSomething()</item>
/// <item>Assignment operators: x += 5</item>
/// </list>
/// </remarks>
public record ExpressionStatement(Expression Expression, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitExpressionStatement(node: this);
    }
}

/// <summary>
/// Statement that wraps a declaration when it appears in statement context.
/// Used when declarations like variable declarations appear inside function bodies or blocks.
/// </summary>
/// <param name="Declaration">The declaration to wrap as a statement</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// This allows declarations to be used in contexts that expect statements:
/// <list type="bullet">
/// <item>Variable declarations inside function bodies: var x = 5</item>
/// <item>Local declarations inside blocks: let y = computeValue()</item>
/// <item>Nested function definitions inside other functions</item>
/// </list>
/// </remarks>
public record DeclarationStatement(Declaration Declaration, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitDeclarationStatement(node: this);
    }
}

/// <summary>
/// Assignment statement that stores a value into a target location.
/// Represents the assignment operator (=) in both RazorForge and Cake.
/// </summary>
/// <param name="Target">The left-hand side target to assign to (variable, property, array element)</param>
/// <param name="Value">The right-hand side expression whose value will be assigned</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports various assignment targets:
/// <list type="bullet">
/// <item>Variables: x = 42</item>
/// <item>Object properties: obj.field = value</item>
/// <item>Array elements: arr[index] = item</item>
/// <item>Chained assignments: a.b.c = value</item>
/// </list>
/// </remarks>
public record AssignmentStatement(Expression Target, Expression Value, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitAssignmentStatement(node: this);
    }
}

/// <summary>
/// Return statement that exits from a function with an optional value.
/// Used to terminate function execution and optionally return a result to the caller.
/// </summary>
/// <param name="Value">Optional expression to return as the function result; null for void returns</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Behavior varies by context:
/// <list type="bullet">
/// <item>In functions with return type: must provide compatible value</item>
/// <item>In void functions: value must be null</item>
/// <item>In procedures/recipes: may omit return statement (implicit void return)</item>
/// <item>Multiple returns allowed; first encountered terminates execution</item>
/// </list>
/// </remarks>
public record ReturnStatement(Expression? Value, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitReturnStatement(node: this);
    }

    /// <summary>
    /// Compatibility property for tests that expect ReturnStatement.Expression
    /// </summary>
    public Expression? Expression => Value;
}

#endregion

#region Control Flow Statements

/// <summary>
/// Conditional statement that executes different code paths based on a boolean condition.
/// Represents if-then-else constructs in both RazorForge and Cake.
/// </summary>
/// <param name="Condition">Boolean expression to evaluate for branching decision</param>
/// <param name="ThenStatement">Statement to execute when condition is true</param>
/// <param name="ElseStatement">Optional statement to execute when condition is false</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports both simple and complex branching:
/// <list type="bullet">
/// <item>Simple if: if (x > 0) print("positive")</item>
/// <item>If-else: if (x > 0) print("positive") else print("not positive")</item>
/// <item>Nested conditions: if (x > 0) if (x < 10) print("single digit")</item>
/// <item>Block statements: if (condition) { multiple; statements; }</item>
/// </list>
/// </remarks>
public record IfStatement(
    Expression Condition,
    Statement ThenStatement,
    Statement? ElseStatement,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIfStatement(node: this);
    }

    /// <summary>
    /// Compatibility properties for tests that expect ThenBranch and ElseBranch
    /// </summary>
    public Statement ThenBranch => ThenStatement;

    public Statement? ElseBranch => ElseStatement;
}

/// <summary>
/// Loop statement that repeatedly executes a body while a condition remains true.
/// Represents while loops that check the condition before each iteration.
/// </summary>
/// <param name="Condition">Boolean expression evaluated before each iteration</param>
/// <param name="Body">Statement to execute repeatedly while condition is true</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Loop behavior characteristics:
/// <list type="bullet">
/// <item>Pre-condition check: body may not execute if condition is initially false</item>
/// <item>Supports break and continue statements for flow control</item>
/// <item>Infinite loops possible if condition never becomes false</item>
/// <item>Body can be single statement or block statement</item>
/// </list>
/// </remarks>
public record WhileStatement(Expression Condition, Statement Body, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitWhileStatement(node: this);
    }
}

/// <summary>
/// Iterator loop statement that executes a body for each element in an iterable collection.
/// Represents for-in loops that automatically handle iteration over collections.
/// </summary>
/// <param name="Variable">Loop variable name that receives each element value</param>
/// <param name="Iterable">Expression that evaluates to an iterable collection (arrays, ranges, strings)</param>
/// <param name="Body">Statement to execute for each iteration with the loop variable bound</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports iteration over various collection types:
/// <list type="bullet">
/// <item>Arrays: for item in [1, 2, 3]</item>
/// <item>Ranges: for i in (0 to 10)</item>
/// <item>Strings: for char in "hello"</item>
/// <item>Custom iterables implementing iteration protocols</item>
/// </list>
/// </remarks>
public record ForStatement(
    string Variable,
    Expression Iterable,
    Statement Body,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitForStatement(node: this);
    }
}

/// <summary>
/// Compound statement that groups multiple statements into a single logical unit.
/// Represents block statements enclosed in braces that create new lexical scopes.
/// </summary>
/// <param name="Statements">Ordered list of statements to execute sequentially</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Block statement characteristics:
/// <list type="bullet">
/// <item>Sequential execution: statements execute in declaration order</item>
/// <item>Lexical scoping: creates new scope for variable declarations</item>
/// <item>Early termination: return, break, continue can exit block early</item>
/// <item>Nested blocks: blocks can contain other block statements</item>
/// </list>
/// </remarks>
public record BlockStatement(List<Statement> Statements, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitBlockStatement(node: this);
    }
}

/// <summary>
/// Pattern matching statement that executes different code based on value structure.
/// Represents when/match expressions that provide powerful destructuring and conditional logic.
/// </summary>
/// <param name="Expression">Expression whose value will be matched against patterns</param>
/// <param name="Clauses">List of pattern-action pairs with optional guard conditions</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Advanced pattern matching features:
/// <list type="bullet">
/// <item>Structural matching: when value { Point(x, y) => ... }</item>
/// <item>Guard conditions: when x { n if n > 0 => ... }</item>
/// <item>Exhaustiveness checking: compiler ensures all cases are covered</item>
/// <item>Variable binding: patterns can extract and bind values</item>
/// </list>
/// </remarks>
public record WhenStatement(
    Expression Expression,
    List<WhenClause> Clauses,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitWhenStatement(node: this);
    }
}

/// <summary>
/// Pattern matching clause that associates a pattern with an action and optional guard.
/// Used within when statements to define conditional execution paths.
/// </summary>
/// <param name="Pattern">Pattern to match against the when expression value</param>
/// <param name="Body">Statement to execute when pattern matches and guard (if present) is true</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Guards provide additional filtering beyond structural pattern matching.
/// </remarks>
public record WhenClause(Pattern Pattern, Statement Body, SourceLocation Location);

/// <summary>
/// Control flow statement that immediately exits the innermost loop.
/// Transfers control to the statement following the loop construct.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Break statement behavior:
/// <list type="bullet">
/// <item>Exits innermost loop only (while, for, when with iteration)</item>
/// <item>Compile-time error if used outside loop context</item>
/// <item>Skips any remaining loop iterations</item>
/// <item>Continues execution after loop construct</item>
/// </list>
/// </remarks>
public record BreakStatement(SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitBreakStatement(node: this);
    }
}

/// <summary>
/// Control flow statement that skips to the next iteration of the innermost loop.
/// Jumps to the loop condition check, skipping remaining statements in current iteration.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Continue statement behavior:
/// <list type="bullet">
/// <item>Skips remaining statements in current loop iteration</item>
/// <item>Returns to loop condition evaluation (while) or next element (for)</item>
/// <item>Compile-time error if used outside loop context</item>
/// <item>Does not exit the loop, only skips current iteration</item>
/// </list>
/// </remarks>
public record ContinueStatement(SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitContinueStatement(node: this);
    }
}

#endregion

#region Pattern Matching

/// <summary>
/// Base entity for patterns used in when/match statements for structural matching.
/// Patterns define how to match and destructure values during pattern matching operations.
/// </summary>
/// <param name="Location">Source location information for error reporting</param>
/// <remarks>
/// Pattern types support various matching scenarios:
/// <list type="bullet">
/// <item>Value matching: exact literal comparison</item>
/// <item>Type matching: structural type checking with optional binding</item>
/// <item>Variable binding: capturing matched values for use in action</item>
/// <item>Wildcard matching: accepting any value without binding</item>
/// </list>
/// </remarks>
public abstract record Pattern(SourceLocation Location);

/// <summary>
/// Pattern that matches exact literal values like numbers, strings, or booleans.
/// Used for precise value comparison in pattern matching constructs.
/// </summary>
/// <param name="Value">The exact value to match against (42, "hello", true, etc.)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples: when x { 42 => ..., "hello" => ..., true => ... }
/// </remarks>
public record LiteralPattern(object Value, SourceLocation Location) : Pattern(Location: Location);

/// <summary>
/// Pattern that binds a matched value to a variable name for use in the action.
/// Creates a new variable in the action scope containing the matched value.
/// </summary>
/// <param name="Name">Variable name to bind the matched value to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Example: when someValue { x => print(x) } binds someValue to x.
/// </remarks>
public record IdentifierPattern(string Name, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern that matches values of a specific type, optionally binding to a variable.
/// Performs runtime type checking and optional destructuring of typed values.
/// </summary>
/// <param name="Type">Type expression specifying the type to match</param>
/// <param name="VariableName">Optional variable name to bind the typed value to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>when obj { String => ... } matches any string</item>
/// <item>when obj { Point p => ... } matches Point and binds to p</item>
/// </list>
/// </remarks>
public record TypePattern(TypeExpression Type, string? VariableName, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern that matches any value without binding it to a variable.
/// Used as a catch-all pattern, typically in the final clause of pattern matching.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>when value { _ => print("default case") }</item>
/// <item>Often used for exhaustive pattern matching</item>
/// </list>
/// </remarks>
public record WildcardPattern(SourceLocation Location) : Pattern(Location: Location);

#endregion

#region Unsafe Statements

/// <summary>
/// Statement representing a danger! block that disables safety checks.
/// Allows access to unsafe memory operations and bypasses normal language restrictions.
/// </summary>
/// <param name="Body">Block statement containing the unsafe operations</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Danger blocks enable low-level memory operations:
/// <list type="bullet">
/// <item>Raw memory access with read_as! and write_as!</item>
/// <item>Volatile operations for memory-mapped I/O</item>
/// <item>Type punning and pointer arithmetic</item>
/// <item>Forced object invalidation</item>
/// <item>Bypassing bounds checking and null checks</item>
/// </list>
/// Should be used sparingly and with extreme caution.
/// </remarks>
public record DangerStatement(BlockStatement Body, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitDangerStatement(node: this);
    }
}

/// <summary>
/// Represents a mayhem block statement for ultimate unsafe operations.
/// Mayhem blocks enable the most dangerous features like runtime method replacement and code execution.
/// </summary>
/// <remarks>
/// A mayhem block provides access to the Reality Bender's Metamorph module
/// which can modify program structure at runtime. Should almost never be used.
/// </remarks>
public record MayhemStatement(BlockStatement Body, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitMayhemStatement(node: this);
    }
}

/// <summary>
/// Represents a viewing block statement for scoped read-only access.
/// Syntax: viewing &lt;expression&gt; as &lt;handle&gt; { &lt;body&gt; }
/// </summary>
/// <param name="Source">The expression to view (will be temporarily stolen)</param>
/// <param name="Handle">The variable name for the Viewed&lt;T&gt; handle</param>
/// <param name="Body">The block statement to execute with read-only access</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Viewing semantics:
/// <list type="bullet">
/// <item>Source becomes deadref during scope (temporarily stolen)</item>
/// <item>Handle provides read-only access (Viewed&lt;T&gt;)</item>
/// <item>Handle is copyable - can pass to multiple functions</item>
/// <item>Source is automatically restored when scope exits</item>
/// <item>Prevents aliasing: can't hijack the source while viewing</item>
/// </list>
/// </remarks>
public record ViewingStatement(
    Expression Source,
    string Handle,
    BlockStatement Body,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitViewingStatement(node: this);
    }
}

/// <summary>
/// Represents a hijacking block statement for scoped exclusive access (single-threaded).
/// Syntax: hijacking &lt;expression&gt; as &lt;handle&gt; { &lt;body&gt; }
/// </summary>
/// <param name="Source">The expression to hijack (will be temporarily stolen)</param>
/// <param name="Handle">The variable name for the Hijacked&lt;T&gt; handle</param>
/// <param name="Body">The block statement to execute with exclusive access</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Hijacking semantics:
/// <list type="bullet">
/// <item>Source becomes deadref during scope (temporarily stolen)</item>
/// <item>Handle provides exclusive read/write access (Hijacked&lt;T&gt;)</item>
/// <item>Handle is NOT copyable - unique access only</item>
/// <item>Source is automatically restored when scope exits</item>
/// <item>Prevents aliasing: can't view or hijack the source while hijacking</item>
/// </list>
/// </remarks>
public record HijackingStatement(
    Expression Source,
    string Handle,
    BlockStatement Body,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitHijackingStatement(node: this);
    }
}

/// <summary>
/// Represents a threadwitnessing block statement for thread-safe scoped read access.
/// Syntax: threadwitnessing &lt;expression&gt; as &lt;handle&gt; { &lt;body&gt; }
/// </summary>
/// <param name="Source">The ThreadShared expression to witness (will be temporarily stolen)</param>
/// <param name="Handle">The variable name for the thread-safe read handle (ThreadWitnessed&lt;T&gt;)</param>
/// <param name="Body">The block statement to execute with read access</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// ThreadWitnessing semantics (for ThreadShared with MultiReadLock policy):
/// <list type="bullet">
/// <item>Source becomes deadref during scope (temporarily stolen)</item>
/// <item>Handle acquires read lock on ThreadShared&lt;T&gt;, producing ThreadWitnessed&lt;T&gt;</item>
/// <item>Multiple threadwitnessing handles can coexist</item>
/// <item>Source is automatically restored when scope exits</item>
/// <item>Blocks threadseizing attempts until released</item>
/// </list>
/// </remarks>
public record ThreadWitnessingStatement(
    Expression Source,
    string Handle,
    BlockStatement Body,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitThreadWitnessingStatement(node: this);
    }
}

/// <summary>
/// Represents a threadseizing block statement for thread-safe scoped exclusive access.
/// Syntax: threadseizing &lt;expression&gt; as &lt;handle&gt; { &lt;body&gt; }
/// </summary>
/// <param name="Source">The ThreadShared expression to seize (will be temporarily stolen)</param>
/// <param name="Handle">The variable name for the thread-safe exclusive handle (ThreadSeized&lt;T&gt;)</param>
/// <param name="Body">The block statement to execute with exclusive access</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// ThreadSeizing semantics (for ThreadShared&lt;T&gt;):
/// <list type="bullet">
/// <item>Source becomes deadref during scope (temporarily stolen)</item>
/// <item>Handle acquires exclusive lock on ThreadShared&lt;T&gt;, producing ThreadSeized&lt;T&gt;</item>
/// <item>Blocks all other access (threadwitnessing and threadseizing) until released</item>
/// <item>Handle is NOT copyable - unique access only</item>
/// <item>Source is automatically restored when scope exits</item>
/// </list>
/// </remarks>
public record ThreadSeizingStatement(
    Expression Source,
    string Handle,
    BlockStatement Body,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitThreadSeizingStatement(node: this);
    }
}

#endregion
