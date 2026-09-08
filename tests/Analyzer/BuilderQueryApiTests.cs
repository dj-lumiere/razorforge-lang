using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Full API tests for BuilderQuery -> verifies every per-type routine, standalone routine,
/// and platform/build info routine resolves correctly with 'import BuilderQuery'.
/// Also verifies import-gating for each category and that wired routines are unaffected.
/// </summary>
public class BuilderQueryApiTests
{
    #region Per-Type Routines -> Records
    /// <summary>
    /// Verifies that the test validates type name available.
    /// </summary>

    [Fact]
    public void Record_TypeName_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.type_name()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates type kind available.
    /// </summary>

    [Fact]
    public void Record_TypeKind_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.type_kind()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates type id available.
    /// </summary>

    [Fact]
    public void Record_TypeId_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.type_id()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates module name available.
    /// </summary>

    [Fact]
    public void Record_ModuleName_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.module_name()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates is generic available.
    /// </summary>

    [Fact]
    public void Record_IsGeneric_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.is_generic()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates data size available.
    /// </summary>

    [Fact]
    public void Record_DataSize_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.data_size()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates member variable count available.
    /// </summary>

    [Fact]
    public void Record_MemberVariableCount_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.member_variable_count()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates generic args available.
    /// </summary>

    [Fact]
    public void Record_GenericArgs_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.generic_args()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates protocols available.
    /// </summary>

    [Fact]
    public void Record_Protocols_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.protocols()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates routine names available.
    /// </summary>

    [Fact]
    public void Record_RoutineNames_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.routine_names()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates annotations available.
    /// </summary>

    [Fact]
    public void Record_Annotations_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.annotations()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates dependencies available.
    /// </summary>

    [Fact]
    public void Record_Dependencies_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.dependencies()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates origin module available.
    /// </summary>

    [Fact]
    public void Record_OriginModule_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Point
                         x: S64
                         y: S64

                       routine test()
                         var p = Point(x: 1, y: 2)
                         var v = p.full_type_name()
                         return
                       """);
    }

    #endregion

    #region Per-Type Routines -> Entities
    /// <summary>
    /// Verifies that the test validates type name available.
    /// </summary>

    [Fact]
    public void Entity_TypeName_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       entity Counter
                         value: S64

                       routine test()
                         var c = Counter(value: 0)
                         var v = c.type_name()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates type kind available.
    /// </summary>

    [Fact]
    public void Entity_TypeKind_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       entity Counter
                         value: S64

                       routine test()
                         var c = Counter(value: 0)
                         var v = c.type_kind()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates member variable count available.
    /// </summary>

    [Fact]
    public void Entity_MemberVariableCount_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       entity Counter
                         value: S64

                       routine test()
                         var c = Counter(value: 0)
                         var v = c.member_variable_count()
                         return
                       """);
    }

    #endregion

    #region Per-Type Routines -> Choices
    /// <summary>
    /// Verifies that the test validates type name available.
    /// </summary>

    [Fact]
    public void Choice_TypeName_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       choice Color
                         RED
                         GREEN
                         BLUE

                       routine test()
                         var c = Color.RED
                         var v = c.type_name()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates type kind available.
    /// </summary>

    [Fact]
    public void Choice_TypeKind_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       choice Color
                         RED
                         GREEN
                         BLUE

                       routine test()
                         var c = Color.RED
                         var v = c.type_kind()
                         return
                       """);
    }

    #endregion

    #region Per-Type Routines -> Import Gating (every routine without import)
    /// <summary>Verifies that the routine without import reports the expected error.</summary>

    [Theory]
    [InlineData("type_name")]
    [InlineData("type_kind")]
    [InlineData("type_id")]
    [InlineData("module_name")]
    [InlineData("is_generic")]
    [InlineData("data_size")]
    [InlineData("member_variable_count")]
    [InlineData("generic_args")]
    [InlineData("protocols")]
    [InlineData("routine_names")]
    [InlineData("annotations")]
    [InlineData("dependencies")]
    [InlineData("full_type_name")]
    public void PerTypeRoutine_WithoutImport_ReportsError(string routineName)
    {
        string source = $$"""
                          record Pair
                            a: S64
                            b: S64

                          routine test()
                            var p = Pair(a: 1, b: 2)
                            var v = p.{{routineName}}()
                            return
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>Verifies that the routine with import produces no unexpected diagnostics.</summary>

    [Theory]
    [InlineData("type_name")]
    [InlineData("type_kind")]
    [InlineData("type_id")]
    [InlineData("module_name")]
    [InlineData("is_generic")]
    [InlineData("data_size")]
    [InlineData("member_variable_count")]
    [InlineData("generic_args")]
    [InlineData("protocols")]
    [InlineData("routine_names")]
    [InlineData("annotations")]
    [InlineData("dependencies")]
    [InlineData("full_type_name")]
    public void PerTypeRoutine_WithImport_NoError(string routineName)
    {
        string source = $$"""
                          import BuilderQuery

                          record Pair
                            a: S64
                            b: S64

                          routine test()
                            var p = Pair(a: 1, b: 2)
                            var v = p.{{routineName}}()
                            return
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion

    #region Standalone Routines -> Source Location
    /// <summary>Verifies that the routine without import reports the expected error.</summary>

    [Theory]
    [InlineData("source_file")]
    [InlineData("source_line")]
    [InlineData("source_column")]
    [InlineData("source_routine")]
    [InlineData("source_module")]
    [InlineData("source_text")]
    [InlineData("caller_file")]
    [InlineData("caller_line")]
    [InlineData("caller_routine")]
    public void SourceLocationRoutine_WithoutImport_ReportsError(string routineName)
    {
        string source = $$"""
                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        // Standalone BuilderQuery routines are plain `module BuilderQuery` members: without the
        // import the bare name is simply out of scope, an ordinary UnknownIdentifier (not a bespoke
        // import-required diagnostic), consistent with every other unimported module member.
        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>Verifies that the routine with import produces no unexpected diagnostics.</summary>

    [Theory]
    [InlineData("source_file")]
    [InlineData("source_line")]
    [InlineData("source_column")]
    [InlineData("source_routine")]
    [InlineData("source_module")]
    [InlineData("source_text")]
    [InlineData("caller_file")]
    [InlineData("caller_line")]
    [InlineData("caller_routine")]
    public void SourceLocationRoutine_WithImport_NoError(string routineName)
    {
        string source = $$"""
                          import BuilderQuery

                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion

    #region Standalone Routines -> Platform/Build Info
    /// <summary>Verifies that the routine without import reports the expected error.</summary>

    [Theory]
    [InlineData("target_os")]
    [InlineData("target_arch")]
    [InlineData("builder_version")]
    [InlineData("build_timestamp")]
    [InlineData("build_mode")]
    [InlineData("page_size")]
    [InlineData("cache_line")]
    [InlineData("word_size")]
    public void PlatformRoutine_WithoutImport_ReportsError(string routineName)
    {
        string source = $$"""
                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        // See SourceLocationRoutine_WithoutImport_ReportsError: standalone BuilderQuery routines are
        // ordinary module members now, so a missing import is a plain UnknownIdentifier.
        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>Verifies that the routine with import produces no unexpected diagnostics.</summary>

    [Theory]
    [InlineData("target_os")]
    [InlineData("target_arch")]
    [InlineData("builder_version")]
    [InlineData("build_timestamp")]
    [InlineData("build_mode")]
    [InlineData("page_size")]
    [InlineData("cache_line")]
    [InlineData("word_size")]
    public void PlatformRoutine_WithImport_NoError(string routineName)
    {
        string source = $$"""
                          import BuilderQuery

                          routine test()
                            var v = {{routineName}}()
                            return
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion

    #region Wired Routines -> Never Gated
    /// <summary>
    /// Verifies that the test validates routine without import without unexpected diagnostics.
    /// </summary>

    [Theory]
    [InlineData("""var s = f"{p}" """)]
    [InlineData("""var s = f"{p:?}" """)]
    [InlineData("var eq = p == q")]
    [InlineData("var ne = p != q")]
    [InlineData("""var h = {p}""")]
    public void WiredRoutine_WithoutImport_NoError(string usage)
    {
        string source = $$"""
                          record Point
                            x: S64
                            y: S64

                          routine test()
                            var p = Point(x: 1, y: 2)
                            var q = Point(x: 3, y: 4)
                            {{usage}}
                            return
                          """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion

    #region Multiple Routines Combined
    /// <summary>
    /// Verifies that the test validates per type routines on record available.
    /// </summary>

    [Fact]
    public void AllPerTypeRoutines_OnRecord_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Pair
                         x: S64
                         y: S64

                       routine test()
                         var p = Pair(x: 1, y: 2)
                         var a = p.type_name()
                         var b = p.type_kind()
                         var c = p.type_id()
                         var d = p.module_name()
                         var e = p.is_generic()
                         var f = p.data_size()
                         var g = p.member_variable_count()
                         var h = p.generic_args()
                         var i = p.protocols()
                         var j = p.routine_names()
                         var k = p.annotations()
                         var l = p.dependencies()
                         var m = p.full_type_name()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates standalone routines available.
    /// </summary>

    [Fact]
    public void AllStandaloneRoutines_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       routine test()
                         var a = source_file()
                         var b = source_line()
                         var c = source_column()
                         var d = source_routine()
                         var e = source_module()
                         var f = source_text()
                         var g = caller_file()
                         var h = caller_line()
                         var i = caller_routine()
                         var j = target_os()
                         var k = target_arch()
                         var l = builder_version()
                         var m = build_timestamp()
                         var n = build_mode()
                         var o = page_size()
                         var p = cache_line()
                         var q = word_size()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates per type and standalone available.
    /// </summary>

    [Fact]
    public void MixedPerTypeAndStandalone_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       record Pair
                         a: S64
                         b: S64

                       routine test()
                         var p = Pair(a: 1, b: 2)
                         var name = p.type_name()
                         var file = source_file()
                         var os = target_os()
                         var sz = p.data_size()
                         var line = source_line()
                         return
                       """);
    }

    #endregion


    #region Per-Type Routines -> On Different Type Kinds
    /// <summary>
    /// Verifies that the test validates all metadata available.
    /// </summary>

    [Fact]
    public void Entity_AllMetadata_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       entity Node
                         value: S64

                       routine test()
                         var n = Node(value: 42)
                         var a = n.type_name()
                         var b = n.type_kind()
                         var c = n.type_id()
                         var d = n.module_name()
                         var e = n.is_generic()
                         var f = n.member_variable_count()
                         var g = n.protocols()
                         var h = n.routine_names()
                         return
                       """);
    }
    /// <summary>
    /// Verifies that the test validates all metadata available.
    /// </summary>

    [Fact]
    public void Choice_AllMetadata_Available()
    {
        AssertAnalyzesSa("""
                       import BuilderQuery

                       choice Direction
                         NORTH
                         SOUTH
                         EAST
                         WEST

                       routine test()
                         var d = Direction.NORTH
                         var a = d.type_name()
                         var b = d.type_kind()
                         var c = d.type_id()
                         var e = d.module_name()
                         return
                       """);
    }

    #endregion
}
