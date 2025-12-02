# RazorForge/Suflae AST API Reference

## Overview

This document provides a comprehensive guide to the Abstract Syntax Tree (AST) implementation in the RazorForge/Suflae
compiler. The AST represents the syntactic structure of source code and serves as the primary data structure for
semantic analysis, optimization, and code generation.

## Table of Contents

1. [Architecture](#architecture)
2. [Core Interfaces](#core-interfaces)
3. [Expression Nodes](#expression-nodes)
4. [Statement Nodes](#statement-nodes)
5. [Declaration Nodes](#declaration-nodes)
6. [Visitor Pattern](#visitor-pattern)
7. [Usage Examples](#usage-examples)

---

## Architecture

### Design Principles

The AST implementation follows these key principles:

- **Immutability**: All nodes are implemented as C# records, providing structural equality and immutability
- **Visitor Pattern**: Extensible traversal and transformation via the `IAstVisitor<T>` interface
- **Source Location Tracking**: Every node tracks its source location for precise error reporting
- **Type Safety**: Strong typing ensures compile-time correctness
- **Separation of Concerns**: Clear separation between expressions, statements, and declarations

### File Organization

```
src/AST/
├── ASTNode.cs        # Base interfaces and visitor pattern
├── Expressions.cs    # All expression node types
├── Statements.cs     # All statement node types
└── Declarations.cs   # All declaration node types
```

### Region Structure

Each file is organized with regions for easy navigation:

**ASTNode.cs**

- `#region Base Interfaces and Types`
- `#region Visitor Pattern`
- `#region Root Program Node`

**Expressions.cs**

- `#region Base Expression Types`
- `#region Literal and Identifier Expressions`
- `#region Operator Expressions`
- `#region Function Call and Access Expressions`
- `#region Type Expressions`
- `#region Memory Operation Expressions`

**Statements.cs**

- `#region Base Statement Types`
- `#region Simple Statements`
- `#region Control Flow Statements`
- `#region Pattern Matching`
- `#region Unsafe Statements`

**Declarations.cs**

- `#region Base Declaration Types`
- `#region Variable and Function Declarations`
- `#region Type Declarations`
- `#region Import and Module Declarations`
- `#region Supporting Types and Enums`
- `#region Advanced Declarations`

---

## Core Interfaces

### IAstNode

Base interface for all AST nodes.

```csharp
public interface IAstNode
{
    SourceLocation Location { get; }
    T Accept<T>(IAstVisitor<T> visitor);
}
```

**Properties:**

- `Location`: Source location information for error reporting

**Methods:**

- `Accept<T>`: Accepts a visitor for traversal/transformation

### SourceLocation

Immutable record tracking source code position.

```csharp
public record SourceLocation(int Line, int Column, int Position);
```

**Parameters:**

- `Line`: 1-based line number
- `Column`: 1-based column number
- `Position`: 0-based absolute character position

### AstNode

Abstract base class for all concrete AST nodes.

```csharp
public abstract record AstNode(SourceLocation Location) : IAstNode
{
    public abstract T Accept<T>(IAstVisitor<T> visitor);
}
```

---

## Expression Nodes

Expressions are constructs that evaluate to produce values.

### Literal Expressions

#### LiteralExpression

Represents constant values in source code.

```csharp
public record LiteralExpression(
    object Value,
    TokenType LiteralType,
    SourceLocation Location
) : Expression(Location);
```

**Supported Types:**

- Integers: `s8`, `s16`, `s32`, `s64`, `s128`, `u8`, `u16`, `u32`, `u64`, `u128`
- Floats: `f16`, `f32`, `f64`, `f128`
- Decimals: `d32`, `d64`, `d128`
- Text: `letter` (char), `text` (string)
- Boolean: `true`, `false`
- Duration: `h`, `m`, `s`, `ms`, `us`, `ns`
- Memory: `b`, `kb`, `mb`, `gb`, `tb`, `pb`

**Example:**

```csharp
var literal = new LiteralExpression(42, TokenType.IntLiteral, location);
```

#### IdentifierExpression

References a named symbol (variable, function, type).

```csharp
public record IdentifierExpression(
    string Name,
    SourceLocation Location
) : Expression(Location);
```

**Usage:**

```csharp
var identifier = new IdentifierExpression("myVariable", location);
```

### Operator Expressions

#### BinaryExpression

Combines two operands with a binary operator.

```csharp
public record BinaryExpression(
    Expression Left,
    BinaryOperator Operator,
    Expression Right,
    SourceLocation Location
) : Expression(Location);
```

**Supported Operators:**

- **Arithmetic**: `Add`, `Subtract`, `Multiply`, `Divide`, `Modulo`, `Power`
- **Comparison**: `Equal`, `NotEqual`, `Less`, `LessEqual`, `Greater`, `GreaterEqual`
- **Logical**: `And`, `Or`
- **Bitwise**: `BitwiseAnd`, `BitwiseOr`, `BitwiseXor`, `LeftShift`, `RightShift`
- **Membership**: `In`, `NotIn`, `Is`, `IsNot`, `Follows`, `NotFollows`

**Overflow Variants:**
Each arithmetic operator has variants for different overflow handling:

- `*Wrap`: Wrapping overflow
- `*Saturate`: Saturating arithmetic
- `*Unchecked`: No overflow checking
- `*Checked`: Explicit overflow checking

**Example:**

```csharp
var addition = new BinaryExpression(
    new LiteralExpression(1, TokenType.IntLiteral, loc),
    BinaryOperator.Add,
    new LiteralExpression(2, TokenType.IntLiteral, loc),
    location
);
```

#### UnaryExpression

Applies a unary operator to a single operand.

```csharp
public record UnaryExpression(
    UnaryOperator Operator,
    Expression Operand,
    SourceLocation Location
) : Expression(Location);
```

**Supported Operators:**

- `Plus`: Unary plus
- `Minus`: Negation
- `Not`: Logical NOT
- `BitwiseNot`: Bitwise complement

#### ChainedComparisonExpression

Chains multiple comparison operations.

```csharp
public record ChainedComparisonExpression(
    List<Expression> Operands,
    List<BinaryOperator> Operators,
    SourceLocation Location
) : Expression(Location);
```

**Example in RazorForge:**

```razorforge
1 < x < 10  // Equivalent to: 1 < x && x < 10
```

**Usage:**

```csharp
var chained = new ChainedComparisonExpression(
    new List<Expression> { one, x, ten },
    new List<BinaryOperator> { BinaryOperator.Less, BinaryOperator.Less },
    location
);
```

### Function Call and Access Expressions

#### CallExpression

Invokes a function or method.

```csharp
public record CallExpression(
    Expression Callee,
    List<Expression> Arguments,
    SourceLocation Location
) : Expression(Location);
```

**Usage:**

```csharp
var call = new CallExpression(
    new IdentifierExpression("print", loc),
    new List<Expression> { new LiteralExpression("Hello", TokenType.StringLiteral, loc) },
    location
);
```

#### MemberExpression

Accesses a member of an object.

```csharp
public record MemberExpression(
    Expression Object,
    string PropertyName,
    SourceLocation Location
) : Expression(Location);
```

**Example:** `obj.field`

#### IndexExpression

Accesses an element by index.

```csharp
public record IndexExpression(
    Expression Object,
    Expression Index,
    SourceLocation Location
) : Expression(Location);
```

**Example:** `array[0]`, `dict["key"]`

#### ConditionalExpression

Ternary conditional operator.

```csharp
public record ConditionalExpression(
    Expression Condition,
    Expression TrueExpression,
    Expression FalseExpression,
    SourceLocation Location
) : Expression(Location);
```

**Example:** `condition ? trueValue : falseValue`

#### LambdaExpression

Anonymous function expression.

```csharp
public record LambdaExpression(
    List<Parameter> Parameters,
    Expression Body,
    SourceLocation Location
) : Expression(Location);
```

**Example:** `(x, y) => x + y`

#### RangeExpression

Creates a sequence of values.

```csharp
public record RangeExpression(
    Expression Start,
    Expression End,
    Expression? Step,
    SourceLocation Location
) : Expression(Location);
```

**Example:** `(0 to 10)`, `(0 to 10 step 2)`

### Type Expressions

#### TypeExpression

Represents a type reference.

```csharp
public record TypeExpression(
    string Name,
    List<TypeExpression>? GenericArguments,
    SourceLocation Location
) : Expression(Location);
```

**Examples:**

- Simple: `s32`, `text`, `bool`
- Generic: `Array[s32]`, `Dictionary[text, s32]`

#### TypeConversionExpression

Explicit type conversion.

```csharp
public record TypeConversionExpression(
    string TargetType,
    Expression Expression,
    bool IsMethodStyle,
    SourceLocation Location
) : Expression(Location);
```

**Examples:**

- Function style: `s32!(3.14)`
- Method style: `3.14.s32!()`

### Memory Operation Expressions

#### SliceConstructorExpression

Creates memory slice instances.

```csharp
public record SliceConstructorExpression(
    string SliceType,
    Expression SizeExpression,
    SourceLocation Location
) : Expression(Location);
```

**Types:**

- `DynamicSlice`: Heap-allocated memory
- `TemporarySlice`: Stack-allocated memory

**Example:** `DynamicSlice(64)`

#### GenericMethodCallExpression

Generic method with type parameters.

```csharp
public record GenericMethodCallExpression(
    Expression Object,
    string MethodName,
    List<TypeExpression> TypeArguments,
    List<Expression> Arguments,
    bool IsMemoryOperation,
    SourceLocation Location
) : Expression(Location);
```

**Example:** `buffer.read<s32>!(0)`

#### MemoryOperationExpression

Memory operations with `!` syntax.

```csharp
public record MemoryOperationExpression(
    Expression Object,
    string OperationName,
    List<Expression> Arguments,
    SourceLocation Location
) : Expression(Location);
```

**Operations:**

- `size!()`: Get size in bytes
- `address!()`: Get memory address
- `hijack!()`: Take ownership
- `unsafe_ptr!(offset)`: Get unsafe pointer

---

## Statement Nodes

Statements are executable constructs that perform actions.

### Simple Statements

#### ExpressionStatement

Evaluates an expression for side effects.

```csharp
public record ExpressionStatement(
    Expression Expression,
    SourceLocation Location
) : Statement(Location);
```

**Example:** `print("Hello");`

#### DeclarationStatement

Wraps a declaration in statement context.

```csharp
public record DeclarationStatement(
    Declaration Declaration,
    SourceLocation Location
) : Statement(Location);
```

**Example:** `var x = 42;` inside a function

#### AssignmentStatement

Assigns a value to a target.

```csharp
public record AssignmentStatement(
    Expression Target,
    Expression Value,
    SourceLocation Location
) : Statement(Location);
```

**Examples:**

- Variable: `x = 42`
- Property: `obj.field = value`
- Index: `arr[0] = item`

#### ReturnStatement

Exits a function with optional value.

```csharp
public record ReturnStatement(
    Expression? Value,
    SourceLocation Location
) : Statement(Location);
```

**Examples:**

- `return 42;`
- `return;` (void)

### Control Flow Statements

#### IfStatement

Conditional branching.

```csharp
public record IfStatement(
    Expression Condition,
    Statement ThenStatement,
    Statement? ElseStatement,
    SourceLocation Location
) : Statement(Location);
```

**Example:**

```razorforge
if (x > 0) {
    print("positive")
} else {
    print("non-positive")
}
```

#### WhileStatement

Pre-condition loop.

```csharp
public record WhileStatement(
    Expression Condition,
    Statement Body,
    SourceLocation Location
) : Statement(Location);
```

#### ForStatement

Iterator loop over collections.

```csharp
public record ForStatement(
    string Variable,
    Expression Iterable,
    Statement Body,
    SourceLocation Location
) : Statement(Location);
```

**Example:** `for item in array { ... }`

#### BlockStatement

Groups multiple statements.

```csharp
public record BlockStatement(
    List<Statement> Statements,
    SourceLocation Location
) : Statement(Location);
```

#### WhenStatement

Pattern matching statement.

```csharp
public record WhenStatement(
    Expression Expression,
    List<WhenClause> Clauses,
    SourceLocation Location
) : Statement(Location);
```

**Example:**

```razorforge
when value {
    42 => print("forty-two")
    n if n > 0 => print("positive")
    _ => print("other")
}
```

#### BreakStatement

Exits innermost loop.

```csharp
public record BreakStatement(SourceLocation Location) : Statement(Location);
```

#### ContinueStatement

Skips to next loop iteration.

```csharp
public record ContinueStatement(SourceLocation Location) : Statement(Location);
```

### Pattern Matching

#### Pattern Types

Base class for all patterns:

```csharp
public abstract record Pattern(SourceLocation Location);
```

**Concrete Pattern Types:**

1. **LiteralPattern**: Matches exact values
   ```csharp
   public record LiteralPattern(object Value, SourceLocation Location)
   ```

2. **IdentifierPattern**: Binds to variable
   ```csharp
   public record IdentifierPattern(string Name, SourceLocation Location)
   ```

3. **TypePattern**: Matches by type
   ```csharp
   public record TypePattern(TypeExpression Type, string? VariableName, SourceLocation Location)
   ```

4. **WildcardPattern**: Matches anything
   ```csharp
   public record WildcardPattern(SourceLocation Location)
   ```

### Unsafe Statements

#### DangerStatement

Disables safety checks for low-level operations.

```csharp
public record DangerStatement(
    BlockStatement Body,
    SourceLocation Location
) : Statement(Location);
```

**Features:**

- Raw memory access
- Volatile operations
- Type punning
- Bypassing bounds checks

**Example:**

```razorforge
danger! {
    let ptr = buffer.address!()
    // Unsafe operations here
}
```

---

## Declaration Nodes

Declarations introduce new names into scopes.

### Variable Declarations

#### VariableDeclaration

Declares a variable.

```csharp
public record VariableDeclaration(
    string Name,
    TypeExpression? Type,
    Expression? Initializer,
    VisibilityModifier Visibility,
    bool IsMutable,
    SourceLocation Location
) : Declaration(Location);
```

**Examples:**

- Type inference: `var x = 42`
- Explicit type: `var x: s64 = 42`
- Immutable: `let x = 42`
- Uninitialized: `var x: s32`

### Function Declarations

#### FunctionDeclaration

Declares a function or routine.

```csharp
public record FunctionDeclaration(
    string Name,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    Statement Body,
    VisibilityModifier Visibility,
    List<string> Attributes,
    SourceLocation Location,
    List<string>? GenericParameters = null
) : Declaration(Location);
```

**Features:**

- Generic functions
- Default parameters
- Attributes/decorators
- Visibility modifiers

**Example:**

```razorforge
public routine greet(name: text = "World") -> text {
    return "Hello, " + name
}
```

### Type Declarations

#### ClassDeclaration (Entity)

Reference type with inheritance.

```csharp
public record ClassDeclaration(
    string Name,
    List<string>? GenericParameters,
    TypeExpression? BaseClass,
    List<TypeExpression> Interfaces,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location
) : Declaration(Location);
```

**Example:**

```razorforge
entity Dog from Animal follows Nameable {
    var name: text

    routine bark() {
        print("Woof!")
    }
}
```

#### StructDeclaration (Record)

Value type with structural equality.

```csharp
public record StructDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<Declaration> Members,
    VisibilityModifier Visibility,
    SourceLocation Location
) : Declaration(Location);
```

**Example:**

```razorforge
record Point(x: f64, y: f64)
```

#### MenuDeclaration (Option/Enum)

Enumeration type.

```csharp
public record MenuDeclaration(
    string Name,
    List<EnumVariant> Variants,
    List<FunctionDeclaration> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location
) : Declaration(Location);
```

**Example:**

```razorforge
option HttpStatus {
    Ok = 200
    NotFound = 404
    ServerError = 500
}
```

#### VariantDeclaration

Tagged union/algebraic data type.

```csharp
public record VariantDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<VariantCase> Cases,
    List<FunctionDeclaration> Methods,
    VisibilityModifier Visibility,
    VariantKind Kind,
    SourceLocation Location
) : Declaration(Location);
```

**Variant Kinds:**

- `Chimera`: Default tagged union (requires `danger!` for access)
- `Variant`: All fields must be records (safe)
- `Mutant`: Raw memory union (requires `danger!`, unsafe)

**Example:**

```razorforge
variant Result[T, E] {
    Success(T)
    Error(E)
}
```

#### FeatureDeclaration (Trait/Interface)

Behavioral contract.

```csharp
public record FeatureDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<FunctionSignature> Methods,
    VisibilityModifier Visibility,
    SourceLocation Location
) : Declaration(Location);
```

**Example:**

```razorforge
feature Drawable {
    routine draw()
}
```

#### ImplementationDeclaration

Provides method implementations.

```csharp
public record ImplementationDeclaration(
    TypeExpression Type,
    TypeExpression? Trait,
    List<FunctionDeclaration> Methods,
    SourceLocation Location
) : Declaration(Location);
```

**Examples:**

- Inherent impl: `String follows { ... }`
- Trait impl: `MyType follows Drawable { ... }`

### Import Declarations

#### ImportDeclaration

Imports modules and symbols.

```csharp
public record ImportDeclaration(
    string ModulePath,
    string? Alias,
    List<string>? SpecificImports,
    SourceLocation Location
) : Declaration(Location);
```

**Examples:**

- Full module: `import std.collections`
- With alias: `import std.collections as col`
- Specific: `import std.collections { List, Map }`

### Advanced Declarations

#### ExternalDeclaration

Links to native functions.

```csharp
public record ExternalDeclaration(
    string Name,
    List<string>? GenericParameters,
    List<Parameter> Parameters,
    TypeExpression? ReturnType,
    string? CallingConvention,
    SourceLocation Location
) : Declaration(Location);
```

**Example:**

```razorforge
external("C") routine malloc(size: uaddr) -> cptr<cvoid>
```

---

## Visitor Pattern

### IAstVisitor<T>

Interface for implementing AST traversals and transformations.

```csharp
public interface IAstVisitor<T>
{
    // Expression visitors
    T VisitLiteralExpression(LiteralExpression node);
    T VisitIdentifierExpression(IdentifierExpression node);
    T VisitBinaryExpression(BinaryExpression node);
    // ... etc

    // Statement visitors
    T VisitExpressionStatement(ExpressionStatement node);
    T VisitIfStatement(IfStatement node);
    // ... etc

    // Declaration visitors
    T VisitVariableDeclaration(VariableDeclaration node);
    T VisitFunctionDeclaration(FunctionDeclaration node);
    // ... etc

    // Program visitor
    T VisitProgram(Program node);
}
```

### Implementing a Visitor

**Example: Pretty Printer**

```csharp
public class PrettyPrintVisitor : IAstVisitor<string>
{
    private int _indentLevel = 0;

    public string VisitBinaryExpression(BinaryExpression node)
    {
        var left = node.Left.Accept(this);
        var right = node.Right.Accept(this);
        var op = node.Operator.ToStringRepresentation();
        return $"({left} {op} {right})";
    }

    public string VisitLiteralExpression(LiteralExpression node)
    {
        return node.Value.ToString() ?? "";
    }

    public string VisitIfStatement(IfStatement node)
    {
        var condition = node.Condition.Accept(this);
        _indentLevel++;
        var thenBranch = node.ThenStatement.Accept(this);
        var result = $"if ({condition}) {{\n{Indent()}{thenBranch}\n{Indent(-1)}}}";

        if (node.ElseStatement != null)
        {
            var elseBranch = node.ElseStatement.Accept(this);
            result += $" else {{\n{Indent()}{elseBranch}\n{Indent(-1)}}}";
        }

        _indentLevel--;
        return result;
    }

    private string Indent(int offset = 0)
    {
        return new string(' ', (_indentLevel + offset) * 4);
    }

    // Implement other visitor methods...
}
```

**Usage:**

```csharp
var program = parser.ParseProgram();
var printer = new PrettyPrintVisitor();
var output = program.Accept(printer);
Console.WriteLine(output);
```

---

## Usage Examples

### Creating an AST Manually

```csharp
// Create: var x = 1 + 2
var location = new SourceLocation(1, 1, 0);

var declaration = new VariableDeclaration(
    Name: "x",
    Type: null, // Inferred
    Initializer: new BinaryExpression(
        Left: new LiteralExpression(1, TokenType.IntLiteral, location),
        Operator: BinaryOperator.Add,
        Right: new LiteralExpression(2, TokenType.IntLiteral, location),
        Location: location
    ),
    Visibility: VisibilityModifier.Private,
    IsMutable: true,
    Location: location
);
```

### Traversing an AST

```csharp
public class VariableCollector : IAstVisitor<List<string>>
{
    public List<string> VisitProgram(Program node)
    {
        var variables = new List<string>();
        foreach (var decl in node.Declarations)
        {
            if (decl is VariableDeclaration varDecl)
            {
                variables.Add(varDecl.Name);
            }
        }
        return variables;
    }

    // Implement other methods to return empty lists or recurse...
}

// Usage
var collector = new VariableCollector();
var variables = program.Accept(collector);
```

### Pattern Matching with When Statements

```csharp
// Create: when value { 42 => "found", _ => "not found" }
var whenStmt = new WhenStatement(
    Expression: new IdentifierExpression("value", location),
    Clauses: new List<WhenClause>
    {
        new WhenClause(
            Pattern: new LiteralPattern(42, location),
            Body: new ExpressionStatement(
                new LiteralExpression("found", TokenType.StringLiteral, location),
                location
            ),
            Location: location
        ),
        new WhenClause(
            Pattern: new WildcardPattern(location),
            Body: new ExpressionStatement(
                new LiteralExpression("not found", TokenType.StringLiteral, location),
                location
            ),
            Location: location
        )
    },
    Location: location
);
```

---

## Best Practices

### 1. Always Track Source Locations

```csharp
// Good
var expr = new BinaryExpression(left, op, right, location);

// Bad - missing location makes error reporting difficult
// var expr = new BinaryExpression(left, op, right, null);
```

### 2. Use Visitor Pattern for Traversals

```csharp
// Good - extensible and maintainable
public class MyAnalyzer : IAstVisitor<AnalysisResult> { ... }

// Bad - instanceof/type checking scattered throughout code
if (node is BinaryExpression bin) { ... }
else if (node is CallExpression call) { ... }
```

### 3. Leverage Pattern Matching

```csharp
// Good - clear and concise
return node switch
{
    BinaryExpression bin => AnalyzeBinary(bin),
    CallExpression call => AnalyzeCall(call),
    _ => throw new NotImplementedException()
};
```

### 4. Immutability Benefits

```csharp
// Records provide automatic structural equality
var expr1 = new LiteralExpression(42, TokenType.IntLiteral, loc);
var expr2 = new LiteralExpression(42, TokenType.IntLiteral, loc);
Assert.True(expr1 == expr2); // Structural equality

// Create modified copies with 'with' expressions
var modified = expr1 with { Value = 43 };
```

### 5. Null Safety

```csharp
// Nullable types document optional values
TypeExpression? returnType = null; // Optional return type
Expression? initializer = null;    // Optional initializer

// Use null-conditional operators
var hasReturn = declaration.ReturnType?.Name != null;
```

---

## Common Pitfalls

### 1. Forgetting to Handle All Visitor Methods

**Problem:**

```csharp
public class MyVisitor : IAstVisitor<string>
{
    public string VisitBinaryExpression(BinaryExpression node) => ...
    // Forgot to implement other methods - compilation error!
}
```

**Solution:** Implement all required methods or use a base class with default implementations.

### 2. Not Preserving Location Information

**Problem:**

```csharp
var transformed = new BinaryExpression(left, op, right, new SourceLocation(0, 0, 0));
// Lost original location!
```

**Solution:** Always propagate original location information.

### 3. Mutable State in Immutable Trees

**Problem:**

```csharp
// Trying to modify immutable node
node.Name = "newName"; // Compilation error
```

**Solution:** Use `with` expressions to create modified copies:

```csharp
var modified = node with { Name = "newName" };
```

---

## Performance Considerations

### Memory Usage

- AST nodes are immutable records stored on the heap
- Large programs can consume significant memory
- Consider streaming parsing for very large files

### Traversal Efficiency

- Visitor pattern has minimal overhead
- Avoid creating new visitors repeatedly; reuse instances when possible
- Use early returns in visitors to avoid unnecessary traversals

### Pattern Matching

- C# pattern matching is efficiently compiled
- Use pattern matching over type checking with `is`/`as`

---

## Extensibility

### Adding New Node Types

1. Define the record in the appropriate file (Expressions.cs, Statements.cs, Declarations.cs)
2. Add to the appropriate base class (Expression, Statement, Declaration)
3. Add visitor method to IAstVisitor<T>
4. Update all existing visitor implementations

### Custom Attributes

```csharp
// Extend with custom data using inheritance or composition
public record AnnotatedExpression(
    Expression Inner,
    Dictionary<string, object> Metadata,
    SourceLocation Location
) : Expression(Location);
```

---

## References

- **Source Files**: `src/AST/*.cs`
- **Test Files**: `tests/Parser/ParserTests.cs`, `tests/Analysis/SemanticAnalyzerTests.cs`
- **Related Documentation**:
    - Parser Implementation
    - Semantic Analyzer
    - Code Generator

---

## Changelog

- **v0.1**: Initial AST design with regions and comprehensive documentation
- Added extensive XML documentation comments
- Organized files with region directives for better navigation

---

*This document is automatically synchronized with the source code. Last updated: 2025-10-11*
