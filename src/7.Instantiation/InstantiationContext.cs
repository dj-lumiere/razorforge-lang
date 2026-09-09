using Compiler.Declaration;
using Compiler.Targeting;
using Microsoft.Win32;
using SyntaxTree;
using TypeInfo = TypeModel.Types.TypeInfo;

namespace Compiler.Instantiation;

/// <summary>
/// Invariant result of <c>RoutineReachabilityPass</c>'s per-body AST walk, keyed by
/// <see cref="RoutineDeclaration"/> reference. The set of call-like nodes and the var-decl type map
/// a body produces are a pure function of its AST (both are collected WITHOUT the frame's generic-type
/// substitutions — see <c>CollectCallsAndLocalVarTypes</c>), so they can be computed once and reused.
/// Because a warm-restored stdlib AST reuses the same decl objects across compiles, caching this in the
/// daemon-lifetime warm state lets each warm run skip re-walking the ~1641 reachable stdlib bodies (the
/// dominant reachability cost); per-frame resolution/substitution still runs, so liveness is unchanged.
/// </summary>
public sealed record RoutineBodyScan(
    List<object> Calls,
    Dictionary<string, TypeInfo> VarDeclTypes);

/// <summary>
/// Guarded context for Phase 7 generic instantiation work.
/// This is scaffold-only for now; existing monomorphization still lives elsewhere.
/// </summary>
public sealed class InstantiationContext
{
    /// <summary>
    /// Semantic registry used to resolve generic definitions and concrete instantiations.
    /// </summary>
    public TypeRegistry Registry { get; }

    /// <summary>
    /// User programs that seed reachable generic type and routine discovery.
    /// </summary>
    public List<(Program Program, string FilePath, string Module)> UserPrograms { get; }

    /// <summary>
    /// Verified routine bodies keyed by registry key, used as source bodies for instantiation.
    /// </summary>
    public IReadOnlyDictionary<string, Statement> RoutineBodies { get; }

    /// <summary>
    /// WARM-ONLY lookup source for stdlib routine template bodies (keyed by registry key). On a warm
    /// compile <see cref="RoutineBodies"/> holds ONLY the user program's bodies — the restore path
    /// deliberately keeps the stdlib out of the variant-generation working set. But protocol
    /// default-impl lowering still needs the stdlib EXTENSION template body (e.g.
    /// <c>Iterable[Text].join</c>) to recognize + clone it per implementer, so this dictionary makes
    /// those templates available for CONTAINMENT/LOOKUP only — it is NOT iterated as a live body set
    /// (that would re-walk the whole stdlib every warm run). Empty on a cold compile, where
    /// <see cref="RoutineBodies"/> already contains every stdlib body.
    /// </summary>
    public IReadOnlyDictionary<string, Statement> StdlibTemplateBodies { get; }

    /// <summary>
    /// Synthesized error-handling variant bodies that may contain reachable generic calls.
    /// </summary>
    public Dictionary<string, Statement> VariantBodies { get; }

    /// <summary>
    /// Concrete generic bodies produced by instantiation and later consumed by codegen.
    /// </summary>
    public Dictionary<string, MonomorphizedBody> InstantiatedGenericBodies { get; }

    /// <summary>
    /// Target platform used when generic expansion depends on platform constants.
    /// </summary>
    public TargetConfig Target { get; }

    /// <summary>
    /// Build mode used when generic expansion depends on compile-time configuration.
    /// </summary>
    public RfBuildMode BuildMode { get; }

    /// <summary>
    /// Canonical keys for reachable concrete generic types discovered during collection.
    /// </summary>
    public HashSet<string> ReachableGenericTypes { get; } = [];

    /// <summary>
    /// Canonical keys for reachable concrete generic routines discovered during collection.
    /// </summary>
    public HashSet<string> ReachableGenericRoutines { get; } = [];

    /// <summary>
    /// Strategy-B live routine set: <see cref="RegistryKey"/> values reachable from
    /// program entry points (<c>start()</c>, <c>@test</c>, <c>@bench</c>) via a transitive
    /// call-graph BFS. When non-empty, GMP gates body emission on membership so unreachable
    /// memberRoutines on live concrete types (e.g. <c>List[Text].insertion_sort</c> when no caller
    /// uses it) are skipped, preventing the stdlib closure cascade from forcing emission of
    /// unused routines. Populated by <c>RoutineReachabilityPass</c>; empty disables filtering.
    /// </summary>
    public HashSet<string> LiveRoutineKeys { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Strategy-B live owner-type set: <c>TypeInfo.FullName</c> values for concrete owner
    /// types whose routines were reached by the entry-point BFS. GMP gates
    /// <c>ProcessConcreteType</c> on membership so unreachable concrete instances
    /// (e.g. <c>Array[BuildMode, 63]</c>, <c>BTreeListNode[Text]</c>) don't get monomorphized at all.
    /// Populated by <c>RoutineReachabilityPass</c>; empty disables filtering.
    /// </summary>
    public HashSet<string> LiveOwnerTypeNames { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>
    /// Call graph used by the v0.2.0 may-suspend effect analysis. Populated additively by
    /// <c>RoutineReachabilityPass</c> as it resolves callees (caller→callee edges, plus the
    /// <see cref="Compiler.Verification.CallGraphNode.DirectlySuspends"/> seed when a callee is a suspend
    /// primitive). Consumed by <see cref="Compiler.Verification.MaySuspendAnalysis"/> after that pass.
    /// </summary>
    public Verification.CallGraph MaySuspendGraph { get; } = new();

    /// <summary>
    /// Result of the may-suspend fixpoint: registry keys of routines that can transitively reach a
    /// coroutine suspend point, and therefore need cancellation-frame instrumentation in Phase 4.
    /// Empty for any program that never reaches a suspend primitive (i.e. all current code).
    /// </summary>
    public HashSet<string> MaySuspendRoutineKeys { get; } = new(comparer: StringComparer.Ordinal);

    /// <summary>When true, passes print per-iteration diagnostics to stderr.</summary>
    public bool SaTiming { get; set; }

    /// <summary>
    /// Stage-2 (pull/(B)) demand-resolution hook: given a reached routine's <see cref="RegistryKey"/>,
    /// ensure its body has been semantically analyzed (calls/types resolved → <c>ResolvedRoutine</c>/
    /// <c>ResolvedType</c> set) so the collector can follow it. Set by <c>SemanticVerifier</c> to its
    /// on-demand analyzer; the collector calls it per reached routine BEFORE walking the body — this is
    /// how "resolve-as-you-collect from start" bootstraps (start's body is analyzed, its resolved calls
    /// name callees, each callee is analyzed on reach, and so on). Null ⇒ bodies were analyzed eagerly
    /// (the pre-(B) pipeline), so the collector just reads the already-set resolutions.
    /// </summary>
    public Func<string, bool>? AnalyzeRoutineOnDemand { get; set; }

    /// <summary>
    /// When true, root EVERY concrete stdlib routine in reachability so monomorphization materializes the
    /// FULL stdlib generic closure, not just what the entry program reaches. Used when emitting a precompiled
    /// stdlib "base" that must define everything it references (a user's own instantiations are compiled
    /// separately, on demand). Normal per-run / release builds leave this false → entry-point-driven liveness.
    /// </summary>
    public bool SeedAllStdlibRoutines { get; init; }

    /// <summary>
    /// Daemon-lifetime cache of per-body reachability scans (see <see cref="RoutineBodyScan"/>), keyed
    /// by stdlib <see cref="RoutineDeclaration"/> reference. Null on a plain compile with no warm state;
    /// when non-null, <c>RoutineReachabilityPass</c> reuses a cached scan instead of re-walking the body
    /// and stores newly-walked stdlib bodies for the next warm run. Only stdlib decls are cached (user
    /// decls change every edit and are cheap to walk), so it is bounded and never returns stale results.
    /// </summary>
    public Dictionary<RoutineDeclaration, RoutineBodyScan>? BodyScanCache { get; }

    /// <summary>
    /// Initializes shared state for Phase 7 generic reachability and monomorphization.
    /// </summary>
    /// <param name="registry">The semantic type registry for the current compilation.</param>
    /// <param name="userPrograms">User program triples that seed generic discovery.</param>
    /// <param name="routineBodies">Verified routine bodies keyed by registry key.</param>
    /// <param name="options">Optional tuning values; defaults apply when null.</param>
    public InstantiationContext(TypeRegistry registry,
        List<(Program Program, string FilePath, string Module)> userPrograms,
        IReadOnlyDictionary<string, Statement> routineBodies, InstantiationOptions? options = null)
    {
        Registry = registry;
        UserPrograms = userPrograms;
        RoutineBodies = routineBodies;
        StdlibTemplateBodies =
            options?.StdlibTemplateBodies ?? new Dictionary<string, Statement>();
        VariantBodies = options?.VariantBodies ?? [];
        InstantiatedGenericBodies = options?.InstantiatedGenericBodies ?? [];
        Target = options?.Target ?? TargetConfig.ForCurrentHost();
        BuildMode = options?.BuildMode ?? RfBuildMode.Debug;
        BodyScanCache = options?.BodyScanCache;
    }
}

/// <summary>
/// Optional configuration bundle for <see cref="InstantiationContext"/>. Groups the six optional
/// construction-time inputs so the constructor stays under the parameter-count limit.
/// </summary>
public sealed class InstantiationOptions
{
    /// <summary>Synthesized error-handling variant bodies that may contain reachable generic calls.</summary>
    public Dictionary<string, Statement>? VariantBodies { get; init; }

    /// <summary>Concrete generic bodies produced by prior instantiation runs.</summary>
    public Dictionary<string, MonomorphizedBody>? InstantiatedGenericBodies { get; init; }

    /// <summary>Target platform; defaults to the host platform when null.</summary>
    public TargetConfig? Target { get; init; }

    /// <summary>Build mode used when generic expansion depends on compile-time configuration.</summary>
    public RfBuildMode BuildMode { get; init; } = RfBuildMode.Debug;

    /// <summary>Daemon-lifetime per-body reachability scan cache; null disables caching.</summary>
    public Dictionary<RoutineDeclaration, RoutineBodyScan>? BodyScanCache { get; init; }

    /// <summary>WARM-ONLY lookup source for stdlib routine template bodies; empty on a cold compile.</summary>
    public IReadOnlyDictionary<string, Statement>? StdlibTemplateBodies { get; init; }
}
