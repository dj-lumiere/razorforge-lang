using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Singleton error type used when type resolution fails.
/// This is a builder-internal sentinel, not a real user-visible type.
/// </summary>
public sealed class ErrorTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Error;

    /// <summary>
    /// Singleton instance of the error type.
    /// </summary>
    public static readonly ErrorTypeInfo Instance = new();

    private ErrorTypeInfo() : base(name: "<error>")
    {
    }

    /// <inheritdoc/>
    /// <returns>Always returns this instance.</returns>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        return this;
    }
}
