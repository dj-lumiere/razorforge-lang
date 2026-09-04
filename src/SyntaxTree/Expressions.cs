using System.Collections.Generic;
using Compiler.Tokenizer;
using TypeModel.Reprs;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace SyntaxTree;

#region Base Expression Types

/// <summary>
/// Base entity for all expression nodes in the AST.
/// Expressions are constructs that evaluate to produce values and can be used as operands.
/// They represent computations, value retrievals, and data transformations.
/// </summary>
/// <param name="Location">Source location information for error reporting and debugging</param>
/// <remarks>
/// Expressions form the computational foundation of the languages:
/// <list type="bullet">
/// <item>Literal values: numbers, strings, booleans</item>
/// <item>Variable access and function calls</item>
/// <item>Arithmetic and logical operations</item>
/// <item>Complex operations: member access, indexing, type conversions</item>
/// <item>Memory operations: slice creators, memory slice memberRoutine calls</item>
/// <item>Danger zone operations: raw memory access, type punning</item>
/// </list>
/// After semantic analysis, ResolvedType contains the computed type of this expression.
/// </remarks>
public abstract record Expression(SourceLocation Location) : SyntaxTreeNode(Location: Location)
{
    /// <summary>
    /// The resolved type of this expression, set by the semantic analyzer.
    /// This is null before semantic analysis and populated during type checking.
    /// Code generators should use this instead of re-inferring types.
    /// </summary>
    public TypeInfo? ResolvedType { get; set; }

    /// <summary>
    /// For a collection literal (`[..]`/`{..}`) whose resolved type `obeys ListLiteral/SetLiteral/
    /// DictLiteral`, the monomorphized `Type.from_literal[K]` static builder the literal lowers to.
    /// SA resolves it; reachability seeds it; ExpressionLoweringPass emits the call. Null for the inline
    /// `Array`/`BitArray` literals (pure insertvalue) and non-literal expressions.
    /// </summary>
    public TypeModel.Symbols.RoutineInfo? ResolvedLiteralBuilder { get; set; }

    /// <summary>
    /// The finalized backend representation for this expression after postprocessing.
    /// This separates semantic type identity from ABI/storage identity.
    /// </summary>
    public BackendRepr? ResolvedRepr { get; set; }

    /// <summary>
    /// True if this expression produces an in-flight entity (`T`) — a freshly produced
    /// entity value that has not yet been bound to a name. Set by SA on:
    ///   - call expressions whose callee's <c>RoutineInfo.IsInFlightReturn</c> is true,
    ///   - `steal x` expressions (consume binding → produce in-flight),
    ///   - entity creator expressions (the new value is in-flight until bound),
    ///   - collection literals,
    ///   - expressions whose inner expression is in-flight and structurally forwards it.
    /// Drives auto-bind at binding sites and distinct diagnostic formatting
    /// (`T` vs `T` render differently — `?Counter(value: 10)` vs `Counter(value: 10)`).
    /// Kept independent of <see cref="ResolvedType"/>, which carries only the bare entity
    /// type — in-flight-ness is a per-expression production attribute, not a type identity.
    /// </summary>
    public bool IsInFlight { get; set; }

    /// <summary>
    /// When set, this read was flow-narrowed from a carrier/variant (<c>NarrowedFrom</c>) to a single
    /// concrete arm/payload type (<see cref="ResolvedType"/>) — e.g. `x` used as `S64` inside the else
    /// of `if x is None`, or as arm `B` inside the else of `if x is A` on a two-arm variant. SA sets
    /// this on the narrowed <see cref="IdentifierExpression"/>; a postprocessing pass rewrites such
    /// reads into a payload extraction (<see cref="CarrierPayloadExpression"/>). Null for ordinary
    /// (non-narrowed) reads.
    /// </summary>
    public TypeInfo? NarrowedFrom { get; set; }
}

#endregion

#region Literal and Identifier Expressions

/// <summary>
/// Expression representing constant literal values embedded in source code.
/// Includes all primitive types: integers, floats, strings, characters, and booleans.
/// </summary>
/// <param name="Value">The raw literal value parsed from source.</param>
/// <param name="LiteralType">Token type indicating the specific literal format used</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports the full range of RazorForge/Suflae literal types:
/// <list type="bullet">
/// <item>Integers: S8, S16, S32, S64, S128, U8, U16, U32, U64, U128, Address</item>
/// <item>Floats: F16, F32, F64, F128</item>
/// <item>Decimals: D32, D64, D128</item>
/// <item>Text and characters: Character, Byte, Text, Bytes</item>
/// <item>Booleans: true, false</item>
/// <item>Duration: w, d, h, m, s, ms, us, ns</item>
/// <item>ByteSize: b, kb, kib, mb, mib, gb, gib</item>
/// </list>
/// </remarks>
public record LiteralExpression(object Value, TokenType LiteralType, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitLiteralExpression(node: this);
    }
}

/// <summary>
/// Abstract base for parts of an inserted text expression (f-string).
/// </summary>
public abstract record InsertedTextPart(SourceLocation Location);

/// <summary>
/// A literal text segment within an f-string.
/// </summary>
public record TextPart(string Text, SourceLocation Location)
    : InsertedTextPart(Location: Location);

/// <summary>
/// An embedded expression within an f-string, with optional format specifier.
/// </summary>
public record ExpressionPart(Expression Expression, string? FormatSpec, SourceLocation Location)
    : InsertedTextPart(Location: Location);

/// <summary>
/// Expression representing an f-string with text insertion: f"Hello, {name}!"
/// Contains a list of text and expression parts.
/// </summary>
public record InsertedTextExpression(
    List<InsertedTextPart> Parts,
    bool IsRaw,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitInsertedTextExpression(node: this);
    }
}

/// <summary>
/// Expression representing a list literal: [1, 2, 3]
/// Creates a List containing the specified elements.
/// </summary>
/// <param name="Elements">The expressions for each list element</param>
/// <param name="ElementType">Optional explicit element type annotation</param>
/// <param name="Location">Source location information</param>
public record ListLiteralExpression(
    List<Expression> Elements,
    TypeExpression? ElementType,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitListLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a set literal: {1, 2, 3}
/// Creates a Set containing the specified unique elements.
/// </summary>
/// <param name="Elements">The expressions for each set element</param>
/// <param name="ElementType">Optional explicit element type annotation</param>
/// <param name="Location">Source location information</param>
public record SetLiteralExpression(
    List<Expression> Elements,
    TypeExpression? ElementType,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitSetLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a dictionary literal: {key1: value1, key2: value2}
/// Creates a Dict with the specified key-value pairs.
/// </summary>
/// <param name="Pairs">The key-value expression pairs</param>
/// <param name="KeyType">Optional explicit key type annotation</param>
/// <param name="ValueType">Optional explicit value type annotation</param>
/// <param name="Location">Source location information</param>
public record DictLiteralExpression(
    List<(Expression Key, Expression Value)> Pairs,
    TypeExpression? KeyType,
    TypeExpression? ValueType,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDictLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a tuple literal: (1, 2, 3) or (x,) for single-element.
/// Always creates a Tuple (inline LLVM struct). Entities stored as ptr fields.
/// </summary>
/// <param name="Elements">The expressions for each tuple element (must have at least 1 element)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Tuple literal syntax:
/// <list type="bullet">
/// <item>Multi-element: (1, 2, 3) - creates tuple with 3 elements</item>
/// <item>Single-element: (42,) - trailing comma required to distinguish from parenthesized expression</item>
/// <item>Nested: (1, (2, 3)) - tuples can contain other tuples</item>
/// </list>
/// Note: Empty tuples () are not valid. Use None for unit type.
/// Access elements via .item0, .item1, etc.
/// </remarks>
public record TupleLiteralExpression(List<Expression> Elements, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitTupleLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression that references a named symbol (variable, function, entity, etc.).
/// Represents the use of identifiers that must be resolved during semantic analysis.
/// </summary>
/// <param name="Name">The identifier name to look up in the symbol table</param>
/// <param name="Location">Source location information</param>
/// <param name="Realm">Optional cross-language realm qualifier (e.g. <c>"RF"</c>) from a <c>Realm::Name</c> prefix.</param>
/// <remarks>
/// Identifier resolution follows lexical scoping rules:
/// <list type="bullet">
/// <item>Variables: local scope, then enclosing scopes, then global</item>
/// <item>Functions: may be resolved with overloading based on call context</item>
/// <item>Types: resolved in type scope with visibility rules</item>
/// <item>Qualified names: may represent member access chains</item>
/// </list>
/// </remarks>
public record IdentifierExpression(string Name, SourceLocation Location, string? Realm = null)
    : Expression(Location: Location)
{
    /// <summary>``
    /// When set, this identifier resolved as a flag member in flag-context (e.g. a bare
    /// `READ` in a flags test). The bit position lets ExpressionLoweringPass emit
    /// the bitmask literal without re-doing the lookup.
    /// </summary>
    public int? ResolvedFlagsBit { get; set; }

    /// <summary>
    /// When set, this identifier is a routine-VALUE reference whose routine is already resolved —
    /// codegen materializes it as a closure directly, skipping name-based lookup. Used for
    /// references a lowering pass constructs (e.g. an unbound member-routine reference for a cycle-
    /// collector trace/free hook), where the bare name cannot be resolved by lookup because it needs
    /// the owner type. <see cref="Expression.ResolvedType"/> must be the matching <c>RoutineTypeInfo</c>.
    /// </summary>
    public RoutineInfo? ResolvedRoutine { get; set; }

    /// <summary>
    /// Set by semantic analysis when this identifier resolved to a Suflae module-level <c>global</c>
    /// (its <see cref="TypeModel.Symbols.VariableInfo.IsGlobal"/> is true). Because <c>LookupVariable</c>
    /// checks local scopes BEFORE the global table, a local that shadows a global resolves to the local
    /// and this flag stays false — so the flag is shadowing-exact. Consumed by
    /// <c>GlobalEntityRewritePass</c>, which rewrites every flagged reference to a field access on the
    /// hidden per-program <c>__ModuleGlobals</c> singleton (the thread-safe storage), so no bare global
    /// identifier survives into codegen.
    /// </summary>
    public bool IsModuleGlobal { get; set; }

    /// <summary>
    /// Set by semantic analysis to the <see cref="VariableInfo"/> this identifier bound to (a local,
    /// parameter, each-loop/using/pattern binding, global, or preset), or null when it resolved to
    /// something else (type, choice case, routine) or did not resolve. Because lookup returns the ONE
    /// stored instance per binding, two references to the same binding carry the SAME object — so
    /// reference-identity gives scope-precise "same variable" grouping for the language server's
    /// references / rename / go-to-definition. Not consumed by codegen; purely an IDE aid.
    /// </summary>
    public VariableInfo? ResolvedVariable { get; set; }

    /// <summary>
    /// Set by <see cref="Compiler.Postprocessing.Passes.TemporaryTeardownPass"/> on the synthetic temp
    /// identifier it introduces as the tail of a lowered managed-leaf reassignment
    /// (<c>target = __rv</c>). It marks, STRUCTURALLY (not by parsing the <c>__rv_</c> name), that this
    /// reassignment is already the pass's OWN output — so a second run over the same body (the
    /// warm-restore re-lowering) recognizes it and skips re-injecting the teardown, which would emit a
    /// second <c>target.destroy()</c> and double-free the heap buffer.
    /// </summary>
    public bool IsSynthesizedTeardownTemp { get; set; }

    /// <summary>
    /// Set by semantic analysis when this reference reads a variable that is DEAD at this point — its
    /// ownership was moved out by an earlier <c>steal</c> (or send) and it has not been re-bound since.
    /// Mirrors the analyzer's flow-sensitive deadref set (with the same if/else merge + rebind revival),
    /// captured PER OCCURRENCE so the language server can grey out / strike the dead uses. The consuming
    /// use inside the <c>steal</c> itself stays false (it is still valid — it is what does the moving).
    /// </summary>
    public bool IsDeadUse { get; set; }

    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitIdentifierExpression(node: this);
    }
}

#endregion

#region Operator Expressions

/// <summary>
/// Expression representing a compound assignment operation (e.g., a += b).
/// The semantic analyzer dispatches this to an in-place wired memberRoutine (iadd, etc.)
/// or falls back to create-and-assign (a = a.add(b)) when no in-place operator exists.
/// </summary>
/// <param name="Target">The assignment target (must be a modifiable variable, member variable, or index)</param>
/// <param name="Operator">The base binary operator (Add, Subtract, etc. — not Assign)</param>
/// <param name="Location">Source location information</param>
/// <param name="Value">The right-hand side value expression</param>
public sealed record CompoundAssignmentExpression(
    Expression Target,
    BinaryOperator Operator,
    Expression Value,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitCompoundAssignmentExpression(node: this);
    }
}

/// <summary>
/// Expression that combines two operands with a binary operator.
/// Supports arithmetic, comparison, logical, and bitwise operations.
/// </summary>
/// <param name="Left">Left-hand side operand expression</param>
/// <param name="Operator">Binary operator defining the operation to perform</param>
/// <param name="Right">Right-hand side operand expression</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports extensive operator categories:
/// <list type="bullet">
/// <item>Arithmetic: +, -, *, /, //, %, ** (with overflow variants)</item>
/// <item>Comparison: ==, !=, &lt;, &lt;=, &gt;, &gt;=</item>
/// <item>Logical: and, or</item>
/// <item>Membership: in, notin, is, isnot, obeys, disobeys</item>
/// <item>Bitwise: &amp;, |, ^, &lt;&lt;, &lt;&lt;?, &gt;&gt;, &lt;&lt;&lt;, &gt;&gt;&gt;</item>
/// </list>
/// </remarks>
public record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitBinaryExpression(node: this);
    }
}

/// <summary>
/// Expression that applies a unary operator to a single operand.
/// Supports arithmetic negation, logical negation, and bitwise operations.
/// </summary>
/// <param name="Operator">Unary operator defining the operation to perform</param>
/// <param name="Operand">Expression to apply the operator to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supported unary operators:
/// <list type="bullet">
/// <item>Arithmetic: -x (negation)</item>
/// <item>Logical: not (logical NOT)</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// </list>
/// </remarks>
public record UnaryExpression(UnaryOperator Operator, Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitUnaryExpression(node: this);
    }
}

/// <summary>
/// Expression that invokes a function or memberRoutine with zero or more arguments.
/// Represents function calls, memberRoutine invocations, and creator calls.
/// </summary>
/// <param name="Callee">Expression that evaluates to a callable (function, memberRoutine, lambda)</param>
/// <param name="Arguments">List of argument expressions to pass to the callable</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports various call patterns:
/// <list type="bullet">
/// <item>Function calls: routine(a, b, c)</item>
/// <item>memberRoutine calls: me.MemberRoutine(x, y)</item>
/// <item>Creator calls: Point(x, y)</item>
/// <item>Lambda calls: ((x) => x + 1)(42)</item>
/// <item>Operator memberRoutine calls: me.add(you)</item>
/// </list>
/// </remarks>
public record CallExpression(
    Expression Callee,
    List<Expression> Arguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitCallExpression(node: this);
    }

    /// <summary>
    /// Compatibility property for tests that expect CallExpression.Name
    /// Returns the name if Callee is an IdentifierExpression
    /// </summary>
    public string? Name => Callee is IdentifierExpression id
        ? id.Name
        : null;

    /// <summary>
    /// The resolved routine for this call, set by the semantic analyzer during overload resolution.
    /// When set, the codegen should use this instead of performing its own lookup.
    /// </summary>
    public RoutineInfo? ResolvedRoutine { get; set; }

    /// <summary>
    /// True when the call site used the failable `!` marker (e.g. <c>foo!(args)</c>).
    /// The Callee's name stays BARE — this structured flag records the `!` instead of baking
    /// it into the identifier string.
    /// </summary>
    public bool IsFailable { get; init; }

    /// <summary>
    /// Semantic-owned lowering classification for this call.
    /// Later phases should prefer this over re-deriving call meaning from AST shape.
    /// </summary>
    public CallLoweringKind LoweringKind { get; set; }

    /// <summary>
    /// The target type being constructed or converted to, when this call-like node represents
    /// construction rather than a plain routine/memberRoutine invocation.
    /// </summary>
    public TypeInfo? ConstructedType { get; set; }

    /// <summary>
    /// When true, this call is a collection literal constructor (e.g., List(1, 2, 3), Set(1, 2, 3)).
    /// Codegen should emit create() + repeated add/add_last calls instead of a normal function call.
    /// </summary>
    public bool IsCollectionLiteral { get; set; }

    /// <summary>
    /// memberRoutine-level type arguments, set by <c>GenericCallLoweringPass</c> when lowering a
    /// <c>GenericMemberRoutineCallExpression</c> that has explicit type parameters at the call site
    /// (e.g., <c>buf.read![U8](offset)</c> -> <c>TypeArguments = [U8]</c>).
    /// Null for calls with no memberRoutine-level type args.
    /// </summary>
    public List<TypeExpression>? TypeArguments { get; set; }

    /// <summary>
    /// True when this call was synthesized by a compiler lowering pass (e.g.
    /// <c>ControlFlowLoweringPass</c> emitting <c>iter.iter()</c> / <c>iter.try_emit()</c>
    /// for a for-loop). SA uses this to skip checks meant to gate user code from invoking
    /// dunder-private memberRoutines directly.
    /// </summary>
    public bool IsSynthesizedLowering { get; set; }
}

/// <summary>
/// Named argument expression used in function calls.
/// Allows specifying argument name explicitly: func(name: value)
/// </summary>
/// <param name="Name">The parameter name</param>
/// <param name="Value">The argument value expression.</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Named arguments support:
/// <list type="bullet">
/// <item>Explicit parameter binding: Error(message: "failed", code: 500)</item>
/// <item>Out-of-order arguments: Point(y: 10, x: 5)</item>
/// <item>Clarity for boolean/numeric arguments: set_visible(visible: true)</item>
/// <item>Optional parameter selection: connect(host: "localhost", port: 8080)</item>
/// </list>
/// </remarks>
public record NamedArgumentExpression(string Name, Expression Value, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitNamedArgumentExpression(node: this);
    }
}

/// <summary>
/// Expression representing a dict entry literal (key:value pair) in argument context.
/// Used in collection literal constructors: Dict(1:10, 2:20), SortedDict("a":"b")
/// </summary>
/// <param name="Key">The key expression</param>
/// <param name="Location">Source location information</param>
/// <param name="Value">The value expression</param>
public record DictEntryLiteralExpression(Expression Key, Expression Value, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDictEntryLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression representing a creator call with named member variable initializers.
/// Creates instances using parenthesis syntax: TypeName(field1: value1, field2: value2)
/// </summary>
/// <param name="TypeName">The name of the type being created</param>
/// <param name="TypeArguments">Optional generic type arguments (e.g., List&lt;T&gt;)</param>
/// <param name="MemberVariables">List of member variable name-value pairs for initialization</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Creator expression patterns:
/// <list type="bullet">
/// <item>Simple: Point(x: 10, y: 20)</item>
/// <item>Generic: TextIterator&lt;T&gt;(text: me, index: 0)</item>
/// <item>Nested: Node(value: 5, next: Node(value: 10, next: None))</item>
/// </list>
/// </remarks>
public record CreatorExpression(
    string TypeName,
    List<TypeExpression>? TypeArguments,
    List<(string Name, Expression Value)> MemberVariables,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitCreatorExpression(node: this);
    }

    /// <summary>
    /// Semantic-owned lowering classification for this creator.
    /// </summary>
    public CallLoweringKind LoweringKind { get; set; }

    /// <summary>
    /// The fully resolved type constructed by this creator, when known.
    /// </summary>
    public TypeInfo? ConstructedType { get; set; }

    /// <summary>
    /// When the creator's named arguments match a `create(named:)` overload (rather than
    /// field names), SA resolves and stores that creator routine here. Codegen then dispatches
    /// through the routine instead of doing inline field initialization.
    /// </summary>
    public RoutineInfo? ResolvedCreatorRoutine { get; set; }
}

/// <summary>
/// Expression for functional update - creating a modified copy of a value with specified changes.
/// Represents the 'with' keyword for unmodifiable updates.
/// </summary>
/// <param name="Base">The base expression to copy and modify</param>
/// <param name="Updates">List of member variable/index updates (name/index, value)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// With expression patterns:
/// <list type="bullet">
/// <item>Member variable update: record with .memberVar = newValue</item>
/// <item>Multiple member variables: record with .x = 1, .y = 2</item>
/// <item>Nested member variable: record with .address.city = "NYC"</item>
/// <item>Index update: collection with [i] = newValue</item>
/// </list>
/// For member variable updates, MemberVariablePath is set and Index is null.
/// For index updates, Index is set and MemberVariablePath is null.
/// </remarks>
public record WithExpression(
    Expression Base,
    List<(List<string>? MemberVariablePath, Expression? Index, Expression Value)> Updates,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitWithExpression(node: this);
    }
}

/// <summary>
/// Expression that accesses a member (member variable or member routine) of an object.
/// Represents the dot notation for accessing object members.
/// </summary>
/// <param name="Object">Expression that evaluates to the object containing the member</param>
/// <param name="MemberName">Bare name of the member to access — never carries a trailing
/// <c>!</c>; the failable marker is tracked structurally in <see cref="IsFailable"/>.</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Member access patterns:
/// <list type="bullet">
/// <item>Member-variable access: obj.memberVar</item>
/// <item>Member-routine reference: obj.routine (not a call)</item>
/// <item>Chained access: obj.child.grandchild</item>
/// </list>
/// </remarks>
public record MemberExpression(Expression Object, string MemberName, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>
    /// True when the source wrote the failable marker (<c>obj.routine!(...)</c>). The <c>!</c> is
    /// consumed by the parser as a separate token and recorded here — it is NEVER part of
    /// <see cref="MemberName"/>. Consumers that need failable overload resolution read this flag
    /// instead of scanning the name for a trailing <c>!</c>.
    /// </summary>
    public bool IsFailable { get; init; }

    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitMemberExpression(node: this);
    }
}

/// <summary>The syntactic position a <c>${...}</c> splice appears in, fixing the kind its inner
/// expression must fold to at monomorphization.</summary>
public enum SpliceKind
{
    /// <summary>Member-selector position (<c>x.${...}</c>): the inner must fold to a field NAME (Text).</summary>
    Selector,

    /// <summary>Expression position (<c>${...}</c>): a general comptime value splice.</summary>
    Value
}

/// <summary>
/// A comptime splice <c>${expr}</c> in expression position. The inner expression is a projection
/// of an <see cref="ExpandStatement"/> handle (e.g. <c>m.name</c>). Never survives monomorphization.
/// </summary>
/// <param name="Inner">The spliced projection expression.</param>
/// <param name="RequiredKind">The kind the position demands (Selector → Text name).</param>
/// <param name="Location">Source location information.</param>
public record SpliceExpression(Expression Inner, SpliceKind RequiredKind, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitSpliceExpression(node: this);
    }
}

/// <summary>
/// A member access whose selector is a comptime splice: <c>x.${m.name}</c>. Kept structurally
/// distinct from a plain <see cref="MemberExpression"/> so the monomorphizer knows to fold the
/// splice to a concrete field name and rewrite this to a real member access — a plain
/// <see cref="MemberExpression"/> is never touched by the expander.
/// </summary>
/// <param name="Object">The receiver whose field is selected.</param>
/// <param name="Selector">The <c>${...}</c> splice that folds to the field name.</param>
/// <param name="Location">Source location information.</param>
public record SpliceMemberExpression(Expression Object, SpliceExpression Selector, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitSpliceMemberExpression(node: this);
    }
}

/// <summary>
/// Expression that conditionally accesses a member of an object if the object is not none.
/// Represents the ?. operator for safe navigation / optional chaining.
/// </summary>
/// <param name="Object">Expression that may evaluate to none</param>
/// <param name="MemberName">Name of the property/member variable to access if object is not none</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples: obj?.memberVar, result?.value
/// If the object is none, the entire expression evaluates to none.
/// </remarks>
public record OptionalMemberExpression(
    Expression Object,
    string MemberName,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitOptionalMemberExpression(node: this);
    }
}

/// <summary>
/// Expression that accesses an element of a collection using an index or key.
/// Represents bracket notation for array indexing and dictionary access.
/// </summary>
/// <param name="Object">Expression that evaluates to an indexable collection</param>
/// <param name="Index">Expression that evaluates to the index or key</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Index access patterns:
/// <list type="bullet">
/// <item>Array indexing: arr[0], arr[i]</item>
/// <item>Dictionary access: dict["key"]</item>
/// <item>String indexing: str[pos]</item>
/// <item>Multi-dimensional: matrix[row][col]</item>
/// </list>
/// </remarks>
public record IndexExpression(Expression Object, Expression Index, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitIndexExpression(node: this);
    }

    /// <summary>
    /// When this IndexExpression is the target of an assignment, set by OperatorLoweringPass
    /// to the memberRoutine-generic-resolved <c>setitem</c>/<c>setitem!</c> routine. Codegen uses
    /// this in place of a fresh registry lookup so memberRoutine-level generics (e.g.
    /// <c>BitList.setitem![I]</c> -> <c>BitList.setitem![S64]</c>) dispatch to the
    /// monomorphized entry.
    /// </summary>
    public RoutineInfo? ResolvedSetItem { get; set; }
}

/// <summary>
/// Expression that selects between two values based on a boolean condition.
/// Represents the ternary conditional operator (if condition then true_value else false_value).
/// </summary>
/// <param name="Condition">Boolean expression to evaluate for selection</param>
/// <param name="TrueExpression">Expression to evaluate and return if condition is true</param>
/// <param name="FalseExpression">Expression to evaluate and return if condition is false</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Conditional expression behavior:
/// <list type="bullet">
/// <item>Short-circuit evaluation: only one branch is evaluated</item>
/// <item>Type compatibility: both branches must have compatible types</item>
/// <item>Nested conditionals: can be chained for complex logic</item>
/// <item>Expression context: can be used anywhere a value is expected</item>
/// </list>
/// </remarks>
public record ConditionalExpression(
    Expression Condition,
    Expression TrueExpression,
    Expression FalseExpression,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitConditionalExpression(node: this);
    }
}

/// <summary>
/// Expression representing a block that evaluates to a value.
/// The block consists of statements followed by a final expression that becomes the block's value.
/// </summary>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Block expressions are used for:
/// <list type="bullet">
/// <item>Inline if-else expressions: if condition then expr1 else expr2</item>
/// <item>Multi-statement computations that produce a value</item>
/// <item>Scoped variable declarations that contribute to a result</item>
/// </list>
/// </remarks>
/// <param name="Value">The final expression whose result the block evaluates to</param>
public record BlockExpression(Expression Value, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitBlockExpression(node: this);
    }
}

/// <summary>
/// Expression that chains multiple comparison operations in a single statement.
/// Allows natural mathematical notation like a &lt; b &lt; c instead of a &lt; b and b &lt; c.
/// </summary>
/// <param name="Operands">List of expressions to compare (minimum 2 required)</param>
/// <param name="Operators">List of comparison operators between operands (one fewer than operands)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Chained comparison features:
/// <list type="bullet">
/// <item>Mathematical notation: 1 &lt; x &lt; 10, a >= b == c</item>
/// <item>Short-circuit evaluation: stops on first false comparison</item>
/// <item>Mixed operators: supports different operators in the same chain</item>
/// <item>Type checking: ensures compatible types across all comparisons</item>
/// </list>
/// </remarks>
public record ChainedComparisonExpression(
    List<Expression> Operands,
    List<BinaryOperator> Operators,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitChainedComparisonExpression(node: this);
    }
}

/// <summary>
/// Expression that creates a sequence of values between start and end bounds.
/// Supports both simple ranges and ranges with custom step values.
/// </summary>
/// <param name="Start">Expression that evaluates to the starting value of the range</param>
/// <param name="End">Expression that evaluates to the ending value of the range (inclusive)</param>
/// <param name="Step">Optional expression for the step size between values (default is 1)</param>
/// <param name="IsDescending">Whether the range counts downward.</param>
/// <param name="Location">Source location information</param>
/// <param name="IsExclusive">Whether the end bound is excluded.</param>
/// <remarks>
/// Range expression variants:
/// <list type="bullet">
/// <item>Simple range: (0 to 10) creates sequence [0, 1, 2, ..., 10]</item>
/// <item>Step range: (0 to 10 by 2) creates [0, 2, 4, 6, 8, 10]</item>
/// <item>Reverse range: (10 downto 0) creates [10, 9, 8, ..., 0]</item>
/// <item>Reverse with step: (10 downto 0 by 2) creates [10, 8, 6, 4, 2, 0]</item>
/// <item>Iterable: can be used in for loops and collection operations</item>
/// </list>
/// </remarks>
public record RangeExpression(
    Expression Start,
    Expression End,
    Expression? Step,
    bool IsDescending,
    SourceLocation Location,
    bool IsExclusive = false) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitRangeExpression(node: this);
    }
}

/// <summary>
/// Expression that creates an anonymous function with parameters and a body expression.
/// Represents closures that can capture variables from their defining scope.
/// </summary>
/// <param name="Parameters">List of parameters the lambda accepts</param>
/// <param name="Body">Expression that forms the body of the lambda (returned value)</param>
/// <param name="Captures">Optional explicit captures from 'given' clause (null = implicit capture)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Lambda expression features:
/// <list type="bullet">
/// <item>Anonymous functions: (x, y) => x + y</item>
/// <item>Closure capture: can access variables from enclosing scope</item>
/// <item>Explicit captures: x given (lo, hi) => lo &lt;= x &lt; hi</item>
/// <item>Type inference: parameter and return types often inferred</item>
/// <item>Higher-order functions: can be passed as arguments or returned</item>
/// <item>Itertools: enables select, where, aggregate functions</item>
/// </list>
/// </remarks>
public record LambdaExpression(
    List<Parameter> Parameters,
    Expression Body,
    List<string>? Captures,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitLambdaExpression(node: this);
    }
}

#endregion

#region Function Call and Access Expressions

/// <summary>
/// Represents a parameter definition for functions, memberRoutines, and lambdas.
/// Includes optional type annotation and default value for flexible parameter handling.
/// </summary>
/// <param name="Name">Parameter name used for binding in the function body</param>
/// <param name="Type">Optional type annotation; if null, type is inferred from usage</param>
/// <param name="DefaultValue">Optional default value expression; allows parameter to be omitted in calls</param>
/// <param name="Location">Source location information</param>
/// <param name="IsVariadic">Whether the parameter captures variadic arguments.</param>
/// <remarks>
/// Parameter flexibility supports various declaration patterns:
/// <list type="bullet">
/// <item>Simple: param</item>
/// <item>Typed: param: s32</item>
/// <item>With default: param = 42</item>
/// <item>Fully specified: param: s32 = 42</item>
/// </list>
/// </remarks>
public record Parameter(
    string Name,
    TypeExpression? Type,
    Expression? DefaultValue,
    SourceLocation Location,
    bool IsVariadic = false);

#endregion

#region Type Expressions

/// <summary>
/// Expression that represents a type reference in type annotations and declarations.
/// Supports simple types, generic types, and complex type constructions.
/// </summary>
/// <param name="Name">Base type name (s32, Text, MyClass, etc.)</param>
/// <param name="GenericArguments">Optional list of type arguments for generic types</param>
/// <param name="Location">Source location information</param>
/// <param name="IsRvalue">True if this type was written with the `T` prefix mark (entity rvalue, return-position only). SA enforces position validity.</param>
/// <param name="Realm">Optional cross-language realm qualifier (e.g. <c>"RF"</c>); null = ambient realm of the file.</param>
/// <param name="SpliceHandle">Comptime type-position splice handle name (e.g. from <c>$typeof(m)</c>); null for ordinary types.</param>
/// <param name="ComptimeValue">Comptime const-generic expression for a value type-arg slot; null for ordinary type-args.</param>
/// <remarks>
/// Type expression patterns:
/// <list type="bullet">
/// <item>Primitive types: s32, f64, bool, letter</item>
/// <item>Entity types: Text, MyClass, Point</item>
/// <item>Generic types: List[s32], Dictionary[Text, s32]</item>
/// <item>Nested generics: List[Dictionary[Text, s32]]</item>
/// </list>
/// </remarks>
public record TypeExpression(
    string Name,
    List<TypeExpression>? GenericArguments,
    SourceLocation Location,
    bool IsRvalue = false,
    // Cross-language realm tag from a `Realm::Name` qualifier (e.g. `RF::Core.List`). "RF" = the
    // RazorForge/bare realm (the resolver skips Suflae's entity->Roamed lowering for it); null = the
    // ambient realm of the file. Only "RF" is wired for now (Suflae wrappers holding a bare RF entity).
    string? Realm = null,
    // Comptime type-position splice: when non-null this whole type IS the `${handle.type}` projection
    // of an expand handle (e.g. `${m.type}` in `Array[${m.type}, N]`). At expansion it resolves to the
    // current member/arm's static type. Null for an ordinary written type.
    string? SpliceHandle = null,
    // Comptime VALUE-position splice used as a const-generic argument: `${max(T.data_size().byte_size(), 8)}`
    // in `Array[U8, ${...}]`. When non-null this type-arg is a const-generic whose integer value is the
    // monomorph-time fold of this expression (see GenericAstRewriter / RewriteContext.TypeSubs). Distinct
    // from SpliceHandle, which is a TYPE splice; this is a comptime scalar. Null for ordinary type-args.
    Expression? ComptimeValue = null) : Expression(Location: Location)
{
    /// <summary>
    /// Conditional-conformance conditions from an <c>obeys P onlyif (cond, …)</c> clause: this generic
    /// type obeys THIS protocol ONLY when every listed condition holds (comma = AND). Set by the parser on
    /// the protocol TypeExpression in a type header's obeys list; null = unconditional obeys. Each condition
    /// is a <c>&lt;param&gt; obeys &lt;protocol&gt;</c> pair (a <see cref="GenericConstraintDeclaration"/>).
    /// Consumed by the conformance layer, which confers the protocol only for concrete instances whose
    /// substituted conditions resolve true — the visible, legible form of the structural cascade.
    /// </summary>
    public List<GenericConstraintDeclaration>? ConformanceConditions { get; set; }

    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitTypeExpression(node: this);
    }
}

/// <summary>
/// Expression that performs explicit type conversion between compatible types.
/// Supports both function-style and memberRoutine-style conversion syntax.
/// </summary>
/// <param name="TargetType">Name of the target type to convert to</param>
/// <param name="Expression">Source expression to convert</param>
/// <param name="IsMemberRoutineStyle">true for memberRoutine-style (x.s32!()), false for function-style (s32!(x))</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Type conversion styles:
/// <list type="bullet">
/// <item>Function-style: s32!(3.14), bool!(1)</item>
/// <item>memberRoutine-style: 3.14.s32!(), 1.bool!()</item>
/// <item>Safety: explicit conversions may fail at runtime</item>
/// <item>Checked: conversion failures throw exceptions</item>
/// </list>
/// Both styles are semantically equivalent; choice is stylistic preference.
/// </remarks>
public record TypeConversionExpression(
    string TargetType,
    Expression Expression,
    bool IsMemberRoutineStyle,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitTypeConversionExpression(node: this);
    }

    /// <summary>
    /// Semantic-owned lowering classification for this conversion.
    /// </summary>
    public CallLoweringKind LoweringKind { get; set; }

    /// <summary>
    /// The fully resolved conversion target type, when known.
    /// </summary>
    public TypeInfo? ConstructedType { get; set; }
}

#endregion

#region Memory Operation Expressions

/// <summary>
/// Expression representing generic memberRoutine calls with type parameters.
/// Used for generic operations like read!&lt;T&gt;() and write!&lt;T&gt;().
/// </summary>
/// <param name="Object">Expression representing the object being called</param>
/// <param name="memberRoutineName">Name of the generic memberRoutine</param>
/// <param name="TypeArguments">List of type arguments for the generic memberRoutine</param>
/// <param name="Arguments">List of argument expressions passed to the memberRoutine</param>
/// <param name="IsMemoryOperation">Whether this memberRoutine call uses ! syntax (memory operation)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Generic memberRoutine calls enable type-safe slice operations:
/// <list type="bullet">
/// <item>buffer.read!&lt;s32&gt;(0) - read s32 at offset 0</item>
/// <item>buffer.write!&lt;f64&gt;(8, 3.14) - write f64 at offset 8</item>
/// <item>Type arguments specify the data type for the operation</item>
/// </list>
/// </remarks>
public record GenericMemberRoutineCallExpression(
    Expression Object,
    string MemberRoutineName,
    List<TypeExpression> TypeArguments,
    List<Expression> Arguments,
    bool IsMemoryOperation,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitGenericMemberRoutineCallExpression(node: this);
    }

    /// <summary>
    /// When true, this call is a collection literal constructor (e.g., List[S64](1, 2, 3)).
    /// Codegen should emit create() + repeated add/add_last calls instead of a normal type constructor.
    /// </summary>
    public bool IsCollectionLiteral { get; set; }

    /// <summary>
    /// The fully resolved RoutineInfo from semantic analysis (with owner-level and memberRoutine-level
    /// generic substitution applied). Set for generic memberRoutine calls on objects (e.g., obj.MemberRoutine[U](args)).
    /// </summary>
    public RoutineInfo? ResolvedRoutine { get; set; }

    /// <summary>
    /// Semantic-owned lowering classification for this generic call.
    /// </summary>
    public CallLoweringKind LoweringKind { get; set; }

    /// <summary>
    /// The target type being constructed by this generic call, when applicable.
    /// </summary>
    public TypeInfo? ConstructedType { get; set; }
}

/// <summary>
/// Expression representing generic member access with type parameters.
/// Used for accessing generic properties or member variables with type specification.
/// </summary>
/// <param name="Object">Expression representing the object being accessed</param>
/// <param name="MemberName">Name of the generic member</param>
/// <param name="TypeArguments">List of type arguments for the generic member</param>
/// <param name="Location">Source location information</param>
public record GenericMemberExpression(
    Expression Object,
    string MemberName,
    List<TypeExpression> TypeArguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitGenericMemberExpression(node: this);
    }
}

/// <summary>
/// Unclassified bracket-access expression produced by the parser for any <c>expr[...]</c>,
/// <c>expr[...](...)</c>, or <c>expr![...](...)</c> form. The parser does NOT decide whether the
/// brackets are a generic type-argument list or a value index — it parses the bracket contents
/// uniformly as expressions and the <c>BracketReclassifyPass</c>
/// reclassifies every such node into an existing <see cref="IndexExpression"/>,
/// <see cref="GenericMemberRoutineCallExpression"/>, or <see cref="GenericMemberExpression"/> before the
/// main semantic resolve runs. No downstream consumer should ever observe this node.
/// </summary>
/// <param name="Object">Expression the brackets apply to (identifier, member access, etc.).</param>
/// <param name="Args">Bracket contents parsed as expressions (one per comma-separated entry).</param>
/// <param name="CallArgs">Trailing <c>(...)</c> call arguments, or null when there is no call.</param>
/// <param name="Location">Source location information.</param>
public record BracketAccessExpression(
    Expression Object,
    List<Expression> Args,
    List<Expression>? CallArgs,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>
    /// True when the failable <c>!</c> marker preceded the brackets or the call parens
    /// (e.g. <c>foo![T](x)</c> / <c>obj.MemberRoutine![T](x)</c>). Maps to
    /// <see cref="GenericMemberRoutineCallExpression.IsMemoryOperation"/> after reclassification.
    /// </summary>
    public bool IsFailable { get; init; }

    /// <summary>Accepts a visitor for AST traversal and transformation.</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitBracketAccessExpression(node: this);
    }
}

#endregion

#region Carrier Pattern Expressions

/// <summary>
/// Expression that produces the compile-time FNV-1a type_id for a type.
/// Used in lowered CrashablePattern branches to compare against the carrier's type_id field.
/// Codegen evaluates this as a U64 literal via ComputeTypeId(Type.FullName).
/// </summary>
/// <param name="Type">The type whose id is computed</param>
/// <param name="Location">Source location information</param>
public record TypeIdExpression(TypeExpression Type, SourceLocation Location)
    : Expression(Location: Location)
{

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor) =>
        visitor.VisitTypeIdExpression(node: this);
}

/// <summary>
/// Expression that extracts the payload from a Result/Lookup carrier and reinterprets it
/// as a specific concrete type.
/// Codegen emits: GEP field 1 -> load i64 -> inttoptr to ConcreteType*.
/// </summary>
/// <param name="Carrier">The Result/Lookup carrier expression</param>
/// <param name="ConcreteType">The concrete type to cast the payload to</param>
/// <param name="Location">Source location information</param>
public record CarrierPayloadExpression(
    Expression Carrier,
    TypeExpression ConcreteType,
    SourceLocation Location) : Expression(Location: Location)
{

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor) =>
        visitor.VisitCarrierPayloadExpression(node: this);
}

#endregion

#region Ownership Transfer Expressions

/// <summary>
/// Expression that transfers ownership of an entity (RazorForge only).
/// The steal keyword moves ownership from the source to the destination,
/// invalidating the source (it becomes a deadref).
/// </summary>
/// <param name="Operand">Expression being stolen from (must be stealable)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Steal expression rules:
/// <list type="bullet">
/// <item>Can steal: raw entities</item>
/// <item>Cannot steal: Viewing&lt;T&gt;, Modifying&lt;T&gt;, Retained&lt;T&gt;, Tracked&lt;T&gt;, Guarded&lt;T, P&gt;, Witnessed&lt;T, P&gt;, Consulting&lt;T&gt;, Amending&lt;T&gt;, Hijacked&lt;T&gt;</item>
/// <item>After stealing, source becomes deadref (using it is a build error)</item>
/// <item>Used for: ownership transfer (steal node), container push (list.push(steal node)),
/// and consuming iteration (for item in steal list)</item>
/// </list>
/// </remarks>
public record StealExpression(Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitStealExpression(node: this);
    }
}

#endregion
