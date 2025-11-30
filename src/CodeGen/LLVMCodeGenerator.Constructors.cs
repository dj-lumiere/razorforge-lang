using Compilers.Shared.Analysis;
using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Tries to handle a call to an external function from an imported module.
    /// Looks up the function in the semantic symbol table and emits a direct call if found.
    /// </summary>
    /// <param name="funcName">The qualified function name (e.g., "Console.get_line") or
    /// a direct external function name (e.g., "rf_console_get_line")</param>
    /// <param name="arguments">The arguments to the function call</param>
    /// <param name="resultTemp">The temporary variable to store the result</param>
    /// <param name="result">The output result string (set if handled)</param>
    /// <returns>true if the function was handled; false otherwise</returns>
    private bool TryHandleExternalFunctionCall(string funcName, List<Expression> arguments, string resultTemp, out string? result)
    {
        result = null;
        if (_semanticSymbolTable == null)
        {
            return false;
        }

        // Look for the external function in the symbol table
        // First try direct lookup by name
        Symbol? symbol = _semanticSymbolTable.Lookup(name: funcName);

        // If not found, try common naming conventions
        if (symbol == null)
        {
            // Try "rf_module_method" pattern (e.g., "Console.get_line" -> "rf_console_get_line")
            int dotIndex = funcName.LastIndexOf(value: '.');
            if (dotIndex >= 0)
            {
                string moduleName = funcName.Substring(startIndex: 0, length: dotIndex);
                string methodName = funcName.Substring(startIndex: dotIndex + 1);
                string externalName = $"rf_{moduleName.ToLowerInvariant()}_{methodName}";
                symbol = _semanticSymbolTable.Lookup(name: externalName);
            }
        }

        // Check if we found an external function
        if (symbol is FunctionSymbol funcSymbol && funcSymbol.IsExternal)
        {
            string externalFuncName = funcSymbol.Name;

            // Found the external function - emit the call
            // First, visit all arguments
            var argValues = new List<(string value, string type)>();
            for (int i = 0; i < arguments.Count; i++)
            {
                string argValue = arguments[index: i]
                   .Accept(visitor: this);
                string argType = "i32"; // default

                // Get the expected parameter type from the function symbol
                if (i < funcSymbol.Parameters.Count && funcSymbol.Parameters[index: i].Type != null)
                {
                    argType = MapTypeToLLVM(rfType: funcSymbol.Parameters[index: i].Type!.Name);
                }
                else if (_tempTypes.TryGetValue(key: argValue, value: out TypeInfo? argTypeInfo))
                {
                    argType = argTypeInfo.LLVMType;
                }

                argValues.Add(item: (argValue, argType));
            }

            // Build the argument list string
            string argList = string.Join(separator: ", ", values: argValues.Select(selector: a => $"{a.type} {a.value}"));

            // Determine the return type
            string returnType = funcSymbol.ReturnType != null
                ? MapTypeToLLVM(rfType: funcSymbol.ReturnType.Name)
                : "void";

            // Emit the call
            if (returnType == "void")
            {
                _output.AppendLine(handler: $"  call void @{externalFuncName}({argList})");
                result = resultTemp;
            }
            else
            {
                _output.AppendLine(handler: $"  {resultTemp} = call {returnType} @{externalFuncName}({argList})");

                // Track the type of the result
                string rfReturnType = funcSymbol.ReturnType?.Name ?? "s32";
                bool isUnsigned = rfReturnType.StartsWith(value: "u");
                bool isFloat = rfReturnType.StartsWith(value: "f") || rfReturnType.StartsWith(value: "d");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: returnType, IsUnsigned: isUnsigned, IsFloatingPoint: isFloat, RazorForgeType: rfReturnType);

                result = resultTemp;
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// Tries to handle a call to an imported module function (including generic functions).
    /// Looks up the function in the generic templates and instantiates if needed.
    /// </summary>
    /// <param name="moduleName">The module name (e.g., "Console")</param>
    /// <param name="functionName">The function name (e.g., "alert")</param>
    /// <param name="arguments">The arguments to the function call</param>
    /// <param name="resultTemp">The temporary variable to store the result</param>
    /// <param name="result">The output result string (set if handled)</param>
    /// <returns>true if the function was handled; false otherwise</returns>
    private bool TryHandleImportedModuleFunctionCall(string moduleName, string functionName, List<Expression> arguments, string resultTemp, out string? result)
    {
        result = null;

        // Full qualified function name (e.g., "Console.alert")
        string qualifiedName = $"{moduleName}.{functionName}";

        // First, visit arguments to get their types (needed for both generic and overload resolution)
        var argValues = new List<(string value, string type, string rfType)>();
        foreach (Expression arg in arguments)
        {
            string argValue = arg.Accept(visitor: this);
            string llvmType = "i32";
            string rfType = "s32";

            if (_tempTypes.TryGetValue(key: argValue, value: out TypeInfo? typeInfo))
            {
                llvmType = typeInfo.LLVMType;
                rfType = typeInfo.RazorForgeType;
            }

            argValues.Add(item: (argValue, llvmType, rfType));
        }

        // Check if there's a non-generic overload that matches the argument types
        // This takes priority over generic instantiation
        if (_loadedModules != null)
        {
            foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
            {
                foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
                {
                    if (decl is FunctionDeclaration funcDecl && funcDecl.Name == qualifiedName && (funcDecl.GenericParameters == null || funcDecl.GenericParameters.Count == 0) && funcDecl.Parameters?.Count == argValues.Count)
                    {
                        // Check if parameter types match
                        bool matches = true;
                        for (int i = 0; i < funcDecl.Parameters.Count && matches; i++)
                        {
                            string paramType = funcDecl.Parameters[index: i].Type?.Name ?? "s32";
                            string argRfType = argValues[index: i].rfType;
                            // Match if types are compatible (exact match or compatible string types)
                            if (paramType != argRfType && !(IsStringType(typeName: paramType) && IsStringType(typeName: argRfType)))
                            {
                                matches = false;
                            }
                        }

                        if (matches)
                        {
                            // Found a matching non-generic overload - use it
                            string argList = string.Join(separator: ", ", values: argValues.Select(selector: a => $"{a.type} {a.value}"));

                            // Determine return type
                            string returnType = funcDecl.ReturnType != null
                                ? MapTypeToLLVM(rfType: funcDecl.ReturnType.Name)
                                : "void";

                            // Emit the call
                            if (returnType == "void")
                            {
                                _output.AppendLine(handler: $"  call void @\"{qualifiedName}\"({argList})");
                                result = resultTemp;
                            }
                            else
                            {
                                _output.AppendLine(handler: $"  {resultTemp} = call {returnType} @\"{qualifiedName}\"({argList})");

                                // Track result type
                                string rfReturnType = funcDecl.ReturnType?.Name ?? "s32";
                                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: returnType, IsUnsigned: rfReturnType.StartsWith(value: "u"), IsFloatingPoint: rfReturnType.StartsWith(value: "f") || rfReturnType.StartsWith(value: "d"), RazorForgeType: rfReturnType);

                                result = resultTemp;
                            }

                            return true;
                        }
                    }
                }
            }
        }

        // Check if we have a generic function template for this
        if (_genericFunctionTemplates.TryGetValue(key: qualifiedName, value: out FunctionDeclaration? template))
        {
            // It's a generic function - infer types and instantiate
            // Infer generic type arguments from the first argument
            // For Console.alert<T>(value: T), T is inferred from the argument type
            var inferredTypes = new List<string>();
            if (argValues.Count > 0)
            {
                // Use the RazorForge type name for the generic substitution
                string inferredType = argValues[index: 0].rfType;
                // Only normalize to cstr if the rfType wasn't properly tracked
                if (argValues[index: 0].type == "i8*" && (inferredType == "s32" || string.IsNullOrEmpty(value: inferredType)))
                {
                    inferredType = "cstr";
                }

                inferredTypes.Add(item: inferredType);
            }
            else if (template.GenericParameters != null)
            {
                // No arguments - use default types
                foreach (string gp in template.GenericParameters)
                {
                    inferredTypes.Add(item: "s32");
                }
            }

            // Instantiate the generic function
            string mangledName = InstantiateGenericFunction(functionName: qualifiedName, typeArguments: inferredTypes);

            // Build argument list for the call
            string argList = string.Join(separator: ", ", values: argValues.Select(selector: a => $"{a.type} {a.value}"));

            // Determine return type
            string returnType = "void";
            if (template.ReturnType != null)
            {
                returnType = MapTypeToLLVM(rfType: template.ReturnType.Name);
            }

            // Emit the call
            if (returnType == "void")
            {
                _output.AppendLine(handler: $"  call void @\"{mangledName}\"({argList})");
                result = resultTemp;
            }
            else
            {
                _output.AppendLine(handler: $"  {resultTemp} = call {returnType} @\"{mangledName}\"({argList})");
                result = resultTemp;
            }

            return true;
        }

        // Check if it's a non-generic imported function
        // Look for function like "Console.get_line" in the generated functions
        if (_generatedFunctions.Contains(item: qualifiedName))
        {
            // Use already-visited argValues from above
            string argList = string.Join(separator: ", ", values: argValues.Select(selector: a => $"{a.type} {a.value}"));

            // Look up the function's return type from loaded modules
            string returnType = "i8*";
            string rfReturnType = "cstr";

            foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules ?? new Dictionary<string, ModuleResolver.ModuleInfo>())
            {
                foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
                {
                    if (decl is FunctionDeclaration funcDecl && funcDecl.Name == qualifiedName)
                    {
                        if (funcDecl.ReturnType != null)
                        {
                            returnType = MapTypeToLLVM(rfType: funcDecl.ReturnType.Name);
                            rfReturnType = funcDecl.ReturnType.Name;
                        }
                        else
                        {
                            returnType = "void";
                            rfReturnType = "void";
                        }

                        break;
                    }
                }
            }

            // Emit call
            if (returnType == "void")
            {
                _output.AppendLine(handler: $"  call void @\"{qualifiedName}\"({argList})");
            }
            else
            {
                _output.AppendLine(handler: $"  {resultTemp} = call {returnType} @\"{qualifiedName}\"({argList})");
            }

            // Track result type
            _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: returnType, IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: rfReturnType);

            result = resultTemp;
            return true;
        }

        return false;
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
                    string msgValue = arguments[index: 0]
                       .Accept(visitor: this);
                    // Just pass through the message pointer as the error value
                    // The throw statement will handle it appropriately
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "Error");
                    _output.AppendLine(handler: $"  {resultTemp} = bitcast i8* {msgValue} to i8*"); // identity cast
                    return resultTemp;
                }

                _output.AppendLine(handler: $"  {resultTemp} = bitcast i8* null to i8*");
                return resultTemp;

            default:
                // Unknown Error method
                _output.AppendLine(handler: $"  ; Unknown Error.{methodName} - not implemented");
                _output.AppendLine(handler: $"  {resultTemp} = inttoptr i32 0 to i8*");
                return resultTemp;
        }
    }

    /// <summary>
    /// Checks if a sanitized function name is a type constructor call.
    /// Matches patterns: TypeName___create___throwable (from TypeName!) or try_TypeName___create__ (from TypeName?)
    /// </summary>
    private bool IsTypeConstructorCall(string sanitizedName)
    {
        return sanitizedName.EndsWith(value: "___create___throwable") || sanitizedName.StartsWith(value: "try_") && sanitizedName.EndsWith(value: "___create__");
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

        if (functionName.EndsWith(value: "___create___throwable"))
        {
            baseType = functionName[..^"___create___throwable".Length];
            isThrowable = true;
        }
        else if (functionName.StartsWith(value: "try_") && functionName.EndsWith(value: "___create__"))
        {
            baseType = functionName["try_".Length..^"___create__".Length];
            isThrowable = false;
        }
        else
        {
            // Unknown pattern
            _output.AppendLine(handler: $"  ; Unknown type constructor: {functionName}");
            _output.AppendLine(handler: $"  {resultTemp} = add i32 0, 0");
            return resultTemp;
        }

        // Get the string argument
        string strArg = arguments.Count > 0
            ? arguments[index: 0]
               .Accept(visitor: this)
            : "null";

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
                _output.AppendLine(handler: $"  {longResult} = call i64 @strtol(i8* {strArg}, i8** null, i32 10)");
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
                    _output.AppendLine(handler: $"  {resultTemp} = add i64 {longResult}, 0"); // identity
                }
                else
                {
                    _output.AppendLine(handler: $"  {resultTemp} = trunc i64 {longResult} to {llvmType}");
                }

                return resultTemp;

            case "u32":
            case "u64":
            case "u16":
            case "u8":
                // Use strtol and cast to unsigned
                string ulongResult = GetNextTemp();
                _output.AppendLine(handler: $"  {ulongResult} = call i64 @strtol(i8* {strArg}, i8** null, i32 10)");
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
                    _output.AppendLine(handler: $"  {resultTemp} = add i64 {ulongResult}, 0");
                }
                else
                {
                    _output.AppendLine(handler: $"  {resultTemp} = trunc i64 {ulongResult} to {uLlvmType}");
                }

                return resultTemp;

            default:
                // Unknown type - return 0
                _output.AppendLine(handler: $"  ; Unknown type constructor for: {baseType}");
                _output.AppendLine(handler: $"  {resultTemp} = add i32 0, 0");
                return resultTemp;
        }
    }

    /// <summary>
    /// Checks if a function call is actually a record constructor call.
    /// Returns true if the callee name matches a known record type.
    /// </summary>
    private bool IsRecordConstructorCall(string typeName)
    {
        return _recordFields.ContainsKey(key: typeName);
    }

    /// <summary>
    /// Handles record constructor calls like letter8(value: codepoint) or StackFrame(file_id: 1, ...).
    /// Allocates the struct on stack and initializes fields from named arguments.
    /// </summary>
    private string HandleRecordConstructorCall(string typeName, List<Expression> arguments, string resultTemp)
    {
        if (!_recordFields.TryGetValue(key: typeName, value: out List<(string Name, string Type)>? fields))
        {
            _output.AppendLine(handler: $"  ; Unknown record type: {typeName}");
            _output.AppendLine(handler: $"  {resultTemp} = alloca %{typeName}");
            return resultTemp;
        }

        // Allocate the struct on the stack
        string structPtr = GetNextTemp();
        _output.AppendLine(handler: $"  {structPtr} = alloca %{typeName}");

        // Build a map of argument name -> value
        var argValues = new Dictionary<string, string>();
        var argTypes = new Dictionary<string, string>();

        foreach (Expression arg in arguments)
        {
            if (arg is NamedArgumentExpression namedArg)
            {
                string value = namedArg.Value.Accept(visitor: this);
                argValues[key: namedArg.Name] = value;
                // Try to get the type from temp tracking
                if (_tempTypes.TryGetValue(key: value, value: out TypeInfo? typeInfo))
                {
                    argTypes[key: namedArg.Name] = typeInfo.LLVMType;
                }
            }
            else
            {
                // Positional argument - match by index
                string value = arg.Accept(visitor: this);
                int index = arguments.IndexOf(item: arg);
                if (index < fields.Count)
                {
                    argValues[key: fields[index: index].Name] = value;
                    if (_tempTypes.TryGetValue(key: value, value: out TypeInfo? typeInfo))
                    {
                        argTypes[key: fields[index: index].Name] = typeInfo.LLVMType;
                    }
                }
            }
        }

        // Initialize each field with getelementptr and store
        for (int i = 0; i < fields.Count; i++)
        {
            (string fieldName, string fieldType) = fields[index: i];
            string fieldPtr = GetNextTemp();
            _output.AppendLine(handler: $"  {fieldPtr} = getelementptr inbounds %{typeName}, ptr {structPtr}, i32 0, i32 {i}");

            if (argValues.TryGetValue(key: fieldName, value: out string? value))
            {
                // Use the type from tracked temps if available, otherwise use the field type
                string storeType = argTypes.TryGetValue(key: fieldName, value: out string? argType)
                    ? argType
                    : fieldType;
                _output.AppendLine(handler: $"  store {storeType} {value}, ptr {fieldPtr}");
            }
            else
            {
                // Field not provided - initialize with zero
                _output.AppendLine(handler: $"  store {fieldType} zeroinitializer, ptr {fieldPtr}");
            }
        }

        // Load the struct value to return (records are value types)
        _output.AppendLine(handler: $"  {resultTemp} = load %{typeName}, ptr {structPtr}");
        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: $"%{typeName}", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeName);

        return resultTemp;
    }

    private string HandleDangerZoneFunction(GenericMethodCallExpression node, string functionName, string llvmType, string typeName, string resultTemp)
    {
        switch (functionName)
        {
            case "write_as":
                string addrTemp = node.Arguments[index: 0]
                                      .Accept(visitor: this);
                string writeValueTemp = node.Arguments[index: 1]
                                            .Accept(visitor: this);
                _output.AppendLine(handler: $"  %ptr_{resultTemp} = inttoptr i64 {addrTemp} to ptr");
                _output.AppendLine(handler: $"  store {llvmType} {writeValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "read_as":
                string readAddrTemp = node.Arguments[index: 0]
                                          .Accept(visitor: this);
                _output.AppendLine(handler: $"  %ptr_{resultTemp} = inttoptr i64 {readAddrTemp} to ptr");
                _output.AppendLine(handler: $"  {resultTemp} = load {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName), IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeName);
                return resultTemp;

            case "volatile_write":
                string volWriteAddrTemp = node.Arguments[index: 0]
                                              .Accept(visitor: this);
                string volWriteValueTemp = node.Arguments[index: 1]
                                               .Accept(visitor: this);
                _output.AppendLine(handler: $"  %ptr_{resultTemp} = inttoptr i64 {volWriteAddrTemp} to ptr");
                _output.AppendLine(handler: $"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "volatile_read":
                string volReadAddrTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                _output.AppendLine(handler: $"  %ptr_{resultTemp} = inttoptr i64 {volReadAddrTemp} to ptr");
                _output.AppendLine(handler: $"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{resultTemp}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName), IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: typeName);
                return resultTemp;

            default:
                throw new NotImplementedException(message: $"Danger zone function {functionName} not implemented in LLVM generator");
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

    private string HandleNonGenericDangerZoneFunction(CallExpression node, string functionName, string resultTemp)
    {
        switch (functionName)
        {
            case "address_of":
                // address_of!(variable) -> uaddr (address of variable)
                // Expects a single identifier argument
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException(message: $"address_of! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                Expression argument = node.Arguments[index: 0];
                if (argument is IdentifierExpression varIdent)
                {
                    // Generate ptrtoint to get address of variable
                    _output.AppendLine(handler: $"  {resultTemp} = ptrtoint ptr %{varIdent.Name} to i64");
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: true, RazorForgeType: "uaddr"); // uaddr is unsigned
                    return resultTemp;
                }
                else
                {
                    // Handle complex expressions by first evaluating them
                    string argTemp = argument.Accept(visitor: this);
                    _output.AppendLine(handler: $"  {resultTemp} = ptrtoint ptr {argTemp} to i64");
                    _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64", IsUnsigned: false, IsFloatingPoint: true, RazorForgeType: "uaddr"); // uaddr is unsigned
                    return resultTemp;
                }

            case "invalidate":
                // invalidate!(slice) -> void (free memory)
                if (node.Arguments.Count != 1)
                {
                    throw new InvalidOperationException(message: $"invalidate! expects exactly 1 argument, got {node.Arguments.Count}");
                }

                Expression sliceArgument = node.Arguments[index: 0];
                // Evaluate the argument and then call heap_free on it
                string sliceTemp = sliceArgument.Accept(visitor: this);
                _output.AppendLine(handler: $"  call void @heap_free(ptr {sliceTemp})");
                return ""; // void return

            default:
                throw new NotImplementedException(message: $"Non-generic danger zone function {functionName} not implemented in LLVM generator");
        }
    }

    /// <summary>
    /// Generates LLVM IR for a named argument expression (name: value).
    /// For LLVM IR, named arguments are just their values - the naming is handled at the call site.
    /// </summary>
    public string VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        // For LLVM IR, we just generate the value - argument matching happens at the call site
        return node.Value.Accept(visitor: this);
    }

    /// <summary>
    /// Generates LLVM IR for a struct literal expression (Type { field: value, ... }).
    /// Allocates memory for the struct and initializes fields.
    /// </summary>
    public string VisitStructLiteralExpression(StructLiteralExpression node)
    {
        // TODO: Implement struct literal code generation
        // For now, generate a placeholder that will be expanded when structs are fully implemented
        string structType = node.TypeName;
        if (node.TypeArguments != null && node.TypeArguments.Count > 0)
        {
            structType += "<" + string.Join(separator: ", ", values: node.TypeArguments.Select(selector: t => t.Name)) + ">";
        }

        _output.AppendLine(value: $"  ; TODO: Struct literal not yet implemented: {structType}");
        return "null";
    }
}
