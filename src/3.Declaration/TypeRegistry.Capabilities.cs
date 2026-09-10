using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Per-type capability queries. Answers "does this type support wired routine X?"
/// recursively, considering generic-def constraints on the routine in question.
/// </summary>
/// <remarks>
/// Used to gate codegen `declare` emission and reachability seeding for wired
/// routine families whose body has a built-in constraint surface that the registered
/// implementation can't satisfy for every instantiation — e.g. `Array[T, N].eq`
/// declares `needs T obeys Equatable`, so `Array[X, 64]` should NOT carry an
/// `eq` symbol because `X` is not equatable.
///
/// Results are cached per FullName + protocol for the lifetime of the registry.
/// Cache entries are conservative — when the cache cannot decide (incomplete type
/// substitution, missing generic def), the query returns true so the caller does
/// not silently drop a needed symbol.
/// </remarks>
public sealed partial class TypeRegistry
{
    private const string ContainsMemberRoutineName = "contains";

    private readonly Dictionary<(string FullName, string Protocol), bool> _capabilityCache = new();

    /// <summary>
    /// Keys currently being computed by <see cref="HasCapability"/> — used for cycle detection.
    /// A self-referential type (record containing itself via a wrapper) that re-enters
    /// <see cref="HasCapability"/> for its own key is assumed to be capable (conservative seed)
    /// so the recursion terminates. Written ONLY while the computation is in progress, never
    /// while the final result is being cached.
    /// </summary>
    private readonly HashSet<(string FullName, string Protocol)> _capabilityInProgress = new();

    /// <summary>
    /// Wired routine name -> (protocol it requires the owner to obey, canonical wired-routine name
    /// to look up on the owner). Derived from the single source of truth
    /// <see cref="WiredRoutineCatalog"/> (entries flagged <see cref="WiredViews.Capability"/>); used
    /// by both the constraint walker (which receives a `T obeys P` constraint and must check T) and
    /// the routine-applicability gate (which receives a routine and must check its owner). To add a
    /// wired routine, edit <see cref="WiredRoutineCatalog"/>, not this projection.
    /// </summary>
    private static readonly Dictionary<string, (string Protocol, string WiredName)>
        _wiredRoutineMap = WiredRoutineCatalog.BuildCapabilityMap();

    /// <summary>
    /// Reverse map: protocol -> canonical wired routine that materialises it. Built once
    /// from <see cref="_wiredRoutineMap"/>; the protocol's "canonical" routine is the one
    /// whose wired name matches the protocol's primary operator.
    /// </summary>
    private static readonly Dictionary<string, string> _protocolToWired = BuildProtocolToWired();

    private static Dictionary<string, string> BuildProtocolToWired()
    {
        var result = new Dictionary<string, string>(comparer: StringComparer.Ordinal);
        foreach ((string _, (string proto, string wired)) in _wiredRoutineMap)
        {
            result[key: proto] = wired;
        }

        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="type"/> can host wired routine <paramref name="wiredName"/>
    /// for this concrete instantiation — i.e. all `T obeys P` constraints on the generic-def
    /// version of that routine are satisfied by <paramref name="type"/>'s type arguments,
    /// AND the routine is either defined directly or derivable through the protocol marker.
    /// </summary>
    public bool TypeHasWiredRoutine(TypeSymbol type, string wiredName)
    {
        if (!_wiredRoutineMap.TryGetValue(key: wiredName,
                value: out (string Protocol, string WiredName) entry))
        {
            return true;
        }

        return HasCapability(type: type, protocol: entry.Protocol, wiredName: entry.WiredName);
    }

    /// <summary>Returns true if the type implements <c>Equatable</c> (eq).</summary>
    public bool TypeHasEquality(TypeSymbol type)
    {
        return TypeHasWiredRoutine(type: type, wiredName: "eq");
    }
    /// <summary>Returns true if the type implements <c>Containable</c> (contains).</summary>
    public bool TypeHasContainment(TypeSymbol type)
    {
        return TypeHasWiredRoutine(type: type, wiredName: ContainsMemberRoutineName);
    }
    /// <summary>Returns true if the type implements <c>Hashable</c> (hash).</summary>
    public bool TypeHasHashing(TypeSymbol type)
    {
        return TypeHasWiredRoutine(type: type, wiredName: "hash");
    }
    /// <summary>Returns true if the type implements <c>Comparable</c> (cmp).</summary>
    public bool TypeHasComparison(TypeSymbol type)
    {
        return TypeHasWiredRoutine(type: type, wiredName: "cmp");
    }

    private bool HasCapability(TypeSymbol type, string protocol, string wiredName)
    {
        (string FullName, string protocol) cacheKey = (type.FullName, protocol);
        if (_capabilityCache.TryGetValue(key: cacheKey, value: out bool cached))
        {
            return cached;
        }

        // Cycle-breaking: if this key is already on the call stack (a self-referential type,
        // e.g. a record containing itself via a wrapper), return `true` conservatively so the
        // recursion terminates. The conservative assumption cannot produce false positives because
        // any step that proves the type LACKS the capability overwrites the cache before returning.
        if (!_capabilityInProgress.Add(item: cacheKey))
        {
            return true;
        }

        try
        {
            bool result = ComputeCapability(type: type, protocol: protocol, wiredName: wiredName);
            _capabilityCache[key: cacheKey] = result;
            return result;
        }
        finally
        {
            _capabilityInProgress.Remove(item: cacheKey);
        }
    }

    private bool ComputeCapability(TypeSymbol type, string protocol, string wiredName)
    {
        // Generic parameters, error / blank types pass through — they're either further
        // substituted downstream or already a no-op.
        if (type is GenericParameterTypeSymbol or ErrorTypeSymbol || type.IsNone)
        {
            return true;
        }

        // No backend-type shortcut: scalar primitives still define `eq`/`hash` explicitly
        // (see Core/Numerics/*.rf) and will be picked up by the LookupMemberRoutine fallback below.
        // The shortcut used to fire on every `@llvm("…")`-backed record — including aggregate-
        // backed records like `Array[T, N]` (`@llvm("[{N} x {T}]")`) — which masked the
        // generic-constraint check we need for correctness.

        // For a generic resolution G[T1, T2, ...], the generic def's wired-memberRoutine may carry
        // `Ti obeys P` constraints. Each must hold for the corresponding type arg, where
        // the capability being demanded is keyed by P (not by the outer `protocol` we're
        // currently evaluating) — e.g. Array.contains demands `T obeys Equatable`, so we
        // recurse for Equatable on T, not for Container.
        TypeSymbol? genericDef = type switch
        {
            RecordTypeSymbol r => r.GenericDefinition,
            EntityTypeSymbol e => e.GenericDefinition,
            _ => null
        };
        if (genericDef != null && type.TypeArguments is { Count: > 0 } typeArgs &&
            genericDef.GenericParameters is { Count: > 0 } gParams &&
            gParams.Count == typeArgs.Count)
        {
            RoutineInfo? defMemberRoutine =
                LookupMemberRoutine(type: genericDef, memberRoutineName: wiredName);
            if (defMemberRoutine is { GenericConstraints: { Count: > 0 } constraints } &&
                !GenericArgConstraintsHoldForWired(constraints: constraints,
                    gParams: gParams,
                    typeArgs: typeArgs))
            {
                return false;
            }
        }

        // Direct support: type has a CONCRETE impl of the memberRoutine (explicit or synthesised).
        // A lookup that resolves to the ABSTRACT protocol memberRoutine (e.g. `Equatable.eq` for a
        // plain record that neither defines `eq` nor obeys Equatable) does NOT count — it has no
        // body, so reporting capability here would let callers emit a call to the unimplemented
        // abstract symbol (LINKERR). Genuine conformance is established by the TypeObeysProtocol
        // check below (concrete impl) or by obeying the protocol.
        RoutineInfo? direct = LookupMemberRoutine(type: type, memberRoutineName: wiredName);
        if (direct != null && direct.OwnerType is not ProtocolTypeSymbol)
        {
            return true;
        }

        // A name-only lookup returns null when >1 overload shares the name (no first-wins). This is an
        // EXISTENCE check ("does the type host a concrete impl?"), not a unique binding — any concrete
        // overload counts. Probe the candidate set directly so an overloaded member (e.g. a container's
        // `getitem(index:)` + `getitem(range:)`) still reports the capability instead of losing it.
        if (direct == null && HasConcreteMemberOverload(type: type, memberRoutineName: wiredName))
        {
            return true;
        }

        // Marker conformance: the type obeys the named protocol — we expect a body to
        // appear eventually (via auto-synthesis) or for it to be an abstract marker.
        if (TypeObeysProtocol(type: type, protocolName: protocol))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Checks each <c>Ti obeys P</c> constraint on a generic-def wired routine against the corresponding
    /// type argument: for every obeys-constraint whose parameter maps to a slot, the type arg in that
    /// slot must have P's underlying wired capability. Returns false as soon as one fails; unknown
    /// protocols (marker traits with no wired routine) are skipped.
    /// </summary>
    private bool GenericArgConstraintsHoldForWired(List<GenericConstraintDeclaration> constraints,
        List<string> gParams, List<TypeSymbol> typeArgs)
    {
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ConstraintType != ConstraintKind.Obeys ||
                c.ConstraintTypes is not { Count: > 0 } protos)
            {
                continue;
            }

            int idx = FindParamSlot(gParams: gParams, paramName: c.ParameterName);
            if (idx < 0)
            {
                continue;
            }

            if (!ProtocolsHoldForArg(protos: protos, argType: typeArgs[index: idx]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Finds the zero-based slot of <paramref name="paramName"/> in <paramref name="gParams"/>,
    /// or -1 when not present.
    /// </summary>
    private static int FindParamSlot(List<string> gParams, string paramName)
    {
        for (int i = 0; i < gParams.Count; i++)
        {
            if (gParams[index: i] == paramName)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Returns false as soon as one of <paramref name="protos"/> names a protocol whose wired
    /// routine <paramref name="argType"/> does not satisfy. Unknown protocols (marker traits with
    /// no canonical wired routine) are skipped — they carry no wired capability to verify.
    /// </summary>
    private bool ProtocolsHoldForArg(List<TypeExpression> protos, TypeSymbol argType)
    {
        return protos.Select(selector: protoExpr => protoExpr.Name)
                     .Where(predicate: name => _protocolToWired.ContainsKey(key: name))
                     .All(predicate: name => HasCapability(type: argType,
                          protocol: name,
                          wiredName: _protocolToWired[key: name]));
    }

    /// <summary>
    /// Returns true iff <paramref name="type"/> can auto-derive <c>Assignable</c>:
    /// its LLVM layout contains no <c>ptr</c> (and is not zero-sized in a way that
    /// indicates a managed reference). Concretely:
    /// <list type="bullet">
    ///   <item><description>Records: <see cref="RecordTypeSymbol.LlvmType"/> contains no "ptr" substring.</description></item>
    ///   <item><description>Choice/Flags: always (tag-only layout).</description></item>
    ///   <item><description>Tuples: every element is auto-deriveable.</description></item>
    ///   <item><description>Entities, wrappers, variants, crashables, protocols: never (always ptr-shaped).</description></item>
    ///   <item><description>Routines: yes — a routine value is a NON-OWNING ptr (fnptr / closure blob), bitwise-copyable like a stored C function pointer.</description></item>
    ///   <item><description>Generic parameters: false (decision deferred to instantiation).</description></item>
    /// </list>
    /// Raw-pointer types like <c>Hijacked[T]</c> and <c>CPtr</c> are ptr-shaped and
    /// therefore must opt in manually with a trivial <c>assign() -> Me  return me</c>.
    /// </summary>
    public bool CanAutoDeriveAssignable(TypeSymbol type)
    {
        return type switch
        {
            ChoiceTypeSymbol => true,
            FlagsTypeSymbol => true,
            // A routine VALUE is a plain, NON-OWNING ptr (a bare fnptr / a closure blob = C's
            // `(fnptr[, userdata])`). Bitwise-dup is sound — it aliases the same callable with no
            // owned resource to double-free (its lifecycle Store/Destroy are both null). So a routine
            // is a freely-copyable ptr leaf, like a stored C function pointer.
            RoutineTypeSymbol => true,
            TupleTypeSymbol tuple => tuple.ElementTypes.All(predicate: CanAutoDeriveAssignable),
            RecordTypeSymbol record => !record.IsGenericDefinition &&
                                     !LayoutContainsPtr(layout: record.LlvmType),
            _ => false
        };
    }

    /// <summary>
    /// Returns true iff <paramref name="type"/> can auto-derive <c>Assignable</c> by FIELD-WALK: every field /
    /// element is itself Assignable — it obeys <c>Assignable</c> (a managed leaf <c>Text</c>/<c>Integer</c>, or
    /// an RC wrapper) or cascades. Unlike <see cref="CanAutoDeriveAssignable"/> (no-ptr → a bitwise copy that
    /// is also <c>Copyable</c>), this PERMITS managed (ptr) fields — the synthesized <c>store</c> field-walks
    /// them, calling each field's <c>store</c>. Blocked by an <c>entity</c> or access-token field (no store).
    /// Wrapper types are excluded here — RC wrappers derive <c>Assignable</c> via
    /// <c>ProtocolConformanceAnalyzer.ApplyAutoAssignableConformance</c>, and the borrow/access tokens are
    /// deliberately unAssignable (scope-bound). This is the "identity-less → freely copyable" predicate,
    /// structural — the store-based replacement for the arbitrary <c>IsTriviallyAssignable</c> heuristic.
    /// </summary>
    public bool CanMemberVariableWalkAssignable(TypeSymbol type)
    {
        string? wrapperBase = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } gd } => gd.Name,
            RecordTypeSymbol r => r.Name,
            _ => null
        };
        if (wrapperBase != null && RuntimeContract.WrapperTypes.Contains(item: wrapperBase))
        {
            return false;
        }

        // NOTE: an RC-handle field makes the containing record NON-Assignable — the RC wrappers no longer
        // obey `Assignable` and are NOT recognised structurally here. This is intended: `var b = rc` is
        // rejected (explicit `.share()` only), so a record HOLDING an RC must likewise not be implicitly
        // copied (that would silently share the handle). Reconstruct it explicitly instead
        // (WithBaseNotAssignable / RF-S420).
        bool MemberVariableAssignable(TypeSymbol f)
        {
            return CanAutoDeriveAssignable(type: f) || CanMemberVariableWalkAssignable(type: f) ||
                   TypeObeysProtocol(type: f, protocolName: "Assignable");
        }

        return type switch
        {
            ChoiceTypeSymbol or FlagsTypeSymbol => true,
            TupleTypeSymbol t => t.ElementTypes.All(predicate: MemberVariableAssignable),
            RecordTypeSymbol { IsGenericDefinition: false } r => r.MemberVariables is { Count: > 0 }
                ? r.MemberVariables.All(predicate: m => MemberVariableAssignable(f: m.Type))
                // No AST member variables: an `@llvm` inline-storage record (Array[T,N], Vector[T,N])
                // stores its type-KIND generic args INLINE — cascade storability to them so
                // Array[Text] is Assignable (Text is) but Array[SomeEntity] is NOT (entity has no
                // store). A const-generic VALUE arg (N) stores nothing → filtered out. Wrapper
                // records are excluded above. A field-less record with no type args ⇒ vacuously
                // Assignable (All over empty).
                : (r.TypeArguments ?? []).Where(predicate: a =>
                                              a.Category != TypeModel.Enums.TypeCategory
                                                 .ConstGenericValue)
                                         .All(predicate: MemberVariableAssignable),
            _ => false
        };
    }

    /// <summary>
    /// ④ standard-impl eligibility evaluator (<c>needs P everywhere</c>): the CONCRETE type
    /// <paramref name="type"/> obeys <paramref name="protocol"/> structurally IFF EVERY member
    /// (allmemvarof per kind) obeys it — ∀-only, concrete-only. Per-member verdict uses the declared
    /// conformance query (<see cref="TypeObeysProtocol"/>), matching how <c>needs T obeys P</c> is
    /// checked. Empty member set (field-less record, choice/flags) ⇒ vacuously true.
    /// <para>
    /// WRAPPER types (RC handles, raw-pointer <c>Hijacked</c>/<c>CPtr</c>, scope-bound access tokens) are
    /// excluded up front — a raw/shared handle is never structurally copyable (bitwise dup would alias →
    /// double-free), and the Assignable wrappers declare their conformance explicitly (so the gate would skip
    /// them anyway). An <c>@llvm</c> aggregate (Array[T,N], Vector[T,N]) has NO AST member variables — its
    /// elements live in the layout string — so <see cref="MemberProjection"/> cascades to its type-KIND
    /// generic args, making <c>Array[Entity]</c> correctly NON-conforming rather than vacuously true.
    /// </para>
    /// </summary>
    public bool EverywhereObeys(TypeSymbol type, string protocol)
    {
        string? wrapperBase = type switch
        {
            RecordTypeSymbol { GenericDefinition: { } gd } => gd.Name,
            RecordTypeSymbol r => r.Name,
            _ => null
        };
        if (wrapperBase != null && RuntimeContract.WrapperTypes.Contains(item: wrapperBase))
        {
            return false;
        }

        return MemberProjection(type: type)
           .All(predicate: m => TypeObeysProtocol(type: m, protocolName: protocol));
    }

    /// <summary>
    /// Kind-appropriate member sequence for the <c>everywhere</c> quantifier: record/entity →
    /// member-variable types (allmemvarof), variant → live branch payload types (branchof, skipping the
    /// payload-less None branch), choice/flags → none (scalar discriminant, no member types). A TUPLE is
    /// NOT special-cased: by the time <c>everywhere</c> is evaluated it has been lowered to a
    /// <see cref="RecordTypeSymbol"/> (positional member variables), so it flows through the record arm. An
    /// <c>@llvm</c> type has no member variables, so <c>everywhere</c> does not apply to it — it declares
    /// its conformance explicitly instead.
    /// </summary>
    // Choice/Flags/Variant derive from RecordTypeSymbol, so the more-derived arms MUST precede the record
    // arm (else CS8510 unreachable). Variant → branchof; choice/flags → none; plain record/entity →
    // allmemvarof.
    private static IEnumerable<TypeSymbol> MemberProjection(TypeSymbol type)
    {
        return type switch
        {
            ChoiceTypeSymbol or FlagsTypeSymbol => [],
            VariantTypeSymbol { IsGenericDefinition: false } v => v.Members
               .Where(predicate: m => m.Type != null)
               .Select(selector: m => m.Type!),
            RecordTypeSymbol { IsGenericDefinition: false } r => r.MemberVariables is
                { Count: > 0 } mv
                ? mv.Select(selector: m => m.Type)
                // No AST members: an `@llvm` inline-storage record (Array[T,N], Vector[T,N]) stores its
                // type-KIND generic args INLINE — cascade to them (const-generic N filtered) so Array[Entity]
                // is NOT vacuously conforming. A truly field-less record with no type args ⇒ [] ⇒ vacuous.
                : (r.TypeArguments ?? []).Where(predicate: a =>
                    a.Category != TypeModel.Enums.TypeCategory.ConstGenericValue),
            EntityTypeSymbol { IsGenericDefinition: false } e => e.MemberVariables.Select(
                selector: m => m.Type),
            _ => []
        };
    }


    private static bool LayoutContainsPtr(string layout)
    {
        // "void" (zero-sized) and any layout without "ptr" auto-derives.
        // Substring check is safe: LLVM type syntax uses "ptr" only as a literal type
        // token; no primitive scalar contains the substring (i8, i64, f32, [N x T], etc.).
        return layout.Contains(value: "ptr", comparisonType: StringComparison.Ordinal);
    }

    /// <summary>
    /// Public conformance probe by protocol NAME — backs the buildtime <c>m.obeys(Protocol)</c> expand
    /// projection. Returns true when <paramref name="type"/> either DECLARES the protocol (walks
    /// <c>ImplementedProtocols</c> transitively) OR STRUCTURALLY satisfies it by supplying a concrete
    /// (non-abstract) impl of every memberRoutine the protocol requires. The structural arm matters for
    /// synthesized capabilities that aren't spelled as an explicit <c>obeys</c>: e.g. every
    /// Record/Entity/Variant gets a synthesized <c>serialize()</c>, so a scalar field like <c>S32</c>
    /// satisfies <c>Serializable</c> (it has the memberRoutine) even though it lists only numeric protocols.
    /// A positive result therefore guarantees the protocol's wired memberRoutine resolves to a real body —
    /// safe for a derive template to gate a <c>me.field.serialize()</c> call on (else fall back to
    /// <c>represent</c>), and the buildtime-if prune then drops the untaken branch before codegen.
    /// </summary>
    public bool DoesTypeObeyProtocol(TypeSymbol type, string protocolName)
    {
        if (TypeObeysProtocol(type: type, protocolName: protocolName))
        {
            return true;
        }

        // Structural satisfaction: the type has a concrete impl of every memberRoutine the protocol declares.
        // The required names come from the protocol's OWN declared memberRoutines (plus those it inherits from
        // parent protocols), NOT from GetMemberRoutinesForType(...).Where(OwnerType is ProtocolTypeSymbol) — the
        // latter can be empty for a protocol whose sole memberRoutine was substituted onto a non-protocol owner
        // (e.g. Serializable's `serialize`), which then wrongly reported EVERY structural implementer as
        // NON-conforming. That masked itself for records/entities that obey via a declared bundle
        // (RecordType/EntityType), but broke a type like `List[Text]` that carries a conditionally-
        // available `serialize` without declaring Serializable — its derived `serialize()` field-walk
        // then boxed `represent()` instead of recursing (the json_encode `tags` regression).
        if (LookupType(name: protocolName) is not ProtocolTypeSymbol proto)
        {
            return false;
        }

        HashSet<string> requiredNames = CollectProtocolMemberRoutineNames(proto: proto);
        return requiredNames.Count > 0 && requiredNames.All(predicate: name =>
            LookupMemberRoutine(type: type, memberRoutineName: name) is
                { OwnerType: not ProtocolTypeSymbol });
    }

    /// <summary>
    /// The set of memberRoutine names a protocol requires an implementer to provide — its own declared
    /// <see cref="ProtocolTypeSymbol.MemberRoutines"/> plus every name inherited from its parent protocols
    /// (transitively). Used by the structural-conformance path of <see cref="DoesTypeObeyProtocol"/>.
    /// </summary>
    private static HashSet<string> CollectProtocolMemberRoutineNames(ProtocolTypeSymbol proto)
    {
        var names = new HashSet<string>(comparer: StringComparer.Ordinal);
        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        var stack = new Stack<ProtocolTypeSymbol>();
        stack.Push(item: proto);
        while (stack.Count > 0)
        {
            ProtocolTypeSymbol current = stack.Pop();
            if (!seen.Add(item: current.Name))
            {
                continue;
            }

            foreach (ProtocolMemberRoutineInfo m in current.MemberRoutines)
            {
                names.Add(item: m.Name);
            }

            foreach (ProtocolTypeSymbol parent in current.ParentProtocols)
            {
                stack.Push(item: parent);
            }
        }

        return names;
    }

    /// <summary>
    /// The SINGLE authority for "does this concrete <paramref name="type"/> obey the protocol named
    /// <paramref name="protocolName"/>" via its DECLARED conformance: the type's own
    /// <c>ImplementedProtocols</c> (+ their parent chain), gated by any <c>onlyif</c> clause, plus the
    /// reflexive marker-protocol rule. Both the generic-constraint gate (<see cref="ImplementerSatisfiesConstraint"/>)
    /// and the SA-level <c>SemanticVerifier.ImplementsProtocol</c> delegate here for the declared-conformance
    /// check (the latter adds category/generic-param/structural cases around this core). Matches on either the
    /// exact name or the bare (bracket-stripped) name, so a parameterised <c>Controlling[List[S64]]</c> and the
    /// registered generic-def <c>Controlling</c> both resolve.
    /// </summary>
    internal bool TypeObeysProtocol(TypeSymbol type, string protocolName)
    {
        // Marker reference protocols (Accessing/Controlling) are obeyed reflexively by any non-entity
        // type — folded here so every caller (constraint gate + ImplementsProtocol) shares the one rule.
        if (SatisfiesMarkerProtocolReflexively(implementer: type, protocolName: protocolName))
        {
            return true;
        }

        List<TypeSymbol>? implemented = type switch
        {
            ChoiceTypeSymbol c => c.ImplementedProtocols,
            FlagsTypeSymbol f => f.ImplementedProtocols,
            RecordTypeSymbol r => r.ImplementedProtocols,
            EntityTypeSymbol e => e.ImplementedProtocols,
            _ => null
        };
        if (implemented == null)
        {
            return false;
        }

        // Reduce the target to the registry's ONE canonical protocol object, then match every implemented
        // protocol (+ its parent chain) by reference IDENTITY against it — no name-string equality anywhere.
        // Canonicalizing both sides through the registry (rather than trusting the object a type happened to
        // store at declaration time) is what makes identity reliable despite realm/registration duplication.
        TypeSymbol? targetDef = CanonicalProtocolDef(t: LookupType(name: protocolName));
        if (targetDef == null)
        {
            return false;
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);
        if (!implemented.Any(predicate: p => Walk(candidate: p, tgtDef: targetDef, seenSet: seen)))
        {
            return false;
        }

        // Conditional-conformance gate: an `obeys P onlyif (…)` protocol is obeyed by a concrete
        // generic INSTANCE only when the clause's conditions hold for its bound type args.
        return ConditionalConformanceHolds(type: type, protocolName: protocolName);

        bool Walk(TypeSymbol candidate, TypeSymbol tgtDef, HashSet<string> seenSet)
        {
            if (!seenSet.Add(item: candidate.Name))
            {
                return false;
            }

            if (ReferenceEquals(objA: CanonicalProtocolDef(t: candidate), objB: tgtDef))
            {
                return true;
            }

            TypeSymbol latest = LookupType(name: candidate.Name) ?? candidate;
            if (latest is ProtocolTypeSymbol proto)
            {
                return proto.ParentProtocols.Any(predicate: parent =>
                    Walk(candidate: parent, tgtDef: tgtDef, seenSet: seenSet));
            }

            return false;
        }
    }

    /// <summary>The registry's single CANONICAL object for the protocol <paramref name="t"/> names — its
    /// generic definition, re-fetched through <see cref="LookupType(string)"/> so two references to the "same"
    /// protocol (one stored on a type's <c>ImplementedProtocols</c>, one freshly resolved) collapse to ONE
    /// object that reference-identity can compare. Returns the type unchanged when it is not a protocol.</summary>
    private TypeSymbol? CanonicalProtocolDef(TypeSymbol? t)
    {
        if (t is not ProtocolTypeSymbol p)
        {
            return t;
        }

        TypeSymbol def = p.GenericDefinition ?? p;
        return LookupType(name: def.Name) ?? def;
    }

    /// <summary>
    /// Evaluates an <c>obeys P onlyif (param obeys proto, …)</c> clause for a concrete generic instance:
    /// true unless <paramref name="type"/> is an instance whose definition declared conditions for
    /// <paramref name="protocolName"/> and some condition fails for the bound type argument. Permissive on
    /// anything it cannot evaluate (a bare def with no args, an unknown param) — the clause only ever
    /// TIGHTENS an already-positive conformance, never widens it.
    /// </summary>
    internal bool ConditionalConformanceHolds(TypeSymbol type, string protocolName)
    {
        if (type.TypeArguments is not { Count: > 0 } args)
        {
            return true;
        }

        TypeSymbol? def = LookupType(name: type.BareName);
        Dictionary<string, List<(string ParamName, string ProtocolName)>>? map = def switch
        {
            EntityTypeSymbol e => e.ConditionalObeys,
            RecordTypeSymbol r => r.ConditionalObeys,
            _ => null
        };
        if (map == null ||
            !map.TryGetValue(key: protocolName, value: out List<(string, string)>? conds))
        {
            return true;
        }

        List<string>? defParams = def!.GenericParameters;
        if (defParams == null)
        {
            return true;
        }

        foreach ((string param, string proto) in conds)
        {
            int idx = defParams.IndexOf(item: param);
            if (idx < 0 || idx >= args.Count)
            {
                continue;
            }

            if (!TypeObeysProtocol(type: args[index: idx], protocolName: proto))
            {
                return false;
            }
        }

        return true;
    }
}
