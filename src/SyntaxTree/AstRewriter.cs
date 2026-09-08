namespace SyntaxTree;

/// <summary>
/// Reconstructing depth-first AST transformer — the write-side counterpart to <see cref="AstWalker"/>.
/// A lowering/desugaring pass derives from this and OVERRIDES only the <c>Visit*</c> hooks for the node
/// kinds it rewrites; the base supplies the structural recursion (visit each child, rebuild the parent via
/// a <c>with</c> expression only when a child actually changed) that every pass would otherwise re-hand-roll.
///
/// <para>Semantics: pre-order dispatch, post-order rebuild. <see cref="VisitStatement"/> /
/// <see cref="VisitExpression"/> dispatch on the concrete node type to the specific hook; each specific hook
/// defaults to rewriting the node's children. A hook that returns the SAME reference signals "unchanged", so
/// parents avoid allocating a new record. Reference identity is preserved for untouched subtrees, which lets
/// callers cheaply detect "did anything change" via <c>ReferenceEquals</c>.</para>
///
/// <para>Only the Statement/Expression spine is rewritten (the shape lowering passes touch). Declarations,
/// patterns, types, and auxiliary records are returned unchanged by default; a pass that needs them can
/// override the relevant hook. Keep the node coverage in sync with <see cref="AstWalker.EnumerateChildren"/>.</para>
/// </summary>
public abstract class AstRewriter
{
    // ---------------- Statements ----------------

    public virtual Statement VisitStatement(Statement stmt)
    {
        return stmt switch
        {
            BlockStatement s => VisitBlock(s),
            IfStatement s => VisitIf(s),
            WhileStatement s => VisitWhile(s),
            LoopStatement s => VisitLoop(s),
            EachStatement s => VisitEach(s),
            WhenStatement s => VisitWhen(s),
            DangerStatement s => VisitDanger(s),
            UsingStatement s => VisitUsing(s),
            ReturnStatement s => VisitReturn(s),
            BecomesStatement s => VisitBecomes(s),
            ThrowStatement s => VisitThrow(s),
            VariantReturnStatement s => VisitVariantReturn(s),
            DiscardStatement s => VisitDiscard(s),
            ExpressionStatement s => VisitExpressionStatement(s),
            AssignmentStatement s => VisitAssignment(s),
            DeclarationStatement s => VisitDeclarationStatement(s),
            _ => stmt // AbsentStatement / PassStatement / Break / Continue / others: leaf, unchanged.
        };
    }

    protected virtual Statement VisitBlock(BlockStatement s)
    {
        List<Statement> rewritten = RewriteList(s.Statements, VisitStatement);
        return ReferenceEquals(rewritten, s.Statements) ? s : s with { Statements = rewritten };
    }

    protected virtual Statement VisitIf(IfStatement s)
    {
        Expression cond = VisitExpression(s.Condition);
        Statement then = VisitStatement(s.ThenStatement);
        Statement? els = s.ElseStatement != null ? VisitStatement(s.ElseStatement) : null;
        return ReferenceEquals(cond, s.Condition) && ReferenceEquals(then, s.ThenStatement)
            && ReferenceEquals(els, s.ElseStatement)
            ? s
            : s with { Condition = cond, ThenStatement = then, ElseStatement = els };
    }

    protected virtual Statement VisitWhile(WhileStatement s)
    {
        Expression cond = VisitExpression(s.Condition);
        Statement body = VisitStatement(s.Body);
        Statement? els = s.ElseBranch != null ? VisitStatement(s.ElseBranch) : null;
        return ReferenceEquals(cond, s.Condition) && ReferenceEquals(body, s.Body)
            && ReferenceEquals(els, s.ElseBranch)
            ? s
            : s with { Condition = cond, Body = body, ElseBranch = els };
    }

    protected virtual Statement VisitLoop(LoopStatement s)
    {
        Statement body = VisitStatement(s.Body);
        return ReferenceEquals(body, s.Body) ? s : s with { Body = body };
    }

    protected virtual Statement VisitEach(EachStatement s)
    {
        Expression iterable = VisitExpression(s.Iterable);
        Statement body = VisitStatement(s.Body);
        Statement? els = s.ElseBranch != null ? VisitStatement(s.ElseBranch) : null;
        return ReferenceEquals(iterable, s.Iterable) && ReferenceEquals(body, s.Body)
            && ReferenceEquals(els, s.ElseBranch)
            ? s
            : s with { Iterable = iterable, Body = body, ElseBranch = els };
    }

    protected virtual Statement VisitWhen(WhenStatement s)
    {
        Expression subject = VisitExpression(s.Expression);
        List<WhenClause> clauses = RewriteList(s.Clauses,
            c =>
            {
                Statement body = VisitStatement(c.Body);
                return ReferenceEquals(body, c.Body) ? c : c with { Body = body };
            });
        return ReferenceEquals(subject, s.Expression) && ReferenceEquals(clauses, s.Clauses)
            ? s
            : s with { Expression = subject, Clauses = clauses };
    }

    protected virtual Statement VisitDanger(DangerStatement s)
    {
        Statement body = VisitStatement(s.Body);
        return ReferenceEquals(body, s.Body) ? s : s with { Body = (BlockStatement)body };
    }

    protected virtual Statement VisitUsing(UsingStatement s)
    {
        Expression resource = VisitExpression(s.Resource);
        Statement body = VisitStatement(s.Body);
        Statement? fallback = s.FallbackBody != null ? VisitStatement(s.FallbackBody) : null;
        return ReferenceEquals(resource, s.Resource) && ReferenceEquals(body, s.Body)
            && ReferenceEquals(fallback, s.FallbackBody)
            ? s
            : s with { Resource = resource, Body = body, FallbackBody = fallback };
    }

    protected virtual Statement VisitReturn(ReturnStatement s)
    {
        if (s.Value == null) return s;
        Expression v = VisitExpression(s.Value);
        return ReferenceEquals(v, s.Value) ? s : s with { Value = v };
    }

    protected virtual Statement VisitBecomes(BecomesStatement s)
    {
        Expression v = VisitExpression(s.Value);
        return ReferenceEquals(v, s.Value) ? s : s with { Value = v };
    }

    protected virtual Statement VisitThrow(ThrowStatement s)
    {
        Expression e = VisitExpression(s.Error);
        return ReferenceEquals(e, s.Error) ? s : s with { Error = e };
    }

    protected virtual Statement VisitVariantReturn(VariantReturnStatement s)
    {
        if (s.Value == null) return s;
        Expression v = VisitExpression(s.Value);
        return ReferenceEquals(v, s.Value) ? s : s with { Value = v };
    }

    protected virtual Statement VisitDiscard(DiscardStatement s)
    {
        Expression e = VisitExpression(s.Expression);
        return ReferenceEquals(e, s.Expression) ? s : s with { Expression = e };
    }

    protected virtual Statement VisitExpressionStatement(ExpressionStatement s)
    {
        Expression e = VisitExpression(s.Expression);
        return ReferenceEquals(e, s.Expression) ? s : s with { Expression = e };
    }

    protected virtual Statement VisitAssignment(AssignmentStatement s)
    {
        Expression target = VisitExpression(s.Target);
        Expression value = VisitExpression(s.Value);
        return ReferenceEquals(target, s.Target) && ReferenceEquals(value, s.Value)
            ? s
            : s with { Target = target, Value = value };
    }

    protected virtual Statement VisitDeclarationStatement(DeclarationStatement s)
    {
        if (s.Declaration is not VariableDeclaration { Initializer: { } init } vd) return s;
        Expression e = VisitExpression(init);
        return ReferenceEquals(e, init) ? s : s with { Declaration = vd with { Initializer = e } };
    }

    // ---------------- Expressions ----------------

    public virtual Expression VisitExpression(Expression expr)
    {
        return expr switch
        {
            BinaryExpression e => VisitBinary(e),
            UnaryExpression e => VisitUnary(e),
            CompoundAssignmentExpression e => VisitCompoundAssignment(e),
            CallExpression e => VisitCall(e),
            NamedArgumentExpression e => VisitNamedArgument(e),
            MemberExpression e => VisitMember(e),
            OptionalMemberExpression e => VisitOptionalMember(e),
            IndexExpression e => VisitIndex(e),
            ConditionalExpression e => VisitConditional(e),
            BlockExpression e => VisitBlockExpression(e),
            CreatorExpression e => VisitCreator(e),
            TypeConversionExpression e => VisitTypeConversion(e),
            StealExpression e => VisitSteal(e),
            BackIndexExpression e => VisitBackIndex(e),
            RangeExpression e => VisitRange(e),
            ChainedComparisonExpression e => VisitChainedComparison(e),
            TupleLiteralExpression e => VisitTupleLiteral(e),
            ListLiteralExpression e => VisitListLiteral(e),
            SetLiteralExpression e => VisitSetLiteral(e),
            DictLiteralExpression e => VisitDictLiteral(e),
            InsertedTextExpression e => VisitInsertedText(e),
            IsPatternExpression e => VisitIsPattern(e),
            FlagsTestExpression e => VisitFlagsTest(e),
            GenericMemberRoutineCallExpression e => VisitGenericMemberRoutineCall(e),
            GenericMemberExpression e => VisitGenericMember(e),
            _ => expr // LiteralExpression / IdentifierExpression / others: leaf, unchanged.
        };
    }

    protected virtual Expression VisitBinary(BinaryExpression e)
    {
        Expression l = VisitExpression(e.Left);
        Expression r = VisitExpression(e.Right);
        return ReferenceEquals(l, e.Left) && ReferenceEquals(r, e.Right) ? e : e with { Left = l, Right = r };
    }

    protected virtual Expression VisitUnary(UnaryExpression e)
    {
        Expression o = VisitExpression(e.Operand);
        return ReferenceEquals(o, e.Operand) ? e : e with { Operand = o };
    }

    protected virtual Expression VisitCompoundAssignment(CompoundAssignmentExpression e)
    {
        Expression t = VisitExpression(e.Target);
        Expression v = VisitExpression(e.Value);
        return ReferenceEquals(t, e.Target) && ReferenceEquals(v, e.Value) ? e : e with { Target = t, Value = v };
    }

    protected virtual Expression VisitCall(CallExpression e)
    {
        Expression callee = VisitExpression(e.Callee);
        List<Expression> args = RewriteList(e.Arguments, VisitExpression);
        return ReferenceEquals(callee, e.Callee) && ReferenceEquals(args, e.Arguments)
            ? e
            : e with { Callee = callee, Arguments = args };
    }

    protected virtual Expression VisitNamedArgument(NamedArgumentExpression e)
    {
        Expression v = VisitExpression(e.Value);
        return ReferenceEquals(v, e.Value) ? e : e with { Value = v };
    }

    protected virtual Expression VisitMember(MemberExpression e)
    {
        Expression o = VisitExpression(e.Object);
        return ReferenceEquals(o, e.Object) ? e : e with { Object = o };
    }

    protected virtual Expression VisitOptionalMember(OptionalMemberExpression e)
    {
        Expression o = VisitExpression(e.Object);
        return ReferenceEquals(o, e.Object) ? e : e with { Object = o };
    }

    protected virtual Expression VisitIndex(IndexExpression e)
    {
        Expression o = VisitExpression(e.Object);
        Expression i = VisitExpression(e.Index);
        if (ReferenceEquals(o, e.Object) && ReferenceEquals(i, e.Index)) return e;
        // IndexExpression carries a resolved setitem routine alongside its type; preserve both on the
        // rebuilt node (the convention every hand-rolled index rewrite in the lowering passes follows).
        IndexExpression rewritten = e with { Object = o, Index = i };
        rewritten.ResolvedType = e.ResolvedType;
        rewritten.ResolvedSetItem = e.ResolvedSetItem;
        return rewritten;
    }

    protected virtual Expression VisitConditional(ConditionalExpression e)
    {
        Expression c = VisitExpression(e.Condition);
        Expression t = VisitExpression(e.TrueExpression);
        Expression f = VisitExpression(e.FalseExpression);
        return ReferenceEquals(c, e.Condition) && ReferenceEquals(t, e.TrueExpression) && ReferenceEquals(f, e.FalseExpression)
            ? e
            : e with { Condition = c, TrueExpression = t, FalseExpression = f };
    }

    protected virtual Expression VisitBlockExpression(BlockExpression e)
    {
        Expression v = VisitExpression(e.Value);
        return ReferenceEquals(v, e.Value) ? e : e with { Value = v };
    }

    protected virtual Expression VisitCreator(CreatorExpression e)
    {
        List<(string Name, Expression Value)> mvs = RewriteList(e.MemberVariables,
            mv =>
            {
                Expression v = VisitExpression(mv.Value);
                return ReferenceEquals(v, mv.Value) ? mv : (mv.Name, v);
            });
        return ReferenceEquals(mvs, e.MemberVariables) ? e : e with { MemberVariables = mvs };
    }

    protected virtual Expression VisitTypeConversion(TypeConversionExpression e)
    {
        Expression v = VisitExpression(e.Expression);
        return ReferenceEquals(v, e.Expression) ? e : e with { Expression = v };
    }

    protected virtual Expression VisitSteal(StealExpression e)
    {
        Expression o = VisitExpression(e.Operand);
        return ReferenceEquals(o, e.Operand) ? e : e with { Operand = o };
    }

    protected virtual Expression VisitBackIndex(BackIndexExpression e)
    {
        Expression o = VisitExpression(e.Operand);
        return ReferenceEquals(o, e.Operand) ? e : e with { Operand = o };
    }

    protected virtual Expression VisitRange(RangeExpression e)
    {
        Expression start = VisitExpression(e.Start);
        Expression end = VisitExpression(e.End);
        Expression? step = e.Step != null ? VisitExpression(e.Step) : null;
        return ReferenceEquals(start, e.Start) && ReferenceEquals(end, e.End) && ReferenceEquals(step, e.Step)
            ? e
            : e with { Start = start, End = end, Step = step };
    }

    protected virtual Expression VisitChainedComparison(ChainedComparisonExpression e)
    {
        List<Expression> ops = RewriteList(e.Operands, VisitExpression);
        return ReferenceEquals(ops, e.Operands) ? e : e with { Operands = ops };
    }

    protected virtual Expression VisitTupleLiteral(TupleLiteralExpression e)
    {
        List<Expression> els = RewriteList(e.Elements, VisitExpression);
        return ReferenceEquals(els, e.Elements) ? e : e with { Elements = els };
    }

    protected virtual Expression VisitListLiteral(ListLiteralExpression e)
    {
        List<Expression> els = RewriteList(e.Elements, VisitExpression);
        return ReferenceEquals(els, e.Elements) ? e : e with { Elements = els };
    }

    protected virtual Expression VisitSetLiteral(SetLiteralExpression e)
    {
        List<Expression> els = RewriteList(e.Elements, VisitExpression);
        return ReferenceEquals(els, e.Elements) ? e : e with { Elements = els };
    }

    protected virtual Expression VisitDictLiteral(DictLiteralExpression e)
    {
        List<(Expression Key, Expression Value)> pairs = RewriteList(e.Pairs,
            p =>
            {
                Expression k = VisitExpression(p.Key);
                Expression v = VisitExpression(p.Value);
                return ReferenceEquals(k, p.Key) && ReferenceEquals(v, p.Value) ? p : (k, v);
            });
        return ReferenceEquals(pairs, e.Pairs) ? e : e with { Pairs = pairs };
    }

    protected virtual Expression VisitInsertedText(InsertedTextExpression e)
    {
        List<InsertedTextPart> parts = RewriteList(e.Parts,
            part =>
            {
                if (part is not ExpressionPart ep) return part;
                Expression v = VisitExpression(ep.Expression);
                return ReferenceEquals(v, ep.Expression) ? part : ep with { Expression = v };
            });
        return ReferenceEquals(parts, e.Parts) ? e : e with { Parts = parts };
    }

    protected virtual Expression VisitIsPattern(IsPatternExpression e)
    {
        Expression o = VisitExpression(e.Expression);
        return ReferenceEquals(o, e.Expression) ? e : e with { Expression = o };
    }

    protected virtual Expression VisitFlagsTest(FlagsTestExpression e)
    {
        Expression s = VisitExpression(e.Subject);
        return ReferenceEquals(s, e.Subject) ? e : e with { Subject = s };
    }

    protected virtual Expression VisitGenericMemberRoutineCall(GenericMemberRoutineCallExpression e)
    {
        Expression o = VisitExpression(e.Object);
        List<Expression> args = RewriteList(e.Arguments, VisitExpression);
        return ReferenceEquals(o, e.Object) && ReferenceEquals(args, e.Arguments)
            ? e
            : e with { Object = o, Arguments = args };
    }

    protected virtual Expression VisitGenericMember(GenericMemberExpression e)
    {
        Expression o = VisitExpression(e.Object);
        return ReferenceEquals(o, e.Object) ? e : e with { Object = o };
    }

    // ---------------- Helpers ----------------

    /// <summary>Rewrites every item; returns the SAME list reference when nothing changed (so parents
    /// can skip rebuilding), otherwise a new list with the rewritten items.</summary>
    protected static List<T> RewriteList<T>(IReadOnlyList<T> items, System.Func<T, T> rewrite)
    {
        List<T>? result = null;
        for (int i = 0; i < items.Count; i++)
        {
            T original = items[i];
            T rewritten = rewrite(original);
            if (!ReferenceEquals(rewritten, original) && result == null)
            {
                result = new List<T>(capacity: items.Count);
                for (int j = 0; j < i; j++) result.Add(items[j]);
            }
            result?.Add(rewritten);
        }
        return result ?? (items as List<T> ?? items.ToList());
    }
}
