using System.Linq;
using System.Text;
using Compiler.Instantiation;
using Compiler.Declaration;
using Compiler.Targeting;
using SyntaxTree;
using TypeModel.Enums;
using TypeModel.Symbols;
using TypeModel.Types;

namespace Compiler.CodeGen;

/// <summary>
/// Optional configuration for <see cref="LlvmCodeGenerator"/>. Bundles the parameters that are
/// rarely all supplied at once so neither constructor overload exceeds seven parameters.
/// </summary>
public sealed class LlvmCodeGeneratorOptions
{
    /// <summary>Optional stdlib programs for intrinsic routine definitions.</summary>
    public List<(Program Program, string FilePath, string Module)>? StdlibPrograms { get; init; }

    /// <summary>Target platform configuration (defaults to current host when null).</summary>
    public TargetConfig? Target { get; init; }

    /// <summary>Build optimization mode (defaults to Debug).</summary>
    public RfBuildMode BuildMode { get; init; } = RfBuildMode.Debug;

    /// <summary>AST bodies for compiler-generated derived operators.</summary>
    public IReadOnlyDictionary<string, Statement>? SynthesizedBodies { get; init; }

    /// <summary>Instantiated generic bodies from GenericMonomorphizationPass.</summary>
    public IReadOnlyDictionary<string, MonomorphizedBody>? InstantiatedGenericBodies { get; init; }

    /// <summary>Reachable routine keys; empty disables reachability filtering.</summary>
    public IReadOnlyCollection<string>? LiveRoutineKeys { get; init; }

    /// <summary>Routine keys that may suspend (used for coroutine frame layout).</summary>
    public IReadOnlyCollection<string>? MaySuspendRoutineKeys { get; init; }

    /// <summary>Mangled symbols already defined in the resident base dylib; empty = full emission.</summary>
    public IReadOnlyCollection<string>? ResidentSymbols { get; init; }
}

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
    /// Reachable routine RegistryKeys produced by <c>RoutineReachabilityPass</c>.
    /// When non-empty, Phase A's stdlib body emission gates by this set in addition to
    /// <see cref="_generatedRoutines"/>, preventing the lazy-declaration cascade from
    /// emitting bodies for unreachable routines.
    /// </summary>
    private HashSet<string> _liveRoutineKeys = new(comparer: StringComparer.Ordinal);

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
    private bool IsResident(string mangledFuncName)
    {
        return _residentSymbols.Count > 0 && _residentSymbols.Contains(item: mangledFuncName);
    }

    /// <summary>
    /// Base-emission mode (resident-JIT incremental Phase 0a, see
    /// <c>internal-wiki/RESIDENT-JIT-INCREMENTAL-V0.5.md</c> §2A.5 / Phase 0a): this generator is
    /// emitting the NON-PRUNED precompiled base (stdlib), not a user program — so it must NOT emit an
    /// <c>@main</c> entry point (the delta module supplies it). Set only by <see cref="GenerateBase"/>.
    /// </summary>
    private bool _baseMode;

    /// <summary>Wrapper type base names for member forwarding in codegen.</summary>
    // These types will eventually all map to an opaque llvm ptr; until then codegen needs this list.
    private static readonly IReadOnlySet<string> WrapperTypeNames = RuntimeContract.WrapperTypes;

    /// <summary>The user program ASTs to generate code for (single-file or multi-file).</summary>
    private readonly List<(Program Program, string FilePath, string Module)> _userPrograms;

    /// <summary>The stdlib programs to include routine bodies from.</summary>
    private readonly List<(Program Program, string FilePath, string Module)> _stdlibPrograms;

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
    private readonly Dictionary<string, string> _cstrConstants =
        new(comparer: StringComparer.Ordinal);

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
    // Keyed by mangled name string; a future refactor could key by RoutineInfo instead.
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
    /// Returns a snapshot of the mangled LLVM symbol names this generator actually emitted a
    /// <c>define</c> for. This is the PRODUCER side of the resident/delta split (resident-JIT
    /// incremental): when a base module is codegen'd + JIT'd into the resident dylib, its emitted
    /// symbols become the <c>residentSymbols</c> set fed to the delta build's generator, which then
    /// emits those as extern <c>declare</c>s instead of re-defining them. Valid after
    /// <see cref="Generate"/>.
    /// </summary>
    public IReadOnlyCollection<string> GetEmittedRoutineSymbols()
    {
        return new HashSet<string>(collection: _generatedRoutineDefs,
            comparer: StringComparer.Ordinal);
    }

    /// <summary>The return type of the current function being generated.</summary>
    private TypeInfo? _currentRoutineReturnType;

    /// <summary>Diagnostic-only: owner-qualified name of the routine currently being emitted.</summary>
    private string? _currentRoutineDiagName;

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

    /// <summary>FreeRoutine-entry alloca instructions emitted once per function.</summary>
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
    /// <param name="options">Optional generation settings (stdlib, target, build mode, bodies, keys).</param>
    public LlvmCodeGenerator(Program program, TypeRegistry registry,
        LlvmCodeGeneratorOptions? options = null) : this(userPrograms:
        [
            (program, program.Location.FileName, program.Declarations
                                                        .OfType<ModuleDeclaration>()
                                                        .FirstOrDefault()
                                                       ?.Path ?? "")
        ],
        registry: registry,
        options: options)
    {
    }

    /// <summary>
    /// Creates a new LLVM code generator for multiple user programs (multi-file build).
    /// </summary>
    /// <param name="userPrograms">The user program ASTs with file paths and module names.</param>
    /// <param name="registry">The type registry from semantic analysis.</param>
    /// <param name="options">Optional generation settings (stdlib, target, build mode, bodies, keys).</param>
    public LlvmCodeGenerator(List<(Program Program, string FilePath, string Module)> userPrograms,
        TypeRegistry registry, LlvmCodeGeneratorOptions? options = null)
    {
        options ??= new LlvmCodeGeneratorOptions();
        _target = options.Target ?? TargetConfig.ForCurrentHost();
        if (_target.PointerBitWidth != 64)
        {
            throw new ArgumentException(
                message:
                $"Only 64-bit targets are currently supported (got {_target.PointerBitWidth}).",
                paramName: nameof(options));
        }

        _userPrograms = userPrograms;
        _registry = registry;
        _stdlibPrograms = options.StdlibPrograms ?? [];
        if (options.SynthesizedBodies != null)
        {
            _synthesizedBodies = options.SynthesizedBodies;
        }

        if (options.InstantiatedGenericBodies != null)
        {
            _instantiatedGenericBodies = options.InstantiatedGenericBodies;
        }

        if (options.LiveRoutineKeys is { Count: > 0 })
        {
            _liveRoutineKeys = new HashSet<string>(collection: options.LiveRoutineKeys,
                comparer: StringComparer.Ordinal);
        }

        if (options.ResidentSymbols is { Count: > 0 })
        {
            _residentSymbols = new HashSet<string>(collection: options.ResidentSymbols,
                comparer: StringComparer.Ordinal);
        }

        _buildMode = options.BuildMode;
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
    private static TypeInfo? GetGenericBase(TypeInfo type)
    {
        return GetGenericBaseStatic(type: type);
    }

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
    private static string? GetGenericBaseName(TypeInfo type)
    {
        return GetGenericBaseNameStatic(type: type);
    }

    /// <summary>Guarded helper for resolved-generic base-name lookups.</summary>
    internal static string? GetGenericBaseNameStatic(TypeInfo type)
    {
        return GetGenericBaseStatic(type: type)
          ?.Name;
    }

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
            if (!timing)
            {
                return;
            }

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
        return (ir, GetEmittedRoutineSymbols());
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
                    ContainsGenericParameter(type: t) || t is ErrorTypeInfo ||
                    ContainsAbstractProjection(type: t)) != true)
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
        {
            return !cc.TryFold(resolveTypeParam: _ => null, pointerSize: 8, result: out _);
        }

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

        // A generic DEFINITION type itself (e.g. `RoamController[T]`, `Amending[T, P]`) has no concrete
        // layout — its own type parameters are unbound, so its `TypeArguments` are the param placeholders
        // (or null). Treat it as non-concrete so an INSTANCE routine anchored on a generic-def owner (a
        // template like `RoamController[T].member_type_id`, wrongly demand-collected under its generic-def
        // key) is skipped, not emitted by-value — emitting its `me` param recurses into a generic-def field
        // (`Amending`) with no LLVM layout and detonates codegen. Concrete instances emit under their own key.
        if (type.IsGenericDefinition)
        {
            return true;
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
        if (r.ReturnType is TypeInfo rt && SignatureTypeIsUnresolved(t: rt))
        {
            return true;
        }

        foreach (ParameterInfo p in r.Parameters)
        {
            if (p.Type is TypeInfo pt && SignatureTypeIsUnresolved(t: pt))
            {
                return true;
            }
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
        if (ContainsGenericParameter(type: t))
        {
            return true;
        }

        if (t.Name.Contains(value: "__Vararg", comparisonType: StringComparison.Ordinal))
        {
            return true;
        }

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

                TypeInfo? type = _registry.LookupVariable(name: g.Name)
                                         ?.Type;
                if (type == null)
                {
                    continue; // SA registered every global; skip defensively if absent
                }

                string llvmType = GetValueLlvmType(type: type);
                string qualified = string.IsNullOrEmpty(value: module)
                    ? g.Name
                    : $"{module}.{g.Name}";
                string symbol = $"@\"global.{qualified}\"";

                EmitLine(sb: _globalDeclarations,
                    line: $"{symbol} = global {llvmType} zeroinitializer");
                _moduleGlobals[key: g.Name] = (type, symbol);
            }
        }
    }

    private void GenerateRoutineDeclarations()
    {
        // Build set of routine names that have bodies (in user programs or stdlib)
        HashSet<string> routinesWithBodies = CollectRoutinesWithBodies();

        foreach (RoutineInfo routine in _registry.GetAllRoutines()
                                                 .Where(predicate: r =>
                                                      !ShouldSkipRoutineDeclaration(routine: r,
                                                          routinesWithBodies: routinesWithBodies)))
        {
            // Only emit 'declare' for truly external routines
            GenerateRoutineDeclaration(routine: routine);
        }
    }

    /// <summary>Collects the names of routines that have bodies (user program + stdlib).</summary>
    private HashSet<string> CollectRoutinesWithBodies()
    {
        var routinesWithBodies = new HashSet<string>();
        foreach ((Program program, string _, string _) in _userPrograms.Concat(
                     second: _stdlibPrograms))
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

        // DUMB TRANSLATOR (big-bang): codegen makes NO liveness/set/resolution decision. The step before
        // codegen — the demand collector (RoutineCollectionPass) — materialized EVERY body to emit into
        // `_instantiatedGenericBodies` (reached stdlib + monomorphized + synthesized + wrapper-forwarder,
        // each pre-resolved). Codegen just translates that one set to IR: no Phase-A stdlib resolution,
        // no per-body liveness gate, no `_referencedKeys` fixpoint, no per-owner synthesis at emission.
        // The only checks below are correctness bookkeeping (don't define a symbol twice; a resident body
        // lives in the base dylib; an empty sentinel is not a real body) — not decisions about WHAT exists.
        foreach ((string _, MonomorphizedBody body) in _instantiatedGenericBodies)
        {
            string instFuncName = MangleRoutineName(routine: body.Info);
            if (_generatedRoutineDefs.Contains(item: instFuncName) ||
                IsResident(mangledFuncName: instFuncName))
            {
                continue;
            }

            // A body anchored on a generic-DEFINITION owner (a template like `RoamController[T].member_type_id`
            // or `Array[T, N].member_type_id` wrongly demand-collected under its generic-def key, not a
            // concrete instance) has no concrete `me` layout — emitting its param list recurses into an
            // unbound/generic-def field type with no LLVM layout and detonates codegen. `IsGenericDefinition`
            // is the precise "this is a template" signal (unlike ContainsGenericParameter, which early-returns
            // concrete for an `@llvm`-backed generic def like `Array[T, N]`). Skip it (correctness bookkeeping,
            // like the sentinel/resident skips above); concrete instances still emit under their own key. The
            // non-synthesized branch's GenerateRoutineDefinition applies the same guard via ShouldSkipRoutine.
            if (body.Info.OwnerType is { IsGenericDefinition: true })
            {
                continue;
            }

            if (body.IsSynthesized)
            {
                if (body.Ast.Body is BlockStatement { Statements.Count: 0 })
                {
                    continue; // sentinel, not a body
                }

                _generatedRoutineDefs.Add(item: instFuncName);
                _generatedRoutines.Add(item: instFuncName);
                EmitSynthesizedBodyFromAst(routine: body.Info,
                    funcName: instFuncName,
                    body: body.Ast.Body);
            }
            else
            {
                GenerateRoutineDefinition(routine: body.Ast, preResolvedInfo: body.Info);
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
            output.AppendLine(value: "; FreeRoutine declarations");
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

        // FreeRoutine definitions
        if (_functionDefinitions.Length > 0)
        {
            output.AppendLine(value: "; FreeRoutine definitions");
            output.Append(value: _functionDefinitions);
        }

        AppendMainEntryPoint(output: output);

        // Normalize to Unix line endings (clang/LLVM requires LF, not CRLF)
        string normalized = output.ToString()
                                  .Replace(oldValue: "\r\n", newValue: "\n")
                                  .Replace(oldValue: "\r", newValue: "\n");
        // TBAA first (tags loads/stores), then line-tables debug info (tags instructions + define
        // headers). Both are text post-passes that append their own metadata block; DI numbers itself
        // above TBAA's fixed !0..!22. ApplyDebugInfo is a no-op outside debug builds.
        return ApplyDebugInfo(ir: ApplyTbaa(ir: normalized));
    }

    /// <summary>
    /// Appends the type-declaration block (record -> variant -> entity -> crashable -> closure, each
    /// bucket already name-sorted), skipping empty buckets and the header when no types exist.
    /// </summary>
    private void AppendTypeDeclarations(StringBuilder output)
    {
        bool anyTypes = _typeDeclarationsRecord.Count > 0 || _typeDeclarationsVariant.Count > 0 ||
                        _typeDeclarationsEntity.Count > 0 ||
                        _typeDeclarationsCrashable.Count > 0 || _typeDeclarationsClosure.Count > 0;
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
            message: $"Codegen bug: declare/define signature mismatch for @{name}.\n" +
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
            output.AppendLine(
                value:
                "@_rf_trace_stack = external thread_local global [32 x { ptr, ptr, i32, i32 }]");
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
            value: "define private void @_rf_trace_update_loc(i32 %ln, i32 %col) alwaysinline {");
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
            value: "  call void @rf_print_shadow_stack_data(ptr @_rf_trace_stack, i32 %depth)");
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
        static bool IsStartSymbol(string f)
        {
            return f.EndsWith(value: ".start()\"") || f.EndsWith(value: " start()\"");
        }

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
        int open = at < 0
            ? -1
            : header.IndexOf(value: '(', startIndex: at);
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
        string head = header[..at]
           .Trim();
        int firstSpace = head.IndexOf(value: ' ');
        string returnSegment = firstSpace < 0
            ? ""
            : head[(firstSpace + 1)..]
               .Trim();
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
                              paramSegment[index: i] == ',' && depth == 0;
            if (atBoundary)
            {
                string raw = paramSegment[start..i]
                   .Trim();
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
            return endQuote < 0
                ? token
                : token[..(endQuote + 1)];
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
            string firstWord = sp < 0
                ? token
                : token[..sp];
            if (!ReturnAttributeWords.Contains(item: firstWord))
            {
                break;
            }

            token = sp < 0
                ? ""
                : token[(sp + 1)..]
                   .TrimStart();
        }

        return token;
    }

    /// <summary>
    /// Reads the leading balanced struct (<c>{…}</c>) or array (<c>[…]</c>) type from
    /// <paramref name="token"/>. Returns the whole token if the brackets are unbalanced.
    /// </summary>
    private static string ReadBalancedType(string token, char openChar)
    {
        char closeChar = openChar == '{'
            ? '}'
            : ']';
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
        string alignSuffix = align is { } a
            ? $", align {a}"
            : "";
        EmitLine(sb: _currentRoutineEntryAllocas,
            line: $"  {llvmName} = alloca {llvmType}{alignSuffix}");
    }

    /// <summary>The forced stack-slot alignment for a value of <paramref name="type"/>, from a record's
    /// <c>@layout("align=N")</c>; null when the type has no forced alignment.</summary>
    private static int? ForcedAllocaAlignment(TypeInfo type)
    {
        return type is RecordTypeInfo { ForcedAlignment: { } n }
            ? n
            : null;
    }

    /// <summary>
    /// Emits a null-terminated C string as an LLVM global constant.
    /// Returns the global name (e.g., "@.cstr.0") which can be used as a ptr.
    /// </summary>
    private string EmitCStringConstant(string value)
    {
        if (_cstrConstants.TryGetValue(key: value, value: out string? cached))
        {
            return cached;
        }

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
        _cstrConstants[key: value] = name;
        return name;
    }

    #endregion
}
