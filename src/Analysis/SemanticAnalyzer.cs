using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Comprehensive semantic analyzer for RazorForge and Suflae languages.
///
/// This analyzer performs multi-phase semantic analysis combining:
/// <list type="bullet">
/// <item>Traditional type checking and symbol resolution</item>
/// <item>Advanced memory safety analysis with ownership tracking</item>
/// <item>Language-specific behavior handling (RazorForge vs Suflae)</item>
/// <item>Memory operation validation (retain, share, track, etc.)</item>
/// <item>Cross-language compatibility checking</item>
/// </list>
///
/// The analyzer integrates tightly with the MemoryAnalyzer to enforce
/// RazorForge's explicit memory model and Suflae's automatic RC model.
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

    /// <summary>Target language (RazorForge or Suflae) for language-specific behavior</summary>
    private readonly Language _language;

    /// <summary>Language mode for additional behavior customization</summary>
    private readonly LanguageMode _mode;

    /// <summary>Module resolver for handling imports</summary>
    private readonly ModuleResolver _moduleResolver;

    /// <summary>Source file name for error reporting</summary>
    private readonly string? _fileName;

    /// <summary>Tracks whether we're currently inside a danger block</summary>
    private bool _isInDangerMode = false;

    /// <summary>Tracks whether we're currently inside a mayhem block</summary>
    private bool _isInMayhemMode = false;

    /// <summary>Tracks whether we're currently inside a 'when' expression condition</summary>
    private bool _isInWhenCondition = false;

    /// <summary>Tracks whether we're currently inside a usurping function that can return Hijacked tokens</summary>
    private bool _isInUsurpingFunction = false;

    /// <summary>
    /// Tracks scoped token variables that cannot escape their scope.
    /// Maps token variable name to the scope depth where it was created.
    /// These tokens (Viewed, Hijacked, Seized, Observed) cannot be:
    /// - Assigned to variables outside the scoped statement
    /// - Returned from non-usurping functions
    /// - Passed to functions (unless consumed immediately)
    /// </summary>
    private readonly Dictionary<string, int> _scopedTokens = new();

    /// <summary>Current scope depth for tracking scoped tokens</summary>
    private int _scopeDepth = 0;

    /// <summary>
    /// Tracks source variables that are temporarily invalidated during scoped access.
    /// Maps source variable name to (scope depth, access type).
    /// When a source is invalidated, it cannot be used until the scoped statement exits.
    ///
    /// Examples:
    /// - viewing x as v { ... } - x is invalidated (cannot read or write)
    /// - hijacking x as h { ... } - x is invalidated (cannot read or write)
    /// - seizing shared_x as s { ... } - shared_x is invalidated (lock held)
    /// - observing shared_x as o { ... } - shared_x is invalidated (lock held)
    /// </summary>
    private readonly Dictionary<string, (int scopeDepth, string accessType)> _invalidatedSources = new();

    /// <summary>
    /// Initialize semantic analyzer with integrated memory safety analysis.
    /// Sets up both traditional semantic analysis and memory model enforcement
    /// based on the target language's memory management strategy.
    /// </summary>
    /// <param name="language">Target language (RazorForge or Suflae)</param>
    /// <param name="mode">Language mode for behavior customization</param>
    /// <param name="searchPaths">Optional custom search paths for module resolution</param>
    /// <param name="fileName">Source file name for error reporting</param>
    public SemanticAnalyzer(Language language, LanguageMode mode, List<string>? searchPaths = null, string? fileName = null)
    {
        _symbolTable = new SymbolTable();
        _memoryAnalyzer = new MemoryAnalyzer(language: language, mode: mode);
        _moduleResolver = new ModuleResolver(language, mode, searchPaths);
        _errors = new List<SemanticError>();
        _language = language;
        _mode = mode;
        _fileName = fileName;

        InitializeBuiltInTypes();
    }

    /// <summary>
    /// Initialize built-in types for the RazorForge language.
    /// Registers standard library types like DynamicSlice and TemporarySlice.
    /// </summary>
    private void InitializeBuiltInTypes()
    {
        // Register DynamicSlice record type
        var heapSliceType = new TypeInfo(Name: "DynamicSlice", IsReference: false);
        var heapSliceSymbol =
            new StructSymbol(Name: "DynamicSlice", Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: heapSliceSymbol);

        // Register TemporarySlice record type
        var stackSliceType = new TypeInfo(Name: "TemporarySlice", IsReference: false);
        var stackSliceSymbol =
            new StructSymbol(Name: "TemporarySlice", Visibility: VisibilityModifier.Public);
        _symbolTable.TryDeclare(symbol: stackSliceSymbol);

        // Register primitive types
        RegisterPrimitiveType(typeName: "uaddr");
        RegisterPrimitiveType(typeName: "saddr");
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

    /// <summary>
    /// Performs semantic analysis on the entire program.
    /// Validates all declarations, statements, and expressions in the AST.
    /// </summary>
    /// <param name="program">The program AST to analyze</param>
    /// <returns>List of semantic errors found during analysis</returns>
    public List<SemanticError> Analyze(AST.Program program)
    {
        program.Accept(visitor: this);
        return Errors;
    }

    // Program
    /// <summary>
    /// Visits a program node and analyzes all top-level declarations.
    /// </summary>
    /// <param name="node">Program node containing all declarations</param>
    /// <returns>Null</returns>
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
    /// Suflae: Objects start as Shared with automatic reference counting
    /// </summary>
    public object? VisitVariableDeclaration(VariableDeclaration node)
    {
        // Type check initializer expression if present
        if (node.Initializer != null)
        {
            // CRITICAL: Check for inline-only method calls (.view(), .hijack())
            // These produce temporary tokens that cannot be stored in variables
            if (IsInlineOnlyMethodCall(expr: node.Initializer, out string? methodName))
            {
                AddError(
                    message: $"Cannot store result of '.{methodName}()' in a variable. " +
                             $"Inline tokens must be used directly (e.g., 'obj.{methodName}().field') " +
                             $"or use scoped syntax (e.g., '{(methodName == "view" ? "viewing" : "hijacking")} obj as handle {{ ... }}').",
                    location: node.Location);
            }

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
            _isInUsurpingFunction = true;
        }

        // Enter new lexical scopes for function parameters and body isolation
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();

        try
        {
            // Register generic type parameters if this is a generic function
            if (node.GenericParameters != null && node.GenericParameters.Count > 0)
            {
                foreach (string typeParam in node.GenericParameters)
                {
                    // Register type parameter as a special type symbol
                    var typeParamSymbol = new TypeParameterSymbol(Name: typeParam);
                    _symbolTable.TryDeclare(symbol: typeParamSymbol);
                }
            }

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
                _isInUsurpingFunction = false;
            }
        }

        // Add function to symbol table (with generic parameters if present)
        TypeInfo? returnType = ResolveType(typeExpr: node.ReturnType);
        var funcSymbol = new FunctionSymbol(
            Name: node.Name,
            Parameters: node.Parameters,
            ReturnType: returnType,
            Visibility: node.Visibility,
            IsUsurping: isUsurping,
            GenericParameters: node.GenericParameters?.ToList());
        if (!_symbolTable.TryDeclare(symbol: funcSymbol))
        {
            AddError(message: $"Function '{node.Name}' is already declared",
                location: node.Location);
        }

        return null;
    }

    /// <summary>
    /// Visits a class declaration and registers it in the symbol table.
    /// Validates class members and inheritance relationships.
    /// </summary>
    /// <param name="node">Class declaration node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a struct (record) declaration and registers it in the symbol table.
    /// Validates struct fields and their types.
    /// </summary>
    /// <param name="node">Struct declaration node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a menu declaration and validates its structure.
    /// Menus define user interface navigation and commands.
    /// </summary>
    /// <param name="node">Menu declaration node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a variant (tagged union/enum) declaration and registers it in the symbol table.
    /// Validates variant cases and their associated data types.
    /// </summary>
    /// <param name="node">Variant declaration node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a feature (interface/trait) declaration and registers it in the symbol table.
    /// Features define contracts that types can implement.
    /// </summary>
    /// <param name="node">Feature declaration node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits an implementation declaration that implements a feature for a type.
    /// Validates that all feature requirements are satisfied.
    /// </summary>
    /// <param name="node">Implementation declaration node</param>
    /// <returns>Null</returns>
    public object? VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        // Implementation blocks don't create new symbols but verify interfaces
        return null;
    }

    /// <summary>
    /// Visits an import declaration and handles module imports.
    /// Registers imported symbols in the current scope.
    /// </summary>
    /// <param name="node">Import declaration node</param>
    /// <returns>Null</returns>
    public object? VisitImportDeclaration(ImportDeclaration node)
    {
        try
        {
            // Load the module and all its dependencies
            List<ModuleResolver.ModuleInfo> modules =
                _moduleResolver.LoadModuleWithDependencies(node.ModulePath);

            // Process each loaded module (dependencies first, then the requested module)
            foreach (ModuleResolver.ModuleInfo moduleInfo in modules)
            {
                ProcessImportedModule(moduleInfo, node);
            }

            return null;
        }
        catch (ModuleException ex)
        {
            AddError(
                message: $"Failed to import module '{node.ModulePath}': {ex.Message}",
                location: node.Location);
            return null;
        }
    }

    /// <summary>
    /// Processes an imported module by adding its symbols to the symbol table.
    /// </summary>
    private void ProcessImportedModule(ModuleResolver.ModuleInfo moduleInfo, ImportDeclaration importDecl)
    {
        // Analyze the imported module's AST to extract symbols
        foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
        {
            // Skip import declarations in the imported module (already handled by transitive loading)
            if (declaration is ImportDeclaration)
            {
                continue;
            }

            // Add the declaration's symbols to our symbol table
            if (declaration is FunctionDeclaration funcDecl)
            {
                // Create function symbol (reuse the Parameter objects from AST)
                var funcSymbol = new FunctionSymbol(
                    Name: funcDecl.Name,
                    Parameters: funcDecl.Parameters,
                    ReturnType: funcDecl.ReturnType != null
                        ? ResolveTypeExpression(funcDecl.ReturnType)
                        : new TypeInfo(Name: "void", IsReference: false),
                    Visibility: funcDecl.Visibility,
                    IsUsurping: false,
                    GenericParameters: funcDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>());

                if (!_symbolTable.TryDeclare(funcSymbol))
                {
                    AddError(
                        message: $"Imported symbol '{funcDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is ClassDeclaration classDecl)
            {
                // Create class/entity symbol
                var classSymbol = new ClassSymbol(
                    Name: classDecl.Name,
                    BaseClass: null,
                    Interfaces: new List<TypeExpression>(),
                    Visibility: classDecl.Visibility,
                    GenericParameters: classDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>());

                if (!_symbolTable.TryDeclare(classSymbol))
                {
                    AddError(
                        message: $"Imported type '{classDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is StructDeclaration structDecl)
            {
                // Create struct/record symbol
                var structSymbol = new StructSymbol(
                    Name: structDecl.Name,
                    Visibility: structDecl.Visibility);

                if (!_symbolTable.TryDeclare(structSymbol))
                {
                    AddError(
                        message: $"Imported type '{structDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is VariantDeclaration variantDecl)
            {
                // Create variant symbol (chimera/variant/mutant)
                var variantSymbol = new ClassSymbol(
                    Name: variantDecl.Name,
                    BaseClass: null,
                    Interfaces: new List<TypeExpression>(),
                    Visibility: variantDecl.Visibility,
                    GenericParameters: variantDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>());

                if (!_symbolTable.TryDeclare(variantSymbol))
                {
                    AddError(
                        message: $"Imported type '{variantDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            // TODO: Handle other declaration types (FeatureDeclaration, MenuDeclaration, etc.)
        }
    }

    /// <summary>
    /// Visits a redefinition declaration that redefines an existing function.
    /// Validates that the original function exists and signatures match.
    /// </summary>
    /// <param name="node">Redefinition declaration node</param>
    /// <returns>Null</returns>
    public object? VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        // TODO: Handle method redefinition
        return null;
    }

    /// <summary>
    /// Visits a using declaration that brings items into scope.
    /// Similar to C# using static directive.
    /// </summary>
    /// <param name="node">Using declaration node</param>
    /// <returns>Null</returns>
    public object? VisitUsingDeclaration(UsingDeclaration node)
    {
        // TODO: Handle type alias
        return null;
    }

    // Statements
    /// <summary>
    /// Visits an expression statement that evaluates an expression for its side effects.
    /// </summary>
    /// <param name="node">Expression statement node</param>
    /// <returns>Null</returns>
    public object? VisitExpressionStatement(ExpressionStatement node)
    {
        node.Expression.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Visits a declaration statement and registers the variable in the symbol table.
    /// Performs type checking and memory safety analysis.
    /// </summary>
    /// <param name="node">Declaration statement node</param>
    /// <returns>Null</returns>
    public object? VisitDeclarationStatement(DeclarationStatement node)
    {
        node.Declaration.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Analyze assignment statements with language-specific memory model handling.
    ///
    /// This method demonstrates the fundamental difference between RazorForge and Suflae:
    ///
    /// RazorForge: Assignments use move semantics - objects are transferred and source may become invalid.
    /// The analyzer needs sophisticated analysis to determine when moves occur vs copies.
    ///
    /// Suflae: Assignments use automatic reference counting - both source and target share the object
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

        // CRITICAL: Check for inline-only method calls (.view(), .hijack())
        // These produce temporary tokens that cannot be stored via assignment
        if (IsInlineOnlyMethodCall(expr: node.Value, out string? methodName))
        {
            AddError(
                message: $"Cannot assign result of '.{methodName}()' to a variable. " +
                         $"Inline tokens must be used directly (e.g., 'obj.{methodName}().field') " +
                         $"or use scoped syntax (e.g., '{(methodName == "view" ? "viewing" : "hijacking")} obj as handle {{ ... }}').",
                location: node.Location);
        }

        // CRITICAL: Prevent mutation through read-only wrapper types
        // Viewed<T> and Observed<T> provide read-only access - cannot mutate through them
        if (node.Target is MemberExpression memberTarget)
        {
            var objectType = memberTarget.Object.Accept(visitor: this) as TypeInfo;
            if (objectType != null && IsReadOnlyWrapperType(objectType.Name))
            {
                AddError(
                    message: $"Cannot mutate field through read-only wrapper '{objectType.Name}'. " +
                             $"Read-only wrappers (Viewed<T>, Observed<T>) do not allow mutation. " +
                             $"Use hijacking or seizing for mutable access.",
                    location: node.Location);
            }
        }

        // CRITICAL: Prevent scoped tokens from escaping their scope
        // Scoped tokens (Viewed, Hijacked, Seized, Observed) cannot be assigned to variables
        if (node.Value is IdentifierExpression valIdent && IsScopedToken(valIdent.Name))
        {
            AddError(
                message: $"Cannot assign scoped token '{valIdent.Name}' to a variable. " +
                         $"Scoped tokens are bound to their declaring scope and cannot escape. " +
                         $"Use the token directly within the scoped statement block.",
                location: node.Location);
        }

        // CRITICAL: Language-specific memory model handling for assignments
        if (node.Target is IdentifierExpression targetId &&
            node.Value is IdentifierExpression valueId)
        {
            if (_language == Language.Suflae)
            {
                // Suflae: Automatic reference counting - both variables share the same object
                // Source remains valid, RC is incremented, no invalidation occurs
                _memoryAnalyzer.HandleSuflaeAssignment(target: targetId.Name, source: valueId.Name,
                    location: node.Location);
            }
            else if (_language == Language.RazorForge)
            {
                // RazorForge: Move semantics - determine if assignment is copy or move
                if (targetType != null)
                {
                    bool isMove =
                        DetermineMoveSemantics(valueExpr: node.Value, targetType: targetType);

                    if (isMove)
                    {
                        // Move operation: Transfer ownership from source to target
                        if (node.Value is IdentifierExpression sourceId)
                        {
                            // In move semantics, the source is invalidated
                            // For now, we register the target and note that ownership transferred
                            // TODO: Add validation to prevent use-after-move for the source
                        }

                        // Register new object with ownership transferred
                        _memoryAnalyzer.RegisterObject(name: targetId.Name, type: targetType,
                            location: node.Location);
                    }
                    else
                    {
                        // Copy operation: Create new reference
                        _memoryAnalyzer.RegisterObject(name: targetId.Name, type: targetType,
                            location: node.Location);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Visits a return statement and validates the return value type.
    /// </summary>
    /// <param name="node">Return statement node</param>
    /// <returns>Null</returns>
    public object? VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value != null)
        {
            var returnType = node.Value.Accept(visitor: this) as TypeInfo;

            // CRITICAL: Prevent inline-only tokens from being returned (no-return rule)
            // .view() and .hijack() produce tokens that cannot escape the immediate expression
            if (IsInlineOnlyMethodCall(expr: node.Value, out string? methodName))
            {
                AddError(
                    message: $"Cannot return result of '.{methodName}()' from a routine. " +
                             $"Inline tokens (Viewed<T>, Hijacked<T>) cannot escape their usage context. " +
                             $"Return the extracted value instead, or use a callback pattern.",
                    location: node.Location);
            }

            // CRITICAL: Prevent scoped tokens from escaping via return
            // Only usurping functions can return Hijacked<T> tokens
            // Viewed, Seized, Observed tokens can NEVER escape (even from usurping functions)
            if (node.Value is IdentifierExpression returnId && IsScopedToken(returnId.Name))
            {
                // Check if this is a Hijacked<T> token and we're in a usurping function
                if (returnType != null && returnType.Name.StartsWith("Hijacked<") && _isInUsurpingFunction)
                {
                    // Allowed: usurping functions can return Hijacked<T>
                }
                else
                {
                    string tokenType = returnType?.Name ?? "scoped token";
                    AddError(
                        message: $"Cannot return scoped token '{returnId.Name}' of type {tokenType}. " +
                                 $"Scoped tokens are bound to their declaring scope and cannot escape. " +
                                 $"Only usurping functions can return Hijacked<T> tokens. " +
                                 $"Viewed, Seized, and Observed tokens can never escape.",
                        location: node.Location);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Visits an if statement and validates the condition and branches.
    /// </summary>
    /// <param name="node">If statement node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a while loop statement and validates the condition and body.
    /// </summary>
    /// <param name="node">While statement node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a for loop statement and validates the iterator and body.
    /// </summary>
    /// <param name="node">For statement node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a when (pattern matching) statement and validates all pattern clauses.
    /// </summary>
    /// <param name="node">When statement node</param>
    /// <returns>Null</returns>
    public object? VisitWhenStatement(WhenStatement node)
    {
        // Set context flag - operations like try_seize/check_seize must be in when condition
        bool wasInWhenCondition = _isInWhenCondition;
        _isInWhenCondition = true;

        var expressionType = node.Expression.Accept(visitor: this) as TypeInfo;

        // Reset context after evaluating the condition
        _isInWhenCondition = wasInWhenCondition;

        foreach (WhenClause clause in node.Clauses)
        {
            // Enter new scope for pattern variables and scoped tokens
            _symbolTable.EnterScope();
            _memoryAnalyzer.EnterScope();
            _scopeDepth++;

            try
            {
                // Type check pattern against expression and bind pattern variables
                // This may register scoped tokens from fallible lock operations
                ValidatePatternMatch(pattern: clause.Pattern, expressionType: expressionType,
                    location: clause.Location);

                clause.Body.Accept(visitor: this);
            }
            finally
            {
                // Clean up scoped tokens and restore sources when clause exits
                RestoreInvalidatedSources();
                ExitScopeCleanupTokens();
                _scopeDepth--;
                _memoryAnalyzer.ExitScope();
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

    /// <summary>
    /// Visits a break statement that exits a loop.
    /// </summary>
    /// <param name="node">Break statement node</param>
    /// <returns>Null</returns>
    public object? VisitBreakStatement(BreakStatement node)
    {
        return null;
    }
    /// <summary>
    /// Visits a continue statement that skips to the next loop iteration.
    /// </summary>
    /// <param name="node">Continue statement node</param>
    /// <returns>Null</returns>
    public object? VisitContinueStatement(ContinueStatement node)
    {
        return null;
    }

    /// <summary>
    /// Visits a throw statement that returns an error via Result.
    /// </summary>
    /// <param name="node">Throw statement node</param>
    /// <returns>Null</returns>
    public object? VisitThrowStatement(ThrowStatement node)
    {
        // Visit the error expression
        node.Error.Accept(visitor: this);
        return null;
    }

    /// <summary>
    /// Visits an absent statement that indicates value not found.
    /// </summary>
    /// <param name="node">Absent statement node</param>
    /// <returns>Null</returns>
    public object? VisitAbsentStatement(AbsentStatement node)
    {
        // No expression to visit for absent
        return null;
    }

    // Expressions
    /// <summary>
    /// Visits a literal expression and infers its type.
    /// </summary>
    /// <param name="node">Literal expression node</param>
    /// <returns>TypeInfo representing the literal type</returns>
    public object? VisitLiteralExpression(LiteralExpression node)
    {
        // Use the explicit LiteralType from the token to determine the type
        // This is more accurate than inferring from C# runtime type
        return node.LiteralType switch
        {
            // Explicitly typed integer literals (same for both languages)
            TokenType.S8Literal => new TypeInfo(Name: "s8", IsReference: false),
            TokenType.S16Literal => new TypeInfo(Name: "s16", IsReference: false),
            TokenType.S32Literal => new TypeInfo(Name: "s32", IsReference: false),
            TokenType.S64Literal => new TypeInfo(Name: "s64", IsReference: false),
            TokenType.S128Literal => new TypeInfo(Name: "s128", IsReference: false),
            TokenType.SyssintLiteral => new TypeInfo(Name: "saddr", IsReference: false),
            TokenType.U8Literal => new TypeInfo(Name: "u8", IsReference: false),
            TokenType.U16Literal => new TypeInfo(Name: "u16", IsReference: false),
            TokenType.U32Literal => new TypeInfo(Name: "u32", IsReference: false),
            TokenType.U64Literal => new TypeInfo(Name: "u64", IsReference: false),
            TokenType.U128Literal => new TypeInfo(Name: "u128", IsReference: false),
            TokenType.SysuintLiteral => new TypeInfo(Name: "uaddr", IsReference: false),

            // Untyped integer: RazorForge defaults to s64, Suflae defaults to Integer (arbitrary precision)
            TokenType.Integer => new TypeInfo(
                Name: _language == Language.Suflae ? "Integer" : "s64",
                IsReference: false),

            // Explicitly typed floating-point literals (same for both languages)
            TokenType.F16Literal => new TypeInfo(Name: "f16", IsReference: false),
            TokenType.F32Literal => new TypeInfo(Name: "f32", IsReference: false),
            TokenType.F64Literal => new TypeInfo(Name: "f64", IsReference: false),
            TokenType.F128Literal => new TypeInfo(Name: "f128", IsReference: false),
            TokenType.D32Literal => new TypeInfo(Name: "d32", IsReference: false),
            TokenType.D64Literal => new TypeInfo(Name: "d64", IsReference: false),
            TokenType.D128Literal => new TypeInfo(Name: "d128", IsReference: false),

            // Untyped decimal: RazorForge defaults to f64, Suflae defaults to Decimal (arbitrary precision)
            TokenType.Decimal => new TypeInfo(
                Name: _language == Language.Suflae ? "Decimal" : "f64",
                IsReference: false),

            // Text literals: RazorForge has Text<letter>/Text<letter8>/Text<letter16>, Suflae has Text (UTF-8) and Bytes (no Text16)
            TokenType.TextLiteral or TokenType.FormattedText or TokenType.RawText or TokenType.RawFormattedText =>
                new TypeInfo(Name: _language == Language.Suflae ? "Text" : "Text<letter>", IsReference: false),
            TokenType.Text8Literal or TokenType.Text8FormattedText or TokenType.Text8RawText or TokenType.Text8RawFormattedText =>
                new TypeInfo(Name: _language == Language.Suflae ? "Bytes" : "Text<letter8>", IsReference: false),
            // Text16 literals: Only supported in RazorForge, not in Suflae
            TokenType.Text16Literal or TokenType.Text16FormattedText or TokenType.Text16RawText or TokenType.Text16RawFormattedText =>
                HandleText16Literal(node),

            // Suflae-only bytes literals (b"", br"", bf"", brf"")
            TokenType.BytesLiteral or TokenType.BytesRawLiteral or TokenType.BytesFormatted or TokenType.BytesRawFormatted =>
                HandleBytesLiteral(node),

            // Duration literals - all produce Duration type
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral or TokenType.MinuteLiteral
                or TokenType.SecondLiteral or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral
                or TokenType.NanosecondLiteral =>
                new TypeInfo(Name: "Duration", IsReference: false),

            // Memory size literals - all produce MemorySize type
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral
                or TokenType.KilobitLiteral or TokenType.KibibitLiteral or TokenType.MegabyteLiteral
                or TokenType.MebibyteLiteral or TokenType.MegabitLiteral or TokenType.MebibitLiteral
                or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral or TokenType.GigabitLiteral
                or TokenType.GibibitLiteral or TokenType.TerabyteLiteral or TokenType.TebibyteLiteral
                or TokenType.TerabitLiteral or TokenType.TebibitLiteral or TokenType.PetabyteLiteral
                or TokenType.PebibyteLiteral or TokenType.PetabitLiteral or TokenType.PebibitLiteral =>
                new TypeInfo(Name: "MemorySize", IsReference: false),

            // Boolean and none literals (same for both languages)
            TokenType.True or TokenType.False => new TypeInfo(Name: "Bool", IsReference: false),
            TokenType.None => new TypeInfo(Name: "none", IsReference: false),
            _ => InferLiteralType(value: node.Value) // Fallback to runtime type inference
        };
    }

    /// <summary>
    /// Visits an identifier expression and looks up its type in the symbol table.
    /// </summary>
    /// <param name="node">Identifier expression node</param>
    /// <returns>TypeInfo of the identifier</returns>
    public object? VisitIdentifierExpression(IdentifierExpression node)
    {
        Symbol? symbol = _symbolTable.Lookup(name: node.Name);
        if (symbol == null)
        {
            AddError(message: $"Undefined identifier '{node.Name}'", location: node.Location);
            return null;
        }

        // CRITICAL: Check if this source variable is invalidated by a scoped access statement
        if (IsSourceInvalidated(node.Name))
        {
            string? accessType = GetInvalidationAccessType(node.Name);
            AddError(
                message: $"Cannot access '{node.Name}' while it is being accessed via {accessType} statement. " +
                         $"The source is temporarily unavailable while the scoped token exists. " +
                         $"Access the data through the scoped token instead.",
                location: node.Location);
        }

        return symbol.Type;
    }

    /// <summary>
    /// Visits a binary expression and validates operand types.
    /// </summary>
    /// <param name="node">Binary expression node</param>
    /// <returns>TypeInfo of the result</returns>
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

        // Comparison operators return Bool
        if (IsComparisonOperator(op: node.Operator))
        {
            return new TypeInfo(Name: "Bool", IsReference: false);
        }

        // Logical operators return Bool
        if (IsLogicalOperator(op: node.Operator))
        {
            return new TypeInfo(Name: "Bool", IsReference: false);
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

    private bool IsComparisonOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Equal or BinaryOperator.NotEqual
                or BinaryOperator.Less or BinaryOperator.LessEqual
                or BinaryOperator.Greater or BinaryOperator.GreaterEqual
                or BinaryOperator.In or BinaryOperator.NotIn
                or BinaryOperator.Is or BinaryOperator.IsNot
                or BinaryOperator.From or BinaryOperator.NotFrom
                or BinaryOperator.Follows or BinaryOperator.NotFollows => true,
            _ => false
        };
    }

    private bool IsLogicalOperator(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.And or BinaryOperator.Or => true,
            _ => false
        };
    }

    private bool AreTypesCompatible(TypeInfo left, TypeInfo right)
    {
        // If either type is a generic parameter, they are compatible
        // (concrete type checking happens at instantiation time)
        if (left.IsGenericParameter || right.IsGenericParameter)
        {
            return true;
        }

        // Types are compatible if they are exactly the same
        return left.Name == right.Name && left.IsReference == right.IsReference;
    }

    /// <summary>
    /// Visits a unary expression and validates the operand type.
    /// </summary>
    /// <param name="node">Unary expression node</param>
    /// <returns>TypeInfo of the result</returns>
    public object? VisitUnaryExpression(UnaryExpression node)
    {
        var operandType = node.Operand.Accept(visitor: this) as TypeInfo;
        // TODO: Check unary operator compatibility
        return operandType;
    }

    /// <summary>
    /// Analyze function calls with special handling for memory operation methods.
    ///
    /// This is where the magic happens for memory operations like obj.retain(), obj.share(), etc.
    /// The analyzer detects method calls ending with '!' and routes them through the memory
    /// analyzer for proper ownership tracking and safety validation.
    ///
    /// Memory operations are the core of RazorForge's explicit memory model, allowing
    /// programmers to transform objects between different wrapper types (Owned, Retained,
    /// Shared, Hijacked, etc.) with compile-time safety guarantees.
    ///
    /// Regular function calls are handled with standard type checking and argument validation.
    /// </summary>
    public object? VisitCallExpression(CallExpression node)
    {
        // Check if this is a standalone danger zone function call (address_of!, invalidate!)
        if (node.Callee is IdentifierExpression identifierExpr)
        {
            string functionName = identifierExpr.Name;

            // Check for failable type conversion: s32!(expr), u64!(expr), Text!(expr), etc.
            // Type conversion functions end with '!' and the base name is a type
            if (functionName.EndsWith("!"))
            {
                string baseTypeName = functionName.TrimEnd('!');
                if (IsTypeName(baseTypeName))
                {
                    // This is a failable type conversion
                    // Type check all arguments (should be exactly 1)
                    foreach (Expression arg in node.Arguments)
                    {
                        arg.Accept(visitor: this);
                    }

                    // Return the target type as the result type
                    return new TypeInfo(Name: baseTypeName, IsReference: false);
                }
            }

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

        // Handle namespaced function calls (e.g., Console.show_line())
        // When callee is a MemberExpression with an IdentifierExpression as object,
        // check if this is a namespaced function call before treating it as a method call
        if (node.Callee is MemberExpression namespacedCall &&
            namespacedCall.Object is IdentifierExpression namespaceId)
        {
            string qualifiedName = $"{namespaceId.Name}.{namespacedCall.PropertyName}";
            Symbol? funcSymbol = _symbolTable.Lookup(name: qualifiedName);
            if (funcSymbol != null)
            {
                // Type check all arguments
                foreach (Expression arg in node.Arguments)
                {
                    arg.Accept(visitor: this);
                }

                // Return the function's return type
                if (funcSymbol is FunctionSymbol func)
                {
                    return func.ReturnType;
                }
                else if (funcSymbol is FunctionOverloadSet overloadSet && overloadSet.Overloads.Count > 0)
                {
                    // TODO: Proper overload resolution based on argument types
                    // For now, return the first overload's return type
                    return overloadSet.Overloads[0].ReturnType;
                }

                return funcSymbol.Type;
            }
        }

        // Standard function call type checking
        var functionType = node.Callee.Accept(visitor: this) as TypeInfo;

        // CRITICAL: Validate arguments for inline-only and scoped token rules
        ValidateCallArguments(node);

        // Type check all arguments
        foreach (Expression arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // TODO: Return function's actual return type based on signature
        return functionType;
    }

    /// <summary>
    /// Validates function call arguments for memory safety rules:
    /// 1. Inline-only tokens (.view()/.hijack()) cannot be passed to container methods (push, insert, etc.)
    /// 2. Scoped tokens cannot be passed to container methods
    /// 3. Same Hijacked&lt;T&gt; token cannot be passed twice in the same call
    /// </summary>
    private void ValidateCallArguments(CallExpression node)
    {
        // Check for container methods that store values
        bool isContainerStorageMethod = false;
        if (node.Callee is MemberExpression memberCall)
        {
            isContainerStorageMethod = memberCall.PropertyName is
                "push" or "append" or "insert" or "add" or "set" or "put" or
                "enqueue" or "push_front" or "push_back";
        }

        // Track Hijacked tokens to detect duplicates
        var hijackedTokens = new HashSet<string>();

        foreach (var arg in node.Arguments)
        {
            // Rule 1: Check for inline-only method calls being stored in containers
            if (isContainerStorageMethod && IsInlineOnlyMethodCall(arg, out string? methodName))
            {
                AddError(
                    message: $"Cannot store result of '.{methodName}()' in a container. " +
                             $"Inline tokens (Viewed<T>, Hijacked<T>) cannot be stored. " +
                             $"Extract the value first, or use ownership transfer with .consume().",
                    location: node.Location);
            }

            // Rule 2: Check for scoped tokens being stored in containers
            if (isContainerStorageMethod && arg is IdentifierExpression argIdent && IsScopedToken(argIdent.Name))
            {
                AddError(
                    message: $"Cannot store scoped token '{argIdent.Name}' in a container. " +
                             $"Scoped tokens are bound to their scope and cannot escape. " +
                             $"Extract the value first, or restructure to keep the token local.",
                    location: node.Location);
            }

            // Rule 3: Check for duplicate Hijacked<T> tokens in same call
            if (arg is IdentifierExpression tokenIdent)
            {
                var tokenType = _symbolTable.Lookup(tokenIdent.Name)?.Type;
                if (tokenType != null && tokenType.Name.StartsWith("Hijacked<"))
                {
                    if (!hijackedTokens.Add(tokenIdent.Name))
                    {
                        AddError(
                            message: $"Cannot pass the same Hijacked<T> token '{tokenIdent.Name}' twice in a single call. " +
                                     $"Hijacked<T> represents unique exclusive access and cannot be duplicated.",
                            location: node.Location);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Visits a member access expression and validates the member exists.
    /// </summary>
    /// <param name="node">Member expression node</param>
    /// <returns>TypeInfo of the member</returns>
    public object? VisitMemberExpression(MemberExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;

        if (objectType == null)
        {
            return null;
        }

        // Check if accessing field through a wrapper type that allows transparent access
        // Hijacked<T>, Viewed<T>, Retained<T> all allow direct field access on inner type T
        if (IsTransparentWrapperType(objectType.Name, out string? innerTypeName))
        {
            // Unwrap to get the inner type and check member on that type
            // For now, we'll return a generic TypeInfo - full field validation would need type lookup
            // TODO: Look up field type from type definition of innerTypeName
            return new TypeInfo(Name: "unknown", IsReference: false);
        }

        // TODO: Check if member exists on objectType directly
        return null;
    }

    /// <summary>
    /// Checks if a type is a transparent wrapper that allows direct field access.
    /// Transparent wrappers: Hijacked&lt;T&gt;, Viewed&lt;T&gt;, Retained&lt;T&gt;, Observed&lt;T&gt;, Seized&lt;T&gt;
    /// Note: Snatched&lt;T&gt; requires explicit .reveal_as&lt;U&gt;() - not transparent
    /// </summary>
    private bool IsTransparentWrapperType(string typeName, out string? innerType)
    {
        // Match patterns: Hijacked<T>, Viewed<T>, Retained<T>, Observed<T>, Seized<T>
        foreach (var wrapperPrefix in new[] { "Hijacked<", "Viewed<", "Retained<", "Observed<", "Seized<" })
        {
            if (typeName.StartsWith(wrapperPrefix) && typeName.EndsWith(">"))
            {
                // Extract inner type T from Wrapper<T>
                innerType = typeName.Substring(wrapperPrefix.Length,
                    typeName.Length - wrapperPrefix.Length - 1);
                return true;
            }
        }

        innerType = null;
        return false;
    }

    /// <summary>
    /// Checks if a type is a read-only wrapper that does not allow mutation.
    /// Read-only wrappers: Viewed<T>, Observed<T>
    /// </summary>
    private bool IsReadOnlyWrapperType(string typeName)
    {
        return typeName.StartsWith("Viewed<") || typeName.StartsWith("Observed<");
    }

    /// <summary>
    /// Visits an index expression and validates the indexing operation.
    /// </summary>
    /// <param name="node">Index expression node</param>
    /// <returns>TypeInfo of the indexed element</returns>
    public object? VisitIndexExpression(IndexExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        var indexType = node.Index.Accept(visitor: this) as TypeInfo;
        // TODO: Check indexing compatibility
        return null;
    }

    /// <summary>
    /// Visits a conditional (ternary) expression and validates all branches.
    /// </summary>
    /// <param name="node">Conditional expression node</param>
    /// <returns>TypeInfo of the result</returns>
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

    /// <summary>
    /// Visits a lambda expression and validates parameter and return types.
    /// </summary>
    /// <param name="node">Lambda expression node</param>
    /// <returns>TypeInfo representing the function type</returns>
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

    /// <summary>
    /// Visits a chained comparison expression (e.g., a &lt; b &lt; c).
    /// </summary>
    /// <param name="node">Chained comparison expression node</param>
    /// <returns>TypeInfo of bool</returns>
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

    /// <summary>
    /// Visits a range expression and validates the start and end values.
    /// </summary>
    /// <param name="node">Range expression node</param>
    /// <returns>TypeInfo representing the range type</returns>
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

    /// <summary>
    /// Visits a type expression and resolves it to a TypeInfo.
    /// </summary>
    /// <param name="node">Type expression node</param>
    /// <returns>TypeInfo</returns>
    public object? VisitTypeExpression(TypeExpression node)
    {
        // Type expressions are handled during semantic analysis
        return null;
    }

    /// <summary>
    /// Visits a type conversion expression and validates the conversion.
    /// </summary>
    /// <param name="node">Type conversion expression node</param>
    /// <returns>TypeInfo of the target type</returns>
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
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" or "u8" or "u16" or "u32"
                or "u64" or "u128" or "uaddr" or "f16" or "f32" or "f64" or "f128" or "d32"
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
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" or "u8" or "u16" or "u32"
                or "u64" or "u128" or "uaddr" or "f16" or "f32" or "f64" or "f128" or "d32"
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

        // Check if this is a type parameter (e.g., T in a generic function)
        Symbol? symbol = _symbolTable.Lookup(name: typeExpr.Name);
        if (symbol is TypeParameterSymbol typeParam)
        {
            // Return type info marked as a generic parameter
            return new TypeInfo(Name: typeExpr.Name, IsReference: false, IsGenericParameter: true);
        }

        // Handle generic types with type arguments
        if (typeExpr.GenericArguments != null && typeExpr.GenericArguments.Count > 0)
        {
            var resolvedArgs = typeExpr.GenericArguments
                                       .Select(selector: arg => ResolveType(typeExpr: arg))
                                       .Where(predicate: t => t != null)
                                       .Cast<TypeInfo>()
                                       .ToList();

            return new TypeInfo(Name: typeExpr.Name,
                IsReference: false, // TODO: Determine based on base type
                GenericArguments: resolvedArgs);
        }

        return new TypeInfo(Name: typeExpr.Name, IsReference: false);
    }

    private TypeInfo? ResolveType(TypeExpression? typeExpr, SourceLocation location)
    {
        return ResolveType(typeExpr: typeExpr);
    }

    /// <summary>
    /// Resolves a generic type expression with substitution from generic bindings.
    /// Used when instantiating generic functions or classes with concrete types.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve</param>
    /// <param name="genericBindings">Map of generic parameter names to concrete types</param>
    /// <returns>Resolved type with generic parameters substituted</returns>
    private TypeInfo? ResolveGenericType(TypeExpression? typeExpr,
        Dictionary<string, TypeInfo> genericBindings)
    {
        if (typeExpr == null)
        {
            return null;
        }

        // If this is a generic parameter, substitute with the actual type
        if (genericBindings.TryGetValue(key: typeExpr.Name, value: out TypeInfo? actualType))
        {
            return actualType;
        }

        // If it has generic arguments, recursively resolve them
        if (typeExpr.GenericArguments != null && typeExpr.GenericArguments.Count > 0)
        {
            var resolvedArgs = typeExpr.GenericArguments
                                       .Select(selector: arg => ResolveGenericType(typeExpr: arg,
                                            genericBindings: genericBindings))
                                       .Where(predicate: t => t != null)
                                       .Cast<TypeInfo>()
                                       .ToList();

            return new TypeInfo(Name: typeExpr.Name, IsReference: false,
                GenericArguments: resolvedArgs);
        }

        // Regular non-generic type
        return ResolveType(typeExpr: typeExpr);
    }

    /// <summary>
    /// Validates that a concrete type satisfies generic constraints.
    /// Checks base type requirements and value/reference type constraints.
    /// </summary>
    /// <param name="genericParam">Name of the generic parameter being constrained</param>
    /// <param name="actualType">The concrete type being validated</param>
    /// <param name="constraint">The constraint to validate against</param>
    /// <param name="location">Source location for error reporting</param>
    private void ValidateGenericConstraints(string genericParam, TypeInfo actualType,
        GenericConstraint? constraint, SourceLocation location)
    {
        if (constraint == null)
        {
            return;
        }

        // Validate value type constraint (record)
        if (constraint.IsValueType && actualType.IsReference)
        {
            AddError(
                message:
                $"Type argument '{actualType.Name}' for generic parameter '{genericParam}' must be a value type (record)",
                location: location);
        }

        // Validate reference type constraint (entity)
        if (constraint.IsReferenceType && !actualType.IsReference)
        {
            AddError(
                message:
                $"Type argument '{actualType.Name}' for generic parameter '{genericParam}' must be a reference type (entity)",
                location: location);
        }

        // Validate base type constraints
        if (constraint.BaseTypes != null)
        {
            foreach (TypeInfo baseType in constraint.BaseTypes)
            {
                if (!IsAssignableFrom(baseType: baseType, derivedType: actualType))
                {
                    AddError(
                        message:
                        $"Type argument '{actualType.Name}' for generic parameter '{genericParam}' does not satisfy constraint '{baseType.Name}'",
                        location: location);
                }
            }
        }
    }

    /// <summary>
    /// Checks if a derived type is assignable to a base type.
    /// Used for validating generic constraints that require base types.
    /// </summary>
    private bool IsAssignableFrom(TypeInfo baseType, TypeInfo derivedType)
    {
        // Same type is always assignable
        if (baseType.Name == derivedType.Name)
        {
            return true;
        }

        // TODO: Check actual inheritance hierarchy from symbol table
        // For now, we'll just check type name equality
        // In a full implementation, this would check:
        // 1. Direct inheritance (Entity Derived from Base)
        // 2. Interface implementation (Entity implements Protocol)
        // 3. Variant case matching

        return false;
    }

    /// <summary>
    /// Performs type inference for generic method calls.
    /// Attempts to deduce generic type arguments from the method arguments.
    /// </summary>
    /// <param name="funcSymbol">The generic function being called</param>
    /// <param name="arguments">The arguments passed to the function</param>
    /// <param name="genericBindings">Output dictionary of inferred type bindings</param>
    private void InferGenericTypes(FunctionSymbol funcSymbol, List<Expression> arguments,
        Dictionary<string, TypeInfo> genericBindings)
    {
        if (funcSymbol.GenericParameters == null || funcSymbol.GenericParameters.Count == 0)
        {
            return;
        }

        // Match argument types with parameter types to infer generic arguments
        for (int i = 0;
             i < Math.Min(val1: funcSymbol.Parameters.Count, val2: arguments.Count);
             i++)
        {
            Parameter param = funcSymbol.Parameters[index: i];
            Expression arg = arguments[index: i];

            var argType = arg.Accept(visitor: this) as TypeInfo;
            if (argType == null || param.Type == null)
            {
                continue;
            }

            // Try to match the parameter type pattern with the argument type
            InferGenericTypeFromPattern(paramType: param.Type, argType: argType,
                genericBindings: genericBindings);
        }
    }

    /// <summary>
    /// Recursively matches a parameter type pattern against an argument type
    /// to infer generic type parameter bindings.
    /// </summary>
    private void InferGenericTypeFromPattern(TypeExpression paramType, TypeInfo argType,
        Dictionary<string, TypeInfo> genericBindings)
    {
        // If the parameter type is a generic parameter (e.g., T), bind it
        // We need to check if it's in the function's generic parameters list
        // For now, use a simple heuristic: single uppercase letter names are generic params
        if (paramType.Name.Length == 1 && char.IsUpper(c: paramType.Name[index: 0]) &&
            !genericBindings.ContainsKey(key: paramType.Name))
        {
            genericBindings[key: paramType.Name] = argType;
            return;
        }

        // If both have generic arguments, recursively match them
        if (paramType.GenericArguments != null && argType.GenericArguments != null &&
            paramType.GenericArguments.Count == argType.GenericArguments.Count)
        {
            for (int i = 0; i < paramType.GenericArguments.Count; i++)
            {
                InferGenericTypeFromPattern(paramType: paramType.GenericArguments[index: i],
                    argType: argType.GenericArguments[index: i], genericBindings: genericBindings);
            }
        }
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

    /// <summary>
    /// Handles Text16 literals - only supported in RazorForge, not in Suflae.
    /// Text16 literals (t16"...", t16f"...", t16r"...", t16rf"...") produce Text&lt;letter16&gt; in RazorForge.
    /// In Suflae, these literals are a compile error since Suflae doesn't support UTF-16 text.
    /// </summary>
    /// <param name="node">The literal expression node</param>
    /// <returns>TypeInfo for Text&lt;letter16&gt; in RazorForge, or error for Suflae</returns>
    private TypeInfo HandleText16Literal(LiteralExpression node)
    {
        if (_language == Language.Suflae)
        {
            AddError(
                message: "Text16 literals (t16\"...\") are not supported in Suflae. " +
                         "Suflae uses Text (UTF-8) and Bytes. Use regular string literals (\"...\") for Text " +
                         "or bytes literals (b\"...\") for raw byte data.",
                location: node.Location);
            // Return Text as fallback type for error recovery
            return new TypeInfo(Name: "Text", IsReference: false);
        }

        // RazorForge: Text16 literals produce Text<letter16>
        return new TypeInfo(Name: "Text<letter16>", IsReference: false);
    }

    /// <summary>
    /// Handles Suflae bytes literals (b"", br"", bf"", brf"").
    /// These are Suflae-only - they produce Bytes type for raw byte data.
    /// In RazorForge, use Text&lt;letter8&gt; for UTF-8 byte strings instead.
    /// </summary>
    /// <param name="node">The literal expression node</param>
    /// <returns>TypeInfo for Bytes in Suflae, or error for RazorForge</returns>
    private TypeInfo HandleBytesLiteral(LiteralExpression node)
    {
        if (_language == Language.RazorForge)
        {
            AddError(
                message: "Bytes literals (b\"...\", br\"...\", bf\"...\", brf\"...\") are Suflae-only syntax. " +
                         "In RazorForge, use t8\"...\" for Text<letter8> (UTF-8) byte strings, " +
                         "or use a DynamicSlice for raw byte data.",
                location: node.Location);
            // Return Text<letter8> as fallback type for error recovery
            return new TypeInfo(Name: "Text<letter8>", IsReference: false);
        }

        // Suflae: Bytes literals produce Bytes type
        return new TypeInfo(Name: "Bytes", IsReference: false);
    }

    /// <summary>
    /// Infers the type of a pattern literal using its explicit TokenType.
    /// This ensures typed literals like 1_s32 in patterns are correctly identified as s32
    /// instead of defaulting to s64 based on C# runtime type.
    /// </summary>
    private TypeInfo InferPatternLiteralType(object? value, TokenType literalType)
    {
        return literalType switch
        {
            // Explicitly typed integer literals (same for both languages)
            TokenType.S8Literal => new TypeInfo(Name: "s8", IsReference: false),
            TokenType.S16Literal => new TypeInfo(Name: "s16", IsReference: false),
            TokenType.S32Literal => new TypeInfo(Name: "s32", IsReference: false),
            TokenType.S64Literal => new TypeInfo(Name: "s64", IsReference: false),
            TokenType.S128Literal => new TypeInfo(Name: "s128", IsReference: false),
            TokenType.SyssintLiteral => new TypeInfo(Name: "saddr", IsReference: false),
            TokenType.U8Literal => new TypeInfo(Name: "u8", IsReference: false),
            TokenType.U16Literal => new TypeInfo(Name: "u16", IsReference: false),
            TokenType.U32Literal => new TypeInfo(Name: "u32", IsReference: false),
            TokenType.U64Literal => new TypeInfo(Name: "u64", IsReference: false),
            TokenType.U128Literal => new TypeInfo(Name: "u128", IsReference: false),
            TokenType.SysuintLiteral => new TypeInfo(Name: "uaddr", IsReference: false),

            // Untyped integer: RazorForge defaults to s64, Suflae defaults to Integer (arbitrary precision)
            TokenType.Integer => new TypeInfo(
                Name: _language == Language.Suflae ? "Integer" : "s64",
                IsReference: false),

            // Explicitly typed floating-point literals (same for both languages)
            TokenType.F16Literal => new TypeInfo(Name: "f16", IsReference: false),
            TokenType.F32Literal => new TypeInfo(Name: "f32", IsReference: false),
            TokenType.F64Literal => new TypeInfo(Name: "f64", IsReference: false),
            TokenType.F128Literal => new TypeInfo(Name: "f128", IsReference: false),
            TokenType.D32Literal => new TypeInfo(Name: "d32", IsReference: false),
            TokenType.D64Literal => new TypeInfo(Name: "d64", IsReference: false),
            TokenType.D128Literal => new TypeInfo(Name: "d128", IsReference: false),

            // Untyped decimal: RazorForge defaults to f64, Suflae defaults to Decimal (arbitrary precision)
            TokenType.Decimal => new TypeInfo(
                Name: _language == Language.Suflae ? "Decimal" : "f64",
                IsReference: false),

            // Text literals
            TokenType.TextLiteral or TokenType.FormattedText or TokenType.RawText or TokenType.RawFormattedText =>
                new TypeInfo(Name: _language == Language.Suflae ? "Text" : "Text<letter>", IsReference: false),
            TokenType.Text8Literal or TokenType.Text8FormattedText or TokenType.Text8RawText or TokenType.Text8RawFormattedText =>
                new TypeInfo(Name: _language == Language.Suflae ? "Bytes" : "Text<letter8>", IsReference: false),
            TokenType.Text16Literal or TokenType.Text16FormattedText or TokenType.Text16RawText or TokenType.Text16RawFormattedText =>
                new TypeInfo(Name: "Text<letter16>", IsReference: false),

            // Suflae-only bytes literals
            TokenType.BytesLiteral or TokenType.BytesRawLiteral or TokenType.BytesFormatted or TokenType.BytesRawFormatted =>
                new TypeInfo(Name: "Bytes", IsReference: false),

            // Duration literals - all produce Duration type
            TokenType.WeekLiteral or TokenType.DayLiteral or TokenType.HourLiteral or TokenType.MinuteLiteral
                or TokenType.SecondLiteral or TokenType.MillisecondLiteral or TokenType.MicrosecondLiteral
                or TokenType.NanosecondLiteral =>
                new TypeInfo(Name: "Duration", IsReference: false),

            // Memory size literals - all produce MemorySize type
            TokenType.ByteLiteral or TokenType.KilobyteLiteral or TokenType.KibibyteLiteral
                or TokenType.KilobitLiteral or TokenType.KibibitLiteral or TokenType.MegabyteLiteral
                or TokenType.MebibyteLiteral or TokenType.MegabitLiteral or TokenType.MebibitLiteral
                or TokenType.GigabyteLiteral or TokenType.GibibyteLiteral or TokenType.GigabitLiteral
                or TokenType.GibibitLiteral or TokenType.TerabyteLiteral or TokenType.TebibyteLiteral
                or TokenType.TerabitLiteral or TokenType.TebibitLiteral or TokenType.PetabyteLiteral
                or TokenType.PebibyteLiteral or TokenType.PetabitLiteral or TokenType.PebibitLiteral =>
                new TypeInfo(Name: "MemorySize", IsReference: false),

            // Boolean and none literals (same for both languages)
            TokenType.True or TokenType.False => new TypeInfo(Name: "Bool", IsReference: false),
            TokenType.None => new TypeInfo(Name: "none", IsReference: false),

            // Fallback to runtime type inference for unknown types
            _ => InferLiteralType(value: value)
        };
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
        // If either type is a generic parameter, defer comparison to instantiation time
        if (type1.IsGenericParameter || type2.IsGenericParameter)
        {
            return true;
        }

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
                or "D128" or "Integer" or "Decimal" => true, // Suflae arbitrary precision types
            _ => false
        };
    }

    private void AddError(string message, SourceLocation location)
    {
        _errors.Add(item: new SemanticError(Message: message, Location: location, FileName: _fileName));
    }

    /// <summary>
    /// Detect memory operation method calls by their distinctive '!' suffix.
    ///
    /// Memory operations are the heart of RazorForge's explicit memory model:
    /// - retain() - create single-threaded RC (green group)
    /// - share() - create multi-threaded RC with policy (blue group)
    /// - track() - create weak reference (green/blue group)
    /// - recover!() - upgrade weak to strong (crashes if dead)
    /// - try_recover() - upgrade weak to strong (returns Maybe)
    /// - snatch!() - force ownership (danger! only)
    /// - release!() - manual RC decrement
    /// - reveal!(), own!() - handle snatched objects (danger! only)
    ///
    /// Scoped access constructs (compile-time borrows):
    /// - viewing/hijacking - immutable/mutable borrow
    /// - observing/seizing - runtime-locked immutable/mutable access
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
            "retain" or "share" or "track" or "snatch!"
                or "try_recover" or "recover!"
                or "try_seize" or "check_seize" or "try_observe" or "check_observe" => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if an expression is an inline-only method call (.view() or .hijack()).
    ///
    /// These methods produce temporary tokens (Viewed&lt;T&gt; or Hijacked&lt;T&gt;) that:
    /// - Cannot be stored in variables (inline-only)
    /// - Cannot be returned from routines (no-return rule)
    /// - Must be used directly: obj.view().field or obj.hijack().method()
    ///
    /// For multiple operations on the same entity, use scoped syntax instead:
    /// - viewing obj as v { ... }
    /// - hijacking obj as h { ... }
    ///
    /// This restriction eliminates the need for lifetime annotations by ensuring
    /// tokens never escape their immediate usage context.
    /// </summary>
    /// <param name="expr">Expression to check</param>
    /// <param name="methodName">Output: the method name if inline-only, null otherwise</param>
    /// <returns>True if this is an inline-only method call</returns>
    private bool IsInlineOnlyMethodCall(Expression expr, out string? methodName)
    {
        methodName = null;

        // Check for method call pattern: obj.view() or obj.hijack()
        if (expr is CallExpression call && call.Callee is MemberExpression member)
        {
            if (member.PropertyName is "view" or "hijack")
            {
                methodName = member.PropertyName;
                return true;
            }
        }

        return false;
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
    /// - usurping public routine __create___exclusive() -> Hijacked&lt;Node&gt;
    /// - usurping routine factory_method() -> Hijacked&lt;Widget&gt;
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
    /// This method processes calls like obj.retain(), obj.share(), etc., which are
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
    /// <param name="operationName">Name of memory operation (e.g., "share")</param>
    /// <param name="arguments">Method arguments (usually empty for memory ops)</param>
    /// <param name="location">Source location for error reporting</param>
    /// <returns>Wrapper type info for the result, or None if operation failed</returns>
    private TypeInfo? HandleMemoryOperationCall(MemberExpression memberExpr, string operationName,
        List<Expression> arguments, SourceLocation location)
    {
        // Extract object name - currently limited to simple identifiers
        // TODO: Support more complex expressions like container[index].share()
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

        // Validate that fallible lock operations are used in 'when' context
        if (RequiresWhenContext(operation: operation.Value) && !_isInWhenCondition)
        {
            AddError(
                message:
                $"Operation '{operationName}' returns a scope-bound token and must be used directly in a 'when' expression. " +
                $"Tokens cannot be stored in variables.",
                location: location);
            return null;
        }

        // Extract policy argument for share()
        LockingPolicy? policy = null;
        if (operation == MemoryOperation.Share)
        {
            // share(Mutex) or share(MultiReadLock)
            // For now, accept policy as a simple identifier argument
            if (arguments.Count > 0 && arguments[index: 0] is IdentifierExpression policyId)
            {
                policy = policyId.Name switch
                {
                    "Mutex" => LockingPolicy.Mutex,
                    "MultiReadLock" => LockingPolicy.MultiReadLock,
                    _ => null
                };

                if (policy == null)
                {
                    AddError(
                        message:
                        $"Invalid policy '{policyId.Name}'. Expected 'Mutex' or 'MultiReadLock'",
                        location: location);
                    return null;
                }
            }
            else
            {
                // Default to Mutex if no policy specified
                policy = LockingPolicy.Mutex;
            }
        }

        // CRITICAL: Delegate to memory analyzer for safety validation and ownership tracking
        // This is where all the memory safety magic happens
        MemoryObject? resultObj = _memoryAnalyzer.HandleMemoryOperation(objectName: objId.Name,
            operation: operation.Value, location: location, policy: policy);
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
    /// <item>Single-threaded RC: retain, track</item>
    /// <item>Multi-threaded RC: share</item>
    /// <item>Weak reference ops: track, try_recover, recover!</item>
    /// <item>Unsafe operations (danger! only): snatch!</item>
    /// </list>
    /// </summary>
    /// <param name="operationName">Method name from source code</param>
    /// <returns>Corresponding memory operation enum, or null if not found</returns>
    private MemoryOperation? ParseMemoryOperation(string operationName)
    {
        return operationName switch
        {
            // Group 2: Single-threaded reference counting
            "retain" => MemoryOperation.Retain,
            "track" => MemoryOperation.Track,
            "try_recover" => MemoryOperation.Recover,
            "recover!" => MemoryOperation.Recover,

            // Group 3: Multi-threaded reference counting
            "share" => MemoryOperation.Share,

            // Fallible lock acquisition (must be used with 'when')
            "try_seize" => MemoryOperation.TrySeize,
            "check_seize" => MemoryOperation.CheckSeize,
            "try_observe" => MemoryOperation.TryObserve,
            "check_observe" => MemoryOperation.CheckObserve,

            // Unsafe operations (danger! block only)
            "snatch!" => MemoryOperation.Snatch,
            _ => null
        };
    }

    private MemoryOperation? ParseMemoryOperation(string operationName, SourceLocation location)
    {
        return ParseMemoryOperation(operationName: operationName);
    }

    /// <summary>
    /// Check if a memory operation returns a scope-bound token that must be used in 'when' expression.
    /// These operations return Maybe&lt;Token&gt; or Result&lt;Token, Error&gt; that cannot be stored.
    /// </summary>
    /// <param name="operation">The memory operation to check</param>
    /// <returns>True if operation requires immediate 'when' usage</returns>
    private bool RequiresWhenContext(MemoryOperation operation)
    {
        return operation switch
        {
            MemoryOperation.TrySeize or MemoryOperation.CheckSeize
                or MemoryOperation.TryObserve or MemoryOperation.CheckObserve => true,
            _ => false
        };
    }

    /// <summary>
    /// Check if a type is a scoped token type that cannot escape its scope.
    /// Scoped tokens: Viewed&lt;T&gt;, Hijacked&lt;T&gt;, Seized&lt;T&gt;, Observed&lt;T&gt;.
    /// These are created by scoped access statements (viewing, hijacking, seizing, observing).
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>True if type is a scoped token</returns>
    private bool IsScopedTokenType(TypeInfo? type)
    {
        if (type == null) return false;

        string typeName = type.Name;
        return typeName.StartsWith("Viewed<") ||
               typeName.StartsWith("Hijacked<") ||
               typeName.StartsWith("Seized<") ||
               typeName.StartsWith("Observed<");
    }

    /// <summary>
    /// Check if a type is a fallible lock token (from try_seize, check_seize, try_observe, check_observe).
    /// These operations return Maybe&lt;Seized&lt;T&gt;&gt;, Result&lt;Seized&lt;T&gt;, E&gt;,
    /// Maybe&lt;Observed&lt;T&gt;&gt;, or Result&lt;Observed&lt;T&gt;, E&gt;.
    /// </summary>
    /// <param name="type">Type to check</param>
    /// <returns>True if type is a fallible lock token wrapper</returns>
    private bool IsFallibleLockToken(TypeInfo? type)
    {
        if (type == null) return false;

        string typeName = type.Name;

        // Check for Maybe<Seized<T>> or Maybe<Observed<T>>
        if (typeName.StartsWith("Maybe<"))
        {
            string inner = ExtractGenericTypeArgument(typeName, "Maybe");
            return inner.StartsWith("Seized<") || inner.StartsWith("Observed<");
        }

        // Check for Result<Seized<T>, E> or Result<Observed<T>, E>
        if (typeName.StartsWith("Result<"))
        {
            string firstArg = ExtractFirstGenericTypeArgument(typeName, "Result");
            return firstArg.StartsWith("Seized<") || firstArg.StartsWith("Observed<");
        }

        return false;
    }

    /// <summary>
    /// Extract the type argument from a generic type (e.g., Maybe&lt;Seized&lt;T&gt;&gt; → Seized&lt;T&gt;).
    /// </summary>
    private string ExtractGenericTypeArgument(string typeName, string wrapperName)
    {
        int startIdx = typeName.IndexOf('<');
        int endIdx = typeName.LastIndexOf('>');

        if (startIdx > 0 && endIdx > startIdx)
        {
            return typeName.Substring(startIdx + 1, endIdx - startIdx - 1);
        }

        return string.Empty;
    }

    /// <summary>
    /// Extract the first type argument from a multi-parameter generic type
    /// (e.g., Result&lt;Seized&lt;T&gt;, Error&gt; → Seized&lt;T&gt;).
    /// </summary>
    private string ExtractFirstGenericTypeArgument(string typeName, string wrapperName)
    {
        int startIdx = typeName.IndexOf('<');
        if (startIdx <= 0) return string.Empty;

        // Find the matching comma that separates first and second type arguments
        // Need to track angle bracket depth to handle nested generics
        int depth = 0;
        for (int i = startIdx + 1; i < typeName.Length; i++)
        {
            if (typeName[i] == '<') depth++;
            else if (typeName[i] == '>') depth--;
            else if (typeName[i] == ',' && depth == 0)
            {
                return typeName.Substring(startIdx + 1, i - startIdx - 1).Trim();
            }
        }

        // If no comma found, return the entire inner content
        int endIdx = typeName.LastIndexOf('>');
        if (endIdx > startIdx)
        {
            return typeName.Substring(startIdx + 1, endIdx - startIdx - 1).Trim();
        }

        return string.Empty;
    }

    /// <summary>
    /// Register a scoped token variable that cannot escape the current scope.
    /// </summary>
    /// <param name="tokenName">Name of the token variable</param>
    private void RegisterScopedToken(string tokenName)
    {
        _scopedTokens[tokenName] = _scopeDepth;
    }

    /// <summary>
    /// Check if a variable is a scoped token that cannot escape its scope.
    /// </summary>
    /// <param name="variableName">Name of the variable to check</param>
    /// <returns>True if variable is a scoped token</returns>
    private bool IsScopedToken(string variableName)
    {
        return _scopedTokens.ContainsKey(variableName);
    }

    /// <summary>
    /// Clean up scoped tokens that are going out of scope.
    /// Called when exiting a scope to remove tokens bound to that scope.
    /// </summary>
    private void ExitScopeCleanupTokens()
    {
        // Remove all tokens that were created at the current scope depth
        var tokensToRemove = _scopedTokens
            .Where(kvp => kvp.Value == _scopeDepth)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var token in tokensToRemove)
        {
            _scopedTokens.Remove(token);
        }
    }

    /// <summary>
    /// Invalidate a source variable during a scoped access statement.
    /// The source cannot be accessed while the scoped token exists.
    /// </summary>
    /// <param name="sourceName">Name of the source variable to invalidate</param>
    /// <param name="accessType">Type of access (viewing, hijacking, seizing, observing)</param>
    private void InvalidateSource(string sourceName, string accessType)
    {
        _invalidatedSources[sourceName] = (_scopeDepth, accessType);
    }

    /// <summary>
    /// Check if a source variable is currently invalidated.
    /// </summary>
    /// <param name="sourceName">Name of the source variable</param>
    /// <returns>True if source is invalidated</returns>
    private bool IsSourceInvalidated(string sourceName)
    {
        return _invalidatedSources.ContainsKey(sourceName);
    }

    /// <summary>
    /// Get the access type for an invalidated source.
    /// </summary>
    /// <param name="sourceName">Name of the source variable</param>
    /// <returns>Access type (e.g., "viewing", "hijacking") or null if not invalidated</returns>
    private string? GetInvalidationAccessType(string sourceName)
    {
        return _invalidatedSources.TryGetValue(sourceName, out var info) ? info.accessType : null;
    }

    /// <summary>
    /// Restore invalidated sources when exiting a scope.
    /// Called when a scoped statement exits to re-enable source access.
    /// </summary>
    private void RestoreInvalidatedSources()
    {
        // Remove all sources that were invalidated at the current scope depth
        var sourcesToRestore = _invalidatedSources
            .Where(kvp => kvp.Value.scopeDepth == _scopeDepth)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var source in sourcesToRestore)
        {
            _invalidatedSources.Remove(source);
        }
    }

    /// <summary>
    /// Create TypeInfo instances for wrapper types in RazorForge's memory model.
    ///
    /// This method generates the type names that appear in the type system for
    /// memory-wrapped objects. Each wrapper type has a distinctive generic syntax:
    /// <list type="bullet">
    /// <item>Owned: Direct type name (Node, List&lt;s32&gt;)</item>
    /// <item>Hijacked&lt;T&gt;: Exclusive access wrapper (red group 🔴)</item>
    /// <item>Retained&lt;T&gt;: Single-threaded shared ownership wrapper (green group 🟢)</item>
    /// <item>Tracked&lt;T&gt;: Weak observer wrapper (brown group 🟤)</item>
    /// <item>Shared&lt;T, Policy&gt;: Multi-threaded shared wrapper (blue group 🔵)</item>
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
            WrapperType.Retained => $"Retained<{baseType.Name}>", // Single-threaded RC 🟢
            WrapperType.Tracked => $"Tracked<{baseType.Name}>", // Weak observer 🟤
            WrapperType.Shared => $"Shared<{baseType.Name}>", // Thread-safe shared 🔵
            WrapperType.Snatched => $"Snatched<{baseType.Name}>", // Contaminated ownership 💀

            _ => baseType.Name
        };

        return new TypeInfo(Name: typeName, IsReference: baseType.IsReference);
    }

    // Memory slice expression visitor methods
    /// <summary>
    /// Visits a slice constructor expression and validates the slice creation.
    /// </summary>
    /// <param name="node">Slice constructor expression node</param>
    /// <returns>TypeInfo of the slice</returns>
    public object? VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        // Validate size expression is compatible with uaddr type
        var sizeType = node.SizeExpression.Accept(visitor: this) as TypeInfo;
        if (sizeType == null)
        {
            AddError(message: $"Slice size expression has unknown type", location: node.Location);
        }
        else if (sizeType.Name != "uaddr")
        {
            // Allow integer literals to be coerced to uaddr
            if (sizeType.IsInteger)
            {
                // Implicit conversion from any integer type to uaddr for slice sizes
                // This handles cases like DynamicSlice(64) where 64 might be typed as s32, s64, etc.
            }
            else
            {
                AddError(
                    message:
                    $"Slice size must be of type uaddr or compatible integer type, found {sizeType.Name}",
                    location: node.Location);
            }
        }

        // Return appropriate slice type
        string sliceTypeName = node.SliceType;
        return new TypeInfo(Name: sliceTypeName,
            IsReference: false); // Slice types are value types (structs)
    }

    /// <summary>
    /// Visits a generic method call expression and validates type arguments.
    /// </summary>
    /// <param name="node">Generic method call expression node</param>
    /// <returns>TypeInfo of the return value</returns>
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
        if (objectType.Name == "DynamicSlice" || objectType.Name == "TemporarySlice")
        {
            return ValidateSliceGenericMethod(node: node, sliceType: objectType);
        }

        // Handle other generic method calls
        return ValidateGenericMethodCall(node: node, objectType: objectType);
    }

    /// <summary>
    /// Visits a generic member access expression and validates type arguments.
    /// </summary>
    /// <param name="node">Generic member expression node</param>
    /// <returns>TypeInfo of the member</returns>
    public object? VisitGenericMemberExpression(GenericMemberExpression node)
    {
        var objectType = node.Object.Accept(visitor: this) as TypeInfo;
        if (objectType == null)
        {
            AddError(message: "Cannot access member on null object", location: node.Location);
            return null;
        }

        // Validate type arguments are well-formed
        foreach (TypeExpression typeArg in node.TypeArguments)
        {
            TypeInfo? resolvedType = ResolveTypeExpression(typeExpr: typeArg);
            if (resolvedType == null)
            {
                AddError(message: $"Unknown type argument '{typeArg.Name}'",
                    location: node.Location);
            }
        }

        // Validate member exists and is generic
        TypeInfo? memberType = ValidateGenericMember(objectType: objectType,
            memberName: node.MemberName, typeArguments: node.TypeArguments,
            location: node.Location);

        return memberType ?? new TypeInfo(Name: "unknown", IsReference: false);
    }

    /// <summary>
    /// Visits a memory operation expression (e.g., retain, share, track) and validates safety.
    /// </summary>
    /// <param name="node">Memory operation expression node</param>
    /// <returns>TypeInfo of the wrapped result</returns>
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
        if (objectType.Name == "DynamicSlice" || objectType.Name == "TemporarySlice")
        {
            return ValidateSliceMemoryOperation(node: node, sliceType: objectType);
        }

        // Handle other memory operations through memory analyzer
        MemoryOperation? memOp = GetMemoryOperation(operationName: node.OperationName);
        if (memOp == null)
        {
            return objectType;
        }

        MemoryObject? memoryObject =
            _memoryAnalyzer.GetMemoryObject(name: node.Object.ToString() ?? "");
        if (memoryObject == null)
        {
            return objectType;
        }

        MemoryOperationResult result = _memoryAnalyzer.ValidateMemoryOperation(
            memoryObject: memoryObject, operation: memOp.Value, location: node.Location);
        if (result.IsSuccess)
        {
            return CreateWrapperTypeInfo(baseType: memoryObject.BaseType,
                wrapper: result.NewWrapperType);
        }

        foreach (MemoryError error in result.Errors)
        {
            AddError(message: error.Message, location: error.Location);
        }

        return CreateWrapperTypeInfo(baseType: memoryObject.BaseType,
            wrapper: result.NewWrapperType);
    }

    /// <summary>
    /// Visits a danger! block that disables safety checks.
    /// Tracks danger mode state for validation.
    /// </summary>
    /// <param name="node">Danger statement node</param>
    /// <returns>Null</returns>
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

    /// <summary>
    /// Visits a mayhem! block that disables all safety checks.
    /// Tracks mayhem mode state for validation.
    /// </summary>
    /// <param name="node">Mayhem statement node</param>
    /// <returns>Null</returns>
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

    public object? VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        // Intrinsics can only be used inside danger! blocks
        if (!_isInDangerMode)
        {
            AddError(message: $"Intrinsic '{node.IntrinsicName}' can only be used inside danger! blocks",
                location: node.Location);
            return null;
        }

        // Visit arguments for type checking
        foreach (var arg in node.Arguments)
        {
            arg.Accept(visitor: this);
        }

        // For now, return a generic type - intrinsics will be validated in codegen
        // We could add more sophisticated type checking here based on the intrinsic name
        return new TypeInfo(Name: "intrinsic_result", IsReference: false);
    }

    /// <summary>
    /// Visits an external declaration for FFI bindings.
    /// Registers external functions in the symbol table.
    /// </summary>
    /// <param name="node">External declaration node</param>
    /// <returns>Null</returns>
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
                // read<T>!(offset: uaddr) -> T
                if (args.Count != 1)
                {
                    AddError(message: "read<T>! requires exactly one argument (offset)",
                        location: node.Location);
                    return null;
                }

                var offsetType = args[index: 0]
                   .Accept(visitor: this) as TypeInfo;
                if (offsetType?.Name != "uaddr" && !IsIntegerType(typeName: offsetType?.Name))
                {
                    AddError(message: "read<T>! offset must be of type uaddr",
                        location: node.Location);
                }

                return new TypeInfo(Name: targetType.Name, IsReference: false);

            case "write":
                // write<T>!(offset: uaddr, value: T)
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

                if (writeOffsetType?.Name != "uaddr" &&
                    !IsIntegerType(typeName: writeOffsetType?.Name))
                {
                    AddError(message: "write<T>! offset must be of type uaddr",
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

                return new TypeInfo(Name: "uaddr", IsReference: false);

            case "address":
                if (args.Count != 0)
                {
                    AddError(message: "address! operation takes no arguments",
                        location: node.Location);
                }

                return new TypeInfo(Name: "uaddr", IsReference: false);

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
                if (offsetType?.Name != "uaddr")
                {
                    AddError(message: "unsafe_ptr! offset must be of type uaddr",
                        location: node.Location);
                }

                return new TypeInfo(Name: "uaddr", IsReference: false);

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
        // Check if this is actually a generic function call (not a method on an object)
        if (node.Object is IdentifierExpression funcIdent)
        {
            // Look up the function in the symbol table
            Symbol? funcSymbol = _symbolTable.Lookup(name: funcIdent.Name);

            // Check for function overload set
            if (funcSymbol is FunctionOverloadSet overloadSet)
            {
                // Find a matching overload with the right number of generic parameters
                foreach (FunctionSymbol overload in overloadSet.Overloads)
                {
                    if (overload.GenericParameters != null &&
                        overload.GenericParameters.Count == node.TypeArguments.Count)
                    {
                        funcSymbol = overload;
                        break;
                    }
                }
            }

            if (funcSymbol is FunctionSymbol func &&
                func.GenericParameters != null &&
                func.GenericParameters.Count > 0)
            {
                // This is a generic function call - resolve the return type
                // by substituting the type arguments

                // Create binding map from type parameters to concrete types
                var genericBindings = new Dictionary<string, TypeInfo>();
                for (int i = 0; i < Math.Min(func.GenericParameters.Count, node.TypeArguments.Count); i++)
                {
                    string paramName = func.GenericParameters[i];
                    TypeInfo? argType = ResolveType(typeExpr: node.TypeArguments[i]);
                    if (argType != null)
                    {
                        genericBindings[paramName] = argType;
                    }
                }

                // Resolve return type with generic substitution
                if (func.ReturnType != null)
                {
                    // If return type is a generic parameter, substitute it
                    if (genericBindings.TryGetValue(func.ReturnType.Name, out TypeInfo? resolvedReturnType))
                    {
                        return resolvedReturnType;
                    }
                    return func.ReturnType;
                }

                return new TypeInfo(Name: "void", IsReference: false);
            }
        }

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
            "s8" or "s16" or "s32" or "s64" or "s128" or "saddr" => true,
            "u8" or "u16" or "u32" or "u64" or "u128" or "uaddr" => true,
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
            "write_as" or "read_as" or "volatile_write" or "volatile_read" or "address_of"
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
            "address_of" => ValidateAddrOf(args: args, location: node.Location),
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
            AddError(message: "address_of! requires exactly one argument (variable)",
                location: location);
            return null;
        }

        // The argument should be a variable reference
        var argType = args[index: 0]
           .Accept(visitor: this) as TypeInfo;
        if (argType == null)
        {
            AddError(message: "address_of! argument must be a valid variable", location: location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo(Name: "uaddr", IsReference: false);
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
        if (argType?.Name != "DynamicSlice" && argType?.Name != "TemporarySlice" &&
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
            "address_of" or "invalidate" => true,
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
            "address_of" => ValidateAddrOfFunction(args: node.Arguments, location: node.Location),
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
            AddError(message: "address_of! requires exactly one argument (variable)",
                location: location);
            return null;
        }

        // The argument should be a variable reference (IdentifierExpression)
        Expression arg = args[index: 0];
        if (arg is not IdentifierExpression)
        {
            AddError(message: "address_of! argument must be a variable identifier",
                location: location);
            return null;
        }

        // Validate that the variable exists
        var argType = arg.Accept(visitor: this) as TypeInfo;
        if (argType == null)
        {
            AddError(message: "address_of! argument must be a valid variable", location: location);
            return null;
        }

        // Return pointer-sized integer (address)
        return new TypeInfo(Name: "uaddr", IsReference: false);
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
        if (argType?.Name != "DynamicSlice" && argType?.Name != "TemporarySlice" &&
            argType?.Name != "ptr")
        {
            AddError(message: "invalidate! argument must be a slice or pointer",
                location: location);
        }

        return new TypeInfo(Name: "void", IsReference: false);
    }

    private bool IsInDangerBlock()
    {
        // Use the tracked danger/mayhem mode state
        return _isInDangerMode || _isInMayhemMode;
    }

    private bool IsEntityType(TypeExpression type)
    {
        // Check if type is declared as entity in symbol table
        Symbol? symbol = _symbolTable.Lookup(name: type.Name);
        if (symbol?.Type != null)
        {
            // Check if it's marked as a reference type (entities are reference types)
            return symbol.Type.IsReference;
        }

        // Fallback to naming convention patterns for common entity types
        return type.Name.EndsWith(value: "Entity") || type.Name.EndsWith(value: "Service") ||
               type.Name.EndsWith(value: "Controller");
    }

    /// <summary>
    /// Determines whether an assignment should use move semantics or copy semantics.
    /// In RazorForge, move semantics transfer ownership while copy semantics create a new reference.
    /// </summary>
    /// <param name="valueExpr">The expression being assigned</param>
    /// <param name="targetType">The type of the target variable</param>
    /// <returns>True if move semantics should be used, false for copy semantics</returns>
    private bool DetermineMoveSemantics(Expression valueExpr, TypeInfo targetType)
    {
        // Rule 1: Primitive types are always copied (never moved)
        if (IsPrimitiveType(type: targetType))
        {
            return false; // Copy
        }

        // Rule 2: Literals and computed expressions are always copies
        if (valueExpr is not IdentifierExpression)
        {
            return false; // Copy (new allocation)
        }

        // Rule 3: Check if the value type is copyable
        // Records are copyable (they have copy semantics)
        // Entities and wrapper types require explicit operations
        if (IsRecordType(type: targetType))
        {
            return false; // Copy (records support automatic copy)
        }

        // Rule 4: For entities and heap-allocated objects, default to move
        // unless explicitly wrapped in share!() or other wrapper operations
        if (IsEntityType(type: targetType) || IsHeapAllocatedType(type: targetType))
        {
            // Check if the expression is wrapped in a sharing operation
            // For now, assume move unless we detect explicit copy markers
            return true; // Move (transfer ownership)
        }

        // Rule 5: Default to copy for safety
        return false; // Copy
    }

    /// <summary>
    /// Checks if a type is a primitive type (built-in scalar types).
    /// </summary>
    private bool IsPrimitiveType(TypeInfo type)
    {
        string[] primitiveTypes = new[]
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "f16",
            "f32",
            "f64",
            "f128",
            "d32",
            "d64",
            "d128",
            "bool",
            "letter",
            "saddr",
            "uaddr"
        };

        return primitiveTypes.Contains(value: type.Name);
    }

    /// <summary>
    /// Checks if a type is a record type (value type with copy semantics).
    /// </summary>
    private bool IsRecordType(TypeInfo type)
    {
        // Records are non-reference types in the symbol table
        // Or follow naming conventions
        if (!type.IsReference)
        {
            // Check if it's registered as a record in the symbol table
            return true; // Non-reference types are typically records
        }

        return type.Name.EndsWith(value: "Record") || type.Name.StartsWith(value: "Record");
    }

    /// <summary>
    /// Checks if a type requires heap allocation (slices, collections, etc.).
    /// </summary>
    private bool IsHeapAllocatedType(TypeInfo type)
    {
        string[] heapTypes = new[]
        {
            "DynamicSlice",
            "List",
            "Dict",
            "Set",
            "Text"
        };

        return heapTypes.Contains(value: type.Name) || type.Name.StartsWith(value: "Retained<") ||
               type.Name.StartsWith(value: "Shared<");
    }

    /// <summary>
    /// Checks if a type is an entity type (accepts TypeInfo instead of TypeExpression).
    /// </summary>
    private bool IsEntityType(TypeInfo type)
    {
        // Entities are reference types
        if (type.IsReference)
        {
            return true;
        }

        // Fallback to naming conventions
        return type.Name.EndsWith(value: "Entity") || type.Name.EndsWith(value: "Service") ||
               type.Name.EndsWith(value: "Controller");
    }

    /// <summary>
    /// Validates that a pattern is compatible with the matched expression type.
    /// Binds pattern variables to the appropriate scope.
    /// </summary>
    private void ValidatePatternMatch(Pattern pattern, TypeInfo? expressionType,
        SourceLocation location)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                // Literal patterns: check that literal type matches expression type
                // Use the explicit LiteralType from the pattern to determine the type accurately
                TypeInfo literalType = InferPatternLiteralType(literalPattern.Value, literalPattern.LiteralType);
                if (expressionType != null &&
                    !AreTypesCompatible(left: literalType, right: expressionType))
                {
                    AddError(
                        message:
                        $"Pattern literal type '{literalType.Name}' is not compatible with expression type '{expressionType.Name}'",
                        location: location);
                }

                break;

            case IdentifierPattern identifierPattern:
                // Identifier patterns: bind the matched value to a new variable
                if (expressionType != null)
                {
                    var patternVar = new VariableSymbol(Name: identifierPattern.Name,
                        Type: expressionType,
                        IsMutable: false, // Pattern variables are immutable by default
                        Visibility: VisibilityModifier.Private);
                    _symbolTable.TryDeclare(symbol: patternVar);

                    // CRITICAL: Check if this is a fallible lock token (from try_seize/check_seize/try_observe/check_observe)
                    // These return Maybe<Seized<T>>, Result<Seized<T>, E>, Maybe<Observed<T>>, or Result<Observed<T>, E>
                    // The inner token (Seized/Observed) is scoped and cannot escape
                    if (IsFallibleLockToken(expressionType))
                    {
                        // Register the pattern variable as a scoped token
                        // Even though it's wrapped in Maybe/Result, the success case contains a scoped token
                        RegisterScopedToken(identifierPattern.Name);
                    }
                }

                break;

            case TypePattern typePattern:
                // Type patterns: check type compatibility and bind variable if provided
                TypeInfo? patternType = ResolveTypeExpression(typeExpr: typePattern.Type);

                if (expressionType != null && patternType != null)
                {
                    // Check if the pattern type is compatible with expression type
                    // For type patterns, we check if expressionType can be narrowed to patternType
                    if (!IsTypeNarrowable(fromType: expressionType, toType: patternType))
                    {
                        AddError(
                            message:
                            $"Type pattern '{patternType.Name}' cannot match expression of type '{expressionType.Name}'",
                            location: location);
                    }

                    // Bind variable if provided
                    if (typePattern.VariableName != null)
                    {
                        var patternVar = new VariableSymbol(Name: typePattern.VariableName,
                            Type: patternType, // Variable has the narrowed type
                            IsMutable: false, Visibility: VisibilityModifier.Private);
                        _symbolTable.TryDeclare(symbol: patternVar);
                    }
                }

                break;

            case WildcardPattern:
                // Wildcard patterns match everything, no type checking needed
                break;

            case ExpressionPattern expressionPattern:
                // Expression patterns are used for guard conditions in standalone when blocks
                // The expression should evaluate to a boolean
                TypeInfo? exprType = expressionPattern.Expression.Accept(visitor: this) as TypeInfo;
                if (exprType != null && exprType.Name != "Bool" && exprType.Name != "bool")
                {
                    AddError(
                        message: $"Expression pattern must be a boolean expression, got '{exprType.Name}'",
                        location: location);
                }
                break;

            default:
                AddError(message: $"Unknown pattern type: {pattern.GetType().Name}",
                    location: location);
                break;
        }
    }


    /// <summary>
    /// Checks if a type can be narrowed to another type in pattern matching.
    /// Used for type patterns to validate runtime type checks.
    /// </summary>
    private bool IsTypeNarrowable(TypeInfo fromType, TypeInfo toType)
    {
        // Same type is always narrowable
        if (fromType.Name == toType.Name)
        {
            return true;
        }

        // If fromType is a base type and toType is derived (requires inheritance info)
        // For now, allow any reference type to be narrowed to any other reference type
        if (fromType.IsReference && toType.IsReference)
        {
            return true; // Runtime check will validate
        }

        // Numeric types can be narrowed if they're in the same family
        if (AreNumericTypesCompatible(type1: fromType.Name, type2: toType.Name))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks if two numeric types are in the same family (signed int, unsigned int, float).
    /// </summary>
    private bool AreNumericTypesCompatible(string type1, string type2)
    {
        string[] signedInts =
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "saddr"
        };
        string[] unsignedInts =
        {
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "uaddr"
        };
        string[] floats =
        {
            "f16",
            "f32",
            "f64",
            "f128"
        };
        string[] decimals =
        {
            "d32",
            "d64",
            "d128"
        };

        return signedInts.Contains(value: type1) && signedInts.Contains(value: type2) ||
               unsignedInts.Contains(value: type1) && unsignedInts.Contains(value: type2) ||
               floats.Contains(value: type1) && floats.Contains(value: type2) ||
               decimals.Contains(value: type1) && decimals.Contains(value: type2);
    }

    /// <summary>
    /// Resolves a type expression to a TypeInfo.
    /// </summary>
    private TypeInfo? ResolveTypeExpression(TypeExpression typeExpr)
    {
        // Look up in symbol table
        Symbol? symbol = _symbolTable.Lookup(name: typeExpr.Name);
        if (symbol?.Type != null)
        {
            return symbol.Type;
        }

        // Check if it's a built-in type
        if (IsBuiltInType(typeName: typeExpr.Name))
        {
            return new TypeInfo(Name: typeExpr.Name, IsReference: false);
        }

        return null;
    }

    /// <summary>
    /// Checks if a type name is a built-in type.
    /// </summary>
    private bool IsBuiltInType(string typeName)
    {
        string[] builtInTypes =
        {
            "s8",
            "s16",
            "s32",
            "s64",
            "s128",
            "u8",
            "u16",
            "u32",
            "u64",
            "u128",
            "f16",
            "f32",
            "f64",
            "f128",
            "d32",
            "d64",
            "d128",
            "bool",
            "letter",
            "Text",
            "saddr",
            "uaddr",
            "DynamicSlice",
            "TemporarySlice"
        };

        return builtInTypes.Contains(value: typeName);
    }

    /// <summary>
    /// Checks if a name is a valid type name (primitive, built-in, or declared type).
    /// Used for detecting failable type conversions like s32!(expr).
    /// </summary>
    private bool IsTypeName(string name)
    {
        // Check primitive types
        string[] primitiveTypes =
        {
            "s8", "s16", "s32", "s64", "s128", "saddr",
            "u8", "u16", "u32", "u64", "u128", "uaddr",
            "f16", "f32", "f64", "f128",
            "d32", "d64", "d128",
            "bool", "letter", "letter8", "letter16",
            "Text", "Bytes"
        };

        if (primitiveTypes.Contains(name))
        {
            return true;
        }

        // Check built-in types
        if (IsBuiltInType(name))
        {
            return true;
        }

        // Check declared types in symbol table
        Symbol? symbol = _symbolTable.Lookup(name);
        if (symbol is TypeSymbol or StructSymbol or ClassSymbol or VariantSymbol)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Validates a generic member access and determines the result type.
    /// Checks that the member exists on the object type and validates type arguments.
    /// </summary>
    private TypeInfo? ValidateGenericMember(TypeInfo objectType, string memberName,
        List<TypeExpression> typeArguments, SourceLocation location)
    {
        // For collections, validate common generic members
        if (IsCollectionType(typeName: objectType.Name))
        {
            return ValidateCollectionGenericMember(collectionType: objectType,
                memberName: memberName, typeArguments: typeArguments, location: location);
        }

        // For wrapper types (Shared<T>, Hijacked<T>, etc.), validate wrapper members
        if (IsWrapperType(typeName: objectType.Name))
        {
            return ValidateWrapperGenericMember(wrapperType: objectType, memberName: memberName,
                typeArguments: typeArguments, location: location);
        }

        // For custom types, check if member is declared
        // For now, return unknown type - proper implementation would query the type's members
        AddError(
            message: $"Type '{objectType.Name}' does not have a generic member '{memberName}'",
            location: location);

        return null;
    }

    /// <summary>
    /// Validates generic members on collection types (List, Dict, Set, etc.).
    /// </summary>
    private TypeInfo? ValidateCollectionGenericMember(TypeInfo collectionType, string memberName,
        List<TypeExpression> typeArguments, SourceLocation location)
    {
        // Common collection generic members
        switch (memberName)
        {
            case "get":
            case "at":
                // Returns element type (first type parameter of collection)
                if (typeArguments.Count != 0)
                {
                    AddError(message: $"'{memberName}' does not take type arguments",
                        location: location);
                }

                // Extract element type from collection (e.g., List<T> -> T)
                return ExtractElementType(collectionType: collectionType);

            case "map":
            case "filter":
            case "transform":
                // Generic transformation methods
                if (typeArguments.Count != 1)
                {
                    AddError(message: $"'{memberName}' requires exactly one type argument",
                        location: location);
                    return null;
                }

                return ResolveTypeExpression(typeExpr: typeArguments[index: 0]);

            default:
                AddError(
                    message:
                    $"Collection type '{collectionType.Name}' does not have generic member '{memberName}'",
                    location: location);
                return null;
        }
    }

    /// <summary>
    /// Validates generic members on wrapper types (Retained&lt;T&gt;, Shared&lt;T, Policy&gt;, etc.).
    /// </summary>
    private TypeInfo? ValidateWrapperGenericMember(TypeInfo wrapperType, string memberName,
        List<TypeExpression> typeArguments, SourceLocation location)
    {
        switch (memberName)
        {
            case "unwrap":
            case "get":
                // Returns the wrapped type
                if (typeArguments.Count != 0)
                {
                    AddError(message: $"'{memberName}' does not take type arguments",
                        location: location);
                }

                return ExtractWrappedType(wrapperType: wrapperType);

            default:
                AddError(
                    message:
                    $"Wrapper type '{wrapperType.Name}' does not have generic member '{memberName}'",
                    location: location);
                return null;
        }
    }

    /// <summary>
    /// Checks if a type is a collection type.
    /// </summary>
    private bool IsCollectionType(string typeName)
    {
        string[] collectionTypes =
        {
            "List",
            "Dict",
            "Set",
            "Deque",
            "PriorityQueue"
        };
        return collectionTypes.Contains(value: typeName) ||
               collectionTypes.Any(predicate: ct => typeName.StartsWith(value: ct + "<"));
    }

    /// <summary>
    /// Checks if a type is a wrapper type.
    /// </summary>
    private bool IsWrapperType(string typeName)
    {
        string[] wrapperTypes =
        {
            "Retained",
            "Tracked",
            "Shared",
            "Hijacked",
            "Snatched"
        };
        return wrapperTypes.Contains(value: typeName) ||
               wrapperTypes.Any(predicate: wt => typeName.StartsWith(value: wt + "<"));
    }

    /// <summary>
    /// Extracts the element type from a collection type (e.g., List&lt;s32&gt; → s32).
    /// </summary>
    private TypeInfo? ExtractElementType(TypeInfo collectionType)
    {
        // Parse generic type syntax: CollectionName<ElementType>
        string typeName = collectionType.Name;
        int startIdx = typeName.IndexOf(value: '<');
        int endIdx = typeName.LastIndexOf(value: '>');

        if (startIdx > 0 && endIdx > startIdx)
        {
            string elementTypeName =
                typeName.Substring(startIndex: startIdx + 1, length: endIdx - startIdx - 1);
            return new TypeInfo(Name: elementTypeName, IsReference: false);
        }

        // If not parameterized, return unknown
        return new TypeInfo(Name: "unknown", IsReference: false);
    }

    /// <summary>
    /// Extracts the wrapped type from a wrapper type (e.g., Shared&lt;Point&gt; → Point).
    /// </summary>
    private TypeInfo? ExtractWrappedType(TypeInfo wrapperType)
    {
        // Parse generic type syntax: WrapperName<WrappedType>
        string typeName = wrapperType.Name;
        int startIdx = typeName.IndexOf(value: '<');
        int endIdx = typeName.LastIndexOf(value: '>');

        if (startIdx > 0 && endIdx > startIdx)
        {
            string wrappedTypeName =
                typeName.Substring(startIndex: startIdx + 1, length: endIdx - startIdx - 1);
            return new TypeInfo(Name: wrappedTypeName, IsReference: true);
        }

        // If not parameterized, return unknown
        return new TypeInfo(Name: "unknown", IsReference: false);
    }

    /// <summary>
    /// Visits a viewing statement node (scoped read-only access).
    /// Syntax: viewing &lt;source&gt; as &lt;handle&gt; { ... }
    /// Creates a temporary Viewed&lt;T&gt; handle with read-only access.
    /// </summary>
    public object? VisitViewingStatement(ViewingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot view expression with unknown type", location: node.Location);
            return null;
        }

        // Create a new scope for the viewing block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create a Viewed<T> type for the handle
            var viewedType = new TypeInfo(Name: $"Viewed<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: viewedType,
                Visibility: VisibilityModifier.Private, IsMutable: false);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope",
                    location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            RegisterScopedToken(node.Handle);

            // CRITICAL: Invalidate source during scope - prevent concurrent access
            // The source should not be accessible while the Viewed<T> token exists
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceIdent.Name, "viewing");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits a hijacking statement node (scoped exclusive access).
    /// Syntax: hijacking &lt;source&gt; as &lt;handle&gt; { ... }
    /// Creates a temporary Hijacked&lt;T&gt; handle with exclusive write access.
    /// </summary>
    public object? VisitHijackingStatement(HijackingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot hijack expression with unknown type",
                location: node.Location);
            return null;
        }

        // Create a new scope for the hijacking block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create a Hijacked<T> type for the handle
            var hijackedType = new TypeInfo(
                Name: $"Hijacked<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: hijackedType,
                Visibility: VisibilityModifier.Private, IsMutable: true);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope",
                    location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            // Note: Hijacked<T> CAN escape from usurping functions, but that's validated in return statement
            RegisterScopedToken(node.Handle);

            // CRITICAL: Invalidate source during scope - prevent concurrent access
            // The source should not be accessible while the Hijacked<T> token exists
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceIdent.Name, "hijacking");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits an observing statement node (thread-safe scoped read access).
    /// Syntax: observing &lt;handle&gt; from &lt;source&gt;: { ... }
    /// Creates a temporary Observed&lt;T&gt; handle with shared read lock.
    /// IMPORTANT: Only works with Shared&lt;T, MultiReadLock&gt;, not Shared&lt;T, Mutex&gt;.
    /// </summary>
    public object? VisitObservingStatement(ObservingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot observe expression with unknown type",
                location: node.Location);
            return null;
        }

        // Extract the source object to check its policy
        if (node.Source is IdentifierExpression sourceId)
        {
            MemoryObject? sourceObj = _memoryAnalyzer.GetObject(name: sourceId.Name);
            if (sourceObj != null)
            {
                // COMPILE-TIME CHECK: observing requires MultiReadLock policy
                if (sourceObj.Wrapper == WrapperType.Shared &&
                    sourceObj.Policy != LockingPolicy.MultiReadLock)
                {
                    AddError(
                        message: $"observing requires Shared<T, MultiReadLock>. " +
                                 $"Object '{sourceId.Name}' has policy {sourceObj.Policy}. " +
                                 $"Use seizing for exclusive access, or create with MultiReadLock policy.",
                        location: node.Location);
                    return null;
                }
            }
        }

        // Create a new scope for the observing block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create an Observed<T> type for the handle (not Witnessed<T>)
            var observedType = new TypeInfo(Name: $"Observed<{sourceType.Name}>",
                IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: observedType,
                Visibility: VisibilityModifier.Private, IsMutable: false);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope",
                    location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            RegisterScopedToken(node.Handle);

            // CRITICAL: Acquire read lock on source - invalidate during lock
            // The source Shared<T, MultiReadLock> must have its read lock acquired
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceIdent.Name, "observing");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - read lock released, handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits a seizing statement node (thread-safe scoped exclusive access).
    /// Syntax: seizing &lt;handle&gt; from &lt;source&gt;: { ... }
    /// Creates a temporary Seized&lt;T&gt; handle with exclusive write lock.
    /// Works with both Vault&lt;T, Mutex&gt; and Vault&lt;T, MultiReadLock&gt;.
    /// </summary>
    public object? VisitSeizingStatement(SeizingStatement node)
    {
        // Evaluate the source expression to get its type
        var sourceType = node.Source.Accept(visitor: this) as TypeInfo;

        if (sourceType == null)
        {
            AddError(message: "Cannot seize expression with unknown type",
                location: node.Location);
            return null;
        }

        // Create a new scope for the seizing block
        _symbolTable.EnterScope();
        _memoryAnalyzer.EnterScope();
        _scopeDepth++;

        try
        {
            // Create a Seized<T> type for the handle
            var seizedType = new TypeInfo(Name: $"Seized<{sourceType.Name}>", IsReference: true);

            // Declare the handle variable in the scope
            var handleSymbol = new VariableSymbol(Name: node.Handle, Type: seizedType,
                Visibility: VisibilityModifier.Private, IsMutable: true);

            if (!_symbolTable.TryDeclare(symbol: handleSymbol))
            {
                AddError(message: $"Handle '{node.Handle}' already declared in this scope",
                    location: node.Location);
            }

            // CRITICAL: Register this as a scoped token that cannot escape
            RegisterScopedToken(node.Handle);

            // CRITICAL: Acquire write lock on source - invalidate during lock
            // The source Shared<T, Policy> must have its write lock acquired
            if (node.Source is IdentifierExpression sourceIdent)
            {
                InvalidateSource(sourceIdent.Name, "seizing");
            }

            // Visit the body with the handle available
            node.Body.Accept(visitor: this);
        }
        finally
        {
            // Exit the scope - write lock released, handle goes out of scope, source is restored
            RestoreInvalidatedSources();
            ExitScopeCleanupTokens();
            _scopeDepth--;
            _memoryAnalyzer.ExitScope();
            _symbolTable.ExitScope();
        }

        return null;
    }

    /// <summary>
    /// Visits a named argument expression (name: value).
    /// Named arguments allow explicit specification of which parameter receives which value.
    /// </summary>
    public object? VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        // Type check the value expression
        return node.Value.Accept(visitor: this);
    }
}
