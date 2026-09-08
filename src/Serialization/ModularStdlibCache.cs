using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using Compiler.Instantiation;
using Compiler.Declaration;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Serialization;

/// <summary>
/// Modular (per-module) compiled-stdlib artifacts — the separate-compilation layer. Partitions one
/// <see cref="SemanticVerifier.CompiledStdlibState"/> into N per-module <c>.pbrf</c> files plus a small
/// index, and reassembles them via the shell two-phase loader so that editing one stdlib module only
/// rewrites that module's artifact.
///
/// <para>Ownership: a symbol (TypeInfo/RoutineInfo/VariableInfo) is owned by its <see cref="TypeInfo.Module"/>
/// (routine → owner's or its own module); monomorphized instances go to the <c>«inst»</c> pseudo-module and
/// structural types to <c>«builtin»</c>. Cross-module references are extern (resolved to shells); interior
/// objects (dicts, AST bodies, ParameterInfo, SourceLocation) stay local to an artifact and may duplicate —
/// proven safe by the partition de-risk (the only shared interiors are identity-insensitive leaves).</para>
/// </summary>
public static class ModularStdlibCache
{
    public const string Inst = "«inst»";
    public const string Builtin = "«builtin»";
    public const string Misc = "«misc»"; // body entries whose routine key resolves to no known module

    private const string IndexFile = "index.pbrf";

    // ---- ownership + key oracles (must match the extern idOf used at serialize time) ----------------

    internal static string ModuleOf(object o)
    {
        switch (o)
        {
            case TypeInfo t:
                if (t.TypeArguments is { Count: > 0 }) return Inst;
                if (t is RoutineTypeInfo or TupleTypeInfo or GenericParameterTypeInfo
                    or ProtocolSelfTypeInfo or ConstGenericValueTypeInfo or ComptimeConstGenericTypeInfo
                    or AssociatedProjectionTypeInfo) return Builtin;
                return string.IsNullOrEmpty(t.Module) ? Builtin : t.Module!;
            case RoutineInfo r:
                if (r.TypeArguments is { Count: > 0 }) return Inst;
                TypeInfo? owner = r.OwnerType;
                if (owner is { TypeArguments: { Count: > 0 } }) return Inst;
                string? m = owner?.Module ?? r.Module;
                return string.IsNullOrEmpty(m) ? Builtin : m!;
            case VariableInfo:
                return Builtin;
            default:
                return Builtin;
        }
    }

    private static bool IsSymbol(object o) => o is TypeInfo or RoutineInfo or VariableInfo;

    // ---- per-module container (a slice of every sliceable CompiledStdlibState/Snapshot dict) ---------

    /// <summary>One module's slice of every partitionable dictionary. Serialized as the artifact's container
    /// root; symbol values become externs, body values stay local. Reassembly is a plain union across all
    /// modules' slices, so any per-entry partition reproduces the exact original state.</summary>
    public sealed class ModuleSlice
    {
        public Dictionary<string, TypeInfo> Types = new();
        public Dictionary<string, TypeInfo> Resolutions = new();
        public Dictionary<string, WrapperTypeInfo> WrapperResolutions = new();
        public Dictionary<string, TypeInfo> EntitySpecializations = new();
        public Dictionary<string, TypeInfo> TypesByShortName = new();
        public Dictionary<string, RoutineInfo> Routines = new();
        public Dictionary<string, RoutineInfo> RoutinesByQualifiedName = new();
        public Dictionary<string, Dictionary<string, List<RoutineInfo>>> RoutinesByOwner = new();
        public Dictionary<string, RoutineInfo> RoutineResolutions = new();
        public Dictionary<string, VariableInfo> Presets = new();
        public Dictionary<string, VariableInfo> PresetsByQualifiedName = new();
        public List<ProgramEntry> StdlibPrograms = new();
        public Dictionary<string, SynthEntry> SynthesizedBodies = new();
        public Dictionary<string, Statement> VariantBodies = new();
        public Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies = new();
        public Dictionary<string, Statement> RoutineBodies = new();
        public Dictionary<string, DeferredEntry> DeferredVariantBases = new();
    }

    // ValueTuples serialize via reflection (boxed struct) which is slow + fragile for records; use plain
    // classes for the tuple-shaped entries so they go through the fast compiled-field path.
    public sealed class ProgramEntry { public Program Program = null!; public string FilePath = ""; public string Module = ""; }
    public sealed class SynthEntry { public RoutineInfo Routine = null!; public Statement Body = null!; }
    public sealed class DeferredEntry { public RoutineInfo BaseRoutine = null!; public Statement Body = null!; public bool Pessimistic; }

    /// <summary>Top-level index: global (non-sliced) metadata + the module label list.</summary>
    public sealed class Index
    {
        public Language Language;
        public HashSet<string> LoadedModules = new();
        public Dictionary<string, string> ModuleNames = new();
        public string? StdlibRootPath;
        public List<string> Modules = new(); // artifact labels (each → <label>.pbrf)
    }

    private static string ArtifactFileName(string moduleLabel)
    {
        // Sanitize the label into a filename ('/' in IO/File, '«»' sentinels).
        var sb = new System.Text.StringBuilder();
        foreach (char c in moduleLabel)
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return sb.ToString() + ".pbrf";
    }

    // ---- serialize -------------------------------------------------------------------------------

    /// <summary>Partition <paramref name="state"/> into per-module artifacts written under
    /// <paramref name="dir"/> (created if missing). Returns the module labels written.</summary>
    public static IReadOnlyList<string> Serialize(SemanticVerifier.CompiledStdlibState state, string dir)
    {
        Directory.CreateDirectory(path: dir);
        TypeRegistry.StdlibSnapshot reg = state.Registry;

        // 1. Collect every symbol reachable in the graph, bucketed by owning module (guarantees every extern
        //    has a shell), and build a routine-key → module map for attributing body-dict entries.
        var symbolsByModule = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        var keyToModule = new Dictionary<string, string>(StringComparer.Ordinal);
        // Each collected symbol gets a globally-unique id → extern identity == object identity (no reliance
        // on name uniqueness; two distinct symbols with the same FullName never collide).
        var ids = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
        CollectSymbols(state, symbolsByModule, keyToModule, ids);

        // 2. Slice every dict per module.
        var slices = new Dictionary<string, ModuleSlice>(StringComparer.Ordinal);
        SliceAllDictionaries(state, reg, keyToModule, slices);

        // 3. Every module label that has symbols OR a slice becomes an artifact.
        var labels = new HashSet<string>(symbolsByModule.Keys, StringComparer.Ordinal);
        labels.UnionWith(slices.Keys);

        PbrfSerializer.SymbolIdentity idOf = o =>
            IsSymbol(o) ? (ModuleOf(o), ids[o].ToString()) : ((string, string)?)null;

        WriteArtifacts(dir, labels, symbolsByModule, slices, idOf);

        // 4. Index.
        var index = new Index
        {
            Language = reg.Language,
            LoadedModules = new HashSet<string>(reg.LoadedModules, StringComparer.OrdinalIgnoreCase),
            ModuleNames = new Dictionary<string, string>(reg.ModuleNames, StringComparer.OrdinalIgnoreCase),
            StdlibRootPath = reg.StdlibRootPath,
            Modules = labels.ToList(),
        };
        using (var fs = File.Create(Path.Combine(dir, IndexFile)))
            PbrfSerializer.Serialize(stream: fs, root: index);

        return index.Modules;
    }

    /// <summary>Slices every partitionable dictionary of <paramref name="reg"/>/<paramref name="state"/> into
    /// per-module <see cref="ModuleSlice"/>s keyed by owning module label, populating <paramref name="slices"/>.</summary>
    private static void SliceAllDictionaries(SemanticVerifier.CompiledStdlibState state,
        TypeRegistry.StdlibSnapshot reg, Dictionary<string, string> keyToModule,
        Dictionary<string, ModuleSlice> slices)
    {
        ModuleSlice Slice(string m)
        {
            if (!slices.TryGetValue(m, out var s)) slices[m] = s = new ModuleSlice();
            return s;
        }

        foreach (var kv in reg.Types) Slice(ModuleOf(kv.Value)).Types[kv.Key] = kv.Value;
        foreach (var kv in reg.Resolutions) Slice(ModuleOf(kv.Value)).Resolutions[kv.Key] = kv.Value;
        foreach (var kv in reg.WrapperResolutions) Slice(ModuleOf(kv.Value)).WrapperResolutions[kv.Key] = kv.Value;
        foreach (var kv in reg.EntitySpecializations) Slice(ModuleOf(kv.Value)).EntitySpecializations[kv.Key] = kv.Value;
        foreach (var kv in reg.TypesByShortName) Slice(ModuleOf(kv.Value)).TypesByShortName[kv.Key] = kv.Value;
        foreach (var kv in reg.Routines) Slice(ModuleOf(kv.Value)).Routines[kv.Key] = kv.Value;
        foreach (var kv in reg.RoutinesByQualifiedName) Slice(ModuleOf(kv.Value)).RoutinesByQualifiedName[kv.Key] = kv.Value;
        foreach (var kv in reg.RoutineResolutions) Slice(ModuleOf(kv.Value)).RoutineResolutions[kv.Key] = kv.Value;
        foreach (var kv in reg.Presets) Slice(Builtin).Presets[kv.Key] = kv.Value;
        foreach (var kv in reg.PresetsByQualifiedName) Slice(Builtin).PresetsByQualifiedName[kv.Key] = kv.Value;
        foreach (var kv in reg.RoutinesByOwner)
        {
            string m = kv.Value.Values.SelectMany(l => l).Select(ModuleOf).FirstOrDefault() ?? Misc;
            Slice(m).RoutinesByOwner[kv.Key] = kv.Value;
        }

        foreach (var e in state.StdlibPrograms)
            Slice(string.IsNullOrEmpty(e.Module) ? Misc : e.Module)
                .StdlibPrograms.Add(new ProgramEntry { Program = e.Program, FilePath = e.FilePath, Module = e.Module });
        foreach (var kv in state.SynthesizedBodies)
            Slice(ModuleOf(kv.Value.Routine)).SynthesizedBodies[kv.Key] =
                new SynthEntry { Routine = kv.Value.Routine, Body = kv.Value.Body };
        foreach (var kv in state.VariantBodies)
            Slice(keyToModule.GetValueOrDefault(kv.Key, Misc)).VariantBodies[kv.Key] = kv.Value;
        foreach (var kv in state.RoutineBodies)
            Slice(keyToModule.GetValueOrDefault(kv.Key, Misc)).RoutineBodies[kv.Key] = kv.Value;
        foreach (var kv in state.InstantiatedGenericBodies)
            Slice(Inst).InstantiatedGenericBodies[kv.Key] = kv.Value;
        foreach (var kv in reg.DeferredVariantBases)
            Slice(ModuleOf(kv.Value.baseRoutine)).DeferredVariantBases[kv.Key] =
                new DeferredEntry
                {
                    BaseRoutine = kv.Value.baseRoutine, Body = kv.Value.body, Pessimistic = kv.Value.pessimistic
                };
    }

    /// <summary>Writes one <c>.pbrf</c> artifact per module label under <paramref name="dir"/>: each holds the
    /// module's owned symbols (bodies) + its dict slice, with cross-module symbol refs written as externs.</summary>
    private static void WriteArtifacts(string dir, HashSet<string> labels,
        Dictionary<string, List<object>> symbolsByModule, Dictionary<string, ModuleSlice> slices,
        PbrfSerializer.SymbolIdentity idOf)
    {
        foreach (string label in labels)
        {
            var owned = symbolsByModule.GetValueOrDefault(label) ?? new List<object>();
            ModuleSlice slice = slices.GetValueOrDefault(label) ?? new ModuleSlice();
            string path = Path.Combine(dir, ArtifactFileName(label));
            using var fs = File.Create(path);
            using var buf = new BufferedStream(fs, 1 << 20);
            PbrfSerializer.SerializeModule(stream: buf, ownedSymbols: owned, idOf: idOf, container: slice);
        }
    }

    // ---- deserialize -----------------------------------------------------------------------------

    /// <summary>Reassemble a <see cref="SemanticVerifier.CompiledStdlibState"/> from per-module artifacts in
    /// <paramref name="dir"/> via the shell two-phase loader (cycle-safe).</summary>
    public static SemanticVerifier.CompiledStdlibState Deserialize(string dir)
    {
        Index index;
        using (var fs = File.OpenRead(Path.Combine(dir, IndexFile)))
            index = PbrfSerializer.Deserialize<Index>(fs);

        // Read every artifact into memory (needed for the two passes).
        var bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string label in index.Modules)
            bytes[label] = File.ReadAllBytes(Path.Combine(dir, ArtifactFileName(label)));

        // Phase A: create a shell per exported symbol across ALL artifacts, keyed (module,key).
        var shells = new Dictionary<(string, string), object>();
        var moduleShells = new Dictionary<string, List<object>>(StringComparer.Ordinal);
        foreach (string label in index.Modules)
        {
            var list = new List<object>();
            using var ms = new MemoryStream(bytes[label], writable: false);
            foreach (var (key, type) in PbrfSerializer.ReadModuleManifest(ms))
            {
                object shell = RuntimeHelpers.GetUninitializedObject(type);
                shells[(label, key)] = shell;
                list.Add(shell);
            }
            moduleShells[label] = list;
        }

        PbrfSerializer.ExternResolver resolver = (mod, key) =>
        {
            if (shells.TryGetValue((mod, key), out var s)) return s;
            throw new InvalidDataException($"unresolved extern {mod}!{key}");
        };

        // Phase B: fill shells + read each module's container slice, then union all slices.
        var merged = new ModuleSlice();
        foreach (string label in index.Modules)
        {
            using var ms = new MemoryStream(bytes[label], writable: false);
            var slice = (ModuleSlice)PbrfSerializer.FillModuleGraph(ms, moduleShells[label], resolver)!;
            MergeInto(merged, slice);
        }

        var snapshot = new TypeRegistry.StdlibSnapshot
        {
            Language = index.Language,
            Types = merged.Types,
            Resolutions = merged.Resolutions,
            WrapperResolutions = merged.WrapperResolutions,
            EntitySpecializations = merged.EntitySpecializations,
            TypesByShortName = merged.TypesByShortName,
            Routines = merged.Routines,
            RoutinesByQualifiedName = merged.RoutinesByQualifiedName,
            RoutinesByOwner = merged.RoutinesByOwner,
            RoutineResolutions = merged.RoutineResolutions,
            Presets = merged.Presets,
            PresetsByQualifiedName = merged.PresetsByQualifiedName,
            LoadedModules = index.LoadedModules,
            ModuleNames = index.ModuleNames,
            StdlibRootPath = index.StdlibRootPath,
            DeferredVariantBases = merged.DeferredVariantBases
                .ToDictionary(kv => kv.Key, kv => (kv.Value.BaseRoutine, kv.Value.Body, kv.Value.Pessimistic)),
        };

        return new SemanticVerifier.CompiledStdlibState
        {
            Language = index.Language,
            Registry = snapshot,
            StdlibPrograms = merged.StdlibPrograms
                .Select(e => (e.Program, e.FilePath, e.Module)).ToList(),
            SynthesizedBodies = merged.SynthesizedBodies
                .ToDictionary(kv => kv.Key, kv => (kv.Value.Routine, kv.Value.Body)),
            VariantBodies = merged.VariantBodies,
            InstantiatedGenericBodies = merged.InstantiatedGenericBodies,
            RoutineBodies = merged.RoutineBodies,
        };
    }

    private static void MergeInto(ModuleSlice dst, ModuleSlice src)
    {
        foreach (var kv in src.Types) dst.Types[kv.Key] = kv.Value;
        foreach (var kv in src.Resolutions) dst.Resolutions[kv.Key] = kv.Value;
        foreach (var kv in src.WrapperResolutions) dst.WrapperResolutions[kv.Key] = kv.Value;
        foreach (var kv in src.EntitySpecializations) dst.EntitySpecializations[kv.Key] = kv.Value;
        foreach (var kv in src.TypesByShortName) dst.TypesByShortName[kv.Key] = kv.Value;
        foreach (var kv in src.Routines) dst.Routines[kv.Key] = kv.Value;
        foreach (var kv in src.RoutinesByQualifiedName) dst.RoutinesByQualifiedName[kv.Key] = kv.Value;
        foreach (var kv in src.RoutinesByOwner) dst.RoutinesByOwner[kv.Key] = kv.Value;
        foreach (var kv in src.RoutineResolutions) dst.RoutineResolutions[kv.Key] = kv.Value;
        foreach (var kv in src.Presets) dst.Presets[kv.Key] = kv.Value;
        foreach (var kv in src.PresetsByQualifiedName) dst.PresetsByQualifiedName[kv.Key] = kv.Value;
        dst.StdlibPrograms.AddRange(src.StdlibPrograms);
        foreach (var kv in src.SynthesizedBodies) dst.SynthesizedBodies[kv.Key] = kv.Value;
        foreach (var kv in src.VariantBodies) dst.VariantBodies[kv.Key] = kv.Value;
        foreach (var kv in src.InstantiatedGenericBodies) dst.InstantiatedGenericBodies[kv.Key] = kv.Value;
        foreach (var kv in src.RoutineBodies) dst.RoutineBodies[kv.Key] = kv.Value;
        foreach (var kv in src.DeferredVariantBases) dst.DeferredVariantBases[kv.Key] = kv.Value;
    }

    // ---- symbol collection (whole-graph walk) ----------------------------------------------------

    private static void CollectSymbols(SemanticVerifier.CompiledStdlibState state,
        Dictionary<string, List<object>> symbolsByModule, Dictionary<string, string> keyToModule,
        Dictionary<object, long> ids)
    {
        // Seed routine-key → module from the routine tables (for attributing body-dict entries by key).
        foreach (var kv in state.Registry.Routines) keyToModule[kv.Key] = ModuleOf(kv.Value);
        foreach (var kv in state.Registry.RoutineResolutions) keyToModule.TryAdd(kv.Key, ModuleOf(kv.Value));

        long nextId = 0;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(state);
        while (stack.Count > 0)
        {
            object cur = stack.Pop();
            foreach (object child in Neighbors(cur))
            {
                if (!seen.Add(child)) continue;
                if (IsSymbol(child))
                {
                    ids[child] = nextId++;
                    string m = ModuleOf(child);
                    if (!symbolsByModule.TryGetValue(m, out var list)) symbolsByModule[m] = list = new();
                    list.Add(child);
                }
                stack.Push(child);
            }
        }
    }

    private static readonly Dictionary<Type, FieldInfo[]> _fields = new();
    private static FieldInfo[] Fields(Type t)
    {
        if (_fields.TryGetValue(t, out var c)) return c;
        var list = new List<FieldInfo>();
        for (Type? x = t; x != null && x != typeof(object); x = x.BaseType)
            list.AddRange(x.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly));
        return _fields[t] = list.ToArray();
    }

    private static bool IsInline(object v)
    {
        Type t = v.GetType();
        return v is string || t.IsPrimitive || t.IsEnum || t == typeof(decimal);
    }

    private static IEnumerable<object> Neighbors(object o)
    {
        Type t = o.GetType();
        if (o is string || t.IsPrimitive || t.IsEnum || t == typeof(decimal)) yield break;
        if (o is Array arr)
        {
            foreach (object? e in arr) if (e != null && !IsInline(e)) yield return e;
            yield break;
        }
        if (o is IDictionary dict)
        {
            foreach (DictionaryEntry e in dict)
            {
                if (e.Key != null && !IsInline(e.Key)) yield return e.Key;
                if (e.Value != null && !IsInline(e.Value)) yield return e.Value;
            }
            yield break;
        }
        if (o is IEnumerable seq)
        {
            foreach (object? e in seq) if (e != null && !IsInline(e)) yield return e;
            yield break;
        }
        foreach (FieldInfo f in Fields(t))
        {
            object? v = f.GetValue(o);
            if (v != null && !IsInline(v)) yield return v;
        }
    }
}
