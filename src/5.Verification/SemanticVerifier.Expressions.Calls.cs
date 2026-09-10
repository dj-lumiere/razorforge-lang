using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

public sealed partial class SemanticVerifier
{
    /// <summary>
    /// Prefix → creator-variant name for CONVERSION-variant member-routine chains: <c>.try_S64()</c> etc.
    /// map to <c>S64.try_create</c>/<c>check_create</c>/<c>lookup_create</c> (the recoverable forms of the
    /// <c>.S64!()</c> → <c>S64.create!(from:)</c> conversion). See the conversion-variant resolution in
    /// <c>AnalyzeCallExpression</c>.
    /// </summary>
    // A conversion call `.try_TYPE()` resolves to TYPE's failable-creator recovery variant, named with the
    // reserved "create" token (see ErrorHandlingGenerator.GenerateVariantName): `.try_S32()` → `S32`'s
    // `try_create`. (The `try_K` surface heuristic — strip prefix, test remainder as a type — is a pending
    // design decision vs an explicit `K.try_create`; this table is the current spelling.)
    private static readonly (string prefix, string cname)[] ConversionVariantCreators =
    [
        ("try_", "try_create"),
        ("check_", "check_create"),
        ("lookup_", "lookup_create")
    ];

    private const string StartRoutineName = "start";

    private const string UseWhenHint =
        "Use 'when' to match the result, '??' to provide a default, or make the enclosing routine failable (!).";

    private const string NoneTypeName = "None";
    private const string ModifyMemberRoutineName = "modify";

    /// <summary>
    /// Enforces the realm gate at a free-routine call site: a FOREIGN routine (C extern / LLVM intrinsic)
    /// must be called with its realm qualifier (`C::name(...)` / `LLVM::name(...)`), and a `C::`/`LLVM::`
    /// qualifier must resolve to a routine of that realm. `RF::`/`SF::` qualifiers (native cross-realm
    /// references) are allowed through. Reports a diagnostic when the call is illegal.
    /// </summary>
    private void CheckCallRealm(IdentifierExpression callee, RoutineInfo routine,
        SourceLocation location)
    {
        string? tag = callee.Realm;
        if (tag == null)
        {
            if (routine.IsForeign)
            {
                string realm = routine.Realm == RoutineRealm.C
                    ? "C"
                    : "LLVM";
                // `import Module.C::name` lifts the qualifier requirement for that one routine — a bare
                // call is then legitimate (the import is the explicit realm-crossing opt-in).
                if (_importedForeignAliases.Contains(item: $"{realm}::{routine.Name}"))
                {
                    return;
                }

                ReportError(code: SemanticDiagnosticCode.DirectWiredRoutineCall,
                    message:
                    $"Foreign routine '{routine.Name}' lives in the {realm} realm — call it as " +
                    $"'{realm}::{routine.Name}(...)', or bring it into scope with " +
                    $"'import <module>.{realm}::{routine.Name}'.",
                    location: location);
            }

            return;
        }

        if (tag is "C" or "LLVM")
        {
            RoutineRealm expected = tag == "C"
                ? RoutineRealm.C
                : RoutineRealm.LLVM;
            if (routine.Realm != expected)
            {
                ReportError(code: SemanticDiagnosticCode.DirectWiredRoutineCall,
                    message: $"'{tag}::{routine.Name}' does not name a {tag} routine.",
                    location: location);
            }
        }
    }

    /// <summary>
    /// The comptime metadata-reflection intrinsics (`nameof`/`orderof`/`typeof`/`typeidof`/`valueof`/
    /// `placeof`/`sizeof`). Each reads a comptime property off the active `expand` handle (or, for
    /// `sizeof`/`typeof`, a type). Folded off the unroll context at monomorphization; see
    /// <c>GenericAstRewriter.FoldMetadataIntrinsic</c>.
    /// </summary>
    internal static bool IsMetadataIntrinsic(string name)
    {
        return name is "nameof" or "orderof" or "typeof" or "typeidof" or "valueof" or "placeof"
            or "sizeof" or "visibilityof";
    }

    /// <summary>
    /// Analyzes a comptime metadata intrinsic call (`nameof(m)`, `sizeof(T)`, …). The argument is an
    /// expand handle or a type name; either way its concrete value only exists at monomorphization, so
    /// the intrinsic types leniently (like the old dot-projection) and the real fold runs at instantiation.
    /// </summary>
    private TypeSymbol AnalyzeMetadataIntrinsic(string name)
    {
        return name switch
        {
            "nameof" => _registry.LookupType(name: "Text") ?? ErrorTypeInfo.Instance,
            "orderof" or "typeidof" or "placeof" or "sizeof" =>
                _registry.LookupType(name: "U64") ?? ErrorTypeInfo.Instance,
            // `visibilityof(m)` yields the member's OPEN/POSTED/SECRET visibility as the existing
            // `Visibility` choice (BuilderQuery), narrowed by `is SECRET` etc. at the use site. Resolve
            // through imports (Visibility lives in BuilderQuery, not Core) so the matched value is the
            // concrete choice type — that lets the `is SECRET` pattern bind the case directly instead of
            // resolving `SECRET` as a bare type via the cross-module short-name scan.
            "visibilityof" => LookupTypeWithImports(name: "Visibility") ?? ErrorTypeInfo.Instance,
            "valueof" => _registry.LookupType(name: "S32") ?? ErrorTypeInfo.Instance,
            // `typeof(m)` in expression position is a comptime typewise receiver (deferred, like the
            // old `${m.type}`): the real type only exists post-monomorph.
            _ => ErrorTypeInfo.Instance
        };
    }

    private TypeSymbol AnalyzeCallExpression(CallExpression call, TypeSymbol? expectedType = null)
    {
        TypeSymbol result = AnalyzeCallExpressionCore(call: call, expectedType: expectedType);
        EnforceSuflaeUnsafeCall(resolved: call.ResolvedRoutine, location: call.Location);
        return result;
    }

    /// <summary>
    /// Suflae unsafe-call gate: whatever overload a call finally resolved to, a <c>dangerous</c> routine is
    /// not part of Suflae's safe surface. Entity wrappers already hide their dangerous members (the
    /// auto-forwarder denylist), but dangerous FREE routines auto-preluded from Core (<c>hollow[T]()</c>,
    /// <c>roamed_from_addr</c>, …) and any dangerous method on a SHARED record slip past wrapping — this is
    /// the one unified choke point (called from both the plain- and generic-call analyzers) that closes them.
    /// Runs only for user Suflae source: stdlib <c>.rf</c> bodies analyze in RF mode, and SF stdlib wrappers
    /// are exempt (a forwarder may still chain a builder-internal). Suflae has no <c>danger</c> block, so
    /// there is no in-Suflae opt-in — the surface is simply unavailable.
    /// </summary>
    private void EnforceSuflaeUnsafeCall(RoutineInfo? resolved, SourceLocation location)
    {
        if (_registry.Language == Language.Suflae && !IsStdlibFile(filePath: _currentFilePath) &&
            !InDangerBlock && resolved is { IsDangerous: true } dangerousRoutine)
        {
            ReportError(code: SemanticDiagnosticCode.FeatureNotInSuflae,
                message:
                $"'{dangerousRoutine.Name}' is unsafe (dangerous) surface and is not available in " +
                "Suflae — Suflae hides memory-unsafe operations.",
                location: location);
        }
    }

    /// <summary>
    /// Packs the trailing positional arguments of a call to a variadic routine into a single
    /// <c>Array[T, K]</c> literal, in place. A variadic parameter <c>nums...: T</c> was desugared to a
    /// const-generic <c>Array[T, __VarargN]</c> (via the variadic-param desugaring pass); wrapping the K
    /// call arguments into an <c>Array[T, K]</c> makes the argument count match the single parameter, so
    /// the normal const-generic inference below binds <c>__VarargN = K</c> and one specialized body is
    /// monomorphized per arity. No-op when the routine is not variadic or the args are already packed.
    /// Trailing NAMED arguments (e.g. <c>sep:</c>/<c>end:</c>) stay after the packed Array.
    /// </summary>
    private void PackVariadicCallArgs(CallExpression call, RoutineInfo routine)
    {
        PackVariadicCallArgs(arguments: call.Arguments, routine: routine, location: call.Location);
    }

    /// <summary>Discarding wrapper for call sites that don't need the "did it pack?" result.</summary>
    private void PackVariadicCallArgs(List<Expression> arguments, RoutineInfo routine,
        SourceLocation location)
    {
        TryPackVariadicCallArgs(arguments: arguments, routine: routine, location: location);
    }

    /// <summary>
    /// Argument-list form used by every call shape (plain call, member call, generic member call). Packs
    /// the trailing positional args into a single Array[T, K] literal, mutating <paramref name="arguments"/>
    /// in place. Returns true when a pack was performed. Returns FALSE when there are no positional args to
    /// pack (e.g. a memberwise field-init `BitList(data:.., count:..)` on a type that also has a variadic
    /// `create` — packing an empty group there would prepend a bogus `Array[T, 0]` and corrupt the call),
    /// so callers can skip variadic monomorphization.
    /// </summary>
    private bool TryPackVariadicCallArgs(List<Expression> arguments, RoutineInfo routine,
        SourceLocation location)
    {
        if (!routine.IsVariadic)
        {
            return false;
        }

        int variadicIndex = -1;
        for (int i = 0; i < routine.Parameters.Count; i++)
        {
            if (routine.Parameters[index: i].IsVariadicParam)
            {
                variadicIndex = i;
                break;
            }
        }

        // Element type T comes from the desugared Array[T, __VarargN] parameter (first type arg).
        if (variadicIndex < 0 || routine.Parameters[index: variadicIndex].Type is not
                { IsGenericResolution: true, TypeArguments: [var elemType, ..] })
        {
            return false;
        }

        var leading = new List<Expression>();
        var group = new List<Expression>();
        var trailingNamed = new List<Expression>();
        for (int i = 0; i < arguments.Count; i++)
        {
            Expression a = arguments[index: i];
            if (a is NamedArgumentExpression)
            {
                trailingNamed.Add(item: a);
            }
            else if (i < variadicIndex)
            {
                leading.Add(item: a);
            }
            else
            {
                group.Add(item: a);
            }
        }

        // Already packed: a single positional list literal occupies the variadic slot.
        if (group is [ListLiteralExpression])
        {
            return false;
        }

        // No positional args to pack: this call is NOT using the variadic form (e.g. a memberwise
        // field-init `T(data:.., count:..)` on a type that also has a variadic `create`). Packing an
        // empty `Array[T, 0]` here would prepend a bogus literal and corrupt the field-init call.
        if (group.Count == 0)
        {
            return false;
        }

        TypeSymbol? arrayDef = _registry.LookupType(name: "Array");
        if (arrayDef == null)
        {
            return false;
        }

        int arity = group.Count;
        var arityConst = new ConstGenericValueTypeInfo(literalText: arity.ToString(),
            value: arity,
            explicitTypeName: "U64");
        TypeSymbol arrayType = _registry.GetOrCreateResolution(genericDef: arrayDef,
            typeArguments: [elemType, arityConst]);

        var arrayLit =
            new ListLiteralExpression(Elements: group, ElementType: null, Location: location);
        AnalyzeExpression(expression: arrayLit, expectedType: arrayType);

        arguments.Clear();
        arguments.AddRange(collection: leading);
        arguments.Add(item: arrayLit);
        arguments.AddRange(collection: trailingNamed);
        return true;
    }

    /// <summary>
    /// Resolves a zero-arg construction <c>Type()</c> on a non-generic, non-variant/protocol
    /// <paramref name="zeroArgType"/>: binds the no-arg <c>create</c> (routing through a user-declared
    /// one for its side-effects; leaving a synthesized memberwise creator to inline construction) and
    /// returns the BARE type (so the SF entity-lowering pass wraps the construction in <c>.roam()</c>).
    /// </summary>
    private TypeSymbol AnalyzeZeroArgConstruction(CallExpression call, TypeSymbol zeroArgType)
    {
        RoutineInfo? zeroCreate = _registry.LookupCreatorOverload(type: zeroArgType,
            argTypes: new List<TypeInfo>());
        call.ConstructedType = zeroArgType;
        call.LoweringKind = ClassifyConstruction(type: zeroArgType,
            isCollectionLiteral: call.IsCollectionLiteral);
        // A user-declared (non-synthesized) `create` has a real body/side-effects — route the
        // call through it (this ALSO seeds it for reachability). A synthesized memberwise creator
        // is left to inline construction.
        if (zeroCreate is { IsSynthesized: false })
        {
            call.ResolvedRoutine = zeroCreate;
        }

        call.IsInFlight = zeroCreate?.IsInFlightReturn ?? false;
        // Return the BARE entity type (NOT create's declared return, which for a Suflae entity is
        // `Roamed[E]`): the SF entity-lowering pass keys on `ResolvedType is EntityTypeInfo` to
        // wrap a construction in `.roam()`, so a Roamed return type would divert it to the
        // arg-carrying-create path and drop the wrap. For an RF entity the two coincide.
        return zeroArgType;
    }

    private TypeSymbol AnalyzeCallExpressionCore(CallExpression call,
        TypeSymbol? expectedType = null)
    {
        // Comptime `expand` gate: a member-routine call on a comptime member value (me.$nameof(m).cmp()/
        // .hash()/…) is a wired op — a GATED one (cmp/hash/…) needs the enclosing template's `needs P
        // everywhere` guarantee; a universal one (represent/serialize) passes freely.
        if (call.Callee is MemberExpression
            {
                Object: SpliceMemberExpression, MemberName: var comptimeOp
            })
        {
            EnforceComptimeMemberGate(wiredName: comptimeOp, location: call.Location);
        }

        if (TryAnalyzeMetadataIntrinsicCall(call: call) is { } intrinsicResult)
        {
            return intrinsicResult;
        }

        TypeSymbol? resolved = call.Callee switch
        {
            IdentifierExpression id => AnalyzeIdentifierCall(call: call,
                id: id,
                expectedType: expectedType),
            MemberExpression member => AnalyzeMemberCall(call: call, member: member),
            _ => null
        };
        if (resolved != null)
        {
            return resolved;
        }

        return AnalyzeDynamicCallExpression(call: call);
    }

    /// <summary>
    /// Intercepts comptime metadata intrinsic calls (`nameof(m)` / `sizeof(T)` / …) — a call
    /// whose callee is one of the reserved `*of` names with a single argument. These have no
    /// RoutineInfo; they fold off the expand-unroll context at monomorphization.
    /// Returns the intrinsic's result type when matched, or null when the call is not an intrinsic.
    /// </summary>
    private TypeSymbol? TryAnalyzeMetadataIntrinsicCall(CallExpression call)
    {
        if (call.Callee is not IdentifierExpression { Name: var ofName } ||
            !IsMetadataIntrinsic(name: ofName) || call.Arguments is not { Count: 1 })
        {
            return null;
        }

        // BuilderExpansion gate: the reflection intrinsics live in the BuilderExpansion module
        // (siblings of the `expand` sources); using one requires the opt-in import.
        if (!_importedModules.Contains(item: "BuilderExpansion"))
        {
            ReportError(code: SemanticDiagnosticCode.BuilderExpansionImportRequired,
                message: $"'{ofName}(...)' requires 'import BuilderExpansion'.",
                location: call.Location);
        }

        return AnalyzeMetadataIntrinsic(name: ofName);
    }

    /// <summary>
    /// Handles a dynamic (non-identifier, non-member) call expression — analyzes the callee and
    /// arguments, sets DynamicCall lowering kind, and returns the result type (unwrapping
    /// RoutineTypeInfo to its return type).
    /// </summary>
    private TypeSymbol AnalyzeDynamicCallExpression(CallExpression call)
    {
        TypeSymbol calleeType = AnalyzeExpression(expression: call.Callee);

        foreach (Expression arg in call.Arguments)
        {
            AnalyzeExpression(expression: arg);
        }

        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        call.LoweringKind = CallLoweringKind.DynamicCall;

        if (calleeType is RoutineTypeInfo routineType)
        {
            return routineType.ReturnType ??
                   _registry.LookupType(name: NoneTypeName) ?? ErrorTypeInfo.Instance;
        }

        return calleeType;
    }

    private TypeSymbol? AnalyzeIdentifierCall(CallExpression call, IdentifierExpression id,
        TypeSymbol? expectedType)
    {
        // The failable `!` marker is a structured flag on the CallExpression, not part of
        // the identifier string (which is bare).
        bool isFailableCall = call.IsFailable;
        string callName = id.Name;

        TypeSymbol? callableType = ResolveCallableType(call: call, id: id, callName: callName);

        RoutineInfo? routine = ResolveInitialFreeRoutine(call: call,
            callName: callName,
            isFailableCall: isFailableCall);

        RefineLocalFreeRoutine(call: call,
            callName: callName,
            expectedType: expectedType,
            routine: ref routine);

        // Zero-arg construction `Type()`: the arg-bearing constructor block below is gated on
        // `Arguments.Count > 0`, and the free-routine path requires `routine != null`. A no-arg
        // `Type()` therefore had NO handler here and relied on `LookupRoutine("Type")` (above)
        // finding the routed `create` via the cross-module short-name scan; scan-off that misses
        // and the call reaches codegen unresolved, emitting a bare `@Type()` stub → linker
        // "undefined value @Type". Resolve the no-arg `create` on the import-resolved callableType
        // directly (scan-independent). Variant/protocol construction and generic-def bare `T()`
        // have their own paths, so exclude them.
        if (callableType is { IsGenericDefinition: false } zeroArgType && routine == null &&
            call.Arguments.Count == 0 && zeroArgType is not (VariantTypeInfo or ProtocolTypeInfo))
        {
            return AnalyzeZeroArgConstruction(call: call, zeroArgType: zeroArgType);
        }

        if (AnalyzeArgumentConstruction(call: call,
                isFailableCall: isFailableCall,
                callableType: ref callableType) is { } resultAnalyzeArgumentConstruction)
        {
            return resultAnalyzeArgumentConstruction;
        }

        if (AnalyzeResolvedFreeRoutine(call: call, id: id, routine: routine) is
            { } resultAnalyzeResolvedFreeRoutine)
        {
            return resultAnalyzeResolvedFreeRoutine;
        }

        // Could be a type creator
        if (AnalyzeNamedTypeConstruction(call: call, id: id, type: callableType) is
            { } resultAnalyzeNamedTypeConstruction)
        {
            return resultAnalyzeNamedTypeConstruction;
        }

        return AnalyzeImportFallbackCall(call: call, id: id, callName: callName,
            expectedType: expectedType);
    }

    /// <summary>
    /// Resolves the callable type for a constructor call: performs realm-blind lookup,
    /// applies explicit realm override, and reports ambiguous multi-module construction.
    /// Also resolves explicit constructor type arguments (`List[T](...)`).
    /// </summary>
    private TypeSymbol? ResolveCallableType(CallExpression call, IdentifierExpression id,
        string callName)
    {
        // Look up the type with `!` stripped — `U32!(level)` is a failable type
        // constructor call routing to `U32.create!(from: U64)`. Without stripping,
        // `LookupTypeWithImports("U32!")` returns null and the call falls through to
        // non-creator paths, eventually mis-picking a non-failable overload by name.
        TypeSymbol? callableType = LookupTypeWithImports(name: callName);
        // Honor an explicit `RF::`/`SF::` realm on a constructor call (`RF::Core.List[T]()`): the
        // lookup above is realm-blind (prefers the file's resolution realm), so inside an SF file
        // `RF::Core.List[T]()` would resolve to the SF-realm list and the SF wrapper's constructor
        // `return List[T](inner: RF::Core.List[T]())` would self-recurse. Swap to the qualified realm.
        if (id.Realm is { } calleeRealm && callableType is TypeInfo calleeDef &&
            calleeDef.Realm != calleeRealm &&
            _registry.ReResolveInRealm(type: calleeDef, realm: calleeRealm) is { } realmDef)
        {
            callableType = realmDef;
        }

        // Module-scoped ambiguity for a bare construction `T(...)`: T declared in 2+ imported
        // modules (own module not shadowing) is ambiguous. Mirrors the type-annotation check in
        // TypeResolver.ResolveTypeCore; still constructs (first-match) so no null cascade.
        if (callableType != null)
        {
            List<string> ambigCtor = _typeResolver.ImportedModulesDeclaring(name: callName);
            if (ambigCtor.Count >= 2)
            {
                ReportError(code: SemanticDiagnosticCode.AmbiguousTypeReference,
                    message: $"Type '{callName}' is declared in multiple imported modules " +
                             $"({string.Join(separator: ", ", values: ambigCtor)}) — the current module " +
                             "declares no such type to shadow it. Qualify the reference or restructure imports.",
                    location: call.Location);
            }
        }

        ResolveExplicitConstructorTypeArguments(call: call, callableType: ref callableType);
        return callableType;
    }

    /// <summary>
    /// Performs initial free-routine lookup: module-local by name, module-prefixed fallback,
    /// implicit-failable retry, on-demand variant synthesis, and variadic arg packing.
    /// </summary>
    private RoutineInfo? ResolveInitialFreeRoutine(CallExpression call, string callName,
        bool isFailableCall)
    {
        // (A direct free call to a wired routine is unreachable now: `$` is a separate Dollar
        // token that the parser consumes structurally — a free-call `callName` is always bare and
        // free routines are never wired member routines. Wired-member misuse is caught on the
        // member-call path below via IsOperatorWired / the iter·access·control guard.)

        // Display-routine desugaring (phase 1): `show(x)` / `alert(x)` where x is a
        // copy-restricted wrapper becomes `show(x.represent())` / `alert(x.diagnose())`
        // BEFORE overload resolution. The rewrite turns the call into a Text-typed
        // argument, so overload resolution picks the `show(value: Accessing[Text])`
        // / `alert(value: Accessing[Text])` overload instead of the generic-T variant
        // that would either trigger S420 (implicit copy of the wrapper) or — worse —
        // bind to the wrong overload and emit a garbage call at runtime.
        if (_registry.Language == Language.RazorForge)
        {
            RewriteDisplayRoutineWrapperArgs(callName: callName, arguments: call.Arguments);
        }

        RoutineInfo? routine = _registry.LookupRoutine(fullName: callName,
            isFailable: isFailableCall);
        // Try current module prefix (e.g., "infinite_loop" -> "HelloWorld.infinite_loop")
        if (routine == null && _currentModuleName != null && !callName.Contains(value: '.'))
        {
            routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{callName}",
                isFailable: isFailableCall);
        }

        // Call-site `!` is OPTIONAL: a bare `foo()` call may bind a failable routine `foo!`
        // when only the failable form exists. The name is BARE and failability is a
        // structural flag, not part of the name — so a non-`!` call to a `!`-only routine
        // resolves to the failable form and is crash-on-failure (the failability tracking
        // below keys off routine.IsFailable, and the UnhandledCrashableCall warning is
        // suppressed). Retry with isFailable: true when the bare lookup missed.
        LookupImplicitFailableRoutine(isFailableCall: isFailableCall,
            callName: callName,
            routine: ref routine);

        // On-demand failable-variant synthesis: a free `try_`/`check_`/`lookup_` call whose
        // variant isn't registered yet is synthesized from its base failable routine (the
        // on-demand replacement for eager pre-registration of every failable's variants).
        if (routine == null)
        {
            routine = TrySynthesizeFreeVariantOnDemand(callName: callName);
        }

        // Variadic call: pack the K trailing args into an Array[T, K] literal so the arg count
        // matches the desugared single Array parameter and const-generic inference binds the
        // arity (must run before the generic branches below).
        if (routine != null)
        {
            PackVariadicCallArgs(call: call, routine: routine);
        }

        return routine;
    }

    /// <summary>
    /// Refines the locally-resolved free routine via explicit type-arg binding, generic arity
    /// selection, implicit inference, arity rebinding, arg-type overload resolution, and
    /// variadic fallback — all on the module-local candidate.
    /// </summary>
    private void RefineLocalFreeRoutine(CallExpression call, string callName,
        TypeSymbol? expectedType, ref RoutineInfo? routine)
    {
        // Explicit type arguments on a generic routine call — monomorphize immediately so
        // that ResolvedType is concrete (e.g., signed_div[S32](...) -> ReturnType = S32, not T).
        BindExplicitFreeTypeArguments(call: call, routine: ref routine);

        // Generic overload disambiguation by arity: several generic free routines can share one
        // name (e.g. `zip(a,b)` / `zip(a,b,c)` / `zip(a,b,c,d)`), but the first-wins name lookup
        // returns a single instance. When that instance is a generic definition whose parameter
        // count doesn't match the call, re-resolve to the same-name generic overload with the
        // matching arity so inference below runs against the right template.
        SelectGenericFreeOverloadByArity(call: call, callName: callName, routine: ref routine);

        // Implicit type-argument inference for a generic routine call without explicit `[...]`.
        // Without this, callers like `set_byte_at(arr, 0, b)` keep the generic definition and
        // its return type stays `Array[Byte, N]`, breaking assignment/conversion checks.
        InferFreeRoutineArguments(call: call, expectedType: expectedType, routine: ref routine);

        // Overload resolution: re-resolve when the initial lookup (first-wins by base name)
        // returns a routine with a different arity than the call site. This handles the case
        // where a zero-arg overload was registered first but the call has arguments, or where
        // a same-first-param overload was registered first but the call has different arity.
        RebindFreeOverloadByArity(call: call, callName: callName, routine: ref routine);

        // Overload resolution: if the found routine is non-generic and any
        // positional argument doesn't match the bound routine's parameter type,
        // try a specific or generic overload (e.g., show[T] or a ByteSize overload
        // when the U64 overload was first-bound).
        ResolveFreeOverloadByArgumentTypes(call: call, callName: callName, routine: ref routine);

        // Variadic fallback: if resolved routine is non-variadic but has too many args,
        // try a variadic generic overload (e.g., show("a","b","c") -> show[T](values...: T))
        RecoverMissingFreeOverload(call: call, callName: callName, routine: ref routine);
    }

    /// <summary>
    /// Import-fallback call path: looks up a module-prefixed free routine (e.g. `Core.normalize_duration`),
    /// refines it via arity/inference/overload resolution, and delegates to
    /// <see cref="AnalyzeFallbackFreeRoutine"/>.
    /// </summary>
    private TypeSymbol? AnalyzeImportFallbackCall(CallExpression call, IdentifierExpression id,
        string callName, TypeSymbol? expectedType)
    {
        // Try module-prefixed routine lookup (e.g., Core.normalize_duration)
        // This is done after type creator check to avoid shadowing type creators
        // with identically-named convenience functions (e.g., "routine U32(from: U8)")
        RoutineInfo? routine = LookupRoutineWithImports(name: callName);

        // Generic overload disambiguation by arity for an import-resolved routine: several
        // generic free routines can share one name (e.g. `zip(a,b)` / `zip(a,b,c)` /
        // `zip(a,b,c,d)` in IterTools). The import lookup returns a single arbitrary-arity
        // instance; when its parameter count doesn't match the call, re-resolve to the
        // same-name generic overload with the matching arity so inference below runs against
        // the right template.
        SelectFallbackGenericOverload(call: call, callName: callName, routine: ref routine);

        // Import-resolved generic routine with matching arity but no explicit type args:
        // infer type arguments so the resolved routine is concrete (mirrors the
        // module-local inference path above). Without this, an import-resolved `zip(a,b)`
        // keeps its generic definition and RF-S161 fires downstream.
        InferFallbackFreeTypeArguments(call: call,
            expectedType: expectedType,
            routine: ref routine);

        // Overload resolution for import-resolved routines (e.g., show[T] from IO/Console)
        ResolveFallbackFreeOverload(call: call, callName: callName, routine: ref routine);

        // Variadic fallback for import-resolved routines
        RecoverFallbackFreeOverload(call: call, callName: callName, routine: ref routine);

        return AnalyzeFallbackFreeRoutine(call: call, id: id, routine: routine);
    }

    private TypeSymbol? AnalyzeMemberCall(CallExpression call, MemberExpression member)
    {
        // Module-qualified routine call: `Module.routine(...)`. When the callee's object is a
        // bare identifier that names an imported module — and is neither a value nor a type in
        // scope — resolve it to a module-level routine (they register under the `Module.name`
        // key). The identifier may be a full single-segment module name (`ModuleA`) OR the
        // LEAF of a hierarchical module path (`JsonEncodeApi` for `Tests/Stdlib/JsonEncodeApi`),
        // since a `/`-path can't be written in expression position (`/` is division). This MUST
        // run before AnalyzeExpression(member.Object), which would otherwise report the module
        // name as an unknown identifier (RF-S007).
        if (AnalyzeImportedModuleCall(call: call, member: member) is
            { } resultAnalyzeImportedModuleCall)
        {
            return resultAnalyzeImportedModuleCall;
        }

        TypeSymbol objectType = AnalyzeExpression(expression: member.Object);

        // Comptime expand-handle capability probe: `m.obeying(SomeProtocol)` -> Bool, folded at
        // monomorphization to the member type's conformance. The argument is a PROTOCOL name
        // (a type/protocol identifier, not a runtime value), so short-circuit before normal
        // argument analysis. The handle types leniently so an expand body typechecks before
        // monomorphization; any other call on it is a clear mistake.
        if (AnalyzeComptimeHandleCall(call: call, member: member, objectType: objectType) is
            { } resultAnalyzeComptimeHandleCall)
        {
            return resultAnalyzeComptimeHandleCall;
        }

        // iter / refer / control are dunder-private to their protocols — only the
        // corresponding lowering passes may emit them (for-loop → iter; argument
        // coercion → refer/control). Forbidding user calls prevents storing the
        // result in a variable, which would let a borrow / iterator outlive its source.
        // Stdlib is exempt — its iterator implementations and wrapper bodies chain these
        // dunders directly (e.g., `me.source.iter()`, wrapper `refer` forwarders).
        if (ValidateDirectWiredMemberCall(call: call, member: member) is
            { } resultValidateDirectWiredMemberCall)
        {
            return resultValidateDirectWiredMemberCall;
        }

        if (ValidateMemberCallOperandType(call: call, member: member,
                objectType: objectType) is { } typeError)
        {
            return typeError;
        }

        bool isFailableMemberRoutineCall = member.IsFailable;
        string callLookupName = member.MemberName;
        TypeSymbol dispatchType = objectType;

        ResolveMemberRoutineCandidate(call: call,
            member: member,
            objectType: objectType,
            isFailableMemberRoutineCall: isFailableMemberRoutineCall,
            dispatchType: ref dispatchType,
            memberRoutine: out RoutineInfo? memberRoutine,
            ambiguousSeed: out bool ambiguousSeed);

        SelectMemberOverloadByArgumentTypes(call: call,
            callLookupName: callLookupName,
            dispatchType: dispatchType,
            memberRoutine: ref memberRoutine,
            ambiguousSeed: ambiguousSeed);

        if (AnalyzeResolvedMemberCall(call: call,
                member: member,
                objectType: objectType,
                dispatchType: dispatchType,
                memberRoutine: memberRoutine) is { } resultAnalyzeResolvedMemberCall)
        {
            return resultAnalyzeResolvedMemberCall;
        }

        if (AnalyzeMemberChainConversion(call: call,
                member: member,
                objectType: objectType) is { } resultAnalyzeMemberChainConversion)
        {
            return resultAnalyzeMemberChainConversion;
        }

        // Unresolved member call on a concrete field-bearing receiver. `.field` (member
        // variable access) and `.field()` (routine call) are DISTINCT forms that may
        // coexist on the same name; the parentheses pick the routine. So a `.name()` that
        // resolved to no routine is an error — EXCEPT the genuine dynamic call through a
        // Routine-typed field (a `ptr` closure, e.g. `me.predicate(item)`), which is
        // dispatched indirectly and must fall through to the dynamic-call path below.
        // Without this guard such calls silently became DynamicCall and only "worked" via
        // a codegen fallback that read the field or re-resolved a failable variant — the
        // intent-rediscovery task #23 removes. Restricted to Entity/Record receivers so
        // generic-parameter / protocol / wrapper receivers keep their deferred resolution.
        return AnalyzeUnresolvedMemberFieldCall(call: call,
            member: member,
            objectType: objectType,
            isFailableMemberRoutineCall: isFailableMemberRoutineCall,
            callLookupName: callLookupName);
    }

    /// <summary>
    /// Early-exit type guard for member calls: rejects arithmetic operators on Choice and Flags
    /// types, and pre-checks nested grasping (detect before routine resolution since modify()
    /// may not resolve by concrete type name).
    /// Returns <see cref="ErrorTypeInfo.Instance"/> on a hard error, null when the call may proceed.
    /// </summary>
    private ErrorTypeInfo? ValidateMemberCallOperandType(CallExpression call, MemberExpression member,
        TypeSymbol objectType)
    {
        // Choice types cannot use any operator wired memberRoutines
        if (objectType is ChoiceTypeInfo && IsOperatorWired(name: member.MemberName))
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnChoiceType,
                message:
                $"Operator '{member.MemberName}' cannot be used with choice type '{objectType.Name}'. " +
                "Choice types do not support operators. Use 'is' for case matching and regular member routines for additional behavior.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        // #134/#135: Flags types cannot use any operator wired memberRoutines
        if (objectType is FlagsTypeInfo && IsOperatorWired(name: member.MemberName))
        {
            ReportError(code: SemanticDiagnosticCode.ArithmeticOnFlagsType,
                message:
                $"Operator '{member.MemberName}' cannot be used with flags type '{objectType.Name}'. " +
                "Use 'but' to remove flags and 'is'/'isnot' to test flags.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        // #137: Nested grasping detection — checked before memberRoutine resolution
        // since modify() is generic extension T.modify() that may not resolve by concrete type name
        if (member.MemberName == ModifyMemberRoutineName &&
            IsNestedModifying(source: member.Object))
        {
            ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                message: "Cannot modify a member of an already-modified object. " +
                         "Modify the parent entity directly instead.",
                location: call.Location);
        }

        return null;
    }

    /// <summary>
    /// Performs the full member-routine candidate resolution pipeline: initial lookup by name +
    /// failability, implicit-failable retry, owner-constraint validation, transparent wrapper
    /// forwarding, Roamed/generic-param receiver forwarding, ambiguous-seed disambiguation, and
    /// named-argument overload pinning. Outputs the resolved <paramref name="memberRoutine"/>,
    /// updated <paramref name="dispatchType"/>, and whether an <paramref name="ambiguousSeed"/>
    /// forces a full arg-type retry.
    /// </summary>
    private void ResolveMemberRoutineCandidate(CallExpression call, MemberExpression member,
        TypeSymbol objectType, bool isFailableMemberRoutineCall,
        ref TypeSymbol dispatchType, out RoutineInfo? memberRoutine, out bool ambiguousSeed)
    {
        string callLookupName = member.MemberName;
        memberRoutine = _registry.LookupMemberRoutine(type: dispatchType,
            memberRoutineName: callLookupName,
            isFailable: isFailableMemberRoutineCall);

        // Call-site `!` is OPTIONAL: a bare (`x.retrieve()`) call may bind a failable
        // routine when only the failable form exists. The name is BARE and failability is
        // a structural flag, not part of the name — so a non-`!` call to a `!`-only routine
        // resolves to the failable form and is crash-on-failure (the UnhandledCrashableCall
        // warning is suppressed). Retry with isFailable: true when the bare lookup missed.
        if (memberRoutine == null && !isFailableMemberRoutineCall)
        {
            memberRoutine = _registry.LookupMemberRoutine(type: dispatchType,
                memberRoutineName: callLookupName,
                isFailable: true);
        }

        // Clean-diagnostic gate: a resolved member routine whose owner-level `needs param obeys P`
        // constraint is unmet by the concrete receiver (e.g. `List[Widget].duplicate()` with
        // `needs T obeys Copyable`, Widget not Copyable) is RF-S150 here, not an over-prune crash.
        if (memberRoutine != null)
        {
            ValidateMemberOwnerConstraints(memberRoutine: memberRoutine,
                ownerType: dispatchType,
                location: member.Location);
        }

        // Phase D: Transparent wrapper forwarding — if the memberRoutine isn't found directly on
        // the wrapper, synthesize a forwarder that delegates to the inner type's memberRoutine
        // via `Hijacked[T](me).extract().MemberRoutine(...)`.
        if (memberRoutine == null && IsWrapperType(type: dispatchType))
        {
            memberRoutine = TrySynthesizeWrapperForwarder(wrapperType: dispatchType,
                memberRoutineName: callLookupName,
                isFailable: isFailableMemberRoutineCall);
        }

        ResolveTransparentMemberRoutine(objectType: objectType,
            isFailableMemberRoutineCall: isFailableMemberRoutineCall,
            callLookupName: callLookupName,
            dispatchType: ref dispatchType,
            memberRoutine: ref memberRoutine);

        // Generic-parameter receiver: resolve via Obeys constraints from the current
        // routine and its owner type. e.g. `key.hash()` where `K obeys Hashable`
        // dispatches through Hashable's protocol memberRoutine.
        ResolveConstrainedMemberRoutine(isFailableMemberRoutineCall: isFailableMemberRoutineCall,
            callLookupName: callLookupName,
            dispatchType: dispatchType,
            memberRoutine: ref memberRoutine);

        // Ambiguous multi-overload seed. A routine's identity is (name, parameter-types), so once
        // >1 same-name overload is registered the name-only lookups above returned null BY DESIGN
        // (no first-wins — that was the S8-vs-S64 mis-pick bug class). Pin the unique overload from
        // the call shape: filter the candidates by arity + supplied named-argument names, preferring
        // the requested failability. A single survivor IS the answer. Several survivors differ only
        // by parameter TYPE (e.g. `Text.split(Character)` vs `split(Text)`) — scaffold with one so
        // the arguments can be analyzed for their expected param types, and set `ambiguousSeed` to
        // FORCE the argType-driven retry below to pin the unique (name, argTypes) match.
        ambiguousSeed = false;
        SynthesizeMemberVariantOnDemand(call: call,
            isFailableMemberRoutineCall: isFailableMemberRoutineCall,
            callLookupName: callLookupName,
            dispatchType: dispatchType,
            memberRoutine: ref memberRoutine,
            ambiguousSeed: ref ambiguousSeed);

        // Named-argument overload disambiguation. LookupMemberRoutine returns one overload by name.
        // When the call supplies a named argument that the initial overload lacks — e.g.
        // get_count with a predicate argument resolving first to the zero-arg get_count — prefer
        // the overload whose parameters cover every named argument. This MUST run before the
        // arguments are analyzed below: otherwise a callback argument is analyzed against a
        // missing/wrong parameter type, collapses to an error type, and the later type-based
        // overload retry can no longer recover the right memberRoutine.
        TryDisambiguateMemberOverloadByNamedArgs(call: call,
            callLookupName: callLookupName,
            dispatchType: dispatchType,
            memberRoutine: ref memberRoutine);
    }

    /// <summary>
    /// When the call supplies named arguments that the current candidate doesn't cover, searches
    /// for an overload whose parameter list covers all provided named-argument names. Updates
    /// <paramref name="memberRoutine"/> when a unique arity+name match is found.
    /// </summary>
    private void TryDisambiguateMemberOverloadByNamedArgs(CallExpression call,
        string callLookupName, TypeSymbol dispatchType, ref RoutineInfo? memberRoutine)
    {
        if (memberRoutine == null || call.Arguments.Count == 0 ||
            !call.Arguments.Any(predicate: a => a is NamedArgumentExpression))
        {
            return;
        }

        var providedNames = call.Arguments
                                .OfType<NamedArgumentExpression>()
                                .Select(selector: n => n.Name)
                                .ToList();
        // Capture in a local so the lambda below can close over it (ref params can't be captured).
        RoutineInfo currentCandidate = memberRoutine;
        bool covers = providedNames.All(predicate: n =>
            currentCandidate.Parameters.Any(predicate: p => p.Name == n));
        if (covers)
        {
            return;
        }

        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: dispatchType,
            memberRoutineName: callLookupName,
            candidates: candidates);
        RoutineInfo? byName = candidates.FirstOrDefault(predicate: c =>
            c.Parameters.Count == call.Arguments.Count &&
            providedNames.All(predicate: n => c.Parameters.Any(predicate: p => p.Name == n)));
        if (byName != null)
        {
            memberRoutine = byName;
        }
    }

    /// <summary>
    /// Handles the member-chain constructor path: `"42".S32!()` → `S32.create!(from: "42")`.
    /// Strips conversion-variant prefixes (`try_`/`check_`/`lookup_`), infers generic type args
    /// from the variant arm when the target is a generic definition, then delegates to
    /// <see cref="AnalyzeMemberConversion"/>.
    /// Returns a resolved type on success, null when the member name is not a type.
    /// </summary>
    private TypeSymbol? AnalyzeMemberChainConversion(CallExpression call, MemberExpression member,
        TypeInfo objectType)
    {
        // #78: memberRoutine-chain constructor — "42".S32!() -> S32.create!(from: "42").
        // MemberName is bare; failability is carried structurally in member.IsFailable.
        bool isFailable = member.IsFailable;
        string potentialTypeName = member.MemberName;

        // Conversion-VARIANT chain: `.try_S64()`/`.check_S64()`/`.lookup_S64()` on a value resolve to
        // the target type's `try_create`/`check_create`/`lookup_create` (the recoverable forms of the
        // `.S64!()` -> `S64.create!(from:)` conversion). This is what a `try_`/`check_`/`lookup_`
        // variant body's re-analysis of a converted `!` conversion call must resolve to — else the
        // bare "try_create" member name re-resolves against the RECEIVER type (e.g. F64.try_create)
        // instead of the conversion TARGET (S64.try_create). Strip the prefix ONLY when the remainder
        // is an actual type name, so a real `try_foo` member routine is untouched.
        string creatorName = RoutineInfo.CreatorName;
        foreach ((string prefix, string cname) in ConversionVariantCreators)
        {
            if (potentialTypeName.StartsWith(value: prefix,
                    comparisonType: StringComparison.Ordinal) &&
                LookupTypeWithImports(name: potentialTypeName[prefix.Length..]) is not null)
            {
                potentialTypeName = potentialTypeName[prefix.Length..];
                creatorName = cname;
                break;
            }
        }

        TypeSymbol? targetType = LookupTypeWithImports(name: potentialTypeName);

        // Type-arg inference for a memberRoutine-chain variant arm extractor: `sv.Dict!()` where `Dict`
        // is a generic definition and the receiver is a variant — adopt the type arguments of the
        // variant's arm whose generic base is `Dict` (mirrors the construction-form inference), so
        // the concrete `Dict[Text, SerialValue].create!(from: sv)` is found instead of the def's
        // bare `Dict.create()` (which trips RF-S770 with 0 params).
        if (targetType is { IsGenericDefinition: true } && isFailable &&
            objectType is VariantTypeInfo mcVariant)
        {
            string mcBase = targetType.Name;
            VariantMemberInfo? mcArm = mcVariant.Members.FirstOrDefault(predicate: m =>
                !m.IsNone && m.Type is not null && (m.Type switch
                {
                    EntityTypeInfo e => e.GenericDefinition?.Name,
                    RecordTypeInfo r => r.GenericDefinition?.Name,
                    _ => null
                } ?? m.Type.Name) == mcBase);
            if (mcArm?.Type is { } mcArmType)
            {
                targetType = mcArmType;
            }
        }

        return AnalyzeMemberConversion(call: call,
            objectType: objectType,
            potentialTypeName: potentialTypeName,
            creatorName: creatorName,
            targetType: targetType);
    }

    /// <summary>
    /// Resolves a module-qualified routine reference `moduleRef.routineName` to a module-level
    /// routine. <paramref name="moduleRef"/> may be a full single-segment module name (<c>ModuleA</c>)
    /// or the LEAF of a hierarchical imported module path (<c>JsonEncodeApi</c> →
    /// <c>Tests/Stdlib/JsonEncodeApi</c>) — a `/`-path can't be spelled in expression position because
    /// `/` is division. Candidate modules are the imported modules whose full path equals the ref or
    /// whose last `/`-segment equals it. Returns the unique matching routine, or null when none match.
    /// If more than one distinct routine matches (two imported modules sharing a leaf), reports an
    /// ambiguity error, sets <paramref name="ambiguous"/>, and returns null.
    /// </summary>
    private RoutineInfo? ResolveModuleQualifiedRoutine(string moduleRef, string routineName,
        bool isFailable, SourceLocation location, out bool ambiguous)
    {
        ambiguous = false;
        var matches = new List<RoutineInfo>();
        var seenKeys = new HashSet<string>(comparer: StringComparer.Ordinal);
        foreach (string module in _importedModules)
        {
            bool isLeafOrFull = module == moduleRef ||
                                module.LastIndexOf(value: '/') is var slash && slash >= 0 && module
                                   .AsSpan(start: slash + 1)
                                   .SequenceEqual(other: moduleRef);
            if (!isLeafOrFull)
            {
                continue;
            }

            RoutineInfo? candidate = _registry.LookupRoutine(fullName: $"{module}.{routineName}",
                isFailable: isFailable);
            if (candidate is { OwnerType: null } && seenKeys.Add(item: candidate.RegistryKey))
            {
                matches.Add(item: candidate);
            }
        }

        if (matches.Count > 1)
        {
            ambiguous = true;
            ReportError(code: SemanticDiagnosticCode.AmbiguousModuleQualifiedCall,
                message:
                $"'{moduleRef}.{routineName}' is ambiguous — it matches routines in multiple " +
                $"imported modules ({string.Join(separator: ", ", values: matches.Select(selector: m => m.BaseName))}). " +
                "Use a more specific module name.",
                location: location);
            return null;
        }

        return matches.Count == 1
            ? matches[index: 0]
            : null;
    }

    /// <summary>
    /// Finalizes a module-qualified routine call (`ModuleName.routine(...)`): binds the resolved
    /// module-level routine to the call, records failable-call bookkeeping, validates access and
    /// arguments, and returns the call's result type. Mirrors the standalone-routine branch of
    /// <see cref="AnalyzeCallExpression"/>; the callee stays a <c>MemberExpression</c> but the
    /// resolved routine has no owner, which is how codegen and reachability tell the two apart.
    /// </summary>
    private TypeSymbol AnalyzeModuleQualifiedRoutineCall(CallExpression call, RoutineInfo routine)
    {
        call.ResolvedRoutine = routine;
        call.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);

        // Track failable calls for error-handling variant generation (same rule as a bare call).
        if (routine.IsFailable && _currentRoutine != null)
        {
            _currentRoutine.HasFailableCalls = true;
            _currentRoutine.FailableCallees.Add(item: routine);

            if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
                !_currentRoutine.IsSynthesized)
            {
                ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                    message:
                    $"Failable routine '{routine.Name}!' called without error handling. " +
                    UseWhenHint,
                    location: call.Location);
            }
        }

        ValidateRoutineAccess(routine: routine,
            accessLocation: call.Location,
            isCompilerSynthesized: call.IsSynthesizedLowering);
        AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        TypeSymbol returnType = routine.ReturnType ??
                                _registry.LookupType(name: NoneTypeName) ?? ErrorTypeInfo.Instance;
        call.IsInFlight = routine.IsInFlightReturn;

        // A `threaded`/`suspended` module routine yields an `Agent[T]` handle, exactly like a bare
        // async call. The crossing rule (RF-S632) applies to its arguments the same way.
        if (routine.AsyncStatus is AsyncStatus.Threaded or AsyncStatus.Suspended)
        {
            ValidateAsyncRoutineArguments(routine: routine,
                arguments: call.Arguments,
                boundaryKind: routine.AsyncStatus == AsyncStatus.Threaded
                    ? "threaded"
                    : "suspended",
                location: call.Location);
            TypeSymbol? agentDef = _registry.LookupType(name: "Agent");
            return agentDef != null
                ? _registry.GetOrCreateResolution(genericDef: agentDef,
                    typeArguments: [returnType])
                : returnType;
        }

        return returnType;
    }

    private static CallLoweringKind ClassifyStandaloneRoutineCall(RoutineInfo routine)
    {
        if (routine.LlvmIrTemplate != null)
        {
            return CallLoweringKind.LlvmIntrinsic;
        }

        if (routine.IsSynthesized &&
            BuilderInfoProvider.IsBuilderQueryStandalone(name: routine.Name))
        {
            return CallLoweringKind.BuilderIntrinsic;
        }

        return CallLoweringKind.DirectRoutine;
    }

    private static CallLoweringKind ClassifyMemberRoutineCall(RoutineInfo memberRoutine)
    {
        if (memberRoutine.LlvmIrTemplate != null)
        {
            return CallLoweringKind.LlvmIntrinsic;
        }

        if (memberRoutine.IsSynthesized &&
            BuilderInfoProvider.IsBuilderQueryRoutine(name: memberRoutine.Name))
        {
            return CallLoweringKind.BuilderIntrinsic;
        }

        return CallLoweringKind.DirectMemberRoutine;
    }

    private static CallLoweringKind ClassifyConstruction(TypeInfo type, bool isCollectionLiteral)
    {
        if (isCollectionLiteral)
        {
            return CallLoweringKind.CollectionConstruction;
        }

        return type is WrapperTypeInfo
            ? CallLoweringKind.WrapperConstruction
            : CallLoweringKind.TypeConstructor;
    }

    /// <summary>
    /// Validates a called memberRoutine's <c>needs P in [...]</c> (<see cref="ConstraintKind.TypeEquality"/>)
    /// constraints when the constrained parameter is inherited from the receiver type rather than
    /// supplied as an explicit type argument — e.g. <c>Guarded[T, P].amend() needs P in [Exclusive,
    /// MultiRead]</c> called on a <c>Guarded[Counter, ReadOnly]</c>. The standard constraint validator
    /// (<c>TypeResolver.ValidateTypeEqualityConstraint</c>) only fires when a generic type/memberRoutine is
    /// explicitly instantiated, so receiver-bound parameters would otherwise go unchecked.
    /// </summary>
    private void ValidateReceiverInheritedTypeEqualityConstraints(RoutineInfo memberRoutine,
        TypeSymbol receiverType, MemberExpression member, SourceLocation location)
    {
        if (memberRoutine.GenericConstraints is not { Count: > 0 } constraints)
        {
            return;
        }

        // Map the receiver's generic parameter names to its bound type arguments. The names live on
        // the generic definition; the bindings on the resolved instance.
        List<string>? paramNames = receiverType.GenericParameters ??
                                   (receiverType as RecordTypeInfo)?.GenericDefinition
                                 ?.GenericParameters;
        List<TypeInfo>? boundArgs = receiverType.TypeArguments;
        if (paramNames is not { Count: > 0 } || boundArgs is not { Count: > 0 })
        {
            return;
        }

        foreach (GenericConstraintDeclaration constraint in constraints)
        {
            if (constraint.ConstraintType != ConstraintKind.TypeEquality ||
                constraint.ConstraintTypes is not { Count: > 0 } allowed)
            {
                continue;
            }

            int paramIndex = paramNames.IndexOf(item: constraint.ParameterName);
            if (paramIndex < 0 || paramIndex >= boundArgs.Count)
            {
                continue;
            }

            TypeInfo bound = boundArgs[index: paramIndex];
            string boundBase = bound.BareName;
            string boundShort = boundBase.Contains(value: '.')
                ? boundBase[(boundBase.LastIndexOf(value: '.') + 1)..]
                : boundBase;

            bool inSet = allowed.Any(predicate: ce =>
                ce.Name == bound.Name || ce.Name == boundBase || ce.Name == boundShort);
            if (inSet)
            {
                continue;
            }

            string allowedList = string.Join(separator: ", ",
                values: allowed.Select(selector: t => t.Name));
            ReportError(code: SemanticDiagnosticCode.TypeEqualityConstraintViolation,
                message: $"'{member.MemberName}()' is not available on '{receiverType.Name}': " +
                         $"'{boundShort}' is not in [{allowedList}] " +
                         $"(constraint on '{constraint.ParameterName}').",
                location: location);
        }
    }


    private TypeSymbol? AnalyzeArgumentConstruction(CallExpression call, bool isFailableCall,
        ref TypeInfo? callableType)
    {
        if (callableType == null || call.Arguments.Count == 0)
        {
            return null;
        }

        // NOTE: explicit `Type(...)` construction resolves to FIXED-ARITY constructors only —
        // the variadic literal builder is a distinct `from_literal` static routine (never a
        // constructor), so `List(5)` stays the capacity ctor and only `[..]` literals lower to
        // the variadic path. No variadic packing here.

        // Field-init shorthand: `Point(x, y)` == `Point(x: x, y: y)` — pun bare identifiers
        // matching field names into named args before construction binding.
        List<MemberVariableInfo>? punFields = callableType switch
        {
            EntityTypeInfo punEntity => punEntity.MemberVariables,
            RecordTypeInfo punRecord => punRecord.MemberVariables,
            _ => null
        };
        if (punFields != null)
        {
            PunMatchingNamedArgs(arguments: call.Arguments,
                targetNames: punFields.Select(selector: f => f.Name)
                                      .ToList());
        }

        List<TypeSymbol> creatorArgTypes = AnalyzeConstructorArguments(call: call,
            callableType: callableType);

        TryInferVariantArmCreatorType(callableType: ref callableType,
            isFailableCall: isFailableCall,
            creatorArgTypes: creatorArgTypes);

        RoutineInfo? creator = _registry.LookupCreatorOverload(type: callableType,
            argTypes: creatorArgTypes);

        // A creator on a generic DEFINITION (e.g. `Retained[T].create(from: T)`) cannot be
        // arg-matched: a concrete arg (`Node`) never "matches" the unbound param `T`, so the
        // overload matcher returns null. Fall back to the def's creator selected by arity — the
        // type args are inferred from it right below (callableType → the concrete instance).
        creator ??= FallbackGenericDefCreatorByArity(callableType: callableType,
            creatorArgTypes: creatorArgTypes);

        // Field-init recovery: the by-TYPE overload lookup above misses when an argument's type failed
        // to resolve (e.g. a generic-call arg `hijacked_none[U64]()` left un-lowered by the reduced
        // stdlib-validation pipeline yields ErrorType, so no by-type creator matches). A memberwise
        // field-init `Type(field: value, ...)` is identified by NAMES, not arg types — every provided
        // name is a field of the type — so recover the synthesized all-fields creator by matching field
        // names. Keeps field-init construction resolving for EVERY type regardless of whether each arg
        // type resolved; a no-op in the full pipeline, where the by-type lookup already succeeds.
        creator ??= FallbackMemberwiseCreatorByFieldNames(callableType: callableType, call: call);

        TryInferGenericDefinitionCreatorType(callableType: ref callableType,
            creator: creator,
            creatorArgTypes: creatorArgTypes);
        // Re-resolve creator after type inference may have specialized callableType.
        if (creator != null && callableType is { IsGenericDefinition: false })
        {
            creator = _registry.LookupCreatorOverload(type: callableType,
                argTypes: creatorArgTypes) ?? creator;
        }

        if (creator == null || creator.Parameters.Count != creatorArgTypes.Count ||
            creator.Parameters.Any(predicate: p => p.IsVariadicParam))
        {
            return null;
        }

        ValidateCreatorEntityOwnershipArgs(call: call,
            creator: creator,
            creatorArgTypes: creatorArgTypes);

        // An auto-generated variant arm EXTRACTOR `Arm.create!(from: V)` is synthesized
        // but has a real pattern-matching body — it is NOT a memberwise field-init, and
        // for a scalar arm (S32) `ClassifyConstruction` would tag it a value conversion,
        // making codegen bit-reinterpret the variant. Treat it as a normal memberRoutine call and
        // route it through ResolvedRoutine below.
        bool isVariantArmExtractor = creator is
            { IsCreator: true, IsFailable: true, Parameters: [{ Type: VariantTypeInfo }] };

        call.ConstructedType = callableType;
        call.LoweringKind = isVariantArmExtractor
            ? ClassifyMemberRoutineCall(memberRoutine: creator)
            : ClassifyConstruction(type: callableType,
                isCollectionLiteral: call.IsCollectionLiteral);

        // `Type(...)` written *inside* Type's own `create` only needs the
        // inline base case when it resolves back to the SAME `create` we are
        // compiling — that is the genuine self-recursion to break. A call to a
        // *different* overload (e.g. `F128(from: hi)` resolving to
        // `create(from: U64)` inside `create(from: U128)`) is an ordinary
        // conversion and must keep its resolved routine; otherwise codegen is left
        // to guess and, for bit-carrier types like F128, mis-lowers it to a raw
        // `sext`/reinterpret of the integer into the i128 IEEE carrier.
        bool insideOwnCreate = _currentRoutine is { IsCreator: true } currentCreate &&
                               currentCreate.OwnerType != null &&
                               (currentCreate.OwnerType.FullName == callableType.FullName ||
                                currentCreate.OwnerType.Name == callableType.Name) &&
                               ReferenceEquals(objA: creator, objB: currentCreate);

        // Route through a *user-declared* `create` so its body/side-effects run.
        // The synthesized memberwise creator (IsSynthesized) is pure field-init and
        // is left to inline construction in codegen.
        if (!insideOwnCreate && (!creator.IsSynthesized || isVariantArmExtractor))
        {
            call.ResolvedRoutine = creator;
            TrackFailableConstructorCall(creator: creator,
                typeName: callableType.Name,
                location: call.Location);
        }

        call.IsInFlight = creator.IsInFlightReturn;
        return creator.ReturnType ?? callableType;
    }

    /// <summary>
    /// Analyzes each constructor argument, resolving the expected field type for contextual
    /// inference (e.g. literal adaptation, <c>roamed_none()</c> binding). Returns the list of
    /// analyzed argument types to feed into creator overload resolution.
    /// </summary>
    private List<TypeSymbol> AnalyzeConstructorArguments(CallExpression call,
        TypeInfo callableType)
    {
        // Variant construction auto-wraps the argument into the variant (e.g.
        // `Inner(7_s32)` -> Inner's S32 arm, `Inner(none)` -> Inner's None arm), so the
        // argument's contextual type is the variant itself. Without this, a bare `none`
        // argument has no expected type and errors S016.
        TypeSymbol? variantArgContext = callableType is VariantTypeInfo
            ? callableType
            : null;
        var creatorArgTypes = new List<TypeSymbol>(capacity: call.Arguments.Count);
        int creatorPosIdx = 0;
        List<MemberVariableInfo>? ctorMemberVariables = callableType switch
        {
            EntityTypeInfo entityCtor => entityCtor.MemberVariables,
            RecordTypeInfo recordCtor => recordCtor.MemberVariables,
            _ => null
        };
        foreach (Expression arg in call.Arguments)
        {
            TypeSymbol? argExpected = variantArgContext;
            if (argExpected == null && ctorMemberVariables != null)
            {
                argExpected = ResolveConstructorArgExpectedType(arg: arg,
                    posIdx: creatorPosIdx,
                    callableType: callableType,
                    ctorMemberVariables: ctorMemberVariables);
                Expression argVal = arg is NamedArgumentExpression nav
                    ? nav.Value
                    : arg;
                TypeSymbol argAnalyzed =
                    AnalyzeExpression(expression: arg, expectedType: argExpected);
                MemberVariableInfo? field = ResolveCtorField(arg: arg,
                    posIdx: creatorPosIdx,
                    ctorMemberVariables: ctorMemberVariables);
                // Suflae: a NON-NULLABLE entity field (`x: E`) rejects a possibly-none value —
                // literal `none` or an unchecked `E?` read. Only an optional field (`x: E?`)
                // may hold a null Roamed handle.
                if (field is
                    {
                        IsNullable: false,
                        Type: RecordTypeInfo
                        {
                            GenericDefinition.Name: Declaration.RuntimeContract.Roamed
                        }
                    } && IsNullableEntityRead(expr: argVal))
                {
                    ReportNullableIntoNonNull(target: $"field '{field.Name}'",
                        value: argVal,
                        optionalHint: $"{field.Name}: <Type>?");
                }

                creatorArgTypes.Add(item: argAnalyzed);
                creatorPosIdx++;
                continue;
            }

            creatorArgTypes.Add(item: AnalyzeExpression(expression: arg,
                expectedType: argExpected));
            creatorPosIdx++;
        }

        return creatorArgTypes;
    }

    /// <summary>
    /// Resolves the target field for a constructor argument at the given positional index,
    /// used to supply a contextual expected type for literal adaptation.
    /// </summary>
    private static MemberVariableInfo? ResolveCtorField(Expression arg, int posIdx,
        List<MemberVariableInfo> ctorMemberVariables)
    {
        if (arg is NamedArgumentExpression na)
        {
            return ctorMemberVariables.FirstOrDefault(predicate: mv => mv.Name == na.Name);
        }

        return posIdx < ctorMemberVariables.Count
            ? ctorMemberVariables[index: posIdx]
            : null;
    }

    /// <summary>
    /// Resolves the expected type for a constructor argument: first by matching the field name
    /// (or positional slot), then by falling back to the <c>create</c> parameter name when the
    /// user constructor's parameter names differ from the field names, then by substituting
    /// generic type parameters for a concrete instantiation.
    /// </summary>
    private TypeSymbol? ResolveConstructorArgExpectedType(Expression arg, int posIdx,
        TypeInfo callableType, List<MemberVariableInfo> ctorMemberVariables)
    {
        // Field-init constructor `T(field: value)` — the arg's expected type is the target
        // field's type, so a bare integer literal adapts to it (S64/…) instead of stalling
        // at the Suflae `Integer` default (RF escapes this only because its default IS S64).
        // BOTH entity and record targets do inline field-init construction, so both need
        // this — gating on EntityTypeInfo alone left RECORD constructors (`Point(x: 1)`)
        // with a null expected type → `1` stayed Integer → codegen `Integer`-into-`i64` /
        // pruned `Integer.from_literal`. Inferring the field type is the compiler's job.
        MemberVariableInfo? field = ResolveCtorField(arg: arg,
            posIdx: posIdx,
            ctorMemberVariables: ctorMemberVariables);
        TypeSymbol? argExpected = field?.Type;

        // A USER constructor's PARAMETER names may differ from the field names
        // (`routine Pt(v: S64) -> Pt` with a field `x`), so the field-by-name lookup
        // above finds nothing → the bare literal would stall at Suflae's `Integer`
        // default (→ a pruned `Integer.from_literal`). Fall back to the matching
        // `create` param's type so `Pt(v: 3)` coerces `3` to the param's type.
        if (argExpected == null && arg is NamedArgumentExpression ctorArg)
        {
            argExpected = _registry.GetMemberRoutinesForType(type: callableType)
                                   .Where(predicate: m => m.IsCreator)
                                   .SelectMany(selector: m => m.Parameters)
                                   .FirstOrDefault(predicate: p => p.Name == ctorArg.Name)
                                  ?.Type;
        }

        // For a generic record/entity instantiation (Box[S64]), resolve the field's
        // formal param (`T`) to the concrete type arg so the literal conforms to S64,
        // not to the unresolved `T`.
        if (argExpected != null && callableType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            argExpected = SubstituteTypeParameters(type: argExpected, genericType: callableType);
        }

        return argExpected;
    }

    /// <summary>
    /// When the callable type is a generic definition and the sole constructor argument is a
    /// variant, adopts the type arguments of the matching variant arm so that (for example)
    /// <c>Dict!(from: sv)</c> resolves to the concrete <c>Dict[Text, SerialValue]</c> arm type
    /// instead of the bare generic definition.
    /// </summary>
    private static void TryInferVariantArmCreatorType(ref TypeInfo callableType,
        bool isFailableCall, List<TypeSymbol> creatorArgTypes)
    {
        // Type-arg inference for a bare failable variant arm extractor: `Dict!(from: sv)`
        // where `Dict` is a generic definition and the single argument is a variant — adopt
        // the type args of the variant's arm whose generic base is `Dict`.
        if (!callableType.IsGenericDefinition || !isFailableCall ||
            creatorArgTypes is not [VariantTypeInfo argVariant])
        {
            return;
        }

        string baseName = callableType.Name;
        VariantMemberInfo? matchArm = argVariant.Members.FirstOrDefault(predicate: m =>
            !m.IsNone && m.Type is not null && (m.Type switch
            {
                EntityTypeInfo e => e.GenericDefinition?.Name,
                RecordTypeInfo r => r.GenericDefinition?.Name,
                _ => null
            } ?? m.Type.Name) == baseName);
        if (matchArm?.Type is { } inferredArmType)
        {
            callableType = inferredArmType;
        }
    }

    /// <summary>
    /// When a creator exists on a generic definition, infers the concrete type arguments from
    /// the constructor's parameter types and re-specializes <paramref name="callableType"/> to
    /// the resulting concrete instance (e.g. <c>Retained(from: n)</c> → <c>Retained[Node]</c>).
    /// Mutates <paramref name="callableType"/> in place; the caller must re-lookup the creator
    /// on the new concrete type.
    /// </summary>
    /// <summary>
    /// When the overload-matched creator is null on a generic-definition type, falls back to the
    /// single arity-matching creator from the definition's member routine list. A concrete arg
    /// (`Node`) never matches the unbound param `T` in the overload matcher, so this arity-only
    /// fallback seeds the creator before type-arg inference specializes the callableType.
    /// </summary>
    private RoutineInfo? FallbackGenericDefCreatorByArity(TypeInfo callableType,
        List<TypeSymbol> creatorArgTypes)
    {
        if (!callableType.IsGenericDefinition)
        {
            return null;
        }

        var defCreators = _registry.GetMemberRoutinesForType(type: callableType)
                                   .Where(predicate: m =>
                                        m.IsCreator && m.Parameters.Count ==
                                        creatorArgTypes.Count)
                                   .ToList();
        return defCreators.Count == 1 ? defCreators[index: 0] : null;
    }

    /// <summary>
    /// Field-init recovery when the by-TYPE creator lookup missed because an argument's type failed to
    /// resolve. A memberwise <c>Type(field: value, ...)</c> is identified by NAMES: every named argument
    /// is a field of the type and the arity matches the field count. Returns the synthesized all-fields
    /// creator (params == the fields, registered by AutoWiredRegistrationPass) matched by field-NAME set
    /// rather than arg types, so field-init construction resolves even when an arg type is ErrorType (as
    /// in the reduced stdlib-validation pipeline, whose un-lowered generic-call args do not resolve).
    /// Null when the call is not a name-complete field-init (arity/name mismatch, positional, or a
    /// non-aggregate target) — so it never displaces a genuine by-type overload match.
    /// </summary>
    private RoutineInfo? FallbackMemberwiseCreatorByFieldNames(TypeInfo callableType, CallExpression call)
    {
        List<MemberVariableInfo>? fields = callableType switch
        {
            EntityTypeInfo e => e.MemberVariables,
            RecordTypeInfo r => r.MemberVariables,
            _ => null
        };
        if (fields is not { Count: > 0 } || call.Arguments.Count != fields.Count)
        {
            return null;
        }

        // Every argument must be a NAMED arg naming a field (positional field-init was already punned to
        // named form by PunMatchingNamedArgs before this point).
        var fieldNames = fields.Select(selector: f => f.Name).ToHashSet();
        foreach (Expression arg in call.Arguments)
        {
            if (arg is not NamedArgumentExpression named || !fieldNames.Contains(item: named.Name))
            {
                return null;
            }
        }

        // Prefer a registered memberwise creator (entities get one from AutoWiredRegistrationPass),
        // matched by field-NAME set rather than by arg types.
        RoutineInfo? registered = _registry.GetMemberRoutinesForType(type: callableType)
                                           .FirstOrDefault(predicate: m =>
                                                m.IsCreator && m.Parameters.Count == fields.Count &&
                                                m.Parameters.Select(selector: p => p.Name)
                                                 .ToHashSet()
                                                 .SetEquals(other: fieldNames));
        if (registered != null)
        {
            return registered;
        }

        // A RECORD has no registered memberwise creator — its field-init is inline construction — so
        // synthesize a transient all-fields creator here. The caller treats a synthesized creator as
        // inline field-init (it emits no ResolvedRoutine), so this only supplies the ConstructedType /
        // arity the resolution needs; codegen still does inline field construction.
        return new RoutineInfo(name: RoutineInfo.CreatorName)
        {
            Kind = RoutineKind.Creator,
            OwnerType = callableType,
            Parameters = fields.Select(selector: f => new ParameterInfo(name: f.Name, type: f.Type))
                               .ToList(),
            ReturnType = callableType,
            IsSynthesized = true
        };
    }

    private void TryInferGenericDefinitionCreatorType(ref TypeInfo callableType,
        RoutineInfo? creator, List<TypeSymbol> creatorArgTypes)
    {
        // Generic-def constructor routed through a user `create`: infer the wrapper's type args
        // from the creator's params so callableType becomes the CONCRETE instance and the creator
        // re-resolves to its instantiated form. `Retained(from: n)` (n: Node) → creator
        // `Retained[T].create(from: T)` binds T = Node ⇒ `Retained[Node]`, so ConstructedType,
        // ResolvedRoutine, and result type all match the explicit `Retained[Node](from: n)` path
        // (else codegen calls an uninstantiated create → AccessViolation). Reuses the already-
        // analyzed creatorArgTypes so `steal`-marked args are not re-analyzed (no double deadref).
        if (creator == null || !callableType.IsGenericDefinition ||
            callableType.GenericParameters is not { Count: > 0 } ctorDefParams)
        {
            return;
        }

        var ctorInferred = new TypeSymbol?[ctorDefParams.Count];
        int ctorArgN = Math.Min(val1: creator.Parameters.Count, val2: creatorArgTypes.Count);
        for (int ci = 0; ci < ctorArgN; ci++)
        {
            InferMemberRoutineTypeArgumentsFromTypes(
                paramType: creator.Parameters[index: ci].Type,
                argType: creatorArgTypes[index: ci],
                genericParameters: ctorDefParams,
                inferred: ctorInferred);
        }

        if (ctorInferred.All(predicate: t => t is not null) &&
            _registry.GetOrCreateResolution(genericDef: callableType,
                typeArguments: ctorInferred.Select(selector: t => t!)
                                           .ToList()) is { } ctorConcrete)
        {
            callableType = ctorConcrete;
        }
    }

    /// <summary>
    /// RF-S413 for constructor calls: reports an error when a bare entity is passed to a
    /// consuming entity parameter without an explicit <c>steal</c>. Verb-wrapped args
    /// (<c>steal x</c>, <c>x.copy()</c>) are <c>StealExpression</c>/<c>CallExpression</c>
    /// nodes, not <c>IdentifierExpression</c>/<c>MemberExpression</c>, so they pass freely.
    /// No-op in Suflae mode (Suflae does not use the RF ownership transfer model).
    /// </summary>
    private void ValidateCreatorEntityOwnershipArgs(CallExpression call, RoutineInfo creator,
        List<TypeSymbol> creatorArgTypes)
    {
        // RF-S413 for CONSTRUCTOR/creator calls: a bare entity passed to a consuming
        // entity parameter needs an explicit `steal` — same rule AnalyzeCallArguments
        // enforces for ordinary calls, but the creator path analyzes args separately and
        // used to bypass it. Without this, `Bytes(from_list: raw)` (raw a bare List entity)
        // slips through un-stolen; the callee owns and tears down the param while the
        // caller still owns `raw` → double-free once the param type's `destroy` is
        // materialized. Verb-wrapped args (`steal x`, `x.copy()`) are Steal/Call nodes, not
        // Identifier/Member, so they are excluded automatically.
        if (_registry.Language != Language.RazorForge)
        {
            return;
        }

        for (int ci = 0; ci < call.Arguments.Count; ci++)
        {
            Expression cArg = call.Arguments[index: ci];
            Expression cArgValue = cArg is NamedArgumentExpression cna ? cna.Value : cArg;
            ParameterInfo? cParam;
            if (cArg is NamedArgumentExpression cNamed)
            {
                cParam = creator.Parameters.FirstOrDefault(predicate: p => p.Name == cNamed.Name);
            }
            else
            {
                cParam = ci < creator.Parameters.Count ? creator.Parameters[index: ci] : null;
            }

            if (cParam is { Type: EntityTypeInfo } &&
                cArgValue is IdentifierExpression or MemberExpression &&
                creatorArgTypes[index: ci] is EntityTypeInfo cArgEntity)
            {
                ReportError(code: SemanticDiagnosticCode.BareEntityAssignment,
                    message:
                    $"Cannot pass entity '{cArgEntity.Name}' to consuming parameter " +
                    $"'{cParam.Name}' of '{creator.Name}' directly. Use 'steal' for " +
                    "ownership transfer, or pass a borrow.",
                    location: cArgValue.Location);
            }
        }
    }

    /// <summary>
    /// Records a failable constructor call on the enclosing routine and emits
    /// <see cref="SemanticWarningCode.UnhandledCrashableCall"/> when the enclosing routine is
    /// non-failable and not synthesized. Mirrors the same tracking done for failable member
    /// routine calls so that constructor failability propagates correctly.
    /// </summary>
    private void TrackFailableConstructorCall(RoutineInfo creator, string typeName,
        SourceLocation location)
    {
        // Failability propagation for failable constructors (e.g. `U32!(x)`
        // routing to `U32.create!(from: U64)`).
        if (!creator.IsFailable || _currentRoutine == null)
        {
            return;
        }

        _currentRoutine.HasFailableCalls = true;
        _currentRoutine.FailableCallees.Add(item: creator);
        if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
            !_currentRoutine.IsSynthesized)
        {
            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                message: $"Failable constructor '{typeName}!' called without error handling. " +
                         UseWhenHint,
                location: location);
        }
    }

    private void RecoverMissingFreeOverload(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is { IsVariadic: false } && call.Arguments.Count > routine.Parameters.Count)
        {
            RoutineInfo? variadicGeneric = _registry.LookupVariadicGenericOverload(name: callName);
            if (variadicGeneric != null)
            {
                List<TypeInfo>? inferred =
                    InferGenericTypeArguments(genericRoutine: variadicGeneric,
                        arguments: call.Arguments);
                routine = inferred != null
                    ? _registry.GetOrCreateRoutineResolution(genericDef: variadicGeneric,
                        typeArguments: inferred)
                    : variadicGeneric;
                call.ResolvedRoutine = routine;
            }
        }
    }

    private void ResolveFreeOverloadByArgumentTypes(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is not { IsGenericDefinition: false } || call.Arguments.Count == 0 ||
            routine.Parameters.Count != call.Arguments.Count)
        {
            return;
        }

        if (!HasArgumentTypeMismatch(call: call, routine: routine))
        {
            return;
        }

        // Collect all resolved arg types for better overload disambiguation
        var resolvedArgTypes = new List<TypeSymbol>();
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression actualArg = call.Arguments[index: i] is NamedArgumentExpression nai
                ? nai.Value
                : call.Arguments[index: i];
            TypeSymbol argType = AnalyzeExpression(expression: actualArg);
            if (argType != ErrorTypeInfo.Instance)
            {
                resolvedArgTypes.Add(item: argType);
            }
        }

        // Bare callName misses module-qualified overloads (the routines register
        // under `Module.name#params`). Fall back to the resolved routine's qualified
        // BaseName so overload resolution finds sibling overloads in the same module.
        RoutineInfo? better =
            _registry.LookupRoutineOverload(baseName: callName,
                argTypes: resolvedArgTypes) ??
            _registry.LookupRoutineOverload(baseName: routine.BaseName,
                argTypes: resolvedArgTypes);
        // Only accept a CONCRETE overload here. A generic definition can leak out of the
        // by-argType lookup when an argument is itself a bare generic parameter whose NAME
        // collides with the overload's own parameter name (e.g. arg `value: T` at a call
        // inside `wrap[T]`, matching `tag[T](value: T)`'s registry key `tag#T`). Taking it
        // raw would leave `routine` an un-inferred generic def → RF-S161. Fall through to
        // the generic-overload + inference path below instead.
        if (better is { IsGenericDefinition: false } && better != routine)
        {
            routine = better;
            call.ResolvedRoutine = routine;
            return;
        }

        TryRebindFreeOverloadAsGeneric(call: call,
            callName: callName,
            routine: ref routine);
    }

    /// <summary>
    /// Returns true when any argument's analyzed type does not match the corresponding
    /// parameter type of <paramref name="routine"/> (by full name or assignability).
    /// Used to determine whether overload re-resolution is necessary.
    /// </summary>
    private bool HasArgumentTypeMismatch(CallExpression call, RoutineInfo routine)
    {
        for (int i = 0; i < call.Arguments.Count; i++)
        {
            Expression argExpr = call.Arguments[index: i] is NamedArgumentExpression nax
                ? nax.Value
                : call.Arguments[index: i];
            TypeSymbol pt = routine.Parameters[index: i].Type;
            // Pass the parameter type as the expected type so a context-dependent arg
            // (`none`, a bare literal) resolves here instead of prematurely erroring —
            // AnalyzeCallArguments re-checks with the correct per-binding type afterwards.
            TypeSymbol at = AnalyzeExpression(expression: argExpr, expectedType: pt);
            if (at == ErrorTypeInfo.Instance)
            {
                continue;
            }

            if (at.FullName != pt.FullName && !IsAssignableTo(source: at, target: pt))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// When a concrete overload re-resolution fails, attempts to find a generic overload
    /// by arity and infer its type arguments, falling back to sibling overloads when the
    /// first-pick generic does not unify.
    /// </summary>
    private void TryRebindFreeOverloadAsGeneric(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        RoutineInfo? generic = _registry.LookupGenericOverload(name: callName,
            preferredArity: call.Arguments.Count);
        if (generic == null)
        {
            return;
        }

        List<TypeInfo>? inferred = InferGenericTypeArguments(genericRoutine: generic,
            arguments: call.Arguments);
        // Use GetOrCreateRoutineResolution so the monomorphisation lands
        // in `_routineResolutions`. CreateInstance alone produced a stray
        // instance that codegen mangled to `show(Point)` but
        // ProcessResolvedMemberRoutineGenericRoutines never picked up — no body
        // emitted, link errors followed.
        routine = inferred != null
            ? _registry.GetOrCreateRoutineResolution(genericDef: generic,
                typeArguments: inferred)
            : generic;
        call.ResolvedRoutine = routine;
    }

    private void RebindFreeOverloadByArity(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is not { IsGenericDefinition: false, IsVariadic: false } ||
            call.Arguments.Count == routine.Parameters.Count)
        {
            return;
        }

        var arityArgTypes = new List<TypeSymbol>();
        foreach (Expression arg in call.Arguments)
        {
            Expression actual = arg is NamedArgumentExpression nai ? nai.Value : arg;
            TypeSymbol t = AnalyzeExpression(expression: actual);
            if (t != ErrorTypeInfo.Instance)
            {
                arityArgTypes.Add(item: t);
            }
        }

        RoutineInfo? arityMatch =
            _registry.LookupRoutineOverload(baseName: callName, argTypes: arityArgTypes) ??
            _registry.LookupRoutineOverload(baseName: routine.BaseName, argTypes: arityArgTypes);
        if (arityMatch != null && arityMatch != routine)
        {
            routine = arityMatch;
            call.ResolvedRoutine = routine;
            return;
        }

        // `LookupGenericOverload` returns the first same-arity overload; it cannot
        // choose among generic overloads that differ only in PARAMETER TYPE (e.g.
        // `when_interrupted[T, P](Guarded[T, P])` vs `when_interrupted[T](Roamed[T])`).
        // If the first pick does not unify, try the sibling overloads.
        TryRebindFreeOverloadAsGenericWithSiblings(call: call,
            callName: callName,
            routine: ref routine);
    }

    /// <summary>
    /// Looks up a generic overload by arity for <paramref name="callName"/>, tries to infer
    /// its type arguments, and when the first pick does not unify iterates sibling overloads of
    /// the same arity until one does. Binds <paramref name="routine"/> to the resolved instance.
    /// </summary>
    private void TryRebindFreeOverloadAsGenericWithSiblings(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        RoutineInfo? generic = _registry.LookupGenericOverload(name: callName,
            preferredArity: call.Arguments.Count);
        if (generic == null)
        {
            return;
        }

        List<TypeInfo>? inferred = InferGenericTypeArguments(genericRoutine: generic,
            arguments: call.Arguments);

        if (inferred == null)
        {
            TryInferFromGenericSiblings(call: call,
                generic: ref generic,
                inferred: ref inferred);
        }

        routine = inferred != null
            ? _registry.GetOrCreateRoutineResolution(genericDef: generic,
                typeArguments: inferred)
            : generic;
        call.ResolvedRoutine = routine;
    }

    /// <summary>
    /// Iterates same-name, same-arity generic sibling overloads and returns the first one
    /// whose type arguments can be inferred from the call's arguments. Mutates
    /// <paramref name="generic"/> and <paramref name="inferred"/> when a matching sibling
    /// is found.
    /// </summary>
    private void TryInferFromGenericSiblings(CallExpression call, ref RoutineInfo generic,
        ref List<TypeInfo>? inferred)
    {
        foreach (RoutineInfo sibling in _registry.GenericOverloadsByArity(
                     name: generic.Name,
                     arity: call.Arguments.Count))
        {
            if (sibling == generic)
            {
                continue;
            }

            List<TypeInfo>? siblingInferred = InferGenericTypeArguments(genericRoutine: sibling,
                arguments: call.Arguments);
            if (siblingInferred != null)
            {
                generic = sibling;
                inferred = siblingInferred;
                break;
            }
        }
    }

    private void InferFreeRoutineArguments(CallExpression call, TypeInfo? expectedType,
        ref RoutineInfo? routine)
    {
        if (routine is not { IsGenericDefinition: true } ||
            call.TypeArguments is { Count: > 0 } ||
            routine.GenericParameters is not { Count: > 0 } ||
            call.Arguments.Count != routine.Parameters.Count)
        {
            return;
        }

        List<TypeInfo>? inferred = InferGenericTypeArguments(genericRoutine: routine,
            arguments: call.Arguments,
            expectedType: expectedType);

        // The initial pick is first-wins by name+arity, which is not enough to choose among
        // several generic overloads that differ only in PARAMETER TYPE (e.g.
        // `when_interrupted[T, P](Guarded[T, P])` vs `when_interrupted[T](Roamed[T])`). If it
        // does not unify with the arguments, try the sibling overloads and take the one that does.
        if (inferred == null)
        {
            TryInferFromGenericSiblingsWithExpected(call: call,
                expectedType: expectedType,
                generic: ref routine,
                inferred: ref inferred);
        }

        if (inferred != null)
        {
            // Same clean-diagnostic constraint check as the explicit-type-arg branch, for an
            // INFERRED generic call (arg-typed, no `[...]`).
            ValidateRoutineGenericConstraints(routine: routine,
                typeArgs: inferred,
                location: call.Location);
            routine = _registry.GetOrCreateRoutineResolution(
                genericDef: routine,
                typeArguments: inferred);
        }
    }

    private void SelectGenericFreeOverloadByArity(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is { IsGenericDefinition: true, IsVariadic: false } &&
            (call.TypeArguments == null || call.TypeArguments.Count == 0) &&
            routine.Parameters.Count != call.Arguments.Count)
        {
            RoutineInfo? arityGeneric =
                _registry.LookupGenericOverload(name: callName,
                    preferredArity: call.Arguments.Count) ??
                _registry.LookupGenericOverload(name: routine.BaseName,
                    preferredArity: call.Arguments.Count);
            if (arityGeneric is { IsVariadic: false } &&
                arityGeneric.Parameters.Count == call.Arguments.Count)
            {
                routine = arityGeneric;
            }
        }
    }

    private void BindExplicitFreeTypeArguments(CallExpression call, ref RoutineInfo? routine)
    {
        if (routine is { IsGenericDefinition: true } &&
            call.TypeArguments is { Count: > 0 } routineExplicitTypeArgs &&
            routine.GenericParameters?.Count == routineExplicitTypeArgs.Count)
        {
            var resolvedTypeArguments =
                new List<TypeInfo>(capacity: routineExplicitTypeArgs.Count);
            foreach (TypeExpression ta in routineExplicitTypeArgs)
            {
                resolvedTypeArguments.Add(item: ResolveType(typeExpr: ta));
            }

            // Enforce the routine's `needs <param> obeys P` constraints against the explicit type
            // args as a CLEAN semantic error (RF-S150) — before monomorphization prunes the body
            // and codegen would instead trip an "over-prune / undefined symbol" crash.
            ValidateRoutineGenericConstraints(routine: routine,
                typeArgs: resolvedTypeArguments,
                location: call.Location);
            routine = _registry.GetOrCreateRoutineResolution(
                genericDef: routine,
                typeArguments: resolvedTypeArguments);
        }
    }

    private void LookupImplicitFailableRoutine(bool isFailableCall, string callName,
        ref RoutineInfo? routine)
    {
        if (routine == null && !isFailableCall)
        {
            routine = _registry.LookupRoutine(fullName: callName, isFailable: true);
            if (routine == null && _currentModuleName != null && !callName.Contains(value: '.'))
            {
                routine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{callName}",
                    isFailable: true);
            }
        }
    }

    private void ResolveExplicitConstructorTypeArguments(CallExpression call,
        ref TypeInfo? callableType)
    {
        if (callableType != null && call.TypeArguments is { Count: > 0 } typeArguments)
        {
            var resolvedTypeArguments = new List<TypeSymbol>(capacity: typeArguments.Count);
            foreach (TypeExpression typeArg in typeArguments)
            {
                resolvedTypeArguments.Add(item: ResolveType(typeExpr: typeArg));
            }

            if (callableType.IsGenericDefinition)
            {
                ValidateGenericConstraints(genericDef: callableType,
                    typeArgs: resolvedTypeArguments,
                    location: call.Location);
                callableType = _registry.GetOrCreateResolution(genericDef: callableType,
                    typeArguments: resolvedTypeArguments.ToList());
            }
        }
    }

    private ErrorTypeInfo? AnalyzeUnresolvedMemberFieldCall(CallExpression call,
        MemberExpression member, TypeInfo objectType, bool isFailableMemberRoutineCall,
        string callLookupName)
    {
        if (objectType is EntityTypeInfo or RecordTypeInfo)
        {
            List<MemberVariableInfo> receiverFields = objectType switch
            {
                EntityTypeInfo e => e.MemberVariables,
                RecordTypeInfo r => r.MemberVariables,
                _ => []
            };
            MemberVariableInfo? namedField =
                receiverFields.FirstOrDefault(predicate: mv => mv.Name == callLookupName);

            // A Routine-typed field is the only legitimate "member routine == null" member call:
            // it is invoked indirectly through the stored closure pointer.
            if (namedField is not { Type: RoutineTypeInfo })
            {
                string hint;
                if (namedField != null)
                {
                    hint = $" '{callLookupName}' is a field — access it as '.{callLookupName}' " +
                           "(no parentheses), or define a routine of that name.";
                }
                else if (!isFailableMemberRoutineCall &&
                         _registry.LookupMemberRoutine(type: objectType,
                             memberRoutineName: callLookupName,
                             isFailable: true) != null)
                {
                    hint = $" Did you mean the failable form '.{callLookupName}!()'?";
                }
                else
                {
                    hint = "";
                }

                ReportError(code: SemanticDiagnosticCode.MemberRoutineNotFound,
                    message:
                    $"No routine '{member.MemberName}()' is defined on '{objectType.Name}'.{hint}",
                    location: call.Location);
                return ErrorTypeInfo.Instance;
            }
        }

        return null;
    }

    private TypeSymbol? AnalyzeMemberConversion(CallExpression call, TypeInfo objectType,
        string potentialTypeName, string creatorName, TypeInfo? targetType)
    {
        if (targetType == null)
        {
            return null;
        }

        // Look up the creator on the target type, using memberRoutine-overload resolution
        // to match the object type (e.g., Text -> S32.create!(from_text: Text)).
        // Note: parser strips '!' from routine names — IsFailable is a separate flag.
        // Always look up "create" and check IsFailable on the result.
        // create is owner-scoped, so LookupMemberRoutineOverload (not LookupRoutineOverload)
        // is the right entry point — the latter only indexes free functions.
        RoutineInfo? creator = _registry.LookupMemberRoutineOverload(type: targetType,
            memberRoutineName: creatorName,
            argTypes: [objectType]);
        // Fall back to default overload if no match by arg type
        string creatorFullName = $"{targetType.FullName}.{creatorName}";
        creator ??= _registry.LookupRoutine(fullName: creatorFullName);

        if (creator == null)
        {
            return null;
        }

        call.ConstructedType = targetType;
        call.LoweringKind = CallLoweringKind.TypeConstructor;
        // Stamp the resolved creator so codegen calls it directly rather than
        // rediscovering intent from a null ResolvedRoutine (task #23). The receiver
        // is the conversion source — codegen passes it as the `from:` argument.
        call.ResolvedRoutine = creator;

        TypeSymbol? validationError = ValidateMemberConversionCreator(call: call,
            objectType: objectType,
            potentialTypeName: potentialTypeName,
            creator: creator);
        if (validationError != null)
        {
            return validationError;
        }

        TrackFailableMemberRoutineCall(memberRoutine: creator, location: call.Location);

        // A conversion-variant chain (`.try_S64()` -> `S64.try_create`) returns the carrier
        // (Maybe/Result/Lookup[targetType]), not the bare target type. `create` returns the
        // target type as before. `call.ConstructedType` stays the target so codegen's
        // TypeConstructor path passes the receiver as the `from:` arg either way.
        return creatorName == RoutineInfo.CreatorName
            ? targetType
            : creator.ReturnType ?? targetType;
    }

    /// <summary>
    /// Validates the resolved member-conversion creator: checks that it has exactly one non-me
    /// parameter, that the call passes no extra arguments, and that the object type is assignable
    /// to the creator parameter type. Returns <see cref="ErrorTypeInfo.Instance"/> on failure,
    /// null on success.
    /// </summary>
    private ErrorTypeInfo? ValidateMemberConversionCreator(CallExpression call, TypeInfo objectType,
        string potentialTypeName, RoutineInfo creator)
    {
        var nonMeParams = creator.Parameters
                                 .Where(predicate: p => p.Name != "me")
                                 .ToList();

        if (nonMeParams.Count != 1)
        {
            ReportError(code: SemanticDiagnosticCode.MemberRoutineChainMultiArg,
                message:
                $"member routine-chain constructor '{potentialTypeName}' requires exactly one non-'me' parameter, " +
                $"but 'create' has {nonMeParams.Count}.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        if (call.Arguments.Count > 0)
        {
            ReportError(code: SemanticDiagnosticCode.MemberRoutineChainMultiArg,
                message:
                $"member routine-chain constructor '{potentialTypeName}' takes no additional arguments — " +
                "the object itself is the argument.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        // Type-check the object expression against the constructor parameter.
        // We only reach the failure branch when LookupMemberRoutineOverload found no
        // create overload accepting objectType and the fallback above returned
        // an arbitrary overload (e.g. create(from: S8)). Report the real problem
        // — the missing conversion routine — rather than a misleading mismatch
        // against that arbitrary overload's parameter type.
        if (!IsAssignableTo(source: objectType, target: nonMeParams[index: 0].Type))
        {
            ReportError(code: SemanticDiagnosticCode.ArgumentTypeMismatch,
                message:
                $"Type '{objectType.Name}' has no conversion to '{potentialTypeName}': " +
                $"no '{potentialTypeName}.create(from: {objectType.Name})' is defined.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        return null;
    }

    private TypeSymbol? AnalyzeResolvedMemberCall(CallExpression call, MemberExpression member,
        TypeInfo objectType, TypeInfo dispatchType, RoutineInfo? memberRoutine)
    {
        if (memberRoutine == null)
        {
            return null;
        }

        call.LoweringKind = ClassifyMemberRoutineCall(memberRoutine: memberRoutine);

        // Import-gating: BuilderQuery routines require 'import BuilderQuery'
        if (memberRoutine.IsSynthesized &&
            BuilderInfoProvider.IsBuilderQueryRoutine(name: memberRoutine.Name) &&
            !_importedModules.Contains(item: "BuilderQuery"))
        {
            ReportError(code: SemanticDiagnosticCode.BuilderQueryImportRequired,
                message: $"'{memberRoutine.Name}()' requires 'import BuilderQuery'.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        TrackFailableMemberRoutineCall(memberRoutine: memberRoutine, location: call.Location);

        // #151: Static/instance mismatch — common routine called on instance.
        // Generic type parameters (e.g., `T` inside `Dict[K, V]` body) are not
        // registered as types but ARE valid receivers for common routines.
        if (memberRoutine.IsCommon && member.Object is IdentifierExpression instanceId &&
            LookupTypeWithImports(name: instanceId.Name) == null &&
            !IsGenericParameter(name: instanceId.Name))
        {
            ReportError(code: SemanticDiagnosticCode.CommonRoutineMismatch,
                message:
                $"Common routine '{memberRoutine.Name}' must be called on the type '{objectType.Name}', not on an instance.",
                location: call.Location);
        }

        ValidateRoutineAccess(routine: memberRoutine,
            accessLocation: call.Location,
            isCompilerSynthesized: call.IsSynthesizedLowering);

        ValidateMemberCallMutability(call: call,
            member: member,
            objectType: objectType,
            dispatchType: dispatchType,
            memberRoutine: memberRoutine);

        // Variadic member routine (e.g. a collection `create(elements...: T)`): pack the K
        // trailing args into an Array[T, K] literal so arg count matches the desugared single
        // Array parameter and the arity binds during inference below.
        bool didPackVariadic = TryPackVariadicCallArgs(arguments: call.Arguments,
            routine: memberRoutine,
            location: call.Location);

        // For a VARIADIC generic member routine (e.g. `List[T].from_literal(elements...: T)`),
        // the arity generic `__VarargN` must be inferred from the freshly-packed
        // `Array[T, K]` literal BEFORE AnalyzeCallArguments below re-analyzes that literal
        // against the still-generic parameter type `Array[T, __VarargN]` — which would
        // re-resolve the array's arity type-arg back to the unbound `__VarargN` and lose K.
        // Infer + monomorphize here so the subsequent analysis runs against the concrete
        // per-arity body. (Mirrors the free-routine path: pack → infer → analyze.)
        if (didPackVariadic && memberRoutine.IsGenericDefinition)
        {
            List<TypeInfo>? variadicArgs = InferMemberRoutineGenericTypeArguments(
                genericMemberRoutine: memberRoutine,
                arguments: call.Arguments,
                receiverType: dispatchType);
            if (variadicArgs != null)
            {
                memberRoutine = _registry.GetOrCreateRoutineResolution(
                    genericDef: memberRoutine,
                    typeArguments: variadicArgs);
            }
        }

        AnalyzeCallArguments(routine: memberRoutine,
            arguments: call.Arguments,
            location: call.Location,
            callObjectType: dispatchType);

        if (memberRoutine.IsGenericDefinition)
        {
            List<TypeInfo>? inferredMemberRoutineTypeArgs =
                InferMemberRoutineGenericTypeArguments(genericMemberRoutine: memberRoutine,
                    arguments: call.Arguments,
                    receiverType: dispatchType);
            if (inferredMemberRoutineTypeArgs != null)
            {
                memberRoutine = _registry.GetOrCreateRoutineResolution(
                    genericDef: memberRoutine,
                    typeArguments: inferredMemberRoutineTypeArgs);
                // AnalyzeCallArguments above ran against the still-generic signature, so a
                // lambda argument whose parameter binds a memberRoutine-level generic kept it
                // unresolved (e.g. `acc` in `accumulate[U](combiner: Routine[(U,T),U])`
                // stayed `U`). Now that the memberRoutine generics are bound, re-analyze the
                // lambda arguments against the resolved parameter types so their
                // parameters become concrete — otherwise the lifted lambda mangles with an
                // unbound generic (`[lambda]...(U,S64)`) and codegen cannot emit it.
                ReanalyzeLambdaArguments(resolvedMemberRoutine: memberRoutine,
                    arguments: call.Arguments,
                    callObjectType: dispatchType);
            }
        }

        // P1: Store fully resolved RoutineInfo (with owner-level generic substitution)
        call.ResolvedRoutine = memberRoutine;

        ValidateMemberCallSemantics(call: call,
            member: member,
            objectType: objectType,
            memberRoutine: memberRoutine);

        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        TypeSymbol callReturnType = SubstituteMemberReturnType(memberRoutine: memberRoutine,
            dispatchType: dispatchType);
        call.IsInFlight = memberRoutine.IsInFlightReturn;
        return callReturnType;
    }

    /// <summary>
    /// Records a failable member-routine call on the enclosing routine and emits
    /// <see cref="SemanticWarningCode.UnhandledCrashableCall"/> when the enclosing routine is
    /// non-failable and not synthesized. Mirrors <c>TrackFailableConstructorCall</c> for the
    /// member-call path.
    /// </summary>
    private void TrackFailableMemberRoutineCall(RoutineInfo memberRoutine, SourceLocation location)
    {
        if (!memberRoutine.IsFailable || _currentRoutine == null)
        {
            return;
        }

        _currentRoutine.HasFailableCalls = true;
        _currentRoutine.FailableCallees.Add(item: memberRoutine);
        if (!_currentRoutine.IsFailable && _currentRoutine.Name != StartRoutineName &&
            !_currentRoutine.IsSynthesized)
        {
            ReportWarning(code: SemanticWarningCode.UnhandledCrashableCall,
                message: $"Failable routine '{memberRoutine.Name}!' called without error handling. " +
                         UseWhenHint,
                location: location);
        }
    }

    /// <summary>
    /// Validates mutability constraints for a member-routine call: read-only wrapper protocol,
    /// <c>@readonly</c> context enforcement (<c>me.mutate()</c> in a <c>@readonly</c> routine),
    /// and preset-variable mutation prohibition.
    /// </summary>
    private void ValidateMemberCallMutability(CallExpression call, MemberExpression member,
        TypeInfo objectType, TypeInfo dispatchType, RoutineInfo memberRoutine)
    {
        if (!ReferenceEquals(objA: dispatchType, objB: objectType) &&
            IsReadOnlyTransparentProtocol(type: objectType) && !memberRoutine.IsReadOnly)
        {
            ReportError(
                code: SemanticDiagnosticCode.WritableMemberRoutineThroughReadOnlyWrapper,
                message:
                $"Cannot call writable member routine '{memberRoutine.Name}' through read-only protocol '{objectType.Name}'. " +
                "Use Controlling[T] or a writable token instead.",
                location: call.Location);
        }

        // @readonly enforcement: cannot call mutating memberRoutines on 'me'. RazorForge-only —
        // Suflae hides @readonly/@reshaping, so a Suflae build never enforces it (even on the
        // borrowed RF stdlib, whose readonly discipline is RazorForge's own concern).
        if (_registry.CompilationLanguage != Language.Suflae &&
            _currentRoutine is { IsReadOnly: true } &&
            member.Object is IdentifierExpression { Name: "me" } && !memberRoutine.IsReadOnly)
        {
            ReportError(code: SemanticDiagnosticCode.MutationInReadonlyMemberRoutine,
                message:
                $"Cannot call non-readonly member routine '{memberRoutine.Name}' on 'me' in a @readonly member routine. " +
                "Mark the called member routine @readonly or use @reshaping.",
                location: call.Location);
        }

        // Preset enforcement: cannot call mutating memberRoutines on preset variables. Uses
        // IsReadOnly (annotation OR category) not a bare category check — a member routine
        // whose registration left MutationCategory at the default would otherwise look
        // mutating and spuriously reject a plainly-@readonly call (e.g. list.count()).
        if (member.Object is IdentifierExpression letTarget && !memberRoutine.IsReadOnly)
        {
            VariableInfo? targetVar = _registry.LookupVariable(name: letTarget.Name);
            if (targetVar is { IsModifiable: false })
            {
                ReportError(code: SemanticDiagnosticCode.ModifyingCallOnImmutable,
                    message:
                    $"Cannot call modifying member routine '{memberRoutine.Name}' on preset variable '{letTarget.Name}'.",
                    location: call.Location);
            }
        }
    }

    /// <summary>
    /// Validates call-site semantic rules that apply AFTER argument analysis: real↔complex
    /// promotion, partial-entity access (.view()/.modify() on a member field), nested grasping,
    /// re-grasping, token downgrade, Guarded/Watched hijack in danger, receiver-inherited
    /// type-equality constraints, multi-threaded access-token using-requirement, reshaping
    /// during iteration, initonly-record grasp warning, and Channel send deadref marking.
    /// </summary>
    private void ValidateMemberCallSemantics(CallExpression call, MemberExpression member,
        TypeInfo objectType, RoutineInfo memberRoutine)
    {
        ValidateMemberCallOperatorSemantics(call: call,
            member: member,
            objectType: objectType,
            memberRoutine: memberRoutine);

        ValidateMemberCallTokenSemantics(call: call,
            member: member,
            objectType: objectType,
            memberRoutine: memberRoutine);

        // #22: Reject reshaping operations on the collection being iterated (RF-S625). Keyed on
        // the @reshaping marker (via IsReshaping) — the definitional signal, and robust to
        // member-routine registration paths that leave MutationCategory at its default.
        if (member.Object is IdentifierExpression iterTarget &&
            _activeIterationSources.Contains(item: iterTarget.Name) &&
            memberRoutine.IsReshaping)
        {
            ReportError(code: SemanticDiagnosticCode.ReshapingDuringIteration,
                message:
                $"Cannot call reshaping member routine '{memberRoutine.Name}' on '{iterTarget.Name}' while iterating over it. " +
                "Collect changes and apply them after the loop.",
                location: call.Location);
        }

        // #47: .grasp() on @initonly record warns — record is frozen after construction
        // Check if the variable holding the record is @initonly bound
        if (member.MemberName == ModifyMemberRoutineName && objectType is RecordTypeInfo &&
            member.Object is IdentifierExpression graspTarget)
        {
            VariableInfo? targetVar = _registry.LookupVariable(name: graspTarget.Name);
            if (targetVar is { IsModifiable: false })
            {
                ReportWarning(code: SemanticWarningCode.HijackOnInitOnly,
                    message:
                    $"Calling '.grasp()' on @initonly-bound record '{graspTarget.Name}'. " +
                    "The record is frozen after construction — grasping has no practical effect.",
                    location: call.Location);
            }
        }

        // #104/#23: Channel send() makes source variable a deadref
        if (member is { MemberName: "send", Object: IdentifierExpression sendSource } &&
            objectType.BareName == "Channel")
        {
            _deadrefVariables.Add(item: sendSource.Name);
        }
    }

    /// <summary>
    /// Validates operator-specific semantic constraints: real↔complex promotion prohibition
    /// (operators other than add/sub do not allow it).
    /// </summary>
    private void ValidateMemberCallOperatorSemantics(CallExpression call, MemberExpression member,
        TypeInfo objectType, RoutineInfo memberRoutine)
    {
        // #68: Real-to-Complex promotion — only add/sub allow float↔complex cross-type
        if (!IsOperatorWired(name: member.MemberName) ||
            member.MemberName is "add" or "sub" or "iadd" or "isub" ||
            call.Arguments.Count == 0 || memberRoutine.Parameters.Count == 0)
        {
            return;
        }

        TypeSymbol argType = memberRoutine.Parameters[index: 0].Type;
        if (IsFloatType(type: objectType) && IsComplexType(type: argType) ||
            IsComplexType(type: objectType) && IsFloatType(type: argType))
        {
            ReportError(code: SemanticDiagnosticCode.RealComplexPromotionInvalid,
                message:
                $"Operator '{member.MemberName}' does not allow real↔complex promotion. " +
                "Only '+' and '-' support implicit real-to-complex conversion. Use explicit conversion for other operators.",
                location: call.Location);
        }
    }

    /// <summary>
    /// Validates access-token and modify-member semantic constraints: partial entity access,
    /// nested grasping, re-grasping, view downgrade, hijack danger requirement, receiver-
    /// inherited type-equality constraints, and multi-threaded access-token using requirement.
    /// </summary>
    private void ValidateMemberCallTokenSemantics(CallExpression call, MemberExpression member,
        TypeInfo objectType, RoutineInfo memberRoutine)
    {
        ValidateAccessTokenViewModifyRules(call: call, member: member, objectType: objectType);

        // #97: A Hijacked[T] memberRoutine requires a danger block ONLY when the memberRoutine itself is
        // `dangerous` (peek/poke/as_entity/invalidate/… — real deref/free/UB ops). That is
        // already enforced uniformly by the `routine.IsDangerous` gate in
        // ValidateRoutineAccess, so there is NO blanket "any Hijacked member routine needs danger"
        // rule: the pointer-value ops (address/type_name/is_none/cmp/hash/represent) read
        // an integer without dereferencing and are safe outside danger (danger-audit).

        // #98: .hijack() on Guarded/Witnessed requires danger block
        if (member.MemberName == Declaration.RuntimeContract.RawPointer.Hijack &&
            !InDangerBlock &&
            (IsSharedType(type: objectType) || IsWatchedType(type: objectType)))
        {
            ReportError(code: SemanticDiagnosticCode.SnatchRequiresDanger,
                message:
                $"Calling '.hijack()' on '{objectType.Name}' requires a 'danger' block. " +
                "Hijacked values bypasses reference counting safety.",
                location: call.Location);
        }

        // `consult` and `amend` are ordinary Guarded member routines now — their
        // policy legality (consult not on Exclusive, amend not on ReadOnly) is enforced by
        // the type-equality constraint (RF-S160), and their lifetime by the using-binding
        // rule. The earlier ad-hoc validation was replaced by the type system.

        // Enforce a memberRoutine's `needs P in [...]` (TypeEquality) constraint when the
        // constrained parameter is INHERITED FROM THE RECEIVER (e.g.
        // `Guarded[T, P].amend() needs P in [Exclusive, MultiRead]`, with P bound by the
        // receiver `Guarded[Counter, ReadOnly]`). The general constraint validator only
        // fires for explicitly-instantiated generics, so a receiver-bound param — which
        // carries no explicit type args at the call site — is validated here instead.
        ValidateReceiverInheritedTypeEqualityConstraints(memberRoutine: memberRoutine,
            receiverType: objectType,
            member: member,
            location: call.Location);

        // A multi-threaded access token (Consulting/Amending, produced by
        // consult()/amend()) is only legal as the immediate resource of a `using` block,
        // so its lock spans exactly that scope. Reject every other position — inline use,
        // a function argument, an unbound statement — with RF-S629. (The "cannot bind to a
        // var" half is already enforced for inline-only tokens at var-declaration sites.)
        if (memberRoutine.ReturnType is { } mtReturn &&
            mtReturn.BareName is Declaration.RuntimeContract.Consulting
                or Declaration.RuntimeContract.Amending &&
            !ReferenceEquals(objA: call, objB: _usingResourceNode))
        {
            ReportError(code: SemanticDiagnosticCode.MtTokenRequiresUsing,
                message:
                $"'{member.MemberName}()' returns a scope-bound access token and must be " +
                $"opened with 'using' (e.g. 'using …{member.MemberName}() as v'). It " +
                "cannot be used inline, passed as an argument, or stored.",
                location: call.Location);
        }
    }

    /// <summary>
    /// Validates the view/modify access-token usage rules: partial entity-field access prohibition
    /// (entity.field.view()/grasp()), nested grasping prohibition, re-grasping prohibition, and
    /// view-downgrade prohibition on Modifying/Amending tokens.
    /// </summary>
    private void ValidateAccessTokenViewModifyRules(CallExpression call, MemberExpression member,
        TypeInfo objectType)
    {
        // #12: Partial access rule — entity.field.view() is not allowed
        if (member.MemberName is "view" or ModifyMemberRoutineName &&
            member.Object is MemberExpression innerMember)
        {
            TypeSymbol innerObjectType = innerMember.Object.ResolvedType ?? ErrorTypeInfo.Instance;
            if (innerObjectType is EntityTypeInfo)
            {
                ReportError(code: SemanticDiagnosticCode.PartialAccessOnEntity,
                    message:
                    $"Cannot call '.{member.MemberName}()' on entity member variable '{innerMember.MemberName}'. " +
                    $"Access the entity directly instead of its individual member variables.",
                    location: call.Location);
            }
        }

        // #137: Nested grasping detection
        if (member.MemberName == ModifyMemberRoutineName &&
            IsNestedModifying(source: member.Object))
        {
            ReportError(code: SemanticDiagnosticCode.NestedHijackingNotAllowed,
                message: "Cannot grasp a member of an already-grasped object. " +
                         "Hijack the parent entity directly instead.",
                location: call.Location);
        }

        // #92: Re-grasping prohibition — cannot grasp an already-grasped token
        if (member.MemberName == ModifyMemberRoutineName && IsModifyingType(type: objectType))
        {
            ReportError(code: SemanticDiagnosticCode.ReHijackingProhibited,
                message: $"Cannot re-modify an already-modified token '{objectType.Name}'. " +
                         "The entity is already exclusively accessed.",
                location: call.Location);
        }

        // #170: Downgrade prohibition — cannot call .view() on Modifying/Amending
        if (member.MemberName == "view" && (IsModifyingType(type: objectType) ||
                                            IsAmendingType(type: objectType)))
        {
            ReportError(code: SemanticDiagnosticCode.TokenDowngradeProhibited,
                message: $"Cannot downgrade '{objectType.Name}' with '.view()'. " +
                         "Modifying/Amending tokens already have write access — use them directly.",
                location: call.Location);
        }
    }

    /// <summary>
    /// Computes the concrete return type of a resolved member-routine call by applying all
    /// applicable substitutions: generic-parameter receiver, protocol owner type args,
    /// ProtocolSelf "Me" placeholder, and obeys-constraint protocol params for generic-param
    /// dispatch types. Falls back to <c>None</c> when the routine declares no return type.
    /// </summary>
    private TypeSymbol SubstituteMemberReturnType(RoutineInfo memberRoutine, TypeInfo dispatchType)
    {
        TypeSymbol? callReturnType = memberRoutine.ReturnType;
        if (callReturnType == null)
        {
            return _registry.LookupType(name: NoneTypeName) ?? ErrorTypeInfo.Instance;
        }

        var substitutions = new Dictionary<string, TypeSymbol>();

        // GenericParameterTypeInfo owner -> map param name to receiver type
        if (memberRoutine.OwnerType is GenericParameterTypeInfo genParamOwner)
        {
            substitutions[key: genParamOwner.Name] = dispatchType;
        }

        // Protocol owner -> map protocol generic params to receiver's type args
        if (memberRoutine.OwnerType is ProtocolTypeInfo protoOwner && dispatchType is
                { IsGenericResolution: true, TypeArguments: not null })
        {
            ProtocolTypeInfo protoGenDef = protoOwner.GenericDefinition ?? protoOwner;
            if (protoGenDef.GenericParameters is { Count: > 0 })
            {
                for (int i = 0;
                     i < protoGenDef.GenericParameters.Count &&
                     i < dispatchType.TypeArguments.Count;
                     i++)
                {
                    substitutions[key: protoGenDef.GenericParameters[index: i]] =
                        dispatchType.TypeArguments[index: i];
                }
            }
        }

        // The ProtocolSelf placeholder "Me" in a return type always denotes the receiver.
        // For example, Iterable[T].enumerate returns an EnumerateIterator parameterized
        // by Me. Bind Me to the concrete receiver so the call's return type is the
        // concrete adapter type. This is unconditional: the protocol-extension routine
        // is re-homed onto the implementer (owner is the concrete type, not the protocol),
        // so an owner-is-protocol gate would miss it; for non-protocol routines no return
        // type contains Me, so this substitution is a no-op.
        substitutions[key: "Me"] = dispatchType;

        // Protocol memberRoutine resolved through a generic param's `obeys` constraint
        // (e.g. `r.iter()` where `r: __T0 obeys Iterable[S64]`). The resolved memberRoutine
        // is homed on the bare generic param, and its signature carries the PROTOCOL's
        // own element param (`Iterator[T]`), which is distinct from `__T0` and so isn't
        // bound by the branches above. Bind each obeys-constraint protocol's params from
        // the constraint's type args (`Iterable[S64]` ⇒ T=S64) so a return type like
        // `Iterator[T]` resolves to `Iterator[S64]` instead of leaking the element param
        // into the monomorphized body (`GenericParameterTypeInfo 'T' reached GetLlvmType`).
        if (dispatchType is GenericParameterTypeInfo dispatchParam)
        {
            BindObeyingConstraintSubstitutions(dispatchParam: dispatchParam,
                substitutions: substitutions);
        }

        if (substitutions.Count > 0)
        {
            callReturnType = SubstituteWithMapping(type: callReturnType,
                substitutions: substitutions);
        }

        return callReturnType;
    }

    /// <summary>
    /// Populates <paramref name="substitutions"/> with mappings from each
    /// <c>obeys</c>-constrained protocol's generic parameters to the constraint's concrete
    /// type arguments, for a dispatch type that is a generic parameter (e.g.
    /// <c>__T0 obeys Iterable[S64]</c> → T = S64).
    /// </summary>
    private void BindObeyingConstraintSubstitutions(GenericParameterTypeInfo dispatchParam,
        Dictionary<string, TypeSymbol> substitutions)
    {
        foreach (GenericConstraintDeclaration gc in ActiveConstraintsFor(
                     paramName: dispatchParam.Name))
        {
            if (gc is not { ConstraintType: ConstraintKind.Obeys, ConstraintTypes: not null })
            {
                continue;
            }

            BindObeyingConstraintTypeExpressions(constraintTypes: gc.ConstraintTypes,
                substitutions: substitutions);
        }
    }

    /// <summary>
    /// For each constraint type expression in an <c>obeys</c> constraint, resolves the protocol
    /// and populates the substitution dict with its generic parameter→argument mappings.
    /// </summary>
    private void BindObeyingConstraintTypeExpressions(IEnumerable<TypeExpression> constraintTypes,
        Dictionary<string, TypeSymbol> substitutions)
    {
        foreach (TypeExpression ce in constraintTypes)
        {
            TypeSymbol resolvedConstraint = _typeResolver.ResolveType(typeExpr: ce);
            if (resolvedConstraint is not ProtocolTypeInfo rcProto ||
                rcProto.TypeArguments is not { Count: > 0 } cArgs)
            {
                continue;
            }

            ProtocolTypeInfo rcDef = rcProto.GenericDefinition ?? rcProto;
            if (rcDef.GenericParameters is not { Count: > 0 } cParams)
            {
                continue;
            }

            for (int i = 0; i < cParams.Count && i < cArgs.Count; i++)
            {
                substitutions[key: cParams[index: i]] = cArgs[index: i];
            }
        }
    }

    private void SelectMemberOverloadByArgumentTypes(CallExpression call, string callLookupName,
        TypeInfo dispatchType, ref RoutineInfo? memberRoutine, bool ambiguousSeed)
    {
        if (memberRoutine is not { IsGenericDefinition: false } || call.Arguments.Count == 0)
        {
            return;
        }

        List<TypeSymbol> resolvedArgTypes = ResolveMemberCallArgTypes(call: call,
            memberRoutine: memberRoutine,
            dispatchType: dispatchType);

        bool arityMismatch = memberRoutine.Parameters.Count != resolvedArgTypes.Count;
        bool firstArgMismatch = !arityMismatch && memberRoutine.Parameters.Count > 0 &&
                                resolvedArgTypes.Count > 0 &&
                                !IsAssignableTo(source: resolvedArgTypes[index: 0],
                                    target: memberRoutine.Parameters[index: 0].Type);

        if (!arityMismatch && !firstArgMismatch && !ambiguousSeed)
        {
            return;
        }

        RoutineInfo? betterMemberRoutine =
            _registry.LookupMemberRoutineOverload(type: dispatchType,
                memberRoutineName: callLookupName,
                argTypes: resolvedArgTypes);
        if (betterMemberRoutine != null)
        {
            memberRoutine = betterMemberRoutine;
        }
    }

    /// <summary>
    /// Analyzes each call argument against its expected parameter type (with owner-generic
    /// substitution applied) and returns the list of resolved argument types, skipping
    /// error-typed args.
    /// </summary>
    private List<TypeSymbol> ResolveMemberCallArgTypes(CallExpression call,
        RoutineInfo memberRoutine, TypeInfo dispatchType)
    {
        var resolvedArgTypes = new List<TypeSymbol>(capacity: call.Arguments.Count);
        int posIdx = 0;
        foreach (Expression arg in call.Arguments)
        {
            Expression actualArg = arg is NamedArgumentExpression named ? named.Value : arg;
            TypeSymbol? expectedParamType = ResolveMemberCallArgExpectedType(arg: arg,
                posIdx: posIdx,
                memberRoutine: memberRoutine);

            if (expectedParamType != null && memberRoutine.OwnerType is { IsGenericDefinition: true })
            {
                expectedParamType = SubstituteOwnerGenerics(paramType: expectedParamType,
                    lookupType: dispatchType,
                    ownerType: memberRoutine.OwnerType) ?? expectedParamType;
            }

            TypeSymbol argType = AnalyzeExpression(expression: actualArg,
                expectedType: expectedParamType);
            if (argType != ErrorTypeInfo.Instance)
            {
                resolvedArgTypes.Add(item: argType);
            }

            posIdx++;
        }

        return resolvedArgTypes;
    }

    /// <summary>
    /// Resolves the expected parameter type for a single call argument, using named-argument
    /// lookup or positional index into the routine's parameter list.
    /// </summary>
    private static TypeSymbol? ResolveMemberCallArgExpectedType(Expression arg, int posIdx,
        RoutineInfo memberRoutine)
    {
        if (arg is NamedArgumentExpression namedArg)
        {
            ParameterInfo? p = memberRoutine.Parameters.FirstOrDefault(predicate: pp =>
                pp.Name == namedArg.Name);
            return p?.Type;
        }

        return posIdx < memberRoutine.Parameters.Count
            ? memberRoutine.Parameters[index: posIdx].Type
            : null;
    }

    private void SynthesizeMemberVariantOnDemand(CallExpression call,
        bool isFailableMemberRoutineCall, string callLookupName, TypeInfo dispatchType,
        ref RoutineInfo? memberRoutine, ref bool ambiguousSeed)
    {
        if (memberRoutine == null)
        {
            var seedCandidates = new List<RoutineInfo>();
            _registry.CollectMemberRoutineCandidates(type: dispatchType,
                memberRoutineName: callLookupName,
                candidates: seedCandidates);
            if (seedCandidates.Count > 1)
            {
                var seedNames = call.Arguments
                                    .OfType<NamedArgumentExpression>()
                                    .Select(selector: n => n.Name)
                                    .ToList();
                var arityMatches = seedCandidates.Where(predicate: c =>
                                                      c.Parameters.Count == call.Arguments.Count &&
                                                      seedNames.All(predicate: n =>
                                                          c.Parameters.Any(predicate: p =>
                                                              p.Name == n)))
                                                 .ToList();
                var failMatches = arityMatches
                                 .Where(predicate: c =>
                                      c.IsFailable == isFailableMemberRoutineCall)
                                 .ToList();
                List<RoutineInfo> pick = failMatches.Count > 0
                    ? failMatches
                    : arityMatches;
                if (pick.Count == 1)
                {
                    memberRoutine = pick[index: 0];
                }
                else if (pick.Count > 1)
                {
                    memberRoutine = pick[index: 0];
                    ambiguousSeed = true;
                }
            }
        }
    }

    private void ResolveConstrainedMemberRoutine(bool isFailableMemberRoutineCall,
        string callLookupName, TypeInfo dispatchType, ref RoutineInfo? memberRoutine)
    {
        if (memberRoutine == null && dispatchType is GenericParameterTypeInfo genParam)
        {
            var constraints = ActiveConstraintsFor(paramName: genParam.Name)
               .ToList();
            memberRoutine = _registry.LookupMemberRoutineViaConstraints(param: genParam,
                memberRoutineName: callLookupName,
                isFailable: isFailableMemberRoutineCall,
                constraints: constraints,
                protocolResolver: LookupTypeWithImports);
            if (memberRoutine == null && !isFailableMemberRoutineCall)
            {
                memberRoutine = _registry.LookupMemberRoutineViaConstraints(param: genParam,
                    memberRoutineName: callLookupName,
                    isFailable: true,
                    constraints: constraints,
                    protocolResolver: LookupTypeWithImports);
            }
        }
    }

    private void ResolveTransparentMemberRoutine(TypeInfo objectType,
        bool isFailableMemberRoutineCall, string callLookupName, ref TypeInfo dispatchType,
        ref RoutineInfo? memberRoutine)
    {
        if (memberRoutine == null &&
            TryUnwrapMarkerReceiver(type: objectType, innerType: out TypeSymbol target))
        {
            dispatchType = target;
            memberRoutine = _registry.LookupMemberRoutine(type: dispatchType,
                memberRoutineName: callLookupName,
                isFailable: isFailableMemberRoutineCall);
            if (memberRoutine == null && !isFailableMemberRoutineCall)
            {
                memberRoutine = _registry.LookupMemberRoutine(type: dispatchType,
                    memberRoutineName: callLookupName,
                    isFailable: true);
            }
        }
    }

    private ErrorTypeInfo? ValidateDirectWiredMemberCall(CallExpression call,
        MemberExpression member)
    {
        if ((member.MemberName == "iter" || member.MemberName == "access" ||
             member.MemberName == "control") && !call.IsSynthesizedLowering &&
            !IsStdlibFile(filePath: call.Location.FileName))
        {
            string hint = member.MemberName == "iter"
                ? "use an 'each' loop or iterable combinators (skip, take, map, etc.) instead."
                : "pass the value to a routine whose parameter is typed " +
                  "Accessing[T] / Controlling[T] — the compiler coerces it for you.";
            ReportError(code: SemanticDiagnosticCode.DirectWiredRoutineCall,
                message:
                $"member routine '{member.MemberName}' is internal to the compiler — {hint}",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        return null;
    }

    private TypeSymbol? AnalyzeComptimeHandleCall(CallExpression call, MemberExpression member,
        TypeInfo objectType)
    {
        if (objectType is ComptimeHandleTypeInfo)
        {
            if (member.MemberName == "obeying" && call.Arguments is [IdentifierExpression])
            {
                return _registry.LookupType(name: "Bool") ?? ErrorTypeInfo.Instance;
            }

            ReportError(code: SemanticDiagnosticCode.MemberNotFound,
                message: $"Comptime expand handle has no call '{member.MemberName}(...)'. " +
                         "Available: 'obeying(Protocol)' -> Bool.",
                location: call.Location);
            return ErrorTypeInfo.Instance;
        }

        return null;
    }

    private TypeSymbol? AnalyzeImportedModuleCall(CallExpression call, MemberExpression member)
    {
        if (member.Object is IdentifierExpression moduleRef &&
            _registry.LookupVariable(name: moduleRef.Name) == null &&
            (_currentModuleName == null ||
             _registry.LookupVariable(name: $"{_currentModuleName}.{moduleRef.Name}") == null) &&
            LookupTypeWithImports(name: moduleRef.Name) == null)
        {
            bool modFailable = member.IsFailable;
            string modName = member.MemberName;
            RoutineInfo? modRoutine = ResolveModuleQualifiedRoutine(moduleRef: moduleRef.Name,
                routineName: modName,
                isFailable: modFailable,
                location: call.Location,
                ambiguous: out bool ambiguous);
            if (ambiguous)
            {
                return ErrorTypeInfo.Instance;
            }

            if (modRoutine is { OwnerType: null })
            {
                return AnalyzeModuleQualifiedRoutineCall(call: call, routine: modRoutine);
            }
        }

        return null;
    }

    private TypeSymbol? AnalyzeFallbackFreeRoutine(CallExpression call, IdentifierExpression id,
        RoutineInfo? routine)
    {
        if (routine == null)
        {
            return null;
        }

        // Realm gate: a foreign routine (C extern / LLVM intrinsic) must be called via its
        // `C::`/`LLVM::` qualifier, and a `C::`/`LLVM::` qualifier must name a matching realm.
        CheckCallRealm(callee: id, routine: routine, location: call.Location);

        if (CheckUnresolvableGenericRoutine(call: call, routine: routine))
        {
            return ErrorTypeInfo.Instance;
        }

        call.ResolvedRoutine = routine;
        call.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);

        // Standalone BuilderQuery routines are plain `module BuilderQuery` members now:
        // normal import scoping gates them (no import → UnknownIdentifier), so no bespoke
        // import-required diagnostic here. (Per-type reflection routines keep their gate.)

        TrackFailableMemberRoutineCall(memberRoutine: routine, location: call.Location);

        ValidateRoutineAccess(routine: routine,
            accessLocation: call.Location,
            isCompilerSynthesized: call.IsSynthesizedLowering);
        AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        TypeSymbol returnType = routine.ReturnType ??
                                _registry.LookupType(name: NoneTypeName) ??
                                ErrorTypeInfo.Instance;
        call.IsInFlight = routine.IsInFlightReturn;
        return returnType;
    }

    private void RecoverFallbackFreeOverload(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is { IsVariadic: false } && call.Arguments.Count > routine.Parameters.Count)
        {
            RoutineInfo? variadicGeneric = _registry.LookupVariadicGenericOverload(name: callName);
            if (variadicGeneric != null)
            {
                List<TypeInfo>? inferred =
                    InferGenericTypeArguments(genericRoutine: variadicGeneric,
                        arguments: call.Arguments);
                routine = inferred != null
                    ? _registry.GetOrCreateRoutineResolution(genericDef: variadicGeneric,
                        typeArguments: inferred)
                    : variadicGeneric;
                call.ResolvedRoutine = routine;
            }
        }
    }

    private void ResolveFallbackFreeOverload(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is not { IsGenericDefinition: false } || call.Arguments.Count == 0 ||
            routine.Parameters.Count == 0)
        {
            return;
        }

        Expression firstArgImport =
            call.Arguments[index: 0] is NamedArgumentExpression naImport
                ? naImport.Value
                : call.Arguments[index: 0];
        TypeSymbol firstArgTypeImport = AnalyzeExpression(expression: firstArgImport);
        TypeSymbol firstParamTypeImport = routine.Parameters[index: 0].Type;
        if (firstArgTypeImport == ErrorTypeInfo.Instance ||
            firstArgTypeImport.FullName == firstParamTypeImport.FullName ||
            IsAssignableTo(source: firstArgTypeImport, target: firstParamTypeImport))
        {
            return;
        }

        // Collect all resolved arg types for better overload disambiguation
        var resolvedArgTypesImport = new List<TypeSymbol> { firstArgTypeImport };
        for (int i = 1; i < call.Arguments.Count; i++)
        {
            Expression actualArgImport =
                call.Arguments[index: i] is NamedArgumentExpression naiImport
                    ? naiImport.Value
                    : call.Arguments[index: i];
            TypeSymbol argTypeImport = AnalyzeExpression(expression: actualArgImport);
            if (argTypeImport != ErrorTypeInfo.Instance)
            {
                resolvedArgTypesImport.Add(item: argTypeImport);
            }
        }

        // Try module-qualified specific overload (e.g., "IO.show#S64")
        RoutineInfo? betterImport =
            _registry.LookupRoutineOverload(baseName: routine.BaseName,
                argTypes: resolvedArgTypesImport);
        if (betterImport != null && betterImport != routine)
        {
            routine = betterImport;
            call.ResolvedRoutine = routine;
            return;
        }

        // `LookupGenericOverload` returns the first same-name overload; it cannot
        // choose among generic overloads that differ only in PARAMETER TYPE (e.g.
        // `when_interrupted[T, P](Guarded[T, P])` vs `when_interrupted[T](Roamed[T])`).
        // If the first pick does not unify, try the sibling overloads of matching arity.
        RoutineInfo? genericImport = _registry.LookupGenericOverload(name: callName);
        if (genericImport == null)
        {
            return;
        }

        List<TypeInfo>? inferredImport = InferGenericTypeArguments(genericRoutine: genericImport,
            arguments: call.Arguments);
        if (inferredImport == null)
        {
            TryInferFromGenericSiblings(call: call,
                generic: ref genericImport,
                inferred: ref inferredImport);
        }

        routine = inferredImport != null
            ? _registry.GetOrCreateRoutineResolution(genericDef: genericImport,
                typeArguments: inferredImport)
            : genericImport;
        call.ResolvedRoutine = routine;
    }

    private void InferFallbackFreeTypeArguments(CallExpression call, TypeInfo? expectedType,
        ref RoutineInfo? routine)
    {
        if (routine is not { IsGenericDefinition: true } ||
            call.TypeArguments is { Count: > 0 } ||
            routine.GenericParameters is not { Count: > 0 } ||
            call.Arguments.Count != routine.Parameters.Count)
        {
            return;
        }

        List<TypeInfo>? inferredImportGen = InferGenericTypeArguments(genericRoutine: routine,
            arguments: call.Arguments,
            expectedType: expectedType);

        // First-wins by name+arity cannot choose among generic overloads that differ only in
        // PARAMETER TYPE (e.g. `when_interrupted[T, P](Guarded[T, P])` vs
        // `when_interrupted[T](Roamed[T])`). If the initial pick does not unify, try the
        // sibling overloads and take the one that does.
        if (inferredImportGen == null)
        {
            TryInferFromGenericSiblingsWithExpected(call: call,
                expectedType: expectedType,
                generic: ref routine,
                inferred: ref inferredImportGen);
        }

        if (inferredImportGen != null)
        {
            routine = _registry.GetOrCreateRoutineResolution(
                genericDef: routine,
                typeArguments: inferredImportGen);
        }
    }

    /// <summary>
    /// Variant of <see cref="TryInferFromGenericSiblings"/> that passes an expected-type hint to
    /// <see cref="InferGenericTypeArguments"/>. Used by the fallback inference path where a
    /// return-type-only generic may need a contextual type to bind its parameters.
    /// </summary>
    private void TryInferFromGenericSiblingsWithExpected(CallExpression call,
        TypeInfo? expectedType, ref RoutineInfo generic, ref List<TypeInfo>? inferred)
    {
        foreach (RoutineInfo sibling in _registry.GenericOverloadsByArity(
                     name: generic.Name,
                     arity: call.Arguments.Count))
        {
            if (sibling == generic)
            {
                continue;
            }

            List<TypeInfo>? siblingInferred = InferGenericTypeArguments(
                genericRoutine: sibling,
                arguments: call.Arguments,
                expectedType: expectedType);
            if (siblingInferred != null)
            {
                generic = sibling;
                inferred = siblingInferred;
                break;
            }
        }
    }

    private void SelectFallbackGenericOverload(CallExpression call, string callName,
        ref RoutineInfo? routine)
    {
        if (routine is { IsGenericDefinition: true, IsVariadic: false } &&
            (call.TypeArguments == null || call.TypeArguments.Count == 0) &&
            routine.Parameters.Count != call.Arguments.Count)
        {
            RoutineInfo? arityGeneric =
                _registry.LookupGenericOverload(name: callName,
                    preferredArity: call.Arguments.Count) ??
                _registry.LookupGenericOverload(name: routine.BaseName,
                    preferredArity: call.Arguments.Count);
            if (arityGeneric is { IsVariadic: false } &&
                arityGeneric.Parameters.Count == call.Arguments.Count)
            {
                routine = arityGeneric;
            }
        }
    }

    private TypeSymbol? AnalyzeNamedTypeConstruction(CallExpression call, IdentifierExpression id,
        TypeInfo? type)
    {
        if (type == null)
        {
            return null;
        }

        call.ConstructedType = type;
        call.LoweringKind = ClassifyConstruction(type: type,
            isCollectionLiteral: call.IsCollectionLiteral);

        List<TypeSymbol> argTypes = AnalyzeNamedTypeConstructionArgs(call: call, type: type);

        // C95: Try create overload match first
        // e.g., BitList(capacity: 32u64) -> BitList.create(capacity: U64)
        // e.g., BitList(32u64) -> BitList.create(capacity: U64) instead of collection literal
        if (call.Arguments.Count > 0)
        {
            RoutineInfo? creator = _registry.LookupCreatorOverload(type: type, argTypes: argTypes);

            if (creator != null && creator.Parameters.Count == argTypes.Count &&
                !creator.Parameters.Any(predicate: p => p.IsVariadicParam))
            {
                call.ResolvedRoutine = creator;
                call.LoweringKind = ClassifyConstruction(type: type,
                    isCollectionLiteral: call.IsCollectionLiteral);
                call.IsInFlight = creator.IsInFlightReturn;
                return creator.ReturnType ?? type;
            }

            // Entity types can only be constructed via create — no fallback
            if (type is EntityTypeInfo)
            {
                ReportError(code: SemanticDiagnosticCode.TypeNotCallable,
                    message:
                    $"No matching 'create' overload found for entity type '{type.Name}' " +
                    $"with {argTypes.Count} argument(s).",
                    location: call.Location);
            }
        }

        ValidateConstructorArgumentNaming(call: call, id: id, type: type);
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);
        call.IsInFlight = type.ImplicitConstructorReturnsInFlight;

        return type;
    }

    /// <summary>
    /// Analyzes each argument of a named-type construction call (<c>Type(args…)</c>), supplying
    /// the matching field type as the contextual expected type for literal adaptation and generic
    /// binding. Returns the list of analyzed argument types.
    /// </summary>
    private List<TypeSymbol> AnalyzeNamedTypeConstructionArgs(CallExpression call, TypeInfo type)
    {
        // Analyze all arguments once before branching. Variant construction auto-wraps
        // the argument into the variant, so its contextual type is the variant itself
        // (lets a bare `none` argument resolve to the variant's None arm).
        TypeSymbol? variantArgContext = type is VariantTypeInfo ? type : null;
        // Both entity AND record targets do inline field-init construction here — gating on
        // EntityTypeInfo alone left a RECORD constructor's arg with a null expected type, so
        // a bare `1` reset to the Suflae `Integer` default (this is the SECOND, final ctor
        // block: it re-analyzes and would clobber the first block's conformance → the record
        // literal ended up Integer → codegen `Integer`-into-`i64` / pruned from_literal).
        List<MemberVariableInfo>? ctorMemberVariables = type switch
        {
            EntityTypeInfo entityCtorType => entityCtorType.MemberVariables,
            RecordTypeInfo recordCtorType => recordCtorType.MemberVariables,
            _ => null
        };
        var argTypes = new List<TypeSymbol>();
        int ctorPosIdx = 0;
        foreach (Expression arg in call.Arguments)
        {
            TypeSymbol? argExpected = variantArgContext;
            if (argExpected == null && ctorMemberVariables != null)
            {
                MemberVariableInfo? field = ResolveCtorField(arg: arg,
                    posIdx: ctorPosIdx,
                    ctorMemberVariables: ctorMemberVariables);
                argExpected = field?.Type;
                if (argExpected != null && type is
                        { IsGenericResolution: true, TypeArguments: not null })
                {
                    argExpected = SubstituteTypeParameters(type: argExpected, genericType: type);
                }
            }

            argTypes.Add(item: AnalyzeExpression(expression: arg, expectedType: argExpected));
            ctorPosIdx++;
        }

        return argTypes;
    }

    /// <summary>
    /// Enforces the named-argument rules for type constructors: RF-S510 (3+ fields → all named,
    /// error) and the related recommendation (2 fields → named preferred, warning).
    /// </summary>
    private void ValidateConstructorArgumentNaming(CallExpression call, IdentifierExpression id,
        TypeInfo type)
    {
        // S510: Type creators with 3+ fields require all named arguments.
        // W258: For 2 fields, naming is recommended but only emits a warning.
        int memberCount = type switch
        {
            EntityTypeInfo e => e.MemberVariables.Count,
            RecordTypeInfo r => r.MemberVariables.Count,
            _ => 0
        };
        if (memberCount >= 3)
        {
            foreach (Expression arg in call.Arguments.Where(predicate: a =>
                         a is not NamedArgumentExpression))
            {
                ReportError(code: SemanticDiagnosticCode.NamedArgumentRequired,
                    message:
                    $"Type '{id.Name}' has {memberCount} fields - all constructor arguments must be named.",
                    location: arg.Location);
            }
        }
        else if (memberCount == 2)
        {
            foreach (Expression arg in call.Arguments.Where(predicate: a =>
                         a is not NamedArgumentExpression))
            {
                ReportWarning(code: SemanticWarningCode.NamedArgumentRecommended,
                    message:
                    $"Type '{id.Name}' has 2 fields - naming constructor arguments is recommended for clarity.",
                    location: arg.Location);
            }
        }
    }

    private TypeSymbol? AnalyzeResolvedFreeRoutine(CallExpression call, IdentifierExpression id,
        RoutineInfo? routine)
    {
        if (routine == null)
        {
            return null;
        }

        // Realm gate: a foreign routine (C extern / LLVM intrinsic) must be called via its
        // `C::`/`LLVM::` qualifier, and a `C::`/`LLVM::` qualifier must name a matching realm.
        CheckCallRealm(callee: id, routine: routine, location: call.Location);

        if (CheckUnresolvableGenericRoutine(call: call, routine: routine))
        {
            return ErrorTypeInfo.Instance;
        }

        call.ResolvedRoutine = routine;
        call.LoweringKind = ClassifyStandaloneRoutineCall(routine: routine);

        // Standalone BuilderQuery routines are plain `module BuilderQuery` members now:
        // normal import scoping gates them (no import → UnknownIdentifier), so no bespoke
        // import-required diagnostic here. (Per-type reflection routines keep their gate.)

        TrackFailableMemberRoutineCall(memberRoutine: routine, location: call.Location);

        ValidateRoutineAccess(routine: routine,
            accessLocation: call.Location,
            isCompilerSynthesized: call.IsSynthesizedLowering);

        AnalyzeCallArguments(routine: routine, arguments: call.Arguments, location: call.Location);
        ValidateExclusiveTokenUniqueness(arguments: call.Arguments, location: call.Location);

        TypeSymbol returnType = routine.ReturnType ??
                                _registry.LookupType(name: NoneTypeName) ??
                                ErrorTypeInfo.Instance;
        call.IsInFlight = routine.IsInFlightReturn;

        return WrapAsyncReturnType(call: call, routine: routine, returnType: returnType);
    }

    /// <summary>
    /// Returns true (and reports RF-S161) when a generic routine could not be instantiated:
    /// no explicit type args were supplied, arity matches, but inference left a type parameter
    /// unbound (e.g. a return-only <c>To</c> with no expected-type context).
    /// </summary>
    private bool CheckUnresolvableGenericRoutine(CallExpression call, RoutineInfo routine)
    {
        // Inference guard: if the routine is STILL a generic definition here — no explicit
        // `[...]` args and matching arity, yet none of the inference/overload passes above
        // instantiated it — then some type parameter (e.g. a return-only `To` with no
        // expected-type context) could not be bound. Report cleanly instead of letting a
        // call whose return type is an abstract type parameter reach codegen and crash.
        if (routine is not { IsGenericDefinition: true } ||
            call.TypeArguments is { Count: > 0 } ||
            call.Arguments.Count != routine.Parameters.Count)
        {
            return false;
        }

        string genericNames = string.Join(separator: ", ",
            values: routine.GenericParameters ?? []);
        ReportError(code: SemanticDiagnosticCode.CannotInferTypeArgument,
            message:
            $"Cannot infer type argument(s) [{genericNames}] for generic routine " +
            $"'{routine.BaseName}' from this call. Specify them explicitly, e.g. " +
            $"{routine.BaseName}[{genericNames}](...).",
            location: call.Location);
        return true;
    }

    /// <summary>
    /// For <c>threaded</c> and <c>suspended</c> routines, validates async-boundary argument
    /// restrictions and wraps the plain return type in <c>Agent[T]</c>. Returns the plain
    /// <paramref name="returnType"/> unchanged for synchronous routines.
    /// </summary>
    private TypeSymbol WrapAsyncReturnType(CallExpression call, RoutineInfo routine,
        TypeSymbol returnType)
    {
        // A `threaded routine` call spawns an OS thread and yields an `Agent[T]`
        // handle (T = the routine's own return type, kind THREAD). The handle is awaited
        // via the stdlib `Agent[T].retrieve!()` / `.waitfor(deadline)` memberRoutines.
        if (routine.AsyncStatus == AsyncStatus.Threaded)
        {
            ValidateAsyncRoutineArguments(routine: routine,
                arguments: call.Arguments,
                boundaryKind: "threaded",
                location: call.Location);
            TypeSymbol? agentDef = _registry.LookupType(name: "Agent");
            return agentDef != null
                ? _registry.GetOrCreateResolution(genericDef: agentDef,
                    typeArguments: [returnType])
                : returnType;
        }

        // A `suspended routine` call creates a stackful coroutine and yields an
        // `Agent[T]` handle (kind CORO), driven to completion via `Agent[T].retrieve!()`.
        // Under M:N a coroutine may run on any worker in parallel with its siblings, so
        // the same crossing rule as `threaded` applies to its arguments (RF-S632).
        if (routine.AsyncStatus == AsyncStatus.Suspended)
        {
            ValidateAsyncRoutineArguments(routine: routine,
                arguments: call.Arguments,
                boundaryKind: "suspended",
                location: call.Location);
            TypeSymbol? agentDef = _registry.LookupType(name: "Agent");
            return agentDef != null
                ? _registry.GetOrCreateResolution(genericDef: agentDef,
                    typeArguments: [returnType])
                : returnType;
        }

        return returnType;
    }
}
