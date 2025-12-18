using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    public string VisitImportedDeclaration(ExternalDeclaration node)
    {
        UpdateLocation(node.Location);
        string sanitizedName = SanitizeFunctionName(name: node.Name);

        // Skip if already declared in boilerplate
        if (_builtinExternals.Contains(item: sanitizedName))
        {
            return "";
        }

        // Generate external function declaration
        string paramTypes = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p =>
                p.Type != null ? MapRazorForgeTypeToLLVM(razorForgeType: p.Type.Name) : "i8*"));

        // Add variadic marker if needed
        if (node.IsVariadic)
        {
            paramTypes = string.IsNullOrEmpty(value: paramTypes)
                ? "..."
                : paramTypes + ", ...";
        }

        string returnType;
        if (node.ReturnType == null || node.ReturnType.Name == "void")
        {
            returnType = "void";
        }
        else
        {
            returnType = MapRazorForgeTypeToLLVM(razorForgeType: node.ReturnType.Name);
        }

        // Map calling convention to LLVM calling convention attribute
        string callingConventionAttr =
            MapCallingConventionToLLVM(callingConvention: node.CallingConvention);

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
                _output.AppendLine(
                    handler: $"declare {returnType} @{sanitizedName}({paramTypes})");
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

    /// <summary>
    /// Ensures a record type is defined in the output before it's used.
    /// This searches for the record definition in loaded modules and emits it if not already done.
    /// </summary>
    private void EnsureRecordTypeIsDefined(string typeName)
    {
        // Check if already emitted
        if (_emittedTypes.Contains(typeName))
        {
            return;
        }

        // Search for the record declaration in loaded modules
        if (_loadedModules != null)
        {
            foreach (var (_, moduleInfo) in _loadedModules)
            {
                foreach (IAstNode decl in moduleInfo.Ast.Declarations)
                {
                    if (decl is RecordDeclaration recordDecl && recordDecl.Name == typeName)
                    {
                        // Found it - generate the type definition
                        // For non-generic records, this will emit the type immediately
                        if (recordDecl.GenericParameters == null || recordDecl.GenericParameters.Count == 0)
                        {
                            VisitRecordDeclaration(recordDecl);
                        }
                        return;
                    }
                }
            }
        }

        // If we get here, the type wasn't found - it might be a built-in or will be defined later
        // Don't throw an error, just continue
    }

    private string MapRazorForgeTypeToLLVM(string razorForgeType)
    {
        return razorForgeType switch
        {
            // LLVM types (when used directly in intrinsics) - pass through
            "i1" => "i1",
            "i8" => "i8",
            "i16" => "i16",
            "i32" => "i32",
            "i64" => "i64",
            "i128" => "i128",
            "half" => "half",
            "float" => "float",
            "double" => "double",
            "fp128" => "fp128",
            "ptr" => "ptr",

            // RazorForge types - all map to their record wrapper types
            // This enforces the "everything is a record" philosophy
            "s8" => "%s8",
            "s16" => "%s16",
            "s32" => "%s32",
            "s64" => "%s64",
            "s128" => "%s128",
            "u8" => "%u8",
            "u16" => "%u16",
            "u32" => "%u32",
            "u64" => "%u64",
            "u128" => "%u128",
            "saddr" or "iptr" => "%saddr",
            "uptr" => "%uaddr", // uptr stays as record
            "f16" => "%f16",
            "f32" => "%f32",
            "f64" => "%f64",
            "f128" => "%f128",
            "bool" => "%bool",
            "letter" => "i32", // UTF-32
            "Text" => "ptr",
            "DynamicSlice" or "TemporarySlice" => "ptr",
            "Blank" => "void",

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
            "cuptr" or "cvoid" => _targetPlatform.GetPointerSizedIntType(),
            "cbool" => "i1",

            // C string type (null-terminated char pointer)
            "cstr" => "i8*",

            // Address types when used as C pointers
            "uaddr" => "i8*", // When uaddr is used as C pointer (for strlen, strcpy, etc.)

            _ => razorForgeType.StartsWith("c") || razorForgeType.Contains("<")
                ? "ptr"  // C FFI types and generic types default to pointer
                : $"%{razorForgeType}" // User-defined record/entity types use struct syntax
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
            "size_of" or "align_of" or "get_compile_type_name" or "field_names" or "field_count"
                or "has_method" => true,
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
                message: $"CompilerService intrinsic {functionName} requires a type argument");
        }

        TypeExpression typeArg = node.TypeArguments.First();
        string typeName = typeArg.Name;

        switch (functionName)
        {
            case "size_of":
                // Get size of type in bytes using LLVM's getelementptr trick
                int size = GetTypeSize(typeName: typeName);
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {size}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: true,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                return resultTemp;

            case "align_of":
                // Get alignment of type
                int alignment = GetAlignment(typeName: typeName);
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {alignment}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: true,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                return resultTemp;

            case "get_compile_type_name":
                // Return the type name as a string constant
                string typeNameStr = typeName;
                string strConstName = GetOrCreateStringConstant(value: typeNameStr);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = getelementptr [{typeNameStr.Length + 1} x i8], [{typeNameStr.Length + 1} x i8]* {strConstName}, i32 0, i32 0");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i8*",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "field_count":
                // Get number of fields in a struct/record type
                int fieldCount = GetFieldCount(typeName: typeName);
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {fieldCount}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: true,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                return resultTemp;

            case "field_names":
                // TODO: Return list of field names - requires runtime list construction
                _output.AppendLine(
                    handler:
                    $"  ; TODO: field_names<{typeName}>() - requires List<Text> construction");
                _output.AppendLine(handler: $"  {resultTemp} = inttoptr i64 0 to ptr");
                return resultTemp;

            case "has_method":
                // TODO: Check if type has a method - requires symbol table lookup
                _output.AppendLine(
                    handler: $"  ; TODO: has_method<{typeName}>() - requires symbol table lookup");
                _output.AppendLine(handler: $"  {resultTemp} = add i1 0, 0");
                return resultTemp;

            default:
                throw new NotImplementedException(
                    message: $"CompilerService intrinsic {functionName} not implemented");
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
        if (IsPrimitiveType(typeName: typeName))
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
            "s8" or "s16" or "s32" or "s64" or "s128" or "u8" or "u16" or "u32" or "u64" or "u128"
                or "f16" or "f32" or "f64" or "f128" or "bool" or "letter8" or "letter16"
                or "letter32" or "uaddr" or "saddr" => true,
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
        string escaped = value.Replace(oldValue: "\\", newValue: "\\5C")
                              .Replace(oldValue: "\"", newValue: "\\22");
        _stringConstants.Add(
            item:
            $"{strConst} = private unnamed_addr constant [{len} x i8] c\"{escaped}\\00\", align 1");

        return strConst;
    }

    /// <summary>
    /// Checks if the function is a source location intrinsic.
    /// </summary>
    private bool IsSourceLocationIntrinsic(string functionName)
    {
        return functionName switch
        {
            "get_line_number" or "get_column_number" or "get_file_name" or "get_caller_name"
                or "get_current_module" => true,
            _ => false
        };
    }

    /// <summary>
    /// Handles source location intrinsic calls.
    /// These are compile-time evaluated based on the AST node's source location.
    /// </summary>
    private string HandleSourceLocationIntrinsic(CallExpression node, string functionName,
        string resultTemp)
    {
        switch (functionName)
        {
            case "get_line_number":
                int line = node.Location.Line;
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {line}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "s64");
                return resultTemp;

            case "get_column_number":
                int column = node.Location.Column;
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {column}");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "s64");
                return resultTemp;

            case "get_file_name":
                // Get file name from current context (would need to be passed through)
                string fileName = _currentFileName ?? "unknown";
                string fileNameConst = GetOrCreateStringConstant(value: fileName);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = getelementptr [{fileName.Length + 1} x i8], [{fileName.Length + 1} x i8]* {fileNameConst}, i32 0, i32 0");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i8*",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "get_caller_name":
                // Get the current function name
                string callerName = _currentFunctionName ?? "unknown";
                string callerConst = GetOrCreateStringConstant(value: callerName);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = getelementptr [{callerName.Length + 1} x i8], [{callerName.Length + 1} x i8]* {callerConst}, i32 0, i32 0");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i8*",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "get_current_module":
                // Get module name from file path
                string moduleName = _currentFileName != null
                    ? Path.GetFileNameWithoutExtension(path: _currentFileName)
                    : "unknown";
                string moduleConst = GetOrCreateStringConstant(value: moduleName);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = getelementptr [{moduleName.Length + 1} x i8], [{moduleName.Length + 1} x i8]* {moduleConst}, i32 0, i32 0");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i8*",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "Text<letter8>");
                return resultTemp;

            default:
                throw new NotImplementedException(
                    message: $"Source location intrinsic {functionName} not implemented");
        }
    }

    /// <summary>
    /// Checks if the function is an error intrinsic (verify!, breach!, stop!).
    /// </summary>
    private bool IsErrorIntrinsic(string functionName)
    {
        return functionName switch
        {
            "verify!" or "breach!" or "stop!" => true,
            _ => false
        };
    }

    /// <summary>
    /// Handles error intrinsic calls (verify!, breach!, stop!).
    /// These throw specific Crashable error types.
    /// </summary>
    private string HandleErrorIntrinsic(CallExpression node, string functionName,
        string resultTemp)
    {
        _hasReturn = true;
        _blockTerminated = true;

        switch (functionName)
        {
            case "verify!":
                // verify!(condition) or verify!(condition, message)
                // Throws VerificationFailedError if condition is false
                if (node.Arguments.Count == 0)
                {
                    throw new InvalidOperationException(
                        message: "verify!() requires at least one argument (condition)");
                }

                string condition = node.Arguments[index: 0]
                                       .Accept(visitor: this);
                string verifyTrueLabel = GetNextLabel();
                string verifyFalseLabel = GetNextLabel();

                _output.AppendLine(
                    handler:
                    $"  br i1 {condition}, label %{verifyTrueLabel}, label %{verifyFalseLabel}");

                // False branch - throw error
                _output.AppendLine(handler: $"{verifyFalseLabel}:");
                string verifyMessage = "Verification failed";
                if (node.Arguments.Count > 1 &&
                    node.Arguments[index: 1] is LiteralExpression msgLit &&
                    msgLit.Value is string msgStr)
                {
                    verifyMessage = msgStr;
                }

                EmitThrowError(errorTypeName: "VerificationFailedError", message: verifyMessage);

                // True branch - continue execution
                _output.AppendLine(handler: $"{verifyTrueLabel}:");
                _blockTerminated = false;
                _hasReturn = false;
                return resultTemp;

            case "breach!":
                // breach!() or breach!(message)
                // Throws LogicBreachedError - indicates unreachable code was reached
                string breachMessage = "Logic breach: unreachable code executed";
                if (node.Arguments.Count > 0 &&
                    node.Arguments[index: 0] is LiteralExpression breachMsgLit &&
                    breachMsgLit.Value is string breachMsgStr)
                {
                    breachMessage = breachMsgStr;
                }

                EmitThrowError(errorTypeName: "LogicBreachedError", message: breachMessage);
                return resultTemp;

            case "stop!":
                // stop!() or stop!(message)
                // Throws UserTerminationError - explicit program termination
                string stopMessage = "Program terminated by user";
                if (node.Arguments.Count > 0 &&
                    node.Arguments[index: 0] is LiteralExpression stopMsgLit &&
                    stopMsgLit.Value is string stopMsgStr)
                {
                    stopMessage = stopMsgStr;
                }

                EmitThrowError(errorTypeName: "UserTerminationError", message: stopMessage);
                return resultTemp;

            default:
                throw new NotImplementedException(
                    message: $"Error intrinsic {functionName} not implemented");
        }
    }

    /// <summary>
    /// Emits code to throw a Crashable error with the given type name and message.
    /// </summary>
    private void EmitThrowError(string errorTypeName, string message)
    {
        // Create string constants for error type and message
        string errorTypeConst = $"@.str_errtype{_tempCounter++}";
        int typeLen = errorTypeName.Length + 1;

        if (_stringConstants == null)
        {
            _stringConstants = new List<string>();
        }

        _stringConstants.Add(
            item:
            $"{errorTypeConst} = private unnamed_addr constant [{typeLen} x i8] c\"{errorTypeName}\\00\", align 1");

        string msgConst = $"@.str_errmsg{_tempCounter++}";
        int msgLen = message.Length + 1;
        string escapedMsg = message.Replace(oldValue: "\\", newValue: "\\5C")
                                   .Replace(oldValue: "\"", newValue: "\\22");
        _stringConstants.Add(
            item:
            $"{msgConst} = private unnamed_addr constant [{msgLen} x i8] c\"{escapedMsg}\\00\", align 1");

        string typePtr = GetNextTemp();
        _output.AppendLine(
            handler:
            $"  {typePtr} = getelementptr [{typeLen} x i8], [{typeLen} x i8]* {errorTypeConst}, i32 0, i32 0");

        string messagePtr = GetNextTemp();
        _output.AppendLine(
            handler:
            $"  {messagePtr} = getelementptr [{msgLen} x i8], [{msgLen} x i8]* {msgConst}, i32 0, i32 0");

        // Use stack trace infrastructure to throw
        _stackTraceCodeGen?.EmitThrow(errorTypePtr: typePtr, messagePtr: messagePtr);
    }

    /// <summary>
    /// Visits a preset declaration (compile-time constant).
    /// Generates a global constant in LLVM IR.
    /// </summary>
    public string VisitPresetDeclaration(PresetDeclaration node)
    {
        UpdateLocation(node.Location);
        string sanitizedName = SanitizeFunctionName(name: node.Name);
        string llvmType = MapRazorForgeTypeToLLVM(razorForgeType: node.Type.Name);

        // Track the preset type so it can be loaded correctly later
        _symbolTypes[key: node.Name] = llvmType;
        _symbolRfTypes[key: node.Name] = node.Type.Name;
        _globalConstants.Add(item: node.Name); // Mark as global constant

        // Determine the appropriate zero/null value for the type
        string zeroValue;
        if (llvmType.EndsWith(value: "*") || llvmType == "ptr")
        {
            zeroValue = "null";
        }
        else if (llvmType is "half" or "float" or "double" or "fp128")
        {
            zeroValue = "0.0";
        }
        else if (llvmType.StartsWith("%") && !llvmType.EndsWith("*"))
        {
            // Struct/record types need zeroinitializer
            zeroValue = "zeroinitializer";
        }
        else
        {
            zeroValue = "0";
        }

        // Evaluate the value expression for the constant
        // For simple literals, we can emit them directly
        if (node.Value is LiteralExpression literal)
        {
            string value = literal.Value?.ToString() ?? zeroValue;
            // Handle null pointer case
            if (value == "0" && (llvmType.EndsWith(value: "*") || llvmType == "ptr"))
            {
                value = "null";
            }

            // Handle floating-point constants - ensure proper format for LLVM
            if (llvmType is "fp128" or "half" or "float" or "double")
            {
                if (literal.Value is double or decimal or float or int or long)
                {
                    double doubleValue = Convert.ToDouble(literal.Value);

                    // Use hex format for all floating-point types for maximum compatibility
                    long bits = BitConverter.DoubleToInt64Bits(doubleValue);

                    if (llvmType == "fp128")
                    {
                        // fp128 uses 0xL prefix
                        value = $"0xL{bits:X16}";
                    }
                    else if (llvmType == "half")
                    {
                        // half uses 0xH prefix
                        // Need to convert double to half-precision
                        ushort halfBits = (ushort)((bits >> 48) & 0xFFFF);
                        value = $"0xH{halfBits:X4}";
                    }
                    else if (llvmType == "float")
                    {
                        // For float, we need to ensure proper formatting
                        // Convert to float first, then format appropriately
                        float floatValue = (float)doubleValue;

                        // Special case handling for special values
                        if (float.IsPositiveInfinity(floatValue))
                        {
                            value = "0x7F800000";
                        }
                        else if (float.IsNegativeInfinity(floatValue))
                        {
                            value = "0xFF800000";
                        }
                        else if (float.IsNaN(floatValue))
                        {
                            value = "0x7FC00000";
                        }
                        else if (floatValue == 0.0f)
                        {
                            value = "0.0";
                        }
                        else
                        {
                            // LLVM IR hex floats are always 64-bit (double precision)
                            // For float, we need to convert to double first
                            double asDouble = (double)floatValue;
                            ulong doubleBits = (ulong)BitConverter.DoubleToInt64Bits(asDouble);
                            value = $"0x{doubleBits:X16}";
                        }
                    }
                    else // double
                    {
                        // For double, similar handling
                        if (double.IsPositiveInfinity(doubleValue))
                        {
                            value = "0x7FF0000000000000";
                        }
                        else if (double.IsNegativeInfinity(doubleValue))
                        {
                            value = "0xFFF0000000000000";
                        }
                        else if (double.IsNaN(doubleValue))
                        {
                            value = "0x7FF8000000000000";
                        }
                        else if (doubleValue == 0.0)
                        {
                            value = "0.0";
                        }
                        else
                        {
                            // Use IEEE 754 hexadecimal format for reliability
                            ulong doubleBits = (ulong)bits;
                            value = $"0x{doubleBits:X16}";
                        }
                    }
                }
            }

            // For record types, wrap the primitive value in a struct literal
            if (llvmType.StartsWith("%") && !llvmType.EndsWith("*") && llvmType != "ptr")
            {
                // Ensure the record type is defined before we use it in a constant
                // This is necessary because constants are emitted early in the code generation
                string recordTypeName = llvmType.Substring(1); // Remove the % prefix
                EnsureRecordTypeIsDefined(recordTypeName);

                // Check if this record has multiple fields
                bool isMultiField = _recordFields.ContainsKey(recordTypeName) &&
                                   _recordFields[recordTypeName].Count > 1;

                // If using zeroinitializer OR if it's a multi-field record, emit it directly
                if (value == "zeroinitializer" || isMultiField)
                {
                    _output.AppendLine(
                        handler: $"@{sanitizedName} = constant {llvmType} zeroinitializer");
                }
                else
                {
                    // Wrap the primitive value in a struct literal (single-field records only)
                    // Check the actual field type from record fields
                    string fieldType;
                    if (_recordFields.TryGetValue(recordTypeName, out var fields) && fields.Count > 0)
                    {
                        fieldType = fields[0].Type;
                    }
                    else
                    {
                        // Fallback: infer from type name
                        fieldType = InferPrimitiveTypeFromRecordName(llvmType);
                    }

                    // For field types that are themselves record types (like %u32), use zeroinitializer
                    if (fieldType.StartsWith("%") && !fieldType.EndsWith("*") && fieldType != "ptr")
                    {
                        _output.AppendLine(
                            handler: $"@{sanitizedName} = constant {llvmType} {{ {fieldType} zeroinitializer }}");
                        return "";
                    }

                    string primitiveType = fieldType;

                    // For floating-point record types, we need to convert the value to hex format
                    // if it's not already in the correct format
                    if (primitiveType is "fp128" or "half" or "float" or "double" &&
                        !value.StartsWith("0x") && value != "0.0" &&
                        literal.Value is double or decimal or float or int or long)
                    {
                        double doubleValue = Convert.ToDouble(literal.Value);
                        long bits = BitConverter.DoubleToInt64Bits(doubleValue);

                        if (primitiveType == "fp128")
                        {
                            value = $"0xL{bits:X16}";
                        }
                        else if (primitiveType == "half")
                        {
                            ushort halfBits = (ushort)((bits >> 48) & 0xFFFF);
                            value = $"0xH{halfBits:X4}";
                        }
                        else if (primitiveType == "float")
                        {
                            float floatValue = (float)doubleValue;
                            if (float.IsPositiveInfinity(floatValue))
                                value = "0x7F800000";
                            else if (float.IsNegativeInfinity(floatValue))
                                value = "0xFF800000";
                            else if (float.IsNaN(floatValue))
                                value = "0x7FC00000";
                            else
                            {
                                double asDouble = (double)floatValue;
                                ulong doubleBits = (ulong)BitConverter.DoubleToInt64Bits(asDouble);
                                value = $"0x{doubleBits:X16}";
                            }
                        }
                        else // double
                        {
                            if (double.IsPositiveInfinity(doubleValue))
                                value = "0x7FF0000000000000";
                            else if (double.IsNegativeInfinity(doubleValue))
                                value = "0xFFF0000000000000";
                            else if (double.IsNaN(doubleValue))
                                value = "0x7FF8000000000000";
                            else
                            {
                                ulong doubleBits = (ulong)bits;
                                value = $"0x{doubleBits:X16}";
                            }
                        }
                    }

                    _output.AppendLine(
                        handler: $"@{sanitizedName} = constant {llvmType} {{ {primitiveType} {value} }}");
                }
            }
            else
            {
                _output.AppendLine(
                    handler: $"@{sanitizedName} = constant {llvmType} {value}");
            }
        }
        else
        {
            // For more complex expressions, we need to evaluate at compile time
            // For now, emit a placeholder
            _output.AppendLine(
                handler: $"; TODO: Evaluate preset {node.Name} at compile time");
            _output.AppendLine(
                handler: $"@{sanitizedName} = constant {llvmType} {zeroValue}");
        }

        return "";
    }
}
