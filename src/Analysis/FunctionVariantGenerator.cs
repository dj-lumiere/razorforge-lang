using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Generates safe function variants (try_, check_, find_) based on throw/absent usage.
///
/// Generation rules:
/// - No throw, no absent → Compile Error (! functions must have throw or absent)
/// - No throw, has absent → generates try_ only
/// - Has throw, no absent → generates try_, check_
/// - Has throw, has absent → generates try_, find_
///
/// Variant behaviors:
/// - try_foo() → wraps crashes in Maybe&lt;T&gt; (None on crash or absent)
/// - check_foo() → wraps throw in Result&lt;T&gt; (Error variant on throw)
/// - find_foo() → wraps throw/absent in Lookup&lt;T&gt; (Error/Absent/Value variants)
/// </summary>
public class FunctionVariantGenerator : IAstVisitor<object?>
{
    private readonly List<FunctionDeclaration> _generatedVariants = new();
    private bool _hasThrow;
    private bool _hasAbsent;
    private bool _hasUnrecoverableIntrinsic;

    /// <summary>
    /// Unrecoverable intrinsics that implicitly have @crash_only behavior.
    /// These functions cannot be safely wrapped in try_/check_/find_ variants.
    /// </summary>
    private static readonly HashSet<string> UnrecoverableIntrinsics = new()
    {
        "stop!", // User-initiated termination
        "breach!", // Logic breach (unreachable code reached)
        "verify!" // Verification/assertion failure
    };

    /// <summary>
    /// Gets the list of generated function variants.
    /// </summary>
    public List<FunctionDeclaration> GeneratedVariants => _generatedVariants;

    /// <summary>
    /// Analyzes a program and generates all necessary function variants.
    /// </summary>
    public void GenerateVariants(Compilers.Shared.AST.Program program)
    {
        program.Accept(visitor: this);
    }

    public object? VisitProgram(Compilers.Shared.AST.Program node)
    {
        foreach (IAstNode declaration in node.Declarations)
        {
            declaration.Accept(visitor: this);
        }

        return null;
    }

    public object? VisitFunctionDeclaration(FunctionDeclaration node)
    {
        // Skip if function name already has a variant prefix
        if (node.Name.StartsWith(value: "try_") || node.Name.StartsWith(value: "check_") ||
            node.Name.StartsWith(value: "find_"))
        {
            return null;
        }

        // Skip if function has @crash_only attribute - no safe variants generated
        if (node.Attributes.Contains(item: "crash_only"))
        {
            return null;
        }

        // Analyze the function body to detect throw/absent usage and unrecoverable intrinsics
        _hasThrow = false;
        _hasAbsent = false;
        _hasUnrecoverableIntrinsic = false;

        if (node.Body != null)
        {
            AnalyzeFunctionBody(statement: node.Body);
        }

        // Skip if function contains unrecoverable intrinsics (stop!, breach!, verify!)
        // These are implicitly @crash_only - they cannot be safely wrapped
        if (_hasUnrecoverableIntrinsic)
        {
            return null;
        }

        // Generate variants based on what was found
        // Rules:
        // - no throw, no absent → CE (should be caught by semantic analyzer)
        // - no throw, has absent → try_ only
        // - has throw, no absent → try_, check_
        // - has throw, has absent → try_, find_

        switch (_hasThrow)
        {
            case true when _hasAbsent:
                // Has both throw and absent → generate try_ and find_
                _generatedVariants.Add(item: GenerateTryVariant(original: node));
                _generatedVariants.Add(item: GenerateFindVariant(original: node));
                break;
            case true:
                // Has throw but no absent → generate try_ and check_
                _generatedVariants.Add(item: GenerateTryVariant(original: node));
                _generatedVariants.Add(item: GenerateCheckVariant(original: node));
                break;
            default:
            {
                if (_hasAbsent)
                {
                    // Has absent but no throw → generate try_ only
                    _generatedVariants.Add(item: GenerateTryVariant(original: node));
                }

                break;
            }
        }
        // else: no throw, no absent → compile error (handled by semantic analyzer)

        return null;
    }

    /// <summary>
    /// Analyzes a function body to detect throw/absent statements and unrecoverable intrinsics.
    /// </summary>
    private void AnalyzeFunctionBody(Statement statement)
    {
        while (true)
        {
            switch (statement)
            {
                case ThrowStatement:
                    _hasThrow = true;
                    break;
                case AbsentStatement:
                    _hasAbsent = true;
                    break;
                case BlockStatement block:
                {
                    foreach (Statement stmt in block.Statements)
                    {
                        AnalyzeFunctionBody(statement: stmt);
                    }

                    break;
                }
                case IfStatement ifStmt:
                {
                    AnalyzeExpression(expression: ifStmt.Condition);
                    AnalyzeFunctionBody(statement: ifStmt.ThenStatement);
                    if (ifStmt.ElseStatement != null)
                    {
                        statement = ifStmt.ElseStatement;
                        continue;
                    }

                    break;
                }
                case WhileStatement whileStmt:
                    AnalyzeExpression(expression: whileStmt.Condition);
                    statement = whileStmt.Body;
                    continue;
                case ForStatement forStmt:
                    AnalyzeExpression(expression: forStmt.Iterable);
                    statement = forStmt.Body;
                    continue;
                case WhenStatement whenStmt:
                {
                    AnalyzeExpression(expression: whenStmt.Expression);
                    foreach (WhenClause clause in whenStmt.Clauses)
                    {
                        AnalyzeFunctionBody(statement: clause.Body);
                    }

                    break;
                }
                case ExpressionStatement exprStmt:
                    AnalyzeExpression(expression: exprStmt.Expression);
                    break;
                case DeclarationStatement declStmt when declStmt.Declaration is VariableDeclaration varDecl:
                {
                    if (varDecl.Initializer != null)
                    {
                        AnalyzeExpression(expression: varDecl.Initializer);
                    }

                    break;
                }
                case ReturnStatement returnStmt:
                {
                    if (returnStmt.Value != null)
                    {
                        AnalyzeExpression(expression: returnStmt.Value);
                    }

                    break;
                }
            }

            break;
        }
    }

    /// <summary>
    /// Analyzes an expression to detect calls to unrecoverable intrinsics.
    /// </summary>
    private void AnalyzeExpression(Expression expression)
    {
        switch (expression)
        {
            case CallExpression call:
            {
                // Check if this is a call to an unrecoverable intrinsic
                string? functionName = GetFunctionName(callee: call.Callee);
                if (functionName != null && UnrecoverableIntrinsics.Contains(item: functionName))
                {
                    _hasUnrecoverableIntrinsic = true;
                }

                // Analyze arguments
                foreach (Expression arg in call.Arguments)
                {
                    AnalyzeExpression(expression: arg);
                }

                break;
            }
            case BinaryExpression binary:
                AnalyzeExpression(expression: binary.Left);
                AnalyzeExpression(expression: binary.Right);
                break;
            case UnaryExpression unary:
                AnalyzeExpression(expression: unary.Operand);
                break;
            case ConditionalExpression cond:
                AnalyzeExpression(expression: cond.Condition);
                AnalyzeExpression(expression: cond.TrueExpression);
                AnalyzeExpression(expression: cond.FalseExpression);
                break;
            case MemberExpression member:
                AnalyzeExpression(expression: member.Object);
                break;
            case IndexExpression index:
                AnalyzeExpression(expression: index.Object);
                AnalyzeExpression(expression: index.Index);
                break;
        }
    }

    /// <summary>
    /// Extracts the function name from a callee expression.
    /// </summary>
    private static string? GetFunctionName(Expression callee)
    {
        return callee switch
        {
            IdentifierExpression id => id.Name,
            MemberExpression member => member.PropertyName,
            _ => null
        };
    }

    /// <summary>
    /// Generates the try_ variant that wraps crashes in Maybe&lt;T&gt;.
    /// throw Error => return None
    /// return value => return Some(value)
    /// </summary>
    private FunctionDeclaration GenerateTryVariant(FunctionDeclaration original)
    {
        // Strip the ! suffix from the original name since try_ variants are not throwable
        string baseName = original.Name.TrimEnd(trimChar: '!');

        // Handle type-qualified methods (e.g., "s32.__add__" → "s32.try_add")
        string newName;
        if (baseName.Contains('.'))
        {
            // Split into type and method parts
            int dotIndex = baseName.LastIndexOf('.');
            string typePart = baseName.Substring(0, dotIndex);
            string methodPart = baseName.Substring(dotIndex + 1);

            // Strip dunder wrapper from method part only
            // __add__ → add
            if (methodPart.StartsWith("__") && methodPart.EndsWith("__"))
            {
                methodPart = methodPart[2..^2];
            }

            newName = $"{typePart}.try_{methodPart}";
        }
        else
        {
            // Strip dunder wrapper (__name__) for cleaner variant names
            // __add__! → try_add (not try___add__)
            if (baseName.StartsWith("__") && baseName.EndsWith("__"))
            {
                baseName = baseName[2..^2];
            }

            newName = $"try_{baseName}";
        }

        // Wrap return type in Maybe<T>
        TypeExpression? newReturnType = original.ReturnType != null
            ? new TypeExpression(Name: "Maybe",
                GenericArguments: [original.ReturnType],
                Location: original.ReturnType.Location)
            : null;

        // Transform the original body:
        // - throw Error => return None
        // - return value => return Some(value)
        Statement? transformedBody = original.Body != null
            ? TransformBodyForTryVariant(statement: original.Body, location: original.Location)
            : null;

        return new FunctionDeclaration(Name: newName,
            Parameters: original.Parameters,
            ReturnType: newReturnType,
            Body: transformedBody,
            Visibility: original.Visibility,
            Attributes: original.Attributes,
            Location: original.Location);
    }

    /// <summary>
    /// Transforms a function body for try_ variant:
    /// - throw Error => return None (compiler handles Maybe type)
    /// - absent => return None (compiler handles Maybe type)
    /// - return value => return value (compiler handles implicit Some)
    /// </summary>
    private Statement TransformBodyForTryVariant(Statement statement, SourceLocation location)
    {
        return statement switch
        {
            // throw Error => return None
            ThrowStatement => new ReturnStatement(
                Value: new IdentifierExpression(Name: "None", Location: location),
                Location: location),

            // absent => return None
            AbsentStatement => new ReturnStatement(
                Value: new IdentifierExpression(Name: "None", Location: location),
                Location: location),

            // return value stays the same - compiler handles implicit Some wrapping
            ReturnStatement => statement,

            BlockStatement block => new BlockStatement(Statements: block.Statements
                   .Select(selector: s =>
                        TransformBodyForTryVariant(statement: s, location: location))
                   .ToList(),
                Location: block.Location),

            IfStatement ifStmt => new IfStatement(Condition: ifStmt.Condition,
                ThenStatement: TransformBodyForTryVariant(statement: ifStmt.ThenStatement,
                    location: location),
                ElseStatement: ifStmt.ElseStatement != null
                    ? TransformBodyForTryVariant(statement: ifStmt.ElseStatement,
                        location: location)
                    : null,
                Location: ifStmt.Location),

            WhileStatement whileStmt => new WhileStatement(Condition: whileStmt.Condition,
                Body: TransformBodyForTryVariant(statement: whileStmt.Body, location: location),
                Location: whileStmt.Location),

            ForStatement forStmt => new ForStatement(Variable: forStmt.Variable,
                Iterable: forStmt.Iterable,
                Body: TransformBodyForTryVariant(statement: forStmt.Body, location: location),
                Location: forStmt.Location),

            WhenStatement whenStmt => new WhenStatement(Expression: whenStmt.Expression,
                Clauses: whenStmt.Clauses
                                 .Select(selector: c => new WhenClause(Pattern: c.Pattern,
                                      Body: TransformBodyForTryVariant(statement: c.Body,
                                          location: location),
                                      Location: c.Location))
                                 .ToList(),
                Location: whenStmt.Location),

            _ => statement // Pass through other statements unchanged
        };
    }

    /// <summary>
    /// Generates the check_ variant that wraps throw in Result&lt;T&gt;.
    /// throw Error => return error (compiler handles Result type)
    /// return value => return value (compiler handles implicit Ok)
    /// </summary>
    private FunctionDeclaration GenerateCheckVariant(FunctionDeclaration original)
    {
        // Strip the ! suffix from the original name since check_ variants are not throwable
        string baseName = original.Name.TrimEnd(trimChar: '!');

        // Handle type-qualified methods (e.g., "s32.__add__" → "s32.check_add")
        string newName;
        if (baseName.Contains('.'))
        {
            // Split into type and method parts
            int dotIndex = baseName.LastIndexOf('.');
            string typePart = baseName.Substring(0, dotIndex);
            string methodPart = baseName.Substring(dotIndex + 1);

            // Strip dunder wrapper from method part only
            // __add__ → add
            if (methodPart.StartsWith("__") && methodPart.EndsWith("__"))
            {
                methodPart = methodPart[2..^2];
            }

            newName = $"{typePart}.check_{methodPart}";
        }
        else
        {
            // Strip dunder wrapper (__name__) for cleaner variant names
            // __add__! → check_add (not check___add__)
            if (baseName.StartsWith("__") && baseName.EndsWith("__"))
            {
                baseName = baseName[2..^2];
            }

            newName = $"check_{baseName}";
        }

        // Wrap return type in Result<T>
        TypeExpression? newReturnType = original.ReturnType != null
            ? new TypeExpression(Name: "Result",
                GenericArguments: [original.ReturnType],
                Location: original.ReturnType.Location)
            : null;

        // Transform the original body:
        // - throw Error => return error
        // - return value => return value
        Statement? transformedBody = original.Body != null
            ? TransformBodyForCheckVariant(statement: original.Body, location: original.Location)
            : null;

        return new FunctionDeclaration(Name: newName,
            Parameters: original.Parameters,
            ReturnType: newReturnType,
            Body: transformedBody,
            Visibility: original.Visibility,
            Attributes: original.Attributes,
            Location: original.Location);
    }

    /// <summary>
    /// Transforms a function body for check_ variant:
    /// - throw Error => return error (compiler handles Result type)
    /// - return value => return value (compiler handles implicit Ok)
    /// </summary>
    private Statement TransformBodyForCheckVariant(Statement statement, SourceLocation location)
    {
        return statement switch
        {
            // throw Error => return error (direct return of Error value)
            ThrowStatement throwStmt => new ReturnStatement(Value: throwStmt.Error,
                Location: location),

            // return value stays the same - compiler handles implicit Ok wrapping
            ReturnStatement => statement,

            BlockStatement block => new BlockStatement(Statements: block.Statements
                   .Select(selector: s =>
                        TransformBodyForCheckVariant(statement: s, location: location))
                   .ToList(),
                Location: block.Location),

            IfStatement ifStmt => new IfStatement(Condition: ifStmt.Condition,
                ThenStatement: TransformBodyForCheckVariant(statement: ifStmt.ThenStatement,
                    location: location),
                ElseStatement: ifStmt.ElseStatement != null
                    ? TransformBodyForCheckVariant(statement: ifStmt.ElseStatement,
                        location: location)
                    : null,
                Location: ifStmt.Location),

            WhileStatement whileStmt => new WhileStatement(Condition: whileStmt.Condition,
                Body: TransformBodyForCheckVariant(statement: whileStmt.Body, location: location),
                Location: whileStmt.Location),

            ForStatement forStmt => new ForStatement(Variable: forStmt.Variable,
                Iterable: forStmt.Iterable,
                Body: TransformBodyForCheckVariant(statement: forStmt.Body, location: location),
                Location: forStmt.Location),

            WhenStatement whenStmt => new WhenStatement(Expression: whenStmt.Expression,
                Clauses: whenStmt.Clauses
                                 .Select(selector: c => new WhenClause(Pattern: c.Pattern,
                                      Body: TransformBodyForCheckVariant(statement: c.Body,
                                          location: location),
                                      Location: c.Location))
                                 .ToList(),
                Location: whenStmt.Location),

            _ => statement // Pass through other statements unchanged
        };
    }

    /// <summary>
    /// Generates the find_ variant that wraps throw/absent in Lookup&lt;T&gt;.
    /// throw Error => return error (compiler handles Lookup type)
    /// absent => return None
    /// return value => return value (compiler handles implicit Value)
    /// </summary>
    private FunctionDeclaration GenerateFindVariant(FunctionDeclaration original)
    {
        // Strip the ! suffix from the original name since find_ variants are not throwable
        string baseName = original.Name.TrimEnd(trimChar: '!');

        // Handle type-qualified methods (e.g., "s32.__add__" → "s32.find_add")
        string newName;
        if (baseName.Contains('.'))
        {
            // Split into type and method parts
            int dotIndex = baseName.LastIndexOf('.');
            string typePart = baseName.Substring(0, dotIndex);
            string methodPart = baseName.Substring(dotIndex + 1);

            // Strip dunder wrapper from method part only
            // __add__ → add
            if (methodPart.StartsWith("__") && methodPart.EndsWith("__"))
            {
                methodPart = methodPart[2..^2];
            }

            newName = $"{typePart}.find_{methodPart}";
        }
        else
        {
            // Strip dunder wrapper (__name__) for cleaner variant names
            // __add__! → find_add (not find___add__)
            if (baseName.StartsWith("__") && baseName.EndsWith("__"))
            {
                baseName = baseName[2..^2];
            }

            newName = $"find_{baseName}";
        }

        // Wrap return type in Lookup<T>
        TypeExpression? newReturnType = original.ReturnType != null
            ? new TypeExpression(Name: "Lookup",
                GenericArguments: [original.ReturnType],
                Location: original.ReturnType.Location)
            : null;

        // Transform the original body:
        // - throw Error => return error
        // - absent => return None
        // - return value => return value
        Statement? transformedBody = original.Body != null
            ? TransformBodyForFindVariant(statement: original.Body, location: original.Location)
            : null;

        return new FunctionDeclaration(Name: newName,
            Parameters: original.Parameters,
            ReturnType: newReturnType,
            Body: transformedBody,
            Visibility: original.Visibility,
            Attributes: original.Attributes,
            Location: original.Location);
    }

    /// <summary>
    /// Transforms a function body for find_ variant:
    /// - throw Error => return error (compiler handles Lookup type)
    /// - absent => return None
    /// - return value => return value (compiler handles implicit Value)
    /// </summary>
    private Statement TransformBodyForFindVariant(Statement statement, SourceLocation location)
    {
        return statement switch
        {
            // throw Error => return error (direct return of Error value)
            ThrowStatement throwStmt => new ReturnStatement(Value: throwStmt.Error,
                Location: location),

            // absent => return None
            AbsentStatement => new ReturnStatement(
                Value: new IdentifierExpression(Name: "None", Location: location),
                Location: location),

            // return value stays the same - compiler handles implicit Value wrapping
            ReturnStatement => statement,

            BlockStatement block => new BlockStatement(Statements: block.Statements
                   .Select(
                        selector: s =>
                            TransformBodyForFindVariant(statement: s, location: location))
                   .ToList(),
                Location: block.Location),

            IfStatement ifStmt => new IfStatement(Condition: ifStmt.Condition,
                ThenStatement: TransformBodyForFindVariant(statement: ifStmt.ThenStatement,
                    location: location),
                ElseStatement: ifStmt.ElseStatement != null
                    ? TransformBodyForFindVariant(statement: ifStmt.ElseStatement,
                        location: location)
                    : null,
                Location: ifStmt.Location),

            WhileStatement whileStmt => new WhileStatement(Condition: whileStmt.Condition,
                Body: TransformBodyForFindVariant(statement: whileStmt.Body, location: location),
                Location: whileStmt.Location),

            ForStatement forStmt => new ForStatement(Variable: forStmt.Variable,
                Iterable: forStmt.Iterable,
                Body: TransformBodyForFindVariant(statement: forStmt.Body, location: location),
                Location: forStmt.Location),

            WhenStatement whenStmt => new WhenStatement(Expression: whenStmt.Expression,
                Clauses: whenStmt.Clauses
                                 .Select(selector: c => new WhenClause(Pattern: c.Pattern,
                                      Body: TransformBodyForFindVariant(statement: c.Body,
                                          location: location),
                                      Location: c.Location))
                                 .ToList(),
                Location: whenStmt.Location),

            _ => statement // Pass through other statements unchanged
        };
    }

    #region Unimplemented Visitor Methods

    public object? VisitVariableDeclaration(VariableDeclaration node)
    {
        return null;
    }
    public object? VisitEntityDeclaration(EntityDeclaration node)
    {
        return null;
    }
    public object? VisitRecordDeclaration(RecordDeclaration node)
    {
        return null;
    }
    public object? VisitChoiceDeclaration(ChoiceDeclaration node)
    {
        return null;
    }
    public object? VisitVariantDeclaration(VariantDeclaration node)
    {
        return null;
    }
    public object? VisitProtocolDeclaration(ProtocolDeclaration node)
    {
        return null;
    }
    public object? VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        return null;
    }
    public object? VisitImportDeclaration(ImportDeclaration node)
    {
        return null;
    }
    public object? VisitNamespaceDeclaration(NamespaceDeclaration node)
    {
        return null;
    }
    public object? VisitDefineDeclaration(RedefinitionDeclaration node)
    {
        return null;
    }
    public object? VisitUsingDeclaration(UsingDeclaration node)
    {
        return null;
    }
    public object? VisitExternalDeclaration(ExternalDeclaration node)
    {
        return null;
    }
    public object? VisitExpressionStatement(ExpressionStatement node)
    {
        return null;
    }
    public object? VisitDeclarationStatement(DeclarationStatement node)
    {
        return null;
    }
    public object? VisitAssignmentStatement(AssignmentStatement node)
    {
        return null;
    }
    public object? VisitTupleDestructuringStatement(TupleDestructuringStatement node)
    {
        return null;
    }
    public object? VisitReturnStatement(ReturnStatement node)
    {
        return null;
    }
    public object? VisitIfStatement(IfStatement node)
    {
        return null;
    }
    public object? VisitWhileStatement(WhileStatement node)
    {
        return null;
    }
    public object? VisitForStatement(ForStatement node)
    {
        return null;
    }
    public object? VisitWhenStatement(WhenStatement node)
    {
        return null;
    }
    public object? VisitBlockStatement(BlockStatement node)
    {
        return null;
    }
    public object? VisitBreakStatement(BreakStatement node)
    {
        return null;
    }
    public object? VisitContinueStatement(ContinueStatement node)
    {
        return null;
    }
    public object? VisitThrowStatement(ThrowStatement node)
    {
        return null;
    }
    public object? VisitAbsentStatement(AbsentStatement node)
    {
        return null;
    }
    public object? VisitPassStatement(PassStatement node)
    {
        return null;
    }
    public object? VisitPresetDeclaration(PresetDeclaration node)
    {
        return null;
    }
    public object? VisitLiteralExpression(LiteralExpression node)
    {
        return null;
    }

    public object? VisitListLiteralExpression(ListLiteralExpression node)
    {
        return null;
    }

    public object? VisitSetLiteralExpression(SetLiteralExpression node)
    {
        return null;
    }

    public object? VisitDictLiteralExpression(DictLiteralExpression node)
    {
        return null;
    }

    public object? VisitIdentifierExpression(IdentifierExpression node)
    {
        return null;
    }
    public object? VisitBinaryExpression(BinaryExpression node)
    {
        return null;
    }
    public object? VisitUnaryExpression(UnaryExpression node)
    {
        return null;
    }
    public object? VisitCallExpression(CallExpression node)
    {
        return null;
    }
    public object? VisitMemberExpression(MemberExpression node)
    {
        return null;
    }
    public object? VisitIndexExpression(IndexExpression node)
    {
        return null;
    }
    public object? VisitConditionalExpression(ConditionalExpression node)
    {
        return null;
    }

    public object? VisitBlockExpression(BlockExpression node)
    {
        return null;
    }

    public object? VisitRangeExpression(RangeExpression node)
    {
        return null;
    }
    public object? VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        return null;
    }
    public object? VisitLambdaExpression(LambdaExpression node)
    {
        return null;
    }
    public object? VisitTypeExpression(TypeExpression node)
    {
        return null;
    }
    public object? VisitTypeConversionExpression(TypeConversionExpression node)
    {
        return null;
    }
    public object? VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        return null;
    }
    public object? VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        return null;
    }
    public object? VisitGenericMemberExpression(GenericMemberExpression node)
    {
        return null;
    }
    public object? VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        return null;
    }
    public object? VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        return null;
    }
    public object? VisitNativeCallExpression(NativeCallExpression node)
    {
        return null;
    }
    public object? VisitDangerStatement(DangerStatement node)
    {
        return null;
    }
    public object? VisitViewingStatement(ViewingStatement node)
    {
        return null;
    }
    public object? VisitHijackingStatement(HijackingStatement node)
    {
        return null;
    }
    public object? VisitInspectingStatement(InspectingStatement node)
    {
        return null;
    }
    public object? VisitSeizingStatement(SeizingStatement node)
    {
        return null;
    }
    public object? VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        return null;
    }

    public object? VisitConstructorExpression(ConstructorExpression node)
    {
        return null;
    }

    #endregion
}
