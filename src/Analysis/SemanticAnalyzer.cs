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
        _memoryAnalyzer = new MemoryAnalyzer(language, mode);
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
        var heapSliceType = new TypeInfo("HeapSlice", false);
        var heapSliceSymbol = new StructSymbol("HeapSlice", VisibilityModifier.Public);
        _symbolTable.TryDeclare(heapSliceSymbol);

        // Register StackSlice record type
        var stackSliceType = new TypeInfo("StackSlice", false);
        var stackSliceSymbol = new StructSymbol("StackSlice", VisibilityModifier.Public);
        _symbolTable.TryDeclare(stackSliceSymbol);

        // Register primitive types
        RegisterPrimitiveType("sysuint");
        RegisterPrimitiveType("syssint");
        RegisterPrimitiveType("u8");
        RegisterPrimitiveType("u16");
        RegisterPrimitiveType("u32");
        RegisterPrimitiveType("u64");
        RegisterPrimitiveType("u128");
        RegisterPrimitiveType("s8");
        RegisterPrimitiveType("s16");
        RegisterPrimitiveType("s32");
        RegisterPrimitiveType("s64");
        RegisterPrimitiveType("s128");
        RegisterPrimitiveType("f16");
        RegisterPrimitiveType("f32");
        RegisterPrimitiveType("f64");
        RegisterPrimitiveType("f128");
        RegisterPrimitiveType("d32");
        RegisterPrimitiveType("d64");
        RegisterPrimitiveType("d128");
        RegisterPrimitiveType("bool");
        RegisterPrimitiveType("letter");
        RegisterPrimitiveType("letter8");
        RegisterPrimitiveType("letter16");
        RegisterPrimitiveType("text");
        RegisterPrimitiveType("text8");
        RegisterPrimitiveType("text16");
    }

    /// <summary>
    /// Helper method to register a primitive type.
    /// </summary>
    private void RegisterPrimitiveType(string typeName)
    {
        var typeInfo = new TypeInfo(typeName, false);
        var typeSymbol = new TypeSymbol(typeName, typeInfo, VisibilityModifier.Public);
        _symbolTable.TryDeclare(typeSymbol);
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
            var allErrors = new List<SemanticError>(_errors);
            // Convert memory safety violations to semantic errors for unified reporting
            allErrors.AddRange(_memoryAnalyzer.Errors.Select(me =>
                new SemanticError(me.Message, me.Location)));
            return allErrors;
        }
    }
    
    public List<SemanticError> Analyze(AST.Program program)
    {
        program.Accept(this);
        return Errors;
    }
    
    // Program
    public object? VisitProgram(AST.Program node)
    {
        foreach (var declaration in node.Declarations)
        {
            declaration.Accept(this);
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
            var initType = node.Initializer.Accept(this) as TypeInfo;
            
            // Validate type compatibility when explicit type is declared
            if (node.Type != null)
            {
                var declaredType = ResolveType(node.Type);
                if (declaredType != null && !IsAssignable(declaredType, initType))
                {
                    AddError($"Cannot assign {initType?.Name ?? "unknown"} to {declaredType.Name}", node.Location);
                }
            }
            
            // CRITICAL: Register object in memory analyzer for ownership tracking
            // This is where objects enter the memory model and become subject to safety rules
            var type = ResolveType(node.Type) ?? initType ?? new TypeInfo("Unknown", false);
            _memoryAnalyzer.RegisterObject(node.Name, type, node.Location);
        }
        
        // Add variable symbol to symbol table for name resolution
        // Use inferred type from initializer if no explicit type is specified
        TypeInfo? variableType = null;
        if (node.Type != null)
        {
            variableType = ResolveType(node.Type);
        }
        else if (node.Initializer != null)
        {
            // Infer type from initializer for auto variables
            variableType = node.Initializer.Accept(this) as TypeInfo;
        }

        var symbol = new VariableSymbol(node.Name, variableType, node.IsMutable, node.Visibility);
        if (!_symbolTable.TryDeclare(symbol))
        {
            AddError($"Variable '{node.Name}' is already declared in current scope", node.Location);
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
        bool isUsurping = node.Name.Contains("usurping") || CheckIfUsurpingFunction(node);
        
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
            foreach (var param in node.Parameters)
            {
                var paramType = ResolveType(param.Type);
                var paramSymbol = new VariableSymbol(param.Name, paramType, false, VisibilityModifier.Private);
                _symbolTable.TryDeclare(paramSymbol);
                
                // Register parameter objects in memory analyzer for ownership tracking
                // Parameters enter the function with appropriate wrapper types based on language
                if (paramType != null)
                {
                    _memoryAnalyzer.RegisterObject(param.Name, paramType, node.Location);
                }
            }
            
            // CRITICAL: Validate return type against usurping function rules
            // Only usurping functions can return Hijacked<T> (exclusive tokens)
            if (node.ReturnType != null)
            {
                var funcReturnType = ResolveType(node.ReturnType);
                if (funcReturnType != null)
                {
                    _memoryAnalyzer.ValidateFunctionReturn(funcReturnType, node.Location);
                }
            }
            
            // Analyze function body with full memory safety checking
            if (node.Body != null)
            {
                node.Body.Accept(this);
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
        var returnType = ResolveType(node.ReturnType);
        var funcSymbol = new FunctionSymbol(node.Name, node.Parameters, returnType, node.Visibility, isUsurping);
        if (!_symbolTable.TryDeclare(funcSymbol))
        {
            AddError($"Function '{node.Name}' is already declared", node.Location);
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
            foreach (var member in node.Members)
            {
                member.Accept(this);
            }
        }
        finally
        {
            _symbolTable.ExitScope();
        }
        
        // Add entity to symbol table
        var classSymbol = new ClassSymbol(node.Name, node.BaseClass, node.Interfaces, node.Visibility);
        if (!_symbolTable.TryDeclare(classSymbol))
        {
            AddError($"Entity '{node.Name}' is already declared", node.Location);
        }
        
        return null;
    }
    
    public object? VisitStructDeclaration(StructDeclaration node)
    {
        // Similar to entity but with value semantics
        var structSymbol = new StructSymbol(node.Name, node.Visibility);
        if (!_symbolTable.TryDeclare(structSymbol))
        {
            AddError($"Record '{node.Name}' is already declared", node.Location);
        }
        return null;
    }
    
    public object? VisitMenuDeclaration(MenuDeclaration node)
    {
        var menuSymbol = new MenuSymbol(node.Name, node.Visibility);
        if (!_symbolTable.TryDeclare(menuSymbol))
        {
            AddError($"Option '{node.Name}' is already declared", node.Location);
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
                    AddError($"{node.Kind} '{node.Name}' must be declared inside a danger! block", node.Location);
                }
                break;

            case VariantKind.Variant:
                // Validate that all fields in all cases are records (value types)
                foreach (var variantCase in node.Cases)
                {
                    if (variantCase.AssociatedTypes != null)
                    {
                        foreach (var type in variantCase.AssociatedTypes)
                        {
                            // Check if type is an entity (reference type)
                            if (IsEntityType(type))
                            {
                                AddError($"Variant '{node.Name}' case '{variantCase.Name}' contains entity type '{type}'. All variant fields must be records (value types)", node.Location);
                            }
                        }
                    }
                }
                break;
        }

        var variantSymbol = new VariantSymbol(node.Name, node.Visibility);
        if (!_symbolTable.TryDeclare(variantSymbol))
        {
            AddError($"Variant '{node.Name}' is already declared", node.Location);
        }
        return null;
    }
    
    public object? VisitFeatureDeclaration(FeatureDeclaration node)
    {
        var featureSymbol = new FeatureSymbol(node.Name, node.Visibility);
        if (!_symbolTable.TryDeclare(featureSymbol))
        {
            AddError($"Feature '{node.Name}' is already declared", node.Location);
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
        node.Expression.Accept(this);
        return null;
    }

    public object? VisitDeclarationStatement(DeclarationStatement node)
    {
        node.Declaration.Accept(this);
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
        var targetType = node.Target.Accept(this) as TypeInfo;
        var valueType = node.Value.Accept(this) as TypeInfo;
        
        if (targetType != null && valueType != null && !IsAssignable(targetType, valueType))
        {
            AddError($"Cannot assign {valueType.Name} to {targetType.Name}", node.Location);
        }
        
        // CRITICAL: Language-specific memory model handling for assignments
        if (node.Target is IdentifierExpression targetId && node.Value is IdentifierExpression valueId)
        {
            if (_language == Language.Cake)
            {
                // Cake: Automatic reference counting - both variables share the same object
                // Source remains valid, RC is incremented, no invalidation occurs
                _memoryAnalyzer.HandleCakeAssignment(targetId.Name, valueId.Name, node.Location);
            }
            else if (_language == Language.RazorForge)
            {
                // RazorForge: Move semantics - object ownership may transfer
                // TODO: Implement sophisticated move analysis (copy vs move determination)
                // For now, treat as creating new object reference
                if (targetType != null)
                {
                    _memoryAnalyzer.RegisterObject(targetId.Name, targetType, node.Location);
                }
            }
        }
        
        return null;
    }
    
    public object? VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value != null)
        {
            node.Value.Accept(this);
        }
        return null;
    }
    
    public object? VisitIfStatement(IfStatement node)
    {
        // Check condition is boolean
        var conditionType = node.Condition.Accept(this) as TypeInfo;
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError($"If condition must be boolean, got {conditionType.Name}", node.Location);
        }
        
        node.ThenStatement.Accept(this);
        node.ElseStatement?.Accept(this);
        return null;
    }
    
    public object? VisitWhileStatement(WhileStatement node)
    {
        // Check condition is boolean  
        var conditionType = node.Condition.Accept(this) as TypeInfo;
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError($"While condition must be boolean, got {conditionType.Name}", node.Location);
        }
        
        node.Body.Accept(this);
        return null;
    }
    
    public object? VisitForStatement(ForStatement node)
    {
        // Enter new scope for loop variable
        _symbolTable.EnterScope();
        
        try
        {
            // Check iterable type
            var iterableType = node.Iterable.Accept(this) as TypeInfo;
            // TODO: Check if iterable implements Iterable interface
            
            // Add loop variable to scope
            var loopVarSymbol = new VariableSymbol(node.Variable, null, false, VisibilityModifier.Private);
            _symbolTable.TryDeclare(loopVarSymbol);
            
            node.Body.Accept(this);
        }
        finally
        {
            _symbolTable.ExitScope();
        }
        
        return null;
    }
    
    public object? VisitWhenStatement(WhenStatement node)
    {
        var expressionType = node.Expression.Accept(this) as TypeInfo;
        
        foreach (var clause in node.Clauses)
        {
            // Enter new scope for pattern variables
            _symbolTable.EnterScope();
            
            try
            {
                // TODO: Type check pattern against expression
                clause.Body.Accept(this);
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
            foreach (var statement in node.Statements)
            {
                statement.Accept(this);
            }
        }
        finally
        {
            // CRITICAL: Scope cleanup automatically invalidates all objects in this scope
            // This is a fundamental memory safety mechanism preventing use-after-scope
            _symbolTable.ExitScope();
            _memoryAnalyzer.ExitScope();  // Invalidates all objects declared in this scope
        }
        
        return null;
    }
    
    public object? VisitBreakStatement(BreakStatement node) => null;
    public object? VisitContinueStatement(ContinueStatement node) => null;
    
    // Expressions  
    public object? VisitLiteralExpression(LiteralExpression node)
    {
        return InferLiteralType(node.Value);
    }
    
    public object? VisitIdentifierExpression(IdentifierExpression node)
    {
        var symbol = _symbolTable.Lookup(node.Name);
        if (symbol == null)
        {
            AddError($"Undefined identifier '{node.Name}'", node.Location);
            return null;
        }
        
        return symbol.Type;
    }
    
    public object? VisitBinaryExpression(BinaryExpression node)
    {
        var leftType = node.Left.Accept(this) as TypeInfo;
        var rightType = node.Right.Accept(this) as TypeInfo;
        
        // Check for mixed-type arithmetic (REJECTED per user requirement)
        if (leftType != null && rightType != null && IsArithmeticOperator(node.Operator))
        {
            if (!AreTypesCompatible(leftType, rightType))
            {
                AddError($"Mixed-type arithmetic is not allowed. Cannot perform {node.Operator} between {leftType.Name} and {rightType.Name}. Use explicit type conversion with {rightType.Name}!(x) or x.{rightType.Name}!().", node.Location);
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
            BinaryOperator.Add or BinaryOperator.Subtract or BinaryOperator.Multiply or 
            BinaryOperator.TrueDivide or BinaryOperator.Divide or BinaryOperator.Modulo or BinaryOperator.Power or
            BinaryOperator.AddWrap or BinaryOperator.SubtractWrap or BinaryOperator.MultiplyWrap or 
            BinaryOperator.DivideWrap or BinaryOperator.ModuloWrap or BinaryOperator.PowerWrap or
            BinaryOperator.AddSaturate or BinaryOperator.SubtractSaturate or BinaryOperator.MultiplySaturate or 
            BinaryOperator.DivideSaturate or BinaryOperator.ModuloSaturate or BinaryOperator.PowerSaturate or
            BinaryOperator.AddUnchecked or BinaryOperator.SubtractUnchecked or BinaryOperator.MultiplyUnchecked or 
            BinaryOperator.DivideUnchecked or BinaryOperator.ModuloUnchecked or BinaryOperator.PowerUnchecked or
            BinaryOperator.AddChecked or BinaryOperator.SubtractChecked or BinaryOperator.MultiplyChecked or 
            BinaryOperator.DivideChecked or BinaryOperator.ModuloChecked or BinaryOperator.PowerChecked => true,
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
        var operandType = node.Operand.Accept(this) as TypeInfo;
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
            var functionName = identifierExpr.Name;
            if (IsNonGenericDangerZoneFunction(functionName))
            {
                // Only allow these functions in danger mode
                if (!_isInDangerMode)
                {
                    _errors.Add(new SemanticError($"Danger zone function '{functionName}!' can only be used inside danger blocks", node.Location));
                    return new TypeInfo("void", false);
                }
                return ValidateNonGenericDangerZoneFunction(node, functionName);
            }
        }

        // CRITICAL: Detect memory operation method calls (ending with '!')
        // These are the core operations of RazorForge's memory model
        if (node.Callee is MemberExpression memberExpr &&
            IsMemoryOperation(memberExpr.PropertyName))
        {
            // Route through specialized memory operation handler
            return HandleMemoryOperationCall(memberExpr, memberExpr.PropertyName, node.Arguments, node.Location);
        }

        // Standard function call type checking
        var functionType = node.Callee.Accept(this) as TypeInfo;

        // Type check all arguments
        foreach (var arg in node.Arguments)
        {
            arg.Accept(this);
        }

        // TODO: Return function's actual return type based on signature
        return functionType;
    }
    
    public object? VisitMemberExpression(MemberExpression node)
    {
        var objectType = node.Object.Accept(this) as TypeInfo;
        // TODO: Check if member exists on type
        return null;
    }
    
    public object? VisitIndexExpression(IndexExpression node)
    {
        var objectType = node.Object.Accept(this) as TypeInfo;
        var indexType = node.Index.Accept(this) as TypeInfo;
        // TODO: Check indexing compatibility
        return null;
    }
    
    public object? VisitConditionalExpression(ConditionalExpression node)
    {
        var conditionType = node.Condition.Accept(this) as TypeInfo;
        var trueType = node.TrueExpression.Accept(this) as TypeInfo;
        var falseType = node.FalseExpression.Accept(this) as TypeInfo;
        
        if (conditionType != null && conditionType.Name != "Bool")
        {
            AddError($"Conditional expression condition must be boolean", node.Location);
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
            foreach (var param in node.Parameters)
            {
                var paramType = ResolveType(param.Type);
                var paramSymbol = new VariableSymbol(param.Name, paramType, false, VisibilityModifier.Private);
                _symbolTable.TryDeclare(paramSymbol);
            }
            
            return node.Body.Accept(this);
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
        
        foreach (var operand in node.Operands)
        {
            var operandType = operand.Accept(this) as TypeInfo;
            
            if (prevType != null && operandType != null)
            {
                // TODO: Check if types are comparable
                if (!AreComparable(prevType, operandType))
                {
                    AddError($"Cannot compare {prevType.Name} with {operandType.Name}", node.Location);
                }
            }
            
            prevType = operandType;
        }
        
        // Chained comparisons always return boolean
        return new TypeInfo("Bool", false);
    }
    
    public object? VisitRangeExpression(RangeExpression node)
    {
        // Check start and end are numeric
        var startType = node.Start.Accept(this) as TypeInfo;
        var endType = node.End.Accept(this) as TypeInfo;
        
        if (startType != null && !IsNumericType(startType))
        {
            AddError($"Range start must be numeric, got {startType.Name}", node.Location);
        }
        
        if (endType != null && !IsNumericType(endType))
        {
            AddError($"Range end must be numeric, got {endType.Name}", node.Location);
        }
        
        // Check step if present
        if (node.Step != null)
        {
            var stepType = node.Step.Accept(this) as TypeInfo;
            if (stepType != null && !IsNumericType(stepType))
            {
                AddError($"Range step must be numeric, got {stepType.Name}", node.Location);
            }
        }
        
        // Range expressions return a Range<T> type
        return new TypeInfo("Range", false);
    }
    
    public object? VisitTypeExpression(TypeExpression node)
    {
        // Type expressions are handled during semantic analysis
        return null;
    }
    
    public object? VisitTypeConversionExpression(TypeConversionExpression node)
    {
        // Analyze the source expression
        var sourceType = node.Expression.Accept(this) as TypeInfo;
        
        // Validate the target type exists
        var targetTypeName = node.TargetType;
        if (!IsValidType(targetTypeName))
        {
            _errors.Add(new SemanticError($"Unknown type: {targetTypeName}", node.Location));
            return null;
        }
        
        // Check if the conversion is valid
        if (!IsValidConversion(sourceType?.Name ?? "unknown", targetTypeName))
        {
            _errors.Add(new SemanticError($"Cannot convert from {sourceType?.Name ?? "unknown"} to {targetTypeName}", node.Location));
            return null;
        }
        
        // Return the target type
        return new TypeInfo(targetTypeName, IsUnsignedType(targetTypeName));
    }
    
    private bool IsValidType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "syssint" or
            "u8" or "u16" or "u32" or "u64" or "u128" or "sysuint" or
            "f16" or "f32" or "f64" or "f128" or
            "d32" or "d64" or "d128" or
            "bool" or "letter8" or "letter16" or "letter32" => true,
            _ => false
        };
    }
    
    private bool IsValidConversion(string sourceType, string targetType)
    {
        // For now, allow all conversions between numeric types
        // In a production compiler, this would have more sophisticated rules
        return IsNumericType(sourceType) && IsNumericType(targetType);
    }
    
    private bool IsNumericType(string typeName)
    {
        return typeName switch
        {
            "s8" or "s16" or "s32" or "s64" or "s128" or "syssint" or
            "u8" or "u16" or "u32" or "u64" or "u128" or "sysuint" or
            "f16" or "f32" or "f64" or "f128" or
            "d32" or "d64" or "d128" => true,
            _ => false
        };
    }
    
    private bool IsUnsignedType(string typeName)
    {
        return typeName.StartsWith("u");
    }
    
    // Helper methods
    private TypeInfo? ResolveType(TypeExpression? typeExpr)
    {
        if (typeExpr == null) return null;

        // TODO: Proper type resolution
        return new TypeInfo(typeExpr.Name, false);
    }

    private TypeInfo? ResolveType(TypeExpression? typeExpr, SourceLocation location)
    {
        return ResolveType(typeExpr);
    }
    
    private TypeInfo InferLiteralType(object? value)
    {
        return value switch
        {
            bool => new TypeInfo("bool", false),
            int => new TypeInfo("s32", false),
            long => new TypeInfo("s64", false),
            float => new TypeInfo("f32", false),
            double => new TypeInfo("f64", false),
            string => new TypeInfo("text", false),
            null => new TypeInfo("none", false),
            _ => new TypeInfo("unknown", false)
        };
    }

    private TypeInfo InferLiteralType(object? value, SourceLocation location)
    {
        return InferLiteralType(value);
    }
    
    private bool IsAssignable(TypeInfo target, TypeInfo? source)
    {
        if (source == null) return false;
        
        // TODO: Implement proper type compatibility rules
        return target.Name == source.Name;
    }
    
    private bool AreComparable(TypeInfo type1, TypeInfo type2)
    {
        // TODO: Implement proper comparability rules
        // For now, allow comparison between same types and numeric types
        return type1.Name == type2.Name || (IsNumericType(type1) && IsNumericType(type2));
    }
    
    private bool IsNumericType(TypeInfo type)
    {
        return type.Name switch
        {
            "I8" or "I16" or "I32" or "I64" or "I128" or "Isys" or
            "U8" or "U16" or "U32" or "U64" or "U128" or "Usys" or
            "F16" or "F32" or "F64" or "F128" or
            "D32" or "D64" or "D128" or
            "Integer" or "Decimal" => true, // Cake arbitrary precision types
            _ => false
        };
    }
    
    private void AddError(string message, SourceLocation location)
    {
        _errors.Add(new SemanticError(message, location));
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
            "hijack!" or "share!" or "watch!" or "thread_share!" or "thread_watch!" or
            "steal!" or "snatch!" or "release!" or "try_share!" or "try_thread_share!" or
            "reveal!" or "own!" => true,
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
        return node.Name.StartsWith("usurping_") || 
               node.Name.Contains("Usurping");
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
            AddError("Memory operations can only be called on simple identifiers", location);
            return null;
        }
        
        // Parse operation name to memory operation enum
        var operation = ParseMemoryOperation(operationName);
        if (operation == null)
        {
            AddError($"Unknown memory operation: {operationName}", location);
            return null;
        }
        
        // CRITICAL: Delegate to memory analyzer for safety validation and ownership tracking
        // This is where all the memory safety magic happens
        var resultObj = _memoryAnalyzer.HandleMemoryOperation(objId.Name, operation.Value, location);
        if (resultObj == null)
        {
            // Operation failed - error already reported by memory analyzer
            return null;
        }
        
        // Create type information for the result wrapper type
        return CreateWrapperTypeInfo(resultObj.BaseType, resultObj.Wrapper);
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
        return ParseMemoryOperation(operationName);
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
        var typeName = wrapper switch
        {
            // Direct ownership - no wrapper syntax
            WrapperType.Owned => baseType.Name,
            
            // Memory wrapper types with generic syntax
            WrapperType.Hijacked => $"Hijacked<{baseType.Name}>",      // Exclusive access 🔴
            WrapperType.Shared => $"Shared<{baseType.Name}>",          // Shared ownership 🟢
            WrapperType.Watched => $"Watched<{baseType.Name}>",        // Weak observer 🟤
            WrapperType.ThreadShared => $"ThreadShared<{baseType.Name}>",   // Thread-safe shared 🔵
            WrapperType.ThreadWatched => $"ThreadWatched<{baseType.Name}>", // Thread-safe weak 🟣
            WrapperType.Snatched => $"Snatched<{baseType.Name}>",      // Contaminated ownership 💀
            
            _ => baseType.Name
        };
        
        return new TypeInfo(typeName, baseType.IsReference);
    }

    // Memory slice expression visitor methods
    public object? VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        // Validate size expression is compatible with sysuint type
        var sizeType = node.SizeExpression.Accept(this) as TypeInfo;
        if (sizeType == null)
        {
            AddError($"Slice size expression has unknown type", node.Location);
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
                AddError($"Slice size must be of type sysuint or compatible integer type, found {sizeType.Name}", node.Location);
            }
        }

        // Return appropriate slice type
        var sliceTypeName = node.SliceType;
        return new TypeInfo(sliceTypeName, false); // Slice types are value types (structs)
    }

    public object? VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        // Check if this is a standalone global function call (e.g., write_as<T>!, read_as<T>!)
        if (node.Object is IdentifierExpression identifierExpr)
        {
            var functionName = identifierExpr.Name;

            // Handle built-in danger zone operations
            if (IsDangerZoneFunction(functionName))
            {
                return ValidateDangerZoneFunction(node, functionName);
            }
        }

        // Validate object type supports the generic method
        var objectType = node.Object.Accept(this) as TypeInfo;
        if (objectType == null)
        {
            AddError("Cannot call method on null object", node.Location);
            return null;
        }

        // Check if this is a slice operation
        if (objectType.Name == "HeapSlice" || objectType.Name == "StackSlice")
        {
            return ValidateSliceGenericMethod(node, objectType);
        }

        // Handle other generic method calls
        return ValidateGenericMethodCall(node, objectType);
    }

    public object? VisitGenericMemberExpression(GenericMemberExpression node)
    {
        var objectType = node.Object.Accept(this) as TypeInfo;
        if (objectType == null)
        {
            AddError("Cannot access member on null object", node.Location);
            return null;
        }

        // TODO: Implement generic member access validation
        return new TypeInfo("unknown", false);
    }

    public object? VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        var objectType = node.Object.Accept(this) as TypeInfo;
        if (objectType == null)
        {
            AddError("Cannot perform memory operation on null object", node.Location);
            return null;
        }

        // Check if this is a slice operation
        if (objectType.Name == "HeapSlice" || objectType.Name == "StackSlice")
        {
            return ValidateSliceMemoryOperation(node, objectType);
        }

        // Handle other memory operations through memory analyzer
        var memOp = GetMemoryOperation(node.OperationName);
        if (memOp != null)
        {
            var memoryObject = _memoryAnalyzer.GetMemoryObject(node.Object.ToString() ?? "");
            if (memoryObject != null)
            {
                var result = _memoryAnalyzer.ValidateMemoryOperation(memoryObject, memOp.Value, node.Location);
                if (!result.IsSuccess)
                {
                    foreach (var error in result.Errors)
                    {
                        AddError(error.Message, error.Location);
                    }
                }
                return CreateWrapperTypeInfo(memoryObject.BaseType, result.NewWrapperType);
            }
        }

        return objectType;
    }

    public object? VisitDangerStatement(DangerStatement node)
    {
        // Save current danger mode state
        var previousDangerMode = _isInDangerMode;

        try
        {
            // Enable danger mode for this block
            _isInDangerMode = true;

            // Create new scope for variables declared in danger block
            _symbolTable.EnterScope();

            // Process the danger block body with elevated permissions
            node.Body.Accept(this);
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
        var previousMayhemMode = _isInMayhemMode;

        try
        {
            // Enable mayhem mode for this block
            _isInMayhemMode = true;

            // Create new scope for variables declared in mayhem block
            _symbolTable.EnterScope();

            // Process the mayhem block body with maximum permissions
            node.Body.Accept(this);
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
        var parameters = node.Parameters;
        var returnType = node.ReturnType != null ? new TypeInfo(node.ReturnType.Name, false) : null;

        var functionSymbol = new FunctionSymbol(
            node.Name,
            parameters,
            returnType,
            VisibilityModifier.External,
            false,
            node.GenericParameters?.ToList()
        );

        if (!_symbolTable.TryDeclare(functionSymbol))
        {
            AddError($"External function '{node.Name}' is already declared", node.Location);
        }

        return null;
    }

    private object? ValidateSliceGenericMethod(GenericMethodCallExpression node, TypeInfo sliceType)
    {
        var methodName = node.MethodName;
        var typeArgs = node.TypeArguments;
        var args = node.Arguments;

        // Validate type arguments
        if (typeArgs.Count != 1)
        {
            AddError($"Slice method '{methodName}' requires exactly one type argument", node.Location);
            return null;
        }

        var targetType = typeArgs[0];

        switch (methodName)
        {
            case "read":
                // read<T>!(offset: sysuint) -> T
                if (args.Count != 1)
                {
                    AddError("read<T>! requires exactly one argument (offset)", node.Location);
                    return null;
                }
                var offsetType = args[0].Accept(this) as TypeInfo;
                if (offsetType?.Name != "sysuint" && !IsIntegerType(offsetType?.Name))
                {
                    AddError("read<T>! offset must be of type sysuint", node.Location);
                }
                return new TypeInfo(targetType.Name, false);

            case "write":
                // write<T>!(offset: sysuint, value: T)
                if (args.Count != 2)
                {
                    AddError("write<T>! requires exactly two arguments (offset, value)", node.Location);
                    return null;
                }
                var writeOffsetType = args[0].Accept(this) as TypeInfo;
                var valueType = args[1].Accept(this) as TypeInfo;

                if (writeOffsetType?.Name != "sysuint" && !IsIntegerType(writeOffsetType?.Name))
                {
                    AddError("write<T>! offset must be of type sysuint", node.Location);
                }
                if (valueType?.Name != targetType.Name && !IsCompatibleType(valueType?.Name, targetType.Name))
                {
                    AddError($"write<T>! value must be of type {targetType.Name}", node.Location);
                }
                return new TypeInfo("void", false);

            default:
                AddError($"Unknown slice generic method: {methodName}", node.Location);
                return null;
        }
    }

    private object? ValidateSliceMemoryOperation(MemoryOperationExpression node, TypeInfo sliceType)
    {
        var operationName = node.OperationName;
        var args = node.Arguments;

        switch (operationName)
        {
            case "size":
                if (args.Count != 0)
                {
                    AddError("size! operation takes no arguments", node.Location);
                }
                return new TypeInfo("sysuint", false);

            case "address":
                if (args.Count != 0)
                {
                    AddError("address! operation takes no arguments", node.Location);
                }
                return new TypeInfo("sysuint", false);

            case "is_valid":
                if (args.Count != 0)
                {
                    AddError("is_valid! operation takes no arguments", node.Location);
                }
                return new TypeInfo("bool", false);

            case "unsafe_ptr":
                if (args.Count != 1)
                {
                    AddError("unsafe_ptr! requires exactly one argument (offset)", node.Location);
                    return null;
                }
                var offsetType = args[0].Accept(this) as TypeInfo;
                if (offsetType?.Name != "sysuint")
                {
                    AddError("unsafe_ptr! offset must be of type sysuint", node.Location);
                }
                return new TypeInfo("sysuint", false);

            case "slice":
                if (args.Count != 2)
                {
                    AddError("slice! requires exactly two arguments (offset, bytes)", node.Location);
                    return null;
                }
                return new TypeInfo(sliceType.Name, true); // Returns same slice type

            case "hijack":
            case "refer":
                // Memory model operations - delegate to memory analyzer
                return HandleMemoryModelOperation(node, sliceType);

            default:
                AddError($"Unknown slice operation: {operationName}", node.Location);
                return null;
        }
    }

    private object? ValidateGenericMethodCall(GenericMethodCallExpression node, TypeInfo objectType)
    {
        // TODO: Implement validation for other generic method calls
        return new TypeInfo("unknown", false);
    }

    private object? HandleMemoryModelOperation(MemoryOperationExpression node, TypeInfo sliceType)
    {
        // Integrate with memory analyzer for wrapper type operations
        var memOp = GetMemoryOperation(node.OperationName);
        if (memOp != null)
        {
            // Create a temporary memory object for validation
            var memoryObject = new MemoryObject(
                node.Object.ToString() ?? "slice",
                sliceType,
                WrapperType.Owned,
                ObjectState.Valid,
                1,
                node.Location
            );

            var result = _memoryAnalyzer.ValidateMemoryOperation(memoryObject, memOp.Value, node.Location);
            if (!result.IsSuccess)
            {
                foreach (var error in result.Errors)
                {
                    AddError(error.Message, error.Location);
                }
            }
            return CreateWrapperTypeInfo(sliceType, result.NewWrapperType);
        }

        return sliceType;
    }

    private MemoryOperation? GetMemoryOperation(string operationName)
    {
        return ParseMemoryOperation(operationName);
    }

    private MemoryOperation? GetMemoryOperation(string operationName, SourceLocation location)
    {
        return GetMemoryOperation(operationName);
    }

    private object? ValidateSliceGenericMethod(GenericMethodCallExpression node, TypeInfo sliceType, SourceLocation location)
    {
        return ValidateSliceGenericMethod(node, sliceType);
    }

    private object? ValidateGenericMethodCall(GenericMethodCallExpression node, TypeInfo objectType, SourceLocation location)
    {
        return ValidateGenericMethodCall(node, objectType);
    }

    private object? ValidateSliceMemoryOperation(MemoryOperationExpression node, TypeInfo sliceType, SourceLocation location)
    {
        return ValidateSliceMemoryOperation(node, sliceType);
    }

    private object? HandleMemoryModelOperation(MemoryOperationExpression node, TypeInfo sliceType, SourceLocation location)
    {
        return HandleMemoryModelOperation(node, sliceType);
    }

    private TypeInfo? HandleMemoryOperationCall(MemberExpression memberExpr, string operationName, List<Expression> arguments, SourceLocation location, SourceLocation nodeLocation)
    {
        return HandleMemoryOperationCall(memberExpr, operationName, arguments, location);
    }

    private TypeInfo CreateWrapperTypeInfo(TypeInfo baseType, WrapperType wrapper, SourceLocation location)
    {
        return CreateWrapperTypeInfo(baseType, wrapper);
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
        if (sourceType == targetType) return true;

        // Allow integer type coercion
        if (IsIntegerType(sourceType) && IsIntegerType(targetType))
            return true;

        return false;
    }

    private bool IsDangerZoneFunction(string functionName)
    {
        return functionName switch
        {
            "write_as" or "read_as" or "volatile_write" or "volatile_read" or "addr_of" or "invalidate" => true,
            _ => false
        };
    }

    private object? ValidateDangerZoneFunction(GenericMethodCallExpression node, string functionName)
    {
        // These functions are only available in danger blocks
        if (!_isInDangerMode)
        {
            AddError($"Function '{functionName}' is only available within danger! blocks", node.Location);
            return null;
        }

        var args = node.Arguments;
        var typeArgs = node.TypeArguments;

        return functionName switch
        {
            "write_as" => ValidateWriteAs(args, typeArgs, node.Location),
            "read_as" => ValidateReadAs(args, typeArgs, node.Location),
            "volatile_write" => ValidateVolatileWrite(args, typeArgs, node.Location),
            "volatile_read" => ValidateVolatileRead(args, typeArgs, node.Location),
            "addr_of" => ValidateAddrOf(args, node.Location),
            "invalidate" => ValidateInvalidate(args, node.Location),
            _ => throw new InvalidOperationException($"Unknown danger zone function: {functionName}")
        };
    }

    private object? ValidateWriteAs(List<Expression> args, List<TypeExpression> typeArgs, SourceLocation location)
    {
        if (typeArgs.Count != 1)
        {
            AddError("write_as<T>! requires exactly one type argument", location);
            return null;
        }

        if (args.Count != 2)
        {
            AddError("write_as<T>! requires exactly two arguments (address, value)", location);
            return null;
        }

        var targetType = typeArgs[0].Name;
        var addressType = args[0].Accept(this) as TypeInfo;
        var valueType = args[1].Accept(this) as TypeInfo;

        // Address should be integer type (convertible to pointer)
        if (!IsIntegerType(addressType?.Name))
        {
            AddError("write_as<T>! address must be an integer type", location);
        }

        // Value should be compatible with target type
        if (valueType?.Name != targetType && !IsCompatibleType(valueType?.Name, targetType))
        {
            AddError($"write_as<T>! value must be of type {targetType}", location);
        }

        return new TypeInfo("void", false);
    }

    private object? ValidateReadAs(List<Expression> args, List<TypeExpression> typeArgs, SourceLocation location)
    {
        if (typeArgs.Count != 1)
        {
            AddError("read_as<T>! requires exactly one type argument", location);
            return null;
        }

        if (args.Count != 1)
        {
            AddError("read_as<T>! requires exactly one argument (address)", location);
            return null;
        }

        var targetType = typeArgs[0].Name;
        var addressType = args[0].Accept(this) as TypeInfo;

        // Address should be integer type (convertible to pointer)
        if (!IsIntegerType(addressType?.Name))
        {
            AddError("read_as<T>! address must be an integer type", location);
        }

        return new TypeInfo(targetType, false);
    }

    private object? ValidateVolatileWrite(List<Expression> args, List<TypeExpression> typeArgs, SourceLocation location)
    {
        // Same validation as write_as but for volatile operations
        return ValidateWriteAs(args, typeArgs, location);
    }

    private object? ValidateVolatileRead(List<Expression> args, List<TypeExpression> typeArgs, SourceLocation location)
    {
        // Same validation as read_as but for volatile operations
        return ValidateReadAs(args, typeArgs, location);
    }

    private object? ValidateAddrOf(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError("addr_of! requires exactly one argument (variable)", location);
            return null;
        }

        // The argument should be a variable reference
        var argType = args[0].Accept(this) as TypeInfo;
        if (argType == null)
        {
            AddError("addr_of! argument must be a valid variable", location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo("sysuint", false);
    }

    private object? ValidateInvalidate(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError("invalidate! requires exactly one argument (slice or pointer)", location);
            return null;
        }

        var argType = args[0].Accept(this) as TypeInfo;

        // Should be a slice type or pointer
        if (argType?.Name != "HeapSlice" && argType?.Name != "StackSlice" && argType?.Name != "ptr")
        {
            AddError("invalidate! argument must be a slice or pointer", location);
        }

        return new TypeInfo("void", false);
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
            AddError($"Function '{functionName}' is only available within danger! blocks", node.Location);
            return null;
        }

        return functionName switch
        {
            "addr_of" => ValidateAddrOfFunction(node.Arguments, node.Location),
            "invalidate" => ValidateInvalidateFunction(node.Arguments, node.Location),
            _ => throw new InvalidOperationException($"Unknown non-generic danger zone function: {functionName}")
        };
    }

    private object? ValidateAddrOfFunction(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError("addr_of! requires exactly one argument (variable)", location);
            return null;
        }

        // The argument should be a variable reference (IdentifierExpression)
        var arg = args[0];
        if (arg is not IdentifierExpression)
        {
            AddError("addr_of! argument must be a variable identifier", location);
            return null;
        }

        // Validate that the variable exists
        var argType = arg.Accept(this) as TypeInfo;
        if (argType == null)
        {
            AddError("addr_of! argument must be a valid variable", location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo("sysuint", false);
    }

    private object? ValidateInvalidateFunction(List<Expression> args, SourceLocation location)
    {
        if (args.Count != 1)
        {
            AddError("invalidate! requires exactly one argument (slice or pointer)", location);
            return null;
        }

        var argType = args[0].Accept(this) as TypeInfo;

        // Should be a slice type or pointer
        if (argType?.Name != "HeapSlice" && argType?.Name != "StackSlice" && argType?.Name != "ptr")
        {
            AddError("invalidate! argument must be a slice or pointer", location);
        }

        return new TypeInfo("void", false);
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
        return type.Name.EndsWith("Entity") ||
               type.Name.EndsWith("Service") ||
               type.Name.EndsWith("Controller");
    }
}