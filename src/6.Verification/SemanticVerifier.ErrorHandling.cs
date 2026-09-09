using Compiler.Diagnostics;
using SyntaxTree;
using TypeModel.Symbols;
using TypeModel.Types;
using System.Text;

using Compiler.Instantiation;

namespace Compiler.Verification;

using TypeSymbol = TypeInfo;

/// <summary>
/// Phase 5: Error handling variant support.
///
/// RazorForge/Suflae error handling model:
/// - Failable functions end with ! suffix (e.g., parse!, connect!)
/// - throw statement: signals a failure with an error value
/// - absent statement: signals "not found" without error
///
/// Variant generation rules:
/// - Only absent: try_ (returns T? -> None on absent)
/// - Only throw: try_ (returns T? -> None on throw) + check_ (returns Result&lt;T&gt;)
/// - Both throw and absent: try_ + lookup_ (returns Lookup&lt;T&gt;)
///
/// The actual variant generation is delegated to <see cref="ErrorHandlingVariantPass"/>
/// which runs in Phase 6 (global desugaring) after body analysis populates <c>_routineBodies</c>.
/// </summary>
public sealed partial class SemanticVerifier
{
    #region Phase 5: Error Handling Body Collection

    /// <summary>
    /// Storage for routine bodies needed during variant generation.
    /// Maps RoutineInfo.RegistryKey to its body statement.
    /// Populated during body analysis; consumed by ErrorHandlingVariantPass in Phase 6.
    /// </summary>
    private readonly Dictionary<string, Statement> _routineBodies = new();

    /// <summary>
    /// Stores a routine body for later variant generation.
    /// Called during Phase 4 body analysis.
    /// </summary>
    /// <param name="routine">The routine whose body is being stored.</param>
    /// <param name="body">The routine's body statement.</param>
    private void StoreRoutineBody(RoutineInfo routine, Statement body)
    {
        _routineBodies[key: routine.RegistryKey] = body;
    }

    /// <summary>
    /// Phase 6 pre-pass: Pre-register error handling variant stubs for user-defined failable routines.
    /// Called before Phase 5 body analysis so that try_/check_/lookup_ variants are in scope
    /// when user code calls them from within the same module.
    /// Uses AST-level throw/absent detection -> no full semantic analysis required.
    /// </summary>
    internal void PreRegisterUserVariants(Program program)
    {
        var generator = new ErrorHandlingGenerator(registry: _registry);
        string? currentModule = GetCurrentModuleName();

        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not RoutineDeclaration routineDecl || !routineDecl.IsFailable ||
                routineDecl.Body == null)
            {
                continue;
            }

            PreRegisterVariantsForDeclaration(generator: generator, decl: routineDecl,
                module: currentModule);
        }

        ScanReservedPrefixCollisions(program: program, module: currentModule);
    }

    /// <summary>
    /// EAGER reserved-prefix collision detection (RF-S409), independent of on-demand variant generation:
    /// a hand-written routine named <c>try_X</c>/<c>check_X</c>/<c>lookup_X</c> whose stripped base <c>X</c>
    /// is a failable routine occupies a slot reserved for the compiler's generated variant. Under the
    /// on-demand model the variant is only synthesized when called, so this collision must be found by
    /// scanning the hand-written routines directly rather than as a side effect of generation.
    /// </summary>
    private void ScanReservedPrefixCollisions(Program program, string? module)
    {
        foreach (ISyntaxTreeNode node in program.Declarations)
        {
            if (node is not RoutineDeclaration decl) continue;
            if (!TrySplitVariantName(variantName: decl.Name, baseName: out string baseName)) continue;

            RoutineInfo? handWritten = ResolveRoutineInfoForDeclaration(decl: decl, moduleName: module);
            if (handWritten is null or { IsSynthesized: true }) continue;

            RoutineInfo? baseRoutine = FindBaseRoutineForVariantName(handWritten: handWritten,
                baseName: baseName, module: module);
            if (baseRoutine is { IsFailable: true })
                CheckReservedVariantCollision(baseRoutine: baseRoutine, variant: handWritten);
        }
    }

    /// <summary>
    /// Looks up the failable base routine for a hand-written <c>try_</c>/<c>check_</c>/<c>lookup_</c>
    /// routine: checks the member-routine table when the hand-written routine has an owner type, otherwise
    /// searches free routines (bare name first, then module-qualified if module is known and the name is unqualified).
    /// </summary>
    private RoutineInfo? FindBaseRoutineForVariantName(RoutineInfo handWritten, string baseName, string? module)
    {
        if (handWritten.OwnerType != null)
        {
            return _registry.LookupMemberRoutine(type: handWritten.OwnerType,
                memberRoutineName: baseName, isFailable: true);
        }

        RoutineInfo? found = _registry.LookupRoutine(fullName: baseName, isFailable: true);
        if (found != null) return found;
        if (module != null && !baseName.Contains(value: '.'))
        {
            return _registry.LookupRoutine(fullName: $"{module}.{baseName}", isFailable: true);
        }

        return null;
    }

    /// <summary>
    /// Resolves a failable routine declaration and registers its <c>try_</c>/<c>check_</c>/<c>lookup_</c>
    /// variant stubs (checking each for a reserved-prefix collision). Shared by
    /// <see cref="PreRegisterUserVariants"/> and <see cref="PreRegisterStdlibVariants"/>. A routine with
    /// direct <c>throw</c>/<c>absent</c> gets precise variants; a propagated-failability routine gets
    /// pessimistic try_+lookup_ stubs so call sites resolve during SA. Skips non-failable or
    /// <c>crash_only</c> routines and generation errors.
    /// </summary>
    private void PreRegisterVariantsForDeclaration(ErrorHandlingGenerator generator,
        RoutineDeclaration decl, string? module)
    {
        // AST scan: routines with direct throw/absent get precise variants.
        // Routines without any (propagated-failability via called `!` routines) get
        // pessimistic try_+lookup_ stubs so callsites can resolve them during SA.
        bool hasDirect = ErrorHandlingGenerator.BodyHasThrowOrAbsent(body: decl.Body!);

        RoutineInfo? routineInfo =
            ResolveRoutineInfoForDeclaration(decl: decl, moduleName: module);
        if (routineInfo == null || !routineInfo.IsFailable) return;
        if (routineInfo.Annotations.Contains(item: "crash_only")) return;

        // Record the base routine's body for ON-DEMAND variant synthesis: a call to
        // try_X/check_X/lookup_X that misses registry lookup during Phase 5 resolves by synthesizing
        // this base's variants here (TrySynthesizeVariantOnDemand). Keyed by the base RegistryKey.
        // Non-emit variants are LAZY: only the base is indexed; the try_/check_/lookup_ variant is
        // synthesized the first time a call site looks it up (TrySynthesizeVariantOnDemand).
        _registry.DeferredVariantBases[key: routineInfo.RegistryKey] = (routineInfo, decl.Body!, !hasDirect);

        // ITERATOR `emit` stays EAGER and is owned END-TO-END by the existing pipeline (for-loop desugar
        // synthesizes `iter.try_emit()`; Phase-8 monomorphization path-2 generates each composed emitter's
        // try_emit BODY). The on-demand hook deliberately SKIPS `emit` (see TrySplitVariantName): a stub-only
        // on-demand `try_emit` would have no body and be pruned (over-prune), and on the warm path — where
        // PreRegisterStdlibVariants is skipped — the generic-def emit variants are RESTORED from the snapshot,
        // so resolution + path-2 monomorphization proceed without the hook. `emit` is a bounded set; the
        // COMBINATORIAL failable surface (every `foo!` → try_/check_/lookup_) is what stays lazy.
        if (routineInfo.Name != "emit") return;
        ErrorHandlingResult result = generator.GenerateVariants(
            routine: routineInfo, body: decl.Body!, pessimistic: !hasDirect);
        if (result.Error != null) return;
        foreach (GeneratedVariant variant in result.Variants)
        {
            CheckReservedVariantCollision(baseRoutine: routineInfo, variant: variant.Routine);
            _registry.RegisterRoutine(routine: variant.Routine);
        }
    }

    // The deferred-variant index itself lives on the registry (TypeRegistry.DeferredVariantBases) so it
    // rides the stdlib snapshot — a snapshot-restored build skips pre-registration, so the index must be
    // captured/restored to keep on-demand synthesis working.

    /// <summary>Base RegistryKeys whose variants have already been synthesized+registered on demand
    /// (memoization — a base's variants are generated once regardless of how many call sites hit it).
    /// Per-run state (not captured): a fresh verifier re-derives it as calls arrive.</summary>
    private readonly HashSet<string> _synthesizedVariantBases = new();

    private static readonly string[] VariantPrefixes = { "try_", "check_", "lookup_" };

    /// <summary>
    /// On-demand failable-variant synthesis. When a member call <c>x.try_foo()</c> /
    /// <c>check_foo()</c> / <c>lookup_foo()</c> misses the registry, strip the prefix, find the base
    /// failable <c>foo</c> on <paramref name="dispatchType"/>, and synthesize+register its variants
    /// from the deferred index — then return the variant that matches <paramref name="variantName"/>.
    /// Returns null when the name has no reserved prefix, no failable base exists, or the base was not
    /// recorded as a deferred variant base (e.g. <c>@crash_only</c>, which generates no variants).
    /// </summary>
    internal RoutineInfo? TrySynthesizeVariantOnDemand(TypeInfo dispatchType, string variantName)
    {
        if (!TrySplitVariantName(variantName: variantName, baseName: out string baseName)) return null;

        RoutineInfo? baseRoutine = _registry.LookupMemberRoutine(type: dispatchType,
            memberRoutineName: baseName, isFailable: true);
        if (baseRoutine is not { IsFailable: true }) return null;
        if (!EnsureVariantsSynthesizedForBase(baseRoutine: baseRoutine)) return null;

        // Resolve one by exact name.
        return _registry.LookupMemberRoutine(type: dispatchType, memberRoutineName: variantName,
            isFailable: false)
            ?? _registry.LookupMemberRoutine(type: dispatchType, memberRoutineName: variantName,
                isFailable: true);
    }

    /// <summary>
    /// On-demand failable-variant synthesis for a FREE routine call: <c>try_foo(...)</c> /
    /// <c>check_foo(...)</c> / <c>lookup_foo(...)</c> whose variant isn't registered — strip the prefix,
    /// find the base failable free routine <c>foo</c> (bare, then module-qualified), synthesize+register
    /// its variants from the deferred index, and return the one matching <paramref name="callName"/>.
    /// </summary>
    internal RoutineInfo? TrySynthesizeFreeVariantOnDemand(string callName)
    {
        if (!TrySplitVariantName(variantName: callName, baseName: out string baseName)) return null;

        RoutineInfo? baseRoutine = _registry.LookupRoutine(fullName: baseName, isFailable: true);
        if (baseRoutine == null && _currentModuleName != null && !baseName.Contains(value: '.'))
            baseRoutine = _registry.LookupRoutine(fullName: $"{_currentModuleName}.{baseName}",
                isFailable: true);
        if (baseRoutine is not { IsFailable: true }) return null;
        if (!EnsureVariantsSynthesizedForBase(baseRoutine: baseRoutine)) return null;

        // The variant name is always callName regardless of whether the base name matches (defensive).
        string variantFull = callName;
        return _registry.LookupRoutine(fullName: variantFull, isFailable: false)
            ?? _registry.LookupRoutine(fullName: variantFull, isFailable: true)
            ?? (_currentModuleName != null && !variantFull.Contains(value: '.')
                ? _registry.LookupRoutine(fullName: $"{_currentModuleName}.{variantFull}", isFailable: false)
                  ?? _registry.LookupRoutine(fullName: $"{_currentModuleName}.{variantFull}", isFailable: true)
                : null);
    }

    /// <summary>
    /// On-demand synthesis for a SPECIFIC base overload (the variant-body rewriter's hook): synthesizes
    /// <paramref name="baseOverload"/>'s variants and returns the one for <paramref name="prefix"/>, matched
    /// by the base's PARAMETER TYPES so an overloaded base (<c>S64.create(from_text:)</c> vs
    /// <c>create(from_int:)</c>) yields the correct overload's variant — a name-only lookup cannot.
    /// </summary>
    internal RoutineInfo? SynthesizeVariantForBase(RoutineInfo baseOverload, string prefix)
    {
        if (baseOverload.Name == "emit") return null; // owned by the eager/monomorphization pipeline
        if (!EnsureVariantsSynthesizedForBase(baseRoutine: baseOverload)) return null;

        // Return the EXACT variant generated for THIS overload (not a by-argType re-lookup).
        if (!_synthesizedVariantsByBase.TryGetValue(key: baseOverload.RegistryKey,
                out List<GeneratedVariant>? variants)
            && !(baseOverload.GenericDefinition is { } gd
                 && _synthesizedVariantsByBase.TryGetValue(key: gd.RegistryKey, out variants)))
            return null;

        foreach (GeneratedVariant v in variants)
        {
            string vprefix = v.Kind switch
            {
                ErrorHandlingVariantKind.Try or ErrorHandlingVariantKind.TryBool => "try",
                ErrorHandlingVariantKind.Check => "check",
                ErrorHandlingVariantKind.Lookup => "lookup",
                _ => ""
            };
            if (vprefix == prefix) return v.Routine;
        }
        return null;
    }

    /// <summary>Splits a <c>try_</c>/<c>check_</c>/<c>lookup_</c> name into its base name; false if the
    /// name carries no reserved variant prefix, the base is empty, or the base is the iterator <c>emit</c>
    /// (which the on-demand hook must NOT own — the for-loop desugar + Phase-8 monomorphization pipeline
    /// generates the composed emitters' try_emit bodies; a stub-only on-demand emit variant would be
    /// body-less and over-pruned, and would interfere with that pipeline on the warm path).</summary>
    private static bool TrySplitVariantName(string variantName, out string baseName)
    {
        baseName = "";
        string? prefix = VariantPrefixes.FirstOrDefault(predicate: variantName.StartsWith);
        if (prefix == null) return false;
        baseName = variantName[prefix.Length..];
        return baseName.Length > 0 && baseName != "emit";
    }

    /// <summary>
    /// Generates + registers the variants of a base failable routine from the deferred index, ONCE
    /// (memoized on the generic-def base key). Returns false if the base was never recorded as a deferred
    /// variant base (e.g. <c>@crash_only</c>). The index is keyed by the GENERIC-DEF base key
    /// (pre-registration ran over generic-def routines); a lookup on a monomorphized receiver returns a
    /// SUBSTITUTED base whose key differs, so fall back to the base's GenericDefinition key.
    /// </summary>
    private bool EnsureVariantsSynthesizedForBase(RoutineInfo baseRoutine)
    {
        if (!_registry.DeferredVariantBases.TryGetValue(key: baseRoutine.RegistryKey,
                value: out (RoutineInfo baseRoutine, Statement body, bool pessimistic) deferred)
            && !(baseRoutine.GenericDefinition is { } baseDef
                 && _registry.DeferredVariantBases.TryGetValue(key: baseDef.RegistryKey, value: out deferred)))
        {
            // Not in the pre-registered index (e.g. a `Type!(from_text:)` constructor whose declaration
            // resolves under a different key than its registered `create#…` overload). Its body is still in
            // the collected routine bodies (CollectStdlibBodiesForVariantGeneration) — synthesize from there.
            // WARM: stdlib routine bodies are NOT collected into _routineBodies (SkipStdlibReprocessing)
            // and PreRegisterStdlibVariants (which fills DeferredVariantBases) is skipped — so a STDLIB
            // base reached on demand by a USER variant body has no body here. The captured
            // stdlib bodies ARE available via `_warmStdlibRoutineBodies`; use them so warm can synthesize
            // the variant exactly as cold does — else the inner rewrite fails and the user variant calls
            // the raw failable form, crashing on the recoverable path.
            if (!_routineBodies.TryGetValue(key: baseRoutine.RegistryKey, value: out Statement? collectedBody)
                && (_warmStdlibRoutineBodies == null
                    || !_warmStdlibRoutineBodies.TryGetValue(key: baseRoutine.RegistryKey, value: out collectedBody)))
                return false;
            bool hasDirect = ErrorHandlingGenerator.BodyHasThrowOrAbsent(body: collectedBody);
            deferred = (baseRoutine, collectedBody, !hasDirect);
        }

        if (_synthesizedVariantBases.Add(item: deferred.baseRoutine.RegistryKey))
        {
            var generator = new ErrorHandlingGenerator(registry: _registry);
            ErrorHandlingResult result = generator.GenerateVariants(routine: deferred.baseRoutine,
                body: deferred.body, pessimistic: deferred.pessimistic);
            if (result.Error == null)
            {
                foreach (GeneratedVariant variant in result.Variants)
                {
                    CheckReservedVariantCollision(baseRoutine: deferred.baseRoutine,
                        variant: variant.Routine);
                    _registry.RegisterRoutine(routine: variant.Routine);
                }
                // Remember this base's EXACT variants so SynthesizeVariantForBase returns the precise
                // overload's variant (a by-argType re-lookup can pick the wrong overload — S64 args match an
                // S8 param via conversion). Keyed by BOTH the base overload's own key and the deferred key.
                _synthesizedVariantsByBase[key: baseRoutine.RegistryKey] = result.Variants;
                _synthesizedVariantsByBase[key: deferred.baseRoutine.RegistryKey] = result.Variants;
                // ENQUEUE body generation — do NOT generate here: this runs inside the LookupMemberRoutine
                // on-demand hook (re-entry-guarded), and generating a body re-looks-up its inner variants,
                // which must re-fire the hook. So bodies are built later by DrainVariantBodyGenQueue, OUTSIDE
                // the guard, where those transitive inner lookups synthesize freely.
                _variantBodyGenQueue.Enqueue(item: (deferred.body, result.Variants));
            }
        }

        return true;
    }

    /// <summary>Exact <see cref="GeneratedVariant"/>s per base RegistryKey, so a rewriter can retrieve the
    /// precise overload's variant instead of a lossy by-argType re-lookup.</summary>
    private readonly Dictionary<string, List<GeneratedVariant>> _synthesizedVariantsByBase = new();

    /// <summary>Bases whose variants are registered but whose bodies are not yet generated. Drained by
    /// <see cref="DrainVariantBodyGenQueue"/> before variant-body analysis.</summary>
    private readonly Queue<(Statement baseBody, List<GeneratedVariant> variants)> _variantBodyGenQueue = new();

    /// <summary>
    /// Generates the bodies of all on-demand-synthesized variants (the demand-driven replacement for
    /// ErrorHandlingVariantPass.TransformPendingBodies, which now only builds `emit` bodies). Each body is
    /// built via <see cref="Compiler.Instantiation.ErrorHandlingVariantPass.GenerateVariantBody"/>; its broad-propagation
    /// rewrite re-looks-up inner failable calls, whose on-demand hook enqueues MORE bases — so this drains
    /// until the queue is empty (transitive closure). Skips variants whose body is already present (a warm
    /// restore or the eager `emit` path).
    /// </summary>
    internal void DrainVariantBodyGenQueue()
    {
        while (_variantBodyGenQueue.Count > 0)
        {
            (Statement baseBody, List<GeneratedVariant> variants) = _variantBodyGenQueue.Dequeue();
            foreach (GeneratedVariant variant in variants)
            {
                string key = variant.Routine.RegistryKey;
                if (_variantBodies.ContainsKey(key: key) || _restoredVariantKeys.Contains(item: key))
                    continue;
                _variantBodies[key: key] = ErrorHandlingVariantPass.GenerateVariantBody(
                    baseBody: baseBody, variant: variant, registry: _registry);
            }
        }
    }

    /// <summary>Variant RegistryKeys already reported as collisions, to avoid duplicate RF-S409s
    /// when a pre-register pass runs over the same routine set more than once.</summary>
    private readonly HashSet<string> _reportedVariantCollisions = new();

    /// <summary>
    /// Reports RF-S409 when a hand-declared routine already occupies the exact slot
    /// (owner + name + signature) the compiler synthesizes for a failable variant
    /// (<c>try_</c>/<c>check_</c>/<c>lookup_</c>). The <see cref="RoutineInfo.RegistryKey"/>
    /// match is uniform across member and free routines. Only a genuine collision counts: a
    /// hand-written <c>try_lock</c> with no failable <c>lock!</c> base generates no variant, so
    /// it never reaches here — the reserved prefixes cost nothing until a colliding failable
    /// routine actually exists.
    /// </summary>
    private void CheckReservedVariantCollision(RoutineInfo baseRoutine, RoutineInfo variant)
    {
        // The variant hasn't been registered yet, so any occupant of its key is pre-existing.
        // Synthesized occupants (e.g. a stub from another pre-register pass) aren't collisions —
        // RegisterRoutine never lets a synthesized routine overwrite a user-written one, so a
        // non-synthesized occupant means a real hand-declared clash.
        string key = variant.RegistryKey;
        if (_registry.GetRoutineByExactKey(registryKey: key) is not { IsSynthesized: false } handWritten)
            return;

        SourceLocation? location = handWritten.Location ?? baseRoutine.Location;
        if (location == null || !_reportedVariantCollisions.Add(item: key)) return;

        ReportError(code: SemanticDiagnosticCode.ReservedRoutinePrefix,
            message:
            $"'{variant.Name}' collides with the variant the compiler generates for failable " +
            $"'{baseRoutine.Name}!'; the try_/check_/lookup_ prefixes are reserved for " +
            "compiler-generated failable variants — rename this routine",
            location: location);
    }

    /// <summary>
    /// Phase 3 global: pre-registers try_/check_/lookup_ stub variants for all failable stdlib
    /// member routines (e.g., Tracked[T].recover!, ListEmitter[T].emit!).
    /// Must run before Phase 4 user-body analysis so that user code calling these variants
    /// (e.g., <c>rt.try_recover()</c> or desugared for-loop <c>iter.try_emit()</c>) resolves
    /// without S450. Mirrors <see cref="PreRegisterUserVariants"/> but for stdlib programs.
    /// </summary>
    private void PreRegisterStdlibVariants()
    {
        var generator = new ErrorHandlingGenerator(registry: _registry);

        foreach ((Program program, _, string module) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || !decl.IsFailable || decl.Body == null)
                    continue;

                PreRegisterVariantsForDeclaration(generator: generator, decl: decl, module: module);
            }
        }

    }

    /// <summary>
    /// Collects stdlib member-routine bodies into <c>_routineBodies</c> keyed by
    /// <see cref="RoutineInfo.RegistryKey"/>. Stdlib routines aren't semantically analyzed
    /// (only registered), so <c>_routineBodies</c> would otherwise contain only user-side
    /// failable routines. Downstream passes need stdlib bodies too:
    /// <see cref="ErrorHandlingVariantPass"/> for failable iterators (e.g.
    /// <c>ListEmitter[T].emit!</c>) and <see cref="Compiler.Instantiation.Passes.ProtocolDefaultImplLoweringPass"/>
    /// for protocol-extension routines (e.g. <c>Iterable[Text].join</c>).
    /// Called before RunPhase4GlobalDesugaring() so the bodies are visible to both phases.
    /// </summary>
    private void CollectStdlibBodiesForVariantGeneration()
    {
        foreach ((Program program, _, string module) in _registry.StdlibPrograms)
        {
            foreach (ISyntaxTreeNode node in program.Declarations)
            {
                if (node is not RoutineDeclaration decl || decl.Body == null)
                    continue;

                // Only member routines: standalone free routines aren't candidates for
                // either variant generation or protocol-default-impl monomorphization.
                if (decl.MemberRoutineName is null)
                    continue;

                // Auto-derive template (`@overridable/@override routine T.MemberRoutine()`): register it
                // in the derive-template store and SKIP `_routineBodies` (its body is consumed ONLY via
                // GetDeriveTemplate / CloneUniversalDeriveBody). See TryRegisterStdlibDeriveTemplate.
                if (TryRegisterStdlibDeriveTemplate(decl: decl))
                    continue;

                // WARM: derive templates (above) MUST still register — user types clone their destroy/
                // represent/… derives from them (WiredRoutinePass). But the stdlib routine BODIES must NOT
                // re-enter _routineBodies: the warm snapshot already restored the stdlib variant/synthesized
                // bodies, and re-adding the bases here would make ErrorHandlingVariantPass.RunGlobal
                // REGENERATE all stdlib variants (key drift → thousands of duplicate variant bodies that
                // AnalyzeVariantBodies re-analyzes (~8 s) + codegen over-prune). So skip only the body-add.
                if (_registry.SkipStdlibReprocessing)
                    continue;

                RoutineInfo? routineInfo = ResolveRoutineInfoForDeclaration(decl: decl, moduleName: module);
                if (routineInfo == null) continue;

                if (!_routineBodies.ContainsKey(key: routineInfo.RegistryKey))
                    _routineBodies[key: routineInfo.RegistryKey] = decl.Body;
            }
        }
    }

    /// <summary>
    /// Registers every stdlib auto-derive TEMPLATE into the derive-template store, standalone from
    /// <see cref="CollectStdlibBodiesForVariantGeneration"/>. Runs UNCONDITIONALLY (even under
    /// <see cref="SaOnly"/>) so a warm-stdlib snapshot — captured with <c>SaOnly=true</c>, before the
    /// variant-body collection ever runs — still carries the templates. Without this the warm
    /// <c>snapshot:</c> full-analyze path has an empty template store (its restored registry has no
    /// StdlibPrograms to re-scan) and <c>WiredRoutinePass</c> throws cloning e.g. <c>CLong.destroy()</c>.
    /// Registration is idempotent (RegisterDeriveTemplate dedups by arity+gate set), so the re-scan inside
    /// CollectStdlibBodiesForVariantGeneration is a harmless no-op.
    /// </summary>
    private void RegisterStdlibDeriveTemplates()
    {
        foreach ((Program program, _, _) in _registry.StdlibPrograms)
            foreach (ISyntaxTreeNode node in program.Declarations)
                if (node is RoutineDeclaration decl)
                    TryRegisterStdlibDeriveTemplate(decl: decl);
    }

    /// <summary>
    /// Registers a single stdlib routine decl as a UNIVERSAL auto-derive template if it is one, returning
    /// true when it was (so the caller SKIPS filing it into <c>_routineBodies</c> — a template body is
    /// consumed only via the derive-template store).
    /// <para>The template is captured straight from the decl (owner param + kind gates + body), NOT via a
    /// resolved RoutineInfo — the owner is a type-parameter placeholder that doesn't resolve, and several
    /// same-signature kind-gated templates must coexist in the store, which the signature-keyed registry
    /// cannot hold.</para>
    /// <para>ONLY a bare type-PARAMETER owner (<c>routine T.represent()</c>) is a universal derive: its
    /// <c>T</c> binds the WHOLE receiver, so the body is re-usable for any type. A GENERIC-INSTANCE
    /// receiver (<c>routine Dict[K, V].represent()</c>) is keyed by its OWN args (<c>K</c>/<c>V</c>), not a
    /// whole-receiver <c>T</c>; registering it universal poisons the store (GetDeriveTemplate could hand
    /// Dict's K/V-bearing body to an unrelated type, leaving K/V unsubstituted). Discriminated on the
    /// parser's STRUCTURED <c>HasReceiverTypeArgs</c> — NOT <c>LookupType(owner)</c>, which a user
    /// <c>record T</c> would trap into dropping the real stdlib template (name-as-identity, [[generic-
    /// parameter identity = SLOT]]).</para>
    /// </summary>
    private bool TryRegisterStdlibDeriveTemplate(RoutineDeclaration decl)
    {
        if (decl.Body == null) return false;
        if (decl.HasReceiverTypeArgs) return false;
        if (decl.OwnerName is not { } deriveOwner) return false;
        if (decl.MemberRoutineName is not { } deriveMember) return false;
        // A derive TEMPLATE is per-type MATERIALIZED (its body is cloned into the template store, NOT filed
        // as one shared generic routine body). Two shapes qualify, and both are read from what's already
        // written — no dedicated marker:
        //   • @overridable / @override — universal or kind-specialized derives (`represent`, choice `count`).
        //   • an OWNER `obeys` constraint (`routine T.lt() needs T obeys Comparable`) — a capability-conferred
        //     derived helper (`lt`/`le`/`gt`/`ge` from `cmp`), which is NOT `@overridable` (you override
        //     `cmp`, not `lt`).
        // An untagged bare-`T` routine with only a KIND gate (`T.view() needs T is EntityType`) is a normal
        // GENERIC method — one shared body via `_routineBodies` — and must NOT be diverted into the store.
        bool hasDeriveAnnotation = decl.Annotations.Contains(item: "overridable")
                                   || decl.Annotations.Contains(item: "override");
        bool hasOwnerObeysConstraint = decl.GenericConstraints?.Any(predicate: c =>
            c.ParameterName == deriveOwner && c.ConstraintType == ConstraintKind.Obeys) == true;
        if (!hasDeriveAnnotation && !hasOwnerObeysConstraint) return false;
        if (!DeriveOwnerIsTypeParameter(ownerName: deriveOwner, decl: decl)) return false;

        _registry.RegisterDeriveTemplate(memberRoutine: deriveMember,
            ownerParam: deriveOwner,
            arity: decl.Parameters.Count,
            constraints: decl.GenericConstraints,
            body: decl.Body);
        return true;
    }

    /// <summary>
    /// True when a bare-owner <c>@overridable/@override</c> derive decl's owner is a type-PARAMETER
    /// placeholder (the <c>T</c> in <c>@overridable routine T.represent()</c>) rather than a CONCRETE
    /// type. A concrete stdlib owner (e.g. <c>@override routine Byte.represent()</c>) is a per-type
    /// OVERRIDE, not a universal template — registering it in the derive-template store would file its
    /// body under a fake "Byte" type-param and hand it to unrelated types. It must instead flow through
    /// the normal per-type body path so it simply overrides the universal derive for its own type.
    /// <para>Name-trap ([[name-canonicalization]]): a USER <c>record T</c> makes <c>LookupType("T")</c>
    /// resolve to the user type, which would wrongly mark the stdlib <c>T.represent</c> template as a
    /// concrete override and drop it. Excluded by requiring the resolved owner to be STDLIB-defined:
    /// this loop only processes stdlib decls, so a real concrete override always resolves to a stdlib
    /// type; an owner resolving to a USER type is the placeholder letter coincidentally reused.</para>
    /// </summary>
    /// <summary>
    /// Enforces the <c>@override</c> marker on a concrete type's routine that collides with a UNIVERSAL
    /// <c>@overridable</c> auto-derive template (<c>represent</c>/<c>diagnose</c>/<c>serialize</c>/
    /// <c>destroy</c> — the never-capability-gated derives). Without the marker the concrete routine is
    /// SILENTLY shadowed by the auto-derive (the Byte.represent → "Byte()" bug), so require it to be
    /// explicit: <c>@override</c> replaces the derive; otherwise it is an error. Opt-in derives
    /// (eq/cmp/hash/copy/store — capability-gated) are excluded: those only exist when the type
    /// <c>obeys</c> the protocol, so a bare user <c>eq</c> is its own definition, not a collision.
    /// Runs after <see cref="CollectStdlibBodiesForVariantGeneration"/> so every template is registered.
    /// </summary>
    private void CheckOverridableDeriveMarkers()
    {
        foreach (TypeInfo type in _registry.GetTypesWithMemberRoutines())
        {
            // Templates live in the derive-template store keyed on a `T` placeholder, never as member
            // routines, so a GenericParameterTypeInfo owner cannot appear here — but guard anyway.
            if (type is GenericParameterTypeInfo) continue;
            foreach (RoutineInfo routine in _registry.GetMemberRoutinesForType(type: type))
            {
                CheckRoutineForOverridableDeriveCollision(type: type, routine: routine);
            }
        }
    }

    /// <summary>
    /// Checks a single routine on <paramref name="type"/> for an <c>@override</c>-marker violation:
    /// reports <see cref="SemanticDiagnosticCode.OverridableDeriveNeedsOverrideMarker"/> when the routine
    /// collides with a non-opt-in auto-derive template that <paramref name="type"/> actually receives
    /// but is not marked <c>@override</c>. Skips synthesized routines and opt-in derives.
    /// </summary>
    private void CheckRoutineForOverridableDeriveCollision(TypeInfo type, RoutineInfo routine)
    {
        if (routine.IsSynthesized) return;
        // Gate-aware: only a collision if THIS type actually RECEIVES a derive of this name+arity —
        // i.e. it satisfies the template's `needs T is <kind>` gate. A name-only HasDeriveTemplate
        // check wrongly flagged a collection's own `count()` against the choice/flags-gated `count`
        // derive (RF-S164), which no non-choice/flags type receives. count/all_cases are ChoiceType/
        // FlagsType-gated buildtime derives; represent/diagnose keep the universal (`T is TypeName`)
        // template, so they still require @override on every concrete override.
        if (_registry.GetDeriveTemplate(name: routine.Name,
                arity: routine.Parameters.Count, forType: type) == null) return;
        if (_registry.IsOptInDeriveMemberRoutine(memberRoutine: routine.Name)) return;
        if (routine.Annotations.Contains(value: "override")) return;
        ReportError(code: SemanticDiagnosticCode.OverridableDeriveNeedsOverrideMarker,
            message:
            $"'{type.Name}.{routine.Name}' collides with the auto-derived '{routine.Name}' every type " +
            $"receives. Mark it '@override' to replace the auto-derive, or remove it (without the " +
            $"marker it would be silently shadowed).",
            location: routine.Location ?? new SourceLocation("", 0, 0, 0));
    }

    private static bool DeriveOwnerIsTypeParameter(string ownerName, RoutineDeclaration decl)
    {
        // A derive TEMPLATE is IDENTIFIED, structurally and resolution-independently, by declaring its
        // owner as a type parameter via a `needs <owner> is …` constraint (`needs T is TypeName`, or a
        // kind-gate like `needs T is VariantType`). EVERY universal derive template in the stdlib carries
        // one. A routine WITHOUT such a constraint on its owner is a per-type OVERRIDE on a concrete type
        // (`@override routine Byte.represent()`, `@override dangerous routine SignalCaster.destroy()`) —
        // it must NOT enter the derive-template store, or e.g. SignalCaster's `me.sig` body would be
        // handed to `Atomic[U8]`. No `LookupType` (which returned null for a not-yet-resolvable bare owner
        // and mis-registered it as a template) and no `record T` name-trap — pure structure.
        return (decl.GenericConstraints ?? []).Any(predicate: c => c.ParameterName == ownerName);
    }

    private static bool LooksLikeGenericParamArg(string ownerTypeName)
    {
        if (TypeInfo.ExtractTypeArgsString(name: ownerTypeName) is not { } inside) return false;
        foreach (string arg in inside.Split(','))
        {
            string a = arg.Trim();
            if (a.Length == 0) return false;
            if (a.Length > 2) return false; // T, K, V, N — single/double upper letters
            if (!char.IsUpper(a[0])) return false;
            if (a.Length == 2 && !char.IsLetterOrDigit(a[1])) return false;
        }
        return true;
    }

    private RoutineInfo? ResolveRoutineInfoForDeclaration(RoutineDeclaration decl, string? moduleName = null)
    {
        if (decl.MemberRoutineName is { } memberRoutineName)
        {
            return ResolveRoutineInfoForMemberDeclaration(decl: decl, memberRoutineName: memberRoutineName,
                moduleName: moduleName);
        }

        string bareName = decl.Name;
        string qualifiedName = string.IsNullOrEmpty(value: moduleName)
            ? bareName
            : $"{moduleName}.{bareName}";

        var standaloneCandidates = _registry.GetAllRoutines()
                                            .Where(routine => routine.OwnerType == null &&
                                                              routine.Name == bareName &&
                                                              (string.IsNullOrEmpty(moduleName) || routine.Module == moduleName ||
                                                               routine.BaseName == qualifiedName))
                                            .ToList();
        return MatchRoutineDeclaration(candidates: standaloneCandidates, decl: decl,
            moduleName: moduleName);
    }

    /// <summary>
    /// Resolves a <see cref="RoutineDeclaration"/> that names a member routine: looks up the owner type
    /// (realm-aware, module-qualified when available), collects member-routine candidates on the bare owner
    /// and on any matching bracketed-owner bucket (for protocol-extension decls such as
    /// <c>Iterable[Text].join</c>), then matches by signature.
    /// </summary>
    private RoutineInfo? ResolveRoutineInfoForMemberDeclaration(RoutineDeclaration decl,
        string memberRoutineName, string? moduleName)
    {
        // Owner is the RENDERED receiver (Iterable[Text]) — the bracketed-owner bucket key used below.
        string ownerTypeName = decl.RenderedReceiver!;

        // Stdlib protocol-extension decls like `Iterable[Text].join` register their routines
        // under the bracketed-owner bucket (FullName = "Core.Iterable[Text]"). Try the
        // bracketed form first, falling back to the gen-def name. Both lookups can succeed
        // on different types: prefer the one that actually has the candidate memberRoutine.
        string bareLookupName = TypeInfo.StripTypeArgs(name: ownerTypeName);

        // Own-module + own-REALM FIRST: a member decl `routine List[T].add_last` in an SF-realm
        // `Standard/Suflae/…` file owns the SF-realm `Core.List`, not the RazorForge-realm one that
        // shares the bare key. The decl's source-file extension (.sf → SF) gives its realm; a realm-
        // blind lookup would type `me` as the RF list (which lacks the SF wrapper's `inner`) → RF-S450.
        string declRealm = decl.Location?.FileName is { } df
                           && df.EndsWith(value: ".sf", comparisonType: StringComparison.OrdinalIgnoreCase)
            ? "SF" : "RF";
        TypeSymbol? bareOwner = LookupBareOwner(moduleName: moduleName,
            bareLookupName: bareLookupName, declRealm: declRealm);
        if (bareOwner == null) return null;

        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: bareOwner, memberRoutineName: memberRoutineName,
            candidates: candidates);

        // Protocol-extension decls like `Iterable[Text].join` register their routines under
        // a bracketed-owner bucket (e.g. owner FullName="Core.Iterable[Text]") that the
        // gen-def lookup misses. Scan all routines for owners whose name shape matches the
        // bracketed form.
        if (ownerTypeName.Contains('[') && !LooksLikeGenericParamArg(ownerTypeName))
        {
            TypeSymbol? bracketed = _registry.LookupType(name: ownerTypeName);
            if (bracketed != null && !ReferenceEquals(bracketed, bareOwner))
            {
                _registry.CollectMemberRoutineCandidates(type: bracketed, memberRoutineName: memberRoutineName,
                    candidates: candidates);
            }
        }

        // For member-routine decls, prefer the decl's actual module (passed in) over the
        // owner type's module: common routines for built-in types (e.g. `S64.from_digit_bytes`
        // declared in `IO/BytesIO`) live in a different module from the owner.
        return MatchRoutineDeclaration(candidates: candidates, decl: decl,
            moduleName: moduleName ?? bareOwner.Module);
    }

    /// <summary>
    /// Looks up the bare (unparameterized) owner type for a member-routine declaration, preferring a
    /// realm-and-module-qualified lookup when <paramref name="moduleName"/> is known.
    /// </summary>
    private TypeSymbol? LookupBareOwner(string? moduleName, string bareLookupName, string declRealm)
    {
        if (moduleName != null)
        {
            string qualified = $"{moduleName}.{bareLookupName}";
            TypeSymbol? realmQualified = _registry.LookupType(name: qualified, realm: declRealm)
                                         ?? _registry.LookupType(name: qualified);
            if (realmQualified != null) return realmQualified;
        }

        return _registry.LookupType(name: bareLookupName);
    }

    private static RoutineInfo? MatchRoutineDeclaration(List<RoutineInfo> candidates,
        RoutineDeclaration decl, string? moduleName)
    {
        if (decl.Parameters.Any(param => param.Type == null))
        {
            return null;
        }

        var astParamTypeNames = decl.Parameters
            .Select(param => NormalizeMatchTypeName(name: GetAstMatchTypeName(typeExpr: param.Type!)))
            .ToList();

        return candidates.FirstOrDefault(candidate =>
            CandidateMatchesDeclaration(candidate: candidate, decl: decl, moduleName: moduleName,
                astParamTypeNames: astParamTypeNames));
    }

    /// <summary>
    /// True when a candidate RoutineInfo matches the declaration by failability, module (when
    /// constrained), and normalized parameter-type names.
    /// </summary>
    private static bool CandidateMatchesDeclaration(RoutineInfo candidate, RoutineDeclaration decl,
        string? moduleName, List<string> astParamTypeNames)
    {
        if (candidate.IsFailable != decl.IsFailable)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(moduleName) && candidate.Module != null && candidate.Module != moduleName)
        {
            return false;
        }

        if (candidate.Parameters.Count != astParamTypeNames.Count)
        {
            return false;
        }

        for (int i = 0; i < astParamTypeNames.Count; i++)
        {
            string candidateTypeName = NormalizeMatchTypeName(name: candidate.Parameters[i].Type.Name);
            if (candidateTypeName != astParamTypeNames[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Strips whitespace and namespace qualifiers from a rendered type name so an AST-derived name and a
    /// registered RoutineInfo parameter-type name compare on their leaf identifiers only.
    /// </summary>
    private static string NormalizeMatchTypeName(string name)
    {
        name = name.Replace(oldValue: " ", newValue: "");
        var sb = new StringBuilder(name.Length);
        var token = new StringBuilder();

        static void FlushToken(StringBuilder source, StringBuilder dest)
        {
            if (source.Length == 0)
            {
                return;
            }

            string segment = source.ToString();
            int lastDot = segment.LastIndexOf(value: '.');
            dest.Append(lastDot >= 0 ? segment[(lastDot + 1)..] : segment);
            source.Clear();
        }

        foreach (char ch in name)
        {
            if (char.IsLetterOrDigit(ch) || ch is '_' or '.' or '/')
            {
                token.Append(value: ch);
                continue;
            }

            FlushToken(source: token, dest: sb);
            sb.Append(value: ch);
        }

        FlushToken(source: token, dest: sb);
        return sb.ToString();
    }

    /// <summary>
    /// Renders a parameter's AST <see cref="TypeExpression"/> to the same textual shape a registered
    /// <c>RoutineInfo</c> parameter type carries, so the two compare by string in
    /// <see cref="MatchRoutineDeclaration"/>. Special-cases the <c>Routine[(params), ret]</c> form.
    /// </summary>
    private static string GetAstMatchTypeName(TypeExpression typeExpr)
    {
        if (typeExpr.GenericArguments is not { Count: > 0 })
        {
            return typeExpr.Name;
        }

        // `Routine[(params), ret]`: RoutineTypeInfo.Name renders the parameter-list tuple
        // PARENTHESIZED — `(T,)` for one element, `(A, B)` for several — and the return type
        // directly, NOT as `Tuple[...]` (see RoutineTypeInfo.BuildName). The AST instead parses
        // the param-list as a generic `Tuple[...]`. Render the Routine form to match exactly, so
        // lambda-taking protocol-extension memberRoutines (Iterable[T].where/select/accumulate/...) match
        // their registered RoutineInfo signature; otherwise their bodies aren't collected and
        // ProtocolDefaultImplLoweringPass can't synthesize per-implementer instances → "undefined
        // symbol" at codegen. Scoped to the Routine param-list ONLY — a standalone `Tuple[...]`
        // parameter keeps its `Tuple[...]` rendering (which matches TupleTypeInfo.Name).
        if (typeExpr.Name == "Routine" && typeExpr.GenericArguments.Count == 2)
        {
            TypeExpression paramTupleExpr = typeExpr.GenericArguments[index: 0];
            string paramList;
            if (paramTupleExpr.Name == "Tuple"
                && paramTupleExpr.GenericArguments is { Count: > 0 } tupleArgs)
            {
                paramList = tupleArgs.Count == 1
                    ? "(" + GetAstMatchTypeName(typeExpr: tupleArgs[index: 0]) + ",)"
                    : "(" + string.Join(separator: ", ",
                        values: tupleArgs.Select(GetAstMatchTypeName)) + ")";
            }
            else
            {
                // 0-parameter routine type: RoutineTypeInfo.BuildName renders "None".
                paramList = GetAstMatchTypeName(typeExpr: paramTupleExpr);
            }

            return $"Routine[{paramList}, {GetAstMatchTypeName(typeExpr: typeExpr.GenericArguments[index: 1])}]";
        }

        return $"{typeExpr.Name}[{string.Join(separator: ",",
            values: typeExpr.GenericArguments.Select(GetAstMatchTypeName))}]";
    }

    #endregion
}
