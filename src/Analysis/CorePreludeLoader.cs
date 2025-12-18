using Compilers.Shared.AST;
using Compilers.Shared.Lexer;
using Compilers.Shared.Parser;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Automatically loads core prelude types and functions that should be available
/// without explicit imports. Files are identified by their "namespace core" declaration,
/// not by directory structure.
/// </summary>
public class CorePreludeLoader
{
    private readonly string _stdlibPath;
    private readonly Language _language;

    public CorePreludeLoader(string stdlibPath, Language language)
    {
        _stdlibPath = stdlibPath;
        _language = language;
    }

    /// <summary>
    /// Scans the entire stdlib directory tree and loads all files that declare "namespace core".
    /// These modules will be automatically imported into every compilation.
    /// </summary>
    /// <returns>Dictionary mapping module names to their parsed ASTs</returns>
    public Dictionary<string, ModuleResolver.ModuleInfo> LoadCorePrelude()
    {
        var preludeModules = new Dictionary<string, ModuleResolver.ModuleInfo>();

        // Recursively scan entire stdlib directory
        ScanDirectory(_stdlibPath, preludeModules);

        return preludeModules;
    }

    /// <summary>
    /// Recursively scans a directory for .rf files with "namespace core" declarations
    /// </summary>
    private void ScanDirectory(string dirPath, Dictionary<string, ModuleResolver.ModuleInfo> modules)
    {
        if (!Directory.Exists(dirPath))
        {
            return;
        }

        // Process all .rf files in current directory
        foreach (string file in Directory.GetFiles(dirPath, "*.rf"))
        {
            LoadFileIfCoreNamespace(file, modules);
        }

        // Recursively scan subdirectories
        foreach (string subDir in Directory.GetDirectories(dirPath))
        {
            ScanDirectory(subDir, modules);
        }
    }

    /// <summary>
    /// Loads a file if it contains a "namespace core" declaration
    /// </summary>
    private void LoadFileIfCoreNamespace(string filePath, Dictionary<string, ModuleResolver.ModuleInfo> modules)
    {
        try
        {
            // Parse the file
            string sourceCode = File.ReadAllText(filePath);
            var tokens = Tokenizer.Tokenize(sourceCode, _language);
            var parser = new Compilers.RazorForge.Parser.RazorForgeParser(tokens, filePath);
            var ast = parser.Parse();

            // Check if this file declares "namespace core"
            bool isCoreNamespace = ast.Declarations.Any(decl =>
                decl is NamespaceDeclaration nsDecl && nsDecl.Path == "core");

            if (isCoreNamespace)
            {
                // Calculate module key relative to stdlib root
                string relativePath = Path.GetRelativePath(_stdlibPath, filePath);
                string moduleKey = relativePath.Replace("\\", "/").Replace(".rf", "");

                Console.WriteLine($"[CorePrelude] Loading: {moduleKey}");

                modules[moduleKey] = new ModuleResolver.ModuleInfo(
                    ModulePath: moduleKey,
                    FilePath: filePath,
                    Ast: ast,
                    Dependencies: new List<string>()
                );
            }
        }
        catch (Exception ex)
        {
            // Log but don't fail - some files might have syntax errors during development
            Console.Error.WriteLine($"Warning: Failed to scan file {filePath}: {ex.Message}");
        }
    }
}