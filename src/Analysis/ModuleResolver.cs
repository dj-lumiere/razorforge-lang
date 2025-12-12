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
    private readonly string? _stdlibPath;
    private readonly string _projectRoot;
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
        _projectRoot = FindProjectRootInternal();
        _stdlibPath = FindStdlibPath();
        _searchPaths = searchPaths ?? GetDefaultSearchPaths();
    }

    /// <summary>
    /// Gets the default search paths for module resolution.
    /// </summary>
    private List<string> GetDefaultSearchPaths()
    {
        var paths = new List<string>();

        // Add stdlib directory (already resolved by FindStdlibPath())
        if (_stdlibPath != null)
        {
            paths.Add(item: _stdlibPath);
        }

        // Add project root directory
        paths.Add(item: _projectRoot);

        return paths;
    }

    /// <summary>
    /// Finds the project root directory by looking for forge.toml or bake.toml.
    /// Static helper method that doesn't depend on instance state.
    /// </summary>
    /// <returns>The project root directory path</returns>
    private static string FindProjectRootInternal()
    {
        string? current = Directory.GetCurrentDirectory();

        // Walk up the directory tree looking for forge.toml or bake.toml
        while (current != null)
        {
            if (File.Exists(Path.Combine(current, "forge.toml")) ||
                File.Exists(Path.Combine(current, "bake.toml")))
            {
                return current;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        // Fallback to current directory if no TOML found
        return Directory.GetCurrentDirectory();
    }

    /// <summary>
    /// Finds the stdlib path using priority: TOML → Environment Variable → Default Location.
    /// </summary>
    /// <returns>The stdlib path, or null if not found</returns>
    private string? FindStdlibPath()
    {
        // 1. Check TOML config (forge.toml or bake.toml)
        string? tomlPath = GetStdlibPathFromToml();
        if (tomlPath != null && Directory.Exists(tomlPath))
        {
            return Path.GetFullPath(tomlPath);
        }

        // 2. Check environment variable
        string? envPath = Environment.GetEnvironmentVariable("RAZORFORGE_STDLIB_PATH");
        if (envPath != null && Directory.Exists(envPath))
        {
            return Path.GetFullPath(envPath);
        }

        // 3. Default: relative to compiler executable
        string? exeDir = Path.GetDirectoryName(
            System.Reflection.Assembly.GetExecutingAssembly().Location);

        if (exeDir != null)
        {
            // Try directly next to executable
            string defaultPath = Path.Combine(exeDir, "stdlib");
            if (Directory.Exists(defaultPath))
            {
                return Path.GetFullPath(defaultPath);
            }

            // Try parent directories (for development builds)
            string? parent = Directory.GetParent(exeDir)?.FullName;
            for (int i = 0; i < 5 && parent != null; i++)
            {
                defaultPath = Path.Combine(parent, "stdlib");
                if (Directory.Exists(defaultPath))
                {
                    return Path.GetFullPath(defaultPath);
                }

                parent = Directory.GetParent(parent)?.FullName;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the stdlib_path from forge.toml or bake.toml if specified.
    /// </summary>
    /// <returns>The stdlib path from TOML, or null if not specified</returns>
    private string? GetStdlibPathFromToml()
    {
        // Find project root first
        string projectRoot = FindProjectRootInternal();

        // Look for forge.toml or bake.toml in project root
        string forgeToml = Path.Combine(projectRoot, "forge.toml");
        string bakeToml = Path.Combine(projectRoot, "bake.toml");

        string? tomlFile = null;
        if (File.Exists(forgeToml))
        {
            tomlFile = forgeToml;
        }
        else if (File.Exists(bakeToml))
        {
            tomlFile = bakeToml;
        }

        if (tomlFile == null)
        {
            return null;
        }

        // Parse TOML file for [build] stdlib_path setting
        // TODO: Implement proper TOML parsing when we add TOML library
        // For now, simple line-by-line parsing
        try
        {
            var lines = File.ReadAllLines(tomlFile);
            bool inBuildSection = false;

            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                // Check for [build] section
                if (trimmed == "[build]")
                {
                    inBuildSection = true;
                    continue;
                }

                // Check for new section (exit [build])
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inBuildSection = false;
                    continue;
                }

                // Parse stdlib_path in [build] section
                if (inBuildSection && trimmed.StartsWith("stdlib_path"))
                {
                    // Extract value: stdlib_path = "/path/to/stdlib"
                    int equalsIndex = trimmed.IndexOf('=');
                    if (equalsIndex >= 0)
                    {
                        string value = trimmed.Substring(equalsIndex + 1).Trim();
                        // Remove quotes if present
                        value = value.Trim('"', '\'');
                        return value;
                    }
                }
            }
        }
        catch
        {
            // Ignore TOML parsing errors, fall back to other methods
        }

        return null;
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
            var moduleInfo = new ModuleInfo(ModulePath: importPath,
                FilePath: filePath,
                Ast: ast,
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
    /// Gets all cached module information including ASTs.
    /// Used by code generator to compile imported module functions.
    /// </summary>
    public IReadOnlyDictionary<string, ModuleInfo> ModuleCache => _moduleCache;

    /// <summary>
    /// Clears the module cache.
    /// </summary>
    public void ClearCache()
    {
        _moduleCache.Clear();
        _loadedModules.Clear();
        _loadingModules.Clear();
    }

    /// <summary>
    /// Adds a module to the cache. Used by CorePreludeLoader to register prelude modules.
    /// </summary>
    public void AddToCache(string moduleKey, ModuleInfo moduleInfo)
    {
        _moduleCache[moduleKey] = moduleInfo;
        _loadedModules.Add(moduleKey);
    }

    /// <summary>
    /// Determines if a file is part of the standard library.
    /// </summary>
    /// <param name="filePath">The absolute file path to check</param>
    /// <returns>True if the file is in stdlib, false otherwise</returns>
    public bool IsStdlibFile(string filePath)
    {
        if (_stdlibPath == null)
        {
            return false;
        }

        string normalizedFile = Path.GetFullPath(filePath);
        string normalizedStdlib = Path.GetFullPath(_stdlibPath);

        return normalizedFile.StartsWith(normalizedStdlib, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Infers the namespace from a file path for project files (non-stdlib).
    /// Returns null for root-level project files (global namespace).
    /// Uses forward slashes for namespace separators.
    /// </summary>
    /// <param name="filePath">The absolute file path</param>
    /// <returns>The inferred namespace with forward slashes, or null for global namespace</returns>
    public string? InferNamespaceFromPath(string filePath)
    {
        string normalizedFilePath = Path.GetFullPath(filePath);
        string normalizedProjectRoot = Path.GetFullPath(_projectRoot);

        // If file is not under project root, return null (global)
        if (!normalizedFilePath.StartsWith(normalizedProjectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Get the directory containing the file
        string? fileDir = Path.GetDirectoryName(normalizedFilePath);
        if (fileDir == null)
        {
            return null;
        }

        // If file is directly in project root, it's global namespace
        if (string.Equals(fileDir, normalizedProjectRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Get the relative path from project root to file directory
        string relativePath = Path.GetRelativePath(normalizedProjectRoot, fileDir);

        // Convert backslashes to forward slashes for namespace
        string namespacePath = relativePath.Replace(Path.DirectorySeparatorChar, '/');

        // Normalize (remove leading/trailing slashes)
        namespacePath = namespacePath.Trim('/');

        return string.IsNullOrEmpty(namespacePath) ? null : namespacePath;
    }

    /// <summary>
    /// Gets the stdlib path if available.
    /// </summary>
    public string? StdlibPath => _stdlibPath;

    /// <summary>
    /// Gets the project root directory.
    /// </summary>
    public string ProjectRoot => _projectRoot;
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
