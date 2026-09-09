namespace TypeModel;

/// <summary>
/// Tunable limits for the associated-type feature (<c>relates</c> + <c>/</c> projection).
///
/// These restrictions are intentionally NOT hardcoded at their enforcement sites — the
/// parser/resolver read them from an instance of this record so they can be relaxed later
/// (e.g. allowing user-defined associated types, or multi-level projection) by changing the
/// defaults or threading a different instance, without hunting down magic literals.
/// </summary>
/// <param name="MaxProjectionDepth">
/// Maximum number of projection segments after the base type. <c>1</c> allows <c>S/Iter</c>
/// but rejects <c>S/Iter/Inner</c>. Raise to lift the single-level restriction.
/// </param>
/// <param name="StdlibOnly">
/// When true, <c>relates</c> clauses and <c>/</c> projections are only permitted in standard
/// library sources; user programs may consume associated types but not declare/project them.
/// Set false to allow user-defined associated types.
/// </param>
public sealed record AssociatedTypeOptions(int MaxProjectionDepth = 1, bool StdlibOnly = true)
{
    /// <summary>The current default limits: single-level projection, stdlib-only.</summary>
    public static readonly AssociatedTypeOptions Default = new();
}
