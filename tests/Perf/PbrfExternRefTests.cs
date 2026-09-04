using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using Compiler.Serialization;
using Xunit;

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
    public sealed class Node
    {
        public string Name = "";
        public Node? Ref;
        public int Payload;
        public List<string> Tags = new();
    }

    private static PbrfSerializer.SymbolIdentity IdOracle(Dictionary<object, (string, string)> map) =>
        o => map.TryGetValue(o, out var id) ? id : ((string, string)?)null;

    [Fact]
    public void ModularArtifacts_ShellLoad_ResolvesCrossModuleCycle()
    {
        // A ↔ B cycle across two modules; each carries interior state (Payload + Tags list).
        var a = new Node { Name = "A", Payload = 1, Tags = { "a1", "a2" } };
        var b = new Node { Name = "B", Payload = 2, Tags = { "b1" } };
        a.Ref = b;
        b.Ref = a;

        var map = new Dictionary<object, (string, string)>(ReferenceEqualityComparer.Instance)
        {
            [a] = ("M1", "A"),
            [b] = ("M2", "B"),
        };
        PbrfSerializer.SymbolIdentity idOf = IdOracle(map);

        byte[] m1, m2;
        using (var ms = new MemoryStream()) { PbrfSerializer.SerializeModule(ms, new object[] { a }, idOf); m1 = ms.ToArray(); }
        using (var ms = new MemoryStream()) { PbrfSerializer.SerializeModule(ms, new object[] { b }, idOf); m2 = ms.ToArray(); }

        // Phase A: read both manifests, create a shell per exported symbol, register in the global table.
        var shells = new Dictionary<(string, string), object>();
        var m1Shells = new List<object>();
        var m2Shells = new List<object>();
        using (var ms = new MemoryStream(m1))
            foreach (var (key, type) in PbrfSerializer.ReadModuleManifest(ms))
            { var shell = RuntimeHelpers.GetUninitializedObject(type); shells[("M1", key)] = shell; m1Shells.Add(shell); }
        using (var ms = new MemoryStream(m2))
            foreach (var (key, type) in PbrfSerializer.ReadModuleManifest(ms))
            { var shell = RuntimeHelpers.GetUninitializedObject(type); shells[("M2", key)] = shell; m2Shells.Add(shell); }

        PbrfSerializer.ExternResolver resolver = (mod, key) => shells[(mod, key)];

        // Phase B: fill each module's shells from its graph (either order — shells all exist).
        using (var ms = new MemoryStream(m2)) PbrfSerializer.FillModuleGraph(ms, m2Shells, resolver);
        using (var ms = new MemoryStream(m1)) PbrfSerializer.FillModuleGraph(ms, m1Shells, resolver);

        var liveA = (Node)shells[("M1", "A")];
        var liveB = (Node)shells[("M2", "B")];

        // Bodies filled.
        Assert.Equal("A", liveA.Name);
        Assert.Equal(1, liveA.Payload);
        Assert.Equal(new[] { "a1", "a2" }, liveA.Tags);
        Assert.Equal("B", liveB.Name);
        Assert.Equal(new[] { "b1" }, liveB.Tags);

        // The cross-module cycle resolved to the SAME shells — reference identity across artifacts.
        Assert.Same(liveB, liveA.Ref);
        Assert.Same(liveA, liveB.Ref);
    }

    [Fact]
    public void MonolithicPath_RoundTripsGraphUnchanged()
    {
        var b = new Node { Name = "B", Payload = 2 };
        var a = new Node { Name = "A", Payload = 1, Ref = b };

        byte[] bytes;
        using (var ms = new MemoryStream()) { PbrfSerializer.Serialize(ms, a); bytes = ms.ToArray(); }

        Node restored;
        using (var ms = new MemoryStream(bytes)) restored = PbrfSerializer.Deserialize<Node>(ms);

        Assert.Equal("A", restored.Name);
        Assert.Equal("B", restored.Ref!.Name);
        Assert.Equal(2, restored.Ref.Payload);
    }
}
