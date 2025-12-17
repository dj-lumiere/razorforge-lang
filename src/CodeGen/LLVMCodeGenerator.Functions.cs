using System.Text;
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
        UpdateLocation(node.Location);
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
        UpdateLocation(node.Location);
        // Check if this is a generic function (has type parameters)
        if (node.GenericParameters is not { Count: > 0 })
        {
            return GenerateFunctionCode(node: node, typeSubstitutions: null);
        }

        // CRITICAL FIX: Check if the "generic parameters" are actually concrete types embedded in the function name
        // Example: `Text<letter8>.to_cstr` has genericParams=["letter8"] but letter8 is a concrete type,
        // not a type parameter. This happens because the parser can't distinguish between:
        //   - `Text<T>.method` (generic method on generic type)
        //   - `Text<letter8>.method` (specialized method on specialized generic type)
        // We detect this by checking if the type parameter appears in the function name as part of a generic type
        var actualGenericParams = new List<string>();
        foreach (string param in node.GenericParameters)
        {
            // Check if this parameter appears in the name as part of a generic type (e.g., "Text<letter8>")
            // If the function name contains "<param>" before the dot, it's part of the type name, not a generic parameter
            // Example: "Text<letter8>.to_cstr" - letter8 is part of type name
            // Example: "Text<T>.method<U>" - T is part of type name, U is a generic parameter
            int dotPos = node.Name.IndexOf('.');
            if (dotPos > 0)
            {
                string typePartOfName = node.Name.Substring(0, dotPos);
                if (!typePartOfName.Contains($"<{param}>"))
                {
                    // This parameter is NOT in the type part, so it's a real generic parameter (e.g., method-level generic)
                    actualGenericParams.Add(param);
                }
                // else: parameter is in the type part (e.g., Text<letter8>), so it's not a generic parameter
            }
            else
            {
                // No dot in name, this is a standalone generic function
                actualGenericParams.Add(param);
            }
        }

        // If no actual generic parameters remain, treat as non-generic
        if (actualGenericParams.Count == 0)
        {
            return GenerateFunctionCode(node: node, typeSubstitutions: null);
        }

        // Store the template for later instantiation - don't generate code yet
        string templateKey = node.Name;
        _genericFunctionTemplates[key: templateKey] = node;
        _output.AppendLine(
            handler:
            $"; Generic function template: {node.Name}<{string.Join(separator: ", ", values: actualGenericParams)}>");
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
        UpdateLocation(node.Location);
        string functionName = mangledName ?? SanitizeFunctionName(name: node.Name);

        // Special case: start() is the entry point, maps to LLVM main()
        // start() must return i32 for C ABI compatibility, even if declared as Blank
        bool isEntryPoint = functionName == "start";

        // Map start() to main() in LLVM IR
        string llvmFunctionName = isEntryPoint ? "main" : functionName;

        string returnType = node.ReturnType != null
            ?
            MapTypeWithSubstitution(typeName: GetFullTypeName(type: node.ReturnType),
                substitutions: typeSubstitutions)
            : isEntryPoint
                ? "i32"
                : "void";

        // Set the current function return type for return statement processing
        _currentFunctionReturnType = returnType;

        // Track current function name for CompilerService intrinsics
        _currentFunctionName = mangledName ?? node.Name;

        // Clear parameter tracking for this function
        _functionParameters.Clear();

        // Check if this is a method (has a dot in the name like "s64.__add__")
        // BUT: not a namespace function (like "Console.show")
        // If so, add implicit 'me' parameter of the receiver type
        bool isMethod = false;
        string? receiverType = null;

        if (functionName.Contains('.'))
        {
            // Extract potential receiver type/namespace from the ORIGINAL function name
            int dotIndex = node.Name.IndexOf('.');
            if (dotIndex >= 0)
            {
                string prefix = node.Name.Substring(0, dotIndex);

                // Check if the prefix is a registered namespace
                // If it's a namespace, this is a namespace-qualified function, not a method
                if (_semanticSymbolTable == null || !_semanticSymbolTable.IsNamespace(prefix))
                {
                    // It's a type method, not a namespace function
                    isMethod = true;
                    receiverType = prefix;

                    // Apply type parameter substitutions to the receiver type
                    // e.g., "TestType<T>" with {T: "s64"} becomes "TestType<s64>"
                    if (typeSubstitutions != null && receiverType.Contains('<') && receiverType.Contains('>'))
                    {
                        foreach (var kvp in typeSubstitutions)
                        {
                            receiverType = System.Text.RegularExpressions.Regex.Replace(
                                receiverType,
                                $@"\b{kvp.Key}\b",
                                kvp.Value);
                        }
                    }
                }
            }
        }

        var parameters = new List<string>();

        // Check if the function already has an explicit 'me' parameter
        bool hasExplicitMe = node.Parameters != null &&
                             node.Parameters.Any(p => p.Name == "me");

        // Add implicit 'me' parameter for methods (only if not already explicit)
        if (isMethod && receiverType != null && !hasExplicitMe)
        {
            string meType = MapTypeToLLVM(rfType: receiverType);
            // CRITICAL: Multi-field records receive 'me' by POINTER for efficiency
            // Single-field wrappers receive 'me' by VALUE (they're just primitives in a struct)
            bool meIsMultiField = false;
            if (meType.StartsWith("%"))
            {
                string recordName = meType.TrimStart('%');
                if (_recordFields.TryGetValue(recordName, out var fields) && fields.Count > 1)
                {
                    meIsMultiField = true;
                }
            }

            if (meIsMultiField)
            {
                parameters.Add(item: $"ptr %me");
                _symbolTypes[key: "me"] = "ptr"; // The LLVM type is ptr
            }
            else
            {
                parameters.Add(item: $"{meType} %me");
                _symbolTypes[key: "me"] = meType; // The LLVM type is the record type
            }
            _functionParameters.Add(item: "me");
            _symbolRfTypes[key: "me"] = receiverType; // But track the RazorForge type for method lookups
        }

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
                string paramTypeName = GetFullTypeName(type: param.Type);
                string paramType = MapTypeWithSubstitution(typeName: paramTypeName,
                    substitutions: typeSubstitutions);

                // CRITICAL: Multi-field record types (structs) are passed by POINTER for efficiency
                // Single-field record wrappers (like %uaddr = type { i64 }) are passed by VALUE
                // This matches the convention used for 'me' parameter
                bool isRecordType = paramType.StartsWith("%");
                bool passAsPointer = false;

                if (isRecordType)
                {
                    // Check if this is a multi-field struct
                    string recordName = paramType.TrimStart('%');
                    if (_recordFields.TryGetValue(recordName, out var fields) && fields.Count > 1)
                    {
                        passAsPointer = true;
                    }
                    // Single-field wrappers are passed by value
                }

                if (passAsPointer)
                {
                    parameters.Add(item: $"ptr %{param.Name}");
                    _symbolTypes[key: param.Name] = "ptr";
                }
                else
                {
                    parameters.Add(item: $"{paramType} %{param.Name}");
                    _symbolTypes[key: param.Name] = paramType;
                }
                _functionParameters.Add(item: param.Name); // Mark as parameter

                // Also track the RazorForge type for method lookup (with substitutions applied)
                string substitutedRfType = paramTypeName;
                if (typeSubstitutions != null && paramTypeName.Contains('<') && paramTypeName.Contains('>'))
                {
                    // Apply type parameter substitution to the RazorForge type name
                    foreach (var kvp in typeSubstitutions)
                    {
                        substitutedRfType = System.Text.RegularExpressions.Regex.Replace(
                            substitutedRfType,
                            $@"\b{kvp.Key}\b",
                            kvp.Value);
                    }
                }
                else if (typeSubstitutions != null && typeSubstitutions.TryGetValue(paramTypeName, out string? directSubst))
                {
                    // Simple type parameter substitution (e.g., I -> uaddr)
                    substitutedRfType = directSubst;
                }
                _symbolRfTypes[key: param.Name] = substitutedRfType;
            }
        }

        string paramList = string.Join(separator: ", ", values: parameters);

        _output.AppendLine(handler: $"define {returnType} @{llvmFunctionName}({paramList}) {{");
        _output.AppendLine(value: "entry:");

        // Initialize runtime (UTF-8 console on Windows, etc.)
        if (isEntryPoint)
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

        // Reset return and termination flags for this function
        _hasReturn = false;
        _blockTerminated = false;

        // Store type substitutions for use in body generation
        _currentTypeSubstitutions = typeSubstitutions;

        // Visit function body
        if (node.Body != null)
        {
            node.Body.Accept(visitor: this);
        }

        // Clear type substitutions
        _currentTypeSubstitutions = null;

        // CRITICAL: Add default return if the current block is not terminated
        // This handles cases where some paths return/throw but others don't
        // We check _blockTerminated instead of _hasReturn because a function can have
        // multiple paths (e.g., if-else, try-catch) and each needs a terminator
        if (!_blockTerminated)
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
        // Check if this is a simple type parameter that needs substitution
        if (substitutions != null &&
            substitutions.TryGetValue(key: typeName, value: out string? concreteType))
        {
            return MapTypeToLLVM(rfType: concreteType);
        }

        // Check if this is a generic type containing type parameters (e.g., BackIndex<I>)
        if (substitutions != null && typeName.Contains('<') && typeName.Contains('>'))
        {
            // Replace type parameters within the generic type
            // e.g., BackIndex<I> with {I: uaddr} becomes BackIndex<uaddr>
            string substitutedTypeName = typeName;
            foreach (var kvp in substitutions)
            {
                // Use word boundaries to avoid partial replacements
                // Replace I in BackIndex<I> but not in BackIndexInternal
                substitutedTypeName = System.Text.RegularExpressions.Regex.Replace(
                    substitutedTypeName,
                    $@"\b{kvp.Key}\b",
                    kvp.Value);
            }

            // If substitution occurred, map the new type
            if (substitutedTypeName != typeName)
            {
                return MapTypeToLLVM(rfType: substitutedTypeName);
            }
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
        // Make a copy to avoid concurrent modification during iteration
        foreach (string pending in _pendingGenericInstantiations.ToList())
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
            // For methods on generic types like "BackIndex<I>.resolve", we need to extract
            // the type parameters from the function name itself, not just from template.GenericParameters
            var substitutions = new Dictionary<string, string>();

            // Check if this is a method on a generic type (contains '<' before '.')
            if (functionName.Contains('<') && functionName.Contains('.'))
            {
                int dotPos = functionName.IndexOf('.');
                int angleStart = functionName.IndexOf('<');

                if (angleStart < dotPos)
                {
                    // This is a method on a generic type like "BackIndex<I>.resolve"
                    // Extract type parameters from the type name
                    int angleEnd = functionName.IndexOf('>', angleStart);
                    string typeParams = functionName.Substring(angleStart + 1, angleEnd - angleStart - 1);
                    var templateTypeParams = typeParams.Split(',').Select(s => s.Trim()).ToList();

                    // Map template type parameters to concrete type arguments
                    for (int i = 0; i < Math.Min(templateTypeParams.Count, typeArguments.Count); i++)
                    {
                        substitutions[templateTypeParams[i]] = typeArguments[i];
                    }
                }
            }

            // Also add any method-level generic parameters if they exist
            if (template.GenericParameters != null && template.GenericParameters.Count > 0)
            {
                // If we already have substitutions from the enclosing type, method parameters come after
                int offset = substitutions.Count;
                for (int i = 0; i < template.GenericParameters.Count && offset + i < typeArguments.Count; i++)
                {
                    string methodParam = template.GenericParameters[i];
                    if (!substitutions.ContainsKey(methodParam))
                    {
                        substitutions[methodParam] = typeArguments[offset + i];
                    }
                }
            }

            // Generate the mangled name
            var typeInfos = typeArguments
                           .Select(selector: t =>
                                new TypeInfo(Name: t, IsReference: false))
                           .ToList();
            string mangledName =
                MonomorphizeFunctionName(baseName: functionName, typeArgs: typeInfos);

            // Before generating the function, ensure all parameter record types are instantiated
            // This is needed so that field types are tracked in _recordFieldsRfTypes
            if (template.Parameters != null)
            {
                foreach (Parameter param in template.Parameters)
                {
                    if (param.Type != null)
                    {
                        string paramTypeName = GetFullTypeName(type: param.Type);
                        // Apply substitutions to get the concrete type
                        if (paramTypeName.Contains('<') && paramTypeName.Contains('>'))
                        {
                            foreach (var kvp in substitutions)
                            {
                                paramTypeName = System.Text.RegularExpressions.Regex.Replace(
                                    paramTypeName,
                                    $@"\b{kvp.Key}\b",
                                    kvp.Value);
                            }

                            // Parse the generic type to get base name and type args
                            int openBracket = paramTypeName.IndexOf('<');
                            if (openBracket > 0)
                            {
                                string baseName = paramTypeName.Substring(0, openBracket);
                                int closeBracket = paramTypeName.LastIndexOf('>');
                                string typeArgsStr = paramTypeName.Substring(openBracket + 1, closeBracket - openBracket - 1);
                                var typeArgs = typeArgsStr.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                                // Instantiate the record/entity if it's a generic type
                                if (_genericRecordTemplates.ContainsKey(baseName))
                                {
                                    InstantiateGenericRecord(recordName: baseName, typeArguments: typeArgs);
                                }
                                else if (_genericEntityTemplates.ContainsKey(baseName))
                                {
                                    InstantiateGenericEntity(entityName: baseName, typeArguments: typeArgs);
                                }
                            }
                        }
                    }
                }
            }

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

        _output.AppendLine(value: "; Imported function declarations from imports");

        // Track emitted declarations to avoid duplicates
        var emittedDeclarations = new HashSet<string>();

        // Get all external functions from the symbol table
        foreach (Symbol symbol in _semanticSymbolTable.GetAllSymbols())
        {
            if (symbol is FunctionSymbol funcSymbol && funcSymbol.IsExternal)
            {
                // Skip generic external functions - they can't be emitted directly
                // as LLVM IR declarations since they have unresolved type parameters.
                // These will be instantiated with concrete types when called.
                if (funcSymbol.IsGeneric)
                {
                    continue;
                }

                // Skip external functions with 'auto' type parameters - these are
                // polymorphic functions that need type specialization at call sites.
                bool hasAutoType = funcSymbol.Parameters.Any(p => p.Type?.Name == "auto") ||
                                   funcSymbol.ReturnType?.Name == "auto";
                if (hasAutoType)
                {
                    continue;
                }

                // Try to convert RazorForge types to LLVM IR types
                // Skip if any type is unknown (e.g., from modules not loaded in core prelude)
                try
                {
                    string returnType;
                    if (funcSymbol.ReturnType == null || funcSymbol.ReturnType.Name == "void")
                    {
                        returnType = "void";
                    }
                    else
                    {
                        returnType = MapRazorForgeTypeToLLVM(razorForgeType: funcSymbol.ReturnType.Name);
                    }

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
                        string paramType = MapRazorForgeTypeToLLVM(razorForgeType: param.Type.Name);
                        paramTypes.Add(item: paramType);
                    }

                    string paramList = string.Join(separator: ", ", values: paramTypes);
                    string funcName = funcSymbol.Name;

                    // Sanitize function name for LLVM IR (remove ! and other invalid characters)
                    // The ! suffix in RazorForge indicates throwable/crashable functions
                    // but LLVM IR function names cannot contain these characters
                    string sanitizedFuncName = funcName.Replace("!", "");

                    // Create a signature key to detect duplicates
                    string signature = $"declare {returnType} @{sanitizedFuncName}({paramList})";

                    // Only emit if we haven't seen this exact signature before
                    if (!emittedDeclarations.Contains(signature))
                    {
                        _output.AppendLine(value: signature);
                        emittedDeclarations.Add(signature);
                    }
                }
                catch (CodeGenError)
                {
                    // Skip external declarations with unknown types
                    // This can happen when external functions reference types that aren't
                    // part of the core prelude (e.g., d128 external functions)
                    continue;
                }
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

        // Save original filename to restore later
        string originalFileName = _currentFileName;

        // First pass: Process all struct/record declarations and presets to register them
        // This ensures record constructors and constants are available before processing functions
        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            ModuleResolver.ModuleInfo moduleInfo = moduleEntry.Value;

            // Update current file name for accurate error reporting
            _currentFileName = moduleInfo.FilePath;

            foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
            {
                // Process struct declarations to register record types
                if (declaration is RecordDeclaration structDecl)
                {
                    if (structDecl.GenericParameters != null && structDecl.GenericParameters.Count > 0)
                    {
                        // Register generic template without generating code
                        _genericRecordTemplates[structDecl.Name] = structDecl;
                        _output.AppendLine(
                            handler:
                            $"; Generic record template: {structDecl.Name}<{string.Join(separator: ", ", values: structDecl.GenericParameters)}>");
                    }
                    else
                    {
                        // Non-generic record - generate code normally
                        structDecl.Accept(visitor: this);
                    }
                }
                // Process class declarations to register entity types
                else if (declaration is EntityDeclaration classDecl)
                {
                    if (classDecl.GenericParameters != null && classDecl.GenericParameters.Count > 0)
                    {
                        // Register generic template without generating code
                        _genericEntityTemplates[classDecl.Name] = classDecl;
                        _output.AppendLine(
                            handler:
                            $"; Generic entity template: {classDecl.Name}<{string.Join(separator: ", ", values: classDecl.GenericParameters)}>");
                    }
                    else
                    {
                        // Non-generic entity - generate code normally
                        classDecl.Accept(visitor: this);
                    }
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

            // Update current file name for accurate error reporting
            _currentFileName = moduleInfo.FilePath;

            _output.AppendLine(handler: $"; Module: {moduleName}");


            // First register any namespace declarations from this module
            foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
            {
                if (declaration is NamespaceDeclaration nsDecl)
                {
                    _semanticSymbolTable?.RegisterNamespace(nsDecl.Path);
                }
            }

            // Visit each declaration in the module
            foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
            {
                // Process methods inside record declarations
                if (declaration is RecordDeclaration recordDecl)
                {
                    // Check if the record itself is generic
                    bool isGenericRecord = recordDecl.GenericParameters != null && recordDecl.GenericParameters.Count > 0;

                    foreach (Declaration member in recordDecl.Members)
                    {
                        if (member is FunctionDeclaration methodDecl)
                        {
                            // CRITICAL: Method names in record AST are just the method name (e.g., "snatch!")
                            // We need to prepend the record name to get the fully qualified name (e.g., "DynamicSlice.snatch!")
                            string fullyQualifiedName = $"{recordDecl.Name}.{methodDecl.Name}";

                            // Methods on generic records are treated as generic methods
                            // even if they don't have their own generic parameters
                            if (isGenericRecord || (methodDecl.GenericParameters != null && methodDecl.GenericParameters.Count > 0))
                            {
                                // Build template name with record's generic parameters (e.g., "BackIndex<I>.resolve")
                                if (isGenericRecord)
                                {
                                    string genericTypeName = $"{recordDecl.Name}<{string.Join(", ", recordDecl.GenericParameters)}>";
                                    fullyQualifiedName = $"{genericTypeName}.{methodDecl.Name}";
                                }

                                // Register generic method template for later instantiation
                                _genericFunctionTemplates[fullyQualifiedName] = methodDecl with { Name = fullyQualifiedName };
                                continue;
                            }
                            string methodName = SanitizeFunctionName(name: fullyQualifiedName);
                            if (generatedFunctions.Contains(item: methodName))
                            {
                                continue;
                            }

                            generatedFunctions.Add(item: methodName);
                            _generatedFunctions.Add(item: methodName);

                            // Generate the method
                            StringBuilder savedOutput = _output;
                            StringBuilder tempOutput = new StringBuilder();
                            _output = tempOutput;
                            _stackTraceCodeGen?.SetOutput(tempOutput);

                            try
                            {
                                // Create a modified FunctionDeclaration with the fully qualified name
                                var qualifiedMethodDecl = methodDecl with { Name = fullyQualifiedName };
                                qualifiedMethodDecl.Accept(visitor: this);
                                savedOutput.Append(tempOutput.ToString());
                            }
                            catch (CodeGenError ex)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to code generation error: {ex.Message}");
                            }
                            catch (NotImplementedException ex)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to unimplemented feature: {ex.Message}");
                            }
                            catch (StackOverflowException)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to stack overflow (infinite recursion)");
                                throw; // Re-throw stack overflow as it's fatal
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to unexpected error: {ex.GetType().Name}: {ex.Message}");
                            }
                            finally
                            {
                                _output = savedOutput;
                                _stackTraceCodeGen?.SetOutput(savedOutput);
                            }
                        }
                    }
                }

                // Process methods inside entity declarations (same as records)
                else if (declaration is EntityDeclaration entityDecl)
                {
                    // Check if the entity itself is generic
                    bool isGenericEntity = entityDecl.GenericParameters != null && entityDecl.GenericParameters.Count > 0;

                    // Output comment for generic entities (to help with debugging)
                    if (isGenericEntity)
                    {
                        _output.AppendLine($"; Generic entity: {entityDecl.Name}<{string.Join(", ", entityDecl.GenericParameters)}>");
                    }

                    foreach (Declaration member in entityDecl.Members)
                    {
                        if (member is FunctionDeclaration methodDecl)
                        {
                            // CRITICAL: Method names in entity AST are just the method name (e.g., "__create__")
                            // We need to prepend the entity name to get the fully qualified name (e.g., "List.__create__")
                            string fullyQualifiedName = $"{entityDecl.Name}.{methodDecl.Name}";

                            // Methods on generic entities are treated as generic methods
                            // even if they don't have their own generic parameters
                            if (isGenericEntity || (methodDecl.GenericParameters != null && methodDecl.GenericParameters.Count > 0))
                            {
                                // Build template name with entity's generic parameters (e.g., "List<T>.__create__")
                                if (isGenericEntity)
                                {
                                    string genericTypeName = $"{entityDecl.Name}<{string.Join(", ", entityDecl.GenericParameters)}>";
                                    fullyQualifiedName = $"{genericTypeName}.{methodDecl.Name}";
                                }

                                // Register generic method template for later instantiation
                                _genericFunctionTemplates[fullyQualifiedName] = methodDecl with { Name = fullyQualifiedName };
                                continue;
                            }
                            string methodName = SanitizeFunctionName(name: fullyQualifiedName);
                            if (generatedFunctions.Contains(item: methodName))
                            {
                                continue;
                            }

                            generatedFunctions.Add(item: methodName);
                            _generatedFunctions.Add(item: methodName);

                            // Generate the method
                            StringBuilder savedOutput = _output;
                            StringBuilder tempOutput = new StringBuilder();
                            _output = tempOutput;
                            _stackTraceCodeGen?.SetOutput(tempOutput);

                            try
                            {
                                // Create a modified FunctionDeclaration with the fully qualified name
                                var qualifiedMethodDecl = methodDecl with { Name = fullyQualifiedName };
                                qualifiedMethodDecl.Accept(visitor: this);
                                savedOutput.Append(tempOutput.ToString());
                            }
                            catch (CodeGenError ex)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to code generation error: {ex.Message}");
                            }
                            catch (NotImplementedException ex)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to unimplemented feature: {ex.Message}");
                            }
                            catch (StackOverflowException)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to stack overflow (infinite recursion)");
                                throw; // Re-throw stack overflow as it's fatal
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Warning: Skipping function {methodName} due to unexpected error: {ex.GetType().Name}: {ex.Message}");
                            }
                            finally
                            {
                                _output = savedOutput;
                                _stackTraceCodeGen?.SetOutput(savedOutput);
                            }
                        }
                    }
                }

                // Only generate code for function declarations (not external declarations)
                if (declaration is FunctionDeclaration funcDecl)
                {
                    // Check if this function has generic parameters
                    // BUT: If the function name contains generic type syntax (e.g., "List<T>.method"),
                    // the generics come from the receiver type, not method-level generics
                    // PARSER BUG: The parser incorrectly puts receiver type arguments in GenericParameters
                    // For "Text<letter8>.to_cstr", it puts "letter8" in GenericParameters
                    // We need to clear this to prevent it being treated as a generic method
                    bool hasMethodLevelGenerics = funcDecl.GenericParameters != null && funcDecl.GenericParameters.Count > 0;
                    bool isGenericTypeMethod = funcDecl.Name.Contains('<') && funcDecl.Name.Contains('>') && funcDecl.Name.Contains('.');

                    // PARSER BUG WORKAROUND: Clear generic parameters if they're from the receiver type
                    // When we have both hasMethodLevelGenerics and isGenericTypeMethod, the generic
                    // parameters are actually from the receiver type (e.g., Text<letter8>.to_cstr has
                    // "letter8" in GenericParameters, but that's part of the receiver, not method generics)
                    // We can't modify the AST (init-only property), so just override the flag
                    if (hasMethodLevelGenerics && isGenericTypeMethod)
                    {
                        hasMethodLevelGenerics = false; // Treat as non-generic method
                    }

                    if (hasMethodLevelGenerics && !isGenericTypeMethod)
                    {
                        // True method-level generics (not from receiver type) - skip for now
                        continue;
                    }

                    // If it's a method on a generic type, check if it's actually generic
                    if (isGenericTypeMethod)
                    {
                        // Extract the receiver type to check if type arguments are generic
                        int dotIndex = funcDecl.Name.IndexOf('.');
                        string receiverType = funcDecl.Name.Substring(0, dotIndex);
                        int openBracket = receiverType.IndexOf('<');
                        int closeBracket = receiverType.LastIndexOf('>');
                        string typeArgsStr = receiverType.Substring(openBracket + 1, closeBracket - openBracket - 1);

                        // Check if any type argument is a generic parameter (single uppercase letter)
                        bool hasGenericParams = false;
                        var typeArgs = typeArgsStr.Split(',').Select(t => t.Trim());
                        foreach (var typeArg in typeArgs)
                        {
                            if (typeArg.Length == 1 && char.IsUpper(typeArg[0]))
                            {
                                hasGenericParams = true;
                                break;
                            }
                        }

                        if (hasGenericParams)
                        {
                            // This is a true generic method template
                            _genericFunctionTemplates[funcDecl.Name] = funcDecl;
                            _output.AppendLine($"; Generic method template: {funcDecl.Name}");
                            continue;
                        }
                        // else: concrete method on specific instantiation
                        // Need to ensure the type is instantiated first
                        string baseTypeName = receiverType.Substring(0, openBracket);
                        if (_genericEntityTemplates.ContainsKey(baseTypeName))
                        {
                            // Instantiate the entity type if not already done
                            var concreteTypeArgs = typeArgs.ToList();
                            InstantiateGenericEntity(baseTypeName, concreteTypeArgs);
                        }
                        else if (_genericRecordTemplates.ContainsKey(baseTypeName))
                        {
                            // Instantiate the record type if not already done
                            var concreteTypeArgs = typeArgs.ToList();
                            InstantiateGenericRecord(baseTypeName, concreteTypeArgs);
                        }
                        // Now generate the method normally (fall through)
                    }

                    // Check if this is a method on a generic type (old fallback logic)
                    // Extract receiver type from function name (e.g., "List<T>.__create__" -> "List<T>")
                    if (funcDecl.Name.Contains('.'))
                    {
                        int dotIndex = funcDecl.Name.IndexOf('.');
                        string receiverType = funcDecl.Name.Substring(0, dotIndex);

                        // Check if it's a single uppercase letter (generic type parameter like "T.method")
                        if (receiverType.Length == 1 && char.IsUpper(receiverType[0]))
                        {
                            continue; // Skip type parameter methods
                        }

                        // Check if receiver type contains generic parameters (e.g., "List<T>")
                        if (receiverType.Contains('<') && receiverType.Contains('>'))
                        {
                            // Need to determine if type arguments are generic parameters or concrete types
                            // Extract the type arguments from the receiver
                            int openBracket = receiverType.IndexOf('<');
                            int closeBracket = receiverType.LastIndexOf('>');
                            string typeArgsStr = receiverType.Substring(openBracket + 1, closeBracket - openBracket - 1);

                            // Check if any type argument is a generic parameter (single uppercase letter or known generic param)
                            bool hasGenericParams = false;
                            var typeArgs = typeArgsStr.Split(',').Select(t => t.Trim());
                            foreach (var typeArg in typeArgs)
                            {
                                // Simple heuristic: single uppercase letter is likely a generic parameter
                                // Concrete types like "letter8", "u64", etc. have lowercase or numbers
                                if (typeArg.Length == 1 && char.IsUpper(typeArg[0]))
                                {
                                    hasGenericParams = true;
                                    break;
                                }
                            }

                            if (hasGenericParams)
                            {
                                // This is a method on a generic type template - register as template
                                _genericFunctionTemplates[funcDecl.Name] = funcDecl;
                                _output.AppendLine($"; Generic method template: {funcDecl.Name}");
                                continue;
                            }
                            // else: This is a concrete method on a specific instantiation - generate it normally
                        }
                    }

                    // Skip functions with parameters that reference unregistered types
                    // (e.g., routine f128.__create__!(from_text: Text<Letterlikes>) where Text isn't loaded)
                    bool hasUnregisteredType = false;
                    if (funcDecl.Parameters != null)
                    {
                        foreach (var param in funcDecl.Parameters)
                        {
                            if (param.Type != null)
                            {
                                string typeName = GetFullTypeName(param.Type);
                                // Check if this is a generic type (contains '<')
                                if (typeName.Contains('<'))
                                {
                                    // Extract base type name (e.g., "Text" from "Text<Letterlikes>")
                                    int anglePos = typeName.IndexOf('<');
                                    string baseTypeName = typeName.Substring(0, anglePos);

                                    // Check if the base type is registered as a generic template
                                    if (!_genericEntityTemplates.ContainsKey(baseTypeName) &&
                                        !_genericRecordTemplates.ContainsKey(baseTypeName))
                                    {
                                        hasUnregisteredType = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (hasUnregisteredType)
                    {
                        continue;
                    }

                    // Skip if already generated
                    string funcName = SanitizeFunctionName(name: funcDecl.Name);
                    if (generatedFunctions.Contains(item: funcName))
                    {
                        continue;
                    }

                    generatedFunctions.Add(item: funcName);
                    _generatedFunctions.Add(item: funcName);

                    // Generate the function into a temporary buffer
                    // If generation fails, we don't add incomplete function definitions to output
                    // Wrap in try-catch to skip functions that fail code generation
                    // (e.g., inline if-then-else expressions, unimplemented intrinsics, etc.)
                    StringBuilder savedOutput = _output;
                    StringBuilder tempOutput = new StringBuilder();
                    _output = tempOutput;
                    _stackTraceCodeGen?.SetOutput(tempOutput);

                    try
                    {
                        funcDecl.Accept(visitor: this);
                        // Success - append the generated function to the main output
                        savedOutput.Append(tempOutput.ToString());
                    }
                    catch (CodeGenError ex)
                    {
                        // Log warning but continue processing other functions
                        // The incomplete function in tempOutput is discarded
                        Console.WriteLine($"Warning: Skipping function {funcName} due to code generation error: {ex.Message}");
                    }
                    catch (NotImplementedException ex)
                    {
                        // Log warning for unimplemented features
                        // The incomplete function in tempOutput is discarded
                        Console.WriteLine($"Warning: Skipping function {funcName} due to unimplemented feature: {ex.Message}");
                    }
                    finally
                    {
                        // Restore the original output buffer
                        _output = savedOutput;
                        _stackTraceCodeGen?.SetOutput(savedOutput);
                    }
                }
            }
        }

        // Generate any generic types that were discovered during function processing
        // These types need to be inserted earlier in the output, but we need to collect them first
        // TODO: Refactor to use a separate buffer for type definitions
        GeneratePendingTypeInstantiations();

        // Restore original filename
        _currentFileName = originalFileName;

        _output.AppendLine(value: "; ============================================");
        _output.AppendLine();
    }
}
