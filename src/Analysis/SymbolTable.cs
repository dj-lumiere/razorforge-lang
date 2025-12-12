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

    /// <summary>Set of registered namespace names (module paths)</summary>
    private readonly HashSet<string> _namespaces = new();

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
                else if (existing is TypeWithConstructors existingTypeWithCtors)
                {
                    // Add constructor to existing type-with-constructors
                    existingTypeWithCtors.AddConstructor(constructor: newFunc);
                    return true;
                }
                else if (existing is RecordSymbol or EntitySymbol or VariantSymbol or TypeSymbol)
                {
                    // Type symbol exists, convert to TypeWithConstructors
                    var typeWithCtors = new TypeWithConstructors(Name: symbol.Name,
                        TypeSymbol: existing,
                        Constructors: new List<FunctionSymbol> { newFunc },
                        Visibility: existing.Visibility);
                    currentScope[key: symbol.Name] = typeWithCtors;
                    return true;
                }
            }
            // Handle type symbol when constructors already exist
            else if (symbol is RecordSymbol or EntitySymbol or VariantSymbol or TypeSymbol)
            {
                if (existing is FunctionSymbol existingFunc)
                {
                    // Function exists, convert to TypeWithConstructors
                    var typeWithCtors = new TypeWithConstructors(Name: symbol.Name,
                        TypeSymbol: symbol,
                        Constructors: new List<FunctionSymbol> { existingFunc },
                        Visibility: symbol.Visibility);
                    currentScope[key: symbol.Name] = typeWithCtors;
                    return true;
                }
                else if (existing is FunctionOverloadSet existingOverloads)
                {
                    // Overload set exists, convert to TypeWithConstructors
                    var typeWithCtors = new TypeWithConstructors(Name: symbol.Name,
                        TypeSymbol: symbol,
                        Constructors: existingOverloads.Overloads,
                        Visibility: symbol.Visibility);
                    currentScope[key: symbol.Name] = typeWithCtors;
                    return true;
                }
                else if (existing is TypeWithConstructors)
                {
                    // Type already registered with constructors - duplicate type declaration
                    return false;
                }
                else if (existing is RecordSymbol or EntitySymbol or VariantSymbol or TypeSymbol)
                {
                    // Type already exists with same kind - silently ignore (redeclaration from import)
                    // This handles cases like letter8 being both a primitive and a stdlib record
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

    /// <summary>
    /// Gets all symbols from all scopes.
    /// Used by code generators to enumerate external function declarations.
    /// </summary>
    /// <returns>Enumerable of all symbols in all scopes</returns>
    public IEnumerable<Symbol> GetAllSymbols()
    {
        foreach (Dictionary<string, Symbol> scope in _scopes)
        {
            foreach (Symbol symbol in scope.Values)
            {
                // If it's an overload set, return each function
                if (symbol is FunctionOverloadSet overloadSet)
                {
                    foreach (FunctionSymbol overload in overloadSet.Overloads)
                    {
                        yield return overload;
                    }
                }
                else
                {
                    yield return symbol;
                }
            }
        }
    }

    /// <summary>
    /// Registers a namespace (module path) in the symbol table.
    /// This allows distinguishing namespace-qualified functions from type methods.
    /// </summary>
    /// <param name="namespacePath">The namespace path (e.g., "Console", "Standard/IO")</param>
    public void RegisterNamespace(string namespacePath)
    {
        _namespaces.Add(namespacePath);
    }

    /// <summary>
    /// Checks if a given name is a registered namespace.
    /// Used to distinguish "Console.show" (namespace function) from "s64.add" (type method).
    /// </summary>
    /// <param name="name">The name to check</param>
    /// <returns>true if the name is a registered namespace, false otherwise</returns>
    public bool IsNamespace(string name)
    {
        return _namespaces.Contains(name);
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
    List<GenericConstraint>? GenericConstraints = null,
    string? CallingConvention = null,
    bool IsExternal = false) : Symbol(Name: Name, Type: ReturnType, Visibility: Visibility)
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
/// Contains both a type symbol and its constructor functions.
/// This allows types and their constructors to share the same name.
/// For example, 'record cstr { ... }' creates a StructSymbol and
/// 'routine cstr(...) -> cstr { ... }' creates constructor FunctionSymbols.
/// </summary>
public record TypeWithConstructors(
    string Name,
    Symbol TypeSymbol,
    List<FunctionSymbol> Constructors,
    VisibilityModifier Visibility) : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>Adds a constructor to this type</summary>
    public void AddConstructor(FunctionSymbol constructor)
    {
        Constructors.Add(item: constructor);
    }
}

/// <summary>
/// Entity symbol
/// </summary>
public record EntitySymbol(
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
public record RecordSymbol(
    string Name,
    VisibilityModifier Visibility,
    List<string>? GenericParameters = null,
    List<GenericConstraint>? GenericConstraints = null,
    List<string>? Interfaces = null) : Symbol(Name: Name, Type: null, Visibility: Visibility)
{
    /// <summary>true if this record has generic parameters</summary>
    public bool IsGeneric => GenericParameters != null && GenericParameters.Count > 0;

    /// <summary>true if this record implements the Crashable feature</summary>
    public bool IsCrashable => Interfaces?.Contains(item: "Crashable") ?? false;
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
public record ProtocolSymbol(
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
// TypeInfo is now defined in Compilers.Shared.AST.TypeInfo

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
