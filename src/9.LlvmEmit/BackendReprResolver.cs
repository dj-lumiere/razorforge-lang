using Builder.Declaration;
using Builder.Targeting;
using TypeModel.Reprs;
using TypeModel.Types;

namespace Builder.LlvmEmit;

/// <summary>
/// Computes backend representation metadata for fully resolved semantic types.
/// </summary>
public static class BackendReprResolver
{
    /// <summary>
    /// Resolves the backend ABI/storage representation for a semantic type.
    /// </summary>
    public static BackendRepr Resolve(TypeSymbol type, TypeRegistry registry, TargetConfig target)
    {
        return type switch
        {
            TupleTypeSymbol tuple => new BackendRepr(Kind: BackendReprKind.Aggregate,
                SourceType: type,
                LlvmAbiType: $"{{ {string.Join(separator: ", ",
                    values: tuple.ElementTypes.Select(selector: selector =>
                        Resolve(type: selector, registry: registry, target: target).LlvmAbiType))} }}",
                AggregateLayoutKey: type.FullName),

            // Variant is a RecordTypeSymbol subclass — must precede the Record arms.
            VariantTypeSymbol => new BackendRepr(Kind: BackendReprKind.Aggregate,
                SourceType: type,
                LlvmAbiType: type.FullName,
                AggregateLayoutKey: type.FullName),

            RecordTypeSymbol
            {
                BackendType: not null, IsGenericDefinition: false
            } record => ResolveDirectBackendRecord(record: record),

            RecordTypeSymbol record => new BackendRepr(Kind: BackendReprKind.Aggregate,
                SourceType: type,
                LlvmAbiType: record.LlvmType,
                AggregateLayoutKey: type.FullName,
                IsPassedIndirectly: false),

            // Entity (and Crashable, an entity subclass) -> entity ref pointer.
            EntityTypeSymbol => new BackendRepr(Kind: BackendReprKind.EntityRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Entity,
                PointeeType: type),

            ProtocolTypeSymbol => new BackendRepr(Kind: BackendReprKind.ProtocolRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Protocol,
                PointeeType: type),

            WrapperTypeSymbol wrapper => new BackendRepr(Kind: BackendReprKind.WrapperRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: ClassifyPointerFlavor(typeName: wrapper.Name),
                PointeeType: wrapper.InnerType,
                IsTransparent: true),

            RoutineTypeSymbol => new BackendRepr(Kind: BackendReprKind.RoutineRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Routine),

            ConstGenericValueTypeSymbol => new BackendRepr(Kind: BackendReprKind.Scalar,
                SourceType: type,
                LlvmAbiType: "i64"),

            GenericParameterTypeSymbol => new BackendRepr(Kind: BackendReprKind.RawPtr,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Raw),

            ErrorTypeSymbol => new BackendRepr(Kind: BackendReprKind.RawPtr,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Raw),

            _ => new BackendRepr(Kind: BackendReprKind.RawPtr,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Raw)
        };
    }

    /// <summary>
    /// Resolves records that explicitly declare their backend type instead of using their field layout.
    /// </summary>
    private static BackendRepr ResolveDirectBackendRecord(RecordTypeSymbol record)
    {
        if (record.BackendType == "void")
        {
            return new BackendRepr(Kind: BackendReprKind.Void,
                SourceType: record,
                LlvmAbiType: "void");
        }

        if (record.BackendType == "ptr")
        {
            PointerFlavor flavor = ClassifyPointerFlavor(typeName: record.Name);
            BackendReprKind kind = flavor == PointerFlavor.Raw
                ? BackendReprKind.RawPtr
                : BackendReprKind.WrapperRef;
            TypeSymbol? pointeeType = record.TypeArguments is { Count: > 0 }
                ? record.TypeArguments[index: 0]
                : null;

            return new BackendRepr(Kind: kind,
                SourceType: record,
                LlvmAbiType: "ptr",
                PointerFlavor: flavor,
                PointeeType: pointeeType,
                AggregateLayoutKey: record.FullName,
                IsTransparent: kind == BackendReprKind.WrapperRef);
        }

        return new BackendRepr(Kind: BackendReprKind.Scalar,
            SourceType: record,
            LlvmAbiType: record.BackendType!,
            AggregateLayoutKey: record.FullName);
    }

    /// <summary>
    /// Converts wrapper and raw pointer type names into pointer-flavor metadata for codegen.
    /// </summary>
    private static PointerFlavor ClassifyPointerFlavor(string typeName)
    {
        string baseName = typeName.Split(separator: '[', count: 2)[0];
        return baseName switch
        {
            RuntimeContract.Viewing => PointerFlavor.Viewing,
            RuntimeContract.Modifying => PointerFlavor.Modifying,
            RuntimeContract.Consulting => PointerFlavor.Consulting,
            RuntimeContract.Amending => PointerFlavor.Amending,
            RuntimeContract.Retained => PointerFlavor.Retained,
            RuntimeContract.Tracked => PointerFlavor.Tracked,
            RuntimeContract.Guarded => PointerFlavor.Guarded,
            RuntimeContract.Witnessed => PointerFlavor.Witnessed,
            RuntimeContract.Hijacked => PointerFlavor.Hijacked,
            _ => PointerFlavor.Raw
        };
    }
}
