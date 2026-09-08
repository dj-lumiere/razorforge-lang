namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Regression lock for the realm-import alias feature: `import Module.C::name` (or `LLVM::name`) brings a
/// foreign routine into BARE scope, lifting the usual `C::`/`LLVM::` call-site qualifier requirement for
/// that ONE routine. The declaration stays realm-qualified; only the aliased routine may be called bare;
/// everything else still errors (RF-S460). See CheckCallRealm + `_importedForeignAliases`.
/// </summary>
public class RealmImportAliasTests
{
    /// <summary>`import Module.C::name` lets the C routine be called without the `C::` qualifier.</summary>
    [Fact]
    public void ImportAlias_C_AllowsBareCall()
    {
        AssertAnalyzes(source: """
                               import Core.C::rf_runtime_init

                               routine start()
                                 rf_runtime_init()
                                 return
                               """);
    }

    /// <summary>Without the alias import, a bare foreign call is an error — the qualifier is required.</summary>
    [Fact]
    public void BareForeignCall_WithoutAlias_Errors()
    {
        AssertHasError(source: """
                               routine start()
                                 rf_runtime_init()
                                 return
                               """,
            expectedErrorSubstring: "lives in the C realm");
    }

    /// <summary>The alias works for the LLVM realm too: `import Module.LLVM::name` -> bare call.</summary>
    [Fact]
    public void ImportAlias_LLVM_AllowsBareCall()
    {
        AssertAnalyzes(source: """
                               import Core.LLVM::add

                               routine start()
                                 var x = 20_s32
                                 var y = 22_s32
                                 var z = add[S32](a: x, b: y)
                                 return
                               """);
    }

    /// <summary>Precision: an alias is keyed by realm AND name — a `C::` alias does not legitimize a
    /// bare call to a DIFFERENT (here, un-aliased LLVM) foreign routine.</summary>
    [Fact]
    public void ImportAlias_DoesNotLeakToOtherRoutine()
    {
        AssertHasError(source: """
                               import Core.C::rf_runtime_init

                               routine start()
                                 var z = add[S32](a: 1_s32, b: 2_s32)
                                 return
                               """,
            expectedErrorSubstring: "lives in the LLVM realm");
    }
}
