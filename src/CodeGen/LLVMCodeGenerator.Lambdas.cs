using System.Text;
using Compilers.Shared.Analysis;
using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Partial class containing lambda expression code generation.
/// Handles arrow lambda expressions and type inference for lambda bodies.
/// </summary>
public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Generates LLVM IR for lambda expressions (arrow functions).
    /// Creates an internal function definition and returns a function pointer.
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
        lambdaBuilder.AppendLine(handler: $"; Lambda function {lambdaName}");
        lambdaBuilder.AppendLine(
            handler:
            $"define private {returnType} @{lambdaName}({string.Join(separator: ", ", values: paramList)}) {{");
        lambdaBuilder.AppendLine(value: "entry:");

        // Save current state
        var savedSymbolTypes = new Dictionary<string, string>(dictionary: _symbolTypes);
        var savedFunctionParameters = new HashSet<string>(collection: _functionParameters);
        bool savedHasReturn = _hasReturn;
        bool savedBlockTerminated = _blockTerminated;
        Dictionary<string, string>? savedTypeSubstitutions = _currentTypeSubstitutions;

        // Save the current output position to extract lambda body later
        int outputStartPos = _output.Length;

        _hasReturn = false;
        _blockTerminated = false;
        _functionParameters.Clear();

        // Register parameters in symbol table
        for (int i = 0; i < node.Parameters.Count; i++)
        {
            _symbolTypes[key: node.Parameters[index: i].Name] = paramTypes[index: i];
            _functionParameters.Add(item: node.Parameters[index: i].Name);
        }

        // Generate lambda body directly into _output
        string bodyResult = node.Body.Accept(visitor: this);

        // Extract the lambda body code that was generated
        string lambdaBodyCode = _output.ToString()
                                       .Substring(startIndex: outputStartPos);

        // Remove the lambda body code from main output
        _output.Length = outputStartPos;

        // Add the body code and return to lambda builder
        lambdaBuilder.Append(value: lambdaBodyCode);

        // Add return statement with the result
        if (returnType != "void")
        {
            lambdaBuilder.AppendLine(handler: $"  ret {returnType} {bodyResult}");
        }
        else
        {
            lambdaBuilder.AppendLine(value: "  ret void");
        }

        lambdaBuilder.AppendLine(value: "}");

        // Restore state
        _symbolTypes.Clear();
        foreach (KeyValuePair<string, string> kvp in savedSymbolTypes)
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
        _output.AppendLine(
            handler:
            $"  {resultTemp} = bitcast {returnType} ({paramTypeStr})* @{lambdaName} to ptr");

        // Track the type
        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "ptr",
            IsUnsigned: false,
            IsFloatingPoint: false,
            RazorForgeType:
            $"lambda<{string.Join(separator: ", ", values: paramTypes)}>->{returnType}");

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
            BinaryExpression bin => InferBinaryExpressionType(bin: bin,
                symbolTypes: tempSymbolTypes),
            IdentifierExpression id => tempSymbolTypes.TryGetValue(key: id.Name,
                value: out string? t)
                ? t
                : "i32",
            CallExpression => "i32", // Default for function calls
            ConditionalExpression cond => InferLambdaReturnType(body: cond.TrueExpression,
                parameters: parameters),
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
            TokenType.S64Literal or TokenType.U64Literal or TokenType.SyssintLiteral
                or TokenType.SysuintLiteral => "i64",
            TokenType.S128Literal or TokenType.U128Literal => "i128",

            // RazorForge unsuffixed integer -> i64
            // Suflae Integer is arbitrary precision (would need bigint library)
            TokenType.Integer => _language == Language.RazorForge
                ? "i64"
                : "ptr", // ptr for bigint struct

            // Float types - map to appropriate LLVM floating point widths
            TokenType.F16Literal => "half",
            TokenType.F32Literal => "float",
            TokenType.F64Literal => "double",
            TokenType.F128Literal => "fp128",

            // RazorForge unsuffixed decimal -> f64 (double)
            // Suflae Decimal is arbitrary precision (would need decimal library)
            TokenType.Decimal => _language == Language.RazorForge
                ? "double"
                : "ptr", // ptr for decimal struct

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
    private string InferBinaryExpressionType(BinaryExpression bin,
        Dictionary<string, string> symbolTypes)
    {
        // Comparison operators always return bool
        if (bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual or BinaryOperator.Less
            or BinaryOperator.LessEqual or BinaryOperator.Greater or BinaryOperator.GreaterEqual
            or BinaryOperator.And or BinaryOperator.Or)
        {
            return "i1";
        }

        // For arithmetic, infer from operands
        string leftType = bin.Left switch
        {
            LiteralExpression lit => InferLiteralType(lit: lit),
            IdentifierExpression id => symbolTypes.TryGetValue(key: id.Name, value: out string? t)
                ? t
                : "i32",
            BinaryExpression nested => InferBinaryExpressionType(bin: nested,
                symbolTypes: symbolTypes),
            _ => "i32"
        };

        return leftType;
    }

    /// <summary>
    /// Generates LLVM IR for type conversion expressions.
    /// </summary>
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
}
