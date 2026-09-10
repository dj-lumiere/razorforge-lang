using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Placeholder type used for unbound generic type parameters.
/// </summary>
public sealed class TypeParameterPlaceholder : TypeSymbol
{
    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as type parameters are not concrete types.</exception>
    public override TypeCategory Category => throw new InvalidOperationException(
        message: $"Type parameter '{Name}' is not a concrete type.");

    /// <summary>
    /// Initializes a new instance of the <see cref="TypeParameterPlaceholder"/> class.
    /// </summary>
    /// <param name="name">The name of the type parameter.</param>
    public TypeParameterPlaceholder(string name) : base(name: name)
    {
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Always thrown as type parameters cannot be resolved.</exception>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Type parameter '{Name}' cannot be resolved.");
    }
}
