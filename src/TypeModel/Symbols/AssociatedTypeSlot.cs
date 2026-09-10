using TypeModel.Types;

namespace TypeModel.Symbols;

/// <summary>
/// An associated-type slot declared on a protocol via <c>relates Name obeys Constraint</c>.
/// Implementers bind the slot to a concrete type (stored as a binding on the implementer's
/// <see cref="EntityTypeSymbol"/>/<see cref="RecordTypeSymbol"/>).
/// </summary>
public sealed class AssociatedTypeSlot
{
    /// <summary>The slot name (e.g. <c>Iter</c>).</summary>
    public string Name { get; }

    /// <summary>The protocol bound the binding must obey (e.g. <c>Iterator[T]</c>), if any.</summary>
    public TypeSymbol? Constraint { get; init; }

    /// <summary>Initializes a new associated-type slot with the given name.</summary>
    /// <param name="name">The slot name.</param>
    public AssociatedTypeSlot(string name)
    {
        Name = name;
    }
}
