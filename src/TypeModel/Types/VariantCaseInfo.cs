using SyntaxTree;

namespace TypeModel.Types;

/// <summary>
/// Information about a single member in a type-based variant.
/// Members are either real types (S64, Text, etc.) or the None state (zero-sized, no payload).
/// </summary>
// Note: None is a no-payload state; type=null currently encodes that, but ideally None should be a
// distinct explicit payload state rather than a null sentinel (future cleanup).
public sealed class VariantMemberInfo
{
    /// <summary>The member type, or null for the None state.</summary>
    public TypeSymbol? Type { get; }

    /// <summary>Whether this member is the None state.</summary>
    public bool IsNone => Type == null;

    /// <summary>Display name: the type name, or "None" for the None state.</summary>
    public string Name => Type?.Name ?? "None";

    /// <summary>Zero-based declaration ordinal of this member (None first when present). This is a
    /// reflection/<c>branchof</c> index only — it is NOT the runtime discriminant. Codegen stores the
    /// arm's FNV-1a <c>type_id</c> (<c>ComputeTypeId(Type.FullName)</c>, 0 for None) in the variant's
    /// tag field, and PatternLoweringPass matches on that type_id, so nothing compares this ordinal at
    /// runtime.</summary>
    public int Ordinal { get; init; }

    /// <summary>Source location where this member is defined.</summary>
    public SourceLocation? Location { get; init; }

    /// <summary>
    /// Creates a variant member for a real type.
    /// </summary>
    public VariantMemberInfo(TypeSymbol type)
    {
        Type = type;
    }

    /// <summary>
    /// Creates the None state member (zero-sized, no payload).
    /// </summary>
    private VariantMemberInfo()
    {
        Type = null;
    }

    /// <summary>
    /// Creates a None state member with the specified location and tag.
    /// </summary>
    public static VariantMemberInfo CreateNone(int ordinal, SourceLocation? location = null)
    {
        return new VariantMemberInfo { Ordinal = ordinal, Location = location };
    }

    /// <summary>
    /// Creates a copy with substituted type for generic resolution.
    /// </summary>
    public VariantMemberInfo WithSubstitutedType(TypeSymbol newType)
    {
        return new VariantMemberInfo(type: newType) { Ordinal = Ordinal, Location = Location };
    }
}
