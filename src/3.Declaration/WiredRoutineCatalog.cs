namespace Compiler.Declaration;

/// <summary>
/// The views (consumer lists) a wired-routine concept can participate in. Each historical
/// hard-coded list becomes a projection of <see cref="WiredRoutineCatalog"/> filtered by one flag.
/// </summary>
[Flags]
public enum WiredViews
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
    public required WiredViews Views { get; init; }

    /// <summary>The protocols that materialise this routine. <c>[0]</c> is the primary/canonical
    /// protocol used by capability gating; the full list is the <see cref="WiredViews.ProtocolDecl"/>
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
/// is now a projection of <see cref="All"/> filtered by a <see cref="WiredViews"/> flag. Adding or
/// renaming a wired routine is a one-line edit here; the projections (and their <c>#if DEBUG</c>
/// equality assertions at each old site) keep every consumer aligned.
/// </summary>
public static class WiredRoutineCatalog
{
    // Shorthand local aliases to keep the table readable.
    private const WiredViews Cap = WiredViews.Capability;
    private const WiredViews Known = WiredViews.KnownWired;
    private const WiredViews Proto = WiredViews.ProtocolDecl;
    private const WiredViews Seed = WiredViews.ReachabilitySeed;

    // Protocol name constants for strings repeated 4+ times in the catalog.
    private const string ComparableProtocol = "Comparable";
    private const string BitwiseableProtocol = "Bitwiseable";
    private const string BitandCapability = "bitand";
    private const string ShiftableProtocol = "Shiftable";
    private const string AshlCapability = "ashl";
    private const string InPlaceBitandeableProtocol = "InPlaceBitwiseable";
    private const string IbitandCapability = "ibitand";
    private const string InPlaceShiftableProtocol = "InPlaceShiftable";
    private const string IashlCapability = "iashl";

    /// <summary>All wired-routine entries in canonical order. Every consumer projection is a filtered view of this list.</summary>
    public static readonly IReadOnlyList<WiredEntry> All = BuildAll();

    private static WiredEntry[] BuildAll()
    {
        return
        [
            // ---- Creator / context / lifecycle (declarable, not protocol-bound) ----
            // The anonymous constructor carries no name (RoutineInfo.CreatorName) — matched by the empty
            // key; its identity is the Creator kind.
            new WiredEntry
            {
                Name = TypeModel.Symbols.RoutineInfo.CreatorName,
                Kind = WiredKind.Creator,
                Views = Known
            },
            // Infallible literal constructor synthesized by LiteralLoweringPass for `n`/`dn`
            // arbitrary-precision literals (Integer/Decimal.from_literal(text:)). Declarable in
            // stdlib (Known) and seeded live so the synthesized calls keep their link symbols (Seed).
            new WiredEntry { Name = "from_literal", Kind = WiredKind.Creator, Views = Known | Seed },
            new WiredEntry { Name = "enter", Kind = WiredKind.Context, Views = Known },
            new WiredEntry { Name = "exit", Kind = WiredKind.Context, Views = Known },
            new WiredEntry
            {
                Name = "destroy", Kind = WiredKind.Lifecycle, Views = Known, AlwaysLive = true
            },
            new WiredEntry
            {
                Name = "assign",
                Kind = WiredKind.Copy,
                Views = Cap | Seed,
                Protocols = ["Assignable"],
                AlwaysLive = true
            },
            // Deep `copy` (Copyable). Like `store`, it is INJECTED during postprocessing (the record/
            // collection/variant deep-copy point in RecordCopyLoweringPass) — after reachability has run —
            // so it must be AlwaysLive to bypass the GMP reachability gate and Seed to be marked live per
            // concrete owner. Cap gates it on `Copyable`: only owners whose element/arm types are copyable
            // emit a body (e.g. Dict[Text, SerialValue].copy needs SerialValue copyable), so a
            // Dict[Text, NonCopyable] correctly carries no `copy` symbol.
            new WiredEntry
            {
                Name = "duplicate",
                Kind = WiredKind.Copy,
                Views = Cap | Seed,
                Protocols = ["Copyable"],
                AlwaysLive = true
            },

            // ---- Display / hash ----
            new WiredEntry
            {
                Name = "represent",
                Kind = WiredKind.Display,
                Views = Cap | Known | Seed,
                Protocols = ["Representable"]
            },
            new WiredEntry
            {
                Name = "diagnose",
                Kind = WiredKind.Display,
                Views = Cap | Known | Seed,
                Protocols = ["Diagnosable"]
            },
            // Cycle-collector visit hook: a universal `@overridable` no-op auto-conferred on EVERY type
            // (like represent/diagnose), so a generic container buffer-walk can call `element.cyclic_visit()`
            // uniformly. `Roamed[T].cyclic_visit` (hand-written) overrides it to report the controller.
            new WiredEntry
            {
                Name = "cyclic_visit",
                Kind = WiredKind.CycleTrace,
                Views = Cap | Known | Seed,
                Protocols = ["CycleTraceable"]
            },
            new WiredEntry
            {
                Name = "hash",
                Kind = WiredKind.Hash,
                Views = Cap | Known | Seed,
                Protocols = ["Hashable"]
            },

            // ---- Comparison (cmp family shares the cmp body; ne shares eq) ----
            new WiredEntry
            {
                Name = "eq",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Equatable"]
            },
            new WiredEntry
            {
                Name = "ne",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Equatable"],
                CapabilityWiredOverride = "eq"
            },
            new WiredEntry
            {
                Name = "cmp",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ComparableProtocol]
            },
            new WiredEntry
            {
                Name = "lt",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ComparableProtocol],
                CapabilityWiredOverride = "cmp"
            },
            new WiredEntry
            {
                Name = "le",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ComparableProtocol],
                CapabilityWiredOverride = "cmp"
            },
            new WiredEntry
            {
                Name = "gt",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ComparableProtocol],
                CapabilityWiredOverride = "cmp"
            },
            new WiredEntry
            {
                Name = "ge",
                Kind = WiredKind.Comparison,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ComparableProtocol],
                CapabilityWiredOverride = "cmp"
            },

            // ---- Container / iteration / indexing ----
            new WiredEntry
            {
                Name = "contains",
                Kind = WiredKind.Container,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Container"],
                CapabilityWiredOverride = "contains"
            },
            new WiredEntry
            {
                Name = "notcontains",
                Kind = WiredKind.Container,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Container"],
                CapabilityWiredOverride = "contains"
            },
            new WiredEntry
            {
                Name = "iter",
                Kind = WiredKind.Iteration,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Iterable"]
            },
            new WiredEntry
            {
                Name = "emit",
                Kind = WiredKind.Iteration,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Emittable"],
                Failable = true
            },
            new WiredEntry { Name = "try_emit", Kind = WiredKind.Iteration, Views = Seed },
            new WiredEntry
            {
                Name = "getitem",
                Kind = WiredKind.Indexing,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Indexable"],
                Failable = true
            },
            new WiredEntry
            {
                Name = "setitem",
                Kind = WiredKind.Indexing,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["MutableIndexable"],
                Failable = true
            },

            // ---- Unwrap (Maybe / Result / Lookup) ----
            new WiredEntry
            {
                Name = "unwrap", Kind = WiredKind.Unwrap, Views = Known | Seed, Failable = true
            },
            new WiredEntry { Name = "unwrap_or", Kind = WiredKind.Unwrap, Views = Known | Seed },

            // ---- Arithmetic (standard) ----
            new WiredEntry
            {
                Name = "add",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Addable", "DurationAddable"]
            },
            new WiredEntry
            {
                Name = "sub",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Subtractable", "DurationSubtractable"]
            },
            new WiredEntry
            {
                Name = "mul",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Multiplicable", "TextRepeatable", "Scalable"]
            },
            new WiredEntry
            {
                Name = "truediv",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Divisible", "ScalarDivisible"]
            },
            new WiredEntry
            {
                Name = "floordiv",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["FloorDivisible", "ScalarFloorDivisible"]
            },
            new WiredEntry
            {
                Name = "mod",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["FloorDivisible"],
                CapabilityWiredOverride = "floordiv"
            },
            new WiredEntry
            {
                Name = "pow",
                Kind = WiredKind.Arithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Exponentiable"]
            },
            new WiredEntry
            {
                Name = "neg",
                Kind = WiredKind.Unary,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Negatable"]
            },

            // ---- Arithmetic (wrapping) ----
            new WiredEntry
            {
                Name = "add_wrap",
                Kind = WiredKind.ArithmeticWrap,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["WrappingAddable"]
            },
            new WiredEntry
            {
                Name = "sub_wrap",
                Kind = WiredKind.ArithmeticWrap,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["WrappingSubtractable"]
            },
            new WiredEntry
            {
                Name = "mul_wrap",
                Kind = WiredKind.ArithmeticWrap,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["WrappingMultiplicable"]
            },
            new WiredEntry
            {
                Name = "pow_wrap",
                Kind = WiredKind.ArithmeticWrap,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["WrappingExponentiable"]
            },

            // ---- Arithmetic (clamping) ----
            new WiredEntry
            {
                Name = "add_clamp",
                Kind = WiredKind.ArithmeticClamp,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["ClampingAddable"]
            },
            new WiredEntry
            {
                Name = "sub_clamp",
                Kind = WiredKind.ArithmeticClamp,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["ClampingSubtractable"]
            },
            new WiredEntry
            {
                Name = "mul_clamp",
                Kind = WiredKind.ArithmeticClamp,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["ClampingMultiplicable"]
            },
            new WiredEntry
            {
                Name = "truediv_clamp",
                Kind = WiredKind.ArithmeticClamp,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["ClampingDivisible"]
            },
            new WiredEntry
            {
                Name = "pow_clamp",
                Kind = WiredKind.ArithmeticClamp,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["ClampingExponentiable"]
            },

            // ---- Arithmetic (unchecked) ----
            new WiredEntry
            {
                Name = "add_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedAddable"]
            },
            new WiredEntry
            {
                Name = "sub_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedSubtractable"]
            },
            new WiredEntry
            {
                Name = "mul_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedMultiplicable"]
            },
            new WiredEntry
            {
                Name = "truediv_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedTrueDivisible"]
            },
            new WiredEntry
            {
                Name = "floordiv_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedFloorDivisible"]
            },
            new WiredEntry
            {
                Name = "mod_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedFloorDivisible"],
                CapabilityWiredOverride = "floordiv_unchecked"
            },
            new WiredEntry
            {
                Name = "pow_unchecked",
                Kind = WiredKind.ArithmeticUnchecked,
                Views = Cap | Seed,
                Protocols = ["UncheckedExponentiable"]
            },

            // ---- Bitwise (the bitand body covers and/or/xor) ----
            new WiredEntry
            {
                Name = BitandCapability,
                Kind = WiredKind.Bitwise,
                Views = Cap | Known | Proto | Seed,
                Protocols = [BitwiseableProtocol],
                CapabilityWiredOverride = BitandCapability
            },
            new WiredEntry
            {
                Name = "bitor",
                Kind = WiredKind.Bitwise,
                Views = Cap | Known | Proto | Seed,
                Protocols = [BitwiseableProtocol],
                CapabilityWiredOverride = BitandCapability
            },
            new WiredEntry
            {
                Name = "bitxor",
                Kind = WiredKind.Bitwise,
                Views = Cap | Known | Proto | Seed,
                Protocols = [BitwiseableProtocol],
                CapabilityWiredOverride = BitandCapability
            },
            new WiredEntry
            {
                Name = "bitnot",
                Kind = WiredKind.Unary,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["Invertible"]
            },

            // ---- Shift (the ashl body covers all four) ----
            new WiredEntry
            {
                Name = AshlCapability,
                Kind = WiredKind.Shift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ShiftableProtocol],
                CapabilityWiredOverride = AshlCapability
            },
            new WiredEntry
            {
                Name = "ashr",
                Kind = WiredKind.Shift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ShiftableProtocol],
                CapabilityWiredOverride = AshlCapability
            },
            new WiredEntry
            {
                Name = "lshl",
                Kind = WiredKind.Shift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ShiftableProtocol],
                CapabilityWiredOverride = AshlCapability
            },
            new WiredEntry
            {
                Name = "lshr",
                Kind = WiredKind.Shift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [ShiftableProtocol],
                CapabilityWiredOverride = AshlCapability
            },

            // ---- In-place arithmetic (imod shares ifloordiv) ----
            new WiredEntry
            {
                Name = "iadd",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceAddable"]
            },
            new WiredEntry
            {
                Name = "isub",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceSubtractable"]
            },
            new WiredEntry
            {
                Name = "imul",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceMultiplicable"]
            },
            new WiredEntry
            {
                Name = "itruediv",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceDivisible"]
            },
            new WiredEntry
            {
                Name = "ifloordiv",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceFloorDivisible"]
            },
            new WiredEntry
            {
                Name = "imod",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceFloorDivisible"],
                CapabilityWiredOverride = "ifloordiv"
            },
            new WiredEntry
            {
                Name = "ipow",
                Kind = WiredKind.InPlaceArithmetic,
                Views = Cap | Known | Proto | Seed,
                Protocols = ["InPlaceExponentiable"]
            },

            // ---- In-place bitwise (ibitor/ibitxor share ibitand) ----
            new WiredEntry
            {
                Name = IbitandCapability,
                Kind = WiredKind.InPlaceBitwise,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceBitandeableProtocol],
                CapabilityWiredOverride = IbitandCapability
            },
            new WiredEntry
            {
                Name = "ibitor",
                Kind = WiredKind.InPlaceBitwise,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceBitandeableProtocol],
                CapabilityWiredOverride = IbitandCapability
            },
            new WiredEntry
            {
                Name = "ibitxor",
                Kind = WiredKind.InPlaceBitwise,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceBitandeableProtocol],
                CapabilityWiredOverride = IbitandCapability
            },

            // ---- In-place shift (iashr/ilshl/ilshr share iashl) ----
            new WiredEntry
            {
                Name = IashlCapability,
                Kind = WiredKind.InPlaceShift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceShiftableProtocol],
                CapabilityWiredOverride = IashlCapability
            },
            new WiredEntry
            {
                Name = "iashr",
                Kind = WiredKind.InPlaceShift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceShiftableProtocol],
                CapabilityWiredOverride = IashlCapability
            },
            new WiredEntry
            {
                Name = "ilshl",
                Kind = WiredKind.InPlaceShift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceShiftableProtocol],
                CapabilityWiredOverride = IashlCapability
            },
            new WiredEntry
            {
                Name = "ilshr",
                Kind = WiredKind.InPlaceShift,
                Views = Cap | Known | Proto | Seed,
                Protocols = [InPlaceShiftableProtocol],
                CapabilityWiredOverride = IashlCapability
            }
        ];
    }

    // ---------------------------------------------------------------------------
    // Projections — each reproduces a historical hard-coded list exactly.
    // ---------------------------------------------------------------------------

    /// <summary>Capability map: <c>CapabilityKey → (primary Protocol, canonical wired routine)</c>.
    /// Reproduces <c>TypeRegistry.Capabilities._wiredRoutineMap</c>.</summary>
    public static Dictionary<string, (string Protocol, string WiredName)> BuildCapabilityMap()
    {
        var map = new Dictionary<string, (string, string)>(comparer: StringComparer.Ordinal);
        foreach (WiredEntry e in All.Where(predicate: e => e.Views.HasFlag(flag: Cap)))
        {
            map[key: e.Name] = (e.Protocols[index: 0], e.CapabilityWired);
        }

        return map;
    }

    /// <summary>Valid declarable <c>$</c>-names. Reproduces <c>SemanticVerifier.KnownWiredMemberRoutines</c>.</summary>
    public static HashSet<string> BuildKnownWiredMemberRoutines()
    {
        return new HashSet<string>(collection: All
                                              .Where(predicate: e => e.Views.HasFlag(flag: Known))
                                              .Select(selector: e => e.Name),
            comparer: StringComparer.Ordinal);
    }

    /// <summary>Operator → permitting protocols. Reproduces <c>SemanticVerifier.WiredToProtocols</c>.</summary>
    public static Dictionary<string, List<string>> BuildWiredToProtocols()
    {
        return All.Where(predicate: e => e.Views.HasFlag(flag: Proto))
                  .ToDictionary(keySelector: e => e.Name,
                       elementSelector: e => e.Protocols.ToList());
    }

    /// <summary>Names seeded live per concrete owner. Reproduces
    /// <c>RoutineReachabilityPass.WiredRoutineNames</c> (order-independent).</summary>
    public static string[] BuildReachabilitySeedNames()
    {
        return All.Where(predicate: e => e.Views.HasFlag(flag: Seed))
                  .Select(selector: e => e.Name)
                  .ToArray();
    }

    // ---------------------------------------------------------------------------
    // Query API for the generation/lifecycle stages (S2/S3).
    // ---------------------------------------------------------------------------

    private static readonly Dictionary<string, WiredEntry> _byName =
        All.ToDictionary(keySelector: e => e.Name, comparer: StringComparer.Ordinal);

    /// <summary>Names that must be emitted for every live owner (the unified-teardown lifecycle
    /// routines). Used by the GMP gate-bypass and codegen always-live policy in S3.</summary>
    public static readonly IReadOnlySet<string> AlwaysLiveNames = All
       .Where(predicate: e => e.AlwaysLive)
       .Select(selector: e => e.Name)
       .ToHashSet(comparer: StringComparer.Ordinal);

    /// <summary>Looks up a wired entry by its bare canonical name. Returns false when the name is not wired.</summary>
    public static bool TryGet(string name, out WiredEntry entry)
    {
        return _byName.TryGetValue(key: name, value: out entry!);
    }

    /// <summary>Protocols whose derived capability is conferred on EVERY type: <c>Representable</c>
    /// (<c>represent</c>) and <c>Diagnosable</c> (<c>diagnose</c>) are structurally satisfied by all
    /// types, so <c>AutoWiredRegistrationPass</c> registers their memberRoutines on every type. A universal
    /// derive template for one of these IS a live universal memberRoutine and stays SA-analyzed.</summary>
    private static readonly HashSet<string> _autoConferredProtocols =
        new(comparer: StringComparer.Ordinal) { "Representable", "Diagnosable", "CycleTraceable" };

    /// <summary>
    /// True when the universal derive member routine <paramref name="memberRoutine"/> (from an
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
    public static bool IsAutoConferredDerive(string memberRoutine)
    {
        return _byName.TryGetValue(key: memberRoutine, value: out WiredEntry? e) &&
               e.Protocols.Count > 0 &&
               e.Protocols.All(predicate: p => _autoConferredProtocols.Contains(item: p));
    }
}
