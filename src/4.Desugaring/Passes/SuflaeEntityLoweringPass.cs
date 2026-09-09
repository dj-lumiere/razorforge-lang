using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Suflae-only lowering: an SF <c>entity</c> is a <c>Roamed[E]</c> biased-refcounted handle, not a
/// bare single-owner entity. This pass runs at the START of Phase 7 (before
/// <c>RoutineReachabilityPass</c>) so that once entity-typed
/// bindings carry a <c>Roamed[E]</c> resolved type, reachability seeds the roam/promote/lock/cycle
/// machinery off the live wrapper type (the existing <c>Roamed</c>-base seeding), and the Phase-8 RC
/// lifecycle passes (retain-on-copy, release-on-scope-exit) apply — with NO codegen-site changes.
///
/// <para>STAGE 1 (this cut): construction + aliasing + scope-exit release. For every SF construction
/// <c>E(...)</c> (a <see cref="CreatorExpression"/> whose resolved type is a bare
/// <see cref="EntityTypeInfo"/>) the value is rewritten to <c>E(...).roam()</c> and retyped to
/// <c>Roamed[E]</c>; locals bound to such a value (or aliased from one) are tracked so their
/// identifier references retype to <c>Roamed[E]</c>. The entity's own field layout and memberRoutine
/// receivers (<c>me</c>) stay bare <c>E</c> — the controller is peeled by the wrapper forwarder.
/// Lock-wrapped access, promote-at-boundary, and cycle collection ride on later stages.</para>
/// </summary>
internal sealed class SuflaeEntityLoweringPass
{
    private readonly TypeRegistry _registry;

    // Per-routine scope: local name -> the Roamed[E] type it now carries. Reset per routine body.
    private readonly Dictionary<string, WrapperTypeInfo> _roamedLocals = new();

    // Per-routine scope: names that are BORROWED Roamed handles (`me` + Roamed parameters). Returning
    // one hands a fresh reference to the caller while the borrow itself is NOT released at scope exit
    // (ScopeTeardownLoweringPass skips `me` and SF Roamed params), so the return must RETAIN — otherwise
    // the caller's binding and the original owner both release the same controller (double free). An
    // owned local returned by move is NOT in this set, so it is correctly left alone.
    private readonly HashSet<string> _borrowNames = new();

    // True while lowering a `create` constructor body. A constructor returns the BARE entity by
    // convention (the caller roams it — see the class doc's carve-out + stdlib), so its return
    // construction must NOT be roamed here, or the value is roamed twice (once inside `create`, once at
    // the call site) → the outer controller's `data` points at the inner controller → wrong-value read
    // (scalar field) or AccessViolation (pointer field). Reset per routine.
    private bool _inCreateRoutine;

    public SuflaeEntityLoweringPass(TypeRegistry registry)
    {
        _registry = registry;
    }

    public void Run(Program program)
    {
        if (_registry.Language != Language.Suflae) return;

        for (int i = 0; i < program.Declarations.Count; i++)
        {
            switch (program.Declarations[i])
            {
                case RoutineDeclaration r:
                    program.Declarations[i] = LowerRoutine(r);
                    break;
                case EntityDeclaration e:
                    LowerMemberList(e.Members);
                    break;
                case RecordDeclaration rec:
                    LowerMemberList(rec.Members);
                    break;
                case CrashableDeclaration cr:
                    LowerMemberList(cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Post-resolve invariant (the "unification tail" guard): after <see cref="Compiler.Declaration.TypeResolver"/>
    /// centralized entity→<c>Roamed[E]</c> substitution at every signature slot
    /// (<see cref="Compiler.Declaration.TypeResolver.RoamSuflaeEntitySlot"/>), NO parameter or return in a
    /// SUFLAE USER routine may still be a BARE <see cref="EntityTypeInfo"/> — an entity is always the
    /// <c>Roamed[E]</c> handle. A bare entity here means a resolution site slipped the choke point (the
    /// class of bug the centralization was meant to eliminate), so fail LOUDLY at build time rather than
    /// mis-codegen a raw controller access at runtime.
    /// <para>Carve-outs: the stdlib is borrowed RazorForge source (bare single-owner entities — correct);
    /// a <c>create</c> constructor legitimately builds and returns the bare entity before any controller
    /// exists (its <c>me</c>/return stay bare per <see cref="SignatureResolver"/>), so its RETURN is
    /// exempt — but its PARAMETERS (passed-in entity values) must still be <c>Roamed</c>.</para>
    /// </summary>
    private void AssertNoBareEntityInSignature(RoutineDeclaration r)
    {
        string? file = r.Location.FileName;
        if (file == null
            || !file.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase)
            || IsStdlibPath(file: file))
        {
            return;
        }

        foreach (Parameter p in r.Parameters)
            if (p.Type?.ResolvedType is EntityTypeInfo bareParam)
                throw new InvalidOperationException(
                    $"SF representation-unification invariant violated: parameter '{p.Name}' of routine "
                    + $"'{r.Name}' ({file}:{r.Location.Line}) resolved to a BARE entity '{bareParam.Name}' "
                    + "instead of Roamed[E]. An entity slot slipped TypeResolver.RoamSuflaeEntitySlot.");

        if (r.ResolvedInfo is not { IsCreator: true }
            && r.ReturnType?.ResolvedType is EntityTypeInfo bareReturn)
            throw new InvalidOperationException(
                $"SF representation-unification invariant violated: return type of routine '{r.Name}' "
                + $"({file}:{r.Location.Line}) resolved to a BARE entity '{bareReturn.Name}' instead of "
                + "Roamed[E]. An entity slot slipped TypeResolver.RoamSuflaeEntitySlot.");
    }

    private bool IsStdlibPath(string file)
    {
        string? root = _registry.StdlibPath;
        return root != null
               && file.StartsWith(value: root, comparisonType: StringComparison.OrdinalIgnoreCase);
    }

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        for (int i = 0; i < members.Count; i++)
            if (members[i] is RoutineDeclaration mr)
                members[i] = LowerRoutine(mr);
    }

    private RoutineDeclaration LowerRoutine(RoutineDeclaration r)
    {
        AssertNoBareEntityInSignature(r);
        _roamedLocals.Clear();
        _borrowNames.Clear();
        _borrowNames.Add(item: "me");
        // A constructor is the `routine T(...) -> T` form (named after the type it builds). It returns
        // the bare entity by convention; the caller roams. (The routine is mangled to `T.create` at
        // codegen, but its declaration name is the type name here.)
        _inCreateRoutine = r.ReturnType is { Name: var rn } && r.Name == rn;
        foreach (Parameter p in r.Parameters.Where(
                     p => p.Type?.ResolvedType is WrapperTypeInfo { Name: RuntimeContract.Roamed }
                          or RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed }))
            _borrowNames.Add(item: p.Name);
        Statement newBody = LowerStatement(r.Body);
        return ReferenceEquals(newBody, r.Body) ? r : r with { Body = newBody };
    }

    // ---- Statements -----------------------------------------------------------------------------

    private Statement LowerStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                return LowerBlockStatement(block: block);
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd } ds:
                return LowerDeclarationStatement(stmt: stmt, ds: ds, vd: vd);
            case AssignmentStatement assign:
                return LowerAssignmentStatement(stmt: stmt, assign: assign);
            case ReturnStatement { Value: not null } ret:
                return LowerReturnStatement(stmt: stmt, ret: ret);
            case ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign } bin } es:
                return LowerBinaryAssignStatement(stmt: stmt, es: es, bin: bin);
            case ExpressionStatement es:
                return LowerExpressionStatement(stmt: stmt, es: es);
            case DiscardStatement dis:
                return LowerDiscardStatement(stmt: stmt, dis: dis);
            case IfStatement ifs:
                return LowerIfStatement(stmt: stmt, ifs: ifs);
            case WhileStatement w:
                return LowerWhileStatement(stmt: stmt, w: w);
            case LoopStatement loop:
                return LowerLoopStatement(stmt: stmt, loop: loop);
            case EachStatement f:
                return LowerEachStatement(stmt: stmt, f: f);
            case WhenStatement whenStmt:
                return LowerWhenStatement(stmt: stmt, whenStmt: whenStmt);
            case UsingStatement u:
                return LowerUsingStatement(stmt: stmt, u: u);
            case DangerStatement d:
                return LowerDangerStatement(stmt: stmt, d: d);
            default:
                return stmt;
        }
    }

    private Statement LowerExpressionStatement(Statement stmt, ExpressionStatement es)
    {
        Expression e = LowerExpression(es.Expression);
        return ReferenceEquals(e, es.Expression) ? stmt : es with { Expression = e };
    }

    private Statement LowerDiscardStatement(Statement stmt, DiscardStatement dis)
    {
        Expression e = LowerExpression(dis.Expression);
        return ReferenceEquals(e, dis.Expression) ? stmt : dis with { Expression = e };
    }

    private Statement LowerWhileStatement(Statement stmt, WhileStatement w)
    {
        Expression c = LowerExpression(w.Condition);
        Statement b = LowerStatement(w.Body);
        return !ReferenceEquals(c, w.Condition) || !ReferenceEquals(b, w.Body)
            ? w with { Condition = c, Body = b }
            : stmt;
    }

    private Statement LowerLoopStatement(Statement stmt, LoopStatement loop)
    {
        Statement b = LowerStatement(loop.Body);
        return ReferenceEquals(b, loop.Body) ? stmt : loop with { Body = b };
    }

    private Statement LowerEachStatement(Statement stmt, EachStatement f)
    {
        Statement b = LowerStatement(f.Body);
        return ReferenceEquals(b, f.Body) ? stmt : f with { Body = b };
    }

    private Statement LowerUsingStatement(Statement stmt, UsingStatement u)
    {
        Statement b = LowerStatement(u.Body);
        Statement? fb = u.FallbackBody != null ? LowerStatement(u.FallbackBody) : null;
        return !ReferenceEquals(b, u.Body) || !ReferenceEquals(fb, u.FallbackBody)
            ? u with { Body = b, FallbackBody = fb }
            : stmt;
    }

    private Statement LowerDangerStatement(Statement stmt, DangerStatement d)
    {
        Statement b = LowerStatement(d.Body);
        return ReferenceEquals(b, d.Body) ? stmt : d with { Body = (BlockStatement)b };
    }

    private BlockStatement LowerBlockStatement(BlockStatement block)
    {
        bool changed = false;
        var list = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement s in block.Statements)
        {
            Statement ns = LowerStatement(s);
            list.Add(ns);
            if (!ReferenceEquals(ns, s)) changed = true;
        }
        return changed ? block with { Statements = list } : block;
    }

    private Statement LowerDeclarationStatement(Statement stmt, DeclarationStatement ds,
        VariableDeclaration vd)
    {
        Expression init = MaybeRoamCopy(LowerExpression(vd.Initializer!));
        // Track the local as Roamed[E] when its initializer resolved to a Roamed wrapper, so
        // later references (aliasing / access) retype consistently. `var` locals infer their
        // type from the initializer at codegen, so no declared-type rewrite is needed here.
        if (init.ResolvedType is WrapperTypeInfo { Name: RuntimeContract.Roamed } w)
            _roamedLocals[vd.Name] = w;
        return ReferenceEquals(init, vd.Initializer)
            ? stmt
            : ds with { Declaration = vd with { Initializer = init } };
    }

    private Statement LowerAssignmentStatement(Statement stmt, AssignmentStatement assign)
    {
        Expression v = LowerExpression(assign.Value);
        // SF implicit share: a borrowed Roamed RHS bound into a LOCAL slot retains so the slot owns
        // its own count. FIELD-target writes (`o.inner = x`) are NOT handled here — SF entity field
        // REASSIGNMENT has a separate pre-existing codegen crash (baseline AVs with or without the
        // former RoamBump), so the share placement for that path is deferred until that bug is fixed.
        // The former RcRetainLoweringPass.RoamBump auto-share for field targets is removed (it was the
        // compiler hand-simulating the refcount; RF now spells the co-owner explicitly `x.share()`).
        if (assign.Target is IdentifierExpression)
            v = MaybeRoamCopy(v);
        return ReferenceEquals(v, assign.Value) ? stmt : assign with { Value = v };
    }

    // A statement-level assignment `target = value` parses as an ExpressionStatement wrapping a
    // BinaryExpression{Assign} (NOT an AssignmentStatement — the parser only emits BinaryExpression
    // for `=`). SF implicit share: a borrowed Roamed RHS bound into a persistent slot (local var OR a
    // Roamed entity FIELD) must retain so the slot owns its own count. The former RcRetainLoweringPass
    // .RoamBump only fired for RF BARE-entity field writes (its `IsRoamedEntityField` requires the
    // object to be a bare EntityTypeInfo) — it NEVER fired for SF's `roamed_obj.field = x` (object is
    // Roamed), so SF field reassignment had NO retain-new: the field aliased the RHS handle without a
    // count → double-free at teardown. Inserting the share HERE (the SF lowering, the home of implicit
    // sharing) fixes it. MaybeRoamCopy self-gates on a Roamed borrow value; a fresh rvalue / non-Roamed
    // is untouched. The release-old on field overwrite stays in codegen (reassignment ≠ scope exit).
    private Statement LowerBinaryAssignStatement(Statement stmt, ExpressionStatement es,
        BinaryExpression bin)
    {
        Expression left = LowerExpression(bin.Left);
        Expression right = LowerExpression(bin.Right);
        if (bin.Left is IdentifierExpression or MemberExpression)
            right = MaybeRoamCopy(right);
        return ReferenceEquals(left, bin.Left) && ReferenceEquals(right, bin.Right)
            ? stmt
            : es with { Expression = bin with { Left = left, Right = right } };
    }

    private Statement LowerIfStatement(Statement stmt, IfStatement ifs)
    {
        Expression c = LowerExpression(ifs.Condition);
        Statement t = LowerStatement(ifs.ThenStatement);
        Statement? el = ifs.ElseStatement != null ? LowerStatement(ifs.ElseStatement) : null;
        return !ReferenceEquals(c, ifs.Condition) || !ReferenceEquals(t, ifs.ThenStatement)
               || !ReferenceEquals(el, ifs.ElseStatement)
            ? ifs with { Condition = c, ThenStatement = t, ElseStatement = el }
            : stmt;
    }

    private Statement LowerWhenStatement(Statement stmt, WhenStatement whenStmt)
    {
        bool changed = false;
        Expression subj = LowerExpression(whenStmt.Expression);
        if (!ReferenceEquals(subj, whenStmt.Expression)) changed = true;
        var clauses = new List<WhenClause>(capacity: whenStmt.Clauses.Count);
        foreach (WhenClause cl in whenStmt.Clauses)
        {
            Statement nb = LowerStatement(cl.Body);
            clauses.Add(ReferenceEquals(nb, cl.Body) ? cl : cl with { Body = nb });
            if (!ReferenceEquals(nb, cl.Body)) changed = true;
        }
        return changed ? whenStmt with { Expression = subj, Clauses = clauses } : stmt;
    }

    private Statement LowerReturnStatement(Statement stmt, ReturnStatement ret)
    {
        Expression v = LowerExpression(ret.Value!);
        // A `create` constructor returns the BARE entity — the caller roams it (documented
        // convention; stdlib's generic constructors already return bare). If lowering roamed the
        // return construction, peel that top `Roamed(from: X)` back off so the value isn't roamed
        // AGAIN at the call site (the double-roam bug: outer controller.data → inner controller).
        if (_inCreateRoutine && TryUnwrapRoamConstruction(expr: v, inner: out Expression? bareInner))
            return ReferenceEquals(bareInner, ret.Value) ? stmt : ret with { Value = bareInner };
        // Returning a BORROW (`me` / a Roamed param) or a Roamed FIELD read hands a fresh
        // reference to the caller; retain so the caller owns its own count. The borrow itself is
        // not released at scope exit (teardown skips `me` + SF Roamed params), so without the
        // retain the caller's binding and the original owner both release one shared count →
        // double free. An owned local returned by MOVE is not a borrow and is left as-is.
        if ((v is IdentifierExpression rid && _borrowNames.Contains(item: rid.Name))
            || v is MemberExpression)
            v = MaybeRoamCopy(v);
        return ReferenceEquals(v, ret.Value) ? stmt : ret with { Value = v };
    }

    // ---- Expressions ----------------------------------------------------------------------------

    private Expression LowerExpression(Expression expr)
    {
        switch (expr)
        {
            // A creator that yields a bare SF entity -> `.roam()` : Roamed[E].
            case CreatorExpression creator when creator.ResolvedType is EntityTypeInfo ce:
                return WrapInRoam(inner: creator, entity: ce);

            // A collection literal (`[1,2,3]` / `{…}`) is an entity rvalue just like a constructor call —
            // it resolves to a bare `Core.List`/`Set`/`Dict` entity, so an SF entity slot must `.roam()` it
            // (else a bare-list pointer is bound to a `Roamed` handle and reinterpreted as a controller →
            // AccessViolation on first access). ExpressionLoweringPass later expands the literal to a
            // `create + add_last` temp; the `.roam()` wraps that temp reference.
            case ListLiteralExpression when expr.ResolvedType is EntityTypeInfo le:
                return WrapInRoam(inner: expr, entity: le);
            case SetLiteralExpression when expr.ResolvedType is EntityTypeInfo se:
                return WrapInRoam(inner: expr, entity: se);
            case DictLiteralExpression when expr.ResolvedType is EntityTypeInfo de:
                return WrapInRoam(inner: expr, entity: de);

            // Reference to a local we've retyped to Roamed[E] -> flip its resolved type so aliasing
            // and access see the wrapper.
            case IdentifierExpression id
                when _roamedLocals.TryGetValue(id.Name, out WrapperTypeInfo? w)
                     && id.ResolvedType is EntityTypeInfo:
                return RetypeIdentifier(id: id, w: w);

            case MemberExpression m:
                return LowerMemberExpression(m: m);

            // `d[i]` on a Roamed container: recurse into the receiver so its identifier retypes to
            // Roamed[E] (else the getitem receiver stays bare-typed and OperatorLoweringPass lowers it
            // to `Dict.getitem` with the raw RoamController handle — RoamedProjectionLoweringPass then
            // can't see it's Roamed and skips the `raw_inner()` projection, crashing at runtime).
            case IndexExpression ix:
                return LowerIndexExpression(ix: ix);

            // `x is None` / `x isnot None` on a nullable entity reference (`E?` = Roamed[E]): rewrite
            // to `x.is_none()` (negated -> `not x.is_none()`). Done HERE (before reachability) so the
            // Roamed[E].is_none() instance gets seeded/instantiated for the concrete entity; codegen has
            // no direct IsPattern lowering for a Roamed handle. The frontend already narrowed the flow.
            case IsPatternExpression ipe
                when (ipe.Pattern is NonePattern or TypePattern { Type.Name: "None" }):
                return LowerNoneIsPattern(ipe: ipe);

            // A call — INCLUDING a constructor call `E(...)`, which is a CallExpression (not a
            // CreatorExpression) at this phase — that produces a bare SF entity: recurse into its
            // parts, then `.roam()` the whole value.
            case CallExpression call:
                return LowerCallExpression(call: call);

            // A generic-instance construction like `List[Node]()` stays a GenericMemberRoutineCallExpression
            // through codegen (the explicit `[T]` args keep it out of CallExpression form), so it must
            // be promoted here too — else a bare SF container never gets a RoamController and cycle
            // collection reads its raw buffer as a controller and crashes. Mirror the CallExpression
            // construction path: recurse args, retain Roamed-field args, then `.roam()` (promote).
            case GenericMemberRoutineCallExpression gmce:
                return LowerGenericMemberRoutineCall(gmce: gmce);

            // f-string: recurse into each embedded `{ expr }` so entity references inside it retype
            // (else e.g. `f"{b.size}"` reads `b` as a bare entity — actually the RoamController — and
            // returns the refcount instead of the field).
            case InsertedTextExpression fstr:
                return LowerInsertedText(fstr: fstr);

            case BinaryExpression bin:
                return LowerBinaryExpression(bin: bin);

            case UnaryExpression un:
                return LowerUnaryExpression(un: un);

            case NamedArgumentExpression namedArg:
                return LowerNamedArgumentExpression(namedArg: namedArg);

            default:
                return expr;
        }
    }

    private static IdentifierExpression RetypeIdentifier(IdentifierExpression id, WrapperTypeInfo w)
    {
        id.ResolvedType = w;
        return id;
    }

    private MemberExpression LowerMemberExpression(MemberExpression m)
    {
        Expression obj = LowerExpression(m.Object);
        return ReferenceEquals(obj, m.Object) ? m : m with { Object = obj };
    }

    private IndexExpression LowerIndexExpression(IndexExpression ix)
    {
        Expression o = LowerExpression(ix.Object);
        Expression ii = LowerExpression(ix.Index);
        return !ReferenceEquals(o, ix.Object) || !ReferenceEquals(ii, ix.Index)
            ? ix with { Object = o, Index = ii }
            : ix;
    }

    private BinaryExpression LowerBinaryExpression(BinaryExpression bin)
    {
        Expression l = LowerExpression(bin.Left);
        Expression r = LowerExpression(bin.Right);
        return !ReferenceEquals(l, bin.Left) || !ReferenceEquals(r, bin.Right)
            ? bin with { Left = l, Right = r }
            : bin;
    }

    private UnaryExpression LowerUnaryExpression(UnaryExpression un)
    {
        Expression o = LowerExpression(un.Operand);
        return ReferenceEquals(o, un.Operand) ? un : un with { Operand = o };
    }

    private NamedArgumentExpression LowerNamedArgumentExpression(NamedArgumentExpression namedArg)
    {
        Expression v = LowerExpression(namedArg.Value);
        return ReferenceEquals(v, namedArg.Value) ? namedArg : namedArg with { Value = v };
    }

    // f-string: recurse into each embedded `{ expr }` so entity references inside it retype
    // (else e.g. `f"{b.size}"` reads `b` as a bare entity — actually the RoamController — and
    // returns the refcount instead of the field).
    private InsertedTextExpression LowerInsertedText(InsertedTextExpression fstr)
    {
        bool changed = false;
        var parts = new List<InsertedTextPart>(capacity: fstr.Parts.Count);
        foreach (InsertedTextPart part in fstr.Parts)
        {
            if (part is ExpressionPart ep)
            {
                Expression ne = LowerExpression(ep.Expression);
                parts.Add(ReferenceEquals(ne, ep.Expression) ? ep : ep with { Expression = ne });
                if (!ReferenceEquals(ne, ep.Expression)) changed = true;
            }
            else { parts.Add(part); }
        }
        return changed ? fstr with { Parts = parts } : fstr;
    }

    // `x is None` / `x isnot None` on a nullable entity reference (`E?` = Roamed[E]): rewrite to
    // `x.is_none()` (negated -> `not x.is_none()`). Done before reachability so the Roamed[E].is_none()
    // instance gets seeded/instantiated for the concrete entity; codegen has no direct IsPattern
    // lowering for a Roamed handle. The frontend already narrowed the flow.
    private Expression LowerNoneIsPattern(IsPatternExpression ipe)
    {
        Expression inner = LowerExpression(ipe.Expression);
        if (!IsRoamedType(inner.ResolvedType))
        {
            // Not a Roamed operand — leave the IsPattern as-is (Maybe/variant handled downstream).
            return ReferenceEquals(inner, ipe.Expression) ? ipe : ipe with { Expression = inner };
        }

        TypeInfo? boolType = _registry.LookupType(name: "Bool");
        var isNoneCall = new CallExpression(
            Callee: new MemberExpression(Object: inner, MemberName: "is_none",
                Location: ipe.Location) { ResolvedType = boolType },
            Arguments: new List<Expression>(),
            Location: ipe.Location) { ResolvedType = boolType };
        if (!ipe.IsNegated)
            return isNoneCall;
        return new UnaryExpression(Operator: UnaryOperator.Not, Operand: isNoneCall,
            Location: ipe.Location) { ResolvedType = boolType };
    }

    // A call — INCLUDING a constructor call `E(...)`, which is a CallExpression (not a
    // CreatorExpression) at this phase — that produces a bare SF entity: recurse into its parts, then
    // `.roam()` the whole value.
    private Expression LowerCallExpression(CallExpression call)
    {
        Expression callee = LowerExpression(call.Callee);
        bool changed = !ReferenceEquals(callee, call.Callee);

        callee = ProjectRoamedReceiverIntoBareMe(call: call, callee: callee, changed: ref changed);

        var args = new List<Expression>(capacity: call.Arguments.Count);
        foreach (Expression a in call.Arguments)
        {
            Expression na = LowerExpression(a);
            args.Add(na);
            if (!ReferenceEquals(na, a)) changed = true;
        }
        // A construction `E(...)` stores its args into fields; a borrowed Roamed arg going into a
        // Roamed field must retain (else the field + the source local both release → double free).
        if (call.ResolvedType is EntityTypeInfo)
        {
            for (int k = 0; k < args.Count; k++) args[k] = RetainConstructionArg(args[k]);
            changed = true;
        }

        CallExpression lowered = changed ? call with { Callee = callee, Arguments = args } : call;

        // Project each Roamed argument that flows into a BARE-entity parameter through
        // `.raw_inner()`. SF routine/memberRoutine parameters are NOT Roamed-substituted, so their slot
        // is a bare `E` and must receive the real entity pointer — passing the RoamController
        // handle makes the callee read the controller as the entity (`x.field` → crash). Borrow
        // semantics: no retain, the caller keeps ownership. Skips construction (call.ResolvedType
        // is EntityTypeInfo), whose args are field stores needing a retained Roamed (handled
        // above). Mirrors the memberRoutine-receiver `raw_inner` interim below.
        if (call.ResolvedType is not EntityTypeInfo && lowered.ResolvedRoutine is { } argRoutine)
        {
            lowered = ProjectRoamedArgsIntoBareParams(call: lowered, routine: argRoutine);
        }

        // (The interim receiver `.raw_inner()` projection was removed with representation
        // unification: an SF entity memberRoutine's `me` now resolves as `Roamed[E]` — SignatureResolver
        // sets MeType — so the call passes the Roamed handle directly and `me.field` routes through
        // the Roamed access machinery. No projection needed.)

        return WrapCallResultInRoam(call: call, lowered: lowered);
    }

    // Receiver projection: a Roamed handle flowing as the RECEIVER into a BARE-`me` memberRoutine
    // must be projected through `.raw_inner()` to the real entity pointer. Stdlib entities
    // (analyzed in RF mode — e.g. an iterator's `emit!`/`try_emit`) have a bare `me`
    // (MeType is NOT Roamed), so passing the RoamController handle makes the callee read the
    // controller as the entity and crash. USER SF entity memberRoutines have MeType=Roamed and
    // correctly take the handle; memberRoutines declared on Roamed/RoamController itself
    // (roam/raw_inner/is_none) own the handle too. Gate on the resolved routine owning a
    // bare entity with a non-Roamed MeType. Mirrors the argument projection.
    private static Expression ProjectRoamedReceiverIntoBareMe(CallExpression call, Expression callee,
        ref bool changed)
    {
        if (callee is MemberExpression { Object: { } recv } calleeMember
            && call.ResolvedRoutine is { OwnerType: EntityTypeInfo } resolvedCallee
            && resolvedCallee.MeType is not RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed }
            && RoamedInnerEntity(recv.ResolvedType) is { } recvEntity)
        {
            Expression rawRecv = ProjectRawInner(arg: recv, targetEntity: recvEntity);
            if (!ReferenceEquals(rawRecv, recv))
            {
                changed = true;
                return calleeMember with { Object = rawRecv };
            }
        }
        return callee;
    }

    // Wrap the lowered call's result in `.roam()` where the call produces a bare SF entity or a
    // parameterized SF constructor whose `create` body returns the bare entity.
    private CallExpression WrapCallResultInRoam(CallExpression call, CallExpression lowered)
    {
        if (call.ResolvedType is EntityTypeInfo callEntity && !IsRfRealmRef(call.Callee))
            return WrapInRoam(inner: lowered, entity: callEntity);

        // A PARAMETERIZED SF constructor call (`Pt(v: x)`) is a CallExpression typed `Roamed[E]`
        // (SA roamed `create`'s declared return), but the `create` BODY returns the BARE entity
        // (create-returns-bare convention — see ReturnStatement lowering). So it still must be
        // wrapped once at the call site — otherwise a bare entity binds into a Roamed slot
        // (under-roamed → later destroyed as a controller → AccessViolation). The no-arg form
        // `Box()` is typed bare (handled above); this catches the arg-carrying form. Gated on
        // `create` (returns bare) so an ordinary routine returning a `Roamed[E]` value is NOT
        // re-wrapped, and on the SF realm (an RF entity stays bare).
        if (lowered.ResolvedRoutine is { IsCreator: true }
            && RoamedInnerEntity(call.ResolvedType) is { } createEntity
            && !IsRfRealmRef(call.Callee))
        {
            lowered.ResolvedType = createEntity;
            return WrapInRoam(inner: lowered, entity: createEntity);
        }

        return lowered;
    }

    // A generic-instance construction like `List[Node]()` stays a GenericMemberRoutineCallExpression
    // through codegen (the explicit `[T]` args keep it out of CallExpression form), so it must be
    // promoted here too — else a bare SF container never gets a RoamController and cycle collection
    // reads its raw buffer as a controller and crashes. Mirror the CallExpression construction path:
    // recurse args, retain Roamed-field args, then `.roam()` (promote).
    private Expression LowerGenericMemberRoutineCall(GenericMemberRoutineCallExpression gmce)
    {
        var gArgs = new List<Expression>(capacity: gmce.Arguments.Count);
        bool gChanged = false;
        foreach (Expression a in gmce.Arguments)
        {
            Expression na = LowerExpression(a);
            gArgs.Add(na);
            if (!ReferenceEquals(na, a)) gChanged = true;
        }

        if (gmce.ResolvedType is EntityTypeInfo)
        {
            for (int k = 0; k < gArgs.Count; k++) gArgs[k] = RetainConstructionArg(gArgs[k]);
            gChanged = true;
        }

        GenericMemberRoutineCallExpression loweredG =
            gChanged ? gmce with { Arguments = gArgs } : gmce;
        return gmce.ResolvedType is EntityTypeInfo gEntity && !IsRfRealmRef(gmce.Object)
            ? WrapInRoam(inner: loweredG, entity: gEntity)
            : loweredG;
    }

    // Rewrite each argument that lands in a BARE-entity parameter of `routine` from a Roamed handle to
    // `arg.raw_inner()` (the real entity pointer). Named args match by parameter name; positional args
    // map by order over the non-`me` parameters. Non-Roamed args and non-entity params are untouched.
    private static CallExpression ProjectRoamedArgsIntoBareParams(CallExpression call, RoutineInfo routine)
    {
        List<ParameterInfo> nonMe = BuildNonMeParams(routine);

        bool changed = false;
        var newArgs = new List<Expression>(capacity: call.Arguments.Count);
        int posIdx = 0;
        foreach (Expression a in call.Arguments)
        {
            ParameterInfo? param = ResolveArgParam(a: a, nonMe: nonMe, posIdx: ref posIdx);
            if (param?.Type is EntityTypeInfo entity)
            {
                Expression projected = ProjectRawInner(arg: a, targetEntity: entity);
                newArgs.Add(projected);
                if (!ReferenceEquals(projected, a)) changed = true;
            }
            else
            {
                newArgs.Add(a);
            }
        }

        return changed ? call with { Arguments = newArgs } : call;
    }

    // Builds the list of non-`me` parameters from a routine (the subset that call arguments map to).
    private static List<ParameterInfo> BuildNonMeParams(RoutineInfo routine)
    {
        var nonMe = new List<ParameterInfo>();
        foreach (ParameterInfo p in routine.Parameters)
            if (p.Name != "me") nonMe.Add(p);
        return nonMe;
    }

    // Resolves which parameter an argument corresponds to — by name for NamedArgumentExpression,
    // positionally otherwise. Advances posIdx for positional arguments.
    private static ParameterInfo? ResolveArgParam(Expression a, List<ParameterInfo> nonMe,
        ref int posIdx)
    {
        if (a is NamedArgumentExpression named)
        {
            foreach (ParameterInfo p in nonMe)
                if (p.Name == named.Name) return p;
            return null;
        }

        ParameterInfo? param = posIdx < nonMe.Count ? nonMe[posIdx] : null;
        posIdx++;
        return param;
    }

    // Wrap a Roamed-valued receiver/argument in `<value>.control()` : the inner bare entity, via the
    // Controlling marker-protocol deref (Roamed obeys Controlling[T]). A non-Roamed value (already a
    // bare entity, or a non-entity value) is returned unchanged. The access lock is applied around the
    // enclosing statement by RoamedLockBracketLoweringPass, which recognizes this control() coercion —
    // so reaching the inner through it stays serialized (unlike the old raw_inner, which was unlocked).
    private static Expression ProjectRawInner(Expression arg, EntityTypeInfo targetEntity)
    {
        Expression val = arg is NamedArgumentExpression na ? na.Value : arg;
        if (!IsRoamedType(val.ResolvedType)) return arg;

        var inner = new CallExpression(
            Callee: new MemberExpression(Object: val, MemberName: RuntimeContract.Control,
                Location: val.Location) { ResolvedType = targetEntity },
            Arguments: new List<Expression>(),
            Location: val.Location) { ResolvedType = targetEntity };

        return arg is NamedArgumentExpression named
            ? named with { Value = inner }
            : inner;
    }

    // True if the type is a `Roamed[E]` handle in either representation the pipeline produces: a
    // WrapperTypeInfo (from this pass's WrapInRoam) or a RecordTypeInfo (from a field read, whose type
    // TypeBodyResolver builds via GetOrCreateResolution).
    private static bool IsRoamedType(TypeInfo? t)
    {
        return t is WrapperTypeInfo { Name: RuntimeContract.Roamed } or RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed };
    }

    // The bare entity `E` inside a `Roamed[E]` handle, in either representation the pipeline produces
    // (WrapperTypeInfo from WrapInRoam, RecordTypeInfo from a resolver-built handle). Null when the
    // type is not a Roamed handle over an entity.
    private static EntityTypeInfo? RoamedInnerEntity(TypeInfo? t) => t switch
    {
        WrapperTypeInfo { Name: RuntimeContract.Roamed, InnerType: EntityTypeInfo e } => e,
        RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed, TypeArguments: [EntityTypeInfo e] } => e,
        _ => null
    };

    // A construction arg (a `NamedArgumentExpression` or bare value) whose value is a borrowed Roamed
    // reference must retain — it is stored into a Roamed field which the constructed entity now co-owns.
    private static Expression RetainConstructionArg(Expression arg)
    {
        if (arg is NamedArgumentExpression na)
        {
            Expression v = MaybeRoamCopy(na.Value);
            return ReferenceEquals(v, na.Value) ? na : na with { Value = v };
        }
        return MaybeRoamCopy(arg);
    }

    // A borrowed reference (identifier / field read) to a Roamed value in a COPY position (var-init or
    // assignment RHS) must retain — bump the biased refcount via `.roam()` — so the new binding owns its
    // own reference; otherwise the shared controller is released twice (double free). Fresh values (a
    // construct `E(...).roam()`, a call) are already owned and are left alone.
    private static Expression MaybeRoamCopy(Expression expr)
    {
        // Accept BOTH Roamed representations: WrapperTypeInfo (from this pass's WrapInRoam) and
        // RecordTypeInfo (from the resolver's GetOrCreateResolution — e.g. `me`/params/fields typed via
        // MeType / TypeBodyResolver). The `.roam()` copy verb retains the shared controller either way.
        if (expr is (IdentifierExpression or MemberExpression) && IsRoamedType(expr.ResolvedType))
        {
            TypeInfo roamed = expr.ResolvedType!;
            // Copy a borrowed Roamed value by bumping its biased refcount via the RC copy verb `.share()`
            // (renamed from the old construction-masquerading `.roam()` — Roamed[T].share() is the real
            // same-strength co-owner mint).
            return new CallExpression(
                Callee: new MemberExpression(Object: expr, MemberName: RuntimeContract.RefCount.Share,
                    Location: expr.Location) { ResolvedType = roamed },
                Arguments: new List<Expression>(),
                Location: expr.Location) { ResolvedType = roamed };
        }
        return expr;
    }

    // Wrap a bare-entity-valued expression in `<expr>.roam()`, retyped to Roamed[E].
    // An `RF::`-qualified construction/call deliberately opts OUT of the Suflae entity->Roamed
    // lowering: the realm tag reaches the bare RazorForge realm, so its bare-entity result must NOT be
    // `.roam()`-wrapped. This is what lets an SF wrapper entity hold a bare `RF::Core.List` inside
    // without re-roaming it into a `Roamed[List]`. Mirrors TypeResolver.ResolveType's `Realm != "RF"`
    // gate on the type-annotation side; the realm survives on the construction callee's identifier
    // (Parser.Expressions parses `RF::Core.List` into `IdentifierExpression { Realm = "RF" }`).
    private static bool IsRfRealmRef(Expression callee) =>
        callee is IdentifierExpression { Realm: "RF" };

    // Recognizes the `Roamed(from: X)` wrapper construction that <see cref="WrapInRoam"/> builds and
    // yields the inner (pre-roam) construction X. Used to peel a redundant roam off a `create` return.
    private static bool TryUnwrapRoamConstruction(Expression expr, out Expression? inner)
    {
        if (expr is CallExpression
            {
                LoweringKind: CallLoweringKind.WrapperConstruction,
                Callee: IdentifierExpression { Name: RuntimeContract.Roamed },
                Arguments: [NamedArgumentExpression { Name: "from", Value: { } from }]
            })
        {
            inner = from;
            return true;
        }
        inner = null;
        return false;
    }

    private CallExpression WrapInRoam(Expression inner, EntityTypeInfo entity)
    {
        WrapperTypeInfo roamed = _registry.GetOrCreateWrapperType(
            wrapperName: RuntimeContract.Roamed, innerType: entity, isReadOnly: false);
        // Build the resolved `Roamed[E](from: inner)` CONSTRUCTOR call (routes to `Roamed[T].create(from:T)`)
        // instead of the construction-masquerading `inner.roam()` — mirrors what the SA path produces for a
        // bare `Roamed(from: n)` (Calls.cs). `inner` is a FRESH entity rvalue (creator/literal/call), so it
        // moves into the handle with no `steal`. Callee is the type-name identifier; codegen constructs via
        // ConstructedType + ResolvedRoutine (see GenericCallLoweringPass wrapper-construction lowering).
        RoutineInfo? create = _registry.LookupCreatorOverload(type: roamed, argTypes: [entity]);
        return new CallExpression(
            Callee: new IdentifierExpression(Name: RuntimeContract.Roamed, Location: inner.Location)
                { ResolvedType = roamed },
            Arguments: [new NamedArgumentExpression(Name: "from", Value: inner, Location: inner.Location)],
            Location: inner.Location)
        {
            LoweringKind = CallLoweringKind.WrapperConstruction,
            ConstructedType = roamed,
            ResolvedRoutine = create,
            ResolvedType = roamed
        };
    }
}
