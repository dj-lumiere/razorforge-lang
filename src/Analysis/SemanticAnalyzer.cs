using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Comprehensive semantic analyzer for RazorForge and Suflae languages.
///
/// This analyzer performs multi-phase semantic analysis combining:
/// <list type="bullet">
/// <item>Traditional type checking and symbol resolution</item>
/// <item>Advanced memory safety analysis with ownership tracking</item>
/// <item>Language-specific behavior handling (RazorForge vs Suflae)</item>
/// <item>Memory operation validation (retain, share, track, etc.)</item>
/// <item>Cross-language compatibility checking</item>
/// </list>
///
/// The analyzer integrates tightly with the MemoryAnalyzer to enforce
/// RazorForge's explicit memory model and Suflae's automatic RC model.
/// It validates memory operations, tracks object ownership, and prevents
/// use-after-invalidation errors during compilation.
///
/// Key responsibilities:
/// <list type="bullet">
/// <item>Type compatibility checking with mixed-type arithmetic rejection</item>
/// <item>Symbol table management with proper lexical scoping</item>
/// <item>Memory operation method call detection and validation</item>
/// <item>Usurping function rule enforcement</item>
/// <item>Container move semantics vs automatic RC handling</item>
/// <item>Wrapper type creation and transformation tracking</item>
/// </list>
/// </summary>
public partial class SemanticAnalyzer : IAstVisitor<object?>
{
    #region field and property definition

    /// <summary>Symbol table for variable, function, and type declarations</summary>
    private readonly SymbolTable _symbolTable;

    /// <summary>Memory safety analyzer for ownership tracking and memory operations</summary>
    private readonly MemoryAnalyzer _memoryAnalyzer;

    /// <summary>List of semantic errors found during analysis</summary>
    private readonly List<SemanticError> _errors;

    /// <summary>Target language (RazorForge or Suflae) for language-specific behavior</summary>
    private readonly Language _language;

    /// <summary>Language mode for additional behavior customization</summary>
    private readonly LanguageMode _mode;

    /// <summary>Module resolver for handling imports</summary>
    private readonly ModuleResolver _moduleResolver;

    /// <summary>Source file name for error reporting</summary>
    private readonly string? _fileName;

    /// <summary>Tracks whether we're currently inside a danger block</summary>
    private bool _isInDangerMode = false;

    /// <summary>Tracks whether we're currently inside a mayhem block</summary>
    private bool _isInMayhemMode = false;

    /// <summary>Tracks whether we're currently inside a 'when' expression condition</summary>
    private bool _isInWhenCondition = false;

    /// <summary>Tracks whether we're currently inside a usurping function that can return Hijacked tokens</summary>
    private bool _isInUsurpingFunction = false;

    /// <summary>
    /// Tracks scoped token variables that cannot escape their scope.
    /// Maps token variable name to the scope depth where it was created.
    /// These tokens (Viewed, Hijacked, Seized, Observed) cannot be:
    /// - Assigned to variables outside the scoped statement
    /// - Returned from non-usurping functions
    /// - Passed to functions (unless consumed immediately)
    /// </summary>
    private readonly Dictionary<string, int> _scopedTokens = new();

    /// <summary>Current scope depth for tracking scoped tokens</summary>
    private int _scopeDepth = 0;

    /// <summary>
    /// Tracks source variables that are temporarily invalidated during scoped access.
    /// Maps source variable name to (scope depth, access type).
    /// When a source is invalidated, it cannot be used until the scoped statement exits.
    ///
    /// Examples:
    /// - viewing x as v { ... } - x is invalidated (cannot read or write)
    /// - hijacking x as h { ... } - x is invalidated (cannot read or write)
    /// - seizing shared_x as s { ... } - shared_x is invalidated (lock held)
    /// - observing shared_x as o { ... } - shared_x is invalidated (lock held)
    /// </summary>
    private readonly Dictionary<string, (int scopeDepth, string accessType)> _invalidatedSources =
        new();

    /// <summary>
    /// Gets the symbol table containing all resolved symbols.
    /// Useful for code generation to access function and type information.
    /// </summary>
    public SymbolTable SymbolTable => _symbolTable;

    /// <summary>
    /// Gets the loaded module information including ASTs.
    /// Used by code generator to compile imported module functions.
    /// </summary>
    public IReadOnlyDictionary<string, ModuleResolver.ModuleInfo> LoadedModules =>
        _moduleResolver.ModuleCache;

    /// <summary>
    /// Get all semantic and memory safety errors discovered during analysis.
    /// Combines traditional semantic errors with memory safety violations
    /// from the integrated memory analyzer for comprehensive error reporting.
    /// </summary>
    public List<SemanticError> Errors
    {
        get
        {
            var allErrors = new List<SemanticError>(collection: _errors);
            // Convert memory safety violations to semantic errors for unified reporting
            allErrors.AddRange(collection: _memoryAnalyzer.Errors.Select(selector: me =>
                new SemanticError(Message: me.Message, Location: me.Location)));
            return allErrors;
        }
    }

    #endregion

    /// <summary>
    /// Initialize semantic analyzer with integrated memory safety analysis.
    /// Sets up both traditional semantic analysis and memory model enforcement
    /// based on the target language's memory management strategy.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Suflae)</param>
    /// <param name="mode">Language mode for behavior customization</param>
    /// <param name="searchPaths">Optional custom search paths for module resolution</param>
    /// <param name="fileName">Source file name for error reporting</param>
    public SemanticAnalyzer(Language language, LanguageMode mode, List<string>? searchPaths = null,
        string? fileName = null)
    {
        _symbolTable = new SymbolTable();
        _memoryAnalyzer = new MemoryAnalyzer(language: language, mode: mode);
        _moduleResolver =
            new ModuleResolver(language: language, mode: mode, searchPaths: searchPaths);
        _errors = new List<SemanticError>();
        _language = language;
        _mode = mode;
        _fileName = fileName;

        InitializeBuiltInTypes();
    }

    /// <summary>
    /// Initialize built-in types for the RazorForge language.
    /// Registers standard library types like DynamicSlice and TemporarySlice.
    /// </summary>
    private void InitializeBuiltInTypes()
    {
        // Register DynamicSlice record type
        var heapSliceType = new TypeInfo(Name: "DynamicSlice", IsReference: false);
        var heapSliceSymbol =
            new StructSymbol(Name: "DynamicSlice", Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: heapSliceSymbol);

        // Register TemporarySlice record type
        var stackSliceType = new TypeInfo(Name: "TemporarySlice", IsReference: false);
        var stackSliceSymbol =
            new StructSymbol(Name: "TemporarySlice", Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: stackSliceSymbol);

        // Register primitive types
        RegisterPrimitiveType(typeName: "uaddr");
        RegisterPrimitiveType(typeName: "saddr");
        RegisterPrimitiveType(typeName: "u8");
        RegisterPrimitiveType(typeName: "u16");
        RegisterPrimitiveType(typeName: "u32");
        RegisterPrimitiveType(typeName: "u64");
        RegisterPrimitiveType(typeName: "u128");
        RegisterPrimitiveType(typeName: "s8");
        RegisterPrimitiveType(typeName: "s16");
        RegisterPrimitiveType(typeName: "s32");
        RegisterPrimitiveType(typeName: "s64");
        RegisterPrimitiveType(typeName: "s128");
        RegisterPrimitiveType(typeName: "f16");
        RegisterPrimitiveType(typeName: "f32");
        RegisterPrimitiveType(typeName: "f64");
        RegisterPrimitiveType(typeName: "f128");
        RegisterPrimitiveType(typeName: "d32");
        RegisterPrimitiveType(typeName: "d64");
        RegisterPrimitiveType(typeName: "d128");
        RegisterPrimitiveType(typeName: "bool");
        RegisterPrimitiveType(typeName: "letter");
        RegisterPrimitiveType(typeName: "letter8");
        RegisterPrimitiveType(typeName: "letter16");

        // Note: Crashable, Maybe, and error types are loaded from prelude modules
        // (ErrorHandling/Maybe, errors/Crashable, errors/common, etc.)
        // They are no longer hardcoded here.
    }

    /// <summary>
    /// Helper method to register a primitive type.
    /// </summary>
    private void RegisterPrimitiveType(string typeName)
    {
        var typeInfo = new TypeInfo(Name: typeName, IsReference: false);
        var typeSymbol = new TypeSymbol(Name: typeName,
            TypeInfo: typeInfo,
            Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: typeSymbol);
    }

    /// <summary>
    /// Performs semantic analysis on the entire program.
    /// Validates all declarations, statements, and expressions in the AST.
    /// </summary>
    /// <param name="program">The program AST to analyze</param>
    /// <returns>List of semantic errors found during analysis</returns>
    public List<SemanticError> Analyze(AST.Program program)
    {
        program.Accept(visitor: this);
        return Errors;
    }

    // Program
    /// <summary>
    /// Visits a program node and analyzes all top-level declarations.
    /// Automatically loads prelude modules before processing user code.
    /// </summary>
    /// <param name="node">Program node containing all declarations</param>
    /// <returns>Null</returns>
    public object? VisitProgram(AST.Program node)
    {
        // Load prelude modules (Maybe, Crashable, common error types)
        LoadPrelude();

        foreach (IAstNode declaration in node.Declarations)
        {
            declaration.Accept(visitor: this);
        }

        return null;
    }

    /// <summary>
    /// Loads prelude modules that are automatically available without explicit import.
    /// Includes Maybe, Crashable, and common error types.
    /// For Suflae, also includes Integer, Decimal, Console, and Collections.
    /// Note: Result/Lookup are NOT preluded as they are for immediate pattern matching, not storage.
    /// </summary>
    private void LoadPrelude()
    {
        // Common prelude modules for both languages
        var preludeModules = new List<string>
        {
            "ErrorHandling/Maybe",
            "errors/Crashable",
            "errors/common",
            "errors/DivisionByZeroError",
            "errors/IntegerOverflowError",
            "errors/IndexOutOfBoundsError"
        };

        // Suflae-specific prelude: auto-import Integer, Decimal, Console, and Collections
        // These are the default types for Suflae's high-level programming model
        if (_language == Language.Suflae)
        {
            preludeModules.AddRange(collection: new[]
            {
                // Arbitrary precision numeric types (Suflae defaults)
                "Integer",
                "Decimal",
                "Fraction",
                // Console I/O
                "Console",
                // Text
                "Text/Text",
                // Collections - Dynamic (heap-allocated, growable)
                "Collections/List",
                "Collections/Dict",
                "Collections/Set",
                "Collections/Deque",
                "Collections/BitList",
                "Collections/PriorityQueue",
                "Collections/SortedDict",
                "Collections/SortedList",
                "Collections/SortedSet",
                // Collections - Fixed capacity (heap-allocated, fixed size)
                "Collections/FixedList",
                "Collections/FixedDict",
                "Collections/FixedSet",
                "Collections/FixedDeque",
                "Collections/FixedBitList",
                // Collections - Value types (stack-allocated)
                "Collections/ValueList",
                "Collections/ValueBitList",
                "Collections/ValueTuple",
                // Collections - Tuple
                "Collections/Tuple"
            });
        }

        foreach (string modulePath in preludeModules)
        {
            try
            {
                ModuleResolver.ModuleInfo? moduleInfo =
                    _moduleResolver.LoadModule(importPath: modulePath);
                if (moduleInfo != null)
                {
                    // Register all declarations from the prelude module
                    RegisterPreludeDeclarations(ast: moduleInfo.Ast);
                }
            }
            catch (ModuleException)
            {
                // Silently ignore missing prelude modules during development
                // In production, these should always exist
            }
        }
    }
}
