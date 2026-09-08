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
                    WalkStatement(r.Body);
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
            WalkStatement(body);
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
            WalkStatement(body);
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
            WalkStatement(body);
    }

    /// <summary>
    /// Walk member list as part of this compiler phase.
    /// </summary>
    private void WalkMemberList(List<SyntaxTree.Declaration> members)
    {
        foreach (SyntaxTree.Declaration m in members)
            if (m is RoutineDeclaration r) WalkStatement(r.Body);
    }

    // -----------------------------------------------------------------------------

    /// <summary>
    /// Walk statement as part of this compiler phase.
    /// </summary>
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
        // Skip only when FULLY classified — both the lowering kind AND the target routine are known. A body
        // may arrive with LoweringKind set (by GenericAstRewriter) yet ResolvedRoutine still null (a cloned
        // derive's `me.assign()`); the demand collector relies on this pass as the sole member-call resolver
        // (codegen no longer resolves call targets at emission), so it must still resolve those.
        if (call.LoweringKind != CallLoweringKind.Unknown && call.ResolvedRoutine != null) return;

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
        else if (!allArgTypesKnown)
        {
            return;
        }

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
