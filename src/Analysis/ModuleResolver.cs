using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;
using Compilers.RazorForge.Parser;
using Compilers.Suflae.Parser;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Resolves module imports and manages module loading.
/// Handles path resolution, file loading, parsing, and circular dependency detection.
/// </summary>
public class ModuleResolver
{
    private readonly Language _language;
    private readonly LanguageMode _mode;
    private readonly List<string> _searchPaths;
    private readonly HashSet<string> _loadedModules = new();
    private readonly HashSet<string> _loadingModules = new(); // For circular detection
    private readonly Dictionary<string, ModuleInfo> _moduleCache = new();

    /// <summary>
    /// Represents a loaded module with its AST and metadata.
    /// </summary>
    public record ModuleInfo(
        string ModulePath,
        string FilePath,
        Compilers.Shared.AST.Program Ast,
        List<string> Dependencies);

    /// <summary>
    /// Initializes a new module resolver.
    /// </summary>
    /// <param name="language">The language being compiled (RazorForge or Suflae)</param>
    /// <param name="mode">The language mode (Normal or Mayhem)</param>
    /// <param name="searchPaths">List of directories to search for modules (e.g., stdlib, local)</param>
    public ModuleResolver(Language language, LanguageMode mode, List<string>? searchPaths = null)
    {
        _language = language;
        _mode = mode;
        _searchPaths = searchPaths ?? GetDefaultSearchPaths();
    }

    /// <summary>
    /// Gets the default search paths for module resolution.
    /// </summary>
    private List<string> GetDefaultSearchPaths()
    {
        var paths = new List<string>();

        // Add stdlib directory
        string? exeDir = Path.GetDirectoryName(path: System
                                                    .Reflection.Assembly.GetExecutingAssembly()
                                                    .Location);
        if (exeDir != null)
        {
            // Try to find stdlib relative to executable
            string stdlibPath = Path.Combine(path1: exeDir, path2: "stdlib");
            if (Directory.Exists(path: stdlibPath))
            {
                paths.Add(item: stdlibPath);
            }
            else
            {
                // Try parent directories (for development builds)
                string? parent = Directory.GetParent(path: exeDir)
                                         ?.FullName;
                for (int i = 0; i < 5 && parent != null; i++)
                {
                    stdlibPath = Path.Combine(path1: parent, path2: "stdlib");
                    if (Directory.Exists(path: stdlibPath))
                    {
                        paths.Add(item: stdlibPath);
                        break;
                    }

                    parent = Directory.GetParent(path: parent)
                                     ?.FullName;
                }
            }
        }

        // Add current directory
        paths.Add(item: Directory.GetCurrentDirectory());

        return paths;
    }

    /// <summary>
    /// Resolves an import path to a file system path.
    /// </summary>
    /// <param name="importPath">The import path (e.g., "Collections/List")</param>
    /// <returns>The resolved file path, or null if not found</returns>
    public string? ResolveImportPath(string importPath)
    {
        // Normalize the import path
        string normalizedPath =
            importPath.Replace(oldChar: '/', newChar: Path.DirectorySeparatorChar);

        // Try with language-specific extension
        string extension = _language == Language.RazorForge
            ? ".rf"
            : ".sf";
        string fileNameWithExt = normalizedPath + extension;

        // Search in all search paths
        foreach (string searchPath in _searchPaths)
        {
            string fullPath = Path.Combine(path1: searchPath, path2: fileNameWithExt);
            if (File.Exists(path: fullPath))
            {
                return Path.GetFullPath(path: fullPath);
            }
        }

        // Try without adding extension (in case it's already in the import path)
        foreach (string searchPath in _searchPaths)
        {
            string fullPath = Path.Combine(path1: searchPath, path2: normalizedPath);
            if (File.Exists(path: fullPath))
            {
                return Path.GetFullPath(path: fullPath);
            }
        }

        return null;
    }

    /// <summary>
    /// Loads a module and all its transitive dependencies.
    /// </summary>
    /// <param name="importPath">The import path to load</param>
    /// <returns>The loaded module info, or null if loading failed</returns>
    public ModuleInfo? LoadModule(string importPath)
    {
        // Check cache first
        if (_moduleCache.TryGetValue(key: importPath, value: out ModuleInfo? cached))
        {
            return cached;
        }

        // Check for circular dependencies
        if (_loadingModules.Contains(item: importPath))
        {
            throw new ModuleException(message: $"Circular dependency detected: {importPath}");
        }

        // Resolve the file path
        string? filePath = ResolveImportPath(importPath: importPath);
        if (filePath == null)
        {
            throw new ModuleException(message: $"Module not found: {importPath}");
        }

        // Mark as loading (for circular detection)
        _loadingModules.Add(item: importPath);

        try
        {
            // Read and tokenize the file
            string source = File.ReadAllText(path: filePath);
            List<Token> tokens = Tokenizer.Tokenize(source: source, language: _language);

            // Parse the file
            BaseParser parser = _language == Language.RazorForge
                ? new RazorForgeParser(tokens: tokens, fileName: filePath)
                : new SuflaeParser(tokens: tokens, fileName: filePath);

            Compilers.Shared.AST.Program ast = parser.Parse();

            // Extract dependencies (imports in this module)
            var dependencies = new List<string>();
            foreach (IAstNode declaration in ast.Declarations)
            {
                if (declaration is ImportDeclaration import)
                {
                    dependencies.Add(item: import.ModulePath);
                }
            }

            // Create module info
            var moduleInfo = new ModuleInfo(ModulePath: importPath, FilePath: filePath, Ast: ast,
                Dependencies: dependencies);

            // Cache it
            _moduleCache[key: importPath] = moduleInfo;
            _loadedModules.Add(item: importPath);

            return moduleInfo;
        }
        finally
        {
            // Remove from loading set
            _loadingModules.Remove(item: importPath);
        }
    }

    /// <summary>
    /// Loads a module and all its transitive dependencies recursively.
    /// </summary>
    /// <param name="importPath">The import path to load</param>
    /// <returns>List of all loaded modules (including transitive dependencies)</returns>
    public List<ModuleInfo> LoadModuleWithDependencies(string importPath)
    {
        var loadedModules = new List<ModuleInfo>();
        var toLoad = new Queue<string>();
        var loaded = new HashSet<string>();

        toLoad.Enqueue(item: importPath);

        while (toLoad.Count > 0)
        {
            string currentPath = toLoad.Dequeue();

            if (loaded.Contains(item: currentPath))
            {
                continue;
            }

            ModuleInfo? moduleInfo = LoadModule(importPath: currentPath);
            if (moduleInfo == null)
            {
                throw new ModuleException(message: $"Failed to load module: {currentPath}");
            }

            loadedModules.Add(item: moduleInfo);
            loaded.Add(item: currentPath);

            // Enqueue dependencies
            foreach (string dependency in moduleInfo.Dependencies)
            {
                if (!loaded.Contains(item: dependency))
                {
                    toLoad.Enqueue(item: dependency);
                }
            }
        }

        return loadedModules;
    }

    /// <summary>
    /// Gets all loaded module paths.
    /// </summary>
    public IReadOnlySet<string> LoadedModules => _loadedModules;

    /// <summary>
    /// Clears the module cache.
    /// </summary>
    public void ClearCache()
    {
        _moduleCache.Clear();
        _loadedModules.Clear();
        _loadingModules.Clear();
    }
}

/// <summary>
/// Exception thrown when module resolution or loading fails.
/// </summary>
public class ModuleException : Exception
{
    public ModuleException(string message) : base(message: message) { }
    public ModuleException(string message, Exception innerException) : base(message: message,
        innerException: innerException)
    {
    }
}
