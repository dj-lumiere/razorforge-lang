using Compilers.Shared.AST;

namespace Compilers.Shared.CodeGen;

public partial class LLVMCodeGenerator
{
    /// <summary>
    /// Generates LLVM code for a throw statement in the context of a failable function.
    /// Handles error type and message extraction, creates string constants for LLVM IR,
    /// and emits the required instructions to simulate a program crash with accompanying error details.
    /// </summary>
    /// <param name="node">The throw statement node containing information about the error to be thrown,
    /// including its type and message.</param>
    /// <returns>A string containing the generated LLVM IR for the throw operation.</returns>
    public string VisitThrowStatement(ThrowStatement node)
    {
        // throw statement in failable function crashes the program
        // The try_/check_ variants transform this to return None/Error respectively
        _hasReturn = true;
        _blockTerminated = true;

        _output.AppendLine(handler: $"  ; throw - capturing stack and crashing with error");

        // Extract error type name and try to get message from constructor args
        string errorTypeName = "Error";
        string? customMessage = null;

        if (node.Error is CallExpression callExpr)
        {
            if (callExpr.Callee is IdentifierExpression identExpr)
            {
                errorTypeName = identExpr.Name;

                // Try dynamic throw for error types with fields (calls crash_message equivalent)
                if (TryGenerateDynamicThrow(errorTypeName: errorTypeName, callExpr: callExpr))
                {
                    return "";
                }

                // Try to extract message from constructor arguments
                customMessage = ExtractErrorMessageFromArgs(arguments: callExpr.Arguments);
            }
        }

        // Use custom message if provided, otherwise fall back to default
        string errorMessage = customMessage ?? GetDefaultErrorMessage(errorTypeName: errorTypeName);

        // Create string constants for error type and message
        if (_stringConstants == null)
        {
            _stringConstants = new List<string>();
        }

        string errorTypeConst = $"@.str_errtype{_tempCounter++}";
        int typeLen = errorTypeName.Length + 1;
        _stringConstants.Add(item: $"{errorTypeConst} = private unnamed_addr constant [{typeLen} x i8] c\"{errorTypeName}\\00\", align 1");

        string typePtr = GetNextTemp();
        _output.AppendLine(handler: $"  {typePtr} = getelementptr [{typeLen} x i8], [{typeLen} x i8]* {errorTypeConst}, i32 0, i32 0");

        string msgConst = $"@.str_errmsg{_tempCounter++}";
        int msgLen = errorMessage.Length + 1;
        string escapedMsg = errorMessage.Replace(oldValue: "\\", newValue: "\\5C")
                                        .Replace(oldValue: "\"", newValue: "\\22");
        _stringConstants.Add(item: $"{msgConst} = private unnamed_addr constant [{msgLen} x i8] c\"{escapedMsg}\\00\", align 1");

        string messagePtr = GetNextTemp();
        _output.AppendLine(handler: $"  {messagePtr} = getelementptr [{msgLen} x i8], [{msgLen} x i8]* {msgConst}, i32 0, i32 0");

        // Use stack trace infrastructure to throw
        _stackTraceCodeGen?.EmitThrow(errorTypePtr: typePtr, messagePtr: messagePtr);

        return "";
    }

    /// <summary>
    /// Generates a dynamic throw for error types that have fields affecting the message.
    /// Uses runtime sprintf to format the message with field values.
    /// Returns true if handled, false if should use static message.
    /// </summary>
    private bool TryGenerateDynamicThrow(string errorTypeName, CallExpression callExpr)
    {
        // Handle IndexOutOfBoundsError(index: X, count: Y)
        if (errorTypeName == "IndexOutOfBoundsError")
        {
            Expression? indexArg = GetNamedArgument(arguments: callExpr.Arguments, name: "index");
            Expression? countArg = GetNamedArgument(arguments: callExpr.Arguments, name: "count");

            if (indexArg != null && countArg != null)
            {
                string indexVal = indexArg.Accept(visitor: this);
                string countVal = countArg.Accept(visitor: this);

                // Call runtime function: __rf_throw_index_out_of_bounds(index, count)
                _output.AppendLine(value: $"  call void @__rf_throw_index_out_of_bounds(i32 {indexVal}, i32 {countVal})");
                _output.AppendLine(value: "  unreachable");
                return true;
            }
        }

        // Handle IntegerOverflowError(message: "...")
        if (errorTypeName == "IntegerOverflowError")
        {
            Expression? msgArg = GetNamedArgument(arguments: callExpr.Arguments, name: "message");
            if (msgArg is LiteralExpression literal && literal.Value is string msg)
            {
                // Create string constant and call runtime
                string msgConst = $"@.str_overflow_msg{_tempCounter++}";
                int msgLen = msg.Length + 1;
                string escapedMsg = msg.Replace(oldValue: "\\", newValue: "\\5C")
                                       .Replace(oldValue: "\"", newValue: "\\22");
                _stringConstants ??= new List<string>();
                _stringConstants.Add(item: $"{msgConst} = private unnamed_addr constant [{msgLen} x i8] c\"{escapedMsg}\\00\", align 1");

                string msgPtr = GetNextTemp();
                _output.AppendLine(value: $"  {msgPtr} = getelementptr [{msgLen} x i8], [{msgLen} x i8]* {msgConst}, i32 0, i32 0");
                _output.AppendLine(value: $"  call void @__rf_throw_integer_overflow(i8* {msgPtr})");
                _output.AppendLine(value: "  unreachable");
                return true;
            }
        }

        // Handle EmptyCollectionError(operation: "...")
        if (errorTypeName == "EmptyCollectionError")
        {
            Expression? opArg = GetNamedArgument(arguments: callExpr.Arguments, name: "operation");
            if (opArg is LiteralExpression literal && literal.Value is string op)
            {
                // Create string constant and call runtime
                string opConst = $"@.str_empty_op{_tempCounter++}";
                int opLen = op.Length + 1;
                string escapedOp = op.Replace(oldValue: "\\", newValue: "\\5C")
                                     .Replace(oldValue: "\"", newValue: "\\22");
                _stringConstants ??= new List<string>();
                _stringConstants.Add(item: $"{opConst} = private unnamed_addr constant [{opLen} x i8] c\"{escapedOp}\\00\", align 1");

                string opPtr = GetNextTemp();
                _output.AppendLine(value: $"  {opPtr} = getelementptr [{opLen} x i8], [{opLen} x i8]* {opConst}, i32 0, i32 0");
                _output.AppendLine(value: $"  call void @__rf_throw_empty_collection(i8* {opPtr})");
                _output.AppendLine(value: "  unreachable");
                return true;
            }
        }

        // Handle ElementNotFoundError (no fields)
        if (errorTypeName == "ElementNotFoundError")
        {
            _output.AppendLine(value: "  call void @__rf_throw_element_not_found()");
            _output.AppendLine(value: "  unreachable");
            return true;
        }

        return false;
    }
    /// <summary>
    /// Gets a default error message for a Crashable error type.
    /// First tries to read from stdlib crash_message() implementations,
    /// then falls back to minimal placeholders for types not yet parsed.
    /// </summary>
    private string GetDefaultErrorMessage(string errorTypeName)
    {
        // First, try to get the message from stdlib source files
        if (_crashMessageResolver != null)
        {
            string? stdlibMessage = _crashMessageResolver.GetStaticMessage(errorTypeName: errorTypeName);
            if (stdlibMessage != null)
            {
                return stdlibMessage;
            }
        }

        // Fallback for when stdlib is not available or message is dynamic
        // These should ideally never be used - dynamic messages should use runtime calls
        return $"{errorTypeName} occurred";
    }

    /// <summary>
    /// Checks if a type name is a known Crashable error type.
    /// </summary>
    private bool IsCrashableErrorType(string typeName)
    {
        return typeName switch
        {
            "DivisionByZeroError" or "IntegerOverflowError" or "IndexOutOfBoundsError" or "EmptyCollectionError" or "ElementNotFoundError" or "VerificationFailedError" or "LogicBreachedError" or "UserTerminationError" or "AbsentValueError" => true,
            // Also check for any type ending in "Error" as a convention
            _ => typeName.EndsWith(value: "Error")
        };
    }

    /// <summary>
    /// Handles a Crashable error type constructor call.
    /// Returns a string pointer to the error message for use in safe variants.
    /// </summary>
    private string HandleCrashableErrorConstructor(string errorTypeName, string resultTemp)
    {
        string errorMessage = GetDefaultErrorMessage(errorTypeName: errorTypeName);

        if (_stringConstants == null)
        {
            _stringConstants = new List<string>();
        }

        string msgConst = $"@.str_errmsg{_tempCounter++}";
        int msgLen = errorMessage.Length + 1;
        string escapedMsg = errorMessage.Replace(oldValue: "\\", newValue: "\\5C")
                                        .Replace(oldValue: "\"", newValue: "\\22");
        _stringConstants.Add(item: $"{msgConst} = private unnamed_addr constant [{msgLen} x i8] c\"{escapedMsg}\\00\", align 1");

        _output.AppendLine(handler: $"  {resultTemp} = getelementptr [{msgLen} x i8], [{msgLen} x i8]* {msgConst}, i32 0, i32 0");
        _tempTypes[key: resultTemp] = new TypeInfo(LLVMType: "i8*", IsUnsigned: false, IsFloatingPoint: false, RazorForgeType: "text");

        return resultTemp;
    }

    /// <summary>
    /// Handles the generation of LLVM IR code for an absent statement, which signals
    /// a critical runtime error caused by accessing an undefined or missing value.
    /// This triggers a program crash with a generated error stack trace.
    /// </summary>
    /// <param name="node">The absent statement node that indicates a missing value condition
    /// and serves as the basis for the error handling code generation.</param>
    /// <returns>An empty string, as the operation causes a simulated program termination and
    /// does not produce additional LLVM IR output beyond the stack trace emission.</returns>
    public string VisitAbsentStatement(AbsentStatement node)
    {
        // absent statement crashes the program with AbsentValueError
        // This is the expected behavior - absent indicates "not found" which is a crash condition
        _hasReturn = true;
        _blockTerminated = true;

        _output.AppendLine(handler: $"  ; absent - capturing stack and crashing with AbsentValueError");

        // Use stack trace infrastructure to throw AbsentValueError
        _stackTraceCodeGen?.EmitAbsent();

        return "";
    }

    public string VisitWhenStatement(WhenStatement node)
    {
        // Check if this is a standalone when (no subject expression, just guards)
        // or a when with a subject expression to match against
        bool isStandaloneWhen = node.Expression is LiteralExpression lit && lit.Value is int i && i == 0;

        string endLabel = GetNextLabel();

        if (isStandaloneWhen)
        {
            // Standalone when: when { condition1 => body1, condition2 => body2, _ => default }
            // Each clause has an ExpressionPattern with a boolean condition
            for (int idx = 0; idx < node.Clauses.Count; idx++)
            {
                WhenClause clause = node.Clauses[index: idx];
                string nextLabel = idx < node.Clauses.Count - 1
                    ? GetNextLabel()
                    : endLabel;

                if (clause.Pattern is WildcardPattern)
                {
                    // Wildcard pattern - always matches (default case)
                    clause.Body.Accept(visitor: this);
                    _output.AppendLine(value: $"  br label %{endLabel}");
                }
                else if (clause.Pattern is ExpressionPattern exprPat)
                {
                    // Expression pattern - evaluate the boolean condition
                    string condResult = exprPat.Expression.Accept(visitor: this);
                    string thenLabel = GetNextLabel();

                    // Convert to i1 if needed (non-zero = true)
                    string condBool = GetNextTemp();
                    _output.AppendLine(value: $"  {condBool} = icmp ne i32 {condResult}, 0");
                    _output.AppendLine(value: $"  br i1 {condBool}, label %{thenLabel}, label %{nextLabel}");

                    _output.AppendLine(value: $"{thenLabel}:");
                    clause.Body.Accept(visitor: this);
                    _output.AppendLine(value: $"  br label %{endLabel}");

                    if (idx < node.Clauses.Count - 1)
                    {
                        _output.AppendLine(value: $"{nextLabel}:");
                    }
                }
                else
                {
                    // Unknown pattern type in standalone when - skip
                    if (idx < node.Clauses.Count - 1)
                    {
                        _output.AppendLine(value: $"{nextLabel}:");
                    }
                }
            }
        }
        else
        {
            // When with subject: when expr { value1 => body1, value2 => body2, _ => default }
            string subjectValue = node.Expression.Accept(visitor: this);
            TypeInfo subjectType = GetTypeInfo(expr: node.Expression);

            for (int idx = 0; idx < node.Clauses.Count; idx++)
            {
                WhenClause clause = node.Clauses[index: idx];
                string nextLabel = idx < node.Clauses.Count - 1
                    ? GetNextLabel()
                    : endLabel;

                if (clause.Pattern is WildcardPattern)
                {
                    // Wildcard pattern - always matches (default case)
                    clause.Body.Accept(visitor: this);
                    _output.AppendLine(value: $"  br label %{endLabel}");
                }
                else if (clause.Pattern is LiteralPattern litPat)
                {
                    // Literal pattern - compare subject to literal value
                    string litValue = litPat.Value.ToString() ?? "0";
                    string cmpResult = GetNextTemp();
                    string thenLabel = GetNextLabel();

                    _output.AppendLine(value: $"  {cmpResult} = icmp eq {subjectType.LLVMType} {subjectValue}, {litValue}");
                    _output.AppendLine(value: $"  br i1 {cmpResult}, label %{thenLabel}, label %{nextLabel}");

                    _output.AppendLine(value: $"{thenLabel}:");
                    clause.Body.Accept(visitor: this);
                    _output.AppendLine(value: $"  br label %{endLabel}");

                    if (idx < node.Clauses.Count - 1)
                    {
                        _output.AppendLine(value: $"{nextLabel}:");
                    }
                }
                else if (clause.Pattern is IdentifierPattern idPat)
                {
                    // Identifier pattern - bind the subject to a variable and execute body
                    // This is like a default case that captures the value
                    string varPtr = $"%{idPat.Name}";
                    _output.AppendLine(value: $"  {varPtr} = alloca {subjectType.LLVMType}");
                    _output.AppendLine(value: $"  store {subjectType.LLVMType} {subjectValue}, {subjectType.LLVMType}* {varPtr}");
                    _symbolTypes[key: idPat.Name] = subjectType.LLVMType;

                    clause.Body.Accept(visitor: this);
                    _output.AppendLine(value: $"  br label %{endLabel}");

                    if (idx < node.Clauses.Count - 1)
                    {
                        _output.AppendLine(value: $"{nextLabel}:");
                    }
                }
                else
                {
                    // Unknown pattern type - skip to next
                    if (idx < node.Clauses.Count - 1)
                    {
                        _output.AppendLine(value: $"  br label %{nextLabel}");
                        _output.AppendLine(value: $"{nextLabel}:");
                    }
                }
            }
        }

        _output.AppendLine(value: $"{endLabel}:");
        return "";
    }
    /// <summary>
    /// Extracts error message from constructor arguments.
    /// Looks for a 'message' named argument or the first string literal.
    /// </summary>
    private string? ExtractErrorMessageFromArgs(List<Expression> arguments)
    {
        foreach (Expression arg in arguments)
        {
            // Check for named argument like message: "..."
            if (arg is NamedArgumentExpression namedArg)
            {
                if (namedArg.Name == "message" && namedArg.Value is LiteralExpression msgLiteral)
                {
                    if (msgLiteral.Value is string msgStr)
                    {
                        return msgStr;
                    }
                }
            }
            // Check for positional string literal (first one)
            else if (arg is LiteralExpression literal && literal.Value is string str)
            {
                return str;
            }
        }

        return null;
    }
}
