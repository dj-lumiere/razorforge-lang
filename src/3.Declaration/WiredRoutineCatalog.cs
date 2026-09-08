namespace Compiler.Declaration;

/// <summary>
/// The views (consumer lists) a wired-routine concept can participate in. Each historical
/// hard-coded list becomes a projection of <see cref="WiredRoutineCatalog"/> filtered by one flag.
/// </summary>
[Flags]
public enum WiredView
{
    /// <summary>The zero value; no views selected.</summary>
    None = 0,

    /// <summary>In <c>TypeRegistry.Capabilities._wiredRoutineMap</c>: capability gating
    /// (name → primary protocol + canonical wired routine).</summary>
    Capability = 1,

    /// <summary>In <c>SemanticVerifier.KnownWiredMemberRoutines</c>: the set of valid <c>$</c>-prefixed
    /// routine names a user may declare (drives the "unknown wired member routine" diagnostic).</summary>
    KnownWired = 2,

    /// <summary>In <c>SemanticVerifier.WiredToProtocols</c>: operator-declaration protocol
    /// requirement check (name → the protocols that permit declaring the operator).</summary>
    ProtocolDecl = 4,

    /// <summary>In <c>RoutineReachabilityPass.WiredRoutineNames</c>: names seeded live on every
    /// live concrete owner so operator-lowered bodies keep their link symbols.</summary>
    ReachabilitySeed = 8
}

/// <summary>Coarse classification of a wired routine, used by generation/lifecycle policy.</summary>
public enum WiredKind
{
    /// <summary>Object creation (create, from_literal).</summary>
    Creator,
    /// <summary>Equality and ordering (eq, cmp, etc.).</summary>
    Comparison,
    /// <summary>Checked arithmetic (add, sub, mul, div, mod, floordiv, pow).</summary>
    Arithmetic,
    /// <summary>Wrapping arithmetic variants (add%, sub%, mul%).</summary>
    ArithmeticWrap,
    /// <summary>Clamping arithmetic variants (add|, sub|, mul|).</summary>
    ArithmeticClamp,
    /// <summary>Unchecked arithmetic variants (add!, sub!, mul!).</summary>
    ArithmeticUnchecked,
    /// <summary>Bitwise operations (and, or, xor, not).</summary>
    Bitwise,
    /// <summary>Bit-shift operations (shl, shr).</summary>
    Shift,
    /// <summary>Unary prefix operations (neg).</summary>
    Unary,
    /// <summary>Carrier unwrap (unwrap_or, unwrap).</summary>
    Unwrap,
    /// <summary>Container membership (contains, notcontains).</summary>
    Container,
    /// <summary>Iteration protocol (iter, emit).</summary>
    Iteration,
    /// <summary>Indexing protocol (getitem, setitem).</summary>
    Indexing,
    /// <summary>Context/scope protocol (enter, exit).</summary>
    Context,
    /// <summary>Object lifecycle (destroy).</summary>
    Lifecycle,
    /// <summary>Display and diagnostic formatting (represent, diagnose).</summary>
    Display,
    /// <summary>Cycle-collector visit hook (cyclic_visit) — universal no-op, Roamed overrides.</summary>
    CycleTrace,
    /// <summary>Hash computation (hash).</summary>
    Hash,
    /// <summary>Value copy (store).</summary>
    Copy,
    /// <summary>In-place arithmetic assignment (iadd, isub, etc.).</summary>
    InPlaceArithmetic,
    /// <summary>In-place bitwise assignment (iand, ior, ixor).</summary>
    InPlaceBitwise,
    /// <summary>In-place shift assignment (ishl, ishr).</summary>
    InPlaceShift
}

/// <summary>One wired-routine concept (keyed by its bare canonical name, e.g. <c>getitem</c>).</summary>
public sealed class WiredEntry
{
    /// <summary>The bare canonical wired name (e.g. <c>getitem</c>). Never includes a <c>!</c> suffix; failability is tracked by <see cref="Failable"/>.</summary>
    public required string Name { get; init; }
    /// <summary>Coarse classification used by generation and lifecycle policy.</summary>
    public required WiredKind Kind { get; init; }
    /// <summary>Bitmask of consumer lists this entry participates in.</summary>
    public required WiredView Views { get; init; }

    /// <summary>The protocols that materialise this routine. <c>[0]</c> is the primary/canonical
    /// protocol used by capability gating; the full list is the <see cref="WiredView.ProtocolDecl"/>
    /// requirement set. Empty when the routine is not protocol-bound (e.g. <c>store</c> is keyed on
    /// Assignable for capability but not declared via a protocol-operator).</summary>
    public IReadOnlyList<string> Protocols { get; init; } = [];

    /// <summary>The canonical wired routine the capability gate looks up on the owner. Defaults to
    /// <see cref="Name"/>; overridden for derived operators that share a base
    /// (e.g. <c>ne</c>→<c>eq</c>, <c>lt</c>→<c>cmp</c>, <c>mod</c>→<c>floordiv</c>).</summary>
    public string? CapabilityWiredOverride { get; init; }

    /// <summary>True for routines that must be emitted for every live owner regardless of call-site
    /// reachability — the unified-teardown lifecycle routines (<c>destroy</c>/<c>store</c>).</summary>
    public bool AlwaysLive { get; init; }

    /// <summary>True when this routine carries the failable `!` marker. Failability is a PROPERTY —
    /// the `!` is never part of the name, key, or symbol (see <c>getitem</c>/<c>setitem</c>/<c>emit</c>).
    /// Lookups resolve by the bare name and compare this property; they never key on a banged string.</summary>
    public bool Failable { get; init; }

    /// <summary>The canonical wired name used by the capability gate: <see cref="CapabilityWiredOverride"/> if set, otherwise <see cref="Name"/>.</summary>
    public string CapabilityWired => CapabilityWiredOverride ?? Name;
}

/// <summary>
/// Single source of truth for the compiler's built-in ("wired") routine names. Every historical
/// hard-coded list (capability map, known-wired set, operator→protocol map, reachability seed array)
/// is now a projection of <see cref="All"/> filtered by a <see cref="WiredView"/> flag. Adding or
/// renaming a wired routine is a one-line edit here; the projections (and their <c>#if DEBUG</c>
/// equality assertions at each old site) keep every consumer aligned.
/// </summary>
public static class WiredRoutineCatalog
{
    // Shorthand local aliases to keep the table readable.
    private const WiredView Cap = WiredView.Capability;
    private const WiredView Known = WiredView.KnownWired;
    private const WiredView Proto = WiredView.ProtocolDecl;
    private const WiredView Seed = WiredView.ReachabilitySeed;

    /// <summary>All wired-routine entries in canonical order. Every consumer projection is a filtered view of this list.</summary>
    public static readonly IReadOnlyList<WiredEntry> All = BuildAll();

    private static WiredEntry[] BuildAll() =>
    [
        // ---- Creator / context / lifecycle (declarable, not protocol-bound) ----
        new() { Name = "create",  Kind = WiredKind.Creator, Views = Known },
        // Infallible literal constructor synthesized by LiteralLoweringPass for `n`/`dn`
        // arbitrary-precision literals (Integer/Decimal.from_literal(text:)). Declarable in
        // stdlib (Known) and seeded live so the synthesized calls keep their link symbols (Seed).
        new() { Name = "from_literal", Kind = WiredKind.Creator, Views = Known | Seed },
        new() { Name = "enter",   Kind = WiredKind.Context, Views = Known },
        new() { Name = "exit",    Kind = WiredKind.Context, Views = Known },
        new() { Name = "destroy", Kind = WiredKind.Lifecycle, Views = Known, AlwaysLive = true },
        new() { Name = "assign",    Kind = WiredKind.Copy, Views = Cap | Seed,
                Protocols = ["Assignable"], AlwaysLive = true },
        // Deep `copy` (Copyable). Like `store`, it is INJECTED during postprocessing (the record/
        // collection/variant deep-copy point in RecordCopyLoweringPass) — after reachability has run —
        // so it must be AlwaysLive to bypass the GMP reachability gate and Seed to be marked live per
        // concrete owner. Cap gates it on `Copyable`: only owners whose element/arm types are copyable
        // emit a body (e.g. Dict[Text, SerialValue].copy needs SerialValue copyable), so a
        // Dict[Text, NonCopyable] correctly carries no `copy` symbol.
        new() { Name = "duplicate",      Kind = WiredKind.Copy, Views = Cap | Seed,
                Protocols = ["Copyable"], AlwaysLive = true },

        // ---- Display / hash ----
        new() { Name = "represent", Kind = WiredKind.Display, Views = Cap | Known | Seed, Protocols = ["Representable"] },
        new() { Name = "diagnose",  Kind = WiredKind.Display, Views = Cap | Known | Seed, Protocols = ["Diagnosable"] },
        // Cycle-collector visit hook: a universal `@overridable` no-op auto-conferred on EVERY type
        // (like represent/diagnose), so a generic container buffer-walk can call `element.cyclic_visit()`
        // uniformly. `Roamed[T].cyclic_visit` (hand-written) overrides it to report the controller.
        new() { Name = "cyclic_visit", Kind = WiredKind.CycleTrace, Views = Cap | Known | Seed, Protocols = ["CycleTraceable"] },
        new() { Name = "hash",      Kind = WiredKind.Hash,    Views = Cap | Known | Seed, Protocols = ["Hashable"] },

        // ---- Comparison (cmp family shares the cmp body; ne shares eq) ----
        new() { Name = "eq",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Equatable"] },
        new() { Name = "ne",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Equatable"], CapabilityWiredOverride = "eq" },
        new() { Name = "cmp", Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"] },
        new() { Name = "lt",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "cmp" },
        new() { Name = "le",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "cmp" },
        new() { Name = "gt",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "cmp" },
        new() { Name = "ge",  Kind = WiredKind.Comparison, Views = Cap | Known | Proto | Seed, Protocols = ["Comparable"], CapabilityWiredOverride = "cmp" },

        // ---- Container / iteration / indexing ----
        new() { Name = "contains",    Kind = WiredKind.Container, Views = Cap | Known | Proto | Seed, Protocols = ["Container"], CapabilityWiredOverride = "contains" },
        new() { Name = "notcontains", Kind = WiredKind.Container, Views = Cap | Known | Proto | Seed, Protocols = ["Container"], CapabilityWiredOverride = "contains" },
        new() { Name = "iter",        Kind = WiredKind.Iteration, Views = Cap | Known | Proto | Seed, Protocols = ["Iterable"] },
        new() { Name = "emit",        Kind = WiredKind.Iteration, Views = Cap | Known | Proto | Seed, Protocols = ["Emittable"], Failable = true },
        new() { Name = "try_emit",     Kind = WiredKind.Iteration, Views = Seed },
        new() { Name = "getitem",     Kind = WiredKind.Indexing, Views = Cap | Known | Proto | Seed, Protocols = ["Indexable"], Failable = true },
        new() { Name = "setitem",     Kind = WiredKind.Indexing, Views = Cap | Known | Proto | Seed, Protocols = ["MutableIndexable"], Failable = true },

        // ---- Unwrap (Maybe / Result / Lookup) ----
        new() { Name = "unwrap",    Kind = WiredKind.Unwrap, Views = Known | Seed, Failable = true },
        new() { Name = "unwrap_or", Kind = WiredKind.Unwrap, Views = Known | Seed },

        // ---- Arithmetic (standard) ----
        new() { Name = "add",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Addable", "DurationAddable"] },
        new() { Name = "sub",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Subtractable", "DurationSubtractable"] },
        new() { Name = "mul",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Multiplicable", "TextRepeatable", "Scalable"] },
        new() { Name = "truediv",  Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Divisible", "ScalarDivisible"] },
        new() { Name = "floordiv", Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["FloorDivisible", "ScalarFloorDivisible"] },
        new() { Name = "mod",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["FloorDivisible"], CapabilityWiredOverride = "floordiv" },
        new() { Name = "pow",      Kind = WiredKind.Arithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["Exponentiable"] },
        new() { Name = "neg",      Kind = WiredKind.Unary,      Views = Cap | Known | Proto | Seed, Protocols = ["Negatable"] },

        // ---- Arithmetic (wrapping) ----
        new() { Name = "add_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingAddable"] },
        new() { Name = "sub_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingSubtractable"] },
        new() { Name = "mul_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingMultiplicable"] },
        new() { Name = "pow_wrap", Kind = WiredKind.ArithmeticWrap, Views = Cap | Known | Proto | Seed, Protocols = ["WrappingExponentiable"] },

        // ---- Arithmetic (clamping) ----
        new() { Name = "add_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingAddable"] },
        new() { Name = "sub_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingSubtractable"] },
        new() { Name = "mul_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingMultiplicable"] },
        new() { Name = "truediv_clamp", Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingDivisible"] },
        new() { Name = "pow_clamp",     Kind = WiredKind.ArithmeticClamp, Views = Cap | Known | Proto | Seed, Protocols = ["ClampingExponentiable"] },

        // ---- Arithmetic (unchecked) ----
        new() { Name = "add_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedAddable"] },
        new() { Name = "sub_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedSubtractable"] },
        new() { Name = "mul_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedMultiplicable"] },
        new() { Name = "truediv_unchecked",  Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedTrueDivisible"] },
        new() { Name = "floordiv_unchecked", Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedFloorDivisible"] },
        new() { Name = "mod_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedFloorDivisible"], CapabilityWiredOverride = "floordiv_unchecked" },
        new() { Name = "pow_unchecked",      Kind = WiredKind.ArithmeticUnchecked, Views = Cap | Seed, Protocols = ["UncheckedExponentiable"] },

        // ---- Bitwise (the bitand body covers and/or/xor) ----
        new() { Name = "bitand", Kind = WiredKind.Bitwise, Views = Cap | Known | Proto | Seed, Protocols = ["Bitwiseable"], CapabilityWiredOverride = "bitand" },
        new() { Name = "bitor",  Kind = WiredKind.Bitwise, Views = Cap | Known | Proto | Seed, Protocols = ["Bitwiseable"], CapabilityWiredOverride = "bitand" },
        new() { Name = "bitxor", Kind = WiredKind.Bitwise, Views = Cap | Known | Proto | Seed, Protocols = ["Bitwiseable"], CapabilityWiredOverride = "bitand" },
        new() { Name = "bitnot", Kind = WiredKind.Unary,   Views = Cap | Known | Proto | Seed, Protocols = ["Invertible"] },

        // ---- Shift (the ashl body covers all four) ----
        new() { Name = "ashl", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "ashl" },
        new() { Name = "ashr", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "ashl" },
        new() { Name = "lshl", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "ashl" },
        new() { Name = "lshr", Kind = WiredKind.Shift, Views = Cap | Known | Proto | Seed, Protocols = ["Shiftable"], CapabilityWiredOverride = "ashl" },

        // ---- In-place arithmetic (imod shares ifloordiv) ----
        new() { Name = "iadd",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceAddable"] },
        new() { Name = "isub",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceSubtractable"] },
        new() { Name = "imul",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceMultiplicable"] },
        new() { Name = "itruediv",  Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceDivisible"] },
        new() { Name = "ifloordiv", Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceFloorDivisible"] },
        new() { Name = "imod",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceFloorDivisible"], CapabilityWiredOverride = "ifloordiv" },
        new() { Name = "ipow",      Kind = WiredKind.InPlaceArithmetic, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceExponentiable"] },

        // ---- In-place bitwise (ibitor/ibitxor share ibitand) ----
        new() { Name = "ibitand", Kind = WiredKind.InPlaceBitwise, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceBitwiseable"], CapabilityWiredOverride = "ibitand" },
        new() { Name = "ibitor",  Kind = WiredKind.InPlaceBitwise, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceBitwiseable"], CapabilityWiredOverride = "ibitand" },
        new() { Name = "ibitxor", Kind = WiredKind.InPlaceBitwise, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceBitwiseable"], CapabilityWiredOverride = "ibitand" },

        // ---- In-place shift (iashr/ilshl/ilshr share iashl) ----
        new() { Name = "iashl", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "iashl" },
        new() { Name = "iashr", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "iashl" },
        new() { Name = "ilshl", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "iashl" },
        new() { Name = "ilshr", Kind = WiredKind.InPlaceShift, Views = Cap | Known | Proto | Seed, Protocols = ["InPlaceShiftable"], CapabilityWiredOverride = "iashl" },
    ];

    // ---------------------------------------------------------------------------
    // Projections — each reproduces a historical hard-coded list exactly.
    // ---------------------------------------------------------------------------

    /// <summary>Capability map: <c>CapabilityKey → (primary Protocol, canonical wired routine)</c>.
    /// Reproduces <c>TypeRegistry.Capabilities._wiredRoutineMap</c>.</summary>
    public static Dictionary<string, (string Protocol, string WiredName)> BuildCapabilityMap()
    {
        var map = new Dictionary<string, (string, string)>(comparer: StringComparer.Ordinal);
        foreach (WiredEntry e in All.Where(predicate: e => e.Views.HasFlag(flag: Cap)))
            map[key: e.Name] = (e.Protocols[index: 0], e.CapabilityWired);
        return map;
    }

    /// <summary>Valid declarable <c>$</c>-names. Reproduces <c>SemanticVerifier.KnownWiredMemberRoutines</c>.</summary>
    public static HashSet<string> BuildKnownWiredMemberRoutines() =>
        new(collection: All.Where(predicate: e => e.Views.HasFlag(flag: Known)).Select(selector: e => e.Name),
            comparer: StringComparer.Ordinal);

    /// <summary>Operator → permitting protocols. Reproduces <c>SemanticVerifier.WiredToProtocols</c>.</summary>
    public static Dictionary<string, List<string>> BuildWiredToProtocols() =>
        All.Where(predicate: e => e.Views.HasFlag(flag: Proto))
           .ToDictionary(keySelector: e => e.Name, elementSelector: e => e.Protocols.ToList());

    /// <summary>Names seeded live per concrete owner. Reproduces
    /// <c>RoutineReachabilityPass.WiredRoutineNames</c> (order-independent).</summary>
    public static string[] BuildReachabilitySeedNames() =>
        All.Where(predicate: e => e.Views.HasFlag(flag: Seed))
           .Select(selector: e => e.Name)
           .ToArray();

    // ---------------------------------------------------------------------------
    // Query API for the generation/lifecycle stages (S2/S3).
    // ---------------------------------------------------------------------------

    private static readonly Dictionary<string, WiredEntry> _byName =
        All.ToDictionary(keySelector: e => e.Name, comparer: StringComparer.Ordinal);

    /// <summary>Names that must be emitted for every live owner (the unified-teardown lifecycle
    /// routines). Used by the GMP gate-bypass and codegen always-live policy in S3.</summary>
    public static readonly IReadOnlySet<string> AlwaysLiveNames =
        All.Where(predicate: e => e.AlwaysLive).Select(selector: e => e.Name)
           .ToHashSet(comparer: StringComparer.Ordinal);

    /// <summary>Looks up a wired entry by its bare canonical name. Returns false when the name is not wired.</summary>
    public static bool TryGet(string name, out WiredEntry entry) => _byName.TryGetValue(key: name, value: out entry!);

    /// <summary>Returns true when <paramref name="name"/> is a lifecycle-category wired routine (<c>destroy</c> or <c>store</c>).</summary>
    public static bool IsLifecycle(string name) =>
        _byName.TryGetValue(key: name, value: out WiredEntry? e) &&
        e.Kind is WiredKind.Lifecycle or WiredKind.Copy;

    /// <summary>Protocols whose derived capability is conferred on EVERY type: <c>Representable</c>
    /// (<c>represent</c>) and <c>Diagnosable</c> (<c>diagnose</c>) are structurally satisfied by all
    /// types, so <c>AutoWiredRegistrationPass</c> registers their memberRoutines on every type. A universal
    /// derive template for one of these IS a live universal memberRoutine and stays SA-analyzed.</summary>
    private static readonly HashSet<string> _autoConferredProtocols =
        new(comparer: StringComparer.Ordinal) { "Representable", "Diagnosable", "CycleTraceable" };

    /// <summary>
    /// True when the universal derive memberRoutine <paramref name="member routine"/> (from an
    /// <c>@overridable/@override routine T.&lt;memberRoutine&gt;()</c> template) is auto-conferred on EVERY
    /// type — i.e. backed solely by an auto-conferred protocol (<c>Representable</c>/<c>Diagnosable</c>).
    /// Such a template is registered as a live universal memberRoutine and its body is SA-analyzed.
    /// <para>
    /// The complement — <c>!IsAutoConferredDerive</c> — is the OPT-IN derive predicate used by the
    /// registration/verification layers: a derive whose capability is conferred only via explicit
    /// protocol conformance (Equatable/Comparable/Hashable/Serializable/…, or any future opt-in
    /// capability) must NOT become a live universal (it would be force-instantiated for non-conformers)
    /// and its raw pre-monomorph body must not be SA-analyzed — the per-type body comes from the
    /// derive-template store via <c>WiredRoutinePass.CloneUniversalDeriveBody</c> instead. This is
    /// protocol-grounded (no per-memberRoutine name list): a memberRoutine absent from the catalog, or backed by a
    /// non-auto-conferred protocol, is opt-in by default.
    /// </para>
    /// </summary>
    public static bool IsAutoConferredDerive(string memberRoutine) =>
        _byName.TryGetValue(key: memberRoutine, value: out WiredEntry? e) && e.Protocols.Count > 0 &&
        e.Protocols.All(predicate: p => _autoConferredProtocols.Contains(item: p));

#if DEBUG
    // One-time consistency self-check (Debug builds only): every projection must reproduce the
    // historical hard-coded list EXACTLY. Runs on first access to the catalog (its static init).
    // The legacy literals below are the pre-S0 sources of truth, kept here as the reference oracle;
    // they are deleted in a follow-up once the catalog has proven stable. A mismatch throws with a
    // precise diff so a transcription slip is caught locally before any batch sweep.
    // NOTE: _selfChecked is declared at the END of this region — static field initializers run in
    // textual order, so it must come AFTER the _legacy* fields it reads (else they are still null).

    private static bool SelfCheck()
    {
        AssertSetEquals(label: "Capability", expected: _legacyCapabilityMap.Keys,
            actual: BuildCapabilityMap().Keys);
        AssertCapabilityValuesMatch();
        AssertSetEquals(label: "KnownWired", expected: _legacyKnownWired, actual: BuildKnownWiredMemberRoutines());
        AssertSetEquals(label: "WiredToProtocols-keys", expected: _legacyWiredToProtocols.Keys,
            actual: BuildWiredToProtocols().Keys);
        AssertWiredToProtocolsValuesMatch();
        AssertSetEquals(label: "ReachabilitySeed", expected: _legacyReachabilitySeed,
            actual: BuildReachabilitySeedNames());
        return true;
    }

    // Verifies each capability entry's (Protocol, WiredName) pair matches the legacy oracle exactly.
    private static void AssertCapabilityValuesMatch()
    {
        foreach (var (k, v) in _legacyCapabilityMap)
        {
            var got = BuildCapabilityMap()[key: k];
            if (got.Protocol != v.Protocol || got.WiredName != v.WiredName)
            {
                string m = $"capability '{k}' = ({got.Protocol},{got.WiredName}) but legacy ({v.Protocol},{v.WiredName})";
                Console.Error.WriteLine(value: "CATALOG-SELFCHECK: " + m);
                throw new InvalidOperationException(message: m);
            }
        }
    }

    // Verifies each WiredToProtocols entry's protocol list matches the legacy oracle in order.
    private static void AssertWiredToProtocolsValuesMatch()
    {
        foreach (var (k, v) in _legacyWiredToProtocols)
        {
            List<string> got = BuildWiredToProtocols()[key: k];
            if (!got.SequenceEqual(second: v))
            {
                string m = $"WiredToProtocols['{k}'] = [{string.Join(",", got)}] but legacy [{string.Join(",", v)}]";
                Console.Error.WriteLine(value: "CATALOG-SELFCHECK: " + m);
                throw new InvalidOperationException(message: m);
            }
        }
    }

    private static void AssertSetEquals(string label, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var e = new HashSet<string>(collection: expected, comparer: StringComparer.Ordinal);
        var a = new HashSet<string>(collection: actual, comparer: StringComparer.Ordinal);
        if (e.SetEquals(other: a)) return;
        var missing = e.Except(second: a).OrderBy(keySelector: x => x);
        var extra = a.Except(second: e).OrderBy(keySelector: x => x);
        string msg =
            $"WiredRoutineCatalog '{label}' diverged. Missing from catalog: [{string.Join(",", missing)}]. " +
            $"Extra in catalog: [{string.Join(",", extra)}].";
        Console.Error.WriteLine(value: "CATALOG-SELFCHECK: " + msg);
        throw new InvalidOperationException(message: msg);
    }

    private static readonly Dictionary<string, (string Protocol, string WiredName)> _legacyCapabilityMap =
        new(comparer: StringComparer.Ordinal)
        {
            ["eq"] = ("Equatable", "eq"), ["ne"] = ("Equatable", "eq"), ["hash"] = ("Hashable", "hash"),
            ["cmp"] = ("Comparable", "cmp"),
            ["lt"] = ("Comparable", "cmp"), ["le"] = ("Comparable", "cmp"), ["gt"] = ("Comparable", "cmp"),
            ["ge"] = ("Comparable", "cmp"), ["contains"] = ("Container", "contains"),
            ["notcontains"] = ("Container", "contains"), ["getitem"] = ("Indexable", "getitem"),
            ["setitem"] = ("MutableIndexable", "setitem"), ["iter"] = ("Iterable", "iter"),
            ["emit"] = ("Emittable", "emit"), ["represent"] = ("Representable", "represent"),
            ["diagnose"] = ("Diagnosable", "diagnose"),
            ["cyclic_visit"] = ("CycleTraceable", "cyclic_visit"), ["add"] = ("Addable", "add"),
            ["sub"] = ("Subtractable", "sub"), ["mul"] = ("Multiplicable", "mul"),
            ["truediv"] = ("Divisible", "truediv"), ["floordiv"] = ("FloorDivisible", "floordiv"),
            ["mod"] = ("FloorDivisible", "floordiv"), ["pow"] = ("Exponentiable", "pow"),
            ["neg"] = ("Negatable", "neg"), ["bitand"] = ("Bitwiseable", "bitand"),
            ["bitor"] = ("Bitwiseable", "bitand"), ["bitxor"] = ("Bitwiseable", "bitand"),
            ["bitnot"] = ("Invertible", "bitnot"), ["ashl"] = ("Shiftable", "ashl"),
            ["ashr"] = ("Shiftable", "ashl"), ["lshl"] = ("Shiftable", "ashl"),
            ["lshr"] = ("Shiftable", "ashl"), ["iadd"] = ("InPlaceAddable", "iadd"),
            ["isub"] = ("InPlaceSubtractable", "isub"), ["imul"] = ("InPlaceMultiplicable", "imul"),
            ["itruediv"] = ("InPlaceDivisible", "itruediv"), ["ifloordiv"] = ("InPlaceFloorDivisible", "ifloordiv"),
            ["imod"] = ("InPlaceFloorDivisible", "ifloordiv"), ["ipow"] = ("InPlaceExponentiable", "ipow"),
            ["ibitand"] = ("InPlaceBitwiseable", "ibitand"), ["ibitor"] = ("InPlaceBitwiseable", "ibitand"),
            ["ibitxor"] = ("InPlaceBitwiseable", "ibitand"), ["iashl"] = ("InPlaceShiftable", "iashl"),
            ["iashr"] = ("InPlaceShiftable", "iashl"), ["ilshl"] = ("InPlaceShiftable", "iashl"),
            ["ilshr"] = ("InPlaceShiftable", "iashl"), ["add_clamp"] = ("ClampingAddable", "add_clamp"),
            ["sub_clamp"] = ("ClampingSubtractable", "sub_clamp"), ["mul_clamp"] = ("ClampingMultiplicable", "mul_clamp"),
            ["truediv_clamp"] = ("ClampingDivisible", "truediv_clamp"), ["pow_clamp"] = ("ClampingExponentiable", "pow_clamp"),
            ["add_wrap"] = ("WrappingAddable", "add_wrap"), ["sub_wrap"] = ("WrappingSubtractable", "sub_wrap"),
            ["mul_wrap"] = ("WrappingMultiplicable", "mul_wrap"), ["pow_wrap"] = ("WrappingExponentiable", "pow_wrap"),
            ["add_unchecked"] = ("UncheckedAddable", "add_unchecked"), ["sub_unchecked"] = ("UncheckedSubtractable", "sub_unchecked"),
            ["mul_unchecked"] = ("UncheckedMultiplicable", "mul_unchecked"), ["truediv_unchecked"] = ("UncheckedTrueDivisible", "truediv_unchecked"),
            ["floordiv_unchecked"] = ("UncheckedFloorDivisible", "floordiv_unchecked"), ["mod_unchecked"] = ("UncheckedFloorDivisible", "floordiv_unchecked"),
            ["pow_unchecked"] = ("UncheckedExponentiable", "pow_unchecked"), ["assign"] = ("Assignable", "assign"),
            ["duplicate"] = ("Copyable", "duplicate"),
        };

    private static readonly string[] _legacyKnownWired =
    [
        "create", "from_literal", "add", "sub", "mul", "truediv", "floordiv", "mod", "pow",
        "add_wrap", "sub_wrap", "mul_wrap", "pow_wrap",
        "add_clamp", "sub_clamp", "mul_clamp", "truediv_clamp", "pow_clamp",
        "eq", "ne", "lt", "le", "gt", "ge", "cmp",
        "bitand", "bitor", "bitxor", "ashl", "ashr", "lshl", "lshr",
        "neg", "bitnot", "unwrap", "unwrap_or", "contains", "notcontains",
        "iter", "emit", "getitem", "setitem", "enter", "exit", "destroy",
        "represent", "diagnose", "cyclic_visit", "hash",
        "iadd", "isub", "imul", "itruediv", "ifloordiv", "imod", "ipow",
        "ibitand", "ibitor", "ibitxor", "iashl", "iashr", "ilshl", "ilshr",
    ];

    private static readonly Dictionary<string, List<string>> _legacyWiredToProtocols = new()
    {
        ["add"] = ["Addable", "DurationAddable"], ["sub"] = ["Subtractable", "DurationSubtractable"],
        ["mul"] = ["Multiplicable", "TextRepeatable", "Scalable"], ["truediv"] = ["Divisible", "ScalarDivisible"],
        ["floordiv"] = ["FloorDivisible", "ScalarFloorDivisible"], ["mod"] = ["FloorDivisible"],
        ["pow"] = ["Exponentiable"], ["add_wrap"] = ["WrappingAddable"], ["sub_wrap"] = ["WrappingSubtractable"],
        ["mul_wrap"] = ["WrappingMultiplicable"], ["pow_wrap"] = ["WrappingExponentiable"],
        ["add_clamp"] = ["ClampingAddable"], ["sub_clamp"] = ["ClampingSubtractable"],
        ["mul_clamp"] = ["ClampingMultiplicable"], ["truediv_clamp"] = ["ClampingDivisible"],
        ["pow_clamp"] = ["ClampingExponentiable"], ["eq"] = ["Equatable"], ["ne"] = ["Equatable"],
        ["cmp"] = ["Comparable"], ["lt"] = ["Comparable"], ["le"] = ["Comparable"], ["gt"] = ["Comparable"],
        ["ge"] = ["Comparable"], ["bitand"] = ["Bitwiseable"], ["bitor"] = ["Bitwiseable"],
        ["bitxor"] = ["Bitwiseable"], ["ashl"] = ["Shiftable"], ["ashr"] = ["Shiftable"],
        ["lshl"] = ["Shiftable"], ["lshr"] = ["Shiftable"], ["neg"] = ["Negatable"], ["bitnot"] = ["Invertible"],
        ["contains"] = ["Container"], ["notcontains"] = ["Container"], ["getitem"] = ["Indexable"],
        ["setitem"] = ["MutableIndexable"], ["iter"] = ["Iterable"], ["emit"] = ["Emittable"],
        ["iadd"] = ["InPlaceAddable"], ["isub"] = ["InPlaceSubtractable"], ["imul"] = ["InPlaceMultiplicable"],
        ["itruediv"] = ["InPlaceDivisible"], ["ifloordiv"] = ["InPlaceFloorDivisible"], ["imod"] = ["InPlaceFloorDivisible"],
        ["ipow"] = ["InPlaceExponentiable"], ["ibitand"] = ["InPlaceBitwiseable"], ["ibitor"] = ["InPlaceBitwiseable"],
        ["ibitxor"] = ["InPlaceBitwiseable"], ["iashl"] = ["InPlaceShiftable"], ["iashr"] = ["InPlaceShiftable"],
        ["ilshl"] = ["InPlaceShiftable"], ["ilshr"] = ["InPlaceShiftable"],
    };

    private static readonly string[] _legacyReachabilitySeed =
    [
        "from_literal",
        "represent", "diagnose", "cyclic_visit", "hash", "assign", "duplicate", "eq", "ne", "cmp", "lt", "le", "gt", "ge",
        "contains", "notcontains", "iter", "emit", "try_emit",
        "add", "sub", "mul", "truediv", "floordiv", "mod", "pow", "neg",
        "add_wrap", "sub_wrap", "mul_wrap", "pow_wrap",
        "add_unchecked", "sub_unchecked", "mul_unchecked", "truediv_unchecked", "floordiv_unchecked",
        "mod_unchecked", "pow_unchecked",
        "add_clamp", "sub_clamp", "mul_clamp", "truediv_clamp", "pow_clamp",
        "bitand", "bitor", "bitxor", "bitnot", "ashl", "ashr", "lshl", "lshr",
        "iadd", "isub", "imul", "itruediv", "ifloordiv", "imod", "ipow",
        "ibitand", "ibitor", "ibitxor", "iashl", "iashr", "ilshl", "ilshr",
        "unwrap", "unwrap_or", "getitem", "setitem",
    ];

    // Declared LAST so its initializer runs after every _legacy* field above (textual order).
    private static readonly bool _selfChecked = SelfCheck();
#endif
}
