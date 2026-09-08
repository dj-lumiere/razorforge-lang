using System.Collections.Generic;
using Compiler.Desugaring.Passes;
using Compiler.Instantiation;
using Compiler.Resolution;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using Verification;

namespace Compiler.Postprocessing.Passes;

/// <summary>
/// Post-instantiation pass that resolves overloads and assigns <see cref="CallLoweringKind"/>
/// for any <see cref="CallExpression"/> still marked <c>Unknown</c> after semantic analysis.
///
/// <para>Runs after <see cref="GenericCallLoweringPass"/> so that all generic memberRoutine calls
/// have already been lowered to plain <see cref="CallExpression"/> nodes with concrete
/// receiver types. At that point every <c>ResolvedType</c> on expressions is module-qualified
/// and structural (<c>FullName</c>-level) overload matching is safe.</para>
///
/// <para>SA leaves a call <c>Unknown</c> when overload resolution fails during Phase 4 -> typically because the receiver type lacked its module prefix at analysis time (types
/// resolved from generic bodies may arrive unqualified). This pass re-attempts resolution
/// with fully-qualified types and classifies the surviving unknowns via
/// <see cref="CallClassifier"/>.</para>
/// </summary>
internal sealed class CallOverloadResolutionPass
{
    /// <summary>
    /// Stores the registry state used by this compiler phase.
    /// </summary>
    private readonly TypeRegistry _registry;
    private readonly Dictionary<string, Statement>? _variantBodies;
    private readonly Dictionary<string, Statement>? _synthesizedBodies;
    // Per-body local-variable types (name → declared/inferred type), populated as the walk visits declarations
    // in order. Recovers a monomorphized body's member-call receiver whose reference ResolvedType is null
    // (`n.eq(...)` where `var n = me.count()`). Cleared per top-level body via WalkBody.
    private readonly Dictionary<string, TypeInfo> _localVarTypes = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance with the dependencies required for its compiler phase.
    /// </summary>
    internal CallOverloadResolutionPass(PostprocessingContext ctx)
    {
        _registry = ctx.Registry;
        _variantBodies = ctx.VariantBodies;
        _synthesizedBodies = ctx.SynthesizedBodies;
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void Run(Program program)
    {
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            switch (decl)
            {
                case RoutineDeclaration r:
                    WalkBody(r.Body);
                    break;
                case EntityDeclaration e:
                    WalkMemberList(e.Members);
                    break;
                case RecordDeclaration rec:
                    WalkMemberList(rec.Members);
                    break;
                case CrashableDeclaration cr:
                    WalkMemberList(cr.Members);
                    break;
            }
        }
    }

    /// <summary>
    /// Runs this compiler phase over its configured input.
    /// </summary>
    public void RunOnVariantBodies()
    {
        if (_variantBodies == null) return;
        foreach (Statement body in _variantBodies.Values)
            WalkBody(body);
    }

    /// <summary>
    /// Classifies call expressions in a flat sequence of statement bodies.
    /// Used for <c>InstantiatedGenericBodies</c> produced by GMP: <see cref="GenericAstRewriter"/>
    /// rewrites type parameters but does not re-classify <c>try_emit</c> and other wired calls,
    /// leaving their <c>LoweringKind = Unknown</c>. This pass fills in the missing kind.
    /// </summary>
    public void RunOnStatements(IEnumerable<Statement> statements)
    {
        foreach (Statement body in statements)
            WalkBody(body);
    }

    /// <summary>
    /// Like <see cref="RunOnStatements"/> but threads each body's owner type so the implicit `me` receiver is
    /// seeded before the walk — recovers member calls in a monomorph/variant clone whose `me` (and the locals
    /// cascading from it, e.g. `var n = me.count()`) arrive un-typed.
    /// </summary>
    public void RunOnBodiesWithOwners(IEnumerable<(Statement body, TypeInfo? owner)> bodies)
    {
        foreach ((Statement body, TypeInfo? owner) in bodies)
            WalkBody(body, owner: owner);
    }

    /// <summary>
    /// Classifies all <see cref="CallExpression"/> nodes inside synthesized derived-operator bodies
    /// (ne, lt, le, gt, ge, notcontains). These bodies are built by
    /// <see cref="Compiler.Synthesis.DerivedOperatorPass"/> with <c>ResolvedRoutine</c> set but
    /// <c>LoweringKind = Unknown</c>; this pass fills in the missing kind before codegen.
    /// </summary>
    public void RunOnSynthesizedBodies()
    {
        if (_synthesizedBodies == null) return;
        foreach (Statement body in _synthesizedBodies.Values)
            WalkBody(body);
    }

    /// <summary>
    /// Walk member list as part of this compiler phase.
    /// </summary>
    private void WalkMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
            if (m is RoutineDeclaration r) WalkBody(r.Body);
    }

    // -----------------------------------------------------------------------------

    /// <summary>True when <paramref name="type"/> still carries a generic parameter (directly or nested in a
    /// type argument) — i.e. it is not yet a fully-concrete monomorphized type.</summary>
    private static bool TypeContainsGenericParameter(TypeInfo type) =>
        type is GenericParameterTypeInfo or ProtocolSelfTypeInfo or ComptimeConstGenericTypeInfo
        || (type.TypeArguments?.Any(predicate: TypeContainsGenericParameter) ?? false);

    /// <summary>Walks one top-level routine body, resetting the per-body local-variable type scope first.
    /// When <paramref name="owner"/> is known (a monomorphized member routine), seeds the implicit receiver
    /// `me` so a variant/monomorph clone that left `me` un-typed can still resolve `me.count()` etc.</summary>
    private void WalkBody(Statement? body, TypeInfo? owner = null)
    {
        if (body == null) return;
        _localVarTypes.Clear();
        if (owner is { } o and not ErrorTypeInfo) _localVarTypes[key: "me"] = o;
        WalkStatement(body);
    }

    private void WalkStatement(Statement stmt)
    {
        switch (stmt)
        {
            case BlockStatement block:
                foreach (Statement s in block.Statements) WalkStatement(s);
                break;
            case IfStatement ifs:
                WalkExpression(ifs.Condition);
                WalkStatement(ifs.ThenStatement);
                if (ifs.ElseStatement != null) WalkStatement(ifs.ElseStatement);
                break;
            case WhileStatement w:
                WalkExpression(w.Condition);
                WalkStatement(w.Body);
                break;
            case LoopStatement loop:
                WalkStatement(loop.Body);
                break;
            case EachStatement f:
                WalkExpression(f.Iterable);
                WalkStatement(f.Body);
                break;
            case WhenStatement ws:
                WalkExpression(ws.Expression);
                foreach (WhenClause c in ws.Clauses) WalkStatement(c.Body);
                break;
            case ReturnStatement { Value: not null } ret:
                WalkExpression(ret.Value);
                break;
            case AssignmentStatement assign:
                WalkExpression(assign.Target);
                WalkExpression(assign.Value);
                break;
            case DeclarationStatement { Declaration: VariableDeclaration { Initializer: not null } vd }:
                WalkExpression(vd.Initializer);
                // Track the local's inferred type (from the walked initializer) so a later member call on a
                // reference to it can recover a receiver type the monomorph clone left un-annotated.
                if (vd.Initializer.ResolvedType is { } vt and not ErrorTypeInfo)
                    _localVarTypes[key: vd.Name] = vt;
                break;
            case ExpressionStatement es:
                WalkExpression(es.Expression);
                break;
            case DiscardStatement ds:
                WalkExpression(ds.Expression);
                break;
            case ThrowStatement ts:
                WalkExpression(ts.Error);
                break;
            case VariantReturnStatement { Value: not null } vrs:
                WalkExpression(vrs.Value);
                break;
            case BecomesStatement bs:
                WalkExpression(bs.Value);
                break;
            case UsingStatement us:
                WalkStatement(us.Body);
                if (us.FallbackBody != null) WalkStatement(us.FallbackBody);
                break;
            case DangerStatement danger:
                WalkStatement(danger.Body);
                break;
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Walk expression as part of this compiler phase.
    /// </summary>
    private void WalkExpression(Expression expr) // NOSONAR S3776
    {
        switch (expr)
        {
            case LiteralExpression or IdentifierExpression or TypeIdExpression:
                return;

            case CallExpression call:
                ClassifyCall(call);
                WalkExpression(call.Callee);
                foreach (Expression arg in call.Arguments) WalkExpression(arg);
                break;

            case BinaryExpression bin:
                WalkExpression(bin.Left);
                WalkExpression(bin.Right);
                break;

            case UnaryExpression un:
                WalkExpression(un.Operand);
                break;

            case MemberExpression mem:
                WalkExpression(mem.Object);
                break;

            case OptionalMemberExpression omem:
                WalkExpression(omem.Object);
                break;

            case NamedArgumentExpression named:
                WalkExpression(named.Value);
                break;

            case IndexExpression idx:
                WalkExpression(idx.Object);
                WalkExpression(idx.Index);
                break;

            case TypeConversionExpression conv:
                WalkExpression(conv.Expression);
                break;

            case StealExpression steal:
                WalkExpression(steal.Operand);
                break;

            case GenericMemberRoutineCallExpression gmc:
                WalkExpression(gmc.Object);
                foreach (Expression arg in gmc.Arguments) WalkExpression(arg);
                break;

            case GenericMemberExpression gmem:
                WalkExpression(gmem.Object);
                break;

            case IsPatternExpression ip:
                WalkExpression(ip.Expression);
                break;

            case FlagsTestExpression flags:
                WalkExpression(flags.Subject);
                break;

            case ChainedComparisonExpression chain:
                foreach (Expression op in chain.Operands) WalkExpression(op);
                break;

            case CompoundAssignmentExpression comp:
                WalkExpression(comp.Target);
                WalkExpression(comp.Value);
                break;

            case RangeExpression range:
                WalkExpression(range.Start);
                WalkExpression(range.End);
                if (range.Step != null) WalkExpression(range.Step);
                break;

            case ConditionalExpression cond:
                WalkExpression(cond.Condition);
                WalkExpression(cond.TrueExpression);
                WalkExpression(cond.FalseExpression);
                break;

            case TupleLiteralExpression tuple:
                foreach (Expression e in tuple.Elements) WalkExpression(e);
                break;

            case ListLiteralExpression list:
                foreach (Expression e in list.Elements) WalkExpression(e);
                break;

            case SetLiteralExpression set:
                foreach (Expression e in set.Elements) WalkExpression(e);
                break;

            case DictLiteralExpression dict:
                foreach ((Expression k, Expression v) in dict.Pairs)
                {
                    WalkExpression(k);
                    WalkExpression(v);
                }
                break;

            case CreatorExpression creator:
                foreach ((_, Expression v) in creator.MemberVariables) WalkExpression(v);
                break;

            case InsertedTextExpression fstr:
                foreach (InsertedTextPart part in fstr.Parts)
                    if (part is ExpressionPart ep) WalkExpression(ep.Expression);
                break;
        }
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Performs the classify call step for this compiler phase.
    /// </summary>
    private void ClassifyCall(CallExpression call)
    {
        // BACKFILL a null/Error ResolvedType from an ALREADY-resolved routine's return, even when the call is
        // otherwise fully classified (and skipped below). A monomorph / variant clone can carry a resolved
        // ResolvedRoutine yet an un-typed ResolvedType: e.g. `var n = me.count()` keeps ErrorTypeInfo, so `n`
        // never gets a tracked type and a later `n.eq(...)` can't recover its receiver → reaches codegen
        // unresolved. Propagate the concrete return so `n` types. (Do NOT touch a ProtocolTypeInfo ResolvedType
        // here — the abstract-iterator case is handled by the staleProtocolReturn full re-resolve below;
        // backfilling `iter()` returns naively would recurse into RangeEmittable[RangeEmittable[…]].)
        if (call.ResolvedRoutine is { ReturnType: { } rrRet } && rrRet is not ProtocolTypeInfo and not ErrorTypeInfo
            && call.ResolvedType is null or ErrorTypeInfo)
            call.ResolvedType = rrRet;

        // A monomorphized body inherits its template's iter/try_emit resolution: AnnotateIterAndTryEmit
        // (each-loop desugar) annotated the call's ResolvedType with the GENERIC template's `iter` return — the
        // abstract protocol `Emittable[T]` (owner typed as `Iterable[T]`). After the routine is monomorphized
        // (param `Iterable[S64]` → a concrete implementer `Range[S64]`), GenericAstRewriter re-homes the
        // receiver + ResolvedRoutine to the concrete type but leaves ResolvedType as the stale abstract
        // `Emittable[S64]`. The each-loop iterator var infers its type from that ResolvedType → the protocol
        // leaks into codegen (GetLlvmType hard-errors). Detect it — ResolvedType is a bare protocol while the
        // receiver is now a concrete (non-protocol) type — and force a full re-resolution of `iter` on the
        // concrete receiver (its own returns the concrete `RangeEmittable[S64]`), refreshing ResolvedType.
        bool staleProtocolReturn =
            call.ResolvedType is ProtocolTypeInfo
            && call.Callee is MemberExpression
            {
                Object.ResolvedType: { } recvT and not ProtocolTypeInfo and not GenericParameterTypeInfo
                    and not ErrorTypeInfo
            }
            // Only when the receiver is FULLY concrete. In a still-generic body (receiver `RangeEmittable[T]`),
            // re-resolving `iter()` yields a deeper `RangeEmittable[RangeEmittable[T]]` which is then marked a
            // live owner and force-seeds its entity self-free (`hijack`/`Hijacked[…].invalidate`), each level
            // spawning the next → unbounded `RangeEmittable[RangeEmittable[…]]` monomorphization. Concrete
            // receivers (Range[S64], List[S64]) can't nest, so gate on no-generic-parameter.
            && !TypeContainsGenericParameter(type: recvT);
        // Skip only when FULLY classified — both the lowering kind AND the target routine are known. A body
        // may arrive with LoweringKind set (by GenericAstRewriter) yet ResolvedRoutine still null (a cloned
        // derive's `me.assign()`); the demand collector relies on this pass as the sole member-call resolver
        // (codegen no longer resolves call targets at emission), so it must still resolve those.
        if (call.LoweringKind != CallLoweringKind.Unknown && call.ResolvedRoutine != null
            && !staleProtocolReturn)
            return;

        // A stale abstract-protocol ResolvedType (above) must fall through to the FULL member-call resolver,
        // not the fast path below (which keeps the stale ResolvedType). Drop the routine so ClassifyMemberCall
        // re-binds `iter` on the concrete receiver and refreshes ResolvedType to the concrete iterator.
        if (staleProtocolReturn) call.ResolvedRoutine = null;

        // Fast path: routine already resolved by DerivedOperatorPass or SA.
        // Wired routines like ComparisonSign.eq may not be findable via LookupMemberRoutineOverload
        // (they are handled by codegen directly, not registered as normal overloads).
        if (call.ResolvedRoutine != null)
        {
            call.LoweringKind = call.Callee is MemberExpression
                ? CallClassifier.ClassifyMemberRoutineCall(memberRoutine: call.ResolvedRoutine)
                : CallClassifier.ClassifyStandaloneRoutineCall(routine: call.ResolvedRoutine);
            return;
        }

        // Collect fully-qualified arg types set by SA/instantiation on each argument.
        // Some stdlib generic bodies are lowered before SA runs, so literals may lack ResolvedType.
        // Track whether all types are known; non-const-generic member calls still bail when incomplete.
        var argTypes = new List<TypeInfo>(capacity: call.Arguments.Count);
        bool allArgTypesKnown = true;
        foreach (Expression arg in call.Arguments)
        {
            TypeInfo? t = arg is NamedArgumentExpression named
                ? named.Value.ResolvedType
                : arg.ResolvedType;
            if (t == null)
                allArgTypesKnown = false;
            else
                argTypes.Add(t);
        }

        switch (call.Callee)
        {
            case MemberExpression member:
                ClassifyMemberCall(call: call, member: member, argTypes: argTypes,
                    allArgTypesKnown: allArgTypesKnown);
                break;
            case IdentifierExpression { Name: var name }:
                ClassifyStandaloneCall(call: call, name: name, argTypes: argTypes,
                    allArgTypesKnown: allArgTypesKnown);
                break;
        }
    }

    /// <summary>
    /// Resolves and classifies a member-routine call, applying the const-generic receiver
    /// fallback and the failable-form retry.
    /// </summary>
    private void ClassifyMemberCall(CallExpression call, MemberExpression member,
        List<TypeInfo> argTypes, bool allArgTypesKnown)
    {
        // A derive-template field-walk body (`me.$nameof(m).destroy()`) deliberately leaves each unrolled
        // member access's ResolvedType DEFERRED (GenericAstRewriter keeps member types deferred so a
        // recursively-typed field can't spawn unbounded concrete instantiations). `me` itself IS typed to
        // the owner, so recover the field-access receiver by walking the object chain and reading each field
        // type off the owner — WITHOUT backfilling the AST node (leave it deferred; this is resolution-only).
        TypeInfo? receiverType = member.Object.ResolvedType is { } rt and not ErrorTypeInfo
            ? rt
            : ComputeDeferredType(expr: member.Object);
        // A monomorphized body's local-variable REFERENCE can arrive with a null/deferred ResolvedType (the
        // clone doesn't re-annotate every reference), so a member call on it (`n == 0` → `n.eq(...)` where
        // `var n = me.count()`) can't find its receiver type and reaches codegen unresolved. Recover it from
        // the var's DECLARATION type, tracked as this walk visits declarations in body order.
        if (receiverType is null or ErrorTypeInfo && member.Object is IdentifierExpression idRecv
            && _localVarTypes.TryGetValue(key: idRecv.Name, value: out TypeInfo? declaredT))
            receiverType = declaredT;
        if (receiverType == null) return;

        // Const-generic value types (e.g. ConstGenericValueTypeInfo("63") = N=63 in Array[T, 63])
        // are not registered in _routinesByOwner.  Resolve to the underlying numeric type so
        // memberRoutine lookup can find operators like sub!.
        // Also, their arguments may lack ResolvedType (pre-SA stdlib bodies), so allow a
        // type-less fallback lookup — there is typically only one overload for numeric operators.
        bool isConstGenericReceiver = receiverType is ConstGenericValueTypeInfo;
        if (isConstGenericReceiver)
        {
            var constVal = (ConstGenericValueTypeInfo)receiverType;
            string underlyingName = constVal.ExplicitTypeName ?? "U64";
            TypeInfo? resolved = _registry.LookupType(underlyingName);
            if (resolved == null) return;
            receiverType = resolved;
        }
        // NOTE: unknown arg types no longer bail here. A monomorphized / variant-clone body can leave a literal
        // arg (`0u64` in `n.eq(0u64)`) with an ErrorTypeInfo, but the receiver is concrete (U64) and the member
        // is a unique overload — so fall through to the by-NAME lookup below (which returns null on genuine
        // ambiguity, leaving the call unresolved exactly as the old bail did). Only the overload-BY-ARGTYPES
        // lookup needs all arg types; it is already guarded on allArgTypesKnown.

        RoutineInfo? memberRoutine = allArgTypesKnown
            ? _registry.LookupMemberRoutineOverload(type: receiverType,
                memberRoutineName: member.MemberName, argTypes: argTypes)
            : null;
        memberRoutine ??= _registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: member.MemberName);

        // If the non-failable form isn't registered, try the failable form.
        // E.g. U64.sub is not defined (underflow is undefined); only U64.sub! exists.
        // MemberName is bare; failability is structural — retry with isFailable: true.
        if (memberRoutine == null && !member.IsFailable)
        {
            memberRoutine = allArgTypesKnown
                ? _registry.LookupMemberRoutineOverload(type: receiverType,
                    memberRoutineName: member.MemberName, argTypes: argTypes)
                : null;
            memberRoutine ??= _registry.LookupMemberRoutine(type: receiverType,
                memberRoutineName: member.MemberName, isFailable: true);
        }

        if (memberRoutine == null) return;

        call.ResolvedRoutine = memberRoutine;
        call.LoweringKind = CallClassifier.ClassifyMemberRoutineCall(memberRoutine: memberRoutine);
        // Backfill a call whose ResolvedType is stale/unresolved (null, ErrorTypeInfo, or a lingering abstract
        // protocol) from the freshly-resolved concrete routine's return type. A monomorphized / variant-clone
        // body leaves member-call ResolvedTypes stale: `var n = me.count()` keeps an ErrorTypeInfo so `n` is
        // untyped and a later `n.eq(...)` can't recover its receiver; `var it = r.iter()` keeps the abstract
        // `Emittable[S64]` which leaks into codegen. Propagating the concrete return forward types the local
        // (count→U64 lets `n` resolve, iter→RangeEmittable[S64] fixes the each-loop var).
        if (call.ResolvedType is null or ErrorTypeInfo or ProtocolTypeInfo
            && memberRoutine.ReturnType is { } concreteRet
            && concreteRet is not ProtocolTypeInfo and not ErrorTypeInfo)
            call.ResolvedType = concreteRet;
    }

    /// <summary>
    /// Computes an expression's type when its own <see cref="Expression.ResolvedType"/> is null/deferred, by
    /// walking a member-access chain and reading each field's static type off its (recursively computed)
    /// owner. Only field accesses off a typed base (`me`, a typed local) are recovered — it never invents a
    /// type. Used to resolve member calls in derive-template field-walk bodies whose unrolled member accesses
    /// are intentionally left type-deferred (see <c>GenericAstRewriter</c>). Returns null when the chain does
    /// not bottom out in a known type. Does NOT mutate the AST — resolution-only.
    /// </summary>
    private static TypeInfo? ComputeDeferredType(Expression expr)
    {
        if (expr.ResolvedType is { } t and not ErrorTypeInfo)
            return t;
        if (expr is MemberExpression member)
        {
            TypeInfo? ownerType = ComputeDeferredType(expr: member.Object);
            return ownerType switch
            {
                RecordTypeInfo record => record.LookupMemberVariable(memberVariableName: member.MemberName)?.Type,
                EntityTypeInfo entity => entity.LookupMemberVariable(memberVariableName: member.MemberName)?.Type,
                _ => null
            };
        }

        return null;
    }

    /// <summary>
    /// Resolves and classifies a standalone (free-routine) call by overload-by-arg-types,
    /// falling back to a unique by-name lookup.
    /// </summary>
    private void ClassifyStandaloneCall(CallExpression call, string name,
        List<TypeInfo> argTypes, bool allArgTypesKnown)
    {
        // Overload-by-arg-types when all arg types are known; otherwise fall back to a
        // unique by-name lookup. Stdlib bodies aren't fully type-annotated, so a free call
        // like `decimalfixed_neg(a: you)` inside a variant body can reach here with an
        // untyped argument — the by-name lookup still resolves it when the name is
        // unambiguous. (A genuinely ambiguous name with unknown arg types stays unresolved.)
        RoutineInfo? routine = allArgTypesKnown
            ? _registry.LookupRoutineOverload(baseName: name, argTypes: argTypes)
            : null;
        routine ??= _registry.LookupRoutine(fullName: name);
        if (routine == null) return;

        call.ResolvedRoutine = routine;
        call.LoweringKind = CallClassifier.ClassifyStandaloneRoutineCall(routine: routine);
    }
}
