using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;

namespace Compiler.Serialization;

/// <summary>
/// A general reflection-based binary graph serializer for the RazorForge compiled-stdlib snapshot
/// (<c>.pbrf</c> — "prebuilt razorforge"). It serializes the whole in-memory semantic model
/// (TypeInfo/RoutineInfo symbol tables + lowered AST bodies) so a cold compile can DESERIALIZE it
/// instead of re-analyzing the stdlib from scratch (~5 s).
///
/// <para>Design (why custom binary, not System.Text.Json/Newtonsoft):</para>
/// <list type="bullet">
///   <item><b>Serializes FIELDS, not properties</b> — the domain types carry dozens of COMPUTED
///     properties (FullName, RegistryKey, IsGenericDefinition, …) with no backing field; serializing
///     fields excludes them automatically. Static fields (incl. the <c>[ThreadStatic]</c> entity
///     cycle-detection maps) are skipped, so no runtime state leaks in.</item>
///   <item><b><see cref="RuntimeHelpers.GetUninitializedObject"/></b> — reconstructs objects with NO
///     constructor and NO field initializers, sidestepping ctor-cycles and get-only ctor-set props
///     (e.g. <c>Name</c>). The object is registered in the id-table BEFORE its fields are read, so
///     cyclic/shared references (GenericDefinition back-links, an AST node's ResolvedRoutine pointing
///     at the same RoutineInfo the registry holds) resolve to the SAME instance.</item>
///   <item><b>Object-id table</b> preserves reference identity and handles cycles; a <b>type table</b>
///     interns concrete types so polymorphism (22 TypeInfo subtypes, ~100 AST nodes) costs one id.</item>
/// </list>
/// Format is versioned (<see cref="FormatVersion"/>); the cache key (stdlib content + assembly hash) is
/// the caller's responsibility (<c>StdlibSnapshotCache</c>).
/// </summary>
public static class PbrfSerializer
{
    private const uint Magic = 0x46524250; // "PBRF"
    internal const int FormatVersion = 2;

    // Value-slot kinds for reference-typed slots.
    private const byte KindNull = 0;
    private const byte KindRef = 1; // existing object → id follows
    private const byte KindNew = 2; // new object → id, typeId, payload follow
    private const byte KindBoxed = 3; // boxed value type in an object/interface slot

    private const byte
        KindExtern = 4; // symbol owned by ANOTHER module → (moduleKey, stableKey) follow

    /// <summary>Identity of a cross-module SYMBOL (TypeInfo/RoutineInfo/VariableInfo): its owning module +
    /// a stable key unique within the whole program. Returned by the modular serializer's oracle for any
    /// symbol object; <c>null</c> for interior objects (which serialize locally and may duplicate across
    /// artifacts — proven safe by the partition de-risk: the only shared interiors are identity-insensitive
    /// immutable leaves).</summary>
    public delegate (string module, string key)? SymbolIdentity(object value);

    /// <summary>Resolves a cross-module extern reference back to the live symbol instance, against the shell
    /// table populated in phase A across ALL module artifacts (so cyclic cross-module references resolve).</summary>
    public delegate object ExternResolver(string module, string key);

    /// <summary>Monolithic serialize (whole graph, no cross-module externs) — byte-identical to before.</summary>
    public static void Serialize(Stream stream, object root)
    {
        var w = new Writer(stream: stream, symbolIdentity: null);
        w.WriteHeader();
        w.WriteValue(value: root, declaredType: typeof(object));
        w.Flush();
    }

    /// <summary>Monolithic deserialize (no externs expected in the stream).</summary>
    public static T Deserialize<T>(Stream stream)
    {
        var r = new Reader(stream: stream, externResolver: null);
        r.ReadHeader();
        return (T)r.ReadValue(declaredType: typeof(object))!;
    }

    // ---- modular (per-module artifact) API ------------------------------------------------------
    // A module artifact has four segments: header, manifest-length prefix, manifest, and graph body.
    // The manifest lists this module's owned symbols as (stableKey, assemblyQualifiedTypeName) — read
    // in phase A to create GetUninitializedObject shells registered in a global (module,key)->shell table
    // BEFORE any graph is filled, so cyclic cross-module references resolve. The graph holds, in manifest
    // order, each owned symbol's field body; every symbol reference inside a body is written as an extern
    // (module,key) — never inline — so a symbol has exactly one instance (its shell) shared across all
    // artifacts. Interior (non-symbol) objects stay local to the artifact and may duplicate across
    // artifacts (proven safe by the partition de-risk).

    /// <summary>Serialize one module artifact: its owned symbols' bodies + an optional CONTAINER root (this
    /// module's dict slices / bodies), + a manifest. <paramref name="idOf"/> returns (module,key) for any
    /// symbol, null for interior objects. The container's symbol references become externs (resolved to
    /// shells on load); its interior objects (dicts, AST bodies) serialize locally to this artifact.</summary>
    public static void SerializeModule(Stream stream, IReadOnlyList<object> ownedSymbols,
        SymbolIdentity idOf, object? container = null)
    {
        // Manifest to a temp buffer first (its length prefixes the graph so phase A can stop after it).
        byte[] manifestBytes;
        using (var mm = new MemoryStream())
        {
            using var mbw = new BinaryWriter(output: mm, encoding: Encoding.UTF8, leaveOpen: true);
            mbw.Write7BitEncodedInt(value: ownedSymbols.Count);
            foreach (object sym in ownedSymbols)
            {
                (string _, string key) = idOf(value: sym) ??
                                         throw new InvalidOperationException(
                                             message:
                                             $"owned symbol {sym.GetType().Name} has no SymbolIdentity");
                mbw.Write(value: key);
                mbw.Write(value: sym.GetType()
                                    .AssemblyQualifiedName ?? sym.GetType()
                   .FullName!);
            }

            mbw.Flush();
            manifestBytes = mm.ToArray();
        }

        var bw = new BinaryWriter(output: stream, encoding: Encoding.UTF8, leaveOpen: true);
        bw.Write(value: Magic);
        bw.Write(value: FormatVersion);
        bw.Write7BitEncodedInt(value: manifestBytes.Length);
        bw.Write(buffer: manifestBytes);
        bw.Flush();

        // Graph: each owned symbol's field body (manifest order), then the container root. Symbol refs → extern.
        var w = new Writer(stream: stream, symbolIdentity: idOf);
        foreach (object sym in ownedSymbols)
        {
            w.WriteSymbolFields(symbol: sym);
        }

        w.WriteValue(value: container, declaredType: typeof(object));
        w.Flush();
    }

    /// <summary>Phase A: read a module artifact's manifest → its owned symbols' (key, concreteType). The
    /// caller creates a shell per entry and registers it before phase B fills any graph.</summary>
    public static IReadOnlyList<(string key, Type type)> ReadModuleManifest(Stream stream)
    {
        var br = new BinaryReader(input: stream, encoding: Encoding.UTF8, leaveOpen: true);
        if (br.ReadUInt32() != Magic)
        {
            throw new InvalidDataException(message: "Not a .pbrf module (bad magic).");
        }

        int ver = br.ReadInt32();
        if (ver != FormatVersion)
        {
            throw new InvalidDataException(
                message: $".pbrf module version {ver} != expected {FormatVersion}.");
        }

        int manifestLen = br.Read7BitEncodedInt();
        byte[] manifestBytes = br.ReadBytes(count: manifestLen);
        var list = new List<(string, Type)>();
        using var mm = new MemoryStream(buffer: manifestBytes, writable: false);
        using var mbr = new BinaryReader(input: mm, encoding: Encoding.UTF8);
        int count = mbr.Read7BitEncodedInt();
        for (int i = 0; i < count; i++)
        {
            string key = mbr.ReadString();
            var type = Type.GetType(typeName: mbr.ReadString(), throwOnError: true)!;
            list.Add(item: (key, type));
        }

        return list;
    }

    /// <summary>Phase B: fill this module's pre-created shells from the graph. <paramref name="stream"/> must
    /// be positioned at the start of the artifact (the manifest is skipped internally). <paramref name="shells"/>
    /// are this module's shells in manifest order; <paramref name="externResolver"/> resolves every symbol
    /// reference (this module's or another's) against the global shell table.</summary>
    public static object? FillModuleGraph(Stream stream, IReadOnlyList<object> shells,
        ExternResolver externResolver)
    {
        var br = new BinaryReader(input: stream, encoding: Encoding.UTF8, leaveOpen: true);
        br.ReadUInt32(); // magic
        br.ReadInt32(); // version
        int manifestLen = br.Read7BitEncodedInt();
        br.ReadBytes(count: manifestLen); // skip manifest (already read in phase A)

        var r = new Reader(stream: stream, externResolver: externResolver);
        foreach (object shell in shells)
        {
            r.FillSymbolFields(shell: shell);
        }

        return r.ReadValue(declaredType: typeof(object)); // the container root
    }

    // ---- shared field model ---------------------------------------------------------------------

    private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new();

    /// <summary>Instance fields of <paramref name="type"/> (all levels, public+nonpublic), deterministically
    /// ordered. Static fields are excluded — so the entity <c>[ThreadStatic]</c> maps never serialize.</summary>
    internal static FieldInfo[] FieldsOf(Type type)
    {
        if (_fieldCache.TryGetValue(key: type, value: out FieldInfo[]? cached))
        {
            return cached;
        }

        var fields = new List<FieldInfo>();
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            fields.AddRange(collection: t.GetFields(bindingAttr: BindingFlags.Instance |
                                                                 BindingFlags.Public |
                                                                 BindingFlags.NonPublic |
                                                                 BindingFlags.DeclaredOnly));
        }

        // Deterministic order independent of reflection's return order: by declaring-type name then field name.
        FieldInfo[] ordered = fields.OrderBy(keySelector: f => f.DeclaringType!.FullName,
                                         comparer: StringComparer.Ordinal)
                                    .ThenBy(keySelector: f => f.Name,
                                         comparer: StringComparer.Ordinal)
                                    .ToArray();
        _fieldCache[key: type] = ordered;
        return ordered;
    }

    // Compiled field accessors — the reflection HOT PATH (FieldInfo.Get/SetValue per field × hundreds of
    // thousands of objects) is what made deserialize slow. We keep reflection for field DISCOVERY (FieldsOf,
    // cheap + cached) but emit a DynamicMethod get/set per field ONCE (cached), so the per-object field
    // access is a direct IL call. `skipVisibility` + `stfld` also lets us set readonly (init-only / positional
    // record / get-only auto-prop) backing fields, which Expression.Assign can't. Only used for REFERENCE
    // declaring types (the whole TypeInfo/RoutineInfo/AST graph); struct fields (ValueTuple) stay on
    // reflection since a boxed struct can't be mutated through a compiled setter.
    /// <summary>One field's compiled get/set + its declared type. Precomputed per owning type in
    /// <see cref="FieldPlan"/> so the hot loop iterates a flat array with NO per-access dictionary lookup
    /// (a FieldInfo-keyed lookup per field access was itself as slow as the reflection it replaced).</summary>
    internal readonly struct FieldEntry(
        Func<object, object?> get,
        Action<object, object?> set,
        Type fieldType)
    {
        public readonly Func<object, object?> Get = get;
        public readonly Action<object, object?> Set = set;
        public readonly Type FieldType = fieldType;
    }

    private static readonly Dictionary<Type, FieldEntry[]> _plans = new();

    internal static FieldEntry[] FieldPlan(Type type)
    {
        if (_plans.TryGetValue(key: type, value: out FieldEntry[]? plan))
        {
            return plan;
        }

        FieldInfo[] fields = FieldsOf(type: type);
        plan = new FieldEntry[fields.Length];
        for (int i = 0; i < fields.Length; i++)
        {
            plan[i] = new FieldEntry(get: BuildGetter(f: fields[i]),
                set: BuildSetter(f: fields[i]),
                fieldType: fields[i].FieldType);
        }

        _plans[key: type] = plan;
        return plan;
    }

    private static Func<object, object?> BuildGetter(FieldInfo f)
    {
        var dm = new DynamicMethod(name: "pbrf_get",
            returnType: typeof(object),
            parameterTypes: [typeof(object)],
            m: f.DeclaringType!.Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(opcode: OpCodes.Ldarg_0);
        il.Emit(opcode: OpCodes.Castclass, cls: f.DeclaringType!);
        il.Emit(opcode: OpCodes.Ldfld, field: f);
        if (f.FieldType.IsValueType)
        {
            il.Emit(opcode: OpCodes.Box, cls: f.FieldType);
        }

        il.Emit(opcode: OpCodes.Ret);
        return (Func<object, object?>)dm.CreateDelegate(
            delegateType: typeof(Func<object, object?>));
    }

    private static Action<object, object?> BuildSetter(FieldInfo f)
    {
        var dm = new DynamicMethod(name: "pbrf_set",
            returnType: null,
            parameterTypes: [typeof(object), typeof(object)],
            m: f.DeclaringType!.Module,
            skipVisibility: true);
        ILGenerator il = dm.GetILGenerator();
        il.Emit(opcode: OpCodes.Ldarg_0);
        il.Emit(opcode: OpCodes.Castclass, cls: f.DeclaringType!);
        il.Emit(opcode: OpCodes.Ldarg_1);
        il.Emit(opcode: f.FieldType.IsValueType
                ? OpCodes.Unbox_Any
                : OpCodes.Castclass,
            cls: f.FieldType);
        il.Emit(opcode: OpCodes.Stfld, field: f);
        il.Emit(opcode: OpCodes.Ret);
        return (Action<object, object?>)dm.CreateDelegate(
            delegateType: typeof(Action<object, object?>));
    }

    // ---- writer ---------------------------------------------------------------------------------

    private sealed class Writer(Stream stream, SymbolIdentity? symbolIdentity)
    {
        private readonly BinaryWriter _bw = new(output: stream,
            encoding: Encoding.UTF8,
            leaveOpen: true);

        private readonly SymbolIdentity? _symbolIdentity = symbolIdentity;

        private readonly Dictionary<object, int> _ids =
            new(comparer: ReferenceEqualityComparer.Instance);

        private readonly Dictionary<Type, int> _typeIds = new();

        // Strings are interned BY VALUE (not just by reference): a symbol table repeats type names, module
        // paths and identifiers thousands of times, so value-dedup shrinks the file and cuts allocations.
        private readonly Dictionary<string, int>
            _stringIds = new(comparer: StringComparer.Ordinal);

        public void WriteHeader()
        {
            _bw.Write(value: Magic);
            _bw.Write(value: FormatVersion);
        }

        public void Flush()
        {
            _bw.Flush();
        }

        /// <summary>Writes one owned symbol's FIELD body (no id, no extern wrapper — the symbol IS a root of
        /// this artifact). Reference fields to OTHER symbols become externs; interior objects stay local.</summary>
        public void WriteSymbolFields(object symbol)
        {
            foreach (FieldEntry e in FieldPlan(type: symbol.GetType()))
            {
                WriteValue(value: e.Get(arg: symbol), declaredType: e.FieldType);
            }
        }

        private void WriteTypeId(Type type)
        {
            if (_typeIds.TryGetValue(key: type, value: out int id))
            {
                _bw.Write7BitEncodedInt(value: id);
                return;
            }

            id = _typeIds.Count;
            _typeIds[key: type] = id;
            _bw.Write7BitEncodedInt(value: id);
            // First sighting: emit the assembly-qualified name so the reader can resolve it.
            _bw.Write(value: type.AssemblyQualifiedName ?? type.FullName ?? type.Name);
        }

        private void WriteInternedString(string? s)
        {
            if (s == null)
            {
                _bw.Write7BitEncodedInt(value: 0);
                return;
            } // 0 = null

            if (_stringIds.TryGetValue(key: s, value: out int id))
            {
                _bw.Write7BitEncodedInt(value: id + 1); // existing → id+1 (>=1)
                return;
            }

            id = _stringIds.Count;
            _stringIds[key: s] = id;
            _bw.Write7BitEncodedInt(value: id + 1); // new id (== table count) → string follows
            _bw.Write(value: s);
        }

        public void WriteValue(object? value, Type declaredType)
        {
            Type underlying =
                Nullable.GetUnderlyingType(nullableType: declaredType) ?? declaredType;

            // Value-typed slots: no reference tracking, written inline.
            if (declaredType.IsValueType)
            {
                if (Nullable.GetUnderlyingType(nullableType: declaredType) != null)
                {
                    if (value == null)
                    {
                        _bw.Write(value: false);
                        return;
                    }

                    _bw.Write(value: true);
                    WriteInline(value: value, type: underlying);
                    return;
                }

                WriteInline(value: value!, type: declaredType);
                return;
            }

            // Strings: interned by value (see _stringIds). Handles null too.
            if (declaredType == typeof(string))
            {
                WriteInternedString(s: (string?)value);
                return;
            }

            // Reference-typed slots (class/interface/array/object/string/collection).
            if (value == null)
            {
                _bw.Write(value: KindNull);
                return;
            }

            // In modular mode EVERY symbol reference is an extern (module,key) — a symbol's fields are written
            // once, via WriteSymbolFields on its owning artifact's root loop, never inline through a reference.
            // Guarded so the monolithic path (_symbolIdentity == null) is byte-identical: dead branch there.
            if (_symbolIdentity != null &&
                _symbolIdentity(value: value) is (string mod, string key))
            {
                _bw.Write(value: KindExtern);
                WriteInternedString(s: mod);
                WriteInternedString(s: key);
                return;
            }

            if (_ids.TryGetValue(key: value, value: out int existing))
            {
                _bw.Write(value: KindRef);
                _bw.Write7BitEncodedInt(value: existing);
                return;
            }

            Type concrete = value.GetType();
            if (concrete.IsValueType)
            {
                // Boxed value in an object/interface slot.
                _bw.Write(value: KindBoxed);
                WriteTypeId(type: concrete);
                WriteInline(value: value, type: concrete);
                return;
            }

            int newId = _ids.Count;
            _ids[key: value] = newId;
            _bw.Write(value: KindNew);
            _bw.Write7BitEncodedInt(value: newId);
            WriteTypeId(type: concrete);
            WriteBody(value: value, concrete: concrete);
        }

        private void WriteInline(object value, Type type)
        {
            if (type.IsEnum)
            {
                WriteInline(
                    value: Convert.ChangeType(value: value,
                        conversionType: Enum.GetUnderlyingType(enumType: type)),
                    type: Enum.GetUnderlyingType(enumType: type));
                return;
            }

            switch (value)
            {
                case bool b: _bw.Write(value: b); break;
                case byte v: _bw.Write(value: v); break;
                case sbyte v: _bw.Write(value: v); break;
                case short v: _bw.Write(value: v); break;
                case ushort v: _bw.Write(value: v); break;
                case int v: _bw.Write(value: v); break;
                case uint v: _bw.Write(value: v); break;
                case long v: _bw.Write(value: v); break;
                case ulong v: _bw.Write(value: v); break;
                case float v: _bw.Write(value: v); break;
                case double v: _bw.Write(value: v); break;
                case decimal v: _bw.Write(value: v); break;
                case char v: _bw.Write(value: v); break;
                default:
                    // A non-primitive struct (e.g. ValueTuple): write its fields.
                    WriteStructFields(value: value, type: type);
                    break;
            }
        }

        private void WriteStructFields(object value, Type type)
        {
            foreach (FieldInfo f in FieldsOf(type: type))
            {
                WriteValue(value: f.GetValue(obj: value), declaredType: f.FieldType);
            }
        }

        private void WriteBody(object value, Type concrete)
        {
            switch (value)
            {
                case string s:
                    _bw.Write(value: s);
                    return;
                case Array arr:
                    WriteArrayBody(arr: arr, concrete: concrete);
                    return;
                case IDictionary dict when concrete.IsGenericType &&
                                           concrete.GetGenericTypeDefinition() ==
                                           typeof(Dictionary<,>):
                    WriteDictionaryBody(value: value, dict: dict, concrete: concrete);
                    return;
                case IEnumerable seq when concrete.IsGenericType &&
                                          concrete.GetGenericTypeDefinition() == typeof(HashSet<>):
                    WriteHashSetBody(value: value, seq: seq, concrete: concrete);
                    return;
                case IEnumerable seq when concrete.IsGenericType &&
                                          concrete.GetGenericTypeDefinition() == typeof(List<>):
                    WriteListBody(seq: seq, concrete: concrete);
                    return;
                default:
                    // Reference object: compiled getters (hot path). Struct fields stay on reflection above.
                    foreach (FieldEntry e in FieldPlan(type: concrete))
                    {
                        WriteValue(value: e.Get(arg: value), declaredType: e.FieldType);
                    }

                    return;
            }
        }

        private void WriteArrayBody(Array arr, Type concrete)
        {
            _bw.Write7BitEncodedInt(value: arr.Length);
            Type elemT = concrete.GetElementType()!;
            foreach (object? e in arr)
            {
                WriteValue(value: e, declaredType: elemT);
            }
        }

        private void WriteDictionaryBody(object value, IDictionary dict, Type concrete)
        {
            Type[] kv = concrete.GetGenericArguments();
            // Preserve the comparer (e.g. OrdinalIgnoreCase module tables, ReferenceEqualityComparer
            // BodyScanCache) — recreating with the default comparer would break lookups.
            WriteValue(value: concrete.GetProperty(name: "Comparer")!.GetValue(obj: value),
                declaredType: typeof(object));
            _bw.Write7BitEncodedInt(value: dict.Count);
            foreach (DictionaryEntry entry in dict)
            {
                WriteValue(value: entry.Key, declaredType: kv[0]);
                WriteValue(value: entry.Value, declaredType: kv[1]);
            }
        }

        private void WriteHashSetBody(object value, IEnumerable seq, Type concrete)
        {
            Type itemT = concrete.GetGenericArguments()[0];
            WriteValue(value: concrete.GetProperty(name: "Comparer")!.GetValue(obj: value),
                declaredType: typeof(object));
            var items = new List<object?>();
            foreach (object? e in seq)
            {
                items.Add(item: e);
            }

            _bw.Write7BitEncodedInt(value: items.Count);
            foreach (object? e in items)
            {
                WriteValue(value: e, declaredType: itemT);
            }
        }

        private void WriteListBody(IEnumerable seq, Type concrete)
        {
            Type itemT = concrete.GetGenericArguments()[0];
            var items = new List<object?>();
            foreach (object? e in seq)
            {
                items.Add(item: e);
            }

            _bw.Write7BitEncodedInt(value: items.Count);
            foreach (object? e in items)
            {
                WriteValue(value: e, declaredType: itemT);
            }
        }
    }

    // ---- reader ---------------------------------------------------------------------------------

    private sealed class Reader(Stream stream, ExternResolver? externResolver)
    {
        private readonly BinaryReader _br = new(input: stream,
            encoding: Encoding.UTF8,
            leaveOpen: true);

        private readonly ExternResolver? _externResolver = externResolver;
        private readonly List<object?> _objects = new();
        private readonly List<Type> _types = new();
        private readonly List<string> _strings = new();

        /// <summary>Fills a pre-created shell's fields from the graph (phase B). The shell is NOT a local id;
        /// references to it come via externs. Interior objects in the body get local ids as usual.</summary>
        public void FillSymbolFields(object shell)
        {
            foreach (FieldEntry e in FieldPlan(type: shell.GetType()))
            {
                e.Set(arg1: shell, arg2: ReadValue(declaredType: e.FieldType));
            }
        }

        public void ReadHeader()
        {
            if (_br.ReadUInt32() != Magic)
            {
                throw new InvalidDataException(message: "Not a .pbrf stream (bad magic).");
            }

            int ver = _br.ReadInt32();
            if (ver != FormatVersion)
            {
                throw new InvalidDataException(
                    message: $".pbrf format version {ver} != expected {FormatVersion}.");
            }
        }

        private Type ReadTypeId()
        {
            int id = _br.Read7BitEncodedInt();
            if (id < _types.Count)
            {
                return _types[index: id];
            }

            string name = _br.ReadString();
            var type = Type.GetType(typeName: name, throwOnError: true)!;
            _types.Add(
                item: type); // id == _types.Count at first sighting (ids are assigned in order)
            return type;
        }

        private string? ReadInternedString()
        {
            int code = _br.Read7BitEncodedInt();
            if (code == 0)
            {
                return null;
            }

            int id = code - 1;
            if (id < _strings.Count)
            {
                return _strings[index: id];
            }

            string s = _br.ReadString(); // new (id == _strings.Count)
            _strings.Add(item: s);
            return s;
        }

        public object? ReadValue(Type declaredType)
        {
            Type underlying =
                Nullable.GetUnderlyingType(nullableType: declaredType) ?? declaredType;

            if (declaredType.IsValueType)
            {
                if (Nullable.GetUnderlyingType(nullableType: declaredType) != null)
                {
                    return _br.ReadBoolean()
                        ? ReadInline(type: underlying)
                        : null;
                }

                return ReadInline(type: declaredType);
            }

            if (declaredType == typeof(string))
            {
                return ReadInternedString();
            }

            byte kind = _br.ReadByte();
            switch (kind)
            {
                case KindNull: return null;
                case KindRef: return _objects[index: _br.Read7BitEncodedInt()];
                case KindExtern:
                {
                    string mod = ReadInternedString()!;
                    string key = ReadInternedString()!;
                    if (_externResolver == null)
                    {
                        throw new InvalidDataException(
                            message:
                            $"extern reference ({mod}!{key}) but no resolver was supplied.");
                    }

                    return _externResolver(module: mod, key: key);
                }
                case KindBoxed:
                {
                    Type boxed = ReadTypeId();
                    return ReadInline(type: boxed);
                }
                case KindNew:
                {
                    int id = _br.Read7BitEncodedInt();
                    Type concrete = ReadTypeId();
                    return ReadBody(concrete: concrete, id: id);
                }
                default: throw new InvalidDataException(message: $"Unknown value kind {kind}.");
            }
        }

        private object ReadInline(Type type)
        {
            if (type.IsEnum)
            {
                object raw = ReadInline(type: Enum.GetUnderlyingType(enumType: type));
                return Enum.ToObject(enumType: type, value: raw);
            }

            if (type == typeof(bool))
            {
                return _br.ReadBoolean();
            }

            if (type == typeof(byte))
            {
                return _br.ReadByte();
            }

            if (type == typeof(sbyte))
            {
                return _br.ReadSByte();
            }

            if (type == typeof(short))
            {
                return _br.ReadInt16();
            }

            if (type == typeof(ushort))
            {
                return _br.ReadUInt16();
            }

            if (type == typeof(int))
            {
                return _br.ReadInt32();
            }

            if (type == typeof(uint))
            {
                return _br.ReadUInt32();
            }

            if (type == typeof(long))
            {
                return _br.ReadInt64();
            }

            if (type == typeof(ulong))
            {
                return _br.ReadUInt64();
            }

            if (type == typeof(float))
            {
                return _br.ReadSingle();
            }

            if (type == typeof(double))
            {
                return _br.ReadDouble();
            }

            if (type == typeof(decimal))
            {
                return _br.ReadDecimal();
            }

            if (type == typeof(char))
            {
                return _br.ReadChar();
            }

            // Non-primitive struct (e.g. ValueTuple): reconstruct via boxed instance + field set.
            object boxedStruct = RuntimeHelpers.GetUninitializedObject(type: type);
            ReadStructFields(target: boxedStruct, type: type);
            return boxedStruct;
        }

        private void ReadStructFields(object target, Type type)
        {
            foreach (FieldInfo f in FieldsOf(type: type))
            {
                f.SetValue(obj: target, value: ReadValue(declaredType: f.FieldType));
            }
        }

        private object ReadBody(Type concrete, int id)
        {
            // string
            if (concrete == typeof(string))
            {
                string s = _br.ReadString();
                RegisterAt(id: id, obj: s);
                return s;
            }

            // array
            if (concrete.IsArray)
            {
                return ReadArrayBody(concrete: concrete, id: id);
            }

            // dictionary — reconstruct with its serialized comparer, then fill.
            if (concrete.IsGenericType &&
                concrete.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                return ReadDictionaryBody(concrete: concrete, id: id);
            }

            // hashset — reconstruct with its serialized comparer, then fill.
            if (concrete.IsGenericType && concrete.GetGenericTypeDefinition() == typeof(HashSet<>))
            {
                return ReadHashSetBody(concrete: concrete, id: id);
            }

            // list
            if (concrete.IsGenericType && concrete.GetGenericTypeDefinition() == typeof(List<>))
            {
                return ReadListBody(concrete: concrete, id: id);
            }

            // general object: uninitialized (no ctor/initializers), register BEFORE fields for cycles.
            object obj = RuntimeHelpers.GetUninitializedObject(type: concrete);
            RegisterAt(id: id, obj: obj);
            // Compiled setters (hot path) — handles readonly/init backing fields via stfld.
            foreach (FieldEntry e in FieldPlan(type: concrete))
            {
                e.Set(arg1: obj, arg2: ReadValue(declaredType: e.FieldType));
            }

            return obj;
        }

        private Array ReadArrayBody(Type concrete, int id)
        {
            int len = _br.Read7BitEncodedInt();
            Type elemT = concrete.GetElementType()!;
            var arr = Array.CreateInstance(elementType: elemT, length: len);
            RegisterAt(id: id, obj: arr);
            for (int i = 0; i < len; i++)
            {
                arr.SetValue(value: ReadValue(declaredType: elemT), index: i);
            }

            return arr;
        }

        private object ReadDictionaryBody(Type concrete, int id)
        {
            Type[] kv = concrete.GetGenericArguments();
            object? comparer = ReadValue(declaredType: typeof(object));
            object dict = Activator.CreateInstance(type: concrete, args: [comparer])!;
            RegisterAt(id: id, obj: dict);
            int count = _br.Read7BitEncodedInt();
            MethodInfo add = concrete.GetMethod(name: "Add", types: kv)!;
            for (int i = 0; i < count; i++)
            {
                object? k = ReadValue(declaredType: kv[0]);
                object? v = ReadValue(declaredType: kv[1]);
                add.Invoke(obj: dict, parameters: [k, v]);
            }

            return dict;
        }

        private object ReadHashSetBody(Type concrete, int id)
        {
            Type itemT = concrete.GetGenericArguments()[0];
            object? comparer = ReadValue(declaredType: typeof(object));
            object coll = Activator.CreateInstance(type: concrete, args: [comparer])!;
            RegisterAt(id: id, obj: coll);
            int count = _br.Read7BitEncodedInt();
            MethodInfo add = concrete.GetMethod(name: "Add", types: [itemT])!;
            for (int i = 0; i < count; i++)
            {
                add.Invoke(obj: coll, parameters: [ReadValue(declaredType: itemT)]);
            }

            return coll;
        }

        private object ReadListBody(Type concrete, int id)
        {
            object coll = Activator.CreateInstance(type: concrete)!;
            RegisterAt(id: id, obj: coll);
            Type itemT = concrete.GetGenericArguments()[0];
            int count = _br.Read7BitEncodedInt();
            MethodInfo add = concrete.GetMethod(name: "Add", types: [itemT])!;
            for (int i = 0; i < count; i++)
            {
                add.Invoke(obj: coll, parameters: [ReadValue(declaredType: itemT)]);
            }

            return coll;
        }

        private void RegisterAt(int id, object obj)
        {
            while (_objects.Count <= id)
            {
                _objects.Add(item: null);
            }

            _objects[index: id] = obj;
        }
    }
}
