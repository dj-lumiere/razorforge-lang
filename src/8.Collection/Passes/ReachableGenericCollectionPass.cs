using TypeModel.Types;
using Compiler.Instantiation;
using TypeModel.Symbols;

namespace Compiler.Collection.Passes;

/// <summary>
/// Phase 7 collection pass: snapshot the concrete generic instances already discovered by
/// semantic analysis and post-verification lowering so closure can materialize bodies for them.
/// </summary>
internal sealed class ReachableGenericCollectionPass(InstantiationContext ctx)
{
    public void Run()
    {
        foreach (TypeInfo concreteType in ctx.Registry.AllConcreteGenericInstances)
        {
            ctx.ReachableGenericTypes.Add(item: concreteType.FullName);

            TypeInfo genericDefinition = concreteType switch
            {
                RecordTypeInfo { GenericDefinition: { } definition } => definition,
                EntityTypeInfo { GenericDefinition: { } definition } => definition,
                _ => concreteType
            };

            foreach (RoutineInfo memberRoutine in ctx.Registry.GetMemberRoutinesForType(
                         type: genericDefinition))
            {
                ctx.ReachableGenericRoutines.Add(
                    item: $"{concreteType.FullName}.{memberRoutine.Name}");
            }
        }
    }
}
