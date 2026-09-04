using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Desugaring;
using Compiler.Instantiation;
using Compiler.Synthesis;
using Compiler.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

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
    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: r => VisitStatement(r.Body));

    //  Statement lowering

    /// <summary>
    /// Lowers a <see cref="WhenStatement"/>. Unlike the base hook, this also lowers any
    /// <see cref="ExpressionPattern"/> guard (e.g. a <see cref="ChainedComparisonExpression"/>)
    /// in each clause — the base only recurses into the subject and clause bodies.
    /// </summary>
    protected override Statement VisitWhen(WhenStatement w)
    {
        Expression subj = VisitExpression(w.Expression);
        var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
        bool clauseChanged = false;
        foreach (WhenClause c in w.Clauses)
        {
            Statement lBody = VisitStatement(c.Body);
            // Also lower expression patterns (e.g. ChainedComparisonExpression guards)
            Pattern lPattern = c.Pattern is ExpressionPattern ep
                ? ep with { Expression = VisitExpression(ep.Expression) }
                : c.Pattern;
            bool patternChanged = !ReferenceEquals(lPattern, c.Pattern);
            if (!ReferenceEquals(lBody, c.Body) || patternChanged)
            {
                clauses.Add(c with { Body = lBody, Pattern = lPattern });
                clauseChanged = true;
            }
            else
            {
                clauses.Add(c);
            }
        }

        bool changed = !ReferenceEquals(subj, w.Expression) || clauseChanged;
        return changed ? w with { Expression = subj, Clauses = clauses } : w;
    }

    /// <summary>
    /// Lowers an <see cref="AssignmentStatement"/> — ONLY the value, not the target. Unlike the base
    /// hook (which lowers both sides), an indexed-assignment target (<c>arr[i] = val</c>) must stay an
    /// <see cref="IndexExpression"/> so codegen's <c>EmitAssignment</c> can dispatch to <c>setitem!</c>;
    /// lowering the target would convert it to a <c>getitem!</c> call.
    /// </summary>
    protected override Statement VisitAssignment(AssignmentStatement asgn)
    {
        Expression val = VisitExpression(asgn.Value);
        return ReferenceEquals(val, asgn.Value) ? asgn : asgn with { Value = val };
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
                Expression loweredBase = VisitExpression(withExpr.Base);
                var updates =
                    new List<(List<string>? Path, Expression? Index, Expression Value)>(
                        capacity: withExpr.Updates.Count);
                bool changed = !ReferenceEquals(loweredBase, withExpr.Base);
                foreach ((List<string>? path, Expression? index, Expression value) in
                         withExpr.Updates)
                {
                    Expression loweredVal = VisitExpression(value);
                    updates.Add((path, index, loweredVal));
                    if (!ReferenceEquals(loweredVal, value)) changed = true;
                }

                return changed ? withExpr with { Base = loweredBase, Updates = updates } : expr;
            }

            case LambdaExpression lambda:
            {
                Expression loweredBody = VisitExpression(lambda.Body);
                return ReferenceEquals(loweredBody, lambda.Body)
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
    protected override Expression VisitRange(RangeExpression e) => e;

    protected override Expression VisitDictLiteral(DictLiteralExpression e) => e;

    protected override Expression VisitSetLiteral(SetLiteralExpression e) => e;

    protected override Expression VisitOptionalMember(OptionalMemberExpression e) => e;

    protected override Expression VisitTypeConversion(TypeConversionExpression e) => e;

    protected override Expression VisitBackIndex(BackIndexExpression e) => e;

    protected override Expression VisitIsPattern(IsPatternExpression e) => e;

    protected override Expression VisitFlagsTest(FlagsTestExpression e) => e;

    protected override Expression VisitBlockExpression(BlockExpression e) => e;

    // IndexExpression -> obj.getitem!(idx)
    protected override Expression VisitIndex(IndexExpression e) => LowerIndexExpression(idx: e);

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
    protected override Expression VisitGenericMember(GenericMemberExpression gme)
    {
        //  GenericMemberExpression -> member + index ??getitem!
        // Parser quirk: obj.field[i] is parsed as GenericMemberExpression(obj, "field", [i]).
        // TypeArguments are index expressions in disguise; lower to IndexExpression then recurse.
        //
        // Exception: `Ident[T]` (type-with-type-args, e.g. `NumericSumAdd[T].identity_lazy()`)
        // is parsed as GenericMemberExpression(Ident, Ident.Name, [T]) — Object.Name == MemberName.
        // Those are real type args, not indices; lower to a plain MemberExpression so the
        // typewise receiver flows through codegen normally.
        if (gme is { TypeArguments.Count: > 0 } &&
            !(gme.Object is IdentifierExpression idObj && idObj.Name == gme.MemberName))
        {
            return LowerGenericMemberIndex(gme: gme);
        }

        // Typewise receiver `Ident[T]` parsed as GenericMemberExpression(Ident, Ident.Name, [T]).
        // Collapse to a bare IdentifierExpression carrying the resolved type so the outer
        // MemberExpression (e.g. `.identity_lazy()`) sees an identifier with ResolvedType set
        // — that is how codegen detects typewise/common calls.
        if (gme is { TypeArguments.Count: > 0, Object: IdentifierExpression typeIdent } &&
            typeIdent.Name == gme.MemberName)
        {
            return new IdentifierExpression(
                Name: gme.MemberName,
                Location: gme.Location)
            {
                ResolvedType = gme.ResolvedType ?? typeIdent.ResolvedType
            };
        }

        // No type arguments -> plain member access; just lower the object.
        Expression loweredObj = VisitExpression(gme.Object);
        return ReferenceEquals(loweredObj, gme.Object)
            ? gme
            : new MemberExpression(
                Object: loweredObj,
                MemberName: gme.MemberName,
                Location: gme.Location)
            {
                ResolvedType = gme.ResolvedType
            };
    }

    //  ChainedComparisonExpression -> AND-chain of pairwise comparisons
    // e.g. a < b < c ??(a < b) and (b < c)
    // Middle operands may be evaluated twice; acceptable here since chained
    // comparisons in stdlib bodies use trivially pure expressions (identifiers/literals).
    protected override Expression VisitChainedComparison(ChainedComparisonExpression e) =>
        LowerChainedComparison(chain: e);

    //  BinaryExpression -> receiver.MemberRoutine(you: arg)
    // Operators with GetMemberRoutineName() == null (And, Or, Is, Identical, But, ...)
    // are not overloadable and stay as BinaryExpression for codegen.
    protected override Expression VisitBinary(BinaryExpression e) =>
        LowerBinaryExpression(expr: e, bin: e);

    /// <summary>
    /// Rewrites a <see cref="UnaryExpression"/>: <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>)
    /// always lowers to <c>operand.unwrap()</c>; every other unary lowers to its wired memberRoutine
    /// call (or passes through when non-wired).
    /// </summary>
    protected override Expression VisitUnary(UnaryExpression e)
    {
        //  ForceUnwrap (!!) -> operand.unwrap()
        // Always lower to a CallExpression -> never fall back to UnaryExpression.
        // This runs for both user code (where ExpressionLoweringPass has already
        // run but no longer handles ForceUnwrap) and stdlib bodies (which bypass
        // ExpressionLoweringPass).  ResolvedType may be null for stdlib bodies;
        // codegen infers the return type from the unwrap memberRoutine definition.
        if (e.Operator == UnaryOperator.ForceUnwrap)
            return LowerForceUnwrap(forceUnwrap: e);

        //  UnaryExpression -> operand.MemberRoutine()
        // Not, Steal -> no wired memberRoutine, stay as UnaryExpression.
        return LowerUnaryExpression(expr: e, unary: e);
    }

    /// <summary>
    /// Rewrites a <see cref="ConditionalExpression"/> by lowering ONLY its condition — the
    /// true/false branches are left untouched (this is a subexpression form; the top-level
    /// conditional lives elsewhere). Narrower than the base hook, which lowers all three parts.
    /// </summary>
    protected override Expression VisitConditional(ConditionalExpression cond)
    {
        Expression condExpr = VisitExpression(cond.Condition);
        return ReferenceEquals(condExpr, cond.Condition)
            ? cond
            : cond with { Condition = condExpr };
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
                Expression obj = VisitExpression(mem.Object);
                return ReferenceEquals(obj, mem.Object) ? target : mem with { Object = obj };
            }
            case IndexExpression idx:
            {
                Expression obj = VisitExpression(idx.Object);
                Expression index = VisitExpression(idx.Index);

                // Resolve setitem with memberRoutine-level generic monomorphization (parallel to the
                // getitem! lowering path). Non-generic owners with memberRoutine-level generics (e.g.
                // BitList.setitem![I]) need the resolved routine stashed so codegen can dispatch
                // to the monomorphized entry rather than hitting ResolveMemberRoutine's generic-def guard.
                RoutineInfo? resolvedSetItem = null;
                TypeInfo? targetType = obj.ResolvedType ?? idx.Object.ResolvedType;
                if (targetType != null)
                {
                    resolvedSetItem =
                        ctx.Registry.LookupMemberRoutine(type: targetType, memberRoutineName: "setitem");
                    if (resolvedSetItem != null)
                    {
                        var argTypes = new List<TypeInfo>();
                        TypeInfo? indexType = index.ResolvedType ?? idx.Index.ResolvedType;
                        if (indexType != null)
                            argTypes.Add(item: indexType);
                        resolvedSetItem = ResolveMemberRoutineGenericRoutine(
                            routine: resolvedSetItem,
                            argTypes: argTypes);
                    }
                }

                if (ReferenceEquals(obj, idx.Object) && ReferenceEquals(index, idx.Index) &&
                    resolvedSetItem == null)
                {
                    return target;
                }

                var rewritten = idx with { Object = obj, Index = index };
                rewritten.ResolvedType = idx.ResolvedType;
                rewritten.ResolvedSetItem = resolvedSetItem ?? idx.ResolvedSetItem;
                return rewritten;
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
            operands.Add(VisitExpression(operand));

        // Build pairwise comparisons
        Expression result = new BinaryExpression(
            Left: operands[0],
            Operator: chain.Operators[0],
            Right: operands[1],
            Location: chain.Location)
        { ResolvedType = boolType };

        for (int i = 1; i < chain.Operators.Count; i++)
        {
            Expression pairCmp = new BinaryExpression(
                Left: operands[i],
                Operator: chain.Operators[i],
                Right: operands[i + 1],
                Location: chain.Location)
            { ResolvedType = boolType };

            result = new BinaryExpression(
                Left: result,
                Operator: BinaryOperator.And,
                Right: pairCmp,
                Location: chain.Location)
            { ResolvedType = boolType };
        }

        // Recurse so pairwise BinaryExpression nodes created above are also lowered.
        return VisitExpression(result);
    }

    /// <summary>
    /// Lowers a <c>!!</c> (<see cref="UnaryOperator.ForceUnwrap"/>) to <c>operand.unwrap()</c>.
    /// Extracted from the expression-lowering dispatch.
    /// </summary>
    private Expression LowerForceUnwrap(UnaryExpression forceUnwrap)
    {
        Expression operand = VisitExpression(forceUnwrap.Operand);
        TypeInfo? operandType = operand.ResolvedType;
        RoutineInfo? unwrapMemberRoutine = operandType != null
            ? ctx.Registry.LookupMemberRoutine(type: operandType, memberRoutineName: "unwrap")
            : null;
        CallLoweringKind unwrapKind = unwrapMemberRoutine != null
            ? ClassifyMemberRoutine(unwrapMemberRoutine)
            : operandType != null
                ? CallLoweringKind.DirectMemberRoutine
                : CallLoweringKind.Unknown;
        return new CallExpression(
            Callee: new MemberExpression(
                Object: operand,
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
        Expression operand = VisitExpression(unary.Operand);

        if (memberRoutineName == null)
        {
            return ReferenceEquals(operand, unary.Operand)
                ? expr
                : unary with { Operand = operand };
        }

        TypeInfo? operandType = operand.ResolvedType;

        // Flags types have no bitnot memberRoutine body -> codegen handles it via EmitBitwiseNot.
        // Skip memberRoutine-call lowering so the UnaryExpression passes through unchanged.
        if (operandType is FlagsTypeInfo
            && memberRoutineName == "bitnot")
        {
            return ReferenceEquals(operand, unary.Operand)
                ? expr
                : unary with { Operand = operand };
        }

        RoutineInfo? resolvedUnaryMemberRoutine = null;
        if (operandType != null)
        {
            resolvedUnaryMemberRoutine = ctx.Registry.LookupMemberRoutineOverload(type: operandType,
                memberRoutineName: memberRoutineName, argTypes: []);
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

        CallLoweringKind unaryKind = resolvedUnaryMemberRoutine != null
            ? ClassifyMemberRoutine(resolvedUnaryMemberRoutine)
            : operandType != null
                ? CallLoweringKind.DirectMemberRoutine
                : CallLoweringKind.Unknown;

        return new CallExpression(
            Callee: unaryCallee,
            Arguments: [],
            Location: unary.Location)
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
        Expression loweredObj = VisitExpression(gme.Object);
        var memberExpr = new MemberExpression(
            Object: loweredObj,
            MemberName: gme.MemberName,
            Location: gme.Location)
        {
            ResolvedType = gme.ResolvedType
        };

        // Use first type-arg name as identifier (the index variable).
        var idxExpr = new IdentifierExpression(
            Name: gme.TypeArguments[0].Name,
            Location: gme.TypeArguments[0].Location)
        {
            ResolvedType = gme.TypeArguments[0].ResolvedType
        };

        var indexExpr = new IndexExpression(
            Object: memberExpr,
            Index: idxExpr,
            Location: gme.Location)
        {
            ResolvedType = gme.ResolvedType
        };

        // Recurse -> IndexExpression case above converts to getitem! call.
        return VisitExpression(indexExpr);
    }

    /// <summary>
    /// Lowers an <see cref="IndexExpression"/> (<c>obj[i]</c>) to <c>obj.getitem!(i)</c> — resolving
    /// the overload, desugaring any back-index (<c>^n</c>) bounds, and wrapping the result in the
    /// element type's <c>store</c> when reading out an owned element. A typewise type-receiver
    /// (<c>Type[T].member()</c>) collapses to a bare typed identifier instead.
    /// </summary>
    private Expression LowerIndexExpression(IndexExpression idx)
    {
        // Typewise type-receiver: `NumericSumAdd[T].MemberRoutine()` parses as
        // IndexExpression(Ident("NumericSumAdd"), Ident("T")) because the parser only
        // treats `Ident[...]` as a generic-memberRoutine form when `]` is immediately followed
        // by `(`. SA recognizes the pattern and resolves the IndexExpression to the
        // generic resolution type — collapse to a bare typed identifier so MemberExpression
        // codegen sees a typewise receiver.
        string? GendefName(TypeInfo? t) => t switch
        {
            RecordTypeInfo { GenericDefinition: { } d } => d.Name,
            EntityTypeInfo { GenericDefinition: { } d } => d.Name,
            ProtocolTypeInfo { GenericDefinition: { } d } => d.Name,
            _ => null
        };
        if (idx is { Object: IdentifierExpression typeObjId, ResolvedType: { IsGenericResolution: true } resolvedTy } &&
            (resolvedTy.Name == typeObjId.Name
             || GendefName(resolvedTy) == typeObjId.Name))
        {
            return new IdentifierExpression(
                Name: typeObjId.Name,
                Location: idx.Location)
            {
                ResolvedType = resolvedTy
            };
        }

        Expression loweredObj = VisitExpression(idx.Object);
        Expression loweredIdx = VisitExpression(idx.Index);

        // Failability is a property, not part of the name — the property name is always
        // the bare `getitem`; codegen dispatches via ResolvedRoutine (which carries
        // IsFailable). Resolve the memberRoutine to set ResolvedRoutine / lowering kind.
        const string propertyName = "getitem";
        RoutineInfo? resolvedGetItem = null;
        TypeInfo? targetType = idx.Object.ResolvedType;

        // End-relative SLICE bounds: `xs[a til ^0]`. By this pass the range index is already a
        // `Range[U64]` CreatorExpression (ExpressionLoweringPass ran first) whose start/end may
        // still be a `^n` `BackIndexExpression` marker. Rewrite each such bound to the forward
        // position `count - n` via the free routine `back_resolve(count:, offset:)` (returns
        // `count` for `^0`; throws only when the offset n exceeds count).
        if (targetType != null && loweredIdx is CreatorExpression { TypeName: "Range" } rangeCtor &&
            rangeCtor.MemberVariables.Any(predicate: mv =>
                mv.Name is "start" or "end" && mv.Value is BackIndexExpression))
        {
            // ONLY the start/end bounds carry a `^n`; the step/inclusive members must be left
            // untouched (wrapping a numeric `step` in back_resolve would corrupt the stride).
            var rewrittenMembers = rangeCtor.MemberVariables.Select(selector: mv =>
                mv.Name is "start" or "end" && mv.Value is BackIndexExpression backBound
                    ? (mv.Name, BuildBackIndexResolve(loweredObj: loweredObj, backIndex: backBound,
                        targetType: targetType, location: mv.Value.Location))
                    : mv).ToList();
            loweredIdx = rangeCtor with { MemberVariables = rewrittenMembers };
        }

        // Back-index desugaring: `coll[^n]` has a `^n` `BackIndexExpression` index. Collections
        // only expose `getitem!(index: U64)`, so rewrite the index to a forward U64 position via
        // `back_resolve(count: coll.count(), offset: n)` (throws IndexOutOfBoundsError on
        // out-of-range). The object is referenced twice — acceptable for the common `var[^n]`
        // case; a side-effecting receiver would evaluate twice.
        if (targetType != null && loweredIdx is BackIndexExpression backIdx)
        {
            loweredIdx = BuildBackIndexResolve(loweredObj: loweredObj,
                backIndex: backIdx, targetType: targetType, location: idx.Location);
        }

        if (targetType != null)
        {
            TypeInfo? indexType = loweredIdx.ResolvedType ?? idx.Index.ResolvedType;
            resolvedGetItem = ResolveGetItemRoutine(targetType: targetType, indexType: indexType);
        }

        CallLoweringKind getitemKind = resolvedGetItem != null
            ? ClassifyMemberRoutine(resolvedGetItem)
            : targetType != null
                ? CallLoweringKind.DirectMemberRoutine
                : CallLoweringKind.Unknown;
        var member = new MemberExpression(
            Object: loweredObj,
            MemberName: propertyName,
            Location: idx.Location);
        var getitemCall = new CallExpression(
            Callee: member,
            Arguments: [loweredIdx],
            Location: idx.Location)
        {
            ResolvedRoutine = resolvedGetItem,
            ResolvedType = idx.ResolvedType,
            LoweringKind = getitemKind
        };
        // `a[i]` reads an element the container STILL owns. Make it appear as a fresh owned value by
        // applying the element type's `store` — a retaining copy (Text/Integer/variant) so the read
        // no longer aliases the buffer's live element (which would double-free on teardown). A
        // trivially-copyable element has no retaining store (GetLifecycle.Store == null) and its
        // bitwise read is already independent, so it is left bare. A bare `entity` element likewise
        // has no store — reading one out to KEEP it is rejected at SA (single-owner), so it never
        // needs a copy here.
        TypeInfo? elemType = idx.ResolvedType;
        RoutineInfo? elemStore = elemType != null
            ? ctx.Registry.GetLifecycle(type: elemType).Store
            : null;
        if (elemStore == null)
            return getitemCall;
        var storeCallee = new MemberExpression(
            Object: getitemCall, MemberName: elemStore.Name, Location: idx.Location)
            { ResolvedType = elemType };
        return new CallExpression(Callee: storeCallee, Arguments: [], Location: idx.Location)
        {
            ResolvedRoutine = elemStore,
            ResolvedType = elemType,
            LoweringKind = ClassifyMemberRoutine(elemStore)
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
        RoutineInfo? resolvedGetItem = ResolveGetItemOn(targetType: targetType, indexType: indexType);
        // Suflae container locals are `Roamed[Dict]`/`Roamed[List]` post-SA; the wrapper
        // has no `getitem`, so resolve against the UNWRAPPED inner container (exactly as the
        // membership/comparison branch does for `x in d`). RoamedProjectionLoweringPass then
        // projects the Roamed receiver to the inner value via `raw_inner()`.
        if (resolvedGetItem == null &&
            UnwrapRoamedInner(type: targetType) is { } innerTarget)
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
                memberRoutineName: "getitem", argTypes: [indexType]);
            if (byIndex != null) return byIndex;
            if (ctx.Registry.LookupType(name: "U64") is { } u64
                && ctx.Registry.LookupMemberRoutineOverload(type: targetType,
                    memberRoutineName: "getitem", argTypes: [u64]) is { } byU64)
                return byU64;
        }
        return ctx.Registry.LookupMemberRoutine(type: targetType, memberRoutineName: "getitem");
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
            // For Assign: lower the right side, and lower the INTERIOR of the left side
            // (e.g., the Object of a MemberExpression, or the Object/Index of an
            // IndexExpression).  The outermost left-side node must stay as-is so that
            // EmitBinaryAssign can dispatch on its type (MemberExpression -> field write,
            // IndexExpression ??setitem!).  Lowering the entire left would convert
            // IndexExpression -> CallExpression(getitem!), breaking setitem dispatch.
            if (bin.Operator == BinaryOperator.Assign)
            {
                Expression rhs = VisitExpression(bin.Right);
                Expression lhs = LowerAssignTarget(bin.Left);
                return ReferenceEquals(rhs, bin.Right) && ReferenceEquals(lhs, bin.Left)
                    ? expr
                    : bin with { Left = lhs, Right = rhs };
            }

            Expression left0 = VisitExpression(bin.Left);
            Expression right0 = VisitExpression(bin.Right);
            return ReferenceEquals(left0, bin.Left) && ReferenceEquals(right0, bin.Right)
                ? expr
                : bin with { Left = left0, Right = right0 };
        }

        Expression left = VisitExpression(bin.Left);
        Expression right = VisitExpression(bin.Right);

        // Membership operators reverse receiver/argument: x in coll -> coll.contains(x)
        bool isReversed = bin.Operator is BinaryOperator.In or BinaryOperator.NotIn;
        Expression receiver = isReversed ? right : left;
        Expression argument = isReversed ? left : right;

        // Look up the exact overload (by arg type) to get failable suffix, param name, and
        // ResolvedRoutine. LookupMemberRoutineOverload disambiguates e.g. Moment.sub(Moment)->Duration
        // from Moment.sub(Duration)->Moment. Setting ResolvedRoutine tells codegen which
        // overload to call without performing its own (potentially ambiguous) lookup.
        TypeInfo? receiverType = receiver.ResolvedType;
        TypeInfo? argType = argument.ResolvedType;
        RoutineInfo? resolvedMemberRoutine = ResolveBinaryOperatorRoutine(
            memberRoutineName: memberRoutineName, receiverType: receiverType, argType: argType);

        // Mixed fixed-width integer comparisons have no direct cross-width overloads in the
        // stdlib. Normalize both sides to a common width here so we lower to a concrete
        // same-type comparison instead of letting codegen fall back to an arbitrary overload.
        if (resolvedMemberRoutine == null &&
            receiverType != null &&
            argType != null &&
            bin.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                or BinaryOperator.Less or BinaryOperator.LessEqual
                or BinaryOperator.Greater or BinaryOperator.GreaterEqual &&
            TryResolveCommonIntegerComparisonType(left: receiverType,
                right: argType,
                out TypeInfo? commonType))
        {
            resolvedMemberRoutine = NormalizeToCommonIntegerComparison(
                memberRoutineName: memberRoutineName, commonType: commonType!,
                receiver: ref receiver, argument: ref argument);
            receiverType = commonType;
            argType = commonType;
        }

        if (resolvedMemberRoutine is { Parameters.Count: > 0 } &&
            bin.Operator is BinaryOperator.ArithmeticLeftShift
                or BinaryOperator.ArithmeticRightShift
                or BinaryOperator.LogicalLeftShift
                or BinaryOperator.LogicalRightShift)
        {
            TypeInfo paramType = resolvedMemberRoutine.Parameters[index: 0].Type;
            if (argType != null &&
                argType.FullName != paramType.FullName &&
                TryGetFixedWidthIntegerInfo(type: argType, out _, out _) &&
                TryGetFixedWidthIntegerInfo(type: paramType, out _, out _))
            {
                argument = WrapNumericOperand(expr: argument, targetType: paramType);
            }
        }

        // Flags bitand/bitor/bitxor/eq/ne ARE lowered to memberRoutine calls: WiredRoutinePass
        // synthesizes those bodies as @llvm_ir intrinsic calls on the underlying i64 repr
        // (bit_or/bit_and/bit_xor/int_eq/int_ne), so lowering `a | b` to `a.bitor(b)` cannot
        // recurse — the body uses the intrinsic, not the surface operator. (bitnot stays
        // unlowered; its unary path below still passes through for codegen's EmitBitwiseNot.)

        // Choice eq/ne bodies use BinaryOperator.Is (not Equal), so they never reach
        // this point. No skip needed for choice types.

        // Always lower to a memberRoutine call -> even when the memberRoutine isn't in the registry
        // (e.g., stdlib bodies where ResolvedType is null).  When ResolvedRoutine is null,
        // codegen's EmitMemberRoutineCall resolves the memberRoutine at emission time using the receiver's
        // LLVM-inferred type; it will also retry with isFailable:null to find add! etc.
        // Failability is structural on the callee — no `!` in the name. When the memberRoutine is
        // unknown, IsFailable stays false and codegen's EmitMemberRoutineCall retries either form.
        bool binFailable = resolvedMemberRoutine?.IsFailable ?? false;
        string paramName = resolvedMemberRoutine?.Parameters.Count > 0
            ? resolvedMemberRoutine.Parameters[0].Name
            : "you";

        var binCallee = new MemberExpression(
            Object: receiver,
            MemberName: memberRoutineName,
            Location: bin.Location) { IsFailable = binFailable };

        CallLoweringKind lk = resolvedMemberRoutine != null
            ? ClassifyMemberRoutine(resolvedMemberRoutine)
            : receiverType != null ? CallLoweringKind.DirectMemberRoutine
            : CallLoweringKind.Unknown;

        return new CallExpression(
            Callee: binCallee,
            Arguments: [new NamedArgumentExpression(Name: paramName, Value: argument, Location: bin.Location)],
            Location: bin.Location)
        { ResolvedType = bin.ResolvedType, ResolvedRoutine = resolvedMemberRoutine, LoweringKind = lk };
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
            argTypes: [commonType]) ??
                     ctx.Registry.LookupMemberRoutine(type: commonType,
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
        if (receiverType == null) return null;

        RoutineInfo? resolvedMemberRoutine = argType != null
            ? ctx.Registry.LookupMemberRoutineOverload(type: receiverType, memberRoutineName: memberRoutineName,
                argTypes: [argType])
            : ctx.Registry.LookupMemberRoutine(type: receiverType, memberRoutineName: memberRoutineName);
        resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: memberRoutineName);
        // If the non-failable form doesn't exist, try the failable form (sub -> sub!).
        // Types like U64 only define sub! (underflow would be undefined behavior).
        // The name is BARE; failability is structural — retry with isFailable: true.
        if (resolvedMemberRoutine == null)
        {
            resolvedMemberRoutine = argType != null
                ? ctx.Registry.LookupMemberRoutineOverload(type: receiverType,
                    memberRoutineName: memberRoutineName, argTypes: [argType])
                : null;
            resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: receiverType,
                memberRoutineName: memberRoutineName, isFailable: true);
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
            WrapperTypeInfo w
                when Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: w) != null
                => w.InnerType,
            RecordTypeInfo r
                when Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: r) != null
                     && r.TypeArguments is { Count: >= 1 } ra
                => ra[index: 0],
            _ => null
        };
        if (resolvedMemberRoutine == null && innerRecv != null)
        {
            resolvedMemberRoutine = argType != null
                ? ctx.Registry.LookupMemberRoutineOverload(type: innerRecv,
                    memberRoutineName: memberRoutineName, argTypes: [argType])
                : null;
            resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: innerRecv, memberRoutineName: memberRoutineName);
            resolvedMemberRoutine ??= ctx.Registry.LookupMemberRoutine(type: innerRecv,
                memberRoutineName: memberRoutineName, isFailable: true);
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
        => BodyDispatch.RunOnVariantBodies(ctx.VariantBodies, lower: (_, body) => VisitStatement(body));

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
        => BodyDispatch.RunOnInstantiatedGenericBodies(
            instantiatedGenericBodies, lower: (_, entry) => VisitStatement(entry.Ast.Body));

    private RoutineInfo ResolveMemberRoutineGenericRoutine(RoutineInfo routine,
        List<TypeInfo> argTypes)
    {
        if (!routine.IsGenericDefinition || routine.GenericParameters == null)
        {
            return routine;
        }

        if (argTypes.Any(static t => t is ErrorTypeInfo or GenericParameterTypeInfo))
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
            if (param.Name == "me") continue;
            if (argIdx >= argTypes.Count) break;
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
            typeArguments: inferred.Select(selector: t => t!).ToList());
    }

    private static void InferMemberRoutineGenericArguments(TypeInfo paramType, TypeInfo argType,
        List<string> genericParameters, TypeInfo?[] inferred)
    {
        if (paramType is GenericParameterTypeInfo)
        {
            int idx = genericParameters.ToList().IndexOf(item: paramType.Name);
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

        return new CreatorExpression(
            TypeName: targetType.Name,
            TypeArguments: null,
            MemberVariables:
            [
                ("from", expr)
            ],
            Location: expr.Location)
        {
            ResolvedType = targetType,
            ConstructedType = targetType
        };
    }

    private static CallLoweringKind ClassifyMemberRoutine(RoutineInfo memberRoutine)
    {
        if (memberRoutine.LlvmIrTemplate != null) return CallLoweringKind.LlvmIntrinsic;
        return CallLoweringKind.DirectMemberRoutine;
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
    private static TypeInfo? UnwrapRoamedInner(TypeInfo? type) => type switch
    {
        WrapperTypeInfo w when Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: w) != null
            => w.InnerType,
        RecordTypeInfo { TypeArguments: { Count: >= 1 } ra } r
            when Compiler.Resolution.TypeRegistry.GetRcWrapperBaseName(type: r) != null
            => ra[index: 0],
        _ => null
    };

    private Expression BuildBackIndexResolve(Expression loweredObj, BackIndexExpression backIndex,
        TypeInfo targetType, SourceLocation location)
    {
        // receiver.count() -> U64
        RoutineInfo? countRoutine = ctx.Registry.LookupMemberRoutine(type: targetType, memberRoutineName: Resolution.RuntimeContract.Collection.Count);
        var countCall = new CallExpression(
            Callee: new MemberExpression(Object: loweredObj, MemberName: Resolution.RuntimeContract.Collection.Count,
                Location: location),
            Arguments: [],
            Location: location)
        {
            ResolvedRoutine = countRoutine,
            ResolvedType = countRoutine?.ReturnType,
            LoweringKind = countRoutine != null
                ? ClassifyMemberRoutine(countRoutine)
                : CallLoweringKind.DirectMemberRoutine
        };

        // back_resolve(count: coll.count(), offset: n) -> U64 (free routine, failable: throws
        // IndexOutOfBoundsError on overshoot). `^` carries no runtime type; the offset is the `^n`
        // operand, already typed U64 by semantic analysis (see AnalyzeBackIndexExpression).
        RoutineInfo? resolveRoutine =
            ctx.Registry.LookupRoutine(fullName: $"Core.{Resolution.RuntimeContract.BackResolve}", isFailable: true)
            ?? ctx.Registry.LookupRoutine(fullName: Resolution.RuntimeContract.BackResolve, isFailable: true);

        // The offset must be a scalar U64. An untyped/signed integer-literal operand (`^1`) is retagged
        // U64Literal here so codegen never treats it as an arbitrary-precision Integer (Text-backed);
        // any other operand (a U64 variable, an expression) already carries its type and passes through.
        Expression offset = backIndex.Operand is LiteralExpression
            {
                LiteralType: TokenType.UndecidedInteger or TokenType.IntegerLiteral
                    or TokenType.S64Literal or TokenType.U64Literal
            } olit
            ? new LiteralExpression(Value: olit.Value, LiteralType: TokenType.U64Literal, Location: olit.Location)
            {
                ResolvedType = countRoutine?.ReturnType
            }
            : backIndex.Operand;
        return new CallExpression(
            Callee: new IdentifierExpression(Name: Resolution.RuntimeContract.BackResolve, Location: location)
            {
                ResolvedType = resolveRoutine?.ReturnType
            },
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

        if (!TryGetFixedWidthIntegerInfo(type: left, out bool leftSigned, out int leftWidth) ||
            !TryGetFixedWidthIntegerInfo(type: right, out bool rightSigned, out int rightWidth))
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
            targetWidth = NextSignedWidth(minExclusive: Math.Max(val1: leftWidth, val2: rightWidth));
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

        signed = type.Name[0] == 'S';
        if (!signed && type.Name[0] != 'U')
        {
            return false;
        }

        return int.TryParse(s: type.Name[1..], result: out width);
    }

    private static int NextSignedWidth(int minExclusive)
    {
        foreach (int candidate in new[] { 8, 16, 32, 64, 128 })
        {
            if (candidate > minExclusive)
            {
                return candidate;
            }
        }

        return 0;
    }
}
