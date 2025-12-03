using Compilers.Shared.AST;
using Compilers.Shared.Errors;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        _currentLocation = node.Location;
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

                return HandleDangerZoneFunction(node: node,
                    functionName: functionName,
                    llvmType: dangerLlvmType,
                    typeName: dangerTypeArg.Name,
                    resultTemp: resultTemp);
            }

            // Check for CompilerService intrinsics
            if (IsCompilerServiceIntrinsic(functionName: functionName))
            {
                return HandleCompilerServiceIntrinsic(node: node,
                    functionName: functionName,
                    resultTemp: resultTemp);
            }

            // Check for user-defined generic function
            if (_genericFunctionTemplates.ContainsKey(key: functionName))
            {
                // Get the concrete type arguments
                var typeArgs = node.TypeArguments
                                   .Select(selector: t => t.Name)
                                   .ToList();

                // Instantiate the generic function (queues for later code generation)
                string mangledName = InstantiateGenericFunction(functionName: functionName,
                    typeArguments: typeArgs);

                // Generate call to the instantiated function
                var argTemps = new List<string>();
                var argTypes = new List<string>();
                foreach (Expression arg in node.Arguments)
                {
                    string argTemp = arg.Accept(visitor: this);
                    if (!_tempTypes.TryGetValue(key: argTemp, value: out LLVMTypeInfo? ti))
                    {
                        throw CodeGenError.TypeResolutionFailed(
                            typeName: argTemp,
                            context: "could not determine type of argument in generic function call",
                            file: _currentFileName,
                            line: arg.Location.Line,
                            column: arg.Location.Column,
                            position: arg.Location.Position);
                    }
                    argTemps.Add(item: argTemp);
                    argTypes.Add(item: ti.LLVMType);
                }

                string argList = string.Join(separator: ", ",
                    values: argTemps.Zip(second: argTypes,
                        resultSelector: (temp, type) => $"{type} {temp}"));

                // Determine return type from function signature
                string returnType = LookupFunctionReturnType(functionName: mangledName);

                _output.AppendLine(
                    handler: $"  {resultTemp} = call {returnType} @{mangledName}({argList})");
                return resultTemp;
            }

            // Check for generic type constructor calls like Text<letter8>(ptr: ptr)
            // The function name (e.g., "Text") combined with type arguments makes a full type name
            string typeArgStr = string.Join(separator: ", ",
                values: node.TypeArguments.Select(selector: t => t.Name));
            string genericTypeName = $"{functionName}<{typeArgStr}>";

            if (TryHandleGenericTypeConstructor(typeName: genericTypeName,
                    arguments: node.Arguments,
                    resultTemp: resultTemp,
                    result: out string? constructorResult))
            {
                return constructorResult!;
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
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: typeArg.Name);
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
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: typeArg.Name);
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
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(
                    LLVMType: MapRazorForgeTypeToLLVM(razorForgeType: typeArg.Name),
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: typeArg.Name);
                break;

            default:
                // Check if this is a user-defined generic function that needs instantiation
                if (node.Object is IdentifierExpression funcIdentifier)
                {
                    string baseFunctionName = funcIdentifier.Name;

                    // Check if we have a template for this function
                    if (_genericFunctionTemplates.ContainsKey(key: baseFunctionName))
                    {
                        // Get the concrete type arguments
                        var typeArgs = node.TypeArguments
                                           .Select(selector: t => t.Name)
                                           .ToList();

                        // Instantiate the generic function (generates code if not already done)
                        string mangledName =
                            InstantiateGenericFunction(functionName: baseFunctionName,
                                typeArguments: typeArgs);

                        // Generate call to the instantiated function
                        var argTemps = new List<string>();
                        var argTypes = new List<string>();
                        foreach (Expression arg in node.Arguments)
                        {
                            string argTemp = arg.Accept(visitor: this);
                            if (!_tempTypes.TryGetValue(key: argTemp, value: out LLVMTypeInfo? ti))
                            {
                                throw CodeGenError.TypeResolutionFailed(
                                    typeName: argTemp,
                                    context: "could not determine type of argument in generic function call",
                                    file: _currentFileName,
                                    line: arg.Location.Line,
                                    column: arg.Location.Column,
                                    position: arg.Location.Position);
                            }
                            argTemps.Add(item: argTemp);
                            argTypes.Add(item: ti.LLVMType);
                        }

                        string argList = string.Join(separator: ", ",
                            values: argTemps.Zip(second: argTypes,
                                resultSelector: (temp, type) => $"{type} {temp}"));

                        // Determine return type from function signature
                        string returnType = LookupFunctionReturnType(functionName: mangledName);

                        _output.AppendLine(
                            handler:
                            $"  {resultTemp} = call {returnType} @{mangledName}({argList})");
                        return resultTemp;
                    }
                }

                // Check if this is a static method call on a type (e.g., Text<letter8>.from_cstr)
                // In this case, node.Object is a TypeExpression or GenericMemberExpression
                string? staticTypeName = null;
                if (node.Object is TypeExpression typeExpr)
                {
                    staticTypeName = typeExpr.Name;
                }
                else if (node.Object is GenericMemberExpression genMemberExpr)
                {
                    // e.g., Text<letter8> -> "Text<letter8>"
                    string baseType = genMemberExpr.Object is TypeExpression baseTypeExpr
                        ? baseTypeExpr.Name
                        : genMemberExpr.Object.Accept(visitor: this);
                    string genericArgs = string.Join(separator: ", ",
                        values: genMemberExpr.TypeArguments.Select(selector: t => t.Name));
                    staticTypeName = $"{genMemberExpr.MemberName}<{genericArgs}>";
                }

                // For static method calls, we don't have an object temp
                string? objMethodTemp = null;
                string? objectTypeName = staticTypeName;

                if (staticTypeName == null)
                {
                    // Fall through to method call on object instance
                    objMethodTemp = node.Object.Accept(visitor: this);

                    // Get the object's RazorForge type for method lookup
                    // Try multiple sources: _tempTypes, then the expression's ResolvedType
                    if (_tempTypes.TryGetValue(key: objMethodTemp, value: out LLVMTypeInfo? objTypeInfo))
                    {
                        objectTypeName = objTypeInfo.RazorForgeType;
                    }
                    else if (node.Object.ResolvedType != null)
                    {
                        objectTypeName = node.Object.ResolvedType.Name;
                    }
                }

                var methodTypeArgs = node.TypeArguments
                                         .Select(selector: t => t.Name)
                                         .ToList();
                string methodMangledName = methodTypeArgs.Count > 0
                    ? $"{node.MethodName}_{string.Join(separator: "_", values: methodTypeArgs)}"
                    : node.MethodName;

                var methodArgTemps = new List<string>();
                var methodArgTypes = new List<string>();
                foreach (Expression arg in node.Arguments)
                {
                    string argTemp = arg.Accept(visitor: this);
                    if (!_tempTypes.TryGetValue(key: argTemp, value: out LLVMTypeInfo? ti))
                    {
                        throw CodeGenError.TypeResolutionFailed(
                            typeName: argTemp,
                            context: "could not determine type of argument in method call",
                            file: _currentFileName,
                            line: arg.Location.Line,
                            column: arg.Location.Column,
                            position: arg.Location.Position);
                    }
                    methodArgTemps.Add(item: argTemp);
                    methodArgTypes.Add(item: ti.LLVMType);
                }

                string methodArgList = string.Join(separator: ", ",
                    values: methodArgTemps.Zip(second: methodArgTypes,
                        resultSelector: (temp, type) => $"{type} {temp}"));

                // Determine return type from method signature
                string methodReturnType = LookupMethodReturnType(objectTypeName: objectTypeName, methodName: methodMangledName);

                // Generate the call - static methods don't have an object parameter
                if (staticTypeName != null)
                {
                    // Static method call - just pass the arguments
                    _output.AppendLine(
                        handler:
                        $"  {resultTemp} = call {methodReturnType} @{SanitizeFunctionName(name: objectTypeName + "." + methodMangledName)}({methodArgList})");
                }
                else
                {
                    // Instance method call - pass object as first argument
                    _output.AppendLine(
                        handler:
                        $"  {resultTemp} = call {methodReturnType} @{methodMangledName}(ptr {objMethodTemp}{(methodArgTemps.Count > 0 ? ", " + methodArgList : "")})");
                }
                return resultTemp;
        }

        return resultTemp;
    }

    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        _currentLocation = node.Location;
        // TODO: Implement generic member access
        return GetNextTemp();
    }

    public string VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        _currentLocation = node.Location;
        string objectTemp = node.Object.Accept(visitor: this);
        string resultTemp = GetNextTemp();

        switch (node.OperationName)
        {
            case "size":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_size(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                break;

            case "address":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_address(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                break;

            case "is_valid":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i1 @slice_is_valid(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i1",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "bool");
                break;

            case "unsafe_ptr":
                string offsetTemp = node.Arguments[index: 0]
                                        .Accept(visitor: this);
                _output.AppendLine(
                    handler:
                    $"  {resultTemp} = call i64 @slice_unsafe_ptr(ptr {objectTemp}, i64 {offsetTemp})");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
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
                if (_tempTypes.TryGetValue(key: objectTemp, value: out LLVMTypeInfo? objType))
                {
                    _tempTypes[key: resultTemp] = objType;
                }

                break;

            case "hijack":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call ptr @slice_hijack(ptr {objectTemp})");
                if (_tempTypes.TryGetValue(key: objectTemp, value: out LLVMTypeInfo? hijackType))
                {
                    _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "ptr",
                        IsUnsigned: false,
                        IsFloatingPoint: false,
                        RazorForgeType: $"Hijacked<{hijackType.RazorForgeType}>");
                }

                break;

            case "refer":
                _output.AppendLine(
                    handler: $"  {resultTemp} = call i64 @slice_refer(ptr {objectTemp})");
                _tempTypes[key: resultTemp] = new LLVMTypeInfo(LLVMType: "i64",
                    IsUnsigned: false,
                    IsFloatingPoint: false,
                    RazorForgeType: "uaddr");
                break;

            default:
                throw new NotImplementedException(
                    message: $"Memory operation {node.OperationName} not implemented");
        }

        return resultTemp;
    }

    public string VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        _currentLocation = node.Location;
        string resultTemp = GetNextTemp();
        string intrinsicName = node.IntrinsicName;

        // Dispatch to specific intrinsic handler based on category
        if (intrinsicName.StartsWith(value: "load") || intrinsicName.StartsWith(value: "store") ||
            intrinsicName.StartsWith(value: "volatile_") || intrinsicName == "bitcast" ||
            intrinsicName == "invalidate")
        {
            return EmitMemoryIntrinsic(node: node, resultTemp: resultTemp);
        }
        // Arithmetic operations - both with suffixes (add.wrapping) and bare (add, sdiv, neg)
        else if (intrinsicName.StartsWith(value: "add.") ||
                 intrinsicName.StartsWith(value: "sub.") ||
                 intrinsicName.StartsWith(value: "mul.") ||
                 intrinsicName.StartsWith(value: "div.") ||
                 intrinsicName.StartsWith(value: "rem.") || intrinsicName == "add" ||
                 intrinsicName == "sub" || intrinsicName == "mul" || intrinsicName == "sdiv" ||
                 intrinsicName == "udiv" || intrinsicName == "srem" || intrinsicName == "urem" ||
                 intrinsicName == "neg")
        {
            return EmitArithmeticIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName.StartsWith(value: "icmp.") ||
                 intrinsicName.StartsWith(value: "fcmp."))
        {
            return EmitComparisonIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName == "and" || intrinsicName == "or" || intrinsicName == "xor" ||
                 intrinsicName == "not" || intrinsicName == "shl" || intrinsicName == "lshr" ||
                 intrinsicName == "ashr")
        {
            return EmitBitwiseIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName == "trunc" || intrinsicName == "zext" || intrinsicName == "sext" ||
                 intrinsicName == "fptrunc" || intrinsicName == "fpext" ||
                 intrinsicName == "fptoui" || intrinsicName == "fptosi" ||
                 intrinsicName == "uitofp" || intrinsicName == "sitofp")
        {
            return EmitConversionIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName == "sqrt" || intrinsicName == "abs" || intrinsicName == "fabs" ||
                 intrinsicName == "copysign" || intrinsicName == "floor" ||
                 intrinsicName == "ceil" || intrinsicName == "trunc_float" ||
                 intrinsicName == "round" || intrinsicName == "pow" || intrinsicName == "exp" ||
                 intrinsicName == "log" || intrinsicName == "log10" || intrinsicName == "sin" ||
                 intrinsicName == "cos")
        {
            return EmitMathIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName.StartsWith(value: "atomic."))
        {
            return EmitAtomicIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName == "ctpop" || intrinsicName == "ctlz" || intrinsicName == "cttz" ||
                 intrinsicName == "bswap" || intrinsicName == "bitreverse")
        {
            return EmitBitManipIntrinsic(node: node, resultTemp: resultTemp);
        }
        else if (intrinsicName == "sizeof" || intrinsicName == "alignof")
        {
            return EmitTypeInfoIntrinsic(node: node, resultTemp: resultTemp, intrinsicName: intrinsicName);
        }
        else
        {
            throw new NotImplementedException(
                message: $"Intrinsic {intrinsicName} not implemented");
        }
    }

    public string VisitNativeCallExpression(NativeCallExpression node)
    {
        _currentLocation = node.Location;
        string resultTemp = GetNextTemp();
        string functionName = node.FunctionName;

        // Build arguments list
        var argTemps = new List<string>();
        var argTypes = new List<string>();

        foreach (Expression arg in node.Arguments)
        {
            string argTemp = arg.Accept(visitor: this);
            argTemps.Add(item: argTemp);

            // Determine argument type based on expression type
            // For now, default to i64 for numeric types and i8* for pointers
            string argType = DetermineNativeArgType(expr: arg);
            argTypes.Add(item: argType);
        }

        // Build the call
        string argsStr = string.Join(separator: ", ",
            values: argTemps.Zip(second: argTypes,
                resultSelector: (temp, type) => $"{type} {temp}"));

        // Determine return type (default to i8* for pointer-returning functions, i64 otherwise)
        string returnType = DetermineNativeFunctionReturnType(functionName: functionName);

        if (returnType == "void")
        {
            _output.AppendLine(value: $"  call void @{functionName}({argsStr})");
            return ""; // void functions don't return a value
        }
        else
        {
            _output.AppendLine(
                value: $"  {resultTemp} = call {returnType} @{functionName}({argsStr})");
            return resultTemp;
        }
    }

    private string DetermineNativeArgType(Expression expr)
    {
        // For native calls, we use pointer types for addresses and i64 for integers
        if (expr is MemberExpression memberExpr && memberExpr.PropertyName == "handle")
        {
            return "i8*"; // Handle fields are typically pointers
        }
        else if (expr is LiteralExpression litExpr)
        {
            // String literals are pointers
            if (litExpr.LiteralType == TokenType.TextLiteral ||
                litExpr.LiteralType == TokenType.Text8Literal ||
                litExpr.LiteralType == TokenType.Text16Literal)
            {
                return "i8*";
            }
        }
        else if (expr is IdentifierExpression idExpr)
        {
            // Check if we know the type from symbol table
            if (_symbolTypes.TryGetValue(key: idExpr.Name, value: out string? varType))
            {
                if (varType == "uaddr" || varType.Contains(value: "*") || varType == "text" ||
                    varType == "Text")
                {
                    return "i8*";
                }
            }
        }

        // Default to i64 for integer values
        return "i64";
    }

    private string DetermineNativeFunctionReturnType(string functionName)
    {
        // Known return types for common native functions
        if (!functionName.StartsWith(value: "rf_bigint_") &&
            !functionName.StartsWith(value: "rf_bigdec_"))
        {
            return functionName switch
            {
                "printf" => "i32",
                "puts" => "i32",
                "putchar" => "i32",
                "malloc" => "i8*",
                "calloc" => "i8*",
                "realloc" => "i8*",
                "free" => "void",
                "memcpy" => "i8*",
                "memmove" => "i8*",
                "memset" => "i8*",
                "strlen" => "i64",
                "strcmp" => "i32",
                "strcpy" => "i8*",
                "strdup" => "i8*",
                _ => "i64" // Default return type
            };
        }

        // BigInt/BigDec functions that return handles
        if (functionName.EndsWith(value: "_new") || functionName.EndsWith(value: "_copy"))
        {
            return "i8*"; // Returns a pointer/handle
        }

        // Comparison and query functions return i32
        if (functionName.Contains(value: "_cmp") || functionName.Contains(value: "_is_"))
        {
            return "i32";
        }

        // Get functions may return values
        if (functionName.Contains(value: "_get_i64"))
        {
            return "i64";
        }

        if (functionName.Contains(value: "_get_u64"))
        {
            return "i64";
        }

        if (functionName.Contains(value: "_get_str"))
        {
            return "i8*"; // Returns a string pointer
        }

        // Default for bigint/bigdec operations that modify in-place
        return "i32"; // Return status code

        // Standard C library functions
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        _currentLocation = node.Location;
        // Add comment to indicate unsafe block
        _output.AppendLine(value: "  ; === DANGER BLOCK START ===");
        node.Body.Accept(visitor: this);
        _output.AppendLine(value: "  ; === DANGER BLOCK END ===");
        return "";
    }

}