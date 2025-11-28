using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Generates safe function variants (try_, check_, find_) based on fail/absent usage.
///
/// Generation rules:
/// - No fail, no absent → Compile Error (! functions must have fail or absent)
/// - No fail, has absent → generates try_ only
/// - Has fail, no absent → generates try_, check_
/// - Has fail, has absent → generates try_, find_
///
/// Variant behaviors:
/// - try_foo() → wraps crashes in Maybe&lt;T&gt; (None on crash or absent)
/// - check_foo() → wraps fail in Result&lt;T&gt; (Error variant on fail)
/// - find_foo() → wraps fail/absent in Lookup&lt;T&gt; (Error/Absent/Value variants)
/// </summary>
public class FunctionVariantGenerator : IAstVisitor<object?>
{
    private readonly List<FunctionDeclaration> _generatedVariants = new();
    private bool _hasFail;
    private bool _hasAbsent;

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
        if (node.Name.StartsWith("try_") || node.Name.StartsWith("check_") ||
            node.Name.StartsWith("find_"))
        {
            return null;
        }

        // Analyze the function body to detect fail/absent usage
        _hasFail = false;
        _hasAbsent = false;

        if (node.Body != null)
        {
            AnalyzeFunctionBody(node.Body);
        }

        // Generate variants based on what was found
        // Rules:
        // - no fail, no absent → CE (should be caught by semantic analyzer)
        // - no fail, has absent → try_ only
        // - has fail, no absent → try_, check_
        // - has fail, has absent → try_, find_

        if (_hasFail && _hasAbsent)
        {
            // Has both fail and absent → generate try_ and find_
            _generatedVariants.Add(GenerateTryVariant(node));
            _generatedVariants.Add(GenerateFindVariant(node));
        }
        else if (_hasFail)
        {
            // Has fail but no absent → generate try_ and check_
            _generatedVariants.Add(GenerateTryVariant(node));
            _generatedVariants.Add(GenerateCheckVariant(node));
        }
        else if (_hasAbsent)
        {
            // Has absent but no fail → generate try_ only
            _generatedVariants.Add(GenerateTryVariant(node));
        }
        // else: no fail, no absent → compile error (handled by semantic analyzer)

        return null;
    }

    /// <summary>
    /// Analyzes a function body to detect fail and absent statements.
    /// </summary>
    private void AnalyzeFunctionBody(Statement statement)
    {
        if (statement is FailStatement)
        {
            _hasFail = true;
        }
        else if (statement is AbsentStatement)
        {
            _hasAbsent = true;
        }
        else if (statement is BlockStatement block)
        {
            foreach (Statement stmt in block.Statements)
            {
                AnalyzeFunctionBody(stmt);
            }
        }
        else if (statement is IfStatement ifStmt)
        {
            AnalyzeFunctionBody(ifStmt.ThenStatement);
            if (ifStmt.ElseStatement != null)
            {
                AnalyzeFunctionBody(ifStmt.ElseStatement);
            }
        }
        else if (statement is WhileStatement whileStmt)
        {
            AnalyzeFunctionBody(whileStmt.Body);
        }
        else if (statement is ForStatement forStmt)
        {
            AnalyzeFunctionBody(forStmt.Body);
        }
        else if (statement is WhenStatement whenStmt)
        {
            foreach (WhenClause clause in whenStmt.Clauses)
            {
                AnalyzeFunctionBody(clause.Body);
            }
        }
    }

    /// <summary>
    /// Generates the try_ variant that wraps crashes in Maybe&lt;T&gt;.
    /// fail Error => return None
    /// return value => return Some(value)
    /// </summary>
    private FunctionDeclaration GenerateTryVariant(FunctionDeclaration original)
    {
        // Strip the ! suffix from the original name since try_ variants are not failable
        string baseName = original.Name.TrimEnd('!');
        string newName = $"try_{baseName}";

        // Wrap return type in Maybe<T>
        TypeExpression? newReturnType = original.ReturnType != null
            ? new TypeExpression(
                Name: "Maybe",
                GenericArguments: new List<TypeExpression> { original.ReturnType },
                Location: original.ReturnType.Location)
            : null;

        // Transform the original body:
        // - fail Error => return None
        // - return value => return Some(value)
        Statement? transformedBody = original.Body != null
            ? TransformBodyForTryVariant(original.Body, original.Location)
            : null;

        return new FunctionDeclaration(
            Name: newName,
            Parameters: original.Parameters,
            ReturnType: newReturnType,
            Body: transformedBody,
            Visibility: original.Visibility,
            Attributes: original.Attributes,
            Location: original.Location);
    }

    /// <summary>
    /// Transforms a function body for try_ variant:
    /// - fail Error => return None (compiler handles Maybe type)
    /// - absent => return None (compiler handles Maybe type)
    /// - return value => return value (compiler handles implicit Some)
    /// </summary>
    private Statement TransformBodyForTryVariant(Statement statement, SourceLocation location)
    {
        return statement switch
        {
            // fail Error => return None
            FailStatement => new ReturnStatement(
                Value: new IdentifierExpression(Name: "None", Location: location),
                Location: location),

            // absent => return None
            AbsentStatement => new ReturnStatement(
                Value: new IdentifierExpression(Name: "None", Location: location),
                Location: location),

            // return value stays the same - compiler handles implicit Some wrapping
            ReturnStatement => statement,

            BlockStatement block => new BlockStatement(
                Statements: block.Statements
                    .Select(s => TransformBodyForTryVariant(s, location))
                    .ToList(),
                Location: block.Location),

            IfStatement ifStmt => new IfStatement(
                Condition: ifStmt.Condition,
                ThenStatement: TransformBodyForTryVariant(ifStmt.ThenStatement, location),
                ElseStatement: ifStmt.ElseStatement != null
                    ? TransformBodyForTryVariant(ifStmt.ElseStatement, location)
                    : null,
                Location: ifStmt.Location),

            WhileStatement whileStmt => new WhileStatement(
                Condition: whileStmt.Condition,
                Body: TransformBodyForTryVariant(whileStmt.Body, location),
                Location: whileStmt.Location),

            ForStatement forStmt => new ForStatement(
                Variable: forStmt.Variable,
                Iterable: forStmt.Iterable,
                Body: TransformBodyForTryVariant(forStmt.Body, location),
                Location: forStmt.Location),

            WhenStatement whenStmt => new WhenStatement(
                Expression: whenStmt.Expression,
                Clauses: whenStmt.Clauses
                    .Select(c => new WhenClause(
                        Pattern: c.Pattern,
                        Body: TransformBodyForTryVariant(c.Body, location),
                        Location: c.Location))
                    .ToList(),
                Location: whenStmt.Location),

            _ => statement // Pass through other statements unchanged
        };
    }

    /// <summary>
    /// Generates the check_ variant that wraps fail in Result&lt;T&gt;.
    /// fail Error => return error (compiler handles Result type)
    /// return value => return value (compiler handles implicit Ok)
    /// </summary>
    private FunctionDeclaration GenerateCheckVariant(FunctionDeclaration original)
    {
        // Strip the ! suffix from the original name since check_ variants are not failable
        string baseName = original.Name.TrimEnd('!');
        string newName = $"check_{baseName}";

        // Wrap return type in Result<T>
        TypeExpression? newReturnType = original.ReturnType != null
            ? new TypeExpression(
                Name: "Result",
                GenericArguments: new List<TypeExpression> { original.ReturnType },
                Location: original.ReturnType.Location)
            : null;

        // Transform the original body:
        // - fail Error => return error
        // - return value => return value
        Statement? transformedBody = original.Body != null
            ? TransformBodyForCheckVariant(original.Body, original.Location)
            : null;

        return new FunctionDeclaration(
            Name: newName,
            Parameters: original.Parameters,
            ReturnType: newReturnType,
            Body: transformedBody,
            Visibility: original.Visibility,
            Attributes: original.Attributes,
            Location: original.Location);
    }

    /// <summary>
    /// Transforms a function body for check_ variant:
    /// - fail Error => return error (compiler handles Result type)
    /// - return value => return value (compiler handles implicit Ok)
    /// </summary>
    private Statement TransformBodyForCheckVariant(Statement statement, SourceLocation location)
    {
        return statement switch
        {
            // fail Error => return error (direct return of Error value)
            FailStatement failStmt => new ReturnStatement(
                Value: failStmt.Error,
                Location: location),

            // return value stays the same - compiler handles implicit Ok wrapping
            ReturnStatement => statement,

            BlockStatement block => new BlockStatement(
                Statements: block.Statements
                    .Select(s => TransformBodyForCheckVariant(s, location))
                    .ToList(),
                Location: block.Location),

            IfStatement ifStmt => new IfStatement(
                Condition: ifStmt.Condition,
                ThenStatement: TransformBodyForCheckVariant(ifStmt.ThenStatement, location),
                ElseStatement: ifStmt.ElseStatement != null
                    ? TransformBodyForCheckVariant(ifStmt.ElseStatement, location)
                    : null,
                Location: ifStmt.Location),

            WhileStatement whileStmt => new WhileStatement(
                Condition: whileStmt.Condition,
                Body: TransformBodyForCheckVariant(whileStmt.Body, location),
                Location: whileStmt.Location),

            ForStatement forStmt => new ForStatement(
                Variable: forStmt.Variable,
                Iterable: forStmt.Iterable,
                Body: TransformBodyForCheckVariant(forStmt.Body, location),
                Location: forStmt.Location),

            WhenStatement whenStmt => new WhenStatement(
                Expression: whenStmt.Expression,
                Clauses: whenStmt.Clauses
                    .Select(c => new WhenClause(
                        Pattern: c.Pattern,
                        Body: TransformBodyForCheckVariant(c.Body, location),
                        Location: c.Location))
                    .ToList(),
                Location: whenStmt.Location),

            _ => statement // Pass through other statements unchanged
        };
    }

    /// <summary>
    /// Generates the find_ variant that wraps fail/absent in Lookup&lt;T&gt;.
    /// fail Error => return error (compiler handles Lookup type)
    /// absent => return None
    /// return value => return value (compiler handles implicit Value)
    /// </summary>
    private FunctionDeclaration GenerateFindVariant(FunctionDeclaration original)
    {
        // Strip the ! suffix from the original name since find_ variants are not failable
        string baseName = original.Name.TrimEnd('!');
        string newName = $"find_{baseName}";

        // Wrap return type in Lookup<T>
        TypeExpression? newReturnType = original.ReturnType != null
            ? new TypeExpression(
                Name: "Lookup",
                GenericArguments: new List<TypeExpression> { original.ReturnType },
                Location: original.ReturnType.Location)
            : null;

        // Transform the original body:
        // - fail Error => return error
        // - absent => return None
        // - return value => return value
        Statement? transformedBody = original.Body != null
            ? TransformBodyForFindVariant(original.Body, original.Location)
            : null;

        return new FunctionDeclaration(
            Name: newName,
            Parameters: original.Parameters,
            ReturnType: newReturnType,
            Body: transformedBody,
            Visibility: original.Visibility,
            Attributes: original.Attributes,
            Location: original.Location);
    }

    /// <summary>
    /// Transforms a function body for find_ variant:
    /// - fail Error => return error (compiler handles Lookup type)
    /// - absent => return None
    /// - return value => return value (compiler handles implicit Value)
    /// </summary>
    private Statement TransformBodyForFindVariant(Statement statement, SourceLocation location)
    {
        return statement switch
        {
            // fail Error => return error (direct return of Error value)
            FailStatement failStmt => new ReturnStatement(
                Value: failStmt.Error,
                Location: location),

            // absent => return None
            AbsentStatement => new ReturnStatement(
                Value: new IdentifierExpression(Name: "None", Location: location),
                Location: location),

            // return value stays the same - compiler handles implicit Value wrapping
            ReturnStatement => statement,

            BlockStatement block => new BlockStatement(
                Statements: block.Statements
                    .Select(s => TransformBodyForFindVariant(s, location))
                    .ToList(),
                Location: block.Location),

            IfStatement ifStmt => new IfStatement(
                Condition: ifStmt.Condition,
                ThenStatement: TransformBodyForFindVariant(ifStmt.ThenStatement, location),
                ElseStatement: ifStmt.ElseStatement != null
                    ? TransformBodyForFindVariant(ifStmt.ElseStatement, location)
                    : null,
                Location: ifStmt.Location),

            WhileStatement whileStmt => new WhileStatement(
                Condition: whileStmt.Condition,
                Body: TransformBodyForFindVariant(whileStmt.Body, location),
                Location: whileStmt.Location),

            ForStatement forStmt => new ForStatement(
                Variable: forStmt.Variable,
                Iterable: forStmt.Iterable,
                Body: TransformBodyForFindVariant(forStmt.Body, location),
                Location: forStmt.Location),

            WhenStatement whenStmt => new WhenStatement(
                Expression: whenStmt.Expression,
                Clauses: whenStmt.Clauses
                    .Select(c => new WhenClause(
                        Pattern: c.Pattern,
                        Body: TransformBodyForFindVariant(c.Body, location),
                        Location: c.Location))
                    .ToList(),
                Location: whenStmt.Location),

            _ => statement // Pass through other statements unchanged
        };
    }

    #region Unimplemented Visitor Methods

    public object? VisitVariableDeclaration(VariableDeclaration node) => null;
    public object? VisitClassDeclaration(ClassDeclaration node) => null;
    public object? VisitStructDeclaration(StructDeclaration node) => null;
    public object? VisitMenuDeclaration(MenuDeclaration node) => null;
    public object? VisitVariantDeclaration(VariantDeclaration node) => null;
    public object? VisitFeatureDeclaration(FeatureDeclaration node) => null;
    public object? VisitImplementationDeclaration(ImplementationDeclaration node) => null;
    public object? VisitImportDeclaration(ImportDeclaration node) => null;
    public object? VisitRedefinitionDeclaration(RedefinitionDeclaration node) => null;
    public object? VisitUsingDeclaration(UsingDeclaration node) => null;
    public object? VisitExternalDeclaration(ExternalDeclaration node) => null;
    public object? VisitExpressionStatement(ExpressionStatement node) => null;
    public object? VisitDeclarationStatement(DeclarationStatement node) => null;
    public object? VisitAssignmentStatement(AssignmentStatement node) => null;
    public object? VisitReturnStatement(ReturnStatement node) => null;
    public object? VisitIfStatement(IfStatement node) => null;
    public object? VisitWhileStatement(WhileStatement node) => null;
    public object? VisitForStatement(ForStatement node) => null;
    public object? VisitWhenStatement(WhenStatement node) => null;
    public object? VisitBlockStatement(BlockStatement node) => null;
    public object? VisitBreakStatement(BreakStatement node) => null;
    public object? VisitContinueStatement(ContinueStatement node) => null;
    public object? VisitFailStatement(FailStatement node) => null;
    public object? VisitAbsentStatement(AbsentStatement node) => null;
    public object? VisitLiteralExpression(LiteralExpression node) => null;
    public object? VisitIdentifierExpression(IdentifierExpression node) => null;
    public object? VisitBinaryExpression(BinaryExpression node) => null;
    public object? VisitUnaryExpression(UnaryExpression node) => null;
    public object? VisitCallExpression(CallExpression node) => null;
    public object? VisitMemberExpression(MemberExpression node) => null;
    public object? VisitIndexExpression(IndexExpression node) => null;
    public object? VisitConditionalExpression(ConditionalExpression node) => null;
    public object? VisitRangeExpression(RangeExpression node) => null;
    public object? VisitChainedComparisonExpression(ChainedComparisonExpression node) => null;
    public object? VisitLambdaExpression(LambdaExpression node) => null;
    public object? VisitTypeExpression(TypeExpression node) => null;
    public object? VisitTypeConversionExpression(TypeConversionExpression node) => null;
    public object? VisitSliceConstructorExpression(SliceConstructorExpression node) => null;
    public object? VisitGenericMethodCallExpression(GenericMethodCallExpression node) => null;
    public object? VisitGenericMemberExpression(GenericMemberExpression node) => null;
    public object? VisitMemoryOperationExpression(MemoryOperationExpression node) => null;
    public object? VisitIntrinsicCallExpression(IntrinsicCallExpression node) => null;
    public object? VisitDangerStatement(DangerStatement node) => null;
    public object? VisitMayhemStatement(MayhemStatement node) => null;
    public object? VisitViewingStatement(ViewingStatement node) => null;
    public object? VisitHijackingStatement(HijackingStatement node) => null;
    public object? VisitObservingStatement(ObservingStatement node) => null;
    public object? VisitSeizingStatement(SeizingStatement node) => null;
    public object? VisitNamedArgumentExpression(NamedArgumentExpression node) => null;

    #endregion
}
