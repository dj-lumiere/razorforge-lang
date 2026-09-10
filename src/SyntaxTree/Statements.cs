using Builder.Tokenizer;
using Builder.Instantiation;

namespace SyntaxTree;

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
/// <item>FreeRoutine returns and loop control</item>
/// </list>
/// </remarks>
public abstract record Statement(SourceLocation Location) : SyntaxTreeNode(Location: Location);

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
/// <item>FreeRoutine calls that modify state: print("Hello"), array.append(item)</item>
/// <item>memberRoutine invocations: object.doSomething()</item>
/// <item>Assignment operators: x += 5</item>
/// </list>
/// </remarks>
public record ExpressionStatement(Expression Expression, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
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
/// <item>Internal declarations inside blocks: var y = computeValue()</item>
/// <item>Nested function definitions inside other functions</item>
/// </list>
/// </remarks>
public record DeclarationStatement(Declaration Declaration, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDeclarationStatement(node: this);
    }
}

/// <summary>
/// Assignment statement that stores a value into a target location.
/// Represents the assignment operator (=) in both RazorForge and Suflae.
/// </summary>
/// <param name="Target">The left-hand side target to assign to (variable, property, array element)</param>
/// <param name="Value">The right-hand side expression whose value will be assigned</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports various assignment targets:
/// <list type="bullet">
/// <item>Variables: x = 42</item>
/// <item>Object properties: obj.memberVar = value</item>
/// <item>Array elements: arr[index] = item</item>
/// <item>Chained assignments: a.b.c = value</item>
/// </list>
/// </remarks>
public record AssignmentStatement(Expression Target, Expression Value, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>True for the compiler-synthesized assignment that initializes a Suflae module-level
    /// <c>global</c> at the top of <c>start()</c>. Signals the Roamed promotion pass to escape the
    /// global's handle to ESCAPED (armed-lock) mode right after init — a global is reachable from every
    /// task, so its access lock must engage for thread-safe concurrent mutation.</summary>
    public bool IsGlobalInit { get; init; }

    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitAssignmentStatement(node: this);
    }
}

/// <summary>
/// Record/entity destructuring statement: var (memberVar, memberVar2) = expression
/// Unpacks record/entity member variables into multiple variables based on member variable names.
/// Destructuring only works when ALL member variables of the type are public.
/// </summary>
/// <param name="Pattern">The destructuring pattern with member variable bindings</param>
/// <param name="Initializer">Expression that evaluates to a record/entity value</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>var (center, radius) = circle - member variable name matches binding name</item>
/// <item>var (center: c, radius: r) = circle - aliased destructuring</item>
/// <item>var ((x, y), radius) = circle - nested destructuring</item>
/// </list>
/// </remarks>
public record DestructuringStatement(
    DestructuringPattern Pattern,
    Expression Initializer,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDestructuringStatement(node: this);
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
/// <item>In procedures/routines: may omit return statement (implicit void return)</item>
/// <item>Multiple returns allowed; first encountered terminates execution</item>
/// </list>
/// </remarks>
public record ReturnStatement(Expression? Value, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitReturnStatement(node: this);
    }

    /// <summary>
    /// Compatibility property for tests that expect ReturnStatement.Expression
    /// </summary>
    public Expression? Expression => Value;
}

/// <summary>
/// Becomes statement that produces a block result value without exiting the function.
/// Used in multi-statement when/if branches to explicitly indicate the branch's result.
/// </summary>
/// <param name="Value">Expression whose value becomes the block result</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// The becomes statement solves the "stray value" problem in indentation-based blocks:
/// <code>
/// var num = when result
///   is Crashable e
///     show(f"Error: {e.crash_message()}")
///     becomes 0  # Explicit: this block produces 0
///   else value
///     show(f"Success: {value}")
///     becomes value
/// </code>
/// Rules:
/// <list type="bullet">
/// <item>Required when a multi-statement block needs to produce a value</item>
/// <item>Single-expression branches can use => syntax instead</item>
/// <item>CE if a block has statements followed by a trailing value without becomes</item>
/// </list>
/// </remarks>
public record BecomesStatement(Expression Value, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitBecomesStatement(node: this);
    }
}

/// <summary>
/// Statement that fails the current function with an error, returning it via Result[T].
/// Used in RazorForge and Suflae for recoverable errors that should be handled by the caller.
/// </summary>
/// <param name="Error">Expression that evaluates to a Crashable error type</param>
/// <param name="Location">Source location information</param>
/// <param name="IsFatal">When true this is a <c>pierce</c> (fatal, uncatchable); when false a recoverable <c>throw</c>.</param>
/// <remarks>
/// The throw statement is used for expected throwures that should be handled:
/// <code>
/// routine divide!(a: s32, b: s32) -> s32
///   if b == 0
///     throw DivisionError(message: "Division by zero")
///   return a // b
/// </code>
/// Builder generates safe variants: try_divide() -> s32?, check_divide() -> Result[s32]
/// </remarks>
public record ThrowStatement(Expression Error, SourceLocation Location, bool IsFatal = false)
    : Statement(Location: Location)
{
    /// <summary>
    /// When true this is a `pierce` (fatal, uncatchable crash): it does NOT make the routine failable,
    /// is NOT rewritten into a recoverable return in try_/check_/lookup_ variants, and pierces through
    /// every handler to abort. When false this is a recoverable `throw`. Codegen is identical (both
    /// lower to rf_crash) — the difference is purely in the error-handling/variant machinery.
    /// </summary>
    public bool IsFatal { get; init; } = IsFatal;

    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitThrowStatement(node: this);
    }
}

/// <summary>
/// Statement indicating that a value is not found/absent, triggering Lookup[T] generation.
/// Used when a search operation finds no matching value (distinct from an error condition).
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// The absent statement is used to indicate "not found" in search/lookup operations:
/// <code>
/// routine get_user!(id: u64) -> User
///   unless database.connected()
///     throw DatabaseError(message: "Not connected")
///   unless database.has(id)
///     absent  # Value not found
///   return database.get(id)
/// </code>
/// Builder generates: try_get_user() -> User?, lookup_get_user() -> Lookup[User]
/// Pattern matching: is Crashable e / is None / else user
/// </remarks>
public record AbsentStatement(SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitAbsentStatement(node: this);
    }
}

/// <summary>
/// Pass statement that acts as an empty placeholder in records, protocols, or other bodies.
/// Used when a body is syntactically required but no operations are needed.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// The pass statement serves as a no-op placeholder:
/// <list type="bullet">
/// <item>Empty record bodies: record Empty { pass }</item>
/// <item>Empty protocol bodies: protocol Marker { pass }</item>
/// <item>Placeholder for future implementation</item>
/// </list>
/// </remarks>
public record PassStatement(SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitPassStatement(node: this);
    }
}

/// <summary>
/// Statement that explicitly discards the result of an expression.
/// Used when a routine returns a value but the caller intentionally ignores it.
/// </summary>
/// <param name="Expression">The expression whose result is discarded</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// The discard statement prevents warnings about unused return values:
/// <code>
/// # Without discard - build warning: unused return value
/// process_data()
///
/// # With discard - explicitly ignore return value
/// discard process_data()
/// </code>
/// Benefits:
/// <list type="bullet">
/// <item>Explicit intent - Makes it clear the return value is intentionally ignored</item>
/// <item>Prevents bugs - Catches cases where return values are accidentally ignored</item>
/// <item>Clean code - No need for dummy variables like var _ = process_data()</item>
/// </list>
/// </remarks>
public record DiscardStatement(Expression Expression, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDiscardStatement(node: this);
    }
}

#endregion

#region Control Flow Statements

/// <summary>
/// Conditional statement that executes different code paths based on a boolean condition.
/// Represents if-then-else constructs in both RazorForge and Suflae.
/// </summary>
/// <param name="Condition">Boolean expression to evaluate for branching decision</param>
/// <param name="ThenStatement">Statement to execute when condition is true</param>
/// <param name="ElseStatement">Optional statement to execute when condition is false</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports both simple and complex branching:
/// <list type="bullet">
/// <item>Simple if: if (x > 0) show("positive")</item>
/// <item>If-else: if (x > 0) show("positive") else show("not positive")</item>
/// <item>Nested conditions: if (x > 0) if (x &lt; 10) show("single digit")</item>
/// <item>Block statements: if (condition) { multiple \n statements }</item>
/// </list>
/// </remarks>
public record IfStatement(
    Expression Condition,
    Statement ThenStatement,
    Statement? ElseStatement,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitIfStatement(node: this);
    }

    /// <summary>
    /// Compatibility properties for tests that expect ThenBranch and ElseBranch
    /// </summary>
    public Statement ThenBranch => ThenStatement;

    /// <summary>
    /// Compatibility property for tests that expect ElseBranch.
    /// </summary>
    public Statement? ElseBranch => ElseStatement;
}

/// <summary>
/// Loop statement that repeatedly executes a body while a condition remains true.
/// Represents while loops that check the condition before each iteration.
/// </summary>
/// <param name="Condition">Boolean expression evaluated before each iteration</param>
/// <param name="Body">Statement to execute repeatedly while condition is true</param>
/// <param name="Location">Source location information</param>
/// <param name="ElseBranch">Optional else branch that runs if the loop completes without breaking.</param>
/// <remarks>
/// Loop behavior characteristics:
/// <list type="bullet">
/// <item>Pre-condition check: body may not execute if condition is initially false</item>
/// <item>Supports break and continue statements for flow control</item>
/// <item>Infinite loops possible if condition never becomes false</item>
/// <item>Body can be single statement or block statement</item>
/// <item>Python-style else: else branch executes if loop completes without break</item>
/// </list>
/// </remarks>
public record WhileStatement(
    Expression Condition,
    Statement Body,
    Statement? ElseBranch,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitWhileStatement(node: this);
    }
}

/// <summary>
/// Infinite loop statement (the <c>loop</c> keyword). Executes the body unconditionally
/// until a <c>break</c> or <c>return</c> exits it.
/// This is the primitive loop form; <c>while</c> desugars into this.
/// </summary>
/// <param name="Body">Statement to execute on every iteration</param>
/// <param name="Location">Source location information</param>
public record LoopStatement(Statement Body, SourceLocation Location)
    : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitLoopStatement(node: this);
    }

    /// <summary>
    /// True when <see cref="Builder.Desugaring.Passes.ControlFlowLoweringPass"/> synthesized this
    /// loop as the body of a lowered <c>for x in coll</c> — i.e. its body is a
    /// <see cref="WhenStatement"/> over an <c>iter.try_emit()</c> call. Marks the loop so the
    /// <c>IteratorInlineLoweringPass</c> can find the iterator-advance loops to rewrite, instead of
    /// brittle shape-matching against every <c>loop</c>.
    /// </summary>
    public bool IsIteratorEachLoop { get; init; }

    /// <summary>
    /// For a lowered <c>each x in a</c> loop whose iteration source is a plain variable, the name of
    /// that variable (<c>"a"</c>); otherwise <c>null</c>. ControlFlowLoweringPass (Phase 7) rewrites
    /// the surface <see cref="EachStatement"/> away before Phase 5 body analysis, so this marker is
    /// how the reshaping-during-iteration check (SemanticVerifier) recovers the source name to
    /// populate its active-iteration set.
    /// </summary>
    public string? IterationSourceName { get; init; }
}

/// <summary>
/// Iterator loop statement that executes a body for each element in an iterable collection.
/// Represents for-in loops that automatically handle iteration over collections.
/// </summary>
/// <param name="Variable">Loop variable name that receives each element value</param>
/// <param name="Iterable">Expression that evaluates to an iterable collection (arrays, ranges, strings)</param>
/// <param name="Body">Statement to execute for each iteration with the loop variable bound</param>
/// <param name="Location">Source location information</param>
/// <param name="VariablePattern">Optional destructuring pattern used instead of a single loop variable.</param>
/// <param name="ElseBranch">Optional else branch that runs if the loop completes without breaking.</param>
/// <remarks>
/// Supports iteration over various collection types:
/// <list type="bullet">
/// <item>Arrays: for item in [1, 2, 3]</item>
/// <item>Ranges: for i in (0 to 10)</item>
/// <item>Strings: for char in "hello"</item>
/// <item>Custom iterables implementing iteration protocols</item>
/// <item>Tuple destructuring: for (index, item) in items.enumerate()</item>
/// <item>Python-style else: else branch executes if loop completes without break</item>
/// </list>
/// </remarks>
public record EachStatement(
    string? Variable,
    DestructuringPattern? VariablePattern,
    Expression Iterable,
    Statement Body,
    Statement? ElseBranch,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitEachStatement(node: this);
    }
}

/// <summary>The buildtime source an <see cref="ExpandStatement"/> iterates over.</summary>
public enum ExpandSourceKind
{
    /// <summary><c>openmemvarof(T)</c> — the OPEN ∪ POSTED (publicly readable) member variables,
    /// declaration order. Filters out <c>secret</c> fields. Use for public serialization.</summary>
    OpenMemberVariables,

    /// <summary><c>allmemvarof(T)</c> — ALL member variables incl <c>secret</c>, declaration order.
    /// Use for full-state internal derives (hash/cmp/represent/diagnose/destroy).</summary>
    AllMemberVariables,

    /// <summary><c>branchof(T)</c> — the arms of a variant, tag order. Used inside a <c>when</c> to
    /// generate one type-dispatch clause per arm.</summary>
    Arms,

    /// <summary><c>caseof(T)</c> — the cases of a choice (S32 discriminants) or the members of a flags
    /// (U64 bit values), declaration order. The handle exposes <c>c.name</c>, <c>c.id</c> (ordinal) and
    /// <c>c.value</c> (the case's numeric constant, spliced via <c>${c.value}</c>).</summary>
    Cases
}

/// <summary>
/// Compile-time member-expansion loop: <c>expand m in allmemvarof(T)</c>. Unlike the runtime
/// <see cref="EachStatement"/>, this never survives to codegen — it is UNROLLED at monomorphization
/// (once per member of the concrete <c>T</c>) by the generic AST rewriter, with the handle
/// projections (<c>m.name</c>, <c>m.id</c>) folded to literals and <c>x.${m.name}</c> splices
/// rewritten to real member accesses.
/// </summary>
/// <param name="HandleName">The per-part handle identifier (e.g. <c>m</c>).</param>
/// <param name="SourceName">The intrinsic name following <c>in</c> (e.g. <c>allmemvarof</c>, <c>openmemvarof</c>, <c>caseof</c>); classified to an <see cref="ExpandSourceKind"/> by the semantic verifier.</param>
/// <param name="SourceType">The type inside <c>allmemvarof(...)</c> (a generic param before monomorph).</param>
/// <param name="Body">The loop body, cloned per part at monomorphization.</param>
/// <param name="Location">Source location information.</param>
public record ExpandStatement(
    string HandleName,
    string SourceName,
    TypeExpression SourceType,
    Statement Body,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>
    /// The reflection kind, classified from the source intrinsic NAME (`allmemvarof`/`openmemvarof`/
    /// `caseof`). The parser is name-agnostic — it stores whatever identifier follows `in` as
    /// <see cref="SourceName"/> (the sources are BuilderExpansion module intrinsics, not keywords); this
    /// maps it to the kind the unroller branches on. SemanticVerifier validates the name + import-gates
    /// on <c>import BuilderExpansion</c> before the unroll runs.
    /// </summary>
    public ExpandSourceKind SourceKind => ExpandSources.KindOf(name: SourceName);

    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitExpandStatement(node: this);
    }
}

/// <summary>
/// Classifies a BuilderExpansion reflection-source intrinsic name (<c>allmemvarof</c>/<c>openmemvarof</c>/
/// <c>caseof</c>/<c>branchof</c>) into an <see cref="ExpandSourceKind"/>. Lives in the AST layer (not the
/// parser grammar, which is name-agnostic) so both <see cref="ExpandStatement"/> and the analyzer agree on
/// the mapping. Unknown names fall back to <see cref="ExpandSourceKind.AllMemberVariables"/>; the analyzer
/// rejects them first (a name that is not a real source is a semantic error, gated on the module import).
/// </summary>
public static class ExpandSources
{
    /// <summary>The recognized reflection-source intrinsic names.</summary>
    public static readonly IReadOnlySet<string> Names =
        new HashSet<string>(comparer: StringComparer.Ordinal)
        {
            "openmemvarof", "allmemvarof", "caseof", "branchof"
        };

    /// <summary>True if <paramref name="name"/> is a recognized reflection-source intrinsic.</summary>
    public static bool IsSource(string name)
    {
        return Names.Contains(item: name);
    }

    /// <summary>Maps a source intrinsic name to its <see cref="ExpandSourceKind"/>.</summary>
    public static ExpandSourceKind KindOf(string name)
    {
        return name switch
        {
            "openmemvarof" => ExpandSourceKind.OpenMemberVariables,
            "allmemvarof" => ExpandSourceKind.AllMemberVariables,
            "caseof" => ExpandSourceKind.Cases,
            "branchof" => ExpandSourceKind.Arms,
            _ => ExpandSourceKind.AllMemberVariables
        };
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
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
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
/// <param name="ArmExpansion">Buildtime arm-expansion template; when set, clauses are unrolled per variant arm at monomorphization. Null for ordinary <c>when</c>.</param>
/// <remarks>
/// Advanced pattern matching features:
/// <list type="bullet">
/// <item>Structural matching: when value { Point(x, y) => ... }</item>
/// <item>Guard conditions: when x { n if n > 0 => ... }</item>
/// <item>Exhaustiveness checking: builder ensures all cases are covered</item>
/// <item>Variable binding: patterns can extract and bind values</item>
/// </list>
/// </remarks>
public record WhenStatement(
    Expression Expression,
    List<WhenClause> Clauses,
    SourceLocation Location,
    // Buildtime arm-expansion: `when me` / `expand m in branchof(T)` / `is ${m.type} x => …`. When set,
    // the (initially empty) Clauses are UNROLLED from this template at monomorphization — one clause
    // per variant arm, with `${m.type}` folded to the arm type. Null for an ordinary `when`.
    WhenArmExpansion? ArmExpansion = null) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
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
/// A buildtime clause-template for <c>expand m in branchof(T)</c> inside a <c>when</c>. Carries the
/// handle name, the variant source type, and the single template clause (whose pattern is a
/// <see cref="SpliceTypePattern"/>). Monomorphization unrolls this into one concrete
/// <see cref="WhenClause"/> per arm.
/// </summary>
public record WhenArmExpansion(string HandleName, TypeExpression SourceType, WhenClause Template);

/// <summary>
/// Control flow statement that immediately exits the innermost loop.
/// Transfers control to the statement following the loop construct.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Break statement behavior:
/// <list type="bullet">
/// <item>Exits innermost loop only (while, for, when with iteration)</item>
/// <item>Build-time error if used outside loop context</item>
/// <item>Skips any remaining loop iterations</item>
/// <item>Continues execution after loop construct</item>
/// </list>
/// </remarks>
public record BreakStatement(SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
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
/// <item>Build-time error if used outside loop context</item>
/// <item>Does not exit the loop, only skips current iteration</item>
/// </list>
/// </remarks>
public record ContinueStatement(SourceLocation Location) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
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
/// Pattern that matches exact literal values like numbers, texts, or booleans.
/// Used for precise value comparison in pattern matching constructs.
/// </summary>
/// <param name="Value">The exact value to match against (42, "hello", true, etc.)</param>
/// <param name="LiteralType">Token type for typed literal suffixes (e.g., S32Literal for 1_s32)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples: when x { 42 => ..., "hello" => ..., true => ... }
/// </remarks>
public record LiteralPattern(object Value, TokenType LiteralType, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern that binds a matched value to a variable name for use in the action.
/// Creates a new variable in the action scope containing the matched value.
/// </summary>
/// <param name="Name">Variable name to bind the matched value to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Example: when someValue { x => show(x) } binds someValue to x.
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
/// <param name="Bindings">Optional list of bindings for destructuring (e.g., CIRCLE (x, y) or CIRCLE ((x, y), radius))</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>when obj { is Text => ... } matches any text</item>
/// <item>when obj { is Point p => ... } matches Point and binds to p</item>
/// <item>when obj { is Message.TEXT content => ... } matches variant case with binding</item>
/// <item>when obj { is CIRCLE (x, y) => ... } matches variant case with destructuring</item>
/// <item>when obj { is CIRCLE ((x, y), radius) => ... } matches with nested destructuring</item>
/// </list>
/// </remarks>
public record TypePattern(
    TypeExpression Type,
    string? VariableName,
    List<DestructuringBinding>? Bindings,
    SourceLocation Location) : Pattern(Location: Location);

/// <summary>
/// A buildtime type-splice pattern: <c>is ${m.type} x</c> inside an <c>expand m in branchof(T)</c>.
/// The concrete arm type is unknown until monomorphization, where this is replaced by a
/// <see cref="TypePattern"/> bound to the current arm's type. <see cref="VariableName"/> is null
/// for a payload-less arm (<c>is ${m.type} =></c>).
/// </summary>
public record SpliceTypePattern(string HandleName, string? VariableName, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern that matches when a value is NOT a specific type.
/// Used for negated type checking in when clauses: when value { isnot Text => ... }
/// </summary>
/// <param name="Type">The type to check against (negated -> matches if NOT this type)</param>
/// <param name="Location">Source location information</param>
public record NegatedTypePattern(TypeExpression Type, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern that matches flag combinations in when clauses.
/// Used for 'is' flags patterns (has flags).
/// Examples: is READ and WRITE => ..., is READ or WRITE => ...
/// </summary>
/// <param name="FlagNames">List of flag member names to test</param>
/// <param name="Connective">How the flags are combined: And (all required) or Or (any required)</param>
/// <param name="ExcludedFlags">Optional flags to exclude with 'but' (only valid with And connective)</param>
/// <param name="Location">Source location information</param>
public record FlagsPattern(
    List<string> FlagNames,
    FlagsTestConnective Connective,
    List<string>? ExcludedFlags,
    SourceLocation Location) : Pattern(Location: Location);

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

/// <summary>
/// Expression pattern for when statements with guard conditions.
/// Used in standalone when blocks: when { x > 10 => ..., _ => ... }
/// </summary>
/// <param name="Expression">The boolean expression that acts as a guard condition</param>
/// <param name="Location">Source location information</param>
public record ExpressionPattern(Expression Expression, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern for comparison-based matching in when clauses.
/// Compares the when subject against a value using the specified operator.
/// </summary>
/// <param name="Operator">The comparison operator (==, !=, &lt;, &gt;, &lt;=, &gt;=)</param>
/// <param name="Value">The expression to compare the when subject against</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>== 42 => ... (value equality for literals)</item>
/// <item>!= 0 => ... (value inequality)</item>
/// <item>&lt; 0 => ... (numeric comparison)</item>
/// <item>&gt;= 65 => ... (numeric comparison)</item>
/// <item>== true => ... (boolean matching)</item>
/// </list>
/// </remarks>
public record ComparisonPattern(TokenType Operator, Expression Value, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern for matching variant cases with optional destructuring.
/// Supports both qualified (Variant.CASE) and unqualified (CASE) syntax.
/// </summary>
/// <param name="VariantType">Optional type qualifier (e.g., "Message" in Message.TEXT)</param>
/// <param name="CaseName">The variant case name (e.g., "TEXT", "NUMBER")</param>
/// <param name="Bindings">Optional member variable bindings for destructuring the case data</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>is TEXT content => ... (unqualified, binds data to 'content')</item>
/// <item>is Message.TEXT content => ... (qualified, explicit type)</item>
/// <item>is CIRCLE (center, radius) => ... (destructuring)</item>
/// <item>is Shape.RECTANGLE (top_left: corner, size: s) => ... (aliased destructuring)</item>
/// </list>
/// </remarks>
public record VariantPattern(
    string? VariantType,
    string CaseName,
    List<DestructuringBinding>? Bindings,
    SourceLocation Location) : Pattern(Location: Location);

/// <summary>
/// Represents a single binding in destructuring patterns.
/// Supports both direct member-variable-name binding and aliased binding.
/// </summary>
/// <param name="MemberVariableName">The member variable name to extract (null for positional binding)</param>
/// <param name="BindingName">The variable name to bind the value to</param>
/// <param name="NestedPattern">Optional nested pattern for deep destructuring</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>(center, radius) - member variable name matches binding name</item>
/// <item>(center: c, radius: r) - explicit alias</item>
/// <item>((x, y), radius) - nested destructuring</item>
/// </list>
/// </remarks>
public record DestructuringBinding(
    string? MemberVariableName,
    string? BindingName,
    Pattern? NestedPattern,
    SourceLocation Location);

/// <summary>
/// Pattern that adds a guard condition to another pattern.
/// Matches when both the inner pattern matches and the guard evaluates to true.
/// </summary>
/// <param name="InnerPattern">The pattern to match first</param>
/// <param name="Guard">Boolean expression that must be true for the pattern to match</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>is s32 n where n > 0 => ... (type with guard)</item>
/// <item>is NetworkError e where e.code == 404 => ... (error type with guard)</item>
/// </list>
/// </remarks>
public record GuardPattern(Pattern InnerPattern, Expression Guard, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern for matching the None/absent value in Maybe, Result, or Lookup types.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>is None => ... (matches Maybe.None or Lookup.None)</item>
/// </list>
/// </remarks>
public record NonePattern(SourceLocation Location) : Pattern(Location: Location);

/// <summary>
/// Pattern for matching Crashable errors in Result or Lookup types.
/// Optionally binds the error to a variable for inspection.
/// </summary>
/// <param name="ErrorType">Optional specific error type to match (null matches any Crashable)</param>
/// <param name="VariableName">Optional variable name to bind the error to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>is Crashable e => ... (matches any error)</item>
/// <item>is FileNotFoundError e => ... (matches specific error type)</item>
/// <item>is Crashable => ... (matches error without binding)</item>
/// </list>
/// </remarks>
public record CrashablePattern(
    TypeExpression? ErrorType,
    string? VariableName,
    SourceLocation Location) : Pattern(Location: Location);

/// <summary>
/// Else pattern that matches any remaining value and optionally binds it.
/// Must be the last pattern in a when statement.
/// </summary>
/// <param name="VariableName">Optional variable name to bind the matched value to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>else => ... (catch-all without binding)</item>
/// <item>else user => ... (catch-all with binding)</item>
/// </list>
/// </remarks>
public record ElsePattern(string? VariableName, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern for destructuring records and similar types.
/// Used in var bindings and pattern matching for extracting member variables.
/// </summary>
/// <param name="Bindings">List of member variable bindings to extract</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>var (center, radius) = circle</item>
/// <item>var (x, y) = point</item>
/// <item>var ((x, y), radius) = circle (nested)</item>
/// </list>
/// </remarks>
public record DestructuringPattern(List<DestructuringBinding> Bindings, SourceLocation Location)
    : Pattern(Location: Location);

/// <summary>
/// Pattern for type checking with simultaneous destructuring.
/// Used in is expressions like: value is Point (x, y)
/// </summary>
/// <param name="Type">The type to check and destructure</param>
/// <param name="Bindings">List of member variable bindings to extract from the matched value</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>value is Point (x, y) - positional, member variable names match binding names</item>
/// <item>value is Point (x: a, y: b) - named, member variable 'x' binds to 'a', 'y' binds to 'b'</item>
/// <item>value is Line ((x: x1, y: y1), (x: x2, y: y2)) - nested destructuring</item>
/// <item>value is Rectangle (topLeft, _) - partial destructuring with wildcard</item>
/// </list>
/// Destructuring is only allowed when ALL member variables of the type are public.
/// </remarks>
public record TypeDestructuringPattern(
    TypeExpression Type,
    List<DestructuringBinding> Bindings,
    SourceLocation Location) : Pattern(Location: Location);

#endregion

#region Unsafe Statements

/// <summary>
/// Statement representing a danger block that disables safety checks.
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
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDangerStatement(node: this);
    }
}

#endregion

/// <summary>
/// Identifies which control-flow site was transformed into a <see cref="VariantReturnStatement"/>.
/// </summary>
public enum VariantSiteKind
{
    /// <summary>Transformed from a <see cref="ThrowStatement"/>.</summary>
    FromThrow,

    /// <summary>Transformed from an <see cref="AbsentStatement"/>.</summary>
    FromAbsent,

    /// <summary>Transformed from a <see cref="ReturnStatement"/>.</summary>
    FromReturn,

    /// <summary>
    /// <c>Value</c> is already a variant carrier of the right type (rewritten by
    /// <see cref="Builder.Instantiation.ErrorHandlingVariantPass"/> when the original wrapper body
    /// tail-returned a call to a failable routine, e.g. <c>return F!(x)</c> -> <c>return try_F(x)</c>).
    /// Codegen returns <c>Value</c> directly without wrapping it in Some/Ok/Found.
    /// </summary>
    FromVariantPassthrough
}

/// <summary>
/// Synthesized return statement inserted into error-handling variant bodies by
/// <see cref="ErrorHandlingVariantPass"/>.
/// Replaces <see cref="ThrowStatement"/>, <see cref="AbsentStatement"/>, and
/// <see cref="ReturnStatement"/> so that codegen can emit carrier construction via
/// <c>EmitVariantReturn</c> without relying on mutable flags.
/// </summary>
/// <param name="VariantKind">Which variant body this belongs to (Try, TryBool, Check, Lookup).</param>
/// <param name="SiteKind">Which original statement was replaced.</param>
/// <param name="Value">Original expression (error for FromThrow, value for FromReturn; null for FromAbsent).</param>
/// <param name="Location">Source location for diagnostics.</param>
public record VariantReturnStatement(
    ErrorHandlingVariantKind VariantKind,
    VariantSiteKind SiteKind,
    Expression? Value,
    SourceLocation Location) : Statement(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitVariantReturnStatement(node: this);
    }
}

/// <summary>
/// Represents a scoped resource management block (using expr as name).
/// Resource lifetime is tied to the indented body; cleanup occurs at block exit.
/// </summary>
/// <remarks>
/// <para>Syntax: <c>using open("file.txt") as file</c></para>
/// <para>The resource expression must return an object that implements a disposable/closeable protocol.</para>
/// <para>Example:</para>
/// <code>
/// using open("file.txt") as file
///   var content = file.read_all()
///   process(content)
///   # file is automatically closed at block exit
/// </code>
/// </remarks>
public record UsingStatement(
    Expression Resource,
    string Name,
    Statement Body,
    SourceLocation Location,
    Statement? FallbackBody = null) : Statement(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitUsingStatement(node: this);
    }
}
