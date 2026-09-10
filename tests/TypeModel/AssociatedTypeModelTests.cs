using TypeModel.Symbols;
using TypeModel.Types;

namespace RazorForge.Tests.TypeModel;

/// <summary>
/// Type-model tests for associated-type storage (bucket 2): verifies that generic
/// monomorphization (<c>CreateInstance</c>) carries protocol slot declarations and implementer
/// bindings through type-argument substitution.
/// </summary>
public class AssociatedTypeModelTests
{
    /// <summary>
    /// An entity's <c>relates … as Iter</c> binding, when generic over <c>T</c>, must substitute
    /// <c>T</c> in the bound type when the entity is monomorphized.
    /// </summary>
    [Fact]
    public void Entity_CreateInstance_SubstitutesAssociatedTypeBinding()
    {
        var concrete = new RecordTypeSymbol(name: "S64");
        var paramT = new GenericParameterTypeSymbol(name: "T");

        var def = new EntityTypeSymbol(name: "Box")
        {
            GenericParameters = ["T"],
            // relates SomeEmitter[T] as Iter  →  bind Iter to a type that mentions T.
            AssociatedTypeBindings = new Dictionary<string, TypeSymbol> { [key: "Iter"] = paramT }
        };

        var instance = (EntityTypeSymbol)def.CreateInstance(typeArguments: [concrete]);

        Assert.True(condition: instance.AssociatedTypeBindings.ContainsKey(key: "Iter"));
        // T was substituted with the concrete argument.
        Assert.Same(expected: concrete, actual: instance.AssociatedTypeBindings[key: "Iter"]);
    }

    /// <summary>
    /// A protocol's <c>relates Iter obeys …</c> slot must carry through monomorphization with its
    /// constraint substituted.
    /// </summary>
    [Fact]
    public void Protocol_CreateInstance_CarriesAssociatedTypeSlot()
    {
        var concrete = new RecordTypeSymbol(name: "Text");
        var paramT = new GenericParameterTypeSymbol(name: "T");

        var def = new ProtocolTypeSymbol(name: "Sequence")
        {
            GenericParameters = ["T"],
            AssociatedTypes = [new AssociatedTypeSlot(name: "Iter") { Constraint = paramT }]
        };

        var instance = (ProtocolTypeSymbol)def.CreateInstance(typeArguments: [concrete]);

        Assert.Single(collection: instance.AssociatedTypes);
        Assert.Equal(expected: "Iter", actual: instance.AssociatedTypes[index: 0].Name);
        Assert.Same(expected: concrete, actual: instance.AssociatedTypes[index: 0].Constraint);
    }

    /// <summary>
    /// A projection <c>S/Iter</c> records its base and slot name.
    /// </summary>
    [Fact]
    public void Projection_RecordsBaseAndSlot()
    {
        var paramS = new GenericParameterTypeSymbol(name: "S");
        var projection = new AssociatedProjectionTypeSymbol(baseType: paramS, slotName: "Iter");

        Assert.Same(expected: paramS, actual: projection.Base);
        Assert.Equal(expected: "Iter", actual: projection.SlotName);
        Assert.Equal(expected: "S/Iter", actual: projection.Name);
    }

    /// <summary>
    /// Bucket 3 core: substituting <c>S/Iter</c> where <c>S</c> maps to a concrete type that binds
    /// <c>Iter</c> resolves the projection to the bound type.
    /// </summary>
    [Fact]
    public void SubstituteType_ResolvesProjection_WhenBaseBecomesConcreteWithBinding()
    {
        var emitter = new RecordTypeSymbol(name: "ListEmitter");
        var concreteList = new EntityTypeSymbol(name: "List")
        {
            AssociatedTypeBindings =
                new Dictionary<string, TypeSymbol> { [key: "Iter"] = emitter }
        };
        var projection = new AssociatedProjectionTypeSymbol(
            baseType: new GenericParameterTypeSymbol(name: "S"),
            slotName: "Iter");

        var subs = new Dictionary<string, TypeSymbol> { [key: "S"] = concreteList };
        TypeSymbol result = RecordTypeSymbol.SubstituteType(type: projection, substitution: subs);

        Assert.Same(expected: emitter, actual: result);
    }

    /// <summary>
    /// When the base of a projection is still generic after substitution, the projection is kept
    /// (deferred) rather than wrongly resolved.
    /// </summary>
    [Fact]
    public void SubstituteType_KeepsProjection_WhenBaseStillGeneric()
    {
        var projection = new AssociatedProjectionTypeSymbol(
            baseType: new GenericParameterTypeSymbol(name: "S"),
            slotName: "Iter");

        var subs = new Dictionary<string, TypeSymbol>(); // no binding for S
        TypeSymbol result = RecordTypeSymbol.SubstituteType(type: projection, substitution: subs);

        Assert.IsType<AssociatedProjectionTypeSymbol>(@object: result);
    }
}
