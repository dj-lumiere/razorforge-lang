using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Moves the <c>Roamed[E]</c> access-lock bracket out of codegen and into a real AST call. A direct
/// field READ or WRITE through a <c>Roamed[E]</c> handle must be wrapped in
/// <c>handle.lock_enter()</c> … <c>handle.lock_exit()</c> so that — once the object has ESCAPED to
/// another worker — the field touch is serialized. The lock is a no-op while the object is LOCAL and a
/// <b>reentrant</b>, task-keyed acquire once escaped (see <c>Roamed.rf</c>).
///
/// <para>Previously codegen inserted <c>call void @lock_enter/@lock_exit(handle)</c> itself, with NO
/// surface AST call, bracketing each individual field read/write. This pass makes it a REAL AST rewrite
/// instead: for every statement that directly contains a Roamed[E] field access, a
/// <c>handle.lock_enter()</c> <see cref="ExpressionStatement"/> is inserted immediately BEFORE the
/// statement and a matching <c>handle.lock_exit()</c> immediately AFTER it. The read/write itself keeps
/// its value — codegen now just projects through <c>RoamController.data</c> and emits the load/store,
/// with the lock bracket already present as ordinary calls surrounding the whole statement.</para>
///
/// <para><b>Bracket-count decision: per-statement (once per field-access receiver), NOT per-access.</b>
/// The old codegen bracketed each individual access; because the lock is <i>reentrant</i> and a no-op
/// while local, bracketing the whole enclosing statement once per receiver is exact-parity for the
/// escaped-mode serialization semantics (every field touch inside the statement is still inside a held
/// acquire) while keeping this pass simple and safe under NESTED access (multiple Roamed reads in one
/// statement). A single enter…exit spanning the statement cannot leave any field touch unguarded.</para>
///
/// <para>Reachability already seeds <c>lock_enter</c>/<c>lock_exit</c> for every live <c>Roamed[T]</c>
/// via <c>ImplicitCallContract.ForLiveType</c>, so the targets are live/monomorphized. Mirrors
/// <see cref="RoamedSpawnPromotionLoweringPass"/> (statement insertion around a site). Runs after the
/// other Roamed passes so the field accesses it brackets are in final form.</para>
/// </summary>
internal sealed class RoamedLockBracketLoweringPass(PostprocessingContext ctx)
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Inserts Roamed field-access lock brackets across a whole program.</summary>
    public void Run(Program program)
    {
        foreach (SyntaxTree.Declaration decl in program.Declarations.OfType<SyntaxTree.Declaration>())
        {
            LowerDeclaration(decl);
        }
    }

    /// <summary>Inserts Roamed field-access lock brackets in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        foreach (string key in ctx.VariantBodies.Keys.ToList())
        {
            LowerBody(ctx.VariantBodies[key]);
        }
    }

    private void LowerDeclaration(SyntaxTree.Declaration decl)
    {
        switch (decl)
        {
            case RoutineDeclaration r:
                LowerBody(r.Body);
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

    private void LowerMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
        {
            if (m is RoutineDeclaration mr) LowerBody(mr.Body);
        }
    }

    private void LowerBody(Statement body)
    {
        if (body is BlockStatement block) LowerBlock(block);
    }

    // ---- Block rewrite (in place, mirroring RoamedSpawnPromotionLoweringPass) ---------------------

    private void LowerBlock(BlockStatement block)
    {
        var rewritten = new List<Statement>(capacity: block.Statements.Count);
        foreach (Statement stmt in block.Statements)
        {
            RecurseInto(stmt);
            // An atomic-width scalar global RMW (`__globals__.n = __globals__.n.add(d)`) is emitted as a
            // lock-free `atomicrmw` on the field address by codegen — it must NOT be bracketed, or it
            // would needlessly take the escaped lock (and the two paths would disagree). Everything else
            // (heavy-value field RMW, plain read/write) still gets the access-lock brackets.
            List<Expression> handles = IsAtomicGlobalRmwStatement(stmt)
                ? new List<Expression>()
                : FieldAccessHandles(stmt);
            foreach (Expression h in handles) AddBracket(rewritten, h, RuntimeContract.RoamedMemberRoutine.LockEnter);
            rewritten.Add(item: stmt);
            foreach (Expression h in handles) AddBracket(rewritten, h, RuntimeContract.RoamedMemberRoutine.LockExit);
        }
        block.Statements.Clear();
        block.Statements.AddRange(collection: rewritten);
    }

    private void AddBracket(List<Statement> into, Expression handle, string memberRoutine)
    {
        if (MakeLockCall(handle: handle, memberRoutine: memberRoutine) is { } call) into.Add(item: call);
    }

    // The Roamed[E] handle expressions of every DIRECT field access (read or write) in `stmt`'s own
    // expressions. One entry per field-access occurrence (per-statement bracketing, see class doc).
    // Nested statements are bracketed when the block recursion reaches them, so only this statement's
    // own expressions are walked here (matching RoamedSpawnPromotionLoweringPass.CollectPromotes).
    // True when the statement is the lock-free atomic-global RMW that codegen lowers to a single
    // `atomicrmw` (shared matcher with codegen, so both agree on exactly which statements skip the lock).
    private static bool IsAtomicGlobalRmwStatement(Statement stmt)
    {
        (Expression Target, Expression Value)? tv = stmt switch
        {
            AssignmentStatement a => (a.Target, a.Value),
            ExpressionStatement { Expression: BinaryExpression { Operator: BinaryOperator.Assign } b } =>
                (b.Left, b.Right),
            _ => null
        };
        return tv is { } p && CodeGen.LlvmCodeGenerator.TryMatchAtomicModuleGlobalRmw(
            target: p.Target, value: p.Value, fieldMember: out _, isFloat: out _, atomicOp: out _,
            delta: out _);
    }

    private static List<Expression> FieldAccessHandles(Statement stmt)
    {
        var handles = new List<Expression>();
        foreach (Expression e in DirectExpressions(stmt))
        {
            AstWalker.WalkExpressions(root: e, visit: n =>
            {
                if (n is not MemberExpression member) return;
                // A direct field access through a Roamed handle, OR a memberRoutine-dispatch deref (the
                // `control`/`refer`/`raw_inner` coercion RoamedProjectionLoweringPass inserts on a
                // Roamed receiver). Both must hold the access lock across the enclosing statement —
                // otherwise a memberRoutine call on a Roamed receiver (e.g. an SF wrapper's `xs.getitem!(i)`,
                // whose bare-`me` inner is reached via the coercion) touches the object UNLOCKED,
                // breaking escaped-mode serialization. Coarse (whole-statement) bracketing is exact for
                // the reentrant, task-keyed lock.
                if (RoamedFieldReceiver(member) is { } fieldHandle)
                    handles.Add(item: fieldHandle);
                else if (RoamedCoercionReceiver(member) is { } coerceHandle)
                    handles.Add(item: coerceHandle);
            });
        }
        return handles;
    }

    // The expressions directly owned by a statement (its condition/value/target), excluding nested
    // Statement bodies — those are visited separately by the block recursion.
    private static IEnumerable<Expression> DirectExpressions(Statement stmt)
    {
        switch (stmt)
        {
            case ExpressionStatement es:
                yield return es.Expression;
                break;
            case DiscardStatement ds:
                yield return ds.Expression;
                break;
            case ReturnStatement { Value: not null } s: yield return s.Value; break;
            case VariantReturnStatement { Value: not null } s: yield return s.Value; break;
            case BecomesStatement s: yield return s.Value; break;
            case ThrowStatement s: yield return s.Error; break;
            case AssignmentStatement s: yield return s.Target; yield return s.Value; break;
            case DestructuringStatement s: yield return s.Initializer; break;
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } v }:
                yield return v.Initializer; break;
            case IfStatement s: yield return s.Condition; break;
            case WhileStatement s: yield return s.Condition; break;
            case EachStatement s: yield return s.Iterable; break;
            case WhenStatement s: yield return s.Expression; break;
            case UsingStatement s: yield return s.Resource; break;
        }
    }

    // When `member` is a DIRECT field access through a Roamed[E] handle (the member name is a member
    // VARIABLE of the inner entity, not a memberRoutine — a memberRoutine call's callee member name never matches an
    // entity field), returns the Roamed[E] handle expression to bracket; otherwise null.
    private static Expression? RoamedFieldReceiver(MemberExpression member)
    {
        if (RoamedInnerEntity(member.Object.ResolvedType) is not { } innerEntity) return null;
        bool isField = innerEntity.MemberVariables.Any(predicate: mv => mv.Name == member.MemberName);
        return isField ? member.Object : null;
    }

    // When `member` is a deref COERCION (`control` / `refer` / `raw_inner`) on a Roamed[E] handle —
    // the receiver projection RoamedProjectionLoweringPass inserts for a memberRoutine call on a Roamed
    // receiver — returns the Roamed[E] handle to bracket; otherwise null. This is what makes a memberRoutine
    // call on a Roamed receiver hold the access lock (the field-access path above only catches direct
    // field touches). The lifecycle coercions on the handle itself (`roam`/`promote`/`lock_*`/`destroy`)
    // are NOT deref and are deliberately excluded.
    private static Expression? RoamedCoercionReceiver(MemberExpression member)
    {
        if (member.MemberName is not (RuntimeContract.RoamedMemberRoutine.RawInner
            or RuntimeContract.Control or RuntimeContract.Access)) return null;
        return RoamedInnerEntity(member.Object.ResolvedType) is not null ? member.Object : null;
    }

    // The bare entity `E` inside a `Roamed[E]` handle, in either representation the pipeline produces
    // (WrapperTypeInfo from SuflaeEntityLoweringPass.WrapInRoam, RecordTypeInfo from a resolver-built
    // handle). Null when the type is not a Roamed handle over an entity.
    private static EntityTypeInfo? RoamedInnerEntity(TypeInfo? t) => t switch
    {
        WrapperTypeInfo { Name: RuntimeContract.Roamed, InnerType: EntityTypeInfo e } => e,
        RecordTypeInfo { GenericDefinition.Name: RuntimeContract.Roamed, TypeArguments: [EntityTypeInfo e] } => e,
        _ => null
    };

    // Build `handle.lock_enter()` / `handle.lock_exit()` as an ExpressionStatement. Both are dangerous,
    // take the Roamed handle, and return void — so the statement is a pure side effect around the field
    // access, exactly what the removed codegen bracket did. The handle node is reused (side-effect-free
    // to re-evaluate: an identifier / member handle), mirroring the promote/retain steps.
    private ExpressionStatement? MakeLockCall(Expression handle, string memberRoutine)
    {
        if (handle.ResolvedType is not { } recvType) return null;
        RoutineInfo? routine = Registry.LookupMemberRoutine(type: recvType, memberRoutineName: memberRoutine);
        if (routine is null) return null;

        var callee = new MemberExpression(Object: handle, MemberName: memberRoutine, Location: handle.Location)
        {
            ResolvedType = recvType
        };
        var call = new CallExpression(Callee: callee, Arguments: new List<Expression>(),
            Location: handle.Location)
        {
            ResolvedRoutine = routine,
            ResolvedType = routine.ReturnType,
            LoweringKind = Compiler.Verification.CallClassifier.ClassifyMemberRoutineCall(memberRoutine: routine)
        };
        return new ExpressionStatement(Expression: call, Location: handle.Location);
    }

    // ---- Nested-block recursion (mirrors RoamedSpawnPromotionLoweringPass) ------------------------

    private void RecurseInto(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement b:
                LowerBlock(b);
                break;
            case IfStatement i:
                RecurseStmt(i.ThenStatement);
                if (i.ElseStatement != null) RecurseStmt(i.ElseStatement);
                break;
            case WhileStatement w:
                RecurseStmt(w.Body);
                if (w.ElseBranch != null) RecurseStmt(w.ElseBranch);
                break;
            case LoopStatement l:
                RecurseStmt(l.Body);
                break;
            case EachStatement f:
                RecurseStmt(f.Body);
                if (f.ElseBranch != null) RecurseStmt(f.ElseBranch);
                break;
            case DangerStatement d:
                LowerBlock(d.Body);
                break;
            case UsingStatement u:
                RecurseStmt(u.Body);
                if (u.FallbackBody != null) RecurseStmt(u.FallbackBody);
                break;
            case WhenStatement whenStmt:
                foreach (WhenClause clause in whenStmt.Clauses) RecurseStmt(clause.Body);
                break;
        }
    }

    private void RecurseStmt(Statement stmt)
    {
        if (stmt is BlockStatement b) LowerBlock(b);
        else RecurseInto(stmt);
    }
}
