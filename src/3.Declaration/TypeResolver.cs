using Builder.Diagnostics;
using Builder.Tokenizer;
using Builder.Verification;
using SyntaxTree;
using TypeModel;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Builder.Declaration;

/// <summary>
/// Handles resolution of type expressions for the semantic analyzer.
/// </summary>
internal sealed class TypeResolver
{
    private const string MaybeTypeName = "Maybe";

    private readonly SemanticVerifier _sa;

    /// <summary>
    /// Tunable limits for associated-type projection (depth / stdlib-only). Read from a config
    /// record rather than hardcoded so the restrictions can be relaxed without touching call sites.
    /// </summary>
    private readonly AssociatedTypeOptions _assocOptions = AssociatedTypeOptions.Default;

    internal TypeResolver(SemanticVerifier sa)
    {
        _sa = sa;
    }

    #region Type Resolution

    /// <summary>
    /// Looks up a type by name, searching imported modules for the current file.
    /// Example: after "import Collections/List", module "Collections" is imported,
    /// so looking up "List" also tries "Collections.List" (module.type).
    /// </summary>
    internal TypeSymbol? LookupTypeWithImports(string name)
    {
        // Already-qualified names (and the resolution cache for generic instances) are handled
        // directly by the registry — no module search needed.
        if (name.Contains(value: '.'))
        {
            return _sa._registry.LookupType(name: name);
        }

        // MODULE-SCOPED resolution (module A's `Point` and module B's `Point` are DISTINCT types):
        //   1. The current module's own declaration SHADOWS everything else.
        //   2. Then imported modules, in import order.
        //   3. Only if both miss, fall back to the registry's context-free lookup (Core auto-import,
        //      resolution cache, and the cross-module short-name scan). The short-name scan returns
        //      an arbitrary first match, so it MUST be last — otherwise a same-named type in an
        //      unrelated module would win over the current module's own type (the old bug: a member
        //      routine `Type.m` in module B resolved its owner to module A's same-named `Type`).
        if (_sa._currentModuleName != null)
        {
            TypeSymbol? own = _sa._registry.LookupType(name: $"{_sa._currentModuleName}.{name}");
            if (own != null)
            {
                return own;
            }
        }

        foreach (string ns in _sa._importedModules)
        {
            TypeSymbol? imported = _sa._registry.LookupType(name: $"{ns}.{name}");
            if (imported != null)
            {
                // `secret record`/`secret entity` are MODULE-PRIVATE: visible only within their own
                // module (step 1 above), INVISIBLE to importers. Skip so an external reference resolves
                // as "unknown type" rather than leaking an internal engine (e.g. Core's UnpackedFloat).
                if (imported.Visibility == VisibilityModifier.Secret)
                {
                    continue;
                }

                return imported;
            }
        }

        // Context-free fallback (Core auto-import, resolution cache, cross-module short-name scan).
        // Hide a module-private `secret` type here too when it belongs to a DIFFERENT module than the
        // referrer — a null current-module context must not accidentally expose another module's secret.
        TypeSymbol? fallback = _sa._registry.LookupType(name: name);
        if (fallback is { Visibility: VisibilityModifier.Secret } &&
            fallback.Module != _sa._currentModuleName)
        {
            return null;
        }

        return fallback;
    }

    /// <summary>
    /// For a BARE type name, returns the IMPORTED modules (import order, secret-excluded) that declare
    /// it — used to flag an ambiguous cross-module reference (module-scoped names). Returns empty when
    /// the name is qualified, or when the current module declares its own type of that name (own-module
    /// SHADOWS, so there is no ambiguity to report). Core's auto-import is not an explicit import and is
    /// intentionally not counted here.
    /// </summary>
    internal List<string> ImportedModulesDeclaring(string name)
    {
        if (name.Contains(value: '.'))
        {
            return [];
        }

        if (_sa._currentModuleName != null &&
            _sa._registry.LookupType(name: $"{_sa._currentModuleName}.{name}") != null)
        {
            return [];
        }

        var declarers = new List<string>();
        foreach (string ns in _sa._importedModules)
        {
            TypeSymbol? imported = _sa._registry.LookupType(name: $"{ns}.{name}");
            if (imported is not null && imported.Visibility != VisibilityModifier.Secret)
            {
                declarers.Add(item: ns);
            }
        }

        return declarers;
    }

    /// <summary>
    /// Looks up a routine by name, searching the Core module and imported modules.
    /// Called after type creator resolution to avoid shadowing type creators
    /// with identically-named convenience functions (e.g., "routine U32(from: U8)").
    /// </summary>
    internal RoutineInfo? LookupRoutineWithImports(string name)
    {
        // Try Core module prefix (Core routines are auto-imported)
        if (!name.Contains(value: '.'))
        {
            RoutineInfo? result = _sa._registry.LookupRoutine(fullName: $"Core.{name}");
            if (result != null)
            {
                return result;
            }
        }

        // Try each imported module
        return _sa._importedModules
                  .Select(selector: ns => _sa._registry.LookupRoutine(fullName: $"{ns}.{name}"))
                  .FirstOrDefault(predicate: result => result != null);
    }

    /// <summary>
    /// Resolves a type expression to a TypeSymbol.
    /// Nullable types (T?) are desugared to Maybe&lt;T&gt; at parse time,
    /// so by the time we see them here, they're already Maybe&lt;T&gt;.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve.</param>
    /// <returns>The resolved type, or an error type if resolution fails.</returns>
    public TypeSymbol ResolveType(TypeExpression? typeExpr)
    {
        if (typeExpr == null)
        {
            return ErrorTypeSymbol.Instance;
        }

        EnforceSuflaeNumberGate(typeExpr: typeExpr);

        TypeSymbol resolved = ResolveTypeCore(typeExpr: typeExpr);
        // Effective realm = the explicit `RF::`/`SF::` qualifier, else the compilation's ambient realm.
        // Name resolution above is realm-blind and yields the ambient-realm type; when an explicit qualifier
        // names the BRIDGED (non-ambient) realm, swap to that realm's equivalent so `RF::Core.List` in a
        // `.sf` reaches the RazorForge-realm list. No-op when the effective realm IS the ambient one (the
        // common case, and every pure-RF/pure-SF bare reference), and null-safe if no bridged type exists.
        string effectiveRealm = typeExpr.Realm ?? _sa._registry.AmbientRealm;
        if (resolved is TypeSymbol ti && effectiveRealm != ti.Realm &&
            _sa._registry.ReResolveInRealm(type: ti, realm: effectiveRealm) is { } bridged)
        {
            resolved = bridged;
        }

        // An `RF::Name` qualifier is an explicit RazorForge/bare-realm reference — it deliberately opts OUT
        // of Suflae's `entity -> Roamed[entity]` lowering (that is the whole point: an SF wrapper entity
        // holds a BARE `RF::Core.List` inside, without re-roaming it into an infinite `Roamed[List]`). Any
        // other realm (null = ambient) gets the normal Suflae lowering.
        if (typeExpr.Realm != "RF")
        {
            resolved = RoamSuflaeEntitySlot(resolved: resolved);
        }

        typeExpr.ResolvedType = resolved;
        return resolved;
    }

    /// <summary>
    /// Suflae's approachable number model: the bare numeric vocabulary is <c>Integer</c>/<c>Decimal</c>
    /// (plus <c>Text</c>/<c>Bytes</c>); the fixed-width / complex / quaternion zoo (<c>S32</c>, <c>U64</c>,
    /// <c>F128</c>, <c>D64</c>, <c>C32</c>, <c>Q64</c>, …) is import-gated behind <c>import Numerics</c>.
    /// This is an SF-mode VISIBILITY rule layered on the shared types (they physically stay in
    /// <c>module Core</c>, RF's auto-prelude — moving them would break every RF program); RF is unaffected.
    /// <para>Fires ONLY on a USER-source <c>.sf</c> TypeExpression (the compiler's own eager resolution of
    /// these types during SF analysis carries stdlib/synthetic locations and is exempt), and never on an
    /// explicit realm qualifier (<c>RF::S32</c> opts in deliberately). Reports and continues — the real type
    /// still resolves, so no cascade; the diagnostic just fails the compile.</para>
    /// </summary>
    private void EnforceSuflaeNumberGate(TypeExpression typeExpr)
    {
        // "Fixed-width unlocked" = the user imported the WHOLE `Numerics` module. The SF prelude injects a
        // SPECIFIC `import Numerics { Integer }` (so bare Integer/literal-default works) and SKIPS that
        // injection when the user already imports Numerics — so a whole-module import is exactly the case
        // where `Numerics` is imported but the prelude's `Integer` symbol was NOT added.
        // The reliable "SF user source" signal is the TypeExpression's own location: a non-stdlib `.sf`
        // file. (The `_registry.CompilationLanguage` flag is NOT yet "Suflae" when a user annotation is
        // resolved — it toggles around stdlib analysis — so it cannot gate this.) A stdlib `.rf`/`.sf`
        // resolution of the same fixed-width type is excluded by IsStdlibFile.
        string file = typeExpr.Location.FileName;
        if (typeExpr.Realm != null || !IsImportGatedNumeric(name: typeExpr.Name) ||
            !file.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase) ||
            _sa.IsStdlibFile(filePath: file))
        {
            return;
        }

        // "Fixed-width unlocked" = the user imported the WHOLE `Numerics` module. The SF prelude injects a
        // SPECIFIC `import Numerics { Integer }` (so bare Integer/literal-default works) and SKIPS that
        // injection when the user already imports Numerics — so a whole-module import is exactly the case
        // where `Numerics` is imported but the prelude's `Integer` symbol was NOT added.
        bool fixedWidthUnlocked = _sa._importedModules.Contains(item: "Numerics") &&
                                  !_sa._importedSymbolNames.Contains(item: "Integer");
        if (fixedWidthUnlocked)
        {
            return;
        }

        _sa.ReportError(code: SemanticDiagnosticCode.SuflaeNumericImportRequired,
            message:
            $"Fixed-width numeric type '{typeExpr.Name}' is import-gated in Suflae — add " +
            "`import Numerics`. Bare numbers default to Integer/Decimal (fixed-width types like " +
            "S32/U64/F128 stay behind the import to keep the surface approachable).",
            location: typeExpr.Location);
    }

    /// <summary>Whether <paramref name="name"/> is a fixed-width / complex / quaternion numeric — a single
    /// leading class letter (<c>S U F D C Q</c> = signed / unsigned / float / decimal / complex / quaternion)
    /// followed by only digits (the bit width): <c>S8</c>, <c>U1024</c>, <c>F128</c>, <c>D64</c>, <c>C32</c>,
    /// <c>Q64</c>. <c>Integer</c>/<c>Text</c>/<c>Decimal</c>/<c>Bytes</c>/<c>Bool</c> never match.</summary>
    internal static bool IsImportGatedNumeric(string name)
    {
        int dot = name.LastIndexOf(value: '.');
        string bare = dot >= 0
            ? name[(dot + 1)..]
            : name;
        if (bare.Length < 2 || "SUFDCQ".IndexOf(value: bare[index: 0]) < 0)
        {
            return false;
        }

        for (int i = 1; i < bare.Length; i++)
        {
            if (!char.IsDigit(c: bare[index: i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Suflae representation unification, centralized: in a Suflae USER file an <c>entity E</c> is a
    /// <c>Roamed[E]</c> biased-RC handle, so any entity type resolved through <see cref="ResolveType"/>
    /// (fields, parameters, returns, local annotations, container element types — every slot) is
    /// substituted to <c>Roamed[E]</c> at this single choke point instead of per-site gates.
    /// <para>Carve-outs: the stdlib is excluded (borrowed RazorForge source keeps bare single-owner
    /// entities); an already-<c>Roamed[...]</c> type is idempotent (no <c>Roamed[Roamed[E]]</c>); and
    /// <c>Maybe[E]</c> / <c>Maybe[Roamed[E]]</c> (from the <c>E?</c> desugaring) COLLAPSES to a nullable
    /// bare <c>Roamed[E]</c> — an entity reference carries its own none via a null handle, so it needs no
    /// <c>Maybe</c> wrapper (value types still use <c>Maybe[T]</c> for <c>T?</c>).</para>
    /// </summary>
    private TypeSymbol RoamSuflaeEntitySlot(TypeSymbol resolved)
    {
        if (_sa._registry.Language != Language.Suflae)
        {
            return resolved;
        }

        if (_sa.IsStdlibFile(filePath: _sa._currentFilePath))
        {
            return resolved;
        }

        // An entity DEFINED in a user RazorForge module (imported `.rf`, not stdlib) is a genuine RF-realm
        // entity — it keeps RF ownership semantics (bare, deterministic teardown), exactly like an
        // `RF::`-qualified reference. SF must NOT auto-roam it (that silently wraps an RF entity in a
        // RoamController whose ABI the RF constructor/methods don't expect → crash). SF holds it as a bare
        // local, or wraps it in an SF entity for persistence (the RF:: wrapper pattern).
        if (resolved is TypeSymbol { Location.FileName: { } defFile } &&
            defFile.EndsWith(value: ".rf", comparisonType: StringComparison.OrdinalIgnoreCase) &&
            !_sa.IsStdlibFile(filePath: defFile))
        {
            return resolved;
        }

        if (_sa._registry.LookupType(name: RuntimeContract.Roamed) is not { } roamedDef)
        {
            return resolved;
        }

        return RoamSlot(resolved: resolved, roamedDef: roamedDef);
    }

    /// <summary>
    /// Substitutes an <c>entity E</c> slot to <c>Roamed[E]</c>, recursing into generic type ARGUMENTS so
    /// a container's element type is also lowered: <c>List[Box]</c> → <c>Roamed[List[Roamed[Box]]]</c>
    /// (the outer <c>List</c> is itself an entity, the inner <c>Box</c> is an element). Without the
    /// recursion the element stays a bare single-owner entity and the RC copy machinery never engages —
    /// storing it drops the refcount → dangling → UAF on read-back.
    /// </summary>
    private TypeSymbol RoamSlot(TypeSymbol resolved, TypeSymbol roamedDef)
    {
        // Already Roamed — idempotent, and its inner arg is intentionally left as-is (no Roamed[Roamed[E]]).
        if (IsRoamed(type: resolved))
        {
            return resolved;
        }

        // Lower entity type ARGUMENTS first (List[Box] → List[Roamed[Box]]), then wrap the top level.
        resolved = RoamTypeArguments(resolved: resolved, roamedDef: roamedDef);

        return resolved switch
        {
            EntityTypeSymbol entity => _sa._registry.GetOrCreateResolution(genericDef: roamedDef,
                typeArguments: [entity]),
            RecordTypeSymbol
            {
                GenericDefinition.Name: MaybeTypeName,
                TypeArguments: [EntityTypeSymbol innerEntity]
            } => _sa._registry.GetOrCreateResolution(genericDef: roamedDef,
                typeArguments: [innerEntity]),
            RecordTypeSymbol
            {
                GenericDefinition.Name: MaybeTypeName, TypeArguments: [{ } innerRoamed]
            } when IsRoamed(type: innerRoamed) => innerRoamed,
            _ => resolved
        };
    }

    /// <summary>
    /// Recursively lowers each entity generic type argument to <c>Roamed[E]</c> and rebuilds the generic
    /// resolution. <c>Maybe[...]</c> is skipped (the caller's switch collapses it); an already-<c>Roamed</c>
    /// argument is left untouched by <see cref="RoamSlot"/>'s idempotency guard.
    /// </summary>
    private TypeSymbol RoamTypeArguments(TypeSymbol resolved, TypeSymbol roamedDef)
    {
        (TypeSymbol? genericDef, IReadOnlyList<TypeSymbol>? args) = resolved switch
        {
            EntityTypeSymbol
            {
                IsGenericResolution: true, GenericDefinition: { } gd, TypeArguments: { } a
            } => ((TypeSymbol?)gd, (IReadOnlyList<TypeSymbol>?)a),
            RecordTypeSymbol
            {
                IsGenericResolution: true, GenericDefinition: { } gd, TypeArguments: { } a
            } => (gd, a),
            _ => (null, null)
        };
        if (genericDef == null || args == null || args.Count == 0)
        {
            return resolved;
        }

        // Maybe[E] is collapsed to a nullable bare Roamed[E] by RoamSlot's switch, not element-substituted.
        if (genericDef.Name == MaybeTypeName)
        {
            return resolved;
        }

        var newArgs = new List<TypeSymbol>(capacity: args.Count);
        bool changed = false;
        foreach (TypeSymbol arg in args)
        {
            TypeSymbol lowered = RoamSlot(resolved: arg, roamedDef: roamedDef);
            if (!ReferenceEquals(objA: lowered, objB: arg))
            {
                changed = true;
            }

            newArgs.Add(item: lowered);
        }

        return changed
            ? _sa._registry.GetOrCreateResolution(genericDef: genericDef, typeArguments: newArgs)
            : resolved;
    }

    private static bool IsRoamed(TypeSymbol type)
    {
        return type is RecordTypeSymbol { GenericDefinition.Name: RuntimeContract.Roamed }
            or WrapperTypeSymbol { Name: RuntimeContract.Roamed };
    }

    private TypeSymbol ResolveTypeCore(TypeExpression typeExpr)
    {
        // Buildtime type-position splice `${m.type}` in a decl-position expand column template: resolve
        // to the synthetic per-field placeholder. The registry substitutes it with each concrete
        // field's type when the SoA container is instantiated.
        if (typeExpr.SpliceHandle != null)
        {
            return new GenericParameterTypeSymbol(name: MemberExpandTemplateInfo
               .ColumnPlaceholderName);
        }

        // Associated-type projection: `Me/Iter`, `S/Iter` (the parser flattens these into the
        // type name). Handled before the normal Me/lookup paths so the `/` segment-walk wins
        // over module-path resolution when the root is `Me` or an in-scope generic parameter.
        if (typeExpr.Name.Contains(value: '/') &&
            TryResolveAssociatedProjection(typeExpr: typeExpr, result: out TypeSymbol projected))
        {
            return projected;
        }

        // `Me` in a member-routine signature refers to the owner type
        // (e.g. `routine SumS64.combine(you: Me) -> Me` — Me is SumS64).
        // Protocol contexts use ProtocolSelfTypeSymbol via ResolveProtocolType; concrete owners
        // resolve directly to the owner TypeSymbol so callers see Me as a normal nominal type.
        if (typeExpr is { Name: "Me", GenericArguments: not { Count: > 0 } } &&
            _sa._currentRoutine?.OwnerType is { } meOwner and not GenericParameterTypeSymbol)
        {
            return ResolveMeType(meOwner: meOwner);
        }

        // Handle tuple types from parser: Tuple(T, U, ...)
        if (typeExpr is { Name: "Tuple", GenericArguments.Count: > 0 })
        {
            return ResolveTupleType(typeExpr: typeExpr);
        }

        // Handle Routine type: Routine[(T, T), Bool] -> RoutineTypeSymbol
        if (typeExpr is { Name: "Routine", GenericArguments.Count: 2 })
        {
            return ResolveRoutineType(typeExpr: typeExpr);
        }

        // Handle generic types (List<T>, Dict<K, V>, Maybe<T>)
        if (typeExpr.GenericArguments is { Count: > 0 })
        {
            return ResolveGenericType(typeExpr: typeExpr);
        }

        // Generic parameters SHADOW same-named global types. A parameter's NAME is only a source
        // label; its identity is its positional SLOT in the enclosing scope. So resolve an in-scope
        // parameter BEFORE any global lookup — otherwise a user type whose name matches a stdlib
        // generic's parameter (e.g. `record T` vs `List[T]`) hijacks that parameter during the
        // generic's body/signature resolution (the RF-S954 monomorphization collision). Checking the
        // slot here, not a collision-proof renamed string, keeps the label genuinely irrelevant.
        //
        // The shadow is granted ONLY when the name is a GENUINE parameter of a generic-DEFINITION
        // scope (or resolves to nothing global). A name that resolves to a concrete type and is NOT a
        // definition's own parameter is a CONCRETE argument that merely landed in a scope's parameter
        // list — a specialized receiver (`Iterable[Text].join`) or a monomorphized instance's leaked
        // arg (`List[Byte].create`) or a bracket-parsed concrete element (`Tuple[S32, Text, Bool]`).
        // Those must resolve to the concrete type, not a bogus generic parameter, so they fall through.
        bool nameIsParam = IsGenericParameter(name: typeExpr.Name);
        TypeSymbol? globalType = LookupTypeWithImports(name: typeExpr.Name);
        if (nameIsParam &&
            (globalType is null || IsGenericDefinitionScopeParam(name: typeExpr.Name)))
        {
            return new GenericParameterTypeSymbol(name: typeExpr.Name,
                slot: GenericParameterSlot(name: typeExpr.Name));
        }

        // Module-scoped ambiguity: a bare name declared in 2+ imported modules (with the current
        // module NOT declaring its own to shadow) is ambiguous. Report here where a location exists.
        // Still resolve (first-match) below so downstream analysis doesn't cascade on a null type.
        List<string> ambiguousDeclarers = ImportedModulesDeclaring(name: typeExpr.Name);
        if (ambiguousDeclarers.Count >= 2)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.AmbiguousTypeReference,
                message: $"Type '{typeExpr.Name}' is declared in multiple imported modules " +
                         $"({string.Join(separator: ", ", values: ambiguousDeclarers)}) — the current module " +
                         "declares no such type to shadow it. Qualify the reference or restructure imports.",
                location: typeExpr.Location);
        }

        // Try to look up the type by name (including imported modules)
        TypeSymbol? resolved = globalType;
        if (resolved != null)
        {
            return resolved;
        }

        // A name that is an in-scope parameter but ALSO an unresolved global (the guard above only
        // returned for the genuine-definition-scope case) still resolves to the parameter here.
        if (nameIsParam)
        {
            return new GenericParameterTypeSymbol(name: typeExpr.Name,
                slot: GenericParameterSlot(name: typeExpr.Name));
        }

        return ResolveConstGenericOrUnknown(typeExpr: typeExpr);
    }

    /// <summary>
    /// Resolves the tail cases of <see cref="ResolveTypeCore"/>: buildtime value splices, const-generic
    /// literals, preset constants, and the unknown-type fallback. Extracted to keep the parent method
    /// within the allowed cognitive-complexity budget.
    /// </summary>
    private TypeSymbol ResolveConstGenericOrUnknown(TypeExpression typeExpr)
    {
        // Buildtime const-generic argument from a `${…}` value-splice (e.g. the payload buffer size
        // `${max(T.data_size().byte_size(), 8)}`). Resolves to a symbolic BuildtimeConstGenericTypeSymbol.
        // RoutineInfo.SubstituteType folds it to a concrete value once the enclosing type's parameters
        // are bound at monomorphization.
        if (typeExpr.BuildtimeValue != null)
        {
            return new BuildtimeConstGenericTypeSymbol(buildtimeExpr: typeExpr.BuildtimeValue);
        }

        // Check for const generic literal values (e.g., 4, 8u64, true/false)
        // These come from ParseTypeOrConstGeneric in the parser
        if (TryParseConstGenericLiteral(name: typeExpr.Name,
                value: out long constValue,
                explicitType: out string? explicitType))
        {
            return new ConstGenericValueTypeSymbol(literalText: typeExpr.Name,
                value: constValue,
                explicitTypeName: explicitType);
        }

        // Check for preset constants used as const generic arguments
        TypeSymbol? presetConst = ResolvePresetConstGeneric(typeExpr: typeExpr);
        if (presetConst != null)
        {
            return presetConst;
        }

        // Type not found — but a module-private `secret` type of this name may exist and have been
        // hidden on purpose; if so, report that explicitly rather than a misleading "unknown type".
        if (_sa.TryReportSecretTypeAccess(name: typeExpr.Name, location: typeExpr.Location))
        {
            return ErrorTypeSymbol.Instance;
        }

        _sa.ReportError(code: SemanticDiagnosticCode.UnknownType,
            message:
            $"Unknown type '{typeExpr.Name}'.{_sa.UnknownTypeSuggestion(typeName: typeExpr.Name)}",
            location: typeExpr.Location);
        return ErrorTypeSymbol.Instance;
    }

    /// <summary>
    /// Resolves a bare <c>Me</c> in a member-routine signature to its concrete owner type. A protocol
    /// owner yields <see cref="ProtocolSelfTypeSymbol"/>; a generic-definition owner yields the owner
    /// applied to its own parameters (<c>Box</c> → <c>Box[T]</c>); a plain owner returns itself.
    /// </summary>
    private TypeSymbol ResolveMeType(TypeSymbol meOwner)
    {
        // Protocol owner (protocol-extension routine like `Iterable[T].enumerate`): `Me` is the
        // abstract self, resolved per-implementer later. Use ProtocolSelf so a body construction
        // `EnumerateIterator[T, Me]` matches the signature's `Me` (both ProtocolSelf) — otherwise
        // self-applying to `Iterable[T]` mismatches the return type (S301).
        if (meOwner is ProtocolTypeSymbol)
        {
            return ProtocolSelfTypeSymbol.Instance;
        }

        // When the owner is a generic definition (e.g. `Box[T]`), `Me` must resolve to the
        // owner applied to its OWN generic parameters (`Box[T]`), not the bare definition.
        // Otherwise `Me` used as a type argument (`Wrapper[T, Me]`) drops the params, and
        // monomorphization can't substitute them — yielding a malformed `Wrapper[S64, Box]`
        // whose memberRoutines never get instantiated. For a non-generic owner this is a no-op.
        if (meOwner is { IsGenericDefinition: true, GenericParameters: { } ownerParams })
        {
            var selfArgs = ownerParams
                          .Select(selector: p => (TypeSymbol)new GenericParameterTypeSymbol(name: p))
                          .ToList();
            return _sa._registry.GetOrCreateResolution(genericDef: meOwner,
                typeArguments: selfArgs);
        }

        return meOwner;
    }

    /// <summary>
    /// Resolves a parser <c>Tuple(T, U, ...)</c> type expression to a tuple type by resolving each
    /// element type in order.
    /// </summary>
    private TupleTypeSymbol ResolveTupleType(TypeExpression typeExpr)
    {
        var elementTypes = new List<TypeSymbol>();
        foreach (TypeExpression argExpr in typeExpr.GenericArguments!)
        {
            TypeSymbol argType = ResolveType(typeExpr: argExpr);
            elementTypes.Add(item: argType);
        }

        return _sa._registry.GetOrCreateTupleType(elementTypes: elementTypes);
    }

    /// <summary>
    /// Resolves a <c>Routine[(T, T), Bool]</c> type expression to a <see cref="RoutineTypeSymbol"/>,
    /// unpacking the parameter tuple and resolving the return type.
    /// </summary>
    private RoutineTypeSymbol ResolveRoutineType(TypeExpression typeExpr)
    {
        TypeExpression paramTupleExpr = typeExpr.GenericArguments![index: 0];
        TypeExpression returnTypeExpr = typeExpr.GenericArguments[index: 1];

        var paramTypes = new List<TypeSymbol>();
        if (paramTupleExpr is { Name: "Tuple", GenericArguments: not null })
        {
            foreach (TypeExpression paramTypeExpr in paramTupleExpr.GenericArguments)
            {
                paramTypes.Add(item: ResolveType(typeExpr: paramTypeExpr));
            }
        }
        else
        {
            paramTypes.Add(item: ResolveType(typeExpr: paramTupleExpr));
        }

        TypeSymbol? returnType = ResolveType(typeExpr: returnTypeExpr);
        return _sa._registry.GetOrCreateRoutineType(parameterTypes: paramTypes,
            returnType: returnType,
            isFailable: false);
    }

    /// <summary>
    /// Resolves a type expression within a protocol context.
    /// Handles the special 'Me' type which represents the implementing type.
    /// </summary>
    /// <param name="typeExpr">The type expression to resolve.</param>
    /// <returns>The resolved type, or ProtocolSelfTypeSymbol for 'Me'.</returns>
    internal TypeSymbol ResolveProtocolType(TypeExpression? typeExpr)
    {
        if (typeExpr == null)
        {
            return ErrorTypeSymbol.Instance;
        }

        // Handle the special 'Me' type in protocol signatures
        if (typeExpr is { Name: "Me", GenericArguments: not { Count: > 0 } })
        {
            return ProtocolSelfTypeSymbol.Instance;
        }

        // Associated-type projection in a protocol signature (e.g. `Me/Iter` in
        // `routine Me.iter() -> Me/Iter`). `Me` here is the abstract protocol self, so the
        // projection is deferred and resolved per-implementer during monomorphization.
        if (typeExpr.Name.Contains(value: '/'))
        {
            string[] segments = typeExpr.Name.Split(separator: '/');
            TypeSymbol? projBase;
            if (segments[0] == "Me")
            {
                projBase = ProtocolSelfTypeSymbol.Instance;
            }
            else if (IsGenericParameter(name: segments[0]))
            {
                projBase = new GenericParameterTypeSymbol(name: segments[0]);
            }
            else
            {
                projBase = null;
            }

            if (projBase != null && segments.Length - 1 <= _assocOptions.MaxProjectionDepth)
            {
                TypeSymbol current = projBase;
                for (int i = 1; i < segments.Length; i++)
                {
                    current = ProjectAssociatedType(baseType: current, slotName: segments[i]);
                }

                typeExpr.ResolvedType = current;
                return current;
            }
        }

        // Fall back to normal type resolution
        return ResolveType(typeExpr: typeExpr);
    }

    /// <summary>
    /// Resolves a generic type expression (e.g., <c>List[T]</c>, <c>Maybe[s32]</c>) to a concrete
    /// generic resolution, looking up the base type, resolving each type argument, validating
    /// argument counts and generic constraints, and returning the cached resolved type.
    /// </summary>
    private TypeSymbol ResolveGenericType(TypeExpression typeExpr)
    {
        TypeSymbol? genericDef = LookupTypeWithImports(name: typeExpr.Name);
        if (genericDef == null)
        {
            if (_sa.TryReportSecretTypeAccess(name: typeExpr.Name, location: typeExpr.Location))
            {
                return ErrorTypeSymbol.Instance;
            }

            _sa.ReportError(code: SemanticDiagnosticCode.UnknownType,
                message:
                $"Unknown type '{typeExpr.Name}'.{_sa.UnknownTypeSuggestion(typeName: typeExpr.Name)}",
                location: typeExpr.Location);
            return ErrorTypeSymbol.Instance;
        }

        if (!genericDef.IsGenericDefinition)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.TypeNotGeneric,
                message: $"Type '{typeExpr.Name}' is not a generic type.",
                location: typeExpr.Location);
            return ErrorTypeSymbol.Instance;
        }

        var typeArgs = new List<TypeSymbol>();
        foreach (TypeExpression argExpr in typeExpr.GenericArguments!)
        {
            TypeSymbol argType = ResolveType(typeExpr: argExpr);
            typeArgs.Add(item: argType);
        }

        if (genericDef.GenericParameters!.Count != typeArgs.Count)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"Type '{typeExpr.Name}' expects {genericDef.GenericParameters.Count} type arguments, got {typeArgs.Count}.",
                location: typeExpr.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Reject None as a type argument except Result<None>.
        // Lookup<None> is ambiguous in the type_id carrier model because None is also the absent sentinel.
        string? genericDefCarrierName = GetCarrierBaseName(type: genericDef);
        foreach (TypeSymbol arg in typeArgs)
        {
            if (arg is not { Name: "None" } || genericDefCarrierName is "Result")
            {
                continue;
            }

            _sa.ReportError(code: SemanticDiagnosticCode.NoneAsTypeArgument,
                message: "'None' cannot be used as a type argument. " +
                         "'None' is a unit type with no value.",
                location: typeExpr.Location);
            return ErrorTypeSymbol.Instance;
        }

        // Reject nested Maybe types (#83): Maybe[Maybe[T]] / T??
        if (genericDefCarrierName == MaybeTypeName && IsMaybeType(type: typeArgs[index: 0]))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.NestedMaybeProhibited,
                message:
                "'Maybe[Maybe[T]]' is not allowed. A single '?' already expresses optionality.",
                location: typeExpr.Location);
        }

        // Post-Owned-retirement: bare entity T IS the lvalue/bound form, so Maybe[T] /
        // Result[T] / Lookup[T] over a bare entity is now a valid carrier shape — the
        // carrier owns the bound entity directly. The previous S953 rejection of this
        // case was removed alongside Maybe's `needs T is RecordType` constraint.
        // The identity/ownership-transfer semantics of `Maybe[Entity].unwrap() -> T`
        // are still an open design question (likely `T` — return-position rvalue).

        // Post-Owned-retirement: bare entity T in collection slots is fine — bound T is
        // record-shaped (pointer-sized) so List[T]/Dict[K,T] hold the same layout regardless
        // of whether T is entity or record. Entity-ownership semantics (no duplicate handles)
        // are enforced separately at SA-level assignment/copy sites.

        // Validate generic constraints
        ValidateGenericConstraints(genericDef: genericDef,
            typeArgs: typeArgs,
            location: typeExpr.Location);

        return _sa._registry.GetOrCreateResolution(genericDef: genericDef,
            typeArguments: typeArgs);
    }

    /// <summary>
    /// Validates that type arguments satisfy generic constraints.
    /// </summary>
    internal void ValidateGenericConstraints(TypeSymbol genericDef, List<TypeSymbol> typeArgs,
        SourceLocation location)
    {
        // Arity guard: a caller that skips ResolveGenericType's own count check (e.g. the creator path
        // `Guarded[ReadOnly](from: n)` — one type arg for a two-param `Guarded[T, P]`) must get a clean
        // WrongTypeArgumentCount diagnostic, NOT an IndexOutOfRange crash in the param↔arg zip below.
        if (genericDef.GenericParameters is { } arityParams && arityParams.Count != typeArgs.Count)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.WrongTypeArgumentCount,
                message:
                $"Type '{genericDef.Name}' expects {arityParams.Count} type arguments, got {typeArgs.Count}.",
                location: location);
            return;
        }

        if (genericDef.GenericConstraints == null || genericDef.GenericConstraints.Count == 0)
        {
            return; // No constraints to validate
        }

        // Build parameter name to type argument mapping
        var paramToArg = new Dictionary<string, TypeSymbol>();
        for (int i = 0; i < genericDef.GenericParameters!.Count; i++)
        {
            paramToArg[key: genericDef.GenericParameters[index: i]] = typeArgs[index: i];
        }

        foreach (GenericConstraintDeclaration constraint in genericDef.GenericConstraints)
        {
            if (!paramToArg.TryGetValue(key: constraint.ParameterName,
                    value: out TypeSymbol? typeArg))
            {
                continue; // Constraint for unknown parameter
            }

            // Skip validation if type arg is a generic parameter itself
            if (typeArg is GenericParameterTypeSymbol)
            {
                continue;
            }

            // Skip error types to avoid cascading errors
            if (typeArg.Category == TypeCategory.Error)
            {
                continue;
            }

            DispatchConstraintValidation(typeArg: typeArg,
                constraint: constraint,
                location: location);
        }
    }

    /// <summary>
    /// Routes a single generic constraint to its category-specific validator based on
    /// <see cref="GenericConstraintDeclaration.ConstraintType"/>.
    /// </summary>
    private void DispatchConstraintValidation(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        switch (constraint.ConstraintType)
        {
            case ConstraintKind.Obeys:
                ValidateFollowsConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.RecordType:
                ValidateValueTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.EntityType:
                ValidateReferenceTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.RoutineType:
                ValidateRoutineTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.ChoiceType:
                ValidateChoiceTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.VariantType:
                ValidateVariantTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.Crashable:
                ValidateCrashableTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.ConstGeneric:
                ValidateConstGenericConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;

            case ConstraintKind.TypeEquality:
                ValidateTypeEqualityConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                break;
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies an <c>obeys</c> constraint by implementing
    /// all of the required protocols listed in the constraint.
    /// </summary>
    private void ValidateFollowsConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (constraint.ConstraintTypes == null)
        {
            return;
        }

        foreach (TypeExpression protoExpr in constraint.ConstraintTypes.Where(predicate: p =>
                     !_sa.ImplementsProtocol(type: typeArg, protocolName: p.Name)))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ProtocolConstraintViolation,
                message:
                $"Type '{typeArg.Name}' does not implement protocol '{protoExpr.Name}' required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>valuetype</c> constraint,
    /// requiring the argument to be a record (value type).
    /// </summary>
    private void ValidateValueTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Record)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ValueTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a value type (record) required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>referencetype</c> constraint,
    /// requiring the argument to be an entity (reference type).
    /// </summary>
    private void ValidateReferenceTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        // Protocol type arguments are allowed: any concrete value bound to the
        // parameter at the call site will itself be an entity (or fail S152 at
        // that point). Without this, `Accessing[Iterable[T]]` and similar
        // protocol-as-type-argument shapes can't satisfy the entity constraint
        // even though every value they accept is structurally an entity.
        if (typeArg is ProtocolTypeSymbol)
        {
            return;
        }

        if (typeArg.Category != TypeCategory.Entity)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ReferenceTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a reference type (entity) required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Resolves an associated-type projection name (<c>Me/Iter</c>, <c>S/Iter</c>) by walking its
    /// <c>/</c> segments. Returns false when the root is not a projection root (e.g. a module
    /// path), leaving the name to the normal lookup path.
    /// </summary>
    /// <param name="typeExpr">The type expression whose <c>Name</c> contains the <c>/</c>-delimited projection path.</param>
    /// <param name="result">On success, the resolved concrete type for the projection.</param>
    private bool TryResolveAssociatedProjection(TypeExpression typeExpr, out TypeSymbol result)
    {
        result = ErrorTypeSymbol.Instance;
        string[] segments = typeExpr.Name.Split(separator: '/');
        if (segments.Length < 2)
        {
            return false;
        }

        string root = segments[0];

        // The root must be `Me` or an in-scope generic parameter. Anything else (a module path
        // such as `razorforge/Collections.Dict`) is not a projection — defer to normal lookup.
        TypeSymbol baseType;
        if (root == "Me")
        {
            if (_sa._currentRoutine?.OwnerType is not { } owner ||
                owner is GenericParameterTypeSymbol)
            {
                return false;
            }

            baseType = SelfApplyOwner(owner: owner);
        }
        else if (IsGenericParameter(name: root))
        {
            baseType = new GenericParameterTypeSymbol(name: root);
        }
        else
        {
            return false;
        }

        // Depth limit (configurable via AssociatedTypeOptions — not hardcoded).
        int depth = segments.Length - 1;
        if (depth > _assocOptions.MaxProjectionDepth)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.UnknownType,
                message:
                $"Associated-type projection '{typeExpr.Name}' exceeds the maximum projection " +
                $"depth of {_assocOptions.MaxProjectionDepth}.",
                location: typeExpr.Location);
            return true; // handled (with error) — don't fall through to module-path lookup
        }

        TypeSymbol current = baseType;
        for (int i = 1; i < segments.Length; i++)
        {
            current = ProjectAssociatedType(baseType: current, slotName: segments[i]);
        }

        result = current;
        return true;
    }

    /// <summary>
    /// Projects one associated-type slot from a base type. When the base is concrete and has a
    /// binding for the slot, returns the bound concrete type; otherwise (generic param, protocol
    /// self, or not-yet-bound) returns a deferred <see cref="AssociatedProjectionTypeSymbol"/> that
    /// monomorphization resolves once the base becomes concrete.
    /// </summary>
    private static TypeSymbol ProjectAssociatedType(TypeSymbol baseType, string slotName)
    {
        Dictionary<string, TypeSymbol>? bindings = baseType switch
        {
            EntityTypeSymbol e => e.AssociatedTypeBindings,
            RecordTypeSymbol r => r.AssociatedTypeBindings,
            _ => null
        };
        if (bindings != null && bindings.TryGetValue(key: slotName, value: out TypeSymbol? bound))
        {
            return bound;
        }

        return new AssociatedProjectionTypeSymbol(baseType: baseType, slotName: slotName);
    }

    /// <summary>
    /// Resolves a generic-definition owner to itself applied over its own generic parameters
    /// (<c>Box</c> → <c>Box[T]</c>) so projections off <c>Me</c> monomorphize correctly. No-op for
    /// non-generic owners.
    /// </summary>
    private TypeSymbol SelfApplyOwner(TypeSymbol owner)
    {
        // Protocol owner: `Me` is the abstract self (deferred), not the protocol applied to itself.
        if (owner is ProtocolTypeSymbol)
        {
            return ProtocolSelfTypeSymbol.Instance;
        }

        if (owner is { IsGenericDefinition: true, GenericParameters: { } ownerParams })
        {
            var selfArgs = ownerParams
                          .Select(selector: p => (TypeSymbol)new GenericParameterTypeSymbol(name: p))
                          .ToList();
            return _sa._registry.GetOrCreateResolution(genericDef: owner, typeArguments: selfArgs);
        }

        return owner;
    }

    internal bool IsGenericParameter(string name)
    {
        if (_sa._currentRoutine?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        if (_sa._currentType?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        // Extension memberRoutines (routine Foo[T].bar()) — T is the owner type's generic param,
        // not the routine's own, so we also check the owner type's generic parameters.
        if (_sa._currentRoutine?.OwnerType?.GenericParameters?.Contains(value: name) == true)
        {
            return true;
        }

        // Universal memberRoutines (routine T.bar()) — owner IS the generic parameter itself.
        if (_sa._currentRoutine?.OwnerType is GenericParameterTypeSymbol gp && gp.Name == name)
        {
            return true;
        }

        // Wrapper types (Hijacked[T].foo) carry the inner-type binding via
        // InnerType rather than GenericParameters. If the inner type is
        // itself a generic-parameter placeholder, accept that name.
        if (_sa._currentRoutine?.OwnerType is WrapperTypeSymbol
            {
                InnerType: GenericParameterTypeSymbol wgp
            } && wgp.Name == name)
        {
            return true;
        }

        // Some owner types lose their declared GenericParameters list when
        // they're stored back as a resolved-type representation (e.g.,
        // Name="Hijacked[T]" with an empty GenericParameters list). Recover
        // the bound names by parsing the bracketed segment of the owner Name.
        string? ownerName = _sa._currentRoutine?.OwnerType?.Name;
        if (!string.IsNullOrEmpty(value: ownerName) && ownerName.Contains(value: '['))
        {
            return OwnerNameBracketBindsParameter(ownerName: ownerName, name: name);
        }

        return false;
    }

    /// <summary>
    /// Parses the bracketed segment of an owner type name (e.g. <c>Hijacked[T]</c>) and reports whether
    /// any top-level comma-separated argument equals <paramref name="name"/>. Nested brackets are
    /// tracked so a comma inside an inner <c>[...]</c> does not split an argument.
    /// </summary>
    private static bool OwnerNameBracketBindsParameter(string ownerName, string name)
    {
        int open = ownerName.IndexOf(value: '[');
        int close = ownerName.LastIndexOf(value: ']');
        if (close <= open)
        {
            return false;
        }

        string inner = ownerName[(open + 1)..close];
        int depth = 0;
        int start = 0;
        for (int i = 0; i <= inner.Length; i++)
        {
            if (i == inner.Length || inner[index: i] == ',' && depth == 0)
            {
                string arg = inner[start..i]
                   .Trim();
                if (arg == name)
                {
                    return true;
                }

                start = i + 1;
            }
            else if (inner[index: i] == '[')
            {
                depth++;
            }
            else if (inner[index: i] == ']')
            {
                depth--;
            }
        }

        return false;
    }

    /// <summary>
    /// The 0-based positional SLOT of an in-scope generic parameter <paramref name="name"/>, or
    /// <c>-1</c> when it is not a parameter in the current scope. Mirrors the scope-lookup ORDER of
    /// <see cref="IsGenericParameter"/> (routine params, then the owning type's, then the extension
    /// receiver's) so the slot is the index within whichever scope binds the name. This is the
    /// parameter's identity signal — see <see cref="GenericParameterTypeSymbol.Slot"/>.
    /// </summary>
    internal int GenericParameterSlot(string name)
    {
        int slot;
        if ((slot = _sa._currentRoutine?.GenericParameters?.IndexOf(item: name) ?? -1) >= 0)
        {
            return slot;
        }

        if ((slot = _sa._currentType?.GenericParameters?.IndexOf(item: name) ?? -1) >= 0)
        {
            return slot;
        }

        if ((slot = _sa._currentRoutine?.OwnerType?.GenericParameters?.IndexOf(item: name) ??
                    -1) >= 0)
        {
            return slot;
        }

        // Universal memberRoutine (`routine T.bar()`): the owner IS the parameter — a single-slot scope.
        if (_sa._currentRoutine?.OwnerType is GenericParameterTypeSymbol or WrapperTypeSymbol
            {
                InnerType: GenericParameterTypeSymbol
            })
        {
            return 0;
        }

        return -1;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a GENUINE parameter of a generic-DEFINITION scope — a
    /// still-unbound hole of a template — as opposed to a concrete type argument that merely appears
    /// in some scope's parameter list. Used to decide whether an in-scope name may shadow a same-named
    /// global type: only a definition's own unbound parameter shadows (so `record T` cannot hijack
    /// `List[T]`'s parameter), while a concrete argument name (a specialized receiver's `Text`, a
    /// monomorphized instance's leaked arg, a concrete tuple's bracket-parsed element) does NOT — it
    /// must resolve to that concrete type. The routine's OWN parameter list is intentionally excluded
    /// here because a monomorphized instance can carry concrete argument names in it; such a name only
    /// resolves as a parameter when it has no global type at all (handled by the caller's fall-through).
    /// </summary>
    internal bool IsGenericDefinitionScopeParam(string name)
    {
        if (_sa._currentType is { IsGenericDefinition: true, GenericParameters: { } typeParams } &&
            typeParams.Contains(value: name))
        {
            return true;
        }

        if (_sa._currentRoutine?.OwnerType is
                { IsGenericDefinition: true, GenericParameters: { } ownerParams } &&
            ownerParams.Contains(value: name))
        {
            return true;
        }

        // The routine's OWN declared parameters (a free generic `identity[T]` or a member routine's
        // memberRoutine-generic `Holder[A].mapped[U]`) shadow too — but ONLY on the genuine TEMPLATE, whose
        // GenericDefinition is null. A monomorphized instance (GenericDefinition set) can carry
        // substituted concrete argument names in its GenericParameters list (e.g. `List[Byte].create`
        // holding "Byte"); treating those as parameters is exactly the leak this guard prevents.
        if (_sa._currentRoutine is
                { GenericDefinition: null, GenericParameters: { } routineParams } &&
            routineParams.Contains(value: name))
        {
            return true;
        }

        // Universal / wrapper-inner owner: the owner IS the parameter (a single-hole template scope).
        if (_sa._currentRoutine?.OwnerType is GenericParameterTypeSymbol gp && gp.Name == name)
        {
            return true;
        }

        return _sa._currentRoutine?.OwnerType is WrapperTypeSymbol
        {
            InnerType: GenericParameterTypeSymbol wgp
        } && wgp.Name == name;
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>routinetype</c> constraint,
    /// requiring the argument to be a routine (function) type.
    /// </summary>
    private void ValidateRoutineTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Routine)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.RoutineTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a routine type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>choicetype</c> constraint,
    /// requiring the argument to be a choice (discriminated union of unit cases) type.
    /// </summary>
    private void ValidateChoiceTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Choice)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ChoiceTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a choice type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates that a type argument satisfies a <c>varianttype</c> constraint,
    /// requiring the argument to be a variant (tagged union with payloads) type.
    /// </summary>
    private void ValidateVariantTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Variant)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.VariantTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a variant type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    private void ValidateCrashableTypeConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (typeArg.Category != TypeCategory.Crashable)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.CrashableTypeConstraintViolation,
                message:
                $"Type '{typeArg.Name}' is not a crashable type required by constraint on '{constraint.ParameterName}'.",
                location: location);
        }
    }

    /// <summary>
    /// Validates a const generic constraint (e.g., requires N is Address).
    /// Const generics are build-time constant values, not types.
    /// </summary>
    private void ValidateConstGenericConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        TypeExpression requiredTypeExpr = constraint.ConstraintTypes[index: 0];
        string requiredTypeName = requiredTypeExpr.Name;

        // Type-kind marker names (e.g. `needs T is EntityType`) are stored as ConstGeneric
        // constraints by the parser, but they assert a category, not const-compatibility.
        // Validate the corresponding category and return.
        if (TryValidateTypeKindMarker(requiredTypeName: requiredTypeName,
                typeArg: typeArg,
                constraint: constraint,
                location: location))
        {
            return;
        }

        // Resolve the required type and check ConstCompatible conformance
        TypeSymbol? requiredType = LookupTypeWithImports(name: requiredTypeName);
        if (requiredType == null)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.InvalidConstGenericType,
                message:
                $"Unknown const generic type '{requiredTypeName}' for '{constraint.ParameterName}'.",
                location: location);
            return;
        }

        // Check explicit protocol conformance OR choice category.
        // Uses explicit-only check (not structural conformance) because ConstCompatible
        // is a marker protocol — structural conformance would match any type.
        bool isValid =
            _sa.ExplicitlyImplementsProtocol(type: requiredType,
                protocolName: "ConstCompatible") || requiredType.Category == TypeCategory.Choice;

        if (!isValid)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.InvalidConstGenericType,
                message:
                $"Type '{requiredTypeName}' is not valid for const generic '{constraint.ParameterName}'. " +
                "Const generic types must implement ConstCompatible or be a choice type.",
                location: location);
            return;
        }

        // Const generic literal values (e.g., 4, 8u64) — validate typed literals match
        if (typeArg is ConstGenericValueTypeSymbol constVal)
        {
            if (constVal.ExplicitTypeName != null && constVal.ExplicitTypeName != requiredTypeName)
            {
                _sa.ReportError(code: SemanticDiagnosticCode.ConstGenericTypeMismatch,
                    message:
                    $"Const generic '{constraint.ParameterName}' requires type '{requiredTypeName}', got '{constVal.ExplicitTypeName}'.",
                    location: location);
            }

            return; // Accept — literal value satisfies const generic constraint
        }

        // Verify the type argument matches the expected const type
        if (typeArg.Name != requiredTypeName)
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ConstGenericTypeMismatch,
                message:
                $"Const generic '{constraint.ParameterName}' requires type '{requiredTypeName}', got '{typeArg.Name}'.",
                location: location);
        }

        // #65: Choice const generic values must use fully-qualified names (e.g., Mode.DEBUG not bare DEBUG)
        if (requiredType.Category == TypeCategory.Choice && !typeArg.Name.Contains(value: '.'))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.ConstGenericTypeMismatch,
                message:
                $"Choice const generic '{constraint.ParameterName}' requires fully-qualified case name " +
                $"(e.g., '{requiredTypeName}.{typeArg.Name}'), not bare '{typeArg.Name}'.",
                location: location);
        }
    }

    /// <summary>
    /// Handles a type-kind marker name (<c>EntityType</c>/<c>RecordType</c>/<c>RoutineType</c>/
    /// <c>ChoiceType</c>/<c>VariantType</c>/<c>Crashable</c>) that the parser stored as a ConstGeneric
    /// constraint: dispatches to the matching category validator and returns true when handled. Returns
    /// false for a genuine const-compatibility required type, which the caller validates instead.
    /// </summary>
    private bool TryValidateTypeKindMarker(string requiredTypeName, TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        switch (requiredTypeName)
        {
            case "EntityType":
                ValidateReferenceTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                return true;
            case "RecordType":
                ValidateValueTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                return true;
            case "RoutineType":
                ValidateRoutineTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                return true;
            case "ChoiceType":
                ValidateChoiceTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                return true;
            case "VariantType":
                ValidateVariantTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                return true;
            case "Crashable":
                ValidateCrashableTypeConstraint(typeArg: typeArg,
                    constraint: constraint,
                    location: location);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Validates a type equality constraint (e.g., requires T in [s32, u8]).
    /// </summary>
    private void ValidateTypeEqualityConstraint(TypeSymbol typeArg,
        GenericConstraintDeclaration constraint, SourceLocation location)
    {
        if (constraint.ConstraintTypes == null || constraint.ConstraintTypes.Count == 0)
        {
            return;
        }

        // Check if typeArg matches any of the allowed types
        var allowedNames = constraint.ConstraintTypes
                                     .Select(selector: t => t.Name)
                                     .ToList();
        if (allowedNames.Any(predicate: n => n == typeArg.Name || n == typeArg.BareName))
        {
            return; // Found a match
        }

        // No match found
        string allowedTypesList = string.Join(separator: ", ", values: allowedNames);
        _sa.ReportError(code: SemanticDiagnosticCode.TypeEqualityConstraintViolation,
            message:
            $"Type '{typeArg.Name}' is not in [{allowedTypesList}] for constraint on '{constraint.ParameterName}'.",
            location: location);
    }

    /// <summary>
    /// Tries to parse a type expression name as a const generic literal value.
    /// Handles untyped integers (4), typed integers (4u64, 8s32), and booleans (true, false).
    /// </summary>
    private static bool TryParseConstGenericLiteral(string name, out long value,
        out string? explicitType)
    {
        value = 0;
        explicitType = null;

        // Boolean literals
        if (name == "true")
        {
            value = 1;
            explicitType = "Bool";
            return true;
        }

        if (name == "false")
        {
            value = 0;
            explicitType = "Bool";
            return true;
        }

        // Untyped integer literal (e.g., "4", "128")
        if (long.TryParse(s: name, result: out value))
        {
            return true; // No explicit type — inferred from constraint
        }

        // Typed integer literals (e.g., "4u64", "8s32")
        // Try common suffixes
        (string suffix, string typeName)[] suffixes =
        [
            ("u64", "U64"), ("s64", "S64"),
            ("u32", "U32"), ("s32", "S32"),
            ("u16", "U16"), ("s16", "S16"),
            ("u8", "U8"), ("s8", "S8"),
            ("u128", "U128"), ("s128", "S128"),
            ("addr", "Address")
        ];

        foreach ((string suffix, string typeName) in suffixes)
        {
            if (name.EndsWith(value: suffix, comparisonType: StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(s: name[..^suffix.Length], result: out value))
            {
                explicitType = typeName;
                return true;
            }
        }

        return false;
    }

    private TypeSymbol? ResolvePresetConstGeneric(TypeExpression typeExpr)
    {
        VariableInfo? preset = LookupPresetWithImports(name: typeExpr.Name);
        if (preset is not { IsPreset: true, PresetValue: not null })
        {
            return null;
        }

        return ResolvePresetConstGenericValue(preset: preset,
            useLocation: typeExpr.Location,
            visited: new HashSet<string>(comparer: StringComparer.Ordinal));
    }

    private VariableInfo? LookupPresetWithImports(string name)
    {
        VariableInfo? preset = _sa._registry.LookupVariable(name: name);
        if (preset is { IsPreset: true })
        {
            return preset;
        }

        if (_sa._currentModuleName != null && !name.Contains(value: '.'))
        {
            preset = _sa._registry.LookupVariable(name: $"{_sa._currentModuleName}.{name}");
            if (preset is { IsPreset: true })
            {
                return preset;
            }
        }

        return _sa._importedModules
                  .Select(selector: ns => _sa._registry.LookupVariable(name: $"{ns}.{name}"))
                  .FirstOrDefault(predicate: v => v is { IsPreset: true });
    }

    private TypeSymbol ResolvePresetConstGenericValue(VariableInfo preset,
        SourceLocation useLocation, HashSet<string> visited)
    {
        if (!visited.Add(item: preset.QualifiedName))
        {
            _sa.ReportError(code: SemanticDiagnosticCode.PresetNotConstant,
                message:
                $"Preset '{preset.QualifiedName}' cannot be used as a const generic because it forms a cycle.",
                location: useLocation);
            return ErrorTypeSymbol.Instance;
        }

        switch (preset.PresetValue)
        {
            case LiteralExpression literal when TryBuildConstGenericFromLiteral(literal: literal,
                declaredType: preset.Type,
                value: out ConstGenericValueTypeSymbol? constValue):
                return constValue!;

            case IdentifierExpression id:
            {
                VariableInfo? nestedPreset = LookupPresetWithImports(name: id.Name);
                if (nestedPreset is { IsPreset: true, PresetValue: not null })
                {
                    return ResolvePresetConstGenericValue(preset: nestedPreset,
                        useLocation: useLocation,
                        visited: visited);
                }

                break;
            }
        }

        _sa.ReportError(code: SemanticDiagnosticCode.PresetNotConstant,
            message:
            $"Preset '{preset.QualifiedName}' cannot be used as a const generic because its initializer is not a supported build-time literal.",
            location: useLocation);
        return ErrorTypeSymbol.Instance;
    }

    private static bool TryBuildConstGenericFromLiteral(LiteralExpression literal,
        TypeSymbol declaredType, out ConstGenericValueTypeSymbol? value)
    {
        value = null;

        switch (literal.LiteralType)
        {
            case TokenType.True:
                value = new ConstGenericValueTypeSymbol(literalText: "true",
                    value: 1,
                    explicitTypeName: "Bool");
                return true;

            case TokenType.False:
                value = new ConstGenericValueTypeSymbol(literalText: "false",
                    value: 0,
                    explicitTypeName: "Bool");
                return true;

            case TokenType.IntegerLiteral:
            case TokenType.S8Literal:
            case TokenType.S16Literal:
            case TokenType.S32Literal:
            case TokenType.S64Literal:
            case TokenType.S128Literal:
            case TokenType.S256Literal:
            case TokenType.U8Literal:
            case TokenType.U16Literal:
            case TokenType.U32Literal:
            case TokenType.U64Literal:
            case TokenType.U128Literal:
            case TokenType.U256Literal:
            case TokenType.AddressLiteral:
                if (literal.Value is string rawNumeric && TryParseConstGenericLiteral(
                        name: rawNumeric,
                        value: out long parsed,
                        explicitType: out string? explicitType))
                {
                    value = new ConstGenericValueTypeSymbol(literalText: rawNumeric,
                        value: parsed,
                        explicitTypeName: explicitType ??
                                          GetConstGenericExplicitTypeName(
                                              declaredType: declaredType));
                    return true;
                }

                return false;
        }

        return false;
    }

    private static string? GetConstGenericExplicitTypeName(TypeSymbol declaredType)
    {
        return declaredType.Name switch
        {
            "Bool" or "Address" or "U8" or "U16" or "U32" or "U64" or "U128" or "S8" or "S16"
                or "S32" or "S64" or "S128" => declaredType.Name,
            _ => null
        };
    }

    // Static helpers duplicated from SA (originally private static)

    private static string? GetCarrierBaseName(TypeSymbol type)
    {
        if (type is not RecordTypeSymbol r)
        {
            return null;
        }

        string baseName = r.GenericDefinition?.Name ?? r.Name;
        return baseName is MaybeTypeName or "Result" or "Lookup"
            ? baseName
            : null;
    }

    private static bool IsMaybeType(TypeSymbol type)
    {
        return GetCarrierBaseName(type: type) == MaybeTypeName;
    }

    #endregion
}
