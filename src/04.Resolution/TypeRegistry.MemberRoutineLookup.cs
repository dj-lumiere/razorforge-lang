using System;
using System.Collections.Generic;
using System.Linq;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Resolution;

using TypeInfo = TypeInfo;

public sealed partial class TypeRegistry
{
    #region Routine Registration and Lookup

    /// <summary>
    /// Divergent cross-file duplicate constructors found during registration: two creators sharing a
    /// signature but with DIFFERENT bodies, defined in DIFFERENT files. Registration is last-wins, so
    /// one silently shadows the other — the hazard class that made <c>F64(from: F128)</c> resolve to a
    /// recursive-forwarder stub instead of the real engine impl (infinite recursion). Surfaced as a
    /// build error by <see cref="Verification.SemanticVerifier"/>. Benign identical duplicates (same
    /// body, e.g. <c>U16(from: U8)</c> in both U8.rf and U16.rf) are NOT recorded (equal BodyHash).
    /// </summary>
    public List<(RoutineInfo First, RoutineInfo Second)> DivergentDuplicateCreators { get; } = [];

    /// <summary>
    /// Location-free structural hash of a constructor body for the divergent-duplicate guard (source
    /// text, not record ToString which embeds SourceLocation — so identical logic in two files hashes
    /// equal). Null for empty / extern (PassStatement) bodies. Computed only for creators by the two
    /// registration paths (StdlibLoader, SignatureResolver).
    /// </summary>
    public static int? ComputeCreatorBodyHash(Statement? body)
    {
        if (body is null or PassStatement) return null;
        return body.Accept(visitor: new Builder.RfSyntaxTreePrinter())
                   .GetHashCode(comparisonType: StringComparison.Ordinal);
    }

    /// <summary>Registers a routine by its <see cref="RoutineInfo.RegistryKey"/> (overload-exact) and <see cref="RoutineInfo.BaseName"/> (first-match unqualified).</summary>
    public void RegisterRoutine(RoutineInfo routine)
    {
        // Realm-aware storage key: ambient-realm routines key bare (unchanged); a bridged-realm routine
        // (e.g. the RF-realm Core.List reached via RF:: inside an SF compile) is `{realm}::`-prefixed so it
        // never collides with / shadows the ambient same-signature routine — the two world-lines coexist.
        string registryKey = RealmRoutineKey(routine: routine);
        string baseName = routine.BaseName;

        // Register under RegistryKey for exact overload matching.
        bool keyExisted =
            _routines.TryGetValue(key: registryKey, value: out RoutineInfo? existingByKey);
        RegisterRoutineByKey(routine: routine, registryKey: registryKey,
            keyExisted: keyExisted, existingByKey: existingByKey);

        // Also register under base name (first overload wins for unqualified lookup)
        if (!_routines.ContainsKey(key: baseName))
        {
            _routines[key: baseName] = routine;
        }

        // Index by module-qualified name for unambiguous lookup
        string qualifiedName = routine.QualifiedName;
        if (qualifiedName != registryKey && qualifiedName != baseName)
        {
            _routinesByQualifiedName.TryAdd(key: qualifiedName, value: routine);
        }

        // Index by owner type → memberRoutine name → overloads for fast O(1) `owner.MemberRoutine` lookup.
        if (routine.OwnerType != null)
        {
            RegisterRoutineByOwner(routine: routine, registryKey: registryKey, keyExisted: keyExisted);
        }

        // Free-function (owner-less) overloads: fold into _routinesByOwner under the canonical FreeOwnerKey.
        if (routine.OwnerType == null)
        {
            RegisterFreeRoutine(routine: routine, baseName: baseName);
        }
    }

    /// <summary>
    /// Stores <paramref name="routine"/> under its exact <paramref name="registryKey"/>, recording the
    /// divergent cross-file duplicate-constructor guard and honoring the rule that a synthesized routine
    /// never overwrites an existing user-written one.
    /// </summary>
    private void RegisterRoutineByKey(RoutineInfo routine, string registryKey, bool keyExisted,
        RoutineInfo? existingByKey)
    {
        // Never let a synthesized (builder-generated) routine overwrite a user-written one:
        // explicit user routines override synthetic same-signature defaults (e.g., a user
        // `T.create(field: Foo)` overrides the auto-generated record field constructor).
        if (keyExisted)
        {
            // Divergent cross-file duplicate constructor guard (see DivergentDuplicateCreators):
            // same signature + SAME failability, both real (non-synthetic), different files, DIFFERENT
            // bodies. Failability must match: a checked `T!(from: X)` and a reinterpret `T(from: X)`
            // legitimately share a signature (they coexist via the owner+IsFailable index) and are NOT a
            // divergent duplicate — only same-failability same-signature different-body pairs are the bug.
            if (existingByKey is { IsSynthesized: false, BodyHash: { } h1 }
                && routine is { IsSynthesized: false, BodyHash: { } h2 }
                && existingByKey.IsFailable == routine.IsFailable
                && h1 != h2
                && existingByKey.Location?.FileName is { } f1
                && routine.Location?.FileName is { } f2
                && !string.Equals(a: f1, b: f2, comparisonType: StringComparison.OrdinalIgnoreCase))
            {
                DivergentDuplicateCreators.Add(item: (existingByKey, routine));
            }

            bool existingIsUser = !existingByKey!.IsSynthesized;
            bool incomingIsSynthetic = routine.IsSynthesized;
            if (!(existingIsUser && incomingIsSynthetic))
                _routines[key: registryKey] = routine;
        }
        else
        {
            _routines[key: registryKey] = routine;
        }
    }

    /// <summary>
    /// Adds <paramref name="routine"/> to the by-owner overload index. A bare generic-param owner
    /// (`routine T.m()`) is normalized to the canonical GenericOwnerKey so its default-impl member
    /// routines resolve by name off this same store. Re-registrations (same key + Me-constraint set)
    /// replace in place instead of appending.
    /// </summary>
    private void RegisterRoutineByOwner(RoutineInfo routine, string registryKey, bool keyExisted)
    {
        string ownerKey = routine.OwnerType is GenericParameterTypeInfo
            ? GenericOwnerKey
            : RealmRegistryKey(type: routine.OwnerType!);
        if (!_routinesByOwner.TryGetValue(key: ownerKey, value: out Dictionary<string, List<RoutineInfo>>? byName))
        {
            byName = new Dictionary<string, List<RoutineInfo>>(comparer: StringComparer.Ordinal);
            _routinesByOwner[key: ownerKey] = byName;
        }
        if (!byName.TryGetValue(key: routine.Name, value: out List<RoutineInfo>? list))
        {
            list = [];
            byName[key: routine.Name] = list;
        }

        // Dedup by (RegistryKey, Me-constraint set): a re-registered routine (same owner and
        // signature) REPLACES its prior list entry instead of appending. Appending duplicates
        // here let memberRoutine resolution iterate stale-and-fresh copies of the same overload and pick
        // order-dependently — a non-determinism that manifested as platform-specific codegen.
        // Failability is NOT part of the identity: a name maps to at most one routine (declaring
        // both `mul` and `mul!` is a name collision, not two coexisting routines), and `!` is
        // never in the name. The dedup scan only runs when the RegistryKey was already present
        // (`keyExisted`); a key's first registration stays an O(1) append. User-written routines
        // are never replaced by a synthesized same-identity routine.
        if (keyExisted)
        {
            // Identity includes the Me-constraint set: several `needs Me is VariantType` /
            // `obeys X`-gated protocol-default bodies share a RegistryKey (same signature) yet are
            // DISTINCT overloads that must coexist so within-dispatch can pick the kind-matched
            // one. Only a truly same-signature, same-constraint re-registration replaces in place.
            int existingIdx = list.FindIndex(match: r =>
                r.RegistryKey == registryKey && SameMeConstraintSet(a: r, b: routine));
            if (existingIdx < 0)
                list.Add(item: routine);
            else if (!(!list[index: existingIdx].IsSynthesized && routine.IsSynthesized))
                list[index: existingIdx] = routine;
        }
        else
        {
            list.Add(item: routine);
        }
    }

    /// <summary>
    /// Folds an owner-less (free / independent) <paramref name="routine"/> into <c>_routinesByOwner</c>
    /// under the canonical FreeOwnerKey, keyed by base name → overloads (append + reference-dedup).
    /// </summary>
    private void RegisterFreeRoutine(RoutineInfo routine, string baseName)
    {
        if (!_routinesByOwner.TryGetValue(key: FreeOwnerKey, value: out Dictionary<string, List<RoutineInfo>>? freeByName))
        {
            freeByName = new Dictionary<string, List<RoutineInfo>>(comparer: StringComparer.Ordinal);
            _routinesByOwner[key: FreeOwnerKey] = freeByName;
        }
        if (!freeByName.TryGetValue(key: baseName, value: out List<RoutineInfo>? overloadList))
        {
            overloadList = [];
            freeByName[key: baseName] = overloadList;
        }
        if (!overloadList.Contains(item: routine))
            overloadList.Add(item: routine);
    }

    /// <summary>
    /// Checks if a routine with the given key is registered.
    /// </summary>
    public bool HasRoutine(string key) => _routines.ContainsKey(key: key);

    /// <summary>
    /// Looks up a routine overload that matches the given argument types.
    /// Falls back to the default (first-registered) overload if no exact match.
    /// </summary>
    /// <param name="baseName">The routine's base name (e.g., "List.append", "IO.show").</param>
    /// <param name="argTypes">The argument types to match against.</param>
    public RoutineInfo? LookupRoutineOverload(string baseName, List<TypeInfo> argTypes)
    {
        // Try exact overload match by RegistryKey format.
        // Zero-arg routines register under baseName (no '#' suffix) — match that directly.
        string paramTypeNames =
            string.Join(separator: ",",
                values: argTypes.Select(RoutineInfo.GetTypeIdentity));
        string registryKey = argTypes.Count == 0 ? baseName : $"{baseName}#{paramTypeNames}";
        if (_routines.TryGetValue(key: registryKey, value: out RoutineInfo? overload))
        {
            return overload;
        }

        // Core-prefix fallback: bare callee names (e.g., "rf_allocate_dynamic_uninit") register
        // under "Core.rf_allocate_dynamic_uninit#…". Mirror LookupRoutine's behavior so overload
        // resolution can find module-qualified registrations from unqualified call sites.
        if (!baseName.Contains(value: '.'))
        {
            string coreKey = argTypes.Count == 0
                ? $"Core.{baseName}"
                : $"Core.{baseName}#{paramTypeNames}";
            if (_routines.TryGetValue(key: coreKey, value: out RoutineInfo? coreOverload))
            {
                return coreOverload;
            }
        }

        // Try matching generic overloads by reconstructing the generic parameter pattern.
        if (MatchGenericOverloadByPattern(baseName: baseName, argTypes: argTypes) is { } genericOverload)
        {
            return genericOverload;
        }

        // Structural candidate search: iterate all overloads registered for this base name and
        // match positionally by full type identity (module-qualified, includes generic args).
        // Runs before the first-wins fallback so multi-overload disambiguation is type-correct.
        if (MatchStructuralFreeOverload(baseName: baseName, argTypes: argTypes) is { } structuralMatch)
        {
            return structuralMatch;
        }

        // Fall back to the first-registered overload ONLY when there is a single overload (first-wins is
        // then the correct, unambiguous choice). With ≥2 overloads and no exact/generic/structural match,
        // first-wins silently picks the WRONG one — the overloads are declared S8, S16, S32, S64, U64… so
        // it always returns the S8 conversion regardless of the argument type (the reported bug). Return
        // null instead; the caller reports ArgumentTypeMismatch rather than emitting a mis-typed call.
        if (HasMultipleRegisteredOverloads(baseName: baseName)) return null;
        return LookupRoutine(fullName: baseName);
    }

    /// <summary>True if <paramref name="baseName"/> has more than one registered overload (keys
    /// <c>baseName</c> and/or <c>baseName#…</c>). Used to gate the first-wins fallback, which is only
    /// correct for a lone overload.</summary>
    private bool HasMultipleRegisteredOverloads(string baseName)
    {
        int n = 0;
        string prefix = baseName + "#";
        foreach (string k in _routines.Keys)
        {
            if (k == baseName || k.StartsWith(value: prefix, comparisonType: StringComparison.Ordinal))
            {
                if (++n > 1) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Tries to match a generic overload by reconstructing the generic-parameter pattern from a generic
    /// argument. e.g. arg <c>SortedSet[S64]</c> → its def <c>SortedSet[T]</c> → key
    /// <c>{baseName}#SortedSet[T]</c>. Skips variadic overloads (the variadic fallback handles those).
    /// </summary>
    private RoutineInfo? MatchGenericOverloadByPattern(string baseName, List<TypeInfo> argTypes)
    {
        foreach (TypeInfo argType in argTypes)
        {
            if (!argType.IsGenericResolution)
            {
                continue;
            }

            TypeInfo? genericDef = argType switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                _ => null
            };
            if (genericDef?.GenericParameters == null)
            {
                continue;
            }

            string genericArgName = RoutineInfo.GetTypeIdentity(type: genericDef);
            string genericRegistryKey = $"{baseName}#{genericArgName}";
            if (_routines.TryGetValue(key: genericRegistryKey,
                    value: out RoutineInfo? genericOverload) &&
                !genericOverload
                   .IsVariadic) // Skip variadic overloads — handled by variadic fallback
            {
                return genericOverload;
            }
        }

        return null;
    }

    /// <summary>
    /// Structural candidate search over the free-overload list for <paramref name="baseName"/> (with the
    /// Core-prefix fallback): matches positionally by full type identity. Only disambiguates when more
    /// than one overload exists; returns null otherwise.
    /// </summary>
    private RoutineInfo? MatchStructuralFreeOverload(string baseName, List<TypeInfo> argTypes)
    {
        List<RoutineInfo>? overloadCandidates = FreeOverloads(baseName: baseName);
        if (overloadCandidates == null && !baseName.Contains(value: '.'))
        {
            overloadCandidates = FreeOverloads(baseName: $"Core.{baseName}");
        }
        if (overloadCandidates is { Count: > 1 })
        {
            foreach (RoutineInfo candidate in overloadCandidates)
            {
                if (candidate.Parameters.Count != argTypes.Count) continue;
                bool match = true;
                for (int i = 0; i < argTypes.Count; i++)
                {
                    if (RoutineInfo.GetTypeIdentity(type: candidate.Parameters[index: i].Type) !=
                        RoutineInfo.GetTypeIdentity(type: argTypes[index: i]))
                    {
                        match = false;
                        break;
                    }
                }

                if (match) return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up the routine registered under an exact <see cref="RoutineInfo.RegistryKey"/>.
    /// Unlike <see cref="LookupRoutineOverload"/> this applies no Core-prefix or generic
    /// fallbacks — it answers "is this precise owner+name+signature slot occupied?", which
    /// reserved-variant collision detection needs to compare a generated variant against any
    /// hand-written routine sharing its key.
    /// </summary>
    public RoutineInfo? GetRoutineByExactKey(string registryKey) =>
        _routines.TryGetValue(key: registryKey, value: out RoutineInfo? routine) ? routine : null;

    /// <summary>
    /// Looks up a routine by its full name. A routine's identity is (owner, bare-name) — the
    /// failable `!` is NOT part of the name, so a name maps to at most ONE routine and failability
    /// is an attribute read off that routine, never a lookup key. When <paramref name="isFailable"/>
    /// is given it filters the found routine (returns null on a failability mismatch) so a bare call
    /// can retry for the failable-only form; it never selects between two same-named variants
    /// (declaring both `foo` and `foo!` is a name collision, not two coexisting routines).
    /// </summary>
    /// <param name="fullName">The fully qualified name of the routine.</param>
    /// <param name="isFailable">If non-null, require the routine's failability to match.</param>
    /// <returns>The routine info if found, null otherwise.</returns>
    public RoutineInfo? LookupRoutine(string fullName, bool? isFailable = null)
    {
        RoutineInfo? routine =
            _routines.GetValueOrDefault(key: fullName)
            ?? _routineResolutions.GetValueOrDefault(key: fullName)
            ?? _routinesByQualifiedName.GetValueOrDefault(key: fullName)
            ?? (!fullName.Contains(value: '.')
                ? _routines.GetValueOrDefault(key: $"Core.{fullName}")
                : null);

        if (routine == null) return null;
        return isFailable != null && routine.IsFailable != isFailable.Value ? null : routine;
    }

    /// <summary>
    /// Looks up a routine by its module-qualified name (e.g., "Core.S8.add").
    /// </summary>
    public RoutineInfo? LookupRoutineByQualifiedName(string qualifiedName)
    {
        return _routinesByQualifiedName.GetValueOrDefault(key: qualifiedName);
    }

    /// <summary>
    /// Looks up a routine by its short name (without module prefix).
    /// Used by codegen when the AST has "Console.show" but the registry key is "IO.show".
    /// Falls back to a linear scan only when neither fast-path dictionaries find a match.
    /// </summary>
    public RoutineInfo? LookupRoutineByName(string name, bool? isFailable = null)
    {
        // Fast path: the primary _routines index covers the common case; fall back to a Core prefix
        // and finally a targeted linear scan for codegen short-name lookups.
        if (_routines.TryGetValue(key: name, value: out RoutineInfo? found) &&
            found.OwnerType == null &&
            (isFailable == null || found.IsFailable == isFailable))
            return found;

        if (_routines.TryGetValue(key: $"Core.{name}", value: out found) &&
            found.OwnerType == null &&
            (isFailable == null || found.IsFailable == isFailable))
            return found;

        // Fallback: targeted linear scan (rare; used only by codegen short-name lookups)
        return _routines.Values.FirstOrDefault(routine =>
            routine.Name == name &&
            routine.OwnerType == null &&
            (isFailable == null || routine.IsFailable == isFailable));
    }

    /// <summary>
    /// Finds a generic overload of a free function by name (e.g., show[T] for "show").
    /// Backed by <see cref="GenericFreeFunctions"/>, which scans the FreeOwnerKey store.
    /// </summary>
    /// <param name="name">The routine name (without generic params).</param>
    /// <param name="preferredArity">Expected argument count; -1 means any arity is acceptable.</param>
    public RoutineInfo? LookupGenericOverload(string name, int preferredArity = -1)
    {
        List<RoutineInfo> candidates = GenericFreeFunctions(name: name);
        if (candidates.Count == 0)
            return null;

        // Prefer non-variadic overloads matching the preferred arity first.
        RoutineInfo? arityMismatch = null;
        RoutineInfo? variadicFallback = null;
        foreach (RoutineInfo routine in candidates)
        {
            if (routine.IsVariadic) { variadicFallback ??= routine; continue; }
            if (preferredArity < 0 || routine.Parameters.Count == preferredArity) return routine;
            arityMismatch ??= routine;
        }

        return variadicFallback ?? arityMismatch;
    }

    /// <summary>
    /// Finds a variadic generic overload of a free function by name (e.g., show[T](values...: T) for "show").
    /// Backed by <see cref="GenericFreeFunctions"/>, which scans the FreeOwnerKey store.
    /// </summary>
    public RoutineInfo? LookupVariadicGenericOverload(string name)
        => GenericFreeFunctions(name: name).FirstOrDefault(routine => routine.IsVariadic);

    /// <summary>
    /// Updates a routine with resolved parameters and return type.
    /// Used for external declarations that are registered in Phase 1 without params.
    /// </summary>
    /// <param name="routine">The routine to update.</param>
    /// <param name="parameters">The resolved parameters.</param>
    /// <param name="returnType">The resolved return type.</param>
    /// <param name="genericParameters">Updated generic parameters (may include implicit ones from protocol-as-type).</param>
    /// <param name="genericConstraints">Updated generic constraints (may include implicit ones from protocol-as-type).</param>
    public void UpdateRoutine(RoutineInfo routine, List<ParameterInfo> parameters,
        TypeInfo? returnType, List<string>? genericParameters,
        List<GenericConstraintDeclaration>? genericConstraints)
    {
        string baseName = routine.BaseName;
        if (!_routines.ContainsKey(key: baseName))
        {
            return;
        }

        // Create updated routine with resolved signature
        var updatedRoutine = new RoutineInfo(name: routine.Name)
        {
            Kind = routine.Kind,
            OwnerType = routine.OwnerType,
            MeType = routine.MeType,
            Parameters = parameters,
            ReturnType = returnType,
            IsFailable = routine.IsFailable,
            DeclaredMutation = routine.DeclaredMutation,
            MutationCategory = routine.MutationCategory,
            GenericParameters = genericParameters,
            GenericConstraints = genericConstraints,
            Visibility = routine.Visibility,
            Location = routine.Location,
            Documentation = routine.Documentation,
            Module = routine.Module,
            ModulePath = routine.ModulePath,
            Annotations = routine.Annotations,
            CallingConvention = routine.CallingConvention,
            IsVariadic = routine.IsVariadic,
            IsDangerous = routine.IsDangerous,
            Storage = routine.Storage,
            AsyncStatus = routine.AsyncStatus,
            FailableVariant = routine.FailableVariant
        };

        // Replace base name entry
        _routines[key: baseName] = updatedRoutine;

        // Register with resolved RegistryKey for overload-specific lookup
        string registryKey = updatedRoutine.RegistryKey;
        if (registryKey != baseName)
        {
            _routines[key: registryKey] = updatedRoutine;
        }

        // Update the module-qualified name index
        string qualifiedName = updatedRoutine.QualifiedName;
        if (qualifiedName != baseName)
        {
            _routinesByQualifiedName[key: qualifiedName] = updatedRoutine;
        }

        // Update the routines-by-owner index if this is a memberRoutine
        if (routine.OwnerType != null)
        {
            UpdateRoutineByOwnerIndex(routine: routine, updatedRoutine: updatedRoutine, baseName: baseName);
        }

        // Update the free-function overload entry (now under FreeOwnerKey) — replace old instance by reference
        if (updatedRoutine.OwnerType == null)
        {
            UpdateFreeRoutineOverload(routine: routine, updatedRoutine: updatedRoutine, baseName: baseName);
        }
    }

    /// <summary>Replaces the by-owner overload-list entry for a re-resolved member routine in place.</summary>
    private void UpdateRoutineByOwnerIndex(RoutineInfo routine, RoutineInfo updatedRoutine, string baseName)
    {
        string ownerKey = routine.OwnerType is GenericParameterTypeInfo
            ? GenericOwnerKey
            : routine.OwnerType!.FullName;
        if (_routinesByOwner.TryGetValue(key: ownerKey, value: out Dictionary<string, List<RoutineInfo>>? byName)
            && byName.TryGetValue(key: baseName, value: out List<RoutineInfo>? list))
        {
            int index = list.FindIndex(match: r => r.BaseName == baseName);
            if (index >= 0)
            {
                list[index: index] = updatedRoutine;
            }
        }
    }

    /// <summary>Replaces the FreeOwnerKey overload-list entry (by reference) for a re-resolved free routine.</summary>
    private void UpdateFreeRoutineOverload(RoutineInfo routine, RoutineInfo updatedRoutine, string baseName)
    {
        if (FreeOverloads(baseName: baseName) is not { } overloadList)
            return;
        int idx = overloadList.FindIndex(match: r => ReferenceEquals(r, routine));
        if (idx >= 0)
            overloadList[index: idx] = updatedRoutine;
        else if (!overloadList.Contains(item: updatedRoutine))
            overloadList.Add(item: updatedRoutine);
    }

    /// <summary>
    /// Recursively unifies a specialized-receiver pattern (a memberRoutine's <c>MeType</c>, e.g.
    /// <c>List[Agent[V]]</c>) against the concrete receiver (<c>List[Agent[S64]]</c>), recording
    /// each memberRoutine generic parameter's binding (V → S64) into <paramref name="into"/>. Used so a
    /// member declared on a specialized generic instantiation resolves to a fully concrete memberRoutine.
    /// </summary>
    private static void UnifyReceiverGenerics(TypeInfo pattern, TypeInfo concrete,
        List<string>? genericParams, Dictionary<string, TypeInfo> into)
    {
        if (genericParams is not { Count: > 0 }) return;
        if (pattern is GenericParameterTypeInfo gp)
        {
            if (genericParams.Contains(item: gp.Name) && !into.ContainsKey(key: gp.Name) &&
                concrete is not GenericParameterTypeInfo)
            {
                into[key: gp.Name] = concrete;
            }
            return;
        }
        if (pattern.TypeArguments is { Count: > 0 } pArgs &&
            concrete.TypeArguments is { Count: > 0 } cArgs)
        {
            for (int i = 0; i < pArgs.Count && i < cArgs.Count; i++)
            {
                UnifyReceiverGenerics(pattern: pArgs[index: i], concrete: cArgs[index: i],
                    genericParams: genericParams, into: into);
            }
        }
    }

    /// <summary>
    /// True when two routines carry the same set of <c>needs Me …</c> gate constraints (compared by
    /// kind + constraint-type names, order-insensitive). Differently-gated same-signature protocol
    /// defaults are DISTINCT overloads and must not dedup each other in the owner memberRoutine list.
    /// </summary>
    /// <param name="a">First routine to compare.</param>
    /// <param name="b">Second routine to compare.</param>
    private static bool SameMeConstraintSet(RoutineInfo a, RoutineInfo b)
    {
        static List<string> MeGates(RoutineInfo r) =>
            (r.GenericConstraints ?? [])
            .Where(predicate: c => c.ParameterName == "Me")
            .Select(selector: c =>
                $"{c.ConstraintType}:{string.Join(separator: ",", values: (c.ConstraintTypes ?? []).Select(selector: t => t.Name))}")
            .OrderBy(keySelector: s => s, comparer: StringComparer.Ordinal)
            .ToList();

        List<string> ga = MeGates(r: a);
        List<string> gb = MeGates(r: b);
        return ga.Count == gb.Count && ga.SequenceEqual(second: gb);
    }

    /// <summary>
    /// Picks the most-specific of several same-name candidate routines for a concrete implementer:
    /// among those whose <c>needs Me …</c> constraints the implementer satisfies, the one with the
    /// MOST such constraints wins (an unconstrained body is the least-specific fallback). Ties and
    /// the no-satisfied-candidate case fall back to the first candidate.
    /// </summary>
    private RoutineInfo? SelectMostSpecificForImplementer(List<RoutineInfo> candidates,
        TypeInfo implementer)
    {
        RoutineInfo? best = null;
        int bestScore = -1;
        foreach (RoutineInfo candidate in candidates)
        {
            List<GenericConstraintDeclaration> meConstraints =
                candidate.GenericConstraints?
                    .Where(predicate: c => c.ParameterName == "Me")
                    .ToList() ?? [];
            if (!meConstraints.All(predicate: c =>
                    ImplementerSatisfiesConstraint(implementer: implementer, constraint: c)))
            {
                continue; // some Me-constraint is unmet — this body doesn't apply
            }

            if (meConstraints.Count > bestScore)
            {
                bestScore = meConstraints.Count;
                best = candidate;
            }
        }

        return best ?? candidates.FirstOrDefault();
    }

    /// <summary>
    /// True when a concrete implementer type satisfies a single <c>needs Me …</c> constraint used to
    /// gate a protocol-default body (the kind constraints <c>is variant/choice/flags/record/entity</c>
    /// plus <c>obeys P</c>).
    /// </summary>
    /// <summary>
    /// A marker protocol (<c>Accessing[T]</c>/<c>Controlling[T]</c>) is a read/control REFERENCE bound.
    /// It is satisfied by any non-entity type: a value/record is read/controlled by identity (its own
    /// value — <c>access()</c>/<c>control()</c> return <c>me</c>), and a concrete access token
    /// (<c>Viewing</c>/<c>Modifying</c>/…, a record) both self-satisfies here and obeys structurally via
    /// its parent chain. An ENTITY does NOT self-satisfy — it must be wrapped in a token to be borrowed,
    /// so a bare entity is rejected (its call site wraps it via <c>.view()</c>/<c>.modify()</c>). This
    /// gate is permissive by design (like the other kind gates); the concrete type-argument consistency
    /// is enforced by generic inference + body type-checking.
    /// </summary>
    private static bool SatisfiesMarkerProtocolReflexively(TypeInfo implementer, string protocolName)
    {
        if (!RuntimeContract.IsMarkerProtocol(baseName: protocolName))
            return false;
        return implementer is not EntityTypeInfo and not ProtocolTypeInfo;
    }

    private bool ImplementerSatisfiesConstraint(TypeInfo implementer,
        GenericConstraintDeclaration constraint)
    {
        switch (constraint.ConstraintType)
        {
            case ConstraintKind.VariantType:
                return implementer is VariantTypeInfo;
            case ConstraintKind.ChoiceType:
                return implementer is ChoiceTypeInfo;
            case ConstraintKind.FlagsType:
                return implementer is FlagsTypeInfo;
            case ConstraintKind.TupleType:
                return implementer is TupleTypeInfo;
            case ConstraintKind.RoutineType:
                return implementer is RoutineTypeInfo;
            case ConstraintKind.Crashable:
                return implementer is CrashableTypeInfo;
            case ConstraintKind.RedirectType:
                // A field-less aggregate: an empty record, or a scalar kind (choice/flags carry no
                // member variables). Its `allmemvarof` is empty, so the base field-walk is degenerate.
                return implementer switch
                {
                    RecordTypeInfo r => r.MemberVariables.Count == 0,
                    EntityTypeInfo e => e.MemberVariables.Count == 0,
                    _ => false
                };
            case ConstraintKind.EntityType:
                // `is EntityType` — an entity. A crashable IS an entity subtype (heap-allocated), so it
                // satisfies this directly: it reuses the entity derives (notably `destroy` = field-walk +
                // `hijack().invalidate()`) rather than needing a duplicate CrashableType template. Its
                // crashable-specific members (represent/diagnose/crash_message) still come from
                // HandleCrashable via DispatchByOwnerType, which routes by owner type before any template.
                return implementer is EntityTypeInfo;
            case ConstraintKind.RecordType:
                // `is RecordType` — a plain value record; exclude the sum/enum/tuple record
                // subtypes, which have their own more-specific kind gates.
                return implementer is RecordTypeInfo;
            case ConstraintKind.Obeys:
                // TypeObeysProtocol folds in the reflexive marker-protocol rule, so no separate check here.
                return constraint.ConstraintTypes?.All(predicate: p =>
                    TypeObeysProtocol(type: implementer, protocolName: p.Name)) ?? true;
            case ConstraintKind.AnyType:
                // `is TypeName` — a bare type-parameter declaration; satisfied by every type.
                return true;
            default:
                // Unknown/unsupported gate — treat as satisfied so it never wrongly excludes.
                return true;
        }
    }

    /// <summary>
    /// Registers an auto-derive template captured directly from a stdlib <c>@overridable/@override
    /// routine T.MemberRoutine()</c> declaration: the owner type-parameter name (<c>T</c>), the memberRoutine's
    /// kind gate constraints (<c>needs T is VariantType/…</c>), and its AST body. Several
    /// same-signature templates coexist (distinguished by their gate set) because selection is
    /// per-type at SYNTHESIS time — this store never goes through the signature-keyed registry.
    /// </summary>
    /// <summary>The default-impl member routine named <paramref name="memberRoutineName"/> declared on a bare
    /// generic-param owner (`routine T.m()`), or null. Found under the canonical GenericOwnerKey in
    /// _routinesByOwner — the by-name resolution path that replaced the old separate _universalMemberRoutines index.
    /// First overload wins (matching the prior first-registration-wins TryAdd).</summary>
    private RoutineInfo? DefaultMemberRoutine(string memberRoutineName)
        => _routinesByOwner.TryGetValue(key: GenericOwnerKey,
               value: out Dictionary<string, List<RoutineInfo>>? byName)
           && byName.TryGetValue(key: memberRoutineName, value: out List<RoutineInfo>? list)
           && list.Count > 0
            ? list[index: 0]
            : null;

    /// <summary>The free-function (owner-less) overload list for <paramref name="baseName"/>, or null —
    /// stored under the canonical FreeOwnerKey in _routinesByOwner (replaced the old _routineOverloads).</summary>
    private List<RoutineInfo>? FreeOverloads(string baseName)
        => _routinesByOwner.TryGetValue(key: FreeOwnerKey, value: out Dictionary<string, List<RoutineInfo>>? byName)
           && byName.TryGetValue(key: baseName, value: out List<RoutineInfo>? list)
            ? list
            : null;

    /// <summary>Generic-definition free functions with the bare name <paramref name="name"/> — filtered off
    /// the FreeOwnerKey store (which is keyed by BaseName = Module.Name), replacing the old separate
    /// _genericFreeFunctions by-Name index. Matches the old semantics (all modules' same-named generics).</summary>
    private List<RoutineInfo> GenericFreeFunctions(string name)
        => _routinesByOwner.TryGetValue(key: FreeOwnerKey, value: out Dictionary<string, List<RoutineInfo>>? byName)
            ? OwnerMemberRoutines(byName: byName).Where(predicate: r => r.Name == name && r.IsGenericDefinition).ToList()
            : [];

    /// <summary>
    /// All non-variadic generic free-routine overloads of <paramref name="name"/> whose parameter
    /// count equals <paramref name="arity"/>. Multiple generic overloads may share a name and arity,
    /// distinguished ONLY by parameter type (e.g. a `Guarded[T, P]` context vs a `Roamed[T]` context);
    /// overload resolution tries each candidate's type-argument inference to pick the one that unifies.
    /// </summary>
    public List<RoutineInfo> GenericOverloadsByArity(string name, int arity)
        => GenericFreeFunctions(name: name)
            .Where(predicate: r => !r.IsVariadic && r.Parameters.Count == arity)
            .ToList();

    public void RegisterDeriveTemplate(string memberRoutine, string ownerParam, int arity,
        List<GenericConstraintDeclaration>? constraints, Statement body)
    {
        if (!_deriveTemplates.TryGetValue(key: memberRoutine,
                value: out List<(string, int, List<GenericConstraintDeclaration>, Statement)>? list))
        {
            list = [];
            _deriveTemplates[key: memberRoutine] = list;
        }

        List<GenericConstraintDeclaration> gates = DeriveKindGates(constraints: constraints);
        string gateKey = DeriveGateKey(gates: gates);
        // Dedup by (arity, gate set): several same-name templates coexist — kind-gated overrides
        // (different gates) and, for `hash`, the fast `hash()` vs keyed `hash(k0, k1)` forms
        // (different arity, same gates).
        if (list.Any(predicate: e => e.Item2 == arity && DeriveGateKey(gates: e.Item3) == gateKey))
            return; // already captured (re-run across passes)
        list.Add(item: (ownerParam, arity, gates, body));
    }

    /// <summary>
    /// Selects the most-specific auto-derive template for <paramref name="forType"/>: among the
    /// candidates whose kind gates (<c>needs T is VariantType/…</c>) the type satisfies, the one
    /// with the MOST gates wins; the unconstrained base is the fallback. Returns the owner
    /// type-parameter name (for the T→type substitution) and the template body.
    /// </summary>
    /// <summary>True when a universal auto-derive template (<c>@overridable routine T.&lt;name&gt;()</c>) is
    /// registered for <paramref name="name"/> — i.e. a body exists for the everywhere-derive registration to
    /// fill. Guards generic stub registration so a member with no universal body is never stubbed.</summary>
    public bool HasDeriveTemplate(string name) => _deriveTemplates.ContainsKey(key: name);

    public (string OwnerParam, Statement Body)? GetDeriveTemplate(string name, int arity,
        TypeInfo forType)
    {
        if (!_deriveTemplates.TryGetValue(key: name,
                value: out List<(string, int, List<GenericConstraintDeclaration>, Statement)>? list))
            return null;

        (string, Statement)? best = null;
        int bestScore = -1;
        foreach ((string ownerParam, int tArity, List<GenericConstraintDeclaration> gates,
                     Statement body) in list)
        {
            if (tArity != arity)
                continue;
            if (!gates.All(predicate: g =>
                    ImplementerSatisfiesConstraint(implementer: forType, constraint: g)))
                continue;
            if (gates.Count > bestScore)
            {
                bestScore = gates.Count;
                best = (ownerParam, body);
            }
        }

        return best;
    }

    /// <summary>The kind gate constraints (<c>is VariantType/choice/flags/…</c>) that drive
    /// per-type derive selection. Obeys/other constraints are ignored for gating.</summary>
    private static List<GenericConstraintDeclaration> DeriveKindGates(
        List<GenericConstraintDeclaration>? constraints)
        => (constraints ?? [])
            .Where(predicate: c => c.ConstraintType is ConstraintKind.VariantType
                or ConstraintKind.ChoiceType or ConstraintKind.FlagsType
                or ConstraintKind.TupleType or ConstraintKind.RecordType
                or ConstraintKind.EntityType or ConstraintKind.RoutineType
                or ConstraintKind.Crashable or ConstraintKind.RedirectType)
            .ToList();

    private static string DeriveGateKey(List<GenericConstraintDeclaration> gates)
        => string.Join(separator: "&",
            values: gates
                .Select(selector: c => c.ConstraintType.ToString())
                .OrderBy(keySelector: s => s, comparer: StringComparer.Ordinal));

    /// <summary>Looks up a memberRoutine on a type, returning a fully-resolved <see cref="RoutineInfo"/> with type parameters substituted for generic owners and protocol memberRoutines.</summary>
    /// <param name="type">The type to search.</param>
    /// <param name="memberRoutineName">The memberRoutine name to look up.</param>
    /// <param name="isFailable">Filter by failability; null = accept either.</param>
    /// <param name="forImplementer">Concrete implementer for protocol memberRoutine substitution.</param>
    public RoutineInfo? LookupMemberRoutine(TypeInfo type, string memberRoutineName, bool? isFailable = null,
        TypeInfo? forImplementer = null)
    {
        // Transparent-protocol unwrap: Accessing[X] / Controlling[X] are markers that
        // dispatch every memberRoutine to X. If the receiver is one of these wrappers with a
        // single type argument, recurse on the inner type. Without this, for-loops over
        // `Accessing[Iterable[T]]` parameters can't resolve iter at SA time, leading
        // to "no resolved member routine" warnings during generic monomorphization.
        if (type is ProtocolTypeInfo { TypeArguments: { Count: 1 } markerArgs } markerProto)
        {
            string markerBase = (markerProto.GenericDefinition ?? markerProto).BareName;
            if (RuntimeContract.IsMarkerProtocol(baseName: markerBase))
            {
                RoutineInfo? viaInner = LookupMemberRoutine(type: markerArgs[index: 0],
                    memberRoutineName: memberRoutineName, isFailable: isFailable);
                if (viaInner != null) return viaInner;
            }
        }

        // First check the type's own memberRoutines (O(1) by name via the nested store)
        if (LookupOwnMemberRoutine(type: type, memberRoutineName: memberRoutineName,
                isFailable: isFailable, forImplementer: forImplementer) is { } ownMatch)
        {
            return ownMatch;
        }

        // On-demand failable-variant synthesis, placed RIGHT AFTER the own-routine miss and BEFORE the
        // protocol / generic-resolution / wrapper fallbacks below — several of those `return` a (possibly
        // null) result and would short-circuit past a miss handler at the tail. A try_/check_/lookup_
        // variant is a concrete routine registered ON its owner, so once the synthesizer registers it the
        // own-lookup (this call re-entered, or the generic-def recursion) resolves it. The re-entry guard
        // stops the synthesizer's own base + result lookups from recursing into this hook.
        if (_onDemandVariantSynthesizer != null && !_inVariantSynthesis
            && HasFailableVariantPrefix(name: memberRoutineName))
        {
            _inVariantSynthesis = true;
            try
            {
                RoutineInfo? synthesized = _onDemandVariantSynthesizer(arg1: type, arg2: memberRoutineName);
                if (synthesized != null && (isFailable == null || synthesized.IsFailable == isFailable))
                    return synthesized;
            }
            finally
            {
                _inVariantSynthesis = false;
            }
        }

        // For protocol types, check the protocol's memberRoutine signatures
        if (type is ProtocolTypeInfo proto)
        {
            ProtocolMemberRoutineInfo? protoMemberRoutine =
                proto.MemberRoutines.FirstOrDefault(predicate: m =>
                    m.Name == memberRoutineName &&
                    (isFailable == null || m.IsFailable == isFailable));
            if (protoMemberRoutine != null)
            {
                return SynthesizeProtocolMemberRoutine(proto: proto,
                    protoMemberRoutine: protoMemberRoutine,
                    ownerType: type);
            }
        }

        // For resolved generics, check the generic definition's memberRoutines
        if (type.IsGenericResolution)
        {
            TypeInfo? genericDef = type switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                // Wrapper types: memberRoutines are registered on the corresponding RecordTypeInfo
                // (e.g. _routinesByOwner["Core.Hijacked"] holds extract, offset, etc.).
                // Always look up the RecordTypeInfo by base name, regardless of whether
                // InnerType is a generic parameter — Hijacked[T] and Hijacked[Character]
                // both need to route through the generic definition's memberRoutine table.
                WrapperTypeInfo wt => LookupType(name: wt.Name),
                _ => null
            };

            if (genericDef != null)
            {
                RoutineInfo? genericMemberRoutine =
                    LookupMemberRoutine(type: genericDef, memberRoutineName: memberRoutineName, isFailable: isFailable);
                // Skip the generic-def → concrete substitution path when the inner lookup
                // resolved via the universal-memberRoutine fallback (e.g. `T.hijack()`). In that
                // case `genericMemberRoutine` already has its universal T baked to the generic-def
                // (e.g. `Hijacked[Retained-genericdef]`), and a second
                // SubstituteMemberRoutineForOwner with the concrete `type` only substitutes the
                // OUTER record's generic params (Retained's T → Counter) — it can't reach
                // the inner T binding any more. Fall through to the universal path below
                // so `T` binds directly to the concrete `type` (e.g. Retained[Counter])
                // and produces `Hijacked[Retained[Counter]]`.
                if (genericMemberRoutine != null &&
                    genericMemberRoutine.GenericDefinition?.OwnerType is not GenericParameterTypeInfo)
                {
                    return SubstituteMemberRoutineForOwner(memberRoutine: genericMemberRoutine, resolvedOwner: type);
                }
            }
        }

        // Fallback: a default-impl member routine on a bare generic-param owner (`routine T.m()`), found by
        // name under the canonical GenericOwnerKey in the same _routinesByOwner store and substituted onto the
        // concrete receiver (derive template / access coercion / hijack / `@innate` fold-intrinsic — the
        // AST/synthesis + fold layers decide what happens next). No separate "universal member routines" index.
        if (DefaultMemberRoutine(memberRoutineName: memberRoutineName) is { } defaultMember)
        {
            return SubstituteMemberRoutineForOwner(memberRoutine: defaultMember, resolvedOwner: type);
        }

        // Generic parameter receivers route through caller-supplied constraints — see
        // LookupMemberRoutineViaConstraints below. The plain LookupMemberRoutine path has no routine
        // context to discover Obeys constraints, so it cannot resolve them here.

        // Check implemented protocols for default implementations
        List<TypeInfo>? protocols = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };
        if (protocols != null)
        {
            return LookupMemberRoutineViaImplementedProtocols(type: type, protocols: protocols,
                memberRoutineName: memberRoutineName, forImplementer: forImplementer, isFailable: isFailable);
        }

        // WrapperTypeInfo (Viewing/Modifying/Consulting/Amending/Guarded/Witnessed)
        // is the parallel representation to the substituted RecordTypeInfo of the same wrapper.
        // The RecordTypeInfo path finds memberRoutines via its substituted `Controlling[InnerT]` /
        // `Accessing[InnerT]` protocol entry. WrapperTypeInfo carries no ImplementedProtocols,
        // so the protocols loop above is skipped — without this fallback, the call dispatcher
        // would then synthesize a forwarder whose body is never emitted (LINKERR). Resolve
        // directly to InnerType as a last resort. Hijacked is intentionally excluded — its
        // members must be reached via explicit extract()/as_entity().
        //
        // Retained/Tracked are also excluded: they are `@llvm("ptr")` to a `RetainController[T]`,
        // NOT to T directly. Falling through here would dispatch an inner-T memberRoutine with `me` =
        // controller pointer, reading controller's strong+weak counts as if they were T's first
        // fields. The forwarder-synthesis path emits the correct double-indirection body
        // (Hijacked[RetainController[T]](me).as_entity().raw_data().as_entity().MemberRoutine(...)).
        if (type is WrapperTypeInfo { Name: RuntimeContract.Viewing
                or RuntimeContract.Modifying or RuntimeContract.Consulting or RuntimeContract.Amending or RuntimeContract.Guarded or RuntimeContract.Witnessed
            } forwardingWrapper)
        {
            return LookupMemberRoutine(type: forwardingWrapper.InnerType,
                memberRoutineName: memberRoutineName, isFailable: isFailable);
        }

        return null;
    }

    /// <summary>
    /// Verifier-installed hook that synthesizes a failable variant (<c>try_</c>/<c>check_</c>/
    /// <c>lookup_</c>) from its base failable routine the first time it is looked up — the on-demand
    /// replacement for eager pre-registration of every failable routine's variants. Signature is
    /// <c>(receiverType, variantName) → variant RoutineInfo?</c>.
    /// </summary>
    public Func<TypeInfo, string, RoutineInfo?>? OnDemandVariantSynthesizer
    {
        get => _onDemandVariantSynthesizer;
        set => _onDemandVariantSynthesizer = value;
    }

    private Func<TypeInfo, string, RoutineInfo?>? _onDemandVariantSynthesizer;

    /// <summary>
    /// Verifier-installed hook that synthesizes the variant of a SPECIFIC base overload (a
    /// <see cref="RoutineInfo"/>, not a name) — used by the variant-body rewriter, which holds the exact
    /// failable routine being rewritten and must get THAT overload's variant (a name-only lookup can't
    /// disambiguate <c>S64.create(from_text:)</c> from <c>S64.create(from_int:)</c>). Signature is
    /// <c>(baseOverload, "try"|"check"|"lookup") → variant RoutineInfo?</c>.
    /// </summary>
    public Func<RoutineInfo, string, RoutineInfo?>? OnDemandVariantForBase { get; set; }

    /// <summary>Re-entry guard: true while <see cref="_onDemandVariantSynthesizer"/> is running, so its
    /// own lookups (the base routine, then the freshly-registered variant) don't re-trigger the hook.</summary>
    private bool _inVariantSynthesis;

    private static bool HasFailableVariantPrefix(string name)
        => name.StartsWith(value: "try_", comparisonType: StringComparison.Ordinal)
           || name.StartsWith(value: "check_", comparisonType: StringComparison.Ordinal)
           || name.StartsWith(value: "lookup_", comparisonType: StringComparison.Ordinal);

    /// <summary>
    /// Resolves a memberRoutine through <paramref name="type"/>'s implemented protocols' default
    /// implementations (threading the concrete implementer for within-dispatch). Skips the loop for
    /// Retained/Tracked records so the call dispatcher falls through to wrapper-forwarder synthesis
    /// (their pointer addresses a controller struct, not T directly). Always returns a definite result
    /// (the found routine or null) — mirroring the original terminal `return null`.
    /// </summary>
    private RoutineInfo? LookupMemberRoutineViaImplementedProtocols(TypeInfo type,
        List<TypeInfo> protocols, string memberRoutineName, TypeInfo? forImplementer, bool? isFailable)
    {
        // Retained/Tracked obey `Controlling[T]`. The recursive LookupMemberRoutine call on a
        // `Controlling[X]` protocol triggers the marker-protocol unwrap at the top of this
        // memberRoutine, dispatching the lookup transparently to X's memberRoutine. That is correct for
        // protocol-as-type parameter receivers (where the call site already holds an X-shaped
        // pointer), but WRONG for RC wrappers — their pointer addresses a `RetainController[T]`
        // struct, NOT T directly. Letting the unwrap proceed here returns the inner T memberRoutine
        // (e.g. `ListNode.chain_text`), which the call dispatcher then invokes with the
        // controller pointer as `me`, reading strong+weak counts as if they were T's first
        // fields. Skip the protocols loop for Retained/Tracked records so the call dispatcher
        // falls through to the wrapper-forwarder synthesis path, which emits the correct
        // double-indirection body.
        string recBaseName = type switch
        {
            RecordTypeInfo r2 => (r2.GenericDefinition ?? r2).BareName,
            _ => type.BareName
        };
        bool skipProtocols = recBaseName is RuntimeContract.Retained or RuntimeContract.Tracked;
        if (!skipProtocols)
        {
            foreach (var protocol in protocols)
            {
                // Thread the concrete implementer so a protocol with several `needs`-gated
                // default bodies dispatches to the kind-matched one (within-dispatch).
                // Thread isFailable: a protocol default of the same name+failability must NOT shadow the
                // implementer's OWN method of the OTHER failability. E.g. resolving `a.first()` first probes
                // isFailable=False; `Array[T, N].first!()` (failable) is correctly excluded upstream expecting
                // a failable retry — but if this protocol path drops the filter it returns the failable
                // `Iterable[T].first` default anyway, shadowing the own method (whose iterating body leaks an
                // abstract Emittable[T] into codegen). Respecting isFailable makes the probe miss here and the
                // failable retry find the own method.
                var res = LookupMemberRoutine(type: protocol, memberRoutineName: memberRoutineName,
                    forImplementer: forImplementer ?? type, isFailable: isFailable);
                if (res != null) return res;
            }
        }
        return null;
    }

    /// <summary>
    /// Looks up a memberRoutine directly on <paramref name="type"/>'s own overload table (O(1) by name).
    /// Applies within-dispatch (most-specific for a concrete implementer when &gt;1 candidate) and
    /// normalizes a generic-def owner to the concrete owner. Returns null when the type has no own table
    /// entry or no name/failability match (caller falls through to the other resolution paths).
    /// </summary>
    private RoutineInfo? LookupOwnMemberRoutine(TypeInfo type, string memberRoutineName,
        bool? isFailable, TypeInfo? forImplementer)
    {
        if (!_routinesByOwner.TryGetValue(key: RealmRegistryKey(type: type), value: out Dictionary<string, List<RoutineInfo>>? ownByName)
            || !ownByName.TryGetValue(key: memberRoutineName, value: out List<RoutineInfo>? memberRoutines))
        {
            return null;
        }

        List<RoutineInfo> nameMatches = memberRoutines.Where(predicate: m =>
            isFailable == null || m.IsFailable == isFailable).ToList();

        // A routine's identity is (declaration-name, parameter-types). NAME ALONE cannot pin a
        // unique overload once >1 same-name routine is registered — so a name-only lookup here must
        // NOT silently take the first-registered one (that is the S8-vs-S64 mis-pick bug class: the
        // numeric overloads register S8, S16, …, S64, so first-wins always returns the S8 form). With
        // >1 candidate the ONLY legitimate name-only disambiguation is protocol `within`-dispatch:
        // several `needs`-gated default bodies (`needs Me is VariantType` / `is ChoiceType` / `obeys X`)
        // resolved FOR a concrete implementer — pick the MOST-SPECIFIC whose Me-constraints it satisfies.
        // Otherwise the lookup is genuinely ambiguous → return null so the caller re-resolves through the
        // argType-aware overload matcher (LookupMemberRoutineOverload) instead of mis-binding. The
        // single-candidate path (0 or 1 match) is unchanged.
        RoutineInfo? memberRoutine;
        if (nameMatches.Count > 1)
        {
            memberRoutine = forImplementer != null
                ? SelectMostSpecificForImplementer(candidates: nameMatches, implementer: forImplementer)
                : null;
        }
        else
        {
            memberRoutine = nameMatches.FirstOrDefault();
        }
        if (memberRoutine != null)
        {
            bool shouldNormalizeConcreteOwner =
                (type.IsGenericResolution || type is WrapperTypeInfo { TypeArguments: { Count: > 0 } }) &&
                (memberRoutine.OwnerType is { IsGenericDefinition: true } ||
                 memberRoutine.IsGenericDefinition);
            if (shouldNormalizeConcreteOwner)
            {
                return SubstituteMemberRoutineForOwner(memberRoutine: memberRoutine, resolvedOwner: type);
            }

            return memberRoutine;
        }

        return null;
    }

    /// <summary>
    /// Resolves a memberRoutine on a generic-parameter receiver by walking <c>Obeys</c> constraints
    /// supplied by the caller (typically the current routine + its owner type). Each constraint
    /// protocol is queried via <see cref="LookupMemberRoutine"/>, which synthesizes a <see cref="RoutineInfo"/>
    /// from the matching <see cref="ProtocolMemberRoutineInfo"/>. Returns the first hit, or null.
    /// </summary>
    public RoutineInfo? LookupMemberRoutineViaConstraints(GenericParameterTypeInfo param,
        string memberRoutineName, bool? isFailable,
        IEnumerable<GenericConstraintDeclaration> constraints,
        Func<string, TypeInfo?>? protocolResolver = null)
    {
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (c.ParameterName != param.Name ||
                c.ConstraintType != ConstraintKind.Obeys ||
                c.ConstraintTypes == null)
                continue;
            foreach (TypeExpression protocolExpr in c.ConstraintTypes)
            {
                // Resolve the constraint's protocol IMPORT-aware (a user protocol like `Greetable`
                // lives in the referring module, not Core) — the bare registry lookup only resolved it
                // via the cross-module short-name scan. Fall back to the bare lookup when no resolver.
                TypeInfo? proto = protocolResolver?.Invoke(protocolExpr.Name)
                                  ?? LookupType(name: protocolExpr.Name);
                if (proto is not ProtocolTypeInfo protoInfo)
                    continue;
                // Synthesize directly with the generic parameter as ownerType so that
                // Me-self-type slots in the protocol signature substitute to `param`
                // (e.g. `T`), not to the protocol itself. Going through LookupMemberRoutine
                // would bind Me to the protocol type, yielding signatures like
                // `combine(you: Combinable) -> Combinable` instead of `-> T`.
                ProtocolMemberRoutineInfo? protoMemberRoutine =
                    protoInfo.MemberRoutines.FirstOrDefault(predicate: m =>
                        m.Name == memberRoutineName &&
                        (isFailable == null || m.IsFailable == isFailable));
                if (protoMemberRoutine != null)
                    return SynthesizeProtocolMemberRoutine(proto: protoInfo,
                        protoMemberRoutine: protoMemberRoutine,
                        ownerType: param);

                // Extension memberRoutines (default implementations) declared as
                // `routine Iterable[T].List()` are registered against the protocol's owner
                // table, NOT in `protoInfo.MemberRoutines` (which holds only the abstract signatures).
                // Resolve them through the protocol's generic definition so a generic-parameter
                // receiver (`S obeys Iterable[T]`) can call them. The returned routine keeps the
                // protocol's element params and `Me` in its signature; the caller's member-call
                // substitution block binds them (the obeys constraint maps `Iterable[T]`'s element
                // → the receiver's element, and `Me` → the receiver `param`).
                RoutineInfo? extensionMemberRoutine =
                    LookupMemberRoutine(type: protoInfo, memberRoutineName: memberRoutineName, isFailable: isFailable);
                if (extensionMemberRoutine is { OwnerType: not GenericParameterTypeInfo })
                    return extensionMemberRoutine;
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up a memberRoutine overload on a type using the argument types for disambiguation.
    /// This is used for operator/member dispatch where multiple wired overloads may exist
    /// on the same owner type (for example Moment.sub(Duration) and Moment.sub(Moment)).
    /// </summary>
    public RoutineInfo? LookupMemberRoutineOverload(TypeInfo type, string memberRoutineName,
        List<TypeInfo> argTypes)
    {
        // Transparent-protocol unwrap: Accessing[X] / Controlling[X] forward every memberRoutine
        // to X. Mirror the unwrap in LookupMemberRoutine so overload-driven resolution (e.g. the
        // CallOverloadResolutionPass walking f-string-lowered represent calls on a
        // `Accessing[Text]` receiver) lands on Text's memberRoutine instead of synthesizing a
        // protocol-dispatch stub on Accessing that has no implementers registered.
        if (type is ProtocolTypeInfo { TypeArguments: { Count: 1 } markerArgs } markerProto)
        {
            string markerBase = (markerProto.GenericDefinition ?? markerProto).BareName;
            if (RuntimeContract.IsMarkerProtocol(baseName: markerBase))
            {
                RoutineInfo? viaInner = LookupMemberRoutineOverload(type: markerArgs[index: 0],
                    memberRoutineName: memberRoutineName, argTypes: argTypes);
                if (viaInner != null) return viaInner;
            }
        }

        var candidates = new List<RoutineInfo>();
        CollectMemberRoutineCandidates(type: type, memberRoutineName: memberRoutineName, candidates: candidates);

        // Protocol abstract memberRoutines are never a valid dispatch target on a concrete receiver —
        // RF protocols are abstract-only (no default impls). Including them would let lookup
        // pick `Equatable.eq(Self)` for `S128 == S64`, masking the integer-promotion fallback
        // and emitting an unresolved `Core.Equatable.eq` symbol at link time.
        if (type is not ProtocolTypeInfo)
        {
            candidates.RemoveAll(match: c => c.OwnerType is ProtocolTypeInfo);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return MatchMemberOverloadByArgTypes(candidates: candidates, receiverType: type, argTypes: argTypes);
    }

    /// <summary>
    /// THE single member-overload matcher: resolves the UNIQUE routine among <paramref name="candidates"/>
    /// whose parameters match <paramref name="argTypes"/> (a routine's identity is its name + parameter
    /// types, so name + argTypes name at most one). Two tiers, exact before assignable:
    /// <list type="number">
    ///   <item>EXACT type-name match — unique by construction (no two routines share an owner + signature),
    ///     so the first exact hit is the answer.</item>
    ///   <item>ASSIGNABLE match — collected across ALL candidates; a lone survivor wins, but TWO OR MORE
    ///     assignable candidates are genuinely ambiguous → return null (the caller reports an
    ///     ArgumentTypeMismatch). No first-registered fallback: name + argTypes must pin exactly one, else
    ///     it is an error, never an arbitrary pick.</item>
    /// </list>
    /// A <see cref="ProtocolSelfTypeInfo"/> parameter binds to the concrete <paramref name="receiverType"/>;
    /// a universal (bare generic-param owner) winner is re-homed onto it via
    /// <see cref="SubstituteMemberRoutineForOwner"/>.
    /// </summary>
    private RoutineInfo? MatchMemberOverloadByArgTypes(List<RoutineInfo> candidates,
        TypeInfo receiverType, List<TypeInfo> argTypes)
    {
        RoutineInfo Home(RoutineInfo winner) =>
            winner.OwnerType is GenericParameterTypeInfo
                ? SubstituteMemberRoutineForOwner(memberRoutine: winner, resolvedOwner: receiverType) ?? winner
                : winner;

        bool ParamsMatch(RoutineInfo candidate, Func<TypeInfo, TypeInfo, bool> match)
        {
            if (candidate.Parameters.Count != argTypes.Count) return false;
            for (int i = 0; i < argTypes.Count; i++)
            {
                TypeInfo paramType = candidate.Parameters[index: i].Type;
                if (paramType is ProtocolSelfTypeInfo) paramType = receiverType;
                if (!match(argTypes[index: i], paramType)) return false;
            }
            return true;
        }

        // Tier 1 — exact type-name match (unique by declaration).
        foreach (RoutineInfo candidate in candidates)
            if (ParamsMatch(candidate: candidate, match: (arg, param) => param.Name == arg.Name))
                return Home(winner: candidate);

        // Tier 2 — assignable match; UNIQUE or null (no first-wins on ambiguity).
        RoutineInfo? assignable = null;
        foreach (RoutineInfo candidate in candidates)
        {
            if (!ParamsMatch(candidate: candidate,
                    match: (arg, param) => IsMemberRoutineArgumentAssignable(source: arg, target: param)))
                continue;
            if (assignable != null && !ReferenceEquals(objA: assignable, objB: candidate))
                return null; // ≥2 assignable overloads — genuinely ambiguous.
            assignable = candidate;
        }
        return assignable != null ? Home(winner: assignable) : null;
    }

    /// <summary>
    /// Synthesizes a complete RoutineInfo from a ProtocolMemberRoutineInfo, including parameters,
    /// modification category, storage, and all other metadata. Substitutes generic type
    /// parameters for instantiated generic protocols (e.g., Iterator[S64]: T -> S64).
    /// </summary>
    private RoutineInfo SynthesizeProtocolMemberRoutine(ProtocolTypeInfo proto,
        ProtocolMemberRoutineInfo protoMemberRoutine, TypeInfo ownerType) // NOSONAR S3776
    {
        // Build substitution map for generic protocols (e.g., Iterator[S64]: T -> S64)
        Dictionary<string, TypeInfo>? substitution = null;
        if (proto.TypeArguments is { Count: > 0 })
        {
            ProtocolTypeInfo genericDef = proto.GenericDefinition ?? proto;
            if (genericDef.GenericParameters is { Count: > 0 })
            {
                substitution = new Dictionary<string, TypeInfo>();
                for (int i = 0;
                     i < genericDef.GenericParameters.Count && i < proto.TypeArguments.Count;
                     i++)
                {
                    substitution[key: genericDef.GenericParameters[index: i]] =
                        proto.TypeArguments[index: i];
                }
            }
        }

        // Resolve return type with substitution
        TypeInfo? resolvedReturn = protoMemberRoutine.ReturnType;
        if (resolvedReturn != null && substitution != null)
        {
            resolvedReturn =
                SubstituteTypeInProtocol(type: resolvedReturn, substitution: substitution);
        }

        // ProtocolSelf (Me) in protocol memberRoutine signatures means the concrete implementing type.
        if (resolvedReturn is ProtocolSelfTypeInfo)
            resolvedReturn = ownerType;

        // Convert ProtocolMemberRoutineInfo.ParameterTypes -> ParameterInfo list
        var parameters = new List<ParameterInfo>();
        for (int i = 0; i < protoMemberRoutine.ParameterTypes.Count; i++)
        {
            TypeInfo paramType = protoMemberRoutine.ParameterTypes[index: i];
            if (substitution != null)
            {
                paramType = SubstituteTypeInProtocol(type: paramType, substitution: substitution);
            }

            if (paramType is ProtocolSelfTypeInfo)
                paramType = ownerType;

            string paramName = i < protoMemberRoutine.ParameterNames.Count
                ? protoMemberRoutine.ParameterNames[index: i]
                : $"arg{i}";
            parameters.Add(
                item: new ParameterInfo(name: paramName, type: paramType) { Index = i });
        }

        return new RoutineInfo(name: protoMemberRoutine.Name)
        {
            OwnerType = ownerType,
            Parameters = parameters,
            ReturnType = resolvedReturn,
            IsFailable = protoMemberRoutine.IsFailable,
            MutationCategory = protoMemberRoutine.Mutation,
            Storage = protoMemberRoutine.IsInstanceMemberRoutine
                ? StorageClass.None
                : StorageClass.Common,
            AsyncStatus = AsyncStatus.None,
            IsSynthesized = true,
            Location = protoMemberRoutine.Location
        };
    }

    /// <summary>
    /// Substitutes a universal (bare generic-param owner, <c>routine T.m()</c>) memberRoutine onto the
    /// concrete <paramref name="resolvedOwner"/>: binds <c>T</c>→owner in params + return, drops <c>T</c>
    /// from the memberRoutine generics, keeps memberRoutine-own + owner <c>in [...]</c> constraints, and caches.
    /// </summary>
    private RoutineInfo? SubstituteUniversalOwnerMemberRoutine(RoutineInfo memberRoutine,
        TypeInfo resolvedOwner, GenericParameterTypeInfo universalOwner)
    {
        var substitution = new Dictionary<string, TypeInfo>
        {
            [key: universalOwner.Name] = resolvedOwner
        };

        var substitutedParams = memberRoutine.Parameters
                                      .Select(selector: p =>
                                           RoutineInfo.SubstituteParameterType(param: p,
                                               substitution: substitution))
                                      .ToList();
        TypeInfo? substitutedReturn = memberRoutine.ReturnType != null
            ? RoutineInfo.SubstituteType(type: memberRoutine.ReturnType, substitution: substitution)
            : null;
        List<string>? memberRoutineOnlyGenericParams = memberRoutine.GenericParameters?
            .Where(gp => gp != universalOwner.Name)
            .ToList();
        if (memberRoutineOnlyGenericParams?.Count == 0)
            memberRoutineOnlyGenericParams = null;

        // Keep constraints on the memberRoutine's own generic params, PLUS `in [...]` (TypeEquality)
        // constraints on the OWNER's params (e.g. `Guarded[T, P].amend() needs P in [...]`). The
        // owner param is already substituted on the resolved instance, but the constraint is not
        // validated here — it is preserved so the call-site verifier can check it against the
        // receiver's bound argument (otherwise a memberRoutine constraint on an inherited param vanishes
        // unchecked).
        List<GenericConstraintDeclaration>? memberRoutineOnlyConstraints = memberRoutine.GenericConstraints?
            .Where(c => memberRoutineOnlyGenericParams?.Contains(c.ParameterName) == true
                || c.ConstraintType == ConstraintKind.TypeEquality)
            .ToList();
        if (memberRoutineOnlyConstraints?.Count == 0)
            memberRoutineOnlyConstraints = null;

        var resolvedUniversalMemberRoutine = new RoutineInfo(name: memberRoutine.Name)
        {
            Kind = memberRoutine.Kind,
            OwnerType = resolvedOwner,
            Parameters = substitutedParams,
            ReturnType = substitutedReturn,
            IsFailable = memberRoutine.IsFailable,
            DeclaredMutation = memberRoutine.DeclaredMutation,
            MutationCategory = memberRoutine.MutationCategory,
            GenericParameters = memberRoutineOnlyGenericParams,
            GenericConstraints = memberRoutineOnlyConstraints,
            Visibility = memberRoutine.Visibility,
            Location = memberRoutine.Location,
            Documentation = memberRoutine.Documentation,
            Module = memberRoutine.Module,
            ModulePath = memberRoutine.ModulePath,
            Annotations = memberRoutine.Annotations,
            CallingConvention = memberRoutine.CallingConvention,
            IsVariadic = memberRoutine.IsVariadic,
            IsDangerous = memberRoutine.IsDangerous,
            IsSynthesized = memberRoutine.IsSynthesized,
            TypeArguments = memberRoutine.TypeArguments,
            GenericDefinition = memberRoutine.GenericDefinition ?? memberRoutine,
            WrapperForwarderInnerMemberRoutine = memberRoutine.WrapperForwarderInnerMemberRoutine,
            WrapperForwarderInnerGenericDef = memberRoutine.WrapperForwarderInnerGenericDef,
            Storage = memberRoutine.Storage,
            AsyncStatus = memberRoutine.AsyncStatus,
            FailableVariant = memberRoutine.FailableVariant,
            OriginalName = memberRoutine.OriginalName
        };

        return CacheResolvedOwnerMemberRoutine(resolvedMemberRoutine: resolvedUniversalMemberRoutine);
    }

    /// <summary>
    /// Re-resolves a synthesized wrapper-forwarder memberRoutine against the concrete inner type's real
    /// memberRoutine (avoiding the inner-T/wrapper-T name collision that naive substitution causes). Returns
    /// null when the concrete inner type does not have the forwarded memberRoutine (do not fabricate it).
    /// </summary>
    private RoutineInfo? SubstituteWrapperForwarderMemberRoutine(RoutineInfo memberRoutine,
        TypeInfo resolvedOwner, RoutineInfo innerGenMemberRoutine)
    {
        TypeInfo concreteInner = resolvedOwner.TypeArguments![index: 0];
        RoutineInfo? concreteInnerMemberRoutine = LookupMemberRoutine(
            type: concreteInner,
            memberRoutineName: innerGenMemberRoutine.Name,
            isFailable: innerGenMemberRoutine.IsFailable);
        if (concreteInnerMemberRoutine != null)
        {
            var fwdParams = concreteInnerMemberRoutine.Parameters
                .Select(p => p.Name == "me"
                    ? p.WithSubstitutedType(newType: resolvedOwner)
                    : p)
                .ToList();
            var resolvedWrapperForwarder = new RoutineInfo(name: memberRoutine.Name)
            {
                Kind = memberRoutine.Kind,
                OwnerType = resolvedOwner,
                Parameters = fwdParams,
                ReturnType = concreteInnerMemberRoutine.ReturnType,
                IsFailable = memberRoutine.IsFailable,
                DeclaredMutation = memberRoutine.DeclaredMutation,
                MutationCategory = memberRoutine.MutationCategory,
                Visibility = memberRoutine.Visibility,
                Location = memberRoutine.Location,
            Documentation = memberRoutine.Documentation,
                Module = memberRoutine.Module,
                ModulePath = memberRoutine.ModulePath,
                Annotations = memberRoutine.Annotations,
                CallingConvention = memberRoutine.CallingConvention,
                IsVariadic = memberRoutine.IsVariadic,
                IsDangerous = memberRoutine.IsDangerous,
                IsSynthesized = true,
                TypeArguments = memberRoutine.TypeArguments,
                GenericDefinition = memberRoutine.GenericDefinition ?? memberRoutine,
                WrapperForwarderInnerMemberRoutine = concreteInnerMemberRoutine,
                WrapperForwarderInnerGenericDef = memberRoutine.WrapperForwarderInnerGenericDef,
                Storage = memberRoutine.Storage,
                AsyncStatus = memberRoutine.AsyncStatus,
                FailableVariant = memberRoutine.FailableVariant,
                OriginalName = memberRoutine.OriginalName,
                // Propagate memberRoutine-level generic parameters from the concrete inner memberRoutine so
                // OperatorLoweringPass can monomorphize (e.g. Text.getitem![I] -> [U64]).
                GenericParameters = concreteInnerMemberRoutine.GenericParameters ?? memberRoutine.GenericParameters,
                GenericConstraints = concreteInnerMemberRoutine.GenericConstraints ?? memberRoutine.GenericConstraints,
            };
            return CacheResolvedOwnerMemberRoutine(resolvedMemberRoutine: resolvedWrapperForwarder);
        }
        // The concrete inner type does not have this forwarded memberRoutine — do not fabricate it.
        return null;
    }

    /// <summary>
    /// Substitutes the owner type's generic type parameters into a memberRoutine's signature.
    /// For example, List[S32].add(item: T) -> List[S32].add(item: S32).
    /// </summary>
    internal RoutineInfo? SubstituteMemberRoutineForOwner(RoutineInfo memberRoutine, TypeInfo resolvedOwner)
    {
        if (memberRoutine.OwnerType is GenericParameterTypeInfo universalOwner)
        {
            return SubstituteUniversalOwnerMemberRoutine(memberRoutine: memberRoutine,
                resolvedOwner: resolvedOwner, universalOwner: universalOwner);
        }

        // Build substitution map from the resolved owner's generic definition
        TypeInfo? genericDef = resolvedOwner switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            // Wrapper types (Hijacked[T], Hijacked[Byte], etc.) — look up generic def by base name
            WrapperTypeInfo => LookupType(name: resolvedOwner.Name),
            _ => null
        };

        if (genericDef?.GenericParameters == null || resolvedOwner.TypeArguments == null)
        {
            return memberRoutine;
        }

        var substitution2 = new Dictionary<string, TypeInfo>();
        for (int i = 0;
             i < genericDef.GenericParameters.Count && i < resolvedOwner.TypeArguments.Count;
             i++)
        {
            substitution2[key: genericDef.GenericParameters[index: i]] =
                resolvedOwner.TypeArguments[index: i];
        }

        // Specialized-receiver member (e.g. `routine List[Agent[V]].gather!()`): the memberRoutine's
        // generic param V is determined by the receiver PATTERN (MeType = List[Agent[V]]), not by a
        // call-site `[..]`. Unify MeType against the concrete owner (List[Agent[S64]]) to bind V=S64
        // and fold it into the owner substitution. This makes the resolved memberRoutine FULLY CONCRETE
        // (V dropped from memberRoutine generics, no `[S64]` mangle suffix), so SA, reachability, GMP and
        // codegen all key on the same name — `Core.List[Agent[S64]].gather`.
        if (memberRoutine.MeType is { } mePattern)
        {
            UnifyReceiverGenerics(pattern: mePattern, concrete: resolvedOwner,
                genericParams: memberRoutine.GenericParameters, into: substitution2);
        }

        if (substitution2.Count == 0)
        {
            return memberRoutine;
        }

        // Wrapper-forwarder: re-resolve signature against the concrete inner memberRoutine instead of
        // naive name substitution (inner-T vs wrapper-T collision: both T and List[T] use T,
        // so {T: List[Character]} would map List[T].getitem!'s T to List[Character], not Character).
        // Note: wrapper types like T may be RecordTypeInfo (declared as `record` in RF),
        // not WrapperTypeInfo, so check TypeArguments.Count rather than the runtime type.
        if (memberRoutine is { IsSynthesized: true, WrapperForwarderInnerMemberRoutine: { } innerGenMemberRoutine } &&
            resolvedOwner.TypeArguments is { Count: 1 } && resolvedOwner is not GenericParameterTypeInfo)
        {
            return SubstituteWrapperForwarderMemberRoutine(memberRoutine: memberRoutine,
                resolvedOwner: resolvedOwner, innerGenMemberRoutine: innerGenMemberRoutine);
        }

        // Substitute types in parameters
        var substitutedParams2 = memberRoutine.Parameters
                                      .Select(selector: p =>
                                           RoutineInfo.SubstituteParameterType(param: p,
                                               substitution: substitution2))
                                      .ToList();

        // Substitute return type
        // Special case: if return type IS the owner's generic def (e.g. Maybe.store returns Maybe_def),
        // the concrete return type is resolvedOwner itself (Maybe[ListNode[S64]], not Maybe_def).
        TypeInfo? substitutedReturn2;
        if (memberRoutine.ReturnType != null && genericDef != null &&
            (ReferenceEquals(objA: memberRoutine.ReturnType, objB: genericDef) ||
             memberRoutine.ReturnType.Name == genericDef.Name && memberRoutine.ReturnType.IsGenericDefinition))
        {
            substitutedReturn2 = resolvedOwner;
        }
        else
        {
            substitutedReturn2 = memberRoutine.ReturnType != null
                ? RoutineInfo.SubstituteType(type: memberRoutine.ReturnType, substitution: substitution2)
                : null;
        }

        // If return type is still a generic definition (e.g., track() -> Tracked_def when
        // Tracked[T] was declared), instantiate it using the substitution map.
        if (substitutedReturn2 is { IsGenericDefinition: true, GenericParameters: { } retGenericParams } retDef)
        {
            var retArgs = retGenericParams
                .Select(selector: p => substitution2.TryGetValue(p, out TypeInfo? subType) ?
                    subType : null)
                .ToList();
            if (retArgs.All(predicate: a => a != null))
                substitutedReturn2 = retDef.CreateInstance(typeArguments: retArgs.Select(selector: a => a!).ToList());
        }

        // Only keep memberRoutine-level generic parameters (owner params are now resolved)
        List<string>? memberRoutineOnlyGenericParams2 = memberRoutine.GenericParameters?
            .Where(gp => !substitution2.ContainsKey(gp))
            .ToList();
        if (memberRoutineOnlyGenericParams2?.Count == 0)
            memberRoutineOnlyGenericParams2 = null;

        // Keep memberRoutine-level constraints PLUS owner-param `in [...]` (TypeEquality) constraints, so a
        // memberRoutine constraint on an inherited param (e.g. `Guarded[T, P].amend() needs P in [...]`)
        // survives to be validated at the call site against the receiver's bound argument.
        List<GenericConstraintDeclaration>? memberRoutineOnlyConstraints2 = memberRoutine
            .GenericConstraints?
            .Where(c => memberRoutineOnlyGenericParams2?.Contains(c.ParameterName) == true
                || c.ConstraintType == ConstraintKind.TypeEquality)
            .ToList();
        if (memberRoutineOnlyConstraints2?.Count == 0)
            memberRoutineOnlyConstraints2 = null;

        var resolvedOwnerMemberRoutine = new RoutineInfo(name: memberRoutine.Name)
        {
            Kind = memberRoutine.Kind,
            OwnerType = resolvedOwner,
            // Carry the specialized-receiver pattern (e.g. List[Agent[V]]) unchanged: V is a memberRoutine
            // generic param, not an owner param, so owner substitution leaves it intact. Receiver-
            // based memberRoutine-generic inference at the call site needs this pattern to bind V. (The
            // Suflae entity `me`=Roamed[E] handle is substituted downstream in
            // GenericMonomorphizationPass, so it does not need owner substitution here — and doing it
            // here mistyped some stdlib Hijacked `me` receivers, tripping RF-S627.)
            MeType = memberRoutine.MeType,
            Parameters = substitutedParams2,
            ReturnType = substitutedReturn2,
            IsFailable = memberRoutine.IsFailable,
            DeclaredMutation = memberRoutine.DeclaredMutation,
            MutationCategory = memberRoutine.MutationCategory,
            GenericParameters = memberRoutineOnlyGenericParams2,
            GenericConstraints = memberRoutineOnlyConstraints2,
            Visibility = memberRoutine.Visibility,
            Location = memberRoutine.Location,
            Documentation = memberRoutine.Documentation,
            Module = memberRoutine.Module,
            ModulePath = memberRoutine.ModulePath,
            Annotations = memberRoutine.Annotations,
            CallingConvention = memberRoutine.CallingConvention,
            IsVariadic = memberRoutine.IsVariadic,
            IsDangerous = memberRoutine.IsDangerous,
            IsSynthesized = memberRoutine.IsSynthesized,
            TypeArguments = memberRoutine.TypeArguments,
            GenericDefinition = memberRoutine.GenericDefinition ?? memberRoutine,
            WrapperForwarderInnerMemberRoutine = memberRoutine.WrapperForwarderInnerMemberRoutine,
            WrapperForwarderInnerGenericDef = memberRoutine.WrapperForwarderInnerGenericDef,
            Storage = memberRoutine.Storage,
            AsyncStatus = memberRoutine.AsyncStatus,
            FailableVariant = memberRoutine.FailableVariant,
            OriginalName = memberRoutine.OriginalName
        };
        return CacheResolvedOwnerMemberRoutine(resolvedMemberRoutine: resolvedOwnerMemberRoutine);
    }

    /// <summary>
    /// Public entry to register a fully-resolved RoutineInfo into the resolutions cache,
    /// keyed by <see cref="RoutineInfo.RegistryKey"/>. Returns the cached instance if one
    /// already exists for that key; otherwise inserts and returns <paramref name="resolvedMemberRoutine"/>.
    /// Used by reachability/instantiation when it constructs concrete routine clones (e.g.
    /// substituting memberRoutine-level TypeArguments after owner monomorphization) that need to be
    /// visible to <c>GenericMonomorphizationPass</c> via <see cref="GetAllRoutineResolutions"/>.
    /// </summary>
    public RoutineInfo RegisterRoutineResolution(RoutineInfo resolvedMemberRoutine)
        => CacheResolvedOwnerMemberRoutine(resolvedMemberRoutine: resolvedMemberRoutine);

    /// <summary>
    /// Removes a routine resolution entry by its (current) registry key. Used when a
    /// resolution's parameter types have been mutated in-place (e.g.
    /// MarkerProtocolDesugarPass rewriting Accessing[T] → T) so the resolution needs to
    /// be re-inserted under its new <see cref="RoutineInfo.RegistryKey"/>.
    /// </summary>
    public bool UnregisterRoutineResolution(string oldKey)
        => _routineResolutions.Remove(key: oldKey);

    private RoutineInfo CacheResolvedOwnerMemberRoutine(RoutineInfo resolvedMemberRoutine)
    {
        // A universal memberRoutine substituted onto a generic-def owner (e.g. `Node.retain()`) produces
        // the same RegistryKey as one substituted onto a concrete instantiation (`Node[T_param].retain()`)
        // because GetTypeIdentity collapses both to "Module.Name[Param]". Caching the first form
        // would then return wrongly-substituted return types (Retained[Node] instead of
        // Retained[Node[T_param]]) for subsequent lookups on the resolution. Only honor the cache
        // when the owner type is referentially the same.
        if (_routineResolutions.TryGetValue(key: resolvedMemberRoutine.RegistryKey,
                value: out RoutineInfo? cached)
            && ReferenceEquals(objA: cached.OwnerType, objB: resolvedMemberRoutine.OwnerType))
        {
            return cached;
        }

        _routineResolutions[key: resolvedMemberRoutine.RegistryKey] = resolvedMemberRoutine;
        return resolvedMemberRoutine;
    }

    /// <summary>
    /// EXISTENCE check: true when <paramref name="type"/> hosts at least one CONCRETE (non-protocol-owner)
    /// overload named <paramref name="memberRoutineName"/>. Unlike <see cref="LookupMemberRoutine"/> this
    /// never needs a unique winner, so it stays correct for an overloaded member (e.g. a container's
    /// <c>getitem(index:)</c> + <c>getitem(range:)</c>) where a name-only unique lookup returns null.
    /// </summary>
    internal bool HasConcreteMemberOverload(TypeInfo type, string memberRoutineName)
    {
        var candidates = new List<RoutineInfo>();
        CollectMemberRoutineCandidates(type: type, memberRoutineName: memberRoutineName, candidates: candidates);
        return candidates.Any(predicate: c => c.OwnerType is not ProtocolTypeInfo);
    }

    internal void CollectMemberRoutineCandidates(TypeInfo type, string memberRoutineName, List<RoutineInfo> candidates)
    {
        if (_routinesByOwner.TryGetValue(key: RealmRegistryKey(type: type), value: out Dictionary<string, List<RoutineInfo>>? byName)
            && byName.TryGetValue(key: memberRoutineName, value: out List<RoutineInfo>? memberRoutines))
        {
            candidates.AddRange(memberRoutines);
        }

        if (type is ProtocolTypeInfo proto)
        {
            foreach (ProtocolMemberRoutineInfo protoMemberRoutine in proto.MemberRoutines.Where(predicate: m => m.Name == memberRoutineName))
            {
                candidates.Add(item: SynthesizeProtocolMemberRoutine(proto: proto,
                    protoMemberRoutine: protoMemberRoutine,
                    ownerType: type));
            }
        }

        if (type.IsGenericResolution)
        {
            TypeInfo? genericDef = type switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                ProtocolTypeInfo p => p.GenericDefinition,
                WrapperTypeInfo wt => LookupType(name: wt.Name),
                _ => null
            };

            if (genericDef != null)
            {
                var genericCandidates = new List<RoutineInfo>();
                CollectMemberRoutineCandidates(type: genericDef, memberRoutineName: memberRoutineName, candidates: genericCandidates);
                foreach (RoutineInfo genericCandidate in genericCandidates)
                {
                    if (genericCandidate.OwnerType is GenericParameterTypeInfo)
                    {
                        candidates.Add(item: genericCandidate);
                    }
                    else
                    {
                        RoutineInfo? substituted = SubstituteMemberRoutineForOwner(memberRoutine: genericCandidate,
                            resolvedOwner: type);
                        if (substituted != null)
                            candidates.Add(item: substituted);
                    }
                }
            }
        }

        if (DefaultMemberRoutine(memberRoutineName: memberRoutineName) is { } defaultMember)
        {
            candidates.Add(item: SubstituteMemberRoutineForOwner(memberRoutine: defaultMember,
                resolvedOwner: type)!);
        }

        List<TypeInfo>? protocols = type switch
        {
            RecordTypeInfo r => r.ImplementedProtocols,
            EntityTypeInfo e => e.ImplementedProtocols,
            _ => null
        };

        if (protocols != null)
        {
            foreach (TypeInfo protocol in protocols)
            {
                CollectMemberRoutineCandidates(type: protocol, memberRoutineName: memberRoutineName, candidates: candidates);
            }
        }
    }

    private static bool IsMemberRoutineArgumentAssignable(TypeInfo source, TypeInfo target)
    {
        // Compare by Name (includes generic args, e.g. "List[S64]") rather than FullName
        // because arg types constructed during SA may lack a module prefix while registry
        // types are always module-qualified — FullName comparison would break that pairing.
        if (source.Name == target.Name)
        {
            return true;
        }

        if (target is ProtocolTypeInfo targetProto)
        {
            // For generic-protocol targets (e.g. Accessing[Bytes]), require the type-argument
            // to match the source. Without this check, ANY type matches ANY generic protocol —
            // CStr.create(Accessing[Bytes]) "accepts" a Text arg, beating
            // CStr.create(Accessing[Text]) by source order and producing garbled output
            // when SA emits a call to the wrong overload.
            if (targetProto.TypeArguments is { Count: 1 } pTypeArgs)
            {
                return pTypeArgs[0].Name == source.Name
                    || pTypeArgs[0].FullName == source.FullName;
            }
            return true;
        }

        if (target.Name == "Me")
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Gets all registered routines, excluding pruned generic stubs.
    /// </summary>
    /// <returns>An enumerable of all registered routines.</returns>
    public IEnumerable<RoutineInfo> GetAllRoutines(bool requireLive = true)
    {
        // `_routines` indexes each first-overload routine under BOTH its RegistryKey and its
        // (owner-qualified) BaseName, so `.Values` yields that object twice. Dedup by reference so
        // every consumer (codegen, synthesis passes, the registered-count) sees each routine once
        // instead of ~2x — the inflated count that made a trivial program look like it had 12k
        // routines when it actually has ~6.6k.
        IEnumerable<RoutineInfo> all = _prunedGenericBases.Count == 0
            ? _routines.Values.Distinct()
            : _routines.Values.Distinct().Where(r => !_prunedGenericBases.Contains(r.BaseName));
        // Exclude:
        // - @innate routines: compile-time-only stubs (type_name, module_name, etc.) that
        //   BuilderQueryInliningPass folds to literals; they have no body and must never reach codegen.
        // - Routines on generic-definition owner types: bodies are synthesised per concrete instance;
        //   emitting them for the definition produces [T]/[K,V] placeholders in LLVM.
        // - Routines on None owners: None -> LLVM void, illegal as a parameter type.
        // - Routines on non-live concrete generic owner types: phantom instantiations.
        // requireLive=false (base build): keep every concrete non-generic-def routine regardless of liveness,
        // so the resident base materializes the FULL stdlib generic closure ahead of time (not just what an
        // entry program reaches). Normal builds pass requireLive=true → byte-identical.
        return all.Where(r =>
                      !r.Annotations.Contains(value: "innate") &&
                      (r.OwnerType == null ||
                       (!r.OwnerType.IsNone &&
                        !r.OwnerType.IsGenericDefinition &&
                        (r.OwnerType.TypeArguments == null ||
                         r.OwnerType.TypeArguments.All(a => !a.IsNone)) &&
                        (!requireLive || IsConcreteTypeLive(r.OwnerType)))));
    }

    /// <summary>
    /// Gets all concrete generic routine resolutions created from generic routine definitions.
    /// </summary>
    public IEnumerable<RoutineInfo> GetAllRoutineResolutions()
    {
        return _routineResolutions.Values;
    }

    /// <summary>
    /// Removes generic-definition routines that were never instantiated for any concrete type.
    /// Called at the end of Phase 6 global desugaring, after all variant and wired bodies have
    /// been generated. Routines whose <c>BaseName</c> has no concrete entry in either
    /// <c>_routines</c> or <c>_routineResolutions</c> are marked as pruned and excluded from
    /// subsequent <see cref="GetAllRoutines"/> calls (codegen, AST printer, etc.).
    /// </summary>
    public void PruneUnusedGenericRoutines()
    {
        // Collect base names that have at least one concrete (non-generic) instance.
        var concreteBases = new HashSet<string>(capacity: _routines.Count + _routineResolutions.Count);
        foreach (RoutineInfo r in _routines.Values)
        {
            if (!r.IsGenericDefinition)
                concreteBases.Add(r.BaseName);
        }
        foreach (RoutineInfo r in _routineResolutions.Values)
        {
            concreteBases.Add(r.BaseName);
        }

        // Mark every generic definition whose base has no concrete instance.
        foreach (RoutineInfo r in _routines.Values)
        {
            if (r.IsGenericDefinition && !concreteBases.Contains(r.BaseName))
                _prunedGenericBases.Add(r.BaseName);
        }

        // Also prune routines with <error> in parameter or return types.
        // These arise from implicit-generic routines (e.g. `routine max!(values...: T) needs T obeys P`)
        // where the generic parameter T was never added to GenericParameters, causing type resolution
        // to fall back to ErrorTypeInfo. Such routines can never be called with valid types.
        foreach (RoutineInfo r in _routines.Values)
        {
            if (r.Parameters.Any(p => p.Type.Name.Contains(value: "<error>"))
                || (r.ReturnType?.Name.Contains(value: "<error>") ?? false))
            {
                _prunedGenericBases.Add(r.BaseName);
            }
        }
    }

    /// <summary>
    /// Returns true if the routine with the given base name was pruned as an unused generic.
    /// Used by the desugaring pipeline to also evict matching entries from the variant-body dictionary.
    /// </summary>
    public bool IsRoutinePruned(string baseName) => _prunedGenericBases.Contains(baseName);

    /// <summary>
    /// Gets the memberRoutines registered DIRECTLY on a type's own table (raw). Returns empty for a generic
    /// resolution like <c>List[S64]</c> whose concrete owner is never written into
    /// <c>_routinesByOwner</c>. Callers that need the resolved own-memberRoutine set of a generic resolution
    /// (e.g. unified teardown/copy lifecycle resolution) must use
    /// <see cref="GetOwnMemberRoutinesResolved"/> instead.
    /// </summary>
    /// <param name="type">The type to get memberRoutines for.</param>
    /// <returns>An enumerable of all memberRoutines for the type.</returns>
    public IEnumerable<RoutineInfo> GetMemberRoutinesForType(TypeInfo type)
    {
        return _routinesByOwner.TryGetValue(key: RealmRegistryKey(type: type), value: out Dictionary<string, List<RoutineInfo>>? byName)
            ? OwnerMemberRoutines(byName: byName)
            : [];
    }

    private readonly Dictionary<string, List<RoutineInfo>> _memberRoutinesForTypeCache =
        new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// The unified own-memberRoutine resolver: returns the memberRoutines a type provides ITSELF, including — for a
    /// generic resolution whose concrete owner is absent from <c>_routinesByOwner</c> — the generic
    /// definition's own memberRoutines substituted for this owner (via <see cref="SubstituteMemberRoutineForOwner"/>).
    /// This is the single source of truth the find-side and the lifecycle (teardown/copy) passes share,
    /// so <c>GetMemberRoutinesForType</c> (raw) and <see cref="LookupMemberRoutine"/> can no longer disagree about
    /// whether e.g. <c>Retained[Tracer]</c> has a <c>destroy</c>.
    ///
    /// <para>Plain OWN-memberRoutine enumeration only — no protocol-memberRoutine synthesis, no universal-memberRoutine
    /// stub, no marker/wrapper unwrap (those are dispatch concerns). That keeps it from surfacing the
    /// no-owner universal <c>T.destroy</c> stub for a borrowed referent. Results are cached per
    /// <c>FullName</c>; only fully-concrete resolutions are admitted to the cache.</para>
    /// </summary>
    public IEnumerable<RoutineInfo> GetOwnMemberRoutinesResolved(TypeInfo type)
    {
        if (_routinesByOwner.TryGetValue(key: RealmRegistryKey(type: type), value: out Dictionary<string, List<RoutineInfo>>? ownByName))
            return OwnerMemberRoutines(byName: ownByName);

        if (!type.IsGenericResolution ||
            type.TypeArguments is null ||
            type.TypeArguments.Any(predicate: a => a is GenericParameterTypeInfo or ErrorTypeInfo || a.IsNone))
            return [];

        // Cache key is realm-QUALIFIED: RF and SF resolutions share a realm-free FullName
        // (`Core.List[Core.U64]`), so a FullName key would cross-contaminate — the first-computed realm's
        // member set (e.g. SF List's `inner`-forwarders) would be served for the other realm's instance.
        if (_memberRoutinesForTypeCache.TryGetValue(key: type.RealmQualifiedName, value: out List<RoutineInfo>? cached))
            return cached;

        var result = new List<RoutineInfo>();
        TypeInfo? genericDef = type switch
        {
            RecordTypeInfo r => r.GenericDefinition,
            EntityTypeInfo e => e.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            WrapperTypeInfo wt => LookupType(name: wt.Name),
            _ => null
        };
        if (genericDef != null && !ReferenceEquals(objA: genericDef, objB: type) &&
            _routinesByOwner.TryGetValue(key: RealmRegistryKey(type: genericDef), value: out Dictionary<string, List<RoutineInfo>>? defByName))
        {
            foreach (RoutineInfo m in OwnerMemberRoutines(byName: defByName))
            {
                // Universal (T-owned) memberRoutines are not the type's OWN memberRoutines — skip them so the
                // no-owner T.destroy stub never leaks in for a borrowed referent.
                if (m.OwnerType is GenericParameterTypeInfo) continue;
                RoutineInfo? sub = SubstituteMemberRoutineForOwner(memberRoutine: m, resolvedOwner: type);
                if (sub != null) result.Add(item: sub);
            }
        }

        _memberRoutinesForTypeCache[key: type.RealmQualifiedName] = result;
        return result;
    }

    /// <summary>The owned-value lifecycle of a type, resolved through the single unified own-memberRoutine
    /// resolver (<see cref="GetOwnMemberRoutinesResolved"/>) so the teardown and copy passes agree about
    /// generic resolutions like <c>Retained[Tracer]</c> / <c>Maybe[Text]</c>.</summary>
    public readonly record struct Lifecycle(RoutineInfo? Store, RoutineInfo? Destroy, bool IsBorrow);

    /// <summary>
    /// Lifecycle and reference are governed by the four wired routines
    /// <c>create</c>/<c>refer</c>/<c>control</c>/<c>destroy</c> — the system is AGNOSTIC to
    /// specific wrapper-type names (no hardcoded Viewing/Modifying/Hijacked list). Teardown simply calls
    /// <c>destroy</c> uniformly: it is a real destructor on owning types and a no-op on the
    /// access/borrow wrappers, so firing it is always safe by construction. The only thing this gate
    /// excludes is the ABSTRACT tier — generic parameters and protocols (the latter also covering the
    /// <c>Accessing</c>/<c>Controlling</c> access markers) — which have no concrete <c>destroy</c> to
    /// resolve. The one remaining hazard, a <c>T</c> reference bound to the bare referent type via the
    /// reference primitives <c>refer</c>/<c>control</c>/<c>as_entity</c>, is excluded at the binding
    /// site by <c>ScopeTeardownLoweringPass.IsViewBinding</c> (keyed on the producing verb, since the
    /// binding's static type is the referent itself, not a borrow wrapper).
    /// </summary>
    private static bool IsBorrowTier(TypeInfo type) =>
        type is GenericParameterTypeInfo or ProtocolTypeInfo;

    /// <summary>
    /// If <paramref name="type"/> is an RC wrapper (Retained/Tracked/Guarded/Witnessed/Roamed) — matched
    /// by its generic base name — returns that base name, else null. Used to redirect the abstract
    /// <c>store</c> hook to the wrapper's concrete refcount copy verb (see
    /// <c>RuntimeContract.RcCopyVerb</c>).
    /// </summary>
    internal static string? GetRcWrapperBaseName(TypeInfo type)
    {
        // Prefer the generic DEFINITION's name (a resolution's own Name may carry a module prefix, e.g.
        // `Core.Roamed[...]`, which would not match the bare `Roamed` allowlist). `BareName` drops the
        // `[typeargs]` suffix, so no manual bracket parsing here.
        string? baseName = type switch
        {
            RecordTypeInfo { GenericDefinition: { } gd } => gd.BareName,
            WrapperTypeInfo wt => wt.BareName,
            RecordTypeInfo r => r.BareName,
            _ => null
        };

        return baseName is not null && RuntimeContract.RcWrapperBaseNames.Contains(item: baseName)
            ? baseName
            : null;
    }

    /// <summary>
    /// Resolves a type's owned-value lifecycle: its retaining <c>store</c> (a hand-written, i.e.
    /// non-synthesized, zero-arg <c>store</c> on a record — the managed-leaf retain hook), its
    /// <c>destroy</c> (preferring the user-written one), and whether it is a borrow-tier type. The
    /// teardown and copy lowering passes both drive off THIS one decision, so a value is either both
    /// retaining-copied and balanced-destroyed, or neither — never the asymmetry that double-freed
    /// before. Resolved via <see cref="GetOwnMemberRoutinesResolved"/>, so it works for generic resolutions.
    /// </summary>
    // True iff a memberRoutine's owner-level `needs <param> obeys <Protocol>` constraints HOLD for the concrete
    // owner's type args. e.g. `Array[T,N].assign() needs T obeys Assignable` — for `Array[Node]` the map
    // T→Node fails (an entity is not Assignable), so the store hook must NOT be handed to the copy-lowering
    // pass: injecting a `needs`-gated memberRoutine whose constraint is unmet produces a body that can't resolve
    // its inner `element.assign()` → the "declared+called but never defined" over-prune crash. Monomorph's
    // ConstraintsSatisfied deliberately trusts SA for `Obeys`, and no SA site rejects `var b = a` on a
    // container of a non-Assignable element, so this is the guard that keeps the injection honest.
    private bool OwnerConstraintsSatisfied(RoutineInfo memberRoutine, TypeInfo ownerType)
    {
        if (memberRoutine.GenericConstraints is not { Count: > 0 } constraints)
            return true;
        List<string>? paramNames =
            (ownerType as RecordTypeInfo)?.GenericDefinition?.GenericParameters
            ?? ownerType.GenericParameters;
        List<TypeInfo>? args = ownerType.TypeArguments;
        if (paramNames is null || args is null)
            return true;
        var subs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
        for (int i = 0; i < paramNames.Count && i < args.Count; i++)
            subs[key: paramNames[i]] = args[i];
        foreach (GenericConstraintDeclaration c in constraints)
        {
            if (subs.TryGetValue(key: c.ParameterName, value: out TypeInfo? actual)
                && !ImplementerSatisfiesConstraint(implementer: actual, constraint: c))
                return false;
        }
        return true;
    }

    /// <summary>Returns the <see cref="Lifecycle"/> (store/destroy hooks) for <paramref name="type"/>, or a borrow-tier sentinel for generic/protocol types.</summary>
    public Lifecycle GetLifecycle(TypeInfo type)
    {
        if (IsBorrowTier(type: type))
            return new Lifecycle(Store: null, Destroy: null, IsBorrow: true);

        List<RoutineInfo> own = GetOwnMemberRoutinesResolved(type: type).ToList();
        RoutineInfo? destroy = own
            .Where(predicate: m => m.Name == "destroy" && m.Parameters.Count == 0)
            .OrderBy(keySelector: m => m.IsSynthesized ? 1 : 0)
            .FirstOrDefault();
        RoutineInfo? store = ResolveStoreHook(type: type, own: own);
        return new Lifecycle(Store: store, Destroy: destroy, IsBorrow: false);
    }

    /// <summary>
    /// The store-site hook the copy-lowering pass injects at each <c>store</c> point to make an aliased
    /// value sound: the RC <c>share</c> verb for a Suflae RC wrapper, the deep <c>duplicate</c> for a
    /// variant with a destructible arm, or the retaining record <c>assign</c> (hand-written, or the
    /// synthesized field-walk when a field itself retains). Null ⇒ the value is not Assignable and no
    /// implicit copy is injected.
    /// </summary>
    private RoutineInfo? ResolveStoreHook(TypeInfo type, List<RoutineInfo> own)
    {
        // Variant MUST be checked before RecordTypeInfo: VariantTypeInfo is a RecordTypeInfo subclass,
        // so `type is RecordTypeInfo` would otherwise capture variants and give them the record
        // field-walk copy — but a variant is a { tag, payload } union whose deep copy needs tag
        // dispatch (BuildVariantCopyBody). Using the record copy on a variant double-frees / corrupts
        // its heap arm (the nested_serialize regression).
        // RC wrappers (Retained/Tracked/Guarded/Witnessed/Roamed) define no literal `store` memberRoutine — their
        // retaining copy IS the refcount verb (retain/track/share/watch/roam). LookupMemberRoutine redirects
        // `store`→that verb, but GetOwnMemberRoutinesResolved (below) never surfaces a `store` for them, so the
        // record branch's name=="assign" filter would miss it → Store=null → no retain injected. A container
        // storing a Roamed element (`List[Roamed[E]].add_last`'s `poke(value)`) then aliases without a
        // refcount bump → the element dangles when the caller's handle releases (the List[entity] UAF).
        // Resolve the copy verb through the redirect so instantiated generic bodies get a real retaining
        // copy — checked BEFORE the RecordTypeInfo branch (RC wrappers ARE records). SUFLAE-ONLY: in SF an
        // `entity` is a `Roamed` and containers hold `Roamed[E]` elements that MUST auto-retain on store; in
        // RazorForge `Roamed`/RC handles are managed MANUALLY (`.roam()`/`.release()` in danger blocks, e.g.
        // roamed_cycle_api), so auto-retain here would double-count and leak. Gate to the SF compile.
        if (Language == TypeModel.Enums.Language.Suflae && GetRcWrapperBaseName(type: type) is not null)
        {
            // RC copy verb is `share` (the refcount-bump co-owner mint) — renamed from the STEP-3 unified
            // `store` so it reads as the explicit-share op and is distinct from value-record `store`.
            return LookupMemberRoutine(type: type, memberRoutineName: RuntimeContract.RefCount.Share);
        }

        if (type is VariantTypeInfo variant && VariantHasDestructibleArm(variant: variant))
        {
            // A variant with a destructible arm (an arm whose own destroy does real work — a heap
            // entity like a collection, a managed leaf like Text, or a record that transitively owns
            // one) would DOUBLE-FREE if bitwise-aliased: two copies of the variant both tear down the
            // same heap arm. Its synthesized deep `copy` (WiredRoutinePass.BuildVariantCopyBody,
            // tag-dispatch → reconstruct each destructible arm with `arm.copy()`) makes an independent
            // value. Return it as the store hook so the copy-lowering pass injects it at every store
            // point (record-ctor field-store, call-arg, assignment) — exactly where a bare alias would
            // otherwise be torn down by both owners.
            return own.FirstOrDefault(predicate: m =>
                m.Name == "duplicate" && m.Parameters.Count == 0);
        }

        if (type is RecordTypeInfo rec)
        {
            // A hand-written store is always a retaining copy (the managed-leaf retain hook,
            // e.g. Text/Decimal bumping a shared controller). Skip it when its owner-level `needs`
            // constraint is unmet for the concrete type — e.g. `Array[SomeEntity]` whose element-loop
            // store `needs T obeys Assignable` (an entity is not Assignable): returning it would inject a
            // store whose body can't resolve → over-prune crash. Store=null ⇒ the value is not Assignable
            // and the implicit copy is (correctly) not injected.
            RoutineInfo? store = own.FirstOrDefault(predicate: m =>
                m.Name == "assign" && m.Parameters.Count == 0 && !m.IsSynthesized
                && OwnerConstraintsSatisfied(memberRoutine: m, ownerType: type));

            // The synthesized record store is field-delegating (WiredRoutinePass.
            // BuildRecordCopyBody) — symmetric with the field-delegating synthesized destroy.
            // Treat it as a retaining copy iff some field itself needs one, so it gets injected
            // at copy sites and balances the per-field destroy at teardown (else: double-free).
            if (store is null && RecordHasRetainingMemberVariable(record: rec))
                store = own.FirstOrDefault(predicate: m =>
                    m.Name == "assign" && m.Parameters.Count == 0);
            return store;
        }

        return null;
    }

    /// <summary>
    /// Whether a variant has at least one arm whose payload owns a real destructor — i.e. an arm type
    /// with a non-borrow <c>destroy</c> (a heap entity/collection, a managed leaf like <c>Text</c>, or
    /// a record that transitively owns one). Such an arm double-frees on bitwise alias, so the variant
    /// needs a synthesized deep <c>copy</c>. None/None/scalar arms are safe to bitwise-copy and are
    /// ignored. Drives the variant branch of <see cref="GetLifecycle"/> and the copy/Copyable synthesis.
    /// </summary>
    public bool VariantHasDestructibleArm(VariantTypeInfo variant)
    {
        if (variant.IsGenericDefinition)
            return false;

        foreach (VariantMemberInfo member in variant.Members)
        {
            if (member.IsNone || member.Type is null)
                continue;

            Lifecycle armLc = GetLifecycle(type: member.Type);
            if (!armLc.IsBorrow && armLc.Destroy is not null)
                return true;
            // An ENTITY arm is a heap reference with a destructor and double-frees on bitwise alias,
            // even when its (generic-instance) destructor isn't materialized yet at this phase — so
            // GetLifecycle reports a null Destroy. Recognize it directly by kind (mirrors the copy
            // body in WiredRoutinePass.BuildVariantCopyBody, which copies every non-borrow arm).
            if (member.Type is EntityTypeInfo)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Whether a record transitively contains a field that needs a retaining copy — i.e. a
    /// field whose type has a hand-written <c>store</c> (a managed leaf such as <c>Text</c> or
    /// <c>Decimal</c>), or a composite record that itself contains one. Drives whether the
    /// synthesized field-delegating <c>store</c> counts as retaining in <see cref="GetLifecycle"/>.
    /// </summary>
    private bool RecordHasRetainingMemberVariable(RecordTypeInfo record,
        HashSet<string>? visited = null)
    {
        if (record.BackendType != null || record.MemberVariables is null)
            return false;
        visited ??= new HashSet<string>();
        if (!visited.Add(item: record.FullName ?? record.Name))
            return false; // recursive-record cycle guard

        foreach (MemberVariableInfo field in record.MemberVariables)
        {
            if (field.Type is not RecordTypeInfo fieldRec)
                continue;
            List<RoutineInfo> fieldOwn = GetOwnMemberRoutinesResolved(type: fieldRec).ToList();
            if (fieldOwn.Any(predicate: m =>
                    m.Name == "assign" && m.Parameters.Count == 0 && !m.IsSynthesized))
                return true;
            if (RecordHasRetainingMemberVariable(record: fieldRec, visited: visited))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Gets or creates a resolved generic routine.
    /// </summary>
    /// <param name="genericDef">The generic routine definition.</param>
    /// <param name="typeArguments">The type arguments for resolution.</param>
    /// <returns>The resolved routine (cached if already created).</returns>
    public RoutineInfo GetOrCreateRoutineResolution(RoutineInfo genericDef,
        List<TypeInfo> typeArguments)
    {
        RoutineInfo resolved = genericDef.CreateInstance(typeArguments: typeArguments);
        string key = resolved.RegistryKey;

        if (_routineResolutions.TryGetValue(key: key, value: out RoutineInfo? existing))
        {
            return existing;
        }

        _routineResolutions[key: key] = resolved;

        return resolved;
    }

    #endregion

    #region Protocol Type Substitution

    /// <summary>
    /// Recursively substitutes generic type parameters in a type.
    /// Handles both direct parameters (T -> S64) and composite types (Iterator[T] -> Iterator[S64]).
    /// </summary>
    private TypeInfo SubstituteTypeInProtocol(TypeInfo type,
        Dictionary<string, TypeInfo> substitution)
    {
        // Direct substitution for generic parameters
        if (type is GenericParameterTypeInfo &&
            substitution.TryGetValue(key: type.Name, value: out TypeInfo? sub))
        {
            return sub;
        }

        // Recursive substitution in type arguments
        if (type.TypeArguments is not { Count: > 0 })
        {
            return type;
        }

        bool anyChanged = false;
        var newArgs = new List<TypeInfo>();
        foreach (TypeInfo arg in type.TypeArguments)
        {
            TypeInfo resolved = SubstituteTypeInProtocol(type: arg, substitution: substitution);
            newArgs.Add(item: resolved);
            if (!ReferenceEquals(objA: resolved, objB: arg))
            {
                anyChanged = true;
            }
        }

        if (!anyChanged)
        {
            return type;
        }

        // Get the generic definition and create a new instance with substituted args
        TypeInfo? genDef = type switch
        {
            EntityTypeInfo e => e.GenericDefinition,
            RecordTypeInfo r => r.GenericDefinition,
            ProtocolTypeInfo p => p.GenericDefinition,
            _ => null
        };

        if (genDef != null)
        {
            return GetOrCreateResolution(genericDef: genDef, typeArguments: newArgs);
        }

        return type;
    }

    /// <summary>
    /// Returns all memberRoutines registered for the given owner type (by FullName key).
    /// Used by SA's eager wrapper-forwarder synthesis to enumerate inner-type memberRoutines.
    /// </summary>
    public List<RoutineInfo> GetMemberRoutinesForOwner(TypeInfo ownerType)
    {
        return _routinesByOwner.TryGetValue(key: RealmRegistryKey(type: ownerType), value: out Dictionary<string, List<RoutineInfo>>? byName)
            ? OwnerMemberRoutines(byName: byName).ToList()
            : [];
    }

    /// <summary>
    /// Enumerates every registered member routine object exactly once. <c>_routinesByOwner</c> holds
    /// the full per-owner memberRoutine lists (including all overloads), which is the comprehensive set the
    /// wired-ness inference pass must iterate. Deduped by reference because the same routine object can
    /// appear under multiple owner keys (e.g. a shell/canonical duplicate of a generic definition).
    /// </summary>
    public IEnumerable<RoutineInfo> EnumerateMemberRoutines()
    {
        var seen = new HashSet<RoutineInfo>(comparer: ReferenceEqualityComparer.Instance);
        foreach ((string ownerKey, Dictionary<string, List<RoutineInfo>> byName) in _routinesByOwner)
        {
            if (ownerKey == FreeOwnerKey) continue; // free functions are not member routines
            foreach (RoutineInfo r in OwnerMemberRoutines(byName: byName))
                if (seen.Add(item: r))
                    yield return r;
        }
    }

    #endregion
}
