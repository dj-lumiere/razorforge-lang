using Compiler.Instantiation;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Lowers operator-sugar expressions to plain memberRoutine call nodes.
/// Runs after <see cref="ExpressionLoweringPass"/> in the per-file pipeline.
///
/// <para>Transformations:</para>
/// <list type="bullet">
///   <item><see cref="IndexExpression"/> (<c>obj[i]</c>) ??
///         <c>obj.getitem!(i)</c> -> failable memberRoutine call.</item>
///   <item><see cref="GenericMemberExpression"/> (<c>obj.field[i]</c>, parser quirk) ??
///         <c>MemberExpression(obj, field)</c> + <c>IndexExpression</c> ??<c>getitem!</c>.</item>
///   <item><see cref="BinaryExpression"/> with an overloadable operator ??
///         <c>left.MemberRoutine(you: right)</c>. Membership operators reverse operands:
///         <c>x in coll</c> ??<c>coll.contains(x)</c>.</item>
///   <item><see cref="UnaryExpression"/> with <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>) ??
///         <c>operand.unwrap()</c> -> always lowered, even in stdlib bodies (which bypass
///         <see cref="ExpressionLoweringPass"/>).</item>
///   <item><see cref="UnaryExpression"/> with a wired memberRoutine (<c>-</c>, <c>~</c>) ??
///         <c>operand.neg()</c> / <c>operand.bitnot()</c> when the memberRoutine is resolved.</item>
/// </list>
///
/// <para>Only the <em>value</em> side of <see cref="AssignmentStatement"/> is lowered.
/// Indexed-assignment targets (<c>arr[i] = val</c>) remain as <see cref="IndexExpression"/>
/// so codegen's <c>EmitAssignment</c> can dispatch to <c>setitem!</c>.</para>
/// </summary>
internal sealed class OperatorLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    /// <summary>The bare name of the element-access member routine (no failable suffix).</summary>
    private const string GetItemMemberRoutine = "getitem";

    /// <summary>Ordered set of signed integer widths used for upcasting arithmetic results.</summary>
    private static readonly int[] SignedWidths = [8, 16, 32, 64, 128];

    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    //  Statement lowering

    /// <summary>
    /// Lowers a <see cref="WhenStatement"/>. Unlike the base hook, this also lowers any
    /// <see cref="ExpressionPattern"/> guard (e.g. a <see cref="ChainedComparisonExpression"/>)
    /// in each clause — the base only recurses into the subject and clause bodies.
    /// </summary>
    protected override Statement VisitWhen(WhenStatement s)
    {
        Expression subj = VisitExpression(expr: s.Expression);
        var clauses = new List<WhenClause>(capacity: s.Clauses.Count);
        bool clauseChanged = false;
        foreach (WhenClause c in s.Clauses)
        {
            Statement lBody = VisitStatement(stmt: c.Body);
            // Also lower expression patterns (ChainedComparisonExpression guards, etc.)
            Pattern lPattern = c.Pattern is ExpressionPattern ep
                ? ep with { Expression = VisitExpression(expr: ep.Expression) }
                : c.Pattern;
            bool patternChanged = !ReferenceEquals(objA: lPattern, objB: c.Pattern);
            if (!ReferenceEquals(objA: lBody, objB: c.Body) || patternChanged)
            {
                clauses.Add(item: c with { Body = lBody, Pattern = lPattern });
                clauseChanged = true;
            }
            else
            {
                clauses.Add(item: c);
            }
        }

        bool changed = !ReferenceEquals(objA: subj, objB: s.Expression) || clauseChanged;
        return changed
            ? s with { Expression = subj, Clauses = clauses }
            : s;
    }

    /// <summary>
    /// Lowers an <see cref="AssignmentStatement"/> — ONLY the value, not the target. Unlike the base
    /// hook (which lowers both sides), an indexed-assignment target (<c>arr[i] = val</c>) must stay an
    /// <see cref="IndexExpression"/> so codegen's <c>EmitAssignment</c> can dispatch to <c>setitem!</c>;
    /// lowering the target would convert it to a <c>getitem!</c> call.
    /// </summary>
    protected override Statement VisitAssignment(AssignmentStatement s)
    {
        Expression val = VisitExpression(expr: s.Value);
        return ReferenceEquals(objA: val, objB: s.Value)
            ? s
            : s with { Value = val };
    }

    //  Expression lowering

    /// <summary>
    /// Dispatches the operator-sugar expression kinds this pass rewrites into their transform hooks.
    /// The base <see cref="AstRewriter.VisitExpression"/> supplies structural recursion for every other
    /// kind; two node types the base leaves untouched — <see cref="WithExpression"/> (base recurses its
    /// members) and <see cref="LambdaExpression"/> (its body) — are recursed here to preserve the
    /// original pass's coverage. <see cref="ConditionalExpression"/> is handled via
    /// <see cref="VisitConditional"/> (only its condition is lowered).
    /// </summary>
    public override Expression VisitExpression(Expression expr)
    {
        switch (expr)
        {
            case WithExpression withExpr:
            {
                Expression loweredBase = VisitExpression(expr: withExpr.Base);
                var updates =
                    new List<(List<string>? Path, Expression? Index, Expression Value)>(
                        capacity: withExpr.Updates.Count);
                bool changed = !ReferenceEquals(objA: loweredBase, objB: withExpr.Base);
                foreach ((List<string>? path, Expression? index, Expression value) in withExpr
                            .Updates)
                {
                    Expression loweredVal = VisitExpression(expr: value);
                    updates.Add(item: (path, index, loweredVal));
                    if (!ReferenceEquals(objA: loweredVal, objB: value))
                    {
                        changed = true;
                    }
                }

                return changed
                    ? withExpr with { Base = loweredBase, Updates = updates }
                    : expr;
            }

            case LambdaExpression lambda:
            {
                Expression loweredBody = VisitExpression(expr: lambda.Body);
                return ReferenceEquals(objA: loweredBody, objB: lambda.Body)
                    ? expr
                    : lambda with { Body = loweredBody };
            }

            default:
                return base.VisitExpression(expr: expr);
        }
    }

    // The original pass had NO case for the node kinds below: they fell to its `default: return expr`
    // arm — returned UNCHANGED, with no recursion into their children. The base hooks would recurse
    // into them, so override each to preserve the original leaf behavior exactly.
    protected override Expression VisitRange(RangeExpression e)
    {
        return e;
    }

    protected override Expression VisitDictLiteral(DictLiteralExpression e)
    {
        return e;
    }

    protected override Expression VisitSetLiteral(SetLiteralExpression e)
    {
        return e;
    }

    protected override Expression VisitOptionalMember(OptionalMemberExpression e)
    {
        return e;
    }

    protected override Expression VisitTypeConversion(TypeConversionExpression e)
    {
        return e;
    }

    protected override Expression VisitBackIndex(BackIndexExpression e)
    {
        return e;
    }

    protected override Expression VisitIsPattern(IsPatternExpression e)
    {
        return e;
    }

    protected override Expression VisitFlagsTest(FlagsTestExpression e)
    {
        return e;
    }

    protected override Expression VisitBlockExpression(BlockExpression e)
    {
        return e;
    }

    // IndexExpression -> obj.getitem!(idx)
    protected override Expression VisitIndex(IndexExpression e)
    {
        return LowerIndexExpression(idx: e);
    }

    /// <summary>
    /// Rewrites <see cref="GenericMemberExpression"/> forms:
    /// <list type="bullet">
    ///   <item><c>obj.field[i]</c> (type-args are index expressions in disguise) -> member + index
    ///         ??<c>getitem!</c>.</item>
    ///   <item><c>Ident[T]</c> typewise receiver (Object.Name == MemberName) -> a bare typed
    ///         <see cref="IdentifierExpression"/>.</item>
    ///   <item>no type-args -> a plain <see cref="MemberExpression"/> (object lowered).</item>
    /// </list>
    /// </summary>
    protected override Expression VisitGenericMember(GenericMemberExpression e)
    {
        // Parser quirk: obj.field[i] is parsed as GenericMemberExpression(obj, "field", [i]).
        // TypeArguments are index expressions in disguise; lower to IndexExpression then recurse.
        //
        // Exception: Ident[T] (type-with-type-args, e.g. NumericSumAdd[T].identity_lazy())
        // is parsed as GenericMemberExpression(Ident, Ident.Name, [T]) — Object.Name == MemberName.
        // Those are real type args, not indices; lower to a plain MemberExpression so the
        // typewise receiver flows through codegen normally.
        if (e is { TypeArguments.Count: > 0 } &&
            !(e.Object is IdentifierExpression idObj && idObj.Name == e.MemberName))
        {
            return LowerGenericMemberIndex(gme: e);
        }

        // Typewise receiver Ident[T] parsed as GenericMemberExpression(Ident, Ident.Name, [T]).
        // Collapse to a bare IdentifierExpression carrying the resolved type so the outer
        // MemberExpression (e.g. .identity_lazy()) sees an identifier with ResolvedType set —
        // that is how codegen detects typewise/common calls.
        if (e is { TypeArguments.Count: > 0, Object: IdentifierExpression typeIdent } &&
            typeIdent.Name == e.MemberName)
        {
            return new IdentifierExpression(Name: e.MemberName, Location: e.Location)
            {
                ResolvedType = e.ResolvedType ?? typeIdent.ResolvedType
            };
        }

        // No type arguments: plain member access; just lower the object.
        Expression loweredObj = VisitExpression(expr: e.Object);
        return ReferenceEquals(objA: loweredObj, objB: e.Object)
            ? e
            : new MemberExpression(Object: loweredObj,
                MemberName: e.MemberName,
                Location: e.Location) { ResolvedType = e.ResolvedType };
    }

    // ChainedComparisonExpression is lowered to an AND-chain of pairwise comparisons.
    // Middle operands may be evaluated twice; acceptable here since chained
    // comparisons in stdlib bodies use trivially pure expressions (identifiers/literals).
    protected override Expression VisitChainedComparison(ChainedComparisonExpression e)
    {
        return LowerChainedComparison(chain: e);
    }

    // BinaryExpression is lowered to a member-routine call receiver.MemberRoutine(you: arg).
    // Operators with GetMemberRoutineName() == null (And, Or, Is, Identical, But, ...)
    // are not overloadable and stay as BinaryExpression for codegen.
    protected override Expression VisitBinary(BinaryExpression e)
    {
        return LowerBinaryExpression(expr: e, bin: e);
    }

    /// <summary>
    /// Rewrites a <see cref="UnaryExpression"/>: <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>)
    /// always lowers to <c>operand.unwrap()</c>; every other unary lowers to its wired memberRoutine
    /// call (or passes through when non-wired).
    /// </summary>
    protected override Expression VisitUnary(UnaryExpression e)
    {
        // ForceUnwrap (!!) is always lowered to an unwrap member-routine call, never left as UnaryExpression.
        // This runs for both user code (where ExpressionLoweringPass has already run but no
        // longer handles ForceUnwrap) and stdlib bodies (which bypass ExpressionLoweringPass).
        // ResolvedType may be null for stdlib bodies; codegen infers the return type from
        // the unwrap member-routine definition.
        if (e.Operator == UnaryOperator.ForceUnwrap)
        {
            return LowerForceUnwrap(forceUnwrap: e);
        }

        // Other unary operators (Not, Steal) have no wired member routine and stay as UnaryExpression.
        return LowerUnaryExpression(expr: e, unary: e);
    }

    /// <summary>
    /// Rewrites a <see cref="ConditionalExpression"/> by lowering ONLY its condition — the
    /// true/false branches are left untouched (this is a subexpression form; the top-level
    /// conditional lives elsewhere). Narrower than the base hook, which lowers all three parts.
    /// </summary>
    protected override Expression VisitConditional(ConditionalExpression e)
    {
        Expression condExpr = VisitExpression(expr: e.Condition);
        return ReferenceEquals(objA: condExpr, objB: e.Condition)
            ? e
            : e with { Condition = condExpr };
    }

    /// <summary>
    /// Lowers the INTERIOR of an assignment target while preserving the outermost
    /// node type so <c>EmitBinaryAssign</c> can still dispatch on it:
    /// <list type="bullet">
    ///   <item><c>MemberExpression(obj, prop)</c> -> lower <c>obj</c>; keep outer MemberExpression.</item>
    ///   <item><c>IndexExpression(coll, idx)</c>  -> lower <c>coll</c> and <c>idx</c>; keep outer IndexExpression.</item>
    ///   <item><c>IdentifierExpression</c>         -> unchanged.</item>
    /// </list>
    /// This is needed because assignment targets like <c>node!!.field = v</c> or
    /// <c>coll[expr!!] = v</c> have <c>!!</c> (ForceUnwrap) nested inside the target,
    /// which must be lowered to <c>unwrap()</c> even though the outer shape must remain.
    /// </summary>
    private Expression LowerAssignTarget(Expression target)
    {
        switch (target)
        {
            case MemberExpression mem:
            {
                Expression obj = VisitExpression(expr: mem.Object);
                return ReferenceEquals(objA: obj, objB: mem.Object)
                    ? target
                    : mem with { Object = obj };
            }
            case IndexExpression idx:
            {
                Expression obj = VisitExpression(expr: idx.Object);
                Expression index = VisitExpression(expr: idx.Index);

                // STAGE-1 (pull/(B)): PURELY STRUCTURAL — recurse into the interior only, keep the outer
                // IndexExpression shape so codegen's EmitAssignment still dispatches to setitem. The eager
                // setitem resolution (LookupMemberRoutine + memberRoutine-generic monomorphization) that used
                // to live here is REMOVED; resolution is Stage-2's job (resolve-as-you-collect), so this pass
                // reads no `ResolvedType` and makes no registry lookup.
                if (ReferenceEquals(objA: obj, objB: idx.Object) &&
                    ReferenceEquals(objA: index, objB: idx.Index))
                {
                    return target;
                }

                return idx with { Object = obj, Index = index };
            }
            default:
                return target;
        }
    }

    /// <summary>
    /// Lowers a <see cref="ChainedComparisonExpression"/> (<c>a &lt; b &lt; c</c>) to an AND-chain of
    /// pairwise comparisons, then recurses so each pairwise <see cref="BinaryExpression"/> is lowered.
    /// Extracted from the expression-lowering dispatch.
    /// </summary>
    private Expression LowerChainedComparison(ChainedComparisonExpression chain)
    {
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");

        // Lower all operands
        var operands = new List<Expression>(capacity: chain.Operands.Count);
        foreach (Expression operand in chain.Operands)
        {
            operands.Add(item: VisitExpression(expr: operand));
        }

        // Build pairwise comparisons
        Expression result = new BinaryExpression(Left: operands[index: 0],
            Operator: chain.Operators[index: 0],
            Right: operands[index: 1],
            Location: chain.Location) { ResolvedType = boolType };

        for (int i = 1; i < chain.Operators.Count; i++)
        {
            Expression pairCmp = new BinaryExpression(Left: operands[index: i],
                Operator: chain.Operators[index: i],
                Right: operands[index: i + 1],
                Location: chain.Location) { ResolvedType = boolType };

            result = new BinaryExpression(Left: result,
                Operator: BinaryOperator.And,
                Right: pairCmp,
                Location: chain.Location) { ResolvedType = boolType };
        }

        // Recurse so pairwise BinaryExpression nodes created above are also lowered.
        return VisitExpression(expr: result);
    }

    /// <summary>
    /// Lowers a <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>) to <c>operand.unwrap()</c>.
    /// Extracted from the expression-lowering dispatch.
    /// </summary>
    private CallExpression LowerForceUnwrap(UnaryExpression forceUnwrap)
    {
        Expression operand = VisitExpression(expr: forceUnwrap.Operand);
        TypeInfo? operandType = operand.ResolvedType;
        RoutineInfo? unwrapMemberRoutine = operandType != null
            ? ctx.Registry.LookupMemberRoutine(type: operandType, memberRoutineName: "unwrap")
            : null;
        CallLoweringKind unwrapKind =
            ClassifyCallLoweringKind(routine: unwrapMemberRoutine, receiverType: operandType);
        return new CallExpression(
            Callee: new MemberExpression(Object: operand,
                MemberName: "unwrap",
                Location: forceUnwrap.Location),
            Arguments: [],
            Location: forceUnwrap.Location)
        {
            ResolvedType = forceUnwrap.ResolvedType,
            ResolvedRoutine = unwrapMemberRoutine,
            LoweringKind = unwrapKind
        };
    }

    /// <summary>
    /// Lowers a general <see cref="UnaryExpression"/> (a wired memberRoutine such as <c>-</c>/<c>~</c>)
    /// to <c>operand.MemberRoutine()</c>. A non-wired operator or a flags <c>bitnot</c> passes through
    /// with only its operand lowered. Extracted from the expression-lowering dispatch.
    /// </summary>
    private Expression LowerUnaryExpression(Expression expr, UnaryExpression unary)
    {
        string? memberRoutineName = unary.Operator.GetMemberRoutineName();
        Expression operand = VisitExpression(expr: unary.Operand);

        if (memberRoutineName == null)
        {
            return ReferenceEquals(objA: operand, objB: unary.Operand)
                ? expr
                : unary with { Operand = operand };
        }

        TypeInfo? operandType = operand.ResolvedType;

        // Flags types have no bitnot memberRoutine body -> codegen handles it via EmitBitwiseNot.
        // Skip memberRoutine-call lowering so the UnaryExpression passes through unchanged.
        if (operandType is FlagsTypeInfo && memberRoutineName == "bitnot")
        {
            return ReferenceEquals(objA: operand, objB: unary.Operand)
                ? expr
                : unary with { Operand = operand };
        }

        RoutineInfo? resolvedUnaryMemberRoutine = null;
        if (operandType != null)
        {
            resolvedUnaryMemberRoutine =
                ctx.Registry.LookupMemberRoutineOverload(type: operandType,
                    memberRoutineName: memberRoutineName,
                    argTypes: []);
            resolvedUnaryMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: operandType,
                memberRoutineName: memberRoutineName);
        }

        // Always lower to a memberRoutine call -> even when memberRoutine isn't resolved
        // (e.g., stdlib bodies with no ResolvedType on operands).
        // Failability is structural on the callee — no `!` in the name.
        bool unaryFailable = resolvedUnaryMemberRoutine?.IsFailable ?? false;

        var unaryCallee = new MemberExpression(
            Object: operand,
            MemberName: memberRoutineName,
            Location: unary.Location) { IsFailable = unaryFailable };

        CallLoweringKind unaryKind = ClassifyCallLoweringKind(routine: resolvedUnaryMemberRoutine,
            receiverType: operandType);

        return new CallExpression(Callee: unaryCallee, Arguments: [], Location: unary.Location)
        {
            ResolvedType = unary.ResolvedType,
            ResolvedRoutine = resolvedUnaryMemberRoutine,
            LoweringKind = unaryKind
        };
    }

    /// <summary>
    /// Lowers the parser-quirk <c>obj.field[i]</c> form (a <see cref="GenericMemberExpression"/> whose
    /// type-arguments are actually index expressions) into a <see cref="MemberExpression"/> wrapped in
    /// an <see cref="IndexExpression"/>, then recurses so the index becomes a <c>getitem!</c> call.
    /// Extracted from the expression-lowering dispatch.
    /// </summary>
    private Expression LowerGenericMemberIndex(GenericMemberExpression gme)
    {
        Expression loweredObj = VisitExpression(expr: gme.Object);
        var memberExpr = new MemberExpression(
            Object: loweredObj,
            MemberName: gme.MemberName,
            Location: gme.Location) { ResolvedType = gme.ResolvedType };

        // Use first type-arg name as identifier (the index variable).
        var idxExpr =
            new IdentifierExpression(Name: gme.TypeArguments[index: 0].Name,
                Location: gme.TypeArguments[index: 0].Location)
            {
                ResolvedType = gme.TypeArguments[index: 0].ResolvedType
            };

        var indexExpr =
            new IndexExpression(Object: memberExpr, Index: idxExpr, Location: gme.Location)
            {
                ResolvedType = gme.ResolvedType
            };

        // Recurse -> IndexExpression case above converts to getitem! call.
        return VisitExpression(expr: indexExpr);
    }

    /// <summary>
    /// Lowers an <see cref="IndexExpression"/> (<c>obj[i]</c>) to <c>obj.getitem!(i)</c> — resolving
    /// the overload, desugaring any back-index (<c>^n</c>) bounds, and wrapping the result in the
    /// element type's <c>store</c> when reading out an owned element. A typewise type-receiver
    /// (<c>Type[T].member()</c>) collapses to a bare typed identifier instead.
    /// </summary>
    private Expression LowerIndexExpression(IndexExpression idx)
    {
        // Typewise type-receiver: IndexExpression(Ident("NumericSumAdd"), Ident("T")) — SA has
        // already resolved this to a generic resolution type. Collapse to a bare typed identifier
        // so MemberExpression codegen sees a typewise receiver.
        if (TryLowerTypewiseReceiver(idx: idx, result: out Expression typewiseIdent))
        {
            return typewiseIdent;
        }

        Expression loweredObj = VisitExpression(expr: idx.Object);
        Expression loweredIdx = VisitExpression(expr: idx.Index);
        TypeInfo? targetType = idx.Object.ResolvedType;

        // Desugar end-relative back-index bounds on range slices and scalar subscripts.
        loweredIdx = LowerBackIndexBounds(loweredObj: loweredObj,
            loweredIdx: loweredIdx,
            targetType: targetType,
            location: idx.Location);

        RoutineInfo? resolvedGetItem = null;
        if (targetType != null)
        {
            TypeInfo? indexType = loweredIdx.ResolvedType ?? idx.Index.ResolvedType;
            resolvedGetItem = ResolveGetItemRoutine(targetType: targetType, indexType: indexType);
        }

        CallLoweringKind getitemKind =
            ClassifyCallLoweringKind(routine: resolvedGetItem, receiverType: targetType);
        var member = new MemberExpression(Object: loweredObj,
            MemberName: GetItemMemberRoutine,
            Location: idx.Location);
        var getitemCall =
            new CallExpression(Callee: member, Arguments: [loweredIdx], Location: idx.Location)
            {
                ResolvedRoutine = resolvedGetItem,
                ResolvedType = idx.ResolvedType,
                LoweringKind = getitemKind
            };
        return WrapGetItemWithStore(getitemCall: getitemCall, idx: idx);
    }

    /// <summary>
    /// Returns true when <paramref name="idx"/> is a typewise type-receiver of the form
    /// <c>Ident[T]</c> resolved to a generic resolution — and sets <paramref name="result"/> to
    /// the collapsed bare <see cref="IdentifierExpression"/> carrying the resolved type. Returns
    /// false for all other index expressions.
    /// </summary>
    private static bool TryLowerTypewiseReceiver(IndexExpression idx, out Expression result)
    {
        if (idx is
            {
                Object: IdentifierExpression typeObjId,
                ResolvedType: { IsGenericResolution: true } resolvedTy
            } && (resolvedTy.Name == typeObjId.Name ||
                  GetGenericDefName(t: resolvedTy) == typeObjId.Name))
        {
            result = new IdentifierExpression(Name: typeObjId.Name, Location: idx.Location)
            {
                ResolvedType = resolvedTy
            };
            return true;
        }

        result = idx;
        return false;
    }

    /// <summary>
    /// Returns the name of the generic definition for a resolved generic type, or null for non-generic types.
    /// Used to match typewise receivers of the form <c>GenericType[T]</c>.
    /// </summary>
    private static string? GetGenericDefName(TypeInfo? t)
    {
        return t switch
        {
            RecordTypeInfo { GenericDefinition: { } d } => d.Name,
            EntityTypeInfo { GenericDefinition: { } d } => d.Name,
            ProtocolTypeInfo { GenericDefinition: { } d } => d.Name,
            _ => null
        };
    }

    /// <summary>
    /// Desugars back-index (<c>^n</c>) bounds in <paramref name="loweredIdx"/>: rewrites each
    /// <see cref="BackIndexExpression"/> marker on a range slice's start/end members, or a scalar
    /// subscript, to a forward U64 position via <c>back_resolve</c>. Returns the (possibly rewritten)
    /// index expression unchanged when no back-index is present.
    /// </summary>
    private Expression LowerBackIndexBounds(Expression loweredObj, Expression loweredIdx,
        TypeInfo? targetType, SourceLocation location)
    {
        // End-relative SLICE bounds: `xs[a til ^0]`. By this pass the range index is already a
        // Range[U64] CreatorExpression (ExpressionLoweringPass ran first) whose start/end may
        // still be a BackIndexExpression marker. Rewrite each such bound to the forward position
        // via back_resolve. ONLY the start/end bounds carry a back-index; the step/inclusive
        // members must be left untouched (rewriting a numeric step would corrupt the stride).
        if (targetType != null &&
            loweredIdx is CreatorExpression { TypeName: "Range" } rangeCtor &&
            rangeCtor.MemberVariables.Any(predicate: mv =>
                mv.Name is "start" or "end" && mv.Value is BackIndexExpression))
        {
            var rewrittenMembers = rangeCtor.MemberVariables
                                            .Select(selector: mv =>
                                                 mv.Name is "start" or "end" &&
                                                 mv.Value is BackIndexExpression backBound
                                                     ? (mv.Name,
                                                         (Expression)BuildBackIndexResolve(
                                                             loweredObj: loweredObj,
                                                             backIndex: backBound,
                                                             targetType: targetType,
                                                             location: mv.Value.Location))
                                                     : mv)
                                            .ToList();
            return rangeCtor with { MemberVariables = rewrittenMembers };
        }

        // Scalar back-index: `coll[^n]` — rewrite the single BackIndexExpression to a forward U64.
        // The object is referenced twice — acceptable for the common `var[^n]` case; a
        // side-effecting receiver would evaluate twice.
        if (targetType != null && loweredIdx is BackIndexExpression backIdx)
        {
            return BuildBackIndexResolve(loweredObj: loweredObj,
                backIndex: backIdx,
                targetType: targetType,
                location: location);
        }

        return loweredIdx;
    }

    /// <summary>
    /// Wraps a <c>getitem!</c> call in the element type's <c>store</c> member routine when the
    /// element type owns its data (Text, Integer, variant wrappers). A trivially-copyable or
    /// entity element passes through unwrapped. Extracted from <see cref="LowerIndexExpression"/>.
    /// </summary>
    private CallExpression WrapGetItemWithStore(CallExpression getitemCall, IndexExpression idx)
    {
        // `a[i]` reads an element the container still owns. Apply the element type's store
        // (a retaining copy for Text/Integer/variant) so the read no longer aliases the
        // buffer's live element and avoids a double-free on teardown. A trivially-copyable
        // element has no retaining store (GetLifecycle.Store == null) and is left bare.
        // A bare entity element likewise has no store — reading one out to keep it is rejected
        // at SA (single-owner), so it never needs a copy here.
        TypeInfo? elemType = idx.ResolvedType;
        RoutineInfo? elemStore = elemType != null
            ? ctx.Registry.GetLifecycle(type: elemType)
                 .Store
            : null;
        if (elemStore == null)
        {
            return getitemCall;
        }

        var storeCallee = new MemberExpression(
            Object: getitemCall,
            MemberName: elemStore.Name,
            Location: idx.Location) { ResolvedType = elemType };
        return new CallExpression(Callee: storeCallee, Arguments: [], Location: idx.Location)
        {
            ResolvedRoutine = elemStore,
            ResolvedType = elemType,
            LoweringKind = ClassifyMemberRoutine(memberRoutine: elemStore)
        };
    }

    /// <summary>
    /// Resolves the <c>getitem</c> overload for a subscript on <paramref name="targetType"/> by the
    /// (forward U64) index type — trying the exact overload, the bare lookup, the UNWRAPPED inner
    /// container of a Roamed wrapper, and finally memberRoutine-level generic monomorphization.
    /// Extracted from <see cref="LowerIndexExpression"/>.
    /// </summary>
    private RoutineInfo? ResolveGetItemRoutine(TypeInfo targetType, TypeInfo? indexType)
    {
        // Pick the `getitem` overload by the (now forward U64) index argument type.
        RoutineInfo? resolvedGetItem =
            ResolveGetItemOn(targetType: targetType, indexType: indexType);
        // Suflae container locals are `Roamed[Dict]`/`Roamed[List]` post-SA; the wrapper
        // has no `getitem`, so resolve against the UNWRAPPED inner container (exactly as the
        // membership/comparison branch does for `x in d`). RoamedProjectionLoweringPass then
        // projects the Roamed receiver to the inner value via `raw_inner()`.
        if (resolvedGetItem == null && UnwrapRoamedInner(type: targetType) is { } innerTarget)
        {
            resolvedGetItem = ResolveGetItemOn(targetType: innerTarget, indexType: indexType);
        }

        if (resolvedGetItem != null && indexType != null)
        {
            resolvedGetItem = ResolveMemberRoutineGenericRoutine(routine: resolvedGetItem,
                argTypes: [indexType]);
        }

        return resolvedGetItem;
    }

    /// <summary>
    /// Resolves the <c>getitem</c> overload on one owner by index arg type. A SCALAR subscript's
    /// accessor is <c>getitem(index: U64)</c> (the index is coerced to U64), so when the raw index type
    /// doesn't exact-match an overload — e.g. an <c>S64</c> index — retry with <c>U64</c> to pin the
    /// scalar accessor. Only falls back to a name-only lookup for a genuinely single-overload container
    /// (a name-only lookup returns null once a <c>getitem(range:)</c> sibling makes the name ambiguous —
    /// no first-wins).
    /// </summary>
    private RoutineInfo? ResolveGetItemOn(TypeInfo targetType, TypeInfo? indexType)
    {
        if (indexType != null)
        {
            RoutineInfo? byIndex = ctx.Registry.LookupMemberRoutineOverload(type: targetType,
                memberRoutineName: GetItemMemberRoutine,
                argTypes: [indexType]);
            if (byIndex != null)
            {
                return byIndex;
            }

            if (ctx.Registry.LookupType(name: "U64") is { } u64 &&
                ctx.Registry.LookupMemberRoutineOverload(type: targetType,
                    memberRoutineName: GetItemMemberRoutine,
                    argTypes: [u64]) is { } byU64)
            {
                return byU64;
            }
        }

        return ctx.Registry.LookupMemberRoutine(type: targetType,
            memberRoutineName: GetItemMemberRoutine);
    }

    /// <summary>
    /// Lowers a <see cref="BinaryExpression"/>: a non-overloadable operator (memberRoutineName == null)
    /// stays a <see cref="BinaryExpression"/> (only its operands / assignment interior lowered); an
    /// overloadable operator becomes <c>receiver.MemberRoutine(you: arg)</c> with the resolved overload.
    /// </summary>
    private Expression LowerBinaryExpression(Expression expr, BinaryExpression bin)
    {
        string? memberRoutineName = bin.Operator.GetMemberRoutineName();
        if (memberRoutineName == null)
        {
            return LowerNonOverloadableBinary(expr: expr, bin: bin);
        }

        Expression left = VisitExpression(expr: bin.Left);
        Expression right = VisitExpression(expr: bin.Right);

        // Membership operators reverse receiver/argument: x in coll -> coll.contains(x)
        bool isReversed = bin.Operator is BinaryOperator.In or BinaryOperator.NotIn;
        Expression receiver = isReversed
            ? right
            : left;
        Expression argument = isReversed
            ? left
            : right;

        TypeInfo? receiverType = receiver.ResolvedType;
        TypeInfo? argType = argument.ResolvedType;
        RoutineInfo? resolvedMemberRoutine = ResolveBinaryOperatorRoutine(
            memberRoutineName: memberRoutineName,
            receiverType: receiverType,
            argType: argType);

        // Mixed fixed-width integer comparisons have no direct cross-width overloads in the
        // stdlib. Normalize both sides to a common width here so we lower to a concrete
        // same-type comparison instead of letting codegen fall back to an arbitrary overload.
        TryNormalizeMixedIntegerComparison(bin: bin,
            memberRoutineName: memberRoutineName,
            receiverType: ref receiverType,
            argType: ref argType,
            receiver: ref receiver,
            argument: ref argument,
            resolvedMemberRoutine: ref resolvedMemberRoutine);

        // Shift operators require the shift-amount operand to match the parameter's exact width.
        argument = TryNarrowShiftOperand(bin: bin,
            resolvedMemberRoutine: resolvedMemberRoutine,
            argType: argType,
            argument: argument);

        // Always lower to a memberRoutine call — even when the memberRoutine is not in the registry
        // (e.g. stdlib bodies where ResolvedType is null). When ResolvedRoutine is null, codegen's
        // EmitMemberRoutineCall resolves at emission time and retries the failable form as needed.
        // Failability is structural on the callee (no '!' in the name).
        bool binFailable = resolvedMemberRoutine?.IsFailable ?? false;
        string paramName = resolvedMemberRoutine?.Parameters.Count > 0
            ? resolvedMemberRoutine.Parameters[index: 0].Name
            : "you";

        var binCallee = new MemberExpression(
            Object: receiver,
            MemberName: memberRoutineName,
            Location: bin.Location) { IsFailable = binFailable };

        CallLoweringKind lk = ClassifyCallLoweringKind(routine: resolvedMemberRoutine,
            receiverType: receiverType);

        return new CallExpression(Callee: binCallee,
            Arguments:
            [
                new NamedArgumentExpression(Name: paramName,
                    Value: argument,
                    Location: bin.Location)
            ],
            Location: bin.Location)
        {
            ResolvedType = bin.ResolvedType,
            ResolvedRoutine = resolvedMemberRoutine,
            LoweringKind = lk
        };
    }

    /// <summary>
    /// Lowers a non-overloadable <see cref="BinaryExpression"/> (one whose operator has no member-routine
    /// name): for <see cref="BinaryOperator.Assign"/>, lowers the RHS and the interior of the LHS
    /// (preserving the outer LHS shape so codegen can dispatch setitem); for all others, lowers both
    /// operands normally. Extracted from <see cref="LowerBinaryExpression"/>.
    /// </summary>
    private Expression LowerNonOverloadableBinary(Expression expr, BinaryExpression bin)
    {
        if (bin.Operator == BinaryOperator.Assign)
        {
            // For assignment: lower the RHS and the INTERIOR of the LHS. The outermost LHS node
            // must stay as-is so EmitBinaryAssign can dispatch on its type (MemberExpression →
            // field write, IndexExpression → setitem!). Lowering the entire LHS would convert an
            // IndexExpression to a getitem! call, breaking setitem dispatch.
            Expression rhs = VisitExpression(expr: bin.Right);
            Expression lhs = LowerAssignTarget(target: bin.Left);
            return ReferenceEquals(objA: rhs, objB: bin.Right) &&
                   ReferenceEquals(objA: lhs, objB: bin.Left)
                ? expr
                : bin with { Left = lhs, Right = rhs };
        }

        Expression left0 = VisitExpression(expr: bin.Left);
        Expression right0 = VisitExpression(expr: bin.Right);
        return ReferenceEquals(objA: left0, objB: bin.Left) &&
               ReferenceEquals(objA: right0, objB: bin.Right)
            ? expr
            : bin with { Left = left0, Right = right0 };
    }

    /// <summary>
    /// When a comparison operator is unresolved and both operands are fixed-width integers of
    /// different widths, normalizes them to a common type. Updates <paramref name="resolvedMemberRoutine"/>,
    /// <paramref name="receiver"/>, <paramref name="argument"/>, <paramref name="receiverType"/>, and
    /// <paramref name="argType"/> in place. No-op when the conditions do not apply.
    /// </summary>
    private void TryNormalizeMixedIntegerComparison(BinaryExpression bin, string memberRoutineName,
        ref TypeInfo? receiverType, ref TypeInfo? argType, ref Expression receiver,
        ref Expression argument, ref RoutineInfo? resolvedMemberRoutine)
    {
        if (resolvedMemberRoutine != null || receiverType == null || argType == null)
        {
            return;
        }

        if (bin.Operator is not (BinaryOperator.Equal or BinaryOperator.NotEqual
            or BinaryOperator.Less or BinaryOperator.LessEqual or BinaryOperator.Greater
            or BinaryOperator.GreaterEqual))
        {
            return;
        }

        if (!TryResolveCommonIntegerComparisonType(left: receiverType,
                right: argType,
                commonType: out TypeInfo? commonType))
        {
            return;
        }

        resolvedMemberRoutine = NormalizeToCommonIntegerComparison(
            memberRoutineName: memberRoutineName,
            commonType: commonType!,
            receiver: ref receiver,
            argument: ref argument);
        receiverType = commonType;
        argType = commonType;
    }

    /// <summary>
    /// When a shift operator's argument type differs from the expected parameter type, wraps the
    /// argument in a numeric conversion to match the parameter. Returns the (possibly wrapped)
    /// argument. No-op when the operator is not a shift or the types already match.
    /// </summary>
    private static Expression TryNarrowShiftOperand(BinaryExpression bin,
        RoutineInfo? resolvedMemberRoutine, TypeInfo? argType, Expression argument)
    {
        if (resolvedMemberRoutine is not { Parameters.Count: > 0 })
        {
            return argument;
        }

        if (bin.Operator is not (BinaryOperator.ArithmeticLeftShift
            or BinaryOperator.ArithmeticRightShift or BinaryOperator.LogicalLeftShift
            or BinaryOperator.LogicalRightShift))
        {
            return argument;
        }

        TypeInfo paramType = resolvedMemberRoutine.Parameters[index: 0].Type;
        if (argType != null && argType.FullName != paramType.FullName &&
            TryGetFixedWidthIntegerInfo(type: argType, signed: out _, width: out _) &&
            TryGetFixedWidthIntegerInfo(type: paramType, signed: out _, width: out _))
        {
            return WrapNumericOperand(expr: argument, targetType: paramType);
        }

        return argument;
    }

    /// <summary>
    /// Normalizes both operands of a mixed-width integer comparison to <paramref name="commonType"/>
    /// (wrapping each) and resolves the comparison overload on that common type. Extracted from
    /// <see cref="LowerBinaryExpression"/>.
    /// </summary>
    private RoutineInfo? NormalizeToCommonIntegerComparison(string memberRoutineName,
        TypeInfo commonType, ref Expression receiver, ref Expression argument)
    {
        receiver = WrapNumericOperand(expr: receiver, targetType: commonType);
        argument = WrapNumericOperand(expr: argument, targetType: commonType);
        return ctx.Registry.LookupMemberRoutineOverload(type: commonType,
            memberRoutineName: memberRoutineName,
            argTypes: [commonType]) ?? ctx.Registry.LookupMemberRoutine(type: commonType,
            memberRoutineName: memberRoutineName);
    }

    /// <summary>
    /// Resolves the memberRoutine implementing a binary operator on <paramref name="receiverType"/>:
    /// tries the exact overload by argument type, then the bare lookup, then the failable form, and
    /// finally the UNWRAPPED inner type of a Roamed container wrapper. Returns null when unresolved.
    /// </summary>
    private RoutineInfo? ResolveBinaryOperatorRoutine(string memberRoutineName,
        TypeInfo? receiverType, TypeInfo? argType)
    {
        if (receiverType == null)
        {
            return null;
        }

        RoutineInfo? resolvedMemberRoutine = argType != null
            ? ctx.Registry.LookupMemberRoutineOverload(type: receiverType,
                memberRoutineName: memberRoutineName,
                argTypes: [argType])
            : ctx.Registry.LookupMemberRoutine(type: receiverType,
                memberRoutineName: memberRoutineName);
        resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: memberRoutineName);
        // If the non-failable form doesn't exist, try the failable form (sub -> sub!).
        // Types like U64 only define sub! (underflow would be undefined behavior).
        // The name is BARE; failability is structural — retry with isFailable: true.
        if (resolvedMemberRoutine == null)
        {
            resolvedMemberRoutine = argType != null
                ? ctx.Registry.LookupMemberRoutineOverload(type: receiverType,
                    memberRoutineName: memberRoutineName,
                    argTypes: [argType])
                : null;
            resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: receiverType,
                memberRoutineName: memberRoutineName,
                isFailable: true);
        }

        // Suflae wraps container locals as `Roamed[Dict]` / `Roamed[Set]` post-SA. A
        // membership/comparison operator lowers to `receiver.contains(x)` / `.eq(x)` HERE in
        // Phase 8 — after the wrapper-forwarder pass has frozen — so the memberRoutine is unresolved
        // on the wrapper. Resolve it against the UNWRAPPED inner type (exactly as SA did for
        // an explicit `d.count()` while `d` was still the bare container before promotion) and
        // stamp the inner memberRoutine; codegen projects the Roamed receiver to the inner value for
        // `me`, same as every other inner-memberRoutine call on a Roamed container.
        // The Roamed handle reaches here in EITHER representation the pipeline produces: a
        // WrapperTypeInfo (SuflaeEntityLoweringPass.WrapInRoam) or a RecordTypeInfo (resolver-
        // built). Extract the inner container type from whichever it is.
        TypeInfo? innerRecv = receiverType switch
        {
            WrapperTypeInfo w when Declaration.TypeRegistry.GetRcWrapperBaseName(type: w) != null
                => w.InnerType,
            RecordTypeInfo r when Declaration.TypeRegistry.GetRcWrapperBaseName(type: r) != null &&
                                  r.TypeArguments is { Count: >= 1 } ra => ra[index: 0],
            _ => null
        };
        if (resolvedMemberRoutine == null && innerRecv != null)
        {
            resolvedMemberRoutine = argType != null
                ? ctx.Registry.LookupMemberRoutineOverload(type: innerRecv,
                    memberRoutineName: memberRoutineName,
                    argTypes: [argType])
                : null;
            resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: innerRecv,
                memberRoutineName: memberRoutineName);
            resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: innerRecv,
                memberRoutineName: memberRoutineName,
                isFailable: true);
            // NOTE: the Roamed receiver is NOT projected here. Resolving the inner memberRoutine is
            // enough — codegen's unified receiver projection (EmitMemberRoutineCall) wraps the
            // Roamed handle in `raw_inner()` for every bare-`me` inner memberRoutine uniformly, so the
            // operator-lowered call, an index `d[i]`, and an explicit `d.count()` all funnel
            // through the one projection site.
        }

        return resolvedMemberRoutine;
    }

    /// <summary>
    /// Lowers operator expressions in all synthesized bodies stored in <see cref="PostprocessingContext.VariantBodies"/>.
    /// Called once from <see cref="DesugaringPipeline.RunGlobal"/> after <c>WiredRoutinePass</c> has
    /// populated <c>VariantBodies</c>.
    /// </summary>
    public void RunOnVariantBodies()
    {
        BodyDispatch.RunOnVariantBodies(bodies: ctx.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    /// <summary>
    /// Lowers operator expressions in instantiated generic routine bodies.
    /// Phase 7's <c>GenericMonomorphizationPass</c> populates <c>InstantiatedGenericBodies</c>
    /// AFTER the Phase 8 RunGlobal sweep has finished, so those bodies miss the regular
    /// per-program operator-lowering pass. Without this memberRoutine, `me.size = me.size + 1_u64`
    /// inside a monomorphized routine reaches codegen as a bare <c>BinaryExpression(Add)</c>
    /// and trips the "must be lowered to a wired call" guard.
    /// Caller passes the map directly (PostprocessingContext doesn't hold it).
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(bodies: instantiatedGenericBodies,
            lower: (_, entry) => VisitStatement(stmt: entry.Ast.Body));
    }

    private RoutineInfo ResolveMemberRoutineGenericRoutine(RoutineInfo routine,
        List<TypeInfo> argTypes)
    {
        if (!routine.IsGenericDefinition || routine.GenericParameters == null)
        {
            return routine;
        }

        if (argTypes.Any(predicate: static t => t is ErrorTypeInfo or GenericParameterTypeInfo))
        {
            return routine;
        }

        var inferred = new TypeInfo?[routine.GenericParameters.Count];
        // Skip the implicit `me` receiver parameter — argTypes only contains the explicit
        // call-site arguments (index type, value type, etc.), so we must align them
        // against the non-me parameters to correctly infer memberRoutine-level generics like I.
        int argIdx = 0;
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Name == "me")
            {
                continue;
            }

            if (argIdx >= argTypes.Count)
            {
                break;
            }

            InferMemberRoutineGenericArguments(paramType: param.Type,
                argType: argTypes[index: argIdx],
                genericParameters: routine.GenericParameters,
                inferred: inferred);
            argIdx++;
        }

        if (inferred.Any(predicate: t => t is null or ErrorTypeInfo or GenericParameterTypeInfo))
        {
            return routine;
        }

        return ctx.Registry.GetOrCreateRoutineResolution(genericDef: routine,
            typeArguments: inferred.Select(selector: t => t!)
                                   .ToList());
    }

    private static void InferMemberRoutineGenericArguments(TypeInfo paramType, TypeInfo argType,
        List<string> genericParameters, TypeInfo?[] inferred)
    {
        if (paramType is GenericParameterTypeInfo)
        {
            int idx = genericParameters.ToList()
                                       .IndexOf(item: paramType.Name);
            if (idx >= 0 && inferred[idx] == null)
            {
                inferred[idx] = argType;
            }

            return;
        }

        if (paramType is { TypeArguments: { Count: > 0 } paramArgs } &&
            argType is { TypeArguments: { Count: > 0 } argArgs } &&
            paramArgs.Count == argArgs.Count)
        {
            for (int i = 0; i < paramArgs.Count; i++)
            {
                InferMemberRoutineGenericArguments(paramType: paramArgs[index: i],
                    argType: argArgs[index: i],
                    genericParameters: genericParameters,
                    inferred: inferred);
            }
        }
    }

    private static Expression WrapNumericOperand(Expression expr, TypeInfo targetType)
    {
        if (expr.ResolvedType?.FullName == targetType.FullName)
        {
            return expr;
        }

        return new CreatorExpression(TypeName: targetType.Name,
            TypeArguments: null,
            MemberVariables:
            [
                ("from", expr)
            ],
            Location: expr.Location) { ResolvedType = targetType, ConstructedType = targetType };
    }

    private static CallLoweringKind ClassifyMemberRoutine(RoutineInfo memberRoutine)
    {
        if (memberRoutine.LlvmIrTemplate != null)
        {
            return CallLoweringKind.LlvmIntrinsic;
        }

        return CallLoweringKind.DirectMemberRoutine;
    }

    /// <summary>
    /// Returns the <see cref="CallLoweringKind"/> for a call site: classifies via the resolved
    /// routine when available; falls back to <see cref="CallLoweringKind.DirectMemberRoutine"/> when
    /// the receiver type is known but the routine is unresolved; otherwise
    /// <see cref="CallLoweringKind.Unknown"/>.
    /// </summary>
    private static CallLoweringKind ClassifyCallLoweringKind(RoutineInfo? routine,
        TypeInfo? receiverType)
    {
        if (routine != null)
        {
            return ClassifyMemberRoutine(memberRoutine: routine);
        }

        return receiverType != null
            ? CallLoweringKind.DirectMemberRoutine
            : CallLoweringKind.Unknown;
    }

    /// <summary>
    /// Builds the forward-index expression for a back-index subscript: given a receiver and a `^n`
    /// <c>BackIndexExpression</c> marker, produces <c>back_resolve(count: receiver.count(), offset: n)</c>
    /// — a resolved <c>U64</c> position. `^` carries no runtime type; this call-site desugaring is what
    /// lets collections declare only the <c>getitem!(index: U64)</c> form. The free routine
    /// <c>back_resolve</c> throws <c>IndexOutOfBoundsError</c> on out-of-range.
    /// </summary>
    // The inner container type inside a `Roamed[E]` handle (either representation the pipeline
    // produces), or null when the type is not an RC wrapper. Mirrors the membership/comparison branch's
    // inner-type unwrap so the index (`d[i]`) getitem resolves against the bare container.
    private static TypeInfo? UnwrapRoamedInner(TypeInfo? type)
    {
        return type switch
        {
            WrapperTypeInfo w when Declaration.TypeRegistry.GetRcWrapperBaseName(type: w) != null
                => w.InnerType,
            RecordTypeInfo { TypeArguments: { Count: >= 1 } ra } r when Declaration.TypeRegistry
               .GetRcWrapperBaseName(type: r) != null => ra[index: 0],
            _ => null
        };
    }

    private CallExpression BuildBackIndexResolve(Expression loweredObj,
        BackIndexExpression backIndex, TypeInfo targetType, SourceLocation location)
    {
        // Resolve the count member routine on the receiver; its return type is U64.
        RoutineInfo? countRoutine = ctx.Registry.LookupMemberRoutine(type: targetType,
            memberRoutineName: Declaration.RuntimeContract.Collection.Count);
        var countCall = new CallExpression(
            Callee: new MemberExpression(Object: loweredObj,
                MemberName: Declaration.RuntimeContract.Collection.Count,
                Location: location),
            Arguments: [],
            Location: location)
        {
            ResolvedRoutine = countRoutine,
            ResolvedType = countRoutine?.ReturnType,
            LoweringKind = countRoutine != null
                ? ClassifyMemberRoutine(memberRoutine: countRoutine)
                : CallLoweringKind.DirectMemberRoutine
        };

        // back_resolve(count: coll.count(), offset: n) -> U64 (free routine, failable: throws
        // IndexOutOfBoundsError on overshoot). `^` carries no runtime type; the offset is the `^n`
        // operand, already typed U64 by semantic analysis (see AnalyzeBackIndexExpression).
        // NOTE: do NOT filter on `isFailable: true`. `back_resolve` has no `!` marker — its failability
        // is INFERRED from the body's `throw` (RF-S753). Under the demand pipeline this lowering runs on
        // the USER body BEFORE the collector analyzes BackIndex.rf on reach, so at this point back_resolve
        // is registered (signature/return-type resolved) but not yet marked failable — an `isFailable:true`
        // filter would spuriously reject it and the unresolved call would trip RF-S959 at codegen. The name
        // is a unique builtin (no failable/non-failable overload pair), so failability is not a disambiguator.
        RoutineInfo? resolveRoutine =
            ctx.Registry.LookupRoutine(
                fullName: $"Core.{Declaration.RuntimeContract.BackResolve}") ??
            ctx.Registry.LookupRoutine(fullName: Declaration.RuntimeContract.BackResolve);

        // The offset must be a scalar U64. An untyped/signed integer-literal operand (e.g. ^1) is retagged
        // to U64Literal so codegen never treats it as an arbitrary-precision Integer (the Text-backed big-int type).
        // Any other operand (a U64 variable or arbitrary expression) already carries its type and passes through.
        Expression offset = backIndex.Operand is LiteralExpression
        {
            LiteralType: TokenType.UndecidedInteger or TokenType.IntegerLiteral
            or TokenType.S64Literal or TokenType.U64Literal
        } olit
            ? new LiteralExpression(Value: olit.Value,
                LiteralType: TokenType.U64Literal,
                Location: olit.Location) { ResolvedType = countRoutine?.ReturnType }
            : backIndex.Operand;
        return new CallExpression(
            Callee: new IdentifierExpression(Name: Declaration.RuntimeContract.BackResolve,
                Location: location) { ResolvedType = resolveRoutine?.ReturnType },
            Arguments: [countCall, offset],
            Location: location)
        {
            ResolvedRoutine = resolveRoutine,
            ResolvedType = resolveRoutine?.ReturnType,
            LoweringKind = CallLoweringKind.DirectRoutine
        };
    }

    private bool TryResolveCommonIntegerComparisonType(TypeInfo left, TypeInfo right,
        out TypeInfo? commonType)
    {
        commonType = null;

        if (!TryGetFixedWidthIntegerInfo(type: left,
                signed: out bool leftSigned,
                width: out int leftWidth) || !TryGetFixedWidthIntegerInfo(type: right,
                signed: out bool rightSigned,
                width: out int rightWidth))
        {
            return false;
        }

        bool targetSigned;
        int targetWidth;
        if (leftSigned == rightSigned)
        {
            targetSigned = leftSigned;
            targetWidth = Math.Max(val1: leftWidth, val2: rightWidth);
        }
        else if (leftSigned && leftWidth > rightWidth)
        {
            targetSigned = true;
            targetWidth = leftWidth;
        }
        else if (rightSigned && rightWidth > leftWidth)
        {
            targetSigned = true;
            targetWidth = rightWidth;
        }
        else
        {
            targetSigned = true;
            targetWidth =
                NextSignedWidth(minExclusive: Math.Max(val1: leftWidth, val2: rightWidth));
            if (targetWidth == 0)
            {
                return false;
            }
        }

        commonType = ctx.Registry.LookupType(name: $"{(targetSigned ? "S" : "U")}{targetWidth}");
        return commonType != null;
    }

    private static bool TryGetFixedWidthIntegerInfo(TypeInfo type, out bool signed, out int width)
    {
        signed = false;
        width = 0;

        if (string.IsNullOrEmpty(value: type.Name) || type.Name.Length < 2)
        {
            return false;
        }

        signed = type.Name[index: 0] == 'S';
        if (!signed && type.Name[index: 0] != 'U')
        {
            return false;
        }

        return int.TryParse(s: type.Name[1..], result: out width);
    }

    private static int NextSignedWidth(int minExclusive)
    {
        return SignedWidths.FirstOrDefault(predicate: c => c > minExclusive);
    }
}
