using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Singleton error type used when type resolution fails.
/// This is a builder-internal sentinel, not a real user-visible type.
/// </summary>
public sealed class ErrorTypeSymbol : TypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Error;

    /// <summary>
    /// Singleton instance of the error type.
    /// </summary>
    public static readonly ErrorTypeSymbol Instance = new();

    private ErrorTypeSymbol() : base(name: "<error>")
    {
    }

    /// <inheritdoc/>
    /// <returns>Always returns this instance.</returns>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        return this;
    }
}
