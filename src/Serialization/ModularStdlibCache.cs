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
    /// <summary>Pseudo-module label for monomorphized (instantiated) generic symbols.</summary>
    public const string Inst = "«inst»";

    /// <summary>Pseudo-module label for structural/builtin types that have no explicit module.</summary>
    public const string Builtin = "«builtin»";

    /// <summary>Pseudo-module label for body entries whose routine key cannot be attributed to any known module.</summary>
    public const string
        Misc = "«misc»"; // body entries whose routine key resolves to no known module

    private const string IndexFile = "index.pbrf";

    // ---- ownership + key oracles (must match the extern idOf used at serialize time) ----------------

    internal static string ModuleOf(object o)
    {
        switch (o)
        {
            case TypeInfo t:
                if (t.TypeArguments is { Count: > 0 })
                {
                    return Inst;
                }

                if (t is RoutineTypeInfo or TupleTypeInfo or GenericParameterTypeInfo
                    or ProtocolSelfTypeInfo or ConstGenericValueTypeInfo
                    or ComptimeConstGenericTypeInfo or AssociatedProjectionTypeInfo)
                {
                    return Builtin;
                }

                return string.IsNullOrEmpty(value: t.Module)
                    ? Builtin
                    : t.Module!;
            case RoutineInfo r:
                if (r.TypeArguments is { Count: > 0 })
                {
                    return Inst;
                }

                TypeInfo? owner = r.OwnerType;
                if (owner is { TypeArguments: { Count: > 0 } })
                {
                    return Inst;
                }

                string? m = owner?.Module ?? r.Module;
                return string.IsNullOrEmpty(value: m)
                    ? Builtin
                    : m!;
            case VariableInfo:
                return Builtin;
            default:
                return Builtin;
        }
    }

    private static bool IsSymbol(object o)
    {
        return o is TypeInfo or RoutineInfo or VariableInfo;
    }

    // ---- per-module container (a slice of every sliceable CompiledStdlibState/Snapshot dict) ---------

    /// <summary>One module's slice of every partitionable dictionary. Serialized as the artifact's container
    /// root; symbol values become externs, body values stay local. Reassembly is a plain union across all
    /// modules' slices, so any per-entry partition reproduces the exact original state.
    ///
    /// <para><see cref="PbrfSerializer"/> serializes these members via reflection in a deterministic sorted
    /// order (values only, not names); the cache is keyed by assembly hash, so any shape change regenerates
    /// it — auto-properties round-trip identically.</para></summary>
    public sealed class ModuleSlice
    {
        /// <summary>All types owned by this module, keyed by their full name.</summary>
        public Dictionary<string, TypeInfo> Types { get; set; } = new();

        /// <summary>Type resolution table (alias/short-name → canonical TypeInfo) for this module's types.</summary>
        public Dictionary<string, TypeInfo> Resolutions { get; set; } = new();

        /// <summary>RC wrapper type resolutions (name → WrapperTypeInfo) for this module.</summary>
        public Dictionary<string, WrapperTypeInfo> WrapperResolutions { get; set; } = new();

        /// <summary>Entity specialization overrides (key → TypeInfo) belonging to this module.</summary>
        public Dictionary<string, TypeInfo> EntitySpecializations { get; set; } = new();

        /// <summary>Short-name → TypeInfo lookup table for this module's types.</summary>
        public Dictionary<string, TypeInfo> TypesByShortName { get; set; } = new();

        /// <summary>All routines owned by this module, keyed by their full qualified name.</summary>
        public Dictionary<string, RoutineInfo> Routines { get; set; } = new();

        /// <summary>Routines indexed by their fully-qualified name (used for fast exact-name lookup).</summary>
        public Dictionary<string, RoutineInfo> RoutinesByQualifiedName { get; set; } = new();

        /// <summary>Routines grouped by owner type name, then by routine name, as a list of overloads.</summary>
        public Dictionary<string, Dictionary<string, List<RoutineInfo>>> RoutinesByOwner
        {
            get;
            set;
        } = new();

        /// <summary>Routine resolution table (short/alias key → RoutineInfo) for this module.</summary>
        public Dictionary<string, RoutineInfo> RoutineResolutions { get; set; } = new();

        /// <summary>Preset (constant/inline) variable declarations owned by this module.</summary>
        public Dictionary<string, VariableInfo> Presets { get; set; } = new();

        /// <summary>Presets indexed by their fully-qualified name.</summary>
        public Dictionary<string, VariableInfo> PresetsByQualifiedName { get; set; } = new();

        /// <summary>Stdlib program AST entries (one per source file) attributed to this module.</summary>
        public List<ProgramEntry> StdlibPrograms { get; set; } = new();

        /// <summary>Synthesized routine bodies (wired/$represent/$diagnose/derive) produced for this module's types.</summary>
        public Dictionary<string, SynthEntry> SynthesizedBodies { get; set; } = new();

        /// <summary>Error-variant routine bodies (try_/check_/lookup_ wrappers) attributed to this module.</summary>
        public Dictionary<string, Statement> VariantBodies { get; set; } = new();

        /// <summary>Monomorphized generic routine bodies attributed to the inst pseudo-module.</summary>
        public Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; set; } =
            new();

        /// <summary>Regular routine bodies attributed to this module.</summary>
        public Dictionary<string, Statement> RoutineBodies { get; set; } = new();

        /// <summary>Deferred error-variant base bodies awaiting specialization, attributed to this module.</summary>
        public Dictionary<string, DeferredEntry> DeferredVariantBases { get; set; } = new();
    }

    // ValueTuples serialize via reflection (boxed struct) which is slow + fragile for records; use plain
    // classes for the tuple-shaped entries so they go through the fast compiled-field path.

    /// <summary>Holds the parsed AST and metadata for one stdlib source file.</summary>
    public sealed class ProgramEntry
    {
        /// <summary>The parsed program AST for the stdlib source file.</summary>
        public Program Program { get; set; } = null!;

        /// <summary>Absolute path of the stdlib source file on disk.</summary>
        public string FilePath { get; set; } = "";

        /// <summary>Module label that owns this source file (e.g. "Core", "Collections/List").</summary>
        public string Module { get; set; } = "";
    }

    /// <summary>Holds a synthesized routine body together with its owning routine descriptor.</summary>
    public sealed class SynthEntry
    {
        /// <summary>The routine descriptor that owns this synthesized body.</summary>
        public RoutineInfo Routine { get; set; } = null!;

        /// <summary>The synthesized body statement (a block or lowered form).</summary>
        public Statement Body { get; set; } = null!;
    }

    /// <summary>Holds a deferred error-variant base body awaiting per-concrete-type specialization.</summary>
    public sealed class DeferredEntry
    {
        /// <summary>The base routine descriptor from which the variant is specialized.</summary>
        public RoutineInfo BaseRoutine { get; set; } = null!;

        /// <summary>The unspecialized body statement carried until specialization time.</summary>
        public Statement Body { get; set; } = null!;

        /// <summary>True if this deferred body was produced under pessimistic (check_) semantics.</summary>
        public bool Pessimistic { get; set; }
    }

    /// <summary>Top-level index: global (non-sliced) metadata + the module label list.</summary>
    public sealed class Index
    {
        /// <summary>The realm/language (RazorForge or Suflae) that these stdlib artifacts were compiled for.</summary>
        public Language Language { get; set; }

        /// <summary>Set of module labels that were loaded during the compilation that produced these artifacts.</summary>
        public HashSet<string> LoadedModules { get; set; } = new();

        /// <summary>Mapping from module path to canonical module name, as recorded at analysis time.</summary>
        public Dictionary<string, string> ModuleNames { get; set; } = new();

        /// <summary>Absolute path to the stdlib root directory used when these artifacts were produced.</summary>
        public string? StdlibRootPath { get; set; }

        /// <summary>Artifact labels present in this cache directory (each maps to a &lt;label&gt;.pbrf file).</summary>
        public List<string> Modules { get; set; } = new(); // artifact labels (each → <label>.pbrf)
    }

    private static string ArtifactFileName(string moduleLabel)
    {
        // Sanitize the label into a filename ('/' in IO/File, '«»' sentinels).
        var sb = new System.Text.StringBuilder();
        foreach (char c in moduleLabel)
        {
            sb.Append(value: char.IsLetterOrDigit(c: c) || c is '.' or '-' or '_'
                ? c
                : '_');
        }

        return sb.ToString() + ".pbrf";
    }

    // ---- serialize -------------------------------------------------------------------------------

    /// <summary>Partition <paramref name="state"/> into per-module artifacts written under
    /// <paramref name="dir"/> (created if missing). Returns the module labels written.</summary>
    public static IReadOnlyList<string> Serialize(SemanticVerifier.CompiledStdlibState state,
        string dir)
    {
        Directory.CreateDirectory(path: dir);
        TypeRegistry.StdlibSnapshot reg = state.Registry;

        // 1. Collect every symbol reachable in the graph, bucketed by owning module (guarantees every extern
        //    has a shell), and build a routine-key → module map for attributing body-dict entries.
        var symbolsByModule =
            new Dictionary<string, List<object>>(comparer: StringComparer.Ordinal);
        var keyToModule = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        // Each collected symbol gets a globally-unique id → extern identity == object identity (no reliance
        // on name uniqueness; two distinct symbols with the same FullName never collide).
        var ids = new Dictionary<object, long>(comparer: ReferenceEqualityComparer.Instance);
        CollectSymbols(state: state,
            symbolsByModule: symbolsByModule,
            keyToModule: keyToModule,
            ids: ids);

        // 2. Slice every dict per module.
        var slices = new Dictionary<string, ModuleSlice>(comparer: StringComparer.Ordinal);
        SliceAllDictionaries(state: state,
            reg: reg,
            keyToModule: keyToModule,
            slices: slices);

        // 3. Every module label that has symbols OR a slice becomes an artifact.
        var labels = new HashSet<string>(collection: symbolsByModule.Keys,
            comparer: StringComparer.Ordinal);
        labels.UnionWith(other: slices.Keys);

        PbrfSerializer.SymbolIdentity idOf = o => IsSymbol(o: o)
            ? (ModuleOf(o: o), ids[key: o]
               .ToString())
            : ((string, string)?)null;

        WriteArtifacts(dir: dir,
            labels: labels,
            symbolsByModule: symbolsByModule,
            slices: slices,
            idOf: idOf);

        // 4. Index.
        var index = new Index
        {
            Language = reg.Language,
            LoadedModules =
                new HashSet<string>(collection: reg.LoadedModules,
                    comparer: StringComparer.OrdinalIgnoreCase),
            ModuleNames =
                new Dictionary<string, string>(dictionary: reg.ModuleNames,
                    comparer: StringComparer.OrdinalIgnoreCase),
            StdlibRootPath = reg.StdlibRootPath,
            Modules = labels.ToList()
        };
        using (FileStream fs = File.Create(path: Path.Combine(path1: dir, path2: IndexFile)))
        {
            PbrfSerializer.Serialize(stream: fs, root: index);
        }

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
            if (!slices.TryGetValue(key: m, value: out ModuleSlice? s))
            {
                slices[key: m] = s = new ModuleSlice();
            }

            return s;
        }

        SliceRegistryDictionaries(reg: reg, getSlice: Slice);
        SliceBodyDictionaries(state: state, keyToModule: keyToModule, getSlice: Slice);
    }

    /// <summary>Slices the type-registry symbol dictionaries from <paramref name="reg"/> into per-module slices
    /// using the <paramref name="getSlice"/> factory.</summary>
    private static void SliceRegistryDictionaries(TypeRegistry.StdlibSnapshot reg,
        Func<string, ModuleSlice> getSlice)
    {
        foreach (KeyValuePair<string, TypeInfo> kv in reg.Types)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .Types[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, TypeInfo> kv in reg.Resolutions)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .Resolutions[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, WrapperTypeInfo> kv in reg.WrapperResolutions)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .WrapperResolutions[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, TypeInfo> kv in reg.EntitySpecializations)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .EntitySpecializations[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, TypeInfo> kv in reg.TypesByShortName)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .TypesByShortName[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, RoutineInfo> kv in reg.Routines)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .Routines[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, RoutineInfo> kv in reg.RoutinesByQualifiedName)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .RoutinesByQualifiedName[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, RoutineInfo> kv in reg.RoutineResolutions)
        {
            getSlice(arg: ModuleOf(o: kv.Value))
               .RoutineResolutions[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, VariableInfo> kv in reg.Presets)
        {
            getSlice(arg: Builtin)
               .Presets[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, VariableInfo> kv in reg.PresetsByQualifiedName)
        {
            getSlice(arg: Builtin)
               .PresetsByQualifiedName[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Dictionary<string, List<RoutineInfo>>> kv in reg
                    .RoutinesByOwner)
        {
            string m = kv.Value
                         .Values
                         .SelectMany(selector: l => l)
                         .Select(selector: ModuleOf)
                         .FirstOrDefault() ?? Misc;
            getSlice(arg: m)
               .RoutinesByOwner[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, (RoutineInfo baseRoutine, Statement body, bool pessimistic)>
                     kv in reg.DeferredVariantBases)
        {
            getSlice(arg: ModuleOf(o: kv.Value.baseRoutine))
               .DeferredVariantBases[key: kv.Key] = new DeferredEntry
            {
                BaseRoutine = kv.Value.baseRoutine,
                Body = kv.Value.body,
                Pessimistic = kv.Value.pessimistic
            };
        }
    }

    /// <summary>Slices the body dictionaries from <paramref name="state"/> into per-module slices
    /// using the <paramref name="getSlice"/> factory.</summary>
    private static void SliceBodyDictionaries(SemanticVerifier.CompiledStdlibState state,
        Dictionary<string, string> keyToModule, Func<string, ModuleSlice> getSlice)
    {
        foreach ((Program Program, string FilePath, string Module) e in state.StdlibPrograms)
        {
            getSlice(arg: string.IsNullOrEmpty(value: e.Module)
                    ? Misc
                    : e.Module)
               .StdlibPrograms
               .Add(item: new ProgramEntry
                {
                    Program = e.Program, FilePath = e.FilePath, Module = e.Module
                });
        }

        foreach (KeyValuePair<string, (RoutineInfo Routine, Statement Body)> kv in state
                    .SynthesizedBodies)
        {
            getSlice(arg: ModuleOf(o: kv.Value.Routine))
                   .SynthesizedBodies[key: kv.Key] =
                new SynthEntry { Routine = kv.Value.Routine, Body = kv.Value.Body };
        }

        foreach (KeyValuePair<string, Statement> kv in state.VariantBodies)
        {
            getSlice(arg: keyToModule.GetValueOrDefault(key: kv.Key, defaultValue: Misc))
               .VariantBodies[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Statement> kv in state.RoutineBodies)
        {
            getSlice(arg: keyToModule.GetValueOrDefault(key: kv.Key, defaultValue: Misc))
               .RoutineBodies[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, MonomorphizedBody> kv in state.InstantiatedGenericBodies)
        {
            getSlice(arg: Inst)
               .InstantiatedGenericBodies[key: kv.Key] = kv.Value;
        }
    }

    /// <summary>Writes one <c>.pbrf</c> artifact per module label under <paramref name="dir"/>: each holds the
    /// module's owned symbols (bodies) + its dict slice, with cross-module symbol refs written as externs.</summary>
    private static void WriteArtifacts(string dir, HashSet<string> labels,
        Dictionary<string, List<object>> symbolsByModule, Dictionary<string, ModuleSlice> slices,
        PbrfSerializer.SymbolIdentity idOf)
    {
        foreach (string label in labels)
        {
            List<object> owned =
                symbolsByModule.GetValueOrDefault(key: label) ?? new List<object>();
            ModuleSlice slice = slices.GetValueOrDefault(key: label) ?? new ModuleSlice();
            string path = Path.Combine(path1: dir, path2: ArtifactFileName(moduleLabel: label));
            using FileStream fs = File.Create(path: path);
            using var buf = new BufferedStream(stream: fs, bufferSize: 1 << 20);
            PbrfSerializer.SerializeModule(stream: buf,
                ownedSymbols: owned,
                idOf: idOf,
                container: slice);
        }
    }

    // ---- deserialize -----------------------------------------------------------------------------

    /// <summary>Reassemble a <see cref="SemanticVerifier.CompiledStdlibState"/> from per-module artifacts in
    /// <paramref name="dir"/> via the shell two-phase loader (cycle-safe).</summary>
    public static SemanticVerifier.CompiledStdlibState Deserialize(string dir)
    {
        Index index;
        using (FileStream fs = File.OpenRead(path: Path.Combine(path1: dir, path2: IndexFile)))
        {
            index = PbrfSerializer.Deserialize<Index>(stream: fs);
        }

        // Read every artifact into memory (needed for the two passes).
        var bytes = new Dictionary<string, byte[]>(comparer: StringComparer.Ordinal);
        foreach (string label in index.Modules)
        {
            bytes[key: label] = File.ReadAllBytes(path: Path.Combine(path1: dir,
                path2: ArtifactFileName(moduleLabel: label)));
        }

        // Phase A: create a shell per exported symbol across ALL artifacts, keyed (module,key).
        var shells = new Dictionary<(string, string), object>();
        var moduleShells = new Dictionary<string, List<object>>(comparer: StringComparer.Ordinal);
        foreach (string label in index.Modules)
        {
            var list = new List<object>();
            using var ms = new MemoryStream(buffer: bytes[key: label], writable: false);
            foreach ((string key, Type type) in PbrfSerializer.ReadModuleManifest(stream: ms))
            {
                object shell = RuntimeHelpers.GetUninitializedObject(type: type);
                shells[key: (label, key)] = shell;
                list.Add(item: shell);
            }

            moduleShells[key: label] = list;
        }

        PbrfSerializer.ExternResolver resolver = (mod, key) =>
        {
            if (shells.TryGetValue(key: (mod, key), value: out object? s))
            {
                return s;
            }

            throw new InvalidDataException(message: $"unresolved extern {mod}!{key}");
        };

        // Phase B: fill shells + read each module's container slice, then union all slices.
        var merged = new ModuleSlice();
        foreach (string label in index.Modules)
        {
            using var ms = new MemoryStream(buffer: bytes[key: label], writable: false);
            var slice = (ModuleSlice)PbrfSerializer.FillModuleGraph(stream: ms,
                shells: moduleShells[key: label],
                externResolver: resolver)!;
            MergeInto(dst: merged, src: slice);
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
            DeferredVariantBases = merged.DeferredVariantBases.ToDictionary(
                keySelector: kv => kv.Key,
                elementSelector: kv =>
                    (kv.Value.BaseRoutine, kv.Value.Body, kv.Value.Pessimistic))
        };

        return new SemanticVerifier.CompiledStdlibState
        {
            Language = index.Language,
            Registry = snapshot,
            StdlibPrograms = merged.StdlibPrograms
                                   .Select(selector: e => (e.Program, e.FilePath, e.Module))
                                   .ToList(),
            SynthesizedBodies =
                merged.SynthesizedBodies.ToDictionary(keySelector: kv => kv.Key,
                    elementSelector: kv => (kv.Value.Routine, kv.Value.Body)),
            VariantBodies = merged.VariantBodies,
            InstantiatedGenericBodies = merged.InstantiatedGenericBodies,
            RoutineBodies = merged.RoutineBodies
        };
    }

    private static void MergeInto(ModuleSlice dst, ModuleSlice src)
    {
        MergeTypeDictionaries(dst: dst, src: src);
        MergeRoutineDictionaries(dst: dst, src: src);
        MergeBodyDictionaries(dst: dst, src: src);
    }

    /// <summary>Merges type-related dictionary slices from <paramref name="src"/> into <paramref name="dst"/>.</summary>
    private static void MergeTypeDictionaries(ModuleSlice dst, ModuleSlice src)
    {
        foreach (KeyValuePair<string, TypeInfo> kv in src.Types)
        {
            dst.Types[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, TypeInfo> kv in src.Resolutions)
        {
            dst.Resolutions[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, WrapperTypeInfo> kv in src.WrapperResolutions)
        {
            dst.WrapperResolutions[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, TypeInfo> kv in src.EntitySpecializations)
        {
            dst.EntitySpecializations[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, TypeInfo> kv in src.TypesByShortName)
        {
            dst.TypesByShortName[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, VariableInfo> kv in src.Presets)
        {
            dst.Presets[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, VariableInfo> kv in src.PresetsByQualifiedName)
        {
            dst.PresetsByQualifiedName[key: kv.Key] = kv.Value;
        }
    }

    /// <summary>Merges routine-related dictionary slices from <paramref name="src"/> into <paramref name="dst"/>.</summary>
    private static void MergeRoutineDictionaries(ModuleSlice dst, ModuleSlice src)
    {
        foreach (KeyValuePair<string, RoutineInfo> kv in src.Routines)
        {
            dst.Routines[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, RoutineInfo> kv in src.RoutinesByQualifiedName)
        {
            dst.RoutinesByQualifiedName[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Dictionary<string, List<RoutineInfo>>> kv in src
                    .RoutinesByOwner)
        {
            dst.RoutinesByOwner[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, RoutineInfo> kv in src.RoutineResolutions)
        {
            dst.RoutineResolutions[key: kv.Key] = kv.Value;
        }
    }

    /// <summary>Merges body and program-entry slices from <paramref name="src"/> into <paramref name="dst"/>.</summary>
    private static void MergeBodyDictionaries(ModuleSlice dst, ModuleSlice src)
    {
        dst.StdlibPrograms.AddRange(collection: src.StdlibPrograms);
        foreach (KeyValuePair<string, SynthEntry> kv in src.SynthesizedBodies)
        {
            dst.SynthesizedBodies[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Statement> kv in src.VariantBodies)
        {
            dst.VariantBodies[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, MonomorphizedBody> kv in src.InstantiatedGenericBodies)
        {
            dst.InstantiatedGenericBodies[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, Statement> kv in src.RoutineBodies)
        {
            dst.RoutineBodies[key: kv.Key] = kv.Value;
        }

        foreach (KeyValuePair<string, DeferredEntry> kv in src.DeferredVariantBases)
        {
            dst.DeferredVariantBases[key: kv.Key] = kv.Value;
        }
    }

    // ---- symbol collection (whole-graph walk) ----------------------------------------------------

    private static void CollectSymbols(SemanticVerifier.CompiledStdlibState state,
        Dictionary<string, List<object>> symbolsByModule, Dictionary<string, string> keyToModule,
        Dictionary<object, long> ids)
    {
        // Seed routine-key → module from the routine tables (for attributing body-dict entries by key).
        foreach (KeyValuePair<string, RoutineInfo> kv in state.Registry.Routines)
        {
            keyToModule[key: kv.Key] = ModuleOf(o: kv.Value);
        }

        foreach (KeyValuePair<string, RoutineInfo> kv in state.Registry.RoutineResolutions)
        {
            keyToModule.TryAdd(key: kv.Key, value: ModuleOf(o: kv.Value));
        }

        long nextId = 0;
        var seen = new HashSet<object>(comparer: ReferenceEqualityComparer.Instance);
        var stack = new Stack<object>();
        stack.Push(item: state);
        while (stack.Count > 0)
        {
            object cur = stack.Pop();
            foreach (object child in Neighbors(o: cur))
            {
                if (!seen.Add(item: child))
                {
                    continue;
                }

                if (IsSymbol(o: child))
                {
                    ids[key: child] = nextId++;
                    string m = ModuleOf(o: child);
                    if (!symbolsByModule.TryGetValue(key: m, value: out List<object>? list))
                    {
                        symbolsByModule[key: m] = list = new List<object>();
                    }

                    list.Add(item: child);
                }

                stack.Push(item: child);
            }
        }
    }

    private static readonly Dictionary<Type, FieldInfo[]> _fields = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(category: "csharpsquid", checkId: "S3011",
        Justification =
            "The serializer intentionally reflects over its own private instance fields to persist " +
            "internal compiler types; the reflected types are internal and never attacker-supplied.")]
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

    private static bool IsInline(object v)
    {
        Type t = v.GetType();
        return v is string || t.IsPrimitive || t.IsEnum || t == typeof(decimal);
    }

    private static IEnumerable<object> Neighbors(object o)
    {
        Type t = o.GetType();
        if (IsInline(v: o))
        {
            yield break;
        }

        if (o is IDictionary dict)
        {
            foreach (object neighbor in NeighborsDictionary(dict: dict))
            {
                yield return neighbor;
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

    /// <summary>Yields non-inline keys and values from a dictionary for graph traversal.</summary>
    private static IEnumerable<object> NeighborsDictionary(IDictionary dict)
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
    }
}
