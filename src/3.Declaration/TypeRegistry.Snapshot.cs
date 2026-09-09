using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;
using Compiler.Verification.Enums;
using Compiler.Verification.Scopes;

namespace Compiler.Declaration;

partial class TypeRegistry
{
    /// <summary>
    /// Immutable snapshot of the registry state after stdlib loading and body analysis.
    /// Used by tests to restore pre-analyzed stdlib state instead of re-parsing 168 files per test.
    /// </summary>
    public sealed class StdlibSnapshot
    {
        /// <summary>The language (RazorForge or Suflae) this snapshot was built for.</summary>
        public Language Language { get; init; }

        // Type storage
        /// <summary>All registered types keyed by full name.</summary>
        public Dictionary<string, TypeInfo> Types { get; init; } = null!;
        /// <summary>Generic-resolution cache keyed by instantiated full name.</summary>
        public Dictionary<string, TypeInfo> Resolutions { get; init; } = null!;
        /// <summary>Wrapper-type resolution cache (Owned/Retained/etc.) keyed by full name.</summary>
        public Dictionary<string, WrapperTypeInfo> WrapperResolutions { get; init; } = null!;
        /// <summary>Entity specializations keyed by full name.</summary>
        public Dictionary<string, TypeInfo> EntitySpecializations { get; init; } = null!;
        /// <summary>Types indexed by short (unqualified) name for import resolution.</summary>
        public Dictionary<string, TypeInfo> TypesByShortName { get; init; } = null!;

        // Routine storage — list-valued dicts need copied lists (not shared) so tests can extend them
        /// <summary>All routines keyed by RegistryKey.</summary>
        public Dictionary<string, RoutineInfo> Routines { get; init; } = null!;
        /// <summary>Routines keyed by qualified name.</summary>
        public Dictionary<string, RoutineInfo> RoutinesByQualifiedName { get; init; } = null!;
        /// <summary>Routines grouped by owner type full name, then by memberRoutine name → overloads (a bare
        /// generic-param owner is stored under the canonical GenericOwnerKey).</summary>
        public Dictionary<string, Dictionary<string, List<RoutineInfo>>> RoutinesByOwner { get; init; } = null!;
        /// <summary>Resolved routine instances (concrete-owner substitutions) keyed by RegistryKey.</summary>
        public Dictionary<string, RoutineInfo> RoutineResolutions { get; init; } = null!;

        // Preset storage
        /// <summary>Preset variables keyed by short name.</summary>
        public Dictionary<string, VariableInfo> Presets { get; init; } = null!;
        /// <summary>Preset variables keyed by qualified name.</summary>
        public Dictionary<string, VariableInfo> PresetsByQualifiedName { get; init; } = null!;

        // Module tracking
        /// <summary>Set of module paths already loaded into the registry.</summary>
        public HashSet<string> LoadedModules { get; init; } = null!;
        /// <summary>Module alias → qualified module name map.</summary>
        public Dictionary<string, string> ModuleNames { get; init; } = null!;

        /// <summary>Root path for on-demand module loading (e.g. import BuilderQuery).</summary>
        public string? StdlibRootPath { get; init; }

        /// <summary>Auto-derive templates (<c>@overridable routine T.destroy()/represent()/…</c>) keyed by
        /// memberRoutine name. Restored so a warm full-analyze can still CLONE a per-type derive body
        /// (e.g. <c>CLong.destroy()</c>) for a stdlib type whose synthesized body was not pre-captured —
        /// without them <see cref="GetDeriveTemplate"/> returns null and WiredRoutinePass throws.</summary>
        public Dictionary<string,
            List<(string OwnerParam, int Arity, List<SyntaxTree.GenericConstraintDeclaration> Gates,
                SyntaxTree.Statement Body)>> DeriveTemplates { get; init; } = null!;

        /// <summary>Deferred failable-variant bases (base RegistryKey → base routine, AST body, pessimistic)
        /// captured so a snapshot-restored build — which skips variant pre-registration — can still
        /// synthesize stdlib <c>try_</c>/<c>check_</c>/<c>lookup_</c> variants on demand. See
        /// <see cref="DeferredVariantBases"/>.</summary>
        public Dictionary<string, (RoutineInfo baseRoutine, SyntaxTree.Statement body, bool pessimistic)>
            DeferredVariantBases { get; init; } = null!;
    }

    /// <summary>
    /// Deferred failable-variant bases: base <see cref="RoutineInfo.RegistryKey"/> → (base routine, its AST
    /// body, pessimistic flag). Populated by variant pre-registration; consumed by the on-demand variant
    /// synthesizer (a call to <c>try_X</c>/<c>check_X</c>/<c>lookup_X</c> generates the base's variants from
    /// this index). Lives on the registry (not the verifier) so it rides the stdlib snapshot — a
    /// snapshot-restored build SKIPS pre-registration, so without this the index would be empty and stdlib
    /// variant calls would fail to resolve. AST bodies in a snapshot are already precedented by
    /// <see cref="StdlibSnapshot.DeriveTemplates"/>.
    /// </summary>
    internal Dictionary<string, (RoutineInfo baseRoutine, SyntaxTree.Statement body, bool pessimistic)>
        DeferredVariantBases { get; private set; } = new();

    /// <summary>
    /// Captures a snapshot of the current registry state.
    /// Call after stdlib loading and body analysis is complete.
    /// List-valued dictionaries are deep-copied so tests can extend them without poisoning the snapshot.
    /// </summary>
    public StdlibSnapshot CaptureSnapshot() =>
        new()
        {
            Language = Language,
            Types = new Dictionary<string, TypeInfo>(_types),
            Resolutions = new Dictionary<string, TypeInfo>(_resolutions),
            WrapperResolutions = new Dictionary<string, WrapperTypeInfo>(_wrapperResolutions),
            EntitySpecializations = new Dictionary<string, TypeInfo>(_entitySpecializations),
            TypesByShortName = new Dictionary<string, TypeInfo>(_typesByShortName),
            Routines = new Dictionary<string, RoutineInfo>(_routines),
            RoutinesByQualifiedName = new Dictionary<string, RoutineInfo>(_routinesByQualifiedName),
            RoutinesByOwner = _routinesByOwner.ToDictionary(
                keySelector: kv => kv.Key,
                elementSelector: kv => kv.Value.ToDictionary(
                    keySelector: m => m.Key,
                    elementSelector: m => new List<RoutineInfo>(m.Value))),
            RoutineResolutions = new Dictionary<string, RoutineInfo>(_routineResolutions),
            Presets = new Dictionary<string, VariableInfo>(_presets),
            PresetsByQualifiedName = new Dictionary<string, VariableInfo>(_presetsByQualifiedName),
            LoadedModules = new HashSet<string>(_loadedModules, StringComparer.OrdinalIgnoreCase),
            ModuleNames = new Dictionary<string, string>(_moduleNames, StringComparer.OrdinalIgnoreCase),
            StdlibRootPath = _stdlibPath,
            DeriveTemplates = _deriveTemplates.ToDictionary(
                keySelector: kv => kv.Key,
                elementSelector: kv => kv.Value
                    .Select(selector: e => (e.OwnerParam, e.Arity, e.Gates, e.Body))
                    .ToList()),
            DeferredVariantBases =
                new Dictionary<string, (RoutineInfo baseRoutine, SyntaxTree.Statement body, bool pessimistic)>(
                    DeferredVariantBases),
        };

    /// <summary>
    /// Restores registry state from a stdlib snapshot.
    /// Used by tests to skip the expensive stdlib parse + body analysis phases.
    /// </summary>
    public TypeRegistry(Language language, StdlibSnapshot snapshot)
    {
        Language = language;
        RegisterAsAmbient(registry: this);
        GlobalScope = new Scope(kind: ScopeKind.Global);
        _currentScope = GlobalScope;

        RestoreTypeStorage(snapshot: snapshot);
        RestoreRoutineStorage(snapshot: snapshot);
        RestorePresetStorage(snapshot: snapshot);
        RestoreModuleTracking(snapshot: snapshot);
        RestoreDeriveTemplates(snapshot: snapshot);
        // Restore the deferred variant index so on-demand synthesis works without re-running pre-registration.
        if (snapshot.DeferredVariantBases != null)
            DeferredVariantBases =
                new Dictionary<string, (RoutineInfo baseRoutine, SyntaxTree.Statement body, bool pessimistic)>(
                    snapshot.DeferredVariantBases);

        // Fresh loader per test — shares no mutable state with other test instances.
        // The snapshot's loader is only used to capture the registry state; on-demand module
        // loading (import BuilderQuery, etc.) goes through this new per-test loader.
        // Modules already in _loadedModules (restored from snapshot) are skipped without re-parsing.
        // NOTE: _stdlibPath is readonly, so it must be assigned directly in the constructor.
        _stdlibPath = snapshot.StdlibRootPath;
        InitializeRestoredLoader(snapshot: snapshot, language: language);
    }

    /// <summary>Restores the type-storage dictionaries (types, resolutions, wrappers, specializations,
    /// short-name index) from a snapshot.</summary>
    private void RestoreTypeStorage(StdlibSnapshot snapshot)
    {
        foreach (var kv in snapshot.Types) _types[kv.Key] = kv.Value;
        foreach (var kv in snapshot.Resolutions) _resolutions[kv.Key] = kv.Value;
        foreach (var kv in snapshot.WrapperResolutions) _wrapperResolutions[kv.Key] = kv.Value;
        foreach (var kv in snapshot.EntitySpecializations) _entitySpecializations[kv.Key] = kv.Value;
        foreach (var kv in snapshot.TypesByShortName) _typesByShortName[kv.Key] = kv.Value;
    }

    /// <summary>Restores the routine-storage dictionaries. List-valued dicts get NEW lists so test
    /// mutations don't leak back into the shared snapshot.</summary>
    private void RestoreRoutineStorage(StdlibSnapshot snapshot)
    {
        foreach (var kv in snapshot.Routines) _routines[kv.Key] = kv.Value;
        foreach (var kv in snapshot.RoutinesByQualifiedName) _routinesByQualifiedName[kv.Key] = kv.Value;
        foreach (var kv in snapshot.RoutinesByOwner)
            _routinesByOwner[kv.Key] = kv.Value.ToDictionary(
                keySelector: m => m.Key, elementSelector: m => new List<RoutineInfo>(m.Value));
        foreach (var kv in snapshot.RoutineResolutions) _routineResolutions[kv.Key] = kv.Value;
    }

    /// <summary>Restores the preset dictionaries from a snapshot.</summary>
    private void RestorePresetStorage(StdlibSnapshot snapshot)
    {
        foreach (var kv in snapshot.Presets) _presets[kv.Key] = kv.Value;
        foreach (var kv in snapshot.PresetsByQualifiedName) _presetsByQualifiedName[kv.Key] = kv.Value;
    }

    /// <summary>Restores the loaded-module set and module-name aliases from a snapshot.</summary>
    private void RestoreModuleTracking(StdlibSnapshot snapshot)
    {
        foreach (var m in snapshot.LoadedModules) _loadedModules.Add(m);
        foreach (var kv in snapshot.ModuleNames) _moduleNames[kv.Key] = kv.Value;
    }

    /// <summary>
    /// Restores auto-derive templates so a warm full-analyze can clone per-type derive bodies (e.g.
    /// CLong.destroy) for stdlib types whose synthesized body was not pre-captured.
    /// </summary>
    private void RestoreDeriveTemplates(StdlibSnapshot snapshot)
    {
        if (snapshot.DeriveTemplates != null)
            foreach (var kv in snapshot.DeriveTemplates)
                _deriveTemplates[kv.Key] = kv.Value
                    .Select(selector: e => (e.OwnerParam, e.Arity, e.Gates, e.Body))
                    .ToList();
    }

    /// <summary>
    /// Sets up the per-instance stdlib loader for a restored registry. A fresh loader shares no mutable
    /// state with other test instances; on-demand imports (import BuilderQuery, etc.) go through it, and
    /// modules already restored into <c>_loadedModules</c> are skipped without re-parsing.
    /// </summary>
    private void InitializeRestoredLoader(StdlibSnapshot snapshot, Language language)
    {
        if (snapshot.StdlibRootPath != null)
            _stdlibLoader = new StdlibLoader(stdlibRoot: snapshot.StdlibRootPath, language: language)
            {
                // Core is restored (already lowered) — an on-demand import re-scans stdlib and re-parses
                // every Core file into the loader's _corePrograms; flag it so those stale unanalyzed copies
                // are NOT reported as freshly-loaded (they'd be re-lowered and crash — the D128 ternary bug).
                CoreResident = true
            };
        _coreModuleLoaded = true;
    }
}
