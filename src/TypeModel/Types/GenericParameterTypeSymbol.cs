using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Represents a generic type parameter (like T in <c>List[T]</c>).
/// This is an unbound placeholder that gets replaced during generic resolution.
/// </summary>
public sealed class GenericParameterTypeSymbol : TypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.TypeParameter;

    /// <summary>
    /// When this param was produced by <c>WrapperForwardingPass</c>'s inner-T-rename
    /// (an inner type's generic param name collides with the wrapper's own param name),
    /// holds the original inner-param name (the substitution lookup key in monomorphization).
    /// Null for normal (non-renamed) generic params.
    /// </summary>
    /// <remarks>
    /// Substitution sites should look up this property first (when non-null) before falling
    /// back to <see cref="TypeSymbol.Name"/>. This replaces the older string-prefix sentinel
    /// (e.g. <c>__rfwd_T__</c>) with a structural marker that doesn't require lexical
    /// awareness in every substitution path.
    /// </remarks>
    public string? ForwarderOriginalName { get; init; }

    /// <summary>
    /// Positional slot of this parameter in its declaring scope's parameter list (0-based): the
    /// <c>0</c>th, <c>1</c>st, … hole of the template. This — NOT <see cref="TypeSymbol.Name"/> — is a
    /// parameter's true identity: the source name is a human label, so renaming it must be a no-op,
    /// while the slot is what a concrete type argument binds to at instantiation. <c>-1</c> when the
    /// slot is unknown (a placeholder minted outside a resolution scope, e.g. a self-application).
    /// </summary>
    /// <remarks>
    /// Carrying the slot makes the "name is a label, slot is identity" principle structural rather
    /// than encoded in a collision-proof name string. It lets resolution treat a bare name that
    /// matches an in-scope parameter as that parameter regardless of any same-named global type.
    /// </remarks>
    public int Slot { get; init; } = -1;

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericParameterTypeSymbol"/> class.
    /// </summary>
    /// <param name="name">The name of the generic type parameter (a source label; see <see cref="Slot"/>).</param>
    public GenericParameterTypeSymbol(string name) : base(name: name)
    {
    }

    /// <summary>
    /// Initializes a generic parameter carrying its positional <paramref name="slot"/> identity.
    /// </summary>
    /// <param name="name">The source label of the parameter.</param>
    /// <param name="slot">The 0-based positional slot in the declaring scope (see <see cref="Slot"/>).</param>
    public GenericParameterTypeSymbol(string name, int slot) : base(name: name)
    {
        Slot = slot;
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as generic parameters cannot be resolved.</exception>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        throw new InvalidOperationException(message: "Cannot resolve a generic type parameter.");
    }
}
