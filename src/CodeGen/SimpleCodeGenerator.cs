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
/// This generator respects the target language (RazorForge vs Cake) and
/// mode (Normal/Danger for RazorForge, Sweet/Bitter for Cake) to produce
/// appropriate output formatting and comments.
/// </remarks>
public class SimpleCodeGenerator : IAstVisitor<string>
{
    /// <summary>StringBuilder for accumulating the generated output</summary>
    private readonly StringBuilder _output;

    /// <summary>Target language (RazorForge or Cake)</summary>
    private readonly Language _language;

    /// <summary>Language mode affecting generation behavior</summary>
    private readonly LanguageMode _mode;

    /// <summary>Current indentation level for pretty-printing</summary>
    private int _indentLevel;

    /// <summary>
    /// Initializes a new simple code generator for the specified language and mode.
    /// </summary>
    /// <param name="language">The target language (RazorForge or Cake)</param>
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
    public string GetGeneratedCode() => _output.ToString();
    
    /// <summary>
    /// Generates code for the entire program by visiting the AST.
    /// Adds a header comment indicating the target language and mode,
    /// then processes all top-level declarations.
    /// </summary>
    /// <param name="program">The program AST node to generate code for</param>
    public void Generate(AST.Program program)
    {
        _output.AppendLine($"; Generated {_language} code ({_mode} mode)");
        _output.AppendLine();
        
        program.Accept(this);
    }
    
    private void WriteLine(string text = "")
    {
        if (!string.IsNullOrEmpty(text))
        {
            _output.AppendLine(new string(' ', _indentLevel * 2) + text);
        }
        else
        {
            _output.AppendLine();
        }
    }
    
    private void Indent() => _indentLevel++;
    private void Dedent() => _indentLevel--;
    
    // AST Visitor Implementation
    
    public string VisitProgram(AST.Program node)
    {
        foreach (var declaration in node.Declarations)
        {
            declaration.Accept(this);
        }
        return "";
    }
    
    public string VisitVariableDeclaration(VariableDeclaration node)
    {
        var typeStr = node.Type?.Name ?? "auto";
        var modStr = node.IsMutable ? "var" : "let";
        var visStr = node.Visibility != VisibilityModifier.Private ? $"{node.Visibility.ToString().ToLower()} " : "";
        
        if (node.Initializer != null)
        {
            var initValue = node.Initializer.Accept(this);
            WriteLine($"{visStr}{modStr} {node.Name}: {typeStr} = {initValue}");
        }
        else
        {
            WriteLine($"{visStr}{modStr} {node.Name}: {typeStr}");
        }
        
        return "";
    }
    
    public string VisitFunctionDeclaration(FunctionDeclaration node)
    {
        var visStr = node.Visibility != VisibilityModifier.Private ? $"{node.Visibility.ToString().ToLower()} " : "";
        var paramStr = string.Join(", ", node.Parameters.Select(p => $"{p.Name}: {p.Type?.Name ?? "auto"}"));
        var returnStr = node.ReturnType?.Name ?? "void";
        
        WriteLine($"{visStr}func {node.Name}({paramStr}) -> {returnStr}");
        WriteLine("{");
        Indent();
        
        if (node.Body != null)
        {
            node.Body.Accept(this);
        }
        
        Dedent();
        WriteLine("}");
        WriteLine();
        
        return "";
    }
    
    public string VisitClassDeclaration(ClassDeclaration node)
    {
        var visStr = node.Visibility != VisibilityModifier.Private ? $"{node.Visibility.ToString().ToLower()} " : "";
        var baseStr = node.BaseClass != null ? $" from {node.BaseClass.Name}" : "";
        
        WriteLine($"{visStr}entity {node.Name}{baseStr}");
        WriteLine("{");
        Indent();
        
        foreach (var member in node.Members)
        {
            member.Accept(this);
        }
        
        Dedent();
        WriteLine("}");
        WriteLine();
        
        return "";
    }
    
    public string VisitStructDeclaration(StructDeclaration node)
    {
        WriteLine($"record {node.Name}");
        WriteLine("{");
        WriteLine("  ; record members would go here");
        WriteLine("}");
        WriteLine();
        return "";
    }
    
    public string VisitMenuDeclaration(MenuDeclaration node)
    {
        WriteLine($"enum {node.Name}");
        WriteLine("{");
        WriteLine("  ; enum variants would go here");
        WriteLine("}");
        WriteLine();
        return "";
    }
    
    public string VisitVariantDeclaration(VariantDeclaration node)
    {
        WriteLine($"variant {node.Name}");
        WriteLine("{");
        WriteLine("  ; variant cases would go here");
        WriteLine("}");
        WriteLine();
        return "";
    }
    
    public string VisitFeatureDeclaration(FeatureDeclaration node)
    {
        WriteLine($"feature {node.Name}");
        WriteLine("{");
        Indent();
        
        foreach (var method in node.Methods)
        {
            var paramStr = string.Join(", ", method.Parameters.Select(p => $"{p.Name}: {p.Type?.Name ?? "auto"}"));
            var returnStr = method.ReturnType?.Name ?? "void";
            WriteLine($"  {method.Name}({paramStr}) -> {returnStr}");
        }
        
        Dedent();
        WriteLine("}");
        WriteLine();
        return "";
    }
    
    public string VisitImplementationDeclaration(ImplementationDeclaration node)
    {
        WriteLine("implementation block");
        return "";
    }
    
    public string VisitImportDeclaration(ImportDeclaration node)
    {
        WriteLine("import statement");
        return "";
    }
    
    public string VisitRedefinitionDeclaration(RedefinitionDeclaration node)
    {
        WriteLine($"redefine {node.OldName} as {node.NewName}");
        return "";
    }
    
    public string VisitUsingDeclaration(UsingDeclaration node)
    {
        WriteLine($"using {node.Type.Accept(this)} as {node.Alias}");
        return "";
    }
    
    // Statements

    public string VisitExpressionStatement(ExpressionStatement node)
    {
        var expr = node.Expression.Accept(this);
        WriteLine(expr);
        return "";
    }

    public string VisitDeclarationStatement(DeclarationStatement node)
    {
        var decl = node.Declaration.Accept(this);
        return decl;
    }
    
    public string VisitAssignmentStatement(AssignmentStatement node)
    {
        var target = node.Target.Accept(this);
        var value = node.Value.Accept(this);
        WriteLine($"{target} = {value}");
        return "";
    }
    
    public string VisitReturnStatement(ReturnStatement node)
    {
        if (node.Value != null)
        {
            var value = node.Value.Accept(this);
            WriteLine($"return {value}");
        }
        else
        {
            WriteLine("return");
        }
        return "";
    }
    
    public string VisitIfStatement(IfStatement node)
    {
        var condition = node.Condition.Accept(this);
        WriteLine($"if ({condition})");
        WriteLine("{");
        Indent();
        node.ThenStatement.Accept(this);
        Dedent();
        WriteLine("}");
        
        if (node.ElseStatement != null)
        {
            WriteLine("else");
            WriteLine("{");
            Indent();
            node.ElseStatement.Accept(this);
            Dedent();
            WriteLine("}");
        }
        
        return "";
    }
    
    public string VisitWhileStatement(WhileStatement node)
    {
        var condition = node.Condition.Accept(this);
        WriteLine($"while ({condition})");
        WriteLine("{");
        Indent();
        node.Body.Accept(this);
        Dedent();
        WriteLine("}");
        return "";
    }
    
    public string VisitForStatement(ForStatement node)
    {
        var iterable = node.Iterable.Accept(this);
        WriteLine($"for {node.Variable} in {iterable}");
        WriteLine("{");
        Indent();
        node.Body.Accept(this);
        Dedent();
        WriteLine("}");
        return "";
    }
    
    public string VisitWhenStatement(WhenStatement node)
    {
        var expr = node.Expression.Accept(this);
        WriteLine($"when {expr}");
        WriteLine("{");
        Indent();
        
        foreach (var clause in node.Clauses)
        {
            // Pattern matching would go here
            WriteLine("pattern => statement");
        }
        
        Dedent();
        WriteLine("}");
        return "";
    }
    
    public string VisitBlockStatement(BlockStatement node)
    {
        foreach (var statement in node.Statements)
        {
            statement.Accept(this);
        }
        return "";
    }
    
    public string VisitBreakStatement(BreakStatement node)
    {
        WriteLine("break");
        return "";
    }
    
    public string VisitContinueStatement(ContinueStatement node)
    {
        WriteLine("continue");
        return "";
    }
    
    // Expressions
    
    public string VisitLiteralExpression(LiteralExpression node)
    {
        return node.Value switch
        {
            bool b => b.ToString().ToLower(),
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
        var left = node.Left.Accept(this);
        var right = node.Right.Accept(this);
        var op = node.Operator switch
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
        var operand = node.Operand.Accept(this);
        var op = node.Operator switch
        {
            UnaryOperator.Minus => "-",
            UnaryOperator.Not => "!",
            _ => "?"
        };
        
        return $"{op}{operand}";
    }
    
    public string VisitCallExpression(CallExpression node)
    {
        var callee = node.Callee.Accept(this);
        var args = string.Join(", ", node.Arguments.Select(arg => arg.Accept(this)));
        return $"{callee}({args})";
    }
    
    public string VisitMemberExpression(MemberExpression node)
    {
        var obj = node.Object.Accept(this);
        return $"{obj}.{node.PropertyName}";
    }
    
    public string VisitIndexExpression(IndexExpression node)
    {
        var obj = node.Object.Accept(this);
        var index = node.Index.Accept(this);
        return $"{obj}[{index}]";
    }
    
    public string VisitConditionalExpression(ConditionalExpression node)
    {
        var condition = node.Condition.Accept(this);
        var trueExpr = node.TrueExpression.Accept(this);
        var falseExpr = node.FalseExpression.Accept(this);
        return $"({condition} ? {trueExpr} : {falseExpr})";
    }
    
    public string VisitRangeExpression(RangeExpression node)
    {
        var start = node.Start.Accept(this);
        var end = node.End.Accept(this);
        
        if (node.Step != null)
        {
            var step = node.Step.Accept(this);
            return $"({start} to {end} step {step})";
        }
        else
        {
            return $"({start} to {end})";
        }
    }
    
    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        if (node.Operands.Count < 2) return "";
        
        var result = new StringBuilder();
        result.Append("(");
        
        for (int i = 0; i < node.Operators.Count; i++)
        {
            if (i > 0) result.Append(" and ");
            
            result.Append(node.Operands[i].Accept(this));
            result.Append(" ");
            result.Append(OperatorToString(node.Operators[i]));
            result.Append(" ");
            result.Append(node.Operands[i + 1].Accept(this));
        }
        
        result.Append(")");
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
        var params_ = string.Join(", ", node.Parameters.Select(p => p.Name));
        var body = node.Body.Accept(this);
        return $"({params_}) => {body}";
    }
    
    public string VisitTypeExpression(TypeExpression node)
    {
        return node.Name;
    }
    
    public string VisitTypeConversionExpression(TypeConversionExpression node)
    {
        var expr = node.Expression.Accept(this);

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
        var size = node.SizeExpression.Accept(this);
        return $"{node.SliceType}({size})";
    }

    public string VisitGenericMethodCallExpression(GenericMethodCallExpression node)
    {
        var obj = node.Object.Accept(this);
        var typeArgs = string.Join(", ", node.TypeArguments.Select(t => t.Accept(this)));
        var args = string.Join(", ", node.Arguments.Select(a => a.Accept(this)));
        var bang = node.IsMemoryOperation ? "!" : "";
        return $"{obj}.{node.MethodName}<{typeArgs}>{bang}({args})";
    }

    public string VisitGenericMemberExpression(GenericMemberExpression node)
    {
        var obj = node.Object.Accept(this);
        var typeArgs = string.Join(", ", node.TypeArguments.Select(t => t.Accept(this)));
        return $"{obj}.{node.MemberName}<{typeArgs}>";
    }

    public string VisitMemoryOperationExpression(MemoryOperationExpression node)
    {
        var obj = node.Object.Accept(this);
        var args = string.Join(", ", node.Arguments.Select(a => a.Accept(this)));
        return $"{obj}.{node.OperationName}!({args})";
    }

    public string VisitDangerStatement(DangerStatement node)
    {
        WriteLine("danger! {");
        _indentLevel++;
        node.Body.Accept(this);
        _indentLevel--;
        WriteLine("}");
        return "";
    }

    public string VisitMayhemStatement(MayhemStatement node)
    {
        WriteLine("mayhem! {");
        _indentLevel++;
        node.Body.Accept(this);
        _indentLevel--;
        WriteLine("}");
        return "";
    }

    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        var sb = new StringBuilder();
        sb.Append("external recipe ");
        sb.Append(node.Name);

        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            sb.Append("<");
            sb.Append(string.Join(", ", node.GenericParameters));
            sb.Append(">");
        }

        sb.Append("!(");
        if (node.Parameters.Count > 0)
        {
            sb.Append(string.Join(", ", node.Parameters.Select(p => $"{p.Name}: {p.Type?.Accept(this) ?? "unknown"}")));
        }
        sb.Append(")");

        if (node.ReturnType != null)
        {
            sb.Append(" -> ");
            sb.Append(node.ReturnType.Accept(this));
        }

        WriteLine(sb.ToString());
        return "";
    }
}