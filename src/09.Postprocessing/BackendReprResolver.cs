using System.Linq;
using Compiler.Resolution;
using Compiler.Targeting;
using TypeModel.Reprs;
using TypeModel.Types;

namespace Compiler.Postprocessing;

/// <summary>
/// Computes backend representation metadata for fully resolved semantic types.
/// </summary>
public static class BackendReprResolver
{
    /// <summary>
    /// Resolves the backend ABI/storage representation for a semantic type.
    /// </summary>
    public static BackendRepr Resolve(TypeInfo type, TypeRegistry registry, TargetConfig target)
    {
        return type switch
        {
            TupleTypeInfo tuple => new BackendRepr(
                Kind: BackendReprKind.Aggregate,
                SourceType: type,
                LlvmAbiType: $"{{ {string.Join(separator: ", ",
                    values: tuple.ElementTypes.Select(selector =>
                        Resolve(type: selector, registry: registry, target: target).LlvmAbiType))} }}",
                AggregateLayoutKey: type.FullName),

            // Variant is a RecordTypeInfo subclass — must precede the Record arms.
            VariantTypeInfo => new BackendRepr(
                Kind: BackendReprKind.Aggregate,
                SourceType: type,
                LlvmAbiType: type.FullName,
                AggregateLayoutKey: type.FullName),

            RecordTypeInfo
            {
                BackendType: not null, IsGenericDefinition: false
            } record => ResolveDirectBackendRecord(record: record),

            RecordTypeInfo record => new BackendRepr(
                Kind: BackendReprKind.Aggregate,
                SourceType: type,
                LlvmAbiType: record.LlvmType,
                AggregateLayoutKey: type.FullName,
                IsPassedIndirectly: false),

            // Entity (and Crashable, an entity subclass) -> entity ref pointer.
            EntityTypeInfo => new BackendRepr(
                Kind: BackendReprKind.EntityRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Entity,
                PointeeType: type),

            ProtocolTypeInfo => new BackendRepr(
                Kind: BackendReprKind.ProtocolRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Protocol,
                PointeeType: type),

            WrapperTypeInfo wrapper => new BackendRepr(
                Kind: BackendReprKind.WrapperRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: ClassifyPointerFlavor(typeName: wrapper.Name),
                PointeeType: wrapper.InnerType,
                IsTransparent: true),

            RoutineTypeInfo => new BackendRepr(
                Kind: BackendReprKind.RoutineRef,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Routine),

            ConstGenericValueTypeInfo => new BackendRepr(
                Kind: BackendReprKind.Scalar,
                SourceType: type,
                LlvmAbiType: "i64"),

            GenericParameterTypeInfo => new BackendRepr(
                Kind: BackendReprKind.RawPtr,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Raw),

            ErrorTypeInfo => new BackendRepr(
                Kind: BackendReprKind.RawPtr,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Raw),

            _ => new BackendRepr(
                Kind: BackendReprKind.RawPtr,
                SourceType: type,
                LlvmAbiType: "ptr",
                PointerFlavor: PointerFlavor.Raw)
        };
    }

    /// <summary>
    /// Resolves records that explicitly declare their backend type instead of using their field layout.
    /// </summary>
    private static BackendRepr ResolveDirectBackendRecord(RecordTypeInfo record)
    {
        if (record.BackendType == "void")
        {
            return new BackendRepr(
                Kind: BackendReprKind.Void,
                SourceType: record,
                LlvmAbiType: "void");
        }

        if (record.BackendType == "ptr")
        {
            PointerFlavor flavor = ClassifyPointerFlavor(typeName: record.Name);
            BackendReprKind kind = flavor == PointerFlavor.Raw
                ? BackendReprKind.RawPtr
                : BackendReprKind.WrapperRef;
            TypeInfo? pointeeType = record.TypeArguments is { Count: > 0 }
                ? record.TypeArguments[0]
                : null;

            return new BackendRepr(
                Kind: kind,
                SourceType: record,
                LlvmAbiType: "ptr",
                PointerFlavor: flavor,
                PointeeType: pointeeType,
                AggregateLayoutKey: record.FullName,
                IsTransparent: kind == BackendReprKind.WrapperRef);
        }

        return new BackendRepr(
            Kind: BackendReprKind.Scalar,
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
