using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for choices (simple enumerations with optional integer values).
/// Backed by <c>i32</c> at the LLVM level. Choices CAN have memberRoutines, unlike variants.
/// Cases use SCREAMING_SNAKE_CASE.
/// </summary>
public sealed class ChoiceTypeInfo : RecordTypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Choice;

    /// <summary>The cases of this choice type.</summary>
    public List<ChoiceCaseInfo> Cases { get; init; } = [];

    // ImplementedProtocols is inherited from RecordTypeInfo (no shadowing).

    /// <summary>Whether all cases have explicit values.</summary>
    public bool HasExplicitValues => Cases.All(predicate: c => c.Value.HasValue);

    /// <summary>The underlying integer type for this choice. Defaults to S32.</summary>
    public TypeInfo? UnderlyingType { get; init; }

    /// <summary>Creates a new choice type with the given name and default i32 backend type.</summary>
    public ChoiceTypeInfo(string name) : base(name: name)
    {
        BackendType = "i32";
    }


    /// <inheritdoc/>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Choice type '{Name}' cannot be resolved with type arguments.");
    }
}
