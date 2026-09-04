namespace Compiler.Diagnostics;

/// <summary>
/// Process-wide compiler diagnostic / dev-loop flags, populated from <c>config.toml</c> at the start
/// of a build. These REPLACE the former <c>RAZORFORGE_*</c> / <c>RF_*</c> environment variables (removed):
/// dev-loop routing lives in <c>[target]</c> (<c>use-daemon</c>, <c>mode = "debug-jit"</c>) while the
/// diagnostics below — incl. <c>timing</c> (merged sa/phase) — live in <c>[debug]</c>.
/// <para>The one-shot CLI sets these once per run; the compile daemon serves requests SERIALLY, so it
/// resets and re-applies them per request from the client's manifest with no concurrent mutation. Machine-
/// level install paths (LLVM home, stdlib root, daemon pipe) stay as environment variables by design —
/// they are shared across projects, not per-project config.</para>
/// </summary>
public static class DiagnosticFlags
{
    /// <summary>Print per-phase <c>[SA]</c>/<c>[phase]</c> timing. Was <c>RAZORFORGE_PHASE_TIMING</c>.</summary>
    public static bool PhaseTiming;

    /// <summary>Survey unresolved marker-protocol conformances. Was <c>RF_MARKER_SURVEY</c>.</summary>

    /// <summary>Print codegen DCE prune statistics. Was <c>RF_PRUNE_STATS</c>.</summary>
    public static bool PruneStats;

    /// <summary>Trace ORC-JIT lowering stages. Was <c>RAZORFORGE_JIT_TRACE</c>.</summary>
    public static bool JitTrace;

    /// <summary>Path to dump the routine-reachability set, or null. Was <c>RF_REACHABILITY_DUMP</c>.</summary>
    public static string? ReachabilityDump;

    /// <summary>Path to dump the maysuspend analysis, or null. Was <c>RF_MAYSUSPEND_DUMP</c>.</summary>
    public static string? MaySuspendDump;

    /// <summary>Clears every flag. Call at the start of each build (esp. each daemon request) so a prior
    /// request's flags never leak into the next.</summary>
    public static void Reset()
    {
        PhaseTiming = false;
        PruneStats = false;
        JitTrace = false;
        ReachabilityDump = null;
        MaySuspendDump = null;
    }
}
