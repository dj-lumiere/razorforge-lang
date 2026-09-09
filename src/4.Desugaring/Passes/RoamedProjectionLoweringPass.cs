using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Desugaring.Passes;

/// <summary>
/// Suflae <c>Roamed[E]</c> receiver-transparency lowering. A Suflae local promoted to
/// <c>Roamed[E]</c> (post-SA) reaches its inner value's memberRoutines through the wrapper handle:
/// operator-lowered calls (<c>x in d</c> -> <c>d.contains(x)</c>, <c>d[i]</c> -> <c>d.getitem(i)</c>,
/// <c>==</c>/<c>&lt;</c>) and f-string <c>represent</c>/<c>diagnose</c> arrive holding the
/// <c>RoamController</c> handle.
///
/// <para>This pass makes the transparency a REAL AST rewrite instead of a codegen-inserted call:
/// for every <see cref="CallExpression"/> whose callee is a <see cref="MemberExpression"/> on a
/// <c>Roamed[E]</c> receiver, <see cref="RoamedTransparency.Project"/> is the single decision point.
/// When it applies it (a) re-resolves a wrapper-shadowed <c>represent</c>/<c>diagnose</c> to the
/// inner value's routine (stamped onto <see cref="CallExpression.ResolvedRoutine"/>), and (b) for a
/// bare-<c>me</c> inner memberRoutine, rewrites the receiver to <c>receiver.raw_inner()</c> — projecting the
/// controller handle to the real inner pointer. Codegen then emits the already-projected,
/// already-inner-resolved call verbatim.</para>
///
/// <para>Runs after <see cref="OperatorLoweringPass"/> and <see cref="FStringLoweringPass"/> so the
/// operator/f-string-lowered Roamed calls are visible. Idempotent: an already-projected receiver is
/// inner-typed (not Roamed) so <c>Project</c> returns null on a second visit.</para>
///
/// <para>All structural recursion (statements, other expressions) is supplied by
/// <see cref="AstRewriter"/>; this pass overrides only <see cref="VisitCall"/> to apply the Roamed
/// transparency projection after its children have been rewritten.</para>
/// </summary>
internal sealed class RoamedProjectionLoweringPass(PostprocessingContext ctx) : AstRewriter
{
    private TypeRegistry Registry => ctx.Registry;

    /// <summary>Lowers Roamed receiver projections across a whole program.</summary>
    public void Run(Program program)
    {
        BodyDispatch.RunOnProgram(program: program, lower: r => VisitStatement(stmt: r.Body));
    }

    /// <summary>Lowers Roamed receiver projections in synthesized variant bodies.</summary>
    public void RunOnVariantBodies()
    {
        BodyDispatch.RunOnVariantBodies(bodies: ctx.VariantBodies,
            lower: (_, body) => VisitStatement(stmt: body));
    }

    // ---- The core rewrite -----------------------------------------------------------------------

    /// <summary>
    /// The only node this pass rewrites: after the base rewrites the call's callee and arguments, apply
    /// the Roamed transparency projection when the callee is a member call on a Roamed[E] receiver.
    /// </summary>
    protected override Expression VisitCall(CallExpression e)
    {
        Expression lowered = base.VisitCall(e: e);
        return lowered is CallExpression call
            ? ProjectRoamedReceiver(call: call)
            : lowered;
    }

    // The core rewrite: when the callee is a member call on a Roamed[E] receiver and
    // RoamedTransparency.Project applies, stamp the inner memberRoutine as ResolvedRoutine and — for a
    // bare-`me` inner memberRoutine — rewrite the receiver to `receiver.raw_inner()`.
    private CallExpression ProjectRoamedReceiver(CallExpression call)
    {
        if (call.Callee is not MemberExpression member)
        {
            return call;
        }

        TypeInfo? receiverType = member.Object.ResolvedType;
        if (receiverType is null)
        {
            return call;
        }

        // The initial memberRoutine: the SA/operator-stamped routine, or (when unresolved on a Roamed
        // receiver — an operator-lowered `d[i]`/`x in d` whose owner has no such memberRoutine) a transparent
        // lookup on the wrapper, matching the codegen path this pass replaces.
        RoutineInfo? memberRoutine = call.ResolvedRoutine ??
                                     Registry.LookupMemberRoutine(type: receiverType,
                                         memberRoutineName: member.MemberName);

        RoamedTransparency.Projection? proj = RoamedTransparency.Project(
            receiverType: receiverType,
            memberRoutine: memberRoutine,
            memberName: member.MemberName,
            registry: Registry);
        if (proj is not { } roamProj)
        {
            return call;
        }

        // Stamp the inner memberRoutine (represent/diagnose shadowed by the wrapper → inner's; a null-stamped
        // operator call → the transparently-resolved inner memberRoutine) so codegen emits it directly.
        call.ResolvedRoutine = roamProj.MemberRoutine;
        call.LoweringKind =
            Verification.CallClassifier.ClassifyMemberRoutineCall(
                memberRoutine: roamProj.MemberRoutine);
        if (!roamProj.ProjectToInner)
        {
            return call;
        }

        Expression innerRecv = MakeControlCall(receiver: member.Object,
            receiverType: receiverType,
            innerType: roamProj.InnerType);
        if (ReferenceEquals(objA: innerRecv, objB: member.Object))
        {
            return call;
        }

        return call with { Callee = member with { Object = innerRecv } };
    }

    // Build `receiver.control()` : the inner entity, via the Controlling marker-protocol deref (Roamed
    // obeys Controlling[T]). Stamps the resolved control routine and inner type so codegen emits it as a
    // real, already-resolved call. Reachability seeds control via ImplicitCallContract.ForLiveType, so
    // the target is live/monomorphized. The access lock is applied around the enclosing statement by
    // RoamedLockBracketLoweringPass (which recognizes this control() coercion), so the deref is safe.
    private Expression MakeControlCall(Expression receiver, TypeInfo receiverType,
        TypeInfo innerType)
    {
        RoutineInfo? control = Registry.LookupMemberRoutine(type: receiverType,
            memberRoutineName: RuntimeContract.Control);
        if (control is null)
        {
            return receiver;
        }

        var callee = new MemberExpression(Object: receiver,
            MemberName: RuntimeContract.Control,
            Location: receiver.Location) { ResolvedType = innerType };
        return new CallExpression(Callee: callee,
            Arguments: new List<Expression>(),
            Location: receiver.Location) { ResolvedRoutine = control, ResolvedType = innerType };
    }
}
