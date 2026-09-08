using System;
using System.Collections.Generic;
using System.Linq;
using Compiler.Tokenizer;
using Compiler.Synthesis;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Lowers high-level expression constructs to simpler ANF-style statement+expression forms.
/// Runs last in the per-file desugaring pipeline (after ControlFlowLoweringPass).
///
/// Sub-transformations applied in-order during a single recursive walk:
/// <list type="bullet">
///   <item>1a. Chained comparisons: <c>a &lt; b &lt; c</c> -> <c>(a &lt; b) and (b &lt; c)</c></item>
///   <item>1b. None-coalescing: <c>a ?? b</c> -> temp vars + WhenStatement (preserves lazy eval)</item>
///   <item>1c. Force-unwrap: <c>a!!</c> -> <c>a.unwrap()</c> -- handled by <see cref="OperatorLoweringPass"/>
///         so that stdlib bodies (which bypass this pass) are also covered.</item>
///   <item>1d. Optional member access: <c>a?.prop</c> -> temp vars + WhenStatement</item>
/// </list>
///
/// Hoisting transforms (1b, 1d) use ANF lifting: they return a list of statements
/// to splice before the containing statement plus a replacement <see cref="IdentifierExpression"/>.
/// </summary>
internal sealed class ExpressionLoweringPass(PostprocessingContext ctx)
{
    private const string NoneTypeName = "None";
    private const string TypeIdFieldName = "type_id";

    private int _tempCount;

    /// <summary>
    /// Set true whenever this run synthesizes a <see cref="WhenStatement"/> that a subsequent
    /// <see cref="PatternLoweringPass"/> must still fold into an if/else chain — i.e. an absence-when
    /// from <c>??</c>/<c>?.</c> lowering, or a hoisted when-expression. The pipeline reads this after
    /// the first ELP run to decide whether the second PLP+ELP round is needed at all; when nothing
    /// produced a WhenStatement, that round is a pure no-op re-walk and is skipped.
    /// </summary>
    public bool ProducedWhenStatement { get; private set; }

    private string NextTempName(string prefix) => $"_{prefix}_{_tempCount++}";

    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: r => LowerStatementFull(r.Body));

    // --- Statement lowering ------------------------------------------------------

    /// <summary>
    /// Fully lowers a statement, wrapping in a <see cref="BlockStatement"/> if hoisted
    /// statements need to precede it.
    /// </summary>
    private Statement LowerStatementFull(Statement stmt)
    {
        var (hoisted, lowered) = LowerStatement(stmt);
        if (hoisted.Count == 0) return lowered;

        var stmts = new List<Statement>(capacity: hoisted.Count + 1);
        stmts.AddRange(hoisted);
        stmts.Add(lowered);
        return new BlockStatement(Statements: stmts, Location: stmt.Location);
    }

    /// <summary>
    /// Lowers a statement, returning any statements that must be prepended before it.
    /// </summary>
    private (List<Statement> Hoisted, Statement Lowered) LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            // -- Compound: recurse into children --------------------------------

            case BlockStatement b:
            {
                List<Statement> loweredList = LowerStatementList(b.Statements);
                if (ReferenceEquals(loweredList, b.Statements)) return ([], b);
                return ([], b with { Statements = loweredList });
            }

            case IfStatement ifs:
            {
                var (condH, loweredCond) = LowerExpr(ifs.Condition);
                Statement then = LowerStatementFull(ifs.ThenStatement);
                Statement? elseS = ifs.ElseStatement != null
                    ? LowerStatementFull(ifs.ElseStatement)
                    : null;
                bool changed = !ReferenceEquals(loweredCond, ifs.Condition)
                               || !ReferenceEquals(then, ifs.ThenStatement)
                               || !ReferenceEquals(elseS, ifs.ElseStatement);
                if (!changed && condH.Count == 0) return ([], stmt);
                return (condH,
                    ifs with { Condition = loweredCond, ThenStatement = then, ElseStatement = elseS });
            }

            case WhileStatement w:
            {
                var (condH, loweredCond) = LowerExpr(w.Condition);
                Statement body = LowerStatementFull(w.Body);
                Statement? elseB = w.ElseBranch != null
                    ? LowerStatementFull(w.ElseBranch)
                    : null;
                bool changed = !ReferenceEquals(loweredCond, w.Condition)
                               || !ReferenceEquals(body, w.Body)
                               || !ReferenceEquals(elseB, w.ElseBranch);
                if (!changed && condH.Count == 0) return ([], stmt);
                return (condH, w with { Condition = loweredCond, Body = body, ElseBranch = elseB });
            }

            case LoopStatement loop:
            {
                Statement body = LowerStatementFull(loop.Body);
                if (ReferenceEquals(body, loop.Body)) return ([], stmt);
                return ([], loop with { Body = body });
            }

            case WhenStatement w:
            {
                var (subjH, loweredSubj) = LowerExpr(w.Expression);
                bool clauseChanged = false;
                var clauses = new List<WhenClause>(capacity: w.Clauses.Count);
                foreach (WhenClause c in w.Clauses)
                {
                    Statement lBody = LowerStatementFull(c.Body);
                    if (!ReferenceEquals(lBody, c.Body))
                    {
                        clauses.Add(c with { Body = lBody });
                        clauseChanged = true;
                    }
                    else
                    {
                        clauses.Add(c);
                    }
                }

                bool changed = !ReferenceEquals(loweredSubj, w.Expression) || clauseChanged;
                if (!changed && subjH.Count == 0) return ([], stmt);
                return (subjH, w with { Expression = loweredSubj, Clauses = clauses });
            }

            case UsingStatement u:
            {
                var (hoisted, loweredRes) = LowerExpr(u.Resource);
                Statement body = LowerStatementFull(u.Body);
                Statement? fb = u.FallbackBody != null ? LowerStatementFull(u.FallbackBody) : null;
                bool changed = !ReferenceEquals(loweredRes, u.Resource)
                               || !ReferenceEquals(body, u.Body)
                               || !ReferenceEquals(fb, u.FallbackBody);
                if (!changed && hoisted.Count == 0) return ([], stmt);
                return (hoisted, u with { Resource = loweredRes, Body = body, FallbackBody = fb });
            }

            case DangerStatement d:
            {
                Statement lowered = LowerStatementFull(d.Body);
                if (ReferenceEquals(lowered, d.Body)) return ([], stmt);
                return ([], d with { Body = (BlockStatement)lowered });
            }

            // -- Simple: lower the contained expressions -------------------------

            case AssignmentStatement asgn:
            {
                var (hoisted, loweredVal) = LowerExpr(asgn.Value);
                // Maybe auto-wrap on a member store: `me.field = x` where `field : Maybe[T]` and
                // `x : T` boxes the bare value into `Maybe[T](present: true, value: x)` as a real
                // CreatorExpression (codegen's record-creator path builds the { i1, T } aggregate),
                // instead of codegen hand-building the insertvalue at the store site.
                Expression effectiveVal =
                    TryWrapMemberMaybe(target: asgn.Target, value: loweredVal) ?? loweredVal;
                if (hoisted.Count == 0 && ReferenceEquals(effectiveVal, asgn.Value)) return ([], stmt);
                return (hoisted, asgn with { Value = effectiveVal });
            }

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } decl:
            {
                var (hoisted, loweredInit) = LowerExpr(vd.Initializer);
                Expression effectiveInit = TryWrapCarrier(vd.Type, loweredInit) ?? loweredInit;
                if (hoisted.Count == 0 && ReferenceEquals(effectiveInit, vd.Initializer))
                    return ([], stmt);
                return (hoisted, decl with { Declaration = vd with { Initializer = effectiveInit } });
            }

            case ReturnStatement { Value: not null } ret:
            {
                var (hoisted, loweredVal) = LowerExpr(ret.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, ret.Value)) return ([], stmt);
                return (hoisted, ret with { Value = loweredVal });
            }

            case ExpressionStatement { Expression: CompoundAssignmentExpression } es:
            {
                // Compound assignment in statement position: the result value is discarded.
                // LowerExpr for the fallback path returns (hoisted=[AssignmentStatement], residual=LHS).
                // Don't emit the residual as a bare expression statement.
                var (hoisted, loweredExpr) = LowerExpr(es.Expression);
                if (hoisted.Count == 0)
                    return ([], es with { Expression = loweredExpr });
                if (hoisted.Count == 1) return ([], hoisted[0]);
                return ([], new BlockStatement(hoisted, es.Location));
            }

            case ExpressionStatement es:
            {
                var (hoisted, loweredExpr) = LowerExpr(es.Expression);
                if (hoisted.Count == 0 && ReferenceEquals(loweredExpr, es.Expression)) return ([], stmt);
                return (hoisted, es with { Expression = loweredExpr });
            }

            case DiscardStatement ds:
            {
                var (hoisted, loweredExpr) = LowerExpr(ds.Expression);
                if (hoisted.Count == 0 && ReferenceEquals(loweredExpr, ds.Expression)) return ([], stmt);
                return (hoisted, ds with { Expression = loweredExpr });
            }

            case BecomesStatement bs:
            {
                var (hoisted, loweredVal) = LowerExpr(bs.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, bs.Value)) return ([], stmt);
                return (hoisted, bs with { Value = loweredVal });
            }

            case ThrowStatement t:
            {
                var (hoisted, loweredErr) = LowerExpr(t.Error);
                if (hoisted.Count == 0 && ReferenceEquals(loweredErr, t.Error)) return ([], stmt);
                return (hoisted, t with { Error = loweredErr });
            }

            // D-AST-7: recurse into variant return value expressions.
            case VariantReturnStatement { Value: not null } vrs:
            {
                var (hoisted, loweredVal) = LowerExpr(vrs.Value);
                if (hoisted.Count == 0 && ReferenceEquals(loweredVal, vrs.Value)) return ([], stmt);
                return (hoisted, vrs with { Value = loweredVal });
            }

            default:
                return ([], stmt);
        }
    }

    /// <summary>
    /// Lowers a flat statement list, splicing in hoisted statements at each site.
    /// Returns the original list if no changes were made (preserving reference identity).
    /// </summary>
    private List<Statement> LowerStatementList(List<Statement> stmts)
    {
        var result = new List<Statement>(capacity: stmts.Count);
        bool anyChanged = false;

        foreach (Statement stmt in stmts)
        {
            var (hoisted, lowered) = LowerStatement(stmt);
            if (hoisted.Count > 0 || !ReferenceEquals(lowered, stmt))
                anyChanged = true;
            result.AddRange(hoisted);
            result.Add(lowered);
        }

        return anyChanged ? result : stmts;
    }

    // --- Expression lowering -----------------------------------------------------

    /// <summary>
    /// Lowers an expression, returning:
    /// <list type="bullet">
    ///   <item>A list of statements to hoist before the containing statement.</item>
    ///   <item>A replacement expression (often the original, or a temp-var ref).</item>
    /// </list>
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerExpr(Expression expr)
    {
        switch (expr)
        {
            // -- Step 0: flow-narrowed read ---------------------------------------
            // `x` narrowed to a single arm/payload of a carrier/variant (SA set NarrowedFrom =
            // declared aggregate, ResolvedType = the arm). Rewrite the read into a payload extraction
            // from the full underlying value so codegen loads the arm, not the whole aggregate.
            case IdentifierExpression { NarrowedFrom: not null } narrowedId:
            {
                TypeInfo declared = narrowedId.NarrowedFrom!;
                TypeInfo target = narrowedId.ResolvedType!;
                SourceLocation nloc = narrowedId.Location;

                Expression rawRead()
                    => new IdentifierExpression(Name: narrowedId.Name, Location: nloc)
                        { ResolvedType = declared };

                CarrierPayloadExpression extractStep(Expression carrier, TypeInfo step)
                    => new CarrierPayloadExpression(
                        Carrier: carrier, ConcreteType: TypeInfoToExpr(type: step, loc: nloc),
                        Location: nloc) { ResolvedType = step };

                // Non-carrier variant: extract along the arm path — which may be NESTED, e.g. an
                // `Outer` narrowed to `Inner` then to `Inner`'s arm `S32` yields Outer -> Inner -> S32
                // (each level a field-1 payload load).
                if (declared is VariantTypeInfo variant &&
                    !IsMaybeRecord(type: declared) && !IsResultOrLookup(type: declared) &&
                    FindVariantArmPath(from: variant, target: target) is { } path)
                {
                    Expression acc = rawRead();
                    foreach (TypeInfo step in path)
                        acc = extractStep(carrier: acc, step: step);
                    return ([], acc);
                }

                // Carrier (Maybe/Result/Lookup): single-level payload extraction.
                return ([], extractStep(carrier: rawRead(), step: target));
            }

            // -- Step 1a: chained comparisons -------------------------------------
            // Multi-comparison chains (a <= b <= c) are lowered to pairwise comparisons
            // joined by And. The And must then be further lowered to ConditionalExpression.
            case ChainedComparisonExpression chain:
            {
                var (h, lowered) = LowerChainedComparison(chain);
                if (lowered is BinaryExpression { Operator: BinaryOperator.And } andBin)
                {
                    var (andH, andLowered) = LowerBooleanAnd(andBin);
                    return (Concat(h, andH), andLowered);
                }
                return (h, lowered);
            }

            // -- Step 1b: none-coalescing (??) ------------------------------------
            case BinaryExpression { Operator: BinaryOperator.NoneCoalesce } binary:
                return LowerNoneCoalesce(binary);

            // -- Step 1c: force-unwrap (!!) -- handled by OperatorLoweringPass --------
            // !! is desugared to operand.unwrap() in OperatorLoweringPass so that
            // stdlib bodies (which bypass ExpressionLoweringPass) are also covered.

            // -- Step 1d: optional member access (?.) -----------------------------
            case OptionalMemberExpression optMember:
                return LowerOptionalMember(optMember);

            // -- Step 1e: flags combination (and/but on FlagsTypeInfo) -------------
            case BinaryExpression { Operator: BinaryOperator.And or BinaryOperator.But, Left.ResolvedType: FlagsTypeInfo } flagsBin:
                return LowerFlagsCombination(flagsBin);

            // -- Step 1f: carrier absence checks (is None / is None) -------------
            case IsPatternExpression ipe:
                return LowerIsPatternExpression(ipe);

            // -- Step 1f-2: variant type test (x is T / x isnot T) -> type_id compare --
            // Variant subjects: x is S64 -> x.type_id == FNV("S64")
            //                   x isnot S64 -> x.type_id != FNV("S64")
            case BinaryExpression { Operator: BinaryOperator.Is or BinaryOperator.IsNot, Left.ResolvedType: VariantTypeInfo } isBin:
                return LowerVariantIsExpression(isBin);

            // -- Step 1f-3: choice discriminant test (a is b / a isnot b) -> S32 equality --
            // A choice is S32-backed; reinterpret both sides to S32 and compare with ==/!=, which OLP
            // lowers to S32.eq / S32.ne (icmp eq/ne i32). Keeps the `is` operator out of codegen.
            case BinaryExpression { Operator: BinaryOperator.Is or BinaryOperator.IsNot, Left.ResolvedType: ChoiceTypeInfo choiceLeft } choiceBin:
                return LowerChoiceIsExpression(choiceBin, choiceLeft);

            // -- Step 1g: boolean short-circuit And -> ConditionalExpression --------
            // a and b  ->  if a { _cif = b } else { _cif = false }
            // Flags And (union of active bits) is handled by Step 1e above.
            case BinaryExpression { Operator: BinaryOperator.And, Left.ResolvedType: not FlagsTypeInfo } boolAnd:
                return LowerBooleanAnd(boolAnd);

            // -- Step 1h: boolean short-circuit Or -> ConditionalExpression ---------
            // a or b  ->  if a { _cif = true } else { _cif = b }
            case BinaryExpression { Operator: BinaryOperator.Or } boolOr:
                return LowerBooleanOr(boolOr);

            // -- Step 1h-2: obeys/disobeys -> compile-time Bool literal -------------
            // `x obeys P` / `x disobeys P` are resolved during SA (which validates the
            // conformance and gates any error). The result is a fixed compile-time constant:
            // `obeys` is true, `disobeys` is false. Fold to a Bool literal here so codegen
            // never has to synthesize the constant itself.
            case BinaryExpression { Operator: BinaryOperator.Obeys or BinaryOperator.Disobeys } obeysBin:
            {
                bool obeysValue = obeysBin.Operator == BinaryOperator.Obeys;
                return ([], new LiteralExpression(
                    Value: obeysValue,
                    LiteralType: obeysValue ? TokenType.True : TokenType.False,
                    Location: obeysBin.Location)
                    { ResolvedType = obeysBin.ResolvedType ?? ctx.Registry.LookupType(name: "Bool") });
            }

            // -- Step 1j: member-store Maybe auto-wrap on `me.field = x` -----------
            // User assignments parse as BinaryExpression(Assign), not AssignmentStatement. When the
            // LHS is a `Maybe[T]` member and the RHS is a bare `T`, box it into a Maybe creator here
            // (the same wrap the AssignmentStatement path applies) so codegen never hand-builds the
            // { i1, T } insertvalue at the store site.
            case BinaryExpression { Operator: BinaryOperator.Assign } assignBin:
            {
                var (leftH, loweredLeft) = LowerExpr(assignBin.Left);
                var (rightH, loweredRight) = LowerExpr(assignBin.Right);
                Expression wrappedRight =
                    TryWrapMemberMaybe(target: loweredLeft, value: loweredRight) ?? loweredRight;
                var hoisted = Concat(leftH, rightH);
                if (hoisted.Count == 0
                    && ReferenceEquals(loweredLeft, assignBin.Left)
                    && ReferenceEquals(wrappedRight, assignBin.Right))
                    return ([], expr);
                return (hoisted, assignBin with { Left = loweredLeft, Right = wrappedRight });
            }

            // -- Recursive descent for all other node types ------------------------

            case BinaryExpression bin:
            {
                var (leftH, loweredLeft) = LowerExpr(bin.Left);
                var (rightH, loweredRight) = LowerExpr(bin.Right);
                var hoisted = Concat(leftH, rightH);
                if (hoisted.Count == 0
                    && ReferenceEquals(loweredLeft, bin.Left)
                    && ReferenceEquals(loweredRight, bin.Right))
                    return ([], expr);
                return (hoisted, bin with { Left = loweredLeft, Right = loweredRight });
            }

            // -- Step 1i: logical not -> ConditionalExpression ----------------------
            // not x  ->  if x { _cif = false } else { _cif = true }
            // BitwiseNot (~) on FlagsTypeInfo stays as UnaryExpression for OperatorLoweringPass.
            case UnaryExpression { Operator: UnaryOperator.Not } notExpr:
                return LowerLogicalNot(notExpr);

            case UnaryExpression unary:
            {
                var (h, lowered) = LowerExpr(unary.Operand);
                if (h.Count == 0 && ReferenceEquals(lowered, unary.Operand)) return ([], expr);
                return (h, unary with { Operand = lowered });
            }

            case CallExpression call:
                return LowerCallExpr(call: call, expr: expr);

            case MemberExpression mem:
                return LowerMemberExpr(mem: mem, expr: expr);

            case IndexExpression idx:
            {
                var (objH, loweredObj) = LowerExpr(idx.Object);
                var (idxH, loweredIdx) = LowerExpr(idx.Index);
                var hoisted = Concat(objH, idxH);
                if (hoisted.Count == 0
                    && ReferenceEquals(loweredObj, idx.Object)
                    && ReferenceEquals(loweredIdx, idx.Index))
                    return ([], expr);
                var rewritten = idx with { Object = loweredObj, Index = loweredIdx };
                rewritten.ResolvedType = idx.ResolvedType;
                rewritten.ResolvedSetItem = idx.ResolvedSetItem;
                return (hoisted, rewritten);
            }

            case NamedArgumentExpression named:
                // Strip the wrapper -- after SA the argument is already in its correct position.
                return LowerExpr(named.Value);

            case CreatorExpression creator:
            {
                var hoisted = new List<Statement>();
                var members = new List<(string Name, Expression Value)>(
                    capacity: creator.MemberVariables.Count);
                bool changed = false;
                foreach ((string name, Expression value) in creator.MemberVariables)
                {
                    var (h, lowered) = LowerExpr(value);
                    hoisted.AddRange(h);
                    members.Add((name, lowered));
                    if (!ReferenceEquals(lowered, value)) changed = true;
                }

                if (!changed && hoisted.Count == 0) return ([], expr);
                return (hoisted, creator with { MemberVariables = members });
            }

            case WithExpression withExpr:
                return LowerWithExpression(withExpr);

            case GenericMemberRoutineCallExpression gmc:
            {
                var hoisted = new List<Statement>();
                var (objH, loweredObj) = LowerExpr(gmc.Object);
                hoisted.AddRange(objH);

                var args = new List<Expression>(capacity: gmc.Arguments.Count);
                bool argsChanged = false;
                foreach (Expression arg in gmc.Arguments)
                {
                    var (h, lowered) = LowerExpr(arg);
                    hoisted.AddRange(h);
                    args.Add(lowered);
                    if (!ReferenceEquals(lowered, arg)) argsChanged = true;
                }

                if (hoisted.Count == 0 && !argsChanged
                    && ReferenceEquals(loweredObj, gmc.Object))
                    return ([], expr);
                return (hoisted, gmc with { Object = loweredObj, Arguments = args });
            }

            case CompoundAssignmentExpression compound:
                return LowerCompoundAssignment(compound: compound);

            case StealExpression steal:
            {
                // Strip the wrapper -- ownership transfer semantics are only needed during SA.
                var (h, lowered) = LowerExpr(steal.Operand);
                return (h, lowered);
            }

            case InsertedTextExpression ftext:
            {
                var hoisted = new List<Statement>();
                var parts = new List<InsertedTextPart>(capacity: ftext.Parts.Count);
                bool changed = false;
                foreach (InsertedTextPart part in ftext.Parts)
                {
                    if (part is ExpressionPart ep)
                    {
                        var (h, lowered) = LowerExpr(ep.Expression);
                        hoisted.AddRange(h);
                        parts.Add(ep with { Expression = lowered });
                        if (!ReferenceEquals(lowered, ep.Expression)) changed = true;
                    }
                    else
                    {
                        parts.Add(part);
                    }
                }

                if (!changed && hoisted.Count == 0) return ([], expr);
                return (hoisted, ftext with { Parts = parts });
            }

            case ConditionalExpression cond:
                return LowerConditionalExpr(cond: cond);

            case TupleLiteralExpression tuple:
                return LowerTupleLiteral(tuple: tuple);

            case ListLiteralExpression list:
                return LowerListLiteral(list);

            case SetLiteralExpression set:
                return LowerSetLiteral(set);

            case DictLiteralExpression dict:
                return LowerDictLiteral(dict);

            case DictEntryLiteralExpression dictEntry:
                return LowerDictEntryLiteral(dictEntry);

            case FlagsTestExpression flagsTest:
                return LowerFlagsTest(flagsTest);

            case RangeExpression range:
                return LowerRange(range);

            case WhenExpression whenExpr:
                return LowerWhenExpr(whenExpr: whenExpr, expr: expr);

            case IdentifierExpression id:
                return LowerIdentifierExpr(id: id, expr: expr);

            // Bare unsuffixed literals: rewrite LiteralType to the SA-resolved concrete type
            // so codegen never receives UndecidedInteger / UndecidedDecimal tokens.
            case LiteralExpression { LiteralType: TokenType.UndecidedInteger } undecInt:
                return ([], undecInt with { LiteralType = ResolveUndecidedIntegerLiteral(undecInt) });

            case LiteralExpression { LiteralType: TokenType.UndecidedDecimal } undecDec:
                return ([], undecDec with { LiteralType = ResolveUndecidedDecimalLiteral(undecDec) });

            // Lambda bodies are lifted to top-level routines by LambdaLiftingPass, which runs
            // AFTER this pass — so the lifted body is never lowered again. Descend into the body
            // here so its UndecidedInteger/UndecidedDecimal literals get a concrete LiteralType;
            // otherwise codegen receives UndecidedInteger and (per IsIntegerLiteralType) emits it
            // as a Text string constant (e.g. `x % 2` → `mod(i64, %Record.Text)` type mismatch).
            // Lambda bodies are expression-position and cannot carry hoisted statements, so only
            // rewrite when lowering produced none; complex bodies (`??`/`?.`) fall through unchanged.
            case LambdaExpression lambda:
            {
                var (bodyH, loweredBody) = LowerExpr(expr: lambda.Body);
                if (bodyH.Count == 0 && !ReferenceEquals(objA: loweredBody, objB: lambda.Body))
                    return ([], lambda with { Body = loweredBody });
                return ([], expr);
            }

            default:
                // LiteralExpression, TypeExpression,
                // BlockExpression, TypeConversionExpression, GenericMemberExpression:
                // no sub-expressions that need lowering.
                return ([], expr);
        }
    }

    // Lowers a call expression: recurses into callee + args, auto-wraps variant arm arguments,
    // and rewrites a call-form variant construction into a CreatorExpression.
    private (List<Statement> Hoisted, Expression Expr) LowerCallExpr(CallExpression call,
        Expression expr)
    {
        var hoisted = new List<Statement>();
        var (calleeH, loweredCallee) = LowerExpr(call.Callee);
        hoisted.AddRange(calleeH);

        var args = new List<Expression>(capacity: call.Arguments.Count);
        bool argsChanged = false;
        // Auto-wrap an arm value passed to a VARIANT parameter (`tag(s: 9_s32)` where `tag`
        // takes a `Shape`) — the same rewrite the declaration auto-wrap uses, keyed on the
        // resolved routine's parameter type. Positional args map by index, named by name.
        RoutineInfo? callRoutine = call.ResolvedRoutine;
        int posArgIdx = 0;
        foreach (Expression arg in call.Arguments)
        {
            // Preserve NamedArgumentExpression wrappers -- codegen uses arg names to detect
            // direct field constructors (e.g., Point(x: 1, y: 2) vs CStr(from: v)).
            // Only lower the inner value expression, not the wrapper itself.
            if (arg is NamedArgumentExpression namedArg)
            {
                var (h, loweredValue) = LowerExpr(namedArg.Value);
                hoisted.AddRange(h);
                TypeInfo? paramType = callRoutine?.Parameters
                    .FirstOrDefault(predicate: p => p.Name == namedArg.Name)?.Type;
                Expression wrappedValue =
                    TryWrapVariantArm(targetType: paramType, init: loweredValue) ?? loweredValue;
                Expression loweredNamed = ReferenceEquals(wrappedValue, namedArg.Value)
                    ? namedArg
                    : namedArg with { Value = wrappedValue };
                args.Add(loweredNamed);
                if (!ReferenceEquals(loweredNamed, arg)) argsChanged = true;
            }
            else
            {
                var (h, lowered) = LowerExpr(arg);
                hoisted.AddRange(h);
                TypeInfo? paramType =
                    callRoutine != null && posArgIdx < callRoutine.Parameters.Count
                        ? callRoutine.Parameters[index: posArgIdx].Type
                        : null;
                Expression wrapped =
                    TryWrapVariantArm(targetType: paramType, init: lowered) ?? lowered;
                args.Add(wrapped);
                if (!ReferenceEquals(wrapped, arg)) argsChanged = true;
            }

            posArgIdx++;
        }

        // Variant construction via the call form: `Inner(7_s32)` / `Inner(none)`. SA leaves
        // these as CallExpressions with ConstructedType=<variant> but no create routine, so
        // codegen would emit a bogus `call @Inner`. Rewrite to the variant CreatorExpression
        // that EmitVariantConstruction handles (the same shape the assignment auto-wrap uses).
        if (TryRewriteVariantCallConstruction(call: call, args: args) is { } variantCreator)
            return (hoisted, variantCreator);

        if (hoisted.Count == 0 && !argsChanged
            && ReferenceEquals(loweredCallee, call.Callee))
            return ([], expr);
        return (hoisted, call with { Callee = loweredCallee, Arguments = args });
    }

    // Lowers a member expression, folding choice/flags member access to a literal where applicable.
    private (List<Statement> Hoisted, Expression Expr) LowerMemberExpr(MemberExpression mem,
        Expression expr)
    {
        // Fold choice case member access (e.g. Direction.NORTH, someVar.NORTH) -> int literal
        if (mem.Object.ResolvedType is ChoiceTypeInfo choiceType)
        {
            ChoiceCaseInfo? caseInfo = choiceType.Cases
                .FirstOrDefault(c => c.Name == mem.MemberName);
            if (caseInfo != null)
                return ([], new LiteralExpression(
                    Value: caseInfo.ComputedValue,
                    LiteralType: TokenType.S32Literal,
                    Location: mem.Location)
                    { ResolvedType = mem.ResolvedType ?? choiceType });
        }

        // Fold flags member access (e.g. Perms.READ) -> bitmask literal
        if (mem.Object.ResolvedType is FlagsTypeInfo flagsType)
        {
            FlagsMemberInfo? memberInfo = flagsType.Members
                .FirstOrDefault(m => m.Name == mem.MemberName);
            if (memberInfo != null)
                return ([], new LiteralExpression(
                    Value: 1UL << memberInfo.BitPosition,
                    LiteralType: TokenType.U64Literal,
                    Location: mem.Location)
                    { ResolvedType = mem.ResolvedType ?? flagsType });
        }

        var (h, lowered) = LowerExpr(mem.Object);
        if (h.Count == 0 && ReferenceEquals(lowered, mem.Object)) return ([], expr);
        return (h, mem with { Object = lowered });
    }

    // Lowers a compound assignment (x += y) to an in-place member call when available, else the
    // fallback `x = x OP y; x`.
    private (List<Statement> Hoisted, Expression Expr) LowerCompoundAssignment(
        CompoundAssignmentExpression compound)
    {
        string? inPlaceName = compound.Operator.GetInPlaceMemberRoutineName();
        var (targetH, loweredTarget) = LowerExpr(compound.Target);
        var (valueH, loweredValue) = LowerExpr(compound.Value);
        var hoisted = new List<Statement>(capacity: targetH.Count + valueH.Count + 1);
        hoisted.AddRange(targetH);
        hoisted.AddRange(valueH);
        SourceLocation loc = compound.Location;
        // Try in-place memberRoutine first (iadd, isub, etc.)
        if (inPlaceName != null && loweredTarget.ResolvedType != null &&
            ctx.Registry.LookupMemberRoutine(type: loweredTarget.ResolvedType, memberRoutineName: inPlaceName) != null)
        {
            var inPlaceCall = new CallExpression(
                Callee: new MemberExpression(
                    Object: loweredTarget,
                    MemberName: inPlaceName,
                    Location: loc),
                Arguments: [new NamedArgumentExpression(Name: "you", Value: loweredValue, Location: loc)],
                Location: loc) { ResolvedType = compound.ResolvedType };
            return (hoisted, inPlaceCall);
        }
        // Fallback: hoist x = x OP y; return x
        var binExpr = new BinaryExpression(
            Left: loweredTarget,
            Operator: compound.Operator,
            Right: loweredValue,
            Location: loc) { ResolvedType = compound.ResolvedType };
        hoisted.Add(new AssignmentStatement(Target: loweredTarget, Value: binExpr, Location: loc));
        return (hoisted, loweredTarget);
    }

    // D-AST-6: hoist a conditional to `var _cif_N: T; if cond { _cif_N = a } else { _cif_N = b }`.
    private (List<Statement> Hoisted, Expression Expr) LowerConditionalExpr(ConditionalExpression cond)
    {
        // ResolvedType must be set -- SA annotates user ternaries, and synthesized
        // ConditionalExpression nodes (from DerivedOperatorPass) are explicitly typed.
        // Prefer a concrete (non-generic-definition) candidate: SA types a conditional from
        // its TRUE branch, and in a monomorphized body `if e==0 then me else …` the cond node's
        // own ResolvedType can keep the generic self-type `UnpackedFloat[M,L,W]` (the
        // rewriter concretizes the `me` IDENTIFIER but not the conditional node it feeds). A
        // generic-definition record lowers to `ptr` (GetLlvmType), mistyping the `_cif` slot —
        // so fall through to a branch type that the rewriter DID concretize.
        TypeInfo? resultType = FirstConcrete(cond.ResolvedType,
            cond.TrueExpression.ResolvedType, cond.FalseExpression.ResolvedType);
        if (resultType == null)
            throw new InvalidOperationException(
                $"ConditionalExpression reached ExpressionLoweringPass without a resolved type " +
                $"at {cond.Location}. Semantic verifier must annotate all " +
                $"ConditionalExpression nodes.");

        var (condH, loweredCond) = LowerExpr(cond.Condition);
        var (trueH, loweredTrue) = LowerExpr(cond.TrueExpression);
        var (falseH, loweredFalse) = LowerExpr(cond.FalseExpression);
        string tempName = NextTempName("cif");
        SourceLocation loc = cond.Location;

        var hoisted = new List<Statement>(capacity: condH.Count + 2);
        hoisted.AddRange(condH);
        AddTempVarUninit(hoisted, tempName, resultType, loc);

        Expression tempRef = MakeRef(tempName, resultType, loc);

        Statement thenBody = trueH.Count > 0
            ? new BlockStatement(
                Statements: [..trueH,
                    new AssignmentStatement(Target: tempRef, Value: loweredTrue,
                        Location: loc)],
                Location: loc)
            : new AssignmentStatement(Target: tempRef, Value: loweredTrue, Location: loc);

        Statement elseBody = falseH.Count > 0
            ? new BlockStatement(
                Statements: [..falseH,
                    new AssignmentStatement(Target: tempRef, Value: loweredFalse,
                        Location: loc)],
                Location: loc)
            : new AssignmentStatement(Target: tempRef, Value: loweredFalse, Location: loc);

        hoisted.Add(new IfStatement(
            Condition: loweredCond,
            ThenStatement: thenBody,
            ElseStatement: elseBody,
            Location: loc));

        return (hoisted, tempRef);
    }

    // Lowers a tuple literal to a `Tuple` record CreatorExpression (item0/item1/... members).
    private (List<Statement> Hoisted, Expression Expr) LowerTupleLiteral(TupleLiteralExpression tuple)
    {
        var hoisted = new List<Statement>();
        var elems = new List<Expression>(capacity: tuple.Elements.Count);
        foreach (Expression el in tuple.Elements)
        {
            var (h, lowered) = LowerExpr(el);
            hoisted.AddRange(h);
            elems.Add(lowered);
        }

        if (tuple.ResolvedType is not TupleTypeInfo tupleType)
            throw new InvalidOperationException(
                $"TupleLiteralExpression has no resolved TupleTypeInfo at {tuple.Location}.");

        var memberVars = new List<(string Name, Expression Value)>(capacity: elems.Count);
        for (int i = 0; i < elems.Count; i++)
            memberVars.Add(($"item{i}", elems[i]));
        var creator = new CreatorExpression(
            TypeName: tupleType.Name,
            TypeArguments: null,
            MemberVariables: memberVars,
            Location: tuple.Location)
        { ResolvedType = tupleType };
        return (hoisted, creator);
    }

    // D-AST-10: hoist a when-expression to `var _wres_N: T; WhenStatement; replace with _wres_N`.
    private (List<Statement> Hoisted, Expression Expr) LowerWhenExpr(WhenExpression whenExpr,
        Expression expr)
    {
        // Skip hoisting if the result type is unknown (e.g., unanalyzed stdlib bodies).
        if (whenExpr.ResolvedType == null)
            return ([], expr);

        TypeInfo? resultType = whenExpr.ResolvedType;
        string tempName = NextTempName("wres");
        SourceLocation loc = whenExpr.Location;

        var hoisted = new List<Statement>();

        // Lower the subject expression if present.
        Expression? loweredSubject = null;
        if (whenExpr.Expression != null)
        {
            var (subjH, ls) = LowerExpr(whenExpr.Expression);
            hoisted.AddRange(subjH);
            loweredSubject = ls;
        }

        // Declare result temp.
        AddTempVarUninit(hoisted, tempName, resultType, loc);
        Expression tempRef = MakeRef(tempName, resultType, loc);

        // Build new clauses: body of each clause becomes body + assignment to _wres_N.
        var clauses = new List<WhenClause>(capacity: whenExpr.Clauses.Count);
        foreach (WhenClause c in whenExpr.Clauses)
        {
            // The clause body is an expression -- wrap in ExpressionStatement or
            // AssignmentStatement. If the body is a BlockExpression, extract its last
            // expression as the value; otherwise treat the clause body directly.
            Statement clauseBody;
            if (c.Body is ExpressionStatement { Expression: var clauseExpr })
            {
                var (h, loweredClauseExpr) = LowerExpr(clauseExpr);
                Statement assignment = new AssignmentStatement(
                    Target: tempRef, Value: loweredClauseExpr, Location: loc);
                clauseBody = h.Count > 0
                    ? new BlockStatement(
                        Statements: [..h, assignment],
                        Location: loc)
                    : assignment;
            }
            else
            {
                // Body is already a statement; run LowerStatementFull on it.
                clauseBody = LowerStatementFull(c.Body);
            }
            clauses.Add(c with { Body = clauseBody });
        }

        // Subjectless (condition-based) when-expressions have no subject to lower;
        // mirror ParseWhenStatement and synthesize a Bool `true` subject — EmitWhen
        // unconditionally emits the subject expression.
        Expression whenSubject = loweredSubject ?? new LiteralExpression(
            Value: true,
            LiteralType: TokenType.True,
            Location: loc) { ResolvedType = ctx.Registry.LookupType(name: "Bool") };

        ProducedWhenStatement = true;
        hoisted.Add(new WhenStatement(
            Expression: whenSubject,
            Clauses: clauses,
            Location: loc));

        return (hoisted, tempRef);
    }

    // Folds bare flag-context / choice-case identifiers to their literal value.
    private (List<Statement> Hoisted, Expression Expr) LowerIdentifierExpr(IdentifierExpression id,
        Expression expr)
    {
        // Fold bare flag-context identifiers (e.g. a bare `READ` in a flags test)
        // -> bitmask literal. SA stamps ResolvedFlagsBit when it resolves a bare
        // identifier against a flag context.
        if (id.ResolvedFlagsBit is int bit && id.ResolvedType is FlagsTypeInfo)
            return ([], new LiteralExpression(
                Value: 1UL << bit,
                LiteralType: TokenType.U64Literal,
                Location: id.Location)
                { ResolvedType = id.ResolvedType });

        // Fold standalone choice case identifiers (e.g. ME_SMALL) -> int literal
        var choiceCase = ctx.Registry.LookupChoiceCase(caseName: id.Name);
        if (choiceCase != null)
            return ([], new LiteralExpression(
                Value: choiceCase.Value.CaseInfo.ComputedValue,
                LiteralType: TokenType.S32Literal,
                Location: id.Location)
                { ResolvedType = id.ResolvedType ?? choiceCase.Value.ChoiceType });
        return ([], expr);
    }

    // Maps an UndecidedInteger literal's SA-resolved type to its concrete integer LiteralType.
    private static TokenType ResolveUndecidedIntegerLiteral(LiteralExpression undecInt) =>
        undecInt.ResolvedType?.Name switch
        {
            "S8"      => TokenType.S8Literal,
            "S16"     => TokenType.S16Literal,
            "S32"     => TokenType.S32Literal,
            "S128"    => TokenType.S128Literal,
            "S256"    => TokenType.S256Literal,
            "U8"      => TokenType.U8Literal,
            "U16"     => TokenType.U16Literal,
            "U32"     => TokenType.U32Literal,
            "U64"     => TokenType.U64Literal,
            "U128"    => TokenType.U128Literal,
            "U256"    => TokenType.U256Literal,
            "Address" => TokenType.AddressLiteral,
            "Integer" => TokenType.IntegerLiteral,
            _         => TokenType.S64Literal  // This should be language specific: Suflae should use IntegerLiteral
        };

    // Maps an UndecidedDecimal literal's SA-resolved type to its concrete decimal LiteralType.
    private static TokenType ResolveUndecidedDecimalLiteral(LiteralExpression undecDec) =>
        undecDec.ResolvedType?.Name switch
        {
            "F16"     => TokenType.F16Literal,
            "F32"     => TokenType.F32Literal,
            "F128"    => TokenType.F128Literal,
            "D32"     => TokenType.D32Literal,
            "D64"     => TokenType.D64Literal,
            "D128"    => TokenType.D128Literal,
            "Decimal" => TokenType.DecimalLiteral,
            _         => TokenType.F64Literal  // This should be language specific: Suflae should use DecimalLiteral
        };

    /// <summary>
    /// Rewrites a call-form variant construction (<c>Inner(7_s32)</c> / <c>Inner(none)</c>) into the
    /// variant <see cref="CreatorExpression"/> codegen expects. Returns null when the call is not a
    /// bodyless single-argument variant construction or the argument matches no arm.
    /// </summary>
    private CreatorExpression? TryRewriteVariantCallConstruction(CallExpression call,
        List<Expression> args)
    {
        if (call is not { ConstructedType: VariantTypeInfo callVariant, ResolvedRoutine: null }
            || args.Count != 1)
            return null;

        Expression vArg = args[index: 0] is NamedArgumentExpression vna ? vna.Value : args[index: 0];
        string? armName = null;
        if (vArg is LiteralExpression { LiteralType: TokenType.NoneValue })
        {
            if (callVariant.Members.Any(predicate: m => m.IsNone)) armName = "None";
        }
        else if (vArg.ResolvedType is { } vArgType)
        {
            VariantMemberInfo? m = FindVariantMember(callVariant, vArgType);
            if (m != null) armName = m.IsNone ? "None" : m.Type!.Name;
        }
        if (armName == null) return null;

        return new CreatorExpression(
            TypeName: callVariant.Name,
            TypeArguments: null,
            MemberVariables: [(armName, vArg)],
            Location: call.Location)
        {
            ResolvedType = callVariant,
            ConstructedType = callVariant,
        };
    }

    // --- Flags-test & range lowerings --------------------------------------------

    /// <summary>
    /// OR-folds the bit positions of <paramref name="flagNames"/> into a mask against
    /// <paramref name="flagsType"/>. Unknown names contribute nothing.
    /// </summary>
    private static ulong FlagMaskFor(FlagsTypeInfo flagsType, IEnumerable<string>? flagNames)
    {
        ulong mask = 0;
        if (flagNames == null) return mask;
        foreach (string flagName in flagNames)
        {
            FlagsMemberInfo? m = flagsType.Members.FirstOrDefault(x => x.Name == flagName);
            if (m != null) mask |= 1UL << m.BitPosition;
        }
        return mask;
    }

    /// <summary>
    /// Lowers a flags test (<c>x is READ and WRITE</c> / <c>x isnot …</c>) to a bitmask
    /// comparison expression, folding in an optional excluded-flags check.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerFlagsTest(FlagsTestExpression flagsTest)
    {
        var (subjH, loweredSubj) = LowerExpr(flagsTest.Subject);
        SourceLocation loc = flagsTest.Location;
        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        if (loweredSubj.ResolvedType is not FlagsTypeInfo flagsType
            || u64Type == null || boolType == null)
            return (subjH, flagsTest with { Subject = loweredSubj });

        ulong testMask = FlagMaskFor(flagsType: flagsType, flagNames: flagsTest.TestFlags);
        ulong excludedMask = FlagMaskFor(flagsType: flagsType, flagNames: flagsTest.ExcludedFlags);

        var maskLit = new LiteralExpression(
            Value: testMask, LiteralType: TokenType.U64Literal, Location: loc)
            { ResolvedType = u64Type };
        var zeroLit = new LiteralExpression(
            Value: 0UL, LiteralType: TokenType.U64Literal, Location: loc)
            { ResolvedType = u64Type };

        Expression bitResult = flagsTest.Kind switch
        {
            FlagsTestKind.Is when flagsTest.Connective == FlagsTestConnective.And =>
                new BinaryExpression(
                    Left: new BinaryExpression(
                        Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                        Right: maskLit, Location: loc) { ResolvedType = u64Type },
                    Operator: BinaryOperator.Equal, Right: maskLit, Location: loc)
                    { ResolvedType = boolType },
            FlagsTestKind.Is =>
                new BinaryExpression(
                    Left: new BinaryExpression(
                        Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                        Right: maskLit, Location: loc) { ResolvedType = u64Type },
                    Operator: BinaryOperator.NotEqual, Right: zeroLit, Location: loc)
                    { ResolvedType = boolType },
            FlagsTestKind.IsNot =>
                new BinaryExpression(
                    Left: new BinaryExpression(
                        Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                        Right: maskLit, Location: loc) { ResolvedType = u64Type },
                    Operator: BinaryOperator.NotEqual, Right: maskLit, Location: loc)
                    { ResolvedType = boolType },
            _ =>
                new BinaryExpression(
                    Left: new BinaryExpression(
                        Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                        Right: maskLit, Location: loc) { ResolvedType = u64Type },
                    Operator: BinaryOperator.Equal, Right: maskLit, Location: loc)
                    { ResolvedType = boolType }
        };

        if (excludedMask > 0)
        {
            var excLit = new LiteralExpression(
                Value: excludedMask, LiteralType: TokenType.U64Literal, Location: loc)
                { ResolvedType = u64Type };
            var excCheck = new BinaryExpression(
                Left: new BinaryExpression(
                    Left: loweredSubj, Operator: BinaryOperator.BitwiseAnd,
                    Right: excLit, Location: loc) { ResolvedType = u64Type },
                Operator: BinaryOperator.Equal, Right: zeroLit, Location: loc)
                { ResolvedType = boolType };
            bitResult = new BinaryExpression(
                Left: bitResult, Operator: BinaryOperator.And,
                Right: excCheck, Location: loc) { ResolvedType = boolType };
        }

        return (subjH, bitResult);
    }

    /// <summary>
    /// Builds the step expression for a range: the explicit step if present, otherwise a default
    /// of 1 (built via <c>T.from_literal("1")</c> for record element types, else a raw S64 literal).
    /// Appends any hoisted statements from lowering an explicit step to <paramref name="hoisted"/>.
    /// </summary>
    private Expression LowerRangeStep(RangeExpression range, TypeInfo? elemType,
        SourceLocation loc, ref List<Statement> hoisted)
    {
        if (range.Step != null)
        {
            var (stepH, loweredStep) = LowerExpr(range.Step);
            hoisted = Concat(hoisted, stepH);
            return loweredStep;
        }

        // Default step of 1. LiteralLoweringPass has ALREADY run, so a raw literal stamped
        // with a record element type (Suflae's arbitrary-precision `Integer`/`Decimal`)
        // would reach codegen as an invalid `%Record.Integer 1` constant. When the element
        // type has a `from_literal` constructor (Integer/Decimal), build `T.from_literal(
        // text: "1")` — mirroring how LiteralLoweringPass lowers the start/end literals.
        // Scalar element types (RF's S64) have no `from_literal` and keep the raw literal.
        RoutineInfo? stepFromLiteral = elemType != null
            ? ctx.Registry.LookupMemberRoutine(type: elemType, memberRoutineName: "from_literal")
            : null;
        if (elemType != null && stepFromLiteral != null)
        {
            var stepText = new LiteralExpression(
                Value: "1", LiteralType: TokenType.TextLiteral, Location: loc)
                { ResolvedType = ctx.Registry.LookupType(name: "Text") };
            return new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: elemType.Name, Location: loc)
                        { ResolvedType = elemType },
                    MemberName: "from_literal", Location: loc),
                Arguments: [new NamedArgumentExpression(Name: "text", Value: stepText,
                    Location: loc)],
                Location: loc)
                { ResolvedRoutine = stepFromLiteral, ResolvedType = stepFromLiteral.ReturnType };
        }

        return new LiteralExpression(
            Value: 1L, LiteralType: TokenType.S64Literal, Location: loc)
            { ResolvedType = elemType };
    }

    /// <summary>
    /// Lowers a range expression (<c>a..b</c> / <c>a..=b step s</c>) to a
    /// <c>Range[T](start, end, step, inclusive)</c> creator.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerRange(RangeExpression range)
    {
        var (startH, loweredStart) = LowerExpr(range.Start);
        var (endH, loweredEnd) = LowerExpr(range.End);
        var hoisted = Concat(startH, endH);
        SourceLocation loc = range.Location;
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? elemType = loweredStart.ResolvedType ?? loweredEnd.ResolvedType;

        Expression stepExpr = LowerRangeStep(range: range, elemType: elemType, loc: loc,
            hoisted: ref hoisted);

        var inclusiveLit = new LiteralExpression(
            Value: !range.IsExclusive,
            LiteralType: !range.IsExclusive ? TokenType.True : TokenType.False,
            Location: loc) { ResolvedType = boolType };

        // Build TypeArguments from resolved element type so EmitConstructorCall
        // uses the concrete Range[T] definition instead of the generic definition.
        // Prefer the type arg from the resolved Range[T] type, then fall back to
        // the inferred element type from the start/end sub-expressions.
        TypeInfo? resolvedElem = range.ResolvedType?.TypeArguments is { Count: > 0 }
            ? range.ResolvedType.TypeArguments[0]
            : elemType;

        if (resolvedElem == null)
            throw new InvalidOperationException(
                $"RangeExpression at {range.Location} has no resolvable element type. " +
                "Semantic verifier must annotate the start/end expressions before " +
                "ExpressionLoweringPass runs.");

        List<TypeExpression> typeArgs = [TypeInfoToExpr(type: resolvedElem, loc: loc)];

        return (hoisted, new CreatorExpression(
            TypeName: "Range",
            TypeArguments: typeArgs,
            MemberVariables: [
                ("start", loweredStart),
                ("end", loweredEnd),
                ("step", stepExpr),
                ("inclusive", inclusiveLit)
            ],
            Location: loc) { ResolvedType = range.ResolvedType });
    }

    // --- Collection literal lowerings --------------------------------------------

    /// <summary>
    /// Lowers a list literal to: var _lit_N = Collection(); _lit_N.add_last(e)...
    /// Array[T,N] and BitArray[N] are inline IR -- kept as ListLiteralExpression for codegen.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerListLiteral(ListLiteralExpression list)
    {
        TypeInfo? resolvedType = list.ResolvedType;
        // Unwrap transparent ownership wrappers (T, Retained[T], Tracked[T]) so that
        // Owned[List[S64]] uses "List" as baseName, not "Owned".
        TypeInfo? listType = UnwrapOwnershipWrapper(resolvedType) ?? resolvedType;
        SourceLocation loc = list.Location;

        string baseName = GetCollectionBaseName(listType) ?? "List";

        // Array/BitArray are pure inline IR (insertvalue) -- pass through with element recursion only.
        if (baseName is "Array" or "BitArray")
            return LowerInlineArrayLiteral(list: list);

        // A ListLiteral-conforming type lowers to `Type.from_literal(a, b, c)` (SA resolved the
        // monomorphized builder). The literal elements are packed into an inline `Array[T, K]`.
        if (list.ResolvedLiteralBuilder is { Parameters.Count: >= 1 } listBuilder)
        {
            var (h, elems) = LowerElements(elements: list.Elements);
            return (h, MakeFromLiteralCall(builder: listBuilder, arrayElements: elems,
                literalResultType: resolvedType ?? listType, loc: loc));
        }

        if (listType == null) return ([], list);

        string tempName = NextTempName("lit");
        var hoisted2 = new List<Statement>();

        AddTempVar(hoisted2, tempName, listType, MakeZeroArgCreator(listType, baseName, loc), loc);
        Expression colRef = MakeRef(tempName, listType, loc);

        // List, CircularList, BitList append at the end; everything else uses add().
        string addMemberRoutine = baseName is "List" or "CircularList" or "BitList"
            ? Resolution.RuntimeContract.Collection.AddLast
            : Resolution.RuntimeContract.Collection.Add;

        foreach (Expression elem in list.Elements)
        {
            var (h, lowered) = LowerExpr(elem);
            hoisted2.AddRange(h);
            hoisted2.Add(MakeCollectionAddCall(colRef, listType, addMemberRoutine, [lowered], loc));
        }

        // If the original expression was wrapped in Owned/Retained/Tracked, restore that
        // wrapper type on the returned reference so downstream code sees the correct type.
        Expression result = resolvedType != null && !ReferenceEquals(resolvedType, listType)
            ? new IdentifierExpression(Name: tempName, Location: loc) { ResolvedType = resolvedType }
            : colRef;
        return (hoisted2, result);
    }

    /// <summary>
    /// Lowers an inline Array/BitArray literal: recurse into elements only (pure insertvalue IR),
    /// keeping the node as a ListLiteralExpression for codegen.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerInlineArrayLiteral(ListLiteralExpression list)
    {
        var hoisted = new List<Statement>();
        var elems = new List<Expression>(capacity: list.Elements.Count);
        bool changed = false;
        foreach (Expression el in list.Elements)
        {
            var (h, lowered) = LowerExpr(el);
            hoisted.AddRange(h);
            elems.Add(lowered);
            if (!ReferenceEquals(lowered, el)) changed = true;
        }
        if (!changed && hoisted.Count == 0) return ([], list);
        return (hoisted, list with { Elements = elems });
    }

    /// <summary>
    /// Lowers a set literal to: var _lit_N = Set(); _lit_N.add(e)...
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerSetLiteral(SetLiteralExpression set)
    {
        TypeInfo? resolvedType = set.ResolvedType;
        SourceLocation loc = set.Location;
        if (resolvedType == null) return ([], set);

        // Unwrap Owned/Retained/Tracked so the temp var holds the inner collection (Set/SortedSet/
        // SecureSet/...) instead of the wrapper. Without this, MakeCollectionAddCall resolves
        // `.add` against Owned[…] (which has no add) and codegen throws "no resolved member routine".
        TypeInfo setType = UnwrapOwnershipWrapper(resolvedType) ?? resolvedType;
        string baseName = GetCollectionBaseName(setType) ?? "Set";

        // A SetLiteral-conforming type lowers to `Type.from_literal(a, b, c)`.
        if (set.ResolvedLiteralBuilder is { Parameters.Count: >= 1 } setBuilder)
        {
            var (h, elems) = LowerElements(elements: set.Elements);
            return (h, MakeFromLiteralCall(builder: setBuilder, arrayElements: elems,
                literalResultType: resolvedType, loc: loc));
        }

        string tempName = NextTempName("lit");
        var hoisted = new List<Statement>();

        AddTempVar(hoisted, tempName, setType, MakeZeroArgCreator(setType, baseName, loc), loc);
        Expression colRef = MakeRef(tempName, setType, loc);

        foreach (Expression elem in set.Elements)
        {
            var (h, lowered) = LowerExpr(elem);
            hoisted.AddRange(h);
            hoisted.Add(MakeCollectionAddCall(colRef, setType, Resolution.RuntimeContract.Collection.Add, [lowered], loc));
        }

        Expression result = !ReferenceEquals(resolvedType, setType)
            ? new IdentifierExpression(Name: tempName, Location: loc) { ResolvedType = resolvedType }
            : colRef;
        return (hoisted, result);
    }

    /// <summary>
    /// Lowers a dict literal to: var _lit_N = Dict(); _lit_N.add(k, v)...
    /// PriorityQueue {priority: element} -> add(element, priority) (arguments reversed).
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerDictLiteral(DictLiteralExpression dict)
    {
        TypeInfo? resolvedType = dict.ResolvedType;
        SourceLocation loc = dict.Location;
        if (resolvedType == null) return ([], dict);

        // Unwrap Owned/Retained/Tracked — see LowerSetLiteral for the rationale.
        TypeInfo dictType = UnwrapOwnershipWrapper(resolvedType) ?? resolvedType;
        string baseName = GetCollectionBaseName(dictType) ?? "Dict";

        // A DictLiteral-conforming type lowers to `Type.from_literal(DictEntry(k, v), …)`.
        if (dict.ResolvedLiteralBuilder is { Parameters.Count: >= 1 } dictBuilder
            && dictBuilder.Parameters[index: 0].Type.TypeArguments is [{ } entryElementType, ..])
        {
            var hoistedB = new List<Statement>();
            var entries = new List<Expression>(capacity: dict.Pairs.Count);
            foreach ((Expression key, Expression value) in dict.Pairs)
            {
                var (kh, lk) = LowerExpr(key);
                var (vh, lv) = LowerExpr(value);
                hoistedB.AddRange(kh);
                hoistedB.AddRange(vh);
                entries.Add(item: MakeDictEntry(entryType: entryElementType, key: lk, value: lv, loc: loc));
            }
            return (hoistedB, MakeFromLiteralCall(builder: dictBuilder, arrayElements: entries,
                literalResultType: resolvedType, loc: loc));
        }

        string tempName = NextTempName("lit");
        var hoisted = new List<Statement>();

        AddTempVar(hoisted, tempName, dictType, MakeZeroArgCreator(dictType, baseName, loc), loc);
        Expression colRef = MakeRef(tempName, dictType, loc);

        bool isPriorityQueue = baseName == "PriorityQueue";

        foreach ((Expression key, Expression value) in dict.Pairs)
        {
            var (keyH, loweredKey) = LowerExpr(key);
            var (valH, loweredVal) = LowerExpr(value);
            hoisted.AddRange(keyH);
            hoisted.AddRange(valH);

            // PriorityQueue literal: {priority: element} -> add(element, priority)
            List<Expression> args = isPriorityQueue
                ? [loweredVal, loweredKey]
                : [loweredKey, loweredVal];
            hoisted.Add(MakeCollectionAddCall(colRef, dictType, Resolution.RuntimeContract.Collection.Add, args, loc));
        }

        Expression result = !ReferenceEquals(resolvedType, dictType)
            ? new IdentifierExpression(Name: tempName, Location: loc) { ResolvedType = resolvedType }
            : colRef;
        return (hoisted, result);
    }

    /// <summary>
    /// Lowers a standalone dict-entry literal <c>key:value</c> to a
    /// <c>CreatorExpression("DictEntry", ...)</c> record constructor.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerDictEntryLiteral(
        DictEntryLiteralExpression dictEntry)
    {
        var (keyH, loweredKey) = LowerExpr(dictEntry.Key);
        var (valH, loweredVal) = LowerExpr(dictEntry.Value);
        var hoisted = Concat(keyH, valH);

        TypeInfo? entryType = dictEntry.ResolvedType;
        if (entryType == null)
        {
            if (ReferenceEquals(loweredKey, dictEntry.Key) && ReferenceEquals(loweredVal, dictEntry.Value)
                && hoisted.Count == 0)
                return ([], dictEntry);
            return (hoisted, dictEntry with { Key = loweredKey, Value = loweredVal });
        }

        string baseName = GetCollectionBaseName(entryType) ?? "DictEntry";
        List<TypeExpression>? typeArgs = entryType.TypeArguments?.Count > 0
            ? entryType.TypeArguments.Select(t => TypeInfoToExpr(t, dictEntry.Location)).ToList()
            : null;

        return (hoisted, new CreatorExpression(
            TypeName: baseName,
            TypeArguments: typeArgs,
            MemberVariables: [("key", loweredKey), ("value", loweredVal)],
            Location: dictEntry.Location) { ResolvedType = entryType });
    }

    // --- Carrier wrap lowering ----------------------------------------------------

    /// <summary>
    /// Implicit carrier-wrap rewrite for variable declarations:
    /// <c>var m: Maybe[T] = expr</c> where <c>expr : T</c> is rewritten to
    /// <c>var m: Maybe[T] = Maybe[T](present: true, value: expr)</c>.
    /// Returns null when no wrap applies (init already matches the carrier, type
    /// annotation is absent, or carrier isn't Maybe). Mirrors the SA assignability
    /// rule that permits <c>T -&gt; Maybe[T]</c> in initializers.
    /// </summary>
    private Expression? TryWrapCarrier(TypeExpression? varType, Expression init)
    {
        if (varType is null) return null;
        TypeInfo? targetType = varType.ResolvedType;
        TypeInfo? initType = init.ResolvedType;
        if (initType is null) return null;

        // Variant auto-wrap: `var x: Number = 42_s64` where Number has an S64 arm
        // becomes a CreatorExpression that codegen routes through EmitVariantConstruction.
        if (TryWrapVariantArm(targetType: targetType, init: init) is { } wrapped)
            return wrapped;
        if (targetType is VariantTypeInfo) return null; // variant target, but not a wrappable arm

        string carrierName = LastNameSegment(varType.Name);
        // Only Maybe handled at this stage. Result/Lookup payloads need TypeLayoutPass
        // to stamp byte sizes before their construction can be synthesized.
        if (carrierName != "Maybe") return null;
        if (varType.GenericArguments is not { Count: 1 } typeArgs) return null;

        // Skip if init already produces the carrier itself (e.g. explicit Maybe[T](...)).
        string initBase = CarrierBaseName(initType);
        if (initBase == "Maybe") return null;

        // Build `Maybe[T](present: true, value: init)`. The carrier is a stdlib record
        // with named fields {present: Bool, value: T} — field-init lowering in codegen
        // handles construction via the existing record-creator path.
        var trueLit = new LiteralExpression(
            Value: true,
            LiteralType: TokenType.True,
            Location: init.Location);
        var maybeCreator = new CreatorExpression(
            TypeName: "Maybe",
            TypeArguments: typeArgs,
            MemberVariables: [(Resolution.RuntimeContract.Carrier.PresentField, trueLit), (Resolution.RuntimeContract.Carrier.ValueField, init)],
            Location: init.Location)
        {
            ResolvedType = targetType,
            ConstructedType = targetType,
        };
        return maybeCreator;
    }

    /// <summary>
    /// Member-store Maybe auto-wrap: when the assignment target is a <c>MemberExpression</c> whose
    /// resolved field type is <c>Maybe[T]</c> and the value is a bare <c>T</c> (not already a Maybe,
    /// not the <c>none</c> literal), box it into <c>Maybe[T](present: true, value: value)</c>. Returns
    /// null otherwise. Mirrors <see cref="TryWrapCarrier"/> but keys off the target member's resolved
    /// TypeInfo rather than a declared TypeExpression — replaces the hand-built { i1, T } insertvalue
    /// that codegen used to emit at the member store (D3).
    /// </summary>
    private Expression? TryWrapMemberMaybe(Expression target, Expression value)
    {
        if (target is not MemberExpression member) return null;
        TypeInfo? fieldType = member.ResolvedType;
        if (fieldType is null || CarrierBaseName(fieldType) != "Maybe") return null;
        if (fieldType.TypeArguments is not { Count: 1 }) return null;

        // The `none` literal is Maybe's zero value — leave it as-is (codegen stores zeroinitializer).
        if (value is LiteralExpression { LiteralType: TokenType.NoneValue }) return null;

        // Already a Maybe carrier — no wrap.
        TypeInfo? valueType = value.ResolvedType;
        if (valueType != null && CarrierBaseName(valueType) == "Maybe") return null;

        List<TypeExpression> typeArgs =
            [TypeInfoToExpr(type: fieldType.TypeArguments[index: 0], loc: value.Location)];
        var trueLit = new LiteralExpression(
            Value: true, LiteralType: TokenType.True, Location: value.Location)
            { ResolvedType = ctx.Registry.LookupType(name: "Bool") };
        return new CreatorExpression(
            TypeName: "Maybe",
            TypeArguments: typeArgs,
            MemberVariables:
            [
                (Resolution.RuntimeContract.Carrier.PresentField, trueLit),
                (Resolution.RuntimeContract.Carrier.ValueField, value)
            ],
            Location: value.Location)
        {
            ResolvedType = fieldType,
            ConstructedType = fieldType,
        };
    }

    /// <summary>
    /// Wraps a value of a variant ARM into a variant construction when the target type is that
    /// variant (an <c>S32</c> value into a <c>Shape</c> param/slot, <c>none</c> into a variant that
    /// has a None arm). Returns null when the target isn't a variant, the value already IS the
    /// variant (passthrough — including a different variant that is itself an arm must still wrap),
    /// or it matches no arm. Guarded by the declaration auto-wrap and the call-argument auto-wrap.
    /// </summary>
    private Expression? TryWrapVariantArm(TypeInfo? targetType, Expression init)
    {
        if (targetType is not VariantTypeInfo variant) return null;

        // `= none` / `f(none)` → the variant's None arm if it has one.
        if (init is LiteralExpression { LiteralType: TokenType.NoneValue })
        {
            return variant.Members.Any(predicate: m => m.IsNone)
                ? MakeVariantArmCreator(variant: variant, armName: "None", init: init)
                : null;
        }

        TypeInfo? initType = init.ResolvedType;
        if (initType is null || initType.FullName == variant.FullName) return null;

        VariantMemberInfo? member = FindVariantMember(variant: variant, initType: initType);
        return member is null
            ? null
            : MakeVariantArmCreator(variant: variant,
                armName: member.IsNone ? "None" : member.Type!.Name, init: init);
    }

    private static CreatorExpression MakeVariantArmCreator(VariantTypeInfo variant, string armName,
        Expression init)
        => new(TypeName: variant.Name, TypeArguments: null,
            MemberVariables: [(armName, init)], Location: init.Location)
        {
            ResolvedType = variant, ConstructedType = variant
        };

    private static VariantMemberInfo? FindVariantMember(VariantTypeInfo variant, TypeInfo initType)
    {
        foreach (VariantMemberInfo m in variant.Members)
        {
            if (m.IsNone) continue;
            if (m.Type is null) continue;
            if (m.Type.Name == initType.Name || m.Type.FullName == initType.FullName)
                return m;
        }
        return null;
    }

    private static string LastNameSegment(string name)
    {
        int dot = name.LastIndexOf('.');
        return dot >= 0 ? name[(dot + 1)..] : name;
    }

    private static string CarrierBaseName(TypeInfo type)
    {
        string raw = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.Name,
        };
        return LastNameSegment(raw);
    }

    // --- Collection lowering helpers ----------------------------------------------

    private static TypeInfo? UnwrapOwnershipWrapper(TypeInfo? type)
    {
        if (type is WrapperTypeInfo { Name: Resolution.RuntimeContract.Owned or Resolution.RuntimeContract.Retained or Resolution.RuntimeContract.Tracked } w)
            return w.InnerType;
        // T / Retained[T] / Tracked[T] are declared as `record T` in stdlib, so
        // they surface as RecordTypeInfo, not WrapperTypeInfo. Match by base name + single
        // TypeArgument and return the inner collection so downstream lowering sees the actual
        // base (BitList, SortedSet, …) instead of the Owned envelope.
        if (type is RecordTypeInfo { TypeArguments: { Count: 1 } recArgs } rec
            && (rec.GenericDefinition?.Name is Resolution.RuntimeContract.Owned or Resolution.RuntimeContract.Retained or Resolution.RuntimeContract.Tracked
                || GetCollectionBaseName(rec) is Resolution.RuntimeContract.Owned or Resolution.RuntimeContract.Retained or Resolution.RuntimeContract.Tracked))
        {
            return recArgs[0];
        }
        return null;
    }

    private static string GetCollectionBaseName(TypeInfo? type)
    {
        if (type == null) return "Collection";
        return type switch
        {
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => type.BareName
        };
    }

    private static CreatorExpression MakeZeroArgCreator(TypeInfo collectionType, string baseName,
        SourceLocation loc)
    {
        List<TypeExpression>? typeArgs = collectionType.TypeArguments?.Count > 0
            ? collectionType.TypeArguments.Select(t => TypeInfoToExpr(t, loc)).ToList()
            : null;

        return new CreatorExpression(
            TypeName: baseName,
            TypeArguments: typeArgs,
            MemberVariables: [],
            Location: loc)
        {
            ResolvedType = collectionType,
            ConstructedType = collectionType,
        };
    }

    private DiscardStatement MakeCollectionAddCall(Expression receiver, TypeInfo receiverType,
        string memberRoutineName, List<Expression> args, SourceLocation loc)
    {
        RoutineInfo? memberRoutine = ctx.Registry.LookupMemberRoutine(type: receiverType, memberRoutineName: memberRoutineName);

        var callee = new MemberExpression(Object: receiver, MemberName: memberRoutineName,
            Location: loc);
        var call = new CallExpression(
            Callee: callee,
            Arguments: args,
            Location: loc)
        {
            ResolvedType = memberRoutine?.ReturnType,
            ResolvedRoutine = memberRoutine,
            LoweringKind = memberRoutine != null ? CallLoweringKind.DirectMemberRoutine : CallLoweringKind.Unknown
        };
        return new DiscardStatement(Expression: call, Location: loc);
    }

    /// <summary>Lowers a list of element expressions, collecting their hoisted statements.</summary>
    private (List<Statement> Hoisted, List<Expression> Lowered) LowerElements(List<Expression> elements)
    {
        var hoisted = new List<Statement>();
        var lowered = new List<Expression>(capacity: elements.Count);
        foreach (Expression el in elements)
        {
            var (h, low) = LowerExpr(el);
            hoisted.AddRange(h);
            lowered.Add(low);
        }
        return (hoisted, lowered);
    }

    /// <summary>
    /// Builds the `Type.from_literal(a, b, c)` call a collection literal lowers to: packs the (already
    /// lowered) elements into an inline `Array[E, K]` literal and calls the monomorphized `from_literal[K]`
    /// static builder. `from_literal` is a `common` routine (no `me`), so its single parameter is the Array.
    /// </summary>
    private Expression MakeFromLiteralCall(RoutineInfo builder, List<Expression> arrayElements,
        TypeInfo? literalResultType, SourceLocation loc)
    {
        TypeInfo arrayType = builder.Parameters[index: 0].Type; // Array[E, K]
        var arrayLit = new ListLiteralExpression(Elements: arrayElements, ElementType: null,
            Location: loc) { ResolvedType = arrayType };

        TypeInfo? ownerType = builder.OwnerType;
        var typeRef = new IdentifierExpression(Name: GetCollectionBaseName(ownerType), Location: loc)
            { ResolvedType = ownerType };
        var callee = new MemberExpression(Object: typeRef, MemberName: "from_literal", Location: loc);
        return new CallExpression(Callee: callee, Arguments: [arrayLit], Location: loc)
        {
            ResolvedRoutine = builder,
            ResolvedType = literalResultType ?? builder.ReturnType,
            LoweringKind = CallLoweringKind.DirectMemberRoutine
        };
    }

    /// <summary>Builds a `DictEntry[K, V](key: k, value: v)` record construction for a dict-literal pair.</summary>
    private static Expression MakeDictEntry(TypeInfo entryType, Expression key, Expression value,
        SourceLocation loc)
    {
        List<TypeExpression>? typeArgs = entryType.TypeArguments is { Count: > 0 } eargs
            ? eargs.Select(selector: t => TypeInfoToExpr(t, loc)).ToList()
            : null;
        return new CreatorExpression(
            TypeName: GetCollectionBaseName(entryType),
            TypeArguments: typeArgs,
            MemberVariables: [("key", key), ("value", value)],
            Location: loc) { ResolvedType = entryType };
    }

    // --- Specific hoisting lowerings ---------------------------------------------

    /// <summary>
    /// 1f-2. Lowers <c>x is T</c> / <c>x isnot T</c> for variant subjects to a
    /// <c>type_id</c> field comparison:
    /// <c>x is S64</c> -> <c>x.type_id == FNV("S64")</c>,
    /// <c>x isnot S64</c> -> <c>x.type_id != FNV("S64")</c>.
    /// None maps to tag 0.  Falls through for unresolved right-hand types.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerVariantIsExpression(BinaryExpression bin)
    {
        var (leftH, loweredLeft) = LowerExpr(bin.Left);
        bool isNot = bin.Operator == BinaryOperator.IsNot;
        SourceLocation loc = bin.Location;

        TypeInfo? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        if (u64Type == null || boolType == null) return (leftH, bin with { Left = loweredLeft });

        // Resolve the right-hand type name.
        string? typeName = bin.Right switch
        {
            IdentifierExpression id => id.Name,
            TypeExpression te => te.Name,
            _ => null
        };
        if (typeName == null) return (leftH, bin with { Left = loweredLeft });

        ulong typeId;
        if (typeName is NoneTypeName or "None")
        {
            typeId = 0;
        }
        else
        {
            TypeInfo? targetType = ctx.Registry.LookupType(name: typeName)
                ?? (bin.Right is IdentifierExpression rid ? rid.ResolvedType : null)
                ?? (bin.Right is TypeExpression rte ? rte.ResolvedType : null);
            if (targetType == null) return (leftH, bin with { Left = loweredLeft });
            typeId = TypeIdHelper.ComputeTypeId(fullName: targetType.FullName);
        }

        var typeIdAccess = new MemberExpression(
            Object: loweredLeft, MemberName: TypeIdFieldName, Location: loc) { ResolvedType = u64Type };
        var constant = new LiteralExpression(
            Value: typeId, LiteralType: TokenType.U64Literal, Location: loc)
            { ResolvedType = u64Type };
        var cmp = new BinaryExpression(
            Left: typeIdAccess,
            Operator: isNot ? BinaryOperator.NotEqual : BinaryOperator.Equal,
            Right: constant,
            Location: loc) { ResolvedType = boolType };
        return (leftH, cmp);
    }

    /// <summary>
    /// Lowers a choice discriminant test <c>a is b</c> / <c>a isnot b</c> to an S32 equality:
    /// reinterpret both choice values to their underlying S32 (<c>S32(from: _)</c>, a no-op bit view)
    /// and compare with <c>==</c> / <c>!=</c>, which OperatorLoweringPass lowers to <c>S32.eq</c> /
    /// <c>S32.ne</c> (icmp eq/ne i32). This keeps the <c>is</c> operator out of codegen.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerChoiceIsExpression(
        BinaryExpression bin, ChoiceTypeInfo choiceType)
    {
        var (leftH, loweredLeft) = LowerExpr(bin.Left);
        var (rightH, loweredRight) = LowerExpr(bin.Right);
        bool isNot = bin.Operator == BinaryOperator.IsNot;
        SourceLocation loc = bin.Location;

        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? underlying = choiceType.UnderlyingType ?? ctx.Registry.LookupType(name: "S32");
        List<Statement> hoisted = Concat(leftH, rightH);
        if (boolType == null || underlying == null)
            return (hoisted, bin with { Left = loweredLeft, Right = loweredRight });

        CreatorExpression Reinterpret(Expression inner) =>
            new(TypeName: underlying.Name,
                TypeArguments: null,
                MemberVariables: [("from", inner)],
                Location: loc)
            {
                ResolvedType = underlying, LoweringKind = CallLoweringKind.TypeConstructor
            };

        var cmp = new BinaryExpression(
            Left: Reinterpret(loweredLeft),
            Operator: isNot ? BinaryOperator.NotEqual : BinaryOperator.Equal,
            Right: Reinterpret(loweredRight),
            Location: loc) { ResolvedType = boolType };
        return (hoisted, cmp);
    }

    /// <summary>
    /// 1g. Lowers boolean short-circuit And to <see cref="ConditionalExpression"/>:
    /// <c>a and b</c> -> <c>if a { _cif = b } else { _cif = false }</c>.
    /// The right operand (<paramref name="bin"/>.Right) is NOT pre-lowered here;
    /// the ConditionalExpression case hoists its setup into the true branch only,
    /// preserving short-circuit evaluation.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerBooleanAnd(BinaryExpression bin)
    {
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        if (boolType == null) return ([], bin);

        SourceLocation loc = bin.Location;
        var (leftH, loweredLeft) = LowerExpr(bin.Left);

        var falseLit = new LiteralExpression(
            Value: false, LiteralType: TokenType.False, Location: loc)
            { ResolvedType = boolType };

        var condExpr = new ConditionalExpression(
            Condition: loweredLeft,
            TrueExpression: bin.Right,
            FalseExpression: falseLit,
            Location: loc) { ResolvedType = boolType };

        var (condH, condRef) = LowerExpr(condExpr);
        return (Concat(leftH, condH), condRef);
    }

    /// <summary>
    /// 1h. Lowers boolean short-circuit Or to <see cref="ConditionalExpression"/>:
    /// <c>a or b</c> -> <c>if a { _cif = true } else { _cif = b }</c>.
    /// The right operand is placed in the false branch only, preserving lazy evaluation.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerBooleanOr(BinaryExpression bin)
    {
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        if (boolType == null) return ([], bin);

        SourceLocation loc = bin.Location;
        var (leftH, loweredLeft) = LowerExpr(bin.Left);

        var trueLit = new LiteralExpression(
            Value: true, LiteralType: TokenType.True, Location: loc)
            { ResolvedType = boolType };

        var condExpr = new ConditionalExpression(
            Condition: loweredLeft,
            TrueExpression: trueLit,
            FalseExpression: bin.Right,
            Location: loc) { ResolvedType = boolType };

        var (condH, condRef) = LowerExpr(condExpr);
        return (Concat(leftH, condH), condRef);
    }

    /// <summary>
    /// 1i. Lowers logical not to <see cref="ConditionalExpression"/>:
    /// <c>not x</c> -> <c>if x { _cif = false } else { _cif = true }</c>.
    /// FlagsTypeInfo bitwise-not (<c>~</c>) is lowered to <c>bitnot()</c> by
    /// <see cref="OperatorLoweringPass"/> and never reaches this path.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerLogicalNot(UnaryExpression notExpr)
    {
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        if (boolType == null) return ([], notExpr);

        SourceLocation loc = notExpr.Location;
        var (h, loweredOp) = LowerExpr(notExpr.Operand);

        var trueLit = new LiteralExpression(
            Value: true, LiteralType: TokenType.True, Location: loc)
            { ResolvedType = boolType };
        var falseLit = new LiteralExpression(
            Value: false, LiteralType: TokenType.False, Location: loc)
            { ResolvedType = boolType };

        var condExpr = new ConditionalExpression(
            Condition: loweredOp,
            TrueExpression: falseLit,
            FalseExpression: trueLit,
            Location: loc) { ResolvedType = boolType };

        var (condH, condRef) = LowerExpr(condExpr);
        return (Concat(h, condH), condRef);
    }

    /// <summary>
    /// 1e. Lowers flags combination operators to plain bitwise operations:
    /// <list type="bullet">
    ///   <item><c>a and b</c> (union of active bits)      -> <c>BitwiseOr(a, b)</c></item>
    ///   <item><c>a but b</c> (bit clear: a &amp; ~b)     -> <c>BitwiseAnd(a, BitwiseNot(b))</c></item>
    /// </list>
    /// Codegen emits <c>or i64</c> / <c>and i64 ... xor i64 ..., -1</c> for these via
    /// <c>EmitPrimitiveBinaryOp</c>, making <c>EmitFlagsCombine</c> / <c>EmitBitClear</c> dead.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerFlagsCombination(BinaryExpression binary)
    {
        var (leftH, loweredLeft) = LowerExpr(binary.Left);
        var (rightH, loweredRight) = LowerExpr(binary.Right);
        var hoisted = Concat(leftH, rightH);

        var flagsType = (FlagsTypeInfo)binary.Left.ResolvedType!;

        Expression lowered;
        if (binary.Operator == BinaryOperator.And)
        {
            // flags and flags -> bitwise OR (union of active bits)
            lowered = new BinaryExpression(
                Left: loweredLeft,
                Operator: BinaryOperator.BitwiseOr,
                Right: loweredRight,
                Location: binary.Location) { ResolvedType = flagsType };
        }
        else
        {
            // flags but flags -> bitwise AND with NOT of right (bit clear)
            var notRight = new UnaryExpression(
                Operator: UnaryOperator.BitwiseNot,
                Operand: loweredRight,
                Location: binary.Location) { ResolvedType = flagsType };
            lowered = new BinaryExpression(
                Left: loweredLeft,
                Operator: BinaryOperator.BitwiseAnd,
                Right: notRight,
                Location: binary.Location) { ResolvedType = flagsType };
        }

        return (hoisted, lowered);
    }

    /// <summary>
    /// 1f. Lowers <c>x is None</c> / <c>x is None</c> / their negated forms for carriers:
    /// <list type="bullet">
    ///   <item><c>Maybe[T record] is None</c>  ->  <c>not x.present</c></item>
    ///   <item><c>Maybe[T record] isnot None</c>  ->  <c>x.present</c></item>
    ///   <item><c>Lookup[T] is None</c>  ->  <c>x.type_id == 0_u64</c></item>
    ///   <item><c>Lookup[T] isnot None</c>  ->  <c>x.type_id != 0_u64</c></item>
    /// </list>
    /// <c>Maybe[T entity]</c> absence checks are NOT lowered here (require Snatched null compare);
    /// they fall through unchanged for <c>EmitIsPattern</c> in codegen.
    /// </summary>
    /// <summary>
    /// Lowers <c>base with .field1 = v1, .field2 = v2</c> into
    /// <c>var tmp = base.assign(); tmp.field1 = v1; tmp.field2 = v2; tmp</c>. The
    /// <c>store</c> dispatch carries any per-field semantics (e.g. retains on
    /// <c>Retained[T]</c> fields) that a field-by-field constructor rebuild would skip.
    /// SA gates this in <c>AnalyzeWithExpression</c> (base type must obey Assignable).
    /// Only handles simple (non-nested, non-index) updates on RecordTypeInfo.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerWithExpression(WithExpression withExpr)
    {
        var (baseHoisted, loweredBase) = LowerExpr(withExpr.Base);
        SourceLocation loc = withExpr.Location;

        TypeInfo? baseType = withExpr.Base.ResolvedType;
        if (baseType is not RecordTypeInfo recordType)
        {
            // Not a record -- pass through unchanged.
            if (ReferenceEquals(loweredBase, withExpr.Base) && baseHoisted.Count == 0)
                return ([], withExpr);
            return (baseHoisted, withExpr with { Base = loweredBase });
        }

        // Hoist base to a temp if it isn't a trivial identifier (avoid double-eval).
        var hoisted = new List<Statement>(baseHoisted);
        Expression baseRef = HoistWithBase(loweredBase: loweredBase, baseType: baseType, loc: loc,
            hoisted: hoisted);

        // Lower each override expression up front.
        var (allSimple, loweredOverrides) = LowerWithOverrides(withExpr: withExpr, hoisted: hoisted);

        if (!allSimple)
        {
            // Nested paths or index updates -- not yet lowered; pass through.
            return (hoisted, withExpr with { Base = baseRef });
        }

        Expression copyRef = BuildWithCopy(baseRef: baseRef, baseType: baseType,
            recordType: recordType, loweredOverrides: loweredOverrides, loc: loc, hoisted: hoisted);
        return (hoisted, copyRef);
    }

    // Hoists the with-base to a temp var if it isn't already a trivial identifier (avoid double-eval),
    // returning the reference to use for the base.
    private Expression HoistWithBase(Expression loweredBase, TypeInfo baseType, SourceLocation loc,
        List<Statement> hoisted)
    {
        if (loweredBase is IdentifierExpression) return loweredBase;
        string tempName = NextTempName(prefix: "with_base");
        AddTempVar(hoisted: hoisted, name: tempName, typeHint: baseType,
            initializer: loweredBase, loc: loc);
        return new IdentifierExpression(Name: tempName, Location: loc) { ResolvedType = baseType };
    }

    // Lowers each with-override value expression, appending any hoisted statements. Returns
    // AllSimple=false the moment a nested path or index update is seen (not yet lowered).
    private (bool AllSimple, List<(string Field, Expression Value)> Overrides) LowerWithOverrides(
        WithExpression withExpr, List<Statement> hoisted)
    {
        var loweredOverrides = new List<(string Field, Expression Value)>();
        foreach ((List<string>? path, Expression? idx, Expression value) in withExpr.Updates)
        {
            if (path is [string singleField] && idx == null)
            {
                var (valH, loweredVal) = LowerExpr(value);
                hoisted.AddRange(valH);
                loweredOverrides.Add((singleField, loweredVal));
            }
            else
            {
                return (false, loweredOverrides);
            }
        }

        return (true, loweredOverrides);
    }

    // Builds `var with_copy = baseRef.assign(); with_copy.field = value; …` returning the copy ref.
    private Expression BuildWithCopy(Expression baseRef, TypeInfo baseType, RecordTypeInfo recordType,
        List<(string Field, Expression Value)> loweredOverrides, SourceLocation loc,
        List<Statement> hoisted)
    {
        // var with_copy = baseRef.assign()
        var copyCall = new CallExpression(
            Callee: new MemberExpression(
                Object: baseRef, MemberName: "assign", Location: loc)
                { ResolvedType = baseType },
            Arguments: [],
            Location: loc) { ResolvedType = baseType };

        string copyTempName = NextTempName(prefix: "with_copy");
        AddTempVar(hoisted: hoisted, name: copyTempName, typeHint: baseType,
            initializer: copyCall, loc: loc);
        var copyRef = new IdentifierExpression(Name: copyTempName, Location: loc)
            { ResolvedType = baseType };

        // with_copy.field = value
        foreach ((string fieldName, Expression value) in loweredOverrides)
        {
            MemberVariableInfo? memberInfo =
                recordType.LookupMemberVariable(memberVariableName: fieldName);
            var target = new MemberExpression(
                Object: copyRef, MemberName: fieldName, Location: loc)
                { ResolvedType = memberInfo?.Type };
            hoisted.Add(item: new AssignmentStatement(
                Target: target, Value: value, Location: loc));
        }

        return copyRef;
    }

    private (List<Statement> Hoisted, Expression Expr) LowerIsPatternExpression(
        IsPatternExpression ipe)
    {
        TypeInfo? operandType = ipe.Expression.ResolvedType;
        bool isNoneCheck = ipe.Pattern is NonePattern or TypePattern { Type.Name: "None" };
        bool isNoneTypeCheck = ipe.Pattern is TypePattern { Type.Name: NoneTypeName };

        // Lower the operand expression first.
        var (hoisted, loweredExpr) = LowerExpr(ipe.Expression);

        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");
        TypeInfo? u64Type  = ctx.Registry.LookupType(name: "U64");

        // Maybe[T record]: x is None -> not x.present; x isnot None -> x.present
        if (isNoneCheck && IsMaybeRecord(operandType))
            return LowerMaybeAbsenceCheck(ipe: ipe, loweredExpr: loweredExpr, boolType: boolType,
                hoisted: hoisted);

        // Result/Lookup: x is None -> x.type_id == 0_u64; x isnot None -> x.type_id != 0_u64
        if (isNoneTypeCheck && IsResultOrLookup(operandType))
            return (hoisted, MakeTypeIdZeroCompare(loweredExpr: loweredExpr, isNegated: ipe.IsNegated,
                loc: ipe.Location, u64Type: u64Type, boolType: boolType));

        // D-AST-11: user VariantTypeInfo -- x is T -> x.type_id == FNV-1a(T.FullName)
        if (ipe.Pattern is TypePattern { } tp && operandType is VariantTypeInfo)
        {
            var (matched, result) = LowerVariantIsPattern(ipe: ipe, tp: tp, loweredExpr: loweredExpr,
                u64Type: u64Type, boolType: boolType, hoisted: hoisted);
            if (matched) return result;
        }

        // Choice type: `c is CASE` -> `c == CASE_value`; `c isnot CASE` -> `c != CASE_value`.
        // ChoiceType is backed by an integer (default i32) with each case having a discrete
        // ComputedValue; comparison lowers to a direct integer eq/ne against the case constant.
        if (ipe.Pattern is TypePattern choiceTp && operandType is ChoiceTypeInfo choiceType)
        {
            var (matched, result) = LowerChoiceIsPattern(ipe: ipe, choiceTp: choiceTp,
                choiceType: choiceType, loweredExpr: loweredExpr, boolType: boolType,
                hoisted: hoisted);
            if (matched) return result;
        }

        // Flags type: `p is FLAG` -> `(p & mask) != 0`; `p isnot FLAG` -> `(p & mask) == 0`
        if (ipe.Pattern is TypePattern flagsTp && operandType is FlagsTypeInfo flagsType2)
        {
            var (matched, result) = LowerFlagsIsPattern(ipe: ipe, flagsTp: flagsTp,
                flagsType2: flagsType2, loweredExpr: loweredExpr, hoisted: hoisted);
            if (matched) return result;
        }

        // Entity `x is T`: RF entities have no subtyping, so a concrete-entity `is` a concrete-entity is
        // BUILDTIME-decidable — same type = always true, different = always false. Fold to a Bool literal
        // so codegen never sees an entity type-test (the old "optimistic match" hack disappears). A
        // protocol/Unknown operand carries a runtime type_id and is handled by its own path, not here.
        if (ipe.Pattern is TypePattern entTp && operandType is EntityTypeInfo)
        {
            TypeInfo? target = entTp.Type.ResolvedType ?? ctx.Registry.LookupType(name: entTp.Type.Name);
            if (target is EntityTypeInfo)
            {
                bool same = operandType.FullName == target.FullName;
                bool value = ipe.IsNegated ? !same : same;
                return (hoisted, new LiteralExpression(
                    Value: value,
                    LiteralType: value ? TokenType.True : TokenType.False,
                    Location: ipe.Location) { ResolvedType = boolType });
            }
        }

        // Not lowerable (Maybe[T entity] or other): pass through, but recurse operand.
        if (ReferenceEquals(loweredExpr, ipe.Expression) && hoisted.Count == 0)
            return ([], ipe);
        return (hoisted, ipe with { Expression = loweredExpr });
    }

    // Maybe[T record]: x is None -> not x.present; x isnot None -> x.present
    private (List<Statement> Hoisted, Expression Expr) LowerMaybeAbsenceCheck(
        IsPatternExpression ipe, Expression loweredExpr, TypeInfo? boolType, List<Statement> hoisted)
    {
        var presentAccess = new MemberExpression(
            Object: loweredExpr, MemberName: Resolution.RuntimeContract.Carrier.PresentField, Location: ipe.Location)
        {
            ResolvedType = boolType
        };
        if (ipe.IsNegated)
            return (hoisted, presentAccess);
        var notNode = new UnaryExpression(
            Operator: UnaryOperator.Not,
            Operand: presentAccess,
            Location: ipe.Location) { ResolvedType = boolType };
        var (notH, loweredNot) = LowerLogicalNot(notNode);
        hoisted.AddRange(notH);
        return (hoisted, loweredNot);
    }

    // x.type_id == 0_u64 (or != for isnot).
    private static Expression MakeTypeIdZeroCompare(Expression loweredExpr, bool isNegated,
        SourceLocation loc, TypeInfo? u64Type, TypeInfo? boolType)
    {
        var typeIdAccess = new MemberExpression(
            Object: loweredExpr, MemberName: TypeIdFieldName, Location: loc)
        {
            ResolvedType = u64Type
        };
        var zero = new LiteralExpression(
            Value: 0UL,
            LiteralType: TokenType.U64Literal,
            Location: loc) { ResolvedType = u64Type };
        return new BinaryExpression(
            Left: typeIdAccess,
            Operator: isNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal,
            Right: zero,
            Location: loc) { ResolvedType = boolType };
    }

    // D-AST-11: user VariantTypeInfo -- x is T -> x.type_id == FNV-1a(T.FullName).
    // Returns Matched=false when the target type is unresolvable (caller falls through).
    private (bool Matched, (List<Statement> Hoisted, Expression Expr) Result) LowerVariantIsPattern(
        IsPatternExpression ipe, TypePattern tp, Expression loweredExpr,
        TypeInfo? u64Type, TypeInfo? boolType, List<Statement> hoisted)
    {
        TypeInfo? targetType = tp.Type.ResolvedType
            ?? ctx.Registry.LookupType(name: tp.Type.Name);
        // None: type_id == 0
        if (tp.Type.Name == NoneTypeName || targetType?.Name == NoneTypeName)
        {
            Expression cmp0 = MakeTypeIdZeroCompare(loweredExpr: loweredExpr,
                isNegated: ipe.IsNegated, loc: ipe.Location, u64Type: u64Type, boolType: boolType);
            return (true, (hoisted, cmp0));
        }

        // Specific member type: type_id == FNV-1a(fullName)
        if (targetType != null)
        {
            ulong typeId = TypeIdHelper.ComputeTypeId(fullName: targetType.FullName);
            var typeIdAccess = new MemberExpression(
                Object: loweredExpr, MemberName: TypeIdFieldName, Location: ipe.Location)
            { ResolvedType = u64Type };
            var constant = new LiteralExpression(
                Value: typeId,
                LiteralType: TokenType.U64Literal,
                Location: ipe.Location) { ResolvedType = u64Type };
            BinaryOperator op = ipe.IsNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal;
            Expression cmpT = new BinaryExpression(
                Left: typeIdAccess,
                Operator: op,
                Right: constant,
                Location: ipe.Location) { ResolvedType = boolType };
            return (true, (hoisted, cmpT));
        }

        return (false, default);
    }

    // Choice type: `c is CASE` -> `c == CASE_value`; `c isnot CASE` -> `c != CASE_value`.
    // Returns Matched=false when the case name doesn't resolve (caller falls through).
    private (bool Matched, (List<Statement> Hoisted, Expression Expr) Result) LowerChoiceIsPattern(
        IsPatternExpression ipe, TypePattern choiceTp, ChoiceTypeInfo choiceType,
        Expression loweredExpr, TypeInfo? boolType, List<Statement> hoisted)
    {
        // Pattern name may be qualified (`Color.RED`) from f-string holes or bare (`RED`)
        // from when-clause arms. Match on the trailing segment either way.
        string choiceCaseName = choiceTp.Type.Name;
        int choiceDot = choiceCaseName.LastIndexOf('.');
        if (choiceDot >= 0) choiceCaseName = choiceCaseName.Substring(choiceDot + 1);
        ChoiceCaseInfo? choiceCase = choiceType.Cases.FirstOrDefault(
            c => c.Name == choiceCaseName);
        if (choiceCase != null && boolType != null)
        {
            TypeInfo underlying = choiceType.UnderlyingType
                ?? ctx.Registry.LookupType(name: "S32")!;
            var caseLit = new LiteralExpression(
                Value: (long)choiceCase.ComputedValue,
                LiteralType: TokenType.S32Literal,
                Location: ipe.Location) { ResolvedType = underlying };
            Expression cmpChoice = new BinaryExpression(
                Left: loweredExpr,
                Operator: ipe.IsNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal,
                Right: caseLit,
                Location: ipe.Location) { ResolvedType = boolType };
            return (true, (hoisted, cmpChoice));
        }

        return (false, default);
    }

    // Flags type: `p is FLAG` -> `(p & mask) != 0`; `p isnot FLAG` -> `(p & mask) == 0`.
    // Returns Matched=false when U64/Bool are unresolvable (caller falls through).
    private (bool Matched, (List<Statement> Hoisted, Expression Expr) Result) LowerFlagsIsPattern(
        IsPatternExpression ipe, TypePattern flagsTp, FlagsTypeInfo flagsType2,
        Expression loweredExpr, List<Statement> hoisted)
    {
        TypeInfo? u64Type2 = ctx.Registry.LookupType(name: "U64");
        TypeInfo? boolType2 = ctx.Registry.LookupType(name: "Bool");
        if (u64Type2 == null || boolType2 == null) return (false, default);

        FlagsMemberInfo? member = flagsType2.Members.FirstOrDefault(
            m => m.Name == flagsTp.Type.Name);
        if (member != null)
        {
            ulong mask = 1UL << member.BitPosition;
            var maskLit2 = new LiteralExpression(
                Value: mask, LiteralType: TokenType.U64Literal, Location: ipe.Location)
                { ResolvedType = u64Type2 };
            var zeroLit2 = new LiteralExpression(
                Value: 0UL, LiteralType: TokenType.U64Literal, Location: ipe.Location)
                { ResolvedType = u64Type2 };
            Expression bitAnd = new BinaryExpression(
                Left: loweredExpr, Operator: BinaryOperator.BitwiseAnd,
                Right: maskLit2, Location: ipe.Location) { ResolvedType = u64Type2 };
            Expression cmpFlags = new BinaryExpression(
                Left: bitAnd,
                Operator: ipe.IsNegated ? BinaryOperator.Equal : BinaryOperator.NotEqual,
                Right: zeroLit2,
                Location: ipe.Location) { ResolvedType = boolType2 };
            return (true, (hoisted, cmpFlags));
        }

        // Option A: `subj is <expr>` on flags-typed LHS where the name is not a
        // member — treat as variable reference, lower to subset check
        // `(subj & rhs) == rhs` (or `!= rhs` for isnot).
        var rhsRef = new IdentifierExpression(
            Name: flagsTp.Type.Name, Location: ipe.Location)
            { ResolvedType = flagsType2 };
        Expression bitAnd2 = new BinaryExpression(
            Left: loweredExpr, Operator: BinaryOperator.BitwiseAnd,
            Right: rhsRef, Location: ipe.Location) { ResolvedType = flagsType2 };
        Expression cmpSubset = new BinaryExpression(
            Left: bitAnd2,
            Operator: ipe.IsNegated ? BinaryOperator.NotEqual : BinaryOperator.Equal,
            Right: rhsRef,
            Location: ipe.Location) { ResolvedType = boolType2 };
        return (true, (hoisted, cmpSubset));
    }

    /// <summary>
    /// 1a. Lowers a chained comparison <c>a &lt; b &lt; c</c> to
    /// <c>(a &lt; b) and (b &lt; c)</c>, hoisting complex middle operands.
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerChainedComparison(
        ChainedComparisonExpression chain)
    {
        SourceLocation loc = chain.Location;
        var hoisted = new List<Statement>();

        // Lower all operands, accumulating any of their own hoisted stmts.
        var operands = new List<Expression>(capacity: chain.Operands.Count);
        foreach (Expression op in chain.Operands)
        {
            var (h, lowered) = LowerExpr(op);
            hoisted.AddRange(h);
            operands.Add(lowered);
        }

        // Hoist middle operands (index 1 ... n-2) that are not trivially pure,
        // to prevent double-evaluation.
        for (int i = 1; i < operands.Count - 1; i++)
        {
            Expression mid = operands[i];
            if (mid is IdentifierExpression or LiteralExpression) continue;

            string tempName = NextTempName("cmp_mid");
            TypeInfo? midType = mid.ResolvedType;

            var varDecl = new VariableDeclaration(
                Name: tempName,
                Type: midType != null ? TypeInfoToExpr(midType, loc) : null,
                Initializer: mid,
                Visibility: VisibilityModifier.Secret,
                Location: loc);
            hoisted.Add(new DeclarationStatement(Declaration: varDecl, Location: loc));

            var tempRef = new IdentifierExpression(Name: tempName, Location: loc)
            {
                ResolvedType = midType
            };
            operands[i] = tempRef;
        }

        // Build pairwise comparisons, chained with 'and'.
        TypeInfo? boolType = ctx.Registry.LookupType(name: "Bool");

        Expression result = new BinaryExpression(
            Left: operands[0],
            Operator: chain.Operators[0],
            Right: operands[1],
            Location: loc)
        {
            ResolvedType = boolType
        };

        for (int i = 1; i < chain.Operators.Count; i++)
        {
            Expression pairCmp = new BinaryExpression(
                Left: operands[i],
                Operator: chain.Operators[i],
                Right: operands[i + 1],
                Location: loc)
            {
                ResolvedType = boolType
            };

            result = new BinaryExpression(
                Left: result,
                Operator: BinaryOperator.And,
                Right: pairCmp,
                Location: loc)
            {
                ResolvedType = boolType
            };
        }

        return (hoisted, result);
    }

    /// <summary>
    /// 1b. Lowers <c>a ?? b</c> to:
    /// <code>
    ///   var _car_N = a
    ///   var _qq_N: T
    ///   when _car_N
    ///     is None/None -> _qq_N = b
    ///     else v        -> _qq_N = v
    ///   // replacement: _qq_N
    /// </code>
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerNoneCoalesce(BinaryExpression binary)
    {
        SourceLocation loc = binary.Location;
        TypeInfo? carrierType = binary.Left.ResolvedType;
        TypeInfo? valueType = binary.ResolvedType; // T = the inner type

        // Skip hoisting if types are unknown (e.g., unanalyzed stdlib bodies).
        if (carrierType == null || valueType == null)
            return ([], binary);

        string carName = NextTempName("car");
        string qqName = NextTempName("qq");
        string valName = NextTempName("val");

        var hoisted = new List<Statement>();

        // Lower both sides first, collecting any of their own hoisted stmts.
        var (leftH, loweredLeft) = LowerExpr(binary.Left);
        var (rightH, loweredRight) = LowerExpr(binary.Right);
        hoisted.AddRange(leftH);

        // var _car_N = a
        AddTempVar(hoisted, carName, carrierType, loweredLeft, loc);

        // var _qq_N: T  (uninitialized; type annotation gives codegen the LLVM type)
        AddTempVarUninit(hoisted, qqName, valueType, loc);

        Expression carRef = MakeRef(carName, carrierType, loc);
        Expression qqRef = MakeRef(qqName, valueType, loc);
        Expression valRef = MakeRef(valName, valueType, loc);

        // None/None clause: prepend any hoisting from the right operand, then assign.
        var noneBody = new List<Statement>(capacity: rightH.Count + 1);
        noneBody.AddRange(rightH);
        noneBody.Add(new AssignmentStatement(Target: qqRef, Value: loweredRight, Location: loc));

        ProducedWhenStatement = true;
        var whenStmt = new WhenStatement(
            Expression: carRef,
            Clauses:
            [
                new WhenClause(
                    Pattern: MakeAbsencePattern(carrierType, loc),
                    Body: new BlockStatement(Statements: noneBody, Location: loc),
                    Location: loc),
                new WhenClause(
                    Pattern: new ElsePattern(VariableName: valName, Location: loc),
                    Body: new AssignmentStatement(Target: qqRef, Value: valRef, Location: loc),
                    Location: loc)
            ],
            Location: loc);
        hoisted.Add(whenStmt);

        return (hoisted, MakeRef(qqName, valueType, loc));
    }

    /// <summary>
    /// 1d. Lowers <c>a?.prop</c> to:
    /// <code>
    ///   var _car_N = a
    ///   var _om_N: Maybe[PropType]
    ///   when _car_N
    ///     is None/None -> _om_N = None   (zeroinitializer via IdentifierExpression("None"))
    ///     else v        -> _om_N = v.prop  (auto-wrapped if needed by codegen)
    ///   // replacement: _om_N
    /// </code>
    /// </summary>
    private (List<Statement> Hoisted, Expression Expr) LowerOptionalMember(
        OptionalMemberExpression optMember)
    {
        SourceLocation loc = optMember.Location;
        TypeInfo? carrierType = optMember.Object.ResolvedType;
        TypeInfo? resultType = optMember.ResolvedType; // Maybe[PropType]

        // Skip hoisting if types are unknown (e.g., unanalyzed stdlib bodies).
        if (carrierType == null || resultType == null)
            return ([], optMember);

        string carName = NextTempName("car");
        string omName = NextTempName("om");
        string valName = NextTempName("val");

        var hoisted = new List<Statement>();

        var (objH, loweredObj) = LowerExpr(optMember.Object);
        hoisted.AddRange(objH);

        AddTempVar(hoisted, carName, carrierType, loweredObj, loc);
        AddTempVarUninit(hoisted, omName, resultType, loc);

        Expression carRef = MakeRef(carName, carrierType, loc);
        Expression omRef = MakeRef(omName, resultType, loc);

        // Inner type for member access
        TypeInfo? innerType = carrierType?.TypeArguments?[0];
        Expression valRef = MakeRef(valName, innerType, loc);

        // val.prop
        TypeInfo? propType = resultType?.TypeArguments?[0];
        var memberAccess = new MemberExpression(
            Object: valRef,
            MemberName: optMember.MemberName,
            Location: loc)
        {
            ResolvedType = propType
        };

        // None literal (absent Maybe) -- codegen treats "None" identifier as zeroinitializer
        var noneLiteral = new IdentifierExpression(Name: "None", Location: loc)
        {
            ResolvedType = resultType
        };

        ProducedWhenStatement = true;
        var whenStmt = new WhenStatement(
            Expression: carRef,
            Clauses:
            [
                new WhenClause(
                    Pattern: MakeAbsencePattern(carrierType, loc),
                    Body: new AssignmentStatement(
                        Target: omRef, Value: noneLiteral, Location: loc),
                    Location: loc),
                new WhenClause(
                    Pattern: new ElsePattern(VariableName: valName, Location: loc),
                    Body: new AssignmentStatement(
                        Target: omRef, Value: memberAccess, Location: loc),
                    Location: loc)
            ],
            Location: loc);
        hoisted.Add(whenStmt);

        return (hoisted, MakeRef(omName, resultType, loc));
    }

    // --- Helpers -----------------------------------------------------------------

    private static List<Statement> Concat(List<Statement> a, List<Statement> b)
    {
        if (a.Count == 0) return b;
        if (b.Count == 0) return a;
        var result = new List<Statement>(capacity: a.Count + b.Count);
        result.AddRange(a);
        result.AddRange(b);
        return result;
    }

    /// <summary>Adds <c>var name = initializer</c> to <paramref name="hoisted"/>.</summary>
    private static void AddTempVar(
        List<Statement> hoisted, string name, TypeInfo? typeHint,
        Expression initializer, SourceLocation loc)
    {
        var decl = new VariableDeclaration(
            Name: name,
            Type: typeHint != null ? TypeInfoToExpr(typeHint, loc) : null,
            Initializer: initializer,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        hoisted.Add(new DeclarationStatement(Declaration: decl, Location: loc));
    }

    /// <summary>
    /// Picks the first candidate type that is concrete (not a generic-definition record/entity,
    /// which would lower to <c>ptr</c>), falling back to the first non-null candidate. Used to type
    /// a hoisted result temp from a conditional/when whose own ResolvedType may carry a stale generic
    /// self-type even when a branch was concretized during monomorphization.
    /// </summary>
    private static TypeInfo? FirstConcrete(params TypeInfo?[] candidates)
    {
        TypeInfo? firstNonNull = null;
        foreach (TypeInfo? c in candidates)
        {
            if (c == null) continue;
            firstNonNull ??= c;
            if (!c.IsGenericDefinition) return c;
        }
        return firstNonNull;
    }

    private static void AddTempVarUninit(
        List<Statement> hoisted, string name, TypeInfo? typeHint, SourceLocation loc)
    {
        if (typeHint == null) return; // can't emit without a type; leave to codegen fallback
        // IsLateInit so codegen zero-inits the placeholder (entities get a calloc'd block, value/
        // managed-leaf records get zeroinitializer). Each when/`??`/`?.` arm assigns this temp, and
        // an owned-type assignment releases the OLD value first (entities via ScopeTeardownLoweringPass,
        // managed-leaf records via TemporaryTeardownPass) — so the placeholder it tears down on the
        // first assignment must be a null-safe zeroed value, not garbage.
        var decl = new VariableDeclaration(
            Name: name,
            Type: TypeInfoToExpr(typeHint, loc),
            Initializer: null,
            Visibility: VisibilityModifier.Secret,
            Location: loc,
            IsLateInit: true);
        hoisted.Add(new DeclarationStatement(Declaration: decl, Location: loc));
    }

    /// <summary>
    /// Creates an <see cref="IdentifierExpression"/> for a synthetic temp variable.
    /// </summary>
    private static IdentifierExpression MakeRef(string name, TypeInfo? resolvedType, SourceLocation loc)
    {
        return new IdentifierExpression(Name: name, Location: loc)
        {
            ResolvedType = resolvedType
        };
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is <c>Maybe[T]</c> where T is a record/value type
    /// (the two-field variant with <c>present</c> and <c>value</c> fields).
    /// </summary>
    private static bool IsMaybeRecord(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => type.Name
        };
        if (baseName != "Maybe") return false;
        if (type.TypeArguments is not { Count: > 0 }) return false;
        // Post-Owned-retirement: Maybe[T entity] uses the same record-shaped carrier
        // as Maybe[T record] (single `present`+`value` layout), so accept both.
        return true;
    }

    /// <summary>
    /// Finds the chain of variant arm types leading from <paramref name="from"/> to
    /// <paramref name="target"/> — <c>[target]</c> when it is a direct arm, or the nested path
    /// (e.g. <c>[Inner, S32]</c> for an <c>Outer</c> whose <c>Inner</c> arm holds <c>S32</c>).
    /// Returns null when unreachable. Arms are distinct types, so the path is unique.
    /// </summary>
    private static List<TypeInfo>? FindVariantArmPath(VariantTypeInfo from, TypeInfo target)
    {
        foreach (VariantMemberInfo member in from.Members)
        {
            if (member.Type is not { } armType) continue;
            if (armType.Name == target.Name) return [armType];
            if (armType is VariantTypeInfo sub && FindVariantArmPath(from: sub, target: target) is
                { } rest)
            {
                rest.Insert(index: 0, item: armType);
                return rest;
            }
        }

        return null;
    }

    /// <summary>Returns true if the type is <c>Result[T]</c> or <c>Lookup[T]</c>.</summary>
    private static bool IsResultOrLookup(TypeInfo? type)
    {
        if (type == null) return false;
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => type.Name
        };
        return baseName is "Result" or "Lookup";
    }

    /// <summary>
    /// Returns the appropriate absence pattern for the carrier:
    /// <c>NonePattern</c> for Maybe[T], <c>TypePattern("None")</c> for Result/Lookup.
    /// </summary>
    private static Pattern MakeAbsencePattern(TypeInfo? carrierType, SourceLocation loc)
    {
        // Maybe is identified by name prefix "Maybe"
        string? baseName = carrierType switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            _ => carrierType?.Name
        };

        if (baseName == "Maybe")
            return new NonePattern(Location: loc);

        // Result, Lookup, or unknown -- use None type pattern
        return new TypePattern(
            Type: new TypeExpression(Name: NoneTypeName, GenericArguments: null, Location: loc),
            VariableName: null,
            Bindings: null,
            Location: loc);
    }

    /// <summary>
    /// Converts a <see cref="TypeInfo"/> to a <see cref="TypeExpression"/> suitable for
    /// use as a variable type annotation in a synthetic <see cref="VariableDeclaration"/>.
    /// </summary>
    private static TypeExpression TypeInfoToExpr(TypeInfo type, SourceLocation loc)
    {
        // For generic resolutions, use the base definition name (not the resolved "Maybe[S64]").
        string baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution ? type.BareName : type.Name
        };

        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments.Select(selector: a => TypeInfoToExpr(a, loc)).ToList()
            : null;

        // Carry the already-resolved TypeInfo so codegen uses it directly instead of re-resolving the
        // bare name via the cross-module short-name scan. ONLY for a fully-concrete type — annotating an
        // unsubstituted generic parameter would trip the Track-C monomorphization-completeness guard.
        return new TypeExpression(Name: baseName, GenericArguments: args, Location: loc)
        {
            ResolvedType = TypeContainsGenericParameter(type) ? null : type
        };
    }

    /// <summary>True when <paramref name="type"/> is (or transitively contains) an unsubstituted
    /// generic parameter / protocol-self — such a type must NOT be frozen onto a synthesized
    /// TypeExpression's ResolvedType (the monomorphizer would fail its completeness check).</summary>
    private static bool TypeContainsGenericParameter(TypeInfo type) =>
        type is GenericParameterTypeInfo or ProtocolSelfTypeInfo or ComptimeConstGenericTypeInfo
        || (type.TypeArguments?.Any(predicate: TypeContainsGenericParameter) ?? false);

    /// <summary>
    /// D-AST-7: Runs expression lowering on all synthesized variant bodies in
    /// <see cref="PostprocessingContext.VariantBodies"/> so that hoisting transforms
    /// (conditional expressions, when expressions, etc.) are applied to variant bodies
    /// generated by <see cref="ErrorHandlingVariantPass"/>.
    /// </summary>
    public void RunOnVariantBodies()
        => BodyDispatch.RunOnVariantBodies(
            ctx.VariantBodies, lower: (_, body) => LowerStatementFull(stmt: body));

    /// <summary>
    /// Lowers monomorphized generic bodies that GMP cloned from generic-def ASTs after the
    /// per-program Phase 8 sweep finished. Without this, constructs like
    /// <c>for i in 0u64 til arr_size</c> (RangeExpression) and `not expr` (UnaryExpression Not)
    /// inside a monomorphized routine reach codegen unchanged and trip the residual-node guards.
    /// Mirror of <c>OperatorLoweringPass.RunOnInstantiatedGenericBodies</c>.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, Instantiation.MonomorphizedBody> instantiatedGenericBodies)
        => BodyDispatch.RunOnInstantiatedGenericBodies(
            instantiatedGenericBodies, lower: (_, entry) => LowerStatementFull(stmt: entry.Ast.Body));
}
