namespace Compilers.Shared.AST;

/// <summary>
/// Base entity for all declaration nodes
/// </summary>
public abstract record Declaration(SourceLocation Location) : AstNode(Location: Location);

/// <summary>
/// Variable declaration: var name: Type = initializer
/// </summary>
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
/// Function/recipe declaration
/// </summary>
public record FunctionDeclaration(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    Statement Body,
    VisibilityModifier Visibility,
    List<string> Attributes, // For decorators like @[everywhere get]
    SourceLocation Location,
    List<string>? GenericParameters = null) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFunctionDeclaration(node: this);
    }
}

/// <summary>
/// Entity declaration
/// </summary>
public record ClassDeclaration(
    string Name,
    List<string>? GenericParameters,
    TypeExpression? BaseClass, // from Animal
    List<TypeExpression> Interfaces, // implements/follows
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitClassDeclaration(node: this);
    }
}

/// <summary>
/// Record declaration
/// </summary>
public record StructDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitStructDeclaration(node: this);
    }
}

/// <summary>
/// Option (enum) declaration
/// </summary>
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
/// Variant (tagged union) declaration
/// </summary>
public enum VariantKind
{
    Chimera, // Default tagged union - requires danger! block
    Variant, // All fields must be records (value types)
    Mutant // Raw memory union - requires danger! block
}

public record VariantDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<VariantCase> Cases,
    List<FunctionDeclaration> Methods,
    VisibilityModifier Visibility,
    VariantKind Kind, // Track which keyword was used
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitVariantDeclaration(node: this);
    }
}

/// <summary>
/// Feature/Trait declaration
/// </summary>
public record FeatureDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<FunctionSignature> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location) : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitFeatureDeclaration(node: this);
    }
}

/// <summary>
/// Implementation block: SomeType follows SomeTrait { ... }
/// </summary>
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

/// <summary>
/// Import declaration: import module.submodule
/// </summary>
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

/// <summary>
/// Enum variant
/// </summary>
public record EnumVariant(
    string Name,
    int? Value, // For explicit values like Ok = 200
    SourceLocation Location);

/// <summary>
/// Variant case (for tagged unions)
/// </summary>
public record VariantCase(
    string Name,
    List<TypeExpression>? AssociatedTypes, // Success(T), Error(String)
    SourceLocation Location);

/// <summary>
/// Function signature (for traits/features)
/// </summary>
public record FunctionSignature(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    SourceLocation Location);

/// <summary>
/// Visibility modifiers controlling access to declarations.
/// Supports both Cake's descriptive keywords and RazorForge's traditional modifiers.
/// </summary>
/// <remarks>
/// The visibility system is designed to be intuitive while providing precise control:
///
/// Cake visibility (descriptive):
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
    // Cake descriptive visibility
    /// <summary>Only accessible within the declaring entity (Cake: onlyme)</summary>
    OnlyMe,

    /// <summary>Accessible within the declaring entity and its subclasses (Cake: onlyfamily)</summary>
    OnlyFamily,

    /// <summary>Accessible within the same module/package (Cake: onlyhere)</summary>
    OnlyHere,

    /// <summary>Accessible from anywhere (Cake: everywhere)</summary>
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

/// <summary>
/// Redefine statement: redefine OldName as NewName
/// </summary>
public record RedefinitionDeclaration(string OldName, string NewName, SourceLocation Location)
    : Declaration(Location: Location)
{
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitRedefinitionDeclaration(node: this);
    }
}

/// <summary>
/// Using statement: using TypeName as Alias
/// </summary>
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
/// <item>external("C") recipe malloc(size: sysuint) -> cptr&lt;cvoid&gt;</item>
/// <item>external("C") recipe free(ptr: cptr&lt;cvoid&gt;)</item>
/// <item>external recipe heap_alloc!(bytes: sysuint) -> sysuint (default C convention)</item>
/// <item>No function body - implementation provided by native runtime</item>
/// <item>Links to C functions at compile time</item>
/// </list>
/// </remarks>
public record ExternalDeclaration(
    string Name,
    List<string>? GenericParameters,
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
/// Recipe declaration - alias for FunctionDeclaration to support RazorForge "recipe" keyword.
/// Provides compatibility with test code that expects RecipeDeclaration type.
/// </summary>
public record RecipeDeclaration : FunctionDeclaration
{
    public RecipeDeclaration(string Name, List<Parameter> Parameters, TypeExpression? ReturnType,
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
