using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Instantiation.Passes;

/// <summary>
/// Track-C tripwire (C1). After GMP + all instantiated-body lowering, verifies that every
/// FULLY-CONCRETE monomorphized body is free of residual generics: no <see cref="TypeInfo"/>
/// containing a <see cref="GenericParameterTypeInfo"/>, no callee bound to a generic-definition
/// owner, no unresolved const-generic identifier. If any survive, LLVM codegen would have to
/// monomorphize / substitute at emit time — the exact thing Track C removes — so we throw LOUDLY
/// here, naming the offending node/type, instead of leaking it into codegen.
///
/// <para>
/// Only bodies whose <see cref="MonomorphizedBody.Info"/> is itself concrete (owner not a generic
/// definition, routine not a generic definition, no residual generic parameters in its signature)
/// are checked. Generic-definition BuilderQuery bodies (emitted deliberately with an empty
/// substitution map by <c>EmitGenericDefBuilderQueryBodies</c>) are exempt: codegen substitutes
/// their single type parameter per concrete owner and they never reach the concrete-only guards.
/// </para>
/// </summary>
internal static class MonomorphizationCompletenessAssertionPass
{
    public static void Run(IReadOnlyDictionary<string, MonomorphizedBody> bodies)
    {
        foreach ((string key, MonomorphizedBody body) in bodies)
        {
            if (!IsConcreteInfo(info: body.Info))
            {
                continue;
            }

            if (body.Ast?.Body == null)
            {
                continue;
            }

            CheckBody(key: key, body: body);
        }
    }

    /// <summary>
    /// A body is subject to the assertion only when its target routine is fully concrete: a
    /// non-generic routine on a non-generic-definition owner, with no generic parameter surviving
    /// in its parameter or return types.
    /// </summary>
    private static bool IsConcreteInfo(RoutineInfo info)
    {
        if (info.IsGenericDefinition)
        {
            return false;
        }

        if (info.OwnerType is { IsGenericDefinition: true })
        {
            return false;
        }

        if (info.OwnerType != null && ContainsGenericParameter(type: info.OwnerType))
        {
            return false;
        }

        if (info.ReturnType != null && ContainsGenericParameter(type: info.ReturnType))
        {
            return false;
        }

        return info.Parameters.All(predicate: p =>
            p.Type == null || !ContainsGenericParameter(type: p.Type));
    }

    private static void CheckBody(string key, MonomorphizedBody body)
    {
        string routine = body.Info.FullName;
        AstWalker.Walk(root: body.Ast!.Body,
            visit: node =>
            {
                switch (node)
                {
                    case Expression expr:
                        CheckExpression(routine: routine, key: key, expr: expr);
                        break;
                    case Parameter { Type.ResolvedType: { } pType } param:
                        AssertConcrete(type: pType,
                            routine: routine,
                            key: key,
                            where: $"parameter '{param.Name}' type");
                        break;
                }
            });
        if (body.Ast.ReturnType?.ResolvedType is { } retType)
        {
            AssertConcrete(type: retType,
                routine: routine,
                key: key,
                where: "return type");
        }
    }

    private static void CheckExpression(string routine, string key, Expression expr)
    {
        if (expr.ResolvedType is { } rt)
        {
            AssertConcrete(type: rt,
                routine: routine,
                key: key,
                where: $"{expr.GetType().Name}.ResolvedType");
        }

        RoutineInfo? callee = expr switch
        {
            CallExpression { ResolvedRoutine: { } cr } => cr,
            GenericMemberRoutineCallExpression { ResolvedRoutine: { } gr } => gr,
            CreatorExpression { ResolvedCreatorRoutine: { } ccr } => ccr,
            _ => null
        };
        if (callee is { OwnerType.IsGenericDefinition: true })
        {
            throw Fail(routine: routine,
                key: key,
                where:
                $"{expr.GetType().Name} callee '{callee.FullName}' bound to generic-definition owner");
        }

        if (callee != null && callee.IsGenericDefinition)
        {
            throw Fail(routine: routine,
                key: key,
                where:
                $"{expr.GetType().Name} callee '{callee.FullName}' is a generic definition");
        }
    }

    private static void AssertConcrete(TypeInfo type, string routine, string key,
        string where)
    {
        if (ContainsGenericParameter(type: type))
        {
            throw Fail(routine: routine,
                key: key,
                where: $"{where} = '{type.FullName}' contains an unsubstituted generic parameter");
        }
    }

    private static InvalidOperationException Fail(string routine, string key, string where)
    {
        return new InvalidOperationException(
            message:
            $"[Track-C/C1] Monomorphization incomplete: {where}, in body of '{routine}' (key '{key}'). " +
            "GenericAstRewriter must substitute every position to a concrete type/literal before codegen. " +
            "Fix the rewriter — do not re-enable codegen-time substitution.");
    }

    /// <summary>
    /// True when the type IS a residual generic parameter (<c>T</c>), or nests one at any depth in
    /// its type arguments. A bare generic DEFINITION used as a type label (<c>Maybe</c> with no
    /// arguments) is deliberately NOT flagged: it is not a substitutable parameter — codegen never
    /// monomorphizes it — and pre-existing type-label imprecision on some identifier nodes tags such
    /// bare defs. C1 targets unsubstituted <see cref="GenericParameterTypeInfo"/> and generic-def
    /// CALLEE owners (checked separately), not incompletely-labelled leaves.
    /// </summary>
    private static bool ContainsGenericParameter(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo)
        {
            return true;
        }

        // RoutineTypeInfo (`Routine[(T,), U]`) and TupleTypeInfo (`Tuple[T, Bool]`) carry their
        // component types in dedicated slots, NOT TypeArguments, so the recursion below would miss a
        // residual param nested inside them. A chained iterator emitter stores its projection as
        // `secret transform: Routine[(T,), U]`; `me.transform(item)`'s indirect return type is read
        // straight off that RoutineTypeInfo at codegen — an unsubstituted `U` there would slip past
        // C1 and only surface as a GetLlvmType crash. Recurse both slot kinds explicitly.
        if (type is RoutineTypeInfo rt)
        {
            return rt.ParameterTypes.Any(predicate: p => ContainsGenericParameter(type: p)) ||
                   rt.ReturnType != null && ContainsGenericParameter(type: rt.ReturnType);
        }

        if (type is TupleTypeInfo tuple)
        {
            return tuple.ElementTypes.Any(predicate: e => ContainsGenericParameter(type: e));
        }

        return type.TypeArguments is { Count: > 0 } args &&
               args.Any(predicate: a => ContainsGenericParameter(type: a));
    }
}
