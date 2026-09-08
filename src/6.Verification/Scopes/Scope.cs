using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;

namespace Compiler.Verification.Scopes;

using TypeSymbol = TypeInfo;

/// <summary>
/// Represents a scope in the program for variable and symbol lookup.
/// Scopes form a tree hierarchy with parent/child relationships.
/// </summary>
public sealed class Scope
{
    /// <summary>The kind of this scope.</summary>
    public ScopeKind Kind { get; }

    /// <summary>Optional name for the scope (module name, function name, etc.).</summary>
    public string? Name { get; init; }

    /// <summary>The parent scope, or null for the global scope.</summary>
    public Scope? Parent { get; }

    /// <summary>Variables declared in this scope.</summary>
    private readonly Dictionary<string, VariableInfo> _variables = new();

    /// <summary>Type narrowings applied in this scope (e.g., after null checks).</summary>
    private readonly Dictionary<string, TypeSymbol> _typeNarrowings = new();

    /// <summary>Suflae flow typing: per-scope nullability facts for nullable entity references. A value
    /// of <c>true</c> means PROVEN non-none in this scope (e.g. inside `if x isnot None` or after a
    /// `if x is None: return` guard); <c>false</c> means KNOWN nullable again (e.g. reassigned a
    /// possibly-none value), which SHADOWS an outer proven fact. Absence falls through to the parent
    /// scope. Scoped like narrowings — a branch-scope fact is discarded on exit; a guard-clause fact
    /// lands in the enclosing scope and persists.</summary>
    private readonly Dictionary<string, bool> _nullabilityFacts = new();

    /// <summary>Variant arms proven EXCLUDED for a variable in this scope (by full type name), e.g.
    /// inside the else of `if x is A` on a variant. Accumulates down an if/elseif chain: once every
    /// arm but one is excluded, the variable narrows to that single remaining arm. A scope's set is
    /// self-contained (seeded with the inherited exclusions when first written).</summary>
    private readonly Dictionary<string, HashSet<string>> _excludedArms = new();

    /// <summary>For type scopes, the associated type.</summary>
    public TypeSymbol? AssociatedType { get; init; }

    /// <summary>
    /// Initializes a new instance of the <see cref="Scope"/> class.
    /// </summary>
    /// <param name="kind">The kind of scope.</param>
    /// <param name="parent">The parent scope, or null for the global scope.</param>
    public Scope(ScopeKind kind, Scope? parent = null)
    {
        Kind = kind;
        Parent = parent;
    }

    /// <summary>
    /// Declares a variable in this scope.
    /// </summary>
    /// <returns>True if successful, false if already declared in this scope.</returns>
    public bool DeclareVariable(VariableInfo variable)
    {
        return _variables.TryAdd(key: variable.Name, value: variable);
    }

    /// <summary>
    /// Looks up a variable by name, searching this scope and parent scopes.
    /// </summary>
    /// <returns>The variable info if found, null otherwise.</returns>
    public VariableInfo? LookupVariable(string name)
    {
        return _variables.TryGetValue(key: name, value: out VariableInfo? variable)
            ? variable
            : Parent?.LookupVariable(name: name);
    }

    /// <summary>
    /// Checks if a variable is declared in this exact scope (not parent scopes).
    /// </summary>
    /// <param name="name">The variable name to check.</param>
    /// <returns>True if the variable is declared in this scope, false otherwise.</returns>
    public bool IsDeclaredLocally(string name)
    {
        return _variables.ContainsKey(key: name);
    }

    /// <summary>
    /// Narrows the type of a variable in this scope.
    /// Used for type narrowing after pattern checks (e.g., after "unless x is None").
    /// </summary>
    /// <param name="name">The variable name to narrow.</param>
    /// <param name="narrowedType">The narrowed type.</param>
    public void NarrowVariable(string name, TypeSymbol narrowedType)
    {
        _typeNarrowings[key: name] = narrowedType;
    }

    /// <summary>
    /// Gets the narrowed type for a variable, searching this scope and parent scopes.
    /// </summary>
    /// <param name="name">The variable name to look up.</param>
    /// <returns>The narrowed type if found, null otherwise.</returns>
    public TypeSymbol? GetNarrowedType(string name)
    {
        return _typeNarrowings.TryGetValue(key: name, value: out TypeSymbol? type)
            ? type
            : Parent?.GetNarrowedType(name: name);
    }

    /// <summary>
    /// Records a variant arm (by full type name) as excluded for a variable in this scope, seeding
    /// from the inherited exclusions so the local set is self-contained for nested lookups.
    /// </summary>
    public void ExcludeArm(string name, string armFullName)
    {
        if (!_excludedArms.TryGetValue(key: name, value: out HashSet<string>? set))
        {
            set = new HashSet<string>(collection: GetExcludedArms(name: name));
            _excludedArms[key: name] = set;
        }

        set.Add(item: armFullName);
    }

    /// <summary>
    /// Gets the arms excluded for a variable, searching this scope then parent scopes.
    /// </summary>
    public IReadOnlyCollection<string> GetExcludedArms(string name)
    {
        return _excludedArms.TryGetValue(key: name, value: out HashSet<string>? set)
            ? set
            : Parent?.GetExcludedArms(name: name) ?? System.Array.Empty<string>();
    }

    /// <summary>
    /// Suflae flow typing: marks a nullable entity reference as PROVEN non-none in this scope.
    /// </summary>
    public void MarkNonNull(string name)
    {
        _nullabilityFacts[key: name] = true;
    }

    /// <summary>
    /// Suflae flow typing: records that a variable is KNOWN nullable again in this scope (e.g. after
    /// reassigning a possibly-none value). This SHADOWS any proven-non-none fact from an outer scope.
    /// </summary>
    public void MarkNullableAgain(string name)
    {
        _nullabilityFacts[key: name] = false;
    }

    /// <summary>
    /// Suflae flow typing: true if the variable has been proven non-none in the nearest enclosing scope
    /// that has a fact about it. A nearer <c>false</c> (re-nullified) shadows a farther <c>true</c>.
    /// </summary>
    public bool IsProvenNonNull(string name)
    {
        return _nullabilityFacts.TryGetValue(key: name, value: out bool proven)
            ? proven
            : Parent?.IsProvenNonNull(name: name) ?? false;
    }

    /// <summary>
    /// Gets all variables declared in this scope.
    /// </summary>
    /// <returns>An enumerable of all variables declared locally in this scope.</returns>
    public IEnumerable<VariableInfo> GetLocalVariables()
    {
        return _variables.Values;
    }

    /// <summary>
    /// Gets all variables visible in this scope (including from parent scopes).
    /// </summary>
    /// <returns>An enumerable of all variables visible from this scope.</returns>
    public IEnumerable<VariableInfo> GetAllVisibleVariables()
    {
        var variables = new Dictionary<string, VariableInfo>();

        // Collect from this scope and all parents
        Scope? current = this;
        while (current != null)
        {
            foreach (KeyValuePair<string, VariableInfo> kvp in current._variables)
            {
                // Don't override - inner scope shadows outer
                variables.TryAdd(key: kvp.Key, value: kvp.Value);
            }

            current = current.Parent;
        }

        return variables.Values;
    }

    /// <summary>
    /// Creates a child scope of this scope.
    /// </summary>
    /// <param name="kind">The kind of the child scope.</param>
    /// <param name="name">Optional name for the child scope.</param>
    /// <returns>The newly created child scope.</returns>
    public Scope CreateChildScope(ScopeKind kind, string? name = null)
    {
        return new Scope(kind: kind, parent: this) { Name = name };
    }

    /// <summary>
    /// Gets the depth of this scope (0 for global).
    /// </summary>
    public int Depth
    {
        get
        {
            int depth = 0;
            Scope? current = Parent;
            while (current != null)
            {
                depth++;
                current = current.Parent;
            }

            return depth;
        }
    }

    /// <summary>
    /// Checks if this scope is inside a danger block.
    /// </summary>
    public bool IsInDangerBlock
    {
        get
        {
            Scope? current = this;
            while (current != null)
            {
                if (current.Kind == ScopeKind.Danger)
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }
    }

    /// <summary>
    /// Checks if this scope is inside a loop (while, for, until).
    /// </summary>
    public bool IsInLoop
    {
        get
        {
            Scope? current = this;
            while (current != null)
            {
                if (current.Kind == ScopeKind.Loop)
                {
                    return true;
                }

                current = current.Parent;
            }

            return false;
        }
    }

    /// <summary>
    /// Gets the enclosing function scope, if any.
    /// </summary>
    public Scope? EnclosingFunction
    {
        get
        {
            Scope? current = this;
            while (current != null)
            {
                if (current.Kind == ScopeKind.Function)
                {
                    return current;
                }

                current = current.Parent;
            }

            return null;
        }
    }
}
