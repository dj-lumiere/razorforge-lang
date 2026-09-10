using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for flags (bitmask types with named members).
/// Backed by <c>i64</c> at the LLVM level. Max 64 members, auto-assigned power-of-two bit positions.
/// Only builder-generated operators allowed.
/// </summary>
public sealed class FlagsTypeSymbol : RecordTypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Flags;

    /// <summary>The members of this flags type.</summary>
    public List<FlagsMemberInfo> Members { get; init; } = [];

    // ImplementedProtocols is inherited from RecordTypeSymbol (no shadowing).

    /// <summary>Creates a new flags type with the given name and default i64 backend type.</summary>
    public FlagsTypeSymbol(string name) : base(name: name)
    {
        BackendType = "i64";
    }


    /// <inheritdoc/>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Flags type '{Name}' cannot be resolved with type arguments.");
    }
}

/// <summary>A single flags member.</summary>
/// <param name="Name">The name (SCREAMING_SNAKE_CASE).</param>
/// <param name="BitPosition">The bit position (0-63). Bitmask = 1UL &lt;&lt; BitPosition.</param>
public sealed record FlagsMemberInfo(string Name, int BitPosition);
