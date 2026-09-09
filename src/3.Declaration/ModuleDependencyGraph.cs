using Compiler.Diagnostics;
using Compiler.Verification.Results;
using SyntaxTree;

namespace Compiler.Declaration;

/// <summary>
/// Represents a module in the dependency graph.
/// A module corresponds to a unique module path (e.g., "Collections", "Banking/Core").
/// </summary>
public sealed class ModuleNode
{
    /// <summary>The module path (e.g., "Collections", "Standard/Errors").</summary>
    public string ModulePath { get; }

    /// <summary>The source file that defines this module (first file with this module declaration).</summary>
    public string? SourceFile { get; set; }

    /// <summary>Modules that this module imports.</summary>
    public HashSet<string> Dependencies { get; } = [];

    /// <summary>Modules that import this module (reverse dependencies).</summary>
    public HashSet<string> Dependents { get; } = [];

    /// <summary>Source locations of import statements for error reporting.</summary>
    public Dictionary<string, SourceLocation> ImportLocations { get; } = [];

    /// <summary>
    /// Initializes a new <see cref="ModuleNode"/> with the given module path.
    /// </summary>
    /// <param name="modulePath">The fully-qualified module path (e.g., "Collections", "Banking/Core").</param>
    public ModuleNode(string modulePath)
    {
        ModulePath = modulePath;
    }
}

/// <summary>
/// Tracks module dependencies and detects circular imports.
/// This is the core infrastructure for preventing Static Initialization Order Fiasco (SIOF).
/// </summary>
/// <remarks>
/// RazorForge/Suflae bans circular imports at build time because:
/// <list type="bullet">
/// <item>Presets (build-time constants) may depend on other module presets</item>
/// <item>Initialization order becomes undefined with circular dependencies</item>
/// <item>Clean architecture: circular deps indicate design problems</item>
/// </list>
/// </remarks>
public sealed class ModuleDependencyGraph
{
    /// <summary>All registered modules.</summary>
    private readonly Dictionary<string, ModuleNode> _modules = [];

    /// <summary>Errors discovered during dependency analysis.</summary>
    private readonly List<SemanticError> _errors = [];

    /// <summary>Gets all errors discovered during dependency analysis.</summary>
    public List<SemanticError> Errors => _errors;

    /// <summary>
    /// Registers a module in the graph.
    /// </summary>
    /// <param name="modulePath">The path of the module.</param>
    /// <param name="sourceFile">The source file defining this module.</param>
    /// <returns>The registered or existing module node.</returns>
    public ModuleNode GetOrCreateModule(string modulePath, string? sourceFile = null)
    {
        if (!_modules.TryGetValue(key: modulePath, value: out ModuleNode? node))
        {
            node = new ModuleNode(modulePath: modulePath);
            _modules[key: modulePath] = node;
        }

        if (sourceFile != null && node.SourceFile == null)
        {
            node.SourceFile = sourceFile;
        }

        return node;
    }

    /// <summary>
    /// Records that one module imports another.
    /// Immediately checks for circular dependencies.
    /// </summary>
    /// <param name="fromModule">The importing module's path.</param>
    /// <param name="toModule">The imported module's path.</param>
    /// <param name="importLocation">Source location of the import statement.</param>
    /// <returns>True if the import is valid, false if it creates a cycle.</returns>
    public bool AddDependency(string fromModule, string toModule, SourceLocation importLocation)
    {
        ModuleNode from = GetOrCreateModule(modulePath: fromModule);
        ModuleNode to = GetOrCreateModule(modulePath: toModule);

        // Check for self-import
        if (fromModule == toModule)
        {
            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.SelfImport,
                Message: $"Module '{fromModule}' cannot import itself.",
                Location: importLocation));
            return false;
        }

        // Add the dependency
        from.Dependencies.Add(item: toModule);
        from.ImportLocations[key: toModule] = importLocation;
        to.Dependents.Add(item: fromModule);

        // Check if this creates a cycle
        List<string>? cycle = FindCycleFrom(startModule: toModule, targetModule: fromModule);
        if (cycle != null)
        {
            // Build cycle path string: A -> B -> C -> A
            cycle.Add(item: fromModule); // Complete the cycle
            string cyclePath = string.Join(separator: " -> ", values: cycle);

            _errors.Add(item: new SemanticError(Code: SemanticDiagnosticCode.CircularImport,
                Message: $"Circular import detected: {cyclePath}",
                Location: importLocation));

            // Remove the dependency that created the cycle
            from.Dependencies.Remove(item: toModule);
            to.Dependents.Remove(item: fromModule);
            from.ImportLocations.Remove(key: toModule);

            return false;
        }

        return true;
    }

    /// <summary>
    /// Finds a path from startModule to targetModule if one exists.
    /// Used to detect cycles when adding a new dependency.
    /// </summary>
    /// <param name="startModule">Module to start searching from.</param>
    /// <param name="targetModule">Module we're trying to reach.</param>
    /// <returns>The cycle path if found, null otherwise.</returns>
    private List<string>? FindCycleFrom(string startModule, string targetModule)
    {
        var visited = new HashSet<string>();
        var path = new List<string>();

        // Depth-first search from current to targetModule. Appends visited nodes to path
        // and returns true once the target becomes reachable.
        bool DFS(string current)
        {
            if (current == targetModule)
            {
                return true;
            }

            if (visited.Contains(item: current))
            {
                return false;
            }

            visited.Add(item: current);
            path.Add(item: current);

            if (_modules.TryGetValue(key: current, value: out ModuleNode? node) &&
                node.Dependencies.Any(predicate: dep => DFS(current: dep)))
            {
                return true;
            }

            path.RemoveAt(index: path.Count - 1);
            return false;
        }

        if (DFS(current: startModule))
        {
            return path;
        }

        return null;
    }

    /// <summary>
    /// Gets the topological order for module initialization.
    /// Modules should be initialized in this order to ensure dependencies are ready.
    /// </summary>
    /// <returns>List of module paths in initialization order (dependencies first).</returns>
    /// <exception cref="InvalidOperationException">Thrown if there are undetected cycles.</exception>
    public List<string> GetInitializationOrder()
    {
        var result = new List<string>();
        var visited = new HashSet<string>();
        var visiting = new HashSet<string>(); // For cycle detection

        // Depth-first post-order traversal that emits each module after all dependencies.
        // Throws if an unexpected cycle slips past AddDependency validation.
        void Visit(string module)
        {
            if (visited.Contains(item: module))
            {
                return;
            }

            if (visiting.Contains(item: module))
            {
                // This should not happen if AddDependency properly validates
                throw new InvalidOperationException(
                    message:
                    $"Unexpected cycle detected at module '{module}'. This indicates a bug in dependency tracking.");
            }

            visiting.Add(item: module);

            if (_modules.TryGetValue(key: module, value: out ModuleNode? node))
            {
                foreach (string dep in node.Dependencies)
                {
                    Visit(module: dep);
                }
            }

            visiting.Remove(item: module);
            visited.Add(item: module);
            result.Add(item: module);
        }

        foreach (string module in _modules.Keys)
        {
            Visit(module: module);
        }

        return result;
    }

    /// <summary>
    /// Gets all modules that a given module transitively depends on.
    /// </summary>
    /// <param name="modulePath">The module to get dependencies for.</param>
    /// <returns>Set of all transitive dependencies.</returns>
    public IReadOnlySet<string> GetTransitiveDependencies(string modulePath)
    {
        var result = new HashSet<string>();
        var stack = new Stack<string>();

        stack.Push(item: modulePath);

        while (stack.Count > 0)
        {
            string current = stack.Pop();

            if (_modules.TryGetValue(key: current, value: out ModuleNode? node))
            {
                foreach (string dep in node.Dependencies.Where(predicate: dep =>
                             result.Add(item: dep)))
                {
                    stack.Push(item: dep);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Gets all registered modules.
    /// </summary>
    public IEnumerable<ModuleNode> GetAllModules()
    {
        return _modules.Values;
    }

    /// <summary>
    /// Gets a specific module if it exists.
    /// </summary>
    /// <param name="modulePath">The module path to look up.</param>
    /// <returns>The module node if found, null otherwise.</returns>
    public ModuleNode? GetModule(string modulePath)
    {
        return _modules.TryGetValue(key: modulePath, value: out ModuleNode? node)
            ? node
            : null;
    }

    /// <summary>
    /// Clears all modules and errors. Used for testing or rebuilding.
    /// </summary>
    public void Clear()
    {
        _modules.Clear();
        _errors.Clear();
    }
}
