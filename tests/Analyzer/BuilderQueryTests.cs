using Builder.Diagnostics;
using Builder.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for BuilderQuery import-gating and routine availability.
/// </summary>
public class BuilderQueryTests
{
    #region Import Gating -> Per-Type Routines

    /// <summary>
    /// Verifies that the test validates name without import and reports the expected error.
    /// </summary>
    [Fact]
    public void TypeName_WithoutImport_ReportsError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        routine test()
                          var p = Point(x: 1, y: 2)
                          var name = p.type_name()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates name with import without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void TypeName_WithImport_NoError()
    {
        string source = """
                        import BuilderQuery

                        record Point
                          x: S64
                          y: S64

                        routine test()
                          var p = Point(x: 1, y: 2)
                          var name = p.type_name()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates size without import and reports the expected error.
    /// </summary>
    [Fact]
    public void DataSize_WithoutImport_ReportsError()
    {
        string source = """
                        record Pair
                          a: S64
                          b: S64

                        routine test()
                          var p = Pair(a: 1, b: 2)
                          var sz = p.data_size()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates size with import without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void DataSize_WithImport_NoError()
    {
        string source = """
                        import BuilderQuery

                        record Pair
                          a: S64
                          b: S64

                        routine test()
                          var p = Pair(a: 1, b: 2)
                          var sz = p.data_size()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion

    #region Import Gating -> Standalone Routines

    /// <summary>
    /// Verifies that the test validates file without import and reports the expected error.
    /// </summary>
    [Fact]
    public void SourceFile_WithoutImport_ReportsError()
    {
        string source = """
                        routine test()
                          var f = source_file()
                          return
                        """;

        // Standalone BuilderQuery routines are plain `module BuilderQuery` members: a missing
        // import is an ordinary UnknownIdentifier, not a bespoke import-required diagnostic.
        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>
    /// Verifies that the test validates file with import without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void SourceFile_WithImport_NoError()
    {
        string source = """
                        import BuilderQuery

                        routine test()
                          var f = source_file()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates line without import and reports the expected error.
    /// </summary>
    [Fact]
    public void SourceLine_WithoutImport_ReportsError()
    {
        string source = """
                        routine test()
                          var ln = source_line()
                          return
                        """;

        // See SourceFile_WithoutImport_ReportsError: standalone routine → plain UnknownIdentifier.
        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }
    /// <summary>
    /// Verifies that the test validates file without import and reports the expected error.
    /// </summary>
    [Fact]
    public void CallerFile_WithoutImport_ReportsError()
    {
        string source = """
                        routine test()
                          var f = caller_file()
                          return
                        """;

        // See SourceFile_WithoutImport_ReportsError: standalone routine → plain UnknownIdentifier.
        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UnknownIdentifier);
    }

    #endregion

    #region Wired Routines NOT Gated

    /// <summary>
    /// Verifies that the test validates represent without import without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void WiredRepresent_WithoutImport_NoError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        routine test()
                          var p = Point(x: 1, y: 2)
                          var s = f"{p}"
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates eq without import without unexpected diagnostics.
    /// </summary>
    [Fact]
    public void WiredEq_WithoutImport_NoError()
    {
        string source = """
                        record Point
                          x: S64
                          y: S64

                        routine test()
                          var a = Point(x: 1, y: 2)
                          var b = Point(x: 3, y: 4)
                          var eq = a == b
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion

    #region Multiple Routines With Import

    /// <summary>
    /// Verifies that the test validates routines with import all available.
    /// </summary>
    [Fact]
    public void MultipleRoutines_WithImport_AllAvailable()
    {
        string source = """
                        import BuilderQuery

                        record Pair
                          x: S64
                          y: S64

                        routine test()
                          var p = Pair(x: 1, y: 2)
                          var name = p.type_name()
                          var kind = p.type_kind()
                          var tid = p.type_id()
                          var mod = p.module_name()
                          var gen = p.is_generic()
                          var sz = p.data_size()
                          var cnt = p.member_variable_count()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates routines with import available.
    /// </summary>
    [Fact]
    public void ListRoutines_WithImport_Available()
    {
        string source = """
                        import BuilderQuery

                        record Pair
                          x: S64
                          y: S64

                        routine test()
                          var p = Pair(x: 1, y: 2)
                          var protos = p.protocols()
                          var names = p.routine_names()
                          var annots = p.annotations()
                          var gargs = p.generic_args()
                          var deps = p.dependencies()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }
    /// <summary>
    /// Verifies that the test validates routines with import available.
    /// </summary>
    [Fact]
    public void StandaloneRoutines_WithImport_Available()
    {
        string source = """
                        import BuilderQuery

                        routine test()
                          var f = source_file()
                          var r = source_routine()
                          var m = source_module()
                          var ln = source_line()
                          var col = source_column()
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.DoesNotContain(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.BuilderQueryImportRequired);
    }

    #endregion
}
