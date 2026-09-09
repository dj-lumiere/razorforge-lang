using SyntaxTree;

namespace Compiler.Declaration;

/// <summary>
/// Desugars a homogeneous variadic parameter <c>nums...: T</c> into a const-generic
/// <c>Array[T, __VarargN]</c> parameter plus an implicit <c>[__VarargN]</c> generic and a
/// <c>needs __VarargN is U64</c> constraint. This turns a variadic routine into an ordinary
/// const-generic template: a call site with <c>K</c> trailing arguments packs them into an
/// <c>Array[T, K]</c> literal (see the call-site lowering) and binds <c>__VarargN = K</c>, so the
/// existing const-generic inference / monomorphization / codegen path produces one specialized body
/// per arity — pure per-arity monomorphization, no dedicated variadic ABI.
///
/// This only models the HOMOGENEOUS, build-time-count case (every element type <c>T</c>, count known
/// at the call site) — exactly what collection literals (<c>[a, b, c]</c> → <c>List[T].create</c>)
/// need. Heterogeneous variadics (<c>show(a, b, c)</c> with mixed types) are NOT handled here.
///
/// The rewrite mutates the <see cref="RoutineDeclaration"/> AST node in place. That same node is the
/// one both registration paths (<c>StdlibLoader</c> for stdlib, <c>SignatureResolver</c> for user code)
/// and the monomorphization index (<c>GenericMonomorphizationPass._routineIndex</c>, built from
/// <c>program.Declarations</c>) read later, so a single mutation propagates everywhere. Idempotent: a
/// parameter already rewritten to <c>Array[_, __Vararg…]</c> is left untouched, so calling this from
/// both registration entry points is safe.
/// </summary>
public static class VariadicParamDesugar
{
    private const string VarargGenericPrefix = "__Vararg";

    /// <summary>
    /// Applies the variadic → const-generic-Array rewrite to every variadic parameter of the routine.
    /// No-op when the routine has no variadic parameter or all of them are already desugared.
    /// </summary>
    public static void Apply(RoutineDeclaration routine)
    {
        // Fast path: nothing to do unless there is at least one not-yet-desugared variadic param.
        bool anyPending = routine.Parameters.Any(predicate: p =>
            p.IsVariadic && p.Type != null && !IsAlreadyDesugared(paramType: p.Type));
        if (!anyPending)
        {
            return;
        }

        List<string> generics = routine.GenericParameters?.ToList() ?? [];
        List<GenericConstraintDeclaration>
            constraints = routine.GenericConstraints?.ToList() ?? [];

        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            Parameter param = routine.Parameters[index: i];
            if (!param.IsVariadic || param.Type == null ||
                IsAlreadyDesugared(paramType: param.Type))
            {
                continue;
            }

            routine.Parameters[index: i] = DesugarVariadicParam(param: param,
                generics: generics,
                constraints: constraints);
        }

        routine.GenericParameters = generics;
        routine.GenericConstraints = constraints;
    }

    /// <summary>
    /// Rewrites a single variadic parameter <c>nums: T</c> into the const-generic form
    /// <c>nums: Array[T, __VarargN]</c>, appending the fresh arity generic and its
    /// <c>needs __VarargN is U64</c> constraint to the supplied lists. Returns the rewritten parameter.
    /// </summary>
    private static Parameter DesugarVariadicParam(Parameter param, List<string> generics,
        List<GenericConstraintDeclaration> constraints)
    {
        string arityName = FreshArityName(existing: generics);
        generics.Add(item: arityName);
        constraints.Add(item: new GenericConstraintDeclaration(ParameterName: arityName,
            ConstraintType: ConstraintKind.ConstGeneric,
            ConstraintTypes:
            [new TypeExpression(Name: "U64", GenericArguments: null, Location: param.Location)],
            Location: param.Location));

        // nums: T   ->   nums: Array[T, __VarargN]. Array is a `module Core` primitive (auto-imported),
        // so a bare reference resolves via the import-gated Core prefix in every file.
        var arrayType = new TypeExpression(Name: "Array",
            GenericArguments:
            [
                param.Type!,
                new TypeExpression(Name: arityName,
                    GenericArguments: null,
                    Location: param.Type!.Location)
            ],
            Location: param.Type.Location);

        // Keep IsVariadic = true as the marker that the call site must pack trailing args into
        // the Array[T, K] literal; the param's TYPE is now the Array template.
        return param with { Type = arrayType };
    }

    /// <summary>A variadic param is already desugared when its type is <c>Array[_, __Vararg…]</c>.</summary>
    private static bool IsAlreadyDesugared(TypeExpression paramType)
    {
        return paramType is { Name: "Array", GenericArguments: [_, { Name: var n }] } &&
               n.StartsWith(value: VarargGenericPrefix);
    }

    private static string FreshArityName(List<string> existing)
    {
        int counter = 0;
        string candidate = $"{VarargGenericPrefix}{counter}";
        while (existing.Contains(item: candidate))
        {
            counter++;
            candidate = $"{VarargGenericPrefix}{counter}";
        }

        return candidate;
    }
}
