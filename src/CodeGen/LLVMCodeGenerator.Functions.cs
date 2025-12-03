using Compilers.Shared.Analysis;
using Compilers.Shared.AST;
using Compilers.Shared.Errors;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Visits the program node in the Abstract Syntax Tree (AST), processes its declarations,
    /// and generates the corresponding LLVM IR while handling any pending generic function instantiations.
    /// </summary>
    /// <param name="node">The program node representing the entry point of the AST, containing a collection of declarations.</param>
    /// <returns>A string representing the generated LLVM IR code for the program.</returns>
    public string VisitProgram(AST.Program node)
    {
        _currentLocation = node.Location;
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
    /// <summary>
    /// Visits a function declaration in the abstract syntax tree (AST) and generates the corresponding LLVM IR code.
    /// Handles both generic and non-generic functions by analyzing type parameters and substitutions.
    /// </summary>
    /// <param name="node">The function declaration node in the AST to be processed.</param>
    /// <returns>A string representation of the generated LLVM IR code for the function.</returns>
    public string VisitFunctionDeclaration(FunctionDeclaration node)
    {
        _currentLocation = node.Location;
        // Check if this is a generic function (has type parameters)
        if (node.GenericParameters is not { Count: > 0 })
        {
            return GenerateFunctionCode(node: node, typeSubstitutions: null);
        }

        // Store the template for later instantiation - don't generate code yet
        string templateKey = node.Name;
        _genericFunctionTemplates[key: templateKey] = node;
        _output.AppendLine(
            handler:
            $"; Generic function template: {node.Name}<{string.Join(separator: ", ", values: node.GenericParameters)}>");
        return "";

        // Non-generic function - generate code normally
    }

    /// <summary>
    /// Generates LLVM IR code for a function, optionally applying type substitutions for generic instantiation.
    /// </summary>
    /// <param name="node">The function declaration</param>
    /// <param name="typeSubstitutions">Map from type parameter names to concrete types (null for non-generic)</param>
    /// <param name="mangledName">Optional mangled name for generic instantiations</param>
    /// <returns>Empty string (output written to _output)</returns>
    private string GenerateFunctionCode(FunctionDeclaration node,
        Dictionary<string, string>? typeSubstitutions, string? mangledName = null)
    {
        _currentLocation = node.Location;
        string functionName = mangledName ?? SanitizeFunctionName(name: node.Name);

        // Special case: main() must return i32 for C ABI compatibility, even if declared as Blank
        bool isMain = functionName == "main";

        string returnType = node.ReturnType != null
            ?
            MapTypeWithSubstitution(typeName: GetFullTypeName(type: node.ReturnType),
                substitutions: typeSubstitutions)
            : isMain
                ? "i32"
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
                if (param.Type == null)
                {
                    throw CodeGenError.TypeResolutionFailed(
                        typeName: param.Name,
                        context: "function parameter must have a type annotation",
                        file: _currentFileName,
                        line: param.Location.Line,
                        column: param.Location.Column,
                        position: param.Location.Position);
                }
                string paramType = MapTypeWithSubstitution(typeName: GetFullTypeName(type: param.Type),
                    substitutions: typeSubstitutions);
                parameters.Add(item: $"{paramType} %{param.Name}");
                _symbolTypes[key: param.Name] = paramType;
                _functionParameters.Add(item: param.Name); // Mark as parameter

                // Also track the RazorForge type for method lookup
                _symbolRfTypes[key: param.Name] = GetFullTypeName(type: param.Type);
            }
        }

        string paramList = string.Join(separator: ", ", values: parameters);

        _output.AppendLine(handler: $"define {returnType} @{functionName}({paramList}) {{");
        _output.AppendLine(value: "entry:");

        // Initialize runtime (UTF-8 console on Windows, etc.)
        if (isMain)
        {
            _output.AppendLine(value: "  call void @rf_runtime_init()");
        }

        // Register file and routine for stack trace support
        uint fileId = _symbolTables.RegisterFile(filePath: _currentFileName ?? "<unknown>");
        uint routineId =
            _symbolTables.RegisterRoutine(routineName: _currentFunctionName ?? functionName);
        // For free functions, typeId is 0; for methods, it would be the enclosing type
        uint typeId = 0; // TODO: Set to enclosing type ID when inside record/entity/resident
        uint line = (uint)node.Location.Line;
        uint column = (uint)node.Location.Column;

        // Emit stack frame push at routine entry
        _stackTraceCodeGen?.EmitPushFrame(fileId: fileId,
            routineId: routineId,
            typeId: typeId,
            line: line,
            column: column);

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
            // Emit stack frame pop before return
            _stackTraceCodeGen?.EmitPopFrame();

            switch (returnType)
            {
                case "void":
                    _output.AppendLine(value: "  ret void");
                    break;
                case "i32":
                    _output.AppendLine(value: "  ret i32 0");
                    break;
            }
        }

        _output.AppendLine(value: "}");
        _output.AppendLine();

        return "";
    }
    /// <summary>
    /// Maps a type name to LLVM type, applying type substitutions for generic instantiation.
    /// </summary>
    private string MapTypeWithSubstitution(string typeName,
        Dictionary<string, string>? substitutions)
    {
        // Check if this is a type parameter that needs substitution
        if (substitutions != null &&
            substitutions.TryGetValue(key: typeName, value: out string? concreteType))
        {
            return MapTypeToLLVM(rfType: concreteType);
        }

        return MapTypeToLLVM(rfType: typeName);
    }


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
        if (!_genericFunctionTemplates.TryGetValue(key: functionName,
                value: out FunctionDeclaration? template))
        {
            // No template found - might be an external generic or error
            return functionName;
        }

        // Check if already instantiated
        var typeInfos = typeArguments.Select(selector: t =>
                                          new TypeInfo(Name: t,
                                              IsReference: false))
                                     .ToList();
        string mangledName = MonomorphizeFunctionName(baseName: functionName, typeArgs: typeInfos);

        if (IsAlreadyInstantiated(functionName: functionName, typeArgs: typeInfos))
        {
            return mangledName;
        }

        // Track this instantiation and queue for later generation
        TrackInstantiation(functionName: functionName, typeArgs: typeInfos);

        // Queue the instantiation data for later code generation
        _pendingGenericInstantiations.Add(
            item: $"{functionName}|{string.Join(separator: ",", values: typeArguments)}");

        return mangledName;
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
    private string MonomorphizeFunctionName(string baseName, List<TypeInfo> typeArgs)
    {
        if (typeArgs == null || typeArgs.Count == 0)
        {
            return baseName;
        }

        string suffix = string.Join(separator: "_",
            values: typeArgs.Select(selector: t => t.Name
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
    private bool IsAlreadyInstantiated(string functionName, List<TypeInfo> typeArgs)
    {
        if (!_genericInstantiations.TryGetValue(key: functionName,
                value: out List<List<TypeInfo>>? existingInstantiations))
        {
            return false;
        }

        foreach (List<TypeInfo> existing in existingInstantiations)
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
    private void TrackInstantiation(string functionName, List<TypeInfo> typeArgs)
    {
        if (!_genericInstantiations.ContainsKey(key: functionName))
        {
            _genericInstantiations[key: functionName] = new List<List<TypeInfo>>();
        }

        _genericInstantiations[key: functionName]
           .Add(item: [..typeArgs]);
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
            string[] parts = pending.Split(separator: '|');
            string functionName = parts[0];
            var typeArguments = parts[1]
                               .Split(separator: ',')
                               .ToList();

            if (!_genericFunctionTemplates.TryGetValue(key: functionName,
                    value: out FunctionDeclaration? template))
            {
                continue;
            }

            // Create type substitution map
            var substitutions = new Dictionary<string, string>();
            for (int i = 0;
                 i < Math.Min(val1: template.GenericParameters!.Count, val2: typeArguments.Count);
                 i++)
            {
                substitutions[key: template.GenericParameters[index: i]] = typeArguments[index: i];
            }

            // Generate the mangled name
            var typeInfos = typeArguments
                           .Select(selector: t =>
                                new TypeInfo(Name: t, IsReference: false))
                           .ToList();
            string mangledName =
                MonomorphizeFunctionName(baseName: functionName, typeArgs: typeInfos);

            // Generate the instantiated function code
            GenerateFunctionCode(node: template,
                typeSubstitutions: substitutions,
                mangledName: mangledName);
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
        _output.AppendLine(value: "; Lambda function definitions");

        foreach (string lambdaDefinition in _pendingLambdaDefinitions)
        {
            _output.Append(value: lambdaDefinition);
        }

        _pendingLambdaDefinitions.Clear();
    }
    /// <summary>
    /// Emits LLVM IR declare statements for external functions found in the semantic symbol table.
    /// These are functions declared with external("C") in imported modules.
    /// </summary>
    private void EmitExternalDeclarationsFromSymbolTable()
    {
        if (_semanticSymbolTable == null)
        {
            return;
        }

        _output.AppendLine(value: "; External function declarations from imports");

        // Get all external functions from the symbol table
        foreach (Symbol symbol in _semanticSymbolTable.GetAllSymbols())
        {
            if (symbol is FunctionSymbol funcSymbol && funcSymbol.IsExternal)
            {
                // Convert RazorForge types to LLVM IR types
                string returnType = funcSymbol.ReturnType != null
                    ? MapTypeToLLVM(rfType: funcSymbol.ReturnType.Name)
                    : "void";

                var paramTypes = new List<string>();
                foreach (Parameter param in funcSymbol.Parameters)
                {
                    if (param.Type == null)
                    {
                        throw CodeGenError.TypeResolutionFailed(
                            typeName: param.Name,
                            context: "external function parameter must have a type annotation",
                            file: _currentFileName,
                            line: _currentLocation.Line,
                            column: _currentLocation.Column,
                            position: _currentLocation.Position);
                    }
                    string paramType = MapTypeToLLVM(rfType: param.Type.Name);
                    paramTypes.Add(item: paramType);
                }

                string paramList = string.Join(separator: ", ", values: paramTypes);
                string funcName = funcSymbol.Name;

                _output.AppendLine(handler: $"declare {returnType} @{funcName}({paramList})");
            }
        }
    }

    /// <summary>
    /// Generates LLVM IR code for all functions from imported modules.
    /// This allows calling stdlib functions like Console.alert without hardcoding.
    /// </summary>
    private void GenerateImportedModuleFunctions()
    {
        if (_loadedModules == null || _loadedModules.Count == 0)
        {
            return;
        }

        _output.AppendLine();
        _output.AppendLine(value: "; ============================================");
        _output.AppendLine(value: "; Imported module types");
        _output.AppendLine(value: "; ============================================");

        // First pass: Process all struct/record declarations and presets to register them
        // This ensures record constructors and constants are available before processing functions
        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            ModuleResolver.ModuleInfo moduleInfo = moduleEntry.Value;

            foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
            {
                // Process struct declarations to register record types
                if (declaration is RecordDeclaration structDecl)
                {
                    structDecl.Accept(visitor: this);
                }
                // Process class declarations to register entity types
                else if (declaration is EntityDeclaration classDecl)
                {
                    classDecl.Accept(visitor: this);
                }
                // Process preset declarations (compile-time constants)
                else if (declaration is PresetDeclaration presetDecl)
                {
                    presetDecl.Accept(visitor: this);
                }
            }
        }

        // Generate any pending generic type instantiations that were queued during struct processing
        // This is needed because record types may reference generic types like Text<letter32>
        GeneratePendingTypeInstantiations();

        _output.AppendLine();
        _output.AppendLine(value: "; ============================================");
        _output.AppendLine(value: "; Imported module functions");
        _output.AppendLine(value: "; ============================================");

        // Track which functions we've already generated to avoid duplicates
        var generatedFunctions = new HashSet<string>();

        // Second pass: Process all function declarations
        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            string moduleName = moduleEntry.Key;
            ModuleResolver.ModuleInfo moduleInfo = moduleEntry.Value;

            _output.AppendLine(handler: $"; Module: {moduleName}");

            // Visit each declaration in the module
            foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
            {
                // Only generate code for function declarations (not external declarations)
                if (declaration is FunctionDeclaration funcDecl)
                {
                    // Skip if already generated
                    string funcName = SanitizeFunctionName(name: funcDecl.Name);
                    if (generatedFunctions.Contains(item: funcName))
                    {
                        continue;
                    }

                    generatedFunctions.Add(item: funcName);

                    // Track non-generic functions in the instance field for call resolution
                    if (funcDecl.GenericParameters == null ||
                        funcDecl.GenericParameters.Count == 0)
                    {
                        _generatedFunctions.Add(item: funcName);
                    }

                    // Generate the function
                    funcDecl.Accept(visitor: this);
                }
            }
        }

        _output.AppendLine(value: "; ============================================");
        _output.AppendLine();
    }
}
