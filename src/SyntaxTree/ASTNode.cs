namespace SyntaxTree;

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
public interface ISyntaxTreeNode
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
    T Accept<T>(ISyntaxTreeVisitor<T> visitor);
}

/// <summary>
/// Immutable record containing source code location information for AST nodes.
/// Enables precise error reporting, debugging, and IDE integration features.
/// </summary>
/// <param name="Line">1-based line number in the source file</param>
/// <param name="Column">1-based column number within the line</param>
/// <param name="Position">0-based absolute character position in the source text</param>
/// <param name="FileName">Source file name for the location.</param>
/// <remarks>
/// Location information is preserved throughout the build pipeline:
/// <list type="bullet">
/// <item>Lexical analysis: attached to tokens during scanning</item>
/// <item>Parsing: propagated to AST nodes during parse tree construction</item>
/// <item>Semantic analysis: used for precise error reporting</item>
/// <item>Code generation: enables source maps and debugging info</item>
/// </list>
/// </remarks>
public record SourceLocation(string FileName, int Line, int Column, int Position);

/// <summary>
/// Abstract base implementation for all AST nodes in the builder.
/// Provides common functionality and enforces consistent interface implementation.
/// </summary>
/// <param name="Location">Source location information for this node</param>
/// <remarks>
/// This abstract record serves as the foundation for all AST nodes:
/// <list type="bullet">
/// <item>Implements ISyntaxTreeNode interface consistently</item>
/// <item>Provides immutable record semantics for structural equality</item>
/// <item>Ensures every node tracks its source location</item>
/// <item>Forces derived classes to implement visitor pattern</item>
/// </list>
/// </remarks>
public abstract record SyntaxTreeNode(SourceLocation Location) : ISyntaxTreeNode
{
    /// <summary>
    /// Abstract memberRoutine that must be implemented by all concrete AST node types.
    /// Routes the visitor to the appropriate visit memberRoutine for this node type.
    /// </summary>
    /// <typeparam name="T">Return type of the visitor operation</typeparam>
    /// <param name="visitor">Visitor instance to accept</param>
    /// <returns>Result of the appropriate visitor memberRoutine</returns>
    public abstract T Accept<T>(ISyntaxTreeVisitor<T> visitor);
}

#endregion

#region Visitor Pattern

/// <summary>
/// Visitor pattern interface for extensible AST operations.
/// Enables implementation of various builder passes, analyzers, and transformations
/// without modifying the AST node classes themselves.
/// </summary>
/// <typeparam name="T">Return type for visitor memberRoutines, enabling flexible operation results</typeparam>
/// <remarks>
/// The visitor pattern provides several benefits:
/// <list type="bullet">
/// <item>Extensibility: new operations can be added without changing AST classes</item>
/// <item>Separation of concerns: keeps operation logic separate from data structure</item>
/// <item>Type safety: build-time checking ensures all node types are handled</item>
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
public interface ISyntaxTreeVisitor<out T>
{
    // Expression visitor memberRoutines - handle all expression node types

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

    /// <summary>Visits a tuple literal expression node (1, 2, 3) or (x,)</summary>
    /// <param name="node">The tuple literal to visit</param>
    /// <returns>Result of visiting the tuple literal</returns>
    T VisitTupleLiteralExpression(TupleLiteralExpression node);

    /// <summary>Visits an identifier expression node (variable/function references)</summary>
    /// <param name="node">The identifier expression to visit</param>
    /// <returns>Result of visiting the identifier expression</returns>
    T VisitIdentifierExpression(IdentifierExpression node);

    /// <summary>Visits a compound assignment expression node (operations like a += b)</summary>
    /// <param name="node">The compound assignment expression to visit</param>
    /// <returns>Result of visiting the compound assignment expression</returns>
    T VisitCompoundAssignmentExpression(CompoundAssignmentExpression node);

    /// <summary>Visits a binary expression node (operations like a + b, x == y)</summary>
    /// <param name="node">The binary expression to visit</param>
    /// <returns>Result of visiting the binary expression</returns>
    T VisitBinaryExpression(BinaryExpression node);

    /// <summary>Visits a unary expression node (operations like -x, not condition)</summary>
    /// <param name="node">The unary expression to visit</param>
    /// <returns>Result of visiting the unary expression</returns>
    T VisitUnaryExpression(UnaryExpression node);

    /// <summary>Visits a call expression node (function/memberRoutine invocations)</summary>
    /// <param name="node">The call expression to visit</param>
    /// <returns>Result of visiting the call expression</returns>
    T VisitCallExpression(CallExpression node);

    /// <summary>Visits a named argument expression node (name: value syntax in calls)</summary>
    /// <param name="node">The named argument expression to visit</param>
    /// <returns>Result of visiting the named argument expression</returns>
    T VisitNamedArgumentExpression(NamedArgumentExpression node);

    /// <summary>Visits a dict entry literal expression node (key:value pair in collection constructor)</summary>
    /// <param name="node">The dict entry literal expression to visit</param>
    /// <returns>Result of visiting the dict entry literal expression</returns>
    T VisitDictEntryLiteralExpression(DictEntryLiteralExpression node);

    /// <summary>Visits a creator expression node (Type(memberVar: value) syntax)</summary>
    /// <param name="node">The creator expression to visit</param>
    /// <returns>Result of visiting the creator expression</returns>
    T VisitCreatorExpression(CreatorExpression node);

    /// <summary>Visits a with expression node (functional update like value with (memberVar: newVal))</summary>
    /// <param name="node">The with expression to visit</param>
    /// <returns>Result of visiting the with expression</returns>
    T VisitWithExpression(WithExpression node);

    /// <summary>Visits a member expression node (member variable access like obj.memberVar)</summary>
    /// <param name="node">The member expression to visit</param>
    /// <returns>Result of visiting the member expression</returns>
    T VisitMemberExpression(MemberExpression node);

    /// <summary>Visits a comptime splice expression node (${expr})</summary>
    /// <param name="node">The splice expression to visit</param>
    /// <returns>Result of visiting the splice expression</returns>
    T VisitSpliceExpression(SpliceExpression node);

    /// <summary>Visits a splice-selector member access node (x.${...})</summary>
    /// <param name="node">The splice member expression to visit</param>
    /// <returns>Result of visiting the splice member expression</returns>
    T VisitSpliceMemberExpression(SpliceMemberExpression node);

    /// <summary>Visits an optional member expression node (safe navigation like obj?.memberVar)</summary>
    /// <param name="node">The optional member expression to visit</param>
    /// <returns>Result of visiting the optional member expression</returns>
    T VisitOptionalMemberExpression(OptionalMemberExpression node);

    /// <summary>Visits an index expression node (array/collection access like arr[0])</summary>
    /// <param name="node">The index expression to visit</param>
    /// <returns>Result of visiting the index expression</returns>
    T VisitIndexExpression(IndexExpression node);

    /// <summary>Visits a conditional expression node (ternary operator: if condition then true else false)</summary>
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

    /// <summary>Visits a generic memberRoutine call expression node (memberRoutines with type parameters)</summary>
    /// <param name="node">The generic memberRoutine call expression to visit</param>
    /// <returns>Result of visiting the generic memberRoutine call expression</returns>
    T VisitGenericMemberRoutineCallExpression(GenericMemberRoutineCallExpression node);

    /// <summary>Visits a generic member expression node (members with type parameters)</summary>
    /// <param name="node">The generic member expression to visit</param>
    /// <returns>Result of visiting the generic member expression</returns>
    T VisitGenericMemberExpression(GenericMemberExpression node);

    /// <summary>Visits an unclassified bracket-access node (must be lowered by BracketReclassifyPass)</summary>
    /// <param name="node">The bracket-access expression to visit</param>
    /// <returns>Result of visiting the bracket-access expression</returns>
    /// <remarks>
    /// This node is a transient parser output; the reclassify pass rewrites it into an
    /// IndexExpression / GenericMemberRoutineCallExpression / GenericMemberExpression before analysis.
    /// Any visitor reaching it indicates the reclassify pass was skipped.
    /// </remarks>
    T VisitBracketAccessExpression(BracketAccessExpression node);

    /// <summary>Visits a type-id expression node (compile-time FNV-1a type_id constant)</summary>
    /// <param name="node">The type-id expression to visit</param>
    /// <returns>Result of visiting the type-id expression</returns>
    T VisitTypeIdExpression(TypeIdExpression node);

    /// <summary>Visits a carrier-payload expression node (extracts and reinterprets Result/Lookup payload)</summary>
    /// <param name="node">The carrier-payload expression to visit</param>
    /// <returns>Result of visiting the carrier-payload expression</returns>
    T VisitCarrierPayloadExpression(CarrierPayloadExpression node);

    /// <summary>Visits a crashable-dispatch expression node (runtime type_id dispatch of a Crashable member)</summary>
    /// <param name="node">The crashable-dispatch expression to visit</param>
    /// <returns>Result of visiting the crashable-dispatch expression</returns>
    T VisitCrashableDispatchExpression(CrashableDispatchExpression node);

    /// <summary>Visits an is-pattern expression node (pattern matching like 'value is Point (x, y)')</summary>
    /// <param name="node">The is-pattern expression to visit</param>
    /// <returns>Result of visiting the is-pattern expression</returns>
    T VisitIsPatternExpression(IsPatternExpression node);

    /// <summary>Visits a flags test expression node (flags testing like 'perms is READ and WRITE')</summary>
    /// <param name="node">The flags test expression to visit</param>
    /// <returns>Result of visiting the flags test expression</returns>
    T VisitFlagsTestExpression(FlagsTestExpression node);

    /// <summary>Visits a when expression node (pattern matching expression like 'when x { 0 => "zero", else => "many" }')</summary>
    /// <param name="node">The when expression to visit</param>
    /// <returns>Result of visiting the when expression</returns>
    T VisitWhenExpression(WhenExpression node);

    /// <summary>Visits a steal expression node (ownership transfer like 'steal node')</summary>
    /// <param name="node">The steal expression to visit</param>
    /// <returns>Result of visiting the steal expression</returns>
    /// <remarks>RazorForge only - transfers ownership and invalidates the source</remarks>
    T VisitStealExpression(StealExpression node);

    /// <summary>Visits a waitfor expression node (await async computation)</summary>
    /// <param name="node">The waitfor expression to visit</param>
    /// <returns>Result of visiting the waitfor expression</returns>
    /// <remarks>Used in suspended/threaded routines to await Task-like values</remarks>
    T VisitWaitforExpression(WaitforExpression node);

    /// <summary>Visits a dependent waitfor expression node (waitfor with task dependencies)</summary>
    /// <param name="node">The dependent waitfor expression to visit</param>
    /// <returns>Result of visiting the dependent waitfor expression</returns>
    /// <remarks>Used for declarative task dependency graphs with 'after' clause</remarks>
    T VisitDependentWaitforExpression(DependentWaitforExpression node);

    /// <summary>Visits a back index expression node (end-relative indexing like '^1')</summary>
    /// <param name="node">The back index expression to visit</param>
    /// <returns>Result of visiting the back index expression</returns>
    /// <remarks>^n creates an index that counts from the end of a sequence</remarks>
    T VisitBackIndexExpression(BackIndexExpression node);

    /// <summary>Visits an inserted text expression node (f-string like f"Hello, {name}!")</summary>
    /// <param name="node">The inserted text expression to visit</param>
    /// <returns>Result of visiting the inserted text expression</returns>
    T VisitInsertedTextExpression(InsertedTextExpression node);

    // Statement visitor memberRoutines - handle all statement node types

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

    /// <summary>Visits a record/entity destructuring statement node (var (memberVar, memberVar2) = expr)</summary>
    /// <param name="node">The destructuring statement to visit</param>
    /// <returns>Result of visiting the destructuring statement</returns>
    T VisitDestructuringStatement(DestructuringStatement node);

    /// <summary>Visits a return statement node (function return with optional value)</summary>
    /// <param name="node">The return statement to visit</param>
    /// <returns>Result of visiting the return statement</returns>
    T VisitReturnStatement(ReturnStatement node);
    /// <summary>Visits a becomes statement node (block result in multi-statement branches)</summary>
    /// <param name="node">The becomes statement to visit</param>
    /// <returns>Result of visiting the becomes statement</returns>
    T VisitBecomesStatement(BecomesStatement node);

    /// <summary>Visits a synthesized variant return statement (replaces throw/absent/return in variant bodies)</summary>
    /// <param name="node">The variant return statement to visit</param>
    /// <returns>Result of visiting the variant return statement</returns>
    T VisitVariantReturnStatement(VariantReturnStatement node);

    /// <summary>Visits a throw statement node (error throw, triggers Result&lt;T&gt; or Lookup&lt;T&gt;)</summary>
    /// <param name="node">The throw statement to visit</param>
    /// <returns>Result of visiting the throw statement</returns>
    T VisitThrowStatement(ThrowStatement node);

    /// <summary>Visits an absent statement node (value not found, triggers T? or Lookup&lt;T&gt;)</summary>
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

    /// <summary>Visits a loop statement node (infinite loop primitive)</summary>
    /// <param name="node">The loop statement to visit</param>
    /// <returns>Result of visiting the loop statement</returns>
    T VisitLoopStatement(LoopStatement node);

    /// <summary>Visits a for statement node (iterator loop over collections)</summary>
    /// <param name="node">The for statement to visit</param>
    /// <returns>Result of visiting the for statement</returns>
    T VisitEachStatement(EachStatement node);

    /// <summary>Visits a comptime member-expansion statement node (expand m in allmemvarof(T))</summary>
    /// <param name="node">The expand statement to visit</param>
    /// <returns>Result of visiting the expand statement</returns>
    T VisitExpandStatement(ExpandStatement node);

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

    /// <summary>Visits a using statement node (resource management)</summary>
    /// <param name="node">The using statement to visit</param>
    /// <returns>Result of visiting the using statement</returns>
    T VisitUsingStatement(UsingStatement node);

    /// <summary>Visits a discard statement node (explicit return value discard)</summary>
    /// <param name="node">The discard statement to visit</param>
    /// <returns>Result of visiting the discard statement</returns>
    T VisitDiscardStatement(DiscardStatement node);

    // Declaration visitor memberRoutines - handle all declaration node types

    /// <summary>Visits a variable declaration node (var declarations)</summary>
    /// <param name="node">The variable declaration to visit</param>
    /// <returns>Result of visiting the variable declaration</returns>
    T VisitVariableDeclaration(VariableDeclaration node);

    /// <summary>Visits a decl-position expand member-generation directive.</summary>
    /// <param name="node">The expand-member declaration to visit</param>
    /// <returns>Result of visiting the expand-member declaration</returns>
    T VisitExpandMemberDeclaration(ExpandMemberDeclaration node);

    /// <summary>Visits a function declaration node (routine/function definitions)</summary>
    /// <param name="node">The function declaration to visit</param>
    /// <returns>Result of visiting the function declaration</returns>
    T VisitFunctionDeclaration(RoutineDeclaration node);

    /// <summary>Visits a class declaration node (entity/class definitions)</summary>
    /// <param name="node">The class declaration to visit</param>
    /// <returns>Result of visiting the class declaration</returns>
    T VisitEntityDeclaration(EntityDeclaration node);

    /// <summary>Visits a struct declaration node (record/struct definitions)</summary>
    /// <param name="node">The struct declaration to visit</param>
    /// <returns>Result of visiting the struct declaration</returns>
    T VisitRecordDeclaration(RecordDeclaration node);

    /// <summary>Visits a menu declaration node (option/enum definitions)</summary>
    /// <param name="node">The menu declaration to visit</param>
    /// <returns>Result of visiting the menu declaration</returns>
    T VisitChoiceDeclaration(ChoiceDeclaration node);

    /// <summary>Visits a flags declaration node (combinable bitflag set definitions)</summary>
    /// <param name="node">The flags declaration to visit</param>
    /// <returns>Result of visiting the flags declaration</returns>
    T VisitFlagsDeclaration(FlagsDeclaration node);

    /// <summary>Visits a crashable declaration node (throwable error entity)</summary>
    /// <param name="node">The crashable declaration to visit</param>
    /// <returns>Result of visiting the crashable declaration</returns>
    T VisitCrashableDeclaration(CrashableDeclaration node);

    /// <summary>Visits a variant declaration node (tagged union/algebraic data type definitions)</summary>
    /// <param name="node">The variant declaration to visit</param>
    /// <returns>Result of visiting the variant declaration</returns>
    T VisitVariantDeclaration(VariantDeclaration node);

    /// <summary>Visits a feature declaration node (trait/interface definitions)</summary>
    /// <param name="node">The feature declaration to visit</param>
    /// <returns>Result of visiting the feature declaration</returns>
    T VisitProtocolDeclaration(ProtocolDeclaration node);

    /// <summary>Visits an import declaration node (module imports)</summary>
    /// <param name="node">The import declaration to visit</param>
    /// <returns>Result of visiting the import declaration</returns>
    T VisitImportDeclaration(ImportDeclaration node);

    /// <summary>Visits a module declaration node (module path declaration)</summary>
    /// <param name="node">The module declaration to visit</param>
    /// <returns>Result of visiting the module declaration</returns>
    T VisitModuleDeclaration(ModuleDeclaration node);

    /// <summary>Visits a redefinition declaration node (symbol aliasing, define A as B)</summary>
    /// <param name="node">The redefinition declaration to visit</param>
    /// <returns>Result of visiting the redefinition declaration</returns>
    T VisitDefineDeclaration(DefineDeclaration node);

    /// <summary>Visits an external declaration node (native function declarations)</summary>
    /// <param name="node">The external declaration to visit</param>
    /// <returns>Result of visiting the external declaration</returns>
    T VisitExternalDeclaration(ExternalDeclaration node);

    /// <summary>Visits an external block declaration (groups multiple external routines)</summary>
    /// <param name="node">The external block declaration to visit</param>
    /// <returns>Result of visiting the external block declaration</returns>
    T VisitExternalBlockDeclaration(ExternalBlockDeclaration node);

    /// <summary>Visits a preset declaration node (build-time constants)</summary>
    /// <param name="node">The preset declaration to visit</param>
    /// <returns>Result of visiting the preset declaration</returns>
    T VisitPresetDeclaration(PresetDeclaration node);

    /// <summary>Visits a pass statement node (empty placeholder statement)</summary>
    /// <param name="node">The pass statement to visit</param>
    /// <returns>Result of visiting the pass statement</returns>
    T VisitPassStatement(PassStatement node);

    // Program visitor memberRoutine - handle the root program node

    /// <summary>Visits the root program node (build unit)</summary>
    /// <param name="node">The program node to visit</param>
    /// <returns>Result of visiting the program</returns>
    T VisitProgram(Program node);
}

#endregion

#region Root Program Node

/// <summary>
/// Root node of the Abstract Syntax Tree representing a complete program or build unit.
/// Contains all top-level declarations and serves as the entry point for AST operations.
/// </summary>
/// <param name="Declarations">List of all top-level declarations in the program (functions, classes, variables, imports)</param>
/// <param name="Location">Source location information (typically represents the entire file)</param>
/// <remarks>
/// The Program node is the root of the AST hierarchy:
/// <list type="bullet">
/// <item>Entry point: all AST traversals begin at the Program node</item>
/// <item>Build unit: represents a single source file or module</item>
/// <item>Declaration container: holds all top-level program constructs</item>
/// <item>Global scope: establishes the outermost lexical scope</item>
/// </list>
///
/// Typical program structure:
/// <list type="bullet">
/// <item>Import declarations for external dependencies</item>
/// <item>Type declarations (classes, structs, enums, traits)</item>
/// <item>FreeRoutine and variable declarations</item>
/// <item>Implementation blocks for traits</item>
/// </list>
/// </remarks>
public record Program(List<ISyntaxTreeNode> Declarations, SourceLocation Location)
    : SyntaxTreeNode(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(ISyntaxTreeVisitor<T> visitor)
    {
        return visitor.VisitProgram(node: this);
    }
}

#endregion
