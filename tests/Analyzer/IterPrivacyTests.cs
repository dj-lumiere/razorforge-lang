#pragma warning disable CS1591
using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// `$iter`, `$access`, and `$control` are dunder-private — only the compiler's lowering
/// passes may emit them (for-loop → $iter; argument coercion → $access/$control). User
/// code calling them directly is a compile error; otherwise the borrow / iterator could
/// be stored in a var and outlive its source. Defining the dunder is still allowed.
/// </summary>
public class IterPrivacyTests
{
    [Fact]
    public void UserCode_CallsDollarIter_OnList_Rejected()
    {
        string source = """
                        routine start()
                          var xs = [1_s64, 2_s64, 3_s64]
                          var it = xs.iter()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }

    [Fact]
    public void EachLoop_OverList_Resolves()
    {
        string source = """
                        routine start()
                          var xs = [1_s64, 2_s64, 3_s64]
                          each x in xs
                            return
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    [Fact]
    public void UserCode_CallsDollarAccess_OnOwnedEntity_Rejected()
    {
        string source = """
                        entity Counter
                          value: S64

                        routine start()
                          var c = Counter(value: 1)
                          var r = c.access()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }

    [Fact]
    public void UserCode_CallsDollarControl_OnOwnedEntity_Rejected()
    {
        string source = """
                        entity Counter
                          value: S64

                        routine start()
                          var c = Counter(value: 1)
                          var w = c.control()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }

    [Fact]
    public void ReferringParam_CallSiteCoercion_Resolves()
    {
        // The compiler injects `.access()` automatically at the Accessing[T] param.
        // No user-visible $access call appears in source; the check must not fire on the
        // synthesized coercion.
        string source = """
                        record Box
                          n: S64

                        routine echo(b: Accessing[Box]) -> S64
                          return 0_s64

                        routine start()
                          var b = Box(n: 1)
                          var r = echo(b: b)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }

    [Fact]
    public void UserCode_DefinesDollarIter_OnRecord_Allowed()
    {
        // Declaring a `$iter` memberRoutine on a user type is fine — the prohibition is on
        // *calling* `$iter` from user code, not on implementing the protocol.
        string source = """
                        record Bag
                          n: S64

                        routine Bag.iter() -> Bag
                          return me
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.DirectWiredRoutineCall);
    }
}
