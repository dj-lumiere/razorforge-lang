using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Symbol table for tracking declared symbols across lexical scopes.
/// Implements a stack-based scope management system that supports nested scopes
/// with proper symbol resolution following lexical scoping rules.
/// </summary>
/// <remarks>
/// The symbol table maintains a stack of scopes, where each scope is a dictionary
/// mapping symbol names to their declarations. When looking up symbols, the table
/// searches from the innermost scope outward until a symbol is found or all scopes
/// are exhausted.
///
/// Scope management:
/// <list type="bullet">
/// <item>Global scope is always present and cannot be exited</item>
/// <item>Function scopes are created for function bodies</item>
/// <item>Block scopes are created for control flow constructs</item>
/// <item>Entity/record scopes are created for member declarations</item>
/// </list>
/// </remarks>
public class SymbolTable
{
    /// <summary>Stack of scopes, with the topmost being the current scope</summary>
    private readonly Stack<Dictionary<string, Symbol>> _scopes;

    /// <summary>
    /// Initializes a new symbol table with a global scope.
    /// </summary>
    public SymbolTable()
    {
        _scopes = new Stack<Dictionary<string, Symbol>>();
        EnterScope(); // Global scope - always present
    }

    /// <summary>
    /// Enters a new lexical scope.
    /// Creates a new scope dictionary and pushes it onto the scope stack.
    /// </summary>
    /// <remarks>
    /// This method is called when entering:
    /// <list type="bullet">
    /// <item>Function bodies</item>
    /// <item>Block statements (if, while, for, etc.)</item>
    /// <item>Entity or record declarations</item>
    /// <item>Lambda expressions</item>
    /// </list>
    /// </remarks>
    public void EnterScope()
    {
        _scopes.Push(item: new Dictionary<string, Symbol>());
    }

    /// <summary>
    /// Exits the current lexical scope.
    /// Removes the topmost scope from the stack, but preserves the global scope.
    /// </summary>
    /// <remarks>
    /// The global scope is never removed to ensure that global symbols
    /// remain accessible throughout the program. If this method is called
    /// when only the global scope remains, no action is taken.
    /// </remarks>
    public void ExitScope()
    {
        if (_scopes.Count > 1) // Keep global scope
        {
            _scopes.Pop();
        }
    }

    /// <summary>
    /// Attempts to declare a symbol in the current scope.
    /// </summary>
    /// <param name="symbol">The symbol to declare</param>
    /// <returns>true if the symbol was successfully declared; false if a symbol with the same name already exists in the current scope</returns>
    /// <remarks>
    /// This method only checks for name conflicts within the current scope.
    /// It is valid to shadow symbols from outer scopes. The symbol is added
    /// to the current (topmost) scope on the stack.
    /// Function symbols support overloading - multiple functions with the same name
    /// but different signatures can coexist in the same scope.
    /// </remarks>
    public bool TryDeclare(Symbol symbol)
    {
        Dictionary<string, Symbol> currentScope = _scopes.Peek();

        if (currentScope.TryGetValue(key: symbol.Name, value: out Symbol? existing))
        {
            // Handle function overloading
            if (symbol is FunctionSymbol newFunc)
            {
                if (existing is FunctionSymbol existingFunc)
                {
                    // Convert single function to overload set
                    var overloadSet = new FunctionOverloadSet(Name: symbol.Name,
                        Overloads: new List<FunctionSymbol> { existingFunc, newFunc },
                        Visibility: existingFunc.Visibility);
                    currentScope[key: symbol.Name] = overloadSet;
                    return true;
                }
                else if (existing is FunctionOverloadSet existingSet)
                {
                    // Add to existing overload set
                    existingSet.AddOverload(overload: newFunc);
                    return true;
                }
            }

            return false; // Non-function symbol conflict
        }

        currentScope[key: symbol.Name] = symbol;
        return true;
    }

    /// <summary>
    /// Looks up a symbol by name using lexical scoping rules.
    /// Searches from the innermost scope outward until the symbol is found.
    /// </summary>
    /// <param name="name">The name of the symbol to find</param>
    /// <returns>The symbol if found; null if not found in any scope</returns>
    /// <remarks>
    /// The lookup follows standard lexical scoping rules:
    /// <list type="bullet">
    /// <item>Search starts in the current (innermost) scope</item>
    /// <item>If not found, search each outer scope in order</item>
    /// <item>Return the first matching symbol found</item>
    /// <item>Return null if the symbol is not found in any scope</item>
    /// </list>
    /// This implements symbol shadowing - inner scope symbols hide outer scope symbols with the same name.
    /// </remarks>
    public Symbol? Lookup(string name)
    {
        // Search from innermost to outermost scope
        foreach (Dictionary<string, Symbol> scope in _scopes)
        {
            if (scope.TryGetValue(key: name, value: out Symbol? symbol))
            {
                return symbol;
            }
        }

        return null; // Symbol not found in any scope
    }
}

/// <summary>
/// Base class for all symbols in the symbol table
/// </summary>
public abstract record Symbol(string Name, TypeInfo? Type, VisibilityModifier Visibility);

/// <summary>
/// Variable symbol
/// </summary>
public record VariableSymbol(
    string Name,
    TypeInfo? Type,
    bool IsMutable,
    VisibilityModifier Visibility) : Symbol(Name: Name, Type: Type, Visibility: Visibility);

/// <summary>
/// Function symbol
/// </summary>
public record FunctionSymbol(
    string Name,
    List<Parameter> Parameters,
    TypeInfo? ReturnType,
    VisibilityModifier Visibility,
    bool IsUsurping = false,
    List<string>? GenericParameters = null,
    List<GenericConstraint>? GenericConstraints = null)
    : Symbol(Name: Name, Type: ReturnType, Visibility: Visibility)
{
    /// <summary>true if this function has generic parameters</summary>
    public bool IsGeneric => GenericParameters != null && GenericParameters.Count > 0;
}

/// <summary>
/// Contains multiple function overloads with the same name but different signatures.
/// This enables function overloading in RazorForge.
/// </summary>
public record FunctionOverloadSet(
    string Name,
    List<FunctionSymbol> Overloads,
    VisibilityModifier Visibility) : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>Adds an overload to this set</summary>
    public void AddOverload(FunctionSymbol overload)
    {
        Overloads.Add(item: overload);
    }
}

/// <summary>
/// Entity symbol
/// </summary>
public record ClassSymbol(
    string Name,
    TypeExpression? BaseClass,
    List<TypeExpression> Interfaces,
    VisibilityModifier Visibility,
    List<string>? GenericParameters = null,
    List<GenericConstraint>? GenericConstraints = null)
    : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>true if this entity has generic parameters</summary>
    public bool IsGeneric => GenericParameters != null && GenericParameters.Count > 0;
}

/// <summary>
/// Record symbol
/// </summary>
public record StructSymbol(
    string Name,
    VisibilityModifier Visibility,
    List<string>? GenericParameters = null,
    List<GenericConstraint>? GenericConstraints = null)
    : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>true if this record has generic parameters</summary>
    public bool IsGeneric => GenericParameters != null && GenericParameters.Count > 0;
}

/// <summary>
/// Option (enum) symbol
/// </summary>
public record MenuSymbol(string Name, VisibilityModifier Visibility)
    : Symbol(Name: Name, Type: null, Visibility: Visibility);

/// <summary>
/// Variant symbol
/// </summary>
public record VariantSymbol(
    string Name,
    VisibilityModifier Visibility,
    List<string>? GenericParameters = null,
    List<GenericConstraint>? GenericConstraints = null)
    : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>true if this variant has generic parameters</summary>
    public bool IsGeneric => GenericParameters != null && GenericParameters.Count > 0;
}

/// <summary>
/// Feature (trait/interface) symbol
/// </summary>
public record FeatureSymbol(
    string Name,
    VisibilityModifier Visibility,
    List<string>? GenericParameters = null,
    List<GenericConstraint>? GenericConstraints = null)
    : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>true if this feature has generic parameters</summary>
    public bool IsGeneric => GenericParameters != null && GenericParameters.Count > 0;
}

/// <summary>
/// Type symbol for built-in and defined types
/// </summary>
public record TypeSymbol(string Name, TypeInfo TypeInfo, VisibilityModifier Visibility)
    : Symbol(Name: Name, Type: TypeInfo, Visibility: Visibility);

/// <summary>
/// Type information for symbols, including primitive type classification.
/// Provides utilities for type checking and compatibility analysis.
/// </summary>
/// <param name="Name">The type name (e.g., "s32", "f64", "bool", "MyClass")</param>
/// <param name="IsReference">true if this is a reference type; false for value types</param>
/// <param name="GenericArguments">Generic type arguments if this is a generic type instantiation (e.g., Array[s32])</param>
/// <param name="IsGenericParameter">true if this represents a generic type parameter (e.g., T in Array[T])</param>
/// <remarks>
/// This record encapsulates type information used throughout semantic analysis.
/// It provides convenient properties to classify primitive types according to
/// RazorForge's type system:
/// <list type="bullet">
/// <item>Signed integers: s8, s16, s32, s64, s128, saddr</item>
/// <item>Unsigned integers: u8, u16, u32, u64, u128, uaddr</item>
/// <item>IEEE754 binary floating point: f16, f32, f64, f128</item>
/// <item>IEEE754 decimal floating point: d32, d64, d128</item>
/// <item>Character types: letter8, letter16, letter</item>
/// <item>Text types: text8, text16, text</item>
/// </list>
/// </remarks>
public record TypeInfo(
    string Name,
    bool IsReference,
    List<TypeInfo>? GenericArguments = null,
    bool IsGenericParameter = false)
{
    /// <summary>true if this is any numeric type (integer, floating point, or decimal)</summary>
    public bool IsNumeric => Name.StartsWith(value: "s") || Name.StartsWith(value: "u") ||
                             Name.StartsWith(value: "f") || Name.StartsWith(value: "d") ||
                             Name == "saddr" || Name == "uaddr";

    /// <summary>true if this is any integer type (signed or unsigned)</summary>
    public bool IsInteger => Name.StartsWith(value: "s") || Name.StartsWith(value: "u") ||
                             Name == "saddr" || Name == "uaddr";

    /// <summary>true if this is any floating point type (binary or decimal)</summary>
    public bool IsFloatingPoint => Name.StartsWith(value: "f") || Name.StartsWith(value: "d");

    /// <summary>true if this is a signed numeric type</summary>
    public bool IsSigned => Name.StartsWith(value: "s") || Name.StartsWith(value: "f") ||
                            Name.StartsWith(value: "d") || Name == "saddr";

    /// <summary>true if this is a generic type (has generic arguments)</summary>
    public bool IsGeneric => GenericArguments != null && GenericArguments.Count > 0;

    /// <summary>Gets the fully qualified type name including generic arguments (e.g., "Array[s32]")</summary>
    public string FullName
    {
        get
        {
            if (!IsGeneric)
            {
                return Name;
            }

            string args = string.Join(separator: ", ",
                values: GenericArguments!.Select(selector: t => t.FullName));
            return $"{Name}[{args}]";
        }
    }
}

/// <summary>
/// Generic constraint information for type parameters
/// </summary>
/// <param name="ParameterName">The name of the generic type parameter (e.g., "T")</param>
/// <param name="BaseTypes">List of base type constraints (e.g., where T : IComparable)</param>
/// <param name="IsValueType">true if constrained to value types (e.g., where T : record)</param>
/// <param name="IsReferenceType">true if constrained to reference types (e.g., where T : entity)</param>
public record GenericConstraint(
    string ParameterName,
    List<TypeInfo>? BaseTypes = null,
    bool IsValueType = false,
    bool IsReferenceType = false);

/// <summary>
/// Symbol representing a generic type parameter (e.g., T in routine swap&lt;T&gt;()).
/// Used during semantic analysis to recognize type parameters as valid types within
/// generic function and type definitions.
/// </summary>
/// <param name="Name">The name of the type parameter (e.g., "T", "K", "V")</param>
public record TypeParameterSymbol(string Name) : Symbol(Name: Name,
    Type: new TypeInfo(Name: Name, IsReference: false, IsGenericParameter: true),
    Visibility: VisibilityModifier.Private);

/// <summary>
/// Semantic error information
/// </summary>
public record SemanticError(string Message, SourceLocation Location, string? FileName = null);
