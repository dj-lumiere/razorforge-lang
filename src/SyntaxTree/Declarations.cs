using TypeModel.Symbols;

namespace SyntaxTree;

#region Base Declaration Types

/// <summary>
/// Base entity for all declaration nodes in the AST.
/// Declarations introduce new names into scopes and define program structure.
/// </summary>
/// <param name="Location">Source location information for error reporting and debugging</param>
/// <remarks>
/// Declarations form the structural backbone of programs:
/// <list type="bullet">
/// <item>Variables: local and global state</item>
/// <item>Functions: executable procedures and routines</item>
/// <item>Types: classes, structs, enums, variants, traits</item>
/// <item>Modules: imports and module organization</item>
/// </list>
/// </remarks>
public abstract record Declaration(SourceLocation Location) : SyntaxTreeNode(Location: Location)
{
    /// <summary>
    /// The documentation-comment text (the lines of a <c>###</c> doc block) written immediately above
    /// this declaration, joined with newlines, or null if none. Attached by the parser; carried into
    /// <c>RoutineInfo.Documentation</c> at registration so hover / completion can surface it (including
    /// for stdlib symbols, which are parsed the same way). Not consumed by codegen.
    /// </summary>
    public string? Documentation { get; set; }
}

/// <summary>
/// Empty placeholder declaration used inside type bodies.
/// Equivalent to Python's 'pass' statement but as a declaration.
/// </summary>
/// <param name="Location">Source location information</param>
public record PassDeclaration(SourceLocation Location) : Declaration(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        // PassDeclaration is a no-op, just return default
        return default!;
    }
}

/// <summary>
/// Generic constraint declaration for type parameters.
/// Specifies requirements that generic type arguments must satisfy.
/// </summary>
/// <param name="ParameterName">The name of the generic type parameter (e.g., "T")</param>
/// <param name="ConstraintType">The type of constraint (obeys, record, entity)</param>
/// <param name="ConstraintTypes">List of types that the parameter must satisfy</param>
/// <param name="Location">Source location information.</param>
/// <remarks>
/// Supports various constraint syntaxes:
/// <list type="bullet">
/// <item>Protocol constraint: where T obeys Comparable</item>
/// <item>Value type constraint: where T obeys RecordType</item>
/// <item>Reference type constraint: where T obeys EntityType</item>
/// <item>Routine type constraint: where T obeys RoutineType</item>
/// </list>
/// </remarks>
public record GenericConstraintDeclaration(
    string ParameterName,
    ConstraintKind ConstraintType,
    List<TypeExpression>? ConstraintTypes = null,
    SourceLocation? Location = null);

/// <summary>
/// Associated-type clause introduced by <c>relates</c> on a type declaration.
/// Two forms:
/// <list type="bullet">
///   <item>Protocol slot declaration: <c>relates Iter obeys Iterator[T]</c> — <see cref="Name"/>
///   is the slot, <see cref="Constraint"/> is the bound, <see cref="Binding"/> is null.</item>
///   <item>Implementer binding: <c>relates ListEmitter[T] as Iter</c> — <see cref="Name"/> is the
///   slot, <see cref="Binding"/> is the concrete type, <see cref="Constraint"/> is null.</item>
/// </list>
/// </summary>
/// <param name="Name">The associated-type slot name (e.g. <c>Iter</c>).</param>
/// <param name="Constraint">Protocol bound for a slot declaration; null for a binding.</param>
/// <param name="Binding">Concrete type bound to the slot by an implementer; null for a declaration.</param>
/// <param name="Location">Source location.</param>
public record AssociatedTypeDeclaration(
    string Name,
    TypeExpression? Constraint = null,
    TypeExpression? Binding = null,
    SourceLocation? Location = null);

#endregion

#region Variable and FreeRoutine Declarations

/// <summary>
/// Variable declaration: var name: Type = initializer
/// Introduces a new variable binding in the current scope.
/// </summary>
/// <param name="Name">Variable identifier name</param>
/// <param name="Type">Optional type annotation; if null, type is inferred from initializer</param>
/// <param name="Initializer">Optional initial value expression</param>
/// <param name="Visibility">Access control modifier (public, published, internal, private)</param>
/// <param name="Location">Source location information</param>
/// <param name="Annotations">Optional annotation markers applied to the declaration.</param>
/// <param name="IsLateInit">Whether the variable is initialized lazily after declaration.</param>
/// <remarks>
/// Variable declarations support various patterns:
/// <list type="bullet">
/// <item>Type inference: var x = 42 (infers s64 for RazorForge/Integer for Suflae)</item>
/// <item>Explicit typing: var x: s64 = 42</item>
/// <item>Uninitialized (requires type): var x: s32</item>
/// <item>Published: published var x = 42 (public read, private write)</item>
/// </list>
///
/// Visibility levels:
/// <list type="bullet">
/// <item>public - read/write from anywhere</item>
/// <item>published - public read, private write</item>
/// <item>internal - read/write within module</item>
/// <item>private - read/write within file</item>
/// </list>
/// </remarks>
public record VariableDeclaration(
    string Name,
    TypeExpression? Type,
    Expression? Initializer,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<string>? Annotations = null,
    bool IsLateInit = false,
    bool IsGlobal = false) : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitVariableDeclaration(node: this);
    }
}

/// <summary>
/// One member-variable template inside a decl-position <c>expand</c>: <c>[secret] ${namesplice}: Type</c>
/// where the name is a <c>${m.name}</c> / <c>${"prefix" + m.name}</c> splice and the type may carry a
/// <c>${m.type}</c> splice. Materialized once per member of the concrete source type at instantiation.
/// </summary>
/// <param name="NamePrefix">The literal prefix before the field name (e.g. <c>"inner_"</c>), or "".</param>
/// <param name="Type">The column type (may contain a <c>${m.type}</c> <see cref="TypeExpression.SpliceHandle"/>).</param>
/// <param name="Visibility">Access modifier for the generated member variable.</param>
/// <param name="Location">Source location information.</param>
public record ExpandMemberTemplate(
    string NamePrefix,
    TypeExpression Type,
    VisibilityModifier Visibility,
    SourceLocation Location);

/// <summary>
/// A decl-position comptime member-generation directive inside a record/entity body:
/// <c>expand m in allmemvarof(T)</c> followed by an indented block of <see cref="ExpandMemberTemplate"/>s.
/// At instantiation (when the source type <c>T</c> is concrete) each template is materialized once per
/// member of <c>T</c>, laying out struct-of-arrays column members (SplitArray/SplitList). Never survives
/// to codegen — the generated <see cref="MemberVariableInfo"/> columns are appended to the concrete
/// instance's member list by the type registry.
/// </summary>
/// <param name="HandleName">The per-member handle identifier (e.g. <c>m</c>).</param>
/// <param name="SourceType">The type inside <c>allmemvarof(...)</c> — a generic param before instantiation.</param>
/// <param name="Templates">The per-member column templates.</param>
/// <param name="Location">Source location information.</param>
public record ExpandMemberDeclaration(
    string HandleName,
    TypeExpression SourceType,
    List<ExpandMemberTemplate> Templates,
    SourceLocation Location) : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitExpandMemberDeclaration(node: this);
    }
}

/// <summary>
/// FreeRoutine/routine declaration that defines executable code blocks.
/// Represents both traditional functions and RazorForge "routines".
/// </summary>
/// <param name="Name">FreeRoutine identifier name</param>
/// <param name="Parameters">List of parameter definitions</param>
/// <param name="ReturnType">Optional return type; null for void/procedure functions</param>
/// <param name="Body">FreeRoutine body statement (typically a BlockStatement)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Annotations">Decorators like @inline for properties</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="GenericConstraints">Optional generic constraints.</param>
/// <param name="IsFailable">Whether the routine has a failable <c>!</c> suffix.</param>
/// <param name="IsCommon">Whether the routine is a <c>common</c> (type-level static) member routine.</param>
/// <param name="Async">Suspended or threaded routine mode.</param>
/// <param name="IsDangerous">Whether the routine requires a <c>danger</c> context.</param>
/// <param name="IsWiredMemberRoutine">Whether the routine is a wired (compiler-synthesized) member routine.</param>
/// <remarks>
/// FreeRoutine declarations support:
/// <list type="bullet">
/// <item>Generic functions: routine sort[T](items: List[T])</item>
/// <item>Default parameters: routine greet(name: text = "World")</item>
/// <item>Type inference: parameters and return types can be inferred</item>
/// <item>Annotations: @inline,etc.</item>
/// <item>Visibility: public, private, internal</item>
/// <item>Suspended: async function that can use waitfor</item>
/// </list>
/// </remarks>
public record RoutineDeclaration(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    Statement Body,
    VisibilityModifier Visibility,
    List<string> Annotations, // For decorators like @inline, @readonly
    SourceLocation Location,
    List<string>? GenericParameters = null,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool IsFailable = false,
    bool IsCommon = false,
    AsyncStatus Async = AsyncStatus.None,
    bool IsDangerous = false,
    bool IsWiredMemberRoutine = false) : Declaration(Location: Location)
{
    /// <summary>
    /// Generic parameter names. Settable so SignatureResolver can desugar a protocol-typed
    /// parameter (<c>r: Iterable[S64]</c>) into an implicit generic (<c>__T0 obeys Iterable[S64]</c>)
    /// on the AST decl itself — keeping the AST in lockstep with the desugared <c>RoutineInfo</c>
    /// so the generic-monomorphization pipeline (FindInStdlib + GenericAstRewriter) treats implicit
    /// and explicit generics identically. The body property shadows the positional parameter; the
    /// primary constructor still initializes it.
    /// </summary>
    public List<string>? GenericParameters { get; set; } = GenericParameters;

    /// <summary>
    /// Generic constraints (incl. implicit <c>obeys</c> constraints synthesized for desugared
    /// protocol parameters). Settable for the same reason as <see cref="GenericParameters"/>.
    /// </summary>
    public List<GenericConstraintDeclaration>? GenericConstraints { get; set; } = GenericConstraints;

    /// <summary>
    /// Variables taken out of scope within this routine via <c>steal</c> / consumption (SA's
    /// per-routine deadref set, snapshotted after body analysis). The scope-exit teardown pass
    /// merges these into its move-set so a stolen binding is NOT destroyed at scope exit — the
    /// authoritative record of the explicit <c>steal</c> move, robust to the <c>steal</c> AST
    /// wrapper being normalized away (e.g. <c>Text(from_list: steal digits)</c> → <c>Text(digits)</c>).
    /// </summary>
    public HashSet<string>? StolenVariableNames { get; set; }

    /// <summary>
    /// The <see cref="RoutineInfo"/> this declaration was registered as, attached at registration time
    /// (SignatureResolver for user code, StdlibLoader for the stdlib). Codegen reads this DIRECTLY
    /// instead of re-deriving the binding by parsing the name string and looking the owner type up by
    /// bare name — a module-blind re-lookup that bound one module's body to another module's symbol
    /// when two modules declared the same-named type. Parallel to <see cref="CallExpression.ResolvedRoutine"/>.
    /// </summary>
    public RoutineInfo? ResolvedInfo { get; set; }

    /// <summary>
    /// For a member routine (dotted name like <c>T.serialize</c> or <c>List[T].append</c>), the bare
    /// owner segment BEFORE the type-args and the dot (<c>"T"</c>, <c>"List"</c>); null for a free
    /// routine. Captured structurally by the parser from the separate owner/memberRoutine tokens so consumers
    /// (SA template detection, registration) never re-split <see cref="Name"/> with <c>IndexOf('.')</c>
    /// — the name-canonicalization discipline: the dotted string is a rendering, these are the truth.
    /// </summary>
    public string? OwnerName { get; set; }

    /// <summary>The memberRoutine segment AFTER the final dot (<c>"serialize"</c>, <c>"append"</c>); null for
    /// a free routine (whose memberRoutine name is simply <see cref="Name"/>).</summary>
    public string? MemberRoutineName { get; set; }

    /// <summary>True when the owner carried type-args (<c>List[T].append</c>) — i.e. a real
    /// generic-instance receiver, not a bare type-parameter placeholder like <c>T.serialize</c>.</summary>
    public bool HasReceiverTypeArgs { get; set; }

    /// <summary>The receiver as a STRUCTURED type expression (<c>List[T]</c>, <c>Iterable[Text]</c>),
    /// captured by the parser instead of being serialized into <see cref="Name"/> and re-parsed by
    /// consumers via <c>ParseTypeExpressionString</c>. Null for a free routine or a bare
    /// type-parameter receiver (<c>T.serialize</c>). Name-canonicalization: prefer this over slicing
    /// the owner substring out of <see cref="Name"/>.</summary>
    public TypeExpression? ReceiverType { get; set; }

    /// <summary>
    /// The receiver exactly AS WRITTEN, including any type-args (<c>"List[T]"</c>, <c>"Iterable[Text]"</c>,
    /// <c>"T"</c>), or null for a free routine. This is the rendered registry-key owner form that several
    /// resolution/registration sites need (a bracketed owner keys the protocol-extension / concrete-
    /// specialization bucket). ASSEMBLED BY THE PARSER from the separate owner/bracket/arg tokens as it
    /// builds <see cref="Name"/> — NOT sliced back out of the concatenated <see cref="Name"/> string. This
    /// is the name-canonicalization discipline: the pieces come from the tokens the parser already has;
    /// the dotted <see cref="Name"/> is a rendering. Consumers MUST read this rather than re-split
    /// <see cref="Name"/>; prefer <see cref="OwnerName"/> (bare) or <see cref="ReceiverType"/> (structured)
    /// where those suffice.
    /// </summary>
    public string? RenderedReceiver { get; set; }

    /// <summary>
    /// The owner-qualified composite identity (<c>"List[T].append"</c>, <c>"S32.add"</c>) used as a
    /// registry / reachability-index key; for a free routine (or a type-body member whose owner comes from
    /// the enclosing <c>_currentType</c>, not extension syntax) this is just the bare <see cref="Name"/>.
    /// Built from the structured <see cref="RenderedReceiver"/> + <see cref="MemberRoutineName"/> in ONE
    /// place — the canonical composite. Index/lookup sites that need the owner-qualified form MUST read
    /// this; <see cref="Name"/> is the bare member. (While the parser still folds the owner into
    /// <see cref="Name"/> this equals <see cref="Name"/> exactly; once <see cref="Name"/> is bare this is
    /// the sole owner-qualified renderer.)
    /// </summary>
    public string QualifiedName =>
        RenderedReceiver is { } receiver ? $"{receiver}.{MemberRoutineName}" : Name;

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitFunctionDeclaration(node: this);
    }
}

#endregion

#region Type Declarations

/// <summary>
/// Entity (class) declaration that defines reference types with inheritance.
/// Represents object-oriented classes with member variables, memberRoutines, and inheritance.
/// </summary>
/// <param name="Name">Class identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Protocols">List of protocols to implement (obeys)</param>
/// <param name="Members">List of member declarations (member variables, memberRoutines, properties)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericConstraints">Optional generic constraints.</param>
/// <param name="HasPassBody">Whether the declaration uses a <c>pass</c> body.</param>
/// <remarks>
/// Entity declarations support:
/// <list type="bullet">
/// <item>Single protocol implementation: entity Dog obeys Animal</item>
/// <item>Multiple protocol implementation: entity Dog obeys Nameable, Trainable</item>
/// <item>Generic classes: entity Container[T]</item>
/// <item>Member visibility: public, private, internal member variables/memberRoutines</item>
/// <item>Creators: defined as special routine memberRoutines</item>
/// </list>
/// </remarks>
public record EntityDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> Protocols,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool HasPassBody = false) : Declaration(Location: Location)
{
    /// <summary>Associated-type clauses (<c>relates …</c>) on this entity. Set by the parser.</summary>
    public List<AssociatedTypeDeclaration>? AssociatedTypes { get; set; }

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitEntityDeclaration(node: this);
    }
}

/// <summary>
/// Record (struct) declaration that defines value types with structural equality.
/// Represents data structures with value semantics.
/// </summary>
/// <param name="Name">Record identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Protocols">List of protocols to implement (obeys)</param>
/// <param name="Members">List of member declarations (member variables and memberRoutines)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericConstraints">Optional generic constraints.</param>
/// <param name="HasPassBody">Whether the declaration uses a <c>pass</c> body.</param>
/// <param name="Annotations">Optional record annotations.</param>
/// <remarks>
/// Record declarations provide:
/// <list type="bullet">
/// <item>Value semantics: equality based on member variable values, not identity</item>
/// <item>Immutability: member variables are always readonly</item>
/// <item>Stack allocation: stored inline rather than heap-allocated</item>
/// <item>Generic records: record Point[T](x: T, y: T)</item>
/// <item>Copy creators: automatic with-expressions support</item>
/// </list>
/// </remarks>
public record RecordDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> Protocols,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    bool HasPassBody = false,
    List<string>? Annotations = null) : Declaration(Location: Location)
{
    /// <summary>Associated-type clauses (<c>relates …</c>) on this record. Set by the parser.</summary>
    public List<AssociatedTypeDeclaration>? AssociatedTypes { get; set; }

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitRecordDeclaration(node: this);
    }
}

/// <summary>
/// Choice declaration that defines discriminated unions of named constants.
/// Represents enumeration types with optional associated values.
/// </summary>
/// <param name="Name">Choice identifier name</param>
/// <param name="Cases">List of choice variant definitions with optional values</param>
/// <param name="member routines">List of memberRoutines that can be called on choice values</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Choice declarations support:
/// <list type="bullet">
/// <item>Simple enums: choice Status { Ok, Error, Pending }</item>
/// <item>Explicit values: choice HttpCode { Ok = 200, NotFound = 404 }</item>
/// <item>memberRoutines: choices can have associated behavior</item>
/// <item>Pattern matching: used with when statements</item>
/// </list>
/// </remarks>
public record ChoiceDeclaration(
    string Name,
    List<ChoiceCase> Cases,
    List<RoutineDeclaration> MemberRoutines,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    /// <summary>Associated-type clauses (<c>relates …</c>) on this choice. Set by the parser.</summary>
    public List<AssociatedTypeDeclaration>? AssociatedTypes { get; set; }

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitChoiceDeclaration(node: this);
    }
}

/// <summary>
/// Flags declaration that defines combinable bitflag sets.
/// Each member represents a power-of-two bit flag backed by U64.
/// </summary>
/// <param name="Name">Flags type identifier name</param>
/// <param name="Members">List of UPPER_IDENTIFIER member names (builder assigns bit positions)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Flags declarations support:
/// <list type="bullet">
/// <item>Combining: perms = READ or WRITE</item>
/// <item>Testing: perms is READ</item>
/// <item>Exact match: perms == (READ and WRITE)</item>
/// <item>Removal: perms but WRITE</item>
/// <item>Max 64 members (U64 backing)</item>
/// </list>
/// </remarks>
public record FlagsDeclaration(
    string Name,
    List<string> Members,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitFlagsDeclaration(node: this);
    }
}

/// <summary>
/// Crashable type declaration — a throwable error entity.
/// Always heap-allocated. Must provide crash_message() -> Text.
/// crash_title() is synthesized from the type name (CamelCase -> sentence case).
/// Automatically conforms to the Crashable protocol.
/// </summary>
/// <param name="Name">Crashable type identifier name</param>
/// <param name="Members">Field declarations and optional crash_message() routine body</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
public record CrashableDeclaration(
    string Name,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitCrashableDeclaration(node: this);
    }
}

/// <summary>
/// Variant declaration that defines type-based tagged unions.
/// Each member is a type — the type IS the tag. No named cases.
/// </summary>
/// <param name="Name">Variant identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Members">List of variant member types</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericConstraints">Optional generic constraints.</param>
/// <remarks>
/// Variant declarations enable type-safe unions:
/// <list type="bullet">
/// <item>Type-based: variant Value { S64, Text, None }</item>
/// <item>Pattern matching: when v is S64 n => use n</item>
/// <item>Generic variants: variant Maybe[T] { T, None }</item>
/// <item>No duplicate types allowed</item>
/// </list>
/// </remarks>
public record VariantDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<VariantMember> Members,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null)
    : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitVariantDeclaration(node: this);
    }
}

/// <summary>
/// Protocol (trait/interface) declaration that defines behavioral contracts.
/// Specifies memberRoutine signatures that implementing types must provide.
/// </summary>
/// <param name="Name">Protocol identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="ParentProtocols">List of parent protocols this protocol extends (obeys)</param>
/// <param name="member routines">List of memberRoutine signatures (without implementations)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericConstraints">Optional generic constraints.</param>
/// <remarks>
/// Protocol declarations enable polymorphism and code reuse:
/// <list type="bullet">
/// <item>Interface contracts: protocol Drawable { routine Me.draw() }</item>
/// <item>Generic protocols: protocol Comparable[T] { routine Me.cmp(you: Me) -> ComparisonSign }</item>
/// <item>Multiple implementation: types can implement multiple protocols</item>
/// <item>Default memberRoutines: protocols can provide default implementations</item>
/// <item>Protocol bounds: generic constraints (where T obeys Comparable)</item>
/// <item>Protocol inheritance: protocol DetailedPrintable obeys Printable { ... }</item>
/// </list>
/// </remarks>
public record ProtocolDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<TypeExpression> ParentProtocols,
    List<RoutineSignature> MemberRoutines,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null)
    : Declaration(Location: Location)
{
    /// <summary>Associated-type slot declarations (<c>relates …</c>) on this protocol. Set by the parser.</summary>
    public List<AssociatedTypeDeclaration>? AssociatedTypes { get; set; }

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitProtocolDeclaration(node: this);
    }
}

#endregion

#region Import and Module Declarations

/// <summary>
/// Module declaration that establishes the module path for all symbols in a file.
/// Must appear at the top of a source file, before any other declarations.
/// </summary>
/// <param name="Path">Slash-separated module path (e.g., "Standard/Errors")</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Module declarations establish module identity:
/// <list type="bullet">
/// <item>Module path: module Standard/Errors</item>
/// <item>Nested modules: module Company/Project/Utils</item>
/// <item>Symbol qualification: types become fully qualified as Path.TypeName</item>
/// <item>Import resolution: other files import this module by path</item>
/// </list>
/// </remarks>
public record ModuleDeclaration(string Path, SourceLocation Location)
    : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitModuleDeclaration(node: this);
    }
}

/// <summary>
/// Import declaration that brings external modules and symbols into scope.
/// Enables code organization and dependency management.
/// </summary>
/// <param name="ModulePath">Slash-separated module path (e.g., "Standard/Collections/List")</param>
/// <param name="Alias">Optional alias for imported module (as collections)</param>
/// <param name="SpecificImports">Optional list of specific symbols to import ([List, Dict])</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Import declarations support various patterns:
/// <list type="bullet">
/// <item>Full module: import Standard/Collections</item>
/// <item>With alias: import Standard/Collections as col</item>
/// <item>Specific items: import Standard/Collections.[List, Dict]</item>
/// <item>Nested modules: import Company/Project/Utils/Helpers</item>
/// </list>
/// </remarks>
public record ImportDeclaration(
    string ModulePath,
    string? Alias, // as alias
    List<string>? SpecificImports, // [item1, item2]
    SourceLocation Location,
    // Realm-qualified foreign-routine imports: `import Module.C::qsort` brings the `C::qsort` foreign
    // routine declared in `Module` into scope as a BARE name (`qsort(...)`), lifting the usual
    // `C::`/`LLVM::` call-site qualifier requirement for that one routine. Each entry is (Realm, Name).
    List<(string Realm, string Name)>? RealmImports = null) : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitImportDeclaration(node: this);
    }
}

#endregion

#region Supporting Types and Enums

/// <summary>
/// Specifies the async execution model for a routine. This is ORTHOGONAL to
/// <see cref="FailableVariant"/> (the compiler-generated failable-wrapper kind): a routine has an
/// async model AND, independently, may be a generated failable variant. The two used to be mixed
/// into one enum; they are now separate.
/// </summary>
public enum AsyncStatus
{
    /// <summary>Regular synchronous routine</summary>
    None,

    /// <summary>
    /// Lightweight async (suspended) - cooperative multitasking using green threads.
    /// Best for I/O-bound operations. Uses waitfor to await results.
    /// </summary>
    Suspended,

    /// <summary>
    /// OS-level threading for CPU-bound parallel work.
    /// Uses real threads with higher overhead but true parallelism.
    /// </summary>
    Threaded
}

/// <summary>
/// Which compiler-generated failable wrapper a routine is, if any. Orthogonal to
/// <see cref="AsyncStatus"/> (the async execution model). A routine is at most one of these; the
/// failable variants were previously mixed into <see cref="AsyncStatus"/> and are now separated out.
/// </summary>
public enum FailableVariant
{
    /// <summary>Not a compiler-generated failable variant (the common case).</summary>
    None,

    /// <summary>
    /// Compiler-generated lookup_ variant: wraps a failable routine to return Lookup[T].
    /// throw -> error carrier, absent -> zeroinitializer, return -> success carrier.
    /// </summary>
    Lookup,

    /// <summary>
    /// Compiler-generated check_ variant: wraps a failable routine to return Result[None].
    /// throw -> error carrier, absent/return -> success zeroinitializer (None).
    /// </summary>
    Check,

    /// <summary>
    /// Compiler-generated try_ variant for None-returning failable routines.
    /// Returns Bool (i1): true = success, false = absent or throw.
    /// </summary>
    TryBool,

    /// <summary>
    /// Compiler-generated try_ variant for non-None failable routines.
    /// Returns Maybe[T] carrier: absent/throw -> zeroinitializer (None), return value -> present.
    /// RoutineInfo.ReturnType is the full Maybe[T] type; codegen uses GetLLVMType directly.
    /// </summary>
    Try
}

/// <summary>
/// Value assigning used within Choice (enum) declarations.
/// Represents a single named constant with optional explicit value.
/// </summary>
/// <param name="Name">Variant identifier name</param>
/// <param name="Value">Optional explicit integer value (e.g., OK: 200)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Choice can have explicit or implicit values:
/// <list type="bullet">
/// <item>Implicit: variants are numbered 0, 1, 2, ...</item>
/// <item>Explicit: OK: 200, ERROR: 404</item>
/// <item>Mixed: Some explicit, some implicit (increment from last)</item>
/// </list>
/// </remarks>
public record ChoiceCase(
    string Name,
    Expression? Value, // For explicit values like OK: 200 - semantic analyzer converts
    SourceLocation Location);

/// <summary>
/// Variant member definition used within Variant declarations.
/// Each member is a type expression — the type IS the tag.
/// </summary>
/// <param name="Type">The type expression for this member</param>
/// <param name="Location">Source location information</param>
public record VariantMember(TypeExpression Type, SourceLocation Location);

/// <summary>
/// Routine (function) signature used within Protocol (trait) declarations.
/// Specifies memberRoutine contract without implementation.
/// </summary>
/// <param name="Name">memberRoutine identifier name</param>
/// <param name="Parameters">List of parameter definitions</param>
/// <param name="ReturnType">Optional return type; null for void memberRoutines</param>
/// <param name="Location">Source location information</param>
/// <param name="Annotations">Optional protocol annotations.</param>
/// <remarks>
/// FreeRoutine signatures define the contract that implementers must fulfill:
/// <list type="bullet">
/// <item>Abstract memberRoutines: no body, just signature</item>
/// <item>Parameter names: used for documentation and named arguments</item>
/// <item>Type constraints: can include generic bounds</item>
/// <item>Default implementations: traits can provide default bodies</item>
/// </list>
/// </remarks>
public record RoutineSignature(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    List<string>? Annotations,
    SourceLocation Location)
{
    /// <summary>
    /// True when the signature was written with the failable `!` marker. The `!` is a STRUCTURED
    /// attribute — <see cref="Name"/> stays bare.
    /// </summary>
    public bool IsFailable { get; init; }
}

#endregion

#region Advanced Declarations

/// <summary>
/// Define declaration that creates an alias for an existing identifier.
/// Allows renaming symbols for disambiguation or clarity.
/// </summary>
/// <param name="OldName">Original identifier name to alias</param>
/// <param name="NewName">New identifier name to use</param>
/// <param name="Location">Source location information</param>
/// <param name="Annotations">Optional annotation markers applied to the declaration.</param>
/// <remarks>
/// Redefinition enables:
/// <list type="bullet">
/// <item>Disambiguation: define standard/List as StdList</item>
/// <item>Migration: gradually rename APIs</item>
/// <item>Backward compatibility: keep old names working</item>
/// </list>
/// </remarks>
public record DefineDeclaration(string OldName, string NewName, SourceLocation Location,
    List<string>? Annotations)
    : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitDefineDeclaration(node: this);
    }
}

/// <summary>
/// Preset declaration that defines a build-time constant value.
/// Similar to const in other languages but with RazorForge naming convention.
/// </summary>
/// <param name="Name">Constant identifier name</param>
/// <param name="Type">Type of the constant value</param>
/// <param name="Value">Constant expression that must be evaluable at build time</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Preset declarations provide build-time constants:
/// <list type="bullet">
/// <item>Simple constants: preset PI: f64 = 3.14159</item>
/// <item>Integer constants: preset MAX_SIZE: u32 = 1024u32</item>
/// <item>Pointer constants: preset cnullptr: Address = 0a</item>
/// <item>Must be evaluable at build time</item>
/// </list>
/// </remarks>
public record PresetDeclaration(
    string Name,
    TypeExpression Type,
    Expression Value,
    SourceLocation Location) : Declaration(Location: Location)
{
    /// <summary>Whether declared <c>secret preset</c> — file-private (inlinable only in its own file).</summary>
    public bool IsSecret { get; init; }

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitPresetDeclaration(node: this);
    }
}

/// <summary>
/// External function declaration that links to native runtime functions.
/// Used for declaring functions implemented in C or other native languages.
/// </summary>
/// <param name="Name">Name of the external function</param>
/// <param name="GenericParameters">Generic type parameters if the function is generic</param>
/// <param name="Parameters">FreeRoutine parameters with types</param>
/// <param name="ReturnType">Return type of the function (null for void)</param>
/// <param name="CallingConvention">Calling convention ("C", "stdcall", "fastcall", etc.)</param>
/// <param name="IsVariadic">Whether the function accepts variable arguments (like C's printf with "...")</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericConstraints">Optional generic constraints.</param>
/// <param name="Annotations">Optional annotations attached to the external routine.</param>
/// <param name="IsDangerous">Whether the external routine is marked dangerous.</param>
/// <remarks>
/// External declarations link RazorForge to native runtime:
/// <list type="bullet">
/// <item>external("C") routine malloc(size: Address) -> cptr&lt;cvoid&gt;</item>
/// <item>external("C") routine free(ptr: cptr&lt;cvoid&gt;)</item>
/// <item>external routine heap_alloc!(bytes: Address) -> Address (default C convention)</item>
/// <item>No function body - implementation provided by native runtime</item>
/// <item>Links to C functions at build time</item>
/// </list>
/// </remarks>
public record ExternalDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<GenericConstraintDeclaration>? GenericConstraints,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    string? CallingConvention,
    bool IsVariadic,
    List<string>? Annotations,
    bool IsDangerous,
    SourceLocation Location) : Declaration(Location: Location)
{
    /// <summary>
    /// True when the external routine was declared with the failable `!` marker. The `!` is a
    /// STRUCTURED attribute — <see cref="Name"/> stays bare.
    /// </summary>
    public bool IsFailable { get; init; }

    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitExternalDeclaration(node: this);
    }
}

/// <summary>
/// Block form of external declarations grouping multiple routines under one calling convention.
/// <code>external("C") { routine foo() routine bar() }</code>
/// </summary>
public record ExternalBlockDeclaration(List<Declaration> Declarations, SourceLocation Location)
    : Declaration(Location: Location)
{
    /// <inheritdoc/>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitExternalBlockDeclaration(node: this);
    }
}

#endregion
