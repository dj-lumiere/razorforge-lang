using Compiler.Diagnostics;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Contains tests for using statement.
/// </summary>
public class UsingStatementTests
{
    #region Token Using

    /// <summary>
    /// Verifies semantic analysis behavior for token using and binds the expected token type.
    /// </summary>
    [Fact]
    public void Analyze_TokenUsing_BindsTokenType()
    {
        // Token path: using with .view() binds to the Viewing token type
        string source = """
                        entity Point
                          x: S32
                          y: S32

                        routine test(p: Point)
                          using p.view() as v
                            var a: S32 = v.x
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Resource With Void $enter

    /// <summary>
    /// Verifies semantic analysis behavior for resource with void enter exit and binds the expected resource type.
    /// </summary>
    [Fact]
    public void Analyze_ResourceWithVoidEnterExit_BindsResourceType()
    {
        // When $enter returns None (void), the bound variable should have the resource type
        string source = """
                        record Lock obeys Enterable
                          id: S32

                        routine Lock.enter() -> Lock
                          return me

                        routine Lock.exit()
                          return

                        routine test()
                          var lk = Lock(id: 1)
                          using lk as l
                            var a: S32 = l.id
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Resource With Non-Void $enter

    /// <summary>
    /// Verifies semantic analysis behavior for resource with non void enter and binds the enter return type.
    /// </summary>
    [Fact]
    public void Analyze_EntityEnterablePassThrough_BindsEntityType()
    {
        // An entity obeying Enterable passes itself through `$enter` (entities return `?Me`), so the
        // bound variable has the entity type and its members resolve.
        string source = """
                        entity Connection obeys Enterable
                          tag: S32

                        routine Connection.enter() -> Connection
                          return me

                        routine Connection.exit()
                          return

                        routine test()
                          var conn = Connection(tag: 1)
                          using conn as h
                            var a: S32 = h.tag
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Missing $enter/$exit

    /// <summary>
    /// Verifies semantic analysis behavior for resource missing enter exit and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ResourceMissingEnterExit_ReportsError()
    {
        // A non-token type without $enter/$exit should produce an error
        string source = """
                        record PlainResource
                          value: S32

                        routine test()
                          var r = PlainResource(value: 42)
                          using r as res
                            var a: S32 = res.value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }
    /// <summary>
    /// Verifies semantic analysis behavior for resource with only enter and reports the expected error.
    /// </summary>
    [Fact]
    public void Analyze_ResourceWithOnlyEnter_ReportsError()
    {
        // Having only $enter without $exit should still report error
        string source = """
                        record HalfResource
                          value: S32

                        routine HalfResource.enter()
                          return

                        routine test()
                          var r = HalfResource(value: 42)
                          using r as res
                            var a: S32 = res.value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion

    #region Resource With Only $exit

    /// <summary>
    /// Verifies that a resource with only $exit and no $enter reports the missing-enter/exit error.
    /// </summary>
    [Fact]
    public void Analyze_ResourceWithOnlyExit_ReportsError()
    {
        string source = """
                        record HalfResource
                          value: S32

                        routine HalfResource.exit()
                          return

                        routine test()
                          var r = HalfResource(value: 42)
                          using r as res
                            var a: S32 = res.value
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e => e.Code == SemanticDiagnosticCode.UsingTargetMissingEnterExit);
    }

    #endregion

    #region Nested Using Blocks

    /// <summary>
    /// Verifies that nested using blocks make both bound variables available in the inner scope.
    /// </summary>
    [Fact]
    public void Analyze_NestedUsing_BothBindingsInScope_NoError()
    {
        string source = """
                        record Token obeys Enterable
                          id: S32

                        routine Token.enter() -> Token
                          return me
                        routine Token.exit()
                          return

                        routine test()
                          var t1 = Token(id: 1)
                          var t2 = Token(id: 2)
                          using t1 as a
                            using t2 as b
                              var sum: S32 = a.id + b.id
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion

    #region Using Bound Variable Out of Scope

    /// <summary>
    /// Verifies that the variable bound by a using block is not accessible after the block closes.
    /// </summary>
    [Fact]
    public void Analyze_UsingBoundVariableOutOfScope_ReportsError()
    {
        string source = """
                        entity Data
                          value: S32

                        routine test(d: Data)
                          using d.view() as v
                            show(v.value)
                          show(v.value)
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.True(condition: result.Errors.Count > 0);
    }

    #endregion

    #region Generic Using

    /// <summary>
    /// Verifies semantic analysis behavior for generic using and binds the viewing wrapper type.
    /// </summary>
    [Fact]
    public void Analyze_GenericUsing_BindsViewedType()
    {
        // using on a generic resolution type (Container[Point]) obeying Enterable binds the entity
        // (self pass-through), and member access through it resolves.
        string source = """
                        record Point
                          x: S32
                          y: S32

                        entity Container[T] obeys Enterable
                          item: T

                        routine Container[T].enter() -> Container[T]
                          return me

                        routine Container[T].exit()
                          return

                        routine test()
                          var c = Container[Point](item: Point(x: 1, y: 2))
                          using c as p
                            var a: S32 = p.item.x
                          return
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    #endregion
}
