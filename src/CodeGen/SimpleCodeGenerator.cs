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

    /// <summary>Generic function templates for monomorphization</summary>
    private readonly Dictionary<string, FunctionDeclaration> _genericFunctionTemplates = new();

    /// <summary>Tracks instantiated generic functions to avoid duplicates</summary>
    private readonly HashSet<string> _instantiatedGenerics = [];

    /// <summary>Current type substitutions for generic function body generation</summary>
    private Dictionary<string, string>? _currentTypeSubstitutions;

    /// <summary>Loaded modules from semantic analysis for import expansion</summary>
    private IReadOnlyDictionary<string, ModuleResolver.ModuleInfo>? _loadedModules;

    /// <summary>Tracks already expanded modules to avoid duplicates</summary>
    private readonly HashSet<string> _expandedModules = [];

    /// <summary>
    /// Sets or gets the loaded modules for import expansion.
    /// </summary>
    public IReadOnlyDictionary<string, ModuleResolver.ModuleInfo>? LoadedModules
    {
        get => _loadedModules;
        set => _loadedModules = value;
    }

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

    private string GetIndent()
    {
        return new string(c: ' ', count: _indentLevel * 2);
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
        // Check if this is a generic function (has type parameters)
        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            // Store the template for later instantiation - don't generate code yet
            _genericFunctionTemplates[key: node.Name] = node;
            string genericParams = string.Join(separator: ", ", values: node.GenericParameters);
            WriteLine(text: $"; Generic function template: {node.Name}<{genericParams}>");
            WriteLine();
            return "";
        }

        // Non-generic function - generate code normally
        return GenerateFunctionCode(node: node, typeSubstitutions: null);
    }

    /// <summary>
    /// Generates code for a function, optionally applying type substitutions for generic instantiation.
    /// </summary>
    private string GenerateFunctionCode(FunctionDeclaration node,
        Dictionary<string, string>? typeSubstitutions, string? mangledName = null)
    {
        string visStr = node.Visibility != VisibilityModifier.Private
            ? $"{node.Visibility.ToString().ToLower()} "
            : "";
        string paramStr = string.Join(separator: ", ",
            values: node.Parameters.Select(selector: p =>
            {
                string typeName = p.Type?.Name ?? "auto";
                // Apply type substitution if available
                if (typeSubstitutions != null &&
                    typeSubstitutions.TryGetValue(key: typeName, value: out string? concrete))
                {
                    typeName = concrete;
                }

                return $"{p.Name}: {typeName}";
            }));

        string returnStr = node.ReturnType?.Name ?? "Blank";
        // Apply type substitution to return type
        if (typeSubstitutions != null &&
            typeSubstitutions.TryGetValue(key: returnStr, value: out string? concreteReturn))
        {
            returnStr = concreteReturn;
        }

        string funcName = mangledName ?? node.Name;
        WriteLine(text: $"{visStr}routine {funcName}({paramStr}) -> {returnStr}");
        WriteLine(text: "{");
        Indent();

        // Store type substitutions for use in body generation
        _currentTypeSubstitutions = typeSubstitutions;

        if (node.Body != null)
        {
            node.Body.Accept(visitor: this);
        }

        // Clear type substitutions
        _currentTypeSubstitutions = null;

        Dedent();
        WriteLine(text: "}");
        WriteLine();

        return "";
    }

    /// <summary>
    /// Instantiates a generic function with concrete type arguments.
    /// </summary>
    public string InstantiateGenericFunction(string functionName, List<string> typeArguments)
    {
        // Check if we have the template
        if (!_genericFunctionTemplates.TryGetValue(key: functionName,
                value: out FunctionDeclaration? template))
        {
            return functionName;
        }

        // Create mangled name
        string mangledName =
            $"{functionName}_{string.Join(separator: "_", values: typeArguments)}";

        // Check if already instantiated
        if (_instantiatedGenerics.Contains(item: mangledName))
        {
            return mangledName;
        }

        // Mark as instantiated
        _instantiatedGenerics.Add(item: mangledName);

        // Create type substitution map
        var substitutions = new Dictionary<string, string>();
        for (int i = 0;
             i < Math.Min(val1: template.GenericParameters!.Count, val2: typeArguments.Count);
             i++)
        {
            substitutions[key: template.GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Generate the instantiated function code
        GenerateFunctionCode(node: template,
            typeSubstitutions: substitutions,
            mangledName: mangledName);

        return mangledName;
    }

    public string VisitEntityDeclaration(EntityDeclaration node)
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

    public string VisitRecordDeclaration(RecordDeclaration node)
    {
        WriteLine(text: $"record {node.Name}");
        WriteLine(text: "{");
        WriteLine(text: "  ; record members would go here");
        WriteLine(text: "}");
        WriteLine();
        return "";
    }

    public string VisitChoiceDeclaration(ChoiceDeclaration node)
    {
        WriteLine(text: $"choice {node.Name}");
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

    public string VisitProtocolDeclaration(ProtocolDeclaration node)
    {
        WriteLine(text: $"protocol {node.Name}");
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
        string modulePath = node.ModulePath;

        // Check if we've already expanded this module
        if (_expandedModules.Contains(item: modulePath))
        {
            WriteLine(text: $"; (already included: {modulePath})");
            return "";
        }

        // Mark as expanded to prevent infinite recursion
        _expandedModules.Add(item: modulePath);

        // Try to find the module in loaded modules
        if (_loadedModules != null && _loadedModules.TryGetValue(key: modulePath, value: out ModuleResolver.ModuleInfo? moduleInfo))
        {
            WriteLine(text: $"; ============================================================================");
            WriteLine(text: $"; BEGIN IMPORT: {modulePath}");
            WriteLine(text: $"; Source: {moduleInfo.FilePath}");
            WriteLine(text: $"; ============================================================================");
            WriteLine();

            // Visit all declarations from the imported module
            foreach (IAstNode declaration in moduleInfo.Ast.Declarations)
            {
                declaration.Accept(visitor: this);
            }

            WriteLine(text: $"; ============================================================================");
            WriteLine(text: $"; END IMPORT: {modulePath}");
            WriteLine(text: $"; ============================================================================");
            WriteLine();
        }
        else
        {
            // Module not found in loaded modules - just output the import statement
            WriteLine(text: $"import {modulePath}  ; (module not loaded)");
        }

        return "";
    }

    public string VisitNamespaceDeclaration(NamespaceDeclaration node)
    {
        WriteLine(text: $"namespace {node.Path}");
        return "";
    }

    public string VisitDefineDeclaration(RedefinitionDeclaration node)
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
            string patternStr = FormatPattern(pattern: clause.Pattern);

            // Check if body is a simple expression statement (single-line case)
            if (clause.Body is ExpressionStatement exprStmt)
            {
                string bodyStr = exprStmt.Expression.Accept(visitor: this);
                WriteLine(text: $"{patternStr} => {bodyStr}");
            }
            else if (clause.Body is BlockStatement blockStmt && blockStmt.Statements.Count == 1 &&
                     blockStmt.Statements[index: 0] is ExpressionStatement singleExpr)
            {
                string bodyStr = singleExpr.Expression.Accept(visitor: this);
                WriteLine(text: $"{patternStr} => {bodyStr}");
            }
            else
            {
                // Multi-statement body - output as block
                WriteLine(text: $"{patternStr} =>");
                WriteLine(text: "{");
                Indent();
                clause.Body.Accept(visitor: this);
                Dedent();
                WriteLine(text: "}");
            }
        }

        Dedent();
        WriteLine(text: "}");
        return "";
    }

    /// <summary>
    /// Formats a pattern for output in when statements.
    /// </summary>
    private string FormatPattern(Pattern pattern)
    {
        return pattern switch
        {
            LiteralPattern lit => FormatLiteralValue(value: lit.Value),
            IdentifierPattern id => id.Name,
            WildcardPattern => "_",
            ExpressionPattern expr => expr.Expression.Accept(visitor: this),
            TypePattern tp => tp.VariableName != null
                ? $"is {tp.Type.Accept(visitor: this)} {tp.VariableName}"
                : $"is {tp.Type.Accept(visitor: this)}",
            _ => pattern.ToString() ?? "?"
        };
    }

    /// <summary>
    /// Formats a literal value for pattern output.
    /// </summary>
    private string FormatLiteralValue(object value, string? typeName = null)
    {
        return value switch
        {
            bool b => b.ToString().ToLower(),
            string s => $"\"{s}\"",
            null => "null",
            _ => FormatNumericLiteral(value: value, suffix: typeName)
        };
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

    public string VisitThrowStatement(ThrowStatement node)
    {
        string error = node.Error.Accept(visitor: this);
        WriteLine(text: $"throw {error}");
        return "";
    }

    public string VisitAbsentStatement(AbsentStatement node)
    {
        WriteLine(text: "absent");
        return "";
    }

    public string VisitPassStatement(PassStatement node)
    {
        WriteLine(text: "pass");
        return "";
    }

    public string VisitPresetDeclaration(PresetDeclaration node)
    {
        string typeStr = node.Type.Accept(visitor: this);
        string valueStr = node.Value.Accept(visitor: this);
        WriteLine(text: $"preset {node.Name}: {typeStr} = {valueStr}");
        return "";
    }

    // Expressions

    public string VisitLiteralExpression(LiteralExpression node)
    {
        // Get the type suffix from ResolvedType if available
        string? suffix = node.ResolvedType?.Name;

        return node.Value switch
        {
            bool b => b.ToString().ToLower(),
            string s => $"\"{s}\"",
            null => "null",
            _ => FormatNumericLiteral(value: node.Value, suffix: suffix)
        };
    }

    /// <summary>
    /// Formats a numeric literal with the appropriate RazorForge type suffix.
    /// </summary>
    private string FormatNumericLiteral(object value, string? suffix)
    {
        string valueStr = value switch
        {
            float f => f.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            double d => d.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            decimal dec => dec.ToString(provider: System.Globalization.CultureInfo.InvariantCulture),
            _ => value.ToString() ?? "0"
        };

        return string.IsNullOrEmpty(value: suffix) ? valueStr : $"{valueStr}_{suffix}";
    }

    public string VisitListLiteralExpression(ListLiteralExpression node)
    {
        IEnumerable<string> elements =
            node.Elements.Select(selector: e => e.Accept(visitor: this));
        return $"[{string.Join(separator: ", ", values: elements)}]";
    }

    public string VisitSetLiteralExpression(SetLiteralExpression node)
    {
        IEnumerable<string> elements =
            node.Elements.Select(selector: e => e.Accept(visitor: this));
        return $"{{{string.Join(separator: ", ", values: elements)}}}";
    }

    public string VisitDictLiteralExpression(DictLiteralExpression node)
    {
        IEnumerable<string> pairs = node.Pairs.Select(selector: p =>
            $"{p.Key.Accept(visitor: this)}: {p.Value.Accept(visitor: this)}");
        return $"{{{string.Join(separator: ", ", values: pairs)}}}";
    }

    public string VisitIdentifierExpression(IdentifierExpression node)
    {
        return node.Name;
    }

    public string VisitBinaryExpression(BinaryExpression node)
    {
        string left = node.Left.Accept(visitor: this);
        string right = node.Right.Accept(visitor: this);
        string op = OperatorToString(op: node.Operator);

        return $"({left} {op} {right})";
    }

    public string VisitUnaryExpression(UnaryExpression node)
    {
        string operand = node.Operand.Accept(visitor: this);
        string op = node.Operator switch
        {
            UnaryOperator.Minus => "-",
            UnaryOperator.Not => "not",
            UnaryOperator.BitwiseNot => "~",
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

    public string VisitBlockExpression(BlockExpression node)
    {
        // A block expression evaluates to its inner expression
        return node.Value.Accept(visitor: this);
    }

    public string VisitRangeExpression(RangeExpression node)
    {
        string start = node.Start.Accept(visitor: this);
        string end = node.End.Accept(visitor: this);
        string rangeOp = node.IsDescending
            ? "downto"
            : "to";

        if (node.Step != null)
        {
            string step = node.Step.Accept(visitor: this);
            return $"({start} {rangeOp} {end} by {step})";
        }
        else
        {
            return $"({start} {rangeOp} {end})";
        }
    }

    public string VisitChainedComparisonExpression(ChainedComparisonExpression node)
    {
        if (node.Operands.Count < 2)
        {
            return "";
        }

        var result = new StringBuilder();
        result.Append(value: '(');

        for (int i = 0; i < node.Operators.Count; i++)
        {
            if (i > 0)
            {
                result.Append(value: " and ");
            }

            result.Append(value: node.Operands[index: i]
                                     .Accept(visitor: this));
            result.Append(value: ' ');
            result.Append(value: OperatorToString(op: node.Operators[index: i]));
            result.Append(value: ' ');
            result.Append(value: node.Operands[index: i + 1]
                                     .Accept(visitor: this));
        }

        result.Append(value: ')');
        return result.ToString();
    }

    private string OperatorToString(BinaryOperator op)
    {
        return op switch
        {
            // Arithmetic - standard operations
            BinaryOperator.Add => "+",
            BinaryOperator.Subtract => "-",
            BinaryOperator.Multiply => "*",
            BinaryOperator.TrueDivide => "/",
            BinaryOperator.FloorDivide => "//",
            BinaryOperator.Modulo => "%",
            BinaryOperator.Power => "**",

            // Arithmetic with overflow handling - Wrap
            BinaryOperator.AddWrap => "+%",
            BinaryOperator.SubtractWrap => "-%",
            BinaryOperator.MultiplyWrap => "*%",
            BinaryOperator.PowerWrap => "**%",

            // Arithmetic with overflow handling - Saturate
            BinaryOperator.AddSaturate => "+^",
            BinaryOperator.SubtractSaturate => "-^",
            BinaryOperator.MultiplySaturate => "*^",
            BinaryOperator.PowerSaturate => "**^",

            // Arithmetic with overflow handling - Checked
            BinaryOperator.AddChecked => "+?",
            BinaryOperator.SubtractChecked => "-?",
            BinaryOperator.MultiplyChecked => "*?",
            BinaryOperator.FloorDivideChecked => "//?",
            BinaryOperator.ModuloChecked => "%?",
            BinaryOperator.PowerChecked => "**?",

            // Comparison - equality and relational
            BinaryOperator.Equal => "==",
            BinaryOperator.NotEqual => "!=",
            BinaryOperator.Identical => "===",
            BinaryOperator.NotIdentical => "!==",
            BinaryOperator.Less => "<",
            BinaryOperator.LessEqual => "<=",
            BinaryOperator.Greater => ">",
            BinaryOperator.GreaterEqual => ">=",

            // Comparison - membership and type
            BinaryOperator.In => "in",
            BinaryOperator.NotIn => "notin",
            BinaryOperator.Is => "is",
            BinaryOperator.IsNot => "isnot",
            BinaryOperator.From => "from",
            BinaryOperator.NotFrom => "notfrom",
            BinaryOperator.Follows => "follows",
            BinaryOperator.NotFollows => "notfollows",

            // Logical
            BinaryOperator.And => "and",
            BinaryOperator.Or => "or",

            // Bitwise
            BinaryOperator.BitwiseAnd => "&",
            BinaryOperator.BitwiseOr => "|",
            BinaryOperator.BitwiseXor => "^",
            BinaryOperator.ArithmeticLeftShift => "<<",
            BinaryOperator.ArithmeticLeftShiftChecked => "<<?",
            BinaryOperator.ArithmeticRightShift => ">>",
            BinaryOperator.LogicalLeftShift => "<<<",
            BinaryOperator.LogicalRightShift => ">>>",

            // Assignment
            BinaryOperator.Assign => "=",

            // None coalescing
            BinaryOperator.NoneCoalesce => "??",

            _ => "?"
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
        // Check if this is a user-defined generic function that needs instantiation
        if (node.Object is IdentifierExpression funcIdentifier &&
            _genericFunctionTemplates.ContainsKey(key: funcIdentifier.Name))
        {
            // Get the concrete type arguments
            var typeArgList = node.TypeArguments
                                  .Select(selector: t => t.Name)
                                  .ToList();

            // Instantiate the generic function (generates code if not already done)
            string mangledName = InstantiateGenericFunction(functionName: funcIdentifier.Name,
                typeArguments: typeArgList);

            // Generate call to the instantiated function
            string args = string.Join(separator: ", ",
                values: node.Arguments.Select(selector: a => a.Accept(visitor: this)));

            return $"{mangledName}({args})";
        }

        // Default: output as generic method call syntax
        string obj = node.Object.Accept(visitor: this);
        string typeArgs = string.Join(separator: ", ",
            values: node.TypeArguments.Select(selector: t => t.Accept(visitor: this)));
        string defaultArgs = string.Join(separator: ", ",
            values: node.Arguments.Select(selector: a => a.Accept(visitor: this)));
        string bang = node.IsMemoryOperation
            ? "!"
            : "";
        return $"{obj}.{node.MethodName}<{typeArgs}>{bang}({defaultArgs})";
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

    public string VisitIntrinsicCallExpression(IntrinsicCallExpression node)
    {
        string typeArgs = node.TypeArguments.Count > 0
            ? $"<{string.Join(separator: ", ", values: node.TypeArguments)}>"
            : "";
        string args = string.Join(separator: ", ",
            values: node.Arguments.Select(selector: a => a.Accept(visitor: this)));
        return $"@intrinsic.{node.IntrinsicName}{typeArgs}({args})";
    }

    public string VisitNativeCallExpression(NativeCallExpression node)
    {
        string args = string.Join(separator: ", ",
            values: node.Arguments.Select(selector: a => a.Accept(visitor: this)));
        return $"@native.{node.FunctionName}({args})";
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


    public string VisitExternalDeclaration(ExternalDeclaration node)
    {
        var sb = new StringBuilder();
        sb.Append(value: "external routine ");
        sb.Append(value: node.Name);

        if (node.GenericParameters != null && node.GenericParameters.Count > 0)
        {
            sb.Append(value: '<');
            sb.Append(value: string.Join(separator: ", ", values: node.GenericParameters));
            sb.Append(value: '>');
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

    /// <summary>
    /// Generates C code for a viewing statement (scoped read-only access).
    /// For single-threaded code, this creates a const pointer alias.
    /// </summary>
    public string VisitViewingStatement(ViewingStatement node)
    {
        _output.AppendLine(handler: $"{GetIndent()}viewing {node.Source} as {node.Handle}");
        _output.AppendLine(handler: $"{GetIndent()}{{");
        _indentLevel++;

        // Generate source expression
        string sourceExpr = node.Source.Accept(visitor: this);

        // Create const pointer alias for read-only access
        _output.AppendLine(handler: $"{GetIndent()}const void* {node.Handle} = &({sourceExpr});");

        // Generate body
        node.Body.Accept(visitor: this);

        _indentLevel--;
        _output.AppendLine(handler: $"{GetIndent()}}} /* end viewing {node.Handle} */");

        return "";
    }

    /// <summary>
    /// Generates C code for a hijacking statement (scoped exclusive access).
    /// For single-threaded code, this creates a mutable pointer alias.
    /// </summary>
    public string VisitHijackingStatement(HijackingStatement node)
    {
        _output.AppendLine(
            handler: $"{GetIndent()}/* hijacking {node.Source} as {node.Handle} */");
        _output.AppendLine(handler: $"{GetIndent()}{{");
        _indentLevel++;

        // Generate source expression
        string sourceExpr = node.Source.Accept(visitor: this);

        // Create mutable pointer for exclusive access
        _output.AppendLine(handler: $"{GetIndent()}void* {node.Handle} = &({sourceExpr});");

        // Generate body
        node.Body.Accept(visitor: this);

        _indentLevel--;
        _output.AppendLine(handler: $"{GetIndent()}}} /* end hijacking {node.Handle} */");

        return "";
    }

    /// <summary>
    /// Generates C code for an inspecting statement (thread-safe read access).
    /// Acquires a read lock on the shared object.
    /// </summary>
    public string VisitObservingStatement(ObservingStatement node)
    {
        _output.AppendLine(
            handler: $"{GetIndent()}/* inspecting {node.Source} as {node.Handle} */");
        _output.AppendLine(handler: $"{GetIndent()}{{");
        _indentLevel++;

        // Generate source expression
        string sourceExpr = node.Source.Accept(visitor: this);

        // Acquire read lock and get inner pointer
        _output.AppendLine(
            handler:
            $"{GetIndent()}void* {node.Handle} = razorforge_rwlock_read_lock({sourceExpr});");

        // Generate body
        node.Body.Accept(visitor: this);

        // Release read lock
        _output.AppendLine(handler: $"{GetIndent()}razorforge_rwlock_read_unlock({sourceExpr});");

        _indentLevel--;
        _output.AppendLine(handler: $"{GetIndent()}}} /* end inspecting {node.Handle} */");

        return "";
    }

    /// <summary>
    /// Generates C code for a seizing statement (thread-safe exclusive access).
    /// Acquires an exclusive lock on the shared object.
    /// </summary>
    public string VisitSeizingStatement(SeizingStatement node)
    {
        _output.AppendLine(handler: $"{GetIndent()}/* seizing {node.Source} as {node.Handle} */");
        _output.AppendLine(handler: $"{GetIndent()}{{");
        _indentLevel++;

        // Generate source expression
        string sourceExpr = node.Source.Accept(visitor: this);

        // Acquire exclusive lock and get inner pointer
        _output.AppendLine(
            handler: $"{GetIndent()}void* {node.Handle} = razorforge_mutex_lock({sourceExpr});");

        // Generate body
        node.Body.Accept(visitor: this);

        // Release exclusive lock
        _output.AppendLine(handler: $"{GetIndent()}razorforge_mutex_unlock({sourceExpr});");

        _indentLevel--;
        _output.AppendLine(handler: $"{GetIndent()}}} /* end seizing {node.Handle} */");

        return "";
    }

    /// <summary>
    /// Generates code for a named argument expression (name: value).
    /// </summary>
    public string VisitNamedArgumentExpression(NamedArgumentExpression node)
    {
        string value = node.Value.Accept(visitor: this);
        return $"{node.Name}: {value}";
    }

    /// <summary>
    /// Generates code for a constructor expression (Type(field: value, ...)).
    /// </summary>
    public string VisitConstructorExpression(ConstructorExpression node)
    {
        string typeName = node.TypeName;
        if (node.TypeArguments != null && node.TypeArguments.Count > 0)
        {
            typeName += "<" + string.Join(separator: ", ",
                values: node.TypeArguments.Select(selector: t => t.Name)) + ">";
        }

        IEnumerable<string> fieldStrs = node.Fields.Select(selector: f =>
            $"{f.Name}: {f.Value.Accept(visitor: this)}");
        return $"{typeName}({string.Join(separator: ", ", values: fieldStrs)})";
    }
}
