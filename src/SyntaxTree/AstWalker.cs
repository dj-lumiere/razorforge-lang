using System;
using System.Collections.Generic;

namespace SyntaxTree;

/// <summary>
/// Depth-first AST traversal utility. Replaces reflection-based walkers — every concrete
/// Statement, Expression, Declaration, Pattern, and auxiliary node has an explicit case
/// in <see cref="EnumerateChildren"/>. Adding a new AST node type without updating
/// EnumerateChildren means children of that node are silently skipped: keep this file
/// in sync with the records in Statements.cs, Expressions.cs, Expressions.Async.cs,
/// and Declarations.cs.
/// </summary>
public static class AstWalker
{
    /// <summary>
    /// Pre-order DFS: invokes <paramref name="visit"/> on <paramref name="root"/>, then on
    /// every descendant. Null roots are ignored. Walking is unconditional; if a visitor
    /// needs to stop recursion into a subtree, it should track state externally.
    /// </summary>
    public static void Walk(object? root, Action<object> visit)
    {
        if (root == null) return;
        visit(obj: root);
        foreach (object child in EnumerateChildren(node: root))
        {
            Walk(root: child, visit: visit);
        }
    }

    /// <summary>
    /// Convenience overload that only invokes <paramref name="visit"/> on
    /// <see cref="Expression"/> nodes encountered during the walk.
    /// </summary>
    public static void WalkExpressions(object? root, Action<Expression> visit)
    {
        Walk(root: root,
            visit: n =>
            {
                if (n is Expression e) visit(obj: e);
            });
    }

    /// <summary>
    /// Yields the immediate AST children of <paramref name="node"/>. Children include any
    /// Statement, Expression, Declaration, Pattern, or auxiliary record (WhenClause,
    /// Parameter, DestructuringBinding, etc.) reachable from one of <paramref name="node"/>'s
    /// constructor parameters. Non-AST data (strings, enums, primitives, type-system info)
    /// is not yielded.
    /// </summary>
    public static IEnumerable<object> EnumerateChildren(object node)
    {
        // Node types are disjoint across these categories, so at most one helper handles a given
        // node. Chained in the original declaration order (Program → Statements → Expressions →
        // Patterns → Declarations → Auxiliary) so the yielded child order is preserved exactly.
        return EnumerateProgramChildren(node: node)
            ?? EnumerateStatementChildren(node: node)
            ?? EnumerateExpressionChildren(node: node)
            ?? EnumeratePatternChildren(node: node)
            ?? EnumerateDeclarationChildren(node: node)
            ?? EnumerateAuxiliaryChildren(node: node)
            ?? System.Linq.Enumerable.Empty<object>();
    }

    // -------- Program --------
    private static IEnumerable<object>? EnumerateProgramChildren(object node)
    {
        if (node is not Program p) return null;
        return ProgramChildren(p: p);
    }

    private static IEnumerable<object> ProgramChildren(Program p)
    {
        foreach (ISyntaxTreeNode d in p.Declarations) yield return d;
    }

    // -------- Statements --------
    private static IEnumerable<object>? EnumerateStatementChildren(object node)
    {
        switch (node)
        {
            case ExpressionStatement s:
                return new object[] { s.Expression };
            case DeclarationStatement s:
                return new object[] { s.Declaration };
            case AssignmentStatement s:
                return new object[] { s.Target, s.Value };
            case DestructuringStatement s:
                return new object[] { s.Pattern, s.Initializer };
            case ReturnStatement s:
                return s.Value != null ? new object[] { s.Value } : System.Linq.Enumerable.Empty<object>();
            case BecomesStatement s:
                return new object[] { s.Value };
            case ThrowStatement s:
                return new object[] { s.Error };
            case VariantReturnStatement s:
                return s.Value != null ? new object[] { s.Value } : System.Linq.Enumerable.Empty<object>();
            case DiscardStatement s:
                return new object[] { s.Expression };
            case IfStatement s:
                return IfStatementChildren(s: s);
            case WhileStatement s:
                return WhileStatementChildren(s: s);
            case LoopStatement s:
                return new object[] { s.Body };
            case EachStatement s:
                return EachStatementChildren(s: s);
            case BlockStatement s:
                return BlockStatementChildren(s: s);
            case WhenStatement s:
                return WhenStatementChildren(s: s);
            case DangerStatement s:
                return new object[] { s.Body };
            case UsingStatement s:
                return UsingStatementChildren(s: s);
            case AbsentStatement:
            case PassStatement:
            case BreakStatement:
            case ContinueStatement:
                return System.Linq.Enumerable.Empty<object>();
            default:
                return null;
        }
    }

    private static IEnumerable<object> IfStatementChildren(IfStatement s)
    {
        yield return s.Condition;
        yield return s.ThenStatement;
        if (s.ElseStatement != null) yield return s.ElseStatement;
    }

    private static IEnumerable<object> WhileStatementChildren(WhileStatement s)
    {
        yield return s.Condition;
        yield return s.Body;
        if (s.ElseBranch != null) yield return s.ElseBranch;
    }

    private static IEnumerable<object> EachStatementChildren(EachStatement s)
    {
        if (s.VariablePattern != null) yield return s.VariablePattern;
        yield return s.Iterable;
        yield return s.Body;
        if (s.ElseBranch != null) yield return s.ElseBranch;
    }

    private static IEnumerable<object> BlockStatementChildren(BlockStatement s)
    {
        foreach (Statement child in s.Statements) yield return child;
    }

    private static IEnumerable<object> WhenStatementChildren(WhenStatement s)
    {
        yield return s.Expression;
        foreach (WhenClause c in s.Clauses) yield return c;
    }

    private static IEnumerable<object> UsingStatementChildren(UsingStatement s)
    {
        yield return s.Resource;
        yield return s.Body;
        if (s.FallbackBody != null) yield return s.FallbackBody;
    }

    // -------- Expressions --------
    private static IEnumerable<object>? EnumerateExpressionChildren(object node)
    {
        switch (node)
        {
            case InsertedTextExpression e:
                return InsertedTextExpressionChildren(e: e);
            case ListLiteralExpression e:
                return ListLiteralExpressionChildren(e: e);
            case SetLiteralExpression e:
                return SetLiteralExpressionChildren(e: e);
            case DictLiteralExpression e:
                return DictLiteralExpressionChildren(e: e);
            case TupleLiteralExpression e:
                return TupleLiteralExpressionChildren(e: e);
            case CompoundAssignmentExpression e:
                return new object[] { e.Target, e.Value };
            case BinaryExpression e:
                return new object[] { e.Left, e.Right };
            case UnaryExpression e:
                return new object[] { e.Operand };
            case CallExpression e:
                return CallExpressionChildren(e: e);
            case NamedArgumentExpression e:
                return new object[] { e.Value };
            case DictEntryLiteralExpression e:
                return new object[] { e.Key, e.Value };
            case CreatorExpression e:
                return CreatorExpressionChildren(e: e);
            case WithExpression e:
                return WithExpressionChildren(e: e);
            case MemberExpression e:
                return new object[] { e.Object };
            case OptionalMemberExpression e:
                return new object[] { e.Object };
            case IndexExpression e:
                return new object[] { e.Object, e.Index };
            case ConditionalExpression e:
                return new object[] { e.Condition, e.TrueExpression, e.FalseExpression };
            case BlockExpression e:
                return new object[] { e.Value };
            case ChainedComparisonExpression e:
                return ChainedComparisonExpressionChildren(e: e);
            case RangeExpression e:
                return RangeExpressionChildren(e: e);
            case LambdaExpression e:
                return LambdaExpressionChildren(e: e);
            case TypeExpression e:
                return TypeExpressionChildren(e: e);
            case TypeConversionExpression e:
                return new object[] { e.Expression };
            case GenericMemberRoutineCallExpression e:
                return GenericMemberRoutineCallExpressionChildren(e: e);
            case GenericMemberExpression e:
                return GenericMemberExpressionChildren(e: e);
            case TypeIdExpression e:
                return new object[] { e.Type };
            case CarrierPayloadExpression e:
                return new object[] { e.Carrier, e.ConcreteType };
            case IsPatternExpression e:
                return new object[] { e.Expression, e.Pattern };
            case FlagsTestExpression e:
                return new object[] { e.Subject };
            case WhenExpression e:
                return WhenExpressionChildren(e: e);
            case StealExpression e:
                return new object[] { e.Operand };
            case WaitforExpression e:
                return WaitforExpressionChildren(e: e);
            case DependentWaitforExpression e:
                return DependentWaitforExpressionChildren(e: e);
            case BackIndexExpression e:
                return new object[] { e.Operand };
            case LiteralExpression:
            case IdentifierExpression:
                return System.Linq.Enumerable.Empty<object>();
            default:
                return null;
        }
    }

    private static IEnumerable<object> InsertedTextExpressionChildren(InsertedTextExpression e)
    {
        foreach (InsertedTextPart part in e.Parts) yield return part;
    }

    private static IEnumerable<object> ListLiteralExpressionChildren(ListLiteralExpression e)
    {
        foreach (Expression el in e.Elements) yield return el;
        if (e.ElementType != null) yield return e.ElementType;
    }

    private static IEnumerable<object> SetLiteralExpressionChildren(SetLiteralExpression e)
    {
        foreach (Expression el in e.Elements) yield return el;
        if (e.ElementType != null) yield return e.ElementType;
    }

    private static IEnumerable<object> DictLiteralExpressionChildren(DictLiteralExpression e)
    {
        foreach ((Expression Key, Expression Value) pair in e.Pairs)
        {
            yield return pair.Key;
            yield return pair.Value;
        }
        if (e.KeyType != null) yield return e.KeyType;
        if (e.ValueType != null) yield return e.ValueType;
    }

    private static IEnumerable<object> TupleLiteralExpressionChildren(TupleLiteralExpression e)
    {
        foreach (Expression el in e.Elements) yield return el;
    }

    private static IEnumerable<object> CallExpressionChildren(CallExpression e)
    {
        yield return e.Callee;
        foreach (Expression arg in e.Arguments) yield return arg;
        if (e.TypeArguments != null)
            foreach (TypeExpression t in e.TypeArguments) yield return t;
    }

    private static IEnumerable<object> CreatorExpressionChildren(CreatorExpression e)
    {
        if (e.TypeArguments != null)
            foreach (TypeExpression t in e.TypeArguments) yield return t;
        foreach ((string Name, Expression Value) mv in e.MemberVariables)
            yield return mv.Value;
    }

    private static IEnumerable<object> WithExpressionChildren(WithExpression e)
    {
        yield return e.Base;
        foreach ((List<string>? Path, Expression? Index, Expression Value) u in e.Updates)
        {
            if (u.Index != null) yield return u.Index;
            yield return u.Value;
        }
    }

    private static IEnumerable<object> ChainedComparisonExpressionChildren(ChainedComparisonExpression e)
    {
        foreach (Expression op in e.Operands) yield return op;
    }

    private static IEnumerable<object> RangeExpressionChildren(RangeExpression e)
    {
        yield return e.Start;
        yield return e.End;
        if (e.Step != null) yield return e.Step;
    }

    private static IEnumerable<object> LambdaExpressionChildren(LambdaExpression e)
    {
        foreach (Parameter p in e.Parameters) yield return p;
        yield return e.Body;
    }

    private static IEnumerable<object> TypeExpressionChildren(TypeExpression e)
    {
        if (e.GenericArguments != null)
            foreach (TypeExpression t in e.GenericArguments) yield return t;
    }

    private static IEnumerable<object> GenericMemberRoutineCallExpressionChildren(GenericMemberRoutineCallExpression e)
    {
        yield return e.Object;
        foreach (TypeExpression t in e.TypeArguments) yield return t;
        foreach (Expression arg in e.Arguments) yield return arg;
    }

    private static IEnumerable<object> GenericMemberExpressionChildren(GenericMemberExpression e)
    {
        yield return e.Object;
        foreach (TypeExpression t in e.TypeArguments) yield return t;
    }

    private static IEnumerable<object> WhenExpressionChildren(WhenExpression e)
    {
        if (e.Expression != null) yield return e.Expression;
        foreach (WhenClause c in e.Clauses) yield return c;
    }

    private static IEnumerable<object> WaitforExpressionChildren(WaitforExpression e)
    {
        yield return e.Operand;
        if (e.Timeout != null) yield return e.Timeout;
    }

    private static IEnumerable<object> DependentWaitforExpressionChildren(DependentWaitforExpression e)
    {
        foreach (TaskDependency dep in e.Dependencies) yield return dep;
        yield return e.Operand;
        if (e.Timeout != null) yield return e.Timeout;
    }

    // -------- Patterns --------
    private static IEnumerable<object>? EnumeratePatternChildren(object node)
    {
        switch (node)
        {
            case TypePattern p:
                return TypePatternChildren(p: p);
            case NegatedTypePattern p:
                return new object[] { p.Type };
            case ExpressionPattern p:
                return new object[] { p.Expression };
            case ComparisonPattern p:
                return new object[] { p.Value };
            case VariantPattern p:
                return VariantPatternChildren(p: p);
            case GuardPattern p:
                return new object[] { p.InnerPattern, p.Guard };
            case CrashablePattern p:
                return p.ErrorType != null ? new object[] { p.ErrorType } : System.Linq.Enumerable.Empty<object>();
            case DestructuringPattern p:
                return DestructuringPatternChildren(p: p);
            case TypeDestructuringPattern p:
                return TypeDestructuringPatternChildren(p: p);
            case LiteralPattern:
            case IdentifierPattern:
            case FlagsPattern:
            case WildcardPattern:
            case NonePattern:
            case ElsePattern:
                return System.Linq.Enumerable.Empty<object>();
            default:
                return null;
        }
    }

    private static IEnumerable<object> TypePatternChildren(TypePattern p)
    {
        yield return p.Type;
        if (p.Bindings != null)
            foreach (DestructuringBinding b in p.Bindings) yield return b;
    }

    private static IEnumerable<object> VariantPatternChildren(VariantPattern p)
    {
        if (p.Bindings != null)
            foreach (DestructuringBinding b in p.Bindings) yield return b;
    }

    private static IEnumerable<object> DestructuringPatternChildren(DestructuringPattern p)
    {
        foreach (DestructuringBinding b in p.Bindings) yield return b;
    }

    private static IEnumerable<object> TypeDestructuringPatternChildren(TypeDestructuringPattern p)
    {
        yield return p.Type;
        foreach (DestructuringBinding b in p.Bindings) yield return b;
    }

    // -------- Declarations --------
    private static IEnumerable<object>? EnumerateDeclarationChildren(object node)
    {
        switch (node)
        {
            case VariableDeclaration d:
                return VariableDeclarationChildren(d: d);
            case RoutineDeclaration d:
                return RoutineDeclarationChildren(d: d);
            case EntityDeclaration d:
                return EntityDeclarationChildren(d: d);
            case RecordDeclaration d:
                return RecordDeclarationChildren(d: d);
            case ChoiceDeclaration d:
                return ChoiceDeclarationChildren(d: d);
            case CrashableDeclaration d:
                return CrashableDeclarationChildren(d: d);
            case VariantDeclaration d:
                return VariantDeclarationChildren(d: d);
            case ProtocolDeclaration d:
                return ProtocolDeclarationChildren(d: d);
            case PresetDeclaration d:
                return new object[] { d.Type, d.Value };
            case ExternalDeclaration d:
                return ExternalDeclarationChildren(d: d);
            case ExternalBlockDeclaration d:
                return ExternalBlockDeclarationChildren(d: d);
            case PassDeclaration:
            case FlagsDeclaration:
            case ModuleDeclaration:
            case ImportDeclaration:
            case DefineDeclaration:
                return System.Linq.Enumerable.Empty<object>();
            default:
                return null;
        }
    }

    private static IEnumerable<object> VariableDeclarationChildren(VariableDeclaration d)
    {
        if (d.Type != null) yield return d.Type;
        if (d.Initializer != null) yield return d.Initializer;
    }

    private static IEnumerable<object> RoutineDeclarationChildren(RoutineDeclaration d)
    {
        foreach (Parameter p in d.Parameters) yield return p;
        if (d.ReturnType != null) yield return d.ReturnType;
        yield return d.Body;
    }

    private static IEnumerable<object> EntityDeclarationChildren(EntityDeclaration d)
    {
        foreach (TypeExpression t in d.Protocols) yield return t;
        foreach (Declaration m in d.Members) yield return m;
    }

    private static IEnumerable<object> RecordDeclarationChildren(RecordDeclaration d)
    {
        foreach (TypeExpression t in d.Protocols) yield return t;
        foreach (Declaration m in d.Members) yield return m;
    }

    private static IEnumerable<object> ChoiceDeclarationChildren(ChoiceDeclaration d)
    {
        foreach (ChoiceCase c in d.Cases) yield return c;
        foreach (RoutineDeclaration m in d.MemberRoutines) yield return m;
    }

    private static IEnumerable<object> CrashableDeclarationChildren(CrashableDeclaration d)
    {
        foreach (Declaration m in d.Members) yield return m;
    }

    private static IEnumerable<object> VariantDeclarationChildren(VariantDeclaration d)
    {
        foreach (VariantMember m in d.Members) yield return m;
    }

    private static IEnumerable<object> ProtocolDeclarationChildren(ProtocolDeclaration d)
    {
        foreach (TypeExpression t in d.ParentProtocols) yield return t;
        foreach (RoutineSignature m in d.MemberRoutines) yield return m;
    }

    private static IEnumerable<object> ExternalDeclarationChildren(ExternalDeclaration d)
    {
        foreach (Parameter p in d.Parameters) yield return p;
        if (d.ReturnType != null) yield return d.ReturnType;
    }

    private static IEnumerable<object> ExternalBlockDeclarationChildren(ExternalBlockDeclaration d)
    {
        foreach (Declaration child in d.Declarations) yield return child;
    }

    // -------- Auxiliary records --------
    private static IEnumerable<object>? EnumerateAuxiliaryChildren(object node)
    {
        switch (node)
        {
            case WhenClause c:
                return new object[] { c.Pattern, c.Body };
            case DestructuringBinding b:
                return b.NestedPattern != null ? new object[] { b.NestedPattern } : System.Linq.Enumerable.Empty<object>();
            case Parameter p:
                return ParameterChildren(p: p);
            case ChoiceCase c:
                return c.Value != null ? new object[] { c.Value } : System.Linq.Enumerable.Empty<object>();
            case VariantMember m:
                return new object[] { m.Type };
            case RoutineSignature r:
                return RoutineSignatureChildren(r: r);
            case TaskDependency d:
                return new object[] { d.DependencyExpr };
            case ExpressionPart ep:
                return new object[] { ep.Expression };
            case TextPart:
                return System.Linq.Enumerable.Empty<object>();
            default:
                return null;
        }
    }

    private static IEnumerable<object> ParameterChildren(Parameter p)
    {
        if (p.Type != null) yield return p.Type;
        if (p.DefaultValue != null) yield return p.DefaultValue;
    }

    private static IEnumerable<object> RoutineSignatureChildren(RoutineSignature r)
    {
        foreach (Parameter p in r.Parameters) yield return p;
        if (r.ReturnType != null) yield return r.ReturnType;
    }
}
