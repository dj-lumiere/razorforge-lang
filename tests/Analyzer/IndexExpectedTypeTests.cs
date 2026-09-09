using SyntaxTree;
using TypeModel.Types;
using Compiler.Verification.Results;

namespace RazorForge.Tests.Analyzer;

using static TestHelpers;

/// <summary>
/// Tests that integer literals inside `[...]` retype to the indexer parameter type via
/// expected-type plumbing in <c>AnalyzeIndexExpression</c>. Without the plumbing, untyped
/// integer literals default to S64 and would fail to match a U64/U32/U8 indexer parameter.
/// </summary>
public class IndexExpectedTypeTests
{
    /// <summary>Verifies integer literals retype to the U64 indexer parameter type.</summary>
    [Fact]
    public void IndexLiteral_RetypesToGetitemParamType_U64()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.$getitem(index: U64) -> S32
                        entity Bin obeys Indexable
                          size: S32
                        @readonly
                        routine Bin.$getitem(index: U64) -> S32
                          return 0_s32
                        routine probe(b: Bin) -> S32
                          return b[7]
                        """;

        AnalysisResult result = AssertAnalyzesSa(source: source);
        IndexExpression idx = FindFirstIndexExpression(result: result, routineName: "probe");
        TypeInfo? indexType = idx.Index.ResolvedType;
        Assert.NotNull(@object: indexType);
        Assert.Equal(expected: "Core.U64", actual: indexType!.FullName);
    }

    /// <summary>Verifies integer literals retype to the U32 indexer parameter type.</summary>
    [Fact]
    public void IndexLiteral_RetypesToGetitemParamType_U32()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.$getitem(slot: U32) -> S32
                        entity Reg obeys Indexable
                          size: S32
                        @readonly
                        routine Reg.$getitem(slot: U32) -> S32
                          return 0_s32
                        routine probe(r: Reg) -> S32
                          return r[5]
                        """;

        AnalysisResult result = AssertAnalyzesSa(source: source);
        IndexExpression idx = FindFirstIndexExpression(result: result, routineName: "probe");
        TypeInfo? indexType = idx.Index.ResolvedType;
        Assert.NotNull(@object: indexType);
        Assert.Equal(expected: "Core.U32", actual: indexType!.FullName);
    }

    /// <summary>Verifies that an integer literal overflowing the U8 indexer range reports a compile error.</summary>
    [Fact]
    public void IndexLiteralOverflow_OnU8Indexer_ReportsError()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.$getitem(slot: U8) -> S32
                        entity Tiny obeys Indexable
                          size: S32
                        @readonly
                        routine Tiny.$getitem(slot: U8) -> S32
                          return 0_s32
                        routine probe(t: Tiny) -> S32
                          return t[300]
                        """;

        AnalysisResult result = AnalyzeSa(source: source);
        Assert.Contains(collection: result.Errors,
            filter: e =>
                e.Code == Compiler.Diagnostics.SemanticDiagnosticCode.IntegerLiteralOverflow);
    }

    /// <summary>Verifies integer literal 0 on a U64 indexer retypes to U64 without overflow.</summary>
    [Fact]
    public void IndexLiteral_ZeroLiteral_OnU64Indexer_NoError()
    {
        string source = """
                        protocol Indexable
                          @readonly
                          routine Me.$getitem(index: U64) -> S32
                        entity Bin obeys Indexable
                          size: S32
                        @readonly
                        routine Bin.$getitem(index: U64) -> S32
                          return 0_s32
                        routine probe(b: Bin) -> S32
                          return b[0]
                        """;

        AnalysisResult result = AssertAnalyzesSa(source: source);
        Assert.Empty(collection: result.Errors);
    }

    private static IndexExpression FindFirstIndexExpression(AnalysisResult result,
        string routineName)
    {
        RoutineDeclaration? decl = result.Registry
                                         .UserPrograms
                                         .SelectMany(selector: p =>
                                              p.Program.Declarations.OfType<RoutineDeclaration>())
                                         .FirstOrDefault(predicate: d => d.Name == routineName);
        Assert.NotNull(@object: decl);

        var found = new List<IndexExpression>();
        Collect(node: decl!.Body, sink: found);
        Assert.NotEmpty(collection: found);
        return found[index: 0];
    }

    private static void Collect(object? node, List<IndexExpression> sink)
    {
        if (node == null)
        {
            return;
        }

        if (node is IndexExpression ix)
        {
            sink.Add(item: ix);
        }

        Type t = node.GetType();
        if (t.IsPrimitive || node is string || t.IsEnum)
        {
            return;
        }

        foreach (System.Reflection.PropertyInfo prop in t.GetProperties())
        {
            if (prop.GetIndexParameters()
                    .Length > 0)
            {
                continue;
            }

            object? value;
            try { value = prop.GetValue(obj: node); }
            catch { continue; }

            if (value == null)
            {
                continue;
            }

            if (value is Expression or Statement or Declaration)
            {
                Collect(node: value, sink: sink);
            }
            else if (value is System.Collections.IEnumerable en and not string)
            {
                foreach (object? item in en)
                {
                    if (item is Expression || item is Statement || item is Declaration)
                    {
                        Collect(node: item, sink: sink);
                    }
                }
            }
        }
    }
}
