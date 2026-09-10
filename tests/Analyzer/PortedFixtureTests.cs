#pragma warning disable CS1591
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// SA-level invariants extracted from playground fixtures. Each test pins down the
/// specific shape of analysis that the original fixture was guarding. The intent is
/// layer-specific regression coverage — the e2e batch script still runs the full
/// fixtures end-to-end, but a failure here points at the SA/lowering layer directly.
/// </summary>
public class PortedFixtureTests
{
    // ── playground/rc_test.rf ────────────────────────────────────────────────
    // Source memory: is_none_pattern_inversion_fix.md (2026-05-13).
    // `is None` on a Maybe-carrier produced by Tracked.try_recover() must resolve,
    // and the else-arm must bind the inner Retained[T] cleanly.

    [Fact]
    public void Analyze_TrackedTryRecover_IsNone_ElseArm_Resolves()
    {
        string source = """
                        entity Node
                          value: S64

                        routine start()
                          var a = Node(value: 1)
                          var ra = Retained(from: steal a)
                          var rt = ra.observe()
                          when rt.try_hold()
                            is None => return
                            else rc => return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    // ── playground/maybe_entity_test.rf ──────────────────────────────────────
    // Source memory: maybe_entity_auto_own.md + failable_entity_return_sa_check_2026_05_14.md.
    // After the bound-T-is-pointer redesign, Maybe[entity] is the natural carrier
    // for a failable returning an entity. `try_*` form + `is None` must resolve.

    [Fact]
    public void Analyze_FailableReturningEntity_TryForm_WhenIsNone_Resolves()
    {
        string source = """
                        routine get_text!(n: S64) -> Text
                          when n
                            == 0 => absent
                            == 1 => return "hello"
                            else => return "world"

                        routine start()
                          var m1 = try_get_text(n: 1)
                          when m1
                            is None => return
                            else v => return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    // ── playground/desugar_edge_cases.rf::test_compound ──────────────────────
    // Source memory: desugar_edge_cases_fix_2026_05_21.md.
    // Compound-assign through an index expression must dispatch to $setitem!/$getitem!
    // around the in-place operator, on a List[S64] (entity carrier).

    [Fact]
    public void Analyze_CompoundAssignOnListElement_Resolves()
    {
        string source = """
                        routine start()
                          var xs = [1_s64, 2_s64, 3_s64]
                          xs[0_u64] += 10_s64
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
}
