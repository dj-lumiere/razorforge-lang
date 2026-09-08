using Compiler.Diagnostics;
using Compiler.Verification;
using Compiler.Verification.Results;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Types;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests for collection literal type inference from variable annotations.
/// </summary>
public class CollectionLiteralTests
{
    /// <summary>
    /// Verifies that the test validates list literal with type annotation infers element type.
    /// </summary>
    [Fact]
    public void EmptyListLiteral_WithTypeAnnotation_InfersElementType()
    {
        string source = """
                        routine test()
                          var items: List[S64] = []
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that the test validates list literal without type annotation and reports the expected error.
    /// </summary>

    [Fact]
    public void EmptyListLiteral_WithoutTypeAnnotation_ReportsError()
    {
        string source = """
                        routine test()
                          var items = []
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyListNoTypeAnnotation);
    }
    /// <summary>
    /// Verifies that the test validates set literal with type annotation infers element type.
    /// </summary>

    [Fact]
    public void EmptySetLiteral_WithTypeAnnotation_InfersElementType()
    {
        string source = """
                        routine test()
                          var items: Set[S64] = {}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that the test validates set literal without type annotation and reports the expected error.
    /// </summary>

    [Fact]
    public void EmptySetLiteral_WithoutTypeAnnotation_ReportsError()
    {
        string source = """
                        routine test()
                          var items = {}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptySetNoTypeAnnotation);
    }
    /// <summary>
    /// Verifies that the test validates dict literal with type annotation infers key value types.
    /// </summary>

    [Fact]
    public void EmptyDictLiteral_WithTypeAnnotation_InfersKeyValueTypes()
    {
        string source = """
                        routine test()
                          var items: Dict[S64, Text] = {:}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that the test validates dict literal without type annotation and reports the expected error.
    /// </summary>

    [Fact]
    public void EmptyDictLiteral_WithoutTypeAnnotation_ReportsError()
    {
        string source = """
                        routine test()
                          var items = {:}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.EmptyDictNoTypeAnnotation);
    }
    /// <summary>
    /// Verifies that a non-empty list literal infers its element type from the elements.
    /// </summary>
    [Fact]
    public void NonEmptyListLiteral_InfersFromElements()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that a non-empty list literal with an explicit annotation produces no error.
    /// </summary>
    [Fact]
    public void NonEmptyListLiteral_WithTypeAnnotation_NoError()
    {
        string source = """
                        routine test()
                          var items: List[S64] = [10, 20, 30]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates empty set literal infers from elements.
    /// </summary>

    [Fact]
    public void NonEmptySetLiteral_InfersFromElements()
    {
        string source = """
                        routine test()
                          var items = {1, 2, 3}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that the test validates empty dict literal infers from elements.
    /// </summary>

    [Fact]
    public void NonEmptyDictLiteral_InfersFromElements()
    {
        string source = """
                        routine test()
                          var items = {1: "one", 2: "two"}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }
    /// <summary>
    /// Verifies that the test validates literal with circular list annotation retargets to circular list.
    /// </summary>
    [Fact]
    public void ListLiteral_WithCircularListAnnotation_RetargetsToCircularList()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: CircularList[S64] = [1, 2, 3]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates literal with sorted list annotation retargets to sorted list.
    /// </summary>
    [Fact]
    public void ListLiteral_WithSortedListAnnotation_RetargetsToSortedList()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: SortedList[S64] = [3, 1, 2]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates literal with sorted set annotation retargets to sorted set.
    /// </summary>
    [Fact]
    public void SetLiteral_WithSortedSetAnnotation_RetargetsToSortedSet()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: SortedSet[S64] = {3, 1, 2}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates literal with sorted dict annotation retargets to sorted dict.
    /// </summary>
    [Fact]
    public void DictLiteral_WithSortedDictAnnotation_RetargetsToSortedDict()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: SortedDict[S32, S32] = {3: 30, 1: 10, 2: 20}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates literal with priority queue annotation retargets to priority queue.
    /// </summary>
    [Fact]
    public void DictLiteral_WithPriorityQueueAnnotation_RetargetsToPriorityQueue()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: PriorityQueue[S64, Text] = {1: "high", 10: "low"}
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates literal with array annotation retargets to array.
    /// </summary>
    [Fact]
    public void ListLiteral_WithArrayAnnotation_RetargetsToArray()
    {
        string source = """
                        routine test()
                          var items: Array[S64, 4] = [1, 2, 3, 4]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates literal with bit array annotation retargets to bit array.
    /// </summary>
    [Fact]
    public void ListLiteral_WithBitArrayAnnotation_RetargetsToBitArray()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: BitArray[8] = [true, false, true, true, false, true, false, true]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    /// <summary>
    /// Verifies that the test validates list literal with trailing empty inner list uses sibling context.
    /// </summary>
    [Fact]
    public void NestedListLiteral_WithTrailingEmptyInnerList_UsesSiblingContext()
    {
        string source = """
                        routine test()
                          var items = [[1, 2, 3], [4, 5], [6], []]
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge) { SaOnly = true };
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration routine = program.Declarations.OfType<RoutineDeclaration>()
            .Single(predicate: declaration => declaration.Name == "test");
        BlockStatement body = Assert.IsType<BlockStatement>(routine.Body);
        VariableDeclaration variable = body.Statements
            .OfType<DeclarationStatement>()
            .Select(selector: statement => statement.Declaration)
            .OfType<VariableDeclaration>()
            .Single(predicate: declaration => declaration.Name == "items");

        TypeInfo? resolvedType = variable.Initializer?.ResolvedType;
        Assert.NotNull(@object: resolvedType);
        Assert.Equal(expected: "Core.List[Core.List[Core.S64]]",
            actual: resolvedType!.FullName);
    }

    /// <summary>
    /// Verifies that the test validates literal without annotation defaults to owned list.
    /// </summary>
    [Fact]
    public void ListLiteral_WithoutAnnotation_DefaultsToOwnedList()
    {
        string source = """
                        routine test()
                          var items = [1, 2, 3]
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge) { SaOnly = true };
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration routine = program.Declarations.OfType<RoutineDeclaration>()
            .Single(predicate: declaration => declaration.Name == "test");
        BlockStatement body = Assert.IsType<BlockStatement>(routine.Body);
        VariableDeclaration variable = body.Statements
            .OfType<DeclarationStatement>()
            .Select(selector: statement => statement.Declaration)
            .OfType<VariableDeclaration>()
            .Single(predicate: declaration => declaration.Name == "items");

        TypeInfo? resolvedType = variable.Initializer?.ResolvedType;
        Assert.NotNull(@object: resolvedType);
        Assert.Equal(expected: "Core.List[Core.S64]", actual: resolvedType!.FullName);
    }

    /// <summary>
    /// Regression: bare-entity constructor calls bound via `var x = T(...)` must auto-wrap
    /// to T so the local owns the heap allocation. Without the wrap, the var holds a
    /// bare entity pointer, scope-exit cleanup invalidates it, and any caller that stored
    /// the pointer (e.g. an entity field via constructor argument) reads dangling memory.
    /// Originally hit in playground/SegTreeLazy.rf as a heap-layout-flaky IOOB/AV.
    /// </summary>
    [Fact]
    public void CreatorExpression_BareEntity_InfersOwnedWrap()
    {
        string source = """
                        routine test()
                          var items = List[S64](capacity: 8u64)
                          return
                        """;

        Program program = Parse(source: source);
        var analyzer = new SemanticVerifier(language: Language.RazorForge) { SaOnly = true };
        AnalysisResult result = analyzer.Analyze(program: program);

        Assert.Empty(collection: result.Errors);

        RoutineDeclaration routine = program.Declarations.OfType<RoutineDeclaration>()
            .Single(predicate: declaration => declaration.Name == "test");
        BlockStatement body = Assert.IsType<BlockStatement>(routine.Body);
        VariableDeclaration variable = body.Statements
            .OfType<DeclarationStatement>()
            .Select(selector: statement => statement.Declaration)
            .OfType<VariableDeclaration>()
            .Single(predicate: declaration => declaration.Name == "items");

        TypeInfo? resolvedType = variable.Initializer?.ResolvedType;
        Assert.NotNull(@object: resolvedType);
        Assert.Equal(expected: "Core.List[Core.S64]", actual: resolvedType!.FullName);
    }

    /// <summary>
    /// Verifies that the test validates literal with wrong arity and reports the expected error.
    /// </summary>
    [Fact]
    public void ArrayLiteral_WithWrongArity_ReportsError()
    {
        string source = """
                        routine test()
                          var items: Array[S64, 4] = [1, 2, 3, 4, 5]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArgumentCountMismatch);
    }

    /// <summary>
    /// Verifies that the test validates array literal with wrong arity and reports the expected error.
    /// </summary>
    [Fact]
    public void BitArrayLiteral_WithWrongArity_ReportsError()
    {
        string source = """
                        import Collections
                        routine test()
                          var items: BitArray[4] = [true, false, true]
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.ArgumentCountMismatch);
    }
}
