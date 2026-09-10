using Builder.Tokenizer;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Lowering.Passes;

/// <summary>
/// Lowers <see cref="WhenStatement"/>s whose clauses all use simple or carrier-type patterns
/// into plain <see cref="IfStatement"/> chains. Runs last in the per-file pipeline
/// (after <see cref="Builder.Desugaring.Passes.ControlFlowLoweringPass"/> and <see cref="ExpressionLoweringPass"/>).
///
/// <para>Lowerable patterns (all subject types):</para>
/// <list type="bullet">
/// <item><see cref="WildcardPattern"/> -> always matches, no binding.</item>
/// <item><see cref="LiteralPattern"/> -> emits <c>subject == value</c>.</item>
/// <item><see cref="IdentifierPattern"/> -> always matches, binds subject to name.</item>
/// <item><see cref="ExpressionPattern"/> -> boolean expression, subject is ignored.</item>
/// <item><see cref="ComparisonPattern"/> -> emits <c>subject op value</c>.</item>
/// <item><see cref="ElsePattern"/> -> always matches, optional binding (see below).</item>
/// <item><see cref="GuardPattern"/> -> inner condition AND guard; inner must be lowerable.</item>
/// </list>
///
/// <para>Lowerable patterns (Maybe subjects):</para>
/// <list type="bullet">
/// <item><see cref="NonePattern"/> on <c>Maybe[T record]</c> -> <c>not subject.present</c>.</item>
/// <item><see cref="TypePattern"/> T on <c>Maybe[T record]</c> -> <c>subject.present</c>,
/// binding -> <c>subject.value</c>.</item>
/// <item><see cref="ElsePattern"/> on <c>Maybe[T record]</c> -> binding -> <c>subject.value</c>.</item>
/// </list>
///
/// <para>Lowerable patterns (flags subjects):</para>
/// <list type="bullet">
/// <item><see cref="FlagsPattern"/> -> <see cref="FlagsTestExpression"/> condition.</item>
/// </list>
///
/// <para>Lowerable patterns (record subjects -> destructuring):</para>
/// <list type="bullet">
/// <item><see cref="DestructuringPattern"/> with all named simple bindings -> always matches,
/// binds <c>var name = subject.field</c> for each binding.</item>
/// <item><see cref="TypeDestructuringPattern"/> with all named simple bindings -> same, plus
/// type check is always true when SA validated the subject type.</item>
/// </list>
///
/// <para>Lowerable patterns (Result/Lookup and user variant subjects):</para>
/// <list type="bullet">
/// <item><see cref="TypePattern"/> <c>None</c> -> <c>subject.type_id == 0</c>.</item>
/// <item><see cref="TypePattern"/> T -> <c>subject.type_id == FNV-1a(T.FullName)</c>,
/// optional binding -> <see cref="CarrierPayloadExpression"/>.</item>
/// <item><see cref="ElsePattern"/> with binding on Result/Lookup -> <see cref="CarrierPayloadExpression"/>.</item>
/// </list>
///
/// <para>Lowerable patterns (Maybe[T entity] subjects):</para>
/// <list type="bullet">
/// <item><see cref="NonePattern"/> on <c>Maybe[T entity]</c> -> <c>subject.value.is_none()</c>.</item>
/// <item><see cref="TypePattern"/> T on <c>Maybe[T entity]</c> -> <c>not subject.value.is_none()</c>,
/// binding -> <c>subject.value.extract()</c>.</item>
/// <item><see cref="ElsePattern"/> with binding on <c>Maybe[T entity]</c> -> binding -> <c>subject.value.extract()</c>.</item>
/// </list>
///
/// <para>Left unchanged for codegen's <c>EmitWhen</c>:</para>
/// <list type="bullet">
/// <item><see cref="NegatedTypePattern"/> on user variant.</item>
/// <item><see cref="CrashablePattern"/> -> expanded by <see cref="CrashableExpansionPass"/> before this pass;
/// any remaining instances pass through to codegen.</item>
/// <item><see cref="VariantPattern"/> (type-based; use <see cref="TypePattern"/> instead).</item>
/// <item><see cref="DestructuringPattern"/>/<see cref="TypeDestructuringPattern"/> with nested patterns.</item>
/// </list>
/// </summary>
internal sealed class PatternLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private const string ValueFieldName = Declaration.RuntimeContract.Carrier.ValueField;
    private const string TypeIdFieldName = "type_id";

    /// <summary>
    /// Redirects the <c>Carrier</c> of every <see cref="CrashableDispatchExpression"/> in a subtree to a
    /// new expression (the hoisted subject temp). Used by <see cref="VisitWhen"/> when the carrier subject
    /// is non-trivial and gets hoisted, so the dispatch reads the temp instead of re-evaluating the subject.
    /// </summary>
    private sealed class CrashableDispatchCarrierRewriter(Expression newCarrier) : AstRewriter
    {
        public override Expression VisitExpression(Expression expr)
        {
            return expr is CrashableDispatchExpression cd
                ? cd with { Carrier = newCarrier }
                : base.VisitExpression(expr: expr);
        }
    }

    /// <summary>
    /// Tracks the temp count while this compiler phase runs.
    /// </summary>
    private int _tempCount;

    /// <summary>
    /// Performs the next temp name step for this compiler phase.
    /// </summary>
    private string NextTempName(string prefix)
    {
        return $"_pl_{prefix}_{_tempCount++}";
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    /// <summary>
    /// Lowers monomorphized generic bodies that GMP cloned from generic-def ASTs after the
    /// per-program Phase 8 sweep finished. Without this, `when me.field is None => ... else x => ...`
    /// over <c>Maybe[Wrapper[T]]</c> (e.g. <c>Maybe[Retained[ListNode[T]]]</c>) reaches codegen
    /// as an unlowered TypePattern/ElsePattern pair; the codegen TypePattern path then falls
    /// through to an unconditional match, silently selecting the first arm regardless of state.
    /// Mirror of <c>ExpressionLoweringPass.RunOnInstantiatedGenericBodies</c>.
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, Instantiation.MonomorphizedBody> instantiatedGenericBodies)
    {
        BodyDispatch.RunOnInstantiatedGenericBodies(bodies: instantiatedGenericBodies,
            lower: (_, entry) => VisitStatement(stmt: entry.Ast.Body));
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        BodyDispatch.RunOnVariantBodies(bodies: ctx.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// The only node this pass rewrites: a lowerable <see cref="WhenStatement"/> becomes a plain
    /// if/else chain (with hoisted subject temps wrapped in a block). All structural recursion —
    /// statements and nested expressions — is supplied by <see cref="AstRewriter"/>. Clause bodies
    /// are recursed here (matching the original order: bodies first, then the when transform) rather
    /// than via <c>base.VisitWhen</c>, because the transform needs the already-lowered clauses to
    /// build the chain and a non-lowerable when must keep its rebuilt clause list.
    /// </summary>
    protected override Statement VisitWhen(WhenStatement s)
    {
        // Recurse into clause bodies first (handles nested WhenStatements).
        bool clauseChanged = false;
        var loweredClauses = new List<WhenClause>(capacity: s.Clauses.Count);
        foreach (WhenClause c in s.Clauses)
        {
            Statement lBody = VisitStatement(stmt: c.Body);
            if (!ReferenceEquals(objA: lBody, objB: c.Body))
            {
                loweredClauses.Add(item: c with { Body = lBody });
                clauseChanged = true;
            }
            else
            {
                loweredClauses.Add(item: c);
            }
        }

        // Subject-less when (Expression == null): each arm is an ExpressionPattern (bool guard)
        // or ElsePattern. Lower directly to an if/else chain.
        if (s.Expression == null)
        {
            return LowerSubjectlessWhen(loweredClauses: loweredClauses, loc: s.Location);
        }

        TypeSymbol? subjectType = s.Expression.ResolvedType;

        if (!IsLowerable(when: s, subjectType: subjectType))
        {
            // Leave for codegen's EmitWhen; update clauses if any bodies changed.
            return clauseChanged
                ? s with { Clauses = loweredClauses }
                : s;
        }

        // -----------------------------------------------------------------------------
        SourceLocation loc = s.Location;

        // Hoist a non-trivial subject to a temp, then build the if/else chain.
        return BuildLowerableWhen(expression: s.Expression,
            subjectType: subjectType,
            loweredClauses: loweredClauses,
            loc: loc);
    }

    /// <summary>
    /// Builds the output for a lowerable <see cref="WhenStatement"/> after its clauses have been
    /// recursed into: optionally hoists the subject to a temp, rewrites carrier-dispatch bodies,
    /// then assembles the if/else chain. Extracted from <see cref="VisitWhen"/> to reduce its
    /// cognitive complexity.
    /// </summary>
    private Statement BuildLowerableWhen(Expression expression, TypeSymbol? subjectType,
        List<WhenClause> loweredClauses, SourceLocation loc)
    {
        var hoisted = new List<Statement>();
        Expression subject = expression;

        if (subject is not (IdentifierExpression or LiteralExpression))
        {
            string subjName = NextTempName(prefix: "subj");
            AddTempVar(hoisted: hoisted,
                name: subjName,
                typeHint: subjectType,
                initializer: subject,
                loc: loc);
            subject = new IdentifierExpression(Name: subjName, Location: loc)
            {
                ResolvedType = subjectType
            };

            // The subject was hoisted to a temp. Any CrashableDispatchExpression in a (now lowered) clause
            // body captured the ORIGINAL subject expression at CrashableExpansionPass time; redirect it to
            // the temp so a side-effecting subject (e.g. `when check_foo() is Crashable => e.crash_message()`)
            // is not evaluated a second time by the dispatch.
            var redirect = new CrashableDispatchCarrierRewriter(newCarrier: subject);
            for (int i = 0; i < loweredClauses.Count; i++)
            {
                Statement rewrittenBody =
                    redirect.VisitStatement(stmt: loweredClauses[index: i].Body);
                if (!ReferenceEquals(objA: rewrittenBody, objB: loweredClauses[index: i].Body))
                {
                    loweredClauses[index: i] =
                        loweredClauses[index: i] with { Body = rewrittenBody };
                }
            }
        }

        bool isElseNarrowed = DetermineElseNarrowed(loweredClauses: loweredClauses,
            subjectType: subjectType);

        Statement? chain = BuildWhenIfChain(loweredClauses: loweredClauses,
            subject: subject,
            subjectType: subjectType,
            isElseNarrowed: isElseNarrowed,
            loc: loc);

        Statement result = chain ?? new BlockStatement(Statements: [], Location: loc);

        if (hoisted.Count == 0)
        {
            return result;
        }

        hoisted.Add(item: result);
        return new BlockStatement(Statements: hoisted, Location: loc);
    }

    /// <summary>
    /// Determines whether a carrier's <c>else</c> arm is narrowed to the inner type — i.e. ALL non-T
    /// alternatives are covered by prior clauses. CrashableExpansionPass has already fanned
    /// <c>is Crashable</c> into one TypePattern per concrete crashable type, so coverage is counted.
    /// </summary>
    private bool DetermineElseNarrowed(List<WhenClause> loweredClauses, TypeSymbol? subjectType)
    {
        if (IsResultOrLookup(type: subjectType) && subjectType!.TypeArguments?.Count > 0)
        {
            int totalCrashable = ctx.Registry
                                    .GetAllTypes()
                                    .OfType<CrashableTypeSymbol>()
                                    .Count();
            int seenCrashablePatterns = loweredClauses.Count(predicate: c =>
                c.Pattern is TypePattern { Type.ResolvedType: CrashableTypeSymbol });
            // A single un-fanned `is Crashable` arm (CrashablePattern) covers EVERY crashable at once.
            bool crashableCovered =
                loweredClauses.Any(predicate: c => c.Pattern is CrashablePattern) ||
                seenCrashablePatterns >= totalCrashable;
            // Lookup has a None state; Result does not. Result narrows on crashable coverage
            // alone; Lookup additionally requires the None arm.
            if (IsResultType(type: subjectType))
            {
                return crashableCovered;
            }

            // Lookup's absent state is matched by `is None` only
            // (NonePattern from `??`/`?.` desugar OR parser-emitted TypePattern("None")).
            bool seenAbsent = loweredClauses.Any(predicate: c =>
                c.Pattern is TypePattern { Type.Name: "None" } || c.Pattern is NonePattern);
            return seenAbsent && crashableCovered;
        }

        if (IsMaybeRecord(type: subjectType) || IsMaybeEntity(type: subjectType))
        {
            return loweredClauses.Any(predicate: c =>
                c.Pattern is NonePattern || c.Pattern is TypePattern { Type.Name: "None" });
        }

        return false;
    }

    /// <summary>
    /// Builds the if/else chain for a subject <c>when</c> via right-fold (last clause to first).
    /// An always-matching clause (null condition) becomes the final else and discards any chain
    /// built from subsequent unreachable clauses.
    /// </summary>
    private Statement? BuildWhenIfChain(List<WhenClause> loweredClauses, Expression subject,
        TypeSymbol? subjectType, bool isElseNarrowed, SourceLocation loc)
    {
        Statement? chain = null;
        for (int i = loweredClauses.Count - 1; i >= 0; i--)
        {
            WhenClause clause = loweredClauses[index: i];

            // GuardPattern: materialise the inner binding BEFORE testing the guard, so a guard that
            // references the binding (`is T n and n > 0`) sees it. Both an inner-pattern mismatch and
            // a failed guard fall through to the same next-clause `chain`. A flat `(inner and guard)`
            // condition would instead evaluate the guard before the binding is in scope.
            if (clause.Pattern is GuardPattern gp)
            {
                (Expression? innerCond, Statement? innerBinding) = GetPatternCondition(
                    pattern: gp.InnerPattern,
                    subject: subject,
                    subjectType: subjectType,
                    isElseNarrowed: isElseNarrowed);

                Statement guardIf = new IfStatement(Condition: gp.Guard,
                    ThenStatement: clause.Body,
                    ElseStatement: chain,
                    Location: loc);
                Statement innerThen =
                    PrependBinding(binding: innerBinding, body: guardIf, loc: loc);

                chain = innerCond == null
                    ? innerThen
                    : new IfStatement(Condition: innerCond,
                        ThenStatement: innerThen,
                        ElseStatement: chain,
                        Location: loc);
                continue;
            }

            (Expression? cond, Statement? binding) = GetPatternCondition(pattern: clause.Pattern,
                subject: subject,
                subjectType: subjectType,
                isElseNarrowed: isElseNarrowed);

            Statement body = PrependBinding(binding: binding, body: clause.Body, loc: loc);

            if (cond == null)
            {
                // Always-matching clause: becomes the unconditional else.
                chain = body;
            }
            else
            {
                chain = new IfStatement(Condition: cond,
                    ThenStatement: body,
                    ElseStatement: chain,
                    Location: loc);
            }
        }

        return chain;
    }

    /// <summary>
    /// Prepends a pattern's binding statement(s) to a branch body so they share its scope.
    /// A multi-binding <see cref="BlockStatement"/> (from destructuring) is flattened in.
    /// Returns <paramref name="body"/> unchanged when there is no binding.
    /// </summary>
    private static Statement PrependBinding(Statement? binding, Statement body, SourceLocation loc)
    {
        if (binding == null)
        {
            return body;
        }

        if (binding is BlockStatement multiBindBlock)
        {
            var stmts = new List<Statement>(capacity: multiBindBlock.Statements.Count + 1);
            stmts.AddRange(collection: multiBindBlock.Statements);
            stmts.Add(item: body);
            return new BlockStatement(Statements: stmts, Location: loc);
        }

        return new BlockStatement(Statements: [binding, body], Location: loc);
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Lowers a subject-less <c>when</c> (each arm is an <see cref="ExpressionPattern"/> bool guard
    /// or an <see cref="ElsePattern"/>) into an if/else chain.
    /// </summary>
    private static Statement LowerSubjectlessWhen(List<WhenClause> loweredClauses,
        SourceLocation loc)
    {
        // Build if/else chain via right-fold (last clause first).
        Statement? chain = null;
        for (int i = loweredClauses.Count - 1; i >= 0; i--)
        {
            WhenClause clause = loweredClauses[index: i];
            Statement body = clause.Body;
            chain = clause.Pattern switch
            {
                ElsePattern or WildcardPattern =>
                    // Always-matching: becomes the unconditional else.
                    body,
                ExpressionPattern ep => new IfStatement(Condition: ep.Expression,
                    ThenStatement: body,
                    ElseStatement: chain,
                    Location: loc),
                _ => throw new InvalidOperationException(
                    message:
                    $"Unexpected pattern type '{clause.Pattern.GetType().Name}' in subject-less when. " +
                    "Only ElsePattern, WildcardPattern, and ExpressionPattern are valid in subject-less when clauses.")
            };
        }

        return chain ?? new BlockStatement(Statements: [], Location: loc);
    }

    /// <summary>
    /// Returns whether is lowerable applies in the current compiler context.
    /// </summary>
    private static bool IsLowerable(WhenStatement when, TypeSymbol? subjectType)
    {
        return when.Clauses.All(predicate: c => IsLowerablePattern(pattern: c.Pattern,
            subjectType: subjectType));
    }

    /// <summary>
    /// Returns whether is lowerable pattern applies in the current compiler context.
    /// </summary>
    private static bool IsLowerablePattern(Pattern pattern, TypeSymbol? subjectType)
    {
        return pattern switch
        {
            WildcardPattern => true,
            LiteralPattern => true,
            IdentifierPattern => true,
            ExpressionPattern => true,
            ComparisonPattern => true,
            FlagsPattern => subjectType is FlagsTypeSymbol,

            // ElsePattern with binding on Result/Lookup, Maybe[T entity], or Maybe[T record]: lowerable.
            ElsePattern ep => !ep.VariableName.HasValue() || IsResultOrLookup(type: subjectType) ||
                              IsMaybeEntity(type: subjectType) || IsMaybeRecord(type: subjectType),

            NonePattern => IsMaybeRecord(type: subjectType) || IsMaybeEntity(type: subjectType),

            // Maybe[T record] TypePattern: lowerable (uses bool present field, if/else is optimal)
            TypePattern when IsMaybeRecord(type: subjectType) => true,

            // Maybe[T entity] TypePattern: lowerable (null-ptr check via is_none())
            TypePattern when IsMaybeEntity(type: subjectType) => true,

            // Result/Lookup and user variant TypePatterns: lowerable -> condition is type_id == constant.
            TypePattern when IsResultOrLookup(type: subjectType) || subjectType is VariantTypeSymbol
                => true,

            // User choice `is CASE`: lowerable to a discriminant identity compare (subject `is` case value),
            // but only when the case name resolves — an unknown case is left for codegen (br failLabel).
            TypePattern choiceTp when subjectType is ChoiceTypeSymbol choiceCti =>
                choiceCti.Cases.Any(predicate: c =>
                    c.Name == LastNameSegment(name: choiceTp.Type.Name)),

            // Entity `is T`: RF entities have no subtyping, so it is BUILDTIME-decidable (same concrete
            // type = always match, different = never) — lowerable to a constant condition + optional binding.
            TypePattern when subjectType is EntityTypeSymbol => true,

            // The single un-fanned `is Crashable` arm on a carrier (CrashableExpansionPass no longer fans it
            // per-type; it rewrites the arm body to CrashableDispatchExpression and keeps ONE CrashablePattern).
            // Lowerable -> condition is `type_id != 0 && type_id != <T>.type_id()` (i.e. "holds an error").
            CrashablePattern when IsResultOrLookup(type: subjectType) => true,

            // NegatedTypePattern on user variant: lowerable -> condition is type_id != constant.
            NegatedTypePattern when subjectType is VariantTypeSymbol => true,

            DestructuringPattern dp when subjectType is RecordTypeSymbol or EntityTypeSymbol &&
                                         AreDestructuringBindingsLowerable(bindings: dp.Bindings,
                                             subjectType: subjectType) => true,

            TypeDestructuringPattern tdp when subjectType is RecordTypeSymbol or EntityTypeSymbol &&
                                              AreDestructuringBindingsLowerable(
                                                  bindings: tdp.Bindings,
                                                  subjectType: subjectType) => true,

            GuardPattern gp => IsLowerablePattern(pattern: gp.InnerPattern,
                subjectType: subjectType),

            _ => false
        };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Returns the if-condition expression and optional binding statement for a lowerable pattern.
    /// A <c>null</c> condition means the pattern always matches (becomes the final <c>else</c>).
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetPatternCondition(Pattern pattern,
        Expression subject, TypeSymbol? subjectType, bool isElseNarrowed = false)
    {
        SourceLocation loc = pattern.Location;
        TypeSymbol? boolType = ctx.Registry.LookupType(name: "Bool");

        switch (pattern)
        {
            case WildcardPattern:
                return (null, null);

            case LiteralPattern lit:
            {
                var cond = new BinaryExpression(Left: subject,
                    Operator: BinaryOperator.Equal,
                    Right: new LiteralExpression(Value: lit.Value,
                        LiteralType: lit.LiteralType,
                        Location: loc) { ResolvedType = subject.ResolvedType },
                    Location: loc) { ResolvedType = boolType };
                return (cond, null);
            }

            case IdentifierPattern id:
                // Always matches; bind the subject to the identifier name.
                return (null, MakeBinding(name: id.Name, value: subject, loc: id.Location));

            case ExpressionPattern ep:
                // Boolean expression -> subject is not referenced.
                return (ep.Expression, null);

            case ComparisonPattern cmp:
            {
                var cond = new BinaryExpression(
                    Left: subject,
                    Operator: TokenTypeToBinaryOp(op: cmp.Operator),
                    Right: cmp.Value,
                    Location: loc) { ResolvedType = boolType };
                return (cond, null);
            }

            case ElsePattern ep:
                return GetElsePatternCondition(ep: ep,
                    subject: subject,
                    subjectType: subjectType,
                    isElseNarrowed: isElseNarrowed,
                    loc: loc);

            case GuardPattern gp:
            {
                (Expression? innerCond, Statement? innerBinding) =
                    GetPatternCondition(pattern: gp.InnerPattern,
                        subject: subject,
                        subjectType: subjectType);

                // Combine inner condition with guard: (inner AND guard), or just guard.
                Expression guardCond = innerCond != null
                    ? new BinaryExpression(Left: innerCond,
                        Operator: BinaryOperator.And,
                        Right: gp.Guard,
                        Location: loc) { ResolvedType = boolType }
                    : gp.Guard;

                return (guardCond, innerBinding);
            }

            // -----------------------------------------------------------------------------

            case NonePattern:
                // Maybe[T] (both entity and record T): use the present flag. Bound T is
                // record-shaped (pointer slot), so the entity-vs-record split collapses at
                // the carrier level.
                return (MakeNotPresent(subject: subject, loc: loc, boolType: boolType), null);

            case TypePattern tp
                when IsMaybeRecord(type: subjectType) || IsMaybeEntity(type: subjectType):
                return GetMaybeTypePatternCondition(tp: tp,
                    subject: subject,
                    subjectType: subjectType,
                    loc: loc,
                    boolType: boolType);

            case NegatedTypePattern negType when subjectType is VariantTypeSymbol:
                return GetNegatedTypePatternCondition(negType: negType,
                    subject: subject,
                    loc: loc,
                    boolType: boolType);

            case VariantPattern:
                throw new InvalidOperationException(
                    message:
                    "VariantPattern reached PatternLoweringPass -> this pattern type is no longer " +
                    "generated by the parser (use TypePattern / NegatedTypePattern instead).");

            case CrashablePattern when IsResultOrLookup(type: subjectType):
                return GetCrashableArmCondition(subject: subject,
                    subjectType: subjectType,
                    loc: loc,
                    boolType: boolType);

            case CrashablePattern:
                throw new InvalidOperationException(
                    message:
                    "CrashablePattern reached PatternLoweringPass on a non-carrier subject -> only a " +
                    "Result/Lookup carrier's `is Crashable` arm is expected here.");

            case TypePattern tp
                when IsResultOrLookup(type: subjectType) || subjectType is VariantTypeSymbol:
                return GetResultLookupTypePatternCondition(tp: tp,
                    subject: subject,
                    loc: loc,
                    boolType: boolType);

            case TypePattern tp when subjectType is ChoiceTypeSymbol choiceSubj:
                return GetChoiceTypePatternCondition(tp: tp,
                    subject: subject,
                    choiceSubj: choiceSubj,
                    loc: loc,
                    boolType: boolType);

            case TypePattern tp when subjectType is EntityTypeSymbol:
                return GetEntityTypePatternCondition(tp: tp,
                    subject: subject,
                    subjectType: subjectType,
                    loc: loc,
                    boolType: boolType);

            // -----------------------------------------------------------------------------

            case FlagsPattern fp:
            {
                var cond = new FlagsTestExpression(Subject: subject,
                    Kind: FlagsTestKind.Is,
                    TestFlags: fp.FlagNames,
                    Connective: fp.Connective,
                    ExcludedFlags: fp.ExcludedFlags,
                    Location: loc) { ResolvedType = boolType };
                return (cond, null);
            }

            // -----------------------------------------------------------------------------

            case DestructuringPattern dp when subjectType is RecordTypeSymbol rec:
                return GetDestructuringCondition(bindings: dp.Bindings,
                    subject: subject,
                    memberVars: rec.MemberVariables,
                    loc: loc);

            case DestructuringPattern dp when subjectType is EntityTypeSymbol ent:
                return GetDestructuringCondition(bindings: dp.Bindings,
                    subject: subject,
                    memberVars: ent.MemberVariables,
                    loc: loc);

            case TypeDestructuringPattern tdp when subjectType is RecordTypeSymbol rec:
                return GetDestructuringCondition(bindings: tdp.Bindings,
                    subject: subject,
                    memberVars: rec.MemberVariables,
                    loc: loc);

            case TypeDestructuringPattern tdp when subjectType is EntityTypeSymbol ent:
                return GetDestructuringCondition(bindings: tdp.Bindings,
                    subject: subject,
                    memberVars: ent.MemberVariables,
                    loc: loc);

            default:
                throw new InvalidOperationException(
                    message:
                    $"Non-lowerable pattern type '{pattern.GetType().Name}' reached LowerPattern ??" +
                    "IsLowerable should have rejected this pattern before lowering.");
        }
    }

    /// <summary>
    /// Returns the (always-matching) condition and optional binding for an <see cref="ElsePattern"/>.
    /// Extracted from <see cref="GetPatternCondition"/>.
    /// </summary>
    private static (Expression? Cond, Statement? Binding) GetElsePatternCondition(ElsePattern ep,
        Expression subject, TypeSymbol? subjectType, bool isElseNarrowed,
        SourceLocation loc)
    {
        Statement? binding = null;
        if (ep.VariableName != null)
        {
            Expression bindValue;
            if (IsMaybeRecord(type: subjectType) || IsMaybeEntity(type: subjectType))
            {
                // Maybe[T] (record or entity): bind to inner .value field. Bound T is
                // record-shaped (pointer slot) so both branches read the same way.
                bindValue = MakeMemberAccess(subject: subject,
                    field: ValueFieldName,
                    fieldType: subjectType!.TypeArguments![index: 0],
                    loc: loc);
            }
            else if (IsResultOrLookup(type: subjectType) &&
                     subjectType!.TypeArguments?.Count > 0 && isElseNarrowed)
            {
                // Result/Lookup: truly narrowed-to-T else arm -> extract payload.
                // Only when all non-T arms (None + all Crashable types) are handled.
                TypeSymbol innerType = subjectType.TypeArguments[index: 0];
                bindValue = MakeCarrierPayload(subject: subject, innerType: innerType, loc: loc);
            }
            else
            {
                bindValue = subject;
            }

            binding = MakeBinding(name: ep.VariableName, value: bindValue, loc: loc);
        }

        return (null, binding);
    }

    /// <summary>
    /// Returns the condition and optional binding for a <see cref="TypePattern"/> over a
    /// <c>Maybe[T record]</c>/<c>Maybe[T entity]</c> subject. Extracted from
    /// <see cref="GetPatternCondition"/>.
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetMaybeTypePatternCondition(TypePattern tp,
        Expression subject, TypeSymbol? subjectType, SourceLocation loc,
        TypeSymbol? boolType)
    {
        // `is None` on Maybe[T record] tests absence (`not present`); other TypePatterns
        // test presence. Parser emits `is None` as TypePattern with Type.Name == "None".
        TypeSymbol? innerType = subjectType!.TypeArguments![index: 0];
        Expression cond = tp.Type.Name == "None"
            ? MakeNotPresent(subject: subject, loc: loc, boolType: boolType)
            : MakePresentAccess(subject: subject, loc: loc);
        Statement? binding = tp.VariableName != null
            ? MakeBinding(name: tp.VariableName,
                value: MakeMemberAccess(subject: subject,
                    field: ValueFieldName,
                    fieldType: innerType,
                    loc: loc),
                loc: loc)
            : null;
        return (cond, binding);
    }

    /// <summary>
    /// Returns the condition for a <see cref="NegatedTypePattern"/> over a variant subject
    /// (<c>subject.type_id != FNV-1a(type)</c>). Extracted from <see cref="GetPatternCondition"/>.
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetNegatedTypePatternCondition(
        NegatedTypePattern negType, Expression subject, SourceLocation loc,
        TypeSymbol? boolType)
    {
        TypeSymbol? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeSymbol? targetType = negType.Type.ResolvedType ??
                               ctx.Registry.LookupType(name: negType.Type.Name);

        if (targetType == null)
        {
            return (null, null); // Unknown type -> always matches negation (optimistic)
        }

        string fullName = targetType.FullName ?? negType.Type.Name;
        ulong typeId = TypeIdHelper.ComputeTypeId(fullName: fullName);
        Expression cond = new BinaryExpression(
            Left: MakeMemberAccess(subject: subject,
                field: TypeIdFieldName,
                fieldType: u64Type,
                loc: loc),
            Operator: BinaryOperator.NotEqual,
            Right: new LiteralExpression(Value: typeId,
                LiteralType: TokenType.U64Literal,
                Location: loc) { ResolvedType = u64Type },
            Location: loc) { ResolvedType = boolType };
        return (cond, null);
    }

    // -----------------------------------------------------------------------------

    /// <summary>The last dot-separated segment of a (possibly qualified) name, e.g. "Color.RED" -> "RED".</summary>
    private static string LastNameSegment(string name)
    {
        int dot = name.LastIndexOf(value: '.');
        return dot >= 0
            ? name[(dot + 1)..]
            : name;
    }

    /// <summary>
    /// Lowers a user-choice <c>is CASE</c> <see cref="TypePattern"/> to a discriminant equality:
    /// reinterpret the choice to its underlying integer (S32) via <c>S32(from: subject)</c> and compare
    /// to the case's computed discriminant with <c>==</c>. OperatorLoweringPass (which runs AFTER this
    /// pass) lowers the <c>==</c> to <c>S32.eq</c> (icmp eq i32), so the <c>is</c> operator never reaches
    /// codegen. No binding (a choice case carries no payload).
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetChoiceTypePatternCondition(TypePattern tp,
        Expression subject, ChoiceTypeSymbol choiceSubj, SourceLocation loc,
        TypeSymbol? boolType)
    {
        string caseName = LastNameSegment(name: tp.Type.Name);
        ChoiceCaseInfo? matched =
            choiceSubj.Cases.FirstOrDefault(predicate: c => c.Name == caseName);
        // IsLowerablePattern only returns true when the case exists, so `matched` is non-null here.
        TypeSymbol? underlying = choiceSubj.UnderlyingType ?? ctx.Registry.LookupType(name: "S32");
        var reinterpret =
            new CreatorExpression(TypeName: underlying?.Name ?? "S32",
                TypeArguments: null,
                MemberVariables: [("from", subject)],
                Location: loc)
            {
                ResolvedType = underlying, LoweringKind = CallLoweringKind.TypeConstructor
            };
        var valueLit = new LiteralExpression(
            Value: matched!.ComputedValue,
            LiteralType: TokenType.S32Literal,
            Location: loc) { ResolvedType = underlying };
        var cond = new BinaryExpression(
            Left: reinterpret,
            Operator: BinaryOperator.Equal,
            Right: valueLit,
            Location: loc) { ResolvedType = boolType };
        return (cond, null);
    }

    /// <summary>
    /// Lowers an entity <c>is T</c> <see cref="TypePattern"/> to a BUILDTIME-constant condition: RF entities
    /// have no subtyping, so a concrete-entity subject `is` a concrete-entity target is statically known —
    /// same type always matches (null condition = the arm is unconditional; binds `is T name` to the subject),
    /// a different type never matches (a `false` literal). No runtime type test reaches codegen.
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetEntityTypePatternCondition(TypePattern tp,
        Expression subject, TypeSymbol? subjectType, SourceLocation loc,
        TypeSymbol? boolType)
    {
        TypeSymbol? target = tp.Type.ResolvedType ?? ctx.Registry.LookupType(name: tp.Type.Name);
        bool same = target != null && subjectType?.FullName == target.FullName;
        if (same)
        {
            // Always matches; bind `is T name` to the subject (already this exact type).
            Statement? binding = tp.VariableName is { } name
                ? MakeBinding(name: name, value: subject, loc: loc)
                : null;
            return (null, binding);
        }

        // Different concrete entity — never matches.
        return (
            new LiteralExpression(Value: false, LiteralType: TokenType.False, Location: loc)
            {
                ResolvedType = boolType
            }, null);
    }

    /// <summary>
    /// Lowers a <c>TypePattern</c> over a Result/Lookup/Variant carrier to a <c>type_id ==</c> test
    /// (plus the payload binding, if the pattern binds a name). Extracted from
    /// <see cref="GetPatternCondition"/>.
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetResultLookupTypePatternCondition(
        TypePattern tp, Expression subject, SourceLocation loc,
        TypeSymbol? boolType)
    {
        TypeSymbol? u64Type = ctx.Registry.LookupType(name: "U64");
        TypeSymbol? targetType = tp.Type.ResolvedType ?? ctx.Registry.LookupType(name: tp.Type.Name);

        // `is None`  on Lookup/Variant carriers tests type_id == 0.
        if (tp.Type.Name is "None")
        {
            return (MakeTypeIdIsZero(subject: subject,
                loc: loc,
                boolType: boolType,
                u64Type: u64Type), null);
        }

        // Specific type: type_id == FNV-1a(type.FullName).
        // GENERIC PARAM (e.g. `is T` in Result[T].represent): a baked FNV("T") literal
        // would never match after monomorphization (success stores FNV of the concrete type).
        // The binding already substitutes correctly (CarrierPayloadExpression carries the
        // type), but a frozen literal does not. Emit `<T>.type_id()` instead — a type-memberRoutine
        // call GenericAstRewriter folds via the T->concrete substitution map
        // (TryFoldBsCallViaStringSubs) to ComputeTypeId(concrete.FullName), so the condition
        // matches the success state once instantiated.
        Expression typeIdRhs;
        if (targetType is GenericParameterTypeSymbol)
        {
            typeIdRhs = new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: tp.Type.Name, Location: loc),
                    MemberName: TypeIdFieldName,
                    Location: loc),
                Arguments: [],
                Location: loc) { ResolvedType = u64Type };
        }
        else
        {
            string fullName = targetType?.FullName ?? tp.Type.Name;
            typeIdRhs = new LiteralExpression(
                Value: TypeIdHelper.ComputeTypeId(fullName: fullName),
                LiteralType: TokenType.U64Literal,
                Location: loc) { ResolvedType = u64Type };
        }

        Expression cond = new BinaryExpression(
            Left: MakeMemberAccess(subject: subject,
                field: TypeIdFieldName,
                fieldType: u64Type,
                loc: loc),
            Operator: BinaryOperator.Equal,
            Right: typeIdRhs,
            Location: loc) { ResolvedType = boolType };

        Statement? binding = null;
        if (tp.VariableName != null && targetType != null)
        {
            binding = MakeBinding(name: tp.VariableName,
                value: MakeCarrierPayload(subject: subject, innerType: targetType, loc: loc),
                loc: loc);
        }

        return (cond, binding);
    }

    /// <summary>Builds <c>not subject.present</c> for Maybe absence check.</summary>
    private UnaryExpression MakeNotPresent(Expression subject, SourceLocation loc,
        TypeSymbol? boolType)
    {
        return new UnaryExpression(Operator: UnaryOperator.Not,
            Operand: MakePresentAccess(subject: subject, loc: loc),
            Location: loc) { ResolvedType = boolType };
    }

    /// <summary>Builds <c>subject.present</c> member access (Bool).</summary>
    private MemberExpression MakePresentAccess(Expression subject, SourceLocation loc)
    {
        TypeSymbol? boolType = ctx.Registry.LookupType(name: "Bool");
        return new MemberExpression(Object: subject,
            MemberName: Declaration.RuntimeContract.Carrier.PresentField,
            Location: loc) { ResolvedType = boolType };
    }

    /// <summary>Builds a member-access expression with a known field type.</summary>
    private static MemberExpression MakeMemberAccess(Expression subject, string field,
        TypeSymbol? fieldType, SourceLocation loc)
    {
        return new MemberExpression(Object: subject, MemberName: field, Location: loc)
        {
            ResolvedType = fieldType
        };
    }

    /// <summary>
    /// Lowers a carrier's single un-fanned <c>is Crashable</c> arm to the "holds an error" condition
    /// <c>subject.type_id != 0 &amp;&amp; subject.type_id != &lt;T&gt;.type_id()</c> (T = the carrier's success type
    /// argument). No binding: CrashableExpansionPass has already rewritten the arm body's member calls to
    /// <see cref="CrashableDispatchExpression"/>, which reads the erased error straight off the carrier.
    /// The <c>&lt;T&gt;.type_id()</c> form (rather than a baked FNV literal) folds correctly after
    /// monomorphization — the success arm stores the FNV of the concrete T — keeping the generic-def
    /// carrier body crashable-set-independent and therefore snapshot-freeze-safe.
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetCrashableArmCondition(Expression subject,
        TypeSymbol? subjectType, SourceLocation loc, TypeSymbol? boolType)
    {
        TypeSymbol? u64Type = ctx.Registry.LookupType(name: "U64");

        // The success arm's type is the carrier's element type. On a CONCRETE carrier it is
        // TypeArguments[0]; on the generic-DEF (this pass runs on the stdlib Result[T].represent before
        // monomorphization) TypeArguments is empty and the parameter name lives in GenericParameters[0].
        // In both param cases emit `<param>.type_id()` — a builder query that folds to the concrete FNV
        // after GenericAstRewriter substitutes the identifier — matching exactly how the sibling `is T`
        // arm derives its type_id, and keeping this generic-def body crashable-set-independent.
        string? successParamName = null;
        TypeSymbol? successConcrete = null;
        if (subjectType?.TypeArguments is { Count: > 0 } args)
        {
            if (args[index: 0] is GenericParameterTypeSymbol gp0)
            {
                successParamName = gp0.Name;
            }
            else
            {
                successConcrete = args[index: 0];
            }
        }
        else if (subjectType is
                 { IsGenericDefinition: true, GenericParameters: { Count: > 0 } gps })
        {
            successParamName = gps[index: 0];
        }

        Expression successTypeIdRhs = successParamName != null
            ? new CallExpression(
                Callee: new MemberExpression(
                    Object: new IdentifierExpression(Name: successParamName, Location: loc),
                    MemberName: TypeIdFieldName,
                    Location: loc),
                Arguments: [],
                Location: loc) { ResolvedType = u64Type }
            : new LiteralExpression(
                Value: TypeIdHelper.ComputeTypeId(fullName: successConcrete?.FullName ?? ""),
                LiteralType: TokenType.U64Literal,
                Location: loc) { ResolvedType = u64Type };

        Expression notNone = new BinaryExpression(
            Left: MakeMemberAccess(subject: subject,
                field: TypeIdFieldName,
                fieldType: u64Type,
                loc: loc),
            Operator: BinaryOperator.NotEqual,
            Right: new LiteralExpression(Value: 0UL,
                LiteralType: TokenType.U64Literal,
                Location: loc) { ResolvedType = u64Type },
            Location: loc) { ResolvedType = boolType };

        Expression notSuccess = new BinaryExpression(
            Left: MakeMemberAccess(subject: subject,
                field: TypeIdFieldName,
                fieldType: u64Type,
                loc: loc),
            Operator: BinaryOperator.NotEqual,
            Right: successTypeIdRhs,
            Location: loc) { ResolvedType = boolType };

        Expression cond = new BinaryExpression(
            Left: notNone,
            Operator: BinaryOperator.And,
            Right: notSuccess,
            Location: loc) { ResolvedType = boolType };
        return (cond, null);
    }

    /// <summary>Builds <c>subject.type_id == 0_u64</c> for a None/absent check on Result/Lookup.</summary>
    private static BinaryExpression MakeTypeIdIsZero(Expression subject, SourceLocation loc,
        TypeSymbol? boolType, TypeSymbol? u64Type)
    {
        MemberExpression typeIdAccess = MakeMemberAccess(subject: subject,
            field: TypeIdFieldName,
            fieldType: u64Type,
            loc: loc);
        var zero =
            new LiteralExpression(Value: 0UL, LiteralType: TokenType.U64Literal, Location: loc)
            {
                ResolvedType = u64Type
            };
        return new BinaryExpression(Left: typeIdAccess,
            Operator: BinaryOperator.Equal,
            Right: zero,
            Location: loc) { ResolvedType = boolType };
    }

    /// <summary>
    /// Builds a <see cref="CarrierPayloadExpression"/> that extracts the inner value
    /// from the carrier's <c>data_address</c> field cast to <paramref name="innerType"/>.
    /// </summary>
    private static CarrierPayloadExpression MakeCarrierPayload(Expression subject,
        TypeSymbol innerType, SourceLocation loc)
    {
        return new CarrierPayloadExpression(Carrier: subject,
            ConcreteType: TypeInfoToExpr(type: innerType, loc: loc),
            Location: loc) { ResolvedType = innerType };
    }

    /// <summary>
    /// Lowers a named destructuring pattern applied to a record/entity subject to a (condition, binding)
    /// pair. A SIMPLE binding (<c>y: name</c>) always matches and binds <c>var name = subject.field</c>.
    /// A NESTED binding (<c>y: SubPattern</c>) recurses: the field access becomes the sub-pattern's
    /// subject, its condition is AND-combined into the total, and its bindings are appended. Field access
    /// is a pure GEP+load, so re-evaluating it across a nested pattern's condition+binding is side-effect-free.
    /// </summary>
    private (Expression? Cond, Statement? Binding) GetDestructuringCondition(
        List<DestructuringBinding> bindings, Expression subject,
        List<MemberVariableInfo> memberVars, SourceLocation loc)
    {
        TypeSymbol? boolType = ctx.Registry.LookupType(name: "Bool");
        Expression? cond = null;
        var stmts = new List<Statement>(capacity: bindings.Count);
        foreach (DestructuringBinding b in bindings)
        {
            if (b.MemberVariableName == null)
            {
                continue;
            }

            MemberVariableInfo? member =
                memberVars.FirstOrDefault(predicate: m => m.Name == b.MemberVariableName);
            if (member == null)
            {
                continue;
            }

            Expression fieldAccess = MakeMemberAccess(subject: subject,
                field: b.MemberVariableName,
                fieldType: member.Type,
                loc: loc);

            if (b.NestedPattern is { } nested)
            {
                (Expression? subCond, Statement? subBinding) = GetPatternCondition(pattern: nested,
                    subject: fieldAccess,
                    subjectType: member.Type);
                cond = CombineConditions(left: cond,
                    right: subCond,
                    loc: loc,
                    boolType: boolType);
                if (subBinding != null)
                {
                    stmts.Add(item: subBinding);
                }
            }
            else if (b.BindingName != null)
            {
                stmts.Add(item: MakeBinding(name: b.BindingName, value: fieldAccess, loc: loc));
            }
        }

        Statement? binding = stmts.Count switch
        {
            0 => null,
            1 => stmts[index: 0],
            _ => new BlockStatement(Statements: stmts, Location: loc)
        };
        return (cond, binding);
    }

    /// <summary>
    /// AND-combines two nullable conditions: when <paramref name="left"/> is null, returns
    /// <paramref name="right"/> unchanged; otherwise wraps both in a <see cref="BinaryExpression"/>.
    /// </summary>
    private static Expression? CombineConditions(Expression? left, Expression? right,
        SourceLocation loc, TypeSymbol? boolType)
    {
        if (right == null)
        {
            return left;
        }

        if (left == null)
        {
            return right;
        }

        return new BinaryExpression(Left: left,
            Operator: BinaryOperator.And,
            Right: right,
            Location: loc) { ResolvedType = boolType };
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Returns the generic base name of a type (e.g., "Maybe" for <c>Maybe[S64]</c>).
    /// Returns the type's own name if it is not a resolved generic.
    /// </summary>
    private static string GetCarrierBaseName(TypeSymbol type)
    {
        if (type is RecordTypeSymbol { GenericDefinition: not null } r)
        {
            return r.GenericDefinition.Name;
        }

        if (type is EntityTypeSymbol { GenericDefinition: not null } e)
        {
            return e.GenericDefinition.Name;
        }

        return type.Name;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is <c>Maybe[T]</c> where T is a record/value type
    /// (the two-field variant with an accessible <c>present</c> and <c>value</c> field).
    /// Entity-T Maybe has no <c>present</c> field and is not lowerable here.
    /// </summary>
    private static bool IsMaybeRecord(TypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        if (GetCarrierBaseName(type: type) != "Maybe")
        {
            return false;
        }

        if (type.TypeArguments is not { Count: > 0 })
        {
            return false;
        }

        // Entity-T Maybe only has `value: Hijacked[T]`, no `present` field.
        return type.TypeArguments[index: 0] is not EntityTypeSymbol;
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> is <c>Maybe[T]</c> where T is an entity type
    /// (the single-field variant with only a <c>Hijacked[T]</c> value field -> no <c>present</c>).
    /// </summary>
    private static bool IsMaybeEntity(TypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        if (GetCarrierBaseName(type: type) != "Maybe")
        {
            return false;
        }

        if (type.TypeArguments is not { Count: > 0 })
        {
            return false;
        }

        return type.TypeArguments[index: 0] is EntityTypeSymbol;
    }

    /// <summary>Returns true if the type is <c>Result[T]</c> or <c>Lookup[T]</c>.</summary>
    private static bool IsResultOrLookup(TypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        string baseName = GetCarrierBaseName(type: type);
        return baseName is "Result" or "Lookup";
    }

    /// <summary>
    /// Returns whether is result type applies in the current compiler context.
    /// </summary>
    private static bool IsResultType(TypeSymbol? type)
    {
        if (type == null)
        {
            return false;
        }

        return GetCarrierBaseName(type: type) == "Result";
    }

    /// <summary>
    /// Returns true when every named binding is lowerable: a SIMPLE binding (member name + alias, no nested
    /// pattern) always is; a NESTED binding is lowerable when its sub-pattern is lowerable on the member's
    /// type (recursively). Positional bindings (no member name) are left for codegen.
    /// </summary>
    private static bool AreDestructuringBindingsLowerable(List<DestructuringBinding> bindings,
        TypeSymbol? subjectType)
    {
        List<MemberVariableInfo>? memberVars = subjectType switch
        {
            RecordTypeSymbol r => r.MemberVariables,
            EntityTypeSymbol e => e.MemberVariables,
            _ => null
        };
        if (memberVars == null)
        {
            return false;
        }

        return bindings.All(predicate: b =>
        {
            if (b.MemberVariableName == null)
            {
                return false; // positional — not lowered here
            }

            if (b.NestedPattern == null)
            {
                return b.BindingName != null; // simple field -> name binding
            }

            MemberVariableInfo? member =
                memberVars.FirstOrDefault(predicate: m => m.Name == b.MemberVariableName);
            return member != null &&
                   IsLowerablePattern(pattern: b.NestedPattern, subjectType: member.Type);
        });
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Builds the make binding used by later compiler work.
    /// </summary>
    private static DeclarationStatement MakeBinding(string name, Expression value,
        SourceLocation loc)
    {
        TypeSymbol? type = value.ResolvedType;
        var decl = new VariableDeclaration(Name: name,
            Type: type != null
                ? TypeInfoToExpr(type: type, loc: loc)
                : null,
            Initializer: value,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        return new DeclarationStatement(Declaration: decl, Location: loc);
    }

    /// <summary>
    /// Performs the add temp var step for this compiler phase.
    /// </summary>
    private static void AddTempVar(List<Statement> hoisted, string name, TypeSymbol? typeHint,
        Expression initializer, SourceLocation loc)
    {
        var decl = new VariableDeclaration(Name: name,
            Type: typeHint != null
                ? TypeInfoToExpr(type: typeHint, loc: loc)
                : null,
            Initializer: initializer,
            Visibility: VisibilityModifier.Secret,
            Location: loc);
        hoisted.Add(item: new DeclarationStatement(Declaration: decl, Location: loc));
    }

    /// <summary>
    /// Converts a <see cref="TypeSymbol"/> back to a <see cref="TypeExpression"/> for use
    /// in synthetic variable type annotations.
    /// </summary>
    private static TypeExpression TypeInfoToExpr(TypeSymbol type, SourceLocation loc)
    {
        string baseName = type switch
        {
            RecordTypeSymbol { GenericDefinition: not null } r => r.GenericDefinition.Name,
            EntityTypeSymbol { GenericDefinition: not null } e => e.GenericDefinition.Name,
            _ => type.IsGenericResolution
                ? type.BareName
                : type.Name
        };

        List<TypeExpression>? args = type.TypeArguments is { Count: > 0 }
            ? type.TypeArguments
                  .Select(selector: a => TypeInfoToExpr(type: a, loc: loc))
                  .ToList()
            : null;

        // Carry the already-resolved TypeSymbol so codegen uses it directly instead of re-resolving the
        // bare name (which depended on the cross-module short-name scan — e.g. a synthesized
        // `var v: IOError = <payload>` for a `when e is IOError v` arm, IOError living in another module).
        // ONLY for a fully-concrete type: annotating an unsubstituted generic parameter would trip the
        // Track-C monomorphization-completeness guard (ResolvedType='T' reaching codegen).
        return new TypeExpression(Name: baseName, GenericArguments: args, Location: loc)
        {
            ResolvedType = TypeContainsGenericParameter(type: type)
                ? null
                : type
        };
    }

    /// <summary>True when <paramref name="type"/> is (or transitively contains) an unsubstituted
    /// generic parameter / protocol-self — such a type must NOT be frozen onto a synthesized
    /// TypeExpression's ResolvedType (the monomorphizer would fail its completeness check).</summary>
    private static bool TypeContainsGenericParameter(TypeSymbol type)
    {
        return type is GenericParameterTypeSymbol or ProtocolSelfTypeSymbol
                   or BuildtimeConstGenericTypeSymbol ||
               (type.TypeArguments?.Any(predicate: TypeContainsGenericParameter) ?? false);
    }

    /// <summary>
    /// Maps a <see cref="TokenType"/> comparison operator (as used in
    /// <see cref="ComparisonPattern"/>) to the corresponding <see cref="BinaryOperator"/>.
    /// </summary>
    private static BinaryOperator TokenTypeToBinaryOp(TokenType op)
    {
        return op switch
        {
            TokenType.Equal => BinaryOperator.Equal,
            TokenType.NotEqual => BinaryOperator.NotEqual,
            TokenType.Less => BinaryOperator.Less,
            TokenType.LessEqual => BinaryOperator.LessEqual,
            TokenType.Greater => BinaryOperator.Greater,
            TokenType.GreaterEqual => BinaryOperator.GreaterEqual,
            _ => BinaryOperator.Equal
        };
    }
}

/// <summary>Extension to check for a non-null, non-empty string in one expression.</summary>
static file class StringExt
{
    /// <summary>
    /// Returns whether has value applies in the current compiler context.
    /// </summary>
    public static bool HasValue(this string? s)
    {
        return !string.IsNullOrEmpty(value: s);
    }
}
