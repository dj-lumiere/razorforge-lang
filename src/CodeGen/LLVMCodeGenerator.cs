using System.Numerics;
using System.Text;
using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using Compilers.Shared.Lexer;

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

    private readonly StringBuilder _output;
    private readonly Language _language;
    private readonly LanguageMode _mode;
    private readonly TargetPlatform _targetPlatform;
    private int _tempCounter;
    private int _labelCounter;
    private readonly Dictionary<string, string> _symbolTypes;

    private readonly Dictionary<string, string>
        _symbolRfTypes = new(); // Track RazorForge types for symbols

    private readonly HashSet<string>
        _functionParameters = new(); // Track function parameters (no load needed)

    private readonly Dictionary<string, TypeInfo>
        _tempTypes = new(); // Track types of temporary variables

    private bool _hasReturn = false;
    private bool _blockTerminated = false; // Track if current block is terminated (ret/br)
    private List<string>? _stringConstants; // Collect string constants for proper emission

    // Generic instantiation tracking for monomorphization
    private readonly Dictionary<string, List<List<Analysis.TypeInfo>>> _genericInstantiations =
        new();

    private readonly Dictionary<string, FunctionDeclaration> _genericFunctionTemplates = new();
    private readonly List<string> _pendingGenericInstantiations = new();

    // Generic type (record/entity) templates for monomorphization
    private readonly Dictionary<string, StructDeclaration> _genericRecordTemplates = new();
    private readonly Dictionary<string, ClassDeclaration> _genericEntityTemplates = new();
    private readonly Dictionary<string, List<List<string>>> _genericTypeInstantiations = new();
    private readonly List<string> _pendingRecordInstantiations = new();
    private readonly List<string> _pendingEntityInstantiations = new();

    private readonly HashSet<string>
        _emittedTypes = new(); // Track already emitted type definitions

    // Non-generic record type tracking for constructor detection
    // Maps record name to its field declarations (name, type)
    private readonly Dictionary<string, List<(string Name, string Type)>> _recordFields = new();

    // CompilerService intrinsic tracking
    private string? _currentFileName;
    private string? _currentFunctionName;

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
    /// <param name="mode">Language mode (Normal/Danger for RazorForge, Sweet/Bitter for Suflae)</param>
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
    /// <item><strong>External Declarations</strong>: Standard library functions (printf, malloc, etc.)</item>
    /// <item><strong>Math Library Support</strong>: Precision arithmetic function declarations</item>
    /// <item><strong>String Constants</strong>: Global constants for formatted I/O operations</item>
    /// <item><strong>Program Content</strong>: User-defined functions, classes, and global variables</item>
    /// </list>
    ///
    /// The target configuration assumes x86_64 Linux but can be adapted for other platforms.
    /// </remarks>
    public void Generate(AST.Program program)
    {
        // LLVM IR module headers - provide module identification and target configuration
        _output.AppendLine(value: "; ModuleID = 'razorforge'");
        _output.AppendLine(value: "source_filename = \"razorforge.rf\"");
        _output.AppendLine(value: $"target datalayout = \"{_targetPlatform.DataLayout}\"");
        _output.AppendLine(value: $"target triple = \"{_targetPlatform.TripleString}\"");
        _output.AppendLine();

        // External function declarations - C runtime interfaces
        _output.AppendLine(value: "; External function declarations");
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

        // Emit external function declarations from imported modules
        EmitExternalDeclarationsFromSymbolTable();

        _output.AppendLine();

        // Stack trace runtime support declarations
        _stackTraceCodeGen?.EmitGlobalDeclarations();

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
        // Replace ! with _throwable suffix to indicate error-handling functions
        if (name.EndsWith(value: '!'))
        {
            string baseName = name[..^1];

            // Check if this is a type constructor call (e.g., s32!, u64!, Text!)
            // Type constructors map to typename.__create__! -> typename___create___throwable
            if (IsTypeName(name: baseName))
            {
                return baseName + "___create___throwable";
            }

            // Regular throwable function (e.g., divide! -> divide_throwable)
            return baseName + "_throwable";
        }

        // Replace ? with _try suffix for safe/maybe-returning functions
        if (name.EndsWith(value: '?'))
        {
            string baseName = name[..^1];

            // Check if this is a type constructor call (e.g., s32?, u64?, Text?)
            // Safe type constructors map to try_typename.__create__ -> try_typename___create__
            if (IsTypeName(name: baseName))
            {
                return "try_" + baseName + "___create__";
            }

            // Regular safe function (e.g., find? -> find_try or try_find)
            return baseName + "_try";
        }

        // Other special characters can be added here as needed
        return name;
    }

    /// <summary>
    /// Checks if a name represents a built-in or known type name.
    /// Used to distinguish type constructor calls from regular throwable functions.
    /// </summary>
    private bool IsTypeName(string name)
    {
        // Built-in numeric types
        return name is "s8" or "s16" or "s32" or "s64" or "s128" or "u8" or "u16" or "u32" or "u64"
            or "u128" or "f16" or "f32" or "f64" or "f128" or "bool" or "Text" or "letter";
    }

    /// <summary>
    /// Checks if a type is a string/text type for overload resolution.
    /// </summary>
    private bool IsStringType(string typeName)
    {
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
        // Check for generic type syntax: TypeName<T1, T2, ...>
        if (rfType.Contains(value: '<') && rfType.Contains(value: '>'))
        {
            return MapGenericTypeToLLVM(genericType: rfType);
        }

        return rfType switch
        {
            // Signed integers - direct mapping to LLVM integer types
            "s8" => "i8",
            "s16" => "i16",
            "s32" => "i32",
            "s64" => "i64",
            "s128" => "i128",

            // Unsigned integers - use same LLVM type, track signedness separately
            "u8" => "i8",
            "u16" => "i16",
            "u32" => "i32",
            "u64" => "i64",
            "u128" => "i128",

            // System-dependent integers (pointer-sized, architecture-dependent)
            "saddr" or "iptr" => _targetPlatform
               .GetPointerSizedIntType(), // signed pointer-sized - varies by architecture
            "uaddr" or "uptr" => _targetPlatform
               .GetPointerSizedIntType(), // unsigned pointer-sized - varies by architecture

            // IEEE 754 floating point types
            "f16" => "half", // 16-bit half precision
            "f32" => "float", // 32-bit single precision
            "f64" => "double", // 64-bit double precision
            "f128" => "fp128", // 128-bit quad precision

            // Boolean type
            "bool" => "i1", // Single bit boolean

            // Character types (RazorForge letter types)
            "letter8" => "i8", // 8-bit UTF-8 code unit
            "letter16" => "i16", // 16-bit UTF-16 code unit
            "letter32" or "letter" => "i32", // 32-bit Unicode codepoint (default letter type)

            // Text/String types
            "text" => "i8*", // Null-terminated C string
            "Text" => "i8*", // Alternative capitalization

            // C FFI types - Character types
            "cchar" or "cschar" => "i8", // char, signed char
            "cuchar" => "i8", // unsigned char (same LLVM type, different signedness)
            "cwchar" => _targetPlatform
               .GetWCharType(), // wchar_t (varies by OS: 32-bit on Unix/Linux, 16-bit on Windows)
            "cchar8" => "i8", // char8_t
            "cchar16" => "i16", // char16_t
            "cchar32" => "i32", // char32_t

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

            _ => "i8*" // Default to pointer for unknown types (including cptr<T>)
        };
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
            return "i8*"; // Invalid format, fallback
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
            if (typeArgs.Count > 0)
            {
                string innerType = MapTypeToLLVM(rfType: typeArgs[index: 0]);
                // If the inner type is already a pointer, return it as-is
                if (innerType.EndsWith(value: "*"))
                {
                    return innerType;
                }

                // Otherwise return a pointer to the inner type
                return $"{innerType}*";
            }

            return "i8*"; // Default to generic pointer
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
            // Unknown generic type - just create a mangled name
            mangledName = $"{baseName}_{string.Join(separator: "_", values: typeArguments)}";
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
            if (c == '<')
            {
                depth++;
            }
            else if (c == '>')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                string arg = typeArgsStr.Substring(startIndex: start, length: i - start)
                                        .Trim();
                if (!string.IsNullOrEmpty(value: arg))
                {
                    result.Add(item: arg);
                }

                start = i + 1;
            }
        }

        // Don't forget the last argument
        string lastArg = typeArgsStr.Substring(startIndex: start)
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
}
