namespace Compilers.Shared.AST;

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
/// <item>Modules: imports and namespace organization</item>
/// </list>
/// </remarks>
public abstract record Declaration(SourceLocation Location) : AstNode(Location: Location);

/// <summary>
/// Generic constraint declaration for type parameters.
/// Specifies requirements that generic type arguments must satisfy.
/// </summary>
/// <param name="ParameterName">The name of the generic type parameter (e.g., "T")</param>
/// <param name="ConstraintType">The type of constraint (follows, from, record, entity)</param>
/// <param name="ConstraintTypes">List of types that the parameter must satisfy</param>
/// <remarks>
/// Supports various constraint syntaxes:
/// <list type="bullet">
/// <item>Protocol constraint: T follows Comparable</item>
/// <item>Inheritance constraint: T from BaseType</item>
/// <item>Value type constraint: T: record</item>
/// <item>Reference type constraint: T: entity</item>
/// </list>
/// </remarks>
public record GenericConstraintDeclaration(
    string ParameterName,
    ConstraintKind ConstraintType,
    List<TypeExpression>? ConstraintTypes = null,
    SourceLocation Location = default);

/// <summary>
/// Types of generic constraints
/// </summary>
public enum ConstraintKind
{
    /// <summary>Protocol/interface implementation (T follows Comparable)</summary>
    Follows,
    /// <summary>Type inheritance (T from BaseType)</summary>
    From,
    /// <summary>Value type constraint (T: record)</summary>
    ValueType,
    /// <summary>Reference type constraint (T: entity)</summary>
    ReferenceType
}

#endregion

#region Variable and Function Declarations

/// <summary>
/// Variable declaration: var name: Type = initializer
/// Introduces a new variable binding in the current scope.
/// </summary>
/// <param name="Name">Variable identifier name</param>
/// <param name="Type">Optional type annotation; if null, type is inferred from initializer</param>
/// <param name="Initializer">Optional initial value expression</param>
/// <param name="Visibility">Access control modifier (public, private, etc.)</param>
/// <param name="IsMutable">true for 'var' (mutable), false for 'let' (immutable)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Variable declarations support various patterns:
/// <list type="bullet">
/// <item>Type inference: var x = 42 (infers s32)</item>
/// <item>Explicit typing: var x: s64 = 42</item>
/// <item>Uninitialized (requires type): var x: s32</item>
/// <item>Immutable: let x = 42 (cannot be reassigned)</item>
/// <item>Mutable: var x = 42 (can be reassigned)</item>
/// </list>
/// </remarks>
public record VariableDeclaration(
    string Name,
    TypeExpression? Type,
    Expression? Initializer,
    VisibilityModifier Visibility,
    bool IsMutable, // var vs let
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitVariableDeclaration(node: this);
    }
}

/// <summary>
/// Function/routine declaration that defines executable code blocks.
/// Represents both traditional functions and RazorForge "routines".
/// </summary>
/// <param name="Name">Function identifier name</param>
/// <param name="Parameters">List of parameter definitions</param>
/// <param name="ReturnType">Optional return type; null for void/procedure functions</param>
/// <param name="Body">Function body statement (typically a BlockStatement)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Attributes">Decorators like @[everywhere get] for properties</param>
/// <param name="Location">Source location information</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <remarks>
/// Function declarations support:
/// <list type="bullet">
/// <item>Generic functions: routine sort[T](items: Array[T])</item>
/// <item>Default parameters: routine greet(name: text = "World")</item>
/// <item>Type inference: parameters and return types can be inferred</item>
/// <item>Attributes: @[inline], @[everywhere get], etc.</item>
/// <item>Visibility: public, private, protected, internal</item>
/// </list>
/// </remarks>
public record FunctionDeclaration(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    Statement Body,
    VisibilityModifier Visibility,
    List<string> Attributes, // For decorators like @[everywhere get]
    SourceLocation Location,
    List<string>? GenericParameters = null,
    List<GenericConstraintDeclaration>? GenericConstraints = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFunctionDeclaration(node: this);
    }
}

#endregion

#region Type Declarations

/// <summary>
/// Entity (class) declaration that defines reference types with inheritance.
/// Represents object-oriented classes with fields, methods, and inheritance.
/// </summary>
/// <param name="Name">Class identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="BaseClass">Optional base class for inheritance (from Animal)</param>
/// <param name="Interfaces">List of traits/interfaces to implement (follows/implements)</param>
/// <param name="Members">List of member declarations (fields, methods, properties)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Entity declarations support:
/// <list type="bullet">
/// <item>Single inheritance: entity Dog from Animal</item>
/// <item>Multiple interface implementation: entity Dog follows Nameable, Trainable</item>
/// <item>Generic classes: entity Container[T]</item>
/// <item>Member visibility: public, private, protected fields/methods</item>
/// <item>Constructors: defined as special routine methods</item>
/// </list>
/// </remarks>
public record ClassDeclaration(
    string Name,
    List<string>? GenericParameters,
    TypeExpression? BaseClass, // from Animal
    List<TypeExpression> Interfaces, // implements/follows
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitClassDeclaration(node: this);
    }
}

/// <summary>
/// Record (struct) declaration that defines value types with structural equality.
/// Represents immutable data structures with value semantics.
/// </summary>
/// <param name="Name">Record identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Members">List of member declarations (fields and methods)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Record declarations provide:
/// <list type="bullet">
/// <item>Value semantics: equality based on field values, not identity</item>
/// <item>Immutability: fields are typically readonly</item>
/// <item>Stack allocation: stored inline rather than heap-allocated</item>
/// <item>Generic records: record Point[T](x: T, y: T)</item>
/// <item>Copy constructors: automatic with-expressions support</item>
/// </list>
/// </remarks>
public record StructDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null,
    List<TypeExpression>? Interfaces = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitStructDeclaration(node: this);
    }
}

/// <summary>
/// Option (menu/enum) declaration that defines discriminated unions of named constants.
/// Represents enumeration types with optional associated values.
/// </summary>
/// <param name="Name">Enum identifier name</param>
/// <param name="Variants">List of enum variant definitions with optional values</param>
/// <param name="Methods">List of methods that can be called on enum values</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Menu/Option declarations support:
/// <list type="bullet">
/// <item>Simple enums: option Status { Ok, Error, Pending }</item>
/// <item>Explicit values: option HttpCode { Ok = 200, NotFound = 404 }</item>
/// <item>Methods: enums can have associated behavior</item>
/// <item>Pattern matching: used with when statements</item>
/// </list>
/// </remarks>
public record MenuDeclaration(
    string Name,
    List<EnumVariant> Variants,
    List<FunctionDeclaration> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitMenuDeclaration(node: this);
    }
}

/// <summary>
/// Enumeration defining the kind of variant/union type.
/// Controls memory layout and safety guarantees.
/// </summary>
/// <remarks>
/// RazorForge provides three variant kinds with different safety/performance tradeoffs:
/// <list type="bullet">
/// <item>Chimera: Default tagged union - requires danger! block access, tracks active case</item>
/// <item>Variant: All fields must be records (value types) - safe, no danger! needed</item>
/// <item>Mutant: Raw memory union - requires danger! block, no safety guarantees</item>
/// </list>
/// </remarks>
public enum VariantKind
{
    Chimera, // Default tagged union - requires danger! block
    Variant, // All fields must be records (value types)
    Mutant // Raw memory union - requires danger! block
}

/// <summary>
/// Variant declaration that defines tagged unions with multiple possible value types.
/// Represents algebraic data types with multiple cases and associated data.
/// </summary>
/// <param name="Name">Variant identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Cases">List of variant cases with associated types</param>
/// <param name="Methods">List of methods that can be called on variant values</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Kind">Variant kind (Chimera, Variant, or Mutant)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Variant declarations enable powerful type-safe unions:
/// <list type="bullet">
/// <item>Tagged unions: variant Result[T, E] { Success(T), Error(E) }</item>
/// <item>Pattern matching: exhaustive case analysis with when statements</item>
/// <item>Generic variants: variant Option[T] { Some(T), None }</item>
/// <item>Safety levels: Chimera (safe), Variant (value-only), Mutant (unsafe)</item>
/// <item>Associated data: each case can carry different typed data</item>
/// </list>
/// </remarks>
public record VariantDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<VariantCase> Cases,
    List<FunctionDeclaration> Methods,
    VisibilityModifier Visibility,
    VariantKind Kind, // Track which keyword was used
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitVariantDeclaration(node: this);
    }
}

/// <summary>
/// Feature (trait/interface) declaration that defines behavioral contracts.
/// Specifies method signatures that implementing types must provide.
/// </summary>
/// <param name="Name">Trait identifier name</param>
/// <param name="GenericParameters">Optional list of generic type parameter names</param>
/// <param name="Methods">List of method signatures (without implementations)</param>
/// <param name="Visibility">Access control modifier</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Feature/Trait declarations enable polymorphism and code reuse:
/// <list type="bullet">
/// <item>Interface contracts: feature Drawable { routine draw() }</item>
/// <item>Generic traits: feature Comparable[T] { routine compare(other: T) -> s32 }</item>
/// <item>Multiple implementation: types can implement multiple features</item>
/// <item>Default methods: traits can provide default implementations</item>
/// <item>Trait bounds: generic constraints (where T: Comparable)</item>
/// </list>
/// </remarks>
public record FeatureDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<FunctionSignature> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location,
    List<GenericConstraintDeclaration>? GenericConstraints = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFeatureDeclaration(node: this);
    }
}

/// <summary>
/// Implementation block that provides method implementations for types.
/// Either inherent implementations (methods directly on type) or trait implementations.
/// </summary>
/// <param name="Type">The type being implemented (e.g., String, MyClass)</param>
/// <param name="Trait">Optional trait being implemented; null for inherent implementations</param>
/// <param name="Methods">List of method implementations</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Implementation blocks serve two purposes:
/// <list type="bullet">
/// <item>Inherent impls: String follows { routine length() -> sysuint }</item>
/// <item>Trait impls: MyType follows Drawable { routine draw() { ... } }</item>
/// <item>Extension methods: add methods to existing types</item>
/// <item>Organized code: group related methods together</item>
/// <item>Conditional compilation: different impls for different platforms</item>
/// </list>
/// </remarks>
public record ImplementationDeclaration(
    TypeExpression Type,
    TypeExpression? Trait, // null for inherent impl
    List<FunctionDeclaration> Methods,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitImplementationDeclaration(node: this);
    }
}

#endregion

#region Import and Module Declarations

/// <summary>
/// Import declaration that brings external modules and symbols into scope.
/// Enables code organization and dependency management.
/// </summary>
/// <param name="ModulePath">Dot-separated module path (e.g., "std.collections.list")</param>
/// <param name="Alias">Optional alias for imported module (as collections)</param>
/// <param name="SpecificImports">Optional list of specific symbols to import ({ List, Map })</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Import declarations support various patterns:
/// <list type="bullet">
/// <item>Full module: import std.collections</item>
/// <item>With alias: import std.collections as col</item>
/// <item>Specific items: import std.collections { List, Map }</item>
/// <item>Wildcard: import std.collections { * }</item>
/// <item>Nested modules: import company.project.utils.helpers</item>
/// </list>
/// </remarks>
public record ImportDeclaration(
    string ModulePath,
    string? Alias, // as alias
    List<string>? SpecificImports, // { item1, item2 }
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitImportDeclaration(node: this);
    }
}

#endregion

#region Supporting Types and Enums

/// <summary>
/// Enum variant definition used within Menu (enum) declarations.
/// Represents a single named constant with optional explicit value.
/// </summary>
/// <param name="Name">Variant identifier name</param>
/// <param name="Value">Optional explicit integer value (e.g., Ok = 200)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Enum variants can have explicit or implicit values:
/// <list type="bullet">
/// <item>Implicit: variants are numbered 0, 1, 2, ...</item>
/// <item>Explicit: Ok = 200, Error = 404</item>
/// <item>Mixed: Some explicit, some implicit (increment from last)</item>
/// </list>
/// </remarks>
public record EnumVariant(
    string Name,
    int? Value, // For explicit values like Ok = 200
    SourceLocation Location);

/// <summary>
/// Variant case definition used within Variant declarations.
/// Represents one possible case in a tagged union with associated type data.
/// </summary>
/// <param name="Name">Case identifier name</param>
/// <param name="AssociatedTypes">Optional list of types associated with this case</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Variant cases support associated data:
/// <list type="bullet">
/// <item>No data: None (unit case)</item>
/// <item>Single type: Some(T) (single associated value)</item>
/// <item>Multiple types: Error(code: s32, message: text) (tuple)</item>
/// <item>Record-like: Point(x: f64, y: f64) (named fields)</item>
/// </list>
/// </remarks>
public record VariantCase(
    string Name,
    List<TypeExpression>? AssociatedTypes, // Success(T), Error(String)
    SourceLocation Location);

/// <summary>
/// Function signature used within Feature (trait) declarations.
/// Specifies method contract without implementation.
/// </summary>
/// <param name="Name">Method identifier name</param>
/// <param name="Parameters">List of parameter definitions</param>
/// <param name="ReturnType">Optional return type; null for void methods</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Function signatures define the contract that implementers must fulfill:
/// <list type="bullet">
/// <item>Abstract methods: no body, just signature</item>
/// <item>Parameter names: used for documentation and named arguments</item>
/// <item>Type constraints: can include generic bounds</item>
/// <item>Default implementations: traits can provide default bodies</item>
/// </list>
/// </remarks>
public record FunctionSignature(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    SourceLocation Location);

/// <summary>
/// Visibility modifiers controlling access to declarations.
/// Supports both Suflae's descriptive keywords and RazorForge's traditional modifiers.
/// </summary>
/// <remarks>
/// The visibility system is designed to be intuitive while providing precise control:
///
/// Suflae visibility (descriptive):
/// <list type="bullet">
/// <item>onlyme - Only the declaring entity can access</item>
/// <item>onlyfamily - Only the declaring entity and its subclasses can access</item>
/// <item>onlyhere - Only code in the same module/package can access</item>
/// <item>everywhere - Any code can access</item>
/// </list>
///
/// RazorForge visibility (traditional):
/// <list type="bullet">
/// <item>private - Entity-private access only</item>
/// <item>protected - Entity and subclass access</item>
/// <item>internal - Module/assembly-private access</item>
/// <item>public - Unrestricted access</item>
/// </list>
///
/// The 'global' modifier is available in both languages for symbols that
/// should be accessible from any module without explicit import.
/// </remarks>
public enum VisibilityModifier
{
    // Suflae descriptive visibility
    /// <summary>Only accessible within the declaring entity (Suflae: onlyme)</summary>
    OnlyMe,

    /// <summary>Accessible within the declaring entity and its subclasses (Suflae: onlyfamily)</summary>
    OnlyFamily,

    /// <summary>Accessible within the same module/package (Suflae: onlyhere)</summary>
    OnlyHere,

    /// <summary>Accessible from anywhere (Suflae: everywhere)</summary>
    Everywhere,

    // RazorForge traditional visibility
    /// <summary>Only accessible within the declaring entity (RazorForge: private)</summary>
    Private,

    /// <summary>Accessible within the declaring entity and its subclasses (RazorForge: protected)</summary>
    Protected,

    /// <summary>Accessible within the same module/assembly (RazorForge: internal)</summary>
    Internal,

    /// <summary>Accessible from anywhere (RazorForge: public)</summary>
    Public,

    // Common to both languages
    /// <summary>Globally accessible without import (both languages: global)</summary>
    Global,
    External
}

#endregion

#region Advanced Declarations

/// <summary>
/// Redefine declaration that creates an alias for an existing identifier.
/// Allows renaming symbols for disambiguation or clarity.
/// </summary>
/// <param name="OldName">Original identifier name to alias</param>
/// <param name="NewName">New identifier name to use</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Redefinition enables:
/// <list type="bullet">
/// <item>Disambiguation: redefine std.List as StdList</item>
/// <item>Migration: gradually rename APIs</item>
/// <item>Backward compatibility: keep old names working</item>
/// </list>
/// </remarks>
public record RedefinitionDeclaration(string OldName, string NewName, SourceLocation Location)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitRedefinitionDeclaration(node: this);
    }
}

/// <summary>
/// Using declaration that creates a type alias for complex type expressions.
/// Simplifies usage of long or complex type names.
/// </summary>
/// <param name="Type">Type expression to alias</param>
/// <param name="Alias">Short alias name</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Using declarations improve code readability:
/// <list type="bullet">
/// <item>Complex generics: using StringMap = Dictionary[text, text]</item>
/// <item>Long names: using DB = DatabaseConnectionManager</item>
/// <item>Scoped aliases: defined within specific modules</item>
/// <item>Generic aliases: using Result[T] = variant { Ok(T), Error(text) }</item>
/// </list>
/// </remarks>
public record UsingDeclaration(TypeExpression Type, string Alias, SourceLocation Location)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitUsingDeclaration(node: this);
    }
}

/// <summary>
/// External function declaration that links to native runtime functions.
/// Used for declaring functions implemented in C or other native languages.
/// </summary>
/// <param name="Name">Name of the external function</param>
/// <param name="GenericParameters">Generic type parameters if the function is generic</param>
/// <param name="Parameters">Function parameters with types</param>
/// <param name="ReturnType">Return type of the function (null for void)</param>
/// <param name="CallingConvention">Calling convention ("C", "stdcall", "fastcall", etc.)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// External declarations link RazorForge to native runtime:
/// <list type="bullet">
/// <item>external("C") routine malloc(size: sysuint) -> cptr&lt;cvoid&gt;</item>
/// <item>external("C") routine free(ptr: cptr&lt;cvoid&gt;)</item>
/// <item>external routine heap_alloc!(bytes: sysuint) -> sysuint (default C convention)</item>
/// <item>No function body - implementation provided by native runtime</item>
/// <item>Links to C functions at compile time</item>
/// </list>
/// </remarks>
public record ExternalDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<GenericConstraintDeclaration>? GenericConstraints,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    string? CallingConvention,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitExternalDeclaration(node: this);
    }
}

/// <summary>
/// Routine declaration - alias for FunctionDeclaration to support RazorForge "routine" keyword.
/// Provides compatibility with test code that expects RoutineDeclaration type.
/// </summary>
public record RoutineDeclaration : FunctionDeclaration
{
    public RoutineDeclaration(string Name, List<Parameter> Parameters, TypeExpression? ReturnType,
        Statement Body, VisibilityModifier Visibility, List<string> Attributes,
        SourceLocation Location) : base(Name: Name, Parameters: Parameters, ReturnType: ReturnType,
        Body: Body, Visibility: Visibility, Attributes: Attributes, Location: Location)
    {
    }

    /// <summary>
    /// Convenience property to access the body as a BlockStatement
    /// </summary>
    public BlockStatement? BlockBody => Body as BlockStatement;
}

#endregion
