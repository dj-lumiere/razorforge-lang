using System;
using Compiler.Diagnostics;
using Verification.Results;
using TypeModel.Symbols;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for error variant generation.
/// </summary>
public class ErrorVariantGenerationTests
{
    #region Maybe Variant (absent only)
    /// <summary>
    /// Verifies semantic analysis behavior for failable with absent only generates try variant.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithAbsentOnly_GeneratesTryVariant()
    {
        // Routine with 'absent' only generates:
        // - try_get() -> T?
        string source = """
                        routine get!(id: U64) -> User
                          unless has_user(id)
                            absent
                          return fetch_user(id)

                        entity User
                          name: Text

                        routine has_user(id: U64) -> bool
                          return true

                        routine fetch_user(id: U64) -> User
                          return User(name: "test")

                        routine trigger_variants(id: U64) -> User?
                          return try_get(id: id)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Variants are synthesized ON DEMAND — the trigger call above generates try_get.
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_get");
        Assert.NotNull(@object: tryVariant);
        // Return type should be Maybe[User] / User?
    }

    #endregion

    #region Result Variant (throw only)
    /// <summary>
    /// Verifies semantic analysis behavior for failable with throw only generates check and try variants.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithThrowOnly_GeneratesCheckAndTryVariants()
    {
        // Routine with 'throw' only generates:
        // - check_validate() -> Result[T]
        // - try_validate() -> T?
        string source = """
                        crashable ValidationError
                          message: Text

                        @readonly
                        routine ValidationError.crash_message() -> Text
                          return me.message

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        routine validate!(value: S32) -> S32
                          if value < 0
                            throw ValidationError(message: "negative")
                          return value

                        routine trigger_variants(value: S32) -> S32?
                          check_validate(value: value)
                          return try_validate(value: value)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Variants are synthesized ON DEMAND — the trigger calls above generate check_/try_validate.
        RoutineInfo? checkVariant = result.Registry.GetRoutine(name: "check_validate");
        Assert.NotNull(@object: checkVariant);

        // Should also generate try_validate variant
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_validate");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Lookup Variant (throw AND absent)
    /// <summary>
    /// Verifies semantic analysis behavior for failable with both throw and absent generates lookup and try variants.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithBothThrowAndAbsent_GeneratesLookupAndTryVariants()
    {
        // Routine with both 'throw' and 'absent' generates:
        // - lookup_get_user() -> Lookup[T]
        // - try_get_user() -> T?
        string source = """
                        crashable DatabaseError
                          code: S32

                        @readonly
                        routine DatabaseError.crash_message() -> Text
                          return "db error"

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        entity User
                          name: Text

                        routine get_user!(id: U64) -> User
                          if id == 0
                            throw DatabaseError(code: 1)
                          unless user_exists(id)
                            absent
                          return fetch_user(id)

                        routine user_exists(id: U64) -> bool
                          return true

                        routine fetch_user(id: U64) -> User
                          return User(name: "test")

                        routine trigger_variants(id: U64) -> User?
                          lookup_get_user(id: id)
                          return try_get_user(id: id)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Variants are synthesized ON DEMAND — the trigger calls above generate lookup_/try_get_user.
        RoutineInfo? lookupVariant = result.Registry.GetRoutine(name: "lookup_get_user");
        Assert.NotNull(@object: lookupVariant);

        // Should also generate try_get_user variant
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_get_user");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Variant Generation for memberRoutines
    /// <summary>
    /// Verifies semantic analysis behavior for failable memberRoutine generates variants.
    /// </summary>

    [Fact]
    public void Analyze_FailableMemberRoutine_GeneratesVariants()
    {
        string source = """
                        entity Cache
                          data: Dict[Text, S32]

                        routine Cache.get!(key: Text) -> S32
                          unless me.data.has(key)
                            absent
                          return me.data.get(key)

                        routine Cache.trigger(key: Text) -> S32?
                          return me.try_get(key: key)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Variants are synthesized ON DEMAND — the trigger call above generates Cache.try_get.
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "Cache.try_get");
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region No Variant Generation
    /// <summary>
    /// Verifies semantic analysis behavior for non failable routine no variants generated.
    /// </summary>

    [Fact]
    public void Analyze_NonFailableRoutine_NoVariantsGenerated()
    {
        string source = """
                        routine add(a: S32, b: S32) -> S32
                          return a + b
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Should NOT generate try_add, check_add, or lookup_add
        Assert.Null(@object: result.Registry.GetRoutine(name: "try_add"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "check_add"));
        Assert.Null(@object: result.Registry.GetRoutine(name: "lookup_add"));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for failable with no throw or absent warns or errors.
    /// </summary>

    [Fact]
    public void Analyze_FailableWithNoThrowOrAbsent_WarnsOrErrors()
    {
        // Failable routine that never throws or returns absent
        string source = """
                        routine get_value!() -> S32
                          return 42
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Should warn that failable routine never fails
        Assert.True(condition: result.Warnings.Count > 0 || result.Errors.Count > 0);
    }

    #endregion

    #region Error Cases
    /// <summary>
    /// Failability is now INFERRED: a <c>throw</c> in a routine NOT declared <c>!</c> no longer emits
    /// ThrowOutsideFailableFunction — the routine is inferred-failable instead.
    /// </summary>
    [Fact]
    public void Analyze_ThrowInNonFailableRoutine_NoLongerErrors()
    {
        string source = """
                        crashable SomeError
                          msg: Text

                        @readonly
                        routine SomeError.crash_message() -> Text
                          return me.msg

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        routine will_fail() -> S32
                          throw SomeError(msg: "error")
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ThrowOutsideFailableFunction);
    }
    /// <summary>
    /// Failability is now INFERRED: an <c>absent</c> in a routine NOT declared <c>!</c> no longer emits
    /// AbsentOutsideFailableFunction — the routine is inferred-failable instead.
    /// </summary>
    [Fact]
    public void Analyze_AbsentInNonFailableRoutine_NoLongerErrors()
    {
        string source = """
                        routine might_fail() -> S32
                          absent
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.AbsentOutsideFailableFunction);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for throw non crashable and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ThrowNonCrashable_ReportsError()
    {
        string source = """
                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        record NotAnError
                          value: S32

                        routine fail!()
                          throw NotAnError(value: 42)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Message.Contains(value: "Crashable",
                comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion

    #region Variant Naming Convention
    /// <summary>
    /// Verifies semantic analysis behavior for variant names follow convention.
    /// </summary>

    [Fact]
    public void Analyze_VariantNames_FollowConvention()
    {
        string source = """
                        crashable SomeError
                          msg: Text

                        @readonly
                        routine SomeError.crash_message() -> Text
                          return me.msg

                        protocol Crashable
                          @readonly
                          routine Me.crash_message() -> Text

                        routine parse_number!(text: Text) -> S32
                          throw SomeError(msg: "parse failed")

                        routine trigger_variants(text: Text) -> S32?
                          check_parse_number(text: text)
                          return try_parse_number(text: text)
                        """;

        AnalysisResult result = AnalyzeSa(source: source);

        // Variants are synthesized ON DEMAND — the trigger calls above generate check_/try_parse_number.
        RoutineInfo? checkVariant = result.Registry.GetRoutine(name: "check_parse_number");
        RoutineInfo? tryVariant = result.Registry.GetRoutine(name: "try_parse_number");

        Assert.NotNull(@object: checkVariant);
        Assert.NotNull(@object: tryVariant);
    }

    #endregion

    #region Error Handling Types Not Passable as Parameters
    /// <summary>
    /// Verifies semantic analysis behavior for result as parameter and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_ResultAsParameter_ReportsError()
    {
        // Result[T] should not be passable as a function argument
        // It is an internal type for error handling flow, not a first-class type
        string source = """
                        entity User
                          name: Text

                        routine handle_result(result: Result[User])
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "Result",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "parameter",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }
    /// <summary>
    /// Verifies semantic analysis behavior for lookup as parameter and reports the expected error.
    /// </summary>

    [Fact]
    public void Analyze_LookupAsParameter_ReportsError()
    {
        // Lookup[T] should not be passable as a function argument
        // It is an internal type for error handling flow, not a first-class type
        string source = """
                        entity User
                          name: Text

                        routine handle_lookup(result: Lookup[User])
                          pass
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Message.Contains(value: "Lookup",
                    comparisonType: StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains(value: "parameter",
                    comparisonType: StringComparison.OrdinalIgnoreCase));
    }

    #endregion
}
