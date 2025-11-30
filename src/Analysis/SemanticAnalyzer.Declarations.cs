using Compilers.Shared.AST;
using Compilers.Shared.Lexer;

namespace Compilers.Shared.Analysis;

/// <summary>
/// Partial class containing declaration visitors (variable, function, class, struct, variant, etc.).
/// </summary>
public partial class SemanticAnalyzer
{
    /// <summary>
    /// Registers declarations from a prelude module into the symbol table.
    /// </summary>
    private void RegisterPreludeDeclarations(AST.Program ast)
    {
        foreach (IAstNode declaration in ast.Declarations)
        {
            if (declaration is FunctionDeclaration funcDecl)
            {
                var funcSymbol = new FunctionSymbol(Name: funcDecl.Name,
                    Parameters: funcDecl.Parameters,
                    ReturnType: ResolveType(typeExpr: funcDecl.ReturnType),
                    Visibility: funcDecl.Visibility,
                    GenericParameters: funcDecl.GenericParameters);
                _symbolTable.TryDeclare(symbol: funcSymbol);
            }
            else if (declaration is StructDeclaration structDecl)
            {
                var interfaceNames = structDecl.Interfaces
                                              ?.Select(selector: i => i.Name)
                                               .ToList();
                var structSymbol = new StructSymbol(Name: structDecl.Name,
                    Visibility: structDecl.Visibility,
                    GenericParameters: structDecl.GenericParameters,
                    Interfaces: interfaceNames);
                _symbolTable.TryDeclare(symbol: structSymbol);
            }
            else if (declaration is ClassDeclaration classDecl)
            {
                var classSymbol = new ClassSymbol(Name: classDecl.Name,
                    BaseClass: classDecl.BaseClass,
                    Interfaces: classDecl.Interfaces,
                    Visibility: classDecl.Visibility,
                    GenericParameters: classDecl.GenericParameters);
                _symbolTable.TryDeclare(symbol: classSymbol);
            }
            else if (declaration is FeatureDeclaration featureDecl)
            {
                var featureSymbol = new FeatureSymbol(Name: featureDecl.Name,
                    Visibility: featureDecl.Visibility,
                    GenericParameters: featureDecl.GenericParameters);
                _symbolTable.TryDeclare(symbol: featureSymbol);
            }
            else if (declaration is VariantDeclaration variantDecl)
            {
                var variantSymbol = new VariantSymbol(Name: variantDecl.Name,
                    Visibility: variantDecl.Visibility,
                    GenericParameters: variantDecl.GenericParameters);
                _symbolTable.TryDeclare(symbol: variantSymbol);
            }
        }
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
            if (IsInlineOnlyMethodCall(expr: node.Initializer, methodName: out string? methodName))
            {
                AddError(message: $"Cannot store result of '.{methodName}()' in a variable. " +
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

        var symbol = new VariableSymbol(Name: node.Name,
            Type: variableType,
            IsMutable: node.IsMutable,
            Visibility: node.Visibility);
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
        // Check for reserved function name prefixes (compiler-generated variants)
        if (node.Name.StartsWith(value: "try_"))
        {
            AddError(
                message:
                $"Function name '{node.Name}' uses reserved prefix 'try_'. This prefix is reserved for compiler-generated safe variants.",
                location: node.Location);
        }
        else if (node.Name.StartsWith(value: "check_"))
        {
            AddError(
                message:
                $"Function name '{node.Name}' uses reserved prefix 'check_'. This prefix is reserved for compiler-generated safe variants.",
                location: node.Location);
        }
        else if (node.Name.StartsWith(value: "find_"))
        {
            AddError(
                message:
                $"Function name '{node.Name}' uses reserved prefix 'find_'. This prefix is reserved for compiler-generated safe variants.",
                location: node.Location);
        }

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
                var paramSymbol = new VariableSymbol(Name: param.Name,
                    Type: paramType,
                    IsMutable: false,
                    Visibility: VisibilityModifier.Private);
                _symbolTable.TryDeclare(symbol: paramSymbol);

                // Register parameter objects in memory analyzer for ownership tracking
                // Parameters enter the function with appropriate wrapper types based on language
                if (paramType != null)
                {
                    _memoryAnalyzer.RegisterObject(name: param.Name,
                        type: paramType,
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
        var funcSymbol = new FunctionSymbol(Name: node.Name,
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
        var classSymbol = new ClassSymbol(Name: node.Name,
            BaseClass: node.BaseClass,
            Interfaces: node.Interfaces,
            Visibility: node.Visibility);
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
        // Extract interface names for the symbol
        var interfaceNames = node.Interfaces
                                ?.Select(selector: i => i.Name)
                                 .ToList();

        var structSymbol = new StructSymbol(Name: node.Name,
            Visibility: node.Visibility,
            GenericParameters: node.GenericParameters,
            GenericConstraints: null,
            Interfaces: interfaceNames);
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
                _moduleResolver.LoadModuleWithDependencies(importPath: node.ModulePath);

            // Process each loaded module (dependencies first, then the requested module)
            foreach (ModuleResolver.ModuleInfo moduleInfo in modules)
            {
                ProcessImportedModule(moduleInfo: moduleInfo, importDecl: node);
            }

            return null;
        }
        catch (ModuleException ex)
        {
            AddError(message: $"Failed to import module '{node.ModulePath}': {ex.Message}",
                location: node.Location);
            return null;
        }
    }

    /// <summary>
    /// Processes an imported module by adding its symbols to the symbol table.
    /// </summary>
    private void ProcessImportedModule(ModuleResolver.ModuleInfo moduleInfo,
        ImportDeclaration importDecl)
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
                var funcSymbol = new FunctionSymbol(Name: funcDecl.Name,
                    Parameters: funcDecl.Parameters,
                    ReturnType: funcDecl.ReturnType != null
                        ? ResolveTypeExpression(typeExpr: funcDecl.ReturnType)
                        : new TypeInfo(Name: "void", IsReference: false),
                    Visibility: funcDecl.Visibility,
                    IsUsurping: false,
                    GenericParameters: funcDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>());

                if (!_symbolTable.TryDeclare(symbol: funcSymbol))
                {
                    AddError(
                        message:
                        $"Imported symbol '{funcDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is ClassDeclaration classDecl)
            {
                // Create class/entity symbol
                var classSymbol = new ClassSymbol(Name: classDecl.Name,
                    BaseClass: null,
                    Interfaces: new List<TypeExpression>(),
                    Visibility: classDecl.Visibility,
                    GenericParameters: classDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>());

                if (!_symbolTable.TryDeclare(symbol: classSymbol))
                {
                    AddError(
                        message:
                        $"Imported type '{classDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is StructDeclaration structDecl)
            {
                // Create struct/record symbol with interfaces
                var interfaceNames = structDecl.Interfaces
                                              ?.Select(selector: i => i.Name)
                                               .ToList();
                var structSymbol = new StructSymbol(Name: structDecl.Name,
                    Visibility: structDecl.Visibility,
                    GenericParameters: structDecl.GenericParameters,
                    GenericConstraints: null,
                    Interfaces: interfaceNames);

                if (!_symbolTable.TryDeclare(symbol: structSymbol))
                {
                    AddError(
                        message:
                        $"Imported type '{structDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is VariantDeclaration variantDecl)
            {
                // Create variant symbol (chimera/variant/mutant)
                var variantSymbol = new ClassSymbol(Name: variantDecl.Name,
                    BaseClass: null,
                    Interfaces: new List<TypeExpression>(),
                    Visibility: variantDecl.Visibility,
                    GenericParameters: variantDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>());

                if (!_symbolTable.TryDeclare(symbol: variantSymbol))
                {
                    AddError(
                        message:
                        $"Imported type '{variantDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            else if (declaration is ExternalDeclaration externalDecl)
            {
                // Create function symbol for external declaration with calling convention info
                TypeInfo? returnType = externalDecl.ReturnType != null
                    ? ResolveTypeExpression(typeExpr: externalDecl.ReturnType)
                    : new TypeInfo(Name: "void", IsReference: false);

                var funcSymbol = new FunctionSymbol(Name: externalDecl.Name,
                    Parameters: externalDecl.Parameters,
                    ReturnType: returnType,
                    Visibility: VisibilityModifier.External,
                    IsUsurping: false,
                    GenericParameters: externalDecl.GenericParameters,
                    GenericConstraints: new List<GenericConstraint>(),
                    CallingConvention: externalDecl.CallingConvention,
                    IsExternal: true);

                if (!_symbolTable.TryDeclare(symbol: funcSymbol))
                {
                    AddError(
                        message:
                        $"Imported external function '{externalDecl.Name}' conflicts with existing declaration",
                        location: importDecl.Location);
                }
            }
            // TODO: Handle other declaration types (FeatureDeclaration, MenuDeclaration, etc.)
        }
    }

    /// <summary>
    /// Visits a namespace declaration that establishes the module path for the file.
    /// </summary>
    /// <param name="node">Namespace declaration node</param>
    /// <returns>Null</returns>
    public object? VisitNamespaceDeclaration(NamespaceDeclaration node)
    {
        // Namespace declarations establish the module path for symbol resolution
        // This is handled at a higher level during module loading
        return null;
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
}
