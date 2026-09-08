namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Regression lock for the generic-parameter alpha-rename work (CLAUDE.md ⚡ DO-FIRST TODO).
///
/// A generic parameter's NAME is a label, not an identity — a user type named the same as a stdlib
/// generic's parameter (`record T` vs `List[T]`/`Array[T,N]`/`Hijacked[T]`, `record N` vs `Array[T,N]`,
/// `record M` vs `UnpackedFloat[M,L,W]`) must NOT break that generic's monomorphization. Today it DOES
/// (Track-C "unsubstituted generic parameter" / RF-S954), because the pipeline keys on the parameter
/// NAME as identity in ~6 places. These tests encode the fixed behavior; UN-SKIP each when the alpha-
/// rename (parameter identity = slot, not name) lands.
/// </summary>
public class GenericParamCollisionTests
{
    /// <summary>
    /// A user `record T` must coexist with `List[S32]` (List → Hijacked[T]/Array[T,N] internally).
    /// Currently FAILS: `record T` collides with the stdlib generics' `T` parameter.
    /// </summary>
    [Fact]
    public void UserRecordT_CoexistsWith_ListS32()
    {
        AssertAnalyzes(source: """
                               record T
                                 a: S32

                               routine start()
                                 var xs = List[S32]()
                                 xs.add_last(10_s32)
                                 var t = T(a: 1_s32)
                                 return
                               """);
    }

    /// <summary>
    /// The full goal: user `record T`/`N`/`M` (colliding with `Array[T,N]`, `UnpackedFloat[M,L,W]`) plus a
    /// user generic routine `identity[T]` plus F128 (heavy generic instantiation) all coexist in one file.
    /// </summary>
    [Fact]
    public void UserRecordsTNM_CoexistWith_Generics_And_Identity_And_F128()
    {
        AssertAnalyzes(source: """
                               record T
                                 a: S32
                               record N
                                 a: S32
                               record M
                                 a: S32

                               routine identity[U](x: U) -> U
                                 return x

                               routine start()
                                 var xs = List[S32]()
                                 xs.add_last(10_s32)
                                 var id = identity(7_s32)
                                 var f = 3.0_f128.sqrt()
                                 var t = T(a: 1_s32)
                                 var n = N(a: 2_s32)
                                 var m = M(a: 3_s32)
                                 return
                               """);
    }

    /// <summary>
    /// A FREE generic routine's OWN parameter must shadow a user type of the same name: `record T`
    /// plus `routine identity[T](x: T) -> T` — the `T` in the routine is the parameter, not the record,
    /// so `identity(7_s32)` infers `T = S32` rather than type-erroring against `record T`.
    /// </summary>
    [Fact]
    public void FreeGenericRoutine_OwnParam_CoexistsWith_UserRecordOfSameName()
    {
        AssertAnalyzes(source: """
                               record T
                                 a: S32

                               routine identity[T](x: T) -> T
                                 return x

                               routine start()
                                 var id = identity(7_s32)
                                 var t = T(a: 1_s32)
                                 return
                               """);
    }

    /// <summary>
    /// A member routine's OWN memberRoutine-generic parameter must shadow a user type of the same name:
    /// `record U` plus `routine Holder[A].mapped[U](u: U) -> U` — the memberRoutine's `U` is its parameter.
    /// </summary>
    [Fact]
    public void memberRoutineGenericParam_CoexistsWith_UserRecordOfSameName()
    {
        AssertAnalyzes(source: """
                               record Holder[A]
                                 value: A

                               routine Holder[A].mapped[U](u: U) -> U
                                 return u

                               record U
                                 x: S32

                               routine start()
                                 var h = Holder[S32](value: 1_s32)
                                 var r = h.mapped(7_s32)
                                 var u = U(x: 2_s32)
                                 return
                               """);
    }

    /// <summary>
    /// A USER generic type's parameter must shadow a user type of the same name: `record T` plus a
    /// user `record Box[T]` — `Box`'s `item: T` field is the parameter, not the record.
    /// </summary>
    [Fact]
    public void UserGenericType_Param_CoexistsWith_UserRecordOfSameName()
    {
        AssertAnalyzes(source: """
                               record T
                                 a: S32

                               record Box[T]
                                 item: T

                               routine start()
                                 var b = Box[S32](item: 5_s32)
                                 var t = T(a: 1_s32)
                                 return
                               """);
    }
}
