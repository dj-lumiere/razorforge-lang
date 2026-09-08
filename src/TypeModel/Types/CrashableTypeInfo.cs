using System.Text;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for crashable types — throwable error entities.
/// Always heap-allocated (entity semantics). Automatically conforms to the Crashable protocol.
/// Must provide crash_message() -> Text; crash_title() is synthesized from the type name.
/// </summary>
// Crashable is an entity (reference type, heap-allocated) — extends EntityTypeInfo (shared members).
public sealed class CrashableTypeInfo : EntityTypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Crashable;

    /// <summary>
    /// The synthesized crash title (sentence-cased type name, e.g. "Network error" for NetworkError).
    /// Computed once and stored here for codegen use.
    /// </summary>
    public string CrashTitle { get; init; }

    /// <summary>
    /// Initializes a new instance of <see cref="CrashableTypeInfo"/>.
    /// </summary>
    /// <param name="name">The type name.</param>
    public CrashableTypeInfo(string name) : base(name: name)
    {
        CrashTitle = SynthesizeCrashTitle(typeName: name);
    }

    /// <inheritdoc/>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        throw new InvalidOperationException(
            message: $"Crashable type '{Name}' cannot be resolved with type arguments.");
    }

    /// <summary>
    /// Converts a CamelCase type name to a sentence-cased crash title.
    /// Examples: NetworkError -> "Network error", VerificationFailedError -> "Verification failed error"
    /// </summary>
    public static string SynthesizeCrashTitle(string typeName)
    {
        if (string.IsNullOrEmpty(value: typeName))
            return typeName;

        var sb = new StringBuilder();
        for (int i = 0; i < typeName.Length; i++)
        {
            if (i > 0 && char.IsUpper(c: typeName[index: i]))
                sb.Append(value: ' ');
            sb.Append(value: typeName[index: i]);
        }

        string spaced = sb.ToString().ToLowerInvariant();
        return char.ToUpperInvariant(c: spaced[0]) + spaced[1..];
    }
}
