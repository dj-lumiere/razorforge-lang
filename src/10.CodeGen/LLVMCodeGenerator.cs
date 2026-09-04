using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Compiler.Desugaring;
using Compiler.Instantiation;
using Compiler.Instantiation.Passes;
using Compiler.Resolution;
using Compiler.Targeting;
using DebugUtils.Repr;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// LLVM IR code generator for RazorForge and Suflae.
/// Consumes a fully-typed AST from the semantic analyzer.
/// </summary>
public partial class LlvmCodeGenerator
{
    private const string EntryLabel = "entry:";
    private const string RetVoidInstruction = "  ret void";

    #region Fields

    /// <summary>The type registry from semantic analysis.</summary>
    private readonly TypeRegistry _registry;

    /// <summary>AST bodies for compiler-generated derived operators, keyed by RoutineInfo.RegistryKey.</summary>
    private IReadOnlyDictionary<string, Statement> _synthesizedBodies =
        new Dictionary<string, Statement>();

    /// <summary>
    /// Concrete generic memberRoutine bodies from <see cref="Instantiation.Passes.GenericMonomorphizationPass"/>,
    /// keyed by <see cref="TypeModel.Symbols.RoutineInfo.RegistryKey"/>.
    /// Phase B emission iterates this map and emits any body whose
    /// mangled name has been declared in <see cref="_generatedRoutines"/>.
    /// </summary>
    private IReadOnlyDictionary<string, MonomorphizedBody> _instantiatedGenericBodies =
        new Dictionary<string, MonomorphizedBody>();

    /// <summary>
    /// Reachable routine RegistryKeys produced by <see cref="RoutineReachabilityPass"/>.
    /// When non-empty, Phase A's stdlib body emission gates by this set in addition to
    /// <see cref="_generatedRoutines"/>, preventing the lazy-declaration cascade from
    /// emitting bodies for unreachable routines.
    /// </summary>
    private HashSet<string> _liveRoutineKeys = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// RegistryKeys of routines actually REFERENCED while emitting a routine body (the transitive
    /// closure from the user entry points). Codegen gates body definitions on this set so a routine
    /// is emitted only if some emitted body calls it — pruning every routine nothing references
    /// (dead derived operators, dead variants, etc.). Populated by <c>GenerateRoutineDeclaration</c>
    /// whenever <see cref="_emittingRoutineBody"/> is set. The do/while fixpoint in
    /// <c>GenerateRoutineDefinitions</c> drives convergence: each newly-emitted body adds its callees.
    /// </summary>
    private readonly HashSet<string> _referencedKeys = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Strips a leading realm prefix (<c>SF::</c>/<c>RF::</c>) from a routine RegistryKey. Routine
    /// mangling is realm-FREE (option-b, ambient-bare), so an SF-realm body and its RF-realm twin emit
    /// under ONE symbol; but their RegistryKeys differ by the realm prefix. The emission gate compares
    /// against callers' referenced keys, which are recorded from whatever realm the CALL resolved to
    /// (often the ambient RF form). Comparing realm-stripped closes that gap: same emitted symbol =
    /// same emission decision, regardless of which world-line the caller vs the body was keyed under.
    /// </summary>
    private static string StripRealmPrefix(string registryKey)
    {
        int sep = registryKey.IndexOf("::", StringComparison.Ordinal);
        // Realm prefix is a bare word at the very start (e.g. "SF::[member] …"); the "::" never
        // appears elsewhere in a key. Guard on the prefix being alphanumeric so a stray "::" deeper
        // in the string can't truncate a real key.
        if (sep > 0 && registryKey.AsSpan(0, sep).IndexOfAny(" .[]") < 0)
            return registryKey[(sep + 2)..];
        return registryKey;
    }

    /// <summary>
    /// Removes an embedded realm marker (<c>SF::</c>/<c>RF::</c>) from a MANGLED symbol name (where it
    /// sits after the attribute prefix, e.g. <c>[member] SF::Collections.BitList.hijack()</c>) — unlike
    /// <see cref="StripRealmPrefix"/>, which strips a leading realm from a RegistryKey. Mangled names use
    /// <c>.</c> and <c>[]</c> as separators and <c>::</c> ONLY as the realm marker, so a plain replace is
    /// exact. Used to make the over-prune tripwire realm-INSENSITIVE, matching the emission gate
    /// (<see cref="IsRoutineReferenced"/>): a collapsed SF wrapper emits its universal members (hijack,
    /// destroy, …) under the ambient RF symbol, but the reference bookkeeping in the single-program
    /// codegen path may record the SF-mangled variant — same emitted symbol, so same emission decision.
    /// </summary>
    private static string StripRealmMarker(string mangledName)
        => mangledName.Replace(oldValue: "SF::", newValue: "").Replace(oldValue: "RF::", newValue: "");

    /// <summary>
    /// Emission gate: is this routine's body referenced by some emitted call? Realm-insensitive —
    /// see <see cref="StripRealmPrefix"/> — so an SF-realm body whose caller referenced the ambient
    /// RF-keyed symbol is still emitted (they are the same mangled symbol).
    /// </summary>
    private bool IsRoutineReferenced(string registryKey) =>
        _referencedKeys.Contains(item: registryKey)
        || _referencedKeys.Contains(item: StripRealmPrefix(registryKey));

    /// <summary>
    /// True while emitting a routine body (inside <c>GenerateRoutineBody</c>). Reference recording
    /// is gated on this so the broad declaration pre-pass doesn't pollute <see cref="_referencedKeys"/>.
    /// </summary>
    private bool _emittingRoutineBody;

    /// <summary>
    /// Mangled names of non-extern RF routines that were referenced from an emitted body (so a
    /// forward <c>declare</c> was recorded for them) and therefore MUST also receive a <c>define</c>.
    /// A name left here without a matching entry in <see cref="_generatedRoutineDefs"/> after the
    /// fixpoint converges means reachability pruned away a routine that emitted code actually calls —
    /// an over-prune that would otherwise surface only as a linker "undefined symbol". Only populated
    /// while <see cref="_emittingRoutineBody"/> is set, so dead declares from the broad pre-pass
    /// (e.g. an unreferenced <c>List[Character].merge_into</c>) never enter it.
    /// <c>external("C")</c> routines and <c>@innate</c> stubs are excluded — they are bodyless by design.
    /// </summary>
    private readonly HashSet<string> _expectedBodyNames = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// MANGLED symbol names whose bodies live in the RESIDENT base dylib (resident-JIT incremental,
    /// see <c>internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md</c> §2A/C4). When non-empty, codegen emits
    /// each such routine as an extern <c>declare</c> and SKIPS its <c>define</c> in the per-run delta
    /// module (the resident dylib already exports it), and the over-prune tripwire treats
    /// "referenced-but-not-defined-here" as satisfied for these symbols. EMPTY in the cold/AOT path, so
    /// every gate below is byte-identical to the pre-resident behavior when this is unused.
    /// </summary>
    private HashSet<string> _residentSymbols = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// True when this routine's body is provided by the resident base dylib and must NOT be re-defined
    /// in the delta module. Short-circuits on the empty set so the cold path pays nothing.
    /// </summary>
    private bool IsResident(string mangledFuncName) =>
        _residentSymbols.Count > 0 && _residentSymbols.Contains(item: mangledFuncName);

    /// <summary>
    /// Base-emission mode (resident-JIT incremental Phase 0a, see
    /// <c>internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md</c> §2A.5 / Phase 0a): this generator is
    /// emitting the NON-PRUNED precompiled base (stdlib), not a user program — so it must NOT emit an
    /// <c>@main</c> entry point (the delta module supplies it). Set only by <see cref="GenerateBase"/>.
    /// </summary>
    private bool _baseMode;

    /// <summary>
    /// Live concrete owner type FullNames from RoutineReachabilityPass. Used to drive Phase C
    /// monomorphization of synthesized routines (try_emit, represent, diagnose) for generic owners.
    /// </summary>
    private HashSet<string> _liveOwnerTypeNames = new(comparer: StringComparer.Ordinal);

    /// <summary>Wrapper type base names for member forwarding in codegen.</summary>
    // TODO: It shouldn't know about all these types because they are going to be llvm ptr anyway.
    private static readonly IReadOnlySet<string> WrapperTypeNames = RuntimeContract.WrapperTypes;

    /// <summary>The user program ASTs to generate code for (single-file or multi-file).</summary>
    private readonly List<(Program Program, string FilePath, string Module)>
        _userPrograms;

    /// <summary>The stdlib programs to include routine bodies from.</summary>
    private readonly List<(Program Program, string FilePath, string Module)>
        _stdlibPrograms;

    /// <summary>
    /// Type declarations bucketed by kind and sorted lexicographically within each bucket.
    /// Emitted in category order: record -> choice -> variant -> entity -> crashable.
    /// Key = mangled LLVM type name; value = full declaration text (struct line + comment line).
    /// </summary>
    private readonly SortedDictionary<string, string> _typeDeclarationsRecord = new();

    private readonly SortedDictionary<string, string> _typeDeclarationsVariant = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsEntity = new();
    private readonly SortedDictionary<string, string> _typeDeclarationsCrashable = new();

    /// <summary>
    /// Closure environment struct declarations for lifted lambdas: <c>%"Closure.&lt;name&gt;" =
    /// type { ptr, &lt;capture types&gt; }</c>. Keyed by struct name; emitted with the other type
    /// declarations. See closure conversion in <c>GenerateRoutineBody</c> / the lambda value path.
    /// </summary>
    private readonly SortedDictionary<string, string> _typeDeclarationsClosure = new();

    /// <summary>Output buffer for global declarations (constants, presets).</summary>
    private readonly StringBuilder _globalDeclarations = new();

    /// <summary>Output buffer for native/extern function declarations (always emitted).</summary>
    private readonly StringBuilder _functionDeclarations = new();

    /// <summary>
    /// RF function forward declarations keyed by mangled name.
    /// Entries whose name is in <see cref="_generatedRoutineDefs"/> are suppressed at output
    /// time to avoid declare+define conflicts in the same LLVM module.
    /// </summary>
    private readonly Dictionary<string, string> _rfRoutineDeclarations = new();

    /// <summary>Output buffer for function definitions.</summary>
    private readonly StringBuilder _functionDefinitions = new();

    /// <summary>Output buffer for auxiliary top-level helper function definitions.</summary>
    private readonly StringBuilder _auxRoutineDefinitions = new();

    /// <summary>Thunk symbols already emitted for plain routines used as first-class values
    /// (see <c>EnsureRoutineValueThunk</c>) — dedups the closure-ABI adapter per routine.</summary>
    private readonly HashSet<string> _emittedRoutineValueThunks = [];

    /// <summary>Counter for generating unique temporary variable names.</summary>
    private int _tempCounter;

    /// <summary>Counter for generating unique label names.</summary>
    private int _labelCounter;

    /// <summary>Set of already-generated type declarations to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedTypes = [];

    /// <summary>Set of already-generated function declarations to avoid duplicates.</summary>
    private readonly HashSet<string> _generatedRoutines = [];

    /// <summary>Counter for generating unique string constant names.</summary>
    private int _stringCounter;

    /// <summary>Counter for generating unique C string constant names.</summary>
    private int _cstrCounter;

    /// <summary>Map of string values to their global constant names (for deduplication).</summary>
    private readonly Dictionary<string, string> _stringConstants = new();

    /// <summary>Map of C string values to their global constant names (for deduplication).</summary>
    private readonly Dictionary<string, string> _cstrConstants = new(StringComparer.Ordinal);

/// <summary>Map of local variable names to their types for the current function.</summary>
    private readonly Dictionary<string, TypeInfo> _localVariables = new();

    /// <summary>Suflae module-level <c>global</c> variables: source name -&gt; (type, LLVM <c>@global</c>
    /// symbol). Populated by <see cref="GenerateGlobalVariables"/> before routine bodies are emitted;
    /// consulted by the identifier read / assignment / lvalue paths as a fallback after local lookup.</summary>
    private readonly Dictionary<string, (TypeInfo Type, string Symbol)> _moduleGlobals = new();

    /// <summary>Map of source variable names to unique LLVM variable names (handles shadowing).</summary>
    private readonly Dictionary<string, string> _localVarLlvmNames = new();

    /// <summary>
    /// v0.2.0 9-2: per-routine map of an instrumented owned local's source name to its
    /// cancellation-node alloca (<c>%name.cfnode</c>). Populated by a <c>__rf_cf_push</c> marker,
    /// read by the matching <c>__rf_cf_pop</c>. Cleared per routine.
    /// </summary>
    private readonly Dictionary<string, string> _cfNodes = new();

    /// <summary>Counter for deduplicating variable names within a function.</summary>
    private readonly Dictionary<string, int> _varNameCounts = new();

    /// <summary>List of local entity variables (name, LLVM addr name) for auto-cleanup.</summary>
    private readonly List<(string Name, string LLVMAddr)> _localEntityVars = [];

    /// <summary>List of local record variables with RC wrapper fields for retain/release.</summary>
    private readonly List<(string Name, string LLVMAddr, RecordTypeInfo RecordType)>
        _localRcRecordVars = [];

    /// <summary>List of local variables whose type IS an RC wrapper (Retained[T], Guarded[T], etc.).</summary>
    private readonly List<(string Name, string LLVMAddr, RecordTypeInfo RecordType)>
        _localRetainedVars = [];

    /// <summary>Set of already-generated function definitions to avoid duplicates.</summary>
    // TODO: this should be routine info, not string.
    private readonly HashSet<string> _generatedRoutineDefs = [];

    /// <summary>
    /// The emitted <c>define …</c> header line for each generated routine, keyed by mangled name.
    /// Used at output assembly to assert that a routine's <c>define</c> agrees with any <c>declare</c>
    /// recorded for the same symbol (see <see cref="NormalizeFunctionSignature"/>). A mismatch means
    /// codegen computed the function type two different ways — an internal compiler bug that would
    /// otherwise surface as a cryptic <c>llvm-as</c>/<c>opt</c> "call argument type mismatch" far from
    /// the source. We catch it here instead.
    /// </summary>
    private readonly Dictionary<string, string> _generatedRoutineDefHeaders = new();

    /// <summary>
    /// Number of routine bodies actually emitted (the transitive closure referenced from the entry
    /// point). This is the meaningful "how much code did we compile" figure — far smaller than the
    /// registry's total routine count, which holds every stdlib routine available for resolution.
    /// Valid after <see cref="Generate"/> returns.
    /// </summary>
    public int EmittedRoutineCount => _generatedRoutineDefs.Count;

    /// <summary>
    /// The mangled LLVM symbol names this generator actually emitted a <c>define</c> for (a snapshot of
    /// <see cref="_generatedRoutineDefs"/>). This is the PRODUCER side of the resident/delta split
    /// (resident-JIT incremental, see <c>internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md</c> §2A/C4/Phase 0):
    /// when a base module is codegen'd + JIT'd into the resident dylib, its emitted symbols become the
    /// <c>residentSymbols</c> set fed to the delta build's generator, which then emits those as extern
    /// <c>declare</c>s instead of re-defining them. Decision-independent of §3 (whatever process holds the
    /// resident base captures this in-process or ships it over IPC). Valid after <see cref="Generate"/>.
    /// </summary>
    public IReadOnlyCollection<string> EmittedRoutineSymbols =>
        new HashSet<string>(collection: _generatedRoutineDefs, comparer: StringComparer.Ordinal);

    /// <summary>The return type of the current function being generated.</summary>
    private TypeInfo? _currentRoutineReturnType;

    /// <summary>
    /// True when the current function returns its value through a hidden <c>ptr sret(%T) %sret</c>
    /// first parameter rather than by value (the ABI Indirect return form of the struct-ABI
    /// boundary-coercion design). When set, every <c>return</c> stores
    /// through <c>%sret</c> and emits <c>ret void</c>.
    /// </summary>
    private bool _currentReturnViaSret;

    /// <summary>
    /// When non-null, the current function's struct return is COERCED to this ABI register type
    /// (e.g. <c>i64</c> / <c>{ i64, i32 }</c>) — the Phase 2 small-struct register form. The header
    /// returns this type and every <c>return</c> reinterprets the struct value into it. Mutually
    /// exclusive with <see cref="_currentReturnViaSret"/>.
    /// </summary>
    private string? _currentReturnCoerceType;

    /// <summary>Function-entry alloca instructions emitted once per function.</summary>
    private readonly StringBuilder _currentRoutineEntryAllocas = new();

    /// <summary>Tracks alloca names already emitted for the current function to prevent duplicates.</summary>
    private readonly HashSet<string> _emittedAllocaNames = [];

    /// <summary>Target platform configuration (triple, data layout, page size, etc.).</summary>
    private readonly TargetConfig _target;

    /// <summary>Requested build optimization mode.</summary>
    private readonly RfBuildMode _buildMode;

    /// <summary>Pointer bit width for the target platform (64 for x86_64, 32 for x86).</summary>
    private readonly int _pointerBitWidth;

    /// <summary>Pointer size in bytes, derived from <see cref="_pointerBitWidth"/>.</summary>
    private readonly int _pointerSizeBytes;

    /// <summary>LLVM target triple for the current platform.</summary>
    private readonly string _targetTriple;

    /// <summary>LLVM data layout string for the current platform.</summary>
    private readonly string _dataLayout;

    /// <summary>Whether the current function being generated is failable (has ! suffix, can return absent).</summary>
    private bool _currentRoutineIsFailable;

    /// <summary>The routine currently being compiled (for source_routine() / source_module() injection).</summary>
    private RoutineInfo? _currentEmittingRoutine;

    #endregion

    #region Constructor

    /// <summary>
    /// Creates a new LLVM code generator for a single user program.
    /// </summary>
    /// <param name="program">The program AST to generate code for.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="stdlibPrograms">Optional stdlib programs for intrinsic routine definitions.</param>
    /// <param name="target">Target platform configuration (defaults to current host).</param>
    /// <param name="buildMode">Build optimization mode (defaults to Debug).</param>
    /// <param name="instantiatedGenericBodies">The instantiated generic bodies.</param>
    /// <param name="synthesizedBodies">The synthesized bodies.</param>
    /// <param name="liveRoutineKeys">Reachable routine keys from RoutineReachabilityPass; empty disables filtering.</param>
    /// <param name="liveOwnerTypeNames">Live owner type full-names from RoutineReachabilityPass; empty disables filtering.</param>
    /// <param name="maySuspendRoutineKeys">Routine keys that may suspend (used for coroutine frame layout).</param>
    /// <param name="residentSymbols">Mangled symbols already defined in the resident base dylib (resident-JIT incremental); empty = full emission (cold/AOT).</param>
    public LlvmCodeGenerator(Program program, TypeRegistry registry,
        List<(Program Program, string FilePath, string Module)>? stdlibPrograms = null,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug,
        IReadOnlyDictionary<string, Statement>? synthesizedBodies = null,
        IReadOnlyDictionary<string, MonomorphizedBody>? instantiatedGenericBodies = null,
        IReadOnlyCollection<string>? liveRoutineKeys = null,
        IReadOnlyCollection<string>? liveOwnerTypeNames = null,
        IReadOnlyCollection<string>? maySuspendRoutineKeys = null,
        IReadOnlyCollection<string>? residentSymbols = null) :
        this(userPrograms:
            [(program, program.Location.FileName,
                program.Declarations.OfType<ModuleDeclaration>().FirstOrDefault()?.Path ?? "")],
            registry: registry,
            stdlibPrograms: stdlibPrograms,
            target: target,
            buildMode: buildMode,
            synthesizedBodies: synthesizedBodies,
            instantiatedGenericBodies: instantiatedGenericBodies,
            liveRoutineKeys: liveRoutineKeys,
            liveOwnerTypeNames: liveOwnerTypeNames,
            maySuspendRoutineKeys: maySuspendRoutineKeys,
            residentSymbols: residentSymbols)
    {
    }

    /// <summary>
    /// Creates a new LLVM code generator for multiple user programs (multi-file build).
    /// </summary>
    /// <param name="userPrograms">The user program ASTs with file paths and module names.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="stdlibPrograms">Optional stdlib programs for intrinsic routine definitions.</param>
    /// <param name="target">Target platform configuration (defaults to current host).</param>
    /// <param name="buildMode">Build optimization mode (defaults to Debug).</param>
    /// <param name="instantiatedGenericBodies">The instantiated generic bodies.</param>
    /// <param name="synthesizedBodies">The synthesized bodies.</param>
    /// <param name="liveRoutineKeys">Reachable routine keys from RoutineReachabilityPass; empty disables filtering.</param>
    /// <param name="liveOwnerTypeNames">Live owner type full-names from RoutineReachabilityPass; empty disables filtering.</param>
    /// <param name="maySuspendRoutineKeys">Routine keys that may suspend (used for coroutine frame layout).</param>
    /// <param name="residentSymbols">Mangled symbols already defined in the resident base dylib (resident-JIT incremental); empty = full emission (cold/AOT).</param>
    public LlvmCodeGenerator(
        List<(Program Program, string FilePath, string Module)> userPrograms,
        TypeRegistry registry,
        List<(Program Program, string FilePath, string Module)>? stdlibPrograms = null,
        TargetConfig? target = null, RfBuildMode buildMode = RfBuildMode.Debug,
        IReadOnlyDictionary<string, Statement>? synthesizedBodies = null,
        IReadOnlyDictionary<string, MonomorphizedBody>? instantiatedGenericBodies = null,
        IReadOnlyCollection<string>? liveRoutineKeys = null,
        IReadOnlyCollection<string>? liveOwnerTypeNames = null,
        IReadOnlyCollection<string>? maySuspendRoutineKeys = null,
        IReadOnlyCollection<string>? residentSymbols = null)
    {
        _target = target ?? TargetConfig.ForCurrentHost();
        if (_target.PointerBitWidth != 64)
        {
            throw new ArgumentException(
                message:
                $"Only 64-bit targets are currently supported (got {_target.PointerBitWidth}).",
                paramName: nameof(target));
        }

        _userPrograms = userPrograms;
        _registry = registry;
        _stdlibPrograms = stdlibPrograms ?? [];
        if (synthesizedBodies != null) _synthesizedBodies = synthesizedBodies;
        if (instantiatedGenericBodies != null)
            _instantiatedGenericBodies = instantiatedGenericBodies;
        if (liveRoutineKeys is { Count: > 0 })
            _liveRoutineKeys = new HashSet<string>(collection: liveRoutineKeys,
                comparer: StringComparer.Ordinal);
        if (liveOwnerTypeNames is { Count: > 0 })
            _liveOwnerTypeNames = new HashSet<string>(collection: liveOwnerTypeNames,
                comparer: StringComparer.Ordinal);
        if (residentSymbols is { Count: > 0 })
            _residentSymbols = new HashSet<string>(collection: residentSymbols,
                comparer: StringComparer.Ordinal);
        if (maySuspendRoutineKeys is { Count: > 0 })
            new HashSet<string>(collection: maySuspendRoutineKeys,
                comparer: StringComparer.Ordinal);
        _buildMode = buildMode;
        _pointerBitWidth = _target.PointerBitWidth;
        _pointerSizeBytes = _target.PointerBitWidth / 8;
        _targetTriple = _target.Triple;
        _dataLayout = _target.DataLayout;
    }

    #endregion

    #region Helpers

    /// <summary>Whether to emit rf_trace_push/rf_trace_pop calls for stack trace diagnostics.</summary>
    private bool ShouldEmitTrace => _buildMode is RfBuildMode.Debug or RfBuildMode.Release;

    /// <summary>
    /// Whether to emit trace push/pop for the currently-compiled routine.
    /// In Release, @inline routines are excluded — they are implementation details
    /// that inflate the shadow stack without adding navigable frames.
    /// In Debug, all routines are traced.
    /// </summary>
    private bool _traceCurrentRoutine;

    /// <summary>
    /// Looks up a type by name, trying the current routine's module-qualified name first,
    /// then falling back to the bare name. Mirrors SemanticVerifier.LookupTypeInCurrentModule.
    /// </summary>
    private TypeInfo? LookupTypeInCurrentModule(string name)
    {
        string? moduleName = _currentEmittingRoutine?.OwnerType?.Module ??
                             _currentEmittingRoutine?.Module;
        if (moduleName != null && !name.Contains(value: '.'))
        {
            TypeInfo? qualified = _registry.LookupType(name: $"{moduleName}.{name}");
            if (qualified != null)
            {
                return qualified;
            }
        }

        return _registry.LookupType(name: name);
    }

    /// <summary>
    /// Gets the generic definition for a resolved generic type, regardless of concrete subtype.
    /// Returns null for non-generic or non-resolved types.
    /// </summary>
    private static TypeInfo? GetGenericBase(TypeInfo type) =>
        GetGenericBaseStatic(type: type);

    /// <summary>
    /// Gets the generic definition for a resolved generic type.
    /// </summary>
    internal static TypeInfo? GetGenericBaseStatic(TypeInfo type)
    {
        return type switch
        {
            RecordTypeInfo { GenericDefinition: not null } r => r.GenericDefinition,
            EntityTypeInfo { GenericDefinition: not null } e => e.GenericDefinition,
            ProtocolTypeInfo { GenericDefinition: not null } p => p.GenericDefinition,
            _ => null
        };
    }

    /// <summary>
    /// Gets the generic definition's name for a resolved generic type.
    /// Returns null for non-generic or non-resolved types.
    /// </summary>
    private static string? GetGenericBaseName(TypeInfo type) =>
        GetGenericBaseNameStatic(type: type);

    /// <summary>Guarded helper for resolved-generic base-name lookups.</summary>
    internal static string? GetGenericBaseNameStatic(TypeInfo type) =>
        GetGenericBaseStatic(type: type)
          ?.Name;

    #endregion

    #region Public API

    /// <summary>
    /// Generates LLVM IR for the entire program.
    /// </summary>
    /// <returns>The generated LLVM IR as a string.</returns>
    /// <summary>
    /// When true, prints per-phase wall-clock timings to stderr. Set externally before calling
    /// <see cref="Generate"/>. Mirrors the <c>sa-timing</c> manifest flag for codegen visibility.
    /// </summary>
    public bool Timing { get; set; }

    /// <summary>
    /// The manifest executable module (the entry file's module). Selects which <c>start</c> becomes
    /// the program entry when several modules define one (e.g. a test harness importing many
    /// modules). Null/empty for a single-module program, where the sole <c>start</c> is used.
    /// </summary>
    public string? EntryModule { get; init; }

    /// <summary>Generates LLVM IR for all user programs and returns it as a string.</summary>
    public string Generate()
    {
        bool timing = Timing;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Mark(string label)
        {
            if (!timing) return;
            sw.Stop();
            Console.Error.WriteLine(value: $"[CG] {label}: {sw.ElapsedMilliseconds} ms");
            sw.Restart();
        }

        // Stage 1: Generate all type declarations
        GenerateTypeDeclarations();
        Mark(label: "Stage 1 TypeDeclarations");

        // Stage 1b: Emit Suflae module-level `global` storage before routine bodies reference it.
        GenerateGlobalVariables();
        Mark(label: "Stage 1b GlobalVariables");

        // Stage 2: Generate function declarations (signatures)
        GenerateRoutineDeclarations();
        Mark(label: "Stage 2 RoutineDeclarations");

        // Stage 3: Generate function definitions (bodies)
        GenerateRoutineDefinitions();
        Mark(label: "Stage 3 RoutineDefinitions");

        // Stage 4: Generate runtime support (if needed)
        GenerateRuntimeSupport();
        Mark(label: "Stage 4 RuntimeSupport");

        // Combine all sections
        string output = BuildOutput();
        Mark(label: "BuildOutput");
        return output;
    }

    /// <summary>
    /// Base-mode emission (resident-JIT incremental Phase 0a): emits the stdlib as a NON-PRUNED
    /// precompiled base — every declared stdlib body, no user code, and NO <c>@main</c> (the delta module
    /// supplies the entry point). Construct this generator with an EMPTY <c>userPrograms</c> and
    /// <c>liveRoutineKeys: null</c> (⇒ empty live set ⇒ the reachability gate is off ⇒ non-pruned). Returns
    /// the base IR plus the mangled symbols it defined — that symbol set becomes the <c>residentSymbols</c>
    /// fed to the delta build's generator (which then emits those as extern <c>declare</c>s via C4). See
    /// <c>internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md</c> §2A.5 / Phase 0a.
    /// </summary>
    public (string ir, IReadOnlyCollection<string> symbols) GenerateBase()
    {
        _baseMode = true;
        string ir = Generate();
        return (ir, EmittedRoutineSymbols);
    }

    #endregion

    #region Code Generation Phases

    /// <summary>
    /// Generates LLVM type declarations for all types in the registry.
    /// </summary>
    private void GenerateTypeDeclarations()
    {
        // When reachability ran (real builds), SKIP the broad registry type sweep entirely: every
        // struct that emitted code uses is generated on-demand — records & variants via GetLlvmType,
        // entities & crashables via Get{Entity,Crashable}TypeName at their alloc/access/size sites,
        // and nested by-value field types recursively via EnsureTypeGenerated. The no-reachability
        // config (unit tests without RoutineReachabilityPass) still needs the full sweep.
        if (_liveRoutineKeys.Count != 0)
        {
            return;
        }

        GenerateEntityTypeDeclarations();

        // Generate crashable types (always entity semantics — heap-allocated error types)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Crashable))
        {
            if (type is CrashableTypeInfo crashable)
            {
                GenerateCrashableType(crashable: crashable);
            }
        }

        GenerateRecordTypeDeclarations();

        // Generate variant types (tagged unions -> tag + payload record)
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Variant))
        {
            if (type is VariantTypeInfo { IsGenericDefinition: false } variant)
            {
                GenerateVariantType(variant: variant);
            }
        }
    }

    /// <summary>Generates struct types for all concrete entity resolutions in the registry.</summary>
    private void GenerateEntityTypeDeclarations()
    {
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Entity))
        {
            // Skip resolutions with unresolved generic parameters at any depth (e.g.
            // List[BTreeSetNode[T]] where T is nested inside a type argument).
            if (type is EntityTypeInfo { IsGenericDefinition: false } entity &&
                entity.TypeArguments?.Any(predicate: ContainsGenericParameter) != true)
            {
                GenerateEntityType(entity: entity);
            }
        }
    }

    /// <summary>Generates struct types for all concrete record resolutions in the registry.</summary>
    private void GenerateRecordTypeDeclarations()
    {
        foreach (TypeInfo type in _registry.GetTypesByCategory(category: TypeCategory.Record))
        {
            if (type is RecordTypeInfo { IsGenericDefinition: false } record &&
                record.TypeArguments?.Any(predicate: t =>
                    ContainsGenericParameter(t) || t is ErrorTypeInfo ||
                    ContainsAbstractProjection(t)) != true)
            {
                GenerateRecordType(record: record);
            }
        }
    }

    /// <summary>
    /// Checks if a type contains unresolved generic parameters at any nesting depth.
    /// </summary>
    /// <summary>
    /// True if <paramref name="type"/> is, or contains, an unresolved associated-type projection
    /// ('Me/Value', 'S/Iter'). Such a type is abstract until monomorphization resolves the slot, so
    /// a record instantiation built over it (e.g. Maybe[Me/Value] from an abstract protocol memberRoutine
    /// signature) is not emittable LLVM IR and must be skipped at the record-declaration sites.
    /// Kept separate from <see cref="ContainsGenericParameter"/> so routine emission is unaffected.
    /// </summary>
    private static bool ContainsAbstractProjection(TypeInfo type)
    {
        if (type is AssociatedProjectionTypeInfo)
        {
            return true;
        }

        return type.TypeArguments?.Any(predicate: ContainsAbstractProjection) == true;
    }

    private static bool ContainsGenericParameter(TypeInfo type)
    {
        // A comptime const-generic (`${…}` payload-size splice) that has FOLDED to a literal constant
        // (e.g. the `128` width arg of UnpackedFloat[U128, U256, 128]) is concrete — it mangles to a fixed
        // value and its LLVM layout is fixed. Only a STILL-UNFOLDED one (an expression over an unresolved
        // type param) is non-concrete. Without distinguishing these, a folded-const instance is wrongly
        // treated as generic, so its routine declaration is SKIPPED while its call site still emits the
        // concrete mangled name → "use of undefined value" at LLVM parse (surfaced by the resident-JIT base).
        if (type is ComptimeConstGenericTypeInfo cc)
            return !cc.TryFold(resolveTypeParam: _ => null, pointerSize: 8, result: out _);
        if (type is GenericParameterTypeInfo or ErrorTypeInfo)
        {
            return true;
        }

        // Protocol self-type ('Me') has no concrete LLVM representation — treat the same as an
        // unresolved generic parameter so that abstract protocol memberRoutine stubs are never declared.
        // Build-time dispatch: concrete implementers emit their own declarations; the abstract
        // stub with 'Me' in its signature is never valid LLVM IR.
        if (type is ProtocolSelfTypeInfo)
        {
            return true;
        }

        // Types annotated @llvm("...") always map to a fixed LLVM type regardless of type
        // arguments — treat as concrete (e.g. Hijacked[DictEntry[K,V]] -> ptr is valid LLVM IR).
        if (type is RecordTypeInfo { BackendType: not null })
        {
            return false;
        }

        if (type.TypeArguments == null)
        {
            return false;
        }

        return type.TypeArguments.Any(predicate: ContainsGenericParameter);
    }

    /// <summary>
    /// True if a routine's SIGNATURE (return type or any parameter type) still carries an unresolved
    /// generic parameter — a template that is NOT caught by <see cref="RoutineInfo.IsGenericDefinition"/>
    /// (which only covers a routine's OWN declared type params). Example:
    /// <c>List[Character].from_literal(elements: Array[Character, __Vararg0])</c> — the owner is concrete
    /// but the const-generic array arity <c>__Vararg0</c> is unresolved, so emitting it yields malformed IR
    /// (<c>[__Vararg0 x i32]</c>). The normal (pruned) build never reaches such a routine because it is
    /// never live; base-mode emission (<see cref="_baseMode"/>) has no liveness gate, so it must skip these
    /// explicitly — they are materialized on demand (§2A.5), never in the non-pruned base.
    /// </summary>
    private static bool SignatureHasUnresolvedGeneric(RoutineInfo r)
    {
        if (r.ReturnType is TypeInfo rt && SignatureTypeIsUnresolved(t: rt)) return true;
        foreach (ParameterInfo p in r.Parameters)
        {
            if (p.Type is TypeInfo pt && SignatureTypeIsUnresolved(t: pt)) return true;
        }
        return false;
    }

    /// <summary>
    /// A signature type is unresolved (⇒ a template, not emittable) if it contains a generic parameter OR
    /// mentions the internal variadic const-generic marker <c>__Vararg</c>. The latter is checked BY NAME
    /// because a not-yet-arity-monomorphized <c>Array[T, __VarargN]</c> carries <c>__VarargN</c> as a plain
    /// unresolved <see cref="TypeInfo"/> (not a <see cref="GenericParameterTypeInfo"/>), so
    /// <see cref="ContainsGenericParameter"/> misses it. A properly instantiated variadic (arity bound to a
    /// concrete number, e.g. <c>Array[Character, 3]</c>) does NOT mention <c>__Vararg</c>, so a concrete
    /// routine is never wrongly skipped. See VariadicParamDesugar (<c>__Vararg</c> prefix).
    /// </summary>
    private static bool SignatureTypeIsUnresolved(TypeInfo t)
    {
        if (ContainsGenericParameter(type: t)) return true;
        if (t.Name.Contains(value: "__Vararg", comparisonType: StringComparison.Ordinal)) return true;
        return t.TypeArguments?.Any(predicate: SignatureTypeIsUnresolved) == true;
    }

    /// <summary>
    /// Generates LLVM function declarations (signatures only).
    /// Only emits 'declare' for external routines that don't have bodies.
    /// Routines with bodies (user program and stdlib) are handled by GenerateRoutineDefinitions().
    /// </summary>
    /// <summary>
    /// Emits one zero-initialized LLVM <c>@global</c> per Suflae module-level <c>global</c> declaration
    /// and records the name-&gt;symbol mapping in <see cref="_moduleGlobals"/>. The initializer itself is
    /// NOT emitted here — <c>InjectGlobalInitializers</c> moved it into a runtime assignment at the top of
    /// <c>start()</c>, so the storage only needs a zero placeholder. The global's resolved type comes from
    /// the registry (the declaration's initializer was stripped by that injection).
    /// </summary>
    private void GenerateGlobalVariables()
    {
        foreach ((Program program, string _, string module) in _userPrograms)
        {
            foreach (ISyntaxTreeNode decl in program.Declarations)
            {
                if (decl is not VariableDeclaration { IsGlobal: true } g)
                {
                    continue;
                }

                TypeInfo? type = _registry.LookupVariable(name: g.Name)?.Type;
                if (type == null)
                {
                    continue; // SA registered every global; skip defensively if absent
                }

                string llvmType = GetValueLlvmType(type: type);
                string qualified = string.IsNullOrEmpty(value: module) ? g.Name : $"{module}.{g.Name}";
                string symbol = $"@\"global.{qualified}\"";

                EmitLine(sb: _globalDeclarations, line: $"{symbol} = global {llvmType} zeroinitializer");
                _moduleGlobals[key: g.Name] = (type, symbol);
            }
        }
    }

    private void GenerateRoutineDeclarations()
    {
        // Build set of routine names that have bodies (in user programs or stdlib)
        HashSet<string> routinesWithBodies = CollectRoutinesWithBodies();

        foreach (RoutineInfo routine in _registry.GetAllRoutines())
        {
            if (!ShouldSkipRoutineDeclaration(routine: routine,
                    routinesWithBodies: routinesWithBodies))
            {
                // Only emit 'declare' for truly external routines
                GenerateRoutineDeclaration(routine: routine);
            }
        }
    }

    /// <summary>Collects the names of routines that have bodies (user program + stdlib).</summary>
    private HashSet<string> CollectRoutinesWithBodies()
    {
        var routinesWithBodies = new HashSet<string>();
        foreach ((Program program, string _, string _) in _userPrograms.Concat(second: _stdlibPrograms))
        {
            foreach (ISyntaxTreeNode decl in program.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    routinesWithBodies.Add(item: routine.QualifiedName);
                }
            }
        }
        return routinesWithBodies;
    }

    /// <summary>
    /// Whether a routine needs no external <c>declare</c>: generic definitions / unresolved-type /
    /// generic-owner / synthesized / protocol-abstract routines, and non-extern routines that have
    /// an emitted body. C-externs are always declared (they never have an RF body and their bare
    /// symbol can collide with a same-named RF wrapper overload).
    /// </summary>
    private static bool ShouldSkipRoutineDeclaration(RoutineInfo routine,
        HashSet<string> routinesWithBodies)
    {
        if (routine.IsGenericDefinition || HasErrorTypes(routine: routine) ||
            routine.OwnerType is { IsGenericDefinition: true } or GenericParameterTypeInfo ||
            routine.IsSynthesized || routine.OwnerType is ProtocolTypeInfo)
        {
            return true;
        }

        // C-externs are declared unconditionally (declarations dedupe by symbol name).
        if (routine.CallingConvention == "C")
        {
            return false;
        }

        // Skip routines that have bodies (emitted as 'define' in GenerateRoutineDefinitions).
        string fullName = routine.OwnerType != null
            ? $"{routine.OwnerType.Name}.{routine.Name}"
            : routine.Name;
        return routinesWithBodies.Contains(item: routine.Name) ||
               routinesWithBodies.Contains(item: fullName);
    }

    /// <summary>
    /// Checks if a routine has any error types in its signature.
    /// </summary>
    private static bool HasErrorTypes(RoutineInfo routine)
    {
        // Check return type
        if (routine.ReturnType?.Category == TypeCategory.Error)
        {
            return true;
        }

        // Check parameter types
        foreach (ParameterInfo param in routine.Parameters)
        {
            if (param.Type.Category == TypeCategory.Error)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Generates LLVM function definitions (with bodies).
    /// Includes both user program routines and stdlib routines (for intrinsics).
    /// </summary>
    private void GenerateRoutineDefinitions()
    {
        // First, generate user program routines (these take priority)
        foreach ((Program userProgram, string _, string userModule) in _userPrograms)
        {
            foreach (ISyntaxTreeNode decl in userProgram.Declarations)
            {
                if (decl is RoutineDeclaration routine)
                {
                    GenerateRoutineDefinition(routine: routine, moduleContext: userModule);
                }
            }
        }

        // Unified loop: compile stdlib bodies, emit pre-built generic bodies, generate
        // synthesized routines, and then build runtime dispatch stubs. Codegen no longer
        // performs generic discovery or on-demand monomorphization.
        int prevDefCount;
        int prevDeclCount;
        int iterations = 0;
        const int maxIterations = 100;
        do
        {
            prevDefCount = _generatedRoutineDefs.Count;
            prevDeclCount = _generatedRoutines.Count;

            // Phase A: Compile stdlib routine bodies for referenced routines
            foreach ((Program program, string _, string module) in _stdlibPrograms)
            {
                foreach (RoutineDeclaration routine in EnumerateStdlibRoutines(program: program))
                {
                    RoutineInfo? routineInfo = ResolveStdlibRoutineInfo(routine: routine,
                        module: module);

                    if (routineInfo == null || routineInfo.IsGenericDefinition)
                    {
                        continue;
                    }

                    // Base mode has no liveness gate to hide TEMPLATE routines whose IsGenericDefinition is
                    // false but whose signature still carries an unresolved generic parameter (const-generic
                    // arity like __Vararg0). Emitting one yields malformed IR; such templates instantiate on
                    // demand (§2A.5), never in the non-pruned base. No-op for the normal path (_baseMode off).
                    if (_baseMode && SignatureHasUnresolvedGeneric(r: routineInfo))
                    {
                        continue;
                    }

                    if (HasErrorTypes(routine: routineInfo))
                    {
                        continue;
                    }

                    // Only generate definitions for routines that were declared
                    string funcName = MangleRoutineName(routine: routineInfo);
                    if (!_generatedRoutines.Contains(item: funcName))
                    {
                        continue;
                    }

                    // Reachability gate: when LiveRoutineKeys is populated, skip stdlib
                    // routines not reachable from program entry points. This prevents the
                    // lazy-declaration cascade (declare X -> emit X -> declare Y -> emit Y)
                    // from pulling in entire stdlib subgraphs (SortedList, BTreeNode, etc.)
                    // that the user program never invokes.
                    if (_liveRoutineKeys.Count > 0
                        && !IsRoutineReferenced(registryKey: routineInfo.RegistryKey))
                    {
                        continue;
                    }

                    // Skip if already defined, or resident (body lives in the base dylib — emit as an
                    // extern declare only; the resident set is empty in the cold/AOT path).
                    if (_generatedRoutineDefs.Contains(item: funcName) || IsResident(mangledFuncName: funcName))
                    {
                        continue;
                    }

                    // No swallow: a body reaching codegen must be fully resolved by SA / lowering /
                    // monomorphization. A failure here is an upstream contract violation and MUST be
                    // loud (mirrors MonomorphizationCompletenessAssertionPass), not a silent warning.
                    GenerateRoutineDefinition(routine: routine, preResolvedInfo: routineInfo);
                }
            }

            // Phase B: Emit pre-built instantiated generic bodies (monomorphized by GMP in Phase 7).
            // Gate on the live-routine set so dead instantiations don't drag in their callees.
            foreach ((string _, MonomorphizedBody body) in _instantiatedGenericBodies)
            {
                string instFuncName = MangleRoutineName(routine: body.Info);
                if (_generatedRoutineDefs.Contains(item: instFuncName)
                    || IsResident(mangledFuncName: instFuncName)) continue;
                if (_liveRoutineKeys.Count > 0
                    && !IsRoutineReferenced(registryKey: body.Info.RegistryKey))
                    continue;
                // Base mode: skip a template whose signature still carries an unresolved generic param
                // (e.g. a const-generic __Vararg arity) — it instantiates on demand (§2A.5), never in base.
                if (_baseMode && SignatureHasUnresolvedGeneric(r: body.Info)) continue;

                // No swallow (see Phase A): a monomorphized body that fails codegen is an upstream
                // resolution bug — fail loudly rather than dropping the definition (which would only
                // resurface downstream as an "undefined symbol" linker error).
                if (body.IsSynthesized)
                {
                    // Empty-body sentinel — pure IR-level synthesis not yet wired; skip.
                    if (body.Ast.Body is BlockStatement { Statements.Count: 0 })
                    {
                        continue;
                    }

                    _generatedRoutineDefs.Add(item: instFuncName);
                    _generatedRoutines.Add(item: instFuncName);
                    EmitSynthesizedBodyFromAst(routine: body.Info, funcName: instFuncName,
                        body: body.Ast.Body);
                }
                else
                {
                    GenerateRoutineDefinition(routine: body.Ast, preResolvedInfo: body.Info);
                }
            }

            // Phase C: Emit synthesized variant bodies (try_/check_/lookup_ and derived operators).
            // Gate on the live-routine set: a synthesized ne body for a dead type would call a
            // dead eq, leaving the linker hanging on the dead eq symbol.
            foreach ((string key, Statement synthBodyAst) in _synthesizedBodies)
            {
                EmitPhaseCSynthesizedBody(key: key, synthBodyAst: synthBodyAst);
            }

            iterations++;
            if (iterations >= maxIterations)
            {
                Console.Error.WriteLine(
                    value:
                    $"Warning: GenerateFunctionDefinitions reached {maxIterations} iterations, possible infinite loop");
                break;
            }
        } while (_generatedRoutineDefs.Count > prevDefCount ||
                 _generatedRoutines.Count > prevDeclCount);

        if (Compiler.Diagnostics.DiagnosticFlags.PruneStats)
        {
            int liveNotRef = _liveRoutineKeys.Count(predicate: k => !_referencedKeys.Contains(item: k));
            Console.Error.WriteLine(
                value:
                $"[PRUNE-STATS] live={_liveRoutineKeys.Count} referenced={_referencedKeys.Count} " +
                $"defs={_generatedRoutineDefs.Count} expectedBodies={_expectedBodyNames.Count} " +
                $"live_not_referenced={liveNotRef}");
        }

        // Over-prune tripwire (only meaningful when reachability gating is active; with no live set
        // nothing is pruned, so every referenced routine is emitted and this is trivially satisfied).
        // Every routine an emitted body actually references must itself have been emitted. A name in
        // _expectedBodyNames with no define means RoutineReachabilityPass dropped a routine that
        // emitted code calls — caught here as a located codegen error instead of a linker
        // "undefined symbol" far downstream.
        if (_liveRoutineKeys.Count > 0)
        {
            // Realm-INSENSITIVE match (mirrors the emission gate): a collapsed SF wrapper emits its
            // universal members under the ambient RF symbol, but the single-program codegen path can
            // record the SF-mangled variant as expected. The .ll is realm-consistent (build/link prove
            // it), so compare realm-stripped — else a benign SF/RF bookkeeping mismatch false-positives.
            HashSet<string> strippedDefs = _generatedRoutineDefs
                                          .Select(selector: StripRealmMarker)
                                          .ToHashSet();
            // Resident symbols are DEFINED in the base dylib, not this delta module — a referenced-but-
            // not-defined-here resident is expected, not an over-prune. Empty in the cold/AOT path.
            HashSet<string> strippedResident = _residentSymbols
                                              .Select(selector: StripRealmMarker)
                                              .ToHashSet();
            List<string> overPruned = _expectedBodyNames
                                     .Where(predicate: name => !_generatedRoutineDefs.Contains(item: name)
                                          && !strippedDefs.Contains(item: StripRealmMarker(mangledName: name))
                                          && !_residentSymbols.Contains(item: name)
                                          && !strippedResident.Contains(item: StripRealmMarker(mangledName: name)))
                                     .OrderBy(keySelector: name => name, comparer: StringComparer.Ordinal)
                                     .ToList();
            if (overPruned.Count > 0)
            {
                string sample = string.Join(separator: "\n",
                    values: overPruned.Take(count: 20).Select(selector: n => $"  @{n}"));
                string more = overPruned.Count > 20 ? $"\n  … and {overPruned.Count - 20} more" : "";
                throw new InvalidOperationException(
                    message:
                    $"{sample}{more}\nCodegen bug: {overPruned.Count} referenced routine(s) were declared and called " +
                    "but never defined. Reachability pruned a routine that emitted code calls. " +
                    "This would surface as a linker \"undefined symbol\"; catching it here instead.\n" +
                    sample + more);
            }
        }
    }

    /// <summary>
    /// Phase C: emits one synthesized-variant body (try_/check_/lookup_ / derived operator) keyed by
    /// <paramref name="key"/>, applying all the liveness / generic-owner gates. A generic-def owner is
    /// dispatched to the per-concrete-instantiation emitters; a concrete owner is emitted directly.
    /// </summary>
    private void EmitPhaseCSynthesizedBody(string key, Statement synthBodyAst)
    {
        RoutineInfo? synthInfo = _registry.LookupRoutine(fullName: key);
        if (synthInfo == null || synthInfo.IsGenericDefinition) return;
        // Base mode: skip a template whose signature still carries an unresolved generic param
        // (e.g. from_literal(elements: Array[T, __Vararg0])) — instantiated on demand (§2A.5).
        if (_baseMode && SignatureHasUnresolvedGeneric(r: synthInfo)) return;
        // Wrapper-forwarder synthesized bodies are anchored on the generic-def owner
        // (e.g. Retained[T].eq). Reachability seeds the *concrete* monomorphizations
        // (Retained[Text].eq), not the gen-def routine itself, so the gen-def synth
        // would always fail this gate. The inner per-concrete loop below has its own
        // liveness check (_generatedRoutines.Contains), so it's safe to bypass here.
        bool isWrapperForwarderGenDef =
            synthInfo is { IsSynthesized: true, WrapperForwarderInnerMemberRoutine: not null }
            && synthInfo.OwnerType?.IsGenericDefinition == true;
        if (!isWrapperForwarderGenDef
            && _liveRoutineKeys.Count > 0
            && !IsRoutineReferenced(registryKey: synthInfo.RegistryKey))
            return;
        // Skip routines whose owner type still has unresolved generic parameters
        // (e.g. represent/hash on DictEntry[K, V] — the generic definition).
        // IsGenericDefinition only covers routines with their own type params (like
        // hijacked_from[T]); owner-generic types need a separate guard.
        if (synthInfo.OwnerType != null && ContainsGenericParameter(synthInfo.OwnerType))
            return;
        // Skip derived operators on generic owner types (e.g. ArrayIterator.ne).
        // GMP monomorphizes these into InstantiatedGenericBodies (Phase B); emitting the
        // generic-def version here would call a non-existent generic eq/contains.
        // Exception: synthesized wrapper forwarder bodies (T.key_get, etc.) are
        // anchored on the generic-def owner by design. For each concrete resolution,
        // emit the body with the wrapper's type parameter substituted.
        if (synthInfo.OwnerType?.IsGenericDefinition == true)
        {
            // Non-wrapper synthesized bodies on generic-def owners (try_emit, represent,
            // diagnose, hash, eq for generic types like ListEmitter[T], List[T]).
            // For each live concrete instantiation of this owner, lookup the substituted
            // memberRoutine (LookupMemberRoutine normalizes generic-def memberRoutines onto concrete owners),
            // rewrite the shared body to a fully concrete form, and emit one per owner.
            if (synthInfo is { IsSynthesized: true, WrapperForwarderInnerMemberRoutine: null }
                && synthInfo.OwnerType.GenericParameters is { Count: > 0 } gParams)
            {
                EmitSynthesizedBodyPerConcreteOwner(synthInfo: synthInfo,
                    synthBodyAst: synthBodyAst, gParams: gParams);
            }
            if (synthInfo is { IsSynthesized: true, WrapperForwarderInnerMemberRoutine: not null } &&
                synthInfo.OwnerType.GenericParameters is { Count: 1 } wrapperParams)
            {
                EmitWrapperForwarderBodyPerConcreteInner(synthInfo: synthInfo,
                    synthBodyAst: synthBodyAst, wrapperParamName: wrapperParams[0]);
            }
            return;
        }
        string synthFuncName = MangleRoutineName(routine: synthInfo);
        if (_generatedRoutineDefs.Contains(item: synthFuncName)) return;
        _generatedRoutineDefs.Add(item: synthFuncName);
        _generatedRoutines.Add(item: synthFuncName);
        // No swallow (see Phase A): a synthesized body that fails codegen is an upstream
        // synthesis/resolution bug and MUST be loud, not a dropped definition (which would only
        // resurface as a downstream "undefined symbol" linker error).
        EmitSynthesizedBodyFromAst(routine: synthInfo, funcName: synthFuncName,
            body: synthBodyAst);
    }

    /// <summary>
    /// Emits a non-wrapper synthesized body once per live concrete instantiation of its generic-def
    /// owner, rewriting the shared AST to a fully concrete form for each.
    /// </summary>
    private void EmitSynthesizedBodyPerConcreteOwner(RoutineInfo synthInfo, Statement synthBodyAst,
        IReadOnlyList<string> gParams)
    {
        TypeInfo genericOwner = synthInfo.OwnerType!;
        foreach (TypeInfo candidateOwner in _registry.AllConcreteGenericInstancesUnfiltered.ToList())
        {
            if (candidateOwner.IsGenericDefinition) continue;
            if (candidateOwner.TypeArguments is not { Count: > 0 } tArgs) continue;
            if (tArgs.Count != gParams.Count) continue;
            // Match by generic-def reference: candidate must be an instantiation of genericOwner.
            TypeInfo? candidateGenDef = candidateOwner switch
            {
                RecordTypeInfo r => r.GenericDefinition,
                EntityTypeInfo e => e.GenericDefinition,
                WrapperTypeInfo w => _registry.LookupType(name: w.Name),
                _ => null
            };
            if (candidateGenDef == null
                || !ReferenceEquals(objA: candidateGenDef, objB: genericOwner))
                continue;
            if (_liveOwnerTypeNames.Count > 0
                && !_liveOwnerTypeNames.Contains(item: candidateOwner.FullName))
                continue;
            RoutineInfo? concreteMemberRoutine = _registry.LookupMemberRoutine(
                type: candidateOwner, memberRoutineName: synthInfo.Name);
            if (concreteMemberRoutine == null) continue;
            if (_liveRoutineKeys.Count > 0
                && !IsRoutineReferenced(registryKey: concreteMemberRoutine.RegistryKey))
                continue;
            string monoFuncName = MangleRoutineName(routine: concreteMemberRoutine);
            if (_generatedRoutineDefs.Contains(item: monoFuncName)
                || IsResident(mangledFuncName: monoFuncName)) continue;
            var newSubs = new Dictionary<string, TypeInfo>(comparer: StringComparer.Ordinal);
            for (int gi = 0; gi < gParams.Count; gi++)
                newSubs[key: gParams[index: gi]] = tArgs[index: gi];
            // The GenericAstRewriter call below already produces a fully concrete body
            // from newSubs — codegen needs no live substitution map. No swallow (see
            // Phase A): a failure here is an upstream monomorph/resolution bug and must
            // be loud, not a dropped definition.
            _generatedRoutineDefs.Add(item: monoFuncName);
            _generatedRoutines.Add(item: monoFuncName);
            // Rewrite the shared generic-def body per concrete owner BEFORE
            // emission. The raw AST is shared across every instantiation, so
            // BuilderQueryInliningPass had to defer folding its BuilderQuery
            // constants (me.type_name() in synthesized represent/diagnose).
            // GenericAstRewriter deep-clones, substitutes the type params, folds
            // those constants against the concrete owner (same fold logic as the
            // inlining pass), and re-resolves routine bindings — emitting the
            // unrewritten body instead fails with unresolved-call errors.
            var monoStringSubs = newSubs.ToDictionary(
                keySelector: kvp => kvp.Key,
                elementSelector: kvp => kvp.Value.FullName);
            Statement rewrittenSynthBody = GenericAstRewriter.RewriteStatement(
                stmt: synthBodyAst,
                subs: monoStringSubs,
                typeSubs: newSubs,
                registry: _registry,
                enclosingRoutine: concreteMemberRoutine);
            EmitSynthesizedBodyFromAst(routine: concreteMemberRoutine,
                funcName: monoFuncName, body: rewrittenSynthBody);
        }
    }

    /// <summary>
    /// Emits a wrapper-forwarder synthesized body once per concrete single-arg wrapper resolution,
    /// substituting the wrapper's sole type parameter with each concrete inner type.
    /// </summary>
    private void EmitWrapperForwarderBodyPerConcreteInner(RoutineInfo synthInfo,
        Statement synthBodyAst, string wrapperParamName)
    {
        foreach (RoutineInfo concreteWf in _registry.GetAllRoutineResolutions())
        {
            if (!concreteWf.IsSynthesized ||
                concreteWf.WrapperForwarderInnerMemberRoutine == null ||
                !ReferenceEquals(objA: concreteWf.GenericDefinition, objB: synthInfo) ||
                concreteWf.OwnerType?.TypeArguments is not { Count: 1 })
                continue;
            string concreteFuncName = MangleRoutineName(routine: concreteWf);
            if (!_generatedRoutines.Contains(item: concreteFuncName))
                continue;
            if (_generatedRoutineDefs.Contains(item: concreteFuncName)
                || IsResident(mangledFuncName: concreteFuncName))
                continue;
            TypeInfo concreteInner = concreteWf.OwnerType!.TypeArguments![0];
            // Rewrite the shared wrapper-forwarder body to a fully concrete form
            // per inner type BEFORE emission (mirrors the non-wrapper synth loop
            // above), so codegen needs no live substitution map.
            var wfSubs = new Dictionary<string, TypeInfo>
                { [wrapperParamName] = concreteInner };
            // No swallow (see Phase A): a wrapper-forwarder body that fails codegen is
            // an upstream resolution bug — fail loudly, don't drop the definition.
            _generatedRoutineDefs.Add(item: concreteFuncName);
            _generatedRoutines.Add(item: concreteFuncName);
            Statement rewrittenWfBody = GenericAstRewriter.RewriteStatement(
                stmt: synthBodyAst,
                subs: wfSubs.ToDictionary(kvp => kvp.Key,
                    kvp => kvp.Value.FullName),
                typeSubs: wfSubs,
                registry: _registry,
                enclosingRoutine: concreteWf);
            EmitSynthesizedBodyFromAst(routine: concreteWf,
                funcName: concreteFuncName, body: rewrittenWfBody);
        }
    }

    /// <summary>
    /// Resolves the concrete <see cref="RoutineInfo"/> for a stdlib <see cref="RoutineDeclaration"/>
    /// (Phase A): prefers the registration-time structured binding, then multi-key registry lookups,
    /// then overload disambiguation by parameter types, then a failable-flag correction. Returns null
    /// when no routine info can be found.
    /// </summary>
    private RoutineInfo? ResolveStdlibRoutineInfo(RoutineDeclaration routine, string module)
    {
        // Prefer the structured binding attached at registration (StdlibLoader /
        // SignatureResolver) — the exact overload for THIS decl. This is the only
        // reliable identity for a CONSTRUCTOR (`routine T(...)`), whose AST name is the
        // bare type ("U64") with no ".create" for the name-string lookups below to key on.
        // Look up routine info — try multiple keys:
        // 1. Raw AST name (e.g., "show")
        // 2. Module-qualified (e.g., "IO.show")
        // 3. Short name fallback via LookupRoutineByName
        // 4. Overload-based lookup using AST parameter types
        RoutineInfo? routineInfo = routine.ResolvedInfo
                                   ?? _registry.LookupRoutine(fullName: routine.QualifiedName);
        if (routineInfo == null && !string.IsNullOrEmpty(value: module))
        {
            routineInfo =
                _registry.LookupRoutine(fullName: $"{module}.{routine.QualifiedName}");
        }

        if (routineInfo == null)
        {
            if (routine.OwnerName is { } ownerPart && routine.MemberRoutineName is { } shortName)
            {
                // Member declaration (e.g. "UnpackedFloat[M, L, W].cbrt"). Resolve scoped
                // to the owner type FIRST — never fall through to a bare short-name lookup
                // that could bind a same-named free/external routine of a different owner
                // (which would emit this memberRoutine's body under the wrong identity →
                // "Unresolved generic member routine" at codegen). OwnerName is the args-stripped
                // owner base (structural, from the parser).
                TypeInfo? ownerType = _registry.LookupType(name: ownerPart);
                if (ownerType != null)
                {
                    routineInfo = _registry.LookupMemberRoutine(type: ownerType,
                        memberRoutineName: shortName);
                }

                routineInfo ??= _registry.LookupRoutine(fullName: shortName) ??
                                _registry.LookupRoutineByName(name: shortName);
            }
            else
            {
                routineInfo = _registry.LookupRoutineByName(name: routine.QualifiedName);
            }
        }

        // For overloaded routines (e.g., create), try to find the
        // specific overload matching this AST declaration's parameter types.
        // This includes 0-arg overloads — LookupRoutine returns an arbitrary
        // overload, so we must disambiguate for all param counts. Skipped when
        // ResolvedInfo already pinned the exact overload for this decl.
        if (routineInfo != null && routine.ResolvedInfo == null)
        {
            routineInfo = DisambiguateStdlibOverload(routine: routine, routineInfo: routineInfo);
        }

        // Ensure the resolved routine's failable flag matches the AST routine.
        // When failable/non-failable overloads share the same name and parameter types
        // (e.g., interpret_as_utf8() and interpret_as_utf8!()), they collide in
        // the _routines dictionary under the same RegistryKey. The last registration
        // wins, making the first invisible to LookupRoutine. Use LookupMemberRoutine
        // (which indexes by owner type and preserves all overloads) to find the
        // correct variant.
        if (routineInfo != null && routineInfo.IsFailable != routine.IsFailable &&
            routineInfo.OwnerType != null)
        {
            RoutineInfo? corrected = _registry.LookupMemberRoutine(
                type: routineInfo.OwnerType,
                memberRoutineName: routineInfo.Name,
                isFailable: routine.IsFailable);
            if (corrected != null)
            {
                routineInfo = corrected;
            }
        }

        return routineInfo;
    }

    /// <summary>
    /// Disambiguates the resolved stdlib routine to the specific overload whose parameter types match
    /// the AST declaration — first via typed <c>LookupRoutineOverload</c>, then via a param-type-NAME
    /// fallback against the owner's candidate member routines (for generic param types LookupType
    /// misses, e.g. Hijacked[Byte]). Returns the best match, or <paramref name="routineInfo"/> unchanged.
    /// </summary>
    private RoutineInfo DisambiguateStdlibOverload(RoutineDeclaration routine, RoutineInfo routineInfo)
    {
        var astParamTypes = new List<TypeInfo>();
        foreach (Parameter param in routine.Parameters)
        {
            if (param.Type != null)
            {
                string typeName = param.Type.Name;
                if (param.Type.GenericArguments is { Count: > 0 })
                {
                    typeName =
                        $"{typeName}[{string.Join(separator: ", ", values: param.Type.GenericArguments.Select(selector: a => a.Name))}]";
                }

                TypeInfo? t = _registry.LookupType(name: typeName);
                if (t != null)
                {
                    astParamTypes.Add(item: t);
                }
            }
        }

        if (astParamTypes.Count == routine.Parameters.Count)
        {
            RoutineInfo? overload = _registry.LookupRoutineOverload(
                baseName: routineInfo.BaseName,
                argTypes: astParamTypes);
            if (overload != null)
            {
                routineInfo = overload;
            }
        }

        // Fallback: match AST declaration to the exact registry overload by
        // parameter type NAMES. LookupType may fail for generic param types
        // like Hijacked[Byte], so astParamTypes can be incomplete and
        // LookupRoutineOverload may return the wrong overload (or fail).
        // Build the AST param-type name list directly and match against
        // candidate parameter type names. Determine the owner type from the
        // AST routine name (e.g. "Bytes.create") rather than the possibly-
        // wrong initial routineInfo, since LookupRoutineByName returns an
        // arbitrary overload (possibly from a different type).
        TypeInfo? resolvedOwner = routineInfo.OwnerType;
        if (routine.RenderedReceiver is { } ownerName)
        {
            TypeInfo? t = _registry.LookupType(name: ownerName);
            if (t != null) resolvedOwner = t;
        }

        if (resolvedOwner != null)
        {
            RoutineInfo? nameMatch = MatchStdlibOverloadByParamNames(routine: routine,
                routineInfo: routineInfo, resolvedOwner: resolvedOwner);
            if (nameMatch != null)
            {
                routineInfo = nameMatch;
            }
        }

        return routineInfo;
    }

    /// <summary>
    /// Matches a stdlib routine declaration against the owner's candidate member routines by comparing
    /// normalized parameter type NAMES (and failability). Returns the matching routine or null.
    /// </summary>
    private RoutineInfo? MatchStdlibOverloadByParamNames(RoutineDeclaration routine,
        RoutineInfo routineInfo, TypeInfo resolvedOwner)
    {
        var astParamTypeNames = new List<string>();
        foreach (Parameter param in routine.Parameters)
        {
            if (param.Type == null)
            {
                astParamTypeNames.Clear();
                break;
            }

            string tn = param.Type.Name;
            if (param.Type.GenericArguments is { Count: > 0 })
            {
                tn =
                    $"{tn}[{string.Join(separator: ",", values: param.Type.GenericArguments.Select(selector: a => a.Name))}]";
            }

            astParamTypeNames.Add(item: tn);
        }

        if (astParamTypeNames.Count != routine.Parameters.Count)
        {
            return null;
        }

        var candidates = new List<RoutineInfo>();
        _registry.CollectMemberRoutineCandidates(type: resolvedOwner,
            memberRoutineName: routineInfo.Name,
            candidates: candidates);

        static string NormalizeTypeName(string n)
        {
            n = n.Replace(oldValue: " ", newValue: "");
            var sb = new StringBuilder(n.Length);
            var token = new StringBuilder();

            static void FlushToken(StringBuilder source,
                StringBuilder dest)
            {
                if (source.Length == 0)
                {
                    return;
                }

                string segment = source.ToString();
                int lastDot = segment.LastIndexOf(value: '.');
                dest.Append(lastDot >= 0
                    ? segment[(lastDot + 1)..]
                    : segment);
                source.Clear();
            }

            foreach (char ch in n)
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

        // Wrapper / generic TypeInfo.Name omits type arguments (e.g. a
        // Hijacked[Byte] parameter exposes Type.Name = "Hijacked"), so we
        // must rebuild "Name[arg1,arg2,...]" before comparing — otherwise
        // overload disambiguation can't distinguish Hijacked[Byte] from
        // Hijacked[Character] and silently falls through to a wrong overload.
        static string CandidateTypeName(TypeInfo t)
        {
            if (t.TypeArguments is { Count: > 0 } typeArgs && !t.Name.Contains(value: '['))
            {
                return $"{t.Name}[{string.Join(separator: ",", values: typeArgs.Select(selector: a => a.Name))}]";
            }
            return t.Name;
        }

        return candidates.FirstOrDefault(predicate: c =>
        {
            if (c.Parameters.Count != astParamTypeNames.Count)
                return false;
            if (c.IsFailable != routine.IsFailable) return false;
            for (int i = 0; i < astParamTypeNames.Count; i++)
            {
                string candName =
                    NormalizeTypeName(n: CandidateTypeName(c.Parameters[index: i].Type));
                string astName =
                    NormalizeTypeName(n: astParamTypeNames[index: i]);
                if (candName == astName) continue;
                return false;
            }

            return true;
        });
    }

    /// <summary>
    /// Enumerates all compilable RoutineDeclaration nodes from a stdlib program, including
    /// routines nested inside CrashableDeclaration.Members (e.g., crash_message synthesized
    /// from the "message:" directive). Nested routines are yielded with their names prefixed
    /// by the owning type name (e.g., "DivisionByZeroError.crash_message") so Phase A's
    /// registry lookup can find the registered memberRoutine.
    /// </summary>
    private static IEnumerable<RoutineDeclaration> EnumerateStdlibRoutines(Program program)
    {
        foreach (ISyntaxTreeNode decl in program.Declarations)
        {
            if (decl is RoutineDeclaration routine)
            {
                yield return routine;
            }
            else if (decl is CrashableDeclaration crashable)
            {
                foreach (SyntaxTree.Declaration member in crashable.Members)
                {
                    if (member is RoutineDeclaration memberRoutine)
                    {
                        yield return memberRoutine with
                        {
                            Name = $"{crashable.Name}.{memberRoutine.Name}"
                        };
                    }
                }
            }
        }
    }

    /// <summary>
    /// Generates runtime support functions.
    /// External("C") routines from NativeDeclarations.rf are declared via GenerateRoutineDeclarations().
    /// </summary>
    private static void GenerateRuntimeSupport()
    {
        // No-op: external("C") routines are handled by GenerateRoutineDeclarations()
        // via the TypeRegistry (registered from NativeDeclarations.rf).
    }

    /// <summary>
    /// Builds the final output by combining all sections.
    /// </summary>
    /// <returns>The complete LLVM IR module.</returns>
    private string BuildOutput()
    {
        var output = new StringBuilder();

        // Module header
        output.AppendLine(value: "; ModuleID = 'razorforge_module'");
        output.AppendLine(value: "source_filename = \"razorforge_module\"");
        output.AppendLine(handler: $"target datalayout = \"{_dataLayout}\"");
        output.AppendLine(handler: $"target triple = \"{_targetTriple}\"");
        output.AppendLine();

        AppendTypeDeclarations(output: output);

        // Global declarations
        if (_globalDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Global declarations");
            output.Append(value: _globalDeclarations);
            output.AppendLine();
        }

        // Native/extern function declarations (always emitted)
        if (_functionDeclarations.Length > 0)
        {
            output.AppendLine(value: "; Function declarations");
            output.Append(value: _functionDeclarations);
        }

        AppendRfForwardDeclarations(output: output);

        // Inline shadow-stack helpers (only when tracing is on)
        if (ShouldEmitTrace)
        {
            // A DELTA build (resident base present, not the base itself) must REFERENCE the base's shared
            // trace TLS globals as extern, not re-define them — else the base+delta JIT combine hits a
            // duplicate-definition of `__emutls_v._rf_trace_stack`. Base/normal-cold builds define them.
            AppendShadowStackHelpers(output: output,
                deltaMode: _residentSymbols.Count > 0 && !_baseMode);
        }

        // Auxiliary helper definitions
        if (_auxRoutineDefinitions.Length > 0)
        {
            output.AppendLine(value: "; Auxiliary function definitions");
            output.Append(value: _auxRoutineDefinitions);
        }

        // Function definitions
        if (_functionDefinitions.Length > 0)
        {
            output.AppendLine(value: "; Function definitions");
            output.Append(value: _functionDefinitions);
        }

        AppendMainEntryPoint(output: output);

        // Normalize to Unix line endings (clang/LLVM requires LF, not CRLF)
        var normalized = output.ToString()
                               .Replace(oldValue: "\r\n", newValue: "\n")
                               .Replace(oldValue: "\r", newValue: "\n");
        // TBAA first (tags loads/stores), then line-tables debug info (tags instructions + define
        // headers). Both are text post-passes that append their own metadata block; DI numbers itself
        // above TBAA's fixed !0..!22. ApplyDebugInfo is a no-op outside debug builds.
        return ApplyDebugInfo(ApplyTbaa(normalized));
    }

    /// <summary>
    /// Appends the type-declaration block (record -> variant -> entity -> crashable -> closure, each
    /// bucket already name-sorted), skipping empty buckets and the header when no types exist.
    /// </summary>
    private void AppendTypeDeclarations(StringBuilder output)
    {
        bool anyTypes = _typeDeclarationsRecord.Count > 0 || _typeDeclarationsVariant.Count > 0 ||
                        _typeDeclarationsEntity.Count > 0 || _typeDeclarationsCrashable.Count > 0 ||
                        _typeDeclarationsClosure.Count > 0;
        if (!anyTypes)
        {
            return;
        }

        output.AppendLine(value: "; Type declarations");

        void EmitTypeSection(string header, SortedDictionary<string, string> bucket)
        {
            if (bucket.Count == 0)
            {
                return;
            }
            output.AppendLine(handler: $"; -- {header} --");
            foreach (string decl in bucket.Values)
            {
                output.Append(value: decl);
            }
        }

        EmitTypeSection(header: "records", bucket: _typeDeclarationsRecord);
        EmitTypeSection(header: "variants", bucket: _typeDeclarationsVariant);
        EmitTypeSection(header: "entities", bucket: _typeDeclarationsEntity);
        EmitTypeSection(header: "crashables", bucket: _typeDeclarationsCrashable);
        EmitTypeSection(header: "closures", bucket: _typeDeclarationsClosure);
        output.AppendLine();
    }

    /// <summary>
    /// Appends the RF function forward declarations, skipping any symbol that now has a definition
    /// (define wins). A symbol with BOTH a declare and a define is an RF routine, so the two MUST
    /// describe the same function type — the signatures are asserted equal before the declare is
    /// dropped (a mismatch is an internal codegen bug).
    /// </summary>
    private void AppendRfForwardDeclarations(StringBuilder output)
    {
        foreach ((string name, string line) in _rfRoutineDeclarations)
        {
            if (!_generatedRoutineDefs.Contains(item: name))
            {
                output.AppendLine(value: line);
                continue;
            }

            if (_generatedRoutineDefHeaders.TryGetValue(key: name, value: out string? defHeader))
            {
                AssertDeclareDefineAgree(name: name, declLine: line, defHeader: defHeader);
            }
        }
    }

    /// <summary>Throws when a routine's forward declaration and emitted body disagree on type.</summary>
    private static void AssertDeclareDefineAgree(string name, string declLine, string defHeader)
    {
        string declSig = NormalizeFunctionSignature(header: declLine);
        string defSig = NormalizeFunctionSignature(header: defHeader);
        if (declSig == defSig)
        {
            return;
        }
        throw new InvalidOperationException(
            message:
            $"Codegen bug: declare/define signature mismatch for @{name}.\n" +
            $"  declare: {declSig}  ({declLine.Trim()})\n" +
            $"  define : {defSig}  ({defHeader.Trim()})\n" +
            "The forward declaration and the emitted body disagree on the function type. " +
            "This is an internal compiler error — the conversion/mangling path that built " +
            "the declare differs from the one that built the define.");
    }

    /// <summary>
    /// Appends the inline shadow-stack helpers (push/pop/update-loc/print). A 32-entry power-of-2
    /// ring; indices mask with AND so push/pop stay branchless. Only emitted when tracing is on.
    /// </summary>
    private static void AppendShadowStackHelpers(StringBuilder output, bool deltaMode = false)
    {
        output.AppendLine(value: "; Shadow stack (inline — no DLL call)");
        // Delta build references the base's TLS globals (extern, no initializer); base/normal defines them.
        if (deltaMode)
        {
            output.AppendLine(value: "@_rf_trace_stack = external thread_local global [32 x { ptr, ptr, i32, i32 }]");
            output.AppendLine(value: "@_rf_trace_depth = external thread_local global i32");
        }
        else
        {
            output.AppendLine(
                value:
                "@_rf_trace_stack = thread_local global [32 x { ptr, ptr, i32, i32 }] zeroinitializer");
            output.AppendLine(value: "@_rf_trace_depth = thread_local global i32 0");
        }
        output.AppendLine();
        // push helper — branchless: mask index to [0,31] with AND
        output.AppendLine(
            value:
            "define private void @_rf_trace_push(ptr %r, ptr %f, i32 %ln, i32 %col) alwaysinline {");
        output.AppendLine(value: EntryLabel);
        output.AppendLine(value: "  %d = load i32, ptr @_rf_trace_depth");
        output.AppendLine(value: "  %idx32 = and i32 %d, 31");
        output.AppendLine(value: "  %idx = zext i32 %idx32 to i64");
        output.AppendLine(
            value:
            "  %slot = getelementptr inbounds [32 x { ptr, ptr, i32, i32 }], ptr @_rf_trace_stack, i64 0, i64 %idx");
        output.AppendLine(
            value:
            "  %p0 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 0");
        output.AppendLine(value: "  store ptr %r, ptr %p0");
        output.AppendLine(
            value:
            "  %p1 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 1");
        output.AppendLine(value: "  store ptr %f, ptr %p1");
        output.AppendLine(
            value:
            "  %p2 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 2");
        output.AppendLine(value: "  store i32 %ln, ptr %p2");
        output.AppendLine(
            value:
            "  %p3 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 3");
        output.AppendLine(value: "  store i32 %col, ptr %p3");
        output.AppendLine(value: "  %nd = add i32 %d, 1");
        output.AppendLine(value: "  store i32 %nd, ptr @_rf_trace_depth");
        output.AppendLine(value: RetVoidInstruction);
        output.AppendLine(value: "}");
        output.AppendLine();
        // pop helper — branchless: depth is always > 0 when pop is called (paired with push)
        output.AppendLine(value: "define private void @_rf_trace_pop() alwaysinline {");
        output.AppendLine(value: EntryLabel);
        output.AppendLine(value: "  %d = load i32, ptr @_rf_trace_depth");
        output.AppendLine(value: "  %nd = add i32 %d, -1");
        output.AppendLine(value: "  store i32 %nd, ptr @_rf_trace_depth");
        output.AppendLine(value: RetVoidInstruction);
        output.AppendLine(value: "}");
        output.AppendLine();
        // update-loc helper — overwrites the line/col of the current (topmost) frame. Emitted before
        // each call so the trace reflects the call's source line. Skip when depth == 0 (no frame yet).
        output.AppendLine(
            value:
            "define private void @_rf_trace_update_loc(i32 %ln, i32 %col) alwaysinline {");
        output.AppendLine(value: EntryLabel);
        output.AppendLine(value: "  %d = load i32, ptr @_rf_trace_depth");
        output.AppendLine(value: "  %has = icmp ugt i32 %d, 0");
        output.AppendLine(value: "  br i1 %has, label %do_update, label %skip");
        output.AppendLine(value: "do_update:");
        output.AppendLine(value: "  %top = sub i32 %d, 1");
        output.AppendLine(value: "  %top32 = and i32 %top, 31");
        output.AppendLine(value: "  %top64 = zext i32 %top32 to i64");
        output.AppendLine(
            value:
            "  %slot = getelementptr inbounds [32 x { ptr, ptr, i32, i32 }], ptr @_rf_trace_stack, i64 0, i64 %top64");
        output.AppendLine(
            value:
            "  %p2 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 2");
        output.AppendLine(value: "  store i32 %ln, ptr %p2");
        output.AppendLine(
            value:
            "  %p3 = getelementptr inbounds { ptr, ptr, i32, i32 }, ptr %slot, i32 0, i32 3");
        output.AppendLine(value: "  store i32 %col, ptr %p3");
        output.AppendLine(value: "  br label %skip");
        output.AppendLine(value: "skip:");
        output.AppendLine(value: RetVoidInstruction);
        output.AppendLine(value: "}");
        output.AppendLine();
        // printer helper — passes exe TLS data to the DLL
        output.AppendLine(value: "declare void @rf_print_shadow_stack_data(ptr, i32)");
        output.AppendLine(value: "define private void @_rf_print_trace_stack() {");
        output.AppendLine(value: EntryLabel);
        output.AppendLine(value: "  %depth = load i32, ptr @_rf_trace_depth");
        output.AppendLine(
            value:
            "  call void @rf_print_shadow_stack_data(ptr @_rf_trace_stack, i32 %depth)");
        output.AppendLine(value: RetVoidInstruction);
        output.AppendLine(value: "}");
        output.AppendLine();
    }

    /// <summary>
    /// Appends the <c>main()</c> entry point that inits the runtime, sets the trace mode, and calls
    /// the entry module's <c>start()</c>. No-op when no start symbol is found.
    /// </summary>
    private void AppendMainEntryPoint(StringBuilder output)
    {
        // Base mode emits the resident stdlib base — no @main (the delta module supplies the entry point).
        string? startFunc = ResolveEntryStartSymbol();
        if (_baseMode || startFunc == null)
        {
            return;
        }

        // Trace mode: 2=shadow (debug+release), 0=none (release-time/space).
        int traceMode = _buildMode switch
        {
            RfBuildMode.Debug or RfBuildMode.Release => 2,
            _ => 0
        };

        output.AppendLine(value: "declare void @__rf_set_trace_mode(i32)");
        if (ShouldEmitTrace)
        {
            output.AppendLine(value: "declare void @rf_set_stack_printer(ptr)");
        }
        output.AppendLine();
        output.AppendLine(value: "; Entry point");
        output.AppendLine(value: "define i32 @main(i32 %argc, ptr %argv) {");
        output.AppendLine(value: EntryLabel);
        output.AppendLine(value: "  call void @rf_runtime_init()");
        output.AppendLine(handler: $"  call void @__rf_set_trace_mode(i32 {traceMode})");
        if (ShouldEmitTrace)
        {
            output.AppendLine(
                value: "  call void @rf_set_stack_printer(ptr @_rf_print_trace_stack)");
        }
        output.AppendLine(handler: $"  call void @{startFunc}()");
        output.AppendLine(value: "  ret i32 0");
        output.AppendLine(value: "}");
    }

    /// <summary>
    /// Resolves the program-entry <c>start</c> symbol: the entry module's own start (matched by the
    /// module-qualified suffix regardless of attribute prefix), falling back to a lone start symbol
    /// only when no entry module is set. `_generatedRoutineDefs` is unordered, so selecting by name
    /// alone would non-deterministically pick the wrong module's start when several define one.
    /// </summary>
    private string? ResolveEntryStartSymbol()
    {
        static bool IsStartSymbol(string f) =>
            f.EndsWith(value: ".start()\"") || f.EndsWith(value: " start()\"");

        string? startFunc = null;
        if (!string.IsNullOrEmpty(value: EntryModule))
        {
            startFunc = _generatedRoutineDefs.FirstOrDefault(predicate: f =>
                f.EndsWith(value: $"{EntryModule}.start()\""));
        }
        return startFunc ?? _generatedRoutineDefs.SingleOrDefault(predicate: IsStartSymbol);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Attribute/linkage words that may precede the return type of a <c>declare</c>/<c>define</c>
    /// header (e.g. <c>define private noalias ptr @f</c>). Stripped when isolating the bare type.
    /// </summary>
    private static readonly HashSet<string> ReturnAttributeWords =
    [
        "private", "internal", "external", "linkonce", "linkonce_odr", "weak", "weak_odr",
        "noalias", "zeroext", "signext", "inreg", "noundef", "nonnull"
    ];

    /// <summary>
    /// Reduces a <c>declare …</c> or <c>define … {</c> header to a canonical type-only signature
    /// such as <c>i64(i32,ptr)</c> — return type plus the ordered parameter types, with parameter
    /// names and all attributes (sret/byval/align/…) stripped. Two headers for the same symbol that
    /// describe the same LLVM function type normalize to the same string, so an inequality is a real
    /// signature divergence. Used only by the declare/define consistency assertion at output assembly.
    /// </summary>
    private static string NormalizeFunctionSignature(string header)
    {
        int at = header.IndexOf(value: '@');
        int open = at < 0 ? -1 : header.IndexOf(value: '(', startIndex: at);
        if (at < 0 || open < 0)
        {
            return header.Trim();
        }

        int close = FindMatchingParen(text: header, open: open);
        if (close < 0)
        {
            return header.Trim();
        }

        // Return segment = everything between the leading keyword (declare/define) and '@'.
        string head = header[..at].Trim();
        int firstSpace = head.IndexOf(value: ' ');
        string returnSegment = firstSpace < 0 ? "" : head[(firstSpace + 1)..].Trim();
        string returnType = NormalizeTypeToken(token: returnSegment);

        // Parameter types: split on top-level commas, normalize each to its bare type.
        List<string> paramTypes = SplitAndNormalizeParams(paramSegment: header[(open + 1)..close]);

        return $"{returnType}({string.Join(separator: ",", values: paramTypes)})";
    }

    /// <summary>
    /// Returns the index of the paren that matches the <c>(</c> at <paramref name="open"/>, or
    /// <c>-1</c> if unbalanced. Depth-aware so nested parens (e.g. <c>sret(...)</c>) don't confuse it.
    /// </summary>
    private static int FindMatchingParen(string text, int open)
    {
        int depth = 0;
        for (int i = open; i < text.Length; i++)
        {
            char c = text[index: i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Splits a parameter-list body on top-level commas and normalizes each entry to its bare type.
    /// </summary>
    private static List<string> SplitAndNormalizeParams(string paramSegment)
    {
        var paramTypes = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i <= paramSegment.Length; i++)
        {
            bool atBoundary = i == paramSegment.Length ||
                (paramSegment[index: i] == ',' && depth == 0);
            if (atBoundary)
            {
                string raw = paramSegment[start..i].Trim();
                if (raw.Length > 0)
                {
                    paramTypes.Add(item: NormalizeTypeToken(token: raw));
                }

                start = i + 1;
            }
            else if (paramSegment[index: i] == '(')
            {
                depth++;
            }
            else if (paramSegment[index: i] == ')')
            {
                depth--;
            }
        }

        return paramTypes;
    }

    /// <summary>
    /// Extracts the leading LLVM type from a parameter or return token, discarding any leading
    /// return attributes, any trailing parameter attributes, and the <c>%name</c>. Handles struct
    /// (<c>{…}</c>), array (<c>[…]</c>), and quoted named (<c>%"…"</c>) types whose spelling contains
    /// spaces or commas.
    /// </summary>
    private static string NormalizeTypeToken(string token)
    {
        token = StripLeadingReturnAttributes(token: token.Trim());
        if (token.Length == 0)
        {
            return "";
        }

        char first = token[index: 0];
        if (first is '{' or '[')
        {
            return ReadBalancedType(token: token, openChar: first);
        }

        if (token.StartsWith(value: "%\"", comparisonType: StringComparison.Ordinal))
        {
            int endQuote = token.IndexOf(value: '"', startIndex: 2);
            return endQuote < 0 ? token : token[..(endQuote + 1)];
        }

        // Simple type: up to the first whitespace or '(' (an attribute like sret(...) following ptr).
        int stop = token.Length;
        for (int i = 0; i < token.Length; i++)
        {
            if (token[index: i] is ' ' or '(')
            {
                stop = i;
                break;
            }
        }

        return token[..stop];
    }

    /// <summary>
    /// Drops leading attribute/linkage words (these precede a return type). Parameter attributes
    /// follow the type, so they are handled by reading only the leading type in the caller.
    /// </summary>
    private static string StripLeadingReturnAttributes(string token)
    {
        while (token.Length > 0)
        {
            int sp = token.IndexOf(value: ' ');
            string firstWord = sp < 0 ? token : token[..sp];
            if (!ReturnAttributeWords.Contains(item: firstWord))
            {
                break;
            }

            token = sp < 0 ? "" : token[(sp + 1)..].TrimStart();
        }

        return token;
    }

    /// <summary>
    /// Reads the leading balanced struct (<c>{…}</c>) or array (<c>[…]</c>) type from
    /// <paramref name="token"/>. Returns the whole token if the brackets are unbalanced.
    /// </summary>
    private static string ReadBalancedType(string token, char openChar)
    {
        char closeChar = openChar == '{' ? '}' : ']';
        int depth = 0;
        for (int i = 0; i < token.Length; i++)
        {
            if (token[index: i] == openChar)
            {
                depth++;
            }
            else if (token[index: i] == closeChar && --depth == 0)
            {
                return token[..(i + 1)];
            }
        }

        return token;
    }

    /// <summary>
    /// Gets the next unique temporary variable name.
    /// </summary>
    /// <returns>A unique temporary name like %tmp0, %tmp1, etc.</returns>
    private string NextTemp()
    {
        return $"%tmp{_tempCounter++}";
    }

    /// <summary>
    /// Gets the next unique label name.
    /// </summary>
    /// <param name="prefix">Optional prefix for the label.</param>
    /// <returns>A unique label name.</returns>
    private string NextLabel(string prefix = "label")
    {
        return $"{prefix}{_labelCounter++}";
    }

    /// <summary>
    /// Emits a line to a StringBuilder.
    /// </summary>
    private static void EmitLine(StringBuilder sb, string line)
    {
        sb.AppendLine(value: line);
    }

    /// <summary>
    /// Emits a function-local stack allocation into the current function's entry block.
    /// This avoids repeated stack growth when the source declaration appears inside loops.
    /// </summary>
    private void EmitEntryAlloca(string llvmName, string llvmType, int? align = null)
    {
        if (!_emittedAllocaNames.Add(item: llvmName))
        {
            return; // Already emitted for this function — pattern variables shared across when arms
        }

        // @layout("align=N") on a record raises its stack slot's alignment so a pointer handed to C
        // matches the C-side over-aligned struct.
        string alignSuffix = align is { } a ? $", align {a}" : "";
        EmitLine(sb: _currentRoutineEntryAllocas, line: $"  {llvmName} = alloca {llvmType}{alignSuffix}");
    }

    /// <summary>The forced stack-slot alignment for a value of <paramref name="type"/>, from a record's
    /// <c>@layout("align=N")</c>; null when the type has no forced alignment.</summary>
    private static int? ForcedAllocaAlignment(TypeInfo type) =>
        type is RecordTypeInfo { ForcedAlignment: { } n } ? n : null;

    /// <summary>
    /// Emits a null-terminated C string as an LLVM global constant.
    /// Returns the global name (e.g., "@.cstr.0") which can be used as a ptr.
    /// </summary>
    private string EmitCStringConstant(string value)
    {
        if (_cstrConstants.TryGetValue(key: value, value: out string? cached))
            return cached;

        string name = $"@.cstr.{_cstrCounter++}";
        byte[] utf8 = Encoding.UTF8.GetBytes(s: value + "\0");
        var sb = new StringBuilder();
        foreach (byte b in utf8)
        {
            if (b is >= 0x20 and < 0x7F && b != (byte)'\\' && b != (byte)'"')
            {
                sb.Append(value: (char)b);
            }
            else
            {
                sb.Append(handler: $"\\{b:X2}");
            }
        }

        EmitLine(sb: _globalDeclarations,
            line: $"{name} = private unnamed_addr constant [{utf8.Length} x i8] c\"{sb}\"");
        _cstrConstants[value] = name;
        return name;
    }

    #endregion
}
