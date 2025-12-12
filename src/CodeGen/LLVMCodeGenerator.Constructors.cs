using Compilers.Shared.Analysis;
using Compilers.Shared.AST;
using Compilers.Shared.Errors;

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
    private bool TryHandleExternalFunctionCall(string funcName, List<Expression> arguments,
        string resultTemp, out string? result)
    {
        result = null;

        // First try symbol table lookup
        Symbol? symbol = _semanticSymbolTable?.Lookup(name: funcName);

        // If not found in symbol table, try common naming conventions
        if (symbol == null && _semanticSymbolTable != null)
        {
            // Try "rf_module_method" pattern (e.g., "Console.get_line" -> "rf_console_get_line")
            int dotIndex = funcName.LastIndexOf(value: '.');
            if (dotIndex >= 0)
            {
                string moduleName = funcName[..dotIndex];
                string methodName = funcName[(dotIndex + 1)..];
                string externalName = $"rf_{moduleName.ToLowerInvariant()}_{methodName}";
                symbol = _semanticSymbolTable.Lookup(name: externalName);
            }
        }

        // Check if we found an external function in symbol table
        if (symbol is FunctionSymbol { IsExternal: true } funcSymbol)
        {
            return HandleExternalFunctionSymbol(funcSymbol, arguments, resultTemp, out result);
        }

        // Symbol table lookup failed - try finding in loaded modules directly
        // This handles external functions declared in imported modules
        if (_loadedModules == null)
        {
            return false;
        }

        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
            {
                if (decl is ExternalDeclaration extDecl && extDecl.Name == funcName)
                {
                    return HandleExternalDeclaration(extDecl, arguments, resultTemp, out result);
                }
            }
        }

        return false;
    }

    private bool HandleExternalDeclaration(ExternalDeclaration extDecl, List<Expression> arguments,
        string resultTemp, out string? result)
    {
        result = null;

        string externalFuncName = extDecl.Name;

        // Skip generic external functions
        if (extDecl.GenericParameters != null && extDecl.GenericParameters.Count > 0)
        {
            return false;
        }

        // Visit all arguments
        var argValues = new List<(string value, string type)>();
        for (int i = 0; i < arguments.Count; i++)
        {
            string argValue = arguments[i].Accept(visitor: this);
            string argType;
            string expectedType;

            // Get the expected parameter type from the external declaration
            // Use MapRazorForgeTypeToLLVM for external function parameters (FFI boundary)
            if (i < extDecl.Parameters.Count && extDecl.Parameters[i].Type != null)
            {
                expectedType = MapRazorForgeTypeToLLVM(razorForgeType: extDecl.Parameters[i].Type!.Name);
            }
            else if (_tempTypes.TryGetValue(key: argValue, value: out LLVMTypeInfo? argTypeInfo))
            {
                expectedType = argTypeInfo.LLVMType;
            }
            else
            {
                throw CodeGenError.TypeResolutionFailed(typeName: $"argument {i}",
                    context: $"cannot determine type for argument {i} in external function call '{externalFuncName}'",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position);
            }

            // Determine the actual type of the argument
            if (_tempTypes.TryGetValue(key: argValue, value: out LLVMTypeInfo? actualTypeInfo))
            {
                argType = actualTypeInfo.LLVMType;
            }
            else
            {
                argType = expectedType;
            }

            // Unwrap record wrappers when passing to external functions expecting primitives
            // If actual type is a record (%xyz) and expected type is a primitive (i8*, i64, etc.)
            if (argType.StartsWith("%") && !argType.EndsWith("*") &&
                !expectedType.StartsWith("%") && expectedType != "ptr")
            {
                // Remember if this was %uaddr before extracting
                bool wasUaddr = (argType == "%uaddr");

                // Extract the primitive value from the record wrapper
                string primitiveType = InferPrimitiveTypeFromRecordName(argType);
                string unwrapped = GetNextTemp();
                _output.AppendLine($"  {unwrapped} = extractvalue {argType} {argValue}, 0");
                argValue = unwrapped;
                argType = primitiveType;

                // Special case: %uaddr unwrapped to i64 and expected type is i8* (pointer)
                // This happens when cstr (which wraps uaddr) is passed to C functions expecting char*
                if (wasUaddr && primitiveType == "i64" && expectedType == "i8*")
                {
                    string ptrValue = GetNextTemp();
                    _output.AppendLine($"  {ptrValue} = inttoptr i64 {argValue} to i8*");
                    argValue = ptrValue;
                    argType = "i8*";
                }
            }

            // Use argType (which may have been updated by unwrapping) instead of expectedType
            argValues.Add(item: (argValue, argType));
        }

        // Build the argument list string
        string argList = string.Join(separator: ", ",
            values: argValues.Select(selector: a => $"{a.type} {a.value}"));

        // Determine the return type
        string returnType = extDecl.ReturnType != null
            ? MapTypeToLLVM(rfType: extDecl.ReturnType.Name)
            : "void";

        // Emit the call
        if (returnType == "void")
        {
            _output.AppendLine(handler: $"  call void @{externalFuncName}({argList})");
        }
        else
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = call {returnType} @{externalFuncName}({argList})");

            // Track the type of the result
            string rfReturnType = extDecl.ReturnType?.Name ?? "Blank";
            bool isUnsigned = rfReturnType.StartsWith(value: "u");
            bool isFloat = rfReturnType.StartsWith(value: "f") ||
                           rfReturnType.StartsWith(value: "d");
            _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: returnType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: rfReturnType);
        }

        result = resultTemp;
        return true;
    }

    private bool HandleExternalFunctionSymbol(FunctionSymbol funcSymbol, List<Expression> arguments,
        string resultTemp, out string? result)
    {
        result = null;

        string externalFuncName = funcSymbol.Name;

        // Found the external function - emit the call
        // First, visit all arguments
        var argValues = new List<(string value, string type)>();
        for (int i = 0; i < arguments.Count; i++)
        {
            string argValue = arguments[index: i]
               .Accept(visitor: this);
            string argType;
            string expectedType;

            // Get the expected parameter type from the function symbol
            // Use MapRazorForgeTypeToLLVM for external function parameters (FFI boundary)
            if (i < funcSymbol.Parameters.Count && funcSymbol.Parameters[index: i].Type != null)
            {
                expectedType = MapRazorForgeTypeToLLVM(razorForgeType: funcSymbol.Parameters[index: i].Type!.Name);
            }
            else if (_tempTypes.TryGetValue(key: argValue, value: out LLVMTypeInfo? argTypeInfo))
            {
                expectedType = argTypeInfo.LLVMType;
            }
            else
            {
                throw CodeGenError.TypeResolutionFailed(typeName: $"argument {i}",
                    context:
                    $"cannot determine type for argument {i} in external function call '{externalFuncName}'",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position);
            }

            // Determine the actual type of the argument
            if (_tempTypes.TryGetValue(key: argValue, value: out LLVMTypeInfo? actualTypeInfo))
            {
                argType = actualTypeInfo.LLVMType;
            }
            else
            {
                argType = expectedType;
            }

            // Unwrap record wrappers when passing to external functions expecting primitives
            // If actual type is a record (%xyz) and expected type is a primitive (i8*, i64, etc.)
            if (argType.StartsWith("%") && !argType.EndsWith("*") &&
                !expectedType.StartsWith("%") && expectedType != "ptr")
            {
                // Remember if this was %uaddr before extracting
                bool wasUaddr = (argType == "%uaddr");

                // Extract the primitive value from the record wrapper
                string primitiveType = InferPrimitiveTypeFromRecordName(argType);
                string unwrapped = GetNextTemp();
                _output.AppendLine($"  {unwrapped} = extractvalue {argType} {argValue}, 0");
                argValue = unwrapped;
                argType = primitiveType;

                // Special case: %uaddr unwrapped to i64 and expected type is i8* (pointer)
                // This happens when cstr (which wraps uaddr) is passed to C functions expecting char*
                if (wasUaddr && primitiveType == "i64" && expectedType == "i8*")
                {
                    string ptrValue = GetNextTemp();
                    _output.AppendLine($"  {ptrValue} = inttoptr i64 {argValue} to i8*");
                    argValue = ptrValue;
                    argType = "i8*";
                }
            }

            // Use argType (which may have been updated by unwrapping) instead of expectedType
            argValues.Add(item: (argValue, argType));
        }

        // Build the argument list string
        string argList = string.Join(separator: ", ",
            values: argValues.Select(selector: a => $"{a.type} {a.value}"));

        // Determine the return type
        string returnType = funcSymbol.ReturnType != null
            ? MapTypeToLLVM(rfType: funcSymbol.ReturnType.Name)
            : "void";

        // Emit the call
        if (returnType == "void")
        {
            _output.AppendLine(handler: $"  call void @{externalFuncName}({argList})");
        }
        else
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = call {returnType} @{externalFuncName}({argList})");

            // Track the type of the result
            string rfReturnType = funcSymbol.ReturnType?.Name ?? "Blank";
            bool isUnsigned = rfReturnType.StartsWith(value: "u");
            bool isFloat = rfReturnType.StartsWith(value: "f") ||
                           rfReturnType.StartsWith(value: "d");
            _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: returnType,
                IsUnsigned: isUnsigned,
                IsFloatingPoint: isFloat,
                RazorForgeType: rfReturnType);

        }

        result = resultTemp;

        return true;

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
    private bool TryHandleImportedModuleFunctionCall(string moduleName, string functionName,
        List<Expression> arguments, string resultTemp, out string? result)
    {
        result = null;
        string argList;
        string returnType;
        string rfReturnType;

        // Full qualified function name (e.g., "Console.alert")
        string qualifiedName = $"{moduleName}.{functionName}";

        // First, visit arguments to get their types (needed for both generic and overload resolution)
        var argValues = new List<(string value, string type, string rfType)>();
        foreach (Expression arg in arguments)
        {
            string argValue = arg.Accept(visitor: this);
            string llvmType;
            string rfType;

            if (_tempTypes.TryGetValue(key: argValue, value: out LLVMTypeInfo? typeInfo))
            {
                llvmType = typeInfo.LLVMType;
                rfType = typeInfo.RazorForgeType;
            }
            else
            {
                // Try to infer type from the expression itself (for literals and other direct values)
                try
                {
                    LLVMTypeInfo inferredType = GetTypeInfo(expr: arg);
                    llvmType = inferredType.LLVMType;
                    rfType = inferredType.RazorForgeType;
                }
                catch
                {
                    throw CodeGenError.TypeResolutionFailed(typeName: "argument",
                        context:
                        $"cannot determine type for argument in imported module function call '{qualifiedName}'",
                        file: _currentFileName,
                        line: arg.Location.Line,
                        column: arg.Location.Column,
                        position: arg.Location.Position);
                }
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
                    if (decl is not FunctionDeclaration funcDecl ||
                        funcDecl.Name != qualifiedName ||
                        (funcDecl.GenericParameters != null &&
                         funcDecl.GenericParameters.Count != 0) ||
                        funcDecl.Parameters?.Count != argValues.Count)
                    {
                        continue;
                    }

                    // Check if parameter types match
                    bool matches = true;
                    for (int i = 0; i < funcDecl.Parameters.Count && matches; i++)
                    {
                        if (funcDecl.Parameters[index: i].Type == null)
                        {
                            throw CodeGenError.TypeResolutionFailed(
                                typeName: funcDecl.Parameters[index: i].Name,
                                context:
                                $"function '{qualifiedName}' parameter must have a type annotation",
                                file: _currentFileName,
                                line: funcDecl.Parameters[index: i].Location.Line,
                                column: funcDecl.Parameters[index: i].Location.Column,
                                position: funcDecl.Parameters[index: i].Location.Position);
                        }

                        string paramType = funcDecl.Parameters[index: i].Type!.Name;
                        string argRfType = argValues[index: i].rfType;
                        // Match if types are compatible (exact match or compatible string types)
                        if (paramType != argRfType && !(IsStringType(typeName: paramType) &&
                                                        IsStringType(typeName: argRfType)))
                        {
                            matches = false;
                        }
                    }

                    if (!matches)
                    {
                        continue;
                    }

                    // Found a matching non-generic overload - use it
                    argList = string.Join(separator: ", ",
                        values: argValues.Select(selector: a => $"{a.type} {a.value}"));

                    // Determine return type
                    returnType = funcDecl.ReturnType != null
                        ? MapTypeToLLVM(rfType: funcDecl.ReturnType.Name)
                        : "void";

                    // Emit the call
                    if (returnType == "void")
                    {
                        _output.AppendLine(
                            handler: $"  call void @\"{qualifiedName}\"({argList})");
                    }
                    else
                    {
                        _output.AppendLine(
                            handler:
                            $"  {resultTemp} = call {returnType} @\"{qualifiedName}\"({argList})");

                        // Track result type
                        rfReturnType = funcDecl.ReturnType?.Name ?? "Blank";
                        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: returnType,
                            IsUnsigned: rfReturnType.StartsWith(value: "u"),
                            IsFloatingPoint: rfReturnType.StartsWith(value: "f") ||
                                             rfReturnType.StartsWith(value: "d"),
                            RazorForgeType: rfReturnType);

                    }

                    result = resultTemp;

                    return true;
                }
            }
        }

        // Check if we have a generic function template for this
        if (_genericFunctionTemplates.TryGetValue(key: qualifiedName,
                value: out FunctionDeclaration? template))
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
                if (argValues[index: 0].type == "i8*" && (inferredType == "s32" ||
                                                          string.IsNullOrEmpty(
                                                              value: inferredType)))
                {
                    inferredType = "cstr";
                }

                inferredTypes.Add(item: inferredType);
            }
            else if (template.GenericParameters != null)
            {
                // No arguments - cannot infer generic types
                throw CodeGenError.TypeResolutionFailed(typeName: qualifiedName,
                    context: "cannot infer generic type arguments without arguments",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position);
            }

            // Instantiate the generic function
            string mangledName = InstantiateGenericFunction(functionName: qualifiedName,
                typeArguments: inferredTypes);

            // Build argument list for the call
            argList = string.Join(separator: ", ",
                values: argValues.Select(selector: a => $"{a.type} {a.value}"));

            // Determine return type
            returnType = "void";
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
                _output.AppendLine(
                    handler: $"  {resultTemp} = call {returnType} @\"{mangledName}\"({argList})");
                result = resultTemp;
            }

            return true;
        }

        // Check if it's a non-generic imported function
        // Look for function like "Console.get_line" in the generated functions
        if (!_generatedFunctions.Contains(item: qualifiedName))
        {
            return false;
        }

        // Use already-visited argValues from above
        argList = string.Join(separator: ", ",
            values: argValues.Select(selector: a => $"{a.type} {a.value}"));

        // Look up the function's return type from loaded modules
        returnType = "i8*";
        rfReturnType = "cstr";

        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules ??
                 new Dictionary<string, ModuleResolver.ModuleInfo>())
        {
            foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
            {
                if (decl is not FunctionDeclaration funcDecl || funcDecl.Name != qualifiedName)
                {
                    continue;
                }

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

        // Emit call
        if (returnType == "void")
        {
            _output.AppendLine(handler: $"  call void @\"{qualifiedName}\"({argList})");
        }
        else
        {
            _output.AppendLine(
                handler: $"  {resultTemp} = call {returnType} @\"{qualifiedName}\"({argList})");
        }

        // Track result type
        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: returnType,
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: rfReturnType);

        result = resultTemp;
        return true;
    }

    /// <summary>
    /// Handles Error.* calls for error creation and handling.
    /// </summary>
    private string HandleErrorCall(string methodName, List<Expression> arguments,
        string resultTemp)
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
                    _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i8*",
                        IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: "Error");
                    _output.AppendLine(
                        handler:
                        $"  {resultTemp} = bitcast i8* {msgValue} to i8*"); // identity cast
                    return resultTemp;
                }

                _output.AppendLine(handler: $"  {resultTemp} = bitcast i8* null to i8*");
                return resultTemp;

            default:
                // Unknown Error method
                throw CodeGenError.TypeResolutionFailed(typeName: $"Error.{methodName}",
                    context: "unknown Error method",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position);
        }
    }

    /// <summary>
    /// Checks if a sanitized function name is a type constructor call.
    /// Matches patterns: TypeName___create___throwable (from TypeName!) or try_TypeName___create__ (from TypeName?)
    /// </summary>
    private bool IsTypeConstructorCall(string sanitizedName)
    {
        return sanitizedName.EndsWith(value: "___create___throwable") ||
               sanitizedName.StartsWith(value: "try_") &&
               sanitizedName.EndsWith(value: "___create__");
    }

    /// <summary>
    /// Handles type constructor calls like s32___create___throwable (from s32!) or try_s32___create__ (from s32?).
    /// Uses C runtime functions like strtol for parsing.
    /// </summary>
    private string HandleTypeConstructorCall(string functionName, List<Expression> arguments,
        string resultTemp)
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
        else if (functionName.StartsWith(value: "try_") &&
                 functionName.EndsWith(value: "___create__"))
        {
            baseType = functionName["try_".Length..^"___create__".Length];
            isThrowable = false;
        }
        else
        {
            throw CodeGenError.TypeResolutionFailed(typeName: functionName,
                context: "unknown type constructor pattern",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Get the string argument
        if (arguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(typeName: baseType,
                context: "type constructor requires a string argument",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        string strArg = arguments[index: 0]
           .Accept(visitor: this);

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
                _output.AppendLine(
                    handler:
                    $"  {longResult} = call i64 @strtol(i8* {strArg}, i8** null, i32 10)");
                // Truncate to appropriate size
                string llvmType = baseType switch
                {
                    "s8" => "i8",
                    "s16" => "i16",
                    "s32" => "i32",
                    "s64" => "i64",
                    _ => throw CodeGenError.TypeResolutionFailed(typeName: baseType,
                        context: "unsupported signed integer type for constructor",
                        file: _currentFileName,
                        line: _currentLocation.Line,
                        column: _currentLocation.Column,
                        position: _currentLocation.Position)
                };
                if (llvmType == "i64")
                {
                    _output.AppendLine(
                        handler: $"  {resultTemp} = add i64 {longResult}, 0"); // identity
                }
                else
                {
                    _output.AppendLine(
                        handler: $"  {resultTemp} = trunc i64 {longResult} to {llvmType}");
                }

                return resultTemp;

            case "u32":
            case "u64":
            case "u16":
            case "u8":
                // Use strtol and cast to unsigned
                string ulongResult = GetNextTemp();
                _output.AppendLine(
                    handler:
                    $"  {ulongResult} = call i64 @strtol(i8* {strArg}, i8** null, i32 10)");
                string uLlvmType = baseType switch
                {
                    "u8" => "i8",
                    "u16" => "i16",
                    "u32" => "i32",
                    "u64" => "i64",
                    _ => throw CodeGenError.TypeResolutionFailed(typeName: baseType,
                        context: "unsupported unsigned integer type for constructor",
                        file: _currentFileName,
                        line: _currentLocation.Line,
                        column: _currentLocation.Column,
                        position: _currentLocation.Position)
                };
                if (uLlvmType == "i64")
                {
                    _output.AppendLine(handler: $"  {resultTemp} = add i64 {ulongResult}, 0");
                }
                else
                {
                    _output.AppendLine(
                        handler: $"  {resultTemp} = trunc i64 {ulongResult} to {uLlvmType}");
                }

                return resultTemp;

            default:
                throw CodeGenError.TypeResolutionFailed(typeName: baseType,
                    context: "unsupported type for string constructor",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position);
        }
    }

    /// <summary>
    /// Checks if a function call is actually a record constructor call.
    /// Returns true if the callee name matches a known record type.
    /// Also searches loaded modules for record declarations.
    /// </summary>
    private bool IsRecordConstructorCall(string typeName)
    {
        // Pointer types can't be record constructors
        if (typeName.EndsWith("*"))
        {
            return false;
        }

        // First check already-processed records
        if (_recordFields.ContainsKey(key: typeName))
        {
            return true;
        }

        // Check loaded modules for record declarations with this name
        foreach (var (_, moduleInfo) in _loadedModules)
        {
            foreach (IAstNode decl in moduleInfo.Ast.Declarations)
            {
                if (decl is RecordDeclaration recordDecl && recordDecl.Name == typeName)
                {
                    // Found a matching record - process it now
                    GenerateRecordType(node: recordDecl, typeSubstitutions: [], mangledName: typeName);
                    return true;
                }
            }
        }

        // Try to find and parse a file with the same name in the current file's directory
        // This handles cases where letter8.rf uses letter16 without importing it
        if (!string.IsNullOrEmpty(_currentFileName))
        {
            string? currentDir = Path.GetDirectoryName(_currentFileName);
            if (currentDir != null)
            {
                string potentialFile = Path.Combine(currentDir, typeName + ".rf");
                if (File.Exists(potentialFile))
                {
                    return TryParseAndLoadRecordFromFile(potentialFile, typeName);
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Attempts to parse a .rf file and extract a record declaration with the given name.
    /// </summary>
    private bool TryParseAndLoadRecordFromFile(string filePath, string typeName)
    {
        try
        {
            // Read and parse the file
            string source = File.ReadAllText(filePath);
            var tokens = Lexer.Tokenizer.Tokenize(source: source, language: _language);
            var parser = _language == Language.RazorForge
                ? new RazorForge.Parser.RazorForgeParser(tokens: tokens, fileName: filePath)
                : (Compilers.Shared.Parser.BaseParser)new Suflae.Parser.SuflaeParser(tokens: tokens, fileName: filePath);
            var ast = parser.Parse();

            // Look for the record declaration
            foreach (IAstNode decl in ast.Declarations)
            {
                if (decl is RecordDeclaration recordDecl && recordDecl.Name == typeName)
                {
                    // Found the record - generate its type
                    GenerateRecordType(node: recordDecl, typeSubstitutions: [], mangledName: typeName);
                    return true;
                }
            }
        }
        catch
        {
            // If parsing fails, just return false and let the error propagate naturally
        }

        return false;
    }

    /// <summary>
    /// Handles record constructor calls like letter8(value: codepoint) or StackFrame(file_id: 1, ...).
    /// Allocates the struct on stack and initializes fields from named arguments.
    /// </summary>
    private string HandleRecordConstructorCall(string typeName, List<Expression> arguments,
        string resultTemp)
    {
        if (!_recordFields.TryGetValue(key: typeName,
                value: out List<(string Name, string Type)>? fields))
        {
            throw CodeGenError.TypeResolutionFailed(typeName: typeName,
                context: "record type not found for constructor call",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
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
                if (_tempTypes.TryGetValue(key: value, value: out LLVMTypeInfo? typeInfo))
                {
                    argTypes[key: namedArg.Name] = typeInfo.LLVMType;
                }
            }
            else
            {
                // Positional argument - match by index
                string value = arg.Accept(visitor: this);
                int index = arguments.IndexOf(item: arg);
                if (index >= fields.Count)
                {
                    continue;
                }

                argValues[key: fields[index: index].Name] = value;
                if (_tempTypes.TryGetValue(key: value, value: out LLVMTypeInfo? typeInfo))
                {
                    argTypes[key: fields[index: index].Name] = typeInfo.LLVMType;
                }
            }
        }

        // Initialize each field with getelementptr and store
        for (int i = 0; i < fields.Count; i++)
        {
            (string fieldName, string fieldType) = fields[index: i];
            string fieldPtr = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {fieldPtr} = getelementptr inbounds %{typeName}, ptr {structPtr}, i32 0, i32 {i}");

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
                _output.AppendLine(
                    handler: $"  store {fieldType} zeroinitializer, ptr {fieldPtr}");
            }
        }

        // Load the struct value to return (records are value types)
        _output.AppendLine(handler: $"  {resultTemp} = load %{typeName}, ptr {structPtr}");
        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: $"%{typeName}",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: typeName);

        return resultTemp;
    }

    /// <summary>
    /// Handles entity constructor calls like Text<letter8>(letters: list).
    /// Allocates the entity on heap and initializes fields from named arguments.
    /// Returns a pointer to the heap-allocated entity.
    /// </summary>
    private string HandleEntityConstructorCall(string typeName, List<Expression> arguments,
        string resultTemp)
    {
        if (!_recordFields.TryGetValue(key: typeName,
                value: out List<(string Name, string Type)>? fields))
        {
            throw CodeGenError.TypeResolutionFailed(typeName: typeName,
                context: "entity type not found for constructor call",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Calculate size of the struct
        string sizeTemp = GetNextTemp();
        _output.AppendLine(handler: $"  {sizeTemp} = getelementptr %{typeName}, ptr null, i32 1");
        string sizePtrTemp = GetNextTemp();
        _output.AppendLine(handler: $"  {sizePtrTemp} = ptrtoint ptr {sizeTemp} to i64");

        // Allocate on heap using malloc
        string mallocTemp = GetNextTemp();
        _output.AppendLine(handler: $"  {mallocTemp} = call ptr @malloc(i64 {sizePtrTemp})");

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
                if (_tempTypes.TryGetValue(key: value, value: out LLVMTypeInfo? typeInfo))
                {
                    argTypes[key: namedArg.Name] = typeInfo.LLVMType;
                }
            }
            else
            {
                // Positional argument - match by index
                string value = arg.Accept(visitor: this);
                int index = arguments.IndexOf(item: arg);
                if (index >= fields.Count)
                {
                    continue;
                }

                argValues[key: fields[index: index].Name] = value;
                if (_tempTypes.TryGetValue(key: value, value: out LLVMTypeInfo? typeInfo))
                {
                    argTypes[key: fields[index: index].Name] = typeInfo.LLVMType;
                }
            }
        }

        // Initialize each field with getelementptr and store
        for (int i = 0; i < fields.Count; i++)
        {
            (string fieldName, string fieldType) = fields[index: i];
            string fieldPtr = GetNextTemp();
            _output.AppendLine(
                handler:
                $"  {fieldPtr} = getelementptr inbounds %{typeName}, ptr {mallocTemp}, i32 0, i32 {i}");

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
                _output.AppendLine(
                    handler: $"  store {fieldType} zeroinitializer, ptr {fieldPtr}");
            }
        }

        // Return the pointer directly (entities are reference types)
        _output.AppendLine(handler: $"  {resultTemp} = bitcast ptr {mallocTemp} to ptr");
        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "ptr",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType: typeName);

        return resultTemp;
    }

    /// <summary>
    /// Checks if a type name is a primitive type that can be used for type casting.
    /// </summary>
    private bool IsPrimitiveTypeCast(string typeName)
    {
        return typeName switch
        {
            // Signed integers
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" => true,
            // Unsigned integers
            "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" => true,
            // Floating point
            "f16" or "f32" or "f64" or "f128" => true,
            // Decimal
            "d32" or "d64" or "d128" => true,
            // Bool
            "bool" => true,
            _ => false
        };
    }

    /// <summary>
    /// Handles primitive type casts like saddr(value), uaddr(value), s32(value), etc.
    /// Generates appropriate LLVM sext/zext/trunc/bitcast instructions.
    /// </summary>
    private string HandlePrimitiveTypeCast(string targetTypeName, List<Expression> arguments,
        string resultTemp)
    {
        if (arguments.Count == 0)
        {
            throw CodeGenError.TypeResolutionFailed(typeName: targetTypeName,
                context: "type cast requires an argument",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Evaluate the source value
        string sourceValue = arguments[index: 0]
           .Accept(visitor: this);

        // Get source type info - check both _tempTypes and _symbolTypes
        string sourceType;
        bool sourceIsSigned;
        if (_tempTypes.TryGetValue(key: sourceValue, value: out LLVMTypeInfo? sourceTypeInfo))
        {
            sourceType = sourceTypeInfo.LLVMType;
            sourceIsSigned = !sourceTypeInfo.IsUnsigned;
        }
        // Also check _symbolTypes for parameters like '%me'
        else if (sourceValue.StartsWith("%") && _symbolTypes.TryGetValue(sourceValue.Substring(1), out var symbolType))
        {
            sourceType = symbolType;
            sourceIsSigned = symbolType.StartsWith("i") || symbolType.Contains("saddr");
        }
        else
        {
            throw CodeGenError.TypeResolutionFailed(typeName: "source value",
                context: $"cannot determine type for type cast to '{targetTypeName}'",
                file: _currentFileName,
                line: arguments[index: 0].Location.Line,
                column: arguments[index: 0].Location.Column,
                position: arguments[index: 0].Location.Position);
        }

        // If source is a struct, extract the primitive value first
        if (sourceType.StartsWith("%") && !sourceType.EndsWith("*") && sourceType != "ptr")
        {
            string primitiveType = InferPrimitiveTypeFromRecordName(sourceType);
            string extractedValue = GetNextTemp();
            _output.AppendLine($"  {extractedValue} = extractvalue {sourceType} {sourceValue}, 0");
            sourceValue = extractedValue;
            sourceType = primitiveType;
            // Update sourceIsSigned based on primitive type
            sourceIsSigned = primitiveType.StartsWith("i") && !primitiveType.StartsWith("i1");
        }

        // Get target LLVM type
        string targetType = MapTypeToLLVM(rfType: targetTypeName);
        bool targetIsSigned = targetTypeName.StartsWith(value: "s") || targetTypeName == "saddr";

        // Check if target is a record type
        bool targetIsRecord = targetType.StartsWith("%") && !targetType.EndsWith("*") && targetType != "ptr";
        string targetPrimitiveType = targetIsRecord ? InferPrimitiveTypeFromRecordName(targetType) : targetType;

        // Determine conversion operation
        int sourceBits = GetTypeBitWidth(llvmType: sourceType);
        int targetBits = GetTypeBitWidth(llvmType: targetPrimitiveType);

        string convertedValue;
        if (sourceBits == targetBits)
        {
            // Same size - just copy (or bitcast if needed)
            string tempConvert = GetNextTemp();
            _output.AppendLine(handler: $"  {tempConvert} = add {targetPrimitiveType} {sourceValue}, 0");
            convertedValue = tempConvert;
        }
        else if (targetBits > sourceBits)
        {
            // Extension - sext for signed, zext for unsigned
            string extOp = sourceIsSigned
                ? "sext"
                : "zext";
            string tempConvert = GetNextTemp();
            _output.AppendLine(
                handler: $"  {tempConvert} = {extOp} {sourceType} {sourceValue} to {targetPrimitiveType}");
            convertedValue = tempConvert;
        }
        else
        {
            // Truncation
            string tempConvert = GetNextTemp();
            _output.AppendLine(
                handler: $"  {tempConvert} = trunc {sourceType} {sourceValue} to {targetPrimitiveType}");
            convertedValue = tempConvert;
        }

        // If target is a record type, wrap the converted primitive value
        if (targetIsRecord)
        {
            _output.AppendLine($"  {resultTemp} = insertvalue {targetType} undef, {targetPrimitiveType} {convertedValue}, 0");
        }
        else
        {
            // For non-record types, the converted value IS the result
            // We need to copy it to the result temp
            _output.AppendLine($"  {resultTemp} = add {targetPrimitiveType} {convertedValue}, 0");
        }

        // Track the result type
        _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: targetType,
            IsUnsigned: !targetIsSigned,
            IsFloatingPoint: targetTypeName.StartsWith(value: "f"),
            RazorForgeType: targetTypeName);

        return resultTemp;
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
                // Strip % prefix from temp name to avoid %ptr_%tmp -> invalid
                string tempBaseName = resultTemp.TrimStart('%');

                // Check if addrTemp is a %uaddr record - if so, extract the i64
                string actualAddrValue = addrTemp;
                if (_tempTypes.TryGetValue(addrTemp, out LLVMTypeInfo? addrTypeInfo) &&
                    addrTypeInfo.LLVMType == "%uaddr")
                {
                    string extracted = GetNextTemp();
                    _output.AppendLine($"  {extracted} = extractvalue %uaddr {addrTemp}, 0");
                    actualAddrValue = extracted;
                }

                _output.AppendLine(
                    handler: $"  %ptr_{tempBaseName} = inttoptr i64 {actualAddrValue} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store {llvmType} {writeValueTemp}, ptr %ptr_{tempBaseName}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "read_as":
                string readAddrTemp = node.Arguments[index: 0]
                                          .Accept(visitor: this);
                string readTempBaseName = resultTemp.TrimStart('%');

                // Check if readAddrTemp is a %uaddr record - if so, extract the i64
                string actualReadAddrValue = readAddrTemp;
                if (_tempTypes.TryGetValue(readAddrTemp, out LLVMTypeInfo? readAddrTypeInfo) &&
                    readAddrTypeInfo.LLVMType == "%uaddr")
                {
                    string readExtracted = GetNextTemp();
                    _output.AppendLine($"  {readExtracted} = extractvalue %uaddr {readAddrTemp}, 0");
                    actualReadAddrValue = readExtracted;
                }

                _output.AppendLine(
                    handler: $"  %ptr_{readTempBaseName} = inttoptr i64 {actualReadAddrValue} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load {llvmType}, ptr %ptr_{readTempBaseName}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName),
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: typeName);
                return resultTemp;

            case "volatile_write":
                string volWriteAddrTemp = node.Arguments[index: 0]
                                              .Accept(visitor: this);
                string volWriteValueTemp = node.Arguments[index: 1]
                                               .Accept(visitor: this);
                string volWriteTempBaseName = resultTemp.TrimStart('%');

                // Check if volWriteAddrTemp is a %uaddr record - if so, extract the i64
                string actualVolWriteAddrValue = volWriteAddrTemp;
                if (_tempTypes.TryGetValue(volWriteAddrTemp, out LLVMTypeInfo? volWriteAddrTypeInfo) &&
                    volWriteAddrTypeInfo.LLVMType == "%uaddr")
                {
                    string volWriteExtracted = GetNextTemp();
                    _output.AppendLine($"  {volWriteExtracted} = extractvalue %uaddr {volWriteAddrTemp}, 0");
                    actualVolWriteAddrValue = volWriteExtracted;
                }

                _output.AppendLine(
                    handler: $"  %ptr_{volWriteTempBaseName} = inttoptr i64 {actualVolWriteAddrValue} to ptr");
                _output.AppendLine(
                    handler:
                    $"  store volatile {llvmType} {volWriteValueTemp}, ptr %ptr_{volWriteTempBaseName}, align {GetAlignment(typeName: typeName)}");
                return ""; // void return

            case "volatile_read":
                string volReadAddrTemp = node.Arguments[index: 0]
                                             .Accept(visitor: this);
                string volReadTempBaseName = resultTemp.TrimStart('%');

                // Check if volReadAddrTemp is a %uaddr record - if so, extract the i64
                string actualVolReadAddrValue = volReadAddrTemp;
                if (_tempTypes.TryGetValue(volReadAddrTemp, out LLVMTypeInfo? volReadAddrTypeInfo) &&
                    volReadAddrTypeInfo.LLVMType == "%uaddr")
                {
                    string volReadExtracted = GetNextTemp();
                    _output.AppendLine($"  {volReadExtracted} = extractvalue %uaddr {volReadAddrTemp}, 0");
                    actualVolReadAddrValue = volReadExtracted;
                }

                _output.AppendLine(
                    handler: $"  %ptr_{volReadTempBaseName} = inttoptr i64 {actualVolReadAddrValue} to ptr");
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = load volatile {llvmType}, ptr %ptr_{volReadTempBaseName}, align {GetAlignment(typeName: typeName)}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeName),
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: typeName);
                return resultTemp;

            default:
                throw CodeGenError.UnsupportedFeature(
                    feature: $"danger zone function '{functionName}'",
                    file: _currentFileName,
                    line: _currentLocation.Line,
                    column: _currentLocation.Column,
                    position: _currentLocation.Position);
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
                    throw CodeGenError.TypeResolutionFailed(typeName: "address_of",
                        context:
                        $"address_of! expects exactly 1 argument, got {node.Arguments.Count}",
                        file: _currentFileName,
                        line: node.Location.Line,
                        column: node.Location.Column,
                        position: node.Location.Position);
                }

                Expression argument = node.Arguments[index: 0];
                if (argument is IdentifierExpression varIdent)
                {
                    // Generate ptrtoint to get address of variable
                    _output.AppendLine(
                        handler: $"  {resultTemp} = ptrtoint ptr %{varIdent.Name} to i64");
                    _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                        IsUnsigned: false,
                        IsFloatingPoint: true,
                        RazorForgeType: "uaddr"); // uaddr is unsigned
                    return resultTemp;
                }
                else
                {
                    // Handle complex expressions by first evaluating them
                    string argTemp = argument.Accept(visitor: this);
                    _output.AppendLine(handler: $"  {resultTemp} = ptrtoint ptr {argTemp} to i64");
                    _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                        IsUnsigned: false,
                        IsFloatingPoint: true,
                        RazorForgeType: "uaddr"); // uaddr is unsigned
                    return resultTemp;
                }

            case "invalidate":
                // invalidate!(slice) -> void (free memory)
                if (node.Arguments.Count != 1)
                {
                    throw CodeGenError.TypeResolutionFailed(typeName: "invalidate",
                        context:
                        $"invalidate! expects exactly 1 argument, got {node.Arguments.Count}",
                        file: _currentFileName,
                        line: node.Location.Line,
                        column: node.Location.Column,
                        position: node.Location.Position);
                }

                Expression sliceArgument = node.Arguments[index: 0];
                // Evaluate the argument and then call heap_free on it
                string sliceTemp = sliceArgument.Accept(visitor: this);
                _output.AppendLine(handler: $"  call void @heap_free(ptr {sliceTemp})");
                return ""; // void return

            default:
                throw CodeGenError.UnsupportedFeature(
                    feature: $"non-generic danger zone function '{functionName}'",
                    file: _currentFileName,
                    line: node.Location.Line,
                    column: node.Location.Column,
                    position: node.Location.Position);
        }
    }

    /// <summary>
    /// Generates LLVM IR for a named argument expression (name: value).
    /// For LLVM IR, named arguments are just their values - the naming is handled at the call site.
    /// </summary>
    public string VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        UpdateLocation(node.Location);
        // For LLVM IR, we just generate the value - argument matching happens at the call site
        return node.Value.Accept(visitor: this);
    }

    /// <summary>
    /// Generates LLVM IR for a constructor expression (Type(field: value, ...)).
    /// Allocates memory for the type and initializes fields.
    /// </summary>
    public string VisitConstructorExpression(ConstructorExpression node)
    {
        UpdateLocation(node.Location);
        // TODO: Implement constructor expression code generation
        // For now, generate a placeholder that will be expanded when constructors are fully implemented
        string typeName = node.TypeName;
        if (node.TypeArguments != null && node.TypeArguments.Count > 0)
        {
            typeName += "<" + string.Join(separator: ", ",
                values: node.TypeArguments.Select(selector: t => t.Name)) + ">";
        }

        throw CodeGenError.UnsupportedFeature(
            feature: $"constructor expression for type '{typeName}'",
            file: _currentFileName,
            line: node.Location.Line,
            column: node.Location.Column,
            position: node.Location.Position);
    }

    /// <summary>
    /// Looks up the return type of a method based on the object's type and method name.
    /// Searches loaded modules for the method declaration.
    /// </summary>
    /// <param name="objectTypeName">The RazorForge type name of the object (e.g., "Text&lt;letter8&gt;")</param>
    /// <param name="methodName">The name of the method being called (e.g., "to_cstr")</param>
    /// <returns>A tuple of (LLVM return type, RazorForge return type)</returns>
    private (string llvmType, string rfType) LookupMethodReturnType(string? objectTypeName, string methodName)
    {
        if (string.IsNullOrEmpty(value: objectTypeName))
        {
            throw CodeGenError.TypeResolutionFailed(
                typeName: $"{objectTypeName ?? "unknown"}.{methodName}",
                context: "cannot lookup method return type without object type",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Strip pointer suffix from object type - methods are defined on the base type
        string actualTypeName = objectTypeName;
        if (actualTypeName.EndsWith("*"))
        {
            actualTypeName = actualTypeName.TrimEnd('*');
        }

        // For methods with ! suffix, try both with and without the suffix
        // Some methods like "snatch!" are defined with the ! as part of the name,
        // while others use ! as error propagation suffix
        var methodNamesToTry = new List<string> { methodName };
        if (methodName.EndsWith('!'))
        {
            methodNamesToTry.Add(methodName.TrimEnd('!'));
        }

        // Extract base type name (remove generic parameters for lookup)
        string baseTypeName = actualTypeName;
        if (objectTypeName.Contains(value: '<'))
        {
            baseTypeName = objectTypeName.Substring(startIndex: 0,
                length: objectTypeName.IndexOf(value: '<'));
        }

        // FIRST: Try to find the method in the current file being compiled (_currentProgram)
        if (_currentProgram != null)
        {
            foreach (Declaration decl in _currentProgram.Declarations)
            {
                // Check top-level function declarations
                if (decl is FunctionDeclaration funcDecl)
                {
                    (string llvmType, string rfType)? returnType = TryMatchMethod(funcDecl.Name, funcDecl.ReturnType,
                        objectTypeName, baseTypeName, methodNamesToTry);
                    if (returnType != null)
                    {
                        return returnType.Value;
                    }
                }
                // Check functions inside record declarations
                else if (decl is RecordDeclaration recordDecl)
                {
                    // Only check if the base type matches the record name
                    if (recordDecl.Name == baseTypeName)
                    {
                        foreach (Declaration member in recordDecl.Members)
                        {
                            if (member is FunctionDeclaration memberFunc)
                            {
                                // For functions inside records, the funcName is just the method name
                                // e.g., "snatch!" not "DynamicSlice.snatch!"
                                foreach (string nameToTry in methodNamesToTry)
                                {
                                    if (memberFunc.Name == nameToTry)
                                    {
                                        string rfType = memberFunc.ReturnType?.Name ?? "void";
                                        string llvmType = memberFunc.ReturnType != null
                                            ? MapTypeToLLVM(rfType: memberFunc.ReturnType.Name)
                                            : "void";
                                        return (llvmType, rfType);
                                    }
                                }
                            }
                        }
                    }
                }
                // Check functions inside entity declarations
                else if (decl is EntityDeclaration entityDecl)
                {
                    if (entityDecl.Name == baseTypeName)
                    {
                        foreach (Declaration member in entityDecl.Members)
                        {
                            if (member is FunctionDeclaration memberFunc)
                            {
                                foreach (string nameToTry in methodNamesToTry)
                                {
                                    if (memberFunc.Name == nameToTry)
                                    {
                                        string rfType = memberFunc.ReturnType?.Name ?? "void";
                                        string llvmType = memberFunc.ReturnType != null
                                            ? MapTypeToLLVM(rfType: memberFunc.ReturnType.Name)
                                            : "void";
                                        return (llvmType, rfType);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // SECOND: Try to find the method in loaded modules (if available)
        if (_loadedModules == null)
        {
            // If no loaded modules and not found in current program, method doesn't exist
            throw CodeGenError.TypeResolutionFailed(typeName: $"{objectTypeName}.{methodName}",
                context: "method not found in current file (no imported modules available)",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        // Try to find the method in loaded modules
        // Look for patterns like "Text<letter8>.to_cstr" or "TypeName.methodName"
        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
            {
                // Check top-level function declarations
                if (decl is FunctionDeclaration funcDecl)
                {
                    (string llvmType, string rfType)? returnType = TryMatchMethod(funcDecl.Name, funcDecl.ReturnType,
                        objectTypeName, baseTypeName, methodNamesToTry);
                    if (returnType != null)
                    {
                        return returnType.Value;
                    }
                }
                // Check functions inside record declarations
                else if (decl is RecordDeclaration recordDecl)
                {
                    // Only check if the base type matches the record name
                    if (recordDecl.Name == baseTypeName)
                    {
                        foreach (Declaration member in recordDecl.Members)
                        {
                            if (member is FunctionDeclaration memberFunc)
                            {
                                // For functions inside records, the funcName is just the method name
                                // e.g., "snatch!" not "DynamicSlice.snatch!"
                                foreach (string nameToTry in methodNamesToTry)
                                {
                                    if (memberFunc.Name == nameToTry)
                                    {
                                        string rfType = memberFunc.ReturnType?.Name ?? "void";
                                        string llvmType = memberFunc.ReturnType != null
                                            ? MapTypeToLLVM(rfType: memberFunc.ReturnType.Name)
                                            : "void";
                                        return (llvmType, rfType);
                                    }
                                }
                            }
                        }
                    }
                }
                // Check functions inside entity declarations
                else if (decl is EntityDeclaration entityDecl)
                {
                    if (entityDecl.Name == baseTypeName)
                    {
                        foreach (Declaration member in entityDecl.Members)
                        {
                            if (member is FunctionDeclaration memberFunc)
                            {
                                foreach (string nameToTry in methodNamesToTry)
                                {
                                    if (memberFunc.Name == nameToTry)
                                    {
                                        string rfType = memberFunc.ReturnType?.Name ?? "void";
                                        string llvmType = memberFunc.ReturnType != null
                                            ? MapTypeToLLVM(rfType: memberFunc.ReturnType.Name)
                                            : "void";
                                        return (llvmType, rfType);
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Method not found
        throw CodeGenError.TypeResolutionFailed(typeName: $"{objectTypeName}.{methodName}",
            context: "method not found in loaded modules",
            file: _currentFileName,
            line: _currentLocation.Line,
            column: _currentLocation.Column,
            position: _currentLocation.Position);
    }

    /// <summary>
    /// Helper method to match a function against method name patterns.
    /// </summary>
    private (string llvmType, string rfType)? TryMatchMethod(string funcName, TypeExpression? returnType,
        string objectTypeName, string baseTypeName, List<string> methodNamesToTry)
    {
        foreach (string nameToTry in methodNamesToTry)
        {
            bool matches = false;

            // Pattern 1: Exact match with full generic type (e.g., "Text<letter8>.to_cstr")
            if (funcName == $"{objectTypeName}.{nameToTry}")
            {
                matches = true;
            }
            // Pattern 2: Base type match (e.g., "Text.to_cstr" for "Text<letter8>")
            else if (funcName == $"{baseTypeName}.{nameToTry}")
            {
                matches = true;
            }
            // Pattern 3: Generic type pattern (e.g., "Text<T>.to_cstr")
            else if (funcName.StartsWith(value: $"{baseTypeName}<") &&
                     funcName.EndsWith(value: $">.{nameToTry}"))
            {
                matches = true;
            }

            if (matches)
            {
                string rfType = returnType?.Name ?? "void";
                string llvmType = returnType != null ? MapTypeToLLVM(rfType: returnType.Name) : "void";
                return (llvmType, rfType);
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up the return type for a standalone function (not a method on a type).
    /// Searches both the current program and loaded modules.
    /// </summary>
    /// <param name="functionName">The mangled/sanitized function name</param>
    /// <returns>The LLVM return type</returns>
    private string LookupFunctionReturnType(string functionName)
    {
        // Check if it's an external function in the symbol table first
        Symbol? symbol = _semanticSymbolTable?.Lookup(name: functionName);
        if (symbol is FunctionSymbol { IsExternal: true } funcSymbol)
        {
            return funcSymbol.ReturnType != null
                ? MapTypeToLLVM(rfType: funcSymbol.ReturnType.Name)
                : "void";
        }

        // First, try to find the function in the current program being compiled
        if (_currentProgram != null)
        {
            foreach (IAstNode decl in _currentProgram.Declarations)
            {
                if (decl is FunctionDeclaration funcDecl)
                {
                    // Check if this function matches (accounting for name sanitization)
                    string sanitizedFuncName = SanitizeFunctionName(name: funcDecl.Name);
                    if (sanitizedFuncName == functionName || funcDecl.Name == functionName)
                    {
                        // Found the function - return its LLVM type
                        return funcDecl.ReturnType != null
                            ? MapTypeToLLVM(rfType: funcDecl.ReturnType.Name)
                            : "void";
                    }
                }
                else if (decl is ExternalDeclaration extDecl)
                {
                    // Check external function declarations
                    if (extDecl.Name == functionName)
                    {
                        return extDecl.ReturnType != null
                            ? MapTypeToLLVM(rfType: extDecl.ReturnType.Name)
                            : "void";
                    }
                }
            }
        }

        // Try to find the function in loaded modules (imported)
        if (_loadedModules == null)
        {
            throw CodeGenError.TypeResolutionFailed(typeName: functionName,
                context: "module is null",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position);
        }

        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
            {
                if (decl is FunctionDeclaration funcDecl)
                {
                    // Check if this function matches (accounting for name sanitization)
                    string sanitizedFuncName = SanitizeFunctionName(name: funcDecl.Name);
                    if (sanitizedFuncName == functionName || funcDecl.Name == functionName)
                    {
                        // Found the function - return its LLVM type
                        return funcDecl.ReturnType != null
                            ? MapTypeToLLVM(rfType: funcDecl.ReturnType.Name)
                            : "void";
                    }
                }
                else if (decl is ExternalDeclaration extDecl)
                {
                    // Check external function declarations in imported modules
                    if (extDecl.Name == functionName)
                    {
                        return extDecl.ReturnType != null
                            ? MapTypeToLLVM(rfType: extDecl.ReturnType.Name)
                            : "void";
                    }
                }
            }
        }

        // Function not found
        throw CodeGenError.TypeResolutionFailed(typeName: functionName,
            context: "function not found in current program or loaded modules",
            file: _currentFileName,
            line: _currentLocation.Line,
            column: _currentLocation.Column,
            position: _currentLocation.Position);
    }

    /// <summary>
    /// Converts an LLVM type back to a RazorForge type name.
    /// Used for tracking type information in _tempTypes.
    /// </summary>
    /// <param name="llvmType">The LLVM type (e.g., "i64", "i32", "ptr")</param>
    /// <returns>The corresponding RazorForge type name</returns>
    private string GetRazorForgeTypeFromLLVM(string llvmType)
    {
        // Handle record/entity types (e.g., %uaddr, %saddr, %Text, etc.)
        if (llvmType.StartsWith("%"))
        {
            // Strip the % prefix to get the RazorForge type name
            return llvmType.Substring(1);
        }

        return llvmType switch
        {
            "i8" => "s8",
            "i16" => "s16",
            "i32" => "s32",
            "i64" => "s64",
            "i128" => "s128",
            "half" => "f16",
            "float" => "f32",
            "double" => "f64",
            "fp128" => "f128",
            "i1" => "bool",
            "ptr" or "i8*" => "uaddr",  // Generic pointer type
            "void" => "Blank",
            _ => throw CodeGenError.TypeResolutionFailed(typeName: llvmType,
                context: "unknown LLVM type for reverse mapping",
                file: _currentFileName,
                line: _currentLocation.Line,
                column: _currentLocation.Column,
                position: _currentLocation.Position)
        };
    }

    /// <summary>
    /// Handles generic type constructor calls like Text&lt;letter8&gt;(ptr: ptr).
    /// Looks up the __create__ constructor method and generates a call to it.
    /// </summary>
    private bool TryHandleGenericTypeConstructor(string typeName, List<Expression> arguments,
        string resultTemp, out string? result)
    {
        result = null;

        if (_loadedModules == null)
        {
            return false;
        }

        // Extract base type name (e.g., "Text" from "Text<letter8>")
        int genericStart = typeName.IndexOf(value: '<');
        if (genericStart < 0)
        {
            return false;
        }

        string baseTypeName = typeName[..genericStart];

        // Look for __create__ constructor with matching parameter signature
        foreach (KeyValuePair<string, ModuleResolver.ModuleInfo> moduleEntry in _loadedModules)
        {
            foreach (IAstNode decl in moduleEntry.Value.Ast.Declarations)
            {
                if (decl is not FunctionDeclaration funcDecl)
                {
                    continue;
                }

                // Check if this is a __create__ method for the type
                // Pattern: Text<letter8>.__create__ or Text<LetterType>.__create__
                if (!funcDecl.Name.Contains(value: ".__create__"))
                {
                    continue;
                }

                // Extract the type part of the function name
                int createIndex = funcDecl.Name.IndexOf(value: ".__create__");
                if (createIndex < 0)
                {
                    continue;
                }

                string funcTypeName = funcDecl.Name[..createIndex];

                // Check if this is a matching generic type (base name matches)
                // e.g., "Text<LetterType>" matches "Text<letter8>" or just "Text" matches
                int funcGenericStart = funcTypeName.IndexOf(value: '<');
                string funcBaseTypeName = funcGenericStart >= 0
                    ? funcTypeName[..funcGenericStart]
                    : funcTypeName;

                if (funcBaseTypeName != baseTypeName)
                {
                    continue;
                }

                // Check parameter count matches
                if (funcDecl.Parameters.Count != arguments.Count)
                {
                    continue;
                }

                // Check if parameter names match (for named arguments)
                bool paramsMatch = true;
                if (arguments.Count > 0 && arguments[index: 0] is NamedArgumentExpression)
                {
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        if (arguments[index: i] is NamedArgumentExpression namedArg)
                        {
                            bool foundMatch =
                                funcDecl.Parameters.Any(predicate: p => p.Name == namedArg.Name);
                            if (!foundMatch)
                            {
                                paramsMatch = false;
                                break;
                            }
                        }
                    }
                }

                if (!paramsMatch)
                {
                    continue;
                }

                // Found matching constructor - generate the call
                // Substitute the generic type parameters in the function name
                string mangledFuncName = typeName + ".__create__";
                string sanitizedFuncName = SanitizeFunctionName(name: mangledFuncName);

                // CRITICAL: Instantiate the generic method if it exists as a template
                // The template key is like "List<T>.__create__" and we need to instantiate it with concrete types
                string templateKey = funcDecl.Name; // e.g., "List<T>.__create__"
                if (_genericFunctionTemplates.ContainsKey(templateKey))
                {
                    // Extract concrete type arguments from the instantiated type name
                    // typeName is like "List<letter8>", extract ["letter8"]
                    var typeArgs = ExtractTypeArguments(genericType: typeName);
                    InstantiateGenericFunction(functionName: templateKey, typeArguments: typeArgs);
                }

                // Determine return type (should be the generic type itself, represented as a pointer)
                string returnType = "ptr";

                // Generate argument values
                var argList = new List<string>();
                foreach (Expression arg in arguments)
                {
                    Expression actualArg = arg is NamedArgumentExpression namedArg
                        ? namedArg.Value
                        : arg;
                    string argValue = actualArg.Accept(visitor: this);
                    string argType;
                    if (_tempTypes.TryGetValue(key: argValue,
                            value: out LLVMTypeInfo? argTypeInfo))
                    {
                        argType = argTypeInfo.LLVMType;
                    }
                    else
                    {
                        LLVMTypeInfo inferredType = GetTypeInfo(expr: actualArg);
                        argType = inferredType.LLVMType;
                    }

                    argList.Add(item: $"{argType} {argValue}");
                }

                string args = string.Join(separator: ", ", values: argList);
                _output.AppendLine(
                    handler: $"  {resultTemp} = call {returnType} @{sanitizedFuncName}({args})");

                // Track the result type
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: returnType,
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: typeName);

                result = resultTemp;
                return true;
            }
        }

        return false;
    }
}
