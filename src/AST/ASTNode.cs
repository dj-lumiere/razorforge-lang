namespace Compilers.Shared.AST;

#region Base Interfaces and Types

/// <summary>
/// Base interface for all Abstract Syntax Tree (AST) nodes.
/// Provides common functionality for source location tracking and visitor pattern support.
/// </summary>
/// <remarks>
/// All nodes in the AST implement this interface to ensure:
/// <list type="bullet">
/// <item>Source location preservation for error reporting and IDE features</item>
/// <item>Visitor pattern support for traversal and transformation</item>
/// <item>Consistent API across all node types</item>
/// </list>
/// </remarks>
public interface IAstNode
{
    /// <summary>
    /// Gets the source location where this AST node was parsed from.
    /// Used for error reporting, debugging, and IDE integration features.
    /// </summary>
    SourceLocation Location { get; }

    /// <summary>
    /// Accepts a visitor for AST traversal, analysis, or transformation.
    /// Implements the visitor pattern to enable extensible operations on the AST.
    /// </summary>
    /// <typeparam name="T">Return type of the visitor operation</typeparam>
    /// <param name="visitor">Visitor instance that will process this node</param>
    /// <returns>Result of the visitor operation, type determined by visitor implementation</returns>
    T Accept<T>(IAstVisitor<T> visitor);
}

/// <summary>
/// Immutable record containing source code location information for AST nodes.
/// Enables precise error reporting, debugging, and IDE integration features.
/// </summary>
/// <param name="Line">1-based line number in the source file</param>
/// <param name="Column">1-based column number within the line</param>
/// <param name="Position">0-based absolute character position in the source text</param>
/// <remarks>
/// Location information is preserved throughout the compilation pipeline:
/// <list type="bullet">
/// <item>Lexical analysis: attached to tokens during scanning</item>
/// <item>Parsing: propagated to AST nodes during parse tree construction</item>
/// <item>Semantic analysis: used for precise error reporting</item>
/// <item>Code generation: enables source maps and debugging info</item>
/// </list>
/// </remarks>
public record SourceLocation(int Line, int Column, int Position);

/// <summary>
/// Abstract base implementation for all AST nodes in the compiler.
/// Provides common functionality and enforces consistent interface implementation.
/// </summary>
/// <param name="Location">Source location information for this node</param>
/// <remarks>
/// This abstract record serves as the foundation for all AST nodes:
/// <list type="bullet">
/// <item>Implements IAstNode interface consistently</item>
/// <item>Provides immutable record semantics for structural equality</item>
/// <item>Ensures every node tracks its source location</item>
/// <item>Forces derived classes to implement visitor pattern</item>
/// </list>
/// </remarks>
public abstract record AstNode(SourceLocation Location) : IAstNode
{
    /// <summary>
    /// Abstract method that must be implemented by all concrete AST node types.
    /// Routes the visitor to the appropriate visit method for this node type.
    /// </summary>
    /// <typeparam name="T">Return type of the visitor operation</typeparam>
    /// <param name="visitor">Visitor instance to accept</param>
    /// <returns>Result of the appropriate visitor method</returns>
    public abstract T Accept<T>(IAstVisitor<T> visitor);
}

#endregion

#region Visitor Pattern

/// <summary>
/// Visitor pattern interface for extensible AST operations.
/// Enables implementation of various compiler passes, analyzers, and transformations
/// without modifying the AST node classes themselves.
/// </summary>
/// <typeparam name="T">Return type for visitor methods, enabling flexible operation results</typeparam>
/// <remarks>
/// The visitor pattern provides several benefits:
/// <list type="bullet">
/// <item>Extensibility: new operations can be added without changing AST classes</item>
/// <item>Separation of concerns: keeps operation logic separate from data structure</item>
/// <item>Type safety: compile-time checking ensures all node types are handled</item>
/// <item>Consistency: uniform interface for all AST traversal operations</item>
/// </list>
///
/// Typical implementations include:
/// <list type="bullet">
/// <item>Code generators (LLVM IR, JavaScript, C++)</item>
/// <item>Semantic analyzers (type checking, scope resolution)</item>
/// <item>Pretty printers and formatters</item>
/// <item>Optimization passes and transformations</item>
/// </list>
/// </remarks>
public interface IAstVisitor<T>
{
    // Expression visitor methods - handle all expression node types

    /// <summary>Visits a literal expression node (constants like 42, "hello", true)</summary>
    /// <param name="node">The literal expression to visit</param>
    /// <returns>Result of visiting the literal expression</returns>
    T VisitLiteralExpression(LiteralExpression node);

    /// <summary>Visits a list literal expression node [1, 2, 3]</summary>
    /// <param name="node">The list literal to visit</param>
    /// <returns>Result of visiting the list literal</returns>
    T VisitListLiteralExpression(ListLiteralExpression node);

    /// <summary>Visits a set literal expression node {1, 2, 3}</summary>
    /// <param name="node">The set literal to visit</param>
    /// <returns>Result of visiting the set literal</returns>
    T VisitSetLiteralExpression(SetLiteralExpression node);

    /// <summary>Visits a dict literal expression node {k: v}</summary>
    /// <param name="node">The dict literal to visit</param>
    /// <returns>Result of visiting the dict literal</returns>
    T VisitDictLiteralExpression(DictLiteralExpression node);

    /// <summary>Visits an identifier expression node (variable/function references)</summary>
    /// <param name="node">The identifier expression to visit</param>
    /// <returns>Result of visiting the identifier expression</returns>
    T VisitIdentifierExpression(IdentifierExpression node);

    /// <summary>Visits a binary expression node (operations like a + b, x == y)</summary>
    /// <param name="node">The binary expression to visit</param>
    /// <returns>Result of visiting the binary expression</returns>
    T VisitBinaryExpression(BinaryExpression node);

    /// <summary>Visits a unary expression node (operations like -x, !condition)</summary>
    /// <param name="node">The unary expression to visit</param>
    /// <returns>Result of visiting the unary expression</returns>
    T VisitUnaryExpression(UnaryExpression node);

    /// <summary>Visits a call expression node (function/method invocations)</summary>
    /// <param name="node">The call expression to visit</param>
    /// <returns>Result of visiting the call expression</returns>
    T VisitCallExpression(CallExpression node);

    /// <summary>Visits a named argument expression node (name: value syntax in calls)</summary>
    /// <param name="node">The named argument expression to visit</param>
    /// <returns>Result of visiting the named argument expression</returns>
    T VisitNamedArgumentExpression(NamedArgumentExpression node);

    /// <summary>Visits a struct literal expression node (Type { field: value } syntax)</summary>
    /// <param name="node">The struct literal expression to visit</param>
    /// <returns>Result of visiting the struct literal expression</returns>
    T VisitStructLiteralExpression(StructLiteralExpression node);

    /// <summary>Visits a member expression node (property/field access like obj.field)</summary>
    /// <param name="node">The member expression to visit</param>
    /// <returns>Result of visiting the member expression</returns>
    T VisitMemberExpression(MemberExpression node);

    /// <summary>Visits an index expression node (array/collection access like arr[0])</summary>
    /// <param name="node">The index expression to visit</param>
    /// <returns>Result of visiting the index expression</returns>
    T VisitIndexExpression(IndexExpression node);

    /// <summary>Visits a conditional expression node (ternary operator: condition ? true : false)</summary>
    /// <param name="node">The conditional expression to visit</param>
    /// <returns>Result of visiting the conditional expression</returns>
    T VisitConditionalExpression(ConditionalExpression node);

    /// <summary>Visits a block expression node ({ expr })</summary>
    /// <param name="node">The block expression to visit</param>
    /// <returns>Result of visiting the block expression</returns>
    T VisitBlockExpression(BlockExpression node);

    /// <summary>Visits a chained comparison expression node (like 1 &lt; x &lt; 10)</summary>
    /// <param name="node">The chained comparison expression to visit</param>
    /// <returns>Result of visiting the chained comparison expression</returns>
    T VisitChainedComparisonExpression(ChainedComparisonExpression node);

    /// <summary>Visits a range expression node (sequences like 0 to 10)</summary>
    /// <param name="node">The range expression to visit</param>
    /// <returns>Result of visiting the range expression</returns>
    T VisitRangeExpression(RangeExpression node);

    /// <summary>Visits a lambda expression node (anonymous functions like (x) => x + 1)</summary>
    /// <param name="node">The lambda expression to visit</param>
    /// <returns>Result of visiting the lambda expression</returns>
    T VisitLambdaExpression(LambdaExpression node);

    /// <summary>Visits a type expression node (type references like s32, Array[text])</summary>
    /// <param name="node">The type expression to visit</param>
    /// <returns>Result of visiting the type expression</returns>
    T VisitTypeExpression(TypeExpression node);

    /// <summary>Visits a type conversion expression node (explicit casts like s32!(x))</summary>
    /// <param name="node">The type conversion expression to visit</param>
    /// <returns>Result of visiting the type conversion expression</returns>
    T VisitTypeConversionExpression(TypeConversionExpression node);

    // Memory slice expression visitor methods

    /// <summary>Visits a slice constructor expression node (DynamicSlice/TemporarySlice creation)</summary>
    /// <param name="node">The slice constructor expression to visit</param>
    /// <returns>Result of visiting the slice constructor expression</returns>
    T VisitSliceConstructorExpression(SliceConstructorExpression node);

    /// <summary>Visits a generic method call expression node (methods with type parameters)</summary>
    /// <param name="node">The generic method call expression to visit</param>
    /// <returns>Result of visiting the generic method call expression</returns>
    T VisitGenericMethodCallExpression(GenericMethodCallExpression node);

    /// <summary>Visits a generic member expression node (members with type parameters)</summary>
    /// <param name="node">The generic member expression to visit</param>
    /// <returns>Result of visiting the generic member expression</returns>
    T VisitGenericMemberExpression(GenericMemberExpression node);

    /// <summary>Visits a memory operation expression node (operations with ! syntax)</summary>
    /// <param name="node">The memory operation expression to visit</param>
    /// <returns>Result of visiting the memory operation expression</returns>
    T VisitMemoryOperationExpression(MemoryOperationExpression node);

    /// <summary>Visits an intrinsic call expression node (@intrinsic.* operations)</summary>
    /// <param name="node">The intrinsic call expression to visit</param>
    /// <returns>Result of visiting the intrinsic call expression</returns>
    T VisitIntrinsicCallExpression(IntrinsicCallExpression node);

    /// <summary>Visits a native call expression node (@native.* function calls)</summary>
    /// <param name="node">The native call expression to visit</param>
    /// <returns>Result of visiting the native call expression</returns>
    T VisitNativeCallExpression(NativeCallExpression node);

    // Statement visitor methods - handle all statement node types

    /// <summary>Visits an expression statement node (expressions executed for side effects)</summary>
    /// <param name="node">The expression statement to visit</param>
    /// <returns>Result of visiting the expression statement</returns>
    T VisitExpressionStatement(ExpressionStatement node);

    /// <summary>Visits a declaration statement node (declarations in statement context)</summary>
    /// <param name="node">The declaration statement to visit</param>
    /// <returns>Result of visiting the declaration statement</returns>
    T VisitDeclarationStatement(DeclarationStatement node);

    /// <summary>Visits an assignment statement node (value assignments like x = 42)</summary>
    /// <param name="node">The assignment statement to visit</param>
    /// <returns>Result of visiting the assignment statement</returns>
    T VisitAssignmentStatement(AssignmentStatement node);

    /// <summary>Visits a return statement node (function return with optional value)</summary>
    /// <param name="node">The return statement to visit</param>
    /// <returns>Result of visiting the return statement</returns>
    T VisitReturnStatement(ReturnStatement node);

    /// <summary>Visits a throw statement node (error return via Result<T>)</summary>
    /// <param name="node">The throw statement to visit</param>
    /// <returns>Result of visiting the throw statement</returns>
    T VisitThrowStatement(ThrowStatement node);

    /// <summary>Visits an absent statement node (value not found, triggers Lookup<T>)</summary>
    /// <param name="node">The absent statement to visit</param>
    /// <returns>Result of visiting the absent statement</returns>
    T VisitAbsentStatement(AbsentStatement node);

    /// <summary>Visits an if statement node (conditional branching)</summary>
    /// <param name="node">The if statement to visit</param>
    /// <returns>Result of visiting the if statement</returns>
    T VisitIfStatement(IfStatement node);

    /// <summary>Visits a while statement node (pre-condition loop)</summary>
    /// <param name="node">The while statement to visit</param>
    /// <returns>Result of visiting the while statement</returns>
    T VisitWhileStatement(WhileStatement node);

    /// <summary>Visits a for statement node (iterator loop over collections)</summary>
    /// <param name="node">The for statement to visit</param>
    /// <returns>Result of visiting the for statement</returns>
    T VisitForStatement(ForStatement node);

    /// <summary>Visits a block statement node (grouped statements with lexical scope)</summary>
    /// <param name="node">The block statement to visit</param>
    /// <returns>Result of visiting the block statement</returns>
    T VisitBlockStatement(BlockStatement node);

    /// <summary>Visits a when statement node (pattern matching statement)</summary>
    /// <param name="node">The when statement to visit</param>
    /// <returns>Result of visiting the when statement</returns>
    T VisitWhenStatement(WhenStatement node);

    /// <summary>Visits a break statement node (exits innermost loop)</summary>
    /// <param name="node">The break statement to visit</param>
    /// <returns>Result of visiting the break statement</returns>
    T VisitBreakStatement(BreakStatement node);

    /// <summary>Visits a continue statement node (skips to next loop iteration)</summary>
    /// <param name="node">The continue statement to visit</param>
    /// <returns>Result of visiting the continue statement</returns>
    T VisitContinueStatement(ContinueStatement node);

    /// <summary>Visits a danger statement node (unsafe block for low-level operations)</summary>
    /// <param name="node">The danger statement to visit</param>
    /// <returns>Result of visiting the danger statement</returns>
    T VisitDangerStatement(DangerStatement node);

    /// <summary>Visits a mayhem statement node (ultimate unsafe block for runtime modifications)</summary>
    /// <param name="node">The mayhem statement to visit</param>
    /// <returns>Result of visiting the mayhem statement</returns>
    T VisitMayhemStatement(MayhemStatement node);

    /// <summary>Visits a viewing statement node (scoped read-only access)</summary>
    /// <param name="node">The viewing statement to visit</param>
    /// <returns>Result of visiting the viewing statement</returns>
    T VisitViewingStatement(ViewingStatement node);

    /// <summary>Visits a hijacking statement node (scoped exclusive access)</summary>
    /// <param name="node">The hijacking statement to visit</param>
    /// <returns>Result of visiting the hijacking statement</returns>
    T VisitHijackingStatement(HijackingStatement node);

    /// <summary>Visits an observing statement node (thread-safe scoped read access)</summary>
    /// <param name="node">The observing statement to visit</param>
    /// <returns>Result of visiting the observing statement</returns>
    T VisitObservingStatement(ObservingStatement node);

    /// <summary>Visits a seizing statement node (thread-safe scoped exclusive access)</summary>
    /// <param name="node">The seizing statement to visit</param>
    /// <returns>Result of visiting the seizing statement</returns>
    T VisitSeizingStatement(SeizingStatement node);

    // Declaration visitor methods - handle all declaration node types

    /// <summary>Visits a variable declaration node (var/let declarations)</summary>
    /// <param name="node">The variable declaration to visit</param>
    /// <returns>Result of visiting the variable declaration</returns>
    T VisitVariableDeclaration(VariableDeclaration node);

    /// <summary>Visits a function declaration node (routine/function definitions)</summary>
    /// <param name="node">The function declaration to visit</param>
    /// <returns>Result of visiting the function declaration</returns>
    T VisitFunctionDeclaration(FunctionDeclaration node);

    /// <summary>Visits a class declaration node (entity/class definitions)</summary>
    /// <param name="node">The class declaration to visit</param>
    /// <returns>Result of visiting the class declaration</returns>
    T VisitClassDeclaration(ClassDeclaration node);

    /// <summary>Visits a struct declaration node (record/struct definitions)</summary>
    /// <param name="node">The struct declaration to visit</param>
    /// <returns>Result of visiting the struct declaration</returns>
    T VisitStructDeclaration(StructDeclaration node);

    /// <summary>Visits a menu declaration node (option/enum definitions)</summary>
    /// <param name="node">The menu declaration to visit</param>
    /// <returns>Result of visiting the menu declaration</returns>
    T VisitMenuDeclaration(MenuDeclaration node);

    /// <summary>Visits a variant declaration node (tagged union/algebraic data type definitions)</summary>
    /// <param name="node">The variant declaration to visit</param>
    /// <returns>Result of visiting the variant declaration</returns>
    T VisitVariantDeclaration(VariantDeclaration node);

    /// <summary>Visits a feature declaration node (trait/interface definitions)</summary>
    /// <param name="node">The feature declaration to visit</param>
    /// <returns>Result of visiting the feature declaration</returns>
    T VisitFeatureDeclaration(FeatureDeclaration node);

    /// <summary>Visits an implementation declaration node (trait/inherent implementations)</summary>
    /// <param name="node">The implementation declaration to visit</param>
    /// <returns>Result of visiting the implementation declaration</returns>
    T VisitImplementationDeclaration(ImplementationDeclaration node);

    /// <summary>Visits an import declaration node (module imports)</summary>
    /// <param name="node">The import declaration to visit</param>
    /// <returns>Result of visiting the import declaration</returns>
    T VisitImportDeclaration(ImportDeclaration node);

    /// <summary>Visits a namespace declaration node (module path declaration)</summary>
    /// <param name="node">The namespace declaration to visit</param>
    /// <returns>Result of visiting the namespace declaration</returns>
    T VisitNamespaceDeclaration(NamespaceDeclaration node);

    /// <summary>Visits a redefinition declaration node (symbol aliasing)</summary>
    /// <param name="node">The redefinition declaration to visit</param>
    /// <returns>Result of visiting the redefinition declaration</returns>
    T VisitRedefinitionDeclaration(RedefinitionDeclaration node);

    /// <summary>Visits a using declaration node (type aliasing)</summary>
    /// <param name="node">The using declaration to visit</param>
    /// <returns>Result of visiting the using declaration</returns>
    T VisitUsingDeclaration(UsingDeclaration node);

    /// <summary>Visits an external declaration node (native function declarations)</summary>
    /// <param name="node">The external declaration to visit</param>
    /// <returns>Result of visiting the external declaration</returns>
    T VisitExternalDeclaration(ExternalDeclaration node);

    // Program visitor method - handle the root program node

    /// <summary>Visits the root program node (compilation unit)</summary>
    /// <param name="node">The program node to visit</param>
    /// <returns>Result of visiting the program</returns>
    T VisitProgram(Program node);
}

#endregion

#region Root Program Node

/// <summary>
/// Root node of the Abstract Syntax Tree representing a complete program or compilation unit.
/// Contains all top-level declarations and serves as the entry point for AST operations.
/// </summary>
/// <param name="Declarations">List of all top-level declarations in the program (functions, classes, variables, imports)</param>
/// <param name="Location">Source location information (typically represents the entire file)</param>
/// <remarks>
/// The Program node is the root of the AST hierarchy:
/// <list type="bullet">
/// <item>Entry point: all AST traversals begin at the Program node</item>
/// <item>Compilation unit: represents a single source file or module</item>
/// <item>Declaration container: holds all top-level program constructs</item>
/// <item>Global scope: establishes the outermost lexical scope</item>
/// </list>
///
/// Typical program structure:
/// <list type="bullet">
/// <item>Import declarations for external dependencies</item>
/// <item>Type declarations (classes, structs, enums, traits)</item>
/// <item>Function and variable declarations</item>
/// <item>Implementation blocks for traits</item>
/// </list>
/// </remarks>
public record Program(List<IAstNode> Declarations, SourceLocation Location)
    : AstNode(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitProgram(node: this);
    }
}

#endregion
