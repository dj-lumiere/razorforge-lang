using Compilers.Shared.AST;
using Compilers.Shared.Analysis;
using System.Text;

namespace Compilers.Shared.CodeGen;

/// <summary>
/// Simple code generator that outputs human-readable intermediate representation.
/// Used for testing, debugging, and as a reference implementation before
/// implementing full LLVM IR generation.
/// </summary>
/// <remarks>
/// This code generator implements the visitor pattern to traverse the AST
/// and produces textual output that represents the program structure.
/// The output includes:
/// <list type="bullet">
/// <item>Language and mode information in header comments</item>
/// <item>Properly indented nested structures</item>
/// <item>All expressions and statements in readable form</item>
/// <item>Type information where available</item>
/// </list>
///
/// This generator respects the target language (RazorForge vs Suflae) and
/// mode (Normal/Danger for RazorForge, Sweet/Bitter for Suflae) to produce
/// appropriate output formatting and comments.
/// </remarks>
public class SimpleCodeGenerator : IAstVisitor<string>
{
    /// <summary>StringBuilder for accumulating the generated output</summary>
    private readonly StringBuilder _output;

    /// <summary>Target language (RazorForge or Suflae)</summary>
    private readonly Language _language;

    /// <summary>Language mode affecting generation behavior</summary>
    private readonly LanguageMode _mode;

    /// <summary>Current indentation level for pretty-printing</summary>
    private int _indentLevel;

    /// <summary>
    /// Initializes a new simple code generator for the specified language and mode.
    /// </summary>
    /// <param name="language">The target language (RazorForge or Suflae)</param>
    /// <param name="mode">The language mode (Normal/Danger or Sweet/Bitter)</param>
    public SimpleCodeGenerator(Language language, LanguageMode mode)
    {
        _language = language;
        _mode = mode;
        _output = new StringBuilder();
        _indentLevel = 0;
    }

    /// <summary>
    /// Gets the complete generated code as a string.
    /// </summary>
    /// <returns>The generated code with proper formatting and indentation</returns>
    public string GetGeneratedCode()
    {
        return _output.ToString();
    }

    /// <summary>
    /// Generates code for the entire program by visiting the AST.
    /// Adds a header comment indicating the target language and mode,
    /// then processes all top-level declarations.
    /// </summary>
    /// <param name="program">The program AST node to generate code for</param>
    public void Generate(AST.Program program)
    {
        _output.AppendLine(handler: $"; Generated {_language} code ({_mode} mode)");
        _output.AppendLine();

        program.Accept(visitor: this);
    }

    private void WriteLine(string text = "")
    {
        if (!string.IsNullOrEmpty(value: text))
        {
            _output.AppendLine(value: new string(c: ' ', count: _indentLevel * 2) + text);
        }
        else
        {
            _output.AppendLine();
        }
    }

    private void Indent()
    {
        _indentLevel++;
    }
    private void Dedent()
    {
        _indentLevel--;
    }

    // AST Visitor Implementation

    public string VisitProgram(AST.Program node)
    {
        foreach (IAstNode declaration in node.Declarations)
        {
            declaration.Accept(visitor: this);
        }

        return "";
    }

    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        string typeStr = node.Type?.Name ?? "auto";
        string modStr = node.IsMutable
            ? "var"
            : "let";
        string visStr = node.Visibility != VisibilityModifier.Private
            ? $"{node.Visibility.ToString().ToLower()} "
            : "";

        if (node.Initializer != null)
        {
            string initValue = node.Initializer.Accept(visitor: this);
            WriteLine(text: $"{visStr}{modStr} {node.Name}: {typeStr} = {initValue}");
        }
        else
        {
            WriteLine(text: $"{visStr}{modStr} {node.Name}: {typeStr}");
        }

        return "";
    }

    public string VisitFunctionDeclaration(FunctionDeclaration node)
    {
        string visStr = node.Visibility != VisibilityModifier.Private
            ? $"{node.Visibility.ToString().ToLower()} "
            : "";
        string paramStr = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p => $"{p.Name}: {p.Type?.Name ?? "auto"}"));
        string returnStr = node.ReturnType?.Name ?? "void";

        WriteLine(text: $"{visStr}recipe {node.Name}({paramStr}) -> {returnStr}");
        WriteLine(text: "{");
        Indent();

        if (node.Body != null)
        {
            node.Body.Accept(visitor: this);
        }

        Dedent();
        WriteLine(text: "}");
        WriteLine();

        return "";
    }

    public string VisitClassDeclaration(ClassDeclaration node)
    {
        string visStr = node.Visibility != VisibilityModifier.Private
            ? $"{node.Visibility.ToString().ToLower()} "
            : "";
        string baseStr = node.BaseClass != null
            ? $" from {node.BaseClass.Name}"
            : "";

        WriteLine(text: $"{visStr}entity {node.Name}{baseStr}");
        WriteLine(text: "{");
        Indent();

        foreach (Declaration member in node.Members)
        {
            member.Accept(visitor: this);
        }

        Dedent();
        WriteLine(text: "}");
        WriteLine();

        return "";
    }

    public string VisitStructDeclaration(StructDeclaration node)
    {
        WriteLine(text: $"record {node.Name}");
        WriteLine(text: "{");
        WriteLine(text: "  ; record members would go here");
        WriteLine(text: "}");
        WriteLine();
        return "";
    }

    public string VisitMenuDeclaration(MenuDeclaration node)
    {
        WriteLine(text: $"enum {node.Name}");
        WriteLine(text: "{");
        WriteLine(text: "  ; enum variants would go here");
        WriteLine(text: "}");
        WriteLine();
        return "";
    }

    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        WriteLine(text: $"variant {node.Name}");
        WriteLine(text: "{");
        WriteLine(text: "  ; variant cases would go here");
        WriteLine(text: "}");
        WriteLine();
        return "";
    }

    public string VisitFeatureDeclaration(FeatureDeclaration node)
    {
        WriteLine(text: $"feature {node.Name}");
        WriteLine(text: "{");
        Indent();

        foreach (FunctionSignature method in node.Methods)
        {
            string paramStr = string.Join(separator: ", ",
                values: method.Parameters.Select(selector: p =>
                    $"{p.Name}: {p.Type?.Name ?? "auto"}"));
            string returnStr = method.ReturnType?.Name ?? "void";
            WriteLine(text: $"  {method.Name}({paramStr}) -> {returnStr}");
        }

        Dedent();
        WriteLine(text: "}");
        WriteLine();
        return "";
    }

    public string VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        WriteLine(text: "implementation block");
        return "";
    }

    public string VisitImportDeclaration(ImportDeclaration node)
    {
        WriteLine(text: "import statement");
        return "";
    }

    public string VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        WriteLine(text: $"redefine {node.OldName} as {node.NewName}");
        return "";
    }

    public string VisitUsingDeclaration(UsingDeclaration node)
    {
        WriteLine(text: $"using {node.Type.Accept(visitor: this)} as {node.Alias}");
        return "";
    }

    // Statements

    public string VisitExpressionStatement(ExpressionStatement node)
    {
        string expr = node.Expression.Accept(visitor: this);
        WriteLine(text: expr);
        return "";
    }

    public string VisitDeclarationStatement(DeclarationStatement node)
    {
        string decl = node.Declaration.Accept(visitor: this);
        return decl;
    }

    public string VisitAssignmentStatement(AssignmentStatement node)
    {
        string target = node.Target.Accept(visitor: this);
        string value = node.Value.Accept(visitor: this);
        WriteLine(text: $"{target} = {value}");
        return "";
    }

    public string VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value != null)
        {
            string value = node.Value.Accept(visitor: this);
            WriteLine(text: $"return {value}");
        }
        else
        {
            WriteLine(text: "return");
        }

        return "";
    }

    public string VisitIfStatement(IfStatement node)
    {
        string condition = node.Condition.Accept(visitor: this);
        WriteLine(text: $"if ({condition})");
        WriteLine(text: "{");
        Indent();
        node.ThenStatement.Accept(visitor: this);
        Dedent();
        WriteLine(text: "}");

        if (node.ElseStatement != null)
        {
            WriteLine(text: "else");
            WriteLine(text: "{");
            Indent();
            node.ElseStatement.Accept(visitor: this);
            Dedent();
            WriteLine(text: "}");
        }

        return "";
    }

    public string VisitWhileStatement(WhileStatement node)
    {
        string condition = node.Condition.Accept(visitor: this);
        WriteLine(text: $"while ({condition})");
        WriteLine(text: "{");
        Indent();
        node.Body.Accept(visitor: this);
        Dedent();
        WriteLine(text: "}");
        return "";
    }

    public string VisitForStatement(ForStatement node)
    {
        string iterable = node.Iterable.Accept(visitor: this);
        WriteLine(text: $"for {node.Variable} in {iterable}");
        WriteLine(text: "{");
        Indent();
        node.Body.Accept(visitor: this);
        Dedent();
        WriteLine(text: "}");
        return "";
    }

    public string VisitWhenStatement(WhenStatement node)
    {
        string expr = node.Expression.Accept(visitor: this);
        WriteLine(text: $"when {expr}");
        WriteLine(text: "{");
        Indent();

        foreach (WhenClause clause in node.Clauses)
        {
            // Pattern matching would go here
            WriteLine(text: "pattern => statement");
        }

        Dedent();
        WriteLine(text: "}");
        return "";
    }

    public string VisitBlockStatement(BlockStatement node)
    {
        foreach (Statement statement in node.Statements)
        {
            statement.Accept(visitor: this);
        }

        return "";
    }

    public string VisitBreakStatement(BreakStatement node)
    {
        WriteLine(text: "break");
        return "";
    }

    public string VisitContinueStatement(ContinueStatement node)
    {
        WriteLine(text: "continue");
        return "";
    }

    // Expressions

    public string VisitLiteralExpression(LiteralExpression node)
    {
        return node.Value switch
        {
            bool b => b.ToString()
                       .ToLower(),
            int i => i.ToString(),
            long l => l.ToString() + "L",
            float f => f.ToString() + "f",
            double d => d.ToString(),
            string s => $"\"{s}\"",
            null => "null",
            _ => node.Value?.ToString() ?? "unknown"
        };
    }

    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        return node.Name;
    }

    public string VisitBinaryExpression(BinaryExpression node)
    {
        string left = node.Left.Accept(visitor: this);
        string right = node.Right.Accept(visitor: this);
        string op = node.Operator switch
        {
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.Divide => "/",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Less => "<",
            BinaryOperator.LessEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterEqual => ">=",
            BinaryOperator.And => "&&",
            BinaryOperator.Or => "||",
            _ => "?"
        };

        return $"({left} {op} {right})";
    }

    public string VisitUnaryExpression(UnaryExpression node)
    {
        string operand = node.Operand.Accept(visitor: this);
        string op = node.Operator switch
        {
            UnaryOperator.Minus => "-",
            UnaryOperator.Not => "!",
            _ => "?"
        };

        return $"{op}{operand}";
    }

    public string VisitCallExpression(CallExpression node)
    {
        string callee = node.Callee.Accept(visitor: this);
        string args = string.Join(separator: ", ",
            values: node.Arguments.Select(selector: arg => arg.Accept(visitor: this)));
        return $"{callee}({args})";
    }

    public string VisitMemberExpression(MemberExpression node)
    {
        string obj = node.Object.Accept(visitor: this);
        return $"{obj}.{node.PropertyName}";
    }

    public string VisitIndexExpression(IndexExpression node)
    {
        string obj = node.Object.Accept(visitor: this);
        string index = node.Index.Accept(visitor: this);
        return $"{obj}[{index}]";
    }

    public string VisitConditionalExpression(ConditionalExpression node)
    {
        string condition = node.Condition.Accept(visitor: this);
        string trueExpr = node.TrueExpression.Accept(visitor: this);
        string falseExpr = node.FalseExpression.Accept(visitor: this);
        return $"({condition} ? {trueExpr} : {falseExpr})";
    }

    public string VisitRangeExpression(RangeExpression node)
    {
        string start = node.Start.Accept(visitor: this);
        string end = node.End.Accept(visitor: this);

        if (node.Step != null)
        {
            string step = node.Step.Accept(visitor: this);
            return $"({start} to {end} step {step})";
        }
        else
        {
            return $"({start} to {end})";
        }
    }

    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        if (node.Operands.Count < 2)
        {
            return "";
        }

        var result = new StringBuilder();
        result.Append(value: "(");

        for (int i = 0; i < node.Operators.Count; i++)
        {
            if (i > 0)
            {
                result.Append(value: " and ");
            }

            result.Append(value: node.Operands[index: i]
                                     .Accept(visitor: this));
            result.Append(value: " ");
            result.Append(value: OperatorToString(op: node.Operators[index: i]));
            result.Append(value: " ");
            result.Append(value: node.Operands[index: i + 1]
                                     .Accept(visitor: this));
        }

        result.Append(value: ")");
        return result.ToString();
    }

    private string OperatorToString(BinaryOperator op)
    {
        return op switch
        {
            BinaryOperator.Less => "<",
            BinaryOperator.LessEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterEqual => ">=",
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.In => "in",
            BinaryOperator.NotIn => "not in",
            BinaryOperator.Is => "is",
            BinaryOperator.IsNot => "is not",
            BinaryOperator.From => "from",
            BinaryOperator.NotFrom => "not from",
            BinaryOperator.Follows => "follows",
            BinaryOperator.NotFollows => "not follows",
            _ => "=="
        };
    }

    public string VisitLambdaExpression(LambdaExpression node)
    {
        string params_ = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p => p.Name));
        string body = node.Body.Accept(visitor: this);
        return $"({params_}) => {body}";
    }

    public string VisitTypeExpression(TypeExpression node)
    {
        return node.Name;
    }

    public string VisitTypeConversionExpression(TypeConversionExpression node)
    {
        string expr = node.Expression.Accept(visitor: this);

        if (node.IsMethodStyle)
        {
            return $"{expr}.{node.TargetType}!()";
        }
        else
        {
            return $"{node.TargetType}!({expr})";
        }
    }

    // Memory slice expression visitor methods
    public string VisitSliceConstructorExpression(SliceConstructorExpression node)
    {
        string size = node.SizeExpression.Accept(visitor: this);
        return $"{node.SliceType}({size})";
    }

    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        string obj = node.Object.Accept(visitor: this);
        string typeArgs = string.Join(separator: ", ",
            values: node.TypeArguments.Select(selector: t => t.Accept(visitor: this)));
        string args = string.Join(separator: ", ",
            values: node.Arguments.Select(selector: a => a.Accept(visitor: this)));
        string bang = node.IsMemoryOperation
            ? "!"
            : "";
        return $"{obj}.{node.MethodName}<{typeArgs}>{bang}({args})";
    }

    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        string obj = node.Object.Accept(visitor: this);
        string typeArgs = string.Join(separator: ", ",
            values: node.TypeArguments.Select(selector: t => t.Accept(visitor: this)));
        return $"{obj}.{node.MemberName}<{typeArgs}>";
    }

    public string VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        string obj = node.Object.Accept(visitor: this);
        string args = string.Join(separator: ", ",
            values: node.Arguments.Select(selector: a => a.Accept(visitor: this)));
        return $"{obj}.{node.OperationName}!({args})";
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        WriteLine(text: "danger! {");
        _indentLevel++;
        node.Body.Accept(visitor: this);
        _indentLevel--;
        WriteLine(text: "}");
        return "";
    }

    public string VisitMayhemStatement(MayhemStatement node)
    {
        WriteLine(text: "mayhem! {");
        _indentLevel++;
        node.Body.Accept(visitor: this);
        _indentLevel--;
        WriteLine(text: "}");
        return "";
    }

    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        var sb = new StringBuilder();
        sb.Append(value: "external recipe ");
        sb.Append(value: node.Name);

        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            sb.Append(value: "<");
            sb.Append(value: string.Join(separator: ", ", values: node.GenericParameters));
            sb.Append(value: ">");
        }

        sb.Append(value: "!(");
        if (node.Parameters.Count > 0)
        {
            sb.Append(value: string.Join(separator: ", ",
                values: node.Parameters.Select(selector: p =>
                    $"{p.Name}: {p.Type?.Accept(visitor: this) ?? "unknown"}")));
        }

        sb.Append(value: ")");

        if (node.ReturnType != null)
        {
            sb.Append(value: " -> ");
            sb.Append(value: node.ReturnType.Accept(visitor: this));
        }

        WriteLine(text: sb.ToString());
        return "";
    }

    public string VisitViewingStatement(ViewingStatement node)
    {
        // TODO: Implement viewing statement code generation
        throw new NotImplementedException(
            message: "viewing statement not yet implemented in simple code generator");
    }

    public string VisitHijackingStatement(HijackingStatement node)
    {
        // TODO: Implement hijacking statement code generation
        throw new NotImplementedException(
            message: "hijacking statement not yet implemented in simple code generator");
    }

    public string VisitWitnessingStatement(WitnessingStatement node)
    {
        // TODO: Implement witnessing statement code generation
        throw new NotImplementedException(
            message: "witnessing statement not yet implemented in simple code generator");
    }

    public string VisitSeizingStatement(SeizingStatement node)
    {
        // TODO: Implement seizing statement code generation
        throw new NotImplementedException(
            message: "seizing statement not yet implemented in simple code generator");
    }
}
