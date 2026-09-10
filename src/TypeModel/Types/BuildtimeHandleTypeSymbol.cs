using TypeModel.Enums;

namespace TypeModel.Types;

/// <summary>
/// Builder-internal sentinel for the per-part handle of a buildtime <c>expand</c> loop (the <c>m</c>
/// in <c>expand m in allmemvarof(T)</c>). It is never a user-visible type: it exists only so that,
/// during semantic analysis (which runs before monomorphization, when the concrete members of
/// <c>T</c> are unknown), the handle identifier resolves and its projections type leniently —
/// <c>m.name</c> as <c>Text</c>, <c>m.id</c> as <c>U64</c>. The real per-member expansion and
/// typecheck happen at monomorphization in the generic AST rewriter.
/// </summary>
public sealed class BuildtimeHandleTypeSymbol : TypeSymbol
{
    /// <inheritdoc/>
    public override TypeCategory Category => TypeCategory.Error;

    /// <summary>Singleton instance of the buildtime-handle sentinel.</summary>
    public static readonly BuildtimeHandleTypeSymbol Instance = new();

    private BuildtimeHandleTypeSymbol() : base(name: "<buildtime-handle>")
    {
    }

    /// <inheritdoc/>
    /// <returns>Always returns this instance.</returns>
    public override TypeSymbol CreateInstance(List<TypeSymbol> typeArguments)
    {
        return this;
    }
}
