using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// A deferred associated-type projection such as <c>S/Iter</c> — "the <c>Iter</c> of type
/// <see cref="Base"/>". Created during resolution when the base is not yet concrete (e.g. a
/// generic parameter); monomorphization later substitutes <see cref="Base"/> with a concrete
/// type and resolves the projection to that type's associated-type binding.
///
/// Reuses <see cref="TypeCategory.TypeParameter"/> (it is an unresolved/deferred type, like a
/// generic parameter) so existing category-based switches treat it as "not yet concrete" without
/// needing a new category. Substitution logic recognizes it by C# type, not by category.
/// </summary>
public sealed class AssociatedProjectionTypeSymbol : TypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.TypeParameter;

    /// <summary>The type being projected from (e.g. the generic parameter <c>S</c>).</summary>
    public TypeSymbol Base { get; }

    /// <summary>The associated-type slot name being projected (e.g. <c>Iter</c>).</summary>
    public string SlotName { get; }

    /// <summary>Initializes a new projection <c>Base/SlotName</c>.</summary>
    /// <param name="baseType">The type being projected from.</param>
    /// <param name="slotName">The associated-type slot name.</param>
    public AssociatedProjectionTypeSymbol(TypeSymbol baseType, string slotName) : base(
        name: $"{baseType.Name}/{slotName}")
    {
        Base = baseType;
        SlotName = slotName;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always — a projection is resolved by
    /// substitution during monomorphization, not by direct instantiation.</exception>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Cannot directly instantiate an associated-type projection '{Name}'.");
    }
}
