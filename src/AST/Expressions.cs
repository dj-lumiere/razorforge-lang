using Compilers.Shared.Lexer;

namespace Compilers.Shared.AST;

#region Base Expression Types

/// <summary>
/// Base entity for all expression nodes in the AST.
/// Expressions are constructs that evaluate to produce values and can be used as operands.
/// They represent computations, value retrievals, and data transformations.
/// </summary>
/// <param name="Location">Source location information for error reporting and debugging</param>
/// <remarks>
/// Expressions form the computational foundation of the languages:
/// <list type="bullet">
/// <item>Literal values: numbers, strings, booleans</item>
/// <item>Variable access and function calls</item>
/// <item>Arithmetic and logical operations</item>
/// <item>Complex operations: member access, indexing, type conversions</item>
/// <item>Memory operations: slice constructors, memory slice method calls</item>
/// <item>Danger zone operations: raw memory access, type punning</item>
/// </list>
/// </remarks>
public abstract record Expression(SourceLocation Location) : AstNode(Location: Location);

#endregion

#region Literal and Identifier Expressions

/// <summary>
/// Expression representing constant literal values embedded in source code.
/// Includes all primitive types: integers, floats, strings, characters, and booleans.
/// </summary>
/// <param name="Value">The actual value of the literal (42, 3.14, "hello", 'a', true)</param>
/// <param name="LiteralType">Token type indicating the specific literal format used</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports the full range of RazorForge/Suflae literal types:
/// <list type="bullet">
/// <item>Integers: s8, s16, s32, s64, s128, syssint, u8, u16, u32, u64, u128, sysuint</item>
/// <item>Floats: f16, f32, f64, f128</item>
/// <item>Decimals: d32, d64, d128</item>
/// <item>Text and characters: letter, text</item>
/// <item>Booleans: true, false</item>
/// <item>Duration: h, m, s, ms, us, ns</item>
/// <item>MemorySize: b, kb, mb, gb, tb
/// , pb, kbit, mbit, gbit, tbit, pbit
/// , kib, mib, gib, tib, pib, kibit
/// , mibit, gibit, tibit, pibit</item>
/// </list>
/// </remarks>
public record LiteralExpression(object Value, TokenType LiteralType, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitLiteralExpression(node: this);
    }
}

/// <summary>
/// Expression that references a named symbol (variable, function, entity, etc.).
/// Represents the use of identifiers that must be resolved during semantic analysis.
/// </summary>
/// <param name="Name">The identifier name to look up in the symbol table</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Identifier resolution follows lexical scoping rules:
/// <list type="bullet">
/// <item>Variables: local scope, then enclosing scopes, then global</item>
/// <item>Functions: may be resolved with overloading based on call context</item>
/// <item>Types: resolved in type namespace with visibility rules</item>
/// <item>Qualified names: may represent member access chains</item>
/// </list>
/// </remarks>
public record IdentifierExpression(string Name, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIdentifierExpression(node: this);
    }
}

#endregion

#region Operator Expressions

/// <summary>
/// Expression that combines two operands with a binary operator.
/// Supports arithmetic, comparison, logical, and bitwise operations.
/// </summary>
/// <param name="Left">Left-hand side operand expression</param>
/// <param name="Operator">Binary operator defining the operation to perform</param>
/// <param name="Right">Right-hand side operand expression</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports extensive operator categories:
/// <list type="bullet">
/// <item>Arithmetic: +, -, *, /, %, ** (with overflow variants)</item>
/// <item>Comparison: ==, !=, &lt;, &lt;=, &gt;, &gt;=</item>
/// <item>Logical: &amp;&amp;, ||</item>
/// <item>Membership: in, not in, is, is not, follows, not follows</item>
/// <item>Bitwise: &amp;, |, ^, &lt;&lt;, &gt;&gt;</item>
/// </list>
/// </remarks>
public record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitBinaryExpression(node: this);
    }
}

/// <summary>
/// Expression that applies a unary operator to a single operand.
/// Supports arithmetic negation, logical negation, and bitwise operations.
/// </summary>
/// <param name="Operator">Unary operator defining the operation to perform</param>
/// <param name="Operand">Expression to apply the operator to</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supported unary operators:
/// <list type="bullet">
/// <item>Arithmetic: +x (unary plus), -x (negation)</item>
/// <item>Logical: !condition (logical NOT)</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// </list>
/// </remarks>
public record UnaryExpression(UnaryOperator Operator, Expression Operand, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitUnaryExpression(node: this);
    }
}

/// <summary>
/// Expression that invokes a function or method with zero or more arguments.
/// Represents function calls, method invocations, and constructor calls.
/// </summary>
/// <param name="Callee">Expression that evaluates to a callable (function, method, lambda)</param>
/// <param name="Arguments">List of argument expressions to pass to the callable</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Supports various call patterns:
/// <list type="bullet">
/// <item>Function calls: routine(a, b, c)</item>
/// <item>Method calls: obj.method(x, y)</item>
/// <item>Constructor calls: Point(x, y)</item>
/// <item>Lambda calls: ((x) => x + 1)(42)</item>
/// <item>Operator method calls: obj.+(other)</item>
/// </list>
/// </remarks>
public record CallExpression(
    Expression Callee,
    List<Expression> Arguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitCallExpression(node: this);
    }

    /// <summary>
    /// Compatibility property for tests that expect CallExpression.Name
    /// Returns the name if Callee is an IdentifierExpression
    /// </summary>
    public string? Name => Callee is IdentifierExpression id
        ? id.Name
        : null;
}

/// <summary>
/// Named argument expression used in function calls.
/// Allows specifying argument name explicitly: func(name: value)
/// </summary>
/// <param name="Name">The parameter name</param>
/// <param name="Value">The argument value expression</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Named arguments support:
/// <list type="bullet">
/// <item>Explicit parameter binding: Error(message: "failed", code: 500)</item>
/// <item>Out-of-order arguments: Point(y: 10, x: 5)</item>
/// <item>Clarity for boolean/numeric arguments: set_visible(visible: true)</item>
/// <item>Optional parameter selection: connect(host: "localhost", port: 8080)</item>
/// </list>
/// </remarks>
public record NamedArgumentExpression(
    string Name,
    Expression Value,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitNamedArgumentExpression(node: this);
    }
}

/// <summary>
/// Expression that accesses a member (field, property, or method) of an object.
/// Represents the dot notation for accessing object members.
/// </summary>
/// <param name="Object">Expression that evaluates to the object containing the member</param>
/// <param name="PropertyName">Name of the member to access</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Member access patterns:
/// <list type="bullet">
/// <item>Field access: obj.field</item>
/// <item>Property access: obj.property</item>
/// <item>Method reference: obj.method (not a call)</item>
/// <item>Chained access: obj.child.grandchild</item>
/// </list>
/// </remarks>
public record MemberExpression(Expression Object, string PropertyName, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitMemberExpression(node: this);
    }
}

/// <summary>
/// Expression that accesses an element of a collection using an index or key.
/// Represents bracket notation for array indexing and dictionary access.
/// </summary>
/// <param name="Object">Expression that evaluates to an indexable collection</param>
/// <param name="Index">Expression that evaluates to the index or key</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Index access patterns:
/// <list type="bullet">
/// <item>Array indexing: arr[0], arr[i]</item>
/// <item>Dictionary access: dict["key"]</item>
/// <item>String indexing: str[pos]</item>
/// <item>Multi-dimensional: matrix[row][col]</item>
/// </list>
/// </remarks>
public record IndexExpression(Expression Object, Expression Index, SourceLocation Location)
    : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIndexExpression(node: this);
    }
}

/// <summary>
/// Expression that selects between two values based on a boolean condition.
/// Represents the ternary conditional operator (condition ? true_value : false_value).
/// </summary>
/// <param name="Condition">Boolean expression to evaluate for selection</param>
/// <param name="TrueExpression">Expression to evaluate and return if condition is true</param>
/// <param name="FalseExpression">Expression to evaluate and return if condition is false</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Conditional expression behavior:
/// <list type="bullet">
/// <item>Short-circuit evaluation: only one branch is evaluated</item>
/// <item>Type compatibility: both branches must have compatible types</item>
/// <item>Nested conditionals: can be chained for complex logic</item>
/// <item>Expression context: can be used anywhere a value is expected</item>
/// </list>
/// </remarks>
public record ConditionalExpression(
    Expression Condition,
    Expression TrueExpression,
    Expression FalseExpression,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitConditionalExpression(node: this);
    }
}

/// <summary>
/// Expression that chains multiple comparison operations in a single statement.
/// Allows natural mathematical notation like a < b < c instead of a < b && b < c.
/// </summary>
/// <param name="Operands">List of expressions to compare (minimum 2 required)</param>
/// <param name="Operators">List of comparison operators between operands (one fewer than operands)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Chained comparison features:
/// <list type="bullet">
/// <item>Mathematical notation: 1 < x < 10, a >= b == c</item>
/// <item>Short-circuit evaluation: stops on first false comparison</item>
/// <item>Mixed operators: supports different operators in same chain</item>
/// <item>Type checking: ensures compatible types across all comparisons</item>
/// </list>
/// </remarks>
public record ChainedComparisonExpression(
    List<Expression> Operands,
    List<BinaryOperator> Operators,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitChainedComparisonExpression(node: this);
    }
}

/// <summary>
/// Expression that creates a sequence of values between start and end bounds.
/// Supports both simple ranges and ranges with custom step values.
/// </summary>
/// <param name="Start">Expression that evaluates to the starting value of the range</param>
/// <param name="End">Expression that evaluates to the ending value of the range (inclusive or exclusive)</param>
/// <param name="Step">Optional expression for the step size between values (default is 1)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Range expression variants:
/// <list type="bullet">
/// <item>Simple range: (0 to 10) creates sequence [0, 1, 2, ..., 10]</item>
/// <item>Step range: (0 to 10 step 2) creates [0, 2, 4, 6, 8, 10]</item>
/// <item>Reverse range: (10 to 0 step -1) creates [10, 9, 8, ..., 0]</item>
/// <item>Iterable: can be used in for loops and collection operations</item>
/// </list>
/// </remarks>
public record RangeExpression(
    Expression Start,
    Expression End,
    Expression? Step,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitRangeExpression(node: this);
    }
}

/// <summary>
/// Expression that creates an anonymous function with parameters and a body expression.
/// Represents closures that can capture variables from their defining scope.
/// </summary>
/// <param name="Parameters">List of parameters the lambda accepts</param>
/// <param name="Body">Expression that forms the body of the lambda (returned value)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Lambda expression features:
/// <list type="bullet">
/// <item>Anonymous functions: (x, y) => x + y</item>
/// <item>Closure capture: can access variables from enclosing scope</item>
/// <item>Type inference: parameter and return types often inferred</item>
/// <item>Higher-order functions: can be passed as arguments or returned</item>
/// <item>Functional programming: enables map, filter, reduce patterns</item>
/// </list>
/// </remarks>
public record LambdaExpression(
    List<Parameter> Parameters,
    Expression Body,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitLambdaExpression(node: this);
    }
}

/// <summary>
/// Enumeration of binary operators supported in binary expressions.
/// Includes arithmetic, comparison, logical, bitwise, and specialized operators.
/// </summary>
/// <remarks>
/// The operator categories provide comprehensive mathematical and logical operations:
/// <list type="bullet">
/// <item>Arithmetic: Basic math operations with overflow handling variants</item>
/// <item>Comparison: Standard comparisons plus membership and type testing</item>
/// <item>Logical: Boolean logic operations with short-circuit evaluation</item>
/// <item>Bitwise: Low-level bit manipulation operations</item>
/// <item>Assignment: Assignment operators when used in expression context</item>
/// </list>
/// Overflow handling variants allow precise control over integer arithmetic behavior.
/// </remarks>
public enum BinaryOperator
{
    // Arithmetic - standard operations
    Add, Subtract, Multiply, TrueDivide, Divide, Modulo, Power,

    // Arithmetic with overflow handling variants
    AddWrap, SubtractWrap, MultiplyWrap, DivideWrap, ModuloWrap, PowerWrap,
    AddSaturate, SubtractSaturate, MultiplySaturate, DivideSaturate, ModuloSaturate, PowerSaturate,
    AddUnchecked, SubtractUnchecked, MultiplyUnchecked, DivideUnchecked, ModuloUnchecked,
    PowerUnchecked,
    AddChecked, SubtractChecked, MultiplyChecked, DivideChecked, ModuloChecked, PowerChecked,

    // Comparison - equality, relational, and membership
    Equal, NotEqual, Less, LessEqual, Greater, GreaterEqual,
    In, NotIn, Is, IsNot, From, NotFrom, Follows, NotFollows,

    // Logical - boolean operations with short-circuit evaluation
    And, Or,

    // Bitwise - low-level bit manipulation
    BitwiseAnd, BitwiseOr, BitwiseXor, LeftShift, RightShift,

    // Assignment - when assignment is used as expression
    Assign
}

/// <summary>
/// Extension methods for BinaryOperator enum
/// </summary>
public static class BinaryOperatorExtensions
{
    /// <summary>
    /// Converts a BinaryOperator to its string representation
    /// </summary>
    public static string ToStringRepresentation(this BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide or BinaryOperator.TrueDivide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Power => "**",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterEqual => ">=",
            BinaryOperator.And => "and",
            BinaryOperator.Or => "or",
            BinaryOperator.BitwiseAnd => "&",
            BinaryOperator.BitwiseOr => "|",
            BinaryOperator.BitwiseXor => "^",
            BinaryOperator.LeftShift => "<<",
            BinaryOperator.RightShift => ">>",
            BinaryOperator.In => "in",
            BinaryOperator.NotIn => "not in",
            BinaryOperator.Is => "is",
            BinaryOperator.IsNot => "is not",
            BinaryOperator.Assign => "=",
            _ => op.ToString()
        };
    }
}

/// <summary>
/// Enumeration of unary operators that operate on a single operand.
/// Supports arithmetic, logical, and bitwise operations.
/// </summary>
/// <remarks>
/// Unary operators provide fundamental single-operand operations:
/// <list type="bullet">
/// <item>Arithmetic: +x (identity), -x (negation)</item>
/// <item>Logical: !condition (boolean NOT)</item>
/// <item>Bitwise: ~x (bitwise complement)</item>
/// </list>
/// </remarks>
public enum UnaryOperator
{
    // Arithmetic - sign operations
    Plus, Minus,

    // Logical - boolean negation
    Not,

    // Bitwise - bit complement
    BitwiseNot
}

#endregion

#region Function Call and Access Expressions

/// <summary>
/// Represents a parameter definition for functions, methods, and lambdas.
/// Includes optional type annotation and default value for flexible parameter handling.
/// </summary>
/// <param name="Name">Parameter name used for binding in the function body</param>
/// <param name="Type">Optional type annotation; if null, type is inferred from usage</param>
/// <param name="DefaultValue">Optional default value expression; allows parameter to be omitted in calls</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Parameter flexibility supports various declaration patterns:
/// <list type="bullet">
/// <item>Simple: param</item>
/// <item>Typed: param: s32</item>
/// <item>With default: param = 42</item>
/// <item>Fully specified: param: s32 = 42</item>
/// </list>
/// </remarks>
public record Parameter(
    string Name,
    TypeExpression? Type,
    Expression? DefaultValue,
    SourceLocation Location);

#endregion

#region Type Expressions

/// <summary>
/// Expression that represents a type reference in type annotations and declarations.
/// Supports simple types, generic types, and complex type constructions.
/// </summary>
/// <param name="Name">Base type name (s32, String, MyClass, etc.)</param>
/// <param name="GenericArguments">Optional list of type arguments for generic types</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Type expression patterns:
/// <list type="bullet">
/// <item>Primitive types: s32, f64, bool, text</item>
/// <item>Entity types: String, MyClass, Point</item>
/// <item>Generic types: Array[s32], Dictionary[String, s32]</item>
/// <item>Nested generics: Array[Dictionary[String, s32]]</item>
/// </list>
/// </remarks>
public record TypeExpression(
    string Name,
    List<TypeExpression>? GenericArguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitTypeExpression(node: this);
    }
}

/// <summary>
/// Expression that performs explicit type conversion between compatible types.
/// Supports both function-style and method-style conversion syntax.
/// </summary>
/// <param name="TargetType">Name of the target type to convert to</param>
/// <param name="Expression">Source expression to convert</param>
/// <param name="IsMethodStyle">true for method-style (x.s32!()), false for function-style (s32!(x))</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Type conversion styles:
/// <list type="bullet">
/// <item>Function-style: s32!(3.14), bool!(1)</item>
/// <item>Method-style: 3.14.s32!(), 1.bool!()</item>
/// <item>Safety: explicit conversions may fail at runtime</item>
/// <item>Checked: conversion failures throw exceptions</item>
/// </list>
/// Both styles are semantically equivalent; choice is stylistic preference.
/// </remarks>
public record TypeConversionExpression(
    string TargetType,
    Expression Expression,
    bool IsMethodStyle,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitTypeConversionExpression(node: this);
    }
}

#endregion

#region Memory Operation Expressions

/// <summary>
/// Expression representing memory slice constructor calls (DynamicSlice, TemporarySlice).
/// Creates new memory slice instances with specified byte sizes.
/// </summary>
/// <param name="SliceType">Type of slice to construct ("DynamicSlice" or "TemporarySlice")</param>
/// <param name="SizeExpression">Expression evaluating to the size in bytes</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Memory slice constructors create low-level memory management objects:
/// <list type="bullet">
/// <item>DynamicSlice(64) - allocates 64 bytes on heap</item>
/// <item>TemporarySlice(40) - allocates 40 bytes on stack</item>
/// <item>Size expression must evaluate to sysuint type</item>
/// <item>Constructor calls trigger runtime allocation functions</item>
/// </list>
/// </remarks>
public record SliceConstructorExpression(
    string SliceType,
    Expression SizeExpression,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitSliceConstructorExpression(node: this);
    }
}

/// <summary>
/// Expression representing generic method calls with type parameters.
/// Used for slice operations like read&lt;T&gt;!() and write&lt;T&gt;!().
/// </summary>
/// <param name="Object">Expression representing the object being called</param>
/// <param name="MethodName">Name of the generic method</param>
/// <param name="TypeArguments">List of type arguments for the generic method</param>
/// <param name="Arguments">List of argument expressions passed to the method</param>
/// <param name="IsMemoryOperation">Whether this method call uses ! syntax (memory operation)</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Generic method calls enable type-safe slice operations:
/// <list type="bullet">
/// <item>buffer.read&lt;s32&gt;!(0) - read s32 at offset 0</item>
/// <item>buffer.write&lt;f64&gt;!(8, 3.14) - write f64 at offset 8</item>
/// <item>Type arguments specify the data type for the operation</item>
/// <item>Memory operations (!) require bounds checking at runtime</item>
/// </list>
/// </remarks>
public record GenericMethodCallExpression(
    Expression Object,
    string MethodName,
    List<TypeExpression> TypeArguments,
    List<Expression> Arguments,
    bool IsMemoryOperation,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitGenericMethodCallExpression(node: this);
    }
}

/// <summary>
/// Expression representing generic member access with type parameters.
/// Used for accessing generic properties or fields with type specification.
/// </summary>
/// <param name="Object">Expression representing the object being accessed</param>
/// <param name="MemberName">Name of the generic member</param>
/// <param name="TypeArguments">List of type arguments for the generic member</param>
/// <param name="Location">Source location information</param>
public record GenericMemberExpression(
    Expression Object,
    string MemberName,
    List<TypeExpression> TypeArguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitGenericMemberExpression(node: this);
    }
}

/// <summary>
/// Expression representing memory operations with ! syntax.
/// Used for slice operations that can potentially crash on invalid access.
/// </summary>
/// <param name="Object">Expression representing the slice object</param>
/// <param name="OperationName">Name of the memory operation (size, address, hijack, etc.)</param>
/// <param name="Arguments">List of argument expressions for the operation</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Memory operations provide direct access to slice functionality:
/// <list type="bullet">
/// <item>buffer.size!() - get allocated byte size</item>
/// <item>buffer.address!() - get raw memory address</item>
/// <item>buffer.hijack!() - take exclusive ownership</item>
/// <item>buffer.unsafe_ptr!(offset) - get unsafe pointer at offset</item>
/// </list>
/// All operations use ! to indicate potential runtime failures.
/// </remarks>
public record MemoryOperationExpression(
    Expression Object,
    string OperationName,
    List<Expression> Arguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitMemoryOperationExpression(node: this);
    }
}

/// <summary>
/// Expression for compiler intrinsic function calls.
/// Intrinsics map directly to low-level operations and are only available in danger! blocks.
/// </summary>
/// <param name="IntrinsicName">Name of the intrinsic operation (e.g., "load", "add.wrapping", "icmp.slt")</param>
/// <param name="TypeArguments">Type parameters for the intrinsic (e.g., &lt;T&gt; or &lt;T, U&gt;)</param>
/// <param name="Arguments">Arguments passed to the intrinsic</param>
/// <param name="Location">Source location information</param>
/// <remarks>
/// Examples:
/// <list type="bullet">
/// <item>@intrinsic.load&lt;s32&gt;(addr) - Load s32 from memory address</item>
/// <item>@intrinsic.add.wrapping&lt;i32&gt;(a, b) - Wrapping addition</item>
/// <item>@intrinsic.icmp.slt&lt;i64&gt;(x, y) - Signed less than comparison</item>
/// <item>@intrinsic.bitcast&lt;f32, u32&gt;(value) - Type punning (reinterpret bits)</item>
/// </list>
/// All intrinsics must be called within danger! blocks and are validated at compile time.
/// </remarks>
public record IntrinsicCallExpression(
    string IntrinsicName,
    List<string> TypeArguments,
    List<Expression> Arguments,
    SourceLocation Location) : Expression(Location: Location)
{
    /// <summary>Accepts a visitor for AST traversal and transformation</summary>
    public override T Accept<T>(IAstVisitor<T> visitor)
    {
        return visitor.VisitIntrinsicCallExpression(node: this);
    }
}

#endregion
