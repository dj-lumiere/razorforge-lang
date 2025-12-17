using System.Numerics;
using System.Text;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.Lexer;
using Compilers.Shared.Errors;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Production LLVM IR code generator for the RazorForge programming language.
/// Implements the visitor pattern to traverse the AST and emit optimized LLVM intermediate representation
/// that can be compiled to high-performance native machine code.
/// </summary>
/// <remarks>
/// This code generator provides comprehensive support for RazorForge language features:
/// <list type="bullet">
/// <item><strong>Type System</strong>: All primitive types including signed/unsigned integers, IEEE 754 floats, decimals</item>
/// <item><strong>Mathematical Libraries</strong>: Integration with libdfp, libbf, and mafm for precision arithmetic</item>
/// <item><strong>Memory Management</strong>: Stack allocation with proper LLVM memory semantics</item>
/// <item><strong>Control Flow</strong>: Complete implementation of conditionals, loops, and function calls</item>
/// <item><strong>Type Conversions</strong>: Comprehensive casting between all supported numeric types</item>
/// <item><strong>Overflow Handling</strong>: Support for various overflow behaviors (wrap, saturate, checked, unchecked)</item>
/// </list>
///
/// The generated LLVM IR follows modern LLVM conventions and is optimized for:
/// <list type="bullet">
/// <item>Performance: Efficient instruction selection and minimal overhead</item>
/// <item>Correctness: Type-safe operations with proper bounds checking</item>
/// <item>Portability: Target-independent IR that works across architectures</item>
/// <item>Debugging: Preserves source location information for debugging support</item>
/// </list>
///
/// <strong>Architecture:</strong>
/// The generator maintains several key data structures:
/// <list type="bullet">
/// <item>Symbol table for tracking variable types and locations</item>
/// <item>Temporary counter for generating unique LLVM temporary variables</item>
/// <item>Label counter for control flow basic block generation</item>
/// <item>Type tracking for temporary variables to enable accurate type conversions</item>
/// </list>
/// </remarks>
public partial class LLVMCodeGenerator : IAstVisitor<string>
{
    #region field and property definitions

    private StringBuilder _output;
    private readonly Language _language;
    private readonly LanguageMode _mode;
    private readonly TargetPlatform _targetPlatform;
    private int _tempCounter;
    private int _labelCounter;
    private int _varCounter; // For unique local variable names
    private readonly Dictionary<string, string> _symbolTypes;

    private readonly Dictionary<string, string>
        _symbolRfTypes = new(); // Track RazorForge types for symbols

    private readonly HashSet<string>
        _functionParameters = new(); // Track function parameters (no load needed)

    private readonly HashSet<string>
        _globalConstants = new(); // Track global constants (presets) - use @name instead of %name

    private readonly Dictionary<string, LLVMTypeInfo>
        _tempTypes = new(); // Track types of temporary variables

    private bool _hasReturn = false;
    private bool _blockTerminated = false; // Track if current block is terminated (ret/br)
    private List<string>? _stringConstants; // Collect string constants for proper emission

    // Generic instantiation tracking for monomorphization
    private readonly Dictionary<string, List<List<TypeInfo>>> _genericInstantiations = new();

    private readonly Dictionary<string, FunctionDeclaration> _genericFunctionTemplates = new();
    private readonly List<string> _pendingGenericInstantiations = new();

    // Generic type (record/entity) templates for monomorphization
    private readonly Dictionary<string, RecordDeclaration> _genericRecordTemplates = new();
    private readonly Dictionary<string, EntityDeclaration> _genericEntityTemplates = new();
    private readonly Dictionary<string, List<List<string>>> _genericTypeInstantiations = new();
    private readonly List<string> _pendingRecordInstantiations = new();
    private readonly List<string> _pendingEntityInstantiations = new();

    private readonly HashSet<string>
        _emittedTypes = new(); // Track already emitted type definitions

    // Non-generic record type tracking for constructor detection
    // Maps record name to its field declarations (name, type)
    private readonly Dictionary<string, List<(string Name, string Type)>> _recordFields = new();

    // Track RazorForge field types (before LLVM conversion) for method lookup
    private readonly Dictionary<string, List<(string Name, string RfType)>> _recordFieldsRfTypes = new();

    // CompilerService intrinsic tracking
    private string? _currentFileName;
    private string? _currentFunctionName;

    private SourceLocation _currentLocation = new(FileName: "",
        Line: 0,
        Column: 0,
        Position: 0);

    /// <summary>
    /// Sets the source file name for stack trace information.
    /// Should be called before Generate() with the path to the source file.
    /// </summary>
    public string? SourceFileName
    {
        get => _currentFileName;
        set => _currentFileName = value;
    }

    // Lambda code generation tracking
    private int _lambdaCounter;
    private List<string> _pendingLambdaDefinitions = new();

    // Stack trace support for runtime error reporting
    private readonly SymbolTables _symbolTables = new();
    private StackTraceCodeGen? _stackTraceCodeGen;

    // Crash message resolver for reading error messages from stdlib
    private CrashMessageResolver? _crashMessageResolver;

    // Semantic symbol table from semantic analysis
    private SymbolTable? _semanticSymbolTable;

    // Loaded modules from semantic analysis (for compiling imported functions)
    private IReadOnlyDictionary<string, ModuleResolver.ModuleInfo>? _loadedModules;

    // Track generated imported module functions (non-generic)
    private readonly HashSet<string> _generatedFunctions = new();

    // Track declared native functions to avoid duplicate declarations
    private readonly HashSet<string> _declaredNativeFunctions = new();
    private readonly List<string> _nativeFunctionDeclarations = new();

    // Current program being compiled (for looking up functions defined in current file)
    private AST.Program? _currentProgram;

    // Current type substitutions for generic function body generation
    private Dictionary<string, string>? _currentTypeSubstitutions;

    /// <summary>
    /// Sets the semantic symbol table for resolving external functions and imports.
    /// Should be called after semantic analysis and before code generation.
    /// </summary>
    public SymbolTable? SemanticSymbolTable
    {
        get => _semanticSymbolTable;
        set => _semanticSymbolTable = value;
    }

    /// <summary>
    /// Sets the loaded modules for compiling imported function bodies.
    /// Should be called after semantic analysis and before code generation.
    /// </summary>
    public IReadOnlyDictionary<string, ModuleResolver.ModuleInfo>? LoadedModules
    {
        get => _loadedModules;
        set => _loadedModules = value;
    }

    // Return statement - we need to track the expected function return type
    private string
        _currentFunctionReturnType = "i32"; // Default, will be set by VisitFunctionDeclaration

    // Set of C runtime functions already declared in boilerplate
    private static readonly HashSet<string> _builtinExternals = new()
    {
        "printf",
        "puts",
        "putchar",
        "scanf",
        "fgets",
        "fflush",
        "malloc",
        "free",
        "strtol",
        "exit",
        "__acrt_iob_func"
    };

    #endregion

    /// <summary>
    /// Initializes a new LLVM IR code generator for the specified language and mode configuration.
    /// Sets up the internal state required for AST traversal and IR generation.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Suflae) affecting syntax and semantics</param>
    /// <param name="mode">Language mode (Normal/Freestanding for RazorForge and Suflae)</param>
    /// <param name="targetPlatform">Target platform (optional, defaults to x86_64 Linux)</param>
    /// <remarks>
    /// The language and mode parameters influence:
    /// <list type="bullet">
    /// <item>Default type inference behavior</item>
    /// <item>Safety checking levels (bounds checking, overflow handling)</item>
    /// <item>Memory management strategy</item>
    /// <item>Generated code optimization level</item>
    /// </list>
    /// </remarks>
    public LLVMCodeGenerator(Language language, LanguageMode mode,
        TargetPlatform? targetPlatform = null, string? stdlibPath = null)
    {
        _language = language;
        _mode = mode;
        _targetPlatform = targetPlatform ?? TargetPlatform.Default();
        _output = new StringBuilder();
        _tempCounter = 0;
        _labelCounter = 0;
        _varCounter = 0;
        _symbolTypes = new Dictionary<string, string>();
        _stackTraceCodeGen = new StackTraceCodeGen(symbolTables: _symbolTables, output: _output);

        // Initialize crash message resolver if stdlib path is available
        if (stdlibPath != null)
        {
            _crashMessageResolver = new CrashMessageResolver(stdlibPath: stdlibPath);
        }
        else
        {
            // Try to find stdlib relative to executable
            string? exeDir = Path.GetDirectoryName(path: System
                                                        .Reflection.Assembly.GetExecutingAssembly()
                                                        .Location);
            if (exeDir != null)
            {
                string defaultStdlibPath = Path.Combine(path1: exeDir, path2: "stdlib");
                if (Directory.Exists(path: defaultStdlibPath))
                {
                    _crashMessageResolver =
                        new CrashMessageResolver(stdlibPath: defaultStdlibPath);
                }
                else
                {
                    // Try parent directories
                    string? parent = Path.GetDirectoryName(path: exeDir);
                    while (parent != null)
                    {
                        defaultStdlibPath = Path.Combine(path1: parent, path2: "stdlib");
                        if (Directory.Exists(path: defaultStdlibPath))
                        {
                            _crashMessageResolver =
                                new CrashMessageResolver(stdlibPath: defaultStdlibPath);
                            break;
                        }

                        parent = Path.GetDirectoryName(path: parent);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Updates the current location tracking from an AST node's location.
    /// This ensures error messages show the correct file and line number.
    /// Should be called at the start of every Visit method.
    /// </summary>
    /// <param name="location">The source location from the AST node being processed</param>
    private void UpdateLocation(SourceLocation location)
    {
        _currentLocation = location;
        _currentFileName = location.FileName;
    }

    /// <summary>
    /// Retrieves the complete generated LLVM IR code as a string.
    /// </summary>
    /// <returns>Complete LLVM IR module including headers, declarations, and function definitions</returns>
    public string GetGeneratedCode()
    {
        return _output.ToString();
    }

    /// <summary>
    /// Generates complete LLVM IR module for the given program AST.
    /// Emits module headers, external declarations, math library support, and processes all program declarations.
    /// </summary>
    /// <param name="program">The root program AST node to generate code for</param>
    /// <remarks>
    /// The generation process follows this structure:
    /// <list type="bullet">
    /// <item><strong>Module Headers</strong>: LLVM module metadata, target information</item>
    /// <item><strong>Imported Declarations</strong>: Standard library functions (printf, malloc, etc.)</item>
    /// <item><strong>Math Library Support</strong>: Precision arithmetic function declarations</item>
    /// <item><strong>String Constants</strong>: Global constants for formatted I/O operations</item>
    /// <item><strong>Program Content</strong>: User-defined functions, classes, and global variables</item>
    /// </list>
    ///
    /// The target configuration assumes x86_64 Linux but can be adapted for other platforms.
    /// </remarks>
    public void Generate(AST.Program program)
    {
        _currentLocation = _currentLocation with { FileName = _currentFileName ?? throw new ArgumentNullException(paramName: nameof(program)) };
        // LLVM IR module headers - provide module identification and target configuration
        _output.AppendLine(value: "; ModuleID = 'razorforge'");
        _output.AppendLine(value: "source_filename = \"razorforge.rf\"");
        _output.AppendLine(value: $"target datalayout = \"{_targetPlatform.DataLayout}\"");
        _output.AppendLine(value: $"target triple = \"{_targetPlatform.TripleString}\"");
        _output.AppendLine();

        // Imported function declarations - C runtime interfaces
        _output.AppendLine(value: "; Imported function declarations");
        _output.AppendLine(value: "declare i32 @printf(i8*, ...)"); // Formatted output
        _output.AppendLine(value: "declare i32 @puts(i8*)"); // Print string with newline
        _output.AppendLine(value: "declare i32 @putchar(i32)"); // Print single character
        _output.AppendLine(value: "declare i32 @scanf(i8*, ...)"); // Formatted input
        _output.AppendLine(value: "declare i8* @fgets(i8*, i32, i8*)"); // Read line
        _output.AppendLine(value: "declare i32 @fflush(i8*)"); // Flush stream
        _output.AppendLine(value: "declare i8* @malloc(i64)"); // Memory allocation
        _output.AppendLine(value: "declare void @free(i8*)"); // Memory deallocation
        _output.AppendLine(value: "declare i64 @strtol(i8*, i8**, i32)"); // String to long
        _output.AppendLine(value: "declare void @exit(i32)"); // Program exit
        _output.AppendLine(
            value: "declare void @rf_runtime_init()"); // Runtime initialization function
        _output.AppendLine(
            value: "declare i8* @__acrt_iob_func(i32)"); // Windows: get stdin/stdout/stderr

        _output.AppendLine();

        // Emit stack trace runtime declarations
        _stackTraceCodeGen?.EmitGlobalDeclarations();

        // Emit external function declarations from imported modules
        EmitExternalDeclarationsFromSymbolTable();

        // String constants for I/O operations and error messages
        _output.AppendLine(value: "; String constants");
        _output.AppendLine(
            value:
            "@.str_int_fmt = private unnamed_addr constant [4 x i8] c\"%d\\0A\\00\", align 1"); // Integer format with newline
        _output.AppendLine(
            value:
            "@.str_fmt = private unnamed_addr constant [3 x i8] c\"%s\\00\", align 1"); // String format
        _output.AppendLine(
            value:
            "@.str_word_fmt = private unnamed_addr constant [6 x i8] c\"%255s\\00\", align 1"); // Word input format
        _output.AppendLine(
            value:
            "@.str_line_fmt = private unnamed_addr constant [9 x i8] c\"%255[^\\0A]\\00\", align 1"); // Line input format (read until newline)
        _output.AppendLine(
            value:
            "@.str_overflow = private unnamed_addr constant [20 x i8] c\"Arithmetic overflow\\00\", align 1"); // Overflow error

        // Generate code for imported module functions first
        GenerateImportedModuleFunctions();

        // Store the current program for function lookups
        _currentProgram = program;

        // Process the program AST to generate function definitions and global declarations
        program.Accept(visitor: this);

        // Emit collected string constants after the predefined constants
        if (_stringConstants != null && _stringConstants.Count > 0)
        {
            string content = _output.ToString();
            // Find the line after @.str_fmt and insert our constants there
            string strFmtLine =
                "@.str_fmt = private unnamed_addr constant [3 x i8] c\"%s\\00\", align 1";
            int insertPos = content.IndexOf(value: strFmtLine);
            if (insertPos >= 0)
            {
                int endOfLine = content.IndexOf(value: '\n', startIndex: insertPos) + 1;
                string before = content.Substring(startIndex: 0, length: endOfLine);
                string after = content.Substring(startIndex: endOfLine);

                _output.Clear();
                _output.Append(value: before);

                // Add our collected string constants
                foreach (string constant in _stringConstants)
                {
                    _output.AppendLine(value: constant);
                }

                _output.Append(value: after);
            }
        }

        // Emit native function declarations collected during code generation
        EmitNativeFunctionDeclarations();

        // Emit symbol tables for stack trace runtime support
        _stackTraceCodeGen?.EmitSymbolTables();

        // Emit symbol table initialization function (uses llvm.global_ctors to run before main)
        _stackTraceCodeGen?.EmitSymbolTableInitFunction();

        _output.AppendLine();
    }

    /// <summary>Generates a unique LLVM IR temporary variable name</summary>
    /// <returns>Unique temporary variable name in LLVM IR format (%tmp0, %tmp1, etc.)</returns>
    private string GetNextTemp()
    {
        return $"%tmp{_tempCounter++}";
    }

    /// <summary>Generates a unique LLVM IR basic block label</summary>
    /// <returns>Unique label name for LLVM IR basic blocks (label0, label1, etc.)</returns>
    private string GetNextLabel()
    {
        return $"label{_labelCounter++}";
    }

    /// <summary>Generates a unique variable name to avoid conflicts in different scopes</summary>
    /// <param name="baseName">The original variable name from source code</param>
    /// <returns>Unique variable name with suffix (e.g., diff_0, diff_1)</returns>
    private string GetUniqueVarName(string baseName)
    {
        return $"{baseName}_{_varCounter++}";
    }

    /// <summary>
    /// Finds a matching generic template for a method call on a generic type instance.
    /// For example, given BackIndex<uaddr> and method offset_uaddr, finds BackIndex<I>.offset_uaddr
    /// </summary>
    /// <param name="instantiatedType">The concrete generic type (e.g., BackIndex<uaddr>)</param>
    /// <param name="methodName">The method being called (e.g., offset_uaddr)</param>
    /// <returns>The template key if found, null otherwise</returns>
    private string? FindMatchingGenericTemplate(string instantiatedType, string methodName)
    {
        // Extract the base type name (e.g., BackIndex from BackIndex<uaddr>)
        int anglePos = instantiatedType.IndexOf('<');
        if (anglePos < 0) return null;

        string baseTypeName = instantiatedType.Substring(0, anglePos);

        // Look for templates that start with BaseType<
        // The template will be something like "BackIndex<I>.offset_uaddr"
        foreach (string templateKey in _genericFunctionTemplates.Keys)
        {
            if (templateKey.StartsWith($"{baseTypeName}<") &&
                templateKey.Contains($".{methodName}"))
            {
                // Found a matching template
                return templateKey;
            }
        }

        return null;
    }

    /// <summary>
    /// Searches loaded modules for a generic method template matching the given instantiated type and method name.
    /// This is used to find methods like Text&lt;T&gt;.to_cstr when called as Text&lt;letter8&gt;.to_cstr
    /// </summary>
    /// <param name="instantiatedType">The concrete generic type (e.g., Text&lt;letter8&gt;)</param>
    /// <param name="methodName">The method being called (e.g., to_cstr)</param>
    /// <returns>The template key if found (e.g., "Text&lt;T&gt;.to_cstr"), null otherwise</returns>
    private string? FindGenericMethodInLoadedModules(string instantiatedType, string methodName)
    {
        if (_loadedModules == null) return null;

        // Extract the base type name (e.g., Text from Text<letter8>)
        int anglePos = instantiatedType.IndexOf('<');
        if (anglePos < 0) return null;

        string baseTypeName = instantiatedType.Substring(0, anglePos);

        // Search through all loaded modules
        foreach (var moduleEntry in _loadedModules.Values)
        {
            foreach (var decl in moduleEntry.Ast.Declarations)
            {
                // Check if this is a function declaration with a generic type
                if (decl is FunctionDeclaration funcDecl)
                {
                    // Look for patterns like "Text<T>.to_cstr" or "List<T>.len"
                    if (funcDecl.Name.StartsWith($"{baseTypeName}<") &&
                        funcDecl.Name.Contains($".{methodName}"))
                    {
                        // Check if this function has any REAL generic parameters
                        // (not just type specializations in the name)
                        int actualGenericParamCount = 0;
                        if (funcDecl.GenericParameters != null && funcDecl.GenericParameters.Count > 0)
                        {
                            // Apply same filtering logic as Visit FunctionDeclaration
                            foreach (string param in funcDecl.GenericParameters)
                            {
                                int dotPos = funcDecl.Name.IndexOf('.');
                                if (dotPos > 0)
                                {
                                    string typePartOfName = funcDecl.Name.Substring(0, dotPos);
                                    if (!typePartOfName.Contains($"<{param}>"))
                                    {
                                        // This is a real type parameter
                                        actualGenericParamCount++;
                                    }
                                }
                                else
                                {
                                    actualGenericParamCount++;
                                }
                            }
                        }

                        // Only treat as generic template if it has actual generic parameters
                        if (actualGenericParamCount > 0)
                        {
                            // Store the template if not already stored
                            if (!_genericFunctionTemplates.ContainsKey(funcDecl.Name))
                            {
                                _genericFunctionTemplates[funcDecl.Name] = funcDecl;
                            }
                            return funcDecl.Name;
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts concrete type arguments from a generic type name.
    /// For example, BackIndex<uaddr> returns ["uaddr"], List<Text<letter8>> returns ["Text<letter8>"]
    /// </summary>
    /// <param name="genericType">The generic type with concrete arguments (e.g., BackIndex<uaddr>)</param>
    /// <returns>List of concrete type argument names</returns>
    private List<string> ExtractTypeArguments(string genericType)
    {
        var typeArgs = new List<string>();

        int start = genericType.IndexOf('<');
        int end = genericType.LastIndexOf('>');

        if (start < 0 || end < 0 || end <= start) return typeArgs;

        // Extract the content between < and >
        string argsStr = genericType.Substring(start + 1, end - start - 1);

        // Split by comma, but handle nested generics carefully
        int nestLevel = 0;
        int argStart = 0;

        for (int i = 0; i < argsStr.Length; i++)
        {
            char c = argsStr[i];
            if (c == '<')
            {
                nestLevel++;
            }
            else if (c == '>')
            {
                nestLevel--;
            }
            else if (c == ',' && nestLevel == 0)
            {
                // Found a top-level comma - this separates type arguments
                string arg = argsStr.Substring(argStart, i - argStart).Trim();
                if (!string.IsNullOrEmpty(arg))
                {
                    typeArgs.Add(arg);
                }
                argStart = i + 1;
            }
        }

        // Add the last argument
        string lastArg = argsStr.Substring(argStart).Trim();
        if (!string.IsNullOrEmpty(lastArg))
        {
            typeArgs.Add(lastArg);
        }

        return typeArgs;
    }

    /// <summary>
    /// Gets the return type of a generic function by looking at the template and substituting type parameters.
    /// For example, BackIndex<I>.resolve returns I, so BackIndex<uaddr>.resolve returns uaddr.
    /// </summary>
    /// <param name="templateKey">The template function key (e.g., BackIndex<I>.resolve)</param>
    /// <param name="concreteTypeArgs">The concrete type arguments (e.g., ["uaddr"])</param>
    /// <returns>The LLVM return type after type substitution</returns>
    private string GetGenericFunctionReturnType(string templateKey, List<string> concreteTypeArgs)
    {
        if (!_genericFunctionTemplates.TryGetValue(templateKey, out FunctionDeclaration? template))
        {
            return "void";
        }

        if (template.ReturnType == null)
        {
            return "void";
        }

        string returnTypeName = template.ReturnType.Name;

        // If the template has generic parameters, substitute them with concrete types
        if (template.GenericParameters != null && template.GenericParameters.Count > 0)
        {
            for (int i = 0; i < template.GenericParameters.Count && i < concreteTypeArgs.Count; i++)
            {
                string genericParam = template.GenericParameters[i];
                string concreteType = concreteTypeArgs[i];
                returnTypeName = returnTypeName.Replace(genericParam, concreteType);
            }
        }

        // Map the RazorForge type to LLVM type
        return MapTypeToLLVM(returnTypeName);
    }

    /// <summary>
    /// Sanitizes a function name to be valid LLVM IR identifier.
    /// Removes or replaces characters that are not allowed in LLVM function names.
    /// </summary>
    /// <param name="name">The original function name</param>
    /// <returns>Sanitized function name safe for LLVM IR</returns>
    /// <remarks>
    /// LLVM function names can contain alphanumeric characters, underscores, dots, and dollar signs.
    /// Special characters like '!' are replaced with descriptive suffixes.
    /// Examples:
    /// - divide! -> divide_throwable
    /// - Console.print -> Console.print (dots are allowed)
    /// </remarks>
    private string SanitizeFunctionName(string name)
    {
        string unprefixedName = name;
        bool isCrashable = name.EndsWith(value: '!');
        bool isAbsentable = name.EndsWith(value: '?');

        if (isCrashable)
        {
            unprefixedName = unprefixedName[..^1];
        }
        else if (isAbsentable)
        {
            unprefixedName = unprefixedName[..^1];
        }

        // Handle type-qualified methods (e.g., "s32.__add__" → "s32.add_dunder")
        bool isDunder = false;
        string typePrefix = "";
        if (unprefixedName.Contains('.'))
        {
            int dotIndex = unprefixedName.LastIndexOf('.');
            typePrefix = unprefixedName.Substring(0, dotIndex + 1); // Keep the dot
            string methodPart = unprefixedName.Substring(dotIndex + 1);

            // Check if method part is a dunder
            if (methodPart.StartsWith("__") && methodPart.EndsWith("__"))
            {
                isDunder = true;
                methodPart = methodPart[2..^2]; // Strip __ from both ends
                unprefixedName = typePrefix + methodPart;
            }
        }
        else
        {
            // Check if the entire name is a dunder (no type prefix)
            isDunder = unprefixedName.StartsWith(value: "__") && unprefixedName.EndsWith(value: "__");
            if (isDunder)
            {
                unprefixedName = unprefixedName[2..^2];
            }
        }

        bool isTypeName = IsTypeName(name: unprefixedName);

        // Sanitize angle brackets in generic types (e.g., Text<letter8> -> Text_letter8)
        if (unprefixedName.Contains(value: '<'))
        {
            unprefixedName = unprefixedName.Replace(oldValue: "<", newValue: "_")
                                           .Replace(oldValue: ">", newValue: "")
                                           .Replace(oldValue: ", ", newValue: "_")
                                           .Replace(oldValue: ",", newValue: "_");
        }

        if (isDunder && !isTypeName)
        {
            unprefixedName = $"{unprefixedName}_dunder";
        }

        if (isDunder && isTypeName)
        {
            unprefixedName = $"{unprefixedName}___create__";
        }

        if (isCrashable)
        {
            unprefixedName = $"{unprefixedName}_throwable";
        }
        else if (isAbsentable)
        {
            unprefixedName = $"{unprefixedName}_absentable";
        }

        return unprefixedName;
    }

    /// <summary>
    /// Checks if a name represents a built-in or known type name.
    /// Used to distinguish type constructor calls from regular throwable functions.
    /// </summary>
    private bool IsTypeName(string name)
    {
        // TODO: actually figure out what types are loaded
        return name is "s8" or "s16" or "s32" or "s64" or "s128" or "u8" or "u16" or "u32" or "u64"
            or "u128" or "f16" or "f32" or "f64" or "f128" or "bool" or "Text" or "letter";
    }

    /// <summary>
    /// Checks if a type is a string/text type for overload resolution.
    /// </summary>
    private bool IsStringType(string typeName)
    {
        // TODO: ValueText, FixedText, FixedTextBuffer, TextBuffer should be added here
        return typeName.StartsWith(value: "Text<") || typeName == "Text";
    }

    /// <summary>
    /// Maps RazorForge type names to their corresponding LLVM IR type representations.
    /// </summary>
    /// <param name="rfType">RazorForge type name (s32, f64, bool, Text, etc.)</param>
    /// <returns>Corresponding LLVM IR type string</returns>
    /// <remarks>
    /// Type mapping priorities:
    /// <list type="bullet">
    /// <item>Unsigned integers use the same LLVM types as signed (signedness tracked separately)</item>
    /// <item>System-dependent types (saddr, uaddr) map to 64-bit on x86_64</item>
    /// <item>Text types map to i8* (null-terminated C strings)</item>
    /// <item>Unknown types default to i8* for maximum compatibility</item>
    /// </list>
    /// </remarks>
    private string MapTypeToLLVM(string rfType)
    {
        // Check for pointer types (e.g., uaddr*, s32*)
        if (rfType.EndsWith("*"))
        {
            // For pointer types, just return "ptr" in opaque pointer mode (LLVM 15+)
            // The base type is only used for type checking at the RazorForge level
            return "ptr";
        }

        // Check for generic type syntax: TypeName<T1, T2, ...>
        if (rfType.Contains(value: '<') && rfType.Contains(value: '>'))
        {
            return MapGenericTypeToLLVM(genericType: rfType);
        }

        return rfType switch
        {
            // LLVM Native types - direct mapping to LLVM primitives (used in stdlib)
            "LlvmNativeI8" => "i8",
            "LlvmNativeI16" => "i16",
            "LlvmNativeI32" => "i32",
            "LlvmNativeI64" => "i64",
            "LlvmNativeI128" => "i128",
            "LlvmNativeF16" => "half",
            "LlvmNativeF32" => "float",
            "LlvmNativeF64" => "double",
            "LlvmNativeF128" => "fp128",
            "LlvmNativePtr" => "ptr",
            "LlvmNativePtrSizedInt" => _targetPlatform.GetPointerSizedIntType(),
            "LlvmNativePtrSizedUInt" => _targetPlatform.GetPointerSizedIntType(), // Same as signed - LLVM doesn't distinguish

            // Choice/Enum types - map to integers
            // TODO: Generate proper choice type definitions and track them
            "DataState" => "i8", // choice with 3 values (VALID, ABSENT, ERROR)
            "DataHandle" => "%DataHandle", // record type

            // Signed integers - map to record wrapper types (enforcing "everything is a record")
            "s8" => "%s8",
            "s16" => "%s16",
            "s32" => "%s32",
            "s64" => "%s64",
            "s128" => "%s128",

            // Unsigned integers - map to record wrapper types
            "u8" => "%u8",
            "u16" => "%u16",
            "u32" => "%u32",
            "u64" => "%u64",
            "u128" => "%u128",

            // System-dependent integers (pointer-sized, architecture-dependent)
            // NOTE: saddr and uaddr are record types in stdlib, not primitives
            "saddr" or "iptr" => "%saddr",
            "uaddr" or "uptr" => "%uaddr",

            // IEEE 754 floating point types - map to record wrapper types
            "f16" => "%f16", // 16-bit half precision
            "f32" => "%f32", // 32-bit single precision
            "f64" => "%f64", // 64-bit double precision
            "f128" => "%f128", // 128-bit quad precision

            // Boolean type - map to record wrapper type
            "bool" => "%bool", // Boolean wrapper record

            // Void/Blank type
            "void" or "Blank" => "void", // No return value

            // Character types (RazorForge letter types)
            // Note: letter8/16/32 are records in stdlib with methods, not primitives.
            // They fall through to MapUnknownTypeToLLVM which returns %letter8 etc.
            // For primitive character values without methods, use u8/u16/u32 directly.

            // Text/String types - these are entities, not C strings!
            // Text<T> requires a type parameter and is handled by MapGenericTypeToLLVM
            // Plain "Text" defaults to Text<letter32> for convenience
            // TODO: It should be suflae only, razorforge should not do this.
            // Text is a pointer type (reference to heap-allocated string data)
            "text" or "Text" => "ptr",

            // C FFI types - String type (record but commonly used)
            "cstr" => "%cstr", // C string wrapper record { ptr: uaddr }

            // C FFI types - Character types
            "cchar" or "cschar" => "i8", // char, signed char
            "cuchar" => "i8", // unsigned char (same LLVM type, different signedness)
            "cwchar" => _targetPlatform
               .GetWCharType(), // wchar_t (varies by OS: 32-bit on Unix/Linux, 16-bit on Windows)

            // C FFI types - Numeric types
            "cshort" => "i16", // short
            "cushort" => "i16", // unsigned short
            "cint" => "i32", // int
            "cuint" => "i32", // unsigned int
            "clong" => _targetPlatform
               .GetLongType(), // long (varies by OS: 64-bit on Unix x86_64, 32-bit on Windows x86_64)
            "culong" => _targetPlatform
               .GetLongType(), // unsigned long (varies by OS: 64-bit on Unix x86_64, 32-bit on Windows x86_64)
            "cll" => "i64", // long long
            "cull" => "i64", // unsigned long long
            "cfloat" => "float", // float
            "cdouble" => "double", // double

            // C FFI types - Pointer-sized integers (architecture-dependent)
            "csptr" => _targetPlatform
               .GetPointerSizedIntType(), // intptr_t (varies by architecture)
            "cuptr" => _targetPlatform
               .GetPointerSizedIntType(), // uintptr_t (varies by architecture)

            // C FFI types - Special types
            "cvoid" => _targetPlatform
               .GetPointerSizedIntType(), // void (represented as uaddr in RazorForge)
            "cbool" => "i1", // C bool (_Bool)

            _ => MapUnknownTypeToLLVM(
                rfType: rfType) // Check for record types or default to pointer
        };
    }

    /// <summary>
    /// Maps an unknown type to LLVM, checking if it's a registered record type first.
    /// </summary>
    private string MapUnknownTypeToLLVM(string rfType)
    {
        // Handle LLVM primitive type markers (e.g., LlvmI1, LlvmNativeI64, LlvmNativePtrSizedUInt)
        // These are used in stdlib record definitions to indicate the underlying LLVM type
        if (rfType.StartsWith("Llvm"))
        {
            return rfType switch
            {
                "LlvmI1" => "i1",
                "LlvmNativeI8" => "i8",
                "LlvmNativeI16" => "i16",
                "LlvmNativeI32" => "i32",
                "LlvmNativeI64" => "i64",
                "LlvmNativeI128" => "i128",
                "LlvmNativePtrSizedInt" => _targetPlatform.GetPointerSizedIntType(),
                "LlvmNativePtrSizedUInt" => _targetPlatform.GetPointerSizedIntType(),
                "LlvmNativeHalf" => "half",
                "LlvmNativeFloat" => "float",
                "LlvmNativeDouble" => "double",
                "LlvmNativeFp128" => "fp128",
                "LlvmPtr" => "ptr",
                _ => throw CodeGenError.UnknownType(
                    typeName: rfType,
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position)
            };
        }

        // Check if this is a registered record type
        if (_recordFields.ContainsKey(key: rfType))
        {
            return $"%{rfType}"; // Return the struct type reference
        }

        // Check if it's a registered entity type or already emitted
        if (_genericEntityTemplates.ContainsKey(key: rfType) ||
            _emittedTypes.Contains(item: rfType))
        {
            return $"%{rfType}"; // Return the struct type reference
        }

        // Check if this is a generic record/entity template used without type arguments
        // This is a programming error - generic types require concrete type arguments
        if (_genericRecordTemplates.ContainsKey(key: rfType))
        {
            throw CodeGenError.GenericTypeRequiresArguments(typeName: rfType,
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        if (_genericEntityTemplates.ContainsKey(key: rfType))
        {
            throw CodeGenError.GenericTypeRequiresArguments(typeName: rfType,
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Try to find record in loaded modules before giving up
        if (_loadedModules != null)
        {
            foreach (var (_, moduleInfo) in _loadedModules)
            {
                foreach (IAstNode decl in moduleInfo.Ast.Declarations)
                {
                    if (decl is RecordDeclaration recordDecl && recordDecl.Name == rfType)
                    {
                        // Found the record - generate it and return the type
                        VisitRecordDeclaration(recordDecl);
                        return $"%{rfType}";
                    }
                    if (decl is EntityDeclaration entityDecl && entityDecl.Name == rfType)
                    {
                        // Found the entity - generate it and return the type
                        VisitEntityDeclaration(entityDecl);
                        return $"%{rfType}";
                    }
                }
            }
        }

        // Try to find a .rf file with the same name in the current file's directory
        // This handles cases like letter8.rf referencing letter16 (sibling modules)
        if (!string.IsNullOrEmpty(_currentFileName))
        {
            string? currentDir = Path.GetDirectoryName(_currentFileName);
            if (currentDir != null)
            {
                string potentialFile = Path.Combine(currentDir, rfType + ".rf");
                if (File.Exists(potentialFile))
                {
                    if (TryParseAndLoadRecordFromFile(potentialFile, rfType))
                    {
                        return $"%{rfType}";
                    }
                }
            }
        }

        // Unknown type - throw proper compile error instead of silently generating wrong code
        throw CodeGenError.UnknownType(typeName: rfType,
            file: _currentFileName,
            line: _currentLocation.Line,
            column: _currentLocation.Column,
            position: _currentLocation.Position);
    }

    /// <summary>
    /// Builds the full type name from a TypeExpression, including generic arguments.
    /// Examples:
    /// - TypeExpression { Name: "s64" } → "s64"
    /// - TypeExpression { Name: "TestType", GenericArguments: ["s64"] } → "TestType&lt;s64&gt;"
    /// - TypeExpression { Name: "List", GenericArguments: ["List&lt;s32&gt;"] } → "List&lt;List&lt;s32&gt;&gt;"
    /// </summary>
    private string BuildFullTypeName(TypeExpression typeExpr)
    {
        if (typeExpr.GenericArguments == null || typeExpr.GenericArguments.Count == 0)
        {
            return typeExpr.Name;
        }

        // Recursively build generic argument strings
        var argNames = new List<string>();
        foreach (var arg in typeExpr.GenericArguments)
        {
            argNames.Add(BuildFullTypeName(arg));
        }

        return $"{typeExpr.Name}<{string.Join(", ", argNames)}>";
    }

    /// <summary>
    /// Maps a generic type like List&lt;s32&gt; to its monomorphized LLVM type.
    /// Parses the generic syntax, instantiates the type if needed, and returns the LLVM type.
    /// </summary>
    private string MapGenericTypeToLLVM(string genericType)
    {
        // Parse the generic type: TypeName<T1, T2, ...>
        int openBracket = genericType.IndexOf(value: '<');
        int closeBracket = genericType.LastIndexOf(value: '>');

        if (openBracket < 0 || closeBracket < 0 || closeBracket <= openBracket)
        {
            throw new InvalidOperationException($"Invalid generic type format: '{genericType}'");
        }

        string baseName = genericType.Substring(startIndex: 0, length: openBracket);
        string typeArgsStr = genericType.Substring(startIndex: openBracket + 1,
            length: closeBracket - openBracket - 1);

        // Handle special generic types that map directly to LLVM types
        // Snatched<T> is a raw pointer type for C FFI
        if (baseName == "Snatched")
        {
            // Snatched<T> maps to a pointer to T's LLVM type
            // For most cases, this is i8* (especially for Snatched<letter8>)
            List<string> typeArgs = ParseTypeArguments(typeArgsStr: typeArgsStr);
            if (typeArgs.Count <= 0)
            {
                throw new InvalidOperationException(
                    $"Snatched<> requires a type argument: '{genericType}'");
            }

            string innerType = MapTypeToLLVM(rfType: typeArgs[index: 0]);
            // If the inner type is already a pointer, return it as-is
            if (innerType.EndsWith(value: "*"))
            {
                return innerType;
            }

            // Otherwise return a pointer to the inner type
            return $"{innerType}*";

        }

        // Split type arguments (handle nested generics carefully)
        List<string> typeArguments = ParseTypeArguments(typeArgsStr: typeArgsStr);

        // Try to instantiate as a generic record or entity
        string mangledName;
        if (_genericRecordTemplates.ContainsKey(key: baseName))
        {
            mangledName =
                InstantiateGenericRecord(recordName: baseName, typeArguments: typeArguments);
        }
        else if (_genericEntityTemplates.ContainsKey(key: baseName))
        {
            mangledName =
                InstantiateGenericEntity(entityName: baseName, typeArguments: typeArguments);
        }
        else
        {
            // Unknown generic type - crash instead of silently generating wrong code
            throw CodeGenError.TypeResolutionFailed(
                typeName: genericType,
                context: $"unknown generic type '{baseName}' - not a registered generic record or entity",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Return as a pointer to the monomorphized struct
        return $"%{mangledName}*";
    }

    /// <summary>
    /// Parses type arguments from a comma-separated string, handling nested generics.
    /// For example: "s32, List&lt;u8&gt;" -> ["s32", "List&lt;u8&gt;"]
    /// </summary>
    private List<string> ParseTypeArguments(string typeArgsStr)
    {
        var result = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < typeArgsStr.Length; i++)
        {
            char c = typeArgsStr[index: i];
            switch (c)
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                {
                    string arg = typeArgsStr.Substring(startIndex: start, length: i - start)
                                            .Trim();
                    if (!string.IsNullOrEmpty(value: arg))
                    {
                        result.Add(item: arg);
                    }

                    start = i + 1;
                    break;
                }
            }
        }

        // Don't forget the last argument
        string lastArg = typeArgsStr[start..]
           .Trim();
        if (!string.IsNullOrEmpty(value: lastArg))
        {
            result.Add(item: lastArg);
        }

        return result;
    }

    /// <summary>
    /// Gets a named argument from argument list.
    /// </summary>
    private Expression? GetNamedArgument(List<Expression> arguments, string name)
    {
        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression namedArg && namedArg.Name == name)
            {
                return namedArg.Value;
            }
        }

        return null;
    }

    /// <summary>
    /// Ensures a native function is declared before it's called.
    /// Collects declarations to be emitted later at the module level.
    /// </summary>
    private void EnsureNativeFunctionDeclared(string functionName, string returnType, List<string> argTypes)
    {
        // Skip if already declared
        if (_declaredNativeFunctions.Contains(functionName))
        {
            return;
        }

        // Mark as declared
        _declaredNativeFunctions.Add(functionName);

        // Build the declaration and collect it
        string argsStr = string.Join(", ", argTypes);
        string declaration = returnType == "void"
            ? $"declare void @{functionName}({argsStr})"
            : $"declare {returnType} @{functionName}({argsStr})";

        _nativeFunctionDeclarations.Add(declaration);
    }

    /// <summary>
    /// Emits all collected native function declarations.
    /// Call this after processing the program but before emitting symbol tables.
    /// </summary>
    private void EmitNativeFunctionDeclarations()
    {
        if (_nativeFunctionDeclarations.Count > 0)
        {
            _output.AppendLine();
            _output.AppendLine("; Native function declarations (auto-generated)");
            foreach (string declaration in _nativeFunctionDeclarations)
            {
                _output.AppendLine(declaration);
            }
        }
    }
}
