namespace Compilers.Shared.AST;

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
    T VisitLiteralExpression(LiteralExpression node);
    T VisitIdentifierExpression(IdentifierExpression node);
    T VisitBinaryExpression(BinaryExpression node);
    T VisitUnaryExpression(UnaryExpression node);
    T VisitCallExpression(CallExpression node);
    T VisitMemberExpression(MemberExpression node);
    T VisitIndexExpression(IndexExpression node);
    T VisitConditionalExpression(ConditionalExpression node);
    T VisitChainedComparisonExpression(ChainedComparisonExpression node);
    T VisitRangeExpression(RangeExpression node);
    T VisitLambdaExpression(LambdaExpression node);
    T VisitTypeExpression(TypeExpression node);
    T VisitTypeConversionExpression(TypeConversionExpression node);

    // Memory slice expression visitor methods
    T VisitSliceConstructorExpression(SliceConstructorExpression node);
    T VisitGenericMethodCallExpression(GenericMethodCallExpression node);
    T VisitGenericMemberExpression(GenericMemberExpression node);
    T VisitMemoryOperationExpression(MemoryOperationExpression node);

    // Statement visitor methods - handle all statement node types
    T VisitExpressionStatement(ExpressionStatement node);
    T VisitDeclarationStatement(DeclarationStatement node);
    T VisitAssignmentStatement(AssignmentStatement node);
    T VisitReturnStatement(ReturnStatement node);
    T VisitIfStatement(IfStatement node);
    T VisitWhileStatement(WhileStatement node);
    T VisitForStatement(ForStatement node);
    T VisitBlockStatement(BlockStatement node);
    T VisitWhenStatement(WhenStatement node);
    T VisitBreakStatement(BreakStatement node);
    T VisitContinueStatement(ContinueStatement node);
    T VisitDangerStatement(DangerStatement node);
    T VisitMayhemStatement(MayhemStatement node);

    // Declaration visitor methods - handle all declaration node types
    T VisitVariableDeclaration(VariableDeclaration node);
    T VisitFunctionDeclaration(FunctionDeclaration node);
    T VisitClassDeclaration(ClassDeclaration node);
    T VisitStructDeclaration(StructDeclaration node);
    T VisitMenuDeclaration(MenuDeclaration node);
    T VisitVariantDeclaration(VariantDeclaration node);
    T VisitFeatureDeclaration(FeatureDeclaration node);
    T VisitImplementationDeclaration(ImplementationDeclaration node);
    T VisitImportDeclaration(ImportDeclaration node);
    T VisitRedefinitionDeclaration(RedefinitionDeclaration node);
    T VisitUsingDeclaration(UsingDeclaration node);
    T VisitExternalDeclaration(ExternalDeclaration node);

    // Program visitor method - handle the root program node
    T VisitProgram(Program node);
}

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
