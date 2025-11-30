using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        string sanitizedName = SanitizeFunctionName(name: node.Name);

        // Skip if already declared in boilerplate
        if (_builtinExternals.Contains(item: sanitizedName))
        {
            return "";
        }

        // Generate external function declaration
        string paramTypes = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p =>
                MapRazorForgeTypeToLLVM(razorForgeType: p.Type?.Name ?? "void")));

        // Add variadic marker if needed
        if (node.IsVariadic)
        {
            paramTypes = string.IsNullOrEmpty(value: paramTypes)
                ? "..."
                : paramTypes + ", ...";
        }

        string returnType = node.ReturnType != null
            ? MapRazorForgeTypeToLLVM(razorForgeType: node.ReturnType.Name)
            : "void";

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
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64",
                    IsUnsigned: true,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                return resultTemp;

            case "align_of":
                // Get alignment of type
                int alignment = GetAlignment(typeName: typeName);
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {alignment}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64",
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
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i8*",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "Text<letter8>");
                return resultTemp;

            case "field_count":
                // Get number of fields in a struct/record type
                int fieldCount = GetFieldCount(typeName: typeName);
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {fieldCount}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64",
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
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "s64");
                return resultTemp;

            case "get_column_number":
                int column = node.Location.Column;
                _output.AppendLine(handler: $"  {resultTemp} = add i64 0, {column}");
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i64",
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
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i8*",
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
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i8*",
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
                _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i8*",
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
}
