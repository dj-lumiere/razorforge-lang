using System.Linq;
using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Type information for variants (type-based tagged unions).
/// Variants are local-only and unmodifiable with no memberRoutines.
/// Members are types — the type IS the tag. No named cases.
/// </summary>
// Variant is a value type ({ i64 tag, [payload] } aggregate, like a record) → extends RecordTypeSymbol,
// sharing MemberVariables / ImplementedProtocols / AssociatedTypeBindings / GenericDefinition. Variant
// arm data lives in Members. NOTE: any codegen/lifecycle switch that handles RecordTypeSymbol must place
// a `case VariantTypeSymbol` FIRST where variant semantics differ (tag+payload layout, not record fields)
// — a missing Variant case silently treats a variant as a record (wrong copy/diagnose/serialize).
public sealed class VariantTypeSymbol : RecordTypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Variant;

    /// <summary>The member types of this variant. Settable so the stdlib registration fixpoint
    /// (<c>ResolveProgramMemberVariables</c>) can re-resolve arms that had unresolvable forward or
    /// self references (e.g. a recursive <c>List[SerialValue]</c>) on the first pass.</summary>
    public List<VariantMemberInfo> Members { get; set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="VariantTypeSymbol"/> class.
    /// </summary>
    /// <param name="name">The name of the variant type.</param>
    public VariantTypeSymbol(string name) : base(name: name)
    {
    }

    /// <summary>
    /// Finds a member by its type name.
    /// </summary>
    /// <param name="type">The type to look up.</param>
    /// <returns>The matching member info, or null if not found.</returns>
    public VariantMemberInfo? FindMember(TypeSymbol type)
    {
        return Members.FirstOrDefault(predicate: member => member.Type?.Name == type.Name);
    }

    /// <inheritdoc/>
    public override int SizeBytes(int pointerSize)
    {
        // Layout: { i64 type_id, [max-payload bytes] }, aligned to max(tag, payload).
        int maxPayloadSize = 0;
        int maxPayloadAlignment = 1;
        foreach (VariantMemberInfo member in Members.Where(predicate: m =>
                     m is { IsNone: false, Type: not null }))
        {
            int payloadSize = member.Type!.SizeBytes(pointerSize: pointerSize);
            int payloadAlignment = Math.Max(val1: Math.Min(val1: payloadSize, val2: 16), val2: 1);
            maxPayloadSize = Math.Max(val1: maxPayloadSize, val2: payloadSize);
            maxPayloadAlignment = Math.Max(val1: maxPayloadAlignment, val2: payloadAlignment);
        }

        const int tagSize = 8;
        int structAlignment = Math.Max(val1: tagSize, val2: maxPayloadAlignment);
        int size = AlignTo(size: tagSize, alignment: maxPayloadAlignment) + maxPayloadSize;
        return AlignTo(size: size, alignment: structAlignment);
    }

    /// <inheritdoc/>
    /// <exception cref="InvalidOperationException">Thrown if this is not a generic definition.</exception>
    /// <exception cref="ArgumentException">Thrown if the number of type arguments doesn't match.</exception>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        if (!IsGenericDefinition)
        {
            throw new InvalidOperationException(
                message: $"Variant '{Name}' is not a generic definition.");
        }

        if (typeArguments.Count != GenericParameters!.Count)
        {
            throw new ArgumentException(
                message:
                $"Expected {GenericParameters.Count} type arguments, got {typeArguments.Count}.");
        }

        // Create type parameter substitution map
        var substitution = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < GenericParameters.Count; i++)
        {
            substitution[key: GenericParameters[index: i]] = typeArguments[index: i];
        }

        // Substitute types in members
        var substitutedMembers = Members.Select(selector: m =>
                                             SubstituteMemberType(memberInfo: m,
                                                 substitution: substitution))
                                        .ToList();

        // Build resolved type name
        string resolvedName = $"{Name}[{string.Join(separator: ", ",
            values: typeArguments.Select(selector: t => t.Name))}]";

        return new VariantTypeSymbol(name: resolvedName)
        {
            Members = substitutedMembers,
            TypeArguments = typeArguments,
            GenericDefinition = this,
            Visibility = Visibility,
            Location = Location,
            Module = Module,
            Realm = Realm
        };
    }

    /// <summary>
    /// Substitutes the type in a member for generic resolution.
    /// </summary>
    private static VariantMemberInfo SubstituteMemberType(VariantMemberInfo memberInfo,
        Dictionary<string, TypeSymbol> substitution)
    {
        if (memberInfo.IsNone)
        {
            return memberInfo; // None state has no type to substitute
        }

        TypeSymbol substitutedType =
            SubstituteType(type: memberInfo.Type!, substitution: substitution);
        if (substitutedType == memberInfo.Type)
        {
            return memberInfo;
        }

        return memberInfo.WithSubstitutedType(newType: substitutedType);
    }

    /// <summary>
    /// Recursively substitutes type parameters in a type.
    /// </summary>
    private static new TypeSymbol SubstituteType(TypeSymbol type,
        Dictionary<string, TypeSymbol> substitution)
    {
        if (substitution.TryGetValue(key: type.Name, value: out TypeSymbol? substituted))
        {
            return substituted;
        }

        if (type is { IsGenericResolution: true, TypeArguments: not null })
        {
            var newArgs = type.TypeArguments
                              .Select(selector: arg =>
                                   SubstituteType(type: arg, substitution: substitution))
                              .ToList();

            if (type is VariantTypeSymbol { GenericDefinition: not null } variantType)
            {
                return variantType.GenericDefinition.CreateInstance(typeArguments: newArgs);
            }
        }

        return type;
    }
}
