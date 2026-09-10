using Builder.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification;

namespace Builder.Lowering.Passes;

/// <summary>
/// Inserts explicit <c>local.destroy()</c> calls at scope exits — the unified teardown lowering.
/// After this pass codegen performs NO teardown of its own: every owned local/param is destroyed
/// by a real <see cref="CallExpression"/> in the AST, so reachability and monomorphization see the
/// calls naturally and codegen just emits them.
///
/// <para>Teardown is <b>block-scoped</b> (RAII): a local is destroyed at the end of (and on every
/// escape from) the block that declares it. Because control only reaches a block's end / escape
/// after the declaration executed, the local is always initialized at teardown — no null-guard or
/// zero-init dance is needed (unlike the prior function-scoped codegen teardown).</para>
///
/// <para>Insertion rules per exit (locals destroyed in reverse declaration order):</para>
/// <list type="bullet">
///   <item><see cref="ReturnStatement"/> / <see cref="AbsentStatement"/> / <see cref="ThrowStatement"/>
///         / <see cref="VariantReturnStatement"/> — destroy ALL live owned locals/params, except the
///         value being returned.</item>
///   <item><see cref="BreakStatement"/> / <see cref="ContinueStatement"/> — destroy owned locals
///         declared inside the enclosing loop.</item>
///   <item>Block fall-through end — destroy the locals declared in that block.</item>
/// </list>
///
/// <para>Move exclusion: a local that is moved anywhere in the routine (operand of <c>steal</c>, or
/// receiver of <c>.retain()</c>/<c>.track()</c>) is never destroyed — ownership left the binding.
/// This is flow-insensitive, mirroring the prior codegen behaviour.</para>
/// </summary>
internal sealed class ScopeTeardownLoweringPass(PostprocessingContext ctx)
{
    private readonly TypeSymbol? _blankType = ctx.Registry.LookupType(name: "None");
    private int _spillCounter;

    /// <summary>A live owned binding: its name, type, and resolved <c>destroy</c> routine.
    /// An entity binding always holds a valid owned allocation while live (a lateinit zeroed
    /// placeholder, the declaration initializer, or a later-assigned value).</summary>
    private readonly record struct Owned(string Name, TypeSymbol Type, RoutineInfo Destroy);

    public void Run(Program program)
    {
        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[index: i])
            {
                case RoutineDeclaration r:
                    program.Declarations[index: i] = LowerRoutine(r: r);
                    break;
                case EntityDeclaration e:
                    LowerMemberList(members: e.Members);
                    break;
                case RecordDeclaration rec:
                    LowerMemberList(members: rec.Members);
                    break;
                case CrashableDeclaration cr:
                    LowerMemberList(members: cr.Members);
                    break;
            }
        }
    }

    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            // Variant/synthesized bodies have no parameter list available here; teardown applies to
            // their block-local owned vars only.
            _movedNames.Clear();
            CollectMovedNames(stmt: ctx.VariantBodies[key: key]);
            var live = new List<Owned>();
            Statement lowered = LowerStatement(stmt: ctx.VariantBodies[key: key],
                live: live,
                loopBoundary: 0);
            ctx.VariantBodies[key: key] = lowered;
        }
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int j = 0; j < members.Count; j++)
        {
            if (members[index: j] is RoutineDeclaration m)
            {
                members[index: j] = LowerRoutine(r: m);
            }
        }
    }

    private readonly HashSet<string> _movedNames = new(comparer: StringComparer.Ordinal);

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        _movedNames.Clear();
        CollectMovedNames(stmt: r.Body);
        // Merge SA's authoritative per-routine "stolen / out of scope" record. `steal` takes a
        // binding out of scope (ownership moves to the callee, which destroys the content), but the
        // `steal` AST wrapper is normalized away during arg lowering (e.g. `Text(from_list: steal
        // digits)` → `Text(digits)`), so the AST move pre-scan above can miss it. SA recorded it via
        // deadref tracking; trust that so a stolen binding is never torn down here (double-free).
        if (r.StolenVariableNames is { Count: > 0 } stolen)
        {
            _movedNames.UnionWith(other: stolen);
        }

        // THREE-RULES param model (Rule 2): passing into a routine param is either 먹튀 (`steal`, move
        // ownership in — a bare ENTITY the caller relinquished) OR handing over a borrow. A RECORD param
        // (value record / Text / RC wrapper / access token) is always a BORROW — the caller retains
        // ownership and tears it down at ITS scope exit, so the callee must NOT destroy it. Only a bare
        // `EntityTypeSymbol` param is consuming (RF-S413 forces `steal` at the call site), owned by the
        // routine and torn down at every exit exactly like a top-level local. The retain that used to
        // balance a callee-owned record param at the arg boundary is gone (RecordCopyLoweringPass no
        // longer stores at the boundary); retain now lives at the DESTINATION (store primitive / field
        // write), and a fresh rvalue record arg is torn down at the CALLER (TemporaryTeardownPass).
        //
        // Suflae exception: an SF entity parameter is ALSO a borrowed handle — the caller keeps
        // ownership, so destroying it here double-frees the caller's live entity. After representation
        // unification (SignatureResolver.MaybeRoamSuflaeEntity) an SF entity param resolves to `Roamed[E]`
        // (a RecordTypeSymbol) which is a record → borrow by the rule above anyway; a bare `EntityTypeSymbol`
        // param in SF is skipped here (it would otherwise be consuming per the RF rule).
        bool isSuflae = ctx.Registry.Language == TypeModel.Enums.Language.Suflae;
        var paramLive = new List<Owned>();
        foreach (Parameter p in r.Parameters)
        {
            if (p.Name == "me")
            {
                continue;
            }

            if (_movedNames.Contains(item: p.Name))
            {
                continue;
            }

            TypeSymbol? pt = p.Type?.ResolvedType;
            // Only a bare-entity (consuming / steal'd) param is owned by the callee; every record param
            // is a borrow the caller still owns.
            if (pt is not EntityTypeSymbol)
            {
                continue;
            }

            if (isSuflae)
            {
                continue;
            }

            if (TryResolveDestroy(type: pt, destroy: out RoutineInfo? d) && d != null)
            {
                paramLive.Add(item: new Owned(Name: p.Name, Type: pt, Destroy: d));
            }
        }

        Statement newBody = LowerStatement(stmt: r.Body, live: paramLive, loopBoundary: 0);
        return r.Body == newBody && paramLive.Count == 0
            ? r
            : r with { Body = newBody };
    }

    /// <summary>
    /// Lowers a statement. <paramref name="live"/> is the ordered list of owned bindings live on
    /// entry (params + enclosing-block locals). <paramref name="loopBoundary"/> is the index in
    /// <paramref name="live"/> marking the nearest enclosing loop's entry (locals at index &gt;=
    /// boundary were declared inside the loop). The caller owns <paramref name="live"/>; this memberRoutine
    /// copies before recursing into nested scopes.
    /// </summary>
    private Statement LowerStatement(Statement stmt, List<Owned> live, int loopBoundary)
    {
        switch (stmt)
        {
            case BlockStatement b:
                return LowerBlock(block: b, outerLive: live, loopBoundary: loopBoundary);

            case IfStatement ifs:
                return LowerIfStatement(ifs: ifs, live: live, loopBoundary: loopBoundary);

            case WhenStatement w:
                return LowerWhenStatement(w: w, live: live, loopBoundary: loopBoundary);

            case LoopStatement loop:
            {
                // Locals declared inside the loop body sit at index >= live.Count on entry, so the
                // new boundary is the current live count.
                Statement body = LowerStatement(stmt: loop.Body,
                    live: Copy(live: live),
                    loopBoundary: live.Count);
                return loop with { Body = body };
            }

            case DangerStatement d:
                return d with
                {
                    Body = (BlockStatement)LowerStatement(stmt: d.Body,
                        live: Copy(live: live),
                        loopBoundary: loopBoundary)
                };

            // At Phase 7 a user `using` is still a UsingStatement (UsingLoweringPass runs later in
            // Phase 8). Recurse into the body so inner owned locals are torn down, but leave the
            // resource itself alone — its lifetime is governed by the `enter`/`exit` that
            // UsingLoweringPass injects. (In already-lowered stdlib bodies the `using` is gone; its
            // `__uf_` temporaries are skipped by IsUsingBinding instead.)
            case UsingStatement u:
                return LowerUsingStatement(u: u, live: live, loopBoundary: loopBoundary);

            case ReturnStatement or AbsentStatement or ThrowStatement or VariantReturnStatement:
                return PrefixDestroys(exit: stmt,
                    live: live,
                    from: 0,
                    skip: ReturnedName(stmt: stmt));

            case BreakStatement or ContinueStatement:
                return PrefixDestroys(exit: stmt,
                    live: live,
                    from: loopBoundary,
                    skip: null);

            // An owned entity local always holds a valid owned allocation at a reassignment —
            // a `lateinit` binding holds the zeroed placeholder or a prior value, a plain
            // binding was initialized at declaration (block-scoped teardown guarantees the
            // declaration executed). So reassignment must destroy the current content first
            // or it leaks: the branch-init pattern (`lateinit var x: T` then `x = T(...)` per
            // branch) would leak a placeholder per run, and `var r = T(...) ; r = T(...)`
            // would leak the first allocation. Bindings excluded from `live` (stolen, view,
            // using, no destroy) are correctly skipped — same trust as scope-exit teardown.
            // The RHS is spilled to a temp before the destroy so it may still read the
            // old value; a bare-identifier RHS is a move and needs no spill. User assignments
            // are still in operator form here (ExpressionStatement of `=` BinaryExpression —
            // codegen's EmitBinaryAssign); AssignmentStatement appears in synthesized bodies.
            case AssignmentStatement a when a.Target is IdentifierExpression target &&
                                            FindOwnedEntity(live: live, name: target.Name) is
                                                { } owned:
                return LowerEntityReassign(original: a,
                    rhs: a.Value,
                    rebuild: rhs => a with { Value = rhs },
                    owned: owned);

            case ExpressionStatement
            {
                Expression: BinaryExpression
                {
                    Operator: BinaryOperator.Assign, Left: IdentifierExpression target
                } bin
            } es when FindOwnedEntity(live: live, name: target.Name) is { } owned:
                return LowerEntityReassign(original: es,
                    rhs: bin.Right,
                    rebuild: rhs => es with { Expression = bin with { Right = rhs } },
                    owned: owned);

            // Roamed[T] entity-FIELD reassignment (`me.f = EXPR`). Unlike a local, a field is torn down
            // by its CONTAINER's teardown, not scope exit, so overwriting it orphans the old co-owned
            // handle — it must be released HERE or it leaks. (This is the one job of the deleted
            // RcRetainLoweringPass.ReassignRelease; the retain-NEW on the RHS is SF's implicit `.share()`
            // from SuflaeEntityLoweringPass, which runs before this pass, so `rhs` already carries it.)
            case AssignmentStatement a when a.Target is MemberExpression mt:
                return LowerRoamedFieldReassign(original: a,
                    target: mt,
                    rhs: a.Value,
                    rebuild: rhs => a with { Value = rhs });

            case ExpressionStatement
            {
                Expression: BinaryExpression
                {
                    Operator: BinaryOperator.Assign, Left: MemberExpression mt
                } bin
            } es:
                return LowerRoamedFieldReassign(original: es,
                    target: mt,
                    rhs: bin.Right,
                    rebuild: rhs => es with { Expression = bin with { Right = rhs } });

            default:
                return stmt;
        }
    }

    private Statement LowerIfStatement(IfStatement ifs, List<Owned> live, int loopBoundary)
    {
        Statement then = LowerStatement(stmt: ifs.ThenStatement,
            live: Copy(live: live),
            loopBoundary: loopBoundary);
        Statement? elseS = ifs.ElseStatement != null
            ? LowerStatement(stmt: ifs.ElseStatement,
                live: Copy(live: live),
                loopBoundary: loopBoundary)
            : null;
        return ifs with { ThenStatement = then, ElseStatement = elseS };
    }

    private Statement LowerWhenStatement(WhenStatement w, List<Owned> live, int loopBoundary)
    {
        var clauses = w.Clauses
                       .Select(selector: c => c with
                        {
                            Body = LowerStatement(stmt: c.Body,
                                live: Copy(live: live),
                                loopBoundary: loopBoundary)
                        })
                       .ToList();
        return w with { Clauses = clauses };
    }

    private Statement LowerUsingStatement(UsingStatement u, List<Owned> live, int loopBoundary)
    {
        return u with
        {
            Body =
            LowerStatement(stmt: u.Body, live: Copy(live: live), loopBoundary: loopBoundary),
            FallbackBody = u.FallbackBody != null
                ? LowerStatement(stmt: u.FallbackBody,
                    live: Copy(live: live),
                    loopBoundary: loopBoundary)
                : null
        };
    }

    private BlockStatement LowerBlock(BlockStatement block, List<Owned> outerLive,
        int loopBoundary)
    {
        // `live` grows as this block's own declarations are seen; nested scopes get a copy.
        List<Owned> live = Copy(live: outerLive);
        int blockStart = live.Count;
        var stmts = new List<Statement>(capacity: block.Statements.Count + 2);

        foreach (Statement s in block.Statements)
        {
            if (s is DeclarationStatement { Declaration: VariableDeclaration v })
            {
                stmts.Add(item: s);
                RegisterBlockDeclaration(v: v, live: live);
                continue;
            }

            stmts.Add(item: LowerStatement(stmt: s, live: live, loopBoundary: loopBoundary));
        }

        // Fall-through end-of-block: destroy this block's own locals in REVERSE declaration order
        // (LIFO) — a later-declared local may reference an earlier one, so destroying dependents
        // before dependencies is the safe RAII order (and matches this pass's documented contract).
        // Skip entirely when the block already unconditionally terminates (its last statement is a
        // return/throw/absent/break/continue, possibly wrapped by PrefixDestroys): the fall-through
        // is unreachable, and the terminator's own PrefixDestroys already tore these locals down.
        // Emitting them here anyway produced redundant post-terminator `x.destroy()` calls that
        // codegen silently dropped but that polluted the AST (and the dump).
        if (!(stmts.Count > 0 && AlwaysTerminates(s: stmts[^1])))
        {
            for (int i = live.Count - 1; i >= blockStart; i--)
            {
                stmts.Add(item: MakeDestroyStmt(owned: live[index: i], loc: block.Location));
            }
        }

        return block with { Statements = stmts };
    }

    // Records a block-local `var` declaration into `live` (so its scope-exit teardown is emitted),
    // after handling the `var buf = _lit_N` collection-literal ownership transfer.
    private void RegisterBlockDeclaration(VariableDeclaration v, List<Owned> live)
    {
        // A `var buf = _lit_N` binding takes over ownership of a hoisted collection-literal
        // temporary (ExpressionLoweringPass lowers `var buf = []` to
        // `var _lit_N = List(); _lit_N.add(...); var buf = _lit_N`). `buf` and `_lit_N` alias
        // the SAME object, so only ONE may be torn down. When this pass runs AFTER that lowering
        // (stdlib + synthesized variant bodies — user programs are lowered later), both would be
        // live and BOTH destroyed → double-free. Treat the move as consuming the temp: drop it
        // from `live` and record it moved so no scope-exit/return teardown frees it.
        if (v.Initializer is IdentifierExpression { Name: var srcName } &&
            srcName.StartsWith(value: "_lit_", comparisonType: StringComparison.Ordinal))
        {
            _movedNames.Add(item: srcName);
            int srcIdx = live.FindLastIndex(match: o => o.Name == srcName);
            if (srcIdx >= 0)
            {
                live.RemoveAt(index: srcIdx);
            }
        }

        TypeSymbol? t = v.Type?.ResolvedType ?? v.Initializer?.ResolvedType;
        if (t != null && !_movedNames.Contains(item: v.Name) && !IsUsingBinding(v: v) &&
            !IsViewBinding(v: v) && TryResolveDestroy(type: t, destroy: out RoutineInfo? d) &&
            d != null)
        {
            live.Add(item: new Owned(Name: v.Name, Type: t, Destroy: d));
        }
    }

    /// <summary>
    /// Wraps <paramref name="exit"/> in a block that destroys live owned bindings at indices
    /// <c>[from, live.Count)</c>, skipping <paramref name="skip"/> (a returned bare local, which is
    /// moved out).
    ///
    /// <para>When the exit returns a non-trivial EXPRESSION (e.g. <c>return me.concat(other)</c>),
    /// that expression may still read owned locals/params. Destroying them first would free memory
    /// the expression then reads (use-after-free). So the return value is evaluated into a temp
    /// BEFORE the destroys, and the temp — which now owns the result — is returned and excluded from
    /// teardown:
    /// <code>var __td_ret = EXPR ; &lt;destroys&gt; ; return __td_ret</code></para>
    /// </summary>
    private Statement PrefixDestroys(Statement exit, List<Owned> live, int from,
        string? skip)
    {
        var stmts = new List<Statement>();
        Statement finalExit = TrySpillReturnValue(exit: exit,
            live: live,
            from: from,
            skip: ref skip,
            stmts: stmts);

        // Destroy in REVERSE declaration order (LIFO) — the safe RAII order, matching this pass's
        // documented contract and the fall-through end-of-block teardown above.
        for (int i = live.Count - 1; i >= from; i--)
        {
            if (skip != null && live[index: i].Name == skip)
            {
                continue;
            }

            stmts.Add(item: MakeDestroyStmt(owned: live[index: i], loc: exit.Location));
        }

        if (stmts.Count == 0)
        {
            return exit;
        }

        stmts.Add(item: finalExit);
        return new BlockStatement(Statements: stmts, Location: exit.Location);
    }

    // Spills a non-trivial returned expression into a `__td_ret_N` temp so it's computed while its
    // locals are still live (destroying them first would free memory the expression then reads). The
    // spill decl is appended to <paramref name="stmts"/>, <paramref name="skip"/> is updated to the
    // temp (moved out — never torn down), and the rewritten exit (returning the temp) is returned.
    // When no spill is needed the original exit is returned and nothing is appended.
    private Statement TrySpillReturnValue(Statement exit, List<Owned> live, int from,
        ref string? skip, List<Statement> stmts)
    {
        // A bare-identifier return is a move — `skip` already excludes it — and needs no spill.
        Expression? retVal = exit switch
        {
            ReturnStatement r => r.Value,
            VariantReturnStatement vr => vr.Value,
            _ => null
        };
        if (retVal is null or IdentifierExpression ||
            !WillDestroyAny(live: live, from: from, skip: skip))
        {
            return exit;
        }

        string tmp = $"__td_ret_{_spillCounter++}";
        // Leave the slot type to be inferred from EXPR — codegen emits the spilled value with its
        // own resolved type, so the slot must match THAT, not the declared routine return type
        // (they can disagree, e.g. a synthesized diagnose whose AST return type lags the body, or
        // a return that codegen wraps). The failable-passthrough case (node says S64, emits
        // Maybe[S64]) is handled at the source: ErrorHandlingVariantPass stamps the passthrough
        // call's ResolvedType with the variant carrier, so EXPR inference already sees Maybe[S64].
        var decl = new VariableDeclaration(Name: tmp,
            Type: null,
            Initializer: retVal,
            Visibility: VisibilityModifier.Secret,
            Location: exit.Location);
        stmts.Add(item: new DeclarationStatement(Declaration: decl, Location: exit.Location));
        var tmpRef = new IdentifierExpression(Name: tmp, Location: exit.Location)
        {
            ResolvedType = retVal.ResolvedType
        };
        skip = tmp; // the spilled value is moved out — never tear it down
        return exit switch
        {
            ReturnStatement r => r with { Value = tmpRef },
            VariantReturnStatement vr => vr with { Value = tmpRef },
            _ => exit
        };
    }

    /// <summary>
    /// Finds the innermost live binding named <paramref name="name"/> and returns it only if it
    /// is an owned bare-entity local (lateinit or plain) — the bindings whose reassignment must
    /// tear down the old content. A shadowing non-entity binding correctly yields null.
    /// </summary>
    private static Owned? FindOwnedEntity(List<Owned> live, string name)
    {
        for (int i = live.Count - 1; i >= 0; i--)
        {
            if (live[index: i].Name != name)
            {
                continue;
            }

            return live[index: i].Type is EntityTypeSymbol
                ? live[index: i]
                : null;
        }

        return null;
    }

    /// <summary>
    /// Rewrites <c>x = EXPR</c> on an owned entity local to
    /// <c>var __li_N = EXPR ; x.destroy() ; x = __li_N</c> so the current content (placeholder
    /// or prior value) is freed exactly once and the RHS still sees the old value.
    /// <paramref name="rebuild"/> reconstructs the assignment statement (whichever AST form it
    /// has) around the spilled RHS.
    /// </summary>
    private BlockStatement LowerEntityReassign(Statement original, Expression rhs,
        Func<Expression, Statement> rebuild, Owned owned)
    {
        var stmts = new List<Statement>();
        Expression finalRhs = rhs;

        // A bare-identifier RHS is a move of an existing binding — no spill needed.
        if (rhs is not IdentifierExpression)
        {
            string tmp = $"__li_{_spillCounter++}";
            var decl = new VariableDeclaration(Name: tmp,
                Type: null,
                Initializer: rhs,
                Visibility: VisibilityModifier.Secret,
                Location: original.Location);
            stmts.Add(item: new DeclarationStatement(Declaration: decl,
                Location: original.Location));
            finalRhs = new IdentifierExpression(Name: tmp, Location: original.Location)
            {
                ResolvedType = rhs.ResolvedType
            };
        }

        stmts.Add(item: MakeDestroyStmt(owned: owned, loc: original.Location));
        stmts.Add(item: rebuild(arg: finalRhs));
        return new BlockStatement(Statements: stmts, Location: original.Location);
    }

    /// <summary>
    /// Rewrites <c>me.f = EXPR</c> on an owned <c>Roamed[T]</c> field to
    /// <c>var __li_N = EXPR ; me.f.destroy() ; me.f = __li_N</c> so the overwritten (old) handle drops
    /// its co-owned count exactly once. The RHS is spilled BEFORE the destroy so it may still read the
    /// old field value and so a self-assignment (<c>me.f = me.f.share()</c>) bumps before it drops and
    /// never transiently reaches zero. Only fires for a Roamed field (the exact scope of the deleted
    /// <c>RcRetainLoweringPass.ReassignRelease</c>); any other member write passes through unchanged.
    /// </summary>
    private Statement LowerRoamedFieldReassign(Statement original, MemberExpression target,
        Expression rhs, Func<Expression, Statement> rebuild)
    {
        TypeSymbol? fieldType = target.ResolvedType;
        if (fieldType is null ||
            TypeRegistry.GetRcWrapperBaseName(type: fieldType) != RuntimeContract.Roamed ||
            !TryResolveDestroy(type: fieldType, destroy: out RoutineInfo? destroy) ||
            destroy is null)
        {
            return original;
        }

        var stmts = new List<Statement>();
        Expression finalRhs = rhs;

        // A bare-identifier RHS is a move of an existing binding — no spill needed.
        if (rhs is not IdentifierExpression)
        {
            string tmp = $"__li_{_spillCounter++}";
            var decl = new VariableDeclaration(Name: tmp,
                Type: null,
                Initializer: rhs,
                Visibility: VisibilityModifier.Secret,
                Location: original.Location);
            stmts.Add(item: new DeclarationStatement(Declaration: decl,
                Location: original.Location));
            finalRhs = new IdentifierExpression(Name: tmp, Location: original.Location)
            {
                ResolvedType = rhs.ResolvedType
            };
        }

        stmts.Add(item: MakeMemberDestroyStmt(receiver: target,
            destroy: destroy,
            loc: original.Location));
        stmts.Add(item: rebuild(arg: finalRhs));
        return new BlockStatement(Statements: stmts, Location: original.Location);
    }

    private static bool WillDestroyAny(List<Owned> live, int from, string? skip)
    {
        for (int i = from; i < live.Count; i++)
        {
            if (skip == null || live[index: i].Name != skip)
            {
                return true;
            }
        }

        return false;
    }

    private ExpressionStatement MakeDestroyStmt(Owned owned, SourceLocation loc)
    {
        var ident =
            new IdentifierExpression(Name: owned.Name, Location: loc)
            {
                ResolvedType = owned.Type
            };
        var callee = new MemberExpression(Object: ident, MemberName: "destroy", Location: loc)
        {
            ResolvedType = _blankType
        };
        var call = new CallExpression(Callee: callee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = owned.Destroy,
            ResolvedType = _blankType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: owned.Destroy),
            // Compiler-injected teardown: exempt from the RF-S609 dangerous-call gate. `destroy` is a
            // dangerous routine, but this call is the compiler's own scope-exit teardown, not a user
            // calling it outside a `danger` block. Under the demand pipeline this destroy is injected
            // (Phase 7) BEFORE the stdlib body is SA'd on reach, so the dangerous-call check would see it
            // and false-fire — the eager pipeline never did because stdlib SA ran before teardown.
            IsSynthesizedLowering = true
        };
        return new ExpressionStatement(Expression: call, Location: loc);
    }

    /// <summary>Builds <c>receiver.destroy()</c> for an arbitrary lvalue receiver (a member access
    /// <c>me.f</c>) — the field-reassignment counterpart of <see cref="MakeDestroyStmt"/>.</summary>
    private ExpressionStatement MakeMemberDestroyStmt(Expression receiver, RoutineInfo destroy,
        SourceLocation loc)
    {
        var callee = new MemberExpression(Object: receiver, MemberName: "destroy", Location: loc)
        {
            ResolvedType = _blankType
        };
        var call = new CallExpression(Callee: callee, Arguments: [], Location: loc)
        {
            ResolvedRoutine = destroy,
            ResolvedType = _blankType,
            LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: destroy),
            // Compiler-injected teardown — exempt from the RF-S609 dangerous-call gate (see MakeDestroyStmt).
            IsSynthesizedLowering = true
        };
        return new ExpressionStatement(Expression: call, Location: loc);
    }

    /// <summary>
    /// Resolves the <c>destroy</c> to call at scope exit via the unified
    /// <see cref="TypeRegistry.GetLifecycle"/> — the SAME decision the copy pass (<c>NeedsRetainingCopy</c>)
    /// drives off, so a value is either both retaining-copied and balanced-destroyed or neither (the
    /// asymmetry that double-freed before). <c>GetLifecycle</c> resolves through
    /// <c>GetOwnMemberRoutinesResolved</c> (own-memberRoutines-only, generic-resolution aware), so it finds e.g.
    /// <c>Retained[Tracer].destroy</c> without surfacing the no-owner universal <c>T.destroy</c> stub,
    /// and excludes the abstract tier.
    /// </summary>
    private bool TryResolveDestroy(TypeSymbol type, out RoutineInfo? destroy)
    {
        TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: type);
        destroy = lc.Destroy;
        if (lc.IsBorrow || destroy == null)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Every type has a `destroy`, so by default it's called at scope exit (the per-type
    /// `destroy` is a cheap no-op when there's nothing to free). The ONLY exclusions are the
    /// access/borrow tier — `Viewing`/`Modifying`/`Consulting`/`Amending` views, the `Accessing`/
    /// `Controlling` access protocols, and the unmanaged `Hijacked` pointer — whose referent is
    /// owned elsewhere, so destroying them here would free a caller's value. Abstract types
    /// (generic params, protocols) likewise have no concrete destructor to call.
    /// </summary>
    internal static bool NeedsTeardown(TypeSymbol type)
    {
        return type is not (GenericParameterTypeSymbol or ProtocolTypeSymbol);
    }

    /// <summary>
    /// True when control cannot fall off the end of <paramref name="s"/> — every path terminates via
    /// return/throw/absent/variant-return/break/continue. Used to suppress the unreachable fall-through
    /// teardown of a block whose last statement is itself a terminator (bare, or wrapped by
    /// <see cref="PrefixDestroys"/> into a trailing-return block, or an if/else where both arms exit).
    /// Conservative: anything not provably terminating returns false (fall-through teardown kept).
    /// </summary>
    private static bool AlwaysTerminates(Statement s)
    {
        return s switch
        {
            ReturnStatement or AbsentStatement or ThrowStatement or VariantReturnStatement
                or BreakStatement or ContinueStatement => true,
            BlockStatement b => b.Statements.Count > 0 && AlwaysTerminates(s: b.Statements[^1]),
            IfStatement { ElseStatement: { } elseS } ifs =>
                AlwaysTerminates(s: ifs.ThenStatement) && AlwaysTerminates(s: elseS),
            // `danger` is a scope-transparent wrapper (an unsafe marker, no control flow of its own): it
            // terminates exactly when its body does. A `danger { … return }` at a block's end therefore
            // makes the fall-through unreachable, so the outer scope must not append dead teardown after it.
            DangerStatement d => AlwaysTerminates(s: d.Body),
            _ => false
        };
    }

    private static string? ReturnedName(Statement stmt)
    {
        return stmt switch
        {
            ReturnStatement { Value: IdentifierExpression id } => id.Name,
            VariantReturnStatement { Value: IdentifierExpression id } => id.Name,
            _ => null
        };
    }

    private static List<Owned> Copy(List<Owned> live)
    {
        return [.. live];
    }

    /// <summary>
    /// True for the synthetic bindings UsingLoweringPass emits (`var __uf_N = resource` and the
    /// user's `var x = __uf_N.enter()` / `var x = __uf_N`). Their lifetime is governed by the
    /// injected `enter`/`exit`, so they must NOT also be torn down here (that would double-free
    /// the single underlying entity).
    /// </summary>
    private static bool IsUsingBinding(VariableDeclaration v)
    {
        if (v.Name.StartsWith(value: "__uf_"))
        {
            return true;
        }

        return v.Initializer switch
        {
            IdentifierExpression id => id.Name.StartsWith(value: "__uf_"),
            CallExpression { Callee: MemberExpression { Object: IdentifierExpression o } } =>
                o.Name.StartsWith(value: "__uf_"),
            _ => false
        };
    }

    /// <summary>
    /// The reference primitives that yield an in-flight <c>T</c> view of a referent owned elsewhere:
    /// <c>Hijacked[T].as_entity()</c> and the <c>refer</c>/<c>control</c> marker-protocol coercions.
    /// A binding initialized by one of these owns nothing and must NOT be torn down.
    /// </summary>
    private static readonly IReadOnlySet<string> ViewVerbs = RuntimeContract.ViewVerbs;

    /// <summary>
    /// True for a binding that holds a borrowed <c>T</c> view — <c>var ctrl = ptr.as_entity()</c>,
    /// <c>var x = h.refer()</c>, <c>var x = h.control()</c>. These pervade the RC wrapper bodies
    /// (e.g. <c>var ctrl = Hijacked[RetainController[T]](me).as_entity()</c> in <c>Retained.release</c>).
    /// The binding's static type is the bare referent (<c>T</c>), so <c>GetLifecycle</c> would resolve
    /// the referent's real <c>destroy</c> and free a value owned elsewhere — hence we key on the
    /// initializer VERB (a reference primitive), per the four-routine governance model, not the type.
    /// </summary>
    private static bool IsViewBinding(VariableDeclaration v)
    {
        return (v.Initializer is CallExpression { Callee: MemberExpression m } &&
                ViewVerbs.Contains(item: m.MemberName))
               // A variant when-pattern payload binding (`when me is Arm as v: …`, lowered by
               // PatternLoweringPass to `var v = <CarrierPayloadExpression on me>`) is a BORROW/view into the
               // matched variant's payload — the variant still owns it. Tearing `v` down frees the variant's
               // payload out from under it: a read-only `represent` would then corrupt `me`, and the
               // auto-synthesized variant `destroy` (explicit `v.destroy()`) would double-free. So exclude it.
               || v.Initializer is CarrierPayloadExpression;
    }

    // -----------------------------------------------------------------------------
    // Move pre-scan: a binding whose ownership leaves the routine is never torn down here.
    // A binding is "moved" when it is: stolen; consumed by `.retain()`/`.track()` (bare entity)
    // written into storage by a store primitive (`poke`/`store_element_ref`/`store`); or assigned
    // into another binding/field. These are all unambiguous ownership transfers — unlike general
    // argument passing (which is usually a borrow), so we do NOT treat plain call args as moves.
    // (`load_element_ref` is a READ, not a store, so it is excluded.)
    // -----------------------------------------------------------------------------

    private static readonly IReadOnlySet<string> StorePrimitives = RuntimeContract.StorePrimitives;

    private void CollectMovedNames(Statement stmt)
    {
        AstWalker.Walk(root: stmt, visit: CollectMovedNamesFromNode);
    }

    private void CollectMovedNamesFromNode(object node)
    {
        switch (node)
        {
            case StealExpression { Operand: IdentifierExpression id }:
                _movedNames.Add(item: id.Name);
                break;
            // `T.retain()` / `T.track()` on a bare ENTITY consumes it (ownership moves into the
            // Constructing an RC wrapper FROM a bare entity moves the entity into the controller —
            // STRUCTURAL (name-agnostic): a call whose receiver is a bare entity and whose result is
            // an RC wrapper. `Retained.assign()` / `.observe()` mint a handle from an existing WRAPPER
            // receiver (not a bare entity), so they are correctly excluded and still released.
            case CallExpression
                {
                    Callee: MemberExpression
                    {
                        Object: IdentifierExpression { ResolvedType: EntityTypeSymbol } recv
                    }
                } rcCtorCall when rcCtorCall.ResolvedType is { } rcRes &&
                                  TypeRegistry.GetRcWrapperBaseName(type: rcRes) is not null:
                _movedNames.Add(item: recv.Name);
                break;
            // Store primitives write their argument(s) into memory/storage — the source binding
            // is moved into the container, not dropped at scope exit.
            case CallExpression call when CalleeName(callee: call.Callee) is { } n &&
                                          StorePrimitives.Contains(item: n):
                HandleStorePrimitiveMove(call: call);
                break;
            // `target = source` / `me.field = source` moves `source` into the target.
            case AssignmentStatement assign
                when Unwrap(e: assign.Value) is IdentifierExpression rhs:
                _movedNames.Add(item: rhs.Name);
                break;
            // The move in operator-expression form `target = __rv` (an ExpressionStatement wrapping a
            // `BinaryExpression{Assign}`) — the tail TemporaryTeardownPass emits for a lowered managed-
            // leaf reassignment. GATED on the STRUCTURED IsSynthesizedTeardownTemp marker (not the
            // `__rv_` name, and not every operator assign — a plain user `x = y` on a Copyable leaf
            // receives an injected `.assign()` from RecordCopyLoweringPass and its source stays owned,
            // so it must NOT be marked moved). When this pass RE-runs over an already-temp-teardown-
            // lowered body (the warm-restore path), this makes the moved-out `__rv` temp escape scope-
            // exit teardown — otherwise it frees the buffer the target now owns and returns a dangling
            // value (the record-`represent` double-free).
            case BinaryExpression
            {
                Operator: BinaryOperator.Assign,
                Right: IdentifierExpression { IsSynthesizedTeardownTemp: true } rhs2
            }:
                _movedNames.Add(item: rhs2.Name);
                break;
            // An explicit `v.destroy()` consumes `v` — it must NOT then be torn down again at
            // scope exit. This is the auto-synthesized variant `destroy` shape (`when me is Arm
            // as v: v.destroy()`): without this the pattern-bound heap payload is destroyed by the
            // explicit call AND by binding teardown → double free (scalar arms hid it: no-op destroy).
            case CallExpression
            {
                Callee: MemberExpression
                {
                    MemberName: "destroy", Object: IdentifierExpression dv
                }
            }:
                _movedNames.Add(item: dv.Name);
                break;
            // Constructing a VARIANT boxes (takes ownership of) its single payload — the source
            // binding is moved into the variant, not dropped at scope exit. Without this, a heap
            // payload boxed into a returned variant (e.g. a synthesized `serialize()` returning
            // `SerialValue.Dict(<hoisted dict temp>)`) is BOTH boxed and torn down → double free.
            // (Harmless for scalar arms: scalars have no `destroy`. Entity/record field moves are
            // handled via `steal`; variant/carrier boxing has no steal, so mark it here.)
            case CreatorExpression creator
                when (creator.ConstructedType ?? creator.ResolvedType) is VariantTypeSymbol:
                HandleVariantBoxingMove(creator: creator);
                break;
            // Constructing an ENTITY (incl. Crashable) MOVES each identifier argument bound to a
            // NON-Copyable field into that field: the entity is single-owner and a non-Copyable value
            // (an entity, or a collection like List) transfers ownership on construction. A Copyable
            // field (Text, scalar) instead receives a retaining copy (RecordCopyLoweringPass injects
            // it, in a LATER pass), so the source binding stays owned and must still be torn down —
            // hence the field-type Copyable gate (NOT the argument's own ResolvedType, which a hoisted
            // temp identifier may not carry). This matters for synthesized bodies whose list literals
            // ExpressionLoweringPass hoists to `var _lit = List[…](); …` temps BEFORE this pass runs
            // (the reverse of user code, torn down while the literal is still inline): without marking
            // the moved temp, scope-exit teardown would `_lit.destroy()` it on top of the entity's own
            // field teardown → double free (e.g. BuilderQuery `protocol_info`/`routine_info` nested
            // `List[Text]` fields).
            case CreatorExpression entityCreator
                when (entityCreator.ConstructedType ?? entityCreator.ResolvedType) is
                EntityTypeSymbol ent:
                HandleEntityConstructionMove(entityCreator: entityCreator, ent: ent);
                break;
        }
    }

    private void HandleStorePrimitiveMove(CallExpression call)
    {
        foreach (Expression arg in call.Arguments)
        {
            if (Unwrap(e: arg) is IdentifierExpression a)
            {
                _movedNames.Add(item: a.Name);
            }
        }
    }

    private void HandleVariantBoxingMove(CreatorExpression creator)
    {
        foreach ((_, Expression val) in creator.MemberVariables)
        {
            if (Unwrap(e: val) is IdentifierExpression a)
            {
                _movedNames.Add(item: a.Name);
            }
        }
    }

    private void HandleEntityConstructionMove(CreatorExpression entityCreator, EntityTypeSymbol ent)
    {
        foreach ((string memberName, Expression val) in entityCreator.MemberVariables)
        {
            if (Unwrap(e: val) is IdentifierExpression a)
            {
                TypeSymbol? fieldType = ent.MemberVariables
                                         .FirstOrDefault(predicate: m => m.Name == memberName)
                                        ?.Type;
                if (fieldType == null)
                {
                    continue;
                }

                // Mirror RecordCopyLoweringPass.NeedsRetainingCopy EXACTLY (both keyed on the
                // unified GetLifecycle): a field whose type needs a retaining copy (hand-written
                // `store`, e.g. Text) receives an INJECTED copy — the source stays owned and must
                // still be torn down, so it is NOT moved. A field without one (a collection like
                // List, or a bare entity) takes the source by MOVE, so the source binding must
                // not be destroyed. Agreeing with the copy pass is what keeps ownership balanced.
                TypeRegistry.Lifecycle lc = ctx.Registry.GetLifecycle(type: fieldType);
                bool needsRetainingCopy = !lc.IsBorrow && lc.Store != null;
                if (!needsRetainingCopy)
                {
                    _movedNames.Add(item: a.Name);
                }
            }
        }
    }

    private static Expression Unwrap(Expression e)
    {
        return e is NamedArgumentExpression n
            ? n.Value
            : e;
    }

    private static string? CalleeName(Expression callee)
    {
        return callee switch
        {
            MemberExpression m => m.MemberName,
            IdentifierExpression id => id.Name,
            _ => null
        };
    }
}
