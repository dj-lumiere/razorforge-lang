using SyntaxTree;

namespace Builder.Verification;

/// <summary>
/// Immutable "already-analyzed stdlib" cache produced ONCE at the feed boundary (the warm/snapshot
/// SemanticVerifier constructors) and consumed READ-ONLY by every downstream phase. The single point where a
/// warm (incremental) build differs from a cold build: cold uses <see cref="Empty"/> (nothing cached);
/// warm carries the restored snapshot's already-analyzed keys/bodies. Downstream must branch on memo CONTENT
/// (e.g. "is this key already done?"), never on "am I warm?" — so warm ≡ cold by construction.
/// </summary>
internal sealed record StdlibMemo(
    bool IsWarm,
    HashSet<string> RestoredVariantKeys,
    HashSet<string> RestoredInstantiationKeys,
    IReadOnlyDictionary<string, Statement>? WarmStdlibRoutineBodies)
{
    /// <summary>The cold-build memo: nothing cached — every downstream pass does full work.</summary>
    public static StdlibMemo Empty { get; } = new(
        IsWarm: false,
        RestoredVariantKeys: new HashSet<string>(comparer: StringComparer.Ordinal),
        RestoredInstantiationKeys: new HashSet<string>(comparer: StringComparer.Ordinal),
        WarmStdlibRoutineBodies: null);
}
