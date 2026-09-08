using Compiler.Instantiation;
using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Postprocessing pass that lowers two ownership-related constructs:
///
/// <list type="bullet">
/// <item><b>Steal preservation</b> -> keeps <see cref="StealExpression"/> wrappers
/// in the lowered AST so backend entry/codegen can observe explicit ownership
/// transfer sites while still emitting the operand value directly.</item>
/// <item><b>Record copy injection</b> -> rewrites <c>var r2 = r1</c> and <c>r2 = r1</c>
/// where <c>r1</c> is a "borrowed reference" (identifier or field access) of a
/// record type to <c>r1.assign()</c>. Required for RC wrapper types
///  (<c>Retained[T]</c>, <c>Tracked[T]</c>, etc.) where a bit-for-bit struct copy
/// would not increment the reference count, causing a double-free bug.
/// For plain records (no RC fields) <c>store()</c> is semantically identical to
/// a bit copy and is optimized away by LLVM inlining.</item>
/// </list>
///
/// <para>Runs last in the per-file desugaring pipeline (after <see cref="PatternLoweringPass"/>).
/// Needs <c>ResolvedType</c> to be set on all expressions (Phase 4 output).</para>
///
/// <para>Injection is limited to <em>borrowed-reference</em> expressions in assignment
/// positions: <see cref="IdentifierExpression"/> and <see cref="MemberExpression"/> with a
/// record <c>ResolvedType</c>. Fresh values (calls, constructors, arithmetic) are already
/// owned and do not need <c>store()</c>.</para>
/// </summary>
internal sealed class RecordCopyLoweringPass(PostprocessingContext ctx)
{
    // True while lowering the body of a `store` routine. Returning `me` there is the identity-copy
    // primitive itself, so it must NOT be rewritten to `me.assign()` (that would recurse forever).
    private bool _inCopyRoutine;

    // True while lowering an RC-wrapper's refcount COPY VERB body (now the unified `store`). These are
    // hand-written refcount primitives that reference `me` in many forms (receiver, ctor arg, temp spill) —
    // ANY retain-copy injection inside them makes the verb call itself → infinite recursion. So inside such a
    // body, suppress ALL copy injection (stronger than `_inCopyRoutine`, which only guards `return me`).
    private bool _inRcCopyVerb;

    // The MANAGED (retaining-store) parameters of the routine currently being lowered. Under the
    // three-rules param model a record param is a BORROW — passed as-is, owned by the caller. Returning
    // one hands the caller an owned value that ALIASES the caller's still-live argument, so it must be
    // retained (a fresh +1) exactly like `return me`; otherwise both the caller's argument and the
    // returned value free the same controller → double-free. A returned owned LOCAL, by contrast, is a
    // move-out and must NOT be copied. So we retain-on-return only for these borrow params (and `me`).
    private readonly HashSet<string> _borrowParamNames = new(comparer: StringComparer.Ordinal);

    // The memberRoutine-name segment of a routine name/key: strips a leading `Owner.` qualifier and any
    // `(params)` / `[typeargs]` suffix. A stdlib generic DEF is keyed by its FULL name (e.g.
    // `Roamed[T].roam`), so a bare-name equality check would miss `roam` — extract the tail first.
    private static string memberRoutineTail(string nameOrKey)
    {
        int lastDot = nameOrKey.LastIndexOf(value: '.');
        string tail = lastDot >= 0 ? nameOrKey[(lastDot + 1)..] : nameOrKey;
        int cut = tail.IndexOfAny(anyOf: ['(', '[']);
        return cut >= 0 ? tail[..cut] : tail;
    }

    // The bare memberRoutine/routine name a call resolves to (for store-primitive detection). Mirrors
    // ScopeTeardownLoweringPass.CalleeName so the copy pass and the teardown pass agree on which calls
    // are store primitives.
    private static string? CalleeName(Expression callee) => callee switch
    {
        MemberExpression m => m.MemberName,
        IdentifierExpression id => id.Name,
        _ => null
    };

    // The OWNER type's base name of a `Owner.MemberRoutine` routine name/key (strips generic args + module path):
    // `Core.Roamed[Main.Box].roam` -> `Roamed`. Empty for a free routine.
    private static string OwnerBase(string nameOrKey)
    {
        int lastDot = nameOrKey.LastIndexOf(value: '.');
        if (lastDot < 0) return "";
        string owner = TypeInfo.StripTypeArgs(name: nameOrKey[..lastDot]);
        int od = owner.LastIndexOf(value: '.');
        return od >= 0 ? owner[(od + 1)..] : owner;
    }

    // True when the routine is a memberRoutine of an RC wrapper (Retained/Tracked/Guarded/Witnessed/Roamed). Inside ANY
    // such memberRoutine `me` is the primitive handle — retain-copying it makes the memberRoutine call the wrapper's copy verb
    // (`roam`), and the copy verb itself calls other wrapper memberRoutines (controller_address, …) which would ALSO
    // get `me.roam()` injected → mutual recursion (StackOverflow). So suppress all injection in EVERY RC-wrapper
    // memberRoutine body, not just the copy verb. AST-declaration sites (Run / LowerMemberList) and variant-body keys
    // only carry the routine NAME string, so the owner is parsed out of it; the instantiated-generic site has a
    // structural `RoutineInfo.OwnerType` and uses `OwnerTypeIsRcWrapper` instead.
    private static bool OwnerNameIsRcWrapper(string nameOrKey) =>
        OwnerBaseIsRcWrapper(ownerBase: OwnerBase(nameOrKey: nameOrKey));

    // Structural form for AST-declaration sites: the parser's bare OwnerName already IS the owner base
    // (== OwnerBase applied to the composite), so no QualifiedName re-parsing is needed. A null owner
    // (free routine) is never an RC wrapper.
    private static bool OwnerBaseIsRcWrapper(string? ownerBase) =>
        ownerBase is not null && RuntimeContract.RcWrapperBaseNames.Contains(item: ownerBase);

    // Structural form of the above: the routine's OwnerType is an RC wrapper record. Preferred wherever a
    // `RoutineInfo` is on hand — delegates to the same registry check as `IsRcWrapperType`, no name parsing.
    private static bool OwnerTypeIsRcWrapper(TypeInfo? owner) =>
        owner is not null && TypeRegistry.GetRcWrapperBaseName(type: owner) is not null;

    // True when the type is an RC wrapper record (Retained/Tracked/Guarded/Witnessed/Roamed). A field of such a
    // type has its release-old/retain-new RC owned by codegen (isRoamedField), so the copy pass must NOT also
    // retain a field-write RHS of this type (double-count). Delegates to the registry's canonical
    // structural check (matches on GenericDefinition/WrapperTypeInfo) — no ad-hoc name parsing here.
    private static bool IsRcWrapperType(TypeInfo? type) =>
        type is not null && Compiler.Declaration.TypeRegistry.GetRcWrapperBaseName(type: type) is not null;

    // True when the routine (identified by name OR composite key) is one of the duplication verbs whose
    // body must NOT have retain-injection applied to its `me`/identity references: `assign` (the identity
    // shallow-copy primitive — `return me` there must stay bitwise, else `me.assign()` recurses) and
    // `duplicate` (the deep-copy verb — its `me` field-walk / `return me` arms must not gain a spurious
    // retain, which unbalances the refcount → heap corruption). Uses the STRUCTURED member-routine tail
    // (strips Owner./params/typeargs) compared to the canonical contract constants — never a `.Contains`
    // substring scan of the composite key.
    private static bool IsCopyVerbRoutine(string nameOrKey)
    {
        string tail = memberRoutineTail(nameOrKey: nameOrKey);
        return tail == RuntimeContract.Duplication.Assign || tail == RuntimeContract.Duplication.Duplicate;
    }

    // Populates <see cref="_borrowParamNames"/> with the routine's MANAGED (retaining-store) record
    // params — the borrow params a `return` must retain (see the field doc). `me` and value/entity/
    // borrow-tier params are excluded (a returned value record is a bitwise move, an entity/token is
    // never retain-copied).
    private void SetBorrowParams(IReadOnlyList<Parameter>? parameters)
    {
        _borrowParamNames.Clear();
        if (parameters is null) return;
        foreach (Parameter p in parameters)
        {
            if (p.Name == "me") continue;
            TypeInfo? pt = p.Type?.ResolvedType;
            if (pt != null && NeedsRetainingCopy(type: pt, copyMemberRoutine: out _))
                _borrowParamNames.Add(item: p.Name);
        }
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
        => BodyDispatch.RunOnProgram(program, lower: LowerCopyRoutineBody);

    /// <summary>
    /// Per-routine copy-lowering shared by the program and member-list sweeps: set the copy-verb /
    /// RC-wrapper flags and borrow-param set off the routine before lowering its body.
    /// </summary>
    private Statement LowerCopyRoutineBody(RoutineDeclaration r)
    {
        _inCopyRoutine = IsCopyVerbRoutine(nameOrKey: r.QualifiedName);
        _inRcCopyVerb = OwnerBaseIsRcWrapper(ownerBase: r.OwnerName);
        SetBorrowParams(parameters: r.Parameters);
        return LowerStatement(stmt: r.Body);
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
        => BodyDispatch.RunOnVariantBodies(ctx.VariantBodies, lower: (key, body) =>
        {
            // A variant's deep `copy` body (BuildVariantCopyBody) has an `else => return me` arm for
            // its non-destructible (scalar/None) arms. Since a destructible-arm variant now carries a
            // GetLifecycle.Store, that bare `return me` would otherwise re-inject `me.copy()` → infinite
            // recursion. Treat the copy body like `store`: its `return me` is the identity primitive.
            _inCopyRoutine = IsCopyVerbRoutine(nameOrKey: key);
            _inRcCopyVerb = OwnerNameIsRcWrapper(nameOrKey: key);
            SetBorrowParams(parameters: null); // variant/synthesized bodies carry no parameter list here
            return LowerStatement(stmt: body);
        });

    /// <summary>
    /// Injects retaining <c>store</c> into instantiated generic routine bodies. Phase 7's
    /// <c>GenericMonomorphizationPass</c> populates <c>InstantiatedGenericBodies</c> AFTER the
    /// Phase 8 RunGlobal sweep, so those bodies miss the regular per-program copy-lowering. Without
    /// this, a monomorphized routine like <c>BTreeDictNode[Text, S64].entry_get</c> returns a
    /// <c>DictEntry</c> whose <c>Text</c> key is a non-retained alias — torn down per use, then
    /// freed again at container teardown (double-free). Mirrors
    /// <see cref="OperatorLoweringPass.RunOnInstantiatedGenericBodies"/>.
    /// Caller passes the map directly (PostprocessingContext doesn't hold it).
    /// </summary>
    public void RunOnInstantiatedGenericBodies(
        Dictionary<string, MonomorphizedBody> instantiatedGenericBodies)
        => BodyDispatch.RunOnInstantiatedGenericBodies(instantiatedGenericBodies, lower: (key, entry) =>
        {
            _inCopyRoutine = IsCopyVerbRoutine(nameOrKey: key);
            _inRcCopyVerb = OwnerTypeIsRcWrapper(owner: entry.Info.OwnerType);
            SetBorrowParams(parameters: entry.Ast.Parameters);
            return LowerStatement(stmt: entry.Ast.Body);
        });

    // Statement walker

    private Statement LowerBlockStatement(BlockStatement block)
    {
        bool changed = false;
        var newStmts = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement s in block.Statements)
        {
            Statement ns = LowerStatement(stmt: s);
            newStmts.Add(item: ns);
            if (!ReferenceEquals(ns, s)) changed = true;
        }
        return changed ? block with { Statements = newStmts } : block;
    }

    private Statement LowerIfStatement(IfStatement ifStmt, Statement stmt)
    {
        // The condition holds copy positions: `when`/`is` desugar to an if whose condition
        // is e.g. `(x.cmp(you: v)).eq(ME_SMALL)`, where `v` is a call argument needing a
        // retaining `store`. Without lowering the condition, the callee `destroy`s the
        // by-value param while the caller never copied it -> double-free of the reused source.
        Expression newCond = StripStealFromExpr(expr: ifStmt.Condition);
        Statement newThen = LowerStatement(stmt: ifStmt.ThenStatement);
        Statement? newElse = ifStmt.ElseStatement != null
            ? LowerStatement(stmt: ifStmt.ElseStatement)
            : null;
        bool changed = !ReferenceEquals(newCond, ifStmt.Condition) ||
                       !ReferenceEquals(newThen, ifStmt.ThenStatement) ||
                       !ReferenceEquals(newElse, ifStmt.ElseStatement);
        return changed
            ? ifStmt with
            {
                Condition = newCond,
                ThenStatement = newThen,
                ElseStatement = newElse
            }
            : stmt;
    }

    private Statement LowerWhenStatement(WhenStatement whenStmt, Statement stmt)
    {
        bool changed = false;
        // The subject holds copy positions too (e.g. `when x.cmp(you: v) is ...` — `v` is a
        // call argument that needs a retaining `store`). Mirrors the WhenExpression case.
        Expression newSubject = StripStealFromExpr(expr: whenStmt.Expression);
        if (!ReferenceEquals(newSubject, whenStmt.Expression))
            changed = true;

        var newClauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);
        foreach (WhenClause clause in whenStmt.Clauses)
        {
            Statement newBody = LowerStatement(stmt: clause.Body);
            if (!ReferenceEquals(newBody, clause.Body))
            {
                newClauses.Add(item: clause with { Body = newBody });
                changed = true;
            }
            else
            {
                newClauses.Add(item: clause);
            }
        }
        return changed
            ? whenStmt with { Expression = newSubject, Clauses = newClauses }
            : stmt;
    }

    /// <summary>
    /// Lower statement as part of this compiler phase.
    /// </summary>
    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return LowerBlockStatement(block: block);

            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
                return LowerDeclarationStatement(ds: ds, vd: vd, stmt: stmt);

            case AssignmentStatement assign:
            {
                Expression lowered = LowerOwnership(expr: assign.Value, isReturn: false);
                return ReferenceEquals(lowered, assign.Value)
                    ? stmt
                    : assign with { Value = lowered };
            }

            case ReturnStatement { Value: not null } ret:
            {
                Expression lowered = LowerOwnership(expr: ret.Value, isReturn: true);
                return ReferenceEquals(lowered, ret.Value)
                    ? stmt
                    : ret with { Value = lowered };
            }

            case VariantReturnStatement { Value: not null } vrs:
            {
                Expression lowered = LowerOwnership(expr: vrs.Value, isReturn: true);
                return ReferenceEquals(lowered, vrs.Value)
                    ? stmt
                    : vrs with { Value = lowered };
            }

            case IfStatement ifStmt:
                return LowerIfStatement(ifStmt: ifStmt, stmt: stmt);

            case WhileStatement whileStmt:
                return LowerWhileStatement(whileStmt: whileStmt, stmt: stmt);

            case LoopStatement loopStmt:
            {
                Statement newBody = LowerStatement(stmt: loopStmt.Body);
                return ReferenceEquals(newBody, loopStmt.Body)
                    ? stmt
                    : loopStmt with { Body = newBody };
            }

            case EachStatement eachStmt:
            {
                Statement newBody = LowerStatement(stmt: eachStmt.Body);
                return ReferenceEquals(newBody, eachStmt.Body)
                    ? stmt
                    : eachStmt with { Body = newBody };
            }

            case WhenStatement whenStmt:
                return LowerWhenStatement(whenStmt: whenStmt, stmt: stmt);

            case UsingStatement usingStmt:
                return LowerUsingStatement(usingStmt: usingStmt, stmt: stmt);

            case DangerStatement danger:
                return LowerDangerStatement(danger: danger, stmt: stmt);

            case ExpressionStatement es:
            {
                Expression stripped = StripStealFromExpr(expr: es.Expression);
                return ReferenceEquals(stripped, es.Expression)
                    ? stmt
                    : es with { Expression = stripped };
            }

            case DiscardStatement ds:
            {
                // Lower discard to a plain expression statement -> the return value is
                // already dropped by not assigning it. The 'discard' keyword is codegen noise.
                Expression stripped = StripStealFromExpr(expr: ds.Expression);
                return new ExpressionStatement(Expression: stripped, Location: ds.Location);
            }

            default:
                return stmt;
        }
    }

    private Statement LowerDeclarationStatement(DeclarationStatement ds, VariableDeclaration vd, Statement stmt)
    {
        // A carrier PAYLOAD EXTRACTION — `<Maybe/Result/Lookup>.value` (a MemberExpression on a
        // carrier), or the `CarrierPayloadExpression` the Result/Lookup path lowers to — is a VIEW
        // into a payload the carrier still owns, NOT an owning copy. It is the shape every `when`/
        // `each` element binding takes (`else var v -> …` from `try_emit()`'s `Maybe`). Retaining it
        // makes the loop element a co-owner (+1) that never escapes the each scope and is never
        // released → a per-iteration leak + needless churn. The carrier owns the payload for as long
        // as the binding is live (non-escaping), and any ESCAPING use retains at its own store site,
        // so skip the retain here. (dj: iter can't leave the each scope → no escape machinery.)
        //
        // A bare `<carrier>.value` extraction is a MOVE-OUT, not a copy, so it is not retained
        // here. Two shapes reach this: the non-escaping loop-element binding above, and
        // `strip_out`'s `return me.value` (spilled to `__td_ret_N` by ScopeTeardownLoweringPass) —
        // which hands the payload's SINGLE reference to the caller and empties the carrier
        // (`present = false`), the model a `Maybe[Entity]` requires (an entity cannot be co-owned).
        // NOTE: `unwrap`/`!!` does NOT reach here — it returns `me.value.copy()` (an explicit
        // co-owning duplicate in Maybe.rf; `copy` is the unified record/variant/managed-leaf
        // duplication verb, so a Copyable variant like SerialValue resolves via its arm-walk copy),
        // a CallExpression, so the carrier stays a co-owner and the caller gets an independent
        // reference (correct for record/value/managed payloads).
        if (IsCarrierPayloadExtraction(vd.Initializer))
            return stmt;
        Expression lowered = LowerOwnership(expr: vd.Initializer, isReturn: false);
        if (ReferenceEquals(lowered, vd.Initializer)) return stmt;
        var newVd = vd with { Initializer = lowered };
        return ds with { Declaration = newVd };
    }

    private Statement LowerWhileStatement(WhileStatement whileStmt, Statement stmt)
    {
        // Same as IfStatement: the loop condition can carry copy positions.
        Expression newCond = StripStealFromExpr(expr: whileStmt.Condition);
        Statement newBody = LowerStatement(stmt: whileStmt.Body);
        bool changed = !ReferenceEquals(newCond, whileStmt.Condition) ||
                       !ReferenceEquals(newBody, whileStmt.Body);
        return changed
            ? whileStmt with { Condition = newCond, Body = newBody }
            : stmt;
    }

    private Statement LowerUsingStatement(UsingStatement usingStmt, Statement stmt)
    {
        Statement newBody = LowerStatement(stmt: usingStmt.Body);
        Statement? fb = usingStmt.FallbackBody != null
            ? LowerStatement(stmt: usingStmt.FallbackBody)
            : null;
        return ReferenceEquals(newBody, usingStmt.Body)
               && ReferenceEquals(fb, usingStmt.FallbackBody)
            ? stmt
            : usingStmt with { Body = newBody, FallbackBody = fb };
    }

    private Statement LowerDangerStatement(DangerStatement danger, Statement stmt)
    {
        // A `danger` block is just a scoped block whose failable calls crash on failure.
        // Without recursing into its body, call arguments inside it never get their
        // retaining `store` — so the callee's by-value param `destroy` frees the CALLER's
        // value (e.g. `x.divmod!(other: b)` double-frees `b`). Recurse like any other block.
        Statement newBody = LowerStatement(stmt: danger.Body);
        return ReferenceEquals(newBody, danger.Body)
            ? stmt
            : danger with { Body = (BlockStatement)newBody };
    }

    // Core lowering

    /// <summary>
    /// Lowers a single expression sitting in an ownership/copy position (var-binding RHS,
    /// assignment RHS, return value, call argument). A "borrowed reference" — an identifier or
    /// field read — of a type that carries a retaining <c>store</c> (e.g. <c>Text</c>, whose
    /// <c>store</c> bumps the controller refcount) is rewritten to <c>expr.assign()</c> so the new
    /// owner holds its own reference, balancing the <c>destroy</c> that scope-teardown inserts.
    /// Explicit <see cref="StealExpression"/> is preserved (an explicit move, no copy). Fresh
    /// values (calls, constructors, arithmetic) are already owned and are only recursed for
    /// nested copy positions.
    /// </summary>
    /// <param name="isReturn">
    /// True at a return position. Returning a bare local <see cref="IdentifierExpression"/>
    /// transfers ownership out (scope-teardown skips the returned local), so it must NOT be
    /// copied — that would leak the local. A returned field (<see cref="MemberExpression"/>) is
    /// still copied, since the aggregate keeps its own reference.
    /// </param>
    /// <param name="expr">The expression to inspect and potentially wrap in a copy call.</param>
    private Expression LowerOwnership(Expression expr, bool isReturn)
    {
        // Preserve explicit steal so later stages can observe the ownership transfer site.
        if (expr is StealExpression steal)
            return steal;

        // TemporaryTeardownPass move-temps (`__rv_*` = a spilled reassignment RHS, `__tt_*` = a
        // spilled owned receiver) hold a FRESH single-use owned value that is moved, never shared, so
        // it must NOT be retaining-copied. Normally that pass runs after this one, but instantiated
        // generic bodies are re-lowered here post-monomorphization (after the def already grew the
        // temps), and copying `target = __rv` would leak the un-freed `__rv` every iteration.
        if (expr is IdentifierExpression { Name: var tn }
            && (tn.StartsWith(value: "__rv_", comparisonType: StringComparison.Ordinal)
                || tn.StartsWith(value: "__tt_", comparisonType: StringComparison.Ordinal)))
            return expr;

        // Inside a copy-verb body (`store` / variant `copy` / an RC-wrapper's refcount verb like `roam`),
        // `me` is the identity-copy primitive in EVERY position — not just `return me`. E.g. `Roamed.roam`
        // reinterprets `me` via `Hijacked[RoamController[T]](me)`; retain-copying that `me` argument would
        // make `roam` call `roam` → infinite recursion (StackOverflow). Never retain-copy `me` here.
        if (_inCopyRoutine && expr is IdentifierExpression { Name: "me" })
            return expr;

        if (!_inRcCopyVerb
            && expr is IdentifierExpression or MemberExpression
            && NeedsRetainingCopy(type: expr.ResolvedType, copyMemberRoutine: out RoutineInfo? copyMemberRoutine))
        {
            if (isReturn && expr is IdentifierExpression id)
            {
                // Returning the borrowed receiver `me` — or a borrowed managed PARAM — hands the caller
                // an owned value that aliases the caller's still-live argument, so it must be copied
                // (retained). Inside `store` itself `return me` is the identity primitive (excluded). Any
                // OTHER bare identifier is an owned local being moved out, so it is returned as-is.
                bool returningBorrowedReceiver = id.Name == "me" && !_inCopyRoutine;
                bool returningBorrowParam = _borrowParamNames.Contains(item: id.Name);
                if (!returningBorrowedReceiver && !returningBorrowParam)
                    return expr;
            }
            return MakeCopyCall(expr: expr, copyMemberRoutine: copyMemberRoutine!);
        }

        // (`a[i]` element reads are legitimized into an owned value by OperatorLoweringPass, which wraps the
        // raw `getitem` peek in the element type's `store` — no copy injection is needed for them here.)

        // For complex expressions in ownership positions (calls, constructors, etc.),
        // recurse into argument positions (which are themselves copy positions).
        return StripStealFromExpr(expr: expr);
    }

    /// <summary>
    /// Recursively lowers nested expressions, preserving explicit <see cref="StealExpression"/>
    /// markers and injecting a retaining <c>store</c> on borrowed-reference call arguments (each
    /// argument is a copy position — the callee's parameter is an independent owner that gets
    /// torn down at the callee's scope exit).
    /// </summary>
    private Expression StripStealFromExpr(Expression expr)
    {
        return expr switch
        {
            StealExpression steal => steal,
            CallExpression call => StripStealFromCall(call: call),
            CreatorExpression creator => StripStealFromCreator(creator: creator),
            GenericMemberRoutineCallExpression gmc => StripStealFromGenericMemberRoutineCall(gmc: gmc),
            BinaryExpression { Operator: BinaryOperator.Assign } bin => StripStealFromAssign(bin: bin),
            WhenExpression whenExpr => StripStealFromWhen(whenExpr: whenExpr),
            ConditionalExpression cond => StripStealFromConditional(cond: cond),
            // All other expression types: steal cannot appear as a direct child in practice
            // (steal only wraps identifier or member expressions).
            _ => expr
        };
    }

    private Expression StripStealFromCall(CallExpression call)
    {
        bool changed = false;
        // A store primitive (poke / store / store_element_ref) MOVES its argument into raw
        // storage — ScopeTeardownLoweringPass already marks that argument as moved (never torn
        // down). Injecting a retaining copy here would double-count: the copy is written into
        // memory while the un-released source keeps its own reference, leaking one ref per store
        // (e.g. List.add_last's `poke(value)` chain roamed the element TWICE — once for poke's
        // param, once for the inner `LLVM::store` arg — with neither released, so a cycle held
        // through a container never reaches its cycle-internal refcount and cc_collect can't
        // reap it). Pass store-primitive args through untouched to keep copy==teardown.
        bool isStorePrimitive = CalleeName(call.Callee) is { } cn
            && Declaration.RuntimeContract.StorePrimitives.Contains(item: cn);
        // A CONSTRUCTOR/conversion call (ConstructedType != null) persists its args into the new
        // value's fields — a DESTINATION, exactly like a CreatorExpression member-init — so its
        // borrowed-ref args must be retained (a bare struct copy would alias the source and
        // double-free at teardown). A store primitive is likewise a destination (writes raw
        // storage). Only a plain routine/memberRoutine call borrows its args.
        bool isDestination = isStorePrimitive || call.ConstructedType is not null;
        var args = new List<Expression>(capacity: call.Arguments.Count);
        foreach (Expression arg in call.Arguments)
        {
            // Three-rules model: a REGULAR (borrow) call arg passes AS-IS — the caller keeps
            // ownership, retain lives at the DESTINATION, and a fresh rvalue arg is torn down at
            // the caller by TemporaryTeardownPass. A DESTINATION call (constructor/conversion/
            // store primitive) retains its managed value args here.
            Expression s = isDestination ? LowerArgument(arg: arg) : LowerBorrowArgument(arg: arg);
            args.Add(item: s);
            if (!ReferenceEquals(s, arg)) changed = true;
        }

        // Recurse into the receiver chain so nested-call arguments get their
        // retain copies even when the inner call sits in *receiver* position.
        // e.g. f-string lowering turns `f"{c1 == c2}"` into
        // `(c1.eq(c2)).represent()`: the `eq` whose argument `c2` must be
        // copied lives in `Callee.Object`, not in any argument list. We recurse
        // via StripStealFromExpr (not LowerOwnership) so the receiver itself
        // stays borrowed — only nested argument positions get a `store`.
        Expression callee = call.Callee;
        if (callee is MemberExpression cm)
        {
            Expression newObj = StripStealFromExpr(expr: cm.Object);
            if (!ReferenceEquals(newObj, cm.Object))
            {
                callee = cm with { Object = newObj };
                changed = true;
            }
        }

        return changed ? call with { Arguments = args, Callee = callee } : call;
    }

    private Expression StripStealFromCreator(CreatorExpression creator)
    {
        // A constructor's member-variable initializers are copy positions, exactly like call
        // arguments: each becomes an independent field of the new aggregate, so a borrowed
        // reference must be retained. Without this, `DictEntry(key: someText)` aliased the
        // source's controller and double-freed when the entry was later torn down.
        bool changed = false;
        var members = new List<(string Name, Expression Value)>(capacity: creator.MemberVariables.Count);
        foreach ((string Name, Expression Value) mv in creator.MemberVariables)
        {
            Expression s = LowerOwnership(expr: mv.Value, isReturn: false);
            members.Add(item: (mv.Name, s));
            if (!ReferenceEquals(s, mv.Value)) changed = true;
        }

        return changed ? creator with { MemberVariables = members } : creator;
    }

    private Expression StripStealFromGenericMemberRoutineCall(GenericMemberRoutineCallExpression gmc)
    {
        bool changed = false;
        // Store-primitive move semantics (see the CallExpression case) — e.g. poke lowers to
        // `LLVM::store[T](me, value)`, a generic memberRoutine call whose `value` arg is moved into
        // memory and must NOT be retain-copied here.
        bool isStorePrimitiveG = CalleeName(gmc.Object) is { } gcn
            && Declaration.RuntimeContract.StorePrimitives.Contains(item: gcn);
        bool isDestinationG = isStorePrimitiveG || gmc.ConstructedType is not null;
        var args = new List<Expression>(capacity: gmc.Arguments.Count);
        foreach (Expression arg in gmc.Arguments)
        {
            // See the CallExpression case: regular arg = borrow (as-is); destination (constructor/
            // conversion/store-primitive) arg = retain.
            Expression s = isDestinationG ? LowerArgument(arg: arg) : LowerBorrowArgument(arg: arg);
            args.Add(item: s);
            if (!ReferenceEquals(s, arg)) changed = true;
        }

        // Same receiver-chain recursion as CallExpression (see note above).
        Expression newReceiver = StripStealFromExpr(expr: gmc.Object);
        if (!ReferenceEquals(newReceiver, gmc.Object)) changed = true;

        return changed ? gmc with { Arguments = args, Object = newReceiver } : gmc;
    }

    // User assignment `target = value` reaches here as a BinaryExpression(Assign) wrapped
    // in an ExpressionStatement (the AssignmentStatement node is only for synthesized
    // bodies). The RHS is a copy position exactly like a var-init or call argument: a
    // borrowed-reference value (e.g. a `Text` local) must be retained, otherwise
    // scope-teardown's `destroy` of the source frees the buffer the target now aliases
    // (use-after-free). Mirrors the AssignmentStatement / DeclarationStatement handling.
    private Expression StripStealFromAssign(BinaryExpression bin)
    {
        // Field-write to an RC-wrapper / Roamed field: codegen owns the release-old + retain-new RC
        // (isRoamedField), so retaining the RHS here too DOUBLE-counts — an SF self-cycle
        // (`a.next = a`) then never reaches refcount 0 and cc_collect reclaims nothing. Recurse for
        // nested copies but do NOT retain the top-level RHS. Local / non-RC-field targets keep the
        // normal retain (managed leaves like Text ARE pass-owned).
        if (bin.Left is MemberExpression lhsm && IsRcWrapperType(type: lhsm.ResolvedType))
            return bin with { Right = StripStealFromExpr(expr: bin.Right) };
        Expression newRight = LowerOwnership(expr: bin.Right, isReturn: false);
        return ReferenceEquals(newRight, bin.Right) ? bin : bin with { Right = newRight };
    }

    // A `when` used as an EXPRESSION (e.g. `acc +% when x.cmp(you: v) is ... => ...`).
    // The subject and the arm bodies hold copy positions (call arguments, RHS values) that
    // must be lowered — without this, an argument like `v` inside the subject call never
    // gets its retaining `store`, yet the callee (e.g. `Integer.cmp`) still `destroy`s the
    // by-value parameter, double-freeing the reused source on the next iteration.
    private Expression StripStealFromWhen(WhenExpression whenExpr)
    {
        bool changed = false;
        Expression? newSubject = whenExpr.Expression is { } subj
            ? StripStealFromExpr(expr: subj)
            : null;
        if (newSubject is not null && !ReferenceEquals(newSubject, whenExpr.Expression))
            changed = true;

        var newClauses = new List<WhenClause>(capacity: whenExpr.Clauses.Count);
        foreach (WhenClause clause in whenExpr.Clauses)
        {
            Statement nb = LowerStatement(stmt: clause.Body);
            if (!ReferenceEquals(nb, clause.Body))
            {
                newClauses.Add(item: clause with { Body = nb });
                changed = true;
            }
            else
            {
                newClauses.Add(item: clause);
            }
        }

        return changed
            ? whenExpr with { Expression = newSubject, Clauses = newClauses }
            : whenExpr;
    }

    // Top-level conditional expression (`if c then a else b`). The branches are copy
    // positions (each can be the result value); the condition is recursed for nested args.
    private Expression StripStealFromConditional(ConditionalExpression cond)
    {
        Expression newCond = StripStealFromExpr(expr: cond.Condition);
        Expression newTrue = LowerOwnership(expr: cond.TrueExpression, isReturn: false);
        Expression newFalse = LowerOwnership(expr: cond.FalseExpression, isReturn: false);
        return ReferenceEquals(newCond, cond.Condition)
            && ReferenceEquals(newTrue, cond.TrueExpression)
            && ReferenceEquals(newFalse, cond.FalseExpression)
            ? cond
            : cond with
            {
                Condition = newCond,
                TrueExpression = newTrue,
                FalseExpression = newFalse
            };
    }

    /// <summary>
    /// Lowers a single call argument as a copy position, transparently handling the
    /// <see cref="NamedArgumentExpression"/> wrapper required by named-argument calls (S510).
    /// </summary>
    private Expression LowerArgument(Expression arg)
    {
        if (arg is NamedArgumentExpression named)
        {
            Expression loweredValue = LowerOwnership(expr: named.Value, isReturn: false);
            return ReferenceEquals(loweredValue, named.Value) ? named : named with { Value = loweredValue };
        }
        return LowerOwnership(expr: arg, isReturn: false);
    }

    /// <summary>
    /// Lowers a BORROW call argument (three-rules model): the value passes AS-IS — no retaining
    /// <c>store</c> at the boundary, since the caller keeps ownership and the callee only borrows.
    /// We still RECURSE (via <see cref="StripStealFromExpr"/>, not <see cref="LowerOwnership"/>) so that
    /// nested DESTINATIONS inside the argument — a constructor's member-inits, a store-primitive value,
    /// a <c>when</c>/conditional result position — keep their retains. Preserves the
    /// <see cref="NamedArgumentExpression"/> wrapper (S510).
    /// </summary>
    private Expression LowerBorrowArgument(Expression arg)
    {
        if (arg is NamedArgumentExpression named)
        {
            Expression v = StripStealFromExpr(expr: named.Value);
            return ReferenceEquals(v, named.Value) ? named : named with { Value = v };
        }
        return StripStealFromExpr(expr: arg);
    }

    /// <summary>
    /// True when copying a value of <paramref name="type"/> must go through its <c>store</c> rather
    /// than a bitwise duplicate: the type declares a non-synthesized (hand-written) <c>store</c>,
    /// which is how leaf managed types like <c>Text</c> bump their refcount. Trivially-copyable
    /// records have only a synthesized identity <c>store</c> and need no injection.
    /// </summary>
    private bool NeedsRetainingCopy(TypeInfo? type, out RoutineInfo? copyMemberRoutine)
    {
        copyMemberRoutine = null;
        if (type == null) return false;
        // Same unified decision the teardown pass uses (TypeRegistry.GetLifecycle): a retaining copy
        // is needed iff the type has a hand-written (non-synthesized) zero-arg `store` — restricted to
        // records and excluding the borrow tier — resolved through GetOwnMemberRoutinesResolved so generic
        // resolutions (e.g. Maybe[Text]) agree with what teardown sees for the same type.
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: type);
        copyMemberRoutine = lc.Store;
        return !lc.IsBorrow && copyMemberRoutine != null;
    }

    /// <summary>
    /// Builds a fully resolved <c>expr.assign()</c> call. The routine and lowering kind are stamped
    /// here (not left for a later pass) so codegen materializes the <c>store</c> return value — an
    /// unresolved call is emitted as a discarded <c>void</c> call, dropping the retained copy and
    /// leaving the binding with a dangling reference.
    /// </summary>
    /// <summary>
    /// True when <paramref name="init"/> extracts a carrier's inline payload — a <c>CarrierPayloadExpression</c>
    /// (the Result/Lookup arm-binding form) or a member access <c>&lt;carrier&gt;.value</c> where the receiver
    /// is a <c>Maybe</c>/<c>Result</c>/<c>Lookup</c> carrier (the <c>Maybe</c> arm-binding form, e.g. an
    /// <c>each</c>/<c>when</c> element). Such a binding is a VIEW into a payload the carrier owns, so it must
    /// not be retained as an owning copy.
    /// </summary>
    private static bool IsCarrierPayloadExtraction(Expression init) =>
        init is CarrierPayloadExpression
        || (init is MemberExpression { Object.ResolvedType: RecordTypeInfo { CarrierKind: not TypeModel.Enums.CarrierKind.None } });

    private static Expression MakeCopyCall(Expression expr, RoutineInfo copyMemberRoutine)
    {
        // Use the resolved memberRoutine's own name for the property: records/managed-leaves retain via
        // `store`, but a variant's deep copy is `copy` (BuildVariantCopyBody). Codegen dispatches on
        // ResolvedRoutine, but keeping the property name in sync avoids a misleading `store` label on
        // a `copy` call and any name-based lookup drifting.
        var callee = new MemberExpression(Object: expr, MemberName: copyMemberRoutine.Name, Location: expr.Location)
            { ResolvedType = expr.ResolvedType };
        return new CallExpression(Callee: callee, Arguments: [], Location: expr.Location)
        {
            ResolvedRoutine = copyMemberRoutine,
            ResolvedType = expr.ResolvedType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: copyMemberRoutine)
        };
    }
}
