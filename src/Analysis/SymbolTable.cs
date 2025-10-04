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
        _scopes.Push(new Dictionary<string, Symbol>());
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
    /// </remarks>
    public bool TryDeclare(Symbol symbol)
    {
        var currentScope = _scopes.Peek();

        if (currentScope.ContainsKey(symbol.Name))
        {
            return false; // Already declared in current scope
        }

        currentScope[symbol.Name] = symbol;
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
        foreach (var scope in _scopes)
        {
            if (scope.TryGetValue(name, out var symbol))
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
public record VariableSymbol(string Name, TypeInfo? Type, bool IsMutable, VisibilityModifier Visibility) 
    : Symbol(Name, Type, Visibility);

/// <summary>
/// Function symbol
/// </summary>
public record FunctionSymbol(string Name, List<Parameter> Parameters, TypeInfo? ReturnType, VisibilityModifier Visibility, bool IsUsurping = false, List<string>? GenericParameters = null)
    : Symbol(Name, ReturnType, Visibility);

/// <summary>
/// Entity symbol
/// </summary>
public record ClassSymbol(string Name, TypeExpression? BaseClass, List<TypeExpression> Interfaces, VisibilityModifier Visibility) 
    : Symbol(Name, null, Visibility);

/// <summary>
/// Record symbol
/// </summary>
public record StructSymbol(string Name, VisibilityModifier Visibility) 
    : Symbol(Name, null, Visibility);

/// <summary>
/// Option (enum) symbol
/// </summary>
public record MenuSymbol(string Name, VisibilityModifier Visibility) 
    : Symbol(Name, null, Visibility);

/// <summary>
/// Variant symbol
/// </summary>
public record VariantSymbol(string Name, VisibilityModifier Visibility) 
    : Symbol(Name, null, Visibility);

/// <summary>
/// Feature (trait/interface) symbol
/// </summary>
public record FeatureSymbol(string Name, VisibilityModifier Visibility)
    : Symbol(Name, null, Visibility);

/// <summary>
/// Type symbol for built-in and defined types
/// </summary>
public record TypeSymbol(string Name, TypeInfo TypeInfo, VisibilityModifier Visibility)
    : Symbol(Name, TypeInfo, Visibility);

/// <summary>
/// Type information for symbols, including primitive type classification.
/// Provides utilities for type checking and compatibility analysis.
/// </summary>
/// <param name="Name">The type name (e.g., "s32", "f64", "bool", "MyClass")</param>
/// <param name="IsReference">true if this is a reference type; false for value types</param>
/// <remarks>
/// This record encapsulates type information used throughout semantic analysis.
/// It provides convenient properties to classify primitive types according to
/// RazorForge's type system:
/// <list type="bullet">
/// <item>Signed integers: s8, s16, s32, s64, s128, syssint</item>
/// <item>Unsigned integers: u8, u16, u32, u64, u128, sysuint</item>
/// <item>IEEE754 binary floating point: f16, f32, f64, f128</item>
/// <item>IEEE754 decimal floating point: d32, d64, d128</item>
/// <item>Character types: letter8, letter16, letter</item>
/// <item>Text types: text8, text16, text</item>
/// </list>
/// </remarks>
public record TypeInfo(string Name, bool IsReference)
{
    /// <summary>true if this is any numeric type (integer, floating point, or decimal)</summary>
    public bool IsNumeric => Name.StartsWith("s") || Name.StartsWith("u") || Name.StartsWith("f") || Name.StartsWith("d") || Name == "syssint" || Name == "sysuint";

    /// <summary>true if this is any integer type (signed or unsigned)</summary>
    public bool IsInteger => Name.StartsWith("s") || Name.StartsWith("u") || Name == "syssint" || Name == "sysuint";

    /// <summary>true if this is any floating point type (binary or decimal)</summary>
    public bool IsFloatingPoint => Name.StartsWith("f") || Name.StartsWith("d");

    /// <summary>true if this is a signed numeric type</summary>
    public bool IsSigned => Name.StartsWith("s") || Name.StartsWith("f") || Name.StartsWith("d") || Name == "syssint";
}

/// <summary>
/// Semantic error information
/// </summary>
public record SemanticError(string Message, SourceLocation Location);