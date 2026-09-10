using System.Collections;
using System.Reflection;
using TypeModel.Symbols;
using TypeModel.Types;
using Builder.Verification;
using Xunit.Abstractions;

#pragma warning disable xUnit1004
namespace RazorForge.Tests.Perf;

/// <summary>
/// DE-RISK PROBE for modular .pbrf (per-module separate-compilation artifacts). The whole design hinges on
/// ONE empirical question: when we partition the compiled-stdlib object graph by module and walk each
/// module's roots, do all cross-module reference edges terminate at a SYMBOL (TypeSymbol / RoutineInfo /
/// VariableInfo — things with a stable cross-module key), or are there INTERIOR objects reachable from ≥2
/// modules' roots without crossing a symbol boundary (identity hazards that would be duplicated across
/// artifacts and break reference identity on reload)?
///
/// This is NOT a correctness assertion — it prints the distribution and always passes. Delete once the
/// modular serializer lands and its round-trip test supersedes it.
/// </summary>
public sealed class PbrfPartitionAnalysis
{
    private readonly ITestOutputHelper _out;
    public PbrfPartitionAnalysis(ITestOutputHelper output)
    {
        _out = output;
    }

    private const string Builtin = "«builtin»"; // no Module (structural/primitive)

    private const string
        Inst = "«inst»"; // monomorphized generic instance (separate layer, Stage 4)

    // TRUE owning-module attribution: the Module FIELD (not a name-string parse). Monomorphized instances
    // (TypeArguments present) and synthetic/structural types get bucketed OUT of the base-module partition
    // into the «inst» layer, which is exactly the Stage-4 "monomorphs live separately" decision.
    private static string ModuleOf(object o)
    {
        switch (o)
        {
            case TypeSymbol t:
                if (t.TypeArguments is { Count: > 0 })
                {
                    return Inst; // monomorph / resolved generic
                }

                if (t is RoutineTypeSymbol or TupleTypeSymbol or GenericParameterTypeSymbol
                    or ProtocolSelfTypeSymbol or ConstGenericValueTypeSymbol
                    or BuildtimeConstGenericTypeSymbol or AssociatedProjectionTypeSymbol)
                {
                    return Builtin; // structural
                }

                return string.IsNullOrEmpty(value: t.Module)
                    ? Builtin
                    : t.Module!;
            case RoutineInfo r:
                if (r.TypeArguments is { Count: > 0 })
                {
                    return Inst;
                }

                TypeSymbol? owner = r.OwnerType;
                if (owner is { TypeArguments: { Count: > 0 } })
                {
                    return Inst;
                }

                string? m = owner?.Module ?? r.Module;
                return string.IsNullOrEmpty(value: m)
                    ? Builtin
                    : m!;
            default:
                return Builtin;
        }
    }

    private static bool IsSymbol(object o)
    {
        return o is TypeSymbol or RoutineInfo or VariableInfo;
    }

    private static string SymName(object o)
    {
        return o switch
        {
            TypeSymbol t => t.FullName,
            RoutineInfo r => r.RegistryKey,
            VariableInfo v => v.Name,
            _ => o.GetType()
                  .Name
        };
    }

    [Fact(Skip =
        "Manual de-risk probe for modular .pbrf (runs ~8s). Un-skip to re-measure the partition/" +
        "cycle structure; superseded by Stage-2's modular round-trip test once it lands.")]
    public void AnalyzeCrossModuleReferenceBoundary()
    {
        SemanticVerifier.CompiledStdlibState warm =
            SemanticVerifier.CaptureCompiledStdlib(language: global::TypeModel.Enums.Language
               .RazorForge);
        TypeRegistrySnapshotRoots roots = CollectRoots(warm: warm);

        _out.WriteLine(message: $"modules with roots: {roots.ByModule.Count}");
        foreach (KeyValuePair<string, List<object>> kv in roots.ByModule
                                                               .OrderByDescending(keySelector: k =>
                                                                    k.Value.Count)
                                                               .Take(count: 15))
        {
            _out.WriteLine(message: $"  {kv.Key}: {kv.Value.Count} root symbols");
        }

        // reachedBy: interior object -> first module that reached it. Symbols are cut (extern-ref), not owned.
        var reachedBy =
            new Dictionary<object, string>(comparer: ReferenceEqualityComparer.Instance);
        // extern-ref target type distribution (what we cut at)
        var externTargetTypes = new Dictionary<string, int>();
        // module -> set of modules it has extern edges TO (for dependency-DAG / cycle analysis).
        var moduleDeps = new Dictionary<string, HashSet<string>>();
        var edgeSamples = new List<string>();
        // interior objects reached by ≥2 modules (the hazard) -> type distribution
        var sharedInterior = new Dictionary<string, int>();
        var sharedExamples = new Dictionary<string, (string a, string b)>();
        var sharedSamples = new List<string>();

        foreach ((string module, List<object> symbols) in roots.ByModule)
        {
            var stack = new Stack<object>();
            foreach (object s in symbols)
            {
                stack.Push(item: s);
            }

            var localSeen = new HashSet<object>(comparer: ReferenceEqualityComparer.Instance);

            while (stack.Count > 0)
            {
                object cur = stack.Pop();
                if (!localSeen.Add(item: cur))
                {
                    continue;
                }

                foreach (object child in Neighbors(o: cur))
                {
                    // Cross-module CUT: a symbol owned by another module → extern-ref, do not recurse.
                    if (IsSymbol(o: child) && ModuleOf(o: child) is string cm && cm != module)
                    {
                        string tt = child.GetType()
                                         .Name;
                        externTargetTypes[key: tt] =
                            externTargetTypes.GetValueOrDefault(key: tt) + 1;
                        if (!moduleDeps.TryGetValue(key: module, value: out HashSet<string>? deps))
                        {
                            moduleDeps[key: module] = deps = new HashSet<string>();
                        }

                        deps.Add(item: cm);
                        // Sample suspicious Core -> {leaf module} edges: which Core symbol references what?
                        if (module == "Core" &&
                            (cm == "Random" || cm == "Subprocess" || cm == "Math3D" ||
                             cm == "Collections" || cm == "Numerics") && edgeSamples.Count < 40)
                        {
                            edgeSamples.Add(
                                item: $"Core[{SymName(o: cur)}] -> {cm}[{SymName(o: child)}]");
                        }

                        continue;
                    }

                    // A symbol owned by THIS module: it's part of the partition, recurse (but it's a root-ish).
                    // An interior object: attribute ownership; detect multi-module sharing.
                    if (!IsSymbol(o: child))
                    {
                        if (reachedBy.TryGetValue(key: child, value: out string? owner))
                        {
                            if (owner != module)
                            {
                                string tt = child.GetType()
                                                 .Name;
                                sharedInterior[key: tt] =
                                    sharedInterior.GetValueOrDefault(key: tt) + 1;
                                if (!sharedExamples.ContainsKey(key: tt))
                                {
                                    sharedExamples[key: tt] = (owner, module);
                                }

                                if (sharedSamples.Count < 20)
                                {
                                    sharedSamples.Add(item: Describe(o: child));
                                }
                            }

                            // already owned → don't recurse again from this module
                            continue;
                        }

                        reachedBy[key: child] = module;
                    }

                    stack.Push(item: child);
                }
            }
        }

        _out.WriteLine(message: "");
        _out.WriteLine(
            message: "=== EXTERN-REF TARGET TYPES (cross-module edges cut at symbols) ===");
        foreach (KeyValuePair<string, int> kv in externTargetTypes.OrderByDescending(
                     keySelector: k => k.Value))
        {
            _out.WriteLine(message: $"  {kv.Key}: {kv.Value}");
        }

        _out.WriteLine(message: "");
        _out.WriteLine(
            message:
            "=== SHARED INTERIOR OBJECTS (reached by ≥2 modules WITHOUT crossing a symbol) — HAZARDS ===");
        if (sharedInterior.Count == 0)
        {
            _out.WriteLine(
                message:
                "  NONE — the partition is CLEAN: every cross-module edge terminates at a symbol.");
        }

        foreach (KeyValuePair<string, int> kv in sharedInterior.OrderByDescending(
                     keySelector: k => k.Value))
        {
            _out.WriteLine(
                message:
                $"  {kv.Key}: {kv.Value}   e.g. shared between [{sharedExamples[key: kv.Key].a}] and [{sharedExamples[key: kv.Key].b}]");
        }

        _out.WriteLine(message: "");
        _out.WriteLine(
            message: "=== MODULE DEPENDENCY EDGES (M -> modules M extern-references) ===");
        foreach (KeyValuePair<string, HashSet<string>> kv in moduleDeps.OrderBy(
                     keySelector: k => k.Key))
        {
            _out.WriteLine(
                message:
                $"  {kv.Key} -> {string.Join(separator: ", ", values: kv.Value.OrderBy(keySelector: x => x))}");
        }

        _out.WriteLine(message: "");
        _out.WriteLine(message: "=== SUSPICIOUS Core -> leaf EDGE SAMPLES ===");
        foreach (string s in edgeSamples)
        {
            _out.WriteLine(message: $"  {s}");
        }

        List<List<string>> cycles = FindCycles(g: moduleDeps);
        _out.WriteLine(message: "");
        _out.WriteLine(message: "=== CYCLES (mutually-referencing module pairs/SCCs) ===");
        if (cycles.Count == 0)
        {
            _out.WriteLine(
                message:
                "  NONE — module dependency graph is a DAG → topological load works, no shells needed.");
        }

        foreach (List<string> c in cycles)
        {
            _out.WriteLine(message: $"  CYCLE: {string.Join(separator: " -> ", values: c)}");
        }

        _out.WriteLine(message: "");
        _out.WriteLine(message: "=== SHARED-OBJECT SAMPLES ===");
        foreach (string s in sharedSamples)
        {
            _out.WriteLine(message: $"  {s}");
        }

        _out.WriteLine(message: "");
        _out.WriteLine(message: $"total interior objects owned: {reachedBy.Count}");

        // Sanity floor for the probe: the captured stdlib must yield real roots and reach interior objects.
        Assert.True(condition: roots.ByModule.Count > 0, userMessage: "no module roots captured");
        Assert.True(condition: reachedBy.Count > 0, userMessage: "no interior objects reached");
    }

    // DFS back-edge cycle detection: returns each cycle found (as the node path). Coarse but enough to
    // answer "is the module graph a DAG".
    private static List<List<string>> FindCycles(Dictionary<string, HashSet<string>> g)
    {
        var cycles = new List<List<string>>();
        var state = new Dictionary<string, int>(); // 0 unvisited, 1 in-stack, 2 done
        var path = new List<string>();

        void Dfs(string n)
        {
            state[key: n] = 1;
            path.Add(item: n);
            if (g.TryGetValue(key: n, value: out HashSet<string>? deps))
            {
                foreach (string m in deps)
                {
                    if (!g.ContainsKey(key: m) && m != n)
                    {
                        /* leaf module, no out-edges */
                    }

                    int s = state.GetValueOrDefault(key: m);
                    if (s == 1)
                    {
                        int idx = path.IndexOf(item: m);
                        cycles.Add(item: path.GetRange(index: idx, count: path.Count - idx)
                                             .Append(element: m)
                                             .ToList());
                    }
                    else if (s == 0)
                    {
                        Dfs(n: m);
                    }
                }
            }

            state[key: n] = 2;
            path.RemoveAt(index: path.Count - 1);
        }

        foreach (string n in g.Keys.OrderBy(keySelector: x => x))
        {
            if (state.GetValueOrDefault(key: n) == 0)
            {
                Dfs(n: n);
            }
        }

        return cycles;
    }

    private static string Describe(object o)
    {
        switch (o)
        {
            case IList list:
                var elems = new List<string>();
                foreach (object? e in list)
                {
                    elems.Add(item: e?.GetType()
                                      .Name ?? "null");
                    if (elems.Count >= 4)
                    {
                        break;
                    }
                }

                return
                    $"List(count={list.Count}, elems=[{string.Join(separator: ",", values: elems)}])";
            default:
                return $"{o.GetType().Name}: {o}";
        }
    }

    private sealed class TypeRegistrySnapshotRoots
    {
        public Dictionary<string, List<object>> ByModule = new();
    }

    private static TypeRegistrySnapshotRoots CollectRoots(
        SemanticVerifier.CompiledStdlibState warm)
    {
        var r = new TypeRegistrySnapshotRoots();

        void Add(object sym)
        {
            string m = ModuleOf(o: sym);
            if (!r.ByModule.TryGetValue(key: m, value: out List<object>? list))
            {
                r.ByModule[key: m] = list = new List<object>();
            }

            list.Add(item: sym);
        }

        foreach (TypeSymbol t in warm.Registry.Types.Values)
        {
            Add(sym: t);
        }

        foreach (RoutineInfo rt in warm.Registry.Routines.Values)
        {
            Add(sym: rt);
        }

        return r;
    }

    // Reflection field walk (probe only — perf irrelevant). Skips inline values/strings; expands arrays,
    // dictionaries (keys+values), enumerables, and reference-typed fields.
    private static readonly Dictionary<Type, FieldInfo[]> _fields = new();
    private static FieldInfo[] Fields(Type t)
    {
        if (_fields.TryGetValue(key: t, value: out FieldInfo[]? c))
        {
            return c;
        }

        var list = new List<FieldInfo>();
        for (Type? x = t; x != null && x != typeof(object); x = x.BaseType)
        {
            list.AddRange(collection: x.GetFields(bindingAttr: BindingFlags.Instance |
                                                               BindingFlags.Public |
                                                               BindingFlags.NonPublic |
                                                               BindingFlags.DeclaredOnly));
        }

        return _fields[key: t] = list.ToArray();
    }

    private static IEnumerable<object> Neighbors(object o)
    {
        Type t = o.GetType();
        if (o is string || t.IsPrimitive || t.IsEnum || t == typeof(decimal))
        {
            yield break;
        }

        if (o is Array arr)
        {
            foreach (object? e in arr)
            {
                if (e != null && !IsInline(v: e))
                {
                    yield return e;
                }
            }

            yield break;
        }

        if (o is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                if (e.Key != null && !IsInline(v: e.Key))
                {
                    yield return e.Key;
                }

                if (e.Value != null && !IsInline(v: e.Value))
                {
                    yield return e.Value;
                }
            }

            yield break;
        }

        if (o is IEnumerable seq)
        {
            foreach (object? e in seq)
            {
                if (e != null && !IsInline(v: e))
                {
                    yield return e;
                }
            }

            yield break;
        }

        foreach (FieldInfo f in Fields(t: t))
        {
            object? v = f.GetValue(obj: o);
            if (v != null && !IsInline(v: v))
            {
                yield return v;
            }
        }
    }

    private static bool IsInline(object v)
    {
        Type t = v.GetType();
        return v is string || t.IsPrimitive || t.IsEnum || t == typeof(decimal);
    }
}
