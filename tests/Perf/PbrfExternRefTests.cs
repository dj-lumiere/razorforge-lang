using System.Runtime.CompilerServices;
using Builder.Serialization;

namespace RazorForge.Tests.Perf;

/// <summary>
/// Stage-1/2 unit test for the modular .pbrf per-module artifact + shell two-phase loader.
/// A hand-built graph with a CROSS-MODULE CYCLE (A in module M1 ↔ B
/// in module M2) proves: (1) each artifact serializes only its own symbol's body, with the cross-module edge
/// written as an extern; (2) phase A creates a shell per exported symbol across all artifacts; (3) phase B
/// fills the shells, and every extern resolves to the ONE shell — so the cycle links up and reference
/// identity holds across artifacts. Also proves the monolithic path still round-trips unchanged.
/// </summary>
public sealed class PbrfExternRefTests
{
    private static readonly string[] TagsA = ["a1", "a2"];
    private static readonly string[] TagsB = ["b1"];

    public sealed class Node
    {
        public string Name = "";
        public Node? Ref;
        public int Payload;
        public List<string> Tags = new();
    }

    private static PbrfSerializer.SymbolIdentity IdOracle(Dictionary<object, (string, string)> map)
    {
        return o => map.TryGetValue(key: o, value: out (string, string) id)
            ? id
            : ((string, string)?)null;
    }

    [Fact]
    public void ModularArtifacts_ShellLoad_ResolvesCrossModuleCycle()
    {
        // A ↔ B cycle across two modules; each carries interior state (Payload + Tags list).
        var a = new Node { Name = "A", Payload = 1, Tags = { "a1", "a2" } };
        var b = new Node { Name = "B", Payload = 2, Tags = { "b1" } };
        a.Ref = b;
        b.Ref = a;

        var map =
            new Dictionary<object, (string, string)>(comparer: ReferenceEqualityComparer.Instance)
            {
                [key: a] = ("M1", "A"), [key: b] = ("M2", "B")
            };
        PbrfSerializer.SymbolIdentity idOf = IdOracle(map: map);

        byte[] m1, m2;
        using (var ms = new MemoryStream())
        {
            PbrfSerializer.SerializeModule(stream: ms,
                ownedSymbols: new object[]
                {
                    a
                },
                idOf: idOf);
            m1 = ms.ToArray();
        }

        using (var ms = new MemoryStream())
        {
            PbrfSerializer.SerializeModule(stream: ms,
                ownedSymbols: new object[]
                {
                    b
                },
                idOf: idOf);
            m2 = ms.ToArray();
        }

        // Phase A: read both manifests, create a shell per exported symbol, register in the global table.
        var shells = new Dictionary<(string, string), object>();
        var m1Shells = new List<object>();
        var m2Shells = new List<object>();
        using (var ms = new MemoryStream(buffer: m1))
        {
            foreach ((string key, Type type) in PbrfSerializer.ReadModuleManifest(stream: ms))
            {
                object shell = RuntimeHelpers.GetUninitializedObject(type: type);
                shells[key: ("M1", key)] = shell;
                m1Shells.Add(item: shell);
            }
        }

        using (var ms = new MemoryStream(buffer: m2))
        {
            foreach ((string key, Type type) in PbrfSerializer.ReadModuleManifest(stream: ms))
            {
                object shell = RuntimeHelpers.GetUninitializedObject(type: type);
                shells[key: ("M2", key)] = shell;
                m2Shells.Add(item: shell);
            }
        }

        PbrfSerializer.ExternResolver resolver = (mod, key) => shells[key: (mod, key)];

        // Phase B: fill each module's shells from its graph (either order — shells all exist).
        using (var ms = new MemoryStream(buffer: m2))
        {
            PbrfSerializer.FillModuleGraph(stream: ms, shells: m2Shells, externResolver: resolver);
        }

        using (var ms = new MemoryStream(buffer: m1))
        {
            PbrfSerializer.FillModuleGraph(stream: ms, shells: m1Shells, externResolver: resolver);
        }

        var liveA = (Node)shells[key: ("M1", "A")];
        var liveB = (Node)shells[key: ("M2", "B")];

        // Bodies filled.
        Assert.Equal(expected: "A", actual: liveA.Name);
        Assert.Equal(expected: 1, actual: liveA.Payload);
        Assert.Equal(expected: TagsA, actual: liveA.Tags);
        Assert.Equal(expected: "B", actual: liveB.Name);
        Assert.Equal(expected: TagsB, actual: liveB.Tags);

        // The cross-module cycle resolved to the SAME shells — reference identity across artifacts.
        Assert.Same(expected: liveB, actual: liveA.Ref);
        Assert.Same(expected: liveA, actual: liveB.Ref);
    }

    [Fact]
    public void MonolithicPath_RoundTripsGraphUnchanged()
    {
        var b = new Node { Name = "B", Payload = 2 };
        var a = new Node { Name = "A", Payload = 1, Ref = b };

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            PbrfSerializer.Serialize(stream: ms, root: a);
            bytes = ms.ToArray();
        }

        Node restored;
        using (var ms = new MemoryStream(buffer: bytes))
        {
            restored = PbrfSerializer.Deserialize<Node>(stream: ms);
        }

        Assert.Equal(expected: "A", actual: restored.Name);
        Assert.Equal(expected: "B", actual: restored.Ref!.Name);
        Assert.Equal(expected: 2, actual: restored.Ref.Payload);
    }
}
