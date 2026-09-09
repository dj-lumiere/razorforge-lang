using TypeModel.Types;

namespace TypeModel.Reprs;

/// <summary>
/// Concrete backend representation metadata attached before codegen entry.
/// Separates semantic type identity from ABI/storage identity.
/// </summary>
/// <param name="Kind">Broad backend category (primitive, entity ref, aggregate, etc.).</param>
/// <param name="SourceType">The semantic type this representation lowers from.</param>
/// <param name="LlvmAbiType">LLVM ABI type string (e.g. "i64", "ptr", "%Record.Foo").</param>
/// <param name="PointerFlavor">If pointer-like, what kind of pointer (raw, RC, entity, etc.).</param>
/// <param name="PointeeType">If pointer-like, the type pointed at.</param>
/// <param name="AggregateLayoutKey">Stable key used to dedupe aggregate layouts in codegen.</param>
/// <param name="IsTransparent">True when this repr forwards storage to its inner type without a wrapper.</param>
/// <param name="IsPassedIndirectly">True when ABI requires passing this value by hidden pointer.</param>
public sealed record BackendRepr(
    BackendReprKind Kind,
    TypeInfo SourceType,
    string LlvmAbiType,
    PointerFlavor PointerFlavor = PointerFlavor.None,
    TypeInfo? PointeeType = null,
    string? AggregateLayoutKey = null,
    bool IsTransparent = false,
    bool IsPassedIndirectly = false)
{
    /// <summary>True when <see cref="Kind"/> is one of the pointer-shaped backend reprs.</summary>
    public bool IsPointerLike =>
        Kind is BackendReprKind.EntityRef or BackendReprKind.ProtocolRef
            or BackendReprKind.WrapperRef or BackendReprKind.RoutineRef or BackendReprKind.RawPtr;
}
