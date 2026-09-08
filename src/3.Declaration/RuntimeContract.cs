namespace Compiler.Declaration;

/// <summary>
/// Central inventory of the plain (non-<c>$</c>) stdlib routine names, stdlib type names, and native
/// runtime symbols that the compiler references by hard-coded string literal. This is the "gather in
/// one place" step of the compiler↔stdlib name-contract work
///
/// <para><b>Why this exists.</b> Renaming a stdlib routine (e.g. the <c>extract</c>/<c>inject</c> →
/// <c>peek</c>/<c>poke</c> rename, commit 1480acd) silently miscompiles: the compiler looks routines
/// up by literal (<c>LookupMemberRoutine(type, "peek")</c>) and sometimes changes teardown/codegen behavior
/// by matching a callee/type NAME. A rename compiles clean and breaks at runtime. Funnelling every
/// such literal through this one file makes the coupling visible and gives a single place to add the
/// <c>validate-stdlib</c> resolution check (Design 1 step 2, not yet wired).</para>
///
/// <para><b>Scope / non-goals (this step).</b> This file only <i>collects</i> the names as named
/// constants and grouped sets whose values are byte-identical to the current literals — no behavior
/// change. The call sites listed in each member's <c>&lt;remarks&gt;</c> still hold their own copies;
/// migrating them to reference these constants (and adding the resolution check) is the follow-up.
/// The <c>$</c>-wired routine names are intentionally NOT here — they already have a single source of
/// truth in <see cref="WiredRoutineCatalog"/>. The two marker-protocol verbs <c>refer</c>/<c>control</c>
/// and the iteration <c>try_emit</c> are compiler-generated and appear as literals at lowering sites,
/// so they are cross-referenced here for completeness (their catalog entry, where one exists, stays
/// canonical).</para>
/// </summary>
public static class RuntimeContract
{
    // =====================================================================================
    // B-TIER — plain stdlib routine names looked up / property-matched by literal.
    // These are the rename-sensitive contract: an author renaming any of these must update
    // the compiler, and (once step 2 lands) validate-stdlib will fail loudly if they don't.
    // =====================================================================================

    /// <summary>Raw-pointer / entity-escape surface on <c>Hijacked[T]</c> and bare entities.</summary>
    /// <remarks>Sites: WrapperForwardingPass (LookupMemberRoutine/MemberName), PatternLoweringPass,
    /// WiredRoutinePass, LLVMCodeGenerator.Expressions.Calls.</remarks>
    public static class RawPointer
    {
        /// <summary><c>Hijacked[T].peek()</c> — non-destructive read (<c>*ptr</c>).</summary>
        public const string Peek = "peek";
        /// <summary><c>Hijacked[T].poke(value:)</c> — store through the pointer.</summary>
        public const string Poke = "poke";
        /// <summary><c>Hijacked[T].as_entity()</c> — reinterpret the pointee as an owned entity (borrow view).</summary>
        public const string AsEntity = "as_entity";
        /// <summary>Null-pointer predicate on the raw-pointer surface.</summary>
        public const string IsNone = "is_none";
        /// <summary>Entity deallocation primitive.</summary>
        public const string Invalidate = "invalidate";
        /// <summary>Raw-pointer escape hatch that yields a <c>Hijacked[T]</c> (intercepted in codegen).</summary>
        public const string Hijack = "hijack";
    }

    /// <summary>Reference-counting controller surface (<c>RetainController[T]</c> and the RC wrappers).</summary>
    /// <remarks>The controllers' count primitives are the canonical vocabulary <c>hold</c>/<c>unhold</c>
    /// (strong ±1) and <c>observe</c>/<c>unobserve</c> (weak ±1) — plain stdlib memberRoutine names, called only
    /// from wrapper <c>.rf</c> bodies, so they need no constants here.</remarks>
    public static class RefCount
    {
        /// <summary><c>RetainController[T].raw_data()</c> — read the controlled payload.</summary>
        public const string RawData = "raw_data";
        /// <summary>The UNIFIED RC copy verb: a same-strength co-owner mint on any RC wrapper handle
        /// (Retained/Tracked/Guarded/Witnessed/Roamed) — strong→strong, weak→weak, biased→biased. This is the
        /// user-facing `.share()` and the verb codegen/lowering inserts implicitly for RC copy sites.</summary>
        public const string Share = "share";
    }

    /// <summary><c>Roamed[T]</c> memberRoutines that codegen inserts implicitly (no surface AST call),
    /// so RoutineReachabilityPass must anticipate them via the ImplicitCallContract.</summary>
    /// <remarks>Sites: LLVMCodeGenerator (promote at spawn boundary, lock_enter/lock_exit around
    /// direct field access, raw_inner for display-transparency projection) mirrored by
    /// ImplicitCallContract.ForLiveType — keep both bound to these constants, never bare literals.</remarks>
    public static class RoamedMemberRoutine
    {
        /// <summary>Spawn-boundary promotion of a single-thread handle to a shareable one.</summary>
        public const string Promote = "promote";
        /// <summary>Acquire the roam lock before a direct member-variable read.</summary>
        public const string LockEnter = "lock_enter";
        /// <summary>Release the roam lock after a direct member-variable read.</summary>
        public const string LockExit = "lock_exit";
        /// <summary>Project the Roamed handle to the bare inner entity pointer.</summary>
        public const string RawInner = "raw_inner";
    }

    /// <summary>The two ORTHOGONAL duplication verbs. <c>assign</c> (Assignable) = the implicit shallow
    /// bind/share verb that <see cref="Compiler.Desugaring.Passes.RecordCopyLoweringPass"/> INJECTS at
    /// every implicit-copy site (<c>b = a</c>, non-<c>steal</c> arg, return) — a bitwise dup for value
    /// records, a refcount++ share for managed leaves. <c>duplicate</c> (Copyable) = the explicit DEEP copy
    /// (<c>a.duplicate()</c>). For a refcounted (RC/BRC/ARC) type the two coincide (deep-clone collapses to
    /// share). Only <c>assign</c> is injected, so it alone is the identity-copy primitive the copy pass must
    /// not re-inject into (self-recursion guard).</summary>
    public static class Duplication
    {
        /// <summary>Implicit shallow bind/share (Assignable) — the verb injected at copy sites.</summary>
        public const string Assign = "assign";
        /// <summary>Explicit deep copy (Copyable).</summary>
        public const string Duplicate = "duplicate";
    }

    /// <summary>The auto-derived display routines emitted for every type.</summary>
    public static class Display
    {
        /// <summary>User-facing textual form (simple type name).</summary>
        public const string Represent = "represent";
        /// <summary>Diagnostic textual form (module-qualified, all fields).</summary>
        public const string Diagnose = "diagnose";
    }

    /// <summary>The universal derive that turns any value into a <c>SerialValue</c> — every value has one so
    /// a composite walk can call <c>me.field.serialize()</c> unconditionally.</summary>
    /// <remarks>Sites: AutoWiredRegistrationPass (registration), WiredRoutinePass (body synthesis),
    /// RoutineReachabilityPass (seed). Body is aggregate field-walk or scalar box.</remarks>
    public const string Serialize = "serialize";

    /// <summary>Carrier record field names on <c>Maybe[T]</c>/<c>Result[T]</c>.</summary>
    /// <remarks>Sites: ExpressionLoweringPass (tuple synthesis), PatternLoweringPass, ErrorHandlingVariantPass,
    /// LLVMCodeGenerator.Statements (field lookup).</remarks>
    public static class Carrier
    {
        /// <summary>Presence flag field (<c>true</c> = value present / not-absent).</summary>
        public const string PresentField = "present";
        /// <summary>Wrapped-value field.</summary>
        public const string ValueField = "value";
    }

    /// <summary>Collection-shape routines resolved by literal during lowering / reachability.</summary>
    /// <remarks>Sites: OperatorLoweringPass, ExpressionLoweringPass, RoutineReachabilityPass,
    /// LLVMCodeGenerator.Expressions.Collections. <see cref="AddLast"/> vs <see cref="Add"/> is chosen
    /// by base-name (<c>List</c>/<c>CircularList</c>/<c>BitList</c> → add_last, else add).</remarks>
    public static class Collection
    {
        /// <summary>Element count (see also the shipped <c>Sized.count()</c> protocol).</summary>
        public const string Count = "count";
        /// <summary>Unordered insert (Set/Dict).</summary>
        public const string Add = "add";
        /// <summary>Ordered append (List/CircularList/BitList).</summary>
        public const string AddLast = "add_last";
        /// <summary>Element replacement.</summary>
        public const string Replace = "replace";
    }

    /// <summary><c>back_resolve(count:, offset:)</c> — free routine resolving a `^n` back-index offset
    /// to a forward `U64` position against a container size. The `^` syntax is sugar with no runtime
    /// type; this routine is the lowering target.</summary>
    /// <remarks>Sites: OperatorLoweringPass, RoutineReachabilityPass. Failable (throws IndexOutOfBoundsError).</remarks>
    public const string BackResolve = "back_resolve";

    /// <summary><c>data_size()</c> — per-type byte size (compile-time BuilderQuery intrinsic, folded not called).</summary>
    /// <remarks>Sites: BuilderInfoProvider, BuilderQueryInliningPass, GenericAstRewriter.</remarks>
    public const string DataSize = "data_size";

    /// <summary><c>crash_message()</c> on error types — extracts the diagnostic string on the throw path.</summary>
    /// <remarks>Sites: LLVMCodeGenerator.Statements.Returns, WiredRoutinePass, RoutineReachabilityPass.</remarks>
    public const string CrashMessage = "crash_message";

    /// <summary><c>crash_title()</c> on error types — the second Crashable protocol member.</summary>
    /// <remarks>Sites: CrashableExpansionPass (dispatchable-member set), ImplicitCallContract (reachability seed).</remarks>
    public const string CrashTitle = "crash_title";

    /// <summary>Non-failable iterator step generated for <c>for</c>-lowering (the <c>try_</c> variant of
    /// failable <c>emit!</c>). Also carried by <see cref="WiredRoutineCatalog"/> (reachability seed);
    /// listed here because ControlFlowLoweringPass / IteratorInlineLoweringPass match it by literal.</summary>
    public const string TryEmit = "try_emit";

    // =====================================================================================
    // Marker-protocol verbs — compiler-generated $-names that are NOT in WiredRoutineCatalog
    // but are matched by literal at teardown/lowering sites (grouped with the view-verb sets).
    // =====================================================================================

    /// <summary><c>access</c> — the <c>Accessing</c> marker-protocol coercion (yields a read-only view).</summary>
    public const string Access = "access";
    /// <summary><c>control</c> — the <c>Controlling</c> marker-protocol coercion (yields a borrow view).</summary>
    public const string Control = "control";

    /// <summary>The routine-name contracts that MUST resolve to a real, declared stdlib routine —
    /// the rename-sensitive set that <c>validate-stdlib</c>'s <see cref="RuntimeContractCheck"/>
    /// asserts. Deliberately EXCLUDES compiler-generated / intrinsic names that have no stdlib
    /// routine body: <see cref="TryEmit"/> (generated from <c>emit</c>), the marker verbs
    /// <see cref="Access"/>/<see cref="Control"/>, <see cref="DataSize"/> + the BuilderQuery sets
    /// (folded intrinsics), and the native <see cref="Runtime"/> externs (link-checked C-ABI). The
    /// carrier FIELDS (<see cref="Carrier"/>) are member variables, not routines — checked separately.</summary>
    public static readonly IReadOnlyList<string> StdlibRoutineContracts =
    [
        RawPointer.Peek, RawPointer.Poke, RawPointer.AsEntity, RawPointer.IsNone,
        RawPointer.Invalidate, RawPointer.Hijack,
        RefCount.RawData,
        Collection.Count, Collection.Add, Collection.AddLast, Collection.Replace,
        BackResolve, CrashMessage,
    ];

    /// <summary>Additional wrapper / marker-protocol TYPE-name contracts that must each resolve to a
    /// registered type (checked alongside <see cref="WrapperTypes"/> by <see cref="RuntimeContractCheck"/>).
    /// <see cref="Owned"/> is intentionally excluded — it is a compiler-internal wrapper name with no
    /// declared stdlib type, so it cannot be resolution-checked.</summary>
    public static readonly IReadOnlyList<string> StdlibTypeContracts =
    [
        Atomic, Controlling, Accessing,
    ];

    // =====================================================================================
    // C-TIER — name→behavior heuristics. Teardown/codegen change behavior by matching these
    // name SETS. These stay name-based for now; the deep fix derives them from the signature
    // (Design 2B). Kept here so the sets have one definition to point every copy at.
    // =====================================================================================

    /// <summary>Store primitives: a call to one of these MOVES its argument into storage, so the
    /// source binding is not torn down at scope exit.</summary>
    /// <remarks>Sites: ScopeTeardownLoweringPass.StorePrimitives.</remarks>
    public static readonly IReadOnlySet<string> StorePrimitives =
        new HashSet<string>(comparer: StringComparer.Ordinal)
            { RawPointer.Poke, "store_element_ref", "store" };

    /// <summary>Reference primitives whose result BORROWS a referent owned elsewhere — a binding or
    /// temporary initialized by one owns nothing and must not be torn down.</summary>
    /// <remarks>Sites: ScopeTeardownLoweringPass.ViewVerbs, TemporaryTeardownPass.ViewVerbs.</remarks>
    public static readonly IReadOnlySet<string> ViewVerbs =
        new HashSet<string>(comparer: StringComparer.Ordinal)
            { RawPointer.AsEntity, Access, Control };

    // =====================================================================================
    // Wrapper TYPE names — genuine type-identity checks (legitimate to keep as checks, but the
    // string should be a symbol reference eventually — Design 2B). One definition per set here.
    // =====================================================================================

    /// <summary>Read-only single-threaded borrow token.</summary>
    public const string Viewing = "Viewing";
    /// <summary>Exclusive-write single-threaded borrow token.</summary>
    public const string Modifying = "Modifying";
    /// <summary>Read-only multi-threaded borrow token.</summary>
    public const string Consulting = "Consulting";
    /// <summary>Exclusive-write multi-threaded borrow token.</summary>
    public const string Amending = "Amending";
    /// <summary>Reference-counted single-threaded handle.</summary>
    public const string Retained = "Retained";
    /// <summary>Weak-reference single-threaded handle.</summary>
    public const string Tracked = "Tracked";
    /// <summary>Reference-counted multi-threaded handle.</summary>
    public const string Guarded = "Guarded";
    /// <summary>Weak-reference multi-threaded handle.</summary>
    public const string Witnessed = "Witnessed";
    /// <summary>Unmanaged raw-pointer handle.</summary>
    public const string Hijacked = "Hijacked";
    /// <summary>Biased-reference-counted, auto-promoting handle (Suflae `entity` backing). Registered
    /// as an RC wrapper for lifetime (retain/release), but deliberately NOT in the forwarding /
    /// read-only / coercion sets: access is compiler-inserted lock-wrapping, never <c>refer</c>/
    /// <c>control</c> (which would hand out a lock-bypassing raw reference).</summary>
    public const string Roamed = "Roamed";

    // Related wrapper / marker-protocol type names that appear in the same type-identity checks as
    // the nine borrow wrappers above, but are NOT part of the borrow-wrapper contract sets.
    /// <summary>Owning value wrapper (compiler-internal; not a declared stdlib type).</summary>
    public const string Owned = "Owned";
    /// <summary>Atomic value wrapper.</summary>
    public const string Atomic = "Atomic";
    /// <summary>Marker protocol whose coercion mints a controlling borrow (<see cref="Control"/>).</summary>
    public const string Controlling = "Controlling";
    /// <summary>Marker protocol whose coercion mints a referring borrow (<see cref="Access"/>).</summary>
    public const string Accessing = "Accessing";

    /// <summary>
    /// The single classifier for the borrow marker protocols: true when <paramref name="baseName"/> is
    /// the bare name of <see cref="Accessing"/> or <see cref="Controlling"/>. Use this everywhere instead
    /// of open-coding <c>x is Accessing or Controlling</c>.
    /// </summary>
    public static bool IsMarkerProtocol(string? baseName) =>
        baseName is Accessing or Controlling;

    /// <summary>The coercion verb a marker protocol mints: <see cref="Control"/> for
    /// <see cref="Controlling"/> (read/write), else <see cref="Access"/> (read-only).</summary>
    public static string MarkerCoercionVerb(string? baseName) =>
        baseName == Controlling ? Control : Access;

    /// <summary>All wrapper types recognized for layout/dispatch. Mirrors WrapperForwardingPass.WrapperTypes
    /// and LLVMCodeGenerator.WrapperTypeNames.</summary>
    public static readonly IReadOnlySet<string> WrapperTypes =
        new HashSet<string>(comparer: StringComparer.Ordinal)
            { Viewing, Modifying, Consulting, Amending, Guarded, Witnessed, Retained, Tracked, Hijacked, Roamed };

    /// <summary>Wrapper types that transparently forward inner-type memberRoutines — every wrapper EXCEPT
    /// <see cref="Hijacked"/> (the raw-pointer escape hatch). Mirrors WrapperForwardingPass.ForwardingWrapperTypes.</summary>
    public static readonly IReadOnlySet<string> ForwardingWrapperTypes =
        new HashSet<string>(comparer: StringComparer.Ordinal)
            { Viewing, Modifying, Consulting, Amending, Guarded, Witnessed, Retained, Tracked, Roamed };

    /// <summary>Read-only borrow tokens (only <c>@readonly</c> memberRoutines reachable). Mirrors
    /// WrapperForwardingPass.ReadOnlyWrapperTypes.</summary>
    public static readonly IReadOnlySet<string> ReadOnlyWrapperTypes =
        new HashSet<string>(comparer: StringComparer.Ordinal) { Viewing, Consulting };

    /// <summary>Borrow/view wrappers whose value points INTO another value, so a memberRoutine returning one
    /// may ALIAS its receiver. Mirrors TemporaryTeardownPass.ReferringWrapperNAmes.</summary>
    public static readonly IReadOnlySet<string> ReferringWrapperNAmes =
        new HashSet<string>(comparer: StringComparer.Ordinal)
            { Viewing, Modifying, Consulting, Amending, Retained };

    /// <summary>RC-wrapper base names whose refcount release is owned by codegen. Mirrors
    /// TemporaryTeardownPass.RcWrapperBaseNames.</summary>
    public static readonly IReadOnlySet<string> RcWrapperBaseNames =
        new HashSet<string>(comparer: StringComparer.Ordinal)
            { Retained, Tracked, Guarded, Witnessed, Roamed };

    // =====================================================================================
    // BuilderQuery intrinsic names — reflection-style routines folded at compile time
    // (Axis-2 intrinsics: no linkable body, no user-import dependency). Mirrors
    // BuilderInfoProvider.PerTypeRoutines / .StandaloneRoutines.
    // =====================================================================================

    /// <summary>Per-type BuilderQuery member routines (require <c>import BuilderQuery</c>).</summary>
    public static readonly IReadOnlySet<string> BuilderPerTypeRoutines =
        new HashSet<string>(comparer: StringComparer.Ordinal)
        {
            "type_name", "type_kind", "type_id", "module_name", "is_generic", "is_in_flight",
            "generic_args", "member_variable_count", "member_variable_info", "protocols",
            "protocol_info", "routine_names", "routine_info", "annotations", DataSize,
            "full_type_name", "dependencies", "member_type_id",
        };

    /// <summary>Standalone BuilderQuery routines (require <c>import BuilderQuery</c>).</summary>
    public static readonly IReadOnlySet<string> BuilderStandaloneRoutines =
        new HashSet<string>(comparer: StringComparer.Ordinal)
        {
            "source_file", "source_line", "source_column", "source_routine", "source_module",
            "source_text", "caller_file", "caller_line", "caller_routine", "target_os",
            "target_arch", "builder_version", "build_mode", "build_timestamp", "page_size",
            "cache_line", "word_size",
        };

    // =====================================================================================
    // Native runtime externs — C-ABI symbols emitted directly into the module by codegen.
    // These are matched against the native runtime library (native/runtime/*.c), NOT stdlib
    // .rf, so they are a DIFFERENT contract (a rename here means editing the C side too, and
    // validate-stdlib cannot check them). Collected here so codegen has one name table.
    // Sites: LLVMCodeGenerator.Expressions (declarations + call sites).
    // =====================================================================================

    /// <summary>Native runtime function symbols referenced by codegen. Names must match
    /// <c>native/runtime/razorforge_runtime.h</c>.
    ///
    /// <para>INVENTORY-ONLY: unlike the stdlib names above, codegen still emits these inline in its
    /// IR-template strings (<c>declare .. @rf_allocate_dynamic ..</c> / <c>call .. @rf_...</c>). A
    /// rename here breaks LOUDLY at link (undefined symbol), so it needs no silent-break guard and is
    /// deliberately not funnelled through these consts — they exist to document the one name table.</para></summary>
    public static class Runtime
    {
        /// <summary>Zero-initialized heap allocation.</summary>
        public const string AllocateDynamic = "rf_allocate_dynamic";
        /// <summary>Uninitialized heap allocation.</summary>
        public const string AllocateDynamicUninit = "rf_allocate_dynamic_uninit";
        /// <summary>Entity invalidation / free.</summary>
        public const string Invalidate = "rf_invalidate";
        /// <summary>Trace: update current source location.</summary>
        public const string TraceUpdateLoc = "_rf_trace_update_loc";
        /// <summary>Agent/task: create.</summary>
        public const string TaskCreate = "rf_task_create";
        /// <summary>Agent/task: spawn on a dedicated thread.</summary>
        public const string TaskSpawnThreaded = "rf_task_spawn_threaded";
        /// <summary>Agent/task: complete with a value payload.</summary>
        public const string TaskCompleteValue = "rf_task_complete_value";
        /// <summary>Coroutine: create.</summary>
        public const string CoroCreate = "rf_coro_create";
        /// <summary>Coroutine: push a cancellation frame.</summary>
        public const string CoroCfPush = "rf_coro_cf_push";
        /// <summary>Coroutine: pop a cancellation frame.</summary>
        public const string CoroCfPop = "rf_coro_cf_pop";
        /// <summary>Scheduler: spawn onto the default scheduler.</summary>
        public const string SchedSpawnDefault = "rf_sched_spawn_default";
    }
}
