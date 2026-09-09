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
        if (root == null)
        {
            return;
        }

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
                if (n is Expression e)
                {
                    visit(obj: e);
                }
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
        return EnumerateProgramChildren(node: node) ?? EnumerateStatementChildren(node: node) ??
            EnumerateExpressionChildren(node: node) ?? EnumeratePatternChildren(node: node) ??
            EnumerateDeclarationChildren(node: node) ?? EnumerateAuxiliaryChildren(node: node) ??
            Enumerable.Empty<object>();
    }

    // -------- Program --------
    private static IEnumerable<object>? EnumerateProgramChildren(object node)
    {
        if (node is not Program p)
        {
            return null;
        }

        return ProgramChildren(p: p);
    }

    private static IEnumerable<object> ProgramChildren(Program p)
    {
        foreach (ISyntaxTreeNode d in p.Declarations)
        {
            yield return d;
        }
    }

    // -------- Statements --------
    private static IEnumerable<object>? EnumerateStatementChildren(object node)
    {
        return node switch
        {
            ExpressionStatement s => new object[]
            {
                s.Expression
            },
            DeclarationStatement s => new object[]
            {
                s.Declaration
            },
            AssignmentStatement s => new object[]
            {
                s.Target,
                s.Value
            },
            DestructuringStatement s => new object[]
            {
                s.Pattern,
                s.Initializer
            },
            ReturnStatement s => s.Value != null
                ? new object[]
                {
                    s.Value
                }
                : Enumerable.Empty<object>(),
            BecomesStatement s => new object[]
            {
                s.Value
            },
            ThrowStatement s => new object[]
            {
                s.Error
            },
            VariantReturnStatement s => s.Value != null
                ? new object[]
                {
                    s.Value
                }
                : Enumerable.Empty<object>(),
            DiscardStatement s => new object[]
            {
                s.Expression
            },
            IfStatement s => IfStatementChildren(s: s),
            WhileStatement s => WhileStatementChildren(s: s),
            LoopStatement s => new object[]
            {
                s.Body
            },
            EachStatement s => EachStatementChildren(s: s),
            BlockStatement s => BlockStatementChildren(s: s),
            WhenStatement s => WhenStatementChildren(s: s),
            DangerStatement s => new object[]
            {
                s.Body
            },
            UsingStatement s => UsingStatementChildren(s: s),
            AbsentStatement or PassStatement or BreakStatement or ContinueStatement => Enumerable
               .Empty<object>(),
            _ => null
        };
    }

    private static IEnumerable<object> IfStatementChildren(IfStatement s)
    {
        yield return s.Condition;
        yield return s.ThenStatement;
        if (s.ElseStatement != null)
        {
            yield return s.ElseStatement;
        }
    }

    private static IEnumerable<object> WhileStatementChildren(WhileStatement s)
    {
        yield return s.Condition;
        yield return s.Body;
        if (s.ElseBranch != null)
        {
            yield return s.ElseBranch;
        }
    }

    private static IEnumerable<object> EachStatementChildren(EachStatement s)
    {
        if (s.VariablePattern != null)
        {
            yield return s.VariablePattern;
        }

        yield return s.Iterable;
        yield return s.Body;
        if (s.ElseBranch != null)
        {
            yield return s.ElseBranch;
        }
    }

    private static IEnumerable<object> BlockStatementChildren(BlockStatement s)
    {
        foreach (Statement child in s.Statements)
        {
            yield return child;
        }
    }

    private static IEnumerable<object> WhenStatementChildren(WhenStatement s)
    {
        yield return s.Expression;
        foreach (WhenClause c in s.Clauses)
        {
            yield return c;
        }
    }

    private static IEnumerable<object> UsingStatementChildren(UsingStatement s)
    {
        yield return s.Resource;
        yield return s.Body;
        if (s.FallbackBody != null)
        {
            yield return s.FallbackBody;
        }
    }

    // -------- Expressions --------
    private static IEnumerable<object>? EnumerateExpressionChildren(object node)
    {
        return node switch
        {
            InsertedTextExpression e => InsertedTextExpressionChildren(e: e),
            ListLiteralExpression e => ListLiteralExpressionChildren(e: e),
            SetLiteralExpression e => SetLiteralExpressionChildren(e: e),
            DictLiteralExpression e => DictLiteralExpressionChildren(e: e),
            TupleLiteralExpression e => TupleLiteralExpressionChildren(e: e),
            CompoundAssignmentExpression e => new object[]
            {
                e.Target,
                e.Value
            },
            BinaryExpression e => new object[]
            {
                e.Left,
                e.Right
            },
            UnaryExpression e => new object[]
            {
                e.Operand
            },
            CallExpression e => CallExpressionChildren(e: e),
            NamedArgumentExpression e => new object[]
            {
                e.Value
            },
            DictEntryLiteralExpression e => new object[]
            {
                e.Key,
                e.Value
            },
            CreatorExpression e => CreatorExpressionChildren(e: e),
            WithExpression e => WithExpressionChildren(e: e),
            MemberExpression e => new object[]
            {
                e.Object
            },
            OptionalMemberExpression e => new object[]
            {
                e.Object
            },
            IndexExpression e => new object[]
            {
                e.Object,
                e.Index
            },
            ConditionalExpression e => new object[]
            {
                e.Condition,
                e.TrueExpression,
                e.FalseExpression
            },
            BlockExpression e => new object[]
            {
                e.Value
            },
            ChainedComparisonExpression e => ChainedComparisonExpressionChildren(e: e),
            RangeExpression e => RangeExpressionChildren(e: e),
            LambdaExpression e => LambdaExpressionChildren(e: e),
            TypeExpression e => TypeExpressionChildren(e: e),
            TypeConversionExpression e => new object[]
            {
                e.Expression
            },
            GenericMemberRoutineCallExpression e =>
                GenericMemberRoutineCallExpressionChildren(e: e),
            GenericMemberExpression e => GenericMemberExpressionChildren(e: e),
            TypeIdExpression e => new object[]
            {
                e.Type
            },
            CarrierPayloadExpression e => new object[]
            {
                e.Carrier,
                e.ConcreteType
            },
            CrashableDispatchExpression e => new object[]
            {
                e.Carrier
            },
            IsPatternExpression e => new object[]
            {
                e.Expression,
                e.Pattern
            },
            FlagsTestExpression e => new object[]
            {
                e.Subject
            },
            WhenExpression e => WhenExpressionChildren(e: e),
            StealExpression e => new object[]
            {
                e.Operand
            },
            WaitforExpression e => WaitforExpressionChildren(e: e),
            DependentWaitforExpression e => DependentWaitforExpressionChildren(e: e),
            BackIndexExpression e => new object[]
            {
                e.Operand
            },
            LiteralExpression or IdentifierExpression => Enumerable.Empty<object>(),
            _ => null
        };
    }

    private static IEnumerable<object> InsertedTextExpressionChildren(InsertedTextExpression e)
    {
        foreach (InsertedTextPart part in e.Parts)
        {
            yield return part;
        }
    }

    private static IEnumerable<object> ListLiteralExpressionChildren(ListLiteralExpression e)
    {
        foreach (Expression el in e.Elements)
        {
            yield return el;
        }

        if (e.ElementType != null)
        {
            yield return e.ElementType;
        }
    }

    private static IEnumerable<object> SetLiteralExpressionChildren(SetLiteralExpression e)
    {
        foreach (Expression el in e.Elements)
        {
            yield return el;
        }

        if (e.ElementType != null)
        {
            yield return e.ElementType;
        }
    }

    private static IEnumerable<object> DictLiteralExpressionChildren(DictLiteralExpression e)
    {
        foreach ((Expression Key, Expression Value) pair in e.Pairs)
        {
            yield return pair.Key;
            yield return pair.Value;
        }

        if (e.KeyType != null)
        {
            yield return e.KeyType;
        }

        if (e.ValueType != null)
        {
            yield return e.ValueType;
        }
    }

    private static IEnumerable<object> TupleLiteralExpressionChildren(TupleLiteralExpression e)
    {
        foreach (Expression el in e.Elements)
        {
            yield return el;
        }
    }

    private static IEnumerable<object> CallExpressionChildren(CallExpression e)
    {
        yield return e.Callee;
        foreach (Expression arg in e.Arguments)
        {
            yield return arg;
        }

        if (e.TypeArguments != null)
        {
            foreach (TypeExpression t in e.TypeArguments)
            {
                yield return t;
            }
        }
    }

    private static IEnumerable<object> CreatorExpressionChildren(CreatorExpression e)
    {
        if (e.TypeArguments != null)
        {
            foreach (TypeExpression t in e.TypeArguments)
            {
                yield return t;
            }
        }

        foreach ((string Name, Expression Value) mv in e.MemberVariables)
        {
            yield return mv.Value;
        }
    }

    private static IEnumerable<object> WithExpressionChildren(WithExpression e)
    {
        yield return e.Base;
        foreach ((List<string>? Path, Expression? Index, Expression Value) u in e.Updates)
        {
            if (u.Index != null)
            {
                yield return u.Index;
            }

            yield return u.Value;
        }
    }

    private static IEnumerable<object> ChainedComparisonExpressionChildren(
        ChainedComparisonExpression e)
    {
        foreach (Expression op in e.Operands)
        {
            yield return op;
        }
    }

    private static IEnumerable<object> RangeExpressionChildren(RangeExpression e)
    {
        yield return e.Start;
        yield return e.End;
        if (e.Step != null)
        {
            yield return e.Step;
        }
    }

    private static IEnumerable<object> LambdaExpressionChildren(LambdaExpression e)
    {
        foreach (Parameter p in e.Parameters)
        {
            yield return p;
        }

        yield return e.Body;
    }

    private static IEnumerable<object> TypeExpressionChildren(TypeExpression e)
    {
        if (e.GenericArguments != null)
        {
            foreach (TypeExpression t in e.GenericArguments)
            {
                yield return t;
            }
        }
    }

    private static IEnumerable<object> GenericMemberRoutineCallExpressionChildren(
        GenericMemberRoutineCallExpression e)
    {
        yield return e.Object;
        foreach (TypeExpression t in e.TypeArguments)
        {
            yield return t;
        }

        foreach (Expression arg in e.Arguments)
        {
            yield return arg;
        }
    }

    private static IEnumerable<object> GenericMemberExpressionChildren(GenericMemberExpression e)
    {
        yield return e.Object;
        foreach (TypeExpression t in e.TypeArguments)
        {
            yield return t;
        }
    }

    private static IEnumerable<object> WhenExpressionChildren(WhenExpression e)
    {
        if (e.Expression != null)
        {
            yield return e.Expression;
        }

        foreach (WhenClause c in e.Clauses)
        {
            yield return c;
        }
    }

    private static IEnumerable<object> WaitforExpressionChildren(WaitforExpression e)
    {
        yield return e.Operand;
        if (e.Timeout != null)
        {
            yield return e.Timeout;
        }
    }

    private static IEnumerable<object> DependentWaitforExpressionChildren(
        DependentWaitforExpression e)
    {
        foreach (TaskDependency dep in e.Dependencies)
        {
            yield return dep;
        }

        yield return e.Operand;
        if (e.Timeout != null)
        {
            yield return e.Timeout;
        }
    }

    // -------- Patterns --------
    private static IEnumerable<object>? EnumeratePatternChildren(object node)
    {
        return node switch
        {
            TypePattern p => TypePatternChildren(p: p),
            NegatedTypePattern p => new object[]
            {
                p.Type
            },
            ExpressionPattern p => new object[]
            {
                p.Expression
            },
            ComparisonPattern p => new object[]
            {
                p.Value
            },
            VariantPattern p => VariantPatternChildren(p: p),
            GuardPattern p => new object[]
            {
                p.InnerPattern,
                p.Guard
            },
            CrashablePattern p => p.ErrorType != null
                ? new object[]
                {
                    p.ErrorType
                }
                : Enumerable.Empty<object>(),
            DestructuringPattern p => DestructuringPatternChildren(p: p),
            TypeDestructuringPattern p => TypeDestructuringPatternChildren(p: p),
            LiteralPattern or IdentifierPattern or FlagsPattern or WildcardPattern or NonePattern
                or ElsePattern => Enumerable.Empty<object>(),
            _ => null
        };
    }

    private static IEnumerable<object> TypePatternChildren(TypePattern p)
    {
        yield return p.Type;
        if (p.Bindings != null)
        {
            foreach (DestructuringBinding b in p.Bindings)
            {
                yield return b;
            }
        }
    }

    private static IEnumerable<object> VariantPatternChildren(VariantPattern p)
    {
        if (p.Bindings != null)
        {
            foreach (DestructuringBinding b in p.Bindings)
            {
                yield return b;
            }
        }
    }

    private static IEnumerable<object> DestructuringPatternChildren(DestructuringPattern p)
    {
        foreach (DestructuringBinding b in p.Bindings)
        {
            yield return b;
        }
    }

    private static IEnumerable<object> TypeDestructuringPatternChildren(TypeDestructuringPattern p)
    {
        yield return p.Type;
        foreach (DestructuringBinding b in p.Bindings)
        {
            yield return b;
        }
    }

    // -------- Declarations --------
    private static IEnumerable<object>? EnumerateDeclarationChildren(object node)
    {
        return node switch
        {
            VariableDeclaration d => VariableDeclarationChildren(d: d),
            RoutineDeclaration d => RoutineDeclarationChildren(d: d),
            EntityDeclaration d => EntityDeclarationChildren(d: d),
            RecordDeclaration d => RecordDeclarationChildren(d: d),
            ChoiceDeclaration d => ChoiceDeclarationChildren(d: d),
            CrashableDeclaration d => CrashableDeclarationChildren(d: d),
            VariantDeclaration d => VariantDeclarationChildren(d: d),
            ProtocolDeclaration d => ProtocolDeclarationChildren(d: d),
            PresetDeclaration d => new object[]
            {
                d.Type,
                d.Value
            },
            ExternalDeclaration d => ExternalDeclarationChildren(d: d),
            ExternalBlockDeclaration d => ExternalBlockDeclarationChildren(d: d),
            PassDeclaration or FlagsDeclaration or ModuleDeclaration or ImportDeclaration
                or DefineDeclaration => Enumerable.Empty<object>(),
            _ => null
        };
    }

    private static IEnumerable<object> VariableDeclarationChildren(VariableDeclaration d)
    {
        if (d.Type != null)
        {
            yield return d.Type;
        }

        if (d.Initializer != null)
        {
            yield return d.Initializer;
        }
    }

    private static IEnumerable<object> RoutineDeclarationChildren(RoutineDeclaration d)
    {
        foreach (Parameter p in d.Parameters)
        {
            yield return p;
        }

        if (d.ReturnType != null)
        {
            yield return d.ReturnType;
        }

        yield return d.Body;
    }

    private static IEnumerable<object> EntityDeclarationChildren(EntityDeclaration d)
    {
        foreach (TypeExpression t in d.Protocols)
        {
            yield return t;
        }

        foreach (Declaration m in d.Members)
        {
            yield return m;
        }
    }

    private static IEnumerable<object> RecordDeclarationChildren(RecordDeclaration d)
    {
        foreach (TypeExpression t in d.Protocols)
        {
            yield return t;
        }

        foreach (Declaration m in d.Members)
        {
            yield return m;
        }
    }

    private static IEnumerable<object> ChoiceDeclarationChildren(ChoiceDeclaration d)
    {
        foreach (ChoiceCase c in d.Cases)
        {
            yield return c;
        }

        foreach (RoutineDeclaration m in d.MemberRoutines)
        {
            yield return m;
        }
    }

    private static IEnumerable<object> CrashableDeclarationChildren(CrashableDeclaration d)
    {
        foreach (Declaration m in d.Members)
        {
            yield return m;
        }
    }

    private static IEnumerable<object> VariantDeclarationChildren(VariantDeclaration d)
    {
        foreach (VariantMember m in d.Members)
        {
            yield return m;
        }
    }

    private static IEnumerable<object> ProtocolDeclarationChildren(ProtocolDeclaration d)
    {
        foreach (TypeExpression t in d.ParentProtocols)
        {
            yield return t;
        }

        foreach (RoutineSignature m in d.MemberRoutines)
        {
            yield return m;
        }
    }

    private static IEnumerable<object> ExternalDeclarationChildren(ExternalDeclaration d)
    {
        foreach (Parameter p in d.Parameters)
        {
            yield return p;
        }

        if (d.ReturnType != null)
        {
            yield return d.ReturnType;
        }
    }

    private static IEnumerable<object> ExternalBlockDeclarationChildren(ExternalBlockDeclaration d)
    {
        foreach (Declaration child in d.Declarations)
        {
            yield return child;
        }
    }

    // -------- Auxiliary records --------
    private static IEnumerable<object>? EnumerateAuxiliaryChildren(object node)
    {
        return node switch
        {
            WhenClause c => new object[]
            {
                c.Pattern,
                c.Body
            },
            DestructuringBinding b => b.NestedPattern != null
                ? new object[]
                {
                    b.NestedPattern
                }
                : Enumerable.Empty<object>(),
            Parameter p => ParameterChildren(p: p),
            ChoiceCase c => c.Value != null
                ? new object[]
                {
                    c.Value
                }
                : Enumerable.Empty<object>(),
            VariantMember m => new object[]
            {
                m.Type
            },
            RoutineSignature r => RoutineSignatureChildren(r: r),
            TaskDependency d => new object[]
            {
                d.DependencyExpr
            },
            ExpressionPart ep => new object[]
            {
                ep.Expression
            },
            TextPart => Enumerable.Empty<object>(),
            _ => null
        };
    }

    private static IEnumerable<object> ParameterChildren(Parameter p)
    {
        if (p.Type != null)
        {
            yield return p.Type;
        }

        if (p.DefaultValue != null)
        {
            yield return p.DefaultValue;
        }
    }

    private static IEnumerable<object> RoutineSignatureChildren(RoutineSignature r)
    {
        foreach (Parameter p in r.Parameters)
        {
            yield return p;
        }

        if (r.ReturnType != null)
        {
            yield return r.ReturnType;
        }
    }
}
