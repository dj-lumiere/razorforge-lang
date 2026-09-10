using Compiler.Declaration;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Collection;

/// <summary>
/// Pre-synthesis liveness pass: computes the set of concrete generic type instantiations
/// that are actually reachable from user-visible routine signatures and their member-variable
/// chains. Called after all Phase 4 body analysis (including wrapper forwarder synthesis)
/// but before Phase 6 global synthesis (WiredRoutinePass, ErrorHandlingVariantPass).
///
/// After this pass runs, TypeRegistry.AllConcreteGenericInstances and GetAllRoutines()
/// both filter against the computed live set, so WiredRoutinePass and GMP only ever
/// see types that are actually needed by the program.
/// </summary>
internal sealed class TypeLivenessPass(TypeRegistry registry)
{
    private readonly HashSet<string> _live = new(comparer: StringComparer.Ordinal);
    private readonly Queue<TypeInfo> _worklist = new();

    public void Run()
    {
        // Seed 1: all non-generic base types — always live.
        foreach (TypeInfo t in registry.GetAllTypes())
        {
            if (t.TypeArguments == null || t.TypeArguments.Count == 0)
            {
                Enqueue(type: t);
            }
        }

        // Seed 3: return types and parameter types of all non-synthesized routines.
        // Owner types are intentionally excluded from the seed — phantom generic instances
        // appear as routine owners but never as explicit parameters or return values, so
        // excluding them from the seed prevents them from reaching synthesis.
        foreach (RoutineInfo routine in registry.GetAllRoutines())
        {
            if (routine.IsSynthesized)
            {
                continue;
            }

            if (routine.ReturnType != null)
            {
                Enqueue(type: routine.ReturnType);
            }

            foreach (ParameterInfo param in routine.Parameters)
            {
                Enqueue(type: param.Type);
            }
        }

        // First transitive closure: follow type arguments, member variables, wrapper inner types.
        DrainWorklist();

        // Seed 2 (deferred): concrete wrapper instances whose inner type is already live.
        // Deferring prevents phantom wrappers (e.g. BTreeSetNode[Bytes] created
        // as a SA side-effect of Bytes.split -> List[Bytes] -> List.create(from: SortedSet[T]))
        // from being seeded just because they exist in the registry.
        foreach (WrapperTypeInfo w in registry.AllConcreteWrapperInstances)
        {
            if (w.InnerType == null || _live.Contains(item: w.InnerType.FullName))
            {
                Enqueue(type: w);
            }
        }

        // Second transitive closure: extend from newly seeded wrapper instances.
        DrainWorklist();

        registry.SetLiveConcreteTypes(liveTypes: _live);
    }

    private void DrainWorklist()
    {
        while (_worklist.Count > 0)
        {
            EnqueueReachableFrom(t: _worklist.Dequeue());
        }
    }

    /// <summary>
    /// Enqueues every type directly reachable from <paramref name="t"/>: its type arguments and,
    /// per kind, its member-variable types (record/entity) or wrapper inner type.
    /// </summary>
    private void EnqueueReachableFrom(TypeInfo t)
    {
        if (t.TypeArguments != null)
        {
            foreach (TypeInfo arg in t.TypeArguments)
            {
                Enqueue(type: arg);
            }
        }

        switch (t)
        {
            case RecordTypeInfo record:
                foreach (MemberVariableInfo mv in record.MemberVariables)
                {
                    Enqueue(type: mv.Type);
                }

                break;
            case EntityTypeInfo entity:
                foreach (MemberVariableInfo mv in entity.MemberVariables)
                {
                    Enqueue(type: mv.Type);
                }

                break;
            case WrapperTypeInfo wrapper:
                if (wrapper.InnerType != null)
                {
                    Enqueue(type: wrapper.InnerType);
                }

                break;
        }
    }

    private void Enqueue(TypeInfo type)
    {
        if (type is GenericParameterTypeInfo or ErrorTypeInfo)
        {
            return;
        }

        if (!_live.Add(item: type.FullName))
        {
            return;
        }

        _worklist.Enqueue(item: type);
    }
}
