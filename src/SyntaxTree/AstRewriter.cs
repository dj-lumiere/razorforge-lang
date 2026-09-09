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

    /// <summary>
    /// Top-level statement dispatcher: matches <paramref name="stmt"/> on its concrete type and delegates
    /// to the appropriate <c>Visit*</c> hook. Leaf statement kinds (absent, pass, break, continue, etc.)
    /// are returned unchanged. Override this only to intercept ALL statement kinds uniformly.
    /// </summary>
    /// <param name="stmt">The statement node to rewrite.</param>
    /// <returns>The rewritten statement, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="BlockStatement"/> by visiting each child statement in order.
    /// Returns the original node when no children changed.
    /// </summary>
    /// <param name="s">The block statement to rewrite.</param>
    /// <returns>The rewritten block, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitBlock(BlockStatement s)
    {
        List<Statement> rewritten = RewriteList(s.Statements, VisitStatement);
        return ReferenceEquals(rewritten, s.Statements) ? s : s with { Statements = rewritten };
    }

    /// <summary>
    /// Rewrites an <see cref="IfStatement"/> by visiting its condition, then-branch, and optional
    /// else-branch. Returns the original node when no subexpressions or substatements changed.
    /// </summary>
    /// <param name="s">The if statement to rewrite.</param>
    /// <returns>The rewritten if statement, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="WhileStatement"/> by visiting its condition, body, and optional
    /// else-branch (executed when the loop exits normally). Returns the original node when nothing changed.
    /// </summary>
    /// <param name="s">The while statement to rewrite.</param>
    /// <returns>The rewritten while statement, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="LoopStatement"/> (unconditional loop) by visiting its body.
    /// Returns the original node when the body is unchanged.
    /// </summary>
    /// <param name="s">The loop statement to rewrite.</param>
    /// <returns>The rewritten loop statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitLoop(LoopStatement s)
    {
        Statement body = VisitStatement(s.Body);
        return ReferenceEquals(body, s.Body) ? s : s with { Body = body };
    }

    /// <summary>
    /// Rewrites an <see cref="EachStatement"/> by visiting its iterable expression, loop body, and
    /// optional else-branch. Returns the original node when none of the children changed.
    /// </summary>
    /// <param name="s">The each statement to rewrite.</param>
    /// <returns>The rewritten each statement, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="WhenStatement"/> by visiting the subject expression and each clause's body.
    /// Clause patterns are not rewritten (only statement/expression children are in scope).
    /// Returns the original node when neither the subject nor any clause body changed.
    /// </summary>
    /// <param name="s">The when statement to rewrite.</param>
    /// <returns>The rewritten when statement, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="DangerStatement"/> (an unsafe block) by visiting its body.
    /// Returns the original node when the body is unchanged.
    /// </summary>
    /// <param name="s">The danger statement to rewrite.</param>
    /// <returns>The rewritten danger statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitDanger(DangerStatement s)
    {
        Statement body = VisitStatement(s.Body);
        return ReferenceEquals(body, s.Body) ? s : s with { Body = (BlockStatement)body };
    }

    /// <summary>
    /// Rewrites a <see cref="UsingStatement"/> by visiting its resource expression, body, and optional
    /// fallback body (the cleanup branch when acquisition fails). Returns the original node when nothing changed.
    /// </summary>
    /// <param name="s">The using statement to rewrite.</param>
    /// <returns>The rewritten using statement, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="ReturnStatement"/> by visiting the returned value expression, if present.
    /// A bare <c>return</c> (no value) is returned unchanged.
    /// </summary>
    /// <param name="s">The return statement to rewrite.</param>
    /// <returns>The rewritten return statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitReturn(ReturnStatement s)
    {
        if (s.Value == null) return s;
        Expression v = VisitExpression(s.Value);
        return ReferenceEquals(v, s.Value) ? s : s with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="BecomesStatement"/> (a coroutine-yield-like value hand-off) by visiting
    /// its value expression. Returns the original node when the value is unchanged.
    /// </summary>
    /// <param name="s">The becomes statement to rewrite.</param>
    /// <returns>The rewritten becomes statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitBecomes(BecomesStatement s)
    {
        Expression v = VisitExpression(s.Value);
        return ReferenceEquals(v, s.Value) ? s : s with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="ThrowStatement"/> by visiting its error expression.
    /// Returns the original node when the error expression is unchanged.
    /// </summary>
    /// <param name="s">The throw statement to rewrite.</param>
    /// <returns>The rewritten throw statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitThrow(ThrowStatement s)
    {
        Expression e = VisitExpression(s.Error);
        return ReferenceEquals(e, s.Error) ? s : s with { Error = e };
    }

    /// <summary>
    /// Rewrites a <see cref="VariantReturnStatement"/> (a failable-variant return such as <c>absent</c>
    /// or a tagged-union payload return) by visiting the optional value expression.
    /// Returns the original node when the value is absent or unchanged.
    /// </summary>
    /// <param name="s">The variant return statement to rewrite.</param>
    /// <returns>The rewritten variant return statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitVariantReturn(VariantReturnStatement s)
    {
        if (s.Value == null) return s;
        Expression v = VisitExpression(s.Value);
        return ReferenceEquals(v, s.Value) ? s : s with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="DiscardStatement"/> (an explicit <c>discard</c> of a value-producing expression)
    /// by visiting the inner expression. Returns the original node when the expression is unchanged.
    /// </summary>
    /// <param name="s">The discard statement to rewrite.</param>
    /// <returns>The rewritten discard statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitDiscard(DiscardStatement s)
    {
        Expression e = VisitExpression(s.Expression);
        return ReferenceEquals(e, s.Expression) ? s : s with { Expression = e };
    }

    /// <summary>
    /// Rewrites an <see cref="ExpressionStatement"/> (an expression used as a statement for its side effects)
    /// by visiting the inner expression. Returns the original node when the expression is unchanged.
    /// </summary>
    /// <param name="s">The expression statement to rewrite.</param>
    /// <returns>The rewritten expression statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitExpressionStatement(ExpressionStatement s)
    {
        Expression e = VisitExpression(s.Expression);
        return ReferenceEquals(e, s.Expression) ? s : s with { Expression = e };
    }

    /// <summary>
    /// Rewrites an <see cref="AssignmentStatement"/> by visiting both the target (lvalue) and the
    /// value (rvalue) expressions. Returns the original node when neither changed.
    /// </summary>
    /// <param name="s">The assignment statement to rewrite.</param>
    /// <returns>The rewritten assignment statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitAssignment(AssignmentStatement s)
    {
        Expression target = VisitExpression(s.Target);
        Expression value = VisitExpression(s.Value);
        return ReferenceEquals(target, s.Target) && ReferenceEquals(value, s.Value)
            ? s
            : s with { Target = target, Value = value };
    }

    /// <summary>
    /// Rewrites a <see cref="DeclarationStatement"/> by visiting the variable initializer expression,
    /// if the declaration is a <see cref="VariableDeclaration"/> with an initializer present.
    /// Declaration statements without initializers are returned unchanged.
    /// </summary>
    /// <param name="s">The declaration statement to rewrite.</param>
    /// <returns>The rewritten declaration statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitDeclarationStatement(DeclarationStatement s)
    {
        if (s.Declaration is not VariableDeclaration { Initializer: { } init } vd) return s;
        Expression e = VisitExpression(init);
        return ReferenceEquals(e, init) ? s : s with { Declaration = vd with { Initializer = e } };
    }

    // ---------------- Expressions ----------------

    /// <summary>
    /// Top-level expression dispatcher: matches <paramref name="expr"/> on its concrete type and delegates
    /// to the appropriate <c>Visit*</c> hook. Leaf expression kinds (literals, identifiers, etc.) are
    /// returned unchanged. Override this only to intercept ALL expression kinds uniformly.
    /// </summary>
    /// <param name="expr">The expression node to rewrite.</param>
    /// <returns>The rewritten expression, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="BinaryExpression"/> by visiting its left and right operands.
    /// Returns the original node when both operands are unchanged.
    /// </summary>
    /// <param name="e">The binary expression to rewrite.</param>
    /// <returns>The rewritten binary expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitBinary(BinaryExpression e)
    {
        Expression l = VisitExpression(e.Left);
        Expression r = VisitExpression(e.Right);
        return ReferenceEquals(l, e.Left) && ReferenceEquals(r, e.Right) ? e : e with { Left = l, Right = r };
    }

    /// <summary>
    /// Rewrites a <see cref="UnaryExpression"/> by visiting its single operand.
    /// Returns the original node when the operand is unchanged.
    /// </summary>
    /// <param name="e">The unary expression to rewrite.</param>
    /// <returns>The rewritten unary expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitUnary(UnaryExpression e)
    {
        Expression o = VisitExpression(e.Operand);
        return ReferenceEquals(o, e.Operand) ? e : e with { Operand = o };
    }

    /// <summary>
    /// Rewrites a <see cref="CompoundAssignmentExpression"/> (e.g. <c>x += y</c>) by visiting
    /// its target (lvalue) and value (rvalue) expressions. Returns the original node when neither changed.
    /// </summary>
    /// <param name="e">The compound assignment expression to rewrite.</param>
    /// <returns>The rewritten compound assignment expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitCompoundAssignment(CompoundAssignmentExpression e)
    {
        Expression t = VisitExpression(e.Target);
        Expression v = VisitExpression(e.Value);
        return ReferenceEquals(t, e.Target) && ReferenceEquals(v, e.Value) ? e : e with { Target = t, Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="CallExpression"/> by visiting the callee expression and each argument.
    /// Returns the original node when the callee and all arguments are unchanged.
    /// </summary>
    /// <param name="e">The call expression to rewrite.</param>
    /// <returns>The rewritten call expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitCall(CallExpression e)
    {
        Expression callee = VisitExpression(e.Callee);
        List<Expression> args = RewriteList(e.Arguments, VisitExpression);
        return ReferenceEquals(callee, e.Callee) && ReferenceEquals(args, e.Arguments)
            ? e
            : e with { Callee = callee, Arguments = args };
    }

    /// <summary>
    /// Rewrites a <see cref="NamedArgumentExpression"/> (a <c>name: value</c> argument wrapper) by
    /// visiting its value expression. Returns the original node when the value is unchanged.
    /// </summary>
    /// <param name="e">The named argument expression to rewrite.</param>
    /// <returns>The rewritten named argument expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitNamedArgument(NamedArgumentExpression e)
    {
        Expression v = VisitExpression(e.Value);
        return ReferenceEquals(v, e.Value) ? e : e with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="MemberExpression"/> (a <c>obj.field</c> access) by visiting the
    /// receiver object expression. Returns the original node when the object is unchanged.
    /// </summary>
    /// <param name="e">The member expression to rewrite.</param>
    /// <returns>The rewritten member expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitMember(MemberExpression e)
    {
        Expression o = VisitExpression(e.Object);
        return ReferenceEquals(o, e.Object) ? e : e with { Object = o };
    }

    /// <summary>
    /// Rewrites an <see cref="OptionalMemberExpression"/> (a null-safe <c>obj?.field</c> access) by
    /// visiting the receiver object expression. Returns the original node when the object is unchanged.
    /// </summary>
    /// <param name="e">The optional member expression to rewrite.</param>
    /// <returns>The rewritten optional member expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitOptionalMember(OptionalMemberExpression e)
    {
        Expression o = VisitExpression(e.Object);
        return ReferenceEquals(o, e.Object) ? e : e with { Object = o };
    }

    /// <summary>
    /// Rewrites an <see cref="IndexExpression"/> by visiting the object and index sub-expressions.
    /// The resolved type and resolved setitem routine attached to the node are propagated to the rebuilt
    /// node so that post-SA metadata is not lost during reconstruction.
    /// Returns the original node when both the object and index are unchanged.
    /// </summary>
    /// <param name="e">The index expression to rewrite.</param>
    /// <returns>The rewritten index expression, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="ConditionalExpression"/> (<c>if c then t else f</c>) by visiting
    /// the condition, true-branch, and false-branch expressions. Returns the original node when all
    /// three sub-expressions are unchanged.
    /// </summary>
    /// <param name="e">The conditional expression to rewrite.</param>
    /// <returns>The rewritten conditional expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitConditional(ConditionalExpression e)
    {
        Expression c = VisitExpression(e.Condition);
        Expression t = VisitExpression(e.TrueExpression);
        Expression f = VisitExpression(e.FalseExpression);
        return ReferenceEquals(c, e.Condition) && ReferenceEquals(t, e.TrueExpression) && ReferenceEquals(f, e.FalseExpression)
            ? e
            : e with { Condition = c, TrueExpression = t, FalseExpression = f };
    }

    /// <summary>
    /// Rewrites a <see cref="BlockExpression"/> (an expression that evaluates a sub-expression after
    /// a sequence of statements) by visiting its value expression.
    /// Returns the original node when the value is unchanged.
    /// </summary>
    /// <param name="e">The block expression to rewrite.</param>
    /// <returns>The rewritten block expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitBlockExpression(BlockExpression e)
    {
        Expression v = VisitExpression(e.Value);
        return ReferenceEquals(v, e.Value) ? e : e with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="CreatorExpression"/> (a record/entity construction literal) by visiting
    /// each member-variable initializer expression. The member names are preserved unchanged.
    /// Returns the original node when all member initializers are unchanged.
    /// </summary>
    /// <param name="e">The creator expression to rewrite.</param>
    /// <returns>The rewritten creator expression, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites a <see cref="TypeConversionExpression"/> (an explicit type cast) by visiting the
    /// inner expression. Returns the original node when the expression is unchanged.
    /// </summary>
    /// <param name="e">The type conversion expression to rewrite.</param>
    /// <returns>The rewritten type conversion expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitTypeConversion(TypeConversionExpression e)
    {
        Expression v = VisitExpression(e.Expression);
        return ReferenceEquals(v, e.Expression) ? e : e with { Expression = v };
    }

    /// <summary>
    /// Rewrites a <see cref="StealExpression"/> (an ownership-transfer <c>steal</c> prefix) by visiting
    /// its operand. Returns the original node when the operand is unchanged.
    /// </summary>
    /// <param name="e">The steal expression to rewrite.</param>
    /// <returns>The rewritten steal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitSteal(StealExpression e)
    {
        Expression o = VisitExpression(e.Operand);
        return ReferenceEquals(o, e.Operand) ? e : e with { Operand = o };
    }

    /// <summary>
    /// Rewrites a <see cref="BackIndexExpression"/> (a reverse-index <c>^n</c> expression) by visiting
    /// its operand. Returns the original node when the operand is unchanged.
    /// </summary>
    /// <param name="e">The back-index expression to rewrite.</param>
    /// <returns>The rewritten back-index expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitBackIndex(BackIndexExpression e)
    {
        Expression o = VisitExpression(e.Operand);
        return ReferenceEquals(o, e.Operand) ? e : e with { Operand = o };
    }

    /// <summary>
    /// Rewrites a <see cref="RangeExpression"/> by visiting the start, end, and optional step
    /// sub-expressions. Returns the original node when all three are unchanged.
    /// </summary>
    /// <param name="e">The range expression to rewrite.</param>
    /// <returns>The rewritten range expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitRange(RangeExpression e)
    {
        Expression start = VisitExpression(e.Start);
        Expression end = VisitExpression(e.End);
        Expression? step = e.Step != null ? VisitExpression(e.Step) : null;
        return ReferenceEquals(start, e.Start) && ReferenceEquals(end, e.End) && ReferenceEquals(step, e.Step)
            ? e
            : e with { Start = start, End = end, Step = step };
    }

    /// <summary>
    /// Rewrites a <see cref="ChainedComparisonExpression"/> (e.g. <c>0 &lt;= x &lt;= 10</c>) by visiting
    /// each operand expression. Returns the original node when all operands are unchanged.
    /// </summary>
    /// <param name="e">The chained comparison expression to rewrite.</param>
    /// <returns>The rewritten chained comparison expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitChainedComparison(ChainedComparisonExpression e)
    {
        List<Expression> ops = RewriteList(e.Operands, VisitExpression);
        return ReferenceEquals(ops, e.Operands) ? e : e with { Operands = ops };
    }

    /// <summary>
    /// Rewrites a <see cref="TupleLiteralExpression"/> by visiting each element expression.
    /// Returns the original node when all elements are unchanged.
    /// </summary>
    /// <param name="e">The tuple literal expression to rewrite.</param>
    /// <returns>The rewritten tuple literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitTupleLiteral(TupleLiteralExpression e)
    {
        List<Expression> els = RewriteList(e.Elements, VisitExpression);
        return ReferenceEquals(els, e.Elements) ? e : e with { Elements = els };
    }

    /// <summary>
    /// Rewrites a <see cref="ListLiteralExpression"/> by visiting each element expression.
    /// Returns the original node when all elements are unchanged.
    /// </summary>
    /// <param name="e">The list literal expression to rewrite.</param>
    /// <returns>The rewritten list literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitListLiteral(ListLiteralExpression e)
    {
        List<Expression> els = RewriteList(e.Elements, VisitExpression);
        return ReferenceEquals(els, e.Elements) ? e : e with { Elements = els };
    }

    /// <summary>
    /// Rewrites a <see cref="SetLiteralExpression"/> by visiting each element expression.
    /// Returns the original node when all elements are unchanged.
    /// </summary>
    /// <param name="e">The set literal expression to rewrite.</param>
    /// <returns>The rewritten set literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitSetLiteral(SetLiteralExpression e)
    {
        List<Expression> els = RewriteList(e.Elements, VisitExpression);
        return ReferenceEquals(els, e.Elements) ? e : e with { Elements = els };
    }

    /// <summary>
    /// Rewrites a <see cref="DictLiteralExpression"/> by visiting each key-value pair's key and value
    /// expressions. Returns the original node when all pairs are unchanged.
    /// </summary>
    /// <param name="e">The dict literal expression to rewrite.</param>
    /// <returns>The rewritten dict literal expression, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites an <see cref="InsertedTextExpression"/> (an f-string interpolation) by visiting the
    /// expression inside each <see cref="ExpressionPart"/>; non-expression parts (literal text segments)
    /// are passed through unchanged. Returns the original node when no expression parts changed.
    /// </summary>
    /// <param name="e">The inserted text expression to rewrite.</param>
    /// <returns>The rewritten inserted text expression, or the original reference if nothing changed.</returns>
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

    /// <summary>
    /// Rewrites an <see cref="IsPatternExpression"/> (a pattern-test such as <c>x is T t</c>) by visiting
    /// the scrutinee expression. The pattern itself is not rewritten.
    /// Returns the original node when the scrutinee is unchanged.
    /// </summary>
    /// <param name="e">The is-pattern expression to rewrite.</param>
    /// <returns>The rewritten is-pattern expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitIsPattern(IsPatternExpression e)
    {
        Expression o = VisitExpression(e.Expression);
        return ReferenceEquals(o, e.Expression) ? e : e with { Expression = o };
    }

    /// <summary>
    /// Rewrites a <see cref="FlagsTestExpression"/> (a bitfield membership test) by visiting the
    /// subject expression. Returns the original node when the subject is unchanged.
    /// </summary>
    /// <param name="e">The flags-test expression to rewrite.</param>
    /// <returns>The rewritten flags-test expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitFlagsTest(FlagsTestExpression e)
    {
        Expression s = VisitExpression(e.Subject);
        return ReferenceEquals(s, e.Subject) ? e : e with { Subject = s };
    }

    /// <summary>
    /// Rewrites a <see cref="GenericMemberRoutineCallExpression"/> (a <c>obj.method[T](args)</c> call
    /// carrying explicit type arguments) by visiting the receiver object and each argument expression.
    /// Returns the original node when the object and all arguments are unchanged.
    /// </summary>
    /// <param name="e">The generic member routine call expression to rewrite.</param>
    /// <returns>The rewritten expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitGenericMemberRoutineCall(GenericMemberRoutineCallExpression e)
    {
        Expression o = VisitExpression(e.Object);
        List<Expression> args = RewriteList(e.Arguments, VisitExpression);
        return ReferenceEquals(o, e.Object) && ReferenceEquals(args, e.Arguments)
            ? e
            : e with { Object = o, Arguments = args };
    }

    /// <summary>
    /// Rewrites a <see cref="GenericMemberExpression"/> (a <c>obj.member[T]</c> access carrying explicit
    /// type arguments) by visiting the receiver object expression.
    /// Returns the original node when the object is unchanged.
    /// </summary>
    /// <param name="e">The generic member expression to rewrite.</param>
    /// <returns>The rewritten generic member expression, or the original reference if nothing changed.</returns>
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
