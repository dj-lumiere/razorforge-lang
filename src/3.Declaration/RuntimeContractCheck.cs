using SyntaxTree;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// The <c>validate-stdlib</c> resolution check for <see cref="RuntimeContract"/> (Design 1 step 2 of
/// the compiler↔stdlib name-contract work). Turns a SILENT rename miscompile into a LOUD build
/// failure: for every name the compiler hard-codes against the stdlib, assert it still resolves.
///
/// <para>A rename like <c>extract</c>/<c>inject</c> → <c>peek</c>/<c>poke</c> (commit 1480acd) used to
/// compile clean and break at runtime, because the compiler looks these up by literal. With this
/// check wired into the CI-gated <c>validate-stdlib</c> verb, renaming a contract routine/type/field
/// without updating <see cref="RuntimeContract"/> fails immediately, naming the exact broken contract.</para>
///
/// <para>Scope mirrors <see cref="RuntimeContract"/>: it checks the declared-in-stdlib routine names
/// (<see cref="RuntimeContract.StdlibRoutineContracts"/>), the wrapper TYPE names
/// (<see cref="RuntimeContract.WrapperTypes"/>), and the <c>Maybe</c> carrier fields. It deliberately
/// does NOT check compiler-generated / intrinsic names (<c>try_emit</c>, <c>refer</c>/<c>control</c>,
/// BuilderQuery/<c>data_size</c>) or the native <c>rf_*</c> externs (link-checked C-ABI).</para>
/// </summary>
public static class RuntimeContractCheck
{
    /// <summary>The record type that carries the <c>present</c>/<c>value</c> optional fields.</summary>
    private const string CarrierTypeName = "Maybe";

    /// <summary>Runs the contract-resolution check against a fully-loaded stdlib registry. Returns a
    /// human-readable description for each broken contract; an empty list means every contract holds.</summary>
    public static List<string> Check(TypeRegistry registry)
    {
        var errors = new List<string>();

        // 1. Every bare routine name declared anywhere in the stdlib ASTs. Using the declaration
        //    ground truth (not the liveness-filtered GetAllRoutines) so the check is independent of
        //    which routines a user program happens to reach — validate-stdlib has no user program.
        HashSet<string> declaredRoutines = CollectDeclaredRoutineNames(registry: registry);

        // 2. Routine-name contracts must each resolve to a declared stdlib routine.
        CheckRoutineContracts(declaredRoutines: declaredRoutines, errors: errors);

        // 3. Wrapper / marker-protocol TYPE-name contracts must each resolve to a registered type.
        CheckTypeContracts(registry: registry, errors: errors);

        // 4. Carrier-field contracts must exist as member variables on the Maybe record.
        CheckCarrierFieldContracts(registry: registry, errors: errors);

        return errors;
    }

    /// <summary>
    /// Collects every bare routine name declared across all stdlib ASTs (declaration ground truth,
    /// not the liveness-filtered routine set).
    /// </summary>
    private static HashSet<string> CollectDeclaredRoutineNames(TypeRegistry registry)
    {
        var declaredRoutines = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach ((Program program, _, _) in registry.StdlibPrograms)
        {
            AstWalker.Walk(root: program,
                visit: node =>
                {
                    switch (node)
                    {
                        // .Name is the canonical bare member identifier (parser stores owner in the
                        // structured OwnerName/RenderedReceiver fields; `!` is IsFailable) — matches the
                        // bare routine-name contracts directly, no owner/generic-suffix splitting needed.
                        case RoutineDeclaration d: declaredRoutines.Add(item: d.Name); break;
                        case RoutineSignature s: declaredRoutines.Add(item: s.Name); break;
                    }
                });
        }

        return declaredRoutines;
    }

    /// <summary>
    /// Asserts every routine-name contract resolves to a declared stdlib routine, appending a
    /// description for each broken one.
    /// </summary>
    private static void CheckRoutineContracts(HashSet<string> declaredRoutines,
        List<string> errors)
    {
        errors.AddRange(collection: RuntimeContract.StdlibRoutineContracts
                                                   .Where(predicate: name =>
                                                        !declaredRoutines.Contains(item: name))
                                                   .Select(selector: name =>
                                                        $"routine contract '{name}' resolves to NO declared stdlib routine " +
                                                        "(renamed in stdlib without updating RuntimeContract?)"));
    }

    /// <summary>
    /// Asserts every wrapper / marker-protocol type-name contract resolves to a registered type.
    /// </summary>
    private static void CheckTypeContracts(TypeRegistry registry, List<string> errors)
    {
        errors.AddRange(collection: RuntimeContract.WrapperTypes
                                                   .Concat(second: RuntimeContract
                                                       .StdlibTypeContracts)
                                                   .Where(predicate: typeName =>
                                                        registry.LookupType(
                                                            name: typeName) is null)
                                                   .Select(selector: typeName =>
                                                        $"type contract '{typeName}' resolves to NO registered type"));
    }

    /// <summary>
    /// Asserts the <c>present</c>/<c>value</c> carrier fields exist as member variables on the Maybe
    /// record.
    /// </summary>
    private static void CheckCarrierFieldContracts(TypeRegistry registry, List<string> errors)
    {
        TypeSymbol? carrier = registry.LookupType(name: CarrierTypeName);
        if (carrier is null)
        {
            errors.Add(item: $"carrier type '{CarrierTypeName}' is not registered " +
                             "(cannot verify the present/value field contracts)");
            return;
        }

        HashSet<string> fields = MemberVariableNames(type: carrier);
        errors.AddRange(collection: new[]
            {
                RuntimeContract.Carrier.PresentField,
                RuntimeContract.Carrier.ValueField
            }.Where(predicate: field => !fields.Contains(item: field))
             .Select(selector: field =>
                  $"carrier-field contract '{CarrierTypeName}.{field}' resolves to NO member variable"));
    }

    private static HashSet<string> MemberVariableNames(TypeSymbol type)
    {
        IEnumerable<string> names = type switch
        {
            RecordTypeSymbol r => r.MemberVariables.Select(selector: m => m.Name),
            EntityTypeSymbol e => e.MemberVariables.Select(selector: m => m.Name),
            _ => []
        };
        return new HashSet<string>(collection: names, comparer: StringComparer.Ordinal);
    }
}
