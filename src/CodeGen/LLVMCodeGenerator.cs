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
public class LLVMCodeGenerator : IAstVisitor<string>
{
    private readonly StringBuilder _output;
    private readonly Language _language;
    private readonly LanguageMode _mode;
    private readonly TargetPlatform _targetPlatform;
    private int _tempCounter;
    private int _labelCounter;
    private readonly Dictionary<string, string> _symbolTypes;
    private readonly HashSet<string> _functionParameters = new(); // Track function parameters (no load needed)

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
    private readonly HashSet<string> _emittedTypes = new(); // Track already emitted type definitions

    // CompilerService intrinsic tracking
    private string? _currentFileName;
    private string? _currentFunctionName;

    // Lambda code generation tracking
    private int _lambdaCounter;
    private List<string> _pendingLambdaDefinitions = new();

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
        TargetPlatform? targetPlatform = null)
    {
        _language = language;
        _mode = mode;
        _targetPlatform = targetPlatform ?? TargetPlatform.Default();
        _output = new StringBuilder();
        _tempCounter = 0;
        _labelCounter = 0;
        _symbolTypes = new Dictionary<string, string>();
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
        _output.AppendLine(value: "declare i8* @__acrt_iob_func(i32)"); // Windows: get stdin/stdout/stderr
        _output.AppendLine();

        // Mathematical library function declarations - precision arithmetic support
        _output.AppendLine(value: MathLibrarySupport.GenerateDeclarations());

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
        if (name.EndsWith('!'))
        {
            string baseName = name[..^1];

            // Check if this is a type constructor call (e.g., s32!, u64!, Text!)
            // Type constructors map to typename.__create__! -> typename___create___throwable
            if (IsTypeName(baseName))
            {
                return baseName + "___create___throwable";
            }

            // Regular throwable function (e.g., divide! -> divide_throwable)
            return baseName + "_throwable";
        }

        // Replace ? with _try suffix for safe/maybe-returning functions
        if (name.EndsWith('?'))
        {
            string baseName = name[..^1];

            // Check if this is a type constructor call (e.g., s32?, u64?, Text?)
            // Safe type constructors map to try_typename.__create__ -> try_typename___create__
            if (IsTypeName(baseName))
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
        return name is "s8" or "s16" or "s32" or "s64" or "s128" or
                       "u8" or "u16" or "u32" or "u64" or "u128" or
                       "f16" or "f32" or "f64" or "f128" or
                       "bool" or "Text" or "letter";
    }

    /// <summary>
    /// Generates a mangled function name for a generic function instantiation.
    /// Creates unique names by appending type arguments to the base function name.
    /// </summary>
    /// <param name="baseName">The original function name</param>
    /// <param name="typeArgs">List of concrete type arguments for this instantiation</param>
    /// <returns>Mangled function name (e.g., "swap_s32" for swap&lt;s32&gt;)</returns>
    /// <remarks>
    /// Examples:
    /// - swap&lt;T&gt; with T=s32 -> swap_s32
    /// - map&lt;K,V&gt; with K=text,V=s32 -> map_text_s32
    /// - container&lt;Array&lt;s32&gt;&gt; -> container_Array_s32
    /// </remarks>
    private string MonomorphizeFunctionName(string baseName, List<Analysis.TypeInfo> typeArgs)
    {
        if (typeArgs == null || typeArgs.Count == 0)
        {
            return baseName;
        }

        string suffix = string.Join(separator: "_", values: typeArgs.Select(selector: t => t.Name
           .Replace(oldValue: "[", newValue: "_")
           .Replace(oldValue: "]", newValue: "")
           .Replace(oldValue: ",", newValue: "_")
           .Replace(oldValue: " ", newValue: "")));
        return $"{baseName}_{suffix}";
    }

    /// <summary>
    /// Checks if a generic function has already been instantiated with the given type arguments.
    /// </summary>
    /// <param name="functionName">The base function name</param>
    /// <param name="typeArgs">Type arguments to check</param>
    /// <returns>True if this instantiation already exists, false otherwise</returns>
    private bool IsAlreadyInstantiated(string functionName, List<Analysis.TypeInfo> typeArgs)
    {
        if (!_genericInstantiations.TryGetValue(functionName, out List<List<Analysis.TypeInfo>>? existingInstantiations))
        {
            return false;
        }

        foreach (List<Analysis.TypeInfo> existing in existingInstantiations)
        {
            if (existing.Count != typeArgs.Count)
            {
                continue;
            }

            bool matches = true;
            for (int i = 0; i < existing.Count; i++)
            {
                if (existing[index: i].Name != typeArgs[index: i].Name)
                {
                    matches = false;
                    break;
                }
            }

            if (matches)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tracks a new generic function instantiation to avoid generating duplicates.
    /// </summary>
    /// <param name="functionName">The base function name</param>
    /// <param name="typeArgs">Type arguments for this instantiation</param>
    private void TrackInstantiation(string functionName, List<Analysis.TypeInfo> typeArgs)
    {
        if (!_genericInstantiations.ContainsKey(key: functionName))
        {
            _genericInstantiations[key: functionName] = new List<List<Analysis.TypeInfo>>();
        }

        _genericInstantiations[key: functionName]
           .Add(item: [..typeArgs]);
    }

    /// <summary>
    /// Maps RazorForge type names to their corresponding LLVM IR type representations.
    /// Handles both primitive types and specialized mathematical library types.
    /// </summary>
    /// <param name="rfType">RazorForge type name (s32, f64, bool, Text, etc.)</param>
    /// <returns>Corresponding LLVM IR type string</returns>
    /// <remarks>
    /// Type mapping priorities:
    /// <list type="bullet">
    /// <item>Math library types (d32, d64, d128, bigint, decimal) are handled first</item>
    /// <item>Unsigned integers use the same LLVM types as signed (signedness tracked separately)</item>
    /// <item>System-dependent types (saddr, uaddr) map to 64-bit on x86_64</item>
    /// <item>Text types map to i8* (null-terminated C strings)</item>
    /// <item>Unknown types default to i8* for maximum compatibility</item>
    /// </list>
    /// </remarks>
    private string MapTypeToLLVM(string rfType)
    {
        // Check for math library types first (d32, d64, d128, bigint, decimal)
        if (MathLibrarySupport.IsMathLibraryType(type: rfType))
        {
            return MathLibrarySupport.MapMathTypeToLLVM(rfType: rfType);
        }

        // Check for generic type syntax: TypeName<T1, T2, ...>
        if (rfType.Contains('<') && rfType.Contains('>'))
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
        int openBracket = genericType.IndexOf('<');
        int closeBracket = genericType.LastIndexOf('>');

        if (openBracket < 0 || closeBracket < 0 || closeBracket <= openBracket)
        {
            return "i8*"; // Invalid format, fallback
        }

        string baseName = genericType.Substring(0, openBracket);
        string typeArgsStr = genericType.Substring(openBracket + 1, closeBracket - openBracket - 1);

        // Split type arguments (handle nested generics carefully)
        var typeArguments = ParseTypeArguments(typeArgsStr);

        // Try to instantiate as a generic record or entity
        string mangledName;
        if (_genericRecordTemplates.ContainsKey(baseName))
        {
            mangledName = InstantiateGenericRecord(baseName, typeArguments);
        }
        else if (_genericEntityTemplates.ContainsKey(baseName))
        {
            mangledName = InstantiateGenericEntity(baseName, typeArguments);
        }
        else
        {
            // Unknown generic type - just create a mangled name
            mangledName = $"{baseName}_{string.Join("_", typeArguments)}";
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
            char c = typeArgsStr[i];
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
                string arg = typeArgsStr.Substring(start, i - start).Trim();
                if (!string.IsNullOrEmpty(arg))
                {
                    result.Add(arg);
                }
                start = i + 1;
            }
        }

        // Don't forget the last argument
        string lastArg = typeArgsStr.Substring(start).Trim();
        if (!string.IsNullOrEmpty(lastArg))
        {
            result.Add(lastArg);
        }

        return result;
    }

    /// <summary>
    /// Visits the program node in the Abstract Syntax Tree (AST), processes its declarations,
    /// and generates the corresponding LLVM IR while handling any pending generic function instantiations.
    /// </summary>
    /// <param name="node">The program node representing the entry point of the AST, containing a collection of declarations.</param>
    /// <returns>A string representing the generated LLVM IR code for the program.</returns>
    public string VisitProgram(AST.Program node)
    {
        foreach (IAstNode decl in node.Declarations)
        {
            decl.Accept(visitor: this);
        }

        // Generate all pending generic type instantiations at the end
        GeneratePendingTypeInstantiations();

        // Generate all pending generic function instantiations at the end
        GeneratePendingInstantiations();

        // Emit all pending lambda function definitions
        EmitPendingLambdaDefinitions();

        return "";
    }

    // Function declaration
    public string VisitFunctionDeclaration(FunctionDeclaration node)
    {
        // Check if this is a generic function (has type parameters)
        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // Store the template for later instantiation - don't generate code yet
            string templateKey = node.Name;
            _genericFunctionTemplates[templateKey] = node;
            _output.AppendLine($"; Generic function template: {node.Name}<{string.Join(", ", node.GenericParameters)}>");
            return "";
        }

        // Non-generic function - generate code normally
        return GenerateFunctionCode(node, typeSubstitutions: null);
    }

    /// <summary>
    /// Generates LLVM IR code for a function, optionally applying type substitutions for generic instantiation.
    /// </summary>
    /// <param name="node">The function declaration</param>
    /// <param name="typeSubstitutions">Map from type parameter names to concrete types (null for non-generic)</param>
    /// <param name="mangledName">Optional mangled name for generic instantiations</param>
    /// <returns>Empty string (output written to _output)</returns>
    private string GenerateFunctionCode(FunctionDeclaration node,
        Dictionary<string, string>? typeSubstitutions,
        string? mangledName = null)
    {
        string returnType = node.ReturnType != null
            ? MapTypeWithSubstitution(node.ReturnType.Name, typeSubstitutions)
            : "void";

        // Set the current function return type for return statement processing
        _currentFunctionReturnType = returnType;

        // Track current function name for CompilerService intrinsics
        _currentFunctionName = mangledName ?? node.Name;

        // Clear parameter tracking for this function
        _functionParameters.Clear();

        var parameters = new List<string>();

        if (node.Parameters != null)
        {
            foreach (Parameter param in node.Parameters)
            {
                string paramType = param.Type != null
                    ? MapTypeWithSubstitution(param.Type.Name, typeSubstitutions)
                    : "i32";
                parameters.Add(item: $"{paramType} %{param.Name}");
                _symbolTypes[key: param.Name] = paramType;
                _functionParameters.Add(item: param.Name); // Mark as parameter
            }
        }

        string paramList = string.Join(separator: ", ", values: parameters);

        string functionName = mangledName ?? SanitizeFunctionName(node.Name);
        _output.AppendLine(handler: $"define {returnType} @{functionName}({paramList}) {{");
        _output.AppendLine(value: "entry:");

        // Reset return flag for this function
        _hasReturn = false;

        // Store type substitutions for use in body generation
        _currentTypeSubstitutions = typeSubstitutions;

        // Visit function body
        if (node.Body != null)
        {
            node.Body.Accept(visitor: this);
        }

        // Clear type substitutions
        _currentTypeSubstitutions = null;

        // Add default return if needed (only if no explicit return was generated)
        if (!_hasReturn)
        {
            if (returnType == "void")
            {
                _output.AppendLine(value: "  ret void");
            }
            else if (returnType == "i32")
            {
                _output.AppendLine(value: "  ret i32 0");
            }
        }

        _output.AppendLine(value: "}");
        _output.AppendLine();

        return "";
    }

    /// <summary>
    /// Maps a type name to LLVM type, applying type substitutions for generic instantiation.
    /// </summary>
    private string MapTypeWithSubstitution(string typeName, Dictionary<string, string>? substitutions)
    {
        // Check if this is a type parameter that needs substitution
        if (substitutions != null && substitutions.TryGetValue(typeName, out string? concreteType))
        {
            return MapTypeToLLVM(concreteType);
        }
        return MapTypeToLLVM(typeName);
    }

    // Current type substitutions for generic function body generation
    private Dictionary<string, string>? _currentTypeSubstitutions;

    /// <summary>
    /// Instantiates a generic function with concrete type arguments.
    /// The actual code generation is deferred until GeneratePendingInstantiations is called.
    /// </summary>
    /// <param name="functionName">Base name of the generic function</param>
    /// <param name="typeArguments">Concrete type arguments (e.g., ["s32", "u64"])</param>
    /// <returns>The mangled name of the instantiated function</returns>
    public string InstantiateGenericFunction(string functionName, List<string> typeArguments)
    {
        // Check if we have the template
        if (!_genericFunctionTemplates.TryGetValue(functionName, out FunctionDeclaration? template))
        {
            // No template found - might be an external generic or error
            return functionName;
        }

        // Check if already instantiated
        var typeInfos = typeArguments.Select(t => new Analysis.TypeInfo(Name: t, IsReference: false)).ToList();
        string mangledName = MonomorphizeFunctionName(functionName, typeInfos);

        if (IsAlreadyInstantiated(functionName, typeInfos))
        {
            return mangledName;
        }

        // Track this instantiation and queue for later generation
        TrackInstantiation(functionName, typeInfos);

        // Queue the instantiation data for later code generation
        _pendingGenericInstantiations.Add($"{functionName}|{string.Join(",", typeArguments)}");

        return mangledName;
    }

    /// <summary>
    /// Generates code for all pending generic function instantiations.
    /// Should be called after all program code is generated.
    /// </summary>
    private void GeneratePendingInstantiations()
    {
        // Process all pending instantiations
        foreach (string pending in _pendingGenericInstantiations)
        {
            string[] parts = pending.Split('|');
            string functionName = parts[0];
            List<string> typeArguments = parts[1].Split(',').ToList();

            if (!_genericFunctionTemplates.TryGetValue(functionName, out FunctionDeclaration? template))
            {
                continue;
            }

            // Create type substitution map
            var substitutions = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(template.GenericParameters!.Count, typeArguments.Count); i++)
            {
                substitutions[template.GenericParameters[i]] = typeArguments[i];
            }

            // Generate the mangled name
            var typeInfos = typeArguments.Select(t => new Analysis.TypeInfo(Name: t, IsReference: false)).ToList();
            string mangledName = MonomorphizeFunctionName(functionName, typeInfos);

            // Generate the instantiated function code
            GenerateFunctionCode(template, substitutions, mangledName);
        }

        _pendingGenericInstantiations.Clear();
    }

    /// <summary>
    /// Emits all pending lambda function definitions.
    /// Should be called after all program code is generated.
    /// </summary>
    private void EmitPendingLambdaDefinitions()
    {
        if (_pendingLambdaDefinitions.Count == 0)
        {
            return;
        }

        _output.AppendLine();
        _output.AppendLine("; Lambda function definitions");

        foreach (string lambdaDefinition in _pendingLambdaDefinitions)
        {
            _output.Append(lambdaDefinition);
        }

        _pendingLambdaDefinitions.Clear();
    }

    // Variable declaration
    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        string type = node.Type != null
            ? MapTypeToLLVM(rfType: node.Type.Name)
            : "i32";
        string varName = $"%{node.Name}";

        _symbolTypes[key: node.Name] = type;

        if (node.Initializer != null)
        {
            string initValue = node.Initializer.Accept(visitor: this);
            _output.AppendLine(handler: $"  {varName} = alloca {type}");
            _output.AppendLine(handler: $"  store {type} {initValue}, {type}* {varName}");
        }
        else
        {
            _output.AppendLine(handler: $"  {varName} = alloca {type}");
        }

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for binary expressions with comprehensive operator and type support.
    /// Handles arithmetic, comparison, logical, and bitwise operations with proper type management.
    /// </summary>
    /// <param name="node">Binary expression AST node containing operator and operands</param>
    /// <returns>LLVM IR temporary variable containing the result of the binary operation</returns>
    /// <remarks>
    /// This method provides comprehensive binary operation support including:
    /// <list type="bullet">
    /// <item><strong>Math Library Integration</strong>: Automatic routing to specialized libraries for precision types</item>
    /// <item><strong>Overflow Handling</strong>: Support for wrap, saturate, checked, and unchecked variants</item>
    /// <item><strong>Type-Aware Operations</strong>: Correct signed/unsigned and integer/float operation selection</item>
    /// <item><strong>Comparison Operations</strong>: Proper handling of different comparison result types</item>
    /// </list>
    ///
    /// <strong>Operation Categories:</strong>
    /// <list type="bullet">
    /// <item>Arithmetic: +, -, *, /, % with overflow variants</item>
    /// <item>Comparison: ==, !=, &lt;, &lt;=, &gt;, &gt;= returning i1 boolean results</item>
    /// <item>Logical: &amp;&amp;, || with short-circuit evaluation support</item>
    /// <item>Bitwise: &amp;, |, ^, &lt;&lt;, &gt;&gt; for integer types</item>
    /// </list>
    /// </remarks>
    public string VisitBinaryExpression(BinaryExpression node)
    {
        string left = node.Left.Accept(visitor: this);
        string right = node.Right.Accept(visitor: this);
        string result = GetNextTemp();

        // Get operand type information (assume both operands have same type)
        TypeInfo leftTypeInfo = GetTypeInfo(expr: node.Left);
        string operandType = leftTypeInfo.LLVMType;

        // Check if this is a math library operation
        if (MathLibrarySupport.IsMathLibraryType(type: leftTypeInfo.RazorForgeType))
        {
            return GenerateMathLibraryBinaryOp(node: node, left: left, right: right,
                result: result, typeInfo: leftTypeInfo);
        }

        string op = node.Operator switch
        {
            // Regular arithmetic
            BinaryOperator.Add => "add",
            BinaryOperator.Subtract => "sub",
            BinaryOperator.Multiply => "mul",
            BinaryOperator.Divide => GetIntegerDivisionOp(
                typeInfo: leftTypeInfo), // sdiv/udiv based on signed/unsigned
            BinaryOperator.TrueDivide => "fdiv", // / (true division) - floats only
            BinaryOperator.Modulo => GetModuloOp(
                typeInfo: leftTypeInfo), // srem/urem for integers, frem for floats

            // Overflow-handling variants (for now, use LLVM intrinsics will be added later)
            BinaryOperator.AddWrap => "add", // Wrapping is default behavior
            BinaryOperator.SubtractWrap => "sub",
            BinaryOperator.MultiplyWrap => "mul",
            BinaryOperator.DivideWrap => GetIntegerDivisionOp(typeInfo: leftTypeInfo),
            BinaryOperator.ModuloWrap => GetModuloOp(typeInfo: leftTypeInfo),

            BinaryOperator.AddSaturate => "", // Handled separately with intrinsics
            BinaryOperator.SubtractSaturate => "", // Handled separately with intrinsics
            BinaryOperator.MultiplySaturate => "", // Handled separately with intrinsics

            BinaryOperator.AddChecked => "", // Handled separately with overflow intrinsics
            BinaryOperator.SubtractChecked => "", // Handled separately with overflow intrinsics
            BinaryOperator.MultiplyChecked => "", // Handled separately with overflow intrinsics

            BinaryOperator.AddUnchecked => "add", // Regular operations, no overflow checks
            BinaryOperator.SubtractUnchecked => "sub",
            BinaryOperator.MultiplyUnchecked => "mul",
            BinaryOperator.DivideUnchecked => GetIntegerDivisionOp(typeInfo: leftTypeInfo),
            BinaryOperator.ModuloUnchecked => GetModuloOp(typeInfo: leftTypeInfo),

            // Comparisons
            BinaryOperator.Less => "icmp slt",
            BinaryOperator.Greater => "icmp sgt",
            BinaryOperator.Equal => "icmp eq",
            BinaryOperator.NotEqual => "icmp ne",

            _ => "add"
        };

        // Handle special overflow operations with LLVM intrinsics
        if (string.IsNullOrEmpty(value: op))
        {
            // Handle saturating and checked operations
            switch (node.Operator)
            {
                case BinaryOperator.AddSaturate:
                case BinaryOperator.SubtractSaturate:
                case BinaryOperator.MultiplySaturate:
                    return GenerateSaturatingArithmetic(op: node.Operator, left: left,
                        right: right, result: result, typeInfo: leftTypeInfo,
                        llvmType: operandType);

                case BinaryOperator.AddChecked:
                case BinaryOperator.SubtractChecked:
                case BinaryOperator.MultiplyChecked:
                    return GenerateCheckedArithmetic(op: node.Operator, left: left, right: right,
                        result: result, typeInfo: leftTypeInfo, llvmType: operandType);

                default:
                    throw new NotSupportedException(
                        message: $"Operator {node.Operator} is not properly configured");
            }
        }

        // Generate the operation with proper type
        if (op.StartsWith(value: "icmp"))
        {
            // Comparison operations return i1
            _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {right}");
            _tempTypes[key: result] = new TypeInfo(LLVMType: "i1", IsUnsigned: false,
                IsFloatingPoint: false, RazorForgeType: "bool");
        }
        else
        {
            // Arithmetic operations maintain operand type
            _output.AppendLine(handler: $"  {result} = {op} {operandType} {left}, {right}");
            _tempTypes[key: result] = leftTypeInfo; // Result has same type as operands
        }

        return result;
    }

    /// <summary>
    /// Generates LLVM IR for saturating arithmetic operations using LLVM intrinsics.
    /// </summary>
    private string GenerateSaturatingArithmetic(BinaryOperator op, string left, string right,
        string result, TypeInfo typeInfo, string llvmType)
    {
        string intrinsicName = op switch
        {
            BinaryOperator.AddSaturate => typeInfo.IsUnsigned
                ? "llvm.uadd.sat"
                : "llvm.sadd.sat",
            BinaryOperator.SubtractSaturate => typeInfo.IsUnsigned
                ? "llvm.usub.sat"
                : "llvm.ssub.sat",
            BinaryOperator.MultiplySaturate => GenerateSaturatingMultiply(left: left, right: right,
                result: result, typeInfo: typeInfo, llvmType: llvmType),
            _ => throw new NotSupportedException(
                message: $"Saturating operation {op} not supported")
        };

        // For multiply, the implementation is handled separately
        if (op == BinaryOperator.MultiplySaturate)
        {
            return result;
        }

        // Generate intrinsic call for add/subtract
        _output.AppendLine(
            handler:
            $"  {result} = call {llvmType} @{intrinsicName}.{llvmType}({llvmType} {left}, {llvmType} {right})");
        _tempTypes[key: result] = typeInfo;
        return result;
    }

    /// <summary>
    /// Generates saturating multiply using manual overflow detection.
    /// LLVM doesn't provide a direct saturating multiply intrinsic, so we use overflow detection.
    /// </summary>
    private string GenerateSaturatingMultiply(string left, string right, string result,
        TypeInfo typeInfo, string llvmType)
    {
        string overflowTemp = GetNextTemp();
        string structTemp = GetNextTemp();
        string valueTemp = GetNextTemp();
        string didOverflowTemp = GetNextTemp();
        string maxValueTemp = GetNextTemp();
        string minValueTemp = GetNextTemp();
        string saturatedTemp = GetNextTemp();

        string intrinsicName = typeInfo.IsUnsigned
            ? "llvm.umul.with.overflow"
            : "llvm.smul.with.overflow";

        // Call overflow intrinsic
        _output.AppendLine(
            handler:
            $"  {structTemp} = call {{{llvmType}, i1}} @{intrinsicName}.{llvmType}({llvmType} {left}, {llvmType} {right})");
        _output.AppendLine(
            handler: $"  {valueTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 0");
        _output.AppendLine(
            handler: $"  {didOverflowTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 1");

        // Get max/min values for saturation
        (string maxValue, string minValue) =
            GetSaturationBounds(typeInfo: typeInfo, llvmType: llvmType);

        // Determine saturation value based on sign of operands if overflow occurred
        if (typeInfo.IsUnsigned)
        {
            // For unsigned: saturate to max value on overflow
            _output.AppendLine(
                handler:
                $"  {saturatedTemp} = select i1 {didOverflowTemp}, {llvmType} {maxValue}, {llvmType} {valueTemp}");
        }
        else
        {
            // For signed: need to check if result should be min or max
            // If both operands have same sign, overflow goes to max/min in same direction
            string leftSignTemp = GetNextTemp();
            string rightSignTemp = GetNextTemp();
            string sameSigns = GetNextTemp();
            string satValue = GetNextTemp();

            _output.AppendLine(handler: $"  {leftSignTemp} = icmp slt {llvmType} {left}, 0");
            _output.AppendLine(handler: $"  {rightSignTemp} = icmp slt {llvmType} {right}, 0");
            _output.AppendLine(
                handler: $"  {sameSigns} = icmp eq i1 {leftSignTemp}, {rightSignTemp}");

            // If same signs: both positive -> max, both negative -> max (negative * negative = positive)
            // If different signs: result should be min (negative)
            _output.AppendLine(
                handler:
                $"  {satValue} = select i1 {sameSigns}, {llvmType} {maxValue}, {llvmType} {minValue}");
            _output.AppendLine(
                handler:
                $"  {saturatedTemp} = select i1 {didOverflowTemp}, {llvmType} {satValue}, {llvmType} {valueTemp}");
        }

        _output.AppendLine(
            handler: $"  {result} = add {llvmType} {saturatedTemp}, 0  ; final saturated result");
        _tempTypes[key: result] = typeInfo;
        return result;
    }

    /// <summary>
    /// Generates LLVM IR for checked arithmetic operations that trap on overflow.
    /// </summary>
    private string GenerateCheckedArithmetic(BinaryOperator op, string left, string right,
        string result, TypeInfo typeInfo, string llvmType)
    {
        string intrinsicName = op switch
        {
            BinaryOperator.AddChecked => typeInfo.IsUnsigned
                ? "llvm.uadd.with.overflow"
                : "llvm.sadd.with.overflow",
            BinaryOperator.SubtractChecked => typeInfo.IsUnsigned
                ? "llvm.usub.with.overflow"
                : "llvm.ssub.with.overflow",
            BinaryOperator.MultiplyChecked => typeInfo.IsUnsigned
                ? "llvm.umul.with.overflow"
                : "llvm.smul.with.overflow",
            _ => throw new NotSupportedException(message: $"Checked operation {op} not supported")
        };

        string structTemp = GetNextTemp();
        string valueTemp = GetNextTemp();
        string didOverflowTemp = GetNextTemp();
        string trapLabel = GetNextLabel();
        string continueLabel = GetNextLabel();

        // Call overflow intrinsic which returns {result, overflow_flag}
        _output.AppendLine(
            handler:
            $"  {structTemp} = call {{{llvmType}, i1}} @{intrinsicName}.{llvmType}({llvmType} {left}, {llvmType} {right})");
        _output.AppendLine(
            handler: $"  {valueTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 0");
        _output.AppendLine(
            handler: $"  {didOverflowTemp} = extractvalue {{{llvmType}, i1}} {structTemp}, 1");

        // Branch on overflow flag
        _output.AppendLine(
            handler: $"  br i1 {didOverflowTemp}, label %{trapLabel}, label %{continueLabel}");

        // Trap block - call panic/abort on overflow
        _output.AppendLine(handler: $"{trapLabel}:");
        _output.AppendLine(
            value:
            $"  call void @rf_crash(ptr getelementptr inbounds ([20 x i8], [20 x i8]* @.str_overflow, i32 0, i32 0))");
        _output.AppendLine(value: $"  unreachable");

        // Continue block - normal execution
        _output.AppendLine(handler: $"{continueLabel}:");
        _output.AppendLine(
            handler: $"  {result} = add {llvmType} {valueTemp}, 0  ; propagate result");

        _tempTypes[key: result] = typeInfo;
        return result;
    }

    /// <summary>
    /// Gets the saturation bounds (max and min values) for a given type.
    /// </summary>
    private (string maxValue, string minValue) GetSaturationBounds(TypeInfo typeInfo,
        string llvmType)
    {
        if (typeInfo.IsUnsigned)
        {
            // Unsigned: min = 0, max = 2^bits - 1
            int bits = GetTypeBitWidth(llvmType: llvmType);
            string maxValue = bits switch
            {
                8 => "255",
                16 => "65535",
                32 => "4294967295",
                64 => "18446744073709551615",
                128 => "340282366920938463463374607431768211455",
                _ => "0"
            };
            return (maxValue, "0");
        }
        else
        {
            // Signed: min = -2^(bits-1), max = 2^(bits-1) - 1
            int bits = GetTypeBitWidth(llvmType: llvmType);
            (string maxValue, string minValue) = bits switch
            {
                8 => ("127", "-128"),
                16 => ("32767", "-32768"),
                32 => ("2147483647", "-2147483648"),
                64 => ("9223372036854775807", "-9223372036854775808"),
                128 => ("170141183460469231731687303715884105727",
                    "-170141183460469231731687303715884105728"),
                _ => ("0", "0")
            };
            return (maxValue, minValue);
        }
    }

    private string GenerateMathLibraryBinaryOp(BinaryExpression node, string left, string right,
        string result, TypeInfo typeInfo)
    {
        string operation = node.Operator switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.TrueDivide => "/",
            _ => throw new NotSupportedException(
                message:
                $"Operator {node.Operator} not supported for math library type {typeInfo.RazorForgeType}")
        };

        string mathLibraryCode = typeInfo.RazorForgeType switch
        {
            "d32" => MathLibrarySupport.GenerateD32BinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result),
            "d64" => MathLibrarySupport.GenerateD64BinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result),
            "d128" => MathLibrarySupport.GenerateD128BinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result),
            "bigint" => MathLibrarySupport.GenerateBigIntBinaryOp(operation: operation,
                leftOperand: left, rightOperand: right, resultTemp: result,
                tempCounter: _tempCounter.ToString()),
            "decimal" => MathLibrarySupport.GenerateHighPrecisionDecimalBinaryOp(
                operation: operation, leftOperand: left, rightOperand: right, resultTemp: result,
                contextPtr: "%decimal_context"),
            _ => throw new NotSupportedException(
                message: $"Math library type {typeInfo.RazorForgeType} not supported")
        };

        _output.AppendLine(value: mathLibraryCode);
        _tempTypes[key: result] = typeInfo; // Result has same type as operands
        return result;
    }

    // Literal expression
    public string VisitLiteralExpression(LiteralExpression node)
    {
        if (node.Value is int intVal)
        {
            return intVal.ToString();
        }
        else if (node.Value is long longVal)
        {
            return longVal.ToString();
        }
        else if (node.Value is byte byteVal)
        {
            return byteVal.ToString();
        }
        else if (node.Value is sbyte sbyteVal)
        {
            return sbyteVal.ToString();
        }
        else if (node.Value is short shortVal)
        {
            return shortVal.ToString();
        }
        else if (node.Value is ushort ushortVal)
        {
            return ushortVal.ToString();
        }
        else if (node.Value is uint uintVal)
        {
            return uintVal.ToString();
        }
        else if (node.Value is ulong ulongVal)
        {
            return ulongVal.ToString();
        }
        else if (node.Value is float floatVal)
        {
            return floatVal.ToString(format: "G");
        }
        else if (node.Value is double doubleVal)
        {
            return doubleVal.ToString(format: "G");
        }
        else if (node.Value is decimal decimalVal)
        {
            return decimalVal.ToString(format: "G");
        }
        else if (node.Value is BigInteger bigIntVal)
        {
            return bigIntVal.ToString();
        }
        else if (node.Value is Half halfVal)
        {
            return ((float)halfVal).ToString(format: "G");
        }
        else if (node.Value is bool boolVal)
        {
            return boolVal
                ? "1"
                : "0";
        }
        else if (node.Value is string strVal)
        {
            string strConst = $"@.str{_tempCounter++}";
            int len = strVal.Length + 1;
            // Store string constant for later emission instead of inserting immediately
            if (_stringConstants == null)
            {
                _stringConstants = new List<string>();
            }

            _stringConstants.Add(
                item:
                $"{strConst} = private unnamed_addr constant [{len} x i8] c\"{strVal}\\00\", align 1");
            string temp = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {temp} = getelementptr [{len} x i8], [{len} x i8]* {strConst}, i32 0, i32 0");
            // Register the temp as a string pointer type
            _tempTypes[temp] = new TypeInfo("i8*", false, false, "Text");
            return temp;
        }

        return "0";
    }

    // Map TokenType to LLVM type
    private string GetLLVMType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.S8Literal => "i8",
            TokenType.S16Literal => "i16",
            TokenType.S32Literal => "i32",
            TokenType.S64Literal => "i64",
            TokenType.S128Literal => "i128",
            TokenType.U8Literal => "i8", // LLVM doesn't distinguish signed/unsigned at IR level
            TokenType.U16Literal => "i16",
            TokenType.U32Literal => "i32",
            TokenType.U64Literal => "i64",
            TokenType.U128Literal => "i128",
            TokenType.F16Literal => "half",
            TokenType.F32Literal => "float",
            TokenType.F64Literal => "double",
            TokenType.F128Literal => "fp128", // IEEE 754 quad precision
            TokenType.Integer => "i128", // BigInteger -> large integer type
            TokenType.Decimal => "double", // Default floating type
            TokenType.True => "i1",
            TokenType.False => "i1",
            _ => "i32" // Default fallback
        };
    }

    // Identifier expression
    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        // Handle special built-in values
        if (node.Name == "None")
        {
            // None is represented as null pointer for Maybe<T> types
            // When used in a return statement, the return type will be i8* (pointer)
            // so we return null constant
            string nextWord = GetNextTemp();
            _tempTypes[nextWord] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false,
                IsFloatingPoint: false, RazorForgeType: "None");
            // Return null as the None value - it will be used directly in inttoptr or ret
            return "null";
        }

        string type = _symbolTypes.ContainsKey(key: node.Name)
            ? _symbolTypes[key: node.Name]
            : "i32";

        // If this is a function parameter, it's already a value - no load needed
        if (_functionParameters.Contains(item: node.Name))
        {
            return $"%{node.Name}";
        }

        // For local variables, we need to load from the stack
        string temp = GetNextTemp();
        _output.AppendLine(handler: $"  {temp} = load {type}, {type}* %{node.Name}");
        return temp;
    }

    // Function call expression
    public string VisitCallExpression(CallExpression node)
    {
        string result = GetNextTemp();

        // Check if this is a standalone danger zone function call (address_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            string dangerfunctionName = identifierExpr.Name;
            if (IsNonGenericDangerZoneFunction(functionName: dangerfunctionName))
            {
                return HandleNonGenericDangerZoneFunction(node: node,
                    functionName: dangerfunctionName, resultTemp: result);
            }

            // Check for non-generic CompilerService intrinsics (source location)
            if (IsSourceLocationIntrinsic(functionName: dangerfunctionName))
            {
                return HandleSourceLocationIntrinsic(node: node,
                    functionName: dangerfunctionName, resultTemp: result);
            }
        }

        var args = new List<string>();
        foreach (Expression arg in node.Arguments)
        {
            string argValue = arg.Accept(visitor: this);
            // Determine argument type - check if it's a tracked temp, otherwise infer from expression
            string argType = "i32"; // default
            if (_tempTypes.TryGetValue(key: argValue, value: out TypeInfo? argTypeInfo))
            {
                argType = argTypeInfo.LLVMType;
            }
            else if (arg is LiteralExpression literal && literal.Value is string)
            {
                argType = "i8*"; // String literals produce pointers
            }
            args.Add(item: $"{argType} {argValue}");
        }

        string argList = string.Join(separator: ", ", values: args);

        // Special handling for built-in functions
        if (node.Callee is IdentifierExpression id)
        {
            if (id.Name == "show")
            {
                if (args.Count > 0)
                {
                    _output.AppendLine(
                        handler:
                        $"  {result} = call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([4 x i8], [4 x i8]* @.str_int_fmt, i32 0, i32 0), {argList})");
                }

                return result;
            }
        }

        // Get function name without generating extra instructions
        string functionName;
        if (node.Callee is IdentifierExpression identifier)
        {
            functionName = identifier.Name;

            // Check if this is a danger zone function that should use specialized handling
            if (IsNonGenericDangerZoneFunction(functionName: functionName))
            {
                return HandleNonGenericDangerZoneFunction(node: node, functionName: functionName,
                    resultTemp: result);
            }

            // Check for type constructor calls (e.g., s32!, u64!, s32?, etc.)
            string sanitizedName = SanitizeFunctionName(functionName);
            if (IsTypeConstructorCall(sanitizedName))
            {
                return HandleTypeConstructorCall(sanitizedName, node.Arguments, result);
            }
        }
        else if (node.Callee is MemberExpression memberExpr)
        {
            // Handle member expression calls like Console.show, Error.from_text
            string objectName = memberExpr.Object switch
            {
                IdentifierExpression idExpr => idExpr.Name,
                _ => "unknown"
            };

            // Special handling for Console I/O - map directly to C runtime
            if (objectName == "Console")
            {
                return HandleConsoleCall(memberExpr.PropertyName, node.Arguments, result);
            }

            // Special handling for Error type
            if (objectName == "Error")
            {
                return HandleErrorCall(memberExpr.PropertyName, node.Arguments, result);
            }

            // For other member calls, convert to mangled name: Object.method -> Object_method
            functionName = $"{objectName}_{memberExpr.PropertyName}";
        }
        else
        {
            // For more complex expressions, we'd need to handle them differently
            functionName = "unknown_function";
        }

        string sanitizedFunctionName = SanitizeFunctionName(functionName);
        _output.AppendLine(handler: $"  {result} = call i32 @{sanitizedFunctionName}({argList})");
        return result;
    }

    // If statement
    public string VisitIfStatement(IfStatement node)
    {
        string condition = node.Condition.Accept(visitor: this);
        string thenLabel = GetNextLabel();
        string elseLabel = GetNextLabel();
        string endLabel = GetNextLabel();

        _output.AppendLine(
            handler: $"  br i1 {condition}, label %{thenLabel}, label %{elseLabel}");

        // Then block
        _output.AppendLine(handler: $"{thenLabel}:");
        _blockTerminated = false;
        node.ThenStatement.Accept(visitor: this);
        bool thenTerminated = _blockTerminated;
        if (!thenTerminated)
        {
            _output.AppendLine(handler: $"  br label %{endLabel}");
        }

        // Else block
        _output.AppendLine(handler: $"{elseLabel}:");
        _blockTerminated = false;
        if (node.ElseStatement != null)
        {
            node.ElseStatement.Accept(visitor: this);
        }
        bool elseTerminated = _blockTerminated;
        if (!elseTerminated)
        {
            _output.AppendLine(handler: $"  br label %{endLabel}");
        }

        // End label (only if needed)
        if (!thenTerminated || !elseTerminated)
        {
            _output.AppendLine(handler: $"{endLabel}:");
        }
        _blockTerminated = false;

        return "";
    }

    // While statement
    public string VisitWhileStatement(WhileStatement node)
    {
        string condLabel = GetNextLabel();
        string bodyLabel = GetNextLabel();
        string endLabel = GetNextLabel();

        _output.AppendLine(handler: $"  br label %{condLabel}");

        _output.AppendLine(handler: $"{condLabel}:");
        string condition = node.Condition.Accept(visitor: this);
        _output.AppendLine(handler: $"  br i1 {condition}, label %{bodyLabel}, label %{endLabel}");

        _output.AppendLine(handler: $"{bodyLabel}:");
        node.Body.Accept(visitor: this);
        _output.AppendLine(handler: $"  br label %{condLabel}");

        _output.AppendLine(handler: $"{endLabel}:");

        return "";
    }

    // Return statement - we need to track the expected function return type
    private string
        _currentFunctionReturnType = "i32"; // Default, will be set by VisitFunctionDeclaration

    public string VisitReturnStatement(ReturnStatement node)
    {
        _hasReturn = true; // Mark that we've generated a return
        _blockTerminated = true; // Mark block as terminated

        if (node.Value != null)
        {
            string value = node.Value.Accept(visitor: this);
            TypeInfo valueTypeInfo = GetValueTypeInfo(value: value);

            // If the value type doesn't match the function return type, we need to cast
            if (valueTypeInfo.LLVMType != _currentFunctionReturnType)
            {
                string castResult = GetNextTemp();
                GenerateCastInstruction(result: castResult, value: value, fromType: valueTypeInfo,
                    toType: _currentFunctionReturnType);
                _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} {castResult}");
            }
            else
            {
                _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} {value}");
            }
        }
        else
        {
            _output.AppendLine(value: "  ret void");
        }

        return "";
    }

    // Generate appropriate cast instruction
    private void GenerateCastInstruction(string result, string value, TypeInfo fromType,
        string toType)
    {
        bool fromIsPointer = fromType.LLVMType.EndsWith("*");
        bool toIsPointer = toType.EndsWith("*");

        // Handle pointer conversions
        if (!fromIsPointer && toIsPointer)
        {
            // Integer to pointer: use inttoptr
            _output.AppendLine(
                handler: $"  {result} = inttoptr {fromType.LLVMType} {value} to {toType}");
            return;
        }
        else if (fromIsPointer && !toIsPointer)
        {
            // Pointer to integer: use ptrtoint
            _output.AppendLine(
                handler: $"  {result} = ptrtoint {fromType.LLVMType} {value} to {toType}");
            return;
        }
        else if (fromIsPointer && toIsPointer)
        {
            // Pointer to pointer: use bitcast
            _output.AppendLine(
                handler: $"  {result} = bitcast {fromType.LLVMType} {value} to {toType}");
            return;
        }

        // Handle floating point conversions
        if (fromType.IsFloatingPoint || IsFloatingPointType(llvmType: toType))
        {
            // Float conversions need special handling
            _output.AppendLine(
                handler: $"  {result} = fptoui {fromType.LLVMType} {value} to {toType}");
        }
        else
        {
            // Integer truncation or extension
            int fromSize = GetTypeBitWidth(llvmType: fromType.LLVMType);
            int toSize = GetTypeBitWidth(llvmType: toType);

            if (fromSize > toSize)
            {
                // Truncation
                _output.AppendLine(
                    handler: $"  {result} = trunc {fromType.LLVMType} {value} to {toType}");
            }
            else if (fromSize < toSize)
            {
                // Extension
                if (fromType.IsUnsigned)
                {
                    _output.AppendLine(
                        handler: $"  {result} = zext {fromType.LLVMType} {value} to {toType}");
                }
                else
                {
                    _output.AppendLine(
                        handler: $"  {result} = sext {fromType.LLVMType} {value} to {toType}");
                }
            }
            else
            {
                // Same size, just use as-is (bitcast if needed)
                _output.AppendLine(
                    handler: $"  {result} = bitcast {fromType.LLVMType} {value} to {toType}");
            }
        }
    }

    // Get bit width of LLVM type
    private int GetTypeBitWidth(string llvmType)
    {
        return llvmType switch
        {
            "i1" => 1,
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            "i128" => 128,
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            _ => 32 // Default
        };
    }

    // Type information including signedness and RazorForge type
    private record TypeInfo(
        string LLVMType,
        bool IsUnsigned,
        bool IsFloatingPoint,
        string RazorForgeType = "");

    // Get LLVM type for an expression
    private string GetExpressionType(Expression expr)
    {
        return GetTypeInfo(expr: expr)
           .LLVMType;
    }

    // Get complete type information for an expression
    private TypeInfo GetTypeInfo(Expression expr)
    {
        if (expr is LiteralExpression literal)
        {
            string llvmType = GetLLVMType(tokenType: literal.LiteralType);
            bool isUnsigned = IsUnsignedTokenType(tokenType: literal.LiteralType);
            bool isFloatingPoint = IsFloatingPointTokenType(tokenType: literal.LiteralType);
            string razorForgeType = GetRazorForgeTypeFromToken(tokenType: literal.LiteralType);
            return new TypeInfo(LLVMType: llvmType, IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloatingPoint, RazorForgeType: razorForgeType);
        }
        // For binary expressions, we need to evaluate them first to get the result type
        // This is handled by the visitor methods storing results in _tempTypes

        // Default to signed i32
        return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
            RazorForgeType: "i32");
    }

    // Get type info for a temporary variable or literal value
    private TypeInfo GetValueTypeInfo(string value)
    {
        if (_tempTypes.TryGetValue(key: value, value: out TypeInfo? typeInfo))
        {
            return typeInfo;
        }

        // Handle LLVM null constant - it's a pointer type
        if (value == "null")
        {
            return new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "None");
        }

        // If it's not a temp variable, it might be a literal value
        // Try to infer type from the value itself (this is a simplified approach)
        if (int.TryParse(s: value, result: out _))
        {
            // It's a numeric literal, assume i32 for now
            return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i32");
        }

        // Default fallback
        return new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
            RazorForgeType: "i32");
    }

    // Get type info from string type name
    private TypeInfo GetTypeInfo(string typeName)
    {
        return typeName switch
        {
            // Signed integers
            "i8" => new TypeInfo(LLVMType: "i8", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i8"),
            "i16" => new TypeInfo(LLVMType: "i16", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i16"),
            "i32" => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i32"),
            "i64" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i64"),
            "i128" => new TypeInfo(LLVMType: "i128", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i128"),

            // Unsigned integers
            "u8" => new TypeInfo(LLVMType: "i8", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u8"),
            "u16" => new TypeInfo(LLVMType: "i16", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u16"),
            "u32" => new TypeInfo(LLVMType: "i32", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u32"),
            "u64" => new TypeInfo(LLVMType: "i64", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u64"),
            "u128" => new TypeInfo(LLVMType: "i128", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "u128"),

            // System-dependent integers
            "isys" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "isys"), // intptr_t - typically i64 on 64-bit systems
            "usys" => new TypeInfo(LLVMType: "i64", IsUnsigned: true, IsFloatingPoint: false,
                RazorForgeType: "usys"), // uintptr_t - typically i64 on 64-bit systems

            // Floating point types
            "f16" => new TypeInfo(LLVMType: "half", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f16"),
            "f32" => new TypeInfo(LLVMType: "float", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f32"),
            "f64" => new TypeInfo(LLVMType: "double", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f64"),
            "f128" => new TypeInfo(LLVMType: "fp128", IsUnsigned: false, IsFloatingPoint: true,
                RazorForgeType: "f128"),

            // Boolean
            "bool" => new TypeInfo(LLVMType: "i1", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "bool"),

            // Math library types
            "d32" => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "d32"),
            "d64" => new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "d64"),
            "d128" => new TypeInfo(LLVMType: "{i64, i64}", IsUnsigned: false,
                IsFloatingPoint: false, RazorForgeType: "d128"),
            "bigint" => new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "bigint"),
            "decimal" => new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "decimal"),

            _ => new TypeInfo(LLVMType: "i32", IsUnsigned: false, IsFloatingPoint: false,
                RazorForgeType: "i32")
        };
    }

    // Get RazorForge type name from token type
    private string GetRazorForgeTypeFromToken(TokenType tokenType)
    {
        return tokenType switch
        {
            // Integer literals
            TokenType.S8Literal => "s8",
            TokenType.S16Literal => "s16",
            TokenType.S32Literal => "s32",
            TokenType.S64Literal => "s64",
            TokenType.S128Literal => "s128",
            TokenType.U8Literal => "u8",
            TokenType.U16Literal => "u16",
            TokenType.U32Literal => "u32",
            TokenType.U64Literal => "u64",
            TokenType.U128Literal => "u128",

            // Floating point literals
            TokenType.F16Literal => "f16",
            TokenType.F32Literal => "f32",
            TokenType.F64Literal => "f64",
            TokenType.F128Literal => "f128",

            // Decimal literals (IEEE 754)
            TokenType.D32Literal => "d32",
            TokenType.D64Literal => "d64",
            TokenType.D128Literal => "d128",

            // Arbitrary precision types
            TokenType.Integer => "bigint", // Suflae arbitrary precision integer
            TokenType.Decimal => "decimal", // Suflae arbitrary precision decimal

            // Default types
            _ => "i32"
        };
    }

    // Check if token type is unsigned
    private bool IsUnsignedTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.U8Literal or TokenType.U16Literal or TokenType.U32Literal
                or TokenType.U64Literal or TokenType.U128Literal => true,
            _ => false
        };
    }

    // Check if token type is floating point
    private bool IsFloatingPointTokenType(TokenType tokenType)
    {
        return tokenType switch
        {
            TokenType.F16Literal or TokenType.F32Literal or TokenType.F64Literal
                or TokenType.F128Literal or TokenType.Decimal => true,
            _ => false
        };
    }

    // Get appropriate division operation based on type
    private string GetDivisionOp(string llvmType)
    {
        return IsFloatingPointType(llvmType: llvmType)
            ? "fdiv"
            : "sdiv";
    }

    // Get true division operation (always floating point)
    private string GetTrueDivisionOp(string llvmType)
    {
        return "fdiv";
    }

    // Get appropriate modulo operation based on type
    private string GetModuloOp(TypeInfo typeInfo)
    {
        if (typeInfo.IsFloatingPoint)
        {
            return "frem";
        }

        return typeInfo.IsUnsigned
            ? "urem"
            : "srem";
    }

    // Get appropriate integer division operation (signed vs unsigned)
    private string GetIntegerDivisionOp(TypeInfo typeInfo)
    {
        return typeInfo.IsUnsigned
            ? "udiv"
            : "sdiv";
    }

    // Check if LLVM type is floating point
    private bool IsFloatingPointType(string llvmType)
    {
        return llvmType switch
        {
            "half" or "float" or "double" or "fp128" => true,
            _ => false
        };
    }

    // Type declarations
    /// <summary>
    /// Generates LLVM IR for entity (class) declarations.
    /// For generic entities, stores the template for later instantiation.
    /// For non-generic entities, generates the struct type definition immediately.
    /// </summary>
    public string VisitClassDeclaration(ClassDeclaration node)
    {
        // Check if this is a generic entity
        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // Store the template for later instantiation
            _genericEntityTemplates[node.Name] = node;
            _output.AppendLine($"; Generic entity template: {node.Name}<{string.Join(", ", node.GenericParameters)}>");
            return "";
        }

        // Non-generic entity - generate type definition
        return GenerateEntityType(node: node, typeSubstitutions: null, mangledName: null);
    }

    /// <summary>
    /// Generates LLVM IR for record (struct) declarations.
    /// For generic records, stores the template for later instantiation.
    /// For non-generic records, generates the struct type definition immediately.
    /// </summary>
    public string VisitStructDeclaration(StructDeclaration node)
    {
        // Check if this is a generic record
        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // Store the template for later instantiation
            _genericRecordTemplates[node.Name] = node;
            _output.AppendLine($"; Generic record template: {node.Name}<{string.Join(", ", node.GenericParameters)}>");
            return "";
        }

        // Non-generic record - generate type definition
        return GenerateRecordType(node: node, typeSubstitutions: null, mangledName: null);
    }

    /// <summary>
    /// Generates LLVM struct type definition for an entity (class).
    /// </summary>
    private string GenerateEntityType(ClassDeclaration node, Dictionary<string, string>? typeSubstitutions, string? mangledName)
    {
        string typeName = mangledName ?? node.Name;

        // Avoid emitting the same type twice
        if (_emittedTypes.Contains(typeName))
        {
            return "";
        }
        _emittedTypes.Add(typeName);

        // Collect field types
        var fieldTypes = new List<string>();
        var fieldNames = new List<string>(); // For comments

        foreach (Declaration member in node.Members)
        {
            if (member is VariableDeclaration field)
            {
                string fieldType = field.Type != null
                    ? MapTypeWithSubstitution(typeName: field.Type.Name, substitutions: typeSubstitutions)
                    : "i32";
                fieldTypes.Add(item: fieldType);
                fieldNames.Add(item: field.Name);
            }
        }

        // Generate LLVM struct type
        string fieldList = string.Join(separator: ", ", values: fieldTypes);
        _output.AppendLine($"; Entity type: {typeName}");
        _output.AppendLine($"%{typeName} = type {{ {fieldList} }}");

        // Add comment with field names for debugging
        if (fieldNames.Count > 0)
        {
            _output.AppendLine($"; Fields: {string.Join(", ", fieldNames.Select((n, i) => $"{n}: {fieldTypes[i]}"))}");
        }
        _output.AppendLine();

        return "";
    }

    /// <summary>
    /// Generates LLVM struct type definition for a record (struct).
    /// </summary>
    private string GenerateRecordType(StructDeclaration node, Dictionary<string, string>? typeSubstitutions, string? mangledName)
    {
        string typeName = mangledName ?? node.Name;

        // Avoid emitting the same type twice
        if (_emittedTypes.Contains(typeName))
        {
            return "";
        }
        _emittedTypes.Add(typeName);

        // Collect field types
        var fieldTypes = new List<string>();
        var fieldNames = new List<string>(); // For comments

        foreach (Declaration member in node.Members)
        {
            if (member is VariableDeclaration field)
            {
                string fieldType = field.Type != null
                    ? MapTypeWithSubstitution(typeName: field.Type.Name, substitutions: typeSubstitutions)
                    : "i32";
                fieldTypes.Add(item: fieldType);
                fieldNames.Add(item: field.Name);
            }
        }

        // Generate LLVM struct type
        string fieldList = string.Join(separator: ", ", values: fieldTypes);
        _output.AppendLine($"; Record type: {typeName}");
        _output.AppendLine($"%{typeName} = type {{ {fieldList} }}");

        // Add comment with field names for debugging
        if (fieldNames.Count > 0)
        {
            _output.AppendLine($"; Fields: {string.Join(", ", fieldNames.Select((n, i) => $"{n}: {fieldTypes[i]}"))}");
        }
        _output.AppendLine();

        return "";
    }

    /// <summary>
    /// Instantiates a generic record with concrete type arguments.
    /// </summary>
    public string InstantiateGenericRecord(string recordName, List<string> typeArguments)
    {
        if (!_genericRecordTemplates.TryGetValue(recordName, out StructDeclaration? template))
        {
            return recordName; // Not a generic record
        }

        // Generate mangled name
        string mangledName = $"{recordName}_{string.Join("_", typeArguments)}";

        // Check if already instantiated
        if (_emittedTypes.Contains(mangledName))
        {
            return mangledName;
        }

        // Track and queue for later generation
        if (!_genericTypeInstantiations.ContainsKey(recordName))
        {
            _genericTypeInstantiations[recordName] = new List<List<string>>();
        }
        _genericTypeInstantiations[recordName].Add(typeArguments);
        _pendingRecordInstantiations.Add($"{recordName}|{string.Join(",", typeArguments)}");

        return mangledName;
    }

    /// <summary>
    /// Instantiates a generic entity with concrete type arguments.
    /// </summary>
    public string InstantiateGenericEntity(string entityName, List<string> typeArguments)
    {
        if (!_genericEntityTemplates.TryGetValue(entityName, out ClassDeclaration? template))
        {
            return entityName; // Not a generic entity
        }

        // Generate mangled name
        string mangledName = $"{entityName}_{string.Join("_", typeArguments)}";

        // Check if already instantiated
        if (_emittedTypes.Contains(mangledName))
        {
            return mangledName;
        }

        // Track and queue for later generation
        if (!_genericTypeInstantiations.ContainsKey(entityName))
        {
            _genericTypeInstantiations[entityName] = new List<List<string>>();
        }
        _genericTypeInstantiations[entityName].Add(typeArguments);
        _pendingEntityInstantiations.Add($"{entityName}|{string.Join(",", typeArguments)}");

        return mangledName;
    }

    /// <summary>
    /// Generates all pending generic type instantiations.
    /// Should be called after all program code is generated.
    /// </summary>
    private void GeneratePendingTypeInstantiations()
    {
        // Process pending record instantiations
        foreach (string pending in _pendingRecordInstantiations)
        {
            string[] parts = pending.Split('|');
            string recordName = parts[0];
            List<string> typeArguments = parts[1].Split(',').ToList();

            if (!_genericRecordTemplates.TryGetValue(recordName, out StructDeclaration? template))
            {
                continue;
            }

            // Create type substitution map
            var substitutions = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(template.GenericParameters!.Count, typeArguments.Count); i++)
            {
                substitutions[template.GenericParameters[i]] = typeArguments[i];
            }

            // Generate mangled name
            string mangledName = $"{recordName}_{string.Join("_", typeArguments)}";

            // Generate the instantiated record type
            GenerateRecordType(node: template, typeSubstitutions: substitutions, mangledName: mangledName);
        }
        _pendingRecordInstantiations.Clear();

        // Process pending entity instantiations
        foreach (string pending in _pendingEntityInstantiations)
        {
            string[] parts = pending.Split('|');
            string entityName = parts[0];
            List<string> typeArguments = parts[1].Split(',').ToList();

            if (!_genericEntityTemplates.TryGetValue(entityName, out ClassDeclaration? template))
            {
                continue;
            }

            // Create type substitution map
            var substitutions = new Dictionary<string, string>();
            for (int i = 0; i < Math.Min(template.GenericParameters!.Count, typeArguments.Count); i++)
            {
                substitutions[template.GenericParameters[i]] = typeArguments[i];
            }

            // Generate mangled name
            string mangledName = $"{entityName}_{string.Join("_", typeArguments)}";

            // Generate the instantiated entity type
            GenerateEntityType(node: template, typeSubstitutions: substitutions, mangledName: mangledName);
        }
        _pendingEntityInstantiations.Clear();
    }
    public string VisitMenuDeclaration(MenuDeclaration node)
    {
        return "";
    }
    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        return "";
    }
    public string VisitFeatureDeclaration(FeatureDeclaration node)
    {
        return "";
    }
    public string VisitImportDeclaration(ImportDeclaration node)
    {
        return "";
    }
    public string VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        return "";
    }
    public string VisitUsingDeclaration(UsingDeclaration node)
    {
        return "";
    }
    public string VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        return "";
    }
    public string VisitUnaryExpression(UnaryExpression node)
    {
        return "";
    }
    public string VisitMemberExpression(MemberExpression node)
    {
        return "";
    }
    public string VisitIndexExpression(IndexExpression node)
    {
        return "";
    }
    public string VisitConditionalExpression(ConditionalExpression node)
    {
        return "";
    }
    public string VisitRangeExpression(RangeExpression node)
    {
        // For now, generate a simple record representation
        // In a real implementation, this would create a Range<T> object
        string start = node.Start.Accept(visitor: this);
        string end = node.End.Accept(visitor: this);

        if (node.Step != null)
        {
            string step = node.Step.Accept(visitor: this);
            // Generate code for range with step
            _output.AppendLine(handler: $"; Range from {start} to {end} step {step}");
        }
        else
        {
            // Generate code for range without step (default step 1)
            _output.AppendLine(handler: $"; Range from {start} to {end}");
        }

        return start; // Placeholder
    }

    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        // Desugar chained comparison: a < b < c becomes (a < b) and (b < c)
        // with single evaluation of b
        if (node.Operands.Count < 2 || node.Operators.Count < 1)
        {
            return "";
        }

        string result = GetNextTemp();
        var tempVars = new List<string>();

        // Evaluate all operands once and store in temporaries
        for (int i = 0; i < node.Operands.Count; i++)
        {
            if (i == 0)
            {
                // First operand doesn't need temporary storage for first comparison
                tempVars.Add(item: node.Operands[index: i]
                                       .Accept(visitor: this));
            }
            else if (i == node.Operands.Count - 1)
            {
                // Last operand doesn't need temporary storage
                tempVars.Add(item: node.Operands[index: i]
                                       .Accept(visitor: this));
            }
            else
            {
                // Middle operands need temporary storage to avoid multiple evaluation
                string temp = GetNextTemp();
                string operandValue = node.Operands[index: i]
                                          .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  {temp} = add i32 {operandValue}, 0  ; store for reuse");
                tempVars.Add(item: temp);
            }
        }

        // Generate comparisons: (temp0 op0 temp1) and (temp1 op1 temp2) and ...
        var compResults = new List<string>();
        for (int i = 0; i < node.Operators.Count; i++)
        {
            string compResult = GetNextTemp();
            string left = tempVars[index: i];
            string right = tempVars[index: i + 1];
            string op = node.Operators[index: i] switch
            {
                BinaryOperator.Less => "icmp slt",
                BinaryOperator.LessEqual => "icmp sle",
                BinaryOperator.Greater => "icmp sgt",
                BinaryOperator.GreaterEqual => "icmp sge",
                BinaryOperator.Equal => "icmp eq",
                BinaryOperator.NotEqual => "icmp ne",
                _ => "icmp eq"
            };

            _output.AppendLine(handler: $"  {compResult} = {op} i32 {left}, {right}");
            compResults.Add(item: compResult);
        }

        // Combine all comparisons with AND
        if (compResults.Count == 1)
        {
            return compResults[index: 0];
        }

        string finalResult = compResults[index: 0];
        for (int i = 1; i < compResults.Count; i++)
        {
            string temp = GetNextTemp();
            _output.AppendLine(
                handler: $"  {temp} = and i1 {finalResult}, {compResults[index: i]}");
            finalResult = temp;
        }

        return finalResult;
    }
    /// <summary>
    /// Generates LLVM IR for lambda expressions.
    /// Creates an anonymous function and returns a function pointer to it.
    /// </summary>
    /// <param name="node">The lambda expression AST node</param>
    /// <returns>A function pointer reference to the generated lambda</returns>
    /// <remarks>
    /// Lambda implementation strategy:
    /// <list type="bullet">
    /// <item>Generate a unique function name (e.g., __lambda_0, __lambda_1)</item>
    /// <item>Create the function definition with appropriate signature</item>
    /// <item>Queue the definition for emission after main code</item>
    /// <item>Return a function pointer to the lambda</item>
    /// </list>
    /// Note: This implementation does not yet support closure capture.
    /// All variables used in the lambda must be parameters.
    /// </remarks>
    public string VisitLambdaExpression(LambdaExpression node)
    {
        // Generate unique lambda name
        string lambdaName = $"__lambda_{_lambdaCounter++}";

        // Build parameter list and determine types
        var paramTypes = new List<string>();
        var paramList = new List<string>();

        foreach (Parameter param in node.Parameters)
        {
            string paramType = param.Type != null
                ? MapRazorForgeTypeToLLVM(razorForgeType: param.Type.Name)
                : "i32"; // Default to i32 if no type specified

            paramTypes.Add(item: paramType);
            paramList.Add(item: $"{paramType} %{param.Name}");
        }

        // Infer return type from body expression
        // For now, evaluate the body to determine the type
        // We'll generate the body later in the function definition
        string returnType = InferLambdaReturnType(body: node.Body, parameters: node.Parameters);

        // Build function signature string for the type
        string paramTypeStr = string.Join(separator: ", ", values: paramTypes);
        string funcPtrType = $"{returnType} ({paramTypeStr})*";

        // Generate the lambda function definition
        var lambdaBuilder = new StringBuilder();
        lambdaBuilder.AppendLine();
        lambdaBuilder.AppendLine($"; Lambda function {lambdaName}");
        lambdaBuilder.AppendLine($"define private {returnType} @{lambdaName}({string.Join(", ", paramList)}) {{");
        lambdaBuilder.AppendLine("entry:");

        // Save current state
        var savedSymbolTypes = new Dictionary<string, string>(_symbolTypes);
        var savedFunctionParameters = new HashSet<string>(_functionParameters);
        bool savedHasReturn = _hasReturn;
        bool savedBlockTerminated = _blockTerminated;
        var savedTypeSubstitutions = _currentTypeSubstitutions;

        // Save the current output position to extract lambda body later
        int outputStartPos = _output.Length;

        _hasReturn = false;
        _blockTerminated = false;
        _functionParameters.Clear();

        // Register parameters in symbol table
        for (int i = 0; i < node.Parameters.Count; i++)
        {
            _symbolTypes[key: node.Parameters[i].Name] = paramTypes[i];
            _functionParameters.Add(item: node.Parameters[i].Name);
        }

        // Generate lambda body directly into _output
        string bodyResult = node.Body.Accept(visitor: this);

        // Extract the lambda body code that was generated
        string lambdaBodyCode = _output.ToString().Substring(startIndex: outputStartPos);

        // Remove the lambda body code from main output
        _output.Length = outputStartPos;

        // Add the body code and return to lambda builder
        lambdaBuilder.Append(value: lambdaBodyCode);

        // Add return statement with the result
        if (returnType != "void")
        {
            lambdaBuilder.AppendLine($"  ret {returnType} {bodyResult}");
        }
        else
        {
            lambdaBuilder.AppendLine("  ret void");
        }

        lambdaBuilder.AppendLine("}");

        // Restore state
        _symbolTypes.Clear();
        foreach (var kvp in savedSymbolTypes)
        {
            _symbolTypes[key: kvp.Key] = kvp.Value;
        }
        _functionParameters.Clear();
        foreach (string param in savedFunctionParameters)
        {
            _functionParameters.Add(item: param);
        }
        _hasReturn = savedHasReturn;
        _blockTerminated = savedBlockTerminated;
        _currentTypeSubstitutions = savedTypeSubstitutions;

        // Queue the lambda definition for later emission
        _pendingLambdaDefinitions.Add(item: lambdaBuilder.ToString());

        // Return a reference to the function pointer
        // In LLVM, a function reference is just @function_name
        // When used as a value, we need to cast it to the appropriate function pointer type
        string resultTemp = GetNextTemp();
        _output.AppendLine($"  {resultTemp} = bitcast {returnType} ({paramTypeStr})* @{lambdaName} to ptr");

        // Track the type
        _tempTypes[key: resultTemp] = new TypeInfo(
            LLVMType: "ptr",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: $"lambda<{string.Join(", ", paramTypes)}>->{returnType}");

        return resultTemp;
    }

    /// <summary>
    /// Infers the return type of a lambda expression from its body.
    /// </summary>
    private string InferLambdaReturnType(Expression body, List<Parameter> parameters)
    {
        // Create a temporary scope to infer types
        var tempSymbolTypes = new Dictionary<string, string>();
        foreach (Parameter param in parameters)
        {
            string paramType = param.Type != null
                ? MapRazorForgeTypeToLLVM(razorForgeType: param.Type.Name)
                : "i32";
            tempSymbolTypes[key: param.Name] = paramType;
        }

        // Infer type based on expression kind
        return body switch
        {
            LiteralExpression lit => InferLiteralType(lit: lit),
            BinaryExpression bin => InferBinaryExpressionType(bin: bin, symbolTypes: tempSymbolTypes),
            IdentifierExpression id => tempSymbolTypes.TryGetValue(id.Name, out string? t) ? t : "i32",
            CallExpression => "i32", // Default for function calls
            ConditionalExpression cond => InferLambdaReturnType(body: cond.TrueExpression, parameters: parameters),
            _ => "i32" // Default to i32
        };
    }

    /// <summary>
    /// Infers the LLVM type from a literal expression.
    /// For RazorForge: unsuffixed integers default to i64, unsuffixed floats default to f64 (double)
    /// For Suflae: TokenType.Integer and TokenType.Decimal are arbitrary precision (handled separately)
    /// </summary>
    private string InferLiteralType(LiteralExpression lit)
    {
        return lit.LiteralType switch
        {
            // Integer types - map to appropriate LLVM integer widths
            TokenType.S8Literal or TokenType.U8Literal => "i8",
            TokenType.S16Literal or TokenType.U16Literal => "i16",
            TokenType.S32Literal or TokenType.U32Literal => "i32",
            TokenType.S64Literal or TokenType.U64Literal or
            TokenType.SyssintLiteral or TokenType.SysuintLiteral => "i64",
            TokenType.S128Literal or TokenType.U128Literal => "i128",

            // RazorForge unsuffixed integer -> i64
            // Suflae Integer is arbitrary precision (would need bigint library)
            TokenType.Integer => _language == Language.RazorForge ? "i64" : "ptr", // ptr for bigint struct

            // Float types - map to appropriate LLVM floating point widths
            TokenType.F16Literal => "half",
            TokenType.F32Literal => "float",
            TokenType.F64Literal => "double",
            TokenType.F128Literal => "fp128",

            // RazorForge unsuffixed decimal -> f64 (double)
            // Suflae Decimal is arbitrary precision (would need decimal library)
            TokenType.Decimal => _language == Language.RazorForge ? "double" : "ptr", // ptr for decimal struct

            // Boolean
            TokenType.True or TokenType.False => "i1",

            // Text/String types (TextLiteral is the default 32-bit text)
            TokenType.TextLiteral or TokenType.Text8Literal or TokenType.Text16Literal => "ptr",

            // Character types - map to appropriate bit widths
            TokenType.Letter8Literal => "i8",
            TokenType.Letter16Literal => "i16",
            TokenType.LetterLiteral => "i32", // Default letter is 32-bit (Unicode codepoint)

            _ => "i32"
        };
    }

    /// <summary>
    /// Infers the result type of a binary expression.
    /// </summary>
    private string InferBinaryExpressionType(BinaryExpression bin, Dictionary<string, string> symbolTypes)
    {
        // Comparison operators always return bool
        if (bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or
            BinaryOperator.Less or BinaryOperator.LessEqual or
            BinaryOperator.Greater or BinaryOperator.GreaterEqual or
            BinaryOperator.And or BinaryOperator.Or)
        {
            return "i1";
        }

        // For arithmetic, infer from operands
        string leftType = bin.Left switch
        {
            LiteralExpression lit => InferLiteralType(lit: lit),
            IdentifierExpression id => symbolTypes.TryGetValue(id.Name, out string? t) ? t : "i32",
            BinaryExpression nested => InferBinaryExpressionType(bin: nested, symbolTypes: symbolTypes),
            _ => "i32"
        };

        return leftType;
    }
    public string VisitTypeExpression(TypeExpression node)
    {
        return "";
    }

    public string VisitTypeConversionExpression(TypeConversionExpression node)
    {
        string sourceValue = node.Expression.Accept(visitor: this);
        TypeInfo targetTypeInfo = GetTypeInfo(typeName: node.TargetType);

        // Generate a temporary variable for the conversion result
        string tempVar = $"%tmp{_tempCounter++}";

        // Perform the type conversion using LLVM cast instructions
        TypeInfo sourceTypeInfo = GetTypeInfo(expr: node.Expression);

        string conversionOp =
            GetConversionInstruction(sourceType: sourceTypeInfo, targetType: targetTypeInfo);

        _output.AppendLine(
            handler:
            $"  {tempVar} = {conversionOp} {sourceTypeInfo.LLVMType} {sourceValue} to {targetTypeInfo.LLVMType}");

        return tempVar;
    }

    private string GetConversionInstruction(TypeInfo sourceType, TypeInfo targetType)
    {
        // Handle floating point to integer conversions
        if (sourceType.IsFloatingPoint && !targetType.IsFloatingPoint)
        {
            return targetType.IsUnsigned
                ? "fptoui"
                : "fptosi";
        }

        // Handle integer to floating point conversions
        if (!sourceType.IsFloatingPoint && targetType.IsFloatingPoint)
        {
            return sourceType.IsUnsigned
                ? "uitofp"
                : "sitofp";
        }

        // Handle floating point to floating point conversions
        if (sourceType.IsFloatingPoint && targetType.IsFloatingPoint)
        {
            return GetFloatingPointSize(llvmType: sourceType.LLVMType) >
                   GetFloatingPointSize(llvmType: targetType.LLVMType)
                ? "fptrunc"
                : "fpext";
        }

        // Handle integer to integer conversions
        if (!sourceType.IsFloatingPoint && !targetType.IsFloatingPoint)
        {
            int sourceSize = GetIntegerSize(llvmType: sourceType.LLVMType);
            int targetSize = GetIntegerSize(llvmType: targetType.LLVMType);

            if (sourceSize > targetSize)
            {
                return "trunc";
            }
            else if (sourceSize < targetSize)
            {
                return sourceType.IsUnsigned
                    ? "zext"
                    : "sext";
            }
            else
            {
                return "bitcast"; // Same size, just change signedness
            }
        }

        throw new InvalidOperationException(
            message: $"Cannot convert from {sourceType.LLVMType} to {targetType.LLVMType}");
    }

    private int GetFloatingPointSize(string llvmType)
    {
        return llvmType switch
        {
            "half" => 16,
            "float" => 32,
            "double" => 64,
            "fp128" => 128,
            _ => throw new ArgumentException(message: $"Unknown floating point type: {llvmType}")
        };
    }

    private int GetIntegerSize(string llvmType)
    {
        return llvmType switch
        {
            "i8" => 8,
            "i16" => 16,
            "i32" => 32,
            "i64" => 64,
            _ => throw new ArgumentException(message: $"Unknown integer type: {llvmType}")
        };
    }
    public string VisitExpressionStatement(ExpressionStatement node)
    {
        return node.Expression.Accept(visitor: this);
    }
    public string VisitDeclarationStatement(DeclarationStatement node)
    {
        return node.Declaration.Accept(visitor: this);
    }
    public string VisitAssignmentStatement(AssignmentStatement node)
    {
        return "";
    }
    public string VisitForStatement(ForStatement node)
    {
        return "";
    }
    public string VisitBreakStatement(BreakStatement node)
    {
        return "";
    }
    public string VisitContinueStatement(ContinueStatement node)
    {
        return "";
    }

    public string VisitThrowStatement(ThrowStatement node)
    {
        // For now, throw statements will be handled similarly to return statements
        // In the future, this should construct a Result<T> with an error variant
        // and return it from the function
        _hasReturn = true;
        _blockTerminated = true;

        // Generate the error expression (for side effects like print, etc.)
        string error = node.Error.Accept(visitor: this);

        // For now, return a sentinel value compatible with the function's return type
        // TODO: Wrap this in a Result<T> error variant when Result types are implemented
        _output.AppendLine($"  ; throw {error} - returning error sentinel");

        // Return appropriate sentinel value based on current function return type
        if (_currentFunctionReturnType == "void")
        {
            _output.AppendLine("  ret void");
        }
        else if (_currentFunctionReturnType.StartsWith("i") || _currentFunctionReturnType == "i32")
        {
            // For integer types, return -1 as error sentinel
            _output.AppendLine($"  ret {_currentFunctionReturnType} -1");
        }
        else if (_currentFunctionReturnType.StartsWith("float") || _currentFunctionReturnType.StartsWith("double"))
        {
            // For float types, return NaN would be ideal but use 0.0 for simplicity
            _output.AppendLine($"  ret {_currentFunctionReturnType} 0.0");
        }
        else
        {
            // For pointer/other types, return null
            _output.AppendLine($"  ret {_currentFunctionReturnType} null");
        }

        return "";
    }

    public string VisitAbsentStatement(AbsentStatement node)
    {
        // For now, absent statements will be handled similarly to return statements
        // In the future, this should construct a Lookup<T> with an absent variant
        // and return it from the function
        _hasReturn = true;
        _blockTerminated = true;

        // For now, just generate a return with a sentinel value (null/0)
        // TODO: Wrap this in a Lookup<T> absent variant when Lookup types are implemented
        _output.AppendLine(handler: $"  ; absent - returning 'not found' sentinel");
        _output.AppendLine(handler: $"  ret {_currentFunctionReturnType} null");

        return "";
    }

    public string VisitWhenStatement(WhenStatement node)
    {
        return "";
    }
    public string VisitBlockStatement(BlockStatement node)
    {
        foreach (Statement statement in node.Statements)
        {
            statement.Accept(visitor: this);
        }

        return "";
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        string sizeTemp = node.SizeExpression.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        if (node.SliceType == "DynamicSlice")
        {
            // Generate LLVM IR for heap slice construction
            _output.AppendLine(handler: $"  {resultTemp} = call ptr @heap_alloc(i64 {sizeTemp})");
        }
        else if (node.SliceType == "TemporarySlice")
        {
            // Generate LLVM IR for stack slice construction
            _output.AppendLine(handler: $"  {resultTemp} = call ptr @stack_alloc(i64 {sizeTemp})");
        }

        // Store slice type information for later use
        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "ptr", IsUnsigned: false,
            IsFloatingPoint: false, RazorForgeType: node.SliceType);
        return resultTemp;
    }

    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        string resultTemp = GetNextTemp();

        // Check if this is a standalone danger zone function call or user-defined generic function
        if (node.Object is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;

            // Check for danger zone functions
            if (IsDangerZoneFunction(functionName: functionName))
            {
                // Get type argument for generic method
                TypeExpression dangerTypeArg = node.TypeArguments.First();
                string dangerLlvmType =
                    MapRazorForgeTypeToLLVM(razorForgeType: dangerTypeArg.Name);

                return HandleDangerZoneFunction(node: node, functionName: functionName,
                    llvmType: dangerLlvmType, typeName: dangerTypeArg.Name,
                    resultTemp: resultTemp);
            }

            // Check for CompilerService intrinsics
            if (IsCompilerServiceIntrinsic(functionName: functionName))
            {
                return HandleCompilerServiceIntrinsic(node: node, functionName: functionName,
                    resultTemp: resultTemp);
            }

            // Check for user-defined generic function
            if (_genericFunctionTemplates.ContainsKey(functionName))
            {
                // Get the concrete type arguments
                var typeArgs = node.TypeArguments.Select(t => t.Name).ToList();

                // Instantiate the generic function (queues for later code generation)
                string mangledName = InstantiateGenericFunction(functionName, typeArgs);

                // Generate call to the instantiated function
                var argTemps = new List<string>();
                var argTypes = new List<string>();
                foreach (var arg in node.Arguments)
                {
                    string argTemp = arg.Accept(this);
                    string argType = _tempTypes.TryGetValue(argTemp, out var ti) ? ti.LLVMType : "i32";
                    argTemps.Add(argTemp);
                    argTypes.Add(argType);
                }

                string argList = string.Join(", ",
                    argTemps.Zip(argTypes, (temp, type) => $"{type} {temp}"));

                // Determine return type (simplified - would need proper type resolution)
                string returnType = "i32"; // Default

                _output.AppendLine($"  {resultTemp} = call {returnType} @{mangledName}({argList})");
                return resultTemp;
            }
        }

        string objectTemp = node.Object.Accept(visitor: this);

        // Get type argument for generic method
        TypeExpression typeArg = node.TypeArguments.First();
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name);

        switch (node.MethodName)
        {
            case "read":
                string offsetTemp = node.Arguments[index: 0]
                                        .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call {llvmType} @memory_read_{typeArg.Name}(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeArg.Name);
                break;

            case "write":
                string writeOffsetTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                string valueTemp = node.Arguments[index: 1]
                                       .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  call void @memory_write_{typeArg.Name}(ptr {objectTemp}, i64 {writeOffsetTemp}, {llvmType} {valueTemp})");
                resultTemp = ""; // void return
                break;

            case "write_as":
                // write_as<T>!(address, value) - direct memory write to address
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string writeValueTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                resultTemp = ""; // void return
                break;

            case "read_as":
                // read_as<T>!(address) - direct memory read from address
                string readAddrTemp = node.Arguments[index: 0]
                                          .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeArg.Name);
                break;

            case "volatile_write":
                // volatile_write<T>!(address, value) - volatile memory write to address
                string volWriteAddrTemp = node.Arguments[index: 0]
                                              .Accept(visitor: this);
                string volWriteValueTemp = node.Arguments[index: 1]
                                               .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                resultTemp = ""; // void return
                break;

            case "volatile_read":
                // volatile_read<T>!(address) - volatile memory read from address
                string volReadAddrTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeArg.Name)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeArg.Name);
                break;

            default:
                // Check if this is a user-defined generic function that needs instantiation
                if (node.Object is IdentifierExpression funcIdentifier)
                {
                    string baseFunctionName = funcIdentifier.Name;

                    // Check if we have a template for this function
                    if (_genericFunctionTemplates.ContainsKey(baseFunctionName))
                    {
                        // Get the concrete type arguments
                        var typeArgs = node.TypeArguments.Select(t => t.Name).ToList();

                        // Instantiate the generic function (generates code if not already done)
                        string mangledName = InstantiateGenericFunction(baseFunctionName, typeArgs);

                        // Generate call to the instantiated function
                        var argTemps = new List<string>();
                        var argTypes = new List<string>();
                        foreach (var arg in node.Arguments)
                        {
                            string argTemp = arg.Accept(this);
                            string argType = _tempTypes.TryGetValue(argTemp, out var ti) ? ti.LLVMType : "i32";
                            argTemps.Add(argTemp);
                            argTypes.Add(argType);
                        }

                        string argList = string.Join(", ",
                            argTemps.Zip(argTypes, (temp, type) => $"{type} {temp}"));

                        // Determine return type (simplified - would need proper type resolution)
                        string returnType = "i32"; // Default

                        _output.AppendLine($"  {resultTemp} = call {returnType} @{mangledName}({argList})");
                        return resultTemp;
                    }
                }

                // Fall through to method call on object
                string objMethodTemp = node.Object.Accept(this);
                var methodTypeArgs = node.TypeArguments.Select(t => t.Name).ToList();
                string methodMangledName = $"{node.MethodName}_{string.Join("_", methodTypeArgs)}";

                var methodArgTemps = new List<string>();
                foreach (var arg in node.Arguments)
                {
                    methodArgTemps.Add(arg.Accept(this));
                }

                string methodArgList = string.Join(", ",
                    methodArgTemps.Select(t => $"i32 {t}"));

                _output.AppendLine($"  {resultTemp} = call i32 @{methodMangledName}(ptr {objMethodTemp}{(methodArgTemps.Count > 0 ? ", " + methodArgList : "")})");
                return resultTemp;
        }

        return resultTemp;
    }

    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        // TODO: Implement generic member access
        return GetNextTemp();
    }

    public string VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        string objectTemp = node.Object.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        switch (node.OperationName)
        {
            case "size":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_size(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                break;

            case "address":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_address(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                break;

            case "is_valid":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i1 @slice_is_valid(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i1", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "bool");
                break;

            case "unsafe_ptr":
                string offsetTemp = node.Arguments[index: 0]
                                        .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call i64 @slice_unsafe_ptr(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                break;

            case "slice":
                string sliceOffsetTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                string sliceBytesTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call ptr @slice_subslice(ptr {objectTemp}, i64 {sliceOffsetTemp}, i64 {sliceBytesTemp})");

                // Get the original slice type
                if (_tempTypes.TryGetValue(key: objectTemp, value: out TypeInfo? objType))
                {
                    _tempTypes[key: resultTemp] = objType;
                }

                break;

            case "hijack":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call ptr @slice_hijack(ptr {objectTemp})");
                if (_tempTypes.TryGetValue(key: objectTemp, value: out TypeInfo? hijackType))
                {
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "ptr", IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: $"Hijacked<{hijackType.RazorForgeType}>");
                }

                break;

            case "refer":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_refer(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                break;

            default:
                throw new NotImplementedException(
                    message: $"Memory operation {node.OperationName} not implemented");
        }

        return resultTemp;
    }

    public string VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        string resultTemp = GetNextTemp();
        string intrinsicName = node.IntrinsicName;

        // Dispatch to specific intrinsic handler based on category
        if (intrinsicName.StartsWith("load") || intrinsicName.StartsWith("store") ||
            intrinsicName.StartsWith("volatile_") || intrinsicName == "bitcast" ||
            intrinsicName == "invalidate")
        {
            return EmitMemoryIntrinsic(node, resultTemp);
        }
        // Arithmetic operations - both with suffixes (add.wrapping) and bare (add, sdiv, neg)
        else if (intrinsicName.StartsWith("add.") || intrinsicName.StartsWith("sub.") ||
                 intrinsicName.StartsWith("mul.") || intrinsicName.StartsWith("div.") ||
                 intrinsicName.StartsWith("rem.") ||
                 intrinsicName == "add" || intrinsicName == "sub" || intrinsicName == "mul" ||
                 intrinsicName == "sdiv" || intrinsicName == "udiv" ||
                 intrinsicName == "srem" || intrinsicName == "urem" ||
                 intrinsicName == "neg")
        {
            return EmitArithmeticIntrinsic(node, resultTemp);
        }
        else if (intrinsicName.StartsWith("icmp.") || intrinsicName.StartsWith("fcmp."))
        {
            return EmitComparisonIntrinsic(node, resultTemp);
        }
        else if (intrinsicName == "and" || intrinsicName == "or" || intrinsicName == "xor" ||
                 intrinsicName == "not" || intrinsicName == "shl" || intrinsicName == "lshr" ||
                 intrinsicName == "ashr")
        {
            return EmitBitwiseIntrinsic(node, resultTemp);
        }
        else if (intrinsicName == "trunc" || intrinsicName == "zext" || intrinsicName == "sext" ||
                 intrinsicName == "fptrunc" || intrinsicName == "fpext" || intrinsicName == "fptoui" ||
                 intrinsicName == "fptosi" || intrinsicName == "uitofp" || intrinsicName == "sitofp")
        {
            return EmitConversionIntrinsic(node, resultTemp);
        }
        else if (intrinsicName == "sqrt" || intrinsicName == "abs" || intrinsicName == "fabs" ||
                 intrinsicName == "copysign" || intrinsicName == "floor" || intrinsicName == "ceil" ||
                 intrinsicName == "trunc_float" || intrinsicName == "round" || intrinsicName == "pow" ||
                 intrinsicName == "exp" || intrinsicName == "log" || intrinsicName == "log10" ||
                 intrinsicName == "sin" || intrinsicName == "cos")
        {
            return EmitMathIntrinsic(node, resultTemp);
        }
        else if (intrinsicName.StartsWith("atomic."))
        {
            return EmitAtomicIntrinsic(node, resultTemp);
        }
        else if (intrinsicName == "ctpop" || intrinsicName == "ctlz" || intrinsicName == "cttz" ||
                 intrinsicName == "bswap" || intrinsicName == "bitreverse")
        {
            return EmitBitManipIntrinsic(node, resultTemp);
        }
        else
        {
            throw new NotImplementedException($"Intrinsic {intrinsicName} not implemented");
        }
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        // Add comment to indicate unsafe block
        _output.AppendLine(value: "  ; === DANGER BLOCK START ===");
        node.Body.Accept(visitor: this);
        _output.AppendLine(value: "  ; === DANGER BLOCK END ===");
        return "";
    }

    public string VisitMayhemStatement(MayhemStatement node)
    {
        // Add comment to indicate maximum unsafe block
        _output.AppendLine(value: "  ; === MAYHEM BLOCK START ===");
        node.Body.Accept(visitor: this);
        _output.AppendLine(value: "  ; === MAYHEM BLOCK END ===");
        return "";
    }

    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        // Generate external function declaration
        string paramTypes = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p =>
                MapRazorForgeTypeToLLVM(razorForgeType: p.Type?.Name ?? "void")));
        string returnType = node.ReturnType != null
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.ReturnType.Name)
            : "void";

        // Map calling convention to LLVM calling convention attribute
        string callingConventionAttr =
            MapCallingConventionToLLVM(callingConvention: node.CallingConvention);

        string sanitizedName = SanitizeFunctionName(node.Name);

        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // For generic external functions, we'll need to generate specialized versions
            _output.AppendLine(
                handler:
                $"; Generic external function {sanitizedName} - specialized versions generated on demand");
        }
        else
        {
            // Emit external declaration with calling convention
            if (!string.IsNullOrEmpty(value: callingConventionAttr))
            {
                _output.AppendLine(
                    handler:
                    $"declare {callingConventionAttr} {returnType} @{sanitizedName}({paramTypes})");
            }
            else
            {
                _output.AppendLine(handler: $"declare {returnType} @{sanitizedName}({paramTypes})");
            }
        }

        return "";
    }

    /// <summary>
    /// Maps RazorForge calling convention names to LLVM calling convention attributes.
    /// </summary>
    /// <param name="callingConvention">Calling convention string ("C", "stdcall", "fastcall", etc.)</param>
    /// <returns>LLVM calling convention attribute or empty string for default</returns>
    private string MapCallingConventionToLLVM(string? callingConvention)
    {
        if (string.IsNullOrEmpty(value: callingConvention))
        {
            return ""; // Default C calling convention
        }

        return callingConvention.ToLowerInvariant() switch
        {
            "c" => "ccc", // C calling convention (default on most platforms)
            "stdcall" => "x86_stdcallcc", // Windows stdcall
            "fastcall" => "x86_fastcallcc", // x86 fastcall
            "thiscall" => "x86_thiscallcc", // C++ thiscall (MSVC)
            "vectorcall" => "x86_vectorcallcc", // x86 vectorcall (MSVC)
            "win64" => "win64cc", // Windows x64 calling convention
            "sysv64" => "x86_64_sysvcc", // System V AMD64 ABI (Unix/Linux)
            "aapcs" => "arm_aapcscc", // ARM AAPCS
            "aapcs_vfp" => "arm_aapcs_vfpcc", // ARM AAPCS with VFP
            _ => "" // Unknown convention, use default
        };
    }

    private string MapRazorForgeTypeToLLVM(string razorForgeType)
    {
        return razorForgeType switch
        {
            "s8" => "i8",
            "s16" => "i16",
            "s32" => "i32",
            "s64" => "i64",
            "s128" => "i128",
            "u8" => "i8",
            "u16" => "i16",
            "u32" => "i32",
            "u64" => "i64",
            "u128" => "i128",
            "uaddr" or "saddr" or "iptr" or "uptr" => _targetPlatform
               .GetPointerSizedIntType(), // Architecture-dependent pointer-sized integers
            "f16" => "half",
            "f32" => "float",
            "f64" => "double",
            "f128" => "fp128",
            "bool" => "i1",
            "letter" => "i32", // UTF-32
            "text" => "ptr",
            "DynamicSlice" or "TemporarySlice" => "ptr",
            "void" => "void",

            // C FFI types - Character types
            "cchar" or "cschar" => "i8",
            "cuchar" => "i8",
            "cwchar" => _targetPlatform.GetWCharType(), // OS-dependent
            "cchar8" => "i8",
            "cchar16" => "i16",
            "cchar32" => "i32",

            // C FFI types - Numeric types
            "cshort" => "i16",
            "cushort" => "i16",
            "cint" => "i32",
            "cuint" => "i32",
            "clong" => _targetPlatform.GetLongType(), // OS-dependent
            "culong" => _targetPlatform.GetLongType(), // OS-dependent
            "cll" => "i64",
            "cull" => "i64",
            "cfloat" => "float",
            "cdouble" => "double",

            // C FFI types - Pointer types (architecture-dependent)
            "csptr" => _targetPlatform.GetPointerSizedIntType(),
            "cuptr" => _targetPlatform.GetPointerSizedIntType(),
            "cvoid" => _targetPlatform.GetPointerSizedIntType(),
            "cbool" => "i1",

            _ => "ptr" // Default to pointer for unknown types (including cptr<T>)
        };
    }

    private void GenerateSliceRuntimeDeclarations()
    {
        // Generate declarations for slice runtime functions
        string[] declarations = new[]
        {
            "declare ptr @heap_alloc(i64)",
            "declare ptr @stack_alloc(i64)",
            "declare void @heap_free(ptr)",
            "declare ptr @heap_realloc(ptr, i64)",
            "declare void @memory_copy(ptr, ptr, i64)",
            "declare void @memory_fill(ptr, i8, i64)",
            "declare void @memory_zero(ptr, i64)",
            "declare i64 @slice_size(ptr)",
            "declare i64 @slice_address(ptr)",
            "declare i1 @slice_is_valid(ptr)",
            "declare i64 @slice_unsafe_ptr(ptr, i64)",
            "declare ptr @slice_subslice(ptr, i64, i64)",
            "declare ptr @slice_hijack(ptr)",
            "declare i64 @slice_refer(ptr)",

            // Danger zone operations
            "declare i64 @read_as_bytes(i64, i64)",
            "declare void @write_as_bytes(i64, i64, i64)",
            "declare i64 @volatile_read_bytes(i64, i64)",
            "declare void @volatile_write_bytes(i64, i64, i64)",
            "declare i64 @address_of(ptr)",
            "declare void @invalidate_memory(i64)",
            "declare void @rf_crash(ptr)"
        };

        foreach (string decl in declarations)
        {
            _output.AppendLine(value: decl);
        }
    }

    private int GetAlignment(string typeName)
    {
        return typeName switch
        {
            "s8" or "u8" or "bool" => 1,
            "s16" or "u16" => 2,
            "s32" or "u32" or "f32" => 4,
            "s64" or "u64" or "f64" or "ptr" => 8,
            "s128" or "u128" => 16,
            _ => 8 // Default to 8-byte alignment
        };
    }

    private bool IsDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "write_as" or "read_as" or "volatile_write" or "volatile_read" => true,
            _ => false
        };
    }

    /// <summary>
    /// Checks if the function is a CompilerService intrinsic.
    /// </summary>
    private bool IsCompilerServiceIntrinsic(string functionName)
    {
        return functionName switch
        {
            "size_of" or "align_of" or "get_compile_type_name" or
            "field_names" or "field_count" or "has_method" => true,
            _ => false
        };
    }

    /// <summary>
    /// Handles CompilerService intrinsic calls.
    /// These are compile-time evaluated and embedded as constants.
    /// </summary>
    private string HandleCompilerServiceIntrinsic(GenericMethodCallExpression node,
        string functionName, string resultTemp)
    {
        // Get the type argument
        if (node.TypeArguments.Count == 0)
        {
            throw new InvalidOperationException(
                $"CompilerService intrinsic {functionName} requires a type argument");
        }

        TypeExpression typeArg = node.TypeArguments.First();
        string typeName = typeArg.Name;

        switch (functionName)
        {
            case "size_of":
                // Get size of type in bytes using LLVM's getelementptr trick
                int size = GetTypeSize(typeName: typeName);
                _output.AppendLine($"  {resultTemp} = add i64 0, {size}");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: true,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                return resultTemp;

            case "align_of":
                // Get alignment of type
                int alignment = GetAlignment(typeName: typeName);
                _output.AppendLine($"  {resultTemp} = add i64 0, {alignment}");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: true,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                return resultTemp;

            case "get_compile_type_name":
                // Return the type name as a string constant
                string typeNameStr = typeName;
                string strConstName = GetOrCreateStringConstant(typeNameStr);
                _output.AppendLine($"  {resultTemp} = getelementptr [{typeNameStr.Length + 1} x i8], [{typeNameStr.Length + 1} x i8]* {strConstName}, i32 0, i32 0");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "field_count":
                // Get number of fields in a struct/record type
                int fieldCount = GetFieldCount(typeName: typeName);
                _output.AppendLine($"  {resultTemp} = add i64 0, {fieldCount}");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: true,
                    IsFloatingPoint: false, RazorForgeType: "uaddr");
                return resultTemp;

            case "field_names":
                // TODO: Return list of field names - requires runtime list construction
                _output.AppendLine($"  ; TODO: field_names<{typeName}>() - requires List<Text> construction");
                _output.AppendLine($"  {resultTemp} = inttoptr i64 0 to ptr");
                return resultTemp;

            case "has_method":
                // TODO: Check if type has a method - requires symbol table lookup
                _output.AppendLine($"  ; TODO: has_method<{typeName}>() - requires symbol table lookup");
                _output.AppendLine($"  {resultTemp} = add i1 0, 0");
                return resultTemp;

            default:
                throw new NotImplementedException(
                    $"CompilerService intrinsic {functionName} not implemented");
        }
    }

    /// <summary>
    /// Gets the size of a type in bytes.
    /// </summary>
    private int GetTypeSize(string typeName)
    {
        return typeName switch
        {
            "s8" or "u8" or "bool" or "letter8" => 1,
            "s16" or "u16" or "letter16" => 2,
            "s32" or "u32" or "f32" or "letter32" => 4,
            "s64" or "u64" or "f64" => 8,
            "s128" or "u128" or "f128" => 16,
            "uaddr" or "saddr" => 8, // Assume 64-bit platform
            _ => 8 // Default to pointer size for unknown types
        };
    }

    /// <summary>
    /// Gets the number of fields in a struct/record type.
    /// Returns 1 for primitive types.
    /// </summary>
    private int GetFieldCount(string typeName)
    {
        // For primitive types, return 0
        if (IsPrimitiveType(typeName))
        {
            return 1;
        }

        // TODO: Look up type in symbol table to get field count
        // For now, return 1 for unknown types
        return 1;
    }

    /// <summary>
    /// Checks if a type is a primitive (non-compound) type.
    /// </summary>
    private bool IsPrimitiveType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or
            "u8" or "u16" or "u32" or "u64" or "u128" or
            "f16" or "f32" or "f64" or "f128" or
            "bool" or "letter8" or "letter16" or "letter32" or
            "uaddr" or "saddr" => true,
            _ => false
        };
    }

    /// <summary>
    /// Gets or creates a string constant and returns its LLVM name.
    /// </summary>
    private string GetOrCreateStringConstant(string value)
    {
        string strConst = $"@.str_cs{_tempCounter++}";
        int len = value.Length + 1;

        if (_stringConstants == null)
        {
            _stringConstants = new List<string>();
        }

        // Escape special characters for LLVM string literal
        string escaped = value.Replace("\\", "\\5C").Replace("\"", "\\22");
        _stringConstants.Add($"{strConst} = private unnamed_addr constant [{len} x i8] c\"{escaped}\\00\", align 1");

        return strConst;
    }

    /// <summary>
    /// Checks if the function is a source location intrinsic.
    /// </summary>
    private bool IsSourceLocationIntrinsic(string functionName)
    {
        return functionName switch
        {
            "get_line_number" or "get_column_number" or
            "get_file_name" or "get_caller_name" or
            "get_current_module" => true,
            _ => false
        };
    }

    /// <summary>
    /// Handles source location intrinsic calls.
    /// These are compile-time evaluated based on the AST node's source location.
    /// </summary>
    private string HandleSourceLocationIntrinsic(CallExpression node,
        string functionName, string resultTemp)
    {
        switch (functionName)
        {
            case "get_line_number":
                int line = node.Location.Line;
                _output.AppendLine($"  {resultTemp} = add i64 0, {line}");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "s64");
                return resultTemp;

            case "get_column_number":
                int column = node.Location.Column;
                _output.AppendLine($"  {resultTemp} = add i64 0, {column}");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "s64");
                return resultTemp;

            case "get_file_name":
                // Get file name from current context (would need to be passed through)
                string fileName = _currentFileName ?? "unknown";
                string fileNameConst = GetOrCreateStringConstant(fileName);
                _output.AppendLine($"  {resultTemp} = getelementptr [{fileName.Length + 1} x i8], [{fileName.Length + 1} x i8]* {fileNameConst}, i32 0, i32 0");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "get_caller_name":
                // Get the current function name
                string callerName = _currentFunctionName ?? "unknown";
                string callerConst = GetOrCreateStringConstant(callerName);
                _output.AppendLine($"  {resultTemp} = getelementptr [{callerName.Length + 1} x i8], [{callerName.Length + 1} x i8]* {callerConst}, i32 0, i32 0");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "get_current_module":
                // Get module name from file path
                string moduleName = _currentFileName != null
                    ? Path.GetFileNameWithoutExtension(_currentFileName)
                    : "unknown";
                string moduleConst = GetOrCreateStringConstant(moduleName);
                _output.AppendLine($"  {resultTemp} = getelementptr [{moduleName.Length + 1} x i8], [{moduleName.Length + 1} x i8]* {moduleConst}, i32 0, i32 0");
                _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: "Text<letter8>");
                return resultTemp;

            default:
                throw new NotImplementedException(
                    $"Source location intrinsic {functionName} not implemented");
        }
    }

    /// <summary>
    /// Handles Console.* calls by mapping them to C runtime functions.
    /// </summary>
    private string HandleConsoleCall(string methodName, List<Expression> arguments, string resultTemp)
    {
        switch (methodName)
        {
            case "show":
                if (arguments.Count > 0)
                {
                    string argValue = arguments[0].Accept(visitor: this);
                    // Check if argument is a string (i8*) or integer
                    if (_tempTypes.TryGetValue(argValue, out var typeInfo) && typeInfo.LLVMType == "i8*")
                    {
                        // String argument - use printf with %s format
                        _output.AppendLine($"  {resultTemp} = call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([3 x i8], [3 x i8]* @.str_fmt, i32 0, i32 0), i8* {argValue})");
                    }
                    else
                    {
                        // Integer argument - use printf with %d format
                        _output.AppendLine($"  {resultTemp} = call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([4 x i8], [4 x i8]* @.str_int_fmt, i32 0, i32 0), i32 {argValue})");
                    }
                }
                return resultTemp;

            case "show_line":
                if (arguments.Count > 0)
                {
                    string argValue = arguments[0].Accept(visitor: this);
                    // Use puts for string with newline (simpler than printf)
                    if (_tempTypes.TryGetValue(argValue, out var typeInfo) && typeInfo.LLVMType == "i8*")
                    {
                        _output.AppendLine($"  {resultTemp} = call i32 @puts(i8* {argValue})");
                    }
                    else
                    {
                        // Integer - use printf with %d\n format
                        _output.AppendLine($"  {resultTemp} = call i32 (i8*, ...) @printf(i8* getelementptr inbounds ([4 x i8], [4 x i8]* @.str_int_fmt, i32 0, i32 0), i32 {argValue})");
                    }
                }
                else
                {
                    // Empty show_line() - just print newline
                    _output.AppendLine($"  {resultTemp} = call i32 @putchar(i32 10)"); // 10 = '\n'
                }
                return resultTemp;

            case "flush":
                // fflush(stdout) - stdout is __acrt_iob_func(1) on Windows or can use NULL for all
                _output.AppendLine($"  {resultTemp} = call i32 @fflush(i8* null)");
                return resultTemp;

            case "clear":
                // For clear, we can output ANSI escape sequence or just return 0
                // Using ANSI: \x1B[2J\x1B[H (clear screen and move cursor home)
                // For now, just return 0 as a placeholder
                _output.AppendLine($"  {resultTemp} = add i32 0, 0"); // placeholder - returns 0
                return resultTemp;

            case "input_word":
            case "input_line":
                // For input, we need to allocate a buffer and use scanf/fgets
                // This is a simplified implementation - allocate 256 byte buffer
                string bufferTemp = GetNextTemp();
                _output.AppendLine($"  {bufferTemp} = call i8* @malloc(i64 256)");
                if (methodName == "input_word")
                {
                    // scanf("%255s", buffer) - reads word (stops at whitespace)
                    _output.AppendLine($"  {resultTemp} = call i32 (i8*, ...) @scanf(i8* getelementptr inbounds ([6 x i8], [6 x i8]* @.str_word_fmt, i32 0, i32 0), i8* {bufferTemp})");
                }
                else
                {
                    // For input_line, we'd use fgets but need stdin pointer
                    // Simplified: use scanf with %[^\n] pattern
                    _output.AppendLine($"  {resultTemp} = call i32 (i8*, ...) @scanf(i8* getelementptr inbounds ([7 x i8], [7 x i8]* @.str_line_fmt, i32 0, i32 0), i8* {bufferTemp})");
                }
                // Register the buffer as a string type
                _tempTypes[bufferTemp] = new TypeInfo("i8*", false, false, "Text");
                return bufferTemp; // Return the buffer pointer

            default:
                // Unknown Console method - generate a placeholder call
                _output.AppendLine($"  ; Unknown Console.{methodName} - not implemented");
                _output.AppendLine($"  {resultTemp} = add i32 0, 0");
                return resultTemp;
        }
    }

    /// <summary>
    /// Handles Error.* calls for error creation and handling.
    /// </summary>
    private string HandleErrorCall(string methodName, List<Expression> arguments, string resultTemp)
    {
        switch (methodName)
        {
            case "from_text":
                // Error.from_text(message) - for now, just return the message pointer as the "error"
                // In a full implementation, this would create an error struct
                if (arguments.Count > 0)
                {
                    string msgValue = arguments[0].Accept(visitor: this);
                    // Just pass through the message pointer as the error value
                    // The throw statement will handle it appropriately
                    _tempTypes[resultTemp] = new TypeInfo("i8*", false, false, "Error");
                    _output.AppendLine($"  {resultTemp} = bitcast i8* {msgValue} to i8*"); // identity cast
                    return resultTemp;
                }
                _output.AppendLine($"  {resultTemp} = bitcast i8* null to i8*");
                return resultTemp;

            default:
                // Unknown Error method
                _output.AppendLine($"  ; Unknown Error.{methodName} - not implemented");
                _output.AppendLine($"  {resultTemp} = inttoptr i32 0 to i8*");
                return resultTemp;
        }
    }

    /// <summary>
    /// Checks if a sanitized function name is a type constructor call.
    /// Matches patterns: TypeName___create___throwable (from TypeName!) or try_TypeName___create__ (from TypeName?)
    /// </summary>
    private bool IsTypeConstructorCall(string sanitizedName)
    {
        return sanitizedName.EndsWith("___create___throwable") ||
               (sanitizedName.StartsWith("try_") && sanitizedName.EndsWith("___create__"));
    }

    /// <summary>
    /// Handles type constructor calls like s32___create___throwable (from s32!) or try_s32___create__ (from s32?).
    /// Uses C runtime functions like strtol for parsing.
    /// </summary>
    private string HandleTypeConstructorCall(string functionName, List<Expression> arguments, string resultTemp)
    {
        // Extract the base type from the function name
        // Patterns: s32___create___throwable (from s32!) or try_s32___create__ (from s32?)
        string baseType;
        bool isThrowable;

        if (functionName.EndsWith("___create___throwable"))
        {
            baseType = functionName[..^"___create___throwable".Length];
            isThrowable = true;
        }
        else if (functionName.StartsWith("try_") && functionName.EndsWith("___create__"))
        {
            baseType = functionName["try_".Length..^"___create__".Length];
            isThrowable = false;
        }
        else
        {
            // Unknown pattern
            _output.AppendLine($"  ; Unknown type constructor: {functionName}");
            _output.AppendLine($"  {resultTemp} = add i32 0, 0");
            return resultTemp;
        }

        // Get the string argument
        string strArg = arguments.Count > 0 ? arguments[0].Accept(visitor: this) : "null";

        // Handle different base types
        switch (baseType)
        {
            case "s32":
            case "s64":
            case "s16":
            case "s8":
                // Use strtol for signed integer parsing
                // strtol(str, NULL, 10) - parse as base 10
                string longResult = GetNextTemp();
                _output.AppendLine($"  {longResult} = call i64 @strtol(i8* {strArg}, i8** null, i32 10)");
                // Truncate to appropriate size
                string llvmType = baseType switch
                {
                    "s8" => "i8",
                    "s16" => "i16",
                    "s32" => "i32",
                    "s64" => "i64",
                    _ => "i32"
                };
                if (llvmType == "i64")
                {
                    _output.AppendLine($"  {resultTemp} = add i64 {longResult}, 0"); // identity
                }
                else
                {
                    _output.AppendLine($"  {resultTemp} = trunc i64 {longResult} to {llvmType}");
                }
                return resultTemp;

            case "u32":
            case "u64":
            case "u16":
            case "u8":
                // Use strtol and cast to unsigned
                string ulongResult = GetNextTemp();
                _output.AppendLine($"  {ulongResult} = call i64 @strtol(i8* {strArg}, i8** null, i32 10)");
                string uLlvmType = baseType switch
                {
                    "u8" => "i8",
                    "u16" => "i16",
                    "u32" => "i32",
                    "u64" => "i64",
                    _ => "i32"
                };
                if (uLlvmType == "i64")
                {
                    _output.AppendLine($"  {resultTemp} = add i64 {ulongResult}, 0");
                }
                else
                {
                    _output.AppendLine($"  {resultTemp} = trunc i64 {ulongResult} to {uLlvmType}");
                }
                return resultTemp;

            default:
                // Unknown type - return 0
                _output.AppendLine($"  ; Unknown type constructor for: {baseType}");
                _output.AppendLine($"  {resultTemp} = add i32 0, 0");
                return resultTemp;
        }
    }

    private string HandleDangerZoneFunction(GenericMethodCallExpression node, string functionName,
        string llvmType, string typeName, string resultTemp)
    {
        switch (functionName)
        {
            case "write_as":
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string writeValueTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "read_as":
                string readAddrTemp = node.Arguments[index: 0]
                                          .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName), IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: typeName);
                return resultTemp;

            case "volatile_write":
                string volWriteAddrTemp = node.Arguments[index: 0]
                                              .Accept(visitor: this);
                string volWriteValueTemp = node.Arguments[index: 1]
                                               .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "volatile_read":
                string volReadAddrTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                _output.AppendLine(
                    handler: $"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new TypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName), IsUnsigned: false,
                    IsFloatingPoint: false, RazorForgeType: typeName);
                return resultTemp;

            default:
                throw new NotImplementedException(
                    message:
                    $"Danger zone function {functionName} not implemented in LLVM generator");
        }
    }

    private bool IsNonGenericDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "address_of" or "invalidate" => true,
            _ => false
        };
    }

    private string HandleNonGenericDangerZoneFunction(CallExpression node, string functionName,
        string resultTemp)
    {
        switch (functionName)
        {
            case "address_of":
                // address_of!(variable) -> uaddr (address of variable)
                // Expects a single identifier argument
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException(
                        message:
                        $"address_of! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                Expression argument = node.Arguments[index: 0];
                if (argument is IdentifierExpression varIdent)
                {
                    // Generate ptrtoint to get address of variable
                    _output.AppendLine(
                        handler: $"  {resultTemp} = ptrtoint ptr %{varIdent.Name} to i64");
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                        IsFloatingPoint: true, RazorForgeType: "uaddr"); // uaddr is unsigned
                    return resultTemp;
                }
                else
                {
                    // Handle complex expressions by first evaluating them
                    string argTemp = argument.Accept(visitor: this);
                    _output.AppendLine(handler: $"  {resultTemp} = ptrtoint ptr {argTemp} to i64");
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false,
                        IsFloatingPoint: true, RazorForgeType: "uaddr"); // uaddr is unsigned
                    return resultTemp;
                }

            case "invalidate":
                // invalidate!(slice) -> void (free memory)
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException(
                        message:
                        $"invalidate! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                Expression sliceArgument = node.Arguments[index: 0];
                // Evaluate the argument and then call heap_free on it
                string sliceTemp = sliceArgument.Accept(visitor: this);
                _output.AppendLine(handler: $"  call void @heap_free(ptr {sliceTemp})");
                return ""; // void return

            default:
                throw new NotImplementedException(
                    message:
                    $"Non-generic danger zone function {functionName} not implemented in LLVM generator");
        }
    }

    /// <summary>
    /// Generates LLVM IR for a viewing statement (scoped read-only access).
    ///
    /// For single-threaded code, Viewed&lt;T&gt; is a compile-time concept -
    /// the handle is simply an alias to the source pointer. No runtime locks needed.
    ///
    /// Syntax: viewing source as handle { body }
    ///
    /// Generated IR:
    /// 1. Evaluate source expression to get pointer
    /// 2. Create handle as alias to source pointer
    /// 3. Execute body (handle provides read-only access)
    /// 4. Handle goes out of scope (no cleanup needed for single-threaded)
    /// </summary>
    public string VisitViewingStatement(ViewingStatement node)
    {
        // Evaluate the source expression to get a pointer
        string sourcePtr = node.Source.Accept(visitor: this);

        // Get the type of the source
        string sourceType = "ptr";
        if (node.Source is IdentifierExpression sourceId && _symbolTypes.ContainsKey(sourceId.Name))
        {
            sourceType = _symbolTypes[sourceId.Name];
        }

        // Create the handle as an alias to the source pointer
        // For single-threaded Viewed<T>, this is just a pointer copy
        string handleName = $"%{node.Handle}";
        _symbolTypes[node.Handle] = sourceType;

        // The handle is just an alias - no alloca needed, just use the same pointer
        // For stack-allocated values, we need to load the address
        if (sourcePtr.StartsWith("%") && !sourcePtr.StartsWith("%t"))
        {
            // Source is a named variable - the handle points to same location
            _output.AppendLine($"  ; viewing {node.Source} as {node.Handle} - read-only alias");
            _output.AppendLine($"  {handleName} = bitcast {sourceType}* {sourcePtr} to {sourceType}*");
        }
        else
        {
            // Source is a temporary or literal - store in alloca for handle
            _output.AppendLine($"  ; viewing expression as {node.Handle} - read-only access");
            _output.AppendLine($"  {handleName} = alloca {sourceType}");
            _output.AppendLine($"  store {sourceType} {sourcePtr}, {sourceType}* {handleName}");
        }

        // Execute the body with the handle available
        node.Body.Accept(visitor: this);

        // No cleanup needed for single-threaded viewing
        _output.AppendLine($"  ; end viewing {node.Handle}");

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for a hijacking statement (scoped exclusive access).
    ///
    /// For single-threaded code, Hijacked&lt;T&gt; is a compile-time concept -
    /// the handle is an alias to the source pointer with exclusive access.
    /// The semantic analyzer ensures no aliasing violations at compile time.
    ///
    /// Syntax: hijacking source as handle { body }
    ///
    /// Generated IR:
    /// 1. Evaluate source expression to get pointer
    /// 2. Create handle as alias to source pointer (exclusive access enforced at compile time)
    /// 3. Execute body (handle provides read/write access)
    /// 4. Handle goes out of scope (source is restored - compile-time guarantee)
    /// </summary>
    public string VisitHijackingStatement(HijackingStatement node)
    {
        // Evaluate the source expression to get a pointer
        string sourcePtr = node.Source.Accept(visitor: this);

        // Get the type of the source
        string sourceType = "ptr";
        if (node.Source is IdentifierExpression sourceId && _symbolTypes.ContainsKey(sourceId.Name))
        {
            sourceType = _symbolTypes[sourceId.Name];
        }

        // Create the handle as an alias to the source pointer
        // For single-threaded Hijacked<T>, this is just a pointer with exclusive access
        string handleName = $"%{node.Handle}";
        _symbolTypes[node.Handle] = sourceType;

        // The handle is an exclusive alias - same pointer, exclusive access enforced at compile time
        if (sourcePtr.StartsWith("%") && !sourcePtr.StartsWith("%t"))
        {
            // Source is a named variable - the handle points to same location
            _output.AppendLine($"  ; hijacking {node.Source} as {node.Handle} - exclusive access");
            _output.AppendLine($"  {handleName} = bitcast {sourceType}* {sourcePtr} to {sourceType}*");
        }
        else
        {
            // Source is a temporary or literal - store in alloca for handle
            _output.AppendLine($"  ; hijacking expression as {node.Handle} - exclusive access");
            _output.AppendLine($"  {handleName} = alloca {sourceType}");
            _output.AppendLine($"  store {sourceType} {sourcePtr}, {sourceType}* {handleName}");
        }

        // Execute the body with the handle available (exclusive read/write access)
        node.Body.Accept(visitor: this);

        // No runtime cleanup needed - source restoration is a compile-time guarantee
        _output.AppendLine($"  ; end hijacking {node.Handle} - source restored");

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for an observing statement (thread-safe scoped read access).
    ///
    /// For multi-threaded code with Shared&lt;T, MultiReadLock&gt;, this acquires a read lock.
    /// Multiple observers can coexist (RwLock semantics).
    ///
    /// Syntax: observing shared as handle { body }
    ///
    /// Generated IR:
    /// 1. Call runtime to acquire read lock on Shared
    /// 2. Create handle pointing to inner data
    /// 3. Execute body
    /// 4. Call runtime to release read lock
    /// </summary>
    public string VisitObservingStatement(ObservingStatement node)
    {
        // Evaluate the source (should be a Shared<T, Policy>)
        string sourcePtr = node.Source.Accept(visitor: this);

        string handleName = $"%{node.Handle}";

        _output.AppendLine($"  ; observing {node.Source} as {node.Handle} - acquiring read lock");

        // Call runtime to acquire read lock
        // The runtime function returns a pointer to the inner data
        string innerPtr = GetNextTemp();
        _output.AppendLine($"  {innerPtr} = call ptr @razorforge_rwlock_read_lock(ptr {sourcePtr})");

        // Create handle as alias to inner data
        _output.AppendLine($"  {handleName} = bitcast ptr {innerPtr} to ptr");
        _symbolTypes[node.Handle] = "ptr";

        // Execute the body with read access
        node.Body.Accept(visitor: this);

        // Release the read lock
        _output.AppendLine($"  call void @razorforge_rwlock_read_unlock(ptr {sourcePtr})");
        _output.AppendLine($"  ; end observing {node.Handle} - read lock released");

        return "";
    }

    /// <summary>
    /// Generates LLVM IR for a seizing statement (thread-safe scoped exclusive access).
    ///
    /// For multi-threaded code with Shared&lt;T, Policy&gt;, this acquires an exclusive lock.
    /// - Mutex policy: Simple mutex lock
    /// - MultiReadLock policy: Write lock (blocks all readers and writers)
    ///
    /// Syntax: seizing shared as handle { body }
    ///
    /// Generated IR:
    /// 1. Call runtime to acquire exclusive lock on Shared
    /// 2. Create handle pointing to inner data
    /// 3. Execute body
    /// 4. Call runtime to release exclusive lock
    /// </summary>
    public string VisitSeizingStatement(SeizingStatement node)
    {
        // Evaluate the source (should be a Shared<T, Policy>)
        string sourcePtr = node.Source.Accept(visitor: this);

        string handleName = $"%{node.Handle}";

        _output.AppendLine($"  ; seizing {node.Source} as {node.Handle} - acquiring exclusive lock");

        // Call runtime to acquire exclusive lock
        // The runtime function returns a pointer to the inner data
        string innerPtr = GetNextTemp();
        _output.AppendLine($"  {innerPtr} = call ptr @razorforge_mutex_lock(ptr {sourcePtr})");

        // Create handle as alias to inner data
        _output.AppendLine($"  {handleName} = bitcast ptr {innerPtr} to ptr");
        _symbolTypes[node.Handle] = "ptr";

        // Execute the body with exclusive access
        node.Body.Accept(visitor: this);

        // Release the exclusive lock
        _output.AppendLine($"  call void @razorforge_mutex_unlock(ptr {sourcePtr})");
        _output.AppendLine($"  ; end seizing {node.Handle} - exclusive lock released");

        return "";
    }

    #region Intrinsic Helper Methods

    private string EmitMemoryIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "i64";

        switch (intrinsicName)
        {
            case "load":
                {
                    string addrTemp = node.Arguments[0].Accept(this);
                    string ptrTemp = GetNextTemp();
                    _output.AppendLine($"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                    _output.AppendLine($"  {resultTemp} = load {llvmType}, ptr {ptrTemp}");
                    _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: false,
                        IsFloatingPoint: llvmType.Contains("float") || llvmType.Contains("double"),
                        RazorForgeType: node.TypeArguments[0]);
                    return resultTemp;
                }

            case "store":
                {
                    string addrTemp = node.Arguments[0].Accept(this);
                    string valueTemp = node.Arguments[1].Accept(this);
                    string ptrTemp = GetNextTemp();
                    _output.AppendLine($"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                    _output.AppendLine($"  store {llvmType} {valueTemp}, ptr {ptrTemp}");
                    return ""; // void
                }

            case "volatile_load":
                {
                    string addrTemp = node.Arguments[0].Accept(this);
                    string ptrTemp = GetNextTemp();
                    _output.AppendLine($"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                    _output.AppendLine($"  {resultTemp} = load volatile {llvmType}, ptr {ptrTemp}");
                    _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: false,
                        IsFloatingPoint: llvmType.Contains("float") || llvmType.Contains("double"),
                        RazorForgeType: node.TypeArguments[0]);
                    return resultTemp;
                }

            case "volatile_store":
                {
                    string addrTemp = node.Arguments[0].Accept(this);
                    string valueTemp = node.Arguments[1].Accept(this);
                    string ptrTemp = GetNextTemp();
                    _output.AppendLine($"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                    _output.AppendLine($"  store volatile {llvmType} {valueTemp}, ptr {ptrTemp}");
                    return ""; // void
                }

            case "bitcast":
                {
                    string valueTemp = node.Arguments[0].Accept(this);
                    string fromType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]);
                    string toType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[1]);

                    // Allocate space for source type
                    string srcPtr = GetNextTemp();
                    _output.AppendLine($"  {srcPtr} = alloca {fromType}");
                    _output.AppendLine($"  store {fromType} {valueTemp}, ptr {srcPtr}");

                    // Load as destination type
                    _output.AppendLine($"  {resultTemp} = load {toType}, ptr {srcPtr}");
                    _tempTypes[resultTemp] = new TypeInfo(LLVMType: toType, IsUnsigned: false,
                        IsFloatingPoint: toType.Contains("float") || toType.Contains("double"),
                        RazorForgeType: node.TypeArguments[1]);
                    return resultTemp;
                }

            case "invalidate":
                {
                    string addrTemp = node.Arguments[0].Accept(this);
                    string ptrTemp = GetNextTemp();
                    _output.AppendLine($"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");
                    _output.AppendLine($"  call void @free(ptr {ptrTemp})");
                    return ""; // void
                }

            default:
                throw new NotImplementedException($"Memory intrinsic {intrinsicName} not implemented");
        }
    }

    private string EmitArithmeticIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "i32";

        // Determine if type is unsigned or signed
        bool isUnsigned = node.TypeArguments.Count > 0 && node.TypeArguments[0].StartsWith("u");
        bool isFloat = llvmType.Contains("float") || llvmType.Contains("double") || llvmType.Contains("half") || llvmType.Contains("fp128");

        // Handle unary neg operation
        if (intrinsicName == "neg")
        {
            string valueTemp = node.Arguments[0].Accept(this);
            if (isFloat)
            {
                _output.AppendLine($"  {resultTemp} = fneg {llvmType} {valueTemp}");
            }
            else
            {
                _output.AppendLine($"  {resultTemp} = sub {llvmType} 0, {valueTemp}");
            }
            _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat, RazorForgeType: node.TypeArguments[0]);
            return resultTemp;
        }

        // Binary operations need two arguments
        string leftTemp = node.Arguments[0].Accept(this);
        string rightTemp = node.Arguments[1].Accept(this);

        // Basic arithmetic (trapping on overflow for integers, IEEE for floats)
        // For integers, we use overflow intrinsics and trap if overflow occurs
        if (intrinsicName == "add")
        {
            if (isFloat)
            {
                _output.AppendLine($"  {resultTemp} = fadd {llvmType} {leftTemp}, {rightTemp}");
            }
            else
            {
                // Use overflow intrinsic and trap on overflow
                string llvmFunc = isUnsigned ? $"@llvm.uadd.with.overflow.{llvmType}" : $"@llvm.sadd.with.overflow.{llvmType}";
                string structTemp = GetNextTemp();
                _output.AppendLine($"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine($"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine($"  {overflowFlag} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");
                // Trap on overflow
                string trapLabel = $"trap.add.{_tempCounter}";
                string contLabel = $"cont.add.{_tempCounter}";
                _output.AppendLine($"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine($"{trapLabel}:");
                _output.AppendLine($"  call void @llvm.trap()");
                _output.AppendLine($"  unreachable");
                _output.AppendLine($"{contLabel}:");
            }
        }
        else if (intrinsicName == "sub")
        {
            if (isFloat)
            {
                _output.AppendLine($"  {resultTemp} = fsub {llvmType} {leftTemp}, {rightTemp}");
            }
            else
            {
                string llvmFunc = isUnsigned ? $"@llvm.usub.with.overflow.{llvmType}" : $"@llvm.ssub.with.overflow.{llvmType}";
                string structTemp = GetNextTemp();
                _output.AppendLine($"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine($"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine($"  {overflowFlag} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");
                string trapLabel = $"trap.sub.{_tempCounter}";
                string contLabel = $"cont.sub.{_tempCounter}";
                _output.AppendLine($"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine($"{trapLabel}:");
                _output.AppendLine($"  call void @llvm.trap()");
                _output.AppendLine($"  unreachable");
                _output.AppendLine($"{contLabel}:");
            }
        }
        else if (intrinsicName == "mul")
        {
            if (isFloat)
            {
                _output.AppendLine($"  {resultTemp} = fmul {llvmType} {leftTemp}, {rightTemp}");
            }
            else
            {
                string llvmFunc = isUnsigned ? $"@llvm.umul.with.overflow.{llvmType}" : $"@llvm.smul.with.overflow.{llvmType}";
                string structTemp = GetNextTemp();
                _output.AppendLine($"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
                _output.AppendLine($"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
                string overflowFlag = GetNextTemp();
                _output.AppendLine($"  {overflowFlag} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");
                string trapLabel = $"trap.mul.{_tempCounter}";
                string contLabel = $"cont.mul.{_tempCounter}";
                _output.AppendLine($"  br i1 {overflowFlag}, label %{trapLabel}, label %{contLabel}");
                _output.AppendLine($"{trapLabel}:");
                _output.AppendLine($"  call void @llvm.trap()");
                _output.AppendLine($"  unreachable");
                _output.AppendLine($"{contLabel}:");
            }
        }
        else if (intrinsicName == "sdiv")
        {
            _output.AppendLine($"  {resultTemp} = sdiv {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "udiv")
        {
            _output.AppendLine($"  {resultTemp} = udiv {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "srem")
        {
            _output.AppendLine($"  {resultTemp} = srem {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "urem")
        {
            _output.AppendLine($"  {resultTemp} = urem {llvmType} {leftTemp}, {rightTemp}");
        }
        // Wrapping arithmetic (no overflow checks - uses LLVM's default wrapping behavior)
        else if (intrinsicName == "add.wrapping")
        {
            _output.AppendLine($"  {resultTemp} = {(isFloat ? "fadd" : "add")} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "sub.wrapping")
        {
            _output.AppendLine($"  {resultTemp} = {(isFloat ? "fsub" : "sub")} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "mul.wrapping")
        {
            _output.AppendLine($"  {resultTemp} = {(isFloat ? "fmul" : "mul")} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "div.wrapping")
        {
            string divOp = isFloat ? "fdiv" : (isUnsigned ? "udiv" : "sdiv");
            _output.AppendLine($"  {resultTemp} = {divOp} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "rem.wrapping")
        {
            string remOp = isFloat ? "frem" : (isUnsigned ? "urem" : "srem");
            _output.AppendLine($"  {resultTemp} = {remOp} {llvmType} {leftTemp}, {rightTemp}");
        }
        // Overflow-checking arithmetic
        else if (intrinsicName == "add.overflow" || intrinsicName == "sub.overflow" || intrinsicName == "mul.overflow")
        {
            string op = intrinsicName.Split('.')[0]; // "add", "sub", or "mul"
            string llvmFunc = isUnsigned ? $"@llvm.u{op}.with.overflow.{llvmType}" : $"@llvm.s{op}.with.overflow.{llvmType}";

            string structTemp = GetNextTemp();
            _output.AppendLine($"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");

            string valueTemp = GetNextTemp();
            string overflowTemp = GetNextTemp();
            _output.AppendLine($"  {valueTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
            _output.AppendLine($"  {overflowTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 1");

            // For now, just return the value (tuple support would need more work)
            _tempTypes[valueTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat, RazorForgeType: node.TypeArguments[0]);
            return valueTemp;
        }
        // Saturating arithmetic
        else if (intrinsicName == "add.saturating")
        {
            string llvmFunc = isUnsigned ? $"@llvm.uadd.sat.{llvmType}" : $"@llvm.sadd.sat.{llvmType}";
            _output.AppendLine($"  {resultTemp} = call {llvmType} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
        }
        else if (intrinsicName == "sub.saturating")
        {
            string llvmFunc = isUnsigned ? $"@llvm.usub.sat.{llvmType}" : $"@llvm.ssub.sat.{llvmType}";
            _output.AppendLine($"  {resultTemp} = call {llvmType} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
        }
        else if (intrinsicName == "mul.saturating")
        {
            // LLVM doesn't have direct saturating multiply, so we use overflow detection
            // For now, use a placeholder that traps on overflow (TODO: implement proper saturation)
            string llvmFunc = isUnsigned ? $"@llvm.umul.with.overflow.{llvmType}" : $"@llvm.smul.with.overflow.{llvmType}";
            string structTemp = GetNextTemp();
            _output.AppendLine($"  {structTemp} = call {{ {llvmType}, i1 }} {llvmFunc}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
            _output.AppendLine($"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {structTemp}, 0");
            // TODO: Check overflow flag and saturate to MAX/MIN
        }
        else
        {
            throw new NotImplementedException($"Arithmetic intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloat, RazorForgeType: node.TypeArguments[0]);
        return resultTemp;
    }

    private string EmitComparisonIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "i32";

        string leftTemp = node.Arguments[0].Accept(this);
        string rightTemp = node.Arguments[1].Accept(this);

        // Extract comparison predicate from intrinsic name
        // e.g., "icmp.eq", "icmp.slt", "fcmp.oeq"
        string[] parts = intrinsicName.Split('.');
        string cmpType = parts[0]; // "icmp" or "fcmp"
        string predicate = parts[1]; // "eq", "slt", "oeq", etc.

        if (cmpType == "icmp")
        {
            _output.AppendLine($"  {resultTemp} = icmp {predicate} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (cmpType == "fcmp")
        {
            _output.AppendLine($"  {resultTemp} = fcmp {predicate} {llvmType} {leftTemp}, {rightTemp}");
        }

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: "i1", IsUnsigned: false,
            IsFloatingPoint: false, RazorForgeType: "bool");
        return resultTemp;
    }

    private string EmitBitwiseIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "i32";

        if (intrinsicName == "not")
        {
            string valueTemp = node.Arguments[0].Accept(this);
            _output.AppendLine($"  {resultTemp} = xor {llvmType} {valueTemp}, -1");
        }
        else if (intrinsicName == "and" || intrinsicName == "or" || intrinsicName == "xor")
        {
            string leftTemp = node.Arguments[0].Accept(this);
            string rightTemp = node.Arguments[1].Accept(this);
            _output.AppendLine($"  {resultTemp} = {intrinsicName} {llvmType} {leftTemp}, {rightTemp}");
        }
        else if (intrinsicName == "shl" || intrinsicName == "lshr" || intrinsicName == "ashr")
        {
            string valueTemp = node.Arguments[0].Accept(this);
            string amountTemp = node.Arguments[1].Accept(this);
            _output.AppendLine($"  {resultTemp} = {intrinsicName} {llvmType} {valueTemp}, {amountTemp}");
        }
        else
        {
            throw new NotImplementedException($"Bitwise intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: false,
            IsFloatingPoint: false, RazorForgeType: node.TypeArguments[0]);
        return resultTemp;
    }

    private string EmitConversionIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string fromType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]);
        string toType = MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[1]);

        string valueTemp = node.Arguments[0].Accept(this);

        _output.AppendLine($"  {resultTemp} = {intrinsicName} {fromType} {valueTemp} to {toType}");

        bool isFloat = toType.Contains("float") || toType.Contains("double") || toType.Contains("half");
        bool isUnsigned = node.TypeArguments[1].StartsWith("u");

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: toType, IsUnsigned: isUnsigned,
            IsFloatingPoint: isFloat, RazorForgeType: node.TypeArguments[1]);
        return resultTemp;
    }

    private string EmitMathIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "double";

        if (intrinsicName == "sqrt" || intrinsicName == "fabs" || intrinsicName == "floor" ||
            intrinsicName == "ceil" || intrinsicName == "trunc_float" || intrinsicName == "round" ||
            intrinsicName == "exp" || intrinsicName == "log" || intrinsicName == "log10" ||
            intrinsicName == "sin" || intrinsicName == "cos")
        {
            string valueTemp = node.Arguments[0].Accept(this);
            string llvmFunc = intrinsicName == "trunc_float" ? "trunc" : intrinsicName;
            _output.AppendLine($"  {resultTemp} = call {llvmType} @llvm.{llvmFunc}.{llvmType}({llvmType} {valueTemp})");
        }
        else if (intrinsicName == "abs")
        {
            string valueTemp = node.Arguments[0].Accept(this);
            _output.AppendLine($"  {resultTemp} = call {llvmType} @llvm.abs.{llvmType}({llvmType} {valueTemp}, i1 false)");
        }
        else if (intrinsicName == "copysign" || intrinsicName == "pow")
        {
            string leftTemp = node.Arguments[0].Accept(this);
            string rightTemp = node.Arguments[1].Accept(this);
            _output.AppendLine($"  {resultTemp} = call {llvmType} @llvm.{intrinsicName}.{llvmType}({llvmType} {leftTemp}, {llvmType} {rightTemp})");
        }
        else
        {
            throw new NotImplementedException($"Math intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: false,
            IsFloatingPoint: true, RazorForgeType: node.TypeArguments[0]);
        return resultTemp;
    }

    private string EmitAtomicIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "i64";

        string addrTemp = node.Arguments[0].Accept(this);
        string ptrTemp = GetNextTemp();
        _output.AppendLine($"  {ptrTemp} = inttoptr i64 {addrTemp} to ptr");

        if (intrinsicName == "atomic.load")
        {
            _output.AppendLine($"  {resultTemp} = load atomic {llvmType}, ptr {ptrTemp} seq_cst, align 8");
        }
        else if (intrinsicName == "atomic.store")
        {
            string valueTemp = node.Arguments[1].Accept(this);
            _output.AppendLine($"  store atomic {llvmType} {valueTemp}, ptr {ptrTemp} seq_cst, align 8");
            return ""; // void
        }
        else if (intrinsicName == "atomic.add" || intrinsicName == "atomic.sub" || intrinsicName == "atomic.xchg")
        {
            string valueTemp = node.Arguments[1].Accept(this);
            string op = intrinsicName.Split('.')[1]; // "add", "sub", "xchg"
            _output.AppendLine($"  {resultTemp} = atomicrmw {op} ptr {ptrTemp}, {llvmType} {valueTemp} seq_cst");
        }
        else if (intrinsicName == "atomic.cmpxchg")
        {
            string expectedTemp = node.Arguments[1].Accept(this);
            string desiredTemp = node.Arguments[2].Accept(this);

            string cmpxchgTemp = GetNextTemp();
            _output.AppendLine($"  {cmpxchgTemp} = cmpxchg ptr {ptrTemp}, {llvmType} {expectedTemp}, {llvmType} {desiredTemp} seq_cst seq_cst");

            // Extract old value and success flag (tuple support needed)
            _output.AppendLine($"  {resultTemp} = extractvalue {{ {llvmType}, i1 }} {cmpxchgTemp}, 0");
        }
        else
        {
            throw new NotImplementedException($"Atomic intrinsic {intrinsicName} not implemented");
        }

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: false,
            IsFloatingPoint: false, RazorForgeType: node.TypeArguments[0]);
        return resultTemp;
    }

    private string EmitBitManipIntrinsic(IntrinsicCallExpression node, string resultTemp)
    {
        string intrinsicName = node.IntrinsicName;
        string llvmType = node.TypeArguments.Count > 0 ? MapRazorForgeTypeToLLVM(razorForgeType: node.TypeArguments[0]) : "i32";

        string valueTemp = node.Arguments[0].Accept(this);

        if (intrinsicName == "ctlz" || intrinsicName == "cttz")
        {
            _output.AppendLine($"  {resultTemp} = call {llvmType} @llvm.{intrinsicName}.{llvmType}({llvmType} {valueTemp}, i1 false)");
        }
        else
        {
            _output.AppendLine($"  {resultTemp} = call {llvmType} @llvm.{intrinsicName}.{llvmType}({llvmType} {valueTemp})");
        }

        _tempTypes[resultTemp] = new TypeInfo(LLVMType: llvmType, IsUnsigned: false,
            IsFloatingPoint: false, RazorForgeType: node.TypeArguments[0]);
        return resultTemp;
    }

    #endregion

    /// <summary>
    /// Generates LLVM IR for a named argument expression (name: value).
    /// For LLVM IR, named arguments are just their values - the naming is handled at the call site.
    /// </summary>
    public string VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        // For LLVM IR, we just generate the value - argument matching happens at the call site
        return node.Value.Accept(visitor: this);
    }
}
