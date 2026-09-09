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
            BlockStatement s => VisitBlock(s: s),
            IfStatement s => VisitIf(s: s),
            WhileStatement s => VisitWhile(s: s),
            LoopStatement s => VisitLoop(s: s),
            EachStatement s => VisitEach(s: s),
            WhenStatement s => VisitWhen(s: s),
            DangerStatement s => VisitDanger(s: s),
            UsingStatement s => VisitUsing(s: s),
            ReturnStatement s => VisitReturn(s: s),
            BecomesStatement s => VisitBecomes(s: s),
            ThrowStatement s => VisitThrow(s: s),
            VariantReturnStatement s => VisitVariantReturn(s: s),
            DiscardStatement s => VisitDiscard(s: s),
            ExpressionStatement s => VisitExpressionStatement(s: s),
            AssignmentStatement s => VisitAssignment(s: s),
            DeclarationStatement s => VisitDeclarationStatement(s: s),
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
        List<Statement> rewritten = RewriteList(items: s.Statements, rewrite: VisitStatement);
        return ReferenceEquals(objA: rewritten, objB: s.Statements)
            ? s
            : s with { Statements = rewritten };
    }

    /// <summary>
    /// Rewrites an <see cref="IfStatement"/> by visiting its condition, then-branch, and optional
    /// else-branch. Returns the original node when no subexpressions or substatements changed.
    /// </summary>
    /// <param name="s">The if statement to rewrite.</param>
    /// <returns>The rewritten if statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitIf(IfStatement s)
    {
        Expression cond = VisitExpression(expr: s.Condition);
        Statement then = VisitStatement(stmt: s.ThenStatement);
        Statement? els = s.ElseStatement != null
            ? VisitStatement(stmt: s.ElseStatement)
            : null;
        return ReferenceEquals(objA: cond, objB: s.Condition) &&
               ReferenceEquals(objA: then, objB: s.ThenStatement) &&
               ReferenceEquals(objA: els, objB: s.ElseStatement)
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
        Expression cond = VisitExpression(expr: s.Condition);
        Statement body = VisitStatement(stmt: s.Body);
        Statement? els = s.ElseBranch != null
            ? VisitStatement(stmt: s.ElseBranch)
            : null;
        return ReferenceEquals(objA: cond, objB: s.Condition) &&
               ReferenceEquals(objA: body, objB: s.Body) &&
               ReferenceEquals(objA: els, objB: s.ElseBranch)
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
        Statement body = VisitStatement(stmt: s.Body);
        return ReferenceEquals(objA: body, objB: s.Body)
            ? s
            : s with { Body = body };
    }

    /// <summary>
    /// Rewrites an <see cref="EachStatement"/> by visiting its iterable expression, loop body, and
    /// optional else-branch. Returns the original node when none of the children changed.
    /// </summary>
    /// <param name="s">The each statement to rewrite.</param>
    /// <returns>The rewritten each statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitEach(EachStatement s)
    {
        Expression iterable = VisitExpression(expr: s.Iterable);
        Statement body = VisitStatement(stmt: s.Body);
        Statement? els = s.ElseBranch != null
            ? VisitStatement(stmt: s.ElseBranch)
            : null;
        return ReferenceEquals(objA: iterable, objB: s.Iterable) &&
               ReferenceEquals(objA: body, objB: s.Body) &&
               ReferenceEquals(objA: els, objB: s.ElseBranch)
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
        Expression subject = VisitExpression(expr: s.Expression);
        List<WhenClause> clauses = RewriteList(items: s.Clauses,
            rewrite: c =>
            {
                Statement body = VisitStatement(stmt: c.Body);
                return ReferenceEquals(objA: body, objB: c.Body)
                    ? c
                    : c with { Body = body };
            });
        return ReferenceEquals(objA: subject, objB: s.Expression) &&
               ReferenceEquals(objA: clauses, objB: s.Clauses)
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
        Statement body = VisitStatement(stmt: s.Body);
        return ReferenceEquals(objA: body, objB: s.Body)
            ? s
            : s with { Body = (BlockStatement)body };
    }

    /// <summary>
    /// Rewrites a <see cref="UsingStatement"/> by visiting its resource expression, body, and optional
    /// fallback body (the cleanup branch when acquisition fails). Returns the original node when nothing changed.
    /// </summary>
    /// <param name="s">The using statement to rewrite.</param>
    /// <returns>The rewritten using statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitUsing(UsingStatement s)
    {
        Expression resource = VisitExpression(expr: s.Resource);
        Statement body = VisitStatement(stmt: s.Body);
        Statement? fallback = s.FallbackBody != null
            ? VisitStatement(stmt: s.FallbackBody)
            : null;
        return ReferenceEquals(objA: resource, objB: s.Resource) &&
               ReferenceEquals(objA: body, objB: s.Body) &&
               ReferenceEquals(objA: fallback, objB: s.FallbackBody)
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
        if (s.Value == null)
        {
            return s;
        }

        Expression v = VisitExpression(expr: s.Value);
        return ReferenceEquals(objA: v, objB: s.Value)
            ? s
            : s with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="BecomesStatement"/> (a coroutine-yield-like value hand-off) by visiting
    /// its value expression. Returns the original node when the value is unchanged.
    /// </summary>
    /// <param name="s">The becomes statement to rewrite.</param>
    /// <returns>The rewritten becomes statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitBecomes(BecomesStatement s)
    {
        Expression v = VisitExpression(expr: s.Value);
        return ReferenceEquals(objA: v, objB: s.Value)
            ? s
            : s with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="ThrowStatement"/> by visiting its error expression.
    /// Returns the original node when the error expression is unchanged.
    /// </summary>
    /// <param name="s">The throw statement to rewrite.</param>
    /// <returns>The rewritten throw statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitThrow(ThrowStatement s)
    {
        Expression e = VisitExpression(expr: s.Error);
        return ReferenceEquals(objA: e, objB: s.Error)
            ? s
            : s with { Error = e };
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
        if (s.Value == null)
        {
            return s;
        }

        Expression v = VisitExpression(expr: s.Value);
        return ReferenceEquals(objA: v, objB: s.Value)
            ? s
            : s with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="DiscardStatement"/> (an explicit <c>discard</c> of a value-producing expression)
    /// by visiting the inner expression. Returns the original node when the expression is unchanged.
    /// </summary>
    /// <param name="s">The discard statement to rewrite.</param>
    /// <returns>The rewritten discard statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitDiscard(DiscardStatement s)
    {
        Expression e = VisitExpression(expr: s.Expression);
        return ReferenceEquals(objA: e, objB: s.Expression)
            ? s
            : s with { Expression = e };
    }

    /// <summary>
    /// Rewrites an <see cref="ExpressionStatement"/> (an expression used as a statement for its side effects)
    /// by visiting the inner expression. Returns the original node when the expression is unchanged.
    /// </summary>
    /// <param name="s">The expression statement to rewrite.</param>
    /// <returns>The rewritten expression statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitExpressionStatement(ExpressionStatement s)
    {
        Expression e = VisitExpression(expr: s.Expression);
        return ReferenceEquals(objA: e, objB: s.Expression)
            ? s
            : s with { Expression = e };
    }

    /// <summary>
    /// Rewrites an <see cref="AssignmentStatement"/> by visiting both the target (lvalue) and the
    /// value (rvalue) expressions. Returns the original node when neither changed.
    /// </summary>
    /// <param name="s">The assignment statement to rewrite.</param>
    /// <returns>The rewritten assignment statement, or the original reference if nothing changed.</returns>
    protected virtual Statement VisitAssignment(AssignmentStatement s)
    {
        Expression target = VisitExpression(expr: s.Target);
        Expression value = VisitExpression(expr: s.Value);
        return ReferenceEquals(objA: target, objB: s.Target) &&
               ReferenceEquals(objA: value, objB: s.Value)
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
        if (s.Declaration is not VariableDeclaration { Initializer: { } init } vd)
        {
            return s;
        }

        Expression e = VisitExpression(expr: init);
        return ReferenceEquals(objA: e, objB: init)
            ? s
            : s with { Declaration = vd with { Initializer = e } };
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
            BinaryExpression e => VisitBinary(e: e),
            UnaryExpression e => VisitUnary(e: e),
            CompoundAssignmentExpression e => VisitCompoundAssignment(e: e),
            CallExpression e => VisitCall(e: e),
            NamedArgumentExpression e => VisitNamedArgument(e: e),
            MemberExpression e => VisitMember(e: e),
            OptionalMemberExpression e => VisitOptionalMember(e: e),
            IndexExpression e => VisitIndex(e: e),
            ConditionalExpression e => VisitConditional(e: e),
            BlockExpression e => VisitBlockExpression(e: e),
            CreatorExpression e => VisitCreator(e: e),
            TypeConversionExpression e => VisitTypeConversion(e: e),
            StealExpression e => VisitSteal(e: e),
            BackIndexExpression e => VisitBackIndex(e: e),
            RangeExpression e => VisitRange(e: e),
            ChainedComparisonExpression e => VisitChainedComparison(e: e),
            TupleLiteralExpression e => VisitTupleLiteral(e: e),
            ListLiteralExpression e => VisitListLiteral(e: e),
            SetLiteralExpression e => VisitSetLiteral(e: e),
            DictLiteralExpression e => VisitDictLiteral(e: e),
            InsertedTextExpression e => VisitInsertedText(e: e),
            IsPatternExpression e => VisitIsPattern(e: e),
            FlagsTestExpression e => VisitFlagsTest(e: e),
            GenericMemberRoutineCallExpression e => VisitGenericMemberRoutineCall(e: e),
            GenericMemberExpression e => VisitGenericMember(e: e),
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
        Expression l = VisitExpression(expr: e.Left);
        Expression r = VisitExpression(expr: e.Right);
        return ReferenceEquals(objA: l, objB: e.Left) && ReferenceEquals(objA: r, objB: e.Right)
            ? e
            : e with { Left = l, Right = r };
    }

    /// <summary>
    /// Rewrites a <see cref="UnaryExpression"/> by visiting its single operand.
    /// Returns the original node when the operand is unchanged.
    /// </summary>
    /// <param name="e">The unary expression to rewrite.</param>
    /// <returns>The rewritten unary expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitUnary(UnaryExpression e)
    {
        Expression o = VisitExpression(expr: e.Operand);
        return ReferenceEquals(objA: o, objB: e.Operand)
            ? e
            : e with { Operand = o };
    }

    /// <summary>
    /// Rewrites a <see cref="CompoundAssignmentExpression"/> (e.g. <c>x += y</c>) by visiting
    /// its target (lvalue) and value (rvalue) expressions. Returns the original node when neither changed.
    /// </summary>
    /// <param name="e">The compound assignment expression to rewrite.</param>
    /// <returns>The rewritten compound assignment expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitCompoundAssignment(CompoundAssignmentExpression e)
    {
        Expression t = VisitExpression(expr: e.Target);
        Expression v = VisitExpression(expr: e.Value);
        return ReferenceEquals(objA: t, objB: e.Target) && ReferenceEquals(objA: v, objB: e.Value)
            ? e
            : e with { Target = t, Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="CallExpression"/> by visiting the callee expression and each argument.
    /// Returns the original node when the callee and all arguments are unchanged.
    /// </summary>
    /// <param name="e">The call expression to rewrite.</param>
    /// <returns>The rewritten call expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitCall(CallExpression e)
    {
        Expression callee = VisitExpression(expr: e.Callee);
        List<Expression> args = RewriteList(items: e.Arguments, rewrite: VisitExpression);
        return ReferenceEquals(objA: callee, objB: e.Callee) &&
               ReferenceEquals(objA: args, objB: e.Arguments)
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
        Expression v = VisitExpression(expr: e.Value);
        return ReferenceEquals(objA: v, objB: e.Value)
            ? e
            : e with { Value = v };
    }

    /// <summary>
    /// Rewrites a <see cref="MemberExpression"/> (a <c>obj.field</c> access) by visiting the
    /// receiver object expression. Returns the original node when the object is unchanged.
    /// </summary>
    /// <param name="e">The member expression to rewrite.</param>
    /// <returns>The rewritten member expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitMember(MemberExpression e)
    {
        Expression o = VisitExpression(expr: e.Object);
        return ReferenceEquals(objA: o, objB: e.Object)
            ? e
            : e with { Object = o };
    }

    /// <summary>
    /// Rewrites an <see cref="OptionalMemberExpression"/> (a null-safe <c>obj?.field</c> access) by
    /// visiting the receiver object expression. Returns the original node when the object is unchanged.
    /// </summary>
    /// <param name="e">The optional member expression to rewrite.</param>
    /// <returns>The rewritten optional member expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitOptionalMember(OptionalMemberExpression e)
    {
        Expression o = VisitExpression(expr: e.Object);
        return ReferenceEquals(objA: o, objB: e.Object)
            ? e
            : e with { Object = o };
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
        Expression o = VisitExpression(expr: e.Object);
        Expression i = VisitExpression(expr: e.Index);
        if (ReferenceEquals(objA: o, objB: e.Object) && ReferenceEquals(objA: i, objB: e.Index))
        {
            return e;
        }

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
        Expression c = VisitExpression(expr: e.Condition);
        Expression t = VisitExpression(expr: e.TrueExpression);
        Expression f = VisitExpression(expr: e.FalseExpression);
        return ReferenceEquals(objA: c, objB: e.Condition) &&
               ReferenceEquals(objA: t, objB: e.TrueExpression) &&
               ReferenceEquals(objA: f, objB: e.FalseExpression)
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
        Expression v = VisitExpression(expr: e.Value);
        return ReferenceEquals(objA: v, objB: e.Value)
            ? e
            : e with { Value = v };
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
        List<(string Name, Expression Value)> mvs = RewriteList(items: e.MemberVariables,
            rewrite: mv =>
            {
                Expression v = VisitExpression(expr: mv.Value);
                return ReferenceEquals(objA: v, objB: mv.Value)
                    ? mv
                    : (mv.Name, v);
            });
        return ReferenceEquals(objA: mvs, objB: e.MemberVariables)
            ? e
            : e with { MemberVariables = mvs };
    }

    /// <summary>
    /// Rewrites a <see cref="TypeConversionExpression"/> (an explicit type cast) by visiting the
    /// inner expression. Returns the original node when the expression is unchanged.
    /// </summary>
    /// <param name="e">The type conversion expression to rewrite.</param>
    /// <returns>The rewritten type conversion expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitTypeConversion(TypeConversionExpression e)
    {
        Expression v = VisitExpression(expr: e.Expression);
        return ReferenceEquals(objA: v, objB: e.Expression)
            ? e
            : e with { Expression = v };
    }

    /// <summary>
    /// Rewrites a <see cref="StealExpression"/> (an ownership-transfer <c>steal</c> prefix) by visiting
    /// its operand. Returns the original node when the operand is unchanged.
    /// </summary>
    /// <param name="e">The steal expression to rewrite.</param>
    /// <returns>The rewritten steal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitSteal(StealExpression e)
    {
        Expression o = VisitExpression(expr: e.Operand);
        return ReferenceEquals(objA: o, objB: e.Operand)
            ? e
            : e with { Operand = o };
    }

    /// <summary>
    /// Rewrites a <see cref="BackIndexExpression"/> (a reverse-index <c>^n</c> expression) by visiting
    /// its operand. Returns the original node when the operand is unchanged.
    /// </summary>
    /// <param name="e">The back-index expression to rewrite.</param>
    /// <returns>The rewritten back-index expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitBackIndex(BackIndexExpression e)
    {
        Expression o = VisitExpression(expr: e.Operand);
        return ReferenceEquals(objA: o, objB: e.Operand)
            ? e
            : e with { Operand = o };
    }

    /// <summary>
    /// Rewrites a <see cref="RangeExpression"/> by visiting the start, end, and optional step
    /// sub-expressions. Returns the original node when all three are unchanged.
    /// </summary>
    /// <param name="e">The range expression to rewrite.</param>
    /// <returns>The rewritten range expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitRange(RangeExpression e)
    {
        Expression start = VisitExpression(expr: e.Start);
        Expression end = VisitExpression(expr: e.End);
        Expression? step = e.Step != null
            ? VisitExpression(expr: e.Step)
            : null;
        return ReferenceEquals(objA: start, objB: e.Start) &&
               ReferenceEquals(objA: end, objB: e.End) && ReferenceEquals(objA: step, objB: e.Step)
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
        List<Expression> ops = RewriteList(items: e.Operands, rewrite: VisitExpression);
        return ReferenceEquals(objA: ops, objB: e.Operands)
            ? e
            : e with { Operands = ops };
    }

    /// <summary>
    /// Rewrites a <see cref="TupleLiteralExpression"/> by visiting each element expression.
    /// Returns the original node when all elements are unchanged.
    /// </summary>
    /// <param name="e">The tuple literal expression to rewrite.</param>
    /// <returns>The rewritten tuple literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitTupleLiteral(TupleLiteralExpression e)
    {
        List<Expression> els = RewriteList(items: e.Elements, rewrite: VisitExpression);
        return ReferenceEquals(objA: els, objB: e.Elements)
            ? e
            : e with { Elements = els };
    }

    /// <summary>
    /// Rewrites a <see cref="ListLiteralExpression"/> by visiting each element expression.
    /// Returns the original node when all elements are unchanged.
    /// </summary>
    /// <param name="e">The list literal expression to rewrite.</param>
    /// <returns>The rewritten list literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitListLiteral(ListLiteralExpression e)
    {
        List<Expression> els = RewriteList(items: e.Elements, rewrite: VisitExpression);
        return ReferenceEquals(objA: els, objB: e.Elements)
            ? e
            : e with { Elements = els };
    }

    /// <summary>
    /// Rewrites a <see cref="SetLiteralExpression"/> by visiting each element expression.
    /// Returns the original node when all elements are unchanged.
    /// </summary>
    /// <param name="e">The set literal expression to rewrite.</param>
    /// <returns>The rewritten set literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitSetLiteral(SetLiteralExpression e)
    {
        List<Expression> els = RewriteList(items: e.Elements, rewrite: VisitExpression);
        return ReferenceEquals(objA: els, objB: e.Elements)
            ? e
            : e with { Elements = els };
    }

    /// <summary>
    /// Rewrites a <see cref="DictLiteralExpression"/> by visiting each key-value pair's key and value
    /// expressions. Returns the original node when all pairs are unchanged.
    /// </summary>
    /// <param name="e">The dict literal expression to rewrite.</param>
    /// <returns>The rewritten dict literal expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitDictLiteral(DictLiteralExpression e)
    {
        List<(Expression Key, Expression Value)> pairs = RewriteList(items: e.Pairs,
            rewrite: p =>
            {
                Expression k = VisitExpression(expr: p.Key);
                Expression v = VisitExpression(expr: p.Value);
                return ReferenceEquals(objA: k, objB: p.Key) &&
                       ReferenceEquals(objA: v, objB: p.Value)
                    ? p
                    : (k, v);
            });
        return ReferenceEquals(objA: pairs, objB: e.Pairs)
            ? e
            : e with { Pairs = pairs };
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
        List<InsertedTextPart> parts = RewriteList(items: e.Parts,
            rewrite: part =>
            {
                if (part is not ExpressionPart ep)
                {
                    return part;
                }

                Expression v = VisitExpression(expr: ep.Expression);
                return ReferenceEquals(objA: v, objB: ep.Expression)
                    ? part
                    : ep with { Expression = v };
            });
        return ReferenceEquals(objA: parts, objB: e.Parts)
            ? e
            : e with { Parts = parts };
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
        Expression o = VisitExpression(expr: e.Expression);
        return ReferenceEquals(objA: o, objB: e.Expression)
            ? e
            : e with { Expression = o };
    }

    /// <summary>
    /// Rewrites a <see cref="FlagsTestExpression"/> (a bitfield membership test) by visiting the
    /// subject expression. Returns the original node when the subject is unchanged.
    /// </summary>
    /// <param name="e">The flags-test expression to rewrite.</param>
    /// <returns>The rewritten flags-test expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitFlagsTest(FlagsTestExpression e)
    {
        Expression s = VisitExpression(expr: e.Subject);
        return ReferenceEquals(objA: s, objB: e.Subject)
            ? e
            : e with { Subject = s };
    }

    /// <summary>
    /// Rewrites a <see cref="GenericMemberRoutineCallExpression"/> (a <c>obj.method[T](args)</c> call
    /// carrying explicit type arguments) by visiting the receiver object and each argument expression.
    /// Returns the original node when the object and all arguments are unchanged.
    /// </summary>
    /// <param name="e">The generic member routine call expression to rewrite.</param>
    /// <returns>The rewritten expression, or the original reference if nothing changed.</returns>
    protected virtual Expression VisitGenericMemberRoutineCall(
        GenericMemberRoutineCallExpression e)
    {
        Expression o = VisitExpression(expr: e.Object);
        List<Expression> args = RewriteList(items: e.Arguments, rewrite: VisitExpression);
        return ReferenceEquals(objA: o, objB: e.Object) &&
               ReferenceEquals(objA: args, objB: e.Arguments)
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
        Expression o = VisitExpression(expr: e.Object);
        return ReferenceEquals(objA: o, objB: e.Object)
            ? e
            : e with { Object = o };
    }

    // ---------------- Helpers ----------------

    /// <summary>Rewrites every item; returns the SAME list reference when nothing changed (so parents
    /// can skip rebuilding), otherwise a new list with the rewritten items.</summary>
    protected static List<T> RewriteList<T>(IReadOnlyList<T> items, Func<T, T> rewrite)
    {
        List<T>? result = null;
        for (int i = 0; i < items.Count; i++)
        {
            T original = items[index: i];
            T rewritten = rewrite(arg: original);
            if (!ReferenceEquals(objA: rewritten, objB: original) && result == null)
            {
                result = new List<T>(capacity: items.Count);
                for (int j = 0; j < i; j++)
                {
                    result.Add(item: items[index: j]);
                }
            }

            result?.Add(item: rewritten);
        }

        return result ?? items as List<T> ?? items.ToList();
    }
}
