using Compilers.Shared.AST;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Comprehensive semantic analyzer for RazorForge and Cake languages.
/// 
/// This analyzer performs multi-phase semantic analysis combining:
/// <list type="bullet">
/// <item>Traditional type checking and symbol resolution</item>
/// <item>Advanced memory safety analysis with ownership tracking</item>
/// <item>Language-specific behavior handling (RazorForge vs Cake)</item>
/// <item>Memory operation validation (hijack!, share!, etc.)</item>
/// <item>Cross-language compatibility checking</item>
/// </list>
/// 
/// The analyzer integrates tightly with the MemoryAnalyzer to enforce
/// RazorForge's explicit memory model and Cake's automatic RC model.
/// It validates memory operations, tracks object ownership, and prevents
/// use-after-invalidation errors during compilation.
/// 
/// Key responsibilities:
/// <list type="bullet">
/// <item>Type compatibility checking with mixed-type arithmetic rejection</item>
/// <item>Symbol table management with proper lexical scoping</item>
/// <item>Memory operation method call detection and validation</item>
/// <item>Usurping function rule enforcement</item>
/// <item>Container move semantics vs automatic RC handling</item>
/// <item>Wrapper type creation and transformation tracking</item>
/// </list>
/// </summary>
public class SemanticAnalyzer : IAstVisitor<object?>
{
    /// <summary>Symbol table for variable, function, and type declarations</summary>
    private readonly SymbolTable _symbolTable;

    /// <summary>Memory safety analyzer for ownership tracking and memory operations</summary>
    private readonly MemoryAnalyzer _memoryAnalyzer;

    /// <summary>List of semantic errors found during analysis</summary>
    private readonly List<SemanticError> _errors;

    /// <summary>Target language (RazorForge or Cake) for language-specific behavior</summary>
    private readonly Language _language;

    /// <summary>Language mode for additional behavior customization</summary>
    private readonly LanguageMode _mode;

    /// <summary>Tracks whether we're currently inside a danger block</summary>
    private bool _isInDangerMode = false;

    /// <summary>Tracks whether we're currently inside a mayhem block</summary>
    private bool _isInMayhemMode = false;

    /// <summary>
    /// Initialize semantic analyzer with integrated memory safety analysis.
    /// Sets up both traditional semantic analysis and memory model enforcement
    /// based on the target language's memory management strategy.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Cake)</param>
    /// <param name="mode">Language mode for behavior customization</param>
    public SemanticAnalyzer(Language language, LanguageMode mode)
    {
        _symbolTable = new SymbolTable();
        _memoryAnalyzer = new MemoryAnalyzer(language: language, mode: mode);
        _errors = new List<SemanticError>();
        _language = language;
        _mode = mode;

        InitializeBuiltInTypes();
    }

    /// <summary>
    /// Initialize built-in types for the RazorForge language.
    /// Registers standard library types like HeapSlice and StackSlice.
    /// </summary>
    private void InitializeBuiltInTypes()
    {
        // Register HeapSlice record type
        var heapSliceType = new TypeInfo(Name: "HeapSlice", IsReference: false);
        var heapSliceSymbol =
            new StructSymbol(Name: "HeapSlice", Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: heapSliceSymbol);

        // Register StackSlice record type
        var stackSliceType = new TypeInfo(Name: "StackSlice", IsReference: false);
        var stackSliceSymbol =
            new StructSymbol(Name: "StackSlice", Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: stackSliceSymbol);

        // Register primitive types
        RegisterPrimitiveType(typeName: "sysuint");
        RegisterPrimitiveType(typeName: "syssint");
        RegisterPrimitiveType(typeName: "u8");
        RegisterPrimitiveType(typeName: "u16");
        RegisterPrimitiveType(typeName: "u32");
        RegisterPrimitiveType(typeName: "u64");
        RegisterPrimitiveType(typeName: "u128");
        RegisterPrimitiveType(typeName: "s8");
        RegisterPrimitiveType(typeName: "s16");
        RegisterPrimitiveType(typeName: "s32");
        RegisterPrimitiveType(typeName: "s64");
        RegisterPrimitiveType(typeName: "s128");
        RegisterPrimitiveType(typeName: "f16");
        RegisterPrimitiveType(typeName: "f32");
        RegisterPrimitiveType(typeName: "f64");
        RegisterPrimitiveType(typeName: "f128");
        RegisterPrimitiveType(typeName: "d32");
        RegisterPrimitiveType(typeName: "d64");
        RegisterPrimitiveType(typeName: "d128");
        RegisterPrimitiveType(typeName: "bool");
        RegisterPrimitiveType(typeName: "letter");
        RegisterPrimitiveType(typeName: "letter8");
        RegisterPrimitiveType(typeName: "letter16");
        RegisterPrimitiveType(typeName: "text");
        RegisterPrimitiveType(typeName: "text8");
        RegisterPrimitiveType(typeName: "text16");
    }

    /// <summary>
    /// Helper method to register a primitive type.
    /// </summary>
    private void RegisterPrimitiveType(string typeName)
    {
        var typeInfo = new TypeInfo(Name: typeName, IsReference: false);
        var typeSymbol = new TypeSymbol(Name: typeName, TypeInfo: typeInfo,
            Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: typeSymbol);
    }

    /// <summary>
    /// Get all semantic and memory safety errors discovered during analysis.
    /// Combines traditional semantic errors with memory safety violations
    /// from the integrated memory analyzer for comprehensive error reporting.
    /// </summary>
    public List<SemanticError> Errors
    {
        get
        {
            var allErrors = new List<SemanticError>(collection: _errors);
            // Convert memory safety violations to semantic errors for unified reporting
            allErrors.AddRange(collection: _memoryAnalyzer.Errors.Select(selector: me =>
                new SemanticError(Message: me.Message, Location: me.Location)));
            return allErrors;
        }
    }

    public List<SemanticError> Analyze(AST.Program program)
    {
        program.Accept(visitor: this);
        return Errors;
    }

    // Program
    public object? VisitProgram(AST.Program node)
    {
        foreach (IAstNode declaration in node.Declarations)
        {
            declaration.Accept(visitor: this);
        }

        return null;
    }

    // Declarations
    /// <summary>
    /// Analyze variable declarations with integrated memory safety tracking.
    /// Performs traditional type checking while registering objects in the memory analyzer
    /// for ownership tracking. This is where objects enter the memory model and become
    /// subject to memory safety rules.
    /// 
    /// RazorForge: Objects start as Owned with direct ownership
    /// Cake: Objects start as Shared with automatic reference counting
    /// </summary>
    public object? VisitVariableDeclaration(VariableDeclaration node)
    {
        // Type check initializer expression if present
        if (node.Initializer != null)
        {
            var initType = node.Initializer.Accept(visitor: this) as TypeInfo;

            // Validate type compatibility when explicit type is declared
            if (node.Type != null)
            {
                TypeInfo? declaredType = ResolveType(typeExpr: node.Type);
                if (declaredType != null && !IsAssignable(target: declaredType, source: initType))
                {
                    AddError(
                        message:
                        $"Cannot assign {initType?.Name ?? "unknown"} to {declaredType.Name}",
                        location: node.Location);
                }
            }

            // CRITICAL: Register object in memory analyzer for ownership tracking
            // This is where objects enter the memory model and become subject to safety rules
            TypeInfo type = ResolveType(typeExpr: node.Type) ??
                            initType ?? new TypeInfo(Name: "Unknown", IsReference: false);
            _memoryAnalyzer.RegisterObject(name: node.Name, type: type, location: node.Location);
        }

        // Add variable symbol to symbol table for name resolution
        // Use inferred type from initializer if no explicit type is specified
        TypeInfo? variableType = null;
        if (node.Type != null)
        {
            variableType = ResolveType(typeExpr: node.Type);
        }
        else if (node.Initializer != null)
        {
            // Infer type from initializer for auto variables
            variableType = node.Initializer.Accept(visitor: this) as TypeInfo;
        }

        var symbol = new VariableSymbol(Name: node.Name, Type: variableType,
            IsMutable: node.IsMutable, Visibility: node.Visibility);
        if (!_symbolTable.TryDeclare(symbol: symbol))
        {
            AddError(message: $"Variable '{node.Name}' is already declared in current scope",
                location: node.Location);
        }

        return null;
    }

    /// <summary>
    /// Analyze function declarations with usurping function detection and memory scope management.
    /// Handles the special case of usurping functions that are allowed to return Hijacked&lt;T&gt; objects.
    /// Manages both symbol table and memory analyzer scopes for proper isolation of function context.
    /// 
    /// Usurping functions are RazorForge-only and must be explicitly marked to return exclusive tokens.
    /// This prevents accidental exclusive token leakage from regular functions.
    /// </summary>
    public object? VisitFunctionDeclaration(FunctionDeclaration node)
    {
        // Detect usurping functions that can return exclusive tokens (Hijacked<T>)
        // TODO: This should be replaced with an IsUsurping property on FunctionDeclaration
        bool isUsurping = node.Name.Contains(value: "usurping") ||
                          CheckIfUsurpingFunction(node: node);

        if (isUsurping)
        {
            // Enable usurping mode for exclusive token returns
            _memoryAnalyzer.EnterUsurpingFunction();
        }

        // Enter new lexical scopes for function parameters and body isolation
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();

        try
        {
            // Process function parameters - add to both symbol table and memory analyzer
            foreach (Parameter param in node.Parameters)
            {
                TypeInfo? paramType = ResolveType(typeExpr: param.Type);
                var paramSymbol = new VariableSymbol(Name: param.Name, Type: paramType,
                    IsMutable: false, Visibility: VisibilityModifier.Private);
                _symbolTable.TryDeclare(symbol: paramSymbol);

                // Register parameter objects in memory analyzer for ownership tracking
                // Parameters enter the function with appropriate wrapper types based on language
                if (paramType != null)
                {
                    _memoryAnalyzer.RegisterObject(name: param.Name, type: paramType,
                        location: node.Location);
                }
            }

            // CRITICAL: Validate return type against usurping function rules
            // Only usurping functions can return Hijacked<T> (exclusive tokens)
            if (node.ReturnType != null)
            {
                TypeInfo? funcReturnType = ResolveType(typeExpr: node.ReturnType);
                if (funcReturnType != null)
                {
                    _memoryAnalyzer.ValidateFunctionReturn(returnType: funcReturnType,
                        location: node.Location);
                }
            }

            // Analyze function body with full memory safety checking
            if (node.Body != null)
            {
                node.Body.Accept(visitor: this);
            }
        }
        finally
        {
            _symbolTable.ExitScope();
            _memoryAnalyzer.ExitScope();

            if (isUsurping)
            {
                _memoryAnalyzer.ExitUsurpingFunction();
            }
        }

        // Add function to symbol table
        TypeInfo? returnType = ResolveType(typeExpr: node.ReturnType);
        var funcSymbol = new FunctionSymbol(Name: node.Name, Parameters: node.Parameters,
            ReturnType: returnType, Visibility: node.Visibility, IsUsurping: isUsurping);
        if (!_symbolTable.TryDeclare(symbol: funcSymbol))
        {
            AddError(message: $"Function '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    public object? VisitClassDeclaration(ClassDeclaration node)
    {
        // Enter entity scope
        _symbolTable.EnterScope();

        try
        {
            // Process entity members
            foreach (Declaration member in node.Members)
            {
                member.Accept(visitor: this);
            }
        }
        finally
        {
            _symbolTable.ExitScope();
        }

        // Add entity to symbol table
        var classSymbol = new ClassSymbol(Name: node.Name, BaseClass: node.BaseClass,
            Interfaces: node.Interfaces, Visibility: node.Visibility);
        if (!_symbolTable.TryDeclare(symbol: classSymbol))
        {
            AddError(message: $"Entity '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    public object? VisitStructDeclaration(StructDeclaration node)
    {
        // Similar to entity but with value semantics
        var structSymbol = new StructSymbol(Name: node.Name, Visibility: node.Visibility);
        if (!_symbolTable.TryDeclare(symbol: structSymbol))
        {
            AddError(message: $"Record '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    public object? VisitMenuDeclaration(MenuDeclaration node)
    {
        var menuSymbol = new MenuSymbol(Name: node.Name, Visibility: node.Visibility);
        if (!_symbolTable.TryDeclare(symbol: menuSymbol))
        {
            AddError(message: $"Option '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    public object? VisitVariantDeclaration(VariantDeclaration node)
    {
        // Validation based on variant kind
        switch (node.Kind)
        {
            case VariantKind.Chimera:
            case VariantKind.Mutant:
                // TODO: Check if we're in a danger! block
                // For now, we'll add a warning
                if (!IsInDangerBlock())
                {
                    AddError(
                        message:
                        $"{node.Kind} '{node.Name}' must be declared inside a danger! block",
                        location: node.Location);
                }

                break;

            case VariantKind.Variant:
                // Validate that all fields in all cases are records (value types)
                foreach (VariantCase variantCase in node.Cases)
                {
                    if (variantCase.AssociatedTypes != null)
                    {
                        foreach (TypeExpression type in variantCase.AssociatedTypes)
                        {
                            // Check if type is an entity (reference type)
                            if (IsEntityType(type: type))
                            {
                                AddError(
                                    message:
                                    $"Variant '{node.Name}' case '{variantCase.Name}' contains entity type '{type}'. All variant fields must be records (value types)",
                                    location: node.Location);
                            }
                        }
                    }
                }

                break;
        }

        var variantSymbol = new VariantSymbol(Name: node.Name, Visibility: node.Visibility);
        if (!_symbolTable.TryDeclare(symbol: variantSymbol))
        {
            AddError(message: $"Variant '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    public object? VisitFeatureDeclaration(FeatureDeclaration node)
    {
        var featureSymbol = new FeatureSymbol(Name: node.Name, Visibility: node.Visibility);
        if (!_symbolTable.TryDeclare(symbol: featureSymbol))
        {
            AddError(message: $"Feature '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    public object? VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        // Implementation blocks don't create new symbols but verify interfaces
        return null;
    }

    public object? VisitImportDeclaration(ImportDeclaration node)
    {
        // TODO: Module system
        return null;
    }

    public object? VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        // TODO: Handle method redefinition
        return null;
    }

    public object? VisitUsingDeclaration(UsingDeclaration node)
    {
        // TODO: Handle type alias
        return null;
    }

    // Statements
    public object? VisitExpressionStatement(ExpressionStatement node)
    {
        node.Expression.Accept(visitor: this);
        return null;
    }

    public object? VisitDeclarationStatement(DeclarationStatement node)
    {
        node.Declaration.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Analyze assignment statements with language-specific memory model handling.
    /// 
    /// This method demonstrates the fundamental difference between RazorForge and Cake:
    /// 
    /// RazorForge: Assignments use move semantics - objects are transferred and source may become invalid.
    /// The analyzer needs sophisticated analysis to determine when moves occur vs copies.
    /// 
    /// Cake: Assignments use automatic reference counting - both source and target share the object
    /// with automatic RC increment. No invalidation occurs, promoting safe sharing.
    /// 
    /// This difference reflects each language's memory management philosophy:
    /// explicit control vs automatic safety.
    /// </summary>
    public object? VisitAssignmentStatement(AssignmentStatement node)
    {
        // Standard type compatibility checking
        var targetType = node.Target.Accept(visitor: this) as TypeInfo;
        var valueType = node.Value.Accept(visitor: this) as TypeInfo;

        if (targetType != null && valueType != null &&
            !IsAssignable(target: targetType, source: valueType))
        {
            AddError(message: $"Cannot assign {valueType.Name} to {targetType.Name}",
                location: node.Location);
        }

        // CRITICAL: Language-specific memory model handling for assignments
        if (node.Target is IdentifierExpression targetId &&
            node.Value is IdentifierExpression valueId)
        {
            if (_language == Language.Cake)
            {
                // Cake: Automatic reference counting - both variables share the same object
                // Source remains valid, RC is incremented, no invalidation occurs
                _memoryAnalyzer.HandleCakeAssignment(target: targetId.Name, source: valueId.Name,
                    location: node.Location);
            }
            else if (_language == Language.RazorForge)
            {
                // RazorForge: Move semantics - object ownership may transfer
                // TODO: Implement sophisticated move analysis (copy vs move determination)
                // For now, treat as creating new object reference
                if (targetType != null)
                {
                    _memoryAnalyzer.RegisterObject(name: targetId.Name, type: targetType,
                        location: node.Location);
                }
            }
        }

        return null;
    }

    public object? VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value != null)
        {
            node.Value.Accept(visitor: this);
        }

        return null;
    }

    public object? VisitIfStatement(IfStatement node)
    {
        // Check condition is boolean
        var conditionType = node.Condition.Accept(visitor: this) as TypeInfo;
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError(message: $"If condition must be boolean, got {conditionType.Name}",
                location: node.Location);
        }

        node.ThenStatement.Accept(visitor: this);
        node.ElseStatement?.Accept(visitor: this);
        return null;
    }

    public object? VisitWhileStatement(WhileStatement node)
    {
        // Check condition is boolean  
        var conditionType = node.Condition.Accept(visitor: this) as TypeInfo;
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError(message: $"While condition must be boolean, got {conditionType.Name}",
                location: node.Location);
        }

        node.Body.Accept(visitor: this);
        return null;
    }

    public object? VisitForStatement(ForStatement node)
    {
        // Enter new scope for loop variable
        _symbolTable.EnterScope();

        try
        {
            // Check iterable type
            var iterableType = node.Iterable.Accept(visitor: this) as TypeInfo;
            // TODO: Check if iterable implements Iterable interface

            // Add loop variable to scope
            var loopVarSymbol = new VariableSymbol(Name: node.Variable, Type: null,
                IsMutable: false, Visibility: VisibilityModifier.Private);
            _symbolTable.TryDeclare(symbol: loopVarSymbol);

            node.Body.Accept(visitor: this);
        }
        finally
        {
            _symbolTable.ExitScope();
        }

        return null;
    }

    public object? VisitWhenStatement(WhenStatement node)
    {
        var expressionType = node.Expression.Accept(visitor: this) as TypeInfo;

        foreach (WhenClause clause in node.Clauses)
        {
            // Enter new scope for pattern variables
            _symbolTable.EnterScope();

            try
            {
                // TODO: Type check pattern against expression
                clause.Body.Accept(visitor: this);
            }
            finally
            {
                _symbolTable.ExitScope();
            }
        }

        return null;
    }

    /// <summary>
    /// Analyze block statements with proper scope management for both symbols and memory objects.
    /// Block scopes are fundamental to memory safety - when a scope exits, all objects declared
    /// within become invalid (deadref protection). This prevents use-after-scope errors.
    /// 
    /// The memory analyzer automatically invalidates all objects in the scope when it exits,
    /// implementing the core principle that objects cannot outlive their lexical scope.
    /// </summary>
    public object? VisitBlockStatement(BlockStatement node)
    {
        // Enter new lexical scope for both symbol resolution and memory tracking
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();

        try
        {
            // Analyze all statements within the protected scope
            foreach (Statement statement in node.Statements)
            {
                statement.Accept(visitor: this);
            }
        }
        finally
        {
            // CRITICAL: Scope cleanup automatically invalidates all objects in this scope
            // This is a fundamental memory safety mechanism preventing use-after-scope
            _symbolTable.ExitScope();
            _memoryAnalyzer.ExitScope(); // Invalidates all objects declared in this scope
        }

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

    // Expressions  
    public object? VisitLiteralExpression(LiteralExpression node)
    {
        return InferLiteralType(value: node.Value);
    }

    public object? VisitIdentifierExpression(IdentifierExpression node)
    {
        Symbol? symbol = _symbolTable.Lookup(name: node.Name);
        if (symbol == null)
        {
            AddError(message: $"Undefined identifier '{node.Name}'", location: node.Location);
            return null;
        }

        return symbol.Type;
    }

    public object? VisitBinaryExpression(BinaryExpression node)
    {
        var leftType = node.Left.Accept(visitor: this) as TypeInfo;
        var rightType = node.Right.Accept(visitor: this) as TypeInfo;

        // Check for mixed-type arithmetic (REJECTED per user requirement)
        if (leftType != null && rightType != null && IsArithmeticOperator(op: node.Operator))
        {
            if (!AreTypesCompatible(left: leftType, right: rightType))
            {
                AddError(
                    message:
                    $"Mixed-type arithmetic is not allowed. Cannot perform {node.Operator} between {leftType.Name} and {rightType.Name}. Use explicit type conversion with {rightType.Name}!(x) or x.{rightType.Name}!().",
                    location: node.Location);
                return null;
            }
        }

        // Return the common type (they should be the same if we reach here)
        return leftType ?? rightType;
    }

    private bool IsArithmeticOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply
                or BinaryOperator.TrueDivide or BinaryOperator.Divide or BinaryOperator.Modulo
                or BinaryOperator.Power or BinaryOperator.AddWrap or BinaryOperator.SubtractWrap
                or BinaryOperator.MultiplyWrap or BinaryOperator.DivideWrap
                or BinaryOperator.ModuloWrap or BinaryOperator.PowerWrap
                or BinaryOperator.AddSaturate or BinaryOperator.SubtractSaturate
                or BinaryOperator.MultiplySaturate or BinaryOperator.DivideSaturate
                or BinaryOperator.ModuloSaturate or BinaryOperator.PowerSaturate
                or BinaryOperator.AddUnchecked or BinaryOperator.SubtractUnchecked
                or BinaryOperator.MultiplyUnchecked or BinaryOperator.DivideUnchecked
                or BinaryOperator.ModuloUnchecked or BinaryOperator.PowerUnchecked
                or BinaryOperator.AddChecked or BinaryOperator.SubtractChecked
                or BinaryOperator.MultiplyChecked or BinaryOperator.DivideChecked
                or BinaryOperator.ModuloChecked or BinaryOperator.PowerChecked => true,
            _ => false
        };
    }

    private bool AreTypesCompatible(TypeInfo left, TypeInfo right)
    {
        // Types are compatible if they are exactly the same
        return left.Name == right.Name && left.IsReference == right.IsReference;
    }

    public object? VisitUnaryExpression(UnaryExpression node)
    {
        var operandType = node.Operand.Accept(visitor: this) as TypeInfo;
        // TODO: Check unary operator compatibility
        return operandType;
    }

    /// <summary>
    /// Analyze function calls with special handling for memory operation methods.
    /// 
    /// This is where the magic happens for memory operations like obj.share!(), obj.hijack!(), etc.
    /// The analyzer detects method calls ending with '!' and routes them through the memory
    /// analyzer for proper ownership tracking and safety validation.
    /// 
    /// Memory operations are the core of RazorForge's explicit memory model, allowing
    /// programmers to transform objects between different wrapper types (Owned, Shared,
    /// Hijacked, etc.) with compile-time safety guarantees.
    /// 
    /// Regular function calls are handled with standard type checking and argument validation.
    /// </summary>
    public object? VisitCallExpression(CallExpression node)
    {
        // Check if this is a standalone danger zone function call (addr_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;
            if (IsNonGenericDangerZoneFunction(functionName: functionName))
            {
                // Only allow these functions in danger mode
                if (!_isInDangerMode)
                {
                    _errors.Add(item: new SemanticError(
                        Message:
                        $"Danger zone function '{functionName}!' can only be used inside danger blocks",
                        Location: node.Location));
                    return new TypeInfo(Name: "void", IsReference: false);
                }

                return ValidateNonGenericDangerZoneFunction(node: node,
                    functionName: functionName);
            }
        }

        // CRITICAL: Detect memory operation method calls (ending with '!')
        // These are the core operations of RazorForge's memory model
        if (node.Callee is MemberExpression memberExpr &&
            IsMemoryOperation(methodName: memberExpr.PropertyName))
        {
            // Route through specialized memory operation handler
            return HandleMemoryOperationCall(memberExpr: memberExpr,
                operationName: memberExpr.PropertyName, arguments: node.Arguments,
                location: node.Location);
        }

        // Standard function call type checking
        var functionType = node.Callee.Accept(visitor: this) as TypeInfo;

        // Type check all arguments
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // TODO: Return function's actual return type based on signature
        return functionType;
    }

    public object? VisitMemberExpression(MemberExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        // TODO: Check if member exists on type
        return null;
    }

    public object? VisitIndexExpression(IndexExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        var indexType = node.Index.Accept(visitor: this) as TypeInfo;
        // TODO: Check indexing compatibility
        return null;
    }

    public object? VisitConditionalExpression(ConditionalExpression node)
    {
        var conditionType = node.Condition.Accept(visitor: this) as TypeInfo;
        var trueType = node.TrueExpression.Accept(visitor: this) as TypeInfo;
        var falseType = node.FalseExpression.Accept(visitor: this) as TypeInfo;

        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError(message: $"Conditional expression condition must be boolean",
                location: node.Location);
        }

        // TODO: Return common type of true/false branches
        return trueType;
    }

    public object? VisitLambdaExpression(LambdaExpression node)
    {
        // Enter scope for lambda parameters
        _symbolTable.EnterScope();

        try
        {
            foreach (Parameter param in node.Parameters)
            {
                TypeInfo? paramType = ResolveType(typeExpr: param.Type);
                var paramSymbol = new VariableSymbol(Name: param.Name, Type: paramType,
                    IsMutable: false, Visibility: VisibilityModifier.Private);
                _symbolTable.TryDeclare(symbol: paramSymbol);
            }

            return node.Body.Accept(visitor: this);
        }
        finally
        {
            _symbolTable.ExitScope();
        }
    }

    public object? VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        // Check that all operands are comparable
        TypeInfo? prevType = null;

        foreach (Expression operand in node.Operands)
        {
            var operandType = operand.Accept(visitor: this) as TypeInfo;

            if (prevType != null && operandType != null)
            {
                // TODO: Check if types are comparable
                if (!AreComparable(type1: prevType, type2: operandType))
                {
                    AddError(message: $"Cannot compare {prevType.Name} with {operandType.Name}",
                        location: node.Location);
                }
            }

            prevType = operandType;
        }

        // Chained comparisons always return boolean
        return new TypeInfo(Name: "Bool", IsReference: false);
    }

    public object? VisitRangeExpression(RangeExpression node)
    {
        // Check start and end are numeric
        var startType = node.Start.Accept(visitor: this) as TypeInfo;
        var endType = node.End.Accept(visitor: this) as TypeInfo;

        if (startType != null && !IsNumericType(type: startType))
        {
            AddError(message: $"Range start must be numeric, got {startType.Name}",
                location: node.Location);
        }

        if (endType != null && !IsNumericType(type: endType))
        {
            AddError(message: $"Range end must be numeric, got {endType.Name}",
                location: node.Location);
        }

        // Check step if present
        if (node.Step != null)
        {
            var stepType = node.Step.Accept(visitor: this) as TypeInfo;
            if (stepType != null && !IsNumericType(type: stepType))
            {
                AddError(message: $"Range step must be numeric, got {stepType.Name}",
                    location: node.Location);
            }
        }

        // Range expressions return a Range<T> type
        return new TypeInfo(Name: "Range", IsReference: false);
    }

    public object? VisitTypeExpression(TypeExpression node)
    {
        // Type expressions are handled during semantic analysis
        return null;
    }

    public object? VisitTypeConversionExpression(TypeConversionExpression node)
    {
        // Analyze the source expression
        var sourceType = node.Expression.Accept(visitor: this) as TypeInfo;

        // Validate the target type exists
        string targetTypeName = node.TargetType;
        if (!IsValidType(typeName: targetTypeName))
        {
            _errors.Add(item: new SemanticError(Message: $"Unknown type: {targetTypeName}",
                Location: node.Location));
            return null;
        }

        // Check if the conversion is valid
        if (!IsValidConversion(sourceType: sourceType?.Name ?? "unknown",
                targetType: targetTypeName))
        {
            _errors.Add(item: new SemanticError(
                Message:
                $"Cannot convert from {sourceType?.Name ?? "unknown"} to {targetTypeName}",
                Location: node.Location));
            return null;
        }

        // Return the target type
        return new TypeInfo(Name: targetTypeName,
            IsReference: IsUnsignedType(typeName: targetTypeName));
    }

    private bool IsValidType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "syssint" or "u8" or "u16" or "u32"
                or "u64" or "u128" or "sysuint" or "f16" or "f32" or "f64" or "f128" or "d32"
                or "d64" or "d128" or "bool" or "letter8" or "letter16" or "letter32" => true,
            _ => false
        };
    }

    private bool IsValidConversion(string sourceType, string targetType)
    {
        // For now, allow all conversions between numeric types
        // In a production compiler, this would have more sophisticated rules
        return IsNumericType(typeName: sourceType) && IsNumericType(typeName: targetType);
    }

    private bool IsNumericType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "syssint" or "u8" or "u16" or "u32"
                or "u64" or "u128" or "sysuint" or "f16" or "f32" or "f64" or "f128" or "d32"
                or "d64" or "d128" => true,
            _ => false
        };
    }

    private bool IsUnsignedType(string typeName)
    {
        return typeName.StartsWith(value: "u");
    }

    // Helper methods
    private TypeInfo? ResolveType(TypeExpression? typeExpr)
    {
        if (typeExpr == null)
        {
            return null;
        }

        // TODO: Proper type resolution
        return new TypeInfo(Name: typeExpr.Name, IsReference: false);
    }

    private TypeInfo? ResolveType(TypeExpression? typeExpr, SourceLocation location)
    {
        return ResolveType(typeExpr: typeExpr);
    }

    private TypeInfo InferLiteralType(object? value)
    {
        return value switch
        {
            bool => new TypeInfo(Name: "bool", IsReference: false),
            int => new TypeInfo(Name: "s32", IsReference: false),
            long => new TypeInfo(Name: "s64", IsReference: false),
            float => new TypeInfo(Name: "f32", IsReference: false),
            double => new TypeInfo(Name: "f64", IsReference: false),
            string => new TypeInfo(Name: "text", IsReference: false),
            null => new TypeInfo(Name: "none", IsReference: false),
            _ => new TypeInfo(Name: "unknown", IsReference: false)
        };
    }

    private TypeInfo InferLiteralType(object? value, SourceLocation location)
    {
        return InferLiteralType(value: value);
    }

    private bool IsAssignable(TypeInfo target, TypeInfo? source)
    {
        if (source == null)
        {
            return false;
        }

        // TODO: Implement proper type compatibility rules
        return target.Name == source.Name;
    }

    private bool AreComparable(TypeInfo type1, TypeInfo type2)
    {
        // TODO: Implement proper comparability rules
        // For now, allow comparison between same types and numeric types
        return type1.Name == type2.Name ||
               IsNumericType(type: type1) && IsNumericType(type: type2);
    }

    private bool IsNumericType(TypeInfo type)
    {
        return type.Name switch
        {
            "I8" or "I16" or "I32" or "I64" or "I128" or "Isys" or "U8" or "U16" or "U32" or "U64"
                or "U128" or "Usys" or "F16" or "F32" or "F64" or "F128" or "D32" or "D64"
                or "D128" or "Integer" or "Decimal" => true, // Cake arbitrary precision types
            _ => false
        };
    }

    private void AddError(string message, SourceLocation location)
    {
        _errors.Add(item: new SemanticError(Message: message, Location: location));
    }

    /// <summary>
    /// Detect memory operation method calls by their distinctive '!' suffix.
    /// 
    /// Memory operations are the heart of RazorForge's explicit memory model:
    /// - hijack!() - gain exclusive access (red group)
    /// - share!() - create shared ownership (green group) 
    /// - watch!() - create weak observer (green group)
    /// - thread_share!() - thread-safe sharing (blue group)
    /// - thread_watch!() - thread-safe weak reference (blue group)
    /// - steal!() - reclaim ownership when RC=1
    /// - snatch!() - force ownership (danger! only)
    /// - release!() - manual RC decrement
    /// - try_share!(), try_thread_share!() - upgrade weak to strong
    /// - reveal!(), own!() - handle snatched objects (danger! only)
    /// 
    /// The '!' suffix indicates these operations can potentially crash/panic
    /// if used incorrectly, emphasizing their power and responsibility.
    /// </summary>
    /// <param name="methodName">Method name to check</param>
    /// <returns>True if this is a memory operation method</returns>
    private bool IsMemoryOperation(string methodName)
    {
        return methodName switch
        {
            // Core memory transformation operations
            "hijack!" or "share!" or "watch!" or "thread_share!" or "thread_watch!" or "steal!"
                or "snatch!" or "release!" or "try_share!" or "try_thread_share!" or "reveal!"
                or "own!" => true,
            _ => false
        };
    }

    /// <summary>
    /// Detect usurping functions through naming conventions (temporary implementation).
    /// 
    /// Usurping functions are special RazorForge functions that can return exclusive tokens
    /// (Hijacked&lt;T&gt; objects). This prevents accidental exclusive token leakage from regular
    /// functions, which would violate exclusive access guarantees.
    /// 
    /// TODO: This should be replaced with an IsUsurping property on FunctionDeclaration
    /// for proper language support. Current implementation uses naming heuristics.
    /// 
    /// Examples of usurping functions:
    /// - usurping fn create_exclusive() -> Hijacked&lt;Node&gt;
    /// - usurping fn factory_method() -> Hijacked&lt;Widget&gt;
    /// </summary>
    /// <param name="node">Function declaration to check</param>
    /// <returns>True if this function can return exclusive tokens</returns>
    private bool CheckIfUsurpingFunction(FunctionDeclaration node)
    {
        // Temporary heuristic-based detection
        // TODO: Replace with proper AST property when language syntax is finalized
        return node.Name.StartsWith(value: "usurping_") || node.Name.Contains(value: "Usurping");
    }

    /// <summary>
    /// Handle memory operation method calls - the core of RazorForge's memory model.
    /// 
    /// This method processes calls like obj.share!(), obj.hijack!(), etc., which are
    /// the primary way programmers interact with RazorForge's explicit memory management.
    /// 
    /// The process:
    /// 1. Extract the object name (currently limited to simple identifiers)
    /// 2. Parse the operation name to identify the specific memory operation
    /// 3. Delegate to MemoryAnalyzer for ownership tracking and safety validation
    /// 4. Create appropriate wrapper type information for the result
    /// 
    /// Memory operations transform objects between wrapper types while enforcing
    /// safety rules like group separation, reference count constraints, and
    /// use-after-invalidation prevention.
    /// </summary>
    /// <param name="memberExpr">Member expression (obj.method!)</param>
    /// <param name="operationName">Name of memory operation (e.g., "share!")</param>
    /// <param name="arguments">Method arguments (usually empty for memory ops)</param>
    /// <param name="location">Source location for error reporting</param>
    /// <returns>Wrapper type info for the result, or null if operation failed</returns>
    private TypeInfo? HandleMemoryOperationCall(MemberExpression memberExpr, string operationName,
        List<Expression> arguments, SourceLocation location)
    {
        // Extract object name - currently limited to simple identifiers
        // TODO: Support more complex expressions like container[index].share!()
        if (memberExpr.Object is not IdentifierExpression objId)
        {
            AddError(message: "Memory operations can only be called on simple identifiers",
                location: location);
            return null;
        }

        // Parse operation name to memory operation enum
        MemoryOperation? operation = ParseMemoryOperation(operationName: operationName);
        if (operation == null)
        {
            AddError(message: $"Unknown memory operation: {operationName}", location: location);
            return null;
        }

        // CRITICAL: Delegate to memory analyzer for safety validation and ownership tracking
        // This is where all the memory safety magic happens
        MemoryObject? resultObj = _memoryAnalyzer.HandleMemoryOperation(objectName: objId.Name,
            operation: operation.Value, location: location);
        if (resultObj == null)
        {
            // Operation failed - error already reported by memory analyzer
            return null;
        }

        // Create type information for the result wrapper type
        return CreateWrapperTypeInfo(baseType: resultObj.BaseType, wrapper: resultObj.Wrapper);
    }

    /// <summary>
    /// Parse memory operation method names to their corresponding enum values.
    /// 
    /// This mapping connects the source code syntax (method names ending with '!')
    /// to the internal memory operation representation used by the memory analyzer.
    /// 
    /// The systematic naming reflects the memory model's organization:
    /// <list type="bullet">
    /// <item>Basic operations: hijack!, share!, watch!</item>
    /// <item>Thread-safe variants: thread_share!, thread_watch!</item>
    /// <item>Ownership operations: steal!, snatch!, own!</item>
    /// <item>RC management: release!</item>
    /// <item>Weak upgrades: try_share!, try_thread_share!</item>
    /// <item>Unsafe access: reveal!</item>
    /// </list>
    /// </summary>
    /// <param name="operationName">Method name from source code</param>
    /// <returns>Corresponding memory operation enum, or null if not found</returns>
    private MemoryOperation? ParseMemoryOperation(string operationName)
    {
        return operationName switch
        {
            // Group 1: Exclusive access operations
            "hijack!" => MemoryOperation.Hijack,

            // Group 2: Single-threaded shared access
            "share!" => MemoryOperation.Share,
            "watch!" => MemoryOperation.Watch,
            "try_share!" => MemoryOperation.TryShare,

            // Group 3: Multi-threaded shared access
            "thread_share!" => MemoryOperation.ThreadShare,
            "thread_watch!" => MemoryOperation.ThreadWatch,
            "try_thread_share!" => MemoryOperation.TryThreadShare,

            // Ownership reclaim operations
            "steal!" => MemoryOperation.Steal,

            // Manual reference counting
            "release!" => MemoryOperation.Release,

            // Unsafe operations (danger! block only)
            "snatch!" => MemoryOperation.Snatch,
            "reveal!" => MemoryOperation.Reveal,
            "own!" => MemoryOperation.Own,

            _ => null
        };
    }

    private MemoryOperation? ParseMemoryOperation(string operationName, SourceLocation location)
    {
        return ParseMemoryOperation(operationName: operationName);
    }

    /// <summary>
    /// Create TypeInfo instances for wrapper types in RazorForge's memory model.
    /// 
    /// This method generates the type names that appear in the type system for
    /// memory-wrapped objects. Each wrapper type has a distinctive generic syntax:
    /// <list type="bullet">
    /// <item>Owned: Direct type name (Node, List&lt;s32&gt;)</item>
    /// <item>Hijacked&lt;T&gt;: Exclusive access wrapper (red group 🔴)</item>
    /// <item>Shared&lt;T&gt;: Shared ownership wrapper (green group 🟢)</item>
    /// <item>Watched&lt;T&gt;: Weak observer wrapper (brown group 🟤)</item>
    /// <item>ThreadShared&lt;T&gt;: Thread-safe shared wrapper (blue group 🔵)</item>
    /// <item>ThreadWatched&lt;T&gt;: Thread-safe weak wrapper (purple group 🟣)</item>
    /// <item>Snatched&lt;T&gt;: Contaminated ownership wrapper (black group 💀)</item>
    /// </list>
    /// 
    /// These type names provide clear indication of memory semantics in error messages,
    /// IDE tooltips, and documentation.
    /// </summary>
    /// <param name="baseType">Underlying object type</param>
    /// <param name="wrapper">Memory wrapper type</param>
    /// <returns>TypeInfo with appropriate wrapper type name</returns>
    private TypeInfo CreateWrapperTypeInfo(TypeInfo baseType, WrapperType wrapper)
    {
        string typeName = wrapper switch
        {
            // Direct ownership - no wrapper syntax
            WrapperType.Owned => baseType.Name,

            // Memory wrapper types with generic syntax
            WrapperType.Hijacked => $"Hijacked<{baseType.Name}>", // Exclusive access 🔴
            WrapperType.Shared => $"Shared<{baseType.Name}>", // Shared ownership 🟢
            WrapperType.Watched => $"Watched<{baseType.Name}>", // Weak observer 🟤
            WrapperType.ThreadShared => $"ThreadShared<{baseType.Name}>", // Thread-safe shared 🔵
            WrapperType.ThreadWatched => $"ThreadWatched<{baseType.Name}>", // Thread-safe weak 🟣
            WrapperType.Snatched => $"Snatched<{baseType.Name}>", // Contaminated ownership 💀

            _ => baseType.Name
        };

        return new TypeInfo(Name: typeName, IsReference: baseType.IsReference);
    }

    // Memory slice expression visitor methods
    public object? VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        // Validate size expression is compatible with sysuint type
        var sizeType = node.SizeExpression.Accept(visitor: this) as TypeInfo;
        if (sizeType == null)
        {
            AddError(message: $"Slice size expression has unknown type", location: node.Location);
        }
        else if (sizeType.Name != "sysuint")
        {
            // Allow integer literals to be coerced to sysuint
            if (sizeType.IsInteger)
            {
                // Implicit conversion from any integer type to sysuint for slice sizes
                // This handles cases like HeapSlice(64) where 64 might be typed as s32, s64, etc.
            }
            else
            {
                AddError(
                    message:
                    $"Slice size must be of type sysuint or compatible integer type, found {sizeType.Name}",
                    location: node.Location);
            }
        }

        // Return appropriate slice type
        string sliceTypeName = node.SliceType;
        return new TypeInfo(Name: sliceTypeName,
            IsReference: false); // Slice types are value types (structs)
    }

    public object? VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        // Check if this is a standalone global function call (e.g., write_as<T>!, read_as<T>!)
        if (node.Object is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;

            // Handle built-in danger zone operations
            if (IsDangerZoneFunction(functionName: functionName))
            {
                return ValidateDangerZoneFunction(node: node, functionName: functionName);
            }
        }

        // Validate object type supports the generic method
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot call method on null object", location: node.Location);
            return null;
        }

        // Check if this is a slice operation
        if (objectType.Name == "HeapSlice" || objectType.Name == "StackSlice")
        {
            return ValidateSliceGenericMethod(node: node, sliceType: objectType);
        }

        // Handle other generic method calls
        return ValidateGenericMethodCall(node: node, objectType: objectType);
    }

    public object? VisitGenericMemberExpression(GenericMemberExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot access member on null object", location: node.Location);
            return null;
        }

        // TODO: Implement generic member access validation
        return new TypeInfo(Name: "unknown", IsReference: false);
    }

    public object? VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot perform memory operation on null object",
                location: node.Location);
            return null;
        }

        // Check if this is a slice operation
        if (objectType.Name == "HeapSlice" || objectType.Name == "StackSlice")
        {
            return ValidateSliceMemoryOperation(node: node, sliceType: objectType);
        }

        // Handle other memory operations through memory analyzer
        MemoryOperation? memOp = GetMemoryOperation(operationName: node.OperationName);
        if (memOp != null)
        {
            MemoryObject? memoryObject =
                _memoryAnalyzer.GetMemoryObject(name: node.Object.ToString() ?? "");
            if (memoryObject != null)
            {
                MemoryOperationResult result = _memoryAnalyzer.ValidateMemoryOperation(
                    memoryObject: memoryObject, operation: memOp.Value, location: node.Location);
                if (!result.IsSuccess)
                {
                    foreach (MemoryError error in result.Errors)
                    {
                        AddError(message: error.Message, location: error.Location);
                    }
                }

                return CreateWrapperTypeInfo(baseType: memoryObject.BaseType,
                    wrapper: result.NewWrapperType);
            }
        }

        return objectType;
    }

    public object? VisitDangerStatement(DangerStatement node)
    {
        // Save current danger mode state
        bool previousDangerMode = _isInDangerMode;

        try
        {
            // Enable danger mode for this block
            _isInDangerMode = true;

            // Create new scope for variables declared in danger block
            _symbolTable.EnterScope();

            // Process the danger block body with elevated permissions
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the danger block scope
            _symbolTable.ExitScope();

            // Restore previous danger mode
            _isInDangerMode = previousDangerMode;
        }

        return null;
    }

    public object? VisitMayhemStatement(MayhemStatement node)
    {
        // Save current mayhem mode state
        bool previousMayhemMode = _isInMayhemMode;

        try
        {
            // Enable mayhem mode for this block
            _isInMayhemMode = true;

            // Create new scope for variables declared in mayhem block
            _symbolTable.EnterScope();

            // Process the mayhem block body with maximum permissions
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the mayhem block scope
            _symbolTable.ExitScope();

            // Restore previous mayhem mode
            _isInMayhemMode = previousMayhemMode;
        }

        return null;
    }

    public object? VisitExternalDeclaration(ExternalDeclaration node)
    {
        // Create function symbol for external declaration
        List<Parameter> parameters = node.Parameters;
        TypeInfo? returnType = node.ReturnType != null
            ? new TypeInfo(Name: node.ReturnType.Name, IsReference: false)
            : null;

        var functionSymbol = new FunctionSymbol(Name: node.Name, Parameters: parameters,
            ReturnType: returnType, Visibility: VisibilityModifier.External, IsUsurping: false,
            GenericParameters: node.GenericParameters?.ToList());

        if (!_symbolTable.TryDeclare(symbol: functionSymbol))
        {
            AddError(message: $"External function '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    private object? ValidateSliceGenericMethod(GenericMethodCallExpression node,
        TypeInfo sliceType)
    {
        string methodName = node.MethodName;
        List<TypeExpression> typeArgs = node.TypeArguments;
        List<Expression> args = node.Arguments;

        // Validate type arguments
        if (typeArgs.Count != 1)
        {
            AddError(message: $"Slice method '{methodName}' requires exactly one type argument",
                location: node.Location);
            return null;
        }

        TypeExpression targetType = typeArgs[index: 0];

        switch (methodName)
        {
            case "read":
                // read<T>!(offset: sysuint) -> T
                if (args.Count != 1)
                {
                    AddError(message: "read<T>! requires exactly one argument (offset)",
                        location: node.Location);
                    return null;
                }

                var offsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                if (offsetType?.Name != "sysuint" && !IsIntegerType(typeName: offsetType?.Name))
                {
                    AddError(message: "read<T>! offset must be of type sysuint",
                        location: node.Location);
                }

                return new TypeInfo(Name: targetType.Name, IsReference: false);

            case "write":
                // write<T>!(offset: sysuint, value: T)
                if (args.Count != 2)
                {
                    AddError(message: "write<T>! requires exactly two arguments (offset, value)",
                        location: node.Location);
                    return null;
                }

                var writeOffsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                var valueType = args[index: 1]
                   .Accept(visitor: this) as TypeInfo;

                if (writeOffsetType?.Name != "sysuint" &&
                    !IsIntegerType(typeName: writeOffsetType?.Name))
                {
                    AddError(message: "write<T>! offset must be of type sysuint",
                        location: node.Location);
                }

                if (valueType?.Name != targetType.Name &&
                    !IsCompatibleType(sourceType: valueType?.Name, targetType: targetType.Name))
                {
                    AddError(message: $"write<T>! value must be of type {targetType.Name}",
                        location: node.Location);
                }

                return new TypeInfo(Name: "void", IsReference: false);

            default:
                AddError(message: $"Unknown slice generic method: {methodName}",
                    location: node.Location);
                return null;
        }
    }

    private object? ValidateSliceMemoryOperation(MemoryOperationExpression node,
        TypeInfo sliceType)
    {
        string operationName = node.OperationName;
        List<Expression> args = node.Arguments;

        switch (operationName)
        {
            case "size":
                if (args.Count != 0)
                {
                    AddError(message: "size! operation takes no arguments",
                        location: node.Location);
                }

                return new TypeInfo(Name: "sysuint", IsReference: false);

            case "address":
                if (args.Count != 0)
                {
                    AddError(message: "address! operation takes no arguments",
                        location: node.Location);
                }

                return new TypeInfo(Name: "sysuint", IsReference: false);

            case "is_valid":
                if (args.Count != 0)
                {
                    AddError(message: "is_valid! operation takes no arguments",
                        location: node.Location);
                }

                return new TypeInfo(Name: "bool", IsReference: false);

            case "unsafe_ptr":
                if (args.Count != 1)
                {
                    AddError(message: "unsafe_ptr! requires exactly one argument (offset)",
                        location: node.Location);
                    return null;
                }

                var offsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                if (offsetType?.Name != "sysuint")
                {
                    AddError(message: "unsafe_ptr! offset must be of type sysuint",
                        location: node.Location);
                }

                return new TypeInfo(Name: "sysuint", IsReference: false);

            case "slice":
                if (args.Count != 2)
                {
                    AddError(message: "slice! requires exactly two arguments (offset, bytes)",
                        location: node.Location);
                    return null;
                }

                return new TypeInfo(Name: sliceType.Name,
                    IsReference: true); // Returns same slice type

            case "hijack":
            case "refer":
                // Memory model operations - delegate to memory analyzer
                return HandleMemoryModelOperation(node: node, sliceType: sliceType);

            default:
                AddError(message: $"Unknown slice operation: {operationName}",
                    location: node.Location);
                return null;
        }
    }

    private object? ValidateGenericMethodCall(GenericMethodCallExpression node,
        TypeInfo objectType)
    {
        // TODO: Implement validation for other generic method calls
        return new TypeInfo(Name: "unknown", IsReference: false);
    }

    private object? HandleMemoryModelOperation(MemoryOperationExpression node, TypeInfo sliceType)
    {
        // Integrate with memory analyzer for wrapper type operations
        MemoryOperation? memOp = GetMemoryOperation(operationName: node.OperationName);
        if (memOp != null)
        {
            // Create a temporary memory object for validation
            var memoryObject = new MemoryObject(Name: node.Object.ToString() ?? "slice",
                BaseType: sliceType, Wrapper: WrapperType.Owned, State: ObjectState.Valid,
                ReferenceCount: 1, Location: node.Location);

            MemoryOperationResult result = _memoryAnalyzer.ValidateMemoryOperation(
                memoryObject: memoryObject, operation: memOp.Value, location: node.Location);
            if (!result.IsSuccess)
            {
                foreach (MemoryError error in result.Errors)
                {
                    AddError(message: error.Message, location: error.Location);
                }
            }

            return CreateWrapperTypeInfo(baseType: sliceType, wrapper: result.NewWrapperType);
        }

        return sliceType;
    }

    private MemoryOperation? GetMemoryOperation(string operationName)
    {
        return ParseMemoryOperation(operationName: operationName);
    }

    private MemoryOperation? GetMemoryOperation(string operationName, SourceLocation location)
    {
        return GetMemoryOperation(operationName: operationName);
    }

    private object? ValidateSliceGenericMethod(GenericMethodCallExpression node,
        TypeInfo sliceType, SourceLocation location)
    {
        return ValidateSliceGenericMethod(node: node, sliceType: sliceType);
    }

    private object? ValidateGenericMethodCall(GenericMethodCallExpression node,
        TypeInfo objectType, SourceLocation location)
    {
        return ValidateGenericMethodCall(node: node, objectType: objectType);
    }

    private object? ValidateSliceMemoryOperation(MemoryOperationExpression node,
        TypeInfo sliceType, SourceLocation location)
    {
        return ValidateSliceMemoryOperation(node: node, sliceType: sliceType);
    }

    private object? HandleMemoryModelOperation(MemoryOperationExpression node, TypeInfo sliceType,
        SourceLocation location)
    {
        return HandleMemoryModelOperation(node: node, sliceType: sliceType);
    }

    private TypeInfo? HandleMemoryOperationCall(MemberExpression memberExpr, string operationName,
        List<Expression> arguments, SourceLocation location, SourceLocation nodeLocation)
    {
        return HandleMemoryOperationCall(memberExpr: memberExpr, operationName: operationName,
            arguments: arguments, location: location);
    }

    private TypeInfo CreateWrapperTypeInfo(TypeInfo baseType, WrapperType wrapper,
        SourceLocation location)
    {
        return CreateWrapperTypeInfo(baseType: baseType, wrapper: wrapper);
    }

    private bool IsIntegerType(string? typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "syssint" => true,
            "u8" or "u16" or "u32" or "u64" or "u128" or "sysuint" => true,
            _ => false
        };
    }

    private bool IsCompatibleType(string? sourceType, string? targetType)
    {
        if (sourceType == targetType)
        {
            return true;
        }

        // Allow integer type coercion
        if (IsIntegerType(typeName: sourceType) && IsIntegerType(typeName: targetType))
        {
            return true;
        }

        return false;
    }

    private bool IsDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "write_as" or "read_as" or "volatile_write" or "volatile_read" or "addr_of"
                or "invalidate" => true,
            _ => false
        };
    }

    private object? ValidateDangerZoneFunction(GenericMethodCallExpression node,
        string functionName)
    {
        // These functions are only available in danger blocks
        if (!_isInDangerMode)
        {
            AddError(message: $"Function '{functionName}' is only available within danger! blocks",
                location: node.Location);
            return null;
        }

        List<Expression> args = node.Arguments;
        List<TypeExpression> typeArgs = node.TypeArguments;

        return functionName switch
        {
            "write_as" => ValidateWriteAs(args: args, typeArgs: typeArgs, location: node.Location),
            "read_as" => ValidateReadAs(args: args, typeArgs: typeArgs, location: node.Location),
            "volatile_write" => ValidateVolatileWrite(args: args, typeArgs: typeArgs,
                location: node.Location),
            "volatile_read" => ValidateVolatileRead(args: args, typeArgs: typeArgs,
                location: node.Location),
            "addr_of" => ValidateAddrOf(args: args, location: node.Location),
            "invalidate" => ValidateInvalidate(args: args, location: node.Location),
            _ => throw new InvalidOperationException(
                message: $"Unknown danger zone function: {functionName}")
        };
    }

    private object? ValidateWriteAs(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        if (typeArgs.Count != 1)
        {
            AddError(message: "write_as<T>! requires exactly one type argument",
                location: location);
            return null;
        }

        if (args.Count != 2)
        {
            AddError(message: "write_as<T>! requires exactly two arguments (address, value)",
                location: location);
            return null;
        }

        string targetType = typeArgs[index: 0].Name;
        var addressType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;
        var valueType = args[index: 1]
           .Accept(visitor: this) as TypeInfo;

        // Address should be integer type (convertible to pointer)
        if (!IsIntegerType(typeName: addressType?.Name))
        {
            AddError(message: "write_as<T>! address must be an integer type", location: location);
        }

        // Value should be compatible with target type
        if (valueType?.Name != targetType &&
            !IsCompatibleType(sourceType: valueType?.Name, targetType: targetType))
        {
            AddError(message: $"write_as<T>! value must be of type {targetType}",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private object? ValidateReadAs(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        if (typeArgs.Count != 1)
        {
            AddError(message: "read_as<T>! requires exactly one type argument",
                location: location);
            return null;
        }

        if (args.Count != 1)
        {
            AddError(message: "read_as<T>! requires exactly one argument (address)",
                location: location);
            return null;
        }

        string targetType = typeArgs[index: 0].Name;
        var addressType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;

        // Address should be integer type (convertible to pointer)
        if (!IsIntegerType(typeName: addressType?.Name))
        {
            AddError(message: "read_as<T>! address must be an integer type", location: location);
        }

        return new TypeInfo(Name: targetType, IsReference: false);
    }

    private object? ValidateVolatileWrite(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        // Same validation as write_as but for volatile operations
        return ValidateWriteAs(args: args, typeArgs: typeArgs, location: location);
    }

    private object? ValidateVolatileRead(List<Expression> args, List<TypeExpression> typeArgs,
        SourceLocation location)
    {
        // Same validation as read_as but for volatile operations
        return ValidateReadAs(args: args, typeArgs: typeArgs, location: location);
    }

    private object? ValidateAddrOf(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "addr_of! requires exactly one argument (variable)",
                location: location);
            return null;
        }

        // The argument should be a variable reference
        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;
        if (argType == null)
        {
            AddError(message: "addr_of! argument must be a valid variable", location: location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo(Name: "sysuint", IsReference: false);
    }

    private object? ValidateInvalidate(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "invalidate! requires exactly one argument (slice or pointer)",
                location: location);
            return null;
        }

        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;

        // Should be a slice type or pointer
        if (argType?.Name != "HeapSlice" && argType?.Name != "StackSlice" &&
            argType?.Name != "ptr")
        {
            AddError(message: "invalidate! argument must be a slice or pointer",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private bool IsNonGenericDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "addr_of" or "invalidate" => true,
            _ => false
        };
    }

    private object? ValidateNonGenericDangerZoneFunction(CallExpression node, string functionName)
    {
        // These functions are only available in danger blocks
        if (!_isInDangerMode)
        {
            AddError(message: $"Function '{functionName}' is only available within danger! blocks",
                location: node.Location);
            return null;
        }

        return functionName switch
        {
            "addr_of" => ValidateAddrOfFunction(args: node.Arguments, location: node.Location),
            "invalidate" => ValidateInvalidateFunction(args: node.Arguments,
                location: node.Location),
            _ => throw new InvalidOperationException(
                message: $"Unknown non-generic danger zone function: {functionName}")
        };
    }

    private object? ValidateAddrOfFunction(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "addr_of! requires exactly one argument (variable)",
                location: location);
            return null;
        }

        // The argument should be a variable reference (IdentifierExpression)
        Expression arg = args[index: 0];
        if (arg is not IdentifierExpression)
        {
            AddError(message: "addr_of! argument must be a variable identifier",
                location: location);
            return null;
        }

        // Validate that the variable exists
        var argType = arg.Accept(visitor: this) as TypeInfo;
        if (argType == null)
        {
            AddError(message: "addr_of! argument must be a valid variable", location: location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo(Name: "sysuint", IsReference: false);
    }

    private object? ValidateInvalidateFunction(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError(message: "invalidate! requires exactly one argument (slice or pointer)",
                location: location);
            return null;
        }

        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;

        // Should be a slice type or pointer
        if (argType?.Name != "HeapSlice" && argType?.Name != "StackSlice" &&
            argType?.Name != "ptr")
        {
            AddError(message: "invalidate! argument must be a slice or pointer",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private bool IsInDangerBlock()
    {
        // TODO: Implement danger block tracking
        // This would require tracking when we enter/exit danger! blocks during parsing
        return false; // For now, always return false to enforce the check
    }

    private bool IsEntityType(TypeExpression type)
    {
        // TODO: Implement proper type checking
        // This would require looking up the type in the symbol table
        // and checking if it's declared as an entity

        // Common entity type patterns
        return type.Name.EndsWith(value: "Entity") || type.Name.EndsWith(value: "Service") ||
               type.Name.EndsWith(value: "Controller");
    }
}
