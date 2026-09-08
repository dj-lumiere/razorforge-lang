using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Builder-internal sentinel for the per-part handle of a comptime <c>expand</c> loop (the <c>m</c>
/// in <c>expand m in allmemvarof(T)</c>). It is never a user-visible type: it exists only so that,
/// during semantic analysis (which runs before monomorphization, when the concrete members of
/// <c>T</c> are unknown), the handle identifier resolves and its projections type leniently —
/// <c>m.name</c> as <c>Text</c>, <c>m.id</c> as <c>U64</c>. The real per-member expansion and
/// typecheck happen at monomorphization in the generic AST rewriter.
/// </summary>
public sealed class ComptimeHandleTypeInfo : TypeInfo
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Error;

    /// <summary>Singleton instance of the comptime-handle sentinel.</summary>
    public static readonly ComptimeHandleTypeInfo Instance = new();

    private ComptimeHandleTypeInfo() : base(name: "<comptime-handle>")
    {
    }

    /// <inheritdoc/>
    /// <returns>Always returns this instance.</returns>
    public override TypeInfo CreateInstance(List<TypeInfo> typeArguments)
    {
        return this;
    }
}
